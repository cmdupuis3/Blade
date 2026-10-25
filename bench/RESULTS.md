# Blade vs gfortran / ifx — results

> **Record cleanup, 2026-10-24.** The per-wave files this document cites -- `bench/notes/round2/`, `bench/notes/wave3/`,
> `bench/notes/round3/` (the A/B records `w1-ab.md` / `w2-ab.md` / `w3-ab.md`, the Codex reviews, the builder briefs, the
> gates summaries, the chain scripts, the sweeps, the P1 prototype), the top-level plans, sketches and reviews, the
> runner record and the tracked session bundles -- were removed from the tree; they are in git history at cf2028cc.
> Every measured number they carried is a row of `bench/results/stats.csv` (`label` = the A/B mirror named in the text;
> `python bench/harness/stats.py sql "..."` queries it). Nothing below was rewritten; read a cited path as a historical one.

**Round 2 (2026-10-20):** two more waves of microkernels plus an effect-analysis hardening track -- see **§15** (what landed, the wave-1 A/B, the wave-2 A/B, the gap rows it opened). Integration branch `worktree-bench-fortran`, final tip f1910060.

**Status (2026-10-19):** sessions **1a** (serial x licensed x native: gram, comoment3,
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
filled from `results/full/RUNNER-RECORD.md`): **with session 9 on the twin
binary (30FBBC97…, 2dcc0739; `session-220309/`), `blade-llvm` beats or matches the reviewed
experts serially on gram (1.37 / 1.24), antisym (1.13 / 1.07), comoment3 (1.22 / 1.23) and
pairkernel (1.12 / 1.04) in the licensed default — all live — and on gram (1.29 / 1.17)
and antisym (1.04 / 1.00) in the reproducible build**; under threads gram 1.28 / 1.22 and
comoment3 1.46 / 1.22 (live), antisym 1.08 / 1.12 *. Remaining serial losses: comoment3
repro 0.44 (checksum pass; G34 candidate), pairkernel repro 0.78–0.83, recur 0.49–0.55
(G8), band 0.25–0.51 (G30/G33). Open items from the re-measure: G32, G33, G34. **Final gate run on
tree 2dcc0739 (DLL 30FBBC97; consoles `results/gates/final-*.txt`): `blade test` TOTAL
6699 passed, 0 failed; `test interp` 2889 passed, 0 failed, 2 skip-unsupported; `test
opt-diff` 1976 passed (1838 optimizer-inert), 0 failed, 915 skipped; `test llvm` 722
passed, 0 failed, 677 skipped (lane refusals); `test docs` 131 passed, 0 failed, 78
sketch skips.** The branch `worktree-bench-fortran` remains unmerged to master by the
user's choice. §13 is closed as the phase-2 record; **§14 measured on the final tree
d3d5722b (2026-10-19); not yet merged to master** (the follow-up round; round-3 gates
all green). Consolidated per-session
tables: `results/full/RUNNER-RECORD.md` (the runner's record, cross-referenced, not
duplicated here).
Both §12 decisions are taken (mt8 matched parallelism → group-A mt8 rows superseded and
re-run; isolated-excursion drop-and-record rule). Resume sequence RUNNING: re-smoke group
A → mt8-licensed group A + saxpy → serial-licensed antisym (re-measure, §3.1) →
serial-repro 3a/3b → mt8-repro → serial-fastmath → mt8-fastmath → gram blas. The headline
(§11) is written when the blas session lands.

Every number below is copied from a session bundle's `summary.md` / `session.json`; a cell
without a bundle reads `unrun`. The method is `README.md` (entry point: fairness, regimes,
reporting), `kernels/README.md` (tiers, output contract, Blade facts) and `harness/README.md`
(statistics, bundle); kernels `notes/kernel-list.md` and `kernels/<name>/spec.md`; compiler gaps `notes/gap-register.md`; decisions
`notes/design-decisions.md`.

## 0. How to read this document

- **Headline estimand:** the complete timed region (allocation + input generation + kernel
  + both checksums), fresh process, allocation-inclusive (kernels/README.md §3.1).
- **Ratio convention:** ratio = median(arm) / median(Blade reference). **Ratio > 1: Blade
  faster (the arm takes more time); ratio < 1: the arm faster.** Reference: `blade-llvm`
  in serial regimes, `blade-cpp` in mt8 (the LLVM lane has no threads) and wherever the
  LLVM lane refused. 95% intervals: round-block bootstrap, 10,000 resamples (harness/README.md "Protocol and statistics").
- **Verdicts** (δ = 10%): win / detectable-win / equivalent / detectable-loss / loss /
  inconclusive. **`indicative (suppressed by drift)`**: the block failed a drift, trend or
  control check; medians, intervals and the would-be ratio are shown, no verdict is
  claimed, nothing is re-thresholded (README §5; the published measurement runs uncapped
  by the user's choice).
- **Attribution** (compact kernels only): `kernel-only estimate, informal: checksum share
  X%` from `notes/llvm-acceptance-A.md` §13 phase measurements — an attribution, not a
  result; the total is the result.
- **Incremental** (kernels with twins): median(total) − median(own input-only twin) with
  its interval, NOT kernel time; an incremental ratio only where defined (kernels/README.md §3.3).
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
`blas`; bit-identical inputs from the five-round generator (kernels/README.md §3.2); correctness
against the independent numpy oracle on every run; seeded random permutation of arms per
round; two drift controls per round; whole-round bootstrap; freeze at bench-branch commit
957afd95 (bundle git 92777698 is the bench branch at run time).

## 2. Sessions

Cooldowns (inter-run spacing can change thermal state): every session up to and including
serial-repro 3b ran with 2 s between runs, 10 s between kernels and ~5 min between
sessions; from the next session on the relaxed D46 values apply (0.5 s / 3 s / 60 s) — the
column on the right says which was in force. The 64 MB and 2.5 s caps stayed frozen
throughout this run (harness/README.md "Limits: hygiene, not safety").

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
cell count (that is what "dense baseline" means, kernels/README.md §1). Against the experts Blade
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
faster than the C++ lane and gfortran, kernels/README.md §3.3), so the incremental estimates say the
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
MB cap (kernels/README.md §4.3) by ~3% — the three temporaries plus the input at n = 2,000,003 were
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
thermal state, the stream control memory-subsystem contention (harness/README.md "Protocol and statistics"). In session 1a
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
against the independent oracle. Ratio > 1 = Blade faster. Verdict words follow harness/README.md "Protocol and statistics"
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

### 11.3 Dropped and recast workloads (README §5 inventory)

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
| antisym (session 9, twin binary 30FBBC97) | 73.95 (cpp 107.9) | **27.59** | 28.56 | 31.06 / 29.61 | **1.126 [1.119, 1.138] / 1.073 [1.065, 1.085]** | **WIN (gf) / detectable win (ifx), live** (n = 21) | 28.2x / 12.2x | G1 both lanes (F M7, LLVM twin 2dcc0739), G3b | K blocking |
| comoment3 (session 9, twin binary) | 41.49 (cpp 109.7) | **9.312** | 9.593 | 11.37 / 11.40 | **1.221 [1.208, 1.235] / 1.225 [1.219, 1.233]** | **WIN, live** (n = 21) | 64.6x / 77.4x | G1/G1b both lanes (F M8, LLVM twin), G3a (H M4), G3b | — |
| pairkernel | 42.59 (cpp 77.25) | **28.94** | 29.14 | 32.44 / 30.18 | **1.121 [1.118, 1.125] / 1.043 [1.036, 1.047]** | **WIN, live** (n = 21; ifx: detectable) | 2.08x / 2.23x | G3a (H M4), G3b | — |
| recur | 116.9 (cpp 134.1) | 20.43 | 22.49 | 10.07 / 9.995 | 0.493 [0.457, 0.523] * / 0.489 [0.466, 0.500] * | loss, indicative (was 0.090) | 0.93x / 0.77x | G6 (D M1/M3: ~5.7x faster) | the remaining 2x = per-step temporaries + the 48 MB trajectory copy (D's deferred 4.5b(ii)/4.5c, G8) |
| band | — (new) | 3.318 | 9.276 | 0.8135 / 1.042 | 0.245 [0.245, 0.246] / 0.314 [0.314, 0.314] | **loss, live** | 62x / 106x | G4 (C) | G30 (C++ scalar inner loop: 9.3 ms), **G33 candidate** (blade-llvm 3.3 vs the experts' 0.8–1.0 ms although its loop vectorizes) |

Session 9 (`session-220309/`, 21 rounds, controls CV 3.3% / 4.7%, 0 excursions; plan
lines confirmed: blade-llvm `applied [antisym tile (shared C core ... sequential 6x8 ...
strict packed triangle)]` and `applied [simplex tile (shared C core ... W = X(i)*X(j)
panel x 6x8 register tile over k, packed SymIdx<3> pool)]` in licensed AND repro) also
re-ran pairkernel as its control: 29.08 ms, 1.135 * / 1.046 * — unchanged.

¹ Superseded 79AF7EDC values (C++ tiles only; the LLVM reference without its tiles), serial licensed:
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
| antisym (session 9) | 508.2 (cpp 411.3) | **30.47** | 30.65 | 31.77 / 30.46 | **1.043 [1.008, 1.073] / 1.000 [0.975, 1.020]** | **detectable win (gf) / equivalent (ifx), live** — the reproducible build at the experts' speed (from 0.061 / 0.058) | 25.5x / 11.0x | G1 both lanes | — |
| comoment3 (session 9) | 156.8 (cpp 180.6) | 26.46 | 16.12 | 11.73 / 11.77 | 0.443 [0.417, 0.450] / 0.445 [0.420, 0.461] | loss, live (from 0.073) — the kernel tile is in both lanes; the loss is the CHECKSUM PASS (the orbit fold declines in repro) | 22.9x / 27.6x | G1/G1b both lanes | G3a in repro; **G34 candidate: blade-cpp is 0.61 of blade-llvm here (16.1 vs 26.5 ms)** although both run the simplex tile — the LLVM lane's logical-read checksum is slower than the C++ lane's compare-swap canon reads (D M2), i.e. the LLVM lane lacks M2's read |
| pairkernel | 42.78 (cpp 77.59) | 40.82 | 37.85 | 34.32 / 31.91 | 0.841 [0.836, 0.852] / 0.782 [0.777, 0.790] | loss, live | 1.49x / 1.57x | G3b | G3a in repro (the logical-read checksum pass is back) |
| recur | 111.0 (cpp 123.5) | 23.77 | 23.85 | 12.95 / 13.34 | 0.545 [0.538, 0.554] / 0.561 [0.552, 0.571] | loss, live (was 0.117) | 0.75x / 0.63x | G6 | G8, 4.5b(ii) |
| band | — | 9.277 | 9.289 | 3.111 / 3.718 | 0.335 [0.334, 0.336] / 0.401 [0.399, 0.402] | loss, live | 22x / 38x | G4 | G30; in repro both lanes sit at 9.3 ms (the licensed LLVM 3.3 ms was its lanes) |

² Superseded 79AF7EDC values, serial repro (live): antisym blade-llvm 505.9 / blade-cpp 29.78 ms vs
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
| antisym (session 9) | 27.67 | 28.24 | 31.61 / 30.35 | 1.142 [1.105, 1.167] * / 1.097 [1.064, 1.115] * | win, indicative (gf-natural trend 12%) | 5.8x / 3.1x |
| comoment3 (session 9) | 9.622 | 10.60 | 11.98 / 11.79 | 1.245 [1.153, 1.300] * / 1.226 [1.154, 1.318] * | win, indicative (Blade arms' trends 5.9% / 7.1%) | 15.7x / 9.4x |
| band | 3.312 | 9.277 | 1.699 / 1.322 | 0.513 [0.469, 0.517] / 0.399 [0.398, 0.401] | loss, live (clean re-run) | 91x / 100x |

³ Superseded 79AF7EDC values, serial fastmath: antisym blade-llvm 81.31 / blade-cpp 29.48 vs 32.75 /
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
- **Session 9 landed** (twin binary 30FBBC97…, tree 2dcc0739; `session-220309/`): the
  antisym and comoment3 serial rows above are its; it adds **G34 candidate** — in
  serial repro, comoment3 blade-cpp 16.1 ms vs blade-llvm 26.5 ms with both lanes on the
  simplex tile: the LLVM lane's logical-read checksum (the orbit fold declines in repro)
  is slower than the C++ lane's compare-swap canonical reads from D's M2, which the LLVM
  lane does not have. A final gate run closes the set.

### 13.4 Headline of the re-measure (79AF7EDC + session 9 on the twin binary 30FBBC97)

**With the LLVM twins, `blade-llvm` — the headline arm — beats or matches the reviewed
expert tier serially on gram, antisym, comoment3 and pairkernel in the licensed default**
(1.37 / 1.24, 1.13 / 1.07, 1.22 / 1.23, 1.12 / 1.04; all live), **and in the reproducible
build on gram and antisym** (1.29 / 1.17, 1.04 / 1.00; live). The remaining serial losses
are comoment3 repro (0.44, the checksum pass — the orbit fold declines in repro, and the
LLVM lane lacks M2's canonical read: G34 candidate), pairkernel repro (0.78–0.83, the same
pass), recur (0.49–0.55: G8 remnants) and band (0.25–0.51: G30/G33). Everything uncapped,
indicative where starred; the pre-fix figures these replace are in §3–§7.

- **gram now beats both reviewed experts in every regime it was measured**: serial
  licensed 1.37 / 1.24 (live), serial repro 1.29 / 1.17 (live — the reproducible build
  caught up with the licensed one because the tile needs no reorder licence), mt8 licensed
  1.28 / 1.22 (live), mt8 repro 1.08 / 1.04 *, fastmath 1.35 / 1.24 * and 1.23 / 1.17 *,
  and blas serial 1.01 / 0.97 (the checksum tax gone; only G28's repack left, visible at mt8
  0.79 / 0.82). From 0.52 / 0.47 before the fixes.
- **comoment3 beats the experts serially and under threads**: serial licensed 1.22 / 1.23
  (live, from 0.28 / 0.28; blade-llvm 9.31 ms, blade-cpp 9.59), mt8 licensed 1.46 / 1.22
  (live, from 0.24 / 0.20), fastmath 1.25 / 1.23 * and 1.44 / 1.22 *; its one remaining
  loss is serial repro 0.44 (live) — the checksum pass without the orbit fold, where the
  LLVM lane is also 1.6x slower than the C++ lane (G34 candidate).
- **antisym matches or beats the experts in every regime**: serial licensed 1.13 / 1.07
  (live, from 0.42 / 0.40; blade-llvm 27.6 ms), serial repro 1.04 / 1.00 (live, from
  0.061 / 0.058 — the reproducible build at the experts' speed), mt8 licensed 1.08 /
  1.12 *, mt8 fastmath 1.17 / 1.15 *, mt8 repro 0.93 *, serial fastmath 1.14 / 1.10 *.
- **pairkernel wins serial licensed (1.12 / 1.04, live)** from 0.77 / 0.72 — the kernel was
  always equivalent and the orbit fold removed the checksum pass — and regresses under
  threads to 0.82 / 0.79 * (G32) and in repro to 0.84 / 0.78 (the logical pass is back).
- **recur** improved 5–6x (0.09 → 0.49 *, 0.12 → 0.55) and still loses 2x (G8 remnants).
- **band** (new): loses 4x (LLVM) / 3–4x (C++) to the experts and beats the predicate nest
  62–106x; two open items (G30, G33 candidate), no thread form (G31).
- Against the dense naturals every symmetric kernel's margin grew: gram 10x / 10x serial
  and 38x / 39x mt8, comoment3 65x / 77x serial (session 9) and 28x / 35x mt8, antisym 28x /
  12x serial (session 9) and 12.8x / 8.2x mt8, band 62x / 106x.
- Correctness: all pins held in both lanes and both regimes on the merged binaries; the
  orbit fold moves checksums ~4e-15 relative (inside 1e-12).
- Controls per re-measure session (compute / memory CV): 10.4% / 11.3%, 4.5% / 4.4%, 1.9% /
  4.1%, 3.5% / 6.1%, 31.1% / 21.3% (serial-fastmath, disturbed by a sibling build — band
  re-run clean at 1.9% / 3.2%), 2.7% / 4.2%, 1.8% / 10.4% (`RUNNER-RECORD.md`, "Control drift
  per re-measure session"); two aborted mt8-fastmath attempts (a second excursion during a
  sibling `dotnet build`; a 92 ms memory-control excursion) are kept as records. Everything
  uncapped; indicative where starred.

## 14. Follow-up round (2026-10-19) — measured on the final tree d3d5722b (round 3)

Seven branches came out of the §13.3 open items and the §11.5 worklist; all seven are
merged into `worktree-bench-fortran`: checksum-pass 2ebc490f, band 8c66dcb0, blas-repack
5d568404 + 37d97506, maskagg 54c98afe, tri-tiles dc54e1bf, fold-simd 68d344af + fix
4e49f46a, recur f36d5503 + S13 9ebc530f (merged through 7a601446) + fix 9d03e588, with the
integration fix 6016b4f7. The measured tree is d3d5722b (Blade.dll BB52A4C2…); the branch
is not merged to master. §13 stays the closed phase-2 record.

**Source.** `results/full/RUNNER-RECORD.md`, section "Round 3: protocol re-measure of ALL
12 kernels on the FINAL tree d3d5722b" — full intervals and the control drift per session
are there and are not duplicated; bundles
`results/2026-10-19-LAPTOP-350H6LDB/session-{132026 (serial-licensed A), 133604
(serial-licensed B), 135344 (mt8-licensed), 142502 (serial-repro A), 144145 (serial-repro
B), 145930 (mt8-repro), 153107 (serial-fastmath), 155001 (mt8-fastmath), 161102 (gram
blas)}/`; the incremental (total − input-only twin) figures quoted in 14.4, 14.6 and 14.8
are from those bundles' `summary.md`. All twelve kernels in every regime each
`kernel.json` declares, full size, README minimum rounds, interleaved randomized rounds,
both drift controls, D46 cooldowns, the excursion rule; no session aborted, one excursion
dropped (serial-fastmath, onepass ifx-expert-inputonly, round 16); control compute CV
2.2–8.2%, memory CV 5.7–9.3%; 40 of 62 blocks suppressed (37 of them with a Blade
reference; the other three are no-Blade-arm rows). Label as everywhere: **uncapped: max
processor state 100% (Silent scheme); verdicts suppressed by drift are indicative** (`*`).
**Harness evidence caveat:** the bundles' fold-form column does not recognise the scalar
K-lane tree (`__rlane0..7`) still emitted for expression-position / materialized reduces
and reports it as `in-order` (e.g. maskagg blade-cpp and its twin in session 133604, whose
plans say K-lanes(8)); a harness fix is in progress. The medians are unaffected.

**Second opinion.** A Codex audit (`notes/loose-ends-codex.md`) re-checked all 62 final-table
rows against the runner record (no transcription error) and supplied diagnoses used below:
the serial C++ input-twin regression (§1 there), the repro-legality boundary for threaded
folds (§3), the ifx assembly (§4) and small tuning decisions (§7). Its timings are short
diagnostic single-run A/Bs without the protocol's drift controls — causes, not verdicts;
every round-3 number below is unchanged by them.

**Within-session A/Bs ("phase 2", items a–h).** Knob and binary A/Bs run in the same
window as the re-measure, 3 rounds unless stated, scratch bundles under the runner's
scratchpad (not committed); medians in ms as pass 1 / pass 2, binary A/Bs as old1 / new1 /
new2 / old2. NEW = d3d5722b (Blade.dll BB52A4C2…), OLD = master's compiler (sources
identical to f2315642, Blade.dll F9BA95CD…). They are the evidence that a CHANGE moved a
kernel; the before/after tables compare sessions a day apart, in which the experts drift
too.

**Gates on the final tree d3d5722b (round 3; consoles `results/gates/round3-*.txt`):**
`blade test` TOTAL 6780 passed, 0 failed; `test interp` 2912 passed, 0 failed, 2
skip-unsupported; `test opt-diff` 1999 passed (1851 optimizer-inert), 0 failed, 915
skipped; `test llvm` 745 passed, 0 failed, 677 skipped; `test docs` 131 passed, 0 failed,
78 skipped. Round 2 (merged tree 2124edc4, `results/gates/round2-*.txt`) had 5 failures in
`blade test` (TOTAL 6775 passed, 5 failed), all fixed before round 3: functions/168
(fold-simd's K-lane tail took an FMA where the generic instance added rounded products;
4e49f46a rounds every product before the add, in all contraction modes); three BLAS-on
syrk-tile needles in LinAlg Dispatch (blas-repack 37d97506 predated the tri-tiles
`dsyrk_tri` ABI; 6016b4f7); `decision_row_fold_declined_while` (recur S13 did not
recognise the small-buffer `IRArrayLit` zero fill; 9d03e588). Round-3 phase 1 also ran the
fold-lanes, syrk-tile, row-tile and llvm-tiles blocks (green), `run.py check` recur (ok)
and gram (exit 1 under the old blas acceptance rule, expected after 37d97506), and the
smoke `--regimes all` (one failure, the same old rule; the gram-blas re-smoke passed after
39a8a869).

Conventions as in §13: milliseconds, medians; "before" = the §13 state (the 79AF7EDC /
30FBBC97 re-measure) where the kernel was re-measured there, else the pre-fix matrix
(§3–§7, named per row); "after" = round 3; expert columns read before → after (the experts
were re-run in the same sessions); ratio = expert median / Blade median, **> 1 Blade
faster**; reference `blade-llvm` in serial regimes, `blade-cpp` in mt8 and blas; ratio
cells carry the gfortran-expert 95% CI; `*` = verdict suppressed by drift (indicative).
Corpus numbering on the merged tree: checksum-pass `symmetry/123-124`, band
`domains/009-012`, blas-repack `loops/474-476`, maskagg `index-types/511-514`, tri-tiles
`symmetry/125-127`, fold-simd `loops/477-479`, recur `recursive-arrays/026-029`. The
figures quoted in the "What changed" paragraphs are the branch sessions' own single runs —
diagnoses, superseded by the tables. Every "Tests." line records the runner's outcome per
branch commit as the run log has it (outcome and failure names; the per-block TOTAL lines
went to the coordinator and are quoted only where the run log carries them).

### 14.1 checksum-pass (branch 2ebc490f, merged) — G32, G34

**What changed.** G32: the orbit-class checksum fold (H M4) now opens a thread team over
the first-coordinate rows in mt8 — row `i` starts at `canon_count(n) − canon_count(n − i)`,
rows are dealt boustrophedon so the triangle's uneven row lengths balance, each thread
keeps per-class sums, the thread partials are combined in thread order and the
addition-only weight combine follows; `blade plan` prints `fold-form ...; thread team over
the first-coordinate rows`. The LLVM lane stays serial (no thread runtime, G11). G34: the
diagnosis was CORRECTED — both lanes already used the compare-swap canonicalization
network and clang already bound the repeated read; the LLVM repro walk was bound by
per-read binomial offset arithmetic (2(r − 1) runtime binomials, ~38 integer ops per cell
at rank 3). Fix: the packed offset is regrouped into one `coordTerm` per coordinate
(`SimplexBlocksCore.coordTerm`, property-pinned equal to `packedOffsetR` for ranks 2–5),
module-constant tables for static extents ≤ 4096, the remaining divisions marked `sdiv
exact`, and an explicit bind-once of the read. Both lanes are byte-identical under repro.
Corpus `symmetry/123-124`.

**Tests.** d008770d: 1 regression (Simplex Blocks Agreement 'elementwise map over a packed
pool costs no coordinate math' failed on bench-g32-g34, passed 19/19 on the bench binary);
the branch's own A/B aborted x4 on control excursions (machine shared). Follow-up 2ebc490f
(llvm simplex / symmetry / facts): green. Final-tree gates: §14 header (round 3, all
green).

**Within-session A/B (item h, OLD vs NEW, old1 / new1 / new2 / old2).** Serial-repro
comoment3 blade-llvm 25.26 / 15.35 / 14.80 / 25.19 (blade-cpp control 15.69 / 15.76 /
15.02 / 15.91): G34 removes the whole ~10 ms LLVM-lane excess of §13, and the two lanes
now sit level. Serial-repro pairkernel blade-llvm 42.37 / 38.71 / 37.01 / 40.38 (blade-cpp
control unchanged). Mt8-licensed blade-cpp comoment3 3.291 / 3.849 * / 2.894 / 3.188
(* disturbed control), pairkernel 7.641 / 8.222 / 7.599 / 7.491: **G32's thread team did
NOT move pairkernel mt8** — the ~6 ms target is not reproduced. The final mt8 emission
already threads both the packed producer and the orbit checksum (Codex audit §2, from the
135344 plan and C++); a diagnostic phase timing puts ~5.4 ms in the triangular kernel and
~1.5 ms in the checksum, so the old "serial orbit pass" explanation is retired: what the
expert has and Blade lacks is producer-local checksum scheduling (each column re-read for
the checksum inside the same team) and team reuse.

| regime | kernel | before blade-llvm / blade-cpp (ms) | after blade-llvm / blade-cpp (ms) | gf-expert / ifx-expert (ms) | ratio before → after (gf / ifx) | verdict |
|---|---|---|---|---|---|---|
| mt8 x licensed (ref cpp; §13 session 202815) | pairkernel | — / 7.67 | — / 7.541 | 6.27 / 6.03 → 6.032 / 6.014 | 0.82 * / 0.79 * → 0.800 [0.768, 0.829] * / 0.798 * | loss, indicative (135344); unchanged |
| mt8 x licensed | gram | — / 15.58 | — / 13.96 | 19.87 / 19.06 → 19.44 / 18.59 | 1.28 / 1.22 (live) → 1.393 [1.309, 1.441] * / 1.332 * | win, indicative (135344; control) |
| mt8 x licensed | comoment3 | — / 3.311 | — / 2.973 | 4.839 / 4.03 → 4.932 / 4.082 | 1.46 / 1.22 (live) → 1.659 [1.598, 1.716] * / 1.373 * | win, indicative (135344; control) |
| mt8 x licensed | antisym | — / 8.79 | — / 9.026 | 9.50 / 9.82 → 10.65 / 10.65 | 1.08 * / 1.12 * → 1.179 [1.139, 1.268] * / 1.180 * | win, indicative (135344; control) |
| serial x repro (ref llvm; §13 session 9) | comoment3 | 26.46 / 16.12 | 15.3 / 15.54 | 11.73 / 11.77 → 11.82 / 11.93 | 0.44 / 0.45 (live) → 0.773 [0.754, 0.783] / 0.780 | loss narrowed, live (142502) |
| serial x repro | pairkernel | 40.82 / 37.85 | 38.22 / 38.64 | 34.32 / 31.91 → 34.5 / 32.11 | 0.84 / 0.78 (live) → 0.903 [0.898, 0.913] / 0.840 | loss narrowed, live (142502) |
| serial x repro | antisym | 30.47 / 30.65 | 31.12 / 31.62 | 31.77 / 30.46 → 32.5 / 31.28 | 1.04 / 1.00 (live) → 1.045 [1.020, 1.079] / 1.005 | win / parity, live (142502; control) |
| mt8 x repro (ref cpp; §13 session 205306) | comoment3 | — / 9.91 | — / 10.82 | 4.861 / 4.101 → 5.171 / 4.615 | 0.49 * / 0.41 * → 0.478 [0.464, 0.525] / 0.427 | loss, live (145930); unchanged (G35) |
| mt8 x repro | pairkernel | — / 17.34 | — / 18.16 | 6.217 / 6.472 → 6.72 / 6.515 | 0.36 * / 0.37 * → 0.370 [0.354, 0.381] / 0.359 | loss, live (145930); unchanged (G35) |

**Register outcome (measured, round 3):** G34 FIXED (measured) — serial repro comoment3
blade-llvm 26.46 → 15.30 ms, level with blade-cpp (15.54), ratio 0.44 / 0.45 → 0.77 / 0.78
(live). What G34 fixed is the ~10 ms LLVM-lane EXCESS; the residual loss (0.77 / 0.78) is
common to both lanes and is not G34's. Pairkernel repro 0.84 / 0.78 → 0.90 / 0.84. G32
threaded as designed, **no measured gain on pairkernel mt8 (0.80)**; open (producer-local
checksum scheduling, above).

### 14.2 band (branch 8c66dcb0, merged) — G30, G31, G33

**What changed.** G33 was the enumeration ORDER, not vectorization: the licensed fold over
a difference-bounded two-field domain now enumerates by diagonal (`k = i − j` outer; knob
`BLADE_DOMAIN_DIAGONAL`; a general planner rule, licensed-only — repro keeps the
lexicographic order bit-identical), and the expert's licensed arm does the same (`phi`
hoisted per diagonal); the per-cell-division floor is ~2 ms on this Zen 3. G30's real
blockers in the C++ lane were an int64→double conversion with no AVX2 form, a mutable
global `w`, and a frame-scan bug that kept `phi`'s `BLADE_FRAME` because
`static_cast<double>(` read as a functor call; the repro lanes now vectorize the terms with
an in-order sum. G31: a thread-level form for closed-form domain nests — one team, `k` in
fixed per-thread blocks, fixed-order combine (a follow-up fixed a captured-reference gather
inside the OpenMP region); map kernels that assign a captured `let mut` keep lexicographic
order and the serial pass. `kernel.json` is no longer `no_parallel_form`. The branch
session's own single runs (NOT results): serial licensed blade-llvm 0.40 / blade-cpp 0.68
ms against the experts' 0.81 / 1.04; repro 3.8–4.2 ms, both lanes bit-identical; mt8
licensed blade-cpp 1.07 ms. Corpus `domains/009-012`.

**Tests.** b08a9aef (tests 1–14, validate_B x2, checks 17–18): green. Follow-up 8c66dcb0:
run, no failure recorded in the run log. Final-tree gates: §14 header (round 3, all
green).

**Within-session A/B (item f; serial licensed, blade-cpp / blade-llvm, ms).** Full band:
NEW 0.707 / 0.412, `BLADE_DOMAIN_DIAGONAL=0` 2.974 / 2.852 (the per-cell-division floor),
OLD 9.273 / 3.316. Narrow band: NEW 0.158 / 0.100, `BLADE_DOMAIN_DIAGONAL=0` 0.758 /
0.595, OLD 1.123 / 0.613. G30 is the OLD → `DIAGONAL=0` step in the C++ lane (9.27 → 2.97
ms); G33 is the `DIAGONAL=0` → NEW step in both lanes (4–7x).

| regime | kernel | before blade-llvm / blade-cpp (ms) | after blade-llvm / blade-cpp (ms) | gf-expert / ifx-expert (ms) | ratio before → after (gf / ifx) | verdict |
|---|---|---|---|---|---|---|
| serial x licensed (ref llvm; §13 session 201558) | band | 3.318 / 9.276 | 0.4158 / 0.6335 | 0.8135 / 1.042 → 0.8159 / 1.044 (expert sources unchanged) | 0.245 / 0.314 (live) → 1.962 [1.908, 1.995] / 2.511 | **win, live** (132026) |
| serial x repro (ref llvm; §13 session 204141) | band | 9.277 / 9.289 | 3.685 / 3.959 | 3.111 / 3.718 → 3.111 / 3.716 | 0.335 / 0.401 (live) → 0.844 [0.843, 0.847] * / 1.008 * | loss (gf) / parity (ifx), indicative (142502) |
| mt8 x licensed (ref cpp; §13 session 202815) | band | no Blade arm (G31) | — / 1.07 | 0.9909 / 1.705 → 1.04 / 1.78 | — → 0.972 [0.844, 1.053] * / 1.663 * | parity (gf) / win (ifx), indicative (135344) |
| mt8 x repro (ref cpp; §13 session 205306) | band | no Blade arm (G31) | — / 4.678 | 1.33 / 2.344 → 1.411 / 2.431 | — → 0.302 [0.276, 0.331] * / 0.520 * | loss, indicative (145930; G35) |
| serial x fastmath (ref llvm; §13 session 214954) | band | 3.312 / 9.277 | 0.4278 / 0.6455 | 1.699 / 1.322 → 1.695 / 1.325 | 0.513 / 0.399 (live) → 3.961 [3.638, 4.069] / 3.098 | **win, live** (153107) |
| mt8 x fastmath (ref cpp; §13 session 213323) | band | no Blade arm (G31) | — / 1.112 | 1.179 / 1.914 → 1.161 / 1.97 | — → 1.044 [0.971, 1.149] * / 1.773 * | parity (gf) / win (ifx), indicative (155001) |

**Register outcome (measured, round 3):** G30 FIXED (measured) — blade-cpp 9.276 → 0.6335
ms serial licensed, 9.289 → 3.959 ms repro; G33 FIXED (measured) — blade-llvm 3.318 →
0.4158 ms, 0.245 / 0.314 → 1.96 / 2.51 (live), fastmath 0.51 / 0.40 → 3.96 / 3.10 (live);
G31 FIXED (measured, licensed) — mt8 0.97 * / 1.66 *, mt8 fastmath 1.04 * / 1.77 *; the
mt8 repro cell (0.30 * / 0.52 *) is the reproducible threaded fold, serial by design
(G35). The expert sources did NOT change on this item: the Fortran agent's licensed expert
already walked the band by diagonal under `BENCH_LICENSED` (the per-cell order is its repro
arm), which is what the session found when it read the assembly; only `kernel.json`
changed (no_parallel_form dropped). The measured expert medians confirm it (0.8135 →
0.8159 ms).

### 14.3 blas-repack (branch 5d568404 + 37d97506, merged) — G28, M13 hot-row fold

**What changed.** G28: the BLAS-on `dsyrk` route gained a threaded repack argument (the
serial dense staging + repack that cost 18–21% under 8 library threads in §13). M13: the
hot-row fold (`BLADE_HOT_ROW` gate in `optimizerGates`, plan line `[hot-row-fold v1]`)
schedules the consumer fold inside the producer loop for stencil-shaped halo maps — the
checksum of a halo map no longer materializes the second output-sized array (the G10
mechanism). Corpus `loops/474-476`.

**Late additions.** 37d97506 (merged through 571dfb05): **the packed syrk tile preempts
host `?syrk` where eligible**, so in the blas regime blade-cpp's gram runs Blade's native
tile, not the library. The user decided that a native kernel that beats the library is a
valid blas-regime cell: harness rule **D47** (`notes/design-decisions.md`, committed
39a8a869) accepts such a build only when its own plan records that the library declined and
the syrk tile applied for the same subject; a silently unrouted build is still refused; the
cell's evidence reads `blas_route: native-preempt`. The opt-in **`BLADE_BLAS_PREFER`**
(`auto|native|library`; the env var over the `blade.toolchain.json` key; `auto` with MKL
prefers the library) is on branch blas-repack 5d1a3814, tested green, **NOT merged into
the bench branch** — the round-3 blas cells ran the default, which has no such knob.

**Tests.** 5d568404: green. 37d97506 was first tested in round 2 on the merged tree: three
LinAlg Dispatch failures (the BLAS-on syrk-tile needles predated the tri-tiles
`dsyrk_tri` ABI), fixed in 6016b4f7 (TEST RESULT bench-merge 6016b4f7: linalg Dispatch
136/0, probe 21/21, Syrk Tile 4/0, Row Contraction Tiles 19/0, BLAS Tier 19/0). 5d1a3814
(`BLADE_BLAS_PREFER`, unmerged): linalg Dispatch 153/0 (17 new `blas_prefer_*` cases),
syrk-tile 4/0, optimize 152/0, cli 101/0, corpus-wiring 14/0, docs 131/0/78. Final-tree
gates: §14 header (round 3, all green).

**Within-session A/B (item c; gram blas, blade-cpp).** Tile (the default) vs
`BLADE_SYRK_TILE=0` (the library route): serial 61.6 / 62.9 vs 67.4 / 67.2 ms (gf-expert
~67, ifx-expert ~64.5); mt8 18.4 / 18.8 vs 24.3 / 28.0 ms (gf ~25, ifx ~24.5) — the tile is
~7% faster than the library serially and ~29% under 8 threads. G28 staging microbench (ms),
1 thread: zero_fill 3.15, dsyrk_prefault 54.0, dsyrk_fresh 56.3, repack serial 1.50 / omp
1.49, whole route OLD 61.3 vs NEW 59.6; 8 threads: zero_fill 3.58, dsyrk_prefault 16.9,
dsyrk_fresh 18.8, repack serial 1.37 / omp 1.18, whole route OLD 22.5 vs NEW 19.7 — G28
saves ~3% of the library route serially and ~12% at 8 threads.

**Within-session A/B (item d; stencil, `BLADE_HOT_ROW` on vs 0).** Serial blade-cpp 17.3 /
17.75 vs 18.7 / 19.2, blade-llvm 11.6 / 11.35 vs 12.5 / 12.3; mt8 blade-cpp 8.56 / 8.35 vs
9.7 / 10.7 — the hot-row fold is worth 6–8% serially and 15–20% under 8 threads.

| regime | kernel | before blade-llvm / blade-cpp (ms) | after blade-llvm / blade-cpp (ms) | gf-expert / ifx-expert (ms) | ratio before → after (gf / ifx) | verdict |
|---|---|---|---|---|---|---|
| blas x mt8 (ref cpp; §13 session 214522) | gram | — / 24.46 | — / 17.97 (native-preempt) | 19.27 / 19.98 → 21.45 / 22.44 | 0.788 / 0.817 (live) → 1.194 [1.144, 1.346] / 1.249 | **win, live** (161102) |
| blas x serial (ref cpp) | gram | — / 64.53 | — / 61.84 (native-preempt) | 65.39 / 62.48 → 67.37 / 64.43 | 1.013 / 0.968 (live) → 1.089 [1.074, 1.126] / 1.042 | win, live (161102) |
| serial x licensed (ref llvm; pre-fix §3.6) | stencil | 13.51 / 16.34 | 10.71 / 17.25 | 14.46 / 11.64 → 14.72 / 11.65 | 1.070 * / 0.861 * → 1.375 [1.302, 1.404] * / 1.089 * | win, indicative (132026) |
| mt8 x licensed (ref cpp; pre-fix §5.8) | stencil | — / 11.49 | — / 9.579 | 8.968 / 9.120 → 8.481 / 8.868 | 0.780 * / 0.794 * → 0.885 [0.828, 0.970] * / 0.926 * | loss narrowed, indicative (135344) |
| serial x repro (ref llvm; pre-fix §6.1) | stencil | 15.97 / 17.77 | 14.28 / 21.02 | 16.80 / 13.47 → 17.48 / 14.29 | 1.052 * / 0.843 * → 1.224 [1.210, 1.238] * / 1.001 * | win (gf) / parity (ifx), indicative (142502) |

The blade-cpp serial stencil medians read slower than pre-fix (16.34 → 17.25 licensed,
17.77 → 21.02 repro) across sessions a day apart, while the in-session A/B shows the
hot-row fold making the same lane 6–8% faster; the cross-session drift is not this
change's (G37's input-conversion cause was confirmed on the dot and saxpy twins only, so
stencil's share of it is unmeasured).

**Register outcome (measured, round 3):** G28 FIXED (measured) — the library route's
staging gains ~12% at 8 threads in-session, and the default blas cell is now the native
tile (D47): blas mt8 0.79 / 0.82 → 1.19 / 1.25, serial 1.01 / 0.97 → 1.09 / 1.04 (both
live). G10 FIXED (measured where the pass applies) — hot-row fold 6–8% serial, 15–20% mt8
in-session; stencil mt8 is still a loss (0.89 * / 0.93 *).

### 14.4 maskagg (branch 54c98afe, merged) — G9 / M12

**What changed.** Rank-1 `compound` is a prefix-count compaction: `compound_index_t<1>` is
a template specialization holding the ascending kept positions (a count pass + one
branchless fill), BL8006 on a non-kept key; the hashed `SparseIdx` representation is kept
for rank ≥ 2, wildcard and partial reads. The LLVM lane now accepts rank-1 compounds (new
shim symbol `blade_compact1_rank`, attribute group `memory(argmem: read)`), so maskagg's
`blade-llvm` arm is a real lane figure: `kernel.json` flipped to `expected_llvm "lane"`.
The interpreter twin gained the N-operand apply over compounds. The branch session's own
single runs (NOT results): licensed blade-llvm 7.1 ms, blade-cpp 14.1 ms (from 65 ms /
fallback); repro 7.7 / 8.1 ms; experts 3.6–5.0 ms. Corpus `index-types/511-514`.

**Tests.** 24f3435d: 1 failure (interp: index-types 511 not in `interpExpectedUnsupported`).
Follow-up 54c98afe (build + 4 interp keys): run, no failure recorded in the run log.
Final-tree gates: §14 header (round 3, all green).

**Within-session A/B (item h, OLD vs NEW, old1 / new1 / new2 / old2).** Serial-licensed
blade-cpp 61.11 / 6.98 / 7.22 / 77.58, blade-llvm OLD fallback / NEW 4.85 / 5.74;
serial-repro blade-cpp 60.12 / 7.71 / 8.40 / 68.87, blade-llvm NEW 5.44 / 5.40. **Open
item:** maskagg's input-only blade-cpp twin rose from ~2.85 to ~3.85 ms (record: 2.822 →
3.774 licensed, 3.304 → 4.282 repro). maskagg uses the same input generator as the dot /
saxpy twins in which G37's cause was identified (band-vec's split Int64→Float64
conversion, §14.8); it will be re-measured after the context-aware fix (G38).

| regime | kernel | before blade-llvm / blade-cpp (ms) | after blade-llvm / blade-cpp (ms) | gf-expert / ifx-expert (ms) | ratio before → after (gf / ifx) | verdict |
|---|---|---|---|---|---|---|
| serial x licensed (ref: blade-cpp before, blade-llvm after; pre-fix §4.3) | maskagg | fallback / 67.16 | 4.68 / 7.054 | 3.993 / 2.919 → 4.074 / 2.939 | 0.059 * / 0.043 * → 0.871 [0.835, 0.891] * / 0.628 * (vs blade-cpp: 0.58 / 0.42) | loss, indicative (133604) |
| serial x repro (pre-fix §6.2) | maskagg | fallback / 68.86 | 5.942 / 8.265 | 4.982 / 3.752 → 4.982 / 4.02 | 0.072 / 0.054 (live) → 0.839 [0.806, 0.895] * / 0.676 * (vs blade-cpp: 0.60 / 0.49) | loss, indicative (144145) |
| mt8 | maskagg | serial by protocol (D-rule, kernels/README.md §1) | — | — | — | — |

The "vs blade-cpp" ratios are expert median / blade-cpp median computed from the table's
medians (no CI). Incremental (total minus input-only twin, a difference of medians, not a
separately timed kernel phase; bundle `summary.md`): serial licensed blade-llvm 3.036 ms, blade-cpp 3.280, experts 1.164 / 1.188
(ratio 0.383 / 0.391); repro 3.758 / 3.983 vs 1.466 / 1.568 (0.390 / 0.417); before, the
blade-cpp increment was 64.34 ms licensed. The residual loss is the compaction itself
(~3.0 ms against the experts' ~1.2 ms pack); the input generation is at or below the
experts' (blade-llvm twin 1.643 ms vs 2.91 / 1.751).

**Register outcome (measured, round 3):** G9 FIXED (measured) — blade-cpp 67.16 → 7.05 ms
(9.5x; in-session OLD 61–78 → NEW 7.0–7.2), blade-llvm a lane arm at 4.68 ms; ratio vs the
new reference 0.87 * / 0.63 * — **maskagg is the remaining serial-licensed loss to both
experts** (repro 0.84 * / 0.68 *). The maskagg half
of G12/G22's history is closed (the LLVM fallback is gone). The reference arm changed from
`blade-cpp` to `blade-llvm` for this kernel; the before/after ratio therefore reads against
two different references and the table says so.

### 14.5 tri-tiles (branch dc54e1bf, merged) — M15 + G1 remnants

**What changed.** The register-tile family (E/F/G, §13) gets top-aligned column and k
panels; the scale tail is fused into the last K block's tile store (no post-pass); the
syrk route gains row-panel groups (`BLADE_TILE_TUNE syrk_g`, default 4) and antisym gains
sample blocks (`anti_kc`, default 0 = whole chain); the gram-node dispatch prints an omp
census line. 120 program x mode runs are bit-identical to master; raw-bit blocks syrk
2729/2729, row 3177/3177, cross-lane 1660/1660. Corpus `symmetry/125-127`.

**Tests.** 8520a22a: 8 needle failures (row-tile/linalg x5+4, optimize x2, llvm facts x1;
raw-bit suites pass). Follow-up dc54e1bf: run, no failure recorded in the run log. Final-tree
gates: §14 header (round 3, all green).

**Within-session A/B (items a, b; serial licensed).** `BLADE_TILE_TUNE syrk_g` on gram:
blade-llvm g1 61.1 / 58.8, g2 57.2 / 58.0, **g4 (default) 57.3 / 57.4**, g8 60.5 / 60.2;
blade-cpp g1 59.1 / 60.7, g2 60.4 / 61.3, g4 60.1 / 61.4, g8 64.0 / 64.0 → the default 4
is kept. `anti_kc` on antisym: blade-llvm default (0) 27.6 / 27.5, 128 26.9 / 35.2 (second
pass disturbed), 200 26.8 / 26.7; blade-cpp 30.4 / 30.5, 29.4 / 33.8, 29.2 / 29.1 → 200 is a
small directional improvement (~3% llvm, ~4% cpp), insufficient evidence to change the
default (one shape, 3 rounds; 200 stays a tuning candidate). Only these two of the tuning
points T1–T7 were A/B'd in round 3.

| regime | kernel | before blade-llvm / blade-cpp (ms) | after blade-llvm / blade-cpp (ms) | gf-expert / ifx-expert (ms) | ratio before → after (gf / ifx) | verdict |
|---|---|---|---|---|---|---|
| serial x licensed (ref llvm; §13) | gram | 56.1 / 57.96 | 58.11 / 61.88 | 76.75 / 69.36 → 78.53 / 71.62 | 1.368 / 1.236 (live) → 1.351 [1.327, 1.386] * / 1.233 * | win, indicative (132026); unchanged |
| serial x licensed | antisym | 27.59 / 28.56 | 28.01 / 29.97 | 31.06 / 29.61 → 31.19 / 30.1 | 1.126 / 1.073 (live) → 1.113 [1.099, 1.152] / 1.075 | **win, live** (132026); unchanged |
| serial x licensed | comoment3 | 9.312 / 9.593 | 8.726 / 8.945 | 11.37 / 11.40 → 11.56 / 11.68 | 1.221 / 1.225 (live) → 1.325 [1.310, 1.335] / 1.338 | **win, live** (132026) |
| serial x repro (ref llvm; §13) | gram | 59.73 / 61.34 | 62.89 / 65.38 | 77.07 / 70.12 → 79.77 / 72.88 | 1.290 / 1.174 (live) → 1.268 [1.221, 1.310] / 1.159 | **win, live** (142502); unchanged |
| serial x repro | antisym | 30.47 / 30.65 | 31.12 / 31.62 | 31.77 / 30.46 → 32.5 / 31.28 | 1.043 / 1.000 (live) → 1.045 [1.020, 1.079] / 1.005 | win / parity, live (142502) |
| serial x repro | comoment3 | 26.46 / 16.12 | 15.3 / 15.54 | 11.73 / 11.77 → 11.82 / 11.93 | 0.443 / 0.445 (live) → 0.773 [0.754, 0.783] / 0.780 (the checksum pass is 14.1's) | loss, live (142502) |
| mt8 x licensed (ref cpp; §13) | gram | — / 15.58 | — / 13.96 | 19.87 / 19.06 → 19.44 / 18.59 | 1.276 / 1.223 (live) → 1.393 [1.309, 1.441] * / 1.332 * | win, indicative (135344) |
| mt8 x licensed | antisym | — / 8.79 | — / 9.026 | 9.50 / 9.82 → 10.65 / 10.65 | 1.081 * / 1.117 * → 1.179 [1.139, 1.268] * / 1.180 * | win, indicative (135344) |
| mt8 x licensed | comoment3 | — / 3.311 | — / 2.973 | 4.839 / 4.03 → 4.932 / 4.082 | 1.462 / 1.217 (live) → 1.659 [1.598, 1.716] * / 1.373 * | win, indicative (135344) |

**Register outcome (measured, round 3):** tuning, reported as such — the defaults are kept
(`syrk_g` 4, `anti_kc` 0); serial gram and antisym hold their §13 ratios within drift,
comoment3 serial licensed moves 1.22 → 1.33 (live) and the mt8 group-A ratios read higher
(indicative). G1 itself was already FIXED and measured; the "K blocking" remnant on
antisym is answered by `anti_kc` (a small directional improvement at 200, ~3% llvm / ~4%
cpp, insufficient evidence to change the default 0), the scale post-pass and panel
alignment are gone, the gram-node omp census line is printed.

### 14.6 fold-simd (branch 68d344af + 4e49f46a, merged) — M9, M14 / G25, G26

**What changed.** Dot-class licensed folds take a K-lane vector form (`BLADE_FOLD_LANES`
knob; `src/cpp/blade_fold_lanes.hpp`; raw-bit block `Fold Lanes` 38168/38168 in both
modes); SIMD lanes now run INSIDE the Path B chunks under mt8 (G25: threads and lanes
compose); double and float only (Int64 keeps the simd nest); the census line is kept under
`BLADE_OMP_THREADS=1`. The harness's fold-form evidence now reads `SIMD_REDUCTION/K-lane`
for the vector helpers, but under-reports the scalar K-lane tree as `in-order` (§14 header;
harness fix in progress, medians unaffected). The shipped default is K = 8 (unset → Auto →
8 in `CodeGenExprSupport.foldVecLanes`).
4e49f46a (round-2 fix): the K-lane tail never fuses a product into its add — every product
is rounded before the add, in all contraction modes (functions/168, Float32). Corpus
`loops/477-479`.

**Tests.** 134cec69: linalg dispatch x10, omp-reduce x4, omp-pragma x1 failures; checks 0
failures; ab_build 30 built / 0 '!!'. Follow-up 68d344af: run, no failure recorded in the
run log. Round 2 (merged tree): functions/168 failed; fix 4e49f46a: functions 324/0, loops
238/0, linalg 130/0, omp-reduce 85/0, omp-pragma 76/0, fold-lanes 4/0, fresh ab_build 30
built / 0 '!!'. Final-tree gates: §14 header (round 3, all green).

**Within-session A/B (item g; `BLADE_FOLD_LANES` default / off / 4 / 8, blade-cpp, ms).**
Serial: dot 24.04 / 24.53 / 25.49 / 25.02, stencil 16.85 / 17.42 / 17.66 / 17.80,
fusedchain 8.70 / 8.52 / 8.96 / 8.78; mt8: dot 10.40 / 10.68 / 11.77 / 11.07, stencil 8.33
/ 8.22 / 8.72 / 8.30, fusedchain 3.68 / 3.46 / 3.99 / 3.69 (fusedchain is
input-generation dominated). Default and 8 are the SAME algorithm (default K = 8), so their
differences are measurement variation, not a policy arm. `ab_time` (the corrected script's
output, `fold-ab_time-fixed.txt`; process-wall timings from a small diagnostic run, not
verdicts) is broadly neutral but NOT within 1–3% everywhere: off/K8 0.952 on mt8 stencil and
1.057 on mt8 onepass, off/K4 1.038 on mt8 fusedchain. Microbench, v4/simd and v8/simd as the microbench reports them: dot 3000017
0.93–1.04 / 0.94–1.00, gemv fiber 0.96–1.11 / 0.98–1.19, com2 201x10007 1.22–1.27 /
1.15–1.27, com3 61x2003 1.03–1.05 / 1.04–1.06, sq rows 1499x2001 1.01–1.64 / 1.20–1.32,
f32 dot 0.97–1.02 / 0.98–1.10. On the whole-program kernels the K-lane form is a wash
(within ±3% of lanes off in item g; no robust whole-program gain); the shapes that move
are the compute-bound microbench rows, which do not show K8 beating K4 everywhere. Default
K = 8 is kept.

| regime | kernel | before blade-llvm / blade-cpp (ms) | after blade-llvm / blade-cpp (ms) | gf-expert / ifx-expert (ms) | ratio before → after (gf / ifx) | verdict |
|---|---|---|---|---|---|---|
| serial x licensed (ref llvm; pre-fix §4) | dot | 13.20 / 19.28 | 13.49 / 24.56 | 18.44 / 12.67 → 19.1 / 13.04 | 1.397 * / 0.960 * → 1.416 [1.356, 1.511] * / 0.967 * | win (gf) / parity (ifx), indicative (133604) |
| serial x licensed | saxpy | 14.28 / 17.89 | 12.44 / 20.67 | 15.62 / 11.93 → 15.48 / 11.23 | 1.094 * / 0.836 * → 1.244 [1.194, 1.283] * / 0.903 * | win (gf) / loss (ifx), indicative (133604) |
| serial x licensed | fusedchain | 4.571 / 6.754 | 4.382 / 8.522 | 6.905 / 4.897 → 6.866 / 4.483 | 1.511 * / 1.071 * → 1.567 [1.530, 1.602] * / 1.023 * | win (gf) / parity (ifx), indicative (133604) |
| serial x licensed | onepass | 11.67 / 16.77 | 10.94 / 21.03 | 16.83 / 11.78 → 16.69 / 11.29 | 1.442 / 1.009 (live) → 1.525 [1.502, 1.534] / 1.031 | win (gf) / parity (ifx), live (133604) |
| mt8 x licensed (ref cpp; pre-fix §5) | dot | — / 12.00 | — / 11.94 | 10.53 / 10.96 → 11.48 / 11.49 | 0.878 * / 0.914 * → 0.962 [0.869, 1.018] * / 0.962 * | parity, indicative (135344) |
| mt8 x licensed | saxpy | — / 11.40 | — / 11.76 | 8.963 / 9.224 → 9.646 / 10.08 | 0.786 * / 0.809 * → 0.821 [0.792, 0.857] * / 0.858 * | loss, indicative (135344) |
| mt8 x licensed | fusedchain | — / 4.128 | — / 3.177 | 3.253 / 3.509 → 3.245 / 3.508 | 0.788 * / 0.850 * → 1.021 [0.955, 1.101] * / 1.104 * | parity (gf) / win (ifx), indicative (135344) |
| mt8 x licensed | stencil | — / 11.49 | — / 9.579 | 8.968 / 9.120 → 8.481 / 8.868 | 0.780 * / 0.794 * → 0.885 [0.828, 0.970] * / 0.926 * | loss, indicative (135344; shared with 14.3) |

**Incremental (total − input-only twin, ms; before = pre-fix §4/§5, after = bundles
133604 / 135344 `summary.md`).** The K-lane form touches the fold, not the generator, so
this is the quantity expected to move; the intervals are wide.

| regime | kernel | Blade ref | other lane | gf-expert / ifx-expert | incremental ratio gf / ifx |
|---|---|---|---|---|---|
| serial x licensed (ref llvm) | dot | 1.731 → 1.856 | cpp 1.571 → 1.465 | 1.125 / 1.569 → 1.293 / 1.672 | 0.650 / 0.906 → 0.697 [0.394, 1.607] / 0.901 |
| serial x licensed | saxpy | 6.083 → 4.887 | cpp 5.777 → 5.038 | 3.622 / 4.293 → 3.502 / 3.626 | 0.595 / 0.706 → 0.716 [0.619, 0.831] / 0.742 |
| serial x licensed | fusedchain | 0.625 → 0.861 | cpp 0.670 → 0.756 | 0.725 / 1.107 → 0.740 / 0.750 | 1.159 / 1.769 → 0.860 [0.731, 1.199] / 0.872 |
| serial x licensed | onepass | 2.178 → 1.791 | cpp 1.941 → 1.680 | 1.593 / 1.928 → 1.817 / 1.787 | 0.732 / 0.885 → 1.015 [0.910, 1.079] / 0.998 |
| mt8 x licensed (ref cpp) | dot | 1.677 → 2.157 | — | 2.011 / 1.940 → 2.154 / 1.733 | 1.200 / 1.157 → 0.999 [0.550, 1.619] / 0.804 |
| mt8 x licensed | saxpy | 4.429 → 4.793 | — | 3.402 / 3.205 → 3.185 / 3.311 | 0.768 / 0.724 → 0.665 [0.543, 0.910] / 0.691 |
| mt8 x licensed | fusedchain | 0.611 → 0.294 | — | 0.359 / 0.329 → 0.536 / 0.233 | 0.588 / 0.539 → 1.823 [1.046, 3.346] / 0.792 |

The blade-cpp serial **totals** rose against pre-fix (dot 19.28 → 24.56, saxpy 17.89 →
20.67, fusedchain 6.754 → 8.522, onepass 16.77 → 21.03 ms) while its **increments** fell
or held (dot 1.571 → 1.465, onepass 1.941 → 1.680 ms): the rise is in the input-only twin
(dot 17.71 → 23.09 ms; the blade-llvm twin 11.47 → 11.63), not in the fold this branch
changed. Its cause is band-vec b08a9aef's unconditional split Int64→Float64 conversion in
the serial generator loop, not fold-simd — G37 (§14.8).

**Register outcome (measured, round 3):** G25 FIXED as a form, no measured gain — lanes
inside the Path B chunks are emitted, and mt8 lanes on / off is within noise in-session;
mt8 dot 0.88 * / 0.91 * → 0.96 * / 0.96 *, saxpy 0.79 * / 0.81 * → 0.82 * / 0.86 *. G26
PARTLY — the K-lane form is emitted and is a wash on the memory-bound dot-class kernels;
the C++ lane's deficit against blade-llvm on them is not in the fold (G37).

### 14.7 recur (branch f36d5503 + 9ebc530f / 9d03e588, merged) — G8 / M10–M11, S13

**What changed.** The inductive arm's elementwise chain is fused and written in place into
the trajectory row (slice-write fusion under `BLADE_FUSION`): the per-step temporaries and
the per-step copy into the 48 MB trajectory — the "remaining 2x" of §13.3 — are gone where
the pass applies. The branch session's own numbers (NOT results): licensed blade-llvm 22.5
→ 12.4 ms, blade-cpp 24.3 → 15.7, repro ~27 → 17.3, experts ~10.

**Late addition (S13 row-fold fusion, 9ebc530f merged through 7a601446; fix 9d03e588).** A
fold over a `let rec`'s rows is absorbed into the recursion (gate `BLADE_FUSION`; plan rule
`row-fold-fusion`), so recur's checksum no longer re-walks the trajectory after the loop; a
while-guarded recursion, which rewrites rows after the loop, records the decline
(`decision_row_fold_declined_while`). Corpus `recursive-arrays/026-029`.

**Tests.** f36d5503: run, no failure recorded in the run log. S13 was first tested in round
2 on the merged tree: `decision_row_fold_declined_while` failed (S13 did not recognise the
small-buffer `IRArrayLit` zero fill); fix 9d03e588 (TEST RESULT recur 9d03e588, Blade.dll
437E49E3…): optimize 152/0, recursive-arrays 28/0/0, interp 29/0, opt-diff 26/0/2.
Final-tree gates: §14 header (round 3, all green).

**Within-session A/B (item e, 11 rounds; NEW / `BLADE_FUSION=0` / OLD, ms).**
Serial-licensed blade-llvm 9.62 / 9.69, 40.1 / 40.6, 20.9 / 21.8; blade-cpp 11.47 / 11.41,
40.3 / 40.3, 22.4 / 22.5 (gf-expert ~10.0, ifx-expert ~10.6). Serial-repro blade-llvm
13.62 / 13.85, 42.8 / 42.6, 24.8 / 24.5; blade-cpp 15.63 / 15.38, 43.0 / 43.3, 24.6 / 24.3
(gf 13.65, ifx 14.0). NEW is 2.2x / 1.8x faster than OLD in the LLVM lane (licensed /
repro), 2.0x / 1.6x in the C++ lane. Caveat:
`BLADE_FUSION=0` also disables master's elementwise fusion, so the FUSION=0 arm is not a
like-for-like regression signal against OLD. Page faults ~12.9–13.7k in every arm (a
whole-process figure; not diagnostic).

| regime | kernel | before blade-llvm / blade-cpp (ms) | after blade-llvm / blade-cpp (ms) | gf-expert / ifx-expert (ms) | ratio before → after (gf / ifx) | verdict |
|---|---|---|---|---|---|---|
| serial x licensed (ref llvm; §13 session 201558) | recur | 20.43 / 22.49 | 9.159 / 11.27 | 10.07 / 9.995 → 10.2 / 10.3 | 0.493 * / 0.489 * → 1.114 [1.029, 1.159] * / 1.125 * | win, indicative (132026) |
| serial x repro (ref llvm; §13 session 204141) | recur | 23.77 / 23.85 | 13.83 / 15.74 | 12.95 / 13.34 → 13.63 / 14.07 | 0.545 / 0.561 (live) → 0.985 [0.964, 1.003] * / 1.017 * | parity, indicative (142502) |
| mt8 x licensed | recur | no Blade arm (G5, unchanged) | — | 6.815 / 7.486 → 7.077 / 7.951 | — | — (135344) |
| mt8 x repro | recur | no Blade arm (G5, unchanged) | — | 7.407 / 7.795 → 8.005 / 8.117 | — | — (145930) |

**Register outcome (measured, round 3):** G8 FIXED (measured) for the `let rec` step —
recur serial licensed 0.49 * / 0.49 * → 1.11 * / 1.13 *, repro 0.55 / 0.56 → 0.99 * /
1.02 *; in-session NEW 9.6–9.7 vs OLD 20.9–21.8 ms (blade-llvm). The lifted-let half of G8
(fusedchain, saxpy) is not this branch's. G5 (no thread form inside a `let rec` step) is
untouched: recur still has no Blade mt8 arm.

### 14.8 Headline of the follow-up round (round 3, final tree d3d5722b)

**Serial, licensed default (`blade-llvm`): Blade beats both reviewed experts on gram
(1.35 * / 1.23 *), antisym (1.11 / 1.08), comoment3 (1.33 / 1.34), pairkernel (1.12 /
1.05), recur (1.11 * / 1.13 *, from 0.49), band (1.96 / 2.51, from 0.25 / 0.31) and
stencil (1.38 * / 1.09 *); it beats gfortran-expert and matches ifx-expert on fusedchain
(1.57 * / 1.02 *), onepass (1.53 / 1.03) and dot (1.42 * / 0.97 *); saxpy wins against
gfortran and loses to ifx (1.24 * / 0.90 *).** The remaining serial-licensed loss to both
experts is **maskagg (0.87 * / 0.63 *)**, now 14x faster than its pre-fix C++ figure (67.2
→ 4.68 ms) but still behind the experts' pack (incremental, total minus input-only twin,
0.38 / 0.39). Fastmath keeps the same shape (gram 1.33 * / 1.23
*, antisym 1.13 / 1.11, comoment3 1.34 * / 1.33 *, band 3.96 / 3.10; fusedchain / onepass /
dot 1.50 * / 1.50 * / 1.37 against gfortran). The reproducible build holds gram (1.27 /
1.16) and antisym (1.05 / 1.01), reaches parity on recur (0.99 * / 1.02 *) and, against
ifx, on band (1.01 *; 0.84 * against gfortran), and wins against gfortran on fusedchain,
onepass, dot, saxpy and stencil (1.38 *, 1.35, 1.22, 1.08 *, 1.22 *). Under threads (`blade-cpp`, licensed): gram
1.39 * / 1.33 *, antisym 1.18 * / 1.18 *, comoment3 1.66 * / 1.37 *, band 0.97 * / 1.66 *,
fusedchain 1.02 * / 1.10 *, dot 0.96 * / 0.96 *; blas gram 1.09 / 1.04 serial and **1.19 /
1.25 mt8 (live; the native tile preempting `?syrk`, D47)**.

**What moved since §13 (headline cells):** band 0.25 / 0.31 → 1.96 / 2.51 (G30, G33);
recur 0.49 → 1.11 * (G8, S13); maskagg 0.06 → 0.87 * (G9); blas mt8 gram 0.79 / 0.82 →
1.19 / 1.25 (G28 + 37d97506); serial-repro comoment3 0.44 → 0.77 (G34); comoment3 serial
licensed 1.22 → 1.33. Unchanged within drift: gram, antisym, pairkernel serial; pairkernel
mt8 (G32 did not move it).

**The losses that remain, plainly:**

- **mt8 pairkernel:** 0.80 * / 0.80 * licensed, 0.37 / 0.36 repro (live). G32's threaded
  orbit pass is in place and measured no gain (in-session NEW ≈ OLD).
- **maskagg (serial):** 0.871 * / 0.628 * licensed, 0.839 * / 0.676 * repro — the
  compaction pipeline itself (incremental ~3.0 ms against the experts' ~1.2 ms pack).
- **Reproducible threads (mt8 x repro):** comoment3 0.48 / 0.43 (live), pairkernel 0.37 /
  0.36 (live), band 0.30 * / 0.52 *, saxpy 0.77 * / 0.75 *, stencil 0.82 * / 0.89 *, dot
  0.83 * / 0.84 *, fusedchain 0.75 * / 0.83 *. **Blade's reproducible threaded folds are
  serial by design: one ordered chain per fold.** The legality boundary for a follow-up
  (G35): evaluating independent COMPLETE per-cell or per-row inner folds in parallel into
  indexed slots, then running the original outer chain serially in ascending order with its
  seed, is repro-safe — the shape the tiles already have (gram 1.12 * / 1.04 *, antisym
  0.99 / 1.00 here), and the one stencil's hot-row path already uses in mt8 repro (its row
  folds run inside the producer's team). Chunking ONE global reduction across threads is not
  repro-safe even with a fixed partition. That can help pairkernel and comoment3 (their
  logical checksum rows), not band / dot / saxpy / fusedchain, whose checksum is one global
  chain. The experts' repro arms use OpenMP `reduction(+:…)` over global sums (and
  multiplicity-weighted packed checksums on comoment3 / pairkernel): they are not
  byte-identity equivalents of Blade's repro contract, so these cells compare different
  reproducibility constraints.
- **Serial repro:** comoment3 0.77 / 0.78 and pairkernel 0.90 / 0.84 (live) — both narrowed
  (from 0.44 and 0.84 / 0.78), both lanes now level; band 0.844 * against gfortran (parity
  with ifx, 1.008 *).
- **ifx-expert on the memory-bound group B in repro and fastmath:** fusedchain / onepass /
  dot / saxpy 0.84 * / 0.82 / 0.76 / 0.69 * (repro), fusedchain / onepass / dot 0.79 * /
  0.72 * / 0.71 (fastmath) — 0.69–0.84. The fusion is assembly-confirmed on the round-3
  ifx executables (Codex audit §4): in repro (144145) dot, onepass and saxpy update the
  kernel and the input-checksum chains in one loop, and fusedchain uses 64-element block
  scheduling (input sum, vector terms into a buffer, ordered consume); in fastmath (153107)
  dot, onepass and fusedchain also fuse the input GENERATOR into that loop, which is why
  ifx's fastmath arm speeds up sharply (dot 13.0 ms licensed → 9.9 ms fastmath). ifx's repro
  flags still permit contraction (`/fp:precise -mllvm -fp-contract=fast`). Blade's licensed
  and fastmath configurations are intentionally identical (`bench/harness/config.json`,
  `blade_knobs.fastmath`: same knobs, fastmath differs on the Fortran side only), so
  blade-llvm not moving (13.5 → 13.9 ms) is policy, not a failed switch. Incremental (total
  minus input-only twin), ifx-expert's repro dot is 0.59 ms against blade-llvm's 2.82 ms —
  a difference of medians, not an isolated kernel time, since the fused program schedules
  the work differently (G36). In the licensed default the same kernels are at parity with
  ifx except saxpy (0.90 *).
- **Threads, licensed:** saxpy 0.82 * / 0.86 * and stencil 0.89 * / 0.93 * (mt8); mt8
  fastmath dot 0.91 * against gfortran (0.99 * against ifx, parity); mt8 fastmath
  fusedchain 0.91 * / 0.93 * has intervals straddling 1 (indicative parity), as do mt8
  licensed dot and band against gfortran.
- **blade-cpp trails blade-llvm 1.5–1.9x on the memory-bound kernels** (serial licensed dot
  24.56 vs 13.49 ms, fusedchain 8.522 vs 4.382, onepass 21.03 vs 10.94, saxpy 20.67 vs
  12.44, stencil 17.25 vs 10.71, maskagg 7.054 vs 4.68, band 0.6335 vs 0.4158; 1.4–1.6x in
  repro). For dot, saxpy, fusedchain, onepass and maskagg the gap is in the input-only twin,
  not in the incremental (dot twin 23.09 vs 11.63 ms; incremental 1.465 vs 1.856 ms); stencil
  and band were not part of that twin diagnosis, so their lane gap is not explained by it.
  The serial blade-cpp twins are 28–34% slower than in the pre-fix sessions while the
  blade-llvm twins and the mt8 blade-cpp twins are flat (dot 17.71 → 23.09 vs 11.47 → 11.63
  and 10.32 → 9.78 ms). **Cause identified** (Codex audit §1, on the emitted
  `dot/inputonly.blade` and `saxpy/inputonly.blade` twins): band-vec b08a9aef's
  unconditional replacement of `static_cast<double>` on Int64 by the split conversion
  `blade_arith::i64_to_f64` in the serial generator loop — not fold-simd. The emitted twins
  fall into two byte-identical groups (OLD = g32-g34 = maskagg = tri-tiles = fold-simd; NEW
  = band-vec = blas-repack = recur, both carrying the band change), differing only in that
  cast and a removed frame; a diagnostic single-run A/B (5 interleaved runs, no protocol
  controls) gives NEW / OLD +27.3% dot, +27.8% saxpy, and restoring ONLY that cast recovers
  it (dot 17.99 vs OLD 18.03 ms). A context-aware fix (exact conversion where the range is
  proven, the direct scalar cast elsewhere, the split only where packed vectorization
  benefits, keeping band's gain) is in progress; the twins, maskagg (G38) and band will be
  re-measured. The round-3 numbers above are published unchanged (G37).
- **No Blade mt8 arm:** onepass (G7: a join has no thread-level form) and recur (G5: no
  thread form inside a `let rec` step); the experts thread them (onepass 9.0 / 9.5 ms, recur
  7.1 / 8.0 ms licensed). maskagg is serial by protocol.

All of it uncapped (Silent scheme, 100%), 40 of 62 blocks indicative; the within-session
A/Bs of 14.1–14.7 are the evidence for each change. The pre-round headline is §13.4; the
pre-fix headline §11.1. Gate totals on the measured tree: §14 header.

**Final table (round 3, every regime; copied from the runner record, RESULTS
convention).** Ratio = expert median / Blade median, > 1 Blade faster; `§13 gf / ifx` =
the §13.1 ratio for the same cell (group A and band; group B was not re-run in §13);
"verdict" = live or indicative (the block's drift check).

**serial x licensed (ref blade-llvm; sessions 132026 A, 133604 B)**

| kernel | Blade ref (ms) | other lane (ms) | gf-expert / ifx-expert (ms) | ratio gf [95% CI] | ratio ifx [95% CI] | §13 gf / ifx | verdict | block |
|---|---|---|---|---|---|---|---|---|
| gram | 58.11 | cpp 61.88 | 78.53 / 71.62 | 1.351 [1.327, 1.386] * | 1.233 [1.207, 1.260] * | 1.368/1.236 | indicative | 132026 n=11 (suppressed) |
| antisym | 28.01 | cpp 29.97 | 31.19 / 30.1 | 1.113 [1.099, 1.152] | 1.075 [1.052, 1.115] | 1.126/1.073 | live | 132026 n=21 |
| comoment3 | 8.726 | cpp 8.945 | 11.56 / 11.68 | 1.325 [1.310, 1.335] | 1.338 [1.322, 1.377] | 1.221/1.225 | live | 132026 n=21 |
| pairkernel | 29.15 | cpp 29.52 | 32.7 / 30.52 | 1.122 [1.114, 1.128] | 1.047 [1.039, 1.056] | 1.121/1.043 | live | 132026 n=21 |
| recur | 9.159 | cpp 11.27 | 10.2 / 10.3 | 1.114 [1.029, 1.159] * | 1.125 [1.028, 1.175] * | 0.493/0.489 | indicative | 132026 n=21 (suppressed) |
| band | 0.4158 | cpp 0.6335 | 0.8159 / 1.044 | 1.962 [1.908, 1.995] | 2.511 [2.441, 2.552] | 0.245/0.314 | live | 132026 n=21 |
| fusedchain | 4.382 | cpp 8.522 | 6.866 / 4.483 | 1.567 [1.530, 1.602] * | 1.023 [0.997, 1.054] * | — | indicative | 133604 n=21 (suppressed) |
| maskagg | 4.68 | cpp 7.054 | 4.074 / 2.939 | 0.871 [0.835, 0.891] * | 0.628 [0.608, 0.649] * | — | indicative | 133604 n=21 (suppressed) |
| onepass | 10.94 | cpp 21.03 | 16.69 / 11.29 | 1.525 [1.502, 1.534] | 1.031 [1.017, 1.040] | — | live | 133604 n=21 |
| dot | 13.49 | cpp 24.56 | 19.1 / 13.04 | 1.416 [1.356, 1.511] * | 0.967 [0.908, 1.050] * | — | indicative | 133604 n=21 (suppressed) |
| saxpy | 12.44 | cpp 20.67 | 15.48 / 11.23 | 1.244 [1.194, 1.283] * | 0.903 [0.858, 0.951] * | — | indicative | 133604 n=21 (suppressed) |
| stencil | 10.71 | cpp 17.25 | 14.72 / 11.65 | 1.375 [1.302, 1.404] * | 1.089 [1.019, 1.115] * | — | indicative | 132026 n=21 (suppressed) |

**mt8 x licensed (ref blade-cpp; session 135344)**

| kernel | Blade ref (ms) | other lane (ms) | gf-expert / ifx-expert (ms) | ratio gf [95% CI] | ratio ifx [95% CI] | §13 gf / ifx | verdict | block |
|---|---|---|---|---|---|---|---|---|
| gram | 13.96 | llvm serial-only | 19.44 / 18.59 | 1.393 [1.309, 1.441] * | 1.332 [1.238, 1.361] * | 1.276/1.223 | indicative | 135344 n=21 (suppressed) |
| antisym | 9.026 | llvm serial-only | 10.65 / 10.65 | 1.179 [1.139, 1.268] * | 1.180 [1.128, 1.246] * | 1.081/1.117 | indicative | 135344 n=21 (suppressed) |
| comoment3 | 2.973 | llvm serial-only | 4.932 / 4.082 | 1.659 [1.598, 1.716] * | 1.373 [1.299, 1.406] * | 1.462/1.217 | indicative | 135344 n=21 (suppressed) |
| pairkernel | 7.541 | llvm serial-only | 6.032 / 6.014 | 0.800 [0.768, 0.829] * | 0.798 [0.770, 0.816] * | 0.817/0.786 | indicative | 135344 n=21 (suppressed) |
| recur | — | blade-cpp no-parallel-form; blade-llvm serial-only | 7.077 / 7.951 | — | — | — | no Blade arm | 135344 n=21 |
| band | 1.07 | llvm serial-only | 1.04 / 1.78 | 0.972 [0.844, 1.053] * | 1.663 [1.503, 1.803] * | — | indicative | 135344 n=21 (suppressed) |
| fusedchain | 3.177 | llvm serial-only | 3.245 / 3.508 | 1.021 [0.955, 1.101] * | 1.104 [1.063, 1.151] * | — | indicative | 135344 n=21 (suppressed) |
| onepass | — | blade-cpp no-parallel-form; blade-llvm serial-only | 9.014 / 9.522 | — | — | — | no Blade arm | 135344 n=21 |
| dot | 11.94 | llvm serial-only | 11.48 / 11.49 | 0.962 [0.869, 1.018] * | 0.962 [0.908, 1.067] * | — | indicative | 135344 n=21 (suppressed) |
| saxpy | 11.76 | llvm serial-only | 9.646 / 10.08 | 0.821 [0.792, 0.857] * | 0.858 [0.814, 0.897] * | — | indicative | 135344 n=21 (suppressed) |
| stencil | 9.579 | llvm serial-only | 8.481 / 8.868 | 0.885 [0.828, 0.970] * | 0.926 [0.865, 0.998] * | — | indicative | 135344 n=21 (suppressed) |

**serial x repro (ref blade-llvm; sessions 142502 A, 144145 B)**

| kernel | Blade ref (ms) | other lane (ms) | gf-expert / ifx-expert (ms) | ratio gf [95% CI] | ratio ifx [95% CI] | §13 gf / ifx | verdict | block |
|---|---|---|---|---|---|---|---|---|
| gram | 62.89 | cpp 65.38 | 79.77 / 72.88 | 1.268 [1.221, 1.310] | 1.159 [1.116, 1.186] | 1.290/1.174 | live | 142502 n=11 |
| antisym | 31.12 | cpp 31.62 | 32.5 / 31.28 | 1.045 [1.020, 1.079] | 1.005 [0.989, 1.056] | 1.043/1.000 | live | 142502 n=21 |
| comoment3 | 15.3 | cpp 15.54 | 11.82 / 11.93 | 0.773 [0.754, 0.783] | 0.780 [0.765, 0.792] | 0.443/0.445 | live | 142502 n=21 |
| pairkernel | 38.22 | cpp 38.64 | 34.5 / 32.11 | 0.903 [0.898, 0.913] | 0.840 [0.833, 0.849] | 0.841/0.782 | live | 142502 n=21 |
| recur | 13.83 | cpp 15.74 | 13.63 / 14.07 | 0.985 [0.964, 1.003] * | 1.017 [1.008, 1.033] * | 0.545/0.561 | indicative | 142502 n=21 (suppressed) |
| band | 3.685 | cpp 3.959 | 3.111 / 3.716 | 0.844 [0.843, 0.847] * | 1.008 [1.007, 1.013] * | 0.335/0.401 | indicative | 142502 n=21 (suppressed) |
| fusedchain | 6.227 | cpp 10.23 | 8.6 / 5.227 | 1.381 [1.321, 1.418] * | 0.839 [0.783, 0.866] * | — | indicative | 144145 n=21 (suppressed) |
| maskagg | 5.942 | cpp 8.265 | 4.982 / 4.02 | 0.839 [0.806, 0.895] * | 0.676 [0.637, 0.722] * | — | indicative | 144145 n=21 (suppressed) |
| onepass | 16.36 | cpp 26.31 | 22.16 / 13.42 | 1.354 [1.332, 1.365] | 0.820 [0.803, 0.847] | — | live | 144145 n=21 |
| dot | 17.33 | cpp 28.49 | 21.13 / 13.08 | 1.219 [1.207, 1.242] | 0.755 [0.737, 0.770] | — | live | 144145 n=21 |
| saxpy | 15.2 | cpp 23 | 16.44 / 10.43 | 1.082 [1.043, 1.112] * | 0.686 [0.668, 0.706] * | — | indicative | 144145 n=21 (suppressed) |
| stencil | 14.28 | cpp 21.02 | 17.48 / 14.29 | 1.224 [1.210, 1.238] * | 1.001 [0.985, 1.023] * | — | indicative | 142502 n=21 (suppressed) |

**mt8 x repro (ref blade-cpp; session 145930)**

| kernel | Blade ref (ms) | other lane (ms) | gf-expert / ifx-expert (ms) | ratio gf [95% CI] | ratio ifx [95% CI] | §13 gf / ifx | verdict | block |
|---|---|---|---|---|---|---|---|---|
| gram | 22.03 | llvm serial-only | 24.64 / 22.83 | 1.119 [1.070, 1.161] * | 1.036 [0.989, 1.058] * | 1.080/1.035 | indicative | 145930 n=21 (suppressed) |
| antisym | 11.22 | llvm serial-only | 11.15 / 11.22 | 0.993 [0.959, 1.049] | 1.000 [0.951, 1.049] | 0.929/0.929 | live | 145930 n=21 |
| comoment3 | 10.82 | llvm serial-only | 5.171 / 4.615 | 0.478 [0.464, 0.525] | 0.427 [0.412, 0.446] | 0.490/0.414 | live | 145930 n=21 |
| pairkernel | 18.16 | llvm serial-only | 6.72 / 6.515 | 0.370 [0.354, 0.381] | 0.359 [0.348, 0.365] | 0.359/0.373 | live | 145930 n=21 |
| recur | — | blade-cpp no-parallel-form; blade-llvm serial-only | 8.005 / 8.117 | — | — | — | no Blade arm | 145930 n=21 (suppressed) |
| band | 4.678 | llvm serial-only | 1.411 / 2.431 | 0.302 [0.276, 0.331] * | 0.520 [0.504, 0.541] * | — | indicative | 145930 n=21 (suppressed) |
| fusedchain | 5.856 | llvm serial-only | 4.407 / 4.828 | 0.753 [0.701, 0.827] * | 0.825 [0.776, 0.918] * | — | indicative | 145930 n=21 (suppressed) |
| onepass | — | blade-cpp no-parallel-form; blade-llvm serial-only | 11.89 / 11.91 | — | — | — | no Blade arm | 145930 n=21 (suppressed) |
| dot | 14.82 | llvm serial-only | 12.33 / 12.48 | 0.832 [0.768, 0.851] * | 0.842 [0.805, 0.874] * | — | indicative | 145930 n=21 (suppressed) |
| saxpy | 13.53 | llvm serial-only | 10.35 / 10.13 | 0.765 [0.718, 0.817] * | 0.748 [0.725, 0.834] * | — | indicative | 145930 n=21 (suppressed) |
| stencil | 10.43 | llvm serial-only | 8.492 / 9.284 | 0.815 [0.738, 0.864] * | 0.891 [0.854, 0.918] * | — | indicative | 145930 n=21 (suppressed) |

**serial x fastmath (ref blade-llvm; session 153107)**

| kernel | Blade ref (ms) | other lane (ms) | gf-expert / ifx-expert (ms) | ratio gf [95% CI] | ratio ifx [95% CI] | §13 gf / ifx | verdict | block |
|---|---|---|---|---|---|---|---|---|
| gram | 58.85 | cpp 62.68 | 78.46 / 72.46 | 1.333 [1.289, 1.355] * | 1.231 [1.192, 1.251] * | 1.354/1.235 | indicative | 153107 n=11 (suppressed) |
| antisym | 28.2 | cpp 30.1 | 31.73 / 31.19 | 1.125 [1.106, 1.134] | 1.106 [1.074, 1.112] | 1.142/1.097 | live | 153107 n=21 |
| comoment3 | 8.768 | cpp 9.036 | 11.74 / 11.7 | 1.339 [1.312, 1.347] * | 1.334 [1.312, 1.350] * | 1.245/1.226 | indicative | 153107 n=21 (suppressed) |
| band | 0.4278 | cpp 0.6455 | 1.695 / 1.325 | 3.961 [3.638, 4.069] | 3.098 [2.988, 3.210] | 0.513/0.399 | live | 153107 n=21 |
| fusedchain | 4.895 | cpp 8.655 | 7.324 / 3.857 | 1.496 [1.374, 1.688] * | 0.788 [0.705, 0.872] * | — | indicative | 153107 n=21 (suppressed) |
| onepass | 12.07 | cpp 22.12 | 18.12 / 8.728 | 1.502 [1.443, 1.524] * | 0.723 [0.689, 0.752] * | — | indicative | 153107 n=21 (suppressed) |
| dot | 13.9 | cpp 24.96 | 19.02 / 9.888 | 1.369 [1.348, 1.450] | 0.712 [0.671, 0.737] | — | live | 153107 n=21 |

**mt8 x fastmath (ref blade-cpp; session 155001)**

| kernel | Blade ref (ms) | other lane (ms) | gf-expert / ifx-expert (ms) | ratio gf [95% CI] | ratio ifx [95% CI] | §13 gf / ifx | verdict | block |
|---|---|---|---|---|---|---|---|---|
| gram | 20.06 | llvm serial-only | 25.05 / 24.48 | 1.249 [1.198, 1.338] * | 1.220 [1.184, 1.307] * | 1.232/1.166 | indicative | 155001 n=21 (suppressed) |
| antisym | 9.57 | llvm serial-only | 11.33 / 11.62 | 1.184 [1.145, 1.231] * | 1.214 [1.161, 1.260] * | 1.167/1.150 | indicative | 155001 n=21 (suppressed) |
| comoment3 | 3.573 | llvm serial-only | 5.275 / 4.561 | 1.477 [1.428, 1.556] | 1.277 [1.225, 1.354] | 1.441/1.217 | live | 155001 n=21 |
| band | 1.112 | llvm serial-only | 1.161 / 1.97 | 1.044 [0.971, 1.149] * | 1.773 [1.643, 1.847] * | — | indicative | 155001 n=21 (suppressed) |
| fusedchain | 3.686 | llvm serial-only | 3.35 / 3.422 | 0.909 [0.822, 1.073] * | 0.928 [0.835, 1.147] * | — | indicative | 155001 n=21 (suppressed) |
| onepass | — | blade-cpp no-parallel-form; blade-llvm serial-only | 9.43 / 9.602 | — | — | — | no Blade arm | 155001 n=21 (suppressed) |
| dot | 11.8 | llvm serial-only | 10.75 / 11.72 | 0.911 [0.883, 0.978] * | 0.992 [0.954, 1.063] * | — | indicative | 155001 n=21 (suppressed) |

**blas x serial (ref blade-cpp, `blas_route: native-preempt`; session 161102)**

| kernel | Blade ref (ms) | other lane (ms) | gf-expert / ifx-expert (ms) | ratio gf [95% CI] | ratio ifx [95% CI] | §13 gf / ifx | verdict | block |
|---|---|---|---|---|---|---|---|---|
| gram | 61.84 | llvm no-blas | 67.37 / 64.43 | 1.089 [1.074, 1.126] | 1.042 [1.031, 1.063] | 1.013/0.968 | live | 161102 n=11 |

**blas x mt8 (ref blade-cpp, `blas_route: native-preempt`; session 161102)**

| kernel | Blade ref (ms) | other lane (ms) | gf-expert / ifx-expert (ms) | ratio gf [95% CI] | ratio ifx [95% CI] | §13 gf / ifx | verdict | block |
|---|---|---|---|---|---|---|---|---|
| gram | 17.97 | llvm no-blas | 21.45 / 22.44 | 1.194 [1.144, 1.346] | 1.249 [1.204, 1.341] | 0.788/0.817 | live | 161102 n=21 |

## 15. Round 2 (2026-10-19/20) -- the second microkernel round

Plan and record: `bench/notes/round2-microkernel-plan.md`, `bench/notes/round2/`. Baseline
tree master 4136f0b5 (= round-3 tree d3d5722b plus the G37 conversion fix, BLADE_BLAS_PREFER
and harness fixes); baseline gate TOTAL 6800 passed, 0 failed, 1 skipped. Integration branch
`worktree-bench-fortran`.

### 15.1 What landed (wave 1, integrated at 6b3e5293; five gates green)

| item | mechanism | knob | gate / proof |
|---|---|---|---|
| G35 row slots (W1-A) | under `BLADE_FP_REPRO=1` with threads and an `omp` request, each thread folds COMPLETE logical rows into indexed slots; the outer chain runs serially from its seed (no chain split) | `BLADE_FOLD_ROW_SLOTS`, `rowslots_min` (provisional 2^18) | omp-reduce identity T=1/4/7 vs serial; closed admission whitelist incl. the OUTER fold kernel (Codex blocker) |
| Cell epilogues (W1-B) | P1 streaming lane state (same tree as `sq<T,8>`), B1 saxpy rank-1 producer epilogue, B2r register-consume chain interleave for recur/stencil repro | `BLADE_CELL_EPILOGUE` (optimizer gate), `epi_blk`=256, `epi_blk_r`=0 | `blade test cell-epilogue` 56 raw-bit cases (NEW vs gate-off, non-dyadic n=2003, actual teams asserted); gate-off C++ byte-identical to OLD |
| maskagg compaction fusion (W1-C B4) | 6 passes -> 2: count in the mask pass; fused branchless fill+gather with an m+1 slack cell; no `y*y` pool; repro chain fed per 256-cell block; positions always built; `keep` stored | `BLADE_COMPACTION_FUSION` | index-types 515-521; repro raw bits OLD == NEW; licensed 2.8e-16 rel |
| pairkernel compact row classes (W1-C B3) | per-row orbit-class partials written behind each packed row inside the producer's team; combined in row order; no second 25 MB pass; licensed only | `BLADE_COMPACT_ROW_CLASSES` | bits identical at T=1/3/8 (OLD varied with T); symmetry/129-131; LLVM runtime gate at p=2503 within 1e-12 |
| Sibling-fold sharing (W1-D) | `shareSiblingFoldsModule` rewrites sibling scalar folds over one dense axis into the existing `<&!>` join; one-level inliner; unseeded legs seeded with the exact identity (-0.0; formalism 2.4 paragraph, fable ruling) | `BLADE_SIBLING_FOLDS` (optimizer gate) | sibling-folds/001-018; NEW == OLD under REPRO+off both lanes; drop test MEASURED on OLD: hand-joined llvm repro dot 1.22x, onepass 1.27x, licensed flat |

### 15.2 What landed (wave 2; final tip f1910060; six gate runs green, final: default 7180 / opt-diff 2072 / docs 131 / llvm 814 / interp 3013, 0 failed)

| item | mechanism | knob | gate / proof |
|---|---|---|---|
| antisym row groups + simplex edge kloops (W2-D) | syrk's row-group pattern in `dantisym_tri` / `blade_dantisym_tri_f64` (visit order only; bytes identical for every G); live-accumulator edge variants with the corrected per-half formula (dead vector halves 7.3% at p=171) | `BLADE_TILE_TUNE anti_g` (default 1; `0` = cache-budget auto), `tri_edge` (default 1) | row-tile 24, syrk-tile, llvm tiles with a positive route assertion; single-chain edge variants run two chains to keep per-step contraction |
| G7 thread-level `<&!>` join (W2-E) | generalized Path B: one team, per-leg identity-initialised private accumulators, one simd reduction per chunk, fixed ascending combine; fires only on an EXPLICIT `where omp` request + per-leg licence (fable ruling Q2); `onepass.blade` gains the request | `BLADE_JOIN_THREADS` | omp-reduce g7 cases incl. observed teams and OMP_THREAD_LIMIT; onepass has an mt8 arm; dot/fusedchain join+thread in the default build |
| Ordered orbit scatter (W2-A) | strict consumer of a stored SymIdx pool: one pass, every original chain fed in source order (G-row-group vectorizable form); rank 3 as slices + same-Z third event + retained H; both lanes | `BLADE_ORBIT_SCATTER`, `scatter_g`=4, `scatter_b`=64, `scatter_vrow` | `blade test orbit-scatter` (52 cases x 8 builds, walk-forced twins, mutation-checked); prototype MEASURED S/W 0.13 rank 2, 0.07-0.10 rank 3 |
| Pre-existing bug fixes (W2-F) | G41 a fold whose kernel or map body WRITES is never reorder-licensed (the serial K lanes AND Path B were wrong: five mt8 totals for one program); storage-provenance write analysis (a write is local only when its storage is proved fresh; rebinding joins the initializer with every later assignment; deferred wrappers chased; an unresolved callee counts as a write); every LLVM fold entry consults `FoldOperandWrites`; G42 inline projections of a flat `<&!>` join tuple (and its aliases) use flat leaf positions; G43 a join leg that writes is refused BL4005 (an acceptance-policy change; `joinEffectAudit` gates the serial simd form and the G7 team alike); G45 opened (scope-dependent evaluation count of a named writing map) | -- | loops/492-498, tuples/031-033, mutability-errors/014-024, omp-pragma / omp-reduce g41 runtime cases (T=1 and five T=8 runs), llvm goldens emission pins |

### 15.3 Measured: wave-1 A/B (OLD master 4136f0b5 vs NEW integration 6b3e5293)

Harness protocol (`bench/notes/round2/w1-ab.md`; bundles under `C:\bt_r2ab\{old,new}\bench\results\
2026-10-20-LAPTOP-350H6LDB\`): 20 sessions, 05:13-08:15, OLD and NEW interleaved per kernel, 21
rounds, full size, quiet box (3-5% load, AC, Silent, uncapped), harness drift controls within
0.95-1.04, zero failures/aborts/excursions. Ratio = expert median / Blade reference median
(> 1 Blade faster; ref blade-llvm serial, blade-cpp mt8); "ind" = the harness suppressed the
block (indicative). NEW/OLD = Blade NEW median / OLD median (< 1 NEW faster), unpaired
bootstrap CI.

| cell | item | OLD gf / ifx | NEW gf / ifx | RESULTS 14.8 | NEW/OLD (ref lane) | block |
|---|---|---|---|---|---|---|
| comoment3 mt8 repro | G35 | 0.497 / 0.436 | **1.080 / 0.950** | 0.478 / 0.427 | 0.458 [0.443, 0.472] | live |
| pairkernel mt8 repro | G35 | 0.383 / 0.372 | **0.687 / 0.678** | 0.370 / 0.359 | 0.546 [0.534, 0.563] | live (OLD ind) |
| pairkernel mt8 licensed | B3 | 0.797 / 0.791 | **1.020 / 0.967** | 0.800 / 0.798 | 0.801 [0.773, 0.843] | ind / ind |
| pairkernel serial licensed | B3 | 1.119 / 1.041 | 1.164 / 1.086 | 1.122 / 1.047 | 0.960 | live |
| maskagg serial licensed | B4 | 0.856 / 0.619 | **1.062 / 0.788** | 0.871 / 0.628 | 0.795 [0.744, 0.813] | live |
| maskagg serial repro | B4 | 0.893 / 0.688 | **1.062 / 0.813** | 0.839 / 0.676 | 0.843 | live |
| recur serial repro | B2r | 0.993 / 1.016 | **1.337 / 1.369** | 0.985 / 1.017 | 0.745 [0.721, 0.762] | live |
| stencil serial repro | B2r | 1.229 / 0.986 | **1.416 / 1.134** | 1.224 / 1.001 | 0.862 | live |
| stencil mt8 repro | B2r | 0.792 / 0.855 | 0.822 / 0.875 | 0.815 / 0.891 | 0.966 [0.925, 1.033] | ind / ind |
| saxpy serial licensed | B1 | 1.255 / 0.925 | 1.377 / 0.972 | 1.244 / 0.903 | 0.942 | live |
| saxpy mt8 licensed | B1 | 0.824 / 0.840 | 0.938 / 0.970 | 0.821 / 0.858 | 0.900 [0.836, 0.954] | ind / ind |
| saxpy serial repro | B1 | 1.062 / 0.667 | **1.375 / 0.878** | 1.082 / 0.686 | 0.785 | live |
| saxpy mt8 repro | (control) | 0.732 / 0.763 | 0.811 / 0.871 | 0.765 / 0.748 | 0.879 | live (OLD ind) |
| dot serial licensed | C | 1.455 / 0.971 | 1.642 / **1.090** | 1.416 / 0.967 | 0.894 | live |
| dot serial repro | C | 1.213 / 0.739 | **1.696 / 1.012** | 1.219 / 0.755 | 0.715 [0.706, 0.728] | live |
| dot serial fastmath | C | 1.395 / 0.671 | 1.631 / 0.782 | 1.369 / 0.712 | 0.853 | live |
| dot mt8 repro | C | 0.814 / 0.859 | **1.163 / 1.182** | 0.832 / 0.842 | 0.707 | live (OLD ind) |
| onepass serial licensed | C | 1.449 / 1.005 | 1.635 / 1.119 | 1.525 / 1.031 | 0.894 | live |
| onepass serial repro | C | 1.348 / 0.803 | **1.714 / 1.044** | 1.354 / 0.820 | 0.787 | live |
| onepass serial fastmath | C | 1.477 / 0.745 | 1.664 / 0.820 | 1.502 / 0.723 | 0.887 | live |
| fusedchain serial licensed | C | 1.561 / 1.032 | 1.709 / 1.110 | 1.567 / 1.023 | 0.943 | live (OLD ind) |
| fusedchain serial repro | C | 1.393 / 0.823 | **1.852 / 1.093** | 1.381 / 0.839 | 0.754 | live |
| fusedchain serial fastmath | C | 1.479 / 0.808 | 1.688 / 0.941 | 1.496 / 0.788 | 0.857 | ind / ind |
| fusedchain mt8 repro | C | 0.731 / 0.827 | 1.017 / 1.128 | 0.753 / 0.825 | 0.727 | ind / ind |
| gram mt8 licensed | (control) | 1.284 / 1.217 | 1.267 / 1.218 | 1.393 / 1.332 | 1.016 [0.982, 1.047] | live |
| comoment3 serial licensed | (control) | 1.300 / 1.305 | 1.303 / 1.323 | 1.325 / 1.338 | 0.998 | live |
| recur serial licensed | (control) | 1.116 / 1.166 | 1.075 / 1.114 | 1.114 / 1.125 | 1.032 [0.993, 1.053] | live |
| stencil serial licensed | (control) | 1.343 / 1.035 | 1.341 / 1.049 | 1.375 / 1.089 | 0.986 | live (OLD ind) |

No Blade arm is slower than OLD beyond its CI; the Fortran reference arms moved 0.95-1.07
(noise). Item C also rewrites the input-only twins (dot repro twin 0.849, saxpy repro twin
0.889), so "incremental" is not comparable OLD -> NEW; nets are in `w1-ab.md` 2b. Every
mt8-licensed block except gram is indicative on both sides (harness drift flags).

Losses remaining after wave 1: pairkernel serial repro 0.932 / 0.879 and comoment3 serial
repro 0.770 / 0.780 (the ordered scatter's targets, wave 2); pairkernel mt8 repro
0.687 / 0.678; stencil mt8 repro 0.822 / 0.875 and mt8 licensed 0.903 / 0.970 (ind); saxpy
mt8 repro 0.811 / 0.871; ifx fastmath dot 0.782 / onepass 0.820 / fusedchain 0.941 (ind).

### 15.3b Measured: wave-2 A/B

Mirrors: w1 = the wave-1 tip 6b3e5293; w2 = the wave-2 tip binary (1cf90428, bench-identical to f1910060: 48/48 emissions byte-equal,
src/cpp untouched); w2a = w2 + `BLADE_ORBIT_SCATTER=0`; w2b = w2 + `BLADE_TILE_TUNE=anti_g=0,tri_edge=0` + `BLADE_JOIN_THREADS=0`.
23 sessions 14:55-16:22, interleaved per cell, quiet box, harness defaults, zero failures; full tables in `bench/notes/round2/w2-ab.md`.

| cell | item | w1 gf / ifx | w2 gf / ifx | w2 / w1 (Blade ref) | knob-off / w2 | class |
|---|---|---|---|---|---|---|
| pairkernel serial repro | A ordered scatter | 0.929 / 0.859 (loss) | **1.163 / 1.079** (win / detectable-win) | **0.796** [0.788, 0.800] llvm; 0.788 cpp | w2a 1.251 / 1.265 | w2 live (w1 ind) |
| comoment3 serial repro | A scatter rank 3 | 0.770 / 0.775 (loss) | **1.288 / 1.299** (win) | **0.600** [0.584, 0.609] llvm; 0.603 cpp | w2a 1.662 / 1.646 | live |
| pairkernel mt8 repro | (G35 owns it) | 0.673 / 0.666 | 0.687 / 0.664 | 1.022 [0.996, 1.043] | w2a 1.010 | ind |
| comoment3 mt8 repro | (G35 owns it) | 1.105 / 0.933 | 1.097 / 0.925 | 1.004 [0.972, 1.063] | w2a 0.996 | ind |
| comoment3 serial licensed | D3b tri_edge | 1.302 / 1.315 | **1.335 / 1.358** | 0.982 [0.967, 0.993] llvm; 0.979 cpp | w2b 1.021 [1.010, 1.033] | live |
| comoment3 mt8 licensed | D3b tri_edge | 1.551 / 1.345 | 1.560 / 1.321 | 0.979 [0.921, 1.045] | w2b 1.077 [0.999, 1.184] | ind |
| antisym serial licensed | D2 anti_g 1 vs auto | 1.114 / 1.067 | 1.109 / 1.059 | 1.015 [0.998, 1.059] llvm; 1.003 cpp | w2b (auto) 1.008 / 1.027 | live / ind |
| antisym mt8 licensed | D2 anti_g 1 vs auto | 1.123 / 1.121 (ind) | 1.107 / 1.145 (ind) | 1.020 [0.974, 1.052] | w2b (auto) **0.954** [0.904, 0.996]; w2b expert 1.170 / 1.206 live | ind |
| onepass mt8 licensed | G7 join team (NEW arm; w1 = old binary, new spelling) | 0.924 / 0.980 | **1.135 / 1.166** | **0.858** [0.818, 0.903] | w2b 1.153 [1.115, 1.223] | ind |
| dot mt8 licensed | G7 side effect | 0.945 / 0.973 (ind) | **1.053 / 1.127** (detectable-win / win) | **0.867** [0.834, 0.909] | w2b 1.096 [1.057, 1.159] | w2 live |
| fusedchain mt8 licensed | G7 side effect | 0.927 / 1.035 | **0.997 / 1.151** | **0.926** [0.897, 0.947] | w2b 1.113 [1.062, 1.183] | live |
| gram serial licensed | control | 1.366 / 1.230 | 1.369 / 1.239 | 0.998 / 0.987 | -- | live |

No Blade arm is slower on w2 than on w1 beyond its CI (section 5 below: none). Codex's item 7 (decline the join team if mt8 dot regressed)
does not fire: the team helps dot, onepass and fusedchain alike. The scatter is the whole serial-repro story (knob-off restores w1's
times to within noise); it leaves mt8 repro to the G35 row slots as designed. The simplex edge k-loops are a measured 2% (CI excludes 1)
serially. The antisym row-group default: anti_g=1 (the previous visit order) vs auto are within noise serially (both lanes); under
threads auto is 4.6% faster at the edge of its CI on an indicative block -- the default stays at 1 this round, recorded as a quiet-session
re-measure (follow-up), since flipping it would re-open the gates and the mirrors for a marginal, flagged signal.

### 15.4 Pre-existing defects found by this round (gap register)

G40 licensed signed-zero divergence between lanes; G41-G43 (fixed, W2-F); G44 scatter
timing provenance; G45 scope-dependent evaluation count (OPEN); unforced map over a compact
pool fails BL9002 in the C++ lane (CLOSED: the card's nested-kernel probe by 04b00fe8,
`loops/524`; the one use site still failing in round 3 -- an explicitly computed TUPLE member,
`(g |> compute, 2.0)` -- by round-3 W1-3 with the tuple of computes, `tuples/034`-`040`,
`042`-`043`. NOT closed: a BARE deferred member `(g, 1.0)`, which the interpreter holds
unforced inside the tuple -- the C++ lane refuses it at codegen, BL7004 `tuples/041`; the LLVM
lane computes it eagerly; the shared contract remains OPEN as G61); W1-D's "68 vs 20" plain-fold report
(UNVERIFIED, card); join emitter private +0 accumulators override a user -0.0 seed (G40 note).
The Codex every-fold-entry audit of W2-F (`codex-diff-W2F-3.md`) found four more holes of
the G41 class, all pre-existing on master: the LLVM orbit-class fold ran its canonical-pool
pass over a writing virtual source (3 kernel applications for 4 logical reads), hot-row
scheduling ran the row consumer after each producer row without checking the producer's
writes (12 two-pass vs 9), the flat-epilogue nest and the flat materializer asserted
`BLADE_IVDEP` on a writing producer, and a named partial view hid its deferred producer from
the write analysis -- closed by bd34627b (llvm goldens G41-12/13, optimize hot-row decline, omp-pragma ivdep cases, loops/499, mutability-errors/025). A second audit found four more of the same class (`codex-diff-W2F-4.md`): wave 1's compact row classes never checked the PRODUCER (a consumer reading a capture the `comm` producer writes: 12 two-pass vs 9), hot-row scheduling ahead of a writing OUTER kernel or seed (1 vs 0), producer SIMD / team pragmas on a writing producer the lexical BL4005 check misses (a local row view), and the tuple chase dropping views -- closed as a CLASS by b161cf10: one shared predicate, `Optimize.scheduleMayWrite`, consulted by every moving pass for the producer kernel, the consumer kernel, the outer fold kernel, the seed and the intervening bindings (the per-pass matrix is in the G41 row; loops/500-501, mutability-errors/026-028, llvm goldens G41-19, optimize decision cases). Then open: the probe's writing virtual source was BL9002 in the C++ lane (pre-existing; closed by 04b00fe8, see the card above), and `scheduleMayWrite` treats a call into another module as a possible write (a decline, never a wrong answer). The hardening track then ran to nine Codex review rounds (`codex-diff-W2F-3.md` .. `-9.md`), each closing the previous findings and turning up the next same-class hole: compact row classes never checked the producer (G41 class, wave-1 B3), hot-row ahead of a writing outer kernel or seed, producer SIMD / team on a writer the lexical check misses, tuple-held views, an unresolved callback in a parallel body, a legal write to a function's own mut parameter newly refused, one-candidate callback resolution, a non-TOP recursion cutoff, nullary callbacks newly refused, second-order callback propagation, module-alias and discovery-order taint -- all closed through f1910060, where the callback table is a least-fixpoint abstract interpretation and every scheduling admission reads one shared transitive write predicate for producer, consumer, outer kernel, seed and intervening bindings (the per-pass matrix is in the G41 row). Opened and left OPEN: G45 (scope-dependent evaluation count of a named writing map), G46 (a kernel-body `let mut c = G` aliases G in the C++ lane but is copied by the interpreter), G47 (the escape rule refuses a pure higher-order program whose callback escapes through a returned alias or a tuple -- a precision regression on the conservative side, no checked-in program affected).

### 15.5 Not done this round (follow-up cards)

E-G5 recur let-rec step threading (wave 3 design in brief E); B2l licensed lanes arm;
stencil mt8-licensed attribution (~1 ms unexplained vs an expert with the same schedule);
the relational whole-region track (membership hoisting, hash equijoin, aggregate-only
grouping); the N01-N10 benchmark workloads; D1 persistent syrk team (instrument first);
scatter v1.1 contraction invariance REJECTED (do not revisit without a new argument).

## 16. Wave 3 (2026-10-20) -- recur step threading, the stencil attribution, D1 / B2l / anti_g

Record: `bench/notes/wave3/` (README indexes the brief, both Codex reviews, the two attribution
notes, the scripts). Base: `worktree-bench-fortran` at ccde7e4e (the wave-2 tip f1910060 plus
notes). Honesty rules as in 14-15: `MEASURED` names the command; a block the harness flagged
is `indicative`; no Blade arm may be slower than its OLD binary beyond its CI without the row
saying so.

### 16.1 What landed

| item | mechanism | knob | gate / proof |
|---|---|---|---|
| E-G5 threaded `let rec` step (recur mt8; gap G5) | `where omp(v: 1)` on the inductive arm's FINAL map is the request; slice-write fusion keeps it (a nested stage map with one still declines) and admits the step only with the proof the barrier-free team needs: a fresh rank-2 trajectory stored at `BUF[n][k]`, every trajectory read a row view `prefix(n - c)` with a literal `1 <= c <= lo` or a seed row `0 <= r < lo`, read at the cell being written; no write outside the buffer (the shared `scheduleMayWrite` predicate over the slice and the whole time-loop body, the buffer bound locally); no `where repro` callee spliced; one requesting slice per recursion. C++: the time loop inside ONE team, contiguous x ownership fixed for every step (`blade_fold::chunk_lo`), NO per-step synchronization -- MEASURED on the box: a team entry or a barrier is ~40 us (libgomp, Windows; wait-policy knobs change nothing), so brief E's per-step team (300 of them) is 12 ms against a 7 ms expert. A licensed `prodsum(row, row)` fold rides the team as per-worker chunk partials (the self-dot's K-lane form at the configured lane count), combined ascending per row, the time chain in order; otherwise a strict post-pass after the team folds COMPLETE rows (in parallel into slots only under the OUTER fold kernel's own `where omp`, G35's gate; else serially) -- bitwise the serial program for every team size. Fail-closed shape re-check at emission; threads suppressed prints the census at the time loop; a request the fused loop cannot honour leaves the slice UNFUSED (the map keeps its per-step team); an entry no emitter decided prints its census at the end of the unit; the LLVM lane runs the sequential loop and records so (G11) | `BLADE_REC_STEP_OMP` (optimizer gate; off = the pre-wave-3 emission) | corpus recursive-arrays/031-032, mutability-errors/042; 15 optimize decision / emission cases; `blade test rec-step` (threads-on vs threads-off by raw bits -- trajectory, scalar, observed team -- T = 1 / 3 / 8 and a thread-limited team; licensed 1e-12, repro bitwise); two Codex attack reviews (`codex-review-E-G5.md`: 19 objections, all folded in; `codex-diff-E-G5.md`: two findings, fixed) |
| stencil read-back licence (item 2) | `stencil.blade`'s input checksum carries `where omp` on its outer fold kernel (the mt8 read-back licence every Fortran arm has, section 12 / D44): a licensed build threads it (Path B over the rows), a repro build keeps it serial (G51: the row-slot recognizer takes a nested `range` fold, not a row-view fold) | -- | values identical to the original source (repro bitwise); smoke in the A/B |

Not built, on the numbers: **B2l** (brief B's optional licensed-lanes arm: E-G5's in-team
partials serve its mt8 target; serial ceiling <= 3.5%, below what the harness resolves here;
`lanes` stays reserved) and **D1** (instrumented: gram's three regions cost 236-257 us per
call, ~1.8% of an mt8 gram, and the persistent-team shape is no cheaper on this libgomp --
299-376 us; dropped on brief D's own rule).

### 16.2 Attribution (MEASURED, single-process phase timers, directional; `recur-mt8-attribution.md`, `stencil-mt8-attribution.md`)

CAVEAT: the first probes and the first A/B run (18:53-20:09) ran beside a stray single-core
process of this session's own (a `find /` started 16:50, killed 20:11). Every figure below
is from the QUIET-box re-runs (`reprobe.ps1`, `secondlook.ps1` run 3, 20:12-21:35); the
earlier figures stay in the notes, marked.

- **recur mt8 licensed.** E-G5's region is NEAR parity with the gfortran expert's region:
  6.85 vs 6.98 ms at T=8 as written (page faults inside both), 6.02 vs 5.44 pre-touched
  (Blade 11% slower); serially Blade is faster (8.56 vs 9.68; pre-touched 5.04 vs 5.54).
  Writes are flat on this box (48 MB row-stream writes 8.6-10.6 GB/s at T = 1..8), so the
  trajectory store is a floor at any team size. The rest of the gap to the expert's
  program is the 48 MB teardown INSIDE Blade's timed region (+3.4-3.5 ms after a threaded
  fill, +1.8-1.9 serial); the experts free after their stop line (README item 6, G52).
  MEASURED, probe-only: a bounded retention arm (`kMaxBlock` 64 MB, `kBudget` 96 MB in a probe
  copy of the pool header) moves the teardown out of the timer -- recur T=8 program end
  11.5-11.8 -> 7.9-8.2 ms, T=1 11.3 -> 9.6; stencil T=8 9.7-10.2 -> 9.3-9.4. Not changed: the
  footprint policy is the user's call.
- **stencil mt8 licensed.** Expert region 5.38 ms at T=8 (the loaded-box 4.25 was not
  reliable); Blade's 6.0-6.4, of which the `lap` teardown is 0.8-1.0 ms (G52). The earlier
  "DRAM-bound reads" diagnosis is WITHDRAWN: a READ probe scales 22 -> 53 GB/s from 1 to 4
  threads, so the input checksum's 1.8-2.1 ms for 24 MB is ~1 ms slower than a streaming read
  of the same bytes over a warm buffer (0.73 ms). Instrumented (G53), the cost is the FIRST read
  after the fill, not the emission (0.88 ms after a prior full read): G53 closed. The read-back licence interleaved directly (same build, old vs new source,
  T=8): licensed 8.08 vs 8.38 ms medians (IQRs overlap), repro 9.58 vs 9.78 with identical
  checksums -- no measurable gain; kept as the fair spelling.
- **Codex second look** (user request; `codex-second-look.md`, actions in
  `second-look-actions.md`): B2l hand-patched and measured (serial licensed recur, blocks of
  128/256/512: 14.0 / 13.7 / 13.0 vs 13.3 ms one-shot, identical checksum bits) -- within
  noise, stays unbuilt; D1 re-instrumented with the real 201 pack panels (today 251-293 us per
  call, the persistent shape 284-335) -- dropped; G51 judged build-now, narrowly -> follow-up.

### 16.3 Measured: wave-3 A/B (harness `run.py bench`, 18 sessions 20:12-21:30, quiet box; tables `bench/notes/wave3/w3-scripts/tables.md`, `antig.md`)

Mirrors: **w2** = the wave-2 binary on the ORIGINAL sources (OLD); **w3** = the wave-3 tip,
defaults; **w3a** = w3 with `BLADE_REC_STEP_OMP=0`; **w3g** = w3 with `BLADE_TILE_TUNE=anti_g=0`.
The first A/B run (18:53-20:09) is DISCARDED (it ran beside the stray process). Every
session ended with 0 failures; controls held (compute 12.7-14.1 ms, memory 5.3-5.9 ms). A
cross-session ratio below is the analyst's unpaired bootstrap of median(B)/median(A) (4000
resamples; not a harness output); expert ratios are the harness's own (> 1 = Blade faster).
`ind` = at least one block drift-suppressed by the harness: indicative, not live.

| cell | Blade arm: A -> B (ms) | B/A [95% CI] | class | same-session expert ratio (gfortran / ifx expert) |
|---|---|---|---|---|
| recur mt8 licensed: E-G5 off (w3a) -> on (w3) | cpp 40.34 -> 10.01 | w3a/w3 4.03 [3.77, 4.14] | ind | w3 0.752 [0.685, 0.791] / 0.797, suppressed; w2 had no Blade arm (`no_parallel_form`) |
| recur mt8 repro: E-G5 off -> on | cpp 44.13 -> 13.02 | w3a/w3 3.39 [3.29, 3.47] | live | w3 0.573 [0.545, 0.591] loss / 0.615 loss |
| recur serial licensed, w2 -> w3 (control) | llvm 9.61 -> 8.64; cpp 11.67 -> 10.26 | 0.899 [0.875, 0.938]; 0.879 | live | w2 1.075 / 1.097 -> w3 1.070 detectable-win / 1.134 win (the experts moved 0.89-0.93 too: session level) |
| recur serial repro, w2 -> w3 (control) | llvm 9.94 -> 9.56; cpp 11.89 -> 11.29 | 0.961 [0.938, 0.994]; 0.950 | ind | w3 1.328 win / 1.367 win |
| stencil mt8 licensed (read-back licence) | cpp 8.54 -> 9.31 | 1.090 [0.987, 1.151] | ind | w2 0.937 / 0.969 -> w3 0.891 / 0.898 (all suppressed); same-session experts +0.7-6.1% |
| stencil mt8 repro | cpp 9.92 -> 10.84 | **1.093 [1.027, 1.188]** | ind | w2 0.827 / 0.842 -> w3 0.782 / 0.805 (suppressed); same-session experts +3.4-7.8% |
| stencil serial licensed (control) | llvm 11.04 -> 10.76; cpp 14.94 -> 14.45 | 0.974; 0.967 | ind | w3 llvm 1.321 win / 1.022 equivalent (live) |
| gram serial licensed (control) | llvm 58.31 -> 57.89; cpp 59.56 -> 59.68 | 0.993; 1.002 | ind | w3 1.345 / 1.221 (suppressed) |

- **E-G5 is the wave's result.** recur mt8 gains a Blade arm (w2 had none), 4.0x faster than
  its own knob-off mirror licensed (3.4x live under repro). It still LOSES to both experts at
  mt8 (licensed 0.75-0.80 indicative, repro 0.57-0.62 live). The licensed loss is dominated by
  the teardown (16.2: the retention arm removes ~3.5 ms from the program in the probe); the
  repro loss adds the strict post-pass (a second read of the 48 MB trajectory after the team).
  Not claimed as a win.
- **w3a is not the old program.** With the knob off, the NEW source's `where omp(v: 1)` stays
  out of the fusion (the request is never silently dropped), so the slice is unfused and every
  step copies: serial w3a is 2.0-2.3x slower than w3. The baseline for "nothing regressed" is
  w2 (the old source), and serial w3 matches or beats it with the expert ratios unchanged.
- **The one Blade cell slower beyond its CI: stencil mt8 repro, w3/w2 1.093 [1.027, 1.188]
  (indicative).** Its emission differs from the old source's only in form (the request is
  emitted serial under repro, `[omp] requested but emitted serial: BLADE_FP_REPRO=1`, the same
  ascending chains); the same-session expert arms moved +3.4-7.8%; and the two emissions
  interleaved directly (same build, T=8, contract off, 15 runs) give 9.58 vs 9.78 ms with
  identical checksums. Recorded as box drift plus at most ~2%, NOT cleared by the harness.
  The licensed cell (1.090 [0.987, 1.151]) is within its CI; the direct interleave there gives
  8.08 vs 8.38 ms (IQRs overlap): the read-back licence buys nothing measurable (16.2).
- **anti_g default (item 4): KEPT at 1.** Three interleaved sessions per arm, pooled (63
  rounds each; `antig.md`): serial live, auto (w3g) / 1 (w3) = cpp **1.013 [1.004, 1.025]**,
  llvm **1.014 [1.003, 1.029]** -- auto is 1.3-1.4% SLOWER beyond its CI; mt8 cpp 0.967
  [0.925, 1.011], indicative, with the gfortran expert moving 0.969 in the same sessions (box
  drift). The rule was "flip only if auto wins beyond CI on live blocks": it does not.
  Per-session expert ratios (serial, live): llvm 1.099-1.122 vs gfortran expert, 1.047-1.075 vs
  ifx expert -- antisym serial stays a Blade win.
- **D1**: dropped on the instrument (16.2), no A/B cell. **B2l**: not built (16.2).

### 16.4 Gates: GREEN on b4a21003 (Blade.dll 2B9E12AC...; `bench/notes/wave3/w3-gates-summary.md`)

default **7234 / 0** (no skips), opt-diff 2074 / 0 / 945, docs 131 / 0 / 78, llvm 816 / 0 / 691,
interp 3016 / 0 / 3 -- run 17:57-18:38 from `C:\bt_w3` (tests/docs junctions), the hash unchanged
after every gate; the deltas against the wave-2 finals are the wave-3 tests.

## 17. Round 3 (2026-10-21) -- waves 1 and 2: the research round, relational and orbit regions, workloads, the retention arm

Record: `bench/notes/round3/` (README indexes the research briefs A-D and their Codex attack reviews, the staged plan,
every builder brief, every Codex diff review, the two A/B records `w1-ab.md` / `w2-ab.md`, the gates summaries and the
chain scripts). Base: `worktree-bench-fortran` at 66e04875 (the wave-3 tip, master). Process: one squashed commit per item,
each after an independent Codex diff review and the fix round it demanded (three items needed two or three looks); a
merge-level block run on the integration tree after every code merge; the five gates and a quiet-box harness A/B at each
wave end. Honesty rules as in 14-16: `MEASURED` names the command; a block the harness flagged is `indicative`; no Blade
arm may be slower than its OLD binary beyond its CI without the row saying so; a builder's directional number never
replaces the harness's.

### 17.1 Wave 1 (merged 9da403a8, records 667250cd = master): A/B `notes/round3/w1-ab.md`

| cell | item | old gf / ifx | new gf / ifx | new / old (Blade ref) | class |
|---|---|---|---|---|---|
| stencil mt8 repro | A2: the G35 row-slot form admits a row-VIEW inner fold (G51) | 0.805 / 0.877 | **0.924 / 0.959** | **0.876** [0.858, 0.935] cpp | ind |
| maskagg serial licensed | T4b: the positions table elided under a demand census (default); T4a mask-site gather opt-in | 1.064 / 0.777 | **1.290 / 0.970** | **0.817** llvm / 0.772 cpp | ind |
| maskagg serial repro | T4b | 1.073 / 0.827 | **1.260 / 0.955** | **0.866** llvm / 0.856 cpp | live |
| pairkernel / comoment3 mt8 repro, `BLADE_FOLD_ROW_SLOTS=0` | A1 experiment: the ordered scatter in place of the row slots | -- | -- | 0.841 / 0.843 of the row slots | ind |
| antisym serial / mt8 licensed, `anti_g=0` vs 1 | re-measure (3 sessions each) | -- | -- | serial 1.010-1.020 (auto slower); mt8 0.952 (auto faster) | ind |
| twofir (N07) serial / mt8 | new kernel: resident two-output FIR | 1.19 / 0.96; 0.70 / 0.74 | 1.18 / 0.96; 0.74 / 0.75 | ~1.0 | live / ind |
| tensorop (N09) serial / mt8 | new kernel: named-axis tensor-product action (mode_product baseline, serial nests) | 0.19 / 0.14; 0.05 / 0.05 | 0.19 / 0.14; 0.05 / 0.05 | ~1.0 | live / ind |

Also in wave 1: four C++ deferred-materialization BL9002s fixed (W1-3; gap rows G57-G62 opened for the pre-existing
defects it found), tuple members evaluated in source order. Gates on 9da403a8: default 7389/0, opt-diff 2173/0, docs 131/0,
llvm 841/0, interp 3077/0. The A1 experiment and the anti_g re-measure became wave-2 decisions (W2-0).

### 17.2 Wave 2 (tip 3a3925ac): what landed

| item | mechanism | knob | measured cell (17.3) |
|---|---|---|---|
| W2-0a scatter precedence (mt8 repro) | where `OrbitFold.planStrict` admits the walk, the ordered orbit scatter is taken ahead of the G35 row slots; raw-bit pins across default / `prefer` / slots-off / threads-off / the serial walk at T = 1 / 4 / 7 incl. two NaN payloads | `BLADE_FOLD_ROW_SLOTS=prefer` restores the old order | pairkernel mt8 repro 0.846 |
| W2-0b anti_g per regime | row groups auto (cache budget) only when the build emits a thread team AND the driver's static cap (p*p*n) >> 20 is >= 2; else 1 | `BLADE_TILE_TUNE anti_g=<G>` forces | antisym mt8 licensed 0.857 |
| W2-3 row-owned pull scatter (rank 3; G66) | each thread owns whole H rows: seeds H[a, b] once, pulls T(c, a, b) for c < a ascending, then the THIRD=false slice; team completion before the serial scatter2 tail; the executed form (`serial-leaf3` / `row-pull`) chosen on the ACTUAL team and recorded in the run record's `forms` census; 6,923 header identities + emitted raw bits via `BLADE_PRINT_BITS` | `BLADE_ORBIT_PULL`, `pull_min` | comoment3 mt8 repro 0.737 |
| W2-1 membership hoisting (T1; G65) | ONE search structure (linear / bitmap / hash chosen from (M, min, max) only) built before the host, probed by every `contains(B, x)` in its kernels, freed after; one target-evaluation contract in all lanes (set first, target once, before any cell); a real LLVM IRContains arm; host-anchored route pins (31 rows = the corpus) | `BLADE_MEMBER_HOIST` | no bench kernel (directional: semiagg-shaped C++ 82-92 -> 11 ms, LLVM 345-400 -> 6.4 ms) |
| W2-2 T4c shared selection (+ certificate; G70) | one selection index per mask for every `compound(c_j, keep)`, data stores kept, one joint pass at the first companion; a `contains` predicate certified pure/total only with T1's admitted plan; legacy single-compound shapes pinned byte-identical | `BLADE_COMPACTION_FUSION=unshared` | no bench kernel (directional: filtergroup-shaped J = 3 0.70-0.76 both lanes; semiagg flat -- see G70) |
| W2-4 producer-input legs (A7c; G69) | saxpy's input sums ride the y2 producer pass as extra fold legs (strict chains under repro, builtin-licensed otherwise), the callee inlined one level with its pools released at the call's return boundary -- INCOMPLETE: see 17.5 | `BLADE_EPILOGUE_LEGS` | saxpy serial repro 0.889 llvm / 0.923 cpp |
| W2-6 pool-retention arm (A4; G52) | the user's ruling: an opt-in arm, never a default change; `1` = 64 MB / 96 MB per thread (C++; the LLVM shim's process-wide cache gets the same caps); policy in the emitted text (the exe cache keys on it), the run record (`policy.pool`, additive) and the plan build line | `BLADE_POOL_RETAIN` | recur mt8 0.74-0.77, stencil mt8 0.92 under the arm |
| W2-5 haloadjoint (N02) | 1-D weighted nonlinear halo primal + reverse action via `ad.grad`; the strict derivative reference is an explicit ordered update list both Fortran twins implement; N10 gated out as G67 (the per-group packed H fails in every lane), G68 (AD zero-literal compile blowup) | -- | first measurement 0.52-0.65 serial, 0.41-0.65 mt8 (losses) |

### 17.3 Measured: wave-2 A/B (harness `run.py bench`, 28 sessions 15:07-17:49 + the saxpy legs-off re-run, quiet box; `notes/round3/w2-ab.md`)

Mirrors: **r3w1** = the wave-1 tip binary (OLD) on the wave-2 bench tree; **r3w2** = the wave-2 tip, defaults;
**prefer** / **pull0** / **antig1** / **legs0** / **retain** = r3w2 with the one knob named above. Ratios: B/A = analyst
bootstrap of median(B)/median(A) of the Blade reference lane; expert ratios are the harness's own (> 1 Blade faster).

| cell | Blade arm: A -> B (ms) | B/A [95% CI] | class | expert ratio gf / ifx: old -> new |
|---|---|---|---|---|
| pairkernel mt8 repro, old -> new (scatter precedence) | cpp 9.03 -> 7.64 | 0.846 [0.838, 0.861] | live | 0.681 / 0.686 -> 0.803 / 0.805 (loss, narrowed) |
| pairkernel mt8 repro, new -> prefer (the old order) | cpp 7.64 -> 9.01 | 1.179 [1.160, 1.210] | ind | -- |
| comoment3 mt8 repro, old -> new (the pull) | cpp 4.19 -> 3.09 | **0.737** [0.716, 0.760] | live | 1.147 / 0.964 -> **1.553 / 1.305** (both win) |
| comoment3 mt8 repro, new -> pull0 (serial scatter) | cpp 3.09 -> 3.57 | 1.156 [1.108, 1.20] | ind | -- (the `prefer` arm still pulls at rank 3: 1.017 is not a row-slot measurement) |
| comoment3 mt8 repro, new -> g35 (pull off AND `prefer`: the G35 row slots; re-run 18:09-18:11) | cpp 3.09 -> 4.40 | **1.422** [1.383, 1.501]; vs the serial scatter 1.230 [1.185, 1.308] | ind | on the row slots 1.100 / 0.950 (suppressed) |
| pairkernel / comoment3 serial repro (controls, four mirrors) | llvm 29.7 / 8.8 | 0.996-1.009 | live | unchanged |
| antisym mt8 licensed, old -> new | cpp 9.50 -> 8.13 | 0.857 [0.792, 0.899] | ind (pooled) | 1.152 / 1.227 -> 1.15-1.17 / 1.19-1.22 (suppressed) -- NOT an anti_g result: both experts moved 0.846-0.857 in the same sessions (environmental) |
| antisym mt8 licensed, new -> antig1 (1 forced vs auto): the anti_g evidence | cpp 8.13 -> 8.45 | 1.039 [0.992, 1.069] | ind | marginal; the per-regime default is kept on it and wave 1's 0.952 |
| antisym serial licensed, old -> new | llvm 27.45 -> 27.65; cpp 28.32 -> 28.64 | 1.007 [1.001, 1.020]; 1.011 [1.002, 1.019] | ind | 1.109 / 1.052 -> 1.10-1.12 / 1.06-1.07 -- slower at the CI edge on BYTE-IDENTICAL serial emission: session drift, recorded |
| saxpy serial repro, old -> new (the legs) | llvm 10.79 -> 9.59; cpp 15.52 -> 14.33 | 0.889 [0.882, 0.919]; 0.923 [0.917, 0.930] | live | 1.408 / 0.888 -> 1.584 / 0.994 (ifx: loss -> detectable-loss) |
| saxpy serial licensed, old -> new | llvm 10.58 -> 9.45; cpp 15.46 -> 14.65 | 0.893; 0.947 | live | 1.391 / 0.991 -> 1.547 / **1.113** (ifx: equivalent -> win) |
| saxpy mt8 repro / licensed (decline controls) | cpp 10.89 -> 10.87; 8.95 -> 8.98 | 0.998; 1.003 | live / ind | unchanged |
| saxpy serial repro / licensed, new -> legs0 (the separate joined pass; re-run 17:52-18:04) | llvm 9.59 -> 11.18; cpp 14.33 -> 15.76 / llvm 9.45 -> 10.84; cpp 14.65 -> 15.77 | 1.166 [1.129, 1.183]; 1.100 / 1.148; 1.077 | live / ind | legs0 lands 2-4% above the OLD binary (between-session drift); its mt8 controls flat (1.005 / 1.007) |
| haloadjoint serial licensed / repro (new kernel) | llvm 1.73 / 1.83 | old = new (1.006 / 0.997) | live | 0.52 / 0.65; 0.52 / 0.66 |
| haloadjoint mt8 licensed / repro | cpp 2.97 / 2.94 | 0.994 / 0.995 | live / ind | 0.42 / 0.64; 0.44 / 0.65 |
| stencil mt8 repro / licensed, new -> retain | cpp 8.43 -> 7.80; 8.60 -> 7.92 | 0.926 [0.897, 0.943]; 0.920 [0.896, 0.971] | live / ind | with the arm ~0.99 / 1.04; 0.98 / 1.05 (derived from the new-binary expert medians) |
| recur mt8 licensed / repro, new -> retain | cpp 9.13 -> 6.76; 12.38 -> 9.52 | **0.741** [0.724, 0.782]; **0.769** [0.754, 0.790] | ind | with the arm ~1.01 / 1.08; ~0.80 / 0.82 (derived) |
| gram serial licensed (control) | llvm 56.0 -> 56.3 | 1.005 [1.000, 1.046] | ind | unchanged |

Peak working set (harness PeakWorkingSetSize, MB): the retention arm raises recur from 51.7 to 51.8 and stencil from 51.4
to 51.5-51.6; every other mirror equals its default. The LLVM lane has no mt8 arm (G11), so every mt8 cell is blade-cpp.

### 17.4 Gates: GREEN on 3a3925ac (Blade.dll 12917295...; `notes/round3/gates/w2-gates-summary.md`)

default **7872 / 0**, opt-diff 2257 / 0, docs 132 / 0 / 78, llvm 914 / 0, interp 3118 / 0 -- run 13:05-14:38 from `C:\bt_r2w1`,
the hash unchanged after every gate; examples sweep 68 files, the same six pre-existing failures as wave 1 (four notebooks
whose zarr stores are not in the checkout, one BL4003 warning, one BL3999). Every mirror smoked for its benched pairs.

### 17.5 Open at the wave end

- **W2-4's lifetime class (G69): seven Codex looks, the kept inline made OPT-IN by the user's decision (rounds 4-8).** Round 4 made the cleanup an IR-level RETURN-BOUNDARY EVENT (`IRReleasePools`, one per inlined call: a CSE barrier, an IRValidate check, every renderer and the LLVM lane releasing through it, the interpreter a no-op); rounds 5-7 narrowed the admission to a closed whitelist (owned pools = forced rank-1 dense maps; every other callee unit allocation-free by type AND evaluation, every user call in a non-owned unit refused, tagged types judged by their inner type, String leaves refused) and corrected the route and coverage records. The seventh look still found the class open: a non-owned unit may NAME a fold kernel whose body is never resolved by the ownership walk, so a helper called inside that kernel can expand in place in the LLVM lane with an unlisted alias copy. Per the user (2026-10-22: no further fix rounds) round 8 makes the kept one-level inline OPT-IN -- an explicit `BLADE_EPILOGUE_LEGS=1` -- and records the hole as KNOWN INCOMPLETE in G69 / CLAUDE.md / features.md; unset keeps the legs that ride a producer already in the region (the top-level spelling) and declines the inline-needing plans with a record; `=1` is an opt-diff mirror so the arm stays differentially tested. The default path (legs without a kept inline) is verified; the opt-in arm is tested on its witnesses (ASan 030 / 031 / 032 / 033 / 035 C++ only; the ten emitted equality witnesses; 047 = equal LLVM refusal) and NOT proved. The seven looks are sections of `notes/round3/reviews/wave2-codex-reviews.md`. Consequence for the numbers: saxpy's DEFAULT emission is the joined pass again (its 17.3 gains belong to the now-explicit arm); the wave-3 end chain re-measures saxpy as default vs `BLADE_EPILOGUE_LEGS=1` vs the wave-1 binary, four regimes.
- **semiagg under T4c is flat** (G70): at ~5% selectivity the branchless single pass reads the whole payload where the old
  position gather read the selected cells; a selectivity-aware gather is the follow-up.
- **G67 / G68** (N10's per-group packed H; the AD zero-literal compile blowup) block N10 and large haloadjoint sizes.
- **antisym serial drift**: 0.7-1.1% slower pooled across sessions on identical emission -- noise at the CI edge, watched.

### 17.6 Wave 3 (tips 3641bfc1 .. e0991a39; records `notes/round3/reviews/wave3-codex-reviews.md`, `briefs/wave3-builder-briefs.md`): the relational and orbit regions, built

| item | mechanism | knob | status |
|---|---|---|---|
| W3-1 T3 aggregate-only grouping (G71) | a function-body grouping whose grouped arrays feed only aggregate peels runs as ONE pass over the source rows in source order: per row the key, the group (positional / EnumIdx declared order / dynamic first-occurrence through the C core `blade_gkmap_*`), then four EXACT state machines (unseeded, seeded, prodsum, count) -- no perm, no gathered pool; bitwise the two-pass form under REPRO, strict first; three regimes; LLVM dynamic discovery through the shared C core | `BLADE_GROUP_AGG` (optimizer gate) | REDESIGNED after review B; three Codex looks; merged 5ea03056 + records e0991a39 (the sweep evidence made auditable) |
| W3-2 T2 hash equijoin, PILOT (G72) | an inline rank-2 equijoin compound becomes a stable forward-filled CSR over the right keys (`blade_join_*` in `blade_member.h`), a counting pass sizing the relation before any store, a left-major / right-minor probe; C++ lane v1, the LLVM lane declines and records | `BLADE_HASH_JOIN`, `BLADE_JOIN_REPORT` | two Codex looks; merged 4f3a37b6 |
| W3-3 P1 producer-fused ordered commit | a costed standalone prototype (`notes/round3/prototypes/p1-commit`): 21-round interleaved matrix, 1176 processes, 0 identity mismatches; T8 commit d:4 7.98 ms vs scatter 8.38 -- the rule (paired lower CI >= 1.5 ms AND wait fraction < 0.20) fails at every G; BIMODAL at T8 (a fast cluster at or under the expert, a slow one no better than the scatter) | -- | MEASURED and DROPPED by the predeclared rule (3641bfc1); the salvage note (`#codex-w3-3-salvage`) names ONE cheap experiment -- per-physical-core affinity, 0.5-1 day, a predeclared decider -- not taken this round |
| W3-4 P2 row orbit actions (G73) | a top-level rank-2 row map over a stored SymIdx<2, n> pool whose inner fold reads the cell at `S(i, j)` with role terms runs as one pass over the canonical cells (`tri_action` / `action2`; an off-diagonal cell read once, its term evaluated twice with own / partner swapped); bitwise the row walk under REPRO + contract off in both lanes | `BLADE_ORBIT_ACTIONS` | one Codex look; merged 972633c6; not harness-measured (one-core directional only) |
| W2-4 A7c lifetime class (G69), rounds 4-9 | the inlined callee's cleanup as an IR-level return-boundary event (`IRReleasePools`), a fail-closed ownership whitelist, allocation-free evaluation, every user call refused in a non-owned unit; the kept one-level inline made OPT-IN (`BLADE_EPILOGUE_LEGS=1` explicit) by the user's decision after the seventh look; BL7005 warns whenever a lane actually retains it | `BLADE_EPILOGUE_LEGS` | nine Codex looks in all; merged aa39e50a + bea9fcfe; the planned closure is the scope model A (`#codex-g69-design`: real lifetime scopes, 10-15 builder-days, a later item) |

Gates and A/B: one combined chain after wave 4 (17.8).

### 17.7 Wave 4 (tips a7c1394c .. f46fdfc0; `reviews/wave4-codex-reviews.md`, `briefs/wave4-builder-briefs.md`): the gap-register cleanup, closed under the fallback rule

The user's rule (2026-10-22/23): a third look that still fails merges what passes and records every remaining hole as an OPEN gap row; then
(2026-10-23) "don't continue any more rounds on these. Wrap up what's working for wave 4, then benchmark". The orchestrator merged the
reviewed tips with MERGE-TIME fixes for the regressions the last looks found and nothing else; the builders' unreviewed round-3 WIP
commits stay on their branches (W4-2 0fd34ee9, W4-3 5849be90) as the starting point if these are reopened.

| item | landed | fixed at the merge | OPEN after the merge (gap rows) |
|---|---|---|---|
| W4-4 G68 grad zero-fill (a7c1394c) | reverse mode's cotangent zero buffer is a zero-fill MAP (haloadjoint at n = 1,000,003 lowers in 0.64 s, was killed after 627 CPU s) | -- (one fix round after its look) | G78: LLVM `keepPool` nulls the owning function scope's slot (a pre-existing escape) |
| W4-1 deferred values in tuples / aliases (5d4a28d0) | one contract per divergence in every lane: writes through a deferred map REFUSED (BL4005, program-wide fixpoint), alias classes print as one value, tuples of deferred computations stay deferred / any other tuple forces at construction left to right, `if` values over computed arrays ordered or REFUSED (BL6002), calls holding an explicit compute evaluate every argument left to right; corpus tuples/041-067, loops/540-561 | the third look's one REGRESSION: the interpreter forced block-local tuples of deferred tuples (13 lines; tuples/066-067) | G57 PARTIAL (LLVM refuses, not orders, mixed ordered calls); G59 PARTIAL (>16-link tuple aliases); G60 PARTIAL (tuple-held views bypass BL4005; LLVM sole-read rerun at print; LLVM top-level-block alias not copied); G58 notes the pre-existing C++ function-body deferred-tuple BL9002; G75 PARTIAL; G77, G79 OPEN |
| W4-2 function-body folds / captures / refusals (2bd7b485) | G54 scope-exit frees under a POSITIVE synchronous-kernel proof (tensorop's `y` no longer leaks per call); G55 the function-body fold let threads as at top level; G64 the zero-level whole-array apply lowered to its CALL before the lanes split; G67 a compact-cell row map refused once (BL4004) | the second look's one REGRESSION (a use after free: a scalar return's call argument storing a closure): the exemption WITHHELD in any scope whose scalar return names a callable -- fail closed, memfree/029 ASan clean | G54 PARTIAL (the builder's slot-by-slot return classification unreviewed; LLVM evidence only through its own slot policy); G55's plan-subject inconsistency; G64 PARTIAL (LLVM compound parameters: the lane's pre-existing refusal, not a common one; no must-succeed native assertion); G67 with no outer-iteration-dimension guard |
| W4-3 strict seeds / provenance (f46fdfc0) | G74 the unseeded fold seed is a dedicated MARK resolved by route: the exact identity (-0.0) on the strict routes (REPRO build, `where repro` body), the base +0.0 on licensed ones -- licensed emission unchanged (builder-reported 949 / 251 identical files); `where repro` bodies strict in every lane (lifted lambdas / sections inherit the demand); G76 one guard-failure text across the three lanes (17 cli cases) | two fail-safe fixes: the declaration table attached to the FINAL program object (multi-module programs had lost it); the exact anonymous name `lambda` instead of a prefix test | G74 PARTIAL (a fold strict only by construction -- a writing map body -- still starts at +0.0 in a licensed build; the sweep is a reported count, not a committed artifact); G80 OPEN (a NAMED kernel declared outside a `where repro` body keeps the licensed forms); G76's per-lane texts |

Light checks at each merge (C:\bt_r2w1, the integration tree), every block green; the footers are in the commit messages. The corpus
numbering collisions between W4-2 and W4-3 were resolved at the merge (symmetry/142 -> 145, functions/535 -> 537, 536 -> 538).

### 17.8 Measured: the combined wave 3+4 A/B on f46fdfc0 (`notes/round3/w3-ab.md`; 27 sessions 02:18-04:50, 2026-10-24, quiet box) and gates (`notes/round3/gates/w3-gates-summary.md`)

Gates GREEN on f46fdfc0: default **8223 / 0**, opt-diff 2364 / 0 / 966 skipped, docs 133 / 0 / 79, llvm 950 / 0 / 730 skipped,
interp 3229 / 0 / 4 skip-unsupported; examples 68 files, the same six pre-existing failures; every mirror smoked.

| cell | item | blade-llvm NEW/OLD | blade-cpp NEW/OLD | class | expert ratio (gf / ifx) |
|---|---|---|---|---|---|
| groupagg serial repro | W3-1 T3 one pass (default) | **0.435** [0.426, 0.442] | **0.471** [0.455, 0.485] | live | 0.574 -> **1.331** / 0.385 -> 0.889 |
| groupagg_dyn serial repro | T3 dynamic keys | **0.050** (248 -> 12.3 ms) | **0.491** [0.476, 0.503] | live | 0.077 -> **1.554** / 0.076 -> **1.533** |
| groupagg serial licensed | default declines (control) / `BLADE_GROUP_AGG=licensed` opt-in | 1.010 / **0.431** | 1.025 / **0.453** | ind | 0.570 / 0.336 -> 1.278 / 0.745 (opt-in) |
| hashjoin serial lic / repro | W3-2 T2 stable CSR, PILOT | LLVM declines (fallback, both binaries) | **0.010** / **0.009** (8.5 -> 0.087 ms) | ind | 0.008 -> 0.778 / 0.886 (lic): still a loss at pilot size |
| pairaction serial licensed / repro | W3-4 P2 row actions (default) | **0.589** / **0.551** | **0.446** / **0.519** | ind 11/21 | lic 0.653 -> **1.110** / 0.579 -> 0.951; repro 0.559 -> 1.017 / 0.608 -> **1.113** |
| pairaction mt8 | the action declines under a team | -- | 0.98 / 0.97 | ind | unchanged |
| saxpy serial licensed vs r3w1 | W2-4: default joined pass / `BLADE_EPILOGUE_LEGS=1` | 1.046 [1.004, 1.180] / 0.933 | 1.079 [1.026, 1.163] / 0.993 | ind | the default SLOWER than the wave-1 binary beyond the CI (indicative, drifting block) |
| saxpy serial repro vs r3w1 | default / legs1 | 0.976 / **0.869** [0.831, 0.930] | 0.996 / **0.863** [0.835, 0.881] | ind / live | legs1: 1.367 -> **1.488** / 0.872 -> 0.952 |
| pairkernel, comoment3 (mt8 repro), gram (serial) | controls | -- / -- / 0.995 | 1.003 / 1.049 [0.971, 1.083] / 1.014 | ind | wins kept (comoment3 1.53-1.59, gram 1.36 / 1.24) |

Peak working set: groupagg 35 -> 20 MB under the one pass, hashjoin C++ 25.4 -> 5.4 MB; nothing else moved. Decisions: T3 and
P2 stay ON by default; the licensed one pass stays opt-in; T2 is proved at pilot size only (a full-size kernel and the LLVM route
are the follow-ups); the W2-4 default joined pass buys nothing over the wave-1 binary while the opt-in legs arm takes 0.86-0.93
serially -- the scope model A (G69) is the path to a default. Round 4 pivots to peak memory and bandwidth (the user's direction).

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

**The matched-permission rule (kernels/README.md §1/§3) says the two sides should be symmetric.**
Options:

- **(A) Every arm threads preparation and checksum where the language allows.** Blade: a
  `where omp` licence on the generator map's outer level and on the checksum fold / nest
  (kernels/README.md §4.4 spellings; the per-row checksum nest's outer `method_for(range<I>)` takes
  `omp(i: 1)`, the input sum a licensed fold). Consequences: the four group-A Blade
  sources change (blade-A), the smoke evidence resets (harness/README.md "Protocol and statistics" gate), and the mt8
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

The architect's recommendation is (A): it is what kernels/README.md §1's rule already says, it
exercises the compiler's own licence mechanism (the suite's thesis), and it isolates G1 /
G3 as the remaining mt8 losses instead of hiding them behind Amdahl. Until decided, the
mt8 group-A numbers are presented under (C).

### 12.2 The 3x-running-median abort on a single isolated excursion (DECIDED: B)

**Finding.** The saxpy mt8 block was lost to one run (blade-cpp-inputonly, 43 ms, 7.0x its
6 ms running median) in round 7 of 21, with both controls normal and all other runs in the
round at 5–11 ms. The rule (harness/README.md "Protocol and statistics", charter 2.5) exists to stop a thermal event; this was
a single-process stall of the smallest binary, and the rule cannot tell the two apart.

Options (the harness owner makes any change):

- **(A) Keep the rule as is** (safest for the host; costs a block per isolated stall; the
  block is re-run).
- **(B) Isolated-excursion tolerance:** a single run above 3x the running median, with
  both controls normal in that round and no second excursion in the same block, is
  DROPPED AND RECORDED (the sample stays in the bundle, marked, excluded from inference;
  the planned-cell manifest says so); the block aborts on a second excursion or on any
  control excursion. Consequences: a `harness/run.py` change, a harness/README.md "Protocol and statistics" sentence, and the
  bundle schema gains an `excluded_excursion` mark; no source changes.

The architect's recommendation is (B) with the record kept visible: the rule's purpose
(thermal safety) is served by the control check, and a 43 ms stall of a 6 ms process is
not a thermal signature. Either way, saxpy mt8 is re-run.

Until the blas session lands:

- **Dropped and recast workloads** (README §5 inventory): `band` (BL7004 under a deferred
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
  two natural arms peaked at 65.9 / 65.5 MB, ~3% over the 64 MB working-set rule (kernels/README.md
  §4.3); kept and flagged, not re-run. Not run yet: sessions 2–5.
