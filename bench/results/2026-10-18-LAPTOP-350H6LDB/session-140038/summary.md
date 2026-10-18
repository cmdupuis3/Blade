# Blade vs Fortran -- bench session 2026-10-18T14:00:38

- host LAPTOP-350H6LDB | AMD Ryzen 7 5800HS with Radeon Graphics | 8C/16T | 15.41 GB | power ac | max processor state AC 100% | boost AC None | Power Scheme GUID: 64a64f24-65b9-4b56-befd-5ec1eaced9b3  (Silent)
- git fceb895d4e8df71131086a94652b5cab1b120850 | Blade.dll 74b6728036b170d9 | GNU Fortran (Rev4, Built by MSYS2 project) 16.2.0 | Intel(R) Fortran Compiler for applications running on Intel(R) 64, Version 2026.1.0 Build 20260617 | clang version 22.1.8 (https://github.com/msys2/MINGW-packages d34feaaa1d0aff624c870910b6f2589aa40d20de)
- size `full` | duration 1474.1 s | pins: pins-A.json, pins-B.json
- PRIMARY estimand: the complete timed region. ratio = median(arm) / median(Blade reference), round-block bootstrap 95% CI (10000 resamples); ratio > 1 = Blade faster. Verdicts (delta 0.1): win / detectable-win / equivalent / detectable-loss / loss / inconclusive; `suppressed` = the block failed a drift, trend or control check. Intervals are pointwise; no suite-wide claim. incremental = median(arm) - median(its own input-only twin), NOT kernel time. Reference: blade-llvm (serial), blade-cpp (mt8, or when the LLVM lane refused). Fresh-process, allocation-inclusive measurements.
- **FLAG**: antisym/mt8-repro-native: inference suppressed: trend: ifx-natural first-half vs second-half median moved 22.0%; trend: control memory moved 5.2% within the block
- **FLAG**: comoment3/mt8-repro-native: inference suppressed: trend: gfortran-natural first-half vs second-half median moved 11.0%; trend: ifx-expert first-half vs second-half median moved 11.1%
- **FLAG**: gram/mt8-repro-native: inference suppressed: trend: gfortran-natural first-half vs second-half median moved 22.6%
- **FLAG**: pairkernel/mt8-repro-native: inference suppressed: trend: gfortran-natural first-half vs second-half median moved 7.2%; trend: ifx-natural first-half vs second-half median moved 6.1%
- **FLAG**: stencil/mt8-repro-native: inference suppressed: trend: blade-cpp-inputonly first-half vs second-half median moved 5.6%
- **FLAG**: dot/mt8-repro-native: inference suppressed: trend: blade-cpp first-half vs second-half median moved 6.1%; trend: gfortran-natural-inputonly first-half vs second-half median moved 7.6%

## mt8-repro-native

Blade reference per kernel: antisym=blade-cpp, comoment3=blade-cpp, gram=blade-cpp, pairkernel=blade-cpp, fusedchain=blade-cpp, onepass=None, recur=None, stencil=blade-cpp, dot=blade-cpp, saxpy=blade-cpp

| kernel | arm | status | n | min (s) | median (s) | IQR (s) | ratio vs ref [95% CI] | verdict | hypothesis | fold form | parallelism | peak WS (MB) | incremental (s) [CI] | incremental ratio [CI] | notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| antisym | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | LLVM lane has no OpenMP |
| antisym | blade-cpp | ok | 7 | 0.06021 | 0.0607 | 0.001098 | ref |  |  | in-order | openmp (2 construct(s)); threaded maps, serial folds (BLADE_FP_REPRO) | 18.7 |  |  |  |
| antisym | gfortran-natural | ok | 7 | 0.1011 | 0.1067 | 0.005357 | 1.758 [1.656, 1.809] | suppressed | WIN ~2x (half the cells, no diagonal) |  | openmp runtime linked | 23.8 |  |  |  |
| antisym | gfortran-expert | ok | 7 | 0.009114 | 0.009358 | 0.0002865 | 0.154 [0.149, 0.158] | suppressed | parity +-15% |  | openmp runtime linked | 33.1 |  |  |  |
| antisym | ifx-natural | ok | 7 | 0.04833 | 0.05918 | 0.01748 | 0.975 [0.799, 1.148] | suppressed | WIN ~2x (half the cells, no diagonal) |  | openmp runtime linked | 25.4 |  |  |  |
| antisym | ifx-expert | ok | 7 | 0.009526 | 0.009841 | 0.0003663 | 0.162 [0.158, 0.167] | suppressed | parity +-15% |  | openmp runtime linked | 34.7 |  |  |  |

> antisym mt8-repro-native: inference suppressed -- trend: ifx-natural first-half vs second-half median moved 22.0%; trend: control memory moved 5.2% within the block

| comoment3 | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | LLVM lane has no OpenMP |
| comoment3 | blade-cpp | ok | 7 | 0.09158 | 0.09268 | 0.001545 | ref |  |  | in-order | openmp (2 construct(s)); threaded maps, serial folds (BLADE_FP_REPRO) | 12.6 |  |  |  |
| comoment3 | gfortran-natural | ok | 7 | 0.08356 | 0.09318 | 0.00925 | 1.005 [0.961, 1.060] | suppressed | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | openmp runtime linked | 43.9 |  |  |  |
| comoment3 | gfortran-expert | ok | 7 | 0.004697 | 0.005096 | 0.0001546 | 0.055 [0.053, 0.056] | suppressed | parity to 1.3x loss |  | openmp runtime linked | 14.5 |  |  |  |
| comoment3 | ifx-natural | ok | 7 | 0.1049 | 0.1162 | 0.007122 | 1.254 [1.173, 1.298] | suppressed | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | openmp runtime linked | 45.4 |  |  |  |
| comoment3 | ifx-expert | ok | 7 | 0.003933 | 0.004021 | 0.0002987 | 0.043 [0.043, 0.048] | suppressed | parity to 1.3x loss |  | openmp runtime linked | 15.6 |  |  |  |

> comoment3 mt8-repro-native: inference suppressed -- trend: gfortran-natural first-half vs second-half median moved 11.0%; trend: ifx-expert first-half vs second-half median moved 11.1%

| gram | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | LLVM lane has no OpenMP |
| gram | blade-cpp | ok | 7 | 0.1366 | 0.1383 | 0.005351 | ref |  |  | in-order | openmp (2 construct(s)); threaded maps, serial folds (BLADE_FP_REPRO) | 27.8 |  |  |  |
| gram | gfortran-natural | ok | 7 | 0.5571 | 0.6986 | 0.1531 | 5.050 [4.132, 5.385] | suppressed | win 1.5-2x if matmul is a naive nest; parity/loss if a blocked runtime kernel |  | openmp runtime linked | 37.3 |  |  | matmul is unannotated: parallel preparation, serial kernel unless the runtime threads matmul (FORT records) |
| gram | gfortran-expert | ok | 7 | 0.01952 | 0.02019 | 0.0009949 | 0.146 [0.139, 0.151] | suppressed | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | openmp runtime linked | 52.1 |  |  |  |
| gram | ifx-natural | ok | 7 | 0.5861 | 0.6055 | 0.01205 | 4.377 [4.090, 4.464] | suppressed | win 1.5-2x if matmul is a naive nest; parity/loss if a blocked runtime kernel |  | openmp runtime linked | 58.3 |  |  | matmul is unannotated: parallel preparation, serial kernel unless the runtime threads matmul (FORT records) |
| gram | ifx-expert | ok | 7 | 0.01833 | 0.01857 | 0.001041 | 0.134 [0.127, 0.147] | suppressed | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | openmp runtime linked | 53.7 |  |  |  |

> gram mt8-repro-native: inference suppressed -- trend: gfortran-natural first-half vs second-half median moved 22.6%

| pairkernel | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | LLVM lane has no OpenMP |
| pairkernel | blade-cpp | ok | 21 | 0.05403 | 0.05619 | 0.002464 | ref |  |  | in-order | openmp (2 construct(s)); threaded maps, serial folds (BLADE_FP_REPRO) | 29.7 |  |  |  |
| pairkernel | gfortran-natural | ok | 21 | 0.01336 | 0.01424 | 0.002365 | 0.253 [0.242, 0.268] | suppressed | WIN ~2x (half the transcendentals) |  | openmp runtime linked | 53.3 |  |  |  |
| pairkernel | gfortran-expert | ok | 21 | 0.005991 | 0.006202 | 0.0004373 | 0.110 [0.109, 0.115] | suppressed | parity within 5% |  | openmp runtime linked | 29.5 |  |  |  |
| pairkernel | ifx-natural | ok | 21 | 0.01474 | 0.01663 | 0.002301 | 0.296 [0.276, 0.313] | suppressed | WIN ~2x (half the transcendentals) |  | openmp runtime linked | 54.8 |  |  |  |
| pairkernel | ifx-expert | ok | 21 | 0.005925 | 0.006213 | 0.0003489 | 0.111 [0.108, 0.115] | suppressed | parity within 5% |  | openmp runtime linked | 30.9 |  |  |  |

> pairkernel mt8-repro-native: inference suppressed -- trend: gfortran-natural first-half vs second-half median moved 7.2%; trend: ifx-natural first-half vs second-half median moved 6.1%

| fusedchain | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | LLVM lane has no OpenMP |
| fusedchain | blade-cpp | ok | 21 | 0.005415 | 0.005559 | 0.0002549 | ref |  |  | in-order | openmp (1 construct(s)); threaded maps, serial folds (BLADE_FP_REPRO) | 21.0 | 0.001542 [0.001444, 0.001722] | ref |  |
| fusedchain | gfortran-natural | ok | 21 | 0.01092 | 0.01146 | 0.0005934 | 2.062 [1.997, 2.105] | win | WIN 1.5-2.5x (no temporaries) |  | openmp runtime linked | 66.5 | 0.00793 [0.007667, 0.00814] | 5.143 [4.594, 5.491] |  |
| fusedchain | gfortran-expert | ok | 21 | 0.004049 | 0.004181 | 0.0002332 | 0.752 [0.728, 0.776] | loss | parity |  | openmp runtime linked | 20.7 | 0.0006551 [0.0004736, 0.0007675] | 0.425 [0.303, 0.518] |  |
| fusedchain | ifx-natural | ok | 21 | 0.01106 | 0.01149 | 0.0003007 | 2.067 [2.009, 2.095] | win | WIN 1.5-2.5x (no temporaries) |  | openmp runtime linked | 68.2 | 0.00745 [0.007346, 0.007647] | 4.832 [4.349, 5.201] |  |
| fusedchain | ifx-expert | ok | 21 | 0.004366 | 0.004642 | 0.0002936 | 0.835 [0.809, 0.860] | loss | parity |  | openmp runtime linked | 22.3 | 0.000643 [0.0005362, 0.0007651] | 0.417 [0.337, 0.520] |  |
| fusedchain | blade-llvm-inputonly | serial-only | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | LLVM lane has no OpenMP |
| fusedchain | blade-cpp-inputonly | ok | 21 | 0.003855 | 0.004017 | 0.0001229 |  |  |  | in-order | openmp (1 construct(s)); threaded maps, serial folds (BLADE_FP_REPRO) | 21.0 |  |  |  |
| fusedchain | gfortran-natural-inputonly | ok | 21 | 0.003383 | 0.003535 | 0.0002523 |  |  |  |  | openmp runtime linked | 20.7 |  |  |  |
| fusedchain | gfortran-expert-inputonly | ok | 21 | 0.003344 | 0.003526 | 0.0002505 |  |  |  |  | openmp runtime linked | 20.7 |  |  |  |
| fusedchain | ifx-natural-inputonly | ok | 21 | 0.003809 | 0.004042 | 0.0001782 |  |  |  |  | openmp runtime linked | 22.3 |  |  |  |
| fusedchain | ifx-expert-inputonly | ok | 21 | 0.003837 | 0.003999 | 0.0001626 |  |  |  |  | openmp runtime linked | 22.2 |  |  |  |
| onepass | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | LLVM lane has no OpenMP |
| onepass | blade-cpp | no-parallel-form | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | kernel.json no_parallel_form (spec: no licensed parallel spelling) |
| onepass | gfortran-natural | ok | 21 | 0.01453 | 0.01494 | 0.0005766 |  |  | WIN 2-3x (one pass vs four) |  | openmp runtime linked | 43.6 |  |  |  |
| onepass | gfortran-expert | ok | 21 | 0.01065 | 0.01094 | 0.0003713 |  |  | parity |  | openmp runtime linked | 43.6 |  |  |  |
| onepass | ifx-natural | ok | 21 | 0.01499 | 0.01535 | 0.0003249 |  |  | WIN 2-3x (one pass vs four) |  | openmp runtime linked | 45.1 |  |  |  |
| onepass | ifx-expert | ok | 21 | 0.01109 | 0.01141 | 0.0002429 |  |  | parity |  | openmp runtime linked | 45.1 |  |  |  |
| onepass | blade-llvm-inputonly | serial-only | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | LLVM lane has no OpenMP |
| onepass | blade-cpp-inputonly | no-parallel-form | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | kernel.json no_parallel_form (spec: no licensed parallel spelling) |
| onepass | gfortran-natural-inputonly | ok | 21 | 0.008115 | 0.008508 | 0.0005057 |  |  |  |  | openmp runtime linked | 43.6 |  |  |  |
| onepass | gfortran-expert-inputonly | ok | 21 | 0.008095 | 0.00828 | 0.0004396 |  |  |  |  | openmp runtime linked | 43.6 |  |  |  |
| onepass | ifx-natural-inputonly | ok | 21 | 0.008627 | 0.008839 | 0.0003524 |  |  |  |  | openmp runtime linked | 45.1 |  |  |  |
| onepass | ifx-expert-inputonly | ok | 21 | 0.00857 | 0.009136 | 0.0004397 |  |  |  |  | openmp runtime linked | 45.1 |  |  |  |
| recur | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | LLVM lane has no OpenMP |
| recur | blade-cpp | no-parallel-form | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | kernel.json no_parallel_form (spec: no licensed parallel spelling) |
| recur | gfortran-natural | ok | 21 | 0.06932 | 0.0714 | 0.002326 |  |  | parity |  | openmp runtime linked | 52.0 |  |  |  |
| recur | gfortran-expert | ok | 21 | 0.00625 | 0.006822 | 0.0005991 |  |  | LOSS 1.5-3x |  | openmp runtime linked | 51.4 |  |  |  |
| recur | ifx-natural | ok | 21 | 0.01396 | 0.01479 | 0.0006251 |  |  | parity |  | openmp runtime linked | 53.5 |  |  |  |
| recur | ifx-expert | ok | 21 | 0.006968 | 0.007337 | 0.0002349 |  |  | LOSS 1.5-3x |  | openmp runtime linked | 52.9 |  |  |  |
| stencil | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | LLVM lane has no OpenMP |
| stencil | blade-cpp | ok | 21 | 0.01175 | 0.01208 | 0.001001 | ref |  |  | in-order | openmp (2 construct(s)); threaded maps, serial folds (BLADE_FP_REPRO) | 51.3 | 0.006483 [0.006212, 0.006821] | ref |  |
| stencil | gfortran-natural | ok | 21 | 0.008449 | 0.008628 | 0.0004169 | 0.714 [0.695, 0.739] | suppressed | parity (bandwidth-bound) |  | openmp runtime linked | 51.3 | 0.004238 [0.004078, 0.004529] | 0.654 [0.612, 0.701] |  |
| stencil | gfortran-expert | ok | 21 | 0.007571 | 0.008 | 0.0002345 | 0.662 [0.639, 0.675] | suppressed | parity |  | openmp runtime linked | 51.3 | 0.003642 [0.003474, 0.003743] | 0.562 [0.519, 0.588] |  |
| stencil | ifx-natural | ok | 21 | 0.00972 | 0.01001 | 0.0002891 | 0.828 [0.807, 0.839] | suppressed | parity (bandwidth-bound) |  | openmp runtime linked | 52.9 | 0.005217 [0.005068, 0.005325] | 0.805 [0.753, 0.848] |  |
| stencil | ifx-expert | ok | 21 | 0.008015 | 0.008333 | 0.0003509 | 0.690 [0.665, 0.707] | suppressed | parity |  | openmp runtime linked | 52.8 | 0.003556 [0.003353, 0.003754] | 0.549 [0.506, 0.586] |  |
| stencil | blade-llvm-inputonly | serial-only | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | LLVM lane has no OpenMP |
| stencil | blade-cpp-inputonly | ok | 21 | 0.005368 | 0.0056 | 0.0003565 |  |  |  | in-order | openmp (1 construct(s)); threaded maps, serial folds (BLADE_FP_REPRO) | 28.7 |  |  |  |
| stencil | gfortran-natural-inputonly | ok | 21 | 0.004152 | 0.00439 | 0.0001987 |  |  |  |  | openmp runtime linked | 28.4 |  |  |  |
| stencil | gfortran-expert-inputonly | ok | 21 | 0.004104 | 0.004357 | 0.0002225 |  |  |  |  | openmp runtime linked | 28.4 |  |  |  |
| stencil | ifx-natural-inputonly | ok | 21 | 0.004609 | 0.00479 | 0.0002811 |  |  |  |  | openmp runtime linked | 30.0 |  |  |  |
| stencil | ifx-expert-inputonly | ok | 21 | 0.004438 | 0.004777 | 0.0002441 |  |  |  |  | openmp runtime linked | 29.9 |  |  |  |

> stencil mt8-repro-native: inference suppressed -- trend: blade-cpp-inputonly first-half vs second-half median moved 5.6%

| dot | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | LLVM lane has no OpenMP |
| dot | blade-cpp | ok | 21 | 0.01299 | 0.01354 | 0.001746 | ref |  |  | in-order | openmp (1 construct(s)); threaded maps, serial folds (BLADE_FP_REPRO) | 51.5 | 0.002408 [0.001974, 0.003924] | ref |  |
| dot | gfortran-natural | ok | 21 | 0.01067 | 0.01119 | 0.0004212 | 0.827 [0.737, 0.847] | suppressed | parity |  | openmp runtime linked | 51.3 | 0.002484 [0.001998, 0.002873] | 1.032 [0.596, 1.265] |  |
| dot | gfortran-expert | ok | 21 | 0.01073 | 0.01107 | 0.0002932 | 0.818 [0.739, 0.833] | suppressed | parity |  | openmp runtime linked | 51.2 | 0.002648 [0.002491, 0.002777] | 1.099 [0.673, 1.345] |  |
| dot | ifx-natural | ok | 21 | 0.01076 | 0.01152 | 0.0002168 | 0.851 [0.773, 0.871] | suppressed | parity |  | openmp runtime linked | 52.8 | 0.002594 [0.002371, 0.002791] | 1.077 [0.659, 1.357] |  |
| dot | ifx-expert | ok | 21 | 0.01035 | 0.01148 | 0.000357 | 0.848 [0.768, 0.868] | suppressed | parity |  | openmp runtime linked | 52.8 | 0.00254 [0.002269, 0.002788] | 1.055 [0.654, 1.289] |  |
| dot | blade-llvm-inputonly | serial-only | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | LLVM lane has no OpenMP |
| dot | blade-cpp-inputonly | ok | 21 | 0.01059 | 0.01113 | 0.0006875 |  |  |  | in-order | openmp (1 construct(s)); threaded maps, serial folds (BLADE_FP_REPRO) | 51.5 |  |  |  |
| dot | gfortran-natural-inputonly | ok | 21 | 0.008123 | 0.00871 | 0.0008943 |  |  |  |  | openmp runtime linked | 51.2 |  |  |  |
| dot | gfortran-expert-inputonly | ok | 21 | 0.008143 | 0.008419 | 0.0003286 |  |  |  |  | openmp runtime linked | 51.2 |  |  |  |
| dot | ifx-natural-inputonly | ok | 21 | 0.008649 | 0.008924 | 0.0004473 |  |  |  |  | openmp runtime linked | 52.8 |  |  |  |
| dot | ifx-expert-inputonly | ok | 21 | 0.008603 | 0.008936 | 0.0005278 |  |  |  |  | openmp runtime linked | 52.8 |  |  |  |

> dot mt8-repro-native: inference suppressed -- trend: blade-cpp first-half vs second-half median moved 6.1%; trend: gfortran-natural-inputonly first-half vs second-half median moved 7.6%

| saxpy | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | LLVM lane has no OpenMP |
| saxpy | blade-cpp | ok | 21 | 0.01191 | 0.01225 | 0.0006324 | ref |  |  | in-order | openmp (2 construct(s)); threaded maps, serial folds (BLADE_FP_REPRO) | 51.2 | 0.004754 [0.004309, 0.005221] | ref |  |
| saxpy | gfortran-natural | ok | 21 | 0.009638 | 0.01005 | 0.0005983 | 0.821 [0.782, 0.839] | loss | parity |  | openmp runtime linked | 51.3 | 0.004411 [0.004125, 0.004677] | 0.928 [0.817, 1.055] |  |
| saxpy | gfortran-expert | ok | 21 | 0.008629 | 0.008936 | 0.0003292 | 0.729 [0.702, 0.743] | loss | parity |  | openmp runtime linked | 51.3 | 0.003286 [0.002769, 0.003523] | 0.691 [0.568, 0.800] |  |
| saxpy | ifx-natural | ok | 21 | 0.00916 | 0.01014 | 0.0003338 | 0.827 [0.786, 0.838] | loss | parity |  | openmp runtime linked | 52.9 | 0.003911 [0.003616, 0.00411] | 0.823 [0.721, 0.924] |  |
| saxpy | ifx-expert | ok | 21 | 0.009045 | 0.009411 | 0.0003086 | 0.768 [0.736, 0.782] | loss | parity |  | openmp runtime linked | 52.9 | 0.003249 [0.002969, 0.003534] | 0.683 [0.606, 0.781] |  |
| saxpy | blade-llvm-inputonly | serial-only | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | LLVM lane has no OpenMP |
| saxpy | blade-cpp-inputonly | ok | 21 | 0.007158 | 0.007496 | 0.001011 |  |  |  | in-order | openmp (1 construct(s)); threaded maps, serial folds (BLADE_FP_REPRO) | 36.2 |  |  |  |
| saxpy | gfortran-natural-inputonly | ok | 21 | 0.005301 | 0.005641 | 0.0002674 |  |  |  |  | openmp runtime linked | 36.0 |  |  |  |
| saxpy | gfortran-expert-inputonly | ok | 21 | 0.005282 | 0.00565 | 0.0006887 |  |  |  |  | openmp runtime linked | 36.0 |  |  |  |
| saxpy | ifx-natural-inputonly | ok | 21 | 0.005796 | 0.006225 | 0.0005403 |  |  |  |  | openmp runtime linked | 37.6 |  |  |  |
| saxpy | ifx-expert-inputonly | ok | 21 | 0.005743 | 0.006162 | 0.000498 |  |  |  |  | openmp runtime linked | 37.6 |  |  |  |

## Controls

- compute (recur/gfortran-expert/serial-repro-native/full): n=168 median 0.01273 s, baseline 0.0127 s
- memory (saxpy/gfortran-expert/serial-repro-native/control): n=168 median 0.005276 s, baseline 0.005268 s
