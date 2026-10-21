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
        let rowStore = System.Text.RegularExpressions.Regex.IsMatch(body, @"__v\d+\[__k\d+\]\[__k\d+\] = __v\d+;")
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
        let descending = System.Text.RegularExpressions.Regex.IsMatch(cpp, @"- 1L\) - __k\d+\)")
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
        let descending = System.Text.RegularExpressions.Regex.IsMatch(cpp, @"- 1L\) - __k\d+\)")
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
      // review finding 6: a whole-array kernel (input rank 1) is no cellwise map
      decisionCase "decision_compaction_v2_whole_array_kernel_requires"
          (compactionV2DemandSrc "function at0(c: Array<Float64 like CompoundIdx<keep>>) -> Float64 = c(2)\nlet w = object_for(at0) <@> y |> compute\n") "compaction-fusion"
          (fun d -> v2Applied d && evidenceHas "positions required by `y` as an operand of IRApplyCombinator" d)
          "positions required by a whole-array kernel (nonzero input rank)"
      // a cardinality inside an expression's TYPE (a range over `extents(y)`) is certified
      decisionCase "decision_compaction_v2_extent_in_an_embedded_type_elides"
          (compactionV2DemandSrc "let r = 0..extents(y) |> compute\nlet rs = reduce(r, (+))\n") "compaction-fusion"
          (fun d -> v2Applied d && evidenceHas "selection: positions elided" d) "an embedded type's `extents(y)` keeps the table elided"
      decisionCase "decision_compaction_v2_shared_mask_first_declined" compactionV2SharedMaskSrc "compaction-fusion"
          (fun d -> d.Version = 2 && d.Subject = "y" && declinedMentioning "its in-pass count serves one, the last (`y2`)" d)
          "the first compound over a shared mask says it keeps the separate passes"
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
          decisionCase "decision_compaction_v2_gather_declined_shared_mask" compactionV2SharedMaskSrc "compaction-fusion"
              (fun d -> d.Version = 2 && applied d && d.Subject = "y2" && evidenceHas "gather: declined -- `keep` feeds 2 compounds" d)
              "the last compound over a shared mask: fused, gather declined (a shared selection is T4c)")
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

let private planCases () =
    let pinBlas (v: string) f =
        withGate "BLADE_BLAS" v (fun () -> withGate "OPENBLAS_DIR" null (fun () -> withGate "BLADE_BLAS_LINK" null f))
    [ planCase "plan_symmetric_storage_applied" planCommSameArraySrc "symmetric-storage" applied
          "packed storage for the comm covariance of one array"
      planCase "plan_triangular_iteration_applied" planCommSameArraySrc "triangular-iteration" applied
          "triangular iteration over the commuting levels"
      planCase "plan_symmetric_storage_declined_distinct" planCommDistinctSrc "symmetric-storage"
          (declinedMentioning "different arrays") "declined: the commuting positions hold different arrays"
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
      // The review's route shapes (bench/notes/microkernel-sketches-review.md M5):
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

/// The surface join's implicit seed for an unseeded (+) leg the USER wrote is
/// +0.0 (TypeCheckInfer's rule, untouched by the sibling-fold pass, which
/// seeds the legs IT builds with -0.0): the join's accumulators are declared
/// `= 0.0` (leg 0) and `= 0.0` (prodsum).
let private siblingSurfaceJoinSeed () =
    let name = "sibling_folds_surface_join_keeps_plus_zero_seed"
    let src =
        "type R = Idx<3>\n"
        + "let z: Array<Float64 like R> = [-0.0, -0.0, -0.0]\n"
        + "let (j_surface, j_other) = object_for(<&!>) <@> (reduce(z, (+)), prodsum(z, z))\n"
    match withGates siblingSerialPins (fun () -> cppOfSource name src) with
    | Error e -> resultLine Fail name e; false
    | Ok cpp ->
        let seeds =
            System.Text.RegularExpressions.Regex.Matches(mainOf cpp, @"double __(?:v|tup_)\d+_\d+ = ([^;]+);")
            |> Seq.map (fun x -> x.Groups.[1].Value) |> List.ofSeq
        if seeds = [ "0.0"; "0.0" ] then
            resultLine Pass name "the user-written join's unseeded (+) leg keeps its +0.0 seed"
            true
        else
            resultLine Fail name ($"""seeds=[{String.concat "; " seeds}]""")
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
                      Kernels = Set.ofSeq kernels; Forced = 0 }
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
          poolReuseReturnTakesDeadPool ()
          poolReuseChainSharesRoot ()
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
          // Membership hoisting (round 3 B-T1).
          yield! memberHoistDecisionCases ()
          memberHoistEmission ()
          memberHoistGateOffEmission ()
          yield! memberHoistForcedEmission ()
          yield! memberHoistCorpusRoutes ()
          yield! memberHoistPlacementMutations ()
          yield! memberHoistFailClosedGuards ()
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
              (declinedMentioning "gram_apply(A, A, v)") "advisory names gram_apply(A, A, v)" ]
        @ planCases ()
        @ siblingCases ()
        @ [ parallelEmissionIsolated (); declSpanResetIsolated () ]
        @ validatorPins ()
    let passed = results |> List.filter id |> List.length
    let failed = results.Length - passed
    printFooter "Optimization Layer" [$"{passed} passed"; $"{failed} failed"]
    { Block = "Optimization Layer"; Passed = passed; Failed = failed; Skipped = 0
      FailedNames = if failed = 0 then [] else ["see above"] }
