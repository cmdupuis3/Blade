# Design decisions (architect) — for the method reviewer to attack

Every point where the charter was provisional or silent, the choice made, and why. Probe
facts are from `blade_check` / `blade emit` / `blade plan` on this worktree's binary
(compiler 0.20.0) on 2026-10-17, at extents of 11–1009 cells; no timing was run.

## Kernel selection

**D1 — `band` (charter A5) dropped.** Probe: `reduce(method_for(range<Band>) <@> k, (+))`
typechecks but the C++ emitter refuses it: `error[BL7004]: reduce over a deferred
computation is not supported for ragged/grouped/compound/sparse inputs yet -- bind the map
with |> compute and reduce the array`. The `|> compute` form routes through the SparseIdx
machinery (docs/plans/structural/06: "the C++ key builder walks the plan's nested
affine-bounded loops in lex order into a `sparse_index_t` (O(card) build, O(1) hashed
reads)") — a hashed key table of one entry per solution. For a 36M-solution band that is a
hash table, not a loop. The idiom-table row "iterate a constrained index domain" is
therefore correct about enumeration order and visit count but not benchmarkable as a
streaming kernel today. Reproducer: any `static struct` domain under a deferred `reduce`.
Recorded as a compiler finding; the orchestrator decides whether it is worth fixing before
the run. Nothing replaces it: there is no second closed-form-enumeration kernel to test.

**D2 — `pairkernel` (charter A4) is a materialized kernel matrix, not a pair sum.** The
pair sum `Σ_{i<j} k(x_i, x_j)` over a `where comm` kernel is `reduce` over compact storage,
refused at typecheck at six sites (BL3999: "folding the canonical cells and folding the
logical (mirrored) cells differ ... decompact(A, d) first"). `docs/plans/plan-compact-sym-
folds.md` §4 decides `reduce` means the LOGICAL fold and §4.4 says the canonical fold
"deserves a name" but has none yet. The `range<Pair>` domain route is D1. So the one
symmetric pairwise thing Blade can produce from a `comm` declaration is the packed matrix
itself — which is what GP / kernel-method code materializes anyway. The spec names K as the
output and both Fortran tiers store it (natural dense, expert packed). The pair sum is NOT
claimed.

**D3 — checksums of symmetric objects are the logical sum of squares; the total is the
result (restated after M13).** The per-spec "checksum tax" figures are planning estimates
of how much of Blade's region the logical-read checksum occupies; they are NOT measured
components and are NEVER subtracted from a result or a diagnosis. The missing canonical
fold (G2/G3) is the explanation offered when phase evidence points there, not an
adjustment. Original reasoning follows. With no canonical fold (D2), Blade reads all p² logical cells through the
canonicalizing accessor, in the one spelling that streams (README §7.2; the `range<I>,
range<I>` nest allocates a dense p x p — probed; `decompact` allocates dense — read in
`CodeGenBinding.genDecompactBinding`: "hard (dense alloc + symmetry-expanding scatter)").
The expert tier reads its triangle once with weight 2. The tax is p² canonicalizing reads
(~2–4 ns each) against the kernel's per-cell work: negligible for `gram` (1001 FMAs/cell),
`antisym` (802 FMAs/cell), `pairkernel` (sqrt+exp), 10–20% for `comoment3` (201 x 3
flops/cell, p³ = 5.0M reads). Each spec states it; the diagnosis of a `comoment3` loss must
subtract it before blaming the simplex loop. Alternative considered and rejected: a
checksum over canonical cells only (Blade cannot spell it; the tax would move to the
reviewer's trust). Alternative rejected: checksumming a sample of cells (does not validate
the object).

**D4 — `antisym` is the angular-momentum tensor L = XYᵀ − YXᵀ.** The charter's
`f(x_i)g(x_j) − f(x_j)g(x_i)` is three flops per cell once f and g are precomputed (which
both languages would do — it is the same algorithm); the kernel becomes a ~5 ms
memory-bound write under the 64 MB cap (dense natural arm: p <= 2828) and the measurement
would be noise plus the checksum tax. L_ij = Σ_s (x_is y_js − x_js y_is) has an inner
product per cell, is physically meaningful, keeps the anti-invariance syntactic
(`f(a, b) − f(b, a)`), and probed clean: `method_for(Z, Z) <@> lambda(a: T^2, b: T^2) where
anticomm(a, b), omp(a: 1) -> prodsum(a(0), b(1)) - prodsum(b(0), a(1))` with Z = (p, 2, n)
gives `packed SymAntisymmetric storage`, a `23 - __i0 - 1` strict triangle, and a dynamic
omp schedule. Caveat for blade-A: the literal row reads `a(0)` emit a BL8006 literal-bound
check per read; `a((0 : Cx))` with the declared component index type is the proven form.

**D5 — All input ranges have `lo >= 0`.** The charter's `input_checksum` is a plain sum;
with sign-mixed inputs it cancels and a relative tolerance is meaningless. Non-negative
inputs make every checksum in the suite a sum of non-negative terms with no extra pass.
Where the maths would normally be centred (comoment3), the raw moment is used instead; the
kernel's loop structure is identical.

**D6 — Materialization parity (README §1.5–1.6).** Probe: `let y2 = alpha * x + y` followed
by `reduce(y2 * y2, (+))` fuses into the fold and never stores y2. Without the rule, Blade's
`saxpy` would be a 2-stream kernel against Fortran's 3-stream one. Conversely, inputs could
be fused into consumers in Blade (pure functions of the index) and in expert Fortran; the
rule forbids it in every arm so memory-bound kernels stay memory-bound. Allocation is timed
on both sides for the same reason.

**D7 — Tier boundaries (restated after M1/M2/M22).** `natural` denotes the DENSE
BASELINE: the literal transcription with intrinsics and dense storage, no symmetry
exploitation — not a claim that this is all natural Fortran (an upper triangle with
mirrored assignment is natural too; the review is right). `expert` denotes the REVIEWED
implementation, never a proven optimum. gram's natural arm keeps `matmul(transpose(x), x)`
even though it is fast: the intrinsic IS the natural spelling and disabling it because it
beats Blade would weaken the baseline. "Native" = no user-selected external BLAS; compiler
intrinsic runtimes are allowed and disclosed per arm with their route. Equivalence is
defined by outputs, arithmetic dependencies and permitted symmetry reduction; checksum-only
algebraic shortcuts are excluded; storage orientation is ordinary implementation freedom.
Each spec carries a "selection record". Original reasoning follows. The
outer-product ordering of a Gram triangle (`do s; do j; do i = 1, j`) is expert-legal in
`strict` because each cell still accumulates s = 1..n in order; it is also the reason
`gram`'s hypothesis is a strict-regime LOSS — Blade's emitter picks the dot form and jams six
cells (observed in the fixture emit: `__i0 + 6 <= __jm`), which is latency-bound without
reassociation. If the benchmark confirms it, that is a finding against "the fastest way is
the only way", not against the protocol. The intrinsic `matmul` is allowed in natural
(libgfortran's runtime is part of the language, not a library in the charter's sense); the
Fortran agent records whether it was inlined (`-finline-matmul-limit`) or called.

**D8 — Fold licences in `strict`.** OpenMP `reduction(+:)` and Blade's fold licences both
reorder across threads; both are allowed in `mt8 x strict` (tolerance 1e-12). The serial
arm of a licensed Blade source must be verified reorder-free. My original premise — that a
serial build drops `-fopenmp` so the `*_SIMD_REDUCTION` macros (`src/cpp/
blade_portability.hpp`, guarded by `_OPENMP`) expand to nothing — is FALSE: the serial C++
build still passes `-fopenmp` and removes only the threading pragmas (blade-A/B). The
verification is therefore on the emitted `.cpp`: no `BLADE_OMP_*SIMD_REDUCTION` macro in
the serial emission. blade-A verified this for every group-A kernel
(`notes/llvm-acceptance-A.md`); blade-B's fold kernels are checked the same way. The parallel spelling per fold kernel is blade-B's to find
(README §7.4 lists what the probes showed); if none emits a pragma, the mt8 cell says "no
parallel form" and is not faked with a different kernel shape.

**D9 — `inputonly` twins, one PER ARM, are mandatory where the input share exceeds 5%
(restated after M8/M9/M16)**: fusedchain, onepass, stencil, maskagg, dot, saxpy. The
complete timed region is the primary estimand. The twin quantity is the "incremental
total-minus-twin estimate" — reported as a difference with a joint-round bootstrap
interval first, a ratio only when the reference's incremental interval excludes 0 (else
`undefined`), never called kernel time or "net": it also contains output allocation and
first touch, the checksum's cache state, and whatever removing the consumer changed.
Generation cost is measured per arm from its own twin, not assumed equal (Fortran masks,
Blade spells `%`). Original reasoning follows. The LCG costs ~3 ns per cell, more than a saxpy cell;
the total ratio for these kernels is biased toward 1 (both arms pay the same input cost),
which is conservative for Blade's wins and honest for the controls. Both the total and the
net (difference of medians with a bootstrap CI of the difference) are reported; the net is
noisier and says so.

**D10 — Two drift controls (restated after M18).** Compute control = `recur`,
gfortran-expert, serial x strict (tracks core clock/thermal; its region also writes a 48 MB
trajectory — "L2-resident" is per step). Stream control = `saxpy`, gfortran-expert, serial
x strict, built at n = 700,001 (16.8 MB, three streams; tracks memory-subsystem contention
and background load). Both run every round, both are validated like any run; within-block
first-half/second-half drift > 5% flags the block; a rolling control median > 7% off the
session's first block flags the session from there; an invalid control run flags the block.
15% was too permissive for 10% verdicts. Original reasoning for the compute control follows. Compute-bound,
L2-resident per step (20011 cells x 8 B), deterministic, ~0.1 s: it tracks core clock and
thermal state, which is what drifted in August. Memory-bound candidates (dot, onepass) would
track memory contention instead; gram is too long to run once per round of every kernel.

**D11 — SIZE marker grammar** (README §4): one marked declaration per parameter, one
literal on that line, derived sizes as expressions of the declaration. Probed: `let static n
= 31` with `type Sx = Idx<n>`-style use and `Int64<min=0, max=n-1>` typecheck (the `Int`
field type with `max=n-1` does not — Int32/Int64 clash in the desugared bound — so struct
bounds, if any source ever needs them, use `Int64` fields).

**D12 — Verdicts (restated after M17).** Practical threshold δ = 10%. *win*: interval above
1 and median ratio >= 1.10; *detectable*: interval above 1, median < 1.10; *equivalent*:
interval within [1/1.10, 1.10] (need not contain 1); *loss* / *detectable loss* symmetric;
otherwise *inconclusive*. Failure to detect is not equivalence. Intervals are pointwise
(joint-round bootstrap); no suite-wide superiority claim is made, so no multiplicity
correction is applied. The earlier "CI excludes 1 = win / contains 1 = parity" rule is
withdrawn.

**D13 — Thread pinning (restated after M20).** mt8: `OMP_NUM_THREADS=8 OMP_DYNAMIC=FALSE
OMP_PLACES=cores OMP_PROC_BIND=close`, with the actual thread count and placement VERIFIED
once per OpenMP runtime by a probe (the environment requests placement; it does not prove
it). Serial arms are pinned to one physical core (affinity mask at spawn, recorded), SMT
sibling idle. Every sample is a fresh process; the numbers are described as fresh-process,
allocation-inclusive. Unpinned 8-thread runs on an 8-core/16-thread part bounce between SMT
siblings and the variance swamps a 10% effect.

**D14 — `fastmath` (formerly `fast`) regime is per-kernel.** Only kernels with a fold whose association the
regime can change (gram, comoment3, antisym inner products; fusedchain, onepass, dot
folds) run it. pairkernel/stencil/saxpy have no fold but the checksum; recur/maskagg have
none that matters.

**D15 — Memory-bound kernels are shorter than the 0.2 s target.** The 64 MB cap (counting
the natural tier's dense arrays) bounds one pass over the data at ~5–20 ms. Rather than
inflate them with repeated sweeps (which changes the algorithm and lets Blade's deferral or
Fortran's cache-blocking hide the bandwidth question), they are kept single-pass with more
rounds (R >= 11) and the inputonly twin. The charter's "DRAM-resident variant" exists
(the full size); a cache-resident variant is the smoke size and is not timed as a result.

**D16 — `recur` is RK4 of du/dt = u − u³ with the full trajectory as the named output.**
`let rec` always stores the full leading-axis buffer (rec-array windowing is not on master),
so the Fortran tiers must store the trajectory too (the spec names it); an expert that keeps
only the current state would be computing a different output. The expert may fuse the
checksum per step. Blade's step kernel is array-statement RK4 (four stage arrays per step)
— the same temporaries the natural tier has; the expert's scalar-stage loop has none. Hence
the LOSS hypothesis vs expert: this is the control that proves the harness is not rigged.

**D17 — `maskagg` keeps the compaction** (`mask` + `compound` + `reduce`), the style guide's
WHERE idiom, rather than the gated fold `reduce(L <@> lambda(x) -> if x > t then x*x else
0.0, (+))` that examples/01 (g) uses — because the spec names the compacted array y as an
output (as `pack` does), and because the point of the control is the language's filter
path, which is expected to lose. `compound` is top-level-only (BL6002), and the LLVM lane
prints every top-level binding, so this kernel's `blade-llvm` arm is expected to be
unavailable; the `blade-cpp` figure stands in and the table says so. The order of the
compaction is not verified by Σy² (permutation-invariant); the count is. Accepted.

**D18 — `gram` orientation.** The spec's object is G(i,j) = Σ_s X(i,s) X(j,s) with X stored
p x n in Blade (samples contiguous per variable — the only orientation whose rows a
`method_for(X, X)` kernel can dot). Fortran stores x(n, p) (samples contiguous per column:
`matmul(transpose(x), x)` or `dot_product(x(:,i), x(:,j))`), the natural column-major
choice; the expert may transpose or block as it likes. The mathematical G is identical.

**D19 — Eleven kernels, six of them not expected to win.** Four group-A kernels carry the
thesis; group B has two expected wins (fusedchain, onepass) and three controls (stencil,
recur, maskagg); dot and saxpy are the bandwidth controls. Dropping band and recasting
pairkernel left no second "enumeration" kernel; adding one that does not exercise a
compiler-derived structure would only pad the table.

**D20 — comoment3 sizes.** p = 171 and n = 201 are set by the natural tier's dense cube
(171³ x 8 B = 40 MB) and its serial time (3.0 GF ≈ 1–2 s) under the 2 s cap, not by what
Blade's simplex could handle. The finite-n cell ratio is 171³ / C(173,3) = 5.89.

**D21 — The LCG and its seeds (restated after M6/M7).** The generator is the coordinator's
three-round correction of 2026-10-17 (the charter's original two affine rounds composed
into one affine map). Seeds are `10000019 * j` — which makes the arrays NON-OVERLAPPING
SEGMENTS of one sequence (the generator is a function of `k + seed`), not independent
streams; the earlier "never shifted copies" claim is withdrawn in that form. Known bias:
`h2` is even 75% of the time, `h3` odd 75%, affecting only the 2^-31 bit of `u`. Integer
bounds per round: < 2^62 (round 1, with `k + seed < 2^31`), < 2^50 (round 2), < 2^62
(round 3, max ≈ 2.37e18) — the earlier "< 2^56" was wrong. Adequacy for THESE workloads is
measured by `bench/validation/generator_check.py` (blade-B) at full sizes; the generator is
replaced only if something is degenerate. Verified to typecheck as pure Int64 arithmetic in
Blade.

**D22 — Precision of printed values.** Blade prints 15 significant digits; Fortran prints
17. The harness compares relatively; "exact" input agreement means equal to 15 digits. A
strict-arm checksum tolerance of 1e-12 relative is two orders above the 15-digit print
granularity and three above the expected summation-order drift of 1e6–1e7 non-negative
terms.

**D23 — Serial Fortran compiles with `-fopenmp-simd` / `/Qopenmp-simd`.** blade-B found
that Blade's C++ lane passes `-fopenmp` unconditionally and `BLADE_OMP_THREADS=1` removes
only the threading pragmas, so Blade's serial build keeps its `omp simd` hints. The
mirror is simd-only OpenMP for Fortran in the serial regimes: `!$omp simd` honoured (expert
may place it on non-reduction loops), no threading, natural sources unaffected (they carry
no directives). I agree with the coordinator's decision; the alternative — stripping
Blade's simd hints — would require a compiler change, and vectorization hints do not
change results. `!$omp simd reduction` stays `fast`-only.

**D24 — mt8 ratios are against `blade-cpp`.** The LLVM lane has no OpenMP; a `where omp`
licence compiles serial there with no refusal and no note (blade-B). In mt8 the Blade
figure is `blade-cpp`; `blade-llvm` is reported as "serial only (LLVM lane has no
OpenMP)". Serial regimes keep `blade-llvm` as the headline.

**D25 — Contract shape.** `let (checksum, input_checksum) = { let x = inputs(); (kernel(x),
reduce(x, (+))) }` with `inputs` and `kernel` as functions, because the LLVM lane refuses
an application in pack position and the C++ lane ICEs (BL9002) on a deferred map inside a
top-level block (blade-B). Both are register entries.

**D26 — `onepass` has no parallel Blade form and keeps its idiom.** The `<&!>` join cannot
be licensed (README §7.4); the tuple-state fold would thread in the C++ lane but the LLVM
lane refuses it and it is a worse idiom for four plain sums. The mt8 row reads "no parallel
form" rather than switching kernels — that is the finding.

**D27 — Correctness authority is the independent numpy oracle (M11/M12).**
`bench/validation/pins.json` (from `ref_<kernel>.py`) pins `checksum` and `input_checksum`
at smoke and full size; every run of every arm is validated against it; any failure
invalidates the arm's whole block and every dependent ratio; each mt8 arm is gated on its
own validated serial arm. The "every strict arm agrees with every other" wording is replaced
by this oracle-relative rule. Cell-by-cell validation of dump variants at smoke size covers
sign, permutation and compaction order, which sums of squares cannot.

**D28 — Store audit (M10).** No symmetric opaque-consumer boundary exists for Blade, so
materialization of named outputs is verified on the optimized machine code (`objdump -d`)
of every full-size arm before publication; an arm whose stores were eliminated is reported
as "checksum-only computation".

**D29 — Clocks (M14).** All lanes read QPC-backed monotonic clocks after the orchestrator's
runtime change (`std::chrono::steady_clock` in the C++ lane, QPC in the LLVM shim, QPC in
`bench_support.f90`); the previous C++/LLVM clocks were wall-clock. Nonpositive durations
are rejected. The rebuild precedes the pilot.

**D30 — Endpoints (M21).** The region includes allocation and first touch of everything
the program creates; Blade's block scope frees inside the region, Fortran after the stop
line — recorded asymmetry; peak working set recorded per run.

**D31 — `fast` is the lowest-priority regime and is labelled as unequal (M4).** "Vendor
fast-math vs Blade's reassociation licence — unequal permission sets, accuracy-checked at
1e-8"; both Blade lanes reported there (blade-B F11: the C++ lane does not reassociate folds
over deferred computations under the knob; the LLVM lane does).

**D32 — Rounds and sample size (M15).** Seeded random permutation of all arms, twins and
controls each round; whole-round joint bootstrap; minimums enforced (serial 11, memory-bound
21, mt8 7; an override taints the session); a 3-round full-size pilot sets final counts
against a ±5% precision target on the headline ratio.

**D33 — `recur` has Fortran mt8 arms (M5).** The trajectories are independent across x;
Fortran threads them inside the time loop with each trajectory's order preserved; Blade
reads "no parallel form" (G5). `maskagg` is serial "by protocol" (a parallel prefix-sum
pack is a different algorithm), not "by construction".

**D34 — `BLADE_FP_CONTRACT=fast` on every LLVM arm** (blade-A F14): the LLVM lane does no
FMA contraction by default (`src/EmitLlvm.fs:165-184`), which would have put the headline
arm in a different contraction regime from every other arm. Contraction only, no
reassociation — the licence every other arm has. Recorded in the regime table and
`config.json`.

**D35 — Concrete kernel-parameter types** (`Array<Float64 like Nx>` rather than `T^1`) in
gram and comoment3, as antisym and pairkernel already had: the inner trip count is then a
compile-time constant on both sides ("same information"); plan and checksums unchanged.

**D36 — `maskagg` full n = 1,000,003** (from 3,000,017): blade-B found `compound` builds a
hashed index of ~40 B per kept cell (~120 MB, 200–300 ms at the old n). The hashed rank-1
compound index is gap G9; the runner measures peak working set once per kernel at full size.

**D37 — Stencil and recur checksums are the per-row `prodsum` under a licensed fold**
(blade-B): `reduce(reduce(A * A, (+)), (+))` and `reduce(A * A, (+), axes = 2)` materialize
a second output-sized array (72 MB / 96 MB at full size, over the cap), and `omp` on the
`axes = 2` fold kernel is dropped silently (G10).

**D38 — Freeze and inventory (M26).** Sources, sizes, hypotheses, tier selections and
tuning decisions are frozen in a tagged commit before the pilot; the README carries the
inventory of dropped/recast workloads (band; the pair sum; the O(1) antisym) beside the
eleven kernels; controls are in the suite because their behaviour has an independent
explanation, not because they must lose; the recur spec's "a suite that cannot show this
loss is not measuring" is withdrawn.

**D39 — Ownership of the twins.** blade-B wrote and owns the six `inputonly.blade` twins
(one source serving both Blade lanes); Fortran twins are per tier via `-DBENCH_INPUTONLY`
on the same source (FORT).

**D40 — The fp axis is `licensed` / `repro` / `fastmath` (user policy; retires
"strict"/"fast").** Blade's intended default is "always fastest" — every source licence
honoured, including simd reductions on `where omp` folds — and the reproducible serial
association is the opt-in. Today's compiler has the polarity inverted (`BLADE_FP_REASSOC=1`
opts INTO the licensed forms), so the suite measures the intended default through the
knob. `licensed` (HEADLINE): Blade `BLADE_FP_REASSOC=1` (+ `BLADE_FP_CONTRACT=fast` on LLVM
arms); gfortran/ifx = the former strict flags + `-fopenmp-simd`/`/Qopenmp-simd` +
`-DBENCH_LICENSED`/`/DBENCH_LICENSED`, under which the EXPERT tier may place `!$omp simd
reduction(...)` on exactly the fold loops whose Blade counterpart carries a fold licence
(matched permission; natural stays as written). `repro` (secondary): knob off, no
`BENCH_LICENSED`, no reordering in any serial arm; the M3 acceptance check lives here.
`fastmath` (optional, lowest): the former fast definition with its unequal-permissions
label. Order of work: licensed serial, licensed mt8, repro serial, repro mt8, fastmath,
blas. In `licensed` both Blade lanes are reported because the C++ lane's knob does not reach
folds over deferred computations while the LLVM lane's does (blade-B F11; G20). D8, D14,
D23 and D31 are read with "strict" → `repro` and "fast" → `fastmath`; the "fast" wording in
them is historical. Correction (blade-A F15): `licensed` differs from `repro` for every
kernel but maskagg — `prodsum` has no threading slot but is reassociation-licensed, so the
group-A inner dots change form under the knob (4-lane simd reduction in C++, 16-lane FMA
accumulators in LLVM); the earlier "licensed = repro for gram/comoment3/antisym/pairkernel"
is withdrawn, and the matched-permission loops are listed per spec. `blade plan` does not
show the change (G21).

**D40a — The matched-loop table is about the SOURCE licence.** A builtin `(+)` fold in the
Blade source is reorder-licensed even where the C++ lane does not yet honour it (F11; the
LLVM lane marks every such fold `reassoc`; the in-progress compiler flip makes the C++ lane
honour them). Reconciled against the committed sources, the expert's `BENCH_LICENSED`
`simd reduction` stands on dot's fold, fusedchain's fold, onepass's four accumulators,
saxpy's checksum fold, and the checksum sums of stencil, recur and pairkernel; gram,
comoment3 and antisym need none (register-tiled outer-product order, per-cell order kept).
Earlier per-spec lists naming the group-A inner dots are withdrawn.

**D40b — Fortran-review corrections to D40/D40a.** (F2) maskagg's "licensed = repro" was
false: the archived licensed emission reassociates both its output checksum and its input
sum (8 lanes); all eleven kernels differ between the regimes. (F3) The matched-permission
inventory includes EVERY kernel's input-checksum folds (Blade's licensed emission
reassociates them everywhere), with the Blade source's grouping kept (one fold per input
array, per row where rows are summed, combined in the source's order). (F7) Compiler-
generated fusion of generation into the consumer is PERMITTED in every language (it is what
the suite compares); the rules are that inputs are stored (machine-code audit) and that the
SOURCE does not fuse generation into the kernel; the arm record states whether the compiler
fused (ifx does in fusedchain's expert). (F13) "Fortran deallocates after the stop" is
narrowed to retained program arrays; panels and compiler temporaries have their own recorded
lifetimes; the store guarantee is the audit, not source structure. (F14) `serial` means "no
active thread-level OpenMP" — standalone `simd` is active by design on every side; both
drift controls are serial x repro and `config.json` follows the README. (F1) comoment3
divides by n once per cell; permutation-rounding differences in the dense natural arm are
permitted, regrouping the canonical product is not. (F9) antisym's two-matmul spelling is
noted as equally natural and unmeasured.

**D43 — The compiler flip landed; `licensed` is Blade's default, `repro` the opt-in
(supersedes the knob mechanics of D34, D40, D40a).** Commit 5f4b33ea (plan
`docs/plans/plan-fp-default-fastest.md`), merged into the bench branch as 957afd95 together
with master 74b96445. `licensed` = no knob: both lanes honour every source licence (C++
`omp simd reduction` or K-lane accumulators per fold site, LLVM `reassoc nsz`); the LLVM
lane contracts by default, so D34's `BLADE_FP_CONTRACT=fast` is redundant and dropped.
`repro` = `BLADE_FP_REPRO=1` (compatibility spelling `BLADE_FP_REASSOC=0`), which also
serializes the mt8 fold teams (repro x mt8 Blade = threaded maps, serial folds). The F11
C++ deferred-fold gap is fixed (dot's zip fold, the composed chain, a checksum over a
computed map, the 4-leg join all get `SIMD_REDUCTION` by default), so every "blade-cpp
stays serial" hypothesis in the specs is withdrawn; both Blade lanes are still reported.
Gap statuses: G20 FIXED, G21 FIXED (`fold form:` per C++ fold site in `blade plan`), G19
FIXED, G7 PARTLY (simd-reduction join form + census line, no thread form), G14 PARTLY
(prodsum reassociates by default, no threading slot); G24 lists the shapes still without a
fast form. **Freeze state:** bench branch `worktree-bench-fortran` at 957afd95; Blade.exe
hash in the session bundle.

**D44 — mt8 matched parallelism (user decision, 2026-10-18).** Session 2 showed the
Fortran arms threading input generation and the checksum read-back while the group-A
Blade sources threaded only the kernel's level 0; with the checksum pass at 57–70% of the
C++ lane's serial time on comoment3/pairkernel, Amdahl made those mt8 numbers a
comparison of serial checksums, not kernels. Decided: every arm threads preparation and
checksum where the language allows — Blade's four group-A sources gain `where omp`
licences on their input maps' outer level and on their checksum nests/folds (blade-A);
smoke evidence resets; the mt8 group-A blocks are re-run; the superseded numbers stay in
RESULTS.md §5 as an appendix record. Option (B) (no arm threads preparation) and (C) (keep
and label) were rejected: (A) is what the matched-permission rule already said.

**D45 — Isolated-excursion rule (user decision, 2026-10-18).** The always-abort 3x rule
lost the saxpy mt8 block to a single 43 ms stall of a 6 ms input-only twin with both
controls normal. Decided: an isolated single-run excursion (controls normal in that round)
is dropped and recorded (sample kept in the bundle, marked, excluded from inference,
manifest says so); the session aborts on a second excursion in the same block, on any
control excursion, or on AC loss. The harness owner implements it; README §6/§7 updated.
Resume plan: re-smoke → mt8-licensed group A + saxpy → serial-repro 3a/3b → mt8-repro →
serial-fastmath → mt8-fastmath → gram blas.

**D46 — Host rules relaxed to measurement hygiene (user instruction, 2026-10-18).** The
August 2026 crash history behind charter §2 / README §7 was an integrated-graphics
overload, since resolved. The remaining rules are hygiene: one timed process at a time,
interleaved rounds with drift controls, the excursion rule (D45), the free AC check.
Cooldowns are 0.5 s per run, 3 s per kernel, 60 s per session (were 2 s / 10 s / ~5 min
up to and including serial-repro 3b; RESULTS.md §2 lists the values in force per session
because inter-run spacing can change thermal state). The 64 MB working-set and 2.5 s
run caps stay frozen for this run (sizes were chosen under them) but are no longer safety
limits and may be raised in later sessions.

**D42 — mt8 arms run UNBOUND (M20 amended).** libgomp cannot bind threads on Windows
("Affinity not supported", thread count still 8); libiomp5's binding under
`OMP_PLACES=cores OMP_PROC_BIND=close` produced 17–48 ms of jitter at smoke size while
`KMP_AFFINITY=disabled` gives 1.6–1.9 ms (Fortran agent, `notes/fortran-notes.md`). So: no
`OMP_PLACES`/`OMP_PROC_BIND`, `KMP_AFFINITY=disabled` on ifx arms, `OMP_DYNAMIC=FALSE`
kept, thread count verified, placement left to the OS scheduler — recorded as a platform
limitation, not a choice. OpenMP team creation (0.4–0.9 ms) lands inside the timed region
in every threaded arm including Blade's C++ lane; the tables say so. D13's pinning of mt8
threads is superseded; serial-arm pinning stands.

**D41 — Generator variant D (five rounds).** blade-B's full-size checks of the three-round
generator found long-range lag correlations (r = −0.27 at lag 981,991; 570 gram row pairs
beyond 6 sd; non-geometric maskagg run lengths; stencil kurtosis off). Adopted fix: append
`h4 = ((h3 * h3) div 65536) mod 2^31` and `h5 = (h4 * 1103515245 + 12345) mod 2^31`, `u =
h5 / 2^31` (exact: `h3 * h3 < 2^62`). Reference at seed 10000019: h5 = 408748897,
189020261, 1945512958 for k = 0, 1, 2. The measured properties of variant D go in
`notes/generator-check.md` (blade-B); the pins in `validation/pins.json` and every twin are
regenerated from it. D21's bounds gain rounds 4 (< 2^62 before the shift, < 2^46 after) and
5 (< 2^62).

## Open items (not settled here)

- O1 SETTLED (Fortran agent, `notes/fortran-notes.md` §3): ifx 2026.1 `/fp:precise` does
  not contract FMAs and `/Qfma` cannot re-enable it; the strict arm is `/O3 /QxHost
  /fp:precise -mllvm -fp-contract=fast` (contraction on, reassociation off, verified by two
  probes). ifx with no `/fp:` flag defaults to `/fp:fast`, so the flag is always explicit.
- O2 SETTLED (D23): the serial build keeps `omp simd`; Fortran serial gets `-fopenmp-simd`.
- O3 SETTLED for dot/fusedchain (README §7.4, blade-B) and onepass (D26). Per-kernel
  confirmation of an emitted pragma in the mt8 build remains blade-A/B's acceptance check.
- O4 SETTLED (blade-A): it does — in the C++ lane every elementwise op on `T^k` kernel
  parameters is materialized per output cell (`reduce(x * y, (+))`, `reduce(x * y * z,
  (+))`, `prodsum(a - b, a - b)`); the LLVM lane fuses. DECIDED: gram and comoment3 use the
  `prodsum` contraction intrinsic (equally natural, documented); gap register G15.
- FYI outside `bench/`: `tests/fixtures/llvm/bench_sym_gram.blade` and
  `bench_sym_gram_small.blade` no longer typecheck on master (BL3020 on `0.5 * ((i * 31 +
  k) % 7)`, the int/float mixing rule postdates them). Not this suite's to fix.
- Documentation bug (register G17): the style guide's `static struct Band { i: Int<min=0,
  max=n-1> ... }` gives BL4001 with a `let static` bound because `Int` fields are Int32;
  `Int64<...>` fields work.
- O5 LLVM-lane acceptance of each kernel (blade agents); known risks: `let rec` shapes
  (IRForRange), `<&!>` joins, halo windows, `compound`.
- O6 Whether gfortran inlines `matmul` at p = 1601 (Fortran agent).
