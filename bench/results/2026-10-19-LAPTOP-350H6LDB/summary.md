# Blade vs Fortran -- bench session 2026-10-19T16:11:02

- host LAPTOP-350H6LDB | AMD Ryzen 7 5800HS with Radeon Graphics | 8C/16T | 15.41 GB | power ac | max processor state AC 100% | boost AC None | Power Scheme GUID: 64a64f24-65b9-4b56-befd-5ec1eaced9b3  (Silent)
- git 39a8a869ff410c79edbe599a98257d4012241702 | Blade.dll bb52a4c2a008d198 | GNU Fortran (Rev4, Built by MSYS2 project) 16.2.0 | Intel(R) Fortran Compiler for applications running on Intel(R) 64, Version 2026.1.0 Build 20260617 | clang version 22.1.8 (https://github.com/msys2/MINGW-packages d34feaaa1d0aff624c870910b6f2589aa40d20de)
- size `full` | duration 149.1 s | pins: pins-A.json, pins-B.json
- PRIMARY estimand: the complete timed region. ratio = median(arm) / median(Blade reference), round-block bootstrap 95% CI (10000 resamples); ratio > 1 = Blade faster. Verdicts (delta 0.1): win / detectable-win / equivalent / detectable-loss / loss / inconclusive; `suppressed` = the block failed a drift, trend or control check. Intervals are pointwise; no suite-wide claim. incremental = median(arm) - median(its own input-only twin), NOT kernel time. Reference: blade-llvm (serial), blade-cpp (mt8, or when the LLVM lane refused). Fresh-process, allocation-inclusive measurements.

## serial-licensed-blas

Blade reference per kernel: gram=blade-cpp

| kernel | arm | status | n | min (s) | median (s) | IQR (s) | ratio vs ref [95% CI] | verdict | hypothesis | fold form | parallelism | peak WS (MB) | notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| gram | blade-llvm | no-blas | 0 | -- | -- | -- |  |  |  | reordered (4 reassoc flags) |  |  | LLVM lane does not route to BLAS (the exe imports no libopenblas.dll) |
| gram | blade-cpp | ok | 11 | 0.06029 | 0.06184 | 0.0009542 | ref |  |  | reordered (4 SIMD_REDUCTION/K-lane) | serial | 31.5 | native tile preempted the library ([microkernel v1] __v27: applied [packed syrk tile (blade_pgemm::dsyrk_tri, 6x8 register ti) |
| gram | gfortran-expert | ok | 11 | 0.06675 | 0.06737 | 0.00172 | 1.089 [1.074, 1.126] | detectable-win | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | library threads (OPENBLAS_NUM_THREADS=1) | 34.9 |  |
| gram | ifx-expert | ok | 11 | 0.06346 | 0.06443 | 0.001421 | 1.042 [1.031, 1.063] | detectable-win | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | library threads (OPENBLAS_NUM_THREADS=1) | 34.6 |  |

## mt8-licensed-blas

Blade reference per kernel: gram=blade-cpp

| kernel | arm | status | n | min (s) | median (s) | IQR (s) | ratio vs ref [95% CI] | verdict | hypothesis | fold form | parallelism | peak WS (MB) | notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| gram | blade-llvm | no-blas | 0 | -- | -- | -- |  |  |  | reordered (4 reassoc flags) |  |  | LLVM lane does not route to BLAS (the exe imports no libopenblas.dll) |
| gram | blade-cpp | ok | 21 | 0.01619 | 0.01797 | 0.00136 | ref |  |  | reordered (4 SIMD_REDUCTION/K-lane) | openmp (3 construct(s)) | 32.3 | native tile preempted the library ([microkernel v1] __v27: applied [packed syrk tile (blade_pgemm::dsyrk_tri, 6x8 register ti) |
| gram | gfortran-expert | ok | 21 | 0.01729 | 0.02145 | 0.003983 | 1.194 [1.144, 1.346] | win | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | library threads (OPENBLAS_NUM_THREADS=8) + openmp runtime | 36.7 |  |
| gram | ifx-expert | ok | 21 | 0.0211 | 0.02244 | 0.002451 | 1.249 [1.204, 1.341] | win | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | library threads (OPENBLAS_NUM_THREADS=8) + openmp runtime | 38.1 |  |

## Controls

- compute (recur/gfortran-expert/serial-repro-native/full): n=32 median 0.01374 s, baseline 0.0137 s
- memory (saxpy/gfortran-expert/serial-repro-native/control): n=32 median 0.005511 s, baseline 0.005686 s
