# Blade vs Fortran -- bench session 2026-10-18T21:14:35

- host LAPTOP-350H6LDB | AMD Ryzen 7 5800HS with Radeon Graphics | 8C/16T | 15.41 GB | power ac | max processor state AC 100% | boost AC None | Power Scheme GUID: 64a64f24-65b9-4b56-befd-5ec1eaced9b3  (Silent)
- git 05f95277a0dbda3b2e9e98003bd5fb2c34f0e225 | Blade.dll 79af7edce32999db | GNU Fortran (Rev4, Built by MSYS2 project) 16.2.0 | Intel(R) Fortran Compiler for applications running on Intel(R) 64, Version 2026.1.0 Build 20260617 | clang version 22.1.8 (https://github.com/msys2/MINGW-packages d34feaaa1d0aff624c870910b6f2589aa40d20de)
- size `full` | duration 101.4 s | pins: pins-A.json, pins-B.json
- PRIMARY estimand: the complete timed region. ratio = median(arm) / median(Blade reference), round-block bootstrap 95% CI (10000 resamples); ratio > 1 = Blade faster. Verdicts (delta 0.1): win / detectable-win / equivalent / detectable-loss / loss / inconclusive; `suppressed` = the block failed a drift, trend or control check. Intervals are pointwise; no suite-wide claim. incremental = median(arm) - median(its own input-only twin), NOT kernel time. Reference: blade-llvm (serial), blade-cpp (mt8, or when the LLVM lane refused). Fresh-process, allocation-inclusive measurements.
- **ABORTED**: second excursion in block antisym/mt8-fastmath-native: antisym/mt8-fastmath-native/gfortran-expert round 14 took 0.036s, 3.4x its running median (first: antisym/mt8-fastmath-native/ifx-natural round 10) -- stopping (charter 2.5)

## mt8-fastmath-native

Blade reference per kernel: antisym=blade-cpp

| kernel | arm | status | n | min (s) | median (s) | IQR (s) | ratio vs ref [95% CI] | verdict | hypothesis | fold form | parallelism | peak WS (MB) | notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| antisym | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (6 reassoc flags) |  |  | LLVM lane has no OpenMP |
| antisym | blade-cpp | ok | 0 | -- | -- | -- | ref |  |  | reordered (5 SIMD_REDUCTION) | openmp (2 construct(s)) |  |  |
| antisym | gfortran-natural | ok | 0 | -- | -- | -- |  |  | WIN ~2x (half the cells, no diagonal) |  | openmp runtime linked |  |  |
| antisym | gfortran-expert | ok | 0 | -- | -- | -- |  |  | parity +-15% |  | openmp runtime linked |  |  |
| antisym | ifx-natural | ok | 0 | -- | -- | -- |  |  | WIN ~2x (half the cells, no diagonal) |  | openmp runtime linked |  |  |
| antisym | ifx-expert | ok | 0 | -- | -- | -- |  |  | parity +-15% |  | openmp runtime linked |  |  |

## Controls

- compute (recur/gfortran-expert/serial-repro-native/full): n=15 median 0.01315 s, baseline -- s
- memory (saxpy/gfortran-expert/serial-repro-native/control): n=14 median 0.005583 s, baseline -- s

## Failures

- ABORTED: second excursion in block antisym/mt8-fastmath-native: antisym/mt8-fastmath-native/gfortran-expert round 14 took 0.036s, 3.4x its running median (first: antisym/mt8-fastmath-native/ifx-natural round 10) -- stopping (charter 2.5)

## Warnings

- excursion dropped (isolated; recorded): antisym/mt8-fastmath-native/ifx-natural round 10: 0.097s = 4.6x running median
