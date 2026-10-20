# Blade vs Fortran -- bench session 2026-10-19T13:36:04

- host LAPTOP-350H6LDB | AMD Ryzen 7 5800HS with Radeon Graphics | 8C/16T | 15.41 GB | power ac | max processor state AC 100% | boost AC None | Power Scheme GUID: 64a64f24-65b9-4b56-befd-5ec1eaced9b3  (Silent)
- git 39a8a869ff410c79edbe599a98257d4012241702 | Blade.dll bb52a4c2a008d198 | GNU Fortran (Rev4, Built by MSYS2 project) 16.2.0 | Intel(R) Fortran Compiler for applications running on Intel(R) 64, Version 2026.1.0 Build 20260617 | clang version 22.1.8 (https://github.com/msys2/MINGW-packages d34feaaa1d0aff624c870910b6f2589aa40d20de)
- size `full` | duration 1059.3 s | pins: pins-A.json, pins-B.json
- PRIMARY estimand: the complete timed region. ratio = median(arm) / median(Blade reference), round-block bootstrap 95% CI (10000 resamples); ratio > 1 = Blade faster. Verdicts (delta 0.1): win / detectable-win / equivalent / detectable-loss / loss / inconclusive; `suppressed` = the block failed a drift, trend or control check. Intervals are pointwise; no suite-wide claim. incremental = median(arm) - median(its own input-only twin), NOT kernel time. Reference: blade-llvm (serial), blade-cpp (mt8, or when the LLVM lane refused). Fresh-process, allocation-inclusive measurements.
- **FLAG**: fusedchain/serial-licensed-native: inference suppressed: trend: gfortran-natural first-half vs second-half median moved 5.1%; trend: ifx-natural first-half vs second-half median moved 9.7%
- **FLAG**: maskagg/serial-licensed-native: inference suppressed: trend: blade-llvm first-half vs second-half median moved 5.8%
- **FLAG**: dot/serial-licensed-native: inference suppressed: trend: ifx-expert first-half vs second-half median moved 5.9%; trend: ifx-expert-inputonly first-half vs second-half median moved 5.0%; control memory median 0.005687s moved +7.6% vs the session baseline 0.005285s
- **FLAG**: saxpy/serial-licensed-native: inference suppressed: trend: blade-llvm first-half vs second-half median moved 5.1%

## serial-licensed-native

Blade reference per kernel: fusedchain=blade-llvm, maskagg=blade-llvm, onepass=blade-llvm, dot=blade-llvm, saxpy=blade-llvm

| kernel | arm | status | n | min (s) | median (s) | IQR (s) | ratio vs ref [95% CI] | verdict | hypothesis | fold form | parallelism | peak WS (MB) | incremental (s) [CI] | incremental ratio [CI] | notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| fusedchain | blade-llvm | ok | 21 | 0.004178 | 0.004382 | 0.0003411 | ref |  |  | reordered (2 reassoc flags) | serial | 19.9 | 0.0008608 [0.0006066, 0.0009661] | ref |  |
| fusedchain | blade-cpp | ok | 21 | 0.00841 | 0.008522 | 0.0002042 | 1.945 [1.894, 2.007] | suppressed |  | reordered (1 SIMD_REDUCTION/K-lane) | serial | 20.6 | 0.0007558 [0.0006526, 0.0008728] | 0.878 [0.715, 1.297] |  |
| fusedchain | gfortran-natural | ok | 21 | 0.01666 | 0.01701 | 0.00077 | 3.882 [3.759, 4.044] | suppressed | WIN 1.5-2.5x (no temporaries) |  | serial | 66.1 | 0.0104 [0.0102, 0.01091] | 12.083 [10.639, 17.036] |  |
| fusedchain | gfortran-expert | ok | 21 | 0.00665 | 0.006866 | 0.0001385 | 1.567 [1.530, 1.602] | suppressed | parity |  | serial | 20.4 | 0.0007403 [0.0006488, 0.0008226] | 0.860 [0.731, 1.199] |  |
| fusedchain | ifx-natural | ok | 21 | 0.01265 | 0.01317 | 0.001555 | 3.005 [2.923, 3.250] | suppressed | WIN 1.5-2.5x (no temporaries) |  | serial | 65.7 | 0.008732 [0.008506, 0.009824] | 10.144 [8.988, 14.577] |  |
| fusedchain | ifx-expert | ok | 21 | 0.004392 | 0.004483 | 0.0001306 | 1.023 [0.997, 1.054] | suppressed | parity |  | serial | 19.9 | 0.0007504 [0.0006553, 0.0008275] | 0.872 [0.725, 1.180] |  |
| fusedchain | blade-llvm-inputonly | ok | 21 | 0.003446 | 0.003522 | 0.0002553 |  |  |  | reordered (1 reassoc flags) | serial | 19.9 |  |  |  |
| fusedchain | blade-cpp-inputonly | ok | 21 | 0.007657 | 0.007767 | 0.0001628 |  |  |  | in-order | serial | 20.6 |  |  |  |
| fusedchain | gfortran-natural-inputonly | ok | 21 | 0.006528 | 0.006611 | 0.000121 |  |  |  |  | serial | 20.4 |  |  |  |
| fusedchain | gfortran-expert-inputonly | ok | 21 | 0.005991 | 0.006125 | 0.0001321 |  |  |  |  | serial | 20.4 |  |  |  |
| fusedchain | ifx-natural-inputonly | ok | 21 | 0.004314 | 0.004437 | 0.0001869 |  |  |  |  | serial | 19.9 |  |  |  |
| fusedchain | ifx-expert-inputonly | ok | 21 | 0.003639 | 0.003733 | 0.0002342 |  |  |  |  | serial | 19.9 |  |  |  |

> fusedchain serial-licensed-native: inference suppressed -- trend: gfortran-natural first-half vs second-half median moved 5.1%; trend: ifx-natural first-half vs second-half median moved 9.7%

| maskagg | blade-llvm | ok | 21 | 0.004427 | 0.00468 | 0.000364 | ref |  |  | reordered (2 reassoc flags) | serial | 24.6 | 0.003036 [0.002945, 0.003204] | ref |  |
| maskagg | blade-cpp | ok | 21 | 0.006733 | 0.007054 | 0.0003081 | 1.507 [1.441, 1.539] | suppressed |  | in-order | serial | 25.4 | 0.00328 [0.003053, 0.003341] | 1.080 [0.994, 1.112] |  |
| maskagg | gfortran-natural | ok | 21 | 0.008125 | 0.008219 | 0.0001489 | 1.756 [1.699, 1.796] | suppressed | LOSS 1.3-2x |  | serial | 17.5 | 0.004883 [0.00485, 0.004937] | 1.608 [1.520, 1.668] |  |
| maskagg | gfortran-expert | ok | 21 | 0.003939 | 0.004074 | 0.0001739 | 0.871 [0.835, 0.891] | suppressed | LOSS 1.3-2x |  | serial | 16.6 | 0.001164 [0.00109, 0.001253] | 0.383 [0.348, 0.415] |  |
| maskagg | ifx-natural | ok | 21 | 0.01018 | 0.01042 | 0.0002373 | 2.227 [2.155, 2.274] | suppressed | LOSS 1.3-2x |  | serial | 23.7 | 0.008249 [0.008106, 0.00831] | 2.717 [2.572, 2.795] |  |
| maskagg | ifx-expert | ok | 21 | 0.00287 | 0.002939 | 0.0001303 | 0.628 [0.608, 0.649] | suppressed | LOSS 1.3-2x |  | serial | 16.1 | 0.001188 [0.001139, 0.001278] | 0.391 [0.371, 0.421] |  |
| maskagg | blade-llvm-inputonly | ok | 21 | 0.001566 | 0.001643 | 4.3e-05 |  |  |  | reordered (1 reassoc flags) | serial | 12.2 |  |  |  |
| maskagg | blade-cpp-inputonly | ok | 21 | 0.003722 | 0.003774 | 7.73e-05 |  |  |  | in-order | serial | 13.0 |  |  |  |
| maskagg | gfortran-natural-inputonly | ok | 21 | 0.00328 | 0.003335 | 4.36e-05 |  |  |  |  | serial | 12.7 |  |  |  |
| maskagg | gfortran-expert-inputonly | ok | 21 | 0.002862 | 0.00291 | 8.02e-05 |  |  |  |  | serial | 12.7 |  |  |  |
| maskagg | ifx-natural-inputonly | ok | 21 | 0.00215 | 0.002172 | 5.73e-05 |  |  |  |  | serial | 12.3 |  |  |  |
| maskagg | ifx-expert-inputonly | ok | 21 | 0.001692 | 0.001751 | 7.45e-05 |  |  |  |  | serial | 12.3 |  |  |  |

> maskagg serial-licensed-native: inference suppressed -- trend: blade-llvm first-half vs second-half median moved 5.8%

| onepass | blade-llvm | ok | 21 | 0.01075 | 0.01094 | 0.000286 | ref |  |  | reordered (5 reassoc flags) | serial | 42.8 | 0.001791 [0.001739, 0.001958] | ref |  |
| onepass | blade-cpp | ok | 21 | 0.0208 | 0.02103 | 0.0003917 | 1.922 [1.888, 1.943] | win |  | reordered (1 SIMD_REDUCTION/K-lane) | serial | 43.5 | 0.00168 [0.001503, 0.001914] | 0.938 [0.807, 1.087] |  |
| onepass | gfortran-natural | ok | 21 | 0.03209 | 0.03237 | 0.0002545 | 2.958 [2.924, 2.976] | win | WIN 2-3x (one pass vs four) |  | serial | 43.2 | 0.01566 [0.01554, 0.01586] | 8.742 [7.992, 9.031] |  |
| onepass | gfortran-expert | ok | 21 | 0.01651 | 0.01669 | 0.0002199 | 1.525 [1.502, 1.534] | win | parity |  | serial | 43.2 | 0.001817 [0.001716, 0.001914] | 1.015 [0.910, 1.079] |  |
| onepass | ifx-natural | ok | 21 | 0.01248 | 0.01265 | 0.0002761 | 1.156 [1.141, 1.176] | win | WIN 2-3x (one pass vs four) |  | serial | 42.8 | 0.001353 [0.001183, 0.001562] | 0.755 [0.635, 0.877] |  |
| onepass | ifx-expert | ok | 21 | 0.01104 | 0.01129 | 0.0001318 | 1.031 [1.017, 1.040] | detectable-win | parity |  | serial | 42.8 | 0.001787 [0.001556, 0.001883] | 0.998 [0.862, 1.064] |  |
| onepass | blade-llvm-inputonly | ok | 21 | 0.008909 | 0.009154 | 0.0001729 |  |  |  | reordered (1 reassoc flags) | serial | 42.8 |  |  |  |
| onepass | blade-cpp-inputonly | ok | 21 | 0.01914 | 0.01935 | 0.0001871 |  |  |  | in-order | serial | 43.5 |  |  |  |
| onepass | gfortran-natural-inputonly | ok | 21 | 0.01653 | 0.01672 | 0.0001782 |  |  |  |  | serial | 43.2 |  |  |  |
| onepass | gfortran-expert-inputonly | ok | 21 | 0.0147 | 0.01487 | 0.0002092 |  |  |  |  | serial | 43.2 |  |  |  |
| onepass | ifx-natural-inputonly | ok | 21 | 0.01115 | 0.0113 | 0.0002266 |  |  |  |  | serial | 42.8 |  |  |  |
| onepass | ifx-expert-inputonly | ok | 21 | 0.009326 | 0.009501 | 0.0003008 |  |  |  |  | serial | 42.8 |  |  |  |
| dot | blade-llvm | ok | 21 | 0.01206 | 0.01349 | 0.001387 | ref |  |  | reordered (3 reassoc flags) | serial | 50.4 | 0.001856 [0.0008388, 0.002499] | ref |  |
| dot | blade-cpp | ok | 21 | 0.02402 | 0.02456 | 0.0008951 | 1.821 [1.761, 1.954] | suppressed |  | reordered (1 SIMD_REDUCTION/K-lane) | serial | 51.2 | 0.001465 [0.001162, 0.001855] | 0.790 [0.555, 1.780] |  |
| dot | gfortran-natural | ok | 21 | 0.01991 | 0.02105 | 0.000936 | 1.560 [1.504, 1.667] | suppressed | parity |  | serial | 50.9 | 0.002481 [0.002132, 0.002946] | 1.337 [0.935, 2.919] |  |
| dot | gfortran-expert | ok | 21 | 0.01831 | 0.0191 | 0.000941 | 1.416 [1.356, 1.511] | suppressed | parity |  | serial | 50.9 | 0.001293 [0.0007473, 0.001665] | 0.697 [0.394, 1.607] |  |
| dot | ifx-natural | ok | 21 | 0.01375 | 0.01465 | 0.000664 | 1.086 [1.047, 1.175] | suppressed | parity |  | serial | 50.4 | 0.002346 [0.001824, 0.002754] | 1.264 [0.859, 2.918] |  |
| dot | ifx-expert | ok | 21 | 0.01216 | 0.01304 | 0.001141 | 0.967 [0.908, 1.050] | suppressed | parity |  | serial | 50.4 | 0.001672 [0.0004988, 0.002268] | 0.901 [0.277, 2.058] |  |
| dot | blade-llvm-inputonly | ok | 21 | 0.01055 | 0.01163 | 0.000955 |  |  |  | reordered (2 reassoc flags) | serial | 50.4 |  |  |  |
| dot | blade-cpp-inputonly | ok | 21 | 0.02263 | 0.02309 | 0.0004895 |  |  |  | in-order | serial | 51.1 |  |  |  |
| dot | gfortran-natural-inputonly | ok | 21 | 0.01759 | 0.01856 | 0.0007889 |  |  |  |  | serial | 50.9 |  |  |  |
| dot | gfortran-expert-inputonly | ok | 21 | 0.01696 | 0.01781 | 0.0008282 |  |  |  |  | serial | 50.9 |  |  |  |
| dot | ifx-natural-inputonly | ok | 21 | 0.01131 | 0.0123 | 0.0006298 |  |  |  |  | serial | 50.4 |  |  |  |
| dot | ifx-expert-inputonly | ok | 21 | 0.01074 | 0.01137 | 0.001092 |  |  |  |  | serial | 50.4 |  |  |  |

> dot serial-licensed-native: inference suppressed -- trend: ifx-expert first-half vs second-half median moved 5.9%; trend: ifx-expert-inputonly first-half vs second-half median moved 5.0%; control memory median 0.005687s moved +7.6% vs the session baseline 0.005285s

| saxpy | blade-llvm | ok | 21 | 0.01104 | 0.01244 | 0.0007942 | ref |  |  | reordered (3 reassoc flags) | serial | 50.4 | 0.004887 [0.00444, 0.005417] | ref |  |
| saxpy | blade-cpp | ok | 21 | 0.0195 | 0.02067 | 0.0008851 | 1.662 [1.597, 1.699] | suppressed |  | reordered (1 SIMD_REDUCTION/K-lane) | serial | 50.8 | 0.005038 [0.004416, 0.005366] | 1.031 [0.888, 1.161] |  |
| saxpy | gfortran-natural | ok | 21 | 0.01651 | 0.01767 | 0.0009244 | 1.421 [1.347, 1.448] | suppressed | parity |  | serial | 50.9 | 0.005348 [0.004608, 0.005647] | 1.094 [0.920, 1.241] |  |
| saxpy | gfortran-expert | ok | 21 | 0.01474 | 0.01548 | 0.0008066 | 1.244 [1.194, 1.283] | suppressed | parity |  | serial | 50.9 | 0.003502 [0.003123, 0.003856] | 0.716 [0.619, 0.831] |  |
| saxpy | ifx-natural | ok | 21 | 0.01305 | 0.01417 | 0.0008311 | 1.139 [1.090, 1.171] | suppressed | parity |  | serial | 50.4 | 0.006293 [0.005752, 0.006594] | 1.288 [1.130, 1.435] |  |
| saxpy | ifx-expert | ok | 21 | 0.01045 | 0.01123 | 0.0009909 | 0.903 [0.858, 0.951] | suppressed | parity |  | serial | 50.4 | 0.003626 [0.003174, 0.004196] | 0.742 [0.629, 0.885] |  |
| saxpy | blade-llvm-inputonly | ok | 21 | 0.007154 | 0.007549 | 0.0005956 |  |  |  | reordered (2 reassoc flags) | serial | 35.1 |  |  |  |
| saxpy | blade-cpp-inputonly | ok | 21 | 0.0153 | 0.01563 | 0.0005182 |  |  |  | in-order | serial | 35.9 |  |  |  |
| saxpy | gfortran-natural-inputonly | ok | 21 | 0.01176 | 0.01232 | 0.0006223 |  |  |  |  | serial | 35.6 |  |  |  |
| saxpy | gfortran-expert-inputonly | ok | 21 | 0.01141 | 0.01197 | 0.0003672 |  |  |  |  | serial | 35.6 |  |  |  |
| saxpy | ifx-natural-inputonly | ok | 21 | 0.007572 | 0.007877 | 0.0006941 |  |  |  |  | serial | 35.2 |  |  |  |
| saxpy | ifx-expert-inputonly | ok | 21 | 0.007249 | 0.007606 | 0.000618 |  |  |  |  | serial | 35.2 |  |  |  |

> saxpy serial-licensed-native: inference suppressed -- trend: blade-llvm first-half vs second-half median moved 5.1%


## Controls

- compute (recur/gfortran-expert/serial-repro-native/full): n=105 median 0.01286 s, baseline 0.01282 s
- memory (saxpy/gfortran-expert/serial-repro-native/control): n=105 median 0.005336 s, baseline 0.005285 s
