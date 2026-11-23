// Aggregate-only grouping, rule group-aggregate, and its side table (split out of Optimize.fs, cleanup K19).
module Blade.OptimizeGroupAgg

open Blade.Types

open Blade.Ast
open Blade.IR
open Blade.IRMono
open Blade.OptimizeGates
open Blade.OptimizeAnalysis
open Blade.OptimizeStorage

// --- Aggregate-only grouping (round 3 wave 3, W3-1: T3 redesigned; gap G71) -
//
// A `group_keys` consumed only by per-group AGGREGATES:
//
//   function agg(key: .., x: ..) = {
//       let gk = group_keys(key)
//       let g = group_by(x, gk)
//       let s = method_for(g) <@> lambda(r) -> reduce(r, (+)) |> compute
//       let q = method_for(g) <@> lambda(r) -> prodsum(r, r) |> compute
//       let c = extents(gk)
//       ...
//   }
//
// runs today as two or three key passes (discovery, counts, a scattered
// permutation), one gather per group_by into a group-contiguous pool, and one
// pass per peel over the pool. Every peel's chain visits its group's rows in
// ASCENDING SOURCE ROW (the permutation is stable), so the same chains can be
// advanced in ONE pass over the rows in source order: per row, read the key,
// find its group, and advance every state of that group by that row's
// values. Interleaving across groups reorders only INDEPENDENT chains; each
// chain keeps its operands, its order, its seed and its left association --
// bitwise the two-pass form under BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off in
// both lanes. No permutation, no gathered pool (the grouped arrays' rows stay
// null, as in the extents-only gather elision), and each peel binding reads
// its state (`out[g] = acc[g]`), still a named, stored output.
//
// FOUR state machines, never one (review B/2):
//   * UNSEEDED `reduce(r, k)`: a group's FIRST value is copied as the state
//     and consumed exactly once (the first-row update is skipped); every
//     later row is `acc = k(acc, v)`. An EMPTY group keeps its BL8003, raised
//     at the peel's own site for the first empty group in group order.
//   * SEEDED `reduce(r, k, init)`: the original chain from the exact seed --
//     `acc = init` once per group (statically declared empty groups included:
//     they answer the seed), then `acc = k(acc, v)` per row.
//   * PRODSUM `prodsum(r, r)` / `prodsum(ra, rb)`: the prodsum emitters' own
//     form -- `acc = 0` once per group, then `acc += a * b` (the product
//     rounded, then added; the product's operand order as written).
//   * COUNT `extents(r)`: the group's row count (the peel reads the offsets,
//     exactly as today).
// No identity is ever substituted for a generic callable.
//
// THREE regimes kept apart (review B/4): POSITIONAL `Idx<N>` keys -- group g
// is key value g, 0 .. N-1, empty groups included; ENUMIDX -- the DECLARED
// enumeration order, negative members valid (never dropped); DYNAMIC -- groups
// numbered by FIRST occurrence through one find-or-insert per row in the C
// core (blade_member.h's key -> group map: its table order never defines an
// output order). The negative-key drop and the BL8006 key guards are exactly
// today's, at the grouping's site, row by row in source order.
//
// STRICT FIRST (review B/3): in the repro build every admitted state takes the
// pass. In the default (reordering) build a state takes it only when its
// chain is already the strict one there (a kernel with no reorder licence) or
// its value is exact under any grouping (an integer builtin fold, an integer
// prodsum, a count); a LICENSED float fold keeps today's per-group K-lane
// tree -- the one pass's ascending chain is a different association, with no
// tolerance guarantee ([1e16, -1e16, 1, 0, 0, 0, 0, 0, 1] sums to 2 in the
// chain and to 1 in eight lanes) -- unless BLADE_GROUP_AGG=licensed asks for
// that association change explicitly.
//
// FIRST FAILURE (review B/1): accumulation MOVES from the peels up to the
// grouping, so v1 admits only value sources already STORED and DOMINATING
// (bound before the grouping), never assigned anywhere; fold kernels and
// seeds on the closed total whitelist (Blade.IRTotality), row-independent
// and dominating; and nothing between the grouping and the last peel may
// write (the five parties through `scheduleMayWriteIn`). The pass is then
// total apart from the grouping's own BL8006, which fires where today's count
// pass fires it; an intervening failing binding still fails first; each
// BL8003 stays at its peel. FUNCTION BODIES ONLY: a top-level grouped array
// is a named top-level binding and keeps its rows stored (review B/10).
//
// Side table (`GroupAggTable`), both emitters; the IR is untouched, so the
// interpreter runs the as-written CSR + gather + peels (the independent
// oracle of `blade test interp`). Gate: BLADE_GROUP_AGG (`0`/`off`; unset =
// the strict pass plus the exact integer arm; `licensed` adds the licensed
// float arm).

type GroupAggMode =
    | GroupAggOff
    | GroupAggStrict
    | GroupAggLicensed

let groupAggMode () : GroupAggMode =
    match Blade.Toolchain.envSwitch "BLADE_GROUP_AGG" with
    | Blade.Toolchain.SwitchOff -> GroupAggOff
    | Blade.Toolchain.SwitchOther v when v.Trim().ToLowerInvariant() = "licensed" -> GroupAggLicensed
    | _ -> GroupAggStrict

/// The grouping's bucketing regime (the three kept apart).
type GroupAggRegime =
    | GaPositional of groups: int64
    | GaEnum of values: int64 list
    | GaDynamic

/// One state machine.
type GroupAggKind =
    | GaSeeded of kernel: IRExpr * seed: IRExpr
    | GaUnseeded of kernel: IRExpr
    | GaProdSum
    | GaCount

/// One peel's state.
type GroupAggState = {
    PeelId: IRId
    PeelLabel: string
    Kind: GroupAggKind
    /// The stored value sources read at each row (one for a reduce, two for
    /// a prodsum -- the product's operand order -- none for a count).
    Sources: IRExpr list
    /// The state's (the peel output's) element type: Float64 or Int64.
    Elem: ElemType
    /// The peel kernel's frame name (the backtrace line its empty check keeps).
    KernelFrame: string
}

type GroupAggPlan = {
    GkId: IRId
    Subject: string
    FuncId: IRId
    Regime: GroupAggRegime
    Key: IRExpr
    States: GroupAggState list
    GroupBys: Set<IRId>
    Admission: string
    /// Placement handshakes: set by each lane's group_keys emission (true =
    /// it emitted the one pass), read by that lane's group_by and peel
    /// emissions, which take the planned forms only after it.
    EmittedCpp: bool ref
    EmittedLlvm: bool ref
}

/// AsyncLocal like CompactionTable; reset once per program in
/// `lowerTypedProgram` (let ids restart per compile).
module GroupAggTable =
    open System.Threading
    type State =
        { ByGk: Map<IRId, GroupAggPlan>
          ByGroupBy: Map<IRId, IRId>
          ByPeel: Map<IRId, IRId * int> }
    let private empty = { ByGk = Map.empty; ByGroupBy = Map.empty; ByPeel = Map.empty }
    let private cell = new AsyncLocal<State>()
    let private get () = match box cell.Value with null -> empty | _ -> cell.Value
    let reset () = cell.Value <- empty
    let record (p: GroupAggPlan) =
        let s = get ()
        cell.Value <-
            { ByGk = Map.add p.GkId p s.ByGk
              ByGroupBy = p.GroupBys |> Set.fold (fun m g -> Map.add g p.GkId m) s.ByGroupBy
              ByPeel =
                p.States |> List.mapi (fun j st -> (st.PeelId, (p.GkId, j)))
                |> List.fold (fun m (k, v) -> Map.add k v m) s.ByPeel }
    let tryGk (id: IRId) : GroupAggPlan option = Map.tryFind id (get ()).ByGk
    let tryGroupBy (id: IRId) : GroupAggPlan option =
        Map.tryFind id (get ()).ByGroupBy |> Option.bind tryGk
    /// The plan and the state index a peel binding reads.
    let tryPeel (id: IRId) : (GroupAggPlan * int) option =
        Map.tryFind id (get ()).ByPeel |> Option.bind (fun (g, j) -> tryGk g |> Option.map (fun p -> (p, j)))
    let all () : GroupAggPlan list = (get ()).ByGk |> Map.toList |> List.map snd

/// The regime's record text.
let groupAggRegimeText (r: GroupAggRegime) : string =
    match r with
    | GaPositional n ->
        $"positional (Idx<{n}> keys): group g is key value g, 0 .. {n - 1L}, EMPTY groups included; a negative key drops its row and a key >= {n} aborts BL8006 at the grouping, row by row in source order, exactly as today's count pass"
    | GaEnum vs ->
        let shown = vs |> List.map string |> String.concat ", "
        $"EnumIdx ({vs.Length} declared members; the stored key of each, in declared order: [{shown}]): groups in the DECLARED enumeration order (never sorted by value), EMPTY groups included, negative members valid (an EnumIdx key never drops); a key outside the enumeration aborts BL8006 at the grouping, row by row in source order, exactly as today"
    | GaDynamic ->
        "dynamic: groups numbered by FIRST occurrence, through ONE find-or-insert per row in the C core (blade_member.h's key -> group map, both lanes; its table order never defines an output order); a negative key drops its row, exactly as today"

/// Plan aggregate-only grouping over the module's function bodies. Records
/// into `GroupAggTable` and the decision collector (`group-aggregate`); the
/// IR is untouched.
let planGroupAggregate (programFuncs: IRCallable list) (modul: IRModule) : unit =
    let mode = groupAggMode ()
    let funcs = modul.Functions |> List.map (fun f -> (f.Id, f)) |> Map.ofList
    let allCallables = (modul.Functions @ programFuncs) |> List.distinctBy (fun f -> f.Id)
    let callableById = allCallables |> List.map (fun f -> (f.Id, f)) |> Map.ofList
    let resolve (e: IRExpr) : IRCallable option =
        match fst (peelReynolds e) with
        | IRVar (id, _) -> Map.tryFind id callableById
        | _ -> None
    let facts = cseFactsOf funcs
    let writes = scheduleMayWriteIn programFuncs modul
    let wrote (e: IRExpr) = (facts e).MayWrite || writes e
    let reorderingBuild = not (Blade.Toolchain.fpReproEnabled ())
    let decide (subject: string) (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) =
        Blade.Effects.Decisions.record
            { Blade.Effects.Rule = "group-aggregate"; Version = 1
              Span = declSpanOf subject; Subject = subject
              Outcome = outcome; Evidence = evidence }
    let strip (e: IRExpr) = match e with IRCompute inner -> inner | e -> e
    let refs (e: IRExpr) = collectVarRefsIR e
    let bindingIds = modul.Bindings |> List.map (fun b -> b.Id) |> Set.ofList
    let bindingById = modul.Bindings |> List.map (fun b -> (b.Id, b)) |> Map.ofList
    let storedProof = storedArrayProof modul
    // Every let value in the program (module bindings and every body let):
    // a deferred one is never a proved-stored source.
    let letValues = System.Collections.Generic.Dictionary<IRId, IRExpr>()
    for b in modul.Bindings do letValues.[b.Id] <- b.Value
    // the lets inside a binding's payload (a top-level block's `let x = ...`)
    for b in modul.Bindings do
        for e in bindingPayload modul b do
            iterIRExpr (fun n -> match n with IRLet (id, v, _) -> letValues.[id] <- v | _ -> ()) e
    for f in allCallables do
        iterIRExpr (fun n -> match n with IRLet (id, v, _) -> letValues.[id] <- v | _ -> ()) f.Body
    // widened by the program's classifier (gap G98: aliases, wholly-deferred
    // tuples and their projections, let chains ending deferred)
    let isDeferredId =
        widenDeferred modul allCallables (fun (id: IRId) ->
            match letValues.TryGetValue id with
            | true, v -> isDeferredForm v
            | _ -> false)
    let assignsTo (id: IRId) (e: IRExpr) =
        let mutable hit = false
        iterIRExpr (fun n ->
            match n with
            | IRAssign (target, _) when Set.contains id (refs target) -> hit <- true
            | _ -> ()) e
        hit
    let assignedAnywhere (id: IRId) =
        (modul.Bindings |> List.exists (fun b -> bindingPayload modul b |> List.exists (assignsTo id)))
        || (allCallables |> List.exists (fun f -> assignsTo id f.Body))
    // How many times an id is NAMED: inside a given expression, and across
    // the whole program (every binding payload and every callable body).
    let countIn (id: IRId) (e: IRExpr) =
        let mutable k = 0
        iterIRExpr (fun n -> match n with IRVar (i, _) when i = id -> k <- k + 1 | _ -> ()) e
        k
    let countProgram (id: IRId) =
        (modul.Bindings |> List.sumBy (fun b -> bindingPayload modul b |> List.sumBy (countIn id)))
        + (allCallables |> List.sumBy (fun f -> countIn id f.Body))
    let scalarOf (t: IRType) : ElemType option = IRTotality.scalarElem (stripUnits t)
    let elemName (e: ElemType) =
        match e with
        | ETInt64 -> "Int64" | ETInt32 -> "Int32" | ETFloat64 -> "Float64" | ETFloat32 -> "Float32"
        | ETComplex64 -> "Complex64" | ETComplex128 -> "Complex128" | ETBool -> "Bool"
        | ETString -> "String" | ETUnit -> "Unit"
    let builtinOp (c: IRCallable) : IRBinOp option =
        match c.Params, c.Body with
        | [ p0; p1 ], IRBinOp (_, op, IRVar (a, _), IRVar (b, _), _)
            when (match op with IRAdd | IRMul | IRAnd | IROr -> true | _ -> false)
                 && ((a = p0.VarId && b = p1.VarId) || (a = p1.VarId && b = p0.VarId)) -> Some op
        | _ -> None
    // `foldReorderLicensed`'s rule (CodeGenExprSupport), restated over this
    // module's table, as the sibling-fold pass restates it: a `where repro`
    // kernel (or the eta wrapper of one) vetoes; a writing kernel never
    // reaches here (its write declines first).
    let licensed (c: IRCallable) =
        let vetoed =
            c.IsRepro
            || (match c.Body with
                | CallOfNamed (fid, _, _, _) -> (match Map.tryFind fid callableById with Some g -> g.IsRepro | None -> false)
                | _ -> false)
        not vetoed && (c.IsCommutative || not c.CommGroups.IsEmpty || (builtinOp c).IsSome)
    // A lifted peel kernel is named after its binding (`lambda in s`); a
    // function-body let has no other source name in the IR.
    let labelOf (c: IRCallable) =
        if c.SourceName.StartsWith "lambda in " then c.SourceName.Substring 10
        elif c.Name.StartsWith "lambda in " then c.Name.Substring 10
        else c.SourceName
    // ---- top level: a grouped array is a named binding (review B/10) ----
    for b in modul.Bindings do
        match strip b.Value with
        | IRGroupKeys _ ->
            decide b.Name (Blade.Effects.Declined "a top-level grouping: v1 plans groupings inside a function body only (review B/10: a top-level group_by result is a named binding, and an unprinted name is no permission for THIS plan to drop its rows; whether they are gathered stays the existing extents-only elision's decision, computeExtentsOnlyGroupBys)")
                [ "the CSR (counts, offsets, permutation) and the peels stay as written; this plan does not touch the group_by gathers" ]
        | _ -> ()
    // ---- function bodies ----
    let containing = System.Collections.Generic.Dictionary<IRId, int>()
    for f in modul.Functions do
        let seen = System.Collections.Generic.HashSet<IRId>()
        iterIRExpr (fun n ->
            match n with
            | IRLet (id, IRGroupKeys _, _) | IRLet (id, IRCompute (IRGroupKeys _), _) ->
                if seen.Add id then containing.[id] <- (match containing.TryGetValue id with | true, k -> k + 1 | _ -> 1)
            | _ -> ()) f.Body
    for f in modul.Functions do
        let subject = f.SourceName
        let rec spineOf (e: IRExpr) (acc: (IRId * IRExpr) list) =
            match e with
            | IRLet (id, v, body) -> spineOf body ((id, v) :: acc)
            | _ -> List.rev acc
        let spine = spineOf f.Body [] |> List.toArray
        let spineIndex = spine |> Array.mapi (fun i (id, _) -> (id, i)) |> Map.ofArray
        let gkLets = System.Collections.Generic.List<IRId * IRExpr list>()
        iterIRExpr (fun n ->
            match n with
            | IRLet (id, v, _) ->
                (match strip v with
                 | IRGroupKeys ks -> gkLets.Add((id, ks))
                 | _ -> ())
            | _ -> ()) f.Body
        for (gkId, keys) in gkLets |> Seq.distinctBy fst do
            let fail (why: string) (ev: string list) = decide subject (Blade.Effects.Declined why) ev
            match Map.tryFind gkId spineIndex with
            | None -> fail "the grouping is not a statement of the function body's top-level let chain (v1)" []
            | Some _ when (match containing.TryGetValue gkId with | true, k -> k > 1 | _ -> false) ->
                fail "the grouping's function is present in more than one specialization (v1 plans one body)" []
            | Some gi ->
            match keys with
            | [] | _ :: _ :: _ -> fail "a multi-key grouping (v1: one integer key; a compound tuple key keeps the hashed CSR)" []
            | [ key ] ->
            // The regime, from the body's typed mentions of the grouping
            // (the recovery both lanes' emitters make).
            let mutable gkTy : IRType option = None
            iterIRExpr (fun n ->
                match n with
                | IRVar (vid, (IRTGroupKeys _ as ty)) when vid = gkId && gkTy.IsNone -> gkTy <- Some ty
                | _ -> ()) f.Body
            let regime : Result<GroupAggRegime, string> =
                match gkTy with
                | Some (IRTGroupKeys (outer, _, ev)) ->
                    (match IRTotality.evalInt outer.Extent, ev with
                     | Some n, None -> Ok (GaPositional n)
                     | Some _, Some vs ->
                         if vs |> List.forall (function EVInt _ -> true | _ -> false) then
                             Ok (GaEnum (vs |> List.choose (function EVInt n -> Some n | _ -> None)))
                         else Error "string keys (an EnumIdx of strings; v1: Int64 keys)"
                     | None, _ -> Ok GaDynamic)
                | _ -> Error "the grouping's regime is not recoverable from a typed mention"
            match regime with
            | Error why -> fail why []
            | Ok regime ->
            // Dominating names: f's parameters, the spine lets before the
            // grouping, module bindings and callables.
            let paramIds = f.Params |> List.map (fun p -> p.VarId) |> Set.ofList
            let before = spine |> Array.take gi |> Array.map fst |> Set.ofArray
            let dominates (id: IRId) =
                paramIds.Contains id || before.Contains id || bindingIds.Contains id || callableById.ContainsKey id
            // THE PRODUCER PROOF (Codex diff review of W3-1, findings 1 and 3):
            // a source's cells must EXIST at the grouping and its producer must
            // be write-free, proved by QUERIES, not asserted. A named array is
            // proved by its provenance: a module binding through the shared
            // `storedArrayProof` (every link of its alias chain then audited by
            // `scheduleMayWriteIn` + the CSE facts over its PAYLOAD, and never
            // assigned), any other let by the same stored-form rule over its
            // value (a `|> compute`, a literal, a call's array result, an eager
            // set operation -- never a deferred form) and the same write query
            // over that value. `assignedAnywhere` stays a supplementary
            // syntactic check.
            let storedLetForm (v: IRExpr) : string option =
                if isDeferredForm v then None
                else
                match v with
                | IRCompute _ -> Some "`|> compute`d at its site"
                | IRArrayLit _ -> Some "an array literal"
                | IRApp _ -> Some "a call's array result"
                | IRMask _ | IRSort _ | IRUnique _ | IRIntersect _ | IRUnion _ -> Some "an eager set operation"
                | _ -> None
            let provenance (what: string) (sid: IRId) : Result<string, string> =
                if assignedAnywhere sid then Error $"the {what} is assigned in the program (v1: a source never written)"
                elif bindingIds.Contains sid then
                    let b = Map.find sid bindingById
                    match storedProof b with
                    | Error why -> Error $"the {what} is not proved stored: {why}"
                    | Ok (how, chain) ->
                        match chain |> List.tryFind (fun lb -> wrote (bindingPayloadExpr modul lb) || assignedAnywhere lb.Id) with
                        | Some lb -> Error $"the {what}'s producer `{lb.Name}` may write or is assigned (scheduleMayWriteIn over its payload)"
                        | None ->
                            let links = chain |> List.map (fun lb -> $"`{lb.Name}`") |> String.concat " -> "
                            Ok $"`{b.Name}`: a module binding, {how}; producer payload(s) {links}: scheduleMayWriteIn + CSE facts, no write"
                else
                match letValues.TryGetValue sid with
                | true, v ->
                    match storedLetForm v with
                    | None ->
                        if isDeferredForm v then Error $"the {what} is a deferred computation (v1 never forces a producer early)"
                        else Error $"the {what} is a let whose value is not a form v1 proves stored"
                    | Some how ->
                        if wrote v then Error $"the {what}'s producer may write (scheduleMayWriteIn over its value)"
                        else Ok $"a let, {how}; its value: scheduleMayWriteIn + CSE facts, no write"
                | _ -> Error $"the {what} is not a let, a module binding or a parameter v1 can trace"
            // A PARAMETER's provenance: every call site of the function passes,
            // at that position, a named array proved as above or an array
            // literal whose value writes nothing; a function referenced other
            // than by direct calls (passed as a value) declines.
            let callSites =
                lazy (
                    let acc = ResizeArray<IRExpr list>()
                    let scan (e: IRExpr) =
                        iterIRExpr (fun n ->
                            match n with
                            | CallOfNamed (fid, _, args, _) when fid = f.Id -> acc.Add args
                            | _ -> ()) e
                    for b in modul.Bindings do
                        for e in bindingPayload modul b do scan e
                    for c in allCallables do scan c.Body
                    List.ofSeq acc)
            let paramProof (what: string) (p: IRParam) : Result<string, string> =
                let calls = callSites.Force()
                let pos = f.Params |> List.findIndex (fun q -> q.VarId = p.VarId)
                if calls.IsEmpty then Error $"the {what} is a parameter of `{f.SourceName}`, which has no call site v1 can audit"
                elif countProgram f.Id <> calls.Length then Error $"the {what} is a parameter of `{f.SourceName}`, which is referenced other than by direct calls (its arguments cannot be audited)"
                else
                let argProof (args: IRExpr list) : Result<string, string> =
                    if pos < 0 || pos >= args.Length then Error "a call site with a different arity"
                    else
                    match args.[pos] with
                    | IRVar (aid, _) -> provenance $"argument for `{p.Name}`" aid
                    | IRArrayLit _ as lit ->
                        if wrote lit then Error $"the literal argument for `{p.Name}` may write" else Ok "an array literal argument, no write"
                    | _ -> Error $"the argument for `{p.Name}` at a call site is an expression, not a named stored array or a literal (v1)"
                match calls |> List.map argProof |> List.tryPick (function Error why -> Some why | Ok _ -> None) with
                | Some why -> Error $"the {what} (a parameter of `{f.SourceName}`) is not proved stored at every call site: {why}"
                | None ->
                    let shown = calls |> List.map argProof |> List.choose (function Ok s -> Some s | _ -> None) |> List.distinct |> String.concat " / "
                    Ok $"a parameter of `{f.SourceName}`; at each of its {calls.Length} call site(s): {shown}"
            // A source array (the key or a value): rank 1, dense or a rank-1
            // compound, a scalar element; STORED and DOMINATING, never assigned,
            // its producer proved write-free.
            let sourceProof (what: string) (e: IRExpr) : Result<string * ElemType, string> =
                match e with
                | IRVar (sid, sty) ->
                    let shape =
                        match stripUnits sty with
                        | ArrayElem at when at.IndexTypes.Length = 1 && not at.IsVirtual ->
                            let ix = at.IndexTypes.[0]
                            if ix.Rank = 1 && ix.Symmetry = SymNone && (ix.IxKind = IxKPlain || ix.IxKind = IxKCompound) then
                                match scalarOf at.ElemType with
                                | Some et -> Ok et
                                | None -> Error $"the {what} is not an array of scalars"
                            else Error $"the {what} is not a dense rank-1 axis or a rank-1 compound"
                        | ArrayElem at when at.IsVirtual -> Error $"the {what} is a virtual array (no stored cells)"
                        | _ -> Error $"the {what} is not a rank-1 array"
                    match shape with
                    | Error why -> Error why
                    | Ok et ->
                    if assignedAnywhere sid then Error $"the {what} is assigned in the program (v1: a source never written)"
                    elif paramIds.Contains sid then
                        let p = f.Params |> List.find (fun p -> p.VarId = sid)
                        paramProof what p |> Result.map (fun how -> (how, et))
                    elif before.Contains sid then
                        provenance what sid |> Result.map (fun how -> ($"a body let bound before the grouping -- {how}", et))
                    elif bindingIds.Contains sid then
                        provenance what sid |> Result.map (fun how -> (how, et))
                    elif Map.containsKey sid spineIndex then
                        Error $"the {what} is bound after the grouping (v1: a dominating source)"
                    else Error $"the {what} is not a parameter, an earlier body let or a module binding"
                | _ -> Error $"the {what} is an expression, not a named stored array"
            match sourceProof "key array" key with
            | Error why -> fail why []
            | Ok (_, kElem) when kElem <> ETInt64 ->
                fail $"the keys are {elemName kElem} (v1: Int64 keys; float and string keys keep the CSR)" []
            | Ok (keyHow, _) ->
            // ---- uses of the grouping: group_by(_, gk) and extents(gk) only ----
            let groupBySites =
                spine |> Array.toList |> List.indexed |> List.choose (fun (i, (id, v)) ->
                    match strip v with
                    | IRGroupBy (src, IRVar (g, _)) when g = gkId -> Some (i, id, src)
                    | _ -> None)
            let sizeSites =
                spine |> Array.toList |> List.filter (fun (_, v) ->
                    match strip v with
                    | IRGroupSizes (IRVar (g, _)) when g = gkId -> true
                    | _ -> false)
            let gkUses = countProgram gkId
            if gkUses <> groupBySites.Length + sizeSites.Length then
                fail "the grouping is used other than as `group_by(_, gk)` / `extents(gk)` statements of its body (a group_bucket, an alias, a capture, a nested use: v1 declines)" []
            else
            let gbIds = groupBySites |> List.map (fun (_, id, _) -> id) |> Set.ofList
            let gbSource = groupBySites |> List.map (fun (_, id, src) -> (id, src)) |> Map.ofList
            // ---- the peels ----
            let peelSites =
                spine |> Array.toList |> List.indexed |> List.choose (fun (i, (id, v)) ->
                    match strip v with
                    | IRApplyCombinator info when info.Arrays |> List.exists (function IRVar (a, _) -> gbIds.Contains a | _ -> false) ->
                        Some (i, id, v, info)
                    | _ -> None)
            // every use of a grouped array must sit inside one of these peels
            let outsideUse =
                gbIds |> Seq.tryFind (fun g ->
                    let inPeels = peelSites |> List.sumBy (fun (_, _, v, _) -> countIn g v)
                    countProgram g <> inPeels)
            match outsideUse with
            | Some _ ->
                fail "a grouped array is used other than as the operand of a peel statement of the body (a returned / escaping or aliased grouped array, a capture, a row read outside a peel)" []
            | None ->
            // a peel state, or the first failed condition
            let stateOf (pid: IRId) (v: IRExpr) (info: ApplyInfo) : Result<GroupAggState * IRCallable list * IRExpr list, string> =
                match resolve info.Kernel with
                | None -> Error "a peel kernel does not resolve to a callable"
                | Some kc ->
                let label = labelOf kc
                if info.HasReynolds then Error $"the peel `{label}` is a reynolds kernel"
                elif kc.IsOmpParallel then Error $"the peel `{label}` is a `where omp` peel (its row loop is a licensed team; sql-group-by/033: v1 declines)"
                elif kc.IsCudaKernel || kc.IsMpiParallel then Error $"the peel `{label}` is a CUDA / MPI kernel"
                elif kc.Params.Length <> info.Arrays.Length then Error $"the peel `{label}`'s kernel arity differs from its operands"
                elif not (info.Arrays |> List.forall (function IRVar (a, _) -> gbIds.Contains a | _ -> false)) then
                    Error $"the peel `{label}` co-iterates a grouped array with an operand that is not grouped by the same grouping"
                elif wrote v then Error $"the peel `{label}` may write (its kernel or an operand, through the shared write analysis)"
                elif not (kc.Captures |> List.forall (fun c -> dominates c.Id)) then
                    Error $"the peel `{label}`'s kernel captures a value bound after the grouping (a late seed or scalar: it cannot be read where the pass runs)"
                else
                let outElem =
                    match stripUnits info.OutputType with
                    | ArrayElem at when at.IndexTypes.Length = 1 -> scalarOf at.ElemType
                    | _ -> None
                match outElem with
                | None -> Error $"the peel `{label}` does not collapse each group to one scalar"
                | Some oe when oe <> ETFloat64 && oe <> ETInt64 -> Error $"the peel `{label}`'s result is {elemName oe} (v1 states: Float64 / Int64)"
                | Some oe ->
                let srcOf (arr: IRExpr) = match arr with IRVar (a, _) -> Map.find a gbSource | e -> e
                let srcElem (e: IRExpr) = match stripUnits (typeOf e) with ArrayElem at -> scalarOf at.ElemType | _ -> None
                let pIds = kc.Params |> List.map (fun p -> p.VarId)
                let operandOfParam (vid: IRId) = List.findIndex ((=) vid) pIds |> fun k -> srcOf info.Arrays.[k]
                match kc.Params, kc.Body with
                | [ r ], ReduceOfNamed (rv, _, fk, init) when rv = r.VarId ->
                    (match resolve fk with
                     | Some fkc when fkc.Params.Length = 2 ->
                         let src = operandOfParam rv
                         let se = srcElem src
                         // THE FOLD'S TYPES (Codex diff review of W3-1, finding
                         // 2): the state, both kernel parameters, the kernel's
                         // result, the element and (below) the seed must be ONE
                         // supported scalar representation -- a lane's ordinary
                         // fold picks its accumulator from the kernel's result
                         // and converts the first value to it, which the pass
                         // (a state of the output's type) would not reproduce.
                         let shown (e: ElemType option) = match e with Some e -> elemName e | None -> "unresolved"
                         let pEl = fkc.Params |> List.map (fun p -> scalarOf p.Type)
                         let rEl = scalarOf fkc.RetType
                         let pShown = pEl |> List.map shown |> String.concat ", "
                         if pEl <> [ Some oe; Some oe ] || rEl <> Some oe || se <> Some oe then
                             Error $"the fold kernel of `{label}` is not typed in the state's one scalar representation ({elemName oe}): parameters ({pShown}) -> {shown rEl} over {shown se} elements (v1 requires state, parameters, result, element and seed to agree; a mixed-type fold keeps the CSR)"
                         elif wrote fk then Error $"the fold kernel of `{label}` may write"
                         elif not (fkc.Captures |> List.forall (fun c -> dominates c.Id)) then
                             Error $"the fold kernel of `{label}` captures a value bound after the grouping"
                         else
                         match IRTotality.auditCallable resolve (fun _ -> None) isDeferredId fkc [] with
                         | Error why -> Error $"the fold kernel of `{label}` is not closed-total ({why})"
                         | Ok () ->
                         match init with
                         | None ->
                             if se <> Some oe then Error $"the unseeded fold `{label}` changes element type (its first value is copied as the state)"
                             else Ok ({ PeelId = pid; PeelLabel = label; Kind = GaUnseeded fk; Sources = [ src ]; Elem = oe; KernelFrame = kc.SourceName }, [ fkc ], [])
                         | Some s ->
                             if not (refs s |> Set.forall dominates) then
                                 Error $"the seed of `{label}` reads a value that does not dominate the grouping (a row read, a value bound after it)"
                             elif wrote s then Error $"the seed of `{label}` may write"
                             elif (match se with Some e -> not (IRTotality.isReal e) | None -> true) then Error $"the fold `{label}` folds a non-real element"
                             else
                             match IRTotality.walk resolve (fun _ -> None) isDeferredId Map.empty Map.empty Set.empty Set.empty Set.empty s with
                             | Error why -> Error $"the seed of `{label}` may fail ({why})"
                             | Ok _ ->
                                 if scalarOf (typeOf s) <> Some oe then Error $"the seed of `{label}` is not of the state's element type"
                                 else Ok ({ PeelId = pid; PeelLabel = label; Kind = GaSeeded (fk, s); Sources = [ src ]; Elem = oe; KernelFrame = kc.SourceName }, [ fkc ], [ s ])
                     | _ -> Error $"the fold kernel of `{label}` is not a resolved two-parameter callable")
                | [ r ], IRProdSum [ IRVar (a, _); IRVar (b, _) ] when a = r.VarId && b = r.VarId ->
                    let src = operandOfParam a
                    if srcElem src <> Some oe then Error $"the prodsum `{label}` changes element type"
                    else Ok ({ PeelId = pid; PeelLabel = label; Kind = GaProdSum; Sources = [ src; src ]; Elem = oe; KernelFrame = kc.SourceName }, [], [])
                | [ ra; rb ], IRProdSum [ IRVar (a, _); IRVar (b, _) ]
                    when (a = ra.VarId || a = rb.VarId) && (b = ra.VarId || b = rb.VarId) ->
                    let sa = operandOfParam a
                    let sb = operandOfParam b
                    if srcElem sa <> Some oe || srcElem sb <> Some oe then Error $"the prodsum `{label}` changes element type"
                    else Ok ({ PeelId = pid; PeelLabel = label; Kind = GaProdSum; Sources = [ sa; sb ]; Elem = oe; KernelFrame = kc.SourceName }, [], [])
                | [ r ], IRExtent (IRVar (rv, _), 0) when rv = r.VarId ->
                    Ok ({ PeelId = pid; PeelLabel = label; Kind = GaCount; Sources = []; Elem = oe; KernelFrame = kc.SourceName }, [], [])
                | _ -> Error $"the peel `{label}`'s kernel is outside the aggregate subset (reduce(r, k[, init]), prodsum(r, r), prodsum(ra, rb), extents(r)): a row read such as r(0), a composite body, or a kernel over several rows"
            let states =
                peelSites |> List.fold (fun acc (_, pid, v, info) ->
                    match acc with
                    | Error _ -> acc
                    | Ok xs -> (match stateOf pid v info with Ok x -> Ok (xs @ [ x ]) | Error why -> Error why)) (Ok [])
            match states with
            | Error why -> fail why []
            | Ok triples ->
            let states = triples |> List.map (fun (s, _, _) -> s)
            // ---- sources: EVERY group_by operand (count-only and unused
            // grouped arrays included -- their rows are not gathered either, so
            // an unproved producer would lose its evaluation), stored,
            // dominating, write-free (Codex diff review of W3-1, finding 1) ----
            let sourceExprs = groupBySites |> List.map (fun (_, _, src) -> src) |> List.distinct
            let sourceLines = sourceExprs |> List.map (fun e -> (e, sourceProof "value array" e))
            match sourceLines |> List.tryPick (fun (_, r) -> match r with Error why -> Some why | Ok _ -> None) with
            | Some why -> fail why []
            | None ->
            // ---- association, per state ----
            let assoc (s: GroupAggState) : Result<string, string> =
                let isInt = s.Elem = ETInt64
                let licensedText = "LICENSED (BLADE_GROUP_AGG=licensed, an EXPERIMENTAL arm): the ascending chain in place of today's form -- in the C++ lane a per-group K-lane tree, in the LLVM lane a recurrence carrying the fold's fast-math flags -- an association change under the source licence; bits may differ (no tolerance guarantee: the cancellation fixture [1e16, -1e16, 1, 0, 0, 0, 0, 0, 1] sums to 2 in the chain and to 0 in today's C++ K-lane form)"
                match s.Kind with
                | GaCount -> Ok "exact (a count)"
                | GaProdSum ->
                    if not reorderingBuild then Ok "strict (the repro build): `acc = 0`, then `acc += a * b` ascending"
                    elif isInt then Ok "exact (an integer prodsum: wrapping, the same value under any grouping)"
                    elif mode = GroupAggLicensed then Ok licensedText
                    else Error $"`{s.PeelLabel}` is a licensed Float64 prodsum (its `+` is licensed by construction): the one pass's ascending chain is a different association from today's licensed forms (the C++ lane's per-group K-lane tree; the LLVM lane's flagged recurrence), so v1 declines in both lanes -- a conservative shared gate, not a claim that the two lanes' forms agree (BLADE_GROUP_AGG=licensed takes it; the repro build takes the strict pass)"
                | GaSeeded (fk, _) | GaUnseeded fk ->
                    let fkc = (resolve fk).Value
                    if not reorderingBuild then Ok "strict (the repro build): the chain as written"
                    elif not (licensed fkc) then Ok "strict (the kernel carries no reorder licence: every build runs this exact chain)"
                    elif isInt && (builtinOp fkc).IsSome then Ok "exact (an integer builtin fold: wrapping, associative and commutative -- the same value under any grouping)"
                    elif mode = GroupAggLicensed then Ok licensedText
                    else Error $"`{s.PeelLabel}` is a licensed {elemName s.Elem} fold: the one pass's ascending chain is a different association from today's licensed forms (the C++ lane's per-group K-lane tree; the LLVM lane's flagged recurrence), so v1 declines in both lanes -- a conservative shared gate, not a claim that the two lanes' forms agree (BLADE_GROUP_AGG=licensed takes it; the repro build takes the strict pass)"
            let assocs = states |> List.map (fun s -> (s, assoc s))
            // ---- the five parties ----
            let lastPeel = peelSites |> List.map (fun (i, _, _, _) -> i) |> List.fold max gi
            let lastUse = (groupBySites |> List.map (fun (i, _, _) -> i)) |> List.fold max lastPeel
            let between = [ for i in gi + 1 .. lastUse -> spine.[i] ]
            let betweenWriter = between |> List.tryFind (fun (id, v) -> wrote (intervalPayloadOf modul id v))
            let foldKs = triples |> List.collect (fun (_, ks, _) -> ks)
            let seeds = triples |> List.collect (fun (_, _, ss) -> ss)
            let srcText =
                sourceLines |> List.map (fun (e, r) ->
                    let n = match e with IRVar (i, _) -> (match Map.tryFind i bindingById with Some b -> b.Name | None -> (f.Params |> List.tryFind (fun p -> p.VarId = i) |> Option.map (fun p -> p.Name) |> Option.defaultValue $"v{i}")) | _ -> "?"
                    match r with Ok (how, _) -> $"`{n}`: {how}" | Error _ -> "") |> String.concat "; "
            let producerParty = $"producers (the key and EVERY group_by operand, count-only and unused ones included, each by its provenance -- storedArrayProof's chain or the stored-let rule, every link's payload through scheduleMayWriteIn + the CSE facts, a parameter through every call site's argument): the key array: {keyHow}; value source(s) {srcText}; all dominating the grouping, none assigned anywhere (a supplementary syntactic check)"
            let peelParty =
                let names = states |> List.map (fun s -> $"`{s.PeelLabel}`") |> String.concat ", "
                if states.IsEmpty then "consumer (peel) kernels: none (only extents(gk))"
                else $"consumer (peel) kernels {names}: no write (scheduleMayWriteIn over each peel), inside the aggregate subset, captures dominating"
            let foldParty = $"outer fold kernels: {foldKs.Length}, each closed-total (Blade.IRTotality), no write, captures dominating"
            let seedParty = $"seeds: {seeds.Length}, each closed-total, no write, row-independent and dominating (evaluated once per group, before that group's first row, as each peel evaluated it once per group)"
            let betweenParty =
                match betweenWriter with
                | Some _ -> Error "a binding between the grouping and the last peel may write"
                | None -> Ok $"between the grouping and its last use: {between.Length} binding(s), none writes (scheduleMayWriteIn + the CSE facts over each binding's payload)"
            let parallelParty = "parallel: none -- the one pass is serial (a `where omp` peel declines; callbackTable unused: no parallel body is admitted)"
            let matrix =
                "groupAggAdmits (five parties, each through Optimize.scheduleMayWriteIn): "
                + ([ producerParty; peelParty; foldParty; seedParty
                     (match betweenParty with Ok s -> s | Error s -> "DECLINES: " + s); parallelParty ] |> String.concat "; ")
            let stateLine =
                let kindText (s: GroupAggState) =
                    match s.Kind with
                    | GaUnseeded _ -> "UNSEEDED reduce (first value copied and consumed once; empty group: BL8003 at the peel)"
                    | GaSeeded _ -> "SEEDED reduce (the chain from the exact seed; an empty group answers the seed)"
                    | GaProdSum -> "PRODSUM (acc = 0, then acc += a * b)"
                    | GaCount -> "COUNT (offsets[g + 1] - offsets[g])"
                let parts =
                    assocs |> List.map (fun (s, a) ->
                        let at = match a with Ok t -> t | Error t -> "DECLINES: " + t
                        $"`{s.PeelLabel}`: {kindText s}, {at}")
                if parts.IsEmpty then "states: none (the grouping's sizes only)" else "states: " + String.concat "; " parts
            let failureLine =
                "first failure: the pass raises only the grouping's own BL8006 (positional / EnumIdx), at the grouping's span and row, as today's first key pass; accumulation is total (stored sources, closed-total kernels and seeds), so a failing binding between the grouping and a peel still fails first; each unseeded peel keeps its BL8003 at its own site for the FIRST empty group in group order; the peel outputs stay named stored arrays"
            let unproved =
                "unproved: the bit identity with the two-pass form is ARGUED (each chain keeps its operands, order, seed and association; interleaving reorders only independent chains), not proved; the evidence is witnesses: the cli block's gate-off vs gate-on RAW-BIT comparisons (BLADE_PRINT_BITS, BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off, route asserted: the positional fixtures in both lanes, the dynamic ones in the C++ lane) and printed-value witnesses -- corpus sql-group-by/064-074 in the C++ lane (the default suite, test interp, opt-diff), of which the LLVM differential routes and plans 064, 065, 068, 071 and 074"
            let gkName = $"group_keys in `{f.SourceName}`"
            let evidence = [ $"{gkName}: {groupAggRegimeText regime}"; stateLine; failureLine; matrix; unproved ]
            match betweenParty with
            | Error why -> fail why evidence
            | Ok _ ->
            match assocs |> List.tryPick (fun (_, a) -> match a with Error t -> Some t | Ok _ -> None) with
            | Some why -> fail why evidence
            | None ->
            if mode = GroupAggOff then fail "disabled by BLADE_GROUP_AGG" evidence
            else
                GroupAggTable.record
                    { GkId = gkId; Subject = subject; FuncId = f.Id; Regime = regime; Key = key
                      States = states; GroupBys = gbIds; Admission = matrix
                      EmittedCpp = ref false; EmittedLlvm = ref false }
                let peelNames = states |> List.map (fun s -> s.PeelLabel) |> String.concat ", "
                decide subject Blade.Effects.Applied
                    ([ $"one pass over the rows in source order; states {peelNames}; no perm, no gather (the grouped arrays' rows stay null); each peel reads its state" ] @ evidence)
