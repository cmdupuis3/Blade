# Round-3 wave-2 harness A/B: r3w1 (the wave-1 tip binary) vs r3w2 (wave-2 tip 3a3925ac) vs knob arms

Protocol: 28 `bench` sessions 15:07-17:49 (2026-10-21) plus the saxpy legs-off re-run after them, one at a time,
interleaved per cell (old, new, knob arms), harness defaults (21 rounds; gram serial 11), full size, quiet box (load
1.1-2.5% before every block; the chain ran unattended after the five gates and the smokes), AC. Mirrors `C:\bt_r2ab\r3w*`:
r3w1 = the wave-1 tip build (9da403a8, Blade.dll E84BB4E5) + the wave-2 bench tree (so haloadjoint runs on both); r3w2 = the
wave-2 tip build (3a3925ac, Blade.dll 12917295), default config; r3w2prefer = + `BLADE_FOLD_ROW_SLOTS=prefer` (the OLD
precedence: row slots over the scatter under mt8 repro; it cannot preempt the rank-3 pull); r3w2pull0 = + `BLADE_ORBIT_PULL=0`
(the serial ordered scatter instead of the row-owned pull); r3w2antig1 = + `BLADE_TILE_TUNE=anti_g=1` (the old default row
group in every regime); r3w2legs0 = + `BLADE_EPILOGUE_LEGS=0` (the separate joined input-sum pass instead of the producer
legs; the saxpy block of this mirror was refused in the chain -- the smoke plan covered its serial regimes only -- and re-run
with the mt8 pairs smoked, on the same quiet box, right after the chain); r3w2retain = + `BLADE_POOL_RETAIN=1` (the opt-in
A4 retention arm: 64 MB / 96 MB per thread in the C++ lane). Every mirror smoked for its benched pairs (0 failures). Gates on
3a3925ac before the A/B: default 7872/0, opt-diff 2257/0, docs 132/0, llvm 914/0, interp 3118/0; examples sweep 68 files, the
same six pre-existing failures as wave 1. Zero failures / aborts in the A/B; exit 3 = drift flags only (blocks `indicative`).
Ratios: B/A = median(B) / median(A) of the Blade reference lane (blade-llvm serial, blade-cpp mt8), analyst unpaired bootstrap
CI (4000 resamples; NOT a harness output); expert ratio = expert median / Blade ref median (> 1 Blade faster), harness CI
and verdict. antisym ran three sessions per mirror for the new binary and the antig1 arm; its samples are pooled.

## 0. Headline

| cell | item | old gf / ifx | new gf / ifx | new / old (Blade ref) | knob arm / new | class |
|---|---|---|---|---|---|---|
| pairkernel mt8 repro | W2-0a: the ordered scatter takes precedence over the G35 row slots (rank 2) | 0.681 / 0.686 (loss) | **0.803 / 0.805** (loss) | **0.846** [0.838, 0.861] cpp | prefer (old order) 1.179 [1.160, 1.210]; pull0 0.994 (no pull at rank 2: control) | live |
| comoment3 mt8 repro | W2-3: the row-owned pull (rank 3) ahead of the scatter and the row slots | 1.147 / 0.964 | **1.553 / 1.305** (both win) | **0.737** [0.716, 0.760] cpp | pull0 (serial scatter) 1.156 [1.108, 1.20] (ind); prefer 1.017 is NOT a row-slot measurement (the arm still pulls: `prefer` is a scatter / row-slot tie-breaker below the pull); the G35 row slots proper = the g35 arm (pull off AND prefer, re-run 18:09-18:11): 4.40 ms = **1.422** [1.383, 1.501] of the pull and 1.230 [1.185, 1.308] of the serial scatter (ind); expert ratio on the row slots 1.100 / 0.950 | live |
| pairkernel / comoment3 serial repro | controls (all four mirrors must agree) | 1.146 / 1.066; 1.286 / 1.291 | 1.145 / 1.065; 1.289 / 1.291 | 1.001; 0.996 | prefer / pull0 0.997-1.009 | live |
| antisym mt8 licensed | W2-0b: anti_g auto under threads (default), 1 forced | 1.152 / 1.227 (ind) | 1.15-1.17 / 1.19-1.22 (ind) | 0.857 [0.792, 0.899] cpp -- NOT isolable to anti_g: both experts moved 0.846-0.857 in the same sessions (environmental), and the direct knob contrast is the next column | antig1 (1 forced) / auto 1.039 [0.992, 1.069] (ind): the only anti_g-specific evidence, marginal | ind (pooled) |
| antisym serial licensed | W2-0b control (serial default = 1 = the old emission, byte-identical) | 1.109 / 1.052 | 1.104-1.117 / 1.058-1.070 | 1.007 [1.001, 1.020] llvm, 1.011 [1.002, 1.019] cpp -- SLOWER at the CI edge on identical emission: noise between sessions hours apart | antig1 1.009 / 0.996 | ind (pooled) |
| saxpy serial repro | W2-4: the input-sum legs ride the y2 producer pass | 1.408 / 0.888 (loss) | **1.584 / 0.994** (detectable-loss, near parity) | **0.889** [0.882, 0.919] llvm; 0.923 cpp | legs0 (the joined pass) 1.166 [1.129, 1.183] llvm, 1.100 cpp (re-run 17:52-18:04; legs0 lands 2-4% above the OLD binary: between-session drift) | live |
| saxpy serial licensed | W2-4 | 1.391 / 0.991 | **1.547 / 1.113** (win) | **0.893** [0.888, 0.901] llvm; 0.947 cpp | legs0 1.148 [1.124, 1.193] llvm, 1.077 cpp (ind) | live |
| saxpy mt8 repro / licensed | W2-4 decline controls (threaded producer: the legs keep their pass) | 0.821 / 0.859; 0.943 / 0.994 | 0.820 / 0.866; 0.939 / 0.985 | 0.998; 1.003 | -- | live / ind |
| haloadjoint serial licensed / repro | new kernel N02 (1-D weighted nonlinear halo primal + reverse action; five transcendentals per cell vs the expert's two, the adjoint passes serial) | 0.523 / 0.653; 0.516 / 0.655 | 0.521 / 0.646; 0.518 / 0.657 | 1.006; 0.997 | -- | live |
| haloadjoint mt8 licensed / repro | N02 | 0.411 / 0.621; 0.434 / 0.647 | 0.418 / 0.635; 0.436 / 0.648 | 0.994; 0.995 | -- | live / ind |
| stencil mt8 repro / licensed | A4 retention arm (24 MB lap freed inside the timer by default) | 0.927 / 0.961; 0.900 / 0.953 | 0.918 / 0.965; 0.902 / 0.965 (new = old within CI) | 1.003; 0.992 | **retain 0.926** [0.897, 0.943]; **0.920** [0.896, 0.971]; peak working set 51.4 -> 51.5-51.6 MB | live / ind |
| recur mt8 licensed / repro | A4 retention arm (48 MB trajectory) | 0.736 / 0.795; 0.573 / 0.603 (ind) | 0.748 / 0.797; 0.618 / 0.629 (ind; new = old within CI) | 0.989; 1.004 | **retain 0.741** [0.724, 0.782] (9.13 -> 6.76 ms); **0.769** [0.754, 0.790] (12.38 -> 9.52 ms); peak working set 51.7 -> 51.8 MB | ind |
| gram serial licensed | control | 1.357 / 1.230 | 1.359 / 1.222 | 1.005 / 1.010 | -- | ind |

Section 5 (new slower than old beyond the CI): antisym serial licensed, both lanes, 0.7-1.1% (llvm 27.45 -> 27.65 ms, cpp
28.32 -> 28.64), pooled across three sessions hours apart, blocks indicative; the serial emission is byte-identical to the
old binary's (W2-0's fix round pinned old default == new anti_g=1 and the serial default is 1), so this is session drift at
the CI edge, recorded, not a regression. No other Blade arm is slower.

What the measurement settles:
- **The mt8-repro hierarchy is measured: pull > scatter > row slots.** comoment3's pull beats the serial scatter by 13.5%
  and the G35 row slots by 42% (the g35 arm: pull off AND `prefer`; both indicative), and the scatter beats the row slots by
  23%; pairkernel (rank 2, no pull) takes the scatter
  and the old order (`prefer`) is 18% slower. Bit equality of the three forms is pinned by raw-bit tests, not by timings.
- **anti_g per regime: the only anti_g-specific evidence is the direct contrast** (1 forced vs auto 1.039 [0.992, 1.069]
  under threads, indicative, marginal); the 0.857 old/new antisym mt8 move is environmental (both experts moved the same
  way in those sessions). Serially the default is unchanged (byte-identical emission, SHA-verified by the merge-level
  review across the sessions).
- **The saxpy legs clear the drop line on serial repro in both lanes** (llvm 0.889, cpp 0.923; the joined pass without
  them 1.10-1.17 of the new binary) and move the ifx repro cell from a 0.888 loss to 0.994. The "moved free" caveat does
  NOT apply to this baseline: the archived OLD and NEW LLVM emissions of saxpy both allocate y2 in main and neither frees
  it (merge-level review, finding 4), and the C++ release sits where the as-written program's did. What remains open is
  W2-4's general lifetime class (17.5 / G69): these are live observations of saxpy, not a lifetime-matched compiler claim.
- **The A4 retention arm, opt-in:** recur mt8 licensed 9.13 -> 6.76 ms (0.741; the wave-3 probe predicted 11.5 -> 8), repro
  12.38 -> 9.52 (0.769), stencil mt8 0.92; peak working set rises 0.1-0.2 MB on these two kernels. Still opt-in; the user's
  ruling stands.
- **haloadjoint's baseline is now measured** (0.52-0.65 serial, 0.41-0.65 mt8): the expert's two transcendentals per cell vs
  Blade's five (redundant cross-pass primal work) and the serial adjoint passes, as its spec predicted.


Mirrors: r3w1 = wave-1 tip 9da403a8 binary + wave-2 bench; r3w2 = wave-2 tip, defaults; r3w2prefer = BLADE_FOLD_ROW_SLOTS=prefer (old precedence: row slots over the scatter); r3w2pull0 = BLADE_ORBIT_PULL=0 (serial ordered scatter, no row-owned pull); r3w2antig1 = BLADE_TILE_TUNE=anti_g=1 (old default in every regime); r3w2legs0 = BLADE_EPILOGUE_LEGS=0 (separate joined input-sum pass); r3w2retain = BLADE_POOL_RETAIN=1 (A4 opt-in retention arm, 64 MB / 96 MB per thread); r3w2g35 = BLADE_ORBIT_PULL=0 + BLADE_FOLD_ROW_SLOTS=prefer (the G35 row slots at rank 3: pull off AND the old precedence)

## 1. Pairwise medians: B/A < 1 = B faster (samples pooled across sessions of a mirror). CI = analyst unpaired bootstrap.

| kernel | regime | item | B/A | arm | A -> B median (ms) | B/A [95% CI] | class n_A/n_B |
|---|---|---|---|---|---|---|---|
| pairkernel | mt8-repro-native | W2-0a precedence (rank 2: pull falls through) | r3w2/r3w1 | blade-llvm | -- | -- | (arm not ok in a block) |
| pairkernel | mt8-repro-native | W2-0a precedence (rank 2: pull falls through) | r3w2/r3w1 | blade-cpp | 9.027 -> 7.636 | 0.846 [0.838, 0.861] | live 21/21 |
| pairkernel | mt8-repro-native | W2-0a precedence (rank 2: pull falls through) | r3w2/r3w1 | gfortran-natural | 13.41 -> 13.46 | 1.004 [0.994, 1.013] | live 21/21 |
| pairkernel | mt8-repro-native | W2-0a precedence (rank 2: pull falls through) | r3w2/r3w1 | gfortran-expert | 6.149 -> 6.129 | 0.997 [0.948, 1.054] | live 21/21 |
| pairkernel | mt8-repro-native | W2-0a precedence (rank 2: pull falls through) | r3w2/r3w1 | ifx-natural | 15.25 -> 15.8 | 1.036 [0.911, 1.151] | live 21/21 |
| pairkernel | mt8-repro-native | W2-0a precedence (rank 2: pull falls through) | r3w2/r3w1 | ifx-expert | 6.19 -> 6.151 | 0.994 [0.978, 1.012] | live 21/21 |
| pairkernel | mt8-repro-native | W2-0a precedence (rank 2: pull falls through) | r3w2prefer/r3w2 | blade-llvm | -- | -- | (arm not ok in a block) |
| pairkernel | mt8-repro-native | W2-0a precedence (rank 2: pull falls through) | r3w2prefer/r3w2 | blade-cpp | 7.636 -> 9.006 | 1.179 [1.160, 1.210] | ind 21/21 |
| pairkernel | mt8-repro-native | W2-0a precedence (rank 2: pull falls through) | r3w2prefer/r3w2 | gfortran-natural | 13.46 -> 13.5 | 1.003 [0.989, 1.013] | ind 21/21 |
| pairkernel | mt8-repro-native | W2-0a precedence (rank 2: pull falls through) | r3w2prefer/r3w2 | gfortran-expert | 6.129 -> 6.109 | 0.997 [0.953, 1.038] | ind 21/21 |
| pairkernel | mt8-repro-native | W2-0a precedence (rank 2: pull falls through) | r3w2prefer/r3w2 | ifx-natural | 15.8 -> 15.68 | 0.992 [0.876, 1.137] | ind 21/21 |
| pairkernel | mt8-repro-native | W2-0a precedence (rank 2: pull falls through) | r3w2prefer/r3w2 | ifx-expert | 6.151 -> 6.186 | 1.006 [0.987, 1.029] | ind 21/21 |
| pairkernel | mt8-repro-native | W2-0a precedence (rank 2: pull falls through) | r3w2pull0/r3w2 | blade-llvm | -- | -- | (arm not ok in a block) |
| pairkernel | mt8-repro-native | W2-0a precedence (rank 2: pull falls through) | r3w2pull0/r3w2 | blade-cpp | 7.636 -> 7.588 | 0.994 [0.979, 1.006] | live 21/21 |
| pairkernel | mt8-repro-native | W2-0a precedence (rank 2: pull falls through) | r3w2pull0/r3w2 | gfortran-natural | 13.46 -> 13.46 | 1.000 [0.985, 1.036] | live 21/21 |
| pairkernel | mt8-repro-native | W2-0a precedence (rank 2: pull falls through) | r3w2pull0/r3w2 | gfortran-expert | 6.129 -> 6.254 | 1.020 [0.965, 1.063] | live 21/21 |
| pairkernel | mt8-repro-native | W2-0a precedence (rank 2: pull falls through) | r3w2pull0/r3w2 | ifx-natural | 15.8 -> 15.17 | 0.960 [0.867, 1.133] | live 21/21 |
| pairkernel | mt8-repro-native | W2-0a precedence (rank 2: pull falls through) | r3w2pull0/r3w2 | ifx-expert | 6.151 -> 6.044 | 0.983 [0.961, 1.007] | live 21/21 |
| pairkernel | serial-repro-native | control (must agree) | r3w2/r3w1 | blade-llvm | 29.73 -> 29.77 | 1.001 [0.997, 1.003] | live 21/21 |
| pairkernel | serial-repro-native | control (must agree) | r3w2/r3w1 | blade-cpp | 29.63 -> 29.67 | 1.001 [0.998, 1.004] | live 21/21 |
| pairkernel | serial-repro-native | control (must agree) | r3w2/r3w1 | gfortran-natural | 60.21 -> 60.22 | 1.000 [0.997, 1.003] | live 21/21 |
| pairkernel | serial-repro-native | control (must agree) | r3w2/r3w1 | gfortran-expert | 34.06 -> 34.09 | 1.001 [0.995, 1.004] | live 21/21 |
| pairkernel | serial-repro-native | control (must agree) | r3w2/r3w1 | ifx-natural | 63.31 -> 63.38 | 1.001 [0.994, 1.009] | live 21/21 |
| pairkernel | serial-repro-native | control (must agree) | r3w2/r3w1 | ifx-expert | 31.7 -> 31.7 | 1.000 [0.997, 1.003] | live 21/21 |
| pairkernel | serial-repro-native | control (must agree) | r3w2prefer/r3w2 | blade-llvm | 29.77 -> 29.68 | 0.997 [0.994, 1.001] | live 21/21 |
| pairkernel | serial-repro-native | control (must agree) | r3w2prefer/r3w2 | blade-cpp | 29.67 -> 29.6 | 0.998 [0.996, 1.000] | live 21/21 |
| pairkernel | serial-repro-native | control (must agree) | r3w2prefer/r3w2 | gfortran-natural | 60.22 -> 60.08 | 0.998 [0.996, 1.002] | live 21/21 |
| pairkernel | serial-repro-native | control (must agree) | r3w2prefer/r3w2 | gfortran-expert | 34.09 -> 34.18 | 1.003 [0.999, 1.007] | live 21/21 |
| pairkernel | serial-repro-native | control (must agree) | r3w2prefer/r3w2 | ifx-natural | 63.38 -> 63.37 | 1.000 [0.994, 1.004] | live 21/21 |
| pairkernel | serial-repro-native | control (must agree) | r3w2prefer/r3w2 | ifx-expert | 31.7 -> 31.65 | 0.998 [0.996, 1.001] | live 21/21 |
| pairkernel | serial-repro-native | control (must agree) | r3w2pull0/r3w2 | blade-llvm | 29.77 -> 29.68 | 0.997 [0.994, 1.001] | live 21/21 |
| pairkernel | serial-repro-native | control (must agree) | r3w2pull0/r3w2 | blade-cpp | 29.67 -> 29.63 | 0.999 [0.997, 1.001] | live 21/21 |
| pairkernel | serial-repro-native | control (must agree) | r3w2pull0/r3w2 | gfortran-natural | 60.22 -> 60.17 | 0.999 [0.997, 1.003] | live 21/21 |
| pairkernel | serial-repro-native | control (must agree) | r3w2pull0/r3w2 | gfortran-expert | 34.09 -> 34.09 | 1.000 [0.997, 1.004] | live 21/21 |
| pairkernel | serial-repro-native | control (must agree) | r3w2pull0/r3w2 | ifx-natural | 63.38 -> 63.52 | 1.002 [0.997, 1.006] | live 21/21 |
| pairkernel | serial-repro-native | control (must agree) | r3w2pull0/r3w2 | ifx-expert | 31.7 -> 31.62 | 0.997 [0.995, 1.001] | live 21/21 |
| comoment3 | mt8-repro-native | W2-3 pull vs scatter (pull0) vs row slots (g35 = pull0 + prefer; prefer alone still pulls) | r3w2/r3w1 | blade-llvm | -- | -- | (arm not ok in a block) |
| comoment3 | mt8-repro-native | W2-3 pull vs scatter (pull0) vs row slots (g35 = pull0 + prefer; prefer alone still pulls) | r3w2/r3w1 | blade-cpp | 4.194 -> 3.091 | 0.737 [0.716, 0.760] | live 21/21 |
| comoment3 | mt8-repro-native | W2-3 pull vs scatter (pull0) vs row slots (g35 = pull0 + prefer; prefer alone still pulls) | r3w2/r3w1 | gfortran-natural | 88.98 -> 86.63 | 0.974 [0.917, 1.027] | live 21/21 |
| comoment3 | mt8-repro-native | W2-3 pull vs scatter (pull0) vs row slots (g35 = pull0 + prefer; prefer alone still pulls) | r3w2/r3w1 | gfortran-expert | 4.808 -> 4.799 | 0.998 [0.977, 1.010] | live 21/21 |
| comoment3 | mt8-repro-native | W2-3 pull vs scatter (pull0) vs row slots (g35 = pull0 + prefer; prefer alone still pulls) | r3w2/r3w1 | ifx-natural | 111.3 -> 113.1 | 1.016 [0.945, 1.096] | live 21/21 |
| comoment3 | mt8-repro-native | W2-3 pull vs scatter (pull0) vs row slots (g35 = pull0 + prefer; prefer alone still pulls) | r3w2/r3w1 | ifx-expert | 4.044 -> 4.033 | 0.997 [0.961, 1.022] | live 21/21 |
| comoment3 | mt8-repro-native | W2-3 pull vs scatter (pull0) vs row slots (g35 = pull0 + prefer; prefer alone still pulls) | r3w2prefer/r3w2 | blade-llvm | -- | -- | (arm not ok in a block) |
| comoment3 | mt8-repro-native | W2-3 pull vs scatter (pull0) vs row slots (g35 = pull0 + prefer; prefer alone still pulls) | r3w2prefer/r3w2 | blade-cpp | 3.091 -> 3.144 | 1.017 [0.975, 1.054] | ind 21/21 |
| comoment3 | mt8-repro-native | W2-3 pull vs scatter (pull0) vs row slots (g35 = pull0 + prefer; prefer alone still pulls) | r3w2prefer/r3w2 | gfortran-natural | 86.63 -> 88.5 | 1.022 [0.946, 1.080] | ind 21/21 |
| comoment3 | mt8-repro-native | W2-3 pull vs scatter (pull0) vs row slots (g35 = pull0 + prefer; prefer alone still pulls) | r3w2prefer/r3w2 | gfortran-expert | 4.799 -> 4.847 | 1.010 [0.995, 1.028] | ind 21/21 |
| comoment3 | mt8-repro-native | W2-3 pull vs scatter (pull0) vs row slots (g35 = pull0 + prefer; prefer alone still pulls) | r3w2prefer/r3w2 | ifx-natural | 113.1 -> 106.3 | 0.940 [0.892, 1.014] | ind 21/21 |
| comoment3 | mt8-repro-native | W2-3 pull vs scatter (pull0) vs row slots (g35 = pull0 + prefer; prefer alone still pulls) | r3w2prefer/r3w2 | ifx-expert | 4.033 -> 4.006 | 0.993 [0.969, 1.035] | ind 21/21 |
| comoment3 | mt8-repro-native | W2-3 pull vs scatter (pull0) vs row slots (g35 = pull0 + prefer; prefer alone still pulls) | r3w2pull0/r3w2 | blade-llvm | -- | -- | (arm not ok in a block) |
| comoment3 | mt8-repro-native | W2-3 pull vs scatter (pull0) vs row slots (g35 = pull0 + prefer; prefer alone still pulls) | r3w2pull0/r3w2 | blade-cpp | 3.091 -> 3.573 | 1.156 [1.108, 1.207] | ind 21/21 |
| comoment3 | mt8-repro-native | W2-3 pull vs scatter (pull0) vs row slots (g35 = pull0 + prefer; prefer alone still pulls) | r3w2pull0/r3w2 | gfortran-natural | 86.63 -> 90.83 | 1.049 [0.987, 1.120] | ind 21/21 |
| comoment3 | mt8-repro-native | W2-3 pull vs scatter (pull0) vs row slots (g35 = pull0 + prefer; prefer alone still pulls) | r3w2pull0/r3w2 | gfortran-expert | 4.799 -> 5.082 | 1.059 [1.024, 1.083] | ind 21/21 |
| comoment3 | mt8-repro-native | W2-3 pull vs scatter (pull0) vs row slots (g35 = pull0 + prefer; prefer alone still pulls) | r3w2pull0/r3w2 | ifx-natural | 113.1 -> 106.5 | 0.942 [0.901, 1.010] | ind 21/21 |
| comoment3 | mt8-repro-native | W2-3 pull vs scatter (pull0) vs row slots (g35 = pull0 + prefer; prefer alone still pulls) | r3w2pull0/r3w2 | ifx-expert | 4.033 -> 4.02 | 0.997 [0.976, 1.043] | ind 21/21 |
| comoment3 | mt8-repro-native | W2-3 pull vs scatter (pull0) vs row slots (g35 = pull0 + prefer; prefer alone still pulls) | r3w2g35/r3w2 | blade-llvm | -- | -- | (arm not ok in a block) |
| comoment3 | mt8-repro-native | W2-3 pull vs scatter (pull0) vs row slots (g35 = pull0 + prefer; prefer alone still pulls) | r3w2g35/r3w2 | blade-cpp | 3.091 -> 4.396 | 1.422 [1.383, 1.501] | ind 21/21 |
| comoment3 | mt8-repro-native | W2-3 pull vs scatter (pull0) vs row slots (g35 = pull0 + prefer; prefer alone still pulls) | r3w2g35/r3w2 | gfortran-natural | 86.63 -> 87.27 | 1.007 [0.962, 1.081] | ind 21/21 |
| comoment3 | mt8-repro-native | W2-3 pull vs scatter (pull0) vs row slots (g35 = pull0 + prefer; prefer alone still pulls) | r3w2g35/r3w2 | gfortran-expert | 4.799 -> 4.837 | 1.008 [0.995, 1.033] | ind 21/21 |
| comoment3 | mt8-repro-native | W2-3 pull vs scatter (pull0) vs row slots (g35 = pull0 + prefer; prefer alone still pulls) | r3w2g35/r3w2 | ifx-natural | 113.1 -> 116.2 | 1.028 [0.985, 1.097] | ind 21/21 |
| comoment3 | mt8-repro-native | W2-3 pull vs scatter (pull0) vs row slots (g35 = pull0 + prefer; prefer alone still pulls) | r3w2g35/r3w2 | ifx-expert | 4.033 -> 4.176 | 1.035 [1.005, 1.095] | ind 21/21 |
| comoment3 | mt8-repro-native | W2-3 pull vs scatter (pull0) vs row slots (g35 = pull0 + prefer; prefer alone still pulls) | r3w2g35/r3w2pull0 | blade-llvm | -- | -- | (arm not ok in a block) |
| comoment3 | mt8-repro-native | W2-3 pull vs scatter (pull0) vs row slots (g35 = pull0 + prefer; prefer alone still pulls) | r3w2g35/r3w2pull0 | blade-cpp | 3.573 -> 4.396 | 1.230 [1.185, 1.308] | ind 21/21 |
| comoment3 | mt8-repro-native | W2-3 pull vs scatter (pull0) vs row slots (g35 = pull0 + prefer; prefer alone still pulls) | r3w2g35/r3w2pull0 | gfortran-natural | 90.83 -> 87.27 | 0.961 [0.919, 1.026] | ind 21/21 |
| comoment3 | mt8-repro-native | W2-3 pull vs scatter (pull0) vs row slots (g35 = pull0 + prefer; prefer alone still pulls) | r3w2g35/r3w2pull0 | gfortran-expert | 5.082 -> 4.837 | 0.952 [0.936, 0.989] | ind 21/21 |
| comoment3 | mt8-repro-native | W2-3 pull vs scatter (pull0) vs row slots (g35 = pull0 + prefer; prefer alone still pulls) | r3w2g35/r3w2pull0 | ifx-natural | 106.5 -> 116.2 | 1.091 [1.021, 1.140] | ind 21/21 |
| comoment3 | mt8-repro-native | W2-3 pull vs scatter (pull0) vs row slots (g35 = pull0 + prefer; prefer alone still pulls) | r3w2g35/r3w2pull0 | ifx-expert | 4.02 -> 4.176 | 1.039 [0.995, 1.099] | ind 21/21 |
| comoment3 | serial-repro-native | control (must agree) | r3w2/r3w1 | blade-llvm | 8.865 -> 8.827 | 0.996 [0.992, 1.003] | live 21/21 |
| comoment3 | serial-repro-native | control (must agree) | r3w2/r3w1 | blade-cpp | 8.948 -> 8.907 | 0.995 [0.991, 1.002] | live 21/21 |
| comoment3 | serial-repro-native | control (must agree) | r3w2/r3w1 | gfortran-natural | 599.9 -> 600.1 | 1.000 [1.000, 1.002] | live 21/21 |
| comoment3 | serial-repro-native | control (must agree) | r3w2/r3w1 | gfortran-expert | 11.4 -> 11.38 | 0.998 [0.995, 1.008] | live 21/21 |
| comoment3 | serial-repro-native | control (must agree) | r3w2/r3w1 | ifx-natural | 718 -> 718.4 | 1.001 [0.999, 1.003] | live 21/21 |
| comoment3 | serial-repro-native | control (must agree) | r3w2/r3w1 | ifx-expert | 11.45 -> 11.39 | 0.995 [0.986, 1.003] | live 21/21 |
| comoment3 | serial-repro-native | control (must agree) | r3w2prefer/r3w2 | blade-llvm | 8.827 -> 8.867 | 1.005 [0.996, 1.011] | live 21/21 |
| comoment3 | serial-repro-native | control (must agree) | r3w2prefer/r3w2 | blade-cpp | 8.907 -> 8.948 | 1.005 [0.996, 1.007] | live 21/21 |
| comoment3 | serial-repro-native | control (must agree) | r3w2prefer/r3w2 | gfortran-natural | 600.1 -> 600.4 | 1.001 [0.999, 1.001] | live 21/21 |
| comoment3 | serial-repro-native | control (must agree) | r3w2prefer/r3w2 | gfortran-expert | 11.38 -> 11.41 | 1.003 [0.991, 1.009] | live 21/21 |
| comoment3 | serial-repro-native | control (must agree) | r3w2prefer/r3w2 | ifx-natural | 718.4 -> 718.5 | 1.000 [0.998, 1.002] | live 21/21 |
| comoment3 | serial-repro-native | control (must agree) | r3w2prefer/r3w2 | ifx-expert | 11.39 -> 11.44 | 1.004 [0.999, 1.018] | live 21/21 |
| comoment3 | serial-repro-native | control (must agree) | r3w2pull0/r3w2 | blade-llvm | 8.827 -> 8.907 | 1.009 [1.000, 1.017] | live 21/21 |
| comoment3 | serial-repro-native | control (must agree) | r3w2pull0/r3w2 | blade-cpp | 8.907 -> 8.979 | 1.008 [0.998, 1.019] | live 21/21 |
| comoment3 | serial-repro-native | control (must agree) | r3w2pull0/r3w2 | gfortran-natural | 600.1 -> 600.4 | 1.000 [0.999, 1.003] | live 21/21 |
| comoment3 | serial-repro-native | control (must agree) | r3w2pull0/r3w2 | gfortran-expert | 11.38 -> 11.5 | 1.011 [1.000, 1.021] | live 21/21 |
| comoment3 | serial-repro-native | control (must agree) | r3w2pull0/r3w2 | ifx-natural | 718.4 -> 718.5 | 1.000 [0.998, 1.002] | live 21/21 |
| comoment3 | serial-repro-native | control (must agree) | r3w2pull0/r3w2 | ifx-expert | 11.39 -> 11.47 | 1.007 [0.996, 1.030] | live 21/21 |
| antisym | serial-licensed-native | W2-0b anti_g per regime (serial: default 1 = antig1) | r3w2/r3w1 | blade-llvm | 27.45 -> 27.65 | 1.007 [1.001, 1.020] | ind 21/63 |
| antisym | serial-licensed-native | W2-0b anti_g per regime (serial: default 1 = antig1) | r3w2/r3w1 | blade-cpp | 28.32 -> 28.64 | 1.011 [1.002, 1.019] | ind 21/63 |
| antisym | serial-licensed-native | W2-0b anti_g per regime (serial: default 1 = antig1) | r3w2/r3w1 | gfortran-natural | 766.3 -> 771.1 | 1.006 [1.004, 1.009] | ind 21/63 |
| antisym | serial-licensed-native | W2-0b anti_g per regime (serial: default 1 = antig1) | r3w2/r3w1 | gfortran-expert | 30.45 -> 30.73 | 1.009 [0.995, 1.025] | ind 21/63 |
| antisym | serial-licensed-native | W2-0b anti_g per regime (serial: default 1 = antig1) | r3w2/r3w1 | ifx-natural | 329.7 -> 332 | 1.007 [1.003, 1.012] | ind 21/63 |
| antisym | serial-licensed-native | W2-0b anti_g per regime (serial: default 1 = antig1) | r3w2/r3w1 | ifx-expert | 28.88 -> 29.27 | 1.014 [1.004, 1.025] | ind 21/63 |
| antisym | serial-licensed-native | W2-0b anti_g per regime (serial: default 1 = antig1) | r3w2antig1/r3w2 | blade-llvm | 27.65 -> 27.91 | 1.009 [0.995, 1.023] | ind 63/63 |
| antisym | serial-licensed-native | W2-0b anti_g per regime (serial: default 1 = antig1) | r3w2antig1/r3w2 | blade-cpp | 28.64 -> 28.53 | 0.996 [0.987, 1.013] | ind 63/63 |
| antisym | serial-licensed-native | W2-0b anti_g per regime (serial: default 1 = antig1) | r3w2antig1/r3w2 | gfortran-natural | 771.1 -> 768 | 0.996 [0.994, 1.000] | ind 63/63 |
| antisym | serial-licensed-native | W2-0b anti_g per regime (serial: default 1 = antig1) | r3w2antig1/r3w2 | gfortran-expert | 30.73 -> 30.61 | 0.996 [0.986, 1.004] | ind 63/63 |
| antisym | serial-licensed-native | W2-0b anti_g per regime (serial: default 1 = antig1) | r3w2antig1/r3w2 | ifx-natural | 332 -> 332.3 | 1.001 [0.995, 1.007] | ind 63/63 |
| antisym | serial-licensed-native | W2-0b anti_g per regime (serial: default 1 = antig1) | r3w2antig1/r3w2 | ifx-expert | 29.27 -> 29.47 | 1.007 [0.990, 1.021] | ind 63/63 |
| antisym | mt8-licensed-native | W2-0b anti_g per regime (mt8: default auto vs 1) | r3w2/r3w1 | blade-llvm | -- | -- | (arm not ok in a block) |
| antisym | mt8-licensed-native | W2-0b anti_g per regime (mt8: default auto vs 1) | r3w2/r3w1 | blade-cpp | 9.495 -> 8.132 | 0.857 [0.792, 0.900] | ind 21/63 |
| antisym | mt8-licensed-native | W2-0b anti_g per regime (mt8: default auto vs 1) | r3w2/r3w1 | gfortran-natural | 114.6 -> 108.4 | 0.946 [0.914, 0.975] | ind 21/63 |
| antisym | mt8-licensed-native | W2-0b anti_g per regime (mt8: default auto vs 1) | r3w2/r3w1 | gfortran-expert | 10.94 -> 9.381 | 0.857 [0.820, 0.913] | ind 21/63 |
| antisym | mt8-licensed-native | W2-0b anti_g per regime (mt8: default auto vs 1) | r3w2/r3w1 | ifx-natural | 75.36 -> 64.25 | 0.853 [0.777, 0.902] | ind 21/63 |
| antisym | mt8-licensed-native | W2-0b anti_g per regime (mt8: default auto vs 1) | r3w2/r3w1 | ifx-expert | 11.65 -> 9.859 | 0.846 [0.775, 0.898] | ind 20/63 |
| antisym | mt8-licensed-native | W2-0b anti_g per regime (mt8: default auto vs 1) | r3w2antig1/r3w2 | blade-llvm | -- | -- | (arm not ok in a block) |
| antisym | mt8-licensed-native | W2-0b anti_g per regime (mt8: default auto vs 1) | r3w2antig1/r3w2 | blade-cpp | 8.132 -> 8.447 | 1.039 [0.992, 1.068] | ind 63/63 |
| antisym | mt8-licensed-native | W2-0b anti_g per regime (mt8: default auto vs 1) | r3w2antig1/r3w2 | gfortran-natural | 108.4 -> 105.5 | 0.974 [0.953, 0.994] | ind 63/63 |
| antisym | mt8-licensed-native | W2-0b anti_g per regime (mt8: default auto vs 1) | r3w2antig1/r3w2 | gfortran-expert | 9.381 -> 9.255 | 0.987 [0.949, 1.016] | ind 63/63 |
| antisym | mt8-licensed-native | W2-0b anti_g per regime (mt8: default auto vs 1) | r3w2antig1/r3w2 | ifx-natural | 64.25 -> 64.62 | 1.006 [0.910, 1.080] | ind 63/63 |
| antisym | mt8-licensed-native | W2-0b anti_g per regime (mt8: default auto vs 1) | r3w2antig1/r3w2 | ifx-expert | 9.859 -> 9.627 | 0.976 [0.950, 1.008] | ind 63/63 |
| saxpy | serial-repro-native | W2-4 legs (target cell) | r3w2/r3w1 | blade-llvm | 10.79 -> 9.588 | 0.889 [0.882, 0.920] | live 21/21 |
| saxpy | serial-repro-native | W2-4 legs (target cell) | r3w2/r3w1 | blade-cpp | 15.52 -> 14.33 | 0.923 [0.917, 0.930] | live 21/21 |
| saxpy | serial-repro-native | W2-4 legs (target cell) | r3w2/r3w1 | gfortran-natural | 16.6 -> 16.6 | 1.000 [0.992, 1.012] | live 21/21 |
| saxpy | serial-repro-native | W2-4 legs (target cell) | r3w2/r3w1 | gfortran-expert | 15.19 -> 15.19 | 1.000 [0.995, 1.005] | live 21/21 |
| saxpy | serial-repro-native | W2-4 legs (target cell) | r3w2/r3w1 | ifx-natural | 12.86 -> 13.11 | 1.019 [1.009, 1.030] | live 21/21 |
| saxpy | serial-repro-native | W2-4 legs (target cell) | r3w2/r3w1 | ifx-expert | 9.577 -> 9.531 | 0.995 [0.989, 1.003] | live 21/21 |
| saxpy | serial-repro-native | W2-4 legs (target cell) | r3w2legs0/r3w2 | blade-llvm | 9.588 -> 11.18 | 1.166 [1.129, 1.181] | live 21/21 |
| saxpy | serial-repro-native | W2-4 legs (target cell) | r3w2legs0/r3w2 | blade-cpp | 14.33 -> 15.76 | 1.100 [1.089, 1.147] | live 21/21 |
| saxpy | serial-repro-native | W2-4 legs (target cell) | r3w2legs0/r3w2 | gfortran-natural | 16.6 -> 16.68 | 1.004 [0.995, 1.031] | live 21/21 |
| saxpy | serial-repro-native | W2-4 legs (target cell) | r3w2legs0/r3w2 | gfortran-expert | 15.19 -> 15.38 | 1.012 [1.006, 1.026] | live 21/21 |
| saxpy | serial-repro-native | W2-4 legs (target cell) | r3w2legs0/r3w2 | ifx-natural | 13.11 -> 13.06 | 0.996 [0.986, 1.034] | live 21/21 |
| saxpy | serial-repro-native | W2-4 legs (target cell) | r3w2legs0/r3w2 | ifx-expert | 9.531 -> 9.837 | 1.032 [1.008, 1.079] | live 21/21 |
| saxpy | serial-licensed-native | W2-4 legs (target cell) | r3w2/r3w1 | blade-llvm | 10.58 -> 9.448 | 0.893 [0.888, 0.901] | live 21/21 |
| saxpy | serial-licensed-native | W2-4 legs (target cell) | r3w2/r3w1 | blade-cpp | 15.46 -> 14.65 | 0.947 [0.937, 0.953] | live 21/21 |
| saxpy | serial-licensed-native | W2-4 legs (target cell) | r3w2/r3w1 | gfortran-natural | 16.75 -> 16.8 | 1.003 [0.990, 1.011] | live 21/21 |
| saxpy | serial-licensed-native | W2-4 legs (target cell) | r3w2/r3w1 | gfortran-expert | 14.71 -> 14.61 | 0.993 [0.992, 1.002] | live 21/21 |
| saxpy | serial-licensed-native | W2-4 legs (target cell) | r3w2/r3w1 | ifx-natural | 12.98 -> 12.92 | 0.995 [0.988, 1.005] | live 21/21 |
| saxpy | serial-licensed-native | W2-4 legs (target cell) | r3w2/r3w1 | ifx-expert | 10.48 -> 10.52 | 1.003 [0.988, 1.010] | live 21/21 |
| saxpy | serial-licensed-native | W2-4 legs (target cell) | r3w2legs0/r3w2 | blade-llvm | 9.448 -> 10.84 | 1.148 [1.125, 1.193] | ind 21/21 |
| saxpy | serial-licensed-native | W2-4 legs (target cell) | r3w2legs0/r3w2 | blade-cpp | 14.65 -> 15.77 | 1.077 [1.064, 1.098] | ind 21/21 |
| saxpy | serial-licensed-native | W2-4 legs (target cell) | r3w2legs0/r3w2 | gfortran-natural | 16.8 -> 16.96 | 1.009 [0.984, 1.022] | ind 21/21 |
| saxpy | serial-licensed-native | W2-4 legs (target cell) | r3w2legs0/r3w2 | gfortran-expert | 14.61 -> 14.74 | 1.009 [0.998, 1.025] | ind 21/21 |
| saxpy | serial-licensed-native | W2-4 legs (target cell) | r3w2legs0/r3w2 | ifx-natural | 12.92 -> 13.48 | 1.044 [1.010, 1.065] | ind 21/21 |
| saxpy | serial-licensed-native | W2-4 legs (target cell) | r3w2legs0/r3w2 | ifx-expert | 10.52 -> 10.84 | 1.031 [1.012, 1.053] | ind 21/21 |
| saxpy | mt8-repro-native | W2-4 decline control (threaded producer) | r3w2/r3w1 | blade-llvm | -- | -- | (arm not ok in a block) |
| saxpy | mt8-repro-native | W2-4 decline control (threaded producer) | r3w2/r3w1 | blade-cpp | 10.89 -> 10.87 | 0.998 [0.990, 1.004] | live 21/21 |
| saxpy | mt8-repro-native | W2-4 decline control (threaded producer) | r3w2/r3w1 | gfortran-natural | 9.712 -> 9.748 | 1.004 [0.982, 1.031] | live 21/21 |
| saxpy | mt8-repro-native | W2-4 decline control (threaded producer) | r3w2/r3w1 | gfortran-expert | 8.944 -> 8.92 | 0.997 [0.974, 1.037] | live 21/21 |
| saxpy | mt8-repro-native | W2-4 decline control (threaded producer) | r3w2/r3w1 | ifx-natural | 9.889 -> 9.843 | 0.995 [0.982, 1.004] | live 21/21 |
| saxpy | mt8-repro-native | W2-4 decline control (threaded producer) | r3w2/r3w1 | ifx-expert | 9.353 -> 9.41 | 1.006 [0.986, 1.013] | live 21/21 |
| saxpy | mt8-repro-native | W2-4 decline control (threaded producer) | r3w2legs0/r3w2 | blade-llvm | -- | -- | (arm not ok in a block) |
| saxpy | mt8-repro-native | W2-4 decline control (threaded producer) | r3w2legs0/r3w2 | blade-cpp | 10.87 -> 10.93 | 1.005 [0.993, 1.015] | live 21/21 |
| saxpy | mt8-repro-native | W2-4 decline control (threaded producer) | r3w2legs0/r3w2 | gfortran-natural | 9.748 -> 9.701 | 0.995 [0.968, 1.014] | live 21/21 |
| saxpy | mt8-repro-native | W2-4 decline control (threaded producer) | r3w2legs0/r3w2 | gfortran-expert | 8.92 -> 8.927 | 1.001 [0.962, 1.020] | live 21/21 |
| saxpy | mt8-repro-native | W2-4 decline control (threaded producer) | r3w2legs0/r3w2 | ifx-natural | 9.843 -> 9.88 | 1.004 [0.994, 1.020] | live 21/21 |
| saxpy | mt8-repro-native | W2-4 decline control (threaded producer) | r3w2legs0/r3w2 | ifx-expert | 9.41 -> 9.399 | 0.999 [0.991, 1.016] | live 21/21 |
| saxpy | mt8-licensed-native | W2-4 decline control (threaded producer) | r3w2/r3w1 | blade-llvm | -- | -- | (arm not ok in a block) |
| saxpy | mt8-licensed-native | W2-4 decline control (threaded producer) | r3w2/r3w1 | blade-cpp | 8.952 -> 8.981 | 1.003 [0.983, 1.034] | ind 21/21 |
| saxpy | mt8-licensed-native | W2-4 decline control (threaded producer) | r3w2/r3w1 | gfortran-natural | 9.672 -> 9.888 | 1.022 [0.980, 1.036] | ind 21/21 |
| saxpy | mt8-licensed-native | W2-4 decline control (threaded producer) | r3w2/r3w1 | gfortran-expert | 8.446 -> 8.437 | 0.999 [0.987, 1.021] | ind 21/21 |
| saxpy | mt8-licensed-native | W2-4 decline control (threaded producer) | r3w2/r3w1 | ifx-natural | 9.851 -> 9.89 | 1.004 [0.991, 1.012] | ind 21/21 |
| saxpy | mt8-licensed-native | W2-4 decline control (threaded producer) | r3w2/r3w1 | ifx-expert | 8.898 -> 8.849 | 0.995 [0.982, 1.012] | ind 21/21 |
| saxpy | mt8-licensed-native | W2-4 decline control (threaded producer) | r3w2legs0/r3w2 | blade-llvm | -- | -- | (arm not ok in a block) |
| saxpy | mt8-licensed-native | W2-4 decline control (threaded producer) | r3w2legs0/r3w2 | blade-cpp | 8.981 -> 9.041 | 1.007 [0.971, 1.049] | ind 21/21 |
| saxpy | mt8-licensed-native | W2-4 decline control (threaded producer) | r3w2legs0/r3w2 | gfortran-natural | 9.888 -> 9.751 | 0.986 [0.973, 1.017] | ind 21/21 |
| saxpy | mt8-licensed-native | W2-4 decline control (threaded producer) | r3w2legs0/r3w2 | gfortran-expert | 8.437 -> 8.549 | 1.013 [0.988, 1.027] | ind 21/21 |
| saxpy | mt8-licensed-native | W2-4 decline control (threaded producer) | r3w2legs0/r3w2 | ifx-natural | 9.89 -> 9.942 | 1.005 [0.997, 1.015] | ind 21/21 |
| saxpy | mt8-licensed-native | W2-4 decline control (threaded producer) | r3w2legs0/r3w2 | ifx-expert | 8.849 -> 8.947 | 1.011 [0.989, 1.036] | ind 21/21 |
| haloadjoint | serial-licensed-native | new kernel N02 | r3w2/r3w1 | blade-llvm | 1.73 -> 1.741 | 1.006 [0.991, 1.013] | live 21/21 |
| haloadjoint | serial-licensed-native | new kernel N02 | r3w2/r3w1 | blade-cpp | 2.122 -> 2.122 | 1.000 [0.993, 1.012] | live 21/21 |
| haloadjoint | serial-licensed-native | new kernel N02 | r3w2/r3w1 | gfortran-natural | 1.268 -> 1.269 | 1.001 [0.991, 1.006] | live 21/21 |
| haloadjoint | serial-licensed-native | new kernel N02 | r3w2/r3w1 | gfortran-expert | 0.905 -> 0.9072 | 1.002 [0.989, 1.008] | live 21/21 |
| haloadjoint | serial-licensed-native | new kernel N02 | r3w2/r3w1 | ifx-natural | 1.864 -> 1.856 | 0.996 [0.991, 1.005] | live 21/21 |
| haloadjoint | serial-licensed-native | new kernel N02 | r3w2/r3w1 | ifx-expert | 1.13 -> 1.124 | 0.995 [0.987, 1.000] | live 21/21 |
| haloadjoint | mt8-licensed-native | new kernel N02 | r3w2/r3w1 | blade-llvm | -- | -- | (arm not ok in a block) |
| haloadjoint | mt8-licensed-native | new kernel N02 | r3w2/r3w1 | blade-cpp | 2.984 -> 2.967 | 0.994 [0.973, 1.024] | live 21/21 |
| haloadjoint | mt8-licensed-native | new kernel N02 | r3w2/r3w1 | gfortran-natural | 1.733 -> 1.715 | 0.989 [0.966, 1.026] | live 21/21 |
| haloadjoint | mt8-licensed-native | new kernel N02 | r3w2/r3w1 | gfortran-expert | 1.225 -> 1.24 | 1.012 [0.947, 1.054] | live 21/21 |
| haloadjoint | mt8-licensed-native | new kernel N02 | r3w2/r3w1 | ifx-natural | 2.014 -> 2.002 | 0.995 [0.969, 1.026] | live 21/21 |
| haloadjoint | mt8-licensed-native | new kernel N02 | r3w2/r3w1 | ifx-expert | 1.852 -> 1.883 | 1.017 [0.981, 1.028] | live 21/21 |
| haloadjoint | serial-repro-native | new kernel N02 | r3w2/r3w1 | blade-llvm | 1.83 -> 1.825 | 0.997 [0.991, 1.008] | live 21/21 |
| haloadjoint | serial-repro-native | new kernel N02 | r3w2/r3w1 | blade-cpp | 2.227 -> 2.229 | 1.001 [0.994, 1.015] | live 21/21 |
| haloadjoint | serial-repro-native | new kernel N02 | r3w2/r3w1 | gfortran-natural | 1.271 -> 1.267 | 0.997 [0.987, 1.003] | live 21/21 |
| haloadjoint | serial-repro-native | new kernel N02 | r3w2/r3w1 | gfortran-expert | 0.9446 -> 0.9452 | 1.001 [0.993, 1.011] | live 21/21 |
| haloadjoint | serial-repro-native | new kernel N02 | r3w2/r3w1 | ifx-natural | 1.86 -> 1.853 | 0.996 [0.991, 1.006] | live 21/21 |
| haloadjoint | serial-repro-native | new kernel N02 | r3w2/r3w1 | ifx-expert | 1.198 -> 1.199 | 1.000 [0.988, 1.006] | live 21/21 |
| haloadjoint | mt8-repro-native | new kernel N02 | r3w2/r3w1 | blade-llvm | -- | -- | (arm not ok in a block) |
| haloadjoint | mt8-repro-native | new kernel N02 | r3w2/r3w1 | blade-cpp | 2.949 -> 2.935 | 0.995 [0.961, 1.024] | ind 21/21 |
| haloadjoint | mt8-repro-native | new kernel N02 | r3w2/r3w1 | gfortran-natural | 1.846 -> 1.794 | 0.972 [0.944, 1.011] | ind 21/21 |
| haloadjoint | mt8-repro-native | new kernel N02 | r3w2/r3w1 | gfortran-expert | 1.279 -> 1.279 | 1.000 [0.894, 1.053] | ind 21/21 |
| haloadjoint | mt8-repro-native | new kernel N02 | r3w2/r3w1 | ifx-natural | 2.06 -> 2.011 | 0.976 [0.949, 1.021] | ind 21/21 |
| haloadjoint | mt8-repro-native | new kernel N02 | r3w2/r3w1 | ifx-expert | 1.91 -> 1.901 | 0.996 [0.965, 1.023] | ind 21/21 |
| stencil | mt8-repro-native | control (row slots unaffected); A4 retention arm | r3w2/r3w1 | blade-llvm | -- | -- | (arm not ok in a block) |
| stencil | mt8-repro-native | control (row slots unaffected); A4 retention arm | r3w2/r3w1 | blade-cpp | 8.402 -> 8.427 | 1.003 [0.983, 1.030] | live 21/21 |
| stencil | mt8-repro-native | control (row slots unaffected); A4 retention arm | r3w2/r3w1 | gfortran-natural | 8.54 -> 8.622 | 1.010 [0.990, 1.026] | live 21/21 |
| stencil | mt8-repro-native | control (row slots unaffected); A4 retention arm | r3w2/r3w1 | gfortran-expert | 7.789 -> 7.737 | 0.993 [0.979, 1.013] | live 21/21 |
| stencil | mt8-repro-native | control (row slots unaffected); A4 retention arm | r3w2/r3w1 | ifx-natural | 9.71 -> 9.855 | 1.015 [0.991, 1.030] | live 21/21 |
| stencil | mt8-repro-native | control (row slots unaffected); A4 retention arm | r3w2/r3w1 | ifx-expert | 8.075 -> 8.135 | 1.007 [0.993, 1.027] | live 21/21 |
| stencil | mt8-repro-native | control (row slots unaffected); A4 retention arm | r3w2retain/r3w2 | blade-llvm | -- | -- | (arm not ok in a block) |
| stencil | mt8-repro-native | control (row slots unaffected); A4 retention arm | r3w2retain/r3w2 | blade-cpp | 8.427 -> 7.801 | 0.926 [0.899, 0.943] | live 21/21 |
| stencil | mt8-repro-native | control (row slots unaffected); A4 retention arm | r3w2retain/r3w2 | gfortran-natural | 8.622 -> 8.583 | 0.995 [0.983, 1.019] | live 21/21 |
| stencil | mt8-repro-native | control (row slots unaffected); A4 retention arm | r3w2retain/r3w2 | gfortran-expert | 7.737 -> 7.769 | 1.004 [0.984, 1.025] | live 21/21 |
| stencil | mt8-repro-native | control (row slots unaffected); A4 retention arm | r3w2retain/r3w2 | ifx-natural | 9.855 -> 9.794 | 0.994 [0.976, 1.010] | live 21/21 |
| stencil | mt8-repro-native | control (row slots unaffected); A4 retention arm | r3w2retain/r3w2 | ifx-expert | 8.135 -> 8.23 | 1.012 [0.994, 1.029] | live 21/21 |
| stencil | mt8-licensed-native | A4 retention arm (24 MB lap) | r3w2/r3w1 | blade-llvm | -- | -- | (arm not ok in a block) |
| stencil | mt8-licensed-native | A4 retention arm (24 MB lap) | r3w2/r3w1 | blade-cpp | 8.666 -> 8.601 | 0.992 [0.978, 1.014] | live 21/21 |
| stencil | mt8-licensed-native | A4 retention arm (24 MB lap) | r3w2/r3w1 | gfortran-natural | 8.603 -> 8.546 | 0.993 [0.960, 1.005] | live 21/21 |
| stencil | mt8-licensed-native | A4 retention arm (24 MB lap) | r3w2/r3w1 | gfortran-expert | 7.795 -> 7.757 | 0.995 [0.988, 1.008] | live 21/21 |
| stencil | mt8-licensed-native | A4 retention arm (24 MB lap) | r3w2/r3w1 | ifx-natural | 9.864 -> 9.764 | 0.990 [0.975, 1.005] | live 21/21 |
| stencil | mt8-licensed-native | A4 retention arm (24 MB lap) | r3w2/r3w1 | ifx-expert | 8.256 -> 8.296 | 1.005 [0.988, 1.030] | live 21/21 |
| stencil | mt8-licensed-native | A4 retention arm (24 MB lap) | r3w2retain/r3w2 | blade-llvm | -- | -- | (arm not ok in a block) |
| stencil | mt8-licensed-native | A4 retention arm (24 MB lap) | r3w2retain/r3w2 | blade-cpp | 8.601 -> 7.916 | 0.920 [0.897, 0.969] | ind 21/21 |
| stencil | mt8-licensed-native | A4 retention arm (24 MB lap) | r3w2retain/r3w2 | gfortran-natural | 8.546 -> 8.682 | 1.016 [0.999, 1.047] | ind 21/21 |
| stencil | mt8-licensed-native | A4 retention arm (24 MB lap) | r3w2retain/r3w2 | gfortran-expert | 7.757 -> 7.898 | 1.018 [0.999, 1.053] | ind 21/21 |
| stencil | mt8-licensed-native | A4 retention arm (24 MB lap) | r3w2retain/r3w2 | ifx-natural | 9.764 -> 9.888 | 1.013 [1.000, 1.037] | ind 21/21 |
| stencil | mt8-licensed-native | A4 retention arm (24 MB lap) | r3w2retain/r3w2 | ifx-expert | 8.296 -> 8.263 | 0.996 [0.980, 1.023] | ind 21/21 |
| recur | mt8-licensed-native | A4 retention arm (48 MB trajectory) | r3w2/r3w1 | blade-llvm | -- | -- | (arm not ok in a block) |
| recur | mt8-licensed-native | A4 retention arm (48 MB trajectory) | r3w2/r3w1 | blade-cpp | 9.23 -> 9.127 | 0.989 [0.946, 1.037] | ind 21/21 |
| recur | mt8-licensed-native | A4 retention arm (48 MB trajectory) | r3w2/r3w1 | gfortran-natural | 71.19 -> 71.95 | 1.011 [1.000, 1.026] | ind 21/21 |
| recur | mt8-licensed-native | A4 retention arm (48 MB trajectory) | r3w2/r3w1 | gfortran-expert | 6.79 -> 6.824 | 1.005 [0.964, 1.057] | ind 21/21 |
| recur | mt8-licensed-native | A4 retention arm (48 MB trajectory) | r3w2/r3w1 | ifx-natural | 14.66 -> 14.8 | 1.010 [0.990, 1.031] | ind 21/21 |
| recur | mt8-licensed-native | A4 retention arm (48 MB trajectory) | r3w2/r3w1 | ifx-expert | 7.338 -> 7.279 | 0.992 [0.970, 1.063] | ind 21/21 |
| recur | mt8-licensed-native | A4 retention arm (48 MB trajectory) | r3w2retain/r3w2 | blade-llvm | -- | -- | (arm not ok in a block) |
| recur | mt8-licensed-native | A4 retention arm (48 MB trajectory) | r3w2retain/r3w2 | blade-cpp | 9.127 -> 6.764 | 0.741 [0.724, 0.784] | ind 21/21 |
| recur | mt8-licensed-native | A4 retention arm (48 MB trajectory) | r3w2retain/r3w2 | gfortran-natural | 71.95 -> 71.29 | 0.991 [0.971, 1.008] | ind 21/21 |
| recur | mt8-licensed-native | A4 retention arm (48 MB trajectory) | r3w2retain/r3w2 | gfortran-expert | 6.824 -> 6.814 | 0.999 [0.951, 1.033] | ind 21/21 |
| recur | mt8-licensed-native | A4 retention arm (48 MB trajectory) | r3w2retain/r3w2 | ifx-natural | 14.8 -> 14.82 | 1.002 [0.972, 1.028] | ind 21/21 |
| recur | mt8-licensed-native | A4 retention arm (48 MB trajectory) | r3w2retain/r3w2 | ifx-expert | 7.279 -> 7.208 | 0.990 [0.938, 1.026] | ind 21/21 |
| recur | mt8-repro-native | A4 retention arm (48 MB trajectory) | r3w2/r3w1 | blade-llvm | -- | -- | (arm not ok in a block) |
| recur | mt8-repro-native | A4 retention arm (48 MB trajectory) | r3w2/r3w1 | blade-cpp | 12.34 -> 12.38 | 1.004 [0.929, 1.036] | ind 21/21 |
| recur | mt8-repro-native | A4 retention arm (48 MB trajectory) | r3w2/r3w1 | gfortran-natural | 73.32 -> 73.52 | 1.003 [0.982, 1.015] | ind 21/21 |
| recur | mt8-repro-native | A4 retention arm (48 MB trajectory) | r3w2/r3w1 | gfortran-expert | 7.063 -> 7.654 | 1.084 [0.995, 1.144] | ind 21/21 |
| recur | mt8-repro-native | A4 retention arm (48 MB trajectory) | r3w2/r3w1 | ifx-natural | 15.14 -> 15.46 | 1.021 [0.948, 1.064] | ind 21/21 |
| recur | mt8-repro-native | A4 retention arm (48 MB trajectory) | r3w2/r3w1 | ifx-expert | 7.443 -> 7.784 | 1.046 [1.006, 1.163] | ind 21/21 |
| recur | mt8-repro-native | A4 retention arm (48 MB trajectory) | r3w2retain/r3w2 | blade-llvm | -- | -- | (arm not ok in a block) |
| recur | mt8-repro-native | A4 retention arm (48 MB trajectory) | r3w2retain/r3w2 | blade-cpp | 12.38 -> 9.524 | 0.769 [0.754, 0.789] | ind 21/21 |
| recur | mt8-repro-native | A4 retention arm (48 MB trajectory) | r3w2retain/r3w2 | gfortran-natural | 73.52 -> 71.65 | 0.974 [0.966, 0.991] | ind 21/21 |
| recur | mt8-repro-native | A4 retention arm (48 MB trajectory) | r3w2retain/r3w2 | gfortran-expert | 7.654 -> 6.951 | 0.908 [0.863, 0.963] | ind 21/21 |
| recur | mt8-repro-native | A4 retention arm (48 MB trajectory) | r3w2retain/r3w2 | ifx-natural | 15.46 -> 14.8 | 0.957 [0.924, 0.999] | ind 21/21 |
| recur | mt8-repro-native | A4 retention arm (48 MB trajectory) | r3w2retain/r3w2 | ifx-expert | 7.784 -> 7.485 | 0.962 [0.875, 0.993] | ind 21/21 |
| gram | serial-licensed-native | control | r3w2/r3w1 | blade-llvm | 56.04 -> 56.32 | 1.005 [1.000, 1.046] | ind 11/11 |
| gram | serial-licensed-native | control | r3w2/r3w1 | blade-cpp | 57.39 -> 57.96 | 1.010 [0.997, 1.028] | ind 11/11 |
| gram | serial-licensed-native | control | r3w2/r3w1 | gfortran-natural | 453.2 -> 483.2 | 1.066 [0.994, 1.117] | ind 11/11 |
| gram | serial-licensed-native | control | r3w2/r3w1 | gfortran-expert | 76.06 -> 76.55 | 1.006 [0.994, 1.027] | ind 11/11 |
| gram | serial-licensed-native | control | r3w2/r3w1 | ifx-natural | 568.4 -> 567.8 | 0.999 [0.998, 1.001] | ind 11/11 |
| gram | serial-licensed-native | control | r3w2/r3w1 | ifx-expert | 68.91 -> 68.8 | 0.998 [0.993, 1.001] | ind 11/11 |

## 2. Arm medians per block (ms)

| kernel | regime | mirror | block | blade-llvm | blade-cpp | gfortran-natural | gfortran-expert | ifx-natural | ifx-expert |
|---|---|---|---|---|---|---|---|---|---|
| pairkernel | mt8-repro-native | r3w1 | live | serial-only: LLVM lane has no OpenMP | 9.027 | 13.41 | 6.149 | 15.25 | 6.19 |
| pairkernel | mt8-repro-native | r3w2 | live | serial-only: LLVM lane has no OpenMP | 7.636 | 13.46 | 6.129 | 15.8 | 6.151 |
| pairkernel | mt8-repro-native | r3w2prefer | indicative | serial-only: LLVM lane has no OpenMP | 9.006 | 13.5 | 6.109 | 15.68 | 6.186 |
| pairkernel | mt8-repro-native | r3w2pull0 | live | serial-only: LLVM lane has no OpenMP | 7.588 | 13.46 | 6.254 | 15.17 | 6.044 |
| pairkernel | serial-repro-native | r3w1 | live | 29.73 | 29.63 | 60.21 | 34.06 | 63.31 | 31.7 |
| pairkernel | serial-repro-native | r3w2 | live | 29.77 | 29.67 | 60.22 | 34.09 | 63.38 | 31.7 |
| pairkernel | serial-repro-native | r3w2prefer | live | 29.68 | 29.6 | 60.08 | 34.18 | 63.37 | 31.65 |
| pairkernel | serial-repro-native | r3w2pull0 | live | 29.68 | 29.63 | 60.17 | 34.09 | 63.52 | 31.62 |
| comoment3 | mt8-repro-native | r3w1 | live | serial-only: LLVM lane has no OpenMP | 4.194 | 88.98 | 4.808 | 111.3 | 4.044 |
| comoment3 | mt8-repro-native | r3w2 | live | serial-only: LLVM lane has no OpenMP | 3.091 | 86.63 | 4.799 | 113.1 | 4.033 |
| comoment3 | mt8-repro-native | r3w2prefer | indicative | serial-only: LLVM lane has no OpenMP | 3.144 | 88.5 | 4.847 | 106.3 | 4.006 |
| comoment3 | mt8-repro-native | r3w2pull0 | indicative | serial-only: LLVM lane has no OpenMP | 3.573 | 90.83 | 5.082 | 106.5 | 4.02 |
| comoment3 | mt8-repro-native | r3w2g35 | indicative | serial-only: LLVM lane has no OpenMP | 4.396 | 87.27 | 4.837 | 116.2 | 4.176 |
| comoment3 | serial-repro-native | r3w1 | live | 8.865 | 8.948 | 599.9 | 11.4 | 718 | 11.45 |
| comoment3 | serial-repro-native | r3w2 | live | 8.827 | 8.907 | 600.1 | 11.38 | 718.4 | 11.39 |
| comoment3 | serial-repro-native | r3w2prefer | live | 8.867 | 8.948 | 600.4 | 11.41 | 718.5 | 11.44 |
| comoment3 | serial-repro-native | r3w2pull0 | live | 8.907 | 8.979 | 600.4 | 11.5 | 718.5 | 11.47 |
| antisym | serial-licensed-native | r3w1 | live | 27.45 | 28.32 | 766.3 | 30.45 | 329.7 | 28.88 |
| antisym | serial-licensed-native | r3w2 | live | 27.8 | 28.78 | 772.6 | 31.06 | 332.8 | 29.74 |
| antisym | serial-licensed-native | r3w2 | indicative | 27.94 | 28.87 | 771.7 | 30.86 | 336.2 | 29.64 |
| antisym | serial-licensed-native | r3w2 | live | 27.29 | 28.2 | 764.3 | 30.21 | 328.4 | 28.88 |
| antisym | serial-licensed-native | r3w2antig1 | live | 28.44 | 29.46 | 774.9 | 31.37 | 335 | 29.99 |
| antisym | serial-licensed-native | r3w2antig1 | live | 27.27 | 28.03 | 765.2 | 30.24 | 329 | 29.08 |
| antisym | serial-licensed-native | r3w2antig1 | live | 28.2 | 28.77 | 767.8 | 30.58 | 334.1 | 29.47 |
| antisym | mt8-licensed-native | r3w1 | indicative | serial-only: LLVM lane has no OpenMP | 9.495 | 114.6 | 10.94 | 75.36 | 11.65 |
| antisym | mt8-licensed-native | r3w2 | indicative | serial-only: LLVM lane has no OpenMP | 8.562 | 109 | 9.861 | 65.47 | 10.22 |
| antisym | mt8-licensed-native | r3w2 | indicative | serial-only: LLVM lane has no OpenMP | 8.27 | 108.6 | 9.695 | 65.43 | 10.12 |
| antisym | mt8-licensed-native | r3w2 | indicative | serial-only: LLVM lane has no OpenMP | 7.774 | 106.6 | 9.123 | 60.43 | 9.328 |
| antisym | mt8-licensed-native | r3w2antig1 | indicative | serial-only: LLVM lane has no OpenMP | 8.669 | 107.1 | 9.76 | 65.6 | 9.996 |
| antisym | mt8-licensed-native | r3w2antig1 | live | serial-only: LLVM lane has no OpenMP | 8.239 | 104.3 | 9.107 | 68.04 | 9.466 |
| antisym | mt8-licensed-native | r3w2antig1 | indicative | serial-only: LLVM lane has no OpenMP | 8.457 | 104.2 | 9.2 | 55.59 | 9.729 |
| saxpy | serial-repro-native | r3w1 | live | 10.79 | 15.52 | 16.6 | 15.19 | 12.86 | 9.577 |
| saxpy | serial-repro-native | r3w2 | live | 9.588 | 14.33 | 16.6 | 15.19 | 13.11 | 9.531 |
| saxpy | serial-repro-native | r3w2legs0 | live | 11.18 | 15.76 | 16.68 | 15.38 | 13.06 | 9.837 |
| saxpy | serial-licensed-native | r3w1 | live | 10.58 | 15.46 | 16.75 | 14.71 | 12.98 | 10.48 |
| saxpy | serial-licensed-native | r3w2 | live | 9.448 | 14.65 | 16.8 | 14.61 | 12.92 | 10.52 |
| saxpy | serial-licensed-native | r3w2legs0 | indicative | 10.84 | 15.77 | 16.96 | 14.74 | 13.48 | 10.84 |
| saxpy | mt8-repro-native | r3w1 | live | serial-only: LLVM lane has no OpenMP | 10.89 | 9.712 | 8.944 | 9.889 | 9.353 |
| saxpy | mt8-repro-native | r3w2 | live | serial-only: LLVM lane has no OpenMP | 10.87 | 9.748 | 8.92 | 9.843 | 9.41 |
| saxpy | mt8-repro-native | r3w2legs0 | live | serial-only: LLVM lane has no OpenMP | 10.93 | 9.701 | 8.927 | 9.88 | 9.399 |
| saxpy | mt8-licensed-native | r3w1 | live | serial-only: LLVM lane has no OpenMP | 8.952 | 9.672 | 8.446 | 9.851 | 8.898 |
| saxpy | mt8-licensed-native | r3w2 | indicative | serial-only: LLVM lane has no OpenMP | 8.981 | 9.888 | 8.437 | 9.89 | 8.849 |
| saxpy | mt8-licensed-native | r3w2legs0 | live | serial-only: LLVM lane has no OpenMP | 9.041 | 9.751 | 8.549 | 9.942 | 8.947 |
| haloadjoint | serial-licensed-native | r3w1 | live | 1.73 | 2.122 | 1.268 | 0.905 | 1.864 | 1.13 |
| haloadjoint | serial-licensed-native | r3w2 | live | 1.741 | 2.122 | 1.269 | 0.9072 | 1.856 | 1.124 |
| haloadjoint | mt8-licensed-native | r3w1 | live | serial-only: LLVM lane has no OpenMP | 2.984 | 1.733 | 1.225 | 2.014 | 1.852 |
| haloadjoint | mt8-licensed-native | r3w2 | live | serial-only: LLVM lane has no OpenMP | 2.967 | 1.715 | 1.24 | 2.002 | 1.883 |
| haloadjoint | serial-repro-native | r3w1 | live | 1.83 | 2.227 | 1.271 | 0.9446 | 1.86 | 1.198 |
| haloadjoint | serial-repro-native | r3w2 | live | 1.825 | 2.229 | 1.267 | 0.9452 | 1.853 | 1.199 |
| haloadjoint | mt8-repro-native | r3w1 | indicative | serial-only: LLVM lane has no OpenMP | 2.949 | 1.846 | 1.279 | 2.06 | 1.91 |
| haloadjoint | mt8-repro-native | r3w2 | indicative | serial-only: LLVM lane has no OpenMP | 2.935 | 1.794 | 1.279 | 2.011 | 1.901 |
| stencil | mt8-repro-native | r3w1 | live | serial-only: LLVM lane has no OpenMP | 8.402 | 8.54 | 7.789 | 9.71 | 8.075 |
| stencil | mt8-repro-native | r3w2 | live | serial-only: LLVM lane has no OpenMP | 8.427 | 8.622 | 7.737 | 9.855 | 8.135 |
| stencil | mt8-repro-native | r3w2retain | live | serial-only: LLVM lane has no OpenMP | 7.801 | 8.583 | 7.769 | 9.794 | 8.23 |
| stencil | mt8-licensed-native | r3w1 | live | serial-only: LLVM lane has no OpenMP | 8.666 | 8.603 | 7.795 | 9.864 | 8.256 |
| stencil | mt8-licensed-native | r3w2 | live | serial-only: LLVM lane has no OpenMP | 8.601 | 8.546 | 7.757 | 9.764 | 8.296 |
| stencil | mt8-licensed-native | r3w2retain | indicative | serial-only: LLVM lane has no OpenMP | 7.916 | 8.682 | 7.898 | 9.888 | 8.263 |
| recur | mt8-licensed-native | r3w1 | indicative | serial-only: LLVM lane has no OpenMP | 9.23 | 71.19 | 6.79 | 14.66 | 7.338 |
| recur | mt8-licensed-native | r3w2 | indicative | serial-only: LLVM lane has no OpenMP | 9.127 | 71.95 | 6.824 | 14.8 | 7.279 |
| recur | mt8-licensed-native | r3w2retain | live | serial-only: LLVM lane has no OpenMP | 6.764 | 71.29 | 6.814 | 14.82 | 7.208 |
| recur | mt8-repro-native | r3w1 | indicative | serial-only: LLVM lane has no OpenMP | 12.34 | 73.32 | 7.063 | 15.14 | 7.443 |
| recur | mt8-repro-native | r3w2 | indicative | serial-only: LLVM lane has no OpenMP | 12.38 | 73.52 | 7.654 | 15.46 | 7.784 |
| recur | mt8-repro-native | r3w2retain | live | serial-only: LLVM lane has no OpenMP | 9.524 | 71.65 | 6.951 | 14.8 | 7.485 |
| gram | serial-licensed-native | r3w1 | live | 56.04 | 57.39 | 453.2 | 76.06 | 568.4 | 68.91 |
| gram | serial-licensed-native | r3w2 | indicative | 56.32 | 57.96 | 483.2 | 76.55 | 567.8 | 68.8 |

## 3. Expert ratios (expert median / Blade ref median; > 1 Blade faster); harness ratio [CI] verdict

| kernel | regime | mirror | ref | gfortran-expert | ifx-expert | block |
|---|---|---|---|---|---|---|
| pairkernel | mt8-repro-native | r3w1 | blade-cpp | 0.681 [0.664, 0.716] loss | 0.686 [0.678, 0.692] loss | live |
| pairkernel | mt8-repro-native | r3w2 | blade-cpp | 0.803 [0.789, 0.840] loss | 0.805 [0.786, 0.818] loss | live |
| pairkernel | mt8-repro-native | r3w2prefer | blade-cpp | 0.678 [0.661, 0.704] suppressed | 0.687 [0.667, 0.699] suppressed | indicative |
| pairkernel | mt8-repro-native | r3w2pull0 | blade-cpp | 0.824 [0.805, 0.856] loss | 0.797 [0.781, 0.811] loss | live |
| pairkernel | serial-repro-native | r3w1 | blade-llvm | 1.146 [1.142, 1.152] win | 1.066 [1.063, 1.068] detectable-win | live |
| pairkernel | serial-repro-native | r3w2 | blade-llvm | 1.145 [1.143, 1.150] win | 1.065 [1.061, 1.069] detectable-win | live |
| pairkernel | serial-repro-native | r3w2prefer | blade-llvm | 1.152 [1.148, 1.156] win | 1.066 [1.063, 1.069] detectable-win | live |
| pairkernel | serial-repro-native | r3w2pull0 | blade-llvm | 1.149 [1.146, 1.154] win | 1.066 [1.062, 1.069] detectable-win | live |
| comoment3 | mt8-repro-native | r3w1 | blade-cpp | 1.147 [1.117, 1.169] win | 0.964 [0.938, 0.985] detectable-loss | live |
| comoment3 | mt8-repro-native | r3w2 | blade-cpp | 1.553 [1.495, 1.599] win | 1.305 [1.238, 1.344] win | live |
| comoment3 | mt8-repro-native | r3w2prefer | blade-cpp | 1.542 [1.500, 1.585] suppressed | 1.274 [1.226, 1.325] suppressed | indicative |
| comoment3 | mt8-repro-native | r3w2pull0 | blade-cpp | 1.422 [1.363, 1.465] suppressed | 1.125 [1.079, 1.170] suppressed | indicative |
| comoment3 | mt8-repro-native | r3w2g35 | blade-cpp | 1.100 [1.047, 1.119] suppressed | 0.950 [0.894, 0.999] suppressed | indicative |
| comoment3 | serial-repro-native | r3w1 | blade-llvm | 1.286 [1.276, 1.291] win | 1.291 [1.281, 1.302] win | live |
| comoment3 | serial-repro-native | r3w2 | blade-llvm | 1.289 [1.283, 1.298] win | 1.291 [1.277, 1.295] win | live |
| comoment3 | serial-repro-native | r3w2prefer | blade-llvm | 1.287 [1.270, 1.300] win | 1.290 [1.279, 1.309] win | live |
| comoment3 | serial-repro-native | r3w2pull0 | blade-llvm | 1.291 [1.278, 1.305] win | 1.288 [1.269, 1.319] win | live |
| antisym | serial-licensed-native | r3w1 | blade-llvm | 1.109 [1.096, 1.128] win | 1.052 [1.048, 1.066] detectable-win | live |
| antisym | serial-licensed-native | r3w2 | blade-llvm | 1.117 [1.101, 1.143] win | 1.070 [1.054, 1.083] detectable-win | live |
| antisym | serial-licensed-native | r3w2 | blade-llvm | 1.104 [1.070, 1.122] suppressed | 1.061 [1.024, 1.078] suppressed | indicative |
| antisym | serial-licensed-native | r3w2 | blade-llvm | 1.107 [1.101, 1.121] win | 1.058 [1.049, 1.068] detectable-win | live |
| antisym | serial-licensed-native | r3w2antig1 | blade-llvm | 1.103 [1.076, 1.136] win | 1.055 [1.033, 1.080] detectable-win | live |
| antisym | serial-licensed-native | r3w2antig1 | blade-llvm | 1.109 [1.105, 1.123] win | 1.066 [1.057, 1.070] detectable-win | live |
| antisym | serial-licensed-native | r3w2antig1 | blade-llvm | 1.084 [1.074, 1.107] detectable-win | 1.045 [1.027, 1.090] detectable-win | live |
| antisym | mt8-licensed-native | r3w1 | blade-cpp | 1.152 [1.066, 1.205] suppressed | 1.227 [1.127, 1.350] suppressed | indicative |
| antisym | mt8-licensed-native | r3w2 | blade-cpp | 1.152 [1.032, 1.214] suppressed | 1.194 [1.089, 1.279] suppressed | indicative |
| antisym | mt8-licensed-native | r3w2 | blade-cpp | 1.172 [1.089, 1.269] suppressed | 1.223 [1.145, 1.309] suppressed | indicative |
| antisym | mt8-licensed-native | r3w2 | blade-cpp | 1.174 [1.150, 1.219] suppressed | 1.200 [1.178, 1.236] suppressed | indicative |
| antisym | mt8-licensed-native | r3w2antig1 | blade-cpp | 1.126 [1.058, 1.211] suppressed | 1.153 [1.088, 1.203] suppressed | indicative |
| antisym | mt8-licensed-native | r3w2antig1 | blade-cpp | 1.105 [1.091, 1.115] win | 1.149 [1.121, 1.165] win | live |
| antisym | mt8-licensed-native | r3w2antig1 | blade-cpp | 1.088 [1.058, 1.117] suppressed | 1.150 [1.100, 1.161] suppressed | indicative |
| saxpy | serial-repro-native | r3w1 | blade-llvm | 1.408 [1.394, 1.418] win | 0.888 [0.878, 0.893] loss | live |
| saxpy | serial-repro-native | r3w2 | blade-llvm | 1.584 [1.535, 1.590] win | 0.994 [0.962, 0.999] detectable-loss | live |
| saxpy | serial-repro-native | r3w2legs0 | blade-llvm | 1.376 [1.356, 1.407] win | 0.880 [0.851, 0.919] loss | live |
| saxpy | serial-licensed-native | r3w1 | blade-llvm | 1.391 [1.384, 1.398] win | 0.991 [0.985, 1.004] equivalent | live |
| saxpy | serial-licensed-native | r3w2 | blade-llvm | 1.547 [1.536, 1.558] win | 1.113 [1.101, 1.121] win | live |
| saxpy | serial-licensed-native | r3w2legs0 | blade-llvm | 1.360 [1.306, 1.398] suppressed | 1.000 [0.959, 1.034] suppressed | indicative |
| saxpy | mt8-repro-native | r3w1 | blade-cpp | 0.821 [0.812, 0.841] loss | 0.859 [0.851, 0.871] loss | live |
| saxpy | mt8-repro-native | r3w2 | blade-cpp | 0.820 [0.812, 0.853] loss | 0.866 [0.853, 0.871] loss | live |
| saxpy | mt8-repro-native | r3w2legs0 | blade-cpp | 0.817 [0.809, 0.832] loss | 0.860 [0.851, 0.874] loss | live |
| saxpy | mt8-licensed-native | r3w1 | blade-cpp | 0.943 [0.924, 0.951] detectable-loss | 0.994 [0.970, 1.003] equivalent | live |
| saxpy | mt8-licensed-native | r3w2 | blade-cpp | 0.939 [0.915, 0.957] suppressed | 0.985 [0.948, 1.000] suppressed | indicative |
| saxpy | mt8-licensed-native | r3w2legs0 | blade-cpp | 0.946 [0.903, 0.958] detectable-loss | 0.990 [0.946, 1.011] equivalent | live |
| haloadjoint | serial-licensed-native | r3w1 | blade-llvm | 0.523 [0.515, 0.528] loss | 0.653 [0.645, 0.658] loss | live |
| haloadjoint | serial-licensed-native | r3w2 | blade-llvm | 0.521 [0.515, 0.526] loss | 0.646 [0.641, 0.651] loss | live |
| haloadjoint | mt8-licensed-native | r3w1 | blade-cpp | 0.411 [0.400, 0.437] loss | 0.621 [0.608, 0.648] loss | live |
| haloadjoint | mt8-licensed-native | r3w2 | blade-cpp | 0.418 [0.408, 0.431] loss | 0.635 [0.621, 0.642] loss | live |
| haloadjoint | serial-repro-native | r3w1 | blade-llvm | 0.516 [0.510, 0.522] loss | 0.655 [0.650, 0.662] loss | live |
| haloadjoint | serial-repro-native | r3w2 | blade-llvm | 0.518 [0.513, 0.521] loss | 0.657 [0.649, 0.660] loss | live |
| haloadjoint | mt8-repro-native | r3w1 | blade-cpp | 0.434 [0.417, 0.479] suppressed | 0.647 [0.632, 0.661] suppressed | indicative |
| haloadjoint | mt8-repro-native | r3w2 | blade-cpp | 0.436 [0.402, 0.459] suppressed | 0.648 [0.624, 0.671] suppressed | indicative |
| stencil | mt8-repro-native | r3w1 | blade-cpp | 0.927 [0.909, 0.942] detectable-loss | 0.961 [0.943, 0.981] detectable-loss | live |
| stencil | mt8-repro-native | r3w2 | blade-cpp | 0.918 [0.896, 0.937] detectable-loss | 0.965 [0.946, 0.978] detectable-loss | live |
| stencil | mt8-repro-native | r3w2retain | blade-cpp | 0.996 [0.981, 1.022] equivalent | 1.055 [1.038, 1.083] detectable-win | live |
| stencil | mt8-licensed-native | r3w1 | blade-cpp | 0.900 [0.888, 0.913] loss | 0.953 [0.935, 0.968] detectable-loss | live |
| stencil | mt8-licensed-native | r3w2 | blade-cpp | 0.902 [0.888, 0.913] loss | 0.965 [0.946, 0.978] detectable-loss | live |
| stencil | mt8-licensed-native | r3w2retain | blade-cpp | 0.998 [0.940, 1.043] suppressed | 1.044 [0.988, 1.070] suppressed | indicative |
| recur | mt8-licensed-native | r3w1 | blade-cpp | 0.736 [0.702, 0.780] suppressed | 0.795 [0.748, 0.828] suppressed | indicative |
| recur | mt8-licensed-native | r3w2 | blade-cpp | 0.748 [0.731, 0.781] suppressed | 0.797 [0.780, 0.830] suppressed | indicative |
| recur | mt8-licensed-native | r3w2retain | blade-cpp | 1.007 [0.965, 1.033] equivalent | 1.066 [1.022, 1.094] detectable-win | live |
| recur | mt8-repro-native | r3w1 | blade-cpp | 0.573 [0.536, 0.611] suppressed | 0.603 [0.566, 0.629] suppressed | indicative |
| recur | mt8-repro-native | r3w2 | blade-cpp | 0.618 [0.588, 0.653] suppressed | 0.629 [0.609, 0.691] suppressed | indicative |
| recur | mt8-repro-native | r3w2retain | blade-cpp | 0.730 [0.716, 0.755] loss | 0.786 [0.769, 0.802] loss | live |
| gram | serial-licensed-native | r3w1 | blade-llvm | 1.357 [1.350, 1.371] win | 1.230 [1.224, 1.235] win | live |
| gram | serial-licensed-native | r3w2 | blade-llvm | 1.359 [1.307, 1.387] suppressed | 1.222 [1.172, 1.226] suppressed | indicative |

## 4. Blocks

| kernel | regime | mirror | rounds | class | compute ctl | memory ctl | flags | excursions | bundle |
|---|---|---|---|---|---|---|---|---|---|
| pairkernel | mt8-repro-native | r3w1 | 21 | live | 12.676 | 5.253 | - | 0 | session-150716 |
| pairkernel | mt8-repro-native | r3w2 | 21 | live | 12.666 | 5.259 | - | 0 | session-151107 |
| pairkernel | mt8-repro-native | r3w2prefer | 21 | indicative | 12.640 | 5.293 | trend: ifx-natural first-half vs second-half median moved 12.3% | 0 | session-151457 |
| pairkernel | mt8-repro-native | r3w2pull0 | 21 | live | 12.634 | 5.235 | - | 0 | session-151847 |
| pairkernel | serial-repro-native | r3w1 | 21 | live | 12.641 | 5.243 | - | 0 | session-150716 |
| pairkernel | serial-repro-native | r3w2 | 21 | live | 12.642 | 5.248 | - | 0 | session-151107 |
| pairkernel | serial-repro-native | r3w2prefer | 21 | live | 12.601 | 5.260 | - | 0 | session-151457 |
| pairkernel | serial-repro-native | r3w2pull0 | 21 | live | 12.633 | 5.265 | - | 0 | session-151847 |
| comoment3 | mt8-repro-native | r3w1 | 21 | live | 12.714 | 5.211 | - | 0 | session-152237 |
| comoment3 | mt8-repro-native | r3w2 | 21 | live | 12.657 | 5.232 | - | 0 | session-152700 |
| comoment3 | mt8-repro-native | r3w2prefer | 21 | indicative | 12.686 | 5.253 | trend: gfortran-natural first-half vs second-half median moved 6.6% | 0 | session-153124 |
| comoment3 | mt8-repro-native | r3w2pull0 | 21 | indicative | 12.838 | 5.300 | trend: blade-cpp first-half vs second-half median moved 5.5% | 0 | session-153548 |
| comoment3 | mt8-repro-native | r3w2g35 | 21 | indicative | 12.863 | 5.271 | trend: gfortran-natural first-half vs second-half median moved 6.6%; trend: ifx-expert first-half vs second-half median moved 9.3% | 0 | session-180901 |
| comoment3 | serial-repro-native | r3w1 | 21 | live | 12.662 | 5.233 | - | 0 | session-152237 |
| comoment3 | serial-repro-native | r3w2 | 21 | live | 12.657 | 5.290 | - | 0 | session-152700 |
| comoment3 | serial-repro-native | r3w2prefer | 21 | live | 12.650 | 5.228 | - | 0 | session-153124 |
| comoment3 | serial-repro-native | r3w2pull0 | 21 | live | 12.789 | 5.315 | - | 0 | session-153548 |
| antisym | serial-licensed-native | r3w1 | 21 | live | 12.640 | 5.225 | - | 0 | session-154013 |
| antisym | serial-licensed-native | r3w2 | 21 | live | 12.875 | 5.456 | - | 0 | session-154436 |
| antisym | serial-licensed-native | r3w2 | 21 | indicative | 12.916 | 5.262 | trend: blade-llvm first-half vs second-half median moved 8.2% | 0 | session-155331 |
| antisym | serial-licensed-native | r3w2 | 21 | live | 12.629 | 5.251 | - | 0 | session-160214 |
| antisym | serial-licensed-native | r3w2antig1 | 21 | live | 12.739 | 5.261 | - | 0 | session-154901 |
| antisym | serial-licensed-native | r3w2antig1 | 21 | live | 12.638 | 5.254 | - | 0 | session-155753 |
| antisym | serial-licensed-native | r3w2antig1 | 21 | live | 12.612 | 5.214 | - | 0 | session-160632 |
| antisym | mt8-licensed-native | r3w1 | 21 | indicative | 13.765 | 5.633 | trend: blade-cpp first-half vs second-half median moved 11.1%; trend: gfortran-natural first-half vs second-half median moved 5.3%; trend: gfortran-expert first-half vs second-half median moved 5.4%; trend: ifx-natural first-half vs second-half median moved 8.7%; trend: ifx-expert first-half vs second-half median moved 6.6%; control compute median 0.01376s moved +8.9% vs the session baseline 0.01264s; trend: control compute moved 6.7% within the block; control memory median 0.005633s moved +7.8% vs the session baseline 0.005225s; trend: control memory moved 8.3% within the block | 1 | session-154013 |
| antisym | mt8-licensed-native | r3w2 | 21 | indicative | 12.860 | 5.362 | trend: ifx-expert first-half vs second-half median moved 6.3% | 0 | session-154436 |
| antisym | mt8-licensed-native | r3w2 | 21 | indicative | 12.800 | 5.207 | trend: blade-cpp first-half vs second-half median moved 5.8%; trend: ifx-expert first-half vs second-half median moved 6.3% | 0 | session-155331 |
| antisym | mt8-licensed-native | r3w2 | 21 | indicative | 12.614 | 5.225 | trend: ifx-natural first-half vs second-half median moved 5.8% | 0 | session-160214 |
| antisym | mt8-licensed-native | r3w2antig1 | 21 | indicative | 12.793 | 5.257 | trend: gfortran-expert first-half vs second-half median moved 10.4%; trend: ifx-natural first-half vs second-half median moved 12.1% | 0 | session-154901 |
| antisym | mt8-licensed-native | r3w2antig1 | 21 | live | 12.622 | 5.278 | - | 0 | session-155753 |
| antisym | mt8-licensed-native | r3w2antig1 | 21 | indicative | 12.606 | 5.208 | trend: gfortran-natural first-half vs second-half median moved 5.1%; trend: ifx-natural first-half vs second-half median moved 10.0% | 0 | session-160632 |
| saxpy | serial-repro-native | r3w1 | 21 | live | 12.649 | 5.215 | - | 0 | session-161051 |
| saxpy | serial-repro-native | r3w2 | 21 | live | 12.655 | 5.194 | - | 0 | session-162330 |
| saxpy | serial-repro-native | r3w2legs0 | 21 | live | 12.795 | 5.260 | - | 0 | session-175202 |
| saxpy | serial-licensed-native | r3w1 | 21 | live | 12.636 | 5.278 | - | 0 | session-161051 |
| saxpy | serial-licensed-native | r3w2 | 21 | live | 12.673 | 5.242 | - | 0 | session-162330 |
| saxpy | serial-licensed-native | r3w2legs0 | 21 | indicative | 13.064 | 5.324 | trend: blade-llvm first-half vs second-half median moved 5.8%; trend: ifx-natural first-half vs second-half median moved 5.1% | 0 | session-175202 |
| saxpy | mt8-repro-native | r3w1 | 21 | live | 12.634 | 5.223 | - | 0 | session-161051 |
| saxpy | mt8-repro-native | r3w2 | 21 | live | 12.623 | 5.174 | - | 0 | session-162330 |
| saxpy | mt8-repro-native | r3w2legs0 | 21 | live | 12.620 | 5.217 | - | 0 | session-175202 |
| saxpy | mt8-licensed-native | r3w1 | 21 | live | 12.666 | 5.214 | - | 0 | session-161051 |
| saxpy | mt8-licensed-native | r3w2 | 21 | indicative | 12.661 | 5.227 | trend: blade-cpp-inputonly first-half vs second-half median moved 8.1% | 0 | session-162330 |
| saxpy | mt8-licensed-native | r3w2legs0 | 21 | live | 12.700 | 5.252 | - | 0 | session-175202 |
| haloadjoint | serial-licensed-native | r3w1 | 21 | live | 12.631 | 5.225 | - | 0 | session-163626 |
| haloadjoint | serial-licensed-native | r3w2 | 21 | live | 12.659 | 5.248 | - | 0 | session-165357 |
| haloadjoint | mt8-licensed-native | r3w1 | 21 | live | 12.645 | 5.228 | - | 0 | session-163626 |
| haloadjoint | mt8-licensed-native | r3w2 | 21 | live | 12.699 | 5.223 | - | 0 | session-165357 |
| haloadjoint | serial-repro-native | r3w1 | 21 | live | 12.625 | 5.242 | - | 0 | session-163626 |
| haloadjoint | serial-repro-native | r3w2 | 21 | live | 12.630 | 5.236 | - | 0 | session-165357 |
| haloadjoint | mt8-repro-native | r3w1 | 21 | indicative | 13.735 | 5.590 | trend: gfortran-expert first-half vs second-half median moved 6.8%; control compute median 0.01374s moved +8.7% vs the session baseline 0.01263s; trend: control compute moved 9.6% within the block; trend: control memory moved 12.0% within the block | 0 | session-163626 |
| haloadjoint | mt8-repro-native | r3w2 | 21 | indicative | 12.586 | 5.200 | trend: blade-cpp-inputonly first-half vs second-half median moved 6.7% | 0 | session-165357 |
| stencil | mt8-repro-native | r3w1 | 21 | live | 12.608 | 5.215 | - | 0 | session-171128 |
| stencil | mt8-repro-native | r3w2 | 21 | live | 12.672 | 5.243 | - | 0 | session-171843 |
| stencil | mt8-repro-native | r3w2retain | 21 | live | 12.627 | 5.231 | - | 0 | session-172559 |
| stencil | mt8-licensed-native | r3w1 | 21 | live | 12.741 | 5.287 | - | 0 | session-171128 |
| stencil | mt8-licensed-native | r3w2 | 21 | live | 12.633 | 5.229 | - | 0 | session-171843 |
| stencil | mt8-licensed-native | r3w2retain | 21 | indicative | 12.734 | 5.279 | trend: blade-cpp first-half vs second-half median moved 7.2%; trend: gfortran-natural-inputonly first-half vs second-half median moved 5.4% | 0 | session-172559 |
| recur | mt8-licensed-native | r3w1 | 21 | indicative | 12.613 | 5.241 | trend: blade-cpp first-half vs second-half median moved 5.3%; trend: gfortran-expert first-half vs second-half median moved 5.3% | 0 | session-173319 |
| recur | mt8-licensed-native | r3w2 | 21 | indicative | 12.683 | 5.285 | trend: blade-cpp first-half vs second-half median moved 5.9%; trend: gfortran-expert first-half vs second-half median moved 6.5%; trend: ifx-expert first-half vs second-half median moved 6.4% | 0 | session-173734 |
| recur | mt8-licensed-native | r3w2retain | 21 | live | 12.700 | 5.267 | - | 0 | session-174151 |
| recur | mt8-repro-native | r3w1 | 21 | indicative | 12.811 | 5.267 | trend: blade-cpp first-half vs second-half median moved 6.1%; trend: ifx-natural first-half vs second-half median moved 6.1% | 0 | session-173319 |
| recur | mt8-repro-native | r3w2 | 21 | indicative | 12.791 | 5.285 | trend: ifx-expert first-half vs second-half median moved 5.9% | 0 | session-173734 |
| recur | mt8-repro-native | r3w2retain | 21 | live | 12.677 | 5.298 | - | 0 | session-174151 |
| gram | serial-licensed-native | r3w1 | 11 | live | 12.749 | 5.229 | - | 0 | session-174607 |
| gram | serial-licensed-native | r3w2 | 11 | indicative | 12.630 | 5.271 | trend: gfortran-natural first-half vs second-half median moved 6.3% | 0 | session-174747 |

## 5. r3w2 slower than r3w1 beyond the CI (Blade arms)

| kernel | regime | arm | old ms | new ms | new/old [CI] | class |
|---|---|---|---|---|---|---|
| antisym | serial-licensed-native | blade-llvm | 27.45 | 27.65 | 1.007 [1.001, 1.020] | ind |
| antisym | serial-licensed-native | blade-cpp | 28.32 | 28.64 | 1.011 [1.002, 1.019] | ind |

## 6. Missing cells

none

## 7. Sessions

| kernel | mirror | bundle | started | aborted | duration (s) | failures | flags |
|---|---|---|---|---|---|---|---|
| pairkernel | r3w1 | C:\bt_r2ab\r3w1\bench\results\2026-10-21-LAPTOP-350H6LDB\session-150716 | 2026-10-21T15:07:16 | None | 219 | none | none |
| comoment3 | r3w1 | C:\bt_r2ab\r3w1\bench\results\2026-10-21-LAPTOP-350H6LDB\session-152237 | 2026-10-21T15:22:37 | None | 253 | none | none |
| antisym | r3w1 | C:\bt_r2ab\r3w1\bench\results\2026-10-21-LAPTOP-350H6LDB\session-154013 | 2026-10-21T15:40:13 | None | 252 | none | antisym/mt8-licensed-native: inference suppressed: trend: blade-cpp first-half vs second-half median moved 11.1%; trend: gfortran-natural first-half vs second-half median moved 5.3%; trend: gfortran-expert first-half vs second-half median moved 5.4%; trend: ifx-natural first-half vs second-half median moved 8.7%; trend: ifx-expert first-half vs second-half median moved 6.6%; control compute median 0.01376s moved +8.9% vs the session baseline 0.01264s; trend: control compute moved 6.7% within the block; control memory median 0.005633s moved +7.8% vs the session baseline 0.005225s; trend: control memory moved 8.3% within the block |
| saxpy | r3w1 | C:\bt_r2ab\r3w1\bench\results\2026-10-21-LAPTOP-350H6LDB\session-161051 | 2026-10-21T16:10:51 | None | 748 | none | none |
| haloadjoint | r3w1 | C:\bt_r2ab\r3w1\bench\results\2026-10-21-LAPTOP-350H6LDB\session-163626 | 2026-10-21T16:36:26 | None | 1040 | none | haloadjoint/mt8-repro-native: inference suppressed: trend: gfortran-expert first-half vs second-half median moved 6.8%; control compute median 0.01374s moved +8.7% vs the session baseline 0.01263s; trend: control compute moved 9.6% within the block; trend: control memory moved 12.0% within the block |
| stencil | r3w1 | C:\bt_r2ab\r3w1\bench\results\2026-10-21-LAPTOP-350H6LDB\session-171128 | 2026-10-21T17:11:28 | None | 424 | none | none |
| recur | r3w1 | C:\bt_r2ab\r3w1\bench\results\2026-10-21-LAPTOP-350H6LDB\session-173319 | 2026-10-21T17:33:19 | None | 245 | none | recur/mt8-licensed-native: inference suppressed: trend: blade-cpp first-half vs second-half median moved 5.3%; trend: gfortran-expert first-half vs second-half median moved 5.3%; recur/mt8-repro-native: inference suppressed: trend: blade-cpp first-half vs second-half median moved 6.1%; trend: ifx-natural first-half vs second-half median moved 6.1% |
| gram | r3w1 | C:\bt_r2ab\r3w1\bench\results\2026-10-21-LAPTOP-350H6LDB\session-174607 | 2026-10-21T17:46:07 | None | 90 | none | none |
| pairkernel | r3w2 | C:\bt_r2ab\r3w2\bench\results\2026-10-21-LAPTOP-350H6LDB\session-151107 | 2026-10-21T15:11:07 | None | 220 | none | none |
| comoment3 | r3w2 | C:\bt_r2ab\r3w2\bench\results\2026-10-21-LAPTOP-350H6LDB\session-152700 | 2026-10-21T15:27:00 | None | 254 | none | none |
| antisym | r3w2 | C:\bt_r2ab\r3w2\bench\results\2026-10-21-LAPTOP-350H6LDB\session-154436 | 2026-10-21T15:44:36 | None | 253 | none | antisym/mt8-licensed-native: inference suppressed: trend: ifx-expert first-half vs second-half median moved 6.3% |
| antisym | r3w2 | C:\bt_r2ab\r3w2\bench\results\2026-10-21-LAPTOP-350H6LDB\session-155331 | 2026-10-21T15:53:31 | None | 252 | none | antisym/serial-licensed-native: inference suppressed: trend: blade-llvm first-half vs second-half median moved 8.2%; antisym/mt8-licensed-native: inference suppressed: trend: blade-cpp first-half vs second-half median moved 5.8%; trend: ifx-expert first-half vs second-half median moved 6.3% |
| antisym | r3w2 | C:\bt_r2ab\r3w2\bench\results\2026-10-21-LAPTOP-350H6LDB\session-160214 | 2026-10-21T16:02:14 | None | 248 | none | antisym/mt8-licensed-native: inference suppressed: trend: ifx-natural first-half vs second-half median moved 5.8% |
| saxpy | r3w2 | C:\bt_r2ab\r3w2\bench\results\2026-10-21-LAPTOP-350H6LDB\session-162330 | 2026-10-21T16:23:30 | None | 748 | none | saxpy/mt8-licensed-native: inference suppressed: trend: blade-cpp-inputonly first-half vs second-half median moved 8.1% |
| haloadjoint | r3w2 | C:\bt_r2ab\r3w2\bench\results\2026-10-21-LAPTOP-350H6LDB\session-165357 | 2026-10-21T16:53:57 | None | 1040 | none | haloadjoint/mt8-repro-native: inference suppressed: trend: blade-cpp-inputonly first-half vs second-half median moved 6.7% |
| stencil | r3w2 | C:\bt_r2ab\r3w2\bench\results\2026-10-21-LAPTOP-350H6LDB\session-171843 | 2026-10-21T17:18:43 | None | 426 | none | none |
| recur | r3w2 | C:\bt_r2ab\r3w2\bench\results\2026-10-21-LAPTOP-350H6LDB\session-173734 | 2026-10-21T17:37:34 | None | 246 | none | recur/mt8-licensed-native: inference suppressed: trend: blade-cpp first-half vs second-half median moved 5.9%; trend: gfortran-expert first-half vs second-half median moved 6.5%; trend: ifx-expert first-half vs second-half median moved 6.4%; recur/mt8-repro-native: inference suppressed: trend: ifx-expert first-half vs second-half median moved 5.9% |
| gram | r3w2 | C:\bt_r2ab\r3w2\bench\results\2026-10-21-LAPTOP-350H6LDB\session-174747 | 2026-10-21T17:47:47 | None | 90 | none | gram/serial-licensed-native: inference suppressed: trend: gfortran-natural first-half vs second-half median moved 6.3% |
| pairkernel | r3w2prefer | C:\bt_r2ab\r3w2prefer\bench\results\2026-10-21-LAPTOP-350H6LDB\session-151457 | 2026-10-21T15:14:57 | None | 220 | none | pairkernel/mt8-repro-native: inference suppressed: trend: ifx-natural first-half vs second-half median moved 12.3% |
| comoment3 | r3w2prefer | C:\bt_r2ab\r3w2prefer\bench\results\2026-10-21-LAPTOP-350H6LDB\session-153124 | 2026-10-21T15:31:24 | None | 253 | none | comoment3/mt8-repro-native: inference suppressed: trend: gfortran-natural first-half vs second-half median moved 6.6% |
| pairkernel | r3w2pull0 | C:\bt_r2ab\r3w2pull0\bench\results\2026-10-21-LAPTOP-350H6LDB\session-151847 | 2026-10-21T15:18:47 | None | 220 | none | none |
| comoment3 | r3w2pull0 | C:\bt_r2ab\r3w2pull0\bench\results\2026-10-21-LAPTOP-350H6LDB\session-153548 | 2026-10-21T15:35:48 | None | 255 | none | comoment3/mt8-repro-native: inference suppressed: trend: blade-cpp first-half vs second-half median moved 5.5% |
| antisym | r3w2antig1 | C:\bt_r2ab\r3w2antig1\bench\results\2026-10-21-LAPTOP-350H6LDB\session-154901 | 2026-10-21T15:49:01 | None | 259 | none | antisym/mt8-licensed-native: inference suppressed: trend: gfortran-expert first-half vs second-half median moved 10.4%; trend: ifx-natural first-half vs second-half median moved 12.1% |
| antisym | r3w2antig1 | C:\bt_r2ab\r3w2antig1\bench\results\2026-10-21-LAPTOP-350H6LDB\session-155753 | 2026-10-21T15:57:53 | None | 250 | none | none |
| antisym | r3w2antig1 | C:\bt_r2ab\r3w2antig1\bench\results\2026-10-21-LAPTOP-350H6LDB\session-160632 | 2026-10-21T16:06:32 | None | 248 | none | antisym/mt8-licensed-native: inference suppressed: trend: gfortran-natural first-half vs second-half median moved 5.1%; trend: ifx-natural first-half vs second-half median moved 10.0% |
| saxpy | r3w2legs0 | C:\bt_r2ab\r3w2legs0\bench\results\2026-10-21-LAPTOP-350H6LDB\session-163609 | 2026-10-21T16:36:09 | harness error: pre-session checks failed:
  saxpy|mt8-licensed-native|blade-llvm: no passing smoke evidence for the current sources (none); run `smoke --kernels saxpy --regimes mt8-licensed-native`
  saxpy|mt8-licensed-native|blade-cpp: no passing smoke evidence for the current sources (none); run `smoke --kernels saxpy --regimes mt8-licensed-native`
  saxpy|mt8-licensed-native|gfortran-natural: no passing smoke evidence for the current sources (none); run `smoke --kernels saxpy --regimes mt8-licensed-native`
  saxpy|mt8-licensed-native|gfortran-expert: no passing smoke evidence for the current sources (none); run `smoke --kernels saxpy --regimes mt8-licensed-native`
  saxpy|mt8-licensed-native|ifx-natural: no passing smoke evidence for the current sources (none); run `smoke --kernels saxpy --regimes mt8-licensed-native`
  saxpy|mt8-licensed-native|ifx-expert: no passing smoke evidence for the current sources (none); run `smoke --kernels saxpy --regimes mt8-licensed-native`
  saxpy|mt8-licensed-native|blade-llvm-inputonly: no passing smoke evidence for the current sources (none); run `smoke --kernels saxpy --regimes mt8-licensed-native`
  saxpy|mt8-licensed-native|blade-cpp-inputonly: no passing smoke evidence for the current sources (none); run `smoke --kernels saxpy --regimes mt8-licensed-native`
  saxpy|mt8-licensed-native|gfortran-natural-inputonly: no passing smoke evidence for the current sources (none); run `smoke --kernels saxpy --regimes mt8-licensed-native`
  saxpy|mt8-licensed-native|gfortran-expert-inputonly: no passing smoke evidence for the current sources (none); run `smoke --kernels saxpy --regimes mt8-licensed-native`
  saxpy|mt8-licensed-native|ifx-natural-inputonly: no passing smoke evidence for the current sources (none); run `smoke --kernels saxpy --regimes mt8-licensed-native`
  saxpy|mt8-licensed-native|ifx-expert-inputonly: no passing smoke evidence for the current sources (none); run `smoke --kernels saxpy --regimes mt8-licensed-native`
  saxpy|mt8-repro-native|blade-llvm: no passing smoke evidence for the current sources (none); run `smoke --kernels saxpy --regimes mt8-repro-native`
  saxpy|mt8-repro-native|blade-cpp: no passing smoke evidence for the current sources (none); run `smoke --kernels saxpy --regimes mt8-repro-native`
  saxpy|mt8-repro-native|gfortran-natural: no passing smoke evidence for the current sources (none); run `smoke --kernels saxpy --regimes mt8-repro-native`
  saxpy|mt8-repro-native|gfortran-expert: no passing smoke evidence for the current sources (none); run `smoke --kernels saxpy --regimes mt8-repro-native`
  saxpy|mt8-repro-native|ifx-natural: no passing smoke evidence for the current sources (none); run `smoke --kernels saxpy --regimes mt8-repro-native`
  saxpy|mt8-repro-native|ifx-expert: no passing smoke evidence for the current sources (none); run `smoke --kernels saxpy --regimes mt8-repro-native`
  saxpy|mt8-repro-native|blade-llvm-inputonly: no passing smoke evidence for the current sources (none); run `smoke --kernels saxpy --regimes mt8-repro-native`
  saxpy|mt8-repro-native|blade-cpp-inputonly: no passing smoke evidence for the current sources (none); run `smoke --kernels saxpy --regimes mt8-repro-native`
  saxpy|mt8-repro-native|gfortran-natural-inputonly: no passing smoke evidence for the current sources (none); run `smoke --kernels saxpy --regimes mt8-repro-native`
  saxpy|mt8-repro-native|gfortran-expert-inputonly: no passing smoke evidence for the current sources (none); run `smoke --kernels saxpy --regimes mt8-repro-native`
  saxpy|mt8-repro-native|ifx-natural-inputonly: no passing smoke evidence for the current sources (none); run `smoke --kernels saxpy --regimes mt8-repro-native`
  saxpy|mt8-repro-native|ifx-expert-inputonly: no passing smoke evidence for the current sources (none); run `smoke --kernels saxpy --regimes mt8-repro-native` | 6 | ['harness error: pre-session checks failed:\n  saxpy|mt8-licensed-native|blade-llvm: no passing smoke evidence for the current sources (none); run `smoke --kernels saxpy --regimes mt8-licensed-native`\n  saxpy|mt8-licensed-native|blade-cpp: no passing smoke evidence for the current sources (none); run `smoke --kernels saxpy --regimes mt8-licensed-native`\n  saxpy|mt8-licensed-native|gfortran-natural: no passing smoke evidence for the current sources (none); run `smoke --kernels saxpy --regimes mt8-licensed-native`\n  saxpy|mt8-licensed-native|gfortran-expert: no passing smoke evidence for the current sources (none); run `smoke --kernels saxpy --regimes mt8-licensed-native`\n  saxpy|mt8-licensed-native|ifx-natural: no passing smoke evidence for the current sources (none); run `smoke --kernels saxpy --regimes mt8-licensed-native`\n  saxpy|mt8-licensed-native|ifx-expert: no passing smoke evidence for the current sources (none); run `smoke --kernels saxpy --regimes mt8-licensed-native`\n  saxpy|mt8-licensed-native|blade-llvm-inputonly: no passing smoke evidence for the current sources (none); run `smoke --kernels saxpy --regimes mt8-licensed-native`\n  saxpy|mt8-licensed-native|blade-cpp-inputonly: no passing smoke evidence for the current sources (none); run `smoke --kernels saxpy --regimes mt8-licensed-native`\n  saxpy|mt8-licensed-native|gfortran-natural-inputonly: no passing smoke evidence for the current sources (none); run `smoke --kernels saxpy --regimes mt8-licensed-native`\n  saxpy|mt8-licensed-native|gfortran-expert-inputonly: no passing smoke evidence for the current sources (none); run `smoke --kernels saxpy --regimes mt8-licensed-native`\n  saxpy|mt8-licensed-native|ifx-natural-inputonly: no passing smoke evidence for the current sources (none); run `smoke --kernels saxpy --regimes mt8-licensed-native`\n  saxpy|mt8-licensed-native|ifx-expert-inputonly: no passing smoke evidence for the current sources (none); run `smoke --kernels saxpy --regimes mt8-licensed-native`\n  saxpy|mt8-repro-native|blade-llvm: no passing smoke evidence for the current sources (none); run `smoke --kernels saxpy --regimes mt8-repro-native`\n  saxpy|mt8-repro-native|blade-cpp: no passing smoke evidence for the current sources (none); run `smoke --kernels saxpy --regimes mt8-repro-native`\n  saxpy|mt8-repro-native|gfortran-natural: no passing smoke evidence for the current sources (none); run `smoke --kernels saxpy --regimes mt8-repro-native`\n  saxpy|mt8-repro-native|gfortran-expert: no passing smoke evidence for the current sources (none); run `smoke --kernels saxpy --regimes mt8-repro-native`\n  saxpy|mt8-repro-native|ifx-natural: no passing smoke evidence for the current sources (none); run `smoke --kernels saxpy --regimes mt8-repro-native`\n  saxpy|mt8-repro-native|ifx-expert: no passing smoke evidence for the current sources (none); run `smoke --kernels saxpy --regimes mt8-repro-native`\n  saxpy|mt8-repro-native|blade-llvm-inputonly: no passing smoke evidence for the current sources (none); run `smoke --kernels saxpy --regimes mt8-repro-native`\n  saxpy|mt8-repro-native|blade-cpp-inputonly: no passing smoke evidence for the current sources (none); run `smoke --kernels saxpy --regimes mt8-repro-native`\n  saxpy|mt8-repro-native|gfortran-natural-inputonly: no passing smoke evidence for the current sources (none); run `smoke --kernels saxpy --regimes mt8-repro-native`\n  saxpy|mt8-repro-native|gfortran-expert-inputonly: no passing smoke evidence for the current sources (none); run `smoke --kernels saxpy --regimes mt8-repro-native`\n  saxpy|mt8-repro-native|ifx-natural-inputonly: no passing smoke evidence for the current sources (none); run `smoke --kernels saxpy --regimes mt8-repro-native`\n  saxpy|mt8-repro-native|ifx-expert-inputonly: no passing smoke evidence for the current sources (none); run `smoke --kernels saxpy --regimes mt8-repro-native`'] | none |
| saxpy | r3w2legs0 | C:\bt_r2ab\r3w2legs0\bench\results\2026-10-21-LAPTOP-350H6LDB\session-175202 | 2026-10-21T17:52:02 | None | 754 | none | saxpy/serial-licensed-native: inference suppressed: trend: blade-llvm first-half vs second-half median moved 5.8%; trend: ifx-natural first-half vs second-half median moved 5.1% |
| stencil | r3w2retain | C:\bt_r2ab\r3w2retain\bench\results\2026-10-21-LAPTOP-350H6LDB\session-172559 | 2026-10-21T17:25:59 | None | 429 | none | stencil/mt8-licensed-native: inference suppressed: trend: blade-cpp first-half vs second-half median moved 7.2%; trend: gfortran-natural-inputonly first-half vs second-half median moved 5.4% |
| recur | r3w2retain | C:\bt_r2ab\r3w2retain\bench\results\2026-10-21-LAPTOP-350H6LDB\session-174151 | 2026-10-21T17:41:51 | None | 246 | none | none |
| comoment3 | r3w2g35 | C:\bt_r2ab\r3w2g35\bench\results\2026-10-21-LAPTOP-350H6LDB\session-180901 | 2026-10-21T18:09:01 | None | 132 | none | comoment3/mt8-repro-native: inference suppressed: trend: gfortran-natural first-half vs second-half median moved 6.6%; trend: ifx-expert first-half vs second-half median moved 9.3% |

## 8. Peak working set per block (max over samples, MB): the A4 retention arm r3w2retain beside r3w2

| kernel | regime | mirror | class | blade-llvm | blade-cpp | gfortran-natural | gfortran-expert | ifx-natural | ifx-expert |
|---|---|---|---|---|---|---|---|---|---|
| pairkernel | mt8-repro-native | r3w1 | live | n/a | 29.9 | 53.4 | 29.5 | 54.9 | 31.0 |
| pairkernel | mt8-repro-native | r3w2 | live | n/a | 29.9 | 53.4 | 29.5 | 54.9 | 31.0 |
| pairkernel | mt8-repro-native | r3w2prefer | indicative | n/a | 29.9 | 53.4 | 29.5 | 54.9 | 31.0 |
| pairkernel | mt8-repro-native | r3w2pull0 | live | n/a | 29.9 | 53.4 | 29.5 | 54.9 | 31.0 |
| pairkernel | serial-repro-native | r3w1 | live | 28.6 | 29.4 | 52.9 | 29.1 | 52.5 | 28.6 |
| pairkernel | serial-repro-native | r3w2 | live | 28.6 | 29.4 | 52.9 | 29.1 | 52.5 | 28.6 |
| pairkernel | serial-repro-native | r3w2prefer | live | 28.6 | 29.4 | 53.0 | 29.1 | 52.5 | 28.6 |
| pairkernel | serial-repro-native | r3w2pull0 | live | 28.6 | 29.4 | 53.0 | 29.1 | 52.5 | 28.6 |
| comoment3 | mt8-repro-native | r3w1 | live | n/a | 13.2 | 43.9 | 14.5 | 45.5 | 15.8 |
| comoment3 | mt8-repro-native | r3w2 | live | n/a | 13.2 | 43.9 | 14.5 | 45.4 | 15.8 |
| comoment3 | mt8-repro-native | r3w2prefer | indicative | n/a | 13.2 | 44.0 | 14.6 | 45.5 | 15.9 |
| comoment3 | mt8-repro-native | r3w2pull0 | indicative | n/a | 13.3 | 44.0 | 14.5 | 45.4 | 15.8 |
| comoment3 | mt8-repro-native | r3w2g35 | indicative | n/a | 13.2 | 43.9 | 14.5 | 45.5 | 15.9 |
| comoment3 | serial-repro-native | r3w1 | live | 11.7 | 12.7 | 43.5 | 12.3 | 43.1 | 11.9 |
| comoment3 | serial-repro-native | r3w2 | live | 11.7 | 12.7 | 43.5 | 12.4 | 43.1 | 11.9 |
| comoment3 | serial-repro-native | r3w2prefer | live | 11.7 | 12.7 | 43.5 | 12.3 | 43.1 | 11.9 |
| comoment3 | serial-repro-native | r3w2pull0 | live | 11.7 | 12.7 | 43.5 | 12.3 | 43.1 | 11.9 |
| antisym | serial-licensed-native | r3w1 | live | 24.9 | 25.7 | 23.5 | 32.7 | 23.0 | 32.3 |
| antisym | serial-licensed-native | r3w2 | live | 24.9 | 25.7 | 23.5 | 32.7 | 23.0 | 32.3 |
| antisym | serial-licensed-native | r3w2 | indicative | 24.9 | 25.7 | 23.5 | 32.7 | 23.0 | 32.3 |
| antisym | serial-licensed-native | r3w2 | live | 24.9 | 25.7 | 23.5 | 32.7 | 23.0 | 32.3 |
| antisym | serial-licensed-native | r3w2antig1 | live | 24.9 | 25.7 | 23.5 | 32.8 | 23.0 | 32.3 |
| antisym | serial-licensed-native | r3w2antig1 | live | 24.9 | 25.7 | 23.5 | 32.7 | 23.0 | 32.3 |
| antisym | serial-licensed-native | r3w2antig1 | live | 24.9 | 25.7 | 23.5 | 32.7 | 23.0 | 32.3 |
| antisym | mt8-licensed-native | r3w1 | indicative | n/a | 26.4 | 23.9 | 33.2 | 25.4 | 34.7 |
| antisym | mt8-licensed-native | r3w2 | indicative | n/a | 27.2 | 23.9 | 33.2 | 25.4 | 34.7 |
| antisym | mt8-licensed-native | r3w2 | indicative | n/a | 27.2 | 23.9 | 33.2 | 25.4 | 34.7 |
| antisym | mt8-licensed-native | r3w2 | indicative | n/a | 27.3 | 23.9 | 33.2 | 25.4 | 34.7 |
| antisym | mt8-licensed-native | r3w2antig1 | indicative | n/a | 26.4 | 23.9 | 33.2 | 25.4 | 34.7 |
| antisym | mt8-licensed-native | r3w2antig1 | live | n/a | 26.3 | 23.9 | 33.2 | 25.4 | 34.7 |
| antisym | mt8-licensed-native | r3w2antig1 | indicative | n/a | 26.4 | 23.9 | 33.2 | 25.4 | 34.7 |
| saxpy | serial-repro-native | r3w1 | live | 50.4 | 50.8 | 50.9 | 50.9 | 50.4 | 50.4 |
| saxpy | serial-repro-native | r3w2 | live | 50.4 | 50.8 | 50.9 | 50.9 | 50.4 | 50.4 |
| saxpy | serial-repro-native | r3w2legs0 | live | 50.4 | 50.8 | 50.9 | 50.9 | 50.4 | 50.4 |
| saxpy | serial-licensed-native | r3w1 | live | 50.4 | 50.8 | 50.9 | 50.9 | 50.4 | 50.4 |
| saxpy | serial-licensed-native | r3w2 | live | 50.4 | 50.8 | 50.9 | 50.9 | 50.4 | 50.4 |
| saxpy | serial-licensed-native | r3w2legs0 | indicative | 50.4 | 50.8 | 50.9 | 50.9 | 50.4 | 50.4 |
| saxpy | mt8-repro-native | r3w1 | live | n/a | 51.3 | 51.3 | 51.3 | 52.9 | 52.9 |
| saxpy | mt8-repro-native | r3w2 | live | n/a | 51.3 | 51.3 | 51.3 | 52.9 | 52.9 |
| saxpy | mt8-repro-native | r3w2legs0 | live | n/a | 51.3 | 51.3 | 51.3 | 52.9 | 52.9 |
| saxpy | mt8-licensed-native | r3w1 | live | n/a | 51.3 | 51.3 | 51.3 | 52.9 | 52.9 |
| saxpy | mt8-licensed-native | r3w2 | indicative | n/a | 51.3 | 51.3 | 51.3 | 52.9 | 52.9 |
| saxpy | mt8-licensed-native | r3w2legs0 | live | n/a | 51.3 | 51.3 | 51.3 | 52.9 | 52.9 |
| haloadjoint | serial-licensed-native | r3w1 | live | 7.3 | 8.2 | 6.6 | 6.6 | 6.2 | 6.2 |
| haloadjoint | serial-licensed-native | r3w2 | live | 7.3 | 8.3 | 6.6 | 6.6 | 6.2 | 6.2 |
| haloadjoint | mt8-licensed-native | r3w1 | live | n/a | 8.8 | 7.1 | 7.2 | 8.8 | 8.9 |
| haloadjoint | mt8-licensed-native | r3w2 | live | n/a | 8.8 | 7.1 | 7.2 | 8.8 | 8.9 |
| haloadjoint | serial-repro-native | r3w1 | live | 7.3 | 8.3 | 6.6 | 6.6 | 6.2 | 6.2 |
| haloadjoint | serial-repro-native | r3w2 | live | 7.3 | 8.3 | 6.6 | 6.6 | 6.2 | 6.2 |
| haloadjoint | mt8-repro-native | r3w1 | indicative | n/a | 8.8 | 7.1 | 7.2 | 8.8 | 8.9 |
| haloadjoint | mt8-repro-native | r3w2 | indicative | n/a | 8.8 | 7.1 | 7.2 | 8.8 | 8.9 |
| stencil | mt8-repro-native | r3w1 | live | n/a | 51.4 | 51.4 | 51.4 | 53.0 | 52.9 |
| stencil | mt8-repro-native | r3w2 | live | n/a | 51.4 | 51.4 | 51.4 | 53.0 | 52.9 |
| stencil | mt8-repro-native | r3w2retain | live | n/a | 51.6 | 51.4 | 51.4 | 52.9 | 52.9 |
| stencil | mt8-licensed-native | r3w1 | live | n/a | 51.4 | 51.4 | 51.4 | 52.9 | 52.9 |
| stencil | mt8-licensed-native | r3w2 | live | n/a | 51.4 | 51.4 | 51.4 | 52.9 | 52.9 |
| stencil | mt8-licensed-native | r3w2retain | indicative | n/a | 51.5 | 51.4 | 51.4 | 53.0 | 52.9 |
| recur | mt8-licensed-native | r3w1 | indicative | n/a | 51.7 | 52.1 | 51.5 | 53.6 | 53.0 |
| recur | mt8-licensed-native | r3w2 | indicative | n/a | 51.7 | 52.1 | 51.5 | 53.6 | 53.0 |
| recur | mt8-licensed-native | r3w2retain | live | n/a | 51.8 | 52.1 | 51.5 | 53.6 | 53.0 |
| recur | mt8-repro-native | r3w1 | indicative | n/a | 51.7 | 52.1 | 51.5 | 53.6 | 53.0 |
| recur | mt8-repro-native | r3w2 | indicative | n/a | 51.7 | 52.1 | 51.5 | 53.6 | 53.0 |
| recur | mt8-repro-native | r3w2retain | live | n/a | 51.8 | 52.1 | 51.5 | 53.6 | 53.0 |
| gram | serial-licensed-native | r3w1 | live | 30.8 | 31.5 | 36.9 | 51.7 | 56.0 | 51.3 |
| gram | serial-licensed-native | r3w2 | indicative | 30.8 | 31.5 | 36.9 | 51.7 | 56.0 | 51.3 |