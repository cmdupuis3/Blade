# Blade vs Fortran -- bench session 2026-10-18T20:53:06

- host LAPTOP-350H6LDB | AMD Ryzen 7 5800HS with Radeon Graphics | 8C/16T | 15.41 GB | power ac | max processor state AC 100% | boost AC None | Power Scheme GUID: 64a64f24-65b9-4b56-befd-5ec1eaced9b3  (Silent)
- git 05f95277a0dbda3b2e9e98003bd5fb2c34f0e225 | Blade.dll 79af7edce32999db | GNU Fortran (Rev4, Built by MSYS2 project) 16.2.0 | Intel(R) Fortran Compiler for applications running on Intel(R) 64, Version 2026.1.0 Build 20260617 | clang version 22.1.8 (https://github.com/msys2/MINGW-packages d34feaaa1d0aff624c870910b6f2589aa40d20de)
- size `full` | duration 748.0 s | pins: pins-A.json, pins-B.json
- PRIMARY estimand: the complete timed region. ratio = median(arm) / median(Blade reference), round-block bootstrap 95% CI (10000 resamples); ratio > 1 = Blade faster. Verdicts (delta 0.1): win / detectable-win / equivalent / detectable-loss / loss / inconclusive; `suppressed` = the block failed a drift, trend or control check. Intervals are pointwise; no suite-wide claim. incremental = median(arm) - median(its own input-only twin), NOT kernel time. Reference: blade-llvm (serial), blade-cpp (mt8, or when the LLVM lane refused). Fresh-process, allocation-inclusive measurements.
- **FLAG**: antisym/mt8-repro-native: inference suppressed: trend: blade-cpp first-half vs second-half median moved 6.2%; trend: gfortran-natural first-half vs second-half median moved 7.9%; trend: gfortran-expert first-half vs second-half median moved 12.4%; trend: ifx-natural first-half vs second-half median moved 9.9%; trend: ifx-expert first-half vs second-half median moved 5.1%
- **FLAG**: band/mt8-repro-native: inference suppressed: trend: ifx-natural first-half vs second-half median moved 29.7%; trend: ifx-expert first-half vs second-half median moved 8.3%
- **FLAG**: comoment3/mt8-repro-native: inference suppressed: trend: blade-cpp first-half vs second-half median moved 7.2%; trend: gfortran-expert first-half vs second-half median moved 8.4%
- **FLAG**: gram/mt8-repro-native: inference suppressed: trend: gfortran-natural first-half vs second-half median moved 17.8%
- **FLAG**: pairkernel/mt8-repro-native: inference suppressed: trend: gfortran-natural first-half vs second-half median moved 14.6%; trend: gfortran-expert first-half vs second-half median moved 5.9%; trend: ifx-natural first-half vs second-half median moved 8.5%; trend: control memory moved 7.1% within the block
- **FLAG**: recur/mt8-repro-native: inference suppressed: trend: ifx-natural first-half vs second-half median moved 6.5%; trend: ifx-expert first-half vs second-half median moved 5.4%; trend: control memory moved 5.4% within the block

## mt8-repro-native

Blade reference per kernel: antisym=blade-cpp, band=None, comoment3=blade-cpp, gram=blade-cpp, pairkernel=blade-cpp, recur=None

| kernel | arm | status | n | min (s) | median (s) | IQR (s) | ratio vs ref [95% CI] | verdict | hypothesis | fold form | parallelism | peak WS (MB) | notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| antisym | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | in-order |  |  | LLVM lane has no OpenMP |
| antisym | blade-cpp | ok | 21 | 0.009585 | 0.01088 | 0.0008065 | ref |  |  | in-order | openmp (1 construct(s)); threaded maps, serial folds (BLADE_FP_REPRO) | 26.4 |  |
| antisym | gfortran-natural | ok | 21 | 0.1035 | 0.1131 | 0.008584 | 10.400 [10.063, 10.700] | suppressed | WIN ~2x (half the cells, no diagonal) |  | openmp runtime linked | 23.9 |  |
| antisym | gfortran-expert | ok | 21 | 0.009087 | 0.0101 | 0.001305 | 0.929 [0.878, 0.983] | suppressed | parity +-15% |  | openmp runtime linked | 33.2 |  |
| antisym | ifx-natural | ok | 21 | 0.04918 | 0.07228 | 0.009336 | 6.644 [6.142, 6.794] | suppressed | WIN ~2x (half the cells, no diagonal) |  | openmp runtime linked | 25.4 |  |
| antisym | ifx-expert | ok | 21 | 0.009164 | 0.01011 | 0.001874 | 0.929 [0.903, 0.989] | suppressed | parity +-15% |  | openmp runtime linked | 34.7 |  |

> antisym mt8-repro-native: inference suppressed -- trend: blade-cpp first-half vs second-half median moved 6.2%; trend: gfortran-natural first-half vs second-half median moved 7.9%; trend: gfortran-expert first-half vs second-half median moved 12.4%; trend: ifx-natural first-half vs second-half median moved 9.9%; trend: ifx-expert first-half vs second-half median moved 5.1%

| band | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | in-order |  |  | LLVM lane has no OpenMP |
| band | blade-cpp | no-parallel-form | 0 | -- | -- | -- |  |  |  | in-order |  |  | kernel.json no_parallel_form (spec: no licensed parallel spelling) |
| band | gfortran-natural | ok | 21 | 0.02726 | 0.04071 | 0.0179 |  |  | win 10x+ (the natural tier is the n^2 predicate nest, a straw man by construction) |  | openmp runtime linked | 5.8 |  |
| band | gfortran-expert | ok | 21 | 0.00124 | 0.00133 | 0.000106 |  |  | parity on blade-llvm (vectorized inner loop); LOSS on blade-cpp (inner loop scalar, G30) |  | openmp runtime linked | 5.8 |  |
| band | ifx-natural | ok | 21 | 0.02014 | 0.03825 | 0.01648 |  |  | win 10x+ (the natural tier is the n^2 predicate nest, a straw man by construction) |  | openmp runtime linked | 7.4 |  |
| band | ifx-expert | ok | 21 | 0.002063 | 0.002344 | 0.0002786 |  |  | parity on blade-llvm (vectorized inner loop); LOSS on blade-cpp (inner loop scalar, G30) |  | openmp runtime linked | 7.4 |  |

> band mt8-repro-native: inference suppressed -- trend: ifx-natural first-half vs second-half median moved 29.7%; trend: ifx-expert first-half vs second-half median moved 8.3%

| comoment3 | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | in-order |  |  | LLVM lane has no OpenMP |
| comoment3 | blade-cpp | ok | 21 | 0.009407 | 0.00991 | 0.0009265 | ref |  |  | in-order | openmp (1 construct(s)); threaded maps, serial folds (BLADE_FP_REPRO) | 13.2 |  |
| comoment3 | gfortran-natural | ok | 21 | 0.08943 | 0.09583 | 0.003975 | 9.669 [9.185, 9.900] | suppressed | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | openmp runtime linked | 44.0 |  |
| comoment3 | gfortran-expert | ok | 21 | 0.004671 | 0.004861 | 0.0005836 | 0.490 [0.466, 0.521] | suppressed | parity to 1.3x loss |  | openmp runtime linked | 14.6 |  |
| comoment3 | ifx-natural | ok | 21 | 0.1117 | 0.1206 | 0.007591 | 12.172 [11.466, 12.565] | suppressed | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | openmp runtime linked | 45.5 |  |
| comoment3 | ifx-expert | ok | 21 | 0.0038 | 0.0041 | 0.0003175 | 0.414 [0.391, 0.434] | suppressed | parity to 1.3x loss |  | openmp runtime linked | 15.9 |  |

> comoment3 mt8-repro-native: inference suppressed -- trend: blade-cpp first-half vs second-half median moved 7.2%; trend: gfortran-expert first-half vs second-half median moved 8.4%

| gram | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | in-order |  |  | LLVM lane has no OpenMP |
| gram | blade-cpp | ok | 21 | 0.01825 | 0.01914 | 0.00122 | ref |  |  | in-order | openmp (1 construct(s)); threaded maps, serial folds (BLADE_FP_REPRO) | 32.3 |  |
| gram | gfortran-natural | ok | 21 | 0.4627 | 0.5529 | 0.1003 | 28.891 [27.082, 33.014] | suppressed | win 1.5-2x if matmul is a naive nest; parity/loss if a blocked runtime kernel |  | openmp runtime linked | 37.4 | matmul is unannotated: parallel preparation, serial kernel unless the runtime threads matmul (FORT records) |
| gram | gfortran-expert | ok | 21 | 0.01926 | 0.02066 | 0.001002 | 1.080 [1.048, 1.124] | suppressed | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | openmp runtime linked | 52.2 |  |
| gram | ifx-natural | ok | 21 | 0.583 | 0.6163 | 0.02466 | 32.206 [31.432, 33.272] | suppressed | win 1.5-2x if matmul is a naive nest; parity/loss if a blocked runtime kernel |  | openmp runtime linked | 58.4 | matmul is unannotated: parallel preparation, serial kernel unless the runtime threads matmul (FORT records) |
| gram | ifx-expert | ok | 21 | 0.01867 | 0.01981 | 0.001247 | 1.035 [1.002, 1.075] | suppressed | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | openmp runtime linked | 53.8 |  |

> gram mt8-repro-native: inference suppressed -- trend: gfortran-natural first-half vs second-half median moved 17.8%

| pairkernel | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | in-order |  |  | LLVM lane has no OpenMP |
| pairkernel | blade-cpp | ok | 21 | 0.01657 | 0.01734 | 0.0005691 | ref |  |  | in-order | openmp (2 construct(s)); threaded maps, serial folds (BLADE_FP_REPRO) | 29.8 |  |
| pairkernel | gfortran-natural | ok | 21 | 0.01314 | 0.01555 | 0.00376 | 0.897 [0.782, 0.942] | suppressed | WIN ~2x (half the transcendentals) |  | openmp runtime linked | 53.4 |  |
| pairkernel | gfortran-expert | ok | 21 | 0.005907 | 0.006217 | 0.0004662 | 0.359 [0.351, 0.378] | suppressed | parity within 5% |  | openmp runtime linked | 29.5 |  |
| pairkernel | ifx-natural | ok | 21 | 0.01491 | 0.01696 | 0.001521 | 0.978 [0.931, 1.016] | suppressed | WIN ~2x (half the transcendentals) |  | openmp runtime linked | 54.9 |  |
| pairkernel | ifx-expert | ok | 21 | 0.005962 | 0.006472 | 0.0004471 | 0.373 [0.354, 0.380] | suppressed | parity within 5% |  | openmp runtime linked | 31.0 |  |

> pairkernel mt8-repro-native: inference suppressed -- trend: gfortran-natural first-half vs second-half median moved 14.6%; trend: gfortran-expert first-half vs second-half median moved 5.9%; trend: ifx-natural first-half vs second-half median moved 8.5%; trend: control memory moved 7.1% within the block

| recur | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | in-order |  |  | LLVM lane has no OpenMP |
| recur | blade-cpp | no-parallel-form | 0 | -- | -- | -- |  |  |  | in-order |  |  | kernel.json no_parallel_form (spec: no licensed parallel spelling) |
| recur | gfortran-natural | ok | 21 | 0.07111 | 0.07323 | 0.001513 |  |  | parity |  | openmp runtime linked | 52.1 |  |
| recur | gfortran-expert | ok | 21 | 0.006527 | 0.007408 | 0.0006819 |  |  | LOSS 1.5-3x |  | openmp runtime linked | 51.5 |  |
| recur | ifx-natural | ok | 21 | 0.01449 | 0.01546 | 0.001357 |  |  | parity |  | openmp runtime linked | 53.6 |  |
| recur | ifx-expert | ok | 21 | 0.006938 | 0.007795 | 0.0008657 |  |  | LOSS 1.5-3x |  | openmp runtime linked | 53.0 |  |

> recur mt8-repro-native: inference suppressed -- trend: ifx-natural first-half vs second-half median moved 6.5%; trend: ifx-expert first-half vs second-half median moved 5.4%; trend: control memory moved 5.4% within the block


## Controls

- compute (recur/gfortran-expert/serial-repro-native/full): n=126 median 0.01293 s, baseline 0.01292 s
- memory (saxpy/gfortran-expert/serial-repro-native/control): n=126 median 0.005303 s, baseline 0.005352 s
