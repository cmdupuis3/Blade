# Blade vs Fortran -- bench session 2026-10-18T21:49:54

- host LAPTOP-350H6LDB | AMD Ryzen 7 5800HS with Radeon Graphics | 8C/16T | 15.41 GB | power ac | max processor state AC 100% | boost AC None | Power Scheme GUID: 64a64f24-65b9-4b56-befd-5ec1eaced9b3  (Silent)
- git 99b7056f11d28953aab6be253dc229cc7145ebc0 | Blade.dll 79af7edce32999db | GNU Fortran (Rev4, Built by MSYS2 project) 16.2.0 | Intel(R) Fortran Compiler for applications running on Intel(R) 64, Version 2026.1.0 Build 20260617 | clang version 22.1.8 (https://github.com/msys2/MINGW-packages d34feaaa1d0aff624c870910b6f2589aa40d20de)
- size `full` | duration 127.9 s | pins: pins-A.json, pins-B.json
- PRIMARY estimand: the complete timed region. ratio = median(arm) / median(Blade reference), round-block bootstrap 95% CI (10000 resamples); ratio > 1 = Blade faster. Verdicts (delta 0.1): win / detectable-win / equivalent / detectable-loss / loss / inconclusive; `suppressed` = the block failed a drift, trend or control check. Intervals are pointwise; no suite-wide claim. incremental = median(arm) - median(its own input-only twin), NOT kernel time. Reference: blade-llvm (serial), blade-cpp (mt8, or when the LLVM lane refused). Fresh-process, allocation-inclusive measurements.

## serial-fastmath-native

Blade reference per kernel: band=blade-llvm

| kernel | arm | status | n | min (s) | median (s) | IQR (s) | ratio vs ref [95% CI] | verdict | hypothesis | fold form | parallelism | peak WS (MB) | notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| band | blade-llvm | ok | 21 | 0.003299 | 0.003312 | 1.75e-05 | ref |  |  | reordered (2 reassoc flags) | serial | 4.8 |  |
| band | blade-cpp | ok | 21 | 0.009263 | 0.009277 | 2.23e-05 | 2.801 [2.795, 2.806] | win |  | reordered (1 SIMD_REDUCTION) | serial | 5.5 |  |
| band | gfortran-natural | ok | 21 | 0.3003 | 0.3017 | 0.001857 | 91.102 [90.904, 91.455] | win | win 10x+ (the natural tier is the n^2 predicate nest, a straw man by construction) |  | serial | 5.4 |  |
| band | gfortran-expert | ok | 21 | 0.001541 | 0.001699 | 0.0001581 | 0.513 [0.469, 0.517] | loss | parity on blade-llvm (vectorized inner loop); LOSS on blade-cpp (inner loop scalar, G30) |  | serial | 5.4 |  |
| band | ifx-natural | ok | 21 | 0.3278 | 0.3315 | 0.007246 | 100.087 [99.378, 101.014] | win | win 10x+ (the natural tier is the n^2 predicate nest, a straw man by construction) |  | serial | 5.1 |  |
| band | ifx-expert | ok | 21 | 0.001311 | 0.001322 | 2.15e-05 | 0.399 [0.398, 0.401] | loss | parity on blade-llvm (vectorized inner loop); LOSS on blade-cpp (inner loop scalar, G30) |  | serial | 5.0 |  |

## Controls

- compute (recur/gfortran-expert/serial-repro-native/full): n=21 median 0.01277 s, baseline 0.01277 s
- memory (saxpy/gfortran-expert/serial-repro-native/control): n=21 median 0.005391 s, baseline 0.005391 s
