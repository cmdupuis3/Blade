# Runner record: consolidated tables (serial/mt8 x licensed/repro/fastmath x native, plus gram blas)

Label for every number: **uncapped: max processor state 100% (Silent scheme); verdicts suppressed by drift reported as indicative.** Full size, README minimum rounds, no --rounds override.
Ratio = median(arm)/median(Blade reference); > 1 means the arm is slower than Blade. Reference: blade-llvm in serial regimes, blade-cpp in mt8 / blas regimes and for maskagg (the LLVM lane falls back). `suppressed` = the harness withheld the verdict because the block failed a drift/trend/control check; the numbers are kept and indicative. Source of every row: the named session bundle summary.md (immutable).


## serial-licensed-native

Sources: session-042223 -> gram,comoment3,pairkernel,recur,stencil; session-121713 -> antisym; session-090344 -> fusedchain,onepass,maskagg,dot,saxpy

| kernel | arm | status | n | min (s) | median (s) | IQR (s) | ratio vs ref [95% CI] | verdict | hypothesis | fold form | parallelism | peak WS (MB) | incremental (s) [CI] | incremental ratio [CI] | notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| antisym | blade-llvm | ok | 11 | 0.07317 | 0.07395 | 0.0002563 | ref |  |  | reordered (7 reassoc flags) | serial | 17.4 |  |
| antisym | blade-cpp | ok | 11 | 0.1073 | 0.1079 | 0.001023 | 1.459 [1.452, 1.471] | win |  | reordered (7 SIMD_REDUCTION) | serial | 18.3 |  |
| antisym | gfortran-natural | ok | 11 | 0.7647 | 0.7709 | 0.002733 | 10.425 [10.371, 10.455] | win | WIN ~2x (half the cells, no diagonal) |  | serial | 23.4 |  |
| antisym | gfortran-expert | ok | 11 | 0.03024 | 0.03074 | 0.0002121 | 0.416 [0.414, 0.417] | loss | parity +-15% |  | serial | 32.7 |  |
| antisym | ifx-natural | ok | 11 | 0.3308 | 0.3329 | 0.002219 | 4.502 [4.475, 4.516] | win | WIN ~2x (half the cells, no diagonal) |  | serial | 22.9 |  |
| antisym | ifx-expert | ok | 11 | 0.02875 | 0.02954 | 0.0007387 | 0.400 [0.395, 0.407] | loss | parity +-15% |  | serial | 32.2 |  |
| comoment3 | blade-llvm | ok | 11 | 0.04043 | 0.04149 | 0.00117 | ref |  |  | reordered (6 reassoc flags) | serial | 11.1 |  |  |  |
| comoment3 | blade-cpp | ok | 11 | 0.1087 | 0.1097 | 0.0006488 | 2.644 [2.602, 2.690] | suppressed |  | reordered (6 SIMD_REDUCTION) | serial | 12.0 |  |  |  |
| comoment3 | gfortran-natural | ok | 11 | 0.6019 | 0.6034 | 0.002195 | 14.541 [14.307, 14.801] | suppressed | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | serial | 43.3 |  |  |  |
| comoment3 | gfortran-expert | ok | 11 | 0.01114 | 0.01149 | 0.000314 | 0.277 [0.272, 0.285] | suppressed | parity to 1.3x loss |  | serial | 12.1 |  |  |  |
| comoment3 | ifx-natural | ok | 11 | 0.7202 | 0.7237 | 0.003941 | 17.442 [17.209, 17.733] | suppressed | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | serial | 42.9 |  |  |  |
| comoment3 | ifx-expert | ok | 11 | 0.01148 | 0.0117 | 0.0003876 | 0.282 [0.277, 0.293] | suppressed | parity to 1.3x loss |  | serial | 11.7 |  |  |  |
| gram | blade-llvm | ok | 11 | 0.1519 | 0.1567 | 0.00612 | ref |  |  | reordered (8 reassoc flags) | serial | 26.4 |  |  |  |
| gram | blade-cpp | ok | 11 | 0.2508 | 0.2752 | 0.02033 | 1.756 [1.645, 1.828] | suppressed |  | reordered (5 SIMD_REDUCTION) | serial | 27.2 |  |  |  |
| gram | gfortran-natural | ok | 11 | 0.4808 | 0.6016 | 0.08762 | 3.838 [3.186, 3.988] | suppressed | win 1.5-2x if matmul is a naive nest; parity/loss if a blocked runtime kernel |  | serial | 36.7 |  |  |  |
| gram | gfortran-expert | ok | 11 | 0.0784 | 0.08168 | 0.002869 | 0.521 [0.507, 0.539] | suppressed | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | serial | 51.5 |  |  |  |
| gram | ifx-natural | ok | 11 | 0.5723 | 0.5762 | 0.003985 | 3.676 [3.624, 3.785] | suppressed | win 1.5-2x if matmul is a naive nest; parity/loss if a blocked runtime kernel |  | serial | 55.7 |  |  |  |
| gram | ifx-expert | ok | 11 | 0.06995 | 0.07315 | 0.002225 | 0.467 [0.461, 0.483] | suppressed | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | serial | 51.1 |  |  |  |
| pairkernel | blade-llvm | ok | 11 | 0.0411 | 0.04259 | 0.002076 | ref |  |  | reordered (4 reassoc flags) | serial | 28.4 |  |  |  |
| pairkernel | blade-cpp | ok | 11 | 0.07367 | 0.07725 | 0.004973 | 1.814 [1.764, 1.814] | suppressed |  | reordered (3 SIMD_REDUCTION) | serial | 29.2 |  |  |  |
| pairkernel | gfortran-natural | ok | 11 | 0.06109 | 0.06192 | 0.001451 | 1.454 [1.405, 1.471] | suppressed | WIN ~2x (half the transcendentals) |  | serial | 52.7 |  |  |  |
| pairkernel | gfortran-expert | ok | 11 | 0.0324 | 0.03277 | 0.0002508 | 0.769 [0.737, 0.780] | suppressed | parity within 5% |  | serial | 28.8 |  |  |  |
| pairkernel | ifx-natural | ok | 11 | 0.06354 | 0.06453 | 0.001023 | 1.515 [1.448, 1.532] | suppressed | WIN ~2x (half the transcendentals) |  | serial | 52.3 |  |  |  |
| pairkernel | ifx-expert | ok | 11 | 0.03054 | 0.03082 | 0.0007751 | 0.724 [0.691, 0.743] | suppressed | parity within 5% |  | serial | 28.4 |  |  |  |
| fusedchain | blade-llvm | ok | 21 | 0.004228 | 0.004571 | 0.0004095 | ref |  |  | reordered (2 reassoc flags) | serial | 19.6 | 0.0006254 [0.000458, 0.0009815] | ref |  |
| fusedchain | blade-cpp | ok | 21 | 0.006388 | 0.006754 | 0.0006211 | 1.478 [1.400, 1.551] | suppressed |  | reordered (1 SIMD_REDUCTION) | serial | 20.4 | 0.0006697 [0.0003317, 0.0009588] | 1.071 [0.459, 1.778] |  |
| fusedchain | gfortran-natural | ok | 21 | 0.01681 | 0.01798 | 0.001562 | 3.934 [3.749, 4.214] | suppressed | WIN 1.5-2.5x (no temporaries) |  | serial | 65.9 | 0.01121 [0.01069, 0.01216] | 17.929 [11.336, 25.225] |  |
| fusedchain | gfortran-expert | ok | 21 | 0.006552 | 0.006905 | 0.000403 | 1.511 [1.433, 1.563] | suppressed | parity |  | serial | 20.1 | 0.0007251 [0.0005915, 0.0009701] | 1.159 [0.689, 1.718] |  |
| fusedchain | ifx-natural | ok | 21 | 0.01271 | 0.01345 | 0.001078 | 2.943 [2.808, 3.122] | suppressed | WIN 1.5-2.5x (no temporaries) |  | serial | 65.5 | 0.008912 [0.008556, 0.009709] | 14.250 [8.977, 19.471] |  |
| fusedchain | ifx-expert | ok | 21 | 0.004363 | 0.004897 | 0.0005451 | 1.071 [0.986, 1.124] | suppressed | parity |  | serial | 19.7 | 0.001107 [0.0007348, 0.001348] | 1.769 [0.954, 2.459] |  |
| fusedchain | blade-llvm-inputonly | ok | 21 | 0.003566 | 0.003946 | 0.0003916 |  |  |  | reordered (1 reassoc flags) | serial | 19.6 |  |  |  |
| fusedchain | blade-cpp-inputonly | ok | 21 | 0.00571 | 0.006084 | 0.0004335 |  |  |  | in-order | serial | 20.4 |  |  |  |
| fusedchain | gfortran-natural-inputonly | ok | 21 | 0.006526 | 0.006769 | 0.0001908 |  |  |  |  | serial | 20.1 |  |  |  |
| fusedchain | gfortran-expert-inputonly | ok | 21 | 0.005815 | 0.00618 | 0.0002745 |  |  |  |  | serial | 20.1 |  |  |  |
| fusedchain | ifx-natural-inputonly | ok | 21 | 0.004342 | 0.00454 | 0.0003556 |  |  |  |  | serial | 19.7 |  |  |  |
| fusedchain | ifx-expert-inputonly | ok | 21 | 0.003582 | 0.00379 | 0.0003594 |  |  |  |  | serial | 19.7 |  |  |  |
| maskagg | blade-llvm | fallback | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | a compound-index initializer; expected fallback (the LLVM lane refuses `compound` (notes/llvm-acceptance-B.md); blade-cpp is the Blade figure) |
| maskagg | blade-cpp | ok | 21 | 0.0635 | 0.06716 | 0.007288 | ref |  |  | in-order | serial | 46.3 | 0.06434 [0.0629, 0.06801] | ref |  |
| maskagg | gfortran-natural | ok | 21 | 0.008169 | 0.008301 | 0.0005376 | 0.124 [0.117, 0.128] | suppressed | LOSS 1.3-2x |  | serial | 17.3 | 0.00496 [0.004874, 0.005124] | 0.077 [0.073, 0.081] |  |
| maskagg | gfortran-expert | ok | 21 | 0.003902 | 0.003993 | 0.000371 | 0.059 [0.056, 0.064] | suppressed | LOSS 1.3-2x |  | serial | 16.4 | 0.001088 [0.001005, 0.001372] | 0.017 [0.016, 0.021] |  |
| maskagg | ifx-natural | ok | 21 | 0.01021 | 0.01056 | 0.0003739 | 0.157 [0.148, 0.161] | suppressed | LOSS 1.3-2x |  | serial | 23.5 | 0.008379 [0.008222, 0.008452] | 0.130 [0.122, 0.134] |  |
| maskagg | ifx-expert | ok | 21 | 0.002788 | 0.002919 | 0.0004587 | 0.043 [0.041, 0.048] | suppressed | LOSS 1.3-2x |  | serial | 15.9 | 0.001193 [0.001159, 0.001427] | 0.019 [0.017, 0.023] |  |
| maskagg | blade-llvm-inputonly | ok | 21 | 0.00165 | 0.001717 | 0.0001221 |  |  |  | reordered (1 reassoc flags) | serial | 12.0 |  |  |  |
| maskagg | blade-cpp-inputonly | ok | 21 | 0.002755 | 0.002822 | 0.0001425 |  |  |  | in-order | serial | 12.8 |  |  |  |
| maskagg | gfortran-natural-inputonly | ok | 21 | 0.003285 | 0.003341 | 0.0001399 |  |  |  |  | serial | 12.5 |  |  |  |
| maskagg | gfortran-expert-inputonly | ok | 21 | 0.002838 | 0.002906 | 0.000102 |  |  |  |  | serial | 12.5 |  |  |  |
| maskagg | ifx-natural-inputonly | ok | 21 | 0.002158 | 0.002181 | 5.36e-05 |  |  |  |  | serial | 12.0 |  |  |  |
| maskagg | ifx-expert-inputonly | ok | 21 | 0.001674 | 0.001726 | 4.21e-05 |  |  |  |  | serial | 12.0 |  |  |  |
| onepass | blade-llvm | ok | 21 | 0.01105 | 0.01167 | 0.0005775 | ref |  |  | reordered (5 reassoc flags) | serial | 42.5 | 0.002178 [0.001623, 0.002373] | ref |  |
| onepass | blade-cpp | ok | 21 | 0.01634 | 0.01677 | 0.0005779 | 1.437 [1.396, 1.480] | win |  | reordered (1 SIMD_REDUCTION) | serial | 43.3 | 0.001941 [0.00155, 0.002235] | 0.891 [0.691, 1.173] |  |
| onepass | gfortran-natural | ok | 21 | 0.03233 | 0.03287 | 0.0005672 | 2.816 [2.781, 2.893] | win | WIN 2-3x (one pass vs four) |  | serial | 43.0 | 0.01573 [0.01544, 0.01614] | 7.221 [6.670, 9.524] |  |
| onepass | gfortran-expert | ok | 21 | 0.01654 | 0.01683 | 0.0005547 | 1.442 [1.419, 1.497] | win | parity |  | serial | 43.0 | 0.001593 [0.001384, 0.002116] | 0.732 [0.630, 1.154] |  |
| onepass | ifx-natural | ok | 21 | 0.01245 | 0.01296 | 0.0003827 | 1.110 [1.095, 1.146] | win | WIN 2-3x (one pass vs four) |  | serial | 42.6 | 0.0009776 [0.000406, 0.001523] | 0.449 [0.188, 0.795] |  |
| onepass | ifx-expert | ok | 21 | 0.01107 | 0.01178 | 0.0009472 | 1.009 [0.974, 1.051] | equivalent | parity |  | serial | 42.6 | 0.001928 [0.001538, 0.002255] | 0.885 [0.663, 1.308] |  |
| onepass | blade-llvm-inputonly | ok | 21 | 0.009297 | 0.009495 | 0.0005903 |  |  |  | reordered (1 reassoc flags) | serial | 42.5 |  |  |  |
| onepass | blade-cpp-inputonly | ok | 21 | 0.01454 | 0.01483 | 0.0004223 |  |  |  | in-order | serial | 43.3 |  |  |  |
| onepass | gfortran-natural-inputonly | ok | 21 | 0.01661 | 0.01715 | 0.000455 |  |  |  |  | serial | 43.0 |  |  |  |
| onepass | gfortran-expert-inputonly | ok | 21 | 0.0148 | 0.01524 | 0.0005366 |  |  |  |  | serial | 43.0 |  |  |  |
| onepass | ifx-natural-inputonly | ok | 21 | 0.01105 | 0.01198 | 0.00121 |  |  |  |  | serial | 42.6 |  |  |  |
| onepass | ifx-expert-inputonly | ok | 21 | 0.009378 | 0.009849 | 0.0005566 |  |  |  |  | serial | 42.6 |  |  |  |
| recur | blade-llvm | ok | 21 | 0.1051 | 0.1169 | 0.009206 | ref |  |  | reordered (3 reassoc flags) | serial | 51.4 |  |  |  |
| recur | blade-cpp | ok | 21 | 0.116 | 0.1341 | 0.009558 | 1.147 [1.093, 1.182] | suppressed |  | reordered (3 SIMD_REDUCTION) | serial | 52.3 |  |  |  |
| recur | gfortran-natural | ok | 21 | 0.01752 | 0.0197 | 0.00295 | 0.169 [0.151, 0.178] | suppressed | parity |  | serial | 51.4 |  |  |  |
| recur | gfortran-expert | ok | 21 | 0.009116 | 0.01048 | 0.001877 | 0.090 [0.084, 0.096] | suppressed | LOSS 1.5-3x |  | serial | 50.8 |  |  |  |
| recur | ifx-natural | ok | 21 | 0.01504 | 0.016 | 0.001486 | 0.137 [0.131, 0.145] | suppressed | parity |  | serial | 51.0 |  |  |  |
| recur | ifx-expert | ok | 21 | 0.009439 | 0.01127 | 0.002312 | 0.096 [0.088, 0.107] | suppressed | LOSS 1.5-3x |  | serial | 50.4 |  |  |  |
| stencil | blade-llvm | ok | 21 | 0.01189 | 0.01351 | 0.003255 | ref |  |  | reordered (4 reassoc flags) | serial | 49.7 | 0.007253 [0.006547, 0.008686] | ref |  |
| stencil | blade-cpp | ok | 21 | 0.0149 | 0.01634 | 0.003056 | 1.209 [1.102, 1.333] | suppressed |  | reordered (4 SIMD_REDUCTION) | serial | 50.6 | 0.007231 [0.00642, 0.008756] | 0.997 [0.848, 1.166] |  |
| stencil | gfortran-natural | ok | 21 | 0.01678 | 0.01776 | 0.002885 | 1.314 [1.233, 1.452] | suppressed | parity (bandwidth-bound) |  | serial | 50.7 | 0.007371 [0.006663, 0.009109] | 1.016 [0.925, 1.257] |  |
| stencil | gfortran-expert | ok | 21 | 0.0136 | 0.01446 | 0.002 | 1.070 [0.985, 1.146] | suppressed | parity |  | serial | 50.7 | 0.004842 [0.004416, 0.005965] | 0.668 [0.569, 0.790] |  |
| stencil | ifx-natural | ok | 21 | 0.01816 | 0.01896 | 0.0007733 | 1.403 [1.245, 1.516] | suppressed | parity (bandwidth-bound) |  | serial | 50.2 | 0.01201 [0.01151, 0.01224] | 1.656 [1.364, 1.850] |  |
| stencil | ifx-expert | ok | 21 | 0.01058 | 0.01164 | 0.003758 | 0.861 [0.843, 0.949] | suppressed | parity |  | serial | 50.3 | 0.005698 [0.005059, 0.007581] | 0.786 [0.750, 1.000] |  |
| stencil | blade-llvm-inputonly | ok | 21 | 0.005801 | 0.006259 | 0.001044 |  |  |  | reordered (2 reassoc flags) | serial | 27.3 |  |  |  |
| stencil | blade-cpp-inputonly | ok | 21 | 0.008783 | 0.009107 | 0.0008309 |  |  |  | reordered (1 SIMD_REDUCTION) | serial | 28.1 |  |  |  |
| stencil | gfortran-natural-inputonly | ok | 21 | 0.009832 | 0.01039 | 0.0007091 |  |  |  |  | serial | 27.8 |  |  |  |
| stencil | gfortran-expert-inputonly | ok | 21 | 0.008917 | 0.009615 | 0.001391 |  |  |  |  | serial | 27.8 |  |  |  |
| stencil | ifx-natural-inputonly | ok | 21 | 0.006497 | 0.006947 | 0.001039 |  |  |  |  | serial | 27.4 |  |  |  |
| stencil | ifx-expert-inputonly | ok | 21 | 0.005513 | 0.005939 | 0.0007879 |  |  |  |  | serial | 27.4 |  |  |  |
| dot | blade-llvm | ok | 21 | 0.0126 | 0.0132 | 0.001436 | ref |  |  | reordered (3 reassoc flags) | serial | 50.2 | 0.001731 [0.001257, 0.002332] | ref |  |
| dot | blade-cpp | ok | 21 | 0.01865 | 0.01928 | 0.001349 | 1.460 [1.369, 1.518] | suppressed |  | reordered (1 SIMD_REDUCTION) | serial | 50.9 | 0.001571 [0.0005624, 0.001952] | 0.907 [0.306, 1.310] |  |
| dot | gfortran-natural | ok | 21 | 0.01976 | 0.02028 | 0.001278 | 1.536 [1.441, 1.594] | suppressed | parity |  | serial | 50.6 | 0.002029 [0.001687, 0.002659] | 1.172 [0.846, 1.723] |  |
| dot | gfortran-expert | ok | 21 | 0.01817 | 0.01844 | 0.0008363 | 1.397 [1.329, 1.449] | suppressed | parity |  | serial | 50.6 | 0.001125 [0.0008258, 0.001732] | 0.650 [0.381, 1.217] |  |
| dot | ifx-natural | ok | 21 | 0.01358 | 0.01404 | 0.0006264 | 1.063 [1.011, 1.101] | suppressed | parity |  | serial | 50.2 | 0.002262 [0.001672, 0.002596] | 1.306 [0.741, 1.936] |  |
| dot | ifx-expert | ok | 21 | 0.01199 | 0.01267 | 0.001054 | 0.960 [0.905, 1.012] | suppressed | parity |  | serial | 50.2 | 0.001569 [0.001203, 0.002163] | 0.906 [0.593, 1.453] |  |
| dot | blade-llvm-inputonly | ok | 21 | 0.01096 | 0.01147 | 0.0008042 |  |  |  | reordered (2 reassoc flags) | serial | 50.1 |  |  |  |
| dot | blade-cpp-inputonly | ok | 21 | 0.01743 | 0.01771 | 0.001502 |  |  |  | in-order | serial | 50.9 |  |  |  |
| dot | gfortran-natural-inputonly | ok | 21 | 0.01734 | 0.01825 | 0.001063 |  |  |  |  | serial | 50.6 |  |  |  |
| dot | gfortran-expert-inputonly | ok | 21 | 0.0169 | 0.01732 | 0.0009167 |  |  |  |  | serial | 50.7 |  |  |  |
| dot | ifx-natural-inputonly | ok | 21 | 0.01126 | 0.01178 | 0.0009702 |  |  |  |  | serial | 50.2 |  |  |  |
| dot | ifx-expert-inputonly | ok | 21 | 0.01054 | 0.0111 | 0.0007975 |  |  |  |  | serial | 50.2 |  |  |  |
| saxpy | blade-llvm | ok | 21 | 0.01196 | 0.01428 | 0.00317 | ref |  |  | reordered (3 reassoc flags) | serial | 49.6 | 0.006083 [0.004876, 0.007832] | ref |  |
| saxpy | blade-cpp | ok | 21 | 0.0167 | 0.01789 | 0.003701 | 1.253 [1.116, 1.435] | suppressed |  | reordered (1 SIMD_REDUCTION) | serial | 50.6 | 0.005777 [0.004975, 0.0078] | 0.950 [0.730, 1.407] |  |
| saxpy | gfortran-natural | ok | 21 | 0.01651 | 0.01747 | 0.001643 | 1.224 [1.090, 1.349] | suppressed | parity |  | serial | 50.7 | 0.005109 [0.004021, 0.006192] | 0.840 [0.597, 1.100] |  |
| saxpy | gfortran-expert | ok | 21 | 0.01456 | 0.01562 | 0.00265 | 1.094 [0.975, 1.254] | suppressed | parity |  | serial | 50.7 | 0.003622 [0.00277, 0.005728] | 0.595 [0.413, 0.957] |  |
| saxpy | ifx-natural | ok | 21 | 0.01295 | 0.01413 | 0.002521 | 0.989 [0.881, 1.110] | suppressed | parity |  | serial | 50.2 | 0.005848 [0.005103, 0.007107] | 0.962 [0.743, 1.273] |  |
| saxpy | ifx-expert | ok | 21 | 0.01051 | 0.01193 | 0.00379 | 0.836 [0.742, 0.970] | suppressed | parity |  | serial | 50.2 | 0.004293 [0.002807, 0.005824] | 0.706 [0.471, 1.045] |  |
| saxpy | blade-llvm-inputonly | ok | 21 | 0.007398 | 0.008194 | 0.001264 |  |  |  | reordered (2 reassoc flags) | serial | 34.9 |  |  |  |
| saxpy | blade-cpp-inputonly | ok | 21 | 0.01173 | 0.01211 | 0.0006331 |  |  |  | in-order | serial | 35.7 |  |  |  |
| saxpy | gfortran-natural-inputonly | ok | 21 | 0.0116 | 0.01236 | 0.001411 |  |  |  |  | serial | 35.4 |  |  |  |
| saxpy | gfortran-expert-inputonly | ok | 21 | 0.01131 | 0.012 | 0.001498 |  |  |  |  | serial | 35.4 |  |  |  |
| saxpy | ifx-natural-inputonly | ok | 21 | 0.00754 | 0.008277 | 0.001141 |  |  |  |  | serial | 34.9 |  |  |  |
| saxpy | ifx-expert-inputonly | ok | 21 | 0.007202 | 0.007636 | 0.001454 |  |  |  |  | serial | 34.9 |  |  |  |

## mt8-licensed-native

Sources: session-113651 -> antisym,comoment3,gram,pairkernel,saxpy; session-100329 -> fusedchain,onepass,recur,stencil,dot

| kernel | arm | status | n | min (s) | median (s) | IQR (s) | ratio vs ref [95% CI] | verdict | hypothesis | fold form | parallelism | peak WS (MB) | incremental (s) [CI] | incremental ratio [CI] | notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| antisym | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (7 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| antisym | blade-cpp | ok | 21 | 0.02098 | 0.02258 | 0.002201 | ref |  |  | reordered (6 SIMD_REDUCTION) | openmp (4 construct(s)) | 18.7 |  |  |  |
| antisym | gfortran-natural | ok | 21 | 0.1093 | 0.1169 | 0.003743 | 5.177 [5.060, 5.273] | suppressed | WIN ~2x (half the cells, no diagonal) |  | openmp runtime linked | 23.8 |  |  |  |
| antisym | gfortran-expert | ok | 21 | 0.009466 | 0.01038 | 0.001237 | 0.460 [0.440, 0.478] | suppressed | parity +-15% |  | openmp runtime linked | 33.1 |  |  |  |
| antisym | ifx-natural | ok | 21 | 0.06468 | 0.07139 | 0.009553 | 3.162 [3.009, 3.386] | suppressed | WIN ~2x (half the cells, no diagonal) |  | openmp runtime linked | 25.4 |  |  |  |
| antisym | ifx-expert | ok | 21 | 0.009988 | 0.0109 | 0.001045 | 0.483 [0.460, 0.515] | suppressed | parity +-15% |  | openmp runtime linked | 34.6 |  |  |  |
| comoment3 | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (6 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| comoment3 | blade-cpp | ok | 21 | 0.02001 | 0.02062 | 0.0008745 | ref |  |  | reordered (4 SIMD_REDUCTION) | openmp (4 construct(s)) | 12.6 |  |  |  |
| comoment3 | gfortran-natural | ok | 21 | 0.08373 | 0.09194 | 0.004281 | 4.458 [4.295, 4.526] | suppressed | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | openmp runtime linked | 43.9 |  |  |  |
| comoment3 | gfortran-expert | ok | 21 | 0.004615 | 0.004998 | 0.0002994 | 0.242 [0.233, 0.248] | suppressed | parity to 1.3x loss |  | openmp runtime linked | 14.5 |  |  |  |
| comoment3 | ifx-natural | ok | 21 | 0.1015 | 0.1115 | 0.006531 | 5.406 [5.257, 5.613] | suppressed | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | openmp runtime linked | 45.4 |  |  |  |
| comoment3 | ifx-expert | ok | 21 | 0.003796 | 0.004025 | 0.0001324 | 0.195 [0.191, 0.199] | suppressed | parity to 1.3x loss |  | openmp runtime linked | 15.8 |  |  |  |
| gram | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (8 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| gram | blade-cpp | ok | 21 | 0.04006 | 0.04375 | 0.005286 | ref |  |  | reordered (3 SIMD_REDUCTION) | openmp (4 construct(s)) | 27.8 |  |  |  |
| gram | gfortran-natural | ok | 21 | 0.5084 | 0.6141 | 0.09889 | 14.034 [13.086, 14.946] | suppressed | win 1.5-2x if matmul is a naive nest; parity/loss if a blocked runtime kernel |  | openmp runtime linked | 37.3 |  |  | matmul is unannotated: parallel preparation, serial kernel unless the runtime threads matmul (FORT records) |
| gram | gfortran-expert | ok | 21 | 0.01937 | 0.02119 | 0.000815 | 0.484 [0.454, 0.515] | suppressed | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | openmp runtime linked | 52.1 |  |  |  |
| gram | ifx-natural | ok | 21 | 0.5945 | 0.6311 | 0.01628 | 14.423 [13.507, 15.089] | suppressed | win 1.5-2x if matmul is a naive nest; parity/loss if a blocked runtime kernel |  | openmp runtime linked | 58.3 |  |  | matmul is unannotated: parallel preparation, serial kernel unless the runtime threads matmul (FORT records) |
| gram | ifx-expert | ok | 21 | 0.01791 | 0.02002 | 0.001131 | 0.458 [0.427, 0.481] | suppressed | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | openmp runtime linked | 53.7 |  |  |  |
| pairkernel | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (4 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| pairkernel | blade-cpp | ok | 21 | 0.01424 | 0.01611 | 0.002726 | ref |  |  | reordered (1 SIMD_REDUCTION) | openmp (4 construct(s)) | 29.8 |  |  |  |
| pairkernel | gfortran-natural | ok | 21 | 0.01361 | 0.01528 | 0.002157 | 0.948 [0.877, 1.013] | suppressed | WIN ~2x (half the transcendentals) |  | openmp runtime linked | 53.3 |  |  |  |
| pairkernel | gfortran-expert | ok | 21 | 0.005746 | 0.006262 | 0.0003226 | 0.389 [0.373, 0.405] | suppressed | parity within 5% |  | openmp runtime linked | 29.5 |  |  |  |
| pairkernel | ifx-natural | ok | 21 | 0.01476 | 0.01643 | 0.00215 | 1.019 [0.964, 1.124] | suppressed | WIN ~2x (half the transcendentals) |  | openmp runtime linked | 54.8 |  |  |  |
| pairkernel | ifx-expert | ok | 21 | 0.005802 | 0.006209 | 0.0003881 | 0.385 [0.374, 0.406] | suppressed | parity within 5% |  | openmp runtime linked | 30.9 |  |  |  |
| fusedchain | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (2 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| fusedchain | blade-cpp | ok | 21 | 0.003739 | 0.004128 | 0.0005298 | ref |  |  | in-order | openmp (2 construct(s)) | 20.8 | 0.0006109 [0.000239, 0.0009268] | ref |  |
| fusedchain | gfortran-natural | ok | 21 | 0.01114 | 0.01247 | 0.001589 | 3.020 [2.824, 3.239] | suppressed | WIN 1.5-2.5x (no temporaries) |  | openmp runtime linked | 66.4 | 0.008881 [0.008439, 0.009459] | 14.538 [9.490, 38.110] |  |
| fusedchain | gfortran-expert | ok | 21 | 0.003037 | 0.003253 | 0.0003655 | 0.788 [0.738, 0.829] | suppressed | parity |  | openmp runtime linked | 20.6 | 0.0003592 [6.47e-05, 0.0005791] | 0.588 [0.102, 1.551] |  |
| fusedchain | ifx-natural | ok | 21 | 0.01173 | 0.01263 | 0.0008225 | 3.060 [2.847, 3.186] | suppressed | WIN 1.5-2.5x (no temporaries) |  | openmp runtime linked | 68.0 | 0.008508 [0.008014, 0.008755] | 13.927 [9.047, 34.755] |  |
| fusedchain | ifx-expert | ok | 21 | 0.003298 | 0.003509 | 0.000343 | 0.850 [0.799, 0.916] | suppressed | parity |  | openmp runtime linked | 22.1 | 0.0003294 [9.51e-05, 0.000542] | 0.539 [0.154, 1.405] |  |
| fusedchain | blade-llvm-inputonly | serial-only | 0 | -- | -- | -- |  |  |  | reordered (1 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| fusedchain | blade-cpp-inputonly | ok | 21 | 0.003155 | 0.003517 | 0.0005411 |  |  |  | in-order | openmp (1 construct(s)) | 20.8 |  |  |  |
| fusedchain | gfortran-natural-inputonly | ok | 21 | 0.003444 | 0.003585 | 0.0001797 |  |  |  |  | openmp runtime linked | 20.6 |  |  |  |
| fusedchain | gfortran-expert-inputonly | ok | 21 | 0.002663 | 0.002894 | 0.0004293 |  |  |  |  | openmp runtime linked | 20.6 |  |  |  |
| fusedchain | ifx-natural-inputonly | ok | 21 | 0.003896 | 0.004124 | 0.0001186 |  |  |  |  | openmp runtime linked | 22.1 |  |  |  |
| fusedchain | ifx-expert-inputonly | ok | 21 | 0.003057 | 0.00318 | 0.0002564 |  |  |  |  | openmp runtime linked | 22.1 |  |  |  |
| onepass | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (5 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| onepass | blade-cpp | no-parallel-form | 0 | -- | -- | -- |  |  |  | reordered (1 SIMD_REDUCTION) |  |  |  |  | kernel.json no_parallel_form (spec: no licensed parallel spelling) |
| onepass | gfortran-natural | ok | 21 | 0.01455 | 0.01534 | 0.0006284 |  |  | WIN 2-3x (one pass vs four) |  | openmp runtime linked | 43.5 |  |  |  |
| onepass | gfortran-expert | ok | 21 | 0.007733 | 0.009075 | 0.0008121 |  |  | parity |  | openmp runtime linked | 43.5 |  |  |  |
| onepass | ifx-natural | ok | 21 | 0.01504 | 0.01576 | 0.0003841 |  |  | WIN 2-3x (one pass vs four) |  | openmp runtime linked | 45.0 |  |  |  |
| onepass | ifx-expert | ok | 21 | 0.008736 | 0.009403 | 0.0007688 |  |  | parity |  | openmp runtime linked | 45.0 |  |  |  |
| onepass | blade-llvm-inputonly | serial-only | 0 | -- | -- | -- |  |  |  | reordered (1 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| onepass | blade-cpp-inputonly | no-parallel-form | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | kernel.json no_parallel_form (spec: no licensed parallel spelling) |
| onepass | gfortran-natural-inputonly | ok | 21 | 0.008259 | 0.009145 | 0.0008675 |  |  |  |  | openmp runtime linked | 43.5 |  |  |  |
| onepass | gfortran-expert-inputonly | ok | 21 | 0.006545 | 0.007356 | 0.0009792 |  |  |  |  | openmp runtime linked | 43.5 |  |  |  |
| onepass | ifx-natural-inputonly | ok | 21 | 0.008725 | 0.009553 | 0.0007585 |  |  |  |  | openmp runtime linked | 45.0 |  |  |  |
| onepass | ifx-expert-inputonly | ok | 21 | 0.006871 | 0.007625 | 0.00106 |  |  |  |  | openmp runtime linked | 45.0 |  |  |  |
| recur | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (3 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| recur | blade-cpp | no-parallel-form | 0 | -- | -- | -- |  |  |  | reordered (3 SIMD_REDUCTION) |  |  |  |  | kernel.json no_parallel_form (spec: no licensed parallel spelling) |
| recur | gfortran-natural | ok | 21 | 0.0713 | 0.07324 | 0.002285 |  |  | parity |  | openmp runtime linked | 51.9 |  |  |  |
| recur | gfortran-expert | ok | 21 | 0.00657 | 0.007207 | 0.001139 |  |  | LOSS 1.5-3x |  | openmp runtime linked | 51.3 |  |  |  |
| recur | ifx-natural | ok | 21 | 0.01507 | 0.01636 | 0.002069 |  |  | parity |  | openmp runtime linked | 53.4 |  |  |  |
| recur | ifx-expert | ok | 21 | 0.006771 | 0.007911 | 0.001568 |  |  | LOSS 1.5-3x |  | openmp runtime linked | 52.8 |  |  |  |
| stencil | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (4 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| stencil | blade-cpp | ok | 21 | 0.009584 | 0.01149 | 0.001915 | ref |  |  | reordered (3 SIMD_REDUCTION) | openmp (3 construct(s)) | 51.2 | 0.006293 [0.005562, 0.00729] | ref |  |
| stencil | gfortran-natural | ok | 21 | 0.00843 | 0.009477 | 0.0007641 | 0.825 [0.757, 0.894] | suppressed | parity (bandwidth-bound) |  | openmp runtime linked | 51.2 | 0.004899 [0.004588, 0.005327] | 0.778 [0.650, 0.917] |  |
| stencil | gfortran-expert | ok | 21 | 0.007594 | 0.008968 | 0.001189 | 0.780 [0.713, 0.833] | suppressed | parity |  | openmp runtime linked | 51.1 | 0.004372 [0.003748, 0.004718] | 0.695 [0.582, 0.789] |  |
| stencil | ifx-natural | ok | 21 | 0.009775 | 0.01019 | 0.0008873 | 0.887 [0.818, 0.954] | suppressed | parity (bandwidth-bound) |  | openmp runtime linked | 52.7 | 0.005049 [0.0048, 0.005485] | 0.802 [0.701, 0.912] |  |
| stencil | ifx-expert | ok | 21 | 0.008381 | 0.00912 | 0.001541 | 0.794 [0.730, 0.867] | suppressed | parity |  | openmp runtime linked | 52.7 | 0.004021 [0.003618, 0.005046] | 0.639 [0.550, 0.797] |  |
| stencil | blade-llvm-inputonly | serial-only | 0 | -- | -- | -- |  |  |  | reordered (2 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| stencil | blade-cpp-inputonly | ok | 21 | 0.004244 | 0.005197 | 0.000614 |  |  |  | reordered (1 SIMD_REDUCTION) | openmp (1 construct(s)) | 28.5 |  |  |  |
| stencil | gfortran-natural-inputonly | ok | 21 | 0.004179 | 0.004578 | 0.000661 |  |  |  |  | openmp runtime linked | 28.3 |  |  |  |
| stencil | gfortran-expert-inputonly | ok | 21 | 0.003923 | 0.004596 | 0.0003376 |  |  |  |  | openmp runtime linked | 28.3 |  |  |  |
| stencil | ifx-natural-inputonly | ok | 21 | 0.004793 | 0.005141 | 0.0002909 |  |  |  |  | openmp runtime linked | 29.8 |  |  |  |
| stencil | ifx-expert-inputonly | ok | 21 | 0.004557 | 0.005099 | 0.0004658 |  |  |  |  | openmp runtime linked | 29.8 |  |  |  |
| dot | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (3 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| dot | blade-cpp | ok | 21 | 0.01017 | 0.012 | 0.0009239 | ref |  |  | in-order | openmp (2 construct(s)) | 51.3 | 0.001677 [0.000969, 0.002381] | ref |  |
| dot | gfortran-natural | ok | 21 | 0.01073 | 0.01141 | 0.0006569 | 0.951 [0.928, 0.989] | suppressed | parity |  | openmp runtime linked | 51.1 | 0.002595 [0.002235, 0.003038] | 1.548 [1.081, 2.724] |  |
| dot | gfortran-expert | ok | 21 | 0.009646 | 0.01053 | 0.0006028 | 0.878 [0.859, 0.905] | suppressed | parity |  | openmp runtime linked | 51.1 | 0.002011 [0.001794, 0.002308] | 1.200 [0.809, 2.141] |  |
| dot | ifx-natural | ok | 21 | 0.0113 | 0.01183 | 0.0003726 | 0.986 [0.963, 1.025] | suppressed | parity |  | openmp runtime linked | 52.7 | 0.002403 [0.001975, 0.002696] | 1.433 [1.019, 2.425] |  |
| dot | ifx-expert | ok | 21 | 0.01023 | 0.01096 | 0.0004431 | 0.914 [0.888, 0.948] | suppressed | parity |  | openmp runtime linked | 52.7 | 0.00194 [0.00171, 0.002273] | 1.157 [0.797, 2.049] |  |
| dot | blade-llvm-inputonly | serial-only | 0 | -- | -- | -- |  |  |  | reordered (2 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| dot | blade-cpp-inputonly | ok | 21 | 0.00751 | 0.01032 | 0.001512 |  |  |  | in-order | openmp (1 construct(s)) | 51.3 |  |  |  |
| dot | gfortran-natural-inputonly | ok | 21 | 0.008162 | 0.008814 | 0.001058 |  |  |  |  | openmp runtime linked | 51.1 |  |  |  |
| dot | gfortran-expert-inputonly | ok | 21 | 0.007465 | 0.008519 | 0.0003739 |  |  |  |  | openmp runtime linked | 51.1 |  |  |  |
| dot | ifx-natural-inputonly | ok | 21 | 0.008582 | 0.009429 | 0.0007777 |  |  |  |  | openmp runtime linked | 52.7 |  |  |  |
| dot | ifx-expert-inputonly | ok | 21 | 0.007999 | 0.009023 | 0.0006695 |  |  |  |  | openmp runtime linked | 52.7 |  |  |  |
| saxpy | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (3 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| saxpy | blade-cpp | ok | 21 | 0.01049 | 0.0114 | 0.0005977 | ref |  |  | in-order | openmp (3 construct(s)) | 51.2 | 0.004429 [0.004113, 0.004699] | ref |  |
| saxpy | gfortran-natural | ok | 21 | 0.009706 | 0.01036 | 0.0006287 | 0.909 [0.876, 0.942] | suppressed | parity |  | openmp runtime linked | 51.3 | 0.004351 [0.003939, 0.004677] | 0.982 [0.877, 1.091] |  |
| saxpy | gfortran-expert | ok | 21 | 0.008286 | 0.008963 | 0.0003849 | 0.786 [0.766, 0.808] | suppressed | parity |  | openmp runtime linked | 51.3 | 0.003402 [0.002927, 0.003622] | 0.768 [0.659, 0.846] |  |
| saxpy | ifx-natural | ok | 21 | 0.009888 | 0.01059 | 0.0006783 | 0.929 [0.892, 0.963] | suppressed | parity |  | openmp runtime linked | 52.9 | 0.004279 [0.003976, 0.004611] | 0.966 [0.871, 1.068] |  |
| saxpy | ifx-expert | ok | 21 | 0.008817 | 0.009224 | 0.0003052 | 0.809 [0.792, 0.830] | suppressed | parity |  | openmp runtime linked | 52.9 | 0.003205 [0.003045, 0.003482] | 0.724 [0.679, 0.795] |  |
| saxpy | blade-llvm-inputonly | serial-only | 0 | -- | -- | -- |  |  |  | reordered (2 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| saxpy | blade-cpp-inputonly | ok | 21 | 0.005811 | 0.00697 | 0.0006486 |  |  |  | in-order | openmp (1 construct(s)) | 36.3 |  |  |  |
| saxpy | gfortran-natural-inputonly | ok | 21 | 0.00543 | 0.006012 | 0.0004447 |  |  |  |  | openmp runtime linked | 36.0 |  |  |  |
| saxpy | gfortran-expert-inputonly | ok | 21 | 0.005137 | 0.00556 | 0.0007242 |  |  |  |  | openmp runtime linked | 36.0 |  |  |  |
| saxpy | ifx-natural-inputonly | ok | 21 | 0.005915 | 0.006312 | 0.0005098 |  |  |  |  | openmp runtime linked | 37.6 |  |  |  |
| saxpy | ifx-expert-inputonly | ok | 21 | 0.005338 | 0.006019 | 0.0005122 |  |  |  |  | openmp runtime linked | 37.6 |  |  |  |

## serial-repro-native

Sources: session-122606 -> antisym,comoment3,gram,pairkernel,recur,stencil; session-130438 -> fusedchain,onepass,maskagg,dot,saxpy

| kernel | arm | status | n | min (s) | median (s) | IQR (s) | ratio vs ref [95% CI] | verdict | hypothesis | fold form | parallelism | peak WS (MB) | incremental (s) [CI] | incremental ratio [CI] | notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| antisym | blade-llvm | ok | 11 | 0.5045 | 0.5082 | 0.004205 | ref |  |  | in-order | serial | 17.4 |  |  |  |
| antisym | blade-cpp | ok | 11 | 0.4073 | 0.4113 | 0.005101 | 0.809 [0.802, 0.816] | suppressed |  | in-order | serial | 18.3 |  |  |  |
| antisym | gfortran-natural | ok | 11 | 0.768 | 0.7739 | 0.004098 | 1.523 [1.510, 1.529] | suppressed | WIN ~2x (half the cells, no diagonal) |  | serial | 23.4 |  |  |  |
| antisym | gfortran-expert | ok | 11 | 0.03105 | 0.03272 | 0.001761 | 0.064 [0.062, 0.066] | suppressed | parity +-15% |  | serial | 32.7 |  |  |  |
| antisym | ifx-natural | ok | 11 | 0.3309 | 0.3352 | 0.006682 | 0.659 [0.652, 0.666] | suppressed | WIN ~2x (half the cells, no diagonal) |  | serial | 23.0 |  |  |  |
| antisym | ifx-expert | ok | 11 | 0.02926 | 0.03145 | 0.001656 | 0.062 [0.059, 0.063] | suppressed | parity +-15% |  | serial | 32.2 |  |  |  |
| comoment3 | blade-llvm | ok | 11 | 0.1564 | 0.1568 | 0.0005501 | ref |  |  | in-order | serial | 11.3 |  |  |  |
| comoment3 | blade-cpp | ok | 11 | 0.1796 | 0.1806 | 0.0011 | 1.152 [1.147, 1.159] | suppressed |  | in-order | serial | 12.2 |  |  |  |
| comoment3 | gfortran-natural | ok | 11 | 0.5996 | 0.6026 | 0.004986 | 3.842 [3.822, 3.867] | suppressed | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | serial | 43.4 |  |  |  |
| comoment3 | gfortran-expert | ok | 11 | 0.01128 | 0.01177 | 0.0006122 | 0.075 [0.073, 0.077] | suppressed | parity to 1.3x loss |  | serial | 12.3 |  |  |  |
| comoment3 | ifx-natural | ok | 11 | 0.7178 | 0.722 | 0.004725 | 4.604 [4.582, 4.618] | suppressed | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | serial | 43.0 |  |  |  |
| comoment3 | ifx-expert | ok | 11 | 0.0114 | 0.0117 | 0.0003195 | 0.075 [0.074, 0.076] | suppressed | parity to 1.3x loss |  | serial | 11.8 |  |  |  |
| gram | blade-llvm | ok | 11 | 1.217 | 1.219 | 0.004434 | ref |  |  | in-order | serial | 26.6 |  |  |  |
| gram | blade-cpp | ok | 11 | 0.9496 | 0.9536 | 0.004668 | 0.782 [0.778, 0.786] | suppressed |  | in-order | serial | 27.4 |  |  |  |
| gram | gfortran-natural | ok | 11 | 0.498 | 0.5561 | 0.09859 | 0.456 [0.414, 0.507] | suppressed | win 1.5-2x if matmul is a naive nest; parity/loss if a blocked runtime kernel |  | serial | 36.8 |  |  |  |
| gram | gfortran-expert | ok | 11 | 0.07676 | 0.07784 | 0.0008346 | 0.064 [0.063, 0.064] | suppressed | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | serial | 51.6 |  |  |  |
| gram | ifx-natural | ok | 11 | 0.5682 | 0.569 | 0.001298 | 0.467 [0.465, 0.468] | suppressed | win 1.5-2x if matmul is a naive nest; parity/loss if a blocked runtime kernel |  | serial | 55.9 |  |  |  |
| gram | ifx-expert | ok | 11 | 0.07019 | 0.07065 | 0.0003376 | 0.058 [0.058, 0.058] | suppressed | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | serial | 51.2 |  |  |  |
| pairkernel | blade-llvm | ok | 11 | 0.04099 | 0.04278 | 0.002025 | ref |  |  | in-order | serial | 28.5 |  |  |  |
| pairkernel | blade-cpp | ok | 11 | 0.07311 | 0.07759 | 0.005033 | 1.813 [1.748, 1.883] | suppressed |  | in-order | serial | 29.3 |  |  |  |
| pairkernel | gfortran-natural | ok | 11 | 0.06065 | 0.06279 | 0.002652 | 1.468 [1.428, 1.514] | suppressed | WIN ~2x (half the transcendentals) |  | serial | 52.9 |  |  |  |
| pairkernel | gfortran-expert | ok | 11 | 0.03423 | 0.03522 | 0.001041 | 0.823 [0.798, 0.846] | suppressed | parity within 5% |  | serial | 29.0 |  |  |  |
| pairkernel | ifx-natural | ok | 11 | 0.06373 | 0.06505 | 0.001358 | 1.520 [1.485, 1.563] | suppressed | WIN ~2x (half the transcendentals) |  | serial | 52.5 |  |  |  |
| pairkernel | ifx-expert | ok | 11 | 0.03164 | 0.032 | 0.0006767 | 0.748 [0.735, 0.777] | suppressed | parity within 5% |  | serial | 28.5 |  |  |  |
| fusedchain | blade-llvm | ok | 21 | 0.006774 | 0.00716 | 0.0006001 | ref |  |  | in-order | serial | 19.8 | 0.002534 [0.00196, 0.002858] | ref |  |
| fusedchain | blade-cpp | ok | 21 | 0.008159 | 0.008374 | 0.0004274 | 1.170 [1.119, 1.198] | suppressed |  | in-order | serial | 20.6 | 0.001542 [0.001385, 0.001684] | 0.608 [0.516, 0.804] |  |
| fusedchain | gfortran-natural | ok | 21 | 0.01735 | 0.02008 | 0.003587 | 2.805 [2.597, 3.099] | suppressed | WIN 1.5-2.5x (no temporaries) |  | serial | 66.1 | 0.01328 [0.01183, 0.01503] | 5.241 [4.360, 7.478] |  |
| fusedchain | gfortran-expert | ok | 21 | 0.008242 | 0.008775 | 0.001155 | 1.226 [1.168, 1.330] | suppressed | parity |  | serial | 20.3 | 0.001983 [0.001624, 0.002671] | 0.782 [0.616, 1.142] |  |
| fusedchain | ifx-natural | ok | 21 | 0.0134 | 0.01478 | 0.002706 | 2.065 [1.920, 2.206] | suppressed | WIN 1.5-2.5x (no temporaries) |  | serial | 65.6 | 0.01009 [0.009414, 0.011] | 3.982 [3.375, 5.463] |  |
| fusedchain | ifx-expert | ok | 21 | 0.004878 | 0.005319 | 0.0007485 | 0.743 [0.705, 0.803] | suppressed | parity |  | serial | 19.8 | 0.0006533 [0.0002228, 0.0009868] | 0.258 [0.091, 0.411] |  |
| fusedchain | blade-llvm-inputonly | ok | 21 | 0.004351 | 0.004625 | 0.0008889 |  |  |  | in-order | serial | 19.8 |  |  |  |
| fusedchain | blade-cpp-inputonly | ok | 21 | 0.006575 | 0.006833 | 0.0003545 |  |  |  | in-order | serial | 20.6 |  |  |  |
| fusedchain | gfortran-natural-inputonly | ok | 21 | 0.006547 | 0.006801 | 0.0006178 |  |  |  |  | serial | 20.3 |  |  |  |
| fusedchain | gfortran-expert-inputonly | ok | 21 | 0.006547 | 0.006792 | 0.000411 |  |  |  |  | serial | 20.3 |  |  |  |
| fusedchain | ifx-natural-inputonly | ok | 21 | 0.004379 | 0.004692 | 0.0006433 |  |  |  |  | serial | 19.9 |  |  |  |
| fusedchain | ifx-expert-inputonly | ok | 21 | 0.004379 | 0.004666 | 0.0006159 |  |  |  |  | serial | 19.8 |  |  |  |
| maskagg | blade-llvm | fallback | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | a compound-index initializer; expected fallback (the LLVM lane refuses `compound` (notes/llvm-acceptance-B.md); blade-cpp is the Blade figure) |
| maskagg | blade-cpp | ok | 21 | 0.06456 | 0.06886 | 0.003996 | ref |  |  | in-order | serial | 46.5 | 0.06556 [0.06502, 0.06831] | ref |  |
| maskagg | gfortran-natural | ok | 21 | 0.008172 | 0.008288 | 0.0001849 | 0.120 [0.116, 0.122] | loss | LOSS 1.3-2x |  | serial | 17.4 | 0.004967 [0.004881, 0.005039] | 0.076 [0.072, 0.077] |  |
| maskagg | gfortran-expert | ok | 21 | 0.004749 | 0.004982 | 0.0003055 | 0.072 [0.067, 0.074] | loss | LOSS 1.3-2x |  | serial | 16.5 | 0.001623 [0.001402, 0.001741] | 0.025 [0.021, 0.027] |  |
| maskagg | ifx-natural | ok | 21 | 0.01032 | 0.01052 | 0.0002819 | 0.153 [0.147, 0.156] | loss | LOSS 1.3-2x |  | serial | 23.6 | 0.008327 [0.008264, 0.008505] | 0.127 [0.122, 0.130] |  |
| maskagg | ifx-expert | ok | 21 | 0.003645 | 0.003752 | 0.0001219 | 0.054 [0.052, 0.055] | loss | LOSS 1.3-2x |  | serial | 16.0 | 0.001576 [0.001515, 0.001621] | 0.024 [0.023, 0.025] |  |
| maskagg | blade-llvm-inputonly | ok | 21 | 0.002146 | 0.002177 | 5.01e-05 |  |  |  | in-order | serial | 12.2 |  |  |  |
| maskagg | blade-cpp-inputonly | ok | 21 | 0.003235 | 0.003304 | 7.87e-05 |  |  |  | in-order | serial | 12.9 |  |  |  |
| maskagg | gfortran-natural-inputonly | ok | 21 | 0.003292 | 0.003321 | 7.23e-05 |  |  |  |  | serial | 12.6 |  |  |  |
| maskagg | gfortran-expert-inputonly | ok | 21 | 0.003281 | 0.003359 | 0.0001282 |  |  |  |  | serial | 12.7 |  |  |  |
| maskagg | ifx-natural-inputonly | ok | 21 | 0.00215 | 0.002189 | 6.28e-05 |  |  |  |  | serial | 12.2 |  |  |  |
| maskagg | ifx-expert-inputonly | ok | 21 | 0.002149 | 0.002175 | 3.28e-05 |  |  |  |  | serial | 12.2 |  |  |  |
| onepass | blade-llvm | ok | 21 | 0.01647 | 0.01716 | 0.0009009 | ref |  |  | in-order | serial | 42.7 | 0.005372 [0.004542, 0.005893] | ref |  |
| onepass | blade-cpp | ok | 21 | 0.02079 | 0.02109 | 0.001064 | 1.229 [1.210, 1.276] | suppressed |  | in-order | serial | 43.5 | 0.003975 [0.003689, 0.004738] | 0.740 [0.655, 0.941] |  |
| onepass | gfortran-natural | ok | 21 | 0.03227 | 0.03282 | 0.0009868 | 1.913 [1.883, 1.965] | suppressed | WIN 2-3x (one pass vs four) |  | serial | 43.2 | 0.0157 [0.01512, 0.01632] | 2.923 [2.666, 3.464] |  |
| onepass | gfortran-expert | ok | 21 | 0.02106 | 0.02167 | 0.000806 | 1.263 [1.243, 1.292] | suppressed | parity |  | serial | 43.2 | 0.004403 [0.003714, 0.004887] | 0.820 [0.676, 0.978] |  |
| onepass | ifx-natural | ok | 21 | 0.01243 | 0.0135 | 0.001516 | 0.787 [0.754, 0.834] | suppressed | WIN 2-3x (one pass vs four) |  | serial | 42.7 | 0.001251 [0.0007991, 0.002063] | 0.233 [0.141, 0.402] |  |
| onepass | ifx-expert | ok | 21 | 0.01249 | 0.01321 | 0.0006998 | 0.770 [0.751, 0.793] | suppressed | parity |  | serial | 42.7 | 0.001138 [0.0006175, 0.001534] | 0.212 [0.117, 0.299] |  |
| onepass | blade-llvm-inputonly | ok | 21 | 0.01116 | 0.01179 | 0.001456 |  |  |  | in-order | serial | 42.7 |  |  |  |
| onepass | blade-cpp-inputonly | ok | 21 | 0.01655 | 0.01711 | 0.001127 |  |  |  | in-order | serial | 43.5 |  |  |  |
| onepass | gfortran-natural-inputonly | ok | 21 | 0.01673 | 0.01712 | 0.0009254 |  |  |  |  | serial | 43.2 |  |  |  |
| onepass | gfortran-expert-inputonly | ok | 21 | 0.01662 | 0.01726 | 0.001334 |  |  |  |  | serial | 43.2 |  |  |  |
| onepass | ifx-natural-inputonly | ok | 21 | 0.01117 | 0.01225 | 0.0009553 |  |  |  |  | serial | 42.7 |  |  |  |
| onepass | ifx-expert-inputonly | ok | 21 | 0.01132 | 0.01207 | 0.0008942 |  |  |  |  | serial | 42.7 |  |  |  |
| recur | blade-llvm | ok | 21 | 0.1067 | 0.111 | 0.01311 | ref |  |  | in-order | serial | 51.5 |  |  |  |
| recur | blade-cpp | ok | 21 | 0.116 | 0.1235 | 0.01666 | 1.113 [1.047, 1.188] | suppressed |  | in-order | serial | 52.5 |  |  |  |
| recur | gfortran-natural | ok | 21 | 0.01741 | 0.01806 | 0.0006955 | 0.163 [0.150, 0.168] | suppressed | parity |  | serial | 51.6 |  |  |  |
| recur | gfortran-expert | ok | 21 | 0.01248 | 0.01295 | 0.0008918 | 0.117 [0.109, 0.122] | suppressed | LOSS 1.5-3x |  | serial | 51.0 |  |  |  |
| recur | ifx-natural | ok | 21 | 0.01461 | 0.01489 | 0.0005881 | 0.134 [0.123, 0.138] | suppressed | parity |  | serial | 51.1 |  |  |  |
| recur | ifx-expert | ok | 21 | 0.0129 | 0.01324 | 0.0007753 | 0.119 [0.112, 0.125] | suppressed | LOSS 1.5-3x |  | serial | 50.5 |  |  |  |
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
| dot | blade-llvm | ok | 21 | 0.01669 | 0.01698 | 0.000541 | ref |  |  | in-order | serial | 50.3 | 0.003278 [0.003021, 0.003651] | ref |  |
| dot | blade-cpp | ok | 21 | 0.02208 | 0.02273 | 0.001866 | 1.338 [1.305, 1.356] | win |  | in-order | serial | 51.1 | 0.002632 [0.002219, 0.002907] | 0.803 [0.652, 0.960] |  |
| dot | gfortran-natural | ok | 21 | 0.01979 | 0.02014 | 0.0005635 | 1.186 [1.160, 1.202] | win | parity |  | serial | 50.8 | 0.002379 [0.002206, 0.002671] | 0.726 [0.628, 0.857] |  |
| dot | gfortran-expert | ok | 21 | 0.01971 | 0.02007 | 0.001013 | 1.182 [1.156, 1.194] | win | parity |  | serial | 50.8 | 0.002348 [0.002169, 0.002606] | 0.716 [0.622, 0.888] |  |
| dot | ifx-natural | ok | 21 | 0.01368 | 0.01415 | 0.0006806 | 0.833 [0.813, 0.853] | loss | parity |  | serial | 50.4 | 0.002372 [0.0019, 0.002786] | 0.723 [0.577, 0.867] |  |
| dot | ifx-expert | ok | 21 | 0.01169 | 0.01222 | 0.0007343 | 0.720 [0.696, 0.744] | loss | parity |  | serial | 50.4 | 0.0006071 [0.0003605, 0.001043] | 0.185 [0.105, 0.328] |  |
| dot | blade-llvm-inputonly | ok | 21 | 0.01339 | 0.0137 | 0.0004868 |  |  |  | in-order | serial | 50.3 |  |  |  |
| dot | blade-cpp-inputonly | ok | 21 | 0.01967 | 0.0201 | 0.0005128 |  |  |  | in-order | serial | 51.1 |  |  |  |
| dot | gfortran-natural-inputonly | ok | 21 | 0.01729 | 0.01776 | 0.0003529 |  |  |  |  | serial | 50.8 |  |  |  |
| dot | gfortran-expert-inputonly | ok | 21 | 0.01744 | 0.01772 | 0.0004476 |  |  |  |  | serial | 50.8 |  |  |  |
| dot | ifx-natural-inputonly | ok | 21 | 0.01131 | 0.01178 | 0.001156 |  |  |  |  | serial | 50.4 |  |  |  |
| dot | ifx-expert-inputonly | ok | 21 | 0.01124 | 0.01161 | 0.0005402 |  |  |  |  | serial | 50.4 |  |  |  |
| saxpy | blade-llvm | ok | 21 | 0.01515 | 0.01549 | 0.0003152 | ref |  |  | in-order | serial | 49.8 | 0.006356 [0.006183, 0.006631] | ref |  |
| saxpy | blade-cpp | ok | 21 | 0.01815 | 0.0184 | 0.0004201 | 1.188 [1.171, 1.204] | win |  | in-order | serial | 50.7 | 0.005113 [0.004958, 0.005343] | 0.804 [0.755, 0.837] |  |
| saxpy | gfortran-natural | ok | 21 | 0.01635 | 0.01672 | 0.0003598 | 1.079 [1.062, 1.093] | detectable-win | parity |  | serial | 50.8 | 0.004875 [0.004736, 0.005098] | 0.767 [0.726, 0.810] |  |
| saxpy | gfortran-expert | ok | 21 | 0.01524 | 0.01536 | 0.0002874 | 0.992 [0.977, 1.003] | equivalent | parity |  | serial | 50.8 | 0.003516 [0.003454, 0.003669] | 0.553 [0.527, 0.587] |  |
| saxpy | ifx-natural | ok | 21 | 0.01272 | 0.01303 | 0.0002574 | 0.841 [0.828, 0.849] | loss | parity |  | serial | 50.4 | 0.005294 [0.005188, 0.005483] | 0.833 [0.798, 0.870] |  |
| saxpy | ifx-expert | ok | 21 | 0.009455 | 0.009802 | 0.0002335 | 0.633 [0.621, 0.639] | loss | parity |  | serial | 50.4 | 0.002038 [0.001822, 0.002194] | 0.321 [0.283, 0.345] |  |
| saxpy | blade-llvm-inputonly | ok | 21 | 0.008785 | 0.009131 | 0.0003558 |  |  |  | in-order | serial | 35.1 |  |  |  |
| saxpy | blade-cpp-inputonly | ok | 21 | 0.0131 | 0.01328 | 0.0002484 |  |  |  | in-order | serial | 35.8 |  |  |  |
| saxpy | gfortran-natural-inputonly | ok | 21 | 0.0115 | 0.01184 | 0.0002356 |  |  |  |  | serial | 35.6 |  |  |  |
| saxpy | gfortran-expert-inputonly | ok | 21 | 0.01158 | 0.01185 | 7.13e-05 |  |  |  |  | serial | 35.5 |  |  |  |
| saxpy | ifx-natural-inputonly | ok | 21 | 0.007569 | 0.007732 | 0.0002024 |  |  |  |  | serial | 35.1 |  |  |  |
| saxpy | ifx-expert-inputonly | ok | 21 | 0.007423 | 0.007764 | 0.0003424 |  |  |  |  | serial | 35.1 |  |  |  |

## mt8-repro-native

Sources: session-140038 -> all kernels

| kernel | arm | status | n | min (s) | median (s) | IQR (s) | ratio vs ref [95% CI] | verdict | hypothesis | fold form | parallelism | peak WS (MB) | incremental (s) [CI] | incremental ratio [CI] | notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| antisym | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | LLVM lane has no OpenMP |
| antisym | blade-cpp | ok | 7 | 0.06021 | 0.0607 | 0.001098 | ref |  |  | in-order | openmp (2 construct(s)); threaded maps, serial folds (BLADE_FP_REPRO) | 18.7 |  |  |  |
| antisym | gfortran-natural | ok | 7 | 0.1011 | 0.1067 | 0.005357 | 1.758 [1.656, 1.809] | suppressed | WIN ~2x (half the cells, no diagonal) |  | openmp runtime linked | 23.8 |  |  |  |
| antisym | gfortran-expert | ok | 7 | 0.009114 | 0.009358 | 0.0002865 | 0.154 [0.149, 0.158] | suppressed | parity +-15% |  | openmp runtime linked | 33.1 |  |  |  |
| antisym | ifx-natural | ok | 7 | 0.04833 | 0.05918 | 0.01748 | 0.975 [0.799, 1.148] | suppressed | WIN ~2x (half the cells, no diagonal) |  | openmp runtime linked | 25.4 |  |  |  |
| antisym | ifx-expert | ok | 7 | 0.009526 | 0.009841 | 0.0003663 | 0.162 [0.158, 0.167] | suppressed | parity +-15% |  | openmp runtime linked | 34.7 |  |  |  |
| comoment3 | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | LLVM lane has no OpenMP |
| comoment3 | blade-cpp | ok | 7 | 0.09158 | 0.09268 | 0.001545 | ref |  |  | in-order | openmp (2 construct(s)); threaded maps, serial folds (BLADE_FP_REPRO) | 12.6 |  |  |  |
| comoment3 | gfortran-natural | ok | 7 | 0.08356 | 0.09318 | 0.00925 | 1.005 [0.961, 1.060] | suppressed | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | openmp runtime linked | 43.9 |  |  |  |
| comoment3 | gfortran-expert | ok | 7 | 0.004697 | 0.005096 | 0.0001546 | 0.055 [0.053, 0.056] | suppressed | parity to 1.3x loss |  | openmp runtime linked | 14.5 |  |  |  |
| comoment3 | ifx-natural | ok | 7 | 0.1049 | 0.1162 | 0.007122 | 1.254 [1.173, 1.298] | suppressed | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | openmp runtime linked | 45.4 |  |  |  |
| comoment3 | ifx-expert | ok | 7 | 0.003933 | 0.004021 | 0.0002987 | 0.043 [0.043, 0.048] | suppressed | parity to 1.3x loss |  | openmp runtime linked | 15.6 |  |  |  |
| gram | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | LLVM lane has no OpenMP |
| gram | blade-cpp | ok | 7 | 0.1366 | 0.1383 | 0.005351 | ref |  |  | in-order | openmp (2 construct(s)); threaded maps, serial folds (BLADE_FP_REPRO) | 27.8 |  |  |  |
| gram | gfortran-natural | ok | 7 | 0.5571 | 0.6986 | 0.1531 | 5.050 [4.132, 5.385] | suppressed | win 1.5-2x if matmul is a naive nest; parity/loss if a blocked runtime kernel |  | openmp runtime linked | 37.3 |  |  | matmul is unannotated: parallel preparation, serial kernel unless the runtime threads matmul (FORT records) |
| gram | gfortran-expert | ok | 7 | 0.01952 | 0.02019 | 0.0009949 | 0.146 [0.139, 0.151] | suppressed | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | openmp runtime linked | 52.1 |  |  |  |
| gram | ifx-natural | ok | 7 | 0.5861 | 0.6055 | 0.01205 | 4.377 [4.090, 4.464] | suppressed | win 1.5-2x if matmul is a naive nest; parity/loss if a blocked runtime kernel |  | openmp runtime linked | 58.3 |  |  | matmul is unannotated: parallel preparation, serial kernel unless the runtime threads matmul (FORT records) |
| gram | ifx-expert | ok | 7 | 0.01833 | 0.01857 | 0.001041 | 0.134 [0.127, 0.147] | suppressed | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | openmp runtime linked | 53.7 |  |  |  |
| pairkernel | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | LLVM lane has no OpenMP |
| pairkernel | blade-cpp | ok | 21 | 0.05403 | 0.05619 | 0.002464 | ref |  |  | in-order | openmp (2 construct(s)); threaded maps, serial folds (BLADE_FP_REPRO) | 29.7 |  |  |  |
| pairkernel | gfortran-natural | ok | 21 | 0.01336 | 0.01424 | 0.002365 | 0.253 [0.242, 0.268] | suppressed | WIN ~2x (half the transcendentals) |  | openmp runtime linked | 53.3 |  |  |  |
| pairkernel | gfortran-expert | ok | 21 | 0.005991 | 0.006202 | 0.0004373 | 0.110 [0.109, 0.115] | suppressed | parity within 5% |  | openmp runtime linked | 29.5 |  |  |  |
| pairkernel | ifx-natural | ok | 21 | 0.01474 | 0.01663 | 0.002301 | 0.296 [0.276, 0.313] | suppressed | WIN ~2x (half the transcendentals) |  | openmp runtime linked | 54.8 |  |  |  |
| pairkernel | ifx-expert | ok | 21 | 0.005925 | 0.006213 | 0.0003489 | 0.111 [0.108, 0.115] | suppressed | parity within 5% |  | openmp runtime linked | 30.9 |  |  |  |
| fusedchain | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | LLVM lane has no OpenMP |
| fusedchain | blade-cpp | ok | 21 | 0.005415 | 0.005559 | 0.0002549 | ref |  |  | in-order | openmp (1 construct(s)); threaded maps, serial folds (BLADE_FP_REPRO) | 21.0 | 0.001542 [0.001444, 0.001722] | ref |  |
| fusedchain | gfortran-natural | ok | 21 | 0.01092 | 0.01146 | 0.0005934 | 2.062 [1.997, 2.105] | win | WIN 1.5-2.5x (no temporaries) |  | openmp runtime linked | 66.5 | 0.00793 [0.007667, 0.00814] | 5.143 [4.594, 5.491] |  |
| fusedchain | gfortran-expert | ok | 21 | 0.004049 | 0.004181 | 0.0002332 | 0.752 [0.728, 0.776] | loss | parity |  | openmp runtime linked | 20.7 | 0.0006551 [0.0004736, 0.0007675] | 0.425 [0.303, 0.518] |  |
| fusedchain | ifx-natural | ok | 21 | 0.01106 | 0.01149 | 0.0003007 | 2.067 [2.009, 2.095] | win | WIN 1.5-2.5x (no temporaries) |  | openmp runtime linked | 68.2 | 0.00745 [0.007346, 0.007647] | 4.832 [4.349, 5.201] |  |
| fusedchain | ifx-expert | ok | 21 | 0.004366 | 0.004642 | 0.0002936 | 0.835 [0.809, 0.860] | loss | parity |  | openmp runtime linked | 22.3 | 0.000643 [0.0005362, 0.0007651] | 0.417 [0.337, 0.520] |  |
| fusedchain | blade-llvm-inputonly | serial-only | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | LLVM lane has no OpenMP |
| fusedchain | blade-cpp-inputonly | ok | 21 | 0.003855 | 0.004017 | 0.0001229 |  |  |  | in-order | openmp (1 construct(s)); threaded maps, serial folds (BLADE_FP_REPRO) | 21.0 |  |  |  |
| fusedchain | gfortran-natural-inputonly | ok | 21 | 0.003383 | 0.003535 | 0.0002523 |  |  |  |  | openmp runtime linked | 20.7 |  |  |  |
| fusedchain | gfortran-expert-inputonly | ok | 21 | 0.003344 | 0.003526 | 0.0002505 |  |  |  |  | openmp runtime linked | 20.7 |  |  |  |
| fusedchain | ifx-natural-inputonly | ok | 21 | 0.003809 | 0.004042 | 0.0001782 |  |  |  |  | openmp runtime linked | 22.3 |  |  |  |
| fusedchain | ifx-expert-inputonly | ok | 21 | 0.003837 | 0.003999 | 0.0001626 |  |  |  |  | openmp runtime linked | 22.2 |  |  |  |
| onepass | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | LLVM lane has no OpenMP |
| onepass | blade-cpp | no-parallel-form | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | kernel.json no_parallel_form (spec: no licensed parallel spelling) |
| onepass | gfortran-natural | ok | 21 | 0.01453 | 0.01494 | 0.0005766 |  |  | WIN 2-3x (one pass vs four) |  | openmp runtime linked | 43.6 |  |  |  |
| onepass | gfortran-expert | ok | 21 | 0.01065 | 0.01094 | 0.0003713 |  |  | parity |  | openmp runtime linked | 43.6 |  |  |  |
| onepass | ifx-natural | ok | 21 | 0.01499 | 0.01535 | 0.0003249 |  |  | WIN 2-3x (one pass vs four) |  | openmp runtime linked | 45.1 |  |  |  |
| onepass | ifx-expert | ok | 21 | 0.01109 | 0.01141 | 0.0002429 |  |  | parity |  | openmp runtime linked | 45.1 |  |  |  |
| onepass | blade-llvm-inputonly | serial-only | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | LLVM lane has no OpenMP |
| onepass | blade-cpp-inputonly | no-parallel-form | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | kernel.json no_parallel_form (spec: no licensed parallel spelling) |
| onepass | gfortran-natural-inputonly | ok | 21 | 0.008115 | 0.008508 | 0.0005057 |  |  |  |  | openmp runtime linked | 43.6 |  |  |  |
| onepass | gfortran-expert-inputonly | ok | 21 | 0.008095 | 0.00828 | 0.0004396 |  |  |  |  | openmp runtime linked | 43.6 |  |  |  |
| onepass | ifx-natural-inputonly | ok | 21 | 0.008627 | 0.008839 | 0.0003524 |  |  |  |  | openmp runtime linked | 45.1 |  |  |  |
| onepass | ifx-expert-inputonly | ok | 21 | 0.00857 | 0.009136 | 0.0004397 |  |  |  |  | openmp runtime linked | 45.1 |  |  |  |
| recur | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | LLVM lane has no OpenMP |
| recur | blade-cpp | no-parallel-form | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | kernel.json no_parallel_form (spec: no licensed parallel spelling) |
| recur | gfortran-natural | ok | 21 | 0.06932 | 0.0714 | 0.002326 |  |  | parity |  | openmp runtime linked | 52.0 |  |  |  |
| recur | gfortran-expert | ok | 21 | 0.00625 | 0.006822 | 0.0005991 |  |  | LOSS 1.5-3x |  | openmp runtime linked | 51.4 |  |  |  |
| recur | ifx-natural | ok | 21 | 0.01396 | 0.01479 | 0.0006251 |  |  | parity |  | openmp runtime linked | 53.5 |  |  |  |
| recur | ifx-expert | ok | 21 | 0.006968 | 0.007337 | 0.0002349 |  |  | LOSS 1.5-3x |  | openmp runtime linked | 52.9 |  |  |  |
| stencil | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | LLVM lane has no OpenMP |
| stencil | blade-cpp | ok | 21 | 0.01175 | 0.01208 | 0.001001 | ref |  |  | in-order | openmp (2 construct(s)); threaded maps, serial folds (BLADE_FP_REPRO) | 51.3 | 0.006483 [0.006212, 0.006821] | ref |  |
| stencil | gfortran-natural | ok | 21 | 0.008449 | 0.008628 | 0.0004169 | 0.714 [0.695, 0.739] | suppressed | parity (bandwidth-bound) |  | openmp runtime linked | 51.3 | 0.004238 [0.004078, 0.004529] | 0.654 [0.612, 0.701] |  |
| stencil | gfortran-expert | ok | 21 | 0.007571 | 0.008 | 0.0002345 | 0.662 [0.639, 0.675] | suppressed | parity |  | openmp runtime linked | 51.3 | 0.003642 [0.003474, 0.003743] | 0.562 [0.519, 0.588] |  |
| stencil | ifx-natural | ok | 21 | 0.00972 | 0.01001 | 0.0002891 | 0.828 [0.807, 0.839] | suppressed | parity (bandwidth-bound) |  | openmp runtime linked | 52.9 | 0.005217 [0.005068, 0.005325] | 0.805 [0.753, 0.848] |  |
| stencil | ifx-expert | ok | 21 | 0.008015 | 0.008333 | 0.0003509 | 0.690 [0.665, 0.707] | suppressed | parity |  | openmp runtime linked | 52.8 | 0.003556 [0.003353, 0.003754] | 0.549 [0.506, 0.586] |  |
| stencil | blade-llvm-inputonly | serial-only | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | LLVM lane has no OpenMP |
| stencil | blade-cpp-inputonly | ok | 21 | 0.005368 | 0.0056 | 0.0003565 |  |  |  | in-order | openmp (1 construct(s)); threaded maps, serial folds (BLADE_FP_REPRO) | 28.7 |  |  |  |
| stencil | gfortran-natural-inputonly | ok | 21 | 0.004152 | 0.00439 | 0.0001987 |  |  |  |  | openmp runtime linked | 28.4 |  |  |  |
| stencil | gfortran-expert-inputonly | ok | 21 | 0.004104 | 0.004357 | 0.0002225 |  |  |  |  | openmp runtime linked | 28.4 |  |  |  |
| stencil | ifx-natural-inputonly | ok | 21 | 0.004609 | 0.00479 | 0.0002811 |  |  |  |  | openmp runtime linked | 30.0 |  |  |  |
| stencil | ifx-expert-inputonly | ok | 21 | 0.004438 | 0.004777 | 0.0002441 |  |  |  |  | openmp runtime linked | 29.9 |  |  |  |
| dot | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | LLVM lane has no OpenMP |
| dot | blade-cpp | ok | 21 | 0.01299 | 0.01354 | 0.001746 | ref |  |  | in-order | openmp (1 construct(s)); threaded maps, serial folds (BLADE_FP_REPRO) | 51.5 | 0.002408 [0.001974, 0.003924] | ref |  |
| dot | gfortran-natural | ok | 21 | 0.01067 | 0.01119 | 0.0004212 | 0.827 [0.737, 0.847] | suppressed | parity |  | openmp runtime linked | 51.3 | 0.002484 [0.001998, 0.002873] | 1.032 [0.596, 1.265] |  |
| dot | gfortran-expert | ok | 21 | 0.01073 | 0.01107 | 0.0002932 | 0.818 [0.739, 0.833] | suppressed | parity |  | openmp runtime linked | 51.2 | 0.002648 [0.002491, 0.002777] | 1.099 [0.673, 1.345] |  |
| dot | ifx-natural | ok | 21 | 0.01076 | 0.01152 | 0.0002168 | 0.851 [0.773, 0.871] | suppressed | parity |  | openmp runtime linked | 52.8 | 0.002594 [0.002371, 0.002791] | 1.077 [0.659, 1.357] |  |
| dot | ifx-expert | ok | 21 | 0.01035 | 0.01148 | 0.000357 | 0.848 [0.768, 0.868] | suppressed | parity |  | openmp runtime linked | 52.8 | 0.00254 [0.002269, 0.002788] | 1.055 [0.654, 1.289] |  |
| dot | blade-llvm-inputonly | serial-only | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | LLVM lane has no OpenMP |
| dot | blade-cpp-inputonly | ok | 21 | 0.01059 | 0.01113 | 0.0006875 |  |  |  | in-order | openmp (1 construct(s)); threaded maps, serial folds (BLADE_FP_REPRO) | 51.5 |  |  |  |
| dot | gfortran-natural-inputonly | ok | 21 | 0.008123 | 0.00871 | 0.0008943 |  |  |  |  | openmp runtime linked | 51.2 |  |  |  |
| dot | gfortran-expert-inputonly | ok | 21 | 0.008143 | 0.008419 | 0.0003286 |  |  |  |  | openmp runtime linked | 51.2 |  |  |  |
| dot | ifx-natural-inputonly | ok | 21 | 0.008649 | 0.008924 | 0.0004473 |  |  |  |  | openmp runtime linked | 52.8 |  |  |  |
| dot | ifx-expert-inputonly | ok | 21 | 0.008603 | 0.008936 | 0.0005278 |  |  |  |  | openmp runtime linked | 52.8 |  |  |  |
| saxpy | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | LLVM lane has no OpenMP |
| saxpy | blade-cpp | ok | 21 | 0.01191 | 0.01225 | 0.0006324 | ref |  |  | in-order | openmp (2 construct(s)); threaded maps, serial folds (BLADE_FP_REPRO) | 51.2 | 0.004754 [0.004309, 0.005221] | ref |  |
| saxpy | gfortran-natural | ok | 21 | 0.009638 | 0.01005 | 0.0005983 | 0.821 [0.782, 0.839] | loss | parity |  | openmp runtime linked | 51.3 | 0.004411 [0.004125, 0.004677] | 0.928 [0.817, 1.055] |  |
| saxpy | gfortran-expert | ok | 21 | 0.008629 | 0.008936 | 0.0003292 | 0.729 [0.702, 0.743] | loss | parity |  | openmp runtime linked | 51.3 | 0.003286 [0.002769, 0.003523] | 0.691 [0.568, 0.800] |  |
| saxpy | ifx-natural | ok | 21 | 0.00916 | 0.01014 | 0.0003338 | 0.827 [0.786, 0.838] | loss | parity |  | openmp runtime linked | 52.9 | 0.003911 [0.003616, 0.00411] | 0.823 [0.721, 0.924] |  |
| saxpy | ifx-expert | ok | 21 | 0.009045 | 0.009411 | 0.0003086 | 0.768 [0.736, 0.782] | loss | parity |  | openmp runtime linked | 52.9 | 0.003249 [0.002969, 0.003534] | 0.683 [0.606, 0.781] |  |
| saxpy | blade-llvm-inputonly | serial-only | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | LLVM lane has no OpenMP |
| saxpy | blade-cpp-inputonly | ok | 21 | 0.007158 | 0.007496 | 0.001011 |  |  |  | in-order | openmp (1 construct(s)); threaded maps, serial folds (BLADE_FP_REPRO) | 36.2 |  |  |  |
| saxpy | gfortran-natural-inputonly | ok | 21 | 0.005301 | 0.005641 | 0.0002674 |  |  |  |  | openmp runtime linked | 36.0 |  |  |  |
| saxpy | gfortran-expert-inputonly | ok | 21 | 0.005282 | 0.00565 | 0.0006887 |  |  |  |  | openmp runtime linked | 36.0 |  |  |  |
| saxpy | ifx-natural-inputonly | ok | 21 | 0.005796 | 0.006225 | 0.0005403 |  |  |  |  | openmp runtime linked | 37.6 |  |  |  |
| saxpy | ifx-expert-inputonly | ok | 21 | 0.005743 | 0.006162 | 0.000498 |  |  |  |  | openmp runtime linked | 37.6 |  |  |  |

## serial-fastmath-native

Sources: session-142614 -> all kernels

| kernel | arm | status | n | min (s) | median (s) | IQR (s) | ratio vs ref [95% CI] | verdict | hypothesis | fold form | parallelism | peak WS (MB) | incremental (s) [CI] | incremental ratio [CI] | notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| antisym | blade-llvm | ok | 11 | 0.0732 | 0.07376 | 0.0005587 | ref |  |  | reordered (7 reassoc flags) | serial | 17.4 |  |  |  |
| antisym | blade-cpp | ok | 11 | 0.1072 | 0.1087 | 0.001707 | 1.474 [1.463, 1.491] | win |  | reordered (7 SIMD_REDUCTION) | serial | 18.3 |  |  |  |
| antisym | gfortran-natural | ok | 11 | 0.1279 | 0.1299 | 0.003497 | 1.761 [1.749, 1.815] | win | WIN ~2x (half the cells, no diagonal) |  | serial | 23.4 |  |  |  |
| antisym | gfortran-expert | ok | 11 | 0.02981 | 0.03041 | 0.0008583 | 0.412 [0.408, 0.422] | loss | parity +-15% |  | serial | 32.7 |  |  |  |
| antisym | ifx-natural | ok | 11 | 0.08132 | 0.08427 | 0.002625 | 1.142 [1.113, 1.158] | win | WIN ~2x (half the cells, no diagonal) |  | serial | 23.0 |  |  |  |
| antisym | ifx-expert | ok | 11 | 0.02884 | 0.02925 | 0.0003386 | 0.397 [0.395, 0.403] | loss | parity +-15% |  | serial | 32.2 |  |  |  |
| comoment3 | blade-llvm | ok | 11 | 0.04048 | 0.04104 | 0.0005982 | ref |  |  | reordered (6 reassoc flags) | serial | 11.3 |  |  |  |
| comoment3 | blade-cpp | ok | 11 | 0.1069 | 0.1075 | 0.0003849 | 2.620 [2.607, 2.652] | suppressed |  | reordered (6 SIMD_REDUCTION) | serial | 12.2 |  |  |  |
| comoment3 | gfortran-natural | ok | 11 | 0.1442 | 0.1445 | 0.0003535 | 3.521 [3.505, 3.563] | suppressed | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | serial | 43.4 |  |  |  |
| comoment3 | gfortran-expert | ok | 11 | 0.01103 | 0.01131 | 0.0002293 | 0.276 [0.272, 0.279] | suppressed | parity to 1.3x loss |  | serial | 12.3 |  |  |  |
| comoment3 | ifx-natural | ok | 11 | 0.07878 | 0.07997 | 0.007481 | 1.949 [1.934, 2.138] | suppressed | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | serial | 43.0 |  |  |  |
| comoment3 | ifx-expert | ok | 11 | 0.01101 | 0.01126 | 0.0002701 | 0.274 [0.272, 0.280] | suppressed | parity to 1.3x loss |  | serial | 11.9 |  |  |  |
| gram | blade-llvm | ok | 11 | 0.1504 | 0.1506 | 0.0008909 | ref |  |  | reordered (8 reassoc flags) | serial | 26.6 |  |  |  |
| gram | blade-cpp | ok | 11 | 0.2437 | 0.2469 | 0.002782 | 1.640 [1.623, 1.648] | suppressed |  | reordered (5 SIMD_REDUCTION) | serial | 27.3 |  |  |  |
| gram | gfortran-natural | ok | 11 | 0.4432 | 0.4668 | 0.029 | 3.099 [3.021, 3.276] | suppressed | win 1.5-2x if matmul is a naive nest; parity/loss if a blocked runtime kernel |  | serial | 36.9 |  |  |  |
| gram | gfortran-expert | ok | 11 | 0.07499 | 0.07585 | 0.0003758 | 0.504 [0.499, 0.507] | suppressed | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | serial | 51.6 |  |  |  |
| gram | ifx-natural | ok | 11 | 0.1951 | 0.198 | 0.01743 | 1.315 [1.296, 1.419] | suppressed | win 1.5-2x if matmul is a naive nest; parity/loss if a blocked runtime kernel |  | serial | 55.9 |  |  |  |
| gram | ifx-expert | ok | 11 | 0.06896 | 0.06966 | 0.0008084 | 0.463 [0.458, 0.466] | suppressed | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | serial | 51.2 |  |  |  |
| fusedchain | blade-llvm | ok | 21 | 0.00426 | 0.004519 | 0.0003579 | ref |  |  | reordered (2 reassoc flags) | serial | 19.8 | 0.000844 [0.0006553, 0.001016] | ref |  |
| fusedchain | blade-cpp | ok | 21 | 0.0064 | 0.006576 | 0.0002441 | 1.455 [1.402, 1.526] | win |  | reordered (1 SIMD_REDUCTION) | serial | 20.6 | 0.0006663 [0.0006135, 0.0008247] | 0.789 [0.656, 1.139] |  |
| fusedchain | gfortran-natural | ok | 21 | 0.01481 | 0.01529 | 0.0003396 | 3.383 [3.259, 3.521] | win | WIN 1.5-2.5x (no temporaries) |  | serial | 66.1 | 0.009289 [0.009001, 0.009387] | 11.007 [9.083, 14.044] |  |
| fusedchain | gfortran-expert | ok | 21 | 0.006522 | 0.0067 | 0.0001448 | 1.483 [1.428, 1.560] | win | parity |  | serial | 20.3 | 0.0006904 [0.0006143, 0.0008363] | 0.818 [0.666, 1.170] |  |
| fusedchain | ifx-natural | ok | 21 | 0.00935 | 0.009583 | 0.0004148 | 2.121 [2.043, 2.214] | win | WIN 1.5-2.5x (no temporaries) |  | serial | 65.6 | 0.006722 [0.006615, 0.00692] | 7.965 [6.622, 10.263] |  |
| fusedchain | ifx-expert | ok | 21 | 0.003593 | 0.003672 | 9.64e-05 | 0.813 [0.786, 0.845] | loss | parity |  | serial | 19.8 | 0.0008207 [0.0007488, 0.000887] | 0.972 [0.797, 1.242] |  |
| fusedchain | blade-llvm-inputonly | ok | 21 | 0.003508 | 0.003675 | 0.0001367 |  |  |  | reordered (1 reassoc flags) | serial | 19.8 |  |  |  |
| fusedchain | blade-cpp-inputonly | ok | 21 | 0.005781 | 0.00591 | 0.0001563 |  |  |  | in-order | serial | 20.6 |  |  |  |
| fusedchain | gfortran-natural-inputonly | ok | 21 | 0.005775 | 0.005997 | 0.0002062 |  |  |  |  | serial | 20.3 |  |  |  |
| fusedchain | gfortran-expert-inputonly | ok | 21 | 0.005919 | 0.00601 | 0.0001547 |  |  |  |  | serial | 20.3 |  |  |  |
| fusedchain | ifx-natural-inputonly | ok | 21 | 0.00282 | 0.00286 | 7.79e-05 |  |  |  |  | serial | 19.8 |  |  |  |
| fusedchain | ifx-expert-inputonly | ok | 21 | 0.002787 | 0.002852 | 9.23e-05 |  |  |  |  | serial | 19.8 |  |  |  |
| onepass | blade-llvm | ok | 21 | 0.01115 | 0.01176 | 0.0007423 | ref |  |  | reordered (5 reassoc flags) | serial | 42.7 | 0.001999 [0.001689, 0.002457] | ref |  |
| onepass | blade-cpp | ok | 21 | 0.01636 | 0.01706 | 0.0008434 | 1.450 [1.408, 1.496] | suppressed |  | reordered (1 SIMD_REDUCTION) | serial | 43.4 | 0.002163 [0.00184, 0.002571] | 1.082 [0.841, 1.313] |  |
| onepass | gfortran-natural | ok | 21 | 0.01986 | 0.02058 | 0.0008113 | 1.750 [1.686, 1.798] | suppressed | WIN 2-3x (one pass vs four) |  | serial | 43.2 | 0.005687 [0.005014, 0.006067] | 2.844 [2.199, 3.437] |  |
| onepass | gfortran-expert | ok | 21 | 0.01646 | 0.01699 | 0.0009041 | 1.445 [1.392, 1.493] | suppressed | parity |  | serial | 43.2 | 0.001934 [0.001343, 0.002457] | 0.967 [0.669, 1.248] |  |
| onepass | ifx-natural | ok | 21 | 0.007902 | 0.008199 | 0.0004965 | 0.697 [0.677, 0.731] | suppressed | WIN 2-3x (one pass vs four) |  | serial | 42.7 | 0.0008068 [0.0003997, 0.001148] | 0.404 [0.187, 0.577] |  |
| onepass | ifx-expert | ok | 21 | 0.008098 | 0.008435 | 0.0006475 | 0.717 [0.696, 0.746] | suppressed | parity |  | serial | 42.7 | 0.0007231 [0.000542, 0.001031] | 0.362 [0.267, 0.505] |  |
| onepass | blade-llvm-inputonly | ok | 21 | 0.009316 | 0.009763 | 0.0004851 |  |  |  | reordered (1 reassoc flags) | serial | 42.7 |  |  |  |
| onepass | blade-cpp-inputonly | ok | 21 | 0.01443 | 0.0149 | 0.0004507 |  |  |  | in-order | serial | 43.5 |  |  |  |
| onepass | gfortran-natural-inputonly | ok | 21 | 0.01454 | 0.0149 | 0.000603 |  |  |  |  | serial | 43.2 |  |  |  |
| onepass | gfortran-expert-inputonly | ok | 21 | 0.0145 | 0.01506 | 0.001108 |  |  |  |  | serial | 43.2 |  |  |  |
| onepass | ifx-natural-inputonly | ok | 21 | 0.007126 | 0.007392 | 0.0005937 |  |  |  |  | serial | 42.7 |  |  |  |
| onepass | ifx-expert-inputonly | ok | 21 | 0.007136 | 0.007712 | 0.0003866 |  |  |  |  | serial | 42.7 |  |  |  |
| dot | blade-llvm | ok | 21 | 0.01264 | 0.01315 | 0.0003948 | ref |  |  | reordered (3 reassoc flags) | serial | 50.3 | 0.001819 [0.001407, 0.001994] | ref |  |
| dot | blade-cpp | ok | 21 | 0.01877 | 0.01937 | 0.0006723 | 1.472 [1.441, 1.512] | win |  | reordered (1 SIMD_REDUCTION) | serial | 51.1 | 0.001669 [0.001248, 0.001919] | 0.917 [0.663, 1.290] |  |
| dot | gfortran-natural | ok | 21 | 0.01792 | 0.01813 | 0.0005638 | 1.379 [1.364, 1.428] | win | parity |  | serial | 50.8 | 0.00125 [0.0009812, 0.001671] | 0.687 [0.517, 1.049] |  |
| dot | gfortran-expert | ok | 21 | 0.01784 | 0.01816 | 0.0005324 | 1.381 [1.368, 1.432] | win | parity |  | serial | 50.8 | 0.001346 [0.001075, 0.001738] | 0.740 [0.587, 1.108] |  |
| dot | ifx-natural | ok | 21 | 0.01208 | 0.01263 | 0.0005146 | 0.960 [0.936, 0.985] | detectable-loss | parity |  | serial | 50.4 | 0.003737 [0.00345, 0.004068] | 2.055 [1.824, 2.705] |  |
| dot | ifx-expert | ok | 21 | 0.008485 | 0.008914 | 0.0003659 | 0.678 [0.667, 0.700] | loss | parity |  | serial | 50.3 | 2.88e-05 [-0.000324, 0.0003421] | 0.016 [-0.202, 0.191] |  |
| dot | blade-llvm-inputonly | ok | 21 | 0.01095 | 0.01133 | 0.0005224 |  |  |  | reordered (2 reassoc flags) | serial | 50.3 |  |  |  |
| dot | blade-cpp-inputonly | ok | 21 | 0.01739 | 0.0177 | 0.0004538 |  |  |  | in-order | serial | 51.1 |  |  |  |
| dot | gfortran-natural-inputonly | ok | 21 | 0.01655 | 0.01688 | 0.000342 |  |  |  |  | serial | 50.8 |  |  |  |
| dot | gfortran-expert-inputonly | ok | 21 | 0.01649 | 0.01681 | 0.0003467 |  |  |  |  | serial | 50.8 |  |  |  |
| dot | ifx-natural-inputonly | ok | 21 | 0.008407 | 0.008895 | 0.0005934 |  |  |  |  | serial | 50.3 |  |  |  |
| dot | ifx-expert-inputonly | ok | 21 | 0.008417 | 0.008885 | 0.0005986 |  |  |  |  | serial | 50.3 |  |  |  |

## mt8-fastmath-native

Sources: session-144656 -> all kernels

| kernel | arm | status | n | min (s) | median (s) | IQR (s) | ratio vs ref [95% CI] | verdict | hypothesis | fold form | parallelism | peak WS (MB) | incremental (s) [CI] | incremental ratio [CI] | notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| antisym | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (7 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| antisym | blade-cpp | ok | 21 | 0.01947 | 0.02103 | 0.002408 | ref |  |  | reordered (6 SIMD_REDUCTION) | openmp (4 construct(s)) | 18.7 |  |  |  |
| antisym | gfortran-natural | ok | 21 | 0.02522 | 0.03717 | 0.003841 | 1.767 [1.660, 1.811] | suppressed | WIN ~2x (half the cells, no diagonal) |  | openmp runtime linked | 23.8 |  |  |  |
| antisym | gfortran-expert | ok | 21 | 0.009011 | 0.00974 | 0.000434 | 0.463 [0.433, 0.476] | suppressed | parity +-15% |  | openmp runtime linked | 33.1 |  |  |  |
| antisym | ifx-natural | ok | 21 | 0.01621 | 0.02024 | 0.003641 | 0.962 [0.874, 1.051] | suppressed | WIN ~2x (half the cells, no diagonal) |  | openmp runtime linked | 25.4 |  |  |  |
| antisym | ifx-expert | ok | 21 | 0.009227 | 0.01006 | 0.001021 | 0.478 [0.443, 0.493] | suppressed | parity +-15% |  | openmp runtime linked | 34.7 |  |  |  |
| comoment3 | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (6 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| comoment3 | blade-cpp | ok | 21 | 0.01999 | 0.02067 | 0.001849 | ref |  |  | reordered (4 SIMD_REDUCTION) | openmp (4 construct(s)) | 12.6 |  |  |  |
| comoment3 | gfortran-natural | ok | 21 | 0.02571 | 0.0385 | 0.004806 | 1.862 [1.746, 1.915] | suppressed | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | openmp runtime linked | 43.9 |  |  |  |
| comoment3 | gfortran-expert | ok | 21 | 0.004632 | 0.005136 | 0.0005295 | 0.248 [0.236, 0.261] | suppressed | parity to 1.3x loss |  | openmp runtime linked | 14.5 |  |  |  |
| comoment3 | ifx-natural | ok | 21 | 0.01641 | 0.02072 | 0.006106 | 1.002 [0.849, 1.133] | suppressed | WIN 4-6x (C(p+2,3) vs p^3 cells, minus checksum tax) |  | openmp runtime linked | 45.4 |  |  |  |
| comoment3 | ifx-expert | ok | 21 | 0.00384 | 0.004092 | 0.0001952 | 0.198 [0.186, 0.201] | suppressed | parity to 1.3x loss |  | openmp runtime linked | 15.8 |  |  |  |
| gram | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (8 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| gram | blade-cpp | ok | 7 | 0.04901 | 0.05676 | 0.009768 | ref |  |  | reordered (3 SIMD_REDUCTION) | openmp (4 construct(s)) | 27.8 |  |  |  |
| gram | gfortran-natural | ok | 7 | 0.6239 | 0.7068 | 0.03978 | 12.454 [10.354, 13.638] | suppressed | win 1.5-2x if matmul is a naive nest; parity/loss if a blocked runtime kernel |  | openmp runtime linked | 37.3 |  |  | matmul is unannotated: parallel preparation, serial kernel unless the runtime threads matmul (FORT records) |
| gram | gfortran-expert | ok | 7 | 0.01979 | 0.02097 | 0.001172 | 0.370 [0.325, 0.410] | suppressed | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | openmp runtime linked | 52.1 |  |  |  |
| gram | ifx-natural | ok | 7 | 0.2329 | 0.2445 | 0.02162 | 4.308 [3.640, 5.193] | suppressed | win 1.5-2x if matmul is a naive nest; parity/loss if a blocked runtime kernel |  | openmp runtime linked | 58.3 |  |  | matmul is unannotated: parallel preparation, serial kernel unless the runtime threads matmul (FORT records) |
| gram | ifx-expert | ok | 7 | 0.01832 | 0.01957 | 0.0007742 | 0.345 [0.293, 0.380] | suppressed | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | openmp runtime linked | 53.7 |  |  |  |
| fusedchain | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (2 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| fusedchain | blade-cpp | ok | 21 | 0.003368 | 0.004616 | 0.001359 | ref |  |  | in-order | openmp (2 construct(s)) | 21.0 | 0.000735 [-0.0003654, 0.0009839] | ref |  |
| fusedchain | gfortran-natural | ok | 21 | 0.009919 | 0.01166 | 0.001672 | 2.526 [2.392, 3.284] | suppressed | WIN 1.5-2.5x (no temporaries) |  | openmp runtime linked | 66.5 | 0.008541 [0.008062, 0.009182] | undefined: the reference's incremental interval includes 0 |  |
| fusedchain | gfortran-expert | ok | 21 | 0.003027 | 0.003534 | 0.0009255 | 0.766 [0.699, 1.055] | suppressed | parity |  | openmp runtime linked | 20.8 | 0.0006508 [0.0002513, 0.001168] | undefined: the reference's incremental interval includes 0 |  |
| fusedchain | ifx-natural | ok | 21 | 0.01038 | 0.01202 | 0.001787 | 2.605 [2.436, 3.391] | suppressed | WIN 1.5-2.5x (no temporaries) |  | openmp runtime linked | 68.2 | 0.008572 [0.00771, 0.009378] | undefined: the reference's incremental interval includes 0 |  |
| fusedchain | ifx-expert | ok | 21 | 0.003132 | 0.003536 | 0.0007682 | 0.766 [0.721, 0.962] | suppressed | parity |  | openmp runtime linked | 22.3 | 0.0002939 [-0.0001196, 0.0006475] | undefined: the reference's incremental interval includes 0 |  |
| fusedchain | blade-llvm-inputonly | serial-only | 0 | -- | -- | -- |  |  |  | reordered (1 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| fusedchain | blade-cpp-inputonly | ok | 21 | 0.00311 | 0.003881 | 0.000444 |  |  |  | in-order | openmp (1 construct(s)) | 21.0 |  |  |  |
| fusedchain | gfortran-natural-inputonly | ok | 21 | 0.002589 | 0.003117 | 0.0002694 |  |  |  |  | openmp runtime linked | 20.7 |  |  |  |
| fusedchain | gfortran-expert-inputonly | ok | 21 | 0.002583 | 0.002883 | 0.000602 |  |  |  |  | openmp runtime linked | 20.7 |  |  |  |
| fusedchain | ifx-natural-inputonly | ok | 21 | 0.003081 | 0.003451 | 0.0004121 |  |  |  |  | openmp runtime linked | 22.3 |  |  |  |
| fusedchain | ifx-expert-inputonly | ok | 21 | 0.002941 | 0.003242 | 0.0006278 |  |  |  |  | openmp runtime linked | 22.2 |  |  |  |
| onepass | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (5 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| onepass | blade-cpp | no-parallel-form | 0 | -- | -- | -- |  |  |  | reordered (1 SIMD_REDUCTION) |  |  |  |  | kernel.json no_parallel_form (spec: no licensed parallel spelling) |
| onepass | gfortran-natural | ok | 21 | 0.01248 | 0.01322 | 0.000593 |  |  | WIN 2-3x (one pass vs four) |  | openmp runtime linked | 43.6 |  |  |  |
| onepass | gfortran-expert | ok | 21 | 0.008004 | 0.008892 | 0.0006172 |  |  | parity |  | openmp runtime linked | 43.6 |  |  |  |
| onepass | ifx-natural | ok | 21 | 0.01261 | 0.0135 | 0.0005752 |  |  | WIN 2-3x (one pass vs four) |  | openmp runtime linked | 45.1 |  |  |  |
| onepass | ifx-expert | ok | 21 | 0.008351 | 0.009376 | 0.001022 |  |  | parity |  | openmp runtime linked | 45.1 |  |  |  |
| onepass | blade-llvm-inputonly | serial-only | 0 | -- | -- | -- |  |  |  | reordered (1 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| onepass | blade-cpp-inputonly | no-parallel-form | 0 | -- | -- | -- |  |  |  | in-order |  |  |  |  | kernel.json no_parallel_form (spec: no licensed parallel spelling) |
| onepass | gfortran-natural-inputonly | ok | 21 | 0.006178 | 0.006571 | 0.0006137 |  |  |  |  | openmp runtime linked | 43.6 |  |  |  |
| onepass | gfortran-expert-inputonly | ok | 21 | 0.00628 | 0.006768 | 0.0009926 |  |  |  |  | openmp runtime linked | 43.6 |  |  |  |
| onepass | ifx-natural-inputonly | ok | 21 | 0.006631 | 0.007307 | 0.0006084 |  |  |  |  | openmp runtime linked | 45.1 |  |  |  |
| onepass | ifx-expert-inputonly | ok | 21 | 0.006611 | 0.007 | 0.0009107 |  |  |  |  | openmp runtime linked | 45.1 |  |  |  |
| dot | blade-llvm | serial-only | 0 | -- | -- | -- |  |  |  | reordered (3 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| dot | blade-cpp | ok | 21 | 0.009769 | 0.01119 | 0.001658 | ref |  |  | in-order | openmp (2 construct(s)) | 51.5 | 0.001937 [0.0009847, 0.003106] | ref |  |
| dot | gfortran-natural | ok | 21 | 0.009362 | 0.01036 | 0.0006336 | 0.925 [0.855, 0.951] | suppressed | parity |  | openmp runtime linked | 51.3 | 0.002014 [0.001422, 0.002268] | 1.040 [0.571, 1.828] |  |
| dot | gfortran-expert | ok | 21 | 0.009394 | 0.01022 | 0.0008349 | 0.913 [0.867, 0.956] | suppressed | parity |  | openmp runtime linked | 51.2 | 0.002053 [0.001564, 0.002853] | 1.060 [0.692, 1.744] |  |
| dot | ifx-natural | ok | 21 | 0.009856 | 0.01053 | 0.0007002 | 0.941 [0.886, 0.982] | suppressed | parity |  | openmp runtime linked | 52.8 | 0.002348 [0.001666, 0.002687] | 1.212 [0.763, 2.068] |  |
| dot | ifx-expert | ok | 21 | 0.009897 | 0.01065 | 0.00062 | 0.952 [0.903, 0.989] | suppressed | parity |  | openmp runtime linked | 52.8 | 0.002489 [0.00175, 0.002856] | 1.285 [0.615, 2.561] |  |
| dot | blade-llvm-inputonly | serial-only | 0 | -- | -- | -- |  |  |  | reordered (2 reassoc flags) |  |  |  |  | LLVM lane has no OpenMP |
| dot | blade-cpp-inputonly | ok | 21 | 0.008082 | 0.009253 | 0.00174 |  |  |  | in-order | openmp (1 construct(s)) | 51.5 |  |  |  |
| dot | gfortran-natural-inputonly | ok | 21 | 0.007461 | 0.008341 | 0.0005937 |  |  |  |  | openmp runtime linked | 51.2 |  |  |  |
| dot | gfortran-expert-inputonly | ok | 21 | 0.007441 | 0.008166 | 0.001018 |  |  |  |  | openmp runtime linked | 51.3 |  |  |  |
| dot | ifx-natural-inputonly | ok | 21 | 0.007906 | 0.008184 | 0.0009095 |  |  |  |  | openmp runtime linked | 52.8 |  |  |  |
| dot | ifx-expert-inputonly | ok | 21 | 0.007926 | 0.008162 | 0.0008896 |  |  |  |  | openmp runtime linked | 52.8 |  |  |  |

## serial-licensed-blas

Sources: session-150343 -> all kernels

| kernel | arm | status | n | min (s) | median (s) | IQR (s) | ratio vs ref [95% CI] | verdict | hypothesis | fold form | parallelism | peak WS (MB) | notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| gram | blade-llvm | no-blas | 0 | -- | -- | -- |  |  |  | reordered (8 reassoc flags) |  |  | LLVM lane does not route to BLAS (the exe imports no libopenblas.dll) |
| gram | blade-cpp | ok | 11 | 0.07982 | 0.08071 | 0.0008952 | ref |  |  | reordered (4 SIMD_REDUCTION) | library threads (OPENBLAS_NUM_THREADS=1) | 49.0 |  |
| gram | gfortran-expert | ok | 11 | 0.06497 | 0.06571 | 0.000664 | 0.814 [0.805, 0.827] | loss | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | library threads (OPENBLAS_NUM_THREADS=1) | 34.8 |  |
| gram | ifx-expert | ok | 11 | 0.06214 | 0.06283 | 0.0006551 | 0.778 [0.772, 0.786] | loss | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | library threads (OPENBLAS_NUM_THREADS=1) | 34.6 |  |

## mt8-licensed-blas

Sources: session-150343 -> all kernels

| kernel | arm | status | n | min (s) | median (s) | IQR (s) | ratio vs ref [95% CI] | verdict | hypothesis | fold form | parallelism | peak WS (MB) | notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| gram | blade-llvm | no-blas | 0 | -- | -- | -- |  |  |  | reordered (8 reassoc flags) |  |  | LLVM lane does not route to BLAS (the exe imports no libopenblas.dll) |
| gram | blade-cpp | ok | 21 | 0.02382 | 0.02814 | 0.002078 | ref |  |  | reordered (2 SIMD_REDUCTION) | openmp (3 construct(s)) | 51.1 |  |
| gram | gfortran-expert | ok | 21 | 0.01498 | 0.02026 | 0.001776 | 0.720 [0.684, 0.743] | loss | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | library threads (OPENBLAS_NUM_THREADS=8) + openmp runtime | 36.6 |  |
| gram | ifx-expert | ok | 21 | 0.01681 | 0.0195 | 0.001385 | 0.693 [0.665, 0.728] | loss | LOSS 1.5-3x strict; narrows in fast; parity in blas |  | library threads (OPENBLAS_NUM_THREADS=8) + openmp runtime | 38.1 |  |

## Cells that are not plain ok-with-verdict

Statuses other than ok (fallback, serial-only, no-parallel-form, no-blas, unrun) and verdict `suppressed`. Twins (inputonly) are listed with their non-ok status only.

| regime | kernel | arm | status | verdict | session |
|---|---|---|---|---|---|
| serial-licensed-native | comoment3 | blade-cpp | ok | suppressed | session-042223 |
| serial-licensed-native | comoment3 | gfortran-natural | ok | suppressed | session-042223 |
| serial-licensed-native | comoment3 | gfortran-expert | ok | suppressed | session-042223 |
| serial-licensed-native | comoment3 | ifx-natural | ok | suppressed | session-042223 |
| serial-licensed-native | comoment3 | ifx-expert | ok | suppressed | session-042223 |
| serial-licensed-native | gram | blade-cpp | ok | suppressed | session-042223 |
| serial-licensed-native | gram | gfortran-natural | ok | suppressed | session-042223 |
| serial-licensed-native | gram | gfortran-expert | ok | suppressed | session-042223 |
| serial-licensed-native | gram | ifx-natural | ok | suppressed | session-042223 |
| serial-licensed-native | gram | ifx-expert | ok | suppressed | session-042223 |
| serial-licensed-native | pairkernel | blade-cpp | ok | suppressed | session-042223 |
| serial-licensed-native | pairkernel | gfortran-natural | ok | suppressed | session-042223 |
| serial-licensed-native | pairkernel | gfortran-expert | ok | suppressed | session-042223 |
| serial-licensed-native | pairkernel | ifx-natural | ok | suppressed | session-042223 |
| serial-licensed-native | pairkernel | ifx-expert | ok | suppressed | session-042223 |
| serial-licensed-native | recur | blade-cpp | ok | suppressed | session-042223 |
| serial-licensed-native | recur | gfortran-natural | ok | suppressed | session-042223 |
| serial-licensed-native | recur | gfortran-expert | ok | suppressed | session-042223 |
| serial-licensed-native | recur | ifx-natural | ok | suppressed | session-042223 |
| serial-licensed-native | recur | ifx-expert | ok | suppressed | session-042223 |
| serial-licensed-native | stencil | blade-cpp | ok | suppressed | session-042223 |
| serial-licensed-native | stencil | gfortran-natural | ok | suppressed | session-042223 |
| serial-licensed-native | stencil | gfortran-expert | ok | suppressed | session-042223 |
| serial-licensed-native | stencil | ifx-natural | ok | suppressed | session-042223 |
| serial-licensed-native | stencil | ifx-expert | ok | suppressed | session-042223 |
| serial-licensed-native | fusedchain | blade-cpp | ok | suppressed | session-090344 |
| serial-licensed-native | fusedchain | gfortran-natural | ok | suppressed | session-090344 |
| serial-licensed-native | fusedchain | gfortran-expert | ok | suppressed | session-090344 |
| serial-licensed-native | fusedchain | ifx-natural | ok | suppressed | session-090344 |
| serial-licensed-native | fusedchain | ifx-expert | ok | suppressed | session-090344 |
| serial-licensed-native | maskagg | blade-llvm | fallback |  | session-090344 |
| serial-licensed-native | maskagg | gfortran-natural | ok | suppressed | session-090344 |
| serial-licensed-native | maskagg | gfortran-expert | ok | suppressed | session-090344 |
| serial-licensed-native | maskagg | ifx-natural | ok | suppressed | session-090344 |
| serial-licensed-native | maskagg | ifx-expert | ok | suppressed | session-090344 |
| serial-licensed-native | dot | blade-cpp | ok | suppressed | session-090344 |
| serial-licensed-native | dot | gfortran-natural | ok | suppressed | session-090344 |
| serial-licensed-native | dot | gfortran-expert | ok | suppressed | session-090344 |
| serial-licensed-native | dot | ifx-natural | ok | suppressed | session-090344 |
| serial-licensed-native | dot | ifx-expert | ok | suppressed | session-090344 |
| serial-licensed-native | saxpy | blade-cpp | ok | suppressed | session-090344 |
| serial-licensed-native | saxpy | gfortran-natural | ok | suppressed | session-090344 |
| serial-licensed-native | saxpy | gfortran-expert | ok | suppressed | session-090344 |
| serial-licensed-native | saxpy | ifx-natural | ok | suppressed | session-090344 |
| serial-licensed-native | saxpy | ifx-expert | ok | suppressed | session-090344 |
| mt8-licensed-native | antisym | blade-llvm | serial-only |  | session-113651 |
| mt8-licensed-native | antisym | gfortran-natural | ok | suppressed | session-113651 |
| mt8-licensed-native | antisym | gfortran-expert | ok | suppressed | session-113651 |
| mt8-licensed-native | antisym | ifx-natural | ok | suppressed | session-113651 |
| mt8-licensed-native | antisym | ifx-expert | ok | suppressed | session-113651 |
| mt8-licensed-native | comoment3 | blade-llvm | serial-only |  | session-113651 |
| mt8-licensed-native | comoment3 | gfortran-natural | ok | suppressed | session-113651 |
| mt8-licensed-native | comoment3 | gfortran-expert | ok | suppressed | session-113651 |
| mt8-licensed-native | comoment3 | ifx-natural | ok | suppressed | session-113651 |
| mt8-licensed-native | comoment3 | ifx-expert | ok | suppressed | session-113651 |
| mt8-licensed-native | gram | blade-llvm | serial-only |  | session-113651 |
| mt8-licensed-native | gram | gfortran-natural | ok | suppressed | session-113651 |
| mt8-licensed-native | gram | gfortran-expert | ok | suppressed | session-113651 |
| mt8-licensed-native | gram | ifx-natural | ok | suppressed | session-113651 |
| mt8-licensed-native | gram | ifx-expert | ok | suppressed | session-113651 |
| mt8-licensed-native | pairkernel | blade-llvm | serial-only |  | session-113651 |
| mt8-licensed-native | pairkernel | gfortran-natural | ok | suppressed | session-113651 |
| mt8-licensed-native | pairkernel | gfortran-expert | ok | suppressed | session-113651 |
| mt8-licensed-native | pairkernel | ifx-natural | ok | suppressed | session-113651 |
| mt8-licensed-native | pairkernel | ifx-expert | ok | suppressed | session-113651 |
| mt8-licensed-native | saxpy | blade-llvm | serial-only |  | session-113651 |
| mt8-licensed-native | saxpy | gfortran-natural | ok | suppressed | session-113651 |
| mt8-licensed-native | saxpy | gfortran-expert | ok | suppressed | session-113651 |
| mt8-licensed-native | saxpy | ifx-natural | ok | suppressed | session-113651 |
| mt8-licensed-native | saxpy | ifx-expert | ok | suppressed | session-113651 |
| mt8-licensed-native | saxpy | blade-llvm-inputonly | serial-only |  | session-113651 |
| mt8-licensed-native | fusedchain | blade-llvm | serial-only |  | session-100329 |
| mt8-licensed-native | fusedchain | gfortran-natural | ok | suppressed | session-100329 |
| mt8-licensed-native | fusedchain | gfortran-expert | ok | suppressed | session-100329 |
| mt8-licensed-native | fusedchain | ifx-natural | ok | suppressed | session-100329 |
| mt8-licensed-native | fusedchain | ifx-expert | ok | suppressed | session-100329 |
| mt8-licensed-native | fusedchain | blade-llvm-inputonly | serial-only |  | session-100329 |
| mt8-licensed-native | onepass | blade-llvm | serial-only |  | session-100329 |
| mt8-licensed-native | onepass | blade-cpp | no-parallel-form |  | session-100329 |
| mt8-licensed-native | onepass | blade-llvm-inputonly | serial-only |  | session-100329 |
| mt8-licensed-native | onepass | blade-cpp-inputonly | no-parallel-form |  | session-100329 |
| mt8-licensed-native | recur | blade-llvm | serial-only |  | session-100329 |
| mt8-licensed-native | recur | blade-cpp | no-parallel-form |  | session-100329 |
| mt8-licensed-native | stencil | blade-llvm | serial-only |  | session-100329 |
| mt8-licensed-native | stencil | gfortran-natural | ok | suppressed | session-100329 |
| mt8-licensed-native | stencil | gfortran-expert | ok | suppressed | session-100329 |
| mt8-licensed-native | stencil | ifx-natural | ok | suppressed | session-100329 |
| mt8-licensed-native | stencil | ifx-expert | ok | suppressed | session-100329 |
| mt8-licensed-native | stencil | blade-llvm-inputonly | serial-only |  | session-100329 |
| mt8-licensed-native | dot | blade-llvm | serial-only |  | session-100329 |
| mt8-licensed-native | dot | gfortran-natural | ok | suppressed | session-100329 |
| mt8-licensed-native | dot | gfortran-expert | ok | suppressed | session-100329 |
| mt8-licensed-native | dot | ifx-natural | ok | suppressed | session-100329 |
| mt8-licensed-native | dot | ifx-expert | ok | suppressed | session-100329 |
| mt8-licensed-native | dot | blade-llvm-inputonly | serial-only |  | session-100329 |
| serial-repro-native | antisym | blade-cpp | ok | suppressed | session-122606 |
| serial-repro-native | antisym | gfortran-natural | ok | suppressed | session-122606 |
| serial-repro-native | antisym | gfortran-expert | ok | suppressed | session-122606 |
| serial-repro-native | antisym | ifx-natural | ok | suppressed | session-122606 |
| serial-repro-native | antisym | ifx-expert | ok | suppressed | session-122606 |
| serial-repro-native | comoment3 | blade-cpp | ok | suppressed | session-122606 |
| serial-repro-native | comoment3 | gfortran-natural | ok | suppressed | session-122606 |
| serial-repro-native | comoment3 | gfortran-expert | ok | suppressed | session-122606 |
| serial-repro-native | comoment3 | ifx-natural | ok | suppressed | session-122606 |
| serial-repro-native | comoment3 | ifx-expert | ok | suppressed | session-122606 |
| serial-repro-native | gram | blade-cpp | ok | suppressed | session-122606 |
| serial-repro-native | gram | gfortran-natural | ok | suppressed | session-122606 |
| serial-repro-native | gram | gfortran-expert | ok | suppressed | session-122606 |
| serial-repro-native | gram | ifx-natural | ok | suppressed | session-122606 |
| serial-repro-native | gram | ifx-expert | ok | suppressed | session-122606 |
| serial-repro-native | pairkernel | blade-cpp | ok | suppressed | session-122606 |
| serial-repro-native | pairkernel | gfortran-natural | ok | suppressed | session-122606 |
| serial-repro-native | pairkernel | gfortran-expert | ok | suppressed | session-122606 |
| serial-repro-native | pairkernel | ifx-natural | ok | suppressed | session-122606 |
| serial-repro-native | pairkernel | ifx-expert | ok | suppressed | session-122606 |
| serial-repro-native | recur | blade-cpp | ok | suppressed | session-122606 |
| serial-repro-native | recur | gfortran-natural | ok | suppressed | session-122606 |
| serial-repro-native | recur | gfortran-expert | ok | suppressed | session-122606 |
| serial-repro-native | recur | ifx-natural | ok | suppressed | session-122606 |
| serial-repro-native | recur | ifx-expert | ok | suppressed | session-122606 |
| serial-repro-native | stencil | blade-cpp | ok | suppressed | session-122606 |
| serial-repro-native | stencil | gfortran-natural | ok | suppressed | session-122606 |
| serial-repro-native | stencil | gfortran-expert | ok | suppressed | session-122606 |
| serial-repro-native | stencil | ifx-natural | ok | suppressed | session-122606 |
| serial-repro-native | stencil | ifx-expert | ok | suppressed | session-122606 |
| serial-repro-native | fusedchain | blade-cpp | ok | suppressed | session-130438 |
| serial-repro-native | fusedchain | gfortran-natural | ok | suppressed | session-130438 |
| serial-repro-native | fusedchain | gfortran-expert | ok | suppressed | session-130438 |
| serial-repro-native | fusedchain | ifx-natural | ok | suppressed | session-130438 |
| serial-repro-native | fusedchain | ifx-expert | ok | suppressed | session-130438 |
| serial-repro-native | maskagg | blade-llvm | fallback |  | session-130438 |
| serial-repro-native | onepass | blade-cpp | ok | suppressed | session-130438 |
| serial-repro-native | onepass | gfortran-natural | ok | suppressed | session-130438 |
| serial-repro-native | onepass | gfortran-expert | ok | suppressed | session-130438 |
| serial-repro-native | onepass | ifx-natural | ok | suppressed | session-130438 |
| serial-repro-native | onepass | ifx-expert | ok | suppressed | session-130438 |
| mt8-repro-native | antisym | blade-llvm | serial-only |  | session-140038 |
| mt8-repro-native | antisym | gfortran-natural | ok | suppressed | session-140038 |
| mt8-repro-native | antisym | gfortran-expert | ok | suppressed | session-140038 |
| mt8-repro-native | antisym | ifx-natural | ok | suppressed | session-140038 |
| mt8-repro-native | antisym | ifx-expert | ok | suppressed | session-140038 |
| mt8-repro-native | comoment3 | blade-llvm | serial-only |  | session-140038 |
| mt8-repro-native | comoment3 | gfortran-natural | ok | suppressed | session-140038 |
| mt8-repro-native | comoment3 | gfortran-expert | ok | suppressed | session-140038 |
| mt8-repro-native | comoment3 | ifx-natural | ok | suppressed | session-140038 |
| mt8-repro-native | comoment3 | ifx-expert | ok | suppressed | session-140038 |
| mt8-repro-native | gram | blade-llvm | serial-only |  | session-140038 |
| mt8-repro-native | gram | gfortran-natural | ok | suppressed | session-140038 |
| mt8-repro-native | gram | gfortran-expert | ok | suppressed | session-140038 |
| mt8-repro-native | gram | ifx-natural | ok | suppressed | session-140038 |
| mt8-repro-native | gram | ifx-expert | ok | suppressed | session-140038 |
| mt8-repro-native | pairkernel | blade-llvm | serial-only |  | session-140038 |
| mt8-repro-native | pairkernel | gfortran-natural | ok | suppressed | session-140038 |
| mt8-repro-native | pairkernel | gfortran-expert | ok | suppressed | session-140038 |
| mt8-repro-native | pairkernel | ifx-natural | ok | suppressed | session-140038 |
| mt8-repro-native | pairkernel | ifx-expert | ok | suppressed | session-140038 |
| mt8-repro-native | fusedchain | blade-llvm | serial-only |  | session-140038 |
| mt8-repro-native | fusedchain | blade-llvm-inputonly | serial-only |  | session-140038 |
| mt8-repro-native | onepass | blade-llvm | serial-only |  | session-140038 |
| mt8-repro-native | onepass | blade-cpp | no-parallel-form |  | session-140038 |
| mt8-repro-native | onepass | blade-llvm-inputonly | serial-only |  | session-140038 |
| mt8-repro-native | onepass | blade-cpp-inputonly | no-parallel-form |  | session-140038 |
| mt8-repro-native | recur | blade-llvm | serial-only |  | session-140038 |
| mt8-repro-native | recur | blade-cpp | no-parallel-form |  | session-140038 |
| mt8-repro-native | stencil | blade-llvm | serial-only |  | session-140038 |
| mt8-repro-native | stencil | gfortran-natural | ok | suppressed | session-140038 |
| mt8-repro-native | stencil | gfortran-expert | ok | suppressed | session-140038 |
| mt8-repro-native | stencil | ifx-natural | ok | suppressed | session-140038 |
| mt8-repro-native | stencil | ifx-expert | ok | suppressed | session-140038 |
| mt8-repro-native | stencil | blade-llvm-inputonly | serial-only |  | session-140038 |
| mt8-repro-native | dot | blade-llvm | serial-only |  | session-140038 |
| mt8-repro-native | dot | gfortran-natural | ok | suppressed | session-140038 |
| mt8-repro-native | dot | gfortran-expert | ok | suppressed | session-140038 |
| mt8-repro-native | dot | ifx-natural | ok | suppressed | session-140038 |
| mt8-repro-native | dot | ifx-expert | ok | suppressed | session-140038 |
| mt8-repro-native | dot | blade-llvm-inputonly | serial-only |  | session-140038 |
| mt8-repro-native | saxpy | blade-llvm | serial-only |  | session-140038 |
| mt8-repro-native | saxpy | blade-llvm-inputonly | serial-only |  | session-140038 |
| serial-fastmath-native | comoment3 | blade-cpp | ok | suppressed | session-142614 |
| serial-fastmath-native | comoment3 | gfortran-natural | ok | suppressed | session-142614 |
| serial-fastmath-native | comoment3 | gfortran-expert | ok | suppressed | session-142614 |
| serial-fastmath-native | comoment3 | ifx-natural | ok | suppressed | session-142614 |
| serial-fastmath-native | comoment3 | ifx-expert | ok | suppressed | session-142614 |
| serial-fastmath-native | gram | blade-cpp | ok | suppressed | session-142614 |
| serial-fastmath-native | gram | gfortran-natural | ok | suppressed | session-142614 |
| serial-fastmath-native | gram | gfortran-expert | ok | suppressed | session-142614 |
| serial-fastmath-native | gram | ifx-natural | ok | suppressed | session-142614 |
| serial-fastmath-native | gram | ifx-expert | ok | suppressed | session-142614 |
| serial-fastmath-native | onepass | blade-cpp | ok | suppressed | session-142614 |
| serial-fastmath-native | onepass | gfortran-natural | ok | suppressed | session-142614 |
| serial-fastmath-native | onepass | gfortran-expert | ok | suppressed | session-142614 |
| serial-fastmath-native | onepass | ifx-natural | ok | suppressed | session-142614 |
| serial-fastmath-native | onepass | ifx-expert | ok | suppressed | session-142614 |
| mt8-fastmath-native | antisym | blade-llvm | serial-only |  | session-144656 |
| mt8-fastmath-native | antisym | gfortran-natural | ok | suppressed | session-144656 |
| mt8-fastmath-native | antisym | gfortran-expert | ok | suppressed | session-144656 |
| mt8-fastmath-native | antisym | ifx-natural | ok | suppressed | session-144656 |
| mt8-fastmath-native | antisym | ifx-expert | ok | suppressed | session-144656 |
| mt8-fastmath-native | comoment3 | blade-llvm | serial-only |  | session-144656 |
| mt8-fastmath-native | comoment3 | gfortran-natural | ok | suppressed | session-144656 |
| mt8-fastmath-native | comoment3 | gfortran-expert | ok | suppressed | session-144656 |
| mt8-fastmath-native | comoment3 | ifx-natural | ok | suppressed | session-144656 |
| mt8-fastmath-native | comoment3 | ifx-expert | ok | suppressed | session-144656 |
| mt8-fastmath-native | gram | blade-llvm | serial-only |  | session-144656 |
| mt8-fastmath-native | gram | gfortran-natural | ok | suppressed | session-144656 |
| mt8-fastmath-native | gram | gfortran-expert | ok | suppressed | session-144656 |
| mt8-fastmath-native | gram | ifx-natural | ok | suppressed | session-144656 |
| mt8-fastmath-native | gram | ifx-expert | ok | suppressed | session-144656 |
| mt8-fastmath-native | fusedchain | blade-llvm | serial-only |  | session-144656 |
| mt8-fastmath-native | fusedchain | gfortran-natural | ok | suppressed | session-144656 |
| mt8-fastmath-native | fusedchain | gfortran-expert | ok | suppressed | session-144656 |
| mt8-fastmath-native | fusedchain | ifx-natural | ok | suppressed | session-144656 |
| mt8-fastmath-native | fusedchain | ifx-expert | ok | suppressed | session-144656 |
| mt8-fastmath-native | fusedchain | blade-llvm-inputonly | serial-only |  | session-144656 |
| mt8-fastmath-native | onepass | blade-llvm | serial-only |  | session-144656 |
| mt8-fastmath-native | onepass | blade-cpp | no-parallel-form |  | session-144656 |
| mt8-fastmath-native | onepass | blade-llvm-inputonly | serial-only |  | session-144656 |
| mt8-fastmath-native | onepass | blade-cpp-inputonly | no-parallel-form |  | session-144656 |
| mt8-fastmath-native | dot | blade-llvm | serial-only |  | session-144656 |
| mt8-fastmath-native | dot | gfortran-natural | ok | suppressed | session-144656 |
| mt8-fastmath-native | dot | gfortran-expert | ok | suppressed | session-144656 |
| mt8-fastmath-native | dot | ifx-natural | ok | suppressed | session-144656 |
| mt8-fastmath-native | dot | ifx-expert | ok | suppressed | session-144656 |
| mt8-fastmath-native | dot | blade-llvm-inputonly | serial-only |  | session-144656 |
| serial-licensed-blas | gram | blade-llvm | no-blas |  | session-150343 |
| mt8-licensed-blas | gram | blade-llvm | no-blas |  | session-150343 |

## Control drift per session

| session | regime | wall (s) | blocks | suppressed | compute median ms | compute CV | compute range ms | memory median ms | memory CV | memory range ms | excursions dropped |
|---|---|---|---|---|---|---|---|---|---|---|---|
| session-042223 | serial-licensed 1a | 1976 | 6 | 5 | 13.42 | 9.0% | 12.51-17.14 | 5.50 | 9.7% | 5.12-7.45 | 0 |
| session-090344 | serial-licensed 1b | 3282 | 5 | 4 | 13.05 | 9.4% | 12.43-20.67 | 5.40 | 8.9% | 5.13-8.28 | 0 |
| session-121713 | serial-licensed antisym | 229 | 1 | 0 | 12.84 | 1.8% | 12.66-13.44 | 5.45 | 2.7% | 5.31-5.85 | 0 |
| session-100329 | mt8-licensed (aborted at saxpy) | 4114 | 10 | 8 | 13.43 | 9.5% | 12.45-18.05 | 5.47 | 9.0% | 5.09-7.87 | 0 |
| session-113651 | mt8-licensed group-A+saxpy | 2118 | 5 | 5 | 12.98 | 8.9% | 12.56-17.59 | 5.42 | 9.9% | 5.12-9.02 | 0 |
| session-122606 | serial-repro 3a | 2009 | 6 | 6 | 12.98 | 12.1% | 12.57-20.27 | 5.46 | 9.6% | 5.15-7.42 | 0 |
| session-130438 | serial-repro 3b | 3292 | 5 | 2 | 12.95 | 8.7% | 12.44-20.83 | 5.44 | 7.7% | 5.13-7.27 | 0 |
| session-140038 | mt8-repro | 1474 | 10 | 6 | 12.73 | 1.7% | 12.51-13.63 | 5.28 | 4.7% | 5.05-7.41 | 0 |
| session-142614 | serial-fastmath | 845 | 6 | 3 | 12.81 | 8.1% | 12.55-20.19 | 5.29 | 3.6% | 5.13-6.07 | 0 |
| session-144122 | mt8-fastmath (ABORTED, control excursion) | 182 | 2 | 1 | 12.96 | 9.1% | 12.59-18.93 | 5.38 | 73.0% | 5.15-29.74 | 1 |
| session-144656 | mt8-fastmath re-run | 943 | 6 | 6 | 13.01 | 4.8% | 12.46-16.93 | 5.40 | 7.1% | 5.08-7.02 | 0 |
| session-150343 | gram blas serial+mt8 | 124 | 2 | 0 | 12.91 | 5.9% | 12.51-16.97 | 5.37 | 5.1% | 5.16-6.15 | 0 |

## Merged gate run (2026-10-18, 18:24-20:13)

- Commit: worktree-bench-fortran 957c1e0e (phase-2 branches D, E, F, G, H merged). Binary built by the coordinator, not by the runner.
- Blade.dll SHA-256: 79AF7EDCE32999DBDA279BD11D7BF254EC194711BD19F4945677270AF64219E7 (verified before the run, built 18:23:30). Host: AC, no other compute processes, ucrt64 g++ 16.2.0 first on PATH, uncapped (Silent, max state 100%).
- Consoles: `bench/results/gates/merged-<gate>.txt`, progress and per-step wall times in `bench/results/gates/progress.txt`. Each gate was run once; no gate failed, so nothing was rerun.

| gate | TOTAL line (verbatim) | wall | skip explanation (from the output) |
|---|---|---|---|
| `blade test` | `TOTAL: 6686 passed, 0 failed` | 838 s | no skipped suffix |
| `blade test interp` | `Interp Diff: 2882 passed, 0 failed, 2 skip-unsupported, 0 skipped` | 2802 s | the 2 are `Fill Random Symmetric` / `Fill Random Antisymmetric`: `SKIP-UNSUPPORTED: fill_random(mod) (C rand()%mod is nondeterministic)` |
| `blade test opt-diff` | `Opt Diff: 1969 passed (1831 optimizer-inert), 0 failed, 915 skipped, 103s` | 104 s | skips are not itemized in the output; per `tests/OptDiff.fs:160` they are reject probes / programs both the OFF and ON compiles refuse alike (nothing to compare) |
| `blade test llvm` | `TOTAL: 700 passed, 0 failed, 677 skipped` | 952 s | all 677 are `LLVM Differential` programs the LLVM lane refuses (`[SKIP]: <name> -- llvm lane refused: <reason>`, e.g. Int32 parameters, String operands, IRStructLit in value position); the new `LLVM Runtime Shim` (4/4), `LLVM Tiles Raw-Bit` (8/8), `LLVM Blocked Simplex` (15/15) blocks pass |
| `blade test docs` | `Docs (executable code blocks): 131 passed, 0 failed, 78 skipped` | 30 s | 78 `sketch` blocks (by design); 30 checked, 90 run, 10 rejects; link check over 59 pages passed |

### Harness evidence on the same binary
- `run.py smoke --regimes all` (every kernel including the new `band`): `exit 0: 0 failure(s), 0 flag(s)`, 1386 s; console `bench/results/gates/merged-smoke.txt`; session `bench/.build/sessions/2026-10-18-LAPTOP-350H6LDB/session-194325-smoke`. Only the expected statuses (maskagg blade-llvm fallback, blade-llvm serial-only in mt8, no-parallel-form per kernel.json incl. `band` blade-cpp in mt8, gram blade-llvm no-blas); no failure, DQ or unbuilt cell.
- `run.py check <k> --size full --regimes serial-licensed-native,serial-repro-native,mt8-licensed-native`, all `exit 0: 0 failure(s), 0 flag(s)` (50-58 s each): gram, antisym, comoment3, pairkernel, recur, band. Consoles `bench/results/gates/merged-check-<k>.txt`. Every arm validated against the full-size pins. These are single runs per arm (indicative, not measurements). Blade medians in ms (serial-licensed llvm / cpp; serial-repro llvm / cpp; mt8-licensed cpp) against the earlier bench sessions:
  - gram 55.6 / 58.5; 58.9 / 60.3; 15.7 (before: 156.7 / 275.2; 1219 / 954; 43.8). Expert Fortran serial 78.9 / 70.1 ms.
  - antisym 72.5 / 28.4; 507 / 29.6; 9.0 (before: 74.0 / 107.9; 508 / 411; 22.6). Expert 31.1 / 29.2 ms.
  - comoment3 27.5 / 9.6; 156.6 / 15.6; 4.1 (before: 41.5 / 109.7; 156.8 / 180.6; 20.6). Expert 12.0 / 11.4 ms.
  - pairkernel 29.2 / 30.7; 40.9 / 37.3; 7.5 (before: 42.6 / 77.3; 42.8 / 77.6; 16.1). Expert 32.7 / 30.4 ms.
  - recur 19.7 / 20.7; 23.5 / 24.0; blade-cpp no-parallel-form (before: 116.9 / 134.1; 111 / 123.5). Expert 9.1 / 9.8 ms.
  - band (new) 3.3 / 9.3; 9.25 / 9.30; blade-cpp mt8 no-parallel-form. Expert 0.81 / 1.07 ms (natural 207 / 350 ms).

## Re-measure on Blade.dll 79AF7EDC (commit 957c1e0e; tree tip 05f95277), 2026-10-18 20:15-21:52

Label: **uncapped: max processor state 100% (Silent scheme); verdicts suppressed by drift reported as indicative.** Kernels gram, antisym, comoment3, pairkernel, recur, band; every regime each kernel.json declares; full size; README minimum rounds; interleaved randomized rounds; both drift controls; cooldowns 0.5 s/run, 3 s/kernel, 60 s/session; excursion rule: isolated excursion dropped and recorded, abort on a second one or a control excursion.
Convention (RESULTS.md): ratio = expert median / Blade median (the harness `ratio` of the expert arm vs the Blade reference); **< 1 = Fortran faster**. Reference: blade-llvm in serial regimes, blade-cpp in mt8 and blas regimes. `*` = verdict suppressed by drift (indicative); `-` = no Blade arm in that regime (mt8 no-parallel-form) or kernel not declared there.

Sessions: serial-licensed 201558; mt8-licensed 202815; serial-repro 204141; mt8-repro 205306; serial-fastmath 210636 (band there was disturbed by the sibling session, so band comes from the clean re-run 214954); mt8-fastmath 213323 (earlier attempts 211435 and 211831 aborted: second excursion / control excursion, kept as aborted records); blas 214522.


### serial-licensed-native

| kernel | Blade ref | Blade median (ms) | other Blade lane (ms) | gf-expert (ms) | ratio gf-expert/Blade [95% CI] | ifx-expert (ms) | ratio ifx-expert/Blade [95% CI] | natural gf / ifx ratio | block |
|---|---|---|---|---|---|---|---|---|---|
| gram | blade-llvm | 56.1 | cpp 57.96 | 76.75 | 1.368 [1.355, 1.380] | 69.36 | 1.236 [1.227, 1.243] | 9.896 / 10.172 | 201558 n=11 |
| antisym | blade-llvm | 71.44 | cpp 27.93 | 30.28 | 0.424 [0.420, 0.427] | 28.99 | 0.406 [0.400, 0.411] | 10.733 / 4.607 | 201558 n=11 |
| comoment3 | blade-llvm | 25.04 | cpp 9.54 | 11.26 | 0.450 [0.445, 0.454] * | 11.35 | 0.453 [0.451, 0.457] * | 24.052 / 28.773 | 201558 n=21, suppressed |
| pairkernel | blade-llvm | 28.94 | cpp 29.14 | 32.44 | 1.121 [1.118, 1.125] | 30.18 | 1.043 [1.036, 1.047] | 2.082 / 2.230 | 201558 n=21 |
| recur | blade-llvm | 20.43 | cpp 22.49 | 10.07 | 0.493 [0.457, 0.523] * | 9.995 | 0.489 [0.466, 0.500] * | 0.929 / 0.770 | 201558 n=21, suppressed |
| band | blade-llvm | 3.318 | cpp 9.276 | 0.8135 | 0.245 [0.245, 0.246] | 1.042 | 0.314 [0.314, 0.314] | 62.375 / 105.554 | 201558 n=21 |

### mt8-licensed-native

| kernel | Blade ref | Blade median (ms) | other Blade lane (ms) | gf-expert (ms) | ratio gf-expert/Blade [95% CI] | ifx-expert (ms) | ratio ifx-expert/Blade [95% CI] | natural gf / ifx ratio | block |
|---|---|---|---|---|---|---|---|---|---|
| gram | blade-cpp | 15.58 | llvm serial-only | 19.87 | 1.276 [1.221, 1.322] | 19.06 | 1.223 [1.176, 1.258] | 37.560 / 39.420 | 202815 n=21 |
| antisym | blade-cpp | 8.79 | llvm serial-only | 9.5 | 1.081 [1.054, 1.114] * | 9.82 | 1.117 [1.080, 1.150] * | 12.788 / 8.206 | 202815 n=21, suppressed |
| comoment3 | blade-cpp | 3.311 | llvm serial-only | 4.839 | 1.462 [1.434, 1.502] | 4.03 | 1.217 [1.184, 1.271] | 28.356 / 35.423 | 202815 n=21 |
| pairkernel | blade-cpp | 7.67 | llvm serial-only | 6.27 | 0.817 [0.772, 0.840] * | 6.03 | 0.786 [0.744, 0.804] * | 1.980 / 2.187 | 202815 n=21, suppressed |
| recur | none | - | blade-cpp no-parallel-form; blade-llvm serial-only | 6.815 | - | 7.486 | - | - / - | 202815 n=21 |
| band | none | - | blade-cpp no-parallel-form; blade-llvm serial-only | 0.9909 | - | 1.705 | - | - / - | 202815 n=21, suppressed |

### serial-repro-native

| kernel | Blade ref | Blade median (ms) | other Blade lane (ms) | gf-expert (ms) | ratio gf-expert/Blade [95% CI] | ifx-expert (ms) | ratio ifx-expert/Blade [95% CI] | natural gf / ifx ratio | block |
|---|---|---|---|---|---|---|---|---|---|
| gram | blade-llvm | 59.73 | cpp 61.34 | 77.07 | 1.290 [1.230, 1.321] | 70.12 | 1.174 [1.119, 1.186] | 9.935 / 9.515 | 204141 n=11 |
| antisym | blade-llvm | 505.9 | cpp 29.78 | 30.83 | 0.061 [0.061, 0.061] | 29.55 | 0.058 [0.058, 0.059] | 1.521 / 0.654 | 204141 n=11 |
| comoment3 | blade-llvm | 156.7 | cpp 15.88 | 11.42 | 0.073 [0.070, 0.074] | 11.5 | 0.073 [0.071, 0.074] | 3.837 / 4.593 | 204141 n=11 |
| pairkernel | blade-llvm | 40.82 | cpp 37.85 | 34.32 | 0.841 [0.836, 0.852] | 31.91 | 0.782 [0.777, 0.790] | 1.489 / 1.565 | 204141 n=21 |
| recur | blade-llvm | 23.77 | cpp 23.85 | 12.95 | 0.545 [0.538, 0.554] | 13.34 | 0.561 [0.552, 0.571] | 0.748 / 0.626 | 204141 n=21 |
| band | blade-llvm | 9.277 | cpp 9.289 | 3.111 | 0.335 [0.334, 0.336] | 3.718 | 0.401 [0.399, 0.402] | 22.304 / 37.841 | 204141 n=21 |

### mt8-repro-native

| kernel | Blade ref | Blade median (ms) | other Blade lane (ms) | gf-expert (ms) | ratio gf-expert/Blade [95% CI] | ifx-expert (ms) | ratio ifx-expert/Blade [95% CI] | natural gf / ifx ratio | block |
|---|---|---|---|---|---|---|---|---|---|
| gram | blade-cpp | 19.14 | llvm serial-only | 20.66 | 1.080 [1.048, 1.124] * | 19.81 | 1.035 [1.002, 1.075] * | 28.891 / 32.206 | 205306 n=21, suppressed |
| antisym | blade-cpp | 10.88 | llvm serial-only | 10.1 | 0.929 [0.878, 0.983] * | 10.11 | 0.929 [0.903, 0.989] * | 10.400 / 6.644 | 205306 n=21, suppressed |
| comoment3 | blade-cpp | 9.91 | llvm serial-only | 4.861 | 0.490 [0.466, 0.521] * | 4.101 | 0.414 [0.391, 0.434] * | 9.669 / 12.172 | 205306 n=21, suppressed |
| pairkernel | blade-cpp | 17.34 | llvm serial-only | 6.217 | 0.359 [0.351, 0.378] * | 6.472 | 0.373 [0.354, 0.380] * | 0.897 / 0.978 | 205306 n=21, suppressed |
| recur | none | - | blade-cpp no-parallel-form; blade-llvm serial-only | 7.407 | - | 7.795 | - | - / - | 205306 n=21, suppressed |
| band | none | - | blade-cpp no-parallel-form; blade-llvm serial-only | 1.33 | - | 2.344 | - | - / - | 205306 n=21, suppressed |

### serial-fastmath-native

| kernel | Blade ref | Blade median (ms) | other Blade lane (ms) | gf-expert (ms) | ratio gf-expert/Blade [95% CI] | ifx-expert (ms) | ratio ifx-expert/Blade [95% CI] | natural gf / ifx ratio | block |
|---|---|---|---|---|---|---|---|---|---|
| gram | blade-llvm | 56.63 | cpp 59.56 | 76.7 | 1.354 [1.341, 1.377] * | 69.96 | 1.235 [1.218, 1.261] * | 9.725 / 3.529 | 210636 n=11, suppressed |
| antisym | blade-llvm | 81.31 | cpp 29.48 | 32.75 | 0.403 [0.385, 0.448] * | 31.77 | 0.391 [0.361, 0.465] * | 1.738 / 1.122 | 210636 n=11, suppressed |
| comoment3 | blade-llvm | 25.04 | cpp 9.595 | 11.21 | 0.447 [0.444, 0.456] | 11.24 | 0.449 [0.443, 0.456] | 5.773 / 3.473 | 210636 n=21 |
| pairkernel | - | - | - | - | - | - | - | - | not declared in this regime |
| recur | - | - | - | - | - | - | - | - | not declared in this regime |
| band | blade-llvm | 3.312 | cpp 9.277 | 1.699 | 0.513 [0.469, 0.517] | 1.322 | 0.399 [0.398, 0.401] | 91.102 / 100.087 | 214954 n=21 |

### mt8-fastmath-native

| kernel | Blade ref | Blade median (ms) | other Blade lane (ms) | gf-expert (ms) | ratio gf-expert/Blade [95% CI] | ifx-expert (ms) | ratio ifx-expert/Blade [95% CI] | natural gf / ifx ratio | block |
|---|---|---|---|---|---|---|---|---|---|
| gram | blade-cpp | 17.01 | llvm serial-only | 20.96 | 1.232 [1.173, 1.282] * | 19.83 | 1.166 [1.112, 1.202] * | 38.433 / 13.835 | 213323 n=21, suppressed |
| antisym | blade-cpp | 9.009 | llvm serial-only | 10.51 | 1.167 [1.109, 1.227] * | 10.36 | 1.150 [1.106, 1.211] * | 4.270 / 2.698 | 213323 n=21, suppressed |
| comoment3 | blade-cpp | 3.632 | llvm serial-only | 5.232 | 1.441 [1.368, 1.594] * | 4.419 | 1.217 [1.113, 1.355] * | 9.867 / 5.898 | 213323 n=21, suppressed |
| pairkernel | - | - | - | - | - | - | - | - | not declared in this regime |
| recur | - | - | - | - | - | - | - | - | not declared in this regime |
| band | none | - | blade-cpp no-parallel-form; blade-llvm serial-only | 1.179 | - | 1.914 | - | - / - | 213323 n=21, suppressed |

### serial-licensed-blas

| kernel | Blade ref | Blade median (ms) | other Blade lane (ms) | gf-expert (ms) | ratio gf-expert/Blade [95% CI] | ifx-expert (ms) | ratio ifx-expert/Blade [95% CI] | natural gf / ifx ratio | block |
|---|---|---|---|---|---|---|---|---|---|
| gram | blade-cpp | 64.53 | llvm no-blas | 65.39 | 1.013 [1.005, 1.023] | 62.48 | 0.968 [0.963, 0.983] | - / - | 214522 n=11 |
| antisym | - | - | - | - | - | - | - | - | not declared in this regime |
| comoment3 | - | - | - | - | - | - | - | - | not declared in this regime |
| pairkernel | - | - | - | - | - | - | - | - | not declared in this regime |
| recur | - | - | - | - | - | - | - | - | not declared in this regime |
| band | - | - | - | - | - | - | - | - | not declared in this regime |

### mt8-licensed-blas

| kernel | Blade ref | Blade median (ms) | other Blade lane (ms) | gf-expert (ms) | ratio gf-expert/Blade [95% CI] | ifx-expert (ms) | ratio ifx-expert/Blade [95% CI] | natural gf / ifx ratio | block |
|---|---|---|---|---|---|---|---|---|---|
| gram | blade-cpp | 24.46 | llvm no-blas | 19.27 | 0.788 [0.731, 0.869] | 19.98 | 0.817 [0.776, 0.897] | - / - | 214522 n=21 |
| antisym | - | - | - | - | - | - | - | - | not declared in this regime |
| comoment3 | - | - | - | - | - | - | - | - | not declared in this regime |
| pairkernel | - | - | - | - | - | - | - | - | not declared in this regime |
| recur | - | - | - | - | - | - | - | - | not declared in this regime |
| band | - | - | - | - | - | - | - | - | not declared in this regime |

### Control drift per re-measure session

| session | regime | wall (s) | blocks | suppressed | compute median ms | compute CV | compute range ms | memory median ms | memory CV | memory range ms | excursions dropped | aborted |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| session-201558 | serial-licensed | 674 | 6 | 2 | 12.74 | 10.4% | 12.43-18.71 | 5.27 | 11.3% | 5.11-8.37 | 0 | no |
| session-202815 | mt8-licensed | 744 | 6 | 3 | 12.73 | 4.5% | 12.49-17.77 | 5.27 | 4.4% | 5.08-6.62 | 0 | no |
| session-204141 | serial-repro | 623 | 6 | 0 | 12.81 | 1.9% | 12.48-13.71 | 5.28 | 4.1% | 5.13-6.21 | 0 | no |
| session-205306 | mt8-repro | 748 | 6 | 6 | 12.93 | 3.5% | 12.52-15.75 | 5.30 | 6.1% | 5.09-6.88 | 0 | no |
| session-210636 | serial-fastmath (band block disturbed by sibling build/test) | 417 | 4 | 3 | 12.98 | 31.1% | 12.54-41.66 | 5.58 | 21.3% | 5.15-11.42 | 0 | no |
| session-213323 | mt8-fastmath (first ~3 min overlapped sibling A/B) | 518 | 4 | 4 | 12.97 | 2.7% | 12.61-14.14 | 5.32 | 4.2% | 5.08-6.25 | 0 | no |
| session-214522 | gram blas | 124 | 2 | 0 | 12.86 | 1.8% | 12.66-13.66 | 5.37 | 10.4% | 5.20-8.59 | 0 | no |
| session-214954 | serial-fastmath band re-run | 128 | 1 | 0 | 12.77 | 1.9% | 12.54-13.53 | 5.39 | 3.2% | 5.18-5.86 | 0 | no |

### Sibling-session coordination (windows)

- Sessions 1-5 (serial-licensed, mt8-licensed, serial-repro, mt8-repro, serial-fastmath, run before the handshake existed): sibling activity was not coordinated. The serial-fastmath band block (21:07:57-21:10:04, compute control median 17.2 ms, max 41.7 ms) was disturbed by the sibling building/testing the compiler; band was re-measured cleanly (214954).
- Aborted attempts (kept as records): mt8-fastmath 21:14-21:16 (second excursion, sibling dotnet build running); rm2-mt8-fastmath 21:18-21:21 (control memory excursion 92 ms = 16.4x median; no build process was consuming CPU at launch).
- Window 1 (21:33:15-21:42:01, mt8-fastmath): announced 21:26; neither twin-busy nor twin-idle existed for the 5-minute wait (opened by timeout); twin-idle first written 21:42:09; the coordinator reports the sibling A/B ran 21:30-21:36, so the first ~3 minutes overlapped it. Controls stayed calm (compute CV 2.7%); not honoured / not confirmed.
- Window 2 (21:45:16-21:47:27, gram blas): announced 21:43; twin-idle present, twin-busy absent at open; session clean (controls compute CV 1.8%). Honoured as far as can be observed.
- Window 3 (21:49:48-21:52:02, band serial-fastmath): announced 21:47; twin-idle present at open; session clean (compute CV 1.9%). Honoured as far as can be observed.

## UPDATED FINAL TABLE on the final binary (Blade.dll 30FBBC97; antisym and comoment3 serial rows from session 9, everything else from sessions 1-8 on Blade.dll 79AF7EDC)

Label: **uncapped: max processor state 100% (Silent scheme); verdicts suppressed by drift reported as indicative.** Ratio = expert median / Blade median; **< 1 = Fortran faster**; reference blade-llvm (serial) / blade-cpp (mt8, blas); `*` = suppressed (indicative); `-` = no Blade arm / not declared. Blocks marked [S9] are session 9 (bundle session-220309, Blade.dll 30FBBC97; antisym and comoment3 blade-llvm now use the shared-C-core tiles); pairkernel serial rows were also re-measured in session 9 as the unchanged-emission control and agree with the earlier rows (licensed 1.135 / 1.046 vs 1.121 / 1.043; repro 0.834 / 0.772 vs 0.841 / 0.782).


### serial-licensed-native

| kernel | Blade ref | Blade median (ms) | other Blade lane (ms) | gf-expert (ms) | ratio gf-expert/Blade [95% CI] | ifx-expert (ms) | ratio ifx-expert/Blade [95% CI] | natural gf / ifx ratio | block |
|---|---|---|---|---|---|---|---|---|---|
| gram | blade-llvm | 56.1 | cpp 57.96 | 76.75 | 1.368 [1.355, 1.380] | 69.36 | 1.236 [1.227, 1.243] | 9.896 / 10.172 | 201558 n=11 |
| antisym | blade-llvm | 27.59 | cpp 28.56 | 31.06 | 1.126 [1.119, 1.138] | 29.61 | 1.073 [1.065, 1.085] | 28.226 / 12.173 | 220309 n=21 [S9] |
| comoment3 | blade-llvm | 9.312 | cpp 9.593 | 11.37 | 1.221 [1.208, 1.235] | 11.4 | 1.225 [1.219, 1.233] | 64.555 / 77.363 | 220309 n=21 [S9] |
| pairkernel | blade-llvm | 28.94 | cpp 29.14 | 32.44 | 1.121 [1.118, 1.125] | 30.18 | 1.043 [1.036, 1.047] | 2.082 / 2.230 | 201558 n=21 |
| recur | blade-llvm | 20.43 | cpp 22.49 | 10.07 | 0.493 [0.457, 0.523] * | 9.995 | 0.489 [0.466, 0.500] * | 0.929 / 0.770 | 201558 n=21, suppressed |
| band | blade-llvm | 3.318 | cpp 9.276 | 0.8135 | 0.245 [0.245, 0.246] | 1.042 | 0.314 [0.314, 0.314] | 62.375 / 105.554 | 201558 n=21 |

### mt8-licensed-native

| kernel | Blade ref | Blade median (ms) | other Blade lane (ms) | gf-expert (ms) | ratio gf-expert/Blade [95% CI] | ifx-expert (ms) | ratio ifx-expert/Blade [95% CI] | natural gf / ifx ratio | block |
|---|---|---|---|---|---|---|---|---|---|
| gram | blade-cpp | 15.58 | llvm serial-only | 19.87 | 1.276 [1.221, 1.322] | 19.06 | 1.223 [1.176, 1.258] | 37.560 / 39.420 | 202815 n=21 |
| antisym | blade-cpp | 8.79 | llvm serial-only | 9.5 | 1.081 [1.054, 1.114] * | 9.82 | 1.117 [1.080, 1.150] * | 12.788 / 8.206 | 202815 n=21, suppressed |
| comoment3 | blade-cpp | 3.311 | llvm serial-only | 4.839 | 1.462 [1.434, 1.502] | 4.03 | 1.217 [1.184, 1.271] | 28.356 / 35.423 | 202815 n=21 |
| pairkernel | blade-cpp | 7.67 | llvm serial-only | 6.27 | 0.817 [0.772, 0.840] * | 6.03 | 0.786 [0.744, 0.804] * | 1.980 / 2.187 | 202815 n=21, suppressed |
| recur | none | - | blade-cpp no-parallel-form; blade-llvm serial-only | 6.815 | - | 7.486 | - | - / - | 202815 n=21 |
| band | none | - | blade-cpp no-parallel-form; blade-llvm serial-only | 0.9909 | - | 1.705 | - | - / - | 202815 n=21, suppressed |

### serial-repro-native

| kernel | Blade ref | Blade median (ms) | other Blade lane (ms) | gf-expert (ms) | ratio gf-expert/Blade [95% CI] | ifx-expert (ms) | ratio ifx-expert/Blade [95% CI] | natural gf / ifx ratio | block |
|---|---|---|---|---|---|---|---|---|---|
| gram | blade-llvm | 59.73 | cpp 61.34 | 77.07 | 1.290 [1.230, 1.321] | 70.12 | 1.174 [1.119, 1.186] | 9.935 / 9.515 | 204141 n=11 |
| antisym | blade-llvm | 30.47 | cpp 30.65 | 31.77 | 1.043 [1.008, 1.073] | 30.46 | 1.000 [0.975, 1.020] | 25.507 / 11.011 | 220309 n=21 [S9] |
| comoment3 | blade-llvm | 26.46 | cpp 16.12 | 11.73 | 0.443 [0.417, 0.450] | 11.77 | 0.445 [0.420, 0.461] | 22.924 / 27.602 | 220309 n=21 [S9] |
| pairkernel | blade-llvm | 40.82 | cpp 37.85 | 34.32 | 0.841 [0.836, 0.852] | 31.91 | 0.782 [0.777, 0.790] | 1.489 / 1.565 | 204141 n=21 |
| recur | blade-llvm | 23.77 | cpp 23.85 | 12.95 | 0.545 [0.538, 0.554] | 13.34 | 0.561 [0.552, 0.571] | 0.748 / 0.626 | 204141 n=21 |
| band | blade-llvm | 9.277 | cpp 9.289 | 3.111 | 0.335 [0.334, 0.336] | 3.718 | 0.401 [0.399, 0.402] | 22.304 / 37.841 | 204141 n=21 |

### mt8-repro-native

| kernel | Blade ref | Blade median (ms) | other Blade lane (ms) | gf-expert (ms) | ratio gf-expert/Blade [95% CI] | ifx-expert (ms) | ratio ifx-expert/Blade [95% CI] | natural gf / ifx ratio | block |
|---|---|---|---|---|---|---|---|---|---|
| gram | blade-cpp | 19.14 | llvm serial-only | 20.66 | 1.080 [1.048, 1.124] * | 19.81 | 1.035 [1.002, 1.075] * | 28.891 / 32.206 | 205306 n=21, suppressed |
| antisym | blade-cpp | 10.88 | llvm serial-only | 10.1 | 0.929 [0.878, 0.983] * | 10.11 | 0.929 [0.903, 0.989] * | 10.400 / 6.644 | 205306 n=21, suppressed |
| comoment3 | blade-cpp | 9.91 | llvm serial-only | 4.861 | 0.490 [0.466, 0.521] * | 4.101 | 0.414 [0.391, 0.434] * | 9.669 / 12.172 | 205306 n=21, suppressed |
| pairkernel | blade-cpp | 17.34 | llvm serial-only | 6.217 | 0.359 [0.351, 0.378] * | 6.472 | 0.373 [0.354, 0.380] * | 0.897 / 0.978 | 205306 n=21, suppressed |
| recur | none | - | blade-cpp no-parallel-form; blade-llvm serial-only | 7.407 | - | 7.795 | - | - / - | 205306 n=21, suppressed |
| band | none | - | blade-cpp no-parallel-form; blade-llvm serial-only | 1.33 | - | 2.344 | - | - / - | 205306 n=21, suppressed |

### serial-fastmath-native

| kernel | Blade ref | Blade median (ms) | other Blade lane (ms) | gf-expert (ms) | ratio gf-expert/Blade [95% CI] | ifx-expert (ms) | ratio ifx-expert/Blade [95% CI] | natural gf / ifx ratio | block |
|---|---|---|---|---|---|---|---|---|---|
| gram | blade-llvm | 56.63 | cpp 59.56 | 76.7 | 1.354 [1.341, 1.377] * | 69.96 | 1.235 [1.218, 1.261] * | 9.725 / 3.529 | 210636 n=11, suppressed |
| antisym | blade-llvm | 27.67 | cpp 28.24 | 31.61 | 1.142 [1.105, 1.167] * | 30.35 | 1.097 [1.064, 1.115] * | 5.772 / 3.058 | 220309 n=21, suppressed [S9] |
| comoment3 | blade-llvm | 9.622 | cpp 10.6 | 11.98 | 1.245 [1.153, 1.300] * | 11.79 | 1.226 [1.154, 1.318] * | 15.714 / 9.433 | 220309 n=21, suppressed [S9] |
| pairkernel | - | - | - | - | - | - | - | - | not declared in this regime |
| recur | - | - | - | - | - | - | - | - | not declared in this regime |
| band | blade-llvm | 3.312 | cpp 9.277 | 1.699 | 0.513 [0.469, 0.517] | 1.322 | 0.399 [0.398, 0.401] | 91.102 / 100.087 | 214954 n=21 |

### mt8-fastmath-native

| kernel | Blade ref | Blade median (ms) | other Blade lane (ms) | gf-expert (ms) | ratio gf-expert/Blade [95% CI] | ifx-expert (ms) | ratio ifx-expert/Blade [95% CI] | natural gf / ifx ratio | block |
|---|---|---|---|---|---|---|---|---|---|
| gram | blade-cpp | 17.01 | llvm serial-only | 20.96 | 1.232 [1.173, 1.282] * | 19.83 | 1.166 [1.112, 1.202] * | 38.433 / 13.835 | 213323 n=21, suppressed |
| antisym | blade-cpp | 9.009 | llvm serial-only | 10.51 | 1.167 [1.109, 1.227] * | 10.36 | 1.150 [1.106, 1.211] * | 4.270 / 2.698 | 213323 n=21, suppressed |
| comoment3 | blade-cpp | 3.632 | llvm serial-only | 5.232 | 1.441 [1.368, 1.594] * | 4.419 | 1.217 [1.113, 1.355] * | 9.867 / 5.898 | 213323 n=21, suppressed |
| pairkernel | - | - | - | - | - | - | - | - | not declared in this regime |
| recur | - | - | - | - | - | - | - | - | not declared in this regime |
| band | none | - | blade-cpp no-parallel-form; blade-llvm serial-only | 1.179 | - | 1.914 | - | - / - | 213323 n=21, suppressed |

### serial-licensed-blas

| kernel | Blade ref | Blade median (ms) | other Blade lane (ms) | gf-expert (ms) | ratio gf-expert/Blade [95% CI] | ifx-expert (ms) | ratio ifx-expert/Blade [95% CI] | natural gf / ifx ratio | block |
|---|---|---|---|---|---|---|---|---|---|
| gram | blade-cpp | 64.53 | llvm no-blas | 65.39 | 1.013 [1.005, 1.023] | 62.48 | 0.968 [0.963, 0.983] | - / - | 214522 n=11 |
| antisym | - | - | - | - | - | - | - | - | not declared in this regime |
| comoment3 | - | - | - | - | - | - | - | - | not declared in this regime |
| pairkernel | - | - | - | - | - | - | - | - | not declared in this regime |
| recur | - | - | - | - | - | - | - | - | not declared in this regime |
| band | - | - | - | - | - | - | - | - | not declared in this regime |

### mt8-licensed-blas

| kernel | Blade ref | Blade median (ms) | other Blade lane (ms) | gf-expert (ms) | ratio gf-expert/Blade [95% CI] | ifx-expert (ms) | ratio ifx-expert/Blade [95% CI] | natural gf / ifx ratio | block |
|---|---|---|---|---|---|---|---|---|---|
| gram | blade-cpp | 24.46 | llvm no-blas | 19.27 | 0.788 [0.731, 0.869] | 19.98 | 0.817 [0.776, 0.897] | - / - | 214522 n=21 |
| antisym | - | - | - | - | - | - | - | - | not declared in this regime |
| comoment3 | - | - | - | - | - | - | - | - | not declared in this regime |
| pairkernel | - | - | - | - | - | - | - | - | not declared in this regime |
| recur | - | - | - | - | - | - | - | - | not declared in this regime |
| band | - | - | - | - | - | - | - | - | not declared in this regime |

Session 9 (session-220309, serial-licensed + serial-repro + serial-fastmath; antisym, comoment3, pairkernel; 21 rounds): wall 1046 s, exit 3 (flags only), 8 blocks, 3 suppressed, excursions dropped 0, no abort. Controls: compute median 12.99 ms CV 3.3% (range 12.50-15.43), memory median 5.43 ms CV 4.7% (range 5.10-6.57). Window A (21:56-22:20): sibling idle flag present and busy flag absent at open; announced 21:54.
Smoke refresh for antisym, comoment3, pairkernel (--regimes all): exit 0, 0 failures, 0 flags, 259 s (`bench/results/full/s9-smoke.console.txt`); `check <k> --size full --regimes serial-licensed-native,serial-repro-native --arms blade-llvm,blade-cpp`: all three exit 0, 0 flags. blade-llvm plan.txt (bench/.build/<k>/full/<regime>/blade-llvm/plan.txt): antisym `[microkernel v1] ... applied [antisym tile (shared C core blade_llvm_tiles.c over blade_tiles.h, sequential 6x8 (P pass, spill, Q pass), strict packed triangle); P=1201 N=401 ...]`; comoment3 `applied [simplex tile (shared C core blade_llvm_tiles.c over blade_tiles.h, W = X(i) * X(j) panel x 6x8 register tile over k, packed SymIdx<3> pool); P=171 N=201 KC=201 ...]`; pairkernel: no tile line.

## Final gate run (2026-10-18, 22:23-22:36)

- Binary: Blade.dll SHA-256 30FBBC9775F694F513F632BE8EF4B24489EB98C3DFB512857CE747E3BFEC58C7 (verified 21:53, built by the coordinator, tree 2dcc0739 plus docs commits to 99b7056f; no rebuild by the runner). Run from the bench worktree root, ucrt64 first on PATH, sibling session idle (window B, announced 22:21, opened 22:23 with twin-idle present), uncapped. Consoles `bench/results/gates/final-<gate>.txt`, per-gate wall times in `bench/results/gates/progress-final.txt`. No gate failed ([FAIL] count 0 in all five), so nothing was rerun.

| gate | TOTAL line (verbatim) | wall | skip explanation |
|---|---|---|---|
| `blade test` | `TOTAL: 6699 passed, 0 failed` | 322 s | no skipped suffix |
| `blade test interp` | `Interp Diff: 2889 passed, 0 failed, 2 skip-unsupported, 0 skipped` | 241 s | the same 2 as before (Fill Random Symmetric / Antisymmetric, nondeterministic C rand()%mod) |
| `blade test opt-diff` | `Opt Diff: 1976 passed (1838 optimizer-inert), 0 failed, 915 skipped, 73s` | 73 s | reject probes / programs both compiles refuse alike (tests/OptDiff.fs:160); count unchanged from the merged run |
| `blade test llvm` | `TOTAL: 722 passed, 0 failed, 677 skipped` | 119 s | the same 677 LLVM-lane refusals in `LLVM Differential` (591 passed); blocks `LLVM Tiles Raw-Bit` 10, `LLVM Blocked Simplex` 15, `LLVM Runtime Shim` 4 all pass |
| `blade test docs` | `Docs (executable code blocks): 131 passed, 0 failed, 78 skipped` | 8 s | 78 sketch blocks (by design) |

Versus the merged run (79AF7EDC): default 6686 -> 6699 passed, interp 2882 -> 2889, opt-diff 1969 -> 1976, llvm 700 -> 722 passed (skips unchanged), docs unchanged; zero failures throughout. The wall times are 3-8x shorter than the merged run's (838 / 2802 / 104 / 952 / 30 s) for the same corpus; the cause is not established from the output (the machine was otherwise idle for this run; a warm executable cache from the earlier run is a candidate).
