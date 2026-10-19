# Blade vs gfortran / ifx — the benchmark suite

Entry point for `bench/`: what the suite claims and what "fair" means (§1), how to build and
run it (§2), the regime matrix (§3), where everything lives (§4), how results are reported
and where they are (§5), and the honesty rules (§6). A kernel author reads
`kernels/README.md` (arms and tiers, the SIZE marker, the output contract, Blade facts);
anyone changing the driver reads `harness/README.md` (verbs, schemas, statistics, session
bundle, limits). `notes/design-decisions.md` is the decision log (D1–D46),
`notes/gap-register.md` the compiler gaps the suite exposed (G1–G34), `notes/kernel-list.md`
the kernel inventory, `kernels/<name>/spec.md` each kernel's authoritative definition, and
`notes/charter.md` the historical charter (this file supersedes its provisional sections
4–7; where they disagree, this file wins; where it is silent, the charter stands). The
independent methodology review is `notes/review-method-codex.md`, its dispositions
`notes/review-method.md`; the protocol incorporates every item there.

Consolidated on 2026-10-18 from the single protocol document: §1 and §3 keep their numbers;
the former §2, §4, §5 (5.1–5.4) and §7.1–7.9 are `kernels/README.md` §1, §2, §3 (3.1–3.4)
and §4 (4.1–4.9); the former §6 and §7 are `harness/README.md` "Protocol and statistics" and
"Limits: hygiene, not safety"; the former §8 and §9 are §5 and §6 here.

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
   legitimate and are recorded per arm (peak working set, harness/README.md "Protocol and statistics").
2. **Same information.** Extents are compile-time constants on both sides (`type I = Idx<N>`
   / `integer, parameter :: n = N`), including the trip counts of kernel-internal loops
   (Blade kernel parameters are typed concretely, `Array<Float64 like Obs>`, not `T^1`, so
   the inner extent is a constant in both languages).
3. **Bit-identical inputs** generated in-program by the LCG of kernels/README.md §3.2, verified against the
   independent oracle (kernels/README.md §3.4) by `input_checksum`; **identical answers** verified by
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
6. **Explicit endpoints (kernels/README.md §3.1).** The timed region includes allocation and first touch of
   everything the program creates; destruction is a recorded asymmetry (Blade's block scope
   frees its arrays inside the region; Fortran's retained program arrays are deallocated
   after the stop line, while worker panels and compiler temporaries have their own
   lifetimes, recorded per arm).


## 2. Building and running

**Prerequisites (this host; `notes/fortran-notes.md` §1 and `harness/README.md` have the
details).**

- Blade: .NET 10 SDK; `dotnet build Blade.fsproj -c Release` at the repository root. The
  harness uses `bin/Release/net10.0/Blade.exe` and refuses to run with a
  `blade.toolchain.json` beside it (it would flip unset gates). Every knob is set per arm from
  `harness/config.json`, never from the environment.
- MSYS2 **ucrt64** at `C:\msys64\ucrt64\bin` FIRST on PATH: g++ for Blade's C++ lane and
  `gfortran` 16.2 (Git's `mingw64\bin` ahead of it makes `f951` die silently). The LLVM lane
  uses `C:\msys64\clang64\bin\clang.exe` (`BLADE_LLVM_CLANG`, set by the harness).
- Intel oneAPI 2026.1 `ifx` (no `ifort` exists): not on PATH; `fortran/build_ifx.cmd` sets up
  `vcvars64.bat` + the compiler's `vars.bat` per build (~2 s). `fortran/build_gfortran.ps1`
  is the gfortran wrapper; both add the fixed per-arm flags and echo the command line.
- OpenBLAS at `%OPENBLAS_DIR%` (0.3.34) for the `blas` regime — and the reason every native
  Blade arm sets `BLADE_BLAS=0` explicitly.
- Python 3.14 at `C:\Python314\python.exe` (`python3` is a Store stub): the driver is
  standard-library-only; the oracle scripts in `validation/` need numpy.

**Verbs.** The driver is `harness/run.py`; `harness/README.md` lists every option, the exit
codes and the schemas.

```
C:\Python314\python.exe bench\harness\run.py list                      # kernels, sizes, regimes, which sources exist
C:\Python314\python.exe bench\harness\run.py check gram                # build every arm at smoke size, run once, validate
C:\Python314\python.exe bench\harness\run.py smoke --regimes all       # every kernel; REQUIRED before bench (smoke evidence)
C:\Python314\python.exe bench\harness\run.py bench                     # the measured protocol -- RUNNER ONLY
```

Only the runner executes `bench` (one timed process at a time, `harness/README.md` "Limits");
everyone else runs `check` at smoke size. `bench` refuses to start without current smoke
evidence for every planned cell and a pin for every kernel at the bench size; pins come from
`validation/ref_<kernel>.py` (numpy, `kernels/README.md` §3.4). Fresh sources need their
`SIZE` markers (`kernels/README.md` §2) and `kernel.json` (owner: harness).

**Regime names.** `<serial|mt8>-<licensed|repro|fastmath>-<native|blas>`:

- threads: `serial` = no thread-level OpenMP anywhere (standalone `simd` stays live on every
  side); `mt8` = `OMP_NUM_THREADS=8`, source licences / `!$omp parallel do` active, C++ lane
  only for Blade (the LLVM lane has no thread runtime).
- fp: `licensed` = Blade's default (every source licence honoured) against Fortran with the
  SAME permission (`-DBENCH_LICENSED`) — the HEADLINE; `repro` = `BLADE_FP_REPRO=1`, no
  reordering in any serial arm; `fastmath` = the vendor's `-ffast-math` / `/fp:fast`,
  labelled unequal-permissions, run last.
- blas: `native` = no user-selected external BLAS (headline); `blas` = gram only, OpenBLAS
  `dsyrk` on both sides.

The full matrix with every flag and knob is §3; the order of work is licensed serial, licensed
mt8, repro serial, repro mt8, fastmath, blas (§3, last paragraph).

## 3. The regime matrix

| axis | regime | Blade (knobs on the one source) | gfortran 16.2 | ifx 2026.1 |
|---|---|---|---|---|
| threads | `serial` = **no active thread-level OpenMP** (standalone `simd` is active by design on every side) | `BLADE_OMP_THREADS=1` at compile (threading pragmas removed; `omp simd` hints stay live) | `-fopenmp-simd`, no `-fopenmp` | `/Qopenmp-simd`, no `/Qopenmp` |
| threads | `mt8` | `BLADE_OMP_THREADS` unset; the source's `where omp(...)` licences emit pragmas; `OMP_NUM_THREADS=8`. **C++ lane only: the LLVM lane has no thread runtime** (`src/EmitLlvm.fs:3037`, `:1004-1023`: by documented design an `omp` clause is "a licence to parallelize, not a demand" — the lane emits the serial body and consumes the same licence for fold reassociation (`reassoc nsz`, now by default); the shim says "this lane has no threads"). So the LLVM arm is serial in every threads regime and the mt8 Blade figure is `blade-cpp`; `blade-llvm` is listed as "serial only (LLVM lane has no threads)" | `-fopenmp`; author places `!$omp parallel do` as kernels/README.md §1 says; `OMP_NUM_THREADS=8` | `/Qopenmp`, same |
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


## 4. Where everything lives

| path | what | owner |
|---|---|---|
| `README.md` | this entry point: goal, fairness, build/run, regimes, reporting, honesty | architect |
| `RESULTS.md` | the results document (closed 2026-10-18): §11 pre-fix headline, §13 post-fix re-measure, §12 decisions, §5A superseded rows | architect |
| `kernels/README.md` | kernel authoring guide: arms and tiers, SIZE marker, output contract, Blade facts | architect |
| `kernels/<name>/spec.md` | the kernel's authoritative definition (sizes, inputs, outputs, checksum, hypotheses, tier selection) | architect |
| `kernels/<name>/<name>.blade`, `inputonly.blade`, `natural.f90`, `expert.f90` (+ `expert_blas.f90` for gram) | the arms' sources; one Blade source per kernel | blade-A / blade-B / fortran |
| `kernels/<name>/kernel.json` | sizes, regimes, arms, tolerances, twins (schema in `harness/README.md`) | harness |
| `fortran/` | `bench_support.f90` (timer, generator, printing), the build wrappers `build_gfortran.ps1` / `build_ifx.cmd`, toolchain probes (`*probe.f90`, `selfcheck.f90`) | fortran |
| `harness/` | `run.py` (the driver), `config.json` (every knob, authoritative), `README.md` (driver's manual: verbs, schemas, statistics, session bundle, limits) | harness |
| `validation/` | `ref_<kernel>.py` oracles (numpy) and their pins `pins-A.json` / `pins-B.json`; `dump/` cell-by-cell variants, `validate_A.py` / `validate_B.py`; `generator_check.py`; `tools/` (plan/fold-form/asm audits) | blade-A / blade-B |
| `notes/design-decisions.md` | the decision log D1–D46 | architect |
| `notes/gap-register.md` | compiler gaps G1–G34 with evidence and status | architect |
| `notes/kernel-list.md` | the kernel inventory, sizes and regime applicability | architect |
| `notes/charter.md` | the original brief (HISTORICAL; its §2 hygiene rules were revised in place on 2026-10-18) | orchestrator |
| `notes/fortran-notes.md`, `notes/harness-notes.md`, `notes/llvm-acceptance-{A,B}.md`, `notes/generator-check.md` | the agents' working notes: toolchain facts, CLI facts, lane acceptance per kernel, the generator statistics | fortran / harness / blade-A / blade-B |
| `notes/review-method{,-codex}.md`, `notes/review-fortran{,-codex}.md` | the independent reviews (Codex transcripts, verbatim) and their dispositions | coordinator |
| `notes/run-log.md`, `notes/phase2-microkernel-plan.md`, `notes/microkernel-sketches-*.md`, `notes/repro/` | the runner's log; the phase-2 plan and sketches; reproducers | runner / coordinator |
| `results/<date>-<host>/session-<HHMMSS>/` | immutable session bundles (`session.json`, `summary.md`, `manifest.json`, build records; binaries git-ignored) | runner |
| `results/full/RUNNER-RECORD.md` | the runner's consolidated per-session record; `results/gates/` the final compiler gate consoles; `results/pilot/`, `smoke*/` the early sweeps | runner |
| `.build/` (git-ignored) | smoke evidence, check/smoke sessions, stub kernels, the last-session timestamp | harness |

## 5. Reporting and where the results are

The measured results are `RESULTS.md` (closed 2026-10-18, every number copied from a session
bundle): **§13 is the post-fix re-measure headline** (Blade.dll 79AF7EDC / 30FBBC97 on tree
2dcc0739, with the final compiler gate totals in its Status line), §11 the pre-fix headline
with inventory and phase-2 worklist, §12 the two user decisions (D44, D45), §5A the superseded
rows; `results/full/RUNNER-RECORD.md` is the runner's record; `notes/gap-register.md` the
compiler gaps each diagnosis names. The report format below is the protocol's.

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
measured one: `band` (closed-form constrained enumeration — dropped on 2026-10-17 because
the domain fold was refused at codegen, BL7004, and the compute form built a hashed key
table, D1/G4; **restored on 2026-10-18 after G4 was fixed** — `kernels/band/spec.md`, the
twelfth kernel, unmeasured until its sources land); the
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

## 6. Honesty rules

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
