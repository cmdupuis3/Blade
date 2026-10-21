# Round-3 wave-1 harness A/B: r3old (master 66e04875) vs r3new (wave-1 tip 9da403a8) vs knob arms

Protocol: 23 `bench` sessions 03:58-06:37 (2026-10-21), one at a time, interleaved per cell (old, new, knob arm), harness defaults
(21 rounds; gram / tensorop serial 11), full size, quiet box (process audit clean before the driver; the chain ran unattended after
the five gates and the smokes), AC. Mirrors `C:\bt_r2ab\r3*`: r3old = the master 66e04875 binary (built in C:\bt_r2emit, Blade.dll
CF1E0B48) + the round-3 bench tree (so the new kernels run on both); r3new = the wave-1 tip build (Blade.dll E84BB4E5), default
config; r3noslots = + `BLADE_FOLD_ROW_SLOTS=0` (the ordered orbit scatter serves mt8 repro instead of the G35 row slots);
r3gather = + `BLADE_COMPACTION_FUSION=gather` (T4a on top of the default T4b); r3antig0 = + `BLADE_TILE_TUNE=anti_g=0`. Every
mirror smoked for its benched pairs (0 failures). Gates on 9da403a8 before the A/B: default 7389/0, opt-diff 2173/0, docs 131/0,
llvm 841/0, interp 3077/0. Zero failures / aborts; exit 3 = drift flags only (blocks `indicative`). Ratios: B/A = median(B) /
median(A) of the Blade reference lane (blade-llvm serial, blade-cpp mt8), analyst unpaired bootstrap CI (4000 resamples; NOT a
harness output); expert ratio = expert median / Blade ref median (> 1 Blade faster), harness CI and verdict. antisym ran three
sessions per mirror; its samples are pooled (n = 63).

## 0. Headline

| cell | item | old gf / ifx | new gf / ifx | new / old (Blade ref) | knob arm / new | class |
|---|---|---|---|---|---|---|
| stencil mt8 repro | A2 row-view fold in the G35 row slots (G51) | 0.805 / 0.877 (loss) | **0.924 / 0.959** | **0.876** [0.858, 0.935] cpp | -- | ind (new) |
| stencil serial repro / licensed / mt8 licensed | A2 (controls; licensed helper arm) | 1.392 / 1.111; 1.351 / 1.042; 0.886 / 0.939 | 1.385 / 1.108; 1.352 / 1.058; 0.873 / 0.947 | 1.001; 0.998; 1.003 | -- | live / ind |
| maskagg serial licensed | T4b positions elided (default) | 1.064 / 0.777 | **1.290 / 0.970** | **0.817** [0.779, 0.839] llvm; 0.772 cpp | r3gather 0.984 [0.955, 1.017] | ind |
| maskagg serial repro | T4b | 1.073 / 0.827 | **1.260 / 0.955** | **0.866** [0.849, 0.876] llvm; 0.856 cpp | r3gather 0.974 [0.961, 0.988] | live |
| pairkernel mt8 repro | A1: default = G35 row slots; noslots = ordered scatter | 0.684 / 0.680 | 0.686 / 0.653 | 1.012 [0.965, 1.038] | **r3noslots 0.841** [0.811, 0.872] -> expert 0.795 / 0.805 | ind |
| comoment3 mt8 repro | A1 | 1.127 / 0.965 | 1.107 / 0.945 | 1.027 [0.986, 1.052] | **r3noslots 0.843** [0.817, 0.885] -> expert 1.303 / 1.120 | ind |
| pairkernel / comoment3 serial repro | A1 controls (all three mirrors must agree) | 1.157 / 1.074; 1.281 / 1.302 | 1.160 / 1.079; 1.292 / 1.291 | 0.997-1.004; noslots 0.999-1.008 | -- | live |
| antisym serial licensed | anti_g 1 (default) vs auto, 3 sessions each | -- | 1.110 / 1.063 (session 1) | -- | r3antig0 / new **1.010** [0.994, 1.016] llvm, **1.020** [1.009, 1.029] cpp (auto slower) | ind (pooled) |
| antisym mt8 licensed | anti_g 1 vs auto | -- | 1.109 / 1.124 (session 1) | -- | r3antig0 / new **0.952** [0.929, 0.987] cpp (auto faster) | ind (pooled) |
| twofir serial licensed / repro | new kernel N07 (resident two-FIR / sibling-map fusion) | 1.192 / 0.955; 1.126 / 0.933 | 1.178 / 0.964; 1.132 / 0.941 | 1.006; 0.995 | -- | live |
| twofir mt8 licensed / repro | N07 | 0.701 / 0.735; 0.576 / 0.600 | 0.737 / 0.752; 0.587 / 0.622 | 0.985; 0.986 | -- | ind / live |
| tensorop serial licensed / repro / fastmath | new kernel N09 (mode_product baseline: serial nests) | 0.190 / 0.136; 0.178 / 0.132; 0.187 / 0.247 | 0.194 / 0.139; 0.183 / 0.137; 0.186 / 0.242 | 0.989; 0.983; 1.013 | -- | live / ind / live |
| tensorop mt8 (all three regimes) | N09 (kernel serial in every regime) | 0.049 / 0.048; 0.049 / 0.047; 0.049 / 0.056 | 0.050 / 0.045; 0.049 / 0.047; 0.047 / 0.059 | 1.002; 1.002; 1.002 | -- | ind |
| gram serial licensed | control | 1.372 / 1.242 | 1.368 / 1.231 | 1.004 / 1.012 | -- | ind / live |

Section 5 (new slower than old beyond the CI): pairkernel serial repro blade-cpp 29.48 -> 29.59 ms, 1.004 [1.001, 1.008], live --
a 0.4% shift on a program wave 1 does not touch (its emission is unchanged by construction: no compound, no row-view fold, no
hidden side-table writer); recorded as noise at the CI edge. No other Blade arm is slower.

Decisions the measurement settles:
- **A1: flip the precedence.** Under mt8 repro the ordered orbit scatter beats the G35 row slots on both stored-pool walks
  (pairkernel 0.841, comoment3 0.843; the serial controls agree to within 0.8% across all three mirrors, so both strict forms
  print the same bits). Wave 2 item W2-0: the scatter wins the precedence when it is eligible; the row slots stay the form for
  walks the scatter does not admit; `blade plan` records the choice; the omp-reduce `row_slots_precede_orbit_scatter` case inverts.
- **T4a stays opt-in.** Its gain on top of T4b is 1.5-2.6% (repro CI excludes 1; licensed within noise), below the brief's drop line.
- **anti_g: default 1 serially, auto under threads.** Serial: auto is 1-2% slower (cpp CI excludes 1, pooled); mt8: auto is 4.8%
  faster (CI excludes 1, pooled, blocks indicative). Wave 2 item W2-0b: a per-regime default (anti_g = 0 when the build emits
  threads, 1 otherwise), recorded in `blade plan`; `BLADE_TILE_TUNE anti_g=<G>` still forces either.
- **The new kernels' baselines** are now measured: twofir loses under threads (0.58-0.75: the two FIR passes and their checksums are
  separate passes -- gap G56, the halo cell epilogue / sibling-map fusion is the round-3 B-adjacent target); tensorop loses 5-20x
  (the mode_product nests are serial in every regime and un-tiled: the strided-contraction recognizer onto the dgemm tile is the
  wave-2/3 item the brief named).


Mirrors: r3old = master 66e04875 binary + round-3 bench; r3new = wave-1 tip, defaults; r3noslots = BLADE_FOLD_ROW_SLOTS=0 (A1: scatter under mt8 repro); r3gather = BLADE_COMPACTION_FUSION=gather (T4a opt-in); r3antig0 = BLADE_TILE_TUNE=anti_g=0

## 1. Pairwise medians: B/A < 1 = B faster (samples pooled across sessions of a mirror). CI = analyst unpaired bootstrap.

| kernel | regime | item | B/A | arm | A -> B median (ms) | B/A [95% CI] | class n_A/n_B |
|---|---|---|---|---|---|---|---|
| stencil | serial-repro-native | A2 row-view fold | r3new/r3old | blade-llvm | 11.86 -> 11.87 | 1.001 [0.989, 1.020] | live 21/21 |
| stencil | serial-repro-native | A2 row-view fold | r3new/r3old | blade-cpp | 15.36 -> 15.3 | 0.996 [0.987, 1.005] | live 21/21 |
| stencil | serial-repro-native | A2 row-view fold | r3new/r3old | gfortran-natural | 16.87 -> 16.81 | 0.996 [0.993, 1.007] | live 21/21 |
| stencil | serial-repro-native | A2 row-view fold | r3new/r3old | gfortran-expert | 16.5 -> 16.44 | 0.996 [0.990, 1.007] | live 21/21 |
| stencil | serial-repro-native | A2 row-view fold | r3new/r3old | ifx-natural | 18.26 -> 18.31 | 1.002 [0.998, 1.013] | live 21/21 |
| stencil | serial-repro-native | A2 row-view fold | r3new/r3old | ifx-expert | 13.17 -> 13.16 | 0.999 [0.983, 1.007] | live 21/21 |
| stencil | mt8-repro-native | A2 row-view fold (G51 target) | r3new/r3old | blade-llvm | -- | -- | (arm not ok in a block) |
| stencil | mt8-repro-native | A2 row-view fold (G51 target) | r3new/r3old | blade-cpp | 9.684 -> 8.48 | 0.876 [0.858, 0.935] | ind 21/21 |
| stencil | mt8-repro-native | A2 row-view fold (G51 target) | r3new/r3old | gfortran-natural | 8.733 -> 8.602 | 0.985 [0.964, 1.014] | ind 21/21 |
| stencil | mt8-repro-native | A2 row-view fold (G51 target) | r3new/r3old | gfortran-expert | 7.796 -> 7.835 | 1.005 [0.988, 1.037] | ind 21/21 |
| stencil | mt8-repro-native | A2 row-view fold (G51 target) | r3new/r3old | ifx-natural | 9.976 -> 9.762 | 0.979 [0.960, 1.007] | ind 21/21 |
| stencil | mt8-repro-native | A2 row-view fold (G51 target) | r3new/r3old | ifx-expert | 8.488 -> 8.13 | 0.958 [0.940, 1.003] | ind 21/21 |
| stencil | serial-licensed-native | A2 licensed helper arm | r3new/r3old | blade-llvm | 10.31 -> 10.29 | 0.998 [0.963, 1.023] | ind 21/21 |
| stencil | serial-licensed-native | A2 licensed helper arm | r3new/r3old | blade-cpp | 13.99 -> 13.94 | 0.996 [0.988, 1.014] | ind 21/21 |
| stencil | serial-licensed-native | A2 licensed helper arm | r3new/r3old | gfortran-natural | 16.88 -> 16.8 | 0.995 [0.980, 1.005] | ind 21/21 |
| stencil | serial-licensed-native | A2 licensed helper arm | r3new/r3old | gfortran-expert | 13.93 -> 13.91 | 0.998 [0.982, 1.017] | ind 21/21 |
| stencil | serial-licensed-native | A2 licensed helper arm | r3new/r3old | ifx-natural | 18.4 -> 18.28 | 0.993 [0.985, 1.001] | ind 21/21 |
| stencil | serial-licensed-native | A2 licensed helper arm | r3new/r3old | ifx-expert | 10.74 -> 10.89 | 1.014 [0.992, 1.031] | ind 21/21 |
| stencil | mt8-licensed-native | A2 licensed helper arm (mt8) | r3new/r3old | blade-llvm | -- | -- | (arm not ok in a block) |
| stencil | mt8-licensed-native | A2 licensed helper arm (mt8) | r3new/r3old | blade-cpp | 8.838 -> 8.862 | 1.003 [0.976, 1.050] | ind 21/21 |
| stencil | mt8-licensed-native | A2 licensed helper arm (mt8) | r3new/r3old | gfortran-natural | 8.804 -> 8.587 | 0.975 [0.965, 1.020] | ind 21/21 |
| stencil | mt8-licensed-native | A2 licensed helper arm (mt8) | r3new/r3old | gfortran-expert | 7.832 -> 7.734 | 0.987 [0.971, 1.010] | ind 21/21 |
| stencil | mt8-licensed-native | A2 licensed helper arm (mt8) | r3new/r3old | ifx-natural | 9.859 -> 9.978 | 1.012 [0.978, 1.042] | ind 21/21 |
| stencil | mt8-licensed-native | A2 licensed helper arm (mt8) | r3new/r3old | ifx-expert | 8.303 -> 8.397 | 1.011 [0.979, 1.047] | ind 21/21 |
| maskagg | serial-licensed-native | T4b default; T4a opt-in | r3new/r3old | blade-llvm | 3.767 -> 3.076 | 0.817 [0.779, 0.839] | ind 21/21 |
| maskagg | serial-licensed-native | T4b default; T4a opt-in | r3new/r3old | blade-cpp | 5.382 -> 4.156 | 0.772 [0.733, 0.818] | ind 21/21 |
| maskagg | serial-licensed-native | T4b default; T4a opt-in | r3new/r3old | gfortran-natural | 8.219 -> 8.303 | 1.010 [0.992, 1.029] | ind 21/21 |
| maskagg | serial-licensed-native | T4b default; T4a opt-in | r3new/r3old | gfortran-expert | 4.008 -> 3.967 | 0.990 [0.946, 1.013] | ind 21/21 |
| maskagg | serial-licensed-native | T4b default; T4a opt-in | r3new/r3old | ifx-natural | 10.71 -> 10.67 | 0.996 [0.986, 1.018] | ind 21/21 |
| maskagg | serial-licensed-native | T4b default; T4a opt-in | r3new/r3old | ifx-expert | 2.925 -> 2.984 | 1.020 [0.994, 1.110] | ind 21/21 |
| maskagg | serial-licensed-native | T4b default; T4a opt-in | r3gather/r3new | blade-llvm | 3.076 -> 3.028 | 0.984 [0.955, 1.017] | ind 21/21 |
| maskagg | serial-licensed-native | T4b default; T4a opt-in | r3gather/r3new | blade-cpp | 4.156 -> 4.202 | 1.011 [0.986, 1.059] | ind 21/21 |
| maskagg | serial-licensed-native | T4b default; T4a opt-in | r3gather/r3new | gfortran-natural | 8.303 -> 8.197 | 0.987 [0.970, 1.006] | ind 21/21 |
| maskagg | serial-licensed-native | T4b default; T4a opt-in | r3gather/r3new | gfortran-expert | 3.967 -> 4.002 | 1.009 [0.982, 1.037] | ind 21/21 |
| maskagg | serial-licensed-native | T4b default; T4a opt-in | r3gather/r3new | ifx-natural | 10.67 -> 10.59 | 0.993 [0.980, 1.009] | ind 21/21 |
| maskagg | serial-licensed-native | T4b default; T4a opt-in | r3gather/r3new | ifx-expert | 2.984 -> 2.915 | 0.977 [0.897, 1.013] | ind 21/21 |
| maskagg | serial-repro-native | T4b default; T4a opt-in | r3new/r3old | blade-llvm | 4.462 -> 3.862 | 0.866 [0.849, 0.876] | live 21/21 |
| maskagg | serial-repro-native | T4b default; T4a opt-in | r3new/r3old | blade-cpp | 5.74 -> 4.913 | 0.856 [0.806, 0.872] | live 21/21 |
| maskagg | serial-repro-native | T4b default; T4a opt-in | r3new/r3old | gfortran-natural | 8.257 -> 8.207 | 0.994 [0.981, 1.013] | live 21/21 |
| maskagg | serial-repro-native | T4b default; T4a opt-in | r3new/r3old | gfortran-expert | 4.788 -> 4.868 | 1.017 [0.998, 1.036] | live 21/21 |
| maskagg | serial-repro-native | T4b default; T4a opt-in | r3new/r3old | ifx-natural | 10.53 -> 10.56 | 1.003 [0.992, 1.020] | live 21/21 |
| maskagg | serial-repro-native | T4b default; T4a opt-in | r3new/r3old | ifx-expert | 3.69 -> 3.688 | 0.999 [0.986, 1.016] | live 21/21 |
| maskagg | serial-repro-native | T4b default; T4a opt-in | r3gather/r3new | blade-llvm | 3.862 -> 3.763 | 0.974 [0.961, 0.988] | live 21/21 |
| maskagg | serial-repro-native | T4b default; T4a opt-in | r3gather/r3new | blade-cpp | 4.913 -> 4.841 | 0.985 [0.969, 1.011] | live 21/21 |
| maskagg | serial-repro-native | T4b default; T4a opt-in | r3gather/r3new | gfortran-natural | 8.207 -> 8.272 | 1.008 [0.985, 1.019] | live 21/21 |
| maskagg | serial-repro-native | T4b default; T4a opt-in | r3gather/r3new | gfortran-expert | 4.868 -> 4.789 | 0.984 [0.962, 1.002] | live 21/21 |
| maskagg | serial-repro-native | T4b default; T4a opt-in | r3gather/r3new | ifx-natural | 10.56 -> 10.58 | 1.002 [0.981, 1.013] | live 21/21 |
| maskagg | serial-repro-native | T4b default; T4a opt-in | r3gather/r3new | ifx-expert | 3.688 -> 3.7 | 1.003 [0.990, 1.016] | live 21/21 |
| pairkernel | mt8-repro-native | A1: row slots (default) vs scatter (noslots) | r3new/r3old | blade-llvm | -- | -- | (arm not ok in a block) |
| pairkernel | mt8-repro-native | A1: row slots (default) vs scatter (noslots) | r3new/r3old | blade-cpp | 9.115 -> 9.228 | 1.012 [0.965, 1.038] | ind 21/21 |
| pairkernel | mt8-repro-native | A1: row slots (default) vs scatter (noslots) | r3new/r3old | gfortran-natural | 13.79 -> 14.76 | 1.071 [0.993, 1.148] | ind 21/21 |
| pairkernel | mt8-repro-native | A1: row slots (default) vs scatter (noslots) | r3new/r3old | gfortran-expert | 6.237 -> 6.333 | 1.015 [0.954, 1.075] | ind 21/21 |
| pairkernel | mt8-repro-native | A1: row slots (default) vs scatter (noslots) | r3new/r3old | ifx-natural | 15.51 -> 16.4 | 1.058 [0.923, 1.102] | ind 21/21 |
| pairkernel | mt8-repro-native | A1: row slots (default) vs scatter (noslots) | r3new/r3old | ifx-expert | 6.196 -> 6.023 | 0.972 [0.948, 1.046] | ind 21/21 |
| pairkernel | mt8-repro-native | A1: row slots (default) vs scatter (noslots) | r3noslots/r3new | blade-llvm | -- | -- | (arm not ok in a block) |
| pairkernel | mt8-repro-native | A1: row slots (default) vs scatter (noslots) | r3noslots/r3new | blade-cpp | 9.228 -> 7.757 | 0.841 [0.811, 0.872] | ind 21/21 |
| pairkernel | mt8-repro-native | A1: row slots (default) vs scatter (noslots) | r3noslots/r3new | gfortran-natural | 14.76 -> 13.65 | 0.924 [0.861, 0.997] | ind 21/21 |
| pairkernel | mt8-repro-native | A1: row slots (default) vs scatter (noslots) | r3noslots/r3new | gfortran-expert | 6.333 -> 6.167 | 0.974 [0.939, 1.028] | ind 21/21 |
| pairkernel | mt8-repro-native | A1: row slots (default) vs scatter (noslots) | r3noslots/r3new | ifx-natural | 16.4 -> 15.47 | 0.943 [0.903, 1.060] | ind 21/21 |
| pairkernel | mt8-repro-native | A1: row slots (default) vs scatter (noslots) | r3noslots/r3new | ifx-expert | 6.023 -> 6.242 | 1.036 [0.953, 1.051] | ind 21/21 |
| pairkernel | serial-repro-native | A1 control (must agree) | r3new/r3old | blade-llvm | 29.6 -> 29.52 | 0.997 [0.988, 1.004] | live 21/21 |
| pairkernel | serial-repro-native | A1 control (must agree) | r3new/r3old | blade-cpp | 29.48 -> 29.59 | 1.004 [1.001, 1.008] | live 21/21 |
| pairkernel | serial-repro-native | A1 control (must agree) | r3new/r3old | gfortran-natural | 60.3 -> 60.4 | 1.002 [0.999, 1.005] | live 21/21 |
| pairkernel | serial-repro-native | A1 control (must agree) | r3new/r3old | gfortran-expert | 34.24 -> 34.24 | 1.000 [0.999, 1.003] | live 21/21 |
| pairkernel | serial-repro-native | A1 control (must agree) | r3new/r3old | ifx-natural | 63.92 -> 63.78 | 0.998 [0.993, 1.003] | live 21/21 |
| pairkernel | serial-repro-native | A1 control (must agree) | r3new/r3old | ifx-expert | 31.8 -> 31.85 | 1.002 [0.993, 1.006] | live 21/21 |
| pairkernel | serial-repro-native | A1 control (must agree) | r3noslots/r3new | blade-llvm | 29.52 -> 29.71 | 1.006 [0.999, 1.012] | live 21/21 |
| pairkernel | serial-repro-native | A1 control (must agree) | r3noslots/r3new | blade-cpp | 29.59 -> 29.55 | 0.999 [0.994, 1.003] | live 21/21 |
| pairkernel | serial-repro-native | A1 control (must agree) | r3noslots/r3new | gfortran-natural | 60.4 -> 60.32 | 0.999 [0.995, 1.002] | live 21/21 |
| pairkernel | serial-repro-native | A1 control (must agree) | r3noslots/r3new | gfortran-expert | 34.24 -> 34.42 | 1.005 [1.002, 1.010] | live 21/21 |
| pairkernel | serial-repro-native | A1 control (must agree) | r3noslots/r3new | ifx-natural | 63.78 -> 64.13 | 1.006 [1.001, 1.010] | live 21/21 |
| pairkernel | serial-repro-native | A1 control (must agree) | r3noslots/r3new | ifx-expert | 31.85 -> 31.78 | 0.998 [0.992, 1.004] | live 21/21 |
| comoment3 | mt8-repro-native | A1: row slots vs scatter | r3new/r3old | blade-llvm | -- | -- | (arm not ok in a block) |
| comoment3 | mt8-repro-native | A1: row slots vs scatter | r3new/r3old | blade-cpp | 4.315 -> 4.432 | 1.027 [0.986, 1.052] | ind 21/21 |
| comoment3 | mt8-repro-native | A1: row slots vs scatter | r3new/r3old | gfortran-natural | 92.36 -> 90.55 | 0.980 [0.946, 1.008] | ind 21/21 |
| comoment3 | mt8-repro-native | A1: row slots vs scatter | r3new/r3old | gfortran-expert | 4.861 -> 4.908 | 1.010 [0.973, 1.047] | ind 21/21 |
| comoment3 | mt8-repro-native | A1: row slots vs scatter | r3new/r3old | ifx-natural | 108.3 -> 110.7 | 1.022 [0.916, 1.082] | ind 21/21 |
| comoment3 | mt8-repro-native | A1: row slots vs scatter | r3new/r3old | ifx-expert | 4.166 -> 4.188 | 1.005 [0.981, 1.042] | ind 21/21 |
| comoment3 | mt8-repro-native | A1: row slots vs scatter | r3noslots/r3new | blade-llvm | -- | -- | (arm not ok in a block) |
| comoment3 | mt8-repro-native | A1: row slots vs scatter | r3noslots/r3new | blade-cpp | 4.432 -> 3.736 | 0.843 [0.817, 0.885] | ind 21/21 |
| comoment3 | mt8-repro-native | A1: row slots vs scatter | r3noslots/r3new | gfortran-natural | 90.55 -> 85.65 | 0.946 [0.919, 1.010] | ind 21/21 |
| comoment3 | mt8-repro-native | A1: row slots vs scatter | r3noslots/r3new | gfortran-expert | 4.908 -> 4.866 | 0.992 [0.957, 1.033] | ind 21/21 |
| comoment3 | mt8-repro-native | A1: row slots vs scatter | r3noslots/r3new | ifx-natural | 110.7 -> 113 | 1.021 [0.968, 1.114] | ind 21/21 |
| comoment3 | mt8-repro-native | A1: row slots vs scatter | r3noslots/r3new | ifx-expert | 4.188 -> 4.184 | 0.999 [0.956, 1.028] | ind 21/21 |
| comoment3 | serial-repro-native | A1 control | r3new/r3old | blade-llvm | 8.909 -> 8.895 | 0.998 [0.992, 1.013] | live 21/21 |
| comoment3 | serial-repro-native | A1 control | r3new/r3old | blade-cpp | 8.934 -> 8.951 | 1.002 [0.997, 1.009] | live 21/21 |
| comoment3 | serial-repro-native | A1 control | r3new/r3old | gfortran-natural | 600.1 -> 600.2 | 1.000 [0.999, 1.000] | live 21/21 |
| comoment3 | serial-repro-native | A1 control | r3new/r3old | gfortran-expert | 11.41 -> 11.49 | 1.007 [0.997, 1.020] | live 21/21 |
| comoment3 | serial-repro-native | A1 control | r3new/r3old | ifx-natural | 719.1 -> 718 | 0.998 [0.995, 1.000] | live 21/21 |
| comoment3 | serial-repro-native | A1 control | r3new/r3old | ifx-expert | 11.6 -> 11.48 | 0.990 [0.982, 1.003] | live 21/21 |
| comoment3 | serial-repro-native | A1 control | r3noslots/r3new | blade-llvm | 8.895 -> 8.912 | 1.002 [0.984, 1.012] | live 21/21 |
| comoment3 | serial-repro-native | A1 control | r3noslots/r3new | blade-cpp | 8.951 -> 9.019 | 1.008 [0.997, 1.010] | live 21/21 |
| comoment3 | serial-repro-native | A1 control | r3noslots/r3new | gfortran-natural | 600.2 -> 600.1 | 1.000 [0.999, 1.001] | live 21/21 |
| comoment3 | serial-repro-native | A1 control | r3noslots/r3new | gfortran-expert | 11.49 -> 11.44 | 0.995 [0.983, 1.004] | live 21/21 |
| comoment3 | serial-repro-native | A1 control | r3noslots/r3new | ifx-natural | 718 -> 718.3 | 1.000 [0.999, 1.004] | live 21/21 |
| comoment3 | serial-repro-native | A1 control | r3noslots/r3new | ifx-expert | 11.48 -> 11.57 | 1.008 [0.998, 1.021] | live 21/21 |
| antisym | serial-licensed-native | anti_g 1 vs auto (3 sessions each, pooled) | r3antig0/r3new | blade-llvm | 28.06 -> 28.34 | 1.010 [0.994, 1.016] | ind 63/63 |
| antisym | serial-licensed-native | anti_g 1 vs auto (3 sessions each, pooled) | r3antig0/r3new | blade-cpp | 28.78 -> 29.35 | 1.020 [1.009, 1.029] | ind 63/63 |
| antisym | serial-licensed-native | anti_g 1 vs auto (3 sessions each, pooled) | r3antig0/r3new | gfortran-natural | 773.5 -> 772.1 | 0.998 [0.997, 1.000] | ind 63/63 |
| antisym | serial-licensed-native | anti_g 1 vs auto (3 sessions each, pooled) | r3antig0/r3new | gfortran-expert | 31.07 -> 30.95 | 0.996 [0.981, 1.011] | ind 63/63 |
| antisym | serial-licensed-native | anti_g 1 vs auto (3 sessions each, pooled) | r3antig0/r3new | ifx-natural | 333.1 -> 332.1 | 0.997 [0.993, 1.002] | ind 63/63 |
| antisym | serial-licensed-native | anti_g 1 vs auto (3 sessions each, pooled) | r3antig0/r3new | ifx-expert | 29.67 -> 29.31 | 0.988 [0.977, 1.008] | ind 63/63 |
| antisym | mt8-licensed-native | anti_g 1 vs auto (mt8, pooled) | r3antig0/r3new | blade-llvm | -- | -- | (arm not ok in a block) |
| antisym | mt8-licensed-native | anti_g 1 vs auto (mt8, pooled) | r3antig0/r3new | blade-cpp | 8.619 -> 8.209 | 0.952 [0.929, 0.987] | ind 63/63 |
| antisym | mt8-licensed-native | anti_g 1 vs auto (mt8, pooled) | r3antig0/r3new | gfortran-natural | 106.9 -> 106.5 | 0.996 [0.989, 1.010] | ind 63/63 |
| antisym | mt8-licensed-native | anti_g 1 vs auto (mt8, pooled) | r3antig0/r3new | gfortran-expert | 9.474 -> 9.464 | 0.999 [0.977, 1.027] | ind 63/63 |
| antisym | mt8-licensed-native | anti_g 1 vs auto (mt8, pooled) | r3antig0/r3new | ifx-natural | 65.19 -> 64.36 | 0.987 [0.923, 1.065] | ind 63/63 |
| antisym | mt8-licensed-native | anti_g 1 vs auto (mt8, pooled) | r3antig0/r3new | ifx-expert | 9.796 -> 9.77 | 0.997 [0.967, 1.025] | ind 63/63 |
| twofir | serial-licensed-native | new kernel N07 | r3new/r3old | blade-llvm | 9.748 -> 9.806 | 1.006 [0.988, 1.023] | live 21/21 |
| twofir | serial-licensed-native | new kernel N07 | r3new/r3old | blade-cpp | 14.27 -> 14.33 | 1.004 [0.984, 1.016] | live 21/21 |
| twofir | serial-licensed-native | new kernel N07 | r3new/r3old | gfortran-natural | 15.98 -> 15.9 | 0.995 [0.984, 1.004] | live 21/21 |
| twofir | serial-licensed-native | new kernel N07 | r3new/r3old | gfortran-expert | 11.62 -> 11.56 | 0.995 [0.972, 1.010] | live 21/21 |
| twofir | serial-licensed-native | new kernel N07 | r3new/r3old | ifx-natural | 13.99 -> 13.89 | 0.993 [0.980, 1.003] | live 21/21 |
| twofir | serial-licensed-native | new kernel N07 | r3new/r3old | ifx-expert | 9.314 -> 9.45 | 1.015 [0.993, 1.030] | live 21/21 |
| twofir | mt8-licensed-native | new kernel N07 | r3new/r3old | blade-llvm | -- | -- | (arm not ok in a block) |
| twofir | mt8-licensed-native | new kernel N07 | r3new/r3old | blade-cpp | 10.41 -> 10.25 | 0.985 [0.958, 1.027] | ind 21/21 |
| twofir | mt8-licensed-native | new kernel N07 | r3new/r3old | gfortran-natural | 9.006 -> 9.099 | 1.010 [0.976, 1.033] | ind 21/21 |
| twofir | mt8-licensed-native | new kernel N07 | r3new/r3old | gfortran-expert | 7.298 -> 7.56 | 1.036 [0.974, 1.070] | ind 21/21 |
| twofir | mt8-licensed-native | new kernel N07 | r3new/r3old | ifx-natural | 9.493 -> 9.285 | 0.978 [0.953, 0.997] | ind 21/21 |
| twofir | mt8-licensed-native | new kernel N07 | r3new/r3old | ifx-expert | 7.653 -> 7.714 | 1.008 [0.978, 1.053] | ind 21/21 |
| twofir | serial-repro-native | new kernel N07 | r3new/r3old | blade-llvm | 11.15 -> 11.1 | 0.995 [0.970, 1.020] | live 21/21 |
| twofir | serial-repro-native | new kernel N07 | r3new/r3old | blade-cpp | 16.48 -> 16.65 | 1.010 [0.999, 1.021] | live 21/21 |
| twofir | serial-repro-native | new kernel N07 | r3new/r3old | gfortran-natural | 15.85 -> 15.98 | 1.008 [0.987, 1.018] | live 21/21 |
| twofir | serial-repro-native | new kernel N07 | r3new/r3old | gfortran-expert | 12.55 -> 12.56 | 1.001 [0.972, 1.016] | live 21/21 |
| twofir | serial-repro-native | new kernel N07 | r3new/r3old | ifx-natural | 14.03 -> 13.79 | 0.983 [0.971, 0.999] | live 21/21 |
| twofir | serial-repro-native | new kernel N07 | r3new/r3old | ifx-expert | 10.4 -> 10.44 | 1.004 [0.983, 1.024] | live 21/21 |
| twofir | mt8-repro-native | new kernel N07 | r3new/r3old | blade-llvm | -- | -- | (arm not ok in a block) |
| twofir | mt8-repro-native | new kernel N07 | r3new/r3old | blade-cpp | 12.74 -> 12.57 | 0.986 [0.968, 1.014] | live 21/21 |
| twofir | mt8-repro-native | new kernel N07 | r3new/r3old | gfortran-natural | 8.932 -> 8.827 | 0.988 [0.960, 1.023] | live 21/21 |
| twofir | mt8-repro-native | new kernel N07 | r3new/r3old | gfortran-expert | 7.337 -> 7.383 | 1.006 [0.980, 1.048] | live 21/21 |
| twofir | mt8-repro-native | new kernel N07 | r3new/r3old | ifx-natural | 9.233 -> 9.356 | 1.013 [0.980, 1.041] | live 21/21 |
| twofir | mt8-repro-native | new kernel N07 | r3new/r3old | ifx-expert | 7.646 -> 7.82 | 1.023 [0.980, 1.059] | live 21/21 |
| tensorop | serial-licensed-native | new kernel N09 | r3new/r3old | blade-llvm | 142 -> 140.4 | 0.989 [0.975, 1.015] | live 11/11 |
| tensorop | serial-licensed-native | new kernel N09 | r3new/r3old | blade-cpp | 148.4 -> 148.5 | 1.001 [0.991, 1.011] | live 11/11 |
| tensorop | serial-licensed-native | new kernel N09 | r3new/r3old | gfortran-natural | 71.91 -> 72.05 | 1.002 [0.999, 1.004] | live 11/11 |
| tensorop | serial-licensed-native | new kernel N09 | r3new/r3old | gfortran-expert | 27.04 -> 27.18 | 1.005 [0.984, 1.019] | live 11/11 |
| tensorop | serial-licensed-native | new kernel N09 | r3new/r3old | ifx-natural | 59.75 -> 59.75 | 1.000 [0.992, 1.024] | live 11/11 |
| tensorop | serial-licensed-native | new kernel N09 | r3new/r3old | ifx-expert | 19.31 -> 19.52 | 1.011 [1.000, 1.019] | live 11/11 |
| tensorop | mt8-licensed-native | new kernel N09 | r3new/r3old | blade-llvm | -- | -- | (arm not ok in a block) |
| tensorop | mt8-licensed-native | new kernel N09 | r3new/r3old | blade-cpp | 148.2 -> 148.5 | 1.002 [0.997, 1.008] | ind 21/21 |
| tensorop | mt8-licensed-native | new kernel N09 | r3new/r3old | gfortran-natural | 21.4 -> 21.82 | 1.020 [0.964, 1.074] | ind 21/21 |
| tensorop | mt8-licensed-native | new kernel N09 | r3new/r3old | gfortran-expert | 7.24 -> 7.371 | 1.018 [0.931, 1.113] | ind 21/21 |
| tensorop | mt8-licensed-native | new kernel N09 | r3new/r3old | ifx-natural | 14.65 -> 13.69 | 0.935 [0.831, 1.052] | ind 21/21 |
| tensorop | mt8-licensed-native | new kernel N09 | r3new/r3old | ifx-expert | 7.091 -> 6.7 | 0.945 [0.898, 1.086] | ind 21/21 |
| tensorop | serial-repro-native | new kernel N09 | r3new/r3old | blade-llvm | 149 -> 146.5 | 0.983 [0.868, 1.068] | ind 11/11 |
| tensorop | serial-repro-native | new kernel N09 | r3new/r3old | blade-cpp | 149.1 -> 149.8 | 1.005 [0.989, 1.023] | ind 11/11 |
| tensorop | serial-repro-native | new kernel N09 | r3new/r3old | gfortran-natural | 72.7 -> 72.28 | 0.994 [0.957, 1.022] | ind 11/11 |
| tensorop | serial-repro-native | new kernel N09 | r3new/r3old | gfortran-expert | 26.53 -> 26.86 | 1.013 [0.983, 1.026] | ind 11/11 |
| tensorop | serial-repro-native | new kernel N09 | r3new/r3old | ifx-natural | 59.74 -> 59.84 | 1.002 [0.987, 1.021] | ind 11/11 |
| tensorop | serial-repro-native | new kernel N09 | r3new/r3old | ifx-expert | 19.68 -> 20.02 | 1.017 [0.998, 1.030] | ind 11/11 |
| tensorop | mt8-repro-native | new kernel N09 | r3new/r3old | blade-llvm | -- | -- | (arm not ok in a block) |
| tensorop | mt8-repro-native | new kernel N09 | r3new/r3old | blade-cpp | 148 -> 148.2 | 1.002 [0.998, 1.006] | ind 21/21 |
| tensorop | mt8-repro-native | new kernel N09 | r3new/r3old | gfortran-natural | 20.63 -> 21.27 | 1.031 [0.954, 1.051] | ind 21/21 |
| tensorop | mt8-repro-native | new kernel N09 | r3new/r3old | gfortran-expert | 7.25 -> 7.218 | 0.996 [0.915, 1.074] | ind 21/21 |
| tensorop | mt8-repro-native | new kernel N09 | r3new/r3old | ifx-natural | 14.86 -> 15.16 | 1.020 [0.953, 1.198] | ind 21/21 |
| tensorop | mt8-repro-native | new kernel N09 | r3new/r3old | ifx-expert | 6.919 -> 6.928 | 1.001 [0.921, 1.109] | ind 21/21 |
| tensorop | serial-fastmath-native | new kernel N09 | r3new/r3old | blade-llvm | 141.7 -> 143.5 | 1.013 [0.983, 1.045] | live 21/21 |
| tensorop | serial-fastmath-native | new kernel N09 | r3new/r3old | blade-cpp | 148.7 -> 148.7 | 1.000 [0.998, 1.003] | live 21/21 |
| tensorop | serial-fastmath-native | new kernel N09 | r3new/r3old | gfortran-natural | 40.23 -> 40.44 | 1.005 [1.000, 1.011] | live 21/21 |
| tensorop | serial-fastmath-native | new kernel N09 | r3new/r3old | gfortran-expert | 26.44 -> 26.65 | 1.008 [0.989, 1.016] | live 21/21 |
| tensorop | serial-fastmath-native | new kernel N09 | r3new/r3old | ifx-natural | 25.11 -> 25.3 | 1.008 [0.993, 1.014] | live 21/21 |
| tensorop | serial-fastmath-native | new kernel N09 | r3new/r3old | ifx-expert | 35.05 -> 34.78 | 0.992 [0.971, 1.021] | live 21/21 |
| tensorop | mt8-fastmath-native | new kernel N09 | r3new/r3old | blade-llvm | -- | -- | (arm not ok in a block) |
| tensorop | mt8-fastmath-native | new kernel N09 | r3new/r3old | blade-cpp | 148.2 -> 148.4 | 1.002 [0.999, 1.004] | ind 21/21 |
| tensorop | mt8-fastmath-native | new kernel N09 | r3new/r3old | gfortran-natural | 13.8 -> 13.62 | 0.987 [0.875, 1.048] | ind 21/21 |
| tensorop | mt8-fastmath-native | new kernel N09 | r3new/r3old | gfortran-expert | 7.194 -> 7.044 | 0.979 [0.938, 1.017] | ind 21/21 |
| tensorop | mt8-fastmath-native | new kernel N09 | r3new/r3old | ifx-natural | 9.105 -> 9.163 | 1.006 [0.959, 1.081] | ind 21/21 |
| tensorop | mt8-fastmath-native | new kernel N09 | r3new/r3old | ifx-expert | 8.239 -> 8.743 | 1.061 [0.867, 1.212] | ind 21/21 |
| gram | serial-licensed-native | control | r3new/r3old | blade-llvm | 56.48 -> 56.69 | 1.004 [0.987, 1.051] | ind 11/11 |
| gram | serial-licensed-native | control | r3new/r3old | blade-cpp | 57.61 -> 58.31 | 1.012 [0.992, 1.039] | ind 11/11 |
| gram | serial-licensed-native | control | r3new/r3old | gfortran-natural | 484.2 -> 571.5 | 1.180 [1.106, 1.266] | ind 11/11 |
| gram | serial-licensed-native | control | r3new/r3old | gfortran-expert | 77.49 -> 77.55 | 1.001 [0.987, 1.010] | ind 11/11 |
| gram | serial-licensed-native | control | r3new/r3old | ifx-natural | 569.2 -> 568.8 | 0.999 [0.997, 1.002] | ind 11/11 |
| gram | serial-licensed-native | control | r3new/r3old | ifx-expert | 70.12 -> 69.76 | 0.995 [0.990, 1.006] | ind 11/11 |

## 2. Arm medians per block (ms)

| kernel | regime | mirror | block | blade-llvm | blade-cpp | gfortran-natural | gfortran-expert | ifx-natural | ifx-expert |
|---|---|---|---|---|---|---|---|---|---|
| stencil | serial-repro-native | r3old | live | 11.86 | 15.36 | 16.87 | 16.5 | 18.26 | 13.17 |
| stencil | serial-repro-native | r3new | live | 11.87 | 15.3 | 16.81 | 16.44 | 18.31 | 13.16 |
| stencil | mt8-repro-native | r3old | live | serial-only: LLVM lane has no OpenMP | 9.684 | 8.733 | 7.796 | 9.976 | 8.488 |
| stencil | mt8-repro-native | r3new | indicative | serial-only: LLVM lane has no OpenMP | 8.48 | 8.602 | 7.835 | 9.762 | 8.13 |
| stencil | serial-licensed-native | r3old | live | 10.31 | 13.99 | 16.88 | 13.93 | 18.4 | 10.74 |
| stencil | serial-licensed-native | r3new | indicative | 10.29 | 13.94 | 16.8 | 13.91 | 18.28 | 10.89 |
| stencil | mt8-licensed-native | r3old | live | serial-only: LLVM lane has no OpenMP | 8.838 | 8.804 | 7.832 | 9.859 | 8.303 |
| stencil | mt8-licensed-native | r3new | indicative | serial-only: LLVM lane has no OpenMP | 8.862 | 8.587 | 7.734 | 9.978 | 8.397 |
| maskagg | serial-licensed-native | r3old | indicative | 3.767 | 5.382 | 8.219 | 4.008 | 10.71 | 2.925 |
| maskagg | serial-licensed-native | r3new | indicative | 3.076 | 4.156 | 8.303 | 3.967 | 10.67 | 2.984 |
| maskagg | serial-licensed-native | r3gather | live | 3.028 | 4.202 | 8.197 | 4.002 | 10.59 | 2.915 |
| maskagg | serial-repro-native | r3old | live | 4.462 | 5.74 | 8.257 | 4.788 | 10.53 | 3.69 |
| maskagg | serial-repro-native | r3new | live | 3.862 | 4.913 | 8.207 | 4.868 | 10.56 | 3.688 |
| maskagg | serial-repro-native | r3gather | live | 3.763 | 4.841 | 8.272 | 4.789 | 10.58 | 3.7 |
| pairkernel | mt8-repro-native | r3old | indicative | serial-only: LLVM lane has no OpenMP | 9.115 | 13.79 | 6.237 | 15.51 | 6.196 |
| pairkernel | mt8-repro-native | r3new | indicative | serial-only: LLVM lane has no OpenMP | 9.228 | 14.76 | 6.333 | 16.4 | 6.023 |
| pairkernel | mt8-repro-native | r3noslots | indicative | serial-only: LLVM lane has no OpenMP | 7.757 | 13.65 | 6.167 | 15.47 | 6.242 |
| pairkernel | serial-repro-native | r3old | live | 29.6 | 29.48 | 60.3 | 34.24 | 63.92 | 31.8 |
| pairkernel | serial-repro-native | r3new | live | 29.52 | 29.59 | 60.4 | 34.24 | 63.78 | 31.85 |
| pairkernel | serial-repro-native | r3noslots | live | 29.71 | 29.55 | 60.32 | 34.42 | 64.13 | 31.78 |
| comoment3 | mt8-repro-native | r3old | indicative | serial-only: LLVM lane has no OpenMP | 4.315 | 92.36 | 4.861 | 108.3 | 4.166 |
| comoment3 | mt8-repro-native | r3new | indicative | serial-only: LLVM lane has no OpenMP | 4.432 | 90.55 | 4.908 | 110.7 | 4.188 |
| comoment3 | mt8-repro-native | r3noslots | indicative | serial-only: LLVM lane has no OpenMP | 3.736 | 85.65 | 4.866 | 113 | 4.184 |
| comoment3 | serial-repro-native | r3old | live | 8.909 | 8.934 | 600.1 | 11.41 | 719.1 | 11.6 |
| comoment3 | serial-repro-native | r3new | live | 8.895 | 8.951 | 600.2 | 11.49 | 718 | 11.48 |
| comoment3 | serial-repro-native | r3noslots | live | 8.912 | 9.019 | 600.1 | 11.44 | 718.3 | 11.57 |
| antisym | serial-licensed-native | r3new | live | 27.96 | 28.72 | 771.8 | 31.04 | 333.5 | 29.73 |
| antisym | serial-licensed-native | r3new | live | 27.72 | 28.92 | 773.1 | 30.62 | 331.4 | 29.51 |
| antisym | serial-licensed-native | r3new | live | 28.84 | 29.29 | 774.3 | 31.26 | 335.3 | 29.67 |
| antisym | serial-licensed-native | r3antig0 | indicative | 29.13 | 30.18 | 773.9 | 32.7 | 337.1 | 32.24 |
| antisym | serial-licensed-native | r3antig0 | live | 28.25 | 28.99 | 771.9 | 30.53 | 331.8 | 29.25 |
| antisym | serial-licensed-native | r3antig0 | live | 28.11 | 29.23 | 771.2 | 30.86 | 331.9 | 29 |
| antisym | mt8-licensed-native | r3new | indicative | serial-only: LLVM lane has no OpenMP | 8.542 | 106.1 | 9.472 | 67.86 | 9.599 |
| antisym | mt8-licensed-native | r3new | live | serial-only: LLVM lane has no OpenMP | 8.455 | 107.2 | 9.468 | 63.66 | 9.676 |
| antisym | mt8-licensed-native | r3new | live | serial-only: LLVM lane has no OpenMP | 8.644 | 107 | 9.52 | 62.16 | 9.932 |
| antisym | mt8-licensed-native | r3antig0 | indicative | serial-only: LLVM lane has no OpenMP | 8.175 | 106.1 | 9.284 | 60 | 9.77 |
| antisym | mt8-licensed-native | r3antig0 | live | serial-only: LLVM lane has no OpenMP | 8.159 | 106.5 | 9.675 | 67.25 | 9.863 |
| antisym | mt8-licensed-native | r3antig0 | indicative | serial-only: LLVM lane has no OpenMP | 8.23 | 106.6 | 9.563 | 63.69 | 9.542 |
| twofir | serial-licensed-native | r3old | live | 9.748 | 14.27 | 15.98 | 11.62 | 13.99 | 9.314 |
| twofir | serial-licensed-native | r3new | live | 9.806 | 14.33 | 15.9 | 11.56 | 13.89 | 9.45 |
| twofir | mt8-licensed-native | r3old | live | serial-only: LLVM lane has no OpenMP | 10.41 | 9.006 | 7.298 | 9.493 | 7.653 |
| twofir | mt8-licensed-native | r3new | indicative | serial-only: LLVM lane has no OpenMP | 10.25 | 9.099 | 7.56 | 9.285 | 7.714 |
| twofir | serial-repro-native | r3old | live | 11.15 | 16.48 | 15.85 | 12.55 | 14.03 | 10.4 |
| twofir | serial-repro-native | r3new | live | 11.1 | 16.65 | 15.98 | 12.56 | 13.79 | 10.44 |
| twofir | mt8-repro-native | r3old | live | serial-only: LLVM lane has no OpenMP | 12.74 | 8.932 | 7.337 | 9.233 | 7.646 |
| twofir | mt8-repro-native | r3new | live | serial-only: LLVM lane has no OpenMP | 12.57 | 8.827 | 7.383 | 9.356 | 7.82 |
| tensorop | serial-licensed-native | r3old | live | 142 | 148.4 | 71.91 | 27.04 | 59.75 | 19.31 |
| tensorop | serial-licensed-native | r3new | live | 140.4 | 148.5 | 72.05 | 27.18 | 59.75 | 19.52 |
| tensorop | mt8-licensed-native | r3old | indicative | serial-only: LLVM lane has no OpenMP | 148.2 | 21.4 | 7.24 | 14.65 | 7.091 |
| tensorop | mt8-licensed-native | r3new | indicative | serial-only: LLVM lane has no OpenMP | 148.5 | 21.82 | 7.371 | 13.69 | 6.7 |
| tensorop | serial-repro-native | r3old | indicative | 149 | 149.1 | 72.7 | 26.53 | 59.74 | 19.68 |
| tensorop | serial-repro-native | r3new | indicative | 146.5 | 149.8 | 72.28 | 26.86 | 59.84 | 20.02 |
| tensorop | mt8-repro-native | r3old | indicative | serial-only: LLVM lane has no OpenMP | 148 | 20.63 | 7.25 | 14.86 | 6.919 |
| tensorop | mt8-repro-native | r3new | indicative | serial-only: LLVM lane has no OpenMP | 148.2 | 21.27 | 7.218 | 15.16 | 6.928 |
| tensorop | serial-fastmath-native | r3old | live | 141.7 | 148.7 | 40.23 | 26.44 | 25.11 | 35.05 |
| tensorop | serial-fastmath-native | r3new | live | 143.5 | 148.7 | 40.44 | 26.65 | 25.3 | 34.78 |
| tensorop | mt8-fastmath-native | r3old | indicative | serial-only: LLVM lane has no OpenMP | 148.2 | 13.8 | 7.194 | 9.105 | 8.239 |
| tensorop | mt8-fastmath-native | r3new | indicative | serial-only: LLVM lane has no OpenMP | 148.4 | 13.62 | 7.044 | 9.163 | 8.743 |
| gram | serial-licensed-native | r3old | indicative | 56.48 | 57.61 | 484.2 | 77.49 | 569.2 | 70.12 |
| gram | serial-licensed-native | r3new | live | 56.69 | 58.31 | 571.5 | 77.55 | 568.8 | 69.76 |

## 3. Expert ratios (expert median / Blade ref median; > 1 Blade faster); harness ratio [CI] verdict

| kernel | regime | mirror | ref | gfortran-expert | ifx-expert | block |
|---|---|---|---|---|---|---|
| stencil | serial-repro-native | r3old | blade-llvm | 1.392 [1.378, 1.415] win | 1.111 [1.104, 1.136] win | live |
| stencil | serial-repro-native | r3new | blade-llvm | 1.385 [1.371, 1.406] win | 1.108 [1.099, 1.119] win | live |
| stencil | mt8-repro-native | r3old | blade-cpp | 0.805 [0.785, 0.824] loss | 0.877 [0.846, 0.897] loss | live |
| stencil | mt8-repro-native | r3new | blade-cpp | 0.924 [0.872, 0.947] suppressed | 0.959 [0.898, 1.001] suppressed | indicative |
| stencil | serial-licensed-native | r3old | blade-llvm | 1.351 [1.306, 1.372] win | 1.042 [0.997, 1.053] equivalent | live |
| stencil | serial-licensed-native | r3new | blade-llvm | 1.352 [1.316, 1.376] suppressed | 1.058 [1.027, 1.065] suppressed | indicative |
| stencil | mt8-licensed-native | r3old | blade-cpp | 0.886 [0.861, 0.924] loss | 0.939 [0.900, 0.977] detectable-loss | live |
| stencil | mt8-licensed-native | r3new | blade-cpp | 0.873 [0.852, 0.898] suppressed | 0.947 [0.914, 0.964] suppressed | indicative |
| maskagg | serial-licensed-native | r3old | blade-llvm | 1.064 [1.020, 1.118] suppressed | 0.777 [0.743, 0.784] suppressed | indicative |
| maskagg | serial-licensed-native | r3new | blade-llvm | 1.290 [1.252, 1.326] suppressed | 0.970 [0.940, 1.059] suppressed | indicative |
| maskagg | serial-licensed-native | r3gather | blade-llvm | 1.322 [1.275, 1.366] win | 0.963 [0.935, 0.989] detectable-loss | live |
| maskagg | serial-repro-native | r3old | blade-llvm | 1.073 [1.056, 1.078] detectable-win | 0.827 [0.811, 0.837] loss | live |
| maskagg | serial-repro-native | r3new | blade-llvm | 1.260 [1.236, 1.287] win | 0.955 [0.942, 0.970] detectable-loss | live |
| maskagg | serial-repro-native | r3gather | blade-llvm | 1.273 [1.251, 1.282] win | 0.983 [0.971, 0.996] detectable-loss | live |
| pairkernel | mt8-repro-native | r3old | blade-cpp | 0.684 [0.654, 0.728] suppressed | 0.680 [0.662, 0.700] suppressed | indicative |
| pairkernel | mt8-repro-native | r3new | blade-cpp | 0.686 [0.661, 0.723] suppressed | 0.653 [0.638, 0.703] suppressed | indicative |
| pairkernel | mt8-repro-native | r3noslots | blade-cpp | 0.795 [0.782, 0.841] suppressed | 0.805 [0.772, 0.832] suppressed | indicative |
| pairkernel | serial-repro-native | r3old | blade-llvm | 1.157 [1.146, 1.160] win | 1.074 [1.065, 1.081] detectable-win | live |
| pairkernel | serial-repro-native | r3new | blade-llvm | 1.160 [1.153, 1.163] win | 1.079 [1.069, 1.085] detectable-win | live |
| pairkernel | serial-repro-native | r3noslots | blade-llvm | 1.158 [1.151, 1.168] win | 1.070 [1.064, 1.075] detectable-win | live |
| comoment3 | mt8-repro-native | r3old | blade-cpp | 1.127 [1.085, 1.172] suppressed | 0.965 [0.922, 0.986] suppressed | indicative |
| comoment3 | mt8-repro-native | r3new | blade-cpp | 1.107 [1.077, 1.162] suppressed | 0.945 [0.917, 0.975] suppressed | indicative |
| comoment3 | mt8-repro-native | r3noslots | blade-cpp | 1.303 [1.254, 1.365] suppressed | 1.120 [1.063, 1.155] suppressed | indicative |
| comoment3 | serial-repro-native | r3old | blade-llvm | 1.281 [1.274, 1.290] win | 1.302 [1.289, 1.311] win | live |
| comoment3 | serial-repro-native | r3new | blade-llvm | 1.292 [1.267, 1.310] win | 1.291 [1.269, 1.302] win | live |
| comoment3 | serial-repro-native | r3noslots | blade-llvm | 1.284 [1.273, 1.298] win | 1.298 [1.285, 1.317] win | live |
| antisym | serial-licensed-native | r3new | blade-llvm | 1.110 [1.079, 1.146] win | 1.063 [1.037, 1.082] detectable-win | live |
| antisym | serial-licensed-native | r3new | blade-llvm | 1.105 [1.088, 1.145] win | 1.064 [1.048, 1.098] detectable-win | live |
| antisym | serial-licensed-native | r3new | blade-llvm | 1.084 [1.015, 1.115] detectable-win | 1.029 [0.967, 1.053] equivalent | live |
| antisym | serial-licensed-native | r3antig0 | blade-llvm | 1.122 [1.086, 1.175] suppressed | 1.106 [1.032, 1.153] suppressed | indicative |
| antisym | serial-licensed-native | r3antig0 | blade-llvm | 1.081 [1.075, 1.098] detectable-win | 1.036 [1.029, 1.048] detectable-win | live |
| antisym | serial-licensed-native | r3antig0 | blade-llvm | 1.098 [1.081, 1.106] detectable-win | 1.032 [1.022, 1.042] detectable-win | live |
| antisym | mt8-licensed-native | r3new | blade-cpp | 1.109 [1.063, 1.131] suppressed | 1.124 [1.074, 1.187] suppressed | indicative |
| antisym | mt8-licensed-native | r3new | blade-cpp | 1.120 [1.054, 1.154] win | 1.144 [1.084, 1.193] win | live |
| antisym | mt8-licensed-native | r3new | blade-cpp | 1.101 [1.044, 1.141] win | 1.149 [1.096, 1.182] win | live |
| antisym | mt8-licensed-native | r3antig0 | blade-cpp | 1.136 [1.076, 1.182] suppressed | 1.195 [1.125, 1.233] suppressed | indicative |
| antisym | mt8-licensed-native | r3antig0 | blade-cpp | 1.186 [1.100, 1.223] win | 1.209 [1.152, 1.252] win | live |
| antisym | mt8-licensed-native | r3antig0 | blade-cpp | 1.162 [1.085, 1.217] suppressed | 1.159 [1.088, 1.205] suppressed | indicative |
| twofir | serial-licensed-native | r3old | blade-llvm | 1.192 [1.167, 1.216] win | 0.955 [0.934, 0.973] detectable-loss | live |
| twofir | serial-licensed-native | r3new | blade-llvm | 1.178 [1.155, 1.195] win | 0.964 [0.943, 0.977] detectable-loss | live |
| twofir | mt8-licensed-native | r3old | blade-cpp | 0.701 [0.679, 0.732] loss | 0.735 [0.716, 0.772] loss | live |
| twofir | mt8-licensed-native | r3new | blade-cpp | 0.737 [0.702, 0.759] suppressed | 0.752 [0.730, 0.784] suppressed | indicative |
| twofir | serial-repro-native | r3old | blade-llvm | 1.126 [1.110, 1.159] win | 0.933 [0.918, 0.957] detectable-loss | live |
| twofir | serial-repro-native | r3new | blade-llvm | 1.132 [1.106, 1.162] win | 0.941 [0.922, 0.965] detectable-loss | live |
| twofir | mt8-repro-native | r3old | blade-cpp | 0.576 [0.555, 0.589] loss | 0.600 [0.585, 0.624] loss | live |
| twofir | mt8-repro-native | r3new | blade-cpp | 0.587 [0.572, 0.601] loss | 0.622 [0.604, 0.643] loss | live |
| tensorop | serial-licensed-native | r3old | blade-llvm | 0.190 [0.188, 0.197] loss | 0.136 [0.135, 0.139] loss | live |
| tensorop | serial-licensed-native | r3new | blade-llvm | 0.194 [0.191, 0.196] loss | 0.139 [0.137, 0.141] loss | live |
| tensorop | mt8-licensed-native | r3old | blade-cpp | 0.049 [0.048, 0.053] suppressed | 0.048 [0.045, 0.050] suppressed | indicative |
| tensorop | mt8-licensed-native | r3new | blade-cpp | 0.050 [0.047, 0.053] suppressed | 0.045 [0.044, 0.050] suppressed | indicative |
| tensorop | serial-repro-native | r3old | blade-llvm | 0.178 [0.162, 0.185] suppressed | 0.132 [0.119, 0.137] suppressed | indicative |
| tensorop | serial-repro-native | r3new | blade-llvm | 0.183 [0.171, 0.192] suppressed | 0.137 [0.129, 0.143] suppressed | indicative |
| tensorop | mt8-repro-native | r3old | blade-cpp | 0.049 [0.048, 0.052] suppressed | 0.047 [0.044, 0.051] suppressed | indicative |
| tensorop | mt8-repro-native | r3new | blade-cpp | 0.049 [0.046, 0.053] suppressed | 0.047 [0.045, 0.049] suppressed | indicative |
| tensorop | serial-fastmath-native | r3old | blade-llvm | 0.187 [0.184, 0.192] loss | 0.247 [0.244, 0.256] loss | live |
| tensorop | serial-fastmath-native | r3new | blade-llvm | 0.186 [0.181, 0.191] loss | 0.242 [0.238, 0.252] loss | live |
| tensorop | mt8-fastmath-native | r3old | blade-cpp | 0.049 [0.047, 0.051] suppressed | 0.056 [0.051, 0.067] suppressed | indicative |
| tensorop | mt8-fastmath-native | r3new | blade-cpp | 0.047 [0.046, 0.049] suppressed | 0.059 [0.053, 0.064] suppressed | indicative |
| gram | serial-licensed-native | r3old | blade-llvm | 1.372 [1.344, 1.380] suppressed | 1.242 [1.221, 1.249] suppressed | indicative |
| gram | serial-licensed-native | r3new | blade-llvm | 1.368 [1.301, 1.377] win | 1.231 [1.176, 1.236] win | live |

## 4. Blocks

| kernel | regime | mirror | rounds | class | compute ctl | memory ctl | flags | excursions | bundle |
|---|---|---|---|---|---|---|---|---|---|
| stencil | serial-repro-native | r3old | 21 | live | 12.729 | 5.289 | - | 0 | session-035807 |
| stencil | serial-repro-native | r3new | 21 | live | 12.661 | 5.214 | - | 0 | session-041046 |
| stencil | mt8-repro-native | r3old | 21 | live | 12.837 | 5.265 | - | 0 | session-035807 |
| stencil | mt8-repro-native | r3new | 21 | indicative | 12.771 | 5.280 | trend: blade-cpp first-half vs second-half median moved 7.1%; trend: gfortran-expert-inputonly first-half vs second-half median moved 6.4% | 0 | session-041046 |
| stencil | serial-licensed-native | r3old | 21 | live | 12.830 | 5.287 | - | 0 | session-035807 |
| stencil | serial-licensed-native | r3new | 21 | indicative | 12.712 | 5.262 | trend: blade-llvm-inputonly first-half vs second-half median moved 5.2% | 0 | session-041046 |
| stencil | mt8-licensed-native | r3old | 21 | live | 12.799 | 5.267 | - | 0 | session-035807 |
| stencil | mt8-licensed-native | r3new | 21 | indicative | 12.759 | 5.253 | trend: ifx-natural first-half vs second-half median moved 5.3% | 0 | session-041046 |
| maskagg | serial-licensed-native | r3old | 21 | indicative | 12.706 | 5.298 | trend: gfortran-expert-inputonly first-half vs second-half median moved 5.7% | 0 | session-042325 |
| maskagg | serial-licensed-native | r3new | 21 | indicative | 12.754 | 5.324 | trend: ifx-expert first-half vs second-half median moved 5.1% | 0 | session-043008 |
| maskagg | serial-licensed-native | r3gather | 21 | live | 12.798 | 5.202 | - | 0 | session-043652 |
| maskagg | serial-repro-native | r3old | 21 | live | 12.702 | 5.218 | - | 0 | session-042325 |
| maskagg | serial-repro-native | r3new | 21 | live | 12.750 | 5.190 | - | 0 | session-043008 |
| maskagg | serial-repro-native | r3gather | 21 | live | 12.846 | 5.220 | - | 0 | session-043652 |
| pairkernel | mt8-repro-native | r3old | 21 | indicative | 12.745 | 5.236 | trend: gfortran-expert first-half vs second-half median moved 6.9%; trend: ifx-expert first-half vs second-half median moved 5.1% | 0 | session-044336 |
| pairkernel | mt8-repro-native | r3new | 21 | indicative | 12.755 | 5.233 | trend: ifx-natural first-half vs second-half median moved 7.2% | 0 | session-044725 |
| pairkernel | mt8-repro-native | r3noslots | 21 | indicative | 12.808 | 5.250 | trend: ifx-natural first-half vs second-half median moved 7.7% | 0 | session-045115 |
| pairkernel | serial-repro-native | r3old | 21 | live | 12.779 | 5.252 | - | 0 | session-044336 |
| pairkernel | serial-repro-native | r3new | 21 | live | 12.776 | 5.228 | - | 0 | session-044725 |
| pairkernel | serial-repro-native | r3noslots | 21 | live | 12.778 | 5.231 | - | 0 | session-045115 |
| comoment3 | mt8-repro-native | r3old | 21 | indicative | 12.823 | 5.240 | trend: ifx-natural first-half vs second-half median moved 6.9% | 0 | session-045505 |
| comoment3 | mt8-repro-native | r3new | 21 | indicative | 12.830 | 5.319 | trend: ifx-natural first-half vs second-half median moved 6.7% | 0 | session-045928 |
| comoment3 | mt8-repro-native | r3noslots | 21 | indicative | 12.851 | 5.291 | trend: blade-cpp first-half vs second-half median moved 5.7% | 0 | session-050350 |
| comoment3 | serial-repro-native | r3old | 21 | live | 12.684 | 5.402 | - | 0 | session-045505 |
| comoment3 | serial-repro-native | r3new | 21 | live | 12.793 | 5.305 | - | 0 | session-045928 |
| comoment3 | serial-repro-native | r3noslots | 21 | live | 12.746 | 5.258 | - | 0 | session-050350 |
| antisym | serial-licensed-native | r3new | 21 | live | 12.743 | 5.247 | - | 0 | session-050813 |
| antisym | serial-licensed-native | r3new | 21 | live | 12.835 | 5.295 | - | 0 | session-051652 |
| antisym | serial-licensed-native | r3new | 21 | live | 12.693 | 5.350 | - | 0 | session-052529 |
| antisym | serial-licensed-native | r3antig0 | 21 | indicative | 12.749 | 5.271 | trend: ifx-expert first-half vs second-half median moved 7.1% | 0 | session-051232 |
| antisym | serial-licensed-native | r3antig0 | 21 | live | 12.794 | 5.294 | - | 0 | session-052110 |
| antisym | serial-licensed-native | r3antig0 | 21 | live | 12.804 | 5.211 | - | 0 | session-052948 |
| antisym | mt8-licensed-native | r3new | 21 | indicative | 12.821 | 5.230 | trend: ifx-expert first-half vs second-half median moved 5.9% | 0 | session-050813 |
| antisym | mt8-licensed-native | r3new | 21 | live | 12.779 | 5.249 | - | 0 | session-051652 |
| antisym | mt8-licensed-native | r3new | 21 | live | 12.747 | 5.236 | - | 0 | session-052529 |
| antisym | mt8-licensed-native | r3antig0 | 21 | indicative | 12.754 | 5.217 | trend: ifx-natural first-half vs second-half median moved 6.5% | 0 | session-051232 |
| antisym | mt8-licensed-native | r3antig0 | 21 | live | 12.667 | 5.265 | - | 0 | session-052110 |
| antisym | mt8-licensed-native | r3antig0 | 21 | indicative | 12.705 | 5.284 | trend: ifx-natural first-half vs second-half median moved 6.9% | 0 | session-052948 |
| twofir | serial-licensed-native | r3old | 21 | live | 12.854 | 5.214 | - | 0 | session-053406 |
| twofir | serial-licensed-native | r3new | 21 | live | 12.712 | 5.203 | - | 0 | session-054640 |
| twofir | mt8-licensed-native | r3old | 21 | live | 12.777 | 5.203 | - | 0 | session-053406 |
| twofir | mt8-licensed-native | r3new | 21 | indicative | 12.721 | 5.272 | trend: blade-cpp-inputonly first-half vs second-half median moved 19.5% | 0 | session-054640 |
| twofir | serial-repro-native | r3old | 21 | live | 12.763 | 5.250 | - | 0 | session-053406 |
| twofir | serial-repro-native | r3new | 21 | live | 12.739 | 5.215 | - | 0 | session-054640 |
| twofir | mt8-repro-native | r3old | 21 | live | 12.778 | 5.291 | - | 0 | session-053406 |
| twofir | mt8-repro-native | r3new | 21 | live | 12.763 | 5.257 | - | 0 | session-054640 |
| tensorop | serial-licensed-native | r3old | 11 | live | 12.667 | 5.388 | - | 0 | session-055915 |
| tensorop | serial-licensed-native | r3new | 11 | live | 12.811 | 5.352 | - | 0 | session-061618 |
| tensorop | mt8-licensed-native | r3old | 21 | indicative | 12.749 | 5.281 | trend: gfortran-expert first-half vs second-half median moved 9.3%; trend: ifx-natural first-half vs second-half median moved 13.2%; trend: ifx-expert first-half vs second-half median moved 6.4% | 0 | session-055915 |
| tensorop | mt8-licensed-native | r3new | 21 | indicative | 12.783 | 5.280 | trend: gfortran-expert first-half vs second-half median moved 5.8% | 0 | session-061618 |
| tensorop | serial-repro-native | r3old | 11 | indicative | 12.811 | 5.402 | trend: blade-llvm first-half vs second-half median moved 8.0%; trend: control memory moved 5.2% within the block | 0 | session-055915 |
| tensorop | serial-repro-native | r3new | 11 | indicative | 12.934 | 5.249 | trend: blade-llvm first-half vs second-half median moved 6.6%; trend: control memory moved 5.0% within the block | 0 | session-061618 |
| tensorop | mt8-repro-native | r3old | 21 | indicative | 12.710 | 5.245 | trend: ifx-expert first-half vs second-half median moved 11.9% | 0 | session-055915 |
| tensorop | mt8-repro-native | r3new | 21 | indicative | 12.821 | 5.261 | trend: gfortran-natural first-half vs second-half median moved 5.7%; trend: gfortran-expert first-half vs second-half median moved 14.9%; trend: ifx-expert first-half vs second-half median moved 7.3% | 0 | session-061618 |
| tensorop | serial-fastmath-native | r3old | 21 | live | 12.917 | 5.455 | - | 0 | session-055915 |
| tensorop | serial-fastmath-native | r3new | 21 | live | 12.750 | 5.239 | - | 0 | session-061618 |
| tensorop | mt8-fastmath-native | r3old | 21 | indicative | 12.796 | 5.261 | trend: gfortran-natural first-half vs second-half median moved 8.3%; trend: ifx-natural-inputonly first-half vs second-half median moved 5.7% | 0 | session-055915 |
| tensorop | mt8-fastmath-native | r3new | 21 | indicative | 12.775 | 5.273 | trend: gfortran-natural first-half vs second-half median moved 6.8%; trend: ifx-expert first-half vs second-half median moved 6.7% | 0 | session-061618 |
| gram | serial-licensed-native | r3old | 11 | indicative | 12.752 | 5.275 | trend: gfortran-natural first-half vs second-half median moved 7.2% | 0 | session-063325 |
| gram | serial-licensed-native | r3new | 11 | live | 12.823 | 5.227 | - | 0 | session-063505 |

## 5. r3new slower than r3old beyond the CI (Blade arms)

| kernel | regime | arm | old ms | new ms | new/old [CI] | class |
|---|---|---|---|---|---|---|
| pairkernel | serial-repro-native | blade-cpp | 29.48 | 29.59 | 1.004 [1.001, 1.008] | live |

## 6. Missing cells

none

## 7. Sessions

| kernel | mirror | bundle | started | aborted | duration (s) | failures | flags |
|---|---|---|---|---|---|---|---|
| stencil | r3old | C:\bt_r2ab\r3old\bench\results\2026-10-21-LAPTOP-350H6LDB\session-035807 | 2026-10-21T03:58:07 | None | 748 | none | none |
| maskagg | r3old | C:\bt_r2ab\r3old\bench\results\2026-10-21-LAPTOP-350H6LDB\session-042325 | 2026-10-21T04:23:25 | None | 393 | none | maskagg/serial-licensed-native: inference suppressed: trend: gfortran-expert-inputonly first-half vs second-half median moved 5.7% |
| pairkernel | r3old | C:\bt_r2ab\r3old\bench\results\2026-10-21-LAPTOP-350H6LDB\session-044336 | 2026-10-21T04:43:36 | None | 219 | none | pairkernel/mt8-repro-native: inference suppressed: trend: gfortran-expert first-half vs second-half median moved 6.9%; trend: ifx-expert first-half vs second-half median moved 5.1% |
| comoment3 | r3old | C:\bt_r2ab\r3old\bench\results\2026-10-21-LAPTOP-350H6LDB\session-045505 | 2026-10-21T04:55:05 | None | 253 | none | comoment3/mt8-repro-native: inference suppressed: trend: ifx-natural first-half vs second-half median moved 6.9% |
| twofir | r3old | C:\bt_r2ab\r3old\bench\results\2026-10-21-LAPTOP-350H6LDB\session-053406 | 2026-10-21T05:34:06 | None | 744 | none | none |
| tensorop | r3old | C:\bt_r2ab\r3old\bench\results\2026-10-21-LAPTOP-350H6LDB\session-055915 | 2026-10-21T05:59:15 | None | 1013 | none | tensorop/mt8-licensed-native: inference suppressed: trend: gfortran-expert first-half vs second-half median moved 9.3%; trend: ifx-natural first-half vs second-half median moved 13.2%; trend: ifx-expert first-half vs second-half median moved 6.4%; tensorop/serial-repro-native: inference suppressed: trend: blade-llvm first-half vs second-half median moved 8.0%; trend: control memory moved 5.2% within the block; tensorop/mt8-repro-native: inference suppressed: trend: ifx-expert first-half vs second-half median moved 11.9%; tensorop/mt8-fastmath-native: inference suppressed: trend: gfortran-natural first-half vs second-half median moved 8.3%; trend: ifx-natural-inputonly first-half vs second-half median moved 5.7% |
| gram | r3old | C:\bt_r2ab\r3old\bench\results\2026-10-21-LAPTOP-350H6LDB\session-063325 | 2026-10-21T06:33:25 | None | 90 | none | gram/serial-licensed-native: inference suppressed: trend: gfortran-natural first-half vs second-half median moved 7.2% |
| stencil | r3new | C:\bt_r2ab\r3new\bench\results\2026-10-21-LAPTOP-350H6LDB\session-041046 | 2026-10-21T04:10:46 | None | 748 | none | stencil/serial-licensed-native: inference suppressed: trend: blade-llvm-inputonly first-half vs second-half median moved 5.2%; stencil/mt8-licensed-native: inference suppressed: trend: ifx-natural first-half vs second-half median moved 5.3%; stencil/mt8-repro-native: inference suppressed: trend: blade-cpp first-half vs second-half median moved 7.1%; trend: gfortran-expert-inputonly first-half vs second-half median moved 6.4% |
| maskagg | r3new | C:\bt_r2ab\r3new\bench\results\2026-10-21-LAPTOP-350H6LDB\session-043008 | 2026-10-21T04:30:08 | None | 393 | none | maskagg/serial-licensed-native: inference suppressed: trend: ifx-expert first-half vs second-half median moved 5.1% |
| pairkernel | r3new | C:\bt_r2ab\r3new\bench\results\2026-10-21-LAPTOP-350H6LDB\session-044725 | 2026-10-21T04:47:25 | None | 219 | none | pairkernel/mt8-repro-native: inference suppressed: trend: ifx-natural first-half vs second-half median moved 7.2% |
| comoment3 | r3new | C:\bt_r2ab\r3new\bench\results\2026-10-21-LAPTOP-350H6LDB\session-045928 | 2026-10-21T04:59:28 | None | 252 | none | comoment3/mt8-repro-native: inference suppressed: trend: ifx-natural first-half vs second-half median moved 6.7% |
| antisym | r3new | C:\bt_r2ab\r3new\bench\results\2026-10-21-LAPTOP-350H6LDB\session-050813 | 2026-10-21T05:08:13 | None | 248 | none | antisym/mt8-licensed-native: inference suppressed: trend: ifx-expert first-half vs second-half median moved 5.9% |
| antisym | r3new | C:\bt_r2ab\r3new\bench\results\2026-10-21-LAPTOP-350H6LDB\session-051652 | 2026-10-21T05:16:52 | None | 248 | none | none |
| antisym | r3new | C:\bt_r2ab\r3new\bench\results\2026-10-21-LAPTOP-350H6LDB\session-052529 | 2026-10-21T05:25:29 | None | 248 | none | none |
| twofir | r3new | C:\bt_r2ab\r3new\bench\results\2026-10-21-LAPTOP-350H6LDB\session-054640 | 2026-10-21T05:46:40 | None | 744 | none | twofir/mt8-licensed-native: inference suppressed: trend: blade-cpp-inputonly first-half vs second-half median moved 19.5% |
| tensorop | r3new | C:\bt_r2ab\r3new\bench\results\2026-10-21-LAPTOP-350H6LDB\session-061618 | 2026-10-21T06:16:18 | None | 1016 | none | tensorop/mt8-licensed-native: inference suppressed: trend: gfortran-expert first-half vs second-half median moved 5.8%; tensorop/serial-repro-native: inference suppressed: trend: blade-llvm first-half vs second-half median moved 6.6%; trend: control memory moved 5.0% within the block; tensorop/mt8-repro-native: inference suppressed: trend: gfortran-natural first-half vs second-half median moved 5.7%; trend: gfortran-expert first-half vs second-half median moved 14.9%; trend: ifx-expert first-half vs second-half median moved 7.3%; tensorop/mt8-fastmath-native: inference suppressed: trend: gfortran-natural first-half vs second-half median moved 6.8%; trend: ifx-expert first-half vs second-half median moved 6.7% |
| gram | r3new | C:\bt_r2ab\r3new\bench\results\2026-10-21-LAPTOP-350H6LDB\session-063505 | 2026-10-21T06:35:05 | None | 91 | none | none |
| pairkernel | r3noslots | C:\bt_r2ab\r3noslots\bench\results\2026-10-21-LAPTOP-350H6LDB\session-045115 | 2026-10-21T04:51:15 | None | 219 | none | pairkernel/mt8-repro-native: inference suppressed: trend: ifx-natural first-half vs second-half median moved 7.7% |
| comoment3 | r3noslots | C:\bt_r2ab\r3noslots\bench\results\2026-10-21-LAPTOP-350H6LDB\session-050350 | 2026-10-21T05:03:50 | None | 253 | none | comoment3/mt8-repro-native: inference suppressed: trend: blade-cpp first-half vs second-half median moved 5.7% |
| maskagg | r3gather | C:\bt_r2ab\r3gather\bench\results\2026-10-21-LAPTOP-350H6LDB\session-043652 | 2026-10-21T04:36:52 | None | 393 | none | none |
| antisym | r3antig0 | C:\bt_r2ab\r3antig0\bench\results\2026-10-21-LAPTOP-350H6LDB\session-051232 | 2026-10-21T05:12:32 | None | 249 | none | antisym/serial-licensed-native: inference suppressed: trend: ifx-expert first-half vs second-half median moved 7.1%; antisym/mt8-licensed-native: inference suppressed: trend: ifx-natural first-half vs second-half median moved 6.5% |
| antisym | r3antig0 | C:\bt_r2ab\r3antig0\bench\results\2026-10-21-LAPTOP-350H6LDB\session-052110 | 2026-10-21T05:21:10 | None | 248 | none | none |
| antisym | r3antig0 | C:\bt_r2ab\r3antig0\bench\results\2026-10-21-LAPTOP-350H6LDB\session-052948 | 2026-10-21T05:29:48 | None | 248 | none | antisym/mt8-licensed-native: inference suppressed: trend: ifx-natural first-half vs second-half median moved 6.9% |