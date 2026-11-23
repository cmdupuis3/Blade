// Compact row classes: the orbit fold's class sums behind each packed row, rule compact-row-classes (split out of Optimize.fs, cleanup K19).
module Blade.OptimizeCompactRows

open Blade.Types

open Blade.Ast
open Blade.IR
open Blade.IRMono
open Blade.OptimizeGates
open Blade.OptimizeAnalysis
open Blade.OptimizeStorage

// --- Compact row classes: the orbit fold's class sums behind each row (B3) --
//
// The pairkernel shape: a compact rank-2 producer (SymIdx<2, n> inclusive or
// AntisymIdx<2, n> strict, the plain compact nest writing one packed row per
// outer iteration) followed by a logical walk the orbit-class fold
// (Blade.OrbitFold) takes. Today the orbit fold re-reads the whole packed pool
// in a second pass (threaded, under a fold `omp`, by its own team with a
// boustrophedon row deal). Scheduled here, each packed row's CLASS partials --
// symmetric: d[i] = f(K(i, i)) (weight 1) and o[i] = sum_{j>i} f(K(i, j))
// (weight 2); strict: o[i] = sum f(K(i, j)), plus o-[i] = sum f(-K(i, j)) for
// a kernel of unknown parity -- are computed right after the row is stored,
// FROM ZERO in a fixed per-row scheme, into rank-1 arrays; the consumer sums
// each array in ascending row order and applies the orbit plan's existing
// addition-only `Total` combine and outer seed.
//
// LICENSED ONLY. It is one more association of the licensed `+` folds over
// the same multiset (each class split into disjoint per-row subsets, combined
// in row order) -- OrbitFold.fs's header argument, unchanged -- so it is taken
// exactly where the orbit fold itself is (every level a licensed builtin `+`,
// inner seeds +0, not under BLADE_FP_REPRO=1). Bits change relative to the
// per-thread orbit tree and become a function of the data alone: no
// accumulator is carried across rows, so team size and schedule do not enter.
//
// This planner records only IR-level facts (it compiles before OrbitFold):
// the producer, the consumer and the consumer's fold. The orbit plan is
// resolved by each emitter at the producer (with the same recogniser and the
// same inputs the consumer's orbit dispatch uses); a lane that cannot place
// the epilogue publishes nothing and the consumer keeps the orbit pass.

/// One scheduled compact producer / orbit-fold consumer pair.
type CompactRowPlan = {
    ProducerId: IRId
    ProducerName: string
    ConsumerId: IRId
    ConsumerName: string
    /// The consumer's `IRReduceCompute (comp, kernel, seed)` -- the input the
    /// emitters hand OrbitFold.recognise at the producer.
    ConsumerComp: IRExpr
    ConsumerKernel: IRExpr
    ConsumerSeed: IRExpr
    /// The bindings that DOMINATE the producer (bound before it in its own
    /// chain, the enclosing function's parameters, the module bindings ahead
    /// of the enclosing top-level binding). The epilogue evaluates the cell
    /// body at the producer, so every free variable of that body must be one
    /// of these (`CompactRowTable.bodyDominated`, checked at emission).
    DominatingIds: Set<IRId>
    /// The C++ lane's handshake: the per-row class arrays' names (accumulator
    /// order), written when the nest placed the epilogue; read by the consumer.
    EmittedCpp: string list option ref
    /// The LLVM lane's twin: the class pools' pointer registers.
    EmittedLlvm: string list option ref
    /// Set by the C++ producer side when it recorded its own decline, so the
    /// consumer does not add a second, vaguer one.
    DeclinedCpp: bool ref
}

module CompactRowTable =
    open System.Threading
    type State = { ByProducer: Map<IRId, CompactRowPlan>; ByConsumer: Map<IRId, CompactRowPlan> }
    let private empty = { ByProducer = Map.empty; ByConsumer = Map.empty }
    let private cell = new AsyncLocal<State>()
    let private get () = match box cell.Value with null -> empty | _ -> cell.Value
    let reset () = cell.Value <- empty
    let record (p: CompactRowPlan) =
        let s = get ()
        cell.Value <- { ByProducer = Map.add p.ProducerId p s.ByProducer
                        ByConsumer = Map.add p.ConsumerId p s.ByConsumer }
    let tryProducer (id: IRId) : CompactRowPlan option = Map.tryFind id (get ()).ByProducer
    let tryConsumer (id: IRId) : CompactRowPlan option = Map.tryFind id (get ()).ByConsumer

    /// May evaluating the orbit plan's cell body ABORT? (integer `/` `%` `^`,
    /// a cast to an integer type, a domain-checked intrinsic, a call, a read.)
    /// Moving the body behind each produced row would reorder such an abort
    /// against the producer's later rows, so a body that may fail declines.
    let bodyMayFail (body: IRExpr) : bool =
        let intTy (t: IRType) =
            match stripUnits t with
            | IRTScalar (ETInt64 | ETInt32) -> true
            | _ -> false
        let mutable fails = false
        iterIRExpr (fun n ->
            match n with
            | IRBinOp (_, (IRDiv | IRMod | IRCaret), l, r, _) when intTy (typeOf l) || intTy (typeOf r) -> fails <- true
            | IRUnaryOp (IRCast (et, _), _) when (match et with ETInt64 | ETInt32 -> true | _ -> false) -> fails <- true
            | IRUnaryOp (IRMath m, _) when m.Contains "gamma" -> fails <- true
            | IRIndex _ | IRApp _ | IRMatch _ | IRRandAt _ -> fails <- true
            | _ -> ()) body
        fails

    /// Does every FREE variable of the resolved cell body (the read variable
    /// `readVarId` and the body's own let binders excluded) dominate the
    /// producer? The epilogue evaluates the body right after each packed row,
    /// so a scalar bound between the producer and the fold -- or anything the
    /// plan cannot place -- declines.
    let bodyDominated (plan: CompactRowPlan) (readVarId: IRId) (body: IRExpr) : bool =
        let binders = System.Collections.Generic.HashSet<IRId>()
        let used = System.Collections.Generic.HashSet<IRId>()
        iterIRExpr (fun n ->
            match n with
            | IRLet (id, _, _) -> binders.Add id |> ignore
            | IRVar (id, _) -> used.Add id |> ignore
            | _ -> ()) body
        used |> Seq.forall (fun id -> id = readVarId || binders.Contains id || plan.DominatingIds.Contains id)

/// Plan the compact row-class schedule: function-body let chains, the
/// top-level binding sequence, and the let chain nested inside a top-level
/// binding's value (pairkernel's `let (checksum, ..) = { let kmat ..; let ss .. }`).
/// Records into `CompactRowTable` and the decision collector
/// (`compact-row-classes`, its declines; the emitter records the outcome).
let planCompactRowClasses (programFuncs: IRCallable list) (modul: IRModule) : unit =
    let funcs = modul.Functions |> List.map (fun f -> (f.Id, f)) |> Map.ofList
    let facts = cseFactsOf funcs
    let writes = scheduleMayWriteIn programFuncs modul
    let lifetimeBarrier = scheduleHasLifetimeBarrierIn programFuncs modul
    // An intervening statement is audited by its complete payload (finding 7).
    let payloadOf = intervalPayloadOf modul
    let providerEffect = intervalHasProviderEffect modul
    let decide (subject: string) (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) =
        Blade.Effects.Decisions.record
            { Blade.Effects.Rule = "compact-row-classes"; Version = 1
              Span = declSpanOf subject; Subject = subject
              Outcome = outcome; Evidence = evidence }
    let refs (e: IRExpr) = collectVarRefsIR e
    // Does `e` read `pId`, directly or through a callable it names (a lifted
    // kernel lambda reads its array by id inside its own body)?
    let readsTransitively (pId: IRId) (e: IRExpr) =
        let visited = System.Collections.Generic.HashSet<IRId>()
        let rec go (e: IRExpr) =
            let r = refs e
            Set.contains pId r
            || (r |> Set.exists (fun id ->
                    match Map.tryFind id funcs with
                    | Some cl when visited.Add id -> go cl.Body
                    | _ -> false))
        go e
    let compactRank2 (info: ApplyInfo) =
        match stripUnits info.OutputType, resolveCallableIn funcs (fst (peelReynolds info.Kernel)) with
        | ArrayElem at, Some pk
            when not at.IsVirtual && info.KernelOutputRank = 0
                 && not pk.IsCudaKernel && not pk.IsMpiParallel ->
            (match at.IndexTypes with
             | [ ix ] when ix.Rank = 2 && (ix.Symmetry = SymSymmetric || ix.Symmetry = SymAntisymmetric) ->
                 Some (ix, info.HasReynolds)
             | _ -> None)
        | _ -> None
    // `outer`: the ids that dominate the whole chain (the enclosing function's
    // parameters, or the module bindings ahead of the enclosing binding).
    let planChain (outer: Set<IRId>) (items: (IRId * string * IRExpr) list) =
        let arr = List.toArray items
        for pi in 0 .. arr.Length - 1 do
            let (pId, pName, pVal) = arr.[pi]
            match pVal with
            | IRCompute (IRApplyCombinator pinfo) ->
                match compactRank2 pinfo with
                | None -> ()
                | Some (_, hasReynolds) ->
                    let consumer =
                        [ pi + 1 .. arr.Length - 1 ] |> List.tryPick (fun ci ->
                            let (cId, cName, cVal) = arr.[ci]
                            match cVal with
                            | IRReduceCompute ((IRApplyCombinator oinfo as comp), k, seed) ->
                                (match oinfo.Arrays, resolveCallableIn funcs oinfo.Kernel with
                                 | [ IRRange ([ _ ], None) ], Some ocl
                                     when ocl.Params.Length = 1 && readsTransitively pId ocl.Body ->
                                     Some (ci, cId, cName, comp, k, seed)
                                 | _ -> None)
                            | _ -> None)
                    match consumer with
                    | None -> ()
                    | Some (ci, cId, cName, comp, k, seed) ->
                        let what = $"orbit fold of `{pName}`"
                        let declined (why: string) = decide cName (Blade.Effects.Declined why) [ what ]
                        let between = [ pi + 1 .. ci - 1 ] |> List.map (fun i -> arr.[i])
                        let offending =
                            between |> List.tryPick (fun (iId, iName, iVal0) ->
                                // the statement's complete payload (W1-4 review finding 7)
                                let iVal = payloadOf iId iVal0
                                if lifetimeBarrier iVal then Some $"`{iName}` is an inline lifetime boundary between the producer and the fold"
                                elif readsTransitively pId iVal then Some $"`{iName}` reads `{pName}` between the producer and the fold"
                                elif (facts iVal).MayWrite || writes iVal || providerEffect iId then Some $"`{iName}` may write (an assignment, a mutating call or a provider write -- its side-table payload included) between the producer and the fold"
                                else None)
                        let (_, _, cVal) = arr.[ci]
                        if hasReynolds then declined "a Reynolds kernel (v1 schedules the plain compact nest only)"
                        // The consumer's OWN evaluation (its outer seed, the
                        // walk) runs after the producer's rows are summed: a
                        // write there could change the cells first.
                        elif (facts cVal).MayWrite then
                            declined "the fold itself may write (its seed or walk calls something that assigns or mutates), so the producer's rows could change before the fold reads them"
                        // G41 (Codex finding 16): the class partials run behind
                        // each packed row, so a PRODUCER that writes (a scalar
                        // the walk body reads, say) would change what later
                        // rows' partials see. Conservative: any producer write.
                        elif (facts pinfo.Kernel).MayWrite || writes (IRApplyCombinator pinfo) then
                            declined "the producer's kernel may write: the class partials would run between its rows and read across its later writes"
                        elif writes comp then
                            declined "the walk (consumer) kernel may write"
                        elif writes k then
                            declined "the outer fold kernel may write"
                        elif writes seed then
                            declined "the fold's seed may write"
                        else
                        match offending with
                        | Some why -> declined why
                        | None ->
                            let evidence =
                                [ $"`{cName}` = {what}: each packed row's class partials (diagonal / off-diagonal; strict: the orbit class, and its mirror for a kernel of unknown parity) are computed from zero right after the row is stored, into rank-1 arrays; the consumer sums each in ascending row order and applies the orbit plan's addition-only combine"
                                  "licensed regrouping (taken only where the orbit-class fold is: every level a licensed builtin `+`, not under BLADE_FP_REPRO=1); bits independent of team size" ]
                            if not (compactRowClassesEnabled ()) then
                                decide cName (Blade.Effects.Declined "disabled by BLADE_COMPACT_ROW_CLASSES") evidence
                            else
                                CompactRowTable.record
                                    { ProducerId = pId; ProducerName = pName
                                      ConsumerId = cId; ConsumerName = cName
                                      ConsumerComp = comp; ConsumerKernel = k; ConsumerSeed = seed
                                      DominatingIds = Set.union outer (set [ for i in 0 .. pi - 1 -> (let (id, _, _) = arr.[i] in id) ])
                                      EmittedCpp = ref None; EmittedLlvm = ref None; DeclinedCpp = ref false }
            | _ -> ()
    let rec chain (e: IRExpr) : (IRId * string * IRExpr) list =
        match e with
        | IRLet (id, v, body) -> (id, $"__v{id}", v) :: chain body
        | _ -> []
    for f in modul.Functions do
        planChain (f.Params |> List.map (fun p -> p.VarId) |> Set.ofList) (chain f.Body)
    planChain Set.empty (modul.Bindings |> List.map (fun b -> (b.Id, b.Name, b.Value)))
    modul.Bindings |> List.iteri (fun bi b ->
        match b.Value with
        | IRLet _ ->
            let ahead = modul.Bindings |> List.take bi |> List.map (fun x -> x.Id) |> Set.ofList
            planChain ahead (chain b.Value)
        | _ -> ())
