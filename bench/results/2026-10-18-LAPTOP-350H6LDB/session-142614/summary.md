# Blade vs Fortran -- bench session 2026-10-18T14:26:14

- host LAPTOP-350H6LDB | AMD Ryzen 7 5800HS with Radeon Graphics | 8C/16T | 15.41 GB | power ac | max processor state AC 100% | boost AC None | Power Scheme GUID: 64a64f24-65b9-4b56-befd-5ec1eaced9b3  (Silent)
- git fceb895d4e8df71131086a94652b5cab1b120850 | Blade.dll 74b6728036b170d9 | GNU Fortran (Rev4, Built by MSYS2 project) 16.2.0 | Intel(R) Fortran Compiler for applications running on Intel(R) 64, Version 2026.1.0 Build 20260617 | clang version 22.1.8 (https://github.com/msys2/MINGW-packages d34feaaa1d0aff624c870910b6f2589aa40d20de)
- size `full` | duration 844.9 s | pins: pins-A.json, pins-B.json
- PRIMARY estimand: the complete timed region. ratio = median(arm) / median(Blade reference), round-block bootstrap 95% CI (10000 resamples); ratio > 1 = Blade faster. Verdicts (delta 0.1): win / detectable-win / equivalent / detectable-loss / loss / inconclusive; `suppressed` = the block failed a drift, trend or control check. Intervals are pointwise; no suite-wide claim. incremental = median(arm) - median(its own input-only twin), NOT kernel time. Reference: blade-llvm (serial), blade-cpp (mt8, or when the LLVM lane refused). Fresh-process, allocation-inclusive measurements.
- **FLAG**: comoment3/serial-fastmath-native: inference suppressed: trend: ifx-natural first-half vs second-half median moved 8.1%
- **FLAG**: gram/serial-fastmath-native: inference suppressed: trend: ifx-natural first-half vs second-half median moved 6.9%
- **FLAG**: onepass/serial-fastmath-native: inference suppressed: trend: ifx-expert first-half vs second-half median moved 5.8%; trend: gfortran-expert-inputonly first-half vs second-half median moved 9.2%; trend: ifx-natural-inputonly first-half vs second-half median moved 6.9%

## serial-fastmath-native

Blade reference per kernel: antisym=blade-llvm, comoment3=blade-llvm, gram=blade-llvm, fusedchain=blade-llvm, onepass=blade-llvm, dot=blade-llvm

| kernel | arm | status | n | min (s) | median (s) | IQR (s) | ratio vs ref [95% CI] | verdict | hypothesis | fold form | parallelism | peak WS (MB) | incremental (s) [CI] | incremental ratio [CI] | notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| antisym | blade-llvm | ok | 11 | 0.0732 | 0.07376 | 0.0005587 | ref |  |  | reordered (7 reassoc flags) | serial | 17.4 |  |  |  |
| antisym | blade-cpp | ok | 11 | 0.1072 | 0.1087 | 0.001707 | 1.474 [1.463, 1.491] | win |  | reordered (7 SIMD_REDUCTION) | serial | 18.3 |  |  |  |
| antisym | gfortran-natural | ok | 11 | 0.1279 | 0.1299 | 0.003497 | 1.761 [1.749, 1.815] | win | WIN ~2x (half the cells, no diagonal) |  | serial | 23.4 |  |  |  |
| antisym | gfortran-expert | ok | 11 | 0.02981 | 0.03041 | 0.0008583 | 0.412 [0.408, 0.422] | loss | parity +-15% |  | serial | 32.7 |  |  |  |
| antisym | ifx-natural | ok | 11 | 0.08132 | 0.08427 | 0.002625 | 1.142 [1.113, 1.158] | win | WIN ~2x (half the cells, no diagonal) |  | serial | 23.0 |  |  |  |
| antisym | ifx-expert | ok | 11 | 0.02884 | 0.02925 | 0.0003386 | 0.397 [0.395, 0.403] | loss | parity +-15% |  | serial | 32.2 |  |  |  |
| comoment3 | blade-llvm | ok | 11 | 0.04048 | 0.04104 | 0.0005982 | ref |  |  | reordered (6 reassoc flags) | serial | 11.3 |  |  |  |
| comoment3 | blade-cpp | ok | 11 | 0.1069 | 0.1075 | 0.0003849 | 2.620 [2.607, 2.652] | suppressed |  | reordered (6 SIMD_REDUCTION) | serial | 12.2 |  |  |  |
| comoment3 | gfortran-natural | ok | 11 | 0.1442 | 0.1445 | 0.0003535 | 3.521 [3.505, 3.563] | suppressed | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | serial | 43.4 |  |  |  |
| comoment3 | gfortran-expert | ok | 11 | 0.01103 | 0.01131 | 0.0002293 | 0.276 [0.272, 0.279] | suppressed | parity to 1.3x loss |  | serial | 12.3 |  |  |  |
| comoment3 | ifx-natural | ok | 11 | 0.07878 | 0.07997 | 0.007481 | 1.949 [1.934, 2.138] | suppressed | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | serial | 43.0 |  |  |  |
| comoment3 | ifx-expert | ok | 11 | 0.01101 | 0.01126 | 0.0002701 | 0.274 [0.272, 0.280] | suppressed | parity to 1.3x loss |  | serial | 11.9 |  |  |  |

> comoment3 serial-fastmath-native: inference suppressed -- trend: ifx-natural first-half vs second-half median moved 8.1%

| gram | blade-llvm | ok | 11 | 0.1504 | 0.1506 | 0.0008909 | ref |  |  | reordered (8 reassoc flags) | serial | 26.6 |  |  |  |
| gram | blade-cpp | ok | 11 | 0.2437 | 0.2469 | 0.002782 | 1.640 [1.623, 1.648] | suppressed |  | reordered (5 SIMD_REDUCTION) | serial | 27.3 |  |  |  |
| gram | gfortran-natural | ok | 11 | 0.4432 | 0.4668 | 0.029 | 3.099 [3.021, 3.276] | suppressed | win 1.5-2x if matmul is a naive nest; parity/loss if a blocked runtime kernel |  | serial | 36.9 |  |  |  |
| gram | gfortran-expert | ok | 11 | 0.07499 | 0.07585 | 0.0003758 | 0.504 [0.499, 0.507] | suppressed | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | serial | 51.6 |  |  |  |
| gram | ifx-natural | ok | 11 | 0.1951 | 0.198 | 0.01743 | 1.315 [1.296, 1.419] | suppressed | win 1.5-2x if matmul is a naive nest; parity/loss if a blocked runtime kernel |  | serial | 55.9 |  |  |  |
| gram | ifx-expert | ok | 11 | 0.06896 | 0.06966 | 0.0008084 | 0.463 [0.458, 0.466] | suppressed | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | serial | 51.2 |  |  |  |

> gram serial-fastmath-native: inference suppressed -- trend: ifx-natural first-half vs second-half median moved 6.9%

| fusedchain | blade-llvm | ok | 21 | 0.00426 | 0.004519 | 0.0003579 | ref |  |  | reordered (2 reassoc flags) | serial | 19.8 | 0.000844 [0.0006553, 0.001016] | ref |  |
| fusedchain | blade-cpp | ok | 21 | 0.0064 | 0.006576 | 0.0002441 | 1.455 [1.402, 1.526] | win |  | reordered (1 SIMD_REDUCTION) | serial | 20.6 | 0.0006663 [0.0006135, 0.0008247] | 0.789 [0.656, 1.139] |  |
| fusedchain | gfortran-natural | ok | 21 | 0.01481 | 0.01529 | 0.0003396 | 3.383 [3.259, 3.521] | win | WIN 1.5-2.5x (no temporaries) |  | serial | 66.1 | 0.009289 [0.009001, 0.009387] | 11.007 [9.083, 14.044] |  |
| fusedchain | gfortran-expert | ok | 21 | 0.006522 | 0.0067 | 0.0001448 | 1.483 [1.428, 1.560] | win | parity |  | serial | 20.3 | 0.0006904 [0.0006143, 0.0008363] | 0.818 [0.666, 1.170] |  |
| fusedchain | ifx-natural | ok | 21 | 0.00935 | 0.009583 | 0.0004148 | 2.121 [2.043, 2.214] | win | WIN 1.5-2.5x (no temporaries) |  | serial | 65.6 | 0.006722 [0.006615, 0.00692] | 7.965 [6.622, 10.263] |  |
| fusedchain | ifx-expert | ok | 21 | 0.003593 | 0.003672 | 9.64e-05 | 0.813 [0.786, 0.845] | loss | parity |  | serial | 19.8 | 0.0008207 [0.0007488, 0.000887] | 0.972 [0.797, 1.242] |  |
| fusedchain | blade-llvm-inputonly | ok | 21 | 0.003508 | 0.003675 | 0.0001367 |  |  |  | reordered (1 reassoc flags) | serial | 19.8 |  |  |  |
| fusedchain | blade-cpp-inputonly | ok | 21 | 0.005781 | 0.00591 | 0.0001563 |  |  |  | in-order | serial | 20.6 |  |  |  |
| fusedchain | gfortran-natural-inputonly | ok | 21 | 0.005775 | 0.005997 | 0.0002062 |  |  |  |  | serial | 20.3 |  |  |  |
| fusedchain | gfortran-expert-inputonly | ok | 21 | 0.005919 | 0.00601 | 0.0001547 |  |  |  |  | serial | 20.3 |  |  |  |
| fusedchain | ifx-natural-inputonly | ok | 21 | 0.00282 | 0.00286 | 7.79e-05 |  |  |  |  | serial | 19.8 |  |  |  |
| fusedchain | ifx-expert-inputonly | ok | 21 | 0.002787 | 0.002852 | 9.23e-05 |  |  |  |  | serial | 19.8 |  |  |  |
| onepass | blade-llvm | ok | 21 | 0.01115 | 0.01176 | 0.0007423 | ref |  |  | reordered (5 reassoc flags) | serial | 42.7 | 0.001999 [0.001689, 0.002457] | ref |  |
| onepass | blade-cpp | ok | 21 | 0.01636 | 0.01706 | 0.0008434 | 1.450 [1.408, 1.496] | suppressed |  | reordered (1 SIMD_REDUCTION) | serial | 43.4 | 0.002163 [0.00184, 0.002571] | 1.082 [0.841, 1.313] |  |
| onepass | gfortran-natural | ok | 21 | 0.01986 | 0.02058 | 0.0008113 | 1.750 [1.686, 1.798] | suppressed | WIN 2-3x (one pass vs four) |  | serial | 43.2 | 0.005687 [0.005014, 0.006067] | 2.844 [2.199, 3.437] |  |
| onepass | gfortran-expert | ok | 21 | 0.01646 | 0.01699 | 0.0009041 | 1.445 [1.392, 1.493] | suppressed | parity |  | serial | 43.2 | 0.001934 [0.001343, 0.002457] | 0.967 [0.669, 1.248] |  |
| onepass | ifx-natural | ok | 21 | 0.007902 | 0.008199 | 0.0004965 | 0.697 [0.677, 0.731] | suppressed | WIN 2-3x (one pass vs four) |  | serial | 42.7 | 0.0008068 [0.0003997, 0.001148] | 0.404 [0.187, 0.577] |  |
| onepass | ifx-expert | ok | 21 | 0.008098 | 0.008435 | 0.0006475 | 0.717 [0.696, 0.746] | suppressed | parity |  | serial | 42.7 | 0.0007231 [0.000542, 0.001031] | 0.362 [0.267, 0.505] |  |
| onepass | blade-llvm-inputonly | ok | 21 | 0.009316 | 0.009763 | 0.0004851 |  |  |  | reordered (1 reassoc flags) | serial | 42.7 |  |  |  |
| onepass | blade-cpp-inputonly | ok | 21 | 0.01443 | 0.0149 | 0.0004507 |  |  |  | in-order | serial | 43.5 |  |  |  |
| onepass | gfortran-natural-inputonly | ok | 21 | 0.01454 | 0.0149 | 0.000603 |  |  |  |  | serial | 43.2 |  |  |  |
| onepass | gfortran-expert-inputonly | ok | 21 | 0.0145 | 0.01506 | 0.001108 |  |  |  |  | serial | 43.2 |  |  |  |
| onepass | ifx-natural-inputonly | ok | 21 | 0.007126 | 0.007392 | 0.0005937 |  |  |  |  | serial | 42.7 |  |  |  |
| onepass | ifx-expert-inputonly | ok | 21 | 0.007136 | 0.007712 | 0.0003866 |  |  |  |  | serial | 42.7 |  |  |  |

> onepass serial-fastmath-native: inference suppressed -- trend: ifx-expert first-half vs second-half median moved 5.8%; trend: gfortran-expert-inputonly first-half vs second-half median moved 9.2%; trend: ifx-natural-inputonly first-half vs second-half median moved 6.9%

| dot | blade-llvm | ok | 21 | 0.01264 | 0.01315 | 0.0003948 | ref |  |  | reordered (3 reassoc flags) | serial | 50.3 | 0.001819 [0.001407, 0.001994] | ref |  |
| dot | blade-cpp | ok | 21 | 0.01877 | 0.01937 | 0.0006723 | 1.472 [1.441, 1.512] | win |  | reordered (1 SIMD_REDUCTION) | serial | 51.1 | 0.001669 [0.001248, 0.001919] | 0.917 [0.663, 1.290] |  |
| dot | gfortran-natural | ok | 21 | 0.01792 | 0.01813 | 0.0005638 | 1.379 [1.364, 1.428] | win | parity |  | serial | 50.8 | 0.00125 [0.0009812, 0.001671] | 0.687 [0.517, 1.049] |  |
| dot | gfortran-expert | ok | 21 | 0.01784 | 0.01816 | 0.0005324 | 1.381 [1.368, 1.432] | win | parity |  | serial | 50.8 | 0.001346 [0.001075, 0.001738] | 0.740 [0.587, 1.108] |  |
| dot | ifx-natural | ok | 21 | 0.01208 | 0.01263 | 0.0005146 | 0.960 [0.936, 0.985] | detectable-loss | parity |  | serial | 50.4 | 0.003737 [0.00345, 0.004068] | 2.055 [1.824, 2.705] |  |
| dot | ifx-expert | ok | 21 | 0.008485 | 0.008914 | 0.0003659 | 0.678 [0.667, 0.700] | loss | parity |  | serial | 50.3 | 2.88e-05 [-0.000324, 0.0003421] | 0.016 [-0.202, 0.191] |  |
| dot | blade-llvm-inputonly | ok | 21 | 0.01095 | 0.01133 | 0.0005224 |  |  |  | reordered (2 reassoc flags) | serial | 50.3 |  |  |  |
| dot | blade-cpp-inputonly | ok | 21 | 0.01739 | 0.0177 | 0.0004538 |  |  |  | in-order | serial | 51.1 |  |  |  |
| dot | gfortran-natural-inputonly | ok | 21 | 0.01655 | 0.01688 | 0.000342 |  |  |  |  | serial | 50.8 |  |  |  |
| dot | gfortran-expert-inputonly | ok | 21 | 0.01649 | 0.01681 | 0.0003467 |  |  |  |  | serial | 50.8 |  |  |  |
| dot | ifx-natural-inputonly | ok | 21 | 0.008407 | 0.008895 | 0.0005934 |  |  |  |  | serial | 50.3 |  |  |  |
| dot | ifx-expert-inputonly | ok | 21 | 0.008417 | 0.008885 | 0.0005986 |  |  |  |  | serial | 50.3 |  |  |  |

## Controls

- compute (recur/gfortran-expert/serial-repro-native/full): n=96 median 0.01281 s, baseline 0.01271 s
- memory (saxpy/gfortran-expert/serial-repro-native/control): n=96 median 0.005295 s, baseline 0.005271 s
