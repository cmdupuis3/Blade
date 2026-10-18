# Blade vs gfortran / ifx — results

**Status (2026-10-18):** sessions **1a** (serial x licensed x native: gram, comoment3,
antisym, pairkernel, recur, stencil; `session-042223/`) and **1b** (serial x licensed x
native: fusedchain, onepass, maskagg, dot, saxpy + twins; `session-090344/`) are IN —
the headline regime is complete for all eleven kernels. Pending: mt8 x licensed (next),
repro (serial, mt8), fastmath, blas. The final summary, headline, and phase-2 worklist
(§11) are written when the blas session lands.

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

| session | date | bundle | regimes x kernels | rounds | duration | failures / DQ / fallbacks | longest run | peak working set | suppressed blocks |
|---|---|---|---|---|---|---|---|---|---|
| pilot | 2026-10-17 | `results/2026-10-17-LAPTOP-350H6LDB/session-231651/` | all 11, serial-licensed-native | 3 | — | 0 / 0 / 0 | — | — | 7 of 11 |
| **1a** | 2026-10-18 | `results/2026-10-18-LAPTOP-350H6LDB/session-042223/` | serial-licensed-native: gram, comoment3, antisym, pairkernel, recur, stencil | 11 (gram, comoment3, antisym, pairkernel), 21 (recur, stencil) | 1975.8 s | 0 / 0 / 0 (`failures: []`) | 0.7728 s (antisym gfortran-natural median; max single run per bundle) | 55.7 MB (gram ifx-natural) | 5 of 6 (antisym clean) |
| **1b** | 2026-10-18 | `results/2026-10-18-LAPTOP-350H6LDB/session-090344/` | serial-licensed-native: fusedchain, onepass, maskagg, dot, saxpy + twins | 21 each | 3281.9 s | 0 / 0 / 1 expected (maskagg blade-llvm: LLVM lane refuses `compound`; blade-cpp is the reference) | 0.0672 s (maskagg blade-cpp median; all arms < 0.083 s) | **65.9 MB (fusedchain gfortran-natural) and 65.5 MB (ifx-natural) — over the 64 MB rule**; all other arms ≤ 50.9 MB | 4 of 5 (onepass clean) |
| 2 | — | unrun | mt8 x licensed x native | | | | | | |
| 3 | — | unrun | repro (serial, mt8) | | | | | | |
| 4 | — | unrun | blas (gram) | | | | | | |
| 5 | — | unrun | fastmath | | | | | | |

## 3. serial x licensed x native (headline regime) — session 1a

Reference for every kernel in this block: `blade-llvm`. Columns: n, min / median / IQR in
ms, ratio vs reference [95% CI], verdict, hypothesis (from the spec), fold form,
parallelism, peak working set, attribution, notes. Block flags are quoted from the bundle.

### 3.1 antisym — CLEAN block (verdicts stand)

| arm | n | min (ms) | median (ms) | IQR (ms) | ratio [95% CI] | verdict | hypothesis | fold form | parallelism | peak WS (MB) | attribution | notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| blade-llvm | 11 | 73.98 | 74.55 | 1.169 | ref | — | — | reordered (7 reassoc flags) | serial | 17.3 | checksum share ≈ small (A §13) | |
| blade-cpp | 11 | 108.6 | 109.3 | 0.808 | 1.466 [1.437, 1.481] | win (C++ lane 1.47x slower than LLVM lane) | both lanes reported | reordered (6 SIMD_REDUCTION) | serial | 18.1 | | G26 (single-accumulator simd reduction) + G3b (std::sort canonicalizing read); pilot 1.54 |
| gfortran-natural | 11 | 770.6 | 772.8 | 3.259 | 10.366 [10.187, 10.406] | **win** | WIN ~2x | — | serial | 23.2 | | dense baseline, both inner products per cell |
| gfortran-expert | 11 | 30.00 | 31.01 | 0.805 | 0.416 [0.406, 0.418] | **loss** | equivalence to modest loss | — | serial | 32.5 | | G1 |
| ifx-natural | 11 | 331.3 | 332.2 | 0.922 | 4.456 [4.373, 4.471] | **win** | WIN ~2x | — | serial | 22.8 | | |
| ifx-expert | 11 | 28.85 | 29.43 | 0.661 | 0.395 [0.388, 0.403] | **loss** | equivalence to modest loss | — | serial | 32.1 | | G1 |

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

## 5. mt8 x licensed x native — session 2

`unrun`. Denominator `blade-cpp`; `blade-llvm` rows "serial only (LLVM lane has no
threads)"; `onepass` and `recur` Blade rows "no parallel form"; `maskagg` serial by
protocol; parallelism from the per-loop evidence (`fold form:` lines; G25 for chunk
interiors); unbound threads, team creation inside the region (D42).

## 6. repro — session 3

`unrun`. serial: the association-reproducible opt-in (single-chain dots in group A; the
pre-flip hypotheses). mt8: Blade = threaded maps, serial folds; Fortran keeps thread-level
`reduction(+:)` (regime asymmetry, README §3).

## 7. blas (gram) — session 4; fastmath — session 5

`unrun`. blas arms: blade-cpp (auto-routed dsyrk), gfortran-expert, ifx-expert; blade-llvm
"no-blas"; naturals excluded; mt8 = library threads. fastmath label: "vendor fast-math vs
Blade's reassociation licence — unequal permission sets, accuracy-checked at 1e-8".

## 8. Controls record

| session | compute control (recur gfortran-expert, serial x repro, full) | stream control (saxpy gfortran-expert, serial x repro, n = 700,001) | source |
|---|---|---|---|
| pilot | CV 8.5%, +24% excursion in one block | — | coordinator's pilot note; bundle `session-231651/` |
| 1a | n = 86 valid rounds, median 13.42 ms, baseline (first block) 12.75 ms; CV 9.3% over the 87 ok samples; min 12.51, max 17.61 ms; 0 invalid | n = 86, median 5.505 ms, baseline 5.246 ms; CV 9.9% over 87 ok samples; min 5.12, max 7.45 ms; 0 invalid | `summary.md` Controls; CVs computed from `session.json` `controls[].samples[].time_s` |
| 1b | n = 105, median 13.05 ms, baseline 13.12 ms; CV 9.8% over 106 ok samples; min 12.43, max 20.67 ms; 0 invalid | n = 105, median 5.404 ms, baseline 5.398 ms; CV 9.2% over 106 ok samples; min 5.13, max 8.28 ms; 0 invalid | same |

The two controls diagnose different things: the compute control tracks core clock and
thermal state, the stream control memory-subsystem contention (README §6). In session 1a
both moved 11–16% above the session baseline during the comoment3, gram and pairkernel
blocks (the rolling-control rule, threshold 7%) and showed 7–19% within-block trends
elsewhere (threshold 5%); antisym's block was the only one inside both thresholds. In
session 1b the controls' medians sat on their baselines (13.05 vs 13.12 ms; 5.40 vs 5.40
ms) and no block tripped the rolling rule; the four suppressions were within-block
trends of individual arms (5.6–14.8%) and, in the dot and saxpy blocks, of a control
(8.3% compute, 5.2% memory). onepass was the clean block.

## 9. Hypotheses vs outcomes (filled as sessions land)

| kernel | hypothesis (vs dense baseline / vs expert, licensed serial) | outcome | mismatch | diagnosis / gap |
|---|---|---|---|---|
| gram | loss or equivalent / equivalence to modest loss | 1a indicative: 3.8 & 3.7 (Blade faster) / 0.52 & 0.47 | both sides: baseline slower than assumed, expert loss ~2x | G1, G26; matmul routes recorded |
| comoment3 | WIN 4–6x / equivalence to modest loss | 1a indicative: 14.5 & 17.4 / 0.28 & 0.28 | both sides | G1b, G3a, G3b |
| antisym | WIN ~2x / equivalence to modest loss | 1a: 10.4 & 4.46 win / 0.416 & 0.395 loss | both sides | G1, G26, G3b |
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
| antisym vs both experts (0.416 / 0.395, verdict loss) | per-cell operand reloads, 2 loads/FMA vs 0.67; 7.7 / 5.0 vs 16.7–18.6 G FMA/s | G1 | open |
| gram vs experts (0.52 / 0.47, indicative) | same; C++ lane adds the single-accumulator simd reduction | G1, G26 | open |
| comoment3 vs experts (0.28 / 0.28, indicative) | multiply + FMA per term with 3 loads vs hoisted pair product; checksum share 36% / 70% | G1b, G3a, G3b | open |
| pairkernel vs experts (0.77 / 0.72, indicative) | 6.27M-read logical checksum; kernel equivalent | G3a, G2 | open |
| recur vs experts and baselines (0.09–0.17, indicative) | per-step allocator page faults (eight 160 KB temporaries) | G6, G8 | open |
| C++ lane vs LLVM lane on every group-A kernel (1.15–2.64) | single-accumulator simd reduction; out-of-line canonicalizing read | G26, G3b | open |
| maskagg vs both tiers (0.043–0.157, indicative; session 1b) | one `unordered_map` insert per kept cell (~85% of the time), Bool mask pass, `y*y` temporary; LLVM lane refuses `mask`/`compound` | G9, G12 | open |
| C++ lane vs LLVM lane on group B / controls (1.25–1.48, indicative) | mostly INPUT GENERATION (the twins differ by the same factor: 6.1 vs 3.9, 14.8 vs 9.5, 17.7 vs 11.5, 12.1 vs 8.2 ms); the folds' incrementals are equivalent | generator lowering in the C++ lane (not yet a register entry; the kernel folds are G26-free after the flip) | to be entered after the mt8 session |

Fixed before the sessions and reflected in the sources: G19, G20, G21 (5f4b33ea); partly:
G7, G14.

## 11. Headline, inventory, honesty notes, phase-2 worklist

Headline and final honesty notes are written when the final (blas) session lands. The
phase-2 worklist is already settled by the planning pair (opus + Codex, no execution):
**`notes/phase2-microkernel-plan.md`** — 12 ranked items with the deciding emission
site, the arithmetic contract each may change, and Codex's two attack rounds (its §7).
Its order and forecast gains, reproduced here; **the "expected after" figures are
arithmetic FORECASTS from the measured rates of sessions 1a/1b and blade-A §13, not
measurements — the bench tests them**:

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

Plan-level one-page forecast (its §0): gram ~85–95 both lanes (vs experts 73–82);
antisym ~33–37 (vs 29–31); comoment3 ~13–16 (vs 11.5–11.7); pairkernel ~32–34 (vs 31–33);
recur ~20–30 → ~15–25 → ~11–13 (vs 10.5–11.3); stencil unchanged. **Caveat carried from
the plan (§3):** a tile is PROVEN bitwise-identical to the serial reference only under
`BLADE_FP_CONTRACT=off` (no operation can be fused); under the default fast contraction
each compiler may fuse differently, so bitwise agreement must be VERIFIED per compiler
and mode (disassembly: `vfmadd` counts in the tile and in the serial reference agree) —
"bitwise by construction" is not claimed for the default build. What the two planners
agree is still unproven needs the bench, not more planning (plan §7, last section).

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
