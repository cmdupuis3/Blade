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

// PM19: these cases explicitly select the terminal output because named
// top-level mask/compound bindings are observable under default printing.
let private selectedTerminalSrc =
    "let x = [1.0, -2.0, 3.0, 0.0, 4.0]\n"
    + "let keep = mask(x, lambda(v) -> v > 0.0)\n"
    + "let y = compound(x, keep)\n"
    + "let result = reduce(y, lambda(a, b) -> a * 2.0 + b, 1.0)\n"

let private withSelectedTerminal f =
    withGate "BLADE_PRINT" "result" (fun () -> withGate "BLADE_FP_REPRO" "1" f)

let private selectedTerminalEmission () =
    let name = "selected_terminal_repro_elides_all_selection_storage"
    withSelectedTerminal (fun () ->
        match cppOfSource name selectedTerminalSrc with
        | Error e -> resultLine Fail name e; false
        | Ok cpp ->
            checkAll name cpp "guarded serial fold over stored x, no mask/index/selected-value allocation"
                [ "guarded route actually emitted", cpp.Contains "__selected_terminal_"
                  "dense input retained", cpp.Contains "x_extents"
                  "no mask buffer", not (cpp.Contains "keep_extents")
                  "no compact values or index", not (cpp.Contains "y_compact") && not (cpp.Contains "y_idx")
                  "no positions", not (cpp.Contains "y_pos")
                  "no compact reads", not (cpp.Contains "y.data[")
                  "terminal output retained", cpp.Contains "\"result = \"" ])

let private selectedTerminalRefusals () =
    let cases =
        [ "extra_mask_read", selectedTerminalSrc + "let observed = keep(0)\n", "additional consumer"
          "keyed_selection_read", selectedTerminalSrc + "let observed = y(0)\n", "additional consumer"
          "selection_alias", selectedTerminalSrc + "let observed = y\n", "additional consumer"
          "selection_capture", selectedTerminalSrc + "let observed = method_for(x) <@> lambda(v) -> Float64(extents(y)) + v\n", "callable body or capture"
          "selection_extent", selectedTerminalSrc + "let observed = extents(y)\n", "additional consumer"
          "selected_map", selectedTerminalSrc.Replace("reduce(y, lambda", "reduce(y * y, lambda"), "direct seeded"
          "unseeded", selectedTerminalSrc.Replace(", 1.0)\n", ")\n"), "direct seeded"
          "may_fail_predicate", selectedTerminalSrc.Replace("v > 0.0", "lgamma(v) > 0.0"), "pure/total"
          "may_fail_fold", selectedTerminalSrc.Replace("a * 2.0 + b", "a + lgamma(b)"), "pure/total"
          "nonliteral_seed", selectedTerminalSrc.Replace("let keep", "let seed = 1.0\nlet keep").Replace(", 1.0)\n", ", seed)\n"), "explicit seed"
          "source_write", selectedTerminalSrc.Replace("let x =", "let mut x =").Replace("let y =", "x(0) = 9.0\nlet y ="), "may write"
          "hidden_writer",
              "type I = Idx<5>\nfunction bump(a: mut Array<Float64 like I>) -> Float64 = {\n    a(0) += 1.0\n    a(0)\n}\n"
              + selectedTerminalSrc.Replace("let x =", "let mut x: Array<Float64 like I> =").Replace("let y =", "let changed = bump(x)\nlet y ="), "may write"
          "source_alias_writer",
              "type I = Idx<5>\nfunction bump(a: mut Array<Float64 like I>) -> Float64 = {\n    a(0) += 1.0\n    a(0)\n}\n"
              + selectedTerminalSrc.Replace("let x =", "let mut x: Array<Float64 like I> =").Replace("let keep", "let mut alias = x\nlet keep").Replace("let y =", "let changed = bump(alias)\nlet y ="), "may write"
          "side_table_writer",
              "import rand as r\ntype I = Idx<5>\nfunction bump(a: mut Array<Float64 like I>) -> Int64 = {\n    a(0) += 1.0\n    7\n}\n"
              + selectedTerminalSrc.Replace("let x =", "let mut x: Array<Float64 like I> =").Replace("let y =", "let noise = r.uniform(bump(x), 1)\nlet y ="), "may write"
          "deferred_source",
              selectedTerminalSrc.Replace("let x = [1.0, -2.0, 3.0, 0.0, 4.0]", "let x = method_for(range<Idx<5>>) <@> lambda(i) -> Float64(Int64(i))"), "not proved stored"
          "parallel_fold", selectedTerminalSrc.Replace("lambda(a, b) -> a * 2.0 + b", "lambda(a, b) where comm(a, b), omp -> a + b"), "pure/total"
          "second_companion", selectedTerminalSrc + "let other = compound(x, keep)\n", "compound() operand" ]
    [ for suffix, src, why in cases do
        let name = $"selected_terminal_declines_{suffix}"
        yield withSelectedTerminal (fun () ->
            decisionCase name src "selected-terminal" (declinedMentioning why) why)
      for suffix, gate, value, why in
          [ "default_print", "BLADE_PRINT", "", "printed"
            "selected_output", "BLADE_PRINT", "result;y", "printed"
            "mask_output", "BLADE_PRINT", "result keep", "printed"
            "default_licensed_graph", "BLADE_FP_REPRO", "0", "reproducible"
            "optimizer_off", "BLADE_COMPACTION_FUSION", "0", "disabled"
            "legacy_mirror", "BLADE_COMPACTION_FUSION", "b4", "disabled"
            "positions_elided_mirror", "BLADE_COMPACTION_FUSION", "unshared", "disabled" ] do
        let name = $"selected_terminal_declines_{suffix}"
        yield withSelectedTerminal (fun () -> withGate gate value (fun () ->
            decisionCase name selectedTerminalSrc "selected-terminal" (declinedMentioning why) why))
      // lowerTypeExpr erases the mask in this UNUSED alias to IRParam
      // ("compound", 0, IRTNat None). It has no surviving keep reference;
      // a real declared-index payload is checked independently below.
      yield withSelectedTerminal (fun () ->
          decisionCase "selected_terminal_erased_unused_alias_admitted"
              (selectedTerminalSrc.Replace("let y =", "type Selected = CompoundIdx<keep>\nlet y ="))
              "selected-terminal" (fun d -> d.Subject = "y" && applied d)
              "the unused erased alias carries no mask demand") ]

let private selectedTerminalForeignRef () =
    let name = "selected_terminal_foreign_census_keeps_storage"
    withSelectedTerminal (fun () ->
        match withGate "BLADE_COMPACTION_FUSION" "0" (fun () -> lower selectedTerminalSrc) with
        | Error e -> resultLine Fail name e; false
        | Ok ir ->
            let m = ir.Modules.Head
            let y = m.Bindings |> List.find (fun b -> b.Name = "y")
            Blade.Effects.Decisions.start ()
            let kept = Optimize.fuseSelectedTerminalModule (IR.IRBuilder ()) m.Functions (Set.singleton y.Id) m
            let ds = Blade.Effects.Decisions.drain ()
            if kept.CompoundInits.ContainsKey y.Id
               && (ds |> List.exists (fun d -> d.Rule = "selected-terminal" && declinedMentioning "another module" d)) then
                resultLine Pass name "a foreign demand cannot be erased by the module-local census"; true
            else resultLine Fail name "the foreign use did not retain the selected store"; false)

/// lowerIndexType's real CompoundIdx axis carries IRCompoundMask(IRVar keep),
/// whereas an unused standalone alias lowers through lowerTypeExpr and loses
/// that operand. Inject the actual index payload to test the declared-type
/// seam directly, as compactionV2UngroupIndexExtentCensus tests embedded types.
let private selectedTerminalDeclaredIndexPayload () =
    let name = "selected_terminal_declared_index_payload_local_and_foreign"
    withSelectedTerminal (fun () ->
        match withGate "BLADE_COMPACTION_FUSION" "0" (fun () -> lower selectedTerminalSrc) with
        | Error e -> resultLine Fail name e; false
        | Ok ir ->
            let m = ir.Modules.Head
            let y = m.Bindings |> List.find (fun b -> b.Name = "y")
            let keep = m.Bindings |> List.find (fun b -> b.Name = "keep")
            let at = match y.Type with Blade.IR.ArrayElem a -> a | _ -> failwith "y is no array"
            let ix = { at.IndexTypes.Head with Id = 990003; Extent = Blade.IR.IRCompoundMask (Blade.IR.IRVar (keep.Id, keep.Type)) }
            let td = Blade.IR.IRTDIndexType ("DemandedSelection", ix)
            let local = { m with Types = m.Types @ [ td ] }
            // ONLY the declared type survives in Other. No executable value,
            // callable, side-table operand or capture can supply its demand.
            let other =
                { m with Name = "Other"; Types = [ td ]; Bindings = []; Functions = []
                         CompoundInits = Map.empty; SparseInits = Map.empty; RandomInits = Map.empty
                         ProviderWrites = Map.empty; ProviderReads = Map.empty }
            let retained (md: Blade.IR.IRModule) foreign why =
                Blade.Effects.Decisions.start ()
                let kept = Optimize.fuseSelectedTerminalModule (IR.IRBuilder ()) md.Functions foreign md
                let ds = Blade.Effects.Decisions.drain ()
                kept.CompoundInits.ContainsKey y.Id
                && (kept.Bindings |> List.exists (fun b -> b.Id = keep.Id))
                && (ds |> List.exists (fun d -> d.Rule = "selected-terminal" && d.Subject = "y" && declinedMentioning why d))
                && not (ds |> List.exists (fun d -> d.Rule = "selected-terminal" && applied d))
            let foreignRefs = Optimize.moduleReferencedIds other
            checkAll name "" "a real declared-index mask payload keeps storage locally and across modules"
                [ "the type itself names keep", Optimize.moduleReferencedIds other |> Set.contains keep.Id
                  "local type demand retains both stores", retained local Set.empty "index type"
                  "foreign type demand retains both stores", foreignRefs.Contains keep.Id && retained m foreignRefs "another module" ])

/// An optimizer bug can agree in the interpreter and C++ lanes because both
/// consume rewritten IR. Compare interpreter ON/OFF AND independent small
/// pins, and assert admission happened; inert tests cannot pass this gate.
let private selectedTerminalInterpreterCases () =
    let cases =
        [ "ordered_noncommutative", selectedTerminalSrc, "22.0"
          "empty_seed", selectedTerminalSrc.Replace("v > 0.0", "v > 100.0").Replace(", 1.0)\n", ", -7.25)\n"), "-7.25"
          "all_selected", selectedTerminalSrc.Replace("v > 0.0", "v > -100.0"), "48.0"
          "empty_input", "let x: Array<Float64 like Idx<0>> = []\nlet keep = mask(x, lambda(v) -> v > 0.0)\nlet y = compound(x, keep)\nlet result = reduce(y, (+), 3.25)\n", "3.25"
          "cancellation", "let x = [1e16, 1.0, -1e16, 3.0]\nlet keep = mask(x, lambda(v) -> v == v)\nlet y = compound(x, keep)\nlet result = reduce(y, (+), 0.0)\n", "3.0"
          "negative_zero_seed", "let x = [0.0, -0.0]\nlet keep = mask(x, lambda(v) -> v > 1.0)\nlet y = compound(x, keep)\nlet result = reduce(y, (+), -0.0)\n", "-0.0" ]
    [ for suffix, src, expected in cases do
        let name = $"selected_terminal_interp_on_off_{suffix}"
        yield withSelectedTerminal (fun () ->
            let interpret gate = withGate "BLADE_COMPACTION_FUSION" gate (fun () ->
                match lower src with
                | Error e -> Error e
                | Ok ir ->
                    match IRValidate.validateIR ir with
                    | Error es -> Error (String.concat "; " es)
                    | Ok ir ->
                        let admitted = ir.Modules |> List.exists (fun m -> m.Functions |> List.exists (fun f -> f.Name.Contains "__selected_terminal_"))
                        let r = Blade.Interp.Run.runProgram ir name Blade.Interp.Value.defaultLimits
                        Ok (admitted, r))
            match interpret "on", interpret "0" with
            | Ok (true, on), Ok (false, off) when on.ExitCode = 0 && off.ExitCode = 0
                                                     && on.Stdout = off.Stdout && on.Stderr = off.Stderr
                                                     && on.Stdout.Contains ($"result = {expected}") ->
                resultLine Pass name "admitted route agrees with unoptimized IR and independent scalar pin"; true
            | a, b -> resultLine Fail name ($"ON={a}; OFF={b}"); false) ]

// PM10: the named input stays stored/printed. Only its anonymous mapped
// rank-2 intermediate is private; result rows keep their original R index.
let private partialRowsSrc =
    "type R = Idx<2>\ntype C = Idx<3>\n"
    + "let M: Array<Float64 like R, C> = [[1.0, 2.0, 3.0], [4.0, 5.0, 6.0]]\n"
    + "let rows = reduce(method_for(M) <@> lambda(v: Float64) -> v * v + 1.0, (+), 7.0)\n"

let private partialRowsNamedSrc =
    partialRowsSrc.Replace("let rows = reduce(method_for(M) <@> lambda(v: Float64) -> v * v + 1.0, (+), 7.0)",
        "let P = method_for(M) <@> lambda(v: Float64) -> v * v + 1.0 |> compute\nlet rows = reduce(P, (+), 7.0)\nlet sample = P(1, 2)")

let private withPartialRows f =
    withGate "BLADE_PRINT" "" (fun () -> withGate "BLADE_FP_REPRO" "1" (fun () ->
        withGate "BLADE_FP_CONTRACT" "off" f))

let private partialRowsEmission () =
    let name = "partial_rows_cpp_eliminates_only_the_private_mapped_matrix"
    withPartialRows (fun () ->
        let emit gate = withGate "BLADE_FUSION" gate (fun () -> cppOfSource name partialRowsSrc)
        match emit "on", emit "0" with
        | Ok cpp, Ok old ->
            let pools (s: string) = System.Text.RegularExpressions.Regex.Matches(s, @"allocate<typename promote<double, 2>::type, nullptr>").Count
            checkAll name cpp "required M + rows retained; anonymous mapped matrix allocation/fill/read removed"
                [ "real row-terminal callable", cpp.Contains "__partial_rows_"
                  "one required rank-2 pool", pools cpp = 1 && pools old = 2
                  "source print retained", cpp.Contains "\"M = [\""
                  "row output print retained", cpp.Contains "\"rows = [\"" ]
        | a, b -> resultLine Fail name ($"ON={a}; OFF={b}"); false)

let private partialRowsLlvm () =
    withPartialRows (fun () ->
        // L1 restores method_for's row-input forcing boundary in LLVM.
        // C02 safely removes that pool ON; the corrected OFF reference has it.
        let route name src demanded =
            let emit gate = withGate "BLADE_FUSION" gate (fun () ->
                match lower src with Error e -> Error e | Ok ir -> EmitLlvm.tryEmitProgramNamed name ir)
            match emit "on", emit "0" with
            | Ok ll, Ok old ->
                let audit label (s: string) =
                    let matches pattern = System.Text.RegularExpressions.Regex.Matches(s, pattern)
                    let pools =
                        [ for m in matches @"(%r\d+) = call ptr @blade_alloc_cells\(i64 (\d+), i64 (\d+)\)" do
                            yield (m.Groups.[1].Value, m.Groups.[2].Value + "x" + m.Groups.[3].Value) ]
                    let layouts = pools |> List.map snd
                    let expected = if demanded || label = "OFF" then ["6x8"; "6x8"; "2x8"] else ["6x8"; "2x8"]
                    let calls = (matches @"call ptr @blade_alloc_cells\(").Count
                    let description = $"{label}: calls={calls}, layouts={layouts}"
                    // Limit dataflow checks to evaluation, before printing can
                    // introduce unrelated pool reads. Follow both a direct
                    // flattened subscript and a row-view GEP then cell GEP.
                    let endOfEvaluation = s.IndexOf("  call void @blade_print_completed", System.StringComparison.Ordinal)
                    let evaluation = if endOfEvaluation < 0 then "" else s.Substring(0, endOfEvaluation)
                    let dynamicAccess root operation =
                        let mutable pointers = Set.singleton root
                        for m in System.Text.RegularExpressions.Regex.Matches(evaluation,
                                     @"(%r\d+) = getelementptr inbounds double, ptr (%r\d+), i64 %r\d+") do
                            if pointers.Contains m.Groups.[2].Value then pointers <- pointers.Add m.Groups.[1].Value
                        pointers.Remove root |> Seq.exists (fun p ->
                            System.Text.RegularExpressions.Regex.IsMatch(evaluation,
                                operation + System.Text.RegularExpressions.Regex.Escape(p) + @"(?:\r?\n|$)"))
                    let dataflow =
                        match pools with
                        | (input, _) :: rest when not rest.IsEmpty ->
                            let output = rest |> List.last |> fst
                            dynamicAccess input @"= load double, ptr "
                            && dynamicAccess output @"store double %r\d+, ptr "
                            && (pools.Length <> 3 || (dynamicAccess (fst rest.Head) @"store double %r\d+, ptr "
                                                 && dynamicAccess (fst rest.Head) @"= load double, ptr "))
                        | _ -> false
                    let printed text scalar =
                        s.Split('\n') |> Array.tryFind (fun line -> line.Contains ($"c\"{text}\\00\""))
                        |> Option.exists (fun line ->
                            let constant = line.Substring(0, line.IndexOf(" =", System.StringComparison.Ordinal))
                            let call = if scalar then "call void @blade_print_f64(ptr " else "call void @blade_out_str(ptr "
                            s.Contains (call + constant))
                    [ ($"exact required pools ({description})", calls = expected.Length && layouts = expected);
                      ($"only main defines storage ({description})", (matches @"(?m)^define ").Count = 1 && s.Contains "define i32 @main()");
                      ($"M -> map/fold -> rows dataflow ({description})", dataflow && evaluation.Contains "fmul double"
                          && evaluation.Contains "fadd double" && evaluation.Contains "store double 0x401C000000000000");
                      ($"required prints ({description})", printed "M = [" false && printed "rows = [" false
                          && (not demanded || (printed "P = [" false && printed "sample" true)));
                      ($"serial graph ({description})", not (s.Contains "fadd reassoc") && not (s.Contains "fmul reassoc")) ]
                checkAll name ll "C02 streams ON; L1 restores the full row-input boundary OFF; demanded P stays stored"
                    (audit "ON" ll @ audit "OFF" old)
            | a, b -> resultLine Fail name ($"ON={a}; OFF={b}"); false
        [ route "partial_rows_llvm_C02_two_pools_vs_L1_corrected_three_pool_reference" partialRowsSrc false;
          route "partial_rows_llvm_demanded_P_keeps_three_required_allocations" partialRowsNamedSrc true ])

let private partialRowsMetadata () =
    let name = "partial_rows_preserves_result_R_and_constructs_concrete_C_metadata"
    withPartialRows (fun () ->
        match withGate "BLADE_FUSION" "on" (fun () -> lower partialRowsSrc),
              withGate "BLADE_FUSION" "0" (fun () -> lower partialRowsSrc) with
        | Ok ir, Ok old ->
            let m = ir.Modules.Head
            let source = m.Bindings |> List.find (fun b -> b.Name = "M")
            let rows = m.Bindings |> List.find (fun b -> b.Name = "rows")
            let oldRows = old.Modules.Head.Bindings |> List.find (fun b -> b.Name = "rows")
            let fresh = m.Functions |> List.tryFind (fun f -> f.Name.Contains "__partial_rows_")
            match source.Type, rows.Value, fresh with
            | Blade.IR.ArrayElem sourceAt, Blade.IR.IRCompute (Blade.IR.IRApplyCombinator outer), Some f ->
                let columnOk =
                    match f.Params.Head.Type with
                    | Blade.IR.ArrayElem rowAt -> rowAt.IndexTypes = [sourceAt.IndexTypes.[1]]
                    | _ -> false
                let mutable innerOk = false
                Blade.IR.iterIRExpr (function
                    | Blade.IR.IRReduceCompute (Blade.IR.IRApplyCombinator inner, _, _) ->
                        innerOk <- inner.KernelInputRanks = [0] && inner.SDimsPerArray = [1]
                                   && inner.ArrayTypes.Head.IndexTypes = [sourceAt.IndexTypes.[1]]
                    | _ -> ()) f.Body
                checkAll name "" "original output type unchanged; concrete C in row signature/inner map; outer C is a fiber"
                    [ "exact R result type", rows.Type = oldRows.Type && outer.OutputType = oldRows.Type
                      "concrete C row param", columnOk
                      "concrete C inner metadata", innerOk
                      "outer fiber", outer.KernelInputRanks = [1] && outer.ArrayTypes.Head.IndexTypes.[1].Kind = Blade.Types.TDimension
                      "fresh callable origin", m.DerivedFuncOrigins.ContainsKey f.Id ]
            | _ -> resultLine Fail name "the admitted outer/inner shape is missing"; false
        | a, b -> resultLine Fail name ($"ON={a}; OFF={b}"); false)

let private partialRowsFailureSrc =
    "function late_producer_failure(v: Float64) -> Float64 = {\n"
    + "    let z = Int64(floor(v - 6.0))\n    if v > 5.0 then Float64(1 / z) else v\n}\n"
    + "function early_fold_failure(a: Float64, b: Float64) -> Float64 = {\n"
    + "    let z = Int64(floor(b - b))\n    a + Float64(1 / z)\n}\n"
    + partialRowsSrc.Replace("lambda(v: Float64) -> v * v + 1.0, (+)", "late_producer_failure, early_fold_failure")

let private partialRowsSeedFailureSrc =
    "function row_seed_failure() -> Float64 = {\n    let z = 0\n    Float64(1 / z)\n}\n"
    + partialRowsFailureSrc.Replace("early_fold_failure, 7.0)", "(+), row_seed_failure())")

// L1: method_for's entire real input precedes a row kernel's first action.
// These are actual LLVM emissions, independently of the C++/interpreter gate.
let private partialRowsLlvmInputBoundary () =
    let prologue = partialRowsSrc.Substring(0, partialRowsSrc.IndexOf("let rows", System.StringComparison.Ordinal))
    let stored = prologue + "let rows = ((method_for(M) <@> (lambda(row: Array<Float64 like C>) -> reduce(row, (+), 7.0))) |> compute)\n"
    let packedStored =
        "type R = Idx<2>\ntype S = SymIdx<2, 3>\n"
        + "let M: Array<Float64 like R, S> = [[[1.0, 2.0, 3.0], [4.0, 5.0], [6.0]], [[7.0, 8.0, 9.0], [10.0, 11.0], [12.0]]]\n"
        + "let rows = ((method_for(M) <@> (lambda(row: Array<Float64 like S>) -> row(0, 0))) |> compute)\n"
    let shared =
        prologue + "let totals = {\n"
        + "    let P = method_for(M) <@> lambda(v: Float64) -> v * v + 1.0\n"
        + "    let first_rows = reduce(P, (+), 7.0)\n"
        + "    let second_rows = reduce(P, (+), -7.0)\n"
        + "    (reduce(first_rows, (+), 0.0), reduce(second_rows, (+), 0.0))\n}\n"
    let scalarMap = prologue + "let P = method_for(M) <@> lambda(v: Float64) -> v * v + 1.0 |> compute\n"
    let scalarTerminal =
        "let x = [1.0, 2.0, 3.0]\nlet total = reduce(method_for(x) <@> lambda(v: Float64) -> v * v + 1.0, (+), 7.0)\n"
    // An unannotated full rank-2 fold takes TypeCheckInfer.rankKDesugar:
    // bind the anonymous matrix, then index it in a serial IRForRange nest.
    // This pre-existing topology is distinct from a direct scalar terminal.
    let rankTwoMaterialized = partialRowsSrc.Replace("let rows =", "let total =").Replace("(+), 7.0)", "(+), 7.0, axes = 2)")
    let virtuals =
        "type I = Idx<3>\nlet forward = method_for(range<I>) <@> lambda(i: Int64) -> Float64(i) |> compute\n"
        + "let backward = method_for(reverse<I>) <@> lambda(i: Int64) -> Float64(i) |> compute\n"
    let virtualMulti =
        "type R = Idx<2>\ntype C = Idx<3>\n"
        + "let grid = method_for(range<R, C>) <@> lambda(i: Int64, j: Int64) -> Float64(i + j) |> compute\n"
    let layouts (ll: string) =
        [ for m in System.Text.RegularExpressions.Regex.Matches(ll, @"call ptr @blade_alloc_cells\(i64 (\d+), i64 (\d+)\)") do
            yield m.Groups.[1].Value + "x" + m.Groups.[2].Value ]
    let emit name src =
        match lower src with
        | Error e -> Error e
        | Ok ir -> EmitLlvm.tryEmitProgramNamed name ir |> Result.map (fun ll -> ll, ir)
    let controls: (string * string * string list) list =
        [ ("stored_row_input_no_copy", stored, ["6x8"; "2x8"]);
          ("packed_record_row_has_two_axes_one_input_rank", packedStored, ["12x8"; "2x8"]);
          ("shared_named_input_forced_once", shared, ["6x8"; "6x8"; "2x8"; "2x8"]);
          ("scalar_input_map_unchanged", scalarMap, ["6x8"; "6x8"]);
          ("rank1_direct_scalar_terminal_unchanged", scalarTerminal, ["3x8"]);
          ("rank2_full_fold_materialized_desugar", rankTwoMaterialized, ["6x8"; "6x8"]);
          ("virtual_scalar_range_and_reverse_unchanged", virtuals, ["3x8"; "3x8"]);
          ("virtual_multislot_range_unchanged", virtualMulti, ["6x8"]) ]
    [ for gate in ["on"; "0"] do
        for suffix, src, expected in controls do
            let name = $"partial_rows_L1_llvm_{suffix}_{gate}"
            yield withPartialRows (fun () -> withGate "BLADE_FUSION" gate (fun () ->
                match emit name src with
                | Error e -> resultLine Fail name e; false
                | Ok (ll, ir) ->
                    let actual = layouts ll
                    let privateOutput = suffix <> "shared_named_input_forced_once" || (not (ll.Contains "P = [") && ll.Contains "totals")
                    let mutable fused = false
                    let mutable serialNest = false
                    let mapped = System.Collections.Generic.HashSet<int>()
                    let indexed = System.Collections.Generic.HashSet<int>()
                    let noteMapped id (info: Blade.IR.ApplyInfo) =
                        match info.OutputType with
                        | Blade.IR.ArrayElem at when (at.IndexTypes |> List.sumBy _.Rank) = 2 -> mapped.Add id |> ignore
                        | _ -> ()
                    for m in ir.Modules do
                        for b in m.Bindings do
                            match b.Value with
                            | Blade.IR.IRApplyCombinator info | Blade.IR.IRCompute (Blade.IR.IRApplyCombinator info) -> noteMapped b.Id info
                            | _ -> ()
                            Blade.IR.iterIRExpr (function
                                | Blade.IR.IRReduceCompute _ -> fused <- true
                                | Blade.IR.IRForRange _ -> serialNest <- true
                                | Blade.IR.IRLet (id, Blade.IR.IRApplyCombinator info, _)
                                | Blade.IR.IRLet (id, Blade.IR.IRCompute (Blade.IR.IRApplyCombinator info), _) -> noteMapped id info
                                | Blade.IR.IRIndex (Blade.IR.IRVar (id, _), _, _) -> indexed.Add id |> ignore
                                | _ -> ()) b.Value
                    let topology =
                        if suffix = "rank1_direct_scalar_terminal_unchanged" then fused && not serialNest
                        elif suffix = "rank2_full_fold_materialized_desugar" then serialNest && not fused && mapped.Overlaps indexed
                        else true
                    if actual = expected && privateOutput && topology then
                        resultLine Pass name ($"pool layouts={actual}; topology verified; no extra row-input copy"); true
                    else
                        resultLine Fail name ($"expected={expected}, actual={actual}, private output={privateOutput}, topology={topology}, IRReduceCompute={fused}, IRForRange={serialNest}, mapped IDs={mapped.Count}, indexed IDs={indexed.Count}"); false))
        for suffix, src in [ ("producer_before_fold", partialRowsFailureSrc); ("producer_before_seed", partialRowsSeedFailureSrc) ] do
            let name = $"partial_rows_L1_llvm_{suffix}_{gate}"
            yield withPartialRows (fun () -> withGate "BLADE_FUSION" gate (fun () ->
                match emit name src with
                | Error e -> resultLine Fail name e; false
                | Ok (ll, _) ->
                    let actual = layouts ll
                    let mainAt = ll.IndexOf("define i32 @main()", System.StringComparison.Ordinal)
                    let main = if mainAt < 0 then "" else ll.Substring mainAt
                    let producer = System.Text.RegularExpressions.Regex.Match(main, @"call double @bl_late_producer_failure_\d+\(")
                    let rows = System.Text.RegularExpressions.Regex.Match(main, @"call ptr @blade_alloc_cells\(i64 2, i64 8\)")
                    let fillEnd =
                        if not producer.Success || not rows.Success || producer.Index >= rows.Index then ""
                        else main.Substring(producer.Index, rows.Index - producer.Index)
                    checkAll name ll ($"actual pools={actual}; full producer fill must precede row allocation/seed/fold")
                        [ ($"three pools (actual={actual})", actual = ["6x8"; "6x8"; "2x8"]);
                          ("producer fill before rows", producer.Success && rows.Success && producer.Index < rows.Index
                              && fillEnd.Contains "store double" && fillEnd.Contains "br label %loop.cond" && fillEnd.Contains "loop.end") ])) ]

let private partialRowsLlvmMalformedPeel () =
    withPartialRows (fun () -> withGate "BLADE_FUSION" "0" (fun () ->
        let cases =
            [ ("real_row_rank", partialRowsSrc, 1, 0);
              ("negative_row_rank", partialRowsSrc, 1, -1);
              ("virtual_row_rank", "type I = Idx<3>\nlet y = method_for(range<I>) <@> lambda(i: Int64) -> Float64(i) |> compute\n", 0, 1) ]
        [ for suffix, src, originalRank, badRank in cases do
            let name = $"partial_rows_L1_llvm_declines_malformed_{suffix}"
            match lower src with
            | Error e -> resultLine Fail name e; yield false
            | Ok ir ->
                let mutable changed = false
                let alter e =
                    match e with
                    | Blade.IR.IRApplyCombinator info when not changed && info.KernelInputRanks = [originalRank] ->
                        changed <- true
                        Blade.IR.IRApplyCombinator { info with KernelInputRanks = [badRank] }
                    | _ -> e
                let alteredModules =
                    ir.Modules
                    |> List.map (fun (m: Blade.IR.IRModule) ->
                        let bindings =
                            m.Bindings
                            |> List.map (fun b -> { b with Value = Blade.IR.mapIRExpr alter b.Value })
                        { m with Bindings = bindings })
                let altered = { ir with Modules = alteredModules }
                match EmitLlvm.tryEmitProgramNamed name altered with
                | Error why when changed && (why.Contains "input rank" || why.Contains "array-valued kernel argument") ->
                    resultLine Pass name why; yield true
                | other -> resultLine Fail name ($"changed={changed}, emission={other}"); yield false ]))

let private partialRowsInterpreterCases () =
    let cases =
        [ "sum", partialRowsSrc, "[24.0, 87.0]", true
          "ordered", partialRowsSrc.Replace("(+), 7.0", "lambda(a, b) -> a * 2.0 + b, 7.0"), "[84.0, 213.0]", true
          "total_block_and_callee",
              "function square_plus(v: Float64) -> Float64 = {\n    let w = v * v\n    w + 1.0\n}\n"
              + partialRowsSrc.Replace("v * v + 1.0, (+)", "square_plus(v), (+)"), "[24.0, 87.0]", true
          "zero_columns", partialRowsSrc.Replace("Idx<3>", "Idx<0>").Replace("[[1.0, 2.0, 3.0], [4.0, 5.0, 6.0]]", "[[], []]").Replace("7.0)", "-7.25)"), "[-7.25, -7.25]", true
          "zero_rows", partialRowsSrc.Replace("Idx<2>", "Idx<0>").Replace("[[1.0, 2.0, 3.0], [4.0, 5.0, 6.0]]", "[]"), "[]", true
          "cancellation", partialRowsSrc.Replace("[[1.0, 2.0, 3.0], [4.0, 5.0, 6.0]]", "[[1e16, 1.0, -1e16], [1e16, -1e16, 1.0]]").Replace("v * v + 1.0", "v * 1.0").Replace("7.0)", "0.0)"), "[0.0, 1.0]", true
          "negative_zero_seed", partialRowsSrc.Replace("(+), 7.0", "lambda(a, b) -> a, -0.0"), "[-0.0, -0.0]", true
          "paired_failure", partialRowsFailureSrc, "late_producer_failure", false
          "producer_before_row_seed", partialRowsSeedFailureSrc, "late_producer_failure", false ]
    [ for suffix, src, expected, admitted in cases do
        let name = $"partial_rows_interp_on_off_{suffix}"
        yield withPartialRows (fun () ->
            let run gate = withGate "BLADE_FUSION" gate (fun () ->
                match lower src with
                | Error e -> Error e
                | Ok ir ->
                    match IRValidate.validateIR ir with
                    | Error es -> Error (String.concat "; " es)
                    | Ok ir ->
                        let routed = ir.Modules |> List.exists (fun m -> m.Functions |> List.exists (fun f -> f.Name.Contains "__partial_rows_"))
                        Ok (routed, Blade.Interp.Run.runProgram ir name Blade.Interp.Value.defaultLimits))
            match run "on", run "0" with
            | Ok (route, on), Ok (false, off) when route = admitted && on.ExitCode = off.ExitCode
                                                   && on.Stdout = off.Stdout && on.Stderr = off.Stderr ->
                let output = on.Stdout + on.Stderr
                let good =
                    if admitted then on.ExitCode = 0 && output.Contains ($"rows = {expected}")
                    else on.ExitCode <> 0 && output.Contains "BL8013" && output.Contains expected
                         && not (output.Contains "early_fold_failure")
                if good then resultLine Pass name "actual route/refusal agrees with original IR and independent result/failure pin"; true
                else resultLine Fail name output; false
            | a, b -> resultLine Fail name ($"ON={a}; OFF={b}"); false) ]

let private partialRowsRefusals () =
    let named = partialRowsNamedSrc
    let cases =
        [ "named_printed_keyed", named
          "named_deferred", named.Replace(" |> compute", "").Replace("\nlet sample = P(1, 2)", "")
          "producer_capture", "let bias = 1.0\n" + partialRowsSrc.Replace("v * v + 1.0", "v * v + bias")
          "fold_capture", "let bias = 1.0\n" + partialRowsSrc.Replace("(+), 7.0", "lambda(a, b) -> a + b + bias, 7.0")
          "may_fail_producer", partialRowsSrc.Replace("v * v + 1.0", "lgamma(v)")
          "may_fail_fold", partialRowsSrc.Replace("(+), 7.0", "lambda(a, b) -> a + lgamma(b), 7.0")
          "nonliteral_seed", "let seed = 7.0\n" + partialRowsSrc.Replace("7.0)", "seed)")
          "may_fail_seed", "function risky_seed() -> Float64 = {\n    let z = 0\n    Float64(1 / z)\n}\n"
              + partialRowsSrc.Replace("7.0)", "risky_seed())")
          "parallel_producer", partialRowsSrc.Replace("lambda(v: Float64) ->", "lambda(v: Float64) where omp ->")
          "parallel_fold", partialRowsSrc.Replace("(+), 7.0", "lambda(a, b) where comm(a, b), omp -> a + b, 7.0")
          "private_alias_observer", partialRowsSrc.Replace("let rows = reduce(method_for(M) <@> lambda(v: Float64) -> v * v + 1.0, (+), 7.0)",
              "let rows = {\n    let P = method_for(M) <@> lambda(v: Float64) -> v * v + 1.0\n    let alias = P\n    let sample = alias(0, 0)\n    reduce(P, (+), 7.0)\n}")
          "hidden_writer", partialRowsSrc.Replace("let M:",
              "function bump(a: mut Array<Float64 like R, C>) -> Float64 = {\n    a(0, 0) += 1.0\n    a(0, 0)\n}\nlet mut M:")
              .Replace("v * v + 1.0", "{ let changed = bump(M)\n v + changed }")
          "paired_failure", partialRowsFailureSrc ]
    [ for suffix, src in cases do
        let name = $"partial_rows_declines_{suffix}"
        yield withPartialRows (fun () ->
            decisionCase name src "partial-row-fold" (fun d -> match d.Outcome with Blade.Effects.Declined _ -> true | _ -> false)
                "materialized route retained")
      for suffix, key, value in [ "optimizer_off", "BLADE_FUSION", "0"; "licensed_graph", "BLADE_FP_REPRO", "0" ] do
        let name = $"partial_rows_declines_{suffix}"
        yield withPartialRows (fun () -> withGate key value (fun () ->
            decisionCase name partialRowsSrc "partial-row-fold" (fun d -> match d.Outcome with Blade.Effects.Declined _ -> true | _ -> false)
                "the gate preserves the original route"))
      yield withPartialRows (fun () ->
          let name = "partial_rows_demanded_control_keeps_P_allocation_print_and_keyed_read"
          match cppOfSource name named with
          | Error e -> resultLine Fail name e; false
          | Ok cpp -> checkAll name cpp "demanded P remains a materialized observable output"
                          [ "P allocation", cpp.Contains "promote<double, 2>::type, nullptr>(P_extents)"
                            "P print", cpp.Contains "\"P = [\""
                            "no partial terminal route", not (cpp.Contains "__partial_rows_") ]) ]

/// Explicit IR payload probes isolate references source sugar can erase.
/// No backend evaluates these deliberately altered refusal candidates.
let private partialRowsHiddenPayloads () =
    withPartialRows (fun () ->
        match withGate "BLADE_FUSION" "0" (fun () -> lower partialRowsSrc) with
        | Error e -> resultLine Fail "partial_rows_hidden_payload_setup" e; [false]
        | Ok ir ->
            let m = ir.Modules.Head
            let host = m.Bindings |> List.find (fun b -> b.Name = "rows")
            match host.Value with
            | Blade.IR.IRLet (pid, Blade.IR.IRApplyCombinator producer, body) ->
                let source = m.Bindings |> List.find (fun b -> b.Name = "M")
                let sourceAt = match source.Type with Blade.IR.ArrayElem a -> a | _ -> failwith "M is no array"
                let kernelId = match producer.Kernel with Blade.IR.IRVar (id, _) -> id | _ -> failwith "map has no callable"
                let kernel = m.Functions |> List.find (fun f -> f.Id = kernelId)
                let hiddenIx = { sourceAt.IndexTypes.Head with Extent = Blade.IR.IRExtent (Blade.IR.IRVar (pid, producer.OutputType), 0) }
                let typeDemand = { m with Types = m.Types @ [ Blade.IR.IRTDIndexType ("HiddenDemand", hiddenIx) ] }
                let capture: Blade.IR.CaptureInfo = { Id = pid; Name = "hidden"; Type = producer.OutputType; IsMutable = false }
                let captureDemand = { m with Functions = m.Functions @ [ { kernel with Id = 990007; Name = "hidden_capture"; Captures = [capture] } ] }
                let unknownCall =
                    Blade.IR.IRApp (Blade.IR.IRVar (990009, Blade.IR.mkFuncArrow [Blade.Types.IRTScalar Blade.Types.ETFloat64] kernel.RetType),
                                    [ Blade.IR.IRVar (kernel.Params.Head.VarId, kernel.Params.Head.Type) ], kernel.RetType)
                let unknown = { m with Functions = m.Functions |> List.map (fun f -> if f.Id = kernelId then { f with Body = unknownCall } else f) }
                let structuredAt = { sourceAt with IndexTypes = [ { sourceAt.IndexTypes.Head with IxKind = Blade.Types.IxKRagged }; sourceAt.IndexTypes.[1] ] }
                let structuredValue =
                    Blade.IR.mapIRExpr (function
                        | Blade.IR.IRVar (id, _) when id = source.Id -> Blade.IR.IRVar (id, Blade.IR.mkArrayLike structuredAt)
                        | e -> e) host.Value
                let structured = { m with Bindings = m.Bindings |> List.map (fun b -> if b.Id = host.Id then { b with Value = structuredValue } else b) }
                // Even reference-equal expression trees in OTHER bindings
                // remain demands; only the actual host may be subtracted.
                let sharedExpression = { m with Bindings = m.Bindings @ [ { host with Id = 990010; Name = "other_host" } ] }
                let hiddenMapMetadata =
                    let at = { producer.ArrayTypes.Head with IndexTypes = [hiddenIx; sourceAt.IndexTypes.[1]] }
                    let value = Blade.IR.IRLet (pid, Blade.IR.IRApplyCombinator { producer with ArrayTypes = [at] }, body)
                    { m with Bindings = m.Bindings |> List.map (fun b -> if b.Id = host.Id then { b with Value = value } else b) }
                let alteredProducer change =
                    let value = Blade.IR.IRLet (pid, Blade.IR.IRApplyCombinator (change producer), body)
                    { m with Bindings = m.Bindings |> List.map (fun b -> if b.Id = host.Id then { b with Value = value } else b) }
                let sharedAxis = alteredProducer (fun p -> { p with SharedIndexTypes = [hiddenIx] })
                let kernelAxis = alteredProducer (fun p -> { p with KernelTDims = [hiddenIx] })
                let methodAxis = alteredProducer (fun p ->
                    match p.Loop with
                    | Blade.IR.IRMethodFor mf -> { p with Loop = Blade.IR.IRMethodFor { mf with SharedIndexTypes = [hiddenIx] } }
                    | _ -> failwith "map has no method_for")
                let hostSidePayload = { m with SparseInits = m.SparseInits.Add(host.Id, Blade.IR.IRLit (Blade.IR.IRLitFloat 1.0)) }
                let witnesses: (string * Blade.IR.IRModule) list =
                    [ ("declared_type", typeDemand);
                      ("callable_capture", captureDemand);
                      ("unknown_callee", unknown);
                      ("structured_source", structured);
                      ("other_binding_shared_expression", sharedExpression);
                      ("apply_metadata", hiddenMapMetadata);
                      ("shared_axis", sharedAxis);
                      ("kernel_axis", kernelAxis);
                      ("method_for_axis", methodAxis);
                      ("host_execution_payload", hostSidePayload) ]
                [ for suffix, md in witnesses do
                    let name = $"partial_rows_hidden_payload_{suffix}_retains_private_store"
                    Blade.Effects.Decisions.start ()
                    let kept = Optimize.fusePartialRowTerminalsModule (Blade.IR.IRBuilder ()) md.Functions md
                    let decisions = Blade.Effects.Decisions.drain ()
                    let stillPrivate =
                        match (kept.Bindings |> List.find (fun b -> b.Id = host.Id)).Value with
                        | Blade.IR.IRLet (id, _, _) -> id = pid
                        | _ -> false
                    let declined = decisions |> List.exists (fun d -> d.Rule = "partial-row-fold" && not (applied d))
                    if stillPrivate && declined then resultLine Pass name "the complete census/legality proof refuses the altered payload"; yield true
                    else resultLine Fail name "a hidden demand or unsupported callable/renderer was admitted"; yield false ]
            | _ -> resultLine Fail "partial_rows_hidden_payload_setup" "the unoptimized scoped producer shape changed"; [false])

// C04: the source remains required; an anonymous hard transpose is consumed
// only by a scalar seeded partial fold. No row-valued virtual input is added.
let private transposeRowsSrc =
    "type R = Idx<2>\ntype C = Idx<3>\n"
    + "let M: Array<Float64 like R, C> = [[1.0, 2.0, 3.0], [4.0, 5.0, 6.0]]\n"
    + "let rows: Array<Float64 like C> = reduce(transpose(M, [0, 1]), lambda(a: Float64, b: Float64) -> a * 10.0 + b, 7.0)\n"

let private transposeRowsNamedSrc =
    transposeRowsSrc.Replace("let rows:", "let T = transpose(M, [0, 1])\nlet rows:")
                    .Replace("reduce(transpose(M, [0, 1]),", "reduce(T,")
    + "let sample = T(1, 0)\n"

let private withTransposeRows f =
    withPartialRows (fun () -> withGate "BLADE_FUSION" "on" (fun () ->
        withGate "BLADE_BLAS" "off" (fun () -> withGate "BLADE_OMP_THREADS" "1" f)))

let private transposeRowRoute (ir: IR.IRProgram) =
    ir.Modules |> List.exists (fun m -> m.Functions |> List.exists (fun f -> f.Name.StartsWith "__transpose_rows_"))

// C04-I1 exercises the literal constructor directly, without relying on an
// optimizer to avoid the empty input. The existing source ON/OFF pins below
// independently check the original transpose/fold route and exact seed bits.
let private transposeRowsEmptyLiteralShape () =
    let elemTy = Blade.Types.IRTScalar Blade.Types.ETFloat64
    let axis id n : IR.IRIndexType =
        { Id = id
          Rank = 1
          Extent = IR.IRLit (IR.IRLitInt n)
          Symmetry = Blade.Types.SymNone
          Tag = Some ($"I{id}")
          Kind = Blade.Types.SDimension
          IxKind = Blade.Types.IxKPlain
          Dependencies = [] }
    let arrayType indices : IR.IRArrayType =
        { ElemType = elemTy
          IndexTypes = indices
          IsVirtual = false
          Identity = None }
    let literal indices values = Blade.Interp.ArrayOps.arrayLitFromValues (arrayType indices) values
    let rec cells store =
        match store with
        | Blade.Interp.Value.SFloat a -> a.Length
        | Blade.Interp.Value.SInt a -> a.Length
        | Blade.Interp.Value.SComplex a -> a.Length
        | Blade.Interp.Value.SBool a -> a.Length
        | Blade.Interp.Value.SObj a -> a.Length
        | Blade.Interp.Value.SNested rows -> rows |> Array.sumBy cells
        | Blade.Interp.Value.SRagged (rows, _, _) -> rows |> Array.sumBy cells
    let emptyNested store =
        match store with
        | Blade.Interp.Value.SNested rows -> rows.Length = 0
        | _ -> false
    let shapeCase suffix indices expected d1 d2 transposed =
        let name = "transpose_rows_I1_empty_literal_" + suffix
        try
            let a = literal indices []
            let t = Blade.Interp.ArrayOps.transposeArray a d1 d2
            checkAll name "" (sprintf "literal=%A; transpose=%A" a.Extents t.Extents)
                [ ("declared rank/extents and exact nominal indices", a.Extents = expected && a.IndexTypes = indices && a.ElemType = elemTy);
                  ("zero cell backing and no exemplar row", emptyNested a.Data && cells a.Data = 0);
                  ("transpose preserves empty shape without a cell read", t.Extents = transposed && cells t.Data = 0) ]
        with ex -> resultLine Fail name ex.Message; false
    let r0 = axis 991001 0L
    let c3 = axis 991002 3L
    let k2 = axis 991003 2L
    let rank1Empty = literal [r0] []
    let row = literal [k2] [Blade.Interp.Value.VFloat 1.0; Blade.Interp.Value.VFloat 2.0]
    let one = literal [axis 991004 1L] [Blade.Interp.Value.VFloat 3.0]
    let r2 = axis 991005 2L
    let zeroInner = literal [r2; r0] [Blade.Interp.Value.VArray rank1Empty; Blade.Interp.Value.VArray rank1Empty]
    let nonempty = literal [r2; k2] [Blade.Interp.Value.VArray row; Blade.Interp.Value.VArray row]
    let excluded =
        [ ("ragged", [r0; { c3 with IxKind = Blade.Types.IxKRagged }]);
          ("compact", [{ r0 with Rank = 2; Symmetry = Blade.Types.SymSymmetric }; c3]);
          ("dependent", [r0; { c3 with Dependencies = [r0.Id] }]);
          ("unresolved", [r0; { c3 with Extent = IR.IRVar (991006, Blade.Types.IRTScalar Blade.Types.ETInt64) }]);
          ("negative", [r0; axis 991007 (-1L)]);
          ("outside_managed_extent", [r0; axis 991008 (int64 System.Int32.MaxValue + 1L)]);
          ("leading_nonzero", [r2; c3]) ]
    let raggedAxes = [r2; { c3 with IxKind = Blade.Types.IxKRagged }]
    let ragged = literal raggedAxes [Blade.Interp.Value.VArray row; Blade.Interp.Value.VArray one]
    let compactAxes = [{ r2 with Rank = 2; Symmetry = Blade.Types.SymSymmetric }]
    let compact = literal compactAxes [Blade.Interp.Value.VArray row; Blade.Interp.Value.VArray one]
    let raggedBacking =
        match ragged.Data with
        | Blade.Interp.Value.SRagged (_, lens, offsets) ->
            lens = [|2L; 1L|] && offsets = [|0L; 2L; 3L|] && cells ragged.Data = 3
        | _ -> false
    let compactBacking =
        match compact.Data with
        | Blade.Interp.Value.SNested rows -> rows.Length = 2 && cells compact.Data = 3
        | _ -> false
    [ shapeCase "rank2_0_3" [r0; c3] [|0L; 3L|] 0 1 [|3L; 0L|]
      shapeCase "rank3_0_3_2" [r0; c3; k2] [|0L; 3L; 2L|] 0 2 [|2L; 3L; 0L|]
      checkAll "transpose_rows_I1_rank1_unchanged" "" "rank1 remains flat, with its original metadata and values"
          [ ("empty rank1 flat store", rank1Empty.Extents = [|0L|] && rank1Empty.IndexTypes = [r0]
              && (match rank1Empty.Data with Blade.Interp.Value.SFloat a -> a.Length = 0 | _ -> false));
            ("nonempty rank1 values", row.Extents = [|2L|] && row.IndexTypes = [k2]
              && (match row.Data with Blade.Interp.Value.SFloat a -> a = [|1.0; 2.0|] | _ -> false)) ]
      checkAll "transpose_rows_I1_rectangular_existing_rows_unchanged" "" "nonempty exemplar rows retain the existing literal route"
          [ ("zero inner axis", zeroInner.Extents = [|2L; 0L|] && zeroInner.IndexTypes = [r2; r0] && cells zeroInner.Data = 0);
            ("nonempty matrix", nonempty.Extents = [|2L; 2L|] && nonempty.IndexTypes = [r2; k2] && cells nonempty.Data = 4) ]
      for suffix, indices in excluded do
          let a = literal indices []
          checkAll ("transpose_rows_I1_excluded_empty_" + suffix) "" "the narrow repair does not reinterpret excluded metadata"
              [ ("previous flat empty route retained", a.Extents = [|0L|] && a.IndexTypes = indices
                  && (match a.Data with Blade.Interp.Value.SFloat cells -> cells.Length = 0 | _ -> false)) ]
      checkAll "transpose_rows_I1_nonempty_ragged_route_unchanged" "" "ragged rows keep CSR lengths and offsets"
          [ ("ragged shape and metadata", ragged.Extents = [|2L; 2L|] && ragged.IndexTypes = raggedAxes);
            ("CSR backing", raggedBacking) ]
      checkAll "transpose_rows_I1_nonempty_compact_route_unchanged" "" "compact rows keep the declared grouped extent"
          [ ("compact shape and metadata", compact.Extents = [|2L; 2L|] && compact.IndexTypes = compactAxes);
            ("compact nested backing", compactBacking) ] ]

let private transposeRowsEmission () =
    let name = "transpose_rows_cpp_llvm_remove_only_private_transpose"
    withTransposeRows (fun () ->
        let emit src gate = withGate "BLADE_FUSION" gate (fun () ->
            match lower src with
            | Error e -> Error e
            | Ok ir -> EmitLlvm.tryEmitProgramNamed name ir |> Result.map (fun ll -> transposeRowRoute ir, fst (CodeGen.genSelfContainedProgramFromIR ir name), ll))
        match emit transposeRowsSrc "on", emit transposeRowsSrc "0", emit transposeRowsNamedSrc "on" with
        | Ok (routed, cpp, ll), Ok (oldRoute, oldCpp, oldLl), Ok (namedRoute, namedCpp, namedLl) ->
            let rank2 (s: string) = System.Text.RegularExpressions.Regex.Matches(s, @"allocate<typename promote<double, 2>::type, nullptr>").Count
            let layouts (s: string) =
                [ for m in System.Text.RegularExpressions.Regex.Matches(s, @"call ptr @blade_alloc_cells\(i64 (\d+), i64 (\d+)\)") ->
                    m.Groups.[1].Value + "x" + m.Groups.[2].Value ]
            let allocs (s: string) = System.Text.RegularExpressions.Regex.Matches(s, @"call ptr @blade_alloc_cells\(").Count
            let description = $"CPP rank2={rank2 cpp}/{rank2 oldCpp}/{rank2 namedCpp}; LLVM layouts={layouts ll}/{layouts oldLl}/{layouts namedLl}"
            let evaluation =
                let endAt = ll.IndexOf("  call void @blade_print_completed", System.StringComparison.Ordinal)
                if endAt < 0 then "" else ll.Substring(0, endAt)
            let inputPool = System.Text.RegularExpressions.Regex.Match(evaluation, @"(%r\d+) = call ptr @blade_alloc_cells\(i64 6, i64 8\)")
            let inputRead =
                inputPool.Success && System.Text.RegularExpressions.Regex.IsMatch(evaluation,
                    @"getelementptr inbounds double, ptr " + System.Text.RegularExpressions.Regex.Escape(inputPool.Groups.[1].Value) + @", i64 %r\d+")
            let cppFunctions =
                let endAt = cpp.IndexOf("int main", System.StringComparison.Ordinal)
                if endAt < 0 then "" else cpp.Substring(0, endAt)
            checkAll name ll description
                [ ("actual route, not materialized fallback", routed && not oldRoute && not namedRoute);
                  ("only required rank-2 M remains: " + description, rank2 cpp = 1 && rank2 oldCpp = 2 && rank2 namedCpp = 2);
                  ("no transpose copy ON", not (cpp.Contains "// transpose: hard") && oldCpp.Contains "// transpose: hard");
                  ("LLVM exact allocation layouts: " + description, layouts ll = ["6x8"; "3x8"] && allocs ll = 2
                      && layouts oldLl = ["6x8"; "6x8"; "3x8"] && allocs oldLl = 3
                      && layouts namedLl = ["6x8"; "6x8"; "3x8"] && allocs namedLl = 3);
                  ("LLVM strided source dataflow", inputRead && evaluation.Contains "mul i64" && evaluation.Contains ", 3"
                      && System.Text.RegularExpressions.Regex.IsMatch(evaluation, @"icmp slt i64 %r\d+, 2\b")
                      && System.Text.RegularExpressions.Regex.IsMatch(evaluation, @"icmp slt i64 %r\d+, 3\b")
                      && evaluation.Contains "store double 0x401C000000000000" && evaluation.Contains "fmul double" && evaluation.Contains "fadd double");
                  ("C++ scalar source read in the generated callable", System.Text.RegularExpressions.Regex.IsMatch(cppFunctions, @"M\[[^\]]+\]\[[^\]]+\]"));
                  ("required input and result print", cpp.Contains "\"M = [\"" && cpp.Contains "\"rows = [\""
                      && ll.Contains "M = [" && ll.Contains "rows = [");
                  ("named output print retained", namedCpp.Contains "\"T = [\"" && namedLl.Contains "T = [");
                  ("no LLVM reassociation", not (ll.Contains "fadd reassoc") && not (ll.Contains "fmul reassoc")) ]
        | a, b, c -> resultLine Fail name ($"ON={a}; OFF={b}; demanded={c}"); false)

let private transposeRowsMetadataAndInterface () =
    let name = "transpose_rows_complete_census_entry_and_concrete_R_C_metadata"
    withTransposeRows (fun () ->
        match withGate "BLADE_FUSION" "0" (fun () -> lower transposeRowsSrc) with
        | Error e -> resultLine Fail name e; false
        | Ok ir ->
            let m = ir.Modules.Head
            let host = m.Bindings |> List.find (fun b -> b.Name = "rows")
            let source = m.Bindings |> List.find (fun b -> b.Name = "M")
            let input = match source.Type with IR.ArrayElem a -> a | _ -> failwith "M is not rank two"
            let builder () =
                let b = IR.IRBuilder ()
                b.EnsureAtLeast 1000000
                b
            let defaultModule = Optimize.optimizeModule (builder ()) m.Functions m
            let changed = Optimize.optimizeModuleWithForeignCensus (builder ()) m.Functions Set.empty m
            let changedHost = changed.Bindings |> List.find (fun b -> b.Id = host.Id)
            let containsTranspose (md: IR.IRModule) =
                let mutable found = false
                for b in md.Bindings do IR.iterIRExpr (function IR.IRTranspose _ -> found <- true | _ -> ()) b.Value
                found
            let distinctNominalIds =
                match host.Type, host.Value with
                | IR.ArrayElem h, IR.IRLet (_, _, IR.IRCompute (IR.IRApplyCombinator original)) ->
                    match original.OutputType with
                    | IR.ArrayElem c -> h.IndexTypes.Head.Id <> c.IndexTypes.Head.Id
                                        && IRTotality.sameIndex h.IndexTypes.Head c.IndexTypes.Head
                    | _ -> false
                | _ -> false
            let shape =
                match changedHost.Value with
                | IR.IRCompute (IR.IRApplyCombinator outer) ->
                    let col = match outer.Kernel with IR.IRVar (id, _) -> changed.Functions |> List.find (fun f -> f.Id = id) | _ -> failwith "no column kernel"
                    match outer.Arrays, col.Body with
                    | [IR.IRRange ([c], None)], IR.IRReduceCompute (IR.IRApplyCombinator inner, _, seed) ->
                        let read = match inner.Kernel with IR.IRVar (id, _) -> changed.Functions |> List.find (fun f -> f.Id = id) | _ -> failwith "no gather kernel"
                        let origins = changed.DerivedFuncOrigins.ContainsKey col.Id && changed.DerivedFuncOrigins.ContainsKey read.Id
                        let serial (f: IR.IRCallable) = not f.IsCommutative && f.CommGroups.IsEmpty && not f.IsOmpParallel && f.Parallelism.IsEmpty
                        match inner.Arrays, read.Body with
                        | [IR.IRRange ([r], None)], IR.IRIndex (IR.IRVar (s, _), [IR.IRVar (rId, _); IR.IRVar (cId, _)], None) ->
                            s = source.Id && rId = read.Params.Head.VarId && cId = col.Params.Head.VarId
                            && IRTotality.sameIndex r input.IndexTypes.[0] && IRTotality.sameIndex c input.IndexTypes.[1]
                            && outer.OutputType = host.Type && outer.KernelInputRanks = [0] && inner.KernelInputRanks = [0]
                            && outer.ArrayTypes.Head.IsVirtual && inner.ArrayTypes.Head.IsVirtual
                            && seed = IR.IRLit (IR.IRLitFloat 7.0) && origins && serial col && serial read
                            && (read.Captures |> List.map _.Id |> Set.ofList) = Set.ofList [source.Id; col.Params.Head.VarId]
                            && (col.Captures |> List.map _.Id) = [source.Id]
                        | _ -> false
                    | _ -> false
                | _ -> false
            checkAll name "" "default entry retains transpose; explicit complete census permits exact strided idiom"
                [ ("default optimizeModule does not gain the arm", containsTranspose defaultModule);
                  ("explicit entry removes transpose", not (containsTranspose changed));
                  ("actual distinct numeric IDs share nominal C identity", distinctNominalIds);
                  ("output ID/name/type exact", changedHost.Id = host.Id && changedHost.Name = host.Name && changedHost.Type = host.Type);
                  ("concrete R/C ranges, indexed read, seed, captures, origins, serial metadata", shape) ])

let private transposeRowsFailureSrc =
    "function late_transpose_source(v: Float64) -> Float64 = {\n"
    + "    let z = Int64(floor(v - 6.0))\n    if v == 6.0 then Float64(1 / z) else v\n}\n"
    + "function early_transpose_fold(a: Float64, b: Float64) -> Float64 = {\n"
    + "    let z = Int64(floor(b - b))\n    a + Float64(1 / z)\n}\n"
    + transposeRowsSrc.Replace("let M:", "let A:").Replace("let rows:", "let M = method_for(A) <@> late_transpose_source\nlet rows:")
                      .Replace("lambda(a: Float64, b: Float64) -> a * 10.0 + b", "early_transpose_fold")

let private transposeRowsSeedFailureSrc =
    "function transpose_seed_failure() -> Float64 = {\n    let z = 0\n    Float64(1 / z)\n}\n"
    + transposeRowsSrc.Replace("7.0)", "transpose_seed_failure())")

let private transposeRowsInterpreterCases () =
    let cases =
        [ ("ordered", transposeRowsSrc, "[714.0, 725.0, 736.0]", true);
          ("R0", transposeRowsSrc.Replace("Idx<2>", "Idx<0>").Replace("[[1.0, 2.0, 3.0], [4.0, 5.0, 6.0]]", "[]").Replace("7.0)", "-7.25)"), "[-7.25, -7.25, -7.25]", true);
          ("C0", transposeRowsSrc.Replace("Idx<3>", "Idx<0>").Replace("[[1.0, 2.0, 3.0], [4.0, 5.0, 6.0]]", "[[], []]"), "[]", true);
          ("negative_zero", transposeRowsSrc.Replace("Idx<2>", "Idx<0>").Replace("[[1.0, 2.0, 3.0], [4.0, 5.0, 6.0]]", "[]").Replace("7.0)", "-0.0)"), "[-0.0, -0.0, -0.0]", true);
          ("cancellation", transposeRowsSrc.Replace("Idx<2>", "Idx<9>").Replace("Idx<3>", "Idx<2>").Replace("Idx<9>", "Idx<3>")
              .Replace("[[1.0, 2.0, 3.0], [4.0, 5.0, 6.0]]", "[[1e16, 1e16], [1.0, -1e16], [-1e16, 1.0]]").Replace("a * 10.0 + b", "a + b").Replace("7.0)", "0.0)"), "[0.0, 1.0]", true);
          ("stored_alias", transposeRowsSrc.Replace("let M:", "let storage:").Replace("let rows:", "let M = storage\nlet rows:"), "[714.0, 725.0, 736.0]", true);
          ("total_block", "function step(a: Float64, b: Float64) -> Float64 = {\n    let t = a * 10.0\n    t + b\n}\n"
              + transposeRowsSrc.Replace("lambda(a: Float64, b: Float64) -> a * 10.0 + b", "step"), "[714.0, 725.0, 736.0]", true);
          ("deferred_failure_first", transposeRowsFailureSrc, "late_transpose_source", false);
          ("seed_failure", transposeRowsSeedFailureSrc, "transpose_seed_failure", false) ]
    [ for suffix, src, expected, admitted in cases do
        let name = $"transpose_rows_interp_independent_on_off_{suffix}"
        yield withTransposeRows (fun () ->
            let run gate = withGate "BLADE_FUSION" gate (fun () ->
                match lower src with
                | Error e -> Error e
                | Ok ir ->
                    match IRValidate.validateIR ir with
                    | Error es -> Error (String.concat "; " es)
                    | Ok valid -> Ok (transposeRowRoute ir, Blade.Interp.Run.runProgram valid name Blade.Interp.Value.defaultLimits))
            match run "on", run "0" with
            | Ok (routed, on), Ok (false, off) ->
                let pin = if admitted then on.ExitCode = 0 && on.Stdout.Contains ("rows = " + expected)
                          else on.ExitCode <> 0 && on.Stderr.Contains expected && not (on.Stderr.Contains "early_transpose_fold")
                checkAll name on.Stdout "independent value/failure pin plus actual route/refusal and optimizer OFF comparison"
                    [ ("pin and admission", pin && routed = admitted);
                      ("same output/error/status", on.ExitCode = off.ExitCode && on.Stdout = off.Stdout && on.Stderr = off.Stderr) ]
            | a, b -> resultLine Fail name ($"ON={a}; OFF={b}"); false) ]

let private transposeRowsRefusals () =
    let scalar = transposeRowsSrc.Replace("let rows: Array<Float64 like C>", "let total: Float64").Replace("7.0)", "7.0, axes = 2)")
    let cases =
        [ ("named_demanded", transposeRowsNamedSrc);
          ("full_scalar", scalar);
          ("fold_capture", "let bias = 1.0\n" + transposeRowsSrc.Replace("a * 10.0 + b", "a * 10.0 + b + bias"));
          ("failing_fold", transposeRowsSrc.Replace("a * 10.0 + b", "a + lgamma(b)"));
          ("failing_seed", transposeRowsSeedFailureSrc);
          ("seed_capture", "let seed = 7.0\n" + transposeRowsSrc.Replace("7.0)", "seed)"));
          ("parallel_fold", transposeRowsSrc.Replace("lambda(a: Float64, b: Float64) -> a * 10.0 + b", "lambda(a: Float64, b: Float64) where comm(a, b), omp -> a + b"));
          ("deferred_source", transposeRowsSrc.Replace("let M:", "let A:").Replace("let rows:", "let M = method_for(A) <@> lambda(v: Float64) -> v + 1.0\nlet rows:"));
          ("deferred_failure", transposeRowsFailureSrc);
          ("alias_escape", transposeRowsSrc.Replace("reduce(transpose(M, [0, 1]),", "reduce({\n    let t = transpose(M, [0, 1])\n    let alias = t\n    alias\n},")) ]
    [ for suffix, src in cases do
        let name = $"transpose_rows_declines_{suffix}"
        yield withTransposeRows (fun () ->
            match lower src with
            | Error e -> resultLine Fail name e; false
            | Ok ir ->
                if not (transposeRowRoute ir) then resultLine Pass name "the unsupported/demanded source retains its original route"; true
                else resultLine Fail name "unsupported transpose storage was eliminated"; false)
      for suffix, gate, value in [ ("optimizer_off", "BLADE_FUSION", "0"); ("licensed_graph", "BLADE_FP_REPRO", "0") ] do
        yield withTransposeRows (fun () -> withGate gate value (fun () ->
            decisionCase ($"transpose_rows_declines_{suffix}") transposeRowsSrc "transpose-row-fold" (fun d -> not (applied d)) "gated arm keeps the original route")) ]

let private transposeRowsHiddenDemands () =
    withTransposeRows (fun () ->
        match withGate "BLADE_FUSION" "0" (fun () -> lower transposeRowsSrc) with
        | Error e -> resultLine Fail "transpose_rows_hidden_setup" e; [false]
        | Ok ir ->
            let m = ir.Modules.Head
            let host = m.Bindings |> List.find (fun b -> b.Name = "rows")
            let source = m.Bindings |> List.find (fun b -> b.Name = "M")
            match host.Value with
            | IR.IRLet (pid, (IR.IRTranspose _ as producer), IR.IRCompute (IR.IRApplyCombinator consumer)) ->
                let privateType = IR.typeOf producer
                let input = match source.Type with IR.ArrayElem a -> a | _ -> failwith "source is not dense"
                let hiddenIx = { input.IndexTypes.Head with Extent = IR.IRExtent (IR.IRVar (pid, privateType), 0) }
                let changeHost value = { m with Bindings = m.Bindings |> List.map (fun b -> if b.Id = host.Id then { b with Value = value } else b) }
                let changeConsumer f = changeHost (IR.IRLet (pid, producer, IR.IRCompute (IR.IRApplyCombinator (f consumer))))
                let declared = { m with Types = m.Types @ [IR.IRTDIndexType ("HiddenTranspose", hiddenIx)] }
                let foreignModule =
                    { declared with
                        Bindings = []
                        Functions = []
                        CompoundInits = Map.empty
                        SparseInits = Map.empty
                        RandomInits = Map.empty
                        ProviderReads = Map.empty
                        ProviderWrites = Map.empty }
                let foreignRefs = Optimize.moduleReferencedIds foreignModule
                let capture: IR.CaptureInfo = { Id = pid; Name = "hidden"; Type = privateType; IsMutable = false }
                let captureDemand = { m with Functions = m.Functions @ [{ m.Functions.Head with Id = 990032; Name = "observer"; Captures = [capture] }] }
                let shared = { m with Bindings = m.Bindings @ [{ host with Id = 990033; Name = "other" }] }
                let side = { m with SparseInits = m.SparseInits.Add(host.Id, IR.IRLit (IR.IRLitFloat 1.0)) }
                let metadata = changeConsumer (fun c -> { c with SharedIndexTypes = [hiddenIx] })
                let kernelAxis = changeConsumer (fun c -> { c with KernelTDims = [hiddenIx] })
                let operandAxis = changeConsumer (fun c ->
                    let a = c.ArrayTypes.Head
                    { c with ArrayTypes = [{ a with IndexTypes = [hiddenIx; a.IndexTypes.[1]] }] })
                let loopAxis = changeConsumer (fun c ->
                    match c.Loop with
                    | IR.IRMethodFor mf -> { c with Loop = IR.IRMethodFor { mf with SharedIndexTypes = [hiddenIx] } }
                    | _ -> failwith "no method_for")
                let negativeRank = changeConsumer (fun c -> { c with KernelInputRanks = [-1] })
                let unknownFold =
                    let rowId = match consumer.Kernel with IR.IRVar (id, _) -> id | _ -> failwith "no row kernel"
                    let functions =
                        m.Functions
                        |> List.map (fun f ->
                            if f.Id <> rowId then f
                            else
                                match f.Body with
                                | IR.IRReduce (a, k, seed) -> { f with Body = IR.IRReduce (a, IR.IRVar (990036, IR.typeOf k), seed) }
                                | _ -> failwith "no direct row fold")
                    { m with Functions = functions }
                let wrongIdentity =
                    let c = match host.Type with IR.ArrayElem a -> a | _ -> failwith "no output"
                    let ty = IR.mkArrayLike { c with IndexTypes = [{ c.IndexTypes.Head with Tag = Some "WrongC" }] }
                    { m with Bindings = m.Bindings |> List.map (fun b -> if b.Id = host.Id then { b with Type = ty } else b) }
                let structured =
                    let ty = IR.mkArrayLike { input with IndexTypes = [{ input.IndexTypes.Head with IxKind = Blade.Types.IxKRagged }; input.IndexTypes.[1]] }
                    changeHost (IR.mapIRExpr (function IR.IRVar (id, _) when id = source.Id -> IR.IRVar (id, ty) | n -> n) host.Value)
                let sourceEvent = { m with Bindings = m.Bindings |> List.map (fun b -> if b.Id = source.Id then { b with Value = IR.IRCompute (IR.IRBeginPoolScope "hidden") } else b) }
                let write = IR.IRAssign (IR.IRIndex (IR.IRVar (source.Id, source.Type), [IR.IRLit (IR.IRLitInt 0L); IR.IRLit (IR.IRLitInt 0L)], None), IR.IRLit (IR.IRLitFloat 9.0))
                let mutation = changeHost (IR.IRLet (pid, producer, IR.IRLet (990034, write, IR.IRCompute (IR.IRApplyCombinator consumer))))
                let alias = changeHost (IR.IRLet (pid, producer, IR.IRLet (990035, IR.IRVar (pid, privateType), IR.IRCompute (IR.IRApplyCombinator consumer))))
                let witnesses: (string * IR.IRModule * Set<Blade.Types.IRId> option) list =
                    [ ("missing_foreign_certificate", m, None); ("foreign_type_only", m, Some foreignRefs);
                      ("declared_type", declared, Some Set.empty); ("capture", captureDemand, Some Set.empty);
                      ("reference_equal_other_host", shared, Some Set.empty); ("constant_side_payload", side, Some Set.empty);
                      ("shared_axis", metadata, Some Set.empty); ("kernel_axis", kernelAxis, Some Set.empty);
                      ("operand_axis", operandAxis, Some Set.empty); ("method_for_axis", loopAxis, Some Set.empty);
                      ("negative_rank", negativeRank, Some Set.empty); ("wrong_output_identity", wrongIdentity, Some Set.empty);
                      ("unknown_fold", unknownFold, Some Set.empty);
                      ("structured_source", structured, Some Set.empty); ("source_lifetime", sourceEvent, Some Set.empty);
                      ("intervening_mutation", mutation, Some Set.empty); ("local_alias", alias, Some Set.empty) ]
                [ for suffix, md, evidence in witnesses do
                    let name = $"transpose_rows_hidden_{suffix}_keeps_transpose"
                    let builder = IR.IRBuilder ()
                    builder.EnsureAtLeast 1000000
                    Blade.Effects.Decisions.start ()
                    let kept = Optimize.fusePartialRowTerminalsWithCensusModule builder md.Functions evidence md
                    let ds = Blade.Effects.Decisions.drain ()
                    let mutable retained = false
                    for b in kept.Bindings do IR.iterIRExpr (function IR.IRTranspose _ -> retained <- true | _ -> ()) b.Value
                    if retained && (ds |> List.exists (fun d -> d.Rule = "transpose-row-fold" && not (applied d))) then
                        resultLine Pass name "complete residual/metadata/order proof declines"; yield true
                    else resultLine Fail name "unsupported payload lost its hard-transpose storage"; yield false ]
            | _ -> resultLine Fail "transpose_rows_hidden_setup" "the actual immediate transpose/row topology changed"; [false])

let private selectedTerminalLlvm () =
    let name = "selected_terminal_llvm_real_route_no_selection_allocations"
    withSelectedTerminal (fun () ->
        let emit mode = withGate "BLADE_COMPACTION_FUSION" mode (fun () ->
            match lower selectedTerminalSrc with
            | Error e -> Error e
            | Ok ir ->
                let admitted = ir.Modules |> List.exists (fun m -> m.Functions |> List.exists (fun f -> f.Name.Contains "__selected_terminal_"))
                EmitLlvm.tryEmitProgramNamed name ir |> Result.map (fun ll -> admitted, ll))
        match emit "on", emit "0" with
        | Ok (true, ll), Ok (false, old) ->
            let allocs (s: string) = System.Text.RegularExpressions.Regex.Matches(s, @"call ptr @blade_alloc_cells\(").Count
            checkAll name ll "LLVM stays in its own backend: only the stored input remains"
                [ "an actual guarded chain", ll.Contains "fcmp ogt" && (ll.Contains "br i1" || ll.Contains "select i1")
                  "exactly one array allocation", allocs ll = 1
                  "reference really has removable storage", allocs old > allocs ll
                  "serial arithmetic", not (ll.Contains "fadd reassoc") && not (ll.Contains "fmul reassoc") ]
        | a, b -> resultLine Fail name ($"ON={a}; OFF={b}"); false)

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
        withGate "BLADE_EPILOGUE_LEGS" gate (fun () ->
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
    withGate "BLADE_EPILOGUE_LEGS" gate (fun () ->
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
    let cppWith gate = withGate "BLADE_EPILOGUE_LEGS" gate (fun () -> withGate "BLADE_OMP_THREADS" "1" (fun () -> cppOfSource name src))
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
    let emit gate = withGate "BLADE_EPILOGUE_LEGS" gate (fun () -> withGate "BLADE_FP_REPRO" "1" (fun () -> withGate "BLADE_OMP_THREADS" null (fun () -> cppOfSource name eplSaxpySrc)))
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
        + "    s + 1.0\n"
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
        | Ok rn, Ok rt when rn.Length = 3 && rt.IsEmpty && (mainBody normal).Contains "__epl_acc_"
                           && not ((mainBody testMode).Contains "__epl_")
                           && (mainBody testMode).Contains "later("
                           && (mainBody testMode).Contains "early(" ->
            resultLine Pass name "normal: 3 pools released once at return; coverage mode actually declines the retained inlines and keeps both calls, with no fused input legs"
            true
        | a, b -> resultLine Fail name (sprintf "normal %A; test mode %A" a b); false
    | Error e, _ | _, Error e -> resultLine Fail name e; false

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

// A01: the four concrete lifetime holes, plus the documented scratch reuse
// exclusion. These read the actual retained-inline route and allocation
// release order, with CSE on/off. Execution/raw-bit twins live in the
// cell-epilogue block and corpus (the sole runner executes them).
let private lifetimeFiles =
    [ "032_lifetime_deleted_final_join.blade", false, 1
      "033_lifetime_cse_equal_caller_map.blade", true, 1
      "034_lifetime_duplicate_scalar_return.blade", true, 1
      "035_lifetime_function_scalar_return.blade", true, 1
      "036_lifetime_caller_panic.blade", true, 1
      "037_lifetime_scratch_reuse_region.blade", true, 2 ]

let private lifetimeCppFunction (cpp: string) =
    let m = System.Text.RegularExpressions.Regex.Match(cpp, @"(?s)double driver\([^;{]*\) \{.*?\n\}")
    if m.Success then m.Value else ""

let private eplLifetimeWitnesses () =
    [ for (file, inFunction, poolCount) in lifetimeFiles do
       for cse in [ "1"; "0" ] do
        let name = $"inline_lifetime_{file}_cse_{cse}"
        yield
          withGate "BLADE_FP_REPRO" "1" (fun () -> withGate "BLADE_OMP_THREADS" "1" (fun () ->
          withGate "BLADE_BLAS" "off" (fun () -> withGate "BLADE_POOL_REUSE" "1" (fun () ->
          withGate "BLADE_CSE" cse (fun () ->
            match corpusSource ("sibling-folds/" + file) with
            | Error e -> resultLine Fail name e; false
            | Ok src ->
            match cppOfSource name src with
            | Error e -> resultLine Fail name e; false
            | Ok cpp ->
                let body = if inFunction then lifetimeCppFunction cpp else mainBody cpp
                let boundary = body.IndexOf "is released at that return boundary"
                let panic = body.IndexOf "blade_rt::idiv64(7L"
                let panicOrder = not (file.StartsWith "036") || panic > boundary
                match releasesSafe body with
                | Ok pools when pools.Length = poolCount && body.Contains "__epl_acc_" && panicOrder ->
                    resultLine Pass name ($"{poolCount} owned pools released once, no later read; fused route present; caller failure follows cleanup")
                    true
                | rr -> resultLine Fail name (sprintf "release proof %A; fused=%b; boundary=%d panic=%d" rr (body.Contains "__epl_acc_") boundary panic); false))))) ]

let private eplLifetimeValidatorCases () =
    let run name corrupt (needle: string option) =
        withGate "BLADE_FP_REPRO" "1" (fun () -> withGate "BLADE_OMP_THREADS" "1" (fun () ->
        withGate "BLADE_BLAS" "off" (fun () ->
            match corpusSource "sibling-folds/033_lifetime_cse_equal_caller_map.blade" |> Result.bind lower with
            | Error e -> resultLine Fail name e; false
            | Ok ir ->
                let ir' = corrupt ir
                match Blade.IRValidate.validateIR ir', needle with
                | Ok _, None -> resultLine Pass name "untouched lifetime region validates"; true
                | Error es, Some n when es |> List.exists (fun e -> e.Contains n) -> resultLine Pass name n; true
                | Error es, _ -> resultLine Fail name (String.concat " | " es); false
                | Ok _, Some n -> resultLine Fail name ($"malformed lifetime IR accepted: {n}"); false)))
    let mutate change (ir: Blade.IR.IRProgram) =
        { ir with
            Modules =
                ir.Modules |> List.map (fun m ->
                    { m with
                        Functions =
                            m.Functions |> List.map (fun f ->
                                { f with Body = Blade.IR.mapIRExpr change f.Body }) }) }
    let after alias = mutate (fun n ->
        match n with
        | Blade.IR.IRLet (eid, (Blade.IR.IRReleasePools (_, (pool :: _), _) as release), rest) ->
            let read = if alias then Blade.IR.IRVar (990001, Blade.IR.typeOf pool) else pool
            let bad = Blade.IR.IRLet (990002, Blade.IR.IRExtent (read, 0), rest)
            let tail = Blade.IR.IRLet (eid, release, bad)
            if alias then Blade.IR.IRLet (990001, Blade.IR.IRPure pool, tail) else tail
        | _ -> n)
    let foreign = mutate (fun n ->
        match n with
        | Blade.IR.IRReleasePools (scope, pools, call) ->
            let p = pools.Head
            Blade.IR.IRReleasePools (scope, Blade.IR.IRVar (990003, Blade.IR.typeOf p) :: pools, call)
        | _ -> n)
    let missing = mutate (function Blade.IR.IRReleasePools _ -> Blade.IR.IRLit Blade.IR.IRLitUnit | n -> n)
    let embeddedExtent = mutate (fun n ->
        match n with
        | Blade.IR.IRLet (eid, (Blade.IR.IRReleasePools (_, (pool :: _), _) as release), rest) ->
            match Blade.IR.typeOf pool with
            | Blade.Types.IRTArrow ((Blade.Types.SIdx ix) :: slots, ret, ident) ->
                let ty = Blade.Types.IRTArrow (Blade.Types.SIdx { ix with Extent = Blade.IR.IRExtent (pool, 0) } :: slots, ret, ident)
                Blade.IR.IRLet (eid, release, Blade.IR.IRLet (990005, Blade.IR.IRZero ty, rest))
            | _ -> n
        | _ -> n)
    let freshened (ir: Blade.IR.IRProgram) =
        let builder = Blade.IR.IRBuilder()
        builder.EnsureAtLeast(1000000)
        let rewrite (f: Blade.IR.IRCallable) =
            if not (Blade.IR.containsPoolLifetimeEvent f.Body) then f
            else
                let ps, body = Blade.IRMono.freshenCallable builder f
                { f with Params = ps; Body = body }
        { ir with
            Modules =
                ir.Modules |> List.map (fun m ->
                    { m with Functions = m.Functions |> List.map rewrite }) }
    let captured (ir: Blade.IR.IRProgram) =
        let rewriteModule (m: Blade.IR.IRModule) =
            let mutable capture : Blade.IR.IRExpr option = None
            let rewriteNode n =
                match n with
                | Blade.IR.IRLet (eid, (Blade.IR.IRReleasePools (_, (pool :: _), _) as release), rest) ->
                    capture <- Some pool
                    let t = Blade.Types.IRTScalar Blade.Types.ETInt64
                    let callableTy = Blade.Types.IRTArrow ([], t, None)
                    let call = Blade.IR.IRApp (Blade.IR.IRVar (990004, callableTy), [], t)
                    Blade.IR.IRLet (eid, release, Blade.IR.IRLet (990006, call, rest))
                | _ -> n
            let functions =
                m.Functions |> List.map (fun f ->
                    { f with Body = Blade.IR.mapIRExpr rewriteNode f.Body })
            match capture with
            | Some (Blade.IR.IRVar (pid, pty) as pool) ->
                let helper =
                    { functions.Head with
                        Id = 990004
                        Name = "bad_capture"
                        SourceName = "bad_capture"
                        Params = []
                        RetType = Blade.Types.IRTScalar Blade.Types.ETInt64
                        Body = Blade.IR.IRExtent (pool, 0)
                        Captures = [ { Id = pid; Name = "p"; Type = pty; IsMutable = false } ] }
                { m with Functions = functions @ [helper] }
            | _ -> m
        { ir with Modules = ir.Modules |> List.map rewriteModule }
    [ run "inline_lifetime_validator_clean" id None
      run "inline_lifetime_validator_freshened_ids" freshened None
      run "inline_lifetime_validator_captured_pool_after_release" captured (Some "reference to released inline pool")
      run "inline_lifetime_validator_embedded_extent_after_release" embeddedExtent (Some "reference to released inline pool")
      run "inline_lifetime_validator_read_after_release" (after false) (Some "reference to released inline pool")
      run "inline_lifetime_validator_deferred_alias_after_release" (after true) (Some "reference to released inline pool")
      run "inline_lifetime_validator_foreign_pool" foreign (Some "not owned by its inline region")
      run "inline_lifetime_validator_missing_release" missing (Some "no release event") ]

let private eplLifetimeBarrierIsNotWrite () =
    let name = "inline_lifetime_resource_barrier_is_not_source_write"
    match corpusSource "sibling-folds/035_lifetime_function_scalar_return.blade" |> Result.bind lower with
    | Error e -> resultLine Fail name e; false
    | Ok ir ->
        let m = ir.Modules |> List.find (fun m -> m.Functions |> List.exists (fun f -> f.Name = "driver"))
        let f = m.Functions |> List.find (fun f -> f.Name = "driver")
        let barrier = Blade.Optimize.scheduleHasLifetimeBarrierIn [] m f.Body
        let writes = Blade.Optimize.scheduleMayWriteIn [] m f.Body
        if barrier && not writes then resultLine Pass name "lifetime event is a separate motion barrier, with no source write or FP licence"; true
        else resultLine Fail name (sprintf "barrier=%b writes=%b" barrier writes); false

let private eplLifetimeKernelRefusal () =
    let name = "inline_lifetime_expression_renderer_declines"
    match corpusSource "sibling-folds/035_lifetime_function_scalar_return.blade" with
    | Error e -> resultLine Fail name e; false
    | Ok src ->
        let asValue = src + "\nlet held = driver\n"
        withGate "BLADE_FP_REPRO" "1" (fun () -> withGate "BLADE_OMP_THREADS" "1" (fun () ->
        withGate "BLADE_BLAS" "off" (fun () ->
            match cppOfSource (name + "_eligible") src, codegenDecisionsOf asValue, cppOfSource name asValue with
            | Ok baseline, Ok ds, Ok cpp ->
                let declines = ds |> List.filter (fun d ->
                    d.Rule = "sibling-folds" && d.Subject = "driver"
                    && declinedMentioning "kernel(x, y) not inlined: lifetime event not honoured by expression/kernel renderer" d)
                let joined = ds |> List.exists (fun d ->
                    d.Rule = "sibling-folds" && d.Subject = "driver" && applied d)
                let body = lifetimeCppFunction cpp
                let eligible = (lifetimeCppFunction baseline).Contains "__epl_acc_"
                if eligible && declines.Length = 1 && joined && body.Contains "kernel(" && not (body.Contains "__epl_") then
                    resultLine Pass name "eligible baseline fuses; function used as a value records exactly one retained-inline decline while its independent sibling group still applies; original call remains"
                    true
                else
                    let seen = ds |> List.filter (fun d -> d.Rule = "sibling-folds") |> List.map Blade.Effects.Decisions.render |> String.concat " | "
                    resultLine Fail name (sprintf "eligible=%b; capability declines=%d; independent group applied=%b; original call present=%b; decisions: %s" eligible declines.Length joined (body.Contains "kernel(") seen)
                    false
            | Error e, _, _ | _, Error e, _ | _, _, Error e -> resultLine Fail name e; false)))

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

// C03: exactly two private selected terminals in one adjacent tuple host.
// C01's single-fold assertions above remain independent regressions.
let private selectedTupleSrc =
    "let x = [-2.0, 0.0, 1.0, 3.0, -1.0, 2.0]\n"
    + "let keep = mask(x, lambda(v) -> v > 0.0)\n"
    + "let y = compound(x, keep)\n"
    + "let result = (reduce(y, (+), 7.0), reduce(y, lambda(a, b) -> a * 10.0 + b, -2.0))\n"

let private withSelectedTuple f =
    withGates [ ("BLADE_PRINT", "result"); ("BLADE_FP_REPRO", "1"); ("BLADE_FP_CONTRACT", "off");
                ("BLADE_BLAS", "off"); ("BLADE_OMP_THREADS", "1");
                ("BLADE_COMPACTION_FUSION", "on"); ("BLADE_SIBLING_FOLDS", "on") ] f

let private selectedTupleGuardCount (ir: Blade.IR.IRProgram) =
    ir.Modules |> List.sumBy (fun m -> m.Functions |> List.filter (fun f -> f.Name.StartsWith "__selected_terminal_") |> List.length)

let private selectedTupleTopology () =
    let name = "selected_tuple_exact_host_two_seeds_fresh_serial_guards"
    withSelectedTuple (fun () ->
        match withGate "BLADE_COMPACTION_FUSION" "0" (fun () -> lower selectedTupleSrc) with
        | Error e -> resultLine Fail name e; false
        | Ok ir ->
            let m = ir.Modules.Head
            let host = m.Bindings |> List.find (fun b -> b.Name = "result")
            let x = m.Bindings |> List.find (fun b -> b.Name = "x")
            let builder = IR.IRBuilder ()
            builder.EnsureAtLeast 1000000
            let changed = Optimize.fuseSelectedTerminalModule builder m.Functions Set.empty m
            let newHost = changed.Bindings |> List.find (fun b -> b.Id = host.Id)
            let guards = changed.Functions |> List.filter (fun f -> f.Name.StartsWith "__selected_terminal_")
            let rec seedValue = function
                | IR.IRLit (IR.IRLitFloat f) -> Some f
                | IR.IRUnaryOp (IR.IRNeg, e) -> seedValue e |> Option.map (~-)
                | _ -> None
            let shape =
                match host.Value, newHost.Value with
                | IR.IRTuple [ IR.IRReduce (IR.IRVar (y1, _), _, Some _); IR.IRReduce (IR.IRVar (y2, _), _, Some _) ],
                  IR.IRTuple [ IR.IRReduce (IR.IRVar (a, _), IR.IRVar (k1, t1), Some s1);
                               IR.IRReduce (IR.IRVar (b, _), IR.IRVar (k2, t2), Some s2) ] ->
                    y1 = y2 && a = x.Id && b = x.Id && k1 <> k2
                    && seedValue s1 = Some 7.0 && seedValue s2 = Some (-2.0)
                    && ([ k1, t1; k2, t2 ] |> List.forall (fun (id, ty) ->
                        guards |> List.exists (fun g -> g.Id = id && ty = IR.mkFuncArrow (g.Params |> List.map _.Type) g.RetType)))
                | _ -> false
            let guardGraph (g: IR.IRCallable) =
                not g.IsOmpParallel && not g.IsCudaKernel && not g.IsMpiParallel && not g.IsCommutative
                && g.CommGroups.IsEmpty && g.Captures.IsEmpty
                && (match g.Body with
                    | IR.IRIf (_, _, IR.IRVar (acc, _)) -> acc = g.Params.Head.VarId
                    | _ -> false)
                && changed.DerivedFuncOrigins.ContainsKey g.Id
            checkAll name "" "exact tuple topology; original host identity; two fresh unlicensed guards and independent seeds"
                [ ("raw and rewritten tuple topology", shape);
                  ("host ID/name/type preserved", newHost.Name = host.Name && newHost.Type = host.Type);
                  ("only input and result bindings", (changed.Bindings |> List.map _.Id) = [x.Id; host.Id]);
                  ("no selected initializer", changed.CompoundInits.IsEmpty);
                  ("both guards keep false-branch accumulator", guards.Length = 2 && List.forall guardGraph guards) ])

let private selectedTupleEmission () =
    let name = "selected_tuple_storage_elision_vs_sibling_traversal_cpp_and_llvm"
    withSelectedTuple (fun () ->
        let emit compaction sibling = withGates [ ("BLADE_COMPACTION_FUSION", compaction); ("BLADE_SIBLING_FOLDS", sibling) ] (fun () ->
            match lower selectedTupleSrc with
            | Error e -> Error e
            | Ok ir ->
                let mutable joins = 0
                for m in ir.Modules do
                    for b in m.Bindings do
                        IR.iterIRExpr (function IR.IRReduceCompute (_, IR.IRTuple [_; _], IR.IRTuple [_; _]) -> joins <- joins + 1 | _ -> ()) b.Value
                EmitLlvm.tryEmitProgramNamed name ir
                |> Result.map (fun ll -> selectedTupleGuardCount ir, joins, fst (CodeGen.genSelfContainedProgramFromIR ir name), ll))
        // `unshared` disables selected-terminal while retaining the existing
        // positions-elided compaction route. Fully OFF restores positions;
        // it must not inflate the saving credited to this card.
        match emit "on" "on", emit "on" "0", emit "unshared" "on", emit "0" "on" with
        | Ok (g1, j1, joined, ll), Ok (g2, j2, separate, sl), Ok (g3, j3, baseline, old), Ok (g4, j4, _, fullyOff) ->
            let loops (cpp: string) = System.Text.RegularExpressions.Regex.Matches(mainOf cpp, @"for \(size_t (?:__i\d|__ri) = 0;").Count
            let pools (s: string) = System.Text.RegularExpressions.Regex.Matches(s, @"call ptr @blade_alloc_cells\(").Count
            let cppPrivate (s: string) = s.Contains "keep_extents" || s.Contains "y_compact" || s.Contains "y_idx"
            let main = mainOf joined
            let seeds = System.Text.RegularExpressions.Regex.Matches(main, @"double __v\d+_\d+ = ([^;]+);") |> Seq.map (fun m -> m.Groups.[1].Value) |> List.ofSeq
            let llvmScans (s: string) = System.Text.RegularExpressions.Regex.Matches(s, @"icmp slt i64 %r\d+, 6\b").Count
            let description = $"guards={g1}/{g2}/{g3}/{g4}, joins={j1}/{j2}/{j3}/{j4}; C++ scans={loops joined}/{loops separate}; LLVM scans={llvmScans ll}/{llvmScans sl}, pools={pools ll}/{pools sl}/{pools old}/{pools fullyOff}; seeds={seeds}"
            checkAll name ll description
                [ ("actual rewritten and joined IR: " + description, (g1, j1, g2, j2, g3, j3, g4, j4) = (2, 1, 2, 0, 0, 0, 0, 0));
                  ("one vs two native source scans: " + description, loops joined = 1 && loops separate = 2 && llvmScans ll = 1 && llvmScans sl = 2);
                  ("both elided routes retain only required x storage", not (cppPrivate joined) && not (cppPrivate separate)
                      && main.Contains "x_extents" && System.Text.RegularExpressions.Regex.Matches(main, @"allocate<").Count = 1);
                  ("baseline really keeps mask+compact values", cppPrivate baseline && baseline.Contains "y_compact = new double[");
                  ("positions already absent in baseline", not (baseline.Contains "y_pos"));
                  ("two original C++ seeds", seeds = ["7.0"; "-(2.0)"]);
                  ("LLVM actual allocations: " + description, pools ll = 1 && pools sl = 1 && pools old = 3 && pools fullyOff = 4
                      && ll.Contains "call ptr @blade_alloc_cells(i64 6, i64 8)");
                  ("logical predicate retained per LLVM leg", System.Text.RegularExpressions.Regex.Matches(ll, @"fcmp ogt double").Count = 2);
                  ("result observable in both backends", main.Contains "\"result = \"" && ll.Contains "result = ");
                  ("serial LLVM graph", not (ll.Contains "fadd reassoc") && not (ll.Contains "fmul reassoc")) ]
        | a, b, c, d -> resultLine Fail name ($"joined={a}; sibling-off={b}; positions-elided reference={c}; fully OFF={d}"); false)

let private selectedTupleFailures =
    "function late_selected_predicate(v: Float64) -> Bool = {\n"
    + "    let z = Int64(floor(v - 2.0))\n    if v == 2.0 then (1 / z) > 0 else v > 0.0\n}\n"
    + "function early_selected_fold(a: Float64, b: Float64) -> Float64 = {\n"
    + "    let z = Int64(floor(b - b))\n    a + Float64(1 / z)\n}\n"
    + selectedTupleSrc.Replace("lambda(v) -> v > 0.0", "late_selected_predicate").Replace("reduce(y, (+), 7.0)", "reduce(y, early_selected_fold, 7.0)")

let private selectedTupleInterpreterCases () =
    let cases =
        [ ("ordered", selectedTupleSrc, "(13.0, -1868.0)", true);
          ("empty", selectedTupleSrc.Replace("v > 0.0", "v > 100.0"), "(7.0, -2.0)", true);
          ("stored_alias", selectedTupleSrc.Replace("let x =", "let storage =").Replace("let keep =", "let x = storage\nlet keep ="), "(13.0, -1868.0)", true);
          ("empty_input", selectedTupleSrc.Replace("let x = [-2.0, 0.0, 1.0, 3.0, -1.0, 2.0]", "let x: Array<Float64 like Idx<0>> = []"), "(7.0, -2.0)", true);
          ("cancellation", selectedTupleSrc.Replace("[-2.0, 0.0, 1.0, 3.0, -1.0, 2.0]", "[1e16, -1e16, 1.0]").Replace("v > 0.0", "v != 0.0").Replace("(+), 7.0", "(+), 1.0").Replace("a * 10.0 + b", "a - b"), "(1.0, -3.0)", true);
          ("zero_seed_bits", selectedTupleSrc.Replace("v > 0.0", "v > 100.0").Replace("(+), 7.0", "(+), 0.0").Replace(", -2.0)", ", -0.0)"), "(0.0, -0.0)", true);
          ("paired_failure", selectedTupleFailures, "late_selected_predicate", false) ]
    [ for suffix, src, expected, positive in cases do
        let name = $"selected_tuple_interp_independent_on_off_{suffix}"
        yield withSelectedTuple (fun () ->
            let interpret compaction sibling = withGates [ ("BLADE_COMPACTION_FUSION", compaction); ("BLADE_SIBLING_FOLDS", sibling) ] (fun () ->
                match lower src with
                | Error e -> Error e
                | Ok ir ->
                    match IRValidate.validateIR ir with
                    | Error es -> Error (String.concat "; " es)
                    | Ok valid -> Ok (selectedTupleGuardCount ir, Blade.Interp.Run.runProgram valid name Blade.Interp.Value.defaultLimits))
            match interpret "on" "on", interpret "on" "0", interpret "0" "on" with
            | Ok (a, on), Ok (b, separate), Ok (0, off) ->
                let outcome =
                    if positive then a = 2 && b = 2 && on.ExitCode = 0 && separate.ExitCode = 0 && off.ExitCode = 0
                                     && on.Stdout.Contains ("result = " + expected)
                    else a = 0 && b = 0 && on.ExitCode <> 0 && separate.ExitCode = on.ExitCode && off.ExitCode = on.ExitCode
                         && on.Stderr.Contains expected
                checkAll name on.Stdout "three independently pinned arms; admission asserted separately from value agreement"
                    [ ("independent value/failure and route", outcome);
                      ("ON/sibling-OFF/reference stdout", on.Stdout = separate.Stdout && on.Stdout = off.Stdout);
                      ("ON/sibling-OFF/reference stderr", on.Stderr = separate.Stderr && on.Stderr = off.Stderr) ]
            | a, b, c -> resultLine Fail name ($"ON={a}; sibling-off={b}; OFF={c}"); false) ]

let private selectedTupleRefusals () =
    let cases =
        [ ("extra_mask_read", selectedTupleSrc + "let observed = keep(0)\n", "additional consumer");
          ("keyed_selection", selectedTupleSrc + "let observed = y(2)\n", "additional consumer");
          ("selected_alias", selectedTupleSrc + "let observed = y\n", "additional consumer");
          ("third_terminal", selectedTupleSrc + "let third = reduce(y, (+), 3.0)\n", "exactly one");
          ("selected_capture", selectedTupleSrc + "let observed = method_for(x) <@> lambda(v) -> Float64(extents(y)) + v\n", "callable body or capture");
          ("unseeded", selectedTupleSrc.Replace("(+), 7.0", "(+)"), "direct seeded");
          ("selected_map", selectedTupleSrc.Replace("reduce(y, (+)", "reduce(y * y, (+)"), "direct seeded");
          ("separate_bindings", selectedTupleSrc.Replace("let result = (reduce(y, (+), 7.0), reduce(y, lambda(a, b) -> a * 10.0 + b, -2.0))", "let first = reduce(y, (+), 7.0)\nlet result = reduce(y, lambda(a, b) -> a * 10.0 + b, -2.0)"), "exactly one");
          ("dependent_seed", selectedTupleSrc.Replace("let result = (reduce(y, (+), 7.0), reduce(y, lambda(a, b) -> a * 10.0 + b, -2.0))", "let first = reduce(y, (+), 7.0)\nlet result = reduce(y, lambda(a, b) -> a * 10.0 + b, first)"), "exactly one");
          ("intervening_abort", selectedTupleSrc.Replace("let result =", "let z = Int64(floor(x(1)))\nlet boom = 1 / z\nlet result ="), "adjacent");
          ("source_write", selectedTupleSrc.Replace("let x =", "let mut x =").Replace("let y =", "x(0) = 9.0\nlet y ="), "adjacent");
          ("failing_predicate", selectedTupleSrc.Replace("v > 0.0", "lgamma(v) > 0.0"), "pure/total");
          ("failing_second_fold", selectedTupleSrc.Replace("a * 10.0 + b", "a + lgamma(b)"), "pure/total");
          ("failing_second_seed", selectedTupleSrc.Replace(", -2.0)", ", lgamma(-2.0))"), "explicit seed");
          ("unknown_call", "function opaque(v: Float64) -> Float64 = v * v\n" + selectedTupleSrc.Replace("a * 10.0 + b", "a + opaque(b)"), "pure/total");
          ("fold_capture", "let bias = 3.0\n" + selectedTupleSrc.Replace("a * 10.0 + b", "a + b + bias"), "pure/total");
          ("parallel_fold", selectedTupleSrc.Replace("lambda(a, b) -> a * 10.0 + b", "lambda(a, b) where comm(a, b), omp -> a + b"), "pure/total");
          ("paired_failure", selectedTupleFailures, "pure/total") ]
    [ for suffix, src, why in cases do
        yield withSelectedTuple (fun () -> decisionCase ($"selected_tuple_declines_{suffix}") src "selected-terminal" (declinedMentioning why) why)
      for suffix, gate, value, why in
          [ ("default_print", "BLADE_PRINT", "", "printed"); ("mask_print", "BLADE_PRINT", "result keep", "printed");
            ("selected_print", "BLADE_PRINT", "result y", "printed"); ("licensed", "BLADE_FP_REPRO", "0", "reproducible");
            ("optimizer_off", "BLADE_COMPACTION_FUSION", "0", "disabled"); ("unshared", "BLADE_COMPACTION_FUSION", "unshared", "disabled") ] do
        yield withSelectedTuple (fun () -> withGate gate value (fun () ->
            decisionCase ($"selected_tuple_declines_{suffix}") selectedTupleSrc "selected-terminal" (declinedMentioning why) why)) ]

let private selectedTupleHiddenDemands () =
    withSelectedTuple (fun () ->
        match withGate "BLADE_COMPACTION_FUSION" "0" (fun () -> lower selectedTupleSrc) with
        | Error e -> resultLine Fail "selected_tuple_hidden_setup" e; [false]
        | Ok ir ->
            let m = ir.Modules.Head
            let host = m.Bindings |> List.find (fun b -> b.Name = "result")
            let y = m.Bindings |> List.find (fun b -> b.Name = "y")
            let keep = m.Bindings |> List.find (fun b -> b.Name = "keep")
            let x = m.Bindings |> List.find (fun b -> b.Name = "x")
            let at = match x.Type with IR.ArrayElem at -> at | _ -> failwith "x is not dense"
            let hiddenIx = { at.IndexTypes.Head with Id = 990021; Extent = IR.IRExtent (IR.IRVar (y.Id, y.Type), 0) }
            let typeDemand = { m with Types = m.Types @ [IR.IRTDIndexType ("DemandedSelected", hiddenIx)] }
            let capture: IR.CaptureInfo = { Id = keep.Id; Name = "hidden"; Type = keep.Type; IsMutable = false }
            let captureDemand = { m with Functions = m.Functions @ [{ m.Functions.Head with Id = 990022; Name = "observer"; Captures = [capture] }] }
            let otherHost = { m with Bindings = m.Bindings @ [{ host with Id = 990023; Name = "other" }] }
            let payload = { m with SparseInits = m.SparseInits.Add(host.Id, IR.IRLit (IR.IRLitFloat 1.0)) }
            let operandType = IR.mkArrayLike { at with IndexTypes = [hiddenIx] }
            let hiddenOperand =
                let changed = IR.mapIRExpr (function IR.IRVar (id, _) when id = y.Id -> IR.IRVar (id, operandType) | e -> e) host.Value
                { m with Bindings = m.Bindings |> List.map (fun b -> if b.Id = host.Id then { b with Value = changed } else b) }
            let sourceEvent = { m with Bindings = m.Bindings |> List.map (fun b -> if b.Id = x.Id then { b with Value = IR.IRCompute (IR.IRBeginPoolScope "hidden") } else b) }
            let structured =
                let dense = IR.IRVar (x.Id, IR.mkArrayLike { at with IndexTypes = [{ at.IndexTypes.Head with IxKind = Blade.Types.IxKRagged }] })
                { m with CompoundInits = m.CompoundInits.Add(y.Id, (dense, IR.IRVar (keep.Id, keep.Type))) }
            let ownSelectedType =
                let ya = match y.Type with IR.ArrayElem a -> a | _ -> failwith "y is not compound"
                let ty = IR.mkArrayLike { ya with IndexTypes = [{ ya.IndexTypes.Head with Extent = hiddenIx.Extent }] }
                { m with Bindings = m.Bindings |> List.map (fun b -> if b.Id = y.Id then { b with Type = ty } else b) }
            let dynamicAxis =
                let ty = IR.mkArrayLike { at with IndexTypes = [{ at.IndexTypes.Head with Extent = IR.IRExtent (IR.IRVar (x.Id, x.Type), 0) }] }
                { m with CompoundInits = m.CompoundInits.Add(y.Id, (IR.IRVar (x.Id, ty), IR.IRVar (keep.Id, keep.Type))) }
            let unknownKernel =
                let value = match host.Value with
                            | IR.IRTuple [a; IR.IRReduce (v, k, s)] -> IR.IRTuple [a; IR.IRReduce (v, IR.IRVar (990026, IR.typeOf k), s)]
                            | _ -> failwith "tuple topology changed"
                { m with Bindings = m.Bindings |> List.map (fun b -> if b.Id = host.Id then { b with Value = value } else b) }
            let witnesses: (string * IR.IRModule * Set<Blade.Types.IRId>) list =
                [ ("declared_type", typeDemand, Set.empty); ("capture", captureDemand, Set.empty);
                  ("reference_equal_other_host", otherHost, Set.empty); ("host_execution_payload", payload, Set.empty);
                  ("operand_type", hiddenOperand, Set.empty); ("source_lifetime_event", sourceEvent, Set.empty);
                  ("structured_source", structured, Set.empty); ("unknown_kernel", unknownKernel, Set.empty);
                  ("removed_selected_type", ownSelectedType, Set.empty); ("dynamic_axis", dynamicAxis, Set.empty);
                  ("foreign_selection", m, Set.singleton y.Id); ("foreign_mask", m, Set.singleton keep.Id) ]
            [ for suffix, md, foreign in witnesses do
                let name = $"selected_tuple_hidden_{suffix}_retains_storage"
                let builder = IR.IRBuilder ()
                builder.EnsureAtLeast 1000000
                Blade.Effects.Decisions.start ()
                let kept = Optimize.fuseSelectedTerminalModule builder md.Functions foreign md
                let decisions = Blade.Effects.Decisions.drain ()
                let retained = kept.CompoundInits.ContainsKey y.Id && (kept.Bindings |> List.exists (fun b -> b.Id = keep.Id))
                let declined = decisions |> List.exists (fun d -> d.Rule = "selected-terminal" && not (applied d))
                if retained && declined then resultLine Pass name "full residual demand/legality census keeps both private stores"; yield true
                else resultLine Fail name "a hidden demand or unsupported payload was erased"; yield false ])

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
          // Round 3 A7c: producer-input legs (epilogue-legs v1, G69).
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
          yield! eplLifetimeWitnesses ()
          yield! eplLifetimeValidatorCases ()
          eplLifetimeKernelRefusal ()
          eplLifetimeBarrierIsNotWrite ()
          eplThrowingCalleeLlvm ()
          withGate "BLADE_OMP_THREADS" "1" (fun () ->
            eplNoRouteKeepsCall "epilogue_legs_nonliteral_epilogue_seed_keeps_call" eplNonliteralSeedSrc [ ("BLADE_FP_REPRO", "1") ] "seed is not a literal")
          withGate "BLADE_OMP_THREADS" "1" (fun () ->
            eplNoRouteKeepsCall "epilogue_legs_lanes_off_keeps_call" eplSaxpySrc [ ("BLADE_FOLD_LANES", "off") ] "BLADE_FOLD_LANES=off")
          yield! eplDecisionCases ()
          // Compaction fusion (round 2 item B4).
          compactionFusedLicensed ()
          compactionFusedRepro ()
          compactionGateOff ()
          compactionOmpFoldKeepsPool ()
          compactionAliasWriteNoInPass ()
          // PM19 stored-input, private selection, seeded terminal (repro).
          selectedTerminalEmission ()
          selectedTerminalLlvm ()
          selectedTerminalForeignRef ()
          selectedTerminalDeclaredIndexPayload ()
          yield! selectedTerminalRefusals ()
          yield! selectedTerminalInterpreterCases ()
          selectedTupleTopology ()
          selectedTupleEmission ()
          yield! selectedTupleInterpreterCases ()
          yield! selectedTupleRefusals ()
          yield! selectedTupleHiddenDemands ()
          // PM10 private rank-2 map -> seeded row terminal.
          partialRowsEmission ()
          yield! partialRowsLlvm ()
          partialRowsMetadata ()
          yield! partialRowsRefusals ()
          yield! partialRowsInterpreterCases ()
          yield! partialRowsHiddenPayloads ()
          yield! partialRowsLlvmInputBoundary ()
          yield! partialRowsLlvmMalformedPeel ()
          yield! transposeRowsEmptyLiteralShape ()
          transposeRowsEmission ()
          transposeRowsMetadataAndInterface ()
          yield! transposeRowsInterpreterCases ()
          yield! transposeRowsRefusals ()
          yield! transposeRowsHiddenDemands ()
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
