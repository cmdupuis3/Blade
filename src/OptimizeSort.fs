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
