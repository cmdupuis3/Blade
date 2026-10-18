# Blade vs Fortran -- bench session 2026-10-18T15:03:43

- host LAPTOP-350H6LDB | AMD Ryzen 7 5800HS with Radeon Graphics | 8C/16T | 15.41 GB | power ac | max processor state AC 100% | boost AC None | Power Scheme GUID: 64a64f24-65b9-4b56-befd-5ec1eaced9b3  (Silent)
- git fceb895d4e8df71131086a94652b5cab1b120850 | Blade.dll 74b6728036b170d9 | GNU Fortran (Rev4, Built by MSYS2 project) 16.2.0 | Intel(R) Fortran Compiler for applications running on Intel(R) 64, Version 2026.1.0 Build 20260617 | clang version 22.1.8 (https://github.com/msys2/MINGW-packages d34feaaa1d0aff624c870910b6f2589aa40d20de)
- size `full` | duration 123.6 s | pins: pins-A.json, pins-B.json
- PRIMARY estimand: the complete timed region. ratio = median(arm) / median(Blade reference), round-block bootstrap 95% CI (10000 resamples); ratio > 1 = Blade faster. Verdicts (delta 0.1): win / detectable-win / equivalent / detectable-loss / loss / inconclusive; `suppressed` = the block failed a drift, trend or control check. Intervals are pointwise; no suite-wide claim. incremental = median(arm) - median(its own input-only twin), NOT kernel time. Reference: blade-llvm (serial), blade-cpp (mt8, or when the LLVM lane refused). Fresh-process, allocation-inclusive measurements.

## serial-licensed-blas

Blade reference per kernel: gram=blade-cpp

| kernel | arm | status | n | min (s) | median (s) | IQR (s) | ratio vs ref [95% CI] | verdict | hypothesis | fold form | parallelism | peak WS (MB) | notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| gram | blade-llvm | no-blas | 0 | -- | -- | -- |  |  |  | reordered (8 reassoc flags) |  |  | LLVM lane does not route to BLAS (the exe imports no libopenblas.dll) |
| gram | blade-cpp | ok | 11 | 0.07982 | 0.08071 | 0.0008952 | ref |  |  | reordered (4 SIMD_REDUCTION) | library threads (OPENBLAS_NUM_THREADS=1) | 49.0 |  |
| gram | gfortran-expert | ok | 11 | 0.06497 | 0.06571 | 0.000664 | 0.814 [0.805, 0.827] | loss | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | library threads (OPENBLAS_NUM_THREADS=1) | 34.8 |  |
| gram | ifx-expert | ok | 11 | 0.06214 | 0.06283 | 0.0006551 | 0.778 [0.772, 0.786] | loss | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | library threads (OPENBLAS_NUM_THREADS=1) | 34.6 |  |

## mt8-licensed-blas

Blade reference per kernel: gram=blade-cpp

| kernel | arm | status | n | min (s) | median (s) | IQR (s) | ratio vs ref [95% CI] | verdict | hypothesis | fold form | parallelism | peak WS (MB) | notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| gram | blade-llvm | no-blas | 0 | -- | -- | -- |  |  |  | reordered (8 reassoc flags) |  |  | LLVM lane does not route to BLAS (the exe imports no libopenblas.dll) |
| gram | blade-cpp | ok | 21 | 0.02382 | 0.02814 | 0.002078 | ref |  |  | reordered (2 SIMD_REDUCTION) | openmp (3 construct(s)) | 51.1 |  |
| gram | gfortran-expert | ok | 21 | 0.01498 | 0.02026 | 0.001776 | 0.720 [0.684, 0.743] | loss | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | library threads (OPENBLAS_NUM_THREADS=8) + openmp runtime | 36.6 |  |
| gram | ifx-expert | ok | 21 | 0.01681 | 0.0195 | 0.001385 | 0.693 [0.665, 0.728] | loss | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | library threads (OPENBLAS_NUM_THREADS=8) + openmp runtime | 38.1 |  |

## Controls

- compute (recur/gfortran-expert/serial-repro-native/full): n=32 median 0.01291 s, baseline 0.01306 s
- memory (saxpy/gfortran-expert/serial-repro-native/control): n=32 median 0.005372 s, baseline 0.005331 s
