# Blade vs Fortran -- bench session 2026-10-19T14:25:02

- host LAPTOP-350H6LDB | AMD Ryzen 7 5800HS with Radeon Graphics | 8C/16T | 15.41 GB | power ac | max processor state AC 100% | boost AC None | Power Scheme GUID: 64a64f24-65b9-4b56-befd-5ec1eaced9b3  (Silent)
- git 39a8a869ff410c79edbe599a98257d4012241702 | Blade.dll bb52a4c2a008d198 | GNU Fortran (Rev4, Built by MSYS2 project) 16.2.0 | Intel(R) Fortran Compiler for applications running on Intel(R) 64, Version 2026.1.0 Build 20260617 | clang version 22.1.8 (https://github.com/msys2/MINGW-packages d34feaaa1d0aff624c870910b6f2589aa40d20de)
- size `full` | duration 1002.0 s | pins: pins-A.json, pins-B.json
- PRIMARY estimand: the complete timed region. ratio = median(arm) / median(Blade reference), round-block bootstrap 95% CI (10000 resamples); ratio > 1 = Blade faster. Verdicts (delta 0.1): win / detectable-win / equivalent / detectable-loss / loss / inconclusive; `suppressed` = the block failed a drift, trend or control check. Intervals are pointwise; no suite-wide claim. incremental = median(arm) - median(its own input-only twin), NOT kernel time. Reference: blade-llvm (serial), blade-cpp (mt8, or when the LLVM lane refused). Fresh-process, allocation-inclusive measurements.
- **FLAG**: band/serial-repro-native: inference suppressed: trend: control memory moved 8.2% within the block
- **FLAG**: recur/serial-repro-native: inference suppressed: trend: control memory moved 5.5% within the block
- **FLAG**: stencil/serial-repro-native: inference suppressed: trend: ifx-natural-inputonly first-half vs second-half median moved 8.0%; trend: control memory moved 9.7% within the block

## serial-repro-native

Blade reference per kernel: antisym=blade-llvm, band=blade-llvm, comoment3=blade-llvm, gram=blade-llvm, pairkernel=blade-llvm, recur=blade-llvm, stencil=blade-llvm

| kernel | arm | status | n | min (s) | median (s) | IQR (s) | ratio vs ref [95% CI] | verdict | hypothesis | fold form | parallelism | peak WS (MB) | incremental (s) [CI] | incremental ratio [CI] | notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| antisym | blade-llvm | ok | 21 | 0.02994 | 0.03112 | 0.0009682 | ref |  |  | in-order | serial | 25.0 |  |  |  |
| antisym | blade-cpp | ok | 21 | 0.0307 | 0.03162 | 0.001485 | 1.016 [0.998, 1.048] | equivalent |  | in-order | serial | 25.7 |  |  |  |
| antisym | gfortran-natural | ok | 21 | 0.7745 | 0.7797 | 0.03117 | 25.057 [24.827, 25.416] | win | WIN ~2x (half the cells, no diagonal) |  | serial | 23.5 |  |  |  |
| antisym | gfortran-expert | ok | 21 | 0.03088 | 0.0325 | 0.001977 | 1.045 [1.020, 1.079] | detectable-win | parity +-15% |  | serial | 32.7 |  |  |  |
| antisym | ifx-natural | ok | 21 | 0.3339 | 0.337 | 0.00539 | 10.831 [10.681, 10.952] | win | WIN ~2x (half the cells, no diagonal) |  | serial | 23.0 |  |  |  |
| antisym | ifx-expert | ok | 21 | 0.02996 | 0.03128 | 0.003198 | 1.005 [0.989, 1.056] | equivalent | parity +-15% |  | serial | 32.3 |  |  |  |
| band | blade-llvm | ok | 21 | 0.003662 | 0.003685 | 1.95e-05 | ref |  |  | in-order | serial | 4.8 |  |  |  |
| band | blade-cpp | ok | 21 | 0.003947 | 0.003959 | 2.67e-05 | 1.074 [1.072, 1.081] | suppressed |  | in-order | serial | 5.5 |  |  |  |
| band | gfortran-natural | ok | 21 | 0.207 | 0.208 | 0.002052 | 56.453 [56.341, 56.909] | suppressed | win 10x+ (the natural tier is the n^2 predicate nest, a straw man by construction) |  | serial | 5.4 |  |  |  |
| band | gfortran-expert | ok | 21 | 0.003104 | 0.003111 | 8.9e-06 | 0.844 [0.843, 0.847] | suppressed | parity in both lanes: licensed folds walk the band by diagonal (k = i - j outer) as the licensed expert does, with phi hoisted per diagonal by the back end (G33); repro keeps the lex order with the terms vectorized and summed in order (G30) |  | serial | 5.4 |  |  |  |
| band | ifx-natural | ok | 21 | 0.3499 | 0.3513 | 0.00528 | 95.331 [95.066, 96.441] | suppressed | win 10x+ (the natural tier is the n^2 predicate nest, a straw man by construction) |  | serial | 5.1 |  |  |  |
| band | ifx-expert | ok | 21 | 0.003697 | 0.003716 | 1.9e-05 | 1.008 [1.007, 1.013] | suppressed | parity in both lanes: licensed folds walk the band by diagonal (k = i - j outer) as the licensed expert does, with phi hoisted per diagonal by the back end (G33); repro keeps the lex order with the terms vectorized and summed in order (G30) |  | serial | 5.0 |  |  |  |

> band serial-repro-native: inference suppressed -- trend: control memory moved 8.2% within the block

| comoment3 | blade-llvm | ok | 21 | 0.01485 | 0.0153 | 0.0005597 | ref |  |  | in-order | serial | 11.6 |  |  |  |
| comoment3 | blade-cpp | ok | 21 | 0.01517 | 0.01554 | 0.0005863 | 1.016 [0.992, 1.035] | equivalent |  | in-order | serial | 12.7 |  |  |  |
| comoment3 | gfortran-natural | ok | 21 | 0.6006 | 0.6025 | 0.001795 | 39.386 [38.465, 39.874] | win | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | serial | 43.5 |  |  |  |
| comoment3 | gfortran-expert | ok | 21 | 0.01142 | 0.01182 | 0.0003078 | 0.773 [0.754, 0.783] | loss | parity to 1.3x loss |  | serial | 12.4 |  |  |  |
| comoment3 | ifx-natural | ok | 21 | 0.7204 | 0.723 | 0.002333 | 47.263 [46.184, 47.874] | win | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | serial | 43.1 |  |  |  |
| comoment3 | ifx-expert | ok | 21 | 0.01156 | 0.01193 | 0.0003099 | 0.780 [0.765, 0.792] | loss | parity to 1.3x loss |  | serial | 11.9 |  |  |  |
| gram | blade-llvm | ok | 11 | 0.0609 | 0.06289 | 0.00285 | ref |  |  | in-order | serial | 30.8 |  |  |  |
| gram | blade-cpp | ok | 11 | 0.06434 | 0.06538 | 0.001613 | 1.040 [1.001, 1.071] | detectable-win |  | in-order | serial | 31.5 |  |  |  |
| gram | gfortran-natural | ok | 11 | 0.5865 | 0.7553 | 0.02649 | 12.010 [11.478, 12.418] | win | win 1.5-2x if matmul is a naive nest; parity/loss if a blocked runtime kernel |  | serial | 36.9 |  |  |  |
| gram | gfortran-expert | ok | 11 | 0.0787 | 0.07977 | 0.001495 | 1.268 [1.221, 1.310] | win | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | serial | 51.7 |  |  |  |
| gram | ifx-natural | ok | 11 | 0.5735 | 0.5782 | 0.004522 | 9.194 [8.776, 9.369] | win | win 1.5-2x if matmul is a naive nest; parity/loss if a blocked runtime kernel |  | serial | 56.0 |  |  |  |
| gram | ifx-expert | ok | 11 | 0.07193 | 0.07288 | 0.0008825 | 1.159 [1.116, 1.186] | win | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | serial | 51.3 |  |  |  |
| pairkernel | blade-llvm | ok | 21 | 0.03744 | 0.03822 | 0.0006908 | ref |  |  | in-order | serial | 28.6 |  |  |  |
| pairkernel | blade-cpp | ok | 21 | 0.03781 | 0.03864 | 0.0006501 | 1.011 [1.003, 1.025] | detectable-win |  | in-order | serial | 29.4 |  |  |  |
| pairkernel | gfortran-natural | ok | 21 | 0.06083 | 0.06129 | 0.000731 | 1.604 [1.590, 1.621] | win | WIN ~2x (half the transcendentals) |  | serial | 53.0 |  |  |  |
| pairkernel | gfortran-expert | ok | 21 | 0.03432 | 0.0345 | 0.0001285 | 0.903 [0.898, 0.913] | loss | parity within 5% |  | serial | 29.1 |  |  |  |
| pairkernel | ifx-natural | ok | 21 | 0.06431 | 0.06497 | 0.0005379 | 1.700 [1.693, 1.717] | win | WIN ~2x (half the transcendentals) |  | serial | 52.5 |  |  |  |
| pairkernel | ifx-expert | ok | 21 | 0.03191 | 0.03211 | 0.0002222 | 0.840 [0.833, 0.849] | loss | parity within 5% |  | serial | 28.6 |  |  |  |
| recur | blade-llvm | ok | 21 | 0.01307 | 0.01383 | 0.0002424 | ref |  |  | in-order | serial | 50.7 |  |  |  |
| recur | blade-cpp | ok | 21 | 0.01517 | 0.01574 | 0.0005121 | 1.138 [1.120, 1.162] | suppressed |  | in-order | serial | 51.2 |  |  |  |
| recur | gfortran-natural | ok | 21 | 0.01903 | 0.01957 | 0.0003676 | 1.415 [1.401, 1.436] | suppressed | parity |  | serial | 51.7 |  |  |  |
| recur | gfortran-expert | ok | 21 | 0.01297 | 0.01363 | 0.001083 | 0.985 [0.964, 1.003] | suppressed | LOSS 1.5-3x |  | serial | 51.1 |  |  |  |
| recur | ifx-natural | ok | 21 | 0.01585 | 0.01672 | 0.0004533 | 1.208 [1.194, 1.225] | suppressed | parity |  | serial | 51.2 |  |  |  |
| recur | ifx-expert | ok | 21 | 0.01336 | 0.01407 | 0.0004187 | 1.017 [1.008, 1.033] | suppressed | LOSS 1.5-3x |  | serial | 50.6 |  |  |  |

> recur serial-repro-native: inference suppressed -- trend: control memory moved 5.5% within the block

| stencil | blade-llvm | ok | 21 | 0.01362 | 0.01428 | 0.0004974 | ref |  |  | in-order | serial | 50.4 | 0.007269 [0.007014, 0.007507] | ref |  |
| stencil | blade-cpp | ok | 21 | 0.0203 | 0.02102 | 0.000571 | 1.472 [1.447, 1.496] | suppressed |  | in-order | serial | 50.9 | 0.007948 [0.007555, 0.008272] | 1.093 [1.027, 1.161] |  |
| stencil | gfortran-natural | ok | 21 | 0.01752 | 0.01772 | 0.0002916 | 1.241 [1.229, 1.256] | suppressed | parity (bandwidth-bound) |  | serial | 50.9 | 0.007303 [0.007057, 0.007579] | 1.005 [0.950, 1.057] |  |
| stencil | gfortran-expert | ok | 21 | 0.01709 | 0.01748 | 0.0002449 | 1.224 [1.210, 1.238] | suppressed | parity |  | serial | 50.9 | 0.006913 [0.006737, 0.007144] | 0.951 [0.912, 0.999] |  |
| stencil | ifx-natural | ok | 21 | 0.01919 | 0.01973 | 0.0002269 | 1.381 [1.360, 1.400] | suppressed | parity (bandwidth-bound) |  | serial | 50.5 | 0.01238 [0.01191, 0.01267] | 1.703 [1.617, 1.786] |  |
| stencil | ifx-expert | ok | 21 | 0.01351 | 0.01429 | 0.0007247 | 1.001 [0.985, 1.023] | suppressed | parity |  | serial | 50.5 | 0.007024 [0.006693, 0.007389] | 0.966 [0.911, 1.026] |  |
| stencil | blade-llvm-inputonly | ok | 21 | 0.006546 | 0.007014 | 0.000561 |  |  |  | in-order | serial | 27.5 |  |  |  |
| stencil | blade-cpp-inputonly | ok | 21 | 0.01281 | 0.01307 | 0.0003231 |  |  |  | in-order | serial | 28.3 |  |  |  |
| stencil | gfortran-natural-inputonly | ok | 21 | 0.01013 | 0.01042 | 0.0004007 |  |  |  |  | serial | 28.1 |  |  |  |
| stencil | gfortran-expert-inputonly | ok | 21 | 0.01012 | 0.01057 | 0.0004897 |  |  |  |  | serial | 28.0 |  |  |  |
| stencil | ifx-natural-inputonly | ok | 21 | 0.006717 | 0.007345 | 0.0008874 |  |  |  |  | serial | 27.6 |  |  |  |
| stencil | ifx-expert-inputonly | ok | 21 | 0.006625 | 0.00727 | 0.0005651 |  |  |  |  | serial | 27.6 |  |  |  |

> stencil serial-repro-native: inference suppressed -- trend: ifx-natural-inputonly first-half vs second-half median moved 8.0%; trend: control memory moved 9.7% within the block


## Controls

- compute (recur/gfortran-expert/serial-repro-native/full): n=137 median 0.01369 s, baseline 0.01367 s
- memory (saxpy/gfortran-expert/serial-repro-native/control): n=137 median 0.005552 s, baseline 0.00564 s
