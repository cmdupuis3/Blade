# Blade vs gfortran / ifx — results

**Status (2026-10-18):** sessions **1a** (serial x licensed x native: gram, comoment3,
antisym, pairkernel, recur, stencil; `session-042223/`) and **1b** (serial x licensed x
native: fusedchain, onepass, maskagg, dot, saxpy + twins; `session-090344/`) are IN — the
headline regime is complete for all eleven kernels. Session **2** (mt8 x licensed x
native; `session-100329/`: group B + controls stand; its group-A rows are superseded, kept
in §5A) and its **re-run 2b** (`session-113651/`: group A under matched parallelism +
saxpy) are IN — the mt8 regime is complete except maskagg (serial by protocol) and the
Blade "no parallel form" cells (onepass, recur). The serial-licensed antisym re-measure
(`session-121713/`, clean, within 1–2% of 1a) is IN (§3.1). Session **3a**
(serial-repro-native, group A + recur + stencil; `session-122606/`; all six blocks
indicative) is IN (§6.1): the repro build is 13–17x behind the experts on gram, comoment3
and antisym — the measured cost of the old default and the strongest G1 evidence.
Session **3b** (serial-repro-native, group B + controls; `session-130438/`; maskagg, dot
and saxpy CLEAN, fusedchain and onepass indicative) is IN (§6.2). Session **3c**
(mt8-repro-native, ten kernels; `session-140038/`; the first session under the D46
cooldowns — control CV 1.7% / 4.7% against ~9% before; fusedchain, onepass, recur, saxpy
clean) is IN (§6.3) — the repro regime is complete. Session **5a** (serial-fastmath, six
kernels; `session-142614/`; antisym, fusedchain, dot clean) is IN (§7.1); session **5b**
(mt8-fastmath, clean re-run `session-144656/` after the user-caused abort of
`session-144122/`; all six blocks indicative) is IN (§7.2). Session **4** (gram blas, serial + mt8;
`session-150343/`; both blocks clean) is IN (§7.3). **The pre-fix matrix is complete**;
§11 is its headline, inventory, honesty notes and phase-2 worklist. **§13 is the
post-fix re-measure** on Blade.dll 79AF7EDC (tip 957c1e0e; eight sessions 201558–214954,
filled from `results/full/RUNNER-RECORD.md`): gram now beats both experts in every
regime (serial licensed 1.37 / 1.24 live, repro 1.29 / 1.17 live, mt8 1.28 / 1.22 live),
comoment3 and antisym beat or match them under threads, pairkernel wins serial licensed
(1.12 / 1.04 live), recur improved 5–6x and still loses 2x, band (new) loses 3–4x with two
open items; the antisym and comoment3 SERIAL rows await session 9 on the twin binary
(30FBBC97…, 2dcc0739) and carry the C++-tiles-only values in footnotes. New open items
G32 (orbit-class checksum fold serial under mt8) and G33 candidate (band LLVM 3.3 vs 0.8
ms) are in the register. Consolidated per-session
tables: `results/full/RUNNER-RECORD.md` (the runner's record, cross-referenced, not
duplicated here).
Both §12 decisions are taken (mt8 matched parallelism → group-A mt8 rows superseded and
re-run; isolated-excursion drop-and-record rule). Resume sequence RUNNING: re-smoke group
A → mt8-licensed group A + saxpy → serial-licensed antisym (re-measure, §3.1) →
serial-repro 3a/3b → mt8-repro → serial-fastmath → mt8-fastmath → gram blas. The headline
(§11) is written when the blas session lands.

Every number below is copied from a session bundle's `summary.md` / `session.json`; a cell
without a bundle reads `unrun`. The method is `README.md`; kernels `notes/kernel-list.md`
and `kernels/<name>/spec.md`; compiler gaps `notes/gap-register.md`; decisions
`notes/design-decisions.md`.

## 0. How to read this document

- **Headline estimand:** the complete timed region (allocation + input generation + kernel
  + both checksums), fresh process, allocation-inclusive (README §5.1).
- **Ratio convention:** ratio = median(arm) / median(Blade reference). **Ratio > 1: Blade
  faster (the arm takes more time); ratio < 1: the arm faster.** Reference: `blade-llvm`
  in serial regimes, `blade-cpp` in mt8 (the LLVM lane has no threads) and wherever the
  LLVM lane refused. 95% intervals: round-block bootstrap, 10,000 resamples (README §6).
- **Verdicts** (δ = 10%): win / detectable-win / equivalent / detectable-loss / loss /
  inconclusive. **`indicative (suppressed by drift)`**: the block failed a drift, trend or
  control check; medians, intervals and the would-be ratio are shown, no verdict is
  claimed, nothing is re-thresholded (README §8; the published measurement runs uncapped
  by the user's choice).
- **Attribution** (compact kernels only): `kernel-only estimate, informal: checksum share
  X%` from `notes/llvm-acceptance-A.md` §13 phase measurements — an attribution, not a
  result; the total is the result.
- **Incremental** (kernels with twins): median(total) − median(own input-only twin) with
  its interval, NOT kernel time; an incremental ratio only where defined (README §5.3).
- **Fold form** is the plan's `fold form:` line for the Blade arms (`reordered (k reassoc
  flags)` LLVM / `reordered (k SIMD_REDUCTION)` C++); **parallelism** per the per-loop
  evidence.
- Host and build (bundle header): AMD Ryzen 7 5800HS, 8C/16T, 15.41 GB, AC, max processor
  state 100%, Power Scheme "Silent"; git 92777698, Blade.dll 74b6728036b170d9, GNU Fortran
  16.2.0 (MSYS2), ifx 2026.1.0 Build 20260617, clang 22.1.8; pins `pins-A.json`,
  `pins-B.json`. Numbers are specific to this uncapped configuration.

## 1. Method summary

See `README.md`. One idiomatic Blade source per kernel, built for every regime by knobs
only; Fortran at two tiers — `natural` = the dense baseline (literal transcription,
intrinsics, dense storage, no symmetry exploitation), `expert` = the reviewed implementation
(triangular/packed loops, register tiling, fused checksums; `!$omp simd reduction` only on
the loops the Blade source licenses, README §3 matched-loop table); regimes `licensed`
(Blade's default) / `repro` (`BLADE_FP_REPRO=1`) / `fastmath`, `serial` / `mt8`, `native` /
`blas`; bit-identical inputs from the five-round generator (README §5.2); correctness
against the independent numpy oracle on every run; seeded random permutation of arms per
round; two drift controls per round; whole-round bootstrap; freeze at bench-branch commit
957afd95 (bundle git 92777698 is the bench branch at run time).

## 2. Sessions

Cooldowns (inter-run spacing can change thermal state): every session up to and including
serial-repro 3b ran with 2 s between runs, 10 s between kernels and ~5 min between
sessions; from the next session on the relaxed D46 values apply (0.5 s / 3 s / 60 s) — the
column on the right says which was in force. The 64 MB and 2.5 s caps stayed frozen
throughout this run (README §7).

| session | date | bundle | regimes x kernels | rounds | duration | failures / DQ / fallbacks | longest run | peak working set | suppressed blocks | cooldowns in force (run / kernel / session) |
|---|---|---|---|---|---|---|---|---|---|---|
| pilot | 2026-10-17 | `results/2026-10-17-LAPTOP-350H6LDB/session-231651/` | all 11, serial-licensed-native | 3 | — | 0 / 0 / 0 | — | — | 7 of 11 | 2 s / 10 s / ~5 min |
| **1a** | 2026-10-18 | `results/2026-10-18-LAPTOP-350H6LDB/session-042223/` | serial-licensed-native: gram, comoment3, antisym, pairkernel, recur, stencil | 11 (gram, comoment3, antisym, pairkernel), 21 (recur, stencil) | 1975.8 s | 0 / 0 / 0 (`failures: []`) | 0.7728 s (antisym gfortran-natural median; max single run per bundle) | 55.7 MB (gram ifx-natural) | 5 of 6 (antisym clean) | 2 s / 10 s / ~5 min |
| **1b** | 2026-10-18 | `results/2026-10-18-LAPTOP-350H6LDB/session-090344/` | serial-licensed-native: fusedchain, onepass, maskagg, dot, saxpy + twins | 21 each | 3281.9 s | 0 / 0 / 1 expected (maskagg blade-llvm: LLVM lane refuses `compound`; blade-cpp is the reference) | 0.0672 s (maskagg blade-cpp median; all arms < 0.083 s) | **65.9 MB (fusedchain gfortran-natural) and 65.5 MB (ifx-natural) — over the 64 MB rule**; all other arms ≤ 50.9 MB | 4 of 5 (onepass clean) | 2 s / 10 s / ~5 min |
| **2** (partial) | 2026-10-18 | `results/2026-10-18-LAPTOP-350H6LDB/session-100329/` | mt8-licensed-native: antisym, comoment3, gram, pairkernel, fusedchain, onepass, recur, stencil, dot complete; **saxpy ABORTED (round 7 of 21)**; maskagg not in the regime | 7 (gram, comoment3), 21 (others) | 4113.6 s | 0 / 0 / 0; one ABORT (saxpy blade-cpp-inputonly run 0.043 s = 7.0x its running median 0.006 s, "charter 2.5") | 0.6436 s (gram ifx-natural median) | **66.4 / 68.0 MB (fusedchain gfortran-/ifx-natural) — over the 64 MB rule** | 8 of 9 (antisym clean) | 2 s / 10 s / ~5 min |
| **2b** (mt8 re-run) | 2026-10-18 | `results/2026-10-18-LAPTOP-350H6LDB/session-113651/` | mt8-licensed-native: gram, comoment3, antisym, pairkernel (matched parallelism, D44) + saxpy | 21 each | 2118.5 s | 0 / 0 / 0; no abort; no excursion dropped | 0.6311 s (gram ifx-natural median) | 58.3 MB (gram ifx-natural) | 5 of 5 (trend/control checks) | 2 s / 10 s / ~5 min |
| **1a′** (antisym re-measure) | 2026-10-18 | `results/2026-10-18-LAPTOP-350H6LDB/session-121713/` | serial-licensed-native: antisym (after the D44 source change) | 11 | 229.0 s | 0 / 0 / 0; no excursion | 0.7709 s | 32.7 MB | 0 of 1 (clean) | 2 s / 10 s / ~5 min |
| **3a** | 2026-10-18 | `results/2026-10-18-LAPTOP-350H6LDB/session-122606/` | serial-repro-native: gram, comoment3, antisym, pairkernel, recur, stencil | 11 / 11 / 11 / 11 / 21 / 21 | 2008.6 s | 0 / 0 / 0; no abort; no excursion | 1.228 s (gram blade-llvm warm-up; under the cap) | 55.9 MB (gram ifx-natural) | 6 of 6 (first block's compute control ran high at 16.5 ms; later blocks 12–23% below it) | 2 s / 10 s / ~5 min |
| **3b** | 2026-10-18 | `results/2026-10-18-LAPTOP-350H6LDB/session-130438/` | serial-repro-native: fusedchain, maskagg, onepass, dot, saxpy + twins | 21 each | 3291.8 s | 0 / 0 / 1 expected (maskagg blade-llvm) ; no abort; no excursion | 0.0689 s (maskagg blade-cpp) | **66.1 / 65.6 MB (fusedchain naturals, over the 64 MB rule)** | 2 of 5 (fusedchain, onepass); maskagg, dot, saxpy clean | 2 s / 10 s / ~5 min (old driver) |
| **3c** | 2026-10-18 | `results/2026-10-18-LAPTOP-350H6LDB/session-140038/` | mt8-repro-native: ten kernels (maskagg serial by protocol); Blade = threaded maps, serial folds | 7 (antisym, comoment3, gram), 21 (others) | 1474.1 s | 0 / 0 / 0; no abort; no excursion | 0.6986 s (gram gfortran-natural median) | 68.2 / 66.5 MB (fusedchain naturals, over the 64 MB rule) | 6 of 10 (arm trends; fusedchain, onepass, recur, saxpy clean) | **0.5 s / 3 s / 60 s (first D46 session)** |
| **4** | 2026-10-18 | `results/2026-10-18-LAPTOP-350H6LDB/session-150343/` | gram blas: serial (OPENBLAS_NUM_THREADS=1) + mt8 (=8); blade-cpp auto-routed dsyrk, gfortran/ifx expert explicit dsyrk; blade-llvm no-blas; naturals excluded | 11 (serial), 21 (mt8) | 123.6 s | 0 / 0 / 0; no excursion | 0.0807 s | 51.1 MB | 0 of 2 (both clean) | 0.5 s / 3 s / 60 s |
| **5a** | 2026-10-18 | `results/2026-10-18-LAPTOP-350H6LDB/session-142614/` | serial-fastmath-native: antisym, comoment3, gram, fusedchain, onepass, dot (+ twins); tolerance 1e-8 | 11 / 11 / 11 / 21 / 21 / 21 | 844.9 s | 0 / 0 / 0; no abort; no excursion; no fallback | 0.4668 s (gram gfortran-natural median) | 66.1 / 65.6 MB (fusedchain naturals) | 3 of 6 (comoment3, gram, onepass); antisym, fusedchain, dot clean | 0.5 s / 3 s / 60 s |
| 5b (aborted attempt) | 2026-10-18 | `results/2026-10-18-LAPTOP-350H6LDB/session-144122/` | mt8-fastmath-native (attempt) | — | 182.2 s | **ABORTED on a control excursion** (memory control 0.030 s, 5.5x its running median) caused by the user working on the machine at 14:44 (Task Manager open); excluded from every figure | — | — | — | 0.5 s / 3 s / 60 s |
| **5b** | 2026-10-18 | `results/2026-10-18-LAPTOP-350H6LDB/session-144656/` | mt8-fastmath-native: antisym, comoment3, gram, fusedchain, onepass, dot (+ twins); tolerance 1e-8; onepass blade-cpp no-parallel-form | 21 / 21 / 7 / 21 / 21 / 21 | 943.4 s | 0 / 0 / 0; no abort; no excursion | 0.7068 s (gram gfortran-natural median) | 66.5 / 68.2 MB (fusedchain naturals) | 6 of 6 (arm trends) | 0.5 s / 3 s / 60 s |

## 3. serial x licensed x native (headline regime) — session 1a

Reference for every kernel in this block: `blade-llvm`. Columns: n, min / median / IQR in
ms, ratio vs reference [95% CI], verdict, hypothesis (from the spec), fold form,
parallelism, peak working set, attribution, notes. Block flags are quoted from the bundle.

### 3.1 antisym — CLEAN block (verdicts stand); re-measured in `session-121713/` after the D44 licence change

Re-measure bundle: 11 rounds, 229.0 s, 0 flags, NOT suppressed, no excursion; the D44
change altered this source's serial emission in its final 1201-term input-sum fold only
(8 accumulators → one simd reduction, identical at 15 digits, ~1 µs). Every median is
within 1–2% of session 1a's (record below). Controls in this short quiet session: compute
12.84 ms, memory 5.453 ms, both on their baselines.

| arm | n | min (ms) | median (ms) | IQR (ms) | ratio [95% CI] | verdict | hypothesis | fold form | parallelism | peak WS (MB) | attribution | notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| blade-llvm | 11 | 73.17 | 73.95 | 0.256 | ref | — | — | reordered (7 reassoc flags) | serial | 17.4 | checksum share ≈ small (A §13) | |
| blade-cpp | 11 | 107.3 | 107.9 | 1.023 | 1.459 [1.452, 1.471] | win (C++ lane 1.46x slower than LLVM lane) | both lanes reported | reordered (7 SIMD_REDUCTION) | serial | 18.3 | | G26 + G3b; pilot 1.54, session 1a 1.466 |
| gfortran-natural | 11 | 764.7 | 770.9 | 2.733 | 10.425 [10.371, 10.455] | **win** | WIN ~2x | — | serial | 23.4 | | dense baseline, both inner products per cell |
| gfortran-expert | 11 | 30.24 | 30.74 | 0.212 | 0.416 [0.414, 0.417] | **loss** | equivalence to modest loss | — | serial | 32.7 | | G1 |
| ifx-natural | 11 | 330.8 | 332.9 | 2.219 | 4.502 [4.475, 4.516] | **win** | WIN ~2x | — | serial | 22.9 | | |
| ifx-expert | 11 | 28.75 | 29.54 | 0.739 | 0.400 [0.395, 0.407] | **loss** | equivalence to modest loss | — | serial | 32.2 | | G1 |

Session-1a record (`session-042223/`, superseded by the re-measure, kept for the
consistency check): blade-llvm 74.55 ms; blade-cpp 109.3 (1.466 [1.437, 1.481]);
gfortran-natural 772.8 (10.366 [10.187, 10.406]); gfortran-expert 31.01 (0.416 [0.406,
0.418]); ifx-natural 332.2 (4.456 [4.373, 4.471]); ifx-expert 29.43 (0.395 [0.388,
0.403]).

Diagnosis. Both hypotheses missed in magnitude, in opposite directions. Against the dense
baselines Blade wins 10.4x (gfortran) and 4.5x (ifx), not ~2x: the baseline evaluates two
inner products for all p² cells and gfortran's version of that nest runs at a third of
ifx's speed — the win measures the baseline's dense transcription, not only the halved
cell count (that is what "dense baseline" means, README §2). Against the experts Blade
loses 2.4x (ratio 0.416 / 0.395), outside the "equivalence to modest loss" band: the
mechanism is **G1** — Blade's per-cell dot reloads both operand rows for every cell
(2 loads per FMA) while the expert's 8x6 register tile does 0.67 loads per FMA; measured
rates (A §13) antisym LLVM 7.7 G FMA/s, C++ 5.0, experts 16.7–18.6, i.e. load-bound at
half the FMA peak. The C++ lane's 1.47x over the LLVM lane is the expected **G26** (one
`omp simd reduction` accumulator, loop-carried `vaddpd` latency) plus **G3b** (the
canonicalizing read calls `canon_fold` / `std::sort` out of line, 4.9x the LLVM lane's
inlined compare-and-swap), consistent with the pilot's 1.54 and with every other group-A
kernel. Peak working set: the experts' 32 MB reflects their packed panels (recorded,
README §1.1).

### 3.2 comoment3 — indicative (suppressed by drift)

Bundle flag: "control compute median 0.01449 s moved +13.7% vs the session baseline
0.01275 s; trend: control compute moved 14.5% within the block; control memory median
0.005845 s moved +11.4% vs baseline 0.005246 s; trend: control memory moved 18.9% within
the block".

| arm | n | min (ms) | median (ms) | IQR (ms) | ratio [95% CI] | verdict | hypothesis | fold form | parallelism | peak WS (MB) | attribution | notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| blade-llvm | 11 | 40.43 | 41.49 | 1.170 | ref | — | — | reordered (6 reassoc flags) | serial | 11.1 | checksum share 36% (LLVM), A §13 | |
| blade-cpp | 11 | 108.7 | 109.7 | 0.649 | 2.644 [2.602, 2.690] | indicative | both lanes reported | reordered (6 SIMD_REDUCTION) | serial | 12.0 | checksum share 70% (C++) | G26, G3b |
| gfortran-natural | 11 | 601.9 | 603.4 | 2.195 | 14.541 [14.307, 14.801] | indicative | WIN 4–6x | — | serial | 43.3 | | |
| gfortran-expert | 11 | 11.14 | 11.49 | 0.314 | 0.277 [0.272, 0.285] | indicative | equivalence to modest loss | — | serial | 12.1 | | G1b, G3a |
| ifx-natural | 11 | 720.2 | 723.7 | 3.941 | 17.442 [17.209, 17.733] | indicative | WIN 4–6x | — | serial | 42.9 | | |
| ifx-expert | 11 | 11.48 | 11.70 | 0.388 | 0.282 [0.277, 0.293] | indicative | equivalence to modest loss | — | serial | 11.7 | | G1b, G3a |

Diagnosis (indicative). The would-be ratios are 14.5x / 17.4x against the dense cubes
(hypothesis 4–6x: the cell ratio is 5.89, the rest is the dense baseline's p³ nest with a
sample loop per cell) and 0.28 against both experts (a 3.6x loss where "equivalence to a
modest loss" was predicted). Attribution (A §13, informal): the checksum pass is 36% of
the LLVM time (70% of the C++ time), so the kernel-only deficit is ≈ 2.3x — **G1b** (Blade
does a multiply plus an FMA per term with 3 loads; the expert hoists the pair product
`W = a*b` out of the innermost level and does one FMA at 0.67 loads; vector-op ratio
1.97x) — and the remainder is **G3a** (the logical-read checksum over p³ cells, with
**G3b** making the C++ lane's reads 4.9x costlier). The medians agree with the pilot's to
a few percent; the suppression is the control drift, not a measurement defect.

### 3.3 gram — indicative (suppressed by drift)

Bundle flag: "trend: gfortran-natural first-half vs second-half median moved 9.8%; control
compute median 0.01479 s moved +16.0% vs baseline 0.01275 s; control memory median
0.005843 s moved +11.4% vs baseline 0.005246 s; trend: control memory moved 5.6% within
the block".

| arm | n | min (ms) | median (ms) | IQR (ms) | ratio [95% CI] | verdict | hypothesis | fold form | parallelism | peak WS (MB) | attribution | notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| blade-llvm | 11 | 151.9 | 156.7 | 6.12 | ref | — | — | reordered (8 reassoc flags) | serial | 26.4 | checksum share small (A §13) | |
| blade-cpp | 11 | 250.8 | 275.2 | 20.33 | 1.756 [1.645, 1.828] | indicative | both lanes reported | reordered (5 SIMD_REDUCTION) | serial | 27.2 | | G26 |
| gfortran-natural | 11 | 480.8 | 601.6 | 87.62 | 3.838 [3.186, 3.988] | indicative | loss or equivalent | — | serial | 36.7 | | libgfortran serial `matmul` (inline limit 30); within-block trend 9.8% |
| gfortran-expert | 11 | 78.40 | 81.68 | 2.869 | 0.521 [0.507, 0.539] | indicative | equivalence to modest loss | — | serial | 51.5 | | G1 |
| ifx-natural | 11 | 572.3 | 576.2 | 3.985 | 3.676 [3.624, 3.785] | indicative | loss or equivalent | — | serial | 55.7 | | ifx inlines `matmul` |
| ifx-expert | 11 | 69.95 | 73.15 | 2.225 | 0.467 [0.461, 0.483] | indicative | equivalence to modest loss | — | serial | 51.1 | | G1 |

Diagnosis (indicative). Against the `matmul` baselines Blade would be 3.7–3.8x faster —
the hypothesis ("loss or equivalent") assumed the runtime `matmul` would be a fast blocked
kernel; libgfortran's serial routine and ifx's inlined `matmul` both ran at ~0.6 s for the
full p² product, slower than Blade's triangle. Against the experts Blade loses ~2x (0.52 /
0.47): **G1** — the per-cell dot's operand reloads (2 loads per FMA; gram LLVM 8.4 G FMA/s,
C++ 4.4, experts 16.3–18.2) against the 8x6 register-tiled outer product. The C++ lane's
1.76x over the LLVM lane is **G26**. The gfortran-natural arm also tripped its own
within-block trend (9.8%, IQR 88 ms) — its median is the least stable in the session.

### 3.4 pairkernel — indicative (suppressed by drift)

Bundle flag: "control compute median 0.01415 s moved +11.0% vs the session baseline
0.01275 s".

| arm | n | min (ms) | median (ms) | IQR (ms) | ratio [95% CI] | verdict | hypothesis | fold form | parallelism | peak WS (MB) | attribution | notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| blade-llvm | 11 | 41.10 | 42.59 | 2.076 | ref | — | — | reordered (4 reassoc flags) | serial | 28.4 | kernel-only estimate, informal: checksum share 21% (LLVM); kernel 8.9–9.0 ns/cell vs expert total 9.8 | |
| blade-cpp | 11 | 73.67 | 77.25 | 4.973 | 1.814 [1.764, 1.814] | indicative | both lanes reported | reordered (3 SIMD_REDUCTION) | serial | 29.2 | checksum share 57% (C++) | G3b |
| gfortran-natural | 11 | 61.09 | 61.92 | 1.451 | 1.454 [1.405, 1.471] | indicative | WIN ~2x | — | serial | 52.7 | | dense: 2x the `exp` calls |
| gfortran-expert | 11 | 32.40 | 32.77 | 0.251 | 0.769 [0.737, 0.780] | indicative | equivalent | — | serial | 28.8 | | G3a |
| ifx-natural | 11 | 63.54 | 64.53 | 1.023 | 1.515 [1.448, 1.532] | indicative | WIN ~2x | — | serial | 52.3 | | |
| ifx-expert | 11 | 30.54 | 30.82 | 0.775 | 0.724 [0.691, 0.743] | indicative | equivalent | — | serial | 28.4 | | G3a |

Diagnosis (indicative). The would-be win over the dense baselines is 1.45–1.5x, below the
hypothesised ~2x, and the would-be loss to the experts is 1.3–1.4x where equivalence was
predicted; both have the same cause, **G3a**: every arm calls the scalar UCRT `exp` (~6.6 of
~9 ns per cell; ifx its own scalar `exp`, no SVML; the expert's SoA layout buys nothing
measurable), so the kernel proper is equivalent (8.9–9.0 ns per cell against the expert's
9.8 ns total), and the whole difference is Blade's 6.27M-read logical checksum pass (21% of
the LLVM time, 57% of the C++ time, where **G3b** multiplies the read cost). The fix is the
canonical-cell fold (G2) or fusing the fold into the producing nest.

### 3.5 recur — indicative (suppressed by drift)

Bundle flag: "trend: blade-cpp first-half vs second-half median moved 6.1%;
gfortran-natural 10.9%; gfortran-expert 12.7%; ifx-natural 7.1%; ifx-expert 17.1%; control
compute moved 7.9% within the block; control memory moved 7.2% within the block".

| arm | n | min (ms) | median (ms) | IQR (ms) | ratio [95% CI] | verdict | hypothesis | fold form | parallelism | peak WS (MB) | notes |
|---|---|---|---|---|---|---|---|---|---|---|---|
| blade-llvm | 21 | 105.1 | 116.9 | 9.206 | ref | — | — | reordered (3 reassoc flags) | serial | 51.4 | |
| blade-cpp | 21 | 116.0 | 134.1 | 9.558 | 1.147 [1.093, 1.182] | indicative | both lanes reported | reordered (3 SIMD_REDUCTION) | serial | 52.3 | |
| gfortran-natural | 21 | 17.52 | 19.70 | 2.950 | 0.169 [0.151, 0.178] | indicative | equivalent | — | serial | 51.4 | |
| gfortran-expert | 21 | 9.116 | 10.48 | 1.877 | 0.090 [0.084, 0.096] | indicative | LOSS 1.5–3x | — | serial | 50.8 | G6 |
| ifx-natural | 21 | 15.04 | 16.00 | 1.486 | 0.137 [0.131, 0.145] | indicative | equivalent | — | serial | 51.0 | |
| ifx-expert | 21 | 9.439 | 11.27 | 2.312 | 0.096 [0.088, 0.107] | indicative | LOSS 1.5–3x | — | serial | 50.4 | G6 |

Diagnosis (indicative). Blade loses ~11x to the experts (0.090 / 0.096) and ~6–7x even to
the array-statement baselines (0.169 / 0.137) where equivalence with the baselines was
predicted. Mechanism (**G6**, blade-B §6): the ALLOCATOR, not the arithmetic — each time
step allocates and frees eight 160 KB temporaries (four `rhs` results, three stage inputs,
the step result) in both lanes; the Windows heap hands those sizes back as fresh pages, so
each allocation re-takes ~38 page faults at ~1.3 µs: 300 steps x 8 x 38 ≈ 91k faults ≈
118 ms, i.e. 19.6 ns per cell-step against ~2.5 ns of arithmetic. The stage loops are fully
vectorized AVX2; `blade plan` shows pool reuse declined in `step`. Fortran's array
statements do not re-fault because its temporaries stay committed. Three sufficient fixes
are in G6 (stage-chain fusion G8; once-per-recursion temporaries; a size-class free list).
The committed source stays the spec's idiom — this is the language result; the block-kernel
spelling (RK4 as one kernel) measured informally at near-expert speed is an
alternative-spelling note, not a result. The compute control is this kernel's
gfortran-expert at repro: 13.4 ms median here against the measured arm's 10.5 ms in
licensed.

### 3.6 stencil — indicative (suppressed by drift); twins present

Bundle flag (abridged): "trend: blade-llvm first-half vs second-half median moved 25.1%;
blade-cpp 18.0%; gfortran-natural 13.4%; gfortran-expert 15.3%; ifx-expert 28.9%; several
twins 6.8–17.5%; control compute moved 14.0% within the block; control memory 6.1%".

| arm | n | min (ms) | median (ms) | IQR (ms) | ratio [95% CI] | verdict | hypothesis | fold form | parallelism | peak WS (MB) | incremental (ms) [CI] | incremental ratio [CI] |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| blade-llvm | 21 | 11.89 | 13.51 | 3.255 | ref | — | — | reordered (4 reassoc flags) | serial | 49.7 | 7.253 [6.547, 8.686] | ref |
| blade-cpp | 21 | 14.90 | 16.34 | 3.056 | 1.209 [1.102, 1.333] | indicative | both lanes reported | reordered (4 SIMD_REDUCTION) | serial | 50.6 | 7.231 [6.420, 8.756] | 0.997 [0.848, 1.166] |
| gfortran-natural | 21 | 16.78 | 17.76 | 2.885 | 1.314 [1.233, 1.452] | indicative | equivalent | — | serial | 50.7 | 7.371 [6.663, 9.109] | 1.016 [0.925, 1.257] |
| gfortran-expert | 21 | 13.60 | 14.46 | 2.000 | 1.070 [0.985, 1.146] | indicative | equivalent | — | serial | 50.7 | 4.842 [4.416, 5.965] | 0.668 [0.569, 0.790] |
| ifx-natural | 21 | 18.16 | 18.96 | 0.773 | 1.403 [1.245, 1.516] | indicative | equivalent | — | serial | 50.2 | 12.01 [11.51, 12.24] | 1.656 [1.364, 1.850] |
| ifx-expert | 21 | 10.58 | 11.64 | 3.758 | 0.861 [0.843, 0.949] | indicative | equivalent | — | serial | 50.3 | 5.698 [5.059, 7.581] | 0.786 [0.750, 1.000] |
| blade-llvm-inputonly | 21 | 5.801 | 6.259 | 1.044 | — | — | — | reordered (2 reassoc flags) | serial | 27.3 | | |
| blade-cpp-inputonly | 21 | 8.783 | 9.107 | 0.831 | — | — | — | reordered (1 SIMD_REDUCTION) | serial | 28.1 | | |
| gfortran-natural-inputonly | 21 | 9.832 | 10.39 | 0.709 | — | — | — | — | serial | 27.8 | | |
| gfortran-expert-inputonly | 21 | 8.917 | 9.615 | 1.391 | — | — | — | — | serial | 27.8 | | |
| ifx-natural-inputonly | 21 | 6.497 | 6.947 | 1.039 | — | — | — | — | serial | 27.4 | | |
| ifx-expert-inputonly | 21 | 5.513 | 5.939 | 0.788 | — | — | — | — | serial | 27.4 | | |

Diagnosis (indicative). Totals sit within 0.86–1.40 of the LLVM lane — the bandwidth-bound
equivalence the spec predicted, with ifx-expert the fastest total (11.6 ms) and ifx-natural
the slowest (19.0 ms). The incremental view is more informative than the totals here:
input generation differs per arm (twins 5.9–10.4 ms — the LLVM lane and ifx generate
faster than the C++ lane and gfortran, README §5.3), so the incremental estimates say the
LLVM lane, the C++ lane and gfortran-natural spend the same ~7.2–7.4 ms beyond their
twins, the two experts 4.8–5.7 ms (their fused checksum saves a pass: incremental ratios
0.67 / 0.79), and ifx-natural 12.0 ms (its array-section statement is the slow path:
1.66). All of this is indicative — this block carried the session's largest within-block
trends (25–29% on three arms), typical of a 12–19 ms kernel on an uncapped clock.

## 4. serial x licensed x native — session 1b (fusedchain, onepass, maskagg, dot, saxpy)

Bundle `results/2026-10-18-LAPTOP-350H6LDB/session-090344/` (same host/build header as
1a; duration 3281.9 s; `failures: []`). All five kernels have input-only twins per arm, so
every table carries the incremental columns (median(total) − median(own twin), interval
first; NOT kernel time). Reference `blade-llvm` except maskagg (`blade-cpp`: the LLVM lane
refused `compound`, the expected fallback). n = 21 per arm.

### 4.1 fusedchain — indicative (suppressed by drift)

Bundle flag: "trend: ifx-natural first-half vs second-half median moved 7.6%".

| arm | n | min (ms) | median (ms) | IQR (ms) | ratio [95% CI] | verdict | hypothesis | fold form | peak WS (MB) | incremental (ms) [CI] | incremental ratio [CI] |
|---|---|---|---|---|---|---|---|---|---|---|---|
| blade-llvm | 21 | 4.228 | 4.571 | 0.410 | ref | — | — | reordered (2 reassoc flags) | 19.6 | 0.625 [0.458, 0.982] | ref |
| blade-cpp | 21 | 6.388 | 6.754 | 0.621 | 1.478 [1.400, 1.551] | indicative | both lanes reported | reordered (1 SIMD_REDUCTION) | 20.4 | 0.670 [0.332, 0.959] | 1.071 [0.459, 1.778] |
| gfortran-natural | 21 | 16.81 | 17.98 | 1.562 | 3.934 [3.749, 4.214] | indicative | WIN 1.5–2.5x | — | **65.9** | 11.21 [10.69, 12.16] | 17.93 [11.34, 25.23] |
| gfortran-expert | 21 | 6.552 | 6.905 | 0.403 | 1.511 [1.433, 1.563] | indicative | equivalent | — | 20.1 | 0.725 [0.592, 0.970] | 1.159 [0.689, 1.718] |
| ifx-natural | 21 | 12.71 | 13.45 | 1.078 | 2.943 [2.808, 3.122] | indicative | WIN 1.5–2.5x | — | **65.5** | 8.912 [8.556, 9.709] | 14.25 [8.977, 19.47] |
| ifx-expert | 21 | 4.363 | 4.897 | 0.545 | 1.071 [0.986, 1.124] | indicative | equivalent | — | 19.7 | 1.107 [0.735, 1.348] | 1.769 [0.954, 2.459] |
| blade-llvm-inputonly | 21 | 3.566 | 3.946 | 0.392 | — | — | — | reordered (1 reassoc flags) | 19.6 | | |
| blade-cpp-inputonly | 21 | 5.710 | 6.084 | 0.434 | — | — | — | in-order | 20.4 | | |
| gfortran-natural-inputonly | 21 | 6.526 | 6.769 | 0.191 | — | — | — | — | 20.1 | | |
| gfortran-expert-inputonly | 21 | 5.815 | 6.180 | 0.275 | — | — | — | — | 20.1 | | |
| ifx-natural-inputonly | 21 | 4.342 | 4.540 | 0.356 | — | — | — | — | 19.7 | | |
| ifx-expert-inputonly | 21 | 3.582 | 3.790 | 0.359 | — | — | — | — | 19.7 | | |

Diagnosis (indicative). The would-be win over the staged baselines is 3.9x (gfortran) and
2.9x (ifx) on the totals, above the hypothesised 1.5–2.5x; the incremental view says why:
beyond their twins the baselines spend 11.2 / 8.9 ms (three named temporaries written and
re-read; neither compiler fused the separate array statements — ifx-natural's 65.5 MB peak
shows the temporaries are real) while the fused Blade chain spends 0.6–0.7 ms and the
experts 0.7–1.1 ms — the deferred `object_for >>@` chain fuses to one loop as the spec
predicted, and against the experts the incremental intervals all contain 1 (equivalent on
the kernel; ifx-expert's total is equivalent too, 1.071 [0.986, 1.124]). The C++ lane's
1.48x over the LLVM lane is in its TWIN as well (6.08 vs 3.95 ms): generation, not the
fold (the C++ twin's fold form is `in-order`; G26 applies to the total's fold). **Rule
breach to record:** the two natural arms' peak working set (65.9 / 65.5 MB) exceeds the 64
MB cap (README §7.3) by ~3% — the three temporaries plus the input at n = 2,000,003 were
estimated at 64 MB exactly (spec); the overrun is allocator overhead. Kept as measured and
flagged; a re-run at a smaller n is not required for a 3% breach of a safety budget, but
the number is stated.

### 4.2 onepass — CLEAN block (verdicts stand)

| arm | n | min (ms) | median (ms) | IQR (ms) | ratio [95% CI] | verdict | hypothesis | fold form | peak WS (MB) | incremental (ms) [CI] | incremental ratio [CI] |
|---|---|---|---|---|---|---|---|---|---|---|---|
| blade-llvm | 21 | 11.05 | 11.67 | 0.578 | ref | — | — | reordered (5 reassoc flags) | 42.5 | 2.178 [1.623, 2.373] | ref |
| blade-cpp | 21 | 16.34 | 16.77 | 0.578 | 1.437 [1.396, 1.480] | win (C++ lane 1.44x slower) | both lanes reported | reordered (1 SIMD_REDUCTION) | 43.3 | 1.941 [1.550, 2.235] | 0.891 [0.691, 1.173] |
| gfortran-natural | 21 | 32.33 | 32.87 | 0.567 | 2.816 [2.781, 2.893] | **win** | WIN 2–3x | — | 43.0 | 15.73 [15.44, 16.14] | 7.221 [6.670, 9.524] |
| gfortran-expert | 21 | 16.54 | 16.83 | 0.555 | 1.442 [1.419, 1.497] | **win** | equivalent | — | 43.0 | 1.593 [1.384, 2.116] | 0.732 [0.630, 1.154] |
| ifx-natural | 21 | 12.45 | 12.96 | 0.383 | 1.110 [1.095, 1.146] | **win** (detectable, ≥ 1.10 by a hair) | WIN 2–3x | — | 42.6 | 0.978 [0.406, 1.523] | 0.449 [0.188, 0.795] |
| ifx-expert | 21 | 11.07 | 11.78 | 0.947 | 1.009 [0.974, 1.051] | **equivalent** | equivalent | — | 42.6 | 1.928 [1.538, 2.255] | 0.885 [0.663, 1.308] |
| blade-llvm-inputonly | 21 | 9.297 | 9.495 | 0.590 | — | — | — | reordered (1 reassoc flags) | 42.5 | | |
| blade-cpp-inputonly | 21 | 14.54 | 14.83 | 0.422 | — | — | — | in-order | 43.3 | | |
| gfortran-natural-inputonly | 21 | 16.61 | 17.15 | 0.455 | — | — | — | — | 43.0 | | |
| gfortran-expert-inputonly | 21 | 14.80 | 15.24 | 0.537 | — | — | — | — | 43.0 | | |
| ifx-natural-inputonly | 21 | 11.05 | 11.98 | 1.210 | — | — | — | — | 42.6 | | |
| ifx-expert-inputonly | 21 | 9.378 | 9.849 | 0.557 | — | — | — | — | 42.6 | | |

Diagnosis. The gfortran side matches the hypothesis exactly: 2.8x over the four-`sum`
baseline (incremental 15.7 ms beyond its twin — four DRAM passes — against Blade's 2.2 ms),
and the gfortran-expert total is a 1.44x Blade win driven by the TWIN (15.2 vs 9.5 ms
generation; incremental ratio 0.73 [0.63, 1.15], i.e. equivalent on the kernel). The ifx
side is different: ifx-natural's four `sum` intrinsics cost only 1.0 ms beyond its twin
(incremental ratio 0.45 — ifx fused the four passes, the "ifx may fuse" caveat of the
spec), so the total win is 1.11, barely a win by the δ = 10% rule; ifx-expert is
equivalent (1.009 [0.974, 1.051]), the suite's first exact equivalence. The C++ lane's
1.44x is again in its twin (14.8 vs 9.5 ms — input generation), with the join's
incremental equivalent (0.89 [0.69, 1.17]): the four-accumulator join takes the
simd-reduction form in both lanes (G7 partly fixed). The hypothesis "WIN 2–3x" held for
gfortran and failed for ifx because the ifx baseline is not four passes.

### 4.3 maskagg — indicative (suppressed by drift); reference blade-cpp

Bundle flag: "trend: ifx-expert first-half vs second-half median moved 10.4%". The
blade-llvm arm is a recorded **fallback** (n = 0): "a compound-index initializer; expected
fallback (the LLVM lane refuses `compound`; blade-cpp is the Blade figure)".

| arm | n | min (ms) | median (ms) | IQR (ms) | ratio vs blade-cpp [95% CI] | verdict | hypothesis | fold form | peak WS (MB) | incremental (ms) [CI] | incremental ratio [CI] |
|---|---|---|---|---|---|---|---|---|---|---|---|
| blade-llvm | 0 | — | — | — | — | fallback | — | in-order | — | | |
| blade-cpp | 21 | 63.50 | 67.16 | 7.288 | ref | — | LOSS 2–5x | in-order | 46.3 | 64.34 [62.90, 68.01] | ref |
| gfortran-natural | 21 | 8.169 | 8.301 | 0.538 | 0.124 [0.117, 0.128] | indicative | LOSS | — | 17.3 | 4.960 [4.874, 5.124] | 0.077 [0.073, 0.081] |
| gfortran-expert | 21 | 3.902 | 3.993 | 0.371 | 0.059 [0.056, 0.064] | indicative | LOSS | — | 16.4 | 1.088 [1.005, 1.372] | 0.017 [0.016, 0.021] |
| ifx-natural | 21 | 10.21 | 10.56 | 0.374 | 0.157 [0.148, 0.161] | indicative | LOSS | — | 23.5 | 8.379 [8.222, 8.452] | 0.130 [0.122, 0.134] |
| ifx-expert | 21 | 2.788 | 2.919 | 0.459 | 0.043 [0.041, 0.048] | indicative | LOSS | — | 15.9 | 1.193 [1.159, 1.427] | 0.019 [0.017, 0.023] |
| blade-llvm-inputonly | 21 | 1.650 | 1.717 | 0.122 | — | — | — | reordered (1 reassoc flags) | 12.0 | | |
| blade-cpp-inputonly | 21 | 2.755 | 2.822 | 0.143 | — | — | — | in-order | 12.8 | | |
| gfortran-natural-inputonly | 21 | 3.285 | 3.341 | 0.140 | — | — | — | — | 12.5 | | |
| gfortran-expert-inputonly | 21 | 2.838 | 2.906 | 0.102 | — | — | — | — | 12.5 | | |
| ifx-natural-inputonly | 21 | 2.158 | 2.181 | 0.054 | — | — | — | — | 12.0 | | |
| ifx-expert-inputonly | 21 | 1.674 | 1.726 | 0.042 | — | — | — | — | 12.0 | | |

Diagnosis (indicative). The control Blade was not expected to win, it does not: 8x slower
than `pack` (0.124), 6.4x slower than ifx's `pack` (0.157), 17–23x slower than the experts
(0.059 / 0.043); the hypothesised 2–5x loss was an underestimate. Incrementally, Blade
spends 64.3 ms beyond its twin where `pack` spends 5.0 / 8.4 ms and the count-and-scatter
experts 1.1–1.2 ms. Mechanism **G9** (blade-B §6): ~85% of the time is one
`std::unordered_map` insert per kept cell (77.8 ns per kept cell, ≈ 39 ms of the 62 ms
incremental at full size in the pilot; 64 ms here), plus the Bool mask pass and the `y*y`
temporary; the hashed rank-1 compound index is the whole story, and the peak working set
(46.3 MB against the Fortran arms' 16–24 MB) is its footprint. Blade's input generation is
the fastest of all arms (twin 2.8 ms C++ / 1.7 ms LLVM), which only sharpens the point.
The LLVM lane cannot run this kernel at all (refuses `mask` and `compound`, G9/G12).

### 4.4 dot — indicative (suppressed by drift)

Bundle flag: "trend: blade-llvm first-half vs second-half median moved 11.1%; blade-cpp
7.1%; ifx-expert 8.2%; blade-llvm-inputonly 5.6%; blade-cpp-inputonly 8.4%;
ifx-natural-inputonly 7.1%; ifx-expert-inputonly 6.3%; control compute moved 8.3% within
the block".

| arm | n | min (ms) | median (ms) | IQR (ms) | ratio [95% CI] | verdict | hypothesis | fold form | peak WS (MB) | incremental (ms) [CI] | incremental ratio [CI] |
|---|---|---|---|---|---|---|---|---|---|---|---|
| blade-llvm | 21 | 12.60 | 13.20 | 1.436 | ref | — | — | reordered (3 reassoc flags) | 50.2 | 1.731 [1.257, 2.332] | ref |
| blade-cpp | 21 | 18.65 | 19.28 | 1.349 | 1.460 [1.369, 1.518] | indicative | both lanes reported | reordered (1 SIMD_REDUCTION) | 50.9 | 1.571 [0.562, 1.952] | 0.907 [0.306, 1.310] |
| gfortran-natural | 21 | 19.76 | 20.28 | 1.278 | 1.536 [1.441, 1.594] | indicative | equivalent | — | 50.6 | 2.029 [1.687, 2.659] | 1.172 [0.846, 1.723] |
| gfortran-expert | 21 | 18.17 | 18.44 | 0.836 | 1.397 [1.329, 1.449] | indicative | equivalent | — | 50.6 | 1.125 [0.826, 1.732] | 0.650 [0.381, 1.217] |
| ifx-natural | 21 | 13.58 | 14.04 | 0.626 | 1.063 [1.011, 1.101] | indicative | equivalent | — | 50.2 | 2.262 [1.672, 2.596] | 1.306 [0.741, 1.936] |
| ifx-expert | 21 | 11.99 | 12.67 | 1.054 | 0.960 [0.905, 1.012] | indicative | equivalent | — | 50.2 | 1.569 [1.203, 2.163] | 0.906 [0.593, 1.453] |
| blade-llvm-inputonly | 21 | 10.96 | 11.47 | 0.804 | — | — | — | reordered (2 reassoc flags) | 50.1 | | |
| blade-cpp-inputonly | 21 | 17.43 | 17.71 | 1.502 | — | — | — | in-order | 50.9 | | |
| gfortran-natural-inputonly | 21 | 17.34 | 18.25 | 1.063 | — | — | — | — | 50.6 | | |
| gfortran-expert-inputonly | 21 | 16.90 | 17.32 | 0.917 | — | — | — | — | 50.7 | | |
| ifx-natural-inputonly | 21 | 11.26 | 11.78 | 0.970 | — | — | — | — | 50.2 | | |
| ifx-expert-inputonly | 21 | 10.54 | 11.10 | 0.798 | — | — | — | — | 50.2 | | |

Diagnosis (indicative). The bandwidth control behaves as a control: every incremental
interval contains 1 (0.65–1.31 point estimates on 1.1–2.3 ms of dot beyond 11–18 ms of
generation), i.e. the dot itself is equivalent across all six arms, and the totals split by
input generation speed — the LLVM lane and ifx generate in 11–12 ms, the C++ lane and
gfortran in 17–18 ms (the D9 point: generation cost is per arm, measured, never assumed).
The C++ lane's 1.46x total is its twin (17.7 vs 11.5 ms). Nothing here speaks for or
against Blade on the dot; it speaks about the generators.

### 4.5 saxpy — indicative (suppressed by drift)

Bundle flag: "trend: blade-llvm first-half vs second-half median moved 11.1%; blade-cpp
8.9%; ifx-expert 14.8%; blade-llvm-inputonly 12.4%; gfortran-natural-inputonly 12.0%;
gfortran-expert-inputonly 8.2%; ifx-expert-inputonly 8.5%; control memory moved 5.2%
within the block".

| arm | n | min (ms) | median (ms) | IQR (ms) | ratio [95% CI] | verdict | hypothesis | fold form | peak WS (MB) | incremental (ms) [CI] | incremental ratio [CI] |
|---|---|---|---|---|---|---|---|---|---|---|---|
| blade-llvm | 21 | 11.96 | 14.28 | 3.170 | ref | — | — | reordered (3 reassoc flags) | 49.6 | 6.083 [4.876, 7.832] | ref |
| blade-cpp | 21 | 16.70 | 17.89 | 3.701 | 1.253 [1.116, 1.435] | indicative | both lanes reported | reordered (1 SIMD_REDUCTION) | 50.6 | 5.777 [4.975, 7.800] | 0.950 [0.730, 1.407] |
| gfortran-natural | 21 | 16.51 | 17.47 | 1.643 | 1.224 [1.090, 1.349] | indicative | equivalent | — | 50.7 | 5.109 [4.021, 6.192] | 0.840 [0.597, 1.100] |
| gfortran-expert | 21 | 14.56 | 15.62 | 2.650 | 1.094 [0.975, 1.254] | indicative | equivalent | — | 50.7 | 3.622 [2.770, 5.728] | 0.595 [0.413, 0.957] |
| ifx-natural | 21 | 12.95 | 14.13 | 2.521 | 0.989 [0.881, 1.110] | indicative | equivalent | — | 50.2 | 5.848 [5.103, 7.107] | 0.962 [0.743, 1.273] |
| ifx-expert | 21 | 10.51 | 11.93 | 3.790 | 0.836 [0.742, 0.970] | indicative | equivalent | — | 50.2 | 4.293 [2.807, 5.824] | 0.706 [0.471, 1.045] |
| blade-llvm-inputonly | 21 | 7.398 | 8.194 | 1.264 | — | — | — | reordered (2 reassoc flags) | 34.9 | | |
| blade-cpp-inputonly | 21 | 11.73 | 12.11 | 0.633 | — | — | — | in-order | 35.7 | | |
| gfortran-natural-inputonly | 21 | 11.60 | 12.36 | 1.411 | — | — | — | — | 35.4 | | |
| gfortran-expert-inputonly | 21 | 11.31 | 12.00 | 1.498 | — | — | — | — | 35.4 | | |
| ifx-natural-inputonly | 21 | 7.540 | 8.277 | 1.141 | — | — | — | — | 34.9 | | |
| ifx-expert-inputonly | 21 | 7.202 | 7.636 | 1.454 | — | — | — | — | 34.9 | | |

Diagnosis (indicative). Totals within 0.84–1.25 of the LLVM lane, the bandwidth
equivalence predicted; the one would-be deviation is ifx-expert's 0.836 [0.742, 0.970],
whose incremental (4.3 ms vs Blade's 6.1 ms, ratio 0.71 [0.47, 1.05]) and gfortran-expert's
(3.6 ms, 0.60 [0.41, 0.96]) say the experts' store-plus-accumulate single loop saves most of
a pass over y2 — the fused-checksum freedom README §1.5 grants them, which Blade's two
loops (store y2 under `|> compute`, then the checksum fold) do not take. The IQRs here
(2.5–3.8 ms on 12–18 ms medians) are the session's widest: a 48 MB stream on an uncapped
clock.

## 5. mt8 x licensed x native — session 2 (partial) — **group-A rows SUPERSEDED**

**Two bundles.** Session 2 (`session-100329/`, 4113.6 s) measured the group-A blocks
with Blade threading only the kernel's level 0 while the Fortran arms threaded input
generation and the checksum read-back as well; the user's D44 decision (matched
parallelism) superseded those four blocks, the group-A Blade sources gained `where omp`
licences on their input maps and checksum nests (blade-A §14), and the **re-run**
`session-113651/` (2118.5 s; git fceb895d; 21 rounds each; 0 failures; no abort; no
excursion dropped; 5 of 5 blocks suppressed by trend/control checks) is what §5.1–5.4
and §5.10 report. The superseded numbers are kept in appendix §5A so the before/after of
the threading fix is visible. §5.5–5.9 (group B, controls) are from session 2 and stand.
Reading notes for the re-run (blade-A §14): the Blade arms now show `openmp (4
constructs)` — input map, kernel level 0, checksum nest, input sum; the threaded checksum
folds take the **Path B** form, chunks serial within each thread, no simd lanes (**G25**);
only the fold-kernel licence placement threads (a licence on the checksum's row map is
silently dropped, **G27**). Serial emissions of gram, comoment3 and pairkernel are
byte-identical after the change (their §3 rows stand); antisym's differs in its final
input-sum fold only and its serial block is re-measured (§3.1).

**Before/after at a glance (blade-cpp mt8 medians, ms):** antisym 29.7 → **22.6**;
comoment3 87.0 → **20.6**; gram 61.5 → **43.8**; pairkernel 56.5 → **16.1**. Ratios vs the
experts: antisym 0.33/0.35 → **0.46/0.48**; comoment3 0.064/0.048 → **0.24/0.20**; gram
0.35/0.33 → **0.48/0.46**; pairkernel 0.11/0.11 → **0.39/0.39**. Threading the preparation
and checksum removed the Amdahl term; what remains is the serial-regime kernel gap under
threads (G1, G1b, G3a) and the chunked, unvectorized checksum fold (G25).

### 5.1 antisym — indicative (suppressed by drift); re-run, matched parallelism

Bundle flag: "trend: gfortran-expert first-half vs second-half median moved 7.2%; trend:
control compute moved 22.9% within the block; control memory 8.8%".

| arm | n | min (ms) | median (ms) | IQR (ms) | ratio vs blade-cpp [95% CI] | verdict | hypothesis | fold form / parallelism | peak WS (MB) |
|---|---|---|---|---|---|---|---|---|---|
| blade-cpp | 21 | 20.98 | 22.58 | 2.201 | ref | — | — | reordered (6 SIMD_REDUCTION) / openmp (4 constructs) | 18.7 |
| gfortran-natural | 21 | 109.3 | 116.9 | 3.743 | 5.177 [5.060, 5.273] | indicative | WIN ~2x | openmp runtime linked | 23.8 |
| gfortran-expert | 21 | 9.466 | 10.38 | 1.237 | 0.460 [0.440, 0.478] | indicative | equivalence to modest loss | same | 33.1 |
| ifx-natural | 21 | 64.68 | 71.39 | 9.553 | 3.162 [3.009, 3.386] | indicative | WIN ~2x | same | 25.4 |
| ifx-expert | 21 | 9.988 | 10.90 | 1.045 | 0.483 [0.460, 0.515] | indicative | equivalence to modest loss | same | 34.6 |

Diagnosis (indicative). Before the fix Blade's mt8 total was 29.7 ms with serial
preparation and checksum; with all four phases threaded it is 22.6 ms (4.8x over its
109.3 ms serial C++), and the loss to the experts narrows from 3.0x (0.33 / 0.35) to
2.1–2.2x (0.46 / 0.48) — now the same ratio as in the serial regime (0.42 / 0.40 on the
LLVM reference, 1.47x larger on the C++ one), i.e. the mt8 gap IS the serial gap: **G1**
(per-cell operand reloads against the expert's 8x6 register tile) scales with threads on
both sides. The remaining difference between the serial and mt8 ratios is the Path B
checksum (**G25**: 8 serial chains where the experts' threaded `simd reduction` runs 8
vectorized ones). The win over the dense baselines (5.2x / 3.2x) sits between the serial
10.4x / 4.5x and the superseded 3.8x / 2.4x, as expected once Blade's preparation threads
too.

### 5.2 comoment3 — indicative (suppressed by drift); re-run, matched parallelism

Bundle flag: "control compute median 0.01292 s moved −8.9% vs the session baseline
0.01418 s; control memory median 0.005397 s moved −11.0% vs baseline 0.006067 s" (the
session's first block ran hot; later blocks sit BELOW the baseline — the rolling rule
fires either way).

| arm | n | min (ms) | median (ms) | IQR (ms) | ratio [95% CI] | verdict | hypothesis | fold form / parallelism | peak WS (MB) | attribution |
|---|---|---|---|---|---|---|---|---|---|---|
| blade-cpp | 21 | 20.01 | 20.62 | 0.875 | ref | — | — | reordered (4 SIMD_REDUCTION) / openmp (4 constructs) | 12.6 | checksum share 70% serial (C++, A §13); now threaded, Path B |
| gfortran-natural | 21 | 83.73 | 91.94 | 4.281 | 4.458 [4.295, 4.526] | indicative | WIN 4–6x | openmp runtime linked | 43.9 | |
| gfortran-expert | 21 | 4.615 | 4.998 | 0.299 | 0.242 [0.233, 0.248] | indicative | equivalence to modest loss | same | 14.5 | |
| ifx-natural | 21 | 101.5 | 111.5 | 6.531 | 5.406 [5.257, 5.613] | indicative | WIN 4–6x | same | 45.4 | |
| ifx-expert | 21 | 3.796 | 4.025 | 0.132 | 0.195 [0.191, 0.199] | indicative | equivalence to modest loss | same | 15.8 | |

Diagnosis (indicative). The largest before/after in the suite: 87.0 → 20.6 ms (the
superseded figure was 1.26x over the 109.7 ms serial C++; the matched one is 5.3x). The
loss to the experts goes from 16–21x (0.064 / 0.048) to 4.1–5.1x (0.242 / 0.195) — still
wider than the serial 3.6x (0.277 / 0.282 on the LLVM reference) because two things do not
scale on Blade's side: the kernel's inner loop (**G1b**: a multiply plus an FMA per term
with 3 loads against the expert's hoisted pair product, plus **G1**'s per-cell reloads) and
the p³ canonicalizing checksum pass, which is now 8 threads of serial `canon_fold` reads
(**G3a/G3b** under **G25**) against the experts' vectorized threaded sum. The win over the
dense cubes (4.5x / 5.4x) is now where the hypothesis put it (4–6x, the cell ratio 5.89),
whereas the superseded 1.1x / 1.4x was the serial checksum.

### 5.3 gram — indicative (suppressed by drift); re-run, matched parallelism

Bundle flag: "trend: blade-cpp first-half vs second-half median moved 10.7%;
gfortran-natural 8.7%; ifx-expert 5.9%; control compute median −9.5% vs baseline; control
memory −11.4%".

| arm | n | min (ms) | median (ms) | IQR (ms) | ratio [95% CI] | verdict | hypothesis | fold form / parallelism | peak WS (MB) | notes |
|---|---|---|---|---|---|---|---|---|---|---|
| blade-cpp | 21 | 40.06 | 43.75 | 5.286 | ref | — | — | reordered (3 SIMD_REDUCTION) / openmp (4 constructs) | 27.8 | |
| gfortran-natural | 21 | 508.4 | 614.1 | 98.89 | 14.034 [13.086, 14.946] | indicative | loss or equivalent | openmp runtime linked | 37.3 | `matmul` unannotated: parallel preparation, serial kernel (bundle note) |
| gfortran-expert | 21 | 19.37 | 21.19 | 0.815 | 0.484 [0.454, 0.515] | indicative | equivalence to modest loss | same | 52.1 | |
| ifx-natural | 21 | 594.5 | 631.1 | 16.28 | 14.423 [13.507, 15.089] | indicative | loss or equivalent | same | 58.3 | same note |
| ifx-expert | 21 | 17.91 | 20.02 | 1.131 | 0.458 [0.427, 0.481] | indicative | equivalence to modest loss | same | 53.7 | |

Diagnosis (indicative). 61.5 → 43.8 ms (6.3x over the 275.2 ms serial C++ — the best
scaling of the four, as gram's serial phases were the smallest share); the loss to the
experts narrows from 2.8–3.0x to 2.1–2.2x (0.484 / 0.458), again matching the serial
ratio on the C++ reference (0.52 / 0.47 against the LLVM lane, 1.76x larger on the C++
one): **G1** under threads, with **G26** (the C++ lane's single-accumulator simd reduction)
still in the per-cell dot. The `matmul` baselines are unchanged at 614 / 631 ms — the
intrinsic is unannotated and serial ("parallel preparation, serial kernel", M24) — so the
14x would-be win is over a serial kernel and is labelled as such; Blade's threaded
triangle is 14x faster than the runtimes' serial `matmul`, not than a threaded one.

### 5.4 pairkernel — indicative (suppressed by drift); re-run, matched parallelism

Bundle flag: "trend: gfortran-natural first-half vs second-half median moved 7.3%;
ifx-natural 8.0%; control compute median −7.4% vs baseline, trend 9.9%; control memory
−11.6%".

| arm | n | min (ms) | median (ms) | IQR (ms) | ratio [95% CI] | verdict | hypothesis | fold form / parallelism | peak WS (MB) | attribution |
|---|---|---|---|---|---|---|---|---|---|---|
| blade-cpp | 21 | 14.24 | 16.11 | 2.726 | ref | — | — | reordered (1 SIMD_REDUCTION) / openmp (4 constructs) | 29.8 | checksum share 57% serial (C++); now threaded, Path B |
| gfortran-natural | 21 | 13.61 | 15.28 | 2.157 | 0.948 [0.877, 1.013] | indicative | WIN ~2x | openmp runtime linked | 53.3 | |
| gfortran-expert | 21 | 5.746 | 6.262 | 0.323 | 0.389 [0.373, 0.405] | indicative | equivalent | same | 29.5 | |
| ifx-natural | 21 | 14.76 | 16.43 | 2.150 | 1.019 [0.964, 1.124] | indicative | WIN ~2x | same | 54.8 | |
| ifx-expert | 21 | 5.802 | 6.209 | 0.388 | 0.385 [0.374, 0.406] | indicative | equivalent | same | 30.9 | |

Diagnosis (indicative). 56.5 → 16.1 ms (4.8x over the 77.3 ms serial C++); the loss to the
experts from 9x (0.11) to 2.6x (0.39), and the dense baselines from a 3.2–3.4x would-be
loss to equivalence (0.95 / 1.02). The serial-regime reading (kernel equivalent to the
expert, the whole gap in the checksum pass — **G3a**) carries over: the kernel's
transcendental loop scales on both sides, and what keeps Blade at 2.6x behind is the
6.27M-read logical checksum, now 8 serial chains of out-of-line `canon_fold` reads
(**G3b** under **G25**) against the experts' vectorized threaded triangle sum. The
hypothesised ~2x over the dense baselines does not appear under threads: their 2x extra
`exp` work parallelizes as well as Blade's half, and Blade's checksum pass is the
difference that remains. The §5.1–5.4 numbers are
kept here as an APPENDIX RECORD — "superseded: measured with serial preparation and
checksum; re-run pending" — and are not cited in the headline. §5.5–5.9 (group B and the
controls) stand as measured; §5.10 (saxpy) is unrun pending the re-run.

Bundle `results/2026-10-18-LAPTOP-350H6LDB/session-100329/` (same host/build header;
duration 4113.6 s; 0 failures/DQ; one abort, §5.10). Reference `blade-cpp` for every
kernel (the LLVM lane has no thread runtime: every `blade-llvm` row is `serial-only`, n =
0, and is omitted from the tables below). `onepass` and `recur` Blade arms are
`no-parallel-form` (kernel.json; spec), so those two kernels have Fortran mt8 numbers and
no ratio. `maskagg` is serial by protocol and not in this regime. Threads unbound,
`OMP_NUM_THREADS=8`, team creation inside the region (D42). The `parallelism` column is the
bundle's: `openmp (k construct(s))` for Blade = k threaded loops in the emission (the
kernel's level 0 and, in group B, the input map), `openmp runtime linked` for Fortran.
**Read §12.1 before comparing these mt8 numbers across languages: the Fortran arms thread
input generation and the checksum read-back in every kernel, while the group-A Blade
sources thread only the kernel's level 0 — the serial checksum pass (57–70% of the C++
lane's serial time on comoment3/pairkernel) is included in every Blade mt8 number
below.** n = 7 for gram and comoment3 (the mt8 minimum), 21 otherwise.

### 5.5 fusedchain — indicative (suppressed by drift); twins present

Bundle flag: "trend: blade-cpp first-half vs second-half median moved 7.7%;
gfortran-natural 9.9%; gfortran-expert-inputonly 7.4%; control memory trend 5.3%".

| arm | n | min (ms) | median (ms) | IQR (ms) | ratio [95% CI] | verdict | hypothesis | parallelism | peak WS (MB) | incremental (ms) [CI] | incremental ratio [CI] |
|---|---|---|---|---|---|---|---|---|---|---|---|
| blade-cpp | 21 | 3.739 | 4.128 | 0.530 | ref | — | — | in-order fold / openmp (2 constructs: input map + fold) | 20.8 | 0.611 [0.239, 0.927] | ref |
| gfortran-natural | 21 | 11.14 | 12.47 | 1.589 | 3.020 [2.824, 3.239] | indicative | WIN 1.5–2.5x | openmp runtime linked | **66.4** | 8.881 [8.439, 9.459] | 14.54 [9.490, 38.11] |
| gfortran-expert | 21 | 3.037 | 3.253 | 0.366 | 0.788 [0.738, 0.829] | indicative | equivalent | same | 20.6 | 0.359 [0.065, 0.579] | 0.588 [0.102, 1.551] |
| ifx-natural | 21 | 11.73 | 12.63 | 0.823 | 3.060 [2.847, 3.186] | indicative | WIN 1.5–2.5x | same | **68.0** | 8.508 [8.014, 8.755] | 13.93 [9.047, 34.76] |
| ifx-expert | 21 | 3.298 | 3.509 | 0.343 | 0.850 [0.799, 0.916] | indicative | equivalent | same | 22.1 | 0.329 [0.095, 0.542] | 0.539 [0.154, 1.405] |
| blade-cpp-inputonly | 21 | 3.155 | 3.517 | 0.541 | — | — | — | openmp (1 construct) | 20.8 | | |
| gfortran-natural-inputonly | 21 | 3.444 | 3.585 | 0.180 | — | — | — | openmp runtime linked | 20.6 | | |
| gfortran-expert-inputonly | 21 | 2.663 | 2.894 | 0.429 | — | — | — | same | 20.6 | | |
| ifx-natural-inputonly | 21 | 3.896 | 4.124 | 0.119 | — | — | — | same | 22.1 | | |
| ifx-expert-inputonly | 21 | 3.057 | 3.180 | 0.256 | — | — | — | same | 22.1 | | |

Diagnosis (indicative). The fused chain keeps its ~3x over the staged baselines (whose
three temporaries are untouched by threads: 8.5–8.9 ms incremental); against the experts
Blade's total is 0.79 / 0.85 (would-be detectable loss) with incrementals 0.6 / 0.3 ms on
intervals that contain 1 — the 3-ms totals are dominated by generation (3.5 vs 2.9–3.2 ms
twins) and team start-up (0.4–0.9 ms, D42), not by the chain. Note the bundle's fold form
for the mt8 C++ arm reads `in-order`: under threads the licensed fold takes the Path B
chunked form, serial within each chunk (**G25**). Working-set breach again on the natural
arms (66.4 / 68.0 MB).

### 5.6 onepass — Fortran arms only (Blade: no parallel form); indicative

Bundle flag: "trend: ifx-expert first-half vs second-half median moved 6.7%;
gfortran-natural-inputonly 8.0%; ifx-expert-inputonly 8.7%; control compute trend 7.1%".
No ratio (no Blade reference in this regime).

| arm | n | min (ms) | median (ms) | IQR (ms) | hypothesis | twin median (ms) | peak WS (MB) |
|---|---|---|---|---|---|---|---|
| blade-cpp | 0 | — | — | — | no parallel form (the `<&!>` join has a simd form, no thread form; G7) | — | — |
| gfortran-natural | 21 | 14.55 | 15.34 | 0.628 | WIN 2–3x (serial) | 9.145 | 43.5 |
| gfortran-expert | 21 | 7.733 | 9.075 | 0.812 | equivalent (serial) | 7.356 | 43.5 |
| ifx-natural | 21 | 15.04 | 15.76 | 0.384 | | 9.553 | 45.0 |
| ifx-expert | 21 | 8.736 | 9.403 | 0.769 | | 7.625 | 45.0 |

Reading: the Fortran experts' 8-thread reduction takes 9.1 / 9.4 ms against Blade's
SERIAL 11.7 ms (LLVM) / 16.8 ms (C++) from session 1b — the threaded experts win outright
over a kernel Blade cannot thread; reported as a gap (G7), not hidden.

### 5.7 recur — Fortran arms only (Blade: no parallel form); indicative

Bundle flag: "trend: ifx-natural 10.1%; ifx-expert 17.4%; control compute median +11.1%
vs baseline; control memory +8.3%".

| arm | n | min (ms) | median (ms) | IQR (ms) | hypothesis | peak WS (MB) |
|---|---|---|---|---|---|---|
| blade-cpp | 0 | — | — | — | no parallel form (no licence slot inside a `let rec` step; G5) | — |
| gfortran-natural | 21 | 71.30 | 73.24 | 2.285 | equivalent (serial) | 51.9 |
| gfortran-expert | 21 | 6.570 | 7.207 | 1.139 | LOSS 1.5–3x (serial) | 51.3 |
| ifx-natural | 21 | 15.07 | 16.36 | 2.069 | | 53.4 |
| ifx-expert | 21 | 6.771 | 7.911 | 1.568 | | 52.8 |

Reading: the threaded experts reach 7.2 / 7.9 ms (from 10.5 / 11.3 serial — the time loop
limits scaling) against Blade's serial 117 / 134 ms (session 1a): the G6 allocator loss
compounded by the missing parallel form (G5). gfortran-natural's `workshare` arm got
SLOWER under threads (19.7 → 73.2 ms) — the Fortran agent's record explains the
`workshare` lowering; noted, not diagnosed here.

### 5.8 stencil — indicative (suppressed by drift); twins present

Bundle flag (abridged): "trend: blade-cpp 18.9%; gfortran-natural 7.6%; gfortran-expert
9.0%; ifx-natural 6.5%; ifx-expert 12.4%; blade-cpp-inputonly 10.0%;
gfortran-natural-inputonly 12.8%; control compute +10.7% vs baseline, trend 9.5%; control
memory trend 7.6%".

| arm | n | min (ms) | median (ms) | IQR (ms) | ratio [95% CI] | verdict | hypothesis | parallelism | peak WS (MB) | incremental (ms) [CI] | incremental ratio [CI] |
|---|---|---|---|---|---|---|---|---|---|---|---|
| blade-cpp | 21 | 9.584 | 11.49 | 1.915 | ref | — | — | reordered (3 SIMD_REDUCTION) / openmp (3 constructs: inputs, stencil rows, checksum) | 51.2 | 6.293 [5.562, 7.290] | ref |
| gfortran-natural | 21 | 8.430 | 9.477 | 0.764 | 0.825 [0.757, 0.894] | indicative | equivalent | openmp runtime linked | 51.2 | 4.899 [4.588, 5.327] | 0.778 [0.650, 0.917] |
| gfortran-expert | 21 | 7.594 | 8.968 | 1.189 | 0.780 [0.713, 0.833] | indicative | equivalent | same | 51.1 | 4.372 [3.748, 4.718] | 0.695 [0.582, 0.789] |
| ifx-natural | 21 | 9.775 | 10.19 | 0.887 | 0.887 [0.818, 0.954] | indicative | equivalent | same | 52.7 | 5.049 [4.800, 5.485] | 0.802 [0.701, 0.912] |
| ifx-expert | 21 | 8.381 | 9.120 | 1.541 | 0.794 [0.730, 0.867] | indicative | equivalent | same | 52.7 | 4.021 [3.618, 5.046] | 0.639 [0.550, 0.797] |
| blade-cpp-inputonly | 21 | 4.244 | 5.197 | 0.614 | — | — | — | openmp (1 construct) | 28.5 | | |
| gfortran-natural-inputonly | 21 | 4.179 | 4.578 | 0.661 | — | — | — | openmp runtime linked | 28.3 | | |
| gfortran-expert-inputonly | 21 | 3.923 | 4.596 | 0.338 | — | — | — | same | 28.3 | | |
| ifx-natural-inputonly | 21 | 4.793 | 5.141 | 0.291 | — | — | — | same | 29.8 | | |
| ifx-expert-inputonly | 21 | 4.557 | 5.099 | 0.466 | — | — | — | same | 29.8 | | |

Diagnosis (indicative). The only group-B kernel whose Blade source threads all three
phases (3 constructs): totals 0.78–0.89 against the Fortran arms (would-be detectable
losses), incrementals 0.64–0.80 — Blade spends 6.3 ms beyond its twin, Fortran 4.0–5.0 ms.
Two candidate mechanisms, neither measured here: the chunked `Path B` checksum fold
(serial within each chunk, G25) versus Fortran's `simd`+threads reduction, and the
stencil's row-threaded halo nest writing `lap` before a second threaded pass reads it,
where the experts fuse the checksum into the hot row. A 10–12 ms kernel at 8 threads on an
uncapped clock: indicative only.

### 5.9 dot — indicative (suppressed by drift); twins present

Bundle flag: "trend: blade-cpp-inputonly 15.4%; gfortran-natural-inputonly 6.4%;
ifx-expert-inputonly 8.1%".

| arm | n | min (ms) | median (ms) | IQR (ms) | ratio [95% CI] | verdict | hypothesis | parallelism | peak WS (MB) | incremental (ms) [CI] | incremental ratio [CI] |
|---|---|---|---|---|---|---|---|---|---|---|---|
| blade-cpp | 21 | 10.17 | 12.00 | 0.924 | ref | — | — | in-order fold (Path B chunks) / openmp (2 constructs) | 51.3 | 1.677 [0.969, 2.381] | ref |
| gfortran-natural | 21 | 10.73 | 11.41 | 0.657 | 0.951 [0.928, 0.989] | indicative | equivalent | openmp runtime linked | 51.1 | 2.595 [2.235, 3.038] | 1.548 [1.081, 2.724] |
| gfortran-expert | 21 | 9.646 | 10.53 | 0.603 | 0.878 [0.859, 0.905] | indicative | equivalent | same | 51.1 | 2.011 [1.794, 2.308] | 1.200 [0.809, 2.141] |
| ifx-natural | 21 | 11.30 | 11.83 | 0.373 | 0.986 [0.963, 1.025] | indicative | equivalent | same | 52.7 | 2.403 [1.975, 2.696] | 1.433 [1.019, 2.425] |
| ifx-expert | 21 | 10.23 | 10.96 | 0.443 | 0.914 [0.888, 0.948] | indicative | equivalent | same | 52.7 | 1.940 [1.710, 2.273] | 1.157 [0.797, 2.049] |
| blade-cpp-inputonly | 21 | 7.510 | 10.32 | 1.512 | — | — | — | openmp (1 construct) | 51.3 | | |
| gfortran-natural-inputonly | 21 | 8.162 | 8.814 | 1.058 | — | — | — | openmp runtime linked | 51.1 | | |
| gfortran-expert-inputonly | 21 | 7.465 | 8.519 | 0.374 | — | — | — | same | 51.1 | | |
| ifx-natural-inputonly | 21 | 8.582 | 9.429 | 0.778 | — | — | — | same | 52.7 | | |
| ifx-expert-inputonly | 21 | 7.999 | 9.023 | 0.670 | — | — | — | same | 52.7 | | |

Diagnosis (indicative). Totals 0.88–0.99 (equivalent to would-be detectable loss);
incrementals favour Blade (1.16–1.55, the gfortran-natural and ifx-natural intervals
exclude 1): the threaded dot beyond the twin is 1.7 ms in Blade against 1.9–2.6 ms in
Fortran, while Blade's twin is the slowest generator (10.3 ms, with the block's largest
within-block trend, 15.4%). A 48 MB stream on 8 threads is bandwidth-bound in every arm;
nothing here separates the languages beyond generation.

### 5.10 saxpy — indicative (suppressed by drift); from the re-run `session-113651/` (the session-2 block was aborted, §11)

Bundle flag: "trend: blade-cpp first-half vs second-half median moved 6.2%;
blade-cpp-inputonly 11.6%; gfortran-expert-inputonly 9.6%; control compute median −8.8%
vs baseline; control memory −10.4%".

| arm | n | min (ms) | median (ms) | IQR (ms) | ratio [95% CI] | verdict | hypothesis | fold form / parallelism | peak WS (MB) | incremental (ms) [CI] | incremental ratio [CI] |
|---|---|---|---|---|---|---|---|---|---|---|---|
| blade-cpp | 21 | 10.49 | 11.40 | 0.598 | ref | — | — | in-order (Path B) / openmp (3 constructs) | 51.2 | 4.429 [4.113, 4.699] | ref |
| gfortran-natural | 21 | 9.706 | 10.36 | 0.629 | 0.909 [0.876, 0.942] | indicative | equivalent | openmp runtime linked | 51.3 | 4.351 [3.939, 4.677] | 0.982 [0.877, 1.091] |
| gfortran-expert | 21 | 8.286 | 8.963 | 0.385 | 0.786 [0.766, 0.808] | indicative | equivalent | same | 51.3 | 3.402 [2.927, 3.622] | 0.768 [0.659, 0.846] |
| ifx-natural | 21 | 9.888 | 10.59 | 0.678 | 0.929 [0.892, 0.963] | indicative | equivalent | same | 52.9 | 4.279 [3.976, 4.611] | 0.966 [0.871, 1.068] |
| ifx-expert | 21 | 8.817 | 9.224 | 0.305 | 0.809 [0.792, 0.830] | indicative | equivalent | same | 52.9 | 3.205 [3.045, 3.482] | 0.724 [0.679, 0.795] |
| blade-cpp-inputonly | 21 | 5.811 | 6.970 | 0.649 | — | — | — | openmp (1 construct) | 36.3 | | |
| gfortran-natural-inputonly | 21 | 5.430 | 6.012 | 0.445 | — | — | — | openmp runtime linked | 36.0 | | |
| gfortran-expert-inputonly | 21 | 5.137 | 5.560 | 0.724 | — | — | — | same | 36.0 | | |
| ifx-natural-inputonly | 21 | 5.915 | 6.312 | 0.510 | — | — | — | same | 37.6 | | |
| ifx-expert-inputonly | 21 | 5.338 | 6.019 | 0.512 | — | — | — | same | 37.6 | | |

Diagnosis (indicative). The bandwidth control under 8 threads: totals 0.79–0.93 of Blade
(would-be detectable losses to the experts, equivalence to the baselines); incrementally
the baselines match Blade (0.98 / 0.97, intervals contain 1) and the experts spend ~1 ms
less beyond their twins (0.77 / 0.72) — their single store-plus-accumulate loop against
Blade's store pass followed by a Path B checksum fold (**G25**), the same reading as the
serial regime's. The block completed without excursion under the new D45 rule (no sample
dropped).

### 5A. Appendix — the superseded group-A mt8 rows (session-100329, kernel-only threading)

Kept as the record of the before/after of the D44 threading fix; not cited in the headline. Blade threaded only the kernel's level 0 here (`openmp (1 construct)`); the Fortran arms threaded generation, kernel and checksum.

### 5A.1 antisym — superseded record (measured with serial preparation and checksum; re-run pending). Clean block at the time

| arm | n | min (ms) | median (ms) | IQR (ms) | ratio vs blade-cpp [95% CI] | verdict | hypothesis | fold form / parallelism | peak WS (MB) |
|---|---|---|---|---|---|---|---|---|---|
| blade-cpp | 21 | 28.97 | 29.68 | 0.701 | ref | — | — | reordered (6 SIMD_REDUCTION) / openmp (1 construct): kernel level 0; inputs, checksum nest, input sum serial | 18.5 |
| gfortran-natural | 21 | 106.7 | 112.5 | 1.929 | 3.790 [3.731, 3.820] | **win** | WIN ~2x | openmp runtime linked (inputs, kernel, checksum threaded) | 23.7 |
| gfortran-expert | 21 | 9.104 | 9.765 | 1.022 | 0.329 [0.317, 0.348] | **loss** | equivalence to modest loss | same | 33.0 |
| ifx-natural | 21 | 61.88 | 70.30 | 5.985 | 2.368 [2.280, 2.467] | **win** | WIN ~2x | same | 25.2 |
| ifx-expert | 21 | 9.590 | 10.27 | 0.725 | 0.346 [0.331, 0.355] | **loss** | equivalence to modest loss | same | 34.5 |

Diagnosis. Blade's threaded triangle (dynamic schedule on level 0, D-plan `[omp v1]`)
takes 29.7 ms against its 109.3 ms serial C++ (3.7x on 8 threads); the experts go from
31.0 / 29.4 ms to 9.8 / 10.3 ms (3.2x / 2.9x), the dense baselines from 773 / 332 to 112 /
70 ms (6.9x / 4.7x — they also thread generation and checksum). The serial loss to the
experts widens slightly from 2.4x (serial, 0.416 / 0.395) to 3.0x (0.329 / 0.346): **G1**
is unchanged by threads, and the serial checksum nest and input sum in Blade's source
(§12.1) cap its speed-up by Amdahl. The win over the dense baselines narrows from 10.4x /
4.5x to 3.8x / 2.4x for the same reason (the baselines thread everything).

### 5A.2 comoment3 — superseded record (serial preparation and checksum; re-run pending); was indicative, n = 7

Bundle flag: "trend: ifx-expert first-half vs second-half median moved 16.0%; control
compute median 0.01513 s moved +18.0% vs baseline 0.01283 s; trend: control compute moved
22.9% within the block; control memory +9.9%; trend 8.4%".

| arm | n | min (ms) | median (ms) | IQR (ms) | ratio [95% CI] | verdict | hypothesis | parallelism | peak WS (MB) |
|---|---|---|---|---|---|---|---|---|---|
| blade-cpp | 7 | 85.93 | 86.98 | 1.226 | ref | — | — | openmp (1 construct): kernel level 0 only | 12.4 |
| gfortran-natural | 7 | 94.01 | 99.23 | 4.487 | 1.141 [1.086, 1.167] | indicative | WIN 4–6x | openmp runtime linked | 43.7 |
| gfortran-expert | 7 | 4.848 | 5.536 | 0.730 | 0.064 [0.055, 0.066] | indicative | equivalence to modest loss | same | 14.3 |
| ifx-natural | 7 | 116.0 | 122.1 | 7.885 | 1.404 [1.342, 1.489] | indicative | WIN 4–6x | same | 45.2 |
| ifx-expert | 7 | 3.965 | 4.208 | 0.481 | 0.048 [0.047, 0.057] | indicative | equivalence to modest loss | same | 15.6 |

Diagnosis (indicative). The Amdahl case in the clearest form: Blade's mt8 C++ figure
(87.0 ms) barely improves on its serial C++ (109.7 ms, 1.26x on 8 threads) because the
checksum pass — 70% of the C++ lane's serial time on this kernel (A §13, G3a/G3b) — and the
input sum stay serial in the source (§12.1), while the experts thread everything and reach
4.2–5.5 ms (2.1–2.8x on their 11.5–11.7 ms), putting Blade ~16–21x behind (0.064 / 0.048)
and only 1.1–1.4x ahead of the dense cubes (which also thread all three phases). With the
attribution (kernel-only ≈ 30% of 110 ms ≈ 33 ms serial), a threaded kernel alone cannot
explain 87 ms; the serial 70% does. Attribution column: `kernel-only estimate, informal:
checksum share 70% (C++), serial in mt8`.

### 5A.3 gram — superseded record (serial preparation and checksum; re-run pending); was indicative, n = 7

Bundle flag: "trend: gfortran-natural first-half vs second-half median moved 7.7%; trend:
control compute moved 21.3% within the block; control memory 9.0%".

| arm | n | min (ms) | median (ms) | IQR (ms) | ratio [95% CI] | verdict | hypothesis | parallelism | peak WS (MB) | notes |
|---|---|---|---|---|---|---|---|---|---|---|
| blade-cpp | 7 | 58.47 | 61.53 | 5.836 | ref | — | — | openmp (1 construct): kernel level 0 only | 27.6 | |
| gfortran-natural | 7 | 513.4 | 607.7 | 83.26 | 9.876 [8.820, 10.883] | indicative | loss or equivalent | openmp runtime linked | 37.1 | `matmul` unannotated: parallel preparation, serial kernel (bundle note; FORT records whether the runtime threads matmul) |
| gfortran-expert | 7 | 20.57 | 21.65 | 1.161 | 0.352 [0.322, 0.371] | indicative | equivalence to modest loss | same | 51.9 | |
| ifx-natural | 7 | 622.0 | 643.6 | 15.49 | 10.460 [9.565, 10.837] | indicative | loss or equivalent | same | 58.1 | same note |
| ifx-expert | 7 | 20.02 | 20.52 | 0.435 | 0.334 [0.298, 0.344] | indicative | equivalence to modest loss | same | 53.6 | |

Diagnosis (indicative). Blade's threaded triangle: 275 → 61.5 ms (4.5x); the experts: 81.7
/ 73.2 → 21.7 / 20.5 ms (3.8x / 3.6x); the loss to the experts is 2.8–3.0x (0.352 / 0.334)
against the serial 1.9–2.1x — **G1** plus the serial checksum/input sum (§12.1; gram's
checksum share is small, so the widening is smaller than comoment3's). The `matmul`
baselines did NOT speed up (602 → 608 ms, 576 → 644 ms): the intrinsic is unannotated and
the runtimes do not thread it — "parallel preparation, serial kernel" (M24) — so the
would-be 9.9x / 10.5x Blade win here is a win over a serial kernel and is labelled as such.

### 5A.4 pairkernel — superseded record (serial preparation and checksum; re-run pending); was indicative

Bundle flag: "control compute median 0.01457 s moved +13.6% vs baseline 0.01283 s; trend
5.4%; control memory trend 8.5%".

| arm | n | min (ms) | median (ms) | IQR (ms) | ratio [95% CI] | verdict | hypothesis | parallelism | peak WS (MB) |
|---|---|---|---|---|---|---|---|---|---|
| blade-cpp | 21 | 55.19 | 56.52 | 1.326 | ref | — | — | openmp (1 construct): kernel level 0 only | 29.6 |
| gfortran-natural | 21 | 15.44 | 16.62 | 2.000 | 0.294 [0.285, 0.319] | indicative | WIN ~2x | openmp runtime linked | 53.2 |
| gfortran-expert | 21 | 5.938 | 6.237 | 0.364 | 0.110 [0.109, 0.115] | indicative | equivalent | same | 29.3 |
| ifx-natural | 21 | 16.93 | 17.80 | 0.994 | 0.315 [0.307, 0.324] | indicative | WIN ~2x | same | 54.6 |
| ifx-expert | 21 | 5.850 | 6.318 | 0.441 | 0.112 [0.107, 0.115] | indicative | equivalent | same | 30.8 |

Diagnosis (indicative). The serial-regime picture (kernel equivalent, the whole 1.3–1.4x
loss in the checksum pass, G3a) turns into a 3.2–3.4x would-be LOSS to the dense baselines
and a 9x loss to the experts: Blade's kernel threads (77.3 → 56.5 ms total, only 1.37x)
but its checksum pass — 57% of the C++ serial time, 6.27M canonicalizing reads through the
out-of-line `canon_fold` (G3b) — stays serial, while every Fortran arm threads kernel,
generation and checksum. This is the §12.1 asymmetry at its most visible; the number is
reported as "serial checksum included".


## 6. repro — session 3

The association-reproducible opt-in: Blade `BLADE_FP_REPRO=1` (every fold one ascending
serial chain, no lanes, no `simd reduction`, no `reassoc`; fold form `in-order` in every
Blade row), Fortran without `BENCH_LICENSED` (no `simd reduction` anywhere). The expert
Fortran's KERNEL is unchanged between regimes — its register-tiled outer-product order
keeps each cell's summation order and still vectorizes over i, which needs no reordering
licence (README §3 matched-loop table, G1) — so only its checksum sums lose their `simd
reduction`.

### 6.1 serial x repro x native — session 3a (`session-122606/`)

Group A + recur + stencil; 11 rounds (group A), 21 (recur, stencil); 2008.6 s; 0
failures; no abort; no excursion. **All six blocks suppressed**: the compute control's
FIRST block ran high (16.5 ms baseline, 31% within-block trend during antisym), so every
later block sits 12–23% BELOW the baseline and trips the rolling rule downward — the
flags are a session-baseline artefact, not a within-block instability on the kernels'
side (their IQRs are the session's tightest), but the rule is the rule and the verdicts are
indicative. Reference `blade-llvm`.

**The headline of the repro regime.** Blade's serial association is latency-bound: with
every fold reduced to one dependent chain, blade-llvm takes 508 ms on antisym (74 ms
licensed), 1219 ms on gram (157), 157 ms on comoment3 (41.5); blade-cpp 411 / 954 / 181
ms (109 / 275 / 110). The expert Fortran is UNCHANGED (antisym 32.7 vs 30.7 ms, gram 77.8
vs 81.7, comoment3 11.8 vs 11.5), because its ordering was never a reordering — and so in
repro the experts are **13–17x faster** (ratios antisym 0.064 / 0.062, gram 0.064 /
0.058, comoment3 0.075 / 0.075), where the licensed regime had them at 2.1–3.6x. Two
readings follow. **(a)** This is the measured cost of the OLD Blade default — until
5f4b33ea every build was this build — and the quantitative justification of the policy
flip (D40/D43, `docs/plans/plan-fp-default-fastest.md`): the reproducible association
costs 3.5–8x on the kernels Blade is meant to win. **(b)** It is the strongest evidence
for **G1**: the expert proves that an i-vectorized, register-tiled outer product is BOTH
bitwise-reproducible (each cell's sum in ascending s) AND fast; a strict-legal tile of
that form would make Blade's repro build as fast as its licensed one, with no reordering
licence needed. The remaining kernels behave as their serial-licensed selves (pairkernel
0.82 / 0.75: the kernel has no fold; recur 0.12: the allocator, G6; stencil 1.05 / 0.84:
bandwidth), and the dense baselines — whose nests the compilers never vectorized across
the sum — are also unchanged, so gram's `matmul` baseline is now FASTER than Blade's
repro build (0.456 / 0.467).

#### antisym — indicative

| arm | n | min (ms) | median (ms) | IQR (ms) | ratio [95% CI] | verdict | fold form | peak WS (MB) | licensed median (ms, §3.1) |
|---|---|---|---|---|---|---|---|---|---|
| blade-llvm | 11 | 504.5 | 508.2 | 4.205 | ref | — | in-order | 17.4 | 73.95 |
| blade-cpp | 11 | 407.3 | 411.3 | 5.101 | 0.809 [0.802, 0.816] | indicative (C++ lane FASTER than LLVM here) | in-order | 18.3 | 107.9 |
| gfortran-natural | 11 | 768.0 | 773.9 | 4.098 | 1.523 [1.510, 1.529] | indicative | — | 23.4 | 770.9 |
| gfortran-expert | 11 | 31.05 | 32.72 | 1.761 | 0.064 [0.062, 0.066] | indicative | — | 32.7 | 30.74 |
| ifx-natural | 11 | 330.9 | 335.2 | 6.682 | 0.659 [0.652, 0.666] | indicative | — | 23.0 | 332.9 |
| ifx-expert | 11 | 29.26 | 31.45 | 1.656 | 0.062 [0.059, 0.063] | indicative | — | 32.2 | 29.54 |

Note the lane order flips in repro: the C++ lane's single chain (411 ms) beats the LLVM
lane's (508 ms) — with no lanes to lose, the LLVM lane's 64x64 cache bricks with a
per-cell dot (G1 note) cost more than the C++ lane's plain nest; the 1.46x LLVM advantage
of the licensed regime was its four FMA accumulators.

#### comoment3 — indicative

| arm | n | min (ms) | median (ms) | IQR (ms) | ratio [95% CI] | verdict | fold form | peak WS (MB) | licensed median (ms, §3.2) |
|---|---|---|---|---|---|---|---|---|---|
| blade-llvm | 11 | 156.4 | 156.8 | 0.550 | ref | — | in-order | 11.3 | 41.49 |
| blade-cpp | 11 | 179.6 | 180.6 | 1.100 | 1.152 [1.147, 1.159] | indicative | in-order | 12.2 | 109.7 |
| gfortran-natural | 11 | 599.6 | 602.6 | 4.986 | 3.842 [3.822, 3.867] | indicative | — | 43.4 | 603.4 |
| gfortran-expert | 11 | 11.28 | 11.77 | 0.612 | 0.075 [0.073, 0.077] | indicative | — | 12.3 | 11.49 |
| ifx-natural | 11 | 717.8 | 722.0 | 4.725 | 4.604 [4.582, 4.618] | indicative | — | 43.0 | 723.7 |
| ifx-expert | 11 | 11.40 | 11.70 | 0.320 | 0.075 [0.074, 0.076] | indicative | — | 11.8 | 11.70 |

#### gram — indicative

| arm | n | min (ms) | median (ms) | IQR (ms) | ratio [95% CI] | verdict | fold form | peak WS (MB) | licensed median (ms, §3.3) |
|---|---|---|---|---|---|---|---|---|---|
| blade-llvm | 11 | 1217 | 1219 | 4.434 | ref | — | in-order | 26.6 | 156.7 |
| blade-cpp | 11 | 949.6 | 953.6 | 4.668 | 0.782 [0.778, 0.786] | indicative (C++ lane faster) | in-order | 27.4 | 275.2 |
| gfortran-natural | 11 | 498.0 | 556.1 | 98.59 | 0.456 [0.414, 0.507] | indicative — the serial `matmul` runtime now beats Blade | — | 36.8 | 601.6 |
| gfortran-expert | 11 | 76.76 | 77.84 | 0.835 | 0.064 [0.063, 0.064] | indicative | — | 51.6 | 81.68 |
| ifx-natural | 11 | 568.2 | 569.0 | 1.298 | 0.467 [0.465, 0.468] | indicative | — | 55.9 | 576.2 |
| ifx-expert | 11 | 70.19 | 70.65 | 0.338 | 0.058 [0.058, 0.058] | indicative | — | 51.2 | 73.15 |

The longest run of the session (1.228 s, a gram blade-llvm warm-up) stayed under the
2.5 s cap.

#### pairkernel — indicative

| arm | n | min (ms) | median (ms) | IQR (ms) | ratio [95% CI] | verdict | fold form | peak WS (MB) | licensed median (ms, §3.4) |
|---|---|---|---|---|---|---|---|---|---|
| blade-llvm | 11 | 40.99 | 42.78 | 2.025 | ref | — | in-order | 28.5 | 42.59 |
| blade-cpp | 11 | 73.11 | 77.59 | 5.033 | 1.813 [1.748, 1.883] | indicative | in-order | 29.3 | 77.25 |
| gfortran-natural | 11 | 60.65 | 62.79 | 2.652 | 1.468 [1.428, 1.514] | indicative | — | 52.9 | 61.92 |
| gfortran-expert | 11 | 34.23 | 35.22 | 1.041 | 0.823 [0.798, 0.846] | indicative | — | 29.0 | 32.77 |
| ifx-natural | 11 | 63.73 | 65.05 | 1.358 | 1.520 [1.485, 1.563] | indicative | — | 52.5 | 64.53 |
| ifx-expert | 11 | 31.64 | 32.00 | 0.677 | 0.748 [0.735, 0.777] | indicative | — | 28.5 | 30.82 |

Unchanged from licensed on the Blade side (the kernel has no fold; the checksum's chain
was already cheap); the experts lose ~2 ms (their checksum `simd reduction` is off), so
the ratios move from 0.77 / 0.72 to 0.82 / 0.75.

#### recur — indicative

| arm | n | min (ms) | median (ms) | IQR (ms) | ratio [95% CI] | verdict | fold form | peak WS (MB) | licensed median (ms, §3.5) |
|---|---|---|---|---|---|---|---|---|---|
| blade-llvm | 21 | 106.7 | 111.0 | 13.11 | ref | — | in-order | 51.5 | 116.9 |
| blade-cpp | 21 | 116.0 | 123.5 | 16.66 | 1.113 [1.047, 1.188] | indicative | in-order | 52.5 | 134.1 |
| gfortran-natural | 21 | 17.41 | 18.06 | 0.696 | 0.163 [0.150, 0.168] | indicative | — | 51.6 | 19.70 |
| gfortran-expert | 21 | 12.48 | 12.95 | 0.892 | 0.117 [0.109, 0.122] | indicative | — | 51.0 | 10.48 |
| ifx-natural | 21 | 14.61 | 14.89 | 0.588 | 0.134 [0.123, 0.138] | indicative | — | 51.1 | 16.00 |
| ifx-expert | 21 | 12.90 | 13.24 | 0.775 | 0.119 [0.112, 0.125] | indicative | — | 50.5 | 11.27 |

The allocator loss (G6) is regime-independent; the experts' 48 MB checksum pass without
its `simd reduction` costs them ~2 ms (10.5 → 13.0 ms), narrowing the ratio from 0.09 to
0.12 for the wrong reason.

#### stencil — indicative; twins present

| arm | n | min (ms) | median (ms) | IQR (ms) | ratio [95% CI] | verdict | fold form | peak WS (MB) | incremental (ms) [CI] | incremental ratio [CI] |
|---|---|---|---|---|---|---|---|---|---|---|
| blade-llvm | 21 | 15.01 | 15.97 | 1.154 | ref | — | in-order | 49.8 | 8.965 [8.526, 9.281] | ref |
| blade-cpp | 21 | 17.18 | 17.77 | 0.944 | 1.113 [1.080, 1.162] | indicative | in-order | 50.8 | 7.754 [7.371, 8.308] | 0.865 [0.817, 0.937] |
| gfortran-natural | 21 | 16.79 | 17.39 | 0.452 | 1.089 [1.062, 1.125] | indicative | — | 50.9 | 7.175 [6.893, 7.433] | 0.800 [0.761, 0.863] |
| gfortran-expert | 21 | 16.28 | 16.80 | 1.031 | 1.052 [1.032, 1.103] | indicative | — | 50.9 | 6.698 [6.527, 7.532] | 0.747 [0.715, 0.828] |
| ifx-natural | 21 | 18.18 | 18.70 | 0.506 | 1.171 [1.139, 1.209] | indicative | — | 50.4 | 11.80 [11.55, 11.97] | 1.317 [1.258, 1.396] |
| ifx-expert | 21 | 13.15 | 13.47 | 0.831 | 0.843 [0.825, 0.881] | indicative | — | 50.4 | 6.606 [6.432, 7.144] | 0.737 [0.705, 0.804] |
| blade-llvm-inputonly | 21 | 6.857 | 7.008 | 0.189 | — | — | in-order | 27.5 | | |
| blade-cpp-inputonly | 21 | 9.906 | 10.02 | 0.219 | — | — | in-order | 28.2 | | |
| gfortran-natural-inputonly | 21 | 9.890 | 10.21 | 0.299 | — | — | — | 28.0 | | |
| gfortran-expert-inputonly | 21 | 9.893 | 10.10 | 0.294 | — | — | — | 28.0 | | |
| ifx-natural-inputonly | 21 | 6.625 | 6.896 | 0.176 | — | — | — | 27.5 | | |
| ifx-expert-inputonly | 21 | 6.453 | 6.865 | 0.381 | — | — | — | 27.5 | | |

Bandwidth-bound as in licensed (totals 0.84–1.17); Blade's incremental rises from 7.3 to
9.0 ms with its checksum fold now a single chain over 3M cells, so the experts' incremental
ratios improve from 0.67–0.79 to 0.74–0.75 against it — the one place the repro regime
touches the control kernels measurably.

### 6.2 serial x repro x native — session 3b (`session-130438/`)

fusedchain, maskagg, onepass, dot, saxpy; 21 rounds each; 3291.8 s; 0 failures; no
abort; no excursion; the maskagg blade-llvm fallback as expected; cooldowns still 2 s /
10 s / 300 s (the old driver). 2 of 5 blocks suppressed (fusedchain, onepass — within-
block trends of 5–9% on individual arms); **maskagg, dot and saxpy are CLEAN** and carry
verdicts. Reference `blade-llvm` (maskagg: `blade-cpp`). Every Blade row's fold form is
`in-order` in BOTH lanes — in repro each lane emits one scalar dependent chain per fold
(no lanes, no `simd reduction`, no `reassoc`), so the lanes differ here only in how they
lower the loop around the chain and the input generator.

**Lanes in repro.** On dot and saxpy the LLVM lane's TOTAL is the faster one (dot 16.98 vs
22.73 ms, saxpy 15.49 vs 18.40 — ratios 1.34 / 1.19, "win" by the convention), because its
input generation is faster (twins 13.7 vs 20.1 ms, 9.1 vs 13.3 ms); INCREMENTALLY the C++
lane's in-order fold is the cheaper one (dot 2.63 vs 3.28 ms, 0.80 [0.65, 0.96]; saxpy
5.11 vs 6.36 ms, 0.80 [0.76, 0.84]; fusedchain 1.54 vs 2.53 ms, 0.61; onepass 3.98 vs
5.37 ms, 0.74) — the same direction as 3a's antisym/gram, where the C++ lane's plain nest
beat the LLVM lane's 64x64-brick per-cell dot once both were scalar chains. So "the C++
lane beats the LLVM lane in repro" is true of the FOLDS (incremental) and false of the
totals on the memory-bound kernels, where generation dominates.

#### fusedchain — indicative (suppressed by drift); twins present

| arm | n | min (ms) | median (ms) | IQR (ms) | ratio [95% CI] | verdict | fold form | peak WS (MB) | incremental (ms) [CI] | incremental ratio [CI] | licensed median (ms, §4.1) |
|---|---|---|---|---|---|---|---|---|---|---|---|
| blade-llvm | 21 | 6.774 | 7.160 | 0.600 | ref | — | in-order | 19.8 | 2.534 [1.960, 2.858] | ref | 4.571 |
| blade-cpp | 21 | 8.159 | 8.374 | 0.427 | 1.170 [1.119, 1.198] | indicative | in-order | 20.6 | 1.542 [1.385, 1.684] | 0.608 [0.516, 0.804] | 6.754 |
| gfortran-natural | 21 | 17.35 | 20.08 | 3.587 | 2.805 [2.597, 3.099] | indicative | — | **66.1** | 13.28 [11.83, 15.03] | 5.241 [4.360, 7.478] | 17.98 |
| gfortran-expert | 21 | 8.242 | 8.775 | 1.155 | 1.226 [1.168, 1.330] | indicative | — | 20.3 | 1.983 [1.624, 2.671] | 0.782 [0.616, 1.142] | 6.905 |
| ifx-natural | 21 | 13.40 | 14.78 | 2.706 | 2.065 [1.920, 2.206] | indicative | — | **65.6** | 10.09 [9.414, 11.00] | 3.982 [3.375, 5.463] | 13.45 |
| ifx-expert | 21 | 4.878 | 5.319 | 0.749 | 0.743 [0.705, 0.803] | indicative | — | 19.8 | 0.653 [0.223, 0.987] | 0.258 [0.091, 0.411] | 4.897 |
| blade-llvm-inputonly | 21 | 4.351 | 4.625 | 0.889 | — | — | in-order | 19.8 | | | 3.946 |
| blade-cpp-inputonly | 21 | 6.575 | 6.833 | 0.355 | — | — | in-order | 20.6 | | | 6.084 |
| gfortran-natural-inputonly | 21 | 6.547 | 6.801 | 0.618 | — | — | — | 20.3 | | | 6.769 |
| gfortran-expert-inputonly | 21 | 6.547 | 6.792 | 0.411 | — | — | — | 20.3 | | | 6.180 |
| ifx-natural-inputonly | 21 | 4.379 | 4.692 | 0.643 | — | — | — | 19.9 | | | 4.540 |
| ifx-expert-inputonly | 21 | 4.379 | 4.666 | 0.616 | — | — | — | 19.8 | | | 3.790 |

Reading (indicative). Blade's fused chain as a scalar in-order fold costs 2.5 ms (LLVM) /
1.5 ms (C++) beyond its twin, 4x / 2.3x its licensed increment (0.6–0.7 ms); the
gfortran expert's unlicensed loop costs 2.0 ms (its licensed 0.7), ifx-expert's 0.65 ms
(unchanged — ifx FUSES the kernel loop with the input-checksum loop, both reading the
stored input, into one pass, so the full arm adds little over its twin; each reduction
stays a single in-order chain — Fortran agent, `fortran-notes.md` §8c — which is why
ifx-expert is now 1.35x FASTER than blade-llvm, 0.743). The win over the staged baselines
shrinks from 3.9x / 2.9x to 2.8x / 2.1x: the baselines' cost is temporaries, not fold
association, so they lose nothing in repro while Blade loses its lanes.

#### maskagg — CLEAN block (verdicts stand); reference blade-cpp; blade-llvm fallback

| arm | n | min (ms) | median (ms) | IQR (ms) | ratio vs blade-cpp [95% CI] | verdict | fold form | peak WS (MB) | incremental (ms) [CI] | incremental ratio [CI] | licensed median (ms, §4.3) |
|---|---|---|---|---|---|---|---|---|---|---|---|
| blade-llvm | 0 | — | — | — | — | fallback | in-order | — | | | fallback |
| blade-cpp | 21 | 64.56 | 68.86 | 3.996 | ref | — | in-order | 46.5 | 65.56 [65.02, 68.31] | ref | 67.16 |
| gfortran-natural | 21 | 8.172 | 8.288 | 0.185 | 0.120 [0.116, 0.122] | **loss** | — | 17.4 | 4.967 [4.881, 5.039] | 0.076 [0.072, 0.077] | 8.301 |
| gfortran-expert | 21 | 4.749 | 4.982 | 0.306 | 0.072 [0.067, 0.074] | **loss** | — | 16.5 | 1.623 [1.402, 1.741] | 0.025 [0.021, 0.027] | 3.993 |
| ifx-natural | 21 | 10.32 | 10.52 | 0.282 | 0.153 [0.147, 0.156] | **loss** | — | 23.6 | 8.327 [8.264, 8.505] | 0.127 [0.122, 0.130] | 10.56 |
| ifx-expert | 21 | 3.645 | 3.752 | 0.122 | 0.054 [0.052, 0.055] | **loss** | — | 16.0 | 1.576 [1.515, 1.621] | 0.024 [0.023, 0.025] | 2.919 |
| blade-llvm-inputonly | 21 | 2.146 | 2.177 | 0.050 | — | — | in-order | 12.2 | | | 1.717 |
| blade-cpp-inputonly | 21 | 3.235 | 3.304 | 0.079 | — | — | in-order | 12.9 | | | 2.822 |
| gfortran-natural-inputonly | 21 | 3.292 | 3.321 | 0.072 | — | — | — | 12.6 | | | 3.341 |
| gfortran-expert-inputonly | 21 | 3.281 | 3.359 | 0.128 | — | — | — | 12.7 | | | 2.906 |
| ifx-natural-inputonly | 21 | 2.150 | 2.189 | 0.063 | — | — | — | 12.2 | | | 2.181 |
| ifx-expert-inputonly | 21 | 2.149 | 2.175 | 0.033 | — | — | — | 12.2 | | | 1.726 |

Diagnosis. The first clean maskagg block confirms the session-1b (indicative) picture with
verdicts: Blade loses to every Fortran arm — 8.3x to `pack`, 6.5x to ifx's `pack`, 14x /
19x to the experts (0.120 / 0.153 / 0.072 / 0.054). The regime changes nothing on Blade's
side (68.9 vs 67.2 ms: the hashed `unordered_map` compaction, **G9**, has no fold to
reorder) and costs the experts ~0.5–0.8 ms (their checksum `simd reduction` off: 4.98 vs
3.99, 3.75 vs 2.92 ms), which is why the expert ratios are slightly less bad than in
licensed (0.059 / 0.043). The incrementals say the same: 65.6 ms beyond the twin for Blade
against 1.6 ms for the experts and 5.0 / 8.3 ms for `pack`.

#### onepass — indicative (suppressed by drift); twins present

| arm | n | min (ms) | median (ms) | IQR (ms) | ratio [95% CI] | verdict | fold form | peak WS (MB) | incremental (ms) [CI] | incremental ratio [CI] | licensed median (ms, §4.2) |
|---|---|---|---|---|---|---|---|---|---|---|---|
| blade-llvm | 21 | 16.47 | 17.16 | 0.901 | ref | — | in-order | 42.7 | 5.372 [4.542, 5.893] | ref | 11.67 |
| blade-cpp | 21 | 20.79 | 21.09 | 1.064 | 1.229 [1.210, 1.276] | indicative | in-order | 43.5 | 3.975 [3.689, 4.738] | 0.740 [0.655, 0.941] | 16.77 |
| gfortran-natural | 21 | 32.27 | 32.82 | 0.987 | 1.913 [1.883, 1.965] | indicative | — | 43.2 | 15.70 [15.12, 16.32] | 2.923 [2.666, 3.464] | 32.87 |
| gfortran-expert | 21 | 21.06 | 21.67 | 0.806 | 1.263 [1.243, 1.292] | indicative | — | 43.2 | 4.403 [3.714, 4.887] | 0.820 [0.676, 0.978] | 16.83 |
| ifx-natural | 21 | 12.43 | 13.50 | 1.516 | 0.787 [0.754, 0.834] | indicative | — | 42.7 | 1.251 [0.799, 2.063] | 0.233 [0.141, 0.402] | 12.96 |
| ifx-expert | 21 | 12.49 | 13.21 | 0.700 | 0.770 [0.751, 0.793] | indicative | — | 42.7 | 1.138 [0.618, 1.534] | 0.212 [0.117, 0.299] | 11.78 |
| blade-llvm-inputonly | 21 | 11.16 | 11.79 | 1.456 | — | — | in-order | 42.7 | | | 9.495 |
| blade-cpp-inputonly | 21 | 16.55 | 17.11 | 1.127 | — | — | in-order | 43.5 | | | 14.83 |
| gfortran-natural-inputonly | 21 | 16.73 | 17.12 | 0.925 | — | — | — | 43.2 | | | 17.15 |
| gfortran-expert-inputonly | 21 | 16.62 | 17.26 | 1.334 | — | — | — | 43.2 | | | 15.24 |
| ifx-natural-inputonly | 21 | 11.17 | 12.25 | 0.955 | — | — | — | 42.7 | | | 11.98 |
| ifx-expert-inputonly | 21 | 11.32 | 12.07 | 0.894 | — | — | — | 42.7 | | | 12.07 |

Reading (indicative). Blade's four accumulators as scalar in-order chains cost 5.4 ms
(LLVM) / 4.0 ms (C++) beyond the twin against 2.2 / 1.9 ms licensed; gfortran's four `sum`
intrinsics (15.7 ms) and its expert's single loop (4.4 ms, from 1.6 licensed) move the way
the association rule predicts, while ifx's `sum`s and expert loop (1.1–1.3 ms) are
unchanged — ifx fuses the kernel and input-checksum passes into one (each reduction still
an in-order chain; Fortran agent §8c), so both ifx arms now beat blade-llvm on the total
(0.79 / 0.77, would-be detectable losses). The gfortran side keeps
its win (1.9x over the four-pass baseline).

#### dot — CLEAN block (verdicts stand); twins present

| arm | n | min (ms) | median (ms) | IQR (ms) | ratio [95% CI] | verdict | fold form | peak WS (MB) | incremental (ms) [CI] | incremental ratio [CI] | licensed median (ms, §4.4) |
|---|---|---|---|---|---|---|---|---|---|---|---|
| blade-llvm | 21 | 16.69 | 16.98 | 0.541 | ref | — | in-order | 50.3 | 3.278 [3.021, 3.651] | ref | 13.20 |
| blade-cpp | 21 | 22.08 | 22.73 | 1.866 | 1.338 [1.305, 1.356] | win (C++ lane total 1.34x slower; its fold increment 0.80x) | in-order | 51.1 | 2.632 [2.219, 2.907] | 0.803 [0.652, 0.960] | 19.28 |
| gfortran-natural | 21 | 19.79 | 20.14 | 0.564 | 1.186 [1.160, 1.202] | **win** | — | 50.8 | 2.379 [2.206, 2.671] | 0.726 [0.628, 0.857] | 20.28 |
| gfortran-expert | 21 | 19.71 | 20.07 | 1.013 | 1.182 [1.156, 1.194] | **win** | — | 50.8 | 2.348 [2.169, 2.606] | 0.716 [0.622, 0.888] | 18.44 |
| ifx-natural | 21 | 13.68 | 14.15 | 0.681 | 0.833 [0.813, 0.853] | **loss** | — | 50.4 | 2.372 [1.900, 2.786] | 0.723 [0.577, 0.867] | 14.04 |
| ifx-expert | 21 | 11.69 | 12.22 | 0.734 | 0.720 [0.696, 0.744] | **loss** | — | 50.4 | 0.607 [0.361, 1.043] | 0.185 [0.105, 0.328] | 12.67 |
| blade-llvm-inputonly | 21 | 13.39 | 13.70 | 0.487 | — | — | in-order | 50.3 | | | 11.47 |
| blade-cpp-inputonly | 21 | 19.67 | 20.10 | 0.513 | — | — | in-order | 51.1 | | | 17.71 |
| gfortran-natural-inputonly | 21 | 17.29 | 17.76 | 0.353 | — | — | — | 50.8 | | | 18.25 |
| gfortran-expert-inputonly | 21 | 17.44 | 17.72 | 0.448 | — | — | — | 50.8 | | | 17.32 |
| ifx-natural-inputonly | 21 | 11.31 | 11.78 | 1.156 | — | — | — | 50.4 | | | 11.78 |
| ifx-expert-inputonly | 21 | 11.24 | 11.61 | 0.540 | — | — | — | 50.4 | | | 11.10 |

Diagnosis. The first clean dot block, and the verdicts split by compiler: Blade (LLVM
reference) is faster than both gfortran arms (1.19 / 1.18, win) and slower than both ifx
arms (0.83 / 0.72, loss). The totals are generation-dominated (twins 11.6–20.1 ms of
12–23 ms totals), so the split is mostly the generators — LLVM lane and ifx at 11–14 ms,
the C++ lane and gfortran at 17.7–20.1 ms (D9) — but the incrementals carry a real
finding: ifx-expert's dot costs 0.6 ms beyond its twin where every other arm's in-order
chain costs 2.3–3.3 ms (incremental ratio 0.185 [0.105, 0.328]). The Fortran agent
verified from the archived repro executables (`fortran-notes.md` §8c) that ifx does NOT
reassociate under the repro flags — every reduction is a single in-order dependent chain
(`vfmadd231sd` chains unrolled by 8; no packed add/FMA reduction anywhere;
`BENCH_LICENSED` undefined in the build commands): the small increment comes from ifx
FUSING the kernel loop with the input-checksum loop (both read the stored inputs) into one
pass, so the full arm adds little over its input-only twin, while the input fill stays a
separate, stored, 32-bit-SIMD loop; gfortran keeps separate passes. That is compiler
loop fusion, permitted under README §1.5 / F7 and recorded per arm; each reduction keeps
its order and no flag changes. Blade's in-order
chains (3.3 ms LLVM, 2.6 ms C++) are 4x the licensed 0.8 ms-class folds on the same
kernel only because the licensed chain was already memory-bound.

#### saxpy — CLEAN block (verdicts stand); twins present

| arm | n | min (ms) | median (ms) | IQR (ms) | ratio [95% CI] | verdict | fold form | peak WS (MB) | incremental (ms) [CI] | incremental ratio [CI] | licensed median (ms, §4.5) |
|---|---|---|---|---|---|---|---|---|---|---|---|
| blade-llvm | 21 | 15.15 | 15.49 | 0.315 | ref | — | in-order | 49.8 | 6.356 [6.183, 6.631] | ref | 14.28 |
| blade-cpp | 21 | 18.15 | 18.40 | 0.420 | 1.188 [1.171, 1.204] | win (total; fold increment 0.80x) | in-order | 50.7 | 5.113 [4.958, 5.343] | 0.804 [0.755, 0.837] | 17.89 |
| gfortran-natural | 21 | 16.35 | 16.72 | 0.360 | 1.079 [1.062, 1.093] | **detectable-win** | — | 50.8 | 4.875 [4.736, 5.098] | 0.767 [0.726, 0.810] | 17.47 |
| gfortran-expert | 21 | 15.24 | 15.36 | 0.287 | 0.992 [0.977, 1.003] | **equivalent** | — | 50.8 | 3.516 [3.454, 3.669] | 0.553 [0.527, 0.587] | 15.62 |
| ifx-natural | 21 | 12.72 | 13.03 | 0.257 | 0.841 [0.828, 0.849] | **loss** | — | 50.4 | 5.294 [5.188, 5.483] | 0.833 [0.798, 0.870] | 14.13 |
| ifx-expert | 21 | 9.455 | 9.802 | 0.234 | 0.633 [0.621, 0.639] | **loss** | — | 50.4 | 2.038 [1.822, 2.194] | 0.321 [0.283, 0.345] | 11.93 |
| blade-llvm-inputonly | 21 | 8.785 | 9.131 | 0.356 | — | — | in-order | 35.1 | | | 8.194 |
| blade-cpp-inputonly | 21 | 13.10 | 13.28 | 0.248 | — | — | in-order | 35.8 | | | 12.11 |
| gfortran-natural-inputonly | 21 | 11.50 | 11.84 | 0.236 | — | — | — | 35.6 | | | 12.36 |
| gfortran-expert-inputonly | 21 | 11.58 | 11.85 | 0.071 | — | — | — | 35.5 | | | 12.00 |
| ifx-natural-inputonly | 21 | 7.569 | 7.732 | 0.202 | — | — | — | 35.1 | | | 8.277 |
| ifx-expert-inputonly | 21 | 7.423 | 7.764 | 0.342 | — | — | — | 35.1 | | | 7.636 |

Diagnosis. Clean: Blade is a detectable win over gfortran-natural (1.08), equivalent to
gfortran-expert (0.992 [0.977, 1.003] — the suite's tightest equivalence), and loses to
both ifx arms (0.84 / 0.63). As on dot, the totals follow the generators; the
incrementals say ifx-expert's store-plus-accumulate loop costs 2.0 ms beyond its twin
against Blade's 6.4 / 5.1 ms (0.32) and gfortran-expert's 3.5 ms (0.55): the experts' fused
store-plus-accumulate loop, and — for ifx — the kernel pass fused with the input-checksum
pass as well (each reduction in order; §8c). Blade's two passes
(store under `|> compute`, then an in-order checksum chain over 16 MB) are the whole of
its 6.4 ms.

### 6.3 mt8 x repro x native — session 3c (`session-140038/`)

Ten kernels (maskagg serial by protocol); 7 rounds for antisym/comoment3/gram, 21 for
the rest; 1474.1 s — the first session under the D46 cooldowns (0.5 s / 3 s / 60 s); 0
failures; no abort; no excursion. 6 of 10 blocks suppressed by within-block trends of
individual arms (antisym, comoment3, gram, pairkernel, stencil, dot — 5–23%, mostly the
dense naturals); fusedchain, onepass, recur and saxpy clean. Reference `blade-cpp`
(`blade-llvm` serial-only; onepass and recur Blade "no parallel form"). **The regime's
definition shows in the parallelism column:** every Blade row reads `threaded maps,
serial folds (BLADE_FP_REPRO)` — `BLADE_FP_REPRO=1` serializes the fold teams, so Blade
threads its input maps and the kernel's map level but every fold (the inner dots, the
checksum folds, the input sum) is ONE serial chain on one thread — while the Fortran arms
keep thread-level `reduction(+:)` (per-thread partials combined, a reordering the regime
permits them; README §3). The repro-mt8 Blade cells are therefore a definition artefact
as much as a measurement, and the ratios below are read with that in mind.

| kernel | arm | n | median (ms) | IQR (ms) | ratio vs blade-cpp [95% CI] | verdict | twin median (ms) | incremental (ms) / ratio | licensed-mt8 median (ms, §5) |
|---|---|---|---|---|---|---|---|---|---|
| antisym | blade-cpp | 7 | 60.70 | 1.098 | ref | — | — | — | 22.58 |
| antisym | gfortran-natural | 7 | 106.7 | 5.357 | 1.758 [1.656, 1.809] | indicative | — | — | 116.9 |
| antisym | gfortran-expert | 7 | 9.358 | 0.287 | 0.154 [0.149, 0.158] | indicative | — | — | 10.38 |
| antisym | ifx-natural | 7 | 59.18 | 17.48 | 0.975 [0.799, 1.148] | indicative (22% trend) | — | — | 71.39 |
| antisym | ifx-expert | 7 | 9.841 | 0.366 | 0.162 [0.158, 0.167] | indicative | — | — | 10.90 |
| comoment3 | blade-cpp | 7 | 92.68 | 1.545 | ref | — | — | — | 20.62 |
| comoment3 | gfortran-natural | 7 | 93.18 | 9.250 | 1.005 [0.961, 1.060] | indicative | — | — | 91.94 |
| comoment3 | gfortran-expert | 7 | 5.096 | 0.155 | 0.055 [0.053, 0.056] | indicative | — | — | 4.998 |
| comoment3 | ifx-natural | 7 | 116.2 | 7.122 | 1.254 [1.173, 1.298] | indicative | — | — | 111.5 |
| comoment3 | ifx-expert | 7 | 4.021 | 0.299 | 0.043 [0.043, 0.048] | indicative | — | — | 4.025 |
| gram | blade-cpp | 7 | 138.3 | 5.351 | ref | — | — | — | 43.75 |
| gram | gfortran-natural | 7 | 698.6 | 153.1 | 5.050 [4.132, 5.385] | indicative (serial `matmul`) | — | — | 614.1 |
| gram | gfortran-expert | 7 | 20.19 | 0.995 | 0.146 [0.139, 0.151] | indicative | — | — | 21.19 |
| gram | ifx-natural | 7 | 605.5 | 12.05 | 4.377 [4.090, 4.464] | indicative (serial `matmul`) | — | — | 631.1 |
| gram | ifx-expert | 7 | 18.57 | 1.041 | 0.134 [0.127, 0.147] | indicative | — | — | 20.02 |
| pairkernel | blade-cpp | 21 | 56.19 | 2.464 | ref | — | — | — | 16.11 |
| pairkernel | gfortran-natural | 21 | 14.24 | 2.365 | 0.253 [0.242, 0.268] | indicative | — | — | 15.28 |
| pairkernel | gfortran-expert | 21 | 6.202 | 0.437 | 0.110 [0.109, 0.115] | indicative | — | — | 6.262 |
| pairkernel | ifx-natural | 21 | 16.63 | 2.301 | 0.296 [0.276, 0.313] | indicative | — | — | 16.43 |
| pairkernel | ifx-expert | 21 | 6.213 | 0.349 | 0.111 [0.108, 0.115] | indicative | — | — | 6.209 |
| fusedchain | blade-cpp | 21 | 5.559 | 0.255 | ref | — | 4.017 | 1.542 [1.444, 1.722] / ref | 4.128 |
| fusedchain | gfortran-natural | 21 | 11.46 | 0.593 | 2.062 [1.997, 2.105] | **win** | 3.535 | 7.930 / 5.143 [4.594, 5.491] | 12.47 |
| fusedchain | gfortran-expert | 21 | 4.181 | 0.233 | 0.752 [0.728, 0.776] | **loss** | 3.526 | 0.655 / 0.425 [0.303, 0.518] | 3.253 |
| fusedchain | ifx-natural | 21 | 11.49 | 0.301 | 2.067 [2.009, 2.095] | **win** | 4.042 | 7.450 / 4.832 [4.349, 5.201] | 12.63 |
| fusedchain | ifx-expert | 21 | 4.642 | 0.294 | 0.835 [0.809, 0.860] | **loss** | 3.999 | 0.643 / 0.417 [0.337, 0.520] | 3.509 |
| onepass | blade-cpp | 0 | — | — | no parallel form | — | — | — | — |
| onepass | gfortran-natural / expert | 21 | 14.94 / 10.94 | 0.577 / 0.371 | — | — | 8.508 / 8.280 | — | 15.34 / 9.075 |
| onepass | ifx-natural / expert | 21 | 15.35 / 11.41 | 0.325 / 0.243 | — | — | 8.839 / 9.136 | — | 15.76 / 9.403 |
| recur | blade-cpp | 0 | — | — | no parallel form | — | — | — | — |
| recur | gfortran-natural / expert | 21 | 71.40 / 6.822 | 2.326 / 0.599 | — | — | — | — | 73.24 / 7.207 |
| recur | ifx-natural / expert | 21 | 14.79 / 7.337 | 0.625 / 0.235 | — | — | — | — | 16.36 / 7.911 |
| stencil | blade-cpp | 21 | 12.08 | 1.001 | ref | — | 5.600 | 6.483 [6.212, 6.821] / ref | 11.49 |
| stencil | gfortran-natural | 21 | 8.628 | 0.417 | 0.714 [0.695, 0.739] | indicative | 4.390 | 4.238 / 0.654 [0.612, 0.701] | 9.477 |
| stencil | gfortran-expert | 21 | 8.000 | 0.235 | 0.662 [0.639, 0.675] | indicative | 4.357 | 3.642 / 0.562 [0.519, 0.588] | 8.968 |
| stencil | ifx-natural | 21 | 10.01 | 0.289 | 0.828 [0.807, 0.839] | indicative | 4.790 | 5.217 / 0.805 [0.753, 0.848] | 10.19 |
| stencil | ifx-expert | 21 | 8.333 | 0.351 | 0.690 [0.665, 0.707] | indicative | 4.777 | 3.556 / 0.549 [0.506, 0.586] | 9.120 |
| dot | blade-cpp | 21 | 13.54 | 1.746 | ref | — | 11.13 | 2.408 [1.974, 3.924] / ref | 12.00 |
| dot | gfortran-natural | 21 | 11.19 | 0.421 | 0.827 [0.737, 0.847] | indicative | 8.710 | 2.484 / 1.032 [0.596, 1.265] | 11.41 |
| dot | gfortran-expert | 21 | 11.07 | 0.293 | 0.818 [0.739, 0.833] | indicative | 8.419 | 2.648 / 1.099 [0.673, 1.345] | 10.53 |
| dot | ifx-natural | 21 | 11.52 | 0.217 | 0.851 [0.773, 0.871] | indicative | 8.924 | 2.594 / 1.077 [0.659, 1.357] | 11.83 |
| dot | ifx-expert | 21 | 11.48 | 0.357 | 0.848 [0.768, 0.868] | indicative | 8.936 | 2.540 / 1.055 [0.654, 1.289] | 10.96 |
| saxpy | blade-cpp | 21 | 12.25 | 0.632 | ref | — | 7.496 | 4.754 [4.309, 5.221] / ref | 11.40 |
| saxpy | gfortran-natural | 21 | 10.05 | 0.598 | 0.821 [0.782, 0.839] | **loss** | 5.641 | 4.411 / 0.928 [0.817, 1.055] | 10.36 |
| saxpy | gfortran-expert | 21 | 8.936 | 0.329 | 0.729 [0.702, 0.743] | **loss** | 5.650 | 3.286 / 0.691 [0.568, 0.800] | 8.963 |
| saxpy | ifx-natural | 21 | 10.14 | 0.334 | 0.827 [0.786, 0.838] | **loss** | 6.225 | 3.911 / 0.823 [0.721, 0.924] | 10.59 |
| saxpy | ifx-expert | 21 | 9.411 | 0.309 | 0.768 [0.736, 0.782] | **loss** | 6.162 | 3.249 / 0.683 [0.606, 0.781] | 9.224 |

(Minima and the twins' own rows are in the bundle; peak working sets as in §5 — the
fusedchain naturals again 66.5 / 68.2 MB, over the 64 MB rule.)

Diagnosis. **Group A** (all indicative): with its folds serialized Blade loses 6–7x to the
experts on antisym and gram (0.154 / 0.162, 0.146 / 0.134), 18–23x on comoment3 (0.055 /
0.043), 9x on pairkernel (0.110 / 0.111) — against 2.1–5.1x in licensed-mt8 (§5). The
Blade totals (60.7 / 92.7 / 138 / 56.2 ms) are the licensed-mt8 figures (22.6 / 20.6 /
43.8 / 16.1) with the fold teams removed: in this regime every inner `prodsum` and every
checksum fold runs as ONE serial chain on ONE thread, so the kernel's map level threads
across cells but each cell's dot is latency-bound (G1's single chain) and the whole
checksum pass is serial again — the same two costs as the serial-repro regime (§6.1),
now against Fortran arms that keep both their vectorized outer-product kernels and their
thread-level `reduction(+:)` on the checksum. The dense baselines sit where they did in
licensed-mt8 (gram's serial `matmul` at 600–700 ms; antisym/comoment3 baselines threaded
across all phases), so Blade's margins over them shrink to 1.0–1.8x (antisym/comoment3)
or vanish (pairkernel 0.25 / 0.30). **Group B and controls:** fusedchain (clean) keeps
its 2.06 / 2.07 win over the staged baselines (their temporaries do not care about fold
association) and loses to the experts (0.752 / 0.835 — Blade's fold is one serial chain on
one thread, 1.54 ms beyond its twin, the experts' threaded reductions 0.65 ms); saxpy
(clean) loses to all four (0.73–0.83; incrementals 0.68–0.93: the serial checksum chain
over 16 MB against a threaded `reduction`); stencil and dot (indicative) read the same
way (0.66–0.85), dot's incrementals all containing 1. onepass and recur have no Blade
cell; their Fortran arms are within a few percent of their licensed-mt8 values (the
repro flags cost gfortran-expert's onepass 9.1 → 10.9 ms; ifx's `sum`s are unchanged, per
§8c fusion). The controls were the calmest of any session (CV 1.7% / 4.7%; §8).

## 7. fastmath — session 5; blas (gram) — session 4

### 7.1 serial x fastmath x native — session 5a (`session-142614/`)

**Label (README §3): "vendor fast-math vs Blade's reassociation licence — unequal
permission sets, accuracy-checked at 1e-8."** gfortran `-O3 -march=native -ffast-math`,
ifx `/O3 /QxHost /fp:fast`; Blade's arms are its DEFAULT (licensed) build, since Blade has
no blanket fast-math — so every Blade row here is the same emission as in §3/§4 and its
medians repeat them within noise. The checksum tolerance for this regime is **1e-8**
(bundle: `tolerance 1e-08`; 0 disqualifications — every fast-math arm agreed with the
oracle to better than that). antisym, comoment3, gram, fusedchain, onepass, dot; rounds
11 / 11 / 11 / 21 / 21 / 21; 844.9 s under the D46 cooldowns; 0 failures; no abort; no
excursion; no fallback. 3 of 6 blocks suppressed (comoment3, gram, onepass — 5.8–9.2%
trends on ifx arms); antisym, fusedchain, dot clean. Reference `blade-llvm`. Fusedchain
naturals 66.1 / 65.6 MB (over the 64 MB rule again).

| kernel | arm | n | median (ms) | IQR (ms) | ratio [95% CI] | verdict | licensed median (ms) | what fast-math bought this arm |
|---|---|---|---|---|---|---|---|---|
| antisym | blade-llvm | 11 | 73.76 | 0.559 | ref | — | 73.95 | — (same build) |
| antisym | blade-cpp | 11 | 108.7 | 1.707 | 1.474 [1.463, 1.491] | win (lane) | 107.9 | — |
| antisym | gfortran-natural | 11 | 129.9 | 3.497 | 1.761 [1.749, 1.815] | **win** | 770.9 | **5.9x**: the dense per-cell inner products vectorized across the sum |
| antisym | gfortran-expert | 11 | 30.41 | 0.858 | 0.412 [0.408, 0.422] | **loss** | 30.74 | ~1% (nothing left to reorder) |
| antisym | ifx-natural | 11 | 84.27 | 2.625 | 1.142 [1.113, 1.158] | **win** (detectable, 1.14) | 332.9 | **4.0x** |
| antisym | ifx-expert | 11 | 29.25 | 0.339 | 0.397 [0.395, 0.403] | **loss** | 29.54 | ~1% |
| comoment3 | blade-llvm | 11 | 41.04 | 0.598 | ref | — | 41.49 | — |
| comoment3 | blade-cpp | 11 | 107.5 | 0.385 | 2.620 [2.607, 2.652] | indicative | 109.7 | — |
| comoment3 | gfortran-natural | 11 | 144.5 | 0.354 | 3.521 [3.505, 3.563] | indicative | 603.4 | **4.2x** |
| comoment3 | gfortran-expert | 11 | 11.31 | 0.229 | 0.276 [0.272, 0.279] | indicative | 11.49 | ~2% |
| comoment3 | ifx-natural | 11 | 79.97 | 7.481 | 1.949 [1.934, 2.138] | indicative | 723.7 | **9.1x** |
| comoment3 | ifx-expert | 11 | 11.26 | 0.270 | 0.274 [0.272, 0.280] | indicative | 11.70 | ~4% |
| gram | blade-llvm | 11 | 150.6 | 0.891 | ref | — | 156.7 | — |
| gram | blade-cpp | 11 | 246.9 | 2.782 | 1.640 [1.623, 1.648] | indicative | 275.2 | — (within the session-to-session spread) |
| gram | gfortran-natural | 11 | 466.8 | 29.00 | 3.099 [3.021, 3.276] | indicative | 601.6 | 1.3x (libgfortran's `matmul` is a runtime call; `-ffast-math` reaches only the checksum) |
| gram | gfortran-expert | 11 | 75.85 | 0.376 | 0.504 [0.499, 0.507] | indicative | 81.68 | ~8% |
| gram | ifx-natural | 11 | 198.0 | 17.43 | 1.315 [1.296, 1.419] | indicative | 576.2 | **2.9x** (the inlined `matmul` vectorized under `/fp:fast`) |
| gram | ifx-expert | 11 | 69.66 | 0.808 | 0.463 [0.458, 0.466] | indicative | 73.15 | ~5% |
| fusedchain | blade-llvm | 21 | 4.519 | 0.358 | ref | — | 4.571 | — (incremental 0.84 ms) |
| fusedchain | blade-cpp | 21 | 6.576 | 0.244 | 1.455 [1.402, 1.526] | win (lane) | 6.754 | — |
| fusedchain | gfortran-natural | 21 | 15.29 | 0.340 | 3.383 [3.259, 3.521] | **win** | 17.98 | 1.2x (temporaries unchanged; incremental 9.3 vs 11.2 ms) |
| fusedchain | gfortran-expert | 21 | 6.700 | 0.145 | 1.483 [1.428, 1.560] | **win** (its generation twin 6.0 ms dominates; incremental 0.69 ms, 0.82 [0.67, 1.17] ≈ equal) | 6.905 | ~3% |
| fusedchain | ifx-natural | 21 | 9.583 | 0.415 | 2.121 [2.043, 2.214] | **win** | 13.45 | 1.4x (twin 2.86 vs 4.54 ms: the generator itself got faster under `/fp:fast`) |
| fusedchain | ifx-expert | 21 | 3.672 | 0.096 | 0.813 [0.786, 0.845] | **loss** | 4.897 | 1.3x — but its incremental (0.82 ms) equals Blade's (0.84): the gain is the GENERATOR (twin 2.85 vs 3.79 ms), not the chain; the division is now allowed a reciprocal |
| onepass | blade-llvm | 21 | 11.76 | 0.742 | ref | — | 11.67 | — |
| onepass | blade-cpp | 21 | 17.06 | 0.843 | 1.450 [1.408, 1.496] | indicative | 16.77 | — |
| onepass | gfortran-natural | 21 | 20.58 | 0.811 | 1.750 [1.686, 1.798] | indicative | 32.87 | **1.6x** (the four `sum`s vectorized: incremental 5.7 vs 15.7 ms) |
| onepass | gfortran-expert | 21 | 16.99 | 0.904 | 1.445 [1.392, 1.493] | indicative | 16.83 | ~0 (already one loop with four accumulators; the total is its twin) |
| onepass | ifx-natural | 21 | 8.199 | 0.497 | 0.697 [0.677, 0.731] | indicative | 12.96 | 1.6x (twin 7.4 vs 12.0 ms — the generator) |
| onepass | ifx-expert | 21 | 8.435 | 0.648 | 0.717 [0.696, 0.746] | indicative | 11.78 | 1.4x (generator; incremental 0.72 ms vs Blade's 2.0) |
| dot | blade-llvm | 21 | 13.15 | 0.395 | ref | — | 13.20 | — (incremental 1.82 ms) |
| dot | blade-cpp | 21 | 19.37 | 0.672 | 1.472 [1.441, 1.512] | win (lane) | 19.28 | — |
| dot | gfortran-natural | 21 | 18.13 | 0.564 | 1.379 [1.364, 1.428] | **win** | 20.28 | 1.1x (incremental 1.25 vs 2.03 ms: `dot_product` vectorized) |
| dot | gfortran-expert | 21 | 18.16 | 0.532 | 1.381 [1.368, 1.432] | **win** | 18.44 | ~2% (incremental 1.35 vs 1.13 — unchanged) |
| dot | ifx-natural | 21 | 12.63 | 0.515 | 0.960 [0.936, 0.985] | **detectable-loss** | 14.04 | 1.1x (twin 8.9 vs 11.8 ms — the generator; its dot increment is 3.7 ms, larger than Blade's) |
| dot | ifx-expert | 21 | 8.914 | 0.366 | 0.678 [0.667, 0.700] | **loss** | 12.67 | 1.4x — and its incremental is **0.03 ms [−0.32, 0.34]**: beyond its twin the dot costs nothing measurable (kernel fused with the input-sum pass, §8c, now with both reductions reassociated) |

Twins (ms): fusedchain blade-llvm 3.68, blade-cpp 5.91, gfortran 6.00 / 6.01, ifx 2.86 /
2.85; onepass blade-llvm 9.76, blade-cpp 14.90, gfortran 14.90 / 15.06, ifx 7.39 / 7.71;
dot blade-llvm 11.33, blade-cpp 17.70, gfortran 16.88 / 16.81, ifx 8.90 / 8.89.

Reading. What vendor fast-math bought the Fortran arms beyond the matched simd
reductions splits cleanly by tier. The **experts gained almost nothing on the kernels**
(antisym, comoment3 ~1–4%; gram 5–8%; dot's gfortran-expert increment unchanged):
their loops were already vectorized in the licensed regime by the i-ordered outer product
or the matched `simd reduction`, so the extra permissions (reciprocals, finite-math
assumptions, blanket reassociation) had nothing left to reorder — Blade's expert losses
are the same as in licensed (antisym 0.41 / 0.40, comoment3 0.28, gram 0.50 / 0.46). The
**dense baselines gained 4–9x** where their per-cell inner products had been serial
chains (antisym 5.9x / 4.0x, comoment3 4.2x / 9.1x, ifx's inlined `matmul` 2.9x) and
1.2–1.6x on the streaming kernels: the licensed regime denied the naturals any `simd
reduction` by the matched rule (they carry no directives), and fast-math is the blanket
licence that gives it to them — so Blade's wins over the naturals shrink from 10x / 4.5x
to 1.8x / 1.1x on antisym and from 14.5x / 17.4x to 3.5x / 1.9x on comoment3, still wins
(the cell-count advantage survives), while gram's gfortran `matmul` stays a serial
runtime call. The **ifx arms' generator got faster** under `/fp:fast` (fusedchain and
onepass twins 2.9 / 7.4 ms against 4.5 / 12.0 ms licensed), which is most of ifx's
improvement on the group-B totals and is a property of the input loop, not the kernels.
The one new mechanism is ifx-expert's dot: with its kernel fused into the input-checksum
pass (§8c) and both reductions now free to reassociate, the dot's cost beyond the twin
vanishes (0.03 ms, interval across zero). None of this changes the headline regime; it is
reported under the unequal-permissions label.

### 7.2 mt8 x fastmath x native — session 5b (`session-144656/`)

Same label and 1e-8 tolerance as §7.1. All six kernels; rounds 21 / 21 / 7 (gram) / 21 /
21 / 21; 943.4 s under the D46 cooldowns; 0 failures; no abort; no excursion; onepass
blade-cpp `no-parallel-form`. **6 of 6 blocks suppressed** by within-block trends
(5–18% on individual arms, the Blade arm itself on gram 14.1% and fusedchain 18.0%) — every
verdict here is indicative. Reference `blade-cpp` (matched parallelism, D44: Blade threads
inputs, kernel level 0, checksum, input sum; Fortran threads all three phases with
`reduction(+:)`). An earlier attempt, `session-144122/`, ABORTED after 182 s on a control
excursion (memory control 0.030 s, 5.5x its running median) caused by the user working on
the machine at 14:44 (Task Manager open); it is recorded in §2 and excluded from every
figure. Fusedchain naturals 66.5 / 68.2 MB (over the 64 MB rule).

| kernel | arm | n | median (ms) | IQR (ms) | ratio vs blade-cpp [95% CI] | verdict | licensed-mt8 median (ms, §5) | twin median (ms) | incremental (ms) / ratio |
|---|---|---|---|---|---|---|---|---|---|
| antisym | blade-cpp | 21 | 21.03 | 2.408 | ref | — | 22.58 | — | — |
| antisym | gfortran-natural | 21 | 37.17 | 3.841 | 1.767 [1.660, 1.811] | indicative | 116.9 | — | — |
| antisym | gfortran-expert | 21 | 9.740 | 0.434 | 0.463 [0.433, 0.476] | indicative | 10.38 | — | — |
| antisym | ifx-natural | 21 | 20.24 | 3.641 | 0.962 [0.874, 1.051] | indicative | 71.39 | — | — |
| antisym | ifx-expert | 21 | 10.06 | 1.021 | 0.478 [0.443, 0.493] | indicative | 10.90 | — | — |
| comoment3 | blade-cpp | 21 | 20.67 | 1.849 | ref | — | 20.62 | — | — |
| comoment3 | gfortran-natural | 21 | 38.50 | 4.806 | 1.862 [1.746, 1.915] | indicative | 91.94 | — | — |
| comoment3 | gfortran-expert | 21 | 5.136 | 0.530 | 0.248 [0.236, 0.261] | indicative | 4.998 | — | — |
| comoment3 | ifx-natural | 21 | 20.72 | 6.106 | 1.002 [0.849, 1.133] | indicative | 111.5 | — | — |
| comoment3 | ifx-expert | 21 | 4.092 | 0.195 | 0.198 [0.186, 0.201] | indicative | 4.025 | — | — |
| gram | blade-cpp | 7 | 56.76 | 9.768 | ref | — | 43.75 | — | — |
| gram | gfortran-natural | 7 | 706.8 | 39.78 | 12.454 [10.354, 13.638] | indicative (serial `matmul`) | 614.1 | — | — |
| gram | gfortran-expert | 7 | 20.97 | 1.172 | 0.370 [0.325, 0.410] | indicative | 21.19 | — | — |
| gram | ifx-natural | 7 | 244.5 | 21.62 | 4.308 [3.640, 5.193] | indicative | 631.1 | — | — |
| gram | ifx-expert | 7 | 19.57 | 0.774 | 0.345 [0.293, 0.380] | indicative | 20.02 | — | — |
| fusedchain | blade-cpp | 21 | 4.616 | 1.359 | ref | — | 4.128 | 3.881 | 0.735 [−0.365, 0.984] / ref |
| fusedchain | gfortran-natural | 21 | 11.66 | 1.672 | 2.526 [2.392, 3.284] | indicative | 12.47 | 3.117 | 8.541 / undefined (reference interval includes 0) |
| fusedchain | gfortran-expert | 21 | 3.534 | 0.926 | 0.766 [0.699, 1.055] | indicative | 3.253 | 2.883 | 0.651 / undefined |
| fusedchain | ifx-natural | 21 | 12.02 | 1.787 | 2.605 [2.436, 3.391] | indicative | 12.63 | 3.451 | 8.572 / undefined |
| fusedchain | ifx-expert | 21 | 3.536 | 0.768 | 0.766 [0.721, 0.962] | indicative | 3.509 | 3.242 | 0.294 [−0.120, 0.648] / undefined |
| onepass | blade-cpp | 0 | — | — | no parallel form | — | — | — | — |
| onepass | gfortran-natural / expert | 21 | 13.22 / 8.892 | 0.593 / 0.617 | — | — | 15.34 / 9.075 | 6.571 / 6.768 | — |
| onepass | ifx-natural / expert | 21 | 13.50 / 9.376 | 0.575 / 1.022 | — | — | 15.76 / 9.403 | 7.307 / 7.000 | — |
| dot | blade-cpp | 21 | 11.19 | 1.658 | ref | — | 12.00 | 9.253 | 1.937 [0.985, 3.106] / ref |
| dot | gfortran-natural | 21 | 10.36 | 0.634 | 0.925 [0.855, 0.951] | indicative | 11.41 | 8.341 | 2.014 / 1.040 [0.571, 1.828] |
| dot | gfortran-expert | 21 | 10.22 | 0.835 | 0.913 [0.867, 0.956] | indicative | 10.53 | 8.166 | 2.053 / 1.060 [0.692, 1.744] |
| dot | ifx-natural | 21 | 10.53 | 0.700 | 0.941 [0.886, 0.982] | indicative | 11.83 | 8.184 | 2.348 / 1.212 [0.763, 2.068] |
| dot | ifx-expert | 21 | 10.65 | 0.620 | 0.952 [0.903, 0.989] | indicative | 10.96 | 8.162 | 2.489 / 1.285 [0.615, 2.561] |

Reading (all indicative). Under threads the picture of §7.1 repeats: the **experts are
where they were in licensed-mt8** (antisym 9.7 / 10.1 ms, comoment3 5.1 / 4.1, gram 21.0
/ 19.6 — fast-math gave their already-vectorized, already-threaded loops nothing), so
Blade's expert ratios match §5's (antisym 0.46 / 0.48, comoment3 0.25 / 0.20, gram 0.37 /
0.35 on a 7-round block whose Blade arm trended 14%); the **dense baselines collapse
toward Blade** once their serial inner products are allowed to vectorize (antisym 117 →
37 ms gfortran, 71 → 20 ifx; comoment3 92 → 38.5, 112 → 20.7; ifx's inlined `matmul`
631 → 245), so Blade's would-be wins over them shrink to 1.8x (gfortran) and 1.0x (ifx)
on antisym and comoment3 — the cell-count advantage (2x, 5.9x) is now offset by Blade's
own non-vectorized per-cell structure (G1, G1b) and its Path B checksum (G25). gfortran's
`matmul` stays a serial runtime call (707 ms). On the streaming kernels the fast-math
arms' totals are within 0.77–0.95 of Blade with wide, zero-straddling incrementals (the
fusedchain reference's own incremental interval includes 0 at 3–5 ms totals under 8
threads, so the harness reports the incremental ratios as `undefined` — by the M16 rule,
not by omission). Nothing here changes a headline; it is all under the unequal-permissions
label.

### 7.3 blas (gram) — session 4 (`session-150343/`): both blocks CLEAN

Arms: `blade-cpp` with Blade's auto-routed `dsyrk` (`BLADE_BLAS=1`; the plan line reads
`[blas-routing v1] __v27: applied [Syrk (L3, d); HostBlas: blade_linalg::blade_gram_same_d;
tier TierOpenBlasDir]`), `gfortran-expert` and `ifx-expert` calling `dsyrk` explicitly
against the same OpenBLAS; `blade-llvm` recorded `no-blas` ("the exe imports no
libopenblas.dll"); naturals excluded (no library route). 123.6 s; 0 flags, 0 failures, no
excursion; serial 11 rounds with `OPENBLAS_NUM_THREADS=1`, mt8 21 rounds with
`OPENBLAS_NUM_THREADS=8` (library threads on both sides; the Blade arm additionally shows
`openmp (3 constructs)` for its generator, checksum and input sum). Reference `blade-cpp`.

| regime | arm | n | min (ms) | median (ms) | IQR (ms) | ratio vs blade-cpp [95% CI] | verdict | parallelism | peak WS (MB) |
|---|---|---|---|---|---|---|---|---|---|
| serial | blade-cpp (dsyrk) | 11 | 79.82 | 80.71 | 0.895 | ref | — | library threads (1) | 49.0 |
| serial | gfortran-expert (dsyrk) | 11 | 64.97 | 65.71 | 0.664 | 0.814 [0.805, 0.827] | **loss** | library threads (1) | 34.8 |
| serial | ifx-expert (dsyrk) | 11 | 62.14 | 62.83 | 0.655 | 0.778 [0.772, 0.786] | **loss** | library threads (1) | 34.6 |
| mt8 | blade-cpp (dsyrk) | 21 | 23.82 | 28.14 | 2.078 | ref | — | library threads (8) + openmp (3 constructs) | 51.1 |
| mt8 | gfortran-expert (dsyrk) | 21 | 14.98 | 20.26 | 1.776 | 0.720 [0.684, 0.743] | **loss** | library threads (8) + openmp runtime | 36.6 |
| mt8 | ifx-expert (dsyrk) | 21 | 16.81 | 19.50 | 1.385 | 0.693 [0.665, 0.728] | **loss** | library threads (8) + openmp runtime | 38.1 |

Diagnosis (clean verdicts). The same `dsyrk` on both sides, and Blade is 19–22% slower
serial, 28–31% slower threaded. The gap is not in the library call; it is what Blade's
route does around it, read off the archived plan and emit
(`build/gram/full/serial-licensed-blas/blade-cpp/generated/gram.cpp`, `src/cpp/blade_linalg.hpp`
`blade_gram_same_d`): (1) **a dense staging buffer and a repack** — "syrk wants a dense
square C with a leading dimension, so a full m x m buffer is written and then repacked into
Blade's left-justified triangle": `std::vector<double> Cfull(m*m)` (1601² x 8 B = 20.5 MB,
zero-initialized), `cblas_dsyrk(RowMajor, Upper, NoTrans, …)` into it, then a loop copying
the upper triangle into the packed pool — an extra 20.5 MB write, a 10 MB read and a
10 MB write that the expert does not do (it calls `dsyrk` straight into its own dense
array, which is also its checksum's source); the peak working set shows it, 49.0 MB
against 34.8 / 34.6; (2) **the logical-read checksum** — Blade's per-row nest reads all p²
logical cells through `canon_fold<2>` (`std::sort` out of line, **G3b**) where the expert
sums its stored triangle with weight 2; in the licensed serial regime this pass was
measured at a few ms on gram (A §13); (3) the serial input sum and generation in the
serial regime (shared with the experts, so not a differentiator). Items (1) and (2) are
~15 ms serial against the experts' ~63–66 ms `dsyrk`-dominated totals, which is the
observed 15 ms gap; under 8 library threads the `dsyrk` shrinks to ~15 ms but (1) and (2)
do not shrink with it (the repack is serial, the checksum nest threaded only at its outer
level with a Path B fold, **G25**), so the ratio worsens to 0.72 / 0.69. Register entry:
**G28** (BLAS route stages and repacks; the gate-off packed tile of the phase-2 plan writes
the triangle directly and would remove it).

## 8. Controls record

| session | compute control (recur gfortran-expert, serial x repro, full) | stream control (saxpy gfortran-expert, serial x repro, n = 700,001) | source |
|---|---|---|---|
| pilot | CV 8.5%, +24% excursion in one block | — | coordinator's pilot note; bundle `session-231651/` |
| 1a | n = 86 valid rounds, median 13.42 ms, baseline (first block) 12.75 ms; CV 9.3% over the 87 ok samples; min 12.51, max 17.61 ms; 0 invalid | n = 86, median 5.505 ms, baseline 5.246 ms; CV 9.9% over 87 ok samples; min 5.12, max 7.45 ms; 0 invalid | `summary.md` Controls; CVs computed from `session.json` `controls[].samples[].time_s` |
| 1b | n = 105, median 13.05 ms, baseline 13.12 ms; CV 9.8% over 106 ok samples; min 12.43, max 20.67 ms; 0 invalid | n = 105, median 5.404 ms, baseline 5.398 ms; CV 9.2% over 106 ok samples; min 5.13, max 8.28 ms; 0 invalid | same |
| 2 (mt8, partial) | n = 168, median 13.43 ms, baseline 12.83 ms; CV 9.5% over 169 ok samples; min 12.45, max 18.05 ms; 0 invalid | n = 168, median 5.474 ms, baseline 5.311 ms; CV 9.0% over 169 ok samples; min 5.09, max 7.87 ms; 0 invalid | same; both controls normal in the aborted saxpy round |
| 1a′ (antisym re-measure, 229 s) | n = 11, median 12.84 ms = baseline; over the 12 ok samples in `session.json` (warm-up included) CV 9.4%, min 12.66, max 17.37 ms — one 17.4 ms sample, the rest within 12.7–13.3 | n = 11, median 5.453 ms = baseline; CV 3.5% over 12 ok samples, min 5.31, max 5.96 ms | `summary.md` Controls; CVs computed from `session.json` |
| 3a (serial repro) | n = 86, median 12.98 ms, baseline 16.50 ms (the first block, antisym, ran high — 31% within-block trend; every later block 12–23% below); CV 12.1% over 87 ok samples; min 12.57, max 20.27 ms | n = 86, median 5.46 ms, baseline 6.347 ms; CV 9.5% over 87 ok samples; min 5.15, max 7.42 ms | same |
| 3b (serial repro, group B) | n = 105, median 12.95 ms, baseline 13.42 ms; CV 9.4% over 106 ok samples; min 12.44, max 20.83 ms | n = 105, median 5.438 ms, baseline 5.379 ms; CV 7.7% over 106 ok samples; min 5.13, max 7.27 ms | same; no rolling-rule trips, the two suppressions are arm trends |
| **3c (mt8 repro; first session at 0.5 s / 3 s / 60 s)** | n = 168, median 12.73 ms, baseline 12.70 ms; **CV 1.7%** over 169 ok samples; min 12.51, max 13.63 ms | n = 168, median 5.276 ms, baseline 5.268 ms; **CV 4.7%** over 169 ok samples; min 5.05, max 7.41 ms | same |
| 5a (serial fastmath) | n = 96, median 12.81 ms, baseline 12.71 ms; CV 8.0% over 97 ok samples driven by ONE 20.2 ms sample (min 12.55, max 20.19 ms) | n = 96, median 5.295 ms, baseline 5.271 ms; CV 3.6% over 97 ok samples; min 5.13, max 6.07 ms | same |
| 5b (mt8 fastmath) | n = 112, median 13.01 ms, baseline 13.09 ms; CV 4.8% over 113 ok samples; min 12.46, max 16.93 ms | n = 112, median 5.397 ms, baseline 5.263 ms; CV 7.0% over 113 ok samples; min 5.08, max 7.02 ms | same; the aborted attempt `session-144122/` tripped on the memory control (0.030 s, 5.5x) while the user worked on the machine |
| 4 (gram blas) | n = 32, median 12.91 ms, baseline 13.06 ms; CV 5.9%; range 12.51–16.97 ms (runner record) | n = 32, median 5.372 ms, baseline 5.331 ms; CV 5.1%; range 5.16–6.15 ms | `summary.md`; `results/full/RUNNER-RECORD.md` |
| 2b (mt8 re-run) | n = 105, median 12.98 ms, baseline 14.18 ms (the first block ran hot; later blocks 7–10% BELOW it, tripping the rolling rule downward); CV 9.4% over 106 ok samples; min 12.56, max 18.17 ms | n = 105, median 5.424 ms, baseline 6.067 ms; CV 10.0% over 106 ok samples; min 5.12, max 9.02 ms | same |

The two controls diagnose different things: the compute control tracks core clock and
thermal state, the stream control memory-subsystem contention (README §6). In session 1a
both moved 11–16% above the session baseline during the comoment3, gram and pairkernel
blocks (the rolling-control rule, threshold 7%) and showed 7–19% within-block trends
elsewhere (threshold 5%); antisym's block was the only one inside both thresholds. In
session 1b the controls' medians sat on their baselines (13.05 vs 13.12 ms; 5.40 vs 5.40
ms) and no block tripped the rolling rule; the four suppressions were within-block
trends of individual arms (5.6–14.8%) and, in the dot and saxpy blocks, of a control
(8.3% compute, 5.2% memory). onepass was the clean block. In session 2 (mt8) the compute
control moved 11–18% above baseline during comoment3, pairkernel, recur and stencil and
showed 21–23% within-block trends in gram and comoment3 (threaded blocks heat the part
more than serial ones); antisym was again the clean block.

**Observation on inter-run spacing (sessions 3b vs 3c, stated as an observation, not a
re-analysis of earlier sessions).** Under the old cooldowns (2 s between runs, 10 s
between kernels, 300 s between sessions) the compute control's CV was 8.7–12.1% in every
session (3b: 12.95 ms median, CV 9.4% over 106 samples, max 20.8 ms) and the memory
control's 7.7–9.9%. In the first session under the D46 spacing (0.5 s / 3 s / 60 s;
session 3c, 168 control rounds) the compute control's CV fell to **1.7%** (12.73 ms
median, min 12.51, max 13.63 ms) and the memory control's to **4.7%**. The reading
consistent with the numbers: on this uncapped chip the long pauses let the part cool and
re-boost between runs, so consecutive runs sampled different clock states, whereas the
short spacing keeps it in one steady thermal state. The methodological lesson for this
host is that SHORTER spacing gives the steadier measurement; it is recorded here with
both sessions' figures and does not change any earlier session's suppression status or
numbers.

## 9. Hypotheses vs outcomes (filled as sessions land)

| kernel | hypothesis (vs dense baseline / vs expert, licensed serial) | outcome | mismatch | diagnosis / gap |
|---|---|---|---|---|
| gram | loss or equivalent / equivalence to modest loss | 1a indicative: 3.8 & 3.7 (Blade faster) / 0.52 & 0.47 | both sides: baseline slower than assumed, expert loss ~2x | G1, G26; matmul routes recorded |
| comoment3 | WIN 4–6x / equivalence to modest loss | 1a indicative: 14.5 & 17.4 / 0.28 & 0.28 | both sides | G1b, G3a, G3b |
| antisym | WIN ~2x / equivalence to modest loss | 1a′ (clean, re-measured): 10.4 & 4.50 win / 0.416 & 0.400 loss (1a: 10.4 & 4.46 / 0.416 & 0.395) | both sides | G1, G26, G3b |
| pairkernel | WIN ~2x / equivalent | 1a indicative: 1.45 & 1.52 / 0.77 & 0.72 | both sides, same cause | G3a (checksum pass), G3b |
| fusedchain | WIN 1.5–2.5x / equivalent | 1b indicative: 3.93 & 2.94 / 1.51 & 1.07 (incremental vs experts: intervals contain 1) | baseline side: larger win (unfused temporaries, 65.9 / 65.5 MB); expert side holds | fusion by deferral confirmed; C++ lane 1.48x is its twin |
| onepass | WIN 2–3x / equivalent | 1b CLEAN: 2.82 win & 1.11 win / 1.44 win & 1.009 equivalent | ifx side: ifx fused the four `sum`s (1.11, incremental 0.45); gfortran-expert total win is the twin | the join is one pass; G7 partly fixed |
| stencil | equivalent / equivalent | 1a indicative: 1.31 & 1.40 / 1.07 & 0.86 | no (within the band's spirit; indicative) | incremental: experts' fused checksum |
| recur | equivalent / LOSS 1.5–3x | 1a indicative: 0.17 & 0.14 / 0.090 & 0.096 | magnitude (~11x) and the baseline side | G6 (allocator), G8 |
| maskagg | LOSS / LOSS 2–5x | 1b indicative: 0.124 & 0.157 / 0.059 & 0.043 (blade-cpp ref; LLVM fallback) | magnitude (8x / 17–23x) | G9 (hashed compound index), G12/G22 |
| dot | equivalent / equivalent | 1b indicative: totals 1.54 & 1.06 / 1.40 & 0.96; every incremental interval contains 1 | no (totals split by generator speed) | D9; C++ lane 1.46x is its twin |
| saxpy | equivalent / equivalent | 1b indicative: 1.22 & 0.99 / 1.09 & 0.84 | no (experts' fused checksum: incremental 0.60 / 0.71) | — |

## 10. Gap-register cross-references per loss (session 1a)

| loss | mechanism | gap | status |
|---|---|---|---|
| antisym vs both experts (0.416 / 0.395, verdict loss) | per-cell operand reloads, 2 loads/FMA vs 0.67; 7.7 / 5.0 vs 16.7–18.6 G FMA/s | G1 | G1 fixed in C++ post-matrix (F, M7; scratch 120.8 → 36.9 ms, not harness-measured); LLVM twin deferred |
| gram vs experts (0.52 / 0.47, indicative) | same; C++ lane adds the single-accumulator simd reduction | G1, G26 | G1 fixed in both lanes post-matrix (E M5/follow-on, G M6; scratch cpp 275 → 93, llvm 154 → 68.5 ms, not harness-measured); G26 moot here |
| comoment3 vs experts (0.28 / 0.28, indicative) | multiply + FMA per term with 3 loads vs hoisted pair product; checksum share 36% / 70% | G1b, G3a, G3b | G3b FIXED post-matrix (b3272712; scratch: C++ 147 → ~44 ms, not harness-measured); G1b, G3a open |
| pairkernel vs experts (0.77 / 0.72, indicative) | 6.27M-read logical checksum; kernel equivalent | G3a, G2 | G3a fixed post-matrix for the licensed build (H, M4 orbit fold; scratch llvm 42 → 29, cpp 73 → 30 ms, not harness-measured); G2 partly |
| recur vs experts and baselines (0.09–0.17, indicative) | per-step allocator page faults (eight 160 KB temporaries) | G6, G8 | G6 FIXED post-matrix (b3272712; scratch: 136 → 24 ms, not harness-measured); G8 open |
| C++ lane vs LLVM lane on every group-A kernel (1.15–2.64) | single-accumulator simd reduction; out-of-line canonicalizing read | G26, G3b | open |
| maskagg vs both tiers (0.043–0.157, indicative; session 1b) | one `unordered_map` insert per kept cell (~85% of the time), Bool mask pass, `y*y` temporary; LLVM lane refuses `mask`/`compound` | G9, G12 | open |
| mt8 group A after the D44 fix (session 2b, indicative): antisym 0.46 / 0.48, comoment3 0.24 / 0.20, gram 0.48 / 0.46, pairkernel 0.39 / 0.39 vs experts | the serial-regime kernel gap carried into threads (G1 per-cell reloads; G1b for comoment3) plus the Path B checksum fold — 8 serial chains of canonicalizing reads against the experts' vectorized threaded sums (G3a/G3b under G25). The superseded Amdahl term (serial preparation and checksum: 0.064/0.048, 0.11, 0.35/0.33, 0.33/0.35) is gone | G1, G1b, G3a, G3b, G25 | open |
| repro serial, group A (session 3a, indicative): antisym 0.064 / 0.062, gram 0.064 / 0.058, comoment3 0.075 / 0.075 vs experts | Blade's reproducible association is ONE dependent chain per cell (latency-bound: 3.5–8x slower than its own licensed build) while the expert's register-tiled outer-product order is bitwise-reproducible AND vectorized over i; the old Blade default paid this on every build | G1 (a strict-legal tile closes it with no reordering licence), G26; policy D40/D43 | open (tile); default already flipped |
| repro serial, group B (session 3b): dot 0.83 / 0.72 and saxpy 0.84 / 0.63 vs ifx (clean losses); maskagg 0.054–0.153 vs all (clean) | Blade's in-order folds are scalar chains in both lanes (2.6–6.4 ms beyond the twins), as are ifx's (verified: single in-order chains, no packed reduction); ifx's small increments (0.6 / 2.0 / 1.1 ms) come from FUSING the kernel pass with the input-checksum pass, both reading the stored inputs — compiler loop fusion, permitted (README §1.5 / F7); gfortran keeps separate passes; maskagg is regime-independent (G9) | compiler loop-fusion property (ifx), G9; policy D40/D43 | recorded (`fortran-notes.md` §8c) |
| repro mt8, group A (session 3c, indicative): antisym 0.154 / 0.162, gram 0.146 / 0.134, comoment3 0.055 / 0.043, pairkernel 0.110 / 0.111 vs experts; group B clean losses fusedchain 0.752 / 0.835, saxpy 0.73–0.83 | the regime's definition: `BLADE_FP_REPRO=1` serializes every fold team, so each cell's dot and every checksum fold is one serial chain on one thread, while Fortran keeps its thread-level `reduction(+:)` and vectorized outer-product kernels | regime asymmetry (README §3, D43) on top of G1 / G3a; the repro default is the opt-in, not the headline | recorded |
| fastmath serial (session 5a; unequal permissions, 1e-8): antisym 0.41 / 0.40 and fusedchain 0.81 / dot 0.68 vs ifx-expert (clean losses), comoment3 0.28, gram 0.50 / 0.46 (indicative) | the experts gained ~nothing from fast-math on the kernels (already vectorized by ordering or matched simd); Blade's losses are the licensed ones; ifx's generator and fused dot+checksum pass got faster | G1, G1b, G3a (unchanged); ifx loop fusion (§8c) | recorded under the label |
| mt8 onepass and recur: experts 9.1 / 9.4 ms and 7.2 / 7.9 ms vs Blade's serial-only figures | no thread-level form for the join (G7) / no licence slot in a `let rec` step (G5) | G7, G5 | open |
| C++ lane vs LLVM lane on group B / controls (1.25–1.48, indicative) | mostly INPUT GENERATION (the twins differ by the same factor: 6.1 vs 3.9, 14.8 vs 9.5, 17.7 vs 11.5, 12.1 vs 8.2 ms); the folds' incrementals are equivalent | generator lowering in the C++ lane (not yet a register entry; the kernel folds are G26-free after the flip) | to be entered after the mt8 session |

Fixed before the sessions and reflected in the sources: G19, G20, G21 (5f4b33ea); partly:
G7, G14.

## 11. Headline, inventory, honesty notes, phase-2 worklist (final, 2026-10-18)

### 11.1 Headline

Eleven kernels, six arms, four regimes (licensed / repro, serial / mt8), plus fastmath and
blas, twelve session bundles (two aborted and excluded), 0 checksum disqualifications
against the independent oracle. Ratio > 1 = Blade faster. Verdict words follow README §6
(δ = 10%); "indicative" = the block's verdict was suppressed by a drift check.

**Blade vs the dense natural tier (licensed serial, the headline regime):** on the four
symmetric kernels Blade from one idiomatic source is **4–17x faster** than the literal
dense transcription — antisym 10.4x / 4.5x (clean), comoment3 14.5x / 17.4x, gram 3.8x /
3.7x (the serial `matmul` runtime), pairkernel 1.45x / 1.5x (all three indicative) — and
on the fusion kernels 2.8–3.9x (fusedchain, indicative) and 2.8x / 1.1x (onepass, clean:
ifx fused its four `sum`s). The dense baselines are what array syntax with dense storage
costs; the matched-permission rule denies them `simd reduction`, and under vendor
fast-math (§7) they recover 4–9x and the margins shrink to 1.0–1.9x on antisym/comoment3.

**Blade vs the reviewed expert tier (licensed serial):** **2–4x behind on group A** —
antisym 0.416 / 0.400 (clean), gram 0.52 / 0.47, comoment3 0.28 / 0.28, pairkernel 0.77 /
0.72 (indicative) — with the mechanisms measured and registered: the per-cell dot reloads
its operand rows (2 loads per FMA against the expert's 8x6 register tile at 0.67, **G1**;
comoment3's un-hoisted pair product, **G1b**), and the logical-read checksum over the
packed object (**G3a/G3b**; pairkernel's entire loss, its kernel being equivalent). On the
memory-bound kernels Blade is **equivalent to ahead of gfortran** — onepass 1.44 (clean,
driven by its generation twin), fusedchain 1.51, dot 1.40, saxpy 1.09, stencil 1.07 (the
incrementals say equivalent) — and **behind ifx on several** (fusedchain 0.74–0.81, dot
0.72, saxpy 0.63–0.84, stencil 0.86; ifx's generator is faster and it fuses the kernel
with the input-checksum pass, §8c). Two controls lose outright as predicted: recur 0.09
(the per-step allocator, **G6**: ~91k page faults, not arithmetic) and maskagg 0.04–0.15
(the hashed compound index, **G9**).

**repro (the reproducible-association opt-in, serial):** Blade is **13–17x behind the
experts on group A** (antisym 0.064 / 0.062, gram 0.064 / 0.058, comoment3 0.075 / 0.075)
because its reproducible association is one dependent chain per cell while the expert's
register-tiled outer-product order is both bitwise-reproducible and vectorized — the
measured cost of the old default and the strongest evidence for G1. Group B: maskagg
0.04–0.15, dot 1.19 / 1.18 vs gfortran and 0.83 / 0.72 vs ifx, saxpy 1.08 / 0.99 / 0.84 /
0.63 (clean).

**mt8 (licensed, after the matched-parallelism fix D44):** gram 0.48 / 0.46, antisym 0.46
/ 0.48, comoment3 0.24 / 0.20, pairkernel 0.39 / 0.39 vs the experts (all indicative) —
the serial-regime kernel gap carried into threads, plus the Path B checksum fold (serial
within each chunk, **G25**); fusedchain 2.06 / 2.07 over the naturals and 0.75 / 0.84 vs
the experts (clean in repro-mt8), saxpy 0.73–0.83 (clean); onepass and recur have no Blade
thread form (**G7**, **G5**). Before the fix the group-A mt8 Blade figures were 1.3–3.5x
worse (comoment3 87 → 20.6 ms) because Blade threaded only the kernel's level 0 while
Fortran threaded everything; both sets are shown (§5, §5A). In repro-mt8 Blade's folds are
serialized by definition and it is 6–23x behind on group A (§6.3).

**blas (gram, clean):** the same `dsyrk` on both sides and Blade is **19–22% slower serial,
28–31% slower threaded** — its route stages a dense 20.5 MB buffer and repacks it into the
packed triangle, then reads p² logical cells for the checksum (**G28**, G3b).

**The lane story.** In licensed regimes the LLVM lane is ahead of the C++ lane on every
kernel (1.15–2.64x: four ymm FMA accumulators against one `omp simd reduction` accumulator,
**G26**; inlined canonicalizing reads against `std::sort`, **G3b**; and, on group B, a
faster input generator). In repro the order flips on the compact kernels (C++ 0.78–0.81 of
the LLVM time on antisym/gram: once both are scalar chains, the LLVM lane's 64x64 cache
bricks cost more than the C++ plain nest) and on the folds of the streaming kernels
(incremental 0.61–0.80), though not on their totals. The LLVM lane has no thread runtime
(mt8 is C++-only), refuses `compound`/`mask` (maskagg is C++-only) and has no BLAS route.

**Hypotheses vs outcomes (§9):** every group-A hypothesis missed in magnitude on both sides
— the dense-baseline wins were larger than predicted (the baselines' nests are slower than
the cell-count ratio alone implies) and the expert losses larger (G1/G1b/G3 were not in the
pre-measurement model); the control predictions held in direction (recur, maskagg lose;
dot/saxpy/stencil equivalent) and missed in magnitude where the allocator and the hashed
index dominate. Nothing was re-thresholded to fit.

### 11.2 Controls record

The consolidated per-session controls table (compute and stream control medians, CVs,
ranges, excursions dropped, for all twelve sessions) is the runner's
`results/full/RUNNER-RECORD.md`, section "Control drift per session"; it is not duplicated
here. §8 above holds the per-session rows used while filling this document and the
spacing observation: under the old cooldowns (2 s / 10 s / 300 s) the compute control's CV
was 8.7–12.1% in every multi-block session; under the D46 spacing (0.5 s / 3 s / 60 s) it
was 1.7% (mt8-repro), 4.8% (mt8-fastmath), 5.9% (blas), 8.1% with one outlier
(serial-fastmath) — read as the uncapped chip cooling and re-boosting during long pauses;
an observation, not a re-analysis of the earlier sessions.

### 11.3 Dropped and recast workloads (README §8 inventory)

`band` (closed-form constrained enumeration) — dropped before measurement: the domain
fold was refused at codegen (BL7004) and the `|> compute` form built a hashed key table
(D1, G4); **G4 was fixed after the matrix (agent C, 1ea0ddf3 / 4b1a5224) and `band` is
restored as a twelfth kernel (`kernels/band/spec.md`; sources landed in a879ef1a,
smoke-verified in both lanes and both FP regimes, full-size plan = the affine nest), still
unmeasured in this document — it joins the re-measure after the wave-2 merges, with Blade
"no parallel form" in mt8 (G31) and the C++ lane's scalar inner loop (G30) expected to
show**. The **pair
SUM** over a `comm` kernel — recast as the `pairkernel` MATRIX: a fold over compact
storage is refused (BL3999; D2, G2), and the suite therefore measures materialized
symmetric objects, not pair sums. The O(1)-per-cell antisymmetric map — recast as the
angular-momentum tensor (a ~5 ms write under the 64 MB cap; D4). The supported claim is
about materialized symmetric objects, fused chains and folds, and the listed controls —
not about pair sums, constrained enumeration, or anything not in the eleven.

### 11.4 Honesty notes

- **Uncapped host, indicative verdicts.** Every session ran at maximum processor state
  100% ("Silent" scheme) by the user's choice; drift suppression stayed in force and no
  threshold was loosened. Suppressed (indicative) cells per regime, from the runner's
  record: serial-licensed 44, mt8-licensed 32, serial-repro 40, mt8-repro 24,
  serial-fastmath 15, mt8-fastmath 20, blas 0. The clean blocks carrying verdicts are:
  antisym (serial-licensed, re-measure), onepass (serial-licensed), maskagg, dot and saxpy
  (serial-repro), fusedchain, onepass, recur and saxpy (mt8-repro), antisym, fusedchain and
  dot (serial-fastmath), and both blas blocks. An 80%-capped re-run (`harness/README.md`)
  is the documented route to unsuppressed verdicts on the rest.
- **n per cell:** 11 rounds for the four compact kernels in serial regimes, 21 for the
  other seven; 7 for the compact kernels in mt8 (gram and comoment3 in session 2 and
  mt8-repro; gram in mt8-fastmath), 21 otherwise — the README minimums, never overridden.
- **Two aborts, both recorded and excluded:** `session-100329` (mt8-licensed) stopped in
  saxpy's round 7 on a single 43 ms stall of a 6 ms input-only twin under the then
  always-abort rule (controls normal) — the block was re-run in `session-113651`, and the
  rule was changed to drop-and-record an isolated excursion (D45); `session-144122`
  (mt8-fastmath) stopped after 182 s on a memory-control excursion (0.030 s, 5.5x) caused
  by the user working on the machine at 14:44 (Task Manager open) — re-run clean as
  `session-144656`.
- **Provenance caveats.** (i) The serial-licensed rows of gram, comoment3 and pairkernel
  (`session-042223`) pre-date the D44 change to the group-A Blade sources; their serial
  emission was proven byte-identical before and after the change (blade-A §14), so the
  rows stand; antisym's serial emission changed in one input-sum fold and was re-measured
  (`session-121713`, within 1–2% of the original). (ii) The mt8-licensed regime is drawn
  from two sessions: group B and the controls from `session-100329`, group A and saxpy
  from the matched-parallelism re-run `session-113651`; the superseded group-A rows are
  kept in §5A. (iii) The cooldowns changed between serial-repro 3b and mt8-repro (2 s /
  10 s / 300 s → 0.5 s / 3 s / 60 s, D46); the sessions table says which was in force,
  and the control CV fell at the change. (iv) The fusedchain natural arms exceeded the
  64 MB working-set rule by 2–6% in every session that ran them (65.5–68.2 MB); kept and
  flagged. (v) The mt8 group-A re-run's threaded checksum folds are Path B (serial within
  each chunk, G25) and only the fold-kernel licence placement threads (G27) — the
  parallelism column records what was threaded.
- **What was not run:** maskagg in any mt8 regime (serial by protocol); onepass and recur
  Blade cells in mt8 (no parallel form); pairkernel, stencil, recur, maskagg and saxpy in
  fastmath (no fold under test or no Blade form); the naturals in blas; the `blade-llvm`
  arm in every mt8 and blas cell and on maskagg. No cell was estimated.
- **Attribution and incrementals** are labelled evidence, not results: the kernel-only
  shares come from blade-A §13's phase measurements; incrementals are total-minus-twin,
  never kernel time; ratios of incrementals are `undefined` where the reference's interval
  includes 0 (M16).
- **Reviewed, not optimal.** Every Fortran arm was reviewed (`notes/review-fortran.md`);
  "expert" means reviewed. The oracle (`validation/pins.json`, numpy) is independent of
  both languages; 0 disqualifications across all sessions.

### 11.5 Phase-2 worklist

The worklist is the planning pair's **`notes/phase2-microkernel-plan.md`** (12 ranked
items, Codex attack rounds in its §7); its order and forecast gains are reproduced below.
**The "forecast" figures are arithmetic forecasts from the measured rates of this suite
(A §13, B §6, sessions 1a/1b), not measurements** — the bench tests them. Measured
payoffs to beat, from this document: gram expert ratio 0.52 / 0.47 (G1, G26); antisym
0.416 / 0.400 (G1); comoment3 0.28 (G1b, G3a/G3b); pairkernel 0.77 / 0.72 (G3a: the kernel
is equivalent); recur 0.09 (G6); maskagg 0.04–0.15 (G9); blas 0.81 / 0.78 (G28); the
mt8 chunk-serial checksum (G25) and the missing thread forms (G5, G7).

**Phase-2 landings so far (post-matrix; NOT harness-measured — every number below is the
implementing agent's scratch A/B and is superseded by the re-measure after the wave-2
merges):** (a) agent E's packed-triangle syrk tile in the C++ lane (6cde38bb / c42addd2,
G1 partly fixed): gram blade-cpp 275 → 93 ms, ratios vs the experts 0.30 → ~1.04 /
0.27 → ~0.96 on a loaded host, 3 rounds; (b) agent C's domain-enum nest (1ea0ddf3 /
4b1a5224, G4 fixed) and the restored `band` kernel (a879ef1a), smoke-verified, with two
new domain gaps (G30: the C++ lane's inner loop is scalar; G31: no thread-level form);
(c) agent D's M0–M3 (merge b3272712): the retained-block pool cache in both allocators
and let-rec zero-fill elision (G6 fixed: recur C++ 136.4 → 23.8 ms, LLVM 131.5 → 22.3 ms,
page faults 107k → 13.6k and 95k → 13.3k, checksum identical, 7 interleaved serial runs)
and the compare-swap canonical read with bind-once (G3b fixed: comoment3 C++ 147 → ~44 ms;
the flat-offset read was built, measured slower, and is not emitted); (d) **wave 2 fully
merged (tip 957c1e0e)**: E's follow-on (be915333) extends the syrk tile to the `gram(A,
A)` node's BLAS-off arm (bitwise the jam under contract=off); F's M7 antisym tile and M8
comoment3 simplex tile with the W panel (5a4b2a86 / 7134398f; G1 largely fixed in C++,
G1b fixed; scratch antisym blade-cpp 120.8 → 36.9 ms vs experts 31.7 / 30.7, comoment3
kernel-only 4.52 → 1.90 ms at p = 91; peak WS 18.3 → 25.7 MB); G's M6 LLVM syrk tile via
a shared C core (b0932c5e / 7159b133; scratch blade-llvm gram 153.8 → 68.5 ms vs gf-expert
77–85 in the same sessions; cross-lane pools byte-identical 15/15); H's M4 orbit-class
fold (2a23e51a / 957c1e0e; G3a fixed for the licensed build, G2 partly: scratch comoment3
llvm 41.05 → 25.03 / cpp 108.6 → 29.09 ms, pairkernel llvm 42.03 → 29.05 / cpp 73.35 →
29.63 ms; declines under repro). The coordinator's single-run full-size checks on the
merged binary before H (serial licensed, ms llvm/cpp): gram 60/61, antisym 76/30, comoment3
41–51/19–23, recur 22/24, pairkernel 44/41; repro antisym llvm 668 (no LLVM antisym
tile) / cpp 32; all pins hold in both lanes and regimes. **Deferred and flagged for the
re-measure:** the LLVM antisym/comoment3 tile twins (so blade-llvm may now trail blade-cpp
on those two), K blocking in the antisym driver, the orbit fold's threading (a `where omp`
outer checksum fold now runs one serial orbit pass — an mt8 regression risk), G26 where
tiles do not apply (dot-class folds), G30/G31 on band. The one full gate run and the
harness re-measure come next; nothing above is a result.

| # | item (plan §) | effort | forecast (ms, from the session-1a/1b medians) | gap |
|---|---|---|---|---|
| 0 | per-helper-family bitwise gate (raw-bit helper tests against a same-TU serial reference, both `-ffp-contract` modes, disassembly gates) before any tile rollout | — | prerequisite | — |
| 1 | 4.5a exact-size retained-block allocator cache, both runtimes | S | recur 117 → ~20–30 (Codex: ~20 optimistic) | G6 |
| 2 | 4.4a inline canonicalizing read + bind-once (C++ lane) | S | comoment3 cpp 110 → ~50; pairkernel cpp 77 → ~42; in EVERY regime incl. repro | G3b |
| 3 | 4.4b orbit-weighted pool fold for the logical checksum | M | comoment3 llvm 41.5 → ~27; pairkernel llvm 42.6 → ~33; cpp ~34 both after #2 | G3a, G2 |
| 4 | 4.1 native packed-triangle syrk tile, C++ lane (reuse of the shipped 6x8 packed GEMM tile at the BLAS-gate-off branch) | M | gram cpp 275 → ~90–100 (~85–90 after #2); ratio vs gfortran-expert ~0.85–0.95 | G1, G26 |
| 5 | 4.5b(i) zero-fill elision for `let rec` (narrow proof) | S | recur −3..5 | G6 |
| 6 | 4.1 LLVM twin of the syrk tile (shared arithmetic core) | M | gram llvm 157 → ~85–90 | G1 |
| 7 | 4.1b antisym paired strict tile | M | antisym 74.6 → ~34 (the clean block: the place to show a causal gain) | G1 |
| 8 | 4.2 KRS rank-3 tile + hoisted W = X(i)X(j) panel | M–L | comoment3 llvm ~27 → ~13–16, cpp similar | G1b |
| 9 | 4.3 vector-accumulator prodsum (C++) | S | dot / onepass cpp | G26 |
| 10 | 4.5b(ii) destination passing for array-returning steps | M | recur −4..8 | G6 |
| 11 | 4.5c pointwise-body fusion of the stage chain | L | recur → ~11–13 | G8 |
| 12 | 4.6 stencil row scheduling; C++ input-generator investigation | L | low value | — |

Plan-level one-page forecast (its §0): gram ~85–95 ms both lanes (vs experts 73–82 ms);
antisym ~33–37 (vs 29–31); comoment3 ~13–16 (vs 11.5–11.7); pairkernel ~32–34 (vs 31–33);
recur ~20–30 → ~15–25 → ~11–13 (vs 10.5–11.3); stencil unchanged. **Caveat carried from the
plan (§3):** a tile is PROVEN bitwise-identical to the serial reference only under
`BLADE_FP_CONTRACT=off`; under the default fast contraction each compiler may fuse
differently, so bitwise agreement must be VERIFIED per compiler and mode (vfmadd counts
in the tile and in the serial reference agree). Not in the plan but measured here and
registered: G28 (the BLAS route's staging/repack) and G25 (chunk-interior serial folds
under mt8).

## 13. Phase-2 re-measure (post-fix) — filled from the 79AF7EDC runner record; antisym/comoment3 serial rows await session 9

Source: `results/full/RUNNER-RECORD.md`, section "Re-measure on Blade.dll 79AF7EDC
(commit 957c1e0e; tree tip 05f95277), 2026-10-18 20:15–21:52" — full intervals, controls
and the sibling-session window log are there and are not duplicated; bundles
`results/2026-10-18-LAPTOP-350H6LDB/session-{201558, 202815, 204141, 205306, 210636,
213323, 214522, 214954}/` (the aborted `211435` and `211831` are kept as records only).
Kernels: gram, antisym, comoment3, pairkernel, recur and the restored band, in every regime
each `kernel.json` declares, full size, README minimum rounds, interleaved randomized
rounds, both drift controls, D46 cooldowns. Label as everywhere: **uncapped (Silent
scheme, 100%); `*` = verdict suppressed by drift, reported as indicative.** Ratio
convention unchanged: ratio = expert median / Blade median, **> 1 Blade faster, < 1 the
expert faster**; reference `blade-llvm` in serial regimes, `blade-cpp` in mt8 and blas.

**Binary provenance.** 79AF7EDC… (tip 957c1e0e) carries the C++ tiles for gram, antisym
and comoment3 (E M5 + follow-on, F M7/M8), the LLVM gram tile (G M6), the orbit-class fold
(H M4), the pool cache and zero-fill elision (D M1/M3), the compare-swap canonical read (D
M2) and the domain-enum nest (C). It does NOT carry the LLVM antisym/simplex twins
(2dcc0739, DLL 30FBBC97…), so the **antisym and comoment3 serial rows in licensed, repro
and fastmath are marked "session 9 pending"** below; their 79AF7EDC values — the
C++-tiles-only state, in which the LLVM reference still runs the old per-cell dot — are
kept in footnote ¹ so the before/after of the twins will be visible when session 9 lands.
Group B and the controls were not re-run (no fix targets them; §3–§7 stand).

### 13.1 Before / after / expert, per regime

"before" = the pre-fix matrix median (§3–§7, same lane as the reference); "after" = the
re-measure median. Milliseconds. Verdict words: live (unsuppressed block) or indicative.

**serial x licensed (ref blade-llvm; session 201558; controls CV 10.4% / 11.3%)**

| kernel | before (ms) | after blade-llvm (ms) | after blade-cpp (ms) | gf-expert / ifx-expert (ms) | ratio gf / ifx [CI] | verdict | vs naturals gf / ifx | gaps closed | remaining |
|---|---|---|---|---|---|---|---|---|---|
| gram | 156.7 (cpp 275.2) | **56.1** | 57.96 | 76.75 / 69.36 | **1.368 [1.355, 1.380] / 1.236 [1.227, 1.243]** | **WIN, live** (n = 11) | 9.9x / 10.2x | G1 (E M5/follow-on, G M6), G26 moot | — |
| antisym | 73.95 (cpp 107.9) | session 9 pending¹ | session 9 pending¹ | 30.28 / 28.99 | session 9 pending¹ | — | — | G1 C++ (F M7), G3b | LLVM twin measured in session 9; K blocking |
| comoment3 | 41.49 (cpp 109.7) | session 9 pending¹ | session 9 pending¹ | 11.26 / 11.35 | session 9 pending¹ | — | — | G1/G1b C++ (F M8), G3a (H M4), G3b | LLVM twin in session 9 |
| pairkernel | 42.59 (cpp 77.25) | **28.94** | 29.14 | 32.44 / 30.18 | **1.121 [1.118, 1.125] / 1.043 [1.036, 1.047]** | **WIN, live** (n = 21; ifx: detectable) | 2.08x / 2.23x | G3a (H M4), G3b | — |
| recur | 116.9 (cpp 134.1) | 20.43 | 22.49 | 10.07 / 9.995 | 0.493 [0.457, 0.523] * / 0.489 [0.466, 0.500] * | loss, indicative (was 0.090) | 0.93x / 0.77x | G6 (D M1/M3: ~5.7x faster) | the remaining 2x = per-step temporaries + the 48 MB trajectory copy (D's deferred 4.5b(ii)/4.5c, G8) |
| band | — (new) | 3.318 | 9.276 | 0.8135 / 1.042 | 0.245 [0.245, 0.246] / 0.314 [0.314, 0.314] | **loss, live** | 62x / 106x | G4 (C) | G30 (C++ scalar inner loop: 9.3 ms), **G33 candidate** (blade-llvm 3.3 vs the experts' 0.8–1.0 ms although its loop vectorizes) |

¹ 79AF7EDC values (C++ tiles only; the LLVM reference without its tiles), serial licensed:
antisym blade-llvm 71.44 / blade-cpp 27.93 ms vs experts 30.28 / 28.99 → ratios vs
blade-llvm 0.424 [0.420, 0.427] / 0.406 [0.400, 0.411] (live; against blade-cpp the
experts are ~1.08 / 1.04); comoment3 blade-llvm 25.04 / blade-cpp 9.54 vs 11.26 / 11.35 →
0.450 * / 0.453 * (vs blade-cpp ~1.18 / 1.19). naturals 10.7x / 4.6x and 24x / 29x.

**mt8 x licensed (ref blade-cpp; session 202815; controls CV 4.5% / 4.4%)**

| kernel | before (ms, §5) | after blade-cpp (ms) | experts (ms) | ratio gf / ifx [CI] | verdict | vs naturals | closed | remaining |
|---|---|---|---|---|---|---|---|---|
| gram | 43.75 | **15.58** | 19.87 / 19.06 | **1.276 [1.221, 1.322] / 1.223 [1.176, 1.258]** | **WIN, live** | 37.6x / 39.4x | G1 | — |
| antisym | 22.58 | 8.79 | 9.50 / 9.82 | 1.081 [1.054, 1.114] * / 1.117 [1.080, 1.150] * | detectable win, indicative | 12.8x / 8.2x | G1 | K blocking |
| comoment3 | 20.62 | **3.311** | 4.839 / 4.03 | **1.462 [1.434, 1.502] / 1.217 [1.184, 1.271]** | **WIN, live** | 28.4x / 35.4x | G1/G1b, G3a | — |
| pairkernel | 16.11 | 7.67 | 6.27 / 6.03 | 0.817 [0.772, 0.840] * / 0.786 [0.744, 0.804] * | loss, indicative (was 0.39 / 0.39 — better, but a regression against its own serial gain) | 1.98x / 2.19x | G3a (serial form) | **G32: the orbit-class checksum fold is ONE serial pass under mt8** where the old logical walk was threaded |
| recur | no Blade arm | — | 6.815 / 7.486 | — | — | — | — | G5 |
| band | no Blade arm | — | 0.9909 / 1.705 | — | — | — | — | G31 |

**serial x repro (ref blade-llvm; session 204141; ALL LIVE, controls CV 1.9% / 4.1%)** —
the tiles need no reorder licence and apply under `BLADE_FP_REPRO=1`; the orbit fold
declines.

| kernel | before (ms, §6.1) | after blade-llvm (ms) | after blade-cpp (ms) | experts (ms) | ratio gf / ifx [CI] | verdict | vs naturals | closed | remaining |
|---|---|---|---|---|---|---|---|---|---|
| gram | 1219 (cpp 953.6) | **59.73** | 61.34 | 77.07 / 70.12 | **1.290 [1.230, 1.321] / 1.174 [1.119, 1.186]** | **WIN, live** — the tile keeps each cell one ascending chain, so the reproducible build is now as fast as the licensed one | 9.9x / 9.5x | G1 | — |
| antisym | 508.2 (cpp 411.3) | session 9 pending² | session 9 pending² | 30.83 / 29.55 | session 9 pending² | — | — | G1 C++ | LLVM twin (session 9) |
| comoment3 | 156.8 (cpp 180.6) | session 9 pending² | session 9 pending² | 11.42 / 11.50 | session 9 pending² | — | — | G1/G1b C++ | LLVM twin; G3a (orbit fold declines in repro) |
| pairkernel | 42.78 (cpp 77.59) | 40.82 | 37.85 | 34.32 / 31.91 | 0.841 [0.836, 0.852] / 0.782 [0.777, 0.790] | loss, live | 1.49x / 1.57x | G3b | G3a in repro (the logical-read checksum pass is back) |
| recur | 111.0 (cpp 123.5) | 23.77 | 23.85 | 12.95 / 13.34 | 0.545 [0.538, 0.554] / 0.561 [0.552, 0.571] | loss, live (was 0.117) | 0.75x / 0.63x | G6 | G8, 4.5b(ii) |
| band | — | 9.277 | 9.289 | 3.111 / 3.718 | 0.335 [0.334, 0.336] / 0.401 [0.399, 0.402] | loss, live | 22x / 38x | G4 | G30; in repro both lanes sit at 9.3 ms (the licensed LLVM 3.3 ms was its lanes) |

² 79AF7EDC values, serial repro (live): antisym blade-llvm 505.9 / blade-cpp 29.78 ms vs
experts 30.83 / 29.55 → 0.061 / 0.058 against the untiled LLVM reference, i.e. **parity
for the C++ lane** (29.8 vs 30.8 / 29.6) where the pre-fix repro build was 13–17x behind;
comoment3 blade-llvm 156.7 / blade-cpp 15.88 vs 11.42 / 11.50 → 0.073 / 0.073 (C++ lane
1.4x behind). naturals 1.52x / 0.65x and 3.8x / 4.6x against the untiled LLVM lane.

**mt8 x repro (ref blade-cpp; session 205306; all suppressed, controls CV 3.5% / 6.1%)** —
Blade = threaded maps, serial folds (D43).

| kernel | before (ms, §6.3) | after blade-cpp (ms) | experts (ms) | ratio gf / ifx [CI] | verdict | vs naturals | note |
|---|---|---|---|---|---|---|---|
| gram | 138.3 | **19.14** | 20.66 / 19.81 | 1.080 [1.048, 1.124] * / 1.035 [1.002, 1.075] * | equivalent-to-detectable win, indicative | 28.9x / 32.2x | tile under repro |
| antisym | 60.70 | **10.88** | 10.10 / 10.11 | 0.929 [0.878, 0.983] * / 0.929 [0.903, 0.989] * | detectable loss, indicative | 10.4x / 6.6x | |
| comoment3 | 92.68 | 9.91 | 4.861 / 4.101 | 0.490 [0.466, 0.521] * / 0.414 [0.391, 0.434] * | loss, indicative | 9.7x / 12.2x | the checksum pass: in repro the orbit fold declines and the THREADED LOGICAL WALK runs with serial folds per thread (D43) — the kernel tile is not the loss |
| pairkernel | 56.19 | 17.34 | 6.217 / 6.472 | 0.359 [0.351, 0.378] * / 0.373 [0.354, 0.380] * | loss, indicative | 0.90x / 0.98x | same: the checksum pass, not the kernel |
| recur / band | no Blade arm | — | 7.407 / 7.795 ; 1.330 / 2.344 | — | — | — | G5 / G31 |

**serial x fastmath (ref blade-llvm; unequal-permissions label, 1e-8; session 210636, band
from the clean re-run 214954)**

| kernel | after blade-llvm (ms) | after blade-cpp (ms) | experts (ms) | ratio gf / ifx [CI] | verdict | vs naturals |
|---|---|---|---|---|---|---|
| gram | 56.63 | 59.56 | 76.70 / 69.96 | 1.354 [1.341, 1.377] * / 1.235 [1.218, 1.261] * | win, indicative | 9.7x / 3.5x (ifx's `matmul` vectorized under /fp:fast) |
| antisym | session 9 pending³ | session 9 pending³ | 32.75 / 31.77 | session 9 pending³ | — | — |
| comoment3 | session 9 pending³ | session 9 pending³ | 11.21 / 11.24 | session 9 pending³ | — | — |
| band | 3.312 | 9.277 | 1.699 / 1.322 | 0.513 [0.469, 0.517] / 0.399 [0.398, 0.401] | loss, live (clean re-run) | 91x / 100x |

³ 79AF7EDC values, serial fastmath: antisym blade-llvm 81.31 / blade-cpp 29.48 vs 32.75 /
31.77 → 0.403 * / 0.391 * (the session was disturbed by a sibling build, controls CV 31%);
comoment3 25.04 / 9.595 vs 11.21 / 11.24 → 0.447 / 0.449 (live).

**mt8 x fastmath (ref blade-cpp; session 213323; all suppressed; first ~3 min overlapped a
sibling A/B, controls CV 2.7% / 4.2%)**

| kernel | after blade-cpp (ms) | experts (ms) | ratio gf / ifx [CI] | verdict |
|---|---|---|---|---|
| gram | 17.01 | 20.96 / 19.83 | 1.232 [1.173, 1.282] * / 1.166 [1.112, 1.202] * | win, indicative |
| antisym | 9.009 | 10.51 / 10.36 | 1.167 [1.109, 1.227] * / 1.150 [1.106, 1.211] * | win, indicative |
| comoment3 | 3.632 | 5.232 / 4.419 | 1.441 [1.368, 1.594] * / 1.217 [1.113, 1.355] * | win, indicative |

**gram blas (ref blade-cpp; session 214522; both blocks live)**

| regime | after blade-cpp (ms) | experts (ms) | ratio gf / ifx [CI] | verdict | note |
|---|---|---|---|---|---|
| serial | 64.53 (was 80.71) | 65.39 / 62.48 | 1.013 [1.005, 1.023] / 0.968 [0.963, 0.983] | detectable win / detectable loss | the canon-network read (G3b) and orbit fold (G3a) removed the checksum tax around the same `dsyrk`; the staging repack (G28) remains |
| mt8 | 24.46 (was 28.14) | 19.27 / 19.98 | 0.788 [0.731, 0.869] / 0.817 [0.776, 0.897] | loss, live | G28: the serial dense staging + repack costs 18–21% under 8 library threads |

### 13.2 The band story (first measured here)

band is the closed-form-enumeration kernel restored after G4; every arm visits exactly the
4,051,931 band cells and pays one division per cell (`phi` kept per cell; the Fortran
agent's table alternative was rejected). Measured, serial licensed (live): blade-llvm
3.32 ms, blade-cpp 9.28 ms, experts 0.81 / 1.04 ms, the predicate-nest baselines 62x /
106x slower than Blade. Two separate findings. **blade-cpp's 9.3 ms is G30**: the C++
lane's inner loop stays scalar despite the `omp simd reduction` pragma (2 `vdivsd` per
cell against the LLVM lane's 4-wide 8 `vdivpd`); in repro both lanes sit at 9.3 ms, which
says the LLVM lane's licensed advantage was its vector lanes and the C++ lane never had
them. **blade-llvm's 3.3 ms against the experts' 0.8–1.0 ms is NOT explained by
vectorization** — the LLVM lane does vectorize — so a further ~4x sits elsewhere in the
domain nest (bounds recomputation, the check-carrying `x(i)`/`x(j)` subscripts, the
per-row division's latency): recorded as **G33 (candidate)** in the register, needs a
look at the emit before it is diagnosed. No Blade mt8 arm (G31; the experts reach 0.99 /
1.7 ms threaded). The hypothesis ("blade-llvm equivalent to the expert") was wrong by
~4x; the G30 half of the prediction held.

### 13.3 Open items the re-measure exposed

- **G32 (new, measured):** the orbit-class checksum fold (H M4) is one serial pass under
  mt8. pairkernel mt8-licensed: Blade 7.67 ms vs experts 6.27 / 6.03 (0.82 / 0.79,
  indicative) — better than the pre-fix 0.39, but the kernel's threaded triangle is now
  followed by a serial checksum pass where the pre-fix logical walk was threaded. gram and
  comoment3 are not materially affected (small checksum share; the simplex tile dominates).
  Direction: thread the orbit pass across its outermost class level.
- **mt8-repro comoment3 / pairkernel losses (0.49 / 0.41 and 0.36 / 0.37, indicative) are
  the checksum pass, not the kernels**: in repro the orbit fold declines and the threaded
  logical walk runs with every fold serialized per thread (D43); antisym and gram, whose
  checksum shares are small, sit at 0.93 and 1.04–1.08.
- **recur's remaining 2x** (0.49 licensed, 0.55 repro; from 0.09 / 0.12): the per-step
  temporaries and the 48 MB per-step copy into the trajectory — D's deferred 4.5b(ii)
  destination passing and 4.5c stage fusion (G8); the allocator half (G6) is gone (20–24
  ms from 117–134).
- **band**: G30 (C++ lane scalar inner loop) and G33 candidate (blade-llvm 3.3 vs 0.8 ms
  with a vectorized loop); G31 (no thread form).
- **gram blas mt8**: G28's staging repack (18–21%).
- **Session 9** will replace the antisym and comoment3 serial rows with the twin binary
  (30FBBC97…, tree 2dcc0739); a final gate run closes the set.

### 13.4 Headline of the re-measure (79AF7EDC; antisym/comoment3 serial pending session 9)

- **gram now beats both reviewed experts in every regime it was measured**: serial
  licensed 1.37 / 1.24 (live), serial repro 1.29 / 1.17 (live — the reproducible build
  caught up with the licensed one because the tile needs no reorder licence), mt8 licensed
  1.28 / 1.22 (live), mt8 repro 1.08 / 1.04 *, fastmath 1.35 / 1.24 * and 1.23 / 1.17 *,
  and blas serial 1.01 / 0.97 (the checksum tax gone; only G28's repack left, visible at mt8
  0.79 / 0.82). From 0.52 / 0.47 before the fixes.
- **comoment3 beats the experts under threads** (mt8 licensed 1.46 / 1.22, live; mt8
  fastmath 1.44 / 1.22 *) from 0.24 / 0.20; its serial rows await the LLVM twin (the C++
  lane already at 9.5 ms vs the experts' 11.3, i.e. ahead; the untiled LLVM reference at
  25 ms is what the 0.45 * measures).
- **antisym under threads is at or above the experts** (mt8 licensed 1.08 / 1.12 *, mt8
  fastmath 1.17 / 1.15 *, mt8 repro 0.93 *) from 0.46 / 0.48; serial rows await the LLVM
  twin (C++ lane 27.9 vs 30.3 / 29.0 licensed, 29.8 vs 30.8 / 29.6 repro: parity-to-ahead;
  the untiled LLVM reference at 71 / 506 ms is what the 0.42 and 0.061 measure).
- **pairkernel wins serial licensed (1.12 / 1.04, live)** from 0.77 / 0.72 — the kernel was
  always equivalent and the orbit fold removed the checksum pass — and regresses under
  threads to 0.82 / 0.79 * (G32) and in repro to 0.84 / 0.78 (the logical pass is back).
- **recur** improved 5–6x (0.09 → 0.49 *, 0.12 → 0.55) and still loses 2x (G8 remnants).
- **band** (new): loses 4x (LLVM) / 3–4x (C++) to the experts and beats the predicate nest
  62–106x; two open items (G30, G33 candidate), no thread form (G31).
- Against the dense naturals every symmetric kernel's margin grew: gram 10x / 10x serial
  and 38x / 39x mt8, comoment3 24x / 29x serial and 28x / 35x mt8, antisym 10.7x / 4.6x
  serial and 12.8x / 8.2x mt8, band 62x / 106x.
- Correctness: all pins held in both lanes and both regimes on the merged binaries; the
  orbit fold moves checksums ~4e-15 relative (inside 1e-12).
- Controls per re-measure session (compute / memory CV): 10.4% / 11.3%, 4.5% / 4.4%, 1.9% /
  4.1%, 3.5% / 6.1%, 31.1% / 21.3% (serial-fastmath, disturbed by a sibling build — band
  re-run clean at 1.9% / 3.2%), 2.7% / 4.2%, 1.8% / 10.4% (`RUNNER-RECORD.md`, "Control drift
  per re-measure session"); two aborted mt8-fastmath attempts (a second excursion during a
  sibling `dotnet build`; a 92 ms memory-control excursion) are kept as records. Everything
  uncapped; indicative where starred.

## 12. Decisions put to the user — BOTH DECIDED (2026-10-18)

**Decisions:** (1) mt8 matched parallelism — option (A) below: every arm threads input
generation and the checksum read-back where the language allows; the four group-A Blade
sources gain `where omp` licences on their input maps and checksum nests (blade-A
applying); the §5.1–5.4 rows are superseded and re-run (D44). (2) The abort rule — option
(B) below: an isolated single-run excursion with normal controls is dropped and recorded;
the session aborts on a second excursion in the same block, a control excursion, or AC loss
(D45). **Resume plan:** re-smoke; re-run mt8-licensed for the four group-A kernels plus
the saxpy cell; then serial-repro 3a/3b, mt8-repro, serial-fastmath, mt8-fastmath, gram
blas. The option analyses are kept below as the record of the decision.

### 12.1 mt8 fairness asymmetry — what each arm threads (DECIDED: A)

**Finding.** In the mt8 regime the Fortran arms thread input generation AND the checksum
read-back in every kernel (`!$omp parallel do` on the generator loops and the checksum
sums). The group-A Blade sources thread only the kernel's level 0 (`where omp(x: 1)` on the
`comm` kernel): input generation, the per-row checksum nest and the input sum stay serial
(blade-A's M24 note); group B threads the input map, but not every checksum. The bundle's
`openmp (1 construct)` for gram/comoment3/antisym/pairkernel against the Fortran arms'
threaded three phases is that asymmetry. With the checksum pass measured at 57–70% of the
C++ lane's serial time on comoment3/pairkernel (A §13), Amdahl explains the mt8 tables:
comoment3 blade-cpp 110 → 87 ms (1.26x on 8 threads) while the experts reach 4–5 ms;
pairkernel 77 → 57 ms while the experts reach 6 ms; even antisym (small checksum share)
widens from a 2.4x to a 3.0x loss.

**The matched-permission rule (README §2/§3) says the two sides should be symmetric.**
Options:

- **(A) Every arm threads preparation and checksum where the language allows.** Blade: a
  `where omp` licence on the generator map's outer level and on the checksum fold / nest
  (README §7.4 spellings; the per-row checksum nest's outer `method_for(range<I>)` takes
  `omp(i: 1)`, the input sum a licensed fold). Consequences: the four group-A Blade
  sources change (blade-A), the smoke evidence resets (README §6 gate), and the mt8
  group-A blocks (and group B where a checksum is unthreaded) are re-run; the serial
  regimes are unaffected (a threading licence is inert there). This is the option the
  matched-permission rule implies and the one that makes the mt8 ratios a kernel
  comparison.
- **(B) No arm threads preparation or checksum.** Fortran removes the pragmas from the
  generator and checksum loops (FORT); Blade unchanged. Consequences: Fortran sources
  change, smoke resets, every mt8 block re-runs; the mt8 numbers then measure the kernel's
  threading alone, at the cost of Amdahl capping every arm equally (the serial phases are
  40–60% of the small kernels' regions).
- **(C) Keep as measured, labelled.** The current tables stand with the "serial checksum
  included" label and the attribution column; the mt8 group-A ratios are then explicitly
  NOT a like-for-like threading comparison and the write-up says so wherever they appear.

The architect's recommendation is (A): it is what README §2's rule already says, it
exercises the compiler's own licence mechanism (the suite's thesis), and it isolates G1 /
G3 as the remaining mt8 losses instead of hiding them behind Amdahl. Until decided, the
mt8 group-A numbers are presented under (C).

### 12.2 The 3x-running-median abort on a single isolated excursion (DECIDED: B)

**Finding.** The saxpy mt8 block was lost to one run (blade-cpp-inputonly, 43 ms, 7.0x its
6 ms running median) in round 7 of 21, with both controls normal and all other runs in the
round at 5–11 ms. The rule (README §6, charter 2.5) exists to stop a thermal event; this was
a single-process stall of the smallest binary, and the rule cannot tell the two apart.

Options (the harness owner makes any change):

- **(A) Keep the rule as is** (safest for the host; costs a block per isolated stall; the
  block is re-run).
- **(B) Isolated-excursion tolerance:** a single run above 3x the running median, with
  both controls normal in that round and no second excursion in the same block, is
  DROPPED AND RECORDED (the sample stays in the bundle, marked, excluded from inference;
  the planned-cell manifest says so); the block aborts on a second excursion or on any
  control excursion. Consequences: a `harness/run.py` change, a README §6 sentence, and the
  bundle schema gains an `excluded_excursion` mark; no source changes.

The architect's recommendation is (B) with the record kept visible: the rule's purpose
(thermal safety) is served by the control check, and a 43 ms stall of a 6 ms process is
not a thermal signature. Either way, saxpy mt8 is re-run.

Until the blas session lands:

- **Dropped and recast workloads** (README §8 inventory): `band` (BL7004 under a deferred
  fold; hashed key table under compute; G4); the pair SUM over a `comm` kernel (recast as
  the `pairkernel` matrix; BL3999; G2); the O(1)-per-cell antisymmetric map (recast as the
  angular-momentum tensor). The supported claim is about materialized symmetric objects,
  fused chains and folds — not pair sums or constrained enumeration.
- **Honesty notes so far:** the host runs uncapped by the user's choice; 5 of 6 session-1a
  and 4 of 5 session-1b blocks are drift-suppressed and reported as indicative (an
  80%-capped re-run, `harness/README.md`, is the documented way to unsuppressed verdicts;
  no threshold was loosened); the two clean blocks are antisym and onepass. n = 11 per cell
  for the four compact kernels, 21 for the other seven. The attribution column is informal
  phase evidence; totals are the results; incrementals are total-minus-twin, not kernel
  time. Controls are in the same tables as the thesis kernels; antisym's dense-baseline
  wins exceed the hypothesis and are diagnosed, its expert losses are diagnosed, not
  excused; maskagg's loss is far larger than hypothesised and is diagnosed (G9). Every
  Fortran arm was reviewed (`notes/review-fortran.md`); "expert" means reviewed, not
  proven optimal. The oracle is independent of both languages; 0 disqualifications in
  both sessions; one expected fallback (maskagg blade-llvm). **Rule breach:** fusedchain's
  two natural arms peaked at 65.9 / 65.5 MB, ~3% over the 64 MB working-set rule (README
  §7.3); kept and flagged, not re-run. Not run yet: sessions 2–5.
