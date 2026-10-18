# Blade vs Fortran -- bench session 2026-10-18T10:03:29

- host LAPTOP-350H6LDB | AMD Ryzen 7 5800HS with Radeon Graphics | 8C/16T | 15.41 GB | power ac | max processor state AC 100% | boost AC None | Power Scheme GUID: 64a64f24-65b9-4b56-befd-5ec1eaced9b3  (Silent)
- git 927776980d0669edd6b9cc822833ce119dcaabd4 | Blade.dll 74b6728036b170d9 | GNU Fortran (Rev4, Built by MSYS2 project) 16.2.0 | Intel(R) Fortran Compiler for applications running on Intel(R) 64, Version 2026.1.0 Build 20260617 | clang version 22.1.8 (https://github.com/msys2/MINGW-packages d34feaaa1d0aff624c870910b6f2589aa40d20de)
- size `full` | duration 4113.6 s | pins: pins-A.json, pins-B.json
- PRIMARY estimand: the complete timed region. ratio = median(arm) / median(Blade reference), round-block bootstrap 95% CI (10000 resamples); ratio > 1 = Blade faster. Verdicts (delta 0.1): win / detectable-win / equivalent / detectable-loss / loss / inconclusive; `suppressed` = the block failed a drift, trend or control check. Intervals are pointwise; no suite-wide claim. incremental = median(arm) - median(its own input-only twin), NOT kernel time. Reference: blade-llvm (serial), blade-cpp (mt8, or when the LLVM lane refused). Fresh-process, allocation-inclusive measurements.
- **ABORTED**: saxpy/mt8-licensed-native/blade-cpp-inputonly: run took 0.043s, 7.0x its running median 0.006s -- stopping (charter 2.5)
- **FLAG**: comoment3/mt8-licensed-native: inference suppressed: trend: ifx-expert first-half vs second-half median moved 16.0%; control compute median 0.01513s moved +18.0% vs the session baseline 0.01283s; trend: control compute moved 22.9% within the block; control memory median 0.005838s moved +9.9% vs the session baseline 0.005311s; trend: control memory moved 8.4% within the block
- **FLAG**: gram/mt8-licensed-native: inference suppressed: trend: gfortran-natural first-half vs second-half median moved 7.7%; trend: control compute moved 21.3% within the block; trend: control memory moved 9.0% within the block
- **FLAG**: pairkernel/mt8-licensed-native: inference suppressed: control compute median 0.01457s moved +13.6% vs the session baseline 0.01283s; trend: control compute moved 5.4% within the block; trend: control memory moved 8.5% within the block
- **FLAG**: fusedchain/mt8-licensed-native: inference suppressed: trend: blade-cpp first-half vs second-half median moved 7.7%; trend: gfortran-natural first-half vs second-half median moved 9.9%; trend: gfortran-expert-inputonly first-half vs second-half median moved 7.4%; trend: control memory moved 5.3% within the block
- **FLAG**: onepass/mt8-licensed-native: inference suppressed: trend: ifx-expert first-half vs second-half median moved 6.7%; trend: gfortran-natural-inputonly first-half vs second-half median moved 8.0%; trend: ifx-expert-inputonly first-half vs second-half median moved 8.7%; trend: control compute moved 7.1% within the block
- **FLAG**: recur/mt8-licensed-native: inference suppressed: trend: ifx-natural first-half vs second-half median moved 10.1%; trend: ifx-expert first-half vs second-half median moved 17.4%; control compute median 0.01426s moved +11.1% vs the session baseline 0.01283s; control memory median 0.005753s moved +8.3% vs the session baseline 0.005311s
- **FLAG**: stencil/mt8-licensed-native: inference suppressed: trend: blade-cpp first-half vs second-half median moved 18.9%; trend: gfortran-natural first-half vs second-half median moved 7.6%; trend: gfortran-expert first-half vs second-half median moved 9.0%; trend: ifx-natural first-half vs second-half median moved 6.5%; trend: ifx-expert first-half vs second-half median moved 12.4%; trend: blade-cpp-inputonly first-half vs second-half median moved 10.0%; trend: gfortran-natural-inputonly first-half vs second-half median moved 12.8%; control compute median 0.0142s moved +10.7% vs the session baseline 0.01283s; trend: control compute moved 9.5% within the block; trend: control memory moved 7.6% within the block
- **FLAG**: dot/mt8-licensed-native: inference suppressed: trend: blade-cpp-inputonly first-half vs second-half median moved 15.4%; trend: gfortran-natural-inputonly first-half vs second-half median moved 6.4%; trend: ifx-expert-inputonly first-half vs second-half median moved 8.1%

## mt8-licensed-native

Blade reference per kernel: antisym=blade-cpp, comoment3=blade-cpp, gram=blade-cpp, pairkernel=blade-cpp, fusedchain=blade-cpp, onepass=None, recur=None, stencil=blade-cpp, dot=blade-cpp, saxpy=blade-cpp

| kernel | arm | status | n | min (s) | median (s) | IQR (s) | ratio vs ref [95% CI] | verdict | hypothesis | fold form | parallelism | peak WS (MB) | incremental (s) [CI] | incremental ratio [CI] | notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| antisym | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (7 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| antisym | blade-cpp | ok | 21 | 0.02897 | 0.02968 | 0.0007013 | ref |  |  | reordered (6 SIMD_REDUCTION) | openmp (1 construct(s)) | 18.5 |  |  |  |
| antisym | gfortran-natural | ok | 21 | 0.1067 | 0.1125 | 0.001929 | 3.790 [3.731, 3.820] | win | WIN ~2x (half the cells, no diagonal) |  | openmp runtime linked | 23.7 |  |  |  |
| antisym | gfortran-expert | ok | 21 | 0.009104 | 0.009765 | 0.001022 | 0.329 [0.317, 0.348] | loss | parity +-15% |  | openmp runtime linked | 33.0 |  |  |  |
| antisym | ifx-natural | ok | 21 | 0.06188 | 0.0703 | 0.005985 | 2.368 [2.280, 2.467] | win | WIN ~2x (half the cells, no diagonal) |  | openmp runtime linked | 25.2 |  |  |  |
| antisym | ifx-expert | ok | 21 | 0.00959 | 0.01027 | 0.0007247 | 0.346 [0.331, 0.355] | loss | parity +-15% |  | openmp runtime linked | 34.5 |  |  |  |
| comoment3 | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (6 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| comoment3 | blade-cpp | ok | 7 | 0.08593 | 0.08698 | 0.001226 | ref |  |  | reordered (6 SIMD_REDUCTION) | openmp (1 construct(s)) | 12.4 |  |  |  |
| comoment3 | gfortran-natural | ok | 7 | 0.09401 | 0.09923 | 0.004487 | 1.141 [1.086, 1.167] | suppressed | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | openmp runtime linked | 43.7 |  |  |  |
| comoment3 | gfortran-expert | ok | 7 | 0.004848 | 0.005536 | 0.0007303 | 0.064 [0.055, 0.066] | suppressed | parity to 1.3x loss |  | openmp runtime linked | 14.3 |  |  |  |
| comoment3 | ifx-natural | ok | 7 | 0.116 | 0.1221 | 0.007885 | 1.404 [1.342, 1.489] | suppressed | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | openmp runtime linked | 45.2 |  |  |  |
| comoment3 | ifx-expert | ok | 7 | 0.003965 | 0.004208 | 0.0004814 | 0.048 [0.047, 0.057] | suppressed | parity to 1.3x loss |  | openmp runtime linked | 15.6 |  |  |  |

> comoment3 mt8-licensed-native: inference suppressed -- trend: ifx-expert first-half vs second-half median moved 16.0%; control compute median 0.01513s moved +18.0% vs the session baseline 0.01283s; trend: control compute moved 22.9% within the block; control memory median 0.005838s moved +9.9% vs the session baseline 0.005311s; trend: control memory moved 8.4% within the block

| gram | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (8 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| gram | blade-cpp | ok | 7 | 0.05847 | 0.06153 | 0.005836 | ref |  |  | reordered (5 SIMD_REDUCTION) | openmp (1 construct(s)) | 27.6 |  |  |  |
| gram | gfortran-natural | ok | 7 | 0.5134 | 0.6077 | 0.08326 | 9.876 [8.820, 10.883] | suppressed | win 1.5-2x if matmul is a naive nest; parity/loss if a blocked runtime kernel |  | openmp runtime linked | 37.1 |  |  | matmul is unannotated: parallel preparation, serial kernel unless the runtime threads matmul (FORT records) |
| gram | gfortran-expert | ok | 7 | 0.02057 | 0.02165 | 0.001161 | 0.352 [0.322, 0.371] | suppressed | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | openmp runtime linked | 51.9 |  |  |  |
| gram | ifx-natural | ok | 7 | 0.622 | 0.6436 | 0.01549 | 10.460 [9.565, 10.837] | suppressed | win 1.5-2x if matmul is a naive nest; parity/loss if a blocked runtime kernel |  | openmp runtime linked | 58.1 |  |  | matmul is unannotated: parallel preparation, serial kernel unless the runtime threads matmul (FORT records) |
| gram | ifx-expert | ok | 7 | 0.02002 | 0.02052 | 0.0004351 | 0.334 [0.298, 0.344] | suppressed | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | openmp runtime linked | 53.6 |  |  |  |

> gram mt8-licensed-native: inference suppressed -- trend: gfortran-natural first-half vs second-half median moved 7.7%; trend: control compute moved 21.3% within the block; trend: control memory moved 9.0% within the block

| pairkernel | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (4 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| pairkernel | blade-cpp | ok | 21 | 0.05519 | 0.05652 | 0.001326 | ref |  |  | reordered (3 SIMD_REDUCTION) | openmp (1 construct(s)) | 29.6 |  |  |  |
| pairkernel | gfortran-natural | ok | 21 | 0.01544 | 0.01662 | 0.002 | 0.294 [0.285, 0.319] | suppressed | WIN ~2x (half the transcendentals) |  | openmp runtime linked | 53.2 |  |  |  |
| pairkernel | gfortran-expert | ok | 21 | 0.005938 | 0.006237 | 0.0003636 | 0.110 [0.109, 0.115] | suppressed | parity within 5% |  | openmp runtime linked | 29.3 |  |  |  |
| pairkernel | ifx-natural | ok | 21 | 0.01693 | 0.0178 | 0.0009941 | 0.315 [0.307, 0.324] | suppressed | WIN ~2x (half the transcendentals) |  | openmp runtime linked | 54.6 |  |  |  |
| pairkernel | ifx-expert | ok | 21 | 0.00585 | 0.006318 | 0.000441 | 0.112 [0.107, 0.115] | suppressed | parity within 5% |  | openmp runtime linked | 30.8 |  |  |  |

> pairkernel mt8-licensed-native: inference suppressed -- control compute median 0.01457s moved +13.6% vs the session baseline 0.01283s; trend: control compute moved 5.4% within the block; trend: control memory moved 8.5% within the block

| fusedchain | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (2 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| fusedchain | blade-cpp | ok | 21 | 0.003739 | 0.004128 | 0.0005298 | ref |  |  | in-order | openmp (2 construct(s)) | 20.8 | 0.0006109 [0.000239, 0.0009268] | ref |  |
| fusedchain | gfortran-natural | ok | 21 | 0.01114 | 0.01247 | 0.001589 | 3.020 [2.824, 3.239] | suppressed | WIN 1.5-2.5x (no temporaries) |  | openmp runtime linked | 66.4 | 0.008881 [0.008439, 0.009459] | 14.538 [9.490, 38.110] |  |
| fusedchain | gfortran-expert | ok | 21 | 0.003037 | 0.003253 | 0.0003655 | 0.788 [0.738, 0.829] | suppressed | parity |  | openmp runtime linked | 20.6 | 0.0003592 [6.47e-05, 0.0005791] | 0.588 [0.102, 1.551] |  |
| fusedchain | ifx-natural | ok | 21 | 0.01173 | 0.01263 | 0.0008225 | 3.060 [2.847, 3.186] | suppressed | WIN 1.5-2.5x (no temporaries) |  | openmp runtime linked | 68.0 | 0.008508 [0.008014, 0.008755] | 13.927 [9.047, 34.755] |  |
| fusedchain | ifx-expert | ok | 21 | 0.003298 | 0.003509 | 0.000343 | 0.850 [0.799, 0.916] | suppressed | parity |  | openmp runtime linked | 22.1 | 0.0003294 [9.51e-05, 0.000542] | 0.539 [0.154, 1.405] |  |
| fusedchain | blade-llvm-inputonly | serial-only | 0 | -- | -- | -- |  |  |  | reordered (1 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| fusedchain | blade-cpp-inputonly | ok | 21 | 0.003155 | 0.003517 | 0.0005411 |  |  |  | in-order | openmp (1 construct(s)) | 20.8 |  |  |  |
| fusedchain | gfortran-natural-inputonly | ok | 21 | 0.003444 | 0.003585 | 0.0001797 |  |  |  |  | openmp runtime linked | 20.6 |  |  |  |
| fusedchain | gfortran-expert-inputonly | ok | 21 | 0.002663 | 0.002894 | 0.0004293 |  |  |  |  | openmp runtime linked | 20.6 |  |  |  |
| fusedchain | ifx-natural-inputonly | ok | 21 | 0.003896 | 0.004124 | 0.0001186 |  |  |  |  | openmp runtime linked | 22.1 |  |  |  |
| fusedchain | ifx-expert-inputonly | ok | 21 | 0.003057 | 0.00318 | 0.0002564 |  |  |  |  | openmp runtime linked | 22.1 |  |  |  |

> fusedchain mt8-licensed-native: inference suppressed -- trend: blade-cpp first-half vs second-half median moved 7.7%; trend: gfortran-natural first-half vs second-half median moved 9.9%; trend: gfortran-expert-inputonly first-half vs second-half median moved 7.4%; trend: control memory moved 5.3% within the block

| onepass | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (5 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| onepass | blade-cpp | no-parallel-form | 0 | -- | -- | -- |  |  |  | reordered (1 SIMD_REDUCTION) |  |  |  |  | kernel.json no_parallel_form (spec: no licensed parallel spelling) |
| onepass | gfortran-natural | ok | 21 | 0.01455 | 0.01534 | 0.0006284 |  |  | WIN 2-3x (one pass vs four) |  | openmp runtime linked | 43.5 |  |  |  |
| onepass | gfortran-expert | ok | 21 | 0.007733 | 0.009075 | 0.0008121 |  |  | parity |  | openmp runtime linked | 43.5 |  |  |  |
| onepass | ifx-natural | ok | 21 | 0.01504 | 0.01576 | 0.0003841 |  |  | WIN 2-3x (one pass vs four) |  | openmp runtime linked | 45.0 |  |  |  |
| onepass | ifx-expert | ok | 21 | 0.008736 | 0.009403 | 0.0007688 |  |  | parity |  | openmp runtime linked | 45.0 |  |  |  |
| onepass | blade-llvm-inputonly | serial-only | 0 | -- | -- | -- |  |  |  | reordered (1 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| onepass | blade-cpp-inputonly | no-parallel-form | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | kernel.json no_parallel_form (spec: no licensed parallel spelling) |
| onepass | gfortran-natural-inputonly | ok | 21 | 0.008259 | 0.009145 | 0.0008675 |  |  |  |  | openmp runtime linked | 43.5 |  |  |  |
| onepass | gfortran-expert-inputonly | ok | 21 | 0.006545 | 0.007356 | 0.0009792 |  |  |  |  | openmp runtime linked | 43.5 |  |  |  |
| onepass | ifx-natural-inputonly | ok | 21 | 0.008725 | 0.009553 | 0.0007585 |  |  |  |  | openmp runtime linked | 45.0 |  |  |  |
| onepass | ifx-expert-inputonly | ok | 21 | 0.006871 | 0.007625 | 0.00106 |  |  |  |  | openmp runtime linked | 45.0 |  |  |  |

> onepass mt8-licensed-native: inference suppressed -- trend: ifx-expert first-half vs second-half median moved 6.7%; trend: gfortran-natural-inputonly first-half vs second-half median moved 8.0%; trend: ifx-expert-inputonly first-half vs second-half median moved 8.7%; trend: control compute moved 7.1% within the block

| recur | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (3 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| recur | blade-cpp | no-parallel-form | 0 | -- | -- | -- |  |  |  | reordered (3 SIMD_REDUCTION) |  |  |  |  | kernel.json no_parallel_form (spec: no licensed parallel spelling) |
| recur | gfortran-natural | ok | 21 | 0.0713 | 0.07324 | 0.002285 |  |  | parity |  | openmp runtime linked | 51.9 |  |  |  |
| recur | gfortran-expert | ok | 21 | 0.00657 | 0.007207 | 0.001139 |  |  | LOSS 1.5-3x |  | openmp runtime linked | 51.3 |  |  |  |
| recur | ifx-natural | ok | 21 | 0.01507 | 0.01636 | 0.002069 |  |  | parity |  | openmp runtime linked | 53.4 |  |  |  |
| recur | ifx-expert | ok | 21 | 0.006771 | 0.007911 | 0.001568 |  |  | LOSS 1.5-3x |  | openmp runtime linked | 52.8 |  |  |  |

> recur mt8-licensed-native: inference suppressed -- trend: ifx-natural first-half vs second-half median moved 10.1%; trend: ifx-expert first-half vs second-half median moved 17.4%; control compute median 0.01426s moved +11.1% vs the session baseline 0.01283s; control memory median 0.005753s moved +8.3% vs the session baseline 0.005311s

| stencil | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (4 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| stencil | blade-cpp | ok | 21 | 0.009584 | 0.01149 | 0.001915 | ref |  |  | reordered (3 SIMD_REDUCTION) | openmp (3 construct(s)) | 51.2 | 0.006293 [0.005562, 0.00729] | ref |  |
| stencil | gfortran-natural | ok | 21 | 0.00843 | 0.009477 | 0.0007641 | 0.825 [0.757, 0.894] | suppressed | parity (bandwidth-bound) |  | openmp runtime linked | 51.2 | 0.004899 [0.004588, 0.005327] | 0.778 [0.650, 0.917] |  |
| stencil | gfortran-expert | ok | 21 | 0.007594 | 0.008968 | 0.001189 | 0.780 [0.713, 0.833] | suppressed | parity |  | openmp runtime linked | 51.1 | 0.004372 [0.003748, 0.004718] | 0.695 [0.582, 0.789] |  |
| stencil | ifx-natural | ok | 21 | 0.009775 | 0.01019 | 0.0008873 | 0.887 [0.818, 0.954] | suppressed | parity (bandwidth-bound) |  | openmp runtime linked | 52.7 | 0.005049 [0.0048, 0.005485] | 0.802 [0.701, 0.912] |  |
| stencil | ifx-expert | ok | 21 | 0.008381 | 0.00912 | 0.001541 | 0.794 [0.730, 0.867] | suppressed | parity |  | openmp runtime linked | 52.7 | 0.004021 [0.003618, 0.005046] | 0.639 [0.550, 0.797] |  |
| stencil | blade-llvm-inputonly | serial-only | 0 | -- | -- | -- |  |  |  | reordered (2 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| stencil | blade-cpp-inputonly | ok | 21 | 0.004244 | 0.005197 | 0.000614 |  |  |  | reordered (1 SIMD_REDUCTION) | openmp (1 construct(s)) | 28.5 |  |  |  |
| stencil | gfortran-natural-inputonly | ok | 21 | 0.004179 | 0.004578 | 0.000661 |  |  |  |  | openmp runtime linked | 28.3 |  |  |  |
| stencil | gfortran-expert-inputonly | ok | 21 | 0.003923 | 0.004596 | 0.0003376 |  |  |  |  | openmp runtime linked | 28.3 |  |  |  |
| stencil | ifx-natural-inputonly | ok | 21 | 0.004793 | 0.005141 | 0.0002909 |  |  |  |  | openmp runtime linked | 29.8 |  |  |  |
| stencil | ifx-expert-inputonly | ok | 21 | 0.004557 | 0.005099 | 0.0004658 |  |  |  |  | openmp runtime linked | 29.8 |  |  |  |

> stencil mt8-licensed-native: inference suppressed -- trend: blade-cpp first-half vs second-half median moved 18.9%; trend: gfortran-natural first-half vs second-half median moved 7.6%; trend: gfortran-expert first-half vs second-half median moved 9.0%; trend: ifx-natural first-half vs second-half median moved 6.5%; trend: ifx-expert first-half vs second-half median moved 12.4%; trend: blade-cpp-inputonly first-half vs second-half median moved 10.0%; trend: gfortran-natural-inputonly first-half vs second-half median moved 12.8%; control compute median 0.0142s moved +10.7% vs the session baseline 0.01283s; trend: control compute moved 9.5% within the block; trend: control memory moved 7.6% within the block

| dot | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (3 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| dot | blade-cpp | ok | 21 | 0.01017 | 0.012 | 0.0009239 | ref |  |  | in-order | openmp (2 construct(s)) | 51.3 | 0.001677 [0.000969, 0.002381] | ref |  |
| dot | gfortran-natural | ok | 21 | 0.01073 | 0.01141 | 0.0006569 | 0.951 [0.928, 0.989] | suppressed | parity |  | openmp runtime linked | 51.1 | 0.002595 [0.002235, 0.003038] | 1.548 [1.081, 2.724] |  |
| dot | gfortran-expert | ok | 21 | 0.009646 | 0.01053 | 0.0006028 | 0.878 [0.859, 0.905] | suppressed | parity |  | openmp runtime linked | 51.1 | 0.002011 [0.001794, 0.002308] | 1.200 [0.809, 2.141] |  |
| dot | ifx-natural | ok | 21 | 0.0113 | 0.01183 | 0.0003726 | 0.986 [0.963, 1.025] | suppressed | parity |  | openmp runtime linked | 52.7 | 0.002403 [0.001975, 0.002696] | 1.433 [1.019, 2.425] |  |
| dot | ifx-expert | ok | 21 | 0.01023 | 0.01096 | 0.0004431 | 0.914 [0.888, 0.948] | suppressed | parity |  | openmp runtime linked | 52.7 | 0.00194 [0.00171, 0.002273] | 1.157 [0.797, 2.049] |  |
| dot | blade-llvm-inputonly | serial-only | 0 | -- | -- | -- |  |  |  | reordered (2 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| dot | blade-cpp-inputonly | ok | 21 | 0.00751 | 0.01032 | 0.001512 |  |  |  | in-order | openmp (1 construct(s)) | 51.3 |  |  |  |
| dot | gfortran-natural-inputonly | ok | 21 | 0.008162 | 0.008814 | 0.001058 |  |  |  |  | openmp runtime linked | 51.1 |  |  |  |
| dot | gfortran-expert-inputonly | ok | 21 | 0.007465 | 0.008519 | 0.0003739 |  |  |  |  | openmp runtime linked | 51.1 |  |  |  |
| dot | ifx-natural-inputonly | ok | 21 | 0.008582 | 0.009429 | 0.0007777 |  |  |  |  | openmp runtime linked | 52.7 |  |  |  |
| dot | ifx-expert-inputonly | ok | 21 | 0.007999 | 0.009023 | 0.0006695 |  |  |  |  | openmp runtime linked | 52.7 |  |  |  |

> dot mt8-licensed-native: inference suppressed -- trend: blade-cpp-inputonly first-half vs second-half median moved 15.4%; trend: gfortran-natural-inputonly first-half vs second-half median moved 6.4%; trend: ifx-expert-inputonly first-half vs second-half median moved 8.1%

| saxpy | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (3 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| saxpy | blade-cpp | ok | 0 | -- | -- | -- | ref |  |  | in-order | openmp (3 construct(s)) |  |  |  |  |
| saxpy | gfortran-natural | ok | 0 | -- | -- | -- |  |  | parity |  | openmp runtime linked |  |  |  |  |
| saxpy | gfortran-expert | ok | 0 | -- | -- | -- |  |  | parity |  | openmp runtime linked |  |  |  |  |
| saxpy | ifx-natural | ok | 0 | -- | -- | -- |  |  | parity |  | openmp runtime linked |  |  |  |  |
| saxpy | ifx-expert | ok | 0 | -- | -- | -- |  |  | parity |  | openmp runtime linked |  |  |  |  |
| saxpy | blade-llvm-inputonly | serial-only | 0 | -- | -- | -- |  |  |  | reordered (2 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| saxpy | blade-cpp-inputonly | ok | 0 | -- | -- | -- |  |  |  | in-order | openmp (1 construct(s)) |  |  |  |  |
| saxpy | gfortran-natural-inputonly | ok | 0 | -- | -- | -- |  |  |  |  | openmp runtime linked |  |  |  |  |
| saxpy | gfortran-expert-inputonly | ok | 0 | -- | -- | -- |  |  |  |  | openmp runtime linked |  |  |  |  |
| saxpy | ifx-natural-inputonly | ok | 0 | -- | -- | -- |  |  |  |  | openmp runtime linked |  |  |  |  |
| saxpy | ifx-expert-inputonly | ok | 0 | -- | -- | -- |  |  |  |  | openmp runtime linked |  |  |  |  |

## Controls

- compute (recur/gfortran-expert/serial-repro-native/full): n=168 median 0.01343 s, baseline 0.01283 s
- memory (saxpy/gfortran-expert/serial-repro-native/control): n=168 median 0.005474 s, baseline 0.005311 s

## Failures

- ABORTED: saxpy/mt8-licensed-native/blade-cpp-inputonly: run took 0.043s, 7.0x its running median 0.006s -- stopping (charter 2.5)
