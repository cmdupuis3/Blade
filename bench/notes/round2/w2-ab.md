# Wave-2 harness A/B: w1 (wave-1 tip 6b3e5293, Blade.dll 3061E900) vs w2 (wave-2 tip, Blade.dll 2293124A = 1cf90428, bench-identical to f1910060) vs knob-off arms

Protocol: 23 `bench` sessions 14:55-16:22 (file clock), one `run.py bench` at a time, interleaved per cell (w1, w2, knob-off), harness
defaults (21 rounds; gram 11), full size, quiet box (W2-F and Codex idle; load before each session 2-13%), AC. Mirrors: `C:\bt_r2ab\w1`
(6b3e5293 binary + the wave-2 bench tree), `w2` (1cf90428 binary, default config), `w2a` (= w2 + `BLADE_ORBIT_SCATTER=0`),
`w2b` (= w2 + `BLADE_TILE_TUNE=anti_g=0,tri_edge=0` + `BLADE_JOIN_THREADS=0`); each smoked for every benched pair (0 failures);
w2a's bundles record `ordered orbit scatter not taken: disabled by BLADE_ORBIT_SCATTER=0` in both lanes. The 1cf90428 binary emits
byte-identical C++ / LLVM for all 12 bench kernels x {cpp, llvm} x {licensed, repro} as the final tip f1910060 (48/48), and src/cpp is
untouched between them. Zero failures / aborts / excursions; exit 3 = drift flags only (those blocks are `indicative`).
Ratios: B/A = median(B) / median(A) of the Blade reference lane (blade-llvm serial, blade-cpp mt8), analyst unpaired bootstrap CI
(4000 resamples; NOT a harness output); expert ratio = expert median / Blade ref median (> 1 Blade faster), harness CI and verdict.

## 0. Headline

| cell | item | w1 gf / ifx | w2 gf / ifx | w2 / w1 (Blade ref) | knob-off / w2 | class |
|---|---|---|---|---|---|---|
| pairkernel serial repro | A ordered scatter | 0.929 / 0.859 (loss) | **1.163 / 1.079** (win / detectable-win) | **0.796** [0.788, 0.800] llvm; 0.788 cpp | w2a 1.251 / 1.265 | w2 live (w1 ind) |
| comoment3 serial repro | A scatter rank 3 | 0.770 / 0.775 (loss) | **1.288 / 1.299** (win) | **0.600** [0.584, 0.609] llvm; 0.603 cpp | w2a 1.662 / 1.646 | live |
| pairkernel mt8 repro | (G35 owns it) | 0.673 / 0.666 | 0.687 / 0.664 | 1.022 [0.996, 1.043] | w2a 1.010 | ind |
| comoment3 mt8 repro | (G35 owns it) | 1.105 / 0.933 | 1.097 / 0.925 | 1.004 [0.972, 1.063] | w2a 0.996 | ind |
| comoment3 serial licensed | D3b tri_edge | 1.302 / 1.315 | **1.335 / 1.358** | 0.982 [0.967, 0.993] llvm; 0.979 cpp | w2b 1.021 [1.010, 1.033] | live |
| comoment3 mt8 licensed | D3b tri_edge | 1.551 / 1.345 | 1.560 / 1.321 | 0.979 [0.921, 1.045] | w2b 1.077 [0.999, 1.184] | ind |
| antisym serial licensed | D2 anti_g 1 vs auto | 1.114 / 1.067 | 1.109 / 1.059 | 1.015 [0.998, 1.059] llvm; 1.003 cpp | w2b (auto) 1.008 / 1.027 | live / ind |
| antisym mt8 licensed | D2 anti_g 1 vs auto | 1.123 / 1.121 (ind) | 1.107 / 1.145 (ind) | 1.020 [0.974, 1.052] | w2b (auto) **0.954** [0.904, 0.996]; w2b expert 1.170 / 1.206 live | ind |
| onepass mt8 licensed | G7 join team (NEW arm; w1 = old binary, new spelling) | 0.924 / 0.980 | **1.135 / 1.166** | **0.858** [0.818, 0.903] | w2b 1.153 [1.115, 1.223] | ind |
| dot mt8 licensed | G7 side effect | 0.945 / 0.973 (ind) | **1.053 / 1.127** (detectable-win / win) | **0.867** [0.834, 0.909] | w2b 1.096 [1.057, 1.159] | w2 live |
| fusedchain mt8 licensed | G7 side effect | 0.927 / 1.035 | **0.997 / 1.151** | **0.926** [0.897, 0.947] | w2b 1.113 [1.062, 1.183] | live |
| gram serial licensed | control | 1.366 / 1.230 | 1.369 / 1.239 | 0.998 / 0.987 | -- | live |

No Blade arm is slower on w2 than on w1 beyond its CI (section 5 below: none). Codex's item 7 (decline the join team if mt8 dot regressed)
does not fire: the team helps dot, onepass and fusedchain alike. The scatter is the whole serial-repro story (knob-off restores w1's
times to within noise); it leaves mt8 repro to the G35 row slots as designed. The simplex edge k-loops are a measured 2% (CI excludes 1)
serially. The antisym row-group default: anti_g=1 (the previous visit order) vs auto are within noise serially (both lanes); under
threads auto is 4.6% faster at the edge of its CI on an indicative block -- the default stays at 1 this round, recorded as a quiet-session
re-measure (follow-up), since flipping it would re-open the gates and the mirrors for a marginal, flagged signal.


Mirrors: w1 = w1 (6b3e5293, wave-1 tip); w2 = w2 (wave-2 tip, defaults); w2a = w2a (BLADE_ORBIT_SCATTER=0); w2b = w2b (anti_g=0,tri_edge=0; BLADE_JOIN_THREADS=0)

## 1. Pairwise medians: B/A < 1 = B faster. Class live = neither block drift-suppressed; ind = at least one indicative. CI = analyst unpaired bootstrap (not a harness output).

| kernel | regime | item | B/A | arm | A -> B median (ms) | B/A [95% CI] | class n_A/n_B |
|---|---|---|---|---|---|---|---|
| pairkernel | serial-repro-native | A scatter | w2/w1 | blade-llvm | 37.05 -> 29.49 | 0.796 [0.788, 0.800] | ind 21/21 |
| pairkernel | serial-repro-native | A scatter | w2/w1 | blade-cpp | 37.45 -> 29.52 | 0.788 [0.773, 0.794] | ind 21/21 |
| pairkernel | serial-repro-native | A scatter | w2/w1 | gfortran-natural | 60.72 -> 60.34 | 0.994 [0.988, 0.999] | ind 21/21 |
| pairkernel | serial-repro-native | A scatter | w2/w1 | gfortran-expert | 34.42 -> 34.29 | 0.996 [0.980, 1.001] | ind 21/21 |
| pairkernel | serial-repro-native | A scatter | w2/w1 | ifx-natural | 64.05 -> 63.88 | 0.997 [0.991, 1.003] | ind 21/21 |
| pairkernel | serial-repro-native | A scatter | w2/w1 | ifx-expert | 31.81 -> 31.8 | 1.000 [0.996, 1.004] | ind 21/21 |
| pairkernel | serial-repro-native | A scatter | w2a/w2 | blade-llvm | 29.49 -> 36.9 | 1.251 [1.243, 1.267] | live 21/21 |
| pairkernel | serial-repro-native | A scatter | w2a/w2 | blade-cpp | 29.52 -> 37.34 | 1.265 [1.252, 1.278] | live 21/21 |
| pairkernel | serial-repro-native | A scatter | w2a/w2 | gfortran-natural | 60.34 -> 60.29 | 0.999 [0.997, 1.002] | live 21/21 |
| pairkernel | serial-repro-native | A scatter | w2a/w2 | gfortran-expert | 34.29 -> 34.27 | 0.999 [0.998, 1.001] | live 21/21 |
| pairkernel | serial-repro-native | A scatter | w2a/w2 | ifx-natural | 63.88 -> 63.89 | 1.000 [0.996, 1.011] | live 21/21 |
| pairkernel | serial-repro-native | A scatter | w2a/w2 | ifx-expert | 31.8 -> 31.92 | 1.004 [1.000, 1.010] | live 21/21 |
| pairkernel | mt8-repro-native | A scatter (mt8: G35 slots; wavefront) | w2/w1 | blade-llvm | -- | | (serial-only / serial-only) |
| pairkernel | mt8-repro-native | A scatter (mt8: G35 slots; wavefront) | w2/w1 | blade-cpp | 9.157 -> 9.354 | 1.022 [0.996, 1.043] | ind 21/21 |
| pairkernel | mt8-repro-native | A scatter (mt8: G35 slots; wavefront) | w2/w1 | gfortran-natural | 13.51 -> 13.7 | 1.014 [0.950, 1.104] | ind 21/21 |
| pairkernel | mt8-repro-native | A scatter (mt8: G35 slots; wavefront) | w2/w1 | gfortran-expert | 6.16 -> 6.423 | 1.043 [1.005, 1.066] | ind 21/21 |
| pairkernel | mt8-repro-native | A scatter (mt8: G35 slots; wavefront) | w2/w1 | ifx-natural | 15.07 -> 17.16 | 1.139 [1.031, 1.160] | ind 21/21 |
| pairkernel | mt8-repro-native | A scatter (mt8: G35 slots; wavefront) | w2/w1 | ifx-expert | 6.103 -> 6.213 | 1.018 [0.987, 1.042] | ind 21/21 |
| pairkernel | mt8-repro-native | A scatter (mt8: G35 slots; wavefront) | w2a/w2 | blade-llvm | -- | | (serial-only / serial-only) |
| pairkernel | mt8-repro-native | A scatter (mt8: G35 slots; wavefront) | w2a/w2 | blade-cpp | 9.354 -> 9.446 | 1.010 [0.978, 1.039] | ind 21/21 |
| pairkernel | mt8-repro-native | A scatter (mt8: G35 slots; wavefront) | w2a/w2 | gfortran-natural | 13.7 -> 13.87 | 1.012 [0.924, 1.092] | ind 21/21 |
| pairkernel | mt8-repro-native | A scatter (mt8: G35 slots; wavefront) | w2a/w2 | gfortran-expert | 6.423 -> 6.284 | 0.978 [0.954, 1.012] | ind 21/21 |
| pairkernel | mt8-repro-native | A scatter (mt8: G35 slots; wavefront) | w2a/w2 | ifx-natural | 17.16 -> 15.92 | 0.928 [0.871, 1.034] | ind 21/21 |
| pairkernel | mt8-repro-native | A scatter (mt8: G35 slots; wavefront) | w2a/w2 | ifx-expert | 6.213 -> 6.225 | 1.002 [0.976, 1.027] | ind 21/21 |
| comoment3 | serial-repro-native | A scatter rank 3 | w2/w1 | blade-llvm | 14.87 -> 8.927 | 0.600 [0.584, 0.609] | live 21/21 |
| comoment3 | serial-repro-native | A scatter rank 3 | w2/w1 | blade-cpp | 15.02 -> 9.056 | 0.603 [0.595, 0.606] | live 21/21 |
| comoment3 | serial-repro-native | A scatter rank 3 | w2/w1 | gfortran-natural | 600.1 -> 600 | 1.000 [0.999, 1.001] | live 21/21 |
| comoment3 | serial-repro-native | A scatter rank 3 | w2/w1 | gfortran-expert | 11.45 -> 11.5 | 1.004 [0.991, 1.027] | live 21/21 |
| comoment3 | serial-repro-native | A scatter rank 3 | w2/w1 | ifx-natural | 718.5 -> 718.6 | 1.000 [0.997, 1.003] | live 21/21 |
| comoment3 | serial-repro-native | A scatter rank 3 | w2/w1 | ifx-expert | 11.52 -> 11.6 | 1.007 [0.989, 1.011] | live 21/21 |
| comoment3 | serial-repro-native | A scatter rank 3 | w2a/w2 | blade-llvm | 8.927 -> 14.83 | 1.662 [1.629, 1.694] | live 21/21 |
| comoment3 | serial-repro-native | A scatter rank 3 | w2a/w2 | blade-cpp | 9.056 -> 14.91 | 1.646 [1.636, 1.682] | live 21/21 |
| comoment3 | serial-repro-native | A scatter rank 3 | w2a/w2 | gfortran-natural | 600 -> 600.1 | 1.000 [1.000, 1.001] | live 21/21 |
| comoment3 | serial-repro-native | A scatter rank 3 | w2a/w2 | gfortran-expert | 11.5 -> 11.44 | 0.995 [0.972, 1.009] | live 21/21 |
| comoment3 | serial-repro-native | A scatter rank 3 | w2a/w2 | ifx-natural | 718.6 -> 718.2 | 0.999 [0.997, 1.001] | live 21/21 |
| comoment3 | serial-repro-native | A scatter rank 3 | w2a/w2 | ifx-expert | 11.6 -> 11.52 | 0.993 [0.989, 1.006] | live 21/21 |
| comoment3 | mt8-repro-native | A scatter rank 3 (mt8) | w2/w1 | blade-llvm | -- | | (serial-only / serial-only) |
| comoment3 | mt8-repro-native | A scatter rank 3 (mt8) | w2/w1 | blade-cpp | 4.431 -> 4.449 | 1.004 [0.972, 1.063] | ind 21/21 |
| comoment3 | mt8-repro-native | A scatter rank 3 (mt8) | w2/w1 | gfortran-natural | 91.96 -> 91.01 | 0.990 [0.967, 1.069] | ind 21/21 |
| comoment3 | mt8-repro-native | A scatter rank 3 (mt8) | w2/w1 | gfortran-expert | 4.896 -> 4.88 | 0.997 [0.980, 1.021] | ind 21/21 |
| comoment3 | mt8-repro-native | A scatter rank 3 (mt8) | w2/w1 | ifx-natural | 114.9 -> 108 | 0.940 [0.903, 1.042] | ind 21/21 |
| comoment3 | mt8-repro-native | A scatter rank 3 (mt8) | w2/w1 | ifx-expert | 4.134 -> 4.115 | 0.995 [0.948, 1.054] | ind 21/21 |
| comoment3 | mt8-repro-native | A scatter rank 3 (mt8) | w2a/w2 | blade-llvm | -- | | (serial-only / serial-only) |
| comoment3 | mt8-repro-native | A scatter rank 3 (mt8) | w2a/w2 | blade-cpp | 4.449 -> 4.429 | 0.996 [0.942, 1.026] | ind 21/21 |
| comoment3 | mt8-repro-native | A scatter rank 3 (mt8) | w2a/w2 | gfortran-natural | 91.01 -> 90.75 | 0.997 [0.946, 1.019] | ind 21/21 |
| comoment3 | mt8-repro-native | A scatter rank 3 (mt8) | w2a/w2 | gfortran-expert | 4.88 -> 4.879 | 1.000 [0.973, 1.017] | ind 21/21 |
| comoment3 | mt8-repro-native | A scatter rank 3 (mt8) | w2a/w2 | ifx-natural | 108 -> 112.6 | 1.042 [0.962, 1.107] | ind 21/21 |
| comoment3 | mt8-repro-native | A scatter rank 3 (mt8) | w2a/w2 | ifx-expert | 4.115 -> 4.175 | 1.015 [0.962, 1.048] | ind 21/21 |
| comoment3 | serial-licensed-native | D3b tri_edge | w2/w1 | blade-llvm | 8.657 -> 8.501 | 0.982 [0.967, 0.993] | live 21/21 |
| comoment3 | serial-licensed-native | D3b tri_edge | w2/w1 | blade-cpp | 8.829 -> 8.643 | 0.979 [0.966, 0.988] | live 21/21 |
| comoment3 | serial-licensed-native | D3b tri_edge | w2/w1 | gfortran-natural | 599.9 -> 600 | 1.000 [1.000, 1.001] | live 21/21 |
| comoment3 | serial-licensed-native | D3b tri_edge | w2/w1 | gfortran-expert | 11.28 -> 11.35 | 1.006 [0.997, 1.021] | live 21/21 |
| comoment3 | serial-licensed-native | D3b tri_edge | w2/w1 | ifx-natural | 717.7 -> 718.5 | 1.001 [0.999, 1.003] | live 21/21 |
| comoment3 | serial-licensed-native | D3b tri_edge | w2/w1 | ifx-expert | 11.39 -> 11.54 | 1.014 [0.998, 1.023] | live 21/21 |
| comoment3 | serial-licensed-native | D3b tri_edge | w2b/w2 | blade-llvm | 8.501 -> 8.676 | 1.021 [1.010, 1.033] | live 21/21 |
| comoment3 | serial-licensed-native | D3b tri_edge | w2b/w2 | blade-cpp | 8.643 -> 8.825 | 1.021 [1.007, 1.035] | live 21/21 |
| comoment3 | serial-licensed-native | D3b tri_edge | w2b/w2 | gfortran-natural | 600 -> 600.1 | 1.000 [0.999, 1.000] | live 21/21 |
| comoment3 | serial-licensed-native | D3b tri_edge | w2b/w2 | gfortran-expert | 11.35 -> 11.18 | 0.985 [0.974, 0.997] | live 21/21 |
| comoment3 | serial-licensed-native | D3b tri_edge | w2b/w2 | ifx-natural | 718.5 -> 718.9 | 1.001 [0.998, 1.004] | live 21/21 |
| comoment3 | serial-licensed-native | D3b tri_edge | w2b/w2 | ifx-expert | 11.54 -> 11.59 | 1.004 [0.993, 1.021] | live 21/21 |
| comoment3 | mt8-licensed-native | D3b tri_edge (mt8) | w2/w1 | blade-llvm | -- | | (serial-only / serial-only) |
| comoment3 | mt8-licensed-native | D3b tri_edge (mt8) | w2/w1 | blade-cpp | 3.184 -> 3.118 | 0.979 [0.921, 1.045] | ind 21/21 |
| comoment3 | mt8-licensed-native | D3b tri_edge (mt8) | w2/w1 | gfortran-natural | 92.46 -> 88.03 | 0.952 [0.909, 0.992] | ind 21/21 |
| comoment3 | mt8-licensed-native | D3b tri_edge (mt8) | w2/w1 | gfortran-expert | 4.938 -> 4.865 | 0.985 [0.922, 1.034] | ind 21/21 |
| comoment3 | mt8-licensed-native | D3b tri_edge (mt8) | w2/w1 | ifx-natural | 118.1 -> 112.7 | 0.955 [0.840, 1.008] | ind 21/21 |
| comoment3 | mt8-licensed-native | D3b tri_edge (mt8) | w2/w1 | ifx-expert | 4.283 -> 4.119 | 0.962 [0.924, 1.024] | ind 21/21 |
| comoment3 | mt8-licensed-native | D3b tri_edge (mt8) | w2b/w2 | blade-llvm | -- | | (serial-only / serial-only) |
| comoment3 | mt8-licensed-native | D3b tri_edge (mt8) | w2b/w2 | blade-cpp | 3.118 -> 3.358 | 1.077 [0.999, 1.184] | ind 21/21 |
| comoment3 | mt8-licensed-native | D3b tri_edge (mt8) | w2b/w2 | gfortran-natural | 88.03 -> 94.95 | 1.079 [1.021, 1.116] | ind 21/21 |
| comoment3 | mt8-licensed-native | D3b tri_edge (mt8) | w2b/w2 | gfortran-expert | 4.865 -> 5.135 | 1.056 [0.998, 1.108] | ind 21/21 |
| comoment3 | mt8-licensed-native | D3b tri_edge (mt8) | w2b/w2 | ifx-natural | 112.7 -> 122.6 | 1.088 [0.996, 1.239] | ind 21/21 |
| comoment3 | mt8-licensed-native | D3b tri_edge (mt8) | w2b/w2 | ifx-expert | 4.119 -> 4.342 | 1.054 [0.996, 1.078] | ind 21/21 |
| antisym | serial-licensed-native | D2 anti_g 1 vs auto | w2/w1 | blade-llvm | 27.69 -> 28.1 | 1.015 [0.998, 1.059] | live 21/21 |
| antisym | serial-licensed-native | D2 anti_g 1 vs auto | w2/w1 | blade-cpp | 28.64 -> 28.73 | 1.003 [0.986, 1.038] | live 21/21 |
| antisym | serial-licensed-native | D2 anti_g 1 vs auto | w2/w1 | gfortran-natural | 771.9 -> 774.9 | 1.004 [1.000, 1.007] | live 21/21 |
| antisym | serial-licensed-native | D2 anti_g 1 vs auto | w2/w1 | gfortran-expert | 30.86 -> 31.16 | 1.010 [0.999, 1.036] | live 21/21 |
| antisym | serial-licensed-native | D2 anti_g 1 vs auto | w2/w1 | ifx-natural | 332.9 -> 331.9 | 0.997 [0.993, 1.005] | live 21/21 |
| antisym | serial-licensed-native | D2 anti_g 1 vs auto | w2/w1 | ifx-expert | 29.54 -> 29.77 | 1.008 [0.987, 1.027] | live 21/21 |
| antisym | serial-licensed-native | D2 anti_g 1 vs auto | w2b/w2 | blade-llvm | 28.1 -> 28.33 | 1.008 [0.963, 1.027] | ind 21/21 |
| antisym | serial-licensed-native | D2 anti_g 1 vs auto | w2b/w2 | blade-cpp | 28.73 -> 29.5 | 1.027 [0.992, 1.049] | ind 21/21 |
| antisym | serial-licensed-native | D2 anti_g 1 vs auto | w2b/w2 | gfortran-natural | 774.9 -> 771.8 | 0.996 [0.994, 1.003] | ind 21/21 |
| antisym | serial-licensed-native | D2 anti_g 1 vs auto | w2b/w2 | gfortran-expert | 31.16 -> 31.65 | 1.016 [0.991, 1.071] | ind 21/21 |
| antisym | serial-licensed-native | D2 anti_g 1 vs auto | w2b/w2 | ifx-natural | 331.9 -> 332 | 1.000 [0.996, 1.013] | ind 21/21 |
| antisym | serial-licensed-native | D2 anti_g 1 vs auto | w2b/w2 | ifx-expert | 29.77 -> 30.96 | 1.040 [0.985, 1.122] | ind 21/21 |
| antisym | mt8-licensed-native | D2 anti_g 1 vs auto (mt8) | w2/w1 | blade-llvm | -- | | (serial-only / serial-only) |
| antisym | mt8-licensed-native | D2 anti_g 1 vs auto (mt8) | w2/w1 | blade-cpp | 8.535 -> 8.707 | 1.020 [0.974, 1.052] | ind 21/21 |
| antisym | mt8-licensed-native | D2 anti_g 1 vs auto (mt8) | w2/w1 | gfortran-natural | 107.6 -> 108.1 | 1.005 [0.990, 1.011] | ind 21/21 |
| antisym | mt8-licensed-native | D2 anti_g 1 vs auto (mt8) | w2/w1 | gfortran-expert | 9.588 -> 9.639 | 1.005 [0.952, 1.054] | ind 21/21 |
| antisym | mt8-licensed-native | D2 anti_g 1 vs auto (mt8) | w2/w1 | ifx-natural | 64.47 -> 65.65 | 1.018 [0.958, 1.326] | ind 21/21 |
| antisym | mt8-licensed-native | D2 anti_g 1 vs auto (mt8) | w2/w1 | ifx-expert | 9.571 -> 9.969 | 1.041 [1.015, 1.073] | ind 21/21 |
| antisym | mt8-licensed-native | D2 anti_g 1 vs auto (mt8) | w2b/w2 | blade-llvm | -- | | (serial-only / serial-only) |
| antisym | mt8-licensed-native | D2 anti_g 1 vs auto (mt8) | w2b/w2 | blade-cpp | 8.707 -> 8.308 | 0.954 [0.904, 0.996] | ind 21/21 |
| antisym | mt8-licensed-native | D2 anti_g 1 vs auto (mt8) | w2b/w2 | gfortran-natural | 108.1 -> 104.6 | 0.967 [0.926, 0.989] | ind 21/21 |
| antisym | mt8-licensed-native | D2 anti_g 1 vs auto (mt8) | w2b/w2 | gfortran-expert | 9.639 -> 9.724 | 1.009 [0.968, 1.048] | ind 21/21 |
| antisym | mt8-licensed-native | D2 anti_g 1 vs auto (mt8) | w2b/w2 | ifx-natural | 65.65 -> 60.91 | 0.928 [0.812, 1.028] | ind 21/21 |
| antisym | mt8-licensed-native | D2 anti_g 1 vs auto (mt8) | w2b/w2 | ifx-expert | 9.969 -> 10.02 | 1.005 [0.968, 1.050] | ind 21/21 |
| onepass | mt8-licensed-native | G7 join team (new arm; w1 = old binary, new spelling) | w2/w1 | blade-llvm | -- | | (serial-only / serial-only) |
| onepass | mt8-licensed-native | G7 join team (new arm; w1 = old binary, new spelling) | w2/w1 | blade-cpp | 8.88 -> 7.618 | 0.858 [0.818, 0.903] | ind 21/21 |
| onepass | mt8-licensed-native | G7 join team (new arm; w1 = old binary, new spelling) | w2/w1 | gfortran-natural | 14.32 -> 14.83 | 1.036 [1.000, 1.058] | ind 21/21 |
| onepass | mt8-licensed-native | G7 join team (new arm; w1 = old binary, new spelling) | w2/w1 | gfortran-expert | 8.203 -> 8.648 | 1.054 [1.022, 1.110] | ind 21/21 |
| onepass | mt8-licensed-native | G7 join team (new arm; w1 = old binary, new spelling) | w2/w1 | ifx-natural | 14.94 -> 15.14 | 1.013 [0.991, 1.045] | ind 21/21 |
| onepass | mt8-licensed-native | G7 join team (new arm; w1 = old binary, new spelling) | w2/w1 | ifx-expert | 8.706 -> 8.884 | 1.020 [0.992, 1.049] | ind 21/21 |
| onepass | mt8-licensed-native | G7 join team (new arm; w1 = old binary, new spelling) | w2b/w2 | blade-llvm | -- | | (serial-only / serial-only) |
| onepass | mt8-licensed-native | G7 join team (new arm; w1 = old binary, new spelling) | w2b/w2 | blade-cpp | 7.618 -> 8.785 | 1.153 [1.115, 1.223] | ind 21/21 |
| onepass | mt8-licensed-native | G7 join team (new arm; w1 = old binary, new spelling) | w2b/w2 | gfortran-natural | 14.83 -> 14.62 | 0.986 [0.963, 1.002] | ind 21/21 |
| onepass | mt8-licensed-native | G7 join team (new arm; w1 = old binary, new spelling) | w2b/w2 | gfortran-expert | 8.648 -> 8.243 | 0.953 [0.921, 1.000] | ind 21/21 |
| onepass | mt8-licensed-native | G7 join team (new arm; w1 = old binary, new spelling) | w2b/w2 | ifx-natural | 15.14 -> 14.87 | 0.982 [0.953, 1.006] | ind 21/21 |
| onepass | mt8-licensed-native | G7 join team (new arm; w1 = old binary, new spelling) | w2b/w2 | ifx-expert | 8.884 -> 8.667 | 0.976 [0.953, 1.003] | ind 21/21 |
| dot | mt8-licensed-native | G7 side effect | w2/w1 | blade-llvm | -- | | (serial-only / serial-only) |
| dot | mt8-licensed-native | G7 side effect | w2/w1 | blade-cpp | 10.84 -> 9.395 | 0.867 [0.834, 0.909] | ind 21/21 |
| dot | mt8-licensed-native | G7 side effect | w2/w1 | gfortran-natural | 11.03 -> 10.94 | 0.992 [0.975, 1.019] | ind 21/21 |
| dot | mt8-licensed-native | G7 side effect | w2/w1 | gfortran-expert | 10.24 -> 9.89 | 0.965 [0.950, 1.006] | ind 21/21 |
| dot | mt8-licensed-native | G7 side effect | w2/w1 | ifx-natural | 11.54 -> 11.44 | 0.992 [0.980, 1.000] | ind 21/21 |
| dot | mt8-licensed-native | G7 side effect | w2/w1 | ifx-expert | 10.55 -> 10.59 | 1.004 [0.973, 1.030] | ind 21/21 |
| dot | mt8-licensed-native | G7 side effect | w2b/w2 | blade-llvm | -- | | (serial-only / serial-only) |
| dot | mt8-licensed-native | G7 side effect | w2b/w2 | blade-cpp | 9.395 -> 10.29 | 1.096 [1.057, 1.159] | ind 21/21 |
| dot | mt8-licensed-native | G7 side effect | w2b/w2 | gfortran-natural | 10.94 -> 10.95 | 1.000 [0.971, 1.026] | ind 21/21 |
| dot | mt8-licensed-native | G7 side effect | w2b/w2 | gfortran-expert | 9.89 -> 9.992 | 1.010 [0.965, 1.029] | ind 21/21 |
| dot | mt8-licensed-native | G7 side effect | w2b/w2 | ifx-natural | 11.44 -> 11.37 | 0.993 [0.979, 1.008] | ind 21/21 |
| dot | mt8-licensed-native | G7 side effect | w2b/w2 | ifx-expert | 10.59 -> 10.44 | 0.986 [0.964, 1.017] | ind 21/21 |
| fusedchain | mt8-licensed-native | G7 side effect | w2/w1 | blade-llvm | -- | | (serial-only / serial-only) |
| fusedchain | mt8-licensed-native | G7 side effect | w2/w1 | blade-cpp | 3.306 -> 3.061 | 0.926 [0.897, 0.947] | live 21/21 |
| fusedchain | mt8-licensed-native | G7 side effect | w2/w1 | gfortran-natural | 11.4 -> 11.49 | 1.008 [0.960, 1.031] | live 21/21 |
| fusedchain | mt8-licensed-native | G7 side effect | w2/w1 | gfortran-expert | 3.065 -> 3.05 | 0.995 [0.949, 1.027] | live 21/21 |
| fusedchain | mt8-licensed-native | G7 side effect | w2/w1 | ifx-natural | 11.65 -> 11.61 | 0.996 [0.961, 1.028] | live 21/21 |
| fusedchain | mt8-licensed-native | G7 side effect | w2/w1 | ifx-expert | 3.423 -> 3.522 | 1.029 [0.980, 1.085] | live 21/21 |
| fusedchain | mt8-licensed-native | G7 side effect | w2b/w2 | blade-llvm | -- | | (serial-only / serial-only) |
| fusedchain | mt8-licensed-native | G7 side effect | w2b/w2 | blade-cpp | 3.061 -> 3.407 | 1.113 [1.062, 1.183] | live 21/21 |
| fusedchain | mt8-licensed-native | G7 side effect | w2b/w2 | gfortran-natural | 11.49 -> 11.31 | 0.984 [0.961, 1.042] | live 21/21 |
| fusedchain | mt8-licensed-native | G7 side effect | w2b/w2 | gfortran-expert | 3.05 -> 3.074 | 1.008 [0.967, 1.045] | live 21/21 |
| fusedchain | mt8-licensed-native | G7 side effect | w2b/w2 | ifx-natural | 11.61 -> 11.57 | 0.997 [0.962, 1.019] | live 21/21 |
| fusedchain | mt8-licensed-native | G7 side effect | w2b/w2 | ifx-expert | 3.522 -> 3.369 | 0.957 [0.914, 1.046] | live 21/21 |
| gram | serial-licensed-native | control | w2/w1 | blade-llvm | 56.7 -> 56.59 | 0.998 [0.973, 1.011] | live 11/11 |
| gram | serial-licensed-native | control | w2/w1 | blade-cpp | 58.66 -> 57.88 | 0.987 [0.962, 0.993] | live 11/11 |
| gram | serial-licensed-native | control | w2/w1 | gfortran-natural | 580.7 -> 459.3 | 0.791 [0.764, 0.799] | live 11/11 |
| gram | serial-licensed-native | control | w2/w1 | gfortran-expert | 77.47 -> 77.49 | 1.000 [0.984, 1.009] | live 11/11 |
| gram | serial-licensed-native | control | w2/w1 | ifx-natural | 568.1 -> 568.4 | 1.000 [0.999, 1.001] | live 11/11 |
| gram | serial-licensed-native | control | w2/w1 | ifx-expert | 69.72 -> 70.12 | 1.006 [1.001, 1.017] | live 11/11 |

## 2. Arm medians per mirror (ms)

| kernel | regime | mirror | block | blade-llvm | blade-cpp | gfortran-natural | gfortran-expert | ifx-natural | ifx-expert |
|---|---|---|---|---|---|---|---|---|---|
| pairkernel | serial-repro-native | w1 | indicative | 37.05 | 37.45 | 60.72 | 34.42 | 64.05 | 31.81 |
| pairkernel | serial-repro-native | w2 | live | 29.49 | 29.52 | 60.34 | 34.29 | 63.88 | 31.8 |
| pairkernel | serial-repro-native | w2a | live | 36.9 | 37.34 | 60.29 | 34.27 | 63.89 | 31.92 |
| pairkernel | mt8-repro-native | w1 | live | serial-only: LLVM lane has no OpenMP | 9.157 | 13.51 | 6.16 | 15.07 | 6.103 |
| pairkernel | mt8-repro-native | w2 | indicative | serial-only: LLVM lane has no OpenMP | 9.354 | 13.7 | 6.423 | 17.16 | 6.213 |
| pairkernel | mt8-repro-native | w2a | indicative | serial-only: LLVM lane has no OpenMP | 9.446 | 13.87 | 6.284 | 15.92 | 6.225 |
| comoment3 | serial-repro-native | w1 | live | 14.87 | 15.02 | 600.1 | 11.45 | 718.5 | 11.52 |
| comoment3 | serial-repro-native | w2 | live | 8.927 | 9.056 | 600 | 11.5 | 718.6 | 11.6 |
| comoment3 | serial-repro-native | w2a | live | 14.83 | 14.91 | 600.1 | 11.44 | 718.2 | 11.52 |
| comoment3 | mt8-repro-native | w1 | live | serial-only: LLVM lane has no OpenMP | 4.431 | 91.96 | 4.896 | 114.9 | 4.134 |
| comoment3 | mt8-repro-native | w2 | indicative | serial-only: LLVM lane has no OpenMP | 4.449 | 91.01 | 4.88 | 108 | 4.115 |
| comoment3 | mt8-repro-native | w2a | live | serial-only: LLVM lane has no OpenMP | 4.429 | 90.75 | 4.879 | 112.6 | 4.175 |
| comoment3 | serial-licensed-native | w1 | live | 8.657 | 8.829 | 599.9 | 11.28 | 717.7 | 11.39 |
| comoment3 | serial-licensed-native | w2 | live | 8.501 | 8.643 | 600 | 11.35 | 718.5 | 11.54 |
| comoment3 | serial-licensed-native | w2b | live | 8.676 | 8.825 | 600.1 | 11.18 | 718.9 | 11.59 |
| comoment3 | mt8-licensed-native | w1 | live | serial-only: LLVM lane has no OpenMP | 3.184 | 92.46 | 4.938 | 118.1 | 4.283 |
| comoment3 | mt8-licensed-native | w2 | indicative | serial-only: LLVM lane has no OpenMP | 3.118 | 88.03 | 4.865 | 112.7 | 4.119 |
| comoment3 | mt8-licensed-native | w2b | indicative | serial-only: LLVM lane has no OpenMP | 3.358 | 94.95 | 5.135 | 122.6 | 4.342 |
| antisym | serial-licensed-native | w1 | live | 27.69 | 28.64 | 771.9 | 30.86 | 332.9 | 29.54 |
| antisym | serial-licensed-native | w2 | live | 28.1 | 28.73 | 774.9 | 31.16 | 331.9 | 29.77 |
| antisym | serial-licensed-native | w2b | indicative | 28.33 | 29.5 | 771.8 | 31.65 | 332 | 30.96 |
| antisym | mt8-licensed-native | w1 | indicative | serial-only: LLVM lane has no OpenMP | 8.535 | 107.6 | 9.588 | 64.47 | 9.571 |
| antisym | mt8-licensed-native | w2 | indicative | serial-only: LLVM lane has no OpenMP | 8.707 | 108.1 | 9.639 | 65.65 | 9.969 |
| antisym | mt8-licensed-native | w2b | live | serial-only: LLVM lane has no OpenMP | 8.308 | 104.6 | 9.724 | 60.91 | 10.02 |
| onepass | mt8-licensed-native | w1 | indicative | serial-only: LLVM lane has no OpenMP | 8.88 | 14.32 | 8.203 | 14.94 | 8.706 |
| onepass | mt8-licensed-native | w2 | indicative | serial-only: LLVM lane has no OpenMP | 7.618 | 14.83 | 8.648 | 15.14 | 8.884 |
| onepass | mt8-licensed-native | w2b | indicative | serial-only: LLVM lane has no OpenMP | 8.785 | 14.62 | 8.243 | 14.87 | 8.667 |
| dot | mt8-licensed-native | w1 | indicative | serial-only: LLVM lane has no OpenMP | 10.84 | 11.03 | 10.24 | 11.54 | 10.55 |
| dot | mt8-licensed-native | w2 | live | serial-only: LLVM lane has no OpenMP | 9.395 | 10.94 | 9.89 | 11.44 | 10.59 |
| dot | mt8-licensed-native | w2b | indicative | serial-only: LLVM lane has no OpenMP | 10.29 | 10.95 | 9.992 | 11.37 | 10.44 |
| fusedchain | mt8-licensed-native | w1 | live | serial-only: LLVM lane has no OpenMP | 3.306 | 11.4 | 3.065 | 11.65 | 3.423 |
| fusedchain | mt8-licensed-native | w2 | live | serial-only: LLVM lane has no OpenMP | 3.061 | 11.49 | 3.05 | 11.61 | 3.522 |
| fusedchain | mt8-licensed-native | w2b | live | serial-only: LLVM lane has no OpenMP | 3.407 | 11.31 | 3.074 | 11.57 | 3.369 |
| gram | serial-licensed-native | w1 | live | 56.7 | 58.66 | 580.7 | 77.47 | 568.1 | 69.72 |
| gram | serial-licensed-native | w2 | live | 56.59 | 57.88 | 459.3 | 77.49 | 568.4 | 70.12 |

## 3. Expert ratios (expert median / Blade ref median; > 1 Blade faster); harness ratio [harness CI] verdict

| kernel | regime | mirror | ref | gfortran-expert | ifx-expert | block |
|---|---|---|---|---|---|---|
| pairkernel | serial-repro-native | w1 | blade-llvm | 0.929 [0.921, 0.943] suppressed | 0.859 [0.851, 0.862] suppressed | indicative |
| pairkernel | serial-repro-native | w2 | blade-llvm | 1.163 [1.159, 1.167] win | 1.079 [1.075, 1.083] detectable-win | live |
| pairkernel | serial-repro-native | w2a | blade-llvm | 0.929 [0.918, 0.934] detectable-loss | 0.865 [0.855, 0.872] loss | live |
| pairkernel | mt8-repro-native | w1 | blade-cpp | 0.673 [0.664, 0.688] loss | 0.666 [0.654, 0.687] loss | live |
| pairkernel | mt8-repro-native | w2 | blade-cpp | 0.687 [0.661, 0.712] suppressed | 0.664 [0.646, 0.687] suppressed | indicative |
| pairkernel | mt8-repro-native | w2a | blade-cpp | 0.665 [0.650, 0.689] suppressed | 0.659 [0.643, 0.675] suppressed | indicative |
| comoment3 | serial-repro-native | w1 | blade-llvm | 0.770 [0.750, 0.780] loss | 0.775 [0.752, 0.786] loss | live |
| comoment3 | serial-repro-native | w2 | blade-llvm | 1.288 [1.274, 1.314] win | 1.299 [1.275, 1.306] win | live |
| comoment3 | serial-repro-native | w2a | blade-llvm | 0.771 [0.756, 0.786] loss | 0.777 [0.764, 0.788] loss | live |
| comoment3 | mt8-repro-native | w1 | blade-cpp | 1.105 [1.069, 1.151] win | 0.933 [0.890, 0.983] detectable-loss | live |
| comoment3 | mt8-repro-native | w2 | blade-cpp | 1.097 [1.040, 1.126] suppressed | 0.925 [0.877, 0.974] suppressed | indicative |
| comoment3 | mt8-repro-native | w2a | blade-cpp | 1.102 [1.065, 1.113] win | 0.943 [0.902, 0.959] detectable-loss | live |
| comoment3 | serial-licensed-native | w1 | blade-llvm | 1.302 [1.283, 1.320] win | 1.315 [1.303, 1.325] win | live |
| comoment3 | serial-licensed-native | w2 | blade-llvm | 1.335 [1.322, 1.355] win | 1.358 [1.335, 1.374] win | live |
| comoment3 | serial-licensed-native | w2b | blade-llvm | 1.288 [1.276, 1.309] win | 1.335 [1.326, 1.350] win | live |
| comoment3 | mt8-licensed-native | w1 | blade-cpp | 1.551 [1.508, 1.660] win | 1.345 [1.275, 1.433] win | live |
| comoment3 | mt8-licensed-native | w2 | blade-cpp | 1.560 [1.485, 1.685] suppressed | 1.321 [1.268, 1.423] suppressed | indicative |
| comoment3 | mt8-licensed-native | w2b | blade-cpp | 1.529 [1.415, 1.647] suppressed | 1.293 [1.209, 1.371] suppressed | indicative |
| antisym | serial-licensed-native | w1 | blade-llvm | 1.114 [1.089, 1.126] win | 1.067 [1.046, 1.083] detectable-win | live |
| antisym | serial-licensed-native | w2 | blade-llvm | 1.109 [1.064, 1.124] win | 1.059 [1.011, 1.078] detectable-win | live |
| antisym | serial-licensed-native | w2b | blade-llvm | 1.117 [1.091, 1.175] suppressed | 1.093 [1.035, 1.176] suppressed | indicative |
| antisym | mt8-licensed-native | w1 | blade-cpp | 1.123 [1.084, 1.184] suppressed | 1.121 [1.098, 1.154] suppressed | indicative |
| antisym | mt8-licensed-native | w2 | blade-cpp | 1.107 [1.067, 1.168] suppressed | 1.145 [1.112, 1.211] suppressed | indicative |
| antisym | mt8-licensed-native | w2b | blade-cpp | 1.170 [1.143, 1.241] win | 1.206 [1.170, 1.299] win | live |
| onepass | mt8-licensed-native | w1 | blade-cpp | 0.924 [0.861, 0.970] suppressed | 0.980 [0.930, 1.037] suppressed | indicative |
| onepass | mt8-licensed-native | w2 | blade-cpp | 1.135 [1.091, 1.171] suppressed | 1.166 [1.134, 1.187] suppressed | indicative |
| onepass | mt8-licensed-native | w2b | blade-cpp | 0.938 [0.882, 0.977] suppressed | 0.987 [0.928, 1.019] suppressed | indicative |
| dot | mt8-licensed-native | w1 | blade-cpp | 0.945 [0.908, 0.983] suppressed | 0.973 [0.941, 1.020] suppressed | indicative |
| dot | mt8-licensed-native | w2 | blade-cpp | 1.053 [1.010, 1.093] detectable-win | 1.127 [1.086, 1.164] win | live |
| dot | mt8-licensed-native | w2b | blade-cpp | 0.971 [0.897, 0.990] suppressed | 1.015 [0.960, 1.032] suppressed | indicative |
| fusedchain | mt8-licensed-native | w1 | blade-cpp | 0.927 [0.898, 0.966] detectable-loss | 1.035 [0.995, 1.071] equivalent | live |
| fusedchain | mt8-licensed-native | w2 | blade-cpp | 0.997 [0.969, 1.026] equivalent | 1.151 [1.096, 1.193] win | live |
| fusedchain | mt8-licensed-native | w2b | blade-cpp | 0.902 [0.835, 0.941] loss | 0.989 [0.929, 1.083] equivalent | live |
| gram | serial-licensed-native | w1 | blade-llvm | 1.366 [1.336, 1.376] win | 1.230 [1.199, 1.239] win | live |
| gram | serial-licensed-native | w2 | blade-llvm | 1.369 [1.338, 1.383] win | 1.239 [1.228, 1.249] win | live |

## 4. Blocks: rounds, class, control medians (ms), flags, excursions dropped

| kernel | regime | mirror | rounds | class | compute ctl | memory ctl | flags | excursions |
|---|---|---|---|---|---|---|---|---|
| pairkernel | serial-repro-native | w1 | 21 | indicative | 12.939 | 5.261 | trend: control memory moved 5.5% within the block | 0 |
| pairkernel | serial-repro-native | w2 | 21 | live | 12.750 | 5.199 | - | 0 |
| pairkernel | serial-repro-native | w2a | 21 | live | 12.860 | 5.351 | - | 0 |
| pairkernel | mt8-repro-native | w1 | 21 | live | 12.650 | 5.225 | - | 0 |
| pairkernel | mt8-repro-native | w2 | 21 | indicative | 12.703 | 5.138 | trend: gfortran-natural first-half vs second-half median moved 7.8%; trend: ifx-natural first-half vs second-half median moved 7.1% | 0 |
| pairkernel | mt8-repro-native | w2a | 21 | indicative | 12.837 | 5.269 | trend: gfortran-natural first-half vs second-half median moved 8.9%; trend: ifx-natural first-half vs second-half median moved 8.9% | 0 |
| comoment3 | serial-repro-native | w1 | 21 | live | 12.819 | 5.299 | - | 0 |
| comoment3 | serial-repro-native | w2 | 21 | live | 12.811 | 5.260 | - | 0 |
| comoment3 | serial-repro-native | w2a | 21 | live | 12.824 | 5.258 | - | 0 |
| comoment3 | mt8-repro-native | w1 | 21 | live | 12.789 | 5.255 | - | 0 |
| comoment3 | mt8-repro-native | w2 | 21 | indicative | 12.864 | 5.298 | trend: ifx-natural first-half vs second-half median moved 9.8% | 0 |
| comoment3 | mt8-repro-native | w2a | 21 | live | 12.903 | 5.186 | - | 0 |
| comoment3 | serial-licensed-native | w1 | 21 | live | 12.822 | 5.355 | - | 0 |
| comoment3 | serial-licensed-native | w2 | 21 | live | 12.762 | 5.216 | - | 0 |
| comoment3 | serial-licensed-native | w2b | 21 | live | 12.845 | 5.232 | - | 0 |
| comoment3 | mt8-licensed-native | w1 | 21 | live | 12.915 | 5.327 | - | 0 |
| comoment3 | mt8-licensed-native | w2 | 21 | indicative | 12.844 | 5.319 | trend: blade-cpp first-half vs second-half median moved 5.2%; trend: ifx-natural first-half vs second-half median moved 14.6% | 0 |
| comoment3 | mt8-licensed-native | w2b | 21 | indicative | 12.788 | 5.411 | trend: blade-cpp first-half vs second-half median moved 5.4%; trend: gfortran-expert first-half vs second-half median moved 7.6% | 0 |
| antisym | serial-licensed-native | w1 | 21 | live | 12.811 | 5.233 | - | 0 |
| antisym | serial-licensed-native | w2 | 21 | live | 12.754 | 5.353 | - | 0 |
| antisym | serial-licensed-native | w2b | 21 | indicative | 12.721 | 5.220 | trend: gfortran-expert first-half vs second-half median moved 5.4% | 0 |
| antisym | mt8-licensed-native | w1 | 21 | indicative | 12.826 | 5.257 | trend: ifx-natural first-half vs second-half median moved 25.2% | 0 |
| antisym | mt8-licensed-native | w2 | 21 | indicative | 12.757 | 5.215 | trend: blade-cpp first-half vs second-half median moved 6.0%; trend: gfortran-expert first-half vs second-half median moved 6.4%; trend: ifx-natural first-half vs second-half median moved 17.1% | 0 |
| antisym | mt8-licensed-native | w2b | 21 | live | 12.722 | 5.278 | - | 0 |
| onepass | mt8-licensed-native | w1 | 21 | indicative | 12.740 | 5.184 | trend: blade-cpp first-half vs second-half median moved 9.4%; trend: gfortran-expert first-half vs second-half median moved 5.2% | 0 |
| onepass | mt8-licensed-native | w2 | 21 | indicative | 12.741 | 5.246 | trend: blade-cpp-inputonly first-half vs second-half median moved 6.7% | 0 |
| onepass | mt8-licensed-native | w2b | 21 | indicative | 12.722 | 5.216 | trend: blade-cpp-inputonly first-half vs second-half median moved 8.7% | 0 |
| dot | mt8-licensed-native | w1 | 21 | indicative | 12.745 | 5.249 | trend: blade-cpp first-half vs second-half median moved 5.3% | 0 |
| dot | mt8-licensed-native | w2 | 21 | live | 12.739 | 5.282 | - | 0 |
| dot | mt8-licensed-native | w2b | 21 | indicative | 12.806 | 5.187 | trend: ifx-expert-inputonly first-half vs second-half median moved 7.2% | 0 |
| fusedchain | mt8-licensed-native | w1 | 21 | live | 12.748 | 5.299 | - | 0 |
| fusedchain | mt8-licensed-native | w2 | 21 | live | 12.789 | 5.261 | - | 0 |
| fusedchain | mt8-licensed-native | w2b | 21 | live | 12.744 | 5.218 | - | 0 |
| gram | serial-licensed-native | w1 | 11 | live | 12.591 | 5.214 | - | 0 |
| gram | serial-licensed-native | w2 | 11 | live | 12.839 | 5.243 | - | 0 |

## 5. w2 slower than w1 beyond the CI (Blade arms; lower bound of w2/w1 > 1)

none

## 6. Missing cells

none

## 7. Sessions

| kernel | mirror | bundle | started | aborted | duration (s) | failures | flags |
|---|---|---|---|---|---|---|---|
| pairkernel | w1 | C:\bt_r2ab\w1\bench\results\2026-10-20-LAPTOP-350H6LDB\session-145512 | 2026-10-20T14:55:12 | None | 220 | none | pairkernel/serial-repro-native: inference suppressed: trend: control memory moved 5.5% within the block |
| comoment3 | w1 | C:\bt_r2ab\w1\bench\results\2026-10-20-LAPTOP-350H6LDB\session-150642 | 2026-10-20T15:06:42 | None | 249 | none | none |
| comoment3 | w1 | C:\bt_r2ab\w1\bench\results\2026-10-20-LAPTOP-350H6LDB\session-151946 | 2026-10-20T15:19:46 | None | 250 | none | none |
| antisym | w1 | C:\bt_r2ab\w1\bench\results\2026-10-20-LAPTOP-350H6LDB\session-153251 | 2026-10-20T15:32:51 | None | 254 | none | antisym/mt8-licensed-native: inference suppressed: trend: ifx-natural first-half vs second-half median moved 25.2% |
| onepass | w1 | C:\bt_r2ab\w1\bench\results\2026-10-20-LAPTOP-350H6LDB\session-154556 | 2026-10-20T15:45:56 | None | 212 | none | onepass/mt8-licensed-native: inference suppressed: trend: blade-cpp first-half vs second-half median moved 9.4%; trend: gfortran-expert first-half vs second-half median moved 5.2% |
| dot | w1 | C:\bt_r2ab\w1\bench\results\2026-10-20-LAPTOP-350H6LDB\session-155703 | 2026-10-20T15:57:03 | None | 213 | none | dot/mt8-licensed-native: inference suppressed: trend: blade-cpp first-half vs second-half median moved 5.3% |
| fusedchain | w1 | C:\bt_r2ab\w1\bench\results\2026-10-20-LAPTOP-350H6LDB\session-160813 | 2026-10-20T16:08:13 | None | 211 | none | none |
| gram | w1 | C:\bt_r2ab\w1\bench\results\2026-10-20-LAPTOP-350H6LDB\session-161919 | 2026-10-20T16:19:19 | None | 90 | none | none |
| pairkernel | w2 | C:\bt_r2ab\w2\bench\results\2026-10-20-LAPTOP-350H6LDB\session-145903 | 2026-10-20T14:59:03 | None | 218 | none | pairkernel/mt8-repro-native: inference suppressed: trend: gfortran-natural first-half vs second-half median moved 7.8%; trend: ifx-natural first-half vs second-half median moved 7.1% |
| comoment3 | w2 | C:\bt_r2ab\w2\bench\results\2026-10-20-LAPTOP-350H6LDB\session-151101 | 2026-10-20T15:11:01 | None | 252 | none | comoment3/mt8-repro-native: inference suppressed: trend: ifx-natural first-half vs second-half median moved 9.8% |
| comoment3 | w2 | C:\bt_r2ab\w2\bench\results\2026-10-20-LAPTOP-350H6LDB\session-152406 | 2026-10-20T15:24:06 | None | 252 | none | comoment3/mt8-licensed-native: inference suppressed: trend: blade-cpp first-half vs second-half median moved 5.2%; trend: ifx-natural first-half vs second-half median moved 14.6% |
| antisym | w2 | C:\bt_r2ab\w2\bench\results\2026-10-20-LAPTOP-350H6LDB\session-153716 | 2026-10-20T15:37:16 | None | 249 | none | antisym/mt8-licensed-native: inference suppressed: trend: blade-cpp first-half vs second-half median moved 6.0%; trend: gfortran-expert first-half vs second-half median moved 6.4%; trend: ifx-natural first-half vs second-half median moved 17.1% |
| onepass | w2 | C:\bt_r2ab\w2\bench\results\2026-10-20-LAPTOP-350H6LDB\session-154938 | 2026-10-20T15:49:38 | None | 212 | none | onepass/mt8-licensed-native: inference suppressed: trend: blade-cpp-inputonly first-half vs second-half median moved 6.7% |
| dot | w2 | C:\bt_r2ab\w2\bench\results\2026-10-20-LAPTOP-350H6LDB\session-160046 | 2026-10-20T16:00:46 | None | 213 | none | none |
| fusedchain | w2 | C:\bt_r2ab\w2\bench\results\2026-10-20-LAPTOP-350H6LDB\session-161154 | 2026-10-20T16:11:54 | None | 212 | none | none |
| gram | w2 | C:\bt_r2ab\w2\bench\results\2026-10-20-LAPTOP-350H6LDB\session-162059 | 2026-10-20T16:20:59 | None | 90 | none | none |
| pairkernel | w2a | C:\bt_r2ab\w2a\bench\results\2026-10-20-LAPTOP-350H6LDB\session-150252 | 2026-10-20T15:02:52 | None | 219 | none | pairkernel/mt8-repro-native: inference suppressed: trend: gfortran-natural first-half vs second-half median moved 8.9%; trend: ifx-natural first-half vs second-half median moved 8.9% |
| comoment3 | w2a | C:\bt_r2ab\w2a\bench\results\2026-10-20-LAPTOP-350H6LDB\session-151524 | 2026-10-20T15:15:24 | None | 252 | none | none |
| comoment3 | w2b | C:\bt_r2ab\w2b\bench\results\2026-10-20-LAPTOP-350H6LDB\session-152828 | 2026-10-20T15:28:28 | None | 252 | none | comoment3/mt8-licensed-native: inference suppressed: trend: blade-cpp first-half vs second-half median moved 5.4%; trend: gfortran-expert first-half vs second-half median moved 7.6% |
| antisym | w2b | C:\bt_r2ab\w2b\bench\results\2026-10-20-LAPTOP-350H6LDB\session-154136 | 2026-10-20T15:41:36 | None | 249 | none | antisym/serial-licensed-native: inference suppressed: trend: gfortran-expert first-half vs second-half median moved 5.4% |
| onepass | w2b | C:\bt_r2ab\w2b\bench\results\2026-10-20-LAPTOP-350H6LDB\session-155321 | 2026-10-20T15:53:21 | None | 212 | none | onepass/mt8-licensed-native: inference suppressed: trend: blade-cpp-inputonly first-half vs second-half median moved 8.7% |
| dot | w2b | C:\bt_r2ab\w2b\bench\results\2026-10-20-LAPTOP-350H6LDB\session-160430 | 2026-10-20T16:04:30 | None | 213 | none | dot/mt8-licensed-native: inference suppressed: trend: ifx-expert-inputonly first-half vs second-half median moved 7.2% |
| fusedchain | w2b | C:\bt_r2ab\w2b\bench\results\2026-10-20-LAPTOP-350H6LDB\session-161537 | 2026-10-20T16:15:37 | None | 212 | none | none |