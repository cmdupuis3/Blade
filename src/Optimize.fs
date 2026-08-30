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

open Blade.Ast
open Blade.IR
open Blade.IRMono

/// BLADE_FREEZE_IDIOM=0|off disables freeze-idiom recognition (the A/B
/// escape hatch, read per call like every other gate).
let freezeIdiomEnabled () =
    match System.Environment.GetEnvironmentVariable "BLADE_FREEZE_IDIOM" with
    | null -> true
    | v ->
        match v.Trim().ToLowerInvariant() with
        | "0" | "off" | "false" -> false
        | _ -> true

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
// Soundness demands two shape checks, both CONSERVATIVE (any unrecognized
// node declines recognition rather than guessing):
//   - the else-arm is EXACTLY `prefix(n - 1)` (the whole previous slice --
//     an else that repairs, decays, or reads deeper lags is a live arm, not
//     a freeze);
//   - the guard's prefix reads are all at lag 1, and it references neither
//     the step ordinal outside those reads (a guard varying with `n`
//     independently of the trajectory is NOT absorbing: `n < k` flips on
//     its own) nor the bare prefix family.

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
/// never referenced bare, and the whole guard is built from the shapes a
/// convergence predicate uses (literals, variables, arithmetic/comparison/
/// boolean operators, unary ops, applications, ascriptions). Anything else
/// -- a lambda, a block, a match -- declines recognition conservatively.
let rec private guardAdmissible (prefixVar: Ident) (stepVar: Ident) (e: Expr) : bool =
    let ok = guardAdmissible prefixVar stepVar
    match e.Kind with
    | ExprLit _ -> true
    | ExprVar v -> v <> stepVar && v <> prefixVar
    | ExprBinOp (_, _, l, r) -> ok l && ok r
    | ExprUnaryOp (_, x) -> ok x
    | ExprTyped (x, _) -> ok x
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
                  isStepMinusOne stepVar lagArg
                  && restFirst |> List.forall ok
                  && deeper |> List.forall (List.forall ok)
              | _ -> false)
         | ExprVar fn when fn <> stepVar ->
             // An ordinary call (abs, sqrt, a named helper): the head name
             // is loop-invariant; the arguments carry the discipline.
             argLists |> List.forall (List.forall ok)
         | _ -> false)
    | _ -> false

/// Recognize the freeze idiom on an UNGUARDED recursive-array definition and
/// repartition it into the guarded best-effort form. Returns None (leave the
/// definition alone -- it still compiles and still means the same thing) for
/// anything that is not exactly the idiom.
let recognizeFreezeIdiom (def: RecArrayDef) : RecArrayDef option =
    if not (freezeIdiomEnabled ()) then None else
    match def.Guard with
    | Some _ -> None
    | None ->
        match def.SliceExpr.Kind with
        | ExprIf (g, stepExpr, elseExpr) when
                isPrevSliceRead def.PrefixVar def.StepVar elseExpr
                && guardAdmissible def.PrefixVar def.StepVar g ->
            Some { def with Guard = Some g; SliceExpr = stepExpr }
        | _ -> None

// --- Pipeline entry -------------------------------------------------------

/// The IR-level optimization stage, run per module in Lowering after the
/// monomorphizers and the array-binop rewrite, before inline-form lifting:
/// constant-scrutinee match folding (which also resolves symbolic ranks per
/// specialization), then elementwise-chain fusion. One entry point so the
/// pipeline reads as a stage, and so a new pass has one obvious place to
/// join.
let optimizeModule (builder: IRBuilder) (modul: IRModule) : IRModule =
    modul
    |> foldConstMatchesModule
    |> (fun m -> fuseElementwiseChainsModule m builder)
