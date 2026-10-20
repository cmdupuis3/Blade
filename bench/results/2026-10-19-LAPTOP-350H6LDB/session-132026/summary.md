# Blade vs Fortran -- bench session 2026-10-19T13:20:26

- host LAPTOP-350H6LDB | AMD Ryzen 7 5800HS with Radeon Graphics | 8C/16T | 15.41 GB | power ac | max processor state AC 100% | boost AC None | Power Scheme GUID: 64a64f24-65b9-4b56-befd-5ec1eaced9b3  (Silent)
- git 39a8a869ff410c79edbe599a98257d4012241702 | Blade.dll bb52a4c2a008d198 | GNU Fortran (Rev4, Built by MSYS2 project) 16.2.0 | Intel(R) Fortran Compiler for applications running on Intel(R) 64, Version 2026.1.0 Build 20260617 | clang version 22.1.8 (https://github.com/msys2/MINGW-packages d34feaaa1d0aff624c870910b6f2589aa40d20de)
- size `full` | duration 937.5 s | pins: pins-A.json, pins-B.json
- PRIMARY estimand: the complete timed region. ratio = median(arm) / median(Blade reference), round-block bootstrap 95% CI (10000 resamples); ratio > 1 = Blade faster. Verdicts (delta 0.1): win / detectable-win / equivalent / detectable-loss / loss / inconclusive; `suppressed` = the block failed a drift, trend or control check. Intervals are pointwise; no suite-wide claim. incremental = median(arm) - median(its own input-only twin), NOT kernel time. Reference: blade-llvm (serial), blade-cpp (mt8, or when the LLVM lane refused). Fresh-process, allocation-inclusive measurements.
- **FLAG**: gram/serial-licensed-native: inference suppressed: trend: gfortran-natural first-half vs second-half median moved 18.8%
- **FLAG**: recur/serial-licensed-native: inference suppressed: trend: ifx-expert first-half vs second-half median moved 6.2%
- **FLAG**: stencil/serial-licensed-native: inference suppressed: trend: blade-llvm-inputonly first-half vs second-half median moved 6.2%

## serial-licensed-native

Blade reference per kernel: antisym=blade-llvm, band=blade-llvm, comoment3=blade-llvm, gram=blade-llvm, pairkernel=blade-llvm, recur=blade-llvm, stencil=blade-llvm

| kernel | arm | status | n | min (s) | median (s) | IQR (s) | ratio vs ref [95% CI] | verdict | hypothesis | fold form | parallelism | peak WS (MB) | incremental (s) [CI] | incremental ratio [CI] | notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| antisym | blade-llvm | ok | 21 | 0.0269 | 0.02801 | 0.0009191 | ref |  |  | reordered (4 reassoc flags) | serial | 24.9 |  |  |  |
| antisym | blade-cpp | ok | 21 | 0.02912 | 0.02997 | 0.0009731 | 1.070 [1.049, 1.108] | detectable-win |  | reordered (5 SIMD_REDUCTION/K-lane) | serial | 25.7 |  |  |  |
| antisym | gfortran-natural | ok | 21 | 0.7694 | 0.7776 | 0.003638 | 27.761 [27.467, 28.400] | win | WIN ~2x (half the cells, no diagonal) |  | serial | 23.5 |  |  |  |
| antisym | gfortran-expert | ok | 21 | 0.03042 | 0.03119 | 0.001434 | 1.113 [1.099, 1.152] | win | parity +-15% |  | serial | 32.7 |  |  |  |
| antisym | ifx-natural | ok | 21 | 0.3307 | 0.3358 | 0.003756 | 11.988 [11.858, 12.327] | win | WIN ~2x (half the cells, no diagonal) |  | serial | 23.0 |  |  |  |
| antisym | ifx-expert | ok | 21 | 0.02931 | 0.0301 | 0.001342 | 1.075 [1.052, 1.115] | detectable-win | parity +-15% |  | serial | 32.3 |  |  |  |
| band | blade-llvm | ok | 21 | 0.0004077 | 0.0004158 | 1.94e-05 | ref |  |  | reordered (2 reassoc flags) | serial | 4.8 |  |  |  |
| band | blade-cpp | ok | 21 | 0.0006216 | 0.0006335 | 2.41e-05 | 1.524 [1.479, 1.555] | win |  | reordered (1 SIMD_REDUCTION/K-lane) | serial | 5.5 |  |  |  |
| band | gfortran-natural | ok | 21 | 0.2071 | 0.2082 | 0.004748 | 500.649 [486.095, 512.012] | win | win 10x+ (the natural tier is the n^2 predicate nest, a straw man by construction) |  | serial | 5.4 |  |  |  |
| band | gfortran-expert | ok | 21 | 0.0008147 | 0.0008159 | 7.3e-06 | 1.962 [1.908, 1.995] | win | parity in both lanes: licensed folds walk the band by diagonal (k = i - j outer) as the licensed expert does, with phi hoisted per diagonal by the back end (G33); repro keeps the lex order with the terms vectorized and summed in order (G30) |  | serial | 5.4 |  |  |  |
| band | ifx-natural | ok | 21 | 0.3499 | 0.3523 | 0.002881 | 847.209 [822.795, 860.867] | win | win 10x+ (the natural tier is the n^2 predicate nest, a straw man by construction) |  | serial | 5.1 |  |  |  |
| band | ifx-expert | ok | 21 | 0.001036 | 0.001044 | 6.3e-06 | 2.511 [2.441, 2.552] | win | parity in both lanes: licensed folds walk the band by diagonal (k = i - j outer) as the licensed expert does, with phi hoisted per diagonal by the back end (G33); repro keeps the lex order with the terms vectorized and summed in order (G30) |  | serial | 5.0 |  |  |  |
| comoment3 | blade-llvm | ok | 21 | 0.008544 | 0.008726 | 0.00015 | ref |  |  | reordered (6 reassoc flags) | serial | 11.6 |  |  |  |
| comoment3 | blade-cpp | ok | 21 | 0.008778 | 0.008945 | 0.0002422 | 1.025 [1.017, 1.034] | detectable-win |  | reordered (6 SIMD_REDUCTION/K-lane) | serial | 12.7 |  |  |  |
| comoment3 | gfortran-natural | ok | 21 | 0.6006 | 0.6016 | 0.002289 | 68.948 [68.651, 69.561] | win | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | serial | 43.5 |  |  |  |
| comoment3 | gfortran-expert | ok | 21 | 0.01118 | 0.01156 | 0.0002261 | 1.325 [1.310, 1.335] | win | parity to 1.3x loss |  | serial | 12.4 |  |  |  |
| comoment3 | ifx-natural | ok | 21 | 0.7199 | 0.7232 | 0.002177 | 82.875 [82.532, 83.478] | win | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | serial | 43.1 |  |  |  |
| comoment3 | ifx-expert | ok | 21 | 0.01115 | 0.01168 | 0.0005407 | 1.338 [1.322, 1.377] | win | parity to 1.3x loss |  | serial | 11.9 |  |  |  |
| gram | blade-llvm | ok | 11 | 0.05604 | 0.05811 | 0.001245 | ref |  |  | reordered (4 reassoc flags) | serial | 30.8 |  |  |  |
| gram | blade-cpp | ok | 11 | 0.05965 | 0.06188 | 0.001504 | 1.065 [1.036, 1.090] | suppressed |  | reordered (4 SIMD_REDUCTION/K-lane) | serial | 31.5 |  |  |  |
| gram | gfortran-natural | ok | 11 | 0.5475 | 0.6364 | 0.1319 | 10.951 [9.785, 12.737] | suppressed | win 1.5-2x if matmul is a naive nest; parity/loss if a blocked runtime kernel |  | serial | 36.9 |  |  |  |
| gram | gfortran-expert | ok | 11 | 0.07626 | 0.07853 | 0.001288 | 1.351 [1.327, 1.386] | suppressed | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | serial | 51.7 |  |  |  |
| gram | ifx-natural | ok | 11 | 0.5719 | 0.5771 | 0.005479 | 9.932 [9.796, 10.133] | suppressed | win 1.5-2x if matmul is a naive nest; parity/loss if a blocked runtime kernel |  | serial | 56.0 |  |  |  |
| gram | ifx-expert | ok | 11 | 0.06965 | 0.07162 | 0.001371 | 1.233 [1.207, 1.260] | suppressed | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | serial | 51.3 |  |  |  |

> gram serial-licensed-native: inference suppressed -- trend: gfortran-natural first-half vs second-half median moved 18.8%

| pairkernel | blade-llvm | ok | 21 | 0.02902 | 0.02915 | 0.0002945 | ref |  |  | reordered (4 reassoc flags) | serial | 28.6 |  |  |  |
| pairkernel | blade-cpp | ok | 21 | 0.02928 | 0.02952 | 0.0004187 | 1.013 [1.005, 1.022] | detectable-win |  | reordered (3 SIMD_REDUCTION/K-lane) | serial | 29.4 |  |  |  |
| pairkernel | gfortran-natural | ok | 21 | 0.06009 | 0.06095 | 0.0005471 | 2.091 [2.076, 2.105] | win | WIN ~2x (half the transcendentals) |  | serial | 53.0 |  |  |  |
| pairkernel | gfortran-expert | ok | 21 | 0.03235 | 0.0327 | 0.0002321 | 1.122 [1.114, 1.128] | win | parity within 5% |  | serial | 29.1 |  |  |  |
| pairkernel | ifx-natural | ok | 21 | 0.0642 | 0.06499 | 0.00139 | 2.229 [2.212, 2.254] | win | WIN ~2x (half the transcendentals) |  | serial | 52.5 |  |  |  |
| pairkernel | ifx-expert | ok | 21 | 0.03006 | 0.03052 | 0.0004976 | 1.047 [1.039, 1.056] | detectable-win | parity within 5% |  | serial | 28.6 |  |  |  |
| recur | blade-llvm | ok | 21 | 0.008437 | 0.009159 | 0.001194 | ref |  |  | reordered (3 reassoc flags) | serial | 50.7 |  |  |  |
| recur | blade-cpp | ok | 21 | 0.01006 | 0.01127 | 0.001318 | 1.230 [1.118, 1.275] | suppressed |  | reordered (3 SIMD_REDUCTION/K-lane) | serial | 51.2 |  |  |  |
| recur | gfortran-natural | ok | 21 | 0.01754 | 0.01871 | 0.000699 | 2.043 [1.887, 2.098] | suppressed | parity |  | serial | 51.7 |  |  |  |
| recur | gfortran-expert | ok | 21 | 0.009127 | 0.0102 | 0.0009523 | 1.114 [1.029, 1.159] | suppressed | LOSS 1.5-3x |  | serial | 51.0 |  |  |  |
| recur | ifx-natural | ok | 21 | 0.01459 | 0.01574 | 0.0009473 | 1.718 [1.591, 1.775] | suppressed | parity |  | serial | 51.2 |  |  |  |
| recur | ifx-expert | ok | 21 | 0.009442 | 0.0103 | 0.0008484 | 1.125 [1.028, 1.175] | suppressed | LOSS 1.5-3x |  | serial | 50.6 |  |  |  |

> recur serial-licensed-native: inference suppressed -- trend: ifx-expert first-half vs second-half median moved 6.2%

| stencil | blade-llvm | ok | 21 | 0.009873 | 0.01071 | 0.0008378 | ref |  |  | reordered (4 reassoc flags) | serial | 50.4 | 0.00463 [0.004152, 0.005251] | ref |  |
| stencil | blade-cpp | ok | 21 | 0.01663 | 0.01725 | 0.0009315 | 1.612 [1.520, 1.658] | suppressed |  | reordered (4 SIMD_REDUCTION/K-lane) | serial | 50.9 | 0.005338 [0.00501, 0.005806] | 1.153 [0.998, 1.309] |  |
| stencil | gfortran-natural | ok | 21 | 0.0166 | 0.01764 | 0.0003711 | 1.648 [1.565, 1.673] | suppressed | parity (bandwidth-bound) |  | serial | 50.9 | 0.007309 [0.007083, 0.007643] | 1.579 [1.389, 1.800] |  |
| stencil | gfortran-expert | ok | 21 | 0.01402 | 0.01472 | 0.0004832 | 1.375 [1.302, 1.404] | suppressed | parity |  | serial | 50.9 | 0.005474 [0.005188, 0.00571] | 1.183 [1.034, 1.325] |  |
| stencil | ifx-natural | ok | 21 | 0.01808 | 0.01953 | 0.0005225 | 1.824 [1.727, 1.851] | suppressed | parity (bandwidth-bound) |  | serial | 50.5 | 0.01249 [0.01215, 0.01294] | 2.698 [2.386, 3.038] |  |
| stencil | ifx-expert | ok | 21 | 0.01048 | 0.01165 | 0.000977 | 1.089 [1.019, 1.115] | suppressed | parity |  | serial | 50.5 | 0.005619 [0.004819, 0.006019] | 1.214 [1.001, 1.401] |  |
| stencil | blade-llvm-inputonly | ok | 21 | 0.005561 | 0.006078 | 0.000798 |  |  |  | reordered (2 reassoc flags) | serial | 27.5 |  |  |  |
| stencil | blade-cpp-inputonly | ok | 21 | 0.01169 | 0.01192 | 0.0002243 |  |  |  | reordered (1 SIMD_REDUCTION/K-lane) | serial | 28.3 |  |  |  |
| stencil | gfortran-natural-inputonly | ok | 21 | 0.009844 | 0.01033 | 0.0004552 |  |  |  |  | serial | 28.1 |  |  |  |
| stencil | gfortran-expert-inputonly | ok | 21 | 0.008921 | 0.009248 | 0.0004058 |  |  |  |  | serial | 28.0 |  |  |  |
| stencil | ifx-natural-inputonly | ok | 21 | 0.006563 | 0.007037 | 0.000937 |  |  |  |  | serial | 27.6 |  |  |  |
| stencil | ifx-expert-inputonly | ok | 21 | 0.005578 | 0.006036 | 0.001054 |  |  |  |  | serial | 27.6 |  |  |  |

> stencil serial-licensed-native: inference suppressed -- trend: blade-llvm-inputonly first-half vs second-half median moved 6.2%


## Controls

- compute (recur/gfortran-expert/serial-repro-native/full): n=137 median 0.01347 s, baseline 0.01342 s
- memory (saxpy/gfortran-expert/serial-repro-native/control): n=137 median 0.005438 s, baseline 0.005583 s
