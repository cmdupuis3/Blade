# Blade vs Fortran -- bench session 2026-10-18T21:18:31

- host LAPTOP-350H6LDB | AMD Ryzen 7 5800HS with Radeon Graphics | 8C/16T | 15.41 GB | power ac | max processor state AC 100% | boost AC None | Power Scheme GUID: 64a64f24-65b9-4b56-befd-5ec1eaced9b3  (Silent)
- git 05f95277a0dbda3b2e9e98003bd5fb2c34f0e225 | Blade.dll 79af7edce32999db | GNU Fortran (Rev4, Built by MSYS2 project) 16.2.0 | Intel(R) Fortran Compiler for applications running on Intel(R) 64, Version 2026.1.0 Build 20260617 | clang version 22.1.8 (https://github.com/msys2/MINGW-packages d34feaaa1d0aff624c870910b6f2589aa40d20de)
- size `full` | duration 188.0 s | pins: pins-A.json, pins-B.json
- PRIMARY estimand: the complete timed region. ratio = median(arm) / median(Blade reference), round-block bootstrap 95% CI (10000 resamples); ratio > 1 = Blade faster. Verdicts (delta 0.1): win / detectable-win / equivalent / detectable-loss / loss / inconclusive; `suppressed` = the block failed a drift, trend or control check. Intervals are pointwise; no suite-wide claim. incremental = median(arm) - median(its own input-only twin), NOT kernel time. Reference: blade-llvm (serial), blade-cpp (mt8, or when the LLVM lane refused). Fresh-process, allocation-inclusive measurements.
- **ABORTED**: control memory: run took 0.092s, 16.4x its running median -- stopping (charter 2.5)
- **FLAG**: antisym/mt8-fastmath-native: inference suppressed: trend: ifx-natural first-half vs second-half median moved 10.6%; trend: control memory moved 5.4% within the block

## mt8-fastmath-native

Blade reference per kernel: antisym=blade-cpp, band=None

| kernel | arm | status | n | min (s) | median (s) | IQR (s) | ratio vs ref [95% CI] | verdict | hypothesis | fold form | parallelism | peak WS (MB) | notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| antisym | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (6 reassoc flags) |  |  | LLVM lane has no OpenMP |
| antisym | blade-cpp | ok | 21 | 0.00826 | 0.009797 | 0.000629 | ref |  |  | reordered (5 SIMD_REDUCTION) | openmp (2 construct(s)) | 26.4 |  |
| antisym | gfortran-natural | ok | 21 | 0.03568 | 0.04131 | 0.003503 | 4.217 [4.073, 4.449] | suppressed | WIN ~2x (half the cells, no diagonal) |  | openmp runtime linked | 23.9 |  |
| antisym | gfortran-expert | ok | 21 | 0.009825 | 0.01063 | 0.001068 | 1.085 [1.055, 1.149] | suppressed | parity +-15% |  | openmp runtime linked | 33.2 |  |
| antisym | ifx-natural | ok | 21 | 0.01789 | 0.02421 | 0.003546 | 2.471 [2.343, 2.607] | suppressed | WIN ~2x (half the cells, no diagonal) |  | openmp runtime linked | 25.4 |  |
| antisym | ifx-expert | ok | 21 | 0.009651 | 0.01071 | 0.0005653 | 1.093 [1.068, 1.145] | suppressed | parity +-15% |  | openmp runtime linked | 34.7 |  |

> antisym mt8-fastmath-native: inference suppressed -- trend: ifx-natural first-half vs second-half median moved 10.6%; trend: control memory moved 5.4% within the block

| band | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (2 reassoc flags) |  |  | LLVM lane has no OpenMP |
| band | blade-cpp | no-parallel-form | 0 | -- | -- | -- |  |  |  | reordered (1 SIMD_REDUCTION) |  |  | kernel.json no_parallel_form (spec: no licensed parallel spelling) |
| band | gfortran-natural | ok | 0 | -- | -- | -- |  |  | win 10x+ (the natural tier is the n^2 predicate nest, a straw man by construction) |  | openmp runtime linked |  |  |
| band | gfortran-expert | ok | 0 | -- | -- | -- |  |  | parity on blade-llvm (vectorized inner loop); LOSS on blade-cpp (inner loop scalar, G30) |  | openmp runtime linked |  |  |
| band | ifx-natural | ok | 0 | -- | -- | -- |  |  | win 10x+ (the natural tier is the n^2 predicate nest, a straw man by construction) |  | openmp runtime linked |  |  |
| band | ifx-expert | ok | 0 | -- | -- | -- |  |  | parity on blade-llvm (vectorized inner loop); LOSS on blade-cpp (inner loop scalar, G30) |  | openmp runtime linked |  |  |

## Controls

- compute (recur/gfortran-expert/serial-repro-native/full): n=25 median 0.01355 s, baseline 0.01355 s
- memory (saxpy/gfortran-expert/serial-repro-native/control): n=26 median 0.00563 s, baseline 0.00571 s

## Failures

- ABORTED: control memory: run took 0.092s, 16.4x its running median -- stopping (charter 2.5)
