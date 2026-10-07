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
let private gateOn (var: string) =
    match System.Environment.GetEnvironmentVariable var with
    | null -> true
    | v ->
        match v.Trim().ToLowerInvariant() with
        | "0" | "off" | "false" -> false
        | _ -> true

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

/// Every gate of this layer (charter rule 3), in one place: the optimizer
/// differential (`blade test opt-diff`, tests/OptDiff.fs) turns ALL of them
/// off for its reference lane, so a new pass's gate belongs in this list the
/// day the pass lands -- a gate missing here is a pass the differential
/// cannot see.
let optimizerGates = [ "BLADE_FUSION"; "BLADE_FREEZE_IDIOM"; "BLADE_CSE"; "BLADE_POOL_REUSE"; "BLADE_HOIST" ]

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
    | IRTArrow (slots, _, _) -> slots |> List.exists (function SVal _ -> true | _ -> false)
    | _ -> false

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
                | IRParam _ | IRNth | IRArity _ | IRPolyIndex _ | IRPolyTail _ | IRZero _
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
    |> hoistKernelInvariantsModule builder programFuncs
