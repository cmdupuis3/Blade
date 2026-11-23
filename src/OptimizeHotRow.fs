// Hot-row fold scheduling and the round-2 cell-epilogue planner types and side tables (split out of Optimize.fs, cleanup K19).
module Blade.OptimizeHotRow

open Blade.Types

open Blade.Ast
open Blade.IR
open Blade.IRMono
open Blade.OptimizeGates
open Blade.OptimizeAnalysis

// --- Hot-row fold scheduling (bench review item M13, Codex sketch S13) -----
//
// A dense map whose rows a later ROW FOLD consumes,
//
//     let lap = method_for(range<halo<Y, ..>, halo<X, ..>>) <@> k |> compute
//     let s = reduce(method_for(lap) <@> lambda(row) -> f(row), op, seed)
//
// is emitted as two passes over `lap` today: the nest fills every row, then
// the fold reads every row back -- at the stencil benchmark's size a 24 MB
// image that has left every cache between the two passes. The schedule this
// pass plans keeps each row's fold BEHIND the row that produced it, inside
// the producer's own outer loop (and, when that loop is threaded, inside its
// team): row i is produced, `f(row i)` is stored into a rank-1 ROW-SUM array,
// and `s` becomes a fold over that array -- the same per-row values, combined
// in the same order, by the same kernel, from the same seed.
//
// STRICT-LEGAL, under no licence: the per-row fold keeps its own operation
// order (the same code on the same row, run earlier), and the outer combine
// is the same chain over the same values. The interpreter never sees the
// plan -- it is a SIDE TABLE both emitters consult (`HotRowTable`, the
// Types.PoolReuseTable pattern), and the values are unchanged by
// construction, so the differential gates hold. The gate is BLADE_HOT_ROW
// (`blade test opt-diff` compares the two-pass form against this one).
//
// What may NOT move, and the checks that say so:
//   * the row kernel reads only its row: it must neither capture nor
//     reference the producer (row i's fold would otherwise read rows not yet
//     produced), and it must be REPEATABLE in the CSE judge's sense (no
//     assignment, no display, calls only to repeatable callees) -- an effect
//     reordered against the producer's later rows would be observable;
//   * nothing between the producer and the fold may assign, nor reference
//     the producer or any of the row kernel's captures (a `mut` call in
//     between could change what the row kernel reads);
//   * the fold must not LOSE its threads: a `where omp` fold over a producer
//     whose outer level carries no omp licence is threaded today (Path B
//     over the rows) and would run serially behind a serial producer --
//     declined. The build knob is deliberately not consulted: route
//     selection is identical in both emission modes;
//   * the shapes are the plain ones: a dense rank >= 2 producer with scalar
//     cells and no cuda/mpi strategy; the consumer a ROW map (kernel
//     parameter of rank r - 1) over exactly that pool, folded by a
//     two-parameter kernel from a seed, one leaf, no join.
// A row kernel that may ABORT (an unseeded `reduce` over a runtime extent)
// is admitted: it still aborts with the same code on the same row, only
// before later rows are produced rather than after -- the review's
// "BL8009 / BL8003 preserved" item (the halo extent check stays ahead of the
// nest, and the empty-reduce check stays in the row kernel).

// --- Round-2 cell epilogues: the planner types (LANE-NEUTRAL) ---------------
//
// These records hold IR-level facts only: no emitter type (OrbitFold,
// CodeGenExprSupport, EmitLlvm) appears here, because this file compiles
// before every emitter. Each lane resolves its own form at emission (which
// licence the fold carries in THIS build, whether the producer is threaded)
// and records the applied / declined outcome under `cell-epilogue` v1.

/// The per-cell term of a cell chain: the fold of `c * c` (a self-dot:
/// `prodsum(row, row)`, `lambda(w) -> w * w`) or of `c` itself. Seed +0 in
/// both (the IIFE's `T __ps = 0`, blade_fold's zero lanes).
type CellTerm =
    | Square
    | Plain

/// The compact (orbit-class) producer kinds a row-class epilogue serves.
type CompactKind =
    | CompactSym       // SymIdx: inclusive simplex, weight-1 diagonal class
    | CompactAntisym   // AntisymIdx: strict simplex, no stored diagonal

/// B3's candidate (compact rank-2 producer whose logical walk the orbit-class
/// fold would take): recorded by the planner, RESOLVED at emission -- each
/// lane calls its own orbit recognizer on `ConsumerValue` (the C++ lane:
/// Blade.OrbitFold.recognise) and declines when it does not answer `Orbit`.
/// Facts only; no OrbitPlan (OrbitFold compiles after this file).
type CompactRowsCandidate = {
    /// The compact producer's let id.
    ProducerId: IRId
    Kind: CompactKind
    /// The compact group's rank (2 in v1).
    Rank: int
    /// The consumer folds' let ids (outermost first).
    ConsumerIds: IRId list
    /// The consumer's whole value (the nested logical walk), for the
    /// emission-time recognizer.
    ConsumerValue: IRExpr
    /// The fold levels' seeds (outermost first), as IR.
    Seeds: IRExpr list
}

/// Which schedule a hot-row plan is.
type HotRowForm =
    /// A rank >= 2 producer whose ROW map a later fold consumes (M13): the
    /// row's fold behind each produced row, into a rank-1 row-sum array.
    | RowFold
    /// A rank-1 flat producer whose CELL map a later fold consumes (B1): the
    /// fold's lane state (licensed, P1) or ordered chain (repro, P2) fed
    /// behind the producer's own stores -- per Path B chunk when both the
    /// producer and the fold are threaded. `RowMap` is then the cell map
    /// `method_for(P) <@> g`, `CellChain` g's term.
    | ChunkFold
    /// A compact rank-2 producer's per-row orbit-class partials (B3; the
    /// recognition is W1-C's, at emission).
    | CompactClassRows of CompactRowsCandidate

/// One scheduled producer / row-fold pair.
type HotRowPlan = {
    /// The schedule (RowFold for every pre-round-2 plan).
    Form: HotRowForm
    /// The row kernel's / cell kernel's term when it is a +0-seeded
    /// dot-class fold of the row's own cells (`prodsum(row, row)`, `w * w`,
    /// `w`) AND BLADE_CELL_EPILOGUE is on: the emitters may then advance the
    /// fold per cell inside the producer (B2r chain interleave, B1). None =
    /// the row's fold stays one call on the finished row.
    CellChain: CellTerm option
    ProducerId: IRId
    ConsumerId: IRId
    /// The row-sum array: a FRESH id both emitters bind to a rank-1 pool of
    /// the fold kernel's accumulator type over the producer's leading axis.
    /// Never appears in the IR tree; the table is its only home.
    RowSumId: IRId
    RowSumType: IRType
    /// The consumer's row map (`method_for(P) <@> rowKernel`), its one leaf.
    RowMap: ApplyInfo
    FoldKernel: IRExpr
    Seed: IRExpr
    ProducerName: string
    ConsumerName: string
    /// The C++ lane's handshake: the row-sum array's emitted name, written by
    /// the nest emitter when it actually placed the epilogue (a dispatched,
    /// flat, sliced or collapse-pragma nest never does), read by the
    /// consumer's emission to choose between the row-sum fold and the
    /// two-pass form. Cleared at each producer emission and after consumption,
    /// so reused let ids in shape specializations cannot reuse a stale buffer.
    EmittedCpp: string option ref
}

/// The plan, keyed both ways (the producer's emission binds the row-sum
/// array; the consumer's emission folds it). AsyncLocal like
/// Types.PoolReuseTable; reset once per program in `lowerTypedProgram`,
/// because let ids restart per compile.
module HotRowTable =
    open System.Threading
    type State = { ByProducer: Map<IRId, HotRowPlan>; ByConsumer: Map<IRId, HotRowPlan> }
    let private empty = { ByProducer = Map.empty; ByConsumer = Map.empty }
    let private cell = new AsyncLocal<State>()
    let private get () = match box cell.Value with null -> empty | _ -> cell.Value
    let reset () = cell.Value <- empty
    let record (p: HotRowPlan) =
        let s = get ()
        cell.Value <- { ByProducer = Map.add p.ProducerId p s.ByProducer
                        ByConsumer = Map.add p.ConsumerId p s.ByConsumer }
    let tryProducer (id: IRId) : HotRowPlan option = Map.tryFind id (get ()).ByProducer
    let tryConsumer (id: IRId) : HotRowPlan option = Map.tryFind id (get ()).ByConsumer

/// B2r for a recursion (round-2 cell epilogues): a row-fold-fusion step whose
/// per-row fold is `prodsum(row, row)` and whose row is written by ONE
/// slice-write-fused cell loop. The IR is unchanged (the interpreter keeps
/// the spec); an emitter that recognizes the loop advances the row's chain
/// per cell inside it -- the same `+=` of the same products in the same
/// ascending order from the same +0 -- and binds the fold let to the chain.
type CellFoldEntry = {
    /// The cell loop's IRForRange variable (the table key).
    CellLoopVar: IRId
    /// The trajectory buffer; the loop's final statement stores
    /// `Buffer[StepVar][CellLoopVar] = StoredVar`.
    BufferId: IRId
    StepVar: IRId
    /// The let the loop body stores (the cell's value, one scalar local).
    StoredVar: IRId
    /// The fold let `FoldLetId = prodsum(row, row)` the chain replaces, and
    /// the row-view let before it.
    FoldLetId: IRId
    RowLetId: IRId
    Term: CellTerm
    Subject: string
    /// Emission handshake (C++): the chain accumulator's name, set by the
    /// cell loop when it emitted the chain, read and CLEARED by the fold let.
    /// A loop that did not emit the chain leaves it None, and the fold let
    /// renders its prodsum unchanged (the fallback is atomic).
    EmittedCpp: string option ref
    /// The LLVM lane's twin of the handshake (the chain's SSA accumulator).
    EmittedLlvm: string option ref
}

module CellFoldTable =
    open System.Threading
    type State = { ByLoop: Map<IRId, CellFoldEntry>; ByFoldLet: Map<IRId, CellFoldEntry> }
    let private empty = { ByLoop = Map.empty; ByFoldLet = Map.empty }
    let private cell = new AsyncLocal<State>()
    let private get () = match box cell.Value with null -> empty | _ -> cell.Value
    let reset () = cell.Value <- empty
    let record (e: CellFoldEntry) =
        let s = get ()
        cell.Value <- { ByLoop = Map.add e.CellLoopVar e s.ByLoop
                        ByFoldLet = Map.add e.FoldLetId e s.ByFoldLet }
    let tryLoop (id: IRId) : CellFoldEntry option = Map.tryFind id (get ()).ByLoop
    let tryFoldLet (id: IRId) : CellFoldEntry option = Map.tryFind id (get ()).ByFoldLet

/// The row fold a threaded step carries (attached by row-fold fusion when it
/// fuses the recursion's row consumer): the row-view let, the fold let
/// `FoldLetId = K(row)`, the accumulator the time chain updates, the let
/// holding that update `acc <- F(acc, p)`, and K's cell term when K is the
/// self-dot (`prodsum(row, row)` -- the only row kernel whose licensed form
/// a per-worker chunk partial reproduces; any other K folds complete rows).
type RecStepFold = {
    RowLetId: IRId
    FoldLetId: IRId
    AccId: IRId
    AssignLetId: IRId
    Term: CellTerm option
    /// The OUTER fold kernel F carries its own `where omp`: the request that
    /// licenses evaluating K on complete rows in PARALLEL after the team
    /// (G35's row-slot gate); without it the post-pass folds rows serially.
    OuterRequest: bool
    /// K or F demands reproducible evaluation (`where repro`): the licensed
    /// partial form is never taken for this fold.
    StrictOnly: bool
}

/// Wave 3 (E-G5, bench gap G5): a slice-write-fused CELL loop whose root map
/// carried a `where omp` request over the row, with the facts a persistent
/// team over the recursion's steps needs -- the time loop's variable and
/// start, the buffer, the row extent -- proved by `fuseSliceWritesModule`
/// (every trajectory read is the cell being written, of a strictly earlier
/// row). The IR is the ordinary sequential loop (the interpreter and the
/// LLVM lane run it as is); the C++ lane's `genForRangeBinding` emits the
/// time loop as ONE team with fixed contiguous x ownership and no per-step
/// synchronization when it finds the entry under the time loop's variable.
type RecStepEntry = {
    /// The cell loop's IRForRange variable (one table key).
    CellLoopVar: IRId
    /// The enclosing time loop's variable (the other key) and its start.
    StepVar: IRId
    StepLo: int64
    BufferId: IRId
    /// The row extent (the cell loop runs 0..Cells).
    Cells: int64
    Subject: string
    /// The requesting kernel, by the name the program wrote (for the census).
    KernelName: string
    /// Set by row-fold fusion when the recursion's row consumer is fused
    /// into the loop; None = the trajectory is only stored.
    Fold: RecStepFold option ref
    /// Emission handshake (C++): the chunk bounds the time loop's region
    /// hands the cell loop (`__rlo`, `__rhi`), set around the body's
    /// rendering and cleared after; None = the plain 0..Cells loop.
    ChunkBounds: (string * string) option ref
    /// Set by the C++ time-loop emitter when it DECIDED the loop (threaded or
    /// declined with its census line); an entry still unset when the program
    /// is assembled names a request the emitters never saw (the loop reached
    /// an emitter without the hook), and the assembly prints its census.
    Decided: bool ref
}

module RecStepTable =
    open System.Threading
    type State = { ByLoop: Map<IRId, RecStepEntry>; ByStep: Map<IRId, RecStepEntry> }
    let private empty = { ByLoop = Map.empty; ByStep = Map.empty }
    let private cell = new AsyncLocal<State>()
    let private get () = match box cell.Value with null -> empty | _ -> cell.Value
    let reset () = cell.Value <- empty
    /// False when a time loop or a cell loop already has an entry (a second
    /// requesting slice under one time loop): the caller then declines.
    let record (e: RecStepEntry) : bool =
        let s = get ()
        if s.ByStep.ContainsKey e.StepVar || s.ByLoop.ContainsKey e.CellLoopVar then false
        else
            cell.Value <- { ByLoop = Map.add e.CellLoopVar e s.ByLoop
                            ByStep = Map.add e.StepVar e s.ByStep }
            true
    let tryLoop (id: IRId) : RecStepEntry option = Map.tryFind id (get ()).ByLoop
    let tryStep (id: IRId) : RecStepEntry option = Map.tryFind id (get ()).ByStep
    /// The entries no emitter decided (see `Decided`).
    let undecided () : RecStepEntry list =
        (get ()).ByStep |> Map.toList |> List.map snd |> List.filter (fun e -> not e.Decided.Value)

/// How the C++ time-loop emitter (CodeGenBinding.genRecStepTimeLoop) folds a
/// threaded step's fused row consumer -- the ONE predicate that emitter and
/// the recursion-ring planner (`windowRecFoldsModule`) both call, so the
/// planner can only ring a trajectory whose rows the emitter never reads
/// after the team. `strict` = the build or the enclosing scope keeps every
/// fold's serial association (BLADE_FP_REPRO=1, `where repro`); `lanesOff` =
/// `foldLanesSwitchedOff ()`; `rowVec` = the row's element type has
/// blade_fold vector forms (double / float).
type RecStepFoldForm =
    /// No fused row consumer: the team writes rows, nothing reads them after.
    | RecStepNoFold
    /// Licensed self-dot partials per worker chunk inside the team; after the
    /// team only the partials array is read.
    | RecStepInTeamPartials
    /// The strict post-pass: the row kernel on each COMPLETE row after the
    /// team (every row is read again).
    | RecStepStrictPostPass

let recStepFoldForm (strict: bool) (lanesOff: bool) (rowVec: bool) (fold: RecStepFold option) : RecStepFoldForm =
    match fold with
    | None -> RecStepNoFold
    | Some f ->
        if not strict && not f.StrictOnly && f.Term = Some Square && rowVec && not lanesOff then RecStepInTeamPartials
        else RecStepStrictPostPass

/// The recursion ring's leading subscript: `Int64(i) % R` (the form the
/// rec-window desugar's ring already lowers to); a literal row is folded.
let recRingIndex (r: int64) (i0: IRExpr) : IRExpr =
    match i0 with
    | IRLit (IRLitInt v) -> IRLit (IRLitInt (v % r))
    | _ -> IRBinOp (IRElementwise, IRMod, IRUnaryOp (IRCast (ETInt64, SrcLoc.Nowhere), i0), IRLit (IRLitInt r), SrcLoc.Nowhere)

/// Is `i0` the ring subscript of the step variable `n` (`Int64(n) % R`)? The
/// slot count when it is.
let (|RecRingOfStep|_|) (n: IRId) (i0: IRExpr) : int64 option =
    match i0 with
    | IRBinOp (_, IRMod, IRUnaryOp (IRCast (ETInt64, _), IRVar (v, _)), IRLit (IRLitInt r), _) when v = n -> Some r
    | _ -> None

/// One `rec-step-parallel` v1 decision record (the planner's admission and
/// its declines; the C++ emitter records the per-build applied / declined
/// outcome under the same rule).
let recordRecStep (subject: string) (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) =
    Blade.Effects.Decisions.record
        { Blade.Effects.Rule = "rec-step-parallel"; Version = 1
          Span = Blade.Ast.noSpan; Subject = subject
          Outcome = outcome; Evidence = evidence }

/// The cell term of a ROW kernel `lambda(row) -> prodsum(row, row)`.
let rowKernelCellTerm (ck: IRCallable) : CellTerm option =
    match ck.Params, ck.Body with
    | [ p ], IRProdSum [ IRVar (a, _); IRVar (b, _) ] when a = p.VarId && b = p.VarId -> Some Square
    | _ -> None

/// The cell term of a CELL kernel `lambda(w) -> w * w` / `lambda(w) -> w`.
let cellKernelCellTerm (ck: IRCallable) : CellTerm option =
    match ck.Params with
    | [ p ] ->
        let isP (e: IRExpr) =
            match e with
            | IRVar (id, _) -> id = p.VarId
            | IRParam (_, idx, _) -> idx = p.Index
            | _ -> false
        (match ck.Body with
         | IRBinOp (IRElementwise, IRMul, a, b, _) when isP a && isP b -> Some Square
         | e when isP e -> Some Plain
         | _ -> None)
    | _ -> None

/// Plan the hot-row schedule over one module: function-body let chains and
/// the top-level binding sequence alike. Records into `HotRowTable` and the
/// decision collector (`hot-row-fold`); the IR is untouched.
let planHotRowFolds (builder: IRBuilder) (programFuncs: IRCallable list) (modul: IRModule) : unit =
    let funcs = modul.Functions |> List.map (fun f -> (f.Id, f)) |> Map.ofList
    let facts = cseFactsOf funcs
    let writes = scheduleMayWriteIn programFuncs modul
    let lifetimeBarrier = scheduleHasLifetimeBarrierIn programFuncs modul
    // An intervening statement is audited by its complete payload (finding 7).
    let payloadOf = intervalPayloadOf modul
    let providerEffect = intervalHasProviderEffect modul
    let isDeferredId = programDeferredId modul programFuncs Seq.empty
    let decide (subject: string) (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) =
        Blade.Effects.Decisions.record
            { Blade.Effects.Rule = "hot-row-fold"; Version = 1
              Span = Blade.Ast.noSpan; Subject = subject
              Outcome = outcome; Evidence = evidence }
    let rec collapse (e: IRExpr) = match e with IRCompute i -> collapse i | e -> e
    let flatRank (at: IRArrayType) = at.IndexTypes |> List.sumBy (fun ix -> ix.Rank)
    // A dense, plain, rank >= 2 pool of scalar cells with no device / rank
    // strategy on its kernel: the shape whose outer level completes one
    // contiguous row per iteration.
    let producerShape (info: ApplyInfo) : (IRArrayType * IRCallable) option =
        match stripUnits info.OutputType, resolveCallableIn funcs (fst (peelReynolds info.Kernel)) with
        | ArrayElem at, Some pk
            when not at.IsVirtual
                 && at.IndexTypes.Length >= 2
                 && at.IndexTypes |> List.forall (fun ix -> ix.IxKind = IxKPlain && ix.Symmetry = SymNone && ix.Rank = 1)
                 && info.KernelOutputRank = 0
                 && not pk.IsCudaKernel && not pk.IsMpiParallel ->
            Some (at, pk)
        | _ -> None
    // `method_for(P) <@> rowKernel`: one operand, the producer by name; a
    // one-parameter kernel over a slice of rank r - 1.
    let rowMapOf (pId: IRId) (rank: int) (info: ApplyInfo) : IRCallable option =
        match info.Arrays, resolveCallableIn funcs (fst (peelReynolds info.Kernel)) with
        | [ IRVar (a, _) ], Some ck when a = pId && not info.HasReynolds && info.KernelOutputRank = 0 ->
            (match ck.Params with
             | [ p ] ->
                let paramRank =
                    match stripUnits p.Type with
                    | ArrayElem a -> Some (flatRank a)
                    | _ -> None
                if paramRank = Some (rank - 1) || info.KernelInputRanks = [ rank - 1 ] then Some ck else None
             | _ -> None)
        | _ -> None
    // Does the producer's omp licence cover the nest's OUTERMOST level? Param 0
    // contributes the leading S-dims of an outer product; under co-iteration
    // every param peels at level 0. A bare `where omp` (no depths) licenses
    // level 0 (IRStorage.buildLoopNestCodeGen's `licenseUnresolved` rule).
    let outerLicensed (info: ApplyInfo) (pk: IRCallable) =
        pk.IsOmpParallel
        && (List.isEmpty pk.Parallelism
            || pk.Parallelism |> List.exists (fun (pi, d) -> d >= 1 && (pi = 0 || info.IsCoIteration)))
    let refs (e: IRExpr) = collectVarRefsIR e
    let rec planChain (items: (IRId * string * IRExpr) list) =
        let arr = List.toArray items
        for pi in 0 .. arr.Length - 1 do
            let (pId, pName, pVal) = arr.[pi]
            match collapse pVal with
            | IRApplyCombinator pinfo when (match pVal with IRCompute _ -> true | _ -> false) ->
                match producerShape pinfo with
                | None -> planRank1 arr pi pId pName pinfo
                | Some (pAt, pk) ->
                    let rank = flatRank pAt
                    let consumer =
                        [ pi + 1 .. arr.Length - 1 ]
                        |> List.tryPick (fun ci ->
                            let (cId, cName, cVal) = arr.[ci]
                            match cVal with
                            | IRReduceCompute (IRApplyCombinator cinfo, k, seed) ->
                                (match rowMapOf pId rank cinfo with
                                 | Some ck -> Some (ci, cId, cName, cinfo, ck, k, seed)
                                 | None -> None)
                            | _ -> None)
                    match consumer with
                    | None -> ()
                    | Some (ci, cId, cName, cinfo, ck, k, seed) ->
                        let what = $"row fold of `{pName}`"
                        let declined (why: string) = decide cName (Blade.Effects.Declined why) [ what ]
                        let captureIds = ck.Captures |> List.map (fun c -> c.Id) |> Set.ofList
                        let kernelRefs = refs ck.Body
                        let between = [ pi + 1 .. ci - 1 ] |> List.map (fun i -> arr.[i])
                        // Is the row kernel MOVABLE (cannot fail)? Proved
                        // only for the whitelisted self-prodsum
                        // `prodsum(row, row)`: one operand, so no extent
                        // check, and `*` / `+` never trap. Any other row
                        // kernel may fail, and running it at the producer
                        // would move that failure ahead of whatever sits
                        // between the producer and the fold.
                        let kernelMovable = (rowKernelCellTerm ck).IsSome
                        let offending =
                            between |> List.tryPick (fun (iId, iName, iVal0) ->
                                // the statement's complete payload (a module
                                // binding's side-table operands included)
                                let iVal = payloadOf iId iVal0
                                let r = refs iVal
                                // DOMINANCE: the row kernel runs at the
                                // producer, so every value it reads must be
                                // bound before the producer -- a capture or
                                // free reference bound in between would be
                                // read before its declaration (or a shadowed
                                // outer name would be read instead).
                                if lifetimeBarrier iVal then Some $"`{iName}` is an inline lifetime boundary between the producer and the fold"
                                elif Set.contains iId captureIds || Set.contains iId kernelRefs then
                                    Some $"the row kernel reads `{iName}`, which is bound between the producer and the fold (the row kernel would run before it exists)"
                                elif Set.contains pId r then Some $"`{iName}` reads `{pName}` between the producer and the fold"
                                elif not (Set.isEmpty (Set.intersect r captureIds)) then Some $"`{iName}` references a binding the row kernel captures"
                                elif not (facts iVal).Repeatable || writes iVal || providerEffect iId then Some $"`{iName}` is not repeatable (an assignment, display output, a provider write, or a call the module cannot judge -- its side-table payload included) between the producer and the fold"
                                elif not kernelMovable then
                                    Some $"the row kernel may fail (only `prodsum(row, row)` is proved non-failing) and `{iName}` sits between the producer and the fold: running the row kernel at the producer could move its failure ahead of `{iName}`'s"
                                else None)
                        match resolveCallableIn funcs (fst (peelReynolds k)) with
                        | None -> declined "the fold kernel does not resolve to a callable"
                        | Some fk when fk.Params.Length <> 2 -> declined "the fold kernel is not a two-parameter callable"
                        | Some fk when fk.IsCudaKernel || fk.IsMpiParallel -> declined "the fold kernel carries a cuda/mpi strategy"
                        | Some fk ->
                        if Set.contains pId captureIds || Set.contains pId kernelRefs then
                            declined $"the row kernel reads `{pName}` beyond its own row (a capture or a direct reference), so it cannot run before the whole array exists"
                        elif not (facts cinfo.Kernel).Repeatable || writes (IRApplyCombinator cinfo) then
                            declined "the row kernel is not repeatable (an assignment, display output, or a call the module cannot judge), so running it behind each row could reorder an observable effect"
                        // G41 (Codex findings 14, 17): the row kernel would run
                        // between the producer's rows and AHEAD of the outer
                        // combine's earlier applications, so a producer, an
                        // outer fold kernel or a seed that WRITES (its write set
                        // is not proved disjoint from what the row kernel reads)
                        // could change what a row sees. Conservative, all five
                        // parties through the shared predicate.
                        elif (facts pinfo.Kernel).MayWrite || writes (IRApplyCombinator pinfo) then
                            declined "the producer's kernel may write (an assignment or a mutating call): running the row kernel behind each produced row would move its reads across the producer's later writes"
                        elif (facts k).MayWrite || writes k then
                            declined "the outer fold kernel may write: the row values would be computed ahead of its earlier applications, moving the row kernel's reads across its writes"
                        elif (facts seed).MayWrite || writes seed then
                            declined "the fold's seed may write: the row values would be computed ahead of it"
                        elif fk.IsOmpParallel && not (outerLicensed pinfo pk) then
                            declined "the fold's `where omp` threads it over the rows today (Path B) while the producer's outer level carries no omp licence; behind a serial producer the row folds would lose their team"
                        else
                        match offending with
                        | Some why -> declined why
                        | None ->
                        // G265: a row kernel that MAY FAIL (not the movable
                        // self-prodsum, not closed-total: `prodsum(row, b)`
                        // checks its extents, BL8011) runs behind each produced
                        // row, so its failure would overtake the producer's
                        // later cells, the seed and the outer kernel's earlier
                        // applications, which the two-pass form runs first:
                        // every one of them must be closed-total (Codex design
                        // review of G265; the deferred-value predicate), and the
                        // producer must be serial (on its thread team the rows
                        // run out of order, so another row's failure could
                        // arrive first: Codex diff review). A fold of the ROW
                        // PARAMETER itself with a closed-total kernel and seed
                        // cannot fail (every cell exists).
                        let resolveF = resolveCallableIn funcs
                        let totalExpr (e: IRExpr) =
                            IRTotality.walk resolveF (fun _ -> None) isDeferredId Map.empty Map.empty Set.empty Set.empty Set.empty e |> Result.isOk
                        let totalCallable (c: IRCallable) =
                            IRTotality.auditCallable resolveF (fun _ -> None) isDeferredId c [] |> Result.isOk
                        let rowKernelTotal =
                            totalCallable ck
                            || (match ck.Params, ck.Body with
                                | [ rp ], ReduceOfNamed (rv, _, rk, Some rseed) when rv = rp.VarId ->
                                    (match resolveF rk with
                                     | Some rkc when rkc.Params.Length = 2 -> totalCallable rkc && totalExpr rseed
                                     | _ -> false)
                                | _ -> false)
                        if not kernelMovable && not rowKernelTotal
                           && (outerLicensed pinfo pk
                               || (IRTotality.auditApply resolveF (fun _ -> None) isDeferredId pinfo |> Result.isError)
                               || not (totalCallable fk)
                               || not (totalExpr seed)) then
                            declined "the row kernel may fail (it is neither the self-prodsum nor closed-total) and the producer runs a thread team (another row's failure could arrive first) or the producer, the outer fold kernel or the seed is not proved closed-total: running the row kernel behind each produced row could move its failure ahead of theirs (G265)"
                        else
                        let accTy =
                            match stripUnits fk.RetType with
                            | IRTScalar _ as t -> Some t
                            | _ -> None
                        match accTy with
                        | None -> declined "the fold kernel's result is not a scalar"
                        | Some accTy ->
                        let rowsTxt =
                            match (List.head pAt.IndexTypes).Extent with
                            | IRLit (IRLitInt n) -> $"{n} rows"
                            | _ -> "a runtime row count"
                        let team =
                            if outerLicensed pinfo pk then "; the producer's outer level is omp-licensed, so the row folds run inside its team"
                            else ""
                        let evidence =
                            [ $"`{cName}` = fold of the row map over `{pName}` ({rowsTxt}): each row's value is computed right after that row is produced and stored into a rank-1 row-sum array; the outer combine folds the row sums in the same order from the same seed{team}"
                              "strict-legal: no reorder licence used (the per-row fold keeps its operation order; the outer chain is the same values in the same order)" ]
                        if not (hotRowEnabled ()) then
                            decide cName (Blade.Effects.Declined "disabled by BLADE_HOT_ROW") evidence
                        else
                            let rsId = builder.FreshId()
                            let rsTy =
                                mkArrayLike { ElemType = accTy
                                              IndexTypes = [ List.head pAt.IndexTypes ]
                                              IsVirtual = false
                                              Identity = None }
                            // B2r: the row kernel's cell term, when the cell
                            // epilogue may advance the row's chain per cell.
                            let cellChain =
                                match rowKernelCellTerm ck with
                                | Some t when cellEpilogueEnabled () -> Some t
                                | Some _ ->
                                    recordCellEpilogue cName (Blade.Effects.Declined "disabled by BLADE_CELL_EPILOGUE")
                                        [ $"`{cName}`'s row kernel `prodsum(row, row)` keeps one fold call per finished row of `{pName}`" ]
                                    None
                                | None ->
                                    recordCellEpilogue cName (Blade.Effects.Declined "the row kernel is not a +0-seeded dot-class fold of the row's own cells (v1: `prodsum(row, row)`)")
                                        [ $"the hot-row fold of `{pName}` keeps one row-kernel call per finished row" ]
                                    None
                            HotRowTable.record
                                { Form = RowFold; CellChain = cellChain
                                  ProducerId = pId; ConsumerId = cId
                                  RowSumId = rsId; RowSumType = rsTy
                                  RowMap = cinfo; FoldKernel = k; Seed = seed
                                  ProducerName = pName; ConsumerName = cName
                                  EmittedCpp = ref None }
                            decide cName Blade.Effects.Applied evidence
            | _ -> ()
    // B1 (round-2 cell epilogues): a DENSE RANK-1 flat producer whose CELL map
    // a later fold consumes,
    //     let y2 = method_for(zip(x, y)) <@> k |> compute
    //     let s  = reduce(method_for(y2) <@> lambda(w) -> w * w, F, seed)
    // A separate arm from the rank >= 2 row schedule above (whose guard is
    // NOT weakened): the plan says only that the fold's chain may be fed
    // behind the producer's stores; which form (P1 lane state, Path B
    // chunks, P2 ordered chain) is decided per build by each emitter, which
    // records the outcome. The source-level refusals are recorded here.
    and planRank1 (arr: (IRId * string * IRExpr) array) (pi: int) (pId: IRId) (pName: string) (pinfo: ApplyInfo) =
        let rank1Shape =
            match stripUnits pinfo.OutputType, resolveCallableIn funcs (fst (peelReynolds pinfo.Kernel)) with
            | ArrayElem at, Some pk
                when not at.IsVirtual && at.IndexTypes.Length = 1
                     && (let ix = at.IndexTypes.Head in ix.IxKind = IxKPlain && ix.Symmetry = SymNone && ix.Rank = 1)
                     && info0Scalar pinfo ->
                Some (at, pk)
            | _ -> None
        match rank1Shape with
        | None -> ()
        | Some (pAt, pk) ->
        // The consumer: the FIRST later fold of a one-operand cell map over
        // the producer.
        let consumer =
            [ pi + 1 .. arr.Length - 1 ]
            |> List.tryPick (fun ci ->
                let (cId, cName, cVal) = arr.[ci]
                match cVal with
                | IRReduceCompute (IRApplyCombinator cinfo, k, seed) ->
                    (match cinfo.Arrays, resolveCallableIn funcs (fst (peelReynolds cinfo.Kernel)) with
                     | [ IRVar (a, _) ], Some ck when a = pId && ck.Params.Length = 1 && cinfo.KernelOutputRank = 0
                                                     && (match stripUnits ck.Params.Head.Type with IRTScalar _ -> true | _ -> false) ->
                        Some (ci, cId, cName, cinfo, ck, k, seed)
                     | _ -> None)
                | _ -> None)
        match consumer with
        | None -> ()
        | Some (ci, cId, cName, cinfo, ck, k, seed) ->
        let declined (why: string) =
            recordCellEpilogue cName (Blade.Effects.Declined why)
                [ $"`{cName}` folds the cells of `{pName}` after the producer has stored them all (two passes)" ]
        let captureIds = ck.Captures |> List.map (fun c -> c.Id) |> Set.ofList
        let between = [ pi + 1 .. ci - 1 ] |> List.map (fun i -> arr.[i])
        let offending =
            between |> List.tryPick (fun (iId, iName, iVal0) ->
                // the statement's complete payload (finding 7)
                let iVal = payloadOf iId iVal0
                let r = refs iVal
                // Dominance: the cell kernel runs at the producer, so nothing
                // it reads may be bound in between.
                if lifetimeBarrier iVal then Some $"`{iName}` is an inline lifetime boundary between the producer and the fold"
                elif Set.contains iId captureIds || Set.contains iId (refs ck.Body) then
                    Some $"the cell kernel reads `{iName}`, which is bound between the producer and the fold (it would run before `{iName}` exists)"
                elif Set.contains pId r then Some $"`{iName}` reads `{pName}` between the producer and the fold"
                elif not (Set.isEmpty (Set.intersect r captureIds)) then Some $"`{iName}` references a binding the cell kernel captures"
                elif not (facts iVal).Repeatable || writes iVal || providerEffect iId then Some $"`{iName}` is not repeatable (an assignment, display output, a provider write, or a call the module cannot judge -- its side-table payload included) between the producer and the fold"
                else None)
        let extentLit =
            match (List.head pAt.IndexTypes).Extent with
            | IRLit (IRLitInt n) when n > 0L -> Some n
            | _ -> None
        if pk.IsCudaKernel || pk.IsMpiParallel then declined "the producer carries a cuda/mpi strategy"
        elif pinfo.HasReynolds || cinfo.HasReynolds then declined "a Reynolds body (the producer's or the cell kernel's)"
        elif extentLit.IsNone then declined "the producer's extent is not a literal (the flat producer path needs one)"
        elif Set.contains pId captureIds || Set.contains pId (refs ck.Body) then
            declined $"the cell kernel reads `{pName}` beyond its own cell (a capture or a direct reference)"
        // G41: the fold is fed behind the producer's stores -- all five
        // parties through the shared write predicate.
        elif writes (IRApplyCombinator pinfo) then declined "the producer's kernel may write: its writes would interleave with the fold"
        elif writes (IRApplyCombinator cinfo) then declined "the cell kernel may write"
        elif writes k then declined "the fold kernel may write: its applications would interleave with the producer"
        elif writes seed then declined "the fold's seed may write"
        else
        match resolveCallableIn funcs (fst (peelReynolds k)) with
        | None -> declined "the fold kernel does not resolve to a callable"
        | Some fk when fk.Params.Length <> 2 -> declined "the fold kernel is not a two-parameter callable"
        | Some fk when fk.IsCudaKernel || fk.IsMpiParallel -> declined "the fold kernel carries a cuda/mpi strategy"
        | Some fk ->
        match offending with
        | Some why -> declined why
        | None ->
        match cellKernelCellTerm ck with
        | None -> declined "the cell kernel is not `w * w` or `w` of its own cell (v1 recognizes those two cell terms; anything else may abort or read more than the cell)"
        | Some term ->
        match stripUnits fk.RetType with
        | IRTScalar _ as accTy ->
            if not (cellEpilogueEnabled ()) then
                declined "disabled by BLADE_CELL_EPILOGUE"
            else
                HotRowTable.record
                    { Form = ChunkFold; CellChain = Some term
                      ProducerId = pId; ConsumerId = cId
                      // No row-sum array: the plan carries the accumulator
                      // type in RowSumType for the emitters' declarations.
                      RowSumId = builder.FreshId(); RowSumType = accTy
                      RowMap = cinfo; FoldKernel = k; Seed = seed
                      ProducerName = pName; ConsumerName = cName
                      EmittedCpp = ref None }
        | _ -> declined "the fold kernel's result is not a scalar"
    and info0Scalar (info: ApplyInfo) = info.KernelOutputRank = 0
    // Let chains: a function body, the top-level binding sequence, and the
    // chain NESTED inside a top-level binding's value (`let (a, b) = { let
    // k = ..; let s = ..; (..) }` -- pairkernel's shape; the C++ lane names
    // those lets `__v<id>` exactly as a function body's).
    let rec chain (e: IRExpr) : (IRId * string * IRExpr) list =
        match e with
        | IRLet (id, v, body) -> (id, $"__v{id}", v) :: chain body
        | _ -> []
    for f in modul.Functions do
        planChain (chain f.Body)
    planChain (modul.Bindings |> List.map (fun b -> (b.Id, b.Name, b.Value)))
    for b in modul.Bindings do
        match b.Value with
        | IRLet _ -> planChain (chain b.Value)
        | _ -> ()
