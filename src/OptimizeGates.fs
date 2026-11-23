// The optimizer's pass gates and knob lists (split out of Optimize.fs, cleanup K19).
module Blade.OptimizeGates

open Blade.Types

open Blade.Ast
open Blade.IR
open Blade.IRMono

/// A default-ON pass gate: `0` / `off` / `false` disable it, anything else
/// (or unset) leaves it on. Read per call.
let internal gateOn (var: string) = Blade.Toolchain.envFlag var true

/// BLADE_FREEZE_IDIOM=0|off disables freeze-idiom recognition (the A/B
/// escape hatch, read per call like every other gate).
let freezeIdiomEnabled () = gateOn "BLADE_FREEZE_IDIOM"

/// BLADE_CSE=0|off disables let-level CSE (`cseModule`): the analysis still
/// runs, and a body it would have rewritten records `cse` DECLINED ("disabled
/// by BLADE_CSE") with the pairs it would have merged, so `blade plan` shows
/// the A/B.
let cseEnabled () = gateOn "BLADE_CSE"

/// BLADE_POOL_REUSE=0|off disables scratch reuse across barriers
/// (`planPoolReuse`): nothing is written to Types.PoolReuseTable, and a body
/// the plan would have applied to records `pool-reuse` DECLINED ("disabled
/// by BLADE_POOL_REUSE") with the pairs as evidence.
let poolReuseEnabled () = gateOn "BLADE_POOL_REUSE"


/// A05: exact ordinary-function scratch lifetime, independent of donation.
/// Read per lowering; the off arm keeps the original scope-exit cleanup.
let earlyPoolReleaseEnabled () = gateOn "BLADE_POOL_EARLY_RELEASE"

/// BLADE_HOIST=0|off disables kernel-invariant hoisting
/// (`hoistKernelInvariantsModule`): the analysis still runs, and a kernel it
/// would have rewritten records `invariant-hoist` DECLINED ("disabled by
/// BLADE_HOIST") with the values it would have moved out of the nest.
let hoistEnabled () = gateOn "BLADE_HOIST"

/// BLADE_HOT_ROW=0|off disables hot-row fold scheduling (`planHotRowFolds`):
/// the analysis still runs, and a producer/row-fold pair it would have
/// scheduled records `hot-row-fold` DECLINED ("disabled by BLADE_HOT_ROW");
/// nothing reaches the side table, so both emitters keep the two-pass form.
let hotRowEnabled () = gateOn "BLADE_HOT_ROW"

/// BLADE_CELL_EPILOGUE (round-2 cell epilogues, plan rule `cell-epilogue`):
/// a scalar consumer fold runs INSIDE its producer's cell loop instead of
/// re-reading the stored output afterwards -- the rank-1 producer epilogue
/// (B1: P1 streaming lane state licensed, P2 chain interleave repro) and the
/// per-row cell chain of a hot-row / row-fold-fusion row fold (B2r).
///   unset / `1` / `on` -> on
///   `lanes`            -> RESERVED for the opt-in licensed row arm (B2l),
///                         which is not built in v1: today `on` plus a
///                         recorded B2l decline at every licensed row fold
///   `0` / `off`        -> nothing reaches the side tables; every decision
///                         records `declined -- disabled by BLADE_CELL_EPILOGUE`
/// Read per call.
type CellEpilogueMode =
    | CellEpilogueOff
    | CellEpilogueOn
    | CellEpilogueLanes

let cellEpilogueMode () : CellEpilogueMode =
    match System.Environment.GetEnvironmentVariable "BLADE_CELL_EPILOGUE" with
    | null -> CellEpilogueOn
    | v ->
        match v.Trim().ToLowerInvariant() with
        | "0" | "off" | "false" | "no" -> CellEpilogueOff
        | "lanes" -> CellEpilogueLanes
        | _ -> CellEpilogueOn

let cellEpilogueEnabled () = cellEpilogueMode () <> CellEpilogueOff
/// BLADE_COMPACTION_FUSION (`planCompactionFusion`), five values:
///   unset / `1` / `on` -> compaction fusion v2 (round 3, T4b + T4c): the
///                         round-2 plan with the kept-position table ELIDED
///                         when the demand census proves no consumer reads
///                         it; the gather stays at the compound (B4's one
///                         pass); every compound over ONE mask shares ONE
///                         selection index (T4c), its gathers one pass
///   `gather`           -> v2 plus T4a (OPT-IN this round: its own measured
///                         gain, 0.18-0.36 ms on maskagg, is under the round's
///                         0.4 ms acceptance line): the gather -- and the
///                         strict chain -- in the mask pass, n + 1 private
///                         cells under BLADE_TILE_TUNE sel_cap (for a shared
///                         selection: J <= 4 companions, every source bound
///                         before the mask)
///   `unshared`         -> v2 WITHOUT T4c (the pre-T4c v2: one index per
///                         compound; a mask feeding several compounds fuses
///                         the last one only)
///   `b4`               -> the exact round-2 form (B4): count in the mask
///                         pass, ONE fill+gather pass at the compound,
///                         positions always built
///   `0` / `off`        -> nothing reaches the side table: every decision
///                         records `declined -- disabled by
///                         BLADE_COMPACTION_FUSION` and both emitters keep the
///                         separate count / fill / gather / pool passes
/// `b4`, `gather` and `unshared` are knob MIRRORS (`optimizerGateMirrors`):
/// opt-diff compares each against the default. Read per call.
type CompactionFusionMode =
    | CompactionFusionOff
    | CompactionFusionB4
    | CompactionFusionT4
    | CompactionFusionGather
    | CompactionFusionUnshared

let compactionFusionMode () : CompactionFusionMode =
    match Blade.Toolchain.envSwitch "BLADE_COMPACTION_FUSION" with
    | Blade.Toolchain.SwitchOff -> CompactionFusionOff
    | Blade.Toolchain.SwitchOther v when v.ToLowerInvariant() = "b4" -> CompactionFusionB4
    | Blade.Toolchain.SwitchOther v when v.ToLowerInvariant() = "gather" -> CompactionFusionGather
    | Blade.Toolchain.SwitchOther v when v.ToLowerInvariant() = "unshared" -> CompactionFusionUnshared
    | _ -> CompactionFusionT4
let compactionFusionEnabled () = compactionFusionMode () <> CompactionFusionOff

/// T4a's selectivity cap (BLADE_TILE_TUNE `sel_cap`, in KiB; default 262144 =
/// 256 MiB): the single-pass form reserves n + 1 private cells in EVERY
/// compound buffer (values, and the kept positions when they are built)
/// before m is known, so it is taken only while those bytes fit the cap --
/// at compile time for a static n, by a run-time test otherwise; above it the
/// two-pass form (B4) runs. Speed only: both forms build the same compound.
/// `sel_cap=0` never takes the single pass. The KiB-to-byte product is
/// checked (clamped at Int64.MaxValue).
let compactionSelCapBytes () : int64 =
    let kib = max 0L (Blade.Toolchain.tileTune "sel_cap" 262144L)
    if kib > System.Int64.MaxValue / 1024L then System.Int64.MaxValue else kib * 1024L

/// BLADE_COMPACT_ROW_CLASSES=0|off disables compact row-class scheduling
/// (`planCompactRowClasses`): a compact rank-2 producer / orbit-fold pair it
/// would have scheduled records `compact-row-classes` DECLINED ("disabled by
/// BLADE_COMPACT_ROW_CLASSES"); nothing reaches the side table, so both
/// emitters keep the producer nest plus the separate orbit-class pass.
let compactRowClassesEnabled () = gateOn "BLADE_COMPACT_ROW_CLASSES"
/// BLADE_SIBLING_FOLDS=0|off disables sibling-fold traversal sharing
/// (`shareSiblingFoldsModule`): the analysis still runs, and a group it would
/// have joined records `sibling-folds` DECLINED ("disabled by
/// BLADE_SIBLING_FOLDS") with what it would have joined.
let siblingFoldsEnabled () = gateOn "BLADE_SIBLING_FOLDS"
/// The MIXED-LICENCE arm of the sibling-fold pass (gap G107 (2), round 4
/// wave 4), read per call: unset / on / anything else = `split` (licensed and
/// strict legs each share their own traversal); `strict` = the opt-in mirror
/// (`optimizerGateMirrors`) joining every leg in ONE strict traversal.
let siblingFoldsLicenceArm () : string =
    match System.Environment.GetEnvironmentVariable "BLADE_SIBLING_FOLDS" with
    | null -> "split"
    | v when v.Trim().ToLowerInvariant() = "strict" -> "strict"
    | _ -> "split"
/// The OPT-IN top-level region of the sibling-fold pass (gap G107 (3)):
/// `BLADE_SIBLING_FOLDS=top` (an `optimizerGateMirrors` arm). Not the default:
/// joining separate top-level folds trades a licensed fold's own form (the
/// K-lane dot tree, a lone fold's -0.0) for the join's `omp simd reduction`
/// form -- a visible licensed change the corpus pins (sibling-folds/003) and
/// an unmeasured cost against the dot class.
let siblingFoldsTopEnabled () : bool =
    match System.Environment.GetEnvironmentVariable "BLADE_SIBLING_FOLDS" with
    | null -> false
    | v -> v.Trim().ToLowerInvariant() = "top"
/// BLADE_REC_STEP_OMP=0|off keeps a `where omp` request on a `let rec` step's
/// FINAL map out of slice-write fusion (wave 3, E-G5): the requesting map
/// then runs as its own per-step team and the row is copied, exactly as
/// before; the pass records `rec-step-parallel` DECLINED ("disabled by
/// BLADE_REC_STEP_OMP") for the step it would have admitted. Unset = on.
let recStepOmpEnabled () = gateOn "BLADE_REC_STEP_OMP"
/// BLADE_EPILOGUE_LEGS=0|off keeps a producer's INPUT legs (round 3 A7c, gap
/// G69: sibling folds over the operands of a rank-1 `|> compute` producer
/// whose cell epilogue B1 places) out of the producer's fused loop: the
/// legs keep their own joined pass, and the call holding the producer is not
/// inlined for them. The admission still runs and records `epilogue-legs`
/// DECLINED ("disabled by BLADE_EPILOGUE_LEGS") with its five-party matrix.
/// Unset = on.
let epilogueLegsEnabled () = gateOn "BLADE_EPILOGUE_LEGS"
/// The KEPT ONE-LEVEL INLINE (a plan whose producer sits in a callee the legs'
/// statement calls, saxpy's `kernel(x, y)`) is OPT-IN: only an EXPLICIT
/// `BLADE_EPILOGUE_LEGS=1` (`1` / `on` / `true`) retains it. Its lifetime audit is
/// INCOMPLETE (G69, the seventh diff look: a fold kernel a non-owned callee
/// unit names is not audited, and the LLVM lane may expand an allocating call
/// inside it with no pool scope), so unset keeps every plan whose producer is
/// already in the region and declines the inline-needing ones. Per call.
/// The decline text of an inline-needing plan in the default build.
let epilogueInlineOptInDecline = "the kept one-level inline is opt-in (BLADE_EPILOGUE_LEGS=1): its lifetime audit is INCOMPLETE (G69: a fold kernel named by a non-owned unit is not audited); the legs stay in their joined pass and the call stays"
let epilogueLegsInlineOptIn () = Blade.Toolchain.envSwitch "BLADE_EPILOGUE_LEGS" = Blade.Toolchain.SwitchOn
/// BLADE_HASH_JOIN=0|off keeps an inline equijoin compound
/// (`compound(products_map, mask(equal_keys_map, identity))`, round 3 W3-2,
/// item T2, `planEquiJoin`) in the as-written six passes over all N x M pairs
/// (the `==` Bool pool, the identity mask, the bit-vector copy, the hashed
/// rank-2 index, the payload pool, the scatter). The admission still runs and
/// records `hash-join` DECLINED ("disabled by BLADE_HASH_JOIN") with its
/// five-party matrix. Unset = on.
let hashJoinEnabled () = gateOn "BLADE_HASH_JOIN"
/// BLADE_JOIN_REPORT=1|on: COMPILE-TIME instrumentation of the hash join (not
/// an optimizer gate -- no value, schedule or store changes): the C++ lane
/// EMITS a `[join] ...` build report (stderr) after each join's counting
/// pass, printed unconditionally at run time by that executable. Unset / `0`
/// = no report code at all (the default: a run-time switch would cost a
/// `getenv` inside the timed region). Read by the compiler only, per call;
/// `blade plan` names the setting in the `hash-join` record; the suite clears
/// it. BLADE_MEMBER_VERBOSE stays the membership builds' RUN-TIME switch and
/// has no effect on the join.
let joinReportEnabled () = Blade.Toolchain.envFlag "BLADE_JOIN_REPORT" false
/// BLADE_JOIN_SELECTION=1|on: the hash join census also certifies a
/// SELECTION over J (round 4 wave 4 lane H, H1 / ST15): `mask(J, p)` bound
/// whole to a top-level name and `compound(J, keep)` / `compound(J, mask(J, p))`
/// read J by its compact buffer and cardinality only, so J keeps the CSR and
/// its cardinality-only index. DEFAULT ON since round 4 wave 6 (the fresh
/// class-closure review of the census class found no member; wave 4 found a
/// block-local mask, declined since). 0 / off = a mask over J demands the
/// tuple table (the as-written passes, the old default). Read per call; an
/// optimizer gate (opt-diff compares off vs on).
let joinSelectionEnabled () = Blade.Toolchain.envFlag "BLADE_JOIN_SELECTION" true
/// BLADE_COMPUTE_ELIDE=0|off keeps every function- and block-body `|> compute`
/// stored (`elideSingleConsumerComputesModule`): the admission still runs and
/// a compute it would have elided records `compute-elision` DECLINED
/// ("disabled by BLADE_COMPUTE_ELIDE"). Unset = on.
let computeElideEnabled () = gateOn "BLADE_COMPUTE_ELIDE"
/// BLADE_GATHER_ELIDE=0|off keeps a GATHER producer (`method_for(idx) <@>
/// lambda(i) -> A(i) |> compute`, one checked subscript) stored before its
/// fold (round 4 wave 3, PM28, `compute-elision`'s gather-terminal arm,
/// `gatherTerminalCertificate`): the certificate still runs and records
/// DECLINED ("disabled by BLADE_GATHER_ELIDE"). Unset = on (it also needs
/// BLADE_COMPUTE_ELIDE).
let gatherElideEnabled () = gateOn "BLADE_GATHER_ELIDE"
/// BLADE_FOLD_MAP_ELIDE=0|off keeps the FOLD-OF-MAP consumer out of compute
/// elision (round 4 wave 2, PM12, rule `fold-map-elision`): a `|> compute` read
/// only by the operand map of one fold (`reduce(c * c, (+)) / n`) stays stored and
/// records compute elision's previous decline, byte for byte. Unset = on (it also
/// needs BLADE_COMPUTE_ELIDE and BLADE_FUSION, its realization).
let foldMapElideEnabled () = gateOn "BLADE_FOLD_MAP_ELIDE"
/// BLADE_SIBLING_MAPS=0|off keeps every stored map its own traversal (round 4
/// wave 2, PM04, rule `sibling-maps`; `planSiblingMaps`): the admission still
/// runs and a group it would have fused records DECLINED ("disabled by
/// BLADE_SIBLING_MAPS"). Unset = on.
let siblingMapsEnabled () = gateOn "BLADE_SIBLING_MAPS"
/// The SibHaloStream kind (round 4 wave 4, E1 / G106): one segment run loop
/// for halo stencils over one streamed Zarr variable. DEFAULT ON since round 4
/// wave 7 (lane c: a fresh class-closure round under R0 found no member;
/// measured on PM37's two FIRs). `BLADE_SIBLING_MAPS=nostream` = the old
/// default (resident groups only; a knob mirror); `streamed` reads as on.
let siblingMapsStreamedEnabled () =
    match System.Environment.GetEnvironmentVariable "BLADE_SIBLING_MAPS" with
    | null -> true
    | v -> v.Trim().ToLowerInvariant() <> "nostream"
/// BLADE_CALL_ELIDE=0|off keeps every call as a call (`elideCallsModule`, the
/// P4 entry): the admission still runs and a call it would have jointly
/// lowered records `call-elision` DECLINED ("disabled by BLADE_CALL_ELIDE").
/// Unset = on.
let callElideEnabled () = gateOn "BLADE_CALL_ELIDE"
/// BLADE_REC_WINDOW=0|off keeps every block-level `let rec` at its full
/// history (TypeCheckInfer.recArrayWindowDecision, the PM33/PM34 ring): the
/// decision records `rec-window` DECLINED ("disabled by BLADE_REC_WINDOW").
/// Read by the TYPECHECKER (the desugar site), per call. Unset = on.
let recWindowEnabled () = gateOn "BLADE_REC_WINDOW"

/// BLADE_REC_REPLAY=0|off keeps every MODULE-level `let rec` at its full
/// history (rec-window's module arm, the PM33/PM34 ring with PRINT REPLAY,
/// round 4 wave 4 B1): the typechecker adds no `__replay_<x>` sweep and no
/// snapshot bindings, recording `rec-window` DECLINED ("disabled by
/// BLADE_REC_REPLAY"). Block and function bodies keep BLADE_REC_WINDOW alone.
let recReplayEnabled () = gateOn "BLADE_REC_REPLAY"
/// BLADE_FOLD_STATE=0|off keeps the leading-axis fold's per-step state
/// (`let nxt = kernel(acc, G(j)) |> compute; acc = nxt`, a fresh slice every
/// step) instead of the in-place pointwise update (round 4 wave 2, PM14;
/// rule `fold-state-in-place`, an arm of `fuseSliceWritesModule`, so it is
/// also off under BLADE_FUSION=0). Unset = on.
let foldStateEnabled () = gateOn "BLADE_FOLD_STATE"
/// BLADE_REC_FOLD rings a recursion whose trajectory is consumed only by
/// folds and moves those folds into the sweep (`windowRecFoldsModule`, round
/// 4 wave 2, PM35; rule `rec-fold-ring`). DEFAULT ON since round 4 wave 6
/// (gap G94: the fresh class-closure review of the EVALUATION class found no
/// member; the naming class is closed by the reflected constructor table).
/// `0` / `off` = the full history: the admission still runs and records
/// what it WOULD do, `declined -- disabled by BLADE_REC_FOLD`.
let recFoldRingEnabled () = gateOn "BLADE_REC_FOLD"
let recFoldOptInDecline = "disabled by BLADE_REC_FOLD"
/// `BLADE_FOLD_LANES=0|off|false|no`: the fold-lane forms are off. The ONE
/// parse both the C++ emitters (CodeGenExprSupport.foldLanesMode) and the
/// optimizer's ring planner read (a threaded step's row partials need them).
let foldLanesSwitchedOff () =
    match System.Environment.GetEnvironmentVariable "BLADE_FOLD_LANES" with
    | null -> false
    | v -> (match v.Trim().ToLowerInvariant() with "0" | "off" | "false" | "no" -> true | _ -> false)

/// Every gate of this layer (charter rule 3), in one place: the optimizer
/// differential (`blade test opt-diff`, tests/OptDiff.fs) turns ALL of them
/// off for its reference lane, so a new pass's gate belongs in this list the
/// day the pass lands -- a gate missing here is a pass the differential
/// cannot see.
let optimizerGates = [ "BLADE_FUSION"; "BLADE_FREEZE_IDIOM"; "BLADE_CSE"; "BLADE_POOL_REUSE"; "BLADE_POOL_EARLY_RELEASE"; "BLADE_HOIST"; "BLADE_HOT_ROW"; "BLADE_CELL_EPILOGUE"; "BLADE_COMPACTION_FUSION"; "BLADE_COMPACT_ROW_CLASSES"; "BLADE_SIBLING_FOLDS"; "BLADE_REC_STEP_OMP"; "BLADE_MEMBER_HOIST"; "BLADE_EPILOGUE_LEGS"; "BLADE_ORBIT_ACTIONS"; "BLADE_HASH_JOIN"; "BLADE_GROUP_AGG"; "BLADE_COMPUTE_ELIDE"; "BLADE_TRANSPOSE_ELIDE"; "BLADE_CALL_ELIDE"; "BLADE_RECOMPUTE"; "BLADE_REC_WINDOW"; "BLADE_GROUP_VIEW"; "BLADE_SINK_ALIAS"; "BLADE_SINK_STREAM"; "BLADE_HALO_CACHE"; "BLADE_FOLD_MAP_ELIDE"; "BLADE_SIBLING_MAPS"; "BLADE_FOLD_STATE"; "BLADE_REC_FOLD"; "BLADE_GATHER_ELIDE"; "BLADE_DECOMPACT_FOLD"; "BLADE_SORT_FORM"; "BLADE_JOIN_AGG"; "BLADE_JOIN_SELECTION"; "BLADE_REC_REPLAY"; "BLADE_AD_GATHER_FUSE"; "BLADE_BLOCK_FLOAT"; "BLADE_GROUP_ROWMAP"; "BLADE_ASSEMBLY_DEST"; "BLADE_MODE_PRODUCT_DEST"; "BLADE_JOIN_PRODUCER"; "BLADE_STREAM_GROUP_FOLD"; "BLADE_KEYED_PROJECTION"; "BLADE_STREAM_MASK_REPLAY"; "BLADE_LU_COMPANION"; "BLADE_SORT_TERMINAL"; "BLADE_STREAM_FOLD_FUSE"; "BLADE_AD_CHECKPOINT"; "BLADE_STREAM_GROUP_AGG"; "BLADE_AD_REPLAY_PRUNE" ]

/// A gate's KNOB MIRROR: a value that keeps a pass's PREVIOUS version, or
/// selects an opt-in variant (not the pass off), for the passes whose forms
/// extend one another.
/// `blade test opt-diff` emits every program a third way -- the defaults
/// with this one gate at the mirror value -- and compiles + compares the
/// pairs whose C++ differs from the default, so a new version is checked
/// against its predecessor as well as against no optimizer at all (the
/// all-off comparison alone never runs the predecessor). Each gate here is
/// also in `optimizerGates`.
let optimizerGateMirrors : (string * string) list = [ ("BLADE_COMPACTION_FUSION", "b4"); ("BLADE_COMPACTION_FUSION", "gather"); ("BLADE_COMPACTION_FUSION", "unshared"); ("BLADE_EPILOGUE_LEGS", "1"); ("BLADE_POOL_REUSE", "literal"); ("BLADE_SINK_STREAM", "always"); ("BLADE_SIBLING_FOLDS", "strict"); ("BLADE_SIBLING_FOLDS", "top"); ("BLADE_COMPUTE_ELIDE", "chain"); ("BLADE_MODE_PRODUCT_DEST", "1"); ("BLADE_HALO_CACHE", "stencil"); ("BLADE_SIBLING_MAPS", "nostream"); ("BLADE_DECOMPACT_FOLD", "top"); ("BLADE_AD_CHECKPOINT", "3"); ("BLADE_STREAM_GROUP_AGG", "1"); ("BLADE_AD_REPLAY_PRUNE", "1") ]

/// The decline reason for a LICENSED row prodsum (B2r applies only where
/// the build keeps the serial association). `lanes` RESERVES the opt-in
/// licensed arm (B2l), which is not built in v1: the mode is `on` plus this
/// decline, worded so `blade plan` says the request was seen.
let cellEpilogueLicensedRowDecline (where: string) : string =
    match cellEpilogueMode () with
    | CellEpilogueLanes ->
        $"BLADE_CELL_EPILOGUE=lanes requests the licensed row arm (B2l), which is reserved and not built in v1: the licensed row prodsum stays the K-lane form on {where}"
    | _ ->
        $"licensed build: the row's prodsum takes the K-lane form on {where}, which a per-cell chain does not reproduce (the opt-in B2l arm, `lanes`, is reserved and not built in v1)"

/// One `cell-epilogue` v1 decision record (the planner's source-level
/// declines; the emitters record the per-mode applied / declined outcome).
let recordCellEpilogue (subject: string) (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) =
    Blade.Effects.Decisions.record
        { Blade.Effects.Rule = "cell-epilogue"; Version = 1
          Span = Blade.Ast.noSpan; Subject = subject
          Outcome = outcome; Evidence = evidence }
