// THE SORT FORM (round 4 wave 4, lane D item D1; plan card PM16, streaming
// plan section 5.3): `sort(A, key)`'s emission choices, plan rule
// `sort-form v1`, gate BLADE_SORT_FORM (an optimizer gate).
//
// The contract every form keeps (docs/features/sql.md section 9): a STABLE
// ascending sort by key under a TOTAL preorder -- every NaN key orders before
// every other key and all NaNs tie, -0.0 and +0.0 tie, other keys by `<`
// (`blade_rt::sort_key_less`; the interpreter's F# `compare`). Under a strict
// weak order the stable result is UNIQUE (the order by (key, input
// position)), so every form below yields the same permutation.
//
// Two choices, each a side-table fact the C++ emitter reads
// (CodeGenExpr.materializeSortForm); the interpreter runs the as-written sort
// (the oracle); the LLVM lane has no IRSort arm (the program falls back to
// C++, as before -- recorded in the evidence):
//
//   IDENTITY SOURCE. A single-slot plain rank-1 range operand
//   (`sort(range<I>, key)`, `sort(lo..hi, key)`) is its iota lo + i: the
//   output buffer is initialised with it and stable-sorted IN PLACE -- no
//   materialized index source, no permutation scratch, no gather. Gate off:
//   the range is materialized into a scratch source and the general form runs.
//
//   KEY CACHE. A key the closed totality whitelist admits (IRTotality, its
//   parameter bound as a LEVEL of the range's index type when the source is a
//   range, so `k(i)` is proven; every deferred value -- the program's
//   classifier, streamed provider reads, lets whose value is a deferred form,
//   unknown binders -- answers deferred, fail closed), that writes nothing
//   (`scheduleMayWriteIn`) and whose result is a real / integer / Bool scalar:
//   each key evaluated ONCE, in ascending input order, into a buffer of the
//   key's OWN type (never widened: an Int64 key above 2^53 stays exact), the
//   sort compares the cached keys, the buffer is freed right after the sort.
//   Legal because a total pure key's evaluation count and order are
//   unobservable (std::stable_sort never calls the comparator for n <= 1, so
//   even one evaluation needs the totality proof -- kept).
module Blade.OptimizeSort

open Blade.Types
open Blade.IR

type SortFormPlan = {
    /// The key callable's id (the side-table key).
    KeyId: IRId
    /// The operand is a single-slot plain rank-1 range and the identity form
    /// is admitted.
    Identity: bool
    /// The key cache is admitted.
    KeyCache: bool
}

/// AsyncLocal like EquiJoinTable; reset once per program in Lowering.
module SortFormTable =
    open System.Threading
    let private cell = new AsyncLocal<Map<IRId, SortFormPlan>>()
    let private get () = match box cell.Value with null -> Map.empty | _ -> cell.Value
    let reset () = cell.Value <- Map.empty
    let record (p: SortFormPlan) = cell.Value <- Map.add p.KeyId p (get ())
    let tryKey (id: IRId) : SortFormPlan option = Map.tryFind id (get ())

/// BLADE_SORT_FORM=0|off: every sort keeps the general form (a range operand
/// materialized into a scratch source, the key evaluated per comparison).
/// Unset = on. Read per call.
let sortFormEnabled () = Blade.Toolchain.envFlag "BLADE_SORT_FORM" true

/// The single-slot plain dense rank-1 range: the only range with a storable
/// iota (CodeGenExpr.materializeRangeForm's guard, the interpreter's twin).
let identityRangeIx (arr: IRExpr) : IRIndexType option =
    match arr with
    | IRRange ([ ix ], _) when ix.IxKind = IxKPlain && ix.Rank = 1
                              && (match ix.Tag with
                                  | Some t -> not (t.StartsWith haloWinTagPrefix)
                                  | None -> true) -> Some ix
    | _ -> None

let private deferredForm (e: IRExpr) : bool =
    match e with
    | IRApplyCombinator _ | IRComposeApply _ | IRFusion _ | IRParallel _
    | IRComposeObj _ | IRComposeMeth _ | IRBind _ | IRFunctorMap _ | IRZip _
    | IRMethodFor _ | IRObjectFor _
    | IRChoice _ | IRFallback _ | IRGuard _ | IRSequence _
    | IRApp (IRObjectFor _, _, _) -> true
    | _ -> false

let private keyScalar (t: IRType) : bool =
    match stripUnits t with
    | IRTScalar (ETFloat64 | ETFloat32 | ETInt64 | ETInt32 | ETBool) -> true
    | IRTIdxTagged _ -> true
    | _ -> false

/// Plan every `sort` of the module (module payloads and callable bodies).
/// One record per sort site, subject = the binding / function holding it.
let planSortForms (programFuncs: IRCallable list) (modul: IRModule) : unit =
    let funcs = modul.Functions |> List.map (fun f -> (f.Id, f)) |> Map.ofList
    let resolve (k: IRExpr) = match k with IRVar (fid, _) -> Map.tryFind fid funcs | _ -> None
    let writes = Blade.Optimize.scheduleMayWriteIn programFuncs modul
    let enabled = sortFormEnabled ()
    // The deferred predicate (G98's lesson: the program's classifier plus the
    // same let rules, and unknown binders fail closed).
    let letValues = System.Collections.Generic.Dictionary<IRId, IRExpr>()
    for b in modul.Bindings do letValues.[b.Id] <- b.Value
    for b in modul.Bindings do
        for e in Blade.Optimize.bindingPayload modul b do
            iterIRExpr (fun n -> match n with IRLet (id, v, _) -> letValues.[id] <- v | _ -> ()) e
    for f in modul.Functions do
        iterIRExpr (fun n -> match n with IRLet (id, v, _) -> letValues.[id] <- v | _ -> ()) f.Body
    let known = System.Collections.Generic.HashSet<IRId>(letValues.Keys)
    for f in modul.Functions do
        known.Add f.Id |> ignore
        for p in f.Params do known.Add p.VarId |> ignore
        for c in f.Captures do known.Add c.Id |> ignore
        iterIRExpr (fun n ->
            match n with
            | IRForRange (v, _, _, _) -> known.Add v |> ignore
            | IRMatch (_, cases) -> for c in cases do for v in patternBoundIds c.Pattern do known.Add v |> ignore
            | _ -> ()) f.Body
    let moduleDeferred = IRLift.computeDeferredIds modul.Bindings
    let isDeferred (id: IRId) =
        let rec go (seen: Set<IRId>) (id: IRId) =
            not (known.Contains id)
            || moduleDeferred.Contains id
            || (match Map.tryFind id modul.ProviderReads with Some r -> r.Streamed | None -> false)
            || (match letValues.TryGetValue id with
                | true, v when not (seen.Contains id) -> valueDeferred (seen.Add id) v
                | _ -> false)
        and valueDeferred (seen: Set<IRId>) (v: IRExpr) =
            deferredForm v
            || (match v with
                | IRVar (a, _) -> go seen a
                | IRTuple (_ :: _ as es) -> es |> List.forall (valueDeferred seen)
                | IRTupleProj (p, _, _) -> valueDeferred seen p
                | IRLet _ ->
                    let rec final (e: IRExpr) = match e with IRLet (_, _, b) -> final b | _ -> e
                    valueDeferred seen (final v)
                | _ -> false)
        go Set.empty id
    let decide (subject: string) (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) =
        Blade.Effects.Decisions.record
            { Blade.Effects.Rule = "sort-form"; Version = 1
              Span = declSpanOf subject; Subject = subject
              Outcome = outcome; Evidence = evidence }
    let llvmNote = "llvm lane: no sort arm (the program falls back to the C++ lane, as before)"
    // Collect every site first: one key callable named by two sites with
    // different source shapes gets no plan (fail closed).
    let sites = System.Collections.Generic.List<string * IRExpr * IRExpr>()
    for b in modul.Bindings do
        for e in Blade.Optimize.bindingPayload modul b do
            iterIRExpr (fun n -> match n with IRSort (a, k) -> sites.Add((b.Name, a, k)) | _ -> ()) e
    for f in modul.Functions do
        iterIRExpr (fun n -> match n with IRSort (a, k) -> sites.Add((f.SourceName, a, k)) | _ -> ()) f.Body
    let byKey =
        sites |> Seq.choose (fun (s, a, k) -> resolve k |> Option.map (fun cl -> (cl.Id, (s, a, cl))))
              |> Seq.groupBy fst |> Seq.map (fun (id, xs) -> (id, xs |> Seq.map snd |> List.ofSeq)) |> Map.ofSeq
    for (subject, arr, k) in sites do
        match resolve k with
        | None -> decide subject (Blade.Effects.Declined "the key does not resolve to a callable of this module") [ llvmNote ]
        | Some cl ->
        let rangeIx = identityRangeIx arr
        let shared = Map.tryFind cl.Id byKey |> Option.defaultValue []
        let conflicting =
            shared |> List.exists (fun (_, a2, _) -> (identityRangeIx a2).IsSome <> rangeIx.IsSome)
        let identityLine =
            match rangeIx with
            | Some ix ->
                let over = defaultArg ix.Tag "an anonymous index"
                $"identity source: the operand is the plain range over {over}, its iota sorted in place (no index source, no permutation scratch, no gather)"
            | None -> "identity source: not a range operand (the general form: one permutation scratch, freed right after the gather)"
        if not enabled then
            decide subject (Blade.Effects.Declined "disabled by BLADE_SORT_FORM") [ identityLine; llvmNote ]
        elif conflicting then
            decide subject (Blade.Effects.Declined "the key callable is shared by a range and a non-range sort site") [ llvmNote ]
        else
        let keyCache : Result<string, string> =
            match cl.Params with
            | [ p ] when keyScalar cl.RetType ->
                if writes k then Error "the key may write (scheduleMayWriteIn)"
                else
                // a LEVEL only for an un-offset range: `lo..hi`'s values lo + i
                // are not positions of its own axis
                let levels =
                    match rangeIx, arr with
                    | Some ix, IRRange (_, None) -> [ (p, ix) ]
                    | _ -> []
                match IRTotality.auditCallable resolve (fun _ -> None) isDeferred cl levels with
                | Error why -> Error $"the key is not proved total: {why}"
                | Ok () ->
                    let lv = if rangeIx.IsSome then ", its parameter a level of the range" else ""
                    Ok ($"key cache: the key is closed-total (IRTotality{lv}) and write-free (scheduleMayWriteIn); each key evaluated once in ascending input order into a buffer of its own type, freed after the sort")
            | [ _ ] -> Error "the key's result is not a real, integer or Bool scalar"
            | _ -> Error "the key is not a one-parameter callable"
        // one key callable named by several sites: no cache (the table is keyed
        // by the callable; fail closed -- fable review, fix 3)
        let keyCache =
            match keyCache with
            | Ok _ when shared.Length > 1 -> Error "the key callable is shared by several sort sites (v1 caches a key one site names)"
            | r -> r
        let plan = { KeyId = cl.Id; Identity = rangeIx.IsSome; KeyCache = (match keyCache with Ok _ -> true | Error _ -> false) }
        let cacheLine = match keyCache with Ok l -> l | Error why -> $"key cache: declined -- {why} (the key is evaluated per comparison)"
        let order = "order: stable ascending under the total preorder (NaN keys first, all NaNs tie; -0.0 and +0.0 tie) -- unique, so every form yields the same permutation"
        if plan.Identity || plan.KeyCache then
            SortFormTable.record plan
            decide subject Blade.Effects.Applied [ identityLine; cacheLine; order; llvmNote ]
        else
            decide subject (Blade.Effects.Declined $"general form ({cacheLine.Substring(11)})") [ identityLine; order; llvmNote ]

// ===========================================================================
// THE SORT TERMINAL (round 4 wave 5, lane D item D5, gap G127 item a; plan
// rule `sort-terminal v1`, gate BLADE_SORT_TERMINAL). Under the private-spool
// grant (`external-sort v1`), a top-level `s = sort(A, key)` whose ONLY reader
// is one later top-level binding whose whole value is `fp = reduce(s, k[,
// seed])` never holds the sorted output: the sort feeds the fold in sorted
// order (the resident branch walks its permutation, the spool branch's merge
// hands each record to the fold as it emits it, positions ascending), from the
// same seed (unseeded: the first sorted cell), and fp binds the accumulator.
// The same cells, the same order, the same kernel applications. MOVED: the
// fold, from fp's site to s's (earlier): k is a closed allocation-free total
// scalar of its two parameters, so it cannot fail, allocate, or read anything
// an intervening statement changes. REMOVED: the n-cell output allocation
// (BLADE_SINK_STREAM precedent). Codex design review round 1 (BUILD WITH
// CHANGES): literal operand extents only (a dynamic range bound is evaluated
// again by the output's extents table), BLADE_FP_CONTRACT=off whenever the
// kernel holds any floating node, the unseeded first-cell initialization, an
// emission-local handshake.
// ===========================================================================

/// BLADE_SORT_TERMINAL=0|off keeps the sorted output stored. Unset = on.
let sortTerminalEnabled () = Blade.Toolchain.envFlag "BLADE_SORT_TERMINAL" true

type SortTerminalPlan = {
    SortId: IRId
    FoldId: IRId
    FoldName: string
    Kernel: IRExpr
    Seed: IRExpr option
    Evidence: string list
    /// The sort site's handshake: the accumulator's C++ name, published only
    /// when the terminal form was emitted (reset at every sort emission).
    Fed: string option ref
}

module SortTerminalTable =
    open System.Threading
    type private State = { BySort: Map<IRId, SortTerminalPlan>; ByFold: Map<IRId, SortTerminalPlan> }
    let private cell = new AsyncLocal<State>()
    let private get () = match box cell.Value with null -> { BySort = Map.empty; ByFold = Map.empty } | _ -> cell.Value
    let reset () = cell.Value <- { BySort = Map.empty; ByFold = Map.empty }
    let record (p: SortTerminalPlan) =
        let s = get ()
        cell.Value <- { BySort = Map.add p.SortId p s.BySort; ByFold = Map.add p.FoldId p s.ByFold }
    let trySort (id: IRId) : SortTerminalPlan option = Map.tryFind id (get ()).BySort
    let tryFold (id: IRId) : SortTerminalPlan option = Map.tryFind id (get ()).ByFold

let recordSortTerminal (subject: string) (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) =
    Blade.Effects.Decisions.record
        { Blade.Effects.Rule = "sort-terminal"; Version = 1
          Span = declSpanOf subject; Subject = subject
          Outcome = outcome; Evidence = evidence }

let planSortTerminal (programFuncs: IRCallable list) (foreignRefs: Set<IRId>) (singleModule: bool) (modul: IRModule) : unit =
    let sorts = modul.Bindings |> List.filter (fun b -> match b.Value with IRSort _ -> true | _ -> false)
    if sorts.IsEmpty then () else
    let funcs = (programFuncs @ modul.Functions) |> List.map (fun f -> (f.Id, f)) |> Map.ofList
    let resolve (k: IRExpr) = match k with IRVar (fid, _) -> Map.tryFind fid funcs | _ -> None
    let writes = Blade.Optimize.scheduleMayWriteIn programFuncs modul
    let allCallables = (modul.Functions @ programFuncs) |> List.distinctBy (fun f -> f.Id)
    let deferred = Blade.Optimize.programDeferredId modul allCallables Seq.empty
    let arr = List.toArray modul.Bindings
    let indexOf (id: IRId) = arr |> Array.tryFindIndex (fun b -> b.Id = id)
    let printSelection =
        match System.Environment.GetEnvironmentVariable "BLADE_PRINT" with
        | null -> None
        | v ->
            let names =
                v.Split([| ','; ' '; ';' |], System.StringSplitOptions.RemoveEmptyEntries)
                |> Array.map (fun s -> s.Trim()) |> Array.filter (fun s -> s <> "")
            if names.Length = 0 then None else Some (Set.ofArray names)
    let builtinOp (c: IRCallable) : IRBinOp option =
        match c.Params, c.Body with
        | [ p0; p1 ], IRBinOp (_, op, IRVar (a, _), IRVar (b, _), _)
            when (match op with IRAdd | IRMul -> true | _ -> false)
                 && ((a = p0.VarId && b = p1.VarId) || (a = p1.VarId && b = p0.VarId)) -> Some op
        | _ -> None
    let licensed (c: IRCallable) =
        let vetoed =
            c.IsRepro
            || (match c.Body with
                | IRApp (IRVar (fid, _), _, _) -> (match Map.tryFind fid funcs with Some g -> g.IsRepro | None -> false)
                | _ -> false)
        not vetoed && (c.IsCommutative || not c.CommGroups.IsEmpty || (builtinOp c).IsSome)
    let isFloat (t: IRType) = match stripUnits t with IRTScalar (ETFloat64 | ETFloat32) -> true | _ -> false
    let contractOff () =
        System.String.Equals(System.Environment.GetEnvironmentVariable "BLADE_FP_CONTRACT", "off", System.StringComparison.OrdinalIgnoreCase)
    for sb in sorts do
        match sb.Value with
        | IRSort (operand, _) ->
            // the operand's extent: a literal (a dynamic range bound would be
            // evaluated again by the output's extents table -- Codex round 1)
            let literalExtent =
                match operand with
                | IRRange ([ ix ], off) ->
                    (match ix.Extent, off with
                     | IRLit (IRLitInt n), (None | Some (IRLit (IRLitInt _))) -> Some n
                     | _ -> None)
                | IRVar (_, ty) ->
                    (match stripUnits ty with
                     | ArrayElem at when at.IndexTypes.Length = 1 ->
                         (match at.IndexTypes.Head.Extent with IRLit (IRLitInt n) -> Some n | _ -> None)
                     | _ -> None)
                | _ -> None
            let found = Blade.Optimize.referenceCensus allCallables modul sb.Id sb.Name
            let decision : Result<IRBinding * IRCallable * IRExpr option * string list, string> =
                match literalExtent with
                | None -> Error "the operand's extent is not a literal (a plain range with literal bounds or a stored rank-1 array of literal extent; a dynamic bound is evaluated again by the output's extents table)"
                | Some n ->
                match printSelection with
                | None -> Error $"`{sb.Name}` is printed (no print selection: printing reads the sorted output)"
                | Some sel when sel.Contains sb.Name -> Error $"`{sb.Name}` is printed (the print selection names it)"
                | Some _ when not singleModule -> Error "a multi-module program (v1 plans a single module)"
                | Some _ ->
                if foreignRefs.Contains sb.Id then Error $"another module names `{sb.Name}`"
                else
                match found with
                | [ (who, 1) ] ->
                    let fold =
                        arr |> Array.tryFind (fun b ->
                            who = $"`{b.Name}`"
                            && (match b.Value with IRReduce (IRVar (v, _), _, _) -> v = sb.Id | _ -> false))
                    (match fold with
                     | None -> Error $"`{sb.Name}`'s one reader ({who}) is not a top-level binding whose whole value is `reduce({sb.Name}, k[, seed])`"
                     | Some fb ->
                     match indexOf sb.Id, indexOf fb.Id with
                     | Some si, Some fi when si < fi ->
                        (match fb.Value with
                         | IRReduce (_, k, seed) ->
                            (match resolve k with
                             | Some kc when kc.Params.Length = 2 && kc.Captures.IsEmpty && not kc.IsOmpParallel && kc.Parallelism.IsEmpty && not kc.IsCudaKernel && not kc.IsMpiParallel ->
                                 let isP (x: IRExpr) =
                                     kc.Params |> List.exists (fun q ->
                                         match x with
                                         | IRVar (v, _) -> v = q.VarId
                                         | IRParam (_, i, _) -> i = q.Index
                                         | _ -> false)
                                 let anyFloat =
                                     let mutable hit = kc.Params |> List.exists (fun p -> isFloat p.Type) || isFloat kc.RetType
                                     iterIRExpr (fun e -> if (try isFloat (typeOf e) with _ -> false) then hit <- true) kc.Body
                                     hit
                                 let exactInt =
                                     (match stripUnits fb.Type with IRTScalar (ETInt64 | ETInt32) -> true | _ -> false)
                                     && (builtinOp kc).IsSome
                                 if not (Blade.Optimize.totalScalarOver kc.Params kc.Body && Blade.Optimize.allocFreeScalar isP kc.Body) then
                                     Error $"the fold kernel of `{fb.Name}` is not a closed allocation-free total scalar of its two parameters (it moves from `{fb.Name}` to `{sb.Name}`)"
                                 else
                                 match IRTotality.auditCallable resolve (fun _ -> None) deferred kc [] with
                                 | Error why -> Error $"the fold kernel of `{fb.Name}` is not proved total ({why})"
                                 | Ok () ->
                                 if not (exactInt || Blade.Toolchain.fpReproEnabled () || not (licensed kc)) then
                                     Error $"the fold `{fb.Name}` is LICENSED in this build (its stored form may take a reassociated tree, which the sorted chain does not reproduce); BLADE_FP_REPRO=1 or an unlicensed kernel keeps the serial chain"
                                 elif anyFloat && not (contractOff ()) then
                                     Error "the fold kernel holds floating arithmetic and BLADE_FP_CONTRACT is not off (g++ may contract it differently in the merge's context than in the stored fold's)"
                                 else
                                 let seedOk =
                                     match seed with
                                     | None when n >= 1L -> Ok "unseeded: the first sorted cell initializes the chain (n >= 1, a literal extent: no BL8003 moves)"
                                     | None -> Error "an unseeded fold over a possibly empty sort (its BL8003 would move from the fold to the sort)"
                                     | Some (IRLit (IRLitInt _ | IRLitFloat _ | IRLitFloat32 _ | IRLitBool _)) -> Ok "seeded by a literal"
                                     | Some _ -> Error "the fold seed is not a non-string literal"
                                 match seedOk with
                                 | Error why -> Error why
                                 | Ok seedLine ->
                                 let between = arr.[si + 1 .. fi - 1]
                                 match between |> Array.tryFind (fun b ->
                                         let ps = Blade.Optimize.bindingPayload modul b
                                         (ps @ (ps |> List.collect Blade.Optimize.embeddedExtentExprs) @ Blade.Optimize.irTypeExtentExprs b.Type) |> List.exists writes) with
                                 | Some b -> Error $"`{b.Name}` (between `{sb.Name}` and `{fb.Name}`) may write"
                                 | None ->
                                 let strictLine = if exactInt then "an exact integer builtin" elif Blade.Toolchain.fpReproEnabled () then "BLADE_FP_REPRO=1" else "an unlicensed kernel"
                                 let contractLine = if anyFloat then "BLADE_FP_CONTRACT=off" else "no floating node"
                                 Ok (fb, kc, seed,
                                     [ $"`{sb.Name}` is never stored: the sort feeds `{fb.Name}`'s fold in sorted order (resident branch: its permutation walk; spool branch: the merge hands each record to the fold as it emits it, positions ascending) -- the same cells, order and kernel applications as the stored fold; {seedLine}"
                                       $"census: `{sb.Name}` is named once, as `{fb.Name}`'s fold operand (every module root); not printed, single module"
                                       $"moved: the fold, from `{fb.Name}` to `{sb.Name}` -- its kernel is a closed allocation-free total scalar of its two parameters (no failure, no allocation, nothing an intervening statement changes); strict chain ({strictLine}); {contractLine}"
                                       "removed: the n-cell sorted output (a program that died BL8005 there may succeed or reach a later failure: the BLADE_SINK_STREAM precedent)"
                                       "realized only under the BLADE_PRIVATE_SPOOL grant (external-sort v1); the C++ emitter records the outcome"
                                       "llvm lane: no sort arm (the program falls back to the C++ lane)" ])
                             | _ -> Error $"the fold kernel of `{fb.Name}` is not a resolved, capture-free, serial two-parameter callable")
                         | _ -> Error "unreachable")
                     | _ -> Error $"`{fb.Name}` is not a later top-level binding")
                | [] -> Error $"nothing reads `{sb.Name}`"
                | xs -> Error ($"`{sb.Name}` has another reader than one whole-binding fold: " + (xs |> List.map (fun (w, c) -> $"{w} ({c})") |> String.concat ", "))
            match decision with
            | Error why -> recordSortTerminal sb.Name (Blade.Effects.Declined why) []
            | Ok (fb, _, seed, ev) ->
                if not (sortTerminalEnabled ()) then
                    recordSortTerminal sb.Name (Blade.Effects.Declined "disabled by BLADE_SORT_TERMINAL") ev
                else
                    match fb.Value with
                    | IRReduce (_, k, _) ->
                        SortTerminalTable.record { SortId = sb.Id; FoldId = fb.Id; FoldName = fb.Name; Kernel = k; Seed = seed; Evidence = ev; Fed = ref None }
                    | _ -> ()
        | _ -> ()
