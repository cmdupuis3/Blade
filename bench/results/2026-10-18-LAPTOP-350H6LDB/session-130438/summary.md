# Blade vs Fortran -- bench session 2026-10-18T13:04:38

- host LAPTOP-350H6LDB | AMD Ryzen 7 5800HS with Radeon Graphics | 8C/16T | 15.41 GB | power ac | max processor state AC 100% | boost AC None | Power Scheme GUID: 64a64f24-65b9-4b56-befd-5ec1eaced9b3  (Silent)
- git fceb895d4e8df71131086a94652b5cab1b120850 | Blade.dll 74b6728036b170d9 | GNU Fortran (Rev4, Built by MSYS2 project) 16.2.0 | Intel(R) Fortran Compiler for applications running on Intel(R) 64, Version 2026.1.0 Build 20260617 | clang version 22.1.8 (https://github.com/msys2/MINGW-packages d34feaaa1d0aff624c870910b6f2589aa40d20de)
- size `full` | duration 3291.8 s | pins: pins-A.json, pins-B.json
- PRIMARY estimand: the complete timed region. ratio = median(arm) / median(Blade reference), round-block bootstrap 95% CI (10000 resamples); ratio > 1 = Blade faster. Verdicts (delta 0.1): win / detectable-win / equivalent / detectable-loss / loss / inconclusive; `suppressed` = the block failed a drift, trend or control check. Intervals are pointwise; no suite-wide claim. incremental = median(arm) - median(its own input-only twin), NOT kernel time. Reference: blade-llvm (serial), blade-cpp (mt8, or when the LLVM lane refused). Fresh-process, allocation-inclusive measurements.
- **FLAG**: fusedchain/serial-repro-native: inference suppressed: trend: blade-llvm first-half vs second-half median moved 5.7%; trend: gfortran-natural first-half vs second-half median moved 9.1%; trend: ifx-expert first-half vs second-half median moved 7.9%; trend: ifx-natural-inputonly first-half vs second-half median moved 6.3%; trend: ifx-expert-inputonly first-half vs second-half median moved 6.9%; trend: control compute moved 14.2% within the block; trend: control memory moved 8.1% within the block
- **FLAG**: onepass/serial-repro-native: inference suppressed: trend: ifx-expert first-half vs second-half median moved 5.3%; trend: blade-cpp-inputonly first-half vs second-half median moved 5.7%; trend: ifx-natural-inputonly first-half vs second-half median moved 7.3%

## serial-repro-native

Blade reference per kernel: fusedchain=blade-llvm, maskagg=blade-cpp, onepass=blade-llvm, dot=blade-llvm, saxpy=blade-llvm

| kernel | arm | status | n | min (s) | median (s) | IQR (s) | ratio vs ref [95% CI] | verdict | hypothesis | fold form | parallelism | peak WS (MB) | incremental (s) [CI] | incremental ratio [CI] | notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| fusedchain | blade-llvm | ok | 21 | 0.006774 | 0.00716 | 0.0006001 | ref |  |  | in-order | serial | 19.8 | 0.002534 [0.00196, 0.002858] | ref |  |
| fusedchain | blade-cpp | ok | 21 | 0.008159 | 0.008374 | 0.0004274 | 1.170 [1.119, 1.198] | suppressed |  | in-order | serial | 20.6 | 0.001542 [0.001385, 0.001684] | 0.608 [0.516, 0.804] |  |
| fusedchain | gfortran-natural | ok | 21 | 0.01735 | 0.02008 | 0.003587 | 2.805 [2.597, 3.099] | suppressed | WIN 1.5-2.5x (no temporaries) |  | serial | 66.1 | 0.01328 [0.01183, 0.01503] | 5.241 [4.360, 7.478] |  |
| fusedchain | gfortran-expert | ok | 21 | 0.008242 | 0.008775 | 0.001155 | 1.226 [1.168, 1.330] | suppressed | parity |  | serial | 20.3 | 0.001983 [0.001624, 0.002671] | 0.782 [0.616, 1.142] |  |
| fusedchain | ifx-natural | ok | 21 | 0.0134 | 0.01478 | 0.002706 | 2.065 [1.920, 2.206] | suppressed | WIN 1.5-2.5x (no temporaries) |  | serial | 65.6 | 0.01009 [0.009414, 0.011] | 3.982 [3.375, 5.463] |  |
| fusedchain | ifx-expert | ok | 21 | 0.004878 | 0.005319 | 0.0007485 | 0.743 [0.705, 0.803] | suppressed | parity |  | serial | 19.8 | 0.0006533 [0.0002228, 0.0009868] | 0.258 [0.091, 0.411] |  |
| fusedchain | blade-llvm-inputonly | ok | 21 | 0.004351 | 0.004625 | 0.0008889 |  |  |  | in-order | serial | 19.8 |  |  |  |
| fusedchain | blade-cpp-inputonly | ok | 21 | 0.006575 | 0.006833 | 0.0003545 |  |  |  | in-order | serial | 20.6 |  |  |  |
| fusedchain | gfortran-natural-inputonly | ok | 21 | 0.006547 | 0.006801 | 0.0006178 |  |  |  |  | serial | 20.3 |  |  |  |
| fusedchain | gfortran-expert-inputonly | ok | 21 | 0.006547 | 0.006792 | 0.000411 |  |  |  |  | serial | 20.3 |  |  |  |
| fusedchain | ifx-natural-inputonly | ok | 21 | 0.004379 | 0.004692 | 0.0006433 |  |  |  |  | serial | 19.9 |  |  |  |
| fusedchain | ifx-expert-inputonly | ok | 21 | 0.004379 | 0.004666 | 0.0006159 |  |  |  |  | serial | 19.8 |  |  |  |

> fusedchain serial-repro-native: inference suppressed -- trend: blade-llvm first-half vs second-half median moved 5.7%; trend: gfortran-natural first-half vs second-half median moved 9.1%; trend: ifx-expert first-half vs second-half median moved 7.9%; trend: ifx-natural-inputonly first-half vs second-half median moved 6.3%; trend: ifx-expert-inputonly first-half vs second-half median moved 6.9%; trend: control compute moved 14.2% within the block; trend: control memory moved 8.1% within the block

| maskagg | blade-llvm | fallback | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | a compound-index initializer; expected fallback (the LLVM lane refuses `compound` (notes/llvm-acceptance-B.md); blade-cpp is the Blade figure) |
| maskagg | blade-cpp | ok | 21 | 0.06456 | 0.06886 | 0.003996 | ref |  |  | in-order | serial | 46.5 | 0.06556 [0.06502, 0.06831] | ref |  |
| maskagg | gfortran-natural | ok | 21 | 0.008172 | 0.008288 | 0.0001849 | 0.120 [0.116, 0.122] | loss | LOSS 1.3-2x |  | serial | 17.4 | 0.004967 [0.004881, 0.005039] | 0.076 [0.072, 0.077] |  |
| maskagg | gfortran-expert | ok | 21 | 0.004749 | 0.004982 | 0.0003055 | 0.072 [0.067, 0.074] | loss | LOSS 1.3-2x |  | serial | 16.5 | 0.001623 [0.001402, 0.001741] | 0.025 [0.021, 0.027] |  |
| maskagg | ifx-natural | ok | 21 | 0.01032 | 0.01052 | 0.0002819 | 0.153 [0.147, 0.156] | loss | LOSS 1.3-2x |  | serial | 23.6 | 0.008327 [0.008264, 0.008505] | 0.127 [0.122, 0.130] |  |
| maskagg | ifx-expert | ok | 21 | 0.003645 | 0.003752 | 0.0001219 | 0.054 [0.052, 0.055] | loss | LOSS 1.3-2x |  | serial | 16.0 | 0.001576 [0.001515, 0.001621] | 0.024 [0.023, 0.025] |  |
| maskagg | blade-llvm-inputonly | ok | 21 | 0.002146 | 0.002177 | 5.01e-05 |  |  |  | in-order | serial | 12.2 |  |  |  |
| maskagg | blade-cpp-inputonly | ok | 21 | 0.003235 | 0.003304 | 7.87e-05 |  |  |  | in-order | serial | 12.9 |  |  |  |
| maskagg | gfortran-natural-inputonly | ok | 21 | 0.003292 | 0.003321 | 7.23e-05 |  |  |  |  | serial | 12.6 |  |  |  |
| maskagg | gfortran-expert-inputonly | ok | 21 | 0.003281 | 0.003359 | 0.0001282 |  |  |  |  | serial | 12.7 |  |  |  |
| maskagg | ifx-natural-inputonly | ok | 21 | 0.00215 | 0.002189 | 6.28e-05 |  |  |  |  | serial | 12.2 |  |  |  |
| maskagg | ifx-expert-inputonly | ok | 21 | 0.002149 | 0.002175 | 3.28e-05 |  |  |  |  | serial | 12.2 |  |  |  |
| onepass | blade-llvm | ok | 21 | 0.01647 | 0.01716 | 0.0009009 | ref |  |  | in-order | serial | 42.7 | 0.005372 [0.004542, 0.005893] | ref |  |
| onepass | blade-cpp | ok | 21 | 0.02079 | 0.02109 | 0.001064 | 1.229 [1.210, 1.276] | suppressed |  | in-order | serial | 43.5 | 0.003975 [0.003689, 0.004738] | 0.740 [0.655, 0.941] |  |
| onepass | gfortran-natural | ok | 21 | 0.03227 | 0.03282 | 0.0009868 | 1.913 [1.883, 1.965] | suppressed | WIN 2-3x (one pass vs four) |  | serial | 43.2 | 0.0157 [0.01512, 0.01632] | 2.923 [2.666, 3.464] |  |
| onepass | gfortran-expert | ok | 21 | 0.02106 | 0.02167 | 0.000806 | 1.263 [1.243, 1.292] | suppressed | parity |  | serial | 43.2 | 0.004403 [0.003714, 0.004887] | 0.820 [0.676, 0.978] |  |
| onepass | ifx-natural | ok | 21 | 0.01243 | 0.0135 | 0.001516 | 0.787 [0.754, 0.834] | suppressed | WIN 2-3x (one pass vs four) |  | serial | 42.7 | 0.001251 [0.0007991, 0.002063] | 0.233 [0.141, 0.402] |  |
| onepass | ifx-expert | ok | 21 | 0.01249 | 0.01321 | 0.0006998 | 0.770 [0.751, 0.793] | suppressed | parity |  | serial | 42.7 | 0.001138 [0.0006175, 0.001534] | 0.212 [0.117, 0.299] |  |
| onepass | blade-llvm-inputonly | ok | 21 | 0.01116 | 0.01179 | 0.001456 |  |  |  | in-order | serial | 42.7 |  |  |  |
| onepass | blade-cpp-inputonly | ok | 21 | 0.01655 | 0.01711 | 0.001127 |  |  |  | in-order | serial | 43.5 |  |  |  |
| onepass | gfortran-natural-inputonly | ok | 21 | 0.01673 | 0.01712 | 0.0009254 |  |  |  |  | serial | 43.2 |  |  |  |
| onepass | gfortran-expert-inputonly | ok | 21 | 0.01662 | 0.01726 | 0.001334 |  |  |  |  | serial | 43.2 |  |  |  |
| onepass | ifx-natural-inputonly | ok | 21 | 0.01117 | 0.01225 | 0.0009553 |  |  |  |  | serial | 42.7 |  |  |  |
| onepass | ifx-expert-inputonly | ok | 21 | 0.01132 | 0.01207 | 0.0008942 |  |  |  |  | serial | 42.7 |  |  |  |

> onepass serial-repro-native: inference suppressed -- trend: ifx-expert first-half vs second-half median moved 5.3%; trend: blade-cpp-inputonly first-half vs second-half median moved 5.7%; trend: ifx-natural-inputonly first-half vs second-half median moved 7.3%

| dot | blade-llvm | ok | 21 | 0.01669 | 0.01698 | 0.000541 | ref |  |  | in-order | serial | 50.3 | 0.003278 [0.003021, 0.003651] | ref |  |
| dot | blade-cpp | ok | 21 | 0.02208 | 0.02273 | 0.001866 | 1.338 [1.305, 1.356] | win |  | in-order | serial | 51.1 | 0.002632 [0.002219, 0.002907] | 0.803 [0.652, 0.960] |  |
| dot | gfortran-natural | ok | 21 | 0.01979 | 0.02014 | 0.0005635 | 1.186 [1.160, 1.202] | win | parity |  | serial | 50.8 | 0.002379 [0.002206, 0.002671] | 0.726 [0.628, 0.857] |  |
| dot | gfortran-expert | ok | 21 | 0.01971 | 0.02007 | 0.001013 | 1.182 [1.156, 1.194] | win | parity |  | serial | 50.8 | 0.002348 [0.002169, 0.002606] | 0.716 [0.622, 0.888] |  |
| dot | ifx-natural | ok | 21 | 0.01368 | 0.01415 | 0.0006806 | 0.833 [0.813, 0.853] | loss | parity |  | serial | 50.4 | 0.002372 [0.0019, 0.002786] | 0.723 [0.577, 0.867] |  |
| dot | ifx-expert | ok | 21 | 0.01169 | 0.01222 | 0.0007343 | 0.720 [0.696, 0.744] | loss | parity |  | serial | 50.4 | 0.0006071 [0.0003605, 0.001043] | 0.185 [0.105, 0.328] |  |
| dot | blade-llvm-inputonly | ok | 21 | 0.01339 | 0.0137 | 0.0004868 |  |  |  | in-order | serial | 50.3 |  |  |  |
| dot | blade-cpp-inputonly | ok | 21 | 0.01967 | 0.0201 | 0.0005128 |  |  |  | in-order | serial | 51.1 |  |  |  |
| dot | gfortran-natural-inputonly | ok | 21 | 0.01729 | 0.01776 | 0.0003529 |  |  |  |  | serial | 50.8 |  |  |  |
| dot | gfortran-expert-inputonly | ok | 21 | 0.01744 | 0.01772 | 0.0004476 |  |  |  |  | serial | 50.8 |  |  |  |
| dot | ifx-natural-inputonly | ok | 21 | 0.01131 | 0.01178 | 0.001156 |  |  |  |  | serial | 50.4 |  |  |  |
| dot | ifx-expert-inputonly | ok | 21 | 0.01124 | 0.01161 | 0.0005402 |  |  |  |  | serial | 50.4 |  |  |  |
| saxpy | blade-llvm | ok | 21 | 0.01515 | 0.01549 | 0.0003152 | ref |  |  | in-order | serial | 49.8 | 0.006356 [0.006183, 0.006631] | ref |  |
| saxpy | blade-cpp | ok | 21 | 0.01815 | 0.0184 | 0.0004201 | 1.188 [1.171, 1.204] | win |  | in-order | serial | 50.7 | 0.005113 [0.004958, 0.005343] | 0.804 [0.755, 0.837] |  |
| saxpy | gfortran-natural | ok | 21 | 0.01635 | 0.01672 | 0.0003598 | 1.079 [1.062, 1.093] | detectable-win | parity |  | serial | 50.8 | 0.004875 [0.004736, 0.005098] | 0.767 [0.726, 0.810] |  |
| saxpy | gfortran-expert | ok | 21 | 0.01524 | 0.01536 | 0.0002874 | 0.992 [0.977, 1.003] | equivalent | parity |  | serial | 50.8 | 0.003516 [0.003454, 0.003669] | 0.553 [0.527, 0.587] |  |
| saxpy | ifx-natural | ok | 21 | 0.01272 | 0.01303 | 0.0002574 | 0.841 [0.828, 0.849] | loss | parity |  | serial | 50.4 | 0.005294 [0.005188, 0.005483] | 0.833 [0.798, 0.870] |  |
| saxpy | ifx-expert | ok | 21 | 0.009455 | 0.009802 | 0.0002335 | 0.633 [0.621, 0.639] | loss | parity |  | serial | 50.4 | 0.002038 [0.001822, 0.002194] | 0.321 [0.283, 0.345] |  |
| saxpy | blade-llvm-inputonly | ok | 21 | 0.008785 | 0.009131 | 0.0003558 |  |  |  | in-order | serial | 35.1 |  |  |  |
| saxpy | blade-cpp-inputonly | ok | 21 | 0.0131 | 0.01328 | 0.0002484 |  |  |  | in-order | serial | 35.8 |  |  |  |
| saxpy | gfortran-natural-inputonly | ok | 21 | 0.0115 | 0.01184 | 0.0002356 |  |  |  |  | serial | 35.6 |  |  |  |
| saxpy | gfortran-expert-inputonly | ok | 21 | 0.01158 | 0.01185 | 7.13e-05 |  |  |  |  | serial | 35.5 |  |  |  |
| saxpy | ifx-natural-inputonly | ok | 21 | 0.007569 | 0.007732 | 0.0002024 |  |  |  |  | serial | 35.1 |  |  |  |
| saxpy | ifx-expert-inputonly | ok | 21 | 0.007423 | 0.007764 | 0.0003424 |  |  |  |  | serial | 35.1 |  |  |  |

## Controls

- compute (recur/gfortran-expert/serial-repro-native/full): n=105 median 0.01295 s, baseline 0.01342 s
- memory (saxpy/gfortran-expert/serial-repro-native/control): n=105 median 0.005438 s, baseline 0.005379 s
