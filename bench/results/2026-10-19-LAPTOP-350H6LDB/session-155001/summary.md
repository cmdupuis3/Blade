# Blade vs Fortran -- bench session 2026-10-19T15:50:01

- host LAPTOP-350H6LDB | AMD Ryzen 7 5800HS with Radeon Graphics | 8C/16T | 15.41 GB | power ac | max processor state AC 100% | boost AC None | Power Scheme GUID: 64a64f24-65b9-4b56-befd-5ec1eaced9b3  (Silent)
- git 39a8a869ff410c79edbe599a98257d4012241702 | Blade.dll bb52a4c2a008d198 | GNU Fortran (Rev4, Built by MSYS2 project) 16.2.0 | Intel(R) Fortran Compiler for applications running on Intel(R) 64, Version 2026.1.0 Build 20260617 | clang version 22.1.8 (https://github.com/msys2/MINGW-packages d34feaaa1d0aff624c870910b6f2589aa40d20de)
- size `full` | duration 1227.6 s | pins: pins-A.json, pins-B.json
- PRIMARY estimand: the complete timed region. ratio = median(arm) / median(Blade reference), round-block bootstrap 95% CI (10000 resamples); ratio > 1 = Blade faster. Verdicts (delta 0.1): win / detectable-win / equivalent / detectable-loss / loss / inconclusive; `suppressed` = the block failed a drift, trend or control check. Intervals are pointwise; no suite-wide claim. incremental = median(arm) - median(its own input-only twin), NOT kernel time. Reference: blade-llvm (serial), blade-cpp (mt8, or when the LLVM lane refused). Fresh-process, allocation-inclusive measurements.
- **FLAG**: antisym/mt8-fastmath-native: inference suppressed: trend: ifx-natural first-half vs second-half median moved 6.5%
- **FLAG**: band/mt8-fastmath-native: inference suppressed: trend: gfortran-natural first-half vs second-half median moved 7.1%
- **FLAG**: gram/mt8-fastmath-native: inference suppressed: trend: gfortran-expert first-half vs second-half median moved 6.1%; trend: ifx-natural first-half vs second-half median moved 5.1%
- **FLAG**: fusedchain/mt8-fastmath-native: inference suppressed: trend: blade-cpp first-half vs second-half median moved 6.1%; trend: blade-cpp-inputonly first-half vs second-half median moved 14.7%; trend: gfortran-natural-inputonly first-half vs second-half median moved 6.7%; trend: ifx-natural-inputonly first-half vs second-half median moved 5.8%
- **FLAG**: onepass/mt8-fastmath-native: inference suppressed: trend: gfortran-natural-inputonly first-half vs second-half median moved 7.3%; trend: gfortran-expert-inputonly first-half vs second-half median moved 5.4%; trend: ifx-natural-inputonly first-half vs second-half median moved 6.0%
- **FLAG**: dot/mt8-fastmath-native: inference suppressed: trend: gfortran-natural-inputonly first-half vs second-half median moved 6.9%

## mt8-fastmath-native

Blade reference per kernel: antisym=blade-cpp, band=blade-cpp, comoment3=blade-cpp, gram=blade-cpp, fusedchain=blade-cpp, onepass=None, dot=blade-cpp

| kernel | arm | status | n | min (s) | median (s) | IQR (s) | ratio vs ref [95% CI] | verdict | hypothesis | fold form | parallelism | peak WS (MB) | incremental (s) [CI] | incremental ratio [CI] | notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| antisym | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (4 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| antisym | blade-cpp | ok | 21 | 0.008929 | 0.00957 | 0.0006948 | ref |  |  | reordered (5 SIMD_REDUCTION/K-lane) | openmp (3 construct(s)) | 26.4 |  |  |  |
| antisym | gfortran-natural | ok | 21 | 0.04026 | 0.05497 | 0.003642 | 5.745 [5.516, 5.987] | suppressed | WIN ~2x (half the cells, no diagonal) |  | openmp runtime linked | 23.9 |  |  |  |
| antisym | gfortran-expert | ok | 21 | 0.0106 | 0.01133 | 0.000767 | 1.184 [1.145, 1.231] | suppressed | parity +-15% |  | openmp runtime linked | 33.2 |  |  |  |
| antisym | ifx-natural | ok | 21 | 0.01952 | 0.02476 | 0.00353 | 2.587 [2.450, 2.742] | suppressed | WIN ~2x (half the cells, no diagonal) |  | openmp runtime linked | 25.4 |  |  |  |
| antisym | ifx-expert | ok | 21 | 0.0107 | 0.01162 | 0.00104 | 1.214 [1.161, 1.260] | suppressed | parity +-15% |  | openmp runtime linked | 34.7 |  |  |  |

> antisym mt8-fastmath-native: inference suppressed -- trend: ifx-natural first-half vs second-half median moved 6.5%

| band | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (2 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| band | blade-cpp | ok | 21 | 0.0009852 | 0.001112 | 0.0001467 | ref |  |  | reordered (1 SIMD_REDUCTION/K-lane) | openmp (2 construct(s)) | 6.0 |  |  |  |
| band | gfortran-natural | ok | 21 | 0.03435 | 0.04534 | 0.009283 | 40.790 [38.200, 46.932] | suppressed | win 10x+ (the natural tier is the n^2 predicate nest, a straw man by construction) |  | openmp runtime linked | 5.8 |  |  |  |
| band | gfortran-expert | ok | 21 | 0.001035 | 0.001161 | 0.0001191 | 1.044 [0.971, 1.149] | suppressed | parity in both lanes: licensed folds walk the band by diagonal (k = i - j outer) as the licensed expert does, with phi hoisted per diagonal by the back end (G33); repro keeps the lex order with the terms vectorized and summed in order (G30) |  | openmp runtime linked | 5.8 |  |  |  |
| band | ifx-natural | ok | 21 | 0.6082 | 0.6278 | 0.007774 | 564.744 [535.716, 590.219] | suppressed | win 10x+ (the natural tier is the n^2 predicate nest, a straw man by construction) |  | openmp runtime linked | 7.4 |  |  |  |
| band | ifx-expert | ok | 21 | 0.001706 | 0.00197 | 0.0001438 | 1.773 [1.643, 1.847] | suppressed | parity in both lanes: licensed folds walk the band by diagonal (k = i - j outer) as the licensed expert does, with phi hoisted per diagonal by the back end (G33); repro keeps the lex order with the terms vectorized and summed in order (G30) |  | openmp runtime linked | 7.4 |  |  |  |

> band mt8-fastmath-native: inference suppressed -- trend: gfortran-natural first-half vs second-half median moved 7.1%

| comoment3 | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (6 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| comoment3 | blade-cpp | ok | 21 | 0.003118 | 0.003573 | 0.0002935 | ref |  |  | reordered (6 SIMD_REDUCTION/K-lane) | openmp (3 construct(s)) | 13.2 |  |  |  |
| comoment3 | gfortran-natural | ok | 21 | 0.03042 | 0.04222 | 0.009149 | 11.818 [10.643, 13.195] | win | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | openmp runtime linked | 44.0 |  |  |  |
| comoment3 | gfortran-expert | ok | 21 | 0.004749 | 0.005275 | 0.0003967 | 1.477 [1.428, 1.556] | win | parity to 1.3x loss |  | openmp runtime linked | 14.5 |  |  |  |
| comoment3 | ifx-natural | ok | 21 | 0.02067 | 0.02627 | 0.001725 | 7.352 [7.029, 7.778] | win | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | openmp runtime linked | 45.5 |  |  |  |
| comoment3 | ifx-expert | ok | 21 | 0.004115 | 0.004561 | 0.0003127 | 1.277 [1.225, 1.354] | win | parity to 1.3x loss |  | openmp runtime linked | 15.9 |  |  |  |
| gram | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (4 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| gram | blade-cpp | ok | 21 | 0.01724 | 0.02006 | 0.002611 | ref |  |  | reordered (4 SIMD_REDUCTION/K-lane) | openmp (3 construct(s)) | 32.3 |  |  |  |
| gram | gfortran-natural | ok | 21 | 0.5434 | 0.732 | 0.1898 | 36.491 [33.639, 40.134] | suppressed | win 1.5-2x if matmul is a naive nest; parity/loss if a blocked runtime kernel |  | openmp runtime linked | 37.4 |  |  | matmul is unannotated: parallel preparation, serial kernel unless the runtime threads matmul (FORT records) |
| gram | gfortran-expert | ok | 21 | 0.02338 | 0.02505 | 0.002864 | 1.249 [1.198, 1.338] | suppressed | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | openmp runtime linked | 52.2 |  |  |  |
| gram | ifx-natural | ok | 21 | 0.2361 | 0.2528 | 0.01789 | 12.601 [11.785, 13.419] | suppressed | win 1.5-2x if matmul is a naive nest; parity/loss if a blocked runtime kernel |  | openmp runtime linked | 58.4 |  |  | matmul is unannotated: parallel preparation, serial kernel unless the runtime threads matmul (FORT records) |
| gram | ifx-expert | ok | 21 | 0.02182 | 0.02448 | 0.003207 | 1.220 [1.184, 1.307] | suppressed | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | openmp runtime linked | 53.8 |  |  |  |

> gram mt8-fastmath-native: inference suppressed -- trend: gfortran-expert first-half vs second-half median moved 6.1%; trend: ifx-natural first-half vs second-half median moved 5.1%

| fusedchain | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (2 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| fusedchain | blade-cpp | ok | 21 | 0.003069 | 0.003686 | 0.0009106 | ref |  |  | reordered (1 SIMD_REDUCTION/K-lane) | openmp (2 construct(s)) | 21.1 | 0.0002018 [-0.0002386, 0.0009119] | ref |  |
| fusedchain | gfortran-natural | ok | 21 | 0.01077 | 0.01165 | 0.001006 | 3.160 [2.844, 3.610] | suppressed | WIN 1.5-2.5x (no temporaries) |  | openmp runtime linked | 66.6 | 0.008525 [0.008046, 0.00926] | undefined: the reference's incremental interval includes 0 |  |
| fusedchain | gfortran-expert | ok | 21 | 0.002944 | 0.00335 | 0.0008655 | 0.909 [0.822, 1.073] | suppressed | parity |  | openmp runtime linked | 20.8 | 0.0003909 [0.0001862, 0.0007392] | undefined: the reference's incremental interval includes 0 |  |
| fusedchain | ifx-natural | ok | 21 | 0.01092 | 0.01195 | 0.0004843 | 3.242 [2.933, 3.640] | suppressed | WIN 1.5-2.5x (no temporaries) |  | openmp runtime linked | 68.3 | 0.008648 [0.008214, 0.008858] | undefined: the reference's incremental interval includes 0 |  |
| fusedchain | ifx-expert | ok | 21 | 0.00316 | 0.003422 | 0.0008914 | 0.928 [0.835, 1.147] | suppressed | parity |  | openmp runtime linked | 22.3 | 0.0001573 [-0.0001556, 0.0009289] | undefined: the reference's incremental interval includes 0 |  |
| fusedchain | blade-llvm-inputonly | serial-only | 0 | -- | -- | -- |  |  |  | reordered (1 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| fusedchain | blade-cpp-inputonly | ok | 21 | 0.002828 | 0.003485 | 0.0006887 |  |  |  | in-order | openmp (1 construct(s)) | 21.1 |  |  |  |
| fusedchain | gfortran-natural-inputonly | ok | 21 | 0.002709 | 0.003124 | 0.000518 |  |  |  |  | openmp runtime linked | 20.8 |  |  |  |
| fusedchain | gfortran-expert-inputonly | ok | 21 | 0.002527 | 0.002959 | 0.0004452 |  |  |  |  | openmp runtime linked | 20.8 |  |  |  |
| fusedchain | ifx-natural-inputonly | ok | 21 | 0.003022 | 0.003302 | 0.0005414 |  |  |  |  | openmp runtime linked | 22.3 |  |  |  |
| fusedchain | ifx-expert-inputonly | ok | 21 | 0.002933 | 0.003265 | 0.0004718 |  |  |  |  | openmp runtime linked | 22.3 |  |  |  |

> fusedchain mt8-fastmath-native: inference suppressed -- trend: blade-cpp first-half vs second-half median moved 6.1%; trend: blade-cpp-inputonly first-half vs second-half median moved 14.7%; trend: gfortran-natural-inputonly first-half vs second-half median moved 6.7%; trend: ifx-natural-inputonly first-half vs second-half median moved 5.8%

| onepass | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (5 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| onepass | blade-cpp | no-parallel-form | 0 | -- | -- | -- |  |  |  | reordered (1 SIMD_REDUCTION/K-lane) |  |  |  |  | kernel.json no_parallel_form (spec: no licensed parallel spelling) |
| onepass | gfortran-natural | ok | 21 | 0.01347 | 0.015 | 0.001197 |  |  | WIN 2-3x (one pass vs four) |  | openmp runtime linked | 43.7 |  |  |  |
| onepass | gfortran-expert | ok | 21 | 0.008931 | 0.00943 | 0.0009652 |  |  | parity |  | openmp runtime linked | 43.7 |  |  |  |
| onepass | ifx-natural | ok | 21 | 0.01372 | 0.01405 | 0.0004867 |  |  | WIN 2-3x (one pass vs four) |  | openmp runtime linked | 45.2 |  |  |  |
| onepass | ifx-expert | ok | 21 | 0.008617 | 0.009602 | 0.0008913 |  |  | parity |  | openmp runtime linked | 45.2 |  |  |  |
| onepass | blade-llvm-inputonly | serial-only | 0 | -- | -- | -- |  |  |  | reordered (1 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| onepass | blade-cpp-inputonly | no-parallel-form | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | kernel.json no_parallel_form (spec: no licensed parallel spelling) |
| onepass | gfortran-natural-inputonly | ok | 21 | 0.006463 | 0.007638 | 0.00124 |  |  |  |  | openmp runtime linked | 43.7 |  |  |  |
| onepass | gfortran-expert-inputonly | ok | 21 | 0.006376 | 0.007557 | 0.001349 |  |  |  |  | openmp runtime linked | 43.7 |  |  |  |
| onepass | ifx-natural-inputonly | ok | 21 | 0.006705 | 0.008008 | 0.001149 |  |  |  |  | openmp runtime linked | 45.2 |  |  |  |
| onepass | ifx-expert-inputonly | ok | 21 | 0.00671 | 0.0078 | 0.0008745 |  |  |  |  | openmp runtime linked | 45.2 |  |  |  |

> onepass mt8-fastmath-native: inference suppressed -- trend: gfortran-natural-inputonly first-half vs second-half median moved 7.3%; trend: gfortran-expert-inputonly first-half vs second-half median moved 5.4%; trend: ifx-natural-inputonly first-half vs second-half median moved 6.0%

| dot | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (3 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| dot | blade-cpp | ok | 21 | 0.01004 | 0.0118 | 0.0009409 | ref |  |  | reordered (1 SIMD_REDUCTION/K-lane) | openmp (2 construct(s)) | 51.6 | 0.001941 [0.0009747, 0.002416] | ref |  |
| dot | gfortran-natural | ok | 21 | 0.01024 | 0.01121 | 0.0008473 | 0.950 [0.916, 1.000] | suppressed | parity |  | openmp runtime linked | 51.3 | 0.002621 [0.001742, 0.003031] | 1.350 [0.861, 2.646] |  |
| dot | gfortran-expert | ok | 21 | 0.0102 | 0.01075 | 0.0007698 | 0.911 [0.883, 0.978] | suppressed | parity |  | openmp runtime linked | 51.3 | 0.001961 [0.001469, 0.002677] | 1.010 [0.707, 2.247] |  |
| dot | ifx-natural | ok | 21 | 0.01076 | 0.01143 | 0.0005967 | 0.968 [0.936, 1.020] | suppressed | parity |  | openmp runtime linked | 52.9 | 0.002146 [0.001752, 0.002758] | 1.106 [0.805, 2.396] |  |
| dot | ifx-expert | ok | 21 | 0.01086 | 0.01172 | 0.0008258 | 0.992 [0.954, 1.063] | suppressed | parity |  | openmp runtime linked | 52.9 | 0.00216 [0.001532, 0.002785] | 1.113 [0.714, 2.464] |  |
| dot | blade-llvm-inputonly | serial-only | 0 | -- | -- | -- |  |  |  | reordered (2 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| dot | blade-cpp-inputonly | ok | 21 | 0.008192 | 0.009864 | 0.001164 |  |  |  | in-order | openmp (1 construct(s)) | 51.6 |  |  |  |
| dot | gfortran-natural-inputonly | ok | 21 | 0.007616 | 0.008589 | 0.001121 |  |  |  |  | openmp runtime linked | 51.3 |  |  |  |
| dot | gfortran-expert-inputonly | ok | 21 | 0.00766 | 0.008788 | 0.001056 |  |  |  |  | openmp runtime linked | 51.3 |  |  |  |
| dot | ifx-natural-inputonly | ok | 21 | 0.008022 | 0.009283 | 0.0009633 |  |  |  |  | openmp runtime linked | 52.9 |  |  |  |
| dot | ifx-expert-inputonly | ok | 21 | 0.008857 | 0.009557 | 0.0008955 |  |  |  |  | openmp runtime linked | 52.9 |  |  |  |

> dot mt8-fastmath-native: inference suppressed -- trend: gfortran-natural-inputonly first-half vs second-half median moved 6.9%


## Controls

- compute (recur/gfortran-expert/serial-repro-native/full): n=147 median 0.01367 s, baseline 0.01395 s
- memory (saxpy/gfortran-expert/serial-repro-native/control): n=147 median 0.005642 s, baseline 0.005552 s
