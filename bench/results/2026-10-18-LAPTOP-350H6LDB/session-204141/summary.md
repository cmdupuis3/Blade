# Blade vs Fortran -- bench session 2026-10-18T20:41:41

- host LAPTOP-350H6LDB | AMD Ryzen 7 5800HS with Radeon Graphics | 8C/16T | 15.41 GB | power ac | max processor state AC 100% | boost AC None | Power Scheme GUID: 64a64f24-65b9-4b56-befd-5ec1eaced9b3  (Silent)
- git 05f95277a0dbda3b2e9e98003bd5fb2c34f0e225 | Blade.dll 79af7edce32999db | GNU Fortran (Rev4, Built by MSYS2 project) 16.2.0 | Intel(R) Fortran Compiler for applications running on Intel(R) 64, Version 2026.1.0 Build 20260617 | clang version 22.1.8 (https://github.com/msys2/MINGW-packages d34feaaa1d0aff624c870910b6f2589aa40d20de)
- size `full` | duration 622.6 s | pins: pins-A.json, pins-B.json
- PRIMARY estimand: the complete timed region. ratio = median(arm) / median(Blade reference), round-block bootstrap 95% CI (10000 resamples); ratio > 1 = Blade faster. Verdicts (delta 0.1): win / detectable-win / equivalent / detectable-loss / loss / inconclusive; `suppressed` = the block failed a drift, trend or control check. Intervals are pointwise; no suite-wide claim. incremental = median(arm) - median(its own input-only twin), NOT kernel time. Reference: blade-llvm (serial), blade-cpp (mt8, or when the LLVM lane refused). Fresh-process, allocation-inclusive measurements.

## serial-repro-native

Blade reference per kernel: antisym=blade-llvm, band=blade-llvm, comoment3=blade-llvm, gram=blade-llvm, pairkernel=blade-llvm, recur=blade-llvm

| kernel | arm | status | n | min (s) | median (s) | IQR (s) | ratio vs ref [95% CI] | verdict | hypothesis | fold form | parallelism | peak WS (MB) | notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| antisym | blade-llvm | ok | 11 | 0.5038 | 0.5059 | 0.001675 | ref |  |  | in-order | serial | 17.5 |  |
| antisym | blade-cpp | ok | 11 | 0.02949 | 0.02978 | 0.0002229 | 0.059 [0.058, 0.059] | loss |  | in-order | serial | 25.7 |  |
| antisym | gfortran-natural | ok | 11 | 0.7655 | 0.7692 | 0.001729 | 1.521 [1.517, 1.525] | win | WIN ~2x (half the cells, no diagonal) |  | serial | 23.5 |  |
| antisym | gfortran-expert | ok | 11 | 0.03046 | 0.03083 | 0.0002602 | 0.061 [0.061, 0.061] | loss | parity +-15% |  | serial | 32.7 |  |
| antisym | ifx-natural | ok | 11 | 0.329 | 0.3308 | 0.002387 | 0.654 [0.653, 0.659] | loss | WIN ~2x (half the cells, no diagonal) |  | serial | 23.0 |  |
| antisym | ifx-expert | ok | 11 | 0.02924 | 0.02955 | 0.0004349 | 0.058 [0.058, 0.059] | loss | parity +-15% |  | serial | 32.3 |  |
| band | blade-llvm | ok | 21 | 0.00925 | 0.009277 | 4.81e-05 | ref |  |  | in-order | serial | 4.8 |  |
| band | blade-cpp | ok | 21 | 0.009272 | 0.009289 | 3.8e-05 | 1.001 [0.998, 1.005] | equivalent |  | in-order | serial | 5.5 |  |
| band | gfortran-natural | ok | 21 | 0.2062 | 0.2069 | 0.0008428 | 22.304 [22.241, 22.373] | win | win 10x+ (the natural tier is the n^2 predicate nest, a straw man by construction) |  | serial | 5.4 |  |
| band | gfortran-expert | ok | 21 | 0.003104 | 0.00311 | 1.82e-05 | 0.335 [0.334, 0.336] | loss | parity on blade-llvm (vectorized inner loop); LOSS on blade-cpp (inner loop scalar, G30) |  | serial | 5.4 |  |
| band | ifx-natural | ok | 21 | 0.349 | 0.351 | 0.00194 | 37.841 [37.707, 37.941] | win | win 10x+ (the natural tier is the n^2 predicate nest, a straw man by construction) |  | serial | 5.1 |  |
| band | ifx-expert | ok | 21 | 0.003701 | 0.003718 | 2.22e-05 | 0.401 [0.399, 0.402] | loss | parity on blade-llvm (vectorized inner loop); LOSS on blade-cpp (inner loop scalar, G30) |  | serial | 5.0 |  |
| comoment3 | blade-llvm | ok | 11 | 0.1562 | 0.1567 | 0.003557 | ref |  |  | in-order | serial | 11.3 |  |
| comoment3 | blade-cpp | ok | 11 | 0.01554 | 0.01588 | 0.0005634 | 0.101 [0.097, 0.103] | loss |  | in-order | serial | 12.7 |  |
| comoment3 | gfortran-natural | ok | 11 | 0.5998 | 0.6011 | 0.002748 | 3.837 [3.695, 3.855] | win | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | serial | 43.5 |  |
| comoment3 | gfortran-expert | ok | 11 | 0.01125 | 0.01142 | 0.0001694 | 0.073 [0.070, 0.074] | loss | parity to 1.3x loss |  | serial | 12.3 |  |
| comoment3 | ifx-natural | ok | 11 | 0.717 | 0.7195 | 0.001729 | 4.593 [4.424, 4.604] | win | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | serial | 43.1 |  |
| comoment3 | ifx-expert | ok | 11 | 0.01128 | 0.0115 | 0.0002424 | 0.073 [0.071, 0.074] | loss | parity to 1.3x loss |  | serial | 11.9 |  |
| gram | blade-llvm | ok | 11 | 0.05896 | 0.05973 | 0.001879 | ref |  |  | in-order | serial | 30.7 |  |
| gram | blade-cpp | ok | 11 | 0.0601 | 0.06134 | 0.002877 | 1.027 [0.979, 1.076] | equivalent |  | in-order | serial | 31.5 |  |
| gram | gfortran-natural | ok | 11 | 0.5625 | 0.5934 | 0.05366 | 9.935 [9.462, 10.592] | win | win 1.5-2x if matmul is a naive nest; parity/loss if a blocked runtime kernel |  | serial | 36.9 |  |
| gram | gfortran-expert | ok | 11 | 0.07631 | 0.07707 | 0.001744 | 1.290 [1.230, 1.321] | win | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | serial | 51.7 |  |
| gram | ifx-natural | ok | 11 | 0.5673 | 0.5683 | 0.002758 | 9.515 [9.081, 9.566] | win | win 1.5-2x if matmul is a naive nest; parity/loss if a blocked runtime kernel |  | serial | 56.0 |  |
| gram | ifx-expert | ok | 11 | 0.06962 | 0.07012 | 0.0007632 | 1.174 [1.119, 1.186] | win | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | serial | 51.3 |  |
| pairkernel | blade-llvm | ok | 21 | 0.04008 | 0.04082 | 0.0006547 | ref |  |  | in-order | serial | 28.6 |  |
| pairkernel | blade-cpp | ok | 21 | 0.03699 | 0.03785 | 0.001651 | 0.927 [0.918, 0.948] | detectable-loss |  | in-order | serial | 29.4 |  |
| pairkernel | gfortran-natural | ok | 21 | 0.06003 | 0.0608 | 0.0005384 | 1.489 [1.481, 1.503] | win | WIN ~2x (half the transcendentals) |  | serial | 53.0 |  |
| pairkernel | gfortran-expert | ok | 21 | 0.03397 | 0.03432 | 0.0004576 | 0.841 [0.836, 0.852] | loss | parity within 5% |  | serial | 29.1 |  |
| pairkernel | ifx-natural | ok | 21 | 0.06284 | 0.06391 | 0.0006239 | 1.565 [1.557, 1.584] | win | WIN ~2x (half the transcendentals) |  | serial | 52.5 |  |
| pairkernel | ifx-expert | ok | 21 | 0.03139 | 0.03191 | 0.0003441 | 0.782 [0.777, 0.790] | loss | parity within 5% |  | serial | 28.6 |  |
| recur | blade-llvm | ok | 21 | 0.02312 | 0.02377 | 0.0005939 | ref |  |  | in-order | serial | 51.5 |  |
| recur | blade-cpp | ok | 21 | 0.02322 | 0.02385 | 0.0004361 | 1.004 [0.989, 1.019] | equivalent |  | in-order | serial | 52.5 |  |
| recur | gfortran-natural | ok | 21 | 0.01748 | 0.01779 | 0.0004119 | 0.748 [0.740, 0.764] | loss | parity |  | serial | 51.7 |  |
| recur | gfortran-expert | ok | 21 | 0.01253 | 0.01295 | 0.000279 | 0.545 [0.538, 0.554] | loss | LOSS 1.5-3x |  | serial | 51.0 |  |
| recur | ifx-natural | ok | 21 | 0.01434 | 0.01488 | 0.0003183 | 0.626 [0.618, 0.637] | loss | parity |  | serial | 51.2 |  |
| recur | ifx-expert | ok | 21 | 0.01289 | 0.01334 | 0.000282 | 0.561 [0.552, 0.571] | loss | LOSS 1.5-3x |  | serial | 50.6 |  |

## Controls

- compute (recur/gfortran-expert/serial-repro-native/full): n=96 median 0.01281 s, baseline 0.01283 s
- memory (saxpy/gfortran-expert/serial-repro-native/control): n=96 median 0.005282 s, baseline 0.005237 s
