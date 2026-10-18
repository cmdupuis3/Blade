# Blade vs Fortran -- bench session 2026-10-18T14:41:22

- host LAPTOP-350H6LDB | AMD Ryzen 7 5800HS with Radeon Graphics | 8C/16T | 15.41 GB | power ac | max processor state AC 100% | boost AC None | Power Scheme GUID: 64a64f24-65b9-4b56-befd-5ec1eaced9b3  (Silent)
- git fceb895d4e8df71131086a94652b5cab1b120850 | Blade.dll 74b6728036b170d9 | GNU Fortran (Rev4, Built by MSYS2 project) 16.2.0 | Intel(R) Fortran Compiler for applications running on Intel(R) 64, Version 2026.1.0 Build 20260617 | clang version 22.1.8 (https://github.com/msys2/MINGW-packages d34feaaa1d0aff624c870910b6f2589aa40d20de)
- size `full` | duration 182.2 s | pins: pins-A.json, pins-B.json
- PRIMARY estimand: the complete timed region. ratio = median(arm) / median(Blade reference), round-block bootstrap 95% CI (10000 resamples); ratio > 1 = Blade faster. Verdicts (delta 0.1): win / detectable-win / equivalent / detectable-loss / loss / inconclusive; `suppressed` = the block failed a drift, trend or control check. Intervals are pointwise; no suite-wide claim. incremental = median(arm) - median(its own input-only twin), NOT kernel time. Reference: blade-llvm (serial), blade-cpp (mt8, or when the LLVM lane refused). Fresh-process, allocation-inclusive measurements.
- **ABORTED**: control memory: run took 0.030s, 5.5x its running median -- stopping (charter 2.5)
- **FLAG**: antisym/mt8-fastmath-native: inference suppressed: trend: ifx-natural first-half vs second-half median moved 11.0%

## mt8-fastmath-native

Blade reference per kernel: antisym=blade-cpp, comoment3=blade-cpp

| kernel | arm | status | n | min (s) | median (s) | IQR (s) | ratio vs ref [95% CI] | verdict | hypothesis | fold form | parallelism | peak WS (MB) | notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| antisym | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (7 reassoc flags) |  |  | LLVM lane has no OpenMP |
| antisym | blade-cpp | ok | 21 | 0.01918 | 0.02095 | 0.001521 | ref |  |  | reordered (6 SIMD_REDUCTION) | openmp (4 construct(s)) | 18.7 |  |
| antisym | gfortran-natural | ok | 21 | 0.02518 | 0.0354 | 0.005936 | 1.690 [1.500, 1.794] | suppressed | WIN ~2x (half the cells, no diagonal) |  | openmp runtime linked | 23.8 |  |
| antisym | gfortran-expert | ok | 21 | 0.00901 | 0.009806 | 0.001142 | 0.468 [0.447, 0.495] | suppressed | parity +-15% |  | openmp runtime linked | 33.1 |  |
| antisym | ifx-natural | ok | 21 | 0.0167 | 0.0221 | 0.00349 | 1.055 [1.021, 1.118] | suppressed | WIN ~2x (half the cells, no diagonal) |  | openmp runtime linked | 25.4 |  |
| antisym | ifx-expert | ok | 21 | 0.009138 | 0.01001 | 0.0008783 | 0.478 [0.458, 0.510] | suppressed | parity +-15% |  | openmp runtime linked | 34.6 |  |

> antisym mt8-fastmath-native: inference suppressed -- trend: ifx-natural first-half vs second-half median moved 11.0%

| comoment3 | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (6 reassoc flags) |  |  | LLVM lane has no OpenMP |
| comoment3 | blade-cpp | ok | 0 | -- | -- | -- | ref |  |  | reordered (4 SIMD_REDUCTION) | openmp (4 construct(s)) |  |  |
| comoment3 | gfortran-natural | ok | 0 | -- | -- | -- |  |  | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | openmp runtime linked |  |  |
| comoment3 | gfortran-expert | ok | 0 | -- | -- | -- |  |  | parity to 1.3x loss |  | openmp runtime linked |  |  |
| comoment3 | ifx-natural | ok | 0 | -- | -- | -- |  |  | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | openmp runtime linked |  |  |
| comoment3 | ifx-expert | ok | 0 | -- | -- | -- |  |  | parity to 1.3x loss |  | openmp runtime linked |  |  |

## Controls

- compute (recur/gfortran-expert/serial-repro-native/full): n=26 median 0.01296 s, baseline 0.01292 s
- memory (saxpy/gfortran-expert/serial-repro-native/control): n=26 median 0.005375 s, baseline 0.005337 s

## Failures

- ABORTED: control memory: run took 0.030s, 5.5x its running median -- stopping (charter 2.5)

## Warnings

- excursion dropped (isolated; recorded): comoment3/mt8-fastmath-native/blade-cpp round 1: 0.078s = 3.3x running median
