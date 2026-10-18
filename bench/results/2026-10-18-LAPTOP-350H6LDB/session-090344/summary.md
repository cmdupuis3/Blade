# Blade vs Fortran -- bench session 2026-10-18T09:03:44

- host LAPTOP-350H6LDB | AMD Ryzen 7 5800HS with Radeon Graphics | 8C/16T | 15.41 GB | power ac | max processor state AC 100% | boost AC None | Power Scheme GUID: 64a64f24-65b9-4b56-befd-5ec1eaced9b3  (Silent)
- git 927776980d0669edd6b9cc822833ce119dcaabd4 | Blade.dll 74b6728036b170d9 | GNU Fortran (Rev4, Built by MSYS2 project) 16.2.0 | Intel(R) Fortran Compiler for applications running on Intel(R) 64, Version 2026.1.0 Build 20260617 | clang version 22.1.8 (https://github.com/msys2/MINGW-packages d34feaaa1d0aff624c870910b6f2589aa40d20de)
- size `full` | duration 3281.9 s | pins: pins-A.json, pins-B.json
- PRIMARY estimand: the complete timed region. ratio = median(arm) / median(Blade reference), round-block bootstrap 95% CI (10000 resamples); ratio > 1 = Blade faster. Verdicts (delta 0.1): win / detectable-win / equivalent / detectable-loss / loss / inconclusive; `suppressed` = the block failed a drift, trend or control check. Intervals are pointwise; no suite-wide claim. incremental = median(arm) - median(its own input-only twin), NOT kernel time. Reference: blade-llvm (serial), blade-cpp (mt8, or when the LLVM lane refused). Fresh-process, allocation-inclusive measurements.
- **FLAG**: fusedchain/serial-licensed-native: inference suppressed: trend: ifx-natural first-half vs second-half median moved 7.6%
- **FLAG**: maskagg/serial-licensed-native: inference suppressed: trend: ifx-expert first-half vs second-half median moved 10.4%
- **FLAG**: dot/serial-licensed-native: inference suppressed: trend: blade-llvm first-half vs second-half median moved 11.1%; trend: blade-cpp first-half vs second-half median moved 7.1%; trend: ifx-expert first-half vs second-half median moved 8.2%; trend: blade-llvm-inputonly first-half vs second-half median moved 5.6%; trend: blade-cpp-inputonly first-half vs second-half median moved 8.4%; trend: ifx-natural-inputonly first-half vs second-half median moved 7.1%; trend: ifx-expert-inputonly first-half vs second-half median moved 6.3%; trend: control compute moved 8.3% within the block
- **FLAG**: saxpy/serial-licensed-native: inference suppressed: trend: blade-llvm first-half vs second-half median moved 11.1%; trend: blade-cpp first-half vs second-half median moved 8.9%; trend: ifx-expert first-half vs second-half median moved 14.8%; trend: blade-llvm-inputonly first-half vs second-half median moved 12.4%; trend: gfortran-natural-inputonly first-half vs second-half median moved 12.0%; trend: gfortran-expert-inputonly first-half vs second-half median moved 8.2%; trend: ifx-expert-inputonly first-half vs second-half median moved 8.5%; trend: control memory moved 5.2% within the block

## serial-licensed-native

Blade reference per kernel: fusedchain=blade-llvm, maskagg=blade-cpp, onepass=blade-llvm, dot=blade-llvm, saxpy=blade-llvm

| kernel | arm | status | n | min (s) | median (s) | IQR (s) | ratio vs ref [95% CI] | verdict | hypothesis | fold form | parallelism | peak WS (MB) | incremental (s) [CI] | incremental ratio [CI] | notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| fusedchain | blade-llvm | ok | 21 | 0.004228 | 0.004571 | 0.0004095 | ref |  |  | reordered (2 reassoc flags) | serial | 19.6 | 0.0006254 [0.000458, 0.0009815] | ref |  |
| fusedchain | blade-cpp | ok | 21 | 0.006388 | 0.006754 | 0.0006211 | 1.478 [1.400, 1.551] | suppressed |  | reordered (1 SIMD_REDUCTION) | serial | 20.4 | 0.0006697 [0.0003317, 0.0009588] | 1.071 [0.459, 1.778] |  |
| fusedchain | gfortran-natural | ok | 21 | 0.01681 | 0.01798 | 0.001562 | 3.934 [3.749, 4.214] | suppressed | WIN 1.5-2.5x (no temporaries) |  | serial | 65.9 | 0.01121 [0.01069, 0.01216] | 17.929 [11.336, 25.225] |  |
| fusedchain | gfortran-expert | ok | 21 | 0.006552 | 0.006905 | 0.000403 | 1.511 [1.433, 1.563] | suppressed | parity |  | serial | 20.1 | 0.0007251 [0.0005915, 0.0009701] | 1.159 [0.689, 1.718] |  |
| fusedchain | ifx-natural | ok | 21 | 0.01271 | 0.01345 | 0.001078 | 2.943 [2.808, 3.122] | suppressed | WIN 1.5-2.5x (no temporaries) |  | serial | 65.5 | 0.008912 [0.008556, 0.009709] | 14.250 [8.977, 19.471] |  |
| fusedchain | ifx-expert | ok | 21 | 0.004363 | 0.004897 | 0.0005451 | 1.071 [0.986, 1.124] | suppressed | parity |  | serial | 19.7 | 0.001107 [0.0007348, 0.001348] | 1.769 [0.954, 2.459] |  |
| fusedchain | blade-llvm-inputonly | ok | 21 | 0.003566 | 0.003946 | 0.0003916 |  |  |  | reordered (1 reassoc flags) | serial | 19.6 |  |  |  |
| fusedchain | blade-cpp-inputonly | ok | 21 | 0.00571 | 0.006084 | 0.0004335 |  |  |  | in-order | serial | 20.4 |  |  |  |
| fusedchain | gfortran-natural-inputonly | ok | 21 | 0.006526 | 0.006769 | 0.0001908 |  |  |  |  | serial | 20.1 |  |  |  |
| fusedchain | gfortran-expert-inputonly | ok | 21 | 0.005815 | 0.00618 | 0.0002745 |  |  |  |  | serial | 20.1 |  |  |  |
| fusedchain | ifx-natural-inputonly | ok | 21 | 0.004342 | 0.00454 | 0.0003556 |  |  |  |  | serial | 19.7 |  |  |  |
| fusedchain | ifx-expert-inputonly | ok | 21 | 0.003582 | 0.00379 | 0.0003594 |  |  |  |  | serial | 19.7 |  |  |  |

> fusedchain serial-licensed-native: inference suppressed -- trend: ifx-natural first-half vs second-half median moved 7.6%

| maskagg | blade-llvm | fallback | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | a compound-index initializer; expected fallback (the LLVM lane refuses `compound` (notes/llvm-acceptance-B.md); blade-cpp is the Blade figure) |
| maskagg | blade-cpp | ok | 21 | 0.0635 | 0.06716 | 0.007288 | ref |  |  | in-order | serial | 46.3 | 0.06434 [0.0629, 0.06801] | ref |  |
| maskagg | gfortran-natural | ok | 21 | 0.008169 | 0.008301 | 0.0005376 | 0.124 [0.117, 0.128] | suppressed | LOSS 1.3-2x |  | serial | 17.3 | 0.00496 [0.004874, 0.005124] | 0.077 [0.073, 0.081] |  |
| maskagg | gfortran-expert | ok | 21 | 0.003902 | 0.003993 | 0.000371 | 0.059 [0.056, 0.064] | suppressed | LOSS 1.3-2x |  | serial | 16.4 | 0.001088 [0.001005, 0.001372] | 0.017 [0.016, 0.021] |  |
| maskagg | ifx-natural | ok | 21 | 0.01021 | 0.01056 | 0.0003739 | 0.157 [0.148, 0.161] | suppressed | LOSS 1.3-2x |  | serial | 23.5 | 0.008379 [0.008222, 0.008452] | 0.130 [0.122, 0.134] |  |
| maskagg | ifx-expert | ok | 21 | 0.002788 | 0.002919 | 0.0004587 | 0.043 [0.041, 0.048] | suppressed | LOSS 1.3-2x |  | serial | 15.9 | 0.001193 [0.001159, 0.001427] | 0.019 [0.017, 0.023] |  |
| maskagg | blade-llvm-inputonly | ok | 21 | 0.00165 | 0.001717 | 0.0001221 |  |  |  | reordered (1 reassoc flags) | serial | 12.0 |  |  |  |
| maskagg | blade-cpp-inputonly | ok | 21 | 0.002755 | 0.002822 | 0.0001425 |  |  |  | in-order | serial | 12.8 |  |  |  |
| maskagg | gfortran-natural-inputonly | ok | 21 | 0.003285 | 0.003341 | 0.0001399 |  |  |  |  | serial | 12.5 |  |  |  |
| maskagg | gfortran-expert-inputonly | ok | 21 | 0.002838 | 0.002906 | 0.000102 |  |  |  |  | serial | 12.5 |  |  |  |
| maskagg | ifx-natural-inputonly | ok | 21 | 0.002158 | 0.002181 | 5.36e-05 |  |  |  |  | serial | 12.0 |  |  |  |
| maskagg | ifx-expert-inputonly | ok | 21 | 0.001674 | 0.001726 | 4.21e-05 |  |  |  |  | serial | 12.0 |  |  |  |

> maskagg serial-licensed-native: inference suppressed -- trend: ifx-expert first-half vs second-half median moved 10.4%

| onepass | blade-llvm | ok | 21 | 0.01105 | 0.01167 | 0.0005775 | ref |  |  | reordered (5 reassoc flags) | serial | 42.5 | 0.002178 [0.001623, 0.002373] | ref |  |
| onepass | blade-cpp | ok | 21 | 0.01634 | 0.01677 | 0.0005779 | 1.437 [1.396, 1.480] | win |  | reordered (1 SIMD_REDUCTION) | serial | 43.3 | 0.001941 [0.00155, 0.002235] | 0.891 [0.691, 1.173] |  |
| onepass | gfortran-natural | ok | 21 | 0.03233 | 0.03287 | 0.0005672 | 2.816 [2.781, 2.893] | win | WIN 2-3x (one pass vs four) |  | serial | 43.0 | 0.01573 [0.01544, 0.01614] | 7.221 [6.670, 9.524] |  |
| onepass | gfortran-expert | ok | 21 | 0.01654 | 0.01683 | 0.0005547 | 1.442 [1.419, 1.497] | win | parity |  | serial | 43.0 | 0.001593 [0.001384, 0.002116] | 0.732 [0.630, 1.154] |  |
| onepass | ifx-natural | ok | 21 | 0.01245 | 0.01296 | 0.0003827 | 1.110 [1.095, 1.146] | win | WIN 2-3x (one pass vs four) |  | serial | 42.6 | 0.0009776 [0.000406, 0.001523] | 0.449 [0.188, 0.795] |  |
| onepass | ifx-expert | ok | 21 | 0.01107 | 0.01178 | 0.0009472 | 1.009 [0.974, 1.051] | equivalent | parity |  | serial | 42.6 | 0.001928 [0.001538, 0.002255] | 0.885 [0.663, 1.308] |  |
| onepass | blade-llvm-inputonly | ok | 21 | 0.009297 | 0.009495 | 0.0005903 |  |  |  | reordered (1 reassoc flags) | serial | 42.5 |  |  |  |
| onepass | blade-cpp-inputonly | ok | 21 | 0.01454 | 0.01483 | 0.0004223 |  |  |  | in-order | serial | 43.3 |  |  |  |
| onepass | gfortran-natural-inputonly | ok | 21 | 0.01661 | 0.01715 | 0.000455 |  |  |  |  | serial | 43.0 |  |  |  |
| onepass | gfortran-expert-inputonly | ok | 21 | 0.0148 | 0.01524 | 0.0005366 |  |  |  |  | serial | 43.0 |  |  |  |
| onepass | ifx-natural-inputonly | ok | 21 | 0.01105 | 0.01198 | 0.00121 |  |  |  |  | serial | 42.6 |  |  |  |
| onepass | ifx-expert-inputonly | ok | 21 | 0.009378 | 0.009849 | 0.0005566 |  |  |  |  | serial | 42.6 |  |  |  |
| dot | blade-llvm | ok | 21 | 0.0126 | 0.0132 | 0.001436 | ref |  |  | reordered (3 reassoc flags) | serial | 50.2 | 0.001731 [0.001257, 0.002332] | ref |  |
| dot | blade-cpp | ok | 21 | 0.01865 | 0.01928 | 0.001349 | 1.460 [1.369, 1.518] | suppressed |  | reordered (1 SIMD_REDUCTION) | serial | 50.9 | 0.001571 [0.0005624, 0.001952] | 0.907 [0.306, 1.310] |  |
| dot | gfortran-natural | ok | 21 | 0.01976 | 0.02028 | 0.001278 | 1.536 [1.441, 1.594] | suppressed | parity |  | serial | 50.6 | 0.002029 [0.001687, 0.002659] | 1.172 [0.846, 1.723] |  |
| dot | gfortran-expert | ok | 21 | 0.01817 | 0.01844 | 0.0008363 | 1.397 [1.329, 1.449] | suppressed | parity |  | serial | 50.6 | 0.001125 [0.0008258, 0.001732] | 0.650 [0.381, 1.217] |  |
| dot | ifx-natural | ok | 21 | 0.01358 | 0.01404 | 0.0006264 | 1.063 [1.011, 1.101] | suppressed | parity |  | serial | 50.2 | 0.002262 [0.001672, 0.002596] | 1.306 [0.741, 1.936] |  |
| dot | ifx-expert | ok | 21 | 0.01199 | 0.01267 | 0.001054 | 0.960 [0.905, 1.012] | suppressed | parity |  | serial | 50.2 | 0.001569 [0.001203, 0.002163] | 0.906 [0.593, 1.453] |  |
| dot | blade-llvm-inputonly | ok | 21 | 0.01096 | 0.01147 | 0.0008042 |  |  |  | reordered (2 reassoc flags) | serial | 50.1 |  |  |  |
| dot | blade-cpp-inputonly | ok | 21 | 0.01743 | 0.01771 | 0.001502 |  |  |  | in-order | serial | 50.9 |  |  |  |
| dot | gfortran-natural-inputonly | ok | 21 | 0.01734 | 0.01825 | 0.001063 |  |  |  |  | serial | 50.6 |  |  |  |
| dot | gfortran-expert-inputonly | ok | 21 | 0.0169 | 0.01732 | 0.0009167 |  |  |  |  | serial | 50.7 |  |  |  |
| dot | ifx-natural-inputonly | ok | 21 | 0.01126 | 0.01178 | 0.0009702 |  |  |  |  | serial | 50.2 |  |  |  |
| dot | ifx-expert-inputonly | ok | 21 | 0.01054 | 0.0111 | 0.0007975 |  |  |  |  | serial | 50.2 |  |  |  |

> dot serial-licensed-native: inference suppressed -- trend: blade-llvm first-half vs second-half median moved 11.1%; trend: blade-cpp first-half vs second-half median moved 7.1%; trend: ifx-expert first-half vs second-half median moved 8.2%; trend: blade-llvm-inputonly first-half vs second-half median moved 5.6%; trend: blade-cpp-inputonly first-half vs second-half median moved 8.4%; trend: ifx-natural-inputonly first-half vs second-half median moved 7.1%; trend: ifx-expert-inputonly first-half vs second-half median moved 6.3%; trend: control compute moved 8.3% within the block

| saxpy | blade-llvm | ok | 21 | 0.01196 | 0.01428 | 0.00317 | ref |  |  | reordered (3 reassoc flags) | serial | 49.6 | 0.006083 [0.004876, 0.007832] | ref |  |
| saxpy | blade-cpp | ok | 21 | 0.0167 | 0.01789 | 0.003701 | 1.253 [1.116, 1.435] | suppressed |  | reordered (1 SIMD_REDUCTION) | serial | 50.6 | 0.005777 [0.004975, 0.0078] | 0.950 [0.730, 1.407] |  |
| saxpy | gfortran-natural | ok | 21 | 0.01651 | 0.01747 | 0.001643 | 1.224 [1.090, 1.349] | suppressed | parity |  | serial | 50.7 | 0.005109 [0.004021, 0.006192] | 0.840 [0.597, 1.100] |  |
| saxpy | gfortran-expert | ok | 21 | 0.01456 | 0.01562 | 0.00265 | 1.094 [0.975, 1.254] | suppressed | parity |  | serial | 50.7 | 0.003622 [0.00277, 0.005728] | 0.595 [0.413, 0.957] |  |
| saxpy | ifx-natural | ok | 21 | 0.01295 | 0.01413 | 0.002521 | 0.989 [0.881, 1.110] | suppressed | parity |  | serial | 50.2 | 0.005848 [0.005103, 0.007107] | 0.962 [0.743, 1.273] |  |
| saxpy | ifx-expert | ok | 21 | 0.01051 | 0.01193 | 0.00379 | 0.836 [0.742, 0.970] | suppressed | parity |  | serial | 50.2 | 0.004293 [0.002807, 0.005824] | 0.706 [0.471, 1.045] |  |
| saxpy | blade-llvm-inputonly | ok | 21 | 0.007398 | 0.008194 | 0.001264 |  |  |  | reordered (2 reassoc flags) | serial | 34.9 |  |  |  |
| saxpy | blade-cpp-inputonly | ok | 21 | 0.01173 | 0.01211 | 0.0006331 |  |  |  | in-order | serial | 35.7 |  |  |  |
| saxpy | gfortran-natural-inputonly | ok | 21 | 0.0116 | 0.01236 | 0.001411 |  |  |  |  | serial | 35.4 |  |  |  |
| saxpy | gfortran-expert-inputonly | ok | 21 | 0.01131 | 0.012 | 0.001498 |  |  |  |  | serial | 35.4 |  |  |  |
| saxpy | ifx-natural-inputonly | ok | 21 | 0.00754 | 0.008277 | 0.001141 |  |  |  |  | serial | 34.9 |  |  |  |
| saxpy | ifx-expert-inputonly | ok | 21 | 0.007202 | 0.007636 | 0.001454 |  |  |  |  | serial | 34.9 |  |  |  |

> saxpy serial-licensed-native: inference suppressed -- trend: blade-llvm first-half vs second-half median moved 11.1%; trend: blade-cpp first-half vs second-half median moved 8.9%; trend: ifx-expert first-half vs second-half median moved 14.8%; trend: blade-llvm-inputonly first-half vs second-half median moved 12.4%; trend: gfortran-natural-inputonly first-half vs second-half median moved 12.0%; trend: gfortran-expert-inputonly first-half vs second-half median moved 8.2%; trend: ifx-expert-inputonly first-half vs second-half median moved 8.5%; trend: control memory moved 5.2% within the block


## Controls

- compute (recur/gfortran-expert/serial-repro-native/full): n=105 median 0.01305 s, baseline 0.01312 s
- memory (saxpy/gfortran-expert/serial-repro-native/control): n=105 median 0.005404 s, baseline 0.005398 s
