# Blade vs Fortran -- bench session 2026-10-19T13:53:44

- host LAPTOP-350H6LDB | AMD Ryzen 7 5800HS with Radeon Graphics | 8C/16T | 15.41 GB | power ac | max processor state AC 100% | boost AC None | Power Scheme GUID: 64a64f24-65b9-4b56-befd-5ec1eaced9b3  (Silent)
- git 39a8a869ff410c79edbe599a98257d4012241702 | Blade.dll bb52a4c2a008d198 | GNU Fortran (Rev4, Built by MSYS2 project) 16.2.0 | Intel(R) Fortran Compiler for applications running on Intel(R) 64, Version 2026.1.0 Build 20260617 | clang version 22.1.8 (https://github.com/msys2/MINGW-packages d34feaaa1d0aff624c870910b6f2589aa40d20de)
- size `full` | duration 1875.2 s | pins: pins-A.json, pins-B.json
- PRIMARY estimand: the complete timed region. ratio = median(arm) / median(Blade reference), round-block bootstrap 95% CI (10000 resamples); ratio > 1 = Blade faster. Verdicts (delta 0.1): win / detectable-win / equivalent / detectable-loss / loss / inconclusive; `suppressed` = the block failed a drift, trend or control check. Intervals are pointwise; no suite-wide claim. incremental = median(arm) - median(its own input-only twin), NOT kernel time. Reference: blade-llvm (serial), blade-cpp (mt8, or when the LLVM lane refused). Fresh-process, allocation-inclusive measurements.
- **FLAG**: antisym/mt8-licensed-native: inference suppressed: trend: ifx-natural first-half vs second-half median moved 6.2%
- **FLAG**: band/mt8-licensed-native: inference suppressed: trend: gfortran-natural first-half vs second-half median moved 6.6%
- **FLAG**: comoment3/mt8-licensed-native: inference suppressed: control memory median 0.005233s moved -8.8% vs the session baseline 0.005738s
- **FLAG**: gram/mt8-licensed-native: inference suppressed: control memory median 0.005283s moved -7.9% vs the session baseline 0.005738s
- **FLAG**: pairkernel/mt8-licensed-native: inference suppressed: trend: gfortran-natural first-half vs second-half median moved 6.0%; trend: ifx-natural first-half vs second-half median moved 10.3%
- **FLAG**: fusedchain/mt8-licensed-native: inference suppressed: control memory median 0.005248s moved -8.5% vs the session baseline 0.005738s
- **FLAG**: stencil/mt8-licensed-native: inference suppressed: trend: gfortran-natural first-half vs second-half median moved 6.5%; trend: blade-cpp-inputonly first-half vs second-half median moved 12.4%; trend: gfortran-natural-inputonly first-half vs second-half median moved 10.0%; trend: gfortran-expert-inputonly first-half vs second-half median moved 9.0%
- **FLAG**: dot/mt8-licensed-native: inference suppressed: trend: gfortran-expert first-half vs second-half median moved 6.8%; trend: ifx-expert first-half vs second-half median moved 7.6%; trend: blade-cpp-inputonly first-half vs second-half median moved 5.5%; trend: gfortran-natural-inputonly first-half vs second-half median moved 5.2%; trend: gfortran-expert-inputonly first-half vs second-half median moved 9.8%; trend: control memory moved 7.7% within the block
- **FLAG**: saxpy/mt8-licensed-native: inference suppressed: trend: gfortran-natural first-half vs second-half median moved 9.0%; trend: gfortran-natural-inputonly first-half vs second-half median moved 17.7%; trend: gfortran-expert-inputonly first-half vs second-half median moved 27.9%; trend: ifx-expert-inputonly first-half vs second-half median moved 12.2%

## mt8-licensed-native

Blade reference per kernel: antisym=blade-cpp, band=blade-cpp, comoment3=blade-cpp, gram=blade-cpp, pairkernel=blade-cpp, fusedchain=blade-cpp, onepass=None, recur=None, stencil=blade-cpp, dot=blade-cpp, saxpy=blade-cpp

| kernel | arm | status | n | min (s) | median (s) | IQR (s) | ratio vs ref [95% CI] | verdict | hypothesis | fold form | parallelism | peak WS (MB) | incremental (s) [CI] | incremental ratio [CI] | notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| antisym | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (4 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| antisym | blade-cpp | ok | 21 | 0.007741 | 0.009026 | 0.0007561 | ref |  |  | reordered (5 SIMD_REDUCTION/K-lane) | openmp (3 construct(s)) | 26.4 |  |  |  |
| antisym | gfortran-natural | ok | 21 | 0.1024 | 0.1097 | 0.003084 | 12.159 [11.662, 12.601] | suppressed | WIN ~2x (half the cells, no diagonal) |  | openmp runtime linked | 23.9 |  |  |  |
| antisym | gfortran-expert | ok | 21 | 0.009022 | 0.01065 | 0.0007128 | 1.179 [1.139, 1.268] | suppressed | parity +-15% |  | openmp runtime linked | 33.2 |  |  |  |
| antisym | ifx-natural | ok | 21 | 0.05066 | 0.06806 | 0.01062 | 7.541 [6.683, 7.934] | suppressed | WIN ~2x (half the cells, no diagonal) |  | openmp runtime linked | 25.4 |  |  |  |
| antisym | ifx-expert | ok | 21 | 0.009189 | 0.01065 | 0.0007297 | 1.180 [1.128, 1.246] | suppressed | parity +-15% |  | openmp runtime linked | 34.7 |  |  |  |

> antisym mt8-licensed-native: inference suppressed -- trend: ifx-natural first-half vs second-half median moved 6.2%

| band | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (2 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| band | blade-cpp | ok | 21 | 0.0009441 | 0.00107 | 0.0001975 | ref |  |  | reordered (1 SIMD_REDUCTION/K-lane) | openmp (2 construct(s)) | 6.0 |  |  |  |
| band | gfortran-natural | ok | 21 | 0.02717 | 0.04453 | 0.01388 | 41.616 [34.270, 47.779] | suppressed | win 10x+ (the natural tier is the n^2 predicate nest, a straw man by construction) |  | openmp runtime linked | 5.8 |  |  |  |
| band | gfortran-expert | ok | 21 | 0.0008848 | 0.00104 | 0.0001479 | 0.972 [0.844, 1.053] | suppressed | parity in both lanes: licensed folds walk the band by diagonal (k = i - j outer) as the licensed expert does, with phi hoisted per diagonal by the back end (G33); repro keeps the lex order with the terms vectorized and summed in order (G30) |  | openmp runtime linked | 5.8 |  |  |  |
| band | ifx-natural | ok | 21 | 0.02571 | 0.03906 | 0.004368 | 36.506 [33.131, 39.676] | suppressed | win 10x+ (the natural tier is the n^2 predicate nest, a straw man by construction) |  | openmp runtime linked | 7.4 |  |  |  |
| band | ifx-expert | ok | 21 | 0.001658 | 0.00178 | 0.0001878 | 1.663 [1.503, 1.803] | suppressed | parity in both lanes: licensed folds walk the band by diagonal (k = i - j outer) as the licensed expert does, with phi hoisted per diagonal by the back end (G33); repro keeps the lex order with the terms vectorized and summed in order (G30) |  | openmp runtime linked | 7.4 |  |  |  |

> band mt8-licensed-native: inference suppressed -- trend: gfortran-natural first-half vs second-half median moved 6.6%

| comoment3 | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (6 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| comoment3 | blade-cpp | ok | 21 | 0.00281 | 0.002973 | 0.0001981 | ref |  |  | reordered (6 SIMD_REDUCTION/K-lane) | openmp (3 construct(s)) | 13.2 |  |  |  |
| comoment3 | gfortran-natural | ok | 21 | 0.08302 | 0.09213 | 0.005129 | 30.993 [29.612, 32.287] | suppressed | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | openmp runtime linked | 44.0 |  |  |  |
| comoment3 | gfortran-expert | ok | 21 | 0.004714 | 0.004932 | 0.0002031 | 1.659 [1.598, 1.716] | suppressed | parity to 1.3x loss |  | openmp runtime linked | 14.6 |  |  |  |
| comoment3 | ifx-natural | ok | 21 | 0.09901 | 0.115 | 0.006036 | 38.690 [36.688, 39.793] | suppressed | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | openmp runtime linked | 45.5 |  |  |  |
| comoment3 | ifx-expert | ok | 21 | 0.003814 | 0.004082 | 0.0001704 | 1.373 [1.299, 1.406] | suppressed | parity to 1.3x loss |  | openmp runtime linked | 15.8 |  |  |  |

> comoment3 mt8-licensed-native: inference suppressed -- control memory median 0.005233s moved -8.8% vs the session baseline 0.005738s

| gram | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (4 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| gram | blade-cpp | ok | 21 | 0.01313 | 0.01396 | 0.001079 | ref |  |  | reordered (4 SIMD_REDUCTION/K-lane) | openmp (3 construct(s)) | 32.3 |  |  |  |
| gram | gfortran-natural | ok | 21 | 0.4394 | 0.5261 | 0.03592 | 37.696 [35.249, 39.163] | suppressed | win 1.5-2x if matmul is a naive nest; parity/loss if a blocked runtime kernel |  | openmp runtime linked | 37.4 |  |  | matmul is unannotated: parallel preparation, serial kernel unless the runtime threads matmul (FORT records) |
| gram | gfortran-expert | ok | 21 | 0.01882 | 0.01944 | 0.0009085 | 1.393 [1.309, 1.441] | suppressed | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | openmp runtime linked | 52.2 |  |  |  |
| gram | ifx-natural | ok | 21 | 0.5807 | 0.5954 | 0.01146 | 42.661 [40.085, 43.202] | suppressed | win 1.5-2x if matmul is a naive nest; parity/loss if a blocked runtime kernel |  | openmp runtime linked | 58.3 |  |  | matmul is unannotated: parallel preparation, serial kernel unless the runtime threads matmul (FORT records) |
| gram | ifx-expert | ok | 21 | 0.01775 | 0.01859 | 0.0009207 | 1.332 [1.238, 1.361] | suppressed | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | openmp runtime linked | 53.8 |  |  |  |

> gram mt8-licensed-native: inference suppressed -- control memory median 0.005283s moved -7.9% vs the session baseline 0.005738s

| pairkernel | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (4 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| pairkernel | blade-cpp | ok | 21 | 0.007255 | 0.007541 | 0.0003747 | ref |  |  | reordered (3 SIMD_REDUCTION/K-lane) | openmp (4 construct(s)) | 29.9 |  |  |  |
| pairkernel | gfortran-natural | ok | 21 | 0.01338 | 0.01482 | 0.002494 | 1.966 [1.842, 2.116] | suppressed | WIN ~2x (half the transcendentals) |  | openmp runtime linked | 53.4 |  |  |  |
| pairkernel | gfortran-expert | ok | 21 | 0.005824 | 0.006032 | 0.0003175 | 0.800 [0.768, 0.829] | suppressed | parity within 5% |  | openmp runtime linked | 29.5 |  |  |  |
| pairkernel | ifx-natural | ok | 21 | 0.01461 | 0.01561 | 0.002417 | 2.070 [1.962, 2.299] | suppressed | WIN ~2x (half the transcendentals) |  | openmp runtime linked | 54.9 |  |  |  |
| pairkernel | ifx-expert | ok | 21 | 0.005684 | 0.006014 | 0.0001762 | 0.798 [0.770, 0.816] | suppressed | parity within 5% |  | openmp runtime linked | 31.0 |  |  |  |

> pairkernel mt8-licensed-native: inference suppressed -- trend: gfortran-natural first-half vs second-half median moved 6.0%; trend: ifx-natural first-half vs second-half median moved 10.3%

| fusedchain | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (2 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| fusedchain | blade-cpp | ok | 21 | 0.003024 | 0.003177 | 0.0001816 | ref |  |  | reordered (1 SIMD_REDUCTION/K-lane) | openmp (2 construct(s)) | 21.1 | 0.0002939 [0.0001783, 0.0004165] | ref |  |
| fusedchain | gfortran-natural | ok | 21 | 0.01095 | 0.01125 | 0.0006621 | 3.542 [3.438, 3.648] | suppressed | WIN 1.5-2.5x (no temporaries) |  | openmp runtime linked | 66.6 | 0.007614 [0.007374, 0.008037] | 25.907 [18.785, 43.094] |  |
| fusedchain | gfortran-expert | ok | 21 | 0.002988 | 0.003245 | 0.0004661 | 1.021 [0.955, 1.101] | suppressed | parity |  | openmp runtime linked | 20.8 | 0.0005357 [0.0003633, 0.0007629] | 1.823 [1.046, 3.346] |  |
| fusedchain | ifx-natural | ok | 21 | 0.01117 | 0.01174 | 0.0008496 | 3.693 [3.526, 3.834] | suppressed | WIN 1.5-2.5x (no temporaries) |  | openmp runtime linked | 68.3 | 0.007712 [0.007334, 0.008067] | 26.239 [18.325, 43.071] |  |
| fusedchain | ifx-expert | ok | 21 | 0.003317 | 0.003508 | 0.0002499 | 1.104 [1.063, 1.151] | suppressed | parity |  | openmp runtime linked | 22.3 | 0.0002328 [0.0001606, 0.0005058] | 0.792 [0.497, 1.954] |  |
| fusedchain | blade-llvm-inputonly | serial-only | 0 | -- | -- | -- |  |  |  | reordered (1 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| fusedchain | blade-cpp-inputonly | ok | 21 | 0.002731 | 0.002884 | 0.0001647 |  |  |  | in-order | openmp (1 construct(s)) | 21.1 |  |  |  |
| fusedchain | gfortran-natural-inputonly | ok | 21 | 0.003374 | 0.003641 | 0.0002406 |  |  |  |  | openmp runtime linked | 20.8 |  |  |  |
| fusedchain | gfortran-expert-inputonly | ok | 21 | 0.00261 | 0.00271 | 0.0001068 |  |  |  |  | openmp runtime linked | 20.8 |  |  |  |
| fusedchain | ifx-natural-inputonly | ok | 21 | 0.003866 | 0.004024 | 0.000157 |  |  |  |  | openmp runtime linked | 22.3 |  |  |  |
| fusedchain | ifx-expert-inputonly | ok | 21 | 0.002954 | 0.003276 | 0.0002851 |  |  |  |  | openmp runtime linked | 22.3 |  |  |  |

> fusedchain mt8-licensed-native: inference suppressed -- control memory median 0.005248s moved -8.5% vs the session baseline 0.005738s

| onepass | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (5 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| onepass | blade-cpp | no-parallel-form | 0 | -- | -- | -- |  |  |  | reordered (1 SIMD_REDUCTION/K-lane) |  |  |  |  | kernel.json no_parallel_form (spec: no licensed parallel spelling) |
| onepass | gfortran-natural | ok | 21 | 0.01303 | 0.01585 | 0.001196 |  |  | WIN 2-3x (one pass vs four) |  | openmp runtime linked | 43.7 |  |  |  |
| onepass | gfortran-expert | ok | 21 | 0.008382 | 0.009014 | 0.001276 |  |  | parity |  | openmp runtime linked | 43.7 |  |  |  |
| onepass | ifx-natural | ok | 21 | 0.0148 | 0.01582 | 0.001107 |  |  | WIN 2-3x (one pass vs four) |  | openmp runtime linked | 45.2 |  |  |  |
| onepass | ifx-expert | ok | 21 | 0.008825 | 0.009522 | 0.0009399 |  |  | parity |  | openmp runtime linked | 45.2 |  |  |  |
| onepass | blade-llvm-inputonly | serial-only | 0 | -- | -- | -- |  |  |  | reordered (1 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| onepass | blade-cpp-inputonly | no-parallel-form | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | kernel.json no_parallel_form (spec: no licensed parallel spelling) |
| onepass | gfortran-natural-inputonly | ok | 21 | 0.008165 | 0.00893 | 0.0007497 |  |  |  |  | openmp runtime linked | 43.7 |  |  |  |
| onepass | gfortran-expert-inputonly | ok | 21 | 0.006343 | 0.007309 | 0.001297 |  |  |  |  | openmp runtime linked | 43.7 |  |  |  |
| onepass | ifx-natural-inputonly | ok | 21 | 0.008568 | 0.009312 | 0.0008134 |  |  |  |  | openmp runtime linked | 45.2 |  |  |  |
| onepass | ifx-expert-inputonly | ok | 21 | 0.006803 | 0.007768 | 0.001081 |  |  |  |  | openmp runtime linked | 45.2 |  |  |  |
| recur | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (3 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| recur | blade-cpp | no-parallel-form | 0 | -- | -- | -- |  |  |  | reordered (3 SIMD_REDUCTION/K-lane) |  |  |  |  | kernel.json no_parallel_form (spec: no licensed parallel spelling) |
| recur | gfortran-natural | ok | 21 | 0.06971 | 0.07205 | 0.001387 |  |  | parity |  | openmp runtime linked | 52.1 |  |  |  |
| recur | gfortran-expert | ok | 21 | 0.006295 | 0.007077 | 0.0008695 |  |  | LOSS 1.5-3x |  | openmp runtime linked | 51.5 |  |  |  |
| recur | ifx-natural | ok | 21 | 0.0151 | 0.01575 | 0.0008653 |  |  | parity |  | openmp runtime linked | 53.6 |  |  |  |
| recur | ifx-expert | ok | 21 | 0.007023 | 0.007951 | 0.0007818 |  |  | LOSS 1.5-3x |  | openmp runtime linked | 53.0 |  |  |  |
| stencil | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (4 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| stencil | blade-cpp | ok | 21 | 0.008107 | 0.009579 | 0.001043 | ref |  |  | reordered (4 SIMD_REDUCTION/K-lane) | openmp (3 construct(s)) | 51.4 | 0.004917 [0.004177, 0.005553] | ref |  |
| stencil | gfortran-natural | ok | 21 | 0.007569 | 0.00938 | 0.001 | 0.979 [0.926, 1.055] | suppressed | parity (bandwidth-bound) |  | openmp runtime linked | 51.4 | 0.004844 [0.004421, 0.005474] | 0.985 [0.850, 1.202] |  |
| stencil | gfortran-expert | ok | 21 | 0.007056 | 0.008481 | 0.0007709 | 0.885 [0.828, 0.970] | suppressed | parity |  | openmp runtime linked | 51.4 | 0.003855 [0.00343, 0.004267] | 0.784 [0.643, 0.982] |  |
| stencil | ifx-natural | ok | 21 | 0.009618 | 0.01107 | 0.0005245 | 1.155 [1.101, 1.225] | suppressed | parity (bandwidth-bound) |  | openmp runtime linked | 53.0 | 0.00599 [0.005685, 0.006242] | 1.218 [1.055, 1.460] |  |
| stencil | ifx-expert | ok | 21 | 0.008056 | 0.008868 | 0.0007174 | 0.926 [0.865, 0.998] | suppressed | parity |  | openmp runtime linked | 52.9 | 0.003978 [0.003428, 0.004282] | 0.809 [0.665, 0.968] |  |
| stencil | blade-llvm-inputonly | serial-only | 0 | -- | -- | -- |  |  |  | reordered (2 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| stencil | blade-cpp-inputonly | ok | 21 | 0.003974 | 0.004661 | 0.001113 |  |  |  | reordered (1 SIMD_REDUCTION/K-lane) | openmp (1 construct(s)) | 28.7 |  |  |  |
| stencil | gfortran-natural-inputonly | ok | 21 | 0.003987 | 0.004536 | 0.0007086 |  |  |  |  | openmp runtime linked | 28.5 |  |  |  |
| stencil | gfortran-expert-inputonly | ok | 21 | 0.003799 | 0.004626 | 0.0004922 |  |  |  |  | openmp runtime linked | 28.5 |  |  |  |
| stencil | ifx-natural-inputonly | ok | 21 | 0.004539 | 0.005076 | 0.0005038 |  |  |  |  | openmp runtime linked | 30.0 |  |  |  |
| stencil | ifx-expert-inputonly | ok | 21 | 0.004462 | 0.004891 | 0.0004876 |  |  |  |  | openmp runtime linked | 30.0 |  |  |  |

> stencil mt8-licensed-native: inference suppressed -- trend: gfortran-natural first-half vs second-half median moved 6.5%; trend: blade-cpp-inputonly first-half vs second-half median moved 12.4%; trend: gfortran-natural-inputonly first-half vs second-half median moved 10.0%; trend: gfortran-expert-inputonly first-half vs second-half median moved 9.0%

| dot | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (3 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| dot | blade-cpp | ok | 21 | 0.01037 | 0.01194 | 0.001269 | ref |  |  | reordered (1 SIMD_REDUCTION/K-lane) | openmp (2 construct(s)) | 51.6 | 0.002157 [0.001272, 0.002903] | ref |  |
| dot | gfortran-natural | ok | 21 | 0.01112 | 0.01256 | 0.001063 | 1.052 [0.933, 1.110] | suppressed | parity |  | openmp runtime linked | 51.3 | 0.002908 [0.001865, 0.003209] | 1.348 [0.733, 2.213] |  |
| dot | gfortran-expert | ok | 21 | 0.01041 | 0.01148 | 0.001174 | 0.962 [0.869, 1.018] | suppressed | parity |  | openmp runtime linked | 51.3 | 0.002154 [0.00123, 0.002495] | 0.999 [0.550, 1.619] |  |
| dot | ifx-natural | ok | 21 | 0.01157 | 0.01282 | 0.0009062 | 1.074 [0.982, 1.141] | suppressed | parity |  | openmp runtime linked | 52.9 | 0.002522 [0.001559, 0.003581] | 1.169 [0.682, 2.247] |  |
| dot | ifx-expert | ok | 21 | 0.01104 | 0.01149 | 0.001144 | 0.962 [0.908, 1.067] | suppressed | parity |  | openmp runtime linked | 52.9 | 0.001733 [0.001278, 0.002725] | 0.804 [0.524, 1.776] |  |
| dot | blade-llvm-inputonly | serial-only | 0 | -- | -- | -- |  |  |  | reordered (2 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| dot | blade-cpp-inputonly | ok | 21 | 0.008455 | 0.009782 | 0.001226 |  |  |  | in-order | openmp (1 construct(s)) | 51.6 |  |  |  |
| dot | gfortran-natural-inputonly | ok | 21 | 0.008656 | 0.009654 | 0.001063 |  |  |  |  | openmp runtime linked | 51.3 |  |  |  |
| dot | gfortran-expert-inputonly | ok | 21 | 0.00795 | 0.009328 | 0.0007857 |  |  |  |  | openmp runtime linked | 51.3 |  |  |  |
| dot | ifx-natural-inputonly | ok | 21 | 0.0087 | 0.0103 | 0.001475 |  |  |  |  | openmp runtime linked | 52.8 |  |  |  |
| dot | ifx-expert-inputonly | ok | 21 | 0.008311 | 0.009752 | 0.0008473 |  |  |  |  | openmp runtime linked | 52.9 |  |  |  |

> dot mt8-licensed-native: inference suppressed -- trend: gfortran-expert first-half vs second-half median moved 6.8%; trend: ifx-expert first-half vs second-half median moved 7.6%; trend: blade-cpp-inputonly first-half vs second-half median moved 5.5%; trend: gfortran-natural-inputonly first-half vs second-half median moved 5.2%; trend: gfortran-expert-inputonly first-half vs second-half median moved 9.8%; trend: control memory moved 7.7% within the block

| saxpy | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (3 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| saxpy | blade-cpp | ok | 21 | 0.01063 | 0.01176 | 0.0009477 | ref |  |  | reordered (1 SIMD_REDUCTION/K-lane) | openmp (3 construct(s)) | 51.3 | 0.004793 [0.004245, 0.0052] | ref |  |
| saxpy | gfortran-natural | ok | 21 | 0.009777 | 0.01092 | 0.001288 | 0.929 [0.885, 0.977] | suppressed | parity |  | openmp runtime linked | 51.3 | 0.004444 [0.003887, 0.005092] | 0.927 [0.794, 1.083] |  |
| saxpy | gfortran-expert | ok | 21 | 0.008967 | 0.009646 | 0.0005901 | 0.821 [0.792, 0.857] | suppressed | parity |  | openmp runtime linked | 51.3 | 0.003185 [0.002628, 0.004297] | 0.665 [0.543, 0.910] |  |
| saxpy | ifx-natural | ok | 21 | 0.01008 | 0.01145 | 0.0006285 | 0.974 [0.931, 1.017] | suppressed | parity |  | openmp runtime linked | 52.9 | 0.004434 [0.003927, 0.004877] | 0.925 [0.784, 1.089] |  |
| saxpy | ifx-expert | ok | 21 | 0.008932 | 0.01008 | 0.000635 | 0.858 [0.814, 0.897] | suppressed | parity |  | openmp runtime linked | 52.9 | 0.003311 [0.002778, 0.003814] | 0.691 [0.562, 0.850] |  |
| saxpy | blade-llvm-inputonly | serial-only | 0 | -- | -- | -- |  |  |  | reordered (2 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| saxpy | blade-cpp-inputonly | ok | 21 | 0.005704 | 0.006962 | 0.0006375 |  |  |  | in-order | openmp (1 construct(s)) | 36.3 |  |  |  |
| saxpy | gfortran-natural-inputonly | ok | 21 | 0.005353 | 0.006474 | 0.001414 |  |  |  |  | openmp runtime linked | 36.1 |  |  |  |
| saxpy | gfortran-expert-inputonly | ok | 21 | 0.00505 | 0.006461 | 0.001729 |  |  |  |  | openmp runtime linked | 36.1 |  |  |  |
| saxpy | ifx-natural-inputonly | ok | 21 | 0.005915 | 0.007017 | 0.0009364 |  |  |  |  | openmp runtime linked | 37.6 |  |  |  |
| saxpy | ifx-expert-inputonly | ok | 21 | 0.005275 | 0.006772 | 0.001038 |  |  |  |  | openmp runtime linked | 37.6 |  |  |  |

> saxpy mt8-licensed-native: inference suppressed -- trend: gfortran-natural first-half vs second-half median moved 9.0%; trend: gfortran-natural-inputonly first-half vs second-half median moved 17.7%; trend: gfortran-expert-inputonly first-half vs second-half median moved 27.9%; trend: ifx-expert-inputonly first-half vs second-half median moved 12.2%


## Controls

- compute (recur/gfortran-expert/serial-repro-native/full): n=231 median 0.01312 s, baseline 0.01353 s
- memory (saxpy/gfortran-expert/serial-repro-native/control): n=231 median 0.005418 s, baseline 0.005738 s
