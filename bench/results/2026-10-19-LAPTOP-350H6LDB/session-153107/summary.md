# Blade vs Fortran -- bench session 2026-10-19T15:31:07

- host LAPTOP-350H6LDB | AMD Ryzen 7 5800HS with Radeon Graphics | 8C/16T | 15.41 GB | power ac | max processor state AC 100% | boost AC None | Power Scheme GUID: 64a64f24-65b9-4b56-befd-5ec1eaced9b3  (Silent)
- git 39a8a869ff410c79edbe599a98257d4012241702 | Blade.dll bb52a4c2a008d198 | GNU Fortran (Rev4, Built by MSYS2 project) 16.2.0 | Intel(R) Fortran Compiler for applications running on Intel(R) 64, Version 2026.1.0 Build 20260617 | clang version 22.1.8 (https://github.com/msys2/MINGW-packages d34feaaa1d0aff624c870910b6f2589aa40d20de)
- size `full` | duration 1133.2 s | pins: pins-A.json, pins-B.json
- PRIMARY estimand: the complete timed region. ratio = median(arm) / median(Blade reference), round-block bootstrap 95% CI (10000 resamples); ratio > 1 = Blade faster. Verdicts (delta 0.1): win / detectable-win / equivalent / detectable-loss / loss / inconclusive; `suppressed` = the block failed a drift, trend or control check. Intervals are pointwise; no suite-wide claim. incremental = median(arm) - median(its own input-only twin), NOT kernel time. Reference: blade-llvm (serial), blade-cpp (mt8, or when the LLVM lane refused). Fresh-process, allocation-inclusive measurements.
- **FLAG**: comoment3/serial-fastmath-native: inference suppressed: trend: ifx-natural first-half vs second-half median moved 5.6%
- **FLAG**: gram/serial-fastmath-native: inference suppressed: trend: gfortran-natural first-half vs second-half median moved 19.7%; trend: ifx-natural first-half vs second-half median moved 7.2%
- **FLAG**: fusedchain/serial-fastmath-native: inference suppressed: trend: blade-llvm first-half vs second-half median moved 7.8%; trend: blade-llvm-inputonly first-half vs second-half median moved 12.3%; trend: ifx-natural-inputonly first-half vs second-half median moved 24.8%; trend: ifx-expert-inputonly first-half vs second-half median moved 8.0%
- **FLAG**: onepass/serial-fastmath-native: inference suppressed: trend: ifx-expert first-half vs second-half median moved 5.6%; trend: blade-llvm-inputonly first-half vs second-half median moved 6.3%; trend: control memory moved 7.2% within the block

## serial-fastmath-native

Blade reference per kernel: antisym=blade-llvm, band=blade-llvm, comoment3=blade-llvm, gram=blade-llvm, fusedchain=blade-llvm, onepass=blade-llvm, dot=blade-llvm

| kernel | arm | status | n | min (s) | median (s) | IQR (s) | ratio vs ref [95% CI] | verdict | hypothesis | fold form | parallelism | peak WS (MB) | incremental (s) [CI] | incremental ratio [CI] | notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| antisym | blade-llvm | ok | 21 | 0.02763 | 0.0282 | 0.0006982 | ref |  |  | reordered (4 reassoc flags) | serial | 24.9 |  |  |  |
| antisym | blade-cpp | ok | 21 | 0.02952 | 0.0301 | 0.0006135 | 1.067 [1.049, 1.083] | detectable-win |  | reordered (5 SIMD_REDUCTION/K-lane) | serial | 25.7 |  |  |  |
| antisym | gfortran-natural | ok | 21 | 0.1314 | 0.1337 | 0.003657 | 4.742 [4.664, 4.781] | win | WIN ~2x (half the cells, no diagonal) |  | serial | 23.5 |  |  |  |
| antisym | gfortran-expert | ok | 21 | 0.03082 | 0.03173 | 0.000499 | 1.125 [1.106, 1.134] | win | parity +-15% |  | serial | 32.7 |  |  |  |
| antisym | ifx-natural | ok | 21 | 0.08338 | 0.08655 | 0.002486 | 3.069 [3.014, 3.113] | win | WIN ~2x (half the cells, no diagonal) |  | serial | 23.0 |  |  |  |
| antisym | ifx-expert | ok | 21 | 0.02953 | 0.03119 | 0.001105 | 1.106 [1.074, 1.112] | win | parity +-15% |  | serial | 32.3 |  |  |  |
| band | blade-llvm | ok | 21 | 0.0004088 | 0.0004278 | 3.93e-05 | ref |  |  | reordered (2 reassoc flags) | serial | 4.8 |  |  |  |
| band | blade-cpp | ok | 21 | 0.0006152 | 0.0006455 | 1.88e-05 | 1.509 [1.432, 1.562] | win |  | reordered (1 SIMD_REDUCTION/K-lane) | serial | 5.5 |  |  |  |
| band | gfortran-natural | ok | 21 | 0.3013 | 0.303 | 0.003917 | 708.343 [682.188, 734.345] | win | win 10x+ (the natural tier is the n^2 predicate nest, a straw man by construction) |  | serial | 5.4 |  |  |  |
| band | gfortran-expert | ok | 21 | 0.001552 | 0.001695 | 0.000158 | 3.961 [3.638, 4.069] | win | parity in both lanes: licensed folds walk the band by diagonal (k = i - j outer) as the licensed expert does, with phi hoisted per diagonal by the back end (G33); repro keeps the lex order with the terms vectorized and summed in order (G30) |  | serial | 5.4 |  |  |  |
| band | ifx-natural | ok | 21 | 0.3279 | 0.3299 | 0.005044 | 771.103 [743.407, 798.156] | win | win 10x+ (the natural tier is the n^2 predicate nest, a straw man by construction) |  | serial | 5.1 |  |  |  |
| band | ifx-expert | ok | 21 | 0.001318 | 0.001325 | 5.2e-06 | 3.098 [2.988, 3.210] | win | parity in both lanes: licensed folds walk the band by diagonal (k = i - j outer) as the licensed expert does, with phi hoisted per diagonal by the back end (G33); repro keeps the lex order with the terms vectorized and summed in order (G30) |  | serial | 5.0 |  |  |  |
| comoment3 | blade-llvm | ok | 21 | 0.008608 | 0.008768 | 0.0001759 | ref |  |  | reordered (6 reassoc flags) | serial | 11.6 |  |  |  |
| comoment3 | blade-cpp | ok | 21 | 0.008775 | 0.009036 | 0.0003208 | 1.031 [1.013, 1.051] | suppressed |  | reordered (6 SIMD_REDUCTION/K-lane) | serial | 12.7 |  |  |  |
| comoment3 | gfortran-natural | ok | 21 | 0.1442 | 0.1452 | 0.001065 | 16.561 [16.307, 16.683] | suppressed | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | serial | 43.5 |  |  |  |
| comoment3 | gfortran-expert | ok | 21 | 0.01134 | 0.01174 | 0.000288 | 1.339 [1.312, 1.347] | suppressed | parity to 1.3x loss |  | serial | 12.4 |  |  |  |
| comoment3 | ifx-natural | ok | 21 | 0.07968 | 0.08712 | 0.00856 | 9.936 [9.163, 10.098] | suppressed | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | serial | 43.1 |  |  |  |
| comoment3 | ifx-expert | ok | 21 | 0.01129 | 0.0117 | 0.0002335 | 1.334 [1.312, 1.350] | suppressed | parity to 1.3x loss |  | serial | 11.9 |  |  |  |

> comoment3 serial-fastmath-native: inference suppressed -- trend: ifx-natural first-half vs second-half median moved 5.6%

| gram | blade-llvm | ok | 11 | 0.05735 | 0.05885 | 0.002329 | ref |  |  | reordered (4 reassoc flags) | serial | 30.8 |  |  |  |
| gram | blade-cpp | ok | 11 | 0.0613 | 0.06268 | 0.002081 | 1.065 [1.030, 1.088] | suppressed |  | reordered (4 SIMD_REDUCTION/K-lane) | serial | 31.5 |  |  |  |
| gram | gfortran-natural | ok | 11 | 0.5653 | 0.7063 | 0.1461 | 12.001 [9.649, 12.434] | suppressed | win 1.5-2x if matmul is a naive nest; parity/loss if a blocked runtime kernel |  | serial | 37.0 |  |  |  |
| gram | gfortran-expert | ok | 11 | 0.07733 | 0.07846 | 0.00193 | 1.333 [1.289, 1.355] | suppressed | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | serial | 51.7 |  |  |  |
| gram | ifx-natural | ok | 11 | 0.2019 | 0.2034 | 0.01914 | 3.456 [3.382, 3.743] | suppressed | win 1.5-2x if matmul is a naive nest; parity/loss if a blocked runtime kernel |  | serial | 56.0 |  |  |  |
| gram | ifx-expert | ok | 11 | 0.07063 | 0.07246 | 0.001507 | 1.231 [1.192, 1.251] | suppressed | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | serial | 51.3 |  |  |  |

> gram serial-fastmath-native: inference suppressed -- trend: gfortran-natural first-half vs second-half median moved 19.7%; trend: ifx-natural first-half vs second-half median moved 7.2%

| fusedchain | blade-llvm | ok | 21 | 0.004085 | 0.004895 | 0.001037 | ref |  |  | reordered (2 reassoc flags) | serial | 19.9 | 0.0009048 [0.0002909, 0.00158] | ref |  |
| fusedchain | blade-cpp | ok | 21 | 0.008319 | 0.008655 | 0.0007074 | 1.768 [1.639, 1.993] | suppressed |  | reordered (1 SIMD_REDUCTION/K-lane) | serial | 20.6 | 0.0007969 [0.0003963, 0.001403] | 0.881 [0.365, 3.047] |  |
| fusedchain | gfortran-natural | ok | 21 | 0.01558 | 0.01675 | 0.0006342 | 3.421 [3.137, 3.870] | suppressed | WIN 1.5-2.5x (no temporaries) |  | serial | 66.1 | 0.01057 [0.01017, 0.01081] | 11.680 [6.638, 36.400] |  |
| fusedchain | gfortran-expert | ok | 21 | 0.006589 | 0.007324 | 0.0004801 | 1.496 [1.374, 1.688] | suppressed | parity |  | serial | 20.4 | 0.001037 [0.0008225, 0.001377] | 1.146 [0.649, 3.662] |  |
| fusedchain | ifx-natural | ok | 21 | 0.009946 | 0.01103 | 0.001033 | 2.254 [2.028, 2.578] | suppressed | WIN 1.5-2.5x (no temporaries) |  | serial | 65.7 | 0.007969 [0.007359, 0.008458] | 8.808 [4.995, 28.047] |  |
| fusedchain | ifx-expert | ok | 21 | 0.003615 | 0.003857 | 0.0002285 | 0.788 [0.705, 0.872] | suppressed | parity |  | serial | 19.9 | 0.0009075 [0.0006159, 0.001043] | 1.003 [0.503, 2.991] |  |
| fusedchain | blade-llvm-inputonly | ok | 21 | 0.003346 | 0.00399 | 0.001009 |  |  |  | reordered (1 reassoc flags) | serial | 19.9 |  |  |  |
| fusedchain | blade-cpp-inputonly | ok | 21 | 0.007528 | 0.007858 | 0.0005205 |  |  |  | in-order | serial | 20.6 |  |  |  |
| fusedchain | gfortran-natural-inputonly | ok | 21 | 0.005799 | 0.006178 | 0.0004481 |  |  |  |  | serial | 20.4 |  |  |  |
| fusedchain | gfortran-expert-inputonly | ok | 21 | 0.005892 | 0.006287 | 0.0005147 |  |  |  |  | serial | 20.4 |  |  |  |
| fusedchain | ifx-natural-inputonly | ok | 21 | 0.002815 | 0.003062 | 0.0006238 |  |  |  |  | serial | 19.9 |  |  |  |
| fusedchain | ifx-expert-inputonly | ok | 21 | 0.002787 | 0.002949 | 0.0003371 |  |  |  |  | serial | 19.9 |  |  |  |

> fusedchain serial-fastmath-native: inference suppressed -- trend: blade-llvm first-half vs second-half median moved 7.8%; trend: blade-llvm-inputonly first-half vs second-half median moved 12.3%; trend: ifx-natural-inputonly first-half vs second-half median moved 24.8%; trend: ifx-expert-inputonly first-half vs second-half median moved 8.0%

| onepass | blade-llvm | ok | 21 | 0.01059 | 0.01207 | 0.0006845 | ref |  |  | reordered (5 reassoc flags) | serial | 42.8 | 0.001976 [0.001516, 0.002829] | ref |  |
| onepass | blade-cpp | ok | 21 | 0.02107 | 0.02212 | 0.001169 | 1.833 [1.735, 1.871] | suppressed |  | reordered (1 SIMD_REDUCTION/K-lane) | serial | 43.5 | 0.002335 [0.001619, 0.002812] | 1.181 [0.735, 1.704] |  |
| onepass | gfortran-natural | ok | 21 | 0.02088 | 0.02213 | 0.0008117 | 1.833 [1.758, 1.873] | suppressed | WIN 2-3x (one pass vs four) |  | serial | 43.2 | 0.006478 [0.006189, 0.007006] | 3.278 [2.378, 4.302] |  |
| onepass | gfortran-expert | ok | 21 | 0.01736 | 0.01812 | 0.0006166 | 1.502 [1.443, 1.524] | suppressed | parity |  | serial | 43.2 | 0.002385 [0.002035, 0.002696] | 1.207 [0.843, 1.654] |  |
| onepass | ifx-natural | ok | 21 | 0.007978 | 0.009125 | 0.000731 | 0.756 [0.723, 0.779] | suppressed | WIN 2-3x (one pass vs four) |  | serial | 42.8 | 0.001014 [0.0004264, 0.001595] | 0.513 [0.203, 0.888] |  |
| onepass | ifx-expert | ok | 21 | 0.008142 | 0.008728 | 0.0008109 | 0.723 [0.689, 0.752] | suppressed | parity |  | serial | 42.8 | 0.0006865 [7.57e-05, 0.00113] | 0.347 [0.038, 0.623] |  |
| onepass | blade-llvm-inputonly | ok | 21 | 0.00923 | 0.01009 | 0.0009234 |  |  |  | reordered (1 reassoc flags) | serial | 42.8 |  |  |  |
| onepass | blade-cpp-inputonly | ok | 21 | 0.01942 | 0.01979 | 0.0006229 |  |  |  | in-order | serial | 43.5 |  |  |  |
| onepass | gfortran-natural-inputonly | ok | 21 | 0.01492 | 0.01565 | 0.0004718 |  |  |  |  | serial | 43.2 |  |  |  |
| onepass | gfortran-expert-inputonly | ok | 21 | 0.01518 | 0.01574 | 0.0005241 |  |  |  |  | serial | 43.2 |  |  |  |
| onepass | ifx-natural-inputonly | ok | 21 | 0.007358 | 0.008111 | 0.000949 |  |  |  |  | serial | 42.8 |  |  |  |
| onepass | ifx-expert-inputonly | ok | 20 | 0.007211 | 0.008041 | 0.0009081 |  |  |  |  | serial | 42.8 |  |  |  |

> onepass serial-fastmath-native: inference suppressed -- trend: ifx-expert first-half vs second-half median moved 5.6%; trend: blade-llvm-inputonly first-half vs second-half median moved 6.3%; trend: control memory moved 7.2% within the block


> onepass serial-fastmath-native: isolated excursion DROPPED from inference (sample kept, flagged excursion-dropped): onepass/serial-fastmath-native/ifx-expert-inputonly round 16 0.03106 s = 3.83x running median

| dot | blade-llvm | ok | 21 | 0.01262 | 0.0139 | 0.0009133 | ref |  |  | reordered (3 reassoc flags) | serial | 50.4 | 0.001804 [0.001186, 0.002076] | ref |  |
| dot | blade-cpp | ok | 21 | 0.0243 | 0.02496 | 0.0009198 | 1.797 [1.772, 1.851] | win |  | reordered (1 SIMD_REDUCTION/K-lane) | serial | 51.2 | 0.00158 [0.00118, 0.001996] | 0.876 [0.709, 1.317] |  |
| dot | gfortran-natural | ok | 21 | 0.01854 | 0.01923 | 0.0008964 | 1.384 [1.351, 1.442] | win | parity |  | serial | 50.9 | 0.001458 [0.000967, 0.001956] | 0.808 [0.523, 1.308] |  |
| dot | gfortran-expert | ok | 21 | 0.01856 | 0.01902 | 0.00112 | 1.369 [1.348, 1.450] | win | parity |  | serial | 50.9 | 0.001538 [0.00134, 0.002385] | 0.853 [0.700, 1.552] |  |
| dot | ifx-natural | ok | 21 | 0.01247 | 0.01354 | 0.0008115 | 0.975 [0.952, 1.014] | equivalent | parity |  | serial | 50.4 | 0.003924 [0.003557, 0.004268] | 2.175 [1.832, 3.241] |  |
| dot | ifx-expert | ok | 21 | 0.008675 | 0.009888 | 0.00118 | 0.712 [0.671, 0.737] | loss | parity |  | serial | 50.4 | 0.0007087 [-8.77e-05, 0.0009609] | 0.393 [-0.045, 0.732] |  |
| dot | blade-llvm-inputonly | ok | 21 | 0.01131 | 0.01209 | 0.000927 |  |  |  | reordered (2 reassoc flags) | serial | 50.4 |  |  |  |
| dot | blade-cpp-inputonly | ok | 21 | 0.02303 | 0.02338 | 0.0006941 |  |  |  | in-order | serial | 51.2 |  |  |  |
| dot | gfortran-natural-inputonly | ok | 21 | 0.01712 | 0.01777 | 0.0006114 |  |  |  |  | serial | 50.9 |  |  |  |
| dot | gfortran-expert-inputonly | ok | 21 | 0.01699 | 0.01748 | 0.0006561 |  |  |  |  | serial | 50.9 |  |  |  |
| dot | ifx-natural-inputonly | ok | 21 | 0.008655 | 0.009618 | 0.0007388 |  |  |  |  | serial | 50.4 |  |  |  |
| dot | ifx-expert-inputonly | ok | 21 | 0.00866 | 0.00918 | 0.0007432 |  |  |  |  | serial | 50.4 |  |  |  |

## Controls

- compute (recur/gfortran-expert/serial-repro-native/full): n=137 median 0.01364 s, baseline 0.01371 s
- memory (saxpy/gfortran-expert/serial-repro-native/control): n=137 median 0.005544 s, baseline 0.005577 s

## Warnings

- excursion dropped (isolated; recorded): onepass/serial-fastmath-native/ifx-expert-inputonly round 16: 0.031s = 3.8x running median
