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
                  (fun d -> applied d && evidenceMentioning $"fold form: simplex tile 6x8 with W panel, KC={kc}, then / d per cell" d)
                  $"simplex tile at {p} x {n}, KC={kc}")
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
      planCase "plan_orbit_fold_sym2_applied" (planOrbitSrc planOrbitSym2Body) "fold-form"
          (fun d -> applied d && evidenceMentioning "fold form: orbit-class fold (rank 2, 2 classes)" d && evidenceMentioning "weights 1/2" d)
          "orbit-class fold, rank 2, weights 1/2"
      planCase "plan_orbit_fold_axes_spelling_applied" (planOrbitSrc "reduce(g, (+), axes = 2)") "fold-form"
          (fun d -> applied d && evidenceMentioning "orbit-class fold (rank 2" d)
          "`reduce(g, (+), axes = 2)` is the same orbit-class fold"
      planCase "plan_orbit_fold_sym3_applied" planOrbitSym3Src "fold-form"
          (fun d -> applied d && evidenceMentioning "fold form: orbit-class fold (rank 3, 3 classes)" d && evidenceMentioning "weights 1/3/6" d)
          "orbit-class fold, rank 3, weights 1/3/6"
      planCase "plan_orbit_fold_antisym_even" planOrbitAntisymSrc "fold-form"
          (fun d -> applied d && evidenceMentioning "even kernel" d && evidenceMentioning "strict orbits weight 2" d)
          "strict pool, even kernel, weight 2"
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
      // THE THREADED FORM (bench G32): `where omp` on the outer fold is spent
      // as a team over the first-coordinate rows -- in the fold-form line and
      // as an applied `omp` record -- and BLADE_OMP_THREADS=1 keeps the
      // serial pass with the census naming the knob.
      withGate "BLADE_OMP_THREADS" null (fun () ->
          planCase "plan_orbit_fold_threaded" (planOrbitSrc planOrbitSym2Body) "fold-form"
              (fun d -> applied d && evidenceMentioning "orbit-class fold (rank 2, 2 classes); thread team over the first-coordinate rows" d)
              "the omp-licensed orbit fold threads its first-coordinate rows")
      withGate "BLADE_OMP_THREADS" null (fun () ->
          planCase "plan_orbit_fold_threaded_omp_record" (planOrbitSrc planOrbitSym2Body) "omp"
              (fun d -> applied d && evidenceMentioning "orbit-class fold: first-coordinate rows" d)
              "omp applied: the orbit fold's thread team")
      withGate "BLADE_OMP_THREADS" "1" (fun () ->
          planCase "plan_orbit_fold_threads_suppressed" (planOrbitSrc planOrbitSym2Body) "fold-form"
              (fun d -> applied d && not (evidenceMentioning "thread team" d))
              "BLADE_OMP_THREADS=1: the serial orbit pass")
      withGate "BLADE_OMP_THREADS" "1" (fun () ->
          planCase "plan_orbit_fold_threads_suppressed_census" (planOrbitSrc planOrbitSym2Body) "omp"
              (declinedMentioning "BLADE_OMP_THREADS") "omp declined, naming BLADE_OMP_THREADS")
      planCase "plan_orbit_fold_unlicensed_serial" planOrbitSym3Src "fold-form"
          (fun d -> applied d && not (evidenceMentioning "thread team" d)) "no omp request: the serial orbit pass"
      (match withGate "BLADE_OMP_THREADS" null (fun () -> cppOfSource "plan_orbit_fold_threaded_cpp" (planOrbitSrc planOrbitSym2Body)) with
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
          decisionCase "decision_cse_across_pure_call_applied" cseAcrossPureCallSrc "cse" applied
              "cse applied across a call that writes nothing"
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
        @ [ parallelEmissionIsolated (); declSpanResetIsolated () ]
        @ validatorPins ()
    let passed = results |> List.filter id |> List.length
    let failed = results.Length - passed
    printFooter "Optimization Layer" [$"{passed} passed"; $"{failed} failed"]
    { Block = "Optimization Layer"; Passed = passed; Failed = failed; Skipped = 0
      FailedNames = if failed = 0 then [] else ["see above"] }
