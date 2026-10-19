# Blade vs Fortran -- bench session 2026-10-18T21:45:22

- host LAPTOP-350H6LDB | AMD Ryzen 7 5800HS with Radeon Graphics | 8C/16T | 15.41 GB | power ac | max processor state AC 100% | boost AC None | Power Scheme GUID: 64a64f24-65b9-4b56-befd-5ec1eaced9b3  (Silent)
- git 99b7056f11d28953aab6be253dc229cc7145ebc0 | Blade.dll 79af7edce32999db | GNU Fortran (Rev4, Built by MSYS2 project) 16.2.0 | Intel(R) Fortran Compiler for applications running on Intel(R) 64, Version 2026.1.0 Build 20260617 | clang version 22.1.8 (https://github.com/msys2/MINGW-packages d34feaaa1d0aff624c870910b6f2589aa40d20de)
- size `full` | duration 123.9 s | pins: pins-A.json, pins-B.json
- PRIMARY estimand: the complete timed region. ratio = median(arm) / median(Blade reference), round-block bootstrap 95% CI (10000 resamples); ratio > 1 = Blade faster. Verdicts (delta 0.1): win / detectable-win / equivalent / detectable-loss / loss / inconclusive; `suppressed` = the block failed a drift, trend or control check. Intervals are pointwise; no suite-wide claim. incremental = median(arm) - median(its own input-only twin), NOT kernel time. Reference: blade-llvm (serial), blade-cpp (mt8, or when the LLVM lane refused). Fresh-process, allocation-inclusive measurements.

## serial-licensed-blas

Blade reference per kernel: gram=blade-cpp

| kernel | arm | status | n | min (s) | median (s) | IQR (s) | ratio vs ref [95% CI] | verdict | hypothesis | fold form | parallelism | peak WS (MB) | notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| gram | blade-llvm | no-blas | 0 | -- | -- | -- |  |  |  | reordered (4 reassoc flags) |  |  | LLVM lane does not route to BLAS (the exe imports no libopenblas.dll) |
| gram | blade-cpp | ok | 11 | 0.06355 | 0.06453 | 0.0007223 | ref |  |  | reordered (4 SIMD_REDUCTION) | library threads (OPENBLAS_NUM_THREADS=1) | 49.1 |  |
| gram | gfortran-expert | ok | 11 | 0.06415 | 0.06539 | 0.0005121 | 1.013 [1.005, 1.023] | detectable-win | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | library threads (OPENBLAS_NUM_THREADS=1) | 34.9 |  |
| gram | ifx-expert | ok | 11 | 0.06204 | 0.06248 | 0.0006408 | 0.968 [0.963, 0.983] | detectable-loss | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | library threads (OPENBLAS_NUM_THREADS=1) | 34.6 |  |

## mt8-licensed-blas

Blade reference per kernel: gram=blade-cpp

| kernel | arm | status | n | min (s) | median (s) | IQR (s) | ratio vs ref [95% CI] | verdict | hypothesis | fold form | parallelism | peak WS (MB) | notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| gram | blade-llvm | no-blas | 0 | -- | -- | -- |  |  |  | reordered (4 reassoc flags) |  |  | LLVM lane does not route to BLAS (the exe imports no libopenblas.dll) |
| gram | blade-cpp | ok | 21 | 0.02098 | 0.02446 | 0.003138 | ref |  |  | reordered (3 SIMD_REDUCTION) | openmp (2 construct(s)) | 51.2 |  |
| gram | gfortran-expert | ok | 21 | 0.01555 | 0.01927 | 0.002722 | 0.788 [0.731, 0.869] | loss | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | library threads (OPENBLAS_NUM_THREADS=8) + openmp runtime | 36.7 |  |
| gram | ifx-expert | ok | 21 | 0.01857 | 0.01998 | 0.001047 | 0.817 [0.776, 0.897] | loss | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | library threads (OPENBLAS_NUM_THREADS=8) + openmp runtime | 38.1 |  |

## Controls

- compute (recur/gfortran-expert/serial-repro-native/full): n=32 median 0.01286 s, baseline 0.01279 s
- memory (saxpy/gfortran-expert/serial-repro-native/control): n=32 median 0.005375 s, baseline 0.005438 s
