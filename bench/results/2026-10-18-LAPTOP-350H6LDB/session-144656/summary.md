# Blade vs Fortran -- bench session 2026-10-18T14:46:56

- host LAPTOP-350H6LDB | AMD Ryzen 7 5800HS with Radeon Graphics | 8C/16T | 15.41 GB | power ac | max processor state AC 100% | boost AC None | Power Scheme GUID: 64a64f24-65b9-4b56-befd-5ec1eaced9b3  (Silent)
- git fceb895d4e8df71131086a94652b5cab1b120850 | Blade.dll 74b6728036b170d9 | GNU Fortran (Rev4, Built by MSYS2 project) 16.2.0 | Intel(R) Fortran Compiler for applications running on Intel(R) 64, Version 2026.1.0 Build 20260617 | clang version 22.1.8 (https://github.com/msys2/MINGW-packages d34feaaa1d0aff624c870910b6f2589aa40d20de)
- size `full` | duration 943.4 s | pins: pins-A.json, pins-B.json
- PRIMARY estimand: the complete timed region. ratio = median(arm) / median(Blade reference), round-block bootstrap 95% CI (10000 resamples); ratio > 1 = Blade faster. Verdicts (delta 0.1): win / detectable-win / equivalent / detectable-loss / loss / inconclusive; `suppressed` = the block failed a drift, trend or control check. Intervals are pointwise; no suite-wide claim. incremental = median(arm) - median(its own input-only twin), NOT kernel time. Reference: blade-llvm (serial), blade-cpp (mt8, or when the LLVM lane refused). Fresh-process, allocation-inclusive measurements.
- **FLAG**: antisym/mt8-fastmath-native: inference suppressed: trend: ifx-natural first-half vs second-half median moved 9.8%
- **FLAG**: comoment3/mt8-fastmath-native: inference suppressed: trend: gfortran-natural first-half vs second-half median moved 10.6%; trend: ifx-natural first-half vs second-half median moved 9.4%
- **FLAG**: gram/mt8-fastmath-native: inference suppressed: trend: blade-cpp first-half vs second-half median moved 14.1%; trend: gfortran-natural first-half vs second-half median moved 5.3%; trend: gfortran-expert first-half vs second-half median moved 6.2%; trend: control memory moved 8.3% within the block
- **FLAG**: fusedchain/mt8-fastmath-native: inference suppressed: trend: blade-cpp first-half vs second-half median moved 18.0%; trend: gfortran-natural first-half vs second-half median moved 6.9%; trend: gfortran-expert first-half vs second-half median moved 6.9%; trend: ifx-expert first-half vs second-half median moved 14.9%; trend: gfortran-expert-inputonly first-half vs second-half median moved 17.4%; trend: ifx-expert-inputonly first-half vs second-half median moved 7.6%; trend: control compute moved 6.0% within the block; control memory median 0.005756s moved +9.4% vs the session baseline 0.005263s; trend: control memory moved 10.9% within the block
- **FLAG**: onepass/mt8-fastmath-native: inference suppressed: trend: ifx-expert first-half vs second-half median moved 5.3%; trend: gfortran-natural-inputonly first-half vs second-half median moved 5.5%; trend: gfortran-expert-inputonly first-half vs second-half median moved 7.5%
- **FLAG**: dot/mt8-fastmath-native: inference suppressed: trend: blade-cpp-inputonly first-half vs second-half median moved 7.1%; trend: gfortran-expert-inputonly first-half vs second-half median moved 9.8%

## mt8-fastmath-native

Blade reference per kernel: antisym=blade-cpp, comoment3=blade-cpp, gram=blade-cpp, fusedchain=blade-cpp, onepass=None, dot=blade-cpp

| kernel | arm | status | n | min (s) | median (s) | IQR (s) | ratio vs ref [95% CI] | verdict | hypothesis | fold form | parallelism | peak WS (MB) | incremental (s) [CI] | incremental ratio [CI] | notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| antisym | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (7 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| antisym | blade-cpp | ok | 21 | 0.01947 | 0.02103 | 0.002408 | ref |  |  | reordered (6 SIMD_REDUCTION) | openmp (4 construct(s)) | 18.7 |  |  |  |
| antisym | gfortran-natural | ok | 21 | 0.02522 | 0.03717 | 0.003841 | 1.767 [1.660, 1.811] | suppressed | WIN ~2x (half the cells, no diagonal) |  | openmp runtime linked | 23.8 |  |  |  |
| antisym | gfortran-expert | ok | 21 | 0.009011 | 0.00974 | 0.000434 | 0.463 [0.433, 0.476] | suppressed | parity +-15% |  | openmp runtime linked | 33.1 |  |  |  |
| antisym | ifx-natural | ok | 21 | 0.01621 | 0.02024 | 0.003641 | 0.962 [0.874, 1.051] | suppressed | WIN ~2x (half the cells, no diagonal) |  | openmp runtime linked | 25.4 |  |  |  |
| antisym | ifx-expert | ok | 21 | 0.009227 | 0.01006 | 0.001021 | 0.478 [0.443, 0.493] | suppressed | parity +-15% |  | openmp runtime linked | 34.7 |  |  |  |

> antisym mt8-fastmath-native: inference suppressed -- trend: ifx-natural first-half vs second-half median moved 9.8%

| comoment3 | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (6 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| comoment3 | blade-cpp | ok | 21 | 0.01999 | 0.02067 | 0.001849 | ref |  |  | reordered (4 SIMD_REDUCTION) | openmp (4 construct(s)) | 12.6 |  |  |  |
| comoment3 | gfortran-natural | ok | 21 | 0.02571 | 0.0385 | 0.004806 | 1.862 [1.746, 1.915] | suppressed | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | openmp runtime linked | 43.9 |  |  |  |
| comoment3 | gfortran-expert | ok | 21 | 0.004632 | 0.005136 | 0.0005295 | 0.248 [0.236, 0.261] | suppressed | parity to 1.3x loss |  | openmp runtime linked | 14.5 |  |  |  |
| comoment3 | ifx-natural | ok | 21 | 0.01641 | 0.02072 | 0.006106 | 1.002 [0.849, 1.133] | suppressed | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | openmp runtime linked | 45.4 |  |  |  |
| comoment3 | ifx-expert | ok | 21 | 0.00384 | 0.004092 | 0.0001952 | 0.198 [0.186, 0.201] | suppressed | parity to 1.3x loss |  | openmp runtime linked | 15.8 |  |  |  |

> comoment3 mt8-fastmath-native: inference suppressed -- trend: gfortran-natural first-half vs second-half median moved 10.6%; trend: ifx-natural first-half vs second-half median moved 9.4%

| gram | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (8 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| gram | blade-cpp | ok | 7 | 0.04901 | 0.05676 | 0.009768 | ref |  |  | reordered (3 SIMD_REDUCTION) | openmp (4 construct(s)) | 27.8 |  |  |  |
| gram | gfortran-natural | ok | 7 | 0.6239 | 0.7068 | 0.03978 | 12.454 [10.354, 13.638] | suppressed | win 1.5-2x if matmul is a naive nest; parity/loss if a blocked runtime kernel |  | openmp runtime linked | 37.3 |  |  | matmul is unannotated: parallel preparation, serial kernel unless the runtime threads matmul (FORT records) |
| gram | gfortran-expert | ok | 7 | 0.01979 | 0.02097 | 0.001172 | 0.370 [0.325, 0.410] | suppressed | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | openmp runtime linked | 52.1 |  |  |  |
| gram | ifx-natural | ok | 7 | 0.2329 | 0.2445 | 0.02162 | 4.308 [3.640, 5.193] | suppressed | win 1.5-2x if matmul is a naive nest; parity/loss if a blocked runtime kernel |  | openmp runtime linked | 58.3 |  |  | matmul is unannotated: parallel preparation, serial kernel unless the runtime threads matmul (FORT records) |
| gram | ifx-expert | ok | 7 | 0.01832 | 0.01957 | 0.0007742 | 0.345 [0.293, 0.380] | suppressed | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | openmp runtime linked | 53.7 |  |  |  |

> gram mt8-fastmath-native: inference suppressed -- trend: blade-cpp first-half vs second-half median moved 14.1%; trend: gfortran-natural first-half vs second-half median moved 5.3%; trend: gfortran-expert first-half vs second-half median moved 6.2%; trend: control memory moved 8.3% within the block

| fusedchain | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (2 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| fusedchain | blade-cpp | ok | 21 | 0.003368 | 0.004616 | 0.001359 | ref |  |  | in-order | openmp (2 construct(s)) | 21.0 | 0.000735 [-0.0003654, 0.0009839] | ref |  |
| fusedchain | gfortran-natural | ok | 21 | 0.009919 | 0.01166 | 0.001672 | 2.526 [2.392, 3.284] | suppressed | WIN 1.5-2.5x (no temporaries) |  | openmp runtime linked | 66.5 | 0.008541 [0.008062, 0.009182] | undefined: the reference's incremental interval includes 0 |  |
| fusedchain | gfortran-expert | ok | 21 | 0.003027 | 0.003534 | 0.0009255 | 0.766 [0.699, 1.055] | suppressed | parity |  | openmp runtime linked | 20.8 | 0.0006508 [0.0002513, 0.001168] | undefined: the reference's incremental interval includes 0 |  |
| fusedchain | ifx-natural | ok | 21 | 0.01038 | 0.01202 | 0.001787 | 2.605 [2.436, 3.391] | suppressed | WIN 1.5-2.5x (no temporaries) |  | openmp runtime linked | 68.2 | 0.008572 [0.00771, 0.009378] | undefined: the reference's incremental interval includes 0 |  |
| fusedchain | ifx-expert | ok | 21 | 0.003132 | 0.003536 | 0.0007682 | 0.766 [0.721, 0.962] | suppressed | parity |  | openmp runtime linked | 22.3 | 0.0002939 [-0.0001196, 0.0006475] | undefined: the reference's incremental interval includes 0 |  |
| fusedchain | blade-llvm-inputonly | serial-only | 0 | -- | -- | -- |  |  |  | reordered (1 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| fusedchain | blade-cpp-inputonly | ok | 21 | 0.00311 | 0.003881 | 0.000444 |  |  |  | in-order | openmp (1 construct(s)) | 21.0 |  |  |  |
| fusedchain | gfortran-natural-inputonly | ok | 21 | 0.002589 | 0.003117 | 0.0002694 |  |  |  |  | openmp runtime linked | 20.7 |  |  |  |
| fusedchain | gfortran-expert-inputonly | ok | 21 | 0.002583 | 0.002883 | 0.000602 |  |  |  |  | openmp runtime linked | 20.7 |  |  |  |
| fusedchain | ifx-natural-inputonly | ok | 21 | 0.003081 | 0.003451 | 0.0004121 |  |  |  |  | openmp runtime linked | 22.3 |  |  |  |
| fusedchain | ifx-expert-inputonly | ok | 21 | 0.002941 | 0.003242 | 0.0006278 |  |  |  |  | openmp runtime linked | 22.2 |  |  |  |

> fusedchain mt8-fastmath-native: inference suppressed -- trend: blade-cpp first-half vs second-half median moved 18.0%; trend: gfortran-natural first-half vs second-half median moved 6.9%; trend: gfortran-expert first-half vs second-half median moved 6.9%; trend: ifx-expert first-half vs second-half median moved 14.9%; trend: gfortran-expert-inputonly first-half vs second-half median moved 17.4%; trend: ifx-expert-inputonly first-half vs second-half median moved 7.6%; trend: control compute moved 6.0% within the block; control memory median 0.005756s moved +9.4% vs the session baseline 0.005263s; trend: control memory moved 10.9% within the block

| onepass | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (5 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| onepass | blade-cpp | no-parallel-form | 0 | -- | -- | -- |  |  |  | reordered (1 SIMD_REDUCTION) |  |  |  |  | kernel.json no_parallel_form (spec: no licensed parallel spelling) |
| onepass | gfortran-natural | ok | 21 | 0.01248 | 0.01322 | 0.000593 |  |  | WIN 2-3x (one pass vs four) |  | openmp runtime linked | 43.6 |  |  |  |
| onepass | gfortran-expert | ok | 21 | 0.008004 | 0.008892 | 0.0006172 |  |  | parity |  | openmp runtime linked | 43.6 |  |  |  |
| onepass | ifx-natural | ok | 21 | 0.01261 | 0.0135 | 0.0005752 |  |  | WIN 2-3x (one pass vs four) |  | openmp runtime linked | 45.1 |  |  |  |
| onepass | ifx-expert | ok | 21 | 0.008351 | 0.009376 | 0.001022 |  |  | parity |  | openmp runtime linked | 45.1 |  |  |  |
| onepass | blade-llvm-inputonly | serial-only | 0 | -- | -- | -- |  |  |  | reordered (1 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| onepass | blade-cpp-inputonly | no-parallel-form | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | kernel.json no_parallel_form (spec: no licensed parallel spelling) |
| onepass | gfortran-natural-inputonly | ok | 21 | 0.006178 | 0.006571 | 0.0006137 |  |  |  |  | openmp runtime linked | 43.6 |  |  |  |
| onepass | gfortran-expert-inputonly | ok | 21 | 0.00628 | 0.006768 | 0.0009926 |  |  |  |  | openmp runtime linked | 43.6 |  |  |  |
| onepass | ifx-natural-inputonly | ok | 21 | 0.006631 | 0.007307 | 0.0006084 |  |  |  |  | openmp runtime linked | 45.1 |  |  |  |
| onepass | ifx-expert-inputonly | ok | 21 | 0.006611 | 0.007 | 0.0009107 |  |  |  |  | openmp runtime linked | 45.1 |  |  |  |

> onepass mt8-fastmath-native: inference suppressed -- trend: ifx-expert first-half vs second-half median moved 5.3%; trend: gfortran-natural-inputonly first-half vs second-half median moved 5.5%; trend: gfortran-expert-inputonly first-half vs second-half median moved 7.5%

| dot | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (3 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| dot | blade-cpp | ok | 21 | 0.009769 | 0.01119 | 0.001658 | ref |  |  | in-order | openmp (2 construct(s)) | 51.5 | 0.001937 [0.0009847, 0.003106] | ref |  |
| dot | gfortran-natural | ok | 21 | 0.009362 | 0.01036 | 0.0006336 | 0.925 [0.855, 0.951] | suppressed | parity |  | openmp runtime linked | 51.3 | 0.002014 [0.001422, 0.002268] | 1.040 [0.571, 1.828] |  |
| dot | gfortran-expert | ok | 21 | 0.009394 | 0.01022 | 0.0008349 | 0.913 [0.867, 0.956] | suppressed | parity |  | openmp runtime linked | 51.2 | 0.002053 [0.001564, 0.002853] | 1.060 [0.692, 1.744] |  |
| dot | ifx-natural | ok | 21 | 0.009856 | 0.01053 | 0.0007002 | 0.941 [0.886, 0.982] | suppressed | parity |  | openmp runtime linked | 52.8 | 0.002348 [0.001666, 0.002687] | 1.212 [0.763, 2.068] |  |
| dot | ifx-expert | ok | 21 | 0.009897 | 0.01065 | 0.00062 | 0.952 [0.903, 0.989] | suppressed | parity |  | openmp runtime linked | 52.8 | 0.002489 [0.00175, 0.002856] | 1.285 [0.615, 2.561] |  |
| dot | blade-llvm-inputonly | serial-only | 0 | -- | -- | -- |  |  |  | reordered (2 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| dot | blade-cpp-inputonly | ok | 21 | 0.008082 | 0.009253 | 0.00174 |  |  |  | in-order | openmp (1 construct(s)) | 51.5 |  |  |  |
| dot | gfortran-natural-inputonly | ok | 21 | 0.007461 | 0.008341 | 0.0005937 |  |  |  |  | openmp runtime linked | 51.2 |  |  |  |
| dot | gfortran-expert-inputonly | ok | 21 | 0.007441 | 0.008166 | 0.001018 |  |  |  |  | openmp runtime linked | 51.3 |  |  |  |
| dot | ifx-natural-inputonly | ok | 21 | 0.007906 | 0.008184 | 0.0009095 |  |  |  |  | openmp runtime linked | 52.8 |  |  |  |
| dot | ifx-expert-inputonly | ok | 21 | 0.007926 | 0.008162 | 0.0008896 |  |  |  |  | openmp runtime linked | 52.8 |  |  |  |

> dot mt8-fastmath-native: inference suppressed -- trend: blade-cpp-inputonly first-half vs second-half median moved 7.1%; trend: gfortran-expert-inputonly first-half vs second-half median moved 9.8%


## Controls

- compute (recur/gfortran-expert/serial-repro-native/full): n=112 median 0.01301 s, baseline 0.01309 s
- memory (saxpy/gfortran-expert/serial-repro-native/control): n=112 median 0.005397 s, baseline 0.005263 s
