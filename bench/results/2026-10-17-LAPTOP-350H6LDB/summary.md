# Blade vs Fortran -- bench session 2026-10-17T23:16:51

- host LAPTOP-350H6LDB | AMD Ryzen 7 5800HS with Radeon Graphics | 8C/16T | 15.41 GB | power ac | max processor state AC 100% | boost AC None | Power Scheme GUID: 64a64f24-65b9-4b56-befd-5ec1eaced9b3  (Silent)
- git 927776980d0669edd6b9cc822833ce119dcaabd4 | Blade.dll 74b6728036b170d9 | GNU Fortran (Rev4, Built by MSYS2 project) 16.2.0 | Intel(R) Fortran Compiler for applications running on Intel(R) 64, Version 2026.1.0 Build 20260617 | clang version 22.1.8 (https://github.com/msys2/MINGW-packages d34feaaa1d0aff624c870910b6f2589aa40d20de)
- size `full` | duration 1349.4 s | pins: pins-A.json, pins-B.json
- PRIMARY estimand: the complete timed region. ratio = median(arm) / median(Blade reference), round-block bootstrap 95% CI (10000 resamples); ratio > 1 = Blade faster. Verdicts (delta 0.1): win / detectable-win / equivalent / detectable-loss / loss / inconclusive; `suppressed` = the block failed a drift, trend or control check. Intervals are pointwise; no suite-wide claim. incremental = median(arm) - median(its own input-only twin), NOT kernel time. Reference: blade-llvm (serial), blade-cpp (mt8, or when the LLVM lane refused). Fresh-process, allocation-inclusive measurements.
- **FLAG**: tainted: antisym/serial-licensed-native: --rounds 3 below the minimum 11
- **FLAG**: tainted: comoment3/serial-licensed-native: --rounds 3 below the minimum 11
- **FLAG**: comoment3/serial-licensed-native: inference suppressed: control memory median 0.00568s moved +8.8% vs the session baseline 0.005221s
- **FLAG**: tainted: gram/serial-licensed-native: --rounds 3 below the minimum 11
- **FLAG**: tainted: pairkernel/serial-licensed-native: --rounds 3 below the minimum 11
- **FLAG**: pairkernel/serial-licensed-native: inference suppressed: control memory median 0.005697s moved +9.1% vs the session baseline 0.005221s
- **FLAG**: tainted: fusedchain/serial-licensed-native: --rounds 3 below the minimum 21
- **FLAG**: fusedchain/serial-licensed-native: inference suppressed: control compute median 0.01457s moved +13.1% vs the session baseline 0.01288s; control memory median 0.005853s moved +12.1% vs the session baseline 0.005221s
- **FLAG**: tainted: maskagg/serial-licensed-native: --rounds 3 below the minimum 21
- **FLAG**: maskagg/serial-licensed-native: inference suppressed: control compute median 0.0144s moved +11.7% vs the session baseline 0.01288s; control memory median 0.005837s moved +11.8% vs the session baseline 0.005221s
- **FLAG**: tainted: onepass/serial-licensed-native: --rounds 3 below the minimum 21
- **FLAG**: onepass/serial-licensed-native: inference suppressed: control compute median 0.01429s moved +10.9% vs the session baseline 0.01288s; control memory median 0.005772s moved +10.6% vs the session baseline 0.005221s
- **FLAG**: tainted: recur/serial-licensed-native: --rounds 3 below the minimum 21
- **FLAG**: recur/serial-licensed-native: inference suppressed: control compute median 0.01398s moved +8.5% vs the session baseline 0.01288s
- **FLAG**: tainted: stencil/serial-licensed-native: --rounds 3 below the minimum 21
- **FLAG**: tainted: dot/serial-licensed-native: --rounds 3 below the minimum 21
- **FLAG**: dot/serial-licensed-native: inference suppressed: control compute median 0.01596s moved +23.9% vs the session baseline 0.01288s; control memory median 0.005989s moved +14.7% vs the session baseline 0.005221s
- **FLAG**: tainted: saxpy/serial-licensed-native: --rounds 3 below the minimum 21

## serial-licensed-native

Blade reference per kernel: antisym=blade-llvm, comoment3=blade-llvm, gram=blade-llvm, pairkernel=blade-llvm, fusedchain=blade-llvm, maskagg=blade-cpp, onepass=blade-llvm, recur=blade-llvm, stencil=blade-llvm, dot=blade-llvm, saxpy=blade-llvm

| kernel | arm | status | n | min (s) | median (s) | IQR (s) | ratio vs ref [95% CI] | verdict | hypothesis | fold form | parallelism | peak WS (MB) | incremental (s) [CI] | incremental ratio [CI] | notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| antisym | blade-llvm | ok | 3 | 0.07404 | 0.07477 | 0.001718 | ref |  |  | reordered (7 reassoc flags) | serial | 17.3 |  |  |  |
| antisym | blade-cpp | ok | 3 | 0.1101 | 0.1151 | 0.007431 | 1.540 [1.422, 1.688] | win |  | reordered (6 SIMD_REDUCTION) | serial | 18.1 |  |  |  |
| antisym | gfortran-natural | ok | 3 | 0.7736 | 0.7938 | 0.01361 | 10.616 [9.985, 10.721] | win | WIN ~2x (half the cells, no diagonal) |  | serial | 23.2 |  |  |  |
| antisym | gfortran-expert | ok | 3 | 0.03187 | 0.03461 | 0.001597 | 0.463 [0.430, 0.463] | loss | parity +-15% |  | serial | 32.5 |  |  |  |
| antisym | ifx-natural | ok | 3 | 0.3334 | 0.3413 | 0.005662 | 4.564 [4.449, 4.610] | win | WIN ~2x (half the cells, no diagonal) |  | serial | 22.8 |  |  |  |
| antisym | ifx-expert | ok | 3 | 0.02979 | 0.03105 | 0.0009809 | 0.415 [0.398, 0.429] | loss | parity +-15% |  | serial | 32.1 |  |  |  |
| comoment3 | blade-llvm | ok | 3 | 0.0421 | 0.04216 | 0.0002468 | ref |  |  | reordered (6 reassoc flags) | serial | 11.1 |  |  |  |
| comoment3 | blade-cpp | ok | 3 | 0.1086 | 0.1086 | 0.0001655 | 2.576 [2.557, 2.579] | suppressed |  | reordered (6 SIMD_REDUCTION) | serial | 12.0 |  |  |  |
| comoment3 | gfortran-natural | ok | 3 | 0.6011 | 0.6157 | 0.007656 | 14.604 [14.256, 14.640] | suppressed | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | serial | 43.3 |  |  |  |
| comoment3 | gfortran-expert | ok | 3 | 0.01101 | 0.01106 | 8.57e-05 | 0.262 [0.260, 0.266] | suppressed | parity to 1.3x loss |  | serial | 12.1 |  |  |  |
| comoment3 | ifx-natural | ok | 3 | 0.7181 | 0.7218 | 0.006368 | 17.118 [16.944, 17.358] | suppressed | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | serial | 42.9 |  |  |  |
| comoment3 | ifx-expert | ok | 3 | 0.01151 | 0.01164 | 0.0001177 | 0.276 [0.273, 0.276] | suppressed | parity to 1.3x loss |  | serial | 11.7 |  |  |  |

> comoment3 serial-licensed-native: inference suppressed -- control memory median 0.00568s moved +8.8% vs the session baseline 0.005221s

| gram | blade-llvm | ok | 3 | 0.1517 | 0.1529 | 0.001234 | ref |  |  | reordered (8 reassoc flags) | serial | 26.4 |  |  |  |
| gram | blade-cpp | ok | 3 | 0.2736 | 0.2907 | 0.01645 | 1.900 [1.789, 2.021] | win |  | reordered (5 SIMD_REDUCTION) | serial | 27.2 |  |  |  |
| gram | gfortran-natural | ok | 3 | 0.6661 | 0.6738 | 0.00443 | 4.406 [4.355, 4.451] | win | win 1.5-2x if matmul is a naive nest; parity/loss if a blocked runtime kernel |  | serial | 36.7 |  |  |  |
| gram | gfortran-expert | ok | 3 | 0.07796 | 0.07864 | 0.0009518 | 0.514 [0.510, 0.527] | loss | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | serial | 51.5 |  |  |  |
| gram | ifx-natural | ok | 3 | 0.5705 | 0.5743 | 0.00901 | 3.755 [3.727, 3.848] | win | win 1.5-2x if matmul is a naive nest; parity/loss if a blocked runtime kernel |  | serial | 55.7 |  |  |  |
| gram | ifx-expert | ok | 3 | 0.06985 | 0.07069 | 0.002051 | 0.462 [0.461, 0.480] | loss | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | serial | 51.1 |  |  |  |
| pairkernel | blade-llvm | ok | 3 | 0.04179 | 0.042 | 0.0006393 | ref |  |  | reordered (4 reassoc flags) | serial | 28.4 |  |  |  |
| pairkernel | blade-cpp | ok | 3 | 0.07393 | 0.07696 | 0.002997 | 1.833 [1.760, 1.856] | suppressed |  | reordered (3 SIMD_REDUCTION) | serial | 29.2 |  |  |  |
| pairkernel | gfortran-natural | ok | 3 | 0.06029 | 0.06093 | 0.0005614 | 1.451 [1.415, 1.462] | suppressed | WIN ~2x (half the transcendentals) |  | serial | 52.8 |  |  |  |
| pairkernel | gfortran-expert | ok | 3 | 0.03243 | 0.03256 | 0.0004782 | 0.775 [0.753, 0.799] | suppressed | parity within 5% |  | serial | 28.8 |  |  |  |
| pairkernel | ifx-natural | ok | 3 | 0.06298 | 0.06368 | 0.0006395 | 1.516 [1.462, 1.537] | suppressed | WIN ~2x (half the transcendentals) |  | serial | 52.3 |  |  |  |
| pairkernel | ifx-expert | ok | 3 | 0.0304 | 0.03084 | 0.000462 | 0.734 [0.716, 0.749] | suppressed | parity within 5% |  | serial | 28.4 |  |  |  |

> pairkernel serial-licensed-native: inference suppressed -- control memory median 0.005697s moved +9.1% vs the session baseline 0.005221s

| fusedchain | blade-llvm | ok | 3 | 0.004779 | 0.004839 | 0.0003387 | ref |  |  | reordered (2 reassoc flags) | serial | 19.6 | 0.0007132 [0.000172, 0.001532] | ref |  |
| fusedchain | blade-cpp | ok | 3 | 0.006895 | 0.007102 | 0.0002124 | 1.468 [1.302, 1.513] | suppressed |  | reordered (1 SIMD_REDUCTION) | serial | 20.4 | 0.0004618 [0.0002545, 0.0007087] | 0.648 [0.389, 3.894] |  |
| fusedchain | gfortran-natural | ok | 3 | 0.01888 | 0.01897 | 0.0001756 | 3.921 [3.477, 3.975] | suppressed | WIN 1.5-2.5x (no temporaries) |  | serial | 65.9 | 0.01177 [0.01152, 0.01204] | 16.508 [7.826, 69.976] |  |
| fusedchain | gfortran-expert | ok | 3 | 0.007186 | 0.00739 | 0.0001214 | 1.527 [1.361, 1.527] | suppressed | parity |  | serial | 20.1 | 0.0007565 [0.0005087, 0.001148] | 1.061 [0.749, 4.398] |  |
| fusedchain | ifx-natural | ok | 3 | 0.01484 | 0.01494 | 0.000129 | 3.087 [2.766, 3.104] | suppressed | WIN 1.5-2.5x (no temporaries) |  | serial | 65.5 | 0.009697 [0.009697, 0.009995] | 13.597 [6.420, 56.381] |  |
| fusedchain | ifx-expert | ok | 3 | 0.005082 | 0.005226 | 0.0001924 | 1.080 [0.958, 1.130] | suppressed | parity |  | serial | 19.7 | 0.001178 [0.001034, 0.001384] | 1.652 [0.779, 8.045] |  |
| fusedchain | blade-llvm-inputonly | ok | 3 | 0.003925 | 0.004125 | 0.0003708 |  |  |  | reordered (1 reassoc flags) | serial | 19.6 |  |  |  |
| fusedchain | blade-cpp-inputonly | ok | 3 | 0.006394 | 0.006641 | 0.0001282 |  |  |  | in-order | serial | 20.4 |  |  |  |
| fusedchain | gfortran-natural-inputonly | ok | 3 | 0.006984 | 0.007199 | 0.0001883 |  |  |  |  | serial | 20.1 |  |  |  |
| fusedchain | gfortran-expert-inputonly | ok | 3 | 0.006281 | 0.006634 | 0.0001982 |  |  |  |  | serial | 20.1 |  |  |  |
| fusedchain | ifx-natural-inputonly | ok | 3 | 0.004842 | 0.005241 | 0.0002096 |  |  |  |  | serial | 19.7 |  |  |  |
| fusedchain | ifx-expert-inputonly | ok | 3 | 0.004033 | 0.004048 | 2.49e-05 |  |  |  |  | serial | 19.7 |  |  |  |

> fusedchain serial-licensed-native: inference suppressed -- control compute median 0.01457s moved +13.1% vs the session baseline 0.01288s; control memory median 0.005853s moved +12.1% vs the session baseline 0.005221s

| maskagg | blade-llvm | fallback | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | a compound-index initializer; expected fallback (the LLVM lane refuses `compound` (notes/llvm-acceptance-B.md); blade-cpp is the Blade figure) |
| maskagg | blade-cpp | ok | 3 | 0.06496 | 0.06548 | 0.001223 | ref |  |  | in-order | serial | 46.3 | 0.06239 [0.06187, 0.06431] | ref |  |
| maskagg | gfortran-natural | ok | 3 | 0.008769 | 0.008939 | 0.0001117 | 0.137 [0.133, 0.137] | suppressed | LOSS 1.3-2x |  | serial | 17.3 | 0.005318 [0.005139, 0.005389] | 0.085 [0.083, 0.085] |  |
| maskagg | gfortran-expert | ok | 3 | 0.004585 | 0.005006 | 0.0003151 | 0.076 [0.071, 0.080] | suppressed | LOSS 1.3-2x |  | serial | 16.4 | 0.001718 [0.001296, 0.00183] | 0.028 [0.021, 0.028] |  |
| maskagg | ifx-natural | ok | 3 | 0.0106 | 0.01064 | 0.0002101 | 0.163 [0.157, 0.168] | suppressed | LOSS 1.3-2x |  | serial | 23.5 | 0.008158 [0.008129, 0.008533] | 0.131 [0.126, 0.137] |  |
| maskagg | ifx-expert | ok | 3 | 0.003431 | 0.00356 | 0.0001553 | 0.054 [0.052, 0.058] | suppressed | LOSS 1.3-2x |  | serial | 15.9 | 0.001483 [0.001387, 0.001483] | 0.024 [0.022, 0.024] |  |
| maskagg | blade-llvm-inputonly | ok | 3 | 0.001992 | 0.002067 | 0.0001726 |  |  |  | reordered (1 reassoc flags) | serial | 12.0 |  |  |  |
| maskagg | blade-cpp-inputonly | ok | 3 | 0.003084 | 0.003091 | 6.665e-05 |  |  |  | in-order | serial | 12.8 |  |  |  |
| maskagg | gfortran-natural-inputonly | ok | 3 | 0.003603 | 0.003621 | 1.3e-05 |  |  |  |  | serial | 12.5 |  |  |  |
| maskagg | gfortran-expert-inputonly | ok | 3 | 0.003176 | 0.003289 | 0.0001892 |  |  |  |  | serial | 12.5 |  |  |  |
| maskagg | ifx-natural-inputonly | ok | 3 | 0.002467 | 0.002484 | 1.88e-05 |  |  |  |  | serial | 12.0 |  |  |  |
| maskagg | ifx-expert-inputonly | ok | 3 | 0.002044 | 0.002076 | 0.0001507 |  |  |  |  | serial | 12.0 |  |  |  |

> maskagg serial-licensed-native: inference suppressed -- control compute median 0.0144s moved +11.7% vs the session baseline 0.01288s; control memory median 0.005837s moved +11.8% vs the session baseline 0.005221s

| onepass | blade-llvm | ok | 3 | 0.01239 | 0.01267 | 0.000579 | ref |  |  | reordered (5 reassoc flags) | serial | 42.5 | 0.001773 [0.001498, 0.002699] | ref |  |
| onepass | blade-cpp | ok | 3 | 0.01763 | 0.0179 | 0.0002102 | 1.413 [1.321, 1.456] | suppressed |  | reordered (1 SIMD_REDUCTION) | serial | 43.3 | 0.001342 [0.001074, 0.001962] | 0.757 [0.712, 0.954] |  |
| onepass | gfortran-natural | ok | 3 | 0.03385 | 0.03399 | 9.2e-05 | 2.683 [2.508, 2.746] | suppressed | WIN 2-3x (one pass vs four) |  | serial | 43.0 | 0.01574 [0.01543, 0.01591] | 8.878 [5.896, 10.532] |  |
| onepass | gfortran-expert | ok | 3 | 0.01799 | 0.01849 | 0.0003385 | 1.459 [1.364, 1.506] | suppressed | parity |  | serial | 43.0 | 0.002226 [0.001563, 0.002404] | 1.256 [0.854, 1.604] |  |
| onepass | ifx-natural | ok | 3 | 0.01405 | 0.01444 | 0.0003444 | 1.140 [1.037, 1.165] | suppressed | WIN 2-3x (one pass vs four) |  | serial | 42.6 | 0.001795 [0.001448, 0.002093] | 1.013 [0.536, 1.387] |  |
| onepass | ifx-expert | ok | 3 | 0.01275 | 0.01276 | 7.915e-05 | 1.007 [0.941, 1.029] | suppressed | parity |  | serial | 42.6 | 0.001859 [0.001742, 0.002087] | 1.049 [0.645, 1.393] |  |
| onepass | blade-llvm-inputonly | ok | 3 | 0.01085 | 0.01089 | 0.0001536 |  |  |  | reordered (1 reassoc flags) | serial | 42.5 |  |  |  |
| onepass | blade-cpp-inputonly | ok | 3 | 0.01593 | 0.01655 | 0.0003428 |  |  |  | in-order | serial | 43.3 |  |  |  |
| onepass | gfortran-natural-inputonly | ok | 3 | 0.01808 | 0.01825 | 0.0001719 |  |  |  |  | serial | 43.0 |  |  |  |
| onepass | gfortran-expert-inputonly | ok | 3 | 0.01618 | 0.01626 | 0.000121 |  |  |  |  | serial | 43.0 |  |  |  |
| onepass | ifx-natural-inputonly | ok | 3 | 0.0126 | 0.01264 | 3.91e-05 |  |  |  |  | serial | 42.6 |  |  |  |
| onepass | ifx-expert-inputonly | ok | 3 | 0.01067 | 0.0109 | 0.000168 |  |  |  |  | serial | 42.6 |  |  |  |

> onepass serial-licensed-native: inference suppressed -- control compute median 0.01429s moved +10.9% vs the session baseline 0.01288s; control memory median 0.005772s moved +10.6% vs the session baseline 0.005221s

| recur | blade-llvm | ok | 3 | 0.1179 | 0.1184 | 0.002795 | ref |  |  | reordered (3 reassoc flags) | serial | 51.4 |  |  |  |
| recur | blade-cpp | ok | 3 | 0.1345 | 0.1354 | 0.0009898 | 1.143 [1.097, 1.158] | suppressed |  | reordered (3 SIMD_REDUCTION) | serial | 52.3 |  |  |  |
| recur | gfortran-natural | ok | 3 | 0.01873 | 0.01921 | 0.0004278 | 0.162 [0.158, 0.163] | suppressed | parity |  | serial | 51.4 |  |  |  |
| recur | gfortran-expert | ok | 3 | 0.0103 | 0.01108 | 0.000721 | 0.094 [0.087, 0.100] | suppressed | LOSS 1.5-3x |  | serial | 50.8 |  |  |  |
| recur | ifx-natural | ok | 3 | 0.0163 | 0.0163 | 0.0002979 | 0.138 [0.132, 0.143] | suppressed | parity |  | serial | 51.0 |  |  |  |
| recur | ifx-expert | ok | 3 | 0.01082 | 0.01087 | 0.0003774 | 0.092 [0.088, 0.098] | suppressed | LOSS 1.5-3x |  | serial | 50.4 |  |  |  |

> recur serial-licensed-native: inference suppressed -- control compute median 0.01398s moved +8.5% vs the session baseline 0.01288s

| stencil | blade-llvm | ok | 3 | 0.01245 | 0.01387 | 0.001113 | ref |  |  | reordered (4 reassoc flags) | serial | 49.7 | 0.0076 [0.006175, 0.008453] | ref |  |
| stencil | blade-cpp | ok | 3 | 0.01592 | 0.01622 | 0.000228 | 1.169 [1.085, 1.316] | win |  | reordered (4 SIMD_REDUCTION) | serial | 50.6 | 0.006616 [0.006152, 0.007363] | 0.871 [0.748, 1.192] |  |
| stencil | gfortran-natural | ok | 3 | 0.01742 | 0.01798 | 0.001634 | 1.296 [1.296, 1.410] | win | parity (bandwidth-bound) |  | serial | 50.7 | 0.00693 [0.006455, 0.009638] | 0.912 [0.912, 1.140] |  |
| stencil | gfortran-expert | ok | 3 | 0.01458 | 0.01492 | 0.0006163 | 1.075 [1.017, 1.171] | detectable-win | parity |  | serial | 50.7 | 0.005632 [0.005291, 0.006033] | 0.741 [0.701, 0.857] |  |
| stencil | ifx-natural | ok | 3 | 0.01896 | 0.01923 | 0.001769 | 1.387 [1.367, 1.545] | win | parity (bandwidth-bound) |  | serial | 50.2 | 0.0117 [0.01142, 0.01392] | 1.539 [1.539, 1.985] |  |
| stencil | ifx-expert | ok | 3 | 0.01076 | 0.01135 | 0.0007593 | 0.818 [0.733, 0.912] | loss | parity |  | serial | 50.3 | 0.004918 [0.004329, 0.005782] | 0.647 [0.512, 0.839] |  |
| stencil | blade-llvm-inputonly | ok | 3 | 0.006219 | 0.006271 | 0.0001434 |  |  |  | reordered (2 reassoc flags) | serial | 27.3 |  |  |  |
| stencil | blade-cpp-inputonly | ok | 3 | 0.009017 | 0.0096 | 0.0005236 |  |  |  | reordered (1 SIMD_REDUCTION) | serial | 28.1 |  |  |  |
| stencil | gfortran-natural-inputonly | ok | 3 | 0.01097 | 0.01105 | 7.68e-05 |  |  |  |  | serial | 27.8 |  |  |  |
| stencil | gfortran-expert-inputonly | ok | 3 | 0.008996 | 0.009287 | 0.0003909 |  |  |  |  | serial | 27.8 |  |  |  |
| stencil | ifx-natural-inputonly | ok | 3 | 0.006979 | 0.007533 | 0.0007967 |  |  |  |  | serial | 27.4 |  |  |  |
| stencil | ifx-expert-inputonly | ok | 3 | 0.006169 | 0.006431 | 0.0001637 |  |  |  |  | serial | 27.4 |  |  |  |
| dot | blade-llvm | ok | 3 | 0.01462 | 0.01523 | 0.0003261 | ref |  |  | reordered (3 reassoc flags) | serial | 50.2 | 0.0007166 [-0.0007048, 0.003031] | ref |  |
| dot | blade-cpp | ok | 3 | 0.02063 | 0.02074 | 0.001079 | 1.362 [1.351, 1.496] | suppressed |  | reordered (1 SIMD_REDUCTION) | serial | 50.9 | 0.002321 [-0.003365, 0.00437] | undefined: the reference's incremental interval includes 0 |  |
| dot | gfortran-natural | ok | 3 | 0.02015 | 0.02165 | 0.001387 | 1.421 [1.323, 1.501] | suppressed | parity |  | serial | 50.6 | 0.001889 [-0.0001324, 0.003906] | undefined: the reference's incremental interval includes 0 |  |
| dot | gfortran-expert | ok | 3 | 0.02059 | 0.02112 | 0.0004937 | 1.386 [1.386, 1.413] | suppressed | parity |  | serial | 50.6 | 0.002272 [-4.9e-05, 0.003738] | undefined: the reference's incremental interval includes 0 |  |
| dot | ifx-natural | ok | 3 | 0.01659 | 0.01663 | 0.0005409 | 1.092 [1.087, 1.209] | suppressed | parity |  | serial | 50.2 | 0.004506 [0.001438, 0.005551] | undefined: the reference's incremental interval includes 0 |  |
| dot | ifx-expert | ok | 3 | 0.0156 | 0.01727 | 0.00122 | 1.134 [1.024, 1.234] | suppressed | parity |  | serial | 50.2 | 0.003526 [0.002209, 0.004144] | undefined: the reference's incremental interval includes 0 |  |
| dot | blade-llvm-inputonly | ok | 3 | 0.0122 | 0.01452 | 0.00156 |  |  |  | reordered (2 reassoc flags) | serial | 50.2 |  |  |  |
| dot | blade-cpp-inputonly | ok | 3 | 0.01812 | 0.01842 | 0.002937 |  |  |  | in-order | serial | 50.9 |  |  |  |
| dot | gfortran-natural-inputonly | ok | 3 | 0.01774 | 0.01976 | 0.002659 |  |  |  |  | serial | 50.6 |  |  |  |
| dot | gfortran-expert-inputonly | ok | 3 | 0.01738 | 0.01885 | 0.002123 |  |  |  |  | serial | 50.6 |  |  |  |
| dot | ifx-natural-inputonly | ok | 3 | 0.01194 | 0.01212 | 0.001607 |  |  |  |  | serial | 50.2 |  |  |  |
| dot | ifx-expert-inputonly | ok | 3 | 0.01339 | 0.01375 | 0.0002526 |  |  |  |  | serial | 50.2 |  |  |  |

> dot serial-licensed-native: inference suppressed -- control compute median 0.01596s moved +23.9% vs the session baseline 0.01288s; control memory median 0.005989s moved +14.7% vs the session baseline 0.005221s

| saxpy | blade-llvm | ok | 3 | 0.01213 | 0.0123 | 0.0001835 | ref |  |  | reordered (3 reassoc flags) | serial | 49.6 | 0.004856 [0.004712, 0.004856] | ref |  |
| saxpy | blade-cpp | ok | 3 | 0.0168 | 0.01721 | 0.0003611 | 1.399 [1.344, 1.445] | win |  | reordered (1 SIMD_REDUCTION) | serial | 50.6 | 0.005413 [0.004548, 0.005727] | 1.115 [0.961, 1.215] |  |
| saxpy | gfortran-natural | ok | 3 | 0.01655 | 0.01668 | 8.07e-05 | 1.356 [1.324, 1.375] | win | parity |  | serial | 50.6 | 0.004913 [0.004394, 0.00507] | 1.012 [0.928, 1.076] |  |
| saxpy | gfortran-expert | ok | 3 | 0.01479 | 0.01494 | 0.0002638 | 1.214 [1.184, 1.263] | win | parity |  | serial | 50.6 | 0.003563 [0.003416, 0.003877] | 0.734 [0.722, 0.823] |  |
| saxpy | ifx-natural | ok | 3 | 0.01331 | 0.01338 | 0.0001535 | 1.087 [1.070, 1.123] | detectable-win | parity |  | serial | 50.2 | 0.005836 [0.005787, 0.006013] | 1.202 [1.192, 1.276] |  |
| saxpy | ifx-expert | ok | 3 | 0.01046 | 0.0106 | 0.0002856 | 0.862 [0.837, 0.909] | loss | parity |  | serial | 50.2 | 0.00295 [0.00295, 0.003361] | 0.607 [0.607, 0.713] |  |
| saxpy | blade-llvm-inputonly | ok | 3 | 0.007418 | 0.007448 | 0.0001728 |  |  |  | reordered (2 reassoc flags) | serial | 34.9 |  |  |  |
| saxpy | blade-cpp-inputonly | ok | 3 | 0.01176 | 0.0118 | 0.00025 |  |  |  | in-order | serial | 35.7 |  |  |  |
| saxpy | gfortran-natural-inputonly | ok | 3 | 0.01161 | 0.01177 | 0.0002709 |  |  |  |  | serial | 35.4 |  |  |  |
| saxpy | gfortran-expert-inputonly | ok | 3 | 0.01112 | 0.01138 | 0.0001637 |  |  |  |  | serial | 35.4 |  |  |  |
| saxpy | ifx-natural-inputonly | ok | 3 | 0.007527 | 0.00754 | 4.025e-05 |  |  |  |  | serial | 34.9 |  |  |  |
| saxpy | ifx-expert-inputonly | ok | 3 | 0.007281 | 0.007655 | 0.0001926 |  |  |  |  | serial | 34.9 |  |  |  |

## Controls

- compute (recur/gfortran-expert/serial-repro-native/full): n=33 median 0.01372 s, baseline 0.01288 s
- memory (saxpy/gfortran-expert/serial-repro-native/control): n=33 median 0.00567 s, baseline 0.005221 s
