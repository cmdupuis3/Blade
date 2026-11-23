// The semantic-equivalence optimization layer.
//
// This module is the HOME for rewrites that change what a program COSTS
// without changing what it MEANS -- the compiler-side of "the fastest way is
// the only way". Rules of admission, each load-bearing:
//
//   1. COST ONLY, NEVER CONTRACT. A pass may derive an early exit, collapse
//      a loop, or fold a decided branch; it may never add or remove an
//      abort, a licence, or an observable value. (The `while` guard's
//      budget abort is a CONTRACT and therefore a surface spelling, not an
//      optimization -- see recognizeFreezeIdiom below for the boundary.)
//   2. TWIN-SAFE. Both back ends and the interpreter consume the rewritten
//      tree (passes run in Lowering, or pre-lowering where the equivalence
//      is only decidable there), so the differential gates hold by
//      construction; a pass that could not keep them byte-identical does
//      not belong here.
//   3. ESCAPABLE. Every pass carries a per-call environment gate
//      (BLADE_FUSION, BLADE_FREEZE_IDIOM, ...) so any rewrite can be A/B'd
//      against its absence. Gates are functions, never cached module lets
//      -- tests pin and restore them mid-process.
//   4. DECIDABLE AT ITS OWN SEAM. Most passes are IR->IR, but a recognition
//      whose evidence dissolves by IR time (the recursive-array freeze
//      idiom, whose declarative shape only exists on RecArrayDef) runs at
//      the last seam where it is exact. The layer is defined by the charter
//      above, not by a pipeline position.
//
// Implementations that PREDATE the layer live in IRMono for dependency
// reasons -- foldConstIntMatch is shared with the arity specializer (which
// needs it DURING specialization for recursion termination), and the fusion
// pass grew up beside the binop rewrite it runs after. `optimizeModule` is
// the single pipeline entry over them. Since the K19 split this file holds
// only that driver, the two probe cells and the surface other files name as
// `Optimize.<name>`: a new pass lands in an Optimize<Family>.fs file compiled
// before this one (Blade.fsproj order), and a name another file needs from it
// is re-exported at the end of this file.
module Blade.Optimize

open Blade.Types

open Blade.Ast
open Blade.IR
open Blade.IRMono
open Blade.OptimizeGates
open Blade.OptimizeAnalysis
open Blade.OptimizeStorage
open Blade.OptimizeCse
open Blade.OptimizeHoist
open Blade.OptimizeAdvisories
open Blade.OptimizeHotRow
open Blade.OptimizeEpilogueLegs
open Blade.OptimizeMemberHoist
open Blade.OptimizeCompaction
open Blade.OptimizeEquiJoin
open Blade.OptimizeCompactRows
open Blade.OptimizeOrbit
open Blade.OptimizeGroupAgg
open Blade.OptimizeSliceWrites
open Blade.OptimizeRecFold
open Blade.OptimizeSiblingFolds
open Blade.OptimizePartialRow
open Blade.OptimizeComputeElision
open Blade.OptimizeDecompact
open Blade.OptimizeCallElision
open Blade.OptimizeTranspose
open Blade.OptimizeInPlace
open Blade.OptimizeStreaming
open Blade.OptimizeAssemblyDest
open Blade.OptimizeRecompute
open Blade.OptimizeSiblingMaps
open Blade.OptimizeLateRewrites

/// The C++ lane's BLAS gate (`LinAlgPatterns.blasAvailable`, which compiles
/// after this file), installed by Lowering before the optimizer runs: the
/// epilogue-legs planner keeps an inline only when the cell epilogue it rides
/// is not left to the L1 dispatch. A FUNCTION (read per call), never a cached
/// value; the default answers "off".
let mutable blasGateProbe : unit -> bool = fun () -> false
/// LinAlgPatterns.lapackAvailable, installed by Lowering beside blasGateProbe
/// (the LU companion plan asks whether the factor takes the LAPACK route).
let mutable lapackGateProbe : unit -> bool = fun () -> false

/// The passes that read a probe live in earlier files, which cannot name the
/// mutable cells above: each takes its probe as a parameter, read at the same
/// point as before (the lambda reads the cell when the pass calls it).
let shareSiblingFoldsModule (builder: IRBuilder) (programFuncs: IRCallable list) (modul: IRModule) : IRModule =
    OptimizeSiblingFolds.shareSiblingFoldsModule (fun () -> blasGateProbe ()) builder programFuncs modul
let planLuCompanion (programFuncs: IRCallable list) (foreignRefs: Set<IRId>) (modul: IRModule) : unit =
    OptimizeInPlace.planLuCompanion (fun () -> lapackGateProbe ()) programFuncs foreignRefs modul

// --- Pipeline entry -------------------------------------------------------

/// The IR-level optimization stage, run per module in Lowering after the
/// monomorphizers and the array-binop rewrite, before inline-form lifting:
/// constant-scrutinee match folding (which also resolves symbolic ranks per
/// specialization), then elementwise-chain fusion. One entry point so the
/// pipeline reads as a stage, and so a new pass has one obvious place to
/// join.
let private optimizeModuleCore (builder: IRBuilder) (programFuncs: IRCallable list) (singleModule: bool) foreignCensus (modul: IRModule) : IRModule =
    // Normal lowering constructs the events LAST. If a consumer re-applies
    // this stage to optimized IR, retain the established lifetime regions.
    if (modul.Functions |> List.exists (fun f -> containsPoolLifetimeEvent f.Body))
       || (modul.Bindings |> List.exists (fun b -> containsPoolLifetimeEvent b.Value)) then modul else
    recordGramApplyAdvisory modul
    recordSegmentStreaming modul
    // call elision first: a jointly lowered call leaves a fused fold or a
    // map operand that compute elision and fusion then see as written out
    let (callElided, pendingCalls) = elideCallsModule builder programFuncs (foldConstMatchesModule (floatComputedBlocksModule modul))
    let (elided, pendingComputes) = elideSingleConsumerComputesModule builder programFuncs singleModule callElided
    let pendingMaps = pendingCalls @ pendingComputes
    // P5: the two-consumer intermediates compute elision left, priced and
    // chosen by the cost model (BLADE_RECOMPUTE)
    let (recomputed, pendingRecompute) = selectRecomputeModule programFuncs elided
    recomputed
    // G108 (2): a fold over a deferred map of a deferred map reads the inner
    // map in place (fusion, next, splices it)
    |> spliceDeferredFoldChainsModule programFuncs
    // compute elision feeds its map consumers to fusion, its realization
    |> (fun m -> fuseElementwiseChainsModule m builder programFuncs)
    |> (fun m -> recordComputeElisionMapOutcomes pendingMaps m; recordRecomputeMapOutcomes pendingRecompute m; m)
    |> fusePartialRowTerminalsWithCensusModule builder programFuncs foreignCensus
    // PM29: a row fold of a decompacted pool reads the pool (no n x n buffer)
    |> decompactRowFoldModule builder programFuncs
    |> (fun m -> fuseSliceWritesModule m builder programFuncs)
    |> (fun m -> fuseRowFoldsModule m builder programFuncs)
    // PM35: right behind row-fold fusion (it consumes that pass's in-sweep
    // folds and the threaded-step table), before hoisting and sibling folds.
    |> (fun m -> windowRecFoldsModule m builder programFuncs foreignCensus singleModule)
    |> hoistKernelInvariantsModule builder programFuncs
    // LAST (it sees every fold the passes above left in place); before
    // IRLift and CSE, so the pass dedupes repeated legs itself.
    |> shareSiblingFoldsModule builder programFuncs
    // E2 / ST04: a stored producer whose only readers are one join's legs
    // becomes the join's shared leaf (no pool)
    |> elideJoinProducersModule programFuncs

/// `programFuncs` is every callable of the PROGRAM (all modules): a pass that
/// judges a callee's effects resolves it there, since a generic defined in
/// one module and called from another (`from stats import mean`) is not in
/// the calling module's own table.
/// singleModule: the program is this one module (compute elision's
/// top-level mode, which reads the print selection's names, runs only then).
let optimizeModule (builder: IRBuilder) (programFuncs: IRCallable list) (singleModule: bool) (modul: IRModule) : IRModule =
    optimizeModuleCore builder programFuncs singleModule None modul

/// Only the whole-program Lowering driver supplies this complete census.
let internal optimizeModuleWithForeignCensus (builder: IRBuilder) (programFuncs: IRCallable list) (singleModule: bool)
                                            (foreignRefs: Set<IRId>) (modul: IRModule) : IRModule =
    optimizeModuleCore builder programFuncs singleModule (Some foreignRefs) modul

// ---------------------------------------------------------------------------
// The surface other files name as `Optimize.<name>` (cleanup K19): the plan
// families live in the Optimize*.fs files compiled before this one; each name
// another file uses is re-exported here -- functions eta-expanded with their
// own parameters, values and lists as the same object, record and union types
// as abbreviations, side tables as facade modules over the same state.
// ---------------------------------------------------------------------------

// OptimizeGates
let poolReuseEnabled () = OptimizeGates.poolReuseEnabled ()
let earlyPoolReleaseEnabled () = OptimizeGates.earlyPoolReleaseEnabled ()
let epilogueInlineOptInDecline = OptimizeGates.epilogueInlineOptInDecline
let joinReportEnabled () = OptimizeGates.joinReportEnabled ()
let recWindowEnabled () = OptimizeGates.recWindowEnabled ()
let recReplayEnabled () = OptimizeGates.recReplayEnabled ()
let foldLanesSwitchedOff () = OptimizeGates.foldLanesSwitchedOff ()
let optimizerGates = OptimizeGates.optimizerGates
let optimizerGateMirrors : (string * string) list = OptimizeGates.optimizerGateMirrors
let cellEpilogueLicensedRowDecline (where: string) : string = OptimizeGates.cellEpilogueLicensedRowDecline where
let recordCellEpilogue (subject: string) (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) = OptimizeGates.recordCellEpilogue subject outcome evidence

// OptimizeFreezeIdiom
let recognizeFreezeIdiom (calleeAdmissible: string -> string option) (def: RecArrayDef) : RecArrayDef option = OptimizeFreezeIdiom.recognizeFreezeIdiom calleeAdmissible def

// OptimizeAnalysis
let irTypeExtentExprs (t: IRType) : IRExpr list = OptimizeAnalysis.irTypeExtentExprs t
let embeddedExtentExprs (e: IRExpr) : IRExpr list = OptimizeAnalysis.embeddedExtentExprs e
let isFunctionValuedType (t: IRType) : bool = OptimizeAnalysis.isFunctionValuedType t
type WriteFacts = OptimizeAnalysis.WriteFacts
let allocationFreeValueType (t: IRType) : bool = OptimizeAnalysis.allocationFreeValueType t
let holdsNoStorage (t: IRType) : bool = OptimizeAnalysis.holdsNoStorage t
let exprMayWriteWith (facts: WriteFacts) (root: IRExpr) : bool = OptimizeAnalysis.exprMayWriteWith facts root
let callbackTable (resolve: IRExpr -> IRCallable option) (funcs: IRCallable list) (bindings: (IRId * IRExpr) list) : Map<IRId, IRExpr list> = OptimizeAnalysis.callbackTable resolve funcs bindings
type WriteProvenance = OptimizeAnalysis.WriteProvenance
let callableWriteSummaryWith (facts: WriteFacts) (c: IRCallable) : WriteProvenance = OptimizeAnalysis.callableWriteSummaryWith facts c
let callableMayWriteWith (facts: WriteFacts) (c: IRCallable) : bool = OptimizeAnalysis.callableMayWriteWith facts c
let callableMayRetainWith (facts: WriteFacts) (c: IRCallable) : bool = OptimizeAnalysis.callableMayRetainWith facts c
let retentionCheckerWith (facts: WriteFacts) : Set<IRId> -> Set<IRId> -> IRExpr -> bool = OptimizeAnalysis.retentionCheckerWith facts
let bindingSideOperands (modul: IRModule) (id: IRId) : IRExpr list = OptimizeAnalysis.bindingSideOperands modul id
let bindingPayload (modul: IRModule) (b: IRBinding) : IRExpr list = OptimizeAnalysis.bindingPayload modul b
let bindingPayloadExpr (modul: IRModule) (b: IRBinding) : IRExpr = OptimizeAnalysis.bindingPayloadExpr modul b
let scheduleMayWriteIn (programFuncs: IRCallable list) (modul: IRModule) : IRExpr -> bool = OptimizeAnalysis.scheduleMayWriteIn programFuncs modul
let scheduleMayWriteWithCallbacksIn (programFuncs: IRCallable list) (modul: IRModule) : IRExpr -> bool = OptimizeAnalysis.scheduleMayWriteWithCallbacksIn programFuncs modul
let scheduleMayRetainIn (programFuncs: IRCallable list) (modul: IRModule) : Set<IRId> -> Set<IRId> -> IRExpr -> bool = OptimizeAnalysis.scheduleMayRetainIn programFuncs modul
let scheduleHasLifetimeBarrierIn (programFuncs: IRCallable list) (modul: IRModule) : IRExpr -> bool = OptimizeAnalysis.scheduleHasLifetimeBarrierIn programFuncs modul

// OptimizeCse, OptimizePoolReuse
let cseModule (modul: IRModule) : IRModule = OptimizeCse.cseModule modul
let planPoolReuse (mayRetain: Set<IRId> -> Set<IRId> -> IRExpr -> bool) (modul: IRModule) : unit = OptimizePoolReuse.planPoolReuse mayRetain modul

// OptimizeHoist
type HoistDeclSource = OptimizeHoist.HoistDeclSource
module HoistDeclTable =
    let reset () = OptimizeHoist.HoistDeclTable.reset ()
    let record (minted: IRId) (src: HoistDeclSource) = OptimizeHoist.HoistDeclTable.record minted src
    let all () : (IRId * HoistDeclSource) list = OptimizeHoist.HoistDeclTable.all ()

// OptimizeHotRow
type CellTerm = OptimizeHotRow.CellTerm
type HotRowForm = OptimizeHotRow.HotRowForm
type HotRowPlan = OptimizeHotRow.HotRowPlan
module HotRowTable =
    let reset () = OptimizeHotRow.HotRowTable.reset ()
    let tryProducer (id: IRId) : HotRowPlan option = OptimizeHotRow.HotRowTable.tryProducer id
    let tryConsumer (id: IRId) : HotRowPlan option = OptimizeHotRow.HotRowTable.tryConsumer id
type CellFoldEntry = OptimizeHotRow.CellFoldEntry
module CellFoldTable =
    let reset () = OptimizeHotRow.CellFoldTable.reset ()
    let tryLoop (id: IRId) : CellFoldEntry option = OptimizeHotRow.CellFoldTable.tryLoop id
    let tryFoldLet (id: IRId) : CellFoldEntry option = OptimizeHotRow.CellFoldTable.tryFoldLet id
type RecStepEntry = OptimizeHotRow.RecStepEntry
module RecStepTable =
    let reset () = OptimizeHotRow.RecStepTable.reset ()
    let tryLoop (id: IRId) : RecStepEntry option = OptimizeHotRow.RecStepTable.tryLoop id
    let tryStep (id: IRId) : RecStepEntry option = OptimizeHotRow.RecStepTable.tryStep id
    let undecided () : RecStepEntry list = OptimizeHotRow.RecStepTable.undecided ()
type RecStepFoldForm = OptimizeHotRow.RecStepFoldForm
let recStepFoldForm (strict: bool) (lanesOff: bool) (rowVec: bool) (fold: OptimizeHotRow.RecStepFold option) : RecStepFoldForm = OptimizeHotRow.recStepFoldForm strict lanesOff rowVec fold
let (|RecRingOfStep|_|) (n: IRId) (i0: IRExpr) : int64 option = OptimizeHotRow.(|RecRingOfStep|_|) n i0
let planHotRowFolds (builder: IRBuilder) (programFuncs: IRCallable list) (modul: IRModule) : unit = OptimizeHotRow.planHotRowFolds builder programFuncs modul

// OptimizeEpilogueLegs
type EpilogueLeg = OptimizeEpilogueLegs.EpilogueLeg
module EpilogueLegTable =
    let reset () = OptimizeEpilogueLegs.EpilogueLegTable.reset ()
    let tryProducer (id: IRId) : OptimizeEpilogueLegs.EpilogueLegPlan option = OptimizeEpilogueLegs.EpilogueLegTable.tryProducer id
    let tryJoin (id: IRId) : OptimizeEpilogueLegs.EpilogueLegPlan option = OptimizeEpilogueLegs.EpilogueLegTable.tryJoin id
module CallSpanTable =
    let reset () = OptimizeEpilogueLegs.CallSpanTable.reset ()
    let record (fid: IRId) (args: IRExpr list) (span: Blade.Ast.Span) = OptimizeEpilogueLegs.CallSpanTable.record fid args span
module InlineEventSpans =
    let reset () = OptimizeEpilogueLegs.InlineEventSpans.reset ()
let noteReleaseRetained (lane: string) (pools: IRExpr list) (call: string) = OptimizeEpilogueLegs.noteReleaseRetained lane pools call
let recordEpilogueLegs (subject: string) (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) = OptimizeEpilogueLegs.recordEpilogueLegs subject outcome evidence

// OptimizeStorage
type CompactionRoot = OptimizeStorage.CompactionRoot
let compactionRoots (callables: IRCallable list) (modul: IRModule) : CompactionRoot list = OptimizeStorage.compactionRoots callables modul
let moduleReferencedIds (modul: IRModule) : Set<IRId> = OptimizeStorage.moduleReferencedIds modul
let totalScalarOver (ps: IRParam list) (e: IRExpr) : bool = OptimizeStorage.totalScalarOver ps e
let allocFreeScalar (okVar: IRExpr -> bool) (e: IRExpr) : bool = OptimizeStorage.allocFreeScalar okVar e

// OptimizeMemberHoist
type MemberHoistPlan = OptimizeMemberHoist.MemberHoistPlan
module MemberHoistTable =
    let reset () = OptimizeMemberHoist.MemberHoistTable.reset ()
    let record (p: MemberHoistPlan) = OptimizeMemberHoist.MemberHoistTable.record p
    let plansOf (hostId: IRId) : MemberHoistPlan list = OptimizeMemberHoist.MemberHoistTable.plansOf hostId
    let tryProbe (declId: IRId) (setId: IRId) : MemberHoistPlan option = OptimizeMemberHoist.MemberHoistTable.tryProbe declId setId
    let all () : MemberHoistPlan list = OptimizeMemberHoist.MemberHoistTable.all ()
let memberRepName (k: int) : string = OptimizeMemberHoist.memberRepName k
let memberChooseRep (m: int64) (lo: int64) (hi: int64) (forced: int) : int = OptimizeMemberHoist.memberChooseRep m lo hi forced
let planMembershipHoist (programFuncs: IRCallable list) (modul: IRModule) : unit = OptimizeMemberHoist.planMembershipHoist programFuncs modul

// OptimizeCompaction
type CompactionGatherCpp = OptimizeCompaction.CompactionGatherCpp
type CompactionGatherLlvm = OptimizeCompaction.CompactionGatherLlvm
type SharedSelectionCpp = OptimizeCompaction.SharedSelectionCpp
type SharedSelectionLlvm = OptimizeCompaction.SharedSelectionLlvm
type SharedSelection = OptimizeCompaction.SharedSelection
type CompactionPlan = OptimizeCompaction.CompactionPlan
type CompactionFoldPlan = OptimizeCompaction.CompactionFoldPlan
module CompactionTable =
    let reset () = OptimizeCompaction.CompactionTable.reset ()
    let tryMask (id: IRId) : CompactionPlan option = OptimizeCompaction.CompactionTable.tryMask id
    let tryCompound (id: IRId) : CompactionPlan option = OptimizeCompaction.CompactionTable.tryCompound id
    let tryFoldMap (id: IRId) : CompactionFoldPlan option = OptimizeCompaction.CompactionTable.tryFoldMap id
    let foldsOf (compoundId: IRId) : CompactionFoldPlan list = OptimizeCompaction.CompactionTable.foldsOf compoundId
let fuseSelectedTerminalModule (builder: IRBuilder) (programFuncs: IRCallable list)
                               (foreignRefs: Set<IRId>) (modul: IRModule) : IRModule = OptimizeCompaction.fuseSelectedTerminalModule builder programFuncs foreignRefs modul
let planCompactionFusion (programFuncs: IRCallable list) (foreignRefs: Set<IRId>) (modul: IRModule) : unit = OptimizeCompaction.planCompactionFusion programFuncs foreignRefs modul

// OptimizeEquiJoin
type EquiJoinKeyRegime = OptimizeEquiJoin.EquiJoinKeyRegime
type EquiJoinPlan = OptimizeEquiJoin.EquiJoinPlan
module EquiJoinTable =
    let reset () = OptimizeEquiJoin.EquiJoinTable.reset ()
    let tryCompound (id: IRId) : EquiJoinPlan option = OptimizeEquiJoin.EquiJoinTable.tryCompound id
let joinChooseDirect (m: int64) (lo: int64) (hi: int64) (positional: bool) : bool = OptimizeEquiJoin.joinChooseDirect m lo hi positional
let planEquiJoin (programFuncs: IRCallable list) (foreignRefs: Set<IRId>) (modul: IRModule) : unit = OptimizeEquiJoin.planEquiJoin programFuncs foreignRefs modul

// OptimizeCompactRows
type CompactRowPlan = OptimizeCompactRows.CompactRowPlan
module CompactRowTable =
    let reset () = OptimizeCompactRows.CompactRowTable.reset ()
    let tryProducer (id: IRId) : CompactRowPlan option = OptimizeCompactRows.CompactRowTable.tryProducer id
    let tryConsumer (id: IRId) : CompactRowPlan option = OptimizeCompactRows.CompactRowTable.tryConsumer id
    let bodyMayFail (body: IRExpr) : bool = OptimizeCompactRows.CompactRowTable.bodyMayFail body
    let bodyDominated (plan: CompactRowPlan) (readVarId: IRId) (body: IRExpr) : bool = OptimizeCompactRows.CompactRowTable.bodyDominated plan readVarId body
let planCompactRowClasses (programFuncs: IRCallable list) (modul: IRModule) : unit = OptimizeCompactRows.planCompactRowClasses programFuncs modul

// OptimizeOrbit
module OrbitPullTable =
    let reset () = OptimizeOrbit.OrbitPullTable.reset ()
    let record (consumer: IRId) (source: IRId) (audit: Result<string list, string>) = OptimizeOrbit.OrbitPullTable.record consumer source audit
    let tryAudit (consumer: IRId) (source: IRId) : Result<string list, string> option = OptimizeOrbit.OrbitPullTable.tryAudit consumer source
module OrbitActionTable =
    type ReadAudit = OptimizeOrbit.OrbitActionTable.ReadAudit
    type Audit = OptimizeOrbit.OrbitActionTable.Audit
    let reset () = OptimizeOrbit.OrbitActionTable.reset ()
    let tryAudit (action: IRId) : Audit option = OptimizeOrbit.OrbitActionTable.tryAudit action
let planOrbitPull (programFuncs: IRCallable list) (modul: IRModule) : unit = OptimizeOrbit.planOrbitPull programFuncs modul
let planOrbitActions (programFuncs: IRCallable list) (modul: IRModule) : unit = OptimizeOrbit.planOrbitActions programFuncs modul

// OptimizeGroupAgg
type GroupAggRegime = OptimizeGroupAgg.GroupAggRegime
type GroupAggKind = OptimizeGroupAgg.GroupAggKind
type GroupAggState = OptimizeGroupAgg.GroupAggState
type GroupAggPlan = OptimizeGroupAgg.GroupAggPlan
module GroupAggTable =
    let reset () = OptimizeGroupAgg.GroupAggTable.reset ()
    let tryGk (id: IRId) : GroupAggPlan option = OptimizeGroupAgg.GroupAggTable.tryGk id
    let tryGroupBy (id: IRId) : GroupAggPlan option = OptimizeGroupAgg.GroupAggTable.tryGroupBy id
    let tryPeel (id: IRId) : (GroupAggPlan * int) option = OptimizeGroupAgg.GroupAggTable.tryPeel id
let planGroupAggregate (programFuncs: IRCallable list) (modul: IRModule) : unit = OptimizeGroupAgg.planGroupAggregate programFuncs modul

// OptimizeRecFold
let recFoldMentions (vid: IRId) (e: IRExpr) = OptimizeRecFold.recFoldMentions vid e
let recFoldMentionsInTypes (id: IRId) (e: IRExpr) = OptimizeRecFold.recFoldMentionsInTypes id e
let recFoldForeignNaming (callables: IRCallable list) (modul: IRModule) (foreign: Set<IRId> option) (singleModule: bool)
                         (currentFn: IRId) (currentBinding: IRId) (id: IRId) : string option = OptimizeRecFold.recFoldForeignNaming callables modul foreign singleModule currentFn currentBinding id
type RecFoldChannel = OptimizeRecFold.RecFoldChannel
let recFoldConstructorTable : (string * RecFoldChannel) list = OptimizeRecFold.recFoldConstructorTable

// OptimizePartialRow
let internal fusePartialRowTerminalsWithCensusModule (builder: IRBuilder) (programFuncs: IRCallable list)
                                                     (foreignCensus: Set<IRId> option) (modul: IRModule) : IRModule = OptimizePartialRow.fusePartialRowTerminalsWithCensusModule builder programFuncs foreignCensus modul
let fusePartialRowTerminalsModule (builder: IRBuilder) (programFuncs: IRCallable list) (modul: IRModule) : IRModule = OptimizePartialRow.fusePartialRowTerminalsModule builder programFuncs modul
type SparseWildcardFoldPlan = OptimizePartialRow.SparseWildcardFoldPlan
let certifySparseWildcardFold (wholeProgram: IRModule option) (binding: IRBinding)
                              (operand: IRExpr) (kernel: IRExpr) (seed: IRExpr option)
                              : Result<SparseWildcardFoldPlan, string> = OptimizePartialRow.certifySparseWildcardFold wholeProgram binding operand kernel seed

// OptimizeComputeElision
module CallElideProgram =
    let install (ids: Set<IRId>) = OptimizeComputeElision.CallElideProgram.install ids

// OptimizeCallElision
let programValueRefs (modules: IRModule list) : Set<IRId> = OptimizeCallElision.programValueRefs modules

// OptimizeTranspose
module AliasElideTable =
    let reset () = OptimizeTranspose.AliasElideTable.reset ()
    let contains (id: IRId) : bool = OptimizeTranspose.AliasElideTable.contains id
let normalizeTransposesModule (builder: IRBuilder) (programFuncs: IRCallable list) (foreignRefs: Set<IRId>) (singleModule: bool) (modul: IRModule) : IRModule = OptimizeTranspose.normalizeTransposesModule builder programFuncs foreignRefs singleModule modul
let planAliasElision (programFuncs: IRCallable list) (foreignRefs: Set<IRId>) (modul: IRModule) : unit = OptimizeTranspose.planAliasElision programFuncs foreignRefs modul
let recordAliasIdentity (programFuncs: IRCallable list) (foreignRefs: Set<IRId>) (modul: IRModule) : unit = OptimizeTranspose.recordAliasIdentity programFuncs foreignRefs modul

// OptimizeInPlace
module LuCompanionTable =
    let reset () = OptimizeInPlace.LuCompanionTable.reset ()
    let isFactor (id: IRId) : bool = OptimizeInPlace.LuCompanionTable.isFactor id
    let factorOfSolve (id: IRId) : IRId option = OptimizeInPlace.LuCompanionTable.factorOfSolve id
    let isLastSolve (id: IRId) : bool = OptimizeInPlace.LuCompanionTable.isLastSolve id
let sinkAliasEnabled () = OptimizeInPlace.sinkAliasEnabled ()
let recordProviderSink (subject: string) (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) = OptimizeInPlace.recordProviderSink subject outcome evidence
module GroupViewTable =
    let reset () = OptimizeInPlace.GroupViewTable.reset ()
    let tryGroup (id: IRId) = OptimizeInPlace.GroupViewTable.tryGroup id
    let tryUngroup (id: IRId) = OptimizeInPlace.GroupViewTable.tryUngroup id
    let noteEmitted (gid: IRId) (sourceCpp: string) = OptimizeInPlace.GroupViewTable.noteEmitted gid sourceCpp
    let tryEmitted (gid: IRId) = OptimizeInPlace.GroupViewTable.tryEmitted gid
let recordGroupView (subject: string) (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) = OptimizeInPlace.recordGroupView subject outcome evidence
let planStructuralGroupView (programFuncs: IRCallable list) (foreignRefs: Set<IRId>) (modul: IRModule) : unit = OptimizeInPlace.planStructuralGroupView programFuncs foreignRefs modul
module GroupRowMapTable =
    let reset () = OptimizeInPlace.GroupRowMapTable.reset ()
    let tryUngroup (id: IRId) = OptimizeInPlace.GroupRowMapTable.tryUngroup id
    let noteEmitted (hid: IRId) (cppName: string) = OptimizeInPlace.GroupRowMapTable.noteEmitted hid cppName
    let tryEmitted (hid: IRId) = OptimizeInPlace.GroupRowMapTable.tryEmitted hid
let recordGroupRowMap (subject: string) (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) = OptimizeInPlace.recordGroupRowMap subject outcome evidence
let planGroupRowMap (programFuncs: IRCallable list) (foreignRefs: Set<IRId>) (modul: IRModule) : unit = OptimizeInPlace.planGroupRowMap programFuncs foreignRefs modul

// OptimizeStreaming
module StreamGroupFoldTable =
    type Plan = OptimizeStreaming.StreamGroupFoldTable.Plan
    let reset () = OptimizeStreaming.StreamGroupFoldTable.reset ()
    let tryGroup (id: IRId) : Plan option = OptimizeStreaming.StreamGroupFoldTable.tryGroup id
let recordStreamGroupFold (subject: string) (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) = OptimizeStreaming.recordStreamGroupFold subject outcome evidence
let planStreamGroupFold (programFuncs: IRCallable list) (foreignRefs: Set<IRId>) (singleModule: bool) (modul: IRModule) : unit = OptimizeStreaming.planStreamGroupFold programFuncs foreignRefs singleModule modul
let streamFoldFuseEnabled () = OptimizeStreaming.streamFoldFuseEnabled ()
module MaskReplayTable =
    type Plan = OptimizeStreaming.MaskReplayTable.Plan
    let reset () = OptimizeStreaming.MaskReplayTable.reset ()
    let tryMask (id: IRId) : Plan option = OptimizeStreaming.MaskReplayTable.tryMask id
    let tryCompound (id: IRId) : Plan option = OptimizeStreaming.MaskReplayTable.tryCompound id
let planStreamMaskReplay (programFuncs: IRCallable list) (foreignRefs: Set<IRId>) (singleModule: bool) (modul: IRModule) : unit = OptimizeStreaming.planStreamMaskReplay programFuncs foreignRefs singleModule modul
module KeyedProjectionTable =
    type Plan = OptimizeStreaming.KeyedProjectionTable.Plan
    let reset () = OptimizeStreaming.KeyedProjectionTable.reset ()
    let tryRead (id: IRId) : Plan option = OptimizeStreaming.KeyedProjectionTable.tryRead id
    let tryCell (id: IRId) (k: int64) : string option = OptimizeStreaming.KeyedProjectionTable.tryCell id k
let recordKeyedProjection (subject: string) (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) = OptimizeStreaming.recordKeyedProjection subject outcome evidence
let planKeyedCellProjection (foreignRefs: Set<IRId>) (singleModule: bool) (modul: IRModule) : unit = OptimizeStreaming.planKeyedCellProjection foreignRefs singleModule modul
let haloCacheEnabled () = OptimizeStreaming.haloCacheEnabled ()
let haloCacheElementwiseEnabled () = OptimizeStreaming.haloCacheElementwiseEnabled ()
type SinkStreamPlan = OptimizeStreaming.SinkStreamPlan
module SinkStreamTable =
    let reset () = OptimizeStreaming.SinkStreamTable.reset ()
    let record (p: SinkStreamPlan) (inputs: Set<IRId>) = OptimizeStreaming.SinkStreamTable.record p inputs
    let tryProducer (id: IRId) = OptimizeStreaming.SinkStreamTable.tryProducer id
    let tryWrite (id: IRId) = OptimizeStreaming.SinkStreamTable.tryWrite id
    let remove (producerId: IRId) = OptimizeStreaming.SinkStreamTable.remove producerId
    let contains (id: IRId) = OptimizeStreaming.SinkStreamTable.contains id
let recordSinkStream (subject: string) (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) = OptimizeStreaming.recordSinkStream subject outcome evidence
let planProviderSinkStream (programFuncs: IRCallable list) (foreignRefs: Set<IRId>) (singleModule: bool) (modul: IRModule) : unit = OptimizeStreaming.planProviderSinkStream programFuncs foreignRefs singleModule modul

// OptimizeAssemblyDest
type AssemblyDestPlan = OptimizeAssemblyDest.AssemblyDestPlan
module AssemblyDestTable =
    let reset () = OptimizeAssemblyDest.AssemblyDestTable.reset ()
    let tryProducer (id: IRId) = OptimizeAssemblyDest.AssemblyDestTable.tryProducer id
    let forAssembly (id: IRId) = OptimizeAssemblyDest.AssemblyDestTable.forAssembly id
    let remove (producerId: IRId) = OptimizeAssemblyDest.AssemblyDestTable.remove producerId
    let contains (id: IRId) = OptimizeAssemblyDest.AssemblyDestTable.contains id
let recordAssemblyDest (subject: string) (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) = OptimizeAssemblyDest.recordAssemblyDest subject outcome evidence
let planAssemblyDest (programFuncs: IRCallable list) (foreignRefs: Set<IRId>) (singleModule: bool) (modul: IRModule) : unit = OptimizeAssemblyDest.planAssemblyDest programFuncs foreignRefs singleModule modul

// OptimizeSiblingMaps
type SiblingMapKind = OptimizeSiblingMaps.SiblingMapKind
type SiblingMapGroup = OptimizeSiblingMaps.SiblingMapGroup
module SiblingMapTable =
    let reset () = OptimizeSiblingMaps.SiblingMapTable.reset ()
    let tryMember (id: IRId) : (SiblingMapGroup * int) option = OptimizeSiblingMaps.SiblingMapTable.tryMember id
    let isProtected (id: IRId) : bool = OptimizeSiblingMaps.SiblingMapTable.isProtected id
let recordSiblingMaps (subject: string) (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) = OptimizeSiblingMaps.recordSiblingMaps subject outcome evidence
let planSiblingMaps (programFuncs: IRCallable list) (foreignRefs: Set<IRId>) (singleModule: bool) (modul: IRModule) : unit = OptimizeSiblingMaps.planSiblingMaps programFuncs foreignRefs singleModule modul
