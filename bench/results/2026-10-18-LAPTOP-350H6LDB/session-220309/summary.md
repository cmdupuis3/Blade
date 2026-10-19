# Blade vs Fortran -- bench session 2026-10-18T22:03:09

- host LAPTOP-350H6LDB | AMD Ryzen 7 5800HS with Radeon Graphics | 8C/16T | 15.41 GB | power ac | max processor state AC 100% | boost AC None | Power Scheme GUID: 64a64f24-65b9-4b56-befd-5ec1eaced9b3  (Silent)
- git 99a8f915a9c2396f551346d7b4d0ae159221e071 | Blade.dll 30fbbc9775f694f5 | GNU Fortran (Rev4, Built by MSYS2 project) 16.2.0 | Intel(R) Fortran Compiler for applications running on Intel(R) 64, Version 2026.1.0 Build 20260617 | clang version 22.1.8 (https://github.com/msys2/MINGW-packages d34feaaa1d0aff624c870910b6f2589aa40d20de)
- size `full` | duration 1045.8 s | pins: pins-A.json, pins-B.json
- PRIMARY estimand: the complete timed region. ratio = median(arm) / median(Blade reference), round-block bootstrap 95% CI (10000 resamples); ratio > 1 = Blade faster. Verdicts (delta 0.1): win / detectable-win / equivalent / detectable-loss / loss / inconclusive; `suppressed` = the block failed a drift, trend or control check. Intervals are pointwise; no suite-wide claim. incremental = median(arm) - median(its own input-only twin), NOT kernel time. Reference: blade-llvm (serial), blade-cpp (mt8, or when the LLVM lane refused). Fresh-process, allocation-inclusive measurements.
- **FLAG**: antisym/serial-fastmath-native: inference suppressed: trend: gfortran-natural first-half vs second-half median moved 12.0%
- **FLAG**: comoment3/serial-fastmath-native: inference suppressed: trend: blade-llvm first-half vs second-half median moved 5.9%; trend: blade-cpp first-half vs second-half median moved 7.1%
- **FLAG**: pairkernel/serial-licensed-native: inference suppressed: trend: gfortran-natural first-half vs second-half median moved 7.5%; trend: ifx-expert first-half vs second-half median moved 5.9%

## serial-licensed-native

Blade reference per kernel: antisym=blade-llvm, comoment3=blade-llvm, pairkernel=blade-llvm

| kernel | arm | status | n | min (s) | median (s) | IQR (s) | ratio vs ref [95% CI] | verdict | hypothesis | fold form | parallelism | peak WS (MB) | notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| antisym | blade-llvm | ok | 21 | 0.02697 | 0.02759 | 0.0004032 | ref |  |  | reordered (4 reassoc flags) | serial | 24.9 |  |
| antisym | blade-cpp | ok | 21 | 0.02803 | 0.02856 | 0.0006411 | 1.035 [1.024, 1.048] | detectable-win |  | reordered (5 SIMD_REDUCTION) | serial | 25.7 |  |
| antisym | gfortran-natural | ok | 21 | 0.7719 | 0.7787 | 0.005958 | 28.226 [28.038, 28.371] | win | WIN ~2x (half the cells, no diagonal) |  | serial | 23.5 |  |
| antisym | gfortran-expert | ok | 21 | 0.03028 | 0.03106 | 0.0006277 | 1.126 [1.119, 1.138] | win | parity +-15% |  | serial | 32.7 |  |
| antisym | ifx-natural | ok | 21 | 0.3325 | 0.3358 | 0.005176 | 12.173 [12.099, 12.270] | win | WIN ~2x (half the cells, no diagonal) |  | serial | 23.0 |  |
| antisym | ifx-expert | ok | 21 | 0.02894 | 0.02961 | 0.0005523 | 1.073 [1.065, 1.085] | detectable-win | parity +-15% |  | serial | 32.3 |  |
| comoment3 | blade-llvm | ok | 21 | 0.009215 | 0.009312 | 8.14e-05 | ref |  |  | reordered (6 reassoc flags) | serial | 11.6 |  |
| comoment3 | blade-cpp | ok | 21 | 0.009403 | 0.009593 | 0.0001905 | 1.030 [1.023, 1.041] | detectable-win |  | reordered (6 SIMD_REDUCTION) | serial | 12.7 |  |
| comoment3 | gfortran-natural | ok | 21 | 0.5998 | 0.6011 | 0.003909 | 64.555 [64.318, 64.942] | win | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | serial | 43.5 |  |
| comoment3 | gfortran-expert | ok | 21 | 0.01112 | 0.01137 | 0.0002157 | 1.221 [1.208, 1.235] | win | parity to 1.3x loss |  | serial | 12.4 |  |
| comoment3 | ifx-natural | ok | 21 | 0.7176 | 0.7204 | 0.004153 | 77.363 [77.074, 77.807] | win | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | serial | 43.1 |  |
| comoment3 | ifx-expert | ok | 21 | 0.0112 | 0.0114 | 0.0001413 | 1.225 [1.219, 1.233] | win | parity to 1.3x loss |  | serial | 11.9 |  |
| pairkernel | blade-llvm | ok | 21 | 0.02879 | 0.02908 | 0.001619 | ref |  |  | reordered (4 reassoc flags) | serial | 28.6 |  |
| pairkernel | blade-cpp | ok | 21 | 0.02909 | 0.02934 | 0.0009416 | 1.009 [0.960, 1.015] | suppressed |  | reordered (3 SIMD_REDUCTION) | serial | 29.4 |  |
| pairkernel | gfortran-natural | ok | 21 | 0.06011 | 0.06057 | 0.004839 | 2.083 [1.988, 2.167] | suppressed | WIN ~2x (half the transcendentals) |  | serial | 53.0 |  |
| pairkernel | gfortran-expert | ok | 21 | 0.03238 | 0.03302 | 0.0009839 | 1.135 [1.081, 1.153] | suppressed | parity within 5% |  | serial | 29.1 |  |
| pairkernel | ifx-natural | ok | 21 | 0.06339 | 0.06432 | 0.002143 | 2.212 [2.109, 2.224] | suppressed | WIN ~2x (half the transcendentals) |  | serial | 52.5 |  |
| pairkernel | ifx-expert | ok | 21 | 0.03005 | 0.03043 | 0.001902 | 1.046 [1.012, 1.079] | suppressed | parity within 5% |  | serial | 28.6 |  |

> pairkernel serial-licensed-native: inference suppressed -- trend: gfortran-natural first-half vs second-half median moved 7.5%; trend: ifx-expert first-half vs second-half median moved 5.9%


## serial-repro-native

Blade reference per kernel: antisym=blade-llvm, comoment3=blade-llvm, pairkernel=blade-llvm

| kernel | arm | status | n | min (s) | median (s) | IQR (s) | ratio vs ref [95% CI] | verdict | hypothesis | fold form | parallelism | peak WS (MB) | notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| antisym | blade-llvm | ok | 21 | 0.02915 | 0.03047 | 0.001322 | ref |  |  | in-order | serial | 24.9 |  |
| antisym | blade-cpp | ok | 21 | 0.03002 | 0.03065 | 0.001257 | 1.006 [0.983, 1.035] | equivalent |  | in-order | serial | 25.7 |  |
| antisym | gfortran-natural | ok | 21 | 0.7687 | 0.7773 | 0.005975 | 25.507 [24.862, 26.022] | win | WIN ~2x (half the cells, no diagonal) |  | serial | 23.5 |  |
| antisym | gfortran-expert | ok | 21 | 0.03074 | 0.03177 | 0.001175 | 1.043 [1.008, 1.073] | detectable-win | parity +-15% |  | serial | 32.7 |  |
| antisym | ifx-natural | ok | 21 | 0.3303 | 0.3355 | 0.005871 | 11.011 [10.742, 11.288] | win | WIN ~2x (half the cells, no diagonal) |  | serial | 23.0 |  |
| antisym | ifx-expert | ok | 21 | 0.02909 | 0.03046 | 0.00121 | 1.000 [0.975, 1.020] | equivalent | parity +-15% |  | serial | 32.3 |  |
| comoment3 | blade-llvm | ok | 21 | 0.02539 | 0.02646 | 0.002291 | ref |  |  | in-order | serial | 11.6 |  |
| comoment3 | blade-cpp | ok | 21 | 0.01553 | 0.01612 | 0.0009649 | 0.609 [0.574, 0.630] | loss |  | in-order | serial | 12.7 |  |
| comoment3 | gfortran-natural | ok | 21 | 0.5999 | 0.6066 | 0.009442 | 22.924 [21.648, 23.097] | win | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | serial | 43.5 |  |
| comoment3 | gfortran-expert | ok | 21 | 0.01122 | 0.01173 | 0.0004945 | 0.443 [0.417, 0.450] | loss | parity to 1.3x loss |  | serial | 12.4 |  |
| comoment3 | ifx-natural | ok | 21 | 0.7171 | 0.7304 | 0.01487 | 27.602 [25.949, 27.817] | win | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | serial | 43.1 |  |
| comoment3 | ifx-expert | ok | 21 | 0.01139 | 0.01177 | 0.000874 | 0.445 [0.420, 0.461] | loss | parity to 1.3x loss |  | serial | 11.9 |  |
| pairkernel | blade-llvm | ok | 21 | 0.04066 | 0.0412 | 0.0004107 | ref |  |  | in-order | serial | 28.6 |  |
| pairkernel | blade-cpp | ok | 21 | 0.03727 | 0.0379 | 0.0005349 | 0.920 [0.912, 0.927] | detectable-loss |  | in-order | serial | 29.4 |  |
| pairkernel | gfortran-natural | ok | 21 | 0.06009 | 0.06032 | 0.000354 | 1.464 [1.458, 1.473] | win | WIN ~2x (half the transcendentals) |  | serial | 53.0 |  |
| pairkernel | gfortran-expert | ok | 21 | 0.03417 | 0.03437 | 0.000227 | 0.834 [0.830, 0.840] | loss | parity within 5% |  | serial | 29.1 |  |
| pairkernel | ifx-natural | ok | 21 | 0.06349 | 0.06378 | 0.001028 | 1.548 [1.542, 1.572] | win | WIN ~2x (half the transcendentals) |  | serial | 52.5 |  |
| pairkernel | ifx-expert | ok | 21 | 0.03159 | 0.03182 | 0.0002504 | 0.772 [0.769, 0.779] | loss | parity within 5% |  | serial | 28.6 |  |

## serial-fastmath-native

Blade reference per kernel: antisym=blade-llvm, comoment3=blade-llvm

| kernel | arm | status | n | min (s) | median (s) | IQR (s) | ratio vs ref [95% CI] | verdict | hypothesis | fold form | parallelism | peak WS (MB) | notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| antisym | blade-llvm | ok | 21 | 0.0269 | 0.02767 | 0.0009182 | ref |  |  | reordered (4 reassoc flags) | serial | 24.9 |  |
| antisym | blade-cpp | ok | 21 | 0.02789 | 0.02824 | 0.001186 | 1.021 [1.001, 1.058] | suppressed |  | reordered (5 SIMD_REDUCTION) | serial | 25.7 |  |
| antisym | gfortran-natural | ok | 21 | 0.1279 | 0.1597 | 0.04668 | 5.772 [4.723, 6.317] | suppressed | WIN ~2x (half the cells, no diagonal) |  | serial | 23.5 |  |
| antisym | gfortran-expert | ok | 21 | 0.03047 | 0.03161 | 0.001465 | 1.142 [1.105, 1.167] | suppressed | parity +-15% |  | serial | 32.7 |  |
| antisym | ifx-natural | ok | 21 | 0.08177 | 0.0846 | 0.008471 | 3.058 [2.983, 3.286] | suppressed | WIN ~2x (half the cells, no diagonal) |  | serial | 23.0 |  |
| antisym | ifx-expert | ok | 21 | 0.02896 | 0.03035 | 0.001006 | 1.097 [1.064, 1.115] | suppressed | parity +-15% |  | serial | 32.3 |  |

> antisym serial-fastmath-native: inference suppressed -- trend: gfortran-natural first-half vs second-half median moved 12.0%

| comoment3 | blade-llvm | ok | 21 | 0.009304 | 0.009622 | 0.0008919 | ref |  |  | reordered (6 reassoc flags) | serial | 11.6 |  |
| comoment3 | blade-cpp | ok | 21 | 0.009523 | 0.0106 | 0.001234 | 1.101 [1.029, 1.151] | suppressed |  | reordered (6 SIMD_REDUCTION) | serial | 12.7 |  |
| comoment3 | gfortran-natural | ok | 21 | 0.1454 | 0.1512 | 0.004357 | 15.714 [14.778, 16.184] | suppressed | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | serial | 43.5 |  |
| comoment3 | gfortran-expert | ok | 21 | 0.01124 | 0.01198 | 0.0009608 | 1.245 [1.153, 1.300] | suppressed | parity to 1.3x loss |  | serial | 12.4 |  |
| comoment3 | ifx-natural | ok | 21 | 0.08068 | 0.09076 | 0.007654 | 9.433 [8.837, 9.620] | suppressed | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | serial | 43.1 |  |
| comoment3 | ifx-expert | ok | 21 | 0.01133 | 0.01179 | 0.001169 | 1.226 [1.154, 1.318] | suppressed | parity to 1.3x loss |  | serial | 11.9 |  |

> comoment3 serial-fastmath-native: inference suppressed -- trend: blade-llvm first-half vs second-half median moved 5.9%; trend: blade-cpp first-half vs second-half median moved 7.1%


## Controls

- compute (recur/gfortran-expert/serial-repro-native/full): n=168 median 0.01299 s, baseline 0.01302 s
- memory (saxpy/gfortran-expert/serial-repro-native/control): n=168 median 0.005427 s, baseline 0.005315 s
