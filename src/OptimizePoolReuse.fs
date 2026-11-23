// Scratch reuse across barriers, rule pool-reuse (split out of Optimize.fs, cleanup K19).
module Blade.OptimizePoolReuse

open Blade.Types

open Blade.Ast
open Blade.IR
open Blade.IRMono
open Blade.OptimizeGates
open Blade.OptimizeAnalysis


/// SCRATCH REUSE ACROSS BARRIERS (plan-fortran-killer-2 section 4, gate 2).
///
/// Elementwise fusion removes the temporaries it can; the ones it cannot --
/// a whole-array dependency between two pointwise stages (`let y = ..; let m
/// = reduce(y, (+)); let z = y - m`) -- each allocate a fresh pool. In one
/// function body, once such a pool is DEAD (nothing after some point reads
/// it) and UNALIASED (no view, alias, capture-for-later, call argument or
/// assignment ever named it), a later fresh pool of the same element type and
/// the same literal extents can take it instead of allocating: the values
/// are untouched (a dead pool's bits are never read again), only the
/// allocation count and the peak live bytes change -- the optimizer's
/// charter. The plan is keyed by let id in `Types.PoolReuseTable`; codegen
/// renders the reuser's declaration as `{ donor.data, extents }`, skips its
/// scope free, and treats an escaping reuser as an escaping donor.
///
/// Admitted narrowly (each guard removes a way to be wrong, not a way to be
/// slow):
///  * both pools are `|> compute`d applies (`IRCompute (IRApplyCombinator)`)
///    of plain dense rank-1-slot storage with LITERAL extents and a scalar
///    element, and neither let is a `let mut` -- the one shape whose
///    declaration is one `arrayAlloc` line with a static extents table;
///  * the donor is never referenced by anything that could keep a handle on
///    its storage: every position that names it must be a fresh-pool form
///    (its cells are read, its storage is not retained) or scalar-typed; a
///    view, an alias let, a tuple, a CALL (a callee may hand back its
///    argument), an assignment, or a loop declines it;
///  * a DEFERRED let (a bare apply left for a join to consume) attributes
///    its reads to its consumers, and a callable's captures are reads the
///    callable performs when it runs, so liveness sees through both;
///  * the reuser does not read the donor (so the flat nest's restrict
///    pointers never alias), and no member of the donor's pool -- the root
///    and every earlier reuser of it -- is read at or after the reuser.
/// The RETURN position (`z * 0.5` as the body's value) is a reuser too: it
/// is the common tail of a pipeline and the largest single allocation saved.
/// Every function with two or more candidate pools leaves a decision (rule
/// `pool-reuse`): applied, with the pairs and the peak live bytes before and
/// after, or declined with the first blocker.
let private poolReuseElemBytes (t: IRType) : int64 option =
    match stripUnits t with
    | IRTScalar (ETFloat64 | ETInt64) -> Some 8L
    | IRTScalar (ETFloat32 | ETInt32) -> Some 4L
    | IRTScalar ETComplex128 -> Some 16L
    | IRTScalar ETComplex64 -> Some 8L
    | IRTScalar ETBool -> Some 1L
    | _ -> None

/// The reusable shape of a dense pool: (bare element type, literal extents).
let private densePoolShape (ty: IRType) : (IRType * int64 list) option =
    match ty with
    | ArrayElem at when not at.IndexTypes.IsEmpty
                        && at.IndexTypes |> List.forall (fun ix -> ix.IxKind = IxKPlain && ix.Symmetry = SymNone && ix.Rank = 1)
                        && (poolReuseElemBytes at.ElemType).IsSome ->
        let exts = at.IndexTypes |> List.map (fun ix -> match ix.Extent with IRLit (IRLitInt n) when n > 0L -> Some n | _ -> None)
        if exts |> List.forall Option.isSome then Some (stripUnits at.ElemType, exts |> List.map Option.get) else None
    | _ -> None

/// PM60: a plain dense pool shape with a run-time extent on some axis:
/// (bare element type, rank). The planner never keys such a pool by its
/// TYPE -- an index type `like I` is unified by name, not by run-time length
/// (two `like I` arguments, a call result typed I or a lambda's capture typed
/// I may each carry a different length; review PM60-1) -- but by the
/// PROVENANCE of its producer's operand (see planPoolReuse).
let private runtimeDenseShape (ty: IRType) : (IRType * int) option =
    match ty with
    | ArrayElem at when not at.IndexTypes.IsEmpty
                        && at.IndexTypes |> List.forall (fun ix -> ix.IxKind = IxKPlain && ix.Symmetry = SymNone && ix.Rank = 1 && ix.Dependencies.IsEmpty)
                        && (poolReuseElemBytes at.ElemType).IsSome
                        && at.IndexTypes |> List.exists (fun ix -> match ix.Extent with IRLit _ -> false | _ -> true)
                        && at.IndexTypes |> List.forall (fun ix -> match ix.Extent with IRLit (IRLitInt n) -> n > 0L | IRLit _ -> false | _ -> true) ->
        Some (stripUnits at.ElemType, at.IndexTypes.Length)
    | _ -> None

/// The shared fresh-pool classification (IR.isFreshPoolFormWith -- the same
/// definition codegen's escape analysis uses). A CALL is never fresh here: the
/// planner cannot see what a callee hands back.
let private isFreshForm (e: IRExpr) : bool = isFreshPoolFormWith (fun _ -> false) e

/// `mayRetain` (gap G221: Optimize.scheduleMayRetainIn over the program, its
/// scope-local and private-cell sets): a position that may RETAIN storage it
/// names never leaves a donor unaliased -- a retaining ROW KERNEL
/// (`method_for(y) <@> keep`) or a call through an unresolved head is no
/// ordinary IRApp of a declared callable, so `callsCallable` alone missed it.
let planPoolReuse (mayRetain: Set<IRId> -> Set<IRId> -> IRExpr -> bool) (modul: IRModule) : unit =
    let funcs = modul.Functions |> List.map (fun f -> (f.Id, f)) |> Map.ofList
    let rec unroll (e: IRExpr) : (IRId * IRExpr) list * IRExpr =
        match e with
        | IRLet (id, v, body) ->
            let (iv, vf) = unroll v
            let (rb, rf) = unroll body
            (match iv with
             | [] -> ((id, v) :: rb, rf)
             | _ -> (iv @ [ (id, vf) ] @ rb, rf))
        | _ -> ([], e)
    let rec collapse (e: IRExpr) = match e with IRCompute (IRCompute _ as i) -> collapse i | e -> e
    let materialized (v: IRExpr) =
        match collapse v with
        | IRCompute (IRApplyCombinator _) -> true
        | _ -> false
    let deferred (v: IRExpr) =
        match v with
        | IRApplyCombinator _ | IRComposeApply _ -> true
        | _ -> false
    let scalarValued (v: IRExpr) =
        match typeOf v with
        | IRTScalar _ | IRTUnit -> true
        | _ -> false
    let decide (subject: string) (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) =
        Blade.Effects.Decisions.record
            { Blade.Effects.Rule = "pool-reuse"; Version = 1
              Span = Blade.Ast.noSpan; Subject = subject
              Outcome = outcome; Evidence = evidence }
    for f in modul.Functions do
        // A01: reuse has no lifetime-region proof. Decline the whole function
        // rather than joining a callee pool to caller storage or extending an
        // internal donor beyond its original scalar-return boundary.
        let mutable lifetimeEvent = false
        iterIRExpr (function IRBeginPoolScope _ | IRReleasePools _ -> lifetimeEvent <- true | _ -> ()) f.Body
        if lifetimeEvent then
            decide f.SourceName (Blade.Effects.Declined "inline lifetime region: scratch reuse requires a region ownership proof") []
        else
        let (lets, ret) = unroll f.Body
        let letValues = Map.ofList lets
        // Reads a position performs, closed over deferred lets (their reads
        // happen at the consumer) and callable captures (the callable reads
        // them when it runs).
        let refsOf (e: IRExpr) : Set<IRId> =
            let mutable acc = Set.empty
            let work = System.Collections.Generic.Stack<IRId>()
            // G220: an expression a type carries (a range bound) reads too
            for x in e :: embeddedExtentExprs e do
                for r in collectVarRefsIR x do work.Push r
            while work.Count > 0 do
                let id = work.Pop()
                if not (Set.contains id acc) then
                    acc <- Set.add id acc
                    (match Map.tryFind id funcs with
                     | Some g -> for c in g.Captures do work.Push c.Id
                     | None -> ())
                    (match Map.tryFind id letValues with
                     | Some dv when deferred dv ->
                         for x in dv :: embeddedExtentExprs dv do
                             for r in collectVarRefsIR x do work.Push r
                     | _ -> ())
            acc
        let n = lets.Length
        let positions = (lets |> List.map (fun (id, v) -> (Some id, v))) @ [ (None, ret) ]
        let refsAt = positions |> List.map (fun (_, v) -> refsOf v) |> Array.ofList
        let valueAt = positions |> List.map snd |> Array.ofList
        // Every id an assignment names (its target, and every array its RHS
        // or an indexed target reads), anywhere in the body.
        let assignedAnywhere =
            let mutable s = Set.empty
            let note (root: IRExpr) =
             iterIRExpr (fun e ->
                match e with
                | IRAssign (t, rhs) ->
                    (match t with LVVar tid -> s <- Set.add tid s | _ -> ())
                    s <- Set.union s (collectVarRefsIR rhs)
                    (match t with LVVar _ -> () | _ -> s <- Set.union s (collectVarRefsIR t))
                | _ -> ()) root
            // G220: an assignment hidden in a type-carried expression counts
            for root in f.Body :: embeddedExtentExprs f.Body do note root
            s
        // PM60: an IMMUTABLE array -- a parameter of this function or a let of
        // its own body, never named by an assignment (an array's extents never
        // change, so its run-time extents are one value for the whole call).
        let paramIds = f.Params |> List.map (fun p -> p.VarId) |> Set.ofList
        let ownIds = Set.union paramIds (lets |> List.map fst |> Set.ofList)
        let immutable (id: IRId) = ownIds.Contains id && not (assignedAnywhere.Contains id)
        // `literal` restricts the ANALYSIS to the previous literal-only planner;
        // `0`/`off` still analyses (the disabled record names the pairs)
        let runtimeOn =
            match System.Environment.GetEnvironmentVariable "BLADE_POOL_REUSE" with
            | null -> true
            | v -> v.Trim().ToLowerInvariant() <> "literal"
        let unprovedRuntime = ResizeArray<string>()
        // PM60: the run-time extent KEY of a pool, by the PROVENANCE of the
        // producer's operands (the emitted allocation reads its extents from
        // them): a POINTWISE map (every operand cell-for-cell, kernel input
        // rank 0, the output of the operands' rank) whose operands are all
        // the same immutable ARRAY PARAMETER of this function, or all the same
        // earlier run-time candidate (its key, transitively). Anything else --
        // a call result, a capture, a provider / compound / grouped / sorted
        // array, an outer product, a range, a rebound let -- has no key.
        let runtimeKeys = System.Collections.Generic.Dictionary<IRId, string>()
        let provenanceKey (v: IRExpr) (rank: int) : Result<string, string> =
            match collapse v with
            | IRCompute (IRApplyCombinator info) | IRApplyCombinator info ->
                // one operand (no shared axes), or a co-iteration (zip) whose
                // shared axes are exactly the output's plain axes (review PM60-2)
                let pointwise =
                    info.KernelInputRanks |> List.forall (fun r -> r = 0)
                    && info.KernelTDims.IsEmpty
                    && not info.HasReynolds
                    && info.ArrayTypes |> List.forall (fun at -> at.IndexTypes.Length = rank)
                    && ((info.Arrays.Length = 1 && info.SharedIndexTypes.IsEmpty)
                        || (info.IsCoIteration && info.SharedIndexTypes.Length = rank
                            && info.SharedIndexTypes |> List.forall (fun ix -> ix.IxKind = IxKPlain && ix.Symmetry = SymNone && ix.Rank = 1 && ix.Dependencies.IsEmpty)))
                if not pointwise then Error "the producer is not a pointwise map of its operands (its extents are not an operand's)"
                else
                    let keyOf (op: IRExpr) =
                        match op with
                        | IRVar (id, _) when paramIds.Contains id && immutable id -> Some $"extents(param v{id})"
                        | IRVar (id, _) when runtimeKeys.ContainsKey id && immutable id -> Some runtimeKeys.[id]
                        | _ -> None
                    match info.Arrays |> List.map keyOf with
                    | [] -> Error "the producer has no operand"
                    | ks when ks |> List.forall Option.isSome && (ks |> List.map Option.get |> List.distinct).Length = 1 -> Ok ks.Head.Value
                    | ks when ks |> List.forall Option.isSome -> Error "the operands have different provenances"
                    | _ -> Error "an operand is not an immutable array parameter or an earlier keyed pool (a call result, a capture, a provider / compound / grouped array, a range or a rebound let proves no extent)"
            | _ -> Error "the value is not a map"
        // Candidate pools: (position, let id option, shape, bytes).
        let candidateAt (k: int) =
            let (idOpt, v) = positions.[k]
            // A body-level `let` is reassignable in its own scope, so the
            // MutableArrayLets side table names every array let; what matters
            // here is whether an ASSIGNMENT ever names the pool (assignedIds).
            let ok =
                (match idOpt with
                    | Some _ -> materialized v
                    | None -> (match collapse v with IRCompute (IRApplyCombinator _) | IRApplyCombinator _ -> true | _ -> false))
            if not ok then None
            else
                let ty = typeOf (collapse v)
                match densePoolShape ty with
                | Some (elem, exts) ->
                    let bytes = (poolReuseElemBytes elem |> Option.defaultValue 0L) * (exts |> List.fold (*) 1L)
                    Some (elem, exts |> List.map (fun n -> $"{n}"), bytes, false)
                | None when runtimeOn ->
                    match runtimeDenseShape ty with
                    | Some (elem, rank) ->
                        match provenanceKey v rank with
                        | Ok key ->
                            (match idOpt with Some id -> runtimeKeys.[id] <- key | None -> ())
                            // a PROVED run-time shape: no static byte count (not priced)
                            Some (elem, [ key ], 0L, true)
                        | Error why ->
                            (match idOpt with Some id -> unprovedRuntime.Add $"__v{id}: {why}" | None -> unprovedRuntime.Add $"the return: {why}")
                            None
                    | None -> None
                | None -> None
        let candidates = [ for k in 0 .. n -> (k, candidateAt k) ] |> List.choose (fun (k, c) -> c |> Option.map (fun c -> (k, c)))
        if candidates.Length >= 2 then
            // A donor's storage must never be retained by anyone: every
            // position naming it is a fresh-pool form or scalar-typed, and no
            // assignment anywhere names it.
            let assignedIds =
                let mutable s = Set.empty
                for (_, v0) in positions do
                  for v in v0 :: embeddedExtentExprs v0 do
                    iterIRExpr (fun e ->
                        match e with
                        | IRAssign (t, rhs) ->
                            (match t with LVVar tid -> s <- Set.add tid s | _ -> ())
                            s <- Set.union s (collectVarRefsIR rhs)
                            (match t with LVVar _ -> () | _ -> s <- Set.union s (collectVarRefsIR t))
                        | _ -> ()) v
                s
            // A call to a declared or lifted callable that names the donor may
            // retain it (hand it back, or alias it into a module-level mut),
            // whatever the call's own type; a fresh-pool form or a call-free
            // scalar only READS it.
            let scopeLocals = lets |> List.map fst |> Set.ofList
            let wholeRebound =
                let mutable s = Set.empty
                for root in f.Body :: embeddedExtentExprs f.Body do
                    iterIRExpr (fun e -> match e with IRAssign (LVVar tid, _) -> s <- Set.add tid s | _ -> ()) root
                s
            let scopePrivates =
                lets
                |> List.filter (fun (id, v) -> isFreshForm v && not (wholeRebound.Contains id))
                |> List.map fst |> Set.ofList
            // gap G221 (contents provenance; Codex diff review d71e628b round
            // 3): a fresh pool whose CELLS can hold storage (`[Box { a = y }]`)
            // keeps a reference to what it names -- it reads the donor into a
            // second holder, not transiently
            let plainContents (v: IRExpr) =
                let scalarish (t: IRType) =
                    match stripUnits t with
                    | IRTScalar _ | IRTIdxTagged _ | IRTUnit -> true
                    | _ -> false
                match typeOf v with
                | ArrayElem at -> scalarish at.ElemType
                | t -> scalarish t
            let callsCallable (v: IRExpr) =
                if mayRetain scopeLocals scopePrivates v then true
                else
                let mutable found = false
                // G220 (Codex diff review): a call inside a type-carried
                // expression (a range bound) may retain the donor too
                for root in v :: embeddedExtentExprs v do
                    iterIRExpr (fun e ->
                        match e with
                        | CallOfNamed (fid, _, _, _) when Map.containsKey fid funcs -> found <- true
                        | _ -> ()) root
                found
            let unaliased (k: int) (a: IRId) =
                not (Set.contains a assignedIds)
                && [ 0 .. n ] |> List.forall (fun p ->
                    p = k
                    || not (Set.contains a refsAt.[p])
                    || (match positions.[p] with
                        | (Some _, pv) when deferred pv -> true      // reads attributed to its consumers
                        | (_, pv) -> not (callsCallable pv) && ((isFreshForm pv && plainContents pv) || scalarValued pv)))
            // Greedy in program order. `pools`: root position -> members
            // (positions) sharing its storage; `owner`: position -> root.
            let pools = System.Collections.Generic.Dictionary<int, int list>()
            let owner = System.Collections.Generic.Dictionary<int, int>()
            let idAt (p: int) = fst positions.[p]
            let deadFrom (members: int list) (j: int) =
                // no member is read at or after position j
                [ j .. n ] |> List.forall (fun p ->
                    members |> List.forall (fun m ->
                        match idAt m with
                        | Some mid -> not (Set.contains mid refsAt.[p])
                        | None -> true))
            let pairs = ResizeArray<int * int>()
            let runtimeDeclines = ResizeArray<string>()
            // PM60 v1 (run-time shapes): the reuser is a body let (not the
            // return position, whose wrapper would leave with the reuser's own
            // run-time extents table), it is itself unaliased, and no member of
            // the shared pool, the reuser included, is read by the return
            // expression -- so neither storage nor its extents table escapes
            // the frame (the C++ frame frees the reuser's own extents table).
            let runtimeOk (k: int) (members: int list) =
                match idAt k with
                | None -> Error "the return position (its wrapper would leave with its own run-time extents table)"
                | Some rid when not (unaliased k rid) -> Error $"__v{rid} is aliased, captured, assigned or passed to a call"
                | Some _ ->
                    // a SCALAR return that calls no callable only reads the cells
                    let retEscapes (mid: IRId) = Set.contains mid refsAt.[n] && (callsCallable valueAt.[n] || not (scalarValued valueAt.[n]))
                    if (k :: members) |> List.exists (fun m -> match idAt m with Some mid -> retEscapes mid | None -> true) then
                        Error "a member of the shared pool is read by a non-scalar or calling return expression (it could escape the frame)"
                    else Ok ()
            for (k, (elem, exts, _, runtime)) in candidates do
                let donor =
                    candidates
                    |> List.filter (fun (r, (relem, rexts, _, _)) ->
                        r < k && relem = elem && rexts = exts
                        && pools.ContainsKey r
                        && (match idAt r with Some a -> unaliased r a | None -> false)
                        && deadFrom pools.[r] k
                        && (not runtime
                            || (match runtimeOk k pools.[r] with
                                | Ok () -> true
                                | Error why ->
                                    let who = match idAt k with Some id -> "__v" + string id | None -> "the return"
                                    let donorName = "__v" + string ((idAt r) |> Option.defaultValue -1)
                                    runtimeDeclines.Add $"{who} would take {donorName}'s dead pool: declined -- {why}"
                                    false)))
                    |> List.tryHead
                match donor with
                | Some (r, _) ->
                    pools.[r] <- pools.[r] @ [ k ]
                    owner.[k] <- r
                    pairs.Add((k, r))
                | None ->
                    pools.[k] <- [ k ]
                    owner.[k] <- k
            let nameOf (p: int) = match idAt p with Some id -> $"__v{id}" | None -> "the return"
            let bytesOf (p: int) = candidates |> List.tryPick (fun (q, (_, _, b, _)) -> if q = p then Some b else None) |> Option.defaultValue 0L
            let runtimeAt (p: int) = candidates |> List.exists (fun (q, (_, _, _, rt)) -> q = p && rt)
            let keysAt (p: int) = candidates |> List.tryPick (fun (q, (_, ks, _, _)) -> if q = p then Some (String.concat " x " ks) else None) |> Option.defaultValue "?"
            let pairText (verb: string) (k: int) (r: int) =
                if runtimeAt k then $"{nameOf k} {verb} {nameOf r}'s dead pool (run-time shape [{keysAt k}], proved equal; not priced)"
                else $"{nameOf k} {verb} {nameOf r}'s dead pool ({bytesOf k} B)"
            let runtimeNotes =
                (if runtimeDeclines.Count = 0 then [] else List.ofSeq runtimeDeclines)
                @ (if unprovedRuntime.Count = 0 then [] else unprovedRuntime |> Seq.map (fun u -> $"not a candidate: {u}") |> List.ofSeq)
                @ (if pairs |> Seq.exists (fun (k, _) -> runtimeAt k)
                   then [ "run-time-shaped pairs: realized by the C++ lane (the reuser's declaration aliases the donor's data, its own run-time extents table is still freed); the LLVM lane does not consume let-level scratch reuse (its pools stay fresh)" ]
                   else [])
            // Peak live pool bytes: each pool live from its first member's
            // definition to its members' last read.
            let lastRead (p: int) =
                match idAt p with
                | None -> n
                | Some id -> [ p .. n ] |> List.filter (fun q -> Set.contains id refsAt.[q]) |> List.fold max p
            let peakOf (groups: (int * int list) list) =
                let intervals = groups |> List.map (fun (root, members) -> (root, members |> List.map lastRead |> List.fold max root, bytesOf root))
                [ 0 .. n ] |> List.map (fun pos -> intervals |> List.sumBy (fun (lo, hi, b) -> if lo <= pos && pos <= hi then b else 0L)) |> List.fold max 0L
            let before = peakOf (candidates |> List.map (fun (k, _) -> (k, [ k ])))
            let after = peakOf (pools |> Seq.map (fun kv -> (kv.Key, kv.Value)) |> List.ofSeq)
            if pairs.Count > 0 && not (poolReuseEnabled ()) then
                // The escape hatch: the plan is computed (so the A/B names
                // what it would have done) and not one pair reaches codegen.
                decide f.SourceName (Blade.Effects.Declined "disabled by BLADE_POOL_REUSE")
                    ((pairs |> List.ofSeq |> List.map (fun (k, r) -> pairText "would take" k r)) @ runtimeNotes)
            elif pairs.Count > 0 then
                for (k, r) in pairs do
                    match idAt k with
                    | Some id -> Blade.Types.PoolReuseTable.record id (idAt r).Value
                    | None -> Blade.Types.PoolReuseTable.recordReturn f.Id (idAt r).Value
                decide f.SourceName Blade.Effects.Applied
                    ((pairs |> List.ofSeq |> List.map (fun (k, r) -> pairText "takes" k r))
                     @ [ $"pools {candidates.Length} -> {pools.Count}; peak live pool bytes {before} -> {after} (literal extents only)" ]
                     @ runtimeNotes)
            else
                decide f.SourceName (Blade.Effects.Declined "no candidate pool is both dead and unaliased when a pool of the same shape is allocated")
                    ([ $"{candidates.Length} candidate pools, peak live pool bytes {before}" ] @ runtimeNotes)
        elif unprovedRuntime.Count > 0 then
            decide f.SourceName (Blade.Effects.Declined "fewer than two candidate pools")
                (unprovedRuntime |> Seq.map (fun u -> $"not a candidate: {u}") |> List.ofSeq)
