# Blade vs Fortran -- bench session 2026-10-18T20:28:15

- host LAPTOP-350H6LDB | AMD Ryzen 7 5800HS with Radeon Graphics | 8C/16T | 15.41 GB | power ac | max processor state AC 100% | boost AC None | Power Scheme GUID: 64a64f24-65b9-4b56-befd-5ec1eaced9b3  (Silent)
- git 05f95277a0dbda3b2e9e98003bd5fb2c34f0e225 | Blade.dll 79af7edce32999db | GNU Fortran (Rev4, Built by MSYS2 project) 16.2.0 | Intel(R) Fortran Compiler for applications running on Intel(R) 64, Version 2026.1.0 Build 20260617 | clang version 22.1.8 (https://github.com/msys2/MINGW-packages d34feaaa1d0aff624c870910b6f2589aa40d20de)
- size `full` | duration 743.8 s | pins: pins-A.json, pins-B.json
- PRIMARY estimand: the complete timed region. ratio = median(arm) / median(Blade reference), round-block bootstrap 95% CI (10000 resamples); ratio > 1 = Blade faster. Verdicts (delta 0.1): win / detectable-win / equivalent / detectable-loss / loss / inconclusive; `suppressed` = the block failed a drift, trend or control check. Intervals are pointwise; no suite-wide claim. incremental = median(arm) - median(its own input-only twin), NOT kernel time. Reference: blade-llvm (serial), blade-cpp (mt8, or when the LLVM lane refused). Fresh-process, allocation-inclusive measurements.
- **FLAG**: antisym/mt8-licensed-native: inference suppressed: trend: ifx-natural first-half vs second-half median moved 5.2%
- **FLAG**: band/mt8-licensed-native: inference suppressed: trend: gfortran-natural first-half vs second-half median moved 7.5%; trend: ifx-natural first-half vs second-half median moved 8.5%
- **FLAG**: pairkernel/mt8-licensed-native: inference suppressed: trend: control compute moved 8.9% within the block; trend: control memory moved 9.4% within the block

## mt8-licensed-native

Blade reference per kernel: antisym=blade-cpp, band=None, comoment3=blade-cpp, gram=blade-cpp, pairkernel=blade-cpp, recur=None

| kernel | arm | status | n | min (s) | median (s) | IQR (s) | ratio vs ref [95% CI] | verdict | hypothesis | fold form | parallelism | peak WS (MB) | notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| antisym | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (6 reassoc flags) |  |  | LLVM lane has no OpenMP |
| antisym | blade-cpp | ok | 21 | 0.008427 | 0.00879 | 0.0003904 | ref |  |  | reordered (5 SIMD_REDUCTION) | openmp (2 construct(s)) | 26.4 |  |
| antisym | gfortran-natural | ok | 21 | 0.1071 | 0.1124 | 0.004892 | 12.788 [12.364, 13.065] | suppressed | WIN ~2x (half the cells, no diagonal) |  | openmp runtime linked | 23.9 |  |
| antisym | gfortran-expert | ok | 21 | 0.009198 | 0.0095 | 0.0004889 | 1.081 [1.054, 1.114] | suppressed | parity +-15% |  | openmp runtime linked | 33.2 |  |
| antisym | ifx-natural | ok | 21 | 0.058 | 0.07213 | 0.0045 | 8.206 [7.895, 8.561] | suppressed | WIN ~2x (half the cells, no diagonal) |  | openmp runtime linked | 25.4 |  |
| antisym | ifx-expert | ok | 21 | 0.009395 | 0.00982 | 0.0005084 | 1.117 [1.080, 1.150] | suppressed | parity +-15% |  | openmp runtime linked | 34.7 |  |

> antisym mt8-licensed-native: inference suppressed -- trend: ifx-natural first-half vs second-half median moved 5.2%

| band | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (2 reassoc flags) |  |  | LLVM lane has no OpenMP |
| band | blade-cpp | no-parallel-form | 0 | -- | -- | -- |  |  |  | reordered (1 SIMD_REDUCTION) |  |  | kernel.json no_parallel_form (spec: no licensed parallel spelling) |
| band | gfortran-natural | ok | 21 | 0.02694 | 0.04094 | 0.01219 |  |  | win 10x+ (the natural tier is the n^2 predicate nest, a straw man by construction) |  | openmp runtime linked | 5.8 |  |
| band | gfortran-expert | ok | 21 | 0.0009017 | 0.0009909 | 7.66e-05 |  |  | parity on blade-llvm (vectorized inner loop); LOSS on blade-cpp (inner loop scalar, G30) |  | openmp runtime linked | 5.8 |  |
| band | ifx-natural | ok | 21 | 0.01966 | 0.03902 | 0.01753 |  |  | win 10x+ (the natural tier is the n^2 predicate nest, a straw man by construction) |  | openmp runtime linked | 7.4 |  |
| band | ifx-expert | ok | 21 | 0.001619 | 0.001705 | 0.0001105 |  |  | parity on blade-llvm (vectorized inner loop); LOSS on blade-cpp (inner loop scalar, G30) |  | openmp runtime linked | 7.4 |  |

> band mt8-licensed-native: inference suppressed -- trend: gfortran-natural first-half vs second-half median moved 7.5%; trend: ifx-natural first-half vs second-half median moved 8.5%

| comoment3 | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (7 reassoc flags) |  |  | LLVM lane has no OpenMP |
| comoment3 | blade-cpp | ok | 21 | 0.003154 | 0.003311 | 0.000126 | ref |  |  | reordered (5 SIMD_REDUCTION) | openmp (2 construct(s)) | 13.1 |  |
| comoment3 | gfortran-natural | ok | 21 | 0.08393 | 0.09388 | 0.00298 | 28.356 [27.862, 29.147] | win | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | openmp runtime linked | 44.0 |  |
| comoment3 | gfortran-expert | ok | 21 | 0.004693 | 0.004839 | 0.0001191 | 1.462 [1.434, 1.502] | win | parity to 1.3x loss |  | openmp runtime linked | 14.5 |  |
| comoment3 | ifx-natural | ok | 21 | 0.1019 | 0.1173 | 0.007349 | 35.423 [33.982, 36.193] | win | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | openmp runtime linked | 45.5 |  |
| comoment3 | ifx-expert | ok | 21 | 0.003804 | 0.00403 | 0.0002569 | 1.217 [1.184, 1.271] | win | parity to 1.3x loss |  | openmp runtime linked | 15.8 |  |
| gram | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (4 reassoc flags) |  |  | LLVM lane has no OpenMP |
| gram | blade-cpp | ok | 21 | 0.01464 | 0.01558 | 0.0009294 | ref |  |  | reordered (3 SIMD_REDUCTION) | openmp (2 construct(s)) | 32.2 |  |
| gram | gfortran-natural | ok | 21 | 0.4691 | 0.5851 | 0.08765 | 37.560 [33.614, 38.944] | win | win 1.5-2x if matmul is a naive nest; parity/loss if a blocked runtime kernel |  | openmp runtime linked | 37.4 | matmul is unannotated: parallel preparation, serial kernel unless the runtime threads matmul (FORT records) |
| gram | gfortran-expert | ok | 21 | 0.0188 | 0.01987 | 0.001064 | 1.276 [1.221, 1.322] | win | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | openmp runtime linked | 52.2 |  |
| gram | ifx-natural | ok | 21 | 0.5897 | 0.614 | 0.02742 | 39.420 [37.907, 40.867] | win | win 1.5-2x if matmul is a naive nest; parity/loss if a blocked runtime kernel |  | openmp runtime linked | 58.3 | matmul is unannotated: parallel preparation, serial kernel unless the runtime threads matmul (FORT records) |
| gram | ifx-expert | ok | 21 | 0.01793 | 0.01906 | 0.001477 | 1.223 [1.176, 1.258] | win | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | openmp runtime linked | 53.8 |  |
| pairkernel | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (4 reassoc flags) |  |  | LLVM lane has no OpenMP |
| pairkernel | blade-cpp | ok | 21 | 0.007388 | 0.00767 | 0.0006852 | ref |  |  | reordered (2 SIMD_REDUCTION) | openmp (3 construct(s)) | 29.9 |  |
| pairkernel | gfortran-natural | ok | 21 | 0.01327 | 0.01519 | 0.002469 | 1.980 [1.765, 2.068] | suppressed | WIN ~2x (half the transcendentals) |  | openmp runtime linked | 53.4 |  |
| pairkernel | gfortran-expert | ok | 21 | 0.005725 | 0.00627 | 0.0005229 | 0.817 [0.772, 0.840] | suppressed | parity within 5% |  | openmp runtime linked | 29.5 |  |
| pairkernel | ifx-natural | ok | 21 | 0.01483 | 0.01678 | 0.001623 | 2.187 [2.038, 2.280] | suppressed | WIN ~2x (half the transcendentals) |  | openmp runtime linked | 54.9 |  |
| pairkernel | ifx-expert | ok | 21 | 0.00567 | 0.00603 | 0.0002025 | 0.786 [0.744, 0.804] | suppressed | parity within 5% |  | openmp runtime linked | 31.0 |  |

> pairkernel mt8-licensed-native: inference suppressed -- trend: control compute moved 8.9% within the block; trend: control memory moved 9.4% within the block

| recur | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (3 reassoc flags) |  |  | LLVM lane has no OpenMP |
| recur | blade-cpp | no-parallel-form | 0 | -- | -- | -- |  |  |  | reordered (3 SIMD_REDUCTION) |  |  | kernel.json no_parallel_form (spec: no licensed parallel spelling) |
| recur | gfortran-natural | ok | 21 | 0.06817 | 0.07187 | 0.002183 |  |  | parity |  | openmp runtime linked | 52.1 |  |
| recur | gfortran-expert | ok | 21 | 0.00653 | 0.006815 | 0.0003658 |  |  | LOSS 1.5-3x |  | openmp runtime linked | 51.5 |  |
| recur | ifx-natural | ok | 21 | 0.01404 | 0.01483 | 0.0008344 |  |  | parity |  | openmp runtime linked | 53.6 |  |
| recur | ifx-expert | ok | 21 | 0.006905 | 0.007486 | 0.0003362 |  |  | LOSS 1.5-3x |  | openmp runtime linked | 53.0 |  |

## Controls

- compute (recur/gfortran-expert/serial-repro-native/full): n=126 median 0.01273 s, baseline 0.01267 s
- memory (saxpy/gfortran-expert/serial-repro-native/control): n=126 median 0.005271 s, baseline 0.005294 s
