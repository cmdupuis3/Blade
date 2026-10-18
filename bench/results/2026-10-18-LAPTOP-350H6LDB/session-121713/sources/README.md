# Blade vs Fortran benchmark suite — the protocol

This document supersedes the provisional parts of `notes/charter.md` (sections 4–7 there).
Where the two disagree, this file wins; where this file is silent, the charter stands
(host safety rules, toolchain facts, directory layout, ownership). The kernel list is
`notes/kernel-list.md`; each kernel's authoritative definition is `kernels/<name>/spec.md`;
every decision made here that the charter left open is argued in `notes/design-decisions.md`;
the compiler gaps the suite exposes are in `notes/gap-register.md`. The independent
methodology review is `notes/review-method-codex.md`, its dispositions
`notes/review-method.md`; this revision of the protocol incorporates every item there.

## 1. Goal and the fairness definition

The claim under test: **Blade, from ONE idiomatic source per kernel, is at least as fast as
reviewed expert Fortran on kernels where Blade's structure-first design derives the
optimization from declarations** (packed symmetric/antisymmetric storage and triangular
iteration, derived parallelism, fusion by deferral, one-pass multi-accumulator folds) — and
no slower than Fortran on plain kernels. Results are reported whichever way they go; a loss
with a diagnosis is a result, a win against weak Fortran is a defect of the suite, and an
unexpected control win is investigated, not explained away.

A comparison is fair when all of the following hold:

1. **Equivalent computation.** Equivalence is defined by (a) the required outputs the spec
   names, (b) the arithmetic dependencies (which values are combined, in which per-cell
   order — the spec fixes the per-cell summation order so `repro` arms agree to the last
   bit and `licensed` arms differ only by licensed reordering), and (c) the permitted
   symmetry reduction: computing and storing only canonical
   cells of a symmetry the spec declares is allowed in every arm (it is what Blade derives
   from `where comm/anticomm` and what expert Fortran writes by hand). Loop order, blocking,
   packing, jamming several output cells, fusing the checksum into the producing loop, and
   the storage orientation (row- vs column-major — ordinary implementation freedom, not a
   concession) are open to every arm; Blade's are made by its compiler. **Excluded:**
   algebraic shortcuts that compute only the checksum without producing the named output
   (e.g. a norm identity instead of the matrix), and any mathematically different
   algorithm. Extra packing work and memory (panel copies, packed temporaries) are
   legitimate and are recorded per arm (peak working set, §6).
2. **Same information.** Extents are compile-time constants on both sides (`type I = Idx<N>`
   / `integer, parameter :: n = N`), including the trip counts of kernel-internal loops
   (Blade kernel parameters are typed concretely, `Array<Float64 like Obs>`, not `T^1`, so
   the inner extent is a constant in both languages).
3. **Bit-identical inputs** generated in-program by the LCG of §5, verified against the
   independent oracle (§5.4) by `input_checksum`; **identical answers** verified by
   `checksum` against the same oracle to the regime's tolerance, on every run.
4. **Same optimization class, same floating-point regime, same threading regime** (§3), no
   per-kernel flags, no inline asm, no vendor intrinsics. **Native** means *no
   user-selected external BLAS*; compiler intrinsic and math runtimes (libgfortran's
   `matmul`, Intel's runtime `matmul`, SVML, the platform libm) are allowed and DISCLOSED per
   arm (§3, routing audit).
5. **Materialization parity (revised after the Fortran review, F7).** Inputs are STORED
   arrays in every arm (Blade forces them with `|> compute`; Fortran allocates and fills
   them) and the SOURCE does not fuse generation into the kernel — but compiler-generated
   fusion is permitted in every language (ifx fuses generation + kernel + input sum in
   fusedchain's expert while retaining the stores; that is what the suite compares). Named
   array outputs (each spec lists them) are stored in every arm; an arm may fuse the
   checksum into the producing loop but the stores must happen. Scalar outputs and unnamed
   intermediates may be fused freely (that IS the fusion claim). Source-level evidence is not
   proof: before publication every arm at full size passes a **store audit** (`objdump -d`
   of the optimized executable: inputs and named outputs are stored; recorded per arm,
   together with whether the compiler fused generation into the consumer). An arm whose
   compiler eliminated the stores is reported as "checksum-only computation" and its spec
   says so. The guarantee against eliminated stores is this audit, never source structure.
6. **Explicit endpoints (§5.1).** The timed region includes allocation and first touch of
   everything the program creates; destruction is a recorded asymmetry (Blade's block scope
   frees its arrays inside the region; Fortran's retained program arrays are deallocated
   after the stop line, while worker panels and compiler temporaries have their own
   lifetimes, recorded per arm).

## 2. Arms and tiers

Arms per kernel: `blade-llvm` (headline in serial regimes), `blade-cpp`, `gfortran-natural`,
`gfortran-expert`, `ifx-natural`, `ifx-expert`, plus the `inputonly` twin of each arm where
the spec requires one (§5.3).

**Blade** gets exactly one source, `kernels/<name>/<name>.blade`, written to the style
guide in `CLAUDE.md` (highest rung of the idiom ladder the body admits: implicit lifting >
`method_for(range<...>)` > `let rec`), carrying the `where comm/anticomm/omp` declarations
the kernel's structure calls for and nothing else. The same file is built for every regime
by environment knobs only (§3). Arrays live inside a function that returns the scalar;
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
simd reduction` — is regime-governed (§3): in `licensed` the expert tier may place `!$omp
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
reassociates and is governed by §3 (`licensed`: on licence-matched fold loops only, behind
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
smoke evidence resets (§6 gate), and the mt8 group-A blocks are re-run. Serial regimes are
unaffected (a threading licence is inert there). The parallelism column still reports,
per loop, what was threaded.

**Parallel baselines are never silently excluded (M5).** Where Blade has no parallel form,
the Fortran arms still run mt8 and the Blade cells read "no parallel form": `onepass` (the
`<&!>` join cannot be licensed) and `recur` (the x-trajectories are independent and
ordinary Fortran threads them inside the time loop, each trajectory's arithmetic order
preserved; a `let rec` step has no licence slot). `maskagg` is restricted to sequential
compaction **by protocol** (a parallel prefix-sum pack is a different algorithm under §1.1),
not because compaction is inherently sequential; it runs serial only in every language.

## 3. The regime matrix

| axis | regime | Blade (knobs on the one source) | gfortran 16.2 | ifx 2026.1 |
|---|---|---|---|---|
| threads | `serial` = **no active thread-level OpenMP** (standalone `simd` is active by design on every side) | `BLADE_OMP_THREADS=1` at compile (threading pragmas removed; `omp simd` hints stay live) | `-fopenmp-simd`, no `-fopenmp` | `/Qopenmp-simd`, no `/Qopenmp` |
| threads | `mt8` | `BLADE_OMP_THREADS` unset; the source's `where omp(...)` licences emit pragmas; `OMP_NUM_THREADS=8`. **C++ lane only: the LLVM lane has no thread runtime** (`src/EmitLlvm.fs:3037`, `:1004-1023`: by documented design an `omp` clause is "a licence to parallelize, not a demand" — the lane emits the serial body and consumes the same licence for fold reassociation (`reassoc nsz`, now by default); the shim says "this lane has no threads"). So the LLVM arm is serial in every threads regime and the mt8 Blade figure is `blade-cpp`; `blade-llvm` is listed as "serial only (LLVM lane has no threads)" | `-fopenmp`; author places `!$omp parallel do` as §2 says; `OMP_NUM_THREADS=8` | `/Qopenmp`, same |
| fp | `licensed` (HEADLINE) | **Blade's DEFAULT — no knob** (compiler flip 5f4b33ea, merged into the bench branch as 957afd95; `docs/plans/plan-fp-default-fastest.md`): both lanes honour every source licence — C++ lane `omp simd reduction` or K-lane accumulators per fold site, LLVM lane `reassoc nsz`; the LLVM lane now CONTRACTS by default too, so the earlier `BLADE_FP_CONTRACT=fast` on LLVM arms (D34) is redundant and not set. `BLADE_MARCH` default (`native`). Both Blade lanes are reported (they differ in lane width and FMA use, not in which folds are licensed) | `-O3 -march=native -ffp-contract=fast -fopenmp-simd -DBENCH_LICENSED` (NO `-ffast-math`): under `BENCH_LICENSED` the EXPERT tier may place `!$omp simd reduction(...)` on exactly the fold loops whose Blade counterpart carries a fold licence — source-licensed reordering on both sides; natural stays as written | `/O3 /QxHost /fp:precise -mllvm -fp-contract=fast /Qopenmp-simd /DBENCH_LICENSED` (ifx `/fp:precise` does not contract FMAs and `/Qfma` cannot re-enable it — `notes/fortran-notes.md` §3; a bare ifx is `/fp:fast`, so the flag is never omitted); same `BENCH_LICENSED` rule |
| fp | `repro` (secondary) | **`BLADE_FP_REPRO=1`** (compatibility spelling `BLADE_FP_REASSOC=0`): no reordering in any serial arm — AND, new semantics, repro also serializes the mt8 FOLD TEAMS, so `repro x mt8` Blade = threaded maps, serial folds (Fortran repro-mt8 keeps its thread-level `reduction(+:)`, which reorders across threads: a recorded asymmetry of the regime, not of the suite). The M3 acceptance check (no `*_SIMD_REDUCTION` expansion, no `reassoc` in the `.ll`) applies HERE | `-O3 -march=native -ffp-contract=fast -fopenmp-simd` (no `BENCH_LICENSED`: no `simd reduction` anywhere) | `/O3 /QxHost /fp:precise -mllvm -fp-contract=fast /Qopenmp-simd` |
| fp | `fastmath` (optional, lowest) | Blade default (Blade has no blanket fast-math; its licence set is the `licensed` one) — labelled unequal, below | `-O3 -march=native -ffast-math` | `/O3 /QxHost /fp:fast` |
| blas | `native` (headline) | `BLADE_BLAS=0` (explicitly, because `OPENBLAS_DIR` is set on this host) | no user-selected external BLAS: `-fno-external-blas -fno-external-blas64` explicit; `-finline-matmul-limit` left at default and RECORDED (whether `matmul` was inlined or called libgfortran at each size) | no user-selected external BLAS; whether `/Qopt-matmul` is on by default in 2026.1 and where `matmul` routes RECORDED; `/Qparallel` and `/Qpar` absent, verified by the observed thread count |
| blas | `blas` (gram only) | `blade-cpp` only: `BLADE_BLAS=1` (auto-routed `dsyrk`); `blade-llvm` recorded as "no-blas" (the lane has no BLAS route, G23) | `gfortran-expert` calls OpenBLAS `dsyrk` explicitly, linked against `OPENBLAS_DIR`; the natural arm is EXCLUDED here (no library route; its numbers are in the native regime) | `ifx-expert` same; natural excluded. mt8 in this regime is LIBRARY threading on both sides (`OPENBLAS_NUM_THREADS=8`, no user pragmas), labelled "library threads" in the parallelism column |

**`licensed` differs from `repro` for ALL ELEVEN kernels** (blade-A F15, blade-B F11, and
the Fortran review's F2: the archived licensed emission of `maskagg` has 8-lane accumulators
for both its output checksum and its input sum, so the earlier "maskagg is the one
coincidence" was false — every kernel's input-checksum fold reassociates by default): the
inner `prodsum` of gram, comoment3 and antisym has no threading slot but IS a
reassociation-licensed fold (C++ lane `BLADE_OMP_SIMD_REDUCTION(+:__ps)` → `vmulpd`+`vaddpd`
into one 4-lane accumulator, no FMA fused by g++; LLVM lane `fadd reassoc`, four ymm FMA
accumulators = 16 lanes; packed FMAs licensed vs repro: gram 44/0, comoment3 16/0, antisym
42/0), every `(+)` checksum fold becomes a simd-reduction loop, and serial arms are
affected too. Since the flip, `blade plan` prints a `fold form:` line per C++ fold site
(G21 fixed), and the regime record cites both the plan and the emit. Each spec lists the
exact Fortran loops on which the expert may place the matched `simd reduction`.

**History (the polarity question, D40, G20 — fixed by 5f4b33ea).** Until the flip the
licensed forms were knob-gated behind `BLADE_FP_REASSOC=1` and the C++ lane's knob did not
reach folds over deferred computations (blade-B F11: dot's zip fold, the composed chain,
a checksum over a computed map, the 4-leg join), so the two Blade lanes differed in exactly
the kernels where fusion by deferral is the claim. Both are now default-on in both lanes;
the user's policy ("always fastest by default, reproducible association as the opt-in") is
the compiler's behaviour, and `repro` is the opt-in.

**Matched-loop table (reconciled against the committed Blade sources).** The matching rule
is about the SOURCE licence: a builtin `(+)` fold in the Blade source is reorder-licensed,
and since the flip both lanes honour it by default. Under
`BENCH_LICENSED` the expert's `!$omp simd reduction` therefore stands on exactly: **dot**'s
fold; **fusedchain**'s fold; **onepass**'s four accumulators; **saxpy**'s checksum fold;
the checksum sums of **stencil**, **recur** and **pairkernel**; **maskagg**'s output
checksum sum (the compaction itself stays sequential); and — in EVERY kernel, the eleven
included — the **input-checksum folds** (Blade's licensed emission reassociates them
everywhere, A and B evidence), keeping the same grouping the Blade source has: one fold
per input array, and per row where the source sums rows, combined in the source's order.
**The kernel loops of gram, comoment3 and antisym get none**: the expert's register-tiled
outer-product order keeps each cell's summation order and has no reduction loop to license.
The Fortran reviewer audits the sources against this list; the record names, per arm, the
loops reordered.

The fp axis in one sentence (user policy, D40): Blade's intended default is "always
fastest" — every source licence honoured — and the reproducible serial association is the
opt-in; `licensed` measures that default against Fortran given the SAME permission (a
`simd reduction` only where the Blade source licenses one), `repro` measures the opt-in
(no reordering anywhere), and `fastmath` measures the vendor's blanket permission. The
`fastmath` regime is labelled in every table **"vendor fast-math vs Blade's reassociation
licence — unequal permission sets, accuracy-checked at 1e-8"**: GNU/Intel fast-math also
permits reciprocal transformations and finite-only assumptions (relevant to `fusedchain`'s
division) that Blade's knob does not; flag names establish no equivalence. It is run last
and never carries a headline claim.

Routing audit for the `blas` regime: actual `dsyrk` routing verified on both sides from the
BUILT ARTIFACT (Blade: imports / call sites of the executable — `blade plan` prints
`[blas-routing v1] applied` even for the LLVM lane, which has no BLAS route, G23; Fortran:
link map / call), identical OpenBLAS DLL by hash,
same integer ABI, `OPENBLAS_NUM_THREADS=1` in serial and the library's effective thread count
recorded in mt8. For `native`, every arm's record names the intrinsic/runtime routes it took
(matmul route, vector-math calls such as scalar `exp` vs SVML for `pairkernel`), and the
resolved `-march=native` / `/QxHost` target features are archived — the two are reasonable
native-targeting choices, not an identical tuning contract.

Common to all Blade arms: `BLADE_LLVM=1` + `BLADE_LLVM_CLANG=C:\msys64\clang64\bin\clang.exe`
for `blade-llvm`, `BLADE_LLVM` unset for `blade-cpp`; `BLADE_EXE_CACHE=0`; all optimizer
gates at their defaults; `BLADE_PRINT` via `--print checksum,input_checksum`. Every knob the
harness sets comes from the authoritative `harness/config.json`; a missing configuration is
a refusal, never a silent default (M3).

Threads and placement (M20, amended by the Fortran agent's probes, `notes/fortran-notes.md`;
D42): mt8 arms run `OMP_NUM_THREADS=8 OMP_DYNAMIC=FALSE` and are **UNBOUND** — no
`OMP_PLACES`, no `OMP_PROC_BIND`, and `KMP_AFFINITY=disabled` for ifx arms. This is a
platform limitation, recorded as such: libgomp cannot bind threads on Windows ("Affinity
not supported"; the thread count is still 8), and libiomp5's binding under
`OMP_PLACES=cores OMP_PROC_BIND=close` produced 17–48 ms of jitter at smoke size where
`KMP_AFFINITY=disabled` gives 1.6–1.9 ms. The actual thread count is verified once per
OpenMP runtime and recorded; placement is the OS scheduler's. Serial arms are pinned to one
physical core (affinity mask set on the child at spawn, recorded) with its SMT sibling
idle. **OpenMP team creation lands inside the timed region in every threaded arm** (0.4–0.9
ms; Blade's C++ lane included) — a cost all mt8 arms pay alike and the tables note. Every
sample is a fresh process: the measurements are **fresh-process, allocation-inclusive**
numbers (no warm heap, no retained thread pool), and they say so.

Repro-regime enforcement (M3): a `repro` serial Blade build FAILS acceptance if the emitted
`.cpp` contains any `BLADE_OMP_*SIMD_REDUCTION` expansion or the `.ll` carries `reassoc`
flags (the serial C++ build still passes `-fopenmp`, so the check is on the emission, not
the flag line); a `repro` Fortran build fails it if any `simd reduction` directive is
active. In `licensed`, the SAME check is inverted into a matching audit: the Fortran expert's
`simd reduction` placements must correspond one-to-one to the Blade source's fold licences
(listed per kernel in `notes/llvm-acceptance-*.md`), and the record names, per arm, which
fold loops were reordered. An OpenMP `reduction(+:)` (Fortran) and a Blade fold licence both
reorder a sum across threads; both are allowed in every `mt8` regime, and the 1e-12
tolerance absorbs it.

Per-kernel regime applicability is in `notes/kernel-list.md` and each `spec.md`. Order of
work: `licensed x serial x native` for every kernel, then `licensed x mt8`, then `repro x
serial`, `repro x mt8`, then `fastmath`, then `blas`. Threaded arms run only after the
serial arms of the same kernel and regime have validated.

## 4. Sizes and the SIZE marker convention

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

## 5. The output contract

Every arm, every language, prints exactly these three lines on stdout and nothing else:

```
<name> completed in <t>s
checksum = <value>
input_checksum = <value>
```

### 5.1 The timed region and its endpoints

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
  stored is established by the machine-code audit (§1.5), not by where a source declares
  or frees it.
- Blade emits the timing line itself around the whole program body. Blade is run with
  `--print checksum,input_checksum`; in the C++ lane that is exactly what prints. **The
  LLVM lane ignores `--print`/`BLADE_PRINT` and prints every top-level binding** (a compiler
  finding, recorded, not worked around): sources keep every array inside a function and
  expose only the two scalars at top level; the harness tolerates only the documented LLVM
  scalar extras, rejects duplicate contract lines, and treats any other output as a
  contract violation.
- Floats: Fortran prints 17 significant digits (`es24.17`); Blade prints 15. The harness
  parses both as doubles; "exact" agreement of `input_checksum` means equal after rounding
  to 15 significant digits.

### 5.2 Input generation

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
  bit-identical across arms; their SUM is not necessarily exact (it is compared at 15
  digits, §5.4). Reference values for seed 10000019 (variant D): k=0 → h5 = 408748897,
  k=1 → 189020261, k=2 → 1945512958 (intermediate h3 for k=0 is 184246361, the old variant
  C reference, which no longer is the output).
- All input ranges have `lo >= 0`, so `input_checksum` (the plain sum of all input cells
  over all input arrays) is a sum of non-negative terms. A sum cannot detect a permutation,
  a transposition or an exchange of equal-shaped arrays (M11); that is the oracle's job
  (§5.4), not the checksum's.
- Input generation may be threaded in mt8 arms (a licence on the generating kernel in
  Blade, a pragma in Fortran) and is materialized (§1.5). Threaded generation is never
  evidence of a threaded kernel (M24): parallelism is recorded separately for generation,
  kernel and checksum, and a table cell whose kernel loop is unthreaded reads "parallel
  preparation, serial kernel".

### 5.3 Input-generation twins and the "incremental" estimate

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

### 5.4 Correctness: the oracle and the tolerances

- **The correctness authority is an independent reference**, not another arm (M12):
  `bench/validation/ref_<kernel>.py` (numpy, blade agents; independent of Blade and
  Fortran) regenerates the inputs, computes the full output, and writes the pins —
  `checksum` and `input_checksum` at smoke AND full size — to `bench/validation/pins.json`.
  Every run of every arm (warm-ups, rounds, controls, twins) is validated against the pins:
  `licensed` and `repro` arms to **1e-12 relative** on `checksum` (licensed reordering of
  sums of non-negative terms stays far inside it), `fastmath` arms to **1e-8** (flagged);
  `input_checksum` to 15 significant digits in every arm. The rule is **oracle-relative**:
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
  (§1.5) are the evidence.
- **Checksums** are order-insensitive up to the tolerance and non-cancelling: sums of
  squares of the named output over its FULL logical index space, a count added as a float,
  or the kernel's own non-negative scalar. For symmetric objects Blade reads all logical
  cells through its canonicalizing accessor and the expert tier reads its triangle with
  weight 2 — the same value. This costs Blade more than the triangle read (gap register G3);
  **the total is the result and nothing is subtracted from it** (M13): the earlier
  "checksum tax" estimates in the specs are planning aids, labelled so, never a correction.

## 6. Statistics and bookkeeping (the harness)

- **Builds.** Every arm of a kernel is built up front from `harness/config.json`; the
  harness records the exact command line, compiler version string, flags, environment,
  SHA-256 of each executable AND of every resolved runtime DLL (GNU, Intel, Clang,
  OpenBLAS), stack reserve/commit and temporary-placement settings, and for Blade the
  `blade plan` output (which loops carry `[omp v1]`, which routes) and whether the LLVM
  lane printed `[blade] llvm lane refused: <reason>; falling back to C++`. A fallback arm is
  **marked `fallback`** and reported under `blade-cpp` semantics; the `blade-llvm` cell
  shows "refused: <reason>".
- **Smoke first.** `--smoke` runs every arm once at smoke size and checks the contract
  (three lines, pins agree, no unexpected fallback, no extra output), runs the cell-by-cell
  validation, and verifies that every required twin exists and prints `checksum = 0.0`. It
  is a precondition of a session, and the gate ENFORCES that smoke evidence is
  re-established after every binary or configuration change (the exact source, executable
  and config hashes are checked). Status: the runner's first all-regime sweep validated
  448 cells with zero checksum disqualifications (`notes/run-log.md`).
- **Pilot and sample size (M15).** A 3-round full-size pilot sets the production round
  counts against a declared precision target of ±5% on the headline ratio; the defaults
  below are minimums, enforced (a `--rounds` override below the minimum taints the session
  and the table says so): serial 11, memory-bound kernels (full-size serial median < 50 ms)
  21, mt8 7. Thirty to fifty observations per arm across sessions is the working
  expectation for the short kernels, not a guarantee; measurement never stops because
  significance first appeared.
- **Rounds.** Per kernel and regime: one discarded warm-up run per arm, then R rounds. In
  each round every arm — main arms, twins and both controls — runs once in a **seeded random
  permutation** (recorded), not a cyclic rotation. Cooldowns: >= 2 s between runs, >= 10 s
  between kernels.
- **Controls (M18).** Two fixed binaries run once per round of every block: the **compute
  control** (`recur`, gfortran-expert, serial x repro: deterministic, ~0.1 s, tracks core
  clock and thermal state; note its region also allocates and writes a 48 MB trajectory —
  "L2-resident" describes a step, not the region) and the **stream control** (`saxpy`,
  gfortran-expert, serial x repro, built at n = 700,001 — the `sizes.control` variant of
  `saxpy/kernel.json`, which the harness builds like any size variant — a 16.8 MB
  three-stream pass: tracks memory-subsystem contention and background load). BOTH
  controls are serial x `repro` builds; `harness/config.json` follows this text. Every control run is validated
  like any other run; an invalid or thermal-excursion control run flags the block. Within
  a block, a first-half vs second-half median difference above 5% in either control flags
  the block ("within-block drift"); a rolling control median moving more than 7% against
  the session's first block flags the session from that block on. Flagged blocks carry the
  flag in every cell; no result is ever rescaled by a control.
- **Per arm:** n, min, median, IQR. **Headline ratio** = median(arm) / median(blade-llvm)
  in serial regimes (> 1 means Blade faster); in **mt8 the denominator is `blade-cpp`**
  (the LLVM lane has no OpenMP); where `blade-llvm` is a fallback or refused, the
  denominator is `blade-cpp`. The denominator is printed explicitly in every row. Intervals
  are 95% bootstrap percentile intervals of the ratio of medians obtained by **resampling
  whole rounds jointly** (10,000 resamples; twins and controls travel with their round, so
  shared-twin correlations are preserved). Intervals are **pointwise**; the suite makes no
  simultaneous suite-wide superiority claim and applies no multiplicity correction because
  it states none.
- **Incremental estimates (M9, M16).** For kernels with twins: the incremental difference
  `median(total) − median(own twin)` per arm with its joint-round bootstrap interval,
  always; an incremental RATIO only when the reference arm's incremental interval excludes
  0, otherwise the cell reads `undefined`. No resample is ever discarded.
- **Verdicts (M17), practical threshold δ = 10%.** For a ratio r = median(arm)/median(Blade)
  with interval [lo, hi]: *win*: lo > 1 and median r >= 1.10; *detectable*: lo > 1 and
  median r < 1.10; *equivalent*: [lo, hi] ⊂ [1/1.10, 1.10] (the interval need not contain
  1); *loss*: hi < 1 and median r <= 1/1.10; *detectable loss*: hi < 1 and median r >
  1/1.10; otherwise *inconclusive*. Failure to detect a difference is not equivalence.
  Verdicts are per arm and per regime, compared with the spec's hypothesis; a mismatch is
  investigated, never resolved in the hypothesis's favour.
- **Disqualification:** pin mismatch (§5.4); a missing, duplicate or malformed line; a
  nonpositive duration; a run longer than 2.5 s (charter safety rule 3; the child
  watchdog is 3x that, 7.5 s); a thermal excursion — any run, warm-up or control included,
  slower than 3x the block's running median aborts the block and the session pauses (M19);
  the machine on battery (the whole session is refused). **Excursion rule (DECIDED by the
  user, 2026-10-18; D45):** an ISOLATED single-run excursion (one run above 3x the block's
  running median while both controls in that round are normal) is DROPPED AND RECORDED —
  the sample stays in the bundle, marked, excluded from inference, and the planned-cell
  manifest says so; the session ABORTS on a second excursion in the same block, on any
  control excursion, or on AC loss. (Session 2's saxpy mt8 block was lost to a single 43
  ms stall of a 6 ms input-only twin under the previous always-abort rule; RESULTS.md
  §12.2.)
- **Host conditions (M19).** The session records at start and per block: AC state, the
  active power plan and `powercfg /q` processor settings (maximum processor state, boost
  policy), OEM thermal mode where readable. The reference configuration caps the maximum
  processor state at ~80%; every result table is labelled **"max processor state capped
  (~80%)"** and no number is extrapolated to the uncapped machine — the cap can shift the
  balance between compute- and memory-bound kernels.
- **Session bundle (M27)** in `results/<yyyy-mm-dd>-<host>/`, immutable and checkpointed
  after every run: protocol, specs, `config.json`, every source (including
  `bench_support.f90` and the rewritten size variants), generated `.cpp`/`.ll`, build logs
  and effective command lines, executable and DLL hashes, `blade plan` outputs, power and
  affinity settings, seeds and execution order, raw per-run values including warm-ups,
  controls and aborted partial blocks, the generator-check and validation reports, a
  **planned-cell manifest** marking every cell as run / unrun / unsupported / fallback /
  no-parallel-form / failed / disqualified, and exact reproduction commands. Dirty source
  content is preserved, not referenced by SHA alone.
- Nothing is extrapolated or estimated in a result table. An unrun cell is reported as
  unrun.

## 7. Host safety rules (mandatory; from the charter, restated)

1. Only the RUNNER agent executes timing runs; every other agent runs programs for
   correctness at smoke size only (well under 100 ms).
2. Never two heavy processes at once: no concurrent `dotnet build`, benchmark run, or
   compile fleet. The orchestrator owns `dotnet build`.
3. Working set per kernel <= 64 MB in every arm (the natural tier's dense arrays count), a
   single timed run <= ~2 s, cooldowns as in §6. Threaded arms only after the serial arms
   of the same kernel validated.
4. The harness records AC/battery and the power plan and refuses the full suite on battery;
   AC state is rechecked per block.
5. Anything that looks thermal (fans screaming, the machine lagging, a control excursion,
   a second run in one block 3x slower than the running median, AC loss): the harness
   aborts the block; a human never pushes through. A single isolated excursion with normal
   controls is dropped and recorded, not treated as thermal (§6, D45).

**Resume plan after the session-2 abort (2026-10-18):** re-smoke (the group-A sources
changed under D44), then re-run mt8-licensed for the four group-A kernels plus the saxpy
cell, then serial-licensed antisym (its serial emission changed in one input-sum fold —
re-measured for consistency; gram/comoment3/pairkernel's serial emissions are byte-
identical and their serial rows stand), then serial-repro (3a: group A + recur + stencil;
3b: group B + controls), mt8-repro, serial-fastmath, mt8-fastmath, gram blas. Spelling
facts from the licence work (blade-A §14): a threading licence on a checksum ROW MAP is
silently dropped — only the fold-kernel placement threads (G27) — and a licensed fold
inside a result tuple opens no team (bind it to its own `let`); the threaded checksum folds
are Path B, serial within each chunk (G25).

## 7.1–7.9 Blade facts every kernel author relies on (probed 2026-10-17, this binary)

- 7.1 Symmetric storage and triangular iteration are derived only for the SAME ARRAY in
  adjacent commuting positions: `method_for(A, A) <@> lambda(x, y) where comm(x, y)`.
  `method_for(range<I>, range<I>) ... where comm(i, j)` emits a dense square (probed).
- 7.2 A fold over compact storage is refused (BL3999) in every spelling (`reduce`, `prodsum`,
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
- 7.3 `compound(dense, mask)` is valid only as a top-level `let` (BL6002 inside a function),
  and builds a hashed index of ~40 B per kept cell (blade-B) — the reason `maskagg`'s full
  size is n = 1,000,003.
- 7.4 Fold licences (blade-B): the spelling that threads a dot is `reduce(method_for(zip(a,
  b)) <@> lambda(x, y) -> x * y, lambda(p, q) where omp -> p + q, 0.0)` bound to its own
  `let` (`reduce(a * b, lambda ... where omp -> ..., 0.0)` also threads but materializes
  `a * b`); `omp` on the zip MAP feeding `(+)` emits serial; a bare `where omp` fold in
  expression position is emitted serial with a census note; `omp` on a leg of a `<&!>`
  join is emitted serial ("fold accumulates into a shared scalar") and `omp` on the join's
  fold kernel is DROPPED SILENTLY (a finding against the "dropped omp is never silent"
  invariant); so is `omp` on an `axes = 2` fold kernel. There is no licensed parallel form
  of a `<&!>` join.
- 7.5 Lifted lets (`let s1 = 1.5 * a + 0.25`) are materialized, one temporary per `let`,
  never fused across lets (blade-B). Fusion into the consuming fold happens through an
  `object_for(f) >>@ object_for(g)` chain (accepted inside functions in both lanes), a
  single block kernel, or an inline fold operand. A named output is forced with `|>
  compute` regardless — and then audited in the machine code (§1.5).
- 7.6 `blade plan` names what to check: `[symmetric-storage v1] ... applied [packed
  SymSymmetric|SymAntisymmetric storage ...]`, `[triangular-iteration v1] ... applied
  [... speedup x2]`, `[omp v1] ... applied [BLADE_OMP_PARALLEL_FOR(_DYNAMIC); loop nest
  level 0]` — per LOOP, so the record says whether the input map, the kernel or the
  checksum is the threaded one — `[blas-routing]`, `[pool-reuse v1]`, and the
  fusion/CSE/hoist decisions.
- 7.7 The LLVM lane: no thread runtime (an `omp` licence is honoured as fold-FMF
  reordering by default, never as threads — G11); contracts FMAs by default since
  5f4b33ea (G19 fixed; F14 is history); ignores `--print`/`BLADE_PRINT`; refuses
  `compound` (maskagg is `blade-cpp`-only; the fallback honours `--print`); refuses `let
  (a, b) = f()` (application in pack position); refuses a `T^1 -> T^1` row map in value
  position ("IRApplyCombinator in value position"); refuses the tuple-state fold; accepts
  `reduce(m, (+), axes = 2)` (the charter's note is outdated).
- 7.8 Kernel parameters (blade-A): in the C++ lane any elementwise operation on `T^k`
  kernel parameters (`x * y`, `a - b`, `x * y * z`) is materialized as a length-n temporary
  per output cell; the LLVM lane fuses it. `prodsum` is the contraction intrinsic that
  streams in both lanes — gram and comoment3 use it. Literal subscripts into `T^k`
  parameters carry a BL8006 check per read even when cast `(0 : Cx)`; concretely typed
  parameters (`Array<Float64 like Cx, Nx>`) read without the guard and make the inner trip
  count a compile-time constant (§1.2) — every group-A kernel types its parameters
  concretely.
- 7.9 Correctness hazard (blade-A; gap register G16): `R * R` with R antisymmetric is typed
  and stored antisymmetric, so mirrored reads come back negated — a silent wrong value in
  both lanes and the interpreter. No suite kernel squares an antisymmetric array
  elementwise; do not introduce one.

## 8. Reporting

One Markdown table per regime, rows = kernel x arm, columns:

`kernel | arm | n | min (s) | median (s) | IQR (s) | denominator | ratio [95% CI] | verdict | hypothesis | correctness | parallelism | notes`

plus, for kernels with twins, `incremental (s) [CI]` and `incremental ratio [CI] or
undefined`, and, for the compact kernels (gram, comoment3, antisym, pairkernel), an
**attribution** column — `kernel-only estimate, informal: checksum share X%` — drawn from
blade-A's §13 phase measurements. It is labelled an attribution, not a result: the
protocol's total stays the figure, and the column exists so a reader sees that pairkernel
is kernel-equivalent (its 1.36x loss is the 6.27M-read checksum pass; kernel alone 8.9–9.0
ns per cell against the expert's total 9.8 ns) and that comoment3's loss decomposes into
roughly 2.3x on the kernel plus the checksum (36% of its LLVM time, 70% in C++). `correctness` is `ok (pins)` / `DISQUALIFIED (rel err)`; `parallelism` is
`kernel threaded` / `parallel preparation, serial kernel` / `no parallel form` / `serial
only (LLVM lane has no OpenMP)` per the per-loop evidence, and for Blade fold cells the
plan's fold form (`threaded(Path B chunks, serial within each chunk)` in licensed mt8 —
the chunk interior is a serial chain, G25). Reading the `repro x mt8` fold cells: Blade
repro-mt8 arms are **threaded maps, serial folds** (`BLADE_FP_REPRO=1` serializes the fold
teams), while the Fortran repro-mt8 arms keep their thread-level `reduction(+:)` clauses,
which reorder across threads — an asymmetry of the regime's definition, stated in §3, not a
defect of an arm; `notes` carries `fallback`,
`checksum-only computation` (store audit), flag deviations, block flags (within-block
drift, control excursion), and the session labels ("max processor state capped (~80%)",
"fresh-process, allocation-inclusive"). Every table names its regime (`licensed`,
`repro`, `fastmath` with its unequal-permissions label, `blas`) and, in `licensed`, which
fold loops each arm reordered. A kernel's row block is followed by one paragraph comparing
the outcome with the spec's hypothesis and, for every loss AND every unexpected win, a
diagnosis (what the emitted code does that the other loop does not — from `blade plan`,
the emitted C++/LLVM, the machine code, or a counter).

**Inventory of workloads (M26).** Beside the eleven measured kernels every report lists the
workloads that were dropped or recast and why, so the supported claim is not wider than the
measured one: `band` (closed-form constrained enumeration — dropped: the domain fold is
refused at codegen, BL7004, and the compute form builds a hashed key table; D1, G4); the
**pair sum** over a `comm` kernel (recast as the `pairkernel` MATRIX: a fold over compact
storage is refused, BL3999; D2, G2); the O(1)-per-cell antisymmetric map (recast as the
angular-momentum tensor: a ~5 ms write under the 64 MB cap; D4). The claim the suite can
support is therefore about materialized symmetric objects, fused chains and folds, not
about pair sums or constrained enumeration.

**Freeze.** Sources, sizes, hypotheses, tier selections and tuning decisions are frozen in
a tagged commit before the pilot; anything changed after the pilot restarts the pilot.
**Pilot facts (blade-B §6, bundle `results/2026-10-17-LAPTOP-350H6LDB/session-231651/`).**
At n = 3 rounds on the UNCAPPED clock the compute control drifted (CV 8.5%, a +24%
excursion in one block) and the drift rules suppressed inference on 7 of 11 blocks.
**Decision (user's choice): the published measurement runs UNCAPPED** (maximum processor
state 100%, the "Silent" scheme). Drift suppression stays in force; every suppressed
verdict is reported as **"indicative"** with its medians and intervals shown — never
hidden, never re-thresholded. An 80%-capped re-run is the documented way to obtain
unsuppressed verdicts (the `powercfg` recipe is in `harness/README.md`).

**Session 1a (2026-10-18, bundle `results/2026-10-18-LAPTOP-350H6LDB/session-042223/`):**
serial x licensed x native for gram, comoment3, antisym, pairkernel, recur and stencil at
11/21 rounds; 0 failures, 0 disqualifications, 0 fallbacks; longest run 0.79 s; peak
working set 55.7 MB. Controls over 86 runs: compute control median 13.42 ms (CV 9.0%),
stream control 5.50 ms (CV 9.7%). Five of the six blocks are drift-suppressed
(indicative); `antisym` is the exception, with verdicts against blade-llvm (74.6 ms):
blade-cpp 1.47 win, gfortran-natural 10.4 win, ifx-natural 4.46 win, gfortran-expert 0.416
loss, ifx-expert 0.395 loss. The suppressed blocks' medians agree with the pilot's to a few
percent. The pilot also produced two diagnoses the tables will cite:
`recur`'s 11x loss is the per-step allocator (page faults on 8 freed-and-reallocated 160 KB
temporaries, G6), not the arithmetic, and `maskagg`'s time is ~85% hashed-index inserts
(G9).

**Freeze state:** the bench branch `worktree-bench-fortran` at merge commit 957afd95
(compiler flip 5f4b33ea merged with master 74b96445); the Blade.exe hash is recorded in the
session bundle. The
hypotheses are predictions to be tested, not criteria: a control that wins is reported as a
win and investigated.

## 9. Honesty rules

- No extrapolation, no estimates in result tables; the estimates in specs are planning aids
  and are labelled so.
- Controls (`dot`, `saxpy`, `stencil`, `recur`, `maskagg`) are in the suite because their
  behaviour has an independent explanation (bandwidth, a single serial chain, an
  unfusable compaction), not because they must lose; they are run and reported in the same
  tables as the group-A kernels, and an unexpected control win is investigated like a loss.
- Every loss gets a diagnosis; every win gets its mechanism named and checked against
  `blade plan` and the machine code (a win whose mechanism is not the one the spec
  predicted is reported as such).
- A Blade compiler limitation met while writing a kernel is recorded with a minimal
  reproducer (`notes/llvm-acceptance-*.md`, `notes/gap-register.md`), never dodged with a
  knob or an unidiomatic spelling; a worse-but-accepted idiom is a finding, not a fix.
- Interleaved, randomly permuted rounds with validated controls are the only admissible
  evidence; batches run arm-by-arm are not.
- Fortran arms are reviewed by the Fortran reviewer before any number is published; a win
  against unreviewed Fortran is provisional. "Expert" means reviewed, never proven optimal.
- The oracle (`validation/pins.json`) is independent of both languages; no arm is ever the
  reference for another.
