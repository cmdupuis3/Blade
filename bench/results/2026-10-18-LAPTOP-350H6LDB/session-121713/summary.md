# Blade vs Fortran -- bench session 2026-10-18T12:17:13

- host LAPTOP-350H6LDB | AMD Ryzen 7 5800HS with Radeon Graphics | 8C/16T | 15.41 GB | power ac | max processor state AC 100% | boost AC None | Power Scheme GUID: 64a64f24-65b9-4b56-befd-5ec1eaced9b3  (Silent)
- git fceb895d4e8df71131086a94652b5cab1b120850 | Blade.dll 74b6728036b170d9 | GNU Fortran (Rev4, Built by MSYS2 project) 16.2.0 | Intel(R) Fortran Compiler for applications running on Intel(R) 64, Version 2026.1.0 Build 20260617 | clang version 22.1.8 (https://github.com/msys2/MINGW-packages d34feaaa1d0aff624c870910b6f2589aa40d20de)
- size `full` | duration 229.0 s | pins: pins-A.json, pins-B.json
- PRIMARY estimand: the complete timed region. ratio = median(arm) / median(Blade reference), round-block bootstrap 95% CI (10000 resamples); ratio > 1 = Blade faster. Verdicts (delta 0.1): win / detectable-win / equivalent / detectable-loss / loss / inconclusive; `suppressed` = the block failed a drift, trend or control check. Intervals are pointwise; no suite-wide claim. incremental = median(arm) - median(its own input-only twin), NOT kernel time. Reference: blade-llvm (serial), blade-cpp (mt8, or when the LLVM lane refused). Fresh-process, allocation-inclusive measurements.

## serial-licensed-native

Blade reference per kernel: antisym=blade-llvm

| kernel | arm | status | n | min (s) | median (s) | IQR (s) | ratio vs ref [95% CI] | verdict | hypothesis | fold form | parallelism | peak WS (MB) | notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| antisym | blade-llvm | ok | 11 | 0.07317 | 0.07395 | 0.0002563 | ref |  |  | reordered (7 reassoc flags) | serial | 17.4 |  |
| antisym | blade-cpp | ok | 11 | 0.1073 | 0.1079 | 0.001023 | 1.459 [1.452, 1.471] | win |  | reordered (7 SIMD_REDUCTION) | serial | 18.3 |  |
| antisym | gfortran-natural | ok | 11 | 0.7647 | 0.7709 | 0.002733 | 10.425 [10.371, 10.455] | win | WIN ~2x (half the cells, no diagonal) |  | serial | 23.4 |  |
| antisym | gfortran-expert | ok | 11 | 0.03024 | 0.03074 | 0.0002121 | 0.416 [0.414, 0.417] | loss | parity +-15% |  | serial | 32.7 |  |
| antisym | ifx-natural | ok | 11 | 0.3308 | 0.3329 | 0.002219 | 4.502 [4.475, 4.516] | win | WIN ~2x (half the cells, no diagonal) |  | serial | 22.9 |  |
| antisym | ifx-expert | ok | 11 | 0.02875 | 0.02954 | 0.0007387 | 0.400 [0.395, 0.407] | loss | parity +-15% |  | serial | 32.2 |  |

## Controls

- compute (recur/gfortran-expert/serial-repro-native/full): n=11 median 0.01284 s, baseline 0.01284 s
- memory (saxpy/gfortran-expert/serial-repro-native/control): n=11 median 0.005453 s, baseline 0.005453 s
