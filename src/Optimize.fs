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
/// BLADE_COMPACTION_FUSION=0|off disables compaction fusion
/// (`planCompactionFusion`): the analysis still runs, and a mask -> compound
/// -> fold chain it would have fused records `compaction-fusion` DECLINED
/// ("disabled by BLADE_COMPACTION_FUSION"); nothing reaches the side table,
/// so both emitters keep the separate count / fill / gather / pool passes.
let compactionFusionEnabled () = gateOn "BLADE_COMPACTION_FUSION"

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

/// Every gate of this layer (charter rule 3), in one place: the optimizer
/// differential (`blade test opt-diff`, tests/OptDiff.fs) turns ALL of them
/// off for its reference lane, so a new pass's gate belongs in this list the
/// day the pass lands -- a gate missing here is a pass the differential
/// cannot see.
let optimizerGates = [ "BLADE_FUSION"; "BLADE_FREEZE_IDIOM"; "BLADE_CSE"; "BLADE_POOL_REUSE"; "BLADE_HOIST"; "BLADE_HOT_ROW"; "BLADE_CELL_EPILOGUE"; "BLADE_COMPACTION_FUSION"; "BLADE_COMPACT_ROW_CLASSES"; "BLADE_SIBLING_FOLDS" ]

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
let scheduleMayWriteIn (programFuncs: IRCallable list) (modul: IRModule) : IRExpr -> bool =
    let funcs =
        (programFuncs @ modul.Functions) |> List.map (fun f -> (f.Id, f)) |> Map.ofList
    let lets = System.Collections.Generic.Dictionary<IRId, IRExpr>()
    let note (e: IRExpr) = iterIRExpr (fun n -> match n with IRLet (id, v, _) -> lets.[id] <- v | _ -> ()) e
    for b in modul.Bindings do
        lets.[b.Id] <- b.Value
        note b.Value
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
    fun (e: IRExpr) -> exprMayWriteWith facts e

let scheduleMayWrite (modul: IRModule) : IRExpr -> bool = scheduleMayWriteIn [] modul

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
        iterIRExpr (fun n -> match n with IRAssign _ | IRForRange _ -> found <- true | _ -> ()) e
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
                            between |> List.tryPick (fun (iId, iName, iVal) ->
                                let r = refs iVal
                                // DOMINANCE: the row kernel runs at the
                                // producer, so every value it reads must be
                                // bound before the producer -- a capture or
                                // free reference bound in between would be
                                // read before its declaration (or a shadowed
                                // outer name would be read instead).
                                if Set.contains iId captureIds || Set.contains iId kernelRefs then
                                    Some $"the row kernel reads `{iName}`, which is bound between the producer and the fold (the row kernel would run before it exists)"
                                elif Set.contains pId r then Some $"`{iName}` reads `{pName}` between the producer and the fold"
                                elif not (Set.isEmpty (Set.intersect r captureIds)) then Some $"`{iName}` references a binding the row kernel captures"
                                elif not (facts iVal).Repeatable || writes iVal then Some $"`{iName}` is not repeatable (an assignment, display output, or a call the module cannot judge) between the producer and the fold"
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
            between |> List.tryPick (fun (iId, iName, iVal) ->
                let r = refs iVal
                // Dominance: the cell kernel runs at the producer, so nothing
                // it reads may be bound in between.
                if Set.contains iId captureIds || Set.contains iId (refs ck.Body) then
                    Some $"the cell kernel reads `{iName}`, which is bound between the producer and the fold (it would run before `{iName}` exists)"
                elif Set.contains pId r then Some $"`{iName}` reads `{pName}` between the producer and the fold"
                elif not (Set.isEmpty (Set.intersect r captureIds)) then Some $"`{iName}` references a binding the cell kernel captures"
                elif not (facts iVal).Repeatable || writes iVal then Some $"`{iName}` is not repeatable (an assignment, display output, or a call the module cannot judge) between the producer and the fold"
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
// Positions are always built (B4d, eliding them, needs an index-demand
// census across aliases and is deferred). Gate: BLADE_COMPACTION_FUSION.

/// The fused compaction of one `let y = compound(x, keep)` over a let-bound
/// rank-1 `keep = mask(x, pred)`.
type CompactionPlan = {
    MaskId: IRId
    MaskName: string
    CompoundId: IRId
    CompoundName: string
    SourceId: IRId
    /// The C++ lane's handshake: the mask emitter's count variable, written
    /// when it actually counted (its rank-1 callable arm), read by the
    /// compound's emission to choose the fused pass. Reset by the mask's
    /// emission, so a second emission of the same program starts clean.
    CountCpp: string option ref
    /// The LLVM lane's twin: the alloca slot holding the count.
    CountLlvm: string option ref
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
    let record (p: CompactionPlan) =
        let s = get ()
        cell.Value <- { s with ByMask = Map.add p.MaskId p s.ByMask; ByCompound = Map.add p.CompoundId p s.ByCompound }
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

/// Plan compaction fusion over the module's top-level bindings (`compound`
/// is top-level only, BL6002). Records into `CompactionTable` and the
/// decision collector (`compaction-fusion`); the IR is untouched.
let planCompactionFusion (programFuncs: IRCallable list) (modul: IRModule) : unit =
    let funcs = modul.Functions |> List.map (fun f -> (f.Id, f)) |> Map.ofList
    let facts = cseFactsOf funcs
    let writes = scheduleMayWriteIn programFuncs modul
    let enabled = compactionFusionEnabled ()
    let decide (subject: string) (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) =
        Blade.Effects.Decisions.record
            { Blade.Effects.Rule = "compaction-fusion"; Version = 1
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
        (arr |> Array.exists (fun b -> assignsTo id b.Value))
        || (modul.Functions |> List.exists (fun f -> assignsTo id f.Body))
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
                         | IRMask (IRVar (srcId, _), pred) when srcId = xId ->
                            let predOk =
                                match resolveCallableIn funcs pred with
                                | Some pc ->
                                    (match pc.Params with
                                     | [ p ] -> pc.Captures.IsEmpty && totalScalar p pc.Body
                                     | _ -> false)
                                | None -> false
                            let between = [ ki + 1 .. yi - 1 ] |> List.map (fun i -> arr.[i])
                            let offending =
                                between |> List.tryPick (fun b ->
                                    if Set.contains kId (refs b.Value) then Some $"`{b.Name}` reads `{kb.Name}` between the mask and the compound"
                                    elif (facts b.Value).MayWrite || writes b.Value then Some $"`{b.Name}` may write (an assignment or a mutating call) between the mask and the compound"
                                    else None)
                            // (IsMutable is no evidence either way: every
                            // non-static binding is assignable in its own
                            // scope; an actual assignment is.)
                            if assignedAnywhere kId then Error $"`{kb.Name}` is assigned"
                            elif not predOk then Error $"the mask predicate of `{kb.Name}` is not a pure, total scalar function of the cell (v1 judges literal arithmetic, comparisons and logic over the one parameter)"
                            else
                            match offending with
                            | Some why -> Error why
                            | None ->
                                Ok ({ MaskId = kId; MaskName = kb.Name; CompoundId = yId; CompoundName = yName
                                      SourceId = xId; CountCpp = ref None; CountLlvm = ref None },
                                    [ $"`{kb.Name}`'s count is taken in its own mask pass (no count pass); the kept-position fill and the value gather are ONE branchless pass `v = x[i]; {yName}[q] = v; pos[q] = i; q += {kb.Name}[i]` (m + 1 cells in both buffers, the slack never exposed)"
                                      $"`{yName}`'s index: dense extent n, cardinality m, the ascending kept positions (keyed reads, BL8006 unchanged); positions always built (B4d deferred); `{kb.Name}` stays stored"
                                      "stable, ascending, serial: the same compaction in fewer sweeps" ])
                         | IRMask _ -> Error "the mask's source is not the compound's dense operand"
                         | _ -> Error "the compound's mask is not a let-bound `mask(x, pred)`")
                     | _ -> Error "the compound's mask is not a let-bound `mask(x, pred)` ahead of it")
                | _ -> Error "the mask or the dense operand is written in place (v1 fuses a let-bound mask over a stored array)"
            let compaction =
                match compactionDecision with
                | Error why ->
                    decide yName (Blade.Effects.Declined why) []
                    None
                | Ok (plan, evidence) ->
                    if not enabled then
                        decide yName (Blade.Effects.Declined "disabled by BLADE_COMPACTION_FUSION") evidence
                        None
                    else
                        CompactionTable.record plan
                        decide yName Blade.Effects.Applied evidence
                        Some plan
            // ---- B4a (+ B4c): the folds over y's self-product -----------
            for hi in yi + 1 .. arr.Length - 1 do
                let hb = arr.[hi]
                for (mid, info, k, init) in foldSites yId hb.Value do
                    let what = $"fold of `{yName} * {yName}`"
                    let declined (why: string) = decide hb.Name (Blade.Effects.Declined why) [ what ]
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
                    elif (facts hb.Value).MayWrite || writes hb.Value then
                        declined $"`{hb.Name}` may write (an assignment or a mutating call) between the map's materialization and the fold, so the map's snapshot could differ from `{yName}` at the fold"
                    else
                    match fk with
                    | None -> declined "the fold kernel does not resolve to a callable"
                    | Some f when f.IsOmpParallel || f.IsCudaKernel || f.IsMpiParallel ->
                        declined "the fold kernel requests a thread team or a device (`where omp` / cuda / mpi): v1 keeps the materialized route, which owns that census"
                    | Some f when not (builtinAdd f) -> declined "the fold kernel is not the builtin `+` (v1 fuses `reduce(y * y, (+))`)"
                    | Some _ ->
                        // Feeding the chain inside the compaction pass moves
                        // its evaluation from `hb` up to `y`: sound when no
                        // one can change `y`'s cells in between. Without an
                        // alias analysis (a `let mut alias = y` shares the
                        // storage; a call through a `mut` parameter writes
                        // without naming `y`), the proof is NO write at all
                        // from `y` through `hb`, and no assignment to `y`.
                        let inPass =
                            compaction.IsSome && not (assignedAnywhere yId)
                            && ([ yi + 1 .. hi ] |> List.forall (fun i -> not ((facts arr.[i].Value).MayWrite || writes arr.[i].Value)))
                        let evidence =
                            [ $"`{hb.Name}` = {what}: folded straight over `{yName}`'s compact buffer (m contiguous cells), no square pool; licensed: the lane form, repro: the unseeded serial chain from the first kept square, in kept order; every product rounded before its add in both lanes (C++ an asm rounding barrier, LLVM an undecorated fmul), as the stored product was; no write between the map and the fold"
                              (if inPass then $"under the serial (repro) form the chain is fed inside `{yName}`'s compaction pass, block by block, from completed cells only; the empty check (BL8003) stays at `{hb.Name}`"
                               else $"the fold runs at `{hb.Name}` (the compaction pass does not feed it)")
                              $"`{yName}` and every named binding stay stored" ]
                        if not enabled then
                            decide hb.Name (Blade.Effects.Declined "disabled by BLADE_COMPACTION_FUSION") evidence
                        else
                            CompactionTable.recordFold
                                { MapId = mid; CompoundId = yId; CompoundName = yName; HostName = hb.Name
                                  FoldKernel = k; InPass = inPass
                                  EmittedCpp = ref None; EmittedLlvm = ref None }
                            decide hb.Name Blade.Effects.Applied evidence
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
                            between |> List.tryPick (fun (_, iName, iVal) ->
                                if readsTransitively pId iVal then Some $"`{iName}` reads `{pName}` between the producer and the fold"
                                elif (facts iVal).MayWrite || writes iVal then Some $"`{iName}` may write (an assignment or a mutating call) between the producer and the fold"
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
    let expand (ext: int64) (kVar: IRExpr) (outsideOk: IRId -> bool)
               (prelude: System.Collections.Generic.List<IRId * IRExpr>) (root: IRExpr) : Result<IRExpr, string> =
        // The FIRST reason a node is not a cell expression is the decline.
        let mutable why : string option = None
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
        let rec go (env: Map<IRId, IRExpr>) (visiting: Set<IRId>) (e: IRExpr) : IRExpr option =
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
            | IRCompute ((IRApplyCombinator _ | IRCompute _) as inner) -> go env visiting inner
            | IRCompute _ ->
                // Forcing a deferred VALUE (`dn |> compute`) materializes it
                // per step; reading it cell by cell instead would make the
                // emitters materialize the binding itself -- at module level a
                // new printed value (corpus memfree/007).
                fail "the slice forces a deferred value"
            | IRLet (id, v, body) ->
                (match go env visiting v with
                 | Some atom -> go (Map.add id atom env) visiting body
                 | None -> None)
            | IRApplyCombinator info when IRMono.tilePlainInfo info && rowOfExtent ext info.OutputType ->
                (match IRMono.tileKernelOf callables info with
                 | Some k when IRMono.tilePlainKernel k info.Arrays.Length
                               && k.Captures.IsEmpty && not (hasCall k.Body)
                               && IRMono.tilePureBody callables Set.empty k.Body
                               && not (IRMono.tileMayAbort callables Set.empty k.Body)
                               && scalarElem k.RetType
                               && info.ArrayTypes |> List.forall (fun at -> staticExtent at = Some ext && scalarElem at.ElemType) ->
                    let atoms = info.Arrays |> List.map (go env visiting)
                    if atoms |> List.exists Option.isNone then None else
                    let m = List.zip (k.Params |> List.map (fun p -> p.VarId)) (atoms |> List.map Option.get) |> Map.ofList
                    Some (bind (substParams m (freshenLets k.Body)) k.RetType)
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
                    let atoms = args |> List.map (go env visiting)
                    if atoms |> List.exists Option.isNone then None else
                    // The callee sees only its parameters (lexical scope).
                    let env' = List.zip (f.Params |> List.map (fun p -> p.VarId)) (atoms |> List.map Option.get) |> Map.ofList
                    go env' (visiting.Add fid) f.Body
                 | _ -> fail "a callee is not a plain function of dense row parameters (static, capturing, arity-polymorphic, or parallel)")
            | IRApp _ -> fail "the slice calls a function that is recursive, defined in another module, or not row-valued"
            | IRApplyCombinator _ -> fail "an operation over the row is not a plain elementwise map of the loop's extent (a fold, a structured index, or a different shape)"
            | IRIf _ | IRMatch _ -> fail "the slice selects between whole rows (a guarded read or a branch), which is no cell expression"
            | _ -> fail "the slice is not pointwise (an operation other than elementwise maps, lets and pointwise calls)"
        match go Map.empty Set.empty root with
        | Some atom -> Ok atom
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
    // Walk a body keeping the values of enclosing lets (to judge aliases of
    // the buffer). Only let-chains are walked into; the rewrite is local to
    // a `let s = SLICE in let w = <copy loop> in rest` triple.
    let rec rw (scope: Map<IRId, IRExpr>) (e: IRExpr) : IRExpr =
        match e with
        | IRLet (sId, slice, IRLet (wId, loop, rest)) when (rowCopy sId loop).IsSome && not (mentions sId rest) ->
            let kId, ext, aId, bufId, bufT, idxs, ident, kVar, tail = (rowCopy sId loop).Value
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
                    | Some v when not (mentions bufId v) -> not (isDeferredValue v)
                    | Some (IRIndex (IRVar (b, _), rowIdx, _)) when b = bufId ->
                        // A row view BUF(i): cell k is BUF(i, k).
                        (match bufT with
                         | ArrayElem at -> rowIdx.Length = at.IndexTypes.Length - 1
                         | _ -> false)
                    | Some _ -> false
            let prelude = System.Collections.Generic.List<IRId * IRExpr>()
            match expand ext kVar outsideOk prelude slice with
            | Ok atom ->
                fired <- fired + 1
                let store = IRLet (aId, IRAssign (IRIndex (IRVar (bufId, bufT), idxs, ident), atom), tail)
                let body = Seq.foldBack (fun (id, v) acc -> IRLet (id, v, acc)) prelude store
                IRLet (wId, IRForRange (kId, IRLit (IRLitInt 0L), IRLit (IRLitInt ext), body), rw scope rest)
            | Error why ->
                // Only a slice that looks fusable (a call or an elementwise
                // map) is a decision; a literal seed row is just a copy.
                let mutable looksFusable = false
                iterIRExpr (fun n -> match n with IRApp _ | IRApplyCombinator _ -> looksFusable <- true | _ -> ()) slice
                if looksFusable then declines.Add why
                IRLet (sId, rw scope slice, IRLet (wId, rwInner scope loop, rw (Map.add wId loop (Map.add sId slice scope)) rest))
        | IRLet (id, v, body) -> IRLet (id, rw scope v, rw (Map.add id v scope) body)
        | _ -> rwInner scope e
    and rwInner (scope: Map<IRId, IRExpr>) (e: IRExpr) : IRExpr =
        match e with
        | IRForRange (k, lo, hi, body) -> IRForRange (k, lo, hi, rw scope body)
        | IRLet _ -> rw scope e
        | ExprShape ([], _) -> e
        | ExprShape (children, rebuild) -> rebuild (children |> List.map (rw scope))
    // One record per body: applied (with the count) and/or each decline's
    // reason -- a body can hold a fused arm and a declined one.
    let rewriteBody (subject: string) (outerIds: Set<IRId>) (body: IRExpr) =
        let before = fired
        declines.Clear ()
        outer <- outerIds
        let body' = rw Map.empty body
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
    let mutable lastFoldIds : (IRId * IRId) option = None
    // `acc <- F(acc, K(BUF(row)))` as statements ending in `tail`.
    let foldUpdate (accId: IRId) (accT: IRType) (bufId: IRId) (bufT: IRType)
                   (k: IRCallable) (f: IRCallable) (row: IRExpr) (tail: IRExpr) =
        let rowId = builder.FreshId ()
        let pId = builder.FreshId ()
        lastFoldIds <- Some (rowId, pId)
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
                IRLet (builder.FreshId (), IRAssign (IRVar (accId, accT), fBody), tail)))
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
                    match rowKernel, foldKernel with
                    | Some k, Some f when plainScalarKernel 1 k && plainScalarKernel 2 f ->
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
                        // B2r (round-2 cell epilogues): the step's row is
                        // written by ONE slice-write-fused cell loop whose
                        // last statement stores a scalar local into
                        // BUF[n][k], and the row kernel is prodsum(row, row):
                        // record the loop so an emitter may advance the
                        // row's chain per cell (the IR is unchanged).
                        (match rowKernelCellTerm k, stepFoldIds with
                         | Some term, Some (rowId, pId) ->
                            let stepStmts, _ = chainOf body
                            let writers =
                                stepStmts |> List.filter (fun (_, s) -> mentions bufId s && (match s with IRForRange _ -> true | _ -> false))
                            let cellLoop =
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
      ResultType: IRType }

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
            if not ((lets0 |> List.forall (snd >> inlineValueOk)) && inlineValueOk r0) then
                refused "the callee body is not a pure-total let spine of folds, maps and scalar arithmetic"
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
              Threads = (c.IsOmpParallel && lic) || leafOmp; Audit = audit; ResultType = resTy }

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
                            let leg = mkLeg leafF k c seed note false (Ok ()) (typeOf node)
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
                      SeedNote = None; Licensed = true; Op = Some IRAdd; Threads = false; Audit = Ok (); ResultType = f64 }
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

        // ---- rewrite on the spine with only the contributing inlines ---------
        let keptInlines =
            applied |> Seq.collect (fun (g, _, _) -> g |> List.choose (fun m -> inlineKeyOf m.UnitKey)) |> Set.ofSeq
        let finalUnits = buildUnits (fun k -> Set.contains k keptInlines)
        let replace = System.Collections.Generic.Dictionary<SfUnitKey * int, IRExpr>()
        let dropUnits = System.Collections.Generic.HashSet<SfUnitKey>()
        let projOf = System.Collections.Generic.Dictionary<IRId, IRId list * IRType list>()
        let inserts = System.Collections.Generic.Dictionary<SfUnitKey, (IRId * IRExpr) list>()
        for (g, legs, idx) in applied do
            let built = legs |> List.map (fun l -> l.Build ())
            let comp = built |> List.map (fun (lf, _, _) -> lf) |> List.reduce (fun a b -> IRFusion (a, b))
            let rc = IRReduceCompute (comp, IRTuple (built |> List.map (fun (_, k, _) -> k)), IRTuple (built |> List.map (fun (_, _, s) -> s)))
            let jId = builder.FreshId()
            let jTy = typeOf rc
            let pIds = legs |> List.map (fun _ -> builder.FreshId())
            let pTys = legs |> List.map (fun l -> l.ResultType)
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

/// `programFuncs` is every callable of the PROGRAM (all modules): a pass that
/// judges a callee's effects resolves it there, since a generic defined in
/// one module and called from another (`from stats import mean`) is not in
/// the calling module's own table.
let optimizeModule (builder: IRBuilder) (programFuncs: IRCallable list) (modul: IRModule) : IRModule =
    recordGramApplyAdvisory modul
    recordSegmentStreaming modul
    modul
    |> foldConstMatchesModule
    |> (fun m -> fuseElementwiseChainsModule m builder programFuncs)
    |> (fun m -> fuseSliceWritesModule m builder programFuncs)
    |> (fun m -> fuseRowFoldsModule m builder programFuncs)
    |> hoistKernelInvariantsModule builder programFuncs
    // LAST (it sees every fold the passes above left in place); before
    // IRLift and CSE, so the pass dedupes repeated legs itself.
    |> shareSiblingFoldsModule builder programFuncs
