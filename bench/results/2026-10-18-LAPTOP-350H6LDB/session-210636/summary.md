# Blade vs Fortran -- bench session 2026-10-18T21:06:36

- host LAPTOP-350H6LDB | AMD Ryzen 7 5800HS with Radeon Graphics | 8C/16T | 15.41 GB | power ac | max processor state AC 100% | boost AC None | Power Scheme GUID: 64a64f24-65b9-4b56-befd-5ec1eaced9b3  (Silent)
- git 05f95277a0dbda3b2e9e98003bd5fb2c34f0e225 | Blade.dll 79af7edce32999db | GNU Fortran (Rev4, Built by MSYS2 project) 16.2.0 | Intel(R) Fortran Compiler for applications running on Intel(R) 64, Version 2026.1.0 Build 20260617 | clang version 22.1.8 (https://github.com/msys2/MINGW-packages d34feaaa1d0aff624c870910b6f2589aa40d20de)
- size `full` | duration 416.7 s | pins: pins-A.json, pins-B.json
- PRIMARY estimand: the complete timed region. ratio = median(arm) / median(Blade reference), round-block bootstrap 95% CI (10000 resamples); ratio > 1 = Blade faster. Verdicts (delta 0.1): win / detectable-win / equivalent / detectable-loss / loss / inconclusive; `suppressed` = the block failed a drift, trend or control check. Intervals are pointwise; no suite-wide claim. incremental = median(arm) - median(its own input-only twin), NOT kernel time. Reference: blade-llvm (serial), blade-cpp (mt8, or when the LLVM lane refused). Fresh-process, allocation-inclusive measurements.
- **FLAG**: antisym/serial-fastmath-native: inference suppressed: trend: blade-llvm first-half vs second-half median moved 10.3%; trend: gfortran-natural first-half vs second-half median moved 5.7%; trend: gfortran-expert first-half vs second-half median moved 5.8%; trend: ifx-natural first-half vs second-half median moved 13.9%; trend: ifx-expert first-half vs second-half median moved 6.8%; trend: control memory moved 6.3% within the block
- **FLAG**: band/serial-fastmath-native: inference suppressed: trend: gfortran-natural first-half vs second-half median moved 10.0%; trend: ifx-natural first-half vs second-half median moved 9.5%; trend: ifx-expert first-half vs second-half median moved 5.7%; control compute median 0.01724s moved +26.5% vs the session baseline 0.01363s; trend: control compute moved 26.7% within the block; control memory median 0.006585s moved +23.9% vs the session baseline 0.005316s; trend: control memory moved 24.2% within the block
- **FLAG**: gram/serial-fastmath-native: inference suppressed: trend: ifx-natural first-half vs second-half median moved 7.0%

## serial-fastmath-native

Blade reference per kernel: antisym=blade-llvm, band=blade-llvm, comoment3=blade-llvm, gram=blade-llvm

| kernel | arm | status | n | min (s) | median (s) | IQR (s) | ratio vs ref [95% CI] | verdict | hypothesis | fold form | parallelism | peak WS (MB) | notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| antisym | blade-llvm | ok | 11 | 0.07194 | 0.08131 | 0.01237 | ref |  |  | reordered (6 reassoc flags) | serial | 17.5 |  |
| antisym | blade-cpp | ok | 11 | 0.02802 | 0.02948 | 0.001208 | 0.363 [0.335, 0.406] | suppressed |  | reordered (5 SIMD_REDUCTION) | serial | 25.7 |  |
| antisym | gfortran-natural | ok | 11 | 0.1304 | 0.1413 | 0.03714 | 1.738 [1.658, 2.369] | suppressed | WIN ~2x (half the cells, no diagonal) |  | serial | 23.5 |  |
| antisym | gfortran-expert | ok | 11 | 0.03026 | 0.03275 | 0.00376 | 0.403 [0.385, 0.448] | suppressed | parity +-15% |  | serial | 32.7 |  |
| antisym | ifx-natural | ok | 11 | 0.08152 | 0.09122 | 0.01402 | 1.122 [1.046, 1.305] | suppressed | WIN ~2x (half the cells, no diagonal) |  | serial | 23.0 |  |
| antisym | ifx-expert | ok | 11 | 0.02903 | 0.03177 | 0.004306 | 0.391 [0.361, 0.465] | suppressed | parity +-15% |  | serial | 32.3 |  |

> antisym serial-fastmath-native: inference suppressed -- trend: blade-llvm first-half vs second-half median moved 10.3%; trend: gfortran-natural first-half vs second-half median moved 5.7%; trend: gfortran-expert first-half vs second-half median moved 5.8%; trend: ifx-natural first-half vs second-half median moved 13.9%; trend: ifx-expert first-half vs second-half median moved 6.8%; trend: control memory moved 6.3% within the block

| band | blade-llvm | ok | 21 | 0.003298 | 0.0034 | 0.0003313 | ref |  |  | reordered (2 reassoc flags) | serial | 4.8 |  |
| band | blade-cpp | ok | 21 | 0.009265 | 0.009677 | 0.001122 | 2.846 [2.664, 2.940] | suppressed |  | reordered (1 SIMD_REDUCTION) | serial | 5.5 |  |
| band | gfortran-natural | ok | 21 | 0.3023 | 0.3204 | 0.05957 | 94.237 [89.746, 102.118] | suppressed | win 10x+ (the natural tier is the n^2 predicate nest, a straw man by construction) |  | serial | 5.4 |  |
| band | gfortran-expert | ok | 21 | 0.001539 | 0.001722 | 0.0002656 | 0.506 [0.468, 0.543] | suppressed | parity on blade-llvm (vectorized inner loop); LOSS on blade-cpp (inner loop scalar, G30) |  | serial | 5.4 |  |
| band | ifx-natural | ok | 21 | 0.3278 | 0.3497 | 0.05262 | 102.859 [97.583, 110.771] | suppressed | win 10x+ (the natural tier is the n^2 predicate nest, a straw man by construction) |  | serial | 5.1 |  |
| band | ifx-expert | ok | 21 | 0.001311 | 0.001364 | 0.0001259 | 0.401 [0.380, 0.414] | suppressed | parity on blade-llvm (vectorized inner loop); LOSS on blade-cpp (inner loop scalar, G30) |  | serial | 5.0 |  |

> band serial-fastmath-native: inference suppressed -- trend: gfortran-natural first-half vs second-half median moved 10.0%; trend: ifx-natural first-half vs second-half median moved 9.5%; trend: ifx-expert first-half vs second-half median moved 5.7%; control compute median 0.01724s moved +26.5% vs the session baseline 0.01363s; trend: control compute moved 26.7% within the block; control memory median 0.006585s moved +23.9% vs the session baseline 0.005316s; trend: control memory moved 24.2% within the block

| comoment3 | blade-llvm | ok | 21 | 0.02493 | 0.02504 | 7.56e-05 | ref |  |  | reordered (7 reassoc flags) | serial | 11.3 |  |
| comoment3 | blade-cpp | ok | 21 | 0.009384 | 0.009595 | 0.0001531 | 0.383 [0.381, 0.385] | loss |  | reordered (6 SIMD_REDUCTION) | serial | 12.7 |  |
| comoment3 | gfortran-natural | ok | 21 | 0.1439 | 0.1446 | 0.001051 | 5.773 [5.761, 5.801] | win | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | serial | 43.5 |  |
| comoment3 | gfortran-expert | ok | 21 | 0.01103 | 0.01121 | 0.000305 | 0.447 [0.444, 0.456] | loss | parity to 1.3x loss |  | serial | 12.4 |  |
| comoment3 | ifx-natural | ok | 21 | 0.07813 | 0.08697 | 0.001037 | 3.473 [3.449, 3.487] | win | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | serial | 43.1 |  |
| comoment3 | ifx-expert | ok | 21 | 0.01099 | 0.01124 | 0.0003533 | 0.449 [0.443, 0.456] | loss | parity to 1.3x loss |  | serial | 11.9 |  |
| gram | blade-llvm | ok | 11 | 0.05545 | 0.05663 | 0.001333 | ref |  |  | reordered (4 reassoc flags) | serial | 30.7 |  |
| gram | blade-cpp | ok | 11 | 0.0581 | 0.05956 | 0.001755 | 1.052 [1.030, 1.076] | suppressed |  | reordered (4 SIMD_REDUCTION) | serial | 31.5 |  |
| gram | gfortran-natural | ok | 11 | 0.5301 | 0.5507 | 0.0824 | 9.725 [9.501, 11.419] | suppressed | win 1.5-2x if matmul is a naive nest; parity/loss if a blocked runtime kernel |  | serial | 36.9 |  |
| gram | gfortran-expert | ok | 11 | 0.07572 | 0.0767 | 0.000363 | 1.354 [1.341, 1.377] | suppressed | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | serial | 51.7 |  |
| gram | ifx-natural | ok | 11 | 0.1985 | 0.1999 | 0.01634 | 3.529 [3.499, 3.856] | suppressed | win 1.5-2x if matmul is a naive nest; parity/loss if a blocked runtime kernel |  | serial | 56.0 |  |
| gram | ifx-expert | ok | 11 | 0.06915 | 0.06996 | 0.00129 | 1.235 [1.218, 1.261] | suppressed | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | serial | 51.3 |  |

> gram serial-fastmath-native: inference suppressed -- trend: ifx-natural first-half vs second-half median moved 7.0%


## Controls

- compute (recur/gfortran-expert/serial-repro-native/full): n=64 median 0.01298 s, baseline 0.01363 s
- memory (saxpy/gfortran-expert/serial-repro-native/control): n=64 median 0.005576 s, baseline 0.005316 s
