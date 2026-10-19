# Kernel authoring guide — Blade vs gfortran / ifx

The rules a kernel author (Blade or Fortran) and the Fortran reviewer need: the arm and tier
definitions, the SIZE marker, the output contract (timed region, generator, twins, oracle)
and the Blade facts the sources rely on. The suite's entry point — goal, fairness
definition, how to build and run, the regime matrix, reporting, honesty rules — is
`bench/README.md`; the driver's manual (verbs, schemas, statistics, session bundle, limits)
is `bench/harness/README.md`; each kernel's authoritative definition is
`kernels/<name>/spec.md`, the kernel inventory `notes/kernel-list.md`, the decision log
`notes/design-decisions.md`, the compiler gaps `notes/gap-register.md`.

Moved here verbatim from the single protocol document on 2026-10-18 (consolidation pass):
§1 below was `bench/README.md` §2, §2 was §4, §3 (3.1–3.4) was §5 (5.1–5.4), §4 (4.1–4.9)
was §7.1–7.9; cross-references were rewritten, no rule was changed except where this file
says "(driver rule, later)": the `input_checksum` agreement rule is the harness's 1e-14
relative (the earlier "equal 15-digit renderings" was fragile at a half-way point), and
the pins live in `validation/pins-A.json` + `pins-B.json`, merged by the harness.

## 1. Arms and tiers

Arms per kernel: `blade-llvm` (headline in serial regimes), `blade-cpp`, `gfortran-natural`,
`gfortran-expert`, `ifx-natural`, `ifx-expert`, plus the `inputonly` twin of each arm where
the spec requires one (§3.3).

**Blade** gets exactly one source, `kernels/<name>/<name>.blade`, written to the style
guide in `CLAUDE.md` (highest rung of the idiom ladder the body admits: implicit lifting >
`method_for(range<...>)` > `let rec`), carrying the `where comm/anticomm/omp` declarations
the kernel's structure calls for and nothing else. The same file is built for every regime
by environment knobs only (README §3). Arrays live inside a function that returns the scalar;
only the two scalar bindings `checksum` and `input_checksum` are at top level.

**natural = the dense baseline.** The arm identifier stays `natural`; what it denotes is
*the literal transcription of the spec with array syntax and intrinsics and dense storage,
no symmetry exploitation* — every logical cell computed independently, distinct arrays for
distinct named intermediates, `!$omp parallel do` (default schedule) on the outermost loop
or `!$omp workshare` around array statements in mt8. It is NOT a claim that this is the
only natural Fortran: an upper triangle with mirrored assignment, a four-accumulator loop, a
single fused loop are all ordinary Fortran too, and the review (M1) is right that the dense
baseline measures "what array syntax with dense storage costs", not "what a competent
programmer would do". Where the intrinsic IS the natural spelling it is kept even when it is
fast: gram's natural arm is `matmul(transpose(x), x)` (the runtime implementation, not a
hand-chosen slower nest). Each spec records how the baseline was selected.

**expert = the reviewed implementation.** The fastest Fortran the Fortran agent produced and
the Fortran reviewer accepted for the same spec without a user-selected external library,
asm or intrinsics: triangular / simplex loops, packed storage with hand offsets, loop
interchange and outer-product ordering, register blocking and panel packing (recorded),
manual fusion of stages and of the checksum into the producing loop, manual jam of several
output cells (independent accumulators for DIFFERENT cells — legal in every regime, since
no cell's summation order changes), cache-aware orientation, `schedule(dynamic)`/`guided`
on triangular loops in mt8. Reordering ONE sum — splitting a cell's accumulator, `!$omp
simd reduction` — is regime-governed (README §3): in `licensed` the expert tier may place `!$omp
simd reduction(...)` on exactly the fold loops whose Blade counterpart carries a fold
licence (the matched permission, under `-DBENCH_LICENSED`); in `repro` no serial arm
reorders anything; `fastmath` is the vendor's blanket permission. Compiler flags are the
regime's, never per kernel. "Expert" never means "proven fastest possible"; it means
reviewed, and the review notes name what was tried.

Examples: `do i = 1, p; do j = i, p` with packed offset `j*(j-1)/2 + i`; `do s; do j; do
i = 1, j: g(i,j) = g(i,j) + x(i,s)*x(j,s)` (vectorizes over i, keeps every cell's
summation order); one `do k` loop computing all four RK4 stages as scalars.

**Vectorization hints.** Blade's C++ lane passes `-fopenmp` unconditionally;
`BLADE_OMP_THREADS=1` removes only the threading pragmas, so `omp simd` hints stay live in
Blade's serial build. The **serial regimes compile Fortran with `-fopenmp-simd` (gfortran)
/ `/Qopenmp-simd` (ifx)**: `!$omp simd` directives honoured, no threading. `!$omp simd` is
allowed on any loop that is not a reduction in every regime; `!$omp simd reduction(...)`
reassociates and is governed by README §3 (`licensed`: on licence-matched fold loops only, behind
`-DBENCH_LICENSED`; `repro`: never; `fastmath`: the compiler's business). Natural sources
carry no directives and are unaffected. (D23, D40.)

**Contract shape of every Blade source (both lanes):**

```blade sketch
let (checksum, input_checksum) = {
    let x = inputs()              // materialized inputs, a function returning the array(s)
    (kernel(x), reduce(x, (+)))   // the kernel's scalar, the input sum
}
```

The LLVM lane refuses `let (a, b) = f()` (an application in pack position) and the C++ lane
ICEs (BL9002) on a deferred map inside a top-level block, so the inputs and the kernel are
functions and the top-level block holds only calls and the tuple.

**mt8 matched parallelism (DECIDED by the user, 2026-10-18; D44).** In mt8 EVERY arm
threads input generation and the checksum read-back where the language allows, as well
as the kernel: Fortran with `!$omp parallel do` on the generator loops and the checksum
sums (as the session-2 sources already did), Blade with `where omp` licences on the input
maps' outer level and on the checksum nests / folds in addition to the kernel's level 0.
The four group-A Blade sources (gram, comoment3, antisym, pairkernel) gain those licences
(blade-A); the "parallel preparation, serial kernel" / "serial checksum included"
asymmetries that session 2 exposed (RESULTS.md §12.1) therefore disappear in mt8, the
smoke evidence resets (harness/README.md "Protocol and statistics" gate), and the mt8 group-A blocks are re-run. Serial regimes are
unaffected (a threading licence is inert there). The parallelism column still reports,
per loop, what was threaded.

**Parallel baselines are never silently excluded (M5).** Where Blade has no parallel form,
the Fortran arms still run mt8 and the Blade cells read "no parallel form": `onepass` (the
`<&!>` join cannot be licensed) and `recur` (the x-trajectories are independent and
ordinary Fortran threads them inside the time loop, each trajectory's arithmetic order
preserved; a `let rec` step has no licence slot). `maskagg` is restricted to sequential
compaction **by protocol** (a parallel prefix-sum pack is a different algorithm under README §1.1),
not because compaction is inherently sequential; it runs serial only in every language.


## 2. Sizes and the SIZE marker convention

Every kernel has a `smoke` size (every arm finishes in ~10 ms) and a `full` size (serial
0.1–1 s where the 64 MB working-set rule allows it; memory-bound kernels are necessarily
shorter — D15), optionally a `scale` size. Sizes are listed in each `spec.md` and copied by
the harness agent into `kernel.json`. No extent is a power of two anywhere.

**The SIZE marker.** The committed sources are runnable programs at FULL size. Every source
declares each size parameter exactly once, at the top, on a line that ends with the comment
marker `SIZE <param>`:

```
Blade:    type Sx = Idx<6006>                 // SIZE n
          let static n = 6006                 // SIZE n       (either form; one per parameter)
Fortran:  integer, parameter :: n = 6006      ! SIZE n
```

Rules:

- The marker is the LAST thing on the line; `<param>` is the parameter's name as the spec
  spells it (`n`, `p`, `nt`, `nx`, `ny`). One marked line per parameter per source file.
- The marked line contains exactly ONE integer literal, and that literal IS the parameter's
  full-size value; the harness rewrites that literal (and nothing else in the file) to
  produce the smoke (or scale) variant in a scratch copy. It REFUSES a line with more than
  one integer literal, a parameter marked twice, a marker it cannot parse, or a literal that
  does not equal the `full` value in `kernel.json` (M28).
- **Derived sizes are never marked and never written as second literals.** Anything that
  depends on a parameter is spelled as an expression of the marked declaration:
  `Idx<n>` from `let static n`, `Int64<min=0, max=n-1>`, `Int64(extents(range<X>))`,
  Fortran `integer, parameter :: ncells = n*(n+1)/2`, `(nx-2)*(ny-2)`.
- The Blade source stem must equal the kernel directory name (the timing line reads
  `<name> completed in`); the Fortran support module prints the same text from the name
  passed in.


## 3. The output contract

Every arm, every language, prints exactly these three lines on stdout and nothing else:

```
<name> completed in <t>s
checksum = <value>
input_checksum = <value>
```

### 3.1 The timed region and its endpoints

- `<t>` is wall time from a **monotonic, QPC-backed clock in every lane** (M14): Blade's C++
  lane `std::chrono::steady_clock`, the LLVM shim's `blade_now` QueryPerformanceCounter
  (`clock_gettime(CLOCK_MONOTONIC)` off Windows) — a runtime change in `src/`, rebuilt
  before the pilot — and Fortran QueryPerformanceCounter through `bench_support.f90`
  (ifx's `system_clock` has 1 ms granularity and is not used). The harness rejects a
  nonpositive duration as an invalid run.
- The region STARTS before any allocation and ENDS after the last checksum value is
  computed; it includes allocation and first touch of everything the program creates
  (inputs, named outputs, temporaries, packed panels, and OpenMP team creation in every
  threaded arm — 0.4–0.9 ms, Blade's C++ lane included), input generation, the kernel, `checksum` and `input_checksum`. It excludes
  process start-up and result printing. **Destruction endpoint (M21, F13):** Blade's block
  scope frees its function-local arrays before its stop line (a few `free` calls inside the
  region); Fortran's RETAINED program arrays are deallocated after the stop line, while
  worker panels and compiler temporaries have their own lifetimes (recorded per arm). This
  asymmetry is recorded, not hidden; the peak working set (live data including temporaries,
  distinguished from process RSS) is recorded per run. Whether an output was actually
  stored is established by the machine-code audit (README §1.5), not by where a source declares
  or frees it.
- Blade emits the timing line itself around the whole program body. Blade is run with
  `--print checksum,input_checksum`; in the C++ lane that is exactly what prints. **The
  LLVM lane ignores `--print`/`BLADE_PRINT` and prints every top-level binding** (a compiler
  finding, recorded, not worked around): sources keep every array inside a function and
  expose only the two scalars at top level; the harness tolerates only the documented LLVM
  scalar extras, rejects duplicate contract lines, and treats any other output as a
  contract violation.
- Floats: Fortran prints 17 significant digits (`es24.17`); Blade prints 15. The harness
  parses both as doubles; agreement of `input_checksum` means **within 1e-14 relative**
  (driver rule, later: it replaced "equal after rounding to 15 significant digits", which
  is fragile at a half-way point).

### 3.2 Input generation

Every input cell is a pure function of its linear index `k` (0-based, row-major over the
mathematical object's indices as the spec lists them) and a per-array `seed`; five rounds
modulo 2^31 (generator **variant D**), exact in Int64 / `integer(8)` in every language:

```
h1 = ((k + seed) * 1103515245 + 12345) mod 2147483648
h2 = (h1 * (h1 div 4096 + 1)) mod 2147483648      # div = truncating integer division; all operands non-negative
h3 = (h2 * 1103515245 + 12345) mod 2147483648
h4 = ((h3 * h3) div 65536) mod 2147483648         # the square-and-shift round that breaks the long-range lag structure
h5 = (h4 * 1103515245 + 12345) mod 2147483648
u  = h5 / 2147483648.0                              # in [0, 1)
value = lo + (hi - lo) * u                          # the spec gives lo, hi per input
```

- **Why five rounds.** blade-B's full-size checks of the three-round form (variant C) found
  long-range lag correlations — r = −0.27 at lag 981,991, visible as 570 gram row pairs
  beyond 6 sd, non-geometric `maskagg` run lengths and a `stencil` kurtosis off its
  expectation (`notes/generator-check.md`, blade-B). Variant D appends the square-and-shift
  round `h4` (an index-dependent, non-affine mixing step) and one more affine round.
  `notes/generator-check.md` (blade-B) records the before/after table at the specs' full
  sizes: the three-round form's five defects against variant D, which measures at the level
  of a PCG64 reference on every statistic checked; the generator is adopted on that record,
  which is archived with every session.
- **Integer bounds (M7), per round:** round 1 < 2^62 provided `k + seed < 2^31` (every
  size in this suite keeps `k + seed` far below that; the harness checks it from
  `kernel.json`); round 2 < 2^50 (`h1 < 2^31`, `h1 div 4096 + 1 <= 2^19`); round 3 < 2^62
  (maximum ≈ 2.37e18); round 4: `h3 * h3 < 2^62`, the shift leaves < 2^46 before the mod;
  round 5 < 2^62. All fit signed 64-bit. `bench_support.f90` masks and shifts where Blade
  spells `%` and `/`; the two are the same function on this domain.
- **Seeds** are `10000019 * j` for the j-th input array of a kernel (j = 1, 2, ...). This
  does NOT make the arrays independent streams: the generator is one function of `k +
  seed`, so the arrays are **non-overlapping segments of one sequence** (array j at
  index k equals array 1 at index k + 10000019·(j−1); the segments never overlap at this
  suite's sizes). Known bias (M6): over a uniform `h1`, `h2` is even 75% of the time and
  `h3` odd 75% of the time; the square-and-shift round and the final affine round
  redistribute the low bits, and `generator-check.md` reports the residual low-bit
  statistics of `h5`. Whether the generator is adequate for THESE workloads is measured,
  not assumed:
  `bench/validation/generator_check.py` (numpy, blade-B) reports, at the specs' full sizes,
  cross-array correlation, row- and column-stride autocorrelation of the 2-D inputs,
  `maskagg`'s acceptance rate and run-length distribution, the `stencil` output
  distribution and `pairkernel`'s distance histogram; the generator is replaced only if
  something is degenerate, and the report is archived with the session.
- **Power-of-two range widths.** `value = lo + (hi − lo)·u` is a multiply-add and the lanes
  differ on FMA contraction; it is immune exactly when `(hi − lo)·u` is exact, i.e. `(hi −
  lo)` has at most 22 significant bits — in practice a power of two. Every spec uses `[0,
  1)` or `[0, 2)`; `[0.1, 1.0]`-style ranges are forbidden. Individual inputs are therefore
  bit-identical across arms; their SUM is not necessarily exact (it is compared to 1e-14
  relative, §3.4). Reference values for seed 10000019 (variant D): k=0 → h5 = 408748897,
  k=1 → 189020261, k=2 → 1945512958 (intermediate h3 for k=0 is 184246361, the old variant
  C reference, which no longer is the output).
- All input ranges have `lo >= 0`, so `input_checksum` (the plain sum of all input cells
  over all input arrays) is a sum of non-negative terms. A sum cannot detect a permutation,
  a transposition or an exchange of equal-shaped arrays (M11); that is the oracle's job
  (§3.4), not the checksum's.
- Input generation may be threaded in mt8 arms (a licence on the generating kernel in
  Blade, a pragma in Fortran) and is materialized (README §1.5). Threaded generation is never
  evidence of a threaded kernel (M24): parallelism is recorded separately for generation,
  kernel and checksum, and a table cell whose kernel loop is unthreaded reads "parallel
  preparation, serial kernel".

### 3.3 Input-generation twins and the "incremental" estimate

Input generation is inside the timed region, and its cost is NOT assumed equal across arms
(M8: masking vs remainder, vectorization, constant propagation all differ). Every arm of a
kernel whose spec marks the input share above 5% — `fusedchain`, `onepass`, `stencil`,
`maskagg`, `dot`, `saxpy` — has its **own `inputonly` twin**: the same source family with
the kernel and checksum omitted and `checksum = 0.0` printed (verified). Fortran twins are
built per tier from the same source with `-cpp -DBENCH_INPUTONLY` / `/fpp /DBENCH_INPUTONLY`;
`kernels/<name>/inputonly.blade` (blade-B) serves both Blade lanes. Each owner inspects the
optimized generation loop of total and twin once and records vectorization differences and
that the inputs stay materialized in both.

**The complete timed region is the PRIMARY estimand** (M9). The twin-based quantity is the
**incremental total-minus-twin estimate**: `median(total) − median(own twin)`, reported as a
difference with its interval FIRST and as a ratio second, and never called "kernel time" or
"net" — it also contains the output allocation and first touch, the checksum pass's cache
state, and whatever generation/fusion changes removing the consumer caused. A kernel-only
claim is not made by this suite.

### 3.4 Correctness: the oracle and the tolerances

- **The correctness authority is an independent reference**, not another arm (M12):
  `bench/validation/ref_<kernel>.py` (numpy, blade agents; independent of Blade and
  Fortran) regenerates the inputs, computes the full output, and writes the pins —
  `checksum` and `input_checksum` at smoke AND full size — to `bench/validation/pins-A.json`
  / `pins-B.json` (group A / group B authors; the harness merges them and also accepts a
  single `pins.json`; a duplicate entry must agree).
  Every run of every arm (warm-ups, rounds, controls, twins) is validated against the pins:
  `licensed` and `repro` arms to **1e-12 relative** on `checksum` (licensed reordering of
  sums of non-negative terms stays far inside it), `fastmath` arms to **1e-8** (flagged);
  `input_checksum` to **1e-14 relative** in every arm (driver rule, later; was 15 significant digits). The rule is **oracle-relative**:
  an arm is valid iff it agrees with the pins; arms are never compared pairwise.
- **Any correctness failure invalidates that arm's entire block** (all its samples, not the
  failing one) and **every ratio that uses it**; the table shows `DISQUALIFIED (rel err)`
  in that arm's rows and `undefined (reference DQ)` in dependent cells. Each mt8 arm is
  gated on its own validated serial arm.
- **Cell-by-cell validation at smoke size** (M11): dump variants
  (`bench/validation/dump/<kernel>_dump.blade`; Fortran `-DBENCH_DUMP` writes the array)
  are compared against the numpy output cell by cell by `validate.py` for the Blade C++
  lane and both gfortran tiers before a session. This is what catches a sign flip
  (`antisym`), a permutation, a wrong compaction order (`maskagg`) or compensating errors
  (`onepass`) that a sum of squares cannot. At full size the pins plus the store audit
  (README §1.5) are the evidence.
- **Checksums** are order-insensitive up to the tolerance and non-cancelling: sums of
  squares of the named output over its FULL logical index space, a count added as a float,
  or the kernel's own non-negative scalar. For symmetric objects Blade reads all logical
  cells through its canonicalizing accessor and the expert tier reads its triangle with
  weight 2 — the same value. This costs Blade more than the triangle read (gap register G3);
  **the total is the result and nothing is subtracted from it** (M13): the earlier
  "checksum tax" estimates in the specs are planning aids, labelled so, never a correction.


## 4. Blade facts every kernel author relies on (4.1–4.9 probed 2026-10-17, this binary)

- 4.1 Symmetric storage and triangular iteration are derived only for the SAME ARRAY in
  adjacent commuting positions: `method_for(A, A) <@> lambda(x, y) where comm(x, y)`.
  `method_for(range<I>, range<I>) ... where comm(i, j)` emits a dense square (probed).
- 4.2 A fold over compact storage is refused (BL3999) in every spelling (`reduce`, `prodsum`,
  reduction-join legs, `range<SymIdx<...>>` kernels); a partial read `S(i)` of a compact
  group is refused; `decompact` allocates the dense n x n. The checksum spelling that
  streams is the per-row nest: `reduce(method_for(range<I>) <@> lambda(i) ->
  reduce(method_for(range<I>) <@> lambda(j) -> S(i, j) * S(i, j), (+)), (+))` (one n-vector
  allocated, n² canonicalizing reads). For DENSE rank-2 outputs (stencil's `lap`, recur's
  `traj`) the streaming spelling is the per-row `prodsum` under a licensed fold:
  `reduce(method_for(lap) <@> lambda(row) -> prodsum(row, row), lambda(p, q) where omp -> p +
  q, 0.0)` — `reduce(reduce(lap * lap, (+)), (+))` and `reduce(lap * lap, (+), axes = 2)`
  both MATERIALIZE a second array the size of the output (blade-B), which at full size
  breaks the 64 MB cap.
- 4.3 `compound(dense, mask)` is valid only as a top-level `let` (BL6002 inside a function),
  and builds a hashed index of ~40 B per kept cell (blade-B) — the reason `maskagg`'s full
  size is n = 1,000,003.
- 4.4 Fold licences (blade-B): the spelling that threads a dot is `reduce(method_for(zip(a,
  b)) <@> lambda(x, y) -> x * y, lambda(p, q) where omp -> p + q, 0.0)` bound to its own
  `let` (`reduce(a * b, lambda ... where omp -> ..., 0.0)` also threads but materializes
  `a * b`); `omp` on the zip MAP feeding `(+)` emits serial; a bare `where omp` fold in
  expression position is emitted serial with a census note; `omp` on a leg of a `<&!>`
  join is emitted serial ("fold accumulates into a shared scalar") and `omp` on the join's
  fold kernel is DROPPED SILENTLY (a finding against the "dropped omp is never silent"
  invariant); so is `omp` on an `axes = 2` fold kernel. There is no licensed parallel form
  of a `<&!>` join.
- 4.5 Lifted lets (`let s1 = 1.5 * a + 0.25`) are materialized, one temporary per `let`,
  never fused across lets (blade-B). Fusion into the consuming fold happens through an
  `object_for(f) >>@ object_for(g)` chain (accepted inside functions in both lanes), a
  single block kernel, or an inline fold operand. A named output is forced with `|>
  compute` regardless — and then audited in the machine code (README §1.5).
- 4.6 `blade plan` names what to check: `[symmetric-storage v1] ... applied [packed
  SymSymmetric|SymAntisymmetric storage ...]`, `[triangular-iteration v1] ... applied
  [... speedup x2]`, `[omp v1] ... applied [BLADE_OMP_PARALLEL_FOR(_DYNAMIC); loop nest
  level 0]` — per LOOP, so the record says whether the input map, the kernel or the
  checksum is the threaded one — `[blas-routing]`, `[pool-reuse v1]`, and the
  fusion/CSE/hoist decisions.
- 4.7 The LLVM lane: no thread runtime (an `omp` licence is honoured as fold-FMF
  reordering by default, never as threads — G11); contracts FMAs by default since
  5f4b33ea (G19 fixed; F14 is history); ignores `--print`/`BLADE_PRINT`; refuses
  `compound` (maskagg is `blade-cpp`-only; the fallback honours `--print`); refuses `let
  (a, b) = f()` (application in pack position); refuses a `T^1 -> T^1` row map in value
  position ("IRApplyCombinator in value position"); refuses the tuple-state fold; accepts
  `reduce(m, (+), axes = 2)` (the charter's note is outdated).
- 4.8 Kernel parameters (blade-A): in the C++ lane any elementwise operation on `T^k`
  kernel parameters (`x * y`, `a - b`, `x * y * z`) is materialized as a length-n temporary
  per output cell; the LLVM lane fuses it. `prodsum` is the contraction intrinsic that
  streams in both lanes — gram and comoment3 use it. Literal subscripts into `T^k`
  parameters carry a BL8006 check per read even when cast `(0 : Cx)`; concretely typed
  parameters (`Array<Float64 like Cx, Nx>`) read without the guard and make the inner trip
  count a compile-time constant (README §1.2) — every group-A kernel types its parameters
  concretely.
- 4.9 Correctness hazard (blade-A; gap register G16): `R * R` with R antisymmetric is typed
  and stored antisymmetric, so mirrored reads come back negated — a silent wrong value in
  both lanes and the interpreter. No suite kernel squares an antisymmetric array
  elementwise; do not introduce one.
- 4.10 Spelling facts from the licence work (blade-A §14, D44): a threading licence on a
  checksum ROW MAP is silently dropped — only the fold-kernel placement threads (G27) — and
  a licensed fold inside a result tuple opens no team (bind it to its own `let`); the
  threaded checksum folds are Path B, serial within each chunk (G25).
