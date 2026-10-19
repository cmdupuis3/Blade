# Blade vs Fortran -- bench session 2026-10-18T20:15:58

- host LAPTOP-350H6LDB | AMD Ryzen 7 5800HS with Radeon Graphics | 8C/16T | 15.41 GB | power ac | max processor state AC 100% | boost AC None | Power Scheme GUID: 64a64f24-65b9-4b56-befd-5ec1eaced9b3  (Silent)
- git 05f95277a0dbda3b2e9e98003bd5fb2c34f0e225 | Blade.dll 79af7edce32999db | GNU Fortran (Rev4, Built by MSYS2 project) 16.2.0 | Intel(R) Fortran Compiler for applications running on Intel(R) 64, Version 2026.1.0 Build 20260617 | clang version 22.1.8 (https://github.com/msys2/MINGW-packages d34feaaa1d0aff624c870910b6f2589aa40d20de)
- size `full` | duration 674.2 s | pins: pins-A.json, pins-B.json
- PRIMARY estimand: the complete timed region. ratio = median(arm) / median(Blade reference), round-block bootstrap 95% CI (10000 resamples); ratio > 1 = Blade faster. Verdicts (delta 0.1): win / detectable-win / equivalent / detectable-loss / loss / inconclusive; `suppressed` = the block failed a drift, trend or control check. Intervals are pointwise; no suite-wide claim. incremental = median(arm) - median(its own input-only twin), NOT kernel time. Reference: blade-llvm (serial), blade-cpp (mt8, or when the LLVM lane refused). Fresh-process, allocation-inclusive measurements.
- **FLAG**: comoment3/serial-licensed-native: inference suppressed: trend: control compute moved 6.4% within the block
- **FLAG**: recur/serial-licensed-native: inference suppressed: trend: blade-llvm first-half vs second-half median moved 6.4%; trend: blade-cpp first-half vs second-half median moved 11.0%; trend: gfortran-expert first-half vs second-half median moved 12.8%; trend: ifx-natural first-half vs second-half median moved 8.5%; control compute median 0.01365s moved +7.6% vs the session baseline 0.01269s; trend: control compute moved 17.1% within the block; trend: control memory moved 13.2% within the block

## serial-licensed-native

Blade reference per kernel: antisym=blade-llvm, band=blade-llvm, comoment3=blade-llvm, gram=blade-llvm, pairkernel=blade-llvm, recur=blade-llvm

| kernel | arm | status | n | min (s) | median (s) | IQR (s) | ratio vs ref [95% CI] | verdict | hypothesis | fold form | parallelism | peak WS (MB) | notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| antisym | blade-llvm | ok | 11 | 0.07111 | 0.07144 | 0.0004907 | ref |  |  | reordered (6 reassoc flags) | serial | 17.5 |  |
| antisym | blade-cpp | ok | 11 | 0.02776 | 0.02793 | 0.0003007 | 0.391 [0.388, 0.394] | loss |  | reordered (5 SIMD_REDUCTION) | serial | 25.7 |  |
| antisym | gfortran-natural | ok | 11 | 0.7644 | 0.7668 | 0.001064 | 10.733 [10.648, 10.754] | win | WIN ~2x (half the cells, no diagonal) |  | serial | 23.5 |  |
| antisym | gfortran-expert | ok | 11 | 0.02972 | 0.03028 | 0.0002601 | 0.424 [0.420, 0.427] | loss | parity +-15% |  | serial | 32.7 |  |
| antisym | ifx-natural | ok | 11 | 0.3282 | 0.3291 | 0.001868 | 4.607 [4.570, 4.638] | win | WIN ~2x (half the cells, no diagonal) |  | serial | 23.0 |  |
| antisym | ifx-expert | ok | 11 | 0.02869 | 0.02899 | 0.0004105 | 0.406 [0.400, 0.411] | loss | parity +-15% |  | serial | 32.3 |  |
| band | blade-llvm | ok | 21 | 0.003288 | 0.003318 | 6.4e-06 | ref |  |  | reordered (2 reassoc flags) | serial | 4.8 |  |
| band | blade-cpp | ok | 21 | 0.009261 | 0.009276 | 2.25e-05 | 2.796 [2.792, 2.799] | win |  | reordered (1 SIMD_REDUCTION) | serial | 5.5 |  |
| band | gfortran-natural | ok | 21 | 0.2064 | 0.207 | 0.0002496 | 62.375 [62.295, 62.431] | win | win 10x+ (the natural tier is the n^2 predicate nest, a straw man by construction) |  | serial | 5.4 |  |
| band | gfortran-expert | ok | 21 | 0.0008113 | 0.0008135 | 6.4e-06 | 0.245 [0.245, 0.246] | loss | parity on blade-llvm (vectorized inner loop); LOSS on blade-cpp (inner loop scalar, G30) |  | serial | 5.4 |  |
| band | ifx-natural | ok | 21 | 0.3493 | 0.3502 | 0.0009754 | 105.554 [105.362, 105.724] | win | win 10x+ (the natural tier is the n^2 predicate nest, a straw man by construction) |  | serial | 5.1 |  |
| band | ifx-expert | ok | 21 | 0.001035 | 0.001042 | 1e-06 | 0.314 [0.314, 0.314] | loss | parity on blade-llvm (vectorized inner loop); LOSS on blade-cpp (inner loop scalar, G30) |  | serial | 5.0 |  |
| comoment3 | blade-llvm | ok | 21 | 0.02487 | 0.02504 | 0.0003449 | ref |  |  | reordered (7 reassoc flags) | serial | 11.3 |  |
| comoment3 | blade-cpp | ok | 21 | 0.009382 | 0.00954 | 0.0004216 | 0.381 [0.378, 0.392] | suppressed |  | reordered (6 SIMD_REDUCTION) | serial | 12.7 |  |
| comoment3 | gfortran-natural | ok | 21 | 0.5997 | 0.6022 | 0.003934 | 24.052 [23.940, 24.136] | suppressed | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | serial | 43.5 |  |
| comoment3 | gfortran-expert | ok | 21 | 0.01095 | 0.01126 | 0.0002414 | 0.450 [0.445, 0.454] | suppressed | parity to 1.3x loss |  | serial | 12.4 |  |
| comoment3 | ifx-natural | ok | 21 | 0.7173 | 0.7204 | 0.004387 | 28.773 [28.665, 28.873] | suppressed | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | serial | 43.1 |  |
| comoment3 | ifx-expert | ok | 21 | 0.01125 | 0.01135 | 0.0001768 | 0.453 [0.451, 0.457] | suppressed | parity to 1.3x loss |  | serial | 11.9 |  |

> comoment3 serial-licensed-native: inference suppressed -- trend: control compute moved 6.4% within the block

| gram | blade-llvm | ok | 11 | 0.05548 | 0.0561 | 0.0006087 | ref |  |  | reordered (4 reassoc flags) | serial | 30.7 |  |
| gram | blade-cpp | ok | 11 | 0.05756 | 0.05796 | 0.0007279 | 1.033 [1.026, 1.048] | detectable-win |  | reordered (4 SIMD_REDUCTION) | serial | 31.5 |  |
| gram | gfortran-natural | ok | 11 | 0.5031 | 0.5552 | 0.03088 | 9.896 [9.700, 10.563] | win | win 1.5-2x if matmul is a naive nest; parity/loss if a blocked runtime kernel |  | serial | 36.9 |  |
| gram | gfortran-expert | ok | 11 | 0.07575 | 0.07675 | 0.0009007 | 1.368 [1.355, 1.380] | win | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | serial | 51.7 |  |
| gram | ifx-natural | ok | 11 | 0.5677 | 0.5707 | 0.002995 | 10.172 [10.092, 10.215] | win | win 1.5-2x if matmul is a naive nest; parity/loss if a blocked runtime kernel |  | serial | 56.0 |  |
| gram | ifx-expert | ok | 11 | 0.0687 | 0.06936 | 0.0006258 | 1.236 [1.227, 1.243] | win | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | serial | 51.3 |  |
| pairkernel | blade-llvm | ok | 21 | 0.0286 | 0.02894 | 0.000126 | ref |  |  | reordered (4 reassoc flags) | serial | 28.6 |  |
| pairkernel | blade-cpp | ok | 21 | 0.02895 | 0.02914 | 0.0001955 | 1.007 [1.004, 1.012] | detectable-win |  | reordered (3 SIMD_REDUCTION) | serial | 29.4 |  |
| pairkernel | gfortran-natural | ok | 21 | 0.05985 | 0.06025 | 0.0004755 | 2.082 [2.076, 2.093] | win | WIN ~2x (half the transcendentals) |  | serial | 53.0 |  |
| pairkernel | gfortran-expert | ok | 21 | 0.03226 | 0.03244 | 0.0001787 | 1.121 [1.118, 1.125] | win | parity within 5% |  | serial | 29.1 |  |
| pairkernel | ifx-natural | ok | 21 | 0.06312 | 0.06454 | 0.002505 | 2.230 [2.202, 2.266] | win | WIN ~2x (half the transcendentals) |  | serial | 52.5 |  |
| pairkernel | ifx-expert | ok | 21 | 0.02981 | 0.03018 | 0.0003395 | 1.043 [1.036, 1.047] | detectable-win | parity within 5% |  | serial | 28.6 |  |
| recur | blade-llvm | ok | 21 | 0.0194 | 0.02043 | 0.001897 | ref |  |  | reordered (3 reassoc flags) | serial | 51.5 |  |
| recur | blade-cpp | ok | 21 | 0.02053 | 0.02249 | 0.003197 | 1.101 [1.030, 1.156] | suppressed |  | reordered (3 SIMD_REDUCTION) | serial | 52.5 |  |
| recur | gfortran-natural | ok | 21 | 0.01815 | 0.01898 | 0.001122 | 0.929 [0.886, 0.952] | suppressed | parity |  | serial | 51.7 |  |
| recur | gfortran-expert | ok | 21 | 0.009206 | 0.01007 | 0.001655 | 0.493 [0.457, 0.523] | suppressed | LOSS 1.5-3x |  | serial | 51.0 |  |
| recur | ifx-natural | ok | 21 | 0.01468 | 0.01574 | 0.001769 | 0.770 [0.714, 0.788] | suppressed | parity |  | serial | 51.2 |  |
| recur | ifx-expert | ok | 21 | 0.009406 | 0.009995 | 0.0007721 | 0.489 [0.466, 0.500] | suppressed | LOSS 1.5-3x |  | serial | 50.6 |  |

> recur serial-licensed-native: inference suppressed -- trend: blade-llvm first-half vs second-half median moved 6.4%; trend: blade-cpp first-half vs second-half median moved 11.0%; trend: gfortran-expert first-half vs second-half median moved 12.8%; trend: ifx-natural first-half vs second-half median moved 8.5%; control compute median 0.01365s moved +7.6% vs the session baseline 0.01269s; trend: control compute moved 17.1% within the block; trend: control memory moved 13.2% within the block


## Controls

- compute (recur/gfortran-expert/serial-repro-native/full): n=106 median 0.01274 s, baseline 0.01269 s
- memory (saxpy/gfortran-expert/serial-repro-native/control): n=106 median 0.005267 s, baseline 0.00525 s
