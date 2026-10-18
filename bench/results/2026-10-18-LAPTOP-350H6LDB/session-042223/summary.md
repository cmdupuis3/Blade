# Blade vs Fortran -- bench session 2026-10-18T04:22:23

- host LAPTOP-350H6LDB | AMD Ryzen 7 5800HS with Radeon Graphics | 8C/16T | 15.41 GB | power ac | max processor state AC 100% | boost AC None | Power Scheme GUID: 64a64f24-65b9-4b56-befd-5ec1eaced9b3  (Silent)
- git 927776980d0669edd6b9cc822833ce119dcaabd4 | Blade.dll 74b6728036b170d9 | GNU Fortran (Rev4, Built by MSYS2 project) 16.2.0 | Intel(R) Fortran Compiler for applications running on Intel(R) 64, Version 2026.1.0 Build 20260617 | clang version 22.1.8 (https://github.com/msys2/MINGW-packages d34feaaa1d0aff624c870910b6f2589aa40d20de)
- size `full` | duration 1975.8 s | pins: pins-A.json, pins-B.json
- PRIMARY estimand: the complete timed region. ratio = median(arm) / median(Blade reference), round-block bootstrap 95% CI (10000 resamples); ratio > 1 = Blade faster. Verdicts (delta 0.1): win / detectable-win / equivalent / detectable-loss / loss / inconclusive; `suppressed` = the block failed a drift, trend or control check. Intervals are pointwise; no suite-wide claim. incremental = median(arm) - median(its own input-only twin), NOT kernel time. Reference: blade-llvm (serial), blade-cpp (mt8, or when the LLVM lane refused). Fresh-process, allocation-inclusive measurements.
- **FLAG**: comoment3/serial-licensed-native: inference suppressed: control compute median 0.01449s moved +13.7% vs the session baseline 0.01275s; trend: control compute moved 14.5% within the block; control memory median 0.005845s moved +11.4% vs the session baseline 0.005246s; trend: control memory moved 18.9% within the block
- **FLAG**: gram/serial-licensed-native: inference suppressed: trend: gfortran-natural first-half vs second-half median moved 9.8%; control compute median 0.01479s moved +16.0% vs the session baseline 0.01275s; control memory median 0.005843s moved +11.4% vs the session baseline 0.005246s; trend: control memory moved 5.6% within the block
- **FLAG**: pairkernel/serial-licensed-native: inference suppressed: control compute median 0.01415s moved +11.0% vs the session baseline 0.01275s
- **FLAG**: recur/serial-licensed-native: inference suppressed: trend: blade-cpp first-half vs second-half median moved 6.1%; trend: gfortran-natural first-half vs second-half median moved 10.9%; trend: gfortran-expert first-half vs second-half median moved 12.7%; trend: ifx-natural first-half vs second-half median moved 7.1%; trend: ifx-expert first-half vs second-half median moved 17.1%; trend: control compute moved 7.9% within the block; trend: control memory moved 7.2% within the block
- **FLAG**: stencil/serial-licensed-native: inference suppressed: trend: blade-llvm first-half vs second-half median moved 25.1%; trend: blade-cpp first-half vs second-half median moved 18.0%; trend: gfortran-natural first-half vs second-half median moved 13.4%; trend: gfortran-expert first-half vs second-half median moved 15.3%; trend: ifx-expert first-half vs second-half median moved 28.9%; trend: blade-llvm-inputonly first-half vs second-half median moved 17.5%; trend: blade-cpp-inputonly first-half vs second-half median moved 6.8%; trend: gfortran-expert-inputonly first-half vs second-half median moved 10.7%; trend: ifx-natural-inputonly first-half vs second-half median moved 12.7%; trend: ifx-expert-inputonly first-half vs second-half median moved 7.3%; trend: control compute moved 14.0% within the block; trend: control memory moved 6.1% within the block

## serial-licensed-native

Blade reference per kernel: antisym=blade-llvm, comoment3=blade-llvm, gram=blade-llvm, pairkernel=blade-llvm, recur=blade-llvm, stencil=blade-llvm

| kernel | arm | status | n | min (s) | median (s) | IQR (s) | ratio vs ref [95% CI] | verdict | hypothesis | fold form | parallelism | peak WS (MB) | incremental (s) [CI] | incremental ratio [CI] | notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| antisym | blade-llvm | ok | 11 | 0.07398 | 0.07455 | 0.001169 | ref |  |  | reordered (7 reassoc flags) | serial | 17.3 |  |  |  |
| antisym | blade-cpp | ok | 11 | 0.1086 | 0.1093 | 0.0008076 | 1.466 [1.437, 1.481] | win |  | reordered (6 SIMD_REDUCTION) | serial | 18.1 |  |  |  |
| antisym | gfortran-natural | ok | 11 | 0.7706 | 0.7728 | 0.003259 | 10.366 [10.187, 10.406] | win | WIN ~2x (half the cells, no diagonal) |  | serial | 23.2 |  |  |  |
| antisym | gfortran-expert | ok | 11 | 0.03 | 0.03101 | 0.000805 | 0.416 [0.406, 0.418] | loss | parity +-15% |  | serial | 32.5 |  |  |  |
| antisym | ifx-natural | ok | 11 | 0.3313 | 0.3322 | 0.0009223 | 4.456 [4.373, 4.471] | win | WIN ~2x (half the cells, no diagonal) |  | serial | 22.8 |  |  |  |
| antisym | ifx-expert | ok | 11 | 0.02885 | 0.02943 | 0.0006614 | 0.395 [0.388, 0.403] | loss | parity +-15% |  | serial | 32.1 |  |  |  |
| comoment3 | blade-llvm | ok | 11 | 0.04043 | 0.04149 | 0.00117 | ref |  |  | reordered (6 reassoc flags) | serial | 11.1 |  |  |  |
| comoment3 | blade-cpp | ok | 11 | 0.1087 | 0.1097 | 0.0006488 | 2.644 [2.602, 2.690] | suppressed |  | reordered (6 SIMD_REDUCTION) | serial | 12.0 |  |  |  |
| comoment3 | gfortran-natural | ok | 11 | 0.6019 | 0.6034 | 0.002195 | 14.541 [14.307, 14.801] | suppressed | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | serial | 43.3 |  |  |  |
| comoment3 | gfortran-expert | ok | 11 | 0.01114 | 0.01149 | 0.000314 | 0.277 [0.272, 0.285] | suppressed | parity to 1.3x loss |  | serial | 12.1 |  |  |  |
| comoment3 | ifx-natural | ok | 11 | 0.7202 | 0.7237 | 0.003941 | 17.442 [17.209, 17.733] | suppressed | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | serial | 42.9 |  |  |  |
| comoment3 | ifx-expert | ok | 11 | 0.01148 | 0.0117 | 0.0003876 | 0.282 [0.277, 0.293] | suppressed | parity to 1.3x loss |  | serial | 11.7 |  |  |  |

> comoment3 serial-licensed-native: inference suppressed -- control compute median 0.01449s moved +13.7% vs the session baseline 0.01275s; trend: control compute moved 14.5% within the block; control memory median 0.005845s moved +11.4% vs the session baseline 0.005246s; trend: control memory moved 18.9% within the block

| gram | blade-llvm | ok | 11 | 0.1519 | 0.1567 | 0.00612 | ref |  |  | reordered (8 reassoc flags) | serial | 26.4 |  |  |  |
| gram | blade-cpp | ok | 11 | 0.2508 | 0.2752 | 0.02033 | 1.756 [1.645, 1.828] | suppressed |  | reordered (5 SIMD_REDUCTION) | serial | 27.2 |  |  |  |
| gram | gfortran-natural | ok | 11 | 0.4808 | 0.6016 | 0.08762 | 3.838 [3.186, 3.988] | suppressed | win 1.5-2x if matmul is a naive nest; parity/loss if a blocked runtime kernel |  | serial | 36.7 |  |  |  |
| gram | gfortran-expert | ok | 11 | 0.0784 | 0.08168 | 0.002869 | 0.521 [0.507, 0.539] | suppressed | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | serial | 51.5 |  |  |  |
| gram | ifx-natural | ok | 11 | 0.5723 | 0.5762 | 0.003985 | 3.676 [3.624, 3.785] | suppressed | win 1.5-2x if matmul is a naive nest; parity/loss if a blocked runtime kernel |  | serial | 55.7 |  |  |  |
| gram | ifx-expert | ok | 11 | 0.06995 | 0.07315 | 0.002225 | 0.467 [0.461, 0.483] | suppressed | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | serial | 51.1 |  |  |  |

> gram serial-licensed-native: inference suppressed -- trend: gfortran-natural first-half vs second-half median moved 9.8%; control compute median 0.01479s moved +16.0% vs the session baseline 0.01275s; control memory median 0.005843s moved +11.4% vs the session baseline 0.005246s; trend: control memory moved 5.6% within the block

| pairkernel | blade-llvm | ok | 11 | 0.0411 | 0.04259 | 0.002076 | ref |  |  | reordered (4 reassoc flags) | serial | 28.4 |  |  |  |
| pairkernel | blade-cpp | ok | 11 | 0.07367 | 0.07725 | 0.004973 | 1.814 [1.764, 1.814] | suppressed |  | reordered (3 SIMD_REDUCTION) | serial | 29.2 |  |  |  |
| pairkernel | gfortran-natural | ok | 11 | 0.06109 | 0.06192 | 0.001451 | 1.454 [1.405, 1.471] | suppressed | WIN ~2x (half the transcendentals) |  | serial | 52.7 |  |  |  |
| pairkernel | gfortran-expert | ok | 11 | 0.0324 | 0.03277 | 0.0002508 | 0.769 [0.737, 0.780] | suppressed | parity within 5% |  | serial | 28.8 |  |  |  |
| pairkernel | ifx-natural | ok | 11 | 0.06354 | 0.06453 | 0.001023 | 1.515 [1.448, 1.532] | suppressed | WIN ~2x (half the transcendentals) |  | serial | 52.3 |  |  |  |
| pairkernel | ifx-expert | ok | 11 | 0.03054 | 0.03082 | 0.0007751 | 0.724 [0.691, 0.743] | suppressed | parity within 5% |  | serial | 28.4 |  |  |  |

> pairkernel serial-licensed-native: inference suppressed -- control compute median 0.01415s moved +11.0% vs the session baseline 0.01275s

| recur | blade-llvm | ok | 21 | 0.1051 | 0.1169 | 0.009206 | ref |  |  | reordered (3 reassoc flags) | serial | 51.4 |  |  |  |
| recur | blade-cpp | ok | 21 | 0.116 | 0.1341 | 0.009558 | 1.147 [1.093, 1.182] | suppressed |  | reordered (3 SIMD_REDUCTION) | serial | 52.3 |  |  |  |
| recur | gfortran-natural | ok | 21 | 0.01752 | 0.0197 | 0.00295 | 0.169 [0.151, 0.178] | suppressed | parity |  | serial | 51.4 |  |  |  |
| recur | gfortran-expert | ok | 21 | 0.009116 | 0.01048 | 0.001877 | 0.090 [0.084, 0.096] | suppressed | LOSS 1.5-3x |  | serial | 50.8 |  |  |  |
| recur | ifx-natural | ok | 21 | 0.01504 | 0.016 | 0.001486 | 0.137 [0.131, 0.145] | suppressed | parity |  | serial | 51.0 |  |  |  |
| recur | ifx-expert | ok | 21 | 0.009439 | 0.01127 | 0.002312 | 0.096 [0.088, 0.107] | suppressed | LOSS 1.5-3x |  | serial | 50.4 |  |  |  |

> recur serial-licensed-native: inference suppressed -- trend: blade-cpp first-half vs second-half median moved 6.1%; trend: gfortran-natural first-half vs second-half median moved 10.9%; trend: gfortran-expert first-half vs second-half median moved 12.7%; trend: ifx-natural first-half vs second-half median moved 7.1%; trend: ifx-expert first-half vs second-half median moved 17.1%; trend: control compute moved 7.9% within the block; trend: control memory moved 7.2% within the block

| stencil | blade-llvm | ok | 21 | 0.01189 | 0.01351 | 0.003255 | ref |  |  | reordered (4 reassoc flags) | serial | 49.7 | 0.007253 [0.006547, 0.008686] | ref |  |
| stencil | blade-cpp | ok | 21 | 0.0149 | 0.01634 | 0.003056 | 1.209 [1.102, 1.333] | suppressed |  | reordered (4 SIMD_REDUCTION) | serial | 50.6 | 0.007231 [0.00642, 0.008756] | 0.997 [0.848, 1.166] |  |
| stencil | gfortran-natural | ok | 21 | 0.01678 | 0.01776 | 0.002885 | 1.314 [1.233, 1.452] | suppressed | parity (bandwidth-bound) |  | serial | 50.7 | 0.007371 [0.006663, 0.009109] | 1.016 [0.925, 1.257] |  |
| stencil | gfortran-expert | ok | 21 | 0.0136 | 0.01446 | 0.002 | 1.070 [0.985, 1.146] | suppressed | parity |  | serial | 50.7 | 0.004842 [0.004416, 0.005965] | 0.668 [0.569, 0.790] |  |
| stencil | ifx-natural | ok | 21 | 0.01816 | 0.01896 | 0.0007733 | 1.403 [1.245, 1.516] | suppressed | parity (bandwidth-bound) |  | serial | 50.2 | 0.01201 [0.01151, 0.01224] | 1.656 [1.364, 1.850] |  |
| stencil | ifx-expert | ok | 21 | 0.01058 | 0.01164 | 0.003758 | 0.861 [0.843, 0.949] | suppressed | parity |  | serial | 50.3 | 0.005698 [0.005059, 0.007581] | 0.786 [0.750, 1.000] |  |
| stencil | blade-llvm-inputonly | ok | 21 | 0.005801 | 0.006259 | 0.001044 |  |  |  | reordered (2 reassoc flags) | serial | 27.3 |  |  |  |
| stencil | blade-cpp-inputonly | ok | 21 | 0.008783 | 0.009107 | 0.0008309 |  |  |  | reordered (1 SIMD_REDUCTION) | serial | 28.1 |  |  |  |
| stencil | gfortran-natural-inputonly | ok | 21 | 0.009832 | 0.01039 | 0.0007091 |  |  |  |  | serial | 27.8 |  |  |  |
| stencil | gfortran-expert-inputonly | ok | 21 | 0.008917 | 0.009615 | 0.001391 |  |  |  |  | serial | 27.8 |  |  |  |
| stencil | ifx-natural-inputonly | ok | 21 | 0.006497 | 0.006947 | 0.001039 |  |  |  |  | serial | 27.4 |  |  |  |
| stencil | ifx-expert-inputonly | ok | 21 | 0.005513 | 0.005939 | 0.0007879 |  |  |  |  | serial | 27.4 |  |  |  |

> stencil serial-licensed-native: inference suppressed -- trend: blade-llvm first-half vs second-half median moved 25.1%; trend: blade-cpp first-half vs second-half median moved 18.0%; trend: gfortran-natural first-half vs second-half median moved 13.4%; trend: gfortran-expert first-half vs second-half median moved 15.3%; trend: ifx-expert first-half vs second-half median moved 28.9%; trend: blade-llvm-inputonly first-half vs second-half median moved 17.5%; trend: blade-cpp-inputonly first-half vs second-half median moved 6.8%; trend: gfortran-expert-inputonly first-half vs second-half median moved 10.7%; trend: ifx-natural-inputonly first-half vs second-half median moved 12.7%; trend: ifx-expert-inputonly first-half vs second-half median moved 7.3%; trend: control compute moved 14.0% within the block; trend: control memory moved 6.1% within the block


## Controls

- compute (recur/gfortran-expert/serial-repro-native/full): n=86 median 0.01342 s, baseline 0.01275 s
- memory (saxpy/gfortran-expert/serial-repro-native/control): n=86 median 0.005505 s, baseline 0.005246 s
