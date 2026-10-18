# Blade vs Fortran -- bench session 2026-10-18T12:26:06

- host LAPTOP-350H6LDB | AMD Ryzen 7 5800HS with Radeon Graphics | 8C/16T | 15.41 GB | power ac | max processor state AC 100% | boost AC None | Power Scheme GUID: 64a64f24-65b9-4b56-befd-5ec1eaced9b3  (Silent)
- git fceb895d4e8df71131086a94652b5cab1b120850 | Blade.dll 74b6728036b170d9 | GNU Fortran (Rev4, Built by MSYS2 project) 16.2.0 | Intel(R) Fortran Compiler for applications running on Intel(R) 64, Version 2026.1.0 Build 20260617 | clang version 22.1.8 (https://github.com/msys2/MINGW-packages d34feaaa1d0aff624c870910b6f2589aa40d20de)
- size `full` | duration 2008.6 s | pins: pins-A.json, pins-B.json
- PRIMARY estimand: the complete timed region. ratio = median(arm) / median(Blade reference), round-block bootstrap 95% CI (10000 resamples); ratio > 1 = Blade faster. Verdicts (delta 0.1): win / detectable-win / equivalent / detectable-loss / loss / inconclusive; `suppressed` = the block failed a drift, trend or control check. Intervals are pointwise; no suite-wide claim. incremental = median(arm) - median(its own input-only twin), NOT kernel time. Reference: blade-llvm (serial), blade-cpp (mt8, or when the LLVM lane refused). Fresh-process, allocation-inclusive measurements.
- **FLAG**: antisym/serial-repro-native: inference suppressed: trend: gfortran-expert first-half vs second-half median moved 5.1%; trend: ifx-expert first-half vs second-half median moved 7.7%; trend: control compute moved 31.4% within the block; trend: control memory moved 17.1% within the block
- **FLAG**: comoment3/serial-repro-native: inference suppressed: control compute median 0.01435s moved -13.0% vs the session baseline 0.0165s; trend: control compute moved 15.6% within the block; control memory median 0.005784s moved -8.9% vs the session baseline 0.006347s
- **FLAG**: gram/serial-repro-native: inference suppressed: trend: gfortran-natural first-half vs second-half median moved 13.5%; control compute median 0.01277s moved -22.6% vs the session baseline 0.0165s; control memory median 0.005328s moved -16.1% vs the session baseline 0.006347s
- **FLAG**: pairkernel/serial-repro-native: inference suppressed: control compute median 0.01445s moved -12.4% vs the session baseline 0.0165s; trend: control compute moved 19.4% within the block
- **FLAG**: recur/serial-repro-native: inference suppressed: trend: blade-llvm first-half vs second-half median moved 10.1%; trend: blade-cpp first-half vs second-half median moved 11.3%; trend: ifx-expert first-half vs second-half median moved 5.6%; control compute median 0.01292s moved -21.7% vs the session baseline 0.0165s; control memory median 0.005341s moved -15.8% vs the session baseline 0.006347s
- **FLAG**: stencil/serial-repro-native: inference suppressed: trend: blade-llvm first-half vs second-half median moved 6.2%; control compute median 0.01294s moved -21.6% vs the session baseline 0.0165s; control memory median 0.005326s moved -16.1% vs the session baseline 0.006347s

## serial-repro-native

Blade reference per kernel: antisym=blade-llvm, comoment3=blade-llvm, gram=blade-llvm, pairkernel=blade-llvm, recur=blade-llvm, stencil=blade-llvm

| kernel | arm | status | n | min (s) | median (s) | IQR (s) | ratio vs ref [95% CI] | verdict | hypothesis | fold form | parallelism | peak WS (MB) | incremental (s) [CI] | incremental ratio [CI] | notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| antisym | blade-llvm | ok | 11 | 0.5045 | 0.5082 | 0.004205 | ref |  |  | in-order | serial | 17.4 |  |  |  |
| antisym | blade-cpp | ok | 11 | 0.4073 | 0.4113 | 0.005101 | 0.809 [0.802, 0.816] | suppressed |  | in-order | serial | 18.3 |  |  |  |
| antisym | gfortran-natural | ok | 11 | 0.768 | 0.7739 | 0.004098 | 1.523 [1.510, 1.529] | suppressed | WIN ~2x (half the cells, no diagonal) |  | serial | 23.4 |  |  |  |
| antisym | gfortran-expert | ok | 11 | 0.03105 | 0.03272 | 0.001761 | 0.064 [0.062, 0.066] | suppressed | parity +-15% |  | serial | 32.7 |  |  |  |
| antisym | ifx-natural | ok | 11 | 0.3309 | 0.3352 | 0.006682 | 0.659 [0.652, 0.666] | suppressed | WIN ~2x (half the cells, no diagonal) |  | serial | 23.0 |  |  |  |
| antisym | ifx-expert | ok | 11 | 0.02926 | 0.03145 | 0.001656 | 0.062 [0.059, 0.063] | suppressed | parity +-15% |  | serial | 32.2 |  |  |  |

> antisym serial-repro-native: inference suppressed -- trend: gfortran-expert first-half vs second-half median moved 5.1%; trend: ifx-expert first-half vs second-half median moved 7.7%; trend: control compute moved 31.4% within the block; trend: control memory moved 17.1% within the block

| comoment3 | blade-llvm | ok | 11 | 0.1564 | 0.1568 | 0.0005501 | ref |  |  | in-order | serial | 11.3 |  |  |  |
| comoment3 | blade-cpp | ok | 11 | 0.1796 | 0.1806 | 0.0011 | 1.152 [1.147, 1.159] | suppressed |  | in-order | serial | 12.2 |  |  |  |
| comoment3 | gfortran-natural | ok | 11 | 0.5996 | 0.6026 | 0.004986 | 3.842 [3.822, 3.867] | suppressed | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | serial | 43.4 |  |  |  |
| comoment3 | gfortran-expert | ok | 11 | 0.01128 | 0.01177 | 0.0006122 | 0.075 [0.073, 0.077] | suppressed | parity to 1.3x loss |  | serial | 12.3 |  |  |  |
| comoment3 | ifx-natural | ok | 11 | 0.7178 | 0.722 | 0.004725 | 4.604 [4.582, 4.618] | suppressed | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | serial | 43.0 |  |  |  |
| comoment3 | ifx-expert | ok | 11 | 0.0114 | 0.0117 | 0.0003195 | 0.075 [0.074, 0.076] | suppressed | parity to 1.3x loss |  | serial | 11.8 |  |  |  |

> comoment3 serial-repro-native: inference suppressed -- control compute median 0.01435s moved -13.0% vs the session baseline 0.0165s; trend: control compute moved 15.6% within the block; control memory median 0.005784s moved -8.9% vs the session baseline 0.006347s

| gram | blade-llvm | ok | 11 | 1.217 | 1.219 | 0.004434 | ref |  |  | in-order | serial | 26.6 |  |  |  |
| gram | blade-cpp | ok | 11 | 0.9496 | 0.9536 | 0.004668 | 0.782 [0.778, 0.786] | suppressed |  | in-order | serial | 27.4 |  |  |  |
| gram | gfortran-natural | ok | 11 | 0.498 | 0.5561 | 0.09859 | 0.456 [0.414, 0.507] | suppressed | win 1.5-2x if matmul is a naive nest; parity/loss if a blocked runtime kernel |  | serial | 36.8 |  |  |  |
| gram | gfortran-expert | ok | 11 | 0.07676 | 0.07784 | 0.0008346 | 0.064 [0.063, 0.064] | suppressed | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | serial | 51.6 |  |  |  |
| gram | ifx-natural | ok | 11 | 0.5682 | 0.569 | 0.001298 | 0.467 [0.465, 0.468] | suppressed | win 1.5-2x if matmul is a naive nest; parity/loss if a blocked runtime kernel |  | serial | 55.9 |  |  |  |
| gram | ifx-expert | ok | 11 | 0.07019 | 0.07065 | 0.0003376 | 0.058 [0.058, 0.058] | suppressed | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | serial | 51.2 |  |  |  |

> gram serial-repro-native: inference suppressed -- trend: gfortran-natural first-half vs second-half median moved 13.5%; control compute median 0.01277s moved -22.6% vs the session baseline 0.0165s; control memory median 0.005328s moved -16.1% vs the session baseline 0.006347s

| pairkernel | blade-llvm | ok | 11 | 0.04099 | 0.04278 | 0.002025 | ref |  |  | in-order | serial | 28.5 |  |  |  |
| pairkernel | blade-cpp | ok | 11 | 0.07311 | 0.07759 | 0.005033 | 1.813 [1.748, 1.883] | suppressed |  | in-order | serial | 29.3 |  |  |  |
| pairkernel | gfortran-natural | ok | 11 | 0.06065 | 0.06279 | 0.002652 | 1.468 [1.428, 1.514] | suppressed | WIN ~2x (half the transcendentals) |  | serial | 52.9 |  |  |  |
| pairkernel | gfortran-expert | ok | 11 | 0.03423 | 0.03522 | 0.001041 | 0.823 [0.798, 0.846] | suppressed | parity within 5% |  | serial | 29.0 |  |  |  |
| pairkernel | ifx-natural | ok | 11 | 0.06373 | 0.06505 | 0.001358 | 1.520 [1.485, 1.563] | suppressed | WIN ~2x (half the transcendentals) |  | serial | 52.5 |  |  |  |
| pairkernel | ifx-expert | ok | 11 | 0.03164 | 0.032 | 0.0006767 | 0.748 [0.735, 0.777] | suppressed | parity within 5% |  | serial | 28.5 |  |  |  |

> pairkernel serial-repro-native: inference suppressed -- control compute median 0.01445s moved -12.4% vs the session baseline 0.0165s; trend: control compute moved 19.4% within the block

| recur | blade-llvm | ok | 21 | 0.1067 | 0.111 | 0.01311 | ref |  |  | in-order | serial | 51.5 |  |  |  |
| recur | blade-cpp | ok | 21 | 0.116 | 0.1235 | 0.01666 | 1.113 [1.047, 1.188] | suppressed |  | in-order | serial | 52.5 |  |  |  |
| recur | gfortran-natural | ok | 21 | 0.01741 | 0.01806 | 0.0006955 | 0.163 [0.150, 0.168] | suppressed | parity |  | serial | 51.6 |  |  |  |
| recur | gfortran-expert | ok | 21 | 0.01248 | 0.01295 | 0.0008918 | 0.117 [0.109, 0.122] | suppressed | LOSS 1.5-3x |  | serial | 51.0 |  |  |  |
| recur | ifx-natural | ok | 21 | 0.01461 | 0.01489 | 0.0005881 | 0.134 [0.123, 0.138] | suppressed | parity |  | serial | 51.1 |  |  |  |
| recur | ifx-expert | ok | 21 | 0.0129 | 0.01324 | 0.0007753 | 0.119 [0.112, 0.125] | suppressed | LOSS 1.5-3x |  | serial | 50.5 |  |  |  |

> recur serial-repro-native: inference suppressed -- trend: blade-llvm first-half vs second-half median moved 10.1%; trend: blade-cpp first-half vs second-half median moved 11.3%; trend: ifx-expert first-half vs second-half median moved 5.6%; control compute median 0.01292s moved -21.7% vs the session baseline 0.0165s; control memory median 0.005341s moved -15.8% vs the session baseline 0.006347s

| stencil | blade-llvm | ok | 21 | 0.01501 | 0.01597 | 0.001154 | ref |  |  | in-order | serial | 49.8 | 0.008965 [0.008526, 0.009281] | ref |  |
| stencil | blade-cpp | ok | 21 | 0.01718 | 0.01777 | 0.000944 | 1.113 [1.080, 1.162] | suppressed |  | in-order | serial | 50.8 | 0.007754 [0.007371, 0.008308] | 0.865 [0.817, 0.937] |  |
| stencil | gfortran-natural | ok | 21 | 0.01679 | 0.01739 | 0.0004517 | 1.089 [1.062, 1.125] | suppressed | parity (bandwidth-bound) |  | serial | 50.9 | 0.007175 [0.006893, 0.007433] | 0.800 [0.761, 0.863] |  |
| stencil | gfortran-expert | ok | 21 | 0.01628 | 0.0168 | 0.001031 | 1.052 [1.032, 1.103] | suppressed | parity |  | serial | 50.9 | 0.006698 [0.006527, 0.007532] | 0.747 [0.715, 0.828] |  |
| stencil | ifx-natural | ok | 21 | 0.01818 | 0.0187 | 0.0005058 | 1.171 [1.139, 1.209] | suppressed | parity (bandwidth-bound) |  | serial | 50.4 | 0.0118 [0.01155, 0.01197] | 1.317 [1.258, 1.396] |  |
| stencil | ifx-expert | ok | 21 | 0.01315 | 0.01347 | 0.0008307 | 0.843 [0.825, 0.881] | suppressed | parity |  | serial | 50.4 | 0.006606 [0.006432, 0.007144] | 0.737 [0.705, 0.804] |  |
| stencil | blade-llvm-inputonly | ok | 21 | 0.006857 | 0.007008 | 0.000189 |  |  |  | in-order | serial | 27.5 |  |  |  |
| stencil | blade-cpp-inputonly | ok | 21 | 0.009906 | 0.01002 | 0.0002192 |  |  |  | in-order | serial | 28.2 |  |  |  |
| stencil | gfortran-natural-inputonly | ok | 21 | 0.00989 | 0.01021 | 0.0002989 |  |  |  |  | serial | 28.0 |  |  |  |
| stencil | gfortran-expert-inputonly | ok | 21 | 0.009893 | 0.0101 | 0.0002941 |  |  |  |  | serial | 28.0 |  |  |  |
| stencil | ifx-natural-inputonly | ok | 21 | 0.006625 | 0.006896 | 0.0001758 |  |  |  |  | serial | 27.5 |  |  |  |
| stencil | ifx-expert-inputonly | ok | 21 | 0.006453 | 0.006865 | 0.0003807 |  |  |  |  | serial | 27.5 |  |  |  |

> stencil serial-repro-native: inference suppressed -- trend: blade-llvm first-half vs second-half median moved 6.2%; control compute median 0.01294s moved -21.6% vs the session baseline 0.0165s; control memory median 0.005326s moved -16.1% vs the session baseline 0.006347s


## Controls

- compute (recur/gfortran-expert/serial-repro-native/full): n=86 median 0.01298 s, baseline 0.0165 s
- memory (saxpy/gfortran-expert/serial-repro-native/control): n=86 median 0.00546 s, baseline 0.006347 s
