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
// the single pipeline entry over them; new passes land in this file.
module Blade.Optimize

open Blade.Types

open Blade.Ast
open Blade.IR
open Blade.IRMono

/// A default-ON pass gate: `0` / `off` / `false` disable it, anything else
/// (or unset) leaves it on. Read per call.
let private gateOn (var: string) = Blade.Toolchain.envFlag var true

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
/// The C++ lane's BLAS gate (`LinAlgPatterns.blasAvailable`, which compiles
/// after this file), installed by Lowering before the optimizer runs: the
/// epilogue-legs planner keeps an inline only when the cell epilogue it rides
/// is not left to the L1 dispatch. A FUNCTION (read per call), never a cached
/// value; the default answers "off".
let mutable blasGateProbe : unit -> bool = fun () -> false

/// Every gate of this layer (charter rule 3), in one place: the optimizer
/// differential (`blade test opt-diff`, tests/OptDiff.fs) turns ALL of them
/// off for its reference lane, so a new pass's gate belongs in this list the
/// day the pass lands -- a gate missing here is a pass the differential
/// cannot see.
let optimizerGates = [ "BLADE_FUSION"; "BLADE_FREEZE_IDIOM"; "BLADE_CSE"; "BLADE_POOL_REUSE"; "BLADE_POOL_EARLY_RELEASE"; "BLADE_HOIST"; "BLADE_HOT_ROW"; "BLADE_CELL_EPILOGUE"; "BLADE_COMPACTION_FUSION"; "BLADE_COMPACT_ROW_CLASSES"; "BLADE_SIBLING_FOLDS"; "BLADE_REC_STEP_OMP"; "BLADE_MEMBER_HOIST"; "BLADE_EPILOGUE_LEGS" ]

/// A gate's KNOB MIRROR: a value that keeps a pass's PREVIOUS version, or
/// selects an opt-in variant (not the pass off), for the passes whose forms
/// extend one another.
/// `blade test opt-diff` emits every program a third way -- the defaults
/// with this one gate at the mirror value -- and compiles + compares the
/// pairs whose C++ differs from the default, so a new version is checked
/// against its predecessor as well as against no optimizer at all (the
/// all-off comparison alone never runs the predecessor). Each gate here is
/// also in `optimizerGates`.
let optimizerGateMirrors : (string * string) list = [ ("BLADE_COMPACTION_FUSION", "b4"); ("BLADE_COMPACTION_FUSION", "gather"); ("BLADE_COMPACTION_FUSION", "unshared") ]

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

// --- Freeze-idiom recognition (plan-match-statements.md section 5, R7/B) ---
//
// The hand-written convergence idiom on a recursive array's inductive arm:
//
//   | prefix :: n -> prefix :: (if G then STEP else prefix(n - 1))
//
// declares a fixed point: once G is false the slice repeats. When G's only
// per-iteration inputs are reads of prefix(n - 1), falseness is ABSORBING --
// the frozen slice reproduces exactly the inputs the guard just judged
// false, so it stays false by induction -- and the remaining iterations are
// provably copies. Rewriting the definition to the guarded form WITHOUT the
// abort (`Guard = Some G, Slice = STEP`, best-effort) then derives the early
// exit and the freeze epilogue from machinery that already exists, and the
// emitted values are byte-identical to running the budget out: the epilogue
// writes the same repeated slice the else-arm would have written, and every
// skipped guard evaluation is a repeat of one that already completed (G is
// pure surface arithmetic; its inputs no longer change).
//
// This is cost-only by construction, which is the R7 boundary: the `while`
// spelling OPTS INTO the must-converge contract (BL8010 when the budget
// runs out); the recognized idiom keeps if/else's contract -- run to
// budget, freeze if done early, never abort. Same analysis, same break,
// different contract, chosen by spelling.
//
// Soundness demands three shape checks, all CONSERVATIVE (any unrecognized
// node declines recognition rather than guessing):
//   - the else-arm is EXACTLY `prefix(n - 1)` (the whole previous slice --
//     an else that repairs, decays, or reads deeper lags is a live arm, not
//     a freeze);
//   - the guard's prefix reads are all at lag 1, and it references neither
//     the step ordinal outside those reads (a guard varying with `n`
//     independently of the trajectory is NOT absorbing: `n < k` flips on
//     its own) nor the bare prefix family;
//   - every call the guard makes is to a PURE scalar intrinsic (the caller
//     decides which names qualify -- see `calleeAdmissible` below). The
//     skipped guard evaluations are only repeats if evaluating the guard
//     changes nothing: a guard that calls a helper with a `mut` parameter
//     mutates its own input, so its first false answer is not absorbing
//     (plan-fortran-killer-2.md appendix A: `if tick(counter) then ...`
//     froze the trajectory at 1 and dropped six of seven `tick` calls), and
//     a helper that prints or aborts has effects the freeze would drop even
//     when its value would repeat. A user-declared function -- pure or not
//     -- therefore declines today: this seam sees names, not resolved
//     bodies, and an invariant NAME does not make a call repeatable. The
//     P0 follow-up (plan-fortran-killer-2.md section 3) is to keep the
//     candidate and discharge purity against the typed callee, which would
//     re-admit provably pure helpers.

/// `e` is syntactically `<stepVar> - 1`.
let private isStepMinusOne (stepVar: Ident) (e: Expr) : bool =
    match e.Kind with
    | ExprBinOp (Elementwise, OpSub, l, r) ->
        (match l.Kind, r.Kind with
         | ExprVar sv, ExprLit (LitInt 1L) -> sv = stepVar
         | _ -> false)
    | _ -> false

/// `e` is syntactically `prefix(<stepVar> - 1)` -- the whole previous slice.
let private isPrevSliceRead (prefixVar: Ident) (stepVar: Ident) (e: Expr) : bool =
    match e.Kind with
    | ExprApp (h, [arg]) ->
        (match h.Kind with
         | ExprVar pv -> pv = prefixVar && isStepMinusOne stepVar arg
         | _ -> false)
    | _ -> false

/// Guard admissibility: every read of the prefix is at lag 1, the step
/// ordinal appears ONLY inside those lag expressions, the prefix family is
/// never referenced bare, every ordinary call's head satisfies
/// `calleeAdmissible` (a REPEATABLE callee -- an unshadowed scalar intrinsic
/// or a declared function whose effect summary allows a repeat; the caller
/// supplies the judgment because the name and summary tables live in
/// TypeEnv, downstream of this file), and the whole guard is built from the
/// shapes a convergence predicate uses (literals, variables, arithmetic/
/// comparison/boolean operators, unary ops, applications, ascriptions).
/// Anything else -- a lambda, a block, a match -- declines recognition
/// conservatively. Returns None when admissible, else the FIRST reason it is
/// not, which the decision record carries.
let rec private guardInadmissible (calleeAdmissible: string -> string option)
                                  (prefixVar: Ident) (stepVar: Ident) (e: Expr) : string option =
    let check = guardInadmissible calleeAdmissible prefixVar stepVar
    let firstOf (xs: Expr list) = xs |> List.tryPick check
    match e.Kind with
    | ExprLit _ -> None
    | ExprVar v when v = stepVar -> Some "the guard reads the step ordinal outside a lag-1 prefix read (not absorbing: `n < k` flips on its own)"
    | ExprVar v when v = prefixVar -> Some "the guard references the prefix family bare"
    | ExprVar _ -> None
    | ExprBinOp (_, _, l, r) -> firstOf [l; r]
    | ExprUnaryOp (_, x) -> check x
    | ExprTyped (x, _) -> check x
    | ExprApp _ ->
        // Peel the application spine: `prefix(n-1)(j)(k)` is nested
        // ExprApps whose base head is the prefix var and whose FIRST
        // argument list carries the lag.
        let rec spine (f: Expr) (argLists: Expr list list) =
            match f.Kind with
            | ExprApp (h, args) -> spine h (args :: argLists)
            | _ -> f, argLists
        let baseHead, argLists = spine e []
        (match baseHead.Kind with
         | ExprVar pv when pv = prefixVar ->
             (match argLists with
              | (lagArg :: restFirst) :: deeper ->
                  if not (isStepMinusOne stepVar lagArg) then
                      Some "the guard reads the prefix at a lag other than 1"
                  else firstOf (restFirst @ List.concat deeper)
              | _ -> Some "the guard references the prefix family bare")
         | ExprVar fn when fn = stepVar -> Some "the guard applies the step ordinal"
         | ExprVar fn ->
             // An ordinary call. Evaluating a REPEATABLE callee again on the
             // same inputs gives the same value and changes nothing, so the
             // skipped evaluations really are repeats; the arguments carry
             // the lag discipline. Anything else -- a helper that mutates a
             // `mut` argument, prints, reads a file, or calls unknown code --
             // is refused with the caller's reason.
             (match calleeAdmissible fn with
              | Some why -> Some $"the guard calls `{fn}`, which is not repeatable: {why}"
              | None -> firstOf (List.concat argLists))
         | _ -> Some "the guard applies something other than a name")
    | _ -> Some "the guard contains a shape recognition does not read (a lambda, a block, or a match)"

/// Recognize the freeze idiom on an UNGUARDED recursive-array definition and
/// repartition it into the guarded best-effort form. Returns None (leave the
/// definition alone -- it still compiles and still means the same thing) for
/// anything that is not exactly the idiom.
///
/// `calleeAdmissible name` is the caller's judgment of a plain call to
/// `name`: None when it is repeatable (an unshadowed scalar intrinsic, or a
/// declared function whose Blade.Effects summary is repeatable), else the
/// reason it is not. The guard may call nothing else.
///
/// Every CANDIDATE -- an unguarded inductive arm whose slice is an `if` --
/// leaves a record in Blade.Effects.Decisions (rule `freeze-recognition`,
/// v2: v1 admitted callees by name), applied with its discharged
/// obligations or declined with the first reason. A slice that is not an
/// `if` is not a candidate and records nothing.
let recognizeFreezeIdiom (calleeAdmissible: string -> string option) (def: RecArrayDef) : RecArrayDef option =
    match def.Guard with
    | Some _ -> None
    | None ->
        match def.SliceExpr.Kind with
        | ExprIf (g, stepExpr, elseExpr) ->
            let decide (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) =
                Blade.Effects.Decisions.record
                    { Blade.Effects.Rule = "freeze-recognition"; Version = 2
                      Span = def.SliceExpr.Span; Subject = def.Name
                      Outcome = outcome; Evidence = evidence }
            if not (freezeIdiomEnabled ()) then
                decide (Blade.Effects.Declined "disabled by BLADE_FREEZE_IDIOM") []
                None
            elif not (isPrevSliceRead def.PrefixVar def.StepVar elseExpr) then
                decide (Blade.Effects.Declined "the else-arm is not exactly `prefix(n - 1)` (a live arm, not a freeze)") []
                None
            else
                match guardInadmissible calleeAdmissible def.PrefixVar def.StepVar g with
                | Some why ->
                    decide (Blade.Effects.Declined why) []
                    None
                | None ->
                    decide Blade.Effects.Applied
                        [ "else-arm is `prefix(n - 1)`"
                          "guard reads the prefix at lag 1 only and never the step ordinal"
                          "every callee in the guard is repeatable"
                          "contract kept: best-effort freeze, no BL8010 budget abort" ]
                    Some { def with Guard = Some g; SliceExpr = stepExpr }
        | _ -> None

// --- Pipeline entry -------------------------------------------------------

/// The IR-level optimization stage, run per module in Lowering after the
/// monomorphizers and the array-binop rewrite, before inline-form lifting:
/// constant-scrutinee match folding (which also resolves symbolic ranks per
/// specialization), then elementwise-chain fusion. One entry point so the
/// pipeline reads as a stage, and so a new pass has one obvious place to
/// join.
/// The SEGMENT-STREAMING decision (docs/plans/structural/07 §3.4; the
/// principle the user stated: a computation over a streamed, segmented
/// variable is examined as a whole before `compute`, and the compiler --
/// not a task graph -- chooses between reading the store one run at a
/// time and materializing). Today the choice is by consumer SHAPE and is
/// recorded, not costed: a fold walks the store block by block in storage
/// order (bitwise the flat fold), a `group_by` under a structural grouping
/// reads one run per group, a key grouping needs the whole variable. The
/// emission lives in codegen; this pass only says what it will do, so
/// `blade plan` shows it.
/// The streamed variable an expression reads, looking through a lifted
/// kernel: the kernel of an apply is a reference to a callable in the
/// module's function table, and the source it reads is one of that
/// callable's CAPTURES (by the outer binding's id), not a var in the
/// expression itself.
let private streamedReadOf (modul: IRModule) (streamed: Map<Blade.Types.IRId, ProviderReadSpec>) (value: IRExpr) : ProviderReadSpec option =
    let mutable found = None
    iterIRExpr (fun e ->
        match e with
        | IRVar (vid, _) when Map.containsKey vid streamed -> found <- Some streamed.[vid]
        | IRVar (fid, _) ->
            (match modul.Functions |> List.tryFind (fun f -> f.Id = fid) with
             | Some f ->
                 (match f.Captures |> List.tryFind (fun c -> Map.containsKey c.Id streamed) with
                  | Some c -> found <- Some streamed.[c.Id]
                  | None -> ())
             | None -> ())
        | _ -> ()) value
    found

let private recordSegmentStreaming (modul: IRModule) : unit =
    let streamed =
        modul.ProviderReads
        |> Map.filter (fun _ s -> s.Streamed && not s.VarType.IndexTypes.IsEmpty)
    if not (Map.isEmpty streamed) then
        let structural =
            modul.Bindings
            |> List.choose (fun b -> match b.Value with IRSegments _ | IRSegmentsGrid _ -> Some b.Id | _ -> None)
            |> Set.ofList
        // The one number a cost model needs first: what materializing the
        // variable would hold in memory (literal extents only; a symbolic
        // extent says so). The choice itself stays by consumer shape.
        let materializedBytes (s: ProviderReadSpec) : string =
            let elem =
                match stripUnits s.VarType.ElemType with
                | IRTScalar (ETFloat64 | ETInt64) -> Some 8L
                | IRTScalar (ETFloat32 | ETInt32) -> Some 4L
                | IRTScalar ETComplex128 -> Some 16L
                | IRTScalar ETComplex64 -> Some 8L
                | IRTScalar ETBool -> Some 1L
                | _ -> None
            let cells =
                s.VarType.IndexTypes |> List.fold (fun acc ix ->
                    match acc, ix.Extent with
                    | Some a, IRLit (IRLitInt n) -> Some (a * n)
                    | _ -> None) (Some 1L)
            match elem, cells with
            | Some e, Some c -> $"materialized it would hold {e * c} B"
            | _ -> "materialized size not static"
        let decide (subject: string) (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) =
            Blade.Effects.Decisions.record
                { Blade.Effects.Rule = "segment-streaming"; Version = 2
                  Span = Blade.Ast.noSpan; Subject = subject
                  Outcome = outcome; Evidence = evidence }
        for b in modul.Bindings do
            match b.Value with
            | IRReduce (IRVar (vid, _), _, _) when Map.containsKey vid streamed ->
                let s = streamed.[vid]
                decide b.Name Blade.Effects.Applied
                    [ $"fold over the streamed variable '{s.VarName}' walks the store one block at a time, in storage order: the same operation sequence as the flat fold, so the answer is bitwise the materialized one"
                      materializedBytes s ]
            | value when
                    (let mutable halo = false
                     iterIRExpr (fun e ->
                         match e with
                         | IRRange ([ ix ], _) when (match ix.Tag with Some t -> t.StartsWith Blade.Types.haloWinTagPrefix | None -> false) -> halo <- true
                         | _ -> ()) value
                     halo && (streamedReadOf modul streamed value).IsSome) ->
                let s = (streamedReadOf modul streamed value).Value
                decide b.Name Blade.Effects.Applied
                    [ $"the stencil over the streamed variable '{s.VarName}' runs one segment at a time; each run is read with the ghost cells its halo reach demands, nothing else of the variable is ever in memory"
                      materializedBytes s ]
            | value when
                    (let mutable found = false
                     iterIRExpr (fun e ->
                         match e with
                         | IRApplyCombinator info when info.Arrays |> List.exists (function IRVar (vid, _) -> Map.containsKey vid streamed | _ -> false) -> found <- true
                         | _ -> ()) value
                     found) ->
                // every apply in the binding (a zip nested under a reduce
                // included) whose OPERANDS are streamed is an elementwise
                // consumer run one block at a time
                iterIRExpr (fun e ->
                    match e with
                    | IRApplyCombinator info ->
                        (match info.Arrays |> List.tryPick (function IRVar (vid, _) when Map.containsKey vid streamed -> Some streamed.[vid] | _ -> None) with
                         | Some s ->
                             decide b.Name Blade.Effects.Applied
                                 [ $"the elementwise consumer of the streamed variable '{s.VarName}' runs one block of the store's chunk edge at a time (a band of rows above rank 1), each block its own window; nothing else of the variable is ever in memory"
                                   materializedBytes s ]
                         | None -> ())
                    | _ -> ()) value
            | IRGroupBy (IRVar (vid, _), IRVar (gid, _)) when Map.containsKey vid streamed ->
                let s = streamed.[vid]
                if Set.contains gid structural then
                    decide b.Name Blade.Effects.Applied
                        [ $"group_by over the streamed variable '{s.VarName}' under a structural grouping reads one run per group straight into its row; the whole variable is never materialized"
                          materializedBytes s ]
                else
                    decide b.Name (Blade.Effects.Declined "a key grouping needs every cell before any row is known")
                        [ $"'{s.VarName}' is streamed but the grouping is by keys; bind it with .read" ]
            | _ -> ()

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

/// The shared fresh-pool classification (IR.isFreshPoolFormWith -- the same
/// definition codegen's escape analysis uses). A CALL is never fresh here: the
/// planner cannot see what a callee hands back.
let private isFreshForm (e: IRExpr) : bool = isFreshPoolFormWith (fun _ -> false) e

let planPoolReuse (modul: IRModule) : unit =
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
            for r in collectVarRefsIR e do work.Push r
            while work.Count > 0 do
                let id = work.Pop()
                if not (Set.contains id acc) then
                    acc <- Set.add id acc
                    (match Map.tryFind id funcs with
                     | Some g -> for c in g.Captures do work.Push c.Id
                     | None -> ())
                    (match Map.tryFind id letValues with
                     | Some dv when deferred dv -> for r in collectVarRefsIR dv do work.Push r
                     | _ -> ())
            acc
        let n = lets.Length
        let positions = (lets |> List.map (fun (id, v) -> (Some id, v))) @ [ (None, ret) ]
        let refsAt = positions |> List.map (fun (_, v) -> refsOf v) |> Array.ofList
        let valueAt = positions |> List.map snd |> Array.ofList
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
                match densePoolShape (typeOf (collapse v)) with
                | Some (elem, exts) ->
                    let bytes = (poolReuseElemBytes elem |> Option.defaultValue 0L) * (exts |> List.fold (*) 1L)
                    Some (elem, exts, bytes)
                | None -> None
        let candidates = [ for k in 0 .. n -> (k, candidateAt k) ] |> List.choose (fun (k, c) -> c |> Option.map (fun c -> (k, c)))
        if candidates.Length >= 2 then
            // A donor's storage must never be retained by anyone: every
            // position naming it is a fresh-pool form or scalar-typed, and no
            // assignment anywhere names it.
            let assignedIds =
                let mutable s = Set.empty
                for (_, v) in positions do
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
            let callsCallable (v: IRExpr) =
                let mutable found = false
                iterIRExpr (fun e ->
                    match e with
                    | IRApp (IRVar (fid, _), _, _) when Map.containsKey fid funcs -> found <- true
                    | _ -> ()) v
                found
            let unaliased (k: int) (a: IRId) =
                not (Set.contains a assignedIds)
                && [ 0 .. n ] |> List.forall (fun p ->
                    p = k
                    || not (Set.contains a refsAt.[p])
                    || (match positions.[p] with
                        | (Some _, pv) when deferred pv -> true      // reads attributed to its consumers
                        | (_, pv) -> not (callsCallable pv) && (isFreshForm pv || scalarValued pv)))
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
            for (k, (elem, exts, _)) in candidates do
                let donor =
                    candidates
                    |> List.filter (fun (r, (relem, rexts, _)) ->
                        r < k && relem = elem && rexts = exts
                        && pools.ContainsKey r
                        && (match idAt r with Some a -> unaliased r a | None -> false)
                        && deadFrom pools.[r] k)
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
            let bytesOf (p: int) = candidates |> List.tryPick (fun (q, (_, _, b)) -> if q = p then Some b else None) |> Option.defaultValue 0L
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
                    (pairs |> List.ofSeq |> List.map (fun (k, r) -> $"{nameOf k} would take {nameOf r}'s dead pool ({bytesOf k} B)"))
            elif pairs.Count > 0 then
                for (k, r) in pairs do
                    match idAt k with
                    | Some id -> Blade.Types.PoolReuseTable.record id (idAt r).Value
                    | None -> Blade.Types.PoolReuseTable.recordReturn f.Id (idAt r).Value
                decide f.SourceName Blade.Effects.Applied
                    ((pairs |> List.ofSeq |> List.map (fun (k, r) -> $"{nameOf k} takes {nameOf r}'s dead pool ({bytesOf k} B)"))
                     @ [ $"pools {candidates.Length} -> {pools.Count}; peak live pool bytes {before} -> {after} (literal extents only)" ])
            else
                decide f.SourceName (Blade.Effects.Declined "no candidate pool is both dead and unaliased when a pool of the same shape is allocated")
                    [ $"{candidates.Length} candidate pools, peak live pool bytes {before}" ]

/// A kernel reference resolved through the module's own function table
/// (the AsyncLocal CallablesTable is not installed at this point in the
/// pipeline, as the fusion pass notes).
let private resolveCallableIn (funcs: Map<IRId, IRCallable>) (k: IRExpr) : IRCallable option =
    match k with
    | IRVar (fid, _) -> Map.tryFind fid funcs
    | _ -> None

/// LET-LEVEL COMMON-SUBEXPRESSION ELIMINATION over REPEATABLE values -- the
/// P0 consumer plan-fortran-killer-2 section 3 listed ("reuse the facts for
/// ... later CSE") and did not build. Straight-line only: inside one
/// function body, a let whose value is structurally identical to an EARLIER
/// let's value is dropped and every later reference reads the earlier one.
/// Equality of values is equality of results only when
///
///   * the value is REPEATABLE: no assignment, no display, no call whose
///     callee is unknown or not repeatable. A callee is judged by its
///     `Blade.Effects` summary (exactly as the fusion pass reads it) and,
///     for a lifted lambda (which carries no summary -- `Unknown`), by its
///     own body under the same rules; a kernel REFERENCE (the callable an
///     apply or reduce runs per cell) is judged like a call, and a
///     function-typed name the module cannot resolve (a let alias of a
///     lambda, a function parameter, another module's function) is the
///     worst case. A read of a DEFERRED let inherits its producer's facts:
///     the producer runs at the read;
///   * NOTHING BETWEEN THE TWO EVALUATIONS CAN WRITE what the value reads.
///     A body containing an assignment or a loop declines wholesale (cheap
///     and sound). Every other write reaches a body through a CALL -- a
///     callee assigning through a `mut` parameter, or to a module-level
///     `let mut` it names -- so a let whose value may write (a callee whose
///     summary says Mutates, or whose effects are unknown) is a BARRIER:
///     nothing computed before it is reused after it;
///   * neither let is NAMED by a writing evaluation anywhere in the body
///     (directly, or through a callable or deferred let it names): that is
///     the storage a `mut` argument writes, and merging it with its twin
///     would let the write reach a name the program never passed. (Not
///     IRModule.MutableArrayLets: in a function body a plain array `let`
///     arrives there too -- it is reassignable in its own scope, so the
///     checker marks it mutable -- and the table cannot tell a `let mut`
///     apart (observed: both plain lets of a two-map body are listed). An
///     assignment in the body already declines the whole body.)
///
/// A MayFail value is fine: the first evaluation already ran, so the second
/// could not newly fail. Deferred lets (a bare apply left for a join) are
/// never touched -- their identity is the join's sharing declaration, and
/// dropping one would change what the join emits. Trivial values (a
/// literal, a variable) are not worth a record.
///
/// Identity is STRUCTURAL equality of the IR, never a rendering of it (a
/// `%A` key printed floats at ~10 significant digits and truncated deep
/// trees, so `b * 0.1` and `b * 0.10000000001` compared equal). Float
/// literals compare by their BIT PATTERN (so `0.0` and `-0.0` stay apart),
/// and two callables are the same value when their whole records agree once
/// their own ids, names and parameter ids are erased -- computed lazily, for
/// the callables a candidate value actually names.
///
/// Every body with a hit records `cse` (applied, the pairs); with BLADE_CSE
/// off the same body records `cse` declined, naming the pairs it would have
/// merged; nothing otherwise.
type private CseFacts = {
    /// Evaluating it may change a value another evaluation reads.
    MayWrite: bool
    /// Evaluating it again, with the same inputs, gives the same value and
    /// no other observable effect.
    Repeatable: bool
}

/// The CSE judge over one module: facts of an expression, callees resolved
/// through the module's own function table and memoized per callable. A
/// callable met again while its own facts are being computed (recursion)
/// reads as the worst case -- pessimistic, so a cycle can only decline.
let private isFunctionTyped (t: IRType) : bool =
    match stripUnits t with
    // a NULLARY function is `IRTArrow([], ret, None)` (Types / IR reserve the
    // empty-slot arrow for it): a function; an arrow whose slots are ALL index
    // slots is an array typed as its index function, not a callable value
    | IRTArrow (slots, _, _) -> slots.IsEmpty || slots |> List.exists (function SVal _ -> true | _ -> false)
    | _ -> false

/// Public twin of isFunctionTyped: a FUNCTION-valued type (an arrow with a
/// value slot, or the nullary empty-slot arrow), not an array typed as an
/// index arrow. The callback tables key only these parameters.
let isFunctionValuedType (t: IRType) : bool = isFunctionTyped t

let private cseFactsOf (funcs: Map<IRId, IRCallable>) : IRExpr -> CseFacts =
    let worst = { MayWrite = true; Repeatable = false }
    let memo = System.Collections.Generic.Dictionary<IRId, CseFacts>()
    let rec ofCallable (c: IRCallable) : CseFacts =
        match memo.TryGetValue c.Id with
        | true, r -> r
        | _ ->
            memo.[c.Id] <- worst
            let r =
                if not c.Effects.Unknown then
                    { MayWrite = c.Effects.Mutates; Repeatable = Blade.Effects.isRepeatable c.Effects }
                else ofExpr c.Body
            // A static function's call is not a runtime value this pass
            // reasons about (the old rule, kept).
            let r = if c.IsStatic then { r with Repeatable = false } else r
            memo.[c.Id] <- r
            r
    and ofExpr (e: IRExpr) : CseFacts =
        let mutable mayWrite = false
        let mutable repeatable = true
        iterIRExpr (fun n ->
            match n with
            | IRAssign _ | IRForRange _ ->
                mayWrite <- true
                repeatable <- false
            | IRBeginPoolScope _ | IRReleasePools _
            | IRDisplayEmit _ | IRDisplayJson _ | IRDisplayNum _ | IRDisplayStr _ ->
                repeatable <- false
            // An unfilled let-rec buffer is an IDENTITY (the storage the
            // recursion writes), never a value two lets could share.
            | IRUninit _ ->
                repeatable <- false
            // A callable named anywhere -- a call head, a kernel, a callable
            // passed as an argument -- may run: judge it. (The IRApp arm
            // below leaves a resolvable head to this visit.)
            | IRVar (fid, t) ->
                (match Map.tryFind fid funcs with
                 | Some c ->
                    let cf = ofCallable c
                    if cf.MayWrite then mayWrite <- true
                    if not cf.Repeatable then repeatable <- false
                 | None ->
                    // A FUNCTION-typed name this module cannot resolve -- a
                    // let alias of a lifted lambda (`let k = lambda ..`), a
                    // function-typed parameter, another module's function
                    // used as a kernel -- may run and may do anything.
                    if isFunctionTyped t then
                        mayWrite <- true
                        repeatable <- false)
            | IRParam (_, _, t) when isFunctionTyped t ->
                mayWrite <- true
                repeatable <- false
            | IRApp (IRVar (fid, _), _, _) when Map.containsKey fid funcs -> ()
            // A head this module cannot resolve -- a lambda-valued variable,
            // a higher-order parameter, another module's function -- may do
            // anything.
            | IRApp _ ->
                mayWrite <- true
                repeatable <- false
            | _ -> ()) e
        { MayWrite = mayWrite; Repeatable = repeatable }
    ofExpr

/// Where an array value's STORAGE comes from, as far as a write through it is
/// concerned: storage made inside the walked body (`Fresh` -- a store into it
/// is invisible outside), the storage of some of the walked callable's
/// parameters (`Params`, by index -- the CALLER decides what that is), or
/// anything else (`External`: a captured or module binding, a view of one, a
/// call's array result, a form not proved fresh).
type WriteProvenance = { External: bool; Params: Set<int> }

/// The facts the write analysis reads from its context (gaps G41 / G43).
type WriteFacts = {
    /// A callee or kernel reference -> its callable (aliases already chased).
    Resolve: IRExpr -> IRCallable option
    /// An OUTER let's initializer (one not bound inside the walked body): the
    /// analysis follows deferred computations through it -- a named unforced
    /// map runs where it is read -- and stops at a materialization boundary
    /// (`|> compute`, a stored value).
    OuterLet: IRId -> IRExpr option
    /// `let mut a = Z` over a dense array deep-copies Z (genVarAliasBinding /
    /// Interp.Core): such a let owns fresh storage.
    CopiedLet: IRId -> bool
    /// Does an UNRESOLVED call head or function-typed kernel name count as a
    /// write? true for every decline and for the join refusal (a reordered or
    /// joined evaluation must be PROVED write-free); false only for the
    /// parallel-body refusal, which refuses what it can SEE (the front end
    /// already judges a parallel body's calls).
    UnknownIsWrite: bool
    /// A CALLBACK -- a function-valued parameter, by id -- to the argument
    /// expressions every call site passes for it (None: not a known
    /// callback). The analysis JOINS every candidate's effects at each
    /// application (Codex finding 22: never one candidate chosen on a
    /// boolean); a candidate it cannot resolve contributes top under
    /// UnknownIsWrite.
    Callbacks: IRId -> IRExpr list option
}

/// The summary of one callable: does a call write storage the caller can
/// observe regardless of its arguments (`External`), and which of its
/// parameters' storage does it write (`Params`)?
let private writeNone : WriteProvenance = { External = false; Params = Set.empty }
let private writeJoin (a: WriteProvenance) (b: WriteProvenance) =
    { External = a.External || b.External; Params = Set.union a.Params b.Params }

/// A value of this type holds no array storage (scalars, units, index tags,
/// tuples of those). Public for the C++ kernel-body copy (gap G46).
let holdsNoStorage (t: IRType) : bool =
    let rec go (t: IRType) =
        match stripUnits t with
        | IRTScalar _ | IRTIdxTagged _ | IRTUnit | IRTNat _ -> true
        | IRTTuple ts -> ts |> List.forall go
        | _ -> false
    go t

let private isDeferredForm (e: IRExpr) : bool =
    match e with
    | IRApplyCombinator _ | IRComposeApply _ | IRFusion _ | IRParallel _
    | IRComposeObj _ | IRComposeMeth _ | IRBind _ | IRFunctorMap _ | IRZip _
    | IRMethodFor _ | IRObjectFor _
    // wrappers codegen keeps deferred too (CodeGenBinding's deferred
    // registration; EmitLlvm's virtual choice / guard producers): their
    // operands run where the wrapper is read
    | IRChoice _ | IRFallback _ | IRGuard _ | IRSequence _ -> true
    // an object_for application is a VIRTUAL array in the LLVM lane
    // (objectForApply): a call-shaped head is not proof the value was stored
    | IRApp (IRObjectFor _, _, _) -> true
    | _ -> false

/// THE WRITE ANALYSIS (gaps G41 / G43; exprMayWriteWith / callableMayWriteWith): does evaluating `root` write storage
/// that another evaluation can observe? Shared by codegen's reorder licence
/// (CodeGenExprSupport.callableMayWrite / exprMayWrite: a writing kernel or
/// map body is never reordered) and Lowering's join-leg refusal. One policy:
///   * STORAGE PROVENANCE, not initializer syntax: an assignment is local only
///     when its target's storage is proved fresh inside the walked body -- an
///     array literal, a materialized map, a let-rec buffer, a scalar, a
///     `let mut` deep copy, or an alias chain / view / field / tuple
///     projection of such a local. A view of a captured array (`let t = G(0)`),
///     a call's array result (`let t = get_G()`), anything not proved fresh is
///     external.
///   * ARGUMENT-AWARE calls: a callee's summary says which parameters it
///     writes; the write lands on the provenance of the caller's argument, so
///     a callee filling the caller's fresh scratch writes nothing observable.
///   * a callable named as a KERNEL (map, fold, inner reduce) writes
///     observably when it writes anything outside its own body, its
///     parameters included (a kernel's parameters are its operands' cells).
///   * FLOW-INSENSITIVE over rebinding: a local's (or parameter's) provenance
///     is the join of its initializer and EVERY value later assigned to it or
///     stored into one of its fields / elements (`t = G(0)` makes `t` a view
///     of G for every store through it), so a `let` initializer is never
///     taken as lifetime ownership.
///   * a named DEFERRED computation read here is evaluated here: followed
///     through `OuterLet`, let aliases, tuple projections and every deferred
///     wrapper (choice, fallback, guard, sequence included), until a
///     materialization boundary (`|> compute`, a stored value).
///   * an UNRESOLVED call head (a function-typed value, a curried result) or
///     an unresolved function-typed name in a KERNEL slot may do anything:
///     counted as a write (a decline declines, a refusal refuses an
///     unproved-pure evaluation).
/// Cycles: a callable met again while its own summary is computed answers
/// "writes every array parameter" (sound: its own direct writes are found on
/// the first visit); a deferred let is walked once.
let private writeAnalyzer (facts: WriteFacts) =
    let summaries = System.Collections.Generic.Dictionary<IRId, WriteProvenance>()
    let rec summaryOf (c: IRCallable) : WriteProvenance =
        match summaries.TryGetValue c.Id with
        | true, s -> s
        | _ ->
            // RECURSION: the least fixpoint, from "writes nothing" up (a may
            // analysis: a recursive call adds only what the body itself
            // does). The previous pessimistic seed -- "writes every array
            // parameter" -- polluted every self-recursive pure function that
            // forwards an array (examples/mandelbrot.bladenb's `esc_pert`),
            // and through argument-aware calls turned a captured operand into
            // a refused parallel write. Summaries computed DURING an iteration
            // may depend on this one's current value, so they are dropped and
            // recomputed on the next iteration; the last iteration's stay.
            let paramIdx =
                c.Params |> List.indexed |> List.collect (fun (i, p) -> [ (p.VarId, i) ]) |> Map.ofList
            let paramNames = c.Params |> List.indexed |> List.map (fun (i, p) -> (p.Name, i)) |> Map.ofList
            let allStorage =
                { External = false
                  Params = c.Params |> List.indexed |> List.filter (fun (_, p) -> not (holdsNoStorage p.Type)) |> List.map fst |> Set.ofList }
            // TOP: writes outside AND every storage parameter (Codex finding
            // 23: the old budget fallback kept External = false and never
            // re-ran the transfer, so a long parameter-rotating recursion
            // that reaches a capture on its last step was missed).
            let top = { allStorage with External = true }
            let rec iterate (current: WriteProvenance) (round: int) =
                summaries.[c.Id] <- current
                let before = System.Collections.Generic.HashSet<IRId>(summaries.Keys)
                let s = writeJoin current (walkBody paramIdx paramNames c.Body)
                // summaries first computed during this round read `current`;
                // drop them unless `current` is the final answer
                let dropNew () =
                    for k in List.ofSeq summaries.Keys do
                        if k <> c.Id && not (before.Contains k) then summaries.Remove k |> ignore
                if s = current then s
                elif round >= 64 then
                    // not converging within the budget: widen to TOP; the
                    // summaries built on the unfinished value are recomputed
                    dropNew ()
                    top
                else
                    dropNew ()
                    iterate s (round + 1)
            let s = iterate writeNone 0
            summaries.[c.Id] <- s
            s
    /// The writes of one body, as a provenance summary over its parameters.
    and walkBody (paramIdx: Map<IRId, int>) (paramNames: Map<string, int>) (body: IRExpr) : WriteProvenance =
        let locals = System.Collections.Generic.Dictionary<IRId, IRExpr>()
        let walkedLets = System.Collections.Generic.HashSet<IRId>()
        iterIRExpr (fun n -> match n with IRLet (id, v, _) -> locals.[id] <- v | _ -> ()) body
        // Every value a local or parameter can come to hold: its initializer
        // plus each later whole rebinding, and each storage-bearing value
        // stored into one of its fields / elements (a reference stored in an
        // aggregate is projected back out later).
        let rebinds = System.Collections.Generic.Dictionary<IRId, ResizeArray<IRExpr>>()
        let rec rootVar (t: IRExpr) =
            match t with
            | IRIndex (b, _, _) | IRFieldAccess (b, _) | IRTupleProj (b, _, _) -> rootVar b
            | IRVar (id, _) -> Some id
            | _ -> None
        iterIRExpr (fun n ->
            match n with
            | IRAssign (target, v) ->
                (match rootVar target with
                 | Some id when locals.ContainsKey id || paramIdx.ContainsKey id ->
                     let whole = (match target with IRVar _ -> true | _ -> false)
                     let carries = whole || not (try holdsNoStorage (typeOf v) with _ -> false)
                     if carries then
                         (match rebinds.TryGetValue id with
                          | true, l -> l.Add v
                          | _ -> rebinds.[id] <- ResizeArray [ v ])
                 | _ -> ())
            | _ -> ()) body
        let rec prov (seen: Set<IRId>) (e: IRExpr) : WriteProvenance =
            let scalarTyped = try holdsNoStorage (typeOf e) with _ -> false
            let rebound (seen: Set<IRId>) (id: IRId) =
                match rebinds.TryGetValue id with
                | true, vs -> vs |> Seq.map (prov (seen.Add id)) |> Seq.fold writeJoin writeNone
                | _ -> writeNone
            match e with
            | IRVar (id, _) when paramIdx.ContainsKey id ->
                if seen.Contains id then writeNone
                else writeJoin { writeNone with Params = Set.singleton paramIdx.[id] } (rebound seen id)
            | IRParam (nm, _, _) when paramNames.ContainsKey nm -> { writeNone with Params = Set.singleton paramNames.[nm] }
            | IRVar (id, _) when locals.ContainsKey id ->
                if seen.Contains id then writeNone
                else
                    let init = locals.[id]
                    let fromInit =
                        match init with
                        | IRVar _ when facts.CopiedLet id -> writeNone
                        | _ -> prov (seen.Add id) init
                    writeJoin fromInit (rebound seen id)
            | _ when scalarTyped -> writeNone
            | IRVar _ | IRParam _ -> { writeNone with External = true }
            | IRIndex (b, _, _) | IRFieldAccess (b, _) | IRSlice (b, _, _, _) | IRShift (b, _, _, _)
            | IRReverse (b, _) | IRCurry (b, _, _) -> prov seen b
            | IRTupleProj (IRTuple es, i, false) when i >= 0 && i < es.Length -> prov seen es.[i]
            | IRTupleProj (b, _, _) -> prov seen b
            | IRTuple es -> es |> List.map (prov seen) |> List.fold writeJoin writeNone
            | IRIf (_, a, b) -> writeJoin (prov seen a) (prov seen b)
            | IRMatch (_, cases) -> cases |> List.map (fun c -> prov seen c.Body) |> List.fold writeJoin writeNone
            | IRLet (_, _, b) -> prov seen b
            | _ when isFreshPoolFormWith (fun _ -> false) e -> writeNone
            | _ -> { writeNone with External = true }
        // The storage an assignment target writes: strip the element / field /
        // projection steps down to the base expression.
        let rec targetBase (t: IRExpr) =
            match t with
            | IRIndex (b, _, _) | IRFieldAccess (b, _) | IRTupleProj (b, _, _) -> targetBase b
            | other -> other
        let mutable acc = writeNone
        let add (p: WriteProvenance) = acc <- writeJoin acc p
        let rec visit (e: IRExpr) =
            if not acc.External then
                match e with
                | IRAssign (target, value) ->
                    (match target with
                     // Rebinding a name: local when the name is the body's own
                     // (a let or a parameter), an observable write otherwise.
                     | IRVar (id, _) when locals.ContainsKey id || paramIdx.ContainsKey id -> ()
                     | IRVar _ | IRParam _ -> add { writeNone with External = true }
                     | _ -> add (prov Set.empty (targetBase target)))
                    visitChildren target
                    visit value
                | IRApp (f, args, _) ->
                    // The application of `h` to THIS call's `args`; a callback
                    // head applies EVERY candidate, effects joined.
                    let rec callAt (seenCb: Set<IRId>) (h: IRExpr) =
                        match facts.Resolve h with
                        | Some c ->
                            let s = summaryOf c
                            if s.External then add { writeNone with External = true }
                            for k in s.Params do
                                if k < args.Length then add (prov Set.empty args.[k])
                                else add { writeNone with External = true }
                        | None ->
                            match h with
                            | IRVar (cid, _) when (facts.Callbacks cid).IsSome ->
                                // a cycle among callbacks adds nothing new: the
                                // other candidates of the cycle are joined too
                                if not (seenCb.Contains cid) then
                                    for cand in (facts.Callbacks cid).Value do callAt (seenCb.Add cid) cand
                            // a combinator / loop-object head: its kernels are
                            // visited as references below
                            | IRObjectFor _ | IRMethodFor _ | IRCompose _ | IRComposeObj _ | IRComposeMeth _ -> visit h
                            | _ -> if facts.UnknownIsWrite then add { writeNone with External = true }
                    callAt Set.empty f
                    args |> List.iter visit
                | IRVar (id, vty) ->
                    // A callable REFERENCE (a kernel slot, a callable passed
                    // on): its parameters are cells of its operands, so a
                    // parameter write is observable too. A callback joins all
                    // of its candidates. true = judged as a callable.
                    let rec kernelRef (seenCb: Set<IRId>) (h: IRExpr) : bool =
                        match facts.Resolve h with
                        | Some c ->
                            let s = summaryOf c
                            if s.External || not s.Params.IsEmpty then add { writeNone with External = true }
                            true
                        | None ->
                            match h with
                            | IRVar (cid, _) when (facts.Callbacks cid).IsSome ->
                                if not (seenCb.Contains cid) then
                                    for cand in (facts.Callbacks cid).Value do
                                        if not (kernelRef (seenCb.Add cid) cand) then
                                            if facts.UnknownIsWrite then add { writeNone with External = true }
                                true
                            | _ -> false
                    if kernelRef Set.empty e then ()
                    else
                         // an unresolved FUNCTION-typed name (a higher-order
                         // parameter or value in a kernel slot) may do anything
                         if isFunctionTyped vty then
                             if facts.UnknownIsWrite then add { writeNone with External = true }
                         elif not (locals.ContainsKey id) && not (paramIdx.ContainsKey id) && walkedLets.Add id then
                             // a named deferred computation runs where it is read
                             let rec chase (seen: Set<IRId>) (x: IRExpr) =
                                 match x with
                                 | IRVar (j, _) when not (seen.Contains j) ->
                                     (match facts.OuterLet j with
                                      | Some v -> chase (seen.Add j) v
                                      | None -> None)
                                 // a projection of a deferred pack runs the pack
                                 | IRTupleProj (b, _, _) -> chase seen b
                                 // an array VIEW (partial index `m(i)`, slice,
                                 // shift, reverse, curry) of a deferred value is
                                 // virtual too: reading it runs the producer
                                 // (EmitLlvm rowView keeps AVirt / AVirtFlat).
                                 // An explicit `|> compute` stays the boundary.
                                 | IRIndex (b, _, _) | IRSlice (b, _, _, _) | IRShift (b, _, _, _)
                                 | IRReverse (b, _) | IRCurry (b, _, _) when not (try holdsNoStorage (typeOf x) with _ -> true) ->
                                     chase seen b
                                 | IRTuple es ->
                                     // every component is chased by the SAME
                                     // rules (names, projections, views,
                                     // deferred forms); a stored component
                                     // (evaluated at its let) drops out
                                     let live = es |> List.choose (chase seen)
                                     if live.IsEmpty then None else Some (IRTuple live)
                                 | v when isDeferredForm v -> Some v
                                 | _ -> None
                             match facts.OuterLet id |> Option.bind (chase (Set.singleton id)) with
                             | Some deferred -> visit deferred
                             | None -> ()
                | IRParam (_, _, t) when isFunctionTyped t -> if facts.UnknownIsWrite then add { writeNone with External = true }
                | other -> visitChildren other
        and visitChildren (e: IRExpr) =
            let (ExprShape (children, _)) = e
            children |> List.iter visit
        visit body
        acc
    (summaryOf, walkBody)

let exprMayWriteWith (facts: WriteFacts) (root: IRExpr) : bool =
    let (_, walkBody) = writeAnalyzer facts
    let s = walkBody Map.empty Map.empty root
    s.External || not s.Params.IsEmpty

/// The analysis over a CALLABLE's applications (a kernel's): a write to its
/// own parameters counts too (they are its operands' cells).
/// The summary itself: writes to storage outside the callable (`External`)
/// kept apart from writes to its own parameters (`Params`), for a judgment
/// that treats the two differently (Lowering's parallel-body refusal: a
/// `mut` parameter is the caller's to grant, a capture is a race).
let callableWriteSummaryWith (facts: WriteFacts) (c: IRCallable) : WriteProvenance =
    let (summaryOf, _) = writeAnalyzer facts
    summaryOf c

/// An ABSTRACT FUNCTION VALUE for the callback table: the callables an
/// expression may denote (`Fns`, by callable id), whether it may denote
/// ANYTHING (`Top`: an unknown function -- a value from an untracked name, a
/// function returned by a call, a parameter an unknown caller may fill), and,
/// for a tuple, the value of each component. Known targets are kept beside
/// `Top`: a call through `{launch} + Top` still feeds launch's parameters.
type private FnVal = { Fns: Set<IRId>; Top: bool; Comps: Map<int, FnVal> }
let private fvBot : FnVal = { Fns = Set.empty; Top = false; Comps = Map.empty }
let private fvTop : FnVal = { fvBot with Top = true }
let rec private fvJoin (a: FnVal) (b: FnVal) : FnVal =
    { Fns = Set.union a.Fns b.Fns
      Top = a.Top || b.Top
      Comps =
        b.Comps |> Map.fold (fun acc k v ->
            Map.add k (match Map.tryFind k acc with Some w -> fvJoin w v | None -> v) acc) a.Comps }
/// A type whose values may carry a function: a function type, or a tuple
/// with such a component.
let rec private mayHoldFunction (t: IRType) : bool =
    match stripUnits t with
    | IRTTuple ts -> ts |> List.exists mayHoldFunction
    | t -> isFunctionTyped t
/// Every leaf of a (nested) tuple value joined: a FLAT projection may read any.
let rec private fvLeaves (v: FnVal) : FnVal =
    v.Comps |> Map.fold (fun acc _ c -> fvJoin acc (fvLeaves c)) { v with Comps = Map.empty }

/// THE CALLBACK-ARGUMENT TABLE (gap G41; Codex findings 22 / 25 / 26-28),
/// shared by Lowering's parallel-body refusal and both emitters' write facts:
/// for each function-valued parameter -- and each let / module-binding alias
/// of a function value -- the CLOSED set of callables it may hold, or no entry
/// when it may hold an unknown one (the walker then counts its applications
/// as unknown, i.e. a write).
///
/// A monotone abstract interpretation solved to its LEAST FIXPOINT, so the
/// answer does not depend on declaration or discovery order (finding 28):
///   * every tracked name -- a callback parameter, a `let` (local or MODULE
///     level: binding identity is kept, finding 26) -- starts at BOTTOM
///     (undiscovered), never at unknown, and grows by joins: a `let`
///     takes its initializer's value; a parameter the value of every argument
///     any call passes it, through EVERY target the call's head may denote --
///     an indirect `h(f)` (h itself a callback, or an alias of one: aliases
///     are evaluated, not matched by name, finding 27) feeds each candidate's
///     slots, known targets kept even beside an unknown one;
///   * RETURN FLOW (gap G47): every analyzed callable has a RETURN value, the
///     value of its body, and a call evaluates to the join of the return
///     values of every target its head may denote (a parameter's value is
///     the join over all call sites, so the substitution is context-
///     insensitive and sound). A function's result therefore flows to its
///     KNOWN callers instead of escaping: `passthrough(launch)(ident)` feeds
///     ident into launch's `f`. A partial application, a call through an
///     unknown head, and a callable the table did not analyze still return
///     TOP;
///   * PARAMETERS that may hold a function -- a function type, or a TUPLE with
///     a function component (G47) -- are tracked, the tuple ones component-
///     wise, so passing a tuple holding a callable to a function that reads
///     only its scalar component is not an escape;
///   * TOP arises only from a genuine unknown: an untracked name (a binder the
///     table does not model, a builtin head), the function an unknown or
///     partial call returns, a tuple read through an untracked tuple;
///   * a function value that ESCAPES -- reaches a position the table does not
///     follow: stored in a struct, used as a kernel, handed to an unknown call
///     or to an untracked parameter -- makes every tracked parameter of every
///     callable it may denote TOP (an unknown caller may then pass it
///     anything), and escapes that callable's RETURN value too (the unknown
///     caller may call it and keep what it returns). Tuples are followed
///     component-wise (a structural projection reads its component; a flat one
///     any leaf), so a callable stored in a tuple escapes only when the tuple
///     does. An alias INITIALIZER is not an escape; the alias's USES are
///     classified.
/// The lattice is finite, so the iteration converges; should it ever exceed
/// its budget it widens to TOP -- the EMPTY table, every callback unknown
/// (the rule the write summaries follow). `resolve` is the caller's callable
/// resolver (let aliases of named callables included where it chases them).
let callbackTable (resolve: IRExpr -> IRCallable option) (funcs: IRCallable list) (bindings: (IRId * IRExpr) list) : Map<IRId, IRExpr list> =
    let callables = System.Collections.Generic.Dictionary<IRId, IRCallable>()
    for f in funcs do callables.[f.Id] <- f
    // TRACKED parameters: a function type, or a tuple with a function leaf
    // (G47: tracked component-wise; only the function-typed ones are exported)
    let isCbParam = System.Collections.Generic.HashSet<IRId>()
    for f in funcs do
        for p in f.Params do
            if (try mayHoldFunction p.Type with _ -> false) then isCbParam.Add p.VarId |> ignore
    let analyzed = System.Collections.Generic.HashSet<IRId>(funcs |> Seq.map (fun f -> f.Id))
    // ---- one scan: call sites, alias definitions, escaping expressions ----
    let calls = ResizeArray<IRExpr * IRExpr list>()
    let defs = ResizeArray<IRId * IRExpr>()
    let defined = System.Collections.Generic.HashSet<IRId>()
    let escapes = ResizeArray<IRExpr>()
    // `escape`: the value of `e` flows somewhere the table does not follow
    let rec scan (escape: bool) (e: IRExpr) =
        match e with
        | IRApp (h, args, _) ->
            calls.Add (h, args)
            if escape then escapes.Add e
            scan false h
            args |> List.iter (scan false)
        | IRLet (x, v, b) ->
            defs.Add (x, v)
            defined.Add x |> ignore
            scan false v
            scan escape b
        | IRTuple es -> es |> List.iter (scan escape)
        | IRTupleProj (t, _, _) ->
            if escape then escapes.Add e
            scan false t
        | IRIf (c, a, b) ->
            scan false c
            scan escape a
            scan escape b
        | IRSequence es ->
            // a discarded statement's value goes nowhere
            es |> List.iteri (fun i x -> scan (escape && i = es.Length - 1) x)
        | IRVar _ -> if escape then escapes.Add e
        // a REASSIGNED tracked name joins the new value (an untracked target
        // stays unknown: its value escapes through the generic arm)
        | IRAssign (IRVar (x, _), v) when defined.Contains x || isCbParam.Contains x ->
            defs.Add (x, v)
            scan false v
        | ExprShape (children, _) -> children |> List.iter (scan true)
    for (id, v) in bindings do
        defs.Add (id, v)
        defined.Add id |> ignore
        scan false v
    // a function's result flows to its callers (RETURN FLOW, G47): the body's
    // value is the callable's return value, which each call evaluates to and
    // which escapes with the callable itself -- the tail is not an escape
    for f in funcs do scan false f.Body
    // ---- the abstract values ----
    let env = System.Collections.Generic.Dictionary<IRId, FnVal>()
    let ret = System.Collections.Generic.Dictionary<IRId, FnVal>()
    let repr = System.Collections.Generic.Dictionary<IRId, IRExpr>()
    let single (e: IRExpr) (c: IRCallable) =
        if not (callables.ContainsKey c.Id) then callables.[c.Id] <- c
        if not (repr.ContainsKey c.Id) then repr.[c.Id] <- e
        { fvBot with Fns = Set.singleton c.Id }
    let rec eval (e: IRExpr) : FnVal =
        match e with
        | IRVar (id, _) ->
            match resolve e with
            | Some c -> single e c
            | None ->
                match env.TryGetValue id with
                | true, v -> v
                | _ ->
                    // tracked but undiscovered: bottom; untracked: unknown
                    if defined.Contains id || isCbParam.Contains id then fvBot else fvTop
        | IRLet (_, _, b) -> eval b
        | IRIf (_, a, b) -> fvJoin (eval a) (eval b)
        | IRSequence es -> (match List.tryLast es with Some l -> eval l | None -> fvBot)
        | IRTuple es -> { fvBot with Comps = es |> List.mapi (fun i x -> (i, eval x)) |> Map.ofList }
        | IRTupleProj (t, i, flat) ->
            let tv = eval t
            let sub =
                if flat then fvLeaves tv
                else match Map.tryFind i tv.Comps with Some c -> c | None -> fvBot
            if tv.Top then fvJoin sub fvTop else sub
        | IRApp (h, args, _) when (match resolve e with Some _ -> false | None -> true) ->
            // RETURN FLOW (G47): the join of every target's return value; a
            // result that cannot hold a function carries nothing
            if not (try mayHoldFunction (typeOf e) with _ -> true) then fvBot
            else
                let hv = eval h
                let fromTargets =
                    hv.Fns |> Seq.fold (fun acc cid ->
                        match callables.TryGetValue cid with
                        | true, c when analyzed.Contains cid && args.Length = c.Params.Length ->
                            fvJoin acc (match ret.TryGetValue cid with | true, r -> r | _ -> fvBot)
                        // a partial (or over-) application, or a callable whose
                        // body the table did not analyze: an unknown function
                        | _ -> fvJoin acc fvTop) fvBot
                if hv.Top then fvJoin fromTargets fvTop else fromTargets
        | _ ->
            match resolve e with
            | Some c -> single e c
            // an untracked value that may HOLD a function (a function, or a
            // tuple with a function leaf -- a call's result, a field read)
            | None -> if (try mayHoldFunction (typeOf e) with _ -> true) then fvTop else fvBot
    let mutable changed = false
    let setVar (id: IRId) (v: FnVal) =
        let old = match env.TryGetValue id with | true, o -> o | _ -> fvBot
        let nv = fvJoin old v
        if nv <> old then
            env.[id] <- nv
            changed <- true
    let setRet (id: IRId) (v: FnVal) =
        let old = match ret.TryGetValue id with | true, o -> o | _ -> fvBot
        let nv = fvJoin old v
        if nv <> old then
            ret.[id] <- nv
            changed <- true
    // An escaping callable: an unknown caller may fill its tracked parameters
    // with anything AND keep whatever it returns (G47), so its return value
    // escapes too (each callable visited once per escape: a callable may
    // return itself).
    let escapeVal (v: FnVal) =
        let seen = System.Collections.Generic.HashSet<IRId>()
        let rec go (v: FnVal) =
            for cid in v.Fns do
                if seen.Add cid then
                    match callables.TryGetValue cid with
                    | true, c ->
                        for p in c.Params do
                            if isCbParam.Contains p.VarId then setVar p.VarId fvTop
                    | _ -> ()
                    match ret.TryGetValue cid with
                    | true, r -> go r
                    | _ -> ()
            for KeyValue (_, cv) in v.Comps do go cv
        go v
    let budget = 10000
    let mutable rounds = 0
    changed <- true
    while changed && rounds < budget do
        changed <- false
        rounds <- rounds + 1
        for (x, v) in defs do setVar x (eval v)
        for f in funcs do setRet f.Id (eval f.Body)
        for (h, args) in calls do
            let hv = eval h
            for cid in hv.Fns do
                match callables.TryGetValue cid with
                | true, c ->
                    args |> List.iteri (fun i a ->
                        if i < c.Params.Length && isCbParam.Contains c.Params.[i].VarId then setVar c.Params.[i].VarId (eval a)
                        else escapeVal (eval a))
                    // a PARTIAL application: an unknown caller supplies the rest
                    for p in List.skip (min args.Length c.Params.Length) c.Params do
                        if isCbParam.Contains p.VarId then setVar p.VarId fvTop
                | _ -> ()
            // an unknown target: every function-valued argument escapes
            if hv.Top then
                for a in args do escapeVal (eval a)
        for e in escapes do escapeVal (eval e)
    if changed then
        // over budget: widen to TOP -- every callback unknown
        Map.empty
    else
        let reprOf (cid: IRId) =
            match repr.TryGetValue cid with
            | true, e -> e
            | _ -> IRVar (cid, IRTUnit)
        env
        |> Seq.choose (fun kv ->
            let v = kv.Value
            if v.Top || v.Fns.IsEmpty || not v.Comps.IsEmpty then None
            else Some (kv.Key, v.Fns |> Set.toList |> List.map reprOf))
        |> Map.ofSeq

let callableMayWriteWith (facts: WriteFacts) (c: IRCallable) : bool =
    let (summaryOf, _) = writeAnalyzer facts
    let s = summaryOf c
    s.External || not s.Params.IsEmpty

/// The operands a module SIDE TABLE executes at binding `id`'s own site: a
/// `compound(dense, mask)`'s two operands, a `sparse(values, keys)`'s values,
/// a random fill's key / parameters / weights / address (or `fill_random`'s
/// modulus). The binding's own `Value` is then a placeholder (IRUnit), so an
/// analysis that reads only `Value` audits nothing: every admission and every
/// demand census reads `bindingPayload` instead (W1-4 review findings 1-2).
let bindingSideOperands (modul: IRModule) (id: IRId) : IRExpr list =
    (match Map.tryFind id modul.CompoundInits with Some (d, m) -> [ d; m ] | None -> [])
    @ (match Map.tryFind id modul.SparseInits with Some v -> [ v ] | None -> [])
    @ (match Map.tryFind id modul.RandomInits with
       | Some (FillModulus e) -> [ e ]
       | Some (RandGen (_, key, pars, weights, address)) ->
           key :: pars
           @ (weights |> Option.map fst |> Option.toList)
           @ (address |> Option.map (fun (s, o) -> [ s; o ]) |> Option.defaultValue [])
       | None -> [])

/// A module binding's ACTUAL execution payload: its value plus the side-table
/// operands executed at its site.
let bindingPayload (modul: IRModule) (b: IRBinding) : IRExpr list =
    b.Value :: bindingSideOperands modul b.Id

/// The payload as ONE expression (the value when there is no side operand).
let bindingPayloadExpr (modul: IRModule) (b: IRBinding) : IRExpr =
    match bindingSideOperands modul b.Id with
    | [] -> b.Value
    | ops -> IRSequence (ops @ [ b.Value ])

/// THE INTERVENING-STATEMENT PAYLOAD of a scheduling interval (hot-row, the
/// cell epilogue, compact row classes -- W1-4 review finding 7): the planners
/// walk let CHAINS of (id, name, value); a MODULE binding in such a chain is
/// resolved by id to its complete payload (its value plus its side-table
/// operands, plus the source a provider write reads), so the read,
/// repeatability and write checks see what its site actually runs. A nested
/// ordinary let (not a module binding) keeps its own value. Producer and
/// consumer RECOGNITION keeps the original values.
let intervalPayloadOf (modul: IRModule) : IRId -> IRExpr -> IRExpr =
    let byId = modul.Bindings |> List.map (fun b -> (b.Id, b)) |> Map.ofList
    fun (id: IRId) (v: IRExpr) ->
        match Map.tryFind id byId with
        | Some b ->
            let provider =
                match Map.tryFind b.Id modul.ProviderWrites with
                | Some w -> [ IRVar (w.SourceId, IRTUnit) ]
                | None -> []
            match bindingSideOperands modul b.Id @ provider with
            | [] -> v
            | ops -> IRSequence (ops @ [ v ])
        | None -> v

/// A provider effect at an intervening module binding (a store write): an
/// external effect no schedule moves work across.
let intervalHasProviderEffect (modul: IRModule) (id: IRId) : bool =
    modul.ProviderWrites.ContainsKey id
/// THE SCHEDULING-ADMISSION WRITE PREDICATE (gap G41; Codex reviews 14-19):
/// one instance of the write analysis above over a whole module -- callees
/// resolved through the module's functions AND let aliases of them, named
/// deferred values followed through every let in the module (module bindings
/// and function-body lets), `let mut` deep copies of dense arrays recognized.
/// Every pass that MOVES an evaluation relative to another (hot-row and cell
/// epilogue schedules, compact row classes, compaction fusion, row-fold
/// fusion) asks it about all five parties: the producer kernel, the consumer
/// (row / cell / walk) kernel, the OUTER fold kernel, the seed, and every
/// binding in between. A conservative `cseFactsOf` check may stand beside it
/// (it also catches display output), never instead of it: `cseFactsOf` does
/// not chase a named deferred array initializer.
let rec scheduleMayWriteIn (programFuncs: IRCallable list) (modul: IRModule) : IRExpr -> bool =
    scheduleMayWriteWith false programFuncs modul

/// `scheduleMayWriteIn` with every CALLBACK (a function-valued parameter)
/// joined over the arguments its call sites pass (`callbackTable`, the
/// collector Lowering's parallel-body refusal uses): the predicate an
/// admission applies to a PARALLEL body (A7c's producer under an omp
/// request). An unknown callback still counts as a write.
and scheduleMayWriteWithCallbacksIn (programFuncs: IRCallable list) (modul: IRModule) : IRExpr -> bool =
    scheduleMayWriteWith true programFuncs modul

and scheduleMayWriteWith (withCallbacks: bool) (programFuncs: IRCallable list) (modul: IRModule) : IRExpr -> bool =
    let funcs =
        (programFuncs @ modul.Functions) |> List.map (fun f -> (f.Id, f)) |> Map.ofList
    let lets = System.Collections.Generic.Dictionary<IRId, IRExpr>()
    let note (e: IRExpr) = iterIRExpr (fun n -> match n with IRLet (id, v, _) -> lets.[id] <- v | _ -> ()) e
    // a side-table binding's initializer is its PAYLOAD (its value is a
    // placeholder): a reference to it is charged with what its site runs
    for b in modul.Bindings do
        let payload = bindingPayloadExpr modul b
        lets.[b.Id] <- payload
        note payload
    for f in modul.Functions do note f.Body
    let rec chaseAlias (seen: Set<IRId>) (e: IRExpr) =
        match e with
        | IRVar (id, _) when not (funcs.ContainsKey id) && not (seen.Contains id) ->
            (match lets.TryGetValue id with
             | true, (IRVar _ as v) -> chaseAlias (seen.Add id) v
             | _ -> e)
        | _ -> e
    let resolve (e: IRExpr) =
        match chaseAlias Set.empty e with
        | IRVar (id, _) -> Map.tryFind id funcs
        | _ -> None
    let copied (id: IRId) =
        modul.MutableArrayLets.Contains id
        && (match lets.TryGetValue id with
            | true, IRVar (_, ty) ->
                (match stripUnits ty with
                 | ArrayElem at -> at.IndexTypes |> List.forall (fun ix -> ix.IxKind = IxKPlain && ix.Symmetry = SymNone)
                 | _ -> false)
            | _ -> false)
    let facts : WriteFacts =
        { Resolve = resolve
          OuterLet = (fun id -> match lets.TryGetValue id with | true, v -> Some v | _ -> None)
          CopiedLet = copied
          UnknownIsWrite = true
          Callbacks = (fun _ -> None) }
    let facts =
        if not withCallbacks then facts
        else
            let table =
                callbackTable resolve (programFuncs @ modul.Functions) (modul.Bindings |> List.map (fun b -> (b.Id, b.Value)))
            { facts with
                Callbacks = (fun id ->
                    match Map.tryFind id table with
                    | Some args when not args.IsEmpty -> Some args
                    | _ -> None) }
    fun (e: IRExpr) -> exprMayWriteWith facts e

let scheduleMayWrite (modul: IRModule) : IRExpr -> bool = scheduleMayWriteIn [] modul

/// A resource boundary reached directly or through a callable is a motion
/// barrier without being a source write. Every downstream schedule planner
/// audits this independently of scheduleMayWriteIn.
let scheduleHasLifetimeBarrierIn (programFuncs: IRCallable list) (modul: IRModule) : IRExpr -> bool =
    if not (((programFuncs @ modul.Functions) |> List.exists (fun f -> containsPoolLifetimeEvent f.Body))
            || (modul.Bindings |> List.exists (fun b -> containsPoolLifetimeEvent (bindingPayloadExpr modul b)))) then
        fun _ -> false
    else
    let funcs = (programFuncs @ modul.Functions) |> List.map (fun f -> f.Id, f) |> Map.ofList
    let values = System.Collections.Generic.Dictionary<IRId, IRExpr>()
    for b in modul.Bindings do values.[b.Id] <- bindingPayloadExpr modul b
    for e in (modul.Bindings |> List.map _.Value) @ ((programFuncs @ modul.Functions) |> List.map _.Body) do
        iterIRExpr (function IRLet (id, v, _) -> values.[id] <- v | _ -> ()) e
    let rec go seen e =
        match e with
        | IRBeginPoolScope _ | IRReleasePools _ -> true
        | IRVar (id, _) when not (Set.contains id seen) ->
            match Map.tryFind id funcs with
            | Some f -> go (Set.add id seen) f.Body
            | None ->
                match values.TryGetValue id with
                | true, v when isDeferredForm v || isFunctionTyped (typeOf v) -> go (Set.add id seen) v
                | _ -> false
        | ExprShape (children, _) -> children |> List.exists (go seen)
    go Set.empty

/// A float literal as its bit pattern, for identity only: structural `=` on
/// floats is IEEE equality, which calls `0.0` and `-0.0` the same value.
let private floatBitsLit (n: IRExpr) : IRExpr =
    match n with
    | IRLit (IRLitFloat f) -> IRLit (IRLitString ("\u0001f64:" + string (System.BitConverter.DoubleToInt64Bits f)))
    | IRLit (IRLitFloat32 f) -> IRLit (IRLitString ("\u0001f32:" + string (System.BitConverter.SingleToInt32Bits f)))
    | _ -> n

let cseModule (modul: IRModule) : IRModule =
    let funcs = modul.Functions |> List.map (fun f -> (f.Id, f)) |> Map.ofList
    let facts = cseFactsOf funcs
    let enabled = cseEnabled ()
    // Two references to callables are the SAME value when the callables are
    // structurally identical: the whole record once its own id and name and
    // its parameters' names and ids are erased (a parameter becomes its
    // position), float literals by bit pattern. Every `(+)` section and
    // every inline lambda is lifted to its own callable, so two identical
    // folds name different ids; this is the identity the comparison needs.
    // `representative` maps a callable to the first structurally identical
    // one ASKED ABOUT -- a consistent partition by key, computed only for
    // callables a candidate value names.
    let callableKey (f: IRCallable) : IRCallable =
        let subst = f.Params |> List.mapi (fun i p -> (p.VarId, -(i + 1))) |> Map.ofList
        let body =
            mapIRExpr (fun e ->
                match e with
                | IRVar (id, t) when Map.containsKey id subst -> IRVar (subst.[id], t)
                | _ -> floatBitsLit e) f.Body
        { f with
            Id = 0
            Name = ""
            SourceName = ""
            Params = f.Params |> List.mapi (fun i p -> { p with Name = ""; VarId = -(i + 1) })
            Body = body }
    let repOf = System.Collections.Generic.Dictionary<IRId, IRId>()
    let byKey = System.Collections.Generic.Dictionary<IRCallable, IRId>(HashIdentity.Structural)
    let representative (f: IRCallable) : IRId =
        match repOf.TryGetValue f.Id with
        | true, r -> r
        | _ ->
            let k = callableKey f
            let r =
                match byKey.TryGetValue k with
                | true, first -> first
                | _ -> byKey.[k] <- f.Id; f.Id
            repOf.[f.Id] <- r
            r
    let canon (e: IRExpr) : IRExpr =
        mapIRExpr (fun n ->
            match n with
            | IRVar (id, t) ->
                (match Map.tryFind id funcs with
                 | Some f -> IRVar (representative f, t)
                 | None -> n)
            | _ -> floatBitsLit n) e
    let rec unroll (e: IRExpr) : (IRId * IRExpr) list * IRExpr =
        match e with
        | IRLet (id, v, body) ->
            let (iv, vf) = unroll v
            let (rb, rf) = unroll body
            (match iv with
             | [] -> ((id, v) :: rb, rf)
             | _ -> (iv @ [ (id, vf) ] @ rb, rf))
        | _ -> ([], e)
    let trivial (v: IRExpr) =
        match v with
        | IRLit _ | IRVar _ | IRParam _ -> true
        | IRApplyCombinator _ | IRComposeApply _ -> true   // deferred: a join's sharing declaration
        | _ -> false
    let hasBarrier (e: IRExpr) =
        let mutable found = false
        iterIRExpr (fun n -> match n with IRAssign _ | IRForRange _ | IRBeginPoolScope _ | IRReleasePools _ -> found <- true | _ -> ()) e
        found
    let moduleSubst = System.Collections.Generic.Dictionary<IRId, IRId>()
    let rewriteBody (f: IRCallable) : IRCallable =
        let (lets, ret) = unroll f.Body
        if lets.Length < 2 || hasBarrier f.Body then f
        else
            let subst = System.Collections.Generic.Dictionary<IRId, IRId>()
            let applySubst (e: IRExpr) =
                if subst.Count = 0 then e
                else
                    mapIRExpr (fun n ->
                        match n with
                        | IRVar (id, t) when subst.ContainsKey id -> IRVar (subst.[id], t)
                        | _ -> n) e
            let kept = ResizeArray<IRId * IRExpr>()
            let pairs = ResizeArray<IRId * IRId>()
            // The values available for reuse, by canonical form. Cleared at
            // every let that may write: nothing before a barrier is reused
            // after it.
            let available = System.Collections.Generic.Dictionary<IRExpr, IRId>(HashIdentity.Structural)
            // A DEFERRED let (a bare apply left for a join) runs its kernel
            // where it is READ, not where it is bound (codegen forces the
            // producer at the consumer), so an expression naming one
            // inherits its facts -- transitively, since a deferred producer
            // may read another.
            let deferredVals =
                lets |> List.filter (fun (_, v) -> match v with IRApplyCombinator _ | IRComposeApply _ -> true | _ -> false)
                |> Map.ofList
            let deferredFacts = System.Collections.Generic.Dictionary<IRId, CseFacts>()
            let rec factsIn (visiting: Set<IRId>) (v: IRExpr) : CseFacts =
                let mutable r = facts v
                for x in collectVarRefsIR v do
                    match Map.tryFind x deferredVals with
                    | Some dv when not (Set.contains x visiting) ->
                        let d =
                            match deferredFacts.TryGetValue x with
                            | true, d -> d
                            | _ ->
                                let d = factsIn (Set.add x visiting) dv
                                deferredFacts.[x] <- d
                                d
                        r <- { MayWrite = r.MayWrite || d.MayWrite; Repeatable = r.Repeatable && d.Repeatable }
                    | _ -> ()
                r
            let factsOfValue = factsIn Set.empty
            // Lets a writing evaluation NAMES -- directly, through the
            // captures or body of a callable it names, or through a deferred
            // let it reads -- anywhere in the body (the return included).
            // Such a let's storage is what a `mut` argument writes, so it
            // takes no part in a merge, in either role: merged, the write
            // would reach the other name too (a later read of the untouched
            // twin would see it).
            let exposed =
                let acc = System.Collections.Generic.HashSet<IRId>()
                let work = System.Collections.Generic.Stack<IRId>()
                for v in (lets |> List.map snd) @ [ ret ] do
                    if (factsOfValue v).MayWrite then
                        for r in collectVarRefsIR v do work.Push r
                while work.Count > 0 do
                    let r = work.Pop()
                    if acc.Add r then
                        (match Map.tryFind r funcs with
                         | Some g ->
                            for c in g.Captures do work.Push c.Id
                            for b in collectVarRefsIR g.Body do work.Push b
                         | None -> ())
                        (match Map.tryFind r deferredVals with
                         | Some dv -> for b in collectVarRefsIR dv do work.Push b
                         | None -> ())
                acc
            for (id, v0) in lets do
                let v = applySubst v0
                let vf = factsOfValue v
                let dup =
                    if trivial v || not vf.Repeatable || exposed.Contains id then None
                    else
                        let cv = canon v
                        match available.TryGetValue cv with
                        | true, earlier -> Some earlier
                        | _ -> available.[cv] <- id; None
                match dup with
                | Some earlier ->
                    subst.[id] <- earlier
                    pairs.Add((id, earlier))
                | None -> kept.Add((id, v))
                if vf.MayWrite then available.Clear()
            if pairs.Count = 0 then f
            elif not enabled then
                Blade.Effects.Decisions.record
                    { Blade.Effects.Rule = "cse"; Version = 2
                      Span = Blade.Ast.noSpan; Subject = f.SourceName
                      Outcome = Blade.Effects.Declined "disabled by BLADE_CSE"
                      Evidence = pairs |> Seq.map (fun (j, i) -> $"__v{j} is the same repeatable value as __v{i}: left as written") |> List.ofSeq }
                f
            else
                let ret' = applySubst ret
                let body' = Seq.foldBack (fun (id, v) acc -> IRLet (id, v, acc)) kept ret'
                // A dropped let may be CAPTURED by a kernel lambda lifted out of
                // this body: that callable's capture list and body name the
                // dropped id, and codegen forwards captures by name, so both
                // are rewritten too (ids are program-global, so this is exact).
                for (j, i) in pairs do moduleSubst.[j] <- i
                Blade.Effects.Decisions.record
                    { Blade.Effects.Rule = "cse"; Version = 2
                      Span = Blade.Ast.noSpan; Subject = f.SourceName
                      Outcome = Blade.Effects.Applied
                      Evidence = pairs |> Seq.map (fun (j, i) -> $"__v{j} is the same repeatable value as __v{i}: dropped, its reads go to __v{i}") |> List.ofSeq }
                { f with Body = body' }
    let rewritten = modul.Functions |> List.map rewriteBody
    if moduleSubst.Count = 0 then { modul with Functions = rewritten }
    else
        let fix (e: IRExpr) =
            mapIRExpr (fun n ->
                match n with
                | IRVar (id, t) when moduleSubst.ContainsKey id -> IRVar (moduleSubst.[id], t)
                | _ -> n) e
        { modul with
            Functions =
                rewritten |> List.map (fun g ->
                    { g with
                        Body = fix g.Body
                        Captures = g.Captures |> List.map (fun c -> if moduleSubst.ContainsKey c.Id then { c with Id = moduleSubst.[c.Id] } else c) }) }

// --- Kernel-invariant hoisting -----------------------------------------------
//
// TypeCheck desugars an array/scalar elementwise op by EMBEDDING the scalar
// operand's surface expression in the kernel (`a - mean(a)` is
// `method_for(a) <@> lambda(__bx) -> __bx - mean(a) |> compute`; the embedding
// is what lets capture analysis see the operand's names). The scalar side is
// therefore evaluated once PER CELL: centering a length-T row with the
// one-liner folded `a` T times -- O(T^2) -- and `x / sqrt(mean(x * x))` also
// allocated `x * x` per cell, while the block spelling that binds the mean
// with a `let` first is linear. Same computation, two spellings, a factor of
// T apart: a SAME-EMIT violation, and in the spelling the tutorials teach.
// A hand-written `method_for(a) <@> lambda(e) -> e - mean(a)` has the same
// shape and the same cost.
//
// This pass moves the invariant out. For a FORCED apply (`IRCompute
// (IRApplyCombinator ..)`, or the single apply a fused `reduce` folds) whose
// kernel is a lifted lambda, every maximal subexpression S of the kernel
// body that
//
//   * is a rank-0 numeric value reading NO kernel parameter and no name
//     bound inside the body -- every variable it names is one of the
//     kernel's captures, or a callable whose own captures are (so S means
//     the same thing at the apply's site as it did in the kernel), and none
//     is a DEFERRED let, whose producer runs where it is read;
//   * does real work: a call, an array traversal or a math intrinsic
//     (scalar arithmetic over captured scalars is left for the C++ compiler,
//     which already hoists it);
//   * is REPEATABLE and writes nothing (the CSE judge's facts, callees
//     resolved through the WHOLE PROGRAM's function table -- `mean` lives in
//     the stdlib's module), in a kernel that itself writes nothing S could
//     read between two cells;
//   * is evaluated UNCONDITIONALLY by the kernel (a strict position: not
//     under a branch, a match arm or the right operand of `&&` / `||`),
//
// is bound once by a `let` wrapped around the apply, and the kernel -- a
// clone, the original is left for any other reference -- reads that let as
// one more capture. IRLift, which runs next, already drains such a chain to
// the enclosing statement position (it is the shape the post-monomorphization
// broadcast in IRMono.lowerArrayBinOpsModule has always produced), so codegen
// and the interpreter see exactly what the block spelling lowers to.
//
// COST ONLY (charter rule 1). S ran at cell 0 of every non-empty nest and at
// no cell of an empty one; after the rewrite it runs once before the nest:
//
//   * an S that cannot abort is bound unconditionally -- evaluating a pure
//     total value an extra time (the empty nest) is unobservable;
//   * an S that may abort (`mean` of an empty row is BL8003) is bound under
//     the nest's own non-emptiness -- `if <every operand has a cell> then S
//     else zero` -- so an empty nest still evaluates nothing and raises
//     nothing. The guard is spelled only where it is exact: plain dense
//     operands that are literal-extent, or named values whose extents can be
//     read (parameters, forced or literal lets; an operand that is itself a
//     forced map or a call is bound to a name first, ahead of S). Anything
//     else (packed, compound, sparse, ragged, a deferred operand) leaves S
//     in the kernel;
//   * such an S also needs the kernel's remainder to be repeatable, so no
//     output the kernel would have emitted at cell 0 can end up after an
//     abort it used to precede.
//
// RESIDUAL (accepted, as fusion's): the program still aborts exactly when it
// did, but not always with the SAME code -- an S that aborts now does so
// before the nest's own co-iteration extent check and before whatever else
// the kernel would have evaluated first at cell 0. Both runs exit non-zero.
//
// FLOATING POINT. A hoisted value is the same double the kernel computed, but
// g++'s default `-ffp-contract=fast` fuses a multiply into an adjacent add,
// and binding a product to a name ends that adjacency. So S is never ROOTED
// at a multiply (or `^`, or the negation of one): `e - mean(a) * 2.0` hoists
// `mean(a)` and keeps the product in the kernel, beside the add it feeds --
// the same IR the block spelling `let m = mean(a); e - m * 2.0` gives. (What
// g++ then does with an invariant product is g++'s own choice, as it is for
// the block spelling; `blade test opt-diff` compares the printed values.)
// Everything inside S keeps its own shape.
//
// An S with no expression rendering (it contains a forced map, a fold --
// anything IRLift would bind to a statement) cannot sit in the guard's arm,
// where statement-shaped values are refused; it becomes the body of a
// nullary helper lambda capturing what it reads, and the arm is a call.
//
// Runs AFTER fusion: a fused kernel carries every inner kernel's invariants
// and gets one nest, whereas a hoist first would hide the inner maps behind
// lets fusion does not look through.

/// What it takes to know one apply's iteration space is non-empty: the
/// (operand, axis) extents only run time knows -- none when every extent is a
/// positive literal -- and the operands that have to be bound to a name
/// before their extent can be read.
type private NonEmptyPlan =
    { Checks: (int * int) list
      Unnamed: int list }

let hoistKernelInvariantsModule (builder: IRBuilder) (programFuncs: IRCallable list) (modul: IRModule) : IRModule =
    let enabled = hoistEnabled ()
    let own = modul.Functions |> List.map (fun f -> (f.Id, f)) |> Map.ofList
    // Callee resolution is whole-program (ids are program-global); this
    // module's own callables, as the earlier passes left them, win.
    let funcs =
        programFuncs |> List.fold (fun m f -> if Map.containsKey f.Id m then m else Map.add f.Id f m) own
    let facts = cseFactsOf funcs
    let callables = System.Collections.Generic.Dictionary<IRId, IRCallable>()
    for KeyValue (id, f) in funcs do callables.[id] <- f
    let mayAbort (e: IRExpr) : bool = tileMayAbort callables Set.empty e

    // Names whose extents can be READ where an apply stands: parameters (a
    // caller hands over a value, never a deferred form) and lets / bindings
    // of a forced or literal array. A deferred let declares no storage until
    // its consumer forces it, so a guard may not ask for its extent.
    let materialized = System.Collections.Generic.HashSet<IRId>()
    // Names bound to a DEFERRED form (a bare apply, a loop object, a join
    // tree): their producer runs where they are read. A value that reads one
    // is left in the kernel -- moving the read would move the producer.
    let deferred = System.Collections.Generic.HashSet<IRId>()
    let isDeferredForm (v: IRExpr) : bool =
        match v with
        | IRApplyCombinator _ | IRComposeApply _ | IRMethodFor _ | IRObjectFor _
        | IRFusion _ | IRParallel _ | IRChoice _ | IRFallback _ | IRGuard _ | IRSequence _
        | IRReplicate _ | IRFunctorMap _ | IRBind _ | IRPure _ | IRZip _ | IRArrayProduct _
        | IRComposeObj _ | IRComposeMeth _ | IRCompose _ | IRReynolds _ -> true
        | _ -> false
    let note (id: IRId) (v: IRExpr) =
        match v with
        | IRCompute _ | IRArrayLit _ -> materialized.Add id |> ignore
        | v when isDeferredForm v -> deferred.Add id |> ignore
        | _ -> ()
    let noteLets (e: IRExpr) =
        iterIRExpr (fun n ->
            match n with
            | IRLet (id, v, _) -> note id v
            | _ -> ()) e
    for f in modul.Functions do
        for p in f.Params do materialized.Add p.VarId |> ignore
        noteLets f.Body
    for b in modul.Bindings do
        note b.Id b.Value
        noteLets b.Value

    // None: not expressible at this seam (packed / compound / sparse / ragged
    // storage, a virtual or deferred operand), or statically empty. An
    // operand that is a forced map or a call -- a fresh array IRLift binds to
    // a `let` in front of the nest anyway -- can be bound HERE instead, ahead
    // of the hoisted value, so the guard reads its extent by name (and the
    // operand is still evaluated before the value, as it was before the
    // kernel's first cell).
    let nonEmptyOf (info: ApplyInfo) : NonEmptyPlan option =
        if info.Arrays.IsEmpty || info.Arrays.Length <> info.ArrayTypes.Length then None else
        let mutable unknown = false
        let checks = ResizeArray<int * int>()
        let unnamed = ResizeArray<int>()
        List.zip info.Arrays info.ArrayTypes
        |> List.iteri (fun i (a, at) ->
            if at.IsVirtual
               || not (at.IndexTypes |> List.forall (fun ix -> ix.IxKind = IxKPlain && ix.Symmetry = SymNone && ix.Rank = 1)) then
                unknown <- true
            else
                at.IndexTypes |> List.iteri (fun d ix ->
                    match ix.Extent, a with
                    | IRLit (IRLitInt n), _ -> if n <= 0L then unknown <- true
                    | _, IRVar (id, _) when materialized.Contains id -> checks.Add((i, d))
                    | _, IRCompute _ ->
                        checks.Add((i, d))
                        if not (unnamed.Contains i) then unnamed.Add i
                    | _, IRApp (IRVar (fid, _), _, _) when Map.containsKey fid funcs ->
                        checks.Add((i, d))
                        if not (unnamed.Contains i) then unnamed.Add i
                    | _ -> unknown <- true))
        if unknown then None else Some { Checks = List.ofSeq checks; Unnamed = List.ofSeq unnamed }

    let isNumericScalar (t: IRType) : bool =
        match t with
        | AnyPrimElem (ETInt32 | ETInt64 | ETFloat32 | ETFloat64 | ETBool | ETComplex64 | ETComplex128) -> true
        | _ -> false
    // The guard's other arm: never read (the nest is empty), but typed like S.
    let zeroOf (t: IRType) : IRExpr option =
        match t with
        | AnyPrimElem ETInt32 ->
            Some (IRUnaryOp (IRCast (ETInt32, SrcLoc.Nowhere), IRLit (IRLitInt 0L)))
        | _ -> zeroLiteralOf t
    // A call, an array traversal, or a libm intrinsic (g++ does not move a
    // call that may set errno out of a loop). Plain arithmetic is not.
    let hasWork (e: IRExpr) : bool =
        let mutable w = false
        iterIRExpr (fun n ->
            match n with
            | IRApp _ | IRReduce _ | IRReduceCompute _ | IRProdSum _ | IRCompute _ | IRContains _ -> w <- true
            | IRUnaryOp (IRMath _, _) | IRBinOp (_, IRMath2 _, _, _, _) -> w <- true
            | _ -> ()) e
        w
    let rec mulRooted (e: IRExpr) : bool =
        match e with
        | IRBinOp (_, (IRMul | IRCaret), _, _, _) -> true
        | IRUnaryOp (IRNeg, x) -> mulRooted x
        | _ -> false
    // Does `e` mean the same thing outside the kernel? Every name is a
    // capture, or a callable that itself closes over captures only; no
    // binder inside (ids bound in the kernel body are not captures, so a
    // read of one already fails the first test -- refusing the binders too
    // keeps the judgment first-order).
    let closedOver (captureIds: System.Collections.Generic.HashSet<IRId>) (e: IRExpr) : bool =
        let mutable ok = true
        iterIRExpr (fun n ->
            if ok then
                match n with
                | IRVar (id, (IRTLoop _ | IRTComputation _)) when captureIds.Contains id -> ok <- false
                | IRVar (id, _) when captureIds.Contains id -> if deferred.Contains id then ok <- false
                | IRVar (id, _) ->
                    (match Map.tryFind id funcs with
                     | Some g -> if not (g.Captures |> List.forall (fun c -> captureIds.Contains c.Id)) then ok <- false
                     | None -> ok <- false)
                | IRParam _ | IRNth | IRArity _ | IRPolyIndex _ | IRPolyTail _ | IRZero _ | IRUninit _
                | IRLet _ | IRMatch _ | IRForRange _ | IRAssign _ | IRBreakIf _ | IRConstraintCheck _ -> ok <- false
                | _ -> ()) e
        ok
    // Renders as ONE C++ expression (so it can stand in the guard's arm).
    let rec inlineSafe (e: IRExpr) : bool =
        match e with
        | IRLit _ | IRVar _ -> true
        | IRBinOp (_, _, l, r, _) -> inlineSafe l && inlineSafe r
        | IRUnaryOp (_, x) -> inlineSafe x
        | IRFma (a, b, c) -> inlineSafe a && inlineSafe b && inlineSafe c
        | IRComplex (re, im) -> inlineSafe re && inlineSafe im
        | IRApp (IRVar (fid, _), args, _) when Map.containsKey fid funcs -> args |> List.forall inlineSafe
        | IRExtent (IRVar _, _) -> true
        | _ -> false

    // A hoisted value's type is what the C++ it renders to yields, i.e. the
    // checker's rule (exprTypeIfKnown), not typeOf's IR-level one where they
    // part: `abs` of an Int64 stays Int64 (`std::abs`), which typeOf calls
    // Float64 -- and a `double` let fed back into an Int64 kernel was a g++
    // float-conversion rejection.
    let valueType (e: IRExpr) : IRType =
        match exprTypeIfKnown e with
        | Some t -> t
        | None -> typeOf e

    let show (k: IRCallable) (e: IRExpr) : string =
        let nameOf (id: IRId) =
            match k.Captures |> List.tryFind (fun c -> c.Id = id) with
            | Some c -> c.Name
            | None ->
                match Map.tryFind id funcs with
                // The callee as the program wrote it, not its emitted
                // specialization (`mean`, not `mean_HM_<id>_double`).
                | Some f -> f.SourceName
                | None -> $"__v{id}"
        let rec go (depth: int) (e: IRExpr) : string =
            if depth > 3 then ".." else
            match e with
            | IRVar (id, _) -> nameOf id
            | IRApp (f, args, _) ->
                let shown = args |> List.map (go (depth + 1)) |> String.concat ", "
                $"{go (depth + 1) f}({shown})"
            | IRUnaryOp (IRMath m, x) -> $"{m}({go (depth + 1) x})"
            | IRReduce (a, _, _) -> $"reduce({go (depth + 1) a}, ..)"
            | IRProdSum args ->
                let shown = args |> List.map (go (depth + 1)) |> String.concat ", "
                $"prodsum({shown})"
            | IRCompute _ -> "<elementwise map>"
            | _ -> ".."
        go 0 e

    // Minted callables, by the kernel they derive from: each is placed right
    // after that kernel and recorded in DerivedFuncOrigins, so it is emitted
    // at the kernel's program point (the IRModule.DerivedFuncOrigins
    // contract -- a fresh id alone would sort it after every use).
    let minted = System.Collections.Generic.Dictionary<IRId, ResizeArray<IRCallable>>()
    let mint (origin: IRId) (c: IRCallable) =
        match minted.TryGetValue origin with
        | true, l -> l.Add c
        | _ ->
            let l = ResizeArray<IRCallable>()
            l.Add c
            minted.[origin] <- l
    let hoistedIds = System.Collections.Generic.HashSet<IRId>()
    let decided = System.Collections.Generic.HashSet<IRId>()

    let rec rw (e: IRExpr) : IRExpr =
        match e with
        // A conditional arm is not a drain point: IRLift leaves an arm's
        // lets inside the arm, where statement-shaped values are refused.
        // Nothing is hoisted there (the apply keeps today's shape).
        | IRIf (c, t, f) -> IRIf (rw c, t, f)
        | IRMatch (scrut, cases) -> IRMatch (rw scrut, cases)
        | IRBinOp (m, (IRAnd | IROr as op), l, r, loc) -> IRBinOp (m, op, rw l, r, loc)
        | IRCompute (IRApplyCombinator info) ->
            let info1 = rwOperands info
            (match planFor info1 with
             | Some (lets, info2) ->
                List.foldBack (fun (id, v) acc -> IRLet (id, v, acc)) lets (IRCompute (IRApplyCombinator info2))
             | None -> IRCompute (IRApplyCombinator info1))
        // The fused reduction terminal over ONE apply forces it just the
        // same: one nest, the kernel once per cell, nothing when empty (the
        // fold answers its seed). A join tree of several applies is left.
        | IRReduceCompute (IRApplyCombinator info, kernel, init) ->
            let info1 = rwOperands info
            let kernel' = rw kernel
            let init' = rw init
            (match planFor info1 with
             | Some (lets, info2) ->
                List.foldBack (fun (id, v) acc -> IRLet (id, v, acc)) lets
                    (IRReduceCompute (IRApplyCombinator info2, kernel', init'))
             | None -> IRReduceCompute (IRApplyCombinator info1, kernel', init'))
        // A DEFERRED apply runs its kernel where it is read, not here:
        // nothing is hoisted out of it, only its operands are rewritten.
        | IRApplyCombinator info -> IRApplyCombinator (rwOperands info)
        | IRLet (id, v, body) ->
            // A hoist out of a let's VALUE goes in front of the let: the
            // straight-line shape the block spelling has.
            let rec peel acc v =
                match v with
                | IRLet (hid, hv, rest) when hoistedIds.Contains hid -> peel ((hid, hv) :: acc) rest
                | _ -> (List.rev acc, v)
            let (hs, inner) = peel [] (rw v)
            List.foldBack (fun (hid, hv) acc -> IRLet (hid, hv, acc)) hs (IRLet (id, inner, rw body))
        | _ -> rwChildren e
    and rwChildren (e: IRExpr) : IRExpr =
        match e with
        | ExprShape ([], _) -> e
        | ExprShape (children, rebuild) -> rebuild (children |> List.map rw)
    // The Loop slot is PROVENANCE -- a second copy of the operand tree (with
    // its own lifted kernels) that no emitter runs. Rewriting it would mint
    // a let and a kernel per copy that nothing reads; it is left as written.
    and rwOperands (info: ApplyInfo) : ApplyInfo =
        { info with Arrays = info.Arrays |> List.map rw }
    and planFor (info: ApplyInfo) : ((IRId * IRExpr) list * ApplyInfo) option =
        match info.Kernel with
        | IRVar (kid, kty) ->
            (match Map.tryFind kid own with
             | Some k when k.Name.StartsWith "__lambda_" && not k.IsStatic && not k.IsArityPoly
                           && not k.IsCudaKernel && not k.IsMpiParallel ->
                let kf = facts k.Body
                if kf.MayWrite then None else
                let captureIds = System.Collections.Generic.HashSet<IRId>(k.Captures |> List.map (fun c -> c.Id))
                let nonEmpty = lazy (nonEmptyOf info)
                let admit (e: IRExpr) : bool =
                    if mulRooted e || not (hasWork e) || not (closedOver captureIds e) then false else
                    let t = valueType e
                    if not (isNumericScalar t) then false else
                    let f = facts e
                    if not f.Repeatable || f.MayWrite then false
                    elif not (mayAbort e) then true
                    else
                        kf.Repeatable && (zeroOf t).IsSome && nonEmpty.Value.IsSome
                // Maximal candidates in strict positions, in evaluation order.
                let found = ResizeArray<IRExpr>()
                let rec walk (e: IRExpr) =
                    if admit e then (if not (found.Contains e) then found.Add e)
                    else
                        match e with
                        | IRBinOp (_, (IRAnd | IROr), l, _, _) -> walk l
                        | IRIf (c, _, _) -> walk c
                        | IRMatch (scrut, _) -> walk scrut
                        | IRBinOp _ | IRUnaryOp _ | IRFma _ | IRComplex _ | IRTuple _ | IRTupleProj _
                        | IRLet _ | IRApp _ | IRIndex _ | IRFieldAccess _ | IRStructLit _ ->
                            childrenOf e |> List.iter walk
                        | _ -> ()
                walk k.Body
                if found.Count = 0 then None
                elif not enabled then
                    if decided.Add kid then
                        Blade.Effects.Decisions.record
                            { Blade.Effects.Rule = "invariant-hoist"; Version = 1
                              Span = Blade.Ast.noSpan; Subject = k.SourceName
                              Outcome = Blade.Effects.Declined "disabled by BLADE_HOIST"
                              Evidence = found |> Seq.map (fun s -> $"`{show k s}` does not read the element: left in the kernel, evaluated per cell") |> List.ofSeq }
                    None
                else
                    let table = System.Collections.Generic.Dictionary<IRExpr, IRExpr>(HashIdentity.Structural)
                    let lets = ResizeArray<IRId * IRExpr>()
                    let caps = ResizeArray<CaptureInfo>()
                    let evidence = ResizeArray<string>()
                    // The guard, when some value needs one and run time has
                    // to answer: its unnamed operands are bound first.
                    let operandLets, arrays, guard =
                        match nonEmpty.Value with
                        | Some plan when not plan.Checks.IsEmpty && (found |> Seq.exists mayAbort) ->
                            let named = ResizeArray<IRId * IRExpr>()
                            let arrays =
                                info.Arrays |> List.mapi (fun i a ->
                                    if List.contains i plan.Unnamed then
                                        let t = builder.FreshId()
                                        hoistedIds.Add t |> ignore
                                        materialized.Add t |> ignore
                                        named.Add((t, a))
                                        IRVar (t, typeOf a)
                                    else a)
                            let g =
                                plan.Checks
                                |> List.map (fun (i, d) ->
                                    IRBinOp (IRElementwise, IRGt, IRExtent (arrays.[i], d), IRLit (IRLitInt 0L), SrcLoc.Nowhere))
                                |> List.distinct
                                |> List.reduce (fun l r -> IRBinOp (IRElementwise, IRAnd, l, r, SrcLoc.Nowhere))
                            (List.ofSeq named, arrays, Some g)
                        | _ -> ([], info.Arrays, None)
                    for s in found do
                        let sTy = valueType s
                        let sId = builder.FreshId()
                        hoistedIds.Add sId |> ignore
                        // What moves out is rewritten like any other code: it
                        // may hold applies with invariants of their own.
                        let direct =
                            if inlineSafe s then rw s
                            else
                                // The helper is a nullary lambda CAPTURING
                                // what S reads -- directly, or through a
                                // callable it names -- under the kernel's
                                // own capture records: S is its body
                                // verbatim, a nested lambda still finds its
                                // captures by id, and the call site forwards
                                // them exactly as it does for the kernel (a
                                // grouped capture's side state included,
                                // which no ARGUMENT could carry). Captures,
                                // never parameters: a parameter sharing a
                                // module binding's id would make codegen
                                // read that binding as a parameter
                                // everywhere else it is captured.
                                let needed = System.Collections.Generic.HashSet<IRId>()
                                iterIRExpr (fun n ->
                                    match n with
                                    | IRVar (id, _) when captureIds.Contains id -> needed.Add id |> ignore
                                    | IRVar (id, _) ->
                                        (match Map.tryFind id funcs with
                                         | Some g -> for c in g.Captures do needed.Add c.Id |> ignore
                                         | None -> ())
                                    | _ -> ()) s
                                let used = k.Captures |> List.filter (fun c -> needed.Contains c.Id)
                                // The hoisted part of kernel `k`: it prints as `k`.
                                let helper = { mkLambdaCallable builder [] (rw s) sTy used false [] [] false false 256 false
                                                 with SourceName = k.SourceName }
                                mint kid helper
                                IRApp (IRVar (helper.Id, mkFuncArrow [] sTy), [], sTy)
                        let value, how =
                            match mayAbort s, guard with
                            | true, Some g -> IRIf (g, direct, (zeroOf sTy).Value), "before the nest, when the iteration space is non-empty"
                            | _ -> direct, "before the nest"
                        lets.Add((sId, value))
                        table.[s] <- IRVar (sId, sTy)
                        caps.Add { Id = sId; Name = $"__v{sId}"; Type = sTy; IsMutable = false }
                        evidence.Add $"`{show k s}` does not read the element: bound once as __v{sId}, {how}"
                    // Every occurrence reads the let -- also one in a
                    // conditional position, which can no longer fail anew.
                    let rec replaceAll (e: IRExpr) : IRExpr =
                        match table.TryGetValue e with
                        | true, v -> v
                        | _ ->
                            match e with
                            | ExprShape ([], _) -> e
                            | ExprShape (children, rebuild) -> rebuild (children |> List.map replaceAll)
                    let newId = builder.FreshId()
                    mint kid
                        { k with
                            Id = newId
                            Name = $"__lambda_{newId}"
                            Body = rw (replaceAll k.Body)
                            Captures = k.Captures @ List.ofSeq caps }
                    let kernel' = IRVar (newId, kty)
                    let loop' =
                        match info.Loop with
                        | IRObjectFor o when o.Kernel = info.Kernel -> IRObjectFor { o with Kernel = kernel' }
                        | l -> l
                    if decided.Add kid then
                        Blade.Effects.Decisions.record
                            { Blade.Effects.Rule = "invariant-hoist"; Version = 1
                              Span = Blade.Ast.noSpan; Subject = k.SourceName
                              Outcome = Blade.Effects.Applied
                              Evidence = List.ofSeq evidence }
                    Some (operandLets @ List.ofSeq lets, { info with Arrays = arrays; Kernel = kernel'; Loop = loop' })
             | _ -> None)
        | _ -> None

    let functions' = modul.Functions |> List.map (fun f -> { f with Body = rw f.Body })
    let bindings' = modul.Bindings |> List.map (fun b -> { b with Value = rw b.Value })
    if minted.Count = 0 then modul
    else
        let derived (f: IRCallable) : IRCallable list =
            match minted.TryGetValue f.Id with
            | true, l -> List.ofSeq l
            | _ -> []
        { modul with
            Functions = functions' |> List.collect (fun f -> f :: derived f)
            Bindings = bindings'
            DerivedFuncOrigins =
                minted |> Seq.fold (fun acc (KeyValue (origin, l)) ->
                    l |> Seq.fold (fun a c -> Map.add c.Id origin a) acc) modul.DerivedFuncOrigins }

/// The structural/05 D7 ADVISORY, never a rewrite: the checker/optimizer sees
/// `gram(A, A)` decompacted and applied to a vector by a row `prodsum` -- an
/// N x N matrix formed and read once -- and records that `gram_apply(A, A,
/// v)` declares the same action without the matrix. Recorded as a DECLINED
/// decision ("left as written") so `blade plan` shows it beside the others;
/// the fastest-way P3 advisory channel, when it exists, can lift it.
let private recordGramApplyAdvisory (modul: IRModule) : unit =
    let funcs = modul.Functions |> List.map (fun f -> (f.Id, f)) |> Map.ofList
    let values = modul.Bindings |> List.map (fun b -> (b.Id, b)) |> Map.ofList
    let rec collapse (e: IRExpr) = match e with IRCompute i -> collapse i | e -> e
    for b in modul.Bindings do
        match collapse b.Value with
        | IRApplyCombinator info ->
            (match info.Arrays, resolveCallableIn funcs info.Kernel with
             | [ IRVar (gdId, _) ], Some k ->
                // prodsum's two operands arrive in either order (the checker
                // may canonicalize them): one is the row parameter, the other
                // the vector.
                let rowAndVec (p: IRParam) (body: IRExpr) =
                    match body with
                    | IRProdSum [ IRVar (a, _); IRVar (b, _) ] when a = p.VarId -> Some b
                    | IRProdSum [ IRVar (a, _); IRVar (b, _) ] when b = p.VarId -> Some a
                    | _ -> None
                (match Map.tryFind gdId values, k.Params, k.Params |> List.tryHead |> Option.bind (fun p -> rowAndVec p k.Body) with
                 | Some gd, [ _ ], Some vid ->
                    (match gd.Value with
                     | IRDecompact (IRVar (gId, _), 0) ->
                        (match Map.tryFind gId values with
                         | Some g ->
                            (match g.Value with
                             | IRGram (IRVar (aId, _), IRVar (a2, _), true) when aId = a2 ->
                                let nameOf id = modul.Bindings |> List.tryFind (fun x -> x.Id = id) |> Option.map (fun x -> x.Name) |> Option.defaultValue $"__v{id}"
                                Blade.Effects.Decisions.record
                                    { Blade.Effects.Rule = "gram-apply-advisory"; Version = 1
                                      Span = Blade.Ast.noSpan; Subject = b.Name
                                      Outcome = Blade.Effects.Declined $"left as written (advise, never rewrite): `gram_apply({nameOf aId}, {nameOf aId}, {nameOf vid})` declares the same action without the N x N pool"
                                      Evidence = [ $"`{b.Name}` forms the Gram matrix of `{nameOf aId}` (gram, decompact) and applies it once by a row prodsum against `{nameOf vid}`; `gram_apply({nameOf aId}, {nameOf aId}, {nameOf vid})` declares the same action with two rank-1 temporaries and no N x N pool (docs/plans/structural/05)" ] }
                             | _ -> ())
                         | None -> ())
                     | _ -> ())
                 | _ -> ())
             | _ -> ())
        | _ -> ()

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
    /// two-pass form. Lives on the plan so it resets with the table.
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

/// One `rec-step-parallel` v1 decision record (the planner's admission and
/// its declines; the C++ emitter records the per-build applied / declined
/// outcome under the same rule).
let recordRecStep (subject: string) (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) =
    Blade.Effects.Decisions.record
        { Blade.Effects.Rule = "rec-step-parallel"; Version = 1
          Span = Blade.Ast.noSpan; Subject = subject
          Outcome = outcome; Evidence = evidence }

// --- Producer-input legs (round 3 A7c, rule `epilogue-legs` v1, gap G69) -----
//
// saxpy's region after the sibling-fold join:
//
//     let y2 = method_for(zip(x, y)) <@> k |> compute          (named, stored)
//     let s  = reduce(method_for(y2) <@> lambda(w) -> w * w, F, 0.0)   (B1)
//     let j  = reduce(id <@> x <&!> id <@> y, ((+), (+)), (-0.0, -0.0))
//     (s, j[0] + j[1])
//
// walks x and y twice: once in y2's producer loop (which B1 already fuses with
// s), once in j's joined pass. The legs of j fold EXACTLY the producer's input
// operands, so the producer loop can carry them as extra fold state, fed by
// the values it already loads: a STRICT leg as its own ascending chain from its
// own seed (one step per cell, the producer's order), a LICENSED leg (its OWN
// reorder licence -- never the map's, never another leg's, G7 policy) as its
// own K-lane state. Each leg's result is bound where the join was; the combine
// and everything downstream stay at their original points.
//
// The side table below is the whole interface: the IR keeps the join (the
// interpreter runs it as written), the producer's emitter carries the legs
// and publishes their accumulators, the join's emitter binds its tuple from
// them -- or, when the producer did not carry them in this lane and build,
// emits the ordinary joined pass. The admission is
// `producerEpilogueLegAdmission` (closed totality, dominating captures and
// seeds, operand / index equality, the five parties through the shared write
// analysis); the region is assembled by the sibling-fold pass, which -- when
// the producer sits in a callee the legs' statement calls (saxpy's
// `kernel(x, y)`) -- inlines that call one level ONLY for an admitted plan.

/// One input leg riding a producer's loop.
type EpilogueLeg = {
    /// The leg's position among the join's leaves (its tuple slot).
    Slot: int
    /// The producer operand the leg folds: its position in the producer's
    /// `Arrays` (its pool in the flat sweep) and its id.
    OperandPos: int
    OperandId: IRId
    /// The leg's fold kernel and seed, as the join carries them.
    Kernel: IRExpr
    Seed: IRExpr
    /// The leg's accumulator (result) type and the operand's element type.
    ElemType: IRType
    OperandElem: IRType
    /// The leg's OWN reorder licence in this build (planning view; each
    /// emitter re-derives it from its own predicate).
    Licensed: bool
    Name: string
    /// The LLVM lane's handshake: a fresh id the producer binds in its slot
    /// table and the join reads (never in the IR tree).
    SlotId: IRId
}

type EpilogueLegPlan = {
    ProducerId: IRId
    JoinId: IRId
    Legs: EpilogueLeg list
    ProducerName: string
    Subject: string
    /// The call the sibling pass inlined one level FOR this plan (`kernel(x,
    /// y)`), None when the producer was already in the region.
    InlinedCall: string option

    /// C++ handshake: per leg (slot order) the published name and whether it
    /// is a lane partial (true: the join applies seed + combine) or the
    /// finished chain (false). Set by the producer's flat sweep when it
    /// carried the legs, read and CLEARED by the join.
    EmittedCpp: (string * bool) list option ref
    /// An emitter recorded this plan's per-lane outcome already.
    DecidedCpp: bool ref
    DecidedLlvm: bool ref
}

module EpilogueLegTable =
    open System.Threading
    type State = { ByProducer: Map<IRId, EpilogueLegPlan>; ByJoin: Map<IRId, EpilogueLegPlan> }
    let private empty = { ByProducer = Map.empty; ByJoin = Map.empty }
    let private cell = new AsyncLocal<State>()
    let private get () = match box cell.Value with null -> empty | _ -> cell.Value
    let reset () = cell.Value <- empty
    /// False when the producer already carries another join's legs.
    let record (p: EpilogueLegPlan) : bool =
        let s = get ()
        if s.ByProducer.ContainsKey p.ProducerId || s.ByJoin.ContainsKey p.JoinId then false
        else
            cell.Value <- { ByProducer = Map.add p.ProducerId p s.ByProducer
                            ByJoin = Map.add p.JoinId p s.ByJoin }
            true
    let tryProducer (id: IRId) : EpilogueLegPlan option = Map.tryFind id (get ()).ByProducer
    let tryJoin (id: IRId) : EpilogueLegPlan option = Map.tryFind id (get ()).ByJoin

/// One `epilogue-legs` v1 decision (the planner's admission with its
/// five-party matrix; each emitter records its per-lane outcome under the
/// same rule).
let recordEpilogueLegs (subject: string) (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) =
    Blade.Effects.Decisions.record
        { Blade.Effects.Rule = "epilogue-legs"; Version = 1
          Span = Blade.Ast.noSpan; Subject = subject
          Outcome = outcome; Evidence = evidence }

/// One candidate leg as the region presents it.
type EpilogueLegSpec = {
    Name: string
    OperandId: IRId
    OperandName: string
    Kernel: IRExpr
    Seed: IRExpr
    SeedText: string
    /// The leg's own reorder licence in this build (repro: false).
    Licensed: bool
}

/// The region the admission judges: its statements in order, the producer's
/// statement, the join point (the statement the legs' results are bound
/// before) and every statement holding a leg (whose remainder is the legs'
/// OUTER combine).
type EpilogueLegRegion = {
    Subject: string
    ProducerName: string
    ProducerId: IRId
    ProducerInfo: ApplyInfo
    Stmts: (IRId option * string * IRExpr) array
    ProducerAt: int
    JoinAt: int
    MemberAts: int list
    Legs: EpilogueLegSpec list
}

/// What the admission asks of its caller's analyses.
type EpilogueLegFacts = {
    Resolve: IRExpr -> IRCallable option
    /// THE shared transitive write analysis (`scheduleMayWriteIn programFuncs
    /// modul`; an unresolved callee counts as a write).
    Writes: IRExpr -> bool
    /// The same analysis with every callback (a function-valued parameter)
    /// joined over the arguments its call sites pass (`callbackTable`): the
    /// producer's parallel body when it requests omp.
    CallbackWrites: IRExpr -> bool
    /// The CSE facts' repeatability (no display output, no effect).
    Repeatable: IRExpr -> bool
    /// The seed judge (the sibling pass's closed total whitelist).
    SeedTotal: IRExpr -> bool
    /// A statement's complete payload (`intervalPayloadOf`).
    PayloadOf: IRId -> IRExpr -> IRExpr
    ProviderEffect: IRId -> bool
    /// An IRAssign anywhere in the module (bindings' payloads, function
    /// bodies, the region) targets this id.
    AssignedAnywhere: IRId -> bool
    /// This build emits thread-level OpenMP.
    ThreadsBuild: bool
    /// The TRANSITIVE free names of an expression: its references, and for
    /// every callable it names (a kernel, a seed's callee, a nested function)
    /// that callable's captures and body, cycle-safe (the sibling pass's
    /// `freeVarsOf`). Dominance at the producer is judged on this closure,
    /// never on the immediate body alone (Codex W2-4 review finding 1).
    FreeVarsOf: IRExpr -> Set<IRId>
    /// Some why when an operand is not FRESH storage of its own -- a named
    /// view or alias of another array (`let xv = X(0)`, `let xa = x`): v1
    /// admits fresh operands only (a `|> compute`, a function result, a
    /// literal, a parameter), so operand identity is identity of storage.
    OperandNotFresh: IRId -> string option
}

/// THE A7c ADMISSION (rule `epilogue-legs` v1). Ok = the evidence (the
/// five-party matrix); Error = (the first failed condition, the matrix rows
/// established before it). Codex review A finding 11 is the checklist:
///   * the producer: a forced (`|> compute`) dense rank-1 literal-extent map,
///     plain one-level co-iteration over stored named arrays, no Reynolds,
///     cuda or mpi, never assigned; its kernel writes nothing (both the
///     shared analysis and, under an omp request, the callback-joined one);
///     NOT threaded in this build (a strict chain split per thread would not
///     be the serial chain; v1 builds no per-chunk licensed leg either);
///   * each leg: its operand is EXACTLY one of the producer's operands (the
///     same id: not a view, alias, slice or deferred map of it), over the
///     same index space and extent, never assigned; its kernel a resolved
///     two-parameter callable, CLOSED-TOTAL (Blade.IRTotality: no BL8013 /
///     BL8006 / BL8003 site, so running it at the producer cannot move a
///     failure ahead of an intervening binding's), writing nothing (G43);
///     its seed total and writing nothing; every capture / free name of the
///     kernel and the seed bound BEFORE the producer (it runs there);
///   * the outer combine (the remainder of every statement holding a leg)
///     writes nothing -- it stays where it is, reading the same values;
///   * every intervening binding (the producer .. the join point, and the
///     legs' own statements) by its complete payload: no write (an operand
///     array, a capture, a seed input or anything else), no provider effect,
///     repeatable.
let producerEpilogueLegAdmission (facts: EpilogueLegFacts) (r: EpilogueLegRegion) : Result<string list, string * string list> =
    let rows = ResizeArray<string>()
    let fail (why: string) = Error (why, List.ofSeq rows)
    let pinfo = r.ProducerInfo
    let legNames = r.Legs |> List.map (fun l -> l.Name) |> String.concat ", "
    let refs (e: IRExpr) = collectVarRefsIR e
    let opIds = pinfo.Arrays |> List.choose (function IRVar (id, _) -> Some id | _ -> None)
    let opNames =
        List.zip pinfo.Arrays (if pinfo.Identities.Length = pinfo.Arrays.Length then pinfo.Identities else List.replicate pinfo.Arrays.Length (AIDVariable "?"))
        |> List.map (fun (_, ident) -> match ident with AIDVariable n -> n | _ -> "?")
    let plainAxis (t: IRType) : (int64 * IRIndexType) option =
        match stripUnits t with
        | ArrayElem at when not at.IsVirtual ->
            (match at.IndexTypes with
             | [ ix ] when ix.IxKind = IxKPlain && ix.Symmetry = SymNone && ix.Rank = 1 ->
                (match ix.Extent with IRLit (IRLitInt n) when n > 0L -> Some (n, ix) | _ -> None)
             | _ -> None)
        | _ -> None
    match facts.Resolve (fst (peelReynolds pinfo.Kernel)) with
    | None -> fail "the producer's kernel does not resolve to a callable"
    | Some pk ->
    // ---- party 1: the producer kernel ----------------------------------
    let pAxis = plainAxis pinfo.OutputType
    if pAxis.IsNone || pinfo.KernelOutputRank <> 0 then fail $"`{r.ProducerName}` is not a dense rank-1 literal-extent producer"
    elif pinfo.HasReynolds || pk.IsCudaKernel || pk.IsMpiParallel then fail $"`{r.ProducerName}`'s kernel carries a Reynolds body or a cuda/mpi strategy"
    elif opIds.Length <> pinfo.Arrays.Length || pinfo.Arrays.IsEmpty || (pinfo.Arrays.Length > 1 && not pinfo.IsCoIteration) then
        fail $"`{r.ProducerName}` is not a plain co-iteration over stored named arrays"
    elif facts.AssignedAnywhere r.ProducerId then fail $"`{r.ProducerName}` is assigned after its binding (its stores are not the producer's alone)"
    elif facts.Writes (IRApplyCombinator pinfo) then
        fail $"the producer kernel of `{r.ProducerName}` may write (scheduleMayWriteIn): the legs' reads would interleave with its writes"
    elif pk.IsOmpParallel && facts.CallbackWrites (IRApplyCombinator pinfo) then
        fail $"the producer's parallel body may write through a callback (callbackTable)"
    else
    let (pn, pix) = pAxis.Value
    let ompNote = if pk.IsOmpParallel then "; its omp body audited with callbackTable's callbacks too" else ""
    let opsTxt = String.concat ", " opNames
    rows.Add $"producer kernel `{r.ProducerName}` = map over {{{opsTxt}}} ({pn} cells): no write (scheduleMayWriteIn){ompNote}; forced, stored, never assigned"
    if pk.IsOmpParallel && facts.ThreadsBuild then
        let strict = r.Legs |> List.filter (fun l -> not l.Licensed)
        if not strict.IsEmpty then
            let names = strict |> List.map (fun l -> l.Name) |> String.concat ", "
            fail $"the producer runs a thread team; the strict legs {names} keep their serial pass"
        else
            fail $"the producer runs a thread team; the licensed legs {legNames} keep their own pass (v1 builds no per-chunk leg partial: the standalone fold's chunk partition and combine are not shown to be the producer's)"
    else
    // ---- party 2: the leg kernels (operands, totality, writes) -----------
    let legProblem =
        r.Legs |> List.tryPick (fun l ->
            if not (List.contains l.OperandId opIds) then
                if l.OperandId = r.ProducerId then
                    Some $"leg {l.Name} folds the producer's OUTPUT `{r.ProducerName}` (the cell epilogue owns the output; an input leg reads one of the producer's operands)"
                else
                    Some $"leg {l.Name} folds `{l.OperandName}`, which is not one of `{r.ProducerName}`'s operands (an input leg reads exactly the producer's operand -- not an alias, view, slice or deferred map of it)"
            else
            let pos = opIds |> List.findIndex (fun id -> id = l.OperandId)
            match plainAxis (typeOf pinfo.Arrays.[pos]) with
            | Some (n, ix) when n = pn && IRTotality.sameIndex ix pix ->
                if facts.AssignedAnywhere l.OperandId then Some $"leg {l.Name}'s operand `{l.OperandName}` is assigned somewhere in the module"
                else
                match facts.OperandNotFresh l.OperandId with
                | Some why -> Some $"leg {l.Name}'s operand `{l.OperandName}` is {why} (v1 admits fresh operands only: a `|> compute`, a function result, a literal or a parameter -- a shared named view is not proved to be the same storage at both traversals)"
                | None ->
                match facts.Resolve l.Kernel with
                | None -> Some $"leg {l.Name}'s fold kernel does not resolve to a callable"
                | Some kc when kc.Params.Length <> 2 || kc.IsCudaKernel || kc.IsMpiParallel -> Some $"leg {l.Name}'s fold kernel is not a plain two-parameter callable"
                | Some kc when kc.IsOmpParallel && facts.ThreadsBuild ->
                    Some $"leg {l.Name}'s fold kernel requests omp in a build that emits threads: riding the serial producer loop would drop its thread team (the joined pass keeps it)"
                | Some kc ->
                    match IRTotality.auditCallable facts.Resolve (fun _ -> None) (fun _ -> false) kc [] with
                    | Error why -> Some $"leg {l.Name}'s fold kernel is not closed-total ({why}): running it at the producer could move its failure ahead of an intervening binding's"
                    | Ok () ->
                        if facts.Writes l.Kernel then Some $"leg {l.Name}'s fold kernel may write (G43)"
                        else None
            | _ -> Some $"leg {l.Name}'s operand `{l.OperandName}` is not over `{r.ProducerName}`'s index space and extent")
    match legProblem with
    | Some why -> fail why
    | None ->
    rows.Add $"leg kernels {legNames}: each folds one of the producer's own operands (same id, index space and extent; never assigned); closed-total (IRTotality); no write (scheduleMayWriteIn)"
    // ---- party 3: the seeds (and the captures) dominate the producer -----
    let insideIds =
        [ r.ProducerAt .. r.JoinAt - 1 ]
        |> List.choose (fun i -> let (b, n, _) = r.Stmts.[i] in b |> Option.map (fun id -> (id, n)))
    let kernelFree (k: IRExpr) =
        match facts.Resolve k with
        | Some kc -> Set.union (refs kc.Body) (kc.Captures |> List.map (fun c -> c.Id) |> Set.ofList)
        | None -> Set.empty
    let seedProblem =
        r.Legs |> List.tryPick (fun l ->
            if not (facts.SeedTotal l.Seed) then Some $"leg {l.Name}'s seed may fail or has effects"
            elif facts.Writes l.Seed then Some $"leg {l.Name}'s seed may write"
            else
            // TRANSITIVE (finding 1): the kernel's and the seed's callees, their
            // captures and their bodies, followed through every resolved callable.
            let kf = Set.union (kernelFree l.Kernel) (facts.FreeVarsOf l.Kernel)
            let sf = Set.union (refs l.Seed) (facts.FreeVarsOf l.Seed)
            match insideIds |> List.tryFind (fun (id, _) -> Set.contains id kf) with
            | Some (_, n) -> Some $"leg {l.Name}'s fold kernel reads `{n}` (directly or through a callee it names), which is bound at or after the producer (the leg would run before it exists)"
            | None ->
            match insideIds |> List.tryFind (fun (id, _) -> Set.contains id sf) with
            | Some (_, n) -> Some $"leg {l.Name}'s seed reads `{n}` (directly or through a callee it names), which is bound at or after the producer (a strict chain starts at the producer)"
            | None -> None)
    match seedProblem with
    | Some why -> fail why
    | None ->
    let seedTxt = r.Legs |> List.map (fun l -> $"{l.Name}: {l.SeedText}") |> String.concat ", "
    rows.Add $"seeds ({seedTxt}): total, no write, every input (and every capture of the leg kernels, transitively through every callee they name) bound before the producer; each evaluated once, at its chain's start"
    // ---- parties 4-5: the outer combine and the intervening bindings ------
    let between = [ r.ProducerAt + 1 .. r.JoinAt - 1 ] @ r.MemberAts |> List.distinct |> List.sort
    let stmtProblem =
        between |> List.tryPick (fun i ->
            let (b, n, v0) = r.Stmts.[i]
            let v = match b with Some id -> facts.PayloadOf id v0 | None -> v0
            let isMember = List.contains i r.MemberAts
            let what = if isMember then $"the legs' own statement `{n}` (their outer combine)" else $"`{n}`"
            if facts.Writes v then Some $"{what} may write between the producer and the legs (scheduleMayWriteIn): the legs would read their operands before it"
            elif (match b with Some id -> facts.ProviderEffect id | None -> false) then Some $"{what} has a provider effect between the producer and the legs"
            elif not (facts.Repeatable v) then Some $"{what} is not repeatable (display output or a call the module cannot judge) between the producer and the legs"
            else None)
    match stmtProblem with
    | Some why -> fail why
    | None ->
    let memberNames = r.MemberAts |> List.map (fun i -> let (_, n, _) = r.Stmts.[i] in $"`{n}`") |> String.concat ", "
    rows.Add $"outer kernel ({memberNames}: the combine of the legs' results): no write; it stays at its original point"
    let interNames =
        [ r.ProducerAt + 1 .. r.JoinAt - 1 ] |> List.map (fun i -> let (_, n, _) = r.Stmts.[i] in $"`{n}`")
    let interTxt = if interNames.IsEmpty then "none" else String.concat ", " interNames
    rows.Add $"intervening ({interTxt}; the legs' statements included): no write (by complete payload), no provider effect, repeatable"
    Ok (List.ofSeq rows)

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

/// One planned membership hoist (`planMembershipHoist`, below): the structure
/// over set `SetId` built before host `HostId`, read by the probes of `SetId`
/// in the host's kernels `Kernels` (and in the host's own emission, where a
/// lane inlines those kernels). Defined here, ahead of compaction fusion,
/// which reads it: the plan is the PROOF INPUT of T4c's membership-probe
/// certificate (`Admission` is the planner's own five-party record).
type MemberHoistPlan = {
    HostId: IRId
    HostName: string
    SetId: IRId
    SetName: string
    Kernels: Set<IRId>
    /// 0 = the chooser, 1 / 2 / 3 = BLADE_MEMBER_HOIST=linear / bitmap / hash.
    Forced: int
    /// The admission record (`membershipAdmits`' matrix) that let it through.
    Admission: string
}

/// AsyncLocal like CompactionTable; reset once per program in
/// `lowerTypedProgram` (let ids restart per compile).
module MemberHoistTable =
    open System.Threading
    let private cell = new AsyncLocal<Map<IRId, MemberHoistPlan list>>()
    let private get () = match box cell.Value with null -> Map.empty | _ -> cell.Value
    let reset () = cell.Value <- Map.empty
    let record (p: MemberHoistPlan) =
        let s = get ()
        let prev = Map.tryFind p.HostId s |> Option.defaultValue []
        cell.Value <- Map.add p.HostId (prev @ [ p ]) s
    /// The structures to build before host `hostId`, in plan order.
    let plansOf (hostId: IRId) : MemberHoistPlan list = Map.tryFind hostId (get ()) |> Option.defaultValue []
    /// The plan whose structure a probe of set `setId` may read while the
    /// declaration `declId` is being emitted: the host itself, or one of its
    /// probing kernels.
    let tryProbe (declId: IRId) (setId: IRId) : MemberHoistPlan option =
        get () |> Map.toSeq |> Seq.collect snd
        |> Seq.tryFind (fun p -> p.SetId = setId && (p.HostId = declId || p.Kernels.Contains declId))
    /// Every plan, hosts in id order then plan order.
    let all () : MemberHoistPlan list = get () |> Map.toList |> List.collect snd

// --- Compaction fusion: mask -> compound -> fold (round 2 item B4) ----------
//
// The WHERE idiom of examples/01 at its smallest:
//
//   let keep = mask(x, lambda(v) -> v > 1.0)
//   let y = compound(x, keep)
//   let checksum = reduce(y * y, (+)) + Float64(extents(y))
//
// emits SIX sweeps in both lanes: the mask, a count of the mask, the
// branchless kept-position fill, the gather by position, a `y * y` compound
// pool (the elementwise operand forced because the fused fold terminal has
// no compound arm), and the fold over that pool. The plan below removes four
// of them without touching what any binding MEANS -- every named store stays
// (`keep`, `y`, its index with its kept positions), and the IR the
// interpreter runs is untouched (a side table, the HotRowTable pattern):
//
//   * B4b, the COMPACTION plan (`CompactionPlan`): the count of `keep` is
//     taken inside its own mask pass, and the position fill and the value
//     gather are one branchless pass `v = x[i]; y[q] = v; pos[q] = i;
//     q += keep[i]` (m + 1 cells in BOTH buffers, the last the slack the
//     unconditional store needs, never exposed). The index is then built
//     from the table the pass filled (compound_index_t<1>'s adopting
//     constructor): dense extent n, cardinality m, the ascending kept
//     positions -- the table the old constructor built. Stable, ascending,
//     serial, exactly as before.
//   * B4a, the FOLD plan (`CompactionFoldPlan`): the self-product's fold
//     reads `y`'s compact buffer directly (`y.data`, m contiguous cells), no
//     square pool. The fold form is the emitter's: the licensed K-lane tree
//     (`blade_fold::sq`) in the C++ lane, the reassociable chain in the LLVM
//     lane; under BLADE_FP_REPRO=1 the unseeded serial chain from the first
//     kept square, in kept order. BL8003 on an empty `y` stays at the fold's
//     own declaration, after the compaction, as before.
//   * B4c: under the serial (repro) form, when the compaction itself is
//     fused, the chain is FED INSIDE the compaction pass, block by block,
//     from cells already written (index < q, never the slack cell). Its
//     value is the same chain in the same order -- the map is the pure
//     self-product of `y`'s own cells, the fold a builtin `+`, `y` is never
//     assigned -- and the empty check still runs at the fold's own site.
//
// Round 3 (T4, `[compaction-fusion v2]`; `b4` keeps exactly the round-2 form
// above) adds two schedule changes on the same side table -- T4b is the
// DEFAULT, T4a is OPT-IN (`gather`) this round (its own measured gain is
// under the round's acceptance line):
//
//   * T4b, positions ELIDED: the kept-position table is compiler metadata,
//     not a named output. A fail-safe DEMAND CERTIFICATE over `y` (and every
//     elementwise map derived from it, which shares its index) proves that
//     nothing reads it: every use must be a fold, `extents`, a derived map,
//     or printing (which reads the compact buffer); a keyed read, an alias,
//     an argument, a tuple, an assignment, a capture, a provider write, a
//     side-table operand or ANY use the census does not recognize demands
//     the table. Certified, the index is built cardinality-only
//     (compound_index_t<1>'s positions-elided constructor: n and m intact;
//     every lookup through the absent table is an internal error, BL9002,
//     never a read of a null pointer).
//   * T4a, the gather AT THE MASK SITE: `keep`'s own pass sweeps x once:
//     per 256-cell block, the predicate loop (vectorizable, as the counted
//     mask pass is), then the branchless gather of the kept values (and
//     positions, when built) over that L1-resident block, into n + 1 private
//     cells per buffer (m + 1 touched by the n branchless stores); under the
//     serial form B4c's chain is fed after each block. (One scalar loop doing
//     both measured SLOWER than B4's two passes over a cached x: the
//     predicate loses its vector form.) `keep` and `y` stay stored.
//     Admission is `selectionAdmits`: the five parties of the motion (the producer x, the
//     predicate, every planned outer fold's kernel, the seeds, every binding
//     between the mask and the compound) through the shared write analysis
//     (`scheduleMayWriteIn`); a writing party declines to the B4 form, never
//     further. The n + 1 reservation is capped (BLADE_TILE_TUNE sel_cap):
//     above it, at compile time or by a run-time test, B4's two passes run.
//
// Every admission audits a binding's whole PAYLOAD (`bindingPayload`: its
// value and the side-table operands its site runs), never the placeholder
// value of a compound / sparse / random binding.
//
// Gate: BLADE_COMPACTION_FUSION (`0`/`off`, `b4`, default = T4b, `gather` =
// T4b + T4a).

/// T4a's C++ handshake: what the mask pass gathered into, published by the
/// mask's emission and read by the compound's.
type CompactionGatherCpp = {
    /// The value buffer (`new T[n + 1]`; nullptr until the guard holds).
    Compact: string
    /// The kept-position buffer, when the table is built.
    Pos: string option
    /// `None`: the single pass ALWAYS ran (static n within the cap). `Some
    /// g`: a run-time test -- the compound runs B4's pass itself when `g`
    /// is false.
    Guard: string option
    /// The in-pass chains (accumulator, kernel wrapper), so the compound's
    /// fallback pass feeds the same accumulators.
    Feeds: (string * string) list
}

/// The LLVM lane's twin: pool slots (alloca'd `ptr`), the count slot, the
/// run-time guard (an i1 register computed before any branch), and the
/// in-pass chain accumulators with their fold callables' plan ids.
type CompactionGatherLlvm = {
    DstSlot: string
    PosSlot: string option
    CountSlot: string
    Guard: string option
    Feeds: (IRId * string) list
}

/// T4c's C++ handshake: what the shared selection's ONE pass filled (at the
/// mask under `gather`, else at the first companion), published by that site
/// and adopted by every companion.
type SharedSelectionCpp = {
    /// The shared `compound_index_t<1>*` once the first companion built it.
    Index: string option
    /// Each companion's compact value buffer (compound id -> C++ variable).
    Compacts: Map<IRId, string>
    /// The kept-position buffer, when any companion demands the table.
    Pos: string option
    /// T4a's run-time cap guard (the first companion runs the joint B4 pass
    /// itself when it is false).
    Guard: string option
    /// The in-pass chains (accumulator, kernel wrapper, the companion buffer
    /// it reads), so a fallback pass feeds the same accumulators.
    Feeds: (string * string * string) list
    /// The single pass ran in the mask pass (T4a), not at the first companion.
    AtMask: bool
}

/// The LLVM lane's twin: pool slots (alloca'd `ptr`) per companion, the
/// position slot, the count slot, the run-time guard, the in-pass chains
/// (fold plan id, accumulator, the companion's pool slot).
type SharedSelectionLlvm = {
    DstSlots: Map<IRId, string>
    PosSlot: string option
    CountSlot: string
    Guard: string option
    Feeds: (IRId * string * IRId) list
    AtMask: bool
    /// The first companion completed the selection (the pools are filled).
    Ready: bool
}

/// T4c: ONE selection for every `y_j = compound(c_j, keep)` over the same
/// let-bound rank-1 mask (each c_j over the mask source's index space). The
/// plan OWNS the index: built once (counted in the mask pass; its kept
/// positions, when demanded, filled by the one joint pass), shared by every
/// companion's Compound wrapper, freed once -- every companion owns only its
/// own compact data store (`deallocate_compound_shared_index`), the index's
/// single index-only free is registered with the LAST companion.
type SharedSelection = {
    MaskId: IRId
    MaskName: string
    /// The companions in declaration order: (compound id, compound name,
    /// dense source id).
    Members: (IRId * string * IRId) list
    /// Each companion's element type (compound id -> element).
    ElemTypes: Map<IRId, IRType>
    /// The union of the companions' position demands (T4b census per
    /// companion: one keyed read anywhere keeps the shared table).
    PositionsNeeded: bool
    /// The joint gather runs in the mask pass (T4a, `gather`, J <= 4, every
    /// source bound before the mask, within sel_cap).
    GatherAtMask: bool
    StaticN: int64 option
    /// sel_cap as a cell count over EVERY companion buffer and the positions.
    SelCapCells: int64
    BuiltCpp: SharedSelectionCpp option ref
    BuiltLlvm: SharedSelectionLlvm option ref
}

/// The fused compaction of one `let y = compound(x, keep)` over a let-bound
/// rank-1 `keep = mask(x, pred)`.
type CompactionPlan = {
    MaskId: IRId
    MaskName: string
    CompoundId: IRId
    CompoundName: string
    SourceId: IRId
    /// T4b: build the kept-position table (a consumer demands it, the census
    /// could not prove otherwise, or the `b4` mirror).
    PositionsNeeded: bool
    /// T4a: the gather runs in the mask pass (subject to `SelCapCells`).
    GatherAtMask: bool
    /// T4a's static extent, when the source's is a literal (the cap was then
    /// decided at plan time and the emitters take the single pass outright).
    StaticN: int64 option
    /// The selectivity cap as a cell count: the single pass is taken iff
    /// n < SelCapCells, i.e. (n + 1) x (bytes per cell over every buffer)
    /// <= sel_cap -- the product checked by division, never overflowing.
    SelCapCells: int64
    /// The C++ lane's handshake: the mask emitter's count variable, written
    /// when it actually counted (its rank-1 callable arm), read by the
    /// compound's emission to choose the fused pass. Reset by the mask's
    /// emission, so a second emission of the same program starts clean.
    CountCpp: string option ref
    /// The LLVM lane's twin: the alloca slot holding the count.
    CountLlvm: string option ref
    /// T4a: what the mask pass gathered (reset with the count).
    GatherCpp: CompactionGatherCpp option ref
    GatherLlvm: CompactionGatherLlvm option ref
    /// T4c: the shared selection this compound is a companion of (every
    /// companion's plan carries the SAME record and the same count refs).
    Shared: SharedSelection option
}

/// The fused fold `reduce(y * y, (+))` of one compound `y`: the lifted map
/// let (`MapId`, the anonymous `y * y` the fold forces today) is never
/// materialized; the fold reads `y`'s buffer.
type CompactionFoldPlan = {
    MapId: IRId
    CompoundId: IRId
    CompoundName: string
    /// The binding hosting the fold (`checksum`).
    HostName: string
    FoldKernel: IRExpr
    /// The compaction of `y` is fused and `y` is never assigned, so the
    /// compaction pass MAY feed the serial chain (B4c); whether it does is
    /// the emitter's call (the serial form only), published below.
    InPass: bool
    /// Each lane's handshake: the accumulator the compaction pass fed the
    /// chain into (B4c), when it did. Reset by the compound's emission.
    EmittedCpp: string option ref
    EmittedLlvm: string option ref
}

/// AsyncLocal like HotRowTable; reset once per program in
/// `lowerTypedProgram`, because let ids restart per compile.
module CompactionTable =
    open System.Threading
    type State =
        { ByMask: Map<IRId, CompactionPlan>
          ByCompound: Map<IRId, CompactionPlan>
          FoldByMap: Map<IRId, CompactionFoldPlan>
          FoldsByCompound: Map<IRId, CompactionFoldPlan list> }
    let private empty = { ByMask = Map.empty; ByCompound = Map.empty; FoldByMap = Map.empty; FoldsByCompound = Map.empty }
    let private cell = new AsyncLocal<State>()
    let private get () = match box cell.Value with null -> empty | _ -> cell.Value
    let reset () = cell.Value <- empty
    /// ONE plan per mask: the FIRST recorded (a shared selection records its
    /// companions in declaration order, so the mask's plan is the first
    /// companion's, whose `Shared` names them all). A second plan over the
    /// same mask never overwrites the mask's entry -- before T4c the planner
    /// let only one compound per mask through (the others declined), so the
    /// map's last-write-wins was unreachable; it is now refused outright.
    let record (p: CompactionPlan) =
        let s = get ()
        let byMask = if Map.containsKey p.MaskId s.ByMask then s.ByMask else Map.add p.MaskId p s.ByMask
        cell.Value <- { s with ByMask = byMask; ByCompound = Map.add p.CompoundId p s.ByCompound }
    let recordFold (f: CompactionFoldPlan) =
        let s = get ()
        let prev = Map.tryFind f.CompoundId s.FoldsByCompound |> Option.defaultValue []
        cell.Value <- { s with FoldByMap = Map.add f.MapId f s.FoldByMap
                               FoldsByCompound = Map.add f.CompoundId (prev @ [ f ]) s.FoldsByCompound }
    let tryMask (id: IRId) : CompactionPlan option = Map.tryFind id (get ()).ByMask
    let tryCompound (id: IRId) : CompactionPlan option = Map.tryFind id (get ()).ByCompound
    let tryFoldMap (id: IRId) : CompactionFoldPlan option = Map.tryFind id (get ()).FoldByMap
    let foldsOf (compoundId: IRId) : CompactionFoldPlan list =
        Map.tryFind compoundId (get ()).FoldsByCompound |> Option.defaultValue []

/// The extent expressions a TYPE carries (its index records' extents, a tagged
/// index's anonymous extent, nested types' extents): what the generic child
/// walk (`childrenOf`) never enters.
let rec irTypeExtentExprs (t: IRType) : IRExpr list =
    match t with
    | IRTScalar _ | IRTUnit | IRTNat _ | IRTNamed _ | IRTInfer _ -> []
    | IRTTuple ts -> List.collect irTypeExtentExprs ts
    | IRTLoop l -> List.collect irTypeExtentExprs l.ArrayTypes @ (l.KernelType |> Option.map irTypeExtentExprs |> Option.defaultValue [])
    | IRTComputation t | IRTPoly (t, _) | IRTUnitAnnotated (t, _) -> irTypeExtentExprs t
    | IRTIdxTagged (t, tag) -> irTypeExtentExprs t @ (match tag with IRefAnon (_, e) -> [ e ] | _ -> [])
    | IRTDist (_, t, axes) -> irTypeExtentExprs t @ (axes |> List.map (fun ix -> ix.Extent))
    | IRTGroupKeys (a, b, _) -> [ a.Extent; b.Extent ]
    | IRTArrow (slots, r, _) ->
        (slots |> List.collect (function SIdx ix | SIdxVirt ix -> [ ix.Extent ] | SVal st -> irTypeExtentExprs st)) @ irTypeExtentExprs r

let private arrayTypeExtentExprs (at: IRArrayType) : IRExpr list =
    (at.IndexTypes |> List.map (fun ix -> ix.Extent)) @ irTypeExtentExprs at.ElemType

/// Every type and index expression embedded in the nodes of `e` -- a
/// variable's or call's type, an array literal's, an apply's output, operand
/// and shared index types, a range's index records, an `ungroup`'s source
/// index records -- as extent expressions; and, recursively, those embedded
/// in the extracted extents themselves (types are finite trees, so this ends;
/// a depth bound keeps it linear-ish on pathological nesting -- past it the
/// extent itself is still returned, and its references still collected).
let embeddedExtentExprs (e: IRExpr) : IRExpr list =
    let acc = ResizeArray<IRExpr>()
    let rec visit (depth: int) (root: IRExpr) =
        let found = ResizeArray<IRExpr>()
        let addTy (t: IRType) = found.AddRange(irTypeExtentExprs t)
        let addIx (ixs: IRIndexType list) = for ix in ixs do found.Add ix.Extent
        iterIRExpr (fun n ->
            match n with
            | IRVar (_, ty) | IRParam (_, _, ty) | IRZero ty | IRUninit ty | IRApp (_, _, ty) -> addTy ty
            | IRArrayLit (_, at) -> found.AddRange(arrayTypeExtentExprs at)
            | IRApplyCombinator info ->
                addTy info.OutputType
                for at in info.ArrayTypes do found.AddRange(arrayTypeExtentExprs at)
                addIx info.SharedIndexTypes
                addIx info.KernelTDims
            | IRComposeApply info -> addTy info.OutputType
            | IRMethodFor mf ->
                for at in mf.ArrayTypes do found.AddRange(arrayTypeExtentExprs at)
                addIx mf.SharedIndexTypes
            | IRRange (ixs, _) -> addIx ixs
            | IRVirtualReverse ix -> addIx [ ix ]
            // the source index records `childrenOf` does not hand out
            | IRUngroup (_, src) -> addIx [ src ]
            | IRUngroupRows (_, _, src) -> addIx [ src ]
            | IRUngroupGrid (_, srcs, _) -> addIx srcs
            | _ -> ()) root
        for x in found do
            acc.Add x
            if depth < 8 then visit (depth + 1) x
    visit 0 e
    List.ofSeq acc
/// A root of THE MODULE TRAVERSAL that the compaction census (T4b) and the
/// foreign-demand collection (`moduleReferencedIds`, which Lowering hands to
/// every other module's census) share: every executable expression of the
/// module -- binding values, the side-table operands executed at a binding's
/// site, function bodies -- every declared type, and every side table that
/// names a binding by id or by name.
type CompactionRoot =
    | RootBindingValue of IRBinding
    | RootSideOperand of IRBinding * string * IRExpr
    | RootFunctionBody of IRCallable
    | RootType of string * IRType
    | RootSourceRef of string * IRId
    | RootNameRef of string * string

let compactionRoots (callables: IRCallable list) (modul: IRModule) : CompactionRoot list =
    let byId = modul.Bindings |> List.map (fun b -> (b.Id, b)) |> Map.ofList
    let sideLabel (id: IRId) =
        if modul.CompoundInits.ContainsKey id then "a compound() operand"
        elif modul.SparseInits.ContainsKey id then "a sparse() operand"
        else "a random fill's key, parameter, weights or address"
    [ for td in modul.Types do
        match td with
        | IRTDAlias (n, t) -> yield RootType ($"type `{n}`", t)
        | IRTDStruct (n, fs) ->
            for (_, t) in fs do yield RootType ($"struct `{n}`", t)
        | IRTDVariant (n, vs) ->
            for (_, t) in vs do
                match t with Some t -> yield RootType ($"variant `{n}`", t) | None -> ()
        | IRTDIndexType (n, ix) | IRTDEnumIdx (n, ix, _) ->
            yield RootType ($"index type `{n}`", IRTArrow ([ SIdx ix ], IRTUnit, None))
      for b in modul.Bindings do
        yield RootBindingValue b
        for op in bindingSideOperands modul b.Id do yield RootSideOperand (b, sideLabel b.Id, op)
        yield RootType ($"`{b.Name}`", b.Type)
      for f in callables do
        yield RootFunctionBody f
        for p in f.Params do yield RootType ($"`{f.SourceName}`", p.Type)
        yield RootType ($"`{f.SourceName}`", f.RetType)
        for c in f.Captures do yield RootType ($"`{f.SourceName}`", c.Type)
      for KeyValue (wid, w) in modul.ProviderWrites do
        let who = match Map.tryFind wid byId with Some b -> $" (`{b.Name}`)" | None -> ""
        yield RootSourceRef ($"a provider write{who}", w.SourceId)
      for KeyValue (_, r) in modul.ProviderReads do
        match r.MaskName with
        | Some mn -> yield RootNameRef ("a provider read masked by", mn)
        | None -> () ]

/// Every binding / callable id this module's executable code, types and side
/// tables name (`compactionRoots` over the module's own functions): what
/// another module's census must treat as an uncertified use when one of them
/// is its compound.
let moduleReferencedIds (modul: IRModule) : Set<IRId> =
    let acc = System.Collections.Generic.HashSet<IRId>()
    let addExpr (e: IRExpr) =
        for id in collectVarRefsIR e do acc.Add id |> ignore
        for x in embeddedExtentExprs e do
            for id in collectVarRefsIR x do acc.Add id |> ignore
    let addTy (t: IRType) =
        for x in irTypeExtentExprs t do addExpr x
    for root in compactionRoots modul.Functions modul do
        match root with
        | RootBindingValue b -> addExpr b.Value
        | RootSideOperand (_, _, e) -> addExpr e
        | RootFunctionBody f ->
            addExpr f.Body
            for c in f.Captures do acc.Add c.Id |> ignore
        | RootType (_, t) -> addTy t
        | RootSourceRef (_, id) -> acc.Add id |> ignore
        | RootNameRef _ -> ()
    Set.ofSeq acc
/// THE POSITIVE STORAGE PROOF of a top-level array binding (Codex diff review
/// of W2-1, finding 2; shared since W2-2's fix round with compaction fusion's
/// shared selection, whose joint pass reads every source's pool by name): the
/// cells must EXIST as a stored pool at the binding's own site. Membership
/// hoisting applies it to a probed set, the shared selection to the mask
/// source and every companion source. Returns how the cells are stored and
/// the alias chain (the named binding first, the root last).
let storedArrayProof (modul: IRModule) : IRBinding -> Result<string * IRBinding list, string> =
    let arr = List.toArray modul.Bindings
    let indexOf = arr |> Array.mapi (fun i b -> (b.Id, i)) |> Map.ofArray
    let isPlaceholder (b: IRBinding) =
        modul.CompoundInits.ContainsKey b.Id || modul.SparseInits.ContainsKey b.Id || modul.RandomInits.ContainsKey b.Id
    // THE POSITIVE STORAGE PROOF of a set at the build point (Codex diff
    // review of W2-1, finding 2): the set's cells must EXIST, as a stored
    // pool, before the host's first statement -- so its value must be one of
    // the forms that are materialized at their own site (an array literal, a
    // `|> compute`, a compound, an eager set operation, a call's array
    // result, a non-streamed provider read), reached through any chain of
    // top-level let ALIASES (`let B = A`, each hop an earlier binding;
    // `isDeferredForm` of an alias's own value -- an IRVar -- proves nothing
    // about the cells: the C++ lane forces and deep-copies an ASSIGNABLE
    // alias of a deferred map at the alias's site, the ordinary `let B =
    // lazy` case, but propagates the deferral through a non-assignable one,
    // so v1 proves the root and declines a deferred root either way). A virtual
    // array (a range), a deferred map (directly or through an alias), a
    // streamed read and every other form are not proved stored. Returns how
    // the cells are stored and the chain (the named set first, the root
    // last).
    let rec storageOf (seen: Set<IRId>) (b: IRBinding) : Result<string * IRBinding list, string> =
        let isVirtual =
            match stripUnits b.Type with
            | ArrayElem at -> at.IsVirtual
            | _ -> false
        if isVirtual then Error $"`{b.Name}` is a virtual array (it has no stored cells)"
        else
        match Map.tryFind b.Id modul.ProviderReads with
        | Some r when r.Streamed -> Error $"`{b.Name}` is a streamed provider read (never materialized)"
        | Some _ -> Ok ("a provider read, materialized at its site", [ b ])
        | None ->
        if modul.CompoundInits.ContainsKey b.Id then Ok ("a compound, built at its site", [ b ])
        elif isPlaceholder b then Error $"`{b.Name}` is a side-table value v1 does not prove stored"
        else
        match b.Value with
        | IRVar (aid, _) ->
            match Map.tryFind aid indexOf, Map.tryFind b.Id indexOf with
            | Some ai, Some bi when ai < bi && not (seen.Contains aid) ->
                match storageOf (seen.Add b.Id) arr.[ai] with
                | Ok (how, chain) -> Ok ($"an alias of `{arr.[ai].Name}`, {how}", b :: chain)
                | Error why -> Error $"`{b.Name}` is an alias of `{arr.[ai].Name}`, and {why}"
            | _ -> Error $"`{b.Name}` is an alias of a value that is not an earlier top-level binding"
        | v when isDeferredForm v -> Error $"`{b.Name}` is a deferred computation"
        | IRArrayLit _ -> Ok ("an array literal", [ b ])
        | IRCompute _ -> Ok ("a `|> compute`", [ b ])
        | IRMask _ | IRSort _ | IRUnique _ | IRIntersect _ | IRUnion _ -> Ok ("an eager set operation", [ b ])
        | IRApp _ -> Ok ("a call's array result", [ b ])
        | _ -> Error $"`{b.Name}`'s value is not a form v1 proves stored (an array literal, `|> compute`, a compound, an eager set operation, a call's result or a provider read, through aliases)"
    storageOf Set.empty

/// PM19's first storage-elision arm. Unlike the compaction side-table plans,
/// this rewrites the tree BOTH evaluators run, BEFORE any schedule table is
/// built. Only an explicitly unprinted, otherwise private mask/compound pair
/// and one direct seeded scalar fold, or two such folds in one adjacent tuple
/// host, qualify. `compound` is top-level-only
/// today (BL6002), hence the explicit print-selection requirement here.
///
/// Repro builds only: the guarded kernel has no reorder licence. Converting a
/// licensed K-lane fold to this serial chain would change its operation graph.
/// No unseeded, selected-map, membership-predicate or cross-binding join arm.
let fuseSelectedTerminalModule (builder: IRBuilder) (programFuncs: IRCallable list)
                               (foreignRefs: Set<IRId>) (modul: IRModule) : IRModule =
    if modul.CompoundInits.IsEmpty then modul
    else
    let mode = compactionFusionMode ()
    let enabled = mode = CompactionFusionT4 || mode = CompactionFusionGather
    let funcs = (programFuncs @ modul.Functions) |> List.map (fun f -> f.Id, f) |> Map.ofList
    let writes = scheduleMayWriteIn programFuncs modul
    let lifetimeBarrier = scheduleHasLifetimeBarrierIn programFuncs modul
    let facts = cseFactsOf funcs
    let payloadWrites b = bindingPayload modul b |> List.exists (fun e -> (facts e).MayWrite || writes e)
    // Same separators/default as CodeGenState.printSelection, which compiles
    // later. Empty/unset observes every named top-level mask and selection.
    let selectedNames =
        match System.Environment.GetEnvironmentVariable "BLADE_PRINT" with
        | null -> Set.empty
        | s -> s.Split([| ','; ' '; ';' |], System.StringSplitOptions.RemoveEmptyEntries)
               |> Array.map (fun n -> n.Trim()) |> Array.filter ((<>) "") |> Set.ofArray
    let allCallables = (modul.Functions @ programFuncs) |> List.distinctBy (fun f -> f.Id)
    let roots = compactionRoots allCallables modul
    let arr = List.toArray modul.Bindings
    let indexOf id = arr |> Array.tryFindIndex (fun b -> b.Id = id)
    let paramRef (p: IRParam) = function
        | IRVar (id, _) -> id = p.VarId
        | IRParam (_, i, _) -> i = p.Index
        | _ -> false
    // Closed whitelist: no reads/captures/calls/casts/panic sites, and no
    // resource events. Floating arithmetic here is IEEE-total in both lanes.
    let rec total (ps: IRParam list) e =
        match e with
        | _ when ps |> List.exists (fun p -> paramRef p e) -> true
        | IRLit (IRLitFloat _ | IRLitBool _) -> true
        | IRBinOp (_, (IRAdd | IRSub | IRMul | IRDiv | IREq | IRNeq | IRLt | IRLe | IRGt | IRGe | IRAnd | IROr), a, b, _) ->
            total ps a && total ps b
            && (match typeOf a, typeOf b with
                | IRTScalar ETFloat64, IRTScalar ETFloat64
                | IRTScalar ETBool, IRTScalar ETBool -> true
                | _ -> false)
        | IRUnaryOp ((IRNeg | IRNot), x) -> total ps x
        | IRIf (c, t, f) -> total ps c && total ps t && total ps f
        | _ -> false
    let plainCallable arity (c: IRCallable) =
        c.Params.Length = arity && c.Captures.IsEmpty && not c.IsStatic && not c.IsArityPoly
        && not c.IsOmpParallel && not c.IsCudaKernel && not c.IsMpiParallel
        && (c.Params |> List.forall (fun p -> p.Type = IRTScalar ETFloat64))
        && total c.Params c.Body
    let denseFloat64 = function
        | ArrayElem at when at.ElemType = IRTScalar ETFloat64 && not at.IsVirtual ->
            match at.IndexTypes with
            | [ ix ] -> ix.IxKind = IxKPlain && ix.Rank = 1 && ix.Symmetry = SymNone && ix.Dependencies.IsEmpty
            | _ -> false
        | _ -> false
    let minted = ResizeArray<IRCallable * IRId>()
    let removed = System.Collections.Generic.HashSet<IRId>()
    let replacements = System.Collections.Generic.Dictionary<IRId, IRExpr>()
    let decide name outcome evidence =
        Blade.Effects.Decisions.record
            { Blade.Effects.Rule = "selected-terminal"; Version = 1; Span = declSpanOf name
              Subject = name; Outcome = outcome; Evidence = evidence }
    for KeyValue (yid, (dense, mask)) in modul.CompoundInits do
        match indexOf yid, dense, mask with
        | Some yi, IRVar (xid, xt), IRVar (kid, _) ->
            let y = arr.[yi]
            let candidate =
                match indexOf xid, indexOf kid with
                | Some xi, Some ki when xi < ki && ki < yi ->
                    match arr.[ki].Value with
                    | IRMask (IRVar (src, _), pk) when src = xid -> Some (xi, ki, pk)
                    | _ -> None
                | _ -> None
            match candidate with
            | None -> ()
            | Some (xi, ki, pk) ->
                let x, k = arr.[xi], arr.[ki]
                let terminals =
                    arr |> Array.mapi (fun hi b -> hi, b) |> Array.choose (fun (hi, b) ->
                        match b.Value with
                        | IRReduce (IRVar (id, _), fk, Some seed) when id = yid && hi > yi ->
                            Some (hi, b, [ fk, seed ], false)
                        | IRTuple [ IRReduce (IRVar (a, _), k1, Some s1); IRReduce (IRVar (bId, _), k2, Some s2) ]
                            when a = yid && bId = yid && hi > yi -> Some (hi, b, [ k1, s1; k2, s2 ], true)
                        | _ -> None)
                let decline why = decide y.Name (Blade.Effects.Declined why) []
                if terminals.Length <> 1 then decline "requires exactly one direct seeded scalar terminal or one two-terminal tuple host"
                else
                let hi, host, legs, tupleHost = terminals.[0]
                let privateIds = Set.ofList [ kid; yid ]
                let mentions e = not (Set.intersect privateIds (collectVarRefsIR e)).IsEmpty
                let exprDemand e = mentions e || (embeddedExtentExprs e |> List.exists mentions)
                let typeDemand t = irTypeExtentExprs t |> List.exists exprDemand
                let observer =
                    roots |> List.tryPick (function
                        | RootBindingValue b when b.Id = kid || b.Id = yid -> None
                        | RootBindingValue b when b.Id = host.Id ->
                            // Only the matched fold inputs are subtracted. The
                            // exact tuple grammar has no other value children;
                            // operand types are certified separately below.
                            if legs |> List.exists (fun (fk, seed) -> exprDemand fk || exprDemand seed) then
                                Some "the terminal kernel or seed observes the private arrays" else None
                        | RootBindingValue b when exprDemand b.Value -> Some $"an additional consumer in `{b.Name}`"
                        | RootSideOperand (b, _, _) when b.Id = yid -> None
                        | RootSideOperand (b, what, e) when exprDemand e -> Some $"{what} of `{b.Name}`"
                        | RootFunctionBody f when exprDemand f.Body || (f.Captures |> List.exists (fun c -> privateIds.Contains c.Id)) ->
                            Some $"a callable body or capture in `{f.SourceName}`"
                        | RootType (where, t) when where <> $"`{y.Name}`" && typeDemand t -> Some $"the type of {where}"
                        | RootSourceRef (where, id) when privateIds.Contains id -> Some where
                        | RootNameRef (where, n) when n = k.Name || n = y.Name -> Some where
                        | _ -> None)
                let producerProof = storedArrayProof modul x
                let betweenWrites = [ ki + 1 .. hi ] |> List.exists (fun i -> payloadWrites arr.[i])
                let payloadLifetime b = bindingPayload modul b |> List.exists lifetimeBarrier
                let tupleGeometryOk =
                    if not tupleHost then true else
                    let literalSource =
                        match xt with
                        | ArrayElem at ->
                            match at.IndexTypes with
                            | [ ix ] -> match ix.Extent with IRLit (IRLitInt n) -> n >= 0L | _ -> false
                            | _ -> false
                        | _ -> false
                    let maskType =
                        match xt, k.Type with
                        | ArrayElem a, ArrayElem b -> b.ElemType = IRTScalar ETBool && not b.IsVirtual && a.IndexTypes = b.IndexTypes
                        | _ -> false
                    let selectedType =
                        match y.Type with
                        | ArrayElem at when not at.IsVirtual && at.ElemType = IRTScalar ETFloat64 ->
                            match at.IndexTypes with
                            | [ ix ] -> ix.IxKind = IxKCompound && ix.Rank = 1 && ix.Symmetry = SymNone
                                        && ix.Dependencies.IsEmpty && ix.Extent = IRCompoundMask (IRLit IRLitUnit)
                            | _ -> false
                        | _ -> false
                    let inputs =
                        match host.Value with
                        | IRTuple [ IRReduce (IRVar (_, t1), _, _); IRReduce (IRVar (_, t2), _, _) ] -> t1 = y.Type && t2 = y.Type
                        | _ -> false
                    let producerInputs =
                        match k.Value, mask with
                        | IRMask (IRVar (_, sourceType), _), IRVar (_, maskType) -> sourceType = xt && maskType = k.Type
                        | _ -> false
                    let callableTypes =
                        let plainRef expected = function IRVar (_, ty) -> ty = expected | _ -> false
                        plainRef (mkFuncArrow [ IRTScalar ETFloat64 ] (IRTScalar ETBool)) pk
                        && (legs |> List.forall (fun (fk, _) ->
                            plainRef (mkFuncArrow [ IRTScalar ETFloat64; IRTScalar ETFloat64 ] (IRTScalar ETFloat64)) fk))
                    literalSource && maskType && selectedType && inputs && producerInputs
                    && callableTypes && x.Type = xt && y.Value = IRLit IRLitUnit
                let selectedTypeOk =
                    match y.Type with
                    | ArrayElem at ->
                        at.ElemType = IRTScalar ETFloat64
                        && (match at.IndexTypes with [ ix ] -> ix.IxKind = IxKCompound && ix.Rank = 1 | _ -> false)
                    | _ -> false
                if not enabled then decline "disabled by BLADE_COMPACTION_FUSION (legacy mirrors keep their original storage)"
                elif not (Blade.Toolchain.fpReproEnabled ()) then decline "requires the reproducible serial fold graph (BLADE_FP_REPRO=1)"
                elif selectedNames.IsEmpty || selectedNames.Contains k.Name || selectedNames.Contains y.Name then
                    decline "the mask or selected array is printed (requires an explicit print selection excluding both)"
                elif not (selectedNames |> Set.forall (fun n -> arr |> Array.exists (fun b -> b.Name = n))) then
                    decline "the print selection is not confined to this module's known binding names"
                elif foreignRefs.Contains kid || foreignRefs.Contains yid then decline "the mask or selected array is referenced by another module"
                elif tupleHost && (ki + 1 <> yi || yi + 1 <> hi) then
                    decline "the two-terminal tuple requires adjacent mask, compound and host (failure order)"
                elif tupleHost && not tupleGeometryOk then decline "the two-terminal tuple has unsupported literal-axis or selected operand/type metadata"
                elif not (denseFloat64 xt) || not selectedTypeOk
                     || host.Type <> (if tupleHost then IRTTuple [ IRTScalar ETFloat64; IRTScalar ETFloat64 ] else IRTScalar ETFloat64) then
                    decline "requires a plain stored rank-1 Float64 input and scalar Float64 terminal"
                elif not (bindingSideOperands modul k.Id).IsEmpty || not (bindingSideOperands modul host.Id).IsEmpty
                     || (bindingSideOperands modul y.Id).Length <> 2 then decline "an unsupported side-table payload survives"
                elif tupleHost && ([ kid; yid; host.Id ] |> List.exists (fun id -> modul.ProviderReads.ContainsKey id || modul.ProviderWrites.ContainsKey id)) then
                    decline "a tuple-region provider payload survives"
                elif observer.IsSome then decline observer.Value
                elif betweenWrites then decline "an intervening payload may write (source versions are not proved identical)"
                elif tupleHost && ([ ki .. hi ] |> List.exists (fun i -> payloadLifetime arr.[i])) then
                    decline "a tuple-region payload crosses a pool lifetime event"
                elif legs |> List.exists (fun (_, seed) -> not (total [] seed) || typeOf seed <> IRTScalar ETFloat64) then
                    decline "the explicit seed is not a closed pure/total Float64 scalar"
                else
                let folds = legs |> List.map (fun (fk, _) -> resolveCallableIn funcs fk)
                match producerProof, resolveCallableIn funcs pk, folds with
                | Error why, _, _ -> decline $"the input is not proved stored: {why}"
                | Ok (_, chain), _, _ when chain |> List.exists payloadWrites -> decline "a stored input initializer may write"
                | Ok (_, chain), _, _ when tupleHost && (chain |> List.exists payloadLifetime) -> decline "a stored input initializer crosses a pool lifetime event"
                | Ok _, Some pred, folds when plainCallable 1 pred && pred.RetType = IRTScalar ETBool
                                               && (folds |> List.forall (function
                                                   | Some fold -> plainCallable 2 fold && fold.RetType = IRTScalar ETFloat64
                                                                  && (modul.Functions |> List.exists (fun f -> f.Id = fold.Id))
                                                   | None -> false)) ->
                    let guardedLeg (fold: IRCallable) seed =
                        let ps = fold.Params |> List.map (fun p -> { p with VarId = builder.FreshId () })
                        let substitute (old: IRParam list) (values: IRExpr list) body =
                            mapIRExpr (fun n ->
                                List.zip old values |> List.tryPick (fun (p, v) -> if paramRef p n then Some v else None)
                                |> Option.defaultValue n) body
                        let acc, cell = IRVar (ps.[0].VarId, ps.[0].Type), IRVar (ps.[1].VarId, ps.[1].Type)
                        let body = IRIf (substitute pred.Params [ cell ] pred.Body,
                                         substitute fold.Params [ acc; cell ] fold.Body, acc)
                        let guarded = mkCallable builder defaultLambdaOptions ps body fold.RetType [] false [] [] false false 256 false
                        let guarded = { guarded with Name = $"__selected_terminal_{guarded.Id}"; SourceName = fold.SourceName }
                        minted.Add (guarded, fold.Id)
                        // Rebuild the plain function arrow too: all scheduling
                        // metadata comes from the NEW, unlicensed callable.
                        let guardedType = mkFuncArrow (ps |> List.map _.Type) guarded.RetType
                        IRReduce (dense, IRVar (guarded.Id, guardedType), Some seed)
                    let rewritten = List.map2 (fun fold (_, seed) -> guardedLeg (Option.get fold) seed) folds legs
                    replacements.[host.Id] <- if tupleHost then IRTuple rewritten else rewritten.Head
                    removed.Add kid |> ignore
                    removed.Add yid |> ignore
                    decide y.Name Blade.Effects.Applied
                        [ (if tupleHost then
                               $"two guarded stored-input folds at tuple `{host.Name}`; `{k.Name}` Bool mask and `{y.Name}` selected value/index stores eliminated; positions may already be elided"
                           else $"one stored-input scan at `{host.Name}`; `{k.Name}` Bool mask, `{y.Name}` positions/index/value stores eliminated")
                          "complete binding/type/capture/side-table/foreign/print census; no intervening writes"
                          (if tupleHost then "storage elision only at this stage; sibling traversal sharing is separately gated; predicate remains in each fold"
                           else "one guarded scalar terminal")
                          "same source for mask and selection (shape checks redundant); pure/total predicate and fold; seed once at the original terminal"
                          "ascending serial graph; false predicate returns the accumulator without a fold step; empty selection returns the exact seed" ]
                | _ -> decline "the predicate or fold is not a supported capture-free pure/total scalar callable"
        | _ -> ()
    if removed.Count = 0 then modul
    else
        let byOrigin = minted |> Seq.groupBy snd |> Map.ofSeq
        { modul with
            Bindings = modul.Bindings |> List.filter (fun b -> not (removed.Contains b.Id))
                        |> List.map (fun b -> match replacements.TryGetValue b.Id with true, v -> { b with Value = v } | _ -> b)
            CompoundInits = modul.CompoundInits |> Map.filter (fun id _ -> not (removed.Contains id))
            Functions = modul.Functions |> List.collect (fun f ->
                f :: (Map.tryFind f.Id byOrigin |> Option.map (Seq.map fst >> List.ofSeq) |> Option.defaultValue []))
            DerivedFuncOrigins = minted |> Seq.fold (fun m (f, origin) -> Map.add f.Id origin m) modul.DerivedFuncOrigins }

/// Plan compaction fusion over the module's top-level bindings (`compound`
/// is top-level only, BL6002). Records into `CompactionTable` and the
/// decision collector (`compaction-fusion`); the IR is untouched.
let planCompactionFusion (programFuncs: IRCallable list) (foreignRefs: Set<IRId>) (modul: IRModule) : unit =
    let funcs = modul.Functions |> List.map (fun f -> (f.Id, f)) |> Map.ofList
    let facts = cseFactsOf funcs
    let writes = scheduleMayWriteIn programFuncs modul
    let lifetimeBarrier = scheduleHasLifetimeBarrierIn programFuncs modul
    let mode = compactionFusionMode ()
    let enabled = mode <> CompactionFusionOff
    let v2 = mode = CompactionFusionT4 || mode = CompactionFusionGather || mode = CompactionFusionUnshared
    // T4c (the shared selection and the membership-probe certificate): the
    // default and `gather`; `unshared` is exactly the pre-T4c v2.
    let sharedOn = mode = CompactionFusionT4 || mode = CompactionFusionGather
    let gatherOn = mode = CompactionFusionGather
    let decide (subject: string) (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) =
        Blade.Effects.Decisions.record
            { Blade.Effects.Rule = "compaction-fusion"; Version = (if v2 then 2 else 1)
              Span = declSpanOf subject; Subject = subject
              Outcome = outcome; Evidence = evidence }
    let arr = List.toArray modul.Bindings
    let indexOf (id: IRId) = arr |> Array.tryFindIndex (fun b -> b.Id = id)
    let refs (e: IRExpr) = collectVarRefsIR e
    // `id` is the target of an assignment somewhere in `e` (`y = ..`, `y(k) = ..`).
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
    // Each binding's ACTUAL execution payload (its value plus the side-table
    // operands its site runs -- the value alone is a placeholder for a
    // compound / sparse / random binding), through the shared write analysis
    // and the conservative facts check.
    let payloadWrites (b: IRBinding) =
        bindingPayload modul b |> List.exists (fun e -> (facts e).MayWrite || writes e)
    let payloadLifetime (b: IRBinding) = bindingPayload modul b |> List.exists lifetimeBarrier
    let payloadRefs (b: IRBinding) = bindingPayload modul b |> List.map refs |> Set.unionMany
    // Does `b`'s payload READ mask `kId`? A sibling `compound(d, kId)` names
    // the mask only as its own mask operand -- it reads the bits and writes
    // nothing through them -- so that one occurrence is not a read here.
    let readsMask (kId: IRId) (b: IRBinding) =
        let ops =
            match Map.tryFind b.Id modul.CompoundInits with
            | Some (d, IRVar (m, _)) when m = kId -> b.Value :: d :: (bindingSideOperands modul b.Id |> List.skip 2)
            | _ -> bindingPayload modul b
        ops |> List.exists (fun e -> Set.contains kId (refs e))
    // Every id a payload reaches: its references, and transitively the bodies
    // and captures of every callable it names (and of let aliases of them) --
    // what a payload could name, and therefore write through a `mut`
    // parameter, without naming it at its own site.
    let callableById =
        (modul.Functions @ programFuncs) |> List.map (fun f -> (f.Id, f)) |> Map.ofList
    let letValueById = arr |> Array.map (fun b -> (b.Id, b.Value)) |> Map.ofArray
    let transitiveRefs (b: IRBinding) : Set<IRId> =
        let seen = System.Collections.Generic.HashSet<IRId>()
        let work = System.Collections.Generic.Stack<IRId>(payloadRefs b)
        while work.Count > 0 do
            let id = work.Pop()
            if seen.Add id then
                match Map.tryFind id callableById with
                | Some fn ->
                    for r in refs fn.Body do work.Push r
                    for c in fn.Captures do work.Push c.Id
                | None ->
                    match Map.tryFind id letValueById with
                    | Some (IRVar (v, _)) -> work.Push v
                    | _ -> ()
        Set.ofSeq seen
    let isParamRef (p: IRParam) (e: IRExpr) =
        match e with
        | IRVar (v, _) -> v = p.VarId
        | IRParam (_, i, _) -> i = p.Index
        | _ -> false
    let isFloatTy (t: IRType) =
        match stripUnits t with
        | IRTScalar (ETFloat64 | ETFloat32) -> true
        | _ -> false
    // A PURE, TOTAL scalar body over its one parameter: literals, the
    // parameter, arithmetic that cannot trap (no integer `/` `%` `^`, no
    // float->int cast), comparisons, logic, `if`. Anything else -- a call, a
    // capture, an intrinsic that may be domain-checked -- is not judged.
    let rec totalScalar (p: IRParam) (e: IRExpr) =
        match e with
        | _ when isParamRef p e -> true
        | IRLit _ -> true
        | IRBinOp (_, op, l, r, _) ->
            let opOk =
                match op with
                | IRAdd | IRSub | IRMul | IREq | IRNeq | IRLt | IRLe | IRGt | IRGe | IRAnd | IROr -> true
                | IRDiv -> isFloatTy (typeOf l) && isFloatTy (typeOf r)
                | _ -> false
            opOk && totalScalar p l && totalScalar p r
        | IRUnaryOp ((IRNeg | IRNot), x) -> totalScalar p x
        | IRIf (c, t, f) -> totalScalar p c && totalScalar p t && totalScalar p f
        | _ -> false
    let squareMap (info: ApplyInfo) (yId: IRId) : bool =
        let overY = info.Arrays |> List.forall (function IRVar (a, _) -> a = yId | _ -> false)
        let shapeOk =
            not info.Arrays.IsEmpty && overY && not info.HasReynolds && info.KernelOutputRank = 0
            && (info.Arrays.Length = 1 || info.IsCoIteration)
        shapeOk
        && (match resolveCallableIn funcs (fst (peelReynolds info.Kernel)) with
            | Some k when k.Captures.IsEmpty && not k.IsCudaKernel && not k.IsMpiParallel ->
                (match k.Params, k.Body with
                 | [ p ], IRBinOp (IRElementwise, IRMul, a, b, _) -> isParamRef p a && isParamRef p b
                 | [ p0; p1 ], IRBinOp (IRElementwise, IRMul, a, b, _) when info.Arrays.Length = 2 ->
                    (isParamRef p0 a || isParamRef p1 a) && (isParamRef p0 b || isParamRef p1 b)
                 | _ -> false)
            | _ -> false)
    let builtinAdd (k: IRCallable) =
        match k.Params, k.Body with
        | [ p0; p1 ], IRBinOp (_, IRAdd, l, r, _) ->
            (isParamRef p0 l && isParamRef p1 r) || (isParamRef p1 l && isParamRef p0 r)
        | _ -> false
    // The lifted `let m = <map over y>` whose ONE reader is `reduce(m, k, init)`
    // inside a host binding's value.
    let foldSites (yId: IRId) (hostValue: IRExpr) : (IRId * ApplyInfo * IRExpr * IRExpr option) list =
        let found = ResizeArray()
        iterIRExpr (fun n ->
            match n with
            | IRLet (mid, IRCompute (IRApplyCombinator info), body)
                when info.Arrays |> List.exists (function IRVar (a, _) -> a = yId | _ -> false) ->
                let mutable count = 0
                let mutable site = None
                iterIRExpr (fun m ->
                    match m with
                    | IRVar (v, _) when v = mid -> count <- count + 1
                    | IRReduce (IRVar (v, _), k, init) when v = mid -> site <- Some (k, init)
                    | _ -> ()) body
                (match site with
                 | Some (k, init) when count = 1 -> found.Add((mid, info, k, init))
                 | _ -> ())
            | _ -> ()) hostValue
        List.ofSeq found
    // ---- T4b: the positions DEMAND CERTIFICATE ---------------------------
    // Fail-safe by construction: `Ok uses` only when EVERY occurrence of `y`
    // -- and of every elementwise map derived from it, which shares its index
    // (the C++ map output `Compound { new T[card], y.idx, .. }`, the LLVM
    // output's GCompact shape) -- is one this census recognizes as reading
    // the compact buffer or the cardinality alone: a fold, `extents`, a
    // derived map (whose own uses are then certified the same way), or
    // printing (both lanes print `size()` cells of `.data`). The first other
    // occurrence -- a keyed read, an alias, an argument, a tuple, an
    // assignment, a capture, a provider write, an operand of another side
    // table, a type extent other than `extents(y)`, or anything the census
    // does not know -- is `Error <what demands the table>`. Every module
    // binding, every function body and signature (this module's and the
    // program's), every capture list and every side table is scanned.
    let peelMap (e: IRExpr) : ApplyInfo option =
        match e with
        | IRApplyCombinator info | IRCompute (IRApplyCombinator info) -> Some info
        | _ -> None
    let allCallables =
        (modul.Functions @ programFuncs) |> List.distinctBy (fun f -> f.Id)
    let positionsDemand (yId: IRId) (yName: string) : Result<string list, string> =
        let tracked = System.Collections.Generic.Dictionary<IRId, string>()
        tracked.[yId] <- yName
        let uses = ResizeArray<string>()
        let note (u: string) = if not (uses.Contains u) then uses.Add u
        let mutable demand : string option = None
        let req (why: string) = if demand.IsNone then demand <- Some why
        let isT (e: IRExpr) = match e with IRVar (v, _) -> tracked.ContainsKey v | _ -> false
        let nameOf (e: IRExpr) = match e with IRVar (v, _) when tracked.ContainsKey v -> tracked.[v] | _ -> yName
        // A CELLWISE map: every kernel input is one scalar cell (input rank
        // 0, known), the output is one scalar per cell (output rank 0) and
        // keeps the compound axis -- a kernel over the whole array (`c(0)` in
        // a whole-array parameter) or of unknown input rank is no derived map.
        let derivedMap (info: ApplyInfo) =
            not info.Arrays.IsEmpty
            && info.Arrays |> List.forall (fun a -> isT a || (match a with IRZip es -> not es.IsEmpty && List.forall isT es | _ -> false))
            && not info.HasReynolds && info.KernelOutputRank = 0
            && not info.KernelInputRanks.IsEmpty && info.KernelInputRanks |> List.forall ((=) 0)
            && (match stripUnits info.OutputType with
                | ArrayElem o -> (match o.IndexTypes with [ ix ] -> ix.IxKind = IxKCompound && ix.Rank = 1 | _ -> false)
                | _ -> false)
            && (info.Arrays.Length = 1 || info.IsCoIteration)
        let rec scan (where: string) (e: IRExpr) =
            match e with
            | IRExtent (a, _) when isT a -> note "extents (the cardinality)"
            | IRReduce (a, k, init) when isT a ->
                note "folds over the compact buffer"
                scan where k
                init |> Option.iter (scan where)
            | IRLet (mid, v, body) when (match peelMap v with Some info -> derivedMap info | None -> false) ->
                let info = (peelMap v).Value
                tracked.[mid] <- $"the map over `{nameOf (List.head info.Arrays)}` in {where}"
                note "elementwise maps over it (sharing its index)"
                scanMapRest where v
                scan where body
            | IRIndex (a, _, _) when isT a -> req $"a keyed read of `{nameOf a}` in {where}"
            | IRApp (_, args, _) when List.exists isT args ->
                req $"`{nameOf (List.find isT args)}` passed as a call argument in {where}"
            | IRTuple es when List.exists isT es -> req $"`{nameOf (List.find isT es)}` stored in a tuple in {where}"
            | IRAssign (t, _) when (refs t) |> Set.exists tracked.ContainsKey -> req $"an assignment to `{yName}` (or a map of it) in {where}"
            | IRLet (_, v, _) when isT v -> req $"an alias of `{nameOf v}` in {where}"
            | IRVar (v, _) when tracked.ContainsKey v -> req $"a use of `{tracked.[v]}` the census does not certify, in {where}"
            | _ ->
                // an operand position the census does not certify names its node
                match childrenOf e |> List.tryFind isT with
                | Some a ->
                    let node = (Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(e, typeof<IRExpr>) |> fst).Name
                    req $"`{nameOf a}` as an operand of {node} (a use the census does not certify) in {where}"
                | None -> childrenOf e |> List.iter (scan where)
        // A derived map's own operands are the tracked arrays (positions-free
        // by the map's emission); everything else in it is scanned.
        // (The apply's Loop provenance repeats the operands -- `IRMethodFor`
        // over them, or over their `zip` -- and is skipped when it does.)
        and scanMapRest (where: string) (v: IRExpr) =
            match peelMap v with
            | Some info ->
                let operandOnly (a: IRExpr) = isT a || (match a with IRZip es -> List.forall isT es | _ -> false)
                (match info.Loop with
                 | IRMethodFor mf when List.forall operandOnly mf.Arrays -> ()
                 | IRObjectFor ofi -> scan where ofi.Kernel
                 | loop -> scan where loop)
                scan where info.Kernel
            | None -> scan where v
        let scanType (where: string) (t: IRType) =
            for e in (try irTypeExtentExprs t with _ -> [ IRVar (yId, IRTUnit) ]) do scan $"the type of {where}" e
        // Every type and index expression an executable root carries (the
        // generic child walk does not enter types).
        let scanEmbedded (where: string) (e: IRExpr) =
            for x in (try embeddedExtentExprs e with _ -> [ IRVar (yId, IRTUnit) ]) do scan $"a type inside {where}" x
        // Module-level derived maps, in declaration order (a map reads only
        // earlier bindings).
        for b in arr do
            if b.Id <> yId then
                match peelMap b.Value with
                | Some info when derivedMap info ->
                    tracked.[b.Id] <- b.Name
                    note $"the named map `{b.Name}` (sharing its index)"
                | _ -> ()
        // THE MODULE TRAVERSAL (`compactionRoots`, shared with the foreign-
        // demand collection Lowering hands in as `foreignRefs`).
        for root in compactionRoots allCallables modul do
            match root with
            | RootBindingValue b when b.Id = yId -> ()
            | RootBindingValue b ->
                let where = $"`{b.Name}`"
                (match b.Value with
                 | IRVar (v, _) when tracked.ContainsKey v -> req $"the alias `{b.Name}` of `{tracked.[v]}`"
                 | _ -> if tracked.ContainsKey b.Id then scanMapRest where b.Value else scan where b.Value)
                scanEmbedded where b.Value
            | RootSideOperand (b, _, _) when b.Id = yId -> ()
            | RootSideOperand (b, what, e) ->
                let where = $"{what} of `{b.Name}`"
                scan where e
                scanEmbedded where e
            | RootFunctionBody fn ->
                let where = $"`{fn.SourceName}`"
                (match fn.Captures |> List.tryFind (fun c -> tracked.ContainsKey c.Id) with
                 | Some c -> req $"`{tracked.[c.Id]}` captured by {where}"
                 | None -> ())
                scan where fn.Body
                scanEmbedded where fn.Body
            | RootType (where, ty) -> scanType where ty
            | RootSourceRef (where, id) ->
                if tracked.ContainsKey id then req $"{where} of `{tracked.[id]}`"
            | RootNameRef (where, nm) ->
                if tracked.Values |> Seq.contains nm then req $"{where} `{nm}`"
        // Another module naming it (an import): the census does not see
        // that module's uses, so any reference at all demands the table.
        for KeyValue (id, nm) in tracked do
            if foreignRefs.Contains id then req $"`{nm}` named in another module"
        match demand with
        | Some why -> Error why
        | None -> Ok (List.ofSeq uses)
    // The element's bytes per cell (both lanes' pool widths), for the cap.
    let elemBytes (t: IRType) : int64 option =
        match stripUnits t with
        | IRTScalar (ETFloat64 | ETInt64 | ETComplex64) -> Some 8L
        | IRTScalar (ETFloat32 | ETInt32) -> Some 4L
        | IRTScalar ETBool -> Some 1L
        | IRTScalar ETComplex128 -> Some 16L
        | _ -> None
    let staticExtentOf (t: IRType) : int64 option =
        match stripUnits t with
        | ArrayElem a ->
            (match a.IndexTypes with
             | [ ix ] -> (match ix.Extent with IRLit (IRLitInt n) when n >= 0L -> Some n | _ -> None)
             | _ -> None)
        | _ -> None
    let fmtBytes (b: int64) =
        if b >= 1048576L then $"{float b / 1048576.0:F1} MiB" elif b >= 1024L then $"{float b / 1024.0:F1} KiB" else $"{b} B"
    // ---- Part B: the MEMBERSHIP-PROBE CERTIFICATE ------------------------
    // `totalScalar` (above) judges literal arithmetic, comparisons and logic
    // over the one parameter, and rejects every call, capture and `contains`
    // -- it is NOT widened here. A predicate built from `contains(B, t)`
    // probes (`contains(B, v)`, `!contains(B, v)`, `contains(B, v) && p(v)`,
    // under `&&` / `||` / `!` / `if`) is certified pure and total SEPARATELY,
    // and only when membership hoisting ADMITTED this host's probe of B: the
    // T1 planner's own plan (`MemberHoistTable`, its five-party admission
    // record quoted as the proof) is the input -- the set is a stored,
    // never-assigned, rank-1 Int64 binding bound before the host whose
    // producer and consumer write nothing -- never a re-derivation. What
    // the certificate adds is the totality of everything else: every target
    // and every other operand passes `totalScalar` (no panic site), and the
    // lambda captures nothing but the certified set(s). A probe T1 declined
    // (or BLADE_MEMBER_HOIST=0) stays the per-probe scan and the predicate is
    // not certified. The snapshot / effect proof is unchanged: the predicate
    // party is still `scheduleMayWriteIn` over its body.
    let probeCertificate (kb: IRBinding) (pc: IRCallable) (p: IRParam) : Result<string, string> =
        let plans = MemberHoistTable.plansOf kb.Id |> List.filter (fun hp -> hp.Kernels.Contains pc.Id)
        let planOf (sid: IRId) = plans |> List.tryFind (fun hp -> hp.SetId = sid)
        let used = ResizeArray<MemberHoistPlan>()
        let mutable why : string option = None
        let fail (s: string) = (if why.IsNone then why <- Some s); false
        let rec ok (e: IRExpr) =
            match e with
            | IRContains (IRVar (sid, _), t) ->
                (match planOf sid with
                 | None ->
                    let setName = match indexOf sid with Some si -> arr.[si].Name | None -> $"v{sid}"
                    fail $"member-hoist did not admit `{kb.Name}`'s probe of `{setName}` (its `[member-hoist v1] {kb.Name}` record says why, or BLADE_MEMBER_HOIST=0): the probe stays the per-probe scan"
                 | Some hp ->
                    if not (totalScalar p t) then fail $"the target of `{kb.Name}`'s probe of `{hp.SetName}` is not a pure, total scalar of the cell (a panic site, a call or a capture)"
                    else
                        (if not (used |> Seq.exists (fun u -> u.SetId = hp.SetId)) then used.Add hp)
                        true)
            | IRContains _ -> fail $"`{kb.Name}`'s predicate probes a set expression, not a named stored set"
            | IRBinOp (_, (IRAnd | IROr), l, r, _) -> ok l && ok r
            | IRUnaryOp (IRNot, x) -> ok x
            | IRIf (c, t, f) -> ok c && ok t && ok f
            | _ -> totalScalar p e || fail $"`{kb.Name}`'s predicate holds an operand that is not a pure, total scalar of the cell"
        let bodyOk = ok pc.Body
        let certifiedIds = used |> Seq.map (fun hp -> hp.SetId) |> Set.ofSeq
        let strayCapture = pc.Captures |> List.tryFind (fun c -> not (certifiedIds.Contains c.Id))
        if not bodyOk then Error (defaultArg why "the predicate is not certified")
        elif used.Count = 0 then Error "the predicate holds no certified `contains` probe"
        else
        match strayCapture with
        | Some c -> Error $"`{kb.Name}`'s predicate captures `{c.Name}`, which is not a certified set"
        | None ->
            let sets = used |> Seq.map (fun hp -> $"`{hp.SetName}`") |> String.concat ", "
            let proofs = used |> Seq.map (fun hp -> $"[{hp.SetName}: {hp.Admission}]") |> String.concat " "
            Ok $"membership probe certified pure/total by member-hoist's admission: `{kb.Name}`'s predicate probes {sets} through the structure(s) membership hoisting built before `{kb.Name}` (proof input, the T1 planner's own record: {proofs}); every target and every other operand is a pure, total scalar of the cell (no panic site); no capture but the certified set(s); `totalScalar` itself is not widened"
    // The mask predicate's admission: `totalScalar` (B4's), or -- in the T4c
    // modes only (the default and `gather`), so `b4` stays exactly the
    // round-2 form and `unshared` the pre-T4c v2 -- the certificate above.
    let predicateAdmission (kb: IRBinding) : Result<string, string> =
        match kb.Value with
        | IRMask (_, pred) ->
            (match resolveCallableIn funcs pred with
             | Some pc ->
                (match pc.Params with
                 | [ p ] when pc.Captures.IsEmpty && totalScalar p pc.Body ->
                    Ok $"predicate of `{kb.Name}`: a pure, total scalar function of the cell (totalScalar)"
                 | [ p ] when sharedOn ->
                    (match probeCertificate kb pc p with
                     | Ok line -> Ok line
                     | Error why -> Error $"the mask predicate of `{kb.Name}` is not a pure, total scalar function of the cell (v1 judges literal arithmetic, comparisons and logic over the one parameter), and the membership probe is not certified: {why}")
                 | _ -> Error $"the mask predicate of `{kb.Name}` is not a pure, total scalar function of the cell (v1 judges literal arithmetic, comparisons and logic over the one parameter)")
             | None -> Error $"the mask predicate of `{kb.Name}` does not resolve to a callable")
        | _ -> Error "the compound's mask is not a let-bound `mask(x, pred)`"
    // ---- T4c: ONE selection shared by every compound over the same mask ----
    // `keep = mask(x, pred)` and its J companions `y_j = compound(c_j, keep)`
    // (each c_j over x's index space): ONE selection index -- counted in the
    // mask pass, its kept positions (when any companion demands them) filled
    // by the one joint pass -- owned by the plan and shared by every
    // companion's wrapper; the J gathers are ONE branchless pass at the
    // first companion (`b = keep[i]; y1[q] = c1[i]; ..; yJ[q] = cJ[i]; [pos[q]
    // = i;] q += b`), or, under `gather` with J <= 4 and every c_j bound
    // before the mask, T4a's mask pass. Stable, ascending, serial: the same
    // values, cardinality and positions each compound's own passes build.
    // Each companion's data store stays its own named array. Not a T4c case
    // (the round-2 / T4 plan decides): a single companion over x itself, the
    // `unshared` / `b4` mirrors, the gate off.
    let compoundsOfMask : Map<IRId, (IRId * int) list> =
        modul.CompoundInits
        |> Seq.choose (fun (KeyValue (cid, (_, m))) ->
            match m, indexOf cid with
            | IRVar (k, _), Some ci -> Some (k, (cid, ci))
            | _ -> None)
        |> Seq.groupBy fst
        |> Seq.map (fun (k, xs) -> (k, xs |> Seq.map snd |> Seq.sortBy snd |> List.ofSeq))
        |> Map.ofSeq
    let rank1Compound (b: IRBinding) =
        match stripUnits b.Type with
        | ArrayElem at ->
            (match at.IndexTypes with
             | [ ix ] -> ix.IxKind = IxKCompound && ix.Rank = 1
             | _ -> false)
        | _ -> false
    let plainAxisOf (t: IRType) : IRIndexType option =
        match stripUnits t with
        | ArrayElem a when not a.IsVirtual ->
            (match a.IndexTypes with
             | [ ix ] when ix.IxKind = IxKPlain && ix.Rank = 1 && ix.Symmetry = SymNone -> Some ix
             | _ -> None)
        | _ -> None
    let elemOf (t: IRType) = match stripUnits t with ArrayElem a -> a.ElemType | other -> other
    // Per mask: None = not a T4c case; Some (Ok (selection, evidence)) or
    // Some (Error why) -- the reason every companion's record carries.
    let groupDecision (kId: IRId) (ms: (IRId * int) list) : Result<SharedSelection * string list, string> option =
        match indexOf kId with
        | None -> None
        | Some ki ->
        let kb = arr.[ki]
        match kb.Value with
        | IRMask (IRVar (srcId, _), _) when enabled && sharedOn && not ms.IsEmpty ->
            let dense (cid: IRId) = fst modul.CompoundInits.[cid]
            let single =
                match ms with
                | [ (cid, _) ] -> (match dense cid with IRVar (d, _) -> d = srcId | _ -> false)
                | _ -> false
            if single then None
            else
            Some (
                let (firstId, firstI) = List.head ms
                let (lastId, lastI) = List.last ms
                let firstName = arr.[firstI].Name
                let lastName = arr.[lastI].Name
                let memberIds = ms |> List.map fst |> Set.ofList
                let xName = match indexOf srcId with Some xi -> arr.[xi].Name | None -> "x"
                let srcAxis = match indexOf srcId with Some xi -> plainAxisOf arr.[xi].Type | None -> None
                // the companions' sources, each checked; first failure wins
                // THE POSITIVE STORAGE PROOF (`storedArrayProof`, membership
                // hoisting's, through alias chains: a virtual array, a streamed
                // read, a deferred root and every unproved form decline) of the
                // mask source and of EVERY companion source -- the joint pass
                // reads each source's pool by name -- with the assignment check
                // over every binding of each alias chain.
                let storedProof = storedArrayProof modul
                let proveSource (b: IRBinding) : Result<string * IRBinding list, string> =
                    match storedProof b with
                    | Error why -> Error why
                    | Ok (how, chain) ->
                        match chain |> List.tryFind (fun c -> assignedAnywhere c.Id) with
                        | Some c -> Error (sprintf "`%s` (its alias chain) is assigned in the program" c.Name)
                        | None -> Ok (how, chain)
                let memberWhy =
                    ms |> List.tryPick (fun (cid, ci) ->
                        let yb = arr.[ci]
                        if not (rank1Compound yb) then Some $"`{yb.Name}` over `{kb.Name}` has trailing dims or a rank-k index (T4c shares a rank-1 selection only)"
                        elif assignedAnywhere cid then Some $"the companion `{yb.Name}` is written (assigned) in the program"
                        else
                        match dense cid with
                        | IRVar (cj, _) ->
                            (match indexOf cj with
                             | None -> Some $"the dense operand of `{yb.Name}` is not a top-level binding the write analysis can see"
                             | Some cjI ->
                                let cb = arr.[cjI]
                                if cjI >= firstI then Some $"the source `{cb.Name}` of `{yb.Name}` is bound after the first companion `{firstName}`, where the one joint pass runs (v1)"
                                elif (match proveSource cb with Error _ -> true | Ok _ -> false) then
                                    (match proveSource cb with Error why -> Some $"the source `{cb.Name}` of `{yb.Name}` is not proved stored: {why} (the joint pass reads every source's pool by name)" | Ok _ -> None)
                                elif (elemBytes (elemOf yb.Type)).IsNone then Some $"`{yb.Name}`'s element has no fixed cell width"
                                else
                                match plainAxisOf cb.Type, srcAxis with
                                | Some a, Some s when a.Extent = s.Extent -> None
                                | _ -> Some $"the source `{cb.Name}` of `{yb.Name}` is not provably over `{xName}`'s index space (BL3016 territory: never admitted)")
                        | _ -> Some $"the dense operand of `{yb.Name}` is computed in place (T4c gathers stored companions)")
                let sources = ms |> List.choose (fun (cid, _) -> match dense cid with IRVar (cj, _) -> Some cj | _ -> None) |> List.distinct
                let sourceNames = sources |> List.map (fun s -> match indexOf s with Some i -> $"`{arr.[i].Name}`" | None -> "?") |> String.concat ", "
                let names = ms |> List.map (fun (_, ci) -> $"`{arr.[ci].Name}`") |> String.concat ", "
                let between = [ ki + 1 .. lastI ] |> List.filter (fun i -> not (memberIds.Contains arr.[i].Id)) |> List.map (fun i -> arr.[i])
                if ki >= firstI then Error $"`{kb.Name}` is not bound before its companions"
                elif assignedAnywhere kId then Error $"`{kb.Name}` is assigned"
                else
                match memberWhy with
                | Some why -> Error why
                | None ->
                let maskSourceWhy =
                    match indexOf srcId with
                    | None -> Some $"the mask source of `{kb.Name}` is not a top-level binding the write analysis can see"
                    | Some xi ->
                        match proveSource arr.[xi] with
                        | Error why -> Some $"the mask source `{xName}` is not proved stored: {why}"
                        | Ok _ -> None
                match maskSourceWhy with
                | Some why -> Error why
                | None ->
                match predicateAdmission kb with
                | Error why -> Error why
                | Ok predLine ->
                // ---- selectionAdmits, the five parties of T4c's motion ----
                let pc = match kb.Value with IRMask (_, pred) -> resolveCallableIn funcs pred | _ -> None
                let wrote (e: IRExpr) = (facts e).MayWrite || writes e
                let producerParty =
                    // every binding of every source's alias chain, the mask
                    // source's included (x is a producer even when it is no
                    // companion's source)
                    let chains =
                        (srcId :: sources) |> List.distinct |> List.collect (fun s ->
                            match indexOf s with
                            | Some i -> (match proveSource arr.[i] with Ok (_, ch) -> ch | Error _ -> [ arr.[i] ])
                            | None -> [])
                        |> List.distinctBy (fun b -> b.Id)
                    match chains |> List.tryFind payloadWrites with
                    | Some b -> Error $"the producer `{b.Name}` may write (its initializer or side-table payload, through the shared write analysis)"
                    | None -> Ok $"producers {sourceNames} and the mask source `{xName}`: proved stored through their alias chains (storedArrayProof), never assigned, no write in any payload"
                let predicateParty =
                    match pc with
                    | Some c when wrote c.Body -> Error $"the predicate of `{kb.Name}` may write"
                    | Some c when c.IsOmpParallel || c.IsCudaKernel || c.IsMpiParallel -> Error $"the predicate of `{kb.Name}` requests a thread team or a device"
                    | Some _ -> Ok $"predicate of `{kb.Name}`: no write (its site is unchanged)"
                    | None -> Error $"the predicate of `{kb.Name}` does not resolve to a callable"
                // The outer fold kernels and seeds of every fold over a
                // companion: only an unseeded builtin-`+` self-product fold
                // (B4a) is ever fed in the joint pass (its serial chain,
                // under the repro form); every other fold, and every seed,
                // stays at its own site.
                let foldSitesAll =
                    [ for (cid, ci) in ms do
                        for hi in ci + 1 .. arr.Length - 1 do
                            for (_, _, k, init) in foldSites cid arr.[hi].Value do
                                yield (arr.[hi], k, init) ]
                let foldParty =
                    let writing =
                        foldSitesAll |> List.tryFind (fun (_, k, _) ->
                            match resolveCallableIn funcs (fst (peelReynolds k)) with
                            | Some kc -> wrote kc.Body
                            | None -> true)
                    match writing with
                    | Some (hb, _, _) -> Ok $"outer fold kernel(s): {foldSitesAll.Length} over the companions; `{hb.Name}`'s kernel may write (or does not resolve), so it is never fed and stays at its own site"
                    | None ->
                        if foldSitesAll.IsEmpty then Ok "outer fold kernel(s): none over the companions"
                        else Ok $"outer fold kernel(s): {foldSitesAll.Length} over the companions, none writes; only an unseeded builtin `+` self-product fold may have its serial chain fed in the joint pass"
                let seedParty =
                    let seeded = foldSitesAll |> List.filter (fun (_, _, s) -> s.IsSome)
                    match seeded |> List.tryFind (fun (_, _, s) -> match s with Some e -> wrote e | None -> false) with
                    | Some (hb, _, _) -> Ok $"seed(s): `{hb.Name}`'s seed may write -- it is never moved (seeded folds stay at their own sites)"
                    | None -> Ok $"seed(s): {seeded.Length} seeded fold(s) over the companions, none moved; every fed chain is unseeded and keeps its BL8003 check at its own site"
                // every binding in (mask, last companion] but the companions:
                // its WHOLE interval payload (`intervalPayloadOf`: the value,
                // its side-table operands, a provider write's source) through
                // the shared write analysis
                let payloadOf = intervalPayloadOf modul
                let betweenParty =
                    let offends (b: IRBinding) =
                        if payloadLifetime b then Some "contains an inline lifetime boundary"
                        elif wrote (payloadOf b.Id b.Value) || payloadWrites b then Some "may write (its value or side-table payload)"
                        elif intervalHasProviderEffect modul b.Id then Some "is a provider write (an external effect)"
                        elif readsMask kId b then Some $"reads `{kb.Name}` between the mask and the last companion"
                        else None
                    match between |> List.tryPick (fun b -> offends b |> Option.map (fun why -> (b, why))) with
                    | Some (b, why) -> Error $"`{b.Name}` (between `{kb.Name}` and `{lastName}`) {why}"
                    | None ->
                        if between.IsEmpty then Ok $"between `{kb.Name}` and `{lastName}`: no binding but the companions"
                        else Ok ($"between `{kb.Name}` and `{lastName}`: " + (between |> List.map (fun b -> $"`{b.Name}`") |> String.concat ", ") + $": no write in any payload, no provider write, no read of `{kb.Name}` (a read of an earlier companion is fine: its data is its own and the index is complete)")
                let parallelParty = Ok "parallel: none -- the mask pass, the joint pass and every fed chain are serial (a predicate requesting a team or a device declines above; a `where omp` fold is never planned, B4a), so no parallel body runs moved work and callbackTable has no candidate set to audit"
                let parties = [ producerParty; predicateParty; foldParty; seedParty; betweenParty; parallelParty ]
                let matrix =
                    "selectionAdmits (five parties, each through Optimize.scheduleMayWriteIn): "
                    + (parties |> List.map (function Ok s -> s | Error s -> "DECLINES: " + s) |> String.concat "; ")
                match parties |> List.tryPick (function Error s -> Some s | Ok _ -> None) with
                | Some why -> Error $"{why} -- {matrix}"
                | None ->
                // ---- T4b per companion, united ----
                let census = ms |> List.map (fun (cid, ci) -> (arr.[ci].Name, positionsDemand cid arr.[ci].Name))
                let demanded = census |> List.choose (fun (n, r) -> match r with Error why -> Some (n, why) | Ok _ -> None)
                let positionsNeeded = not demanded.IsEmpty
                let selection =
                    match demanded with
                    | [] -> $"selection: positions elided (every companion's census: folds, `extents`, derived maps or printing only) -- the shared index keeps dense extent n and cardinality m; a lookup through the absent table is an internal error (BL9002), never a read"
                    | (n, why) :: _ -> $"selection: positions required by {why} (`{n}`) -- the SHARED index holds the ascending kept positions for every companion (keyed reads, BL8006 unchanged, each at its own site)"
                // ---- T4a for the group ----
                let perCell =
                    ms |> List.fold (fun acc (_, ci) ->
                        match acc, elemBytes (elemOf arr.[ci].Type) with
                        | Some a, Some b -> Some (a + b)
                        | _ -> None) (Some (if positionsNeeded then 8L else 0L))
                let staticN = staticExtentOf kb.Type |> Option.orElse (match indexOf srcId with Some xi -> staticExtentOf arr.[xi].Type | None -> None)
                let capBytes = compactionSelCapBytes ()
                let capCells = match perCell with Some pc when pc > 0L -> capBytes / pc | _ -> 0L
                let gatherWhy : Result<string, string> =
                    if not gatherOn then Error "opt-in this round (BLADE_COMPACTION_FUSION=gather)"
                    elif ms.Length > 4 then Error $"{ms.Length} companions (the mask-site gather takes J <= 4)"
                    else
                    match sources |> List.tryFind (fun s -> match indexOf s with Some i -> i >= ki | None -> true) with
                    | Some s -> Error $"the source `{(match indexOf s with Some i -> arr.[i].Name | None -> string s)}` is bound after `{kb.Name}` (the mask pass cannot read it)"
                    | None ->
                    let andPos = if positionsNeeded then " and the positions" else ""
                    match perCell, staticN with
                    | None, _ -> Error "a companion's element has no fixed cell width the cap can bound"
                    | Some pc, Some n when n < capCells ->
                        Ok $"gather: at the mask site -- `{kb.Name}`'s pass sweeps the sources once (per 256-cell block: the predicate loop, then the branchless gather of every companion at q): n + 1 = {n + 1L} private cells per buffer ({fmtBytes ((n + 1L) * pc)} over all {ms.Length} companions{andPos} <= sel_cap {fmtBytes capBytes}), m + 1 touched"
                    | Some pc, Some n -> Error $"(n + 1) x {pc} B = {fmtBytes ((n + 1L) * pc)} over every companion buffer exceeds sel_cap {fmtBytes capBytes} (BLADE_TILE_TUNE sel_cap): the joint pass reserves only m + 1 cells per buffer"
                    | Some _, None -> Ok $"gather: at the mask site when n < {capCells} at run time (n + 1 private cells per buffer over every companion within sel_cap {fmtBytes capBytes}), else the joint pass at `{firstName}`"
                let gatherAtMask, gatherLine =
                    match gatherWhy with
                    | Ok line -> true, line
                    | Error why -> false, $"gather: declined -- {why}; the joint gather runs at the first companion `{firstName}`"
                let site = if gatherAtMask then $"`{kb.Name}`'s mask pass" else $"the first companion `{firstName}`"
                let colsShown = ms |> List.map (fun (cid, ci) -> let s = (match dense cid with IRVar (d, _) -> (match indexOf d with Some i -> arr.[i].Name | None -> "c") | _ -> "c") in $"{arr.[ci].Name}[q] = {s}[i];") |> String.concat " "
                let posStore = if positionsNeeded then " pos[q] = i;" else ""
                let posFillNote = if positionsNeeded then ", its kept positions filled by the one joint pass in " + site else ""
                let s =
                    { MaskId = kId; MaskName = kb.Name; Members = ms |> List.map (fun (cid, ci) -> (cid, arr.[ci].Name, (match dense cid with IRVar (d, _) -> d | _ -> srcId)))
                      ElemTypes = ms |> List.map (fun (cid, ci) -> (cid, elemOf arr.[ci].Type)) |> Map.ofList
                      PositionsNeeded = positionsNeeded; GatherAtMask = gatherAtMask; StaticN = staticN; SelCapCells = capCells
                      BuiltCpp = ref None; BuiltLlvm = ref None }
                let evidence =
                    [ (if ms.Length = 1 then $"one selection index for its single companion ({names}, whose source is not the mask's own: the shared-selection path), freed once after `{lastName}`" else $"one selection index shared by {ms.Length} companions ({names}), freed once after `{lastName}`")
                      $"the index: counted in `{kb.Name}`'s own mask pass (cardinality m){posFillNote}; COMPLETE once the first companion `{firstName}` is bound (built at its site, before its wrapper) -- the invariant a later companion relies on: a companion read before a later one is built reads its own data and the already-complete index"
                      $"lifetime: the plan owns the index; each companion's Compound wrapper borrows it and owns only its own compact data store (`deallocate_compound_shared_index`); in a scoped frame the ONE index-only free (`deallocate_compound_index_only`) would be registered with the last companion `{lastName}` -- compound() is top-level only (BL6002) and main's top level has no scope, so NO registration and NO free is executed in either lane (program lifetime); no companion frees the index"
                      $"ONE branchless pass in {site} over `{xName}`'s axis: `b = {kb.Name}[i]; {colsShown}{posStore} q += b` (m + 1 cells per buffer, the slack never exposed) -- ascending, serial; identical values, cardinality and positions to each compound's own count / fill / gather passes ({ms.Length} index builds -> 1, {ms.Length} gathers -> 1 pass)"
                      selection
                      gatherLine
                      predLine
                      matrix
                      $"`{kb.Name}` and every companion's data store ({names}) stay stored; the folds over them keep their own chains, seeds and BL8003 sites" ]
                Ok (s, evidence))
        | _ -> None
    let groupDecisions : Map<IRId, Result<SharedSelection * string list, string>> =
        compoundsOfMask |> Map.toSeq |> Seq.choose (fun (k, ms) -> groupDecision k ms |> Option.map (fun r -> (k, r))) |> Map.ofSeq
    // The shared plans by companion: every companion's plan carries the SAME
    // selection record and the SAME count / gather refs (the mask publishes
    // its count once; every companion reads it).
    let sharedPlans : Map<IRId, CompactionPlan * string list> =
        groupDecisions |> Map.toSeq |> Seq.collect (fun (_, r) ->
            match r with
            | Ok (s, evidence) ->
                let countCpp = ref None
                let countLlvm = ref None
                let gatherCpp = ref None
                let gatherLlvm = ref None
                s.Members |> List.map (fun (cid, cname, src) ->
                    (cid, ({ MaskId = s.MaskId; MaskName = s.MaskName; CompoundId = cid; CompoundName = cname
                             SourceId = src; PositionsNeeded = s.PositionsNeeded; GatherAtMask = s.GatherAtMask
                             StaticN = s.StaticN; SelCapCells = s.SelCapCells
                             CountCpp = countCpp; CountLlvm = countLlvm; GatherCpp = gatherCpp; GatherLlvm = gatherLlvm
                             Shared = Some s }, evidence))) |> Seq.ofList
            | Error _ -> Seq.empty) |> Map.ofSeq
    let groupDeclineOf (yId: IRId) : string option =
        match Map.tryFind yId modul.CompoundInits with
        | Some (_, IRVar (k, _)) ->
            (match Map.tryFind k groupDecisions with
             | Some (Error why) -> Some why
             | _ -> None)
        | _ -> None
    for KeyValue (yId, (denseExpr, maskExpr)) in modul.CompoundInits do
        match indexOf yId with
        | None -> ()
        | Some yi ->
        let yb = arr.[yi]
        let yName = yb.Name
        match stripUnits yb.Type with
        | ArrayElem at when at.IndexTypes |> List.exists (fun ix -> ix.IxKind = IxKCompound) ->
            let rank1 =
                at.IndexTypes.Length = 1
                && (List.head at.IndexTypes).IxKind = IxKCompound && (List.head at.IndexTypes).Rank = 1
            if not rank1 then
                decide yName (Blade.Effects.Declined "v1 fuses the rank-1 compaction only: a rank-k mask (hashed index) or trailing dims keep the separate passes") []
            else
            // ---- B4b: the compaction ------------------------------------
            let compactionDecision : Result<CompactionPlan * string list, string> =
                match denseExpr, maskExpr with
                | IRVar (xId, _), IRVar (kId, _) ->
                    (match indexOf kId with
                     | Some ki when ki < yi ->
                        let kb = arr.[ki]
                        (match kb.Value with
                         | IRMask (IRVar (srcId, _), _) when srcId = xId ->
                            // totalScalar, or (v2) the membership-probe certificate
                            let predAdmission = predicateAdmission kb
                            let between = [ ki + 1 .. yi - 1 ] |> List.map (fun i -> arr.[i])
                            // B4's own motion is the COUNT (taken in the mask
                            // pass, sizing the compound's buffers): sound while
                            // nothing between can change `keep`. Each binding's
                            // whole payload is audited (side-table operands
                            // included): a read of `keep`, a visibly writing
                            // value (round 2's rule), or a writing payload that
                            // can reach `keep` (transitively, through the
                            // callables it names) declines.
                            let offending =
                                between |> List.tryPick (fun b ->
                                    if payloadLifetime b then Some $"`{b.Name}` contains an inline lifetime boundary"
                                    elif readsMask kId b then Some $"`{b.Name}` reads `{kb.Name}` between the mask and the compound"
                                    elif (facts b.Value).MayWrite || writes b.Value then Some $"`{b.Name}` may write (an assignment or a mutating call) between the mask and the compound"
                                    elif payloadWrites b && Set.contains kId (transitiveRefs b) then
                                        Some $"`{b.Name}` may write `{kb.Name}` (its side-table payload writes and reaches it) between the mask and the compound"
                                    else None)
                            // (IsMutable is no evidence either way: every
                            // non-static binding is assignable in its own
                            // scope; an actual assignment is.)
                            if assignedAnywhere kId then Error $"`{kb.Name}` is assigned"
                            else
                            match predAdmission with
                            | Error why -> Error why
                            | Ok _ ->
                            match offending with
                            | Some why -> Error why
                            | None ->
                                Ok ({ MaskId = kId; MaskName = kb.Name; CompoundId = yId; CompoundName = yName
                                      SourceId = xId; PositionsNeeded = true; GatherAtMask = false
                                      StaticN = None; SelCapCells = 0L
                                      CountCpp = ref None; CountLlvm = ref None
                                      GatherCpp = ref None; GatherLlvm = ref None; Shared = None },
                                    [ $"`{kb.Name}`'s count is taken in its own mask pass (no count pass); the kept-position fill and the value gather are ONE branchless pass `v = x[i]; {yName}[q] = v; pos[q] = i; q += {kb.Name}[i]` (m + 1 cells in both buffers, the slack never exposed)"
                                      $"`{yName}`'s index: dense extent n, cardinality m, the ascending kept positions (keyed reads, BL8006 unchanged); positions always built (B4d deferred); `{kb.Name}` stays stored"
                                      "stable, ascending, serial: the same compaction in fewer sweeps" ])
                         | IRMask _ ->
                            (match groupDeclineOf yId with
                             | Some why -> Error $"the mask's source is not the compound's dense operand, and the shared selection (T4c) declined: {why}"
                             | None when mode = CompactionFusionUnshared -> Error "the mask's source is not the compound's dense operand (the shared selection, T4c, is disabled by BLADE_COMPACTION_FUSION=unshared)"
                             | None -> Error "the mask's source is not the compound's dense operand")
                         | _ -> Error "the compound's mask is not a let-bound `mask(x, pred)`")
                     | _ -> Error "the compound's mask is not a let-bound `mask(x, pred)` ahead of it")
                | _ -> Error "the mask or the dense operand is written in place (v1 fuses a let-bound mask over a stored array)"
            // A mask feeding SEVERAL compounds publishes its count to ONE plan
            // (the table keeps one plan per mask: the last compound's), so the
            // others keep their separate passes -- and say so in every mode,
            // `b4` included (the round-2 record called them applied while
            // their emission took the unfused path; the emission is unchanged).
            let sharedDecline =
                match compactionDecision with
                | Ok (plan, _) when enabled ->
                    let members =
                        modul.CompoundInits |> Seq.choose (fun (KeyValue (cid, (_, m))) ->
                            match m with
                            | IRVar (v, _) when v = plan.MaskId -> Some cid
                            | _ -> None) |> List.ofSeq
                    let last = List.max members
                    if members.Length > 1 && yId <> last then
                        let lastName = match indexOf last with Some li -> arr.[li].Name | None -> "another compound"
                        let t4c =
                            match groupDeclineOf yId with
                            | Some why -> $"the shared selection (T4c) declined: {why}"
                            | None when mode = CompactionFusionUnshared -> "the shared selection, T4c, is disabled by BLADE_COMPACTION_FUSION=unshared"
                            | None -> "a selection shared across compounds is T4c, which this mode does not take"
                        Some $"`{plan.MaskName}` feeds {members.Length} compounds and its in-pass count serves one, the last (`{lastName}`): `{yName}` keeps the separate count / fill / gather passes ({t4c})"
                    else None
                | _ -> None
            // T4c: this compound is a companion of an admitted shared selection.
            let shared = Map.tryFind yId sharedPlans
            // The compaction is planned (its decision is recorded below, once
            // the folds -- the outer kernels of T4a's matrix -- are known).
            let compaction =
                match shared, compactionDecision with
                | Some (plan, _), _ -> Some plan
                | None, Ok (plan, _) when enabled && sharedDecline.IsNone -> Some plan
                | _ -> None
            // ---- B4a (+ B4c): the folds over y's self-product -----------
            // Planned here, recorded after the compaction's decision.
            let foldRecords = ResizeArray<(unit -> unit)>()
            let foldPlans = ResizeArray<CompactionFoldPlan * IRCallable>()
            let finalPlan : CompactionPlan option ref = ref None
            for hi in yi + 1 .. arr.Length - 1 do
                let hb = arr.[hi]
                for (mid, info, k, init) in foldSites yId hb.Value do
                    let what = $"fold of `{yName} * {yName}`"
                    let declined (why: string) = foldRecords.Add(fun () -> decide hb.Name (Blade.Effects.Declined why) [ what ])
                    let fk = resolveCallableIn funcs (fst (peelReynolds k))
                    let elemOk =
                        match stripUnits at.ElemType with
                        | IRTScalar ETFloat64 -> true
                        | _ -> false
                    if init.IsSome then declined "a seeded fold (v1 fuses the unseeded `reduce(y * y, op)`)"
                    elif not (squareMap info yId) then declined $"the folded map is not the self-product `{yName} * {yName}` of `{yName}`'s own cells (v1 fuses only it: no outer product, no co-iteration with another array, no capture)"
                    elif not elemOk then declined "the compound's element is not Float64"
                    // The SNAPSHOT proof: the map materialized `y * y` at its
                    // let; the fold reads `y` itself at the reduce. Equal only
                    // if nothing writes between them -- and the map and the
                    // reduce both sit inside `hb`'s value, so no write in that
                    // value at all is the proof (a mutating call through a
                    // `mut` parameter or an alias writes without naming `y`).
                    elif payloadLifetime hb then
                        declined $"`{hb.Name}` contains an inline lifetime boundary"
                    elif payloadWrites hb then
                        declined $"`{hb.Name}` may write (an assignment or a mutating call) between the map's materialization and the fold, so the map's snapshot could differ from `{yName}` at the fold"
                    else
                    match fk with
                    | None -> declined "the fold kernel does not resolve to a callable"
                    | Some f when f.IsOmpParallel || f.IsCudaKernel || f.IsMpiParallel ->
                        declined "the fold kernel requests a thread team or a device (`where omp` / cuda / mpi): v1 keeps the materialized route, which owns that census"
                    | Some f when not (builtinAdd f) -> declined "the fold kernel is not the builtin `+` (v1 fuses `reduce(y * y, (+))`)"
                    | Some f ->
                        // Feeding the chain inside the compaction pass moves
                        // its evaluation from `hb` up to `y`: sound when no
                        // one can change `y`'s cells in between. Without an
                        // alias analysis (a `let mut alias = y` shares the
                        // storage; a call through a `mut` parameter writes
                        // without naming `y`), the proof is NO write at all
                        // from `y` through `hb`, and no assignment to `y`.
                        // (T4a moves it further up, to the mask: the interval
                        // `keep`..`y` is write-free by the compaction's own
                        // admission, and the matrix below names it again.)
                        let inPass =
                            compaction.IsSome && not (assignedAnywhere yId)
                            && ([ yi + 1 .. hi ] |> List.forall (fun i -> not (payloadWrites arr.[i]) && not (payloadLifetime arr.[i])))
                        let plan =
                            { MapId = mid; CompoundId = yId; CompoundName = yName; HostName = hb.Name
                              FoldKernel = k; InPass = inPass
                              EmittedCpp = ref None; EmittedLlvm = ref None }
                        foldPlans.Add((plan, f))
                        foldRecords.Add(fun () ->
                            let site =
                                match finalPlan.Value with
                                | Some { Shared = Some s } ->
                                    let firstName = match s.Members with (_, n, _) :: _ -> n | [] -> yName
                                    if s.GatherAtMask && s.StaticN.IsSome then $"`{s.MaskName}`'s mask pass (T4a, the shared selection's one pass)"
                                    elif s.GatherAtMask then $"`{s.MaskName}`'s mask pass (T4a) when the single pass runs, else the shared selection's joint pass at `{firstName}`"
                                    else $"the shared selection's one joint pass at `{firstName}` (T4c)"
                                | Some p when p.GatherAtMask && p.StaticN.IsSome -> $"`{p.MaskName}`'s mask pass (T4a)"
                                | Some p when p.GatherAtMask -> $"`{p.MaskName}`'s mask pass (T4a) when the single pass runs, else `{yName}`'s compaction pass"
                                | _ -> $"`{yName}`'s compaction pass"
                            let evidence =
                                [ $"`{hb.Name}` = {what}: folded straight over `{yName}`'s compact buffer (m contiguous cells), no square pool; licensed: the lane form, repro: the unseeded serial chain from the first kept square, in kept order; every product rounded before its add in both lanes (C++ an asm rounding barrier, LLVM an undecorated fmul), as the stored product was; no write between the map and the fold"
                                  (if inPass then $"under the serial (repro) form the chain is fed inside {site}, block by block, from completed cells only; the empty check (BL8003) stays at `{hb.Name}`"
                                   else $"the fold runs at `{hb.Name}` (the compaction pass does not feed it)")
                                  $"`{yName}` and every named binding stay stored" ]
                            if not enabled then
                                decide hb.Name (Blade.Effects.Declined "disabled by BLADE_COMPACTION_FUSION") evidence
                            else
                                CompactionTable.recordFold plan
                                decide hb.Name Blade.Effects.Applied evidence)
            // ---- T4: the selection (T4b census) and the gather (T4a) ------
            let compaction =
                match compaction, compactionDecision with
                | Some plan, _ when shared.IsSome ->
                    // T4c: the group's record (the same evidence on every companion)
                    let evidence = snd shared.Value
                    CompactionTable.record plan
                    decide yName Blade.Effects.Applied evidence
                    Some plan
                | Some plan, Ok _ when v2 ->
                    let kb = arr.[(indexOf plan.MaskId).Value]
                    let ki = (indexOf plan.MaskId).Value
                    let xName =
                        match indexOf plan.SourceId with
                        | Some xi -> arr.[xi].Name
                        | None -> "x"
                    // T4b
                    let census = positionsDemand yId yName
                    let positionsNeeded = match census with Ok _ -> false | Error _ -> true
                    let selection =
                        match census with
                        | Ok uses ->
                            let seen = if uses.IsEmpty then "no use" else String.concat ", " uses
                            $"selection: positions elided (census: {seen}; printing reads the compact buffer only) -- `{yName}`'s index keeps dense extent n and cardinality m, and any lookup through the absent table is an internal error (BL9002), never a read"
                        | Error why ->
                            $"selection: positions required by {why} -- `{yName}`'s index holds the ascending kept positions (keyed reads, BL8006 unchanged)"
                    // T4a: selectionAdmits, the five parties of the motion
                    // (the gather moves from `y` up to `keep`; an in-pass
                    // chain moves from its fold's host up to `keep`).
                    let predCallable =
                        match kb.Value with
                        | IRMask (_, pred) -> resolveCallableIn funcs pred
                        | _ -> None
                    let wrote (e: IRExpr) = (facts e).MayWrite || writes e
                    let inPassFolds = foldPlans |> Seq.filter (fun (fp, _) -> fp.InPass) |> List.ofSeq
                    let between = [ ki + 1 .. yi - 1 ] |> List.map (fun i -> arr.[i])
                    let sharers =
                        modul.CompoundInits |> Seq.filter (fun (KeyValue (_, (_, m))) ->
                            match m with IRVar (v, _) -> v = plan.MaskId | _ -> false) |> Seq.length
                    let perCell =
                        elemBytes at.ElemType
                        |> Option.map (fun b -> b + (if positionsNeeded then 8L else 0L))
                    let staticN = staticExtentOf kb.Type |> Option.orElse (staticExtentOf (match indexOf plan.SourceId with Some xi -> arr.[xi].Type | None -> IRTUnit))
                    let capBytes = compactionSelCapBytes ()
                    let posWord = if positionsNeeded then ", kept position" else ""
                    let posStore = if positionsNeeded then " pos[q] = i;" else ""
                    let posFill = " and the kept-position fill"
                    let posNone = " (positions elided)"
                    let producerParty =
                        match indexOf plan.SourceId with
                        | Some xi when payloadWrites arr.[xi] -> Error $"the producer `{xName}` may write (its initializer or side-table payload, through the shared write analysis)"
                        | Some _ -> Ok $"producer `{xName}`: no write"
                        | None -> Error $"the producer `{xName}` is not a module binding the write analysis can see"
                    let predicateParty =
                        match predCallable with
                        | Some pc when not (wrote pc.Body) -> Ok $"predicate of `{kb.Name}`: no write (pure, total)"
                        | Some _ -> Error $"the predicate of `{kb.Name}` may write"
                        | None -> Error $"the predicate of `{kb.Name}` does not resolve to a callable"
                    let foldParty =
                        match inPassFolds |> List.tryFind (fun (_, f) -> wrote f.Body) with
                        | Some (fp, _) -> Error $"the outer fold kernel of `{fp.HostName}` may write"
                        | None ->
                            if inPassFolds.IsEmpty then Ok "outer fold kernel(s): none fed in the pass"
                            else Ok ("outer fold kernel(s): " + (inPassFolds |> List.map (fun (fp, _) -> $"`{fp.HostName}` (+)") |> String.concat ", ") + ": no write")
                    let seedParty = Ok "seed(s): none (the compaction has none; every planned fold is unseeded and keeps its BL8003 check at its own site)"
                    // Each intervening binding's WHOLE payload (its value and
                    // the side-table operands its site runs -- a random key, a
                    // compound's operands) and any provider write at it: the
                    // gather moves ahead of all of them.
                    let betweenParty =
                        let offends (b: IRBinding) =
                            if payloadLifetime b then Some "contains an inline lifetime boundary"
                            elif payloadWrites b then Some "may write (its value or side-table payload)"
                            elif modul.ProviderWrites.ContainsKey b.Id then Some "is a provider write (an external effect)"
                            elif readsMask plan.MaskId b then Some $"reads `{kb.Name}`"
                            else None
                        match between |> List.tryPick (fun b -> offends b |> Option.map (fun why -> (b, why))) with
                        | Some (b, why) -> Error $"`{b.Name}` (between `{kb.Name}` and `{yName}`) {why}"
                        | None ->
                            if between.IsEmpty then Ok $"between `{kb.Name}` and `{yName}`: no binding"
                            else Ok ($"between `{kb.Name}` and `{yName}`: " + (between |> List.map (fun b -> $"`{b.Name}`") |> String.concat ", ") + $": no write in any payload, no provider write, no read of `{kb.Name}`")
                    let parties = [ producerParty; predicateParty; foldParty; seedParty; betweenParty ]
                    let matrix =
                        "selectionAdmits (five parties, each through Optimize.scheduleMayWriteIn): "
                        + (parties |> List.map (function Ok s -> s | Error s -> "DECLINES: " + s) |> String.concat "; ")
                    let gatherWhy : Result<string, string> =
                        if not gatherOn then
                            Error "opt-in this round (BLADE_COMPACTION_FUSION=gather): T4a's own measured gain on maskagg, 0.18-0.36 ms, is under the round's 0.4 ms acceptance line"
                        else
                        match parties |> List.tryPick (function Error s -> Some s | Ok _ -> None) with
                        | Some why -> Error why
                        | None ->
                        if sharers > 1 then Error $"`{kb.Name}` feeds {sharers} compounds (a selection shared across compounds is T4c: not taken here)"
                        else
                        match perCell with
                        | None -> Error "the compound's element has no fixed cell width the cap can bound"
                        | Some pc ->
                            let capCells = capBytes / pc
                            match staticN with
                            | Some n when n < capCells ->
                                Ok $"gather: at the mask site -- `{kb.Name}`'s pass sweeps `{xName}` once (per 256-cell block: the predicate loop, then the branchless gather of the kept value{posWord} at q over the L1-resident block): n + 1 = {n + 1L} private cells per buffer ({fmtBytes ((n + 1L) * pc)} <= sel_cap {fmtBytes capBytes}), m + 1 of them touched by the n branchless stores"
                            | Some n ->
                                Error $"(n + 1) x {pc} B = {fmtBytes ((n + 1L) * pc)} exceeds sel_cap {fmtBytes capBytes} (BLADE_TILE_TUNE sel_cap): the two-pass form reserves only m + 1 cells"
                            | None ->
                                Ok $"gather: at the mask site when n < {capCells} at run time (n + 1 private cells x {pc} B within sel_cap {fmtBytes capBytes}), else the compound-site pass -- `{kb.Name}`'s pass sweeps `{xName}` once; m + 1 cells touched by the n branchless stores"
                    let gatherAtMask, gatherLine =
                        match gatherWhy with
                        | Ok line -> true, line
                        | Error why -> false, $"gather: declined -- {why}; the gather stays at `{yName}` (one fill+gather pass, B4)"
                    let capCells = match perCell with Some pc -> capBytes / pc | None -> 0L
                    let plan' =
                        { plan with PositionsNeeded = positionsNeeded; GatherAtMask = gatherAtMask
                                    StaticN = staticN; SelCapCells = capCells }
                    let passLine =
                        if gatherAtMask then
                            $"`{kb.Name}`'s count AND `{yName}`'s gather run in `{kb.Name}`'s own mask pass: `v = {xName}[i]; b = pred(v); {kb.Name}[i] = b; {yName}[q] = v;{posStore} q += b` per 256-cell block -- no count pass and one sweep of `{xName}` and `{kb.Name}` from memory (the gather loop re-reads each block's cells and bits from L1: two loads of `{xName}`, a bit store and load, and a value store per source cell); `{kb.Name}` stays stored"
                        else
                            $"`{kb.Name}`'s count is taken in its own mask pass (no count pass); the value gather{(if positionsNeeded then posFill else posNone)} is ONE branchless pass at `{yName}`: `v = {xName}[i]; {yName}[q] = v;{posStore} q += {kb.Name}[i]` (m + 1 cells per buffer, the slack never exposed); `{kb.Name}` stays stored"
                    let evidence =
                        [ passLine; selection; gatherLine ]
                        @ (if gatherOn then [ matrix ] else [])
                        @ [ "stable, ascending, serial: the same compaction in fewer sweeps" ]
                    CompactionTable.record plan'
                    decide yName Blade.Effects.Applied evidence
                    Some plan'
                | Some plan, Ok (_, evidence) ->
                    CompactionTable.record plan
                    decide yName Blade.Effects.Applied evidence
                    Some plan
                | None, Ok _ when enabled && sharedDecline.IsSome ->
                    decide yName (Blade.Effects.Declined sharedDecline.Value) []
                    None
                | _, Ok (_, evidence) ->
                    decide yName (Blade.Effects.Declined "disabled by BLADE_COMPACTION_FUSION") evidence
                    None
                | _, Error why ->
                    decide yName (Blade.Effects.Declined why) []
                    None
            finalPlan.Value <- compaction
            for r in foldRecords do r ()
        | _ -> ()

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

// --- The row-owned pull scatter's five-party audit (round 3, D-P3) ---------
//
// The C++ lane's rank-3 ordered orbit scatter may run its LEAF stage on a
// thread team (CodeGenBinding.genOrbitPullCpp; the ownership proof is in
// blade_orbit_scatter.hpp leaf3_pull). Nothing is MOVED across bindings: the
// pool is a STORED array materialized before the fold, and the team evaluates
// only the fold's own pure, total sub-computations (OrbitFold.planStrict's
// closed whitelist). The admission still audits every party with the shared
// transitive write analysis (builder preamble rule 4) -- this planner records
// what the IR shows; the codegen side adds the callback-aware analysis of the
// team's bodies, the request gate and the knobs:
//   producer    -- the pool's binding, by its COMPLETE payload
//                  (intervalPayloadOf): no write, no provider effect. It ran
//                  to completion at its own site and is neither re-run nor
//                  moved;
//   consumer    -- the walk (`comp`: every level's map kernel, every inner
//                  level's fold kernel and seed, the cell function): no write;
//   outer       -- the outer fold kernel: no write;
//   seed        -- the outer seed: no write;
//   intervening -- every binding between the pool's binding and the fold (by
//                  its payload: side-table operands and a provider write's
//                  source included): no write, no provider effect.
// A writing party DECLINES (the walk's other strict forms). A pool bound
// OUTSIDE the fold's chain -- a parameter of the enclosing function, or a
// top-level binding ahead of the enclosing binding (a block's let chain) --
// was materialized before the chain ran: a parameter has no producer in this
// scope, a top-level binding is audited by its payload, and every statement
// between it and the fold is intervening. Recorded per (consumer, source); a
// walk with no record (any other placement) DECLINES: unknown = refuse.
module OrbitPullTable =
    open System.Threading
    type private State = { Audits: Map<IRId * IRId, Result<string list, string>> }
    let private empty = { Audits = Map.empty }
    let private cell = new AsyncLocal<State>()
    let private get () = match box cell.Value with null -> empty | _ -> cell.Value
    let reset () = cell.Value <- empty
    let record (consumer: IRId) (source: IRId) (audit: Result<string list, string>) =
        cell.Value <- { Audits = Map.add (consumer, source) audit (get ()).Audits }
    /// Ok (the planner's matrix lines) or Error (the first writing party);
    /// None = no audit for this (walk, pool).
    let tryAudit (consumer: IRId) (source: IRId) : Result<string list, string> option =
        Map.tryFind (consumer, source) (get ()).Audits

/// Plan the pull scatter's audits over the same three scopes as the compact
/// row classes: function-body let chains, the top-level binding sequence, and
/// the let chain nested inside a top-level binding's value (comoment3's
/// `let (checksum, input_checksum) = { let x ..; let t ..; let ss .. }`).
/// Records facts only (no IR change, no decision record: the emitter records
/// the outcome); no gate of its own (the emitter's BLADE_ORBIT_PULL decides).
let planOrbitPull (programFuncs: IRCallable list) (modul: IRModule) : unit =
    let funcs = modul.Functions |> List.map (fun f -> (f.Id, f)) |> Map.ofList
    let facts = cseFactsOf funcs
    let writes = scheduleMayWriteIn programFuncs modul
    let lifetimeBarrier = scheduleHasLifetimeBarrierIn programFuncs modul
    let payloadOf = intervalPayloadOf modul
    let providerEffect = intervalHasProviderEffect modul
    let mayWrite (e: IRExpr) = writes e || (facts e).MayWrite
    let bindingTypes = modul.Bindings |> List.map (fun b -> (b.Id, b.Type)) |> Map.ofList
    // every id `e` reads, directly or through a callable it names (a lifted
    // kernel lambda reads its array by id inside its own body)
    let readsOf (e: IRExpr) : Set<IRId> =
        let visited = System.Collections.Generic.HashSet<IRId>()
        let acc = System.Collections.Generic.HashSet<IRId>()
        let rec go (e: IRExpr) =
            for id in collectVarRefsIR e do
                acc.Add id |> ignore
                match Map.tryFind id funcs with
                | Some cl when visited.Add id -> go cl.Body
                | _ -> ()
        go e
        Set.ofSeq acc
    let isSymPool (t: IRType) =
        match stripUnits t with
        | ArrayElem at when not at.IsVirtual ->
            (match at.IndexTypes with
             | [ ix ] -> ix.Symmetry = SymSymmetric && ix.Rank >= 2
             | _ -> false)
        | _ -> false
    let typeOfItem (id: IRId) (v: IRExpr) =
        match Map.tryFind id bindingTypes with
        | Some t -> t
        | None -> (try typeOf v with _ -> IRTUnit)
    let walkOf (v: IRExpr) =
        match v with
        | IRReduceCompute ((IRApplyCombinator oinfo as comp), k, seed) ->
            (match oinfo.Arrays with
             | [ IRRange ([ _ ], None) ] -> Some (comp, k, seed)
             | _ -> None)
        | _ -> None
    let walkWhy (comp: IRExpr) (k: IRExpr) (seed: IRExpr) =
        if mayWrite comp then Some "the walk (consumer: every level's map kernel, an inner level's fold kernel or seed, the cell function) may write (shared transitive write analysis)"
        elif mayWrite k then Some "the outer fold kernel may write (shared transitive write analysis)"
        elif mayWrite seed then Some "the outer seed may write (shared transitive write analysis)"
        else None
    let interveningWhy (poolName: string) (items: (IRId * string * IRExpr) list) =
        items |> List.tryPick (fun (iId, iName, iVal0) ->
            let iVal = payloadOf iId iVal0
            if lifetimeBarrier iVal then
                Some $"`{iName}` is an inline lifetime boundary between the pool and the fold"
            elif mayWrite iVal || providerEffect iId then
                Some $"`{iName}` may write between `{poolName}`'s materialization and the fold (an assignment, a mutating call or a provider write -- its side-table payload included)"
            else None)
    let matrix (poolName: string) (producer: string) (between: int) =
        [ "five-party audit (planner: Optimize.scheduleMayWriteIn over the module + cseFactsOf, intervening payloads by intervalPayloadOf): "
          + $"producer = `{poolName}` {producer}; "
          + "consumer = the walk (every level's map kernel, every inner level's fold kernel and seed, the cell function) -- no write; "
          + "outer kernel = the outer fold kernel -- no write; "
          + "seeds = the outer seed -- no write (the inner seeds are the consumer's); "
          + $"intervening = {between} binding(s) between the pool's materialization and the fold -- no write, no provider effect (none is moved: the pull reorders only the fold's own sub-computations)" ]
    let audit (poolName: string) (producerWhy: string option) (producer: string)
              (comp: IRExpr) (k: IRExpr) (seed: IRExpr) (between: (IRId * string * IRExpr) list) =
        match producerWhy with
        | Some why -> Error why
        | None ->
        match walkWhy comp k seed with
        | Some why -> Error why
        | None ->
        match interveningWhy poolName between with
        | Some why -> Error why
        | None -> Ok (matrix poolName producer between.Length)
    let stored = "STORED at its own binding before the fold (its payload write-free; it ran to completion and is neither re-run nor moved)"
    let producerWhyOf (sId: IRId) (sName: string) (sVal: IRExpr) =
        let pv = payloadOf sId sVal
        if mayWrite pv || providerEffect sId then
            Some $"the pool's producer `{sName}` may write (its complete payload; shared transitive write analysis)"
        else None
    /// `outer`: pools dominating the chain -- (id, name, producer decline,
    /// producer phrase, the statements between it and the chain).
    let planChain (outer: (IRId * string * string option * string * (IRId * string * IRExpr) list) list)
                  (items: (IRId * string * IRExpr) list) =
        let arr = List.toArray items
        for ci in 0 .. arr.Length - 1 do
            let (cId, _, cVal) = arr.[ci]
            match walkOf cVal with
            | None -> ()
            | Some (comp, k, seed) ->
                let reads = readsOf comp
                for si in 0 .. ci - 1 do
                    let (sId, sName, sVal) = arr.[si]
                    if reads.Contains sId && isSymPool (typeOfItem sId sVal) then
                        let between = [ for i in si + 1 .. ci - 1 -> arr.[i] ]
                        OrbitPullTable.record cId sId
                            (audit sName (producerWhyOf sId sName sVal) stored comp k seed between)
                for (sId, sName, pWhy, producer, ahead) in outer do
                    if reads.Contains sId then
                        let between = ahead @ [ for i in 0 .. ci - 1 -> arr.[i] ]
                        OrbitPullTable.record cId sId (audit sName pWhy producer comp k seed between)
    let rec chain (e: IRExpr) : (IRId * string * IRExpr) list =
        match e with
        | IRLet (id, v, body) -> (id, $"__v{id}", v) :: chain body
        | _ -> []
    for f in modul.Functions do
        let pools =
            f.Params |> List.filter (fun p -> isSymPool p.Type)
            |> List.map (fun p -> (p.VarId, p.Name, None, "a parameter of the enclosing function (materialized by the caller; no producer in this scope)", []))
        planChain pools (chain f.Body)
    let top = modul.Bindings |> List.map (fun b -> (b.Id, b.Name, b.Value))
    planChain [] top
    let bArr = List.toArray modul.Bindings
    let topArr = List.toArray top
    bArr |> Array.iteri (fun bi b ->
        match b.Value with
        | IRLet _ ->
            let pools =
                [ for j in 0 .. bi - 1 do
                    let pb = bArr.[j]
                    if isSymPool pb.Type then
                        let ahead = [ for i in j + 1 .. bi - 1 -> topArr.[i] ]
                        yield (pb.Id, pb.Name, producerWhyOf pb.Id pb.Name pb.Value, stored, ahead) ]
            planChain pools (chain b.Value)
        | _ -> ())
// --- Membership hoisting (round 3, item B-T1; plan rule `member-hoist`) -----
//
// `contains(B, x)` is a linear scan of B. Inside a kernel run once per cell --
// a `mask(src, pred)` predicate, a map kernel, a fold kernel -- that is
// O(n M) compares for an n-cell host over an M-cell set (the semijoin shape
// `keep = mask(cust, lambda(c) -> contains(elig, c))`). The plan builds ONE
// search structure over B immediately before the HOST binding (the mask / map
// / fold whose kernels hold the probes), frees it right after, and each probe
// reads it: blade_member.h's linear / bitmap / hash representation, chosen
// DETERMINISTICALLY from (M, min, max) and the knob only (never from timing),
// all three answering exactly integer `==` against every cell. The IR is
// untouched: the interpreter runs the as-written scan, so `blade test interp`
// compares the as-written program with the hoisted physical form, and
// `blade test opt-diff` compares the gate off with it on.
//
// THE ONE TARGET-EVALUATION CONTRACT (every lane, hoisted or not): the set
// operand is evaluated first, then the target exactly ONCE, before any cell
// is read -- for an empty set too (the interpreter's Loops.fs order; the
// C++ IIFE used to interpolate the target inside the scan, once per scanned
// cell and never for an empty set). A failing target therefore fails before
// any element is read, in every lane.
//
// Shapes (v1): the host is an EAGER top-level binding (not a deferred map;
// of the side-table bindings only a `compound(dense, mask)`, whose site runs
// its inline mask -- the canonical semijoin); the probe sits directly in the body of a
// kernel the host's value names (mask predicate, map kernel, fold kernel),
// that kernel is referenced exactly once in the program (so it runs only
// inside the host), and it is neither a CUDA nor an MPI kernel; the set is a
// NAMED, earlier, STORED top-level binding -- rank 1, dense or a rank-1
// compound, Int64 keys -- that nothing assigns (and not `let mut`). A
// deferred set (a proved-pure producer forced once at the host) is v1.1.
//
// Admission `membershipAdmits` -- the five parties of the motion (the build
// moves up to the host's first statement), each through the shared write
// analysis (`scheduleMayWriteIn`, beside the conservative `cseFactsOf`): the
// set producer (its payload; stored, so not moved -- a writing producer still
// declines), the consumer kernel(s) holding the probes, the enclosing fold
// kernel(s) and seed(s), the host's whole payload (its source, kernels,
// seeds and every statement before its cell loop, all of which now run after
// the build), and the intervening bindings (none: the build is the host's
// first statement). A parallel (`where omp`) kernel is admitted -- the build
// is serial, before the team, and every thread only reads it -- once every
// `callbackTable` candidate of its function-valued parameters is proved
// write-free. A writing party declines to the as-written per-cell scan.
//
// Gate: BLADE_MEMBER_HOIST -- unset / `1` / `on` = the chooser; `linear`,
// `bitmap`, `hash` force a representation for A/B (a forced bitmap whose span
// exceeds its 2^26-bit cap is built as a hash; never an abort); `0` / `off`
// keeps the as-written scan everywhere (the analysis still runs and records
// `declined -- disabled by BLADE_MEMBER_HOIST`).

/// BLADE_MEMBER_HOIST's reading (per call).
type MemberHoistMode =
    | MemberHoistOff
    | MemberHoistAuto
    /// 1 = linear, 2 = bitmap, 3 = hash (blade_member.h's kind numbers).
    | MemberHoistForce of rep: int

let memberHoistMode () : MemberHoistMode =
    match Blade.Toolchain.envSwitch "BLADE_MEMBER_HOIST" with
    | Blade.Toolchain.SwitchOff -> MemberHoistOff
    | Blade.Toolchain.SwitchOther v ->
        match v.Trim().ToLowerInvariant() with
        | "linear" -> MemberHoistForce 1
        | "bitmap" -> MemberHoistForce 2
        | "hash" -> MemberHoistForce 3
        | _ -> MemberHoistAuto
    | _ -> MemberHoistAuto

let memberHoistEnabled () = memberHoistMode () <> MemberHoistOff

/// blade_member.h's representation numbers, spelled.
let memberRepName (k: int) : string =
    match k with
    | 1 -> "linear"
    | 2 -> "bitmap"
    | 3 -> "hash"
    | _ -> "none"

/// blade_member.h's chooser (`blade_member_choose_i64`) restated, for the plan
/// record of a LITERAL set: the PREFERRED representation (the build makes the
/// same choice at run time -- keep the two in step -- and an allocation it
/// cannot make degrades it, bitmap -> hash -> linear, reported by the build,
/// never by this function): M = 0 -> linear (before min / max exist); a forced
/// representation (a forced bitmap over a span past its 2^26-bit cap ->
/// hash); else M <= 16 -> linear, span <= max(64 M, 4096) within the cap ->
/// bitmap, otherwise hash. The span is the unsigned difference max - min,
/// never max - min + 1 (which overflows for the full Int64 range).
let memberChooseRep (m: int64) (lo: int64) (hi: int64) (forced: int) : int =
    if m <= 0L then 1
    else
        let spanM1 = uint64 hi - uint64 lo
        let fits = spanM1 < (1UL <<< 26)
        if forced = 1 then 1
        elif forced = 2 then (if fits then 2 else 3)
        elif forced = 3 then 3
        elif m <= 16L then 1
        else
            let limit =
                if m >= (1L <<< 57) then System.UInt64.MaxValue
                else max 4096UL (uint64 m * 64UL)
            if fits && spanM1 < limit then 2 else 3

/// Plan membership hoisting over the module's top-level bindings. Records
/// into `MemberHoistTable` and the decision collector (`member-hoist`); the
/// IR is untouched.
let planMembershipHoist (programFuncs: IRCallable list) (modul: IRModule) : unit =
    let mode = memberHoistMode ()
    let enabled = mode <> MemberHoistOff
    let forced = match mode with MemberHoistForce k -> k | _ -> 0
    let funcs = modul.Functions |> List.map (fun f -> (f.Id, f)) |> Map.ofList
    let allCallables = (modul.Functions @ programFuncs) |> List.distinctBy (fun f -> f.Id)
    let callableById = allCallables |> List.map (fun f -> (f.Id, f)) |> Map.ofList
    let facts = cseFactsOf funcs
    let writes = scheduleMayWriteIn programFuncs modul
    let lifetimeBarrier = scheduleHasLifetimeBarrierIn programFuncs modul
    let wrote (e: IRExpr) = (facts e).MayWrite || writes e
    let decide (subject: string) (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) =
        Blade.Effects.Decisions.record
            { Blade.Effects.Rule = "member-hoist"; Version = 1
              Span = declSpanOf subject; Subject = subject
              Outcome = outcome; Evidence = evidence }
    let arr = List.toArray modul.Bindings
    let indexOf = arr |> Array.mapi (fun i b -> (b.Id, i)) |> Map.ofArray
    // How many times each id is NAMED across every executable root: binding
    // payloads (values and side-table operands) and every callable body.
    let refCount = System.Collections.Generic.Dictionary<IRId, int>()
    let countRefs (e: IRExpr) =
        iterIRExpr (fun n ->
            match n with
            | IRVar (id, _) ->
                refCount.[id] <- (match refCount.TryGetValue id with | true, k -> k + 1 | _ -> 1)
            | _ -> ()) e
    for b in arr do
        for e in bindingPayload modul b do countRefs e
    for f in allCallables do countRefs f.Body
    let refsOf (id: IRId) = match refCount.TryGetValue id with | true, k -> k | _ -> 0
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
        || (allCallables |> List.exists (fun f -> assignsTo id f.Body))
    let isPlaceholder (b: IRBinding) =
        modul.CompoundInits.ContainsKey b.Id || modul.SparseInits.ContainsKey b.Id || modul.RandomInits.ContainsKey b.Id
    // THE POSITIVE STORAGE PROOF of a set at the build point: the shared
    // `storedArrayProof` (defined ahead of compaction fusion, which applies
    // it to the mask source and every companion source of a shared selection).
    let storedProof = storedArrayProof modul
    let storageOf (_: Set<IRId>) (b: IRBinding) : Result<string * IRBinding list, string> = storedProof b
    // The callback table over the module (parallel kernels' function-valued
    // parameters): computed once, on demand.
    let cbTable =
        lazy (
            let resolve (e: IRExpr) =
                match e with
                | IRVar (id, _) -> Map.tryFind id callableById
                | _ -> None
            callbackTable resolve allCallables (arr |> Array.map (fun b -> (b.Id, bindingPayloadExpr modul b)) |> List.ofArray))
    // A kernel reference resolved to its callable, through a top-level let
    // alias of a lambda (`let pred = lambda ..`, then `mask(xs, pred)`); the
    // alias's own id is kept, because every use of the alias runs the kernel.
    let aliasOf = System.Collections.Generic.Dictionary<IRId, IRId>()
    let resolveKernel (k: IRExpr) : IRCallable option =
        match fst (peelReynolds k) with
        | IRVar (id, _) as head ->
            match resolveCallableIn funcs head with
            | Some c -> Some c
            | None ->
                match Map.tryFind id indexOf with
                | Some i ->
                    match resolveCallableIn funcs arr.[i].Value with
                    | Some c ->
                        aliasOf.[c.Id] <- id
                        Some c
                    | None -> None
                | None -> None
        | other -> resolveCallableIn funcs other
    // How many evaluations can run a kernel: its own references, or -- for a
    // kernel reached through a let alias -- the alias's.
    let kernelRefs (c: IRCallable) =
        match aliasOf.TryGetValue c.Id with
        | true, a -> max (refsOf c.Id) (refsOf a)
        | _ -> refsOf c.Id
    // The kernels a host's value names, with their role: what runs per cell.
    let kernelsOf (v: IRExpr) : (IRCallable * string) list =
        let acc = ResizeArray<IRCallable * string>()
        let add (k: IRExpr) (role: string) =
            match resolveKernel k with
            | Some c when not (acc |> Seq.exists (fun (x, _) -> x.Id = c.Id)) -> acc.Add((c, role))
            | _ -> ()
        iterIRExpr (fun n ->
            match n with
            | IRMask (_, k) -> add k "mask predicate"
            | IRApplyCombinator info -> add info.Kernel "map kernel"
            | IRReduce (_, k, _) | IRReduceCompute (_, k, _) -> add k "fold kernel"
            | _ -> ()) v
        List.ofSeq acc
    // The enclosing folds of a host's value: (kernel, seed) per fold.
    let foldsOf (v: IRExpr) : (IRExpr * IRExpr option) list =
        let acc = ResizeArray<IRExpr * IRExpr option>()
        iterIRExpr (fun n ->
            match n with
            | IRReduce (_, k, init) -> acc.Add((k, init))
            | IRReduceCompute (_, k, init) -> acc.Add((k, Some init))
            | _ -> ()) v
        List.ofSeq acc
    let probesIn (c: IRCallable) : (IRExpr * IRExpr) list =
        let acc = ResizeArray<IRExpr * IRExpr>()
        iterIRExpr (fun n ->
            match n with
            | IRContains (a, x) -> acc.Add((a, x))
            | _ -> ()) c.Body
        List.ofSeq acc
    // The set's integer key class (v1: Int64, through units and index tags).
    let keyClass (t: IRType) : Result<bool, string> =
        match stripUnits t with
        | ArrayElem at ->
            let rec scalarOf (t: IRType) =
                match stripUnits t with
                | IRTIdxTagged (inner, _) -> scalarOf inner
                | IRTScalar e -> Some e
                | _ -> None
            if at.IndexTypes.Length <> 1 then Error $"the set has rank {at.IndexTypes.Length} (v1: rank 1)"
            else
            let ix = at.IndexTypes.[0]
            let compound = ix.IxKind = IxKCompound
            if not (ix.IxKind = IxKPlain || compound) || ix.Symmetry <> SymNone || ix.Rank <> 1 then
                Error "the set's index is neither a plain dense axis nor a rank-1 compound (v1)"
            else
            match scalarOf at.ElemType with
            | Some ETInt64 -> Ok compound
            | Some e ->
                let shown =
                    match e with
                    | ETInt32 -> "Int32" | ETFloat32 -> "Float32" | ETFloat64 -> "Float64"
                    | ETComplex64 -> "Complex64" | ETComplex128 -> "Complex128" | ETBool -> "Bool"
                    | ETString -> "String" | ETUnit -> "Unit" | ETInt64 -> "Int64"
                Error $"the set's elements are {shown} (v1 hoists Int64 keys; Int32 and float keys -- a canonical -0.0, a never-matching NaN -- are v1.1)"
            | None -> Error "the set's elements are not scalars (v1 hoists Int64 keys)"
        | _ -> Error "the set is not an array"
    // A literal set's (M, min, max), for the record.
    let literalKeys (v: IRExpr) : int64 list option =
        // integer literals and their wrapping + - * (formalism 2.4: integers
        // wrap -- `-9223372036854775807 - 1` is Int64's minimum)
        let rec lit (e: IRExpr) =
            match e with
            | IRLit (IRLitInt n) -> Some n
            | IRUnaryOp (IRNeg, x) -> lit x |> Option.map (fun n -> -n)
            | IRBinOp (_, IRAdd, l, r, _) -> Option.map2 (+) (lit l) (lit r)
            | IRBinOp (_, IRSub, l, r, _) -> Option.map2 (-) (lit l) (lit r)
            | IRBinOp (_, IRMul, l, r, _) -> Option.map2 (*) (lit l) (lit r)
            | _ -> None
        match v with
        | IRArrayLit (elems, _) ->
            let ks = elems |> List.map lit
            if ks |> List.forall Option.isSome then Some (ks |> List.map Option.get) else None
        | _ -> None
    let repLine (setB: IRBinding) =
        let ruleText = "linear when M <= 16, bitmap when the span max - min + 1 <= max(64 M, 4096) and <= 2^26 bits, else hash"
        let fallbackText = "the ACTUAL representation can differ: an allocation the build cannot make degrades it (bitmap -> hash -> linear, the same booleans), and the build reports actual vs preferred and why (stderr under BLADE_MEMBER_VERBOSE=1, both lanes; the C++ run record's member_hoist)"
        match literalKeys setB.Value with
        | Some ks ->
            let m = int64 ks.Length
            let rep =
                if ks.IsEmpty then 1
                else memberChooseRep m (List.min ks) (List.max ks) forced
            let span =
                if ks.IsEmpty then "no keys"
                else
                    let s = uint64 (List.max ks) - uint64 (List.min ks)
                    if s = System.UInt64.MaxValue then "span 2^64" else $"span {s + 1UL}"
            let how =
                if forced = 0 then $"the chooser over the literal set: {ruleText}"
                elif forced = rep then $"forced by BLADE_MEMBER_HOIST={memberRepName forced}"
                else $"BLADE_MEMBER_HOIST={memberRepName forced} forced, outside its domain (the span exceeds the bitmap's 2^26-bit cap): built as {memberRepName rep}"
            $"preferred representation: {memberRepName rep} ({span}, M {m}) -- {how}; {fallbackText}"
        | None ->
            if forced = 0 then $"preferred representation: chosen at run time from (M, min, max) only, never from timing -- {ruleText}; {fallbackText}"
            else $"preferred representation: forced {memberRepName forced} (BLADE_MEMBER_HOIST={memberRepName forced}; a forced bitmap whose span exceeds its 2^26-bit cap is built as a hash at run time); {fallbackText}"
    // What a host EXECUTES at its own site: its value -- or, for a
    // `compound(dense, mask)` binding (whose value is a placeholder), the two
    // operands its site runs, so the canonical semijoin
    // `compound(A, mask(A, lambda(x) -> contains(B, x)))` with its mask left
    // inline is a host too. Other side-table bindings (sparse values, random
    // fills) host nothing in v1.
    let hostExprs (b: IRBinding) : IRExpr list =
        if modul.CompoundInits.ContainsKey b.Id then bindingSideOperands modul b.Id
        elif isPlaceholder b then []
        else [ b.Value ]
    let distinctKernels (ks: (IRCallable * string) list) = ks |> List.distinctBy (fun (c, _) -> c.Id)
    for hi in 0 .. arr.Length - 1 do
        let host = arr.[hi]
        let hostSite = hostExprs host
        let allKernels = hostSite |> List.collect kernelsOf |> distinctKernels
        let kernels = allKernels |> List.filter (fun (c, _) -> not (probesIn c).IsEmpty)
        if not kernels.IsEmpty then
            // Host-level refusals, shared by every set probed in the host.
            let hostWhy : string option =
                if hostSite |> List.exists isDeferredForm then
                    Some $"the host `{host.Name}` is a deferred computation (its cells run at each consumer, not at its own site)"
                else
                match kernels |> List.tryFind (fun (c, _) -> kernelRefs c <> 1) with
                | Some (c, _) -> Some $"the kernel `{c.SourceName}` is named {kernelRefs c} times in the program (v1: a kernel that runs only inside its one host)"
                | None ->
                match kernels |> List.tryFind (fun (c, _) -> c.IsCudaKernel || c.IsMpiParallel) with
                | Some (c, _) ->
                    let kind = if c.IsCudaKernel then "CUDA" else "MPI"
                    Some $"the kernel `{c.SourceName}` is a {kind} kernel (its body runs on a device / another rank)"
                | None ->
                if modul.ProviderWrites.ContainsKey host.Id then Some $"the host `{host.Name}` is a provider write (an external effect)"
                else None
            // Each probe's set, in first-probe order.
            let probes =
                [ for (c, _) in kernels do
                    for (a, _) in probesIn c do
                        yield (c, a) ]
            let setIds =
                probes |> List.choose (fun (_, a) -> match a with IRVar (id, _) -> Some id | _ -> None) |> List.distinct
            let unnamed =
                probes |> List.filter (fun (_, a) -> match a with IRVar _ -> false | _ -> true)
            if not unnamed.IsEmpty then
                let (c, _) = unnamed.Head
                decide host.Name (Blade.Effects.Declined $"a probe in `{c.SourceName}` scans a set expression, not a named stored set (its scan stays as written)")
                    [ $"{unnamed.Length} probe site(s) over an unnamed set" ]
            for sid in setIds do
                let probingKernels =
                    probes |> List.filter (fun (_, a) -> match a with IRVar (id, _) -> id = sid | _ -> false)
                           |> List.map fst |> List.distinctBy (fun c -> c.Id)
                let sites = probes |> List.filter (fun (_, a) -> match a with IRVar (id, _) -> id = sid | _ -> false) |> List.length
                let kernelNames = probingKernels |> List.map (fun c -> $"`{c.SourceName}`") |> String.concat ", "
                let setLabel =
                    match Map.tryFind sid indexOf with
                    | Some si -> arr.[si].Name
                    | None -> $"v{sid}"
                let setWhy : Result<IRBinding * bool * IRBinding list * string, string> =
                    match Map.tryFind sid indexOf with
                    | None -> Error $"the set `{setLabel}` is not a top-level binding (a parameter, capture or local: v1 hoists a module set)"
                    | Some si when si >= hi -> Error $"the set `{setLabel}` is bound after the host"
                    | Some si ->
                        let sb = arr.[si]
                        match keyClass sb.Type with
                        | Error why -> Error why
                        | Ok compound ->
                            match storageOf Set.empty sb with
                            | Error why ->
                                Error $"the set `{sb.Name}` is not proved stored at the build point: {why} (v1 hoists a STORED set; a proved-pure producer forced once at the host is v1.1)"
                            | Ok (how, chain) ->
                                match chain |> List.tryPick (fun b ->
                                        if modul.MutableArrayLets.Contains b.Id then Some (b, $"`{b.Name}` is `let mut`")
                                        elif assignedAnywhere b.Id then Some (b, $"`{b.Name}` is assigned somewhere in the program")
                                        elif modul.ProviderWrites.ContainsKey b.Id then Some (b, $"`{b.Name}` is a provider write")
                                        else None) with
                                | Some (b, why) when b.Id = sb.Id -> Error $"the set {why}"
                                | Some (_, why) -> Error $"the set `{sb.Name}` may change: {why} (its alias chain)"
                                | None -> Ok (sb, compound, chain, how)
                match hostWhy, setWhy with
                | Some why, _ | None, Error why ->
                    decide host.Name (Blade.Effects.Declined why)
                        [ $"set `{setLabel}`: {sites} probe site(s) in {kernelNames} keep the as-written scan" ]
                | None, Ok (sb, compound, chain, how) ->
                    // ---- membershipAdmits: the five parties ----
                    let cellsWord = if compound then "compact buffer" else "cells"
                    let setShape = if compound then "a rank-1 compound's compact buffer" else "rank-1 dense"
                    let producerParty =
                        // every binding of the alias chain is the producer:
                        // the root makes the cells, each alias names them
                        match chain |> List.tryFind (fun b -> bindingPayload modul b |> List.exists wrote) with
                        | Some b ->
                            Error $"the set producer `{b.Name}` may write (its initializer or side-table payload, through the shared write analysis)"
                        | None -> Ok $"set producer `{sb.Name}`: stored at its own site -- {how} -- (not moved; the build reads its {cellsWord} once), no write"
                    let consumerParty =
                        match probingKernels |> List.tryFind (fun c -> wrote c.Body) with
                        | Some c -> Error $"the consumer kernel `{c.SourceName}` may write"
                        | None -> Ok $"consumer kernel(s) {kernelNames}: no write"
                    let folds = hostSite |> List.collect foldsOf
                    let foldParty =
                        let kernelWrites (k: IRExpr) =
                            match resolveCallableIn funcs (fst (peelReynolds k)) with
                            | Some kc -> wrote kc.Body
                            | None -> wrote k
                        match folds |> List.tryFind (fun (k, _) -> kernelWrites k) with
                        | Some _ -> Error $"an outer fold kernel of `{host.Name}` may write"
                        | None ->
                        match folds |> List.tryFind (fun (_, s) -> match s with Some e -> wrote e | None -> false) with
                        | Some _ -> Error $"a fold seed of `{host.Name}` may write"
                        | None ->
                            if folds.IsEmpty then Ok "outer fold kernel(s) / seed(s): none (no fold in the host)"
                            else
                                let seeds = folds |> List.filter (fun (_, s) -> s.IsSome) |> List.length
                                Ok $"outer fold kernel(s): {folds.Length}, no write; seed(s): {seeds}, no write (both stay at their own sites, after the build)"
                    let hostParty =
                        if bindingPayload modul host |> List.exists lifetimeBarrier then
                            Error $"the host `{host.Name}` contains an inline lifetime boundary"
                        elif bindingPayload modul host |> List.exists wrote then
                            Error $"the host `{host.Name}` may write (its source, a kernel, a seed or a statement of its payload -- all of which run after the build)"
                        else Ok $"host `{host.Name}` (its source, kernels, seeds and every statement of its payload, all evaluated after the build): no write"
                    let betweenParty = Ok $"between the build and `{host.Name}`: no binding (the build is `{host.Name}`'s first statement)"
                    let parallelParty =
                        // every parallel body of the host (a probing kernel,
                        // or a `where omp` fold the probes run inside)
                        let par = allKernels |> List.filter (fun (c, _) -> c.IsOmpParallel) |> List.map fst
                        if par.IsEmpty then Ok "parallel: none (a serial host)"
                        else
                            let table = cbTable.Force()
                            let cbParams = par |> List.collect (fun c -> c.Params |> List.filter (fun p -> isFunctionValuedType p.Type) |> List.map (fun p -> (c, p)))
                            match cbParams |> List.tryPick (fun (c, p) ->
                                    match Map.tryFind p.VarId table with
                                    | None -> Some $"the callback parameter `{p.Name}` of the parallel kernel `{c.SourceName}` has an unknown candidate set"
                                    | Some cands ->
                                        cands |> List.tryPick (fun cand ->
                                            match resolveCallableIn (callableById) cand with
                                            | Some cc when not (wrote cc.Body) -> None
                                            | _ -> Some $"a callback candidate of `{p.Name}` in the parallel kernel `{c.SourceName}` may write")) with
                            | Some why -> Error why
                            | None ->
                                let names = par |> List.map (fun c -> $"`{c.SourceName}`") |> String.concat ", "
                                let cbs = if cbParams.IsEmpty then "no function-valued parameter" else $"{cbParams.Length} callback parameter(s), every callbackTable candidate write-free"
                                Ok $"parallel: {names} `where omp` -- the build runs serially before the team and every thread only reads it; {cbs}"
                    let parties = [ producerParty; consumerParty; foldParty; hostParty; betweenParty; parallelParty ]
                    let matrix =
                        "membershipAdmits (five parties, each through Optimize.scheduleMayWriteIn): "
                        + (parties |> List.map (function Ok s -> s | Error s -> "DECLINES: " + s) |> String.concat "; ")
                    let setLine =
                        $"set `{sb.Name}` ({setShape}, Int64 keys): ONE search structure built immediately before `{host.Name}` and freed after it; the {sites} probe site(s) in {kernelNames} read it"
                    let targetLine = "target: evaluated exactly once per probe, before any cell is read (the one evaluation contract of every lane; an empty set still evaluates it)"
                    let sameLine = "identical booleans in every representation (integer ==; build-side duplicates collapse; the probe side keeps its order, multiplicity and short circuits); the build is serial, total, and writes nothing the program reads"
                    match parties |> List.tryPick (function Error s -> Some s | Ok _ -> None) with
                    | Some why ->
                        decide host.Name (Blade.Effects.Declined why) [ setLine; matrix ]
                    | None ->
                        let evidence = [ setLine; repLine sb; targetLine; matrix; sameLine ]
                        if not enabled then
                            decide host.Name (Blade.Effects.Declined "disabled by BLADE_MEMBER_HOIST") evidence
                        else
                            MemberHoistTable.record
                                { HostId = host.Id; HostName = host.Name; SetId = sb.Id; SetName = sb.Name
                                  Kernels = probingKernels |> List.map (fun c -> c.Id) |> Set.ofList
                                  Forced = forced; Admission = matrix }
                            decide host.Name Blade.Effects.Applied evidence

// --- Slice-write fusion: destination passing for a rank-1 row write --------
//
// The recursive-array desugar (TypeCheckInfer.inferRecArray) writes every
// row slice through a materialized temporary:
//
//   let s = SLICE in                               -- e.g. step(prefix(n - 1))
//   for k in 0..E { BUF[i.., k] <- s(k) }          -- the row copy
//
// so a step function returning a fresh row costs that row's allocation, a
// full write of it, the copy's read, and the row write -- and every stage
// temporary inside `step` (one per `let`, one per pointwise callee) besides.
// When SLICE is POINTWISE -- cell k of its value depends only on cell k of
// the arrays it reads -- the copy loop can compute each cell where it is
// stored: the slice's whole elementwise DAG, with pointwise callees
// (`step`, `rhs`) inlined, becomes a chain of scalar lets per cell, each
// let of the source computed ONCE per cell (k1 feeding both the k2 stage
// and the final combination is one scalar local, never a recomputation), and
// the last one is stored straight into the destination row. No temporary
// row, no copy, no stage pools: one pass per step, reading the prefix rows
// and writing the row being built (bench/notes/phase2-microkernel-plan.md
// 4.5b(ii) destination passing + 4.5c pointwise-body fusion).
//
// Why it is cost-only (charter rule 1):
//   - every cell's arithmetic is the source's, operation for operation, in
//     the source's association; only WHERE intermediates live changes (a
//     register instead of a pool), so a build with contraction off is bitwise
//     the unfused one (`BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off`, what every
//     differential pins). Under the default fast contraction the compiler
//     sees across former statement boundaries, exactly as the elementwise
//     chain fusion this extends already lets it;
//   - order of reads vs writes: unfused, all of SLICE is read before any cell
//     of the row is written; fused, cell k is read after cells 0..k-1 of the
//     destination were written. Every read of the expansion is at cell k of
//     its array, and an array that may alias the buffer is admitted only as a
//     ROW VIEW of it (`let r = BUF(i)`, whose cell k is BUF(i, k)), so the
//     cells written so far (j < k of the destination row) are never read:
//     each read sees the value it saw unfused;
//   - nothing in the region may abort (tileMayAbort over every kernel), and
//     every operand's extent is the loop's own static extent, so no
//     co-iteration extent check (BL8011) is dropped and none could fire;
//   - kernels are plain, pure, capture-free scalar kernels with no calls;
//     callees are non-static, non-recursive, capture-free functions of array
//     parameters whose bodies are themselves pointwise.
// Anything else declines and the copy stays exactly as the desugar wrote it.
// Gate: BLADE_FUSION (the same escape hatch as the chain fusion it extends;
// `blade test opt-diff` compares the two).
let fuseSliceWritesModule (modul: IRModule) (builder: IRBuilder) (programFuncs: IRCallable list) : IRModule =
    if not (Blade.Toolchain.envFlag "BLADE_FUSION" true) then modul else
    let callables = System.Collections.Generic.Dictionary<IRId, IRCallable>()
    for f in programFuncs do callables.[f.Id] <- f
    for f in modul.Functions do callables.[f.Id] <- f
    // Callees are inlined only from THIS module: their kernels are spliced
    // by body, but a body from another module may name that module's
    // members, which this module's emission does not see under that id.
    let localFuncs = modul.Functions |> List.map (fun f -> f.Id) |> Set.ofList
    // Wave 3 (E-G5): the write predicate for the step-threading admission --
    // the slice and the whole time-loop body (producer and the bindings in
    // between), the G41 rule.
    let writes = scheduleMayWriteIn programFuncs modul
    // The kernel shape slice-write fusion admits MINUS the omp request: a
    // `where omp` on the slice's ROOT map (the step's final map) is the
    // step-threading request, kept by a RecStepTable entry and honoured by
    // the C++ lane's time-loop emitter. Nested stage maps keep declining.
    let recStepKernelShape (k: IRCallable) (arity: int) =
        not k.IsCommutative && k.CommGroups.IsEmpty && k.AntisymGroups.IsEmpty
        && not k.IsCudaKernel && not k.IsMpiParallel && not k.IsArityPoly && not k.IsStatic
        && k.Params.Length = arity
        && k.IsOmpParallel && not k.Parallelism.IsEmpty
        // `omp(v: 1)`: depth exactly one over a rank-1 row names its one axis.
        && k.Parallelism |> List.forall (fun (idx, dims) -> dims = 1 && idx >= 0 && idx < arity)
    let recStepKernel (k: IRCallable) (arity: int) = recStepOmpEnabled () && recStepKernelShape k arity
    let kernelName (k: IRCallable) = if System.String.IsNullOrEmpty k.SourceName then k.Name else k.SourceName
    let staticExtent (at: IRArrayType) : int64 option =
        match at.IndexTypes with
        | [ ix ] when ix.IxKind = IxKPlain && ix.Symmetry = SymNone && ix.Rank = 1 ->
            (match ix.Extent with IRLit (IRLitInt n) -> Some n | _ -> None)
        | _ -> None
    let scalarElem (t: IRType) = match t with IRTScalar _ -> true | _ -> false
    let rowOfExtent (ext: int64) (t: IRType) =
        match t with
        | ArrayElem at -> staticExtent at = Some ext && scalarElem at.ElemType && not at.IsVirtual
        | _ -> false
    let mentions (vid: IRId) (e: IRExpr) =
        let mutable hit = false
        iterIRExpr (fun n -> match n with IRVar (id, _) when id = vid -> hit <- true | _ -> ()) e
        hit
    // A call, or a binder other than `let` (match patterns, loop variables):
    // only let binders are freshened per splice, and one kernel can be
    // spliced several times into one body.
    let hasCall (e: IRExpr) =
        let mutable hit = false
        iterIRExpr (fun n -> match n with IRApp _ | IRMatch _ | IRForRange _ -> hit <- true | _ -> ()) e
        hit
    // A spliced kernel body's own let binders get fresh ids (the same kernel
    // is spliced once per inlined call site: `rhs` four times per step).
    let freshenLets (body: IRExpr) : IRExpr =
        let binders = System.Collections.Generic.List<IRId>()
        iterIRExpr (fun e -> match e with IRLet (id, _, _) -> binders.Add id | _ -> ()) body
        if binders.Count = 0 then body else
        let mapped = binders |> Seq.distinct |> Seq.map (fun id -> id, builder.FreshId()) |> Map.ofSeq
        mapIRExpr (fun e ->
            match e with
            | IRVar (id, t) -> (match Map.tryFind id mapped with Some n -> IRVar (n, t) | None -> e)
            | IRLet (id, v, b) -> (match Map.tryFind id mapped with Some n -> IRLet (n, v, b) | None -> e)
            | _ -> e) body
    let substParams (m: Map<IRId, IRExpr>) (body: IRExpr) =
        mapIRExpr (fun e ->
            match e with
            | IRVar (id, _) -> (match Map.tryFind id m with Some a -> a | None -> e)
            | _ -> e) body
    // The cell expander. `outsideOk x` says whether reading array variable x
    // at cell k is admissible (not the buffer, or a row view of it). Emits
    // scalar lets into `prelude` (evaluation order) and returns the cell's
    // value as an atom (a variable or a literal).
    // The requesting ROOT kernels the knob kept out of the fusion (recorded
    // per body as `rec-step-parallel` declines).
    let knobDeclines = System.Collections.Generic.List<string>()
    let expand (ext: int64) (kVar: IRExpr) (outsideOk: IRId -> bool)
               (prelude: System.Collections.Generic.List<IRId * IRExpr>) (root: IRExpr) : Result<IRExpr * IRCallable option * bool, string> =
        // The FIRST reason a node is not a cell expression is the decline.
        let mutable why : string option = None
        // The slice's root map when it carried the step-threading request.
        let mutable ompRoot : IRCallable option = None
        // A spliced kernel or callee was `where repro` (its demand does not
        // survive the splice -- the pre-existing fusion behaviour; the
        // step-threading admission declines on it).
        let mutable sawRepro = false
        let fail (msg: string) : IRExpr option =
            (if why.IsNone then why <- Some msg)
            None
        let bind (v: IRExpr) (t: IRType) =
            match v with
            | IRVar _ | IRLit _ -> v
            | _ ->
                let id = builder.FreshId()
                prelude.Add (id, v)
                IRVar (id, t)
        // Outside reads are bound once per cell and shared.
        let reads = System.Collections.Generic.Dictionary<IRId, IRExpr>()
        // `atRoot`: this node's value IS the slice (the slice itself, a let's
        // body, an inlined callee's body) -- the one position whose map may
        // carry the step-threading request; an operand never is.
        let rec go (env: Map<IRId, IRExpr>) (visiting: Set<IRId>) (atRoot: bool) (e: IRExpr) : IRExpr option =
            match e with
            | IRVar (id, t) ->
                (match Map.tryFind id env with
                 | Some atom -> Some atom
                 | None ->
                    match reads.TryGetValue id with
                    | true, atom -> Some atom
                    | _ ->
                        match t with
                        | ArrayElem at when rowOfExtent ext t && outsideOk id ->
                            let atom = bind (IRIndex (e, [ kVar ], None)) at.ElemType
                            reads.[id] <- atom
                            Some atom
                        | ArrayElem _ when rowOfExtent ext t ->
                            fail "the slice reads an array that may alias the buffer other than as a row view"
                        | _ -> fail "the slice reads a value that is not a dense row of the loop's static extent")
            | IRCompute ((IRApplyCombinator _ | IRCompute _) as inner) -> go env visiting atRoot inner
            | IRCompute _ ->
                // Forcing a deferred VALUE (`dn |> compute`) materializes it
                // per step; reading it cell by cell instead would make the
                // emitters materialize the binding itself -- at module level a
                // new printed value (corpus memfree/007).
                fail "the slice forces a deferred value"
            | IRLet (id, v, body) ->
                (match go env visiting false v with
                 | Some atom -> go (Map.add id atom env) visiting atRoot body
                 | None -> None)
            | IRApplyCombinator info when IRMono.tilePlainInfo info && rowOfExtent ext info.OutputType ->
                (match IRMono.tileKernelOf callables info with
                 | Some k when (IRMono.tilePlainKernel k info.Arrays.Length || (atRoot && recStepKernel k info.Arrays.Length))
                               && k.Captures.IsEmpty && not (hasCall k.Body)
                               && IRMono.tilePureBody callables Set.empty k.Body
                               && not (IRMono.tileMayAbort callables Set.empty k.Body)
                               && scalarElem k.RetType
                               && info.ArrayTypes |> List.forall (fun at -> staticExtent at = Some ext && scalarElem at.ElemType) ->
                    if not (IRMono.tilePlainKernel k info.Arrays.Length) then ompRoot <- Some k
                    if k.IsRepro then sawRepro <- true
                    let atoms = info.Arrays |> List.map (go env visiting false)
                    if atoms |> List.exists Option.isNone then None else
                    let m = List.zip (k.Params |> List.map (fun p -> p.VarId)) (atoms |> List.map Option.get) |> Map.ofList
                    Some (bind (substParams m (freshenLets k.Body)) k.RetType)
                 | Some k when atRoot && not (recStepOmpEnabled ()) && recStepKernelShape k info.Arrays.Length ->
                    // The knob keeps the request out of the fusion: the map
                    // runs as its own per-step team and the row is copied,
                    // the pre-wave-3 emission (the A/B's knob-off arm).
                    knobDeclines.Add (kernelName k)
                    fail "an elementwise kernel is not a plain pure scalar kernel over rows of the loop's extent"
                 | Some k when not k.Captures.IsEmpty ->
                    fail "an elementwise kernel captures a value computed outside the cell (a fold or another whole-row quantity), so the slice is not pointwise"
                 | Some k when hasCall k.Body -> fail "an elementwise kernel calls a function or binds names by match or loop"
                 | Some k when IRMono.tileMayAbort callables Set.empty k.Body ->
                    fail "an elementwise kernel may abort (fusion never moves an evaluation that may abort)"
                 | Some _ -> fail "an elementwise kernel is not a plain pure scalar kernel over rows of the loop's extent"
                 | None -> fail "an elementwise kernel is not a resolvable callable")
            | IRApp (IRVar (fid, _), args, retT) when localFuncs.Contains fid && not (visiting.Contains fid) && rowOfExtent ext retT ->
                (match callables.TryGetValue fid with
                 | true, f when not f.IsStatic && f.Captures.IsEmpty && not f.IsArityPoly
                                && not f.IsOmpParallel && not f.IsCudaKernel && not f.IsMpiParallel
                                && f.Params.Length = args.Length
                                && f.Params |> List.forall (fun p -> rowOfExtent ext p.Type) ->
                    let atoms = args |> List.map (go env visiting false)
                    if atoms |> List.exists Option.isNone then None else
                    if f.IsRepro then sawRepro <- true
                    // The callee sees only its parameters (lexical scope).
                    let env' = List.zip (f.Params |> List.map (fun p -> p.VarId)) (atoms |> List.map Option.get) |> Map.ofList
                    go env' (visiting.Add fid) atRoot f.Body
                 | _ -> fail "a callee is not a plain function of dense row parameters (static, capturing, arity-polymorphic, or parallel)")
            | IRApp _ -> fail "the slice calls a function that is recursive, defined in another module, or not row-valued"
            | IRApplyCombinator _ -> fail "an operation over the row is not a plain elementwise map of the loop's extent (a fold, a structured index, or a different shape)"
            | IRIf _ | IRMatch _ -> fail "the slice selects between whole rows (a guarded read or a branch), which is no cell expression"
            | _ -> fail "the slice is not pointwise (an operation other than elementwise maps, lets and pointwise calls)"
        match go Map.empty Set.empty true root with
        | Some atom -> Ok (atom, ompRoot, sawRepro)
        | None -> Error (defaultArg why "the slice is not pointwise")
    // The copy loop's shape: `for k in 0..E { let _ = BUF[i.., k] <- s(k) in () }`.
    let rowCopy (sId: IRId) (loop: IRExpr) =
        match loop with
        | IRForRange (kId, IRLit (IRLitInt 0L), IRLit (IRLitInt ext),
                      IRLet (aId, IRAssign (IRIndex (IRVar (bufId, bufT), idxs, ident), IRIndex (IRVar (s, _), [ IRVar (k1, kT) ], _)), tail))
                when s = sId && k1 = kId && not idxs.IsEmpty
                     && (match List.last idxs with IRVar (k2, _) -> k2 = kId | _ -> false)
                     && not (idxs |> List.take (idxs.Length - 1) |> List.exists (fun i -> mentions kId i || mentions sId i))
                     && not (mentions sId tail) ->
            Some (kId, ext, aId, bufId, bufT, idxs, ident, IRVar (k1, kT), tail)
        | _ -> None
    let mutable fired = 0
    let declines = System.Collections.Generic.List<string>()
    // A DEFERRED value (a loop object or an unforced combinator) is
    // materialized by its consumers; a cell read would make the emitters
    // materialize the binding itself instead -- for a module binding, a new
    // printed value. Such reads are never admitted.
    let rec isDeferredValue (v: IRExpr) =
        match v with
        | IRLet (_, _, b) -> isDeferredValue b
        | IRApplyCombinator _ | IRFunctorMap _ | IRComposeApply _ | IRMethodFor _ | IRObjectFor _
        | IRFusion _ | IRParallel _ | IRBind _ | IRCompose _ | IRComposeObj _ | IRComposeMeth _
        | IRPure _ | IRChoice _ | IRFallback _ -> true
        | _ -> false
    let moduleValues = modul.Bindings |> List.map (fun b -> b.Id, b.Value) |> Map.ofList
    let rec moduleReadable (seen: Set<IRId>) (id: IRId) =
        match Map.tryFind id moduleValues with
        | Some (IRVar (y, _)) when not (seen.Contains y) && moduleValues.ContainsKey y -> moduleReadable (seen.Add id) y
        | Some v -> not (isDeferredValue v)
        | None -> false
    let moduleIds = modul.Bindings |> List.map (fun b -> b.Id) |> List.filter (moduleReadable Set.empty) |> Set.ofList
    let mutable outer = moduleIds
    let mutable subjectName = ""
    // Walk a body keeping the values of enclosing lets (to judge aliases of
    // the buffer). Only let-chains are walked into; the rewrite is local to
    // a `let s = SLICE in let w = <copy loop> in rest` triple. `enclosing`
    // is the innermost counted loop around the triple -- (variable, literal
    // start, body) -- the time loop a threaded step runs under.
    let rec rw (enclosing: (IRId * int64 * IRExpr) option) (scope: Map<IRId, IRExpr>) (e: IRExpr) : IRExpr =
        match e with
        | IRLet (sId, slice, IRLet (wId, loop, rest)) when (rowCopy sId loop).IsSome && not (mentions sId rest) ->
            let kId, ext, aId, bufId, bufT, idxs, ident, kVar, tail = (rowCopy sId loop).Value
            // The row views of the buffer the slice reads (their indices),
            // for the step-threading proof below.
            let rowReads = System.Collections.Generic.List<IRExpr list>()
            // Every id in scope whose value may hold the buffer (mentions it,
            // or mentions such an id): a read through one is a buffer read of
            // unknown row, never an "outside" value.
            let aliases =
                let mutable s = Set.empty
                let mutable grow = true
                while grow do
                    grow <- false
                    for KeyValue (x, v) in scope do
                        if not (s.Contains x) then
                            let mutable hit = false
                            iterIRExpr (fun n -> match n with IRVar (id, _) when id = bufId || s.Contains id -> hit <- true | _ -> ()) v
                            if hit then
                                s <- s.Add x
                                grow <- true
                s
            let mentionsAlias (v: IRExpr) =
                let mutable hit = false
                iterIRExpr (fun n -> match n with IRVar (id, _) when aliases.Contains id -> hit <- true | _ -> ()) v
                hit
            let rec outsideOk (x: IRId) =
                if x = bufId then false
                else
                    match Map.tryFind x scope with
                    // A parameter of the enclosing function or a module
                    // binding cannot alias a buffer local to this body; an
                    // id bound by any other binder (a match pattern, a
                    // destructuring) is unknown and declines.
                    | None -> outer.Contains x
                    // An alias `let x = y` is judged as y (a lane may bind it
                    // without a copy).
                    | Some (IRVar (y, _)) -> y <> x && outsideOk y
                    | Some v when not (mentions bufId v) && not (mentionsAlias v) -> not (isDeferredValue v)
                    | Some (IRIndex (IRVar (b, _), rowIdx, _)) when b = bufId ->
                        // A row view BUF(i): cell k is BUF(i, k).
                        (match bufT with
                         | ArrayElem at when rowIdx.Length = at.IndexTypes.Length - 1 ->
                            rowReads.Add rowIdx
                            true
                         | _ -> false)
                    | Some _ -> false
            let prelude = System.Collections.Generic.List<IRId * IRExpr>()
            // Wave 3 (E-G5): the step-threading proof for a slice whose root
            // map carried the request (Codex review, wave3/codex-review-E-G5
            // objections 1-3, 5-8). Ok = a RecStepTable entry; Error = the
            // reason, and the slice stays UNFUSED (the request is then
            // honoured by the map's own per-step team, never dropped).
            let proveStep (k: IRCallable) (sawRepro: bool) : Result<unit, string> =
                match enclosing with
                | None -> Error "the row write is not inside a counted time loop with a literal start"
                | Some (nId, lo, loopBody) ->
                    let isN (e: IRExpr) = match e with IRVar (v, _) -> v = nId | _ -> false
                    // A row the region may read without synchronization: an
                    // earlier row this worker wrote (`n - c`, 1 <= c <= lo,
                    // so never before row 0), or a seed row written before
                    // the loop (0 <= r < lo).
                    let earlierRow (r: IRExpr) =
                        match r with
                        | IRBinOp (_, IRSub, IRVar (v, _), IRLit (IRLitInt c), _) -> v = nId && c >= 1L && c <= lo
                        | IRLit (IRLitInt r) -> r >= 0L && r < lo
                        | _ -> false
                    // Every mention of the buffer in the time loop's body: a
                    // store to the CURRENT row, or a read of an admissible
                    // earlier row (a row view, or a cell of one).
                    let bodyUseOk =
                        let mutable ok = true
                        let rec walk (x: IRExpr) =
                            match x with
                            | IRAssign (IRIndex (IRVar (b, _), i0 :: rest, _), v) when b = bufId ->
                                if not (isN i0) then ok <- false
                                rest |> List.iter walk
                                walk v
                            | IRIndex (IRVar (b, _), i0 :: rest, _) when b = bufId ->
                                if not (earlierRow i0) then ok <- false
                                rest |> List.iter walk
                            | IRVar (b, _) when b = bufId -> ok <- false
                            | ExprShape (children, _) -> children |> List.iter walk
                        walk loopBody
                        ok
                    let fresh =
                        match Map.tryFind bufId scope with
                        | Some (IRUninit _ | IRCompute _ | IRArrayLit _) -> true
                        | _ -> false
                    if not (match idxs with [ i0; _ ] -> isN i0 | _ -> false) then
                        Error "the store is not `BUF[n][k]` with n the enclosing time loop's variable (v1: rank-2 trajectories)"
                    elif not fresh then Error "the trajectory is not a fresh buffer of this body (a parameter, a module binding, or an alias)"
                    elif not (rowReads |> Seq.forall (fun r -> match r with [ r0 ] -> earlierRow r0 | _ -> false)) then
                        Error "a trajectory read is not a row this worker wrote earlier (`prefix(n - c)` with a literal 1 <= c <= the loop's start) or a seed row written before the loop"
                    elif not bodyUseOk then
                        Error "the time loop reads or writes the trajectory other than storing its own row and reading earlier rows"
                    elif sawRepro then Error "a spliced kernel or callee is `where repro` (its demand does not survive the splice)"
                    elif writes slice then Error "the step may write storage outside its own row"
                    // The buffer is this body's own fresh storage: judged
                    // with it bound locally (the row-fold pass's `writes
                    // block`), so only writes OUTSIDE it count.
                    elif writes (IRLet (bufId, IRUninit bufT, loopBody)) then
                        Error "a statement of the time loop may write storage outside the trajectory"
                    elif not (RecStepTable.record
                                { CellLoopVar = kId; StepVar = nId; StepLo = lo; BufferId = bufId; Cells = ext
                                  Subject = subjectName; KernelName = kernelName k
                                  Fold = ref None; ChunkBounds = ref None; Decided = ref false }) then
                        Error "the time loop already carries a threaded step (one requesting slice per recursion)"
                    else Ok ()
            let expanded =
                match expand ext kVar outsideOk prelude slice with
                | Ok (atom, Some k, sawRepro) ->
                    (match proveStep k sawRepro with
                     | Ok () ->
                        recordRecStep subjectName Blade.Effects.Applied
                            [ $"the `where omp` request of `{(kernelName k)}` (the step's final map) names the fused cell loop: every worker reads only its own cell of strictly earlier rows, so the C++ lane may run the recursion's steps on one persistent team with fixed x ownership and no per-step synchronization (the codegen record under this rule says which form)" ]
                        Ok atom
                     | Error why ->
                        recordRecStep subjectName (Blade.Effects.Declined why)
                            [ $"the slice is not fused: the `where omp` request of `{(kernelName k)}` keeps its own per-step team and the row is copied" ]
                        Error ("the step's final map asks for omp, which the fused cell loop cannot honour: " + why))
                | Ok (atom, None, _) -> Ok atom
                | Error why -> Error why
            match expanded with
            | Ok atom ->
                fired <- fired + 1
                let store = IRLet (aId, IRAssign (IRIndex (IRVar (bufId, bufT), idxs, ident), atom), tail)
                let body = Seq.foldBack (fun (id, v) acc -> IRLet (id, v, acc)) prelude store
                IRLet (wId, IRForRange (kId, IRLit (IRLitInt 0L), IRLit (IRLitInt ext), body), rw enclosing scope rest)
            | Error why ->
                // Only a slice that looks fusable (a call or an elementwise
                // map) is a decision; a literal seed row is just a copy.
                let mutable looksFusable = false
                iterIRExpr (fun n -> match n with IRApp _ | IRApplyCombinator _ -> looksFusable <- true | _ -> ()) slice
                if looksFusable then declines.Add why
                IRLet (sId, rw enclosing scope slice, IRLet (wId, rwInner enclosing scope loop, rw enclosing (Map.add wId loop (Map.add sId slice scope)) rest))
        | IRLet (id, v, body) -> IRLet (id, rw enclosing scope v, rw enclosing (Map.add id v scope) body)
        | _ -> rwInner enclosing scope e
    and rwInner (enclosing: (IRId * int64 * IRExpr) option) (scope: Map<IRId, IRExpr>) (e: IRExpr) : IRExpr =
        match e with
        | IRForRange (k, lo, hi, body) ->
            let inner = match lo with IRLit (IRLitInt l) -> Some (k, l, body) | _ -> None
            IRForRange (k, lo, hi, rw inner scope body)
        | IRLet _ -> rw enclosing scope e
        | ExprShape ([], _) -> e
        | ExprShape (children, rebuild) -> rebuild (children |> List.map (rw enclosing scope))
    // One record per body: applied (with the count) and/or each decline's
    // reason -- a body can hold a fused arm and a declined one.
    let rewriteBody (subject: string) (outerIds: Set<IRId>) (body: IRExpr) =
        let before = fired
        declines.Clear ()
        knobDeclines.Clear ()
        outer <- outerIds
        subjectName <- subject
        let body' = rw None Map.empty body
        let record outcome evidence =
            Blade.Effects.Decisions.record
                { Blade.Effects.Rule = "slice-write-fusion"; Version = 1
                  Span = Blade.Ast.noSpan; Subject = subject
                  Outcome = outcome; Evidence = evidence }
        if fired > before then
            record Blade.Effects.Applied
                [ $"{fired - before} row write(s) computed in place: the slice's pointwise DAG (callees inlined) is one scalar let chain per cell, stored straight into the destination row -- no slice temporary, no copy, no stage pools" ]
        for why in declines do
            record (Blade.Effects.Declined why) [ "the row is materialized and copied as written" ]
        for kname in knobDeclines do
            recordRecStep subject (Blade.Effects.Declined "disabled by BLADE_REC_STEP_OMP")
                [ $"the `where omp` request of `{kname}` (the step's final map) is kept out of the fusion: the map runs as its own per-step team and the row is copied" ]
        body'
    let newFunctions =
        modul.Functions |> List.map (fun f ->
            let outerIds = f.Params |> List.fold (fun s p -> Set.add p.VarId s) moduleIds
            { f with Body = rewriteBody f.SourceName outerIds f.Body })
    let newBindings =
        modul.Bindings |> List.map (fun b -> { b with Value = rewriteBody b.Name moduleIds b.Value })
    { modul with Functions = newFunctions; Bindings = newBindings }

// --- Row-fold fusion: consume each recursive-array row while it is hot ----
//
// A recursion whose only consumer is a row-wise fold,
//
//   let T = (let BUF = <fresh> in SEED; for n in L..N { ..row n.. }; BUF) in
//   reduce(method_for(T) <@> lambda(row) -> K(row), F)       -- seed I
//
// re-reads the whole trajectory after the recursion (recur: 48 MB from DRAM,
// ~3 ms of a ~12 ms kernel), while every row was in cache the moment it was
// written. The fold is moved INTO the recursion: an accumulator seeded with
// I, updated `acc <- F(acc, K(BUF(n)))` right after row n is final -- once
// after the seed statements (row 0) and once at the end of each step.
//
// Why it is cost-only (charter rule 1):
//   - `reduce` is a LEFT fold in ascending order (formalism; CLAUDE.md): the
//     rewrite evaluates exactly F(..F(F(I, K(row 0)), K(row 1)).., K(row N-1))
//     with K's own body on each row -- the serial association every build
//     without a reorder licence already uses (BLADE_FP_REPRO=1: bitwise the
//     unfused program). A licensed build gives up only the lane split of an
//     N-term outer sum; each row's K keeps its own (licensed) form;
//   - a row is FINAL when it is folded: the loop writes only row n (every
//     buffer store's leading index is the step variable), the statements
//     before it write only row 0, nothing follows the loop (no `while`
//     freeze epilogue, no break), and every call in the region is repeatable
//     (no `mut` parameter writes a row after it was folded);
//   - K and F are pure and capture-free, so evaluating them earlier
//     (interleaved with the steps) changes no value. Each is evaluated once
//     per row, unconditionally, as before, so an abort is never added or
//     removed; when K (a row prodsum's extent check) and a LATER step would
//     both abort, which one is reported first can change -- the residual the
//     elementwise fusion already accepts;
//   - T has no other use, and the fold is the let's whole body (or the value
//     of its first let): nothing observes the buffer after the recursion.
// Gate: BLADE_FUSION. Plan rule `row-fold-fusion`.
let fuseRowFoldsModule (modul: IRModule) (builder: IRBuilder) (programFuncs: IRCallable list) : IRModule =
    if not (Blade.Toolchain.envFlag "BLADE_FUSION" true) then modul else
    let writes = scheduleMayWriteIn programFuncs modul
    let callables = System.Collections.Generic.Dictionary<IRId, IRCallable>()
    for f in programFuncs do callables.[f.Id] <- f
    for f in modul.Functions do callables.[f.Id] <- f
    let mentions (vid: IRId) (e: IRExpr) =
        let mutable hit = false
        iterIRExpr (fun n -> match n with IRVar (id, _) when id = vid -> hit <- true | _ -> ()) e
        hit
    let count (vid: IRId) (e: IRExpr) =
        let mutable n = 0
        iterIRExpr (fun x -> match x with IRVar (id, _) when id = vid -> n <- n + 1 | _ -> ()) e
        n
    // A pure, capture-free scalar kernel. It MAY abort (a row `prodsum` or
    // `reduce` carries extent checks): every row is still folded exactly
    // once, unconditionally, so an abort still happens -- only WHICH abort is
    // reported first can change when a later step would also have aborted,
    // the residual elementwise fusion already accepts (IRMono.
    // occursOnlyUnconditionally). Nothing observable can run in between:
    // every call in the region is repeatable (no output, no writes).
    let plainScalarKernel (arity: int) (k: IRCallable) =
        k.Params.Length = arity && k.Captures.IsEmpty && not k.IsStatic
        && not k.IsOmpParallel && not k.IsCudaKernel && not k.IsMpiParallel && not k.IsArityPoly
        && (match k.RetType with IRTScalar _ -> true | _ -> false)
        && IRMono.tilePureBody callables Set.empty k.Body
    let resolve (e: IRExpr) =
        match e with
        | IRVar (kid, _) -> (match callables.TryGetValue kid with | true, k -> Some k | _ -> None)
        | _ -> None
    // Every call in a region is repeatable: no assignment through a `mut`
    // parameter (which could rewrite a row already folded), no output.
    let callsRepeatable (e: IRExpr) =
        let mutable ok = true
        iterIRExpr (fun n ->
            match n with
            | IRApp (IRVar (fid, _), _, _) ->
                (match callables.TryGetValue fid with
                 | true, c ->
                    if c.IsStatic then ok <- false
                    elif Blade.Effects.isRepeatable c.Effects then ()
                    elif not c.Effects.Unknown then ok <- false
                    elif not (IRMono.tilePureBody callables Set.empty c.Body) then ok <- false
                 | _ -> ok <- false)
            | IRApp _ | IRBreakIf _ | IRDisplayEmit _ -> ok <- false
            | _ -> ()) e
        ok
    // Every mention of the buffer in a region is a read through a subscript
    // (a row view or a cell) or a store whose leading index is `rowOk`.
    let bufferUseOk (bufId: IRId) (rowOk: IRExpr -> bool) (e: IRExpr) =
        let mutable ok = true
        let rec walk (x: IRExpr) =
            match x with
            | IRAssign (IRIndex (IRVar (b, _), i0 :: rest, _), v) when b = bufId ->
                if not (rowOk i0) then ok <- false
                rest |> List.iter walk
                walk v
            | IRAssign (IRVar (b, _), _) when b = bufId -> ok <- false
            | IRIndex (IRVar (b, _), idxs, _) when b = bufId -> idxs |> List.iter walk
            | IRVar (b, _) when b = bufId -> ok <- false
            | ExprShape (children, _) -> children |> List.iter walk
        walk e
        ok
    let rec chainOf (e: IRExpr) : (IRId * IRExpr) list * IRExpr =
        match e with
        | IRLet (id, v, b) -> let s, fin = chainOf b in (id, v) :: s, fin
        | _ -> [], e
    let rebuild (stmts: (IRId * IRExpr) list) (fin: IRExpr) =
        List.foldBack (fun (id, v) acc -> IRLet (id, v, acc)) stmts fin
    // The (row view, fold) let ids of the LAST foldUpdate built (B2r's
    // CellFoldTable entry names the step's pair).
    let mutable lastFoldIds : (IRId * IRId * IRId) option = None
    // `acc <- F(acc, K(BUF(row)))` as statements ending in `tail`.
    let foldUpdate (accId: IRId) (accT: IRType) (bufId: IRId) (bufT: IRType)
                   (k: IRCallable) (f: IRCallable) (row: IRExpr) (tail: IRExpr) =
        let rowId = builder.FreshId ()
        let pId = builder.FreshId ()
        let assignId = builder.FreshId ()
        lastFoldIds <- Some (rowId, pId, assignId)
        let kParam = k.Params.Head
        let subst (m: Map<IRId, IRExpr>) (body: IRExpr) =
            mapIRExpr (fun x -> match x with IRVar (id, _) -> (match Map.tryFind id m with Some a -> a | None -> x) | _ -> x) body
        let freshenLets (body: IRExpr) =
            let binders = System.Collections.Generic.List<IRId>()
            iterIRExpr (fun x -> match x with IRLet (id, _, _) -> binders.Add id | _ -> ()) body
            let mapped = binders |> Seq.distinct |> Seq.map (fun id -> id, builder.FreshId ()) |> Map.ofSeq
            mapIRExpr (fun x ->
                match x with
                | IRVar (id, t) -> (match Map.tryFind id mapped with Some n -> IRVar (n, t) | None -> x)
                | IRLet (id, v, b) -> (match Map.tryFind id mapped with Some n -> IRLet (n, v, b) | None -> x)
                | _ -> x) body
        let kBody = subst (Map.ofList [ kParam.VarId, IRVar (rowId, kParam.Type) ]) (freshenLets k.Body)
        let fBody =
            subst (Map.ofList [ f.Params.[0].VarId, IRVar (accId, accT); f.Params.[1].VarId, IRVar (pId, k.RetType) ])
                  (freshenLets f.Body)
        IRLet (rowId, IRIndex (IRVar (bufId, bufT), [ row ], None),
            IRLet (pId, kBody,
                IRLet (assignId, IRAssign (IRVar (accId, accT), fBody), tail)))
    let record (subject: string) outcome evidence =
        Blade.Effects.Decisions.record
            { Blade.Effects.Rule = "row-fold-fusion"; Version = 1
              Span = Blade.Ast.noSpan; Subject = subject
              Outcome = outcome; Evidence = evidence }
    let mutable subject = ""
    // Try the rewrite at `let T = BLOCK in E`. Error = decline reason (only
    // reported when E really is a row fold over T).
    let tryAt (tId: IRId) (block: IRExpr) (e: IRExpr) : Result<IRExpr, string> option =
        // The consumer: E is the fold, or E's first let binds it.
        let consumer, rebuildE =
            match e with
            | IRReduceCompute _ -> Some e, (fun (acc: IRExpr) -> acc)
            | IRLet (x, (IRReduceCompute _ as r), rest) when not (mentions tId rest) ->
                Some r, (fun acc -> IRLet (x, acc, rest))
            | _ -> None, id
        match consumer with
        | Some (IRReduceCompute (IRApplyCombinator info, foldK, init)) when
                (match info.Arrays with [ IRVar (a, _) ] -> a = tId | _ -> false) ->
            if count tId e <> count tId (IRReduceCompute (IRApplyCombinator info, foldK, init)) then Some (Error "the recursion's result has another use beside the fold") else
            match block with
            // The buffer is FRESH: the unfilled pool of a proved recursion,
            // the zero former of a large one, or the nested zero literal the
            // desugar writes for a small one (<= 4096 cells, e.g. every small
            // `while`-guarded recursion, whose fill the freeze needs).
            | IRLet (bufId, (IRUninit _ | IRCompute _ | IRArrayLit _), chain) ->
                let stmts, fin = chainOf chain
                let bufT =
                    match block with
                    | IRLet (_, IRUninit t, _) -> Some t
                    | _ -> (match fin with IRVar (b, t) when b = bufId -> Some t | _ -> None)
                match fin, bufT, List.tryLast stmts with
                | IRVar (b, _), Some bufT, Some (loopId, IRForRange (nId, IRLit (IRLitInt lo), IRLit (IRLitInt hi), body)) when b = bufId ->
                    let pre = stmts |> List.take (stmts.Length - 1)
                    let leading =
                        match bufT with
                        | ArrayElem at ->
                            (match at.IndexTypes with
                             | ix :: _ :: _ -> (match ix.Extent with IRLit (IRLitInt n) -> Some n | _ -> None)
                             | _ -> None)
                        | _ -> None
                    let rowKernel = resolve info.Kernel
                    let foldKernel = resolve foldK
                    let isN (i: IRExpr) = match i with IRVar (v, _) -> v = nId | _ -> false
                    let isZero (i: IRExpr) = match i with IRLit (IRLitInt 0L) -> true | _ -> false
                    let rec unitTail (x: IRExpr) =
                        match x with
                        | IRLet (_, _, b) -> unitTail b
                        | IRLit IRLitUnit -> true
                        | _ -> false
                    if leading <> Some hi then Some (Error "the recursion loop does not end at the buffer's leading extent")
                    elif not (lo = 0L || lo = 1L) then Some (Error "the recursion loop starts past row 1")
                    elif info.KernelOutputRank <> 0 || info.HasReynolds || info.IsCoIteration then
                        Some (Error "the fold's map is not a plain per-row map")
                    elif not (match info.OutputType with ArrayElem at -> at.IndexTypes.Length = 1 | _ -> false) then Some (Error "the fold does not reduce the rows to one scalar") else
                    // The step's row is written by ONE slice-write-fused
                    // cell loop whose last statement stores a scalar local
                    // into BUF[n][k] (B2r's and E-G5's shape).
                    let cellLoop =
                        let stepStmts, _ = chainOf body
                        let writers =
                            stepStmts |> List.filter (fun (_, s) -> mentions bufId s && (match s with IRForRange _ -> true | _ -> false))
                        match writers with
                        | [ (_, IRForRange (kv, IRLit (IRLitInt 0L), IRLit (IRLitInt _), cb)) ] ->
                            let cstmts, cfin = chainOf cb
                            (match List.tryLast cstmts, cfin with
                             | Some (_, IRAssign (IRIndex (IRVar (b, _), [ i0; IRVar (k2, _) ], _), IRVar (sv, _))), IRLit IRLitUnit
                                when b = bufId && isN i0 && k2 = kv
                                     && cstmts |> List.exists (fun (id, _) -> id = sv) ->
                                Ok (kv, sv)
                             | _ -> Error "the step's cell loop does not end in one store of a scalar local into its own row")
                        | [] -> Error "the step's row is not written by a slice-write-fused cell loop"
                        | _ -> Error "the step's row is written by more than one loop"
                    // Wave 3 (E-G5): the step is THREADED (its cell loop has a
                    // RecStepTable entry under this time loop).
                    let stepEntry =
                        match cellLoop with
                        | Ok (kv, _) ->
                            (match RecStepTable.tryLoop kv with
                             | Some entry when entry.StepVar = nId && entry.BufferId = bufId -> Some entry
                             | _ -> None)
                        | Error _ -> None
                    // A kernel demanding reproducible evaluation (`where repro`
                    // itself, or the eta-wrapper of such a function).
                    let reproCallee (c: IRCallable) =
                        c.IsRepro
                        || (match c.Body with
                            | IRApp (IRVar (fid, _), _, _) -> (match callables.TryGetValue fid with | true, g -> g.IsRepro | _ -> false)
                            | _ -> false)
                    // A `where omp` on the OUTER fold kernel F is admitted only
                    // behind a threaded step, where it is the request the
                    // row post-pass reads (G35's gate); otherwise it declines
                    // as before (the fold then keeps its own thread forms). A
                    // `where repro` F is never admitted this way: the splice
                    // would drop its BLADE_REPRO_FN boundary (Codex diff
                    // review, finding 1).
                    let outerKernelOk (f: IRCallable) =
                        plainScalarKernel 2 f
                        || (stepEntry.IsSome && f.IsOmpParallel && not (reproCallee f)
                            && plainScalarKernel 2 { f with IsOmpParallel = false })
                    match rowKernel, foldKernel with
                    | Some k, Some f when plainScalarKernel 1 k && outerKernelOk f ->
                        let preOk = pre |> List.forall (fun (_, s) -> bufferUseOk bufId isZero s && callsRepeatable s)
                        let seedWrites = pre |> List.exists (fun (_, s) -> mentions bufId s)
                        if not (bufferUseOk bufId isN body && callsRepeatable body && unitTail body) then
                            Some (Error "a step writes a row other than its own, calls something that is not repeatable, or may stop early")
                        // G41: the fold moves INTO the recursion, between its
                        // steps -- all five parties through the shared write
                        // predicate (the recursion's own buffer is local to it).
                        elif writes block then Some (Error "the recursion (the producer) may write storage outside its own buffer")
                        elif writes (IRApplyCombinator info) then Some (Error "the row kernel may write")
                        elif writes foldK then Some (Error "the fold kernel may write")
                        elif writes init then Some (Error "the fold's seed may write")
                        elif not preOk then Some (Error "a statement before the recursion writes a row other than the seed row")
                        elif lo = 1L && not seedWrites then Some (Error "no seed statement writes row 0")
                        elif lo = 0L && seedWrites then Some (Error "a statement before a seedless recursion writes the buffer") else
                        let accId = builder.FreshId ()
                        let accT = f.RetType
                        // The step variable as the body uses it (a named
                        // recursion axis types it as that index).
                        let nVar =
                            let mutable found = None
                            iterIRExpr (fun x -> match x with IRVar (v, t) when v = nId && found.IsNone -> found <- Some (IRVar (v, t)) | _ -> ()) body
                            defaultArg found (IRVar (nId, IRTScalar ETInt64))
                        // The step body: its unit tail becomes the fold update.
                        let rec withUpdate (x: IRExpr) =
                            match x with
                            | IRLet (id, v, b) -> IRLet (id, v, withUpdate b)
                            | _ -> foldUpdate accId accT bufId bufT k f nVar x
                        let loop' =
                            match snd (List.last stmts) with
                            | IRForRange (nv, l, h, _) -> IRForRange (nv, l, h, withUpdate body)
                            | other -> other
                        let stepFoldIds = lastFoldIds
                        // Wave 3 (E-G5): a THREADED step carries the fold
                        // facts too: the C++ lane's time-loop emitter folds
                        // each row inside the team (the self-dot's licensed
                        // form per worker chunk) or after it (complete rows,
                        // strict). The B2r entry below is STILL recorded: the
                        // serial fallbacks (threads suppressed, the LLVM lane)
                        // keep the chain interleave exactly as before, and the
                        // threaded emission declines it at its own site.
                        (match stepEntry, stepFoldIds with
                         | Some entry, Some (rowId, pId, assignId) ->
                            entry.Fold.Value <-
                                Some { RowLetId = rowId; FoldLetId = pId; AccId = accId; AssignLetId = assignId
                                       Term = rowKernelCellTerm k; OuterRequest = f.IsOmpParallel
                                       StrictOnly = reproCallee k || reproCallee f }
                            recordRecStep subject Blade.Effects.Applied
                                [ (match rowKernelCellTerm k, f.IsOmpParallel with
                                   | Some _, true -> "the fused row fold rides the team: a licensed build folds each worker's own chunk of the row it just wrote (the self-dot's licensed form), the chunks combined ascending from +0 per row; a strict build folds COMPLETE rows into indexed slots after the team, rows in parallel under the outer fold kernel's own `where omp`; the time chain then runs ascending from its seed"
                                   | Some _, false -> "the fused row fold rides the team: a licensed build folds each worker's own chunk of the row it just wrote (the self-dot's licensed form), the chunks combined ascending from +0 per row; a strict build folds COMPLETE rows serially after the team (no `where omp` on the outer fold kernel licenses a parallel row pass); the time chain then runs ascending from its seed"
                                   | None, _ -> "the fused row fold runs serially after the team: the row kernel on each COMPLETE row, then the time chain ascending from its seed (only the self-dot `prodsum(row, row)` has an in-team form)") ]
                         | _ -> ())
                        // B2r (round-2 cell epilogues): the row kernel is
                        // prodsum(row, row): record the loop so an emitter
                        // may advance the row's chain per cell (the IR is
                        // unchanged).
                        (match rowKernelCellTerm k, stepFoldIds with
                         | Some term, Some (rowId, pId, _) ->
                            match cellLoop with
                            | Error why ->
                                recordCellEpilogue subject (Blade.Effects.Declined why)
                                    [ "the row's prodsum runs on the finished row after the step" ]
                            | Ok _ when not (cellEpilogueEnabled ()) ->
                                recordCellEpilogue subject (Blade.Effects.Declined "disabled by BLADE_CELL_EPILOGUE")
                                    [ "the row's prodsum runs on the finished row after the step" ]
                            | Ok (kv, sv) ->
                                CellFoldTable.record
                                    { CellLoopVar = kv; BufferId = bufId; StepVar = nId; StoredVar = sv
                                      FoldLetId = pId; RowLetId = rowId; Term = term; Subject = subject
                                      EmittedCpp = ref None; EmittedLlvm = ref None }
                         | None, _ ->
                            recordCellEpilogue subject (Blade.Effects.Declined "the row kernel is not a +0-seeded dot-class fold of the row's own cells (v1: `prodsum(row, row)`)")
                                [ "the row's fold runs on the finished row after the step" ]
                         | _ -> ())
                        let preStmts =
                            if lo = 1L then
                                let u = builder.FreshId ()
                                pre @ [ u, foldUpdate accId accT bufId bufT k f (IRLit (IRLitInt 0L)) (IRLit IRLitUnit) ]
                            else pre
                        let block' = IRLet (bufId, (match block with IRLet (_, v, _) -> v | _ -> block),
                                            rebuild (preStmts @ [ loopId, loop' ]) fin)
                        Some (Ok (IRLet (accId, init, IRLet (tId, block', rebuildE (IRVar (accId, accT))))))
                    | _ -> Some (Error "the per-row kernel or the fold operator is not a plain pure capture-free scalar kernel")
                | _ when stmts |> List.exists (fun (_, s) -> match s with IRForRange _ -> true | _ -> false) ->
                    Some (Error "the recursion's statements are not a seed followed by one step loop ending the block (a `while` freeze epilogue, or another statement after the loop)")
                | _ -> None
            | _ -> None
        | _ -> None
    let rec rw (e: IRExpr) : IRExpr =
        match e with
        | IRLet (tId, block, body) ->
            (match tryAt tId block body with
             | Some (Ok e') ->
                record subject Blade.Effects.Applied
                    [ "the row-wise fold over the recursion's result is accumulated inside the recursion: each row folded right after it is written (seed row after the seed), ascending, the fold's own left association; the trajectory is not re-read" ]
                (match e' with
                 | IRLet (a, i, IRLet (t, b, rest)) -> IRLet (a, i, IRLet (t, rwInner b, rw rest))
                 | _ -> e')
             | Some (Error why) ->
                record subject (Blade.Effects.Declined why) [ "the fold re-reads the trajectory after the recursion" ]
                IRLet (tId, rw block, rw body)
             | None -> IRLet (tId, rw block, rw body))
        | _ -> rwInner e
    and rwInner (e: IRExpr) =
        match e with
        | IRLet _ -> rw e
        | ExprShape ([], _) -> e
        | ExprShape (children, rebuild) -> rebuild (children |> List.map rw)
    let newFunctions =
        modul.Functions |> List.map (fun f ->
            subject <- f.SourceName
            { f with Body = rw f.Body })
    let newBindings =
        modul.Bindings |> List.map (fun b ->
            subject <- b.Name
            { b with Value = rw b.Value })
    { modul with Functions = newFunctions; Bindings = newBindings }

// --- Sibling-fold traversal sharing (rule `sibling-folds` v1) -----------------
//
// Two scalar folds over the same dense axis in one straight-line region --
//
//     let (checksum, input_checksum) = {
//         let a = inputs(..); let b = inputs(..)
//         (kernel(a, b), reduce(a, (+)) + reduce(b, (+)))
//     }
//
// -- each walk the arrays once. The language already has the one-traversal
// spelling, the `<&!>` reduction JOIN (`IRReduceCompute` with an `IRTuple` of
// per-leg kernels and an `IRTuple` of per-leg seeds): every leg keeps its OWN
// accumulator, seed and ascending operand order, so sharing the traversal only
// interleaves independent chains. This pass rewrites sibling folds into one
// LET-BOUND join plus one projection let per leg, before the lane split, so
// the C++ lane (genReduceJoinCore), the LLVM lane and the interpreter all
// consume the existing, already-differential join support. No emitter change.
//
// REGION: one named function's body, or one module binding's value -- a
// straight-line `let` spine (lambda bodies are a v1.1 item). CANDIDATES sit in
// UNCONDITIONAL positions of a spine statement (a let value or the final
// expression, descending through tuples, arithmetic and call arguments; never
// a branch, a match arm, the right of `&&`/`||`, or a nested `let`):
//
//   * `reduce(a, k[, s])` over a stored rank-1 array;
//   * `IRReduceCompute` over one plain dense map leaf, or an existing let-bound
//     join / shared-fold tree whose tuple is only ever projected;
//   * `prodsum(x1, .., xk)` over Float64 rank-1 arrays (seed +0.0, the
//     left-nested product, as Interp.prodSum and the C++ IIFE fold it);
//   * a call `f(atoms)` to a module function that is inlined ONE level when
//     its body is a pure-total let spine with a scalar return -- and only when
//     one of its folds lands in a joined group (otherwise the call stays).
//
// LEGALITY, each a `declined -- <reason>` when it fails:
//   * every leg pure-total (`sfTotal`, a CLOSED typed whitelist: typed
//     floating `/` is discharged, integer `/` `%`, a non-literal integer `^`,
//     float->int casts, subscripts, guards, lgamma/digamma, output, external
//     reads and unknown or recursive callees decline; seeds are judged too),
//     every array a literal-extent plain dense axis (no runtime agreement
//     check), equal extents across the group;
//   * nothing between the join point and any member may write (the region
//     declines on any IRAssign / IRForRange; every crossed statement is judged
//     by the CSE facts, calls resolved through the whole program, deferred
//     reads inheriting their producer's facts);
//   * every free name of a leg (operands, seeds, captures) bound before the
//     join point;
//   * in a REORDERING build every leg agrees on the reorder licence, and
//     licensed legs fold with ONE builtin operator (the C++ join's
//     `fusedFoldSimdPlan` intersects licences and names one operator);
//   * a THREAD GATE: in a build that emits thread-level OpenMP and reorders,
//     a leg whose fold kernel requests omp under a licence (or whose map
//     kernel requests omp) joins only when EVERY leg carries its own reorder
//     licence and BLADE_JOIN_THREADS is on -- the policy of the threaded
//     reduction join (G7), under which the synthesized join (each leg keeping
//     its OWN request; none is manufactured) opens one team over the shared
//     traversal. Otherwise the group declines, so the requesting leg keeps
//     its own team. Request-only, so it may over-decline.
//   * an UNSEEDED builtin `(+)` / `(*)` leg over a literal extent >= 1 is
//     seeded with its exact identity (`sfIdentitySeed`); any other unseeded
//     kernel declines.
//   * a leg over a fresh `|> compute`d rank-1 map of the same region with no
//     use but folds is LEFT FOR THE PRODUCER EPILOGUE (the cell-epilogue item
//     owns that shape).

/// THE seed of an UNSEEDED builtin leg, in ONE place (round-2 arbitration Q1:
/// docs/formalism.md section 2.4 places NaN payloads and signalling state
/// outside the contract). `-0.0 + x` / `1.0 * x` is the exact identity under
/// round-to-nearest for every finite, infinite, signed-zero and quiet-NaN x,
/// so the seeded chain is bit-identical to the unseeded `a0 op a1 op ..` when
/// the extent is a literal >= 1 (the caller's check). The negative zero is the
/// node `IRUnaryOp(IRNeg, IRLit 0.0)` (renders `-(0.0)` / `fneg`), never a
/// float literal printer's spelling. Complex, unit-carrying, Bool and every
/// other kernel: None (decline).
let private sfIdentitySeed (op: IRBinOp) (et: ElemType) : (IRExpr * string) option =
    match op, et with
    | IRAdd, ETFloat64 -> Some (IRUnaryOp (IRNeg, IRLit (IRLitFloat 0.0)), "-0.0")
    | IRAdd, ETFloat32 -> Some (IRUnaryOp (IRNeg, IRLit (IRLitFloat32 0.0f)), "-0.0")
    | IRMul, ETFloat64 -> Some (IRLit (IRLitFloat 1.0), "1.0")
    | IRMul, ETFloat32 -> Some (IRLit (IRLitFloat32 1.0f), "1.0")
    | IRAdd, ETInt64 -> Some (IRLit (IRLitInt 0L), "0")
    | IRMul, ETInt64 -> Some (IRLit (IRLitInt 1L), "1")
    | IRAdd, ETInt32 -> Some (IRUnaryOp (IRCast (ETInt32, SrcLoc.Nowhere), IRLit (IRLitInt 0L)), "0")
    | IRMul, ETInt32 -> Some (IRUnaryOp (IRCast (ETInt32, SrcLoc.Nowhere), IRLit (IRLitInt 1L)), "1")
    | _ -> None

/// Where a region statement came from: an original spine statement, or the
/// j-th let of a call body inlined at (statement, call ordinal).
type private SfUnitKey =
    | SfOrig of int
    | SfInl of int * int * int

type private SfUnit = { Key: SfUnitKey; Binder: IRId option; Value: IRExpr }

/// One leg of a join. `Build` mints any synthesized kernel on demand (only a
/// rewrite that happens calls it).
type private SfLeg =
    { Build: unit -> IRExpr * IRExpr * IRExpr
      SeedNote: (string * string) option
      Licensed: bool
      Op: IRBinOp option
      Threads: bool
      /// The leg's map kernel against the C++ threaded join's OWN purity /
      /// totality admission (Blade.IRTotality, the predicate
      /// `joinedFoldThreadPlan`'s audit applies): Ok for a synthesized
      /// identity / product leaf.
      Audit: Result<unit, string>
      ResultType: IRType
      /// A leg made from `reduce(a, k[, s])` over a stored array: (a, k, the
      /// leg's seed -- the identity seed for an unseeded builtin leg). The
      /// producer-input admission (A7c) reads it; None for every other leg.
      Source: (IRId * IRExpr * IRExpr) option }

type private SfMember =
    { Unit: int
      UnitKey: SfUnitKey
      Ordinal: int
      Legs: SfLeg list
      Join: IRId option
      Extent: int64
      Axis: string
      Operands: IRId list
      Key: IRExpr option
      Desc: string
      FreeVars: Set<IRId> }

/// The candidate walk over one statement value, shared by discovery and the
/// rewrite so fold / call ordinals agree: `onFold` sees every fold node in an
/// unconditional position (not descended), `onCall` every call `isCall` admits
/// (after its arguments).
let private sfWalk (isCall: IRExpr -> bool) (onFold: int -> IRExpr -> IRExpr) (onCall: int -> IRExpr -> IRExpr) (e: IRExpr) : IRExpr =
    let mutable fo = 0
    let mutable co = 0
    let rec go (e: IRExpr) : IRExpr =
        match e with
        | IRReduce _ | IRReduceCompute _ | IRProdSum _ ->
            let o = fo
            fo <- fo + 1
            onFold o e
        | IRApp (f, args, t) ->
            let args' = args |> List.map go
            let e' = IRApp (f, args', t)
            if isCall e' then
                let o = co
                co <- co + 1
                onCall o e'
            else e'
        | IRTuple es -> IRTuple (es |> List.map go)
        | IRBinOp (m, (IRAnd | IROr as op), l, r, loc) -> IRBinOp (m, op, go l, r, loc)
        | IRBinOp (m, op, l, r, loc) ->
            let l' = go l
            let r' = go r
            IRBinOp (m, op, l', r', loc)
        | IRUnaryOp (op, x) -> IRUnaryOp (op, go x)
        | IRTupleProj (x, i, fl) -> IRTupleProj (go x, i, fl)
        | IRFma (a, b, c) ->
            let a' = go a
            let b' = go b
            IRFma (a', b', go c)
        | IRComplex (a, b) ->
            let a' = go a
            IRComplex (a', go b)
        | IRIf (c, t, f) -> IRIf (go c, t, f)
        | _ -> e
    go e

let shareSiblingFoldsModule (builder: IRBuilder) (programFuncs: IRCallable list) (modul: IRModule) : IRModule =
    let enabled = siblingFoldsEnabled ()
    let own = modul.Functions |> List.map (fun f -> (f.Id, f)) |> Map.ofList
    let funcs =
        programFuncs |> List.fold (fun m f -> if Map.containsKey f.Id m then m else Map.add f.Id f m) own
    let facts = cseFactsOf funcs
    let threadsBuild = Blade.Toolchain.ompThreadEmissionEnabled () && not (Blade.Toolchain.fpReproEnabled ())
    let reorderingBuild = not (Blade.Toolchain.fpReproEnabled ())

    // Names for the plan text: parameters, bindings, and the source names a
    // map's array identities carry.
    let names = System.Collections.Generic.Dictionary<IRId, string>()
    for KeyValue (_, f) in funcs do
        for p in f.Params do names.[p.VarId] <- p.Name
    for b in modul.Bindings do names.[b.Id] <- b.Name
    let noteIdentityNames (e: IRExpr) =
        iterIRExpr (fun n ->
            match n with
            | IRApplyCombinator info when info.Arrays.Length = info.Identities.Length ->
                List.iter2 (fun a ident ->
                    match a, ident with
                    | IRVar (id, _), AIDVariable nm when not (nm.StartsWith "__") && not (names.ContainsKey id) -> names.[id] <- nm
                    | _ -> ()) info.Arrays info.Identities
            | _ -> ()) e
    let nameOf (id: IRId) = match names.TryGetValue id with | true, n -> n | _ -> $"__v{id}"
    // A variable renders as a MARKER resolved when the record is written: the
    // names an inlined body's map identities carry are only known after the
    // tentative inlining that judges the call.
    let showAtom (e: IRExpr) =
        match e with
        | IRVar (id, _) -> $"\u0002{id}\u0003"
        | IRLit (IRLitInt n) -> string n
        | IRLit (IRLitFloat f) -> string f
        | _ -> ".."

    // Names bound to a DEFERRED form (their producer runs where they are read).
    let isDeferredForm (v: IRExpr) : bool =
        match v with
        | IRApplyCombinator _ | IRComposeApply _ | IRMethodFor _ | IRObjectFor _
        | IRFusion _ | IRParallel _ | IRChoice _ | IRFallback _ | IRGuard _ | IRSequence _
        | IRReplicate _ | IRFunctorMap _ | IRBind _ | IRPure _ | IRZip _ | IRArrayProduct _
        | IRComposeObj _ | IRComposeMeth _ | IRCompose _ | IRReynolds _ -> true
        | _ -> false
    let deferred = System.Collections.Generic.HashSet<IRId>()
    let deferredValue = System.Collections.Generic.Dictionary<IRId, IRExpr>()
    let noteDeferred (e: IRExpr) =
        iterIRExpr (fun n ->
            match n with
            | IRLet (id, v, _) when isDeferredForm v ->
                deferred.Add id |> ignore
                deferredValue.[id] <- v
            | _ -> ()) e
    for f in modul.Functions do noteDeferred f.Body
    for b in modul.Bindings do
        if isDeferredForm b.Value then
            deferred.Add b.Id |> ignore
            deferredValue.[b.Id] <- b.Value
        noteDeferred b.Value
    let capturedIds =
        funcs |> Map.fold (fun acc _ f -> f.Captures |> List.fold (fun a c -> Set.add c.Id a) acc) Set.empty
    // Names that merely ALIAS another name (`let w2 = w`): a read of one is a
    // read of the other, for the write closure and the provider test below.
    let aliasOf = System.Collections.Generic.Dictionary<IRId, IRId>()
    let noteAliases (e: IRExpr) =
        iterIRExpr (fun n ->
            match n with
            | IRLet (id, IRVar (y, _), _) when not (Map.containsKey y funcs) -> aliasOf.[id] <- y
            | _ -> ()) e
    for f in modul.Functions do noteAliases f.Body
    for b in modul.Bindings do
        (match b.Value with
         | IRVar (y, _) when not (Map.containsKey y funcs) -> aliasOf.[b.Id] <- y
         | _ -> ())
        noteAliases b.Value
    let rec resolveAlias (fuel: int) (id: IRId) =
        match aliasOf.TryGetValue id with
        | true, y when fuel > 0 -> resolveAlias (fuel - 1) y
        | _ -> id
    // A STREAMED provider read (`s.vars.A |> z.stream`) has an ordinary dense
    // array type but no storage a join can share: it is walked one block at a
    // time by its own fold, and it is an external read, not a pure value.
    let streamedIds =
        modul.ProviderReads |> Map.toSeq |> Seq.filter (fun (_, spec) -> spec.Streamed) |> Seq.map fst |> Set.ofSeq
    let isStreamed (id: IRId) = streamedIds.Contains id || streamedIds.Contains (resolveAlias 64 id)
    let ownBindingIds = modul.Bindings |> List.map (fun b -> b.Id) |> Set.ofList
    // MAY EVALUATING `e` WRITE? The CSE facts (callees resolved through the
    // whole program), CLOSED over what a read evaluates elsewhere: a deferred
    // let's producer runs where it is read, an alias reads its target, and a
    // callable's captures may themselves be deferred -- transitively, cycle-safe
    // (a name met again on the current path adds nothing new).
    let rec writesIn (visiting: Set<IRId>) (e: IRExpr) : bool =
        (facts e).MayWrite
        || (collectVarRefsIR e |> Seq.exists (fun x -> writesThrough visiting x))
    and writesThrough (visiting: Set<IRId>) (x: IRId) : bool =
        if Set.contains x visiting then false
        else
            let v = Set.add x visiting
            match deferredValue.TryGetValue x with
            | true, dv -> writesIn v dv
            | _ ->
            match aliasOf.TryGetValue x with
            | true, y -> writesThrough v y
            | _ ->
            match Map.tryFind x funcs with
            | Some c ->
                (c.Captures |> List.exists (fun cap -> writesThrough v cap.Id))
                || (collectVarRefsIR c.Body |> Seq.exists (fun r ->
                        (deferredValue.ContainsKey r || aliasOf.ContainsKey r || Map.containsKey r funcs)
                        && writesThrough v r))
            | None -> false
    let writeMemo = System.Collections.Generic.Dictionary<IRExpr, bool>(HashIdentity.Reference)
    let mayWrite (e: IRExpr) : bool =
        match writeMemo.TryGetValue e with
        | true, r -> r
        | _ ->
            let r = writesIn Set.empty e
            writeMemo.[e] <- r
            r
    // Mirror of the rank-1 producer-epilogue switch (BLADE_CELL_EPILOGUE,
    // the cell-epilogue item's gate): unset/anything else = on, 0/off/false/no
    // = off. Read per call.
    let cellEpilogueOn () =
        match System.Environment.GetEnvironmentVariable "BLADE_CELL_EPILOGUE" with
        | null -> true
        | v -> not (List.contains (v.Trim().ToLowerInvariant()) [ "0"; "off"; "false"; "no" ])

    // ---- the totality judge (closed, typed whitelist) ----------------------
    let isFloatish (t: IRType) = match t with AnyPrimElem (ETFloat32 | ETFloat64 | ETComplex64 | ETComplex128) -> true | _ -> false
    let isIntish (t: IRType) = match t with AnyPrimElem (ETInt32 | ETInt64 | ETBool) -> true | _ -> false
    let rec sfTotal (visited: Set<IRId>) (e: IRExpr) : bool =
        let tot = sfTotal visited
        match e with
        | IRLit _ -> true
        | IRVar (id, t) ->
            not (Map.containsKey id funcs) && not (isFunctionTyped t) && not (deferred.Contains id)
            && (match t with IRTLoop _ | IRTComputation _ -> false | _ -> true)
        | IRBinOp (_, (IRDiv | IRMod as op), l, r, _) ->
            tot l && tot r
            && (not (irNodeMayAbort e) || (op = IRDiv && isFloatish (typeOf l) && isFloatish (typeOf r)))
        | IRBinOp (_, IRCaret, l, r, _) -> tot l && tot r && (not (irNodeMayAbort e) || isFloatish (typeOf l))
        | IRBinOp (_, _, l, r, _) -> tot l && tot r
        | IRUnaryOp (IRCast ((ETInt64 | ETInt32), _), x) -> tot x && isIntish (typeOf x)
        | IRUnaryOp (IRMath ("lgamma" | "digamma"), _) -> false
        | IRUnaryOp (_, x) -> tot x
        | IRFma (a, b, c) -> tot a && tot b && tot c
        | IRComplex (a, b) -> tot a && tot b
        | IRIf (c, t, f) -> tot c && tot t && tot f
        | IRLet (_, v, b) -> tot v && tot b
        | IRTuple es -> es |> List.forall tot
        | IRTupleProj (x, _, _) -> tot x
        | IRExtent (IRVar _, _) -> true
        | IRApp (IRVar (fid, _), args, _) ->
            args |> List.forall tot
            && (match Map.tryFind fid funcs with
                | Some c when not (Set.contains fid visited) && not c.IsStatic && not c.IsCudaKernel
                              && not c.IsMpiParallel && c.Params.Length = args.Length ->
                    sfTotal (Set.add fid visited) c.Body
                | _ -> false)
        | _ -> false
    let kernelOf (k: IRExpr) : IRCallable option =
        match k with
        | IRVar (kid, _) -> Map.tryFind kid funcs
        | _ -> None
    let kernelTotal (c: IRCallable) (arity: int) =
        c.Params.Length = arity && not c.IsCudaKernel && not c.IsMpiParallel && not c.IsStatic
        && sfTotal (Set.singleton c.Id) c.Body
    let builtinOp (c: IRCallable) : IRBinOp option =
        match c.Params, c.Body with
        | [ p0; p1 ], IRBinOp (_, op, IRVar (a, _), IRVar (b, _), _)
            when (match op with IRAdd | IRMul | IRAnd | IROr -> true | _ -> false)
                 && ((a = p0.VarId && b = p1.VarId) || (a = p1.VarId && b = p0.VarId)) -> Some op
        | _ -> None
    // `foldReorderLicensed`'s rule (CodeGenExprSupport), restated over this
    // module's table: a `where repro` kernel (or the eta wrapper of one) vetoes.
    let licensed (c: IRCallable) =
        let vetoed =
            c.IsRepro
            || (match c.Body with
                | IRApp (IRVar (fid, _), _, _) -> (match Map.tryFind fid funcs with Some g -> g.IsRepro | None -> false)
                | _ -> false)
        not vetoed && (c.IsCommutative || not c.CommGroups.IsEmpty || (builtinOp c).IsSome)
    let plainAxis (at: IRArrayType) : (int64 * IRIndexType) option =
        if at.IsVirtual then None else
        match at.IndexTypes with
        | [ ix ] when ix.IxKind = IxKPlain && ix.Symmetry = SymNone && ix.Rank = 1 ->
            (match ix.Extent with IRLit (IRLitInt n) -> Some (n, ix) | _ -> None)
        | _ -> None
    let axisName (ix: IRIndexType) = match ix.Tag with Some t when not (t.StartsWith "__") -> t | _ -> "axis"
    let bareElem (t: IRType) : ElemType option =
        match t with
        | IRTScalar ((ETFloat64 | ETFloat32 | ETInt64 | ETInt32) as et) -> Some et
        | _ -> None
    let rec leavesOf (e: IRExpr) =
        match e with
        | IRFusion (l, r) -> leavesOf l @ leavesOf r
        | x -> [ x ]
    // A plain dense map leaf over stored literal-extent arrays of one extent:
    // Some (extent, axis, operand ids, kernel).
    let rec plainLeaf (info: ApplyInfo) = plainLeafWith true info
    and plainLeafWith (requireTotal: bool) (info: ApplyInfo) : (int64 * IRIndexType * IRId list * IRCallable) option =
        // The fusion pass's plain-map predicate; a Loop slot that NAMES the
        // loop object (`let l = method_for(x)`, a composed chain binding) is
        // the same provenance, one let away.
        let info' =
            match info.Loop with
            | IRVar _ -> { info with Loop = IRObjectFor { Kernel = info.Kernel; CommGroups = []; InputRanks = []; OutputRank = 0 } }
            | _ -> info
        if not (tilePlainInfo info') || info.Arrays.IsEmpty || info.Arrays.Length <> info.ArrayTypes.Length then None
        elif info.Arrays.Length > 1 && not info.IsCoIteration then None
        else
            let axes = info.ArrayTypes |> List.map plainAxis
            let ids = info.Arrays |> List.choose (function IRVar (id, _) -> Some id | _ -> None)
            if axes |> List.exists Option.isNone || ids.Length <> info.Arrays.Length then None
            else
                let axes = axes |> List.map Option.get
                let n0 = fst axes.Head
                if axes |> List.exists (fun (n, _) -> n <> n0) then None
                else
                    match kernelOf info.Kernel with
                    | Some k when (not requireTotal || kernelTotal k info.Arrays.Length) && bareElem k.RetType |> Option.isSome ->
                        Some (n0, snd axes.Head, ids, k)
                    | _ -> None
    // Pure-total fold NODE (an inlined body's statement): operands stored,
    // literal extents equal, kernels and seeds total, unseeded only over n >= 1.
    // A streamed provider read is neither stored nor pure (see isStreamed).
    let foldTotal (e: IRExpr) : bool =
        match e with
        | IRReduce (IRVar (aid, ArrayElem at), k, init) when not (isStreamed aid) ->
            (match plainAxis at, kernelOf k with
             | Some (n, _), Some c ->
                kernelTotal c 2 && (match init with Some s -> sfTotal Set.empty s | None -> n >= 1)
             | _ -> false)
        | IRReduceCompute (comp, k, s) ->
            let leaves = leavesOf comp
            let ls = leaves |> List.map (function IRApplyCombinator i -> plainLeaf i | _ -> None)
            let ks = match k with IRTuple ks -> ks | k -> [ k ]
            let ss = match s with IRTuple ss -> ss | s -> [ s ]
            ls |> List.forall Option.isSome
            && ls |> List.forall (fun l -> let (_, _, ids, _) = l.Value in ids |> List.forall (isStreamed >> not))
            && (ls |> List.map (fun l -> let (n, _, _, _) = l.Value in n) |> List.distinct |> List.length) = 1
            && ks |> List.forall (fun k -> match kernelOf k with Some c -> kernelTotal c 2 | None -> false)
            && ss |> List.forall (sfTotal Set.empty)
        | IRProdSum args ->
            let axes = args |> List.map (function IRVar (aid, ArrayElem at) when not (isStreamed aid) -> plainAxis at | _ -> None)
            axes |> List.forall Option.isSome
            && (axes |> List.map (fun a -> fst a.Value) |> List.distinct |> List.length) = 1
        | _ -> false

    // ---- one-level inliner ---------------------------------------------------
    // Expression/kernel bodies do not open an allocation tracking frame in
    // C++. Only ordinary statement-rendered functions may retain an inline
    // owning pools. A function used as a value (including a kernel slot or
    // alias) is therefore excluded; direct calls remain statement-rendered.
    let expressionRendered = System.Collections.Generic.HashSet<IRId>()
    let rec noteFunctionUses e =
        match e with
        | IRApp (IRVar _, args, _) -> args |> List.iter noteFunctionUses
        | IRVar (id, _) when Map.containsKey id funcs -> expressionRendered.Add id |> ignore
        | ExprShape (children, _) -> children |> List.iter noteFunctionUses
    for f in programFuncs do noteFunctionUses f.Body
    for f in modul.Functions do noteFunctionUses f.Body
    for b in modul.Bindings do noteFunctionUses b.Value
    let isOwnCall (e: IRExpr) =
        match e with
        | IRApp (IRVar (fid, _), _, _) ->
            (match Map.tryFind fid own with
             | Some c -> not (c.Name.StartsWith "__lambda_")
             | None -> false)
        | _ -> false
    let rec unrollSpine (e: IRExpr) : (IRId * IRExpr) list * IRExpr =
        match e with
        | IRLet (id, v, b) ->
            let (ls, r) = unrollSpine b
            ((id, v) :: ls, r)
        | _ -> ([], e)
    let countFolds (e: IRExpr) =
        let mutable n = 0
        iterIRExpr (fun x -> match x with IRReduce _ | IRReduceCompute _ | IRProdSum _ -> n <- n + 1 | _ -> ()) e
        n
    let inlineValueOk (v: IRExpr) : bool =
        match v with
        | IRReduce _ | IRReduceCompute _ | IRProdSum _ -> foldTotal v
        | IRMethodFor mf -> mf.Arrays |> List.forall (function IRVar _ -> true | _ -> false)
        | IRObjectFor _ -> true
        | IRCompute (IRApplyCombinator info) ->
            (match plainLeaf info with
             | Some (_, _, ids, _) -> ids |> List.forall (isStreamed >> not)
             | None -> false)
        | IRTupleProj (IRVar _, _, _) -> true
        | _ -> sfTotal Set.empty v
    /// Ok (lets, result atom, display) or Error why.
    let inlineJudge (call: IRExpr) : Result<(IRId * IRExpr) list * IRExpr * string, string> =
        match call with
        | IRApp (IRVar (fid, _), args, retTy) ->
            let c = own.[fid]
            let disp = $"""{c.SourceName}({args |> List.map showAtom |> String.concat ", "})"""
            let refused why = Error $"{disp} not inlined: {why}"
            if c.IsRepro then refused "a `where repro` callee keeps its per-function veto"
            elif c.IsStatic || c.IsArityPoly || hasTypeVarsInSignature c then refused "a static, arity-polymorphic or generic callee"
            elif c.IsCudaKernel || c.IsMpiParallel || c.IsOmpParallel then refused "a callee with a parallel strategy"
            elif not c.Captures.IsEmpty then refused "the callee captures"
            elif c.Params.Length <> args.Length then refused "an argument-count mismatch"
            elif args |> List.exists (function IRVar (_, t) -> isFunctionTyped t | IRLit _ -> false | _ -> true) then
                refused "an argument is not a variable or a literal"
            elif args |> List.exists (function IRVar (id, _) -> isStreamed id | _ -> false) then
                refused "an argument is a streamed provider read (walked one block at a time; not a stored, pure value)"
            elif bareElem c.RetType |> Option.isNone then refused "the callee does not return a bare scalar"
            elif Set.contains c.Id (collectVarRefsIR c.Body) then refused "a recursive callee"
            else
            let mutable shapeOk = true
            iterIRExpr (fun n ->
                match n with
                | IRParam _ | IRNth | IRArity _ | IRPolyIndex _ | IRPolyTail _ | IRAssign _ | IRForRange _ -> shapeOk <- false
                | IRVar (id, _) ->
                    (match Map.tryFind id funcs with
                     | Some g when not g.Captures.IsEmpty -> shapeOk <- false
                     | _ -> ())
                | _ -> ()) c.Body
            if not shapeOk then refused "the callee body names a capturing lambda, a pack form or an assignment"
            else
            let (lets0, r0) = unrollSpine c.Body
            let releaseSupported v =
                match v with
                | IRCompute (IRApplyCombinator info) ->
                    match stripUnits info.OutputType with
                    | ArrayElem at -> (plainAxis at).IsSome && (bareElem at.ElemType).IsSome
                    | _ -> false
                | _ -> true
            if not ((lets0 |> List.forall (snd >> inlineValueOk)) && inlineValueOk r0) then
                refused "the callee body is not a pure-total let spine of folds, maps and scalar arithmetic"
            elif not (lets0 |> List.forall (snd >> releaseSupported)) then
                refused "lifetime event not honoured by the common dense rank-one pool renderer"
            else
            let (ps', body') = freshenCallable builder c
            let subst = List.zip ps' args |> List.map (fun (p, a) -> (p.VarId, a)) |> Map.ofList
            let body'' =
                mapIRExpr (fun n ->
                    match n with
                    | IRVar (id, _) when Map.containsKey id subst -> subst.[id]
                    | _ -> n) body'
            let (lets, r) = unrollSpine body''
            let lets, atom =
                match r with
                | IRVar _ | IRLit _ -> lets, r
                | _ ->
                    let t = builder.FreshId()
                    lets @ [ (t, r) ], IRVar (t, retTy)
            Ok (lets, atom, disp)
        | _ -> Error "not a call"

    let minted = ResizeArray<IRCallable * IRId option>()
    let mint (origin: IRId option) (c: IRCallable) =
        minted.Add((c, origin))
        c

    // ---- one region ----------------------------------------------------------
    let processRegion (subject: string) (origin: IRId option) (regionParams: Set<IRId>) (body: IRExpr) : IRExpr =
        let lifetimeProblem =
            if (Blade.Toolchain.ompCoverageModeCell ()).Value then Some "OpenMP coverage renderer"
            elif origin |> Option.exists expressionRendered.Contains then Some "expression/kernel renderer"
            else None
        let (spineLets, final) = unrollSpine body
        let stmts = (spineLets |> List.map (fun (id, v) -> (Some id, v))) @ [ (None, final) ]
        // An inlined call's spine is placed BEFORE its whole statement, ahead
        // of whatever the statement evaluates before (or after) the call; so a
        // statement that may write anywhere keeps its calls as calls.
        let stmtWrites = stmts |> List.map (fun (_, v) -> mayWrite v) |> Array.ofList
        let inlineCache = System.Collections.Generic.Dictionary<int * int, Result<(IRId * IRExpr) list * IRExpr * string, string>>()
        let judgeAt (i: int) (ord: int) (call: IRExpr) =
            match inlineCache.TryGetValue((i, ord)) with
            | true, r -> r
            | _ ->
                let r =
                    match inlineJudge call with
                    | Ok (_, _, disp) when lifetimeProblem.IsSome ->
                        Error $"{disp} not inlined: lifetime event not honoured by {lifetimeProblem.Value}; retained inline declined"
                    | Ok _ when stmtWrites.[i] ->
                        let disp =
                            match call with
                            | IRApp (IRVar (fid, _), args, _) -> $"""{own.[fid].SourceName}({args |> List.map showAtom |> String.concat ", "})"""
                            | _ -> "the call"
                        Error $"{disp} not inlined: its statement may write (inlining moves the call's body ahead of the whole statement, across that write)"
                    | r -> r
                inlineCache.[(i, ord)] <- r
                r
        let refusedCallFolds = System.Collections.Generic.Dictionary<int * int, int>()
        let buildUnits (keep: int * int -> bool) : SfUnit list =
            let units = ResizeArray<SfUnit>()
            stmts |> List.iteri (fun i (binder, v) ->
                let here = ResizeArray<int * (IRId * IRExpr) list>()
                let v' =
                    sfWalk isOwnCall (fun _ n -> n) (fun ord call ->
                        match judgeAt i ord call with
                        | Ok (lets, atom, _) when keep (i, ord) ->
                            here.Add((ord, lets))
                            atom
                        | Ok _ -> call
                        | Error _ ->
                            (match call with
                             | IRApp (IRVar (fid, _), _, _) -> refusedCallFolds.[(i, ord)] <- countFolds own.[fid].Body
                             | _ -> ())
                            call) v
                for (ord, lets) in here do
                    lets |> List.iteri (fun j (id, lv) -> units.Add { Key = SfInl (i, ord, j); Binder = Some id; Value = lv })
                units.Add { Key = SfOrig i; Binder = binder; Value = v' })
            List.ofSeq units
        let tentative = buildUnits (fun _ -> true) |> Array.ofList
        for u in tentative do noteIdentityNames u.Value

        // Fold candidates in unconditional positions.
        let found = ResizeArray<int * int * IRExpr>()
        tentative |> Array.iteri (fun ui u ->
            sfWalk isOwnCall (fun ord n -> found.Add((ui, ord, n)); n) (fun _ c -> c) u.Value |> ignore)
        let refusals =
            // Only a refused callee that HOLDS folds is a decision worth naming.
            inlineCache |> Seq.choose (fun (KeyValue (k, r)) ->
                match r with
                | Error why when (match refusedCallFolds.TryGetValue k with | true, n -> n > 0 | _ -> false) -> Some why
                | _ -> None)
            |> Seq.distinct |> List.ofSeq
        let refusedFolds = refusedCallFolds.Values |> Seq.sum
        if found.Count + refusedFolds < 2 then body else

        let boundAt = System.Collections.Generic.Dictionary<IRId, int>()
        tentative |> Array.iteri (fun ui u -> match u.Binder with Some id -> boundAt.[id] <- ui | None -> ())
        let imperative =
            tentative |> Array.exists (fun u ->
                let mutable hit = false
                iterIRExpr (fun n -> match n with IRAssign _ | IRForRange _ | IRBreakIf _ -> hit <- true | _ -> ()) u.Value
                hit)
        let unitWrites = tentative |> Array.map (fun u -> mayWrite u.Value)
        // LEFT FOR THE PRODUCER EPILOGUE: exactly the shape the rank-1
        // cell-epilogue planner admits (Optimize.planHotRowFolds' rank-1 arm,
        // mirrored textually, not shared): a let-bound
        // `IRReduceCompute(method_for(y) <@> k, F, seed)` whose cell kernel is
        // `w` or `w * w` of its own cell, `y` a let-bound dense rank-1
        // literal-extent `|> compute` map of this region, the fold the FIRST
        // such consumer of `y`, nothing between them reading `y` or what the
        // cell kernel captures, everything between repeatable, the fold
        // kernel a two-parameter callable, no cuda/mpi or Reynolds -- and the
        // epilogue gate on. Anything else is an ordinary candidate.
        let cellTermOk (ck: IRCallable) =
            match ck.Params with
            | [ p ] ->
                let isP (e: IRExpr) =
                    match e with
                    | IRVar (id, _) -> id = p.VarId
                    | IRParam (_, idx, _) -> idx = p.Index
                    | _ -> false
                (match ck.Body with
                 | IRBinOp (IRElementwise, IRMul, a, b, _) when isP a && isP b -> true
                 | e -> isP e)
            | _ -> false
        let oneCellConsumerOf (v: IRExpr) : (IRId * ApplyInfo * IRCallable) option =
            match v with
            | IRReduceCompute (IRApplyCombinator cinfo, _, _) ->
                (match cinfo.Arrays, kernelOf cinfo.Kernel with
                 | [ IRVar (a, _) ], Some ck when ck.Params.Length = 1 && cinfo.KernelOutputRank = 0
                                                 && (match stripUnits ck.Params.Head.Type with IRTScalar _ -> true | _ -> false) ->
                    Some (a, cinfo, ck)
                 | _ -> None)
            | _ -> None
        let epilogueReserved (ui: int) (ord: int) (node: IRExpr) : bool =
            if not (cellEpilogueOn ()) || ord <> 0 then false else
            let u = tentative.[ui]
            match u.Binder, oneCellConsumerOf u.Value, node with
            | Some _, Some (pId, cinfo, ck), IRReduceCompute (_, k, _) when obj.ReferenceEquals(u.Value, node) ->
                (match boundAt.TryGetValue pId with
                 | true, pu when pu < ui ->
                    let rec collapse (e: IRExpr) = match e with IRCompute i -> collapse i | e -> e
                    (match collapse tentative.[pu].Value, tentative.[pu].Value with
                     | IRApplyCombinator pinfo, IRCompute _ ->
                        let rank1 =
                            match stripUnits pinfo.OutputType with
                            | ArrayElem at ->
                                not at.IsVirtual && pinfo.KernelOutputRank = 0
                                && (match at.IndexTypes with
                                    | [ ix ] -> ix.IxKind = IxKPlain && ix.Symmetry = SymNone && ix.Rank = 1
                                                && (match ix.Extent with IRLit (IRLitInt n) -> n > 0L | _ -> false)
                                    | _ -> false)
                            | _ -> false
                        let pkOk =
                            match kernelOf pinfo.Kernel with
                            | Some pk -> not pk.IsCudaKernel && not pk.IsMpiParallel
                            | None -> false
                        let firstConsumer =
                            seq { pu + 1 .. ui - 1 }
                            |> Seq.forall (fun i -> match oneCellConsumerOf tentative.[i].Value with Some (a, _, _) -> a <> pId | None -> true)
                        let captureIds = ck.Captures |> List.map (fun c -> c.Id) |> Set.ofList
                        let betweenOk =
                            seq { pu + 1 .. ui - 1 }
                            |> Seq.forall (fun i ->
                                let t = tentative.[i]
                                let r = collectVarRefsIR t.Value
                                (match t.Binder with Some b -> not (captureIds.Contains b) | None -> true)
                                && not (Set.contains pId r)
                                && Set.isEmpty (Set.intersect r captureIds)
                                && (facts t.Value).Repeatable)
                        let fkOk =
                            match kernelOf k with
                            | Some fk -> fk.Params.Length = 2 && not fk.IsCudaKernel && not fk.IsMpiParallel
                                         && (match stripUnits fk.RetType with IRTScalar _ -> true | _ -> false)
                            | None -> false
                        rank1 && pkOk && firstConsumer && betweenOk && fkOk
                        && not pinfo.HasReynolds && not cinfo.HasReynolds
                        && not (Set.contains pId captureIds) && not (Set.contains pId (collectVarRefsIR ck.Body))
                        && cellTermOk ck
                     | _ -> false)
                 | _ -> false)
            | _ -> false
        let operandProblem (id: IRId) : string option =
            // (A module binding is not refused for being reassignable: inside
            // one region only a CALL or an assignment can write it, and both
            // are barriers above.)
            if deferred.Contains id || deferred.Contains (resolveAlias 64 id) then
                Some "an operand is a deferred computation (one level of sharing only)"
            elif isStreamed id then Some "an operand is a streamed provider read (walked one block at a time by its own fold; not a stored, pure value)"
            elif not (boundAt.ContainsKey id || regionParams.Contains id || ownBindingIds.Contains id) then
                Some "an operand is bound outside this region and module (its provider metadata is not visible here)"
            else None
        let freeVarsOf (e: IRExpr) : Set<IRId> =
            let acc = System.Collections.Generic.HashSet<IRId>()
            let rec addExpr (x: IRExpr) = for id in collectVarRefsIR x do addId id
            and addId (id: IRId) =
                if acc.Add id then
                    match Map.tryFind id funcs with
                    | Some c ->
                        for cap in c.Captures do addId cap.Id
                        addExpr c.Body
                    | None -> ()
            addExpr e
            Set.ofSeq acc
        // Canonical form for the duplicate-leg test: callables by structure,
        // floats by bit pattern.
        let callableKey (f: IRCallable) : IRExpr =
            let subst = f.Params |> List.mapi (fun i p -> (p.VarId, -(i + 1))) |> Map.ofList
            let b =
                mapIRExpr (fun e ->
                    match e with
                    | IRVar (id, t) when Map.containsKey id subst -> IRVar (subst.[id], t)
                    | _ -> floatBitsLit e) f.Body
            IRTuple [ b; IRLit (IRLitString $"{f.IsOmpParallel}|{f.IsCommutative}|{f.IsRepro}|%A{f.CommGroups}") ]
        let canon (e: IRExpr) : IRExpr =
            mapIRExpr (fun n ->
                match n with
                | IRVar (id, _) ->
                    (match Map.tryFind id funcs with
                     | Some f -> callableKey f
                     | None -> n)
                | _ -> floatBitsLit n) e

        let mkLeg (leafF: unit -> IRExpr) (k: IRExpr) (c: IRCallable) (seed: IRExpr) (note: (string * string) option) (leafOmp: bool) (audit: Result<unit, string>) (resTy: IRType) =
            let lic = licensed c
            { Build = (fun () -> (leafF (), k, seed)); SeedNote = note; Licensed = lic; Op = builtinOp c
              Threads = (c.IsOmpParallel && lic) || leafOmp; Audit = audit; ResultType = resTy; Source = None }

        // Member admission.
        let admit (ui: int) (ord: int) (node: IRExpr) : Result<SfMember, string> =
            let u = tentative.[ui]
            let baseM legs n ix ids key desc join =
                { Unit = ui; UnitKey = u.Key; Ordinal = ord; Legs = legs; Join = join; Extent = n
                  Axis = axisName ix; Operands = ids; Key = key; Desc = desc; FreeVars = freeVarsOf node }
            match node with
            | IRReduce (arr, k, init) ->
                let desc = $"reduce({showAtom arr}, ..)"
                (match arr with
                 | IRVar (aid, (ArrayElem at as aty)) ->
                    (match plainAxis at, bareElem at.ElemType, kernelOf k with
                     | None, _, _ -> Error $"{desc}: not over one plain dense literal-extent axis"
                     | _, None, _ -> Error $"{desc}: the element type is not a bare Float64/Float32/Int64/Int32 (complex and unit-carrying legs are a v1.1 item)"
                     | _, _, None -> Error $"{desc}: the fold kernel is not a resolvable callable"
                     | Some (n, ix), Some et, Some c ->
                        match operandProblem aid with
                        | Some why -> Error $"{desc}: {why}"
                        | None ->
                        if not (kernelTotal c 2) then Error $"{desc}: the fold kernel may fail or has effects"
                        else
                        let seedR =
                            match init with
                            | Some s -> if sfTotal Set.empty s then Ok (s, None) else Error "the seed may fail or has effects"
                            | None ->
                                match builtinOp c with
                                | Some op when n >= 1L ->
                                    (match sfIdentitySeed op et with
                                     | Some (s, txt) -> Ok (s, Some (operatorFunctionName op, txt))
                                     | None -> Error "unseeded with no identity seed for its operator and element in v1")
                                | Some _ -> Error "an unseeded leg's extent is not a literal >= 1 (the empty-fold BL8003 is kept)"
                                | None -> Error "unseeded with a non-builtin kernel (no identity)"
                        match seedR with
                        | Error why -> Error $"{desc}: {why}"
                        | Ok (seed, note) ->
                            let elemTy = at.ElemType
                            let leafF () =
                                let pid = builder.FreshId()
                                let parms : IRParam list = [ { Name = "__sfx"; Type = elemTy; Index = 0; VarId = pid } ]
                                let lam =
                                    { mkLambdaCallable builder parms (IRVar (pid, elemTy)) elemTy [] false [] [] false false 256 false
                                        with SourceName = anonymousCallableName (Some subject) "lambda" }
                                    |> mint origin
                                let outTy =
                                    match aty with
                                    | IRTArrow (slots, _, id2) -> IRTArrow (slots, elemTy, id2)
                                    | t -> t
                                mapOneArrayApply arr at (IRVar (lam.Id, mkFuncArrow [ elemTy ] elemTy)) outTy
                            let leg = { mkLeg leafF k c seed note false (Ok ()) (typeOf node) with Source = Some (aid, k, seed) }
                            Ok (baseM [ leg ] n ix [ aid ] (Some (canon node)) desc None))
                 | _ -> Error $"{desc}: the operand is not a stored array named by a variable")
            | IRProdSum args ->
                let desc = $"""prodsum({args |> List.map showAtom |> String.concat ", "})"""
                let typed =
                    args |> List.map (fun a ->
                        match a with
                        | IRVar (id, ArrayElem at) ->
                            (match plainAxis at with
                             | Some (n, ix) when at.ElemType = IRTScalar ETFloat64 -> Some (id, at, n, ix)
                             | _ -> None)
                        | _ -> None)
                if args.Length < 1 || typed |> List.exists Option.isNone then
                    Error $"{desc}: not Float64 rank-1 stored arrays over one plain literal-extent axis"
                else
                let typed = typed |> List.map Option.get
                let (_, _, n0, ix0) = typed.Head
                if typed |> List.exists (fun (_, _, n, _) -> n <> n0) then Error $"{desc}: operand extents differ"
                else
                match typed |> List.tryPick (fun (id, _, _, _) -> operandProblem id) with
                | Some why -> Error $"{desc}: {why}"
                | None ->
                let f64 = IRTScalar ETFloat64
                let addLam () =
                    let a = builder.FreshId()
                    let b = builder.FreshId()
                    let parms : IRParam list =
                        [ { Name = "__sfa"; Type = f64; Index = 0; VarId = a }; { Name = "__sfb"; Type = f64; Index = 1; VarId = b } ]
                    { mkLambdaCallable builder parms (IRBinOp (IRElementwise, IRAdd, IRVar (a, f64), IRVar (b, f64), SrcLoc.Nowhere)) f64 [] false [] [] false false 256 false
                        with SourceName = anonymousCallableName (Some subject) "(+)" }
                let leafF () =
                    let pids = args |> List.map (fun _ -> builder.FreshId())
                    let parms : IRParam list = pids |> List.mapi (fun i p -> { Name = $"__sfp{i}"; Type = f64; Index = i; VarId = p })
                    let prod =
                        pids |> List.map (fun p -> IRVar (p, f64))
                        |> List.reduce (fun l r -> IRBinOp (IRElementwise, IRMul, l, r, SrcLoc.Nowhere))
                    let lam =
                        { mkLambdaCallable builder parms prod f64 [] false [] [] false false 256 false
                            with SourceName = anonymousCallableName (Some subject) "lambda" }
                        |> mint origin
                    let ats = typed |> List.map (fun (_, at, _, _) -> at)
                    let ids = args |> List.map coIterIdentityOf
                    let k = args.Length
                    let mf : MethodForInfo =
                        { Arrays = args; Identities = ids; ArrayTypes = ats; SDimsPerArray = List.replicate k 1
                          TotalSDims = k; SharedIndexTypes = [ ix0 ] }
                    IRApplyCombinator
                        { Loop = IRMethodFor mf
                          Kernel = IRVar (lam.Id, mkFuncArrow (List.replicate k f64) f64)
                          Arrays = args; Identities = ids; ArrayTypes = ats; SharedIndexTypes = [ ix0 ]
                          SymcomStates = List.replicate k SCNeither; TriangularLevels = List.replicate k false
                          SDimsPerArray = List.replicate k 1; KernelInputRanks = List.replicate k 0; KernelOutputRank = 0
                          KernelTDims = []; SpeedupFactor = 1L; ReynoldsSpeedup = 1L; HasReynolds = false
                          OutputType = IRTArrow ([ SIdx ix0 ], f64, None); IsCoIteration = k > 1 }
                let leg =
                    { Build = (fun () ->
                        let leaf = leafF ()
                        let add = addLam () |> mint origin
                        (leaf, IRVar (add.Id, mkFuncArrow [ f64; f64 ] f64), IRLit (IRLitFloat 0.0)))
                      SeedNote = None; Licensed = true; Op = Some IRAdd; Threads = false; Audit = Ok (); ResultType = f64; Source = None }
                Ok (baseM [ leg ] n0 ix0 (typed |> List.map (fun (id, _, _, _) -> id)) (Some (canon node)) desc None)
            | IRReduceCompute (comp, k, s) ->
                let leaves = leavesOf comp
                let infos = leaves |> List.choose (function IRApplyCombinator i -> Some i | _ -> None)
                let ks = match k with IRTuple ks -> ks | k -> List.replicate leaves.Length k
                let ss = match s with IRTuple ss -> ss | s -> List.replicate leaves.Length s
                let isJoin = leaves.Length > 1
                let desc =
                    let ops = infos |> List.collect (fun i -> i.Arrays) |> List.map showAtom |> List.distinct |> String.concat ", "
                    if isJoin then $"a join of {leaves.Length} folds over {{{ops}}}" else $"reduce(<map over {ops}>, ..)"
                if infos.Length <> leaves.Length then Error $"{desc}: a leaf is a named deferred map (one level of sharing only)"
                elif ks.Length <> leaves.Length || ss.Length <> leaves.Length then Error $"{desc}: an unexpected join encoding"
                elif not isJoin && epilogueReserved ui ord node then Error $"{desc}: left for the producer epilogue"
                else
                let pls = infos |> List.map plainLeaf
                if pls |> List.exists Option.isNone then
                    if infos |> List.forall (fun i -> (plainLeafWith false i).IsSome) then
                        Error $"{desc}: a summand may fail or has effects (integer `/` `%%`, a float->int cast, a subscript, lgamma, output, an unknown callee): interleaving it could change which failure comes first"
                    else
                        Error $"{desc}: a leaf is not a plain dense map over stored arrays on one literal-extent axis"
                else
                let pls = pls |> List.map Option.get
                let (n0, ix0, _, _) = pls.Head
                if pls |> List.exists (fun (n, _, _, _) -> n <> n0) then Error $"{desc}: leaf extents differ"
                else
                let ids = pls |> List.collect (fun (_, _, ids, _) -> ids) |> List.distinct
                match ids |> List.tryPick operandProblem with
                | Some why -> Error $"{desc}: {why}"
                | None ->
                let kcs = ks |> List.map kernelOf
                if kcs |> List.exists (function Some c -> not (kernelTotal c 2) | None -> true) then
                    Error $"{desc}: a fold kernel may fail, has effects or does not resolve"
                elif ss |> List.exists (sfTotal Set.empty >> not) then Error $"{desc}: a seed may fail or has effects"
                else
                // A join must be let-bound, its tuple only ever projected. A
                // PER-LEG join types as a FLAT tuple (structural index = leaf
                // index); a SHARED-fold tree types as NESTED PAIRS, where a
                // structural projection `t[1]` names a subtree, not leg 1 --
                // v1 admits a shared tree only when every projection is a
                // flat leaf index.
                let perLeg = match s with IRTuple ss when ss.Length >= 2 -> true | _ -> false
                let joinId =
                    if not isJoin then Ok None
                    else
                        match u.Binder with
                        | Some jid when ord = 0 && (match u.Value with IRReduceCompute _ -> true | _ -> false) ->
                            let mutable all = 0
                            let mutable proj = 0
                            let mutable structural = 0
                            for v in tentative do
                                iterIRExpr (fun x ->
                                    match x with
                                    | IRVar (y, _) when y = jid -> all <- all + 1
                                    | IRTupleProj (IRVar (y, _), k, isFlat) when y = jid ->
                                        proj <- proj + 1
                                        // (two leaves: the pair IS the flat tuple)
                                        if (not isFlat && leaves.Length > 2) || k >= leaves.Length then structural <- structural + 1
                                    | _ -> ()) v.Value
                            if all <> proj || capturedIds.Contains jid then
                                Error $"{desc}: the join's tuple is used whole, not only projected"
                            elif not perLeg && structural > 0 then
                                Error $"{desc}: a shared-fold tree read by a structural (nested-pair) projection; v1 joins only flat leaf projections"
                            else Ok (Some jid)
                        | _ -> Error $"{desc}: an existing join that is not let-bound on its own"
                match joinId with
                | Error why -> Error why
                | Ok join ->
                let legs =
                    List.zip3 infos (List.zip ks (kcs |> List.map Option.get)) (List.zip ss pls)
                    |> List.map (fun (info, (kx, c), (sx, (_, _, _, leafK))) ->
                        let resTy = if isJoin then typeOf sx else typeOf node
                        let audit =
                            Blade.IRTotality.auditApply (resolveCallableIn funcs) (fun _ -> None)
                                (fun id -> deferred.Contains id || deferred.Contains (resolveAlias 64 id)) info
                        mkLeg (fun () -> IRApplyCombinator info) kx c sx None leafK.IsOmpParallel audit resTy)
                Ok (baseM legs n0 ix0 ids (if isJoin then None else Some (canon node)) desc join)
            | _ -> Error "not a fold"

        let admitted = ResizeArray<SfMember>()
        let rejected = ResizeArray<string>()
        if imperative then
            rejected.Add "the region assigns or loops imperatively (a write could fall between the folds)"
        else
            for (ui, ord, node) in found do
                match admit ui ord node with
                | Ok m -> admitted.Add m
                | Error why -> rejected.Add why

        // Grouping: greedy from the earliest member, by extent, availability
        // and the write barrier over the whole interval.
        let groups = ResizeArray<SfMember list>()
        let mutable remaining = admitted |> List.ofSeq |> List.sortBy (fun m -> (m.Unit, m.Ordinal))
        while not remaining.IsEmpty do
            let first = remaining.Head
            let p = first.Unit
            let availableAt (m: SfMember) =
                m.FreeVars |> Set.forall (fun v -> match boundAt.TryGetValue v with | true, b -> b < p | _ -> true)
            let crossesWrite (m: SfMember) = seq { p .. m.Unit } |> Seq.exists (fun i -> unitWrites.[i])
            let ok (m: SfMember) =
                m.Extent = first.Extent && availableAt m && not (crossesWrite m)
            for m in remaining.Tail do
                if m.Extent = first.Extent && not (ok m) then
                    if not (availableAt m) then
                        rejected.Add $"{m.Desc}: an operand, seed or capture is bound after {first.Desc}, so the two cannot share one join point"
                    else
                        rejected.Add $"{m.Desc}: a statement between {first.Desc} and it may write (a call or an assignment), so it cannot move to one join point"
            let firstOk =
                first.FreeVars |> Set.forall (fun v -> match boundAt.TryGetValue v with | true, b -> b < p | _ -> true)
            let take, rest =
                if not firstOk then [], remaining.Tail
                else
                    let others = remaining.Tail |> List.filter ok
                    let taken = others |> List.map (fun m -> (m.Unit, m.Ordinal)) |> Set.ofList
                    let rest = remaining.Tail |> List.filter (fun m -> not (Set.contains (m.Unit, m.Ordinal) taken))
                    (first :: others), rest
            if take.Length >= 2 then groups.Add take
            remaining <- rest

        // Decide each group.
        let decideGroup (g: SfMember list) : Result<unit, string> =
            let legs = g |> List.collect (fun m -> m.Legs)
            // THE THREAD GATE, relaxed to the threaded join's own policy (G7):
            // in a threading licensed build a requesting leg may join only when
            // EVERY leg carries its own licence and the threaded join is on --
            // then the synthesized join keeps each leg's own request (none is
            // manufactured) and threads under the joined Path B form.
            // Otherwise the requesting leg would lose its team: decline.
            // The synthesized traversal must pass the threaded join's ACTUAL
            // admission (the same knob, licence, operator and purity predicates
            // `joinedFoldThreadPlan` applies; the dense literal-extent shape is
            // this pass's own admission) BEFORE it replaces a requesting fold:
            // a join the emitter would then refuse would serialize a fold
            // that threads on its own today. On any failure the originals stay.
            let threadedJoinRefusal () : string option =
                let ops = legs |> List.map (fun l -> l.Op) |> List.distinct
                if not (Blade.Toolchain.joinThreadsEnabled ()) then
                    Some "a leg requests threads in this build and BLADE_JOIN_THREADS=0 keeps a reduction join serial; the leg keeps its own team"
                elif not (legs |> List.forall (fun l -> l.Licensed)) then
                    Some "a leg requests threads in this build but not every leg carries its own reorder licence; the threaded join (G7) needs every leg licensed, so the requesting leg keeps its own team"
                elif not (ops = [ Some IRAdd ] || ops = [ Some IRMul ]) then
                    Some "a leg requests threads in this build but the legs do not all fold with ONE builtin `+` or `*`; the threaded join (G7) has no form for that, so the requesting leg keeps its own team"
                else
                    match legs |> List.tryPick (fun l -> match l.Audit with Error why -> Some why | Ok () -> None) with
                    | Some why ->
                        Some $"a leg requests threads in this build but a leg's map kernel is outside the threaded join's purity/totality whitelist ({why}); the joined team would be refused, so the requesting leg keeps its own team"
                    | None -> None
            let threadRefusal =
                if threadsBuild && legs |> List.exists (fun l -> l.Threads) then threadedJoinRefusal () else None
            if threadRefusal.IsSome then
                Error threadRefusal.Value
            elif reorderingBuild && (legs |> List.exists (fun l -> l.Licensed)) && (legs |> List.exists (fun l -> not l.Licensed)) then
                let nl = legs |> List.filter (fun l -> l.Licensed) |> List.length
                Error $"legs disagree on the reorder licence in a reordering build ({nl} licensed, {legs.Length - nl} unlicensed); one traversal has one order, so the join would serialize the licensed legs"
            elif reorderingBuild && (legs |> List.forall (fun l -> l.Licensed))
                 && (legs |> List.map (fun l -> l.Op) |> List.distinct) |> (fun ops -> ops.Length <> 1 || ops.Head.IsNone) then
                Error "the licensed legs fold with different or non-builtin operators; the joined C++ form takes one builtin operator for every leg"
            else Ok ()

        // Distinct legs of a group (duplicates read one leg) and each member's
        // leg indexes.
        let layout (g: SfMember list) =
            let legs = ResizeArray<SfLeg>()
            let keys = ResizeArray<IRExpr option>()
            let idx =
                g |> List.map (fun m ->
                    match m.Key, m.Legs with
                    | Some key, [ leg ] ->
                        (match keys |> Seq.tryFindIndex (fun k -> k = Some key) with
                         | Some i -> [ i ]
                         | None ->
                            legs.Add leg
                            keys.Add(Some key)
                            [ legs.Count - 1 ])
                    | _, ls ->
                        ls |> List.map (fun l ->
                            legs.Add l
                            keys.Add None
                            legs.Count - 1))
            (List.ofSeq legs, idx)

        let inlineKeyOf (k: SfUnitKey) = match k with SfInl (i, c, _) -> Some (i, c) | SfOrig _ -> None
        let inlineDisp (key: int * int) = match inlineCache.TryGetValue key with | true, Ok (_, _, d) -> d | _ -> "?"
        let evidenceOf (now: bool) (g: SfMember list) (legs: SfLeg list) (dups: int) : string list =
            let opNames = g |> List.collect (fun m -> m.Operands) |> List.distinct |> List.map nameOf |> String.concat ", "
            let first = g.Head
            let inl =
                g |> List.choose (fun m -> inlineKeyOf m.UnitKey) |> List.distinct
                |> List.map (fun k ->
                    if now then $"{inlineDisp k} inlined (pure-total, scalar return)"
                    else $"{inlineDisp k} would be inlined (pure-total, scalar return); left as a call")
            let notes = legs |> List.mapi (fun j l -> (j, l.SeedNote)) |> List.choose (fun (j, n) -> n |> Option.map (fun x -> (j, x)))
            let seedLines =
                if notes.IsEmpty then []
                else
                    let ops = notes |> List.map (fun (_, (o, _)) -> o) |> List.distinct |> String.concat " / "
                    let seeds = notes |> List.map (fun (_, (_, s)) -> s) |> List.distinct |> String.concat " / "
                    [ $"unseeded {ops} legs seeded {seeds}: bit-exact for every reachable value; signalling-NaN state is not preserved and is outside the arithmetic contract (formalism 2.4); the per-leg identity seed is exact, so the joined result equals the separate folds' under serial (repro) evaluation -- a licensed build may reassociate and lose the sign of an exact zero" ]
                    @ (notes |> List.map (fun (j, (o, s)) -> $"leg {j}: unseeded {o} -> seed {s} (identity; bit-exact)"))
            let lic = legs |> List.map (fun l -> if l.Licensed then "licensed" else "unlicensed") |> String.concat ", "
            [ $"{legs.Length} folds over {{{opNames}}} share one traversal ({first.Axis}: {first.Extent} cells)" ]
            @ (if dups > 0 then [ $"{dups} repeated fold(s) read one leg" ] else [])
            @ inl @ seedLines @ [ $"per-leg licences kept: {lic}" ]

        let resolveNames (s: string) =
            System.Text.RegularExpressions.Regex.Replace(s, "\u0002(-?[0-9]+)\u0003", fun m -> nameOf (int m.Groups.[1].Value))
        let record (outcome: Blade.Effects.DecisionOutcome) (ev: string list) =
            let outcome =
                match outcome with
                | Blade.Effects.Declined why -> Blade.Effects.Declined (resolveNames why)
                | o -> o
            Blade.Effects.Decisions.record
                { Blade.Effects.Rule = "sibling-folds"; Version = 1
                  Span = Blade.Ast.noSpan; Subject = subject
                  Outcome = outcome; Evidence = ev |> List.map resolveNames }

        let applied = ResizeArray<SfMember list * SfLeg list * int list list>()
        let mutable anyGroupRecord = false
        // Lifetime capability is a decision about one attempted inline, not
        // about the independent sibling groups that may still be joined.
        // judgeAt memoizes (statement, call ordinal), so enumerate that cache
        // once: discovery/reconstruction cannot duplicate the refusal, and
        // textually identical calls at different positions remain distinct.
        for KeyValue ((statement, ordinal), result) in inlineCache do
            match result with
            | Error why when why.Contains "lifetime event not honoured by"
                             && (match refusedCallFolds.TryGetValue((statement, ordinal)) with true, n -> n > 0 | _ -> false) ->
                anyGroupRecord <- true
                record (Blade.Effects.Declined why)
                    [ $"retained-inline candidate at region statement {statement + 1}, call {ordinal + 1}: the original call remains; independent sibling groups may still share a traversal" ]
            | _ -> ()
        for g in groups do
            let (legs, idx) = layout g
            let memberCount = g |> List.sumBy (fun m -> m.Legs.Length)
            let dups = memberCount - legs.Length
            if legs.Length < 2 then
                anyGroupRecord <- true
                record (Blade.Effects.Declined "the folds are one repeated value (let-level CSE's case, not a join)") []
            else
            let extra = (rejected |> Seq.map (fun r -> $"not joined: {r}") |> List.ofSeq) @ refusals
            match decideGroup g with
            | Error why ->
                anyGroupRecord <- true
                record (Blade.Effects.Declined why) (evidenceOf false g legs dups @ extra)
            | Ok () when not enabled ->
                anyGroupRecord <- true
                record (Blade.Effects.Declined "disabled by BLADE_SIBLING_FOLDS") (evidenceOf false g legs dups @ extra)
            | Ok () ->
                anyGroupRecord <- true
                record Blade.Effects.Applied (evidenceOf true g legs dups @ extra)
                applied.Add((g, legs, idx))
        if not anyGroupRecord then
            let reasons = (List.ofSeq rejected) @ refusals
            match reasons with
            | [] ->
                let exts = admitted |> Seq.map (fun m -> string m.Extent) |> Seq.distinct |> String.concat ", "
                record (Blade.Effects.Declined $"no two folds share one dense axis and one join point (extents {exts})") []
            | r :: rest -> record (Blade.Effects.Declined r) rest

        if applied.Count = 0 then body else

        // ---- A7c: producer-input legs (rule `epilogue-legs` v1, gap G69) -----
        // An applied group whose every leg is `reduce(a, k[, s])` of a stored
        // array, after a rank-1 producer of this region whose cell epilogue
        // B1 will take (`epilogueReserved`: an original statement, or a let of
        // a call body the tentative spine inlined -- saxpy's `kernel(x, y)`)
        // and whose operands (or output) the legs read, is judged by
        // producerEpilogueLegAdmission. Admitted with the gate on, the call
        // holding the producer is inlined FOR the legs (the region then has
        // no call boundary between them), and the plan is recorded once the
        // join has its id (below). Nothing else changes: the join stays in
        // the IR, so the interpreter -- and any lane that does not carry the
        // legs -- runs it as written.
        let legPlans = System.Collections.Generic.Dictionary<int, IRId * ApplyInfo * EpilogueLegSpec list * string option>()
        // One entry per inlined CALL kept for legs (several plans may share it):
        // its pools, released once at the call's return boundary.
        let legInlines = System.Collections.Generic.Dictionary<int * int, (IRId * IRType) list * string>()
        if cellEpilogueOn () then
            let legFacts : EpilogueLegFacts =
                let schedWrites = scheduleMayWriteIn programFuncs modul
                let schedWritesCb = scheduleMayWriteWithCallbacksIn programFuncs modul
                let assignsTo (id: IRId) (e: IRExpr) =
                    let mutable hit = false
                    iterIRExpr (fun n ->
                        match n with
                        | IRAssign (target, _) when Set.contains id (collectVarRefsIR target) -> hit <- true
                        | _ -> ()) e
                    hit
                { Resolve = (fun e -> resolveCallableIn funcs (fst (peelReynolds e)))
                  Writes = (fun e -> schedWrites e || mayWrite e)
                  CallbackWrites = schedWritesCb
                  Repeatable = (fun e -> (facts e).Repeatable)
                  SeedTotal = sfTotal Set.empty
                  PayloadOf = intervalPayloadOf modul
                  ProviderEffect = intervalHasProviderEffect modul
                  AssignedAnywhere = (fun id ->
                      (modul.Bindings |> List.exists (fun b -> bindingPayload modul b |> List.exists (assignsTo id)))
                      || (modul.Functions |> List.exists (fun f -> assignsTo id f.Body))
                      || (tentative |> Array.exists (fun u -> assignsTo id u.Value)))
                  ThreadsBuild = Blade.Toolchain.ompThreadEmissionEnabled ()
                  FreeVarsOf = freeVarsOf
                  OperandNotFresh = (fun id ->
                      let fresh (v: IRExpr) =
                          match v with
                          | IRCompute _ | IRArrayLit _ -> None
                          | IRApp (IRVar (f, _), _, _) when Map.containsKey f funcs -> None
                          | IRVar (y, _) -> Some $"an alias of `{nameOf y}`"
                          | IRApp (IRVar (y, _), _, _) -> Some $"a named view into `{nameOf y}`"
                          | _ -> Some "bound to a value that is not fresh storage of its own"
                      match boundAt.TryGetValue id with
                      | true, ui -> fresh tentative.[ui].Value
                      | _ ->
                          if regionParams.Contains id then None
                          else
                              match modul.Bindings |> List.tryFind (fun b -> b.Id = id) with
                              | Some b -> fresh b.Value
                              | None -> Some "bound outside this region and module") }
            let rec collapseC (e: IRExpr) = match e with IRCompute i -> collapseC i | e -> e
            let taken = System.Collections.Generic.HashSet<IRId>()
            let fmtSeed (s: IRExpr) =
                match s with
                | IRLit (IRLitFloat f) when System.Math.Abs f < 1e15 && f = System.Math.Floor f -> sprintf "%.1f" f
                | IRLit (IRLitFloat f) -> sprintf "%.17g" f
                | IRLit (IRLitInt n) -> string n
                | IRVar (id, _) -> nameOf id
                | _ -> "a total expression"
            applied |> Seq.iteri (fun gi (g, legs, _) ->
                if legs |> List.forall (fun l -> l.Source.IsSome) then
                    let joinAt = (g |> List.minBy (fun m -> (m.Unit, m.Ordinal))).Unit
                    let legOps = legs |> List.map (fun l -> let (a, _, _) = l.Source.Value in a)
                    let producers =
                        [ 0 .. tentative.Length - 1 ]
                        |> List.choose (fun ci ->
                            if not (epilogueReserved ci 0 tentative.[ci].Value) then None
                            else
                                match oneCellConsumerOf tentative.[ci].Value, tentative.[ci].Value with
                                | Some (pId, cinfo, ck), IRReduceCompute (_, ck2, cseed) ->
                                    (match boundAt.TryGetValue pId with
                                     | true, pu when pu < joinAt -> Some (pu, pId, (cinfo, ck, ck2, cseed))
                                     | _ -> None)
                                | _ -> None)
                        |> List.distinctBy (fun (pu, pId, _) -> (pu, pId))
                    let reads (pu: int, pId: IRId, b1) =
                        match collapseC tentative.[pu].Value with
                        | IRApplyCombinator pinfo ->
                            let ops = pinfo.Arrays |> List.choose (function IRVar (id, _) -> Some id | _ -> None)
                            if legOps |> List.exists (fun a -> a = pId || List.contains a ops) then Some (pu, pId, pinfo, b1) else None
                        | _ -> None
                    match producers |> List.choose reads |> List.sortBy (fun (pu, _, _, _) -> -pu) with
                    | [] -> ()
                    | (pu, pId, pinfo, (cinfo, ck, fkExpr, b1Seed)) :: _ ->
                    let pName = nameOf pId
                    let specs =
                        legs |> List.map (fun l ->
                            let (a, k, s) = l.Source.Value
                            let opName = nameOf a
                            let suffix = match l.Op with Some IRAdd -> "sum" | Some IRMul -> "prod" | _ -> "fold"
                            let seedText = match l.SeedNote with Some (_, txt) -> txt | None -> fmtSeed s
                            let spec : EpilogueLegSpec =
                                { Name = $"{opName}-{suffix}"; OperandId = a; OperandName = opName; Kernel = k; Seed = s
                                  SeedText = seedText; Licensed = reorderingBuild && l.Licensed }
                            spec)
                    let names = specs |> List.map (fun s -> s.Name) |> String.concat ", "
                    // Finding 5: a builtin `(+)` / `(*)` leg carries its OWN licence
                    // (formalism 2.4's recognised builtins), so it is strict only in
                    // the repro build; the record says which, per build.
                    let form =
                        if specs |> List.forall (fun s -> not s.Licensed) then "strict chains, ascending, own seeds"
                        elif specs |> List.forall (fun s -> s.Licensed) then
                            "builtin-licensed: each leg its own licence, its own K-lane state (C++) / fold flags (LLVM) and seed, never another leg's or the map's; strict under repro"
                        else "strict legs as ascending chains, licensed legs as their own K-lane states, each its own seed"
                    let headline = $"legs {names} ride {pName}'s producer pass ({form})"
                    let licenceNote =
                        if not reorderingBuild then "licences: the repro build keeps every leg strict (a builtin `(+)` / `(*)` leg is licensed only outside repro)"
                        else
                            specs |> List.map (fun s -> $"{s.Name}: " + (if s.Licensed then "licensed (its own: a builtin or commutative kernel)" else "strict (no licence of its own)")) |> String.concat ", " |> sprintf "licences: %s"
                    let region : EpilogueLegRegion =
                        { Subject = subject; ProducerName = pName; ProducerId = pId; ProducerInfo = pinfo
                          Stmts = tentative |> Array.map (fun u ->
                                    (u.Binder, (match u.Binder with Some id -> nameOf id | None -> "the region's result"), u.Value))
                          ProducerAt = pu; JoinAt = joinAt
                          MemberAts = g |> List.map (fun m -> m.Unit) |> List.distinct |> List.sort
                          Legs = specs }
                    let inlineKey = match tentative.[pu].Key with SfInl (i, c, _) -> Some (i, c) | SfOrig _ -> None
                    // The inlined callee's ARRAY lets (its stored pools), freed at the
                    // call's IRReleasePools event in both lanes.
                    let inlinedPools =
                        match inlineKey with
                        | Some key ->
                            tentative |> Array.toList |> List.choose (fun u ->
                                match u.Key, u.Binder, u.Value with
                                | SfInl (i, c, _), Some id, (IRCompute _ as v) when (i, c) = key -> Some (id, typeOf v)
                                | _ -> None)
                        | None -> []
                    let inlNote =
                        match inlineKey with
                        | Some key ->
                            let pools = inlinedPools |> List.map (fst >> nameOf) |> String.concat ", "
                            [ $"{inlineDisp key} inlined one level so the producer and the legs share one region (no call boundary between them); its pools ({pools}) are released once by an IR-level return event after the scalar return is evaluated; both statement-rendered lanes honour the event, independently of join placement" ]
                        | None -> []
                    // FINDING 4: the inline is kept only when BOTH lanes' fused forms
                    // can carry the legs in this build -- the facts each emitter's own
                    // gate reads (CodeGenCuda.buildFlatEpilogue / attachEpilogueLegs,
                    // EmitLlvm.tryBindFlatEpilogueProducer), stated here so a planned
                    // inline never outlives a route no lane emits.
                    let routeProblem () : string option =
                        let fk = kernelOf fkExpr
                        let elemOf (t: IRType) = match stripUnits t with IRTScalar et -> Some et | ArrayElem at -> (match stripUnits at.ElemType with IRTScalar et -> Some et | _ -> None) | _ -> None
                        let realFloat (et: ElemType option) = (et = Some ETFloat64 || et = Some ETFloat32)
                        let lanesOff () =
                            match System.Environment.GetEnvironmentVariable "BLADE_FOLD_LANES" with
                            | null -> false
                            | v -> List.contains (v.Trim().ToLowerInvariant()) [ "0"; "off"; "false"; "no" ]
                        match fk with
                        | None -> Some "the producer's epilogue fold kernel does not resolve"
                        | Some fk ->
                        let b1Op = builtinOp fk
                        let b1Licensed = reorderingBuild && licensed fk
                        let b1Elem = elemOf fk.RetType
                        let cellElem = elemOf pinfo.OutputType
                        let b1Request = fk.IsOmpParallel || ck.IsOmpParallel
                        match b1Seed with
                        | IRLit _ when b1Op = Some IRAdd || b1Op = Some IRMul ->
                            if cellElem <> b1Elem then Some "the producer's cell type differs from its epilogue fold's accumulator type (no lane fuses it)"
                            elif b1Licensed && b1Op <> Some IRAdd then Some "the epilogue fold is licensed but not a builtin `+` (the C++ lane-state form takes `+` only)"
                            elif b1Licensed && not (realFloat b1Elem) then Some "the epilogue fold is licensed over a non-floating element (no lane-state form)"
                            elif b1Licensed && lanesOff () then Some "the epilogue fold is licensed and BLADE_FOLD_LANES=off (the C++ lane keeps its two-pass fold)"
                            elif blasGateProbe () && not b1Request then Some "the BLAS gate is on and the epilogue fold carries no omp request (the C++ L1 dispatch may own it, in every build)"
                            else
                            specs |> List.tryPick (fun s ->
                                let lk = kernelOf s.Kernel
                                let lElem = lk |> Option.bind (fun c -> elemOf c.RetType)
                                let opElem =
                                    pinfo.Arrays |> List.tryFindIndex (function IRVar (id, _) -> id = s.OperandId | _ -> false)
                                    |> Option.bind (fun pos -> List.tryItem pos pinfo.ArrayTypes)
                                    |> Option.bind (fun at -> match stripUnits at.ElemType with IRTScalar et -> Some et | _ -> None)
                                match lk with
                                | None -> Some $"leg {s.Name}'s kernel does not resolve"
                                | Some lc ->
                                if s.Licensed && not b1Licensed then Some $"leg {s.Name} is licensed but the epilogue keeps its ordered chain in this build (v1 feeds a licensed leg only beside the lane-state form)"
                                elif s.Licensed && (builtinOp lc <> Some IRAdd || not (realFloat lElem) || lanesOff ()) then Some $"leg {s.Name} is licensed but takes no lane-state form (not a floating builtin `+`, or BLADE_FOLD_LANES=off)"
                                elif lElem.IsNone || lElem <> opElem then Some $"leg {s.Name}'s accumulator type is not its operand's element type"
                                else None)
                        | IRLit _ -> Some "the producer's epilogue fold kernel is not a builtin `+` / `*` (both lanes' fused loops evaluate it ahead of the intervening bindings)"
                        | _ -> Some "the producer's epilogue fold seed is not a literal (both lanes' fused loops evaluate it ahead of the producer)"
                    match producerEpilogueLegAdmission legFacts region with
                    | Error (why, rows) ->
                        recordEpilogueLegs subject (Blade.Effects.Declined (resolveNames why)) ((headline :: rows) |> List.map resolveNames)
                    | Ok rows when not (epilogueLegsEnabled ()) ->
                        recordEpilogueLegs subject (Blade.Effects.Declined "disabled by BLADE_EPILOGUE_LEGS") ((headline :: rows) |> List.map resolveNames)
                    | Ok rows when taken.Contains pId ->
                        recordEpilogueLegs subject (Blade.Effects.Declined $"another join's legs already ride `{pName}`'s producer pass") ((headline :: rows) |> List.map resolveNames)
                    | Ok rows ->
                    match routeProblem () with
                    | Some why ->
                        recordEpilogueLegs subject (Blade.Effects.Declined ("no lane would carry the legs in this build: " + why + "; nothing is inlined for them"))
                            ((headline :: rows) |> List.map resolveNames)
                    | None ->
                        taken.Add pId |> ignore
                        (match inlineKey with
                         | Some key -> legInlines.[key] <- (inlinedPools, inlineDisp key)
                         | None -> ())
                        recordEpilogueLegs subject Blade.Effects.Applied
                            ((headline :: [ licenceNote; "planner admission (each lane's emitter records whether its fused form carried the legs)" ] @ inlNote @ rows) |> List.map resolveNames)
                        legPlans.[gi] <- (pId, pinfo, specs, (inlineKey |> Option.map inlineDisp)))

        // ---- rewrite on the spine with only the contributing inlines ---------
        let keptInlines =
            applied |> Seq.collect (fun (g, _, _) -> g |> List.choose (fun m -> inlineKeyOf m.UnitKey)) |> Set.ofSeq
            |> Set.union (Set.ofSeq legInlines.Keys)
        let finalUnits = buildUnits (fun k -> Set.contains k keptInlines)
        // A01: positions are used only while constructing the IR, never saved
        // as lifetime facts. Emit events AFTER reconstruction, including behind
        // a deleted final join. Every retained inline gets one region (also the
        // older join-driven inline), rather than one region per leg plan.
        let starts = System.Collections.Generic.Dictionary<SfUnitKey, IRId * string>()
        let ends = System.Collections.Generic.Dictionary<SfUnitKey, IRId * IRExpr list * string>()
        for key in keptInlines do
            let us = finalUnits |> List.filter (fun u -> inlineKeyOf u.Key = Some key)
            let pools =
                us |> List.choose (fun u ->
                    match u.Binder, u.Value with
                    | Some id, IRCompute (IRApplyCombinator _ as v) ->
                        match stripUnits (typeOf v) with
                        | ArrayElem _ -> Some (IRVar (id, typeOf v))
                        | _ -> None
                    | _ -> None)
            if not pools.IsEmpty then
                let sid = builder.FreshId()
                let call = resolveNames (inlineDisp key)
                starts.[us.Head.Key] <- (sid, call)
                ends.[(List.last us).Key] <- (sid, pools, call)
        let replace = System.Collections.Generic.Dictionary<SfUnitKey * int, IRExpr>()
        let dropUnits = System.Collections.Generic.HashSet<SfUnitKey>()
        let projOf = System.Collections.Generic.Dictionary<IRId, IRId list * IRType list>()
        let inserts = System.Collections.Generic.Dictionary<SfUnitKey, (IRId * IRExpr) list>()
        let groupAt = ref -1
        for (g, legs, idx) in applied do
            groupAt.Value <- groupAt.Value + 1
            let built = legs |> List.map (fun l -> l.Build ())
            let comp = built |> List.map (fun (lf, _, _) -> lf) |> List.reduce (fun a b -> IRFusion (a, b))
            let rc = IRReduceCompute (comp, IRTuple (built |> List.map (fun (_, k, _) -> k)), IRTuple (built |> List.map (fun (_, _, s) -> s)))
            let jId = builder.FreshId()
            let jTy = typeOf rc
            let pIds = legs |> List.map (fun _ -> builder.FreshId())
            let pTys = legs |> List.map (fun l -> l.ResultType)
            // A7c: the admitted plan, keyed by the producer and by this join.
            (match legPlans.TryGetValue groupAt.Value with
             | true, (pId, pinfo, specs, inlinedCall) ->
                let opIds = pinfo.Arrays |> List.map (function IRVar (id, _) -> id | _ -> -1)
                let plan : EpilogueLegPlan =
                    { ProducerId = pId; JoinId = jId
                      Legs =
                        List.zip specs pTys |> List.mapi (fun slot (s, ty) ->
                            let pos = opIds |> List.findIndex (fun id -> id = s.OperandId)
                            { Slot = slot; OperandPos = pos
                              OperandId = s.OperandId; Kernel = s.Kernel; Seed = s.Seed; ElemType = ty
                              OperandElem = (match List.tryItem pos pinfo.ArrayTypes with Some at -> at.ElemType | None -> ty)
                              Licensed = s.Licensed; Name = s.Name; SlotId = builder.FreshId() })
                      ProducerName = nameOf pId; Subject = subject
                      InlinedCall = inlinedCall |> Option.map resolveNames
                      EmittedCpp = ref None; DecidedCpp = ref false; DecidedLlvm = ref false }
                EpilogueLegTable.record plan |> ignore
             | _ -> ())
            let lets =
                (jId, rc) :: (List.mapi (fun i pid -> (pid, IRTupleProj (IRVar (jId, jTy), i, false))) pIds)
            let earliest = (g |> List.minBy (fun m -> (m.Unit, m.Ordinal))).UnitKey
            inserts.[earliest] <- (match inserts.TryGetValue earliest with | true, l -> l | _ -> []) @ lets
            List.iter2 (fun (m: SfMember) (ix: int list) ->
                match m.Join with
                | Some jid ->
                    dropUnits.Add m.UnitKey |> ignore
                    projOf.[jid] <- (ix |> List.map (fun i -> pIds.[i]), ix |> List.map (fun i -> pTys.[i]))
                | None ->
                    let i = ix.Head
                    replace.[(m.UnitKey, m.Ordinal)] <- IRVar (pIds.[i], pTys.[i])) g idx
        let fixProj (e: IRExpr) =
            if projOf.Count = 0 then e
            else
                mapIRExpr (fun n ->
                    match n with
                    | IRTupleProj (IRVar (jid, _), k, _) when projOf.ContainsKey jid ->
                        let (ps, ts) = projOf.[jid]
                        if k < ps.Length then IRVar (ps.[k], ts.[k]) else n
                    | _ -> n) e
        let out = ResizeArray<IRId option * IRExpr>()
        for u in finalUnits do
            (match starts.TryGetValue u.Key with
             | true, (sid, call) -> out.Add((Some sid, IRBeginPoolScope call))
             | _ -> ())
            (match inserts.TryGetValue u.Key with
             | true, lets -> for (id, v) in lets do out.Add((Some id, v))
             | _ -> ())
            if not (dropUnits.Contains u.Key) then
                let v' =
                    sfWalk isOwnCall (fun ord n ->
                        match replace.TryGetValue((u.Key, ord)) with
                        | true, r -> r
                        | _ -> n) (fun _ c -> c) u.Value
                out.Add((u.Binder, fixProj v'))
            (match ends.TryGetValue u.Key with
             | true, (sid, pools, call) ->
                 out.Add((Some (builder.FreshId()), IRReleasePools (IRVar (sid, IRTUnit), pools, call)))
             | _ -> ())
        let items = List.ofSeq out
        let (_, tail) = List.last items
        List.foldBack (fun (b, v) acc ->
            match b with
            | Some id -> IRLet (id, v, acc)
            | None -> acc) (items |> List.take (items.Length - 1)) tail

    let quickHasFold (e: IRExpr) =
        let mutable n = 0
        iterIRExpr (fun x ->
            match x with
            | IRReduce _ | IRReduceCompute _ | IRProdSum _ -> n <- n + 1
            | IRApp _ when isOwnCall x -> n <- n + 1
            | _ -> ()) e
        n >= 2
    let functions' =
        modul.Functions |> List.map (fun f ->
            if f.Name.StartsWith "__lambda_" || f.IsStatic || f.IsArityPoly || f.IsCudaKernel || f.IsMpiParallel
               || hasTypeVarsInSignature f || not (quickHasFold f.Body) then f
            elif f.IsRepro then
                Blade.Effects.Decisions.record
                    { Blade.Effects.Rule = "sibling-folds"; Version = 1; Span = Blade.Ast.noSpan; Subject = f.SourceName
                      Outcome = Blade.Effects.Declined "the region is a `where repro` function (v1 leaves its folds as written)"
                      Evidence = [] }
                f
            else
                let ps = f.Params |> List.map (fun p -> p.VarId) |> Set.ofList
                { f with Body = processRegion f.SourceName (Some f.Id) ps f.Body })
    let bindings' =
        modul.Bindings |> List.map (fun b ->
            if quickHasFold b.Value then { b with Value = processRegion b.Name None Set.empty b.Value } else b)
    if minted.Count = 0 then { modul with Functions = functions'; Bindings = bindings' }
    else
        let byOrigin = minted |> Seq.choose (fun (c, o) -> o |> Option.map (fun oid -> (oid, c))) |> Seq.groupBy fst |> Map.ofSeq
        let loose = minted |> Seq.filter (fun (_, o) -> o.IsNone) |> Seq.map fst |> List.ofSeq
        { modul with
            Functions =
                (functions' |> List.collect (fun f ->
                    match Map.tryFind f.Id byOrigin with
                    | Some cs -> f :: (cs |> Seq.map snd |> List.ofSeq)
                    | None -> [ f ])) @ loose
            Bindings = bindings'
            DerivedFuncOrigins =
                minted |> Seq.fold (fun acc (c, o) ->
                    match o with
                    | Some oid -> Map.add c.Id oid acc
                    | None -> acc) modul.DerivedFuncOrigins }

/// PM10: one private scoped rank-2 scalar map, immediately consumed by a
/// literal-seeded partial fold. Reuse the existing row apply + scalar fused
/// terminal; neither a producer pool nor a new backend operation is needed.
/// Top-level named producers are deliberately outside this first admission.
let internal fusePartialRowTerminalsWithCensusModule (builder: IRBuilder) (programFuncs: IRCallable list)
                                                    (foreignCensus: Set<IRId> option) (modul: IRModule) : IRModule =
    let funcs = (programFuncs @ modul.Functions) |> List.map (fun f -> f.Id, f) |> Map.ofList
    let resolve = resolveCallableIn funcs
    let writes = scheduleMayWriteIn programFuncs modul
    let lifetimeBarrier = scheduleHasLifetimeBarrierIn programFuncs modul
    let allCallables = (modul.Functions @ programFuncs) |> List.distinctBy (fun f -> f.Id)
    let roots = compactionRoots allCallables modul
    let minted = ResizeArray<IRCallable * IRId>()
    let scalar64 = IRTScalar ETFloat64
    let plain (ix: IRIndexType) =
        ix.Rank = 1 && ix.IxKind = IxKPlain && ix.Symmetry = SymNone && ix.Dependencies.IsEmpty
        && (match IRTotality.evalInt ix.Extent with Some n -> n >= 0L | None -> false)
    let arrayOf rank ty =
        match ty with
        | ArrayElem at when at.ElemType = scalar64 && not at.IsVirtual
                            && at.IndexTypes.Length = rank && List.forall plain at.IndexTypes -> Some at
        | _ -> None
    let sameAxes (a: IRArrayType) (b: IRArrayType) =
        a.IndexTypes.Length = b.IndexTypes.Length && List.forall2 IRTotality.sameIndex a.IndexTypes b.IndexTypes
    let unstrategized (c: IRCallable) =
        not c.IsStatic && not c.IsArityPoly && not c.IsOmpParallel && c.Parallelism.IsEmpty
        && not c.IsCudaKernel && not c.IsMpiParallel
    // Totality alone permits immutable free scalar reads. This motion also
    // requires a CLOSED callable graph: params/local lets/callees only, no
    // captures, module variables, callbacks, recursion or unknown functions.
    let closedTotal (root: IRCallable) : Result<unit, string> =
        let rec audit visiting (c: IRCallable) =
            if visiting |> Set.contains c.Id then Error "a recursive callable"
            elif not (unstrategized c) then Error "a parallel/device/static/arity strategy"
            elif not c.Captures.IsEmpty then Error "a callable capture"
            elif writes c.Body then Error "a callable body may write"
            else
            match IRTotality.auditCallable resolve (fun _ -> None) (fun _ -> false) c [] with
            | Error why -> Error why
            | Ok () ->
                let mutable bound = c.Params |> List.map _.VarId |> Set.ofList
                iterIRExpr (function IRLet (id, _, _) -> bound <- bound.Add id | _ -> ()) c.Body
                let refs = collectVarRefsIR c.Body |> Set.filter (fun id -> not (bound.Contains id)) |> Set.toList
                let rec check = function
                    | [] -> Ok ()
                    | id :: rest ->
                        match Map.tryFind id funcs with
                        | None -> Error "an unbound/global/callback reference"
                        | Some callee ->
                            match audit (visiting.Add c.Id) callee with Error why -> Error why | Ok () -> check rest
                check refs
        audit Set.empty root
    let literalSeed = function
        | IRLit (IRLitFloat _) | IRUnaryOp (IRNeg, IRLit (IRLitFloat _)) -> true
        | _ -> false
    let rowFold (info: ApplyInfo) =
        match resolve info.Kernel with
        | Some row when row.Params.Length = 1 && row.RetType = scalar64 && row.Captures.IsEmpty && unstrategized row ->
            match row.Params.Head.Type, row.Body with
            | ArrayElem at, IRReduce (IRVar (id, ty), k, Some seed)
                when id = row.Params.Head.VarId && ty = row.Params.Head.Type
                     && at.ElemType = scalar64 && not at.IsVirtual && at.IndexTypes.Length = 1
                     && at.IndexTypes.Head.Rank = 1 && at.IndexTypes.Head.IxKind = IxKPlain
                     && at.IndexTypes.Head.Symmetry = SymNone && at.IndexTypes.Head.Dependencies.IsEmpty -> Some (row, k, seed)
            | _ -> None
        | _ -> None
    let record subject outcome evidence =
        Blade.Effects.Decisions.record
            { Blade.Effects.Rule = "partial-row-fold"; Version = 1; Span = declSpanOf subject
              Subject = subject; Outcome = outcome; Evidence = evidence }
    let peel = function IRCompute (IRApplyCombinator i) | IRApplyCombinator i -> Some i | _ -> None
    // PM25: the private hard transpose has no observer besides this exact
    // row apply. Manufacture the already-supported C range / R fused-fold
    // idiom, rather than changing the ownership or layout of IRTranspose.
    let transposeRows (host: IRBinding) pid sourceId sourceType (consumer: ApplyInfo) =
        let decide outcome evidence =
            Blade.Effects.Decisions.record
                { Blade.Effects.Rule = "transpose-row-fold"; Version = 1; Span = declSpanOf host.Name
                  Subject = host.Name; Outcome = outcome; Evidence = evidence }
        let unchanged why = decide (Blade.Effects.Declined why) []; host
        let exprDemand e =
            (collectVarRefsIR e).Contains pid
            || (embeddedExtentExprs e |> List.exists (fun x -> (collectVarRefsIR x).Contains pid))
        let typeDemand ty = irTypeExtentExprs ty |> List.exists exprDemand
        let residual =
            // The only exempted value is this host's two certified array
            // input occurrences (Arrays and MethodFor.Arrays). Keep their
            // types and every other payload, including reference-equal
            // expressions rooted in a different binding.
            mapIRExpr (function IRVar (id, ty) when id = pid -> IRZero ty | n -> n) host.Value
        let hasDemand =
            roots |> List.exists (function
                | RootBindingValue b -> exprDemand (if b.Id = host.Id then residual else b.Value)
                | RootSideOperand (_, _, e) -> exprDemand e
                | RootFunctionBody f -> exprDemand f.Body || (f.Captures |> List.exists (fun c -> c.Id = pid))
                | RootType (_, ty) -> typeDemand ty
                | RootSourceRef (_, id) -> id = pid
                | RootNameRef _ -> true)
        let mutable uses = 0
        iterIRExpr (function IRVar (id, _) when id = pid -> uses <- uses + 1 | _ -> ()) host.Value
        let sourceIndex = modul.Bindings |> List.tryFindIndex (fun b -> b.Id = sourceId)
        let hostIndex = modul.Bindings |> List.findIndex (fun b -> b.Id = host.Id)
        match foreignCensus with
        | None -> unchanged "requires explicit complete foreign-demand census evidence"
        | Some refs when refs.Contains pid -> unchanged "the private transpose is referenced by another module"
        | Some _ when not (gateOn "BLADE_FUSION") -> unchanged "disabled by BLADE_FUSION"
        | Some _ when not (Blade.Toolchain.fpReproEnabled ()) -> unchanged "requires the reproducible serial column graph (BLADE_FP_REPRO=1)"
        | Some _ when hasDemand || uses <> 2 -> unchanged "the private transpose has an additional value/type/capture/side-table demand"
        | Some _ when not (bindingSideOperands modul host.Id).IsEmpty
                      || modul.ProviderReads.ContainsKey host.Id || modul.ProviderWrites.ContainsKey host.Id ->
            unchanged "the host has an additional execution payload"
        | Some _ ->
            match arrayOf 2 sourceType, arrayOf 1 consumer.OutputType, consumer.Arrays, rowFold consumer, sourceIndex with
            | Some input, Some output, [IRVar (p, privateType)], Some (row, foldExpr, seed), Some si when p = pid && si < hostIndex ->
                let source = modul.Bindings.[si]
                let transposed = { input with IndexTypes = [input.IndexTypes.[1]; input.IndexTypes.[0]] }
                let storedRoles (a: IRArrayType) = a.IndexTypes |> List.forall (fun ix -> ix.Kind = SDimension)
                let knownArray roles expected ty =
                    match arrayOf 2 ty with
                    | Some a -> sameAxes a expected && (a.IndexTypes |> List.map _.Kind) = roles
                    | None -> false
                let metadataKnown =
                    consumer.KernelInputRanks = [1] && consumer.KernelOutputRank = 0 && not consumer.IsCoIteration
                    && not consumer.HasReynolds && consumer.KernelTDims.IsEmpty && consumer.SharedIndexTypes.IsEmpty
                    && consumer.SpeedupFactor = 1L && consumer.ReynoldsSpeedup = 1L
                    && consumer.TriangularLevels = [false] && consumer.SymcomStates = [SCNeither]
                    && consumer.SDimsPerArray = [1] && consumer.Identities.Length = 1
                    && consumer.ArrayTypes.Length = 1
                    && knownArray [SDimension; TDimension] transposed (mkArrayLike consumer.ArrayTypes.Head)
                    && knownArray [SDimension; SDimension] transposed privateType
                    && source.Type = sourceType && storedRoles input && storedRoles output
                    && (match arrayOf 1 host.Type with Some h -> sameAxes h output && storedRoles h | None -> false)
                    && IRTotality.sameIndex output.IndexTypes.Head input.IndexTypes.[1]
                    && (match consumer.Kernel with
                        | IRVar (id, ty) -> id = row.Id && ty = mkFuncArrow [row.Params.Head.Type] scalar64
                        | _ -> false)
                    && (match consumer.Loop with
                        | IRMethodFor mf ->
                            mf.TotalSDims = 2 && mf.SDimsPerArray = [2] && mf.SharedIndexTypes.IsEmpty
                            && mf.Identities = consumer.Identities && mf.ArrayTypes.Length = 1
                            && knownArray [SDimension; SDimension] transposed (mkArrayLike mf.ArrayTypes.Head)
                            && (match mf.Arrays with [IRVar (v, t)] -> v = pid && t = privateType | _ -> false)
                        | _ -> false)
                let payloadUnsafe b = bindingPayload modul b |> List.exists (fun e -> writes e || lifetimeBarrier e)
                if not metadataKnown then unchanged "the transpose/consumer/index metadata is outside the known plain column geometry"
                elif not (literalSeed seed) || typeOf seed <> scalar64 then unchanged "requires an explicit literal Float64 seed per column"
                elif not (modul.Functions |> List.exists (fun f -> f.Id = row.Id)) then unchanged "the row callable is not owned by this module"
                elif payloadUnsafe host then unchanged "a host payload may write or cross a pool lifetime event"
                else
                match resolve foldExpr, storedArrayProof modul source with
                | None, _ -> unchanged "the scalar fold is unresolved"
                | _, Error why -> unchanged $"source is not proved stored: {why}"
                | Some fold, Ok (_, chain) ->
                    if fold.Params.Length <> 2 || (fold.Params |> List.exists (fun p -> p.Type <> scalar64)) || fold.RetType <> scalar64
                       || (match foldExpr with IRVar (_, ty) -> ty <> mkFuncArrow [scalar64; scalar64] scalar64 | _ -> true) then
                        unchanged "the fold is outside the concrete scalar admission"
                    elif chain |> List.exists (fun b -> modul.ProviderReads.ContainsKey b.Id || payloadUnsafe b) then
                        unchanged "a stored source initializer/alias is a provider, may write or has a lifetime event"
                    else
                    match closedTotal fold with
                    | Error why -> unchanged $"the fold is not closed pure/total: {why}"
                    | Ok () ->
                        let rAxis = { input.IndexTypes.[0] with Kind = SDimension }
                        let cAxis = { input.IndexTypes.[1] with Kind = SDimension }
                        let coordinateType (ix: IRIndexType) =
                            let tag = match ix.Tag with Some n -> IRefNamed n | None -> IRefAnon (ix.Id, ix.Extent)
                            IRTIdxTagged (IRTScalar ETInt64, tag)
                        let param name (ix: IRIndexType) : IRParam =
                            let id = builder.FreshId ()
                            { Name = $"{name}_{id}"; VarId = id; Index = 0; Type = coordinateType ix }
                        let cp, rp = param "__transpose_c" cAxis, param "__transpose_r" rAxis
                        let cv, rv = IRVar (cp.VarId, cp.Type), IRVar (rp.VarId, rp.Type)
                        let sourceCapture: CaptureInfo = { Id = sourceId; Name = source.Name; Type = sourceType; IsMutable = source.IsMutable }
                        let columnCapture: CaptureInfo = { Id = cp.VarId; Name = cp.Name; Type = cp.Type; IsMutable = false }
                        // Bounds are established by construction: each coordinate
                        // ranges over the original source's EXACT index space.
                        let read = IRIndex (IRVar (sourceId, sourceType), [rv; cv], None)
                        let gather = mkCallable builder defaultLambdaOptions [rp] read scalar64 [sourceCapture; columnCapture] false [] [] false false 256 false
                        let gather = { gather with Name = $"__transpose_cell_{gather.Id}"; SourceName = row.SourceName }
                        let map (ix: IRIndexType) (kernel: IRCallable) outputType : ApplyInfo =
                            let range = IRRange ([ix], None)
                            let identity = AIDLiteral (builder.FreshId ())
                            let a = { ElemType = coordinateType ix; IndexTypes = [ix]; IsVirtual = true; Identity = None }
                            { Loop = IRMethodFor { Arrays = [range]; Identities = [identity]; ArrayTypes = [a]
                                                   SDimsPerArray = [1]; TotalSDims = 1; SharedIndexTypes = [] }
                              Kernel = IRVar (kernel.Id, mkFuncArrow (kernel.Params |> List.map _.Type) scalar64)
                              Arrays = [range]; Identities = [identity]; ArrayTypes = [a]; SharedIndexTypes = []
                              SymcomStates = [SCNeither]; TriangularLevels = [false]; SDimsPerArray = [1]
                              KernelInputRanks = [0]; KernelOutputRank = 0; KernelTDims = []
                              SpeedupFactor = 1L; ReynoldsSpeedup = 1L; HasReynolds = false
                              OutputType = outputType; IsCoIteration = false }
                        let innerType = mkArrayLike { ElemType = scalar64; IndexTypes = [rAxis]; IsVirtual = false; Identity = None }
                        let terminal = IRReduceCompute (IRApplyCombinator (map rAxis gather innerType), foldExpr, seed)
                        let column = mkCallable builder defaultLambdaOptions [cp] terminal scalar64 [sourceCapture] false [] [] false false 256 false
                        let column = { column with Name = $"__transpose_rows_{column.Id}"; SourceName = row.SourceName }
                        minted.Add (gather, row.Id)
                        minted.Add (column, row.Id)
                        decide Blade.Effects.Applied
                            [ "private hard-transpose pool removed; required stored source and original C output retained"
                              "C outer range / R inner fused terminal; exact original fold and literal seed once per column"
                              "same index identity, static in-bounds source reads; ascending reproducible association"
                              "complete value/type/capture/side-table/foreign census; immediate scope; no writes or lifetime motion" ]
                        { host with Value = IRCompute (IRApplyCombinator (map cAxis column host.Type)) }
            | _ -> unchanged "requires a dominating stored rank-2 Float64 source and one immediate seeded row fold"
    let rewrite (host: IRBinding) =
        let unchanged why = record host.Name (Blade.Effects.Declined why) []; host
        match host.Value with
        | IRLet (pid, IRTranspose (IRVar (sourceId, sourceType), 0, 1), IRCompute (IRApplyCombinator consumer)) ->
            transposeRows host pid sourceId sourceType consumer
        | IRLet (_, IRTranspose _, _) ->
            Blade.Effects.Decisions.record
                { Blade.Effects.Rule = "transpose-row-fold"; Version = 1; Span = declSpanOf host.Name; Subject = host.Name
                  Outcome = Blade.Effects.Declined "requires one private transpose immediately consumed by its sole row fold"; Evidence = [] }
            host
        | IRLet (pid, value, IRCompute (IRApplyCombinator consumer)) when (peel value).IsSome ->
            let producer = (peel value).Value
            match producer.Arrays, consumer.Arrays, rowFold consumer with
            | [ (IRVar (sourceId, sourceType) as source) ], [ IRVar (p, privateType) ], Some (row, foldExpr, seed) when p = pid ->
                let loopsKnown =
                    match producer.Loop, consumer.Loop with
                    | IRMethodFor a, IRMethodFor b ->
                        (match a.Arrays, b.Arrays with
                         | [IRVar (s, _)], [IRVar (v, _)] -> s = sourceId && v = pid
                         | _ -> false)
                    | _ -> false
                let sourceBinding = modul.Bindings |> List.tryFindIndex (fun b -> b.Id = sourceId)
                let hostIndex = modul.Bindings |> List.findIndex (fun b -> b.Id = host.Id)
                let hasDemand =
                    let mentions e = (collectVarRefsIR e).Contains pid
                    let expr e = mentions e || (embeddedExtentExprs e |> List.exists mentions)
                    let ty t = irTypeExtentExprs t |> List.exists expr
                    // Remove precisely this expression by identity, rather
                    // than exempting a name, an entire host or all its types.
                    let rec outside e =
                        if obj.ReferenceEquals(e, host.Value) then IRLit IRLitUnit
                        else rebuildWith e (childrenOf e |> List.map outside)
                    roots |> List.exists (function
                        | RootBindingValue b when b.Id = host.Id -> expr (outside b.Value)
                        | RootBindingValue b -> expr b.Value
                        | RootSideOperand (_, _, e) -> expr e
                        | RootFunctionBody f -> expr f.Body || (f.Captures |> List.exists (fun c -> c.Id = pid))
                        | RootType (_, t) -> ty t
                        | RootSourceRef (_, id) -> id = pid
                        // Local let names do not survive IR. A name-only
                        // provider operand cannot be certified by this census.
                        | RootNameRef _ -> true)
                let privateMentions =
                    let mutable n = 0
                    iterIRExpr (function IRVar (id, _) when id = pid -> n <- n + 1 | _ -> ()) host.Value
                    n
                let privateTypeDemand =
                    embeddedExtentExprs host.Value |> List.exists (fun e -> (collectVarRefsIR e).Contains pid)
                if not (gateOn "BLADE_FUSION") then unchanged "disabled by BLADE_FUSION"
                elif not (Blade.Toolchain.fpReproEnabled ()) then unchanged "requires the reproducible serial row graph (BLADE_FP_REPRO=1)"
                elif hasDemand || privateTypeDemand || privateMentions <> 2 then
                    unchanged "the private producer has an additional type/capture/side-table demand"
                elif not (literalSeed seed) then unchanged "requires an explicit literal Float64 seed per row"
                elif not loopsKnown then unchanged "an unsupported loop provenance payload survives"
                elif producer.KernelInputRanks <> [0] || producer.KernelOutputRank <> 0 || producer.IsCoIteration
                     || producer.HasReynolds || not producer.KernelTDims.IsEmpty
                     || producer.SpeedupFactor <> 1L || producer.ReynoldsSpeedup <> 1L
                     || producer.SymcomStates <> [SCNeither; SCNeither]
                     || producer.TriangularLevels <> [false; false] then
                    unchanged "the producer is not one plain scalar map"
                elif consumer.KernelInputRanks <> [1] || consumer.KernelOutputRank <> 0 || consumer.HasReynolds
                     || consumer.IsCoIteration || not consumer.KernelTDims.IsEmpty
                     || consumer.SpeedupFactor <> 1L || consumer.ReynoldsSpeedup <> 1L
                     || consumer.TriangularLevels <> [false] || consumer.SymcomStates <> [SCNeither] then
                    unchanged "the consumer is not one plain row fold"
                else
                match arrayOf 2 sourceType, arrayOf 2 producer.OutputType, arrayOf 1 consumer.OutputType,
                      resolve producer.Kernel, resolve foldExpr, sourceBinding with
                | Some input, Some mapped, Some result, Some map, Some fold, Some si when si < hostIndex ->
                    let sourceB = modul.Bindings.[si]
                    let proof = storedArrayProof modul sourceB
                    // Every omitted metadata payload is either the known
                    // source/row role or a static, identical plain axis. In
                    // particular no hidden shared/kernel axis may disappear.
                    let storedRoles (at: IRArrayType) = at.IndexTypes |> List.forall (fun ix -> ix.Kind = SDimension)
                    let denseMetadata (at: IRArrayType) =
                        at.ElemType = scalar64 && not at.IsVirtual && at.IndexTypes.Length = 2
                        && List.forall plain at.IndexTypes
                    let loopMetadata (info: ApplyInfo) expected =
                        match info.Loop with
                        | IRMethodFor mf ->
                            mf.TotalSDims = 2 && mf.SDimsPerArray = [2] && mf.SharedIndexTypes.IsEmpty
                            && mf.Identities = info.Identities && mf.ArrayTypes.Length = 1
                            && denseMetadata mf.ArrayTypes.Head
                            && sameAxes mf.ArrayTypes.Head expected && storedRoles mf.ArrayTypes.Head
                        | _ -> false
                    let metadataKnown =
                        producer.SharedIndexTypes.IsEmpty && consumer.SharedIndexTypes.IsEmpty
                        && producer.SDimsPerArray = [2] && consumer.SDimsPerArray = [1]
                        && producer.Identities.Length = 1 && consumer.Identities.Length = 1
                        && producer.ArrayTypes.Length = 1 && consumer.ArrayTypes.Length = 1
                        && denseMetadata producer.ArrayTypes.Head && denseMetadata consumer.ArrayTypes.Head
                        && sameAxes producer.ArrayTypes.Head input && storedRoles producer.ArrayTypes.Head
                        && sameAxes consumer.ArrayTypes.Head mapped
                        && (consumer.ArrayTypes.Head.IndexTypes |> List.map _.Kind) = [SDimension; TDimension]
                        && storedRoles input && storedRoles mapped && storedRoles result
                        && loopMetadata producer input && loopMetadata consumer mapped
                        && (match arrayOf 2 privateType with Some at -> sameAxes at mapped && storedRoles at | None -> false)
                    if not (sameAxes input mapped) || not (IRTotality.sameIndex result.IndexTypes.Head input.IndexTypes.Head) then
                        unchanged "source/map/result index provenance disagrees"
                    elif not (bindingSideOperands modul host.Id).IsEmpty
                         || modul.ProviderReads.ContainsKey host.Id || modul.ProviderWrites.ContainsKey host.Id then
                        unchanged "the host has an additional execution payload"
                    elif not metadataKnown then unchanged "the apply/method_for metadata is outside the known plain row geometry"
                    elif map.Params.Length <> 1 || map.Params.Head.Type <> scalar64 || map.RetType <> scalar64
                         || fold.Params.Length <> 2 || (fold.Params |> List.exists (fun p -> p.Type <> scalar64))
                         || fold.RetType <> scalar64 || not (modul.Functions |> List.exists (fun f -> f.Id = row.Id)) then
                        unchanged "the map/fold/row callable is outside the concrete scalar admission"
                    else
                    match proof, closedTotal map, closedTotal fold with
                    | Error why, _, _ -> unchanged $"source is not proved stored: {why}"
                    | _, Error why, _ | _, _, Error why -> unchanged $"a producer/fold is not closed pure/total: {why}"
                    | Ok (_, chain), _, _ when chain |> List.exists (fun b -> modul.ProviderReads.ContainsKey b.Id) ->
                        unchanged "provider sources are outside the initial stored-array admission"
                    | Ok (_, chain), _, _ when chain |> List.exists (fun b -> bindingPayload modul b |> List.exists writes) ->
                        unchanged "a stored source initializer or alias payload may write"
                    | Ok _, Ok (), Ok () ->
                        // The old row param may carry __reduce_inferred_n.
                        // Build its real C axis from the stored source, never
                        // from that abstract signature. Keep output R EXACTLY.
                        let column = { input.IndexTypes.[1] with Kind = SDimension }
                        let rowAt = { ElemType = scalar64; IndexTypes = [column]; IsVirtual = false; Identity = None }
                        let rowType = mkArrayLike rowAt
                        let rid = builder.FreshId ()
                        let rp = { Name = $"__partial_row_{rid}"; Index = 0; VarId = rid; Type = rowType }
                        let rv = IRVar (rid, rowType)
                        let rowIdentity = AIDVariable rp.Name
                        let inner =
                            { producer with
                                Loop = IRMethodFor { Arrays = [rv]; Identities = [rowIdentity]; ArrayTypes = [rowAt]
                                                     SDimsPerArray = [1]; TotalSDims = 1; SharedIndexTypes = [] }
                                Arrays = [rv]; Identities = [rowIdentity]; ArrayTypes = [rowAt]
                                SharedIndexTypes = []; SymcomStates = [SCNeither]; TriangularLevels = [false]
                                SDimsPerArray = [1]; KernelInputRanks = [0]; KernelTDims = []; OutputType = rowType }
                        let fid = builder.FreshId ()
                        let terminal = IRReduceCompute (IRApplyCombinator inner, foldExpr, seed)
                        let body = IRLet (fid, terminal, IRVar (fid, scalar64))
                        let fresh = mkCallable builder defaultLambdaOptions [rp] body scalar64 [] false [] [] false false 256 false
                        let fresh = { fresh with Name = $"__partial_rows_{fresh.Id}"; SourceName = row.SourceName }
                        let outerAt =
                            { input with IndexTypes = [ { input.IndexTypes.[0] with Kind = SDimension }
                                                        { input.IndexTypes.[1] with Kind = TDimension } ] }
                        let outer =
                            { consumer with
                                Loop = IRMethodFor { Arrays = [source]; Identities = producer.Identities; ArrayTypes = [input]
                                                     SDimsPerArray = [2]; TotalSDims = 2; SharedIndexTypes = [] }
                                Kernel = IRVar (fresh.Id, mkFuncArrow [rowType] scalar64)
                                Arrays = [source]; Identities = producer.Identities; ArrayTypes = [outerAt] }
                        minted.Add (fresh, row.Id)
                        record host.Name Blade.Effects.Applied
                            [ "one private rank-2 mapped pool removed; stored input and original rank-1 result retained"
                              "concrete source C row view; original R output type/identity; literal seed once per row"
                              "closed pure/total producer/fold/callee graph; immediate sole-use scope and complete residual demand census"
                              "reproducible ascending row chains; C=0 returns each seed, R=0 executes no row seeds" ]
                        { host with Value = IRCompute (IRApplyCombinator outer) }
                | _ -> unchanged "requires a dominating stored plain rank-2 Float64 source and a rank-1 Float64 result"
            | _ -> unchanged "requires one source and an immediate sole seeded row-fold consumer"
        | IRLet (_, value, _) when (peel value).IsSome ->
            match arrayOf 2 (peel value).Value.OutputType with
            | Some _ -> unchanged "the private producer is not consumed immediately by its sole row fold"
            | None -> host
        | IRCompute (IRApplyCombinator info) when (rowFold info).IsSome ->
            unchanged "requires a private scoped producer; top-level named/printed producers retain their storage"
        | _ -> host
    // PM09: the observed rank-k desugar has ONE scalar state, with three
    // unit-valued statement wrappers. Recognize that exact private rank-2
    // region; do not flatten a partial/outer fold or normalize arbitrary lets.
    let removedFullPools = System.Collections.Generic.HashSet<IRId>()
    let fullFold (host: IRBinding) =
        let decide outcome evidence =
            Blade.Effects.Decisions.record
                { Blade.Effects.Rule = "full-map-fold"; Version = 1; Span = declSpanOf host.Name
                  Subject = host.Name; Outcome = outcome; Evidence = evidence }
        let unchanged why = decide (Blade.Effects.Declined why) []; Some host
        match host.Type, host.Value with
        | IRTScalar ETFloat64, IRLet (pid, IRApplyCombinator info, rest) when (arrayOf 2 info.OutputType).IsSome ->
            let int64Type = IRTScalar ETInt64
            let step acc r c = function
                | IRLet (u, IRAssign (IRVar (target, tt),
                    IRApp (fold, [IRVar (a, at); IRIndex (IRVar (p, pt), [IRVar (ri, rt); IRVar (ci, ct)], _)], ret)), IRLit IRLitUnit)
                    when target = acc && a = acc && p = pid && ri = r && ci = c
                         && tt = scalar64 && at = scalar64 && ret = scalar64 && rt = int64Type && ct = int64Type -> Some (u, fold, pt)
                | _ -> None
            let shape =
                match rest with
                | IRLet (acc, seed,
                    IRLet (outerUnit, IRForRange (r, loR, hiR,
                        IRLet (innerUnit, IRForRange (c, loC, hiC, body), IRLit IRLitUnit)), IRVar (last, ty)))
                    when last = acc && ty = scalar64 ->
                    step acc r c body |> Option.map (fun (u, fold, pt) ->
                        acc, seed, [outerUnit; innerUnit; u], r, c, [loR; loC], [hiR; hiC], fold, pt)
                | _ -> None
            match shape, info.Arrays, resolve info.Kernel, foreignCensus with
            | Some (acc, seed, units, r, c, lows, highs, foldExpr, privateType), [IRVar (sid, sty)], Some map, Some foreign ->
                let removed = Set.ofList (pid :: acc :: r :: c :: units)
                let residual = IRReduceCompute (IRApplyCombinator info, foldExpr, seed)
                let refs e =
                    Set.union (collectVarRefsIR e)
                        (embeddedExtentExprs e |> List.map collectVarRefsIR |> Set.unionMany)
                let demanded e = not (Set.intersect removed (refs e)).IsEmpty
                let bad e = writes e || lifetimeBarrier e
                let badPayload e = bad e || (embeddedExtentExprs e |> List.exists bad)
                let extraDemand =
                    roots |> List.exists (function
                        | RootBindingValue b -> demanded (if b.Id = host.Id then residual else b.Value)
                        | RootSideOperand (_, _, e) -> demanded e
                        | RootFunctionBody f -> demanded f.Body || (f.Captures |> List.exists (fun x -> removed.Contains x.Id))
                        | RootType (_, ty) -> irTypeExtentExprs ty |> List.exists demanded
                        | RootSourceRef (_, id) -> removed.Contains id
                        | RootNameRef _ -> true)
                // Count definitions too: a malformed second scope may reuse a
                // removed id without containing a variable read of it.
                let definitions = System.Collections.Generic.Dictionary<IRId, int>()
                let add id =
                    let n = match definitions.TryGetValue id with true, n -> n | _ -> 0
                    definitions.[id] <- n + 1
                let note e =
                    iterIRExpr (function
                        | BinderShape (_, scopes) -> for ids, _ in scopes do for id in ids do add id
                        | _ -> ()) e
                for b in modul.Bindings do add b.Id
                for f in allCallables do
                    add f.Id
                    for p in f.Params do add p.VarId
                for root in roots do
                    match root with
                    | RootBindingValue b -> note b.Value; embeddedExtentExprs b.Value |> List.iter note
                    | RootSideOperand (_, _, e) | RootFunctionBody { Body = e } -> note e; embeddedExtentExprs e |> List.iter note
                    | RootType (_, ty) -> irTypeExtentExprs ty |> List.iter note
                    | _ -> ()
                let unique =
                    removed.Count = 7 && (removed |> Set.forall (fun id ->
                        match definitions.TryGetValue id with true, 1 -> true | _ -> false))
                    && (modul.Bindings |> List.filter (fun b -> b.Id = host.Id) |> List.length) = 1
                    && (modul.Functions |> List.map _.Id |> Set.ofList |> Set.count) = modul.Functions.Length
                let metadata (input: IRArrayType) (output: IRArrayType) =
                    let stored (at: IRArrayType) = at.Identity.IsNone && (at.IndexTypes |> List.forall (fun ix -> ix.Kind = SDimension))
                    let known at =
                        match arrayOf 2 (mkArrayLike at) with
                        | Some a -> sameAxes a input && stored a
                        | None -> false
                    sameAxes input output && stored input && stored output
                    && (match arrayOf 2 privateType with Some a -> sameAxes a output && stored a | _ -> false)
                    && info.KernelInputRanks = [0] && info.KernelOutputRank = 0 && info.KernelTDims.IsEmpty
                    && info.SDimsPerArray = [2] && info.SharedIndexTypes.IsEmpty && not info.IsCoIteration
                    && info.SymcomStates = [SCNeither; SCNeither] && info.TriangularLevels = [false; false]
                    && info.SpeedupFactor = 1L && info.ReynoldsSpeedup = 1L && not info.HasReynolds
                    && info.ArrayTypes.Length = 1 && known info.ArrayTypes.Head && info.Identities.Length = 1
                    && (match info.Loop with
                        | IRMethodFor mf ->
                            mf.TotalSDims = 2 && mf.SDimsPerArray = [2] && mf.SharedIndexTypes.IsEmpty
                            && mf.Identities = info.Identities && mf.ArrayTypes.Length = 1 && known mf.ArrayTypes.Head
                            && (match mf.Arrays with [IRVar (id, ty)] -> id = sid && ty = sty | _ -> false)
                        | _ -> false)
                    && lows = [IRLit (IRLitInt 0L); IRLit (IRLitInt 0L)]
                    && List.forall2
                        (fun (hi: IRExpr) (ix: IRIndexType) ->
                            IRTotality.evalInt hi = IRTotality.evalInt ix.Extent
                            && (match hi with IRLit (IRLitInt _) -> true | _ -> false))
                        highs output.IndexTypes
                let signature arity (cl: IRCallable) reference =
                    cl.Params.Length = arity && cl.RetType = scalar64
                    && (cl.Params |> List.mapi (fun i p -> p.Index = i && p.Type = scalar64) |> List.forall id)
                    && (cl.Params |> List.map _.VarId |> Set.ofList |> Set.count) = arity
                    && typeOf reference = mkFuncArrow (List.replicate arity scalar64) scalar64
                let rec serialGraph seen (cl: IRCallable) =
                    if seen |> Set.contains cl.Id then true
                    else
                        let scalar = function IRTScalar (ETFloat64 | ETInt64 | ETBool) -> true | _ -> false
                        unstrategized cl && cl.ArityParam.IsNone && not cl.IsCommutative
                        && cl.CommGroups.IsEmpty && cl.AntisymGroups.IsEmpty && cl.SignParities.IsEmpty
                        && scalar cl.RetType && (cl.Params |> List.mapi (fun i p -> p.Index = i && scalar p.Type) |> List.forall id)
                        && (cl.Params |> List.map _.VarId |> Set.ofList |> Set.count) = cl.Params.Length
                        && not cl.Effects.Mutates && not cl.Effects.EmitsOutput && not cl.Effects.ReadsExternal && not cl.Effects.MayFail
                        && not (lifetimeBarrier cl.Body)
                        && (collectVarRefsIR cl.Body |> Set.toList |> List.choose (fun id -> Map.tryFind id funcs)
                            |> List.forall (serialGraph (seen.Add cl.Id)))
                let unsafeRoots =
                    roots |> List.exists (function
                        | RootBindingValue b -> badPayload (if b.Id = host.Id then residual else b.Value)
                        | RootSideOperand (_, _, e) | RootFunctionBody { Body = e } -> badPayload e
                        | RootType (_, ty) -> irTypeExtentExprs ty |> List.exists badPayload
                        | _ -> false)
                match arrayOf 2 sty, arrayOf 2 info.OutputType, resolve foldExpr,
                      modul.Bindings |> List.tryFind (fun b -> b.Id = sid) with
                | Some input, Some output, Some fold, Some source when source.Type = sty ->
                    match storedArrayProof modul source, closedTotal map, closedTotal fold with
                    | Ok (_, chain), Ok (), Ok () ->
                        let owned = Set.union removed (chain |> List.map _.Id |> Set.ofList)
                        let ownerIds = owned.Add host.Id
                        let planned id =
                            (HotRowTable.tryProducer id).IsSome || (HotRowTable.tryConsumer id).IsSome
                            || (CellFoldTable.tryLoop id).IsSome || (CellFoldTable.tryFoldLet id).IsSome
                            || (RecStepTable.tryLoop id).IsSome || (RecStepTable.tryStep id).IsSome
                            || (EpilogueLegTable.tryProducer id).IsSome || (EpilogueLegTable.tryJoin id).IsSome
                            || (CompactionTable.tryMask id).IsSome || (CompactionTable.tryCompound id).IsSome || (CompactionTable.tryFoldMap id).IsSome
                            || (CompactRowTable.tryProducer id).IsSome || (CompactRowTable.tryConsumer id).IsSome
                        let ownerConflict =
                            (ownerIds |> Set.exists planned)
                            || (PoolReuseTable.donors () |> Map.exists (fun a b -> ownerIds.Contains a || ownerIds.Contains b))
                            || (PoolReuseTable.returnDonors () |> Map.exists (fun a b -> ownerIds.Contains a || ownerIds.Contains b))
                            || (MemberHoistTable.all () |> List.exists (fun p -> ownerIds.Contains p.HostId || ownerIds.Contains p.SetId || not (Set.intersect ownerIds p.Kernels).IsEmpty))
                        let hiddenExecution =
                            (modul.SparseInits |> Map.exists (fun id _ -> removed.Contains id))
                            || (modul.CompoundInits |> Map.exists (fun id _ -> removed.Contains id))
                            || (modul.RandomInits |> Map.exists (fun id _ -> removed.Contains id))
                            || (modul.ProviderReads |> Map.exists (fun id _ -> removed.Contains id))
                            || (modul.ProviderWrites |> Map.exists (fun id _ -> removed.Contains id))
                        let position id = modul.Bindings |> List.tryFindIndex (fun b -> b.Id = id)
                        let dominates =
                            match position sid, position host.Id with Some s, Some h -> s < h | _ -> false
                        let sourceIdentity =
                            match info.Identities with [AIDVariable name] -> name = source.Name | _ -> false
                        if not (gateOn "BLADE_FUSION") || not (Blade.Toolchain.fpReproEnabled ()) then unchanged "requires FUSION and reproducible serial folding"
                        elif not unique || not dominates then unchanged "nonunique local identities or source does not dominate"
                        elif not (literalSeed seed) || typeOf seed <> scalar64 then unchanged "requires a literal Float64 seed"
                        elif not (metadata input output && sourceIdentity && signature 1 map info.Kernel && signature 2 fold foldExpr) then
                            unchanged "unsupported map/loop/fold metadata or index provenance"
                        elif not (serialGraph Set.empty map && serialGraph Set.empty fold) then unchanged "a callable has effect/strategy metadata"
                        elif not (Set.intersect owned foreign).IsEmpty || extraDemand then unchanged "additional local/source/foreign demand"
                        elif unsafeRoots || (chain |> List.exists (fun b -> modul.ProviderReads.ContainsKey b.Id)) then unchanged "a source/payload may write or cross a lifetime/provider boundary"
                        elif hiddenExecution || not (bindingSideOperands modul host.Id).IsEmpty || modul.ProviderReads.ContainsKey host.Id || modul.ProviderWrites.ContainsKey host.Id then unchanged "host/local execution side payload"
                        elif ownerConflict || (Set.intersect removed modul.MutableArrayLets |> Set.exists ((<>) pid))
                             || (modul.DerivedFuncOrigins |> Map.exists (fun a b -> removed.Contains a || removed.Contains b)) then unchanged "overlapping storage/identity owner"
                        else
                            removedFullPools.Add pid |> ignore
                            decide Blade.Effects.Applied ["one private rank-2 mapped pool removed; stored source retained"; "one literal seed and original ascending scalar fold graph; complete residual/foreign/write census"]
                            Some { host with Value = residual }
                    | _ -> unchanged "source is not stored or callable graph is not closed pure/total"
                | _ -> unchanged "requires stored rank-2 Float64 input and resolved scalar fold"
            | _ -> unchanged "requires the exact unit-wrapped full-fold region and complete foreign census"
        | _ -> None
    let bindings = modul.Bindings |> List.map (fun b -> match fullFold b with Some full -> full | None -> rewrite b)
    if minted.Count = 0 && removedFullPools.Count = 0 then modul
    else
        let byOrigin = minted |> Seq.groupBy snd |> Map.ofSeq
        { modul with
            Bindings = bindings
            MutableArrayLets = Set.difference modul.MutableArrayLets (Set.ofSeq removedFullPools)
            Functions = modul.Functions |> List.collect (fun f ->
                f :: (Map.tryFind f.Id byOrigin |> Option.map (Seq.map fst >> List.ofSeq) |> Option.defaultValue []))
            DerivedFuncOrigins = minted |> Seq.fold (fun m (f, origin) -> Map.add f.Id origin m) modul.DerivedFuncOrigins }

// Existing callers have no complete foreign-binding/type census. C02 map
// admission stays as before; the new transpose arm must decline explicitly.
let fusePartialRowTerminalsModule (builder: IRBuilder) (programFuncs: IRCallable list) (modul: IRModule) : IRModule =
    fusePartialRowTerminalsWithCensusModule builder programFuncs None modul

/// C05's emitter-only certificate. The IR and the interpreter retain the
/// canonical gather; no planner table or shared stored-array rule is changed.
type SparseWildcardFoldPlan = {
    ParentId: IRId
    PinAxis: int
    Pin: int64
    Fold: IRCallable
    Seed: IRExpr
}

let certifySparseWildcardFold (wholeProgram: IRModule option) (binding: IRBinding)
                              (operand: IRExpr) (kernel: IRExpr) (seed: IRExpr option)
                              : Result<SparseWildcardFoldPlan, string> =
    let scalar64 = IRTScalar ETFloat64
    let literal = function
        | IRLit (IRLitFloat _) | IRUnaryOp (IRNeg, IRLit (IRLitFloat _)) -> true
        | _ -> false
    let plain (ix: IRIndexType) =
        ix.Rank = 1 && ix.IxKind = IxKPlain && ix.Symmetry = SymNone
        && ix.Kind = SDimension && ix.Dependencies.IsEmpty
    let candidate =
        match operand with
        | IRIndex (IRVar (_, ArrayElem at), [IRTuple _], _) ->
            at.IndexTypes.Length = 1 && at.IndexTypes.Head.IxKind = IxKSparse && at.IndexTypes.Head.Rank = 2
        | _ -> false
    match wholeProgram with
    | None -> Error "requires complete whole-program emission evidence"
    | Some _ when not (gateOn "BLADE_FUSION") -> Error "disabled by BLADE_FUSION"
    | Some _ when not (Blade.Toolchain.fpReproEnabled ()) -> Error "requires the reproducible serial graph"
    | Some _ when not candidate -> Error "not a direct rank-2 sparse partial operand"
    | Some m ->
        let funcs = m.Functions |> List.map (fun f -> f.Id, f) |> Map.ofList
        let resolve = resolveCallableIn funcs
        let writes = scheduleMayWriteIn m.Functions m
        let lifetime = scheduleHasLifetimeBarrierIn m.Functions m
        let same a b = obj.ReferenceEquals(a, b)
        let host = m.Bindings |> List.filter (fun b -> b.Id = binding.Id)
        let ordinary =
            match host with
            | [h] when h.Name = binding.Name && h.Type = scalar64 && binding.Type = scalar64
                       && same h.Value binding.Value ->
                match h.Value with
                | IRReduce (a, k, s) when same a operand && same k kernel && s = seed -> true
                | _ -> false
            | _ -> false
        let closedTotal root =
            let scalarType = function IRTScalar (ETFloat64 | ETInt64 | ETBool) -> true | _ -> false
            let rec audit visiting (f: IRCallable) =
                if Set.contains f.Id visiting then Error "a recursive fold/callee"
                elif f.IsStatic || f.IsArityPoly || f.IsOmpParallel || not f.Parallelism.IsEmpty
                     || f.IsCudaKernel || f.IsMpiParallel || not f.Captures.IsEmpty then
                    Error "a captured or strategized fold/callee"
                elif not (scalarType f.RetType) || (f.Params |> List.exists (fun p -> not (scalarType p.Type)))
                     || (f.Params |> List.mapi (fun i p -> p.Index <> i) |> List.exists id)
                     || (f.Params |> List.map _.VarId |> Set.ofList |> Set.count) <> f.Params.Length then
                    Error "a non-scalar or malformed fold/callee signature"
                elif writes f.Body || lifetime f.Body then Error "a writing/resource fold/callee"
                else
                    match IRTotality.auditCallable resolve (fun _ -> None) (fun _ -> false) f [] with
                    | Error why -> Error why
                    | Ok () ->
                        let mutable bound = f.Params |> List.map _.VarId |> Set.ofList
                        iterIRExpr (function IRLet (id, _, _) -> bound <- bound.Add id | _ -> ()) f.Body
                        let refs = collectVarRefsIR f.Body |> Set.filter (fun id -> not (bound.Contains id)) |> Set.toList
                        let rec check = function
                            | [] -> Ok ()
                            | id :: rest ->
                                match Map.tryFind id funcs with
                                | None -> Error "an unknown/global/callback fold reference"
                                | Some callee ->
                                    match audit (visiting.Add f.Id) callee with
                                    | Error why -> Error why
                                    | Ok () -> check rest
                        check refs
            audit Set.empty root
        if not ordinary || (m.Bindings |> List.map _.Id |> Set.ofList |> Set.count) <> m.Bindings.Length
                        || funcs.Count <> m.Functions.Length then
            Error "requires the exact ordinary host and unique binding/callable identities"
        elif not m.ProviderReads.IsEmpty || not m.ProviderWrites.IsEmpty || not m.RandomInits.IsEmpty
             || not m.CompoundInits.IsEmpty || not m.MutableArrayLets.IsEmpty then
            Error "additional execution/ownership side payloads"
        else
        match operand, resolve kernel, seed with
        | IRIndex (IRVar (sid, sty), [IRTuple coords], _), Some fold, Some init when literal init ->
            let parent = m.Bindings |> List.tryFind (fun b -> b.Id = sid)
            let pin =
                match classifyCompoundIndexTuple 2 coords with
                | CompoundPartial ([(axis, IRLit (IRLitInt n))], [_]) when coords.Length = 2 && n >= 0L -> Some (axis, n)
                | _ -> None
            match parent, sty, pin, Map.tryFind sid m.SparseInits with
            | Some source, ArrayElem sparse, Some (axis, pinValue), Some (IRVar (vid, vty))
                when source.Type = sty && source.Value = IRLit IRLitUnit
                     && sparse.ElemType = scalar64 && not sparse.IsVirtual && sparse.IndexTypes.Length = 1
                     && m.SparseInits.Count = 1 ->
                let ix = sparse.IndexTypes.Head
                let values = m.Bindings |> List.tryFind (fun b -> b.Id = vid)
                let order = m.Bindings |> List.mapi (fun i b -> b.Id, i) |> Map.ofList
                let dominates =
                    match Map.tryFind vid order, Map.tryFind sid order, Map.tryFind binding.Id order with
                    | Some v, Some s, Some h -> v < s && s < h
                    | _ -> false
                let geometry =
                    match ix.Extent, values, vty with
                    | IRSparseKeys (SkStatic keys), Some vb, ArrayElem vt
                        when ix.Rank = 2 && ix.IxKind = IxKSparse && ix.Symmetry = SymNone
                             && ix.Tag = Some "__sparseidx" && ix.Kind = SDimension && ix.Dependencies.IsEmpty
                             && vb.Type = vty && vt.ElemType = scalar64 && not vt.IsVirtual && vt.IndexTypes.Length = 1
                             && plain vt.IndexTypes.Head && (keys |> List.forall (fun k -> k.Length = 2 && List.forall (fun n -> n >= 0L) k))
                             && (Set.ofList keys).Count = keys.Length ->
                        match vb.Value with
                        | IRArrayLit (cells, at) when at.ElemType = scalar64 && not at.IsVirtual && at.IndexTypes.Length = 1
                                                      && plain at.IndexTypes.Head && IRTotality.sameIndex at.IndexTypes.Head vt.IndexTypes.Head ->
                            cells.Length = keys.Length && List.forall literal cells
                            && IRTotality.evalInt vt.IndexTypes.Head.Extent = Some (int64 keys.Length)
                        | _ -> false
                    | _ -> false
                let signature =
                    fold.Params.Length = 2 && fold.RetType = scalar64
                    && (fold.Params |> List.mapi (fun i p -> p.Index = i && p.Type = scalar64) |> List.forall id)
                    && fold.Params.[0].VarId <> fold.Params.[1].VarId
                    && typeOf kernel = IRTArrow ([SVal scalar64; SVal scalar64], scalar64, None)
                let residualType =
                    match typeOf operand with
                    | ArrayElem at when at.ElemType = scalar64 && not at.IsVirtual && at.IndexTypes.Length = 1 ->
                        let r = at.IndexTypes.Head
                        plain r && r.Extent = IRCompoundProject (IRVar (sid, sty), 1)
                    | _ -> false
                let ids = Set.ofList [vid; sid; binding.Id]
                let planned id =
                    (HotRowTable.tryProducer id).IsSome || (HotRowTable.tryConsumer id).IsSome
                    || (CellFoldTable.tryLoop id).IsSome || (CellFoldTable.tryFoldLet id).IsSome
                    || (RecStepTable.tryLoop id).IsSome || (RecStepTable.tryStep id).IsSome
                    || (EpilogueLegTable.tryProducer id).IsSome || (EpilogueLegTable.tryJoin id).IsSome
                    || (CompactionTable.tryMask id).IsSome || (CompactionTable.tryCompound id).IsSome
                    || (CompactionTable.tryFoldMap id).IsSome
                    || (CompactRowTable.tryProducer id).IsSome || (CompactRowTable.tryConsumer id).IsSome
                let ownerConflict =
                    (ids |> Set.exists planned)
                    || (PoolReuseTable.donors () |> Map.exists (fun a b -> ids.Contains a || ids.Contains b))
                    || (PoolReuseTable.returnDonors () |> Map.exists (fun a b -> ids.Contains a || ids.Contains b))
                    || (MemberHoistTable.all () |> List.exists (fun p -> ids.Contains p.HostId || ids.Contains p.SetId || not (Set.intersect ids p.Kernels).IsEmpty))
                let demand e =
                    let mutable found = false
                    iterIRExpr (function
                        | IRIndex (IRVar (id, _), (IRTuple cs) :: _, _) when id = sid ->
                            match classifyCompoundIndexTuple 2 cs with CompoundPartial _ -> found <- true | _ -> ()
                        | IRCompoundProject (IRVar (id, _), _) when id = sid -> found <- true
                        | IRLet (_, IRVar (id, _), _) when id = sid -> found <- true
                        | IRVar (id, ty) when id = sid && ty <> sty -> found <- true
                        | _ -> ()) e
                    found
                let exprDemand e = demand e || (embeddedExtentExprs e |> List.exists demand)
                // The exact operand's complete type/coordinate metadata is
                // certified above. No other root gets this exemption.
                let residual = IRReduce (IRZero scalar64, kernel, seed)
                let extraDemand =
                    compactionRoots m.Functions m |> List.exists (function
                        | RootBindingValue b ->
                            exprDemand (if b.Id = binding.Id then residual else b.Value)
                            || (b.Id <> sid && b.Id <> binding.Id && (match b.Value with IRVar (id, _) -> id = sid | _ -> false))
                        | RootSideOperand (b, _, e) -> b.Id <> sid || exprDemand e
                        | RootFunctionBody f -> exprDemand f.Body || (f.Captures |> List.exists (fun c -> c.Id = sid || c.Id = vid))
                        | RootType (_, ty) -> irTypeExtentExprs ty |> List.exists exprDemand
                        | RootSourceRef (_, id) -> ids.Contains id
                        | RootNameRef _ -> true)
                let effect =
                    (m.Bindings |> List.exists (fun b -> bindingPayload m b |> List.exists (fun e -> writes e || lifetime e)))
                    || (m.Functions |> List.exists (fun f -> writes f.Body || lifetime f.Body))
                    || (compactionRoots m.Functions m |> List.exists (function
                        | RootType (_, ty) -> irTypeExtentExprs ty |> List.exists (fun e -> writes e || lifetime e)
                        | RootBindingValue b -> embeddedExtentExprs b.Value |> List.exists (fun e -> writes e || lifetime e)
                        | RootSideOperand (_, _, e) | RootFunctionBody { Body = e } ->
                            embeddedExtentExprs e |> List.exists (fun x -> writes x || lifetime x)
                        | _ -> false))
                if not dominates || not geometry || not signature || not residualType then Error "unsupported source/operand/fold geometry or provenance"
                elif ownerConflict then Error "overlapping storage/schedule owner"
                elif extraDemand then Error "additional residual/alias/capture/type/side-payload demand"
                elif effect then Error "a whole-program payload may write or cross a resource boundary"
                else
                    match closedTotal fold with
                    | Error why -> Error why
                    | Ok () -> Ok { ParentId = sid; PinAxis = axis; Pin = pinValue; Fold = fold; Seed = init }
            | _ -> Error "requires one known stored sparse constructor and a literal pin"
        | _ -> Error "requires a direct sparse partial operand, resolved fold and literal seed"

/// `programFuncs` is every callable of the PROGRAM (all modules): a pass that
/// judges a callee's effects resolves it there, since a generic defined in
/// one module and called from another (`from stats import mean`) is not in
/// the calling module's own table.
let private optimizeModuleCore (builder: IRBuilder) (programFuncs: IRCallable list) foreignCensus (modul: IRModule) : IRModule =
    // Normal lowering constructs the events LAST. If a consumer re-applies
    // this stage to optimized IR, retain the established lifetime regions.
    if (modul.Functions |> List.exists (fun f -> containsPoolLifetimeEvent f.Body))
       || (modul.Bindings |> List.exists (fun b -> containsPoolLifetimeEvent b.Value)) then modul else
    recordGramApplyAdvisory modul
    recordSegmentStreaming modul
    modul
    |> foldConstMatchesModule
    |> (fun m -> fuseElementwiseChainsModule m builder programFuncs)
    |> fusePartialRowTerminalsWithCensusModule builder programFuncs foreignCensus
    |> (fun m -> fuseSliceWritesModule m builder programFuncs)
    |> (fun m -> fuseRowFoldsModule m builder programFuncs)
    |> hoistKernelInvariantsModule builder programFuncs
    // LAST (it sees every fold the passes above left in place); before
    // IRLift and CSE, so the pass dedupes repeated legs itself.
    |> shareSiblingFoldsModule builder programFuncs

let optimizeModule (builder: IRBuilder) (programFuncs: IRCallable list) (modul: IRModule) : IRModule =
    optimizeModuleCore builder programFuncs None modul

/// Only the whole-program Lowering driver supplies this complete census.
let internal optimizeModuleWithForeignCensus (builder: IRBuilder) (programFuncs: IRCallable list)
                                            (foreignRefs: Set<IRId>) (modul: IRModule) : IRModule =
    optimizeModuleCore builder programFuncs (Some foreignRefs) modul
