// The hash equijoin, rule hash-join, and its side table (split out of Optimize.fs, cleanup K19).
module Blade.OptimizeEquiJoin

open Blade.Types

open Blade.Ast
open Blade.IR
open Blade.IRMono
open Blade.OptimizeGates
open Blade.OptimizeAnalysis
open Blade.OptimizeStorage

// --- Hash equijoin (round 3 W3-2, item T2; rule `hash-join v1`) -------------
//
// THE SHAPE. A top-level rank-2 compound written INLINE over two outer
// products,
//
//   let J = compound(method_for(a1, b1) <@> pk, mask(method_for(a0, b0) <@> lambda(l, r) -> l == r, lambda(e) -> e))
//
// with a0 / a1 over the LEFT nominal axis and b0 / b1 over the RIGHT one,
// keys a0 / b0 Int64 (plain, or a positional Idx<K> tag), is the equijoin of
// a0 and b0 with payload pk(a1[i], b1[j]). As written it runs SIX passes over
// all N x M pairs (the `==` Bool pool, the identity mask through a wrapper,
// the std::vector<bool> copy, compound_index_t<2>'s hashed enumeration, the
// payload pool, the branchless scatter) and keeps ~20 B per pair live.
//
// THE PHYSICAL FORM (C++ lane, CodeGenBinding.genEquiJoinBinding; the C core
// blade_join_* in blade_member.h): a CSR over the RIGHT keys b0 -- a counting
// sort when the key span is small (a positional key type always takes it), an
// open-addressing key -> bucket map otherwise -- whose bucket rows are filled
// FORWARD (stable: every bucket lists its right rows j ascending); a COUNTING
// pass over the left keys sizes J; then the probe walks i ascending and, for
// each, EVERY right row of its bucket ascending, storing pk(a1[i], b1[j]) at
// compact[q++]. Left-major / right-minor IS the compound's lex storage order
// (BladeLex), so J's cells and their order are the as-written ones: bags
// survive (duplicates on both sides kept), an out-of-domain or negative left
// key matches exactly the right rows equal to it (none, for a key no right row
// holds -- never BL8006: equality has no domain check). J stays STORED (its
// compact buffer) with a CARDINALITY-ONLY compound_index_t<2> (dense extents
// N x M, cardinality |J|; no tuple table, reverse hash or mask -- a lookup
// through the absent table is BL9002). The consumers -- `extents(J)`, folds
// over J, printing -- are untouched: they read the stored J as before.
//
// LEGALITY. The skipped work is `==` on every pair and pk on every
// non-matching pair: pure and total (the closed `totalScalarOver` whitelist),
// so skipping it has no effect. The dropped REPRESENTATIONS (the N x M payload
// and Bool pools, the identity-mask pool, its bit vector, the tuple table and
// reverse hash) are compiler temporaries of the inline spelling: the demand
// census proves no consumer reads anything but J's compact buffer and its
// cardinality. The NAMED R4 spelling (`equal_keys`, `keep`, `products` as
// let-bound arrays) keeps every named array stored and is declined by name.
//
// FIVE PARTIES (equiJoinAdmits, every write query through the shared
// `scheduleMayWriteIn programFuncs modul`): producers (the four sources,
// proved stored by `storedArrayProof`, never assigned, write-free payloads),
// the producer kernels (pk, `==`, the identity predicate: write-free, total,
// capture-free, no team / device request), the consumer (J's whole payload;
// every use of J census-certified, each staying at its own site), the outer
// fold kernels and seeds (named, never moved), the intervening bindings (none:
// the build, the counting pass and the probe all run at J's own site, reading
// the four stored sources there exactly where the six passes read them), and
// parallel bodies (none: every pass is serial).
//
// Lanes: C++ v1. The LLVM lane refuses every rank-2 compound already (the
// program falls back to C++) and records `declined -- llvm lane: the hash
// equijoin is not built in this lane (v1.1)`. The interpreter runs the
// as-written six-pass tree: the independent oracle of `blade test interp`.

/// How the right keys are bucketed (the plan's statement; the run-time
/// chooser in blade_member.h decides the actual form from (M, lo, hi)).
type EquiJoinKeyRegime =
    /// The right key's element type is a positional index type `Idx<K>`
    /// (static K <= 2^20): the counting sort over the keys' [lo, hi] is
    /// forced (span <= K for every well-typed cell; a cell outside [0, K)
    /// still buckets correctly -- the span is measured, never assumed).
    | JoinPositional of k: int64
    /// Plain Int64 keys: the counting sort when the span is within
    /// max(4 M, 4096) cells (and 2^26), else the open-addressing hash.
    | JoinDynamic

type EquiJoinPlan = {
    CompoundId: IRId
    CompoundName: string
    /// The four stored sources as IR references: keys (a0, b0) and payloads
    /// (a1, b1); "left" is the mask's FIRST outer-product operand (the major
    /// axis of J's lex order), "right" its second.
    LeftKey: IRExpr
    RightKey: IRExpr
    LeftPay: IRExpr
    RightPay: IRExpr
    LeftKeyName: string
    RightKeyName: string
    /// pk, the payload kernel (a callable reference), applied as pk(a1[i], b1[j]).
    PayloadKernel: IRExpr
    KeyRegime: EquiJoinKeyRegime
    /// A CONJUNCTIVE key kernel (round 4 wave 4 D3, ST15: `l == r && q(l, r)`,
    /// any conjunct order, every residual conjunct a pure total scalar of the
    /// two key cells): the key kernel itself, re-evaluated on each MATCHED pair
    /// (where its equality conjunct holds) in the counting pass and the probe;
    /// None = the plain `l == r`.
    Residual: IRExpr option
    /// The C++ emission placed it (set by genEquiJoinBinding).
    EmittedCpp: bool ref
}

/// AsyncLocal like CompactionTable; reset once per program in
/// `lowerTypedProgram`, because let ids restart per compile.
module EquiJoinTable =
    open System.Threading
    let private cell = new AsyncLocal<Map<IRId, EquiJoinPlan>>()
    let private get () = match box cell.Value with null -> Map.empty | _ -> cell.Value
    let reset () = cell.Value <- Map.empty
    let record (p: EquiJoinPlan) = cell.Value <- Map.add p.CompoundId p (get ())
    let tryCompound (id: IRId) : EquiJoinPlan option = Map.tryFind id (get ())
    let all () : EquiJoinPlan list = get () |> Map.toList |> List.map snd

/// The C core's form chooser (`blade_join_choose_i64`, blade_member.h)
/// restated for the plan record of a LITERAL right key: `true` = the counting
/// sort (DIRECT), `false` = the hash. Keep the two in step (the cli block's C
/// core driver compares them case by case).
let joinChooseDirect (m: int64) (lo: int64) (hi: int64) (positional: bool) : bool =
    if m <= 0L then true
    else
    let span = uint64 hi - uint64 lo
    if positional && span < (1UL <<< 20) then true
    elif span >= (1UL <<< 26) then false
    else
        let limit = if uint64 m * 4UL > 4096UL then uint64 m * 4UL else 4096UL
        span < limit

/// Plan the hash equijoin over the module's top-level rank-2 compounds
/// (`compound` is top-level only, BL6002). Records into `EquiJoinTable` and
/// the decision collector (`hash-join`); the IR is untouched. Every rank >= 2
/// compound gets one record (applied, or the first failed condition).
let planEquiJoin (programFuncs: IRCallable list) (foreignRefs: Set<IRId>) (modul: IRModule) : unit =
    let funcs = modul.Functions |> List.map (fun f -> (f.Id, f)) |> Map.ofList
    let facts = cseFactsOf funcs
    let writes = scheduleMayWriteIn programFuncs modul
    let wrote (e: IRExpr) = (facts e).MayWrite || writes e
    let enabled = hashJoinEnabled ()
    let decide (subject: string) (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) =
        Blade.Effects.Decisions.record
            { Blade.Effects.Rule = "hash-join"; Version = 1
              Span = declSpanOf subject; Subject = subject
              Outcome = outcome; Evidence = evidence }
    let arr = List.toArray modul.Bindings
    let indexOf (id: IRId) = arr |> Array.tryFindIndex (fun b -> b.Id = id)
    let refs (e: IRExpr) = collectVarRefsIR e
    let assignsTo (id: IRId) (e: IRExpr) =
        let mutable hit = false
        iterIRExpr (fun n ->
            match n with
            | IRAssign (target, _) when Set.contains id (refs target) -> hit <- true
            | _ -> ()) e
        hit
    let assignedAnywhere (id: IRId) =
        (arr |> Array.exists (fun b -> bindingPayload modul b |> List.exists (assignsTo id)))
        || (modul.Functions |> List.exists (fun f -> assignsTo id f.Body))
    let payloadWrites (b: IRBinding) = bindingPayload modul b |> List.exists wrote
    let isParamRef (p: IRParam) (e: IRExpr) =
        match e with
        | IRVar (v, _) -> v = p.VarId
        | IRParam (_, i, _) -> i = p.Index
        | _ -> false
    let nodeName (e: IRExpr) = (Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(e, typeof<IRExpr>) |> fst).Name
    // An outer product over exactly two plain operands, possibly behind the
    // `|> compute` the user wrote or the trivial `let t = apply; t` a lift
    // may wrap it in. Anything else (a lifted statement ahead of the apply,
    // a zip, a reynolds kernel, a rank-raising kernel) is not the shape.
    let rec peelApply (e: IRExpr) : ApplyInfo option =
        match e with
        | IRCompute inner -> peelApply inner
        | IRLet (t, v, IRVar (t', _)) when t = t' -> peelApply v
        | IRApplyCombinator info -> Some info
        | _ -> None
    let outerOperands (info: ApplyInfo) : Result<IRExpr * IRExpr, string> =
        if info.IsCoIteration then Error "a co-iteration (`zip`), not an outer product"
        elif info.HasReynolds then Error "a reynolds kernel"
        elif info.KernelOutputRank <> 0 then Error "a kernel with an array-valued cell"
        else
        match info.Arrays with
        | [ (IRVar _ as l); (IRVar _ as r) ] -> Ok (l, r)
        | [ _; _ ] -> Error "an outer product whose operands are not named stored arrays"
        | xs -> Error $"an outer product over {xs.Length} operand(s) (v1 joins exactly two axes)"
    let nameOfVar (e: IRExpr) =
        match e with
        | IRVar (id, _) -> (match indexOf id with Some i -> arr.[i].Name | None -> $"v{id}")
        | _ -> "?"
    let axisOf (e: IRExpr) : IRIndexType option =
        match e with
        | IRVar (id, _) ->
            match indexOf id with
            | Some i ->
                (match stripUnits arr.[i].Type with
                 | ArrayElem a when not a.IsVirtual ->
                    (match a.IndexTypes with
                     | [ ix ] when ix.IxKind = IxKPlain && ix.Rank = 1 && ix.Symmetry = SymNone -> Some ix
                     | _ -> None)
                 | _ -> None)
            | None -> None
        | _ -> None
    // Index IDENTITY, never extent: the same named index type (its tag) or
    // the same index record.
    let sameAxis (a: IRIndexType) (b: IRIndexType) =
        (a.Tag.IsSome && a.Tag = b.Tag) || a.Id = b.Id
    let elemOfVar (e: IRExpr) : IRType option =
        match e with
        | IRVar (id, _) ->
            (match indexOf id with
             | Some i -> (match stripUnits arr.[i].Type with ArrayElem a -> Some (stripUnits a.ElemType) | _ -> None)
             | None -> None)
        | _ -> None
    // A positional index tag's static extent, through aliases; None for an
    // EnumIdx (its cells hold positions in the enumeration) or an unknown tag.
    let rec positionalExtent (fuel: int) (tag: string) : Result<int64, string> =
        if fuel <= 0 then Error $"the index tag `{tag}` does not resolve"
        else
        match modul.Types |> List.tryPick (fun td ->
                match td with
                | IRTDIndexType (n, ix) when n = tag -> Some (Choice1Of3 ix)
                | IRTDEnumIdx (n, _, _) when n = tag -> Some (Choice2Of3 ())
                | IRTDAlias (n, IRTIdxTagged (_, IRefNamed t2)) when n = tag && t2 <> tag -> Some (Choice3Of3 t2)
                | _ -> None) with
        | Some (Choice1Of3 ix) ->
            (match ix.Extent with
             | IRLit (IRLitInt k) when k > 0L && ix.Rank = 1 && ix.IxKind = IxKPlain -> Ok k
             | _ -> Error $"the positional key type `{tag}` has no static extent")
        | Some (Choice2Of3 ()) -> Error $"an EnumIdx key `{tag}` (its cells hold positions in the enumeration; v1.1)"
        | Some (Choice3Of3 t2) -> positionalExtent (fuel - 1) t2
        | None -> Error $"the index tag `{tag}` does not resolve to a declared index type"
    // The key's class: Ok None = plain Int64, Ok (Some K) = positional Idx<K>.
    let keyClass (e: IRExpr) : Result<int64 option, string> =
        match elemOfVar e with
        | Some (IRTScalar ETInt64) -> Ok None
        | Some (IRTIdxTagged (IRTScalar ETInt64, IRefNamed tag)) -> positionalExtent 16 tag |> Result.map Some
        | Some (IRTIdxTagged (IRTScalar ETInt64, IRefAnon (_, IRLit (IRLitInt k)))) when k > 0L -> Ok (Some k)
        | Some (IRTScalar (ETFloat64 | ETFloat32)) -> Error $"float keys (`{nameOfVar e}`): v1 joins exact integer keys only"
        | Some (IRTScalar ETInt32) -> Error $"Int32 keys (`{nameOfVar e}`): v1 joins Int64 keys"
        | Some (IRTScalar ETBool) -> Error $"Bool keys (`{nameOfVar e}`): v1 joins Int64 keys"
        | Some (IRTScalar ETString) -> Error $"String keys (`{nameOfVar e}`): v1 joins Int64 keys"
        | Some _ -> Error $"the key `{nameOfVar e}` is not an Int64 (plain or positional) element (v1 joins Int64 keys)"
        | None -> Error $"the key `{nameOfVar e}` is not a top-level array binding"
    let storedProof = storedArrayProof modul
    // ---- THE DEMAND CENSUS on J -------------------------------------------
    // Fail-safe: `Ok` only when EVERY occurrence of J anywhere in the program
    // (every module binding and side table, every function body, signature and
    // capture list, this module's and the program's, another module's
    // reference) is `extents(J)` or `reduce(J, k[, init])` with k a resolved,
    // capture-free, pure total two-parameter kernel -- the forms that read J's
    // compact buffer and cardinality alone. Printing J (a top-level binding's
    // print pass, not an IR use) reads `J.size()` cells of `J.data` in the C++
    // lane, the cardinality and the buffer only, and is certified with them.
    // Any other occurrence -- a keyed read, a partial index, a `present` /
    // `<|:>` query, an argument, an alias, a tuple, a capture, a map over J, a
    // provider write, a type extent other than `extents(J)` -- needs the
    // tuple table (and the mask), and is `Error <what demands it>`.
    let allCallables = (modul.Functions @ programFuncs) |> List.distinctBy (fun f -> f.Id)
    let joinDemand (jId: IRId) (jName: string) : Result<string list * (string * IRExpr * IRExpr option) list, string> =
        let uses = ResizeArray<string>()
        let folds = ResizeArray<string * IRExpr * IRExpr option>()
        let note (u: string) = if not (uses.Contains u) then uses.Add u
        let mutable demand : string option = None
        let req (why: string) = if demand.IsNone then demand <- Some why
        let isJ (e: IRExpr) = match e with IRVar (v, _) -> v = jId | _ -> false
        // G128 / ST15: a MASK over J (`let keep = mask(J, p)`, or inline in
        // `compound(J, mask(J, p))`) reads J's compact buffer and its
        // cardinality only -- its bits are `Compound<bool, R>` over J's
        // present cells SHARING J's index -- and `compound(J, keep)` reads J's
        // buffer by POSITION and builds its own rank-1 index from the bits.
        // Each named mask is then censused itself: its uses must be only the
        // mask operand of such a compound, `extents`, a fold, printing, or
        // the compound index type of that compound (its own index); anything
        // else (a keyed read through the shared index) demands J's table.
        let selectionOn = joinSelectionEnabled ()
        let derivedMasks =
            arr |> Array.choose (fun b ->
                if not selectionOn then None else
                match b.Value with
                | IRMask (IRVar (s, _), _) when s = jId -> Some (b.Id, b.Name)
                | _ -> None)
            |> Map.ofArray
        let isMaskOfJ (e: IRExpr) = match e with IRVar (v, _) -> derivedMasks.ContainsKey v | _ -> false
        // compound(J, keep) / compound(J, mask(J, p)) side operands
        let compoundOverJ (b: IRBinding) =
            match Map.tryFind b.Id modul.CompoundInits with
            | Some (IRVar (d, _), m) when d = jId ->
                (match m with
                 | IRVar (k, _) when derivedMasks.ContainsKey k -> Some m
                 | IRMask (IRVar (s, _), _) when s = jId -> Some m
                 | _ -> None)
            | _ -> None
        let foldKernelOk (k: IRExpr) =
            match resolveCallableIn funcs (fst (peelReynolds k)) with
            | Some kc when kc.Captures.IsEmpty && kc.Params.Length = 2 && not kc.IsOmpParallel && not kc.IsCudaKernel && not kc.IsMpiParallel ->
                totalScalarOver kc.Params kc.Body
            | _ -> false
        let rec scan (host: string) (where: string) (e: IRExpr) =
            match e with
            | IRExtent (a, _) when isJ a -> note "`extents` (the cardinality)"
            | IRReduce (a, k, init) when isJ a ->
                if foldKernelOk k then
                    note "folds over the compact buffer (pure total kernels)"
                    folds.Add((host, k, init))
                else req $"a fold over `{jName}` in {where} whose kernel is not a resolved, capture-free, pure total scalar function without a team / device request (v1)"
                scan host where k
                init |> Option.iter (scan host where)
            | IRExtent (a, _) when isMaskOfJ a -> note "`extents` of a mask over J (the cardinality)"
            | IRReduce (a, k, init) when isMaskOfJ a ->
                note "a fold over a mask over J (its bit buffer)"
                scan host where k
                init |> Option.iter (scan host where)
            | IRCompoundMask m when isMaskOfJ m ->
                note "the compound index type of `compound(J, keep)` (its own index, built from the bits at its site)"
            | IRCompoundMask (IRMask (IRVar (s, _), p)) when selectionOn && s = jId ->
                // the inline selection's own compound index type
                note "the compound index type of `compound(J, mask(J, p))` (its own index, built from the bits at its site)"
                scan host where p
            | IRVar (v, _) when derivedMasks.ContainsKey v ->
                req $"a use of the mask `{derivedMasks.[v]}` over `{jName}` the census does not certify (it shares J's index), in {where}"
            | IRIndex (a, _, _) when isJ a -> req $"a keyed read of `{jName}` in {where}"
            | IRApp (_, args, _) when List.exists isJ args -> req $"`{jName}` passed as a call argument in {where}"
            | IRTuple es when List.exists isJ es -> req $"`{jName}` stored in a tuple in {where}"
            | IRAssign (t, _) when Set.contains jId (refs t) -> req $"an assignment to `{jName}` in {where}"
            | IRLet (_, v, _) when isJ v -> req $"an alias of `{jName}` in {where}"
            | IRVar (v, _) when v = jId -> req $"a use of `{jName}` the census does not certify, in {where}"
            | _ ->
                match childrenOf e |> List.tryFind (fun c -> isJ c || isMaskOfJ c) with
                | Some c when isJ c -> req $"`{jName}` as an operand of {nodeName e} (a use the census does not certify) in {where}"
                | Some c -> req $"the mask `{nameOfVar c}` over `{jName}` as an operand of {nodeName e} (a use the census does not certify) in {where}"
                | None -> childrenOf e |> List.iter (scan host where)
        let scanType (where: string) (t: IRType) =
            for x in (try irTypeExtentExprs t with _ -> [ IRVar (jId, IRTUnit) ]) do scan "" $"the type of {where}" x
        let scanEmbedded (where: string) (e: IRExpr) =
            for x in (try embeddedExtentExprs e with _ -> [ IRVar (jId, IRTUnit) ]) do scan "" $"a type inside {where}" x
        for root in compactionRoots allCallables modul do
            match root with
            | RootBindingValue b when b.Id = jId -> ()
            | RootBindingValue b ->
                let where = $"`{b.Name}`"
                (match b.Value with
                 | IRVar (v, _) when v = jId -> req $"the alias `{b.Name}` of `{jName}`"
                 | IRMask (IRVar (s, _), p) when s = jId && derivedMasks.ContainsKey b.Id ->
                     note "a mask over the compact buffer (J's present cells in lex order; its bits share J's index)"
                     scan b.Name where p
                 | _ -> scan b.Name where b.Value)
                scanEmbedded where b.Value
            | RootSideOperand (b, _, _) when b.Id = jId -> ()
            | RootSideOperand (b, what, e) when selectionOn && (compoundOverJ b).IsSome ->
                // compound(J, keep): J by position, the bits by name -- or the
                // inline mask, whose predicate is scanned like any kernel
                let where = $"{what} of `{b.Name}`"
                (match e with
                 | _ when isJ e -> note "`compound(J, mask(J, p))` reads J's compact buffer by position (its own rank-1 index from the bits)"
                 | IRVar (k, _) when derivedMasks.ContainsKey k -> ()
                 | IRMask (IRVar (s, _), p) when s = jId ->
                     note "a mask over the compact buffer (J's present cells in lex order; its bits share J's index)"
                     scan b.Name where p
                 | _ -> scan b.Name where e)
                scanEmbedded where e
            | RootSideOperand (b, what, e) ->
                let where = $"{what} of `{b.Name}`"
                scan b.Name where e
                scanEmbedded where e
            | RootFunctionBody fn ->
                let where = $"`{fn.SourceName}`"
                (match fn.Captures |> List.tryFind (fun c -> c.Id = jId) with
                 | Some _ -> req $"`{jName}` captured by {where}"
                 | None -> ())
                scan fn.SourceName where fn.Body
                scanEmbedded where fn.Body
            | RootType (where, ty) -> scanType where ty
            | RootSourceRef (where, id) ->
                if id = jId then req $"{where} of `{jName}`"
                elif derivedMasks.ContainsKey id then req $"{where} of the mask `{derivedMasks.[id]}` over `{jName}`"
            | RootNameRef (where, nm) ->
                if nm = jName then req $"{where} `{jName}`"
                elif derivedMasks |> Map.exists (fun _ n -> n = nm) then req $"{where} `{nm}` (a mask over `{jName}`)"
        if foreignRefs.Contains jId then req $"`{jName}` named in another module"
        // a mask over J shares J's index: another module naming it could read
        // through that index (Codex review round 1)
        for KeyValue (k, kName) in derivedMasks do
            if foreignRefs.Contains k then req $"the mask `{kName}` over `{jName}` named in another module (it shares J's index)"
        match demand with
        | Some why -> Error why
        | None ->
            note "printing (the C++ print pass reads `size()` cells of the compact buffer)"
            Ok (List.ofSeq uses, List.ofSeq folds)
    let admit (ji: int) (dense: IRExpr) (mask: IRExpr) : Result<EquiJoinPlan * string list, string> =
        let jb = arr.[ji]
        let jName = jb.Name
        let (|Fn|_|) (e: IRExpr) = resolveCallableIn funcs (fst (peelReynolds e))
        let requests (c: IRCallable) = c.IsOmpParallel || c.IsCudaKernel || c.IsMpiParallel
        // ---- the shape ----
        let denseShape =
            match dense with
            | IRVar _ -> Error $"the dense operand is the named array `{nameOfVar dense}`: v1 takes the INLINE spelling only -- a named operand (the R4 spelling of a join, or any stored rank-2 array) stays stored (bench protocol), so no N x M representation can disappear"
            | _ ->
            match peelApply dense with
            | None -> Error $"the dense operand is not an outer product written in place (it is {nodeName dense})"
            | Some info -> outerOperands info |> Result.map (fun ops -> (info, ops)) |> Result.mapError (fun why -> $"the dense operand is {why}")
        let maskShape =
            match mask with
            | IRVar _ -> Error $"the mask is the named array `{nameOfVar mask}`: v1 takes the INLINE spelling only -- a named operand (the R4 spelling of a join, or any stored rank-2 mask) stays stored (bench protocol), so no N x M representation can disappear"
            | IRMask (src, pred) ->
                (match peelApply src with
                 | None -> Error $"the mask's source is not an outer product written in place (it is {nodeName src})"
                 | Some info -> outerOperands info |> Result.map (fun ops -> (info, ops, pred)) |> Result.mapError (fun why -> $"the mask's source is {why}"))
            | other -> Error $"the mask is not an inline `mask(..)` (it is {nodeName other})"
        match denseShape, maskShape with
        | Error why, _ | _, Error why -> Error why
        | Ok (dInfo, (a1, b1)), Ok (mInfo, (a0, b0), pred) ->
        // ---- the kernels: identity predicate, `l == r`, pk ----
        let predWhy =
            match pred with
            | Fn pc when wrote pc.Body -> Some "the mask predicate may write (the shared write analysis): the as-written program evaluates it on every pair"
            | Fn pc ->
                (match pc.Params, pc.Captures with
                 | [ p ], [] when isParamRef p pc.Body -> None
                 | [ _ ], [] -> Some "a non-identity mask predicate (v1 takes `lambda(e) -> e` over the key-equality pool)"
                 | _, _ :: _ -> Some "the mask predicate captures a value"
                 | _ -> Some "the mask predicate is not a one-parameter function")
            | _ -> Some "the mask predicate does not resolve to a callable"
        let eqWhy =
            match mInfo.Kernel with
            | Fn ec when wrote ec.Body -> Some "the key kernel may write (the shared write analysis): the as-written program evaluates it on every pair"
            | Fn ec ->
                (match ec.Params, ec.Captures with
                 | _, _ :: _ -> Some "the key kernel captures a value"
                 | [ p0; p1 ], [] ->
                    let isKeyEq (e: IRExpr) =
                        match e with
                        | IRBinOp (_, IREq, x, y, _) -> (isParamRef p0 x && isParamRef p1 y) || (isParamRef p1 x && isParamRef p0 y)
                        | _ -> false
                    let rec conjuncts (e: IRExpr) =
                        match e with
                        | IRBinOp (_, IRAnd, a, b, _) -> conjuncts a @ conjuncts b
                        | _ -> [ e ]
                    (match ec.Body with
                     | b when isKeyEq b -> None
                     | IRBinOp (_, IRAnd, _, _, _) as b ->
                        // round 4 wave 4 D3 (ST15): `l == r` with RESIDUAL conjuncts.
                        // The as-written mask pass evaluates the whole kernel, left
                        // to right with short circuit, on every pair; the probe
                        // evaluates it on the MATCHED pairs only, so every residual
                        // conjunct must be a pure total scalar of the two key cells
                        // (skipping it on a non-matching pair has no effect).
                        let cs = conjuncts b
                        if not (cs |> List.exists isKeyEq) then Some "the key kernel is not the equality `l == r` of its two parameters (no conjunct is that equality)"
                        elif not (totalScalarOver ec.Params b) then Some "a residual conjunct of the key kernel is not a pure, total scalar of the two key cells (the totalScalar whitelist): the as-written mask pass evaluates it on pairs the probe skips"
                        else None
                     | _ -> Some "the key kernel is not the equality `l == r` of its two parameters")
                 | _ -> Some "the key kernel is not a two-parameter function")
            | _ -> Some "the key kernel does not resolve to a callable"
        let pkRes =
            match dInfo.Kernel with
            | Fn pk when wrote pk.Body ->
                Error "the payload kernel pk may write (the shared write analysis, Optimize.scheduleMayWriteIn): the as-written program evaluates it on EVERY pair, so skipping the non-matching ones would drop writes -- the six passes run as written"
            | Fn pk ->
                (match pk.Params, pk.Captures with
                 | _, _ :: _ -> Error "the payload kernel captures a value (v1: a pure total scalar of its two cells)"
                 | [ _; _ ], [] when not (totalScalarOver pk.Params pk.Body) ->
                    Error "the payload kernel is not a pure, total scalar of its two cells (the totalScalar whitelist: no call, no integer `/` `%` `^`, no cast)"
                 | [ _; _ ], [] -> Ok pk
                 | _ -> Error "the payload kernel is not a two-parameter function")
            | _ -> Error "the payload kernel does not resolve to a callable"
        match predWhy, eqWhy, pkRes with
        | Some why, _, _ | None, Some why, _ -> Error why
        | None, None, Error why -> Error why
        | None, None, Ok pk ->
        let ec = match mInfo.Kernel with Fn c -> c | _ -> pk
        let pc = match pred with Fn c -> c | _ -> pk
        // ---- the axes: index identity, not extent ----
        let axisWhy =
            match axisOf a0, axisOf a1, axisOf b0, axisOf b1 with
            | Some l0, Some l1, Some r0, Some r1 ->
                if not (sameAxis l0 l1) then Some $"the left key `{nameOfVar a0}` and the left payload `{nameOfVar a1}` do not share one nominal axis (index identity, not extent)"
                elif not (sameAxis r0 r1) then Some $"the right key `{nameOfVar b0}` and the right payload `{nameOfVar b1}` do not share one nominal axis (index identity, not extent)"
                else None
            | _ -> Some "a source is not a stored rank-1 array over one plain axis"
        match axisWhy with
        | Some why -> Error why
        | None ->
        match keyClass a0, keyClass b0 with
        | Error why, _ | _, Error why -> Error why
        | Ok _, Ok rightClass ->
        // ---- the sources: bound before J, proved stored, never assigned ----
        let sources = [ a0; b0; a1; b1 ] |> List.distinct
        let sourceWhy =
            sources |> List.tryPick (fun s ->
                match s with
                | IRVar (sid, _) ->
                    (match indexOf sid with
                     | None -> Some $"the source `{nameOfVar s}` is not a top-level binding the write analysis can see"
                     | Some si when si >= ji -> Some $"the source `{arr.[si].Name}` is bound after `{jName}`"
                     | Some si ->
                        match storedProof arr.[si] with
                        | Error why -> Some $"the source `{arr.[si].Name}` is not proved stored: {why}"
                        | Ok (_, chain) ->
                            match chain |> List.tryFind (fun c -> assignedAnywhere c.Id) with
                            | Some c -> Some $"the source `{c.Name}` (an alias chain of `{arr.[si].Name}`) is assigned in the program"
                            | None -> None)
                | _ -> Some "a source is not a named binding")
        match sourceWhy with
        | Some why -> Error why
        | None ->
        if assignedAnywhere jb.Id then Error $"`{jName}` is assigned in the program" else
        // ---- equiJoinAdmits: the five parties ----
        let chains =
            sources |> List.collect (fun s ->
                match s with
                | IRVar (sid, _) ->
                    (match indexOf sid with
                     | Some i -> (match storedProof arr.[i] with Ok (_, ch) -> ch | Error _ -> [ arr.[i] ])
                     | None -> [])
                | _ -> [])
            |> List.distinctBy (fun b -> b.Id)
        let srcNames = sources |> List.map (fun s -> $"`{nameOfVar s}`") |> String.concat ", "
        let producerParty =
            match chains |> List.tryFind payloadWrites with
            | Some b -> Error $"the producer `{b.Name}` may write (its initializer or side-table payload, through the shared write analysis)"
            | None -> Ok $"producers {srcNames}: proved stored at their own sites (storedArrayProof, through their alias chains), never assigned, no write in any payload"
        let kernelParty =
            let ks = [ ("the key kernel `==`", ec); ("the identity predicate", pc); ("the payload kernel pk", pk) ]
            match ks |> List.tryPick (fun (what, c) -> if wrote c.Body then Some $"{what} may write" elif requests c then Some $"{what} requests a thread team or a device" else None) with
            | Some why -> Error why
            | None -> Ok "producer kernels (`==`, the identity predicate, pk): pure, total scalars (the totalScalar whitelist), capture-free, no write, no team or device request -- the skipped evaluations (`==` on every pair, pk on every non-matching pair) have no effect"
        let consumerParty =
            if payloadWrites jb then Error $"`{jName}`'s payload (its value or its compound operands) may write"
            else Ok $"consumer `{jName}`: its whole payload (value and both compound operands) writes nothing; its stored compact buffer is what every consumer reads, each at its own site"
        let parties0 = [ producerParty; kernelParty; consumerParty ]
        match parties0 |> List.tryPick (function Error s -> Some s | Ok _ -> None) with
        | Some why -> Error why
        | None ->
        match joinDemand jb.Id jName with
        | Error why -> Error $"{why}: it needs the tuple table and the N x M mask, which only the as-written hashed index keeps (v1.1)"
        | Ok (uses, folds) ->
        let foldParty =
            let writing = folds |> List.tryFind (fun (_, k, _) -> match resolveCallableIn funcs (fst (peelReynolds k)) with Some kc -> wrote kc.Body | None -> true)
            match folds, writing with
            | [], _ -> "outer fold kernel(s): none over the relation"
            | _, Some (h, _, _) -> $"outer fold kernel(s): {folds.Length} over `{jName}`; `{h}`'s may write -- never moved (it reads the stored relation at its own site)"
            | _, None ->
                let hosts = folds |> List.map (fun (h, _, _) -> $"`{h}`") |> List.distinct |> String.concat ", "
                $"outer fold kernel(s): {folds.Length} over `{jName}` ({hosts}), none writes, none moved -- each reads the stored relation at its own site, in lex order"
        let seedParty =
            let seeded = folds |> List.choose (fun (h, _, s) -> s |> Option.map (fun e -> (h, e)))
            match seeded |> List.tryFind (fun (_, e) -> wrote e) with
            | Some (h, _) -> $"seed(s): `{h}`'s seed may write -- never moved (seeded folds stay at their own sites)"
            | None -> $"seed(s): {seeded.Length} seeded fold(s), none writes, none moved; an unseeded fold keeps its BL8003 empty check at its own site"
        let betweenParty = $"intervening: none -- the CSR build, the counting pass and the probe all run at `{jName}`'s own site and read only the four stored sources there, exactly where the six as-written passes read them"
        let parallelParty = "parallel: none -- every pass is serial; a kernel requesting a team or a device declines above, so no parallel body runs moved work and callbackTable has no candidate set to audit"
        let matrix =
            "equiJoinAdmits (five parties, each write query through Optimize.scheduleMayWriteIn): "
            + String.concat "; " ([ for p in parties0 do match p with Ok s -> yield s | Error s -> yield "DECLINES: " + s ] @ [ foldParty; seedParty; betweenParty; parallelParty ])
        let regime, regimeLine =
            match rightClass with
            | Some k when k <= (1L <<< 20) ->
                JoinPositional k, $"right keys `{nameOfVar b0}` positional (`Idx<{k}>`): a COUNTING SORT over their measured span [lo, hi] (inside [0, {k}) for every well-typed cell; span <= 2^20)"
            | _ ->
                JoinDynamic, $"right keys `{nameOfVar b0}` dynamic Int64: a COUNTING SORT when their span is within max(4 M, 4096) cells (and 2^26), else an open-addressing key -> bucket hash (blade_member.h, chosen at run time from (M, lo, hi) only)"
        // A LITERAL right key (through its alias chain): the form the build
        // will take, by the C core's own rule restated (joinChooseDirect).
        let formLine =
            // (wrapping Int64 arithmetic, as membership hoisting's literal
            // reader: `-9223372036854775807 - 1` is Int64's minimum)
            let rec lit (e: IRExpr) =
                match e with
                | IRLit (IRLitInt n) -> Some n
                | IRUnaryOp (IRNeg, x) -> lit x |> Option.map (fun n -> -n)
                | IRBinOp (_, IRAdd, l, r, _) -> Option.map2 (+) (lit l) (lit r)
                | IRBinOp (_, IRSub, l, r, _) -> Option.map2 (-) (lit l) (lit r)
                | IRBinOp (_, IRMul, l, r, _) -> Option.map2 (*) (lit l) (lit r)
                | _ -> None
            let literalKeys =
                match b0 with
                | IRVar (sid, _) ->
                    (match indexOf sid with
                     | Some si ->
                        (match storedProof arr.[si] with
                         | Ok (_, chain) ->
                            (match (List.last chain).Value with
                             | IRArrayLit (elems, _) ->
                                let ks = elems |> List.map lit
                                if ks |> List.forall Option.isSome then Some (ks |> List.map Option.get) else None
                             | _ -> None)
                         | Error _ -> None)
                     | None -> None)
                | _ -> None
            match literalKeys with
            | Some ks ->
                let m = int64 ks.Length
                let positional = match regime with JoinPositional _ -> true | JoinDynamic -> false
                let direct = ks.IsEmpty || joinChooseDirect m (List.min ks) (List.max ks) positional
                // the INCLUSIVE span as a bigint: [INT64_MIN, INT64_MAX] spans 2^64
                let span = if ks.IsEmpty then "no keys" else $"span {bigint (List.max ks) - bigint (List.min ks) + 1I}"
                let form = if direct then "counting sort" else "hash"
                $"form at run time: {form} ({span}, M {m}; the literal right keys, by the C core's rule)"
            | None -> "form at run time: chosen from (M, lo, hi) only, never from timing"
        let reportLine =
            if joinReportEnabled () then "report: the `[join]` build report is emitted (BLADE_JOIN_REPORT=1 at compile time; printed on stderr by the executable)"
            else "report: none emitted (BLADE_JOIN_REPORT unset at compile time; the compile-time gate emits the `[join]` build report)"
        let plan =
            { CompoundId = jb.Id; CompoundName = jName
              LeftKey = a0; RightKey = b0; LeftPay = a1; RightPay = b1
              LeftKeyName = nameOfVar a0; RightKeyName = nameOfVar b0
              PayloadKernel = dInfo.Kernel; KeyRegime = regime; EmittedCpp = ref false
              Residual = (match ec.Body with IRBinOp (_, IRAnd, _, _, _) -> Some mInfo.Kernel | _ -> None) }
        let evidence =
            [ $"stable CSR over `{nameOfVar b0}`: {regimeLine}; bucket rows filled FORWARD (every bucket lists its right rows j ascending)"
              formLine
              reportLine
              (match ec.Body with
               | IRBinOp (_, IRAnd, _, _, _) -> "residual (D3, ST15): the conjunctive key kernel is re-evaluated on each MATCHED pair, in the counting pass and the probe (its equality conjunct holds there; every residual conjunct is a pure total scalar of the key cells, so its skipped evaluations on non-matching pairs have no effect); a pair it rejects is not in the relation"
               | _ -> "residual: none (the key kernel is the plain `l == r`)")
              $"probe `{nameOfVar a0}` i ascending, EVERY right row of the bucket ascending: left-major / right-minor = the compound's lex order (BladeLex); bags survive (duplicates on both sides kept); negative keys match like any other; a left key no right row holds matches nothing (no BL8006: equality has no domain check)"
              $"a COUNTING pass sizes `{jName}` before any store (|J| = the sum over i of the bucket size of `{nameOfVar a0}`[i]; worst case, every key equal, |J| = N x M -- never more than the as-written N x M payload pool); pk evaluated once per match, in that order"
              $"never built: the N x M payload pool, the N x M Bool `==` pool, the identity-mask pool, its bit vector, the tuple table and the reverse hash (compiler temporaries of the inline spelling); `{jName}` is STORED -- its compact buffer in lex order, with a cardinality-only compound_index_t<2> (dense extents N x M, cardinality |J|; a lookup through the absent table is BL9002)"
              ("uses of `" + jName + "` (census: every one reads the compact buffer or the cardinality alone): " + String.concat ", " uses)
              matrix
              "lanes: C++ v1; the LLVM lane declines (v1.1); the interpreter runs the as-written six passes (the independent oracle)" ]
        Ok (plan, evidence)
    for KeyValue (jId, (dense, mask)) in modul.CompoundInits do
        match indexOf jId with
        | None -> ()
        | Some ji ->
        let jb = arr.[ji]
        let lead =
            match stripUnits jb.Type with
            | ArrayElem at ->
                (match at.IndexTypes |> List.tryFind (fun ix -> ix.IxKind = IxKCompound) with
                 | Some ix -> Some (ix.Rank, at.IndexTypes.Length - 1)
                 | None -> None)
            | _ -> None
        match lead with
        | Some (r, _) when r > 2 -> decide jb.Name (Blade.Effects.Declined $"a rank-{r} compound (v1 joins exactly two axes)") []
        | Some (2, trail) when trail > 0 -> decide jb.Name (Blade.Effects.Declined $"{trail} trailing dim(s) per kept cell (v1 joins a scalar payload per pair)") []
        | Some (2, _) ->
            match admit ji dense mask with
            | Ok (plan, evidence) when enabled ->
                EquiJoinTable.record plan
                decide jb.Name Blade.Effects.Applied evidence
            | Ok (_, evidence) -> decide jb.Name (Blade.Effects.Declined "disabled by BLADE_HASH_JOIN") evidence
            | Error why -> decide jb.Name (Blade.Effects.Declined why) []
        | _ -> ()
