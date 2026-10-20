# Blade vs Fortran -- bench session 2026-10-19T14:59:30

- host LAPTOP-350H6LDB | AMD Ryzen 7 5800HS with Radeon Graphics | 8C/16T | 15.41 GB | power ac | max processor state AC 100% | boost AC None | Power Scheme GUID: 64a64f24-65b9-4b56-befd-5ec1eaced9b3  (Silent)
- git 39a8a869ff410c79edbe599a98257d4012241702 | Blade.dll bb52a4c2a008d198 | GNU Fortran (Rev4, Built by MSYS2 project) 16.2.0 | Intel(R) Fortran Compiler for applications running on Intel(R) 64, Version 2026.1.0 Build 20260617 | clang version 22.1.8 (https://github.com/msys2/MINGW-packages d34feaaa1d0aff624c870910b6f2589aa40d20de)
- size `full` | duration 1895.6 s | pins: pins-A.json, pins-B.json
- PRIMARY estimand: the complete timed region. ratio = median(arm) / median(Blade reference), round-block bootstrap 95% CI (10000 resamples); ratio > 1 = Blade faster. Verdicts (delta 0.1): win / detectable-win / equivalent / detectable-loss / loss / inconclusive; `suppressed` = the block failed a drift, trend or control check. Intervals are pointwise; no suite-wide claim. incremental = median(arm) - median(its own input-only twin), NOT kernel time. Reference: blade-llvm (serial), blade-cpp (mt8, or when the LLVM lane refused). Fresh-process, allocation-inclusive measurements.
- **FLAG**: band/mt8-repro-native: inference suppressed: trend: gfortran-expert first-half vs second-half median moved 12.9%; trend: ifx-natural first-half vs second-half median moved 10.0%; trend: ifx-expert first-half vs second-half median moved 5.3%
- **FLAG**: gram/mt8-repro-native: inference suppressed: trend: control memory moved 8.4% within the block
- **FLAG**: fusedchain/mt8-repro-native: inference suppressed: trend: gfortran-expert first-half vs second-half median moved 5.8%
- **FLAG**: onepass/mt8-repro-native: inference suppressed: trend: gfortran-natural-inputonly first-half vs second-half median moved 7.2%; trend: ifx-natural-inputonly first-half vs second-half median moved 5.9%; trend: control memory moved 5.1% within the block
- **FLAG**: recur/mt8-repro-native: inference suppressed: trend: gfortran-expert first-half vs second-half median moved 7.3%; trend: ifx-expert first-half vs second-half median moved 5.3%
- **FLAG**: stencil/mt8-repro-native: inference suppressed: trend: gfortran-natural first-half vs second-half median moved 6.6%; trend: blade-cpp-inputonly first-half vs second-half median moved 6.2%
- **FLAG**: dot/mt8-repro-native: inference suppressed: trend: ifx-natural first-half vs second-half median moved 7.2%; trend: gfortran-expert-inputonly first-half vs second-half median moved 6.7%
- **FLAG**: saxpy/mt8-repro-native: inference suppressed: trend: gfortran-natural-inputonly first-half vs second-half median moved 5.9%; trend: gfortran-expert-inputonly first-half vs second-half median moved 6.1%; trend: ifx-expert-inputonly first-half vs second-half median moved 11.5%

## mt8-repro-native

Blade reference per kernel: antisym=blade-cpp, band=blade-cpp, comoment3=blade-cpp, gram=blade-cpp, pairkernel=blade-cpp, fusedchain=blade-cpp, onepass=None, recur=None, stencil=blade-cpp, dot=blade-cpp, saxpy=blade-cpp

| kernel | arm | status | n | min (s) | median (s) | IQR (s) | ratio vs ref [95% CI] | verdict | hypothesis | fold form | parallelism | peak WS (MB) | incremental (s) [CI] | incremental ratio [CI] | notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| antisym | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | LLVM lane has no OpenMP |
| antisym | blade-cpp | ok | 21 | 0.01057 | 0.01122 | 0.0009617 | ref |  |  | in-order | openmp (1 construct(s)); threaded maps, serial folds (BLADE_FP_REPRO) | 26.4 |  |  |  |
| antisym | gfortran-natural | ok | 21 | 0.1036 | 0.1109 | 0.00243 | 9.887 [9.497, 10.319] | win | WIN ~2x (half the cells, no diagonal) |  | openmp runtime linked | 23.9 |  |  |  |
| antisym | gfortran-expert | ok | 21 | 0.01026 | 0.01115 | 0.0006328 | 0.993 [0.959, 1.049] | equivalent | parity +-15% |  | openmp runtime linked | 33.2 |  |  |  |
| antisym | ifx-natural | ok | 21 | 0.05381 | 0.06522 | 0.006364 | 5.812 [5.486, 6.130] | win | WIN ~2x (half the cells, no diagonal) |  | openmp runtime linked | 25.4 |  |  |  |
| antisym | ifx-expert | ok | 21 | 0.01042 | 0.01122 | 0.0006118 | 1.000 [0.951, 1.049] | equivalent | parity +-15% |  | openmp runtime linked | 34.7 |  |  |  |
| band | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | LLVM lane has no OpenMP |
| band | blade-cpp | ok | 21 | 0.004505 | 0.004678 | 0.0002032 | ref |  |  | in-order | openmp (1 construct(s)); threaded maps, serial folds (BLADE_FP_REPRO) | 6.0 |  |  |  |
| band | gfortran-natural | ok | 21 | 0.03545 | 0.04708 | 0.0077 | 10.065 [9.411, 10.539] | suppressed | win 10x+ (the natural tier is the n^2 predicate nest, a straw man by construction) |  | openmp runtime linked | 5.8 |  |  |  |
| band | gfortran-expert | ok | 21 | 0.001256 | 0.001411 | 0.0002465 | 0.302 [0.276, 0.331] | suppressed | parity in both lanes: licensed folds walk the band by diagonal (k = i - j outer) as the licensed expert does, with phi hoisted per diagonal by the back end (G33); repro keeps the lex order with the terms vectorized and summed in order (G30) |  | openmp runtime linked | 5.8 |  |  |  |
| band | ifx-natural | ok | 21 | 0.0264 | 0.03601 | 0.008499 | 7.699 [7.107, 8.600] | suppressed | win 10x+ (the natural tier is the n^2 predicate nest, a straw man by construction) |  | openmp runtime linked | 7.4 |  |  |  |
| band | ifx-expert | ok | 21 | 0.002077 | 0.002431 | 0.0001883 | 0.520 [0.504, 0.541] | suppressed | parity in both lanes: licensed folds walk the band by diagonal (k = i - j outer) as the licensed expert does, with phi hoisted per diagonal by the back end (G33); repro keeps the lex order with the terms vectorized and summed in order (G30) |  | openmp runtime linked | 7.4 |  |  |  |

> band mt8-repro-native: inference suppressed -- trend: gfortran-expert first-half vs second-half median moved 12.9%; trend: ifx-natural first-half vs second-half median moved 10.0%; trend: ifx-expert first-half vs second-half median moved 5.3%

| comoment3 | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | LLVM lane has no OpenMP |
| comoment3 | blade-cpp | ok | 21 | 0.01017 | 0.01082 | 0.0004468 | ref |  |  | in-order | openmp (1 construct(s)); threaded maps, serial folds (BLADE_FP_REPRO) | 13.2 |  |  |  |
| comoment3 | gfortran-natural | ok | 21 | 0.08795 | 0.09382 | 0.004045 | 8.672 [8.434, 8.942] | win | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | openmp runtime linked | 44.0 |  |  |  |
| comoment3 | gfortran-expert | ok | 21 | 0.004766 | 0.005171 | 0.0005916 | 0.478 [0.464, 0.525] | loss | parity to 1.3x loss |  | openmp runtime linked | 14.6 |  |  |  |
| comoment3 | ifx-natural | ok | 21 | 0.1112 | 0.1176 | 0.006869 | 10.870 [10.654, 11.320] | win | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | openmp runtime linked | 45.5 |  |  |  |
| comoment3 | ifx-expert | ok | 21 | 0.004149 | 0.004615 | 0.0002893 | 0.427 [0.412, 0.446] | loss | parity to 1.3x loss |  | openmp runtime linked | 15.9 |  |  |  |
| gram | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | LLVM lane has no OpenMP |
| gram | blade-cpp | ok | 21 | 0.02084 | 0.02203 | 0.001235 | ref |  |  | in-order | openmp (1 construct(s)); threaded maps, serial folds (BLADE_FP_REPRO) | 32.3 |  |  |  |
| gram | gfortran-natural | ok | 21 | 0.5487 | 0.7022 | 0.1776 | 31.870 [28.226, 34.089] | suppressed | win 1.5-2x if matmul is a naive nest; parity/loss if a blocked runtime kernel |  | openmp runtime linked | 37.4 |  |  | matmul is unannotated: parallel preparation, serial kernel unless the runtime threads matmul (FORT records) |
| gram | gfortran-expert | ok | 21 | 0.0228 | 0.02464 | 0.001639 | 1.119 [1.070, 1.161] | suppressed | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | openmp runtime linked | 52.2 |  |  |  |
| gram | ifx-natural | ok | 21 | 0.5902 | 0.6204 | 0.01096 | 28.158 [27.430, 28.508] | suppressed | win 1.5-2x if matmul is a naive nest; parity/loss if a blocked runtime kernel |  | openmp runtime linked | 58.4 |  |  | matmul is unannotated: parallel preparation, serial kernel unless the runtime threads matmul (FORT records) |
| gram | ifx-expert | ok | 21 | 0.02059 | 0.02283 | 0.001446 | 1.036 [0.989, 1.058] | suppressed | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | openmp runtime linked | 53.8 |  |  |  |

> gram mt8-repro-native: inference suppressed -- trend: control memory moved 8.4% within the block

| pairkernel | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | LLVM lane has no OpenMP |
| pairkernel | blade-cpp | ok | 21 | 0.01763 | 0.01816 | 0.0004165 | ref |  |  | in-order | openmp (2 construct(s)); threaded maps, serial folds (BLADE_FP_REPRO) | 29.9 |  |  |  |
| pairkernel | gfortran-natural | ok | 21 | 0.01441 | 0.01831 | 0.002162 | 1.008 [0.910, 1.033] | equivalent | WIN ~2x (half the transcendentals) |  | openmp runtime linked | 53.4 |  |  |  |
| pairkernel | gfortran-expert | ok | 21 | 0.006008 | 0.00672 | 0.0004822 | 0.370 [0.354, 0.381] | loss | parity within 5% |  | openmp runtime linked | 29.5 |  |  |  |
| pairkernel | ifx-natural | ok | 21 | 0.0157 | 0.01819 | 0.001019 | 1.002 [0.983, 1.037] | equivalent | WIN ~2x (half the transcendentals) |  | openmp runtime linked | 54.9 |  |  |  |
| pairkernel | ifx-expert | ok | 21 | 0.005884 | 0.006515 | 0.0003568 | 0.359 [0.348, 0.365] | loss | parity within 5% |  | openmp runtime linked | 31.0 |  |  |  |
| fusedchain | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | LLVM lane has no OpenMP |
| fusedchain | blade-cpp | ok | 21 | 0.005174 | 0.005856 | 0.0007811 | ref |  |  | in-order | openmp (1 construct(s)); threaded maps, serial folds (BLADE_FP_REPRO) | 21.1 | 0.001659 [0.001319, 0.002038] | ref |  |
| fusedchain | gfortran-natural | ok | 21 | 0.01181 | 0.01292 | 0.0003552 | 2.206 [2.102, 2.341] | suppressed | WIN 1.5-2.5x (no temporaries) |  | openmp runtime linked | 66.6 | 0.008852 [0.008542, 0.009197] | 5.337 [4.375, 6.885] |  |
| fusedchain | gfortran-expert | ok | 21 | 0.004011 | 0.004407 | 0.0006539 | 0.753 [0.701, 0.827] | suppressed | parity |  | openmp runtime linked | 20.8 | 0.0006302 [0.0001308, 0.000995] | 0.380 [0.084, 0.616] |  |
| fusedchain | ifx-natural | ok | 21 | 0.01181 | 0.01306 | 0.0005166 | 2.231 [2.093, 2.376] | suppressed | WIN 1.5-2.5x (no temporaries) |  | openmp runtime linked | 68.2 | 0.008926 [0.008498, 0.009097] | 5.382 [4.296, 6.786] |  |
| fusedchain | ifx-expert | ok | 21 | 0.004385 | 0.004828 | 0.0005652 | 0.825 [0.776, 0.918] | suppressed | parity |  | openmp runtime linked | 22.3 | 0.0005532 [0.0003772, 0.0009872] | 0.334 [0.204, 0.686] |  |
| fusedchain | blade-llvm-inputonly | serial-only | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | LLVM lane has no OpenMP |
| fusedchain | blade-cpp-inputonly | ok | 21 | 0.003668 | 0.004197 | 0.0005134 |  |  |  | in-order | openmp (1 construct(s)); threaded maps, serial folds (BLADE_FP_REPRO) | 21.1 |  |  |  |
| fusedchain | gfortran-natural-inputonly | ok | 21 | 0.003388 | 0.004066 | 0.0005983 |  |  |  |  | openmp runtime linked | 20.8 |  |  |  |
| fusedchain | gfortran-expert-inputonly | ok | 21 | 0.00336 | 0.003777 | 0.0005555 |  |  |  |  | openmp runtime linked | 20.8 |  |  |  |
| fusedchain | ifx-natural-inputonly | ok | 21 | 0.003861 | 0.004135 | 0.0005712 |  |  |  |  | openmp runtime linked | 22.3 |  |  |  |
| fusedchain | ifx-expert-inputonly | ok | 21 | 0.003893 | 0.004275 | 0.0004645 |  |  |  |  | openmp runtime linked | 22.3 |  |  |  |

> fusedchain mt8-repro-native: inference suppressed -- trend: gfortran-expert first-half vs second-half median moved 5.8%

| onepass | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | LLVM lane has no OpenMP |
| onepass | blade-cpp | no-parallel-form | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | kernel.json no_parallel_form (spec: no licensed parallel spelling) |
| onepass | gfortran-natural | ok | 21 | 0.01525 | 0.01668 | 0.001081 |  |  | WIN 2-3x (one pass vs four) |  | openmp runtime linked | 43.7 |  |  |  |
| onepass | gfortran-expert | ok | 21 | 0.01085 | 0.01189 | 0.0009922 |  |  | parity |  | openmp runtime linked | 43.7 |  |  |  |
| onepass | ifx-natural | ok | 21 | 0.01569 | 0.01651 | 0.001209 |  |  | WIN 2-3x (one pass vs four) |  | openmp runtime linked | 45.2 |  |  |  |
| onepass | ifx-expert | ok | 21 | 0.01115 | 0.01191 | 0.00096 |  |  | parity |  | openmp runtime linked | 45.2 |  |  |  |
| onepass | blade-llvm-inputonly | serial-only | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | LLVM lane has no OpenMP |
| onepass | blade-cpp-inputonly | no-parallel-form | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | kernel.json no_parallel_form (spec: no licensed parallel spelling) |
| onepass | gfortran-natural-inputonly | ok | 21 | 0.008299 | 0.009749 | 0.001003 |  |  |  |  | openmp runtime linked | 43.7 |  |  |  |
| onepass | gfortran-expert-inputonly | ok | 21 | 0.008222 | 0.009391 | 0.0008087 |  |  |  |  | openmp runtime linked | 43.7 |  |  |  |
| onepass | ifx-natural-inputonly | ok | 21 | 0.008543 | 0.009092 | 0.0009962 |  |  |  |  | openmp runtime linked | 45.2 |  |  |  |
| onepass | ifx-expert-inputonly | ok | 21 | 0.008656 | 0.01016 | 0.0008359 |  |  |  |  | openmp runtime linked | 45.2 |  |  |  |

> onepass mt8-repro-native: inference suppressed -- trend: gfortran-natural-inputonly first-half vs second-half median moved 7.2%; trend: ifx-natural-inputonly first-half vs second-half median moved 5.9%; trend: control memory moved 5.1% within the block

| recur | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | LLVM lane has no OpenMP |
| recur | blade-cpp | no-parallel-form | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | kernel.json no_parallel_form (spec: no licensed parallel spelling) |
| recur | gfortran-natural | ok | 21 | 0.07092 | 0.07215 | 0.001349 |  |  | parity |  | openmp runtime linked | 52.1 |  |  |  |
| recur | gfortran-expert | ok | 21 | 0.006867 | 0.008005 | 0.0009834 |  |  | LOSS 1.5-3x |  | openmp runtime linked | 51.5 |  |  |  |
| recur | ifx-natural | ok | 21 | 0.01543 | 0.01627 | 0.0007918 |  |  | parity |  | openmp runtime linked | 53.6 |  |  |  |
| recur | ifx-expert | ok | 21 | 0.007195 | 0.008117 | 0.0007945 |  |  | LOSS 1.5-3x |  | openmp runtime linked | 53.0 |  |  |  |

> recur mt8-repro-native: inference suppressed -- trend: gfortran-expert first-half vs second-half median moved 7.3%; trend: ifx-expert first-half vs second-half median moved 5.3%

| stencil | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | LLVM lane has no OpenMP |
| stencil | blade-cpp | ok | 21 | 0.009642 | 0.01043 | 0.0007131 | ref |  |  | in-order | openmp (2 construct(s)); threaded maps, serial folds (BLADE_FP_REPRO) | 51.4 | 0.004391 [0.004114, 0.004873] | ref |  |
| stencil | gfortran-natural | ok | 21 | 0.00861 | 0.009713 | 0.000795 | 0.932 [0.876, 0.958] | suppressed | parity (bandwidth-bound) |  | openmp runtime linked | 51.4 | 0.00493 [0.004364, 0.005252] | 1.123 [0.949, 1.207] |  |
| stencil | gfortran-expert | ok | 21 | 0.006574 | 0.008492 | 0.001007 | 0.815 [0.738, 0.864] | suppressed | parity |  | openmp runtime linked | 51.4 | 0.00345 [0.002898, 0.004032] | 0.786 [0.637, 0.923] |  |
| stencil | ifx-natural | ok | 21 | 0.009962 | 0.01115 | 0.0008055 | 1.070 [1.008, 1.102] | suppressed | parity (bandwidth-bound) |  | openmp runtime linked | 53.0 | 0.006005 [0.005494, 0.0064] | 1.367 [1.195, 1.477] |  |
| stencil | ifx-expert | ok | 21 | 0.008269 | 0.009284 | 0.0003973 | 0.891 [0.854, 0.918] | suppressed | parity |  | openmp runtime linked | 53.0 | 0.004205 [0.003792, 0.004456] | 0.958 [0.818, 1.063] |  |
| stencil | blade-llvm-inputonly | serial-only | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | LLVM lane has no OpenMP |
| stencil | blade-cpp-inputonly | ok | 21 | 0.005315 | 0.006034 | 0.0005678 |  |  |  | in-order | openmp (1 construct(s)); threaded maps, serial folds (BLADE_FP_REPRO) | 28.7 |  |  |  |
| stencil | gfortran-natural-inputonly | ok | 21 | 0.004255 | 0.004783 | 0.0005229 |  |  |  |  | openmp runtime linked | 28.5 |  |  |  |
| stencil | gfortran-expert-inputonly | ok | 21 | 0.004147 | 0.005042 | 0.000761 |  |  |  |  | openmp runtime linked | 28.5 |  |  |  |
| stencil | ifx-natural-inputonly | ok | 21 | 0.004524 | 0.005145 | 0.0006983 |  |  |  |  | openmp runtime linked | 30.0 |  |  |  |
| stencil | ifx-expert-inputonly | ok | 21 | 0.004435 | 0.00508 | 0.0006057 |  |  |  |  | openmp runtime linked | 30.0 |  |  |  |

> stencil mt8-repro-native: inference suppressed -- trend: gfortran-natural first-half vs second-half median moved 6.6%; trend: blade-cpp-inputonly first-half vs second-half median moved 6.2%

| dot | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | LLVM lane has no OpenMP |
| dot | blade-cpp | ok | 21 | 0.01405 | 0.01482 | 0.0005134 | ref |  |  | in-order | openmp (1 construct(s)); threaded maps, serial folds (BLADE_FP_REPRO) | 51.6 | 0.002584 [0.00242, 0.003454] | ref |  |
| dot | gfortran-natural | ok | 21 | 0.0111 | 0.01244 | 0.000789 | 0.839 [0.799, 0.851] | suppressed | parity |  | openmp runtime linked | 51.3 | 0.002315 [0.001646, 0.002989] | 0.896 [0.529, 1.146] |  |
| dot | gfortran-expert | ok | 21 | 0.01104 | 0.01233 | 0.001191 | 0.832 [0.768, 0.851] | suppressed | parity |  | openmp runtime linked | 51.3 | 0.002625 [0.001623, 0.003078] | 1.016 [0.510, 1.218] |  |
| dot | ifx-natural | ok | 21 | 0.01152 | 0.01232 | 0.001183 | 0.831 [0.789, 0.869] | suppressed | parity |  | openmp runtime linked | 52.9 | 0.001904 [0.001206, 0.00284] | 0.737 [0.403, 1.102] |  |
| dot | ifx-expert | ok | 21 | 0.01143 | 0.01248 | 0.0009939 | 0.842 [0.805, 0.874] | suppressed | parity |  | openmp runtime linked | 52.9 | 0.001733 [0.001282, 0.002551] | 0.671 [0.406, 0.982] |  |
| dot | blade-llvm-inputonly | serial-only | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | LLVM lane has no OpenMP |
| dot | blade-cpp-inputonly | ok | 21 | 0.0108 | 0.01224 | 0.001085 |  |  |  | in-order | openmp (1 construct(s)); threaded maps, serial folds (BLADE_FP_REPRO) | 51.6 |  |  |  |
| dot | gfortran-natural-inputonly | ok | 21 | 0.008247 | 0.01013 | 0.001071 |  |  |  |  | openmp runtime linked | 51.3 |  |  |  |
| dot | gfortran-expert-inputonly | ok | 21 | 0.008383 | 0.00971 | 0.0009161 |  |  |  |  | openmp runtime linked | 51.3 |  |  |  |
| dot | ifx-natural-inputonly | ok | 21 | 0.008735 | 0.01041 | 0.0009274 |  |  |  |  | openmp runtime linked | 52.9 |  |  |  |
| dot | ifx-expert-inputonly | ok | 21 | 0.008904 | 0.01075 | 0.001087 |  |  |  |  | openmp runtime linked | 52.9 |  |  |  |

> dot mt8-repro-native: inference suppressed -- trend: ifx-natural first-half vs second-half median moved 7.2%; trend: gfortran-expert-inputonly first-half vs second-half median moved 6.7%

| saxpy | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | LLVM lane has no OpenMP |
| saxpy | blade-cpp | ok | 21 | 0.012 | 0.01353 | 0.001094 | ref |  |  | in-order | openmp (2 construct(s)); threaded maps, serial folds (BLADE_FP_REPRO) | 51.3 | 0.005397 [0.004442, 0.00594] | ref |  |
| saxpy | gfortran-natural | ok | 21 | 0.009977 | 0.01128 | 0.0006992 | 0.834 [0.812, 0.886] | suppressed | parity |  | openmp runtime linked | 51.3 | 0.00436 [0.003931, 0.005142] | 0.808 [0.705, 1.053] |  |
| saxpy | gfortran-expert | ok | 21 | 0.00837 | 0.01035 | 0.0007955 | 0.765 [0.718, 0.817] | suppressed | parity |  | openmp runtime linked | 51.3 | 0.003973 [0.003301, 0.004505] | 0.736 [0.580, 0.939] |  |
| saxpy | ifx-natural | ok | 21 | 0.01025 | 0.01138 | 0.0007592 | 0.841 [0.813, 0.896] | suppressed | parity |  | openmp runtime linked | 53.0 | 0.004333 [0.003873, 0.004657] | 0.803 [0.681, 1.009] |  |
| saxpy | ifx-expert | ok | 21 | 0.00934 | 0.01013 | 0.0009508 | 0.748 [0.725, 0.834] | suppressed | parity |  | openmp runtime linked | 52.9 | 0.003439 [0.002804, 0.004412] | 0.637 [0.489, 0.918] |  |
| saxpy | blade-llvm-inputonly | serial-only | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | LLVM lane has no OpenMP |
| saxpy | blade-cpp-inputonly | ok | 21 | 0.00682 | 0.008137 | 0.001018 |  |  |  | in-order | openmp (1 construct(s)); threaded maps, serial folds (BLADE_FP_REPRO) | 36.3 |  |  |  |
| saxpy | gfortran-natural-inputonly | ok | 21 | 0.005369 | 0.006923 | 0.00131 |  |  |  |  | openmp runtime linked | 36.1 |  |  |  |
| saxpy | gfortran-expert-inputonly | ok | 21 | 0.005472 | 0.006378 | 0.001126 |  |  |  |  | openmp runtime linked | 36.1 |  |  |  |
| saxpy | ifx-natural-inputonly | ok | 21 | 0.006015 | 0.007044 | 0.0008532 |  |  |  |  | openmp runtime linked | 37.6 |  |  |  |
| saxpy | ifx-expert-inputonly | ok | 21 | 0.005764 | 0.006687 | 0.001245 |  |  |  |  | openmp runtime linked | 37.6 |  |  |  |

> saxpy mt8-repro-native: inference suppressed -- trend: gfortran-natural-inputonly first-half vs second-half median moved 5.9%; trend: gfortran-expert-inputonly first-half vs second-half median moved 6.1%; trend: ifx-expert-inputonly first-half vs second-half median moved 11.5%


## Controls

- compute (recur/gfortran-expert/serial-repro-native/full): n=231 median 0.0136 s, baseline 0.01351 s
- memory (saxpy/gfortran-expert/serial-repro-native/control): n=231 median 0.005561 s, baseline 0.005546 s
