// Optimization-layer emission pins (src/Optimize.fs -- the semantic-
// equivalence layer). The corpus proves VALUES, and every pass in the layer
// is value-preserving by charter, so the corpus is structurally blind to a
// pass that silently stops firing (or fires where it must not). These pins
// read the emitted C++ instead -- the same rationale as FlatPathTests.
//
// Freeze-idiom recognition is the sharpest case: the recognized and
// unrecognized emissions produce byte-identical output at runtime, so ONLY
// an emission pin can distinguish "early exit derived" from "runs the whole
// budget". The contract half matters just as much in the other direction:
// the recognized idiom must NOT acquire the `while` spelling's BL8010
// budget abort (an optimization may change cost, never contract), and a
// guard that reads the step ordinal outside a lag-1 prefix read must
// DECLINE (not absorbing: such a guard can flip back true after a freeze).
//
// Pure lowering + codegen: no g++, no toolchain. Always runs.
module Blade.Tests.OptimizeTests

open Blade
open Blade.Lowering
open Blade.Types
open Blade.IR
open Blade.Tests.TestHarness

let private cppOfSource (testName: string) (src: string) : Result<string, string> =
    try
        match lower src with
        | Error e -> Error ($"lower: {e}")
        | Ok ir -> Ok (fst (CodeGen.genSelfContainedProgramFromIR ir testName))
    with ex -> Error ($"codegen raised: {ex.Message}")

/// The guard-break shape the rec-array machinery emits: `if (!(__vN)) {`.
/// Generated-name-anchored, so the runtime preamble's switch-case breaks and
/// user-visible identifiers cannot match it.
let private guardBreakCount (cpp: string) =
    System.Text.RegularExpressions.Regex.Matches(cpp, @"if \(!\(__v\d+\)\) \{").Count

let private bl8010Count (cpp: string) =
    cpp.Split('\n') |> Array.filter (fun l -> l.Contains "BL8010") |> Array.length

// ---------------------------------------------------------------------------
// Fixtures. Newton for sqrt(2) in three spellings over one budget.
// ---------------------------------------------------------------------------

/// The freeze idiom: unguarded arm, `if G then STEP else prefix(n-1)`,
/// lag-1 guard. Recognition must derive the break and must NOT add the abort.
let private freezeIdiom =
    "type It = Idx<30>\n"
    + "let tol = 0.000000001\n"
    + "let rec xs: Array<Float like It> =\n"
    + "    match xs with\n"
    + "    | zero -> zero\n"
    + "    | zero :: s -> zero :: 1.0\n"
    + "    | prefix :: n -> prefix :: (if abs(prefix(n - 1) * prefix(n - 1) - 2.0) > tol then (prefix(n - 1) + 2.0 / prefix(n - 1)) * 0.5 else prefix(n - 1))\n"
    + "let root = xs(29)\n"

/// The `while` spelling of the same recurrence: break AND the BL8010 abort.
let private whileSpelling =
    "type It = Idx<30>\n"
    + "let tol = 0.000000001\n"
    + "let rec xs: Array<Float like It> =\n"
    + "    match xs with\n"
    + "    | zero -> zero\n"
    + "    | zero :: s -> zero :: 1.0\n"
    + "    | prefix :: n while abs(prefix(n - 1) * prefix(n - 1) - 2.0) > tol -> prefix :: (prefix(n - 1) + 2.0 / prefix(n - 1)) * 0.5\n"
    + "let root = xs(29)\n"

/// A guard reading the step ordinal OUTSIDE a lag-1 prefix read. Freezing a
/// slice does not freeze `n`, so falseness is not absorbing and recognition
/// must decline -- the loop stays a plain full-budget ternary.
let private ordinalGuardDeclines =
    "type It = Idx<10>\n"
    + "let rec ys: Array<Float like It> =\n"
    + "    match ys with\n"
    + "    | zero -> zero\n"
    + "    | zero :: s -> zero :: 1.0\n"
    + "    | prefix :: n -> prefix :: (if n < 5 then prefix(n - 1) * 2.0 else prefix(n - 1))\n"
    + "let last = ys(9)\n"

/// A guard that CALLS a user function with a `mut` parameter. The call
/// mutates the guard's own input, so its first false answer is not
/// absorbing: run to budget, the counter climbs and the guard flips back
/// true; recognized, the trajectory froze at 1 and six of seven `tick`
/// calls vanished (plan-fortran-killer-2.md appendix A). Recognition must
/// decline any non-intrinsic callee -- pure or not, this seam sees names.
let private effectfulGuardDeclines =
    "type C = Idx<1>\n"
    + "type It = Idx<8>\n"
    + "function tick(c: mut Array<Float like C>) -> Bool = {\n"
    + "    c((0 : C)) += 1.0\n"
    + "    c((0 : C)) > 2.0\n"
    + "}\n"
    + "let mut counter: Array<Float like C> = [0.0]\n"
    + "let rec xs: Array<Float like It> =\n"
    + "    match xs with\n"
    + "    | zero -> zero\n"
    + "    | zero :: s -> zero :: 1.0\n"
    + "    | prefix :: n -> prefix :: (if tick(counter) then prefix(n - 1) + 1.0 else prefix(n - 1))\n"
    + "let last = xs((7 : It))\n"
    + "let calls = counter((0 : C))\n"

/// The same shape with a PURE user helper in the guard. `residual`'s effect
/// summary (Blade.Effects, computed from its typed body) is repeatable, so
/// recognition ADMITS the call and derives the break -- the P0 step-2
/// re-admission. v1 declined this by name; the flip is deliberate.
let private pureHelperGuardRecognized =
    "type It = Idx<30>\n"
    + "let tol = 0.000000001\n"
    + "function residual(x: Float) -> Float = abs(x * x - 2.0)\n"
    + "let rec xs: Array<Float like It> =\n"
    + "    match xs with\n"
    + "    | zero -> zero\n"
    + "    | zero :: s -> zero :: 1.0\n"
    + "    | prefix :: n -> prefix :: (if residual(prefix(n - 1)) > tol then (prefix(n - 1) + 2.0 / prefix(n - 1)) * 0.5 else prefix(n - 1))\n"
    + "let root = xs(29)\n"

/// A guard whose `abs` is SHADOWED by a user FUNCTION of the same name. The
/// intrinsic spelling no longer means the intrinsic, so the callee test must
/// consult the scope, not the name table alone -- and what it finds is a
/// declared function with a repeatable summary, so recognition proceeds on
/// that function's own evidence.
let private shadowedIntrinsicPureRecognized =
    "type It = Idx<30>\n"
    + "let tol = 0.000000001\n"
    + "function abs(x: Float) -> Float = if x < 0.0 then 0.0 - x else x\n"
    + "let rec xs: Array<Float like It> =\n"
    + "    match xs with\n"
    + "    | zero -> zero\n"
    + "    | zero :: s -> zero :: 1.0\n"
    + "    | prefix :: n -> prefix :: (if abs(prefix(n - 1) * prefix(n - 1) - 2.0) > tol then (prefix(n - 1) + 2.0 / prefix(n - 1)) * 0.5 else prefix(n - 1))\n"
    + "let root = xs(29)\n"

/// A guard calling a helper that LOOKS pure but calls the effectful `tick`
/// underneath. The summary joins transitively, so the outer helper is not
/// repeatable and recognition declines.
let private transitiveEffectfulGuardDeclines =
    "type C = Idx<1>\n"
    + "type It = Idx<8>\n"
    + "function tick(c: mut Array<Float like C>) -> Bool = {\n"
    + "    c((0 : C)) += 1.0\n"
    + "    c((0 : C)) > 2.0\n"
    + "}\n"
    + "let mut counter: Array<Float like C> = [0.0]\n"
    + "function probe(x: Float) -> Bool = tick(counter) && x < 1000.0\n"
    + "let rec xs: Array<Float like It> =\n"
    + "    match xs with\n"
    + "    | zero -> zero\n"
    + "    | zero :: s -> zero :: 1.0\n"
    + "    | prefix :: n -> prefix :: (if probe(prefix(n - 1)) then prefix(n - 1) + 1.0 else prefix(n - 1))\n"
    + "let last = xs((7 : It))\n"

/// A guard calling a pure helper that itself calls another pure helper: the
/// summaries compose through the call chain, so recognition proceeds.
let private transitivePureGuardRecognized =
    "type It = Idx<30>\n"
    + "let tol = 0.000000001\n"
    + "function sq_err(x: Float) -> Float = x * x - 2.0\n"
    + "function residual(x: Float) -> Float = abs(sq_err(x))\n"
    + "let rec xs: Array<Float like It> =\n"
    + "    match xs with\n"
    + "    | zero -> zero\n"
    + "    | zero :: s -> zero :: 1.0\n"
    + "    | prefix :: n -> prefix :: (if residual(prefix(n - 1)) > tol then (prefix(n - 1) + 2.0 / prefix(n - 1)) * 0.5 else prefix(n - 1))\n"
    + "let root = xs(29)\n"

/// An elementwise chain the fusion pass fuses into one nest (plan-fortran-
/// killer.md arc 1): the decision record must say so.
let private fusionChain =
    "type I = Idx<5>\n"
    + "let a: Array<Float like I> = [1.0, 2.0, 3.0, 4.0, 5.0]\n"
    + "let b: Array<Float like I> = [2.0, 3.0, 4.0, 5.0, 6.0]\n"
    + "let c: Array<Float like I> = [0.5, 0.5, 0.5, 0.5, 0.5]\n"
    + "let d: Array<Float like I> = [1.0, 1.0, 1.0, 1.0, 1.0]\n"
    + "let y = a + b * c - d\n"
    + "let total = reduce(y, (+))\n"

/// A may-abort inner map (lgamma) read only under the host's branch: fusing
/// would skip the inner evaluation on the untaken cells, so the pass declines
/// (Optimize charter rule 1). The same inner read on every cell still fuses.
let private fusionMayAbortConditional =
    "let x = [1.0, 3.0, 2.0]
"
    + "let c = [1.0, 0.0, 1.0]
"
    + "let y = (method_for(zip(c, ((method_for(x) <@> lambda(v) -> lgamma(v)) |> compute))) <@> lambda(k, g) -> if k > 0.5 then g else 0.0) |> compute
"
let private fusionMayAbortUnconditional =
    "let x = [1.0, 3.0, 2.0]
"
    + "let c = [1.0, 0.0, 1.0]
"
    + "let y = (method_for(zip(c, ((method_for(x) <@> lambda(v) -> lgamma(v)) |> compute))) <@> lambda(k, g) -> k + g) |> compute
"

/// Slice-write fusion (Optimize.fuseSliceWritesModule): the bench `recur`
/// shape at toy size. The inductive arm's slice is pointwise (step and rhs
/// elementwise over X), so the row is computed where it is stored.
let private sliceWriteRk4Src =
    "type T = Idx<5>\n"
    + "type X = Idx<7>\n"
    + "function rhs(u: Array<Float64 like X>) -> Array<Float64 like X> = u - u * u * u\n"
    + "function step(u: Array<Float64 like X>) -> Array<Float64 like X> = {\n"
    + "    let k1 = rhs(u)\n"
    + "    let k2 = rhs(u + 0.005 * k1)\n"
    + "    let k3 = rhs(u + 0.005 * k2)\n"
    + "    let k4 = rhs(u + 0.01 * k3)\n"
    + "    u + (0.01 / 6.0) * (k1 + 2.0 * k2 + 2.0 * k3 + k4)\n"
    + "}\n"
    + "function kernel(u0: Array<Float64 like X>) -> Float64 = {\n"
    + "    let rec traj: Array<Float64 like T, X> =\n"
    + "        match traj with\n"
    + "        | zero -> zero\n"
    + "        | zero :: s -> zero :: u0\n"
    + "        | prefix :: n -> prefix :: step(prefix(n - 1))\n"
    + "    reduce(method_for(traj) <@> lambda(row) -> prodsum(row, row), (+))\n"
    + "}\n"
    + "let u0 = method_for(range<X>) <@> lambda(i) -> 0.5 + 0.01 * Float64(i) |> compute\n"
    + "let checksum = kernel(u0)\n"

/// A step that folds over its row is not pointwise: declined, and the
/// record says why.
let private sliceWriteFoldSrc =
    "type T = Idx<4>\n"
    + "type X = Idx<5>\n"
    + "function centered(u: Array<Float64 like X>) -> Array<Float64 like X> = u - reduce(u, (+)) / 5.0\n"
    + "let rec a: Array<Float64 like T, X> =\n"
    + "    match a with\n"
    + "    | zero -> zero\n"
    + "    | zero :: s -> zero :: [1.0, 2.0, 3.0, 4.0, 6.0]\n"
    + "    | prefix :: n -> prefix :: centered(prefix(n - 1)) * 0.5\n"

/// Integer `/` by a value read from the prefix may panic (BL8013 on a zero
/// divisor): the arm is declined, never fused. (By a nonzero LITERAL it
/// cannot, and fuses: corpus recursive-arrays/028 `h`.)
let private sliceWriteIntDivSrc =
    "type T = Idx<4>\n"
    + "type X = Idx<3>\n"
    + "let rec d: Array<Int64 like T, X> =\n"
    + "    match d with\n"
    + "    | zero -> zero\n"
    + "    | zero :: s -> zero :: [1, 2, 3]\n"
    + "    | prefix :: n -> prefix :: prefix(n - 1) / prefix(0) + 7\n"

/// A step that forces a module-level DEFERRED binding (corpus memfree/007):
/// reading it cell by cell would make the emitters materialize the binding
/// itself, which the print pass then shows -- an observable change. Declined.
let private sliceWriteDeferredSrc =
    "type C = Idx<4>\n"
    + "type Steps = Idx<3>\n"
    + "let ll = method_for(range<C>) <@> lambda(m) -> Float64(m)\n"
    + "let neg = lambda(x) -> 0.0 - x\n"
    + "let dn = neg <$> ll\n"
    + "let rec traj: Array<Float like Steps, C> =\n"
    + "    match traj with\n"
    + "    | zero -> zero\n"
    + "    | zero :: s -> zero :: [1.0, 2.0, 3.0, 4.0]\n"
    + "    | prefix :: n -> prefix :: {\n"
    + "        let start = prefix(n - 1)\n"
    + "        let forced = dn |> compute\n"
    + "        start + 0.1 * forced\n"
    + "      }\n"

/// Row-fold fusion (Optimize.fuseRowFoldsModule) must DECLINE a `while`-
/// guarded recursion: its freeze epilogue rewrites rows after the loop, so a
/// row is not final when its step ends.
let private rowFoldWhileSrc =
    "type Sweep = Idx<6>\n"
    + "type Col = Idx<2>\n"
    + "function settle(u0: Array<Float64 like Col>) -> Float64 = {\n"
    + "    let rec f: Array<Float64 like Sweep, Col> =\n"
    + "        match f with\n"
    + "        | zero -> zero\n"
    + "        | zero :: s -> zero :: u0\n"
    + "        | prefix :: n while prefix(n - 1, 0) > 1.0 -> prefix :: prefix(n - 1) * 0.5\n"
    + "    reduce(method_for(f) <@> lambda(row) -> prodsum(row, row), (+))\n"
    + "}\n"
    + "let u0: Array<Float64 like Col> = [8.0, 4.0]\n"
    + "let v = settle(u0)\n"

/// The fold half of the recur emission: no fold loop over the trajectory
/// remains after the recursion (each row is folded inside it).
let private rowFoldRk4Emission () =
    let name = "row_fold_rk4_no_post_recursion_fold"
    match cppOfSource name sliceWriteRk4Src with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        let start = cpp.IndexOf "double kernel(Array<double, 1> u0) {"
        let body = if start < 0 then "" else cpp.Substring(start, cpp.IndexOf("\n}\n", start) - start)
        let postFold = body.Contains "reduce over computation"
        if start >= 0 && not postFold then
            resultLine Pass name "kernel: the row checksum is accumulated inside the recursion; no fold over the trajectory after it"
            true
        else
            resultLine Fail name ($"expected no post-recursion fold in kernel; got found={start >= 0} postFold={postFold}")
            false

/// The emission half of the pin: in the fused kernel no call to `step`, no
/// slice copy and no per-step free remain -- one scalar chain per cell
/// stored into the trajectory row.
let private sliceWriteRk4Emission () =
    let name = "slice_write_rk4_no_step_call_no_copy"
    match cppOfSource name sliceWriteRk4Src with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        let start = cpp.IndexOf "double kernel(Array<double, 1> u0) {"
        let body = if start < 0 then "" else cpp.Substring(start, cpp.IndexOf("\n}\n", start) - start)
        let stepCalls = System.Text.RegularExpressions.Regex.Matches(body, @"\bstep\(").Count
        let rowDeallocs = System.Text.RegularExpressions.Regex.Matches(body, @"deallocate<typename promote<double, 1>").Count
        let rowStore = System.Text.RegularExpressions.Regex.IsMatch(body, @"__v\d+\[__kv\d+\]\[__kv\d+\] = __v\d+;")
        if start >= 0 && stepCalls = 0 && rowDeallocs = 0 && rowStore then
            resultLine Pass name "kernel: no step call, no rank-1 temporary freed, cells stored straight into the trajectory row"
            true
        else
            resultLine Fail name ($"expected no step call / no rank-1 frees / a direct row store in kernel; got found={start >= 0} stepCalls={stepCalls} rowDeallocs={rowDeallocs} rowStore={rowStore}")
            false

// ---------------------------------------------------------------------------

/// The decision record for a source: install a collector, lower, drain.
let private decisionsOf (src: string) : Result<Blade.Effects.Decision list, string> =
    Blade.Effects.Decisions.start ()
    let r =
        match lower src with
        | Error e -> Error ($"lower: {e}")
        | Ok _ -> Ok (Blade.Effects.Decisions.drain ())
    Blade.Effects.Decisions.drain () |> ignore
    r

let private decisionCase (name: string) (src: string) (rule: string)
                         (want: Blade.Effects.Decision -> bool) (describe: string) =
    match decisionsOf src with
    | Error e -> resultLine Fail name e; false
    | Ok ds ->
        let mine = ds |> List.filter (fun d -> d.Rule = rule)
        if mine |> List.exists want then
            resultLine Pass name ($"{mine.Length} `{rule}` decision(s); {describe}")
            true
        else
            let seen = mine |> List.map Blade.Effects.Decisions.render |> String.concat " | "
            let shown = if seen = "" then "no decisions" else seen
            resultLine Fail name ($"wanted {describe}; saw: {shown}")
            false

let private applied (d: Blade.Effects.Decision) =
    match d.Outcome with Blade.Effects.Applied -> true | _ -> false
let private declinedMentioning (needle: string) (d: Blade.Effects.Decision) =
    match d.Outcome with
    | Blade.Effects.Declined why -> why.Contains needle
    | _ -> false

/// Reverse-mode AD of an additive recurrence (docs/plans/structural/01): the
/// recurrence lowers to ONE direct loop in the primal and the adjoint is the
/// same loop run backwards, so the gradient of tests/corpus/ad/008 emits
/// exactly three counted loops -- the primal's, the forward replay's, and
/// the descending adjoint's -- and none of the triangular unroll's `__rk` /
/// `__rm` ordinals. The corpus proves the VALUES (008 and 027-029 at
/// 127/257/509); only an emission pin can tell O(n) from O(n^2).
let private recarrayGradEmission () =
    let name = "recarray_grad_linear_emission"
    let path = "tests/corpus/ad/008_recarray_grad.blade"
    if not (System.IO.File.Exists path) then
        resultLine Fail name $"missing {path} (run from the repo root)"
        false
    else
    match cppOfSource "recarray_grad_linear_emission" (System.IO.File.ReadAllText path) with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        let loops = System.Text.RegularExpressions.Regex.Matches(cpp, @"for \(int64_t ").Count
        let triangular = cpp.Contains "__rk" || cpp.Contains "__rm"
        let descending = System.Text.RegularExpressions.Regex.IsMatch(cpp, @"- 1L\) - __kv\d+\)")
        if loops = 3 && not triangular && descending then
            resultLine Pass name "3 counted loops (primal, replay, descending adjoint); no triangular ordinals"
            true
        else
            resultLine Fail name ($"expected 3 counted loops, no __rk/__rm, a descending index; got {loops} loop(s), triangular={triangular}, descending={descending}")
            false

/// Milestone B of the same design: a NONLINEAR first-order recurrence (the
/// logistic map, tests/corpus/ad/035) takes the same route -- one direct
/// loop, a replay, a descending adjoint -- with `dg/ds` read off the
/// trajectory buffer, so the emission shape is 008's exactly.
let private recarrayGradNonlinearEmission () =
    let name = "recarray_grad_nonlinear_emission"
    // 035's function and gradient call alone (the corpus file also takes
    // two jvps, whose own loops would be counted).
    let src =
        "import ad as ad
"
        + "function f(x: Float, r: Float) -> Float = {
"
        + "    let rec s: Array<Float like Idx<4>> =
"
        + "        match s with
"
        + "        | zero -> zero
"
        + "        | zero :: n -> zero :: x
"
        + "        | prefix :: n -> prefix :: r * prefix(n - 1) * (1.0 - prefix(n - 1))
"
        + "    s(3)
"
        + "}
"
        + "let (gv, gx, gr) = ad.grad(f)(0.25, 2.0)
"
    match cppOfSource name src with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        let loops = System.Text.RegularExpressions.Regex.Matches(cpp, @"for \(int64_t ").Count
        let triangular = cpp.Contains "__rk" || cpp.Contains "__rm"
        let descending = System.Text.RegularExpressions.Regex.IsMatch(cpp, @"- 1L\) - __kv\d+\)")
        if loops = 3 && not triangular && descending then
            resultLine Pass name "3 counted loops (primal, replay, descending adjoint); no triangular ordinals"
            true
        else
            resultLine Fail name ($"expected 3 counted loops, no __rk/__rm, a descending index; got {loops} loop(s), triangular={triangular}, descending={descending}")
            false

/// A halo window over a PLAIN DENSE source reads the source directly --
/// `a[w + k]` over the shrunk interior, in bounds by construction. (The
/// halo carousel ring this used to be measured against -- memory-resident,
/// so it only added a store and a load per step, and its loop-carried
/// dependence withheld vectorization -- is gone: no dense source took it
/// after be9f1f93, and the symmetric sources that still did read their packed
/// rows uncanonicalized -- tests/corpus/loops/211. The `halo carousel` probe
/// below stays as the tripwire that it does not come back unannounced.)
let private haloDenseSourceReadsDirect () =
    let name = "halo_dense_source_reads_direct"
    let src =
        "type H = Idx<9>\n"
        + "let a: Array<Float like H> = [1.0, 2.0, 4.0, 7.0, 11.0, 16.0, 22.0, 29.0, 37.0]\n"
        + "let d = method_for(halo<H, [-1, 0, 1]>) <@> lambda(w) -> a(w(1)) - a(w(-1)) |> compute\n"
    match cppOfSource name src with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        let carousel = cpp.Contains "halo carousel"
        let direct = cpp.Contains "a[(w + 1L)]" && cpp.Contains "a[(w + -(1L))]"
        let interior = cpp.Contains "for (size_t __i0 = 0; __i0 < 7; __i0++)"
        if not carousel && direct && interior then
            resultLine Pass name "no carousel; direct a[w + k] reads over the 7-cell interior"
            true
        else
            resultLine Fail name ($"carousel={carousel}, direct={direct}, interior={interior}")
            false

/// A reduction join's share is read by a DIRECT-FOLD leg (docs/plans/
/// structural/03, defect D2): `reduce(e, (+))` beside `prodsum(e, v)` over
/// the named deferred map `e` spells the producer ONCE in the joint loop --
/// the share's `const` -- and the fold leg accumulates that name. The corpus
/// (loops/205) proves the values; only an emission pin can tell one
/// exponential per iteration from two.
let private joinShareReadByDirectFold () =
    let name = "join_share_read_by_direct_fold"
    let src =
        "type J = Idx<11>\n"
        + "let k = method_for(range<J>) <@> lambda(j) -> 0.2 * Float64(j) - 0.4 |> compute\n"
        + "let v = method_for(range<J>) <@> lambda(j) -> 1.0 + 0.1 * Float64(j) |> compute\n"
        + "let e = method_for(k) <@> lambda(b) -> exp(-b * b)\n"
        + "let z, u = object_for(<&!>) <@> (reduce(e, (+)), prodsum(e, v))\n"
    match cppOfSource name src with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        // One in the lifted kernel body, one in the share's per-iteration const.
        let exps = System.Text.RegularExpressions.Regex.Matches(cpp, @"blade_libm::exp\(").Count
        let shared = cpp.Contains "sharing e per iteration"
        // Wrapper call (repro build) or the vectorized join's builtin update
        // (the 2026-10-17 default): either way the leg reads the share `e`.
        let legReadsShare =
            System.Text.RegularExpressions.Regex.IsMatch(cpp, @"_j0\(\w+_0, e\);|(\w+_0) = \1 \+ \(e\);")
        if exps = 2 && shared && legReadsShare then
            resultLine Pass name "2 blade_libm::exp sites (kernel body + share const); the fold leg reads `e`"
            true
        else
            resultLine Fail name ($"expected 2 blade_libm::exp sites, the share note and `_j0(.., e)`; got exps={exps}, shared={shared}, legReadsShare={legReadsShare}")
            false

/// `reduce(method_for(q, k) <@> lambda(a, b) -> f(a, b), (+))` under the
/// default `axes = 1` streams (docs/plans/structural/03, piece D): the
/// checker rewrites the deferred outer product into an outer apply whose row
/// kernel is the fused fold over `k`, so the emission has NO rank-2 pool, no
/// row-mode `__pfrow`/`__pfsrc` scaffolding, and the exponential is applied
/// inside a fold wrapper. The corpus (loops/206) proves the values.
let private outerProductPartialFoldStreams () =
    let name = "outer_product_partial_fold_streams"
    let src =
        "type I = Idx<7>\n"
        + "type J = Idx<11>\n"
        + "function score(a: Float64, b: Float64) -> Float64 = -(a - b) * (a - b) * 8.0\n"
        + "let q = method_for(range<I>) <@> lambda(i) -> 0.3 * Float64(i) - 0.5 |> compute\n"
        + "let k = method_for(range<J>) <@> lambda(j) -> 0.2 * Float64(j) - 0.4 |> compute\n"
        + "let z = reduce(method_for(q, k) <@> lambda(a, b) -> exp(score(a, b)), (+))\n"
    match cppOfSource name src with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        let pools = System.Text.RegularExpressions.Regex.Matches(cpp, @"Array<double, 2>").Count
        let rowMode = cpp.Contains "__pfrow" || cpp.Contains "__pfsrc"
        // The wrapper form (repro build) or the vectorized fold's builtin
        // update (the 2026-10-17 default); both fold the exp in the row kernel.
        let fusedFold =
            System.Text.RegularExpressions.Regex.IsMatch(cpp, @"= __wrap_\d+_\w+\(\w+, blade_libm::exp\(|(\w+) = \1 \+ blade_libm::exp\(")
        if pools = 0 && not rowMode && fusedFold then
            resultLine Pass name "no rank-2 pool, no row-mode scaffolding; exp folded inside the wrapper"
            true
        else
            resultLine Fail name ($"expected no Array<double, 2>, no __pfrow/__pfsrc, a fused exp fold; got pools={pools}, rowMode={rowMode}, fusedFold={fusedFold}")
            false

/// SCRATCH REUSE (plan-fortran-killer-2 section 4, gate 2;
/// Blade.Optimize.planPoolReuse). The demean shape: `y`, a reduce of `y`, `z
/// = y - m`, and the return `z * 0.5`. The return is written into `y`'s dead
/// pool: its declaration is an alias of `y.data`, and `y`'s scope free is
/// spared while `z`'s stays. One reuse in the whole program (only the
/// shape-specialized body has literal extents).
let private poolReuseSrc =
    "type I = Idx<1000>\n"
    + "let v = method_for(range<I>) <@> lambda(i) -> 0.01 * Float64(i) |> compute\n"
    + "function demean(x: T^1) -> T^1 = {\n"
    + "    let y = x * 2.0 + 1.0\n"
    + "    let m = reduce(y, (+)) / Float64(extents(y))\n"
    + "    let z = y - m\n"
    + "    z * 0.5\n"
    + "}\n"
    + "let out = demean(v)\n"
    + "let s = reduce(out, (+))\n"

let private poolReuseReturnTakesDeadPool () =
    let name = "pool_reuse_return_takes_dead_pool"
    match cppOfSource name poolReuseSrc with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        let aliases = System.Text.RegularExpressions.Regex.Matches(cpp, @"pool reuse: ").Count
        let retAlias = System.Text.RegularExpressions.Regex.IsMatch(cpp, @"Array<double, 1> __ret\d+ = \{ __v\d+\.data, __ret\d+_extents \};")
        if aliases = 1 && retAlias then
            resultLine Pass name "the specialized body's return aliases the dead pool; one reuse in the program"
            true
        else
            resultLine Fail name ($"expected exactly one pool-reuse alias on a __ret declaration; got aliases={aliases}, retAlias={retAlias}")
            false

/// Two let-bound temporaries share one root pool in a chain (`p`, then `q`
/// after `p` is dead, then `s` after `q` is dead; `r` reads `q` so it keeps
/// its own), and the decision record says so: pools 4 -> 2.
let private poolReuseChainSrc =
    "type I = Idx<1000>\n"
    + "let v = method_for(range<I>) <@> lambda(i) -> 0.01 * Float64(i) |> compute\n"
    + "function chain(x: T^1) -> Float64 = {\n"
    + "    let p = x * 2.0\n"
    + "    let m = reduce(p, (+)) + 0.0\n"
    + "    let q = x - m\n"
    + "    let r = q * 3.0\n"
    + "    let k = reduce(r, (+)) + 0.0\n"
    + "    let s = x + k\n"
    + "    reduce(s, (+))\n"
    + "}\n"
    + "let r = chain(v)\n"

let private poolReuseChainSharesRoot () =
    let name = "pool_reuse_chain_shares_root"
    match cppOfSource name poolReuseChainSrc with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        // One body: the shape-specialized copy of `chain`'s HM spec. The
        // unshaped HM copy its only call site left used to be emitted too
        // (and qualified as well, for 4 aliases); shape monomorphization now
        // drops a compiler-made origin every call left, so two reusers.
        let aliases = System.Text.RegularExpressions.Regex.Matches(cpp, @"pool reuse: __v\d+ takes __v\d+'s dead pool").Count
        if aliases = 2 then
            resultLine Pass name "two reusers in the body take the root's pool"
            true
        else
            resultLine Fail name ($"expected 2 pool-reuse aliases (1 body x 2 reusers); got {aliases}")
            false

/// The escape-analysis leak the same work found: a kernel capturing a scalar
/// that was computed FROM an array (`m = reduce(y, (+)) + 0.0`, then `y - m`)
/// used to pin `y` (propagation walked into the scalar's value) so it leaked
/// on every call. A scalar holds no storage; now every function-body pool in
/// this program is freed: deallocate count = allocate count - 1 (the one
/// module-level pool is never scope-freed). No reuse fires here: `z` reads
/// `y` and the return is a scalar.
let private scalarCaptureSrc =
    "type I = Idx<1000>\n"
    + "let v = method_for(range<I>) <@> lambda(i) -> 0.01 * Float64(i) |> compute\n"
    + "function f9(x: T^1) -> Float64 = {\n"
    + "    let y = x * 2.0\n"
    + "    let m = reduce(y, (+)) + 0.0\n"
    + "    let z = y - m\n"
    + "    reduce(z, (+))\n"
    + "}\n"
    + "let r = f9(v)\n"

let private scalarCaptureNoLongerPinsSource () =
    let name = "scalar_capture_no_longer_pins_source"
    match cppOfSource name scalarCaptureSrc with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        let allocs = System.Text.RegularExpressions.Regex.Matches(cpp, @"(?<!de)allocate<typename promote").Count
        let frees = System.Text.RegularExpressions.Regex.Matches(cpp, @"deallocate<typename promote").Count
        let aliases = cpp.Contains "pool reuse:"
        if frees = allocs - 1 && not aliases then
            resultLine Pass name ($"{frees} frees for {allocs} allocations (module pool excepted); no reuse")
            true
        else
            resultLine Fail name ($"expected frees = allocs - 1 and no reuse; got allocs={allocs}, frees={frees}, aliases={aliases}")
            false

/// A PARTIAL sparse read is an allocating gather (three producers, each with
/// its own teardown in nested_array_types.hpp), and none of them used to be
/// freed: `deallocate_gather_dense_trail` was never emitted at all. A
/// let-bound gather in a function body is now freed at the body's exit by the
/// teardown that matches its producer, exactly once; a gather returned
/// through an alias, or whose row VIEW is returned, is spared (the caller
/// reads it); a module-level gather is never scope-freed.
let private sparseGatherSrc =
    "let static K = [(2, 1), (0, 3), (1, 0), (0, 5)]\n"
    + "type E = Idx<4>\n"
    + "type T = Idx<2>\n"
    + "let vals: Array<Float64 like E, T> = [[21.0, 21.5], [3.0, 3.5], [10.0, 10.5], [5.0, 5.5]]\n"
    + "let S = sparse(vals, K)\n"
    + "let flat: Array<Float64 like E> = [1.0, 2.0, 3.0, 4.0]\n"
    + "let F = sparse(flat, K)\n"
    + "let static K3 = [(0, 1, 2), (0, 1, 3), (1, 0, 0), (0, 2, 2)]\n"
    + "let F3 = sparse(flat, K3)\n"
    + "function trail_rows(k: Int64) -> Int64 = {\n    let g = S((k, _))\n    let (n, t) = extents(g)\n    n * 10 + t\n}\n"
    + "function dense_sum(k: Int64) -> Float64 = {\n    let g = F((k, _))\n    reduce(g, (+))\n}\n"
    + "function nested_sum(k: Int64) -> Float64 = {\n    let g = F3((k, _, _))\n    let h = g((1, _))\n    reduce(h, (+))\n}\n"
    + "function ret_alias(k: Int64) = {\n    let g = F((k, _))\n    let h = g\n    h\n}\n"
    + "function ret_view(k: Int64) = {\n    let g = S((k, _))\n    let r = g(1)\n    r\n}\n"
    + "let a = trail_rows(0)\n"
    + "let b = dense_sum(0)\n"
    + "let c = nested_sum(0)\n"
    + "let d = ret_alias(0)\n"
    + "let e = ret_view(0)\n"
    + "let top = F((0, _))\n"

let private sparseGatherTeardowns () =
    let name = "sparse_gather_teardowns"
    match cppOfSource name sparseGatherSrc with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        let rx (p: string) = System.Text.RegularExpressions.Regex.Matches(cpp, p) |> Seq.toList
        let producers =
            rx @"auto (__v\d+) = nested_array_utilities::(make_\w*sparse_gather\w*)<"
            |> List.map (fun m -> (m.Groups.[1].Value, m.Groups.[2].Value))
        let freesOf (v: string) =
            rx (@"nested_array_utilities::(deallocate_\w+)\(" + v + @"\);")
            |> List.map (fun m -> m.Groups.[1].Value)
        // The pairing the runtime documents, independent of the compiler's table.
        let teardownOf =
            Map.ofList [ "make_partial_sparse_gather", "deallocate_sparse"
                         "make_sparse_gather_dense", "deallocate_gather_dense"
                         "make_sparse_gather_dense_trail", "deallocate_gather_dense_trail" ]
        let freed = producers |> List.filter (fun (v, _) -> not (List.isEmpty (freesOf v)))
        let spared = producers |> List.filter (fun (v, _) -> List.isEmpty (freesOf v))
        let mismatched =
            freed |> List.filter (fun (v, p) -> freesOf v <> [ teardownOf.[p] ])
        let trailFreed = freed |> List.exists (fun (_, p) -> p = "make_sparse_gather_dense_trail")
        let spareKinds = spared |> List.map snd |> List.sort
        let topFreed = (rx @"deallocate_\w+\(top\)").Length > 0
        if producers.Length = 6 && freed.Length = 4 && List.isEmpty mismatched && trailFreed
           && spareKinds = [ "make_sparse_gather_dense"; "make_sparse_gather_dense_trail" ]
           && not topFreed then
            resultLine Pass name "4 function-body gathers freed once by their producer's teardown (incl. _dense_trail); the alias- and view-returned gathers and the module gather spared"
            true
        else
            resultLine Fail name
                ($"producers={producers}; freed={freed |> List.map (fun (v, _) -> (v, freesOf v))}; spared={spared}; topFreed={topFreed}")
            false

/// An INLINE partial sparse read as a fold's operand: the fold spells its
/// operand at every element read, its extent test and its guard, so the
/// gather producer used to appear four times in one expression -- run and
/// leaked once per access. Now each site gathers ONCE (the producer appears
/// exactly once per site) and frees the binding with its own teardown. Two
/// sites with no statement scope -- a recursive array's arm and a function's
/// return expression -- and the module-level statement fold, which gathers
/// once into a temp and frees it after the fold.
let private inlineGatherSrc =
    "let static K = [(2, 1), (0, 3), (1, 0), (0, 5)]\n"
    + "type E = Idx<4>\n"
    + "let flat: Array<Float64 like E> = [1.0, 2.0, 3.0, 4.0]\n"
    + "let F = sparse(flat, K)\n"
    + "type Steps = Idx<20>\n"
    + "let rec acc: Array<Float64 like Steps> =\n"
    + "    match acc with\n"
    + "    | zero -> zero\n"
    + "    | zero :: s -> zero :: 0.0\n"
    + "    | prefix :: n -> prefix :: prefix(n - 1) + reduce(F((n % 2, _)), (+))\n"
    + "function fsum(k: Int64) -> Float64 = reduce(F((k, _)), (+))\n"
    + "let fv = fsum(0)\n"
    + "let top = reduce(F((0, _)), (+))\n"

let private inlineGatherBoundOnce () =
    let name = "inline_sparse_gather_bound_once"
    match cppOfSource name inlineGatherSrc with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        let count (p: string) = System.Text.RegularExpressions.Regex.Matches(cpp, p).Count
        let producers = count @"nested_array_utilities::make_sparse_gather_dense<"
        let frees = count @"nested_array_utilities::deallocate_gather_dense\("
        if producers = 3 && frees = 3 then
            resultLine Pass name "3 sites (rec arm, function return, module fold): each gathers once and frees once"
            true
        else
            resultLine Fail name ($"expected 3 producers and 3 frees (one each per site); got producers={producers}, frees={frees}")
            false

/// N7: every OTHER scalar consumer of an inline partial sparse read --
/// `contains` (whose loop spells the operand twice), a subscript of the
/// gather, a call argument and a top-level expression -- binds it once at the
/// consumer and frees it: every producer is paired with a free (each kernel
/// body is emitted both as its lifted function and inline in its nest, so the
/// four sites render seven times).
let private inlineGatherEveryPositionSrc =
    "let static K = [(2, 1), (0, 3), (1, 0), (0, 5)]\n"
    + "type E = Idx<4>\n"
    + "let flat: Array<Float64 like E> = [1.0, 2.0, 3.0, 4.0]\n"
    + "let F = sparse(flat, K)\n"
    + "function head1(v: T^1) -> T^0 = v(0)\n"
    + "let has2 = method_for(0..3) <@> lambda(i) -> contains(F((i, _)), 2.0) |> compute\n"
    + "let first = method_for(0..3) <@> lambda(i) -> F((i, _))(0) |> compute\n"
    + "let viaCall = method_for(0..3) <@> lambda(i) -> head1(F((i, _))) * 10.0 |> compute\n"
    + "let top = contains(F((2, _)), 1.0)\n"

let private inlineGatherEveryPosition () =
    let name = "inline_sparse_gather_every_scalar_position"
    match cppOfSource name inlineGatherEveryPositionSrc with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        let count (p: string) = System.Text.RegularExpressions.Regex.Matches(cpp, p).Count
        let producers = count @"nested_array_utilities::make_sparse_gather_dense<"
        let frees = count @"nested_array_utilities::deallocate_gather_dense\("
        if producers >= 4 && frees = producers then
            resultLine Pass name ($"4 sites (contains, subscript, call argument, top level): {producers} gathers, each freed")
            true
        else
            resultLine Fail name ($"expected every gather freed (at least 4 sites); got producers={producers}, frees={frees}")
            false

/// P3-16: a fused inner body the host reads twice is bound ONCE per cell --
/// `e * e` over `exp(x) * 3.0` evaluated exp twice per cell after fusion.
let private fusionRepeatedParamSrc =
    "let x = [1.0, 2.0, 3.0, 4.0, 5.0]\n"
    + "let z = method_for(exp(x) * 3.0) <@> lambda(e) -> e * e |> compute\n"

let private fusionRepeatedParamBoundOnce () =
    let name = "fusion_repeated_param_binds_inner_once"
    match cppOfSource name fusionRepeatedParamSrc with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        // The fused nest is the line writing `z`; the inner map's own lifted
        // kernel (`return blade_libm::exp(...)`) is emitted separately.
        let nest = cpp.Split('\n') |> Array.filter (fun l -> l.Contains "z[") |> Array.filter (fun l -> l.Contains "blade_libm::exp(")
        let perLine = nest |> Array.map (fun l -> System.Text.RegularExpressions.Regex.Matches(l, @"blade_libm::exp\(").Count)
        if nest.Length >= 1 && perLine |> Array.forall (fun c -> c = 1) then
            resultLine Pass name "the fused nest evaluates exp once per cell"
            true
        else
            resultLine Fail name ($"expected one blade_libm::exp per fused cell; got per-line counts {perLine}")
            false

/// Let-level CSE: `reduce(y, (+))` computed twice in one body (once for the
/// mean, once again for the scale) runs once; the second let is dropped and
/// its reads go to the first. Pinned as the count of scalar-fold IIFEs per
/// program (one body -- the shape-specialized copy; the dead unshaped HM copy
/// is no longer emitted -- one fold after CSE) plus the decision.
let private cseSrc =
    "type I = Idx<1000>\n"
    + "let v = method_for(range<I>) <@> lambda(i) -> 0.01 * Float64(i) |> compute\n"
    + "function twice(x: T^1) -> Float64 = {\n"
    + "    let y = x * 2.0\n"
    + "    let s1 = reduce(y, (+))\n"
    + "    let s2 = reduce(y, (+))\n"
    + "    s1 * 0.5 + s2 * 0.25\n"
    + "}\n"
    + "let r = twice(v)\n"

let private cseDropsRepeatedFold () =
    let name = "cse_drops_repeated_fold"
    match cppOfSource name cseSrc with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        let folds = System.Text.RegularExpressions.Regex.Matches(cpp, @"double __r = ").Count
        if folds = 1 then
            resultLine Pass name "one scalar fold in the specialized body; the duplicate was dropped"
            true
        else
            resultLine Fail name ($"expected 1 scalar fold in the program (one body), got {folds}")
            false

/// Run `f` with the environment variable `var` set to `value`, restoring it.
/// The optimizer gates are read per call, which is what makes this work.
let private withGate (var: string) (value: string) (f: unit -> 'a) : 'a =
    let prior = System.Environment.GetEnvironmentVariable var
    System.Environment.SetEnvironmentVariable(var, value)
    try f () finally System.Environment.SetEnvironmentVariable(var, prior)

/// The rank-2 orbit-class PASS itself, for the pins that read it: compact
/// row classes (BLADE_COMPACT_ROW_CLASSES) would otherwise replace it behind
/// the producer.
let private orbitPass (f: unit -> 'a) : 'a = withGate "BLADE_COMPACT_ROW_CLASSES" "0" f

// ---------------------------------------------------------------------------
// Hot-row fold scheduling (Blade.Optimize.planHotRowFolds; bench review M13).
// The values are pinned by corpus loops/474-476; these pins read the emission
// (the row-sum array exists, its store sits INSIDE the producer's outer loop,
// the consumer folds it) and the decision record of each decline.
// ---------------------------------------------------------------------------

let private hotRowSrc =
    "type Y = Idx<9>\n"
    + "type X = Idx<11>\n"
    + "let u = method_for(range<Y, X>) <@> lambda(i, j) -> Float64(Int64(i) * Int64(j)) |> compute\n"
    + "let lap = method_for(range<halo<Y, [-1, 0, 1]>, halo<X, [-1, 0, 1]>>) <@> lambda(wr, wc) where omp(wr: 1) -> u(wr(-1), wc(0)) + u(wr(1), wc(0)) + u(wr(0), wc(-1)) + u(wr(0), wc(1)) - 4.0 * u(wr(0), wc(0)) |> compute\n"
    + "let ss = reduce(method_for(lap) <@> lambda(row) -> prodsum(row, row), lambda(p, q) where omp -> p + q, 0.0)\n"

/// The schedule emitted: a row-sum array, its per-row store inside the
/// producer's outer loop (before that loop's closing brace, after the inner
/// one's), and the consumer's fold over it.
let private hotRowEmitted () =
    let name = "hot_row_fold_emitted_inside_producer_loop"
    match cppOfSource name hotRowSrc with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        let store = System.Text.RegularExpressions.Regex.Match(cpp, @"__hr_ss\[__i0\] = ")
        let outerOpen = cpp.IndexOf "for (size_t __i0 = 0; __i0 < 7; __i0++)"
        let innerOpen = cpp.IndexOf "for (size_t __i1 = 0; __i1 < 9; __i1++)"
        let combine = cpp.IndexOf "ss = ss + __hr_ss["
        if store.Success && outerOpen >= 0 && innerOpen > outerOpen && store.Index > innerOpen
           && combine > store.Index && cpp.Contains "Array<double, 1> __hr_ss = " then
            resultLine Pass name "row-sum array allocated, filled behind each produced row, folded by the consumer"
            true
        else
            resultLine Fail name ($"store={store.Success}@{store.Index} outer={outerOpen} inner={innerOpen} combine={combine}")
            false

/// BLADE_HOT_ROW=0: the two-pass form, and the decision says why.
let private hotRowGateOff () =
    let name = "hot_row_gate_off_keeps_two_passes"
    match withGate "BLADE_HOT_ROW" "0" (fun () -> cppOfSource name hotRowSrc) with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        if not (cpp.Contains "__hr_ss") then
            resultLine Pass name "no row-sum array with the gate off"
            true
        else
            resultLine Fail name "a row-sum array was emitted with BLADE_HOT_ROW=0"
            false

/// A `where omp` fold over a producer whose outer level carries no licence
/// keeps its Path B team: declined.
let private hotRowLostTeamSrc =
    hotRowSrc.Replace("lambda(wr, wc) where omp(wr: 1) ->", "lambda(wr, wc) ->")

/// A row kernel that reads the producer beyond its own row: declined.
let private hotRowReadsBeyondSrc =
    hotRowSrc.Replace("prodsum(row, row), lambda", "prodsum(row, row) + lap(0, 0), lambda")

// ---------------------------------------------------------------------------
// Round-2 cell epilogues (B1 rank-1 producer epilogue, B2r row chain
// interleave). The values are pinned by corpus loops/480-483 and
// recursive-arrays/030, the licensed bits by `blade test cell-epilogue`;
// these pins read the emission (which form fired, where the chain sits) and
// the `cell-epilogue` decision of every refusal -- most of which are made at
// EMISSION, so the decisions are collected across lowering AND codegen.
// ---------------------------------------------------------------------------

let private cepInputs =
    "type Ix = Idx<1003>\n"
    + "function inputs(m: Int64) -> Array<Float64 like Ix> = {\n"
    + "    method_for(range<Ix>) <@> lambda(i) -> Float64(Int64(i) % m) |> compute\n"
    + "}\n"

/// The saxpy shape in a function body (the bench kernel's).
let private cepSaxpySrc =
    cepInputs
    + "function kernel(x: Array<Float64 like Ix>, y: Array<Float64 like Ix>) -> Float64 = {\n"
    + "    let y2 = method_for(zip(x, y)) <@> lambda(u, v) where omp(u: 1) -> 1.5 * u + v |> compute\n"
    + "    let s = reduce(method_for(y2) <@> lambda(w) -> w * w, lambda(p, q) where omp -> p + q, 0.0)\n"
    + "    s\n"
    + "}\n"
    + "let r = kernel(inputs(7), inputs(5))\n"

let private cepDecisionsOf (src: string) : Result<Blade.Effects.Decision list, string> =
    Blade.Effects.Decisions.start ()
    let r =
        try
            match lower src with
            | Error e -> Error ($"lower: {e}")
            | Ok ir ->
                CodeGen.genSelfContainedProgramFromIR ir "cep_decisions" |> ignore
                CodeGen.takeUnhandledIRNodeDiagnostics () |> ignore
                Ok (Blade.Effects.Decisions.drain ())
        with ex -> Error ($"codegen raised: {ex.Message}")
    Blade.Effects.Decisions.drain () |> ignore
    r

/// A `cell-epilogue` decision over lowering + codegen.
let private cepDecisionCase (name: string) (src: string) (want: Blade.Effects.Decision -> bool) (describe: string) =
    match cepDecisionsOf src with
    | Error e -> resultLine Fail name e; false
    | Ok ds ->
        let mine = ds |> List.filter (fun d -> d.Rule = "cell-epilogue")
        if mine |> List.exists want then
            resultLine Pass name ($"{mine.Length} `cell-epilogue` decision(s); {describe}")
            true
        else
            let seen = mine |> List.map Blade.Effects.Decisions.render |> String.concat " | "
            let shown = if seen = "" then "no decisions" else seen
            resultLine Fail name ($"wanted {describe}; saw: {shown}")
            false

let private kernelBody (cpp: string) =
    let at = cpp.IndexOf "double kernel(Array<double, 1> x, Array<double, 1> y) {"
    if at < 0 then "" else
    let rest = cpp.Substring at
    let close = rest.IndexOf "\n}\n"
    if close < 0 then rest else rest.Substring(0, close)

let private countOf (needle: string) (s: string) =
    System.Text.RegularExpressions.Regex.Matches(s, System.Text.RegularExpressions.Regex.Escape needle).Count

/// Serial licensed: the P1 lane state fed in the producer's block loop, and
/// no second pass over the stored output.
let private cepSaxpySerialLicensed () =
    let name = "cell_epilogue_saxpy_serial_licensed_streams_lane_state"
    match withGate "BLADE_OMP_THREADS" "1" (fun () -> cppOfSource name cepSaxpySrc) with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        let k = kernelBody cpp
        let feed = k.IndexOf "blade_fold::feed_sq<double, 8>(__cep_ls, __fp___v"
        let produce = k.IndexOf "[__fk] = ((1.5 * __fp_x[__fk]) + __fp_y[__fk]);"
        if feed > produce && produce >= 0 && k.Contains "blade_fold::finish_sq<double, 8>(__cep_ls"
           && not (k.Contains "sq<double, 8>(__rsrc0") then
            resultLine Pass name "feed_sq behind each producer block, finish_sq on the tail, no sq<double, 8>(__rsrc0 re-read"
            true
        else
            resultLine Fail name ($"""feed={feed} produce={produce} finish={k.Contains "finish_sq"} reread={k.Contains "sq<double, 8>(__rsrc0"}""")
            false

/// mt8 licensed: ONE team (the producer runs in the fold's Path B region).
let private cepSaxpyThreadedLicensed () =
    let name = "cell_epilogue_saxpy_threaded_one_team"
    match withGate "BLADE_OMP_THREADS" null (fun () -> cppOfSource name cepSaxpySrc) with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        let k = kernelBody cpp
        let teams = countOf "BLADE_OMP_PARALLEL_NUM_THREADS" k
        let forSimd = countOf "BLADE_OMP_PARALLEL_FOR_SIMD" k
        if teams = 1 && forSimd = 0 && k.Contains "blade_fold::chunk_lo(__fp_cells" && k.Contains "feed_sq<double, 8>"
           && not (k.Contains "__rpart") then
            resultLine Pass name "exactly one BLADE_OMP_PARALLEL_NUM_THREADS; chunk_lo chunks produce and feed; no second Path B team"
            true
        else
            resultLine Fail name ($"""teams={teams} parallel-for-simd={forSimd} chunk_lo={k.Contains "chunk_lo"} rpart={k.Contains "__rpart"}""")
            false

/// Repro serial: the chain statement sits INSIDE the producer loop, right
/// behind the store, and no reload loop follows.
let private cepSaxpyRepro () =
    let name = "cell_epilogue_saxpy_repro_chain_inside_producer"
    match withGate "BLADE_FP_REPRO" "1" (fun () -> withGate "BLADE_OMP_THREADS" "1" (fun () -> cppOfSource name cepSaxpySrc)) with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        let k = kernelBody cpp
        let store = k.IndexOf "[__fk] = __c;"
        let chain = k.IndexOf "__cep_acc___v"
        let step = System.Text.RegularExpressions.Regex.Match(k, @"__cep_acc___v\d+ = __wrap_\d+_cep___v\d+\(__cep_acc___v\d+, \(__c \* __c\)\);")
        if store >= 0 && step.Success && step.Index > store && chain >= 0
           && not (k.Contains "for (size_t __i0 = 0; __i0 < 1003; __i0++)") then
            resultLine Pass name "the chain's `acc = F(acc, c * c)` right behind the cell's store; no standalone reload loop"
            true
        else
            resultLine Fail name ($"""store={store} step={step.Success}@{step.Index} reload={k.Contains "for (size_t __i0 = 0; __i0 < 1003; __i0++)"}""")
            false

/// BLADE_CELL_EPILOGUE=0: the two-pass forms.
let private cepGateOff () =
    let name = "cell_epilogue_gate_off_keeps_two_passes"
    let off f = withGate "BLADE_CELL_EPILOGUE" "0" f
    match off (fun () -> withGate "BLADE_OMP_THREADS" "1" (fun () -> cppOfSource name cepSaxpySrc)),
          off (fun () -> withGate "BLADE_FP_REPRO" "1" (fun () -> cppOfSource name hotRowSrc)) with
    | Error e, _ | _, Error e -> resultLine Fail name e; false
    | Ok saxpy, Ok stencil ->
        let k = kernelBody saxpy
        if k.Contains "sq<double, 8>(__rsrc0" && not (k.Contains "feed_sq") && not (stencil.Contains "__hrc_")
           && stencil.Contains "__hr_ss[__i0] = [&]()" then
            resultLine Pass name "saxpy re-reads y2 with sq<double, 8>(__rsrc0; the stencil's row fold is the IIFE on the finished row"
            true
        else
            resultLine Fail name ($"""saxpy reread={k.Contains "sq<double, 8>(__rsrc0"} feed={k.Contains "feed_sq"} stencil chain={stencil.Contains "__hrc_"}""")
            false

/// B2r stencil under repro: the row chain declared in the outer body, the
/// `+=` inside the inner loop right behind the store, the epilogue storing
/// the chain (no IIFE over the finished row).
let private cepStencilRepro () =
    let name = "cell_epilogue_hot_row_repro_chain_in_cell_loop"
    match withGate "BLADE_FP_REPRO" "1" (fun () -> cppOfSource name hotRowSrc) with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        let decl = cpp.IndexOf "double __hrc_ss = 0;"
        let inner = cpp.IndexOf "for (size_t __i1 = 0; __i1 < 9; __i1++)"
        let step = cpp.IndexOf "__orow_lap[__i1] = __c; __hrc_ss += __c * __c; }"
        let store = cpp.IndexOf "__hr_ss[__i0] = __hrc_ss;"
        if decl >= 0 && inner > decl && step > inner && store > step && not (cpp.Contains "__hr_ss[__i0] = [&]()") then
            resultLine Pass name "chain seeded +0 per row, advanced per cell behind the store, stored as the row's value"
            true
        else
            resultLine Fail name ($"decl={decl} inner={inner} step={step} store={store}")
            false

let private cepRecurSrc =
    "type T = Idx<7>\n"
    + "type X = Idx<101>\n"
    + "function total(u0: Array<Float64 like X>) -> Float64 = {\n"
    + "    let rec traj: Array<Float64 like T, X> =\n"
    + "        match traj with\n"
    + "        | zero -> zero\n"
    + "        | zero :: s -> zero :: u0\n"
    + "        | prefix :: n -> prefix :: prefix(n - 1) * 0.5 + 1.0\n"
    + "    reduce(method_for(traj) <@> lambda(row) -> prodsum(row, row), (+))\n"
    + "}\n"
    + "let u0: Array<Float64 like X> = method_for(range<X>) <@> lambda(i) -> Float64(Int64(i) % 3) |> compute\n"
    + "let tot = total(u0)\n"

/// B2r recursion under repro: the step's cell loop carries the chain, the
/// fold let binds it; only the SEED row keeps its prodsum IIFE.
let private cepRecurRepro () =
    let name = "cell_epilogue_recursion_repro_chain_in_cell_loop"
    match withGate "BLADE_FP_REPRO" "1" (fun () -> cppOfSource name cepRecurSrc) with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        let at = cpp.IndexOf "double total(Array<double, 1> u0) {"
        let body = if at < 0 then "" else cpp.Substring(at, (cpp.IndexOf("\n}\n", at) - at))
        let decl = System.Text.RegularExpressions.Regex.Match(body, @"double (__cfc_\d+) = 0;")
        let acc = if decl.Success then decl.Groups.[1].Value else "__cfc_?"
        let step = System.Text.RegularExpressions.Regex.Match(body, System.Text.RegularExpressions.Regex.Escape acc + @" \+= (__v\d+) \* \1;")
        let bind = body.Contains $" = {acc};"
        let iifes = countOf "__ps += " body
        if decl.Success && step.Success && bind && iifes = 1 then
            resultLine Pass name ($"`{acc}` seeded +0 per step, `+=` behind the cell's store, bound by the fold let; one IIFE left (the seed row)")
            true
        else
            resultLine Fail name ($"decl={decl.Success} step={step.Success} bind={bind} iifes={iifes}")
            false

let private cepLostTeamSrc = cepSaxpySrc.Replace("lambda(u, v) where omp(u: 1) ->", "lambda(u, v) ->")
let private cepSerialFoldSrc = cepSaxpySrc.Replace("lambda(p, q) where omp -> p + q", "lambda(p, q) -> p + q")
let private cepBeyondSrc = cepSaxpySrc.Replace("lambda(w) -> w * w", "lambda(w) -> w * w + y2((1 : Ix))")
let private cepBetweenSrc =
    cepSaxpySrc.Replace("    let s = reduce(", "    let m = y2((3 : Ix))\n    let s = reduce(").Replace("    s\n}", "    s + m\n}")
let private cepTermSrc = cepSaxpySrc.Replace("lambda(w) -> w * w", "lambda(w) -> w * 2.0")
let private cepSeedSrc = cepSaxpySrc.Replace("p + q, 0.0)", "p + q, Float64(extents(x)))")
let private cepKernelSrc = cepSaxpySrc.Replace("lambda(p, q) where omp -> p + q", "lambda(p, q) -> p + q * 1.0")
let private cepProductSrc = cepSaxpySrc.Replace("lambda(p, q) where omp -> p + q", "lambda(p, q) where omp -> p * q")
let private cepRowKernelSrc = hotRowSrc.Replace("prodsum(row, row)", "reduce(row, (+), 0.0)")
let private cepFlatGateSrc =
    // A PARTIAL omp licence over a rank-1 sweep is fine; a rank-1 producer
    // whose kernel reads two cells is not elementwise: the flat path refuses.
    cepSaxpySrc.Replace("let y2 = method_for(zip(x, y)) <@> lambda(u, v) where omp(u: 1) -> 1.5 * u + v |> compute",
                        "let y2 = method_for(range<Ix>) <@> lambda(i) where omp(i: 1) -> 1.5 * x(i) + y(i) |> compute")

/// `cell-epilogue` decisions recorded while the LLVM lane emits the program.
let private cepLlvmDecisionCase (name: string) (src: string) (want: Blade.Effects.Decision -> bool) (describe: string) =
    Blade.Effects.Decisions.start ()
    let r =
        try
            match lower src with
            | Error e -> Error ($"lower: {e}")
            | Ok ir ->
                match EmitLlvm.tryEmitProgramNamed "cep_llvm_decisions" ir with
                | Error why -> Error ($"the llvm lane refused: {why}")
                | Ok _ -> Ok (Blade.Effects.Decisions.drain ())
        with ex -> Error ($"llvm emission raised: {ex.Message}")
    Blade.Effects.Decisions.drain () |> ignore
    match r with
    | Error e -> resultLine Fail name e; false
    | Ok ds ->
        let mine = ds |> List.filter (fun d -> d.Rule = "cell-epilogue")
        if mine |> List.exists want then
            resultLine Pass name ($"{mine.Length} llvm `cell-epilogue` decision(s); {describe}")
            true
        else
            let seen = mine |> List.map Blade.Effects.Decisions.render |> String.concat " | "
            let shown = if seen = "" then "no decisions" else seen
            resultLine Fail name ($"wanted {describe}; saw: {shown}")
            false

// Compaction fusion (Blade.Optimize.planCompactionFusion; round 2 item B4,
// bench maskagg). Values are pinned by corpus index-types/515-518; these pins
// read the emission (the count in the mask pass, ONE fill+gather pass, no
// `y * y` pool, the fold form per build) and the decision record of each
// decline. The licensed form is invisible to the repro-pinned differentials,
// so it is pinned here. The round-2 cases run under the `b4` mirror
// (BLADE_COMPACTION_FUSION=b4, the exact round-2 form); the round-3 default
// (v2: T4b, positions elided under the demand census) and its opt-in T4a
// (`gather`: the gather in the mask pass) are pinned by the compactionV2
// cases, values by corpus index-types/522-531 and multifile/037-040.
// ---------------------------------------------------------------------------

let private compactionSrc =
    "type Ix = Idx<1003>\n"
    + "let x = method_for(range<Ix>) <@> lambda(i) -> Float64((Int64(i) * 37) % 11) - 4.0 |> compute\n"
    + "let keep = mask(x, lambda(v) -> v > 1.0)\n"
    + "let y = compound(x, keep)\n"
    + "let checksum = reduce(y * y, (+)) + Float64(extents(y))\n"

/// The b4 mirror, licensed (the suite's default build): the mask pass counts,
/// one fused fill+gather pass, the index adopts the filled table, no square
/// pool, and the fold is the K-lane self-dot over `y.data`.
let private compactionFusedLicensed () =
    let name = "compaction_fusion_one_pass_licensed_sq_over_buffer"
    match withGate "BLADE_COMPACTION_FUSION" "b4" (fun () -> cppOfSource name compactionSrc) with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        let checks =
            [ "no y*y pool", not (cpp.Contains "new double[y.idx->cardinality")
              "count in the mask pass", cpp.Contains "keep__count += (size_t)__keep_k;"
              "exactly one fill+gather pass", countOf "y_q += (size_t)y_idx_pool[y_i];" cpp = 1
              "no gather by position", not (cpp.Contains "rank_to_tuple[y_q]")
              "no counting constructor", not (cpp.Contains "new compound_index_t<1>(\"y_idx\", y_idx_grid, y_idx_pool)")
              "m + 1 cells in both buffers", cpp.Contains "new compound_index_t<1>::IDX[keep__count + 1]" && cpp.Contains "new double[keep__count + 1]"
              "adopting constructor", cpp.Contains "new compound_index_t<1>(\"y_idx\", y_idx_grid, keep__count, y_pos)"
              "K-lane self-dot over y.data", cpp.Contains "blade_fold::sq<double, 8>(y.data, y.idx->cardinality)"
              "BL8003 guard at the fold", cpp.Contains "if (y.idx->cardinality == 0) { blade_rt::panic(\"BL8003\"" ]
        match checks |> List.filter (snd >> not) with
        | [] -> resultLine Pass name "count in the mask pass, one fused pass, no square pool, sq<double, 8> over y.data"; true
        | bad -> resultLine Fail name ("missing: " + (bad |> List.map fst |> String.concat ", ")); false

/// BLADE_FP_REPRO=1: the serial chain is fed inside the compaction pass (peel
/// the first kept square, then the chain over completed cells); the fold's
/// site keeps the empty check and returns the accumulator.
let private compactionFusedRepro () =
    let name = "compaction_fusion_repro_chain_fed_in_pass"
    match withGate "BLADE_COMPACTION_FUSION" "b4" (fun () -> withGate "BLADE_FP_REPRO" "1" (fun () -> cppOfSource name compactionSrc)) with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        let pass = cpp.IndexOf "y_q += (size_t)y_idx_pool[y_i];"
        let peel = cpp.IndexOf "y__fold0 = __t;"
        let step = cpp.IndexOf "y__fold0 = __wrap_"
        let ret = cpp.IndexOf "return y__fold0;"
        let checks =
            [ "no y*y pool", not (cpp.Contains "new double[y.idx->cardinality")
              "one fused pass", countOf "y_q += (size_t)y_idx_pool[y_i];" cpp = 1
              "chain only over completed cells", cpp.Contains "for (; y_f < y_q; y_f++)"
              "peel, then the chain, inside the pass, before the fold reads it", pass >= 0 && peel > pass && step > peel && ret > step
              "products rounded before the add", countOf "BLADE_FOLD_ROUND(__t);" cpp = 2
              "no K-lane form", not (cpp.Contains "blade_fold::sq<double")
              "BL8003 guard at the fold", cpp.Contains "if (y.idx->cardinality == 0) { blade_rt::panic(\"BL8003\"" ]
        match checks |> List.filter (snd >> not) with
        | [] -> resultLine Pass name "peeled chain fed per block inside the fused pass; the fold returns it behind the empty check"; true
        | bad -> resultLine Fail name ("missing: " + (bad |> List.map fst |> String.concat ", ")); false

/// BLADE_COMPACTION_FUSION=0: the old passes (count in the constructor, the
/// gather by position, the `y * y` pool).
let private compactionGateOff () =
    let name = "compaction_fusion_gate_off_keeps_old_passes"
    match withGate "BLADE_COMPACTION_FUSION" "0" (fun () -> cppOfSource name compactionSrc) with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        if cpp.Contains "new double[y.idx->cardinality" && cpp.Contains "rank_to_tuple[y_q]"
           && cpp.Contains "new compound_index_t<1>(\"y_idx\", y_idx_grid, y_idx_pool)"
           && not (cpp.Contains "keep__count") then
            resultLine Pass name "counting constructor, gather by position and the square pool, as before"
            true
        else
            resultLine Fail name "the gate-off emission is not the old pass structure"
            false

/// Declines: the mask read between the mask and the compound; a predicate the
/// planner cannot judge total; a rank-2 mask; a fold kernel that asks for a
/// thread team (Path B); a map that is not the self-product.
let private compactionMaskReadBetweenSrc =
    compactionSrc.Replace("let y = compound(x, keep)\n", "let k0 = keep(0)\nlet y = compound(x, keep)\n")
let private compactionIntrinsicPredSrc =
    compactionSrc.Replace("lambda(v) -> v > 1.0", "lambda(v) -> sqrt(v + 4.0) > 2.0")
let private compactionRank2Src =
    "type R = Idx<3>\n"
    + "type C = Idx<4>\n"
    + "let a = method_for(range<R, C>) <@> lambda(i, j) -> Float64(Int64(i) + Int64(j)) |> compute\n"
    + "let km = mask(a, lambda(v) -> v > 2.0)\n"
    + "let z = compound(a, km)\n"
let private compactionOmpFoldSrc =
    compactionSrc.Replace("reduce(y * y, (+))", "reduce(y * y, lambda(p, q) where omp -> p + q)")
let private compactionNotSquareSrc =
    compactionSrc.Replace("reduce(y * y, (+))", "reduce(y * 2.0, (+))")

/// Writes: a named map materialized, then a mutating call on y, then the
/// fold of the map (B4a must keep the snapshot); and a write through an alias
/// between the compaction and the fold (B4c must not feed the chain early).
let private compactionMutatedSnapshotSrc =
    "type Ix = Idx<9>\n"
    + "let x = method_for(range<Ix>) <@> lambda(i) -> Float64(Int64(i)) |> compute\n"
    + "let keep = mask(x, lambda(v) -> v > 1.5)\n"
    + "let mut y = compound(x, keep)\n"
    + "function overwrite(a: mut Array<Float64 like CompoundIdx<keep>>) -> Int64 = {\n"
    + "    a(2) = 50.0\n"
    + "    1\n"
    + "}\n"
    + "let s = {\n"
    + "    let yy = y * y |> compute\n"
    + "    let changed = overwrite(y)\n"
    + "    reduce(yy, (+))\n"
    + "}\n"
let private compactionAliasWriteSrc =
    "type Ix = Idx<9>\n"
    + "let x = method_for(range<Ix>) <@> lambda(i) -> Float64(Int64(i)) |> compute\n"
    + "let keep = mask(x, lambda(v) -> v > 1.5)\n"
    + "let y = compound(x, keep)\n"
    + "function overwrite(a: mut Array<Float64 like CompoundIdx<keep>>) -> Int64 = {\n"
    + "    a(2) = 50.0\n"
    + "    1\n"
    + "}\n"
    + "let mut alias = y\n"
    + "let changed = overwrite(alias)\n"
    + "let s = reduce(y * y, (+))\n"

let private compactionAliasWriteNoInPass () =
    let name = "compaction_fusion_alias_write_keeps_fold_at_its_site"
    match withGate "BLADE_COMPACTION_FUSION" "b4" (fun () -> withGate "BLADE_FP_REPRO" "1" (fun () -> cppOfSource name compactionAliasWriteSrc)) with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        if not (cpp.Contains "y__fold0") && countOf "y_q += (size_t)y_idx_pool[y_i];" cpp = 1
           && not (cpp.Contains "new double[y.idx->cardinality") then
            resultLine Pass name "fused compaction, no in-pass chain, the fold reads y at its own site"
            true
        else
            resultLine Fail name "an in-pass chain (or the square pool) survived a write through an alias"
            false

// --- Compaction fusion v2 (round 3, T4) --------------------------------------
// T4b (the DEFAULT): the kept-position table elided when the demand census
// certifies no consumer reads it; the gather stays at the compound (B4's one
// pass). T4a (OPT-IN, BLADE_COMPACTION_FUSION=gather): `keep`'s own pass
// counts AND gathers (one sweep of x from memory, n + 1 private cells; the
// strict chain fed there). Every admission audits a binding's whole payload
// (side-table operands included). Values: corpus index-types/522-531 (515-521
// unchanged), multifile/037-040.

let private checkAll (name: string) (cpp: string) (summary: string) (checks: (string * bool) list) =
    match checks |> List.filter (snd >> not) with
    | [] -> resultLine Pass name summary; true
    | bad -> resultLine Fail name ("missing: " + (bad |> List.map fst |> String.concat ", ")); ignore cpp; false

let private withGather (f: unit -> 'a) : 'a = withGate "BLADE_COMPACTION_FUSION" "gather" f

/// The default (T4b only): the count in the mask pass, B4's one fill+gather
/// pass at the compound with NO position store, the cardinality-only index,
/// the K-lane self-dot over y.data.
let private compactionV2DefaultElides () =
    let name = "compaction_v2_default_positions_elided_gather_at_compound"
    match cppOfSource name compactionSrc with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        checkAll name cpp "count in the mask pass, B4's pass at the compound without the positions fill, cardinality-only index"
            [ "count in the mask pass", cpp.Contains "keep__count += (size_t)__keep_k;"
              "no mask-site gather (T4a is opt-in)", not (cpp.Contains "keep__gv")
              "one compound-site pass", countOf "y_q += (size_t)y_idx_pool[y_i];" cpp = 1
              "m + 1 value cells", cpp.Contains "double* y_compact = new double[keep__count + 1];"
              "no positions fill", not (cpp.Contains "y_pos") && not (cpp.Contains "[0] = y_i;")
              "cardinality-only index", cpp.Contains "new compound_index_t<1>(\"y_idx\", y_idx_grid, keep__count, compound_index_t<1>::positions_elided)"
              "K-lane self-dot over y.data", cpp.Contains "blade_fold::sq<double, 8>(y.data, y.idx->cardinality)"
              "BL8003 guard at the fold", cpp.Contains "if (y.idx->cardinality == 0) { blade_rt::panic(\"BL8003\"" ]

/// `gather`, licensed: the gather in the mask pass, one sweep of x from
/// memory, n + 1 cells, no positions (the fold and `extents` read only the
/// buffer and the cardinality), the cardinality-only index, no compound-site
/// pass, no square pool, the K-lane self-dot at the fold's own site.
let private compactionV2Licensed () =
    let name = "compaction_v2_gather_at_mask_positions_elided"
    match withGather (fun () -> cppOfSource name compactionSrc) with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        checkAll name cpp "one sweep of x at the mask (per block: predicate, then gather), cardinality-only index, sq<double, 8> over y.data"
            [ "gather in the mask pass", cpp.Contains "keep__gv[keep__count] = x[__mi];"
              "one sweep of x: per 256-cell block the predicate loop, then the gather loop",
                cpp.Contains "for (size_t keep__g_b = 0; keep__g_b < 1003; keep__g_b += 256)"
                && countOf "for (size_t __mi = keep__g_b; __mi < keep__g_e; __mi++)" cpp = 2
                && not (cpp.Contains "pool_base(x.data)")
              "n + 1 private value cells", cpp.Contains "double* keep__gv = new double[(size_t)(1003) + 1];"
              "no position buffer (no positions fill)", not (cpp.Contains "keep__gp") && not (cpp.Contains "y_pos") && not (cpp.Contains "[0] = __mi;")
              "cardinality-only index", cpp.Contains "new compound_index_t<1>(\"y_idx\", y_idx_grid, keep__count, compound_index_t<1>::positions_elided)"
              "no compound-site pass", not (cpp.Contains "y_q += ")
              "no square pool", not (cpp.Contains "new double[y.idx->cardinality")
              "K-lane self-dot over y.data", cpp.Contains "blade_fold::sq<double, 8>(y.data, y.idx->cardinality)"
              "BL8003 guard at the fold", cpp.Contains "if (y.idx->cardinality == 0) { blade_rt::panic(\"BL8003\"" ]

/// `gather`, BLADE_FP_REPRO=1: B4c's chain moves into the mask pass unchanged
/// -- per 256-cell block, completed cells only, the first square peeled --
/// and the fold's site keeps the empty check and returns the accumulator.
let private compactionV2Repro () =
    let name = "compaction_v2_gather_repro_chain_in_mask_pass"
    match withGather (fun () -> withGate "BLADE_FP_REPRO" "1" (fun () -> cppOfSource name compactionSrc)) with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        let pass = cpp.IndexOf "keep__gv[keep__count] = x[__mi];"
        let peel = cpp.IndexOf "keep__fold0 = __t;"
        let step = cpp.IndexOf "keep__fold0 = __wrap_"
        let ret = cpp.IndexOf "return keep__fold0;"
        checkAll name cpp "the peeled chain fed per block inside the mask pass; the fold returns it behind the empty check"
            [ "blocked mask pass", cpp.Contains "for (size_t __mi = keep__g_b; __mi < keep__g_e; __mi++)"
              "chain only over completed cells", cpp.Contains "for (; keep__g_f < keep__count; keep__g_f++)"
              "gather, peel, chain, then the fold reads it", pass >= 0 && peel > pass && step > peel && ret > step
              "products rounded before the add", countOf "BLADE_FOLD_ROUND(__t);" cpp = 2
              "no compound-site pass", not (cpp.Contains "y_q += ")
              "no K-lane form", not (cpp.Contains "blade_fold::sq<double")
              "BL8003 guard at the fold", cpp.Contains "if (y.idx->cardinality == 0) { blade_rt::panic(\"BL8003\"" ]

/// A keyed read demands the table: under `gather` the mask pass fills the
/// kept positions too, and the index adopts them.
let private compactionV2KeyedSrc = compactionSrc + "let k5 = y(5)\n"
let private compactionV2KeyedKeepsPositions () =
    let name = "compaction_v2_gather_keyed_read_keeps_positions"
    match withGather (fun () -> cppOfSource name compactionV2KeyedSrc) with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        checkAll name cpp "the mask pass fills the kept positions; the index adopts them"
            [ "positions filled in the mask pass", cpp.Contains "keep__gp[keep__count][0] = __mi;"
              "n + 1 position cells", cpp.Contains "compound_index_t<1>::IDX* keep__gp = new compound_index_t<1>::IDX[(size_t)(1003) + 1];"
              "adopting constructor", cpp.Contains "new compound_index_t<1>(\"y_idx\", y_idx_grid, keep__count, keep__gp)"
              "not elided", not (cpp.Contains "positions_elided)") ]

/// `gather` over the selectivity cap: (n + 1) x 8 B over sel_cap=1 KiB keeps
/// B4's two passes (count in the mask pass, one fill+gather pass at the
/// compound, m + 1 cells); the positions are still elided (T4b is
/// independent).
let private compactionV2SelCapFallback () =
    let name = "compaction_v2_gather_sel_cap_keeps_b4_passes"
    match withGather (fun () -> withGate "BLADE_TILE_TUNE" "sel_cap=1" (fun () -> cppOfSource name compactionSrc)) with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        checkAll name cpp "over the cap: B4's compound-site pass, m + 1 cells, positions still elided"
            [ "no mask-site gather", not (cpp.Contains "keep__gv")
              "count in the mask pass", cpp.Contains "keep__count += (size_t)__keep_k;"
              "one compound-site pass", countOf "y_q += (size_t)y_idx_pool[y_i];" cpp = 1
              "m + 1 value cells", cpp.Contains "new double[keep__count + 1]"
              "no positions", not (cpp.Contains "y_pos") && cpp.Contains "positions_elided)" ]

/// `gather` over a run-time source extent: the single pass runs under a
/// run-time cap test, the compound falls back to B4's pass into the same
/// buffers otherwise.
let private compactionV2RuntimeSrc =
    "let k = reduce([3, 4, 5], (+))\n"
    + "let xs = 0..k |> compute\n"
    + "let keep = mask(xs, lambda(v) -> v > 4)\n"
    + "let y = compound(xs, keep)\n"
    + "let s = reduce(y, (+))\n"
let private compactionV2RuntimeGuard () =
    let name = "compaction_v2_gather_runtime_extent_guarded_single_pass"
    match withGather (fun () -> cppOfSource name compactionV2RuntimeSrc) with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        checkAll name cpp "a run-time cap test at the mask; B4's pass at the compound when it fails"
            [ "the run-time cap test", cpp.Contains "const bool keep__one = (size_t)("
              "buffers declared before the branch", cpp.Contains "int64_t* keep__gv = nullptr;"
              "single pass under the guard", cpp.Contains "if (keep__one) {" && cpp.Contains "keep__gv[keep__count] = xs[__mi];"
              "counted-only pass otherwise", cpp.Contains "} else {"
              "B4 fallback at the compound", cpp.Contains "if (!keep__one) {" && cpp.Contains "keep__gv = new int64_t[keep__count + 1];"
              "positions elided", cpp.Contains "positions_elided)" ]

// Demand-kind sources for the census decision cases: one use each that reads
// source positions.
let private compactionV2DemandSrc (uses: string) =
    "type Ix = Idx<13>\n"
    + "let x = method_for(range<Ix>) <@> lambda(i) -> Float64((Int64(i) * 3) % 13) |> compute\n"
    + "let keep = mask(x, lambda(v) -> v > 5.5)\n"
    + "let y = compound(x, keep)\n"
    + uses
let private compactionV2WritingProducerSrc =
    "type Ix = Idx<13>\n"
    + "let mut calls = 0\n"
    + "let x = method_for(range<Ix>) <@> lambda(i) -> {\n"
    + "    calls = calls + 1\n"
    + "    Float64(Int64(i) % 5)\n"
    + "} |> compute\n"
    + "let keep = mask(x, lambda(v) -> v > 1.5)\n"
    + "let y = compound(x, keep)\n"
    + "let s = reduce(y * y, (+))\n"
let private compactionV2SharedMaskSrc =
    compactionSrc + "let y2 = compound(x, keep)\nlet s2 = reduce(y2, (+))\n"
// W1-4 review finding 2: a HIDDEN writer between the mask and the compound --
// a random fill's key (its binding value is a placeholder) and another
// compound's computed dense operand (distinct mask), each writing x.
let private compactionV2HiddenWriterHead =
    "type Ix = Idx<2>\n"
    + "function bump(a: mut Array<Float64 like Ix>) -> Int64 = {\n"
    + "    a(0) = 9.0\n"
    + "    7\n"
    + "}\n"
    + "function bumped(a: mut Array<Float64 like Ix>) -> Array<Float64 like Ix> = {\n"
    + "    a(0) = 9.0\n"
    + "    let b = a + 0.0 |> compute\n"
    + "    b\n"
    + "}\n"
    + "let mut x: Array<Float64 like Ix> = [1.0, 2.0]\n"
    + "let keep = mask(x, lambda(v) -> v > 0.0)\n"
let private compactionV2RandomKeyWriterSrc =
    "import rand as r\n" + compactionV2HiddenWriterHead
    + "let noise = r.uniform(bump(x), 1)\n"
    + "let y = compound(x, keep)\n"
    + "let s = reduce(y, (+))\n"
let private compactionV2CompoundOperandWriterSrc =
    compactionV2HiddenWriterHead
    + "let other = mask(x, lambda(v) -> v > 0.5)\n"
    + "let zz = compound(bumped(x), other)\n"
    + "let y = compound(x, keep)\n"
    + "let s = reduce(y, (+))\n"
let private compactionV2ProviderWriteSrc =
    "import csv as c\n"
    + "type Ix = Idx<13>\n"
    + "let x = method_for(range<Ix>) <@> lambda(i) -> Float64((Int64(i) * 3) % 13) |> compute\n"
    + "let keep = mask(x, lambda(v) -> v > 5.5)\n"
    + "let w = c.write(\"compaction_barrier_out.csv\", x)\n"
    + "let y = compound(x, keep)\n"
    + "let s = reduce(y, (+))\n"
// Two-module programs (finding 1: an imported lookup hidden in a random key).
let private compactionV2ImportedKeySources (exported: string) (key: string) =
    [ ("Data", "module Data\n\nlet x = [12345, -3, 4]\nlet keep = mask(x, lambda(v) -> v > 0)\nlet y = compound(x, keep)\n" + exported)
      ("Main", "module Main\nimport Data\nimport rand as r\n\nlet u = r.uniform(" + key + ", 1)\n") ]
let private compactionV2ImportedHelperSources =
    [ ("Helpers", "module Helpers\n\ntype Ix = Idx<2>\nfunction bump(a: mut Array<Float64 like Ix>) -> Int64 = {\n    a(0) = 9.0\n    7\n}\n")
      ("Main", "module Main\nimport Helpers\nimport rand as r\n\nlet mut x: Array<Float64 like Helpers.Ix> = [1.0, 2.0]\nlet keep = mask(x, lambda(v) -> v > 0.0)\nlet noise = r.uniform(Helpers.bump(x), 1)\nlet y = compound(x, keep)\nlet s = reduce(y, (+))\n") ]
let private v2Applied (d: Blade.Effects.Decision) = d.Version = 2 && applied d && d.Subject = "y"
let private evidenceHas (needle: string) (d: Blade.Effects.Decision) = d.Evidence |> List.exists (fun e -> e.Contains needle)

let private decisionsOfMulti (sources: (string * string) list) : Result<Blade.Effects.Decision list, string> =
    Blade.Effects.Decisions.start ()
    let r =
        match fst (Blade.Lowering.lowerMultiSourceCaptured sources) with
        | Error e -> Error ($"lower: {e}")
        | Ok _ -> Ok (Blade.Effects.Decisions.drain ())
    Blade.Effects.Decisions.drain () |> ignore
    r
let private decisionCaseMulti (name: string) (sources: (string * string) list) (want: Blade.Effects.Decision -> bool) (describe: string) =
    match decisionsOfMulti sources with
    | Error e -> resultLine Fail name e; false
    | Ok ds ->
        let mine = ds |> List.filter (fun d -> d.Rule = "compaction-fusion")
        if mine |> List.exists want then
            resultLine Pass name ($"{mine.Length} `compaction-fusion` decision(s); {describe}")
            true
        else
            let seen = mine |> List.map Blade.Effects.Decisions.render |> String.concat " | "
            let shown = if seen = "" then "no decisions" else seen
            resultLine Fail name ($"wanted {describe}; saw: {shown}")
            false

let private compactionV2DecisionCases () =
    [ // ---- the default (T4b) ----
      decisionCase "decision_compaction_v2_positions_elided" compactionSrc "compaction-fusion"
          (fun d -> v2Applied d && evidenceHas "selection: positions elided (census: " d) "v2 applied: positions elided by the census"
      decisionCase "decision_compaction_v2_default_gather_opt_in" compactionSrc "compaction-fusion"
          (fun d -> v2Applied d && evidenceHas "gather: declined -- opt-in this round (BLADE_COMPACTION_FUSION=gather)" d
                    && not (evidenceHas "selectionAdmits" d))
          "the default keeps the gather at the compound (T4a opt-in)"
      decisionCase "decision_compaction_v2_keyed_read_requires" compactionV2KeyedSrc "compaction-fusion"
          (fun d -> v2Applied d && evidenceHas "selection: positions required by a keyed read of `y` in `k5`" d) "positions required by a keyed read"
      decisionCase "decision_compaction_v2_argument_requires"
          (compactionV2DemandSrc "function at4(c: Array<Float64 like CompoundIdx<keep>>) -> Float64 = c(4)\nlet a4 = at4(y)\n") "compaction-fusion"
          (fun d -> v2Applied d && evidenceHas "positions required by `y` passed as a call argument in `a4`" d) "positions required by a call argument"
      decisionCase "decision_compaction_v2_alias_requires"
          (compactionV2DemandSrc "let w = y\nlet s = reduce(w, (+))\n") "compaction-fusion"
          (fun d -> v2Applied d && evidenceHas "positions required by the alias `w` of `y`" d) "positions required by an alias"
      decisionCase "decision_compaction_v2_capture_requires"
          (compactionV2DemandSrc "let c = reduce(method_for(range<Ix>) <@> lambda(i) -> Float64(extents(y)) + Float64(Int64(i)), (+))\n") "compaction-fusion"
          (fun d -> v2Applied d && evidenceHas "positions required by `y` captured by" d) "positions required by a capture"
      decisionCase "decision_compaction_v2_present_requires"
          (compactionV2DemandSrc "let d: Array<Float64 like Ix> = [0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0]\nlet o = y <|:> d |> compute\n") "compaction-fusion"
          (fun d -> v2Applied d && evidenceHas "positions required by `y` as an operand of IRFallback" d) "positions required by a present read"
      decisionCase "decision_compaction_v2_derived_map_keyed_requires"
          (compactionV2DemandSrc "let z = y * 2.0 |> compute\nlet z7 = z(7)\n") "compaction-fusion"
          (fun d -> v2Applied d && evidenceHas "positions required by a keyed read of `z` in `z7`" d) "positions required through a derived map"
      decisionCase "decision_compaction_v2_derived_map_fold_elides"
          (compactionV2DemandSrc "let z = y * 2.0 |> compute\nlet zs = reduce(z, (+))\n") "compaction-fusion"
          (fun d -> v2Applied d && evidenceHas "selection: positions elided (census: the named map `z`" d) "positions elided through a derived map's fold"
      // review finding 6: a whole-array kernel (input rank 1) is no cellwise map.
      // Since W4-2 (gap G64) the zero-level apply LOWERS to its call
      // (Lowering.isZeroLevelWholeArrayApply), so the census reads `y` as a
      // call argument -- positions still REQUIRED (the kernel's keyed `c(2)`).
      decisionCase "decision_compaction_v2_whole_array_kernel_requires"
          (compactionV2DemandSrc "function at0(c: Array<Float64 like CompoundIdx<keep>>) -> Float64 = c(2)\nlet w = object_for(at0) <@> y |> compute\n") "compaction-fusion"
          (fun d -> v2Applied d && evidenceHas "positions required by `y` passed as a call argument in `w`" d)
          "positions required by a whole-array kernel (lowered to its call, G64)"
      // a cardinality inside an expression's TYPE (a range over `extents(y)`) is certified
      decisionCase "decision_compaction_v2_extent_in_an_embedded_type_elides"
          (compactionV2DemandSrc "let r = 0..extents(y) |> compute\nlet rs = reduce(r, (+))\n") "compaction-fusion"
          (fun d -> v2Applied d && evidenceHas "selection: positions elided" d) "an embedded type's `extents(y)` keeps the table elided"
      // T4c (the default): two compounds over one mask share ONE selection
      decisionCase "decision_compaction_v2_shared_mask_one_selection" compactionV2SharedMaskSrc "compaction-fusion"
          (fun d -> d.Version = 2 && applied d && d.Subject = "y" && evidenceHas "one selection index shared by 2 companions (`y`, `y2`), freed once after `y2`" d)
          "two compounds over one mask share one selection index (T4c)"
      // the pre-T4c v2 (`unshared`): the first compound keeps its separate passes
      withGate "BLADE_COMPACTION_FUSION" "unshared" (fun () ->
          decisionCase "decision_compaction_v2_unshared_shared_mask_first_declined" compactionV2SharedMaskSrc "compaction-fusion"
              (fun d -> d.Version = 2 && d.Subject = "y" && declinedMentioning "its in-pass count serves one, the last (`y2`)" d
                        && declinedMentioning "disabled by BLADE_COMPACTION_FUSION=unshared" d)
              "unshared: the first compound over a shared mask says it keeps the separate passes")
      // finding 2 in the default: the hidden writer only writes x, so B4's count motion stays legal
      decisionCase "decision_compaction_v2_hidden_writer_keeps_b4" compactionV2RandomKeyWriterSrc "compaction-fusion"
          (fun d -> v2Applied d) "a hidden writer of x between keeps B4 (its count motion cannot be changed by x)"
      // finding 1: an imported lookup hidden in a random key (and through an exported derived map)
      decisionCaseMulti "decision_compaction_v2_imported_random_key_requires" (compactionV2ImportedKeySources "" "Data.y(0)")
          (fun d -> d.Version = 2 && applied d && d.Subject = "y" && evidenceHas "positions required by `y` named in another module" d)
          "an imported keyed read inside a random key demands the table"
      decisionCaseMulti "decision_compaction_v2_imported_derived_map_key_requires" (compactionV2ImportedKeySources "let z = y * 2 |> compute\n" "Data.z(2)")
          (fun d -> d.Version = 2 && applied d && d.Subject = "y" && evidenceHas "positions required by `z` named in another module" d)
          "an imported derived map read inside a random key demands the table"
      withGather (fun () ->
          withGate "BLADE_TILE_TUNE" "sel_cap=0" (fun () ->
              decisionCaseMulti "decision_compaction_v2_imported_random_key_requires_gather_sel_cap0" (compactionV2ImportedKeySources "" "Data.y(0)")
                  (fun d -> d.Version = 2 && applied d && d.Subject = "y" && evidenceHas "positions required by `y` named in another module" d)
                  "the same under `gather` with sel_cap=0 (T4b alone)"))
      // ---- `gather` (T4a opt-in) ----
      withGather (fun () ->
          decisionCase "decision_compaction_v2_gather_at_mask" compactionSrc "compaction-fusion"
              (fun d -> v2Applied d && evidenceHas "gather: at the mask site" d) "gather: at the mask site")
      withGather (fun () ->
          decisionCase "decision_compaction_v2_gather_five_party_matrix" compactionSrc "compaction-fusion"
              (fun d -> v2Applied d && evidenceHas "selectionAdmits (five parties, each through Optimize.scheduleMayWriteIn): producer `x`: no write; predicate of `keep`: no write" d
                        && evidenceHas "seed(s): none" d && evidenceHas "between `keep` and `y`: no binding" d)
              "gather: the five-party matrix recorded")
      withGather (fun () ->
          withGate "BLADE_TILE_TUNE" "sel_cap=1" (fun () ->
              decisionCase "decision_compaction_v2_gather_declined_sel_cap" compactionSrc "compaction-fusion"
                  (fun d -> v2Applied d && evidenceHas "gather: declined -- (n + 1) x 8 B = 7.8 KiB exceeds sel_cap 1.0 KiB" d)
                  "gather declined: over the selectivity cap"))
      withGather (fun () ->
          decisionCase "decision_compaction_v2_gather_declined_writing_producer" compactionV2WritingProducerSrc "compaction-fusion"
              (fun d -> v2Applied d && evidenceHas "gather: declined -- the producer `x` may write" d && evidenceHas "DECLINES: the producer `x` may write" d)
              "gather declined: the producer writes (five-party matrix)")
      withGather (fun () ->
          decisionCase "decision_compaction_v2_gather_shared_mask_at_mask" compactionV2SharedMaskSrc "compaction-fusion"
              (fun d -> d.Version = 2 && applied d && d.Subject = "y2" && evidenceHas "one selection index shared by 2 companions" d
                        && evidenceHas "gather: at the mask site -- `keep`'s pass sweeps the sources once" d)
              "gather: the shared selection's one pass is the mask pass (J = 2 <= 4)")
      withGather (fun () ->
          decisionCase "decision_compaction_v2_gather_declined_random_key_writer" compactionV2RandomKeyWriterSrc "compaction-fusion"
              (fun d -> v2Applied d && evidenceHas "gather: declined -- `noise` (between `keep` and `y`) may write (its value or side-table payload)" d)
              "gather declined: a random fill's key writes x (its value is a placeholder)")
      withGather (fun () ->
          decisionCase "decision_compaction_v2_gather_declined_compound_operand_writer" compactionV2CompoundOperandWriterSrc "compaction-fusion"
              (fun d -> v2Applied d && evidenceHas "gather: declined -- `zz` (between `keep` and `y`) may write (its value or side-table payload)" d)
              "gather declined: another compound's operand (a distinct mask) writes x")
      withGather (fun () ->
          decisionCase "decision_compaction_v2_gather_declined_provider_write" compactionV2ProviderWriteSrc "compaction-fusion"
              (fun d -> v2Applied d && evidenceHas "is a provider write (an external effect)" d)
              "gather declined: a provider write between the mask and the compound")
      withGather (fun () ->
          decisionCaseMulti "decision_compaction_v2_gather_declined_imported_helper" compactionV2ImportedHelperSources
              (fun d -> v2Applied d && evidenceHas "gather: declined -- `noise` (between `keep` and `y`) may write" d)
              "gather declined: an imported mutating helper in a random key")
      // ---- the `b4` mirror ----
      withGate "BLADE_COMPACTION_FUSION" "b4" (fun () ->
          decisionCase "decision_compaction_b4_mirror_is_round2" compactionSrc "compaction-fusion"
              (fun d -> d.Version = 1 && applied d && d.Subject = "y" && evidenceHas "positions always built (B4d deferred)" d)
              "the b4 mirror records the round-2 plan (v1, positions always built)")
      withGate "BLADE_COMPACTION_FUSION" "b4" (fun () ->
          decisionCase "decision_compaction_b4_shared_mask_first_declined" compactionV2SharedMaskSrc "compaction-fusion"
              (fun d -> d.Version = 1 && d.Subject = "y" && declinedMentioning "feeds 2 compounds" d)
              "b4: the first compound over a shared mask is recorded declined (its emission was always the separate passes)") ]

// --- Compaction fusion v2, T4c: one selection for every compound over a mask ---
// Three companions over `keep` (x itself, an Int64 column, a Float64 column):
// ONE `compound_index_t<1>` (`keep__sel`) built once, ONE joint pass, each
// companion's OWN data store wrapped around the shared index; no companion
// frees it (the one index-only free is the plan's, registered with the last
// companion -- program lifetime at main's top level). Values: corpus
// index-types/532-543.
let private t4cSrc =
    "type Ix = Idx<1003>\n"
    + "let x = method_for(range<Ix>) <@> lambda(i) -> Float64((Int64(i) * 37) % 11) - 4.0 |> compute\n"
    + "let c2 = method_for(range<Ix>) <@> lambda(i) -> (Int64(i) * 7) % 13 |> compute\n"
    + "let c3 = method_for(range<Ix>) <@> lambda(i) -> Float64(Int64(i) % 5) |> compute\n"
    + "let keep = mask(x, lambda(v) -> v > 1.0)\n"
    + "let y1 = compound(x, keep)\n"
    + "let s1 = reduce(y1 * y1, (+))\n"
    + "let y2 = compound(c2, keep)\n"
    + "let y3 = compound(c3, keep)\n"
    + "let s3 = reduce(y3 * y3, (+)) + Float64(extents(y3))\n"
    + "let s2 = reduce(y2, (+))\n"
// a keyed read of the LAST companion keeps the shared positions (x(1000) = 3.0 is kept)
let private t4cKeyedSrc = t4cSrc + "let k3 = y3(1000)\n"
let private t4cSemiSrc =
    "type Row = Idx<2003>\n"
    + "type Ev = Idx<101>\n"
    + "let elig = method_for(range<Ev>) <@> lambda(e) -> (Int64(e) * 199) % 20011 |> compute\n"
    + "let cust = method_for(range<Row>) <@> lambda(i) -> (Int64(i) * 7919 + 13) % 20011 |> compute\n"
    + "let amt = method_for(range<Row>) <@> lambda(i) -> Float64((Int64(i) * 31) % 1000) |> compute\n"
    + "let keep = mask(cust, lambda(c) -> contains(elig, c))\n"
    + "let y = compound(amt, keep)\n"
    + "let checksum = reduce(y, (+)) + Float64(extents(y))\n"
let private t4cOneBuildChecks (cpp: string) =
    [ "ONE index build", countOf "new compound_index_t<1>(" cpp = 1
      "the shared index", cpp.Contains "compound_index_t<1>* keep__sel = new compound_index_t<1>(\"keep__sel\", keep__sel_grid, keep__count, "
      "y1's own data store", cpp.Contains "nested_array_utilities::Compound<double, 1> y1 { keep__sel_v0, keep__sel, y1_trail };"
      "y2's own data store", cpp.Contains "nested_array_utilities::Compound<int64_t, 1> y2 { keep__sel_v1, keep__sel, y2_trail };"
      "y3's own data store", cpp.Contains "nested_array_utilities::Compound<double, 1> y3 { keep__sel_v2, keep__sel, y3_trail };"
      "three DISTINCT companion value allocations, each allocated once", countOf "keep__sel_v0 = new " cpp + countOf "keep__sv0 = new " cpp >= 1 && countOf "keep__sel_v1 = new " cpp + countOf "keep__sv1 = new " cpp >= 1 && countOf "keep__sel_v2 = new " cpp + countOf "keep__sv2 = new " cpp >= 1
      "at most one shared position allocation", countOf "compound_index_t<1>::IDX[" cpp <= (if cpp.Contains "keep__one" then 2 else 1)
      "no per-companion index deletion (program-lifetime / no-free check)",
        not (cpp.Contains "deallocate_compound(y") && not (cpp.Contains "delete y1_idx") && not (cpp.Contains "delete keep__sel")
        && not (cpp.Contains "deallocate_compound_index_only(keep__sel);") && not (cpp.Contains "deallocate_compound_shared_index(y")
      "the lifetime line, once, at the last companion (no executed free at main's top level)",
        countOf "// T4c lifetime: `keep__sel` is owned by the plan, never by a companion" cpp = 1
        && cpp.Contains "with the last companion `y3` -- at main's top level (compound() is top-level only, BL6002) there is no scope, so no registration and no free is executed: program lifetime" ]
let private t4cDefaultEmission () =
    let name = "compaction_t4c_one_index_one_joint_pass"
    match cppOfSource name t4cSrc with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        checkAll name cpp "one shared index, one joint pass at y1 over the three sources, positions elided, each companion its own buffer"
            (t4cOneBuildChecks cpp
             @ [ "count in the mask pass", cpp.Contains "keep__count += (size_t)__keep_k;"
                 "ONE joint pass", countOf "keep__sel_q += (size_t)keep__sel_pool[keep__sel_i];" cpp = 1
                 "the three gathers in it", cpp.Contains "keep__sel_v0[keep__sel_q] = keep__sel_d0[keep__sel_i];" && cpp.Contains "keep__sel_v1[keep__sel_q] = keep__sel_d1[keep__sel_i];" && cpp.Contains "keep__sel_v2[keep__sel_q] = keep__sel_d2[keep__sel_i];"
                 "m + 1 cells per companion buffer", cpp.Contains "double* keep__sel_v0 = new double[keep__count + 1];" && cpp.Contains "int64_t* keep__sel_v1 = new int64_t[keep__count + 1];"
                 "positions elided", cpp.Contains "keep__count, compound_index_t<1>::positions_elided);" && not (cpp.Contains "keep__sel_pos")
                 "no per-compound pass", not (cpp.Contains "y2_q +=") && not (cpp.Contains "y3_q +=") && not (cpp.Contains "rank_to_tuple[y2_q]")
                 "K-lane self-dot over y3.data", cpp.Contains "blade_fold::sq<double, 8>(y3.data, y3.idx->cardinality)" ])
let private t4cKeyedEmission () =
    let name = "compaction_t4c_keyed_read_keeps_shared_positions"
    match cppOfSource name t4cKeyedSrc with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        checkAll name cpp "a keyed read of y3 keeps the SHARED kept positions, filled once by the joint pass"
            (t4cOneBuildChecks cpp
             @ [ "positions filled once", countOf "keep__sel_pos[keep__sel_q][0] = keep__sel_i;" cpp = 1
                 "m + 1 position cells", cpp.Contains "compound_index_t<1>::IDX* keep__sel_pos = new compound_index_t<1>::IDX[keep__count + 1];"
                 "ONE shared position allocation", countOf "compound_index_t<1>::IDX[" cpp = 1
                 "the index adopts them", cpp.Contains "keep__count, keep__sel_pos);" ])
let private t4cReproEmission () =
    let name = "compaction_t4c_repro_chains_fed_in_joint_pass"
    match withGate "BLADE_FP_REPRO" "1" (fun () -> cppOfSource name t4cSrc) with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        checkAll name cpp "both self-product chains (over y1's and y3's own buffers) fed per block inside the one joint pass"
            (t4cOneBuildChecks cpp
             @ [ "y1's chain peeled from its buffer", cpp.Contains "double __t = keep__sel_v0[0] * keep__sel_v0[0]; BLADE_FOLD_ROUND(__t); keep__sel__fold0 = __t;"
                 "y3's chain peeled from its buffer", cpp.Contains "double __t = keep__sel_v2[0] * keep__sel_v2[0]; BLADE_FOLD_ROUND(__t); keep__sel__fold1 = __t;"
                 "chains only over completed cells", cpp.Contains "for (; keep__sel_f < keep__sel_q; keep__sel_f++)"
                 "products rounded before the add", countOf "BLADE_FOLD_ROUND(__t);" cpp = 4
                 "the folds return the fed chains", cpp.Contains "return keep__sel__fold0;" && cpp.Contains "return keep__sel__fold1;"
                 "no K-lane form", not (cpp.Contains "blade_fold::sq<double") ])
let private t4cGatherEmission () =
    let name = "compaction_t4c_gather_joint_gather_in_mask_pass"
    match withGather (fun () -> cppOfSource name t4cSrc) with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        checkAll name cpp "gather (J = 3 <= 4, every source bound before the mask): the mask pass gathers all three; y1 only completes the index"
            ((t4cOneBuildChecks cpp |> List.filter (fun (k: string, _) -> not (k.Contains "data store")))
             @ [ "the three gathers in the mask pass", cpp.Contains "keep__sv0[keep__count] = x[__mi];" && cpp.Contains "keep__sv1[keep__count] = c2[__mi];" && cpp.Contains "keep__sv2[keep__count] = c3[__mi];"
                 "n + 1 private cells per buffer", cpp.Contains "int64_t* keep__sv1 = new int64_t[(size_t)(1003) + 1];"
                 "no joint pass at y1", not (cpp.Contains "keep__sel_q +=")
                 "each companion's own buffer", cpp.Contains "Compound<double, 1> y1 { keep__sv0, keep__sel, y1_trail };" && cpp.Contains "Compound<int64_t, 1> y2 { keep__sv1, keep__sel, y2_trail };" && cpp.Contains "Compound<double, 1> y3 { keep__sv2, keep__sel, y3_trail };" ])
let private t4cUnsharedEmission () =
    let name = "compaction_t4c_unshared_mirror_one_index_per_compound"
    match withGate "BLADE_COMPACTION_FUSION" "unshared" (fun () -> cppOfSource name t4cSrc) with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        checkAll name cpp "unshared (the pre-T4c v2): no shared index, one index per compound"
            [ "no shared selection", not (cpp.Contains "keep__sel")
              "an index per compound", cpp.Contains "y1_idx" && cpp.Contains "y2_idx" && cpp.Contains "y3_idx"
              "three index builds", countOf "compound_index_t<1>* y" cpp >= 3 ]
let private t4cGateOffEmission () =
    let name = "compaction_t4c_gate_off_one_index_per_compound"
    match withGate "BLADE_COMPACTION_FUSION" "0" (fun () -> cppOfSource name t4cSrc) with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        checkAll name cpp "gate off: no shared index, the counting constructor per compound"
            [ "no shared selection", not (cpp.Contains "keep__sel")
              "the counting constructor per compound", cpp.Contains "new compound_index_t<1>(\"y1_idx\", y1_idx_grid, y1_idx_pool)" && cpp.Contains "new compound_index_t<1>(\"y3_idx\", y3_idx_grid, y3_idx_pool)" ]
/// The LLVM lane (maskagg's reference arm): ONE selection completed at y1 --
/// the joint pass allocates exactly one pool per companion plus ONE shared
/// position pool (all sized by the same m + 1 register), each stored into its
/// own slot; y1, y2 and y3 each bind (load) a DIFFERENT one of the value
/// slots; nothing is freed (program lifetime). `unshared` pays a position pool
/// and a value pool per compound instead. Wiring read from the IR itself.
let private t4cLlvmEmission () =
    let name = "compaction_t4c_llvm_one_selection"
    let llOf () =
        match lower t4cKeyedSrc with
        | Error e -> Error ($"lower: {e}")
        | Ok ir ->
            match EmitLlvm.tryEmitProgramNamed "t4c_llvm" ir with
            | Ok ll -> Ok ll
            | Error r -> Error ($"the llvm lane refused: {r}")
    match llOf (), withGate "BLADE_COMPACTION_FUSION" "unshared" llOf with
    | Ok ll, Ok llOld ->
        let lines = ll.Replace("\r\n", "\n").Split('\n') |> Array.map (fun l -> l.Trim())
        let rx (p: string) = System.Text.RegularExpressions.Regex(p)
        let completed = lines |> Array.findIndex (fun l -> l.Contains "ONE selection (count, positions) for 3 companions, completed at `y1`")
        // the joint pass: the allocations of the m + 1 register just before the completion line
        let allocRe = rx @"^(%[\w.]+) = call ptr @blade_alloc_cells\(i64 (%[\w.]+), i64 8\)"
        let allocs =
            lines.[.. completed] |> Array.indexed |> Array.choose (fun (k, l) ->
                let m = allocRe.Match l in if m.Success then Some (k, m.Groups.[1].Value, m.Groups.[2].Value) else None)
        let jointSize = allocs |> Array.last |> fun (_, _, sz) -> sz
        let joint = allocs |> Array.filter (fun (_, _, sz) -> sz = jointSize)
        let slotOf (reg: string) =
            lines |> Array.tryPick (fun l -> let m = (rx ("^store ptr " + System.Text.RegularExpressions.Regex.Escape reg + @", ptr (%[\w.]+)$")).Match l in if m.Success then Some m.Groups.[1].Value else None)
        let slots = joint |> Array.map (fun (_, r, _) -> slotOf r)
        // the slot each companion binds: the first `load ptr, ptr S` after its line
        let bindAfter (k0: int) =
            lines.[k0 + 1 ..] |> Array.tryPick (fun l -> let m = (rx @"= load ptr, ptr (%[\w.]+)$").Match l in if m.Success then Some m.Groups.[1].Value else None)
        let lineOf (needle: string) = lines |> Array.tryFindIndex (fun l -> l.Contains needle)
        let b1 = bindAfter completed
        let b2 = lineOf "`y2` binds its own pool" |> Option.bind bindAfter
        let b3 = lineOf "`y3` binds its own pool" |> Option.bind bindAfter
        let valueSlots = [ b1; b2; b3 ]
        checkAll name ll "one joint pass: 3 value pools + 1 shared position pool (one m + 1 size), distinct slots; y1 / y2 / y3 each bind a different value slot; no free"
            [ "ONE selection", countOf "ONE selection (count, positions) for 3 companions, completed at `y1`" ll = 1
              "4 joint allocations (3 companions + 1 shared position pool), one size", joint.Length = 4
              "each stored into its own slot", slots |> Array.forall Option.isSome && (slots |> Array.distinct).Length = 4
              "y1, y2, y3 each bind a different joint slot", valueSlots |> List.forall Option.isSome && (valueSlots |> List.distinct).Length = 3
                                                             && valueSlots |> List.forall (fun s -> slots |> Array.contains s)
              "the lifetime line once, at the last companion", countOf "; T4c lifetime: the shared count and positions are owned by the plan, never by a companion (each binds only its own pool; `y3` is the last)" ll = 1
              "no free in main (program lifetime: no companion pool and no shared pool is released)", (match lines |> Array.tryFindIndex (fun l -> l.StartsWith "define i32 @main(") with Some m0 -> not (lines.[m0 ..] |> Array.exists (fun l -> l.Contains "call void @blade_free(")) | None -> false)
              "unshared: one position pool and one value pool per compound (two more pools than the shared selection)", countOf "call ptr @blade_alloc_cells(" ll = countOf "call ptr @blade_alloc_cells(" llOld - 2
              "unshared keeps no shared selection", not (llOld.Contains "shared selection (T4c)") ]
    | Error e, _ | _, Error e -> resultLine Fail name e; false
let private t4cDecisionCases () =
    [ decisionCase "decision_compaction_t4c_applied_three_companions" t4cSrc "compaction-fusion"
          (fun d -> d.Version = 2 && applied d && d.Subject = "y2"
                    && evidenceHas "one selection index shared by 3 companions (`y1`, `y2`, `y3`), freed once after `y3`" d
                    && evidenceHas "lifetime: the plan owns the index" d
                    && evidenceHas "COMPLETE once the first companion `y1` is bound" d)
          "T4c applied: one index for y1, y2, y3, freed once after y3, the completeness invariant recorded"
      decisionCase "decision_compaction_t4c_five_party_matrix" t4cSrc "compaction-fusion"
          (fun d -> d.Version = 2 && applied d && d.Subject = "y1"
                    && evidenceHas "selectionAdmits (five parties, each through Optimize.scheduleMayWriteIn): producers `x`, `c2`, `c3` and the mask source `x`: proved stored through their alias chains (storedArrayProof), never assigned, no write in any payload; predicate of `keep`: no write" d
                    && evidenceHas "outer fold kernel(s): 2 over the companions, none writes" d
                    && evidenceHas "seed(s): 0 seeded fold(s)" d
                    && evidenceHas "between `keep` and `y3`: `s1`: no write in any payload" d
                    && evidenceHas "parallel: none" d)
          "the five-party matrix (producers / predicate / outer kernel / seed / intervening, plus parallel)"
      decisionCase "decision_compaction_t4c_keyed_positions_shared" t4cKeyedSrc "compaction-fusion"
          (fun d -> d.Version = 2 && applied d && d.Subject = "y1" && evidenceHas "selection: positions required by a keyed read of `y3` in `k3` (`y3`) -- the SHARED index" d)
          "one companion's keyed read keeps the shared positions"
      decisionCase "decision_compaction_t4c_declined_keep_read_between" (t4cSrc.Replace("let y2 = compound(c2, keep)\n", "let k0 = keep(2)\nlet y2 = compound(c2, keep)\n")) "compaction-fusion"
          (fun d -> d.Subject = "y2" && declinedMentioning "the shared selection (T4c) declined: `k0` (between `keep` and `y3`) reads `keep`" d)
          "T4c declined: `keep` read between the mask and the last companion"
      decisionCase "decision_compaction_t4c_declined_writer_between"
          ("type Ix = Idx<4>\nfunction bump(a: mut Array<Int64 like Ix>) -> Int64 = {\n    a(0) = 9\n    7\n}\n"
           + "let x: Array<Float64 like Ix> = [1.0, 2.0, 3.0, 4.0]\nlet mut c2: Array<Int64 like Ix> = [1, 2, 3, 4]\n"
           + "let keep = mask(x, lambda(v) -> v > 1.5)\nlet y1 = compound(x, keep)\nlet w = bump(c2)\nlet y2 = compound(c2, keep)\n") "compaction-fusion"
          (fun d -> d.Subject = "y2" && declinedMentioning "`w` (between `keep` and `y2`) may write" d)
          "T4c declined: a writer between the companions (the shared write analysis)"
      decisionCase "decision_compaction_t4c_declined_late_source" (t4cSrc.Replace("let y3 = compound(c3, keep)\n", "let c4 = c3 + 1.0 |> compute\nlet y3 = compound(c4, keep)\n")) "compaction-fusion"
          (fun d -> d.Subject = "y2" && declinedMentioning "the source `c4` of `y3` is bound after the first companion `y1`" d)
          "T4c declined: a source bound after the first companion (v1)"
      decisionCase "decision_compaction_t4c_declined_assigned_companion"
          ("type Ix = Idx<4>\nlet x: Array<Float64 like Ix> = [1.0, 2.0, 3.0, 4.0]\nlet c2: Array<Float64 like Ix> = [5.0, 6.0, 7.0, 8.0]\n"
           + "let keep = mask(x, lambda(v) -> v > 1.5)\nlet mut y1 = compound(x, keep)\nlet y2 = compound(c2, keep)\ny1(3) = 0.5\n") "compaction-fusion"
          (fun d -> d.Subject = "y2" && declinedMentioning "the companion `y1` is written (assigned) in the program" d)
          "T4c declined: a companion written"
      // ---- Part B: the membership-probe certificate ----
      decisionCase "decision_compaction_t4c_probe_certified" t4cSemiSrc "compaction-fusion"
          (fun d -> d.Version = 2 && applied d && d.Subject = "y"
                    && evidenceHas "membership probe certified pure/total by member-hoist's admission: `keep`'s predicate probes `elig`" d
                    && evidenceHas "membershipAdmits (five parties" d
                    && evidenceHas "`totalScalar` itself is not widened" d)
          "Part B: the semijoin predicate certified by T1's own admission record"
      withGate "BLADE_MEMBER_HOIST" "0" (fun () ->
          decisionCase "decision_compaction_t4c_probe_not_certified_t1_off" t4cSemiSrc "compaction-fusion"
              (fun d -> d.Subject = "y" && declinedMentioning "the membership probe is not certified: member-hoist did not admit `keep`'s probe of `elig`" d)
              "Part B: T1 declined (BLADE_MEMBER_HOIST=0) -> per-probe scan, T4c declined")
      decisionCase "decision_compaction_t4c_probe_target_not_total" (t4cSemiSrc.Replace("contains(elig, c)", "contains(elig, c % 7)")) "compaction-fusion"
          (fun d -> d.Subject = "y" && declinedMentioning "the target of `keep`'s probe of `elig` is not a pure, total scalar of the cell" d)
          "Part B: a target with a panic site (integer %) is not certified"
      withGate "BLADE_COMPACTION_FUSION" "b4" (fun () ->
          decisionCase "decision_compaction_t4c_b4_mirror_never_certifies" (t4cSemiSrc.Replace("compound(amt, keep)", "compound(cust, keep)").Replace("reduce(y, (+)) + Float64", "Float64(reduce(y, (+))) + Float64")) "compaction-fusion"
              (fun d -> d.Version = 1 && d.Subject = "y" && declinedMentioning "is not a pure, total scalar function of the cell" d && not (declinedMentioning "membership probe" d))
              "b4 stays the round-2 form: no certificate")
      withGate "BLADE_COMPACTION_FUSION" "unshared" (fun () ->
          decisionCase "decision_compaction_t4c_unshared_mirror" t4cSrc "compaction-fusion"
              (fun d -> d.Subject = "y2" && declinedMentioning "disabled by BLADE_COMPACTION_FUSION=unshared" d)
              "unshared: no shared selection") ]

// --- T4c fix round (Codex diff review of 278353cb) ---------------------------
// Finding 1 (ruled: the singleton expansion is Part B's intent): (a) every
// LEGACY singleton shape -- index-types 515-531 but the two-compound 526: a
// single `compound(x, keep)` over the MASK SOURCE with a total predicate --
// emits the SAME text by default as under `unshared` (the pre-T4c v2, which
// takes the tip's planner path for them), in both lanes; (b) the two NEWLY
// admitted singleton shapes route through the new plan (the twins' values
// and first failures: the cli block). Finding 2: the positive storage proof
// of the mask source and every companion source (alias chains, deferred
// roots, streamed reads). Finding 3: run-time-extent wiring.
let private t4cLegacySingletonIdentity () : bool list =
    let files =
        Blade.Tests.Corpus.category "index-types"
        |> List.filter (fun (n, _) -> n.StartsWith "Compaction fusion" && not (n.Contains "T4c") && not (n.Contains "two compounds over one mask"))
    let llOf (n: string) (src: string) =
        match lower src with
        | Error e -> "lower: " + e
        | Ok ir ->
            match (try EmitLlvm.tryEmitProgramNamed n ir with ex -> Error ("raised: " + ex.Message)) with
            | Ok ll -> ll
            | Error r -> "refused: " + r
    [ let census = "compaction_t4c_legacy_singletons: the 16 shapes of index-types 515-531 (all but 526)"
      if files.Length = 16 then resultLine Pass census "16 files"; yield true
      else resultLine Fail census $"found {files.Length}"; yield false
      for (n, src) in files do
        let name = $"compaction_t4c_legacy_singleton_identity: `{n}`"
        let cDef = cppOfSource n src
        let cOld = withGate "BLADE_COMPACTION_FUSION" "unshared" (fun () -> cppOfSource n src)
        let lDef = llOf n src
        let lOld = withGate "BLADE_COMPACTION_FUSION" "unshared" (fun () -> llOf n src)
        let sel = "__sel"
        match cDef, cOld with
        | Ok a, Ok b when a = b && lDef = lOld && not (a.Contains sel) ->
            resultLine Pass name "C++ and LLVM text identical to the pre-T4c v2 (unshared); no shared selection"
            yield true
        | Ok a, Ok b ->
            resultLine Fail name $"C++ identical: {a = b}; LLVM identical: {lDef = lOld}; shared selection present: {a.Contains sel}"
            yield false
        | Error e, _ | _, Error e -> resultLine Fail name e; yield false ]

/// (b) the newly admitted singletons: a single compound over ANOTHER stored
/// column with a total predicate (the shared-selection path, J = 1), and a
/// single compound whose predicate is a certified probe -- over the mask
/// source (the B4/T4 path, now admitted by the certificate) and over another
/// column (semiagg: the shared-selection path).
let private t4cSingleOtherColumnSrc =
    "type Ix = Idx<1003>\n"
    + "let x = method_for(range<Ix>) <@> lambda(i) -> Float64((Int64(i) * 37) % 11) - 4.0 |> compute\n"
    + "let c = method_for(range<Ix>) <@> lambda(i) -> Float64(Int64(i) % 7) |> compute\n"
    + "let keep = mask(x, lambda(v) -> v > 1.0)\n"
    + "let y = compound(c, keep)\n"
    + "let s = reduce(y * y, (+)) + Float64(extents(y))\n"
let private t4cSingleProbeOverSourceSrc =
    t4cSemiSrc.Replace("compound(amt, keep)", "compound(cust, keep)").Replace("let checksum = reduce(y, (+)) + Float64(extents(y))", "let checksum = Float64(reduce(y, (+))) + Float64(extents(y))")
let private t4cNewSingletonRoutes () : bool list =
    let route (name: string) (src: string) (summary: string) (onChecks: string -> (string * bool) list) (offChecks: string -> (string * bool) list) =
        match cppOfSource name src, withGate "BLADE_COMPACTION_FUSION" "unshared" (fun () -> cppOfSource name src), withGate "BLADE_COMPACTION_FUSION" "0" (fun () -> cppOfSource name src) with
        | Ok on, Ok uns, Ok off -> checkAll name on summary (onChecks on @ (offChecks uns |> List.map (fun (k, v) -> ("unshared: " + k, v))) @ (offChecks off |> List.map (fun (k, v) -> ("gate off: " + k, v))))
        | Error e, _, _ | _, Error e, _ | _, _, Error e -> resultLine Fail name e; false
    [ route "compaction_t4c_new_singleton_other_column" t4cSingleOtherColumnSrc
          "a single compound over another stored column: the shared-selection path (one index, the joint pass at y); unshared / gate off: its own passes"
          (fun cpp -> [ "the shared index", cpp.Contains "compound_index_t<1>* keep__sel = new compound_index_t<1>(\"keep__sel\", keep__sel_grid, keep__count, compound_index_t<1>::positions_elided)"
                        "the one joint pass reads c", cpp.Contains "keep__sel_d0 = nested_array_utilities::pool_base(c.data);" && countOf "keep__sel_q += (size_t)keep__sel_pool[keep__sel_i];" cpp = 1
                        "y wraps its own buffer", cpp.Contains "nested_array_utilities::Compound<double, 1> y { keep__sel_v0, keep__sel, y_trail };" ])
          (fun cpp -> [ "no shared selection", not (cpp.Contains "keep__sel") ])
      route "compaction_t4c_new_singleton_probe_over_source" t4cSingleProbeOverSourceSrc
          "a single compound over the mask source with a certified probe: the B4/T4 plan (count in the mask pass, one fill+gather pass); unshared / gate off: the separate passes"
          (fun cpp -> [ "count in the mask pass", cpp.Contains "keep__count += (size_t)__keep_k;"
                        "one fill+gather pass at y", countOf "y_q += (size_t)y_idx_pool[y_i];" cpp = 1
                        "the hoisted probe is kept", cpp.Contains "blade_member_build_i64(&" ])
          (fun cpp -> [ "no counted mask", not (cpp.Contains "keep__count") ])
      route "compaction_t4c_new_singleton_probe_other_column" t4cSemiSrc
          "semiagg (a single compound over another column, certified probe): the shared-selection path; unshared / gate off: its own passes"
          (fun cpp -> [ "the shared index", cpp.Contains "compound_index_t<1>* keep__sel = new compound_index_t<1>(\"keep__sel\""
                        "the joint pass reads amt", cpp.Contains "keep__sel_d0 = nested_array_utilities::pool_base(amt.data);"
                        "the hoisted probe is kept", cpp.Contains "blade_member_build_i64(&" ])
          (fun cpp -> [ "no shared selection", not (cpp.Contains "keep__sel"); "the hoisted probe is kept (T1 is independent of the mirror)", cpp.Contains "blade_member_build_i64(&" ]) ]

// Finding 2 probes. The reviewer's: a companion source that is an ALIAS of a
// DEFERRED map (genVarAliasBinding names it without a pool behind it).
let private t4cAliasDeferredSrc =
    "type I = Idx<3>\n"
    + "let x: Array<Float64 like I> = [1.0, 2.0, 3.0]\n"
    + "let lazy_col = method_for(x) <@> lambda(v) -> v + 10.0\n"
    + "let alias_col = lazy_col\n"
    + "let keep = mask(x, lambda(v) -> v > 0.0)\n"
    + "let y1 = compound(x, keep)\n"
    + "let y2 = compound(alias_col, keep)\n"
    + "let n = extents(y2)\n"
let private t4cAliasStoredSrc =
    t4cAliasDeferredSrc.Replace("let lazy_col = method_for(x) <@> lambda(v) -> v + 10.0\n", "let lazy_col = method_for(x) <@> lambda(v) -> v + 10.0 |> compute\n")
// the mask source is no companion's source: deferred (not proved stored) /
// stored (audited as a producer, read by no joint pass)
let private t4cMaskSourceStoredSrc =
    "type Ix = Idx<13>\n"
    + "let x = method_for(range<Ix>) <@> lambda(i) -> Float64((Int64(i) * 3) % 13) |> compute\n"
    + "let c2 = method_for(range<Ix>) <@> lambda(i) -> Int64(i) * 10 |> compute\n"
    + "let c3 = method_for(range<Ix>) <@> lambda(i) -> Float64(Int64(i)) * 0.5 |> compute\n"
    + "let keep = mask(x, lambda(v) -> v > 5.5)\n"
    + "let y2 = compound(c2, keep)\n"
    + "let y3 = compound(c3, keep)\n"
    + "let s = Float64(reduce(y2, (+))) + reduce(y3, (+))\n"
let private t4cMaskSourceDeferredSrc =
    t4cMaskSourceStoredSrc.Replace("let x = method_for(range<Ix>) <@> lambda(i) -> Float64((Int64(i) * 3) % 13) |> compute\n", "let x = method_for(range<Ix>) <@> lambda(i) -> Float64((Int64(i) * 3) % 13)\n")
let private t4cStorageProofCases () : bool list =
    [ decisionCase "decision_compaction_t4c_declined_alias_of_deferred" t4cAliasDeferredSrc "compaction-fusion"
          (fun d -> d.Subject = "y2" && declinedMentioning "the source `alias_col` of `y2` is not proved stored: `alias_col` is an alias of `lazy_col`, and `lazy_col` is a deferred computation" d)
          "finding 2: an alias of a deferred map is not proved stored (T4c declined)"
      (let name = "compaction_t4c_alias_of_deferred_takes_own_passes"
       match cppOfSource name t4cAliasDeferredSrc with
       | Error e -> resultLine Fail name e; false
       | Ok cpp ->
           let decl = cpp.IndexOf "Array<double, 1> alias_col = "
           let rd = cpp.IndexOf "y2_densepool = nested_array_utilities::pool_base(alias_col.data);"
           checkAll name cpp "no shared selection: y2 reads its operand in its own pass"
               [ "no shared selection", not (cpp.Contains "keep__sel")
                 "y2 reads `alias_col` in its OWN pass, after the alias is materialized at its own site", decl >= 0 && rd > decl ])
      decisionCase "decision_compaction_t4c_alias_of_stored_admitted" t4cAliasStoredSrc "compaction-fusion"
          (fun d -> d.Subject = "y2" && applied d && evidenceHas "one selection index shared by 2 companions (`y1`, `y2`)" d)
          "an alias of a STORED array is proved stored through its chain (T4c applied)"
      decisionCase "decision_compaction_t4c_declined_mask_source_deferred" t4cMaskSourceDeferredSrc "compaction-fusion"
          (fun d -> d.Subject = "y2" && declinedMentioning "the mask source `x` is not proved stored: `x` is a deferred computation" d)
          "the mask source is audited even when it is no companion's source"
      decisionCase "decision_compaction_t4c_mask_source_audited_not_read" t4cMaskSourceStoredSrc "compaction-fusion"
          (fun d -> d.Subject = "y2" && applied d && evidenceHas "producers `c2`, `c3` and the mask source `x`: proved stored through their alias chains" d)
          "x stored and no companion's source: admitted, x recorded among the producers"
      (let name = "compaction_t4c_mask_source_not_read_by_the_joint_pass"
       match cppOfSource name t4cMaskSourceStoredSrc with
       | Error e -> resultLine Fail name e; false
       | Ok cpp ->
           checkAll name cpp "the joint pass reads c2 and c3 only"
               [ "the joint pass reads c2", cpp.Contains "keep__sel_d0 = nested_array_utilities::pool_base(c2.data);"
                 "and c3", cpp.Contains "keep__sel_d1 = nested_array_utilities::pool_base(c3.data);"
                 "never x", not (cpp.Contains "pool_base(x.data)") ])
      // a STREAMED companion source (hand-built: a NetCDF store is needed to
      // write one): never proved stored
      (let name = "decision_compaction_t4c_declined_streamed_source"
       match lower t4cMaskSourceStoredSrc with
       | Error e -> resultLine Fail name e; false
       | Ok ir ->
           let m = ir.Modules |> List.find (fun m -> not m.CompoundInits.IsEmpty)
           let c2 = m.Bindings |> List.find (fun b -> b.Name = "c2")
           let at = match Blade.IR.stripUnits c2.Type with Blade.IR.ArrayElem a -> a | _ -> failwith "c2 is no array"
           let spec : Blade.IR.ProviderReadSpec =
               { Provider = "netcdf"; FilePath = "stream.nc"; VarName = "c2"; VarType = at; MaskName = None; MaskType = None
                 Window = None; Streamed = true; AllowMissing = false }
           let md = { m with ProviderReads = Map.add c2.Id spec m.ProviderReads }
           Blade.Effects.Decisions.start ()
           Blade.Optimize.planCompactionFusion [] Set.empty md
           let ds = Blade.Effects.Decisions.drain ()
           Blade.Optimize.CompactionTable.reset ()
           if ds |> List.exists (fun d -> d.Rule = "compaction-fusion" && d.Subject = "y2" && declinedMentioning "the source `c2` of `y2` is not proved stored: `c2` is a streamed provider read" d) then
               resultLine Pass name "a streamed companion source is not proved stored (T4c declined)"; true
           else
               resultLine Fail name (ds |> List.filter (fun d -> d.Rule = "compaction-fusion") |> List.map Blade.Effects.Decisions.render |> String.concat " | "); false) ]

// Finding 3: a run-time source extent the shared selection positively takes
// (corpus index-types/542): the joint pass over the run-time grid, and under
// `gather` the run-time cap test at the mask with the first companion's
// guarded joint pass into the same buffers.
let private t4cRuntimeSrc =
    "let k = reduce([3, 4, 5], (+))\n"
    + "let ks = 0..k |> compute\n"
    + "let xs = method_for(ks) <@> lambda(i) -> i * 1 |> compute\n"
    + "let ws = method_for(ks) <@> lambda(i) -> i * 3 + 1 |> compute\n"
    + "let keep = mask(xs, lambda(v) -> v > 4)\n"
    + "let ys = compound(xs, keep)\n"
    + "let zs = compound(ws, keep)\n"
    + "let zs_k9 = zs(9)\n"
    + "let total = reduce(ys, (+)) + reduce(zs, (+))\n"
let private t4cRuntimeRoutes () : bool list =
    [ decisionCase "decision_compaction_t4c_runtime_extent_admitted" t4cRuntimeSrc "compaction-fusion"
          (fun d -> d.Subject = "zs" && applied d && evidenceHas "one selection index shared by 2 companions (`ys`, `zs`)" d)
          "a run-time source extent: the shared selection is admitted"
      (let name = "compaction_t4c_runtime_extent_joint_pass"
       match cppOfSource name t4cRuntimeSrc with
       | Error e -> resultLine Fail name e; false
       | Ok cpp ->
           checkAll name cpp "one index over the run-time grid; one joint pass; ys and zs each wrap their own buffer; one shared position allocation"
               [ "the shared index", countOf "new compound_index_t<1>(" cpp = 1 && cpp.Contains "compound_index_t<1>* keep__sel = new compound_index_t<1>(\"keep__sel\", keep__sel_grid, keep__count, keep__sel_pos);"
                 "the run-time grid", cpp.Contains "size_t keep__sel_grid = keep.extents[0];"
                 "one joint pass", countOf "keep__sel_q += (size_t)keep__sel_pool[keep__sel_i];" cpp = 1
                 "ys / zs own buffers", cpp.Contains "Compound<int64_t, 1> ys { keep__sel_v0, keep__sel, ys_trail };" && cpp.Contains "Compound<int64_t, 1> zs { keep__sel_v1, keep__sel, zs_trail };"
                 "one shared position allocation", countOf "compound_index_t<1>::IDX[" cpp = 1 ])
      (let name = "compaction_t4c_runtime_extent_gather_guard_both_edges"
       match withGather (fun () -> cppOfSource name t4cRuntimeSrc) with
       | Error e -> resultLine Fail name e; false
       | Ok cpp ->
           let maskPass = cpp.IndexOf "if (keep__one) {"
           let fallback = cpp.IndexOf "if (!keep__one) {"
           let joint = cpp.IndexOf "keep__sel_q += (size_t)keep__sel_pool[keep__sel_i];"
           checkAll name cpp "gather: the run-time cap test over BOTH companion buffers at the mask (true edge: the mask pass gathers both), the first companion's guarded joint pass (false edge) into the same buffers"
               [ "the run-time cap comparison", cpp.Contains "const bool keep__one = (size_t)("
                 "true edge: the mask pass gathers both companions", maskPass >= 0 && cpp.Contains "keep__sv0[keep__count] = xs[__mi];" && cpp.Contains "keep__sv1[keep__count] = ws[__mi];"
                 "false edge: the first companion's guarded joint pass, after the mask pass", fallback > maskPass && joint > fallback
                 "into the same buffers", cpp.Contains "keep__sv0[keep__sel_q] = keep__sel_d0[keep__sel_i];" && cpp.Contains "keep__sv1[keep__sel_q] = keep__sel_d1[keep__sel_i];"
                 "the shared index adopts the shared positions", cpp.Contains "keep__count, keep__sp);" ]) ]

/// Review finding 8: a `y` reference whose ONLY occurrence is an `ungroup`'s
/// SOURCE index record (which `childrenOf` never hands out) demands the
/// table -- in y's own module (the census) and from another module (the
/// foreign-demand collection). Hand-built IR over a lowered program: a probe
/// binding `IRUngroup (0, src)` whose source extent is the keyed read `y(0)`.
let private compactionV2UngroupIndexExtentCensus () =
    let name = "compaction_v2_census_sees_ungroup_source_index_extents"
    match lower compactionSrc with
    | Error e -> resultLine Fail name e; false
    | Ok ir ->
        let m = ir.Modules |> List.find (fun m -> not m.CompoundInits.IsEmpty)
        let yb = m.Bindings |> List.find (fun b -> b.Name = "y")
        let lookup = Blade.IR.IRIndex (Blade.IR.IRVar (yb.Id, yb.Type), [ Blade.IR.IRLit (Blade.IR.IRLitInt 0L) ], None)
        let src : Blade.IR.IRIndexType =
            { Id = 990001; Rank = 1; Extent = lookup; Symmetry = Blade.Types.SymNone; Tag = None
              IxKind = Blade.Types.IxKPlain; Kind = Blade.Types.SDimension; Dependencies = [] }
        let probe = { yb with Id = 990002; Name = "probe"; Type = Blade.Types.IRTUnit; Value = Blade.IR.IRUngroup (Blade.IR.IRLit (Blade.IR.IRLitInt 0L), src) }
        let requiredIn (md: Blade.IR.IRModule) (foreign: Set<int>) =
            Blade.Effects.Decisions.start ()
            Blade.Optimize.planCompactionFusion [] foreign md
            let ds = Blade.Effects.Decisions.drain ()
            Blade.Optimize.CompactionTable.reset ()
            ds |> List.exists (fun d -> d.Rule = "compaction-fusion" && d.Subject = "y" && evidenceHas "selection: positions required by" d)
        let elidedWithout = not (requiredIn m Set.empty)
        let localOk = requiredIn { m with Bindings = m.Bindings @ [ probe ] } Set.empty
        let other =
            { m with Name = "Other"; Bindings = [ probe ]; Functions = []; CompoundInits = Map.empty
                     SparseInits = Map.empty; RandomInits = Map.empty; ProviderWrites = Map.empty; ProviderReads = Map.empty }
        let foreignRefs = Blade.Optimize.moduleReferencedIds other
        let foreignOk = foreignRefs.Contains yb.Id && requiredIn m foreignRefs
        checkAll name "" "an ungroup source extent's `y(0)` demands the table, locally and from another module"
            [ "without the probe the table is elided", elidedWithout
              "local: the census sees the ungroup source extent", localOk
              "foreign: moduleReferencedIds collects it and the census honours it", foreignOk ]
// Review finding 7: the other scheduling intervals (hot-row, the B1 cell
// epilogue, compact row classes) audit each intervening MODULE binding by its
// whole payload too -- a writer hidden in a random fill's key or in another
// compound's operand declines the schedule, naming the binding. Values: corpus
// loops/537-539, symmetry/137; knob-off twins: the cli block.
let private intervalB1RandomKeySrc =
    "import rand as r\n"
    + "type I = Idx<2>\n"
    + "function bump(a: mut Array<Float64 like I>) -> Int64 = {\n    a(0) = 9.0\n    7\n}\n"
    + "let x: Array<Float64 like I> = [1.0, 2.0]\n"
    + "let mut p = method_for(x) <@> lambda(v) -> v + 1.0 |> compute\n"
    + "let noise = r.uniform(bump(p), 1)\n"
    + "let s = reduce(method_for(p) <@> lambda(v) -> v * v, (+), 0.0)\n"
let private intervalB1CompoundSrc =
    "type I = Idx<2>\n"
    + "function bumped(a: mut Array<Float64 like I>) -> Array<Float64 like I> = {\n    a(0) = 9.0\n    let b = a + 0.0 |> compute\n    b\n}\n"
    + "let x: Array<Float64 like I> = [1.0, 2.0]\n"
    + "let other = mask(x, lambda(v) -> v > 0.5)\n"
    + "let mut p = method_for(x) <@> lambda(v) -> v + 1.0 |> compute\n"
    + "let zz = compound(bumped(p), other)\n"
    + "let s = reduce(method_for(p) <@> lambda(v) -> v * v, (+), 0.0)\n"
let private intervalHotRowRandomKeySrc =
    "import rand as r\n"
    + "type I = Idx<2>\ntype J = Idx<2>\n"
    + "function bump(a: mut Array<Float64 like I, J>) -> Int64 = {\n    a(0, 0) = 9.0\n    7\n}\n"
    + "let mut p = method_for(range<I, J>) <@> lambda(i, j) -> 1.0 |> compute\n"
    + "let noise = r.uniform(bump(p), 1)\n"
    + "let s = reduce(method_for(p) <@> lambda(row) -> prodsum(row, row), (+), 0.0)\n"
let private intervalCompactRowRandomKeySrc =
    "import rand as r\n"
    + "type I = Idx<2>\n"
    + "function bump(a: mut Array<Float64 like SymIdx<2, I>>) -> Int64 = {\n    a(0, 1) = 9.0\n    7\n}\n"
    + "let x: Array<Float64 like I> = [1.0, 2.0]\n"
    + "let mut p = method_for(x, x) <@> lambda(a, b) where comm(a, b) -> a + b |> compute\n"
    + "let noise = r.uniform(bump(p), 1)\n"
    + "let s = reduce(method_for(range<I>) <@> lambda(i) -> reduce(method_for(range<I>) <@> lambda(j) -> p(i, j), (+)), lambda(a, b) where omp -> a + b, 0.0)\n"
let private intervalPayloadDecisionCases () =
    [ decisionCase "decision_cell_epilogue_declined_random_key_writer" intervalB1RandomKeySrc "cell-epilogue"
          (fun d -> d.Subject = "s" && declinedMentioning "`noise` reads `p` between the producer and the fold" d)
          "B1 declined: a random fill's key between writes the producer"
      decisionCase "decision_cell_epilogue_declined_compound_operand_writer" intervalB1CompoundSrc "cell-epilogue"
          (fun d -> d.Subject = "s" && declinedMentioning "`zz` reads `p` between the producer and the fold" d)
          "B1 declined: another compound's operand between writes the producer"
      decisionCase "decision_hot_row_declined_random_key_writer" intervalHotRowRandomKeySrc "hot-row-fold"
          (fun d -> d.Subject = "s" && declinedMentioning "`noise` reads `p` between the producer and the fold" d)
          "hot-row declined: a random fill's key between writes the producer"
      decisionCase "decision_compact_row_classes_declined_random_key_writer" intervalCompactRowRandomKeySrc "compact-row-classes"
          (fun d -> d.Subject = "s" && declinedMentioning "`noise` reads `p` between the producer and the fold" d)
          "compact row classes declined: a random fill's key between writes the producer" ]
// ---------------------------------------------------------------------------
// Compact row classes (Blade.Optimize.planCompactRowClasses; round 2 item B3,
// bench pairkernel). Values are pinned by corpus symmetry/129-130; the
// team-size determinism by the cli block. These pins read the emission and
// the decision record (codegen half included).
// ---------------------------------------------------------------------------

let private compactRowSrc =
    "type Pts = Idx<37>\n"
    + "type Dim = Idx<3>\n"
    + "let pts = method_for(range<Pts>, range<Dim>) <@> lambda(i, d) -> Float64((Int64(i) * 7 + Int64(d) * 3) % 11) - 5.0 |> compute\n"
    + "let k = method_for(pts, pts) <@> lambda(a: Array<Float64 like Dim>, b: Array<Float64 like Dim>) where comm(a, b), omp(a: 1) -> {\n"
    + "    let dx = a((0 : Dim)) - b((0 : Dim))\n"
    + "    dx * dx + a((2 : Dim)) * b((2 : Dim))\n"
    + "} |> compute\n"
    + "let ss = reduce(method_for(range<Pts>) <@> lambda(i) -> reduce(method_for(range<Pts>) <@> lambda(j) -> k(i, j) * k(i, j), (+)), lambda(u, v) where omp -> u + v, 0.0)\n"

/// Decisions of lowering AND C++ emission (the compact row-class outcome is
/// an emission decision: the orbit plan resolves there).
let private codegenDecisionsOf (src: string) : Result<Blade.Effects.Decision list, string> =
    Blade.Effects.Decisions.start ()
    let r =
        match lower src with
        | Error e -> Error ($"lower: {e}")
        | Ok ir ->
            CodeGen.genSelfContainedProgramFromIR ir "decisions" |> ignore
            Ok (Blade.Effects.Decisions.drain ())
    Blade.Effects.Decisions.drain () |> ignore
    r

let private codegenDecisionCase (name: string) (src: string) (rule: string) (want: Blade.Effects.Decision -> bool) (describe: string) =
    match codegenDecisionsOf src with
    | Error e -> resultLine Fail name e; false
    | Ok ds ->
        let mine = ds |> List.filter (fun d -> d.Rule = rule)
        if mine |> List.exists want then
            resultLine Pass name ($"{mine.Length} `{rule}` decision(s); {describe}")
            true
        else
            let seen = mine |> List.map Blade.Effects.Decisions.render |> String.concat " | "
            let shown = if seen = "" then "no decisions" else seen
            resultLine Fail name ($"wanted {describe}; saw: {shown}")
            false

/// A row kernel reading a binding made between the producer and the fold
/// (here: a function body; the nested-block twin is corpus loops/484).
let private hotRowLateCaptureSrc =
    "type Y = Idx<7>\n"
    + "type X = Idx<9>\n"
    + "function f() -> Float64 = {\n"
    + "    let u = method_for(range<Y, X>) <@> lambda(i, j) -> Float64(Int64(i) * Int64(j)) |> compute\n"
    + "    let lap = method_for(range<halo<Y, [-1, 0, 1]>, halo<X, [-1, 0, 1]>>) <@> lambda(wr, wc) -> u(wr(-1), wc(0)) + u(wr(0), wc(1)) - 2.0 * u(wr(0), wc(0)) |> compute\n"
    + "    let w2 = reduce(method_for(range<X>) <@> lambda(j) -> 0.5, (+))\n"
    + "    let s = reduce(method_for(lap) <@> lambda(row) -> prodsum(row, row) * w2, (+))\n"
    + "    s\n"
    + "}\n"
    + "let c = f()\n"

/// A row kernel that may fail, with a binding between producer and fold
/// (the corpus twin is loops/485).
/// G41 finding 14: the producer bumps G(0); the row kernel reads G(0).
let private hotRowWritingProducerSrc =
    "type I = Idx<2>\ntype J = Idx<3>\nlet mut G = [0.0]\n"
    + "let p = method_for(range<I>, range<J>) <@> lambda(i, j) -> {\n    G(0) = G(0) + 1.0\n    0.0\n} |> compute\n"
    + "let total = reduce(method_for(p) <@> lambda(row: Array<Float64 like J>) -> G(0), (+), 0.0)\n"

/// G41 finding 17: a pure producer, but the OUTER fold kernel writes G(0),
/// which the row map reads (fused 1.0 / G=[2.0]; hot-row 0.0 / G=[2.0]).
let private hotRowWritingOuterKernelSrc =
    "type I = Idx<2>\ntype J = Idx<3>\nlet mut G = [0.0]\n"
    + "function k(a: Float64, b: Float64) -> Float64 = {\n    G(0) = G(0) + 1.0\n    a + b\n}\n"
    + "let p = method_for(range<I>, range<J>) <@> lambda(i, j) -> Float64(i + j) |> compute\n"
    + "let total = reduce(method_for(p) <@> lambda(row: Array<Float64 like J>) -> G(0), k, 0.0)\n"

/// G41 finding 16: the compact producer bumps the scalar G; the walk reads it
/// (two-pass 12 / seen 3; row partials 9).
let private compactRowWritingProducerSrc =
    "type I = Idx<2>\nlet x = method_for(range<I>) <@> lambda(i) -> Float64(i) |> compute\nlet mut G = 0.0\n"
    + "let p = method_for(x, x) <@> lambda(a: Float64, b: Float64) where comm(a, b) -> {\n    G = G + 1.0\n    0.0\n} |> compute\n"
    + "let total = reduce(method_for(range<I>) <@> lambda(i) -> reduce(method_for(range<I>) <@> lambda(j) -> p(i, j) + G, (+), 0.0), (+), 0.0)\n"

let private hotRowMayFailSrc =
    "type R = Idx<5>\n"
    + "type C = Idx<6>\n"
    + "let (s, q) = {\n"
    + "    let zs = method_for(range<C>) <@> lambda(j) -> Int64(j) * 0 |> compute\n"
    + "    let d = reduce(zs, (+))\n"
    + "    let p = method_for(range<R, C>) <@> lambda(i, j) -> Int64(i) * Int64(j) |> compute\n"
    + "    let bad = 7 / d\n"
    + "    let t = reduce(method_for(p) <@> lambda(row: Array<Int64 like C>) -> 1 % row((0 : C)), (+))\n"
    + "    (t, bad)\n"
    + "}\n"

/// A collapse pragma across the row and cell levels: the nest places no row
/// epilogue, so the offered cell chain is declined by the post-check.
let private cepCollapsedSrc = hotRowSrc.Replace("lambda(wr, wc) where omp(wr: 1) ->", "lambda(wr, wc) where omp(wr: 1, wc: 1) ->")
/// The cell level threaded (`omp(wc: 1)`), the fold unthreaded.
let private cepThreadedCellSrc =
    hotRowSrc.Replace("lambda(wr, wc) where omp(wr: 1) ->", "lambda(wr, wc) where omp(wc: 1) ->")
             .Replace("lambda(p, q) where omp -> p + q", "lambda(p, q) -> p + q")

// ---------------------------------------------------------------------------
// Round 3 A7c: producer-INPUT legs (rule `epilogue-legs` v1, gap G69;
// Optimize.producerEpilogueLegAdmission, BLADE_EPILOGUE_LEGS). The values are
// pinned by corpus sibling-folds/019-027, the raw bits by `blade test
// cell-epilogue`; these pins read the emission (the legs inside the producer
// loop, no joined pass; the gate off restores it) in both lanes, and one
// decision per row of the five-party matrix.
// ---------------------------------------------------------------------------
/// The A7c tests' "gate on" arm: a null gate means the EXPLICIT opt-in
/// `BLADE_EPILOGUE_LEGS=1` (the kept one-level inline is opt-in since W2-4
/// round 8); `0` stays the gate off. The default (unset) build is asserted
/// separately by `eplDefaultDeclinesInline`.
let private eplArm (gate: string) = if isNull gate then "1" else gate

/// The bench saxpy shape at n = 1003: the producer in `kernel`, the input
/// sums in the caller (the admission inlines the call for them).
let private eplSaxpySrc =
    cepInputs
    + "function kernel(x: Array<Float64 like Ix>, y: Array<Float64 like Ix>) -> Float64 = {\n"
    + "    let y2 = method_for(zip(x, y)) <@> lambda(u, v) where omp(u: 1) -> 1.5 * u + v |> compute\n"
    + "    let s = reduce(method_for(y2) <@> lambda(w) -> w * w, lambda(p, q) where omp -> p + q, 0.0)\n"
    + "    s\n"
    + "}\n"
    + "let (checksum, input_checksum) = {\n"
    + "    let x = inputs(7)\n"
    + "    let y = inputs(5)\n"
    + "    (kernel(x, y), reduce(x, (+)) + reduce(y, (+)))\n"
    + "}\n"

/// The producer in the region itself: `body` is spliced after `s`, `legs`
/// is the region's result.
let private eplBlockSrc (producerKernel: string) (body: string) (legs: string) =
    cepInputs
    + "let (s, t) = {\n"
    + "    let x = inputs(7)\n"
    + "    let y = inputs(5)\n"
    + "    let xa = x\n"
    + $"    let y2 = method_for(zip(x, y)) <@> {producerKernel} |> compute\n"
    + "    let s = reduce(method_for(y2) <@> lambda(w) -> w * w, lambda(p, q) -> p + q, 0.0)\n"
    + body
    + $"    (s, {legs})\n"
    + "}\n"

let private eplPlain = "lambda(u, v) -> u + v"
let private eplSums = "reduce(x, (+)) + reduce(y, (+))"

/// An `epilogue-legs` decision over lowering + C++ codegen.
let private eplDecisionCase (name: string) (src: string) (want: Blade.Effects.Decision -> bool) (describe: string) =
    match cepDecisionsOf src with
    | Error e -> resultLine Fail name e; false
    | Ok ds ->
        let mine = ds |> List.filter (fun d -> d.Rule = "epilogue-legs")
        if mine |> List.exists want then
            resultLine Pass name ($"{mine.Length} `epilogue-legs` decision(s); {describe}")
            true
        else
            let seen = mine |> List.map Blade.Effects.Decisions.render |> String.concat " | "
            let shown = if seen = "" then "no decisions" else seen
            resultLine Fail name ($"wanted {describe}; saw: {shown}")
            false

let private evidenceMentions (needle: string) (d: Blade.Effects.Decision) = d.Evidence |> List.exists (fun e -> e.Contains needle)

let private mainBody (cpp: string) =
    let at = cpp.IndexOf "int main() {"
    if at < 0 then "" else cpp.Substring at

/// Repro serial: each leg's chain step sits inside the producer's loop right
/// behind the store, the joined pass over x and y is gone, and the tuple is
/// bound from the chains at the join's statement.
let private eplCppRepro () =
    let name = "epilogue_legs_cpp_repro_chains_inside_producer_loop"
    match withGate "BLADE_FP_REPRO" "1" (fun () -> withGate "BLADE_OMP_THREADS" "1" (fun () -> cppOfSource name eplSaxpySrc)) with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        let m = mainBody cpp
        let store = m.IndexOf "[__fk] = __c;"
        let step = System.Text.RegularExpressions.Regex.Match(m, @"__epl_acc_\w+_0 = __wrap_\d+_epl_\w+_0\(__epl_acc_\w+_0, __fp_\w+\[__fk\]\);")
        let close = m.IndexOf("    }\n", (if step.Success then step.Index else 0))
        let bind = m.IndexOf "= __epl_acc_"
        let joinedLoop = m.Contains "for (size_t __i0 = 0; __i0 < 1003; __i0++)"
        if store >= 0 && step.Success && step.Index > store && bind > step.Index && not joinedLoop then
            resultLine Pass name "the legs' `acc = F(acc, x[i])` right behind the producer's store; no joined pass in main; the join binds from the chains"
            true
        else
            resultLine Fail name ($"store={store} step={step.Success}@{step.Index} bind={bind} joinedLoop={joinedLoop} close={close}")
            false

/// Licensed serial: each leg's own lane state fed over the producer's blocks.
let private eplCppLicensed () =
    let name = "epilogue_legs_cpp_licensed_lane_states_in_producer_blocks"
    match withGate "BLADE_OMP_THREADS" "1" (fun () -> cppOfSource name eplSaxpySrc) with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        let m = mainBody cpp
        let feedB1 = m.IndexOf "blade_fold::feed_sq<double, 8>(__cep_ls"
        let feedLeg = m.IndexOf "blade_fold::feed_sum<double, 8>(__epl_ls_"
        let finishLeg = m.IndexOf "blade_fold::finish_sum<double, 8>(__epl_ls_"
        if feedB1 >= 0 && feedLeg > feedB1 && finishLeg > feedLeg && countOf "feed_sum<double, 8>(__epl_ls_" m = 2
           && not (m.Contains "for (size_t __i0 = 0; __i0 < 1003; __i0++)") then
            resultLine Pass name "two leg lane states fed behind B1's feed in each block, finished on the tail; no joined pass"
            true
        else
            resultLine Fail name ($"feedB1={feedB1} feedLeg={feedLeg} finishLeg={finishLeg}")
            false

/// BLADE_EPILOGUE_LEGS=0: no leg in the producer, the joined pass back, and
/// the callee not inlined for the legs.
let private eplCppGateOff () =
    let name = "epilogue_legs_gate_off_restores_joined_pass"
    match withGate "BLADE_EPILOGUE_LEGS" "0" (fun () -> withGate "BLADE_FP_REPRO" "1" (fun () -> withGate "BLADE_OMP_THREADS" "1" (fun () -> cppOfSource name eplSaxpySrc))) with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        let m = mainBody cpp
        if not (cpp.Contains "__epl_") && m.Contains "for (size_t __i0 = 0; __i0 < 1003; __i0++)" && m.Contains "kernel(" then
            resultLine Pass name "no `__epl_` anywhere; main runs the joined pass and calls kernel"
            true
        else
            resultLine Fail name ($"""epl={cpp.Contains "__epl_"} joined={m.Contains "for (size_t __i0 = 0; __i0 < 1003; __i0++)"} call={m.Contains "kernel("}""")
            false

/// The LLVM lane: one loop fewer with the gate on (the joined pass gone; the
/// inlined callee no longer emitted), and its own applied record.
let private eplLlvm () =
    let name = "epilogue_legs_llvm_one_fused_loop"
    let llWith (gate: string) =
        withGate "BLADE_EPILOGUE_LEGS" (eplArm gate) (fun () ->
        withGate "BLADE_FP_REPRO" "1" (fun () -> withGate "BLADE_OMP_THREADS" "1" (fun () ->
            Blade.Effects.Decisions.start ()
            let r =
                match lower eplSaxpySrc with
                | Error e -> Error e
                | Ok ir -> (try EmitLlvm.tryEmitProgramNamed "epl_ll" ir with ex -> Error ("raised: " + ex.Message))
            let ds = Blade.Effects.Decisions.drain ()
            r |> Result.map (fun ll -> (ll, ds)))))
    match llWith null, llWith "0" with
    | Ok (on, ds), Ok (off, _) ->
        let loops (s: string) = s.Replace("\r\n", "\n").Split('\n') |> Array.filter (fun l -> l.StartsWith "loop.cond") |> Array.length
        let rec' = ds |> List.exists (fun d -> d.Rule = "epilogue-legs" && applied d && evidenceMentions "llvm lane: legs x-sum, y-sum folded inside" d)
        if loops on = loops off - 1 && rec' then
            resultLine Pass name ($"{loops on} loops on vs {loops off} off; the llvm lane's applied record")
            true
        else
            resultLine Fail name ($"loops on={loops on} off={loops off} record={rec'}")
            false
    | Error e, _ | _, Error e -> resultLine Fail name ($"the llvm lane refused: {e}"); false

/// A corpus file's source (the checkout's tests/corpus, else the copy
/// deployed beside the binary).
let private corpusSource (rel: string) : Result<string, string> =
    let cands =
        [ System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "tests", "corpus", rel)
          System.IO.Path.Combine(System.AppContext.BaseDirectory, "tests", "corpus", rel) ]
    match cands |> List.tryFind System.IO.File.Exists with
    | Some p -> Ok (System.IO.File.ReadAllText p)
    | None -> Error $"corpus file {rel} not found"

/// The LLVM lane's fused producer loop, structurally: the loop body that
/// stores into a pool (a GEP-addressed store) and updates the most
/// alloca-slot accumulators with an `fadd`. Returns (accumulator updates,
/// GEP reads) of that block and the number of loop bodies in the module.
/// It reads the lane's RAW emitter IR, before clang's optimizer and
/// vectorizer (it would not recognize vectorized IR), and picks the
/// pool-storing block with the most accumulators rather than tracing named
/// inputs -- adequate for these small fixtures.
let private llvmFusedLoop (ll: string) : int * int * int =
    let lines = ll.Replace("\r\n", "\n").Split('\n')
    let slots =
        lines |> Array.choose (fun l ->
            let m = System.Text.RegularExpressions.Regex.Match(l, @"^\s*(%r\d+) = alloca ")
            if m.Success then Some m.Groups.[1].Value else None) |> Set.ofArray
    // blocks: label line .. next label
    let blocks = ResizeArray<string list>()
    let cur = ResizeArray<string>()
    for l in lines do
        if System.Text.RegularExpressions.Regex.IsMatch(l, @"^[A-Za-z_.][A-Za-z0-9_.]*:") then
            blocks.Add(List.ofSeq cur); cur.Clear()
        cur.Add l
    blocks.Add(List.ofSeq cur)
    let bodies = blocks |> Seq.filter (fun b -> match b with h :: _ -> h.StartsWith "loop.body" | [] -> false) |> List.ofSeq
    let stats (b: string list) =
        let geps = b |> List.choose (fun l ->
                    let m = System.Text.RegularExpressions.Regex.Match(l, @"^\s*(%r\d+) = getelementptr inbounds double")
                    if m.Success then Some m.Groups.[1].Value else None) |> Set.ofList
        let fadds = b |> List.choose (fun l ->
                    let m = System.Text.RegularExpressions.Regex.Match(l, @"^\s*(%r\d+) = fadd ")
                    if m.Success then Some m.Groups.[1].Value else None) |> Set.ofList
        let poolStore = b |> List.exists (fun l ->
                            let m = System.Text.RegularExpressions.Regex.Match(l, @"store double %r\d+, ptr (%r\d+)")
                            m.Success && geps.Contains m.Groups.[1].Value)
        let accUpdates = b |> List.filter (fun l ->
                            let m = System.Text.RegularExpressions.Regex.Match(l, @"store double (%r\d+), ptr (%r\d+)")
                            m.Success && fadds.Contains m.Groups.[1].Value && slots.Contains m.Groups.[2].Value) |> List.length
        (poolStore, accUpdates, geps.Count)
    let fused =
        bodies |> List.map stats |> List.filter (fun (p, _, _) -> p)
        |> List.sortByDescending (fun (_, a, _) -> a) |> List.tryHead
    match fused with
    | Some (_, acc, geps) -> (acc, geps, bodies.Length)
    | None -> (0, 0, bodies.Length)

let private eplLlvmOf (src: string) (gate: string) =
    withGate "BLADE_EPILOGUE_LEGS" (eplArm gate) (fun () ->
        Blade.Effects.Decisions.start ()
        let r =
            match lower src with
            | Error e -> Error e
            | Ok ir -> (try EmitLlvm.tryEmitProgramNamed "epl_ll" ir with ex -> Error ("raised: " + ex.Message))
        let ds = Blade.Effects.Decisions.drain ()
        r |> Result.map (fun ll -> (ll, ds)))

/// THE FAILURE FIXTURE ITSELF (sibling-folds/026; review finding 3): the
/// legs ride the producer in both lanes, the joined pass is gone (and
/// restored by the gate), and the producer's stores come before the failing
/// binding. (The fault / site / frame text equality gate on vs off is
/// executed in `blade test cell-epilogue`.)
let private eplFailureFixtureRoute () =
    let name = "epilogue_legs_failure_fixture_026_rides_both_lanes"
    match corpusSource "sibling-folds/026_input_legs_failure_between_aborts.blade" with
    | Error e -> resultLine Fail name e; false
    | Ok src ->
    let src = src.Substring(src.IndexOf '\n' + 1)   // the harness drops the TEST line
    let cppWith gate = withGate "BLADE_EPILOGUE_LEGS" (eplArm gate) (fun () -> withGate "BLADE_OMP_THREADS" "1" (fun () -> cppOfSource name src))
    match cppWith null, cppWith "0", withGate "BLADE_OMP_THREADS" "1" (fun () -> eplLlvmOf src null), withGate "BLADE_OMP_THREADS" "1" (fun () -> eplLlvmOf src "0") with
    | Ok on, Ok off, Ok (llOn, ds), Ok (llOff, _) ->
        let m, mOff = mainBody on, mainBody off
        let feeds = countOf "blade_fold::feed_sum<double, 8>(__epl_ls_" m
        let lastStore = m.LastIndexOf "[__fk] = (__fp_"
        let boom = m.IndexOf "blade_rt::idiv64(10L"
        let joined (s: string) = s.Contains "for (size_t __i0 = 0; __i0 < 1003; __i0++)"
        let (accOn, gepOn, loopsOn) = llvmFusedLoop llOn
        let (accOff, gepOff, loopsOff) = llvmFusedLoop llOff
        let llvmRec = ds |> List.exists (fun d -> d.Rule = "epilogue-legs" && applied d && evidenceMentions "llvm lane: legs x-sum, y-sum folded inside" d)
        if feeds = 2 && lastStore >= 0 && boom > lastStore && not (joined m) && joined mOff && not (off.Contains "__epl_")
           && accOn = accOff + 2 && gepOn = gepOff + 2 && loopsOn = loopsOff - 1 && llvmRec then
            resultLine Pass name ($"C++: 2 leg feeds in the producer, its stores before the BL8013 site, no joined pass (gate off restores it); LLVM: fused loop {accOff}->{accOn} accumulator updates, {gepOff}->{gepOn} cell reads, {loopsOff}->{loopsOn} loops")
            true
        else
            resultLine Fail name ($"feeds={feeds} store={lastStore} boom={boom} joinedOn={joined m} joinedOff={joined mOff} llvm acc {accOff}->{accOn} gep {gepOff}->{gepOn} loops {loopsOff}->{loopsOn} rec={llvmRec}")
            false
    | Error e, _, _, _ | _, Error e, _, _ | _, _, Error e, _ | _, _, _, Error e -> resultLine Fail name e; false

/// The saxpy shape's LLVM route, identified in the fused loop itself: two
/// more accumulator updates and two more cell reads than gate-off's fused
/// loop, and the joined pass's loop gone.
let private eplLlvmAccumulators () =
    let name = "epilogue_legs_llvm_input_accumulators_in_producer_loop"
    match withGate "BLADE_FP_REPRO" "1" (fun () -> withGate "BLADE_OMP_THREADS" "1" (fun () -> eplLlvmOf eplSaxpySrc null, eplLlvmOf eplSaxpySrc "0")) with
    | Ok (on, _), Ok (off, _) ->
        let (aOn, gOn, lOn) = llvmFusedLoop on
        let (aOff, gOff, lOff) = llvmFusedLoop off
        if aOn = aOff + 2 && gOn = gOff + 2 && lOn = lOff - 1 then
            resultLine Pass name ($"fused loop: {aOff}->{aOn} accumulator updates, {gOff}->{gOn} cell reads; {lOff}->{lOn} loops")
            true
        else
            resultLine Fail name ($"acc {aOff}->{aOn} gep {gOff}->{gOn} loops {lOff}->{lOn}")
            false
    | Error e, _ | _, Error e -> resultLine Fail name e; false

/// Finding 4: an inline is kept only when a lane would carry the legs. A
/// nonliteral epilogue seed (no lane's fused form evaluates it ahead) and
/// BLADE_FOLD_LANES=off in a licensed build (no lane-state form) decline AT
/// PLANNING, and the call stays a call.
let private eplNoRouteKeepsCall (name: string) (src: string) (env: (string * string) list) (needle: string) =
    let rec withAll (es: (string * string) list) f = match es with [] -> f () | (k, v) :: rest -> withGate k v (fun () -> withAll rest f)
    match withAll env (fun () -> cepDecisionsOf src), withAll env (fun () -> cppOfSource name src) with
    | Ok ds, Ok cpp ->
        let mine = ds |> List.filter (fun d -> d.Rule = "epilogue-legs")
        let declined = mine |> List.exists (declinedMentioning needle)
        let callKept = (mainBody cpp).Contains "kernel(" && not (cpp.Contains "__epl_")
        if declined && callKept && not (mine |> List.exists applied) then
            resultLine Pass name ($"declined at planning ({needle}); main still calls kernel; no leg emitted")
            true
        else
            let seen = mine |> List.map Blade.Effects.Decisions.render |> String.concat " | "
            resultLine Fail name ($"declined={declined} callKept={callKept}; saw: {seen}")
            false
    | Error e, _ | _, Error e -> resultLine Fail name e; false

let private eplNonliteralSeedSrc =
    cepInputs
    + "function kernel(x: Array<Float64 like Ix>, y: Array<Float64 like Ix>, s0: Float64) -> Float64 = {\n"
    + "    let y2 = method_for(zip(x, y)) <@> lambda(u, v) -> 1.5 * u + v |> compute\n"
    + "    let s = reduce(method_for(y2) <@> lambda(w) -> w * w, lambda(p, q) -> p + q, s0)\n"
    + "    s\n"
    + "}\n"
    + "let (checksum, input_checksum) = {\n"
    + "    let x = inputs(7)\n"
    + "    let y = inputs(5)\n"
    + "    let z0 = x((3 : Ix)) * 0.0 + 0.5\n"
    + "    (kernel(x, y, z0), reduce(x, (+)) + reduce(y, (+)))\n"
    + "}\n"

/// The B1-shaped THROWING callee (sibling-folds/029): never inlined for the
/// legs (its body may fail), so the call -- and its frame -- stays.
let private eplThrowingCalleeKeepsCall () =
    let name = "epilogue_legs_throwing_callee_not_inlined"
    match corpusSource "sibling-folds/029_input_legs_throwing_callee_keeps_frame.blade" with
    | Error e -> resultLine Fail name e; false
    | Ok src ->
    let src = src.Substring(src.IndexOf '\n' + 1)
    match withGate "BLADE_OMP_THREADS" "1" (fun () -> cepDecisionsOf src), withGate "BLADE_OMP_THREADS" "1" (fun () -> cppOfSource name src) with
    | Ok ds, Ok cpp ->
        let noLegs = ds |> List.forall (fun d -> d.Rule <> "epilogue-legs") && not (cpp.Contains "__epl_")
        let callKept = (mainBody cpp).Contains "kernel(" && cpp.Contains "BLADE_FRAME(\"kernel\""
        if noLegs && callKept then
            resultLine Pass name "no epilogue-legs plan; main calls kernel, whose frame holds the division"
            true
        else
            resultLine Fail name ($"noLegs={noLegs} callKept={callKept}")
            false
    | Error e, _ | _, Error e -> resultLine Fail name e; false

/// sibling-folds/028: the transitive-capture regions and the named view.
let private eplCorpus028 () =
    let name = "epilogue_legs_028_transitive_captures_and_views"
    match corpusSource "sibling-folds/028_input_legs_transitive_captures_and_views.blade" with
    | Error e -> resultLine Fail name e; false
    | Ok src ->
    let src = src.Substring(src.IndexOf '\n' + 1)
    match withGate "BLADE_FP_REPRO" "1" (fun () -> withGate "BLADE_OMP_THREADS" "1" (fun () -> cepDecisionsOf src)) with
    | Error e -> resultLine Fail name e; false
    | Ok ds ->
        let legs (subj: string) = ds |> List.filter (fun d -> d.Rule = "epilogue-legs" && d.Subject = subj)
        let ok =
            (legs "_(early1,e1t)" |> List.exists applied) && (legs "_(early2,e2t)" |> List.exists applied)
            && (legs "_(late1,l1t)").IsEmpty && (legs "_(late2,l2t)").IsEmpty
            && (legs "_(view,vt)" |> List.exists (declinedMentioning "not fresh storage"))
            && (legs "_(view,vt)" |> List.forall (applied >> not))
        if ok then
            resultLine Pass name "early1/early2 (module callees) admitted; late1/late2 (capturing nested callees) never reach the admission; the named view declined"
            true
        else
            let seen = ds |> List.filter (fun d -> d.Rule = "epilogue-legs") |> List.map Blade.Effects.Decisions.render |> String.concat " | "
            resultLine Fail name ($"saw: {seen}")
            false

/// The mt8 decline adds no `[omp]` census line (the legs requested nothing):
/// the threaded emission carries exactly gate-off's census lines.
let private eplTeamNoCensus () =
    let name = "epilogue_legs_mt8_decline_adds_no_omp_census"
    let census (cpp: string) = cpp.Replace("\r\n", "\n").Split('\n') |> Array.filter (fun l -> l.Contains "[omp] requested but emitted serial") |> Array.length
    let emit gate = withGate "BLADE_EPILOGUE_LEGS" (eplArm gate) (fun () -> withGate "BLADE_FP_REPRO" "1" (fun () -> withGate "BLADE_OMP_THREADS" null (fun () -> cppOfSource name eplSaxpySrc)))
    match emit null, emit "0" with
    | Ok on, Ok off ->
        if census on = census off && not (on.Contains "__epl_") then
            resultLine Pass name ($"{census on} census line(s) with the legs declined, {census off} with the gate off; no leg emitted")
            true
        else
            resultLine Fail name ($"census on={census on} off={census off}")
            false
    | Error e, _ | _, Error e -> resultLine Fail name e; false

/// Every A7c return-boundary release in `body`: each released pool is freed
/// exactly once and never named after its release. Ok = the pool names.
let private releasesSafe (body: string) : Result<string list, string> =
    let lines = body.Replace("\r\n", "\n").Split('\n')
    let rx = System.Text.RegularExpressions.Regex(@"its pool `(__v\d+)` is released at that return boundary")
    let rels = lines |> Array.mapi (fun i l -> (i, rx.Match l)) |> Array.filter (fun (_, m) -> m.Success) |> Array.map (fun (i, m) -> (i, m.Groups.[1].Value))
    let tok (n: string) (l: string) = System.Text.RegularExpressions.Regex.IsMatch(l, "(?<![A-Za-z0-9_])" + n + "(?![A-Za-z0-9_])")
    let problem =
        rels |> Array.tryPick (fun (i, n) ->
            let frees = lines |> Array.filter (fun l -> l.Contains $"({n}.data, {n}.extents)") |> Array.length
            let after = lines |> Array.skip (i + 2) |> Array.exists (tok n)
            if frees <> 1 then Some $"{n} released {frees} times"
            elif after then Some $"{n} is named after its release"
            else None)
    match problem with
    | Some p -> Error p
    | None -> Ok (rels |> Array.map snd |> List.ofArray)
/// Finding 2 (the route taken): the inlined callee's pool is freed at the
/// call's return boundary (behind the inlined spine's evaluated return) -- in main
/// (no frame: the plain dense free) and in a function body (the frame's own
/// free, then spared at the function's return: no double free).
let private eplReturnPointFree () =
    let name = "epilogue_legs_inlined_pools_freed_at_return_point"
    let fnSrc =
        cepInputs
        + "function kernel(x: Array<Float64 like Ix>, y: Array<Float64 like Ix>) -> Float64 = {\n"
        + "    let y2 = method_for(zip(x, y)) <@> lambda(u, v) -> 2.0 * u + v |> compute\n"
        + "    let s = reduce(method_for(y2) <@> lambda(w) -> w * w, lambda(p, q) -> p + q, 0.0)\n"
        + "    s\n"
        + "}\n"
        + "function driver(x: Array<Float64 like Ix>, y: Array<Float64 like Ix>) -> Float64 = {\n"
        + "    let k = kernel(x, y)\n"
        + "    k + 1000000.0 * (reduce(x, (+)) + reduce(y, (+)))\n"
        + "}\n"
        + "let r = driver(inputs(7), inputs(5))\n"
    match withGate "BLADE_FP_REPRO" "1" (fun () -> withGate "BLADE_OMP_THREADS" "1" (fun () -> cppOfSource name eplSaxpySrc, cppOfSource name fnSrc)) with
    | Ok saxpy, Ok fn ->
        let d = fn.IndexOf "double driver(Array<double, 1> x, Array<double, 1> y) {"
        let dBody = if d < 0 then "" else fn.Substring(d, fn.IndexOf("\n}\n", d) - d)
        match releasesSafe (mainBody saxpy), releasesSafe dBody with
        | Ok [ _ ], Ok [ _ ] when dBody.Contains "__epl_acc_" && countOf "deallocate<typename promote<double, 1>::type, nullptr>(" dBody = 1 ->
            resultLine Pass name "main and driver each release y2 once at the inlined call's return boundary, with no read after it; driver does not free it again at its return"
            true
        | rm, rd -> resultLine Fail name ($"main {rm}; driver {rd}"); false
    | Error e, _ | _, Error e -> resultLine Fail name e; false

/// NEW 1 (second review): the join lands INSIDE the inlined callee
/// (sibling-folds/030 `later`: inner folds of x and y between y2 and a later
/// map z that reads y2) -- the pools are released at the call's return
/// boundary, after z; and with the cell epilogue declined at emission (omp
/// test mode) y2's two-pass epilogue fold still reads it before its release
/// (`early`: the inner folds before s).
let private eplJoinInsideCallee () =
    let name = "epilogue_legs_join_inside_inlined_callee_releases_at_return"
    match corpusSource "sibling-folds/030_input_legs_join_inside_inlined_callee.blade" with
    | Error e -> resultLine Fail name e; false
    | Ok src ->
    let src = src.Substring(src.IndexOf '\n' + 1)
    let emit (testMode: bool) =
        withGate "BLADE_FP_REPRO" "1" (fun () -> withGate "BLADE_OMP_THREADS" "1" (fun () -> withGate "BLADE_POOL_REUSE" "off" (fun () ->
            Blade.CodeGenState.setOmpTestMode testMode
            try cppOfSource name src
            finally Blade.CodeGenState.setOmpTestMode false)))
    match emit false, emit true with
    | Ok normal, Ok testMode ->
        match releasesSafe (mainBody normal), releasesSafe (mainBody testMode) with
        | Ok rn, Ok rt when rn.Length = 3 && rt.Length = 3 && (mainBody normal).Contains "__epl_acc_"
                            && not ((mainBody testMode).Contains "__cep_") && not ((mainBody testMode).Contains "__epl_") ->
            resultLine Pass name ($"3 callee pools (y2 and z of later, y2 of early) released once each at the call's return boundary, no read after any release -- with the epilogue fused, and with it ACTUALLY declined under omp test mode (no `__cep_` / `__epl_` route in main)")
            true
        | a, b -> resultLine Fail name (sprintf "normal %A; test mode %A" a b); false
    | Error e, _ | _, Error e -> resultLine Fail name e; false

/// Round 4: the release in the LLVM lane, read from the raw emitter IR of one
/// function (`define ... @<fn>(` .. `}`): every `; A7c:` free releases a pool
/// this function allocated (directly, or through the tracking slot the alloc
/// was stored to, then nulled), once, and the pool register is never used
/// after it. Ok = the number of releases.
let private llvmReleasesSafe (ll: string) (fn: string) : Result<int, string> =
    let lines = ll.Replace("\r\n", "\n").Split('\n')
    let start = lines |> Array.tryFindIndex (fun l -> l.StartsWith "define " && System.Text.RegularExpressions.Regex.IsMatch(l, "@" + fn + @"(_\d+)?\("))
    match start with
    | None -> Error $"no function @{fn}"
    | Some st ->
    let stop = (lines |> Array.skip st |> Array.findIndex (fun l -> l = "}")) + st
    let body = lines.[st .. stop]
    let rx (p: string) (l: string) = System.Text.RegularExpressions.Regex.Match(l, p)
    let grab (p: string) (k: int) (l: string) =
        let m = rx p l
        if m.Success then Some (m.Groups.[k].Value) else None
    let grab2 (p: string) (l: string) =
        let m = rx p l
        if m.Success then Some (m.Groups.[1].Value, m.Groups.[2].Value) else None
    let allocOf = body |> Array.choose (grab @"^\s*(%r\d+) = call ptr @blade_alloc_cells" 1) |> Set.ofArray
    let slotOf =
        body |> Array.choose (grab2 @"^\s*store ptr (%r\d+), ptr (%r\d+)")
        |> Array.filter (fun (r, _) -> allocOf.Contains r) |> Array.map (fun (r, slot) -> (slot, r)) |> Map.ofArray
    let loadOf = body |> Array.choose (grab2 @"^\s*(%r\d+) = load ptr, ptr (%r\d+)") |> Map.ofArray
    let rels = body |> Array.mapi (fun i l -> (i, l)) |> Array.filter (fun (_, l) -> l.Contains "; A7c:")
    let pools =
        rels |> Array.map (fun (i, l) ->
            let r = (rx @"@blade_free\(ptr (%r\d+)\)" l).Groups.[1].Value
            let pool =
                if allocOf.Contains r then Some r
                else
                    match Map.tryFind r loadOf with
                    | Some slot -> Map.tryFind slot slotOf
                    | None -> None
            (i, pool))
    let tok (r: string) (l: string) = System.Text.RegularExpressions.Regex.IsMatch(l, System.Text.RegularExpressions.Regex.Escape r + "(?![0-9])")
    match pools |> Array.tryPick (fun (i, p) ->
            match p with
            | None -> Some $"release at line {i} frees no pool this function allocated"
            | Some r ->
                let after = body |> Array.skip (i + 1) |> Array.exists (fun l -> tok r l && not (l.TrimStart().StartsWith "store ptr null"))
                // a slot-form release must null its tracking slot on the very next line
                let freed = (rx @"@blade_free\(ptr (%r\d+)\)" body.[i]).Groups.[1].Value
                let slotForm = not (allocOf.Contains freed)
                let nulled =
                    not slotForm
                    || (match Map.tryFind freed loadOf with
                        | Some slot -> i + 1 < body.Length && body.[i + 1].Trim() = $"store ptr null, ptr {slot}"
                        | None -> false)
                if after then Some $"{r} is used after its release"
                elif not nulled then Some $"the slot-form release of {r} does not null its tracking slot"
                else None) with
    | Some why -> Error why
    | None ->
        let distinct = pools |> Array.choose snd |> Array.distinct
        if distinct.Length <> pools.Length then Error "a pool is released twice" else Ok pools.Length

let private eplLlvmEmit (src: string) =
    withGate "BLADE_FP_REPRO" "1" (fun () -> withGate "BLADE_OMP_THREADS" "1" (fun () -> withGate "BLADE_POOL_REUSE" "off" (fun () -> eplLlvmOf src null)))

/// Round 4 hole 4 (the LLVM lane): 019 and 031 release their inlined pools in
/// main (019: y2; 031: y2 and w2, two plans, one call), 034 in its function
/// caller through the tracking slot -- each after its last use, once.
let private eplLlvmReleaseOrder () =
    let name = "epilogue_legs_llvm_release_order_main_and_function"
    let one (rel: string) (fn: string) (want: int) =
        match corpusSource rel with
        | Error e -> Error e
        | Ok src ->
            let src = src.Substring(src.IndexOf '\n' + 1)
            match eplLlvmEmit src with
            | Error e -> Error e
            | Ok (ll, _) ->
                match llvmReleasesSafe ll fn with
                | Ok n when n = want -> Ok n
                | Ok n -> Error $"{rel}: {n} releases in @{fn}, wanted {want}"
                | Error e -> Error $"{rel}: {e}"
    let cases =
        [ ("sibling-folds/019_input_legs_ride_producer.blade", "main", 1)
          ("sibling-folds/031_input_legs_two_plans_one_inlined_call.blade", "main", 2)
          ("sibling-folds/032_input_legs_unused_final_join_atom_return.blade", "main", 1)
          ("sibling-folds/034_input_legs_function_caller_failure_after_return.blade", "bl_driver", 1) ]
    let res = cases |> List.map (fun (r, f, w) -> one r f w)
    // 034: the function caller's release sits BEFORE the failing blade_idiv call
    let order =
        match corpusSource "sibling-folds/034_input_legs_function_caller_failure_after_return.blade" with
        | Error e -> Error e
        | Ok src ->
            match eplLlvmEmit (src.Substring(src.IndexOf '\n' + 1)) with
            | Error e -> Error e
            | Ok (ll, _) ->
                let lines = ll.Replace("\r\n", "\n").Split('\n')
                let st = lines |> Array.findIndex (fun l -> l.StartsWith "define " && System.Text.RegularExpressions.Regex.IsMatch(l, @"@bl_driver(_\d+)?\("))
                let rel = lines |> Array.skip st |> Array.tryFindIndex (fun l -> l.Contains "; A7c:")
                let div = lines |> Array.skip st |> Array.tryFindIndex (fun l -> l.Contains "@blade_idiv(")
                match rel, div with
                | Some r, Some d when r < d -> Ok 1
                | r, d -> Error (sprintf "034 driver: release %A, blade_idiv %A" r d)
    let res = res @ [ order ]
    match res |> List.tryPick (function Error e -> Some e | Ok _ -> None) with
    | Some e -> resultLine Fail name e; false
    | None -> resultLine Pass name "019 / 031 / 032 main and 034's function caller: each inlined pool released once after its last use (031: y2 and w2); the function caller through its tracking slot"; true

/// 033's witness is live: the same two maps in one function body WITHOUT an
/// inline between them ARE merged by CSE (so only the event's barrier keeps
/// 033's z out of the released y2).
let private eplCseProbeSrc =
    cepInputs
    + "function cell(u: Float64, v: Float64) -> Float64 = 2.0 * u + v\n"
    + "function driver(x: Array<Float64 like Ix>, y: Array<Float64 like Ix>) -> Float64 = {\n"
    + "    let y2 = method_for(zip(x, y)) <@> lambda(u, v) -> cell(u, v) |> compute\n"
    + "    let s = reduce(method_for(y2) <@> lambda(w) -> w * w, lambda(p, q) -> p + q, 0.0)\n"
    + "    let z = method_for(zip(x, y)) <@> lambda(u, v) -> cell(u, v) |> compute\n"
    + "    let c = reduce(method_for(z) <@> lambda(w) -> w * w * w, lambda(p, q) -> p + q, 0.0)\n"
    + "    s + c\n"
    + "}\n"
    + "let res = driver(inputs(7), inputs(5))\n"
/// Round 4 holes 1-3 (the C++ lane): 032 -- the callee's last let is a join
/// the rebuild drops, the return atom an earlier scalar: y2 still released in
/// main; 033 -- CSE on: the caller's equal map z keeps its own pool (no
/// merge into the released y2); 034 -- the function caller releases y2
/// before the failing division; 035 -- the callee's pools take no part in
/// scratch reuse in the caller, released once each, while the uninlined
/// kernel keeps its own reuse.
let private eplRound4Cpp () =
    let name = "epilogue_legs_round4_events_cpp"
    let emit rel =
        match corpusSource rel with
        | Error e -> Error e
        | Ok src ->
            let src = src.Substring(src.IndexOf '\n' + 1)
            withGate "BLADE_FP_REPRO" "1" (fun () -> withGate "BLADE_OMP_THREADS" "1" (fun () -> cppOfSource name src))
    let fnBody (cpp: string) (sigText: string) =
        let d = cpp.IndexOf sigText
        if d < 0 then "" else cpp.Substring(d, cpp.IndexOf("\n}\n", d) - d)
    let driverSig = "double driver(Array<double, 1> x, Array<double, 1> y) {"
    match emit "sibling-folds/032_input_legs_unused_final_join_atom_return.blade",
          emit "sibling-folds/033_input_legs_cse_barrier_function_caller.blade",
          emit "sibling-folds/034_input_legs_function_caller_failure_after_return.blade",
          emit "sibling-folds/035_input_legs_scratch_reuse_inside_callee.blade" with
    | Ok c32, Ok c33, Ok c34, Ok c35 ->
        let d33, d34, d35 = fnBody c33 driverSig, fnBody c34 driverSig, fnBody c35 driverSig
        let checks =
            [ ("032 main", (match releasesSafe (mainBody c32) with Ok [ _ ] -> None | r -> Some (sprintf "%A" r)))
              ("033 driver", (match releasesSafe d33 with
                              | Ok [ _ ] when System.Text.RegularExpressions.Regex.Matches(d33, @"(?<!de)allocate<typename promote<double, 1>::type, nullptr>\(").Count = 2 -> None
                              | r -> Some (sprintf "%A; allocations %d" r (System.Text.RegularExpressions.Regex.Matches(d33, @"(?<!de)allocate<typename promote<double, 1>::type, nullptr>\(").Count))))
              ("034 driver", (let rel = d34.IndexOf "is released at that return boundary"
                              let boom = d34.IndexOf "blade_rt::idiv64(10L"
                              match releasesSafe d34 with
                              | Ok [ _ ] when rel >= 0 && boom > rel -> None
                              | r -> Some (sprintf "%A; release %d, division %d" r rel boom)))
              ("035 driver", (match releasesSafe d35 with
                              | Ok [ _; _ ] when not (d35.Contains "pool reuse") -> None
                              | r -> Some (sprintf "%A; reuse in driver %b" r (d35.Contains "pool reuse")))) ]
        match checks |> List.tryPick (fun (w, p) -> p |> Option.map (fun why -> $"{w}: {why}")) with
        | Some why -> resultLine Fail name why; false
        | None -> resultLine Pass name "032: released in main though the callee's last let was dropped; 033: z keeps its own pool under CSE; 034: released before the failing division; 035: no reuse across the boundary, both pools released once"; true
    | Error e, _, _, _ | _, Error e, _, _ | _, _, Error e, _ | _, _, _, Error e -> resultLine Fail name e; false

/// Round 4: IRValidate's rule on hand-built violations of the lowered saxpy
/// shape -- the event moved before the epilogue fold that reads y2 (a read
/// after release), and an event naming a pool bound outside its inline.
let private eplValidateRule () =
    let name = "epilogue_legs_irvalidate_release_rule"
    match withGate "BLADE_FP_REPRO" "1" (fun () -> withGate "BLADE_OMP_THREADS" "1" (fun () -> lower eplSaxpySrc)) with
    | Error e -> resultLine Fail name e; false
    | Ok ir ->
    let rec unroll (e: IRExpr) = match e with IRLet (id, v, b) -> let (ls, r) = unroll b in ((id, v) :: ls, r) | _ -> ([], e)
    let refold (ls: (IRId * IRExpr) list) (r: IRExpr) = List.foldBack (fun (id, v) acc -> IRLet (id, v, acc)) ls r
    let hasEvent (ls: (IRId * IRExpr) list) = ls |> List.exists (fun (_, v) -> match v with IRReleasePools _ -> true | _ -> false)
    let mutate (f: (IRId * IRExpr) list -> (IRId * IRExpr) list) =
        let fixB (b: IRBinding) =
            let (ls, r) = unroll b.Value
            if hasEvent ls then { b with Value = refold (f ls) r } else b
        let fixM (m: IRModule) = { m with Bindings = m.Bindings |> List.map fixB }
        { ir with Modules = ir.Modules |> List.map fixM }
    let ok0 = match Blade.IRValidate.validateIR ir with Ok _ -> true | Error _ -> false
    // (1) the event right behind its spine start (before the epilogue fold)
    let early =
        mutate (fun ls ->
            let ev = ls |> List.find (fun (_, v) -> match v with IRReleasePools _ -> true | _ -> false)
            let start = match snd ev with IRReleasePools (_, s, _) -> s | _ -> -1
            let rest = ls |> List.filter (fun x -> not (obj.ReferenceEquals(x, ev)))
            rest |> List.collect (fun (id, v) -> if id = start then [ (id, v); ev ] else [ (id, v) ]))
    // (2) the event names a caller binding (x, bound before the inline)
    let foreign =
        mutate (fun ls ->
            let firstId = fst ls.Head
            ls |> List.map (fun (id, v) ->
                match v with
                | IRReleasePools (c, s, pools) -> (id, IRReleasePools (c, s, (firstId, IRTUnit) :: pools))
                | _ -> (id, v)))
    let msgs (p: IRProgram) = match Blade.IRValidate.validateIR p with Ok _ -> [] | Error ms -> ms
    let m1, m2 = msgs early, msgs foreign
    if ok0 && (m1 |> List.exists (fun m -> m.Contains "referenced after its release"))
       && (m2 |> List.exists (fun m -> m.Contains "not bound inside the inline it closes")) then
        resultLine Pass name "the lowered program validates; a release moved before a read and a release naming caller storage are both rejected"
        true
    else
        resultLine Fail name (sprintf "ok0=%b early=%A foreign=%A" ok0 m1 m2)
        false
/// Round 5 (fourth review NEW 1, 3): the fail-closed declines. 036 / 037 --
/// a callee holding an array rename (`let spare = x`, an unlisted LLVM copy)
/// is not inlined for the legs, in main and in a failing function caller;
/// 039 -- two inlinable calls in one statement keep both calls. Each: the
/// decline record, the call kept in C++, no `__epl_` leg, and no A7c release
/// in the LLVM module. Round 7 (sixth review NEW 1-3): 044 / 045 / 046 -- a
/// user call in a non-owned unit (a tuple component, a failing caller, a
/// scalar let) -- and 048 -- a String literal inside a Float64 let -- decline
/// too; and each fixture pins its LLVM ROUTE, which A7c's decline leaves as
/// it was: a call that is a direct TUPLE COMPONENT of a top-level statement
/// expands in place in main (no call instruction, no pool scope: 036, 040,
/// 042, 044, 046, 048), while a SCALAR-LET call or an ARITHMETIC OPERAND is
/// OUTLINED and frees its pools at its own return (037 / 041 / 045 `kernel`
/// in bl_driver, 039 `ka` / `kb` in main, 046's inner `extra` in main).
/// Array parameters and totality do not select the route.
let private eplFailClosedDeclines () =
    let name = "epilogue_legs_round5_fail_closed_declines"
    let rx (p: string) (t: string) = System.Text.RegularExpressions.Regex.IsMatch(t, p)
    let llvmFn (ll: string) (fn: string) =
        let lines = ll.Replace("\r\n", "\n").Split('\n')
        match lines |> Array.tryFindIndex (fun l -> l.StartsWith "define " && rx ("@" + fn + @"(_\d+)?\(") l) with
        | None -> ""
        | Some st -> String.concat "\n" (lines.[st .. (lines |> Array.skip st |> Array.findIndex (fun l -> l = "}")) + st])
    let cppFn (cpp: string) (fn: string) =
        if fn = "main" then mainBody cpp
        else
            let d = cpp.IndexOf $"double {fn}(Array<double, 1> x, Array<double, 1> y) {{"
            if d < 0 then "" else cpp.Substring(d, cpp.IndexOf("\n}\n", d) - d)
    // (file, decline needle, the caller function, the callees that must stay
    // calls, the LLVM route: the callees that are CALL instructions in the
    // caller's LLVM body -- outlined -- and the ones expanded in place there)
    let one (rel: string) (needle: string) (caller: string) (calls: string list) (llOutlined: string list) (llExpanded: string list) =
        match corpusSource rel with
        | Error e -> Error e
        | Ok src ->
        let src = src.Substring(src.IndexOf '\n' + 1)
        let serial f = withGate "BLADE_FP_REPRO" "1" (fun () -> withGate "BLADE_OMP_THREADS" "1" f)
        let offCpp = withGate "BLADE_EPILOGUE_LEGS" "0" (fun () -> serial (fun () -> cppOfSource name src))
        let offLl = serial (fun () -> eplLlvmOf src "0")
        match serial (fun () -> cepDecisionsOf src), serial (fun () -> cppOfSource name src), serial (fun () -> eplLlvmOf src null) with
        | Ok ds, Ok cpp, Ok (ll, lds) ->
            let mine = ds |> List.filter (fun d -> d.Rule = "epilogue-legs")
            let declines = mine |> List.filter (declinedMentioning needle)
            let cBody, lBody = cppFn cpp caller, llvmFn ll (if caller = "main" then "main" else "bl_" + caller)
            let llvmApplied = lds |> List.exists (fun d -> d.Rule = "epilogue-legs" && applied d)
            if declines.Length < calls.Length then Error $"{rel}: {declines.Length} decline(s) mentioning `{needle}`, wanted one per call ({calls.Length})"
            elif calls |> List.exists (fun c -> not (declines |> List.exists (fun d -> match d.Outcome with Blade.Effects.Declined w -> w.Contains (c + "(") || calls.Length = 1 | _ -> false))) then
                Error $"{rel}: not every call has its own decline"
            elif mine |> List.exists applied || llvmApplied then Error $"{rel}: an epilogue-legs plan was applied"
            elif cpp.Contains "__epl_" then Error $"{rel}: a C++ leg was emitted"
            elif ll.Contains "; A7c:" then Error $"{rel}: the LLVM module releases an inline's pool"
            elif cBody = "" || lBody = "" then Error $"{rel}: caller {caller} not found in both lanes"
            // A DECLINE changes no emission: each lane's gate-on text equals its
            // gate-off text (the LLVM lane's own call handling -- an outlined call
            // or its in-place expansion -- is whatever it was without A7c).
            elif (match offCpp with Ok c -> c <> cpp | Error _ -> true) then Error $"{rel}: the C++ emission differs from BLADE_EPILOGUE_LEGS=0"
            elif (match offLl with Ok (l, _) -> l <> ll | Error _ -> true) then Error $"{rel}: the LLVM emission differs from BLADE_EPILOGUE_LEGS=0"
            else
                // The LLVM route, pinned per fixture (the byte identity above says
                // A7c's decline did not change it): an outlined callee is a call
                // instruction in the caller's body; an expanded one is neither
                // called there nor defined in the module.
                let llCalls (f: string) = rx (@"call \w+ @bl_" + f + @"(_\d+)?\(") lBody
                let llDefines (f: string) = rx (@"(?m)^define [^@]*@bl_" + f + @"(_\d+)?\(") ll
                match calls |> List.tryFind (fun c -> not (rx (System.Text.RegularExpressions.Regex.Escape c + @"\(") cBody)) with
                | Some c -> Error $"{rel}: {caller} does not keep the C++ call {c}(..)"
                | None ->
                match llOutlined |> List.tryFind (llCalls >> not) with
                | Some c -> Error $"{rel}: the LLVM {caller} does not call the outlined {c}"
                | None ->
                match llExpanded |> List.tryFind (fun c -> llCalls c || llDefines c) with
                | Some c -> Error $"{rel}: the LLVM lane does not expand {c} in place in {caller}"
                | None -> Ok ()
        | Error e, _, _ | _, Error e, _ | _, _, Error e -> Error $"{rel}: {e}"
    let own = "not an owned allocation form"
    let res =
        [ one "sibling-folds/036_input_legs_decline_alias_let_in_callee.blade" own "main" [ "kernel" ] [] [ "kernel" ]
          one "sibling-folds/037_input_legs_decline_alias_function_caller_aborts.blade" own "driver" [ "kernel" ] [ "kernel" ] []
          one "sibling-folds/039_input_legs_decline_two_calls_one_statement.blade" "holds another inlinable call" "main" [ "ka"; "kb" ] [ "ka"; "kb" ] []
          one "sibling-folds/040_input_legs_decline_nested_alias_in_branch.blade" "evaluates a nested let" "main" [ "kernel" ] [] [ "kernel" ]
          one "sibling-folds/041_input_legs_decline_nested_alias_function_caller_aborts.blade" "evaluates a nested let" "driver" [ "kernel" ] [ "kernel" ] []
          one "sibling-folds/042_input_legs_decline_string_let.blade" own "main" [ "kernel" ] [] [ "kernel" ]
          one "sibling-folds/044_input_legs_decline_user_call_tuple_position.blade" "calls extra" "main" [ "kernel" ] [] [ "kernel"; "extra" ]
          one "sibling-folds/045_input_legs_decline_user_call_function_caller_aborts.blade" "calls extra" "driver" [ "kernel" ] [ "kernel" ] []
          one "sibling-folds/046_input_legs_decline_user_call_scalar_let.blade" "calls extra" "main" [ "kernel" ] [ "extra" ] [ "kernel" ]
          one "sibling-folds/048_input_legs_decline_string_literal_in_projection.blade" "evaluates a String literal" "main" [ "kernel" ] [] [ "kernel" ] ]
    match res |> List.tryPick (function Error e -> Some e | Ok () -> None) with
    | Some e -> resultLine Fail name e; false
    | None -> resultLine Pass name "036 / 037 (a rename), 040 / 041 (a rename inside a scalar branch), 042 (a String let), 039 (two calls, one decline each), 044 / 045 / 046 (a user call: tuple position, failing caller, scalar let), 048 (a String literal in a Float64 let): each declines; both lanes emit byte-identically to BLADE_EPILOGUE_LEGS=0; the C++ caller keeps the call; LLVM routes: kernel expanded in place in main for 036 / 040 / 042 / 044 / 046 / 048 (044's extra too), kernel outlined in bl_driver for 037 / 041 / 045, ka / kb outlined in main for 039, extra outlined in main for 046; no leg, no LLVM plan, no inline release"; true

/// Round 6 (fifth review NEW 3): an A7c ownership decline leaves the sibling
/// pass's own join-driven inlining unchanged (043: the callee still inlined
/// for its Jx join, no return-boundary event, no A7c plan in either lane).
let private eplDeclineKeepsSiblingInline () =
    let name = "epilogue_legs_decline_keeps_sibling_inline"
    match corpusSource "sibling-folds/043_input_legs_decline_keeps_sibling_inline.blade" with
    | Error e -> resultLine Fail name e; false
    | Ok src ->
    let src = src.Substring(src.IndexOf '\n' + 1)
    let serial f = withGate "BLADE_FP_REPRO" "1" (fun () -> withGate "BLADE_OMP_THREADS" "1" f)
    let offCpp = withGate "BLADE_EPILOGUE_LEGS" "0" (fun () -> serial (fun () -> cppOfSource name src))
    let offLl = serial (fun () -> eplLlvmOf src "0")
    match serial (fun () -> cepDecisionsOf src), serial (fun () -> cppOfSource name src), serial (fun () -> eplLlvmOf src null), serial (fun () -> lower src) with
    | Ok ds, Ok cpp, Ok (ll, lds), Ok ir ->
        let a7c = ds |> List.exists (fun d -> d.Rule = "epilogue-legs" && declinedMentioning "the call is not retained for A7c (independent sibling-fold inlining is unchanged)" d)
        let w1d = ds |> List.exists (fun d -> d.Rule = "sibling-folds" && applied d && evidenceMentions "kernel(x, y, u, v) inlined" d)
        let noApplied = not ((ds @ lds) |> List.exists (fun d -> d.Rule = "epilogue-legs" && applied d))
        let noCall = not ((mainBody cpp).Contains "kernel(")
        let mutable events = 0
        for m in ir.Modules do
            for b in m.Bindings do iterIRExpr (fun n -> match n with IRReleasePools _ -> events <- events + 1 | _ -> ()) b.Value
        // the decline changes no emission in either lane: the retained W1-D
        // inline (and the alias copy the LLVM lane makes for it) is the
        // pre-existing sibling-fold behaviour, identical with A7c off
        let sameCpp = match offCpp with Ok c -> c = cpp | Error _ -> false
        let sameLl = match offLl with Ok (l, _) -> l = ll | Error _ -> false
        if a7c && w1d && noApplied && noCall && events = 0 && sameCpp && sameLl && not (ll.Contains "; A7c:") && not (cpp.Contains "__epl_") then
            resultLine Pass name "A7c declines with the narrowed wording; no epilogue-legs plan applied in either lane; the sibling pass still inlines kernel for its Jx join; both lanes byte-identical to BLADE_EPILOGUE_LEGS=0; no event, no leg, no inline release"
            true
        else resultLine Fail name (sprintf "a7c %b w1d %b noApplied %b noCall %b events %d sameCpp %b sameLl %b" a7c w1d noApplied noCall events sameCpp sameLl); false
    | Error e, _, _, _ | _, Error e, _, _ | _, _, Error e, _ | _, _, _, Error e -> resultLine Fail name e; false

/// The BL7005 warnings the last emission raised (and clear the channel).
let private bl7005Raised () =
    Blade.Diagnostics.EmitterWarnings.drain () |> List.filter (fun d -> d.Code = "BL7005")

/// W2-4 round 9 (the user's decision): every RETAINED kept inline warns
/// BL7005 ONCE per lane, spanned at its call, in both lanes -- including when
/// the lane's emitter declines the legs' fused form and the planned inline
/// stays (030 under omp test mode); a program with no retained inline (a
/// decline, 047's never-inlined callee) raises none. Runs under the explicit
/// arm (the runner's BLADE_EPILOGUE_LEGS=1); the default build's absence is
/// `epilogue_legs_default_build_declines_kept_inline`.
let private eplKeptInlineWarns () =
    let name = "epilogue_legs_kept_inline_warns_bl7005_both_lanes"
    let serial f = withGate "BLADE_FP_REPRO" "1" (fun () -> withGate "BLADE_OMP_THREADS" "1" f)
    let prefix = "experimental: the kept one-level inline (BLADE_EPILOGUE_LEGS=1) of "
    let tail = " has an INCOMPLETE lifetime audit (G69: allocations made by the kernels it names may survive its return); the default build declines it"
    let src (rel: string) = match corpusSource rel with Ok s -> Ok (s.Substring(s.IndexOf '\n' + 1)) | Error e -> Error e
    let cppWarn (testMode: bool) (s: string) =
        serial (fun () ->
            Blade.CodeGenState.setOmpTestMode testMode
            try (match cppOfSource name s with Ok _ -> Ok (bl7005Raised ()) | Error e -> Error e)
            finally Blade.CodeGenState.setOmpTestMode false)
    let llWarn (s: string) =
        serial (fun () ->
            match lower s with
            | Error e -> Error e
            | Ok ir ->
                let r = (try EmitLlvm.tryEmitProgramNamed "bl7005" ir with ex -> Error ("raised: " + ex.Message))
                Ok (r.IsOk, bl7005Raised ()))
    let wellFormed (calls: string list) (ws: Blade.Diagnostics.Diagnostic list) =
        ws.Length = calls.Length
        && List.forall2 (fun (c: string) (d: Blade.Diagnostics.Diagnostic) ->
               d.Severity = Blade.Diagnostics.SevWarning && d.Message = prefix + c + tail && d.Span.StartLine > 0) calls ws
    // (fixture, source, the calls retained in order)
    let cases =
        [ ("saxpy", Ok eplSaxpySrc, [ "kernel(x, y)" ])
          ("019", src "sibling-folds/019_input_legs_ride_producer.blade", [ "kernel(x, y)" ])
          ("030", src "sibling-folds/030_input_legs_join_inside_inlined_callee.blade", [])
          ("031", src "sibling-folds/031_input_legs_two_plans_one_inlined_call.blade", [])
          ("032", src "sibling-folds/032_input_legs_unused_final_join_atom_return.blade", [])
          ("033", src "sibling-folds/033_input_legs_cse_barrier_function_caller.blade", [ "kernel(x, y)" ])
          ("034", src "sibling-folds/034_input_legs_function_caller_failure_after_return.blade", [])
          ("035", src "sibling-folds/035_input_legs_scratch_reuse_inside_callee.blade", [])
          ("038", src "sibling-folds/038_input_legs_durable_region_start.blade", [ "kernel(x, y, u, v)"; "kernel2(x, y)" ])
          ("036", src "sibling-folds/036_input_legs_decline_alias_let_in_callee.blade", [])
          ("044", src "sibling-folds/044_input_legs_decline_user_call_tuple_position.blade", [])
          ("047", src "sibling-folds/047_input_legs_label_read_not_inlined.blade", []) ]
    // the retained-call count where the call text is not pinned above
    let counts = Map.ofList [ ("030", 2); ("031", 1); ("032", 1); ("034", 1); ("035", 1) ]
    let problems =
        cases |> List.choose (fun (fx, s, calls) ->
            match s with
            | Error e -> Some $"{fx}: {e}"
            | Ok s ->
            let n = match Map.tryFind fx counts with Some n -> n | None -> calls.Length
            let check (ws: Blade.Diagnostics.Diagnostic list) =
                if calls.IsEmpty then ws.Length = n && ws |> List.forall (fun d -> d.Message.StartsWith prefix && d.Message.EndsWith tail && d.Span.StartLine > 0)
                else wellFormed calls ws
            match cppWarn false s, llWarn s with
            | Ok cw, Ok (llOk, lw) ->
                if not (check cw) then Some $"{fx}: C++ raised {cw.Length} BL7005 (wanted {n}): %A{cw |> List.map (fun d -> d.Message, d.Span.StartLine)}"
                elif llOk && not (check lw) then Some $"{fx}: LLVM raised {lw.Length} BL7005 (wanted {n})"
                elif not llOk && not lw.IsEmpty then Some $"{fx}: the LLVM lane refused the program but kept {lw.Length} BL7005"
                elif n > 0 && not llOk then Some $"{fx}: the LLVM lane refused a retaining program"
                else None
            | Error e, _ | _, Error e -> Some $"{fx}: {e}")
    // the emitter declines the fused form (omp test mode) and the inline stays
    let testMode =
        match src "sibling-folds/030_input_legs_join_inside_inlined_callee.blade" with
        | Error e -> Some e
        | Ok s ->
            match cppWarn true s with
            | Ok ws when ws.Length = 2 -> None
            | Ok ws -> Some $"030 under omp test mode: {ws.Length} BL7005 (wanted 2: the planned inlines stay)"
            | Error e -> Some e
    match problems @ Option.toList testMode with
    | [] ->
        resultLine Pass name "one BL7005 per retained inline per lane, spanned at its call, exact text: saxpy / 019 / 031-035 one, 030 / 038 two (030 also under omp test mode, the fused form declined and the inline kept), both lanes; 036 / 044 (declines) and 047 (never inlined; LLVM refusal) none"
        true
    | ps -> resultLine Fail name (String.concat " | " ps); false
/// W2-4 round 8 (the user's decision after the seventh look): in the DEFAULT
/// build (BLADE_EPILOGUE_LEGS unset) the kept one-level inline is OPT-IN, so
/// the saxpy spelling (the producer in `kernel`, the legs in the caller)
/// DECLINES with the opt-in text, keeps its call, mints no return-boundary
/// event, and emits byte-identically to BLADE_EPILOGUE_LEGS=0 in both lanes;
/// a plan whose producer is already in the region (019's second region)
/// still applies by default.
let private eplDefaultDeclinesInline () =
    let name = "epilogue_legs_default_build_declines_kept_inline"
    let serial f = withGate "BLADE_FP_REPRO" "1" (fun () -> withGate "BLADE_OMP_THREADS" "1" f)
    let dflt f = withGate "BLADE_EPILOGUE_LEGS" null (fun () -> serial f)
    let off f = withGate "BLADE_EPILOGUE_LEGS" "0" (fun () -> serial f)
    let llvmOf (src: string) =
        Blade.Effects.Decisions.start ()
        let r =
            match lower src with
            | Error e -> Error e
            | Ok ir -> (try EmitLlvm.tryEmitProgramNamed "epl_ll" ir with ex -> Error ("raised: " + ex.Message))
        let ds = Blade.Effects.Decisions.drain ()
        r |> Result.map (fun ll -> (ll, ds))
    let want = Blade.Optimize.epilogueInlineOptInDecline
    // BL7005 is ABSENT in the default build: nothing is retained, in either lane
    let mutable warned = -1
    let withWarn (r: Result<'a, string>) = (match r with Ok _ -> warned <- max warned 0 + (bl7005Raised ()).Length | Error _ -> ()); r
    match dflt (fun () -> cepDecisionsOf eplSaxpySrc), dflt (fun () -> cppOfSource name eplSaxpySrc |> withWarn), off (fun () -> cppOfSource name eplSaxpySrc),
          dflt (fun () -> llvmOf eplSaxpySrc |> withWarn), off (fun () -> llvmOf eplSaxpySrc), dflt (fun () -> lower eplSaxpySrc) with
    | Ok ds, Ok cpp, Ok cppOff, Ok (ll, lds), Ok (llOff, _), Ok ir ->
        let mine = ds |> List.filter (fun d -> d.Rule = "epilogue-legs")
        let exact = mine |> List.exists (fun d -> match d.Outcome with Blade.Effects.Declined w -> w = want | _ -> false)
        let anyApplied = (mine @ lds) |> List.exists (fun d -> d.Rule = "epilogue-legs" && applied d)
        let mutable events = 0
        for m in ir.Modules do
            for b in m.Bindings do iterIRExpr (fun n -> match n with IRReleasePools _ -> events <- events + 1 | _ -> ()) b.Value
            for fn in m.Functions do iterIRExpr (fun n -> match n with IRReleasePools _ -> events <- events + 1 | _ -> ()) fn.Body
        let kept = (mainBody cpp).Contains "kernel("
        // 019's second region holds its producer itself (no inline): applied by default
        let second =
            match corpusSource "sibling-folds/019_input_legs_ride_producer.blade" with
            | Error _ -> None
            | Ok s ->
                match dflt (fun () -> cepDecisionsOf (s.Substring(s.IndexOf '\n' + 1))) with
                | Ok ds2 ->
                    Some (ds2 |> List.exists (fun d -> d.Rule = "epilogue-legs" && applied d && d.Subject.Contains "s2"),
                          ds2 |> List.exists (fun d -> d.Rule = "epilogue-legs" && d.Subject.Contains "checksum" && (match d.Outcome with Blade.Effects.Declined w -> w = want | _ -> false)))
                | Error _ -> None
        if exact && not anyApplied && events = 0 && kept && cpp = cppOff && ll = llOff && second = Some (true, true) && warned = 0 then
            resultLine Pass name ($"saxpy declines `{want}`; the call stays; no event; no BL7005 in either lane; C++ and LLVM byte-identical to BLADE_EPILOGUE_LEGS=0; 019's in-region plan (s2, t2) still applies by default and its callee region declines the same way")
            true
        else resultLine Fail name (sprintf "exact %b applied %b events %d kept %b cpp= %b ll= %b 019 %A bl7005 %d" exact anyApplied events kept (cpp = cppOff) (ll = llOff) second warned); false
    | Error e, _, _, _, _, _ | _, Error e, _, _, _, _ | _, _, Error e, _, _, _ | _, _, _, Error e, _, _ | _, _, _, _, Error e, _ | _, _, _, _, _, Error e -> resultLine Fail name e; false
/// Round 7 (sixth review NEW 2): 047's callee reads a String-valued EnumIdx
/// label. The predicate would refuse it (a tag is judged by its inner type),
/// but the sibling pass's totality whitelist keeps the call a call first: no
/// epilogue-legs record at all, the C++ main keeps `kernel(..)` and emits
/// byte-identically to BLADE_EPILOGUE_LEGS=0, and the LLVM lane's REFUSAL is
/// identical in both gate states (or the same module, if it ever emits one).
let private eplLabelReadNotInlined () =
    let name = "epilogue_legs_string_label_read_not_inlined"
    match corpusSource "sibling-folds/047_input_legs_label_read_not_inlined.blade" with
    | Error e -> resultLine Fail name e; false
    | Ok src ->
    let src = src.Substring(src.IndexOf '\n' + 1)
    let serial f = withGate "BLADE_FP_REPRO" "1" (fun () -> withGate "BLADE_OMP_THREADS" "1" f)
    let offCpp = withGate "BLADE_EPILOGUE_LEGS" "0" (fun () -> serial (fun () -> cppOfSource name src))
    let llOn, llOff = serial (fun () -> eplLlvmOf src null), serial (fun () -> eplLlvmOf src "0")
    match serial (fun () -> cepDecisionsOf src), serial (fun () -> cppOfSource name src) with
    | Ok ds, Ok cpp ->
        let none = not (ds |> List.exists (fun d -> d.Rule = "epilogue-legs"))
        let kept = (mainBody cpp).Contains "kernel("
        let sameCpp = match offCpp with Ok c -> c = cpp | Error _ -> false
        let sameLl =
            match llOn, llOff with
            | Ok (a, _), Ok (b, _) -> a = b
            | Error a, Error b -> a = b
            | _ -> false
        if none && kept && sameCpp && sameLl then
            resultLine Pass name "no epilogue-legs record (the label-reading callee is not inlined by the sibling pass); C++ emission identical to BLADE_EPILOGUE_LEGS=0 and the call retained; LLVM: the lane's refusal identical in both gate states (or identical modules, if the lane ever supports it) -- not an emitted-equality witness"
            true
        else resultLine Fail name (sprintf "none %b kept %b sameCpp %b sameLl %b" none kept sameCpp sameLl); false
    | Error e, _ | _, Error e -> resultLine Fail name e; false

/// Round 7 (sixth review NEW 2): the allocation-free VALUE predicate judges an
/// index tag by its inner type, recurses through nested tuples, and admits no
/// String and no bare Nat (the LLVM lane's scalar map has no Nat arm).
let private eplValueTypePredicate () =
    let name = "epilogue_legs_allocation_free_value_type_predicate"
    let tag = IRefNamed "RegionIdx"
    let s = IRTScalar ETString
    let cases =
        [ "Float64", IRTScalar ETFloat64, true
          "Int64-tagged label", IRTIdxTagged (IRTScalar ETInt64, tag), true
          "String-tagged label", IRTIdxTagged (s, tag), false
          "String", s, false
          "nested numeric tuple", IRTTuple [ IRTScalar ETFloat64; IRTTuple [ IRTScalar ETInt64; IRTScalar ETBool ] ], true
          "nested tuple with a String-tagged label", IRTTuple [ IRTScalar ETFloat64; IRTTuple [ IRTScalar ETInt64; IRTIdxTagged (s, tag) ] ], false
          "bare Nat", IRTNat (Some 3), false ]
    match cases |> List.tryFind (fun (_, t, want) -> Blade.Optimize.allocationFreeValueType t <> want) with
    | Some (what, _, want) -> resultLine Fail name $"{what}: wanted {want}"; false
    | None -> resultLine Pass name "Float64, an Int64 label and nested numeric tuples admitted; a String, a String-tagged label (bare or nested in a tuple) and bare Nat refused"; true
/// Round 5 (NEW 2): 038's two admitted regions -- the callee's first let a
/// join the rebuild drops, and a first scalar builtin let CSE removes --
/// validate (the region start is the first owned pool) and release once.
let private eplDurableStart () =
    let name = "epilogue_legs_round5_durable_region_start"
    match corpusSource "sibling-folds/038_input_legs_durable_region_start.blade" with
    | Error e -> resultLine Fail name e; false
    | Ok src ->
    let src = src.Substring(src.IndexOf '\n' + 1)
    let serial f = withGate "BLADE_FP_REPRO" "1" (fun () -> withGate "BLADE_OMP_THREADS" "1" f)
    match serial (fun () -> lower src), serial (fun () -> cepDecisionsOf src), serial (fun () -> cppOfSource name src) with
    | Ok ir, Ok ds, Ok cpp ->
        let valid = match Blade.IRValidate.validateIR ir with Ok _ -> None | Error ms -> Some (String.concat " | " ms)
        let applied2 = ds |> List.filter (fun d -> d.Rule = "epilogue-legs" && applied d && not (evidenceMentions "C++ lane" d)) |> List.length
        let cse = ds |> List.exists (fun d -> d.Rule = "cse" && applied d)
        let d = cpp.IndexOf "double driver(Array<double, 1> x, Array<double, 1> y) {"
        let dBody = if d < 0 then "" else cpp.Substring(d, cpp.IndexOf("\n}\n", d) - d)
        let llvmR =
            match eplLlvmEmit src with
            | Error e -> Error e
            | Ok (ll, lds) ->
                let placed = lds |> List.filter (fun d -> d.Rule = "epilogue-legs" && applied d && evidenceMentions "llvm lane: legs x-sum, y-sum folded inside" d) |> List.length
                match llvmReleasesSafe ll "main", llvmReleasesSafe ll "bl_driver" with
                | Ok 1, Ok 1 when placed = 2 -> Ok ()
                | m, dr -> Error (sprintf "llvm main %A, bl_driver %A, placed %d" m dr placed)
        match valid, releasesSafe (mainBody cpp), releasesSafe dBody, llvmR with
        | None, Ok [ _ ], Ok [ _ ], Ok () when applied2 = 2 && cse ->
            resultLine Pass name "both plans admitted (first join dropped; first builtin let CSE-removed); the program validates; C++ main and driver release once; the LLVM lane places both legs plans and releases once in main and once in bl_driver (slot nulled)"
            true
        | v, m, dr, l -> resultLine Fail name (sprintf "validate %A; main %A; driver %A; llvm %A; applied %d; cse %b" v m dr l applied2 cse); false
    | Error e, _, _ | _, Error e, _ | _, _, Error e -> resultLine Fail name e; false

/// 033's true duplicate return: kernel's `sqrt(s)` return is a fresh let
/// structurally equal to its earlier `let r = sqrt(s)` (a builtin since round
/// 7: a user call in the callee declines the kept inline); inlined into
/// driver with CSE on, BOTH lets survive (the return let before the event is
/// pinned).
let private eplDuplicateReturnPinned () =
    let name = "epilogue_legs_duplicate_return_let_pinned"
    match corpusSource "sibling-folds/033_input_legs_cse_barrier_function_caller.blade" with
    | Error e -> resultLine Fail name e; false
    | Ok src ->
    let src = src.Substring(src.IndexOf '\n' + 1)
    match withGate "BLADE_FP_REPRO" "1" (fun () -> withGate "BLADE_OMP_THREADS" "1" (fun () -> lower src)) with
    | Error e -> resultLine Fail name e; false
    | Ok ir ->
        let rec unroll (e: IRExpr) = match e with IRLet (id, v, b) -> let (ls, r) = unroll b in ((id, v) :: ls, r) | _ -> ([], e)
        let driver = ir.Modules |> List.collect (fun m -> m.Functions) |> List.tryFind (fun f -> f.SourceName = "driver")
        match driver with
        | None -> resultLine Fail name "no driver"; false
        | Some f ->
            let (ls, _) = unroll f.Body
            let arr = List.toArray ls
            match arr |> Array.tryFindIndex (fun (_, v) -> match v with IRReleasePools _ -> true | _ -> false) with
            | Some ei when ei > 0 ->
                let (_, retV) = arr.[ei - 1]
                // a non-trivial value (not a bare variable or literal) whose
                // structural twin is an earlier let of the same spine
                let nonLeaf (v: IRExpr) = match v with IRVar _ | IRLit _ | IRParam _ -> false | _ -> true
                let earlierTwin = arr |> Array.take (ei - 1) |> Array.exists (fun (_, v) -> nonLeaf v && v = retV)
                if nonLeaf retV && earlierTwin then
                    resultLine Pass name "the event's preceding let (the fresh return sqrt(s)) survives CSE beside its earlier twin `r`"
                    true
                else resultLine Fail name (sprintf "return let %A; twin %b" retV earlierTwin); false
            | _ -> resultLine Fail name "no event with a preceding let in driver"; false

/// Round 5 (NEW 4): IRValidate's one-level provenance -- a deferred map, a
/// tuple alias and a mut-call result naming a released pool, read after the
/// release, are each rejected (hand-built on the lowered saxpy shape).
let private eplValidateProvenance () =
    let name = "epilogue_legs_irvalidate_one_level_provenance"
    match withGate "BLADE_FP_REPRO" "1" (fun () -> withGate "BLADE_OMP_THREADS" "1" (fun () -> lower eplSaxpySrc)) with
    | Error e -> resultLine Fail name e; false
    | Ok ir ->
    let rec unroll (e: IRExpr) = match e with IRLet (id, v, b) -> let (ls, r) = unroll b in ((id, v) :: ls, r) | _ -> ([], e)
    let refold (ls: (IRId * IRExpr) list) (r: IRExpr) = List.foldBack (fun (id, v) acc -> IRLet (id, v, acc)) ls r
    let anyFn = ir.Modules |> List.collect (fun m -> m.Functions) |> List.head
    let isEvent (v: IRExpr) = match v with IRReleasePools _ -> true | _ -> false
    let mutate (build: (IRId * IRType) -> (IRId * IRExpr) list -> (IRId * IRExpr) list * (IRId * IRExpr) list) =
        let fixB (b: IRBinding) =
            let (ls, r) = unroll b.Value
            match ls |> List.tryFind (snd >> isEvent) with
            | Some ((_, IRReleasePools (_, _, pool :: _)) as ev) ->
                let (before, after) = build pool ls
                let i = ls |> List.findIndex (fun x -> obj.ReferenceEquals(x, ev))
                let pre = ls |> List.take i
                let post = ls |> List.skip (i + 1)
                { b with Value = refold (pre @ before @ [ ev ] @ after @ post) r }
            | _ -> b
        { ir with Modules = ir.Modules |> List.map (fun m -> { m with Bindings = m.Bindings |> List.map fixB }) }
    let deferred =
        mutate (fun (p, pty) ls ->
            let comp =
                ls |> List.pick (fun (_, v) ->
                    match v with
                    | IRReduceCompute ((IRApplyCombinator _ as c), _, _) when Set.contains p (collectVarRefsIR c) -> Some v
                    | _ -> None)
            match comp with
            | IRReduceCompute (c, k, s0) ->
                ([ (990001, c) ], [ (990002, IRReduceCompute (IRVar (990001, typeOf c), k, s0)) ])
            | _ -> ([], []))
    let tupled =
        mutate (fun (p, pty) _ ->
            let tt = IRTuple [ IRVar (p, pty); IRLit (IRLitFloat 1.0) ]
            ([ (990003, tt) ], [ (990004, IRTupleProj (IRVar (990003, typeOf tt), 0, false)) ]))
    let mutArg =
        mutate (fun (p, pty) _ ->
            ([ (990005, IRApp (IRVar (anyFn.Id, IRTUnit), [ IRVar (p, pty) ], pty)) ], [ (990006, IRVar (990005, pty)) ]))
    let msgs (prog: IRProgram) = match Blade.IRValidate.validateIR prog with Ok _ -> [] | Error ms -> ms
    let hit (ms: string list) (alias: int) = ms |> List.exists (fun m -> m.Contains $"__v{alias}, whose definition names a released pool")
    let a, b, c = msgs deferred, msgs tupled, msgs mutArg
    if hit a 990001 && hit b 990003 && hit c 990005 then
        resultLine Pass name "a deferred map, a tuple alias and a mut-call result over a released pool, each read after the release, are rejected"
        true
    else
        resultLine Fail name (sprintf "deferred %A; tuple %A; mut %A" a b c)
        false
/// NEW 2 (second review): two plans riding two producers of ONE inlined call
/// (sibling-folds/031) -- each pool released exactly once; the nested-block
/// region keeps its call.
let private eplTwoPlansOneCall () =
    let name = "epilogue_legs_two_plans_one_call_release_once"
    match corpusSource "sibling-folds/031_input_legs_two_plans_one_inlined_call.blade" with
    | Error e -> resultLine Fail name e; false
    | Ok src ->
    let src = src.Substring(src.IndexOf '\n' + 1)
    match withGate "BLADE_FP_REPRO" "1" (fun () -> withGate "BLADE_OMP_THREADS" "1" (fun () -> withGate "BLADE_POOL_REUSE" "off" (fun () -> cppOfSource name src))) with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        let m = mainBody cpp
        match releasesSafe m with
        | Ok rs when rs.Length = 2 && countOf "__epl_acc_" m >= 4 && System.Text.RegularExpressions.Regex.Matches(m, @"= kernel\(").Count = 1 ->
            resultLine Pass name "y2 and w2 released exactly once each at the shared return boundary (two plans, one call); the nested-block region still calls kernel"
            true
        | r ->
            let legs, calls = countOf "__epl_acc_" m, System.Text.RegularExpressions.Regex.Matches(m, @"= kernel\(").Count
            resultLine Fail name (sprintf "releases %A; legs %d; calls %d" r legs calls); false

/// 029 in the LLVM lane: the throwing callee is not inlined for legs -- no
/// extra accumulator in its fused loop vs gate-off, and its frame is pushed.
let private eplThrowingCalleeLlvm () =
    let name = "epilogue_legs_throwing_callee_llvm_keeps_frame"
    match corpusSource "sibling-folds/029_input_legs_throwing_callee_keeps_frame.blade" with
    | Error e -> resultLine Fail name e; false
    | Ok src ->
    let src = src.Substring(src.IndexOf '\n' + 1)
    match withGate "BLADE_OMP_THREADS" "1" (fun () -> eplLlvmOf src null, eplLlvmOf src "0") with
    | Ok (on, ds), Ok (off, _) ->
        let (aOn, _, _) = llvmFusedLoop on
        let (aOff, _, _) = llvmFusedLoop off
        let frame = on.Contains "c\"kernel\\00\"" && on.Contains "@blade_frame_stack"
        let noPlan = ds |> List.forall (fun d -> d.Rule <> "epilogue-legs")
        if aOn = aOff && frame && noPlan then
            resultLine Pass name ($"no epilogue-legs plan; fused loop accumulators {aOn} == gate-off {aOff}; kernel's frame pushed")
            true
        else
            resultLine Fail name ($"acc on={aOn} off={aOff} frame={frame} noPlan={noPlan}")
            false
    | Error e, _ | _, Error e -> resultLine Fail name e; false
let private eplDecisionCases () =
    let serialRepro f = withGate "BLADE_FP_REPRO" "1" (fun () -> withGate "BLADE_OMP_THREADS" "1" f)
    [ serialRepro (fun () ->
        eplDecisionCase "decision_epilogue_legs_applied" eplSaxpySrc
            (fun d -> applied d && evidenceMentions "legs x-sum, y-sum ride y2's producer pass (strict chains, ascending, own seeds)" d && evidenceMentions "licences: the repro build keeps every leg strict" d
                      && evidenceMentions "kernel(x, y) inlined one level" d && evidenceMentions "producer kernel `y2`" d
                      && evidenceMentions "seeds (x-sum: -0.0, y-sum: -0.0)" d && evidenceMentions "intervening (" d && evidenceMentions "outer kernel (" d)
            "applied with the five-party matrix")
      serialRepro (fun () ->
        eplDecisionCase "decision_epilogue_legs_cpp_applied" eplSaxpySrc
            (fun d -> applied d && evidenceMentions "C++ lane: legs x-sum, y-sum fed inside `y2`'s producer loop" d)
            "the C++ lane's applied record")
      // producer row: a threaded producer (mt8) keeps the strict legs serial
      withGate "BLADE_FP_REPRO" "1" (fun () -> withGate "BLADE_OMP_THREADS" null (fun () ->
        eplDecisionCase "decision_epilogue_legs_declined_mt8" eplSaxpySrc
            (declinedMentioning "the producer runs a thread team; the strict legs x-sum, y-sum keep their serial pass")
            "declined: mt8 (strict legs never split per thread)"))
      eplDecisionCase "decision_epilogue_legs_declined_producer_writes"
          (eplBlockSrc "lambda(u, v) -> {\n        cnt = cnt + 1.0\n        u + v\n    }" "" eplSums |> fun s -> s.Replace("    let xa = x\n", "    let mut cnt = 0.0\n"))
          (declinedMentioning "may write (scheduleMayWriteIn)") "declined: the producer kernel writes"
      // leg row: the output, an alias, a team-requesting leg kernel
      eplDecisionCase "decision_epilogue_legs_declined_output_leg" (eplBlockSrc eplPlain "" "reduce(y2, (+)) + reduce(x, (+))")
          (declinedMentioning "folds the producer's OUTPUT") "declined: a leg over the producer's output"
      eplDecisionCase "decision_epilogue_legs_declined_alias" (eplBlockSrc eplPlain "" "reduce(xa, (+)) + reduce(y, (+))")
          (declinedMentioning "operands (an input leg reads exactly the producer's operand") "declined: a leg over an alias"
      withGate "BLADE_OMP_THREADS" null (fun () ->
        eplDecisionCase "decision_epilogue_legs_declined_omp_leg" (eplBlockSrc eplPlain "" "reduce(x, lambda(p, q) where omp -> p + q, 0.0) + reduce(y, lambda(p, q) where omp -> p + q, 0.0)")
            (declinedMentioning "requests omp in a build that emits threads") "declined: a leg would lose its thread team")
      // seed row: a capture and a seed bound after the producer
      eplDecisionCase "decision_epilogue_legs_declined_late_capture"
          (eplBlockSrc eplPlain "    let c = x((1 : Ix)) + 1.0\n" "reduce(x, lambda(a, b) -> a + c * b, 0.0) + reduce(y, lambda(a, b) -> a + c * b, 0.0)")
          (declinedMentioning "fold kernel reads") "declined: a leg capture bound after the producer"
      eplDecisionCase "decision_epilogue_legs_declined_late_seed"
          (eplBlockSrc eplPlain "    let s0 = x((2 : Ix))\n" "reduce(x, (+), s0) + reduce(y, (+), s0)")
          (declinedMentioning "seed reads") "declined: a seed bound after the producer"
      // intervening row: a write to x between the producer and the legs
      eplDecisionCase "decision_epilogue_legs_declined_write_between"
          ((eplBlockSrc eplPlain "    let z = bump(x)\n" "reduce(x, (+)) + reduce(y, (+)) + z")
              .Replace("    let x = inputs(7)\n", "    let mut x = inputs(7)\n")
              .Replace("let (s, t) = {\n", "function bump(a: mut Array<Float64 like Ix>) -> Float64 = {\n    a((0 : Ix)) = 50.0\n    0.0\n}\nlet (s, t) = {\n"))
          (declinedMentioning "may write between the producer and the legs") "declined: an intervening write"
      // the gate
      serialRepro (fun () ->
        withGate "BLADE_EPILOGUE_LEGS" "0" (fun () ->
            eplDecisionCase "decision_epilogue_legs_declined_gate" eplSaxpySrc
                (fun d -> declinedMentioning "disabled by BLADE_EPILOGUE_LEGS" d && evidenceMentions "producer kernel `y2`" d)
                "declined: disabled by BLADE_EPILOGUE_LEGS, with the matrix")) ]

/// One decision per refusal reason (and the applied forms).
let private cepDecisionCases () =
    [ withGate "BLADE_OMP_THREADS" "1" (fun () ->
        cepDecisionCase "decision_cell_epilogue_applied_lanes" cepSaxpySrc
            (fun d -> applied d && d.Evidence |> List.exists (fun e -> e.Contains "K-lanes(8) self-dot stream fed behind")) "applied: the serial P1 lane state")
      withGate "BLADE_OMP_THREADS" null (fun () ->
        cepDecisionCase "decision_cell_epilogue_applied_chunks" cepSaxpySrc
            (fun d -> applied d && d.Evidence |> List.exists (fun e -> e.Contains "per Path B chunk")) "applied: P1 per Path B chunk")
      withGate "BLADE_FP_REPRO" "1" (fun () -> withGate "BLADE_OMP_THREADS" "1" (fun () ->
        cepDecisionCase "decision_cell_epilogue_applied_chain" cepSaxpySrc
            (fun d -> applied d && d.Evidence |> List.exists (fun e -> e.Contains "chain interleave")) "applied: the P2 chain"))
      withGate "BLADE_OMP_THREADS" null (fun () ->
        cepDecisionCase "decision_cell_epilogue_declined_lost_team" cepLostTeamSrc
            (declinedMentioning "would lose its team") "declined: the fold would lose its Path B team")
      withGate "BLADE_OMP_THREADS" null (fun () ->
        cepDecisionCase "decision_cell_epilogue_declined_serializes_producer" cepSerialFoldSrc
            (declinedMentioning "would serialize the producer") "declined: the producer is threaded, the fold serial")
      withGate "BLADE_FP_REPRO" "1" (fun () -> withGate "BLADE_OMP_THREADS" null (fun () ->
        cepDecisionCase "decision_cell_epilogue_declined_mt8_repro" cepSaxpySrc
            (declinedMentioning "one ordered chain behind a threaded producer") "declined: mt8 repro"))
      cepDecisionCase "decision_cell_epilogue_declined_beyond_cell" cepBeyondSrc
          (declinedMentioning "beyond its own cell") "declined: the cell kernel reads beyond its cell"
      cepDecisionCase "decision_cell_epilogue_declined_between" cepBetweenSrc
          (declinedMentioning "between the producer and the fold") "declined: a reader between producer and fold"
      cepDecisionCase "decision_cell_epilogue_declined_cell_term" cepTermSrc
          (declinedMentioning "not `w * w` or `w`") "declined: the cell term is neither w * w nor w"
      withGate "BLADE_FP_REPRO" "1" (fun () -> withGate "BLADE_OMP_THREADS" "1" (fun () ->
        cepDecisionCase "decision_cell_epilogue_declined_seed" cepSeedSrc
            (declinedMentioning "seed is not a literal") "declined: P2 with a non-literal seed"))
      withGate "BLADE_FP_REPRO" "1" (fun () -> withGate "BLADE_OMP_THREADS" "1" (fun () ->
        cepDecisionCase "decision_cell_epilogue_declined_fold_kernel" cepKernelSrc
            (declinedMentioning "not a builtin") "declined: P2 with a non-builtin fold kernel"))
      withGate "BLADE_OMP_THREADS" "1" (fun () ->
        cepDecisionCase "decision_cell_epilogue_declined_licensed_form" cepProductSrc
            (declinedMentioning "does not reproduce") "declined: a licensed fold P1 does not reproduce")
      withGate "BLADE_OMP_THREADS" "1" (fun () ->
        cepDecisionCase "decision_cell_epilogue_declined_not_flat" cepFlatGateSrc
            (declinedMentioning "did not take the flat elementwise path") "declined: the producer took another route")
      withGate "BLADE_CELL_EPILOGUE" "0" (fun () ->
        cepDecisionCase "decision_cell_epilogue_declined_gate" cepSaxpySrc
            (declinedMentioning "disabled by BLADE_CELL_EPILOGUE") "declined: disabled by BLADE_CELL_EPILOGUE")
      // B2r's refusals.
      cepDecisionCase "decision_cell_epilogue_hot_row_licensed" hotRowSrc
          (declinedMentioning "K-lane form on the hot row") "declined: licensed hot-row prodsum (B2l not built)"
      cepDecisionCase "decision_cell_epilogue_recursion_licensed" cepRecurSrc
          (declinedMentioning "K-lane form on the finished row") "declined: licensed recursion prodsum"
      cepDecisionCase "decision_cell_epilogue_row_kernel_not_dot_class" cepRowKernelSrc
          (declinedMentioning "not a +0-seeded dot-class fold") "declined: the row kernel is not prodsum(row, row)"
      withGate "BLADE_FP_REPRO" "1" (fun () ->
        cepDecisionCase "decision_cell_epilogue_hot_row_applied" hotRowSrc
            (fun d -> applied d && d.Evidence |> List.exists (fun e -> e.Contains "row chain interleave")) "applied: the hot-row chain")
      withGate "BLADE_FP_REPRO" "1" (fun () ->
        cepDecisionCase "decision_cell_epilogue_recursion_applied" cepRecurSrc
            (fun d -> applied d && d.Evidence |> List.exists (fun e -> e.Contains "row chain interleave")) "applied: the recursion chain")
      // The hot-row planner's admission (Codex W1-B review, blockers 1-2).
      decisionCase "decision_hot_row_declined_late_capture" hotRowLateCaptureSrc "hot-row-fold"
          (declinedMentioning "bound between the producer and the fold") "declined: the row kernel reads a binding made after the producer"
      decisionCase "decision_hot_row_declined_may_fail" hotRowMayFailSrc "hot-row-fold"
          (declinedMentioning "the row kernel may fail") "declined: a failing row kernel would move ahead of an intervening binding"
      // G41 (Codex re-re-review, finding 14): a WRITING producer -- the row
      // kernel would run between its rows and read G before its later writes
      // (two-pass 12.0, hot-row 9.0; corpus loops/499). Default and repro.
      decisionCase "decision_hot_row_declined_writing_producer" hotRowWritingProducerSrc "hot-row-fold"
          (declinedMentioning "the producer's kernel may write") "declined: the producer writes"
      withGate "BLADE_FP_REPRO" "1" (fun () ->
        decisionCase "decision_hot_row_declined_writing_producer_repro" hotRowWritingProducerSrc "hot-row-fold"
            (declinedMentioning "the producer's kernel may write") "declined under repro too: the producer writes")
      // G41 finding 17: the OUTER fold kernel writes what the row map reads
      // (values pinned by corpus loops/501).
      decisionCase "decision_hot_row_declined_writing_outer_kernel" hotRowWritingOuterKernelSrc "hot-row-fold"
          (declinedMentioning "the outer fold kernel may write") "declined: the outer combine writes"
      withGate "BLADE_FP_REPRO" "1" (fun () ->
        decisionCase "decision_hot_row_declined_writing_outer_kernel_repro" hotRowWritingOuterKernelSrc "hot-row-fold"
            (declinedMentioning "the outer fold kernel may write") "declined under repro too: the outer combine writes")
      // G41 finding 16: compact row classes behind a WRITING producer (values
      // pinned by corpus loops/500).
      decisionCase "decision_compact_row_classes_declined_writing_producer" compactRowWritingProducerSrc "compact-row-classes"
          (declinedMentioning "the producer's kernel may write") "declined: the compact producer writes"
      // Outer-route and nest refusals of the offered chain.
      withGate "BLADE_FP_REPRO" "1" (fun () ->
        cepDecisionCase "decision_cell_epilogue_collapsed_nest" cepCollapsedSrc
            (declinedMentioning "did not admit the hot-row schedule") "declined: a collapse pragma places no row epilogue")
      withGate "BLADE_FP_REPRO" "1" (fun () ->
        cepDecisionCase "decision_cell_epilogue_threaded_cell_loop" cepThreadedCellSrc
            (declinedMentioning "cell loop is threaded") "declined: the cell loop is threaded")
      withGate "BLADE_BLAS" "1" (fun () -> withGate "BLADE_OMP_THREADS" "1" (fun () ->
        cepDecisionCase "decision_cell_epilogue_declined_blas" cepSerialFoldSrc
            (declinedMentioning "BLAS gate is on") "declined: the BLAS L1 dispatch may own the fold"))
      (CodeGen.setOmpTestMode true
       try
         cepDecisionCase "decision_cell_epilogue_declined_omp_test_mode" cepSaxpySrc
             (declinedMentioning "omp test mode") "declined: omp test mode"
       finally CodeGen.setOmpTestMode false)
      withGate "BLADE_CELL_EPILOGUE" "lanes" (fun () ->
        cepDecisionCase "decision_cell_epilogue_lanes_reserved" hotRowSrc
            (declinedMentioning "BLADE_CELL_EPILOGUE=lanes requests the licensed row arm (B2l), which is reserved") "declined: `lanes` names the reserved B2l arm")
      // The LLVM lane's own records.
      withGate "BLADE_OMP_THREADS" "1" (fun () ->
        cepLlvmDecisionCase "decision_cell_epilogue_llvm_applied_b1" cepSaxpySrc
            (fun d -> applied d && d.Evidence |> List.exists (fun e -> e.Contains "llvm lane: rank-1 producer")) "applied: the llvm fused loop")
      cepLlvmDecisionCase "decision_cell_epilogue_llvm_declined_seed" cepSeedSrc
          (declinedMentioning "llvm lane: the fold's seed is not a literal") "declined: llvm B1 with a non-literal seed"
      cepLlvmDecisionCase "decision_cell_epilogue_llvm_hot_row_licensed" hotRowSrc
          (declinedMentioning "llvm lane: licensed build") "declined: llvm licensed hot-row prodsum"
      cepLlvmDecisionCase "decision_cell_epilogue_llvm_recursion_licensed" cepRecurSrc
          (declinedMentioning "llvm lane: licensed build") "declined: llvm licensed recursion prodsum"
      withGate "BLADE_FP_REPRO" "1" (fun () ->
        cepLlvmDecisionCase "decision_cell_epilogue_llvm_hot_row_applied" hotRowSrc
            (fun d -> applied d && d.Evidence |> List.exists (fun e -> e.Contains "llvm lane: row chain interleave")) "applied: the llvm hot-row chain")
      withGate "BLADE_FP_REPRO" "1" (fun () ->
        cepLlvmDecisionCase "decision_cell_epilogue_llvm_recursion_applied" cepRecurSrc
            (fun d -> applied d && d.Evidence |> List.exists (fun e -> e.Contains "llvm lane: row chain interleave")) "applied: the llvm recursion chain") ]
/// The licensed threaded build: the class partials are written inside the
/// producer's row loop, and there is no orbit pass (no per-thread `__opart`,
/// no second team region).
let private compactRowEmitted () =
    let name = "compact_row_classes_partials_behind_each_row"
    match cppOfSource name compactRowSrc with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        let loop = cpp.IndexOf "for (size_t __i0 = 0; __i0 < 37; __i0++)"
        let inner = cpp.IndexOf("__orow_k[__i1] =", max loop 0)
        let d = cpp.IndexOf "__crc0_k[__i0] ="
        let o = cpp.IndexOf "__crc1_k[__i0] = blade_fold::sq<double, 8>(__orow_k + 1, (size_t)37 - 1 - __i0);"
        let combine = cpp.IndexOf "__oa1 = __oa1 + __crc1_k[__ori];"
        let checks =
            [ "partials inside the producer row loop, after the row", loop >= 0 && inner > loop && d > inner && o > d
              "row-order combine after the producer", combine > o
              "no per-thread orbit partials", not (cpp.Contains "__opart")
              "no orbit team region", not (cpp.Contains "BLADE_OMP_PARALLEL_NUM_THREADS(__oT)")
              "producer team kept", cpp.Contains "BLADE_OMP_PARALLEL_FOR_DYNAMIC"
              "addition-only combine", cpp.Contains "const double __ot1 = __oa1 + __oa1;" ]
        match checks |> List.filter (snd >> not) with
        | [] -> resultLine Pass name "per-row diagonal/off-diagonal partials in the producer loop; row-order combine; no orbit pass"; true
        | bad -> resultLine Fail name ("missing: " + (bad |> List.map fst |> String.concat ", ")); false

/// BLADE_FP_REPRO=1: declined, the orbit walk keeps its order (no partials).
let private compactRowReproDeclined () =
    let name = "compact_row_classes_repro_keeps_the_ordered_walk"
    match withGate "BLADE_FP_REPRO" "1" (fun () -> cppOfSource name compactRowSrc) with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        if not (cpp.Contains "__crc0_k") then resultLine Pass name "no class partials under BLADE_FP_REPRO=1"; true
        else resultLine Fail name "class partials emitted under BLADE_FP_REPRO=1"; false

let private compactRowReaderBetweenSrc =
    compactRowSrc.Replace("let ss = ", "let probe = k(3, 5)\nlet ss = ")

/// A scalar bound between the producer and the fold, read by the cell body:
/// the epilogue would read it before its binding -- declined at emission.
let private compactRowLateScalarSrc =
    compactRowSrc
        .Replace("let ss = ", "let scale = reduce(reduce(pts, (+)), (+)) / 4.0\nlet ss = ")
        .Replace("k(i, j) * k(i, j)", "scale * k(i, j)")

/// A seed that writes the producer through a `mut` parameter: declined.
let private compactRowWritingSeedSrc =
    compactRowSrc
        .Replace("let k = ", "let mut k = ")
        .Replace("let ss = ", "function poke(a: mut Array<Float64 like SymIdx<2, Pts>>) -> Float64 = {\n    a(0, 1) = 1000.0\n    0.0\n}\nlet ss = ")
        .Replace("lambda(u, v) where omp -> u + v, 0.0)", "lambda(u, v) where omp -> u + v, poke(k))")

/// Path B refusal keeps the square pool (the materialized route owns the omp
/// census); serial admission is compactionFusedLicensed above.
let private compactionOmpFoldKeepsPool () =
    let name = "compaction_fusion_path_b_fold_keeps_materialized_route"
    match withGate "BLADE_COMPACTION_FUSION" "b4" (fun () -> cppOfSource name compactionOmpFoldSrc) with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        if cpp.Contains "new double[" && not (cpp.Contains "blade_fold::sq<double, 8>(y.data")
           && countOf "y_q += (size_t)y_idx_pool[y_i];" cpp = 1 then
            resultLine Pass name "the compaction is fused, the omp fold keeps its materialized operand"
            true
        else
            resultLine Fail name "expected a fused compaction with the omp fold over a materialized `y * y`"
            false

/// BLADE_CSE=0: the same program keeps both folds in its one body (2 in all),
/// and the decision says why.
let private cseGateOffKeepsFolds () =
    let name = "cse_gate_off_keeps_both_folds"
    match withGate "BLADE_CSE" "0" (fun () -> cppOfSource name cseSrc) with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        let folds = System.Text.RegularExpressions.Regex.Matches(cpp, @"double __r = ").Count
        if folds = 2 then
            resultLine Pass name "two scalar folds in the body with BLADE_CSE=0"
            true
        else
            resultLine Fail name ($"expected 2 scalar folds with CSE off, got {folds}")
            false

/// BLADE_POOL_REUSE=0: no alias declaration reaches the emission.
let private poolReuseGateOffEmitsNoAlias () =
    let name = "pool_reuse_gate_off_emits_no_alias"
    match withGate "BLADE_POOL_REUSE" "0" (fun () -> cppOfSource name poolReuseChainSrc) with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        if cpp.Contains "pool reuse:" then
            resultLine Fail name "a `pool reuse:` alias was emitted with BLADE_POOL_REUSE=0"
            false
        else
            resultLine Pass name "no pool alias with BLADE_POOL_REUSE=0"
            true

/// KERNEL-INVARIANT HOISTING (Blade.Optimize.hoistKernelInvariantsModule).
/// `x - avg(x)` is `method_for(x) <@> lambda(e) -> e - avg(x) |> compute`,
/// so the fold of `x` used to run once PER CELL -- O(T^2) for a length-T
/// row. The call is bound once, before the nest, and the nest subtracts a
/// name. tests/corpus/functions/192 proves the VALUES (and opt-diff compares
/// them off against on); only an emission pin can tell O(T) from O(T^2).
let private hoistSrc =
    "function avg(row: T^1) -> Float64 = Float64(reduce(row, (+))) / Float64(extents(row))\n"
    + "function center(x: T^1) -> T^1 = x - avg(x)\n"
    + "type I = Idx<1000>\n"
    + "let v = method_for(range<I>) <@> lambda(i) -> 0.01 * Float64(i) |> compute\n"
    + "let c = center(v)\n"

/// The same body reached through a kernel, where the row's extent is not a
/// literal: `avg` of an EMPTY row is BL8003 and an empty map evaluated
/// nothing, so the hoisted value is bound under the nest's non-emptiness.
/// Both means leave the one fused nest.
let private hoistGuardedSrc =
    "function avg(row: T^1) -> Float64 = Float64(reduce(row, (+))) / Float64(extents(row))\n"
    + "function cov(a: T^1, b: T^1) where comm(a, b) -> Float64 = avg((a - avg(a)) * (b - avg(b)))\n"
    + "let R: Array<Float64 like Idx<3>, Idx<4>> = [[1.0, 2.0, 3.0, 6.0], [2.0, 4.0, 6.0, 8.0], [0.0, 1.0, 0.0, 1.0]]\n"
    + "let C = method_for(R, R) <@> cov |> compute\n"

/// An invariant-LOOKING call that writes: `tick` bumps its `mut` argument, so
/// it is not repeatable and runs once per cell exactly as written.
let private hoistEffectfulSrc =
    "type C = Idx<1>\n"
    + "function tick(c: mut Array<Float like C>) -> Float = {\n"
    + "    c((0 : C)) += 1.0\n"
    + "    c((0 : C))\n"
    + "}\n"
    + "let mut counter: Array<Float like C> = [0.0]\n"
    + "let x = [1.0, 2.0, 3.0]\n"
    + "let y = method_for(x) <@> lambda(e) -> e + tick(counter) |> compute\n"

/// A call that may abort, read only under the kernel's branch: the kernel
/// does not evaluate it on every cell, so binding it before the nest could
/// raise a BL8003 the program never raised. It stays in the kernel.
let private hoistConditionalSrc =
    "function avg(row: T^1) -> Float64 = Float64(reduce(row, (+))) / Float64(extents(row))\n"
    + "let x = [1.0, 2.0, 3.0]\n"
    + "let y = method_for(x) <@> lambda(e) -> if e > 1.5 then e - avg(x) else 0.0 |> compute\n"

/// The LATE array/scalar broadcast (IRMono.lowerArrayBinOpsModule). `lmean`
/// returns `T^0`, so inside the generic `center` the scalar operand is still
/// an inference variable when the checker meets `v - lmean(v)`, and the op is
/// rewritten after monomorphization instead. That rewrite builds the kernel
/// the checker builds (`lambda(e) -> e - lmean(v)`); it used to bind the
/// scalar in a `let` around the map, evaluated even for an empty `v` (BL8003
/// where the checker's path returns `[]`). Hoisting then binds it under the
/// nest's non-emptiness. `intersect` gives the row an extent only run time
/// knows, so the guard is a run-time test (tests/corpus/functions/194 runs it
/// on an empty one).
let private lateBroadcastSrc =
    "function lmean(row: T^1) -> T^0 = reduce(row, (+)) / Float64(extents(row))\n"
    + "function center(v: T^1) -> T^1 = v - lmean(v)\n"
    + "let x = [1.0, 2.0, 3.0, 6.0]\n"
    + "let common = intersect(x, [2.0, 6.0, 9.0])\n"
    + "let c = center(common)\n"

/// Nest store statements (`name[__i0] = ...` / `__fp_name[__fk] = ...`) that
/// still call a function whose name starts with `callee`. The ORIGINAL kernel
/// lambda stays in the emission as an unreferenced function whose `return`
/// holds the call; this pattern is the loop body only.
let private perCellCalls (callee: string) (cpp: string) =
    System.Text.RegularExpressions.Regex.Matches(cpp, @"\[__\w+\] = [^\n]*" + callee).Count
let private perCellAvgCalls (cpp: string) = perCellCalls "avg" cpp

let private hoistMovesCallOutOfNest () =
    let name = "hoist_moves_invariant_call_out_of_nest"
    match cppOfSource name hoistSrc with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        let inNest = perCellAvgCalls cpp
        let bound = System.Text.RegularExpressions.Regex.Matches(cpp, @"auto __v\d+ = avg").Count
        if inNest = 0 && bound >= 1 then
            resultLine Pass name ($"no `avg` call in a nest; bound once before it ({bound} let(s))")
            true
        else
            resultLine Fail name ($"expected 0 per-cell `avg` calls and >= 1 hoisted let, got {inNest} and {bound}")
            false

/// The guarded form, and both invariants out of the ONE fused nest.
let private hoistGuardsOnNonEmptyNest () =
    let name = "hoist_guards_on_non_empty_nest"
    match cppOfSource name hoistGuardedSrc with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        let inNest = perCellAvgCalls cpp
        let guarded = System.Text.RegularExpressions.Regex.Matches(cpp, @"extents\[0\]\) > 0L\)\) \? avg").Count
        if inNest = 0 && guarded = 2 then
            resultLine Pass name "two guarded lets (`<non-empty> ? avg(..) : 0.0`), no `avg` call in the nest"
            true
        else
            resultLine Fail name ($"expected 0 per-cell `avg` calls and 2 guarded lets, got {inNest} and {guarded}")
            false

/// BLADE_HOIST=0: the call stays in the nest, as written.
let private hoistGateOffKeepsPerCellCall () =
    let name = "hoist_gate_off_keeps_per_cell_call"
    match withGate "BLADE_HOIST" "0" (fun () -> cppOfSource name hoistSrc) with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        let inNest = perCellAvgCalls cpp
        if inNest >= 1 then
            resultLine Pass name ($"{inNest} per-cell `avg` call(s) with BLADE_HOIST=0")
            true
        else
            resultLine Fail name "no per-cell `avg` call with BLADE_HOIST=0: the gate does not reach the pass"
            false

/// The late broadcast, hoisted: one guarded let, no call left in the nest.
let private lateBroadcastGuardedOnNonEmptyNest () =
    let name = "late_broadcast_guarded_on_non_empty_nest"
    match cppOfSource name lateBroadcastSrc with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        let inNest = perCellCalls "lmean" cpp
        let guarded = System.Text.RegularExpressions.Regex.Matches(cpp, @"extents\[0\]\) > 0L\) \? lmean").Count
        let unconditional = System.Text.RegularExpressions.Regex.Matches(cpp, @"auto __v\d+ = lmean").Count
        if inNest = 0 && guarded = 1 && unconditional = 0 then
            resultLine Pass name "one guarded let (`<non-empty> ? lmean(v) : 0.0`), no unconditional one, no `lmean` call in the nest"
            true
        else
            resultLine Fail name ($"expected 0 per-cell calls, 1 guarded let, 0 unconditional lets; got {inNest}, {guarded}, {unconditional}")
            false

/// BLADE_HOIST=0 shows what the late rewrite itself builds: the scalar is IN
/// the kernel (evaluated per cell, so never for an empty row), not in a `let`
/// in front of the map.
let private lateBroadcastEmbedsScalarInKernel () =
    let name = "late_broadcast_embeds_scalar_in_kernel"
    match withGate "BLADE_HOIST" "0" (fun () -> cppOfSource name lateBroadcastSrc) with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        let inNest = perCellCalls "lmean" cpp
        let bound = System.Text.RegularExpressions.Regex.Matches(cpp, @"auto __v\d+ = [^\n]*lmean").Count
        if inNest >= 1 && bound = 0 then
            resultLine Pass name ($"{inNest} per-cell `lmean` call(s), no `let` in front of the map, with BLADE_HOIST=0")
            true
        else
            resultLine Fail name ($"expected the scalar in the kernel (>= 1 per-cell call, 0 lets), got {inNest} and {bound}")
            false

/// No `rule` decision may be APPLIED for this source (the barrier cases).
let private notAppliedCase (name: string) (src: string) (rule: string) (why: string) =
    match decisionsOf src with
    | Error e -> resultLine Fail name e; false
    | Ok ds ->
        match ds |> List.filter (fun d -> d.Rule = rule && applied d) with
        | [] -> resultLine Pass name why; true
        | hits ->
            let seen = hits |> List.map Blade.Effects.Decisions.render |> String.concat " | "
            resultLine Fail name ($"`{rule}` must not apply ({why}); saw: {seen}")
            false

/// CSE across a call that mutates the array through a `mut` parameter: the
/// second fold must see the write (tests/corpus/functions/134 pins 98).
let private cseMutParamCallSrc =
    "type I = Idx<7>\n"
    + "function bump(a: mut Array<Float like I>) -> Float = {\n"
    + "    a((0 : I)) = 100.0\n"
    + "    0.0\n"
    + "}\n"
    + "function g(x: Array<Float like I>) -> Float = {\n"
    + "    let mut y = x * 2.0\n"
    + "    let s1 = reduce(y, (+))\n"
    + "    let z = bump(y)\n"
    + "    let s2 = reduce(y, (+))\n"
    + "    s2 - s1 + z\n"
    + "}\n"
    + "let a = [1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0]\n"
    + "let m = g(a)\n"

/// CSE across a call that writes a module-level `let mut`.
let private cseGlobalWriteCallSrc =
    "type I = Idx<3>\n"
    + "let mut G: Array<Float like I> = [1.0, 2.0, 3.0]\n"
    + "function poke() -> Float = {\n"
    + "    G((0 : I)) = 100.0\n"
    + "    0.0\n"
    + "}\n"
    + "function g() -> Float = {\n"
    + "    let s1 = reduce(G, (+))\n"
    + "    let z = poke()\n"
    + "    let s2 = reduce(G, (+))\n"
    + "    s2 - s1 + z\n"
    + "}\n"
    + "let m = g()\n"

/// Two folds whose kernels differ only in the 11th significant digit of a
/// float literal: NOT the same value (the old `%A` key merged them).
let private cseFloatLiteralSrc =
    "type I = Idx<7>\n"
    + "function g(x: Array<Float like I>) -> Float = {\n"
    + "    let s1 = reduce(x, lambda(a, b) -> a + b * 0.1)\n"
    + "    let s2 = reduce(x, lambda(a, b) -> a + b * 0.10000000001)\n"
    + "    (s2 - s1) * 1.0e12\n"
    + "}\n"
    + "let a = [1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0]\n"
    + "let m = g(a)\n"

/// ... nor do `0.0` and `-0.0` (IEEE-equal, bitwise different).
let private cseSignedZeroSrc =
    "type I = Idx<3>\n"
    + "function g(x: Array<Float like I>) -> Float = {\n"
    + "    let s1 = reduce(x, lambda(a, b) -> a + b * 0.0)\n"
    + "    let s2 = reduce(x, lambda(a, b) -> a + b * -0.0)\n"
    + "    1.0 / s1 - 1.0 / s2\n"
    + "}\n"
    + "let a = [-1.0, -2.0, -3.0]\n"
    + "let m = g(a)\n"

/// Two folds with identical inline lambda kernels ARE the same value: the
/// pass must still fire on a kernel-carrying pair (a lifted lambda carries
/// no effect summary of its own, so it is judged by its body).
let private cseIdenticalLambdasSrc =
    "type I = Idx<7>\n"
    + "function g(x: Array<Float like I>) -> Float = {\n"
    + "    let s1 = reduce(x, lambda(a, b) -> a + b * 0.1)\n"
    + "    let s2 = reduce(x, lambda(a, b) -> a + b * 0.1)\n"
    + "    s2 - s1\n"
    + "}\n"
    + "let a = [1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0]\n"
    + "let m = g(a)\n"

/// A call that writes NOTHING is not a barrier (tests/corpus/functions/137).
let private cseAcrossPureCallSrc =
    "type I = Idx<7>\n"
    + "function sumsq(v: Array<Float like I>) -> Float = reduce(v * v, (+))\n"
    + "function scaled(x: Array<Float like I>) -> Float = {\n"
    + "    let y = x * 2.0\n"
    + "    let s1 = reduce(y, (+))\n"
    + "    let q = sumsq(y)\n"
    + "    let s2 = reduce(y, (+))\n"
    + "    s1 * 0.5 + s2 * 0.25 + q\n"
    + "}\n"
    + "let a = [1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0]\n"
    + "let r = scaled(a)\n"

/// Twins, one written through a `mut` argument: never merged, in either
/// role (tests/corpus/functions/138).
let private cseTwinOfMutatedSrc =
    "type I = Idx<7>\n"
    + "function dbl(v: Array<Float like I>) -> Array<Float like I> = v * 2.0\n"
    + "function bump(a: mut Array<Float like I>) -> Float = {\n"
    + "    a((0 : I)) = 100.0\n"
    + "    0.0\n"
    + "}\n"
    + "function g(x: Array<Float like I>) -> Float = {\n"
    + "    let mut y1 = dbl(x)\n"
    + "    let y2 = dbl(x)\n"
    + "    let z = bump(y1)\n"
    + "    reduce(y2, (+)) + z\n"
    + "}\n"
    + "let a = [1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0]\n"
    + "let t = g(a)\n"

/// A kernel reached through a local alias (`let k = lambda ..`) that writes a
/// module-level `let mut`: the judge cannot see its body through the alias,
/// so the name is the worst case (tests/corpus/functions/139).
let private cseLambdaAliasKernelSrc =
    "type I = Idx<3>\n"
    + "let mut G: Array<Float like I> = [1.0, 2.0, 3.0]\n"
    + "function g(x: Array<Float like I>) -> Float = {\n"
    + "    let k = lambda(a, b) -> {\n"
    + "        G((0 : I)) = G((0 : I)) + 1.0\n"
    + "        a + b\n"
    + "    }\n"
    + "    let s1 = reduce(x, k)\n"
    + "    let s2 = reduce(x, k)\n"
    + "    s1 + s2\n"
    + "}\n"
    + "let a = [1.0, 2.0, 3.0]\n"
    + "let m = g(a)\n"

/// The structural/05 D7 advisory: gram, decompact, a row prodsum -- recorded
/// as left-as-written with the `gram_apply` spelling in the evidence.
let private gramAdvisorySrc =
    "let A: Array<Float64 like Idx<3>, Idx<2>> = [[1.0, 2.0], [3.0, 4.0], [5.0, 6.0]]\n"
    + "let v: Array<Float64 like Idx<3>> = [1.0, 2.0, 3.0]\n"
    + "let G = gram(A, A)\n"
    + "let Gd = decompact(G, 0)\n"
    + "let y = method_for(Gd) <@> lambda(row) -> prodsum(row, v) |> compute\n"

// ---------------------------------------------------------------------------
// `blade plan`'s CODEGEN decisions: the structure declared symmetry buys,
// OpenMP placement and drops, BLAS/LAPACK routing, the microkernels. These
// record while the C++ is generated, so the record is taken over lower +
// generate (what `blade plan` runs), not lowering alone.

/// The decision record over lower + code generation, text discarded.
let private planDecisionsOf (src: string) : Result<Blade.Effects.Decision list, string> =
    Blade.Effects.Decisions.start ()
    let r =
        try
            match lower src with
            | Error e -> Error ($"lower: {e}")
            | Ok ir ->
                CodeGen.genSelfContainedProgramFromIR ir "plan_case" |> ignore
                Ok (Blade.Effects.Decisions.drain ())
        with ex -> Error ($"codegen raised: {ex.Message}")
    Blade.Effects.Decisions.drain () |> ignore
    r

/// Some `rule` decision satisfies `want` in the lower + generate record.
let private planCase (name: string) (src: string) (rule: string)
                     (want: Blade.Effects.Decision -> bool) (describe: string) =
    match planDecisionsOf src with
    | Error e -> resultLine Fail name e; false
    | Ok ds ->
        let mine = ds |> List.filter (fun d -> d.Rule = rule)
        if mine |> List.exists want then
            resultLine Pass name ($"{mine.Length} `{rule}` decision(s); {describe}")
            true
        else
            let seen = ds |> List.map Blade.Effects.Decisions.render |> String.concat " | "
            let shown = if seen = "" then "empty" else seen
            resultLine Fail name ($"wanted {describe}; the record: {shown}")
            false

let private evidenceMentioning (needle: string) (d: Blade.Effects.Decision) =
    d.Evidence |> List.exists (fun (e: string) -> e.Contains needle)

/// The comm covariance of ONE array (tests/corpus/symmetry/017): packed
/// output storage and a triangular nest, both applied.
let private planCommSameArraySrc =
    "type TimeIdx = Idx<2>\n"
    + "let A: Array<Float64 like Idx<2>, TimeIdx> = [[1.0, 2.0], [3.0, 4.0]]\n"
    + "let k = lambda(a: Array<Float64 like TimeIdx>, b: Array<Float64 like TimeIdx>) where comm(a, b) -> prodsum(a, b)\n"
    + "let r = method_for(A, A) <@> k |> compute\n"

/// The same kernel over two DIFFERENT arrays: `comm` licenses no packed
/// storage, and the record says why.
let private planCommDistinctSrc =
    "type TimeIdx = Idx<2>\n"
    + "let A: Array<Float64 like Idx<2>, TimeIdx> = [[1.0, 2.0], [3.0, 4.0]]\n"
    + "let B: Array<Float64 like Idx<2>, TimeIdx> = [[5.0, 6.0], [7.0, 8.0]]\n"
    + "let k = lambda(a: Array<Float64 like TimeIdx>, b: Array<Float64 like TimeIdx>) where comm(a, b) -> prodsum(a, b)\n"
    + "let r = method_for(A, B) <@> k |> compute\n"

/// A TOP-LEVEL alias of a pure deferred map (formalism 10.2 alias class, 8.3
/// identity groups; docs/plans/plan-identity-transpose-compute.md 2.1): the
/// map and its alias are ONE array, so the comm product over (g, g2) is
/// packed and triangular, and the record names both operands as written.
/// tests/corpus/symmetry/146.
let private planDeferredAliasSrc =
    "let x = [1.0, 2.0, 3.0]\n"
    + "let g = method_for(x) <@> lambda(v) -> v * 2.0\n"
    + "let g2 = g\n"
    + "let r = method_for(g, g2) <@> lambda(a, b) where comm(a, b) -> a * b |> compute\n"

/// The same alias bound inside a BLOCK is a forcing copy (10.2): two arrays,
/// declined. tests/corpus/symmetry/151.
let private planBlockDeferredAliasSrc =
    "let x = [1.0, 2.0, 3.0]\n"
    + "let g = method_for(x) <@> lambda(v) -> v * 2.0\n"
    + "let r = {\n    let b = g\n    method_for(g, b) <@> lambda(a, c) where comm(a, c) -> a * c |> compute\n}\n"

/// A NEVER-WRITTEN top-level alias of a STORED array (docs/plans/plan-
/// identity-transpose-compute.md 2.1, second step; rule `alias-identity v1`):
/// A and B are one array, so the comm product is packed. tests/corpus/
/// symmetry/154.
let private planStoredAliasSrc =
    "type I = Idx<3>\n"
    + "let A: Array<Float64 like I> = [1.0, 2.0, 3.0]\n"
    + "let B = A\n"
    + "let r = method_for(A, B) <@> lambda(a, b) where comm(a, b) -> a * b |> compute\n"

/// The same alias over a CALL root: its reach (`mk`) joins the proof.
let private planStoredAliasCallSrc =
    "type I = Idx<3>\n"
    + "function mk() -> Array<Float64 like I> = [4.0, 5.0, 6.0]\n"
    + "let E = mk()\n"
    + "let F = E\n"
    + "let r = method_for(E, F) <@> lambda(a, b) where comm(a, b) -> a * b |> compute\n"

/// The alias written later: declined, dense (tests/corpus/symmetry/150).
let private planStoredAliasWrittenSrc =
    planStoredAliasSrc + "B((0 : I)) = 9.0\n"

/// The stored alias in a module another module imports: its writes are not
/// judged here, so the proof fails closed.
let private storedAliasImportedSources =
    [ ("Data", "module Data\n\ntype I = Idx<3>\nlet A: Array<Float64 like I> = [1.0, 2.0, 3.0]\nlet B = A\nlet r = method_for(A, B) <@> lambda(a, b) where comm(a, b) -> a * b |> compute\n")
      ("Main", "module Main\nimport Data\n\nlet s = reduce(Data.r, (+), axes = 2)\n") ]

/// A call root whose callee lives in ANOTHER module and hands back that
/// module's own array, which another call writes (Codex review of the call
/// root, finding 3): the reach cannot judge Lib's binding, so neither the
/// identity nor the storage plan may treat B as A.
let private storedAliasForeignCallSources =
    [ ("Lib", "module Lib\n\nlet mut X: Array<Float64 like Idx<3>> = [1.0, 2.0, 3.0]\nfunction get() -> Array<Float64 like Idx<3>> = X\nfunction poke() -> Float64 = {\n    X(0) = 9.0\n    0.0\n}\n")
      ("Main", "module Main\nimport Lib\n\nlet A = Lib.get()\nlet B = A\nlet z = Lib.poke()\nlet snapshot = B(0)\n") ]

/// IRMono's co-iteration identities (plan 2.7): a variable operand keys by its
/// id; every NON-variable operand mints its own identity (it used to share the
/// constant `AIDVariable "__coi"`). tests/corpus/loops/598 runs the shape.
let private coIterIdentityPins () : bool list =
    let builder = IRBuilder()
    let f64 = IRTScalar ETFloat64
    let e1 = IRTupleProj (IRVar (101, f64), 0, true)
    let e2 = IRTupleProj (IRVar (101, f64), 1, true)
    let i1 = Blade.IRMono.coIterIdentityOf builder e1
    let i2 = Blade.IRMono.coIterIdentityOf builder e2
    let i1again = Blade.IRMono.coIterIdentityOf builder e1
    let v1 = Blade.IRMono.coIterIdentityOf builder (IRVar (7, f64))
    let v1again = Blade.IRMono.coIterIdentityOf builder (IRVar (7, f64))
    let v2 = Blade.IRMono.coIterIdentityOf builder (IRVar (8, f64))
    let check name ok detail =
        resultLine (if ok then Pass else Fail) name detail
        ok
    [ check "coi_identity_non_variable_operands_distinct" (not (sameIdentity i1 i2) && not (sameIdentity i1 i1again))
          $"two pack elements and one element twice: {i1}, {i2}, {i1again} (none shared)"
      check "coi_identity_variable_operands_keyed_by_id" (sameIdentity v1 v1again && not (sameIdentity v1 v2))
          $"the same variable twice is one array, two variables are two: {v1}, {v1again}, {v2}" ]

/// An `omp`-licensed outer product: the placed construct is recorded.
let private planOmpSrc =
    "let A = [1.0, 2.0, 3.0]\nlet B = [4.0, 5.0, 6.0]\n"
    + "let m = method_for(A, B) <@> lambda(x, y) where omp(x: 1) -> x * y |> compute\n"

let private planMatmulSrc (m: int) (k: int) (n: int) =
    "import math as m\n"
    + $"let A: Array<Float64 like Idx<{m}>, Idx<{k}>> = method_for(range<Idx<{m}>>, range<Idx<{k}>>) <@> lambda(i, j) -> Float64(i + j) |> compute\n"
    + $"let B: Array<Float64 like Idx<{k}>, Idx<{n}>> = method_for(range<Idx<{k}>>, range<Idx<{n}>>) <@> lambda(i, j) -> Float64(i * j) |> compute\n"
    + "let C = m.matmul(A, B)\n"

/// A partial row fold over a plain dense rank-2 array: the row-fold jam.
let private planRowFoldSrc =
    "let M: Array<Float64 like Idx<9>, Idx<5>> = method_for(range<Idx<9>>, range<Idx<5>>) <@> lambda(i, j) -> Float64(i + j) |> compute\n"
    + "let s = reduce(M, (+))\n"

/// gram of one array, BLAS off: the triangular native arm and its jam.
/// The same-array comm row contraction at 20 x 8: above the syrk-tile
/// crossover (p >= 16, k >= 4), so the BLAS-off arm is the packed tile.
let private planSyrkSrc (p: int) (n: int) =
    $"type V = Idx<{p}>\ntype S = Idx<{n}>\n"
    + "let A: Array<Float64 like V, S> = method_for(range<V>, range<S>) <@> lambda(i, s) -> Float64(i * 8 + s) |> compute\n"
    + "let k = lambda(a: Array<Float64 like S>, b: Array<Float64 like S>) where comm(a, b) -> prodsum(a, b)\n"
    + "let r = method_for(A, A) <@> k |> compute\n"

let private planSyrkTileSrc = planSyrkSrc 20 8

/// The same shape with the kernel's `/ d` tail (fused into the tile's last
/// block store on both lanes).
let private planSyrkScaledSrc =
    "type V = Idx<20>\ntype S = Idx<8>\n"
    + "let x = method_for(range<V>, range<S>) <@> lambda(i, s) -> Float64(i + s) |> compute\n"
    + "let g = method_for(x, x) <@> lambda(a: Array<Float64 like S>, b: Array<Float64 like S>) where comm(a, b) -> prodsum(a, b) / 4.0 |> compute\n"

/// gram(A, A) at 20 x 8: the node's BLAS-off arm takes the syrk tile.
let private planGramTileSrc =
    "let A: Array<Float64 like Idx<20>, Idx<8>> = method_for(range<Idx<20>>, range<Idx<8>>) <@> lambda(i, j) -> Float64(i + j) |> compute\n"
    + "let G = gram(A, A)\n"
/// The antisym pair `prodsum(a(0), b(1)) - prodsum(b(0), a(1))` over the
/// stacked Z(p, 2, n) (review item M7's spelling, bench/kernels/antisym).
let private planAntiSrc (p: int) (n: int) =
    $"type P = Idx<{p}>
type C = Idx<2>
type S = Idx<{n}>
"
    + "let z = method_for(range<P>, range<C>, range<S>) <@> lambda(i, c, s) -> Float64(i * 3 + c + s) |> compute
"
    + "let l = method_for(z, z) <@> lambda(a: Array<Float64 like C, S>, b: Array<Float64 like C, S>) where anticomm(a, b) -> prodsum(a((0 : C)), b((1 : C))) - prodsum(b((0 : C)), a((1 : C))) |> compute
"

/// ...with the bench's thread licence on the kernel's level 0 (`omp(a: 1)`):
/// with thread emission on, the tile driver runs a team (anti_g's threaded
/// regime, round 3 W2-0b).
let private planAntiOmpSrc (p: int) (n: int) =
    (planAntiSrc p n).Replace("where anticomm(a, b) ->", "where anticomm(a, b), omp(a: 1) ->")

/// The rank-3 simplex `prodsum(a, b, c) / Float64(n)` (review item M8's
/// spelling, bench/kernels/comoment3).
let private planSym3Src (p: int) (n: int) =
    $"type V = Idx<{p}>
type S = Idx<{n}>
let static nn = {n}
"
    + "let x = method_for(range<V>, range<S>) <@> lambda(i, s) -> Float64(i + s) |> compute
"
    + "let t = method_for(x, x, x) <@> lambda(a: Array<Float64 like S>, b: Array<Float64 like S>, c: Array<Float64 like S>) where comm(a, b, c) -> prodsum(a, b, c) / Float64(nn) |> compute
"
/// The orbit-class fold fixtures: a SymIdx<2, 7> pool `g` and a checksum
/// binding `ss` over it.
let private planOrbitSrc (ssBody: string) =
    "type Var = Idx<7>\n"
    + "type Obs = Idx<3>\n"
    + "let x = method_for(range<Var>, range<Obs>) <@> lambda(i, s) -> Float64((i * 5 + s * 3) % 7) - 2.0 |> compute\n"
    + "let g = method_for(x, x) <@> lambda(a: Array<Float64 like Obs>, b: Array<Float64 like Obs>) where comm(a, b) -> prodsum(a, b) |> compute\n"
    + $"let ss = {ssBody}\n"

/// `reduce(method_for(range<Var>) <@> lambda(i) -> reduce(method_for(range<Var>) <@> lambda(j) -> BODY, INNER), OUTER)`
let private planOrbitNest (body: string) (inner: string) (outer: string) =
    $"reduce(method_for(range<Var>) <@> lambda(i) -> reduce(method_for(range<Var>) <@> lambda(j) -> {body}, {inner}), {outer})"

let private planOrbitSym2Body = planOrbitNest "g(i, j) * g(i, j)" "(+)" "lambda(u, v) where omp -> u + v, 0.0"

/// Pin several knobs for one call, restoring each (the pull cases' spelling;
/// `withGates` below is the same, defined later in the file).
let private pullGates (pins: (string * string) list) (f: unit -> 'a) : 'a =
    List.foldBack (fun (var, value) (g: unit -> 'a) -> (fun () -> withGate var value g)) pins f ()

/// THE ROW-OWNED PULL fixture (round 3, D-P3): comoment3's rank-3 checksum
/// shape over a packed SymIdx<3, p> pool `t` with the request on the OUTER
/// fold; `between` sits between the pool and the fold, `leaf` is the leaf
/// level's fold (kernel[, seed]), `prelude` lines go first. `bump` writes a
/// cell of the pool through a `mut` parameter (the intervening writer).
let private planPullSrcWith (prelude: string) (p: int) (between: string) (leaf: string) =
    prelude
    + $"type Var = Idx<{p}>\n"
    + "type Obs = Idx<2>\n"
    + "function bump(a: mut Array<Float64 like SymIdx<3, Var>>) -> Int64 = {\n    a(0, 1, 2) = 2.5\n    7\n}\n"
    + "let x = method_for(range<Var>, range<Obs>) <@> lambda(i, s) -> Float64((i * 3 + s * 5) % 7) - 2.0 |> compute\n"
    + "let mut t = method_for(x, x, x) <@> lambda(a: Array<Float64 like Obs>, b: Array<Float64 like Obs>, c: Array<Float64 like Obs>) where comm(a, b, c) -> prodsum(a, b, c) |> compute\n"
    + between
    + $"let ss = reduce(method_for(range<Var>) <@> lambda(i) -> reduce(method_for(range<Var>) <@> lambda(j) -> reduce(method_for(range<Var>) <@> lambda(k) -> t(i, j, k) * t(i, j, k), {leaf}), (+)), lambda(u, v) where omp -> u + v, 0.0)\n"
let private planPullSrc (p: int) (between: string) = planPullSrcWith "" p between "(+)"

/// The EARLIER-TOP-LEVEL-POOL scope (finding 3): the pool `t` is a top-level
/// binding and the walk sits in a block's let chain of a LATER top-level
/// binding; `between` is top-level statements between the two (the audit's
/// intervening set: those, plus the block's statements before the fold).
let private planPullBlockSrc (between: string) =
    let full = planPullSrc 97 between
    let cut = full.IndexOf "let ss = "
    full.Substring(0, cut)
    + "let (s5, q) = {\n    let q = 1.5\n"
    + "    let s5 = reduce(method_for(range<Var>) <@> lambda(i) -> reduce(method_for(range<Var>) <@> lambda(j) -> reduce(method_for(range<Var>) <@> lambda(k) -> t(i, j, k) * t(i, j, k), (+)), (+)), lambda(u, v) where omp -> u + v, 0.0)\n"
    + "    (s5, q)\n}\n"

/// The pull's applied record (form, five-party matrix, ownership plan).
let private pullApplied (d: Blade.Effects.Decision) =
    d.Rule = "fold-form" && d.Outcome = Blade.Effects.Applied
    && d.Evidence |> List.exists (fun (e: string) -> e.Contains "row-owned pull on a thread team")
    && d.Evidence |> List.exists (fun (e: string) -> e.StartsWith "five-party audit (planner: Optimize.scheduleMayWriteIn")
    && d.Evidence |> List.exists (fun (e: string) -> e.StartsWith "team bodies (the callback-aware codegen write analysis")
    && d.Evidence |> List.exists (fun (e: string) -> e.StartsWith "ownership plan: ")

/// No pull record of either kind (the form is not in play).
let private pullCase (name: string) (src: string) (want: Blade.Effects.Decision list -> bool) (describe: string) =
    match planDecisionsOf src with
    | Error e -> resultLine Fail name e; false
    | Ok ds ->
        if want ds then resultLine Pass name describe; true
        else
            let seen = ds |> List.filter (fun d -> d.Rule = "fold-form" || d.Rule = "omp") |> List.map Blade.Effects.Decisions.render |> String.concat " | "
            resultLine Fail name ($"wanted {describe}; the record: {seen}"); false
/// The decision record and the C++ text of one lower + generate.
let private pullPlanAndCpp (src: string) : Result<Blade.Effects.Decision list * string, string> =
    Blade.Effects.Decisions.start ()
    let r =
        try
            match lower src with
            | Error e -> Error ($"lower: {e}")
            | Ok ir ->
                let cpp = fst (CodeGen.genSelfContainedProgramFromIR ir "plan_case")
                Ok (Blade.Effects.Decisions.drain (), cpp)
        with ex -> Error ($"codegen raised: {ex.Message}")
    Blade.Effects.Decisions.drain () |> ignore
    r

/// Codex diff review of W2-3, finding 3 (a): a PRODUCER whose write lives
/// ONLY in its side-table payload (Optimize.bindingPayload: the binding's own
/// value a placeholder-like pure compute, a RandomInits key that writes). No
/// frontend spelling attaches a side table to a SymIdx pool, so the fixture is
/// CONSTRUCTED: the pure pull program, lowered; its writing statement
/// `z = bump(t)` lifted out of the chain and attached as `t`'s random-fill key.
/// The planner's audit must admit the pure module and decline the constructed
/// one, naming the producer.
let private pullSideTableProducerCase () : bool =
    let name = "plan_orbit_pull_declined_producer_side_table_payload (constructed IR)"
    match (try lower (planPullSrc 97 "let z = bump(t)\n") with ex -> Error ex.Message) with
    | Error e -> resultLine Fail name ($"lower: {e}"); false
    | Ok ir ->
        match ir.Modules |> List.tryFind (fun m -> m.Bindings |> List.exists (fun b -> b.Name = "ss")) with
        | None -> resultLine Fail name "no module binds `ss`"; false
        | Some m ->
        let byName (n: string) = m.Bindings |> List.find (fun b -> b.Name = n)
        let t, ss, z = byName "t", byName "ss", byName "z"
        let clean = { m with Bindings = m.Bindings |> List.filter (fun b -> b.Id <> z.Id) }
        let tainted = { clean with RandomInits = Map.add t.Id (IR.RandGen ("uniform", z.Value, [], None, None)) clean.RandomInits }
        let funcs = ir.Modules |> List.collect (fun mm -> mm.Functions)
        let auditOf (mm: IR.IRModule) =
            Blade.Optimize.OrbitPullTable.reset ()
            Blade.Optimize.planOrbitPull funcs mm
            Blade.Optimize.OrbitPullTable.tryAudit ss.Id t.Id
        let pureAudit = auditOf clean
        let taintedAudit = auditOf tainted
        Blade.Optimize.OrbitPullTable.reset ()
        match pureAudit, taintedAudit with
        | Some (Ok _), Some (Error why) when why.Contains "the pool's producer `t` may write" ->
            resultLine Pass name ($"pure control admitted; the side-table writer declines: {why}"); true
        | p, tt ->
            resultLine Fail name ($"wanted the pure module admitted and the constructed one declined on the producer; got pure {p}, constructed {tt}"); false

/// Finding 3 (b): a HIGHER-ORDER callback writer in a would-be team body --
/// `chk`'s walk calls its function-valued parameter `h`, the call site passes a
/// lambda that writes a captured array -- under an otherwise eligible outer
/// request. No `pull3` may be emitted (the walk's cell function is a call, not
/// a pure scalar function of the read: the walk recogniser refuses it,
/// and the omp census names the captured write), or lowering refuses the
/// program (BL4005); beside it the PURE control (the same walk over the same
/// parameter pool, its cell function inline) pulls.
let private pullCallbackWriterCase () : bool =
    let name = "plan_orbit_pull_higher_order_callback_writer_never_pulled"
    let prelude =
        "type Var = Idx<97>\ntype Obs = Idx<2>\ntype K = Idx<2>\n"
        + "let mut G: Array<Float64 like K> = method_for(range<K>) <@> lambda(k) -> 0.0 |> compute\n"
        + "function sq(x: Float64) -> Float64 = x * x\n"
        + "let x = method_for(range<Var>, range<Obs>) <@> lambda(i, s) -> Float64((i * 3 + s * 5) % 7) - 2.0 |> compute\n"
        + "let t = method_for(x, x, x) <@> lambda(a: Array<Float64 like Obs>, b: Array<Float64 like Obs>, c: Array<Float64 like Obs>) where comm(a, b, c) -> prodsum(a, b, c) |> compute\n"
    let walk (cell: string) =
        $"reduce(method_for(range<Var>) <@> lambda(i) -> reduce(method_for(range<Var>) <@> lambda(j) -> reduce(method_for(range<Var>) <@> lambda(k) -> {cell}, (+)), (+)), lambda(u, v) where omp -> u + v, 0.0)"
    let walkH = walk "h(p(i, j, k))"
    let walkSq = walk "p(i, j, k) * p(i, j, k)"
    let writerSrc =
        prelude
        + "let w = lambda(v: Float64) -> {\n    G((0 : K)) = G((0 : K)) + 1.0\n    v * v\n}\n"
        + $"function chk(p: Array<Float64 like SymIdx<3, Var>>, h: Float64 -> Float64) -> Float64 = {{\n    let s = {walkH}\n    s\n}}\n"
        + "let sw = chk(t, w)\n"
    let pureSrc =
        prelude
        + $"function chk(p: Array<Float64 like SymIdx<3, Var>>) -> Float64 = {{\n    let s = {walkSq}\n    s\n}}\n"
        + "let sp = chk(t)\n"
    let anyPull (ds: Blade.Effects.Decision list) = ds |> List.exists pullApplied
    let writerOk =
        match pullGates [ ("BLADE_FP_REPRO", "1"); ("BLADE_OMP_THREADS", null) ] (fun () -> pullPlanAndCpp writerSrc) with
        | Error e when e.Contains "BL4005" -> Ok "lowering refused the writing callback (BL4005)"
        | Error e -> Error e
        | Ok (ds, cpp) ->
            if cpp.Contains "blade_scatter::pull3<" || anyPull ds then Error "a pull3 site over a walk that calls a writing callback"
            else
                let declinedWith (needle: string) =
                    ds |> List.exists (fun d ->
                        match d.Outcome with
                        | Blade.Effects.Declined why -> why.Contains needle
                        | _ -> false)
                let declined =
                    (declinedWith "ordered orbit scatter not taken: the kernel body is not a pure scalar function of the read"
                     || declinedWith "does not resolve to a callable")
                    && declinedWith "the map kernel writes a captured binding"
                if declined then Ok "no pull3; the strict scatter declines the callback walk (not a pure scalar function of the read) and the omp census names the captured write"
                else Error "no pull3, but no decline names the callback"
    let pureOk =
        match pullGates [ ("BLADE_FP_REPRO", "1"); ("BLADE_OMP_THREADS", null) ] (fun () -> pullPlanAndCpp pureSrc) with
        | Error e -> Error e
        | Ok (ds, cpp) ->
            if cpp.Contains "blade_scatter::pull3<" && anyPull ds then Ok "the pure control pulls"
            else Error "the pure control (the same walk in a function, the cell function inline) did not pull"
    match writerOk, pureOk with
    | Ok w, Ok p -> resultLine Pass name ($"{w}; {p}"); true
    | Error e, _ -> resultLine Fail name ("writer: " + e); false
    | _, Error e -> resultLine Fail name ("pure control: " + e); false

let private mentionsPull (d: Blade.Effects.Decision) =
    d.Evidence |> List.exists (fun (e: string) -> e.Contains "row-owned pull")
    || (match d.Outcome with Blade.Effects.Declined why -> why.Contains "row-owned pull" | _ -> false)

let private planOrbitSym3Src =
    "type Var = Idx<7>\n"
    + "type Obs = Idx<4>\n"
    + "let x = method_for(range<Var>, range<Obs>) <@> lambda(i, s) -> Float64((i * 3 + s * 5) % 7) - 2.0 |> compute\n"
    + "let t = method_for(x, x, x) <@> lambda(a: Array<Float64 like Obs>, b: Array<Float64 like Obs>, c: Array<Float64 like Obs>) where comm(a, b, c) -> prodsum(a, b, c) |> compute\n"
    + "let ss = reduce(method_for(range<Var>) <@> lambda(i) -> reduce(method_for(range<Var>) <@> lambda(j) -> reduce(method_for(range<Var>) <@> lambda(k) -> t(i, j, k) * t(i, j, k), (+)), (+)), (+))\n"

let private planOrbitHermitianSrc =
    "type Two = Idx<2>\n"
    + "let A: Array<Complex128 like Two, Idx<3>> = [[complex(1.0, 1.0), complex(2.0, 0.0), complex(0.0, 1.0)], [complex(3.0, -1.0), complex(1.0, 2.0), complex(2.0, 0.0)]]\n"
    + "let H = gram(A, A)\n"
    + "let ss = reduce(method_for(range<Two>) <@> lambda(i) -> reduce(method_for(range<Two>) <@> lambda(j) -> abs(H(i, j)), (+)), (+))\n"

let private planOrbitAntisymSrc =
    "type Part = Idx<6>\n"
    + "type Comp = Idx<2>\n"
    + "let z = method_for(range<Part>, range<Comp>) <@> lambda(i, c) -> Float64((i * 5 + c * 3) % 9) - 4.0 |> compute\n"
    + "let l = method_for(z, z) <@> lambda(a: Array<Float64 like Comp>, b: Array<Float64 like Comp>) where anticomm(a, b) -> a((0 : Comp)) * b((1 : Comp)) - b((0 : Comp)) * a((1 : Comp)) |> compute\n"
    + "let ss = reduce(method_for(range<Part>) <@> lambda(i) -> reduce(method_for(range<Part>) <@> lambda(j) -> l(i, j) * l(i, j), (+)), (+))\n"

/// A `where repro` `+`: a fold kernel with NO reorder licence (the ordered
/// orbit scatter's default-build domain).
let private planScatterRadd = "function radd(a: Float64, b: Float64) where repro -> Float64 = a + b\n"

/// The rank-2 walk with strict (`radd`) folds at both levels.
let private planScatterStrictSrc =
    planScatterRadd + planOrbitSrc (planOrbitNest "g(i, j) * g(i, j)" "radd, -0.0" "radd, 0.75")

/// An Int64 pool folded by a kernel that may panic (integer `/`).
let private planScatterPartialSrc =
    "type Var = Idx<7>\n"
    + "type Obs = Idx<3>\n"
    + "function ihalf(a: Int64, b: Int64) where repro -> Int64 = a + b / 2\n"
    + "let x = method_for(range<Var>, range<Obs>) <@> lambda(i, s) -> Int64((i * 5 + s * 3) % 7) - 2 |> compute\n"
    + "let g = method_for(x, x) <@> lambda(a: Array<Int64 like Obs>, b: Array<Int64 like Obs>) where comm(a, b) -> prodsum(a, b) |> compute\n"
    + "let ss = reduce(method_for(range<Var>) <@> lambda(i) -> reduce(method_for(range<Var>) <@> lambda(j) -> g(i, j), ihalf, 0), ihalf, 0)\n"

/// The orbit fold's repro decline and THEN the scatter's applied record, for
/// the same binding, in that order.
let private planOrbitDeclineThenScatter (name: string) (src: string) =
    match planDecisionsOf src with
    | Error e -> resultLine Fail name e; false
    | Ok ds ->
        let folds = ds |> List.filter (fun d -> d.Rule = "fold-form" && d.Subject = "ss")
        let orbitAt = folds |> List.tryFindIndex (declinedMentioning "orbit-class fold not taken: BLADE_FP_REPRO=1")
        let scatterAt = folds |> List.tryFindIndex (fun d -> applied d && evidenceMentioning "fold form: ordered orbit scatter (rank 2" d)
        match orbitAt, scatterAt with
        | Some o, Some s when o < s ->
            resultLine Pass name "the orbit-class fold's repro decline, then the ordered orbit scatter applied"; true
        | _ ->
            let seen = folds |> List.map Blade.Effects.Decisions.render |> String.concat " | "
            resultLine Fail name ($"wanted the orbit decline followed by the scatter record; the fold-form record: {seen}"); false

let private planGramSrc =
    "let A: Array<Float64 like Idx<9>, Idx<5>> = method_for(range<Idx<9>>, range<Idx<5>>) <@> lambda(i, j) -> Float64(i + j) |> compute\n"
    + "let G = gram(A, A)\n"

/// #65: Lowering's per-program reset of the codegen-refusal span table
/// (IR.declSpansCell) used to clear the ref IN PLACE. A child flow (Task.Run,
/// a large-stack thread) shares its parent's ref, so a child lowering its own
/// program wiped the parent's spans mid-codegen. The reset now installs a
/// fresh ref (the freshCell pattern): the parent's table survives.
let private declSpanResetIsolated () =
    let name = "decl_span_reset_installs_fresh_ref"
    IR.resetDeclSpans ()
    let marker = { Blade.Ast.noSpan with StartLine = 41; EndLine = 41; EndCol = 9 }
    IR.recordDeclSpan "__parent_marker" marker
    let child =
        System.Threading.Tasks.Task.Run(fun () ->
            match lower "let c = 1\nlet d = c + 1\n" with
            | Ok _ -> true
            | Error _ -> false)
    let childLowered = child.Result
    let parentKept = IR.declSpanOf "__parent_marker" = marker
    if childLowered && parentKept then
        resultLine Pass name "a child flow's lowering starts its own span table; the parent's survives"
        true
    else
        resultLine Fail name ($"childLowered={childLowered}, parentKept={parentKept}")
        false

/// PARALLEL EMISSION ISOLATION. The per-program emission cells are
/// AsyncLocal refs, and a child flow inherits its parent's ref OBJECT: when
/// the parent had assembled a program first (as this does, on purpose), every
/// parallel pipeline used to share one ref, and one pipeline's per-program
/// reset cleared a flag another had just set -- a program losing its
/// `#include "blade_packed_gemm.hpp"`. Resets now install fresh refs
/// (CodeGenState.freshCell). Many assemblies race here, each on its own large
/// stack like the corpus harness, alternating a program that needs the
/// packed-gemm header with one that must not name it; every translation unit
/// must carry the include exactly when it calls the kernel.
let private parallelEmissionIsolated () =
    let name = "parallel_emission_cells_isolated"
    withGate "BLADE_BLAS" "0" (fun () ->
    withGate "OPENBLAS_DIR" null (fun () ->
        match lower (planMatmulSrc 20 16 110), lower "let x = [1.0, 2.0, 3.0]\nlet y = x + 1.0\n" with
        | Error e, _ | _, Error e -> resultLine Fail name ($"lower: {e}"); false
        | Ok big, Ok small ->
            // Touch every cell in THIS flow first: the children inherit it.
            CodeGen.genSelfContainedProgramFromIR big "iso_parent" |> ignore
            let consistent (cpp: string) =
                cpp.Contains "blade_pgemm::dgemm_nn" = cpp.Contains "#include \"blade_packed_gemm.hpp\""
            let outcomes =
                [| 0 .. 63 |]
                |> Array.Parallel.map (fun i ->
                    Blade.Runtime.runOnLargeStack (fun () ->
                        let ir = if i % 2 = 0 then big else small
                        let (cpp, _) = CodeGen.genSelfContainedProgramFromIR ir $"iso_{i}"
                        (i % 2 = 0, cpp.Contains "blade_pgemm::dgemm_nn", consistent cpp)))
            let bad = outcomes |> Array.filter (fun (_, _, ok) -> not ok) |> Array.length
            let lostKernel = outcomes |> Array.filter (fun (isBig, calls, _) -> isBig <> calls) |> Array.length
            if bad = 0 && lostKernel = 0 then
                resultLine Pass name "64 concurrent assemblies: every include set matches its own program"
                true
            else
                resultLine Fail name ($"{bad} translation unit(s) with a mismatched packed-gemm include, {lostKernel} with the wrong kernel choice")
                false))

/// The explicit K-lane vector fold forms (review M9 / M14; BLADE_FOLD_LANES):
/// a `prodsum` over two rank-1 operands, a self-dot, three operands; the
/// deferred `reduce(method_for(zip(a, b)) <@> lambda(x, y) -> x * y, (+))`
/// (the dot kernel); the same under a `where omp` fold (Path B, the threaded
/// vector arm); a non-dot-class `where omp` body (the simd form per chunk);
/// and a comm-declared user kernel (the serial chain per chunk, with why).
let private planFoldLanesProdsumSrc =
    "type Ix = Idx<37>\n"
    + "let a = method_for(range<Ix>) <@> lambda(i) -> Float64(Int64(i) % 7 - 3) |> compute\n"
    + "let b = method_for(range<Ix>) <@> lambda(i) -> Float64(Int64(i) % 5 - 2) |> compute\n"
    + "let c = method_for(range<Ix>) <@> lambda(i) -> Float64(Int64(i) % 3 - 1) |> compute\n"
    + "let dot_ab = prodsum(a, b)\n"
    + "let self_a = prodsum(a, a)\n"
    + "let triple = prodsum(a, b, c)\n"

let private planFoldLanesDotSrc =
    "type Ix = Idx<37>\n"
    + "let a = method_for(range<Ix>) <@> lambda(i) -> Float64(Int64(i) % 7 - 3) |> compute\n"
    + "let b = method_for(range<Ix>) <@> lambda(i) -> Float64(Int64(i) % 5 - 2) |> compute\n"
    + "let dot_ab = reduce(method_for(zip(a, b)) <@> lambda(x, y) -> x * y, (+))\n"
    + "let self_a = reduce(method_for(a) <@> lambda(w) -> w * w, (+))\n"
    + "let not_dot = reduce(method_for(zip(a, b)) <@> lambda(x, y) -> x * y + 1.0, (+))\n"

let private planFoldLanesThreadedSrc =
    "function myAdd(a: Float64, b: Float64) where comm(a, b), omp = (a + b) * 1.0\n"
    + "type Ix = Idx<1001>\n"
    + "let a = method_for(range<Ix>) <@> lambda(i) -> Float64(Int64(i) % 7 - 3) |> compute\n"
    + "let b = method_for(range<Ix>) <@> lambda(i) -> Float64(Int64(i) % 5 - 2) |> compute\n"
    + "let dot_ab = reduce(method_for(zip(a, b)) <@> lambda(x, y) -> x * y, lambda(p, q) where omp -> p + q, 0.0)\n"
    + "let chain = reduce(method_for(a) <@> lambda(v) -> (v + 1.0) * v, lambda(p, q) where omp -> p + q, 0.0)\n"
    + "let comm_kernel = reduce(method_for(zip(a, b)) <@> lambda(x, y) -> x * y, myAdd, 0.0)\n"

// --- THE ROW ORBIT ACTION (round 3, D-P2 / gap G73) ------------------------
// `prelude` follows the declarations, `between` sits between the weight `v` and the action
// `u`, whose cell term is `body` and whose fold is `kernSeed`. `bump` writes
// a cell of the pool through a `mut` parameter (an intervening writer).
let private planActionSrcWith (prelude: string) (between: string) (body: string) (kernSeed: string) =
    "let static p = 13\ntype Var = Idx<p>\ntype Obs = Idx<3>\n"
    + "function radd(a: Float64, b: Float64) where repro -> Float64 = a + b\n"
    + "function bump(a: mut Array<Float64 like SymIdx<2, Var>>) -> Int64 = {\n    a(0, 1) = 2.5\n    7\n}\n"
    + prelude
    + "let x = method_for(range<Var>, range<Obs>) <@> lambda(i, s) -> Float64((i * 3 + s * 5) % 7) - 2.0 |> compute\n"
    + "let mut g = method_for(x, x) <@> lambda(a: Array<Float64 like Obs>, b: Array<Float64 like Obs>) where comm(a, b) -> prodsum(a, b) |> compute\n"
    + "let v = method_for(range<Var>) <@> lambda(i) -> Float64((i * 5) % 7) - 3.0 |> compute\n"
    + between
    + $"let u = method_for(range<Var>) <@> lambda(i) -> reduce(method_for(range<Var>) <@> lambda(j) -> {body}, {kernSeed}) |> compute\n"
let private planActionSrc = planActionSrcWith "" "" "g(i, j) * v(j)" "(+), 0.75"

let private actionApplied (d: Blade.Effects.Decision) =
    d.Rule = "fold-form" && d.Outcome = Blade.Effects.Applied && d.Subject = "u"
    && d.Evidence |> List.exists (fun (e: string) -> e = "fold form: orbit action (rank 2, extent 13, 1 channel, row groups of 4): own / partner role terms; every row chain fed its source order from its seed")
    && d.Evidence |> List.exists (fun (e: string) -> e.Contains "its term evaluated TWICE -- row chain a with own = a, partner = b, row chain b with own = b, partner = a (never one weighted term at both endpoints)")
    && d.Evidence |> List.exists (fun (e: string) -> e.StartsWith "five-party audit (planner: Optimize.scheduleMayWriteIn over the module")

let private actionDeclined (needle: string) (d: Blade.Effects.Decision) =
    d.Rule = "fold-form" && declinedMentioning ("orbit action not taken: " + needle) d

/// The LLVM lane's OWN record of the action (EmitLlvm.tryBindOrbitAction,
/// tagged like its scatter records, so `blade plan` under BLADE_LLVM=1 merges
/// it in place of the C++ lane's) and its emitted pass.
let private planActionLlvmRecord () : bool =
    let name = "plan_orbit_action_llvm_lane_own_record"
    pullGates [ ("BLADE_FP_REPRO", "1"); ("BLADE_OMP_THREADS", "1") ] (fun () ->
        Blade.Effects.Decisions.start ()
        let r =
            try
                match lower planActionSrc with
                | Error e -> Error ($"lower: {e}")
                | Ok ir ->
                    match EmitLlvm.tryEmitProgramNamed "plan_case" ir with
                    | Error why -> Error ($"the llvm lane refused: {why}")
                    | Ok ll -> Ok (Blade.Effects.Decisions.drain (), ll)
            with ex -> Error ($"raised: {ex.Message}")
        Blade.Effects.Decisions.drain () |> ignore
        match r with
        | Error e -> resultLine Fail name e; false
        | Ok (ds, ll) ->
            let mine =
                ds |> List.filter (fun d ->
                    d.Rule = "fold-form" && d.Outcome = Blade.Effects.Applied
                    && d.Evidence |> List.contains Blade.OrbitFold.llvmScatterRecordTag
                    && d.Evidence |> List.exists (fun (e: string) -> e.StartsWith "fold form: orbit action (rank 2, extent 13, 1 channel")
                    && d.Evidence |> List.exists (fun (e: string) -> e.StartsWith "five-party audit (planner:"))
            if mine.Length = 1 && ll.Contains "; orbit action (rank 2, extent 13, 1 channel" then
                resultLine Pass name "the LLVM emitter's own applied record (tagged, with the five-party matrix) and its pass in the .ll"; true
            else
                let inLl = ll.Contains "; orbit action (rank 2"
                resultLine Fail name ($"{mine.Length} tagged applied action record(s); pass in .ll: {inLl}"); false)

/// THE LANE SPLIT on a thread request (Codex diff review of 719a2bce, finding
/// 1): with threads ENABLED and the map's `omp(i: 1)` request, the C++ lane
/// declines (`plan_orbit_action_declined_threads_on_request`, the old emitter's
/// row walk keeps the request) while the LLVM lane -- no thread runtime --
/// TAKES the serial action, its own record saying so.
let private planActionLlvmThreadedRequest () : bool =
    let name = "plan_orbit_action_llvm_threaded_request_taken"
    let src = planActionSrc.Replace("let u = method_for(range<Var>) <@> lambda(i) ->", "let u = method_for(range<Var>) <@> lambda(i) where omp(i: 1) ->")
    pullGates [ ("BLADE_FP_REPRO", "1"); ("BLADE_OMP_THREADS", null) ] (fun () ->
        Blade.Effects.Decisions.start ()
        let r =
            try
                match lower src with
                | Error e -> Error ($"lower: {e}")
                | Ok ir ->
                    match EmitLlvm.tryEmitProgramNamed "plan_case" ir with
                    | Error why -> Error ($"the llvm lane refused: {why}")
                    | Ok ll -> Ok (Blade.Effects.Decisions.drain (), ll)
            with ex -> Error ($"raised: {ex.Message}")
        Blade.Effects.Decisions.drain () |> ignore
        match r with
        | Error e -> resultLine Fail name e; false
        | Ok (ds, ll) ->
            let taken =
                ds |> List.exists (fun d ->
                    d.Rule = "fold-form" && d.Outcome = Blade.Effects.Applied
                    && d.Evidence |> List.contains Blade.OrbitFold.llvmScatterRecordTag
                    && d.Evidence |> List.exists (fun (e: string) -> e.StartsWith "fold form: orbit action (rank 2, extent 13, 1 channel")
                    && d.Evidence |> List.exists (fun (e: string) -> e = "a thread request on the map: this lane has no thread runtime -- the action is serial here as every fold in this lane"))
            if taken && ll.Contains "; orbit action (rank 2, extent 13, 1 channel" then
                resultLine Pass name "threads enabled, the map's request present: the LLVM lane TAKES the serial action and records that it has no thread runtime (the C++ lane declines: plan_orbit_action_declined_threads_on_request)"; true
            else
                let shown = ds |> List.filter (fun d -> d.Rule = "fold-form") |> List.map Blade.Effects.Decisions.render |> String.concat " | "
                resultLine Fail name ($"wanted the LLVM lane's applied action with the no-thread-runtime note; the record: {shown}"); false)

let private planActionCases () : bool list =
    let repro f = pullGates [ ("BLADE_FP_REPRO", "1"); ("BLADE_OMP_THREADS", "1") ] f
    [ repro (fun () ->
          planCase "plan_orbit_action_applied_repro" planActionSrc "fold-form"
              (fun d -> actionApplied d
                        && evidenceMentioning "one pass over the 91-cell SymIdx<2, 13> pool of `g` instead of 169 logical reads" d
                        && evidenceMentioning "weights `v` (partner)" d
                        && evidenceMentioning "every fold strict in this build (BLADE_FP_REPRO=1)" d
                        && evidenceMentioning "producer = the pool `g` stored at its own binding (a `|> compute`), 1 binding(s) between it and the action -- no write" d
                        && evidenceMentioning "weights = `v` stored at its own binding (a `|> compute`), 0 binding(s) between it and the action -- no write" d
                        && evidenceMentioning "parallel bodies = none (v1's action is serial" d)
              "the action applied under REPRO: form, role terms, counts, the five-party matrix")
      pullGates [ ("BLADE_FP_REPRO", null); ("BLADE_OMP_THREADS", "1") ] (fun () ->
          planCase "plan_orbit_action_applied_licensed_recorded" planActionSrc "fold-form"
              (fun d -> actionApplied d && evidenceMentioning "the fold carries a reorder licence in this build: the strict form is taken for the row action too" d)
              "the default (licensed) build takes the strict form for the row action, and says so")
      pullGates [ ("BLADE_FP_REPRO", null); ("BLADE_OMP_THREADS", "1"); ("BLADE_ORBIT_ACTIONS", "strict") ] (fun () ->
          planCase "plan_orbit_action_strict_knob_declines_licensed" planActionSrc "fold-form"
              (actionDeclined "BLADE_ORBIT_ACTIONS=strict: the fold carries a reorder licence in this non-repro build")
              "BLADE_ORBIT_ACTIONS=strict keeps the licensed walk in the default build")
      pullGates [ ("BLADE_FP_REPRO", "1"); ("BLADE_OMP_THREADS", "1"); ("BLADE_ORBIT_ACTIONS", "strict") ] (fun () ->
          planCase "plan_orbit_action_strict_knob_applies_repro" planActionSrc "fold-form" actionApplied
              "BLADE_ORBIT_ACTIONS=strict still takes the action in a strict build")
      pullGates [ ("BLADE_FP_REPRO", null); ("BLADE_OMP_THREADS", "1") ] (fun () ->
          planCase "plan_orbit_action_unlicensed_default" (planActionSrcWith "" "" "g(i, j) * v(j)" "radd, 0.75") "fold-form"
              (fun d -> actionApplied d && evidenceMentioning "the fold carries no reorder licence" d)
              "a `where repro` fold kernel in the default build: the strict form, no licence")
      pullGates [ ("BLADE_FP_REPRO", "1"); ("BLADE_ORBIT_ACTIONS", "0") ] (fun () ->
          planCase "plan_orbit_action_declined_knob" planActionSrc "fold-form"
              (actionDeclined "disabled by BLADE_ORBIT_ACTIONS=0") "BLADE_ORBIT_ACTIONS=0 keeps the row walk")
      // REFUSALS, each by its first failed condition (tests/corpus/symmetry/141 runs them)
      repro (fun () ->
          planCase "plan_orbit_action_declined_antisym"
              (planActionSrcWith "" "let an = method_for(x, x) <@> lambda(a: Array<Float64 like Obs>, b: Array<Float64 like Obs>) where anticomm(a, b) -> a((0 : Obs)) * b((1 : Obs)) - b((0 : Obs)) * a((1 : Obs)) |> compute\n" "an(i, j) * v(j)" "(+)")
              "fold-form" (actionDeclined "antisymmetric storage: a mirror cell reads -x and the diagonal +0 (signed events, v2)")
              "an AntisymIdx pool: signed events (v2)")
      repro (fun () ->
          planCase "plan_orbit_action_declined_two_contracted_axes"
              (planActionSrcWith "" "let t3 = method_for(x, x, x) <@> lambda(a: Array<Float64 like Obs>, b: Array<Float64 like Obs>, c: Array<Float64 like Obs>) where comm(a, b, c) -> prodsum(a, b, c) |> compute\n"
                   "reduce(method_for(range<Var>) <@> lambda(k) -> t3(i, j, k) * v(j) * v(k), (+))" "(+)")
              "fold-form" (actionDeclined "two contracted axes in one stage (the term folds a third level): ordered events need per-destination queues (v2)")
              "T(i, j, k) * v(j) * w(k): two contracted axes (v2)")
      repro (fun () ->
          planCase "plan_orbit_action_declined_weight_third_coordinate" (planActionSrcWith "" "" "g(i, j) * v((i + j) % 13)" "(+)") "fold-form"
              (actionDeclined "the term reads another array, or a weight at a subscript other than the row / partner coordinate")
              "a weight read at an index expression")
      repro (fun () ->
          planCase "plan_orbit_action_declined_second_order_read" (planActionSrcWith "" "" "g(i, j) * g(j, i)" "(+)") "fold-form"
              (actionDeclined "the term reads the compact array at more than one coordinate order, or reads a second compact array")
              "the pool read at both coordinate orders (the walk-forced twin's shape)")
      repro (fun () ->
          planCase "plan_orbit_action_declined_deferred_weight"
              (planActionSrcWith "" "let vlate = method_for(range<Var>) <@> lambda(i) -> Float64(i) * 0.5\n" "g(i, j) * vlate(j)" "(+)") "fold-form"
              (actionDeclined "a weight is not a stored array in scope at the action (a deferred weight would run its initializer inside the action)")
              "a deferred weight (its definition would run inside the action)")
      repro (fun () ->
          planCase "plan_orbit_action_declined_deferred_pool"
              (planActionSrcWith "" "let gd = method_for(x, x) <@> lambda(a: Array<Float64 like Obs>, b: Array<Float64 like Obs>) where comm(a, b) -> prodsum(a, b)\n" "gd(i, j) * v(j)" "(+)") "fold-form"
              (actionDeclined "the compact array is a deferred computation (no stored pool; P1-style fusion is v2)")
              "a deferred pool (v2)")
      repro (fun () ->
          planCase "plan_orbit_action_declined_per_row_seed" (planActionSrcWith "" "" "g(i, j) * v(j)" "(+), v(i)") "fold-form"
              (actionDeclined "the seed mentions the row coordinate (per-row seeds are v2)")
              "a per-row seed (v2)")
      repro (fun () ->
          planCase "plan_orbit_action_declined_coordinate_use" (planActionSrcWith "" "" "g(i, j) * Float64(j)" "(+)") "fold-form"
              (actionDeclined "the cell term is outside the closed total whitelist (it uses the row or partner coordinate beyond the pool and weight reads (v1: no other use of i, j))")
              "a coordinate used beyond the reads")
      repro (fun () ->
          planCase "plan_orbit_action_declined_partial_term" (planActionSrcWith "" "" "g(i, j) * Float64(Int64(floor(v(j))) / 2)" "(+)") "fold-form"
              (actionDeclined "the cell term is outside the closed total whitelist")
              "a term that may panic (integer / , a float -> int cast): outside the total whitelist")
      repro (fun () ->
          planCase "plan_orbit_action_declined_block_local"
              (planActionSrcWith "" "" "g(i, j) * v(j)" "(+)" + "let (w, q) = {\n    let q = 1.5\n    let w = method_for(range<Var>) <@> lambda(i) -> reduce(method_for(range<Var>) <@> lambda(j) -> g(i, j) * v(j), (+)) |> compute\n    (w, q)\n}\n")
              "fold-form" (actionDeclined "no five-party audit recorded for this action (v1 admits a TOP-LEVEL action binding")
              "a block-local action: no audit (v1's storage proof covers top-level bindings), unknown declines")
      pullGates [ ("BLADE_FP_REPRO", "1"); ("BLADE_OMP_THREADS", null) ] (fun () ->
          planCase "plan_orbit_action_declined_threads_on_request"
              ((planActionSrc).Replace("let u = method_for(range<Var>) <@> lambda(i) ->", "let u = method_for(range<Var>) <@> lambda(i) where omp(i: 1) ->")) "fold-form"
              (actionDeclined "a thread request on the map: this build emits threads and v1's action is serial -- the row walk keeps the request")
              "a map request in a threaded build: the walk threads the rows")
      repro (fun () ->
          planCase "plan_orbit_action_threads_off_request_census"
              ((planActionSrc).Replace("let u = method_for(range<Var>) <@> lambda(i) ->", "let u = method_for(range<Var>) <@> lambda(i) where omp(i: 1) ->")) "fold-form"
              (fun d -> actionApplied d && evidenceMentioning "[omp] requested but emitted serial: BLADE_OMP_THREADS=1" d && evidenceMentioning "(requested at: the map)" d)
              "BLADE_OMP_THREADS=1: the action is taken and the dropped request named")
      // D/5's mixed nest: a strict outer fold over a licensed inner fold of
      // weighted rows [1e16, -1e16, 1] (the ascending chain gives 1, a
      // regrouped one 0) -- refused in the default build by planStrict's rule;
      // under REPRO every level is strict and the scalar form is v1.1.
      pullGates [ ("BLADE_FP_REPRO", null); ("BLADE_OMP_THREADS", "1") ] (fun () ->
          planCase "plan_orbit_action_declined_mixed_nest"
              ("type Three = Idx<3>\nfunction radd(a: Float64, b: Float64) where repro -> Float64 = a + b\n"
               + "let ones = method_for(range<Three>) <@> lambda(i) -> 1.0 |> compute\n"
               + "let s1 = method_for(ones, ones) <@> lambda(a: Float64, b: Float64) where comm(a, b) -> a * b |> compute\n"
               + "let vmn = method_for(range<Three>) <@> lambda(i) -> if i == 0 then 1.0e16 else if i == 1 then -1.0e16 else 1.0 |> compute\n"
               + "let wmn = reduce(method_for(range<Three>) <@> lambda(i) -> reduce(method_for(range<Three>) <@> lambda(j) -> s1(i, j) * vmn(j), (+)), radd, 0.0)\n")
              "fold-form"
              (actionDeclined "a mixed nest: level 2's fold carries a reorder licence and level 1's does not (v1 keeps the walk; making the licensed level strict would change the default build's bits)")
              "a strict outer fold over a licensed inner fold of weighted rows: a mixed nest")
      repro (fun () ->
          planCase "plan_orbit_action_declined_scalar_form_v11"
              (planActionSrc + "let total = reduce(method_for(range<Var>) <@> lambda(i) -> reduce(method_for(range<Var>) <@> lambda(j) -> g(i, j) * v(j), radd, 0.0), radd, 0.0)\n")
              "fold-form" (actionDeclined "an outer fold over the weighted rows: v1 builds the row action's stored vector output only (the scalar form is v1.1)")
              "a strict walk folding weighted rows: the scalar form is v1.1")
      // THE FIVE-PARTY MATRIX's columns (writing parties decline)
      repro (fun () ->
          planCase "plan_orbit_action_declined_intervening_writer" (planActionSrcWith "" "let z = bump(g)\n" "g(i, j) * v(j)" "(+)") "fold-form"
              (actionDeclined "`z` may write between `g` and the action")
              "an intervening writer (a mutating call on the pool)")
      repro (fun () ->
          planCase "plan_orbit_action_declined_writing_weight_producer"
              (planActionSrcWith "type K = Idx<2>\nlet mut G: Array<Float64 like K> = method_for(range<K>) <@> lambda(k) -> 0.0 |> compute\nlet vw = method_for(range<Var>) <@> lambda(i) -> {\n    G((0 : K)) = G((0 : K)) + 1.0\n    Float64((i * 5) % 7) - 3.0\n} |> compute\n" ""
                   "g(i, j) * vw(j)" "(+)")
              "fold-form" (actionDeclined "the producer of `vw` may write")
              "a weight whose producer writes (the producer party)")
      repro (fun () ->
          planCase "plan_orbit_action_declined_writing_pool_producer"
              ((planActionSrcWith "type K = Idx<2>\nlet mut G: Array<Float64 like K> = method_for(range<K>) <@> lambda(k) -> 0.0 |> compute\n" ""
                   "g(i, j) * v(j)" "(+)").Replace("where comm(a, b) -> prodsum(a, b) |> compute", "where comm(a, b) -> {\n    G((0 : K)) = G((0 : K)) + 1.0\n    prodsum(a, b)\n} |> compute"))
              "fold-form" (actionDeclined "the producer of `g` may write")
              "a pool whose producer writes (the producer party)")
      repro (fun () ->
          planCase "plan_orbit_action_declined_assigned_free_scalar"
              (planActionSrcWith "let mut c = 0.5\nc = 0.75\n" "" "g(i, j) * v(j) + c" "(+)")
              "fold-form" (actionDeclined "`__expr1` may write between `c` and the action")
              "a free scalar assigned between its binding and the action (the intervening party)")
      repro (fun () ->
          planCase "plan_orbit_action_declined_writing_fold_kernel"
              (planActionSrcWith "type K = Idx<2>\nlet mut G: Array<Float64 like K> = method_for(range<K>) <@> lambda(k) -> 0.0 |> compute\n" ""
                   "g(i, j) * v(j)" "lambda(acc, z) where comm(acc, z) -> {\n    G((0 : K)) = G((0 : K)) + 1.0\n    acc + z\n}, 0.0")
              "fold-form" (actionDeclined "the fold kernel")
              "a writing fold kernel (outside the closed whitelist; the planner's fold-kernel column too)")
      repro (fun () ->
          planCase "plan_orbit_action_declined_writing_seed"
              (planActionSrcWith "type K = Idx<2>\nlet mut G: Array<Float64 like K> = method_for(range<K>) <@> lambda(k) -> 0.0 |> compute\nfunction seedf(q: mut Array<Float64 like K>) -> Float64 = {\n    q((0 : K)) = 1.0\n    0.0\n}\n" ""
                   "g(i, j) * v(j)" "(+), seedf(G)")
              "fold-form" (actionDeclined "the seed")
              "a seed that writes through a call") ]
      @ [ planActionLlvmRecord (); planActionLlvmThreadedRequest () ]

let private planCases () =
    let pinBlas (v: string) f =
        withGate "BLADE_BLAS" v (fun () -> withGate "OPENBLAS_DIR" null (fun () -> withGate "BLADE_BLAS_LINK" null f))
    [ planCase "plan_symmetric_storage_applied" planCommSameArraySrc "symmetric-storage" applied
          "packed storage for the comm covariance of one array"
      planCase "plan_triangular_iteration_applied" planCommSameArraySrc "triangular-iteration" applied
          "triangular iteration over the commuting levels"
      planCase "plan_symmetric_storage_declined_distinct" planCommDistinctSrc "symmetric-storage"
          (declinedMentioning "different arrays") "declined: the commuting positions hold different arrays"
      planCase "plan_symmetric_storage_applied_deferred_alias" planDeferredAliasSrc "symmetric-storage"
          (fun d -> applied d && evidenceMentioning "operands: g, g2" d)
          "packed storage over a top-level deferred alias class, operands named as written"
      planCase "plan_triangular_iteration_applied_deferred_alias" planDeferredAliasSrc "triangular-iteration"
          (fun d -> applied d && evidenceMentioning "speedup x2" d)
          "triangular iteration over the deferred alias pair"
      planCase "plan_symmetric_storage_declined_block_alias" planBlockDeferredAliasSrc "symmetric-storage"
          (fun d -> (declinedMentioning "different arrays") d && evidenceMentioning "operands: g, b" d)
          "declined: a block alias is a forcing copy, two arrays"
      // ---- stored aliases (plan 2.1 second step, rule `alias-identity v1`) ----
      planCase "plan_alias_identity_applied_stored" planStoredAliasSrc "alias-identity"
          (fun d -> applied d && d.Subject = "B" && evidenceMentioning "storage of `A`: an array literal" d
                    && evidenceMentioning "never written (module-wide, syntactic)" d
                    && evidenceMentioning "the read-only alias plan admits the same alias" d)
          "a never-written stored alias takes its root's identity; the storage plan agrees"
      planCase "plan_symmetric_storage_applied_stored_alias" planStoredAliasSrc "symmetric-storage"
          (fun d -> applied d && evidenceMentioning "operands: A, B" d && evidenceMentioning "identity-transparent aliases: B = A" d)
          "packed storage over a stored alias pair, the alias named"
      planCase "plan_alias_identity_applied_call_root" planStoredAliasCallSrc "alias-identity"
          (fun d -> applied d && d.Subject = "F" && evidenceMentioning "the call's reach (`mk`) is never written either" d
                    && evidenceMentioning "the read-only alias plan admits the same alias" d)
          "a call root: its reach joins the proof, and the storage plan admits it too"
      planCase "plan_transpose_alias_applied_call_root" planStoredAliasCallSrc "transpose-normal-form"
          (fun d -> applied d && d.Subject = "F" && evidenceMentioning "a call of `mk` (its reach is never written either)" d)
          "the read-only alias plan shares a call root's storage when its reach is never written"
      planCase "plan_alias_identity_declined_written" planStoredAliasWrittenSrc "alias-identity"
          (fun d -> (declinedMentioning "names `B` and holds an assignment") d)
          "declined: the alias is written later (symmetry/150)"
      planCase "plan_symmetric_storage_declined_written_stored_alias" planStoredAliasWrittenSrc "symmetric-storage"
          (declinedMentioning "different arrays") "declined: a written alias is a copy, two arrays"
      // Identity is a LANGUAGE fact: the storage gate changes the storage
      // decision only, never the identity or the packed layout.
      withGate "BLADE_TRANSPOSE_ELIDE" "0" (fun () ->
          planCase "plan_alias_identity_applied_gate_off" planStoredAliasSrc "alias-identity"
              (fun d -> applied d && d.Subject = "B") "gate off: the identity is unchanged")
      withGate "BLADE_TRANSPOSE_ELIDE" "0" (fun () ->
          planCase "plan_symmetric_storage_applied_stored_alias_gate_off" planStoredAliasSrc "symmetric-storage"
              (fun d -> applied d && evidenceMentioning "identity-transparent aliases: B = A" d)
              "gate off: still packed (both lanes read each operand by position)")
      withGate "BLADE_TRANSPOSE_ELIDE" "0" (fun () ->
          planCase "plan_transpose_alias_declined_gate_off" planStoredAliasSrc "transpose-normal-form"
              (fun d -> (declinedMentioning "disabled by BLADE_TRANSPOSE_ELIDE") d && d.Subject = "B")
              "gate off: the storage is copied")
      (match decisionsOfMulti storedAliasImportedSources with
       | Error e -> resultLine Fail "plan_alias_identity_declined_imported_module" e; false
       | Ok ds ->
           let mine = ds |> List.filter (fun d -> d.Rule = "alias-identity")
           let ok = mine |> List.exists (fun d -> d.Subject = "B" && (declinedMentioning "imports this module") d)
           resultLine (if ok then Pass else Fail) "plan_alias_identity_declined_imported_module"
               (if ok then "a module another module imports: its aliases keep their own identity (fail closed)"
                else "wanted a decline naming the importer; the record: " + (mine |> List.map Blade.Effects.Decisions.render |> String.concat " | "))
           ok)
      (match decisionsOfMulti storedAliasForeignCallSources with
       | Error e -> resultLine Fail "plan_alias_declined_foreign_call_root" e; false
       | Ok ds ->
           let pick rule = ds |> List.filter (fun d -> d.Rule = rule && d.Subject = "B")
           let tnf = pick "transpose-normal-form"
           let ai = pick "alias-identity"
           let ok =
               tnf |> List.exists (fun d -> match d.Outcome with Blade.Effects.Declined _ -> true | _ -> false)
               && not (tnf |> List.exists applied)
               && not (ai |> List.exists applied)
           resultLine (if ok then Pass else Fail) "plan_alias_declined_foreign_call_root"
               (if ok then "a call into another module returning its own (written) array: no shared storage, no shared identity"
                else "the record: " + ((tnf @ ai) |> List.map Blade.Effects.Decisions.render |> String.concat " | "))
           ok)
      withGate "BLADE_OMP_THREADS" null (fun () ->
          planCase "plan_omp_placement_applied" planOmpSrc "omp"
              (fun d -> applied d && evidenceMentioning "BLADE_OMP_PARALLEL_FOR" d)
              "omp applied, the placed construct in the evidence")
      withGate "BLADE_OMP_THREADS" "1" (fun () ->
          planCase "plan_omp_drop_declined" planOmpSrc "omp" (declinedMentioning "BLADE_OMP_THREADS")
              "omp declined, naming the BLADE_OMP_THREADS knob")
      pinBlas "0" (fun () ->
          planCase "plan_blas_route_declined_gate_off" (planMatmulSrc 20 16 110) "blas-routing"
              (declinedMentioning "BLAS gate off") "matmul declined: BLAS gate off")
      pinBlas "1" (fun () ->
          planCase "plan_blas_route_applied_gate_on" (planMatmulSrc 20 16 110) "blas-routing"
              (fun d -> applied d && evidenceMentioning "blade_matmul_d" d) "matmul routed to blade_matmul_d")
      pinBlas "0" (fun () ->
          planCase "plan_packed_gemm_applied" (planMatmulSrc 20 16 110) "microkernel"
              (fun d -> applied d && evidenceMentioning "packed gemm" d) "packed gemm above the crossover")
      pinBlas "0" (fun () ->
          planCase "plan_packed_gemm_declined_small" (planMatmulSrc 3 3 3) "microkernel"
              (declinedMentioning "crossover") "packed gemm declined below the crossover")
      pinBlas "0" (fun () ->
          planCase "plan_row_fold_jam_applied" planRowFoldSrc "microkernel"
              (fun d -> applied d && evidenceMentioning "row-fold jam" d) "row-fold jam over the rows")
      pinBlas "0" (fun () ->
          planCase "plan_triangular_gram_jam_applied" planGramSrc "microkernel"
              (fun d -> applied d && evidenceMentioning "triangular gram jam" d) "triangular gram jam")
      // The same-array comm row contraction above the syrk-tile crossover:
      // `blade plan` names the tile and its sample block, in the default AND
      // the repro build (the tile needs no reorder licence).
      pinBlas "0" (fun () ->
       withGate "BLADE_SYRK_TILE" null (fun () ->
          planCase "plan_syrk_tile_applied" planSyrkTileSrc "microkernel"
              (fun d -> applied d && evidenceMentioning "packed syrk tile" d && evidenceMentioning "KC=8" d)
              "packed syrk tile above the crossover"))
      pinBlas "0" (fun () ->
       withGate "BLADE_SYRK_TILE" null (fun () ->
          planCase "plan_syrk_tile_fold_form" planSyrkTileSrc "fold-form"
              (fun d -> applied d && evidenceMentioning "fold form: syrk tile 6x8, KC=8" d)
              "fold form: syrk tile 6x8, KC=8"))
      pinBlas "0" (fun () ->
       withGate "BLADE_SYRK_TILE" null (fun () ->
        withGate "BLADE_FP_REPRO" "1" (fun () ->
          planCase "plan_syrk_tile_applied_in_repro" planSyrkTileSrc "fold-form"
              (fun d -> applied d && evidenceMentioning "fold form: syrk tile 6x8" d)
              "the tile is ON under BLADE_FP_REPRO=1")))
      pinBlas "0" (fun () ->
       withGate "BLADE_SYRK_TILE" null (fun () ->
          planCase "plan_syrk_tile_declined_small" planCommSameArraySrc "microkernel"
              (declinedMentioning "syrk-tile crossover") "syrk tile declined below the crossover"))
      // The gram(A, A) NODE route takes the same tile, in both builds.
      for repro in [ null; "1" ] do
          pinBlas "0" (fun () ->
           withGate "BLADE_SYRK_TILE" null (fun () ->
            withGate "BLADE_FP_REPRO" repro (fun () ->
              planCase (if isNull repro then "plan_gram_node_syrk_tile" else "plan_gram_node_syrk_tile_repro")
                  planGramTileSrc "fold-form"
                  (fun d -> applied d && evidenceMentioning "fold form: syrk tile 6x8, KC=8" d
                            && evidenceMentioning "gram(A, A) node" d)
                  "gram(A, A): fold form: syrk tile 6x8, KC=8")))
      // The review's route shapes (bench/notes/microkernel-sketches-review.md (removed 2026-10-24; in git history at cf2028cc) M5):
      // ragged triangles and multi-K-block chains tile; a 13-row triangle keeps
      // the nest.
      for (p, n, kc) in [ (61, 257, 257); (97, 513, 257); (29, 1001, 334); (61, 31, 31) ] do
          pinBlas "0" (fun () ->
           withGate "BLADE_SYRK_TILE" null (fun () ->
              planCase $"plan_syrk_tile_route_{p}x{n}" (planSyrkSrc p n) "microkernel"
                  (fun d -> applied d && evidenceMentioning $"P={p} K={n} KC={kc}" d)
                  $"packed syrk tile at {p} x {n}, KC={kc}"))
      pinBlas "0" (fun () ->
       withGate "BLADE_SYRK_TILE" null (fun () ->
          planCase "plan_syrk_tile_route_13x31_declines" (planSyrkSrc 13 31) "microkernel"
              (declinedMentioning "syrk-tile crossover") "13 x 31 keeps the per-cell nest"))
      pinBlas "0" (fun () ->
       withGate "BLADE_SYRK_TILE" "0" (fun () ->
          planCase "plan_syrk_tile_declined_knob" planSyrkTileSrc "microkernel"
              (declinedMentioning "BLADE_SYRK_TILE") "syrk tile declined by BLADE_SYRK_TILE=0"))
      // The antisym pair (review M7) and the rank-3 simplex (M8): the review's
      // route shapes tile, the small ones keep the nest, the knobs restore it,
      // and the tiles are ON under BLADE_FP_REPRO=1 (no reorder licence used).
      for (p, n) in [ (61, 401); (61, 600) ] do
          withGate "BLADE_ANTISYM_TILE" null (fun () ->
              planCase $"plan_antisym_tile_route_{p}x{n}" (planAntiSrc p n) "fold-form"
                  (fun d -> applied d && evidenceMentioning "fold form: antisym tile sequential 6x8" d)
                  $"antisym tile at {p} x {n}")
      withGate "BLADE_ANTISYM_TILE" "4x4" (fun () ->
          planCase "plan_antisym_tile_paired_knob" (planAntiSrc 61 401) "fold-form"
              (fun d -> applied d && evidenceMentioning "fold form: antisym tile paired 4x4" d)
              "BLADE_ANTISYM_TILE=4x4 selects the paired schedule")
      // BLADE_TILE_TUNE (speed-only driver parameters): the antisym tile's
      // sample blocks and the syrk tile's row groups are named by the plan;
      // unset = whole chain / groups of 4.
      withGate "BLADE_ANTISYM_TILE" null (fun () ->
       withGate "BLADE_TILE_TUNE" "anti_kc=100" (fun () ->
          planCase "plan_antisym_tile_sample_blocks_knob" (planAntiSrc 61 401) "fold-form"
              (fun d -> applied d && evidenceMentioning "fold form: antisym tile sequential 6x8 (P pass, spill, Q pass), whole chain per tile in sample blocks of <= 100" d)
              "BLADE_TILE_TUNE anti_kc=100 blocks the chain inside the tile visit"))
      withGate "BLADE_ANTISYM_TILE" null (fun () ->
       withGate "BLADE_TILE_TUNE" null (fun () ->
          planCase "plan_antisym_tile_whole_chain_default" (planAntiSrc 61 401) "fold-form"
              (fun d -> applied d && evidenceMentioning "fold form: antisym tile sequential 6x8 (P pass, spill, Q pass), whole chain per tile" d
                        && not (evidenceMentioning "sample blocks" d))
              "unset: the whole chain in one block"))
      // Row-panel groups (BLADE_TILE_TUNE anti_g): forced, and the default
      // compile-time cache budget -- n = 401 fits 4 groups' A pairs plus the
      // active B pair in 256 KiB, a long chain (n = 1201) does not fit 2.
      withGate "BLADE_ANTISYM_TILE" null (fun () ->
       withGate "BLADE_TILE_TUNE" "anti_g=2" (fun () ->
          planCase "plan_antisym_tile_row_groups_knob" (planAntiSrc 61 401) "fold-form"
              (fun d -> applied d && evidenceMentioning "whole chain per tile, row groups of 2 (BLADE_TILE_TUNE anti_g=2)" d)
              "BLADE_TILE_TUNE anti_g=2 names the row group"))
      // The DEFAULT (unset) is PER REGIME (round 3, W2-0b, measured in the
      // wave-1 harness A/B): a SERIAL tile keeps G = 1, the previous order; a
      // TEAM-ELIGIBLE tile -- licence + thread emission + the driver's static
      // work cap (p * p * n) >> 20 >= 2 -- takes the cache-budget groups. The
      // cap is what the driver itself checks: 61 x 401 (cap 1) and 29 x 11
      // (cap 0) never form a team, 193 x 401 (cap 14) and 61 x 1201 (cap 4)
      // may. anti_g=0 / 1..8 force either regime's choice unconditionally.
      let serialBy (why: string) = Blade.CodeGenExprSupport.antisymSerialDefaultNote why
      let eligible (cap: int) = Blade.CodeGenExprSupport.antisymTeamEligibleNote (bigint cap)
      let seq6x8 = "fold form: antisym tile sequential 6x8 (P pass, spill, Q pass), whole chain per tile, "
      let antiDefault (name: string) (src: string) (threads: string) (rule: string) (want: Blade.Effects.Decision -> bool) (describe: string) =
          withGate "BLADE_ANTISYM_TILE" null (fun () ->
           withGate "BLADE_TILE_TUNE" null (fun () ->
            withGate "BLADE_OMP_THREADS" threads (fun () -> planCase name src rule want describe)))
      antiDefault "plan_antisym_tile_row_groups_default" (planAntiSrc 61 401) null "fold-form"
          (fun d -> applied d && evidenceMentioning (seq6x8 + serialBy Blade.CodeGenExprSupport.antisymNoTeamRequested) d)
          "unset, no omp licence (a serial tile in a threaded build): row groups of 1, the serial-tile default named"
      antiDefault "plan_antisym_tile_row_groups_default_team_eligible" (planAntiOmpSrc 193 401) null "fold-form"
          (fun d -> applied d && evidenceMentioning (seq6x8 + "row groups of 4 (cache budget 200 KiB of 256 KiB" + eligible 14 + ")") d)
          "unset, licence, threads on, 193 x 401 (static cap 14): row groups of 4 by the cache budget, the team-eligible default named"
      antiDefault "plan_antisym_tile_row_groups_default_team_eligible_micro_g4" (planAntiOmpSrc 193 401) null "microkernel"
          (fun d -> applied d && evidenceMentioning "P=193 N=401 components=(0,1) KC=0 G=4" d)
          "unset, team-eligible tile: the microkernel record carries G=4"
      antiDefault "plan_antisym_tile_row_groups_default_team_eligible_long_chain" (planAntiOmpSrc 61 1201) null "fold-form"
          (fun d -> applied d && evidenceMentioning ("row groups of 1 -- cache budget: B pair + 2 row panels = 376 KiB > 256 KiB" + eligible 4) d)
          "unset, team-eligible tile (cap 4), n = 1201: the cache budget falls to 1, the regime named"
      antiDefault "plan_antisym_tile_row_groups_default_cap_one_serial" (planAntiOmpSrc 61 401) null "fold-form"
          (fun d -> applied d && evidenceMentioning (seq6x8 + serialBy (Blade.CodeGenExprSupport.antisymNoTeamByWorkCap 1I)) d)
          "unset, licence, threads on, 61 x 401: static cap 1, so no team can form -- row groups of 1, the cause named"
      antiDefault "plan_antisym_tile_row_groups_default_cap_one_micro_g1" (planAntiOmpSrc 61 401) null "microkernel"
          (fun d -> applied d && evidenceMentioning "P=61 N=401 components=(0,1) KC=0 G=1" d)
          "unset, cap-1 tile: the microkernel record carries G=1"
      antiDefault "plan_antisym_tile_row_groups_default_cap_zero_serial" (planAntiOmpSrc 29 11) null "fold-form"
          (fun d -> applied d && evidenceMentioning (serialBy (Blade.CodeGenExprSupport.antisymNoTeamByWorkCap 0I)) d)
          "unset, licence, threads on, 29 x 11: static cap 0 -- row groups of 1"
      antiDefault "plan_antisym_tile_row_groups_default_threads_suppressed" (planAntiOmpSrc 193 401) "1" "fold-form"
          (fun d -> applied d && evidenceMentioning (seq6x8 + serialBy Blade.CodeGenExprSupport.antisymNoTeamRequested) d)
          "unset, licence, BLADE_OMP_THREADS=1 (no team requested), 193 x 401: row groups of 1, the serial-tile default"
      withGate "BLADE_ANTISYM_TILE" null (fun () ->
       withGate "BLADE_TILE_TUNE" "anti_g=1" (fun () ->
        withGate "BLADE_OMP_THREADS" null (fun () ->
          planCase "plan_antisym_tile_row_groups_forced_one_team_eligible" (planAntiOmpSrc 193 401) "fold-form"
              (fun d -> applied d && evidenceMentioning (seq6x8 + "row groups of 1 (BLADE_TILE_TUNE anti_g=1)") d)
              "anti_g=1 on a team-eligible tile forces the previous order")))
      withGate "BLADE_ANTISYM_TILE" null (fun () ->
       withGate "BLADE_TILE_TUNE" "anti_g=2" (fun () ->
        withGate "BLADE_OMP_THREADS" null (fun () ->
          planCase "plan_antisym_tile_row_groups_forced_two_cap_one" (planAntiOmpSrc 61 401) "fold-form"
              (fun d -> applied d && evidenceMentioning (seq6x8 + "row groups of 2 (BLADE_TILE_TUNE anti_g=2)") d)
              "anti_g=2 on a cap-1 tile: an explicit override is unconditional")))
      withGate "BLADE_ANTISYM_TILE" null (fun () ->
       withGate "BLADE_TILE_TUNE" "anti_g=0" (fun () ->
        withGate "BLADE_OMP_THREADS" "1" (fun () ->
          planCase "plan_antisym_tile_row_groups_forced_budget_serial" (planAntiOmpSrc 61 401) "fold-form"
              (fun d -> applied d && evidenceMentioning (seq6x8 + "row groups of 4 (cache budget 200 KiB of 256 KiB)") d)
              "anti_g=0 on a serial tile forces the cache budget")))
      withGate "BLADE_ANTISYM_TILE" null (fun () ->
       withGate "BLADE_TILE_TUNE" "anti_g=0" (fun () ->
          planCase "plan_antisym_tile_budget_auto" (planAntiSrc 61 401) "fold-form"
              (fun d -> applied d && evidenceMentioning "fold form: antisym tile sequential 6x8 (P pass, spill, Q pass), whole chain per tile, row groups of 4 (cache budget 200 KiB of 256 KiB)" d)
              "anti_g=0, n = 401: row groups of 4 by the cache budget"))
      withGate "BLADE_ANTISYM_TILE" null (fun () ->
       withGate "BLADE_TILE_TUNE" "anti_g=0" (fun () ->
          planCase "plan_antisym_tile_budget_auto_long_chain" (planAntiSrc 29 1201) "fold-form"
              (fun d -> applied d && evidenceMentioning "row groups of 1 -- cache budget: B pair + 2 row panels = 376 KiB > 256 KiB" d)
              "anti_g=0, n = 1201: row groups of 1, the budget named"))
      withGate "BLADE_ANTISYM_TILE" null (fun () ->
       withGate "BLADE_TILE_TUNE" "anti_g=0" (fun () ->
          planCase "plan_antisym_tile_budget_micro_records_g" (planAntiSrc 61 401) "microkernel"
              (fun d -> applied d && evidenceMentioning "P=61 N=401 components=(0,1) KC=0 G=4" d)
              "anti_g=0: the microkernel record carries G=4"))
      withGate "BLADE_ANTISYM_TILE" null (fun () ->
       withGate "BLADE_TILE_TUNE" null (fun () ->
          planCase "plan_antisym_tile_default_micro_records_g1" (planAntiSrc 61 401) "microkernel"
              (fun d -> applied d && evidenceMentioning "P=61 N=401 components=(0,1) KC=0 G=1" d)
              "unset: the microkernel record carries G=1"))
      pinBlas "0" (fun () ->
       withGate "BLADE_SYRK_TILE" null (fun () ->
        withGate "BLADE_TILE_TUNE" "syrk_g=2, anti_kc=0" (fun () ->
          planCase "plan_syrk_tile_row_groups_knob" planSyrkTileSrc "fold-form"
              (fun d -> applied d && evidenceMentioning "fold form: syrk tile 6x8, KC=8, row groups of 2" d)
              "BLADE_TILE_TUNE syrk_g=2 names the row group")))
      pinBlas "0" (fun () ->
       withGate "BLADE_SYRK_TILE" null (fun () ->
        withGate "BLADE_TILE_TUNE" null (fun () ->
          planCase "plan_syrk_tile_row_groups_default" planSyrkTileSrc "fold-form"
              (fun d -> applied d && evidenceMentioning "fold form: syrk tile 6x8, KC=8, row groups of 4" d)
              "unset: row groups of 4")))
      // The kernel's `/ d` tail is the last K block's store, not a post-pass:
      // the plan says so, in the nest arm and the gram node's.
      pinBlas "0" (fun () ->
       withGate "BLADE_SYRK_TILE" null (fun () ->
          planCase "plan_syrk_tile_fused_tail" planSyrkScaledSrc "fold-form"
              (fun d -> applied d && evidenceMentioning "fold form: syrk tile 6x8, KC=8" d
                        && evidenceMentioning "then / d per cell (fused into the last block's store)" d)
              "the syrk tile's `/ d` tail is fused into the last block's store"))
      // The gram(A, A) node's omp census (gap register G1 remnant): the
      // driver's team recorded as applied, or the BLADE_OMP_THREADS=1 decline
      // naming the knob -- never silent.
      pinBlas "0" (fun () ->
       withGate "BLADE_SYRK_TILE" null (fun () ->
        withGate "BLADE_OMP_THREADS" "1" (fun () ->
          planCase "plan_gram_node_syrk_tile_omp_census_declined" planGramTileSrc "omp"
              (fun d -> declinedMentioning "BLADE_OMP_THREADS" d && evidenceMentioning "gram node" d)
              "gram node: the tile driver's team declined by BLADE_OMP_THREADS=1, in the census")))
      pinBlas "0" (fun () ->
       withGate "BLADE_SYRK_TILE" null (fun () ->
        withGate "BLADE_OMP_THREADS" null (fun () ->
          planCase "plan_gram_node_syrk_tile_omp_census_applied" planGramTileSrc "omp"
              (fun d -> applied d && evidenceMentioning "inside the tile driver" d && evidenceMentioning "gram node" d)
              "gram node: the tile driver's team recorded as applied")))
      withGate "BLADE_ANTISYM_TILE" null (fun () ->
       withGate "BLADE_FP_REPRO" "1" (fun () ->
          planCase "plan_antisym_tile_applied_in_repro" (planAntiSrc 61 401) "fold-form"
              (fun d -> applied d && evidenceMentioning "fold form: antisym tile" d)
              "the antisym tile is ON under BLADE_FP_REPRO=1"))
      withGate "BLADE_ANTISYM_TILE" null (fun () ->
          planCase "plan_antisym_tile_route_23x11_declines" (planAntiSrc 23 11) "microkernel"
              (declinedMentioning "antisym-tile crossover") "23 x 11 keeps the per-cell nest")
      withGate "BLADE_ANTISYM_TILE" "0" (fun () ->
          planCase "plan_antisym_tile_declined_knob" (planAntiSrc 61 401) "microkernel"
              (declinedMentioning "BLADE_ANTISYM_TILE") "antisym tile declined by BLADE_ANTISYM_TILE=0")
      for (p, n, kc) in [ (29, 67, 67); (29, 201, 201); (61, 11, 11); (61, 201, 201); (29, 700, 234) ] do
          withGate "BLADE_SIMPLEX_TILE" null (fun () ->
              planCase $"plan_simplex_tile_route_{p}x{n}" (planSym3Src p n) "fold-form"
                  (fun d -> applied d && evidenceMentioning $"fold form: simplex tile 6x8 with W panel, KC={kc}, live-accumulator edges, then / d per cell" d)
                  $"simplex tile at {p} x {n}, KC={kc}")
      // The edge tiles' kloop (BLADE_TILE_TUNE tri_edge): the plan names it
      // in both settings, in the simplex and the syrk arms.
      withGate "BLADE_SIMPLEX_TILE" null (fun () ->
       withGate "BLADE_TILE_TUNE" "tri_edge=0" (fun () ->
          planCase "plan_simplex_tile_full_kloop_edges_knob" (planSym3Src 29 67) "fold-form"
              (fun d -> applied d && evidenceMentioning "fold form: simplex tile 6x8 with W panel, KC=67, full-kloop edges (BLADE_TILE_TUNE tri_edge=0)" d)
              "BLADE_TILE_TUNE tri_edge=0 names the full kloop"))
      pinBlas "0" (fun () ->
       withGate "BLADE_SYRK_TILE" null (fun () ->
        withGate "BLADE_TILE_TUNE" null (fun () ->
          planCase "plan_syrk_tile_live_edges_default" planSyrkTileSrc "fold-form"
              (fun d -> applied d && evidenceMentioning "fold form: syrk tile 6x8, KC=8, row groups of 4, live-accumulator edges" d)
              "unset: live-accumulator edges")))
      withGate "BLADE_SIMPLEX_TILE" null (fun () ->
       withGate "BLADE_FP_REPRO" "1" (fun () ->
          planCase "plan_simplex_tile_applied_in_repro" (planSym3Src 29 67) "fold-form"
              (fun d -> applied d && evidenceMentioning "fold form: simplex tile" d)
              "the simplex tile is ON under BLADE_FP_REPRO=1"))
      for (p, n) in [ (13, 11); (13, 201) ] do
          withGate "BLADE_SIMPLEX_TILE" null (fun () ->
              planCase $"plan_simplex_tile_route_{p}x{n}_declines" (planSym3Src p n) "microkernel"
                  (declinedMentioning "simplex-tile crossover") $"{p} x {n} keeps the per-cell nest")
      withGate "BLADE_SIMPLEX_TILE" "0" (fun () ->
          planCase "plan_simplex_tile_declined_knob" (planSym3Src 29 67) "microkernel"
              (declinedMentioning "BLADE_SIMPLEX_TILE") "simplex tile declined by BLADE_SIMPLEX_TILE=0")
      // The explicit K-lane vector fold forms (review M9 / M14): the plan
      // names the form and K at every site, in every mode of BLADE_FOLD_LANES,
      // and the repro build keeps the serial chain.
      withGate "BLADE_FOLD_LANES" null (fun () ->
          planCase "plan_fold_lanes_prodsum_dot" planFoldLanesProdsumSrc "fold-form"
              (fun d -> applied d && d.Subject = "dot_ab" && evidenceMentioning "fold form: simd K-lanes(8) dot" d)
              "prodsum(a, b): simd K-lanes(8) dot")
      withGate "BLADE_FOLD_LANES" null (fun () ->
          planCase "plan_fold_lanes_prodsum_self_dot" planFoldLanesProdsumSrc "fold-form"
              (fun d -> applied d && d.Subject = "self_a" && evidenceMentioning "fold form: simd K-lanes(8) self-dot" d)
              "prodsum(a, a): simd K-lanes(8) self-dot (one load per element)")
      withGate "BLADE_FOLD_LANES" null (fun () ->
          planCase "plan_fold_lanes_prodsum_dot3" planFoldLanesProdsumSrc "fold-form"
              (fun d -> applied d && d.Subject = "triple" && evidenceMentioning "fold form: simd K-lanes(8) dot3" d)
              "prodsum(a, b, c): simd K-lanes(8) dot3")
      withGate "BLADE_FOLD_LANES" "4" (fun () ->
          planCase "plan_fold_lanes_prodsum_k4" planFoldLanesProdsumSrc "fold-form"
              (fun d -> applied d && d.Subject = "dot_ab" && evidenceMentioning "fold form: simd K-lanes(4) dot" d)
              "BLADE_FOLD_LANES=4 forces K = 4")
      withGate "BLADE_FOLD_LANES" "0" (fun () ->
          planCase "plan_fold_lanes_prodsum_knob_off" planFoldLanesProdsumSrc "fold-form"
              (fun d -> applied d && d.Subject = "dot_ab" && evidenceMentioning "fold form: simd-reduction" d)
              "BLADE_FOLD_LANES=0 keeps the one-accumulator simd reduction")
      withGate "BLADE_FP_REPRO" "1" (fun () ->
          planCase "plan_fold_lanes_prodsum_repro" planFoldLanesProdsumSrc "fold-form"
              (fun d -> d.Subject = "dot_ab" && declinedMentioning "serial (repro)" d)
              "BLADE_FP_REPRO=1 keeps the serial chain")
      pinBlas "0" (fun () ->
       withGate "BLADE_FOLD_LANES" null (fun () ->
          planCase "plan_fold_lanes_reduce_dot" planFoldLanesDotSrc "fold-form"
              (fun d -> applied d && d.Subject = "dot_ab" && evidenceMentioning "fold form: simd K-lanes(8) dot" d)
              "reduce over zip(a, b) <@> x * y: simd K-lanes(8) dot"))
      pinBlas "0" (fun () ->
       withGate "BLADE_FOLD_LANES" null (fun () ->
          planCase "plan_fold_lanes_reduce_self_dot" planFoldLanesDotSrc "fold-form"
              (fun d -> applied d && d.Subject = "self_a" && evidenceMentioning "fold form: simd K-lanes(8) self-dot" d)
              "reduce over a <@> w * w: simd K-lanes(8) self-dot"))
      pinBlas "0" (fun () ->
       withGate "BLADE_FOLD_LANES" null (fun () ->
          planCase "plan_fold_lanes_reduce_not_dot_keeps_simd" planFoldLanesDotSrc "fold-form"
              (fun d -> applied d && d.Subject = "not_dot" && evidenceMentioning "fold form: simd-reduction" d)
              "a body that is not a bare product keeps the simd reduction"))
      pinBlas "0" (fun () ->
       withGate "BLADE_FOLD_LANES" null (fun () ->
        withGate "BLADE_OMP_THREADS" null (fun () ->
          planCase "plan_fold_lanes_threaded_dot" planFoldLanesThreadedSrc "fold-form"
              (fun d -> applied d && d.Subject = "dot_ab" && evidenceMentioning "fold form: threaded(Path B chunks, simd K-lanes(8) dot per chunk)" d)
              "a `where omp` dot: Path B chunks, the vector tree per chunk")))
      pinBlas "0" (fun () ->
       withGate "BLADE_FOLD_LANES" null (fun () ->
        withGate "BLADE_OMP_THREADS" null (fun () ->
          planCase "plan_fold_lanes_threaded_chain_simd_per_chunk" planFoldLanesThreadedSrc "fold-form"
              (fun d -> applied d && d.Subject = "chain" && evidenceMentioning "fold form: threaded(Path B chunks, simd-reduction within each chunk)" d)
              "a `where omp` non-dot body: Path B chunks, simd reduction per chunk")))
      pinBlas "0" (fun () ->
       withGate "BLADE_FOLD_LANES" null (fun () ->
        withGate "BLADE_OMP_THREADS" null (fun () ->
          planCase "plan_fold_lanes_threaded_comm_kernel_serial_per_chunk" planFoldLanesThreadedSrc "fold-form"
              (fun d -> applied d && d.Subject = "comm_kernel" && evidenceMentioning "serial within each chunk: the fold kernel's combine is a call" d)
              "a comm-declared kernel: Path B chunks, the serial chain per chunk, with the reason")))
      pinBlas "0" (fun () ->
       withGate "BLADE_FOLD_LANES" "0" (fun () ->
        withGate "BLADE_OMP_THREADS" null (fun () ->
          planCase "plan_fold_lanes_threaded_knob_off" planFoldLanesThreadedSrc "fold-form"
              (fun d -> applied d && d.Subject = "dot_ab" && evidenceMentioning "serial within each chunk: BLADE_FOLD_LANES=0/off" d)
              "BLADE_FOLD_LANES=0: the serial chain per chunk, as before M14")))
      pinBlas "0" (fun () ->
       withGate "BLADE_FOLD_LANES" null (fun () ->
        withGate "BLADE_OMP_THREADS" "1" (fun () ->
          planCase "plan_fold_lanes_threads_off_dot" planFoldLanesThreadedSrc "fold-form"
              (fun d -> applied d && d.Subject = "dot_ab" && evidenceMentioning "fold form: simd K-lanes(8) dot" d)
              "BLADE_OMP_THREADS=1: the serial vector tree over the whole sweep")))
      // The orbit-class fold (src/OrbitFold.fs; bench M4): the logical sum
      // over a compact pool, regrouped by orbit class under the licence.
      // (the rank-2 orbit pass itself: compact row classes, which would
      // replace it behind the producer, is held off for these pins)
      orbitPass (fun () ->
      planCase "plan_orbit_fold_sym2_applied" (planOrbitSrc planOrbitSym2Body) "fold-form"
          (fun d -> applied d && evidenceMentioning "fold form: orbit-class fold (rank 2, 2 classes)" d && evidenceMentioning "weights 1/2" d)
          "orbit-class fold, rank 2, weights 1/2")
      orbitPass (fun () ->
      planCase "plan_orbit_fold_axes_spelling_applied" (planOrbitSrc "reduce(g, (+), axes = 2)") "fold-form"
          (fun d -> applied d && evidenceMentioning "orbit-class fold (rank 2" d)
          "`reduce(g, (+), axes = 2)` is the same orbit-class fold")
      planCase "plan_orbit_fold_sym3_applied" planOrbitSym3Src "fold-form"
          (fun d -> applied d && evidenceMentioning "fold form: orbit-class fold (rank 3, 3 classes)" d && evidenceMentioning "weights 1/3/6" d)
          "orbit-class fold, rank 3, weights 1/3/6"
      orbitPass (fun () ->
      planCase "plan_orbit_fold_antisym_even" planOrbitAntisymSrc "fold-form"
          (fun d -> applied d && evidenceMentioning "even kernel" d && evidenceMentioning "strict orbits weight 2" d)
          "strict pool, even kernel, weight 2")
      withGate "BLADE_FP_REPRO" "1" (fun () ->
          planCase "plan_orbit_fold_declined_repro" (planOrbitSrc planOrbitSym2Body) "fold-form"
              (declinedMentioning "BLADE_FP_REPRO=1") "BLADE_FP_REPRO=1 keeps the logical walk")
      planCase "plan_orbit_fold_declined_non_builtin" (planOrbitSrc (planOrbitNest "g(i, j)" "(+)" "lambda(a, b) -> b + a * 1.0, 0.0")) "fold-form"
          (declinedMentioning "not the builtin `+`") "a non-builtin outer fold keeps the logical walk"
      planCase "plan_orbit_fold_declined_inner_seed" (planOrbitSrc (planOrbitNest "g(i, j)" "(+), 1.0" "(+)")) "fold-form"
          (declinedMentioning "seed is not +0") "a non-zero inner seed keeps the logical walk"
      planCase "plan_orbit_fold_declined_coordinate" (planOrbitSrc (planOrbitNest "g(i, j) + Float64(i)" "(+)" "(+)")) "fold-form"
          (declinedMentioning "level coordinate beyond the read") "a coordinate used beyond the read keeps the logical walk"
      planCase "plan_orbit_fold_declined_fixed_read" (planOrbitSrc (planOrbitNest "g(i, j) + g(0, 0)" "(+)" "(+)")) "fold-form"
          (declinedMentioning "reads another array") "a second read at a fixed cell keeps the logical walk"
      planCase "plan_orbit_fold_declined_hermitian" planOrbitHermitianSrc "fold-form"
          (declinedMentioning "Hermitian storage") "a Hermitian pool (conjugate mirrors) keeps the logical walk"
      withGate "BLADE_ORBIT_FOLD" "0" (fun () ->
          planCase "plan_orbit_fold_declined_knob" (planOrbitSrc planOrbitSym2Body) "fold-form"
              (declinedMentioning "BLADE_ORBIT_FOLD=0") "BLADE_ORBIT_FOLD=0 keeps the logical walk")
      // THE ORDERED ORBIT SCATTER (OrbitFold.planStrict): the strict build's
      // form of the same walk -- applied under repro (the orbit fold's repro
      // decline is FOLLOWED by it), and in the default build where no level is
      // licensed; declined with the first failed condition.
      withGate "BLADE_FP_REPRO" "1" (fun () -> withGate "BLADE_OMP_THREADS" "1" (fun () ->
          planCase "plan_orbit_scatter_sym2_applied_repro" (planOrbitSrc planOrbitSym2Body) "fold-form"
              (fun d -> applied d && evidenceMentioning "fold form: ordered orbit scatter (rank 2, extent 7, row groups of 4)" d
                        && evidenceMentioning "one pass over the 28-cell SymIdx<2, 7> pool" d)
              "ordered orbit scatter, rank 2, row groups of 4"))
      withGate "BLADE_FP_REPRO" "1" (fun () ->
          planOrbitDeclineThenScatter "plan_orbit_fold_declined_repro_then_scatter" (planOrbitSrc planOrbitSym2Body))
      withGate "BLADE_FP_REPRO" "1" (fun () ->
          planCase "plan_orbit_scatter_sym3_applied_repro" planOrbitSym3Src "fold-form"
              (fun d -> applied d && evidenceMentioning "(rank 3, extent 7, row groups of 4, shared pair chains 28 of 49)" d)
              "ordered orbit scatter, rank 3, 28 shared pair chains of 49")
      withGate "BLADE_FP_REPRO" "1" (fun () -> withGate "BLADE_TILE_TUNE" "scatter_g=3,scatter_b=32" (fun () ->
          planCase "plan_orbit_scatter_tune" (planOrbitSrc planOrbitSym2Body) "fold-form"
              (fun d -> applied d && evidenceMentioning "row groups of 3" d && evidenceMentioning "column block 32" d)
              "BLADE_TILE_TUNE scatter_g / scatter_b reach the form"))
      planCase "plan_orbit_scatter_unlicensed_default" planScatterStrictSrc "fold-form"
          (fun d -> applied d && evidenceMentioning "ordered orbit scatter (rank 2" d && evidenceMentioning "no level's fold carries a reorder licence" d)
          "the default build: no level licensed -> the scatter"
      withGate "BLADE_FP_REPRO" "1" (fun () -> withGate "BLADE_ORBIT_FOLD" "0" (fun () ->
          planCase "plan_orbit_scatter_orbit_fold_knob_off" (planOrbitSrc planOrbitSym2Body) "fold-form"
              (fun d -> applied d && evidenceMentioning "ordered orbit scatter (rank 2" d
                        && evidenceMentioning "BLADE_ORBIT_FOLD=0 disables the licensed orbit-class fold only" d)
              "BLADE_ORBIT_FOLD=0 under repro: the scatter still applies, and says why"))
      withGate "BLADE_FP_REPRO" "1" (fun () -> withGate "BLADE_ORBIT_SCATTER" "0" (fun () ->
          planCase "plan_orbit_scatter_declined_knob" (planOrbitSrc planOrbitSym2Body) "fold-form"
              (declinedMentioning "ordered orbit scatter not taken: disabled by BLADE_ORBIT_SCATTER=0")
              "BLADE_ORBIT_SCATTER=0 keeps the walk"))
      withGate "BLADE_FP_REPRO" "1" (fun () ->
          planCase "plan_orbit_scatter_declined_antisym" planOrbitAntisymSrc "fold-form"
              (declinedMentioning "ordered orbit scatter not taken: antisymmetric storage")
              "an AntisymIdx pool keeps the walk (census line)")
      planCase "plan_orbit_scatter_declined_mixed_licence" (planScatterRadd + planOrbitSrc (planOrbitNest "g(i, j) * g(i, j)" "(+)" "radd, 0.0")) "fold-form"
          (declinedMentioning "ordered orbit scatter not taken: a mixed nest: level 2's fold carries a reorder licence and level 1's does not")
          "a licensed inner level under a strict outer one keeps the walk in v1"
      planCase "plan_orbit_scatter_declined_partial_kernel" planScatterPartialSrc "fold-form"
          (declinedMentioning "level 1's fold kernel is outside the scatter's total whitelist (body may panic (integer /))")
          "a fold kernel that may panic keeps the walk"
      withGate "BLADE_FP_REPRO" "1" (fun () ->
          planCase "plan_orbit_scatter_declined_seed_coordinate" (planOrbitSrc (planOrbitNest "g(i, j)" "(+), Float64(i)" "(+)")) "fold-form"
              (declinedMentioning "level 2's seed is outside the scatter's admission (it mentions a level coordinate")
              "an inner seed mentioning the outer coordinate keeps the walk")
      // THE ROW-OWNED PULL SCATTER (round 3, D-P3): rank 3, threaded strict
      // build, the request at the G35 gate -- applied with the five-party
      // matrix and the ownership plan; every pull-specific refusal named;
      // not in play (no record) without threads or without the request.
      pullGates [ ("BLADE_FP_REPRO", "1"); ("BLADE_OMP_THREADS", null) ] (fun () ->
          planCase "plan_orbit_pull_applied_repro" (planPullSrc 97 "") "fold-form"
              (fun d -> pullApplied d
                        && evidenceMentioning "(rank 3, extent 97, row groups of 4, shared pair chains 4753 of 9409), row-owned pull on a thread team" d
                        && evidenceMentioning "156849 slice cells + 152096 pulls = 308945 contiguous reads" d
                        && evidenceMentioning "f evaluated 308945 times (the serial scatter: 156849" d
                        && evidenceMentioning "ownership plan: 97 H rows claimed one at a time in ascending order" d
                        && evidenceMentioning "producer = `t` STORED at its own binding before the fold" d
                        && evidenceMentioning "intervening = 0 binding(s)" d)
              "the pull applied: form, counts (f evaluations NOT shared), ownership plan, five-party matrix")
      pullGates [ ("BLADE_FP_REPRO", "1"); ("BLADE_OMP_THREADS", null) ] (fun () ->
          planCase "plan_orbit_pull_omp_record" (planPullSrc 97 "") "omp"
              (fun d -> applied d && evidenceMentioning "omp parallel num_threads(T) + ascending row claims + team-completion barrier" d)
              "omp applied: the pull's team (ascending row claims, team-completion barrier)")
      pullGates [ ("BLADE_FP_REPRO", "1"); ("BLADE_OMP_THREADS", null) ] (fun () ->
          planCase "plan_orbit_pull_intervening_reader_admitted" (planPullSrc 97 "let probe = t(1, 2, 3)\n") "fold-form"
              (fun d -> pullApplied d && evidenceMentioning "intervening = 1 binding(s)" d)
              "a binding that only READS the pool in between is admitted (and counted)")
      pullGates [ ("BLADE_FP_REPRO", "1"); ("BLADE_OMP_THREADS", null) ] (fun () ->
          planCase "plan_orbit_pull_declined_intervening_writer" (planPullSrc 97 "let z = bump(t)\n") "fold-form"
              (declinedMentioning "row-owned pull scatter not taken: `z` may write between `t`'s materialization and the fold")
              "an intervening writer (a mutating call) declines")
      pullGates [ ("BLADE_FP_REPRO", "1"); ("BLADE_OMP_THREADS", null) ] (fun () ->
          planCase "plan_orbit_pull_declined_side_table_writer" (planPullSrcWith "import rand as r\n" 97 "let noise = r.uniform(bump(t), 1)\n" "(+)") "fold-form"
              (declinedMentioning "row-owned pull scatter not taken: `noise` may write between `t`'s materialization and the fold")
              "an intervening writer hidden in a random fill's key (its side-table payload) declines")
      pullGates [ ("BLADE_FP_REPRO", "1"); ("BLADE_OMP_THREADS", null); ("BLADE_TILE_TUNE", "pull_min=0") ] (fun () ->
          planCase "plan_orbit_pull_declined_writing_producer"
              ("type K = Idx<2>\nlet mut G: Array<Float64 like K> = method_for(range<K>) <@> lambda(k) -> 0.0 |> compute\n"
               + (planPullSrc 13 "").Replace("where comm(a, b, c) -> prodsum(a, b, c) |> compute", "where comm(a, b, c) -> {\n    G((0 : K)) = G((0 : K)) + 1.0\n    prodsum(a, b, c)\n} |> compute"))
              "fold-form"
              (declinedMentioning "row-owned pull scatter not taken: the pool's producer `t` may write")
              "a writing producer declines (the producer party)")
      pullGates [ ("BLADE_FP_REPRO", "1"); ("BLADE_OMP_THREADS", null); ("BLADE_ORBIT_PULL", "0") ] (fun () ->
          planCase "plan_orbit_pull_declined_knob" (planPullSrc 97 "") "fold-form"
              (declinedMentioning "row-owned pull scatter not taken: disabled by BLADE_ORBIT_PULL=0")
              "BLADE_ORBIT_PULL=0 declines (the other strict forms decide)")
      pullGates [ ("BLADE_FP_REPRO", "1"); ("BLADE_OMP_THREADS", null) ] (fun () ->
          planCase "plan_orbit_pull_declined_gate" (planPullSrc 13 "") "fold-form"
              (declinedMentioning "455 slice cells + 364 pulls = 819 pool reads below pull_min = 262144")
              "below its provisional work gate the pull declines")
      pullGates [ ("BLADE_FP_REPRO", "1"); ("BLADE_OMP_THREADS", null); ("BLADE_TILE_TUNE", "pull_min=0") ] (fun () ->
          planCase "plan_orbit_pull_gate_tunable" (planPullSrc 13 "") "fold-form"
              pullApplied "BLADE_TILE_TUNE pull_min=0 admits p = 13")
      pullGates [ ("BLADE_FP_REPRO", "1"); ("BLADE_OMP_THREADS", null) ] (fun () ->
          pullCase "plan_orbit_pull_inner_request_census" (planPullSrcWith "" 97 "" "lambda(a, b) where omp -> a + b, 0.0")
              (fun ds ->
                  ds |> List.exists pullApplied
                  && ds |> List.exists (fun d ->
                         d.Rule = "omp"
                         && (match d.Outcome with
                             | Blade.Effects.Declined why -> why.Contains "the row-owned pull threads the leaf chains under the OUTER level's request only" && why.Contains "requested at: level 3's fold"
                             | _ -> false)))
              "an inner level's request is not spent: the census names it (no licence moves between levels)")
      pullGates [ ("BLADE_FP_REPRO", "1"); ("BLADE_OMP_THREADS", "1") ] (fun () ->
          pullCase "plan_orbit_pull_not_in_play_threads_off" (planPullSrc 97 "")
              (fun ds -> not (ds |> List.exists mentionsPull)
                         && ds |> List.exists (fun d -> applied d && evidenceMentioning "fold form: ordered orbit scatter (rank 3, extent 97" d))
              "BLADE_OMP_THREADS=1: no pull record; the serial scatter")
      pullGates [ ("BLADE_FP_REPRO", "1"); ("BLADE_OMP_THREADS", null) ] (fun () ->
          pullCase "plan_orbit_pull_not_in_play_without_request" ((planPullSrc 97 "").Replace("lambda(u, v) where omp -> u + v, 0.0)", "(+), 0.0)"))
              (fun ds -> not (ds |> List.exists mentionsPull))
              "no outer request: no pull record")
      // Codex diff review of W2-3, finding 3: the producer's SIDE-TABLE
      // payload (constructed IR), a higher-order callback writer in a would-be
      // team body (beside a pure control that pulls), and the earlier-top-
      // level-pool scope (admitted; an intervening top-level writer declines).
      pullSideTableProducerCase ()
      pullCallbackWriterCase ()
      pullGates [ ("BLADE_FP_REPRO", "1"); ("BLADE_OMP_THREADS", null) ] (fun () ->
          planCase "plan_orbit_pull_top_level_pool_in_block_admitted" (planPullBlockSrc "") "fold-form"
              (fun d -> pullApplied d
                        && evidenceMentioning "producer = `t` STORED at its own binding before the fold" d
                        && evidenceMentioning "intervening = 1 binding(s)" d)
              "a top-level pool read by a walk in a later binding's block: audited by its payload, the block's statements intervening; the pull applies")
      pullGates [ ("BLADE_FP_REPRO", "1"); ("BLADE_OMP_THREADS", null) ] (fun () ->
          planCase "plan_orbit_pull_top_level_pool_intervening_writer_declined" (planPullBlockSrc "let z = bump(t)\n") "fold-form"
              (declinedMentioning "row-owned pull scatter not taken: `z` may write between `t`'s materialization and the fold")
              "a top-level writer between the pool and the block declines")
      // The other columns of the matrix, each refused BEFORE the pull is in
      // play (and so never taken): a WRITING outer kernel is not
      // reorder-licensed (G41: no request gate), and a seed that writes
      // through a call is outside the scatter's closed whitelist.
      pullGates [ ("BLADE_FP_REPRO", "1"); ("BLADE_OMP_THREADS", null) ] (fun () ->
          pullCase "plan_orbit_pull_writing_outer_kernel_never_pulled"
              ("type K = Idx<2>\nlet mut G: Array<Float64 like K> = method_for(range<K>) <@> lambda(k) -> 0.0 |> compute\n"
               + (planPullSrc 97 "").Replace("lambda(u, v) where omp -> u + v, 0.0)", "lambda(u, v) where comm(u, v) -> {\n    G((0 : K)) = G((0 : K)) + 1.0\n    u + v\n}, 0.0)").Replace("reduce(method_for(range<Var>) <@> lambda(i) ->", "reduce(method_for(range<Var>) <@> lambda(i) where omp(i: 1) ->"))
              (fun ds -> not (ds |> List.exists pullApplied))
              "a writing outer fold kernel: no pull (no licence, so no request gate)")
      pullGates [ ("BLADE_FP_REPRO", "1"); ("BLADE_OMP_THREADS", null) ] (fun () ->
          pullCase "plan_orbit_pull_writing_seed_never_pulled"
              ("type K = Idx<2>\nlet mut G: Array<Float64 like K> = method_for(range<K>) <@> lambda(k) -> 0.0 |> compute\nfunction seedf(g: mut Array<Float64 like K>) -> Float64 = {\n    g((0 : K)) = 1.0\n    0.0\n}\n"
               + (planPullSrc 97 "").Replace("lambda(u, v) where omp -> u + v, 0.0)", "lambda(u, v) where omp -> u + v, seedf(G))"))
              (fun ds -> not (ds |> List.exists pullApplied)
                         && ds |> List.exists (declinedMentioning "ordered orbit scatter not taken: level 1's seed is outside the scatter's admission"))
              "a seed that writes through a call: the scatter declines (closed whitelist), so no pull")
      pullGates [ ("BLADE_FP_REPRO", null); ("BLADE_OMP_THREADS", null) ] (fun () ->
          pullCase "plan_orbit_pull_not_in_default_build" (planPullSrc 97 "")
              (fun ds -> not (ds |> List.exists mentionsPull))
              "the default (licensed) build: no pull record (the licensed forms own the walk)")
      // THE ROW ORBIT ACTION (round 3, D-P2 / gap G73): applied (repro and
      // licensed, recorded), the knob's three settings, every refusal by its
      // first failed condition, and the five-party matrix's columns.
      yield! planActionCases ()
      // THE THREADED FORM (bench G32): `where omp` on the outer fold is spent
      // as a team over the first-coordinate rows -- in the fold-form line and
      // as an applied `omp` record -- and BLADE_OMP_THREADS=1 keeps the
      // serial pass with the census naming the knob.
      orbitPass (fun () -> withGate "BLADE_OMP_THREADS" null (fun () ->
          planCase "plan_orbit_fold_threaded" (planOrbitSrc planOrbitSym2Body) "fold-form"
              (fun d -> applied d && evidenceMentioning "orbit-class fold (rank 2, 2 classes); thread team over the first-coordinate rows" d)
              "the omp-licensed orbit fold threads its first-coordinate rows"))
      orbitPass (fun () -> withGate "BLADE_OMP_THREADS" null (fun () ->
          planCase "plan_orbit_fold_threaded_omp_record" (planOrbitSrc planOrbitSym2Body) "omp"
              (fun d -> applied d && evidenceMentioning "orbit-class fold: first-coordinate rows" d)
              "omp applied: the orbit fold's thread team"))
      orbitPass (fun () -> withGate "BLADE_OMP_THREADS" "1" (fun () ->
          planCase "plan_orbit_fold_threads_suppressed" (planOrbitSrc planOrbitSym2Body) "fold-form"
              (fun d -> applied d && not (evidenceMentioning "thread team" d))
              "BLADE_OMP_THREADS=1: the serial orbit pass"))
      orbitPass (fun () -> withGate "BLADE_OMP_THREADS" "1" (fun () ->
          planCase "plan_orbit_fold_threads_suppressed_census" (planOrbitSrc planOrbitSym2Body) "omp"
              (declinedMentioning "BLADE_OMP_THREADS") "omp declined, naming BLADE_OMP_THREADS"))
      planCase "plan_orbit_fold_unlicensed_serial" planOrbitSym3Src "fold-form"
          (fun d -> applied d && not (evidenceMentioning "thread team" d)) "no omp request: the serial orbit pass"
      (match orbitPass (fun () -> withGate "BLADE_OMP_THREADS" null (fun () -> cppOfSource "plan_orbit_fold_threaded_cpp" (planOrbitSrc planOrbitSym2Body))) with
       | Error e -> resultLine Fail "orbit_fold_cpp_threaded_rows" e; false
       | Ok cpp ->
           // Each row finds its own pool offset (no running counter across
           // threads), and the per-thread sums join in thread order.
           let wants = [ "BLADE_OMP_PARALLEL_NUM_THREADS(__oT)"
                         "size_t __oo = __ototal - nested_array_utilities::canon_count((size_t)7 - __oc1, 2, false);"
                         "__oa1 = __oa1 + __opart[(size_t)__ot * 2 + 1];"
                         "const double __ot1 = __oa1 + __oa1;" ]
           let missing = wants |> List.filter (fun w -> not (cpp.Contains w))
           if missing.IsEmpty then
               resultLine Pass "orbit_fold_cpp_threaded_rows" "closed-form row offsets, thread-order combine, addition-only class combine"; true
           else
               resultLine Fail "orbit_fold_cpp_threaded_rows" ($"missing {missing}"); false)
      planCase "plan_orbit_fold_combine_is_additions" planOrbitSym3Src "fold-form" applied "applied (C++ text checked below)"
      (match cppOfSource "plan_orbit_fold_cpp" planOrbitSym3Src with
       | Error e -> resultLine Fail "orbit_fold_cpp_addition_combine" e; false
       | Ok cpp ->
           // rep(S3, 3) = (S3 + S3) + S3 and rep(S6, 6) = 3 S6 + 3 S6, as additions:
           let wants = [ "__opool = nested_array_utilities::pool_base(t.data)"
                         "const double __ot1 = __oa1 + __oa1;"
                         "const double __ot2 = __ot1 + __oa1;" ]
           let missing = wants |> List.filter (fun w -> not (cpp.Contains w))
           let products = [ "3.0 * __oa"; "6.0 * __oa"; "2.0 * __oa" ] |> List.filter cpp.Contains
           if missing.IsEmpty && products.IsEmpty then
               resultLine Pass "orbit_fold_cpp_addition_combine" "pool walk + addition-only class combine, no weight products"; true
           else
               resultLine Fail "orbit_fold_cpp_addition_combine" ($"missing {missing}; products {products}"); false) ]

let private runCase (name: string) (src: string) (wantBreaks: int) (wantAborts: int) =
    match cppOfSource name src with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        let breaks = guardBreakCount cpp
        let aborts = bl8010Count cpp
        if breaks = wantBreaks && aborts = wantAborts then
            resultLine Pass name ($"{breaks} guard break(s), {aborts} abort(s)")
            true
        else
            resultLine Fail name ($"expected {wantBreaks} guard break(s) / {wantAborts} abort(s), got {breaks} / {aborts}")
            false

// ---------------------------------------------------------------------------
// IR validator reach (IRValidate.validateModule). Its walkers are folds over
// ExprShape / BinderShape, so a defect below ANY node is seen; these pins
// corrupt a lowered program in the three places the old hand-listed walkers
// were blind: a dangling reference under an unlisted node (IRGram), a
// module binding that reads a LATER binding (the scope used to hold every
// binding id), and an empty match in a FUNCTION body (checked for bindings
// only). The untouched program must still validate.
// ---------------------------------------------------------------------------

let private validatorSrc =
    "function f(x: Float64) -> Float64 = x + 1.0\n"
    + "let a: Array<Float64 like Idx<2>, Idx<2>> = [[1.0, 2.0], [3.0, 4.0]]\n"
    + "let b = f(2.0)\n"

let private validatorCase (name: string) (corrupt: Blade.IR.IRProgram -> Blade.IR.IRProgram) (needle: string option) =
    match lower validatorSrc with
    | Error e -> resultLine Fail name ($"lower: {e}"); false
    | Ok ir ->
        match needle, Blade.IRValidate.validateIR (corrupt ir) with
        | None, Ok _ -> resultLine Pass name "the program validates"; true
        | None, Error es -> resultLine Fail name (String.concat " | " es); false
        | Some n, Error es when es |> List.exists (fun m -> m.Contains n) ->
            resultLine Pass name ($"refused: {n}"); true
        | Some n, Error es -> resultLine Fail name ("wanted `" + n + "`, saw: " + String.concat " | " es); false
        | Some n, Ok _ -> resultLine Fail name ($"wanted `{n}`, but the corrupted program validated"); false

let private mapMain (f: Blade.IR.IRModule -> Blade.IR.IRModule) (p: Blade.IR.IRProgram) : Blade.IR.IRProgram =
    { p with Modules = p.Modules |> List.map f }

let private validatorPins () =
    let var (id, ty) = Blade.IR.IRVar (id, ty)
    [ validatorCase "validate_clean_program" id None
      // `a` rebuilt as gram(dangling, a): only a walker that descends IRGram sees v999999.
      validatorCase "validate_dangling_under_gram"
          (mapMain (fun m ->
              { m with
                  Bindings =
                      m.Bindings |> List.map (fun b ->
                          if b.Name = "b" then
                              let aB = m.Bindings |> List.find (fun x -> x.Name = "a")
                              { b with Value = Blade.IR.IRGram (var (999999, aB.Type), var (aB.Id, aB.Type), false) }
                          else b) }))
          (Some "dangling VarId reference: v999999")
      // `b` moved ahead of `a` and made to read it: a use before definition.
      validatorCase "validate_forward_binding_reference"
          (mapMain (fun m ->
              match m.Bindings |> List.tryFind (fun x -> x.Name = "a"), m.Bindings |> List.tryFind (fun x -> x.Name = "b") with
              | Some aB, Some bB ->
                  let bB' = { bB with Value = Blade.IR.IRExtent (var (aB.Id, aB.Type), 0) }
                  let rest = m.Bindings |> List.filter (fun x -> x.Name <> "b")
                  { m with Bindings = bB' :: rest }
              | _ -> m))
          (Some "dangling VarId reference")
      // f's body replaced by a zero-case match.
      validatorCase "validate_empty_match_in_function"
          (mapMain (fun m ->
              { m with
                  Functions =
                      m.Functions |> List.map (fun fn ->
                          if fn.Name = "f" then { fn with Body = Blade.IR.IRMatch (Blade.IR.IRLit (Blade.IR.IRLitFloat 0.0), []) }
                          else fn) }))
          (Some "empty match expression") ]

// ---------------------------------------------------------------------------
// Sibling-fold traversal sharing (Blade.Optimize.shareSiblingFoldsModule,
// rule `sibling-folds` v1, BLADE_SIBLING_FOLDS). The corpus
// (tests/corpus/sibling-folds) pins the VALUES; these pins read the emission
// (one loop over a and b in `main`, per-leg seeds) and each decision record.
// ---------------------------------------------------------------------------

/// The bench dot shape (bench/kernels/dot) at Idx<1009>.
let private siblingDotSrc =
    "type Ix = Idx<1009>\n"
    + "function inputs(scale: Float64) -> Array<Float64 like Ix> = {\n"
    + "    method_for(range<Ix>) <@> lambda(i) -> scale * Float64(i) + 1.0 |> compute\n"
    + "}\n"
    + "function kernel(a: Array<Float64 like Ix>, b: Array<Float64 like Ix>) -> Float64 = {\n"
    + "    let s = reduce(method_for(zip(a, b)) <@> lambda(x, y) -> x * y, lambda(p, q) where omp -> p + q, 0.0)\n"
    + "    s\n"
    + "}\n"
    + "let (checksum, input_checksum) = {\n"
    + "    let a = inputs(1.0)\n"
    + "    let b = inputs(2.0)\n"
    + "    (kernel(a, b), reduce(a, (+)) + reduce(b, (+)))\n"
    + "}\n"

/// The hand-written explicit join of the same program (round-2 C/dot_joined).
let private siblingDotJoinedSrc =
    "type Ix = Idx<1009>\n"
    + "function inputs(scale: Float64) -> Array<Float64 like Ix> = {\n"
    + "    method_for(range<Ix>) <@> lambda(i) -> scale * Float64(i) + 1.0 |> compute\n"
    + "}\n"
    + "let (checksum, input_checksum) = {\n"
    + "    let a = inputs(1.0)\n"
    + "    let b = inputs(2.0)\n"
    + "    let (s, sa, sb) = object_for(<&!>) <@> (\n"
    + "        reduce(method_for(zip(a, b)) <@> lambda(x, y) -> x * y, lambda(p, q) where omp -> p + q, 0.0),\n"
    + "        reduce(a, (+), -0.0),\n"
    + "        reduce(b, (+), -0.0))\n"
    + "    (s, sa + sb)\n"
    + "}\n"

let private withGates (pins: (string * string) list) (f: unit -> 'a) : 'a =
    List.foldBack (fun (var, value) (g: unit -> 'a) -> (fun () -> withGate var value g)) pins f ()

let private mainOf (cpp: string) =
    let i = cpp.IndexOf "int main"
    if i < 0 then "" else cpp.Substring i

/// The joined program's loop structure in `main`: loops over the axis, the
/// accumulator seeds in declaration order, and the simd-reduction clause arity
/// (0 = no clause).
let private siblingShape (cpp: string) =
    let m = mainOf cpp
    let loops = System.Text.RegularExpressions.Regex.Matches(m, @"for \(size_t __i0 = 0; __i0 < 1009;").Count
    let seeds =
        System.Text.RegularExpressions.Regex.Matches(m, @"double __v\d+_\d+ = ([^;]+);")
        |> Seq.map (fun x -> x.Groups.[1].Value) |> List.ofSeq
    let clause = System.Text.RegularExpressions.Regex.Match(m, @"BLADE_OMP_SIMD_REDUCTION\(\+:([^)]*)\)")
    let arity = if clause.Success then clause.Groups.[1].Value.Split(',').Length else 0
    (loops, seeds, arity)

let private siblingSerialPins = [ ("BLADE_OMP_THREADS", "1"); ("BLADE_FP_REPRO", "1"); ("BLADE_SIBLING_FOLDS", null) ]

/// OMP_THREADS=1 + FP_REPRO=1: ONE loop over a and b in main, three
/// accumulators seeded 0.0 (the kernel's own seed), -(0.0), -(0.0).
let private siblingDotOneLoop () =
    let name = "sibling_folds_dot_one_loop_per_leg_seeds"
    match withGates siblingSerialPins (fun () -> cppOfSource name siblingDotSrc) with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        let (loops, seeds, _) = siblingShape cpp
        if loops = 1 && seeds = [ "0.0"; "-(0.0)"; "-(0.0)" ] then
            resultLine Pass name "one loop over the axis in main; seeds 0.0, -(0.0), -(0.0)"
            true
        else
            resultLine Fail name ($"""loops={loops} seeds=[{String.concat "; " seeds}]""")
            false

/// The natural spelling and the hand-written join emit the same structure
/// (loop count, accumulator seeds, simd clause arity), serial-repro and
/// serial-licensed -- and each matches the EXPECTED structure on its own (one
/// loop; seeds 0.0, -(0.0), -(0.0); clause arity 0 in repro, 3 licensed), so
/// the two cannot agree while both are wrong.
let private siblingConvergence (label: string) (pins: (string * string) list) (wantArity: int) () =
    let name = $"sibling_folds_converges_on_the_explicit_join_{label}"
    match withGates pins (fun () -> cppOfSource name siblingDotSrc, cppOfSource name siblingDotJoinedSrc) with
    | Ok a, Ok b ->
        let sa = siblingShape a
        let sb = siblingShape b
        let (l, s, k) = sa
        if sa = sb && l = 1 && s = [ "0.0"; "-(0.0)"; "-(0.0)" ] && k = wantArity then
            resultLine Pass name ($"""both: {l} loop, seeds [{String.concat "; " s}], simd clause arity {k}""")
            true
        else
            let show (l, s: string list, k) = $"""loops={l} seeds=[{String.concat "; " s}] arity={k}"""
            resultLine Fail name ($"natural {show sa} vs explicit {show sb}; wanted 1 loop, seeds [0.0; -(0.0); -(0.0)], arity {wantArity}")
            false
    | Error e, _ | _, Error e -> resultLine Fail name e; false

let private siblingPlanCase (name: string) (pins: (string * string) list) (src: string)
                            (want: Blade.Effects.Decision -> bool) (describe: string) =
    withGates pins (fun () -> planCase name src "sibling-folds" want describe)

/// Threads on, licensed, BLADE_JOIN_THREADS=0: declined on the thread gate,
/// and the kernel's Path B team is still planned.
let private siblingThreadGate () =
    let name = "sibling_folds_thread_gate_keeps_path_b"
    let pins = [ ("BLADE_OMP_THREADS", null); ("BLADE_FP_REPRO", null); ("BLADE_SIBLING_FOLDS", null); ("BLADE_JOIN_THREADS", "0") ]
    match withGates pins (fun () -> planDecisionsOf siblingDotSrc) with
    | Error e -> resultLine Fail name e; false
    | Ok ds ->
        let gate = ds |> List.exists (fun d -> d.Rule = "sibling-folds" && declinedMentioning "a leg requests threads in this build and BLADE_JOIN_THREADS=0" d)
        let pathB = ds |> List.exists (fun d -> d.Rule = "omp" && applied d && evidenceMentioning "Path B" d)
        if gate && pathB then
            resultLine Pass name "BLADE_JOIN_THREADS=0: declined (a leg requests threads; the join would stay serial); the Path B team is kept"
            true
        else
            let seen = ds |> List.map Blade.Effects.Decisions.render |> String.concat " | "
            resultLine Fail name ($"gate={gate} pathB={pathB}; the record: {seen}")
            false

/// Threads on, licensed, the threaded join on (the default): the requesting
/// leg AND the two builtin input sums carry their own licences, so the group
/// joins (G7 policy: a request on some leg, every leg licensed) and the
/// synthesized join opens ONE team over the shared traversal -- the leg's own
/// `where omp` request carried, none manufactured.
let private siblingThreadedJoin () =
    let name = "sibling_folds_joins_and_threads_under_g7_policy"
    let pins = [ ("BLADE_OMP_THREADS", null); ("BLADE_FP_REPRO", null); ("BLADE_SIBLING_FOLDS", null); ("BLADE_JOIN_THREADS", null) ]
    match withGates pins (fun () -> planDecisionsOf siblingDotSrc) with
    | Error e -> resultLine Fail name e; false
    | Ok ds ->
        let joined = ds |> List.exists (fun d -> d.Rule = "sibling-folds" && applied d)
        let team = ds |> List.exists (fun d -> d.Rule = "omp" && applied d && evidenceMentioning "Path B joined chunks, G7" d && evidenceMentioning "leg 0's fold kernel" d)
        if joined && team then
            resultLine Pass name "applied: the kernel fold and both input sums join, and the join threads (requested by leg 0's own `where omp`)"
            true
        else
            let seen = ds |> List.map Blade.Effects.Decisions.render |> String.concat " | "
            resultLine Fail name ($"joined={joined} team={team}; the record: {seen}")
            false

/// THE NO-LOST-TEAM INVARIANT (Codex W2-E finding 1): in a threaded licensed
/// build, an APPLIED sibling rewrite of a group with a requesting leg implies
/// an APPLIED joined team; otherwise the rewrite declines and the requesting
/// fold's own Path B team survives. The dot shape with a negative integer
/// power in the summand: `sfTotal` alone would admit it (Float^Int negative
/// is a reciprocal, total), but the threaded join's whitelist refuses it, so
/// the pass must decline (the SAME predicate, Blade.IRTotality).
let private siblingNegPowSrc =
    siblingDotSrc.Replace("lambda(x, y) -> x * y,", "lambda(x, y) -> (x * y) ^ -1,")

let private siblingNoLostTeam (label: string) (src: string) (wantJoined: bool) () =
    let name = $"sibling_folds_no_lost_team_{label}"
    let pins = [ ("BLADE_OMP_THREADS", null); ("BLADE_FP_REPRO", null); ("BLADE_SIBLING_FOLDS", null); ("BLADE_JOIN_THREADS", null) ]
    if not (src.Contains "where omp") || (label = "negative_power" && not (src.Contains "^ -1")) then
        resultLine Fail name "test bug: the source lost its request or its negative power"
        false
    else
    match withGates pins (fun () -> planDecisionsOf src) with
    | Error e -> resultLine Fail name e; false
    | Ok ds ->
        let joined = ds |> List.exists (fun d -> d.Rule = "sibling-folds" && applied d)
        let joinedTeam = ds |> List.exists (fun d -> d.Rule = "omp" && applied d && evidenceMentioning "Path B joined chunks, G7" d)
        let ownTeam = ds |> List.exists (fun d -> d.Rule = "omp" && applied d && evidenceMentioning "outer level chunked per thread (Path B)" d)
        let ok = (if joined then joinedTeam else ownTeam) && joined = wantJoined
        let seen = ds |> List.filter (fun d -> d.Rule = "sibling-folds" || d.Rule = "omp") |> List.map Blade.Effects.Decisions.render |> String.concat " | "
        if ok then
            resultLine Pass name (if joined then "applied rewrite => applied joined team" else "declined => the requesting fold keeps its own Path B team")
            true
        else
            resultLine Fail name ($"joined={joined} (wanted {wantJoined}) joinedTeam={joinedTeam} ownTeam={ownTeam}; the record: {seen}")
            false

/// Threads on, licensed: a requesting leg beside an UNLICENSED leg declines
/// the group (the threaded join needs every leg licensed), so the requesting
/// fold keeps its own team.
let private siblingThreadGateUnlicensedSrc =
    "type R = Idx<1009>\n"
    + "let (s, t) = {\n"
    + "    let x = method_for(range<R>) <@> lambda(i) -> Float64(Int64(i) % 5) |> compute\n"
    + "    (reduce(x, lambda(p, q) where omp -> p + q, 0.0), reduce(x, lambda(p, q) -> p + 2.0 * q, 0.0))\n"
    + "}\n"

let private siblingThreadGateUnlicensed () =
    let name = "sibling_folds_thread_gate_unlicensed_leg_declines"
    let pins = [ ("BLADE_OMP_THREADS", null); ("BLADE_FP_REPRO", null); ("BLADE_SIBLING_FOLDS", null); ("BLADE_JOIN_THREADS", null) ]
    match withGates pins (fun () -> planDecisionsOf siblingThreadGateUnlicensedSrc) with
    | Error e -> resultLine Fail name e; false
    | Ok ds ->
        let gate = ds |> List.exists (fun d -> d.Rule = "sibling-folds" && declinedMentioning "not every leg carries its own reorder licence" d)
        if gate then
            resultLine Pass name "declined: a requesting leg beside an unlicensed leg (no licence is borrowed)"
            true
        else
            let seen = ds |> List.map Blade.Effects.Decisions.render |> String.concat " | "
            resultLine Fail name ($"gate={gate}; the record: {seen}")
            false

/// Licence mismatch: a builtin (+) leg beside an unlicensed lambda leg, in a
/// reordering build.
let private siblingLicenceMismatchSrc =
    "type R = Idx<5>\n"
    + "let (s, t) = {\n"
    + "    let x: Array<Float64 like R> = [1.0, 2.0, 3.0, 4.0, 5.0]\n"
    + "    (reduce(x, (+)), reduce(x, lambda(p, q) -> p + 2.0 * q, 0.0))\n"
    + "}\n"

/// An unseeded non-builtin (max) leg has no identity seed.
let private siblingUnseededMaxSrc =
    "type R = Idx<5>\n"
    + "let (s, m) = {\n"
    + "    let x: Array<Float64 like R> = [1.0, 7.0, 3.0, 4.0, 5.0]\n"
    + "    (reduce(x, (+)), reduce(x, lambda(a, b) -> if a > b then a else b))\n"
    + "}\n"

/// A `where repro` callee is not inlined (its per-function veto would be lost).
let private siblingReproCalleeSrc =
    "type R = Idx<5>\n"
    + "function kern(a: Array<Float64 like R>) where repro -> Float64 = {\n"
    + "    reduce(a, lambda(p, q) -> p + q, 0.0)\n"
    + "}\n"
    + "let (k, s) = {\n"
    + "    let x: Array<Float64 like R> = [1.0, 2.0, 3.0, 4.0, 5.0]\n"
    + "    (kern(x), reduce(x, (+)))\n"
    + "}\n"

/// An element write between the folds (tests/corpus/sibling-folds/006).
let private siblingMutationSrc =
    "type R = Idx<4>\n"
    + "function direct(x: Array<Float64 like R>) -> Float64 = {\n"
    + "    let mut v = x * 1.0\n"
    + "    let a = reduce(v, (+))\n"
    + "    v((2 : R)) = 7.0\n"
    + "    let b = reduce(v, (+), 0.0)\n"
    + "    100.0 * a + b\n"
    + "}\n"
    + "let x: Array<Float64 like R> = [1.0, 2.0, 3.0, 4.0]\n"
    + "let d = direct(x)\n"

/// A call writing the array through a `mut` parameter between the folds.
let private siblingInterveningCallSrc =
    "type R = Idx<4>\n"
    + "function bump(a: mut Array<Float64 like R>) -> Float64 = {\n"
    + "    a((0 : R)) = 50.0\n"
    + "    0.0\n"
    + "}\n"
    + "function through_call(x: Array<Float64 like R>) -> Float64 = {\n"
    + "    let mut v = x * 2.0\n"
    + "    let a = reduce(v, (+))\n"
    + "    let z = bump(v)\n"
    + "    let b = reduce(v, (+), 0.0)\n"
    + "    100.0 * a + b + z\n"
    + "}\n"
    + "let x: Array<Float64 like R> = [1.0, 2.0, 3.0, 4.0]\n"
    + "let c = through_call(x)\n"

/// Licensed legs folding with different builtin operators.
let private siblingMixedOpsSrc =
    "type R = Idx<5>\n"
    + "let (s, p) = {\n"
    + "    let x: Array<Float64 like R> = [1.0, 2.0, 3.0, 4.0, 5.0]\n"
    + "    (reduce(x, (+)), reduce(x, (*)))\n"
    + "}\n"

/// A writing call EARLIER IN THE SAME STATEMENT than a pure multi-fold callee
/// (tests/corpus/sibling-folds/013): the callee is not inlined.
let private siblingWriteBeforeCalleeSrc =
    "type R = Idx<3>\n"
    + "function bump(a: mut Array<Float64 like R>) -> Float64 = {\n"
    + "    a((0 : R)) = 50.0\n"
    + "    0.0\n"
    + "}\n"
    + "function stats(x: Array<Float64 like R>) -> Float64 = {\n"
    + "    let s = reduce(x, (+))\n"
    + "    let p = prodsum(x, x)\n"
    + "    s + p\n"
    + "}\n"
    + "let mut xm: Array<Float64 like R> = [1.0, 2.0, 3.0]\n"
    + "let answer = bump(xm) + stats(xm)\n"

/// A shared-fold tree read by a structural projection (`t[1]` of the nested
/// pairs ((s1, s2), s3) is s3, not leg 1): declined.
let private siblingStructuralProjSrc =
    "type R = Idx<3>\n"
    + "function shared(x: Array<Float64 like R>) -> Float64 = {\n"
    + "    let l = method_for(x)\n"
    + "    let t = reduce((l <@> lambda(v) -> v) <&!> (l <@> lambda(v) -> 2.0 * v) <&!> (l <@> lambda(v) -> 3.0 * v), (+))\n"
    + "    t[1] + reduce(x, (+), 10.0)\n"
    + "}\n"
    + "let x: Array<Float64 like R> = [1.0, 2.0, 3.0]\n"
    + "let r = shared(x)\n"

/// A map whose kernel writes (through another callable), DEFINED between two
/// folds of the array it writes and read via an ALIAS: the write closure
/// follows alias -> map -> kernel -> callee. (Gap G45: a NAMED writing map is
/// evaluated once at its definition, so the write happens at `w`'s let --
/// before G45 this fixture defined `w` ahead of all three folds and relied on
/// the map running at its READ; there the folds now legitimately share.)
let private siblingDeferredAliasWriteSrc =
    "type R = Idx<4>\n"
    + "function bump(a: mut Array<Float64 like R>) -> Float64 = {\n"
    + "    a((0 : R)) = 50.0\n"
    + "    0.0\n"
    + "}\n"
    + "function poke(a: mut Array<Float64 like R>) -> Float64 = bump(a)\n"
    + "let mut vv: Array<Float64 like R> = [2.0, 4.0, 6.0, 8.0]\n"
    + "let (ra, rz, rb) = {\n"
    + "    let a = reduce(vv, (+))\n"
    + "    let w = method_for(range<R>) <@> lambda(i) -> poke(vv)\n"
    + "    let w2 = w\n"
    + "    let z = reduce(w2, (+), 0.0)\n"
    + "    let b = reduce(vv, (+), 0.0)\n"
    + "    (a, z, b)\n"
    + "}\n"

/// A fold over a fresh compute map that is NOT the producer-epilogue shape
/// (a plain `reduce(y, (+))` in the return tuple, not a let-bound fold of a
/// cell map): an ordinary candidate, so it joins.
let private siblingNotEpilogueSrc =
    "type R = Idx<4>\n"
    + "let x: Array<Float64 like R> = [1.0, 2.0, 3.0, 4.0]\n"
    + "let (ey, ex) = {\n"
    + "    let y = x * 2.0 |> compute\n"
    + "    (reduce(y, (+)), reduce(x, (+)))\n"
    + "}\n"

/// The saxpy shape (bench/kernels/saxpy): the kernel's `w * w` fold over the
/// fresh y2 IS the producer-epilogue shape.
let private siblingSaxpySrc =
    "type Ix = Idx<1009>\n"
    + "function inputs(scale: Float64) -> Array<Float64 like Ix> = {\n"
    + "    method_for(range<Ix>) <@> lambda(i) -> scale * Float64(i) + 1.0 |> compute\n"
    + "}\n"
    + "function kernel(x: Array<Float64 like Ix>, y: Array<Float64 like Ix>) -> Float64 = {\n"
    + "    let y2 = method_for(zip(x, y)) <@> lambda(a, b) -> 1.5 * a + b |> compute\n"
    + "    let s = reduce(method_for(y2) <@> lambda(w) -> w * w, lambda(p, q) -> p + q, 0.0)\n"
    + "    s\n"
    + "}\n"
    + "let (checksum, input_checksum) = {\n"
    + "    let x = inputs(1.0)\n"
    + "    let y = inputs(2.0)\n"
    + "    (kernel(x, y), reduce(x, (+)) + reduce(y, (+)))\n"
    + "}\n"

/// Repro-kernel legs (tests/corpus/sibling-folds/015): unlicensed in every
/// lane, so the join applies in the default build.
let private siblingReproKernelSrc =
    "type R = Idx<3>\n"
    + "function radd(p: Float64, q: Float64) where repro -> Float64 = p + q\n"
    + "let (n_neg, pos, mixed) = {\n"
    + "    let zs: Array<Float64 like R> = [-0.0, -0.0, -0.0]\n"
    + "    let mz: Array<Float64 like R> = [0.0, -0.0, -0.0]\n"
    + "    (reduce(zs, radd, -0.0), reduce(zs, radd, 0.0), reduce(mz, radd, -0.0))\n"
    + "}\n"

/// The surface join's implicit seed for an unseeded (+) leg the USER wrote
/// (gap G74): the leg is UNSEEDED, so on a STRICT route (BLADE_FP_REPRO=1)
/// its seed is the exact identity -0.0 -- the same seed the sibling-fold pass
/// gives the legs IT builds -- and on a LICENSED route (the default build) the
/// base +0.0, byte for byte as before; a prodsum leg is the sum from zero and
/// keeps +0.0 on both: the join's accumulators are declared `= -0.0` /
/// `= 0.0` under REPRO and `= 0.0` / `= 0.0` in the default build.
let private siblingSurfaceJoinSeed () =
    let name = "sibling_folds_surface_join_unseeded_leg_seed_by_route"
    let src =
        "type R = Idx<3>\n"
        + "let z: Array<Float64 like R> = [-0.0, -0.0, -0.0]\n"
        + "let (j_surface, j_other) = object_for(<&!>) <@> (reduce(z, (+)), prodsum(z, z))\n"
    let seedsUnder (pins: (string * string) list) =
        match withGates pins (fun () -> cppOfSource name src) with
        | Error e -> Error e
        | Ok cpp ->
            Ok (System.Text.RegularExpressions.Regex.Matches(mainOf cpp, @"double __(?:v|tup_)\d+_\d+ = ([^;]+);")
                |> Seq.map (fun x -> x.Groups.[1].Value) |> List.ofSeq)
    let licensedPins = [ ("BLADE_OMP_THREADS", "1"); ("BLADE_FP_REPRO", null); ("BLADE_SIBLING_FOLDS", null) ]
    match seedsUnder siblingSerialPins, seedsUnder licensedPins with
    | Error e, _ | _, Error e -> resultLine Fail name e; false
    | Ok strict, Ok licensed ->
        if strict = [ "-0.0"; "0.0" ] && licensed = [ "0.0"; "0.0" ] then
            resultLine Pass name "REPRO (strict): the unseeded (+) leg takes the exact identity -0.0, the prodsum leg +0.0; default (licensed): both +0.0, the base emission"
            true
        else
            resultLine Fail name ($"""strict seeds=[{String.concat "; " strict}], licensed seeds=[{String.concat "; " licensed}]""")
            false
/// A callee that may fail (integer `/`) beside a sibling fold is NOT inlined:
/// its division stays inside `risky`'s own emitted body, under its
/// BLADE_FRAME, carrying the division's source line (3 here), and `main`
/// calls it -- so a panic names the original site and frame.
let private siblingFailingCalleeSite () =
    let name = "sibling_folds_failing_callee_keeps_site_and_frame"
    let src =
        "type R = Idx<4>\n"
        + "function risky(v: Array<Int64 like R>) -> Int64 = {\n"
        + "    let s = reduce(method_for(v) <@> lambda(e) -> 100 / e, (+), 0)\n"
        + "    s\n"
        + "}\n"
        + "let (k, t) = {\n"
        + "    let iv: Array<Int64 like R> = [1, 2, 0, 4]\n"
        + "    (risky(iv), reduce(iv, (+)))\n"
        + "}\n"
    match withGates siblingSerialPins (fun () -> cppOfSource name src) with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        let fnStart = cpp.IndexOf "int64_t risky(Array<int64_t, 1> v) {"
        let frame = if fnStart >= 0 then cpp.IndexOf("BLADE_FRAME(\"risky\"", fnStart) else -1
        let site = if fnStart >= 0 then System.Text.RegularExpressions.Regex.Match(cpp.Substring fnStart, @"idiv64\(100L, [^,]+, [^,]+, 3\)") else System.Text.RegularExpressions.Match.Empty
        let called = (mainOf cpp).Contains "risky(__v"
        if fnStart >= 0 && frame > fnStart && site.Success && called then
            resultLine Pass name "the division stays in risky's body (line 3, under BLADE_FRAME(\"risky\")); main calls risky"
            true
        else
            resultLine Fail name ($"fn={fnStart} frame={frame} site={site.Success} called={called}")
            false

let private siblingCases () =
    let serialLicensed = [ ("BLADE_OMP_THREADS", "1"); ("BLADE_FP_REPRO", null); ("BLADE_SIBLING_FOLDS", null) ]
    [ siblingDotOneLoop ()
      siblingPlanCase "decision_sibling_folds_applied" siblingSerialPins siblingDotSrc
          (fun d -> applied d && evidenceMentioning "3 folds over {a, b} share one traversal (Ix: 1009 cells)" d
                    && evidenceMentioning "kernel(a, b) inlined (pure-total, scalar return)" d
                    && evidenceMentioning "unseeded (+) legs seeded -0.0: bit-exact for every reachable value" d)
          "applied: 3 folds over {a, b}, kernel(a, b) inlined, unseeded legs seeded -0.0"
      siblingThreadGate ()
      siblingThreadedJoin ()
      siblingThreadGateUnlicensed ()
      siblingNoLostTeam "dot_shape" siblingDotSrc true ()
      siblingNoLostTeam "negative_power" siblingNegPowSrc false ()
      siblingPlanCase "decision_sibling_folds_disabled" (siblingSerialPins @ [ ("BLADE_SIBLING_FOLDS", "0") ]) siblingDotSrc
          (declinedMentioning "disabled by BLADE_SIBLING_FOLDS") "declined: disabled by BLADE_SIBLING_FOLDS"
      siblingConvergence "repro" siblingSerialPins 0 ()
      siblingConvergence "licensed" serialLicensed 3 ()
      siblingPlanCase "decision_sibling_folds_declined_licence" serialLicensed siblingLicenceMismatchSrc
          (declinedMentioning "legs disagree on the reorder licence") "declined: legs disagree on the reorder licence"
      siblingPlanCase "decision_sibling_folds_declined_unseeded_max" siblingSerialPins siblingUnseededMaxSrc
          (declinedMentioning "unseeded with a non-builtin kernel (no identity)") "declined: an unseeded max leg has no identity"
      siblingPlanCase "decision_sibling_folds_declined_repro_callee" siblingSerialPins siblingReproCalleeSrc
          (declinedMentioning "not inlined: a `where repro` callee") "declined: a `where repro` callee is not inlined"
      siblingPlanCase "decision_sibling_folds_declined_mutation" siblingSerialPins siblingMutationSrc
          (declinedMentioning "the region assigns or loops imperatively") "declined: the region assigns between the folds"
      siblingPlanCase "decision_sibling_folds_declined_intervening_call" siblingSerialPins siblingInterveningCallSrc
          (declinedMentioning "may write (a call or an assignment)") "declined: a call that may write sits between the folds"
      siblingPlanCase "decision_sibling_folds_declined_mixed_operators" serialLicensed siblingMixedOpsSrc
          (declinedMentioning "different or non-builtin operators") "declined: licensed legs fold with (+) and (*)"
      siblingPlanCase "decision_sibling_folds_declined_write_before_callee" siblingSerialPins siblingWriteBeforeCalleeSrc
          (declinedMentioning "stats(xm) not inlined: its statement may write") "declined: a writing call earlier in the statement keeps stats a call"
      siblingPlanCase "decision_sibling_folds_declined_structural_projection" siblingSerialPins siblingStructuralProjSrc
          (declinedMentioning "structural (nested-pair) projection") "declined: a shared tree read by t[1]"
      // G45: the writer runs at its own let, BETWEEN `a` and the other two
      // folds -- so `a` (before the write) must stay out of any join, while
      // the alias fold and `b` (both after it) may share one traversal.
      siblingPlanCase "decision_sibling_folds_declined_deferred_alias_write" siblingSerialPins siblingDeferredAliasWriteSrc
          (fun d -> evidenceMentioning "a statement between reduce(vv, ..) and it may write (a call or an assignment)" d)
          "declined for the fold before the writer: a map writing vv, read through an alias, is defined between the folds"
      siblingPlanCase "decision_sibling_folds_not_epilogue_joins" siblingSerialPins siblingNotEpilogueSrc
          (fun d -> applied d && evidenceMentioning "2 folds over" d) "applied: a plain fold over a fresh map is no epilogue's"
      siblingPlanCase "decision_sibling_folds_saxpy_left_for_epilogue" siblingSerialPins siblingSaxpySrc
          (fun d -> applied d && evidenceMentioning "2 folds over" d && evidenceMentioning "left for the producer epilogue" d)
          "applied to the input sums; the w * w fold over y2 left for the producer epilogue"
      siblingPlanCase "decision_sibling_folds_saxpy_epilogue_off_joins_all" (siblingSerialPins @ [ ("BLADE_CELL_EPILOGUE", "0") ]) siblingSaxpySrc
          (fun d -> applied d && evidenceMentioning "3 folds over" d && evidenceMentioning "kernel(x, y) inlined" d)
          "applied: with the epilogue off, the kernel's fold joins the input sums"
      siblingPlanCase "decision_sibling_folds_repro_kernels_applied_default" [ ("BLADE_OMP_THREADS", null); ("BLADE_FP_REPRO", null); ("BLADE_SIBLING_FOLDS", null) ] siblingReproKernelSrc
          (fun d -> applied d && evidenceMentioning "per-leg licences kept: unlicensed, unlicensed, unlicensed" d)
          "applied in the default build: three serial-in-every-lane legs"
      siblingSurfaceJoinSeed ()
      siblingFailingCalleeSite () ]

// ---------------------------------------------------------------------------
// Wave 3, E-G5: the threaded `let rec` step (Blade.Optimize.RecStepTable;
// BLADE_REC_STEP_OMP). The request is the `where omp` on the step's FINAL
// map; corpus recursive-arrays/031-032 pin the values, these pin the
// decisions and the C++ emission's forms.
let private recStepSrcWith (outerKernel: string) (rhsDecl: string) =
    "type T = Idx<4>\ntype X = Idx<3>\n"
    + rhsDecl
    + "function step(u: Array<Float64 like X>) -> Array<Float64 like X> = {\n"
    + "    let k1 = rhs(u)\n"
    + "    method_for(zip(u, k1)) <@> lambda(v, a) where omp(v: 1) -> a + 1.0 |> compute\n"
    + "}\n"
    + "function square(u0: Array<Float64 like X>) -> Float64 = {\n"
    + "    let rec traj: Array<Float64 like T, X> =\n"
    + "        match traj with\n"
    + "        | zero -> zero\n"
    + "        | zero :: s -> zero :: u0\n"
    + "        | prefix :: n -> prefix :: step(prefix(n - 1))\n"
    + "    reduce(method_for(traj) <@> lambda(row) -> prodsum(row, row), " + outerKernel + ")\n"
    + "}\n"
    + "let u0: Array<Float64 like X> = [1.0, 2.0, 4.0]\nlet q = square(u0)\n"
let private recStepRhs = "function rhs(u: Array<Float64 like X>) -> Array<Float64 like X> = u * 0.5\n"
let private recStepRhsRepro = "function rhs(u: Array<Float64 like X>) where repro -> Array<Float64 like X> = u * 0.5\n"
let private recStepSrc = recStepSrcWith "(+)" recStepRhs
let private recStepOuterSrc = recStepSrcWith "lambda(p, q) where omp -> p + q, 0.0" recStepRhs
let private recStepReproCalleeSrc = recStepSrcWith "(+)" recStepRhsRepro
/// A `where repro` OUTER fold kernel with the request: never spliced by the
/// new admission (its BLADE_REPRO_FN boundary would be lost), so row-fold
/// fusion declines it exactly as before (Codex diff review, finding 1).
let private recStepReproOuterSrc =
    recStepSrcWith "addr, 0.0" (recStepRhs + "function addr(a: Float64, b: Float64) where omp, repro -> Float64 = a + b\n")

/// The C++ emission under `pins`: every needle in `must` present, none of
/// `mustNot`.
let private recStepEmissionCase (name: string) (src: string) (pins: (string * string) list)
                                (must: string list) (mustNot: string list) =
    match withGates pins (fun () -> cppOfSource name src) with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        let missing = must |> List.filter (fun n -> not (cpp.Contains n))
        let present = mustNot |> List.filter (fun n -> cpp.Contains n)
        if missing.IsEmpty && present.IsEmpty then
            resultLine Pass name ($"{must.Length} needle(s) present, {mustNot.Length} absent")
            true
        else
            let missingTxt = String.concat "; " missing
            let presentTxt = String.concat "; " present
            resultLine Fail name ($"missing: [{missingTxt}]; unexpected: [{presentTxt}]")
            false

let private recStepCases () : bool list =
    let region = "BLADE_OMP_PARALLEL_NUM_THREADS(__rsT_"
    let chunk = "blade_fold::chunk_lo(__rsn_"
    let threadsOff = "[omp] requested but emitted serial: BLADE_OMP_THREADS=1"
    [ // The planner's admission (lowering-level decisions).
      decisionCase "decision_rec_step_applied" recStepSrc "rec-step-parallel"
          (fun d -> applied d && d.Evidence |> List.exists (fun e -> e.Contains "names the fused cell loop"))
          "applied: the final map's request names the fused cell loop"
      decisionCase "decision_rec_step_fold_attached" recStepSrc "rec-step-parallel"
          (fun d -> applied d && d.Evidence |> List.exists (fun e -> e.Contains "the fused row fold rides the team"))
          "applied: the fused self-dot row fold rides the team"
      decisionCase "decision_rec_step_fold_outer_request" recStepOuterSrc "rec-step-parallel"
          (fun d -> applied d && d.Evidence |> List.exists (fun e -> e.Contains "rows in parallel under the outer fold kernel's own `where omp`"))
          "applied: the outer kernel's request licenses the parallel row post-pass"
      withGate "BLADE_REC_STEP_OMP" "0" (fun () ->
        decisionCase "decision_rec_step_declined_gate" recStepSrc "rec-step-parallel"
            (declinedMentioning "disabled by BLADE_REC_STEP_OMP") "declined: disabled by BLADE_REC_STEP_OMP")
      decisionCase "decision_rec_step_declined_repro_callee" recStepReproCalleeSrc "rec-step-parallel"
          (declinedMentioning "`where repro`") "declined: a spliced callee is `where repro`"
      // The slice stays UNFUSED when the request cannot be honoured (the map
      // keeps its own team): the slice-write record names the step's request.
      decisionCase "decision_rec_step_repro_callee_keeps_request" recStepReproCalleeSrc "slice-write-fusion"
          (declinedMentioning "the step's final map asks for omp, which the fused cell loop cannot honour")
          "slice-write fusion declined so the request keeps its per-step team"
      // The C++ forms.
      recStepEmissionCase "rec_step_emission_licensed_partials" recStepSrc []
          [ region; chunk; "__rspart_"; "blade_fold::sq<double, 8>("; "the row partials combined ascending" ]
          [ "__rsslot_"; threadsOff; "the row fold after the team, serial" ]
      recStepEmissionCase "rec_step_emission_threads_off" recStepSrc [ ("BLADE_OMP_THREADS", "1") ]
          [ threadsOff ]
          [ region; "__rspart_"; "__rsslot_" ]
      recStepEmissionCase "rec_step_emission_repro_serial_rows" recStepSrc [ ("BLADE_FP_REPRO", "1") ]
          [ region; chunk; "the row fold after the team, serial" ]
          [ "__rspart_"; "__rsslot_"; "BLADE_OMP_PARALLEL_FOR\n" ]
      recStepEmissionCase "rec_step_emission_repro_parallel_rows" recStepOuterSrc [ ("BLADE_FP_REPRO", "1") ]
          [ region; chunk; "__rsslot_"; "BLADE_OMP_PARALLEL_FOR" ]
          [ "__rspart_"; "the row fold after the team, serial" ]
      recStepEmissionCase "rec_step_emission_lanes_off_serial_rows" recStepSrc [ ("BLADE_FOLD_LANES", "0") ]
          [ region; "the row fold after the team, serial" ]
          [ "__rspart_" ]
      // BLADE_FOLD_LANES=4: the in-team partial takes the configured count.
      recStepEmissionCase "rec_step_emission_lanes_4_partials" recStepSrc [ ("BLADE_FOLD_LANES", "4") ]
          [ region; "__rspart_"; "blade_fold::sq<double, 4>(" ]
          [ "blade_fold::sq<double, 8>(" ]
      // A `where repro` outer kernel with the request: row-fold fusion
      // declines (the fold keeps its boundary), the step still threads.
      decisionCase "decision_rec_step_repro_outer_kernel_not_fused" recStepReproOuterSrc "row-fold-fusion"
          (declinedMentioning "not a plain pure capture-free scalar kernel") "declined: a `where repro` outer kernel is not spliced"
      recStepEmissionCase "rec_step_emission_repro_outer_kernel_keeps_boundary" recStepReproOuterSrc []
          [ region; "rec-step-parallel v1: one persistent team"; "BLADE_REPRO_FN " ]
          [ "__rspart_"; "__rsslot_"; "the row fold after the team, serial" ]
      recStepEmissionCase "rec_step_emission_gate_off_restores_step_call" recStepSrc [ ("BLADE_REC_STEP_OMP", "0") ]
          [ "step("; "BLADE_OMP_PARALLEL_FOR_SIMD" ]
          [ region; "__rspart_" ] ]

// ---------------------------------------------------------------------------
// Aggregate-only grouping (round 3 wave 3, W3-1; Optimize.planGroupAggregate,
// rule `group-aggregate`, gap G71): the decision record -- the three regimes,
// the four state machines, the strict-first association, the five-party
// matrix, every decline -- and both lanes' one pass (no permutation, no
// gathered pool), with the gate-off control. The values are pinned by corpus
// sql-group-by/064-074.
// ---------------------------------------------------------------------------

let private gaRule = "group-aggregate"
let private gaHead = "type Row = Idx<5>\ntype Region = Idx<4>\n"
let private gaCall (fn: string) (args: string) = $"let keys: Array<Int64 like Row> = [7, 3, 7, 9, 3]\nlet vals: Array<Int64 like Row> = [1, 2, 3, 4, 5]\nlet xs: Array<Float64 like Row> = [1.0, 2.0, 4.0, 8.0, 16.0]\nlet out = {fn}({args})\n"
/// dynamic keys, the four state machines (integer: exact in every build)
let private gaDynSrc =
    gaHead
    + "function agg(k: Array<Int64 like Row>, v: Array<Int64 like Row>) = {\n"
    + "    let gk = group_keys(k)\n    let g = group_by(v, gk)\n"
    + "    let u = method_for(g) <@> lambda(r: Array<Int64 like RaggedIdx<_>>) -> reduce(r, lambda(a, b) -> 3 * a + b) |> compute\n"
    + "    let t = method_for(g) <@> lambda(r: Array<Int64 like RaggedIdx<_>>) -> reduce(r, (+), 1000) |> compute\n"
    + "    let q = method_for(g) <@> lambda(r: Array<Int64 like RaggedIdx<_>>) -> prodsum(r, r) |> compute\n"
    + "    let n = method_for(g) <@> lambda(r: Array<Int64 like RaggedIdx<_>>) -> extents(r) |> compute\n"
    + "    let c = extents(gk)\n    (u, t, q, n, c)\n}\n"
    + gaCall "agg" "keys, vals"
/// positional keys with an empty group
let private gaPosSrc =
    gaHead
    + "function agg(k: Array<Region like Row>, v: Array<Int64 like Row>) = {\n"
    + "    let gk = group_keys(k)\n    let g = group_by(v, gk)\n"
    + "    let t = method_for(g) <@> lambda(r: Array<Int64 like RaggedIdx<_>>) -> reduce(r, (+), 100) |> compute\n"
    + "    let q = method_for(g) <@> lambda(r: Array<Int64 like RaggedIdx<_>>) -> prodsum(r, r) |> compute\n"
    + "    (t, q)\n}\n"
    + "let pk: Array<Region like Row> = [2, 0, 2, -1, 3]\nlet vals: Array<Int64 like Row> = [1, 2, 3, 4, 5]\nlet out = agg(pk, vals)\n"
/// EnumIdx keys, declared unsorted with a negative member
let private gaEnumSrc =
    "type Code = EnumIdx<[7, -2, 3]>\ntype Row = Idx<4>\n"
    + "function agg(k: Array<Code like Row>, v: Array<Int64 like Row>) = {\n"
    + "    let gk = group_keys(k)\n    let g = group_by(v, gk)\n"
    + "    let t = method_for(g) <@> lambda(r: Array<Int64 like RaggedIdx<_>>) -> reduce(r, (+), 0) |> compute\n    t\n}\n"
    + "let codes: Array<Code like Row> = [-2, 7, -2, 7]\nlet vals: Array<Int64 like Row> = [1, 2, 3, 4]\nlet out = agg(codes, vals)\n"
/// a licensed Float64 builtin fold and a Float64 prodsum
let private gaFloatSrc =
    gaHead
    + "function agg(k: Array<Int64 like Row>, x: Array<Float64 like Row>) = {\n"
    + "    let gk = group_keys(k)\n    let g = group_by(x, gk)\n"
    + "    let s = method_for(g) <@> lambda(r: Array<Float64 like RaggedIdx<_>>) -> reduce(r, (+)) |> compute\n"
    + "    let q = method_for(g) <@> lambda(r: Array<Float64 like RaggedIdx<_>>) -> prodsum(r, r) |> compute\n"
    + "    (s, q)\n}\n"
    + gaCall "agg" "keys, xs"
let private gaDeclineSrc (body: string) =
    gaHead
    + "function agg(k: Array<Int64 like Row>, v: Array<Int64 like Row>) = {\n" + body + "}\n"
    + gaCall "agg" "keys, vals"
let private gaRowReadSrc =
    gaDeclineSrc ("    let gk = group_keys(k)\n    let g = group_by(v, gk)\n"
                  + "    let s = method_for(g) <@> lambda(r: Array<Int64 like RaggedIdx<_>>) -> reduce(r, (+)) |> compute\n"
                  + "    let h = method_for(g) <@> lambda(r) -> r(0) |> compute\n    (s, h)\n")
let private gaOmpSrc =
    gaDeclineSrc ("    let gk = group_keys(k)\n    let g = group_by(v, gk)\n"
                  + "    let s = method_for(g) <@> lambda(r: Array<Int64 like RaggedIdx<_>>) where omp(r: 1) -> reduce(r, (+)) |> compute\n    s\n")
let private gaLateSeedSrc =
    gaHead
    + "function agg(k: Array<Int64 like Row>, v: Array<Int64 like Row>, b: Int64) = {\n"
    + "    let gk = group_keys(k)\n    let s0 = b * 2\n    let g = group_by(v, gk)\n"
    + "    let s = method_for(g) <@> lambda(r: Array<Int64 like RaggedIdx<_>>) -> reduce(r, (+), s0) |> compute\n    s\n}\n"
    + gaCall "agg" "keys, vals, 5"
let private gaDeferredSrc =
    gaHead + "let lazy = method_for(range<Row>) <@> lambda(i) -> Int64(i) * 10\n"
    + "function agg(k: Array<Int64 like Row>) = {\n"
    + "    let gk = group_keys(k)\n    let g = group_by(lazy, gk)\n"
    + "    let s = method_for(g) <@> lambda(r: Array<Int64 like RaggedIdx<_>>) -> reduce(r, lambda(a, b) -> 3 * a + b) |> compute\n    s\n}\n"
    + gaCall "agg" "keys"
let private gaWritingSrc =
    "type One = Idx<1>\n" + gaHead
    + "let mut calls: Array<Int64 like One> = [0]\n"
    + "function bump(c: mut Array<Int64 like One>, a: Int64, b: Int64) -> Int64 = {\n    c(0) = c(0) + 1\n    a + b\n}\n"
    + "function agg(k: Array<Int64 like Row>, v: Array<Int64 like Row>) = {\n"
    + "    let gk = group_keys(k)\n    let g = group_by(v, gk)\n"
    + "    let s = method_for(g) <@> lambda(r: Array<Int64 like RaggedIdx<_>>) -> reduce(r, lambda(a, b) -> bump(calls, a, b)) |> compute\n    s\n}\n"
    + gaCall "agg" "keys, vals"
let private gaBetweenWriterSrc =
    "type One = Idx<1>\n" + gaHead
    + "let mut log: Array<Int64 like One> = [0]\n"
    + "function note(c: mut Array<Int64 like One>) -> Int64 = {\n    c(0) = c(0) + 1\n    c(0)\n}\n"
    + "function agg(k: Array<Int64 like Row>, v: Array<Int64 like Row>) = {\n"
    + "    let gk = group_keys(k)\n    let w = note(log)\n    let g = group_by(v, gk)\n"
    + "    let s = method_for(g) <@> lambda(r: Array<Int64 like RaggedIdx<_>>) -> reduce(r, (+)) |> compute\n    (w, s)\n}\n"
    + gaCall "agg" "keys, vals"
let private gaBucketSrc =
    gaDeclineSrc ("    let gk = group_keys(k)\n    let g = group_by(v, gk)\n    let b = group_bucket(gk)\n"
                  + "    let s = method_for(g) <@> lambda(r: Array<Int64 like RaggedIdx<_>>) -> reduce(r, (+)) |> compute\n    (b, s)\n")
let private gaTopLevelSrc =
    gaHead + "let keys: Array<Int64 like Row> = [7, 3, 7, 9, 3]\nlet vals: Array<Int64 like Row> = [1, 2, 3, 4, 5]\n"
    + "let tgk = group_keys(keys)\nlet tg = group_by(vals, tgk)\n"
    + "let ts = method_for(tg) <@> lambda(r: Array<Int64 like RaggedIdx<_>>) -> reduce(r, (+)) |> compute\n"
let private gaMultiKeySrc =
    gaDeclineSrc ("    let gk = group_keys(k, v)\n    let g = group_by(v, gk)\n"
                  + "    let s = method_for(g) <@> lambda(r: Array<Int64 like RaggedIdx<_>>) -> reduce(r, (+)) |> compute\n    s\n")
let private gaApplied (d: Blade.Effects.Decision) = applied d && d.Subject = "agg"
let private gaLazyHead =
    "type Two = Idx<2>\nlet den: Array<Int64 like Two> = [1, 0]\nlet lazy = method_for(den) <@> lambda(d) -> 10 / d\nlet k: Array<Int64 like Two> = [7, 7]\nlet v: Array<Int64 like Two> = [3, 4]\n"
let private gaCountOnlyLazySrc =
    gaLazyHead
    + "function counts(key: Array<Int64 like Two>) -> Int64 = {\n    let gk = group_keys(key)\n    let g = group_by(lazy, gk)\n"
    + "    let n = method_for(g) <@> lambda(r: Array<Int64 like RaggedIdx<_>>) -> extents(r) |> compute\n    reduce(n, (+))\n}\nlet total = counts(k)\n"
let private gaUnusedLazySrc =
    gaLazyHead
    + "function sums(key: Array<Int64 like Two>, x: Array<Int64 like Two>) -> Int64 = {\n    let gk = group_keys(key)\n    let g = group_by(x, gk)\n    let unused = group_by(lazy, gk)\n"
    + "    let s = method_for(g) <@> lambda(r: Array<Int64 like RaggedIdx<_>>) -> reduce(r, (+)) |> compute\n    reduce(s, (+))\n}\nlet total = sums(k, v)\n"
let private gaMixedTypeSrc =
    "type Row = Idx<3>\ntype Region = Idx<2>\n"
    + "function f(k: Array<Region like Row>, v: Array<Int64 like Row>) = {\n    let gk = group_keys(k)\n    let g = group_by(v, gk)\n"
    + "    let s = method_for(g) <@> lambda(r: Array<Int64 like RaggedIdx<_>>) -> reduce(r, lambda(a, b) -> Float64(a) + Float64(b)) |> compute\n    (s, s)\n}\n"
    + "let keys: Array<Region like Row> = [0, 1, 1]\nlet vals: Array<Int64 like Row> = [9007199254740993, 9007199254740993, 2]\nlet (s, s2) = f(keys, vals)\n"
let private gaWritingProducerSrc =
    "type One = Idx<1>\n" + gaHead
    + "let mut log: Array<Int64 like One> = [0]\n"
    + "function make(l: mut Array<Int64 like One>) -> Array<Int64 like Row> = {\n    l(0) = l(0) + 1\n    [1, 2, 3, 4, 5]\n}\n"
    + "let logged = make(log)\n"
    + "function agg(k: Array<Int64 like Row>) = {\n    let gk = group_keys(k)\n    let g = group_by(logged, gk)\n"
    + "    let n = method_for(g) <@> lambda(r: Array<Int64 like RaggedIdx<_>>) -> extents(r) |> compute\n    n\n}\n"
    + "let keys: Array<Int64 like Row> = [7, 3, 7, 9, 3]\nlet out = agg(keys)\n"
let private gaDeferredArgSrc =
    gaHead + "let lazyv = method_for(range<Row>) <@> lambda(i) -> Int64(i) * 10\n"
    + "function agg(k: Array<Int64 like Row>, v: Array<Int64 like Row>) = {\n    let gk = group_keys(k)\n    let g = group_by(v, gk)\n"
    + "    let s = method_for(g) <@> lambda(r: Array<Int64 like RaggedIdx<_>>) -> reduce(r, (+)) |> compute\n    s\n}\n"
    + "let keys: Array<Int64 like Row> = [7, 3, 7, 9, 3]\nlet out = agg(keys, lazyv)\n"
let private gaAssignedSrc =
    gaHead
    + "function agg(k: Array<Int64 like Row>, v: Array<Int64 like Row>) = {\n    let gk = group_keys(k)\n    let g = group_by(v, gk)\n"
    + "    let s = method_for(g) <@> lambda(r: Array<Int64 like RaggedIdx<_>>) -> reduce(r, (+)) |> compute\n    s\n}\n"
    + "let keys: Array<Int64 like Row> = [7, 3, 7, 9, 3]\nlet mut vals: Array<Int64 like Row> = [1, 2, 3, 4, 5]\nvals(0) = 9\nlet out = agg(keys, vals)\n"
let private gaBlockArgSrc =
    gaHead
    + "function mk() -> Array<Int64 like Row> = [1, 2, 3, 4, 5]\n"
    + "function agg(k: Array<Int64 like Row>, v: Array<Int64 like Row>) -> Int64 = {\n    let gk = group_keys(k)\n    let g = group_by(v, gk)\n"
    + "    let s = method_for(g) <@> lambda(r: Array<Int64 like RaggedIdx<_>>) -> reduce(r, (+)) |> compute\n    reduce(s, (+))\n}\n"
    + "let out = {\n    let kk: Array<Int64 like Row> = [7, 3, 7, 9, 3]\n    let vv = mk()\n    agg(kk, vv)\n}\n"

let private groupAggDecisionCases () =
    [ decisionCase "decision_group_agg_dynamic_applied" gaDynSrc gaRule
          (fun d -> gaApplied d
                    && evidenceHas "one pass over the rows in source order; states u, t, q, n; no perm, no gather" d
                    && evidenceHas "dynamic: groups numbered by FIRST occurrence, through ONE find-or-insert per row in the C core" d)
          "applied: dynamic keys, four states, one pass"
      decisionCase "decision_group_agg_four_state_machines" gaDynSrc gaRule
          (fun d -> gaApplied d
                    && evidenceHas "`u`: UNSEEDED reduce (first value copied and consumed once" d
                    && evidenceHas "`t`: SEEDED reduce (the chain from the exact seed" d
                    && evidenceHas "`q`: PRODSUM (acc = 0, then acc += a * b)" d
                    && evidenceHas "`n`: COUNT" d)
          "the four state machines, each named"
      decisionCase "decision_group_agg_five_party_matrix" gaDynSrc gaRule
          (fun d -> gaApplied d
                    && evidenceHas "groupAggAdmits (five parties, each through Optimize.scheduleMayWriteIn): producers (the key and EVERY group_by operand, count-only and unused ones included" d
                    && evidenceHas "the key array: a parameter of `agg`; at each of its 1 call site(s): `keys`: a module binding, an array literal; producer payload(s) `keys`: scheduleMayWriteIn + CSE facts, no write" d
                    && evidenceHas "consumer (peel) kernels `u`, `t`, `q`, `n`: no write" d
                    && evidenceHas "outer fold kernels: 2, each closed-total (Blade.IRTotality), no write" d
                    && evidenceHas "seeds: 1, each closed-total, no write, row-independent and dominating" d
                    && evidenceHas "between the grouping and its last use:" d
                    && evidenceHas "parallel: none -- the one pass is serial" d)
          "the five-party matrix recorded"
      decisionCase "decision_group_agg_first_failure_and_unproved" gaDynSrc gaRule
          (fun d -> gaApplied d
                    && evidenceHas "first failure: the pass raises only the grouping's own BL8006" d
                    && evidenceHas "each unseeded peel keeps its BL8003 at its own site for the FIRST empty group in group order" d
                    && evidenceHas "unproved: the bit identity with the two-pass form is ARGUED" d
                    && evidenceHas "printed-value witnesses -- corpus sql-group-by/064-074 in the C++ lane" d)
          "the failure order and what is unproved, stated"
      decisionCase "decision_group_agg_integer_exact_in_default_build" gaDynSrc gaRule
          (fun d -> gaApplied d && evidenceHas "exact (an integer builtin fold: wrapping, associative and commutative" d
                    && evidenceHas "strict (the kernel carries no reorder licence: every build runs this exact chain)" d)
          "default build: the integer builtin fold is exact, the 3a+b fold strict"
      decisionCase "decision_group_agg_positional_empty_groups" gaPosSrc gaRule
          (fun d -> gaApplied d && evidenceHas "positional (Idx<4> keys): group g is key value g, 0 .. 3, EMPTY groups included" d)
          "applied: positional regime, empty groups included"
      decisionCase "decision_group_agg_enum_declared_order" gaEnumSrc gaRule
          (fun d -> gaApplied d && evidenceHas "EnumIdx (3 declared members; the stored key of each, in declared order: [0, 1, 2]): groups in the DECLARED enumeration order (never sorted by value)" d
                    && evidenceHas "negative members valid (an EnumIdx key never drops)" d)
          "applied: EnumIdx in declared order, negative member valid"
      decisionCase "decision_group_agg_licensed_float_declined" gaFloatSrc gaRule
          (fun d -> d.Subject = "agg" && declinedMentioning "`s` is a licensed Float64 fold: the one pass's ascending chain is a different association from today's licensed forms (the C++ lane's per-group K-lane tree; the LLVM lane's flagged recurrence), so v1 declines in both lanes -- a conservative shared gate" d)
          "default build: a licensed Float64 fold keeps today's form (strict first)"
      withGate "BLADE_FP_REPRO" "1" (fun () ->
          decisionCase "decision_group_agg_repro_float_applied" gaFloatSrc gaRule
              (fun d -> gaApplied d && evidenceHas "`s`: UNSEEDED reduce (first value copied and consumed once; empty group: BL8003 at the peel), strict (the repro build): the chain as written" d
                        && evidenceHas "`q`: PRODSUM (acc = 0, then acc += a * b), strict (the repro build)" d)
              "repro build: the Float64 states take the strict pass")
      withGate "BLADE_GROUP_AGG" "licensed" (fun () ->
          decisionCase "decision_group_agg_licensed_arm" gaFloatSrc gaRule
              (fun d -> gaApplied d && evidenceHas "LICENSED (BLADE_GROUP_AGG=licensed, an EXPERIMENTAL arm): the ascending chain in place of today's form -- in the C++ lane a per-group K-lane tree, in the LLVM lane a recurrence carrying the fold's fast-math flags" d)
              "BLADE_GROUP_AGG=licensed: the licensed float arm, recorded as an association change")
      withGate "BLADE_GROUP_AGG" "0" (fun () ->
          decisionCase "decision_group_agg_disabled" gaDynSrc gaRule
              (fun d -> d.Subject = "agg" && declinedMentioning "disabled by BLADE_GROUP_AGG" d) "declined: disabled by BLADE_GROUP_AGG")
      decisionCase "decision_group_agg_declined_row_read" gaRowReadSrc gaRule
          (fun d -> d.Subject = "agg" && declinedMentioning "the peel `h`'s kernel is outside the aggregate subset" d) "declined: a row-reading consumer r(0)"
      decisionCase "decision_group_agg_declined_omp_peel" gaOmpSrc gaRule
          (fun d -> d.Subject = "agg" && declinedMentioning "is a `where omp` peel" d) "declined: a `where omp` peel"
      decisionCase "decision_group_agg_declined_late_seed" gaLateSeedSrc gaRule
          (fun d -> d.Subject = "agg" && declinedMentioning "captures a value bound after the grouping (a late seed or scalar" d) "declined: a seed bound after the grouping"
      decisionCase "decision_group_agg_declined_deferred_source" gaDeferredSrc gaRule
          (fun d -> d.Subject = "agg" && declinedMentioning "the value array is not proved stored: `lazy` is a deferred computation" d) "declined: a deferred value source"
      decisionCase "decision_group_agg_declined_writing_fold" gaWritingSrc gaRule
          (fun d -> d.Subject = "agg" && declinedMentioning "the peel `s` may write" d) "declined: a group fold writing a captured counter"
      decisionCase "decision_group_agg_declined_writing_between" gaBetweenWriterSrc gaRule
          (fun d -> d.Subject = "agg" && declinedMentioning "a binding between the grouping and the last peel may write" d
                    && evidenceHas "DECLINES: a binding between the grouping and the last peel may write" d)
          "declined: an intervening binding writes (the matrix names it)"
      decisionCase "decision_group_agg_declined_group_bucket" gaBucketSrc gaRule
          (fun d -> d.Subject = "agg" && declinedMentioning "the grouping is used other than as `group_by(_, gk)` / `extents(gk)`" d) "declined: a group_bucket use"
      decisionCase "decision_group_agg_declined_top_level" gaTopLevelSrc gaRule
          (fun d -> d.Subject = "tgk" && declinedMentioning "a top-level grouping: v1 plans groupings inside a function body only" d)
          "declined: a top-level (named, printed) grouped array"
      decisionCase "decision_group_agg_declined_count_only_deferred_source" gaCountOnlyLazySrc gaRule
          (fun d -> d.Subject = "counts" && declinedMentioning "the value array is not proved stored: `lazy` is a deferred computation" d)
          "declined: a COUNT-only grouped array over a deferred producer (its evaluation would be skipped)"
      decisionCase "decision_group_agg_declined_unused_deferred_source" gaUnusedLazySrc gaRule
          (fun d -> d.Subject = "sums" && declinedMentioning "the value array is not proved stored: `lazy` is a deferred computation" d)
          "declined: an UNUSED grouped array over a deferred producer"
      decisionCase "decision_group_agg_declined_mixed_type_fold" gaMixedTypeSrc gaRule
          (fun d -> d.Subject = "f" && declinedMentioning "the fold kernel of `s` is not typed in the state's one scalar representation (Int64): parameters (Int64, Int64) -> Float64 over Int64 elements" d)
          "declined: a fold whose result type differs from its state"
      decisionCase "decision_group_agg_declined_writing_producer" gaWritingProducerSrc gaRule
          (fun d -> d.Subject = "agg" && declinedMentioning "producer `logged` may write or is assigned" d)
          "declined: a value source whose producer writes (count-only)"
      decisionCase "decision_group_agg_declined_deferred_argument" gaDeferredArgSrc gaRule
          (fun d -> d.Subject = "agg" && declinedMentioning "is not proved stored at every call site" d && declinedMentioning "deferred computation" d)
          "declined: a parameter whose call-site argument is a deferred map"
      decisionCase "decision_group_agg_declined_assigned_source" gaAssignedSrc gaRule
          (fun d -> d.Subject = "agg" && declinedMentioning "is assigned in the program" d)
          "declined: a value source assigned elsewhere"
      decisionCase "decision_group_agg_block_let_argument_applied" gaBlockArgSrc gaRule
          (fun d -> gaApplied d && evidenceHas "at each of its 1 call site(s): a let, a call's array result; its value: scheduleMayWriteIn + CSE facts, no write" d)
          "applied: a parameter whose argument is a block let holding a call's array result"
      decisionCase "decision_group_agg_declined_multi_key" gaMultiKeySrc gaRule
          (fun d -> d.Subject = "agg" && declinedMentioning "a multi-key grouping" d) "declined: a multi-key grouping" ]

/// The C++ one pass: the planned dynamic grouping finds each row's group with
/// ONE find-or-insert and builds no permutation; its group_by gathers no rows;
/// the unseeded peel checks its groups before adopting its state. The gate-off
/// control restores the CSR (permutation, gather, peel IIFEs).
let private groupAggCppEmission () =
    let name = "group_agg_cpp_one_pass_no_perm_no_gather"
    match cppOfSource name gaDynSrc, withGate "BLADE_GROUP_AGG" "0" (fun () -> cppOfSource name gaDynSrc) with
    | Ok cpp, Ok off ->
        let want =
            [ "#include \"blade_member.h\""
              "// group-aggregate v1 (Blade.Optimize.planGroupAggregate): ONE pass over the rows in source order, no permutation, no gather"
              "int64_t __gi = blade_gkmap_group_i64(&"
              "blade_gk_slot* __rec = blade_gkmap_record_i64(&"
              "if (__rec[0].i == 0) __rec[1].i = __v; else __rec[1].i = "
              "rows NOT gathered -- every consumer reads its group-aggregate state"
              "blade_rt::panic(\"BL8003\", \"reduce: empty array, no reduction possible\""
              "blade_gkmap_free_i64(&" ]
        let missing = want |> List.filter (fun s -> not (cpp.Contains s))
        let perm = cpp.Contains "__perm["
        let offPerm = off.Contains "__perm[" && not (off.Contains "blade_gkmap") && off.Contains "__pool = new int64_t["
        if missing.IsEmpty && not perm && offPerm then
            resultLine Pass name "one find-or-insert per row, no permutation, rows null, the peel's BL8003 at its site; gate off: the CSR + gather"
            true
        else
            resultLine Fail name ($"missing {missing}; planned perm={perm}; gate-off CSR={offPerm}")
            false
    | Error e, _ | _, Error e -> resultLine Fail name e; false

/// The C++ positional pass: the BL8006 guard and the drop exactly as the
/// CSR's key pass, the group IS the key, the seeded state stored once per
/// group (empty groups included) before the pass.
let private groupAggCppPositional () =
    let name = "group_agg_cpp_positional_guard_and_seed"
    match cppOfSource name gaPosSrc with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        let want =
            [ "if (__k < 0) continue; // negative key: row belongs to no group"
              "if ((size_t)__k >= 4) blade_rt::panic(\"BL8006\", \"index out of bounds: a group_keys key outside 0 .. 3\""
              "size_t __g = (size_t)__k;"
              "for (size_t __g = 0; __g < 4; __g++) "
              "__agg0[__g] = 100L;"
              "__agg1[__g] += v[__ki] * v[__ki];" ]
        let missing = want |> List.filter (fun s -> not (cpp.Contains s))
        if missing.IsEmpty && not (cpp.Contains "__perm[") then
            resultLine Pass name "positional: the key guard and drop as today, seeded states before the pass, prodsum `+= a * b`"
            true
        else
            let perm = cpp.Contains "__perm["
            resultLine Fail name ($"missing {missing}; perm={perm}")
            false

/// The LLVM one pass: the planned dynamic grouping calls the C core's map
/// with one record slot per state plus the count; gate off, the CSR's own
/// discovery uses the same map with one slot (W3-1 retired the linear scan).
let private groupAggLlvmEmission () =
    let name = "group_agg_llvm_one_pass_map"
    let llOf () =
        match lower gaDynSrc with
        | Error e -> Error ($"lower: {e}")
        | Ok ir ->
            match EmitLlvm.tryEmitProgramNamed "group_agg_llvm" ir with
            | Ok ll -> Ok ll
            | Error r -> Error ($"the llvm lane refused: {r}")
    match llOf (), withGate "BLADE_GROUP_AGG" "0" llOf with
    | Ok ll, Ok off ->
        let initRe = System.Text.RegularExpressions.Regex(@"call i32 @blade_gkmap_init_llvm\(ptr %[\w.]+, i64 (\d+)\)")
        let slots (s: string) = [ for m in initRe.Matches s -> m.Groups.[1].Value ]
        let onPass = slots ll = [ "5" ] && ll.Contains "call ptr @blade_gkmap_record_llvm(" && ll.Contains "ga.first"
        let offCsr = slots off = [ "1" ] && not (off.Contains "@blade_gkmap_record_llvm(") && off.Contains "@blade_gkmap_group_llvm("
        if onPass && offCsr then
            resultLine Pass name "planned: one map with 4 states + the count, records advanced in place; gate off: the CSR, its discovery through the same map (one slot)"
            true
        else
            let records = ll.Contains "@blade_gkmap_record_llvm("
            resultLine Fail name ($"planned slots {slots ll} (want [5]) records={records}; gate-off slots {slots off} (want [1])")
            false
    | Error e, _ | _, Error e -> resultLine Fail name e; false

/// The LLVM lane's ordinary dynamic discovery takes the C-core map ONLY over a
/// stored key pool (Codex second look at e6eadfa2): an inline, unforced key
/// producer (here one whose kernel writes a captured counter, evaluated once
/// per CSR pass) keeps the linear scan -- no `blade_gkmap` call at all -- so a
/// later pass can never number a group past the CSR it sized; the stored key
/// pool of `gaDynSrc` takes the map (`group_agg_llvm_one_pass_map`).
let private groupKeysLlvmInlineProducerScan () =
    let name = "group_keys_llvm_inline_key_producer_keeps_the_scan"
    let src =
        "type One = Idx<1>\ntype Two = Idx<2>\nlet mut calls: Array<Int64 like One> = [0]\nlet rows: Array<Int64 like Two> = [0, 0]\n"
        + "function next_key(c: mut Array<Int64 like One>, x: Int64) -> Int64 = {\n    c(0) = c(0) + 1\n    c(0)\n}\n"
        + "let gk = group_keys(method_for(rows) <@> lambda(x) -> next_key(calls, x))\nlet sizes = extents(gk)\n"
    let llOf () =
        match lower src with
        | Error e -> Error ($"lower: {e}")
        | Ok ir ->
            match EmitLlvm.tryEmitProgramNamed "gk_inline" ir with
            | Ok ll -> Ok ll
            | Error r -> Error ($"the llvm lane refused: {r}")
    match withGate "BLADE_GROUP_AGG" "0" llOf, llOf () with
    | Ok off, Ok on ->
        if not (off.Contains "@blade_gkmap") && not (on.Contains "@blade_gkmap") then
            resultLine Pass name "an inline key producer: the linear scan in both gates (no map call)"
            true
        else
            resultLine Fail name "expected no @blade_gkmap call for an inline (non-pool) key producer"
            false
    | Error e, _ | _, Error e -> resultLine Fail name e; false

// ---------------------------------------------------------------------------
// Membership hoisting (round 3, B-T1; Optimize.planMembershipHoist, rule
// `member-hoist`): the decision record -- representation, the five-party
// matrix, every decline -- and the C++ emission's build / probe / free.
// ---------------------------------------------------------------------------

let private memberHead =
    "type Row = Idx<9>\n"
    + "let customer: Array<Int64 like Row> = [3, 1, 3, 2, 1, 4, 3, 9, -5]\n"
let private memberSmallSrc =
    memberHead + "let eligible = [3, 3, 1, -5]\nlet keep = mask(customer, lambda(c) -> contains(eligible, c))\n"
let private memberBitmapSrc =
    memberHead
    + "let evens = [0, 2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 22, 24, 26, 28, 30, 44, 46, 58, 60]\n"
    + "let keep = mask(customer, lambda(c) -> contains(evens, c))\n"
let private memberExtremeSrc =
    memberHead
    + "let wide = [9223372036854775807, -9223372036854775807 - 1, 0, -1, 1, 1000000007, -1000000007, 4611686018427387904, -4611686018427387904, 123456789012345, -98765432109876, 77, -77, 1 - 9223372036854775807, 9223372036854775806, 5, 5, 0]\n"
    + "let keep = mask(customer, lambda(c) -> contains(wide, c))\n"
let private memberComputedSrc =
    "type Ev = Idx<37>\ntype Row = Idx<211>\n"
    + "let elig = method_for(range<Ev>) <@> lambda(e) -> (Int64(e) - 18) * 1000003 |> compute\n"
    + "let cust = method_for(range<Row>) <@> lambda(i) -> (Int64(i) * 7) % 61 |> compute\n"
    + "let flags = method_for(cust) <@> lambda(c) where omp -> contains(elig, c) |> compute\n"
let private memberFoldSrc =
    memberHead + "let B = [2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 22, 24, 26, 28, 30, 32, 34]\n"
    + "let weighted = reduce(method_for(customer) <@> lambda(x) -> if contains(B, x) then x * 10 else 1, (+), 1000)\n"
let private memberWritingKernelSrc =
    "type One = Idx<1>\n" + memberHead
    + "let mut calls: Array<Int64 like One> = [0]\n"
    + "function tick(c: mut Array<Int64 like One>, x: Int64) -> Int64 = {\n    c(0) = c(0) + 1\n    x\n}\n"
    + "let B = [8, 23, 99]\nlet keep = mask(customer, lambda(v) -> contains(B, tick(calls, v)))\n"
let private memberWritingProducerSrc =
    "type One = Idx<1>\ntype Three = Idx<3>\n" + memberHead
    + "let mut log: Array<Int64 like One> = [0]\n"
    + "function make(l: mut Array<Int64 like One>) -> Array<Int64 like Three> = {\n    l(0) = l(0) + 1\n    [4, 8, 15]\n}\n"
    + "let logged = make(log)\nlet keep = mask(customer, lambda(v) -> contains(logged, v))\n"
let private memberAssignedSrc =
    memberHead + "let mut moving = [1, 2, 3]\nmoving(0) = 9\nlet keep = mask(customer, lambda(v) -> contains(moving, v))\n"
let private memberDeferredSetSrc =
    "type Three = Idx<3>\n" + memberHead
    + "let lazy = method_for(range<Three>) <@> lambda(i) -> Int64(i) * 8 + 8\n"
    + "let keep = mask(customer, lambda(v) -> contains(lazy, v))\n"
let private memberDeferredHostSrc =
    memberHead + "let eligible = [3, 3, 1, -5]\nlet flags = method_for(customer) <@> lambda(c) -> contains(eligible, c)\nlet n = reduce(method_for(flags) <@> lambda(b) -> if b then 1 else 0, (+), 0)\n"
let private memberSharedKernelSrc =
    memberHead + "let eligible = [3, 3, 1, -5]\nlet pred = lambda(c: Int64) -> contains(eligible, c)\nlet k1 = mask(customer, pred)\nlet k2 = mask(customer, pred)\n"
let private memberFloatSrc =
    "type Row = Idx<4>\nlet xs: Array<Float64 like Row> = [1.5, 2.5, 0.0, -1.0]\nlet fs = [1.5, -0.0, 2.5]\nlet keep = mask(xs, lambda(x) -> contains(fs, x))\n"
let private memberWritingSeedSrc =
    "type One = Idx<1>\n" + memberHead
    + "let mut log: Array<Int64 like One> = [0]\n"
    + "function seed(l: mut Array<Int64 like One>) -> Int64 = {\n    l(0) = l(0) + 1\n    1000\n}\n"
    + "let B = [2, 4, 6]\n"
    + "let weighted = reduce(method_for(customer) <@> lambda(x) -> if contains(B, x) then x else 0, (+), seed(log))\n"
let private memberDeferredAliasSrc =
    "type Three = Idx<3>\ntype Row = Idx<5>\n"
    + "let src: Array<Int64 like Row> = [1, 4, 3, 9, 2]\n"
    + "let lazy = method_for(range<Three>) <@> lambda(i) -> Int64(i) + 1\n"
    + "let B = lazy\nlet C = B\n"
    + "let flags_b = method_for(src) <@> lambda(v) -> contains(B, v) |> compute\n"
    + "let flags_c = method_for(src) <@> lambda(v) -> contains(C, v) |> compute\n"
    + "let stored = [4, 9, 7]\nlet S1 = stored\nlet S2 = S1\n"
    + "let flags_s = method_for(src) <@> lambda(v) -> contains(S2, v) |> compute\n"
let private memberRule = "member-hoist"
let private memberApplied (subject: string) (d: Blade.Effects.Decision) = applied d && d.Subject = subject

let private memberHoistDecisionCases () =
    [ decisionCase "decision_member_hoist_small_linear" memberSmallSrc memberRule
          (fun d -> memberApplied "keep" d && evidenceHas "representation: linear (span 9, M 4)" d
                    && evidenceHas "set `eligible` (rank-1 dense, Int64 keys): ONE search structure built immediately before `keep`" d)
          "applied: four literal keys, linear"
      decisionCase "decision_member_hoist_literal_bitmap" memberBitmapSrc memberRule
          (fun d -> memberApplied "keep" d && evidenceHas "representation: bitmap (span 61, M 20)" d) "applied: bitmap (span 61, M 20)"
      decisionCase "decision_member_hoist_extreme_keys_hash" memberExtremeSrc memberRule
          (fun d -> memberApplied "keep" d && evidenceHas "representation: hash (span 2^64, M 18)" d) "applied: the full Int64 span is a hash (span 2^64, no overflow)"
      decisionCase "decision_member_hoist_computed_set_runtime_choice" memberComputedSrc memberRule
          (fun d -> memberApplied "flags" d && evidenceHas "representation: chosen at run time from (M, min, max) only, never from timing" d)
          "applied: a computed stored set's representation is chosen at run time"
      decisionCase "decision_member_hoist_five_party_matrix" memberSmallSrc memberRule
          (fun d -> memberApplied "keep" d
                    && evidenceHas "membershipAdmits (five parties, each through Optimize.scheduleMayWriteIn): set producer `eligible`: stored at its own site" d
                    && evidenceHas "consumer kernel(s) `lambda in keep`: no write" d
                    && evidenceHas "outer fold kernel(s) / seed(s): none (no fold in the host)" d
                    && evidenceHas "between the build and `keep`: no binding" d
                    && evidenceHas "parallel: none (a serial host)" d)
          "the five-party matrix recorded"
      decisionCase "decision_member_hoist_fold_kernel_and_seed_parties" memberFoldSrc memberRule
          (fun d -> memberApplied "weighted" d && evidenceHas "outer fold kernel(s): 1, no write; seed(s): 1, no write" d)
          "a fold host: its kernel and seed are parties, both write-free"
      decisionCase "decision_member_hoist_omp_kernel_read_only" memberComputedSrc memberRule
          (fun d -> memberApplied "flags" d && evidenceHas "`where omp` -- the build runs serially before the team and every thread only reads it; no function-valued parameter" d)
          "a `where omp` kernel: serial build, read-only probes"
      decisionCase "decision_member_hoist_target_contract" memberSmallSrc memberRule
          (fun d -> memberApplied "keep" d && evidenceHas "target: evaluated exactly once per probe, before any cell is read" d)
          "the one target-evaluation contract is stated"
      withGate "BLADE_MEMBER_HOIST" "hash" (fun () ->
          decisionCase "decision_member_hoist_forced_hash" memberSmallSrc memberRule
              (fun d -> memberApplied "keep" d && evidenceHas "representation: hash (span 9, M 4) -- forced by BLADE_MEMBER_HOIST=hash" d)
              "BLADE_MEMBER_HOIST=hash forces the hash")
      withGate "BLADE_MEMBER_HOIST" "linear" (fun () ->
          decisionCase "decision_member_hoist_forced_linear" memberBitmapSrc memberRule
              (fun d -> memberApplied "keep" d && evidenceHas "representation: linear (span 61, M 20) -- forced by BLADE_MEMBER_HOIST=linear" d)
              "BLADE_MEMBER_HOIST=linear forces the scan structure")
      withGate "BLADE_MEMBER_HOIST" "bitmap" (fun () ->
          decisionCase "decision_member_hoist_forced_bitmap_outside_domain" memberExtremeSrc memberRule
              (fun d -> memberApplied "keep" d && evidenceHas "BLADE_MEMBER_HOIST=bitmap forced, outside its domain (the span exceeds the bitmap's 2^26-bit cap): built as hash" d)
              "a forced bitmap over the full Int64 span falls back to the hash")
      withGate "BLADE_MEMBER_HOIST" "0" (fun () ->
          decisionCase "decision_member_hoist_disabled" memberSmallSrc memberRule
              (fun d -> d.Subject = "keep" && declinedMentioning "disabled by BLADE_MEMBER_HOIST" d) "declined: disabled by BLADE_MEMBER_HOIST")
      decisionCase "decision_member_hoist_declined_writing_kernel" memberWritingKernelSrc memberRule
          (fun d -> d.Subject = "keep" && declinedMentioning "the consumer kernel `lambda in keep` may write" d
                    && evidenceHas "DECLINES: the consumer kernel" d)
          "declined: the probing kernel writes (five-party matrix)"
      decisionCase "decision_member_hoist_declined_writing_producer" memberWritingProducerSrc memberRule
          (fun d -> d.Subject = "keep" && declinedMentioning "the set producer `logged` may write" d) "declined: the set's producer writes"
      decisionCase "decision_member_hoist_declined_writing_seed" memberWritingSeedSrc memberRule
          (fun d -> d.Subject = "weighted" && declinedMentioning "a fold seed of `weighted` may write" d) "declined: the fold's seed writes"
      decisionCase "decision_member_hoist_declined_assigned_set" memberAssignedSrc memberRule
          (fun d -> d.Subject = "keep" && declinedMentioning "the set `moving` is" d) "declined: the set is written by the program"
      decisionCase "decision_member_hoist_declined_deferred_set" memberDeferredSetSrc memberRule
          (fun d -> d.Subject = "keep" && declinedMentioning "the set `lazy` is not proved stored at the build point: `lazy` is a deferred computation" d) "declined: a deferred set (v1.1)"
      decisionCase "decision_member_hoist_declined_deferred_host" memberDeferredHostSrc memberRule
          (fun d -> d.Subject = "flags" && declinedMentioning "the host `flags` is a deferred computation" d) "declined: a deferred host"
      decisionCase "decision_member_hoist_declined_shared_kernel" memberSharedKernelSrc memberRule
          (fun d -> declinedMentioning "times in the program (v1: a kernel that runs only inside its one host)" d)
          "declined: a kernel named by two hosts"
      decisionCase "decision_member_hoist_declined_float_keys" memberFloatSrc memberRule
          (fun d -> d.Subject = "keep" && declinedMentioning "the set's elements are Float64" d) "declined: Float64 keys (v1.1)"
      // the storage proof follows alias chains (Codex diff review of W2-1,
      // finding 2): an alias of a deferred map declines, an alias chain of a
      // literal is admitted
      decisionCase "decision_member_hoist_declined_deferred_alias_chain" memberDeferredAliasSrc memberRule
          (fun d -> d.Subject = "flags_c"
                    && declinedMentioning "the set `C` is not proved stored at the build point: `C` is an alias of `B`, and `B` is an alias of `lazy`, and `lazy` is a deferred computation" d)
          "declined: a two-hop alias of a deferred map"
      decisionCase "decision_member_hoist_stored_alias_chain_applied" memberDeferredAliasSrc memberRule
          (fun d -> memberApplied "flags_s" d && evidenceHas "an alias of `S1`, an alias of `stored`, an array literal" d)
          "applied: a two-hop alias of a literal, proved stored"
      // the canonical semijoin keeps its mask INLINE in the compound's side
      // table: the compound binding (whose site runs the mask) is the host
      decisionCase "decision_member_hoist_inline_semijoin_compound_host"
          (memberHead + "let eligible = [3, 3, 1, -5]\nlet semi = compound(customer, mask(customer, lambda(c) -> contains(eligible, c)))\n") memberRule
          (fun d -> memberApplied "semi" d && evidenceHas "built immediately before `semi`" d)
          "applied: the compound whose inline mask probes is the host" ]

/// The C++ emission of a planned hoist: the file-scope structure, its build
/// before the host and free after it, the probe testing `kind` first; and
/// the one target-evaluation contract in every IIFE (set, then target once).
let private memberHoistEmission () =
    let name = "member_hoist_cpp_build_probe_free"
    match cppOfSource name memberBitmapSrc with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        let want =
            [ "#include \"blade_member.h\""
              "static blade_member_i64 __bl_member_"
              "blade_member_build_i64(&__bl_member_"
              "nested_array_utilities::pool_base(evens.data), (int64_t)(20), 0);"
              "blade_member_free_i64(&__bl_member_"
              "auto&& __cs = evens; const auto __cx = c; if (__bl_member_"
              ".kind != BLADE_MEMBER_NONE) return blade_member_test_i64(&__bl_member_" ]
        let missing = want |> List.filter (fun s -> not (cpp.Contains s))
        let b = cpp.IndexOf "blade_member_build_i64(&__bl_member_"
        let h = cpp.IndexOf "Array<bool, 1> keep"
        let f = cpp.IndexOf "blade_member_free_i64(&__bl_member_"
        if missing.IsEmpty && b >= 0 && h > b && f > h then
            resultLine Pass name "structure declared, built before `keep`, freed after it; the probe tests kind first"
            true
        else
            resultLine Fail name ($"""missing: {(String.concat " | " missing)}; build/host/free at {b}/{h}/{f}""")
            false

let private memberHoistGateOffEmission () =
    let name = "member_hoist_cpp_gate_off_keeps_scan"
    match withGate "BLADE_MEMBER_HOIST" "0" (fun () -> cppOfSource name memberBitmapSrc) with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        if not (cpp.Contains "blade_member") && cpp.Contains "auto&& __cs = evens; const auto __cx = c; for (size_t __ci = 0;" then
            resultLine Pass name "no structure; the scan evaluates the target once before reading any cell"
            true
        else
            resultLine Fail name "expected no blade_member_* text and the contract-form scan"
            false

/// Lower (warnings captured), VALIDATE the IR, then hand it to `k`: a
/// lowering or validation refusal, or an exception anywhere, is an Error.
let private loweredValidated (src: string) (k: Blade.IR.IRProgram -> 'a) : Result<'a, string> =
    try
        match fst (Lowering.lowerCaptured src) with
        | Error e -> Error ("lowering refused: " + e)
        | Ok ir ->
            match IRValidate.validateIR ir with
            | Error errs -> Error ("IR validation refused: " + String.concat "; " errs)
            | Ok ir -> Ok (k ir)
    with ex -> Error ("raised: " + ex.Message)

/// Where one planned structure `g` (host `host`) sits in emitted C++ `lines`
/// (`mainAt` = the `int main(` line): built ONCE in main and reported on the
/// next line; the host's first statement -- its first mention in main outside
/// a comment and the member calls -- AFTER the report; the host's cell loop
/// (its first `for (` from there: a braced body to its closing brace, a
/// brace-less one to its statement's `;`) closing BEFORE the free, which
/// happens once. Every probe of `g` runs only inside that loop: inline in it,
/// or in a callable (a kernel function before main, a consumer-local
/// `auto W = [&] ...` wrapper in main, transitively) EVERY invocation of
/// which is inside the loop. A kernel copy nothing invokes is dead code (the
/// emitter keeps the function when it inlines the kernel into the loop) and
/// is counted, not failed; at least one probe must run in the loop. Ok = the
/// number of dead kernel copies.
let private memberHostPlacement (lines: string[]) (mainAt: int) (g: string, host: string) : Result<int, string> =
    let re (p: string) = System.Text.RegularExpressions.Regex(p)
    let esc (s: string) = System.Text.RegularExpressions.Regex.Escape s
    let idx (needle: string) = lines |> Array.indexed |> Array.filter (fun (_, l) -> l.Contains needle) |> Array.map fst
    let isComment (l: string) = l.TrimStart().StartsWith "//"
    // the line the brace opened at or after line `s` closes on
    let blockEnd (s: int) =
        let mutable depth = 0
        let mutable opened = false
        let mutable e = -1
        let mutable k = s
        while e < 0 && k < lines.Length do
            for ch in lines.[k] do
                if ch = '{' then
                    depth <- depth + 1
                    opened <- true
                elif ch = '}' then depth <- depth - 1
            if opened && depth <= 0 then e <- k
            k <- k + 1
        e
    // the last line of the loop whose header is line `s`
    let loopEnd (s: int) =
        if lines.[s].TrimEnd().EndsWith "{" then blockEnd s
        elif s + 1 < lines.Length && lines.[s + 1].TrimStart().StartsWith "{" then blockEnd (s + 1)
        else
            // brace-less: the one statement (or nested loop) that is its body
            let mutable depth = 0
            let mutable opened = false
            let mutable e = -1
            let mutable k = s + 1
            while e < 0 && k < lines.Length do
                for ch in lines.[k] do
                    if ch = '{' then
                        depth <- depth + 1
                        opened <- true
                    elif ch = '}' then depth <- depth - 1
                let t = lines.[k].TrimEnd()
                if depth = 0 && (t.EndsWith ";" || (opened && t.EndsWith "}")) then e <- k
                k <- k + 1
            e
    // the function definition (column 0, before main) a line belongs to
    let funcRe = re @"^[A-Za-z_][^;()]*?\b([A-Za-z_][A-Za-z0-9_]*)\(.*\)\s*\{\s*$"
    let enclosingFn (p: int) =
        seq { p .. -1 .. 0 } |> Seq.tryPick (fun k -> let m = funcRe.Match lines.[k] in if m.Success then Some m.Groups.[1].Value else None)
    let wrapRe = re @"^\s*auto (\w+) = \[&\]"
    // a declaration or definition of `fn` (column 0), not a call
    let declOf (fn: string) (l: string) =
        l.Length > 0 && not (System.Char.IsWhiteSpace l.[0]) && (re ("^[A-Za-z_][^=;]*?\\b" + esc fn + "\\(")).IsMatch l
    // every main line that (transitively) invokes callable `fn`; -1 = a call
    // that resolves to no function and no main line
    let rec mainInvocations (visited: Set<string>) (fn: string) : int list =
        if visited.Contains fn then []
        else
            let visited = visited.Add fn
            let callRe = re ("(?<![A-Za-z0-9_])" + esc fn + "\\(")
            [ for k in 0 .. lines.Length - 1 do
                let l = lines.[k]
                if callRe.IsMatch l && not (declOf fn l) && not (isComment l) then
                    if mainAt >= 0 && k > mainAt then
                        let w = wrapRe.Match l
                        if w.Success && w.Groups.[1].Value <> fn then yield! mainInvocations visited w.Groups.[1].Value
                        else yield k
                    else
                        match enclosingFn k with
                        | Some caller when caller <> fn -> yield! mainInvocations visited caller
                        | Some _ -> ()
                        | None -> yield -1 ]
    match idx $"blade_member_build_i64(&{g}, ", idx $"blade_member_note_i64(&{g}, ", idx $"blade_member_free_i64(&{g});" with
    | [| b |], [| n |], [| f |] when mainAt >= 0 && b > mainAt && n = b + 1 && f > n ->
        let hostRe = re ("(?<![A-Za-z0-9_])" + esc host + "(?![A-Za-z0-9])")
        match [ mainAt + 1 .. lines.Length - 1 ]
              |> List.tryFind (fun k -> not (isComment lines.[k]) && not (lines.[k].Contains "blade_member_") && hostRe.IsMatch lines.[k]) with
        | None -> Error $"`{host}` is never mentioned in main"
        | Some h when h <= n -> Error $"`{host}`'s code starts at line {h + 1}, before its structure is built and reported (line {n + 1})"
        | Some h ->
        match [ h .. f - 1 ] |> List.tryFind (fun k -> lines.[k].Contains "for (") with
        | None -> Error $"no cell loop of `{host}` between its first statement (line {h + 1}) and the free"
        | Some ls ->
        let le = loopEnd ls
        if le < 0 || le >= f then Error $"`{host}`'s cell loop (line {ls + 1}) does not complete before the free (line {f + 1})"
        else
        let inLoop (k: int) = k >= ls && k <= le
        let calledOnlyInLoop (fn: string) : Result<bool, string> =
            let inv = mainInvocations Set.empty fn
            match inv |> List.tryFind (inLoop >> not) with
            | Some k -> Error $"`{fn}`, which probes `{g}`, is invoked outside `{host}`'s loop (lines {ls + 1}-{le + 1}) at line {k + 1}"
            | None -> Ok (not inv.IsEmpty)
        let probeVerdict (p: int) : Result<bool, string> =
            if p > mainAt then
                if inLoop p then Ok true
                else
                    let w = wrapRe.Match lines.[p]
                    if w.Success then calledOnlyInLoop w.Groups.[1].Value
                    else Error $"the probe at line {p + 1} is in main outside `{host}`'s loop (lines {ls + 1}-{le + 1})"
            else
                match enclosingFn p with
                | Some fn -> calledOnlyInLoop fn
                | None -> Error $"the probe at line {p + 1} belongs to no function"
        let verdicts = idx $"blade_member_test_i64(&{g}, " |> Array.map probeVerdict
        match verdicts |> Array.tryPick (function Error e -> Some e | Ok _ -> None) with
        | Some e -> Error e
        | None when not (verdicts |> Array.contains (Ok true)) -> Error $"no probe of `{g}` runs inside `{host}`'s loop"
        | None -> Ok (verdicts |> Array.filter (fun v -> v = Ok false) |> Array.length)
    | _ -> Error $"`{g}` is not built once in main, reported on the next line and freed once after"

/// THE C++ ROUTE of every contains program in the corpus (Codex diff reviews
/// of W2-1, finding 4 and the second look's NEW 1; the table is
/// LlvmTests.memberHoistRoutes, which the LLVM route shares, and covers the
/// corpus's contains programs exactly -- asserted here and there). For each
/// planned host, BY NAME (the file-scope structure's declaration names its
/// host), `memberHostPlacement`: built once and reported before the host's
/// first statement, freed after its cell loop closes, every probe run only
/// inside that loop; the compound sets' builds read the cardinality; the
/// structures are exactly the row's hosts, in binding order.
/// A row without a plan has no member text at all. A lowering or validation
/// error, or an exception, fails the row whatever it says.
let private memberHoistCorpusRoutes () : bool list =
    let re (p: string) = System.Text.RegularExpressions.Regex(p)
    [ let (missing, stale) = Blade.Tests.LlvmTests.memberHoistRouteCoverage ()
      let coverName = "member_hoist_route_cpp: the table covers the corpus's contains programs, exactly"
      if missing.IsEmpty && stale.IsEmpty then
          resultLine Pass coverName ($"{Blade.Tests.LlvmTests.memberHoistRoutes.Length} rows = the corpus's contains programs")
          yield true
      else
          resultLine Fail coverName ($"""missing rows: [{String.concat "; " missing}]; stale rows: [{String.concat "; " stale}]""")
          yield false
      for r in Blade.Tests.LlvmTests.memberHoistRoutes do
        let name = $"member_hoist_route_cpp: {r.Category} `{r.Test}` -- {r.Hosts.Length} build(s)"
        match (try Blade.Tests.Corpus.category r.Category |> List.tryFind (fun (n, _) -> n = r.Test) with _ -> None) with
        | None ->
            resultLine Fail name "the corpus file is missing (renamed?)"
            yield false
        | Some (_, src) ->
            match loweredValidated src (fun ir -> fst (CodeGen.genSelfContainedProgramFromIR ir name)) with
            | Error e ->
                resultLine Fail name e
                yield false
            | Ok cpp ->
                let lines = cpp.Replace("\r\n", "\n").Split('\n')
                let mainAt = lines |> Array.tryFindIndex (fun l -> l.StartsWith "int main(") |> Option.defaultValue -1
                let declRe = re @"^static blade_member_i64 (__bl_member_\d+_\d+); // member-hoist v1: the search structure over `[^`]+` for `([^`]+)`"
                let globals =
                    lines |> Array.choose (fun l -> let m = declRe.Match l in if m.Success then Some (m.Groups.[1].Value, m.Groups.[2].Value) else None)
                    |> List.ofArray
                let compoundCount =
                    lines |> Array.filter (fun l -> l.Contains "blade_member_build_i64(&" && l.Contains ".idx->cardinality)") |> Array.length
                let ok, detail =
                    if r.Hosts.IsEmpty then
                        (not (cpp.Contains "blade_member_"), "no plan: no member text, the as-written scan")
                    elif (globals |> List.map snd) <> r.Hosts then
                        (false, $"""structures for [{String.concat ", " (globals |> List.map snd)}], want [{String.concat ", " r.Hosts}]""")
                    else
                    let placements = globals |> List.map (memberHostPlacement lines mainAt)
                    match placements |> List.tryPick (function Error why -> Some why | Ok _ -> None) with
                    | Some why -> (false, why)
                    | None ->
                        if compoundCount <> r.CompoundBuilds then (false, $"{compoundCount} compound build(s), want {r.CompoundBuilds}")
                        else
                            let dead = placements |> List.sumBy (function Ok d -> d | Error _ -> 0)
                            (true, $"""{String.concat ", " r.Hosts}: build -> report -> host's cell loop, every probe run only inside it (inline or through a kernel invoked only there; {dead} uncalled kernel copies) -> free; {r.CompoundBuilds} over a compound's cardinality""")
                resultLine (if ok then Pass else Fail) name detail
                yield ok ]

/// The route checker DISCRIMINATES (Codex second look of W2-1, NEW 1): over
/// the real emission of `sql-unique-contains` "Member Hoist Small Set
/// Linear", the unmutated text places `keep`, and three placement mutations
/// are each refused by `memberHostPlacement` FOR THEIR OWN REASON -- the free
/// moved inside the host's cell loop, the build and report moved after the
/// host's first statement, one more invocation of the probing kernel's
/// wrapper after the free.
let private memberHoistPlacementMutations () : bool list =
    let name = "member_hoist_route_cpp_checker_refuses_misplacements"
    match (try Blade.Tests.Corpus.category "sql-unique-contains" |> List.tryFind (fun (n, _) -> n = "Member Hoist Small Set Linear") with _ -> None) with
    | None -> resultLine Fail name "the corpus file is missing (renamed?)"; [ false ]
    | Some (_, src) ->
    match loweredValidated src (fun ir -> fst (CodeGen.genSelfContainedProgramFromIR ir name)) with
    | Error e -> resultLine Fail name e; [ false ]
    | Ok cpp ->
        let lines = cpp.Replace("\r\n", "\n").Split('\n')
        let mainOf (arr: string[]) = arr |> Array.findIndex (fun l -> l.StartsWith "int main(")
        let declRe = System.Text.RegularExpressions.Regex(@"^static blade_member_i64 (__bl_member_\d+_\d+); // member-hoist v1: the search structure over `[^`]+` for `keep`")
        let wrapper (n: int) (f: int) =
            [ n + 1 .. f - 1 ] |> List.tryPick (fun k ->
                let m = System.Text.RegularExpressions.Regex.Match(lines.[k], @"auto (\w+) = \[&\]")
                if m.Success then Some m.Groups.[1].Value else None)
        match lines |> Array.tryPick (fun l -> let m = declRe.Match l in if m.Success then Some m.Groups.[1].Value else None) with
        | None -> resultLine Fail name "no structure for `keep`"; [ false ]
        | Some g ->
        let at (needle: string) = lines |> Array.findIndex (fun l -> l.Contains needle)
        let b, n, f = at $"blade_member_build_i64(&{g}, ", at $"blade_member_note_i64(&{g}, ", at $"blade_member_free_i64(&{g});"
        match wrapper n f with
        | None -> resultLine Fail name "no consumer-local wrapper between the report and the free"; [ false ]
        | Some w ->
        let hostRe = System.Text.RegularExpressions.Regex(@"(?<![A-Za-z0-9_])keep(?![A-Za-z0-9])")
        let h = [ n + 1 .. f - 1 ] |> List.find (fun k -> hostRe.IsMatch lines.[k] && not (lines.[k].TrimStart().StartsWith "//"))
        let ls = [ h .. f - 1 ] |> List.find (fun k -> lines.[k].Contains "for (")
        // drop the lines `remove`, insert `extra` right after line `after`
        let moved (remove: int list) (after: int) (extra: string list) =
            [| for k in 0 .. lines.Length - 1 do
                 if not (List.contains k remove) then yield lines.[k]
                 if k = after then yield! extra |]
        let mutants =
            [ "the free inside the host's loop", moved [ f ] ls [ lines.[f] ], "does not complete before the free"
              "the build and report after the host's first statement", moved [ b; n ] h [ lines.[b]; lines.[n] ], "before its structure is built and reported"
              "the kernel's wrapper invoked after the free", moved [] f [ $"    bool __late_probe = {w}(0);" ], "is invoked outside" ]
        [ match memberHostPlacement lines (mainOf lines) (g, "keep") with
          | Ok _ -> resultLine Pass (name + ": unmutated") "the real emission places `keep`"; yield true
          | Error e -> resultLine Fail (name + ": unmutated") e; yield false
          for (what, mutant, reason) in mutants do
            match memberHostPlacement mutant (mainOf mutant) (g, "keep") with
            | Error why when why.Contains reason -> resultLine Pass (name + ": " + what) ("refused: " + why); yield true
            | Error why -> resultLine Fail (name + ": " + what) ($"refused for ANOTHER reason: {why} (want `{reason}`)"); yield false
            | Ok _ -> resultLine Fail (name + ": " + what) "the checker accepted a misplaced structure"; yield false ]

/// THE FAIL-CLOSED EMISSION GUARDS (Codex diff reviews of W2-1, finding 2 and
/// the second look's NEW 2): a plan the planner would never make -- a
/// hand-recorded hoist of the DEFERRED map `lazy` for host `flags_b` --
/// reaches both emitters. The C++ lane must EMIT the program (no exception),
/// build nothing, keep the as-written fallback scan (the set bound, the
/// target evaluated once, the loop) and record the build-point decline. The
/// LLVM lane must record the same decline and either emit with no `init` and
/// the scan, or refuse with exactly the known unsupported-computed-set
/// reason. A lowering error, an exception or any other refusal fails.
let private memberHoistFailClosedGuards () : bool list =
    // the set `lazy` is a deferred map the host captures: the C++ lane
    // materializes it inside the host's own prelude, AFTER the build point
    let src =
        "type Three = Idx<3>\ntype Row = Idx<5>\n"
        + "let src: Array<Int64 like Row> = [1, 4, 3, 9, 2]\n"
        + "let lazy = method_for(range<Three>) <@> lambda(i) -> Int64(i) + 1\n"
        + "let flags_b = method_for(src) <@> lambda(v) -> contains(lazy, v) |> compute\n"
    let injectAndRun (name: string) (emit: Blade.IR.IRProgram -> Result<string, string>) (want: string) (judge: Result<string, string> -> Result<string, string>) =
        match loweredValidated src id with
        | Error e -> resultLine Fail name e; false
        | Ok ir ->
            let bs = ir.Modules |> List.collect (fun m -> m.Bindings)
            match bs |> List.tryFind (fun b -> b.Name = "flags_b"), bs |> List.tryFind (fun b -> b.Name = "lazy") with
            | Some host, Some set ->
                let kernels = System.Collections.Generic.HashSet<int>()
                Blade.IR.iterIRExpr (fun n ->
                    match n with
                    | Blade.IR.IRApplyCombinator info -> (match info.Kernel with Blade.IR.IRVar (k, _) -> kernels.Add k |> ignore | _ -> ())
                    | _ -> ()) host.Value
                Blade.Optimize.MemberHoistTable.record
                    { HostId = host.Id; HostName = host.Name; SetId = set.Id; SetName = set.Name
                      Kernels = Set.ofSeq kernels; Forced = 0; Admission = "hand-built" }
                Blade.Effects.Decisions.start ()
                let emitted = try Ok (emit ir) with ex -> Error ("raised: " + ex.Message)
                let ds = Blade.Effects.Decisions.drain ()
                let declined = ds |> List.exists (fun d -> d.Rule = "member-hoist" && d.Subject = "flags_b" && declinedMentioning want d)
                match emitted with
                | Error e -> resultLine Fail name e; false
                | Ok outcome ->
                match judge outcome with
                | Ok detail when declined ->
                    resultLine Pass name ($"the build-point decline is recorded; {detail}")
                    true
                | Ok _ ->
                    let seen = ds |> List.filter (fun d -> d.Rule = "member-hoist") |> List.map Blade.Effects.Decisions.render |> String.concat " | "
                    resultLine Fail name ($"no build-point decline recorded; saw: {seen}")
                    false
                | Error why -> resultLine Fail name why; false
            | _ -> resultLine Fail name "fixture bindings not found"; false
    [ injectAndRun "member_hoist_cpp_fail_closed_deferred_capture"
          (fun ir -> Ok (fst (CodeGen.genSelfContainedProgramFromIR ir "member_fail_closed")))
          "C++ emission: the planned build over `lazy` is not emitted -- it is a deferred computation at the build point"
          (fun outcome ->
              match outcome with
              | Ok cpp when cpp.Contains "blade_member_build_i64(&" -> Error "a build was emitted"
              | Ok cpp when cpp.Contains "auto&& __cs = lazy; const auto __cx = v;" && cpp.Contains "for (size_t __ci = 0;" ->
                  Ok "C++ emitted with no build; the fallback scan binds the set, evaluates the target once, then loops"
              | Ok _ -> Error "the fallback scan is missing"
              | Error e -> Error ("the C++ lane refused: " + e))
      injectAndRun "member_hoist_llvm_fail_closed_deferred_capture"
          (fun ir -> EmitLlvm.tryEmitProgramNamed "member_fail_closed" ir)
          "llvm lane: the planned build over `lazy` is not emitted"
          (fun outcome ->
              match outcome with
              | Ok ll when ll.Contains "@blade_member_init_i64(" -> Error "an init was emitted"
              | Ok ll when ll.Contains "has.cond" -> Ok "emitted with no init; the linear scan"
              | Ok _ -> Error "emitted, but with neither a structure nor the scan"
              | Error why when why.Contains "contains over a computed (unstored) set" -> Ok "refused with the known unsupported-computed-set reason (the program falls back to C++)"
              | Error why -> Error ("refused for ANOTHER reason: " + why)) ]

let private memberHoistForcedEmission () =
    [ for (knob, k) in [ ("linear", 1); ("bitmap", 2); ("hash", 3) ] do
        let name = $"member_hoist_cpp_forced_{knob}"
        match withGate "BLADE_MEMBER_HOIST" knob (fun () -> cppOfSource name memberBitmapSrc) with
        | Error e -> resultLine Fail name e; yield false
        | Ok cpp ->
            let needle = $"(int64_t)(20), {k});"
            if cpp.Contains needle then
                resultLine Pass name $"the build passes forced representation {k}"
                yield true
            else
                resultLine Fail name $"missing `{needle}`"
                yield false ]

// ---------------------------------------------------------------------------
// Compute elision (BLADE_COMPUTE_ELIDE, Optimize.elideSingleConsumerComputesModule;
// docs/plans/plan-identity-transpose-compute.md P3 entry). The VALUES are
// corpus functions/539-547; these read the emission: an elided single-consumer
// `|> compute` allocates nothing in its body in either lane, a declined one
// keeps its pool, and every outcome is a `compute-elision` record.
// ---------------------------------------------------------------------------

let private ceFoldSrc =
    "function total(a: T^1) -> T^0 = {\n"
    + "    let x = method_for(a) <@> lambda(v) -> v * 2.0 + 1.0 |> compute\n"
    + "    reduce(x, (+))\n"
    + "}\n"
    + "let a = [1.0, 2.0, 3.0]\n"
    + "let t = total(a)\n"

let private ceMapSrc =
    "function scale_add(a: T^1, b: T^1) -> T^1 = {\n"
    + "    let x = method_for(a) <@> lambda(v) -> v * 2.0 |> compute\n"
    + "    method_for(zip(x, b)) <@> lambda(p, q) -> p + q |> compute\n"
    + "}\n"
    + "let a = [1.0, 2.0, 3.0]\n"
    + "let b = [10.0, 20.0, 30.0]\n"
    + "let r = scale_add(a, b)\n"

let private ceTwoSrc =
    "function sum_and_product(a: T^1) -> T^0 = {\n"
    + "    let x = method_for(a) <@> lambda(v) -> v + 1.0 |> compute\n"
    + "    reduce(x, (+)) * reduce(x, (*))\n"
    + "}\n"
    + "let a = [1.0, 2.0, 3.0]\n"
    + "let p = sum_and_product(a)\n"

let private ceWriteSrc =
    "function snapshot() -> T^0 = {\n"
    + "    let mut w = [1.0, 2.0, 3.0]\n"
    + "    let x = method_for(w) <@> lambda(v) -> v * 2.0 |> compute\n"
    + "    w(1) = 100.0\n"
    + "    reduce(x, (+))\n"
    + "}\n"
    + "let s = snapshot()\n"

let private ceKeyedSrc =
    "function second(a: T^1) -> T^0 = {\n"
    + "    let x = method_for(a) <@> lambda(v) -> v * 3.0 |> compute\n"
    + "    x(1)\n"
    + "}\n"
    + "let a = [1.0, 2.0, 3.0]\n"
    + "let k = second(a)\n"

let private ceReturnedSrc =
    "function tripled(a: T^1) -> T^1 = {\n"
    + "    let x = method_for(a) <@> lambda(v) -> v * 3.0 |> compute\n"
    + "    x\n"
    + "}\n"
    + "let a = [1.0, 2.0, 3.0]\n"
    + "let r = tripled(a)\n"

let private ceEmptySrc =
    "function total(a: T^1) -> T^0 = {\n"
    + "    let x = method_for(a) <@> lambda(v) -> v * 2.0 + 1.0 |> compute\n"
    + "    reduce(x, (+))\n"
    + "}\n"
    + "let none: Array<Float64 like Idx<0>> = []\n"
    + "let t = total(none)\n"

// Codex review witnesses (a reader in the seed, a reader in a type's extent,
// a producer range bound that may fail): each must decline.
let private ceSeedReaderSrc =
    "function seeded_by_x(a: T^1) -> T^0 = {\n"
    + "    let x = method_for(a) <@> lambda(v) -> v * 2.0 |> compute\n"
    + "    reduce(x, (+), x(0))\n"
    + "}\n"
    + "let t = seeded_by_x([1.0, 2.0, 3.0])\n"

let private ceExtentReaderSrc =
    "function extent_of_x(a: T^1) -> Int64 = {\n"
    + "    let x = method_for(a) <@> lambda(v) -> v + 1 |> compute\n"
    + "    let y = method_for(0..extents(x)) <@> lambda(v) -> v + 1 |> compute\n"
    + "    let s = reduce(x, (+), 0)\n"
    + "    s + reduce(y, (+), 0)\n"
    + "}\n"
    + "let e = extent_of_x([1, 2, 3])\n"

let private ceFailingExtentSrc =
    "function f(z: Int64) -> Int64 = {\n"
    + "    let x = method_for(0..(10 / z)) <@> lambda(v) -> v + 1 |> compute\n"
    + "    let a = [8, 9]\n"
    + "    let bad = a(z - 1)\n"
    + "    let s = reduce(x, (+), 0)\n"
    + "    s + bad\n"
    + "}\n"
    + "let t = f(0)\n"

let private ceExtentWriteSrc =
    "function bump(a: mut Array<Int64 like Idx<3>>) -> Int64 = {\n"
    + "    a(0) = 100\n"
    + "    3\n"
    + "}\n"
    + "function f() -> Int64 = {\n"
    + "    let mut a = [1, 2, 3]\n"
    + "    let x = method_for(a) <@> lambda(v) -> v + 1 |> compute\n"
    + "    let y = method_for(0..bump(a)) <@> lambda(v) -> v + 1 |> compute\n"
    + "    let s = reduce(x, (+), 0)\n"
    + "    s + reduce(y, (+), 0)\n"
    + "}\n"
    + "let t = f()\n"

/// The body of the (specialized) C++ function whose name starts with `fn`.
let private ceCppBody (cpp: string) (fn: string) : string option =
    let m = System.Text.RegularExpressions.Regex.Match(cpp, @"\n[^\n;{}]*\b" + fn + @"\w*\([^\n;{}]*\) \{\n")
    if not m.Success then None
    else
        let start = m.Index + 1
        let stop = cpp.IndexOf("\n}\n", start)
        Some (if stop < 0 then cpp.Substring start else cpp.Substring(start, stop - start))

/// The body of the LLVM function `@bl_<fn>...`.
let private ceLlvmBody (ll: string) (fn: string) : string option =
    let ll = ll.Replace("\r\n", "\n")
    let m = System.Text.RegularExpressions.Regex.Match(ll, @"\ndefine [^\n]*@bl_" + fn + @"[^\n]*\{\n")
    if not m.Success then None
    else
        let start = m.Index + 1
        let stop = ll.IndexOf("\n}\n", start)
        Some (if stop < 0 then ll.Substring start else ll.Substring(start, stop - start))

let private ceAllocs (body: string) = System.Text.RegularExpressions.Regex.Matches(body, @"(?<!de)allocate<").Count

let private ceLlvmOf (name: string) (src: string) : Result<string, string> =
    match lower src with
    | Error e -> Error ($"lower: {e}")
    | Ok ir -> (try EmitLlvm.tryEmitProgramNamed name ir with ex -> Error ("raised: " + ex.Message))

/// C++ allocations in `fn`'s body with the gate on and off.
let private ceCppAllocCase (name: string) (src: string) (fn: string) (wantOn: int -> bool) (wantOff: int -> bool) (describe: string) =
    match cppOfSource name src, withGate "BLADE_COMPUTE_ELIDE" "0" (fun () -> cppOfSource name src) with
    | Ok on, Ok off ->
        match ceCppBody on fn, ceCppBody off fn with
        | Some bOn, Some bOff ->
            let aOn, aOff = ceAllocs bOn, ceAllocs bOff
            if wantOn aOn && wantOff aOff then
                resultLine Pass name ($"{fn}: {aOn} allocation(s) with the gate on, {aOff} off; {describe}")
                true
            else
                resultLine Fail name ($"{fn}: {aOn} allocation(s) on, {aOff} off; wanted {describe}")
                false
        | _ -> resultLine Fail name ($"no C++ body for `{fn}`"); false
    | Error e, _ | _, Error e -> resultLine Fail name e; false

let private ceLlvmFoldNoPool () =
    let name = "compute_elision_llvm_fold_no_pool"
    match ceLlvmOf name ceFoldSrc, withGate "BLADE_COMPUTE_ELIDE" "0" (fun () -> ceLlvmOf name ceFoldSrc) with
    | Ok on, Ok off ->
        match ceLlvmBody on "total", ceLlvmBody off "total" with
        | Some bOn, Some bOff ->
            let count (b: string) = System.Text.RegularExpressions.Regex.Matches(b, @"blade_alloc_cells").Count
            if count bOn = 0 && count bOff >= 1 then
                resultLine Pass name ($"llvm lane, total: 0 pool allocations with the gate on, {count bOff} off")
                true
            else
                resultLine Fail name ($"llvm lane, total: {count bOn} allocation(s) on, {count bOff} off; wanted 0 on, >= 1 off")
                false
        | _ -> resultLine Fail name "no LLVM body for `total`"; false
    | Error e, _ | _, Error e -> resultLine Fail name ($"the llvm lane refused: {e}"); false

let private ceSubject (s: string) (d: Blade.Effects.Decision) = d.Subject = s

// P3 remainder: one level deeper (x0 -> x1 -> fold), and the top level under a
// print selection.
let private ceChainSrc =
    "function chain(a: T^1) -> T^0 = {\n"
    + "    let x0 = method_for(a) <@> lambda(v) -> v * 2.0 |> compute\n"
    + "    let x1 = method_for(x0) <@> lambda(w) -> w + 1.0 |> compute\n"
    + "    reduce(x1, (+))\n"
    + "}\n"
    + "function deeper(a: T^1) -> T^0 = {\n"
    + "    let x0 = method_for(a) <@> lambda(v) -> v * 2.0 |> compute\n"
    + "    let x1 = method_for(x0) <@> lambda(w) -> w + 1.0 |> compute\n"
    + "    let x2 = method_for(x1) <@> lambda(u) -> u * 3.0 |> compute\n"
    + "    reduce(x2, (+))\n"
    + "}\n"
    + "let a = [1.0, 2.0, 3.0]\n"
    + "let t = chain(a)\n"
    + "let t3 = deeper(a)\n"

let private ceTopSrc =
    "let A = [1.0, 2.0, 3.0]\n"
    + "let x = method_for(A) <@> lambda(v) -> v * 2.0 + 1.0 |> compute\n"
    + "let s = reduce(x, (+))\n"

/// C++ allocations in main under BLADE_PRINT=s, compute elision on vs off.
let private ceTopAllocCase () =
    let name = "compute_elision_top_level_print_selection_no_pool"
    withGate "BLADE_PRINT" "s" (fun () ->
        match cppOfSource name ceTopSrc, withGate "BLADE_COMPUTE_ELIDE" "0" (fun () -> cppOfSource name ceTopSrc) with
        | Ok on, Ok off ->
            match ceCppBody on "main", ceCppBody off "main" with
            | Some bOn, Some bOff ->
                let aOn, aOff = ceAllocs bOn, ceAllocs bOff
                if aOn = aOff - 1 then
                    resultLine Pass name ($"main under --print s: {aOn} allocation(s) with the gate on, {aOff} off (x's pool elided)")
                    true
                else
                    resultLine Fail name ($"main under --print s: {aOn} on, {aOff} off; wanted exactly one fewer on")
                    false
            | _ -> resultLine Fail name "no C++ body for `main`"; false
        | Error e, _ | _, Error e -> resultLine Fail name e; false)

let private ceNoRecordFor (name: string) (src: string) (subject: string) =
    match decisionsOf src with
    | Error e -> resultLine Fail name e; false
    | Ok ds ->
        if ds |> List.exists (fun d -> d.Rule = "compute-elision" && d.Subject = subject) then
            resultLine Fail name ($"a `{subject}` compute-elision record exists; wanted none (a printed binding is not a candidate)")
            false
        else
            resultLine Pass name ($"no `{subject}` record: a printed top-level binding keeps today's form silently")
            true

let private ceRemainderCases () : bool list =
    [ ceCppAllocCase "compute_elision_chain_no_pool" ceChainSrc "chain" ((=) 0) (fun n -> n >= 2)
          "x0 and x1 both read in place by the fold"
      decisionCase "decision_compute_elision_chain_one_level_deeper" ceChainSrc "compute-elision"
          (fun d -> ceSubject "chain.x1" d && applied d && d.Evidence |> List.exists (fun e -> e.Contains "one level deeper"))
          "applied to chain.x1 (its producer's operand is x0's elided map)"
      decisionCase "decision_compute_elision_chain_deeper_declines" ceChainSrc "compute-elision"
          (fun d -> ceSubject "deeper.x2" d && declinedMentioning "a deeper chain, declines" d)
          "declined: deeper.x2 would be two levels deep"
      ceTopAllocCase ()
      withGate "BLADE_PRINT" "s" (fun () ->
          decisionCase "decision_compute_elision_top_level_print_selection" ceTopSrc "compute-elision"
              (fun d -> ceSubject "top level.x" d && applied d && d.Evidence |> List.exists (fun e -> e.Contains "the print selection licensed the elision"))
              "applied to the unprinted top-level x, licensed by the print selection")
      ceNoRecordFor "compute_elision_top_level_unselected_is_inert" ceTopSrc "top level.x"
      withGate "BLADE_PRINT" "x,s" (fun () ->
          ceNoRecordFor "compute_elision_top_level_printed_is_inert" ceTopSrc "top level.x") ]

let private computeElisionCases () : bool list =
    [ ceCppAllocCase "compute_elision_fold_no_pool" ceFoldSrc "total" ((=) 0) (fun n -> n >= 1)
          "the fold reads x's map in place (no pool) and the gate restores it"
      ceLlvmFoldNoPool ()
      ceCppAllocCase "compute_elision_map_one_pool" ceMapSrc "scale_add" ((=) 1) ((=) 2)
          "only the returned consumer's pool remains"
      ceCppAllocCase "compute_elision_two_consumers_keep_pool" ceTwoSrc "sum_and_product" (fun n -> n >= 1) (fun n -> n >= 1)
          "two readers keep x's pool"
      ceCppAllocCase "compute_elision_write_between_keeps_pool" ceWriteSrc "snapshot" (fun n -> n >= 2) (fun n -> n >= 2)
          "a write between keeps x's pool (and w's)"
      ceCppAllocCase "compute_elision_keyed_read_keeps_pool" ceKeyedSrc "second" (fun n -> n >= 1) (fun n -> n >= 1)
          "a keyed read keeps x's pool"
      ceCppAllocCase "compute_elision_returned_keeps_pool" ceReturnedSrc "tripled" (fun n -> n >= 1) (fun n -> n >= 1)
          "a returned x keeps its pool"
      decisionCase "decision_compute_elision_fold_applied" ceFoldSrc "compute-elision"
          (fun d -> ceSubject "total.x" d && applied d && d.Evidence |> List.exists (fun e -> e.Contains "licensed route: base seed +0.0"))
          "applied to total.x with the licensed route's base seed +0.0 (W4-3)"
      withGate "BLADE_FP_REPRO" "1" (fun () ->
          decisionCase "decision_compute_elision_fold_applied_repro" ceFoldSrc "compute-elision"
              (fun d -> ceSubject "total.x" d && applied d && d.Evidence |> List.exists (fun e -> e.Contains "strict route: identity seed -0.0"))
              "applied to total.x with the strict route's identity seed -0.0 (W4-3)")
      decisionCase "decision_compute_elision_map_applied" ceMapSrc "compute-elision"
          (fun d -> ceSubject "scale_add.x" d && applied d && d.Evidence |> List.exists (fun e -> e.Contains "single consumer pointwise map"))
          "applied to scale_add.x (pointwise map consumer, fused)"
      decisionCase "decision_compute_elision_declined_two_readers" ceTwoSrc "compute-elision"
          (fun d -> ceSubject "sum_and_product.x" d && declinedMentioning "more than one reader" d) "declined: more than one reader"
      decisionCase "decision_compute_elision_declined_write_between" ceWriteSrc "compute-elision"
          (fun d -> ceSubject "snapshot.x" d && declinedMentioning "may write between the compute and its consumer" d) "declined: a write between"
      decisionCase "decision_compute_elision_declined_keyed_read" ceKeyedSrc "compute-elision"
          (fun d -> ceSubject "second.x" d && declinedMentioning "keyed subscript" d) "declined: a keyed read"
      decisionCase "decision_compute_elision_declined_returned" ceReturnedSrc "compute-elision"
          (fun d -> ceSubject "tripled.x" d && declinedMentioning "is returned" d) "declined: x is returned"
      decisionCase "decision_compute_elision_declined_empty_unseeded" ceEmptySrc "compute-elision"
          (fun d -> ceSubject "total.x" d && declinedMentioning "an unseeded fold over an extent not known at compile time to be >= 1" d)
          "declined: an unseeded fold over an extent that may be empty (BL8003 stays at the fold)"
      decisionCase "decision_compute_elision_declined_seed_reader" ceSeedReaderSrc "compute-elision"
          (fun d -> ceSubject "seeded_by_x.x" d && declinedMentioning "read again by the fold's kernel or seed" d) "declined: the seed reads x"
      decisionCase "decision_compute_elision_declined_extent_reader" ceExtentReaderSrc "compute-elision"
          (fun d -> ceSubject "extent_of_x.x" d && declinedMentioning "extent expression a type carries" d) "declined: a type's extent reads x"
      decisionCase "decision_compute_elision_declined_failing_extent" ceFailingExtentSrc "compute-elision"
          (fun d -> ceSubject "f.x" d && declinedMentioning "an extent of the producer is not a literal" d) "declined: the producer's range bound may fail"
      decisionCase "decision_compute_elision_declined_extent_write" ceExtentWriteSrc "compute-elision"
          (fun d -> ceSubject "f.x" d && declinedMentioning "may write between the compute and its consumer" d) "declined: a write hidden in a range bound"
      withGate "BLADE_COMPUTE_ELIDE" "0" (fun () ->
          decisionCase "decision_compute_elision_declined_gate" ceFoldSrc "compute-elision"
              (fun d -> ceSubject "total.x" d && declinedMentioning "disabled by BLADE_COMPUTE_ELIDE" d)
              "declined: disabled by BLADE_COMPUTE_ELIDE") ]

// ---------------------------------------------------------------------------
// Call elision (BLADE_CALL_ELIDE, Optimize.elideCallsModule; the P4 entry of
// docs/plans/plan-identity-transpose-compute.md). The VALUES are corpus
// functions/554-563; these read the emission: a jointly lowered call is not
// called at its site (so its return pool is never allocated there) in either
// lane, a declined call keeps its call, and every outcome is a `call-elision`
// record.
// ---------------------------------------------------------------------------

let private clPrep = "function prep(a: T^1) -> T^1 = { method_for(a) <@> lambda(v) -> v * 2.0 + 1.0 |> compute }\n"

let private clTopFoldSrc =
    clPrep
    + "let A = 0.5 * Float64(0..1001)\n"
    + "let s = reduce(prep(A), (+))\n"

let private clBodyFoldSrc =
    clPrep
    + "function total(b: T^1) -> T^0 = { reduce(prep(b), (+)) }\n"
    + "let A = [1.0, 2.0, 3.0]\n"
    + "let t = total(A)\n"

let private clMapSrc =
    clPrep
    + "let A = [1.0, 2.0, 3.0]\n"
    + "let y = method_for(prep(A)) <@> lambda(v) -> v * 10.0 |> compute\n"

let private clSpineSrc =
    "function scaled(a: T^1, c: Float64) -> T^1 = {\n"
    + "    let k = c * 2.0\n"
    + "    method_for(a) <@> lambda(v) -> v * k |> compute\n"
    + "}\n"
    + "function both(b: T^1, c: Float64) -> T^0 = {\n"
    + "    let s1 = reduce(scaled(b, c), (+))\n"
    + "    let s2 = reduce(scaled(b, 1.0), (+), 0.5)\n"
    + "    s1 + s2\n"
    + "}\n"
    + "let A = [1.0, 2.0, 3.0]\n"
    + "let t = both(A, 2.0)\n"

let private clRecursiveSrc =
    "function deep(a: Array<Float64 like Idx<3>>, n: Int64) -> Array<Float64 like Idx<3>> = {\n"
    + "    let k = if n == 0 then 0.0 else reduce(deep(a, n - 1), (+))\n"
    + "    method_for(a) <@> lambda(v) -> v + k |> compute\n"
    + "}\n"
    + "let A = [1.0, 2.0, 3.0]\n"
    + "let s = reduce(deep(A, 2), (+))\n"

let private clMutSrc =
    "function prep(a: mut Array<Float64 like Idx<3>>) -> Array<Float64 like Idx<3>> = {\n"
    + "    a(0) = 10.0\n"
    + "    method_for(a) <@> lambda(v) -> v * 2.0 |> compute\n"
    + "}\n"
    + "let mut M = [1.0, 2.0, 3.0]\n"
    + "let s = reduce(prep(M), (+))\n"

let private clOmpSrc =
    "function kpar(xs: Float64^1) -> Float64^1 = {\n"
    + "    method_for(xs) <@> lambda(x) where omp(x: 1) -> x + 1.0 |> compute\n"
    + "}\n"
    + "let A = [1.0, 2.0, 3.0, 4.0]\n"
    + "let t = reduce(kpar(A), (+))\n"

let private clTwoCallsSrc =
    clPrep
    + "let A = [1.0, 2.0, 3.0]\n"
    + "let B = [10.0, 20.0, 30.0]\n"
    + "let y = prep(A) + prep(B)\n"

let private clWriteSrc =
    clPrep
    + "let mut M = [1.0, 2.0, 3.0]\n"
    + "let s = reduce(prep(M), lambda(acc, v) -> {\n"
    + "    M(2) = 100.0\n"
    + "    acc + v\n"
    + "})\n"

let private clEmptySrc =
    clPrep
    + "let none: Array<Float64 like Idx<0>> = []\n"
    + "let t = reduce(prep(none), (+))\n"

let private clValueRefSrc =
    "function inc(a: T^1) -> T^1 = { method_for(a) <@> lambda(v) -> v + 1.0 |> compute }\n"
    + "function apply_to(f: T^1 -> T^1, a: T^1) -> T^1 = { f(a) }\n"
    + "let A = [1.0, 2.0, 3.0]\n"
    + "let viaValue = reduce(apply_to(inc, A), (+))\n"
    + "let direct = reduce(inc(A), (+))\n"

let private clOtherSitesSrc =
    clPrep
    + "let A = [1.0, 2.0, 3.0]\n"
    + "let s = reduce(prep(A), (+))\n"
    + "let r = prep(A)\n"

let private clCapturingLetSrc =
    "function prep(a: T^1, c: Float64) -> T^1 = {\n"
    + "    let k = (lambda(x: Float64) -> c + x)(1.0)\n"
    + "    method_for(a) <@> lambda(v) -> v * k |> compute\n"
    + "}\n"
    + "let A = [1.0, 2.0, 3.0]\n"
    + "let s = reduce(prep(A, 2.0), (+))\n"

let private clCalls (fnPrefix: string) (body: string) =
    System.Text.RegularExpressions.Regex.Matches(body, @"\b" + fnPrefix + @"\w*\(").Count

/// Calls of `callee` (and allocations) in the C++ body of `fn`, gate on and off.
let private clCppCase (name: string) (src: string) (fn: string) (callee: string)
                      (wantOn: int * int -> bool) (wantOff: int * int -> bool) (describe: string) =
    match cppOfSource name src, withGate "BLADE_CALL_ELIDE" "0" (fun () -> cppOfSource name src) with
    | Ok on, Ok off ->
        match ceCppBody on fn, ceCppBody off fn with
        | Some bOn, Some bOff ->
            let cOn, cOff = (clCalls callee bOn, ceAllocs bOn), (clCalls callee bOff, ceAllocs bOff)
            if wantOn cOn && wantOff cOff then
                resultLine Pass name ($"{fn}: (calls of {callee}, allocations) = {cOn} with the gate on, {cOff} off; {describe}")
                true
            else
                resultLine Fail name ($"{fn}: (calls of {callee}, allocations) = {cOn} on, {cOff} off; wanted {describe}")
                false
        | _ -> resultLine Fail name ($"no C++ body for `{fn}`"); false
    | Error e, _ | _, Error e -> resultLine Fail name e; false

/// The LLVM lane: calls of `@bl_<callee>` and pool allocations in `fn`'s body.
let private clLlvmCase (name: string) (src: string) (fn: string) (callee: string) =
    match ceLlvmOf name src, withGate "BLADE_CALL_ELIDE" "0" (fun () -> ceLlvmOf name src) with
    | Ok on, Ok off ->
        match ceLlvmBody on fn, ceLlvmBody off fn with
        | Some bOn, Some bOff ->
            let calls (b: string) = System.Text.RegularExpressions.Regex.Matches(b, @"call [^\n]*@bl_" + callee).Count
            let allocs (b: string) = System.Text.RegularExpressions.Regex.Matches(b, @"blade_alloc_cells").Count
            if calls bOn = 0 && allocs bOn = 0 && calls bOff >= 1 then
                resultLine Pass name ($"llvm lane, {fn}: 0 calls of @bl_{callee} and 0 pool allocations with the gate on; {calls bOff} call(s) off")
                true
            else
                resultLine Fail name ($"llvm lane, {fn}: {calls bOn} call(s) / {allocs bOn} allocation(s) on, {calls bOff} call(s) off; wanted 0 / 0 on, >= 1 off")
                false
        | _ -> resultLine Fail name ($"no LLVM body for `{fn}`"); false
    | Error e, _ | _, Error e -> resultLine Fail name ($"the llvm lane refused: {e}"); false

let private clSubject (prefix: string) (d: Blade.Effects.Decision) = d.Subject.StartsWith prefix

let private callElisionCases () : bool list =
    [ clCppCase "call_elision_top_fold_no_call" clTopFoldSrc "main" "prep"
          (fun (c, _) -> c = 0) (fun (c, _) -> c = 1)
          "the fold reads prep's map in place (no call, so no return pool); the gate restores the one call"
      clCppCase "call_elision_body_fold_no_call_no_pool" clBodyFoldSrc "total" "prep"
          (fun (c, a) -> c = 0 && a = 0) (fun (c, _) -> c = 1)
          "total neither calls prep nor allocates"
      clLlvmCase "call_elision_llvm_body_fold_no_call" clBodyFoldSrc "total" "prep"
      clCppCase "call_elision_map_no_call" clMapSrc "main" "prep"
          (fun (c, _) -> c = 0) (fun (c, _) -> c = 1)
          "the map consumer splices prep's map (no call)"
      clCppCase "call_elision_spine_no_call_no_pool" clSpineSrc "both" "scaled"
          (fun (c, a) -> c = 0 && a = 0) (fun (c, _) -> c = 2)
          "both elided calls inline their scalar spine: no call, no allocation"
      clCppCase "call_elision_recursive_keeps_call" clRecursiveSrc "main" "deep"
          (fun (c, _) -> c = 1) (fun (c, _) -> c = 1) "the recursive call stays (once, at the fold)"
      clCppCase "call_elision_two_calls_keep_calls" clTwoCallsSrc "main" "prep"
          (fun (c, _) -> c >= 2) (fun (c, _) -> c >= 2) "both calls stay"
      clCppCase "call_elision_write_keeps_call" clWriteSrc "main" "prep"
          (fun (c, _) -> c = 1) (fun (c, _) -> c = 1) "the call stays, evaluated once before the writing fold"
      clCppCase "call_elision_other_site_keeps_abi" clOtherSitesSrc "main" "prep"
          (fun (c, _) -> c = 1) (fun (c, _) -> c = 2) "the fold's call is elided; `let r = prep(A)` keeps its call"
      decisionCase "decision_call_elision_top_fold_applied" clTopFoldSrc "call-elision"
          (fun d -> clSubject "s: reduce(prep(A)) -> one fused fold" d && applied d
                    && d.Evidence |> List.exists (fun e -> e.Contains "licensed route: base seed +0.0"))
          "applied to s: reduce(prep(A)) with the licensed route's base seed +0.0 (W4-3)"
      withGate "BLADE_FP_REPRO" "1" (fun () ->
          decisionCase "decision_call_elision_top_fold_applied_repro" clTopFoldSrc "call-elision"
              (fun d -> clSubject "s: reduce(prep(A))" d && applied d
                        && d.Evidence |> List.exists (fun e -> e.Contains "strict route: identity seed -0.0"))
              "applied with the strict route's identity seed -0.0 (W4-3)")
      decisionCase "decision_call_elision_body_fold_applied" clBodyFoldSrc "call-elision"
          (fun d -> clSubject "total: reduce(prep(b))" d && applied d) "applied to total: reduce(prep(b))"
      decisionCase "decision_call_elision_map_applied" clMapSrc "call-elision"
          (fun d -> clSubject "y: a pointwise map over prep(A)" d && applied d
                    && d.Evidence |> List.exists (fun e -> e.Contains "realized by elementwise fusion"))
          "applied to y (pointwise consumer, fused)"
      decisionCase "decision_call_elision_spine_clone" clSpineSrc "call-elision"
          (fun d -> clSubject "both: reduce(scaled(b, c))" d && applied d
                    && d.Evidence |> List.exists (fun e -> e.Contains "a clone `__lambda_"))
          "applied with the capturing kernel cloned"
      decisionCase "decision_call_elision_declined_recursive" clRecursiveSrc "call-elision"
          (fun d -> clSubject "s: reduce(deep(A, 2))" d && declinedMentioning "a recursive callee" d) "declined: recursive"
      decisionCase "decision_call_elision_declined_mut_writer" clMutSrc "call-elision"
          (fun d -> clSubject "s: reduce(prep(M))" d && declinedMentioning "the callee may write" d) "declined: the callee writes its mut parameter"
      decisionCase "decision_call_elision_declined_omp" clOmpSrc "call-elision"
          (fun d -> clSubject "t: reduce(kpar(A))" d && declinedMentioning "parallel strategy" d) "declined: an omp map kernel"
      decisionCase "decision_call_elision_declined_two_calls" clTwoCallsSrc "call-elision"
          (fun d -> clSubject "y: a pointwise map over prep(A)" d && declinedMentioning "several calls in one statement" d)
          "declined: several calls in one statement"
      decisionCase "decision_call_elision_declined_write_between" clWriteSrc "call-elision"
          (fun d -> clSubject "s: reduce(prep(M))" d && declinedMentioning "the consumer statement may write" d)
          "declined: the fold kernel writes the argument"
      decisionCase "decision_call_elision_declined_empty_unseeded" clEmptySrc "call-elision"
          (fun d -> clSubject "t: reduce(prep(none))" d && declinedMentioning "an unseeded fold over an extent not known at compile time to be >= 1" d)
          "declined: an unseeded fold over an extent that may be empty (BL8003 stays at the fold)"
      decisionCase "decision_call_elision_declined_value_reference" clValueRefSrc "call-elision"
          (fun d -> clSubject "direct: reduce(inc(A))" d && declinedMentioning "is referenced as a value" d)
          "declined: the callee is referenced as a value"
      decisionCase "decision_call_elision_declined_capturing_let" clCapturingLetSrc "call-elision"
          (fun d -> clSubject "s: reduce(prep(A, 2.0))" d && declinedMentioning "capturing callable outside its map kernel" d)
          "declined: a scalar let applies a capturing lambda"
      // a KEPT fold call is evaluated once (bound before its statement), gate on and off
      clCppCase "call_elision_kept_call_evaluated_once" clWriteSrc "main" "prep"
          (fun (c, _) -> c = 1) (fun (c, _) -> c = 1) "one call of prep, not one per cell"
      withGate "BLADE_CALL_ELIDE" "0" (fun () ->
          decisionCase "decision_call_elision_declined_gate" clTopFoldSrc "call-elision"
              (fun d -> clSubject "s: reduce(prep(A))" d && declinedMentioning "disabled by BLADE_CALL_ELIDE" d)
              "declined: disabled by BLADE_CALL_ELIDE") ]

// ---------------------------------------------------------------------------
// TRANSPOSE NORMAL FORM (P2 entry of docs/plans/plan-identity-transpose-
// compute.md; Blade.Optimize.normalizeTransposesModule and planAliasElision,
// rule `transpose-normal-form v1`, gate BLADE_TRANSPOSE_ELIDE). The corpus
// (index-types/545-555) proves the values; only these pins can tell a copy
// from none: the double transpose emits no transpose copy and no pool copy,
// the read-only symmetric alias no pool copy, and a written source or alias
// keeps its copy.
// ---------------------------------------------------------------------------

let private tnfDoubleSrc =
    "let M: Array<Float64 like Idx<2>, Idx<3>> = [[1.0, 2.0, 3.0], [4.0, 5.0, 6.0]]\n"
    + "let T2 = transpose(transpose(M, [0, 1]), [0, 1])\n"
    + "let s = reduce(transpose(transpose(M, [0, 1]), [0, 1]), (+), axes = 2)\n"

let private tnfDoubleWrittenSrc =
    "let M: Array<Float64 like Idx<2>, Idx<3>> = [[1.0, 2.0, 3.0], [4.0, 5.0, 6.0]]\n"
    + "let T2 = transpose(transpose(M, [0, 1]), [0, 1])\n"
    + "M(0, 0) = 100.0\n"

let private tnfRank3Src =
    "let A: Array<Float64 like Idx<2>, Idx<3>, Idx<2>> = method_for(range<Idx<2>, Idx<3>, Idx<2>>) <@> lambda(i, j, k) -> Float64(100 * i + 10 * j + k) |> compute\n"
    + "let R = transpose(transpose(transpose(A, [0, 1]), [1, 2]), [0, 1])\n"
    + "let R2 = transpose(transpose(A, [0, 1]), [1, 2])\n"

let private tnfSymPrefix =
    "let A = [1.0, 2.0, 3.0]\n"
    + "let L = method_for(A, A)\n"
    + "let g = lambda(x, y) where comm(x, y) -> 2.0 * x + y\n"
    + "let S = L <@> reynolds(g) |> compute\n"
    + "let ST = transpose(S, [0, 1])\n"
let private tnfSymSrc = tnfSymPrefix + "let tot = reduce(ST, (+), axes = 2)\n"
let private tnfSymSourceWrittenSrc = tnfSymPrefix + "S(0, 1) = 100.0\n"
let private tnfSymAliasWrittenSrc = tnfSymPrefix + "ST(0, 1) = 100.0\n"

let private tnfAntisymSrc =
    "let A = [1.0, 2.0, 3.0]\n"
    + "let L = method_for(A, A)\n"
    + "let g = lambda(x, y) where comm(x, y) -> 2.0 * x + y\n"
    + "let anti = L <@> reynolds(g, Antisymmetric) |> compute\n"
    + "let result = transpose(anti, [0, 1])\n"

// Codex review of the pass (findings 1-2): a write to storage that is local
// only BECAUSE of the copy being elided is still a write.
let private tnfBlockMutParamSrc =
    "function poke(a: mut Array<Float64 like Idx<2>>) -> Float64 = {\n"
    + "    a((0 : Idx<2>)) = 99.0\n"
    + "    a((0 : Idx<2>))\n"
    + "}\n"
    + "let M: Array<Float64 like Idx<2>> = [1.0, 2.0]\n"
    + "let result = {\n"
    + "    let mut X = M\n"
    + "    poke(X)\n"
    + "}\n"
let private tnfNestedViewWriteSrc =
    "let M: Array<Float64 like Idx<2>, Idx<3>> = [[1.0, 2.0, 3.0], [4.0, 5.0, 6.0]]\n"
    + "let result = {\n"
    + "    let r = transpose(transpose(M, [0, 1]), [0, 1])(0)\n"
    + "    r(1) = 99.0\n"
    + "    r(1)\n"
    + "}\n"
let private tnfCount (needle: string) (s: string) =
    s.Split([| needle |], System.StringSplitOptions.None).Length - 1

let private tnfEmission (name: string) (src: string) (judge: string -> Result<string, string>) =
    match cppOfSource name src with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        match judge cpp with
        | Ok detail -> resultLine Pass name detail; true
        | Error detail -> resultLine Fail name detail; false

let private tnfLlvmAllocs (src: string) : Result<int, string> =
    match lower src with
    | Error e -> Error ($"lower: {e}")
    | Ok ir ->
        match EmitLlvm.tryEmitProgramNamed "tnf" ir with
        | Ok ll -> Ok (System.Text.RegularExpressions.Regex.Matches(ll, "call ptr @blade_alloc").Count)
        | Error why -> Error ($"the llvm lane refused: {why}")

let private transposeNormalFormCases () =
    let hardCopies (cpp: string) = tnfCount "// transpose: hard" cpp
    let poolCopies (cpp: string) = tnfCount "std::copy_n(pool_base(" cpp
    [ tnfEmission "transpose_double_identity_no_copy" tnfDoubleSrc (fun cpp ->
          let h, p, alias = hardCopies cpp, poolCopies cpp, cpp.Contains "Array<double, 2> T2 = M;"
          if h = 0 && p = 0 && alias then Ok "no transpose copy, no pool copy: T2 (and the fold's operand) read M's storage"
          else Error ($"hard={h} poolCopies={p} T2-alias={alias}"))
      withGate "BLADE_TRANSPOSE_ELIDE" "0" (fun () ->
          tnfEmission "transpose_double_identity_gate_off_copies" tnfDoubleSrc (fun cpp ->
              let h = hardCopies cpp
              if h = 4 then Ok "gate off: today's four transpose copies"
              else Error ($"expected 4 transpose copies, got {h}")))
      tnfEmission "transpose_double_written_keeps_bind_copy" tnfDoubleWrittenSrc (fun cpp ->
          let h, keep = hardCopies cpp, cpp.Contains "std::copy_n(pool_base(M.data), T2_n, pool_base(T2.data));"
          if h = 0 && keep then Ok "no transpose copy; M is written, so T2 keeps its copy-on-bind copy"
          else Error ($"hard={h} T2-copy={keep}"))
      tnfEmission "transpose_rank3_chain_one_swap" tnfRank3Src (fun cpp ->
          let h = hardCopies cpp
          if h = 3 then Ok "R's three swaps are one; R2's 3-cycle keeps its two: 3 transpose copies"
          else Error ($"expected 3 transpose copies (1 + 2), got {h}"))
      tnfEmission "transpose_symmetric_alias_no_pool_copy" tnfSymSrc (fun cpp ->
          let copy, alias = cpp.Contains "std::copy_n(pool_base(S.data)", cpp.Contains "Array<double, 2> ST = S;"
          if not copy && alias then Ok "ST shares S's packed pool: no allocation, no copy_n"
          else Error ($"poolCopy={copy} alias={alias}"))
      tnfEmission "transpose_symmetric_source_written_keeps_copy" tnfSymSourceWrittenSrc (fun cpp ->
          if cpp.Contains "std::copy_n(pool_base(S.data), ST_n, pool_base(ST.data));" then Ok "S is written after the alias: ST keeps its pool copy"
          else Error "the pool copy is missing")
      tnfEmission "transpose_symmetric_alias_written_keeps_copy" tnfSymAliasWrittenSrc (fun cpp ->
          if cpp.Contains "std::copy_n(pool_base(S.data), ST_n, pool_base(ST.data));" then Ok "ST is written: it keeps its pool copy"
          else Error "the pool copy is missing")
      tnfEmission "transpose_antisym_negate_kept" tnfAntisymSrc (fun cpp ->
          if cpp.Contains "negate_pool(" then Ok "the antisymmetric transpose still negates into a fresh pool"
          else Error "negate_pool is missing")
      (let name = "transpose_llvm_lane_allocations"
       match tnfLlvmAllocs tnfDoubleSrc, withGate "BLADE_TRANSPOSE_ELIDE" "0" (fun () -> tnfLlvmAllocs tnfDoubleSrc),
             tnfLlvmAllocs tnfSymSrc, withGate "BLADE_TRANSPOSE_ELIDE" "0" (fun () -> tnfLlvmAllocs tnfSymSrc) with
       | Ok d, Ok dOff, Ok s, Ok sOff when d = 1 && dOff > d && s = sOff - 1 ->
           resultLine Pass name ($"llvm lane: double transpose {dOff} -> {d} pool (M's), symmetric alias {sOff} -> {s} pools")
           true
       | d, dOff, s, sOff ->
           resultLine Fail name (sprintf "double on/off = %A/%A, symmetric on/off = %A/%A" d dOff s sOff)
           false)
      decisionCase "decision_transpose_chain_identity_applied" tnfDoubleSrc "transpose-normal-form"
          (fun d -> d.Subject = "T2" && applied d && d.Evidence |> List.exists (fun e -> e.Contains "IDENTITY permutation"))
          "applied: T2's chain is the identity"
      decisionCase "decision_transpose_chain_one_swap_applied" tnfRank3Src "transpose-normal-form"
          (fun d -> d.Subject = "R" && applied d && d.Evidence |> List.exists (fun e -> e.Contains "1 swap(s) [0, 2]"))
          "applied: R's chain is the swap [0, 2]"
      decisionCase "decision_transpose_chain_declined_already_normal" tnfRank3Src "transpose-normal-form"
          (fun d -> d.Subject = "R2" && declinedMentioning "already normal" d) "declined: R2's 3-cycle needs its two swaps"
      decisionCase "decision_transpose_alias_applied" tnfSymSrc "transpose-normal-form"
          (fun d -> d.Subject = "ST" && applied d && d.Evidence |> List.exists (fun e -> e.Contains "read-only alias"))
          "applied: ST is a read-only alias of S"
      decisionCase "decision_transpose_alias_declined_source_assigned" tnfSymSourceWrittenSrc "transpose-normal-form"
          (fun d -> d.Subject = "ST" && declinedMentioning "`S` is assigned" d) "declined: S is assigned"
      decisionCase "decision_transpose_alias_declined_alias_assigned" tnfSymAliasWrittenSrc "transpose-normal-form"
          (fun d -> d.Subject = "ST" && declinedMentioning "`ST` is assigned" d) "declined: ST is assigned"
      decisionCase "decision_transpose_antisym_declined" tnfAntisymSrc "transpose-normal-form"
          (fun d -> d.Subject = "result" && declinedMentioning "negation" d) "declined: the antisymmetric negation is kept"
      tnfEmission "transpose_block_mut_param_keeps_copy" tnfBlockMutParamSrc (fun cpp ->
          if cpp.Contains "std::copy_n(pool_base(M.data)" then Ok "the block copy of M passed to a `mut` parameter is kept"
          else Error "the copy of M is missing")
      decisionCase "decision_transpose_alias_declined_block_mut_param" tnfBlockMutParamSrc "transpose-normal-form"
          (fun d -> d.Subject = "result" && declinedMentioning "may write" d) "declined: the block passes its copy to a writing callee"
      tnfEmission "transpose_nested_view_write_keeps_chain" tnfNestedViewWriteSrc (fun cpp ->
          let h = tnfCount "// transpose: hard" cpp
          if h = 2 then Ok "the written row view keeps both transpose copies" else Error ($"expected 2 transpose copies, got {h}"))
      decisionCase "decision_transpose_chain_declined_view_write" tnfNestedViewWriteSrc "transpose-normal-form"
          (fun d -> d.Subject = "result" && declinedMentioning "may write" d) "declined: a view of the result is written"
      (match (try Ok (System.IO.File.ReadAllText "tests/corpus/index-types/555_transpose_named_pipeline_writing_kernel.blade") with ex -> Error ex.Message) with
       | Error e -> resultLine Fail "decision_transpose_named_pipeline_declined" e; false
       | Ok src ->
           decisionCase "decision_transpose_named_pipeline_declined" src "transpose-normal-form"
               (fun d -> d.Subject = "X" && declinedMentioning "may write" d) "declined: a named composition's writing kernel is applied to X")
      withGate "BLADE_TRANSPOSE_ELIDE" "0" (fun () ->
          decisionCase "decision_transpose_declined_gate" tnfSymSrc "transpose-normal-form"
              (declinedMentioning "disabled by BLADE_TRANSPOSE_ELIDE") "declined: disabled by BLADE_TRANSPOSE_ELIDE") ]

// ---------------------------------------------------------------------------
// Hash equijoin (round 3 W3-2, item T2; Blade.Optimize.planEquiJoin, rule
// `hash-join v1`). Values are pinned by corpus sql-joins/001-019 (and the
// interpreter differential over them); these pin the admission record (cases
// for the five-party rows and a SAMPLE of the refusals -- not every refusal),
// the emission (the CSR build, the overflow guards, the counting pass, no
// all-pairs pool), the gate-off form (the six passes), and -- per corpus file
// -- the route each sql-joins file takes (hashJoinCorpusRoutes).
// ---------------------------------------------------------------------------

let private hjHead =
    "type LeftRow = Idx<3>\ntype RightRow = Idx<4>\n"
    + "let left_key: Array<Int64 like LeftRow> = [1, 2, 9]\n"
    + "let left_amount: Array<Int64 like LeftRow> = [2, 3, 7]\n"
    + "let right_key: Array<Int64 like RightRow> = [1, 2, 1, 2]\n"
    + "let right_amount: Array<Int64 like RightRow> = [10, 10, 20, 10]\n"
let private hjJoin (pk: string) (eq: string) (pred: string) =
    $"let matched = compound(method_for(left_amount, right_amount) <@> {pk}, mask(method_for(left_key, right_key) <@> {eq}, {pred}))\n"
let private hjTail = "let match_count = extents(matched)\nlet total = reduce(matched, (+), 0)\n"
let private hjSrc = hjHead + hjJoin "lambda(l, r) -> l * r" "lambda(l, r) -> l == r" "lambda(e) -> e" + hjTail
let private hjPositionalSrc =
    "type KIdx = Idx<5>\ntype LeftRow = Idx<5>\ntype RightRow = Idx<4>\n"
    + "let left_key: Array<KIdx like LeftRow> = [0, 4, 2, 1, 0]\n"
    + "let left_amount: Array<Int64 like LeftRow> = [1, 2, 3, 4, 5]\n"
    + "let right_key: Array<KIdx like RightRow> = [0, 4, 1, 0]\n"
    + "let right_amount: Array<Int64 like RightRow> = [10, 20, 30, 40]\n"
    + hjJoin "lambda(l, r) -> l * r" "lambda(l, r) -> l == r" "lambda(e) -> e" + hjTail
let private hjRule = "hash-join"
let private hjApplied (d: Blade.Effects.Decision) = applied d && d.Subject = "matched"
let private hjDeclined (needle: string) (d: Blade.Effects.Decision) = d.Subject = "matched" && declinedMentioning needle d

let private hashJoinDecisionCases () =
    [ decisionCase "decision_hash_join_applied" hjSrc hjRule
          (fun d -> hjApplied d
                    && evidenceHas "stable CSR over `right_key`" d
                    && evidenceHas "bucket rows filled FORWARD (every bucket lists its right rows j ascending)" d
                    && evidenceHas "left-major / right-minor = the compound's lex order" d
                    && evidenceHas "a COUNTING pass sizes `matched` before any store" d
                    && evidenceHas "cardinality-only compound_index_t<2>" d)
          "applied: stable CSR, left-major / right-minor probe, counting pass, cardinality-only index"
      decisionCase "decision_hash_join_five_party_matrix" hjSrc hjRule
          (fun d -> hjApplied d
                    && evidenceHas "equiJoinAdmits (five parties, each write query through Optimize.scheduleMayWriteIn): producers `left_key`, `right_key`, `left_amount`, `right_amount`: proved stored at their own sites (storedArrayProof" d
                    && evidenceHas "producer kernels (`==`, the identity predicate, pk): pure, total scalars" d
                    && evidenceHas "consumer `matched`: its whole payload (value and both compound operands) writes nothing" d
                    && evidenceHas "outer fold kernel(s): 1 over `matched` (`total`), none writes, none moved" d
                    && evidenceHas "seed(s): 1 seeded fold(s), none writes, none moved" d
                    && evidenceHas "intervening: none -- the CSR build, the counting pass and the probe all run at `matched`'s own site" d
                    && evidenceHas "parallel: none -- every pass is serial" d)
          "the five-party matrix recorded, every row"
      decisionCase "decision_hash_join_census_uses" hjSrc hjRule
          (fun d -> hjApplied d && evidenceHas "uses of `matched` (census: every one reads the compact buffer or the cardinality alone): `extents` (the cardinality), folds over the compact buffer (pure total kernels), printing" d)
          "the demand census lists the certified uses"
      decisionCase "decision_hash_join_positional_counting_sort" hjPositionalSrc hjRule
          (fun d -> hjApplied d && evidenceHas "right keys `right_key` positional (`Idx<5>`): a COUNTING SORT" d)
          "a positional right key takes the counting sort"
      decisionCase "decision_hash_join_extreme_keys_span_bigint"
          (hjSrc.Replace("let right_key: Array<Int64 like RightRow> = [1, 2, 1, 2]", "let right_key: Array<Int64 like RightRow> = [-9223372036854775807 - 1, 2, 9223372036854775807, 2]")) hjRule
          (fun d -> hjApplied d && evidenceHas "form at run time: hash (span 18446744073709551616, M 4" d)
          "the inclusive span of [INT64_MIN, INT64_MAX] prints as 2^64 (a bigint), never a wrapped 0"
      decisionCase "decision_hash_join_dynamic_chooser" hjSrc hjRule
          (fun d -> hjApplied d && evidenceHas "right keys `right_key` dynamic Int64: a COUNTING SORT when their span is within max(4 M, 4096) cells (and 2^26), else an open-addressing key -> bucket hash" d)
          "dynamic keys: the run-time chooser is stated"
      withGate "BLADE_HASH_JOIN" "0" (fun () ->
          decisionCase "decision_hash_join_disabled" hjSrc hjRule
              (fun d -> hjDeclined "disabled by BLADE_HASH_JOIN" d && evidenceHas "equiJoinAdmits (five parties" d)
              "declined: disabled by BLADE_HASH_JOIN, the matrix still recorded")
      // producers
      decisionCase "decision_hash_join_declined_assigned_source"
          (hjSrc.Replace("let right_amount: Array<Int64 like RightRow>", "let mut right_amount: Array<Int64 like RightRow>") + "right_amount(0) = 5\n") hjRule
          (hjDeclined "the source `right_amount` (an alias chain of `right_amount`) is assigned in the program") "declined: a source is assigned"
      decisionCase "decision_hash_join_declined_unstored_source"
          (hjSrc.Replace("let right_amount: Array<Int64 like RightRow> = [10, 10, 20, 10]", "let right_amount = method_for(range<RightRow>) <@> lambda(j) -> Int64(j) * 10")) hjRule
          (hjDeclined "the source `right_amount` is not proved stored") "declined: a deferred source is not proved stored"
      decisionCase "decision_hash_join_declined_writing_producer"
          ("type One = Idx<1>\n" + hjHead.Replace("let right_amount: Array<Int64 like RightRow> = [10, 10, 20, 10]\n", "")
           + "let mut log: Array<Int64 like One> = [0]\n"
           + "function make(l: mut Array<Int64 like One>) -> Array<Int64 like RightRow> = {\n    l(0) = l(0) + 1\n    [10, 10, 20, 10]\n}\n"
           + "let right_amount = make(log)\n"
           + hjJoin "lambda(l, r) -> l * r" "lambda(l, r) -> l == r" "lambda(e) -> e" + hjTail) hjRule
          (hjDeclined "the producer `right_amount` may write") "declined: a producer's initializer writes (the shared write analysis)"
      // producer kernels
      decisionCase "decision_hash_join_declined_writing_payload"
          ("let mut calls = 0\n" + hjHead + "let matched = compound(method_for(left_amount, right_amount) <@> lambda(l, r) -> {\n    calls = calls + 1\n    l * r\n}, mask(method_for(left_key, right_key) <@> lambda(l, r) -> l == r, lambda(e) -> e))\n" + hjTail) hjRule
          (hjDeclined "the payload kernel pk may write") "declined: the payload kernel writes"
      decisionCase "decision_hash_join_declined_payload_not_total" (hjHead + hjJoin "lambda(l, r) -> l / r" "lambda(l, r) -> l == r" "lambda(e) -> e" + hjTail) hjRule
          (hjDeclined "the payload kernel is not a pure, total scalar") "declined: integer `/` in the payload kernel"
      decisionCase "decision_hash_join_declined_payload_capture" (hjHead + "let s = 3\n" + hjJoin "lambda(l, r) -> l * r + s" "lambda(l, r) -> l == r" "lambda(e) -> e" + hjTail) hjRule
          (hjDeclined "the payload kernel captures a value") "declined: the payload kernel captures"
      decisionCase "decision_hash_join_declined_predicate" (hjHead + hjJoin "lambda(l, r) -> l * r" "lambda(l, r) -> l == r" "lambda(e) -> !e" + hjTail) hjRule
          (hjDeclined "a non-identity mask predicate") "declined: a non-identity predicate (the anti-join spelling)"
      decisionCase "decision_hash_join_declined_conjunctive" (hjHead + hjJoin "lambda(l, r) -> l * r" "lambda(l, r) -> l == r && l == r" "lambda(e) -> e" + hjTail) hjRule
          (hjDeclined "a conjunctive key kernel") "declined: a conjunctive key kernel (v1.1)"
      decisionCase "decision_hash_join_declined_not_equality" (hjHead + hjJoin "lambda(l, r) -> l * r" "lambda(l, r) -> l < r" "lambda(e) -> e" + hjTail) hjRule
          (hjDeclined "the key kernel is not the equality") "declined: a band / inequality kernel"
      decisionCase "decision_hash_join_declined_float_keys"
          (hjSrc.Replace("let left_key: Array<Int64 like LeftRow> = [1, 2, 9]", "let left_key: Array<Float64 like LeftRow> = [1.0, 2.0, 9.0]")
                .Replace("let right_key: Array<Int64 like RightRow> = [1, 2, 1, 2]", "let right_key: Array<Float64 like RightRow> = [1.0, 2.0, 1.0, 2.0]")) hjRule
          (hjDeclined "float keys") "declined: float keys"
      // consumer census
      decisionCase "decision_hash_join_declined_keyed_read" (hjSrc + "let corner = matched(0, 0)\n") hjRule
          (hjDeclined "a keyed read of `matched` in `corner`") "declined: a keyed read needs the tuple table"
      decisionCase "decision_hash_join_declined_alias" (hjSrc + "let again = matched\n") hjRule
          (hjDeclined "the alias `again` of `matched`") "declined: an alias of the relation"
      decisionCase "decision_hash_join_declined_fold_not_total" (hjSrc + "let q = reduce(matched, lambda(a, b) -> a / (b + 1), 7)\n") hjRule
          (hjDeclined "whose kernel is not a resolved, capture-free, pure total scalar function") "declined: a fold kernel the census cannot certify"
      decisionCase "decision_hash_join_declined_map_over_relation" (hjSrc + "let twice = method_for(matched) <@> lambda(v) -> v * 2 |> compute\n") hjRule
          (hjDeclined "`matched` as an operand of IRApplyCombinator (a use the census does not certify) in `twice`")
          "declined: a map over the relation is not certified (v1)"
      // the named spelling
      decisionCase "decision_hash_join_declined_named_spelling"
          (hjHead + "let equal_keys = method_for(left_key, right_key) <@> lambda(l, r) -> l == r |> compute\nlet keep = mask(equal_keys, lambda(e) -> e)\nlet products = method_for(left_amount, right_amount) <@> lambda(l, r) -> l * r |> compute\nlet matched = compound(products, keep)\n" + hjTail) hjRule
          (hjDeclined "the R4 spelling") "declined: the named spelling keeps every named array stored" ]

let private hashJoinEmission () =
    let name = "hash_join_cpp_emission"
    match cppOfSource name hjSrc with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        let has (s: string) = cpp.Contains s
        let want =
            [ "#include \"blade_member.h\""
              "blade_join_build_i64(&matched__csr, matched__rk, (int64_t)matched__m, 0)"
              "const int64_t matched__b = blade_join_lookup_i64(&matched__csr, matched__lk[matched__i]);"
              // the overflow guards (review W3-2 finding 5): the dense
              // extents product (before the CSR build), then every bucket
              // length checked-added against the largest allocatable count,
              // BEFORE the relation's allocation and stores (a run-time
              // witness would need N x M past 2^63 / 8 cells: impractical,
              // so the guard is pinned here)
              "const size_t matched__cap = (size_t)PTRDIFF_MAX / sizeof(int64_t) - 1;"
              "if (matched__m != 0 && matched__n > SIZE_MAX / matched__m) throw std::length_error("
              "if (matched__len > matched__cap - matched__card) throw std::length_error("
              "matched__card += matched__len;"
              "int64_t* matched_compact = new int64_t[matched__card + 1];"
              "matched_compact[matched__q++] = __wrap_"
              "matched__ra[matched__csr.perm[matched__t]]"
              "compound_index_t<2>::cardinality_only" ]
        let absent = [ "matched__dense"; "matched__masksrc"; "matched_idx_maskvec"; "matched_densepool" ]
        let missing = want |> List.filter (has >> not)
        let present = absent |> List.filter has
        // the counting pass precedes the probe's first store
        // product check < CSR build < checked-add test < accumulation <
        // relation allocation < fill
        let ordered =
            let at (s: string) = cpp.IndexOf s
            let steps =
                [ at "matched__n > SIZE_MAX / matched__m"
                  at "blade_join_build_i64(&matched__csr"
                  at "if (matched__len > matched__cap - matched__card) throw std::length_error("
                  at "matched__card += matched__len;"
                  at "int64_t* matched_compact = new int64_t[matched__card + 1];"
                  at "matched_compact[matched__q++]" ]
            steps |> List.forall (fun i -> i >= 0) && (steps |> List.pairwise |> List.forall (fun (a, b) -> a < b))
        if missing.IsEmpty && present.IsEmpty && ordered then
            resultLine Pass name "in order: the N x M product check, the CSR build, the checked-add test, the accumulation, the relation's allocation, the probe's stores; the cardinality-only index; no N x M dense / Bool / mask pool, no bit vector"
            true
        else
            resultLine Fail name ($"missing: {missing}; unexpected: {present}; counting-before-probe: {ordered}")
            false

let private hashJoinGateOffEmission () =
    let name = "hash_join_cpp_gate_off_six_passes"
    match withGate "BLADE_HASH_JOIN" "0" (fun () -> cppOfSource name hjSrc) with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        let six = [ "matched__dense"; "matched__masksrc__arr"; "std::vector<bool> matched_idx_maskvec"; "new compound_index_t<2>(\"matched_idx\", matched_idx_extents, matched_idx_maskvec)"; "matched_densepool" ]
        let missing = six |> List.filter (cpp.Contains >> not)
        let csr = cpp.Contains "blade_join_build_i64(&"
        if missing.IsEmpty && not csr then
            resultLine Pass name "gate off: the as-written passes (dense payload pool, Bool pool, identity mask, bit vector, hashed index, scatter); no CSR"
            true
        else
            resultLine Fail name ($"missing: {missing}; CSR present: {csr}")
            false

let private hashJoinPositionalEmission () =
    let name = "hash_join_cpp_positional_forces_counting_sort"
    match cppOfSource name hjPositionalSrc with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        if cpp.Contains "blade_join_build_i64(&matched__csr, matched__rk, (int64_t)matched__m, 1)" then
            resultLine Pass name "a positional right key passes the positional flag (the counting sort over the measured span)"
            true
        else
            resultLine Fail name "the build does not pass the positional flag"
            false

/// THE PER-FILE CORPUS ROUTE CENSUS (review W3-2 finding 1): every
/// tests/corpus/sql-joins file's own route, not a synthetic fixture's -- the
/// value / error pins alone cannot tell the CSR from the six passes (opt-diff
/// treats identical emission as a pass). An ADMITTED file must record the
/// applied plan for each of its relations AND emit the CSR build and the
/// cardinality-only constructor (plus its route needle); a DECLINED file must
/// record its particular decline AND emit no CSR; with BLADE_HASH_JOIN=0 no
/// file emits a CSR. The census also fails when a file on disk is not in the
/// table (a new witness must state its route).
let private hashJoinCorpusRoutes () =
    let dir = "tests/corpus/sql-joins"
    // (complete filename, Ok [(subject, extra plan needle, extra C++ needle)] | Error [(subject, decline needle)])
    let admitted (subs: (string * string * string) list) = Ok subs
    let m = "matched"
    let dyn = "blade_join_build_i64(&matched__csr, matched__rk, (int64_t)matched__m, 0)"
    let table : (string * Result<(string * string * string) list, (string * string) list>) list =
        [ ("001_equijoin_bags_survive.blade", admitted [ (m, "form at run time: counting sort", dyn) ])
          ("002_equijoin_order_fingerprint_interleaved.blade", admitted [ (m, "form at run time: counting sort", dyn) ])
          ("003_equijoin_negative_keys_match.blade", admitted [ (m, "form at run time: counting sort (span 6, M 4", dyn) ])
          ("004_equijoin_no_matches_seeded.blade", admitted [ (m, "form at run time: counting sort", dyn) ])
          ("005_equijoin_no_matches_unseeded_aborts.blade", admitted [ (m, "form at run time: counting sort", dyn) ])
          ("006_equijoin_keyed_read_declines.blade", Error [ (m, "a keyed read of `matched` in `corner`") ])
          ("007_equijoin_writing_payload_declines.blade", Error [ (m, "the payload kernel pk may write") ])
          ("008_equijoin_dynamic_keys_hash.blade", admitted [ (m, "form at run time: hash (span 1000000013, M 6", dyn) ])
          ("009_equijoin_positional_keys.blade", admitted [ (m, "right keys `right_key` positional (`Idx<5>`)", "blade_join_build_i64(&matched__csr, matched__rk, (int64_t)matched__m, 1)") ])
          ("010_equijoin_duplicate_2x2_order.blade", admitted [ (m, "form at run time: counting sort (span 1, M 2", dyn) ])
          ("011_equijoin_out_of_domain_left_key.blade", admitted [ (m, "right keys `right_key` positional (`Idx<4>`)", "blade_join_build_i64(&matched__csr, matched__rk, (int64_t)matched__m, 1)") ])
          ("012_equijoin_all_equal_skew_and_tails.blade",
           admitted [ ("skew", "form at run time: counting sort (span 1, M 7", "blade_join_build_i64(&skew__csr, skew__rk, (int64_t)skew__m, 0)");
                      ("tails", "form at run time: counting sort (span 9, M 7", "blade_join_build_i64(&tails__csr, tails__rk, (int64_t)tails__m, 0)") ])
          ("013_equijoin_named_spelling_declines.blade", Error [ (m, "the dense operand is the named array `products`") ])
          ("014_equijoin_failing_payload_mod_declines.blade", Error [ (m, "the payload kernel is not a pure, total scalar") ])
          ("015_equijoin_failing_payload_negative_power_declines.blade", Error [ (m, "the payload kernel is not a pure, total scalar") ])
          ("016_equijoin_failing_payload_cast_declines.blade", Error [ (m, "the payload kernel is not a pure, total scalar") ])
          ("017_equijoin_failing_payload_call_declines.blade", Error [ (m, "the payload kernel is not a pure, total scalar") ])
          ("018_equijoin_failing_key_kernel_mod_declines.blade", Error [ (m, "the key kernel is not the equality `l == r` of its two parameters") ])
          ("019_equijoin_failing_payload_unresolved_call_declines.blade", Error [ (m, "the payload kernel pk may write") ]) ]
    if not (System.IO.Directory.Exists dir) then
        resultLine Fail "hash_join_corpus_route_census" $"missing {dir} (run from the repo root)"
        [ false ]
    else
    // Keyed by COMPLETE filenames; the disk set and the table set must be
    // EQUAL both ways (a file without a row fails, a row without a file
    // fails, a duplicated row fails).
    let files = System.IO.Directory.GetFiles(dir, "*.blade") |> Array.sort |> List.ofArray
    let diskSet = files |> List.map System.IO.Path.GetFileName |> Set.ofList
    let tableNames = table |> List.map fst
    let tableSet = Set.ofList tableNames
    let unlisted = Set.difference diskSet tableSet |> Set.toList
    let missing = Set.difference tableSet diskSet |> Set.toList
    let duplicated = tableNames |> List.countBy id |> List.filter (fun (_, c) -> c > 1) |> List.map fst
    let coverage =
        let name = "hash_join_corpus_route_census: the disk set of sql-joins files equals the route table's (complete filenames)"
        if unlisted.IsEmpty && missing.IsEmpty && duplicated.IsEmpty then
            resultLine Pass name $"{files.Length} files on disk, {tableNames.Length} rows, the same names"
            true
        else
            resultLine Fail name ($"on disk without a row: {unlisted}; rows without a file: {missing}; duplicated rows: {duplicated}")
            false
    let perFile =
        [ for p in files do
            match table |> List.tryFind (fun (k, _) -> k = System.IO.Path.GetFileName p) with
            | None -> ()
            | Some (k, route) ->
                let file = System.IO.Path.GetFileName p
                let name = $"hash_join_corpus_route: {file}"
                let src = System.IO.File.ReadAllText p
                match decisionsOf src, cppOfSource name src, withGate "BLADE_HASH_JOIN" "0" (fun () -> cppOfSource name src) with
                | Error e, _, _ | _, Error e, _ | _, _, Error e -> resultLine Fail name e; yield false
                | Ok ds, Ok cpp, Ok cppOff ->
                    let mine = ds |> List.filter (fun d -> d.Rule = hjRule)
                    let offCsr = cppOff.Contains "blade_join_build_i64(&"
                    let problems =
                        match route with
                        | Ok subs ->
                            [ for (s, planNeedle, cppNeedle) in subs do
                                if not (mine |> List.exists (fun d -> applied d && d.Subject = s && evidenceHas planNeedle d)) then
                                    yield $"no applied plan for `{s}` naming `{planNeedle}`"
                                if not (cpp.Contains cppNeedle) then yield $"no CSR build `{cppNeedle}`"
                                if not (cpp.Contains $"compound_index_t<2>* {s}_idx = new compound_index_t<2>(\"{s}_idx\", {s}_idx_extents, {s}__card, compound_index_t<2>::cardinality_only)") then
                                    yield $"no cardinality-only constructor for `{s}`"
                                if cpp.Contains $"{s}_idx_maskvec" then yield $"`{s}` still builds the N x M bit vector" ]
                        | Error decl ->
                            [ for (s, needle) in decl do
                                if not (mine |> List.exists (fun d -> d.Subject = s && declinedMentioning needle d)) then
                                    yield $"no decline for `{s}` naming `{needle}`"
                              if cpp.Contains "blade_join_build_i64(&" then yield "a CSR is emitted for a declined join" ]
                    let problems = problems @ (if offCsr then [ "BLADE_HASH_JOIN=0 still emits a CSR" ] else [])
                    let what = match route with Ok subs -> $"""applied ({subs |> List.map (fun (s, _, _) -> s) |> String.concat ", "}), CSR + cardinality-only index emitted""" | Error _ -> "declined with its reason, no CSR"
                    if problems.IsEmpty then
                        resultLine Pass name ($"{what}; gate off: no CSR")
                        yield true
                    else
                        resultLine Fail name (String.concat "; " problems)
                        yield false ]
    coverage :: perFile

let private hashJoinCodegenCases () =
    [ codegenDecisionCase "decision_hash_join_compound_representation" hjSrc "compound"
          (fun d -> applied d && d.Subject = "matched" && evidenceHas "the equijoin relation (hash-join v1)" d)
          "the compound representation record names the relation"
      // the LLVM lane: the rank-2 compound stays refused, and the plan says so
      (let name = "decision_hash_join_llvm_declines"
       Blade.Effects.Decisions.start ()
       let r =
           try
               match lower hjSrc with
               | Error e -> Error ($"lower: {e}")
               | Ok ir ->
                   match EmitLlvm.tryEmitProgramNamed "hj_llvm" ir with
                   | Ok _ -> Error "the llvm lane emitted a rank-2 compound"
                   | Error why -> Ok (why, Blade.Effects.Decisions.drain ())
           with ex -> Error ($"raised: {ex.Message}")
       Blade.Effects.Decisions.drain () |> ignore
       match r with
       | Error e -> resultLine Fail name e; false
       | Ok (why, ds) ->
           let rec' = ds |> List.exists (fun d -> d.Rule = hjRule && d.Subject = "matched" && declinedMentioning "llvm lane: the hash equijoin is not built in this lane (v1.1)" d)
           if rec' && why.Contains "the hash equijoin is not built in this lane" then
               resultLine Pass name "refused (the program falls back to C++) with the recorded `hash-join` decline"
               true
           else
               resultLine Fail name ($"refusal: {why}; recorded: {rec'}")
               false) ]

let runOptimizeTests () =
    printHeader "Blade-DSL: Optimization Layer Tests"
    let results =
        [ // Recognition derives the early exit; the abort stays absent --
          // cost changed, contract untouched.
          runCase "freeze_idiom_break_no_abort" freezeIdiom 1 0
          // The `while` spelling keeps its contract: break AND abort.
          runCase "while_spelling_break_and_abort" whileSpelling 1 1
          // Not absorbing -> declined: no break, no abort, full budget.
          runCase "ordinal_guard_declines" ordinalGuardDeclines 0 0
          // Callee judgment by EFFECT SUMMARY (Blade.Effects): an effectful
          // helper declines (directly or through a pure-looking wrapper);
          // a pure helper, a chain of pure helpers, and a pure user
          // function shadowing an intrinsic name are all admitted.
          runCase "effectful_guard_declines" effectfulGuardDeclines 0 0
          runCase "transitive_effectful_guard_declines" transitiveEffectfulGuardDeclines 0 0
          runCase "pure_helper_guard_recognized" pureHelperGuardRecognized 1 0
          runCase "transitive_pure_guard_recognized" transitivePureGuardRecognized 1 0
          runCase "shadowed_intrinsic_pure_recognized" shadowedIntrinsicPureRecognized 1 0
          // The decision record: what each pass decided and why.
          decisionCase "decision_freeze_applied" freezeIdiom "freeze-recognition" applied
              "freeze-recognition applied"
          decisionCase "decision_freeze_declined_effectful" effectfulGuardDeclines "freeze-recognition"
              (declinedMentioning "`tick`") "declined naming `tick`"
          decisionCase "decision_freeze_declined_ordinal" ordinalGuardDeclines "freeze-recognition"
              (declinedMentioning "step ordinal") "declined for the step ordinal"
          decisionCase "decision_fusion_applied" fusionChain "elementwise-fusion" applied
              "elementwise-fusion applied"
          decisionCase "decision_fusion_declined_conditional_abort" fusionMayAbortConditional "elementwise-fusion"
              (declinedMentioning "may abort") "declined: the inner kernel may abort under a branch"
          decisionCase "decision_fusion_applied_unconditional_abort" fusionMayAbortUnconditional "elementwise-fusion" applied
              "elementwise-fusion applied (inner read on every cell)"
          fusionRepeatedParamBoundOnce ()
          decisionCase "decision_fusion_applied_repeated_param" fusionRepeatedParamSrc "elementwise-fusion" applied
              "elementwise-fusion applied (inner value bound once per cell)"
          // Slice-write fusion: the recursion's row computed in place.
          decisionCase "decision_slice_write_applied" sliceWriteRk4Src "slice-write-fusion" applied
              "slice-write-fusion applied (pointwise RK4 step)"
          decisionCase "decision_slice_write_declined_fold" sliceWriteFoldSrc "slice-write-fusion"
              (declinedMentioning "not pointwise") "declined: the step folds over its row (not pointwise)"
          decisionCase "decision_slice_write_declined_may_abort" sliceWriteIntDivSrc "slice-write-fusion"
              (declinedMentioning "may abort") "declined: integer division may abort"
          decisionCase "decision_slice_write_declined_deferred" sliceWriteDeferredSrc "slice-write-fusion"
              (declinedMentioning "deferred") "declined: the step forces a deferred module binding"
          sliceWriteRk4Emission ()
          // Row-fold fusion: the trajectory's row checksum folded in place.
          decisionCase "decision_row_fold_applied" sliceWriteRk4Src "row-fold-fusion" applied
              "row-fold-fusion applied (prodsum checksum of the RK4 trajectory)"
          decisionCase "decision_row_fold_declined_while" rowFoldWhileSrc "row-fold-fusion"
              (declinedMentioning "freeze") "declined: a while-guarded recursion rewrites rows after the loop"
          rowFoldRk4Emission ()
          // Wave 3, E-G5: the threaded `let rec` step's admission and forms.
          yield! recStepCases ()
          // Reverse-mode AD of an additive recurrence is O(n): loop count pin.
          recarrayGradEmission ()
          recarrayGradNonlinearEmission ()
          // A dense halo source is read directly, with no carousel ring.
          haloDenseSourceReadsDirect ()
          // Streaming reductions (structural/03): the join share read by a
          // direct-fold leg, and the deferred outer product's partial fold.
          joinShareReadByDirectFold ()
          outerProductPartialFoldStreams ()
          // Scratch reuse across barriers (fortran-killer-2 section 4, gate
          // 2): the alias declarations, the decision record, and the
          // escape-analysis leak the work found.
          // Pinned with compute elision OFF (ACCEPTED at the workstream-C
          // review): elision (BLADE_COMPUTE_ELIDE) removes the single-fold
          // temporary this fixture reuses (the returned fold's operand), which is
          // fewer pools, not a reuse regression; reuse is about STORED temporaries.
          withGate "BLADE_COMPUTE_ELIDE" "0" poolReuseReturnTakesDeadPool
          // Pinned with compute elision OFF (ACCEPTED, as above): elision fuses
          // `q` into `r` and `s` into the returned fold, leaving one reuser.
          withGate "BLADE_COMPUTE_ELIDE" "0" poolReuseChainSharesRoot
          // Transpose normal form and the read-only alias plan (P2 entry).
          yield! transposeNormalFormCases ()
          // Hot-row fold scheduling (bench review M13).
          hotRowEmitted ()
          hotRowGateOff ()
          decisionCase "decision_hot_row_applied" hotRowSrc "hot-row-fold" applied
              "hot-row-fold applied"
          withGate "BLADE_HOT_ROW" "0" (fun () ->
              decisionCase "decision_hot_row_declined_gate" hotRowSrc "hot-row-fold"
                  (declinedMentioning "disabled by BLADE_HOT_ROW") "declined: disabled by BLADE_HOT_ROW")
          decisionCase "decision_hot_row_declined_lost_team" hotRowLostTeamSrc "hot-row-fold"
              (declinedMentioning "lose their team") "declined: the fold would lose its Path B team"
          decisionCase "decision_hot_row_declined_reads_beyond" hotRowReadsBeyondSrc "hot-row-fold"
              (declinedMentioning "beyond its own row") "declined: the row kernel reads beyond its row"
          // Round-2 cell epilogues (B1 / B2r).
          cepSaxpySerialLicensed ()
          cepSaxpyThreadedLicensed ()
          cepSaxpyRepro ()
          cepStencilRepro ()
          cepRecurRepro ()
          cepGateOff ()
          yield! cepDecisionCases ()
          // Round 3 A7c: producer-input legs (epilogue-legs v1, G69).
          // Every A7c test below builds under the EXPLICIT opt-in arm (BLADE_EPILOGUE_LEGS=1:
          // the kept one-level inline is opt-in since W2-4 round 8); the default build's
          // decline is asserted on its own, after them.
          yield! withGate "BLADE_EPILOGUE_LEGS" "1" (fun () ->
            [
              eplCppRepro ()
              eplCppLicensed ()
              eplCppGateOff ()
              eplLlvm ()
              eplLlvmAccumulators ()
              eplFailureFixtureRoute ()
              eplThrowingCalleeKeepsCall ()
              eplCorpus028 ()
              eplTeamNoCensus ()
              eplReturnPointFree ()
              eplJoinInsideCallee ()
              eplTwoPlansOneCall ()
              eplThrowingCalleeLlvm ()
              eplLlvmReleaseOrder ()
              eplRound4Cpp ()
              eplValidateRule ()
              eplFailClosedDeclines ()
              eplDeclineKeepsSiblingInline ()
              eplLabelReadNotInlined ()
              eplValueTypePredicate ()
              eplDurableStart ()
              eplDuplicateReturnPinned ()
              eplValidateProvenance ()
              eplKeptInlineWarns ()
              withGate "BLADE_FP_REPRO" "1" (fun () -> withGate "BLADE_OMP_THREADS" "1" (fun () ->
                decisionCase "epilogue_legs_cse_probe_merges_without_event" eplCseProbeSrc "cse" applied "cse applied: without an event the equal maps merge"))
              withGate "BLADE_OMP_THREADS" "1" (fun () ->
                eplNoRouteKeepsCall "epilogue_legs_nonliteral_epilogue_seed_keeps_call" eplNonliteralSeedSrc [ ("BLADE_FP_REPRO", "1") ] "seed is not a literal")
              withGate "BLADE_OMP_THREADS" "1" (fun () ->
                eplNoRouteKeepsCall "epilogue_legs_lanes_off_keeps_call" eplSaxpySrc [ ("BLADE_FOLD_LANES", "off") ] "BLADE_FOLD_LANES=off")
              yield! eplDecisionCases () ])
          eplDefaultDeclinesInline ()
          // Compaction fusion (round 2 item B4).
          compactionFusedLicensed ()
          compactionFusedRepro ()
          compactionGateOff ()
          compactionOmpFoldKeepsPool ()
          compactionAliasWriteNoInPass ()
          // Compaction fusion v2 (round 3 item T4: the gather at the mask
          // site, positions elided under the demand census).
          compactionV2DefaultElides ()
          compactionV2Licensed ()
          compactionV2Repro ()
          compactionV2KeyedKeepsPositions ()
          compactionV2SelCapFallback ()
          compactionV2RuntimeGuard ()
          yield! compactionV2DecisionCases ()
          // T4c (round 3, W2-2): one selection for every compound over a mask
          t4cDefaultEmission ()
          t4cKeyedEmission ()
          t4cReproEmission ()
          t4cGatherEmission ()
          t4cUnsharedEmission ()
          t4cGateOffEmission ()
          t4cLlvmEmission ()
          yield! t4cDecisionCases ()
          // T4c fix round (Codex diff review of 278353cb, findings 1-3)
          yield! t4cLegacySingletonIdentity ()
          yield! t4cNewSingletonRoutes ()
          yield! t4cStorageProofCases ()
          yield! t4cRuntimeRoutes ()
          // Membership hoisting (round 3 B-T1).
          yield! memberHoistDecisionCases ()
          memberHoistEmission ()
          memberHoistGateOffEmission ()
          yield! memberHoistForcedEmission ()
          yield! memberHoistCorpusRoutes ()
          yield! memberHoistPlacementMutations ()
          yield! memberHoistFailClosedGuards ()
          // Hash equijoin (round 3 W3-2, item T2).
          yield! hashJoinDecisionCases ()
          hashJoinEmission ()
          hashJoinGateOffEmission ()
          hashJoinPositionalEmission ()
          yield! hashJoinCodegenCases ()
          yield! hashJoinCorpusRoutes ()
          // Aggregate-only grouping (round 3 wave 3, W3-1; gap G71).
          yield! groupAggDecisionCases ()
          groupAggCppEmission ()
          groupAggCppPositional ()
          groupAggLlvmEmission ()
          groupKeysLlvmInlineProducerScan ()
          compactionV2UngroupIndexExtentCensus ()
          yield! intervalPayloadDecisionCases ()
          // Compact row classes (round 2 item B3).
          compactRowEmitted ()
          compactRowReproDeclined ()
          codegenDecisionCase "decision_compact_row_classes_applied" compactRowSrc "compact-row-classes"
              (fun d -> applied d && d.Evidence |> List.exists (fun e -> e.Contains "compact row classes (2 classes), per-row partials, combined in row order"))
              "applied: compact row classes (2 classes)"
          withGate "BLADE_FP_REPRO" "1" (fun () ->
              codegenDecisionCase "decision_compact_row_classes_declined_repro" compactRowSrc "compact-row-classes"
                  (declinedMentioning "repro build keeps the ordered walk") "declined: repro build keeps the ordered walk")
          withGate "BLADE_COMPACT_ROW_CLASSES" "0" (fun () ->
              decisionCase "decision_compact_row_classes_declined_gate" compactRowSrc "compact-row-classes"
                  (declinedMentioning "disabled by BLADE_COMPACT_ROW_CLASSES") "declined: disabled by BLADE_COMPACT_ROW_CLASSES")
          decisionCase "decision_compact_row_classes_declined_reader" compactRowReaderBetweenSrc "compact-row-classes"
              (declinedMentioning "between the producer and the fold") "declined: the producer is read before the fold"
          codegenDecisionCase "decision_compact_row_classes_declined_late_scalar" compactRowLateScalarSrc "compact-row-classes"
              (declinedMentioning "not bound before the producer") "declined: the cell body reads a scalar bound after the producer"
          decisionCase "decision_compact_row_classes_declined_writing_seed" compactRowWritingSeedSrc "compact-row-classes"
              (declinedMentioning "the fold itself may write") "declined: the fold's seed writes the producer"
          decisionCase "decision_compaction_fold_declined_write_after_map" compactionMutatedSnapshotSrc "compaction-fusion"
              (fun d -> d.Subject = "s" && declinedMentioning "may write" d) "fold declined: a write between the map and the fold"
          decisionCase "decision_compaction_fold_alias_write_not_fed" compactionAliasWriteSrc "compaction-fusion"
              (fun d -> d.Subject = "s" && applied d && d.Evidence |> List.exists (fun e -> e.Contains "does not feed it"))
              "fold applied at its site, not fed in the compaction pass"
          decisionCase "decision_compaction_applied" compactionSrc "compaction-fusion"
              (fun d -> applied d && d.Subject = "y") "compaction-fusion applied to y"
          decisionCase "decision_compaction_fold_applied" compactionSrc "compaction-fusion"
              (fun d -> applied d && d.Subject = "checksum") "compaction-fusion applied to the checksum fold"
          withGate "BLADE_COMPACTION_FUSION" "0" (fun () ->
              decisionCase "decision_compaction_declined_gate" compactionSrc "compaction-fusion"
                  (declinedMentioning "disabled by BLADE_COMPACTION_FUSION") "declined: disabled by BLADE_COMPACTION_FUSION")
          decisionCase "decision_compaction_declined_mask_read_between" compactionMaskReadBetweenSrc "compaction-fusion"
              (declinedMentioning "between the mask and the compound") "declined: the mask is read between mask and compound"
          decisionCase "decision_compaction_declined_predicate" compactionIntrinsicPredSrc "compaction-fusion"
              (declinedMentioning "not a pure, total scalar function") "declined: the predicate is not judged pure and total"
          decisionCase "decision_compaction_declined_rank2" compactionRank2Src "compaction-fusion"
              (declinedMentioning "rank-1 compaction only") "declined: a rank-2 mask"
          decisionCase "decision_compaction_fold_declined_path_b" compactionOmpFoldSrc "compaction-fusion"
              (fun d -> d.Subject = "checksum" && declinedMentioning "thread team" d) "fold declined: a `where omp` fold kernel (Path B)"
          decisionCase "decision_compaction_fold_declined_not_square" compactionNotSquareSrc "compaction-fusion"
              (fun d -> d.Subject = "checksum" && declinedMentioning "self-product" d) "fold declined: the map is not the self-product"
          scalarCaptureNoLongerPinsSource ()
          // Partial sparse reads: every let-bound gather freed by its own
          // producer's teardown, the escaping ones spared.
          sparseGatherTeardowns ()
          inlineGatherBoundOnce ()
          inlineGatherEveryPosition ()
          decisionCase "decision_pool_reuse_applied" poolReuseChainSrc "pool-reuse" applied
              "pool-reuse applied"
          // Let-level CSE over repeatable values, and its decision.
          cseDropsRepeatedFold ()
          decisionCase "decision_cse_applied" cseSrc "cse" applied "cse applied"
          decisionCase "decision_cse_identical_lambdas_applied" cseIdenticalLambdasSrc "cse" applied
              "cse applied to two folds with identical lambda kernels"
          // With sibling-fold sharing on, the pure `sumsq` is inlined and the
          // two identical sums become ONE join leg before CSE runs (the pass
          // dedupes its legs); the CSE property is pinned with it off.
          withGate "BLADE_SIBLING_FOLDS" "0" (fun () ->
              decisionCase "decision_cse_across_pure_call_applied" cseAcrossPureCallSrc "cse" applied
                  "cse applied across a call that writes nothing")
          // Legality: a write between the two evaluations is a barrier, a
          // let the write names is never merged, and callable identity is
          // structural (never a `%A` rendering).
          notAppliedCase "cse_twin_of_mutated_array" cseTwinOfMutatedSrc "cse"
              "the let mut twin is written through a mut argument"
          notAppliedCase "cse_barrier_lambda_alias_kernel" cseLambdaAliasKernelSrc "cse"
              "the kernel is a local alias the judge cannot see through"
          notAppliedCase "cse_barrier_mut_param_call" cseMutParamCallSrc "cse"
              "a call writing the array through a mut parameter sits between the folds"
          notAppliedCase "cse_barrier_global_write_call" cseGlobalWriteCallSrc "cse"
              "a call writing a module-level let mut sits between the folds"
          notAppliedCase "cse_float_literal_identity" cseFloatLiteralSrc "cse"
              "0.1 and 0.10000000001 are different kernels"
          notAppliedCase "cse_signed_zero_identity" cseSignedZeroSrc "cse"
              "0.0 and -0.0 are different kernels"
          // The escape hatches (charter rule 3): emission and decision.
          cseGateOffKeepsFolds ()
          withGate "BLADE_CSE" "0" (fun () ->
              decisionCase "decision_cse_disabled" cseSrc "cse" (declinedMentioning "disabled by BLADE_CSE")
                  "cse declined, disabled by BLADE_CSE")
          poolReuseGateOffEmitsNoAlias ()
          withGate "BLADE_POOL_REUSE" "0" (fun () ->
              decisionCase "decision_pool_reuse_disabled" poolReuseChainSrc "pool-reuse"
                  (declinedMentioning "disabled by BLADE_POOL_REUSE") "pool-reuse declined, disabled by BLADE_POOL_REUSE")
          // Kernel-invariant hoisting: the emission, the guard, the decision,
          // the two refusals (a writing callee; a may-abort value the kernel
          // reads only under a branch) and the escape hatch.
          hoistMovesCallOutOfNest ()
          hoistGuardsOnNonEmptyNest ()
          decisionCase "decision_hoist_applied" hoistSrc "invariant-hoist" applied "invariant-hoist applied"
          decisionCase "decision_hoist_guarded" hoistGuardedSrc "invariant-hoist"
              (fun d -> applied d && d.Evidence |> List.exists (fun e -> e.Contains "when the iteration space is non-empty"))
              "applied, bound under the nest's non-emptiness"
          notAppliedCase "hoist_declines_writing_callee" hoistEffectfulSrc "invariant-hoist"
              "the call writes through a mut parameter: once per cell, as written"
          notAppliedCase "hoist_declines_conditional_may_abort" hoistConditionalSrc "invariant-hoist"
              "the may-abort call is read only under the kernel's branch"
          hoistGateOffKeepsPerCellCall ()
          // The late (post-monomorphization) array/scalar broadcast takes the
          // same road: scalar in the kernel, hoisted under the guard.
          lateBroadcastGuardedOnNonEmptyNest ()
          lateBroadcastEmbedsScalarInKernel ()
          decisionCase "decision_hoist_late_broadcast_guarded" lateBroadcastSrc "invariant-hoist"
              (fun d -> applied d && d.Evidence |> List.exists (fun e -> e.Contains "when the iteration space is non-empty"))
              "applied to the late broadcast, bound under the nest's non-emptiness"
          withGate "BLADE_HOIST" "0" (fun () ->
              decisionCase "decision_hoist_disabled" hoistSrc "invariant-hoist"
                  (declinedMentioning "disabled by BLADE_HOIST") "invariant-hoist declined, disabled by BLADE_HOIST")
          // The gram_apply advisory: left as written, spelled in the evidence.
          decisionCase "decision_gram_apply_advisory" gramAdvisorySrc "gram-apply-advisory"
              (declinedMentioning "gram_apply(A, A, v)") "advisory names gram_apply(A, A, v)"
          // Compute elision (BLADE_COMPUTE_ELIDE): single-consumer body computes.
          yield! computeElisionCases ()
          yield! ceRemainderCases ()
          // Call elision (BLADE_CALL_ELIDE): a supported call read in place.
          yield! callElisionCases () ]
        @ planCases ()
        @ coIterIdentityPins ()
        @ siblingCases ()
        @ [ parallelEmissionIsolated (); declSpanResetIsolated () ]
        @ validatorPins ()
    let passed = results |> List.filter id |> List.length
    let failed = results.Length - passed
    printFooter "Optimization Layer" [$"{passed} passed"; $"{failed} failed"]
    { Block = "Optimization Layer"; Passed = passed; Failed = failed; Skipped = 0
      FailedNames = if failed = 0 then [] else ["see above"] }
