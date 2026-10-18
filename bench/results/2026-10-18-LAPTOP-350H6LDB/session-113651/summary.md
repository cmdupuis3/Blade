# Blade vs Fortran -- bench session 2026-10-18T11:36:51

- host LAPTOP-350H6LDB | AMD Ryzen 7 5800HS with Radeon Graphics | 8C/16T | 15.41 GB | power ac | max processor state AC 100% | boost AC None | Power Scheme GUID: 64a64f24-65b9-4b56-befd-5ec1eaced9b3  (Silent)
- git fceb895d4e8df71131086a94652b5cab1b120850 | Blade.dll 74b6728036b170d9 | GNU Fortran (Rev4, Built by MSYS2 project) 16.2.0 | Intel(R) Fortran Compiler for applications running on Intel(R) 64, Version 2026.1.0 Build 20260617 | clang version 22.1.8 (https://github.com/msys2/MINGW-packages d34feaaa1d0aff624c870910b6f2589aa40d20de)
- size `full` | duration 2118.5 s | pins: pins-A.json, pins-B.json
- PRIMARY estimand: the complete timed region. ratio = median(arm) / median(Blade reference), round-block bootstrap 95% CI (10000 resamples); ratio > 1 = Blade faster. Verdicts (delta 0.1): win / detectable-win / equivalent / detectable-loss / loss / inconclusive; `suppressed` = the block failed a drift, trend or control check. Intervals are pointwise; no suite-wide claim. incremental = median(arm) - median(its own input-only twin), NOT kernel time. Reference: blade-llvm (serial), blade-cpp (mt8, or when the LLVM lane refused). Fresh-process, allocation-inclusive measurements.
- **FLAG**: antisym/mt8-licensed-native: inference suppressed: trend: gfortran-expert first-half vs second-half median moved 7.2%; trend: control compute moved 22.9% within the block; trend: control memory moved 8.8% within the block
- **FLAG**: comoment3/mt8-licensed-native: inference suppressed: control compute median 0.01292s moved -8.9% vs the session baseline 0.01418s; control memory median 0.005397s moved -11.0% vs the session baseline 0.006067s
- **FLAG**: gram/mt8-licensed-native: inference suppressed: trend: blade-cpp first-half vs second-half median moved 10.7%; trend: gfortran-natural first-half vs second-half median moved 8.7%; trend: ifx-expert first-half vs second-half median moved 5.9%; control compute median 0.01283s moved -9.5% vs the session baseline 0.01418s; control memory median 0.005374s moved -11.4% vs the session baseline 0.006067s
- **FLAG**: pairkernel/mt8-licensed-native: inference suppressed: trend: gfortran-natural first-half vs second-half median moved 7.3%; trend: ifx-natural first-half vs second-half median moved 8.0%; control compute median 0.01313s moved -7.4% vs the session baseline 0.01418s; trend: control compute moved 9.9% within the block; control memory median 0.005364s moved -11.6% vs the session baseline 0.006067s
- **FLAG**: saxpy/mt8-licensed-native: inference suppressed: trend: blade-cpp first-half vs second-half median moved 6.2%; trend: blade-cpp-inputonly first-half vs second-half median moved 11.6%; trend: gfortran-expert-inputonly first-half vs second-half median moved 9.6%; control compute median 0.01293s moved -8.8% vs the session baseline 0.01418s; control memory median 0.005433s moved -10.4% vs the session baseline 0.006067s

## mt8-licensed-native

Blade reference per kernel: antisym=blade-cpp, comoment3=blade-cpp, gram=blade-cpp, pairkernel=blade-cpp, saxpy=blade-cpp

| kernel | arm | status | n | min (s) | median (s) | IQR (s) | ratio vs ref [95% CI] | verdict | hypothesis | fold form | parallelism | peak WS (MB) | incremental (s) [CI] | incremental ratio [CI] | notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| antisym | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (7 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| antisym | blade-cpp | ok | 21 | 0.02098 | 0.02258 | 0.002201 | ref |  |  | reordered (6 SIMD_REDUCTION) | openmp (4 construct(s)) | 18.7 |  |  |  |
| antisym | gfortran-natural | ok | 21 | 0.1093 | 0.1169 | 0.003743 | 5.177 [5.060, 5.273] | suppressed | WIN ~2x (half the cells, no diagonal) |  | openmp runtime linked | 23.8 |  |  |  |
| antisym | gfortran-expert | ok | 21 | 0.009466 | 0.01038 | 0.001237 | 0.460 [0.440, 0.478] | suppressed | parity +-15% |  | openmp runtime linked | 33.1 |  |  |  |
| antisym | ifx-natural | ok | 21 | 0.06468 | 0.07139 | 0.009553 | 3.162 [3.009, 3.386] | suppressed | WIN ~2x (half the cells, no diagonal) |  | openmp runtime linked | 25.4 |  |  |  |
| antisym | ifx-expert | ok | 21 | 0.009988 | 0.0109 | 0.001045 | 0.483 [0.460, 0.515] | suppressed | parity +-15% |  | openmp runtime linked | 34.6 |  |  |  |

> antisym mt8-licensed-native: inference suppressed -- trend: gfortran-expert first-half vs second-half median moved 7.2%; trend: control compute moved 22.9% within the block; trend: control memory moved 8.8% within the block

| comoment3 | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (6 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| comoment3 | blade-cpp | ok | 21 | 0.02001 | 0.02062 | 0.0008745 | ref |  |  | reordered (4 SIMD_REDUCTION) | openmp (4 construct(s)) | 12.6 |  |  |  |
| comoment3 | gfortran-natural | ok | 21 | 0.08373 | 0.09194 | 0.004281 | 4.458 [4.295, 4.526] | suppressed | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | openmp runtime linked | 43.9 |  |  |  |
| comoment3 | gfortran-expert | ok | 21 | 0.004615 | 0.004998 | 0.0002994 | 0.242 [0.233, 0.248] | suppressed | parity to 1.3x loss |  | openmp runtime linked | 14.5 |  |  |  |
| comoment3 | ifx-natural | ok | 21 | 0.1015 | 0.1115 | 0.006531 | 5.406 [5.257, 5.613] | suppressed | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | openmp runtime linked | 45.4 |  |  |  |
| comoment3 | ifx-expert | ok | 21 | 0.003796 | 0.004025 | 0.0001324 | 0.195 [0.191, 0.199] | suppressed | parity to 1.3x loss |  | openmp runtime linked | 15.8 |  |  |  |

> comoment3 mt8-licensed-native: inference suppressed -- control compute median 0.01292s moved -8.9% vs the session baseline 0.01418s; control memory median 0.005397s moved -11.0% vs the session baseline 0.006067s

| gram | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (8 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| gram | blade-cpp | ok | 21 | 0.04006 | 0.04375 | 0.005286 | ref |  |  | reordered (3 SIMD_REDUCTION) | openmp (4 construct(s)) | 27.8 |  |  |  |
| gram | gfortran-natural | ok | 21 | 0.5084 | 0.6141 | 0.09889 | 14.034 [13.086, 14.946] | suppressed | win 1.5-2x if matmul is a naive nest; parity/loss if a blocked runtime kernel |  | openmp runtime linked | 37.3 |  |  | matmul is unannotated: parallel preparation, serial kernel unless the runtime threads matmul (FORT records) |
| gram | gfortran-expert | ok | 21 | 0.01937 | 0.02119 | 0.000815 | 0.484 [0.454, 0.515] | suppressed | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | openmp runtime linked | 52.1 |  |  |  |
| gram | ifx-natural | ok | 21 | 0.5945 | 0.6311 | 0.01628 | 14.423 [13.507, 15.089] | suppressed | win 1.5-2x if matmul is a naive nest; parity/loss if a blocked runtime kernel |  | openmp runtime linked | 58.3 |  |  | matmul is unannotated: parallel preparation, serial kernel unless the runtime threads matmul (FORT records) |
| gram | ifx-expert | ok | 21 | 0.01791 | 0.02002 | 0.001131 | 0.458 [0.427, 0.481] | suppressed | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | openmp runtime linked | 53.7 |  |  |  |

> gram mt8-licensed-native: inference suppressed -- trend: blade-cpp first-half vs second-half median moved 10.7%; trend: gfortran-natural first-half vs second-half median moved 8.7%; trend: ifx-expert first-half vs second-half median moved 5.9%; control compute median 0.01283s moved -9.5% vs the session baseline 0.01418s; control memory median 0.005374s moved -11.4% vs the session baseline 0.006067s

| pairkernel | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (4 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| pairkernel | blade-cpp | ok | 21 | 0.01424 | 0.01611 | 0.002726 | ref |  |  | reordered (1 SIMD_REDUCTION) | openmp (4 construct(s)) | 29.8 |  |  |  |
| pairkernel | gfortran-natural | ok | 21 | 0.01361 | 0.01528 | 0.002157 | 0.948 [0.877, 1.013] | suppressed | WIN ~2x (half the transcendentals) |  | openmp runtime linked | 53.3 |  |  |  |
| pairkernel | gfortran-expert | ok | 21 | 0.005746 | 0.006262 | 0.0003226 | 0.389 [0.373, 0.405] | suppressed | parity within 5% |  | openmp runtime linked | 29.5 |  |  |  |
| pairkernel | ifx-natural | ok | 21 | 0.01476 | 0.01643 | 0.00215 | 1.019 [0.964, 1.124] | suppressed | WIN ~2x (half the transcendentals) |  | openmp runtime linked | 54.8 |  |  |  |
| pairkernel | ifx-expert | ok | 21 | 0.005802 | 0.006209 | 0.0003881 | 0.385 [0.374, 0.406] | suppressed | parity within 5% |  | openmp runtime linked | 30.9 |  |  |  |

> pairkernel mt8-licensed-native: inference suppressed -- trend: gfortran-natural first-half vs second-half median moved 7.3%; trend: ifx-natural first-half vs second-half median moved 8.0%; control compute median 0.01313s moved -7.4% vs the session baseline 0.01418s; trend: control compute moved 9.9% within the block; control memory median 0.005364s moved -11.6% vs the session baseline 0.006067s

| saxpy | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (3 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| saxpy | blade-cpp | ok | 21 | 0.01049 | 0.0114 | 0.0005977 | ref |  |  | in-order | openmp (3 construct(s)) | 51.2 | 0.004429 [0.004113, 0.004699] | ref |  |
| saxpy | gfortran-natural | ok | 21 | 0.009706 | 0.01036 | 0.0006287 | 0.909 [0.876, 0.942] | suppressed | parity |  | openmp runtime linked | 51.3 | 0.004351 [0.003939, 0.004677] | 0.982 [0.877, 1.091] |  |
| saxpy | gfortran-expert | ok | 21 | 0.008286 | 0.008963 | 0.0003849 | 0.786 [0.766, 0.808] | suppressed | parity |  | openmp runtime linked | 51.3 | 0.003402 [0.002927, 0.003622] | 0.768 [0.659, 0.846] |  |
| saxpy | ifx-natural | ok | 21 | 0.009888 | 0.01059 | 0.0006783 | 0.929 [0.892, 0.963] | suppressed | parity |  | openmp runtime linked | 52.9 | 0.004279 [0.003976, 0.004611] | 0.966 [0.871, 1.068] |  |
| saxpy | ifx-expert | ok | 21 | 0.008817 | 0.009224 | 0.0003052 | 0.809 [0.792, 0.830] | suppressed | parity |  | openmp runtime linked | 52.9 | 0.003205 [0.003045, 0.003482] | 0.724 [0.679, 0.795] |  |
| saxpy | blade-llvm-inputonly | serial-only | 0 | -- | -- | -- |  |  |  | reordered (2 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| saxpy | blade-cpp-inputonly | ok | 21 | 0.005811 | 0.00697 | 0.0006486 |  |  |  | in-order | openmp (1 construct(s)) | 36.3 |  |  |  |
| saxpy | gfortran-natural-inputonly | ok | 21 | 0.00543 | 0.006012 | 0.0004447 |  |  |  |  | openmp runtime linked | 36.0 |  |  |  |
| saxpy | gfortran-expert-inputonly | ok | 21 | 0.005137 | 0.00556 | 0.0007242 |  |  |  |  | openmp runtime linked | 36.0 |  |  |  |
| saxpy | ifx-natural-inputonly | ok | 21 | 0.005915 | 0.006312 | 0.0005098 |  |  |  |  | openmp runtime linked | 37.6 |  |  |  |
| saxpy | ifx-expert-inputonly | ok | 21 | 0.005338 | 0.006019 | 0.0005122 |  |  |  |  | openmp runtime linked | 37.6 |  |  |  |

> saxpy mt8-licensed-native: inference suppressed -- trend: blade-cpp first-half vs second-half median moved 6.2%; trend: blade-cpp-inputonly first-half vs second-half median moved 11.6%; trend: gfortran-expert-inputonly first-half vs second-half median moved 9.6%; control compute median 0.01293s moved -8.8% vs the session baseline 0.01418s; control memory median 0.005433s moved -10.4% vs the session baseline 0.006067s


## Controls

- compute (recur/gfortran-expert/serial-repro-native/full): n=105 median 0.01298 s, baseline 0.01418 s
- memory (saxpy/gfortran-expert/serial-repro-native/control): n=105 median 0.005424 s, baseline 0.006067 s
