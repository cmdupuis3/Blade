# Blade vs Fortran -- bench session 2026-10-19T14:41:45

- host LAPTOP-350H6LDB | AMD Ryzen 7 5800HS with Radeon Graphics | 8C/16T | 15.41 GB | power ac | max processor state AC 100% | boost AC None | Power Scheme GUID: 64a64f24-65b9-4b56-befd-5ec1eaced9b3  (Silent)
- git 39a8a869ff410c79edbe599a98257d4012241702 | Blade.dll bb52a4c2a008d198 | GNU Fortran (Rev4, Built by MSYS2 project) 16.2.0 | Intel(R) Fortran Compiler for applications running on Intel(R) 64, Version 2026.1.0 Build 20260617 | clang version 22.1.8 (https://github.com/msys2/MINGW-packages d34feaaa1d0aff624c870910b6f2589aa40d20de)
- size `full` | duration 1064.2 s | pins: pins-A.json, pins-B.json
- PRIMARY estimand: the complete timed region. ratio = median(arm) / median(Blade reference), round-block bootstrap 95% CI (10000 resamples); ratio > 1 = Blade faster. Verdicts (delta 0.1): win / detectable-win / equivalent / detectable-loss / loss / inconclusive; `suppressed` = the block failed a drift, trend or control check. Intervals are pointwise; no suite-wide claim. incremental = median(arm) - median(its own input-only twin), NOT kernel time. Reference: blade-llvm (serial), blade-cpp (mt8, or when the LLVM lane refused). Fresh-process, allocation-inclusive measurements.
- **FLAG**: fusedchain/serial-repro-native: inference suppressed: trend: blade-llvm-inputonly first-half vs second-half median moved 5.4%; trend: ifx-expert-inputonly first-half vs second-half median moved 6.7%
- **FLAG**: maskagg/serial-repro-native: inference suppressed: trend: blade-llvm first-half vs second-half median moved 8.8%; trend: ifx-natural-inputonly first-half vs second-half median moved 5.0%
- **FLAG**: saxpy/serial-repro-native: inference suppressed: trend: ifx-expert-inputonly first-half vs second-half median moved 7.6%

## serial-repro-native

Blade reference per kernel: fusedchain=blade-llvm, maskagg=blade-llvm, onepass=blade-llvm, dot=blade-llvm, saxpy=blade-llvm

| kernel | arm | status | n | min (s) | median (s) | IQR (s) | ratio vs ref [95% CI] | verdict | hypothesis | fold form | parallelism | peak WS (MB) | incremental (s) [CI] | incremental ratio [CI] | notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| fusedchain | blade-llvm | ok | 21 | 0.005914 | 0.006227 | 0.0005657 | ref |  |  | in-order | serial | 19.9 | 0.001661 [0.001436, 0.002075] | ref |  |
| fusedchain | blade-cpp | ok | 21 | 0.01004 | 0.01023 | 0.0002552 | 1.643 [1.554, 1.681] | suppressed |  | in-order | serial | 20.6 | 0.001572 [0.001473, 0.00173] | 0.946 [0.732, 1.125] |  |
| fusedchain | gfortran-natural | ok | 21 | 0.01777 | 0.01857 | 0.0005428 | 2.981 [2.823, 3.050] | suppressed | WIN 1.5-2.5x (no temporaries) |  | serial | 66.1 | 0.01159 [0.01115, 0.01179] | 6.978 [5.581, 8.001] |  |
| fusedchain | gfortran-expert | ok | 21 | 0.008285 | 0.0086 | 0.0003268 | 1.381 [1.321, 1.418] | suppressed | parity |  | serial | 20.4 | 0.001713 [0.001299, 0.001934] | 1.031 [0.776, 1.202] |  |
| fusedchain | ifx-natural | ok | 21 | 0.01349 | 0.01445 | 0.000214 | 2.321 [2.186, 2.378] | suppressed | WIN 1.5-2.5x (no temporaries) |  | serial | 65.7 | 0.009756 [0.009543, 0.009996] | 5.873 [4.687, 6.766] |  |
| fusedchain | ifx-expert | ok | 21 | 0.004893 | 0.005227 | 0.0003883 | 0.839 [0.783, 0.866] | suppressed | parity |  | serial | 19.9 | 0.0004083 [2.46e-05, 0.0007924] | 0.246 [0.014, 0.498] |  |
| fusedchain | blade-llvm-inputonly | ok | 21 | 0.00416 | 0.004566 | 0.00043 |  |  |  | in-order | serial | 19.9 |  |  |  |
| fusedchain | blade-cpp-inputonly | ok | 21 | 0.008418 | 0.008657 | 0.0002231 |  |  |  | in-order | serial | 20.6 |  |  |  |
| fusedchain | gfortran-natural-inputonly | ok | 21 | 0.00672 | 0.006974 | 0.0004652 |  |  |  |  | serial | 20.4 |  |  |  |
| fusedchain | gfortran-expert-inputonly | ok | 21 | 0.006574 | 0.006887 | 0.0005338 |  |  |  |  | serial | 20.4 |  |  |  |
| fusedchain | ifx-natural-inputonly | ok | 21 | 0.004359 | 0.004695 | 0.0005601 |  |  |  |  | serial | 19.9 |  |  |  |
| fusedchain | ifx-expert-inputonly | ok | 21 | 0.004295 | 0.004819 | 0.0007499 |  |  |  |  | serial | 19.9 |  |  |  |

> fusedchain serial-repro-native: inference suppressed -- trend: blade-llvm-inputonly first-half vs second-half median moved 5.4%; trend: ifx-expert-inputonly first-half vs second-half median moved 6.7%

| maskagg | blade-llvm | ok | 21 | 0.0053 | 0.005942 | 0.0006474 | ref |  |  | in-order | serial | 24.6 | 0.003758 [0.00335, 0.003901] | ref |  |
| maskagg | blade-cpp | ok | 21 | 0.007712 | 0.008265 | 0.0007868 | 1.391 [1.341, 1.498] | suppressed |  | in-order | serial | 25.4 | 0.003983 [0.003763, 0.004513] | 1.060 [0.980, 1.241] |  |
| maskagg | gfortran-natural | ok | 21 | 0.008253 | 0.008547 | 0.000375 | 1.438 [1.395, 1.513] | suppressed | LOSS 1.3-2x |  | serial | 17.5 | 0.005179 [0.004929, 0.005246] | 1.378 [1.299, 1.520] |  |
| maskagg | gfortran-expert | ok | 21 | 0.004769 | 0.004982 | 0.0003979 | 0.839 [0.806, 0.895] | suppressed | LOSS 1.3-2x |  | serial | 16.6 | 0.001466 [0.00129, 0.001685] | 0.390 [0.337, 0.467] |  |
| maskagg | ifx-natural | ok | 21 | 0.01031 | 0.01077 | 0.0004731 | 1.813 [1.763, 1.939] | suppressed | LOSS 1.3-2x |  | serial | 23.7 | 0.008517 [0.008316, 0.008688] | 2.267 [2.150, 2.530] |  |
| maskagg | ifx-expert | ok | 21 | 0.003692 | 0.00402 | 0.0003694 | 0.676 [0.637, 0.722] | suppressed | LOSS 1.3-2x |  | serial | 16.1 | 0.001568 [0.001332, 0.001874] | 0.417 [0.355, 0.516] |  |
| maskagg | blade-llvm-inputonly | ok | 21 | 0.002059 | 0.002184 | 0.0003155 |  |  |  | in-order | serial | 12.2 |  |  |  |
| maskagg | blade-cpp-inputonly | ok | 21 | 0.00419 | 0.004282 | 7.63e-05 |  |  |  | in-order | serial | 13.0 |  |  |  |
| maskagg | gfortran-natural-inputonly | ok | 21 | 0.003287 | 0.003368 | 0.0001505 |  |  |  |  | serial | 12.7 |  |  |  |
| maskagg | gfortran-expert-inputonly | ok | 21 | 0.003269 | 0.003516 | 0.0003562 |  |  |  |  | serial | 12.7 |  |  |  |
| maskagg | ifx-natural-inputonly | ok | 21 | 0.002147 | 0.002257 | 0.0002649 |  |  |  |  | serial | 12.3 |  |  |  |
| maskagg | ifx-expert-inputonly | ok | 21 | 0.002159 | 0.002452 | 0.0003668 |  |  |  |  | serial | 12.3 |  |  |  |

> maskagg serial-repro-native: inference suppressed -- trend: blade-llvm first-half vs second-half median moved 8.8%; trend: ifx-natural-inputonly first-half vs second-half median moved 5.0%

| onepass | blade-llvm | ok | 21 | 0.01592 | 0.01636 | 0.0003724 | ref |  |  | in-order | serial | 42.8 | 0.004506 [0.004034, 0.004812] | ref |  |
| onepass | blade-cpp | ok | 21 | 0.02602 | 0.02631 | 0.0003462 | 1.608 [1.580, 1.623] | win |  | in-order | serial | 43.5 | 0.004433 [0.004236, 0.004579] | 0.984 [0.906, 1.112] |  |
| onepass | gfortran-natural | ok | 21 | 0.03334 | 0.03407 | 0.000498 | 2.082 [2.050, 2.103] | win | WIN 2-3x (one pass vs four) |  | serial | 43.2 | 0.01641 [0.01614, 0.01656] | 3.641 [3.409, 4.054] |  |
| onepass | gfortran-expert | ok | 21 | 0.02178 | 0.02216 | 0.0002234 | 1.354 [1.332, 1.365] | win | parity |  | serial | 43.2 | 0.004482 [0.004325, 0.004586] | 0.995 [0.922, 1.107] |  |
| onepass | ifx-natural | ok | 21 | 0.0132 | 0.01379 | 0.0006311 | 0.843 [0.824, 0.859] | loss | WIN 2-3x (one pass vs four) |  | serial | 42.8 | 0.001445 [0.00109, 0.001707] | 0.321 [0.242, 0.399] |  |
| onepass | ifx-expert | ok | 21 | 0.01306 | 0.01342 | 0.0005512 | 0.820 [0.803, 0.847] | loss | parity |  | serial | 42.8 | 0.0009791 [0.000763, 0.00173] | 0.217 [0.164, 0.400] |  |
| onepass | blade-llvm-inputonly | ok | 21 | 0.01125 | 0.01186 | 0.0007234 |  |  |  | in-order | serial | 42.8 |  |  |  |
| onepass | blade-cpp-inputonly | ok | 21 | 0.02155 | 0.02188 | 0.00032 |  |  |  | in-order | serial | 43.5 |  |  |  |
| onepass | gfortran-natural-inputonly | ok | 21 | 0.01723 | 0.01766 | 0.0002907 |  |  |  |  | serial | 43.2 |  |  |  |
| onepass | gfortran-expert-inputonly | ok | 21 | 0.01713 | 0.01768 | 0.0002499 |  |  |  |  | serial | 43.2 |  |  |  |
| onepass | ifx-natural-inputonly | ok | 21 | 0.01171 | 0.01235 | 0.0005363 |  |  |  |  | serial | 42.8 |  |  |  |
| onepass | ifx-expert-inputonly | ok | 21 | 0.01161 | 0.01244 | 0.0006153 |  |  |  |  | serial | 42.8 |  |  |  |
| dot | blade-llvm | ok | 21 | 0.01671 | 0.01733 | 0.0005158 | ref |  |  | in-order | serial | 50.4 | 0.002821 [0.002451, 0.003079] | ref |  |
| dot | blade-cpp | ok | 21 | 0.02817 | 0.02849 | 0.0002919 | 1.644 [1.628, 1.680] | win |  | in-order | serial | 51.1 | 0.002654 [0.002513, 0.002871] | 0.941 [0.840, 1.123] |  |
| dot | gfortran-natural | ok | 21 | 0.02054 | 0.0211 | 0.0005843 | 1.217 [1.198, 1.243] | win | parity |  | serial | 50.9 | 0.00265 [0.002348, 0.003074] | 0.939 [0.804, 1.137] |  |
| dot | gfortran-expert | ok | 21 | 0.02051 | 0.02113 | 0.0003567 | 1.219 [1.207, 1.242] | win | parity |  | serial | 50.9 | 0.002613 [0.002313, 0.002869] | 0.926 [0.794, 1.096] |  |
| dot | ifx-natural | ok | 21 | 0.01445 | 0.01506 | 0.0002005 | 0.869 [0.860, 0.885] | loss | parity |  | serial | 50.4 | 0.002671 [0.002445, 0.00298] | 0.947 [0.826, 1.162] |  |
| dot | ifx-expert | ok | 21 | 0.01255 | 0.01308 | 0.0005334 | 0.755 [0.737, 0.770] | loss | parity |  | serial | 50.4 | 0.0005932 [0.0002794, 0.0008741] | 0.210 [0.100, 0.317] |  |
| dot | blade-llvm-inputonly | ok | 21 | 0.01375 | 0.01451 | 0.0004394 |  |  |  | in-order | serial | 50.4 |  |  |  |
| dot | blade-cpp-inputonly | ok | 21 | 0.02554 | 0.02584 | 0.0003632 |  |  |  | in-order | serial | 51.2 |  |  |  |
| dot | gfortran-natural-inputonly | ok | 21 | 0.01805 | 0.01845 | 0.0003361 |  |  |  |  | serial | 50.9 |  |  |  |
| dot | gfortran-expert-inputonly | ok | 21 | 0.01803 | 0.01852 | 0.000553 |  |  |  |  | serial | 50.9 |  |  |  |
| dot | ifx-natural-inputonly | ok | 21 | 0.01166 | 0.01239 | 0.0007299 |  |  |  |  | serial | 50.4 |  |  |  |
| dot | ifx-expert-inputonly | ok | 21 | 0.01176 | 0.01249 | 0.0004404 |  |  |  |  | serial | 50.4 |  |  |  |
| saxpy | blade-llvm | ok | 21 | 0.01456 | 0.0152 | 0.0006663 | ref |  |  | in-order | serial | 50.4 | 0.005931 [0.005581, 0.006256] | ref |  |
| saxpy | blade-cpp | ok | 21 | 0.02253 | 0.023 | 0.0003837 | 1.513 [1.485, 1.547] | suppressed |  | in-order | serial | 50.8 | 0.005595 [0.005196, 0.005966] | 0.943 [0.854, 1.037] |  |
| saxpy | gfortran-natural | ok | 21 | 0.01714 | 0.01761 | 0.0005946 | 1.159 [1.134, 1.208] | suppressed | parity |  | serial | 50.9 | 0.005309 [0.00517, 0.005882] | 0.895 [0.845, 1.030] |  |
| saxpy | gfortran-expert | ok | 21 | 0.01538 | 0.01644 | 0.000675 | 1.082 [1.043, 1.112] | suppressed | parity |  | serial | 50.9 | 0.004177 [0.003683, 0.004559] | 0.704 [0.619, 0.780] |  |
| saxpy | ifx-natural | ok | 21 | 0.01355 | 0.01411 | 0.0004096 | 0.928 [0.901, 0.952] | suppressed | parity |  | serial | 50.4 | 0.006054 [0.005775, 0.006284] | 1.021 [0.948, 1.106] |  |
| saxpy | ifx-expert | ok | 21 | 0.009839 | 0.01043 | 0.000528 | 0.686 [0.668, 0.706] | suppressed | parity |  | serial | 50.4 | 0.002143 [0.001648, 0.002498] | 0.361 [0.284, 0.435] |  |
| saxpy | blade-llvm-inputonly | ok | 21 | 0.008707 | 0.009267 | 0.0006569 |  |  |  | in-order | serial | 35.1 |  |  |  |
| saxpy | blade-cpp-inputonly | ok | 21 | 0.01702 | 0.0174 | 0.0006789 |  |  |  | in-order | serial | 35.9 |  |  |  |
| saxpy | gfortran-natural-inputonly | ok | 21 | 0.01174 | 0.0123 | 0.0005082 |  |  |  |  | serial | 35.6 |  |  |  |
| saxpy | gfortran-expert-inputonly | ok | 21 | 0.01169 | 0.01226 | 0.0005195 |  |  |  |  | serial | 35.6 |  |  |  |
| saxpy | ifx-natural-inputonly | ok | 21 | 0.007732 | 0.008052 | 0.0003786 |  |  |  |  | serial | 35.2 |  |  |  |
| saxpy | ifx-expert-inputonly | ok | 21 | 0.007672 | 0.008287 | 0.0006654 |  |  |  |  | serial | 35.2 |  |  |  |

> saxpy serial-repro-native: inference suppressed -- trend: ifx-expert-inputonly first-half vs second-half median moved 7.6%


## Controls

- compute (recur/gfortran-expert/serial-repro-native/full): n=105 median 0.01366 s, baseline 0.01366 s
- memory (saxpy/gfortran-expert/serial-repro-native/control): n=105 median 0.005548 s, baseline 0.005794 s
