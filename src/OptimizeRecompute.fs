// The recompute selection, rule recompute, priced by the static cost model (split out of Optimize.fs, cleanup K19).
module Blade.OptimizeRecompute

open Blade.Types

open Blade.Ast
open Blade.IR
open Blade.IRMono
open Blade.OptimizeGates
open Blade.OptimizeAnalysis
open Blade.OptimizeStorage
open Blade.OptimizeComputeElision

// ===========================================================================
// RECOMPUTE SELECTION (BLADE_RECOMPUTE, plan rule `recompute v2` -- v2 = cost model v2's bandwidth weight, gap G96;
// docs/plans/plan-identity-transpose-compute.md section 6 P5, the first
// bounded SELECTION by the static cost model, src/CostModel.fs).
//
// Compute elision takes a computed intermediate with ONE consumer; this pass
// takes one with TWO TO EIGHT (PM05, the forked producer DAG: round 4 wave 3
// widened the family from exactly two), where eliding the pool means
// evaluating the producer once PER consumer. Rule 7: removing the buffer is
// never assumed cheaper, so the two candidates are PRICED and the model
// chooses -- with k consumers the recompute candidate pays the producer's
// work k - 1 more times, so a wider fan-out needs a cheaper producer:
//
//   family {MATERIALIZE (today: x written once, read by each consumer),
//           RECOMPUTE (each consumer reads x's map in place)}
//   recompute iff  work(R) - work(M) <= traffic(M) - traffic(R)
//             and  peak(R) <= peak(M), both proved; a tie keeps M.
//
// Optimality is claimed ONLY for this two-candidate family under model v2
// (materialize once, or recompute for EVERY consumer; a split -- recompute
// for some consumers, read the pool in others -- is not in the family).
//
// Admission (fail closed, each a recorded decline), inside a function,
// lambda or block body: `let x = <map> |> compute` read by 2..8
// statements of its own spine, each the WHOLE value of its statement and
// each reading x once per cell -- a fold `reduce(x, k[, s])` (x not in the
// kernel or seed) or a pointwise map / co-iteration `method_for(zip(.., x,
// ..)) <@> g |> compute` whose kernel reads x's cell once (compute elision's
// map-consumer conditions: fusion, its realization, must be able to splice);
// x not captured, assigned, or read by a type's extent; a CHEAP producer: a
// plain dense total (IRTotality) write-free map over STORED names or ranges,
// plain extents, equal literal co-iteration extents, priced by the model at
// <= CostModel.cheapProducerOps ops per cell with no division or
// transcendental; no write or provider effect from the let through the LAST
// consumer (`scheduleMayWriteIn`, every statement between, both consumer
// statements and the extents their types carry); each fold's seed by
// `elidedFoldSeed` (compute elision's rule: an unseeded builtin fold only over
// a literal extent >= 1, so an empty input keeps the pool and BL8003 at the
// FIRST consumer with today's text; the seed by W4-3's route rule). The
// commitments of compute elision follow: SNAPSHOT (the interval audit),
// FAILURE SITE (a total producer), ONCE-ONLY EFFECTS (a write-free producer;
// evaluating a pure total map twice is unobservable), EVALUATION COUNT (each
// consumer reads each cell once).
//
// A TREE REWRITE before the lane split, as compute elision is (rule 8's side
// table not built): the interpreter runs the IR left, `blade test opt-diff`
// (the gate off vs on) is the differential. A map consumer's outcome is
// recorded after elementwise fusion ran (applied iff both hosts spliced).
// ===========================================================================

/// BLADE_RECOMPUTE=0|off keeps every multi-consumer computed intermediate
/// materialized; the admission and the model still run and a recompute it
/// would have chosen records `recompute` DECLINED ("disabled by
/// BLADE_RECOMPUTE") with both costs. Unset = on. Read per call.
let recomputeEnabled () = gateOn "BLADE_RECOMPUTE"

/// The widest fan-out the recompute family prices (PM05: fan-outs 2 / 4 / 8).
/// Past it a computed intermediate keeps its pool, recorded.
let maxRecomputeConsumers = 8

let private recordRecompute (subject: string) (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) =
    Blade.Effects.Decisions.record
        { Blade.Effects.Rule = "recompute"; Version = 2; Span = Blade.Ast.noSpan
          Subject = subject; Outcome = outcome; Evidence = evidence }

/// A recompute with map consumers, recorded once elementwise fusion has run.
type RecomputePending = { RcSubject: string; RcProducer: ApplyInfo; RcEvidence: string list }

let selectRecomputeModule (programFuncs: IRCallable list) (modul: IRModule) : IRModule * RecomputePending list =
    if modul.Functions |> List.exists (fun f -> f.IsCudaKernel) then (modul, []) else
    let pending = ResizeArray<RecomputePending>()
    let enabled = recomputeEnabled ()
    let own = modul.Functions |> List.map (fun f -> (f.Id, f)) |> Map.ofList
    let funcs = programFuncs |> List.fold (fun m f -> if Map.containsKey f.Id m then m else Map.add f.Id f m) own
    let localCallables = System.Collections.Generic.Dictionary<IRId, IRCallable>()
    for f in modul.Functions do localCallables.[f.Id] <- f
    let resolver = System.Collections.Generic.Dictionary<IRId, IRCallable>()
    for f in programFuncs do resolver.[f.Id] <- f
    for f in modul.Functions do resolver.[f.Id] <- f
    let resolve (k: IRExpr) = resolveCallableIn funcs k
    let mayWrite = scheduleMayWriteIn programFuncs modul
    let payloadOf = intervalPayloadOf modul
    let streamedIds = streamedProviderIds modul
    // ---- the module census (as compute elision's) ---------------------------
    let assigned = System.Collections.Generic.HashSet<IRId>()
    let deferredIds = System.Collections.Generic.HashSet<IRId>()
    // the census widened by the program's deferred-value classifier (gap G98
    // class audit) for the totality audits below
    let deferredW = widenDeferred modul (modul.Functions @ programFuncs) deferredIds.Contains
    let rec rootVar (t: IRExpr) =
        match t with
        | IRIndex (b, _, _) | IRFieldAccess (b, _) | IRTupleProj (b, _, _) -> rootVar b
        | IRVar (id, _) -> Some id
        | _ -> None
    let census (e: IRExpr) =
        iterIRExpr (fun n ->
            match n with
            | IRAssign (t, _) -> (match rootVar t with Some id -> assigned.Add id |> ignore | None -> ())
            | IRLet (id, v, _) when isDeferredForm v -> deferredIds.Add id |> ignore
            | _ -> ()) e
    for f in modul.Functions do census f.Body
    for b in modul.Bindings do
        for p in bindingPayload modul b do census p
    let captured =
        funcs |> Map.fold (fun acc _ f -> f.Captures |> List.fold (fun a c -> Set.add c.Id a) acc) Set.empty
    let refsOf (x: IRId) (e: IRExpr) =
        let mutable n = 0
        iterIRExpr (fun m -> match m with IRVar (id, _) when id = x -> n <- n + 1 | _ -> ()) e
        n
    let isX (x: IRId) (e: IRExpr) = match e with IRVar (id, _) -> id = x | _ -> false
    /// The leaves a fused nest reads for one operand: a computed (or
    /// deferred) map operand is spliced, so its own operands count.
    let rec fusedLeaves (a: IRExpr) : int =
        match a with
        | IRCompute (IRApplyCombinator q) | IRApplyCombinator q -> q.Arrays |> List.sumBy fusedLeaves
        | _ -> 1
    let rec plainExtent (e: IRExpr) : bool =
        match e with
        | IRLit _ -> true
        | IRVar (id, ty) -> not (Map.containsKey id funcs) && not (isFunctionTyped ty) && not (deferredIds.Contains id)
        | IRParam (_, _, ty) -> not (isFunctionTyped ty)
        | IRExtent (IRVar (id, _), _) -> not (deferredIds.Contains id)
        | IRExtent (IRParam (_, _, ty), _) -> not (isFunctionTyped ty)
        | IRBinOp (_, (IRAdd | IRSub | IRMul), a, b, _) -> plainExtent a && plainExtent b
        | IRUnaryOp (IRNeg, a) -> plainExtent a
        | _ -> false
    let staticExtents (at: IRArrayType) =
        at.IndexTypes |> List.map (fun ix -> match ix.Extent with IRLit (IRLitInt n) -> Some n | _ -> None)
    let costNames = System.Collections.Generic.Dictionary<IRId, string>()
    for b in modul.Bindings do costNames.[b.Id] <- b.Name
    for KeyValue (_, f) in funcs do
        for pp in f.Params do
            if not (costNames.ContainsKey pp.VarId) then costNames.[pp.VarId] <- pp.Name
    let costNameOf (id: IRId) = match costNames.TryGetValue id with | true, n -> n | _ -> $"__v{id}"
    let nameOfLet (xid: IRId) (p: ApplyInfo) =
        match resolve p.Kernel with
        | Some c when c.SourceName.StartsWith "lambda in " && c.SourceName.Length > 10 -> c.SourceName.Substring 10
        | _ -> costNameOf xid
    let legality =
        Blade.CostModel.legalityLine "recompute per consumer (snapshot: no write or provider effect from the let through the last consumer; failure site: an IRTotality-total producer over plain extents; once-only effects: a write-free producer, so a second evaluation is unobservable; evaluation count: each consumer reads each cell once; empty input: each fold's seed by elidedFoldSeed, an unseeded fold only over a literal extent >= 1)"
    let regionRepro = ref false

    let rec descend (subject: string) (root: IRExpr) (e: IRExpr) : IRExpr =
        match e with
        | IRLet _ -> processSpine subject root e
        | IRIf (c, t, f) -> IRIf (c, descend subject root t, descend subject root f)
        | IRMatch (s, cases) -> IRMatch (s, cases |> List.map (fun c -> { c with Body = descend subject root c.Body }))
        | _ -> e
    and processSpine (subject: string) (root: IRExpr) (e: IRExpr) : IRExpr =
        let rec flatten acc e =
            match e with
            | IRLet (id, v, b) -> flatten ((id, v) :: acc) b
            | t -> (List.rev acc, t)
        let (letList, tail0) = flatten [] e
        let lets = letList |> List.map (fun (id, v) -> (id, descend subject root v)) |> Array.ofList
        let mutable tail = descend subject root tail0
        let dropped = System.Collections.Generic.HashSet<int>()
        for i in 0 .. lets.Length - 1 do
            let (xid, xv) = lets.[i]
            match xv with
            | IRCompute (IRApplyCombinator p) when refsOf xid root >= 2 ->
                let xn = nameOfLet xid p
                let subj = $"{subject}.{xn}"
                let decline (why: string) (ev: string list) = recordRecompute subj (Blade.Effects.Declined why) ev
                let total = refsOf xid root
                // the statements of x's spine after it that read it (a map
                // consumer names x twice: its operand and its loop's copy)
                let later = [ for j in i + 1 .. lets.Length - 1 -> (Some j, snd lets.[j]) ] @ [ (None, tail) ]
                let readers = later |> List.filter (fun (_, v) -> refsOf xid v > 0)
                let here = readers |> List.sumBy (fun (_, v) -> refsOf xid v)
                // one reading statement is compute elision's domain: silent
                if readers.Length < 2 then ()
                elif Set.contains xid captured then decline $"`{xn}` is captured by a kernel or closure" []
                elif assigned.Contains xid then decline $"`{xn}` is assigned" []
                elif embeddedExtentExprs root |> List.sumBy (refsOf xid) > 0 then
                    decline $"`{xn}` is read by an extent expression a type carries" []
                elif here <> total then
                    decline $"`{xn}` is also read outside the statements of its own block (a capture or a nested region)" []
                elif readers.Length > maxRecomputeConsumers then
                    decline $"`{xn}` is read by {readers.Length} statements (the selection takes at most {maxRecomputeConsumers} consumers; more keep the pool)" []
                else
                // ---- the consumers -----------------------------------------
                let classify (sv: IRExpr) : Result<Choice<IRExpr * IRExpr option, ApplyInfo>, string> =
                    match sv with
                    | ReduceOfNamed (y, _, k, init) when y = xid ->
                        if refsOf xid k + (init |> Option.map (refsOf xid) |> Option.defaultValue 0) > 0 then
                            Error $"`{xn}` is read again by a fold's kernel or seed"
                        else Ok (Choice1Of2 (k, init))
                    | IRCompute (IRApplyCombinator h)
                        when (h.Arrays |> List.filter (isX xid) |> List.length) = 1
                             && (h.Arrays |> List.sumBy (refsOf xid)) = 1 && refsOf xid h.Kernel = 0 -> Ok (Choice2Of2 h)
                    | _ -> Error $"a reader of `{xn}` is not a fold or a pointwise map that is the whole value of its statement"
                let kinds = readers |> List.map (fun (at, sv) -> (at, classify sv))
                match kinds |> List.tryPick (fun (_, r) -> match r with Error why -> Some why | Ok _ -> None) with
                | Some why -> decline why []
                | None ->
                let kinds = kinds |> List.map (fun (at, r) -> match r with Ok c -> (at, c) | Error _ -> failwith "unreachable")
                // ---- the producer ------------------------------------------
                let stored (e: IRExpr) = match e with IRVar (id, _) when id = xid -> Some xn | _ -> Blade.CostModel.storedArrayName costNameOf e
                let producerProblem =
                    match resolve p.Kernel with
                    | None -> Some "the producer kernel does not resolve to a callable"
                    | Some pkc ->
                        if not (tilePlainInfo p) then Some "the producer is not a plain dense map or co-iteration"
                        elif not (tilePlainKernel pkc p.Arrays.Length) then Some "the producer kernel declares symmetry, a parallel strategy or arity polymorphism"
                        elif p.Arrays |> List.exists (function IRVar (id, _) -> streamedIds.Contains id | _ -> false) then Some streamedOperandWhy
                        elif not (p.Arrays |> List.forall (function IRVar (id, _) -> not (deferredIds.Contains id) | IRRange _ -> true | _ -> false)) then
                            Some "an operand of the producer is not a stored name or a range"
                        elif not (embeddedExtentExprs (IRApplyCombinator p) |> List.forall plainExtent) then
                            Some "an extent of the producer is not a literal, a scalar name or an extent read"
                        else
                        match Blade.IRTotality.auditApply resolve (fun _ -> None) deferredW p with
                        | Error why -> Some $"the producer kernel is not proved total ({why})"
                        | Ok () ->
                        if mayWrite (IRApplyCombinator p) then Some "the producer may write (a writing producer is evaluated once, at its definition: G45)"
                        elif not (tilePureBody resolver Set.empty pkc.Body) then Some "the producer kernel is not repeatable"
                        elif p.Arrays.Length > 1
                             && (let exts = p.ArrayTypes |> List.map staticExtents
                                 exts |> List.exists (List.exists Option.isNone) || (exts |> List.distinct |> List.length) > 1) then
                            Some "a co-iteration producer over run-time or unequal extents"
                        else
                        match Blade.CostModel.mapPerCell resolve stored p with
                        | Error why -> Some $"the model cannot price the producer ({why})"
                        | Ok pc when not pc.CellOps.Heavy.IsEmpty ->
                            let heavy = String.concat ", " pc.CellOps.Heavy
                            Some $"the producer is not cheap: {Blade.CostModel.opsText pc.CellOps} ({heavy}; a division or transcendental is never recomputed in v1)"
                        | Ok pc when pc.CellOps.Count > Blade.CostModel.cheapProducerOps ->
                            Some $"the producer is not cheap: {Blade.CostModel.opsText pc.CellOps} (cap {Blade.CostModel.cheapProducerOps} ops/cell)"
                        | Ok _ -> None
                match producerProblem with
                | Some why -> decline why []
                | None ->
                // ---- the interval: nothing may write through the last consumer
                let lastIdx = match fst (List.last readers) with Some j -> j | None -> lets.Length
                let interval =
                    [ for j in i + 1 .. lastIdx do
                        if j < lets.Length then yield (Some (fst lets.[j]), payloadOf (fst lets.[j]) (snd lets.[j]))
                        else yield (None, tail) ]
                let writer =
                    interval |> List.exists (fun (idOpt, v) ->
                        let prov = match idOpt with Some id -> intervalHasProviderEffect modul id | None -> false
                        prov || mayWrite v || (embeddedExtentExprs v |> List.exists mayWrite))
                if writer then decline "a statement from the compute through its last consumer may write (a recomputed producer would read the inputs after the write)" []
                else
                // ---- each consumer's own conditions ----------------------------
                let repl = IRCompute (IRApplyCombinator p)
                let subst (e: IRExpr) = mapIRExpr (fun n -> match n with IRVar (y, _) when y = xid -> repl | n -> n) e
                let prepared =
                    kinds |> List.map (fun (at, c) ->
                        match c with
                        | Choice1Of2 (k, init) ->
                            (match elidedFoldSeed regionRepro.Value p.OutputType (resolve k) init with
                             | Error why -> Error why
                             | Ok (seed, note) ->
                                 Ok (at, IRReduceCompute (IRApplyCombinator p, k, seed), Blade.CostModel.RcFold (resolve k), note, None))
                        | Choice2Of2 h ->
                            let pos = h.Arrays |> List.findIndex (isX xid)
                            let problem =
                                match tileKernelOf localCallables h with
                                | _ when not (Blade.Toolchain.envFlag "BLADE_FUSION" true) ->
                                    Some "elementwise fusion, a map consumer's realization, is disabled (BLADE_FUSION)"
                                | None -> Some "a consumer's kernel is not a resolvable callable"
                                | Some hk ->
                                    if not (tilePlainInfo h) then Some "a consumer is not a plain dense map or co-iteration"
                                    elif not (tilePlainKernel hk h.Arrays.Length) then Some "a consumer kernel declares symmetry, a parallel strategy or arity polymorphism"
                                    elif not (tilePureBody resolver Set.empty hk.Body) then Some "a consumer kernel is not repeatable"
                                    elif refsOf hk.Params.[pos].VarId hk.Body <> 1 then Some $"a consumer kernel reads `{xn}`'s cell {refsOf hk.Params.[pos].VarId hk.Body} times (exactly once is required)"
                                    // fusion splices a computed operand's own leaves too:
                                    // count every leaf the fused nest would read (Codex)
                                    elif (h.Arrays |> List.sumBy (fun a -> if isX xid a then 0 else fusedLeaves a)) + p.Arrays.Length > 8 then
                                        Some "the fused nest's leaves (the consumer's other operands, expanded, plus the producer's) would exceed elementwise fusion's fan-in cap (8)"
                                    elif h.SharedIndexTypes.IsEmpty && h.Arrays.Length + p.Arrays.Length - 1 > 1 && p.SharedIndexTypes.IsEmpty then
                                        Some "a fused node would read as an outer product (no shared index records)"
                                    else None
                            match problem with
                            | Some why -> Error why
                            | None ->
                                let h' = { h with Arrays = h.Arrays |> List.map subst; Loop = subst h.Loop }
                                let out = match at with Some j -> costNameOf (fst lets.[j]) | None -> "the returned value"
                                let hk = match h.Kernel with IRVar (kid, _) -> Some kid | _ -> None
                                Ok (at, IRCompute (IRApplyCombinator h'), Blade.CostModel.RcMap (h, h', out), "a pointwise map consumer, realized by elementwise fusion (no pool for it)", hk))
                match prepared |> List.tryPick (function Error why -> Some why | Ok _ -> None) with
                | Some why -> decline why []
                | None ->
                let prepared = prepared |> List.choose (function Ok r -> Some r | Error _ -> None)
                // ---- the model chooses -----------------------------------------
                let consumers = prepared |> List.map (fun (_, _, c, _, _) -> c)
                match Blade.CostModel.recomputeSelection resolve costNameOf stored xn p consumers with
                | Error why -> decline $"the model cannot price the candidates ({why}); the baseline is kept" [ Blade.CostModel.notPriced why ]
                | Ok verdict ->
                let notes = prepared |> List.map (fun (_, _, _, note, _) -> note) |> List.distinct
                let ev =
                    [ $"{readers.Length} consumers of `{xn}`, each the whole value of its statement, each reading every cell once" ]
                    @ notes
                    @ [ "producer cheap, total (IRTotality whitelist) and write-free, over stored names or ranges"
                        "inputs unwritten from the compute through the last consumer (the shared write analysis)"
                        verdict.Selection
                        legality
                        verdict.CostLine ]
                if not verdict.Recompute then
                    decline ("materialized -- " + verdict.Reason) ev
                elif not enabled then decline "disabled by BLADE_RECOMPUTE" ev
                else
                    dropped.Add i |> ignore
                    for (at, v', _, _, _) in prepared do
                        match at with
                        | Some j -> lets.[j] <- (fst lets.[j], v')
                        | None -> tail <- v'
                    let hasMap = prepared |> List.exists (fun (_, _, _, _, hk) -> hk.IsSome)
                    if not hasMap then recordRecompute subj Blade.Effects.Applied ev
                    else pending.Add { RcSubject = subj; RcProducer = p; RcEvidence = ev }
            | _ -> ()
        let kept = lets |> Array.indexed |> Array.filter (fun (i, _) -> not (dropped.Contains i)) |> Array.map snd
        Array.foldBack (fun (id, v) acc -> IRLet (id, v, acc)) kept tail

    let functions' =
        modul.Functions |> List.map (fun f ->
            if f.IsStatic || f.IsCudaKernel || f.IsMpiParallel || f.IsArityPoly || hasTypeVarsInSignature f then f
            else
                let subject = if System.String.IsNullOrEmpty f.SourceName then f.Name else f.SourceName
                regionRepro.Value <- f.IsRepro
                let body' = descend subject f.Body f.Body
                regionRepro.Value <- false
                { f with Body = body' })
    let bindings' =
        modul.Bindings |> List.map (fun b ->
            match b.Value with
            | IRLet _ | IRIf _ | IRMatch _ -> { b with Value = descend b.Name b.Value b.Value }
            | _ -> b)
    ({ modul with Functions = functions'; Bindings = bindings' }, List.ofSeq pending)

/// The recompute decisions with map consumers, once elementwise fusion ran:
/// applied iff x's producer map survives NOWHERE as a computed operand of
/// an apply (keyed on the producer itself, not the host kernel's id, which a
/// partial fusion replaces: Codex review).
let recordRecomputeMapOutcomes (pending: RecomputePending list) (modul: IRModule) : unit =
    if not pending.IsEmpty then
        let survivors = ResizeArray<ApplyInfo>()
        let scan (e: IRExpr) =
            iterIRExpr (fun n ->
                match n with
                | IRApplyCombinator info ->
                    for a in info.Arrays do
                        match a with
                        | IRCompute (IRApplyCombinator q) -> survivors.Add q
                        | _ -> ()
                | _ -> ()) e
        for f in modul.Functions do scan f.Body
        for b in modul.Bindings do scan b.Value
        let survives (p: ApplyInfo) = survivors |> Seq.exists (fun q -> q.Kernel = p.Kernel && q.Arrays = p.Arrays)
        for p in pending do
            if survives p.RcProducer then
                let evidence =
                    p.RcEvidence |> List.collect (fun e ->
                        if e.StartsWith "cost: " then [ "cost: not realized (fusion declined: the producer is built as a consumer's operand; the planned line follows)"; "planned " + e ]
                        else [ e ])
                recordRecompute p.RcSubject
                    (Blade.Effects.Declined "elementwise fusion did not splice the producer into a map consumer (its own decline is recorded); that consumer builds the producer as its operand")
                    evidence
            else recordRecompute p.RcSubject Blade.Effects.Applied p.RcEvidence
