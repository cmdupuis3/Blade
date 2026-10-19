# Blade vs Fortran -- bench session 2026-10-18T21:33:23

- host LAPTOP-350H6LDB | AMD Ryzen 7 5800HS with Radeon Graphics | 8C/16T | 15.41 GB | power ac | max processor state AC 100% | boost AC None | Power Scheme GUID: 64a64f24-65b9-4b56-befd-5ec1eaced9b3  (Silent)
- git 05f95277a0dbda3b2e9e98003bd5fb2c34f0e225 | Blade.dll 79af7edce32999db | GNU Fortran (Rev4, Built by MSYS2 project) 16.2.0 | Intel(R) Fortran Compiler for applications running on Intel(R) 64, Version 2026.1.0 Build 20260617 | clang version 22.1.8 (https://github.com/msys2/MINGW-packages d34feaaa1d0aff624c870910b6f2589aa40d20de)
- size `full` | duration 517.5 s | pins: pins-A.json, pins-B.json
- PRIMARY estimand: the complete timed region. ratio = median(arm) / median(Blade reference), round-block bootstrap 95% CI (10000 resamples); ratio > 1 = Blade faster. Verdicts (delta 0.1): win / detectable-win / equivalent / detectable-loss / loss / inconclusive; `suppressed` = the block failed a drift, trend or control check. Intervals are pointwise; no suite-wide claim. incremental = median(arm) - median(its own input-only twin), NOT kernel time. Reference: blade-llvm (serial), blade-cpp (mt8, or when the LLVM lane refused). Fresh-process, allocation-inclusive measurements.
- **FLAG**: antisym/mt8-fastmath-native: inference suppressed: trend: blade-cpp first-half vs second-half median moved 5.5%; trend: ifx-natural first-half vs second-half median moved 15.9%
- **FLAG**: band/mt8-fastmath-native: inference suppressed: trend: gfortran-natural first-half vs second-half median moved 5.6%; trend: gfortran-expert first-half vs second-half median moved 9.6%
- **FLAG**: comoment3/mt8-fastmath-native: inference suppressed: trend: gfortran-natural first-half vs second-half median moved 33.5%; trend: ifx-natural first-half vs second-half median moved 18.1%
- **FLAG**: gram/mt8-fastmath-native: inference suppressed: trend: blade-cpp first-half vs second-half median moved 7.1%; trend: gfortran-natural first-half vs second-half median moved 8.5%; trend: gfortran-expert first-half vs second-half median moved 6.5%

## mt8-fastmath-native

Blade reference per kernel: antisym=blade-cpp, band=None, comoment3=blade-cpp, gram=blade-cpp

| kernel | arm | status | n | min (s) | median (s) | IQR (s) | ratio vs ref [95% CI] | verdict | hypothesis | fold form | parallelism | peak WS (MB) | notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| antisym | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (6 reassoc flags) |  |  | LLVM lane has no OpenMP |
| antisym | blade-cpp | ok | 21 | 0.008445 | 0.009009 | 0.0008895 | ref |  |  | reordered (5 SIMD_REDUCTION) | openmp (2 construct(s)) | 26.4 |  |
| antisym | gfortran-natural | ok | 21 | 0.03093 | 0.03847 | 0.01023 | 4.270 [4.001, 5.101] | suppressed | WIN ~2x (half the cells, no diagonal) |  | openmp runtime linked | 23.9 |  |
| antisym | gfortran-expert | ok | 21 | 0.009431 | 0.01051 | 0.0008185 | 1.167 [1.109, 1.227] | suppressed | parity +-15% |  | openmp runtime linked | 33.2 |  |
| antisym | ifx-natural | ok | 21 | 0.0169 | 0.02431 | 0.004716 | 2.698 [2.416, 2.885] | suppressed | WIN ~2x (half the cells, no diagonal) |  | openmp runtime linked | 25.4 |  |
| antisym | ifx-expert | ok | 21 | 0.009794 | 0.01036 | 0.0007354 | 1.150 [1.106, 1.211] | suppressed | parity +-15% |  | openmp runtime linked | 34.7 |  |

> antisym mt8-fastmath-native: inference suppressed -- trend: blade-cpp first-half vs second-half median moved 5.5%; trend: ifx-natural first-half vs second-half median moved 15.9%

| band | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (2 reassoc flags) |  |  | LLVM lane has no OpenMP |
| band | blade-cpp | no-parallel-form | 0 | -- | -- | -- |  |  |  | reordered (1 SIMD_REDUCTION) |  |  | kernel.json no_parallel_form (spec: no licensed parallel spelling) |
| band | gfortran-natural | ok | 21 | 0.02813 | 0.04556 | 0.01084 |  |  | win 10x+ (the natural tier is the n^2 predicate nest, a straw man by construction) |  | openmp runtime linked | 5.8 |  |
| band | gfortran-expert | ok | 21 | 0.001039 | 0.001179 | 0.0001621 |  |  | parity on blade-llvm (vectorized inner loop); LOSS on blade-cpp (inner loop scalar, G30) |  | openmp runtime linked | 5.8 |  |
| band | ifx-natural | ok | 21 | 0.624 | 0.6455 | 0.03141 |  |  | win 10x+ (the natural tier is the n^2 predicate nest, a straw man by construction) |  | openmp runtime linked | 7.4 |  |
| band | ifx-expert | ok | 21 | 0.001704 | 0.001914 | 0.0002446 |  |  | parity on blade-llvm (vectorized inner loop); LOSS on blade-cpp (inner loop scalar, G30) |  | openmp runtime linked | 7.4 |  |

> band mt8-fastmath-native: inference suppressed -- trend: gfortran-natural first-half vs second-half median moved 5.6%; trend: gfortran-expert first-half vs second-half median moved 9.6%

| comoment3 | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (7 reassoc flags) |  |  | LLVM lane has no OpenMP |
| comoment3 | blade-cpp | ok | 21 | 0.003096 | 0.003632 | 0.000457 | ref |  |  | reordered (5 SIMD_REDUCTION) | openmp (2 construct(s)) | 13.2 |  |
| comoment3 | gfortran-natural | ok | 21 | 0.02539 | 0.03584 | 0.0125 | 9.867 [7.432, 11.604] | suppressed | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | openmp runtime linked | 44.0 |  |
| comoment3 | gfortran-expert | ok | 21 | 0.004692 | 0.005232 | 0.0005558 | 1.441 [1.368, 1.594] | suppressed | parity to 1.3x loss |  | openmp runtime linked | 14.5 |  |
| comoment3 | ifx-natural | ok | 21 | 0.01625 | 0.02142 | 0.00645 | 5.898 [5.075, 7.214] | suppressed | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | openmp runtime linked | 45.4 |  |
| comoment3 | ifx-expert | ok | 21 | 0.00387 | 0.004419 | 0.0004872 | 1.217 [1.113, 1.355] | suppressed | parity to 1.3x loss |  | openmp runtime linked | 15.7 |  |

> comoment3 mt8-fastmath-native: inference suppressed -- trend: gfortran-natural first-half vs second-half median moved 33.5%; trend: ifx-natural first-half vs second-half median moved 18.1%

| gram | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (4 reassoc flags) |  |  | LLVM lane has no OpenMP |
| gram | blade-cpp | ok | 21 | 0.01527 | 0.01701 | 0.001283 | ref |  |  | reordered (3 SIMD_REDUCTION) | openmp (2 construct(s)) | 32.3 |  |
| gram | gfortran-natural | ok | 21 | 0.567 | 0.6538 | 0.09063 | 38.433 [36.550, 40.506] | suppressed | win 1.5-2x if matmul is a naive nest; parity/loss if a blocked runtime kernel |  | openmp runtime linked | 37.4 | matmul is unannotated: parallel preparation, serial kernel unless the runtime threads matmul (FORT records) |
| gram | gfortran-expert | ok | 21 | 0.02007 | 0.02096 | 0.001408 | 1.232 [1.173, 1.282] | suppressed | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | openmp runtime linked | 52.2 |  |
| gram | ifx-natural | ok | 21 | 0.2114 | 0.2353 | 0.01878 | 13.835 [13.282, 14.516] | suppressed | win 1.5-2x if matmul is a naive nest; parity/loss if a blocked runtime kernel |  | openmp runtime linked | 58.3 | matmul is unannotated: parallel preparation, serial kernel unless the runtime threads matmul (FORT records) |
| gram | ifx-expert | ok | 21 | 0.01843 | 0.01983 | 0.001173 | 1.166 [1.112, 1.202] | suppressed | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | openmp runtime linked | 53.8 |  |

> gram mt8-fastmath-native: inference suppressed -- trend: blade-cpp first-half vs second-half median moved 7.1%; trend: gfortran-natural first-half vs second-half median moved 8.5%; trend: gfortran-expert first-half vs second-half median moved 6.5%


## Controls

- compute (recur/gfortran-expert/serial-repro-native/full): n=84 median 0.01297 s, baseline 0.013 s
- memory (saxpy/gfortran-expert/serial-repro-native/control): n=84 median 0.005315 s, baseline 0.005287 s
