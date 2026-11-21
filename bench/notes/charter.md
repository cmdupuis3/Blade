# Charter: the Blade-vs-Fortran benchmark suite (`bench/`)

> **HISTORICAL (2026-10-18).** This is the orchestrator's original brief, kept as written
> for the record (only §2, the measurement-hygiene rules, was revised in place on
> 2026-10-18, D46). The living protocol is `bench/README.md` (entry point: goal, fairness,
> build/run, regimes, reporting, honesty rules), `bench/kernels/README.md` (kernel authoring:
> tiers, SIZE marker, output contract, Blade facts) and `bench/harness/README.md` (driver:
> verbs, schemas, statistics, session bundle, limits). Where this file and those disagree,
> they win; the candidate kernel list of §7 was finalized in `notes/kernel-list.md`.

Orchestrator's brief for every agent on this job. Read it fully before touching anything.
The protocol document that supersedes the provisional parts of this file is `bench/README.md`
(written by the architect); until it exists, this file is the protocol.

## 1. Goal

A rigorous, reproducible benchmark suite comparing **Blade** against **gfortran 16.2** and
**Intel ifx 2026.1** (there is no `ifort` on this machine; ifort is discontinued and ifx is
its successor) on kernels where Blade's structure-first design should give a legitimate
advantage, plus parity controls. The headline Blade arm is the **LLVM lane**
(`BLADE_LLVM=1`); the C++ lane is always measured too, as the secondary arm that explains
differences and covers programs the LLVM lane refuses.

**Results are reported whichever way they go.** The deliverable is an honest table, not a
press release. A loss with a diagnosis is a valid result; a win obtained by a weak Fortran is
a defect in the suite.

**"Fair fight" means:** the Fortran is what a competent Fortran programmer writes from the
same mathematical spec, at two tiers (natural, expert); same optimization class; same
floating-point regime; same threading regime; bit-identical inputs; checksum-verified
identical answers; same information (compile-time extents on both sides); no library calls
on either side in the native regime; no inline asm or vendor intrinsics anywhere. Blade gets
**one** source per kernel, written in the idiomatic style of `CLAUDE.md`'s style guide, with
no compiler-knob games beyond the regime matrix below.

## 2. Host facts and MEASUREMENT-HYGIENE rules (revised 2026-10-18, D46)

Host: AMD Ryzen 7 5800HS laptop, 8 cores / 16 threads, 15.4 GB RAM, hybrid graphics. The
August 2026 crash history that motivated this section's original "safety rules" (four-plus
DPC_WATCHDOG / VIDEO_TDR bugchecks under concurrent build + AVX load) has since been traced
to an integrated-graphics overload and resolved; the rules that remain are about clean
measurements, not crash protection:

1. **One timed process at a time.** Only the RUNNER agent executes timing runs, and no
   other heavy process (`dotnet build`, a compile fleet, another benchmark) runs
   concurrently with a session — it would perturb the measurement. Other agents run
   programs for correctness at tiny sizes only.
2. **Interleaved rounds and drift controls** (harness/README.md "Protocol and statistics"): seeded random permutation of all
   arms per round, two controls per round, within-block and rolling drift checks; a
   tripped block is reported as indicative.
3. **The excursion rule** (harness/README.md "Protocol and statistics", D45): an isolated single-run excursion with normal
   controls is dropped and recorded; a second excursion in the block, a control excursion,
   or AC loss aborts the block.
4. **AC check** (free): the harness records AC/battery and the power plan, refuses the
   suite on battery, and rechecks per block.
5. **Cooldowns:** 0.5 s between runs, 3 s between kernels, 60 s between sessions (D46;
   previously 2 s / 10 s / ~5 min — the values in force per session are in RESULTS.md §2).
6. The **64 MB working-set cap** and the **2.5 s single-run cap** stay frozen for this run
   (the sizes were chosen under them) but are no longer safety limits and may be raised in
   later sessions. Threaded arms still run only after the serial arms of the same kernel
   verified (correctness gating).

Toolchain facts:

- `g++` and `gfortran` live in `C:\msys64\ucrt64\bin`. That directory **must be first on
  PATH** when compiling: Git-for-Windows' `mingw64\bin` shadows it and `cc1plus` then dies
  silently (exit 1, no message). Verify with an actual compile, not `which`.
- `clang` for the LLVM lane: `C:\msys64\clang64\bin\clang.exe` (clang 22.1.8,
  `x86_64-w64-windows-gnu`). It is not on PATH; Blade's `Build.resolveClang` falls back to
  that exact path, and the harness pins it via `BLADE_LLVM_CLANG` anyway. (`C:\Program
  Files\LLVM\bin\clang.exe` is a different, MSVC-targeting build; `C:\msys64\clangarm64` is
  ARM64-only. Neither is used.)
- `ifx`: not on PATH. It needs the Intel environment:
  `cmd /c "call \"C:\Program Files (x86)\Intel\oneAPI\setvars.bat\" >nul 2>&1 && ifx ..."`
  (setvars takes ~10 s; ifx links with the MSVC linker from VS 2022 / Build Tools, which
  setvars also finds). Windows-style flags: `/O3 /QxHost /Qopenmp /fp:precise|fast`.
- `OPENBLAS_DIR` is set in the environment (`C:\Users\cdupu\tools\OpenBLAS-0.3.34-x64`),
  which turns Blade's BLAS routing **on by default**. Every Blade compile in this suite sets
  `BLADE_BLAS` explicitly (`0` for the native regime, `1` for the BLAS regime).
- Python for the harness: `C:\Python314\python.exe` (3.14, stdlib only; `python3` on PATH is
  a Store stub, do not use it).
- Blade binary: `<worktree>\bin\Release\net10.0\Blade.exe`, built from this worktree. Use
  `blade run <file>` (compiles in a private temp dir; cwd for the program = the source's
  directory), `blade check`, `blade emit`, `blade plan`. `blade run --print checksum` prints
  only that binding.

This worktree: `C:\Users\cdupu\Documents\GitHub\Blade\.claude\worktrees\bench-fortran`
(branch `worktree-bench-fortran`, cut from master at 61e06042). Nobody but the orchestrator
runs git commands. Do not touch anything outside `bench/` without asking.

## 3. Blade facts the kernels and harness rely on

- **Timing line.** There is no user-level timer intrinsic. Every compiled Blade program, in
  BOTH lanes, prints `<name> completed in <t>s` (name = source file stem, t in seconds, a
  monotonic clock) around the whole program body: every top-level binding, i.e. input
  generation + kernel + checksum, **excluding** process startup and the result-printing pass.
  That line is the timing contract for every arm in every language (section 5).
- **Deferral trap.** A deferred binding that nothing consumes is forced by the PRINT pass,
  which runs after the clock stops. So every kernel's output must be consumed by a scalar
  checksum binding inside the program (`reduce(...)` to a scalar), and arrays must never be
  printed (a bare top-level array binding is auto-echoed cell by cell; wrap work in a
  function returning the scalar checksum, as the fixtures do).
- **LLVM lane fallback.** With `BLADE_LLVM=1`, a program the lane refuses prints
  `[blade] llvm lane refused: <reason>; falling back to C++` on **stderr** and silently builds
  the C++ lane. The harness must detect that line and mark the arm as a fallback, never as an
  LLVM measurement. Coverage is ~65% of programs. Known refusals: whole-rank folds
  `reduce(m, (+), axes = 2)` (spell as `reduce(reduce(m, (+)), (+))`), recursive arrays in
  some shapes (`IRForRange`), composed loop objects `>>@` in expression position inside a
  function (refused in both lanes, BL7004). Every candidate kernel is probed in the LLVM
  lane before it is adopted.
- **Threading.** `BLADE_OMP_THREADS=1` at compile time suppresses all thread-pragma
  emission (a true serial build; `omp simd` is kept). Unset, pragmas are emitted where the
  SOURCE carries a `where omp(...)` licence and the degree is the runtime `OMP_NUM_THREADS`.
  Parallelism must be licensed in the one Blade source; the serial arm is the same source
  built with the knob.
- **Reassociation.** `BLADE_FP_REASSOC=1` licenses lane-parallel / `omp simd reduction`
  folds (it changes emission; checksums may drift at the ulp level). A bare `omp` on a
  `reduce` kernel licenses fold reordering and requires a commutative kernel.
- **Flags Blade uses.** C++ lane: `g++ -std=c++17 -O3 -march=native -ffp-contract=fast
  -fno-math-errno -fwrapv` (+ `-fopenmp` when pragmas are emitted). LLVM lane: `clang -O3
  -march=native -fno-math-errno -fwrapv` on the `.ll` (contraction is decided in the IR).
  The Fortran arms mirror these per regime (section 4).
- **Static extents.** `type I = Idx<N>` bakes N into the generated code. The Fortran gets
  `integer, parameter :: n = N` so both compilers see constant trip counts. Same information,
  fair.
- **Never power-of-two extents** on dense data (a ~7x cache-set artifact lives there). Use
  primes or composite non-powers of two (6006, 9999991, 1009, 61, 301, ...). Packed
  triangular storage is immune, but keep the discipline everywhere.
- **Storage.** Blade is row-major by declaration nesting (`Array<T like I, J>`: J contiguous).
  Fortran is column-major. The spec defines the MATHEMATICAL object; each language stores it
  in its own natural orientation. The expert Fortran tier may pick whichever orientation makes
  its loops contiguous (Blade cannot, so this concession favors Fortran, which is the right
  direction for a fair fight).
- **Scalar arithmetic.** `1` is Int64, `1.0` Float64; any implicit int/float mix is an error
  (BL3020): write `Float64(i)` for an index used in float arithmetic. Integer ops wrap.
- Idiom sources: `tests/fixtures/llvm/bench_*.blade` (the existing llvm-bench programs,
  known to pass both lanes), `examples/*.blade`, `tests/corpus/symmetry`, `tests/corpus/loops`,
  `tests/corpus/recursive-arrays`, `tests/corpus/halo`. The style guide in `CLAUDE.md`
  (section "Writing Blade") is binding for the Blade sources. The `blade_corpus_find` MCP tool
  answers "how do I write X in Blade".

## 4. The regime matrix

| axis | regime | Blade | gfortran | ifx |
|---|---|---|---|---|
| threads | `serial` | `BLADE_OMP_THREADS=1` at compile | no `-fopenmp` | no `/Qopenmp` |
| threads | `mt8` | source carries `where omp(...)`; `OMP_NUM_THREADS=8` | `-fopenmp`, author places `!$omp parallel do` on the natural loop; `OMP_NUM_THREADS=8` | `/Qopenmp`, same |
| fp | `strict` (headline) | default (no reassoc) | `-O3 -march=native -ffp-contract=fast` (gfortran's own default contraction; NO `-ffast-math`) | `/O3 /QxHost /fp:precise` (the Fortran agent documents whether ifx contracts FMAs under `precise`; if it does not, add the flag that enables contraction only, and record it) |
| fp | `fast` | `BLADE_FP_REASSOC=1` (+ bare `omp` fold licences where the kernel is a reduce) | `-O3 -march=native -ffast-math` | `/O3 /QxHost /fp:fast` |
| blas | `native` (headline) | `BLADE_BLAS=0` | no library calls | no library calls |
| blas | `blas` (optional, only for kernels Blade routes to BLAS: gram-class) | `BLADE_BLAS=1` (auto-routed) | expert tier calls OpenBLAS `dsyrk`/`dgemm` explicitly, linked against the same `OPENBLAS_DIR` | same |

Order of work: `serial x strict x native` for every kernel first (the honest core), then
`mt8 x strict x native`, then `fast`, then `blas`. Checksum tolerance: `strict` arms must
agree to 1e-12 relative (sums of ~1e7 positive terms drift ~1e-13 from summation order);
`fast` arms to 1e-8 and are flagged as such in the table. A mismatching arm is
**disqualified**, never averaged in.

## 5. Arms, tiers, and the output contract

Arms per kernel: `blade-llvm`, `blade-cpp`, `gfortran-natural`, `gfortran-expert`,
`ifx-natural`, `ifx-expert`.

- **natural** = the spec written the way a competent Fortran programmer writes it first:
  array syntax and intrinsics (`matmul`, `sum`, `dot_product`, `pack`, `maxval`, `where`) or a
  straightforward loop nest; dense storage; no manual symmetry exploitation beyond what is
  obvious from the spec; `!$omp parallel do` on the outermost natural loop in the mt8 regime.
- **expert** = the fastest Fortran the Fortran agent and the Fortran reviewer can produce for
  the same spec without libraries, asm, or intrinsics: triangular loops, packed storage, loop
  interchange, blocking, manual fusion, manual multi-accumulator, cache-aware orientation.
  Compiler flags are the regime's, not per-kernel.
- Blade: **one** source per kernel. The claim under test is "Blade from one idiomatic source
  is at least as fast as expert Fortran".

Every arm, every language, prints on stdout:

```
<name> completed in <t>s
checksum = <value>
input_checksum = <value>
```

`<t>` is the monotonic wall time around [input generation + kernel + checksum] (Fortran:
`system_clock` with `integer(8)` count and rate, around exactly that region; the first line is
printed after the clock stops). Floats are printed with 17 significant digits. Blade prints
`checksum = ...` by itself when the top-level binding is named `checksum`; the harness runs
Blade with `--print checksum,input_checksum`.

Fortran arms print nothing else to stdout. Program names: `<kernel>` for Blade (the source is
`<kernel>.blade`, so the timing line reads `<kernel> completed in`), and the Fortran support
module prints the same `<kernel> completed in` text from a name passed in.

**Input generation (identical bits in every arm).** A pure function of the linear index
`k` (0-based, row-major over the mathematical object's indices): an affine LCG round, a
QUADRATIC mixing round, and a second affine round, all modulo 2^31, with every intermediate
below 2^62 (round 1 while k + seed < 2^31; round 2 < 2^50; round 3 at most ~2.37e18) so it is
exact in Int64 / `integer(8)` arithmetic in every language. (The
charter's first version used two affine rounds; two affine maps compose into ONE affine map,
so u(k) was a sawtooth with lag-2 autocorrelation +0.76 and integer-valued stencil outputs.
Found by the group-B agent, 2026-10-17; the quadratic round is what breaks the affinity.)

```
h1 = ((k + seed) * 1103515245 + 12345) mod 2147483648
h2 = (h1 * (h1 div 4096 + 1)) mod 2147483648      # div = truncating integer division; all operands >= 0
h3 = (h2 * 1103515245 + 12345) mod 2147483648
u  = h3 / 2147483648.0            # in [0, 1)
value = lo + (hi - lo) * u        # the spec gives lo, hi, seed per input
```

Measured over 2e5 cells: mean 0.50, variance 0.0836 (uniform: 0.0833), lag-1/2/7
autocorrelations within +-0.003, no integer second differences. `seed` per input array is
`10000019 * j` for the j-th input of the kernel (j = 1, 2, ...), so two inputs of one kernel
are never shifted copies of each other (k + seed stays below 2^25 for every size in this
suite; the first product stays below 2^56). The Fortran side computes `k` from its own
(column-major) loop indices so the mathematical array is identical; the `input_checksum` (sum
of all input values, printed) is how the harness proves it. Pure functions of the index keep
input generation embarrassingly parallel and cheap in both languages; it is still inside the
timed region, so each spec states its expected share, and the harness supports an optional
`inputonly` twin arm for kernels where that share is not negligible (memory-bound kernels).

**Printing facts (found by the group-B agent):** Blade prints floats with 15 significant
digits, not 17, so "exact" `input_checksum` agreement means equal after rounding to 15
significant digits, and the 1e-12 checksum tolerance stands. The LLVM lane currently IGNORES
`--print` / `BLADE_PRINT` and prints every top-level binding (a compiler finding, recorded,
not worked around): kernels therefore keep every array inside a function and expose only
scalars at top level (`let checksum = ...`, `let input_checksum = ...`), as the fixtures do;
printing happens after the clock stops, so extra scalar lines cost nothing.

**Checksums** must be insensitive to summation order up to the tolerance and must not cancel:
sums of squares, sums of absolute values, or a weighted sum with a positive LCG weight field.
Never a plain sum of sign-mixed values (cancellation makes relative tolerances meaningless;
that lesson was learned on this repo's spectra).

## 6. Statistics and bookkeeping (the harness)

- Build every arm of a kernel up front (record the exact command line, compiler version
  string, flags, env, and the SHA-256 of each executable).
- For each kernel and regime: one discarded warm-up run per arm, then R **rounds**; each round
  runs every arm once in a rotated order (round-robin, interleaved, so machine drift hits all
  arms equally). R >= 7 for serial, >= 5 for mt8. Cooldowns per section 2.
- A fixed **drift control** binary (the gfortran-expert arm of one small kernel, chosen by the
  architect) runs once per round of every kernel; if its median moves more than 15% across a
  session, the session is flagged.
- Report per arm: n, min, median, IQR. Headline ratio = median(arm) / median(blade-llvm), with
  a bootstrap 95% interval on the ratio of medians. Everything above goes into one JSON per
  session plus a Markdown summary; the JSON also carries the CPU name, logical/physical
  counts, AC/battery, power plan, `OMP_NUM_THREADS`, git SHA, Blade.exe hash, timestamps.
- `--smoke` mode: tiny sizes, 1 round, checks only the contract (lines present, checksums
  agree, no LLVM fallback where one is expected to succeed). The runner runs smoke before
  anything else.
- Nothing is ever extrapolated or estimated. An unrun cell is reported as unrun.

## 7. Candidate kernels (provisional; the architect finalizes names, sizes, specs)

Group A: structure the compiler derives (Blade's thesis).

| id | kernel | why Blade might win | fair Fortran counterpart |
|---|---|---|---|
| A1 `gram` | C = Xᵀ X (n x p, p x p symmetric), `where comm` -> packed symmetric storage and triangular iteration | half the flops and stores, derived from one annotation | natural: `matmul(transpose(x), x)` or full square nest; expert: triangular loop, packed or dense symmetric |
| A2 `comoment3` | third-order central comoment tensor T(i,j,k) over p variables, `SymIdx<3, p>` | C(p+2,3) cells vs p^3, derived | natural: triple nest, dense cube; expert: i<=j<=k nest with hand-packed offsets |
| A3 `antisym` | A(i,j) = f(x_i)g(x_j) - f(x_j)g(x_i) with `anticomm`, no stored diagonal, sign-tracked reads | strict half storage, no diagonal | natural: full matrix; expert: strict upper triangle |
| A4 `pairkernel` | sum over pairs of a Gaussian kernel in d=3, `where comm` | triangular iteration from the annotation | natural: double nest with `if (i < j)` or full nest halved; expert: j > i loop |
| A5 `band` | banded operator via `static struct Band {...} where abs(i - j) <= w`, closed-form enumeration | visits only solutions, no predicate | natural: dense nest with an `if`; expert: `do j = max(1, i-w), min(n, i+w)` |

Group B: language-level structure.

| id | kernel | why Blade might win | fair Fortran counterpart |
|---|---|---|---|
| B1 `fusedchain` | a chain of elementwise stages consumed by one reduction (`object_for` composition / deferred maps) | fusion by deferral, no temporaries | natural: array statements (compiler temporaries); expert: one fused loop |
| B2 `onepass` | several statistics in one traversal via the `<&!>` reduction join | one pass over memory | natural: `sum(x)`, `sum(x**2)`, `maxval` (3+ passes); expert: one loop, several accumulators |
| B3 `stencil` | 2-D 5-point stencil via `halo`, no edge guards | no bounds branches, fused with its consumer | natural: array sections `u(2:n-1, 2:m-1) = ...` (temporary); expert: explicit nest |
| B4 `recur` | a nonlinear recurrence via `let rec` over a static axis (parity control) | expected parity; proves the harness is not rigged | natural = expert: a DO loop |
| B5 `maskagg` | `mask` + `compound` + reduce (honest control where Fortran is strong) | likely parity or loss | natural: `sum(x, mask = ...)`; expert: a loop with a branch |

Controls: `dot` (parity), `saxpy`-class (bandwidth parity). The suite must contain controls
that Blade does NOT win, measured and reported, or the wins are not credible.

Each spec states a **hypothesis** (expected outcome and why) before measurement; the results
write-up compares against it. Sizes: cache-resident and DRAM-resident variants where
meaningful, within the 64 MB rule.

## 8. Directory layout (fixed; nobody renames)

```
bench/
  README.md                    the protocol (architect)
  notes/                       charter, probes, reviews, decisions, agent reports
  kernels/<name>/
      spec.md                  math, sizes, input seeds/ranges, checksum, tiers, hypothesis
      <name>.blade             the ONE Blade source
      natural.f90              tier 1 Fortran
      expert.f90               tier 2 Fortran
      kernel.json              sizes (smoke/full), expected shares, arm list, notes
  fortran/
      bench_support.f90        shared module: timer, LCG, printing (identical formatting)
      build_gfortran.ps1 / build_ifx.cmd    compile wrappers taking regime flags
  harness/
      run.py                   the driver (Python 3.14 stdlib only)
      README.md                how to run; what each JSON field means
  results/<yyyy-mm-dd>-<host>/ committed JSON + Markdown snapshots
```

File ownership (no cross-editing; disputes go to the orchestrator):

| who | owns |
|---|---|
| architect (fable) | `README.md`, every `spec.md`, `notes/design-decisions.md`, `notes/kernel-list.md` |
| harness (opus) | `harness/**`, every `kernel.json` (harness config, populated from the specs), `notes/harness-notes.md` |
| blade-A (opus) | `kernels/<group A>/*.blade`, `notes/llvm-acceptance-A.md` |
| blade-B (opus) | `kernels/<group B + controls>/*.blade`, `notes/llvm-acceptance-B.md` |
| fortran (opus) | `fortran/**`, every `natural.f90` / `expert.f90`, `notes/fortran-notes.md` |
| fortran-review (codex) | `notes/review-fortran.md` (findings; patches proposed as diffs, applied by the fortran agent) |
| method-review (codex) | `notes/review-method.md` |
| runner (sonnet) | `results/**`, `notes/run-log.md` |

## 9. Working agreements

- Report back concisely with: what you produced (paths), what you verified and how, what is
  blocked and why, and anything another agent must know. Put long material in your notes
  file, not in the report.
- When you find a Blade compiler bug or refusal, **do not work around it with a knob or an
  unidiomatic spelling**; record it in your notes with a minimal reproducer and tell the
  orchestrator. Choosing a different idiom that is equally natural is fine; choosing a worse
  idiom to dodge a bug is a finding, not a fix.
- Never fabricate a number. If you did not run it, say so.
