# Round 2 wave-1 harness A/B report

- Box: AC power (Win32_Battery status 2), scheme "Silent" (max processor state AC 100%, min 80%), host LAPTOP-350H6LDB (Ryzen 7 5800HS), uncapped; all 20 bench sessions are `bench` verb, size full, default rounds (all blocks ran 21 rounds), cooldowns default. Python `C:\Python314\python.exe`, ucrt64 first on PATH, OPENBLAS_DIR set. One bench at a time, interleaved OLD,NEW per kernel (driver `w1-ab-driver.ps1`; progress `w1-ab-progress.txt`; raw consoles `w1-bench-<kernel>-<old|new>.txt`).
- OLD = C:\bt_r2ab\old (master 4136f0b5), Blade.dll SHA256 462B9D924F5D48B4BAF7F88CADC766DD78685F1BAA2F7CB61D19AEE0E07D19E1. NEW = C:\bt_r2ab\new (integration 6b3e5293), Blade.dll SHA256 3061E9002F30D268A6B9E5BDC1A65C69AF6AE24BE6AF2EFB634475BF9B64F260. Harness/kernels/config identical between mirrors (README.txt in each mirror). Smoke evidence present for both (not re-run by me). Gate #1-5 TOTALs were not run in this task; hash provenance of those gates not verified here.
- Quiet-box check before start (2026-10-20 ~05:08): 6x5 s window avg 4.5%; then two consecutive 60 s windows avg 4.6% and 10.4% (both < 12%). Leftover processes: `Blade.exe` PID 20660 (started 10/19 23:32, CPU 3 s; the `ide serve`, left alone) and ~26 idle `python` processes (each <= 3.7 s CPU total, none consuming CPU; left alone). No dotnet/g++/cc1plus/clang/gfortran/ifx running.
- Load seen immediately before each session (10 s mean of `\Processor(_Total)\% Processor Time`; the previous session had ended 10 s earlier): saxpy old 5.4 / new 2.9; stencil 3.3 / 2.9; recur 3.3 / 3.1; pairkernel 3.4 / 2.9; comoment3 2.8 / 3.1; maskagg 3.5 / 3.5; dot 2.8 / 3.0; onepass 3.0 / 3.1; fusedchain 3.4 / 9.5 (the 9.5% sample was taken as the new `python` bench process (PID 28628) started at 07:57:13, i.e. self-inflicted harness start-up, no other process seen); gram 3.1 / 2.6. Wall: 05:13:28 start, 08:15:23 end (3.0 h, whole list completed, nothing shortened).
- Harness exit codes: every session exit 0 (clean) or 3 (flags only: suppressed blocks); no exit 1; `failures` empty in all 20 session.json; `aborted` None in all; excursions dropped: 0 in all 32 block pairs.
- Cells run (per runbook plus task additions): saxpy x4 regimes, stencil x4, recur x2, pairkernel x4 (serial/mt8 licensed + serial/mt8 repro), comoment3 x3 (serial-repro, mt8-repro, serial-licensed), maskagg x2, dot/onepass/fusedchain x4 (serial licensed/repro/fastmath + mt8-repro), gram mt8-licensed. 32 blocks per side; onepass mt8-repro Blade = no-parallel-form on both sides (not timed). Not run: mt8-licensed for dot/onepass/fusedchain, recur mt8, gram other regimes.
- CI caveat: harness CIs are within-session (arm vs reference). OLD-vs-NEW CIs below are MY unpaired percentile bootstrap (4000 resamples, seeded) over the two sessions' timed round samples; they do not model session-to-session drift. Session-to-session drift is visible in section 4: the compute/memory controls' NEW/OLD medians per block are within 0.95-1.04 everywhere (largest: pairkernel serial-repro memory 1.042, gram compute/memory 1.022/1.021, saxpy mt8-licensed memory 1.030, dot serial-fastmath memory 0.948).
- Verdict class: "live" = block not drift-suppressed, "ind(icative)" = harness suppressed inference for that block (the harness prints verdict `suppressed`). Ratios are reported as measured, none rescaled.
- Pins: no pin mismatch/DQ/fallback in any session. Section 3 "14.8 hist" = RESULTS 14.8 final-table gf / ifx ratios and its own live/indicative class, for the same kernel/regime.

# Wave-1 harness A/B: OLD 4136f0b5 (Blade.dll 462b9d92) vs NEW 6b3e5293 (Blade.dll 3061e900)

## 1. Per-cell medians (NEW/OLD < 1 = NEW faster). Block class: live = unflagged, ind = indicative (drift-suppressed). CI = analyst-computed unpaired percentile bootstrap (4000 resamples over each arm's timed round samples, excursion-dropped excluded) of median(NEW)/median(OLD) across the two separate sessions; NOT a harness output.

| kernel | regime | arm | OLD median (ms) | NEW median (ms) | NEW/OLD [95% CI, analyst unpaired bootstrap] | OLD block | NEW block | n OLD/NEW |
|---|---|---|---|---|---|---|---|---|
| saxpy | serial-licensed-native | blade-llvm | 11.61 | 10.93 | 0.942 [0.913, 0.955] | live | live | 21/21 |
| saxpy | serial-licensed-native | blade-cpp | 16.78 | 15.44 | 0.920 [0.910, 0.951] | live | live | 21/21 |
| saxpy | serial-licensed-native | gfortran-natural | 16.82 | 16.8 | 0.999 [0.980, 1.030] | live | live | 21/21 |
| saxpy | serial-licensed-native | gfortran-expert | 14.57 | 15.05 | 1.033 [0.995, 1.046] | live | live | 21/21 |
| saxpy | serial-licensed-native | ifx-natural | 13.21 | 13.27 | 1.005 [0.984, 1.014] | live | live | 21/21 |
| saxpy | serial-licensed-native | ifx-expert | 10.74 | 10.63 | 0.989 [0.970, 1.030] | live | live | 21/21 |
| saxpy | mt8-licensed-native | blade-llvm | serial-only: LLVM lane has no OpenMP | serial-only: LLVM lane has no OpenMP | | | | |
| saxpy | mt8-licensed-native | blade-cpp | 10.45 | 9.413 | 0.900 [0.836, 0.954] | ind | ind | 21/21 |
| saxpy | mt8-licensed-native | gfortran-natural | 9.992 | 9.994 | 1.000 [0.958, 1.058] | ind | ind | 21/21 |
| saxpy | mt8-licensed-native | gfortran-expert | 8.616 | 8.83 | 1.025 [0.985, 1.048] | ind | ind | 21/21 |
| saxpy | mt8-licensed-native | ifx-natural | 9.96 | 9.991 | 1.003 [0.975, 1.046] | ind | ind | 21/21 |
| saxpy | mt8-licensed-native | ifx-expert | 8.779 | 9.132 | 1.040 [0.976, 1.072] | ind | ind | 21/21 |
| saxpy | serial-repro-native | blade-llvm | 14.26 | 11.19 | 0.785 [0.774, 0.799] | live | live | 21/21 |
| saxpy | serial-repro-native | blade-cpp | 18.38 | 15.88 | 0.864 [0.850, 0.881] | live | live | 21/21 |
| saxpy | serial-repro-native | gfortran-natural | 16.57 | 16.82 | 1.015 [0.994, 1.025] | live | live | 21/21 |
| saxpy | serial-repro-native | gfortran-expert | 15.15 | 15.38 | 1.015 [1.000, 1.040] | live | live | 21/21 |
| saxpy | serial-repro-native | ifx-natural | 12.98 | 13.24 | 1.020 [1.003, 1.037] | live | live | 21/21 |
| saxpy | serial-repro-native | ifx-expert | 9.516 | 9.822 | 1.032 [1.001, 1.057] | live | live | 21/21 |
| saxpy | mt8-repro-native | blade-llvm | serial-only: LLVM lane has no OpenMP | serial-only: LLVM lane has no OpenMP | | | | |
| saxpy | mt8-repro-native | blade-cpp | 12.59 | 11.07 | 0.879 [0.854, 0.916] | ind | live | 21/21 |
| saxpy | mt8-repro-native | gfortran-natural | 10.07 | 9.978 | 0.991 [0.941, 1.038] | ind | live | 21/21 |
| saxpy | mt8-repro-native | gfortran-expert | 9.213 | 8.972 | 0.974 [0.949, 1.039] | ind | live | 21/21 |
| saxpy | mt8-repro-native | ifx-natural | 10.23 | 10.18 | 0.995 [0.950, 1.024] | ind | live | 21/21 |
| saxpy | mt8-repro-native | ifx-expert | 9.611 | 9.645 | 1.004 [0.959, 1.038] | ind | live | 21/21 |
| stencil | serial-repro-native | blade-llvm | 13.5 | 11.63 | 0.862 [0.848, 0.874] | live | live | 21/21 |
| stencil | serial-repro-native | blade-cpp | 17 | 15.42 | 0.907 [0.894, 0.915] | live | live | 21/21 |
| stencil | serial-repro-native | gfortran-natural | 16.86 | 16.77 | 0.994 [0.978, 1.006] | live | live | 21/21 |
| stencil | serial-repro-native | gfortran-expert | 16.59 | 16.47 | 0.993 [0.986, 1.003] | live | live | 21/21 |
| stencil | serial-repro-native | ifx-natural | 18.3 | 18.21 | 0.995 [0.988, 1.002] | live | live | 21/21 |
| stencil | serial-repro-native | ifx-expert | 13.31 | 13.2 | 0.991 [0.978, 1.005] | live | live | 21/21 |
| stencil | mt8-repro-native | blade-llvm | serial-only: LLVM lane has no OpenMP | serial-only: LLVM lane has no OpenMP | | | | |
| stencil | mt8-repro-native | blade-cpp | 9.854 | 9.524 | 0.966 [0.925, 1.033] | ind | ind | 21/21 |
| stencil | mt8-repro-native | gfortran-natural | 8.842 | 8.44 | 0.955 [0.910, 1.003] | ind | ind | 21/21 |
| stencil | mt8-repro-native | gfortran-expert | 7.8 | 7.833 | 1.004 [0.931, 1.038] | ind | ind | 21/21 |
| stencil | mt8-repro-native | ifx-natural | 9.674 | 10.25 | 1.060 [0.963, 1.073] | ind | ind | 21/21 |
| stencil | mt8-repro-native | ifx-expert | 8.43 | 8.335 | 0.989 [0.949, 1.047] | ind | ind | 21/21 |
| stencil | serial-licensed-native | blade-llvm | 10.5 | 10.35 | 0.986 [0.949, 1.030] | ind | live | 21/21 |
| stencil | serial-licensed-native | blade-cpp | 13.94 | 14.22 | 1.020 [0.998, 1.034] | ind | live | 21/21 |
| stencil | serial-licensed-native | gfortran-natural | 16.84 | 16.84 | 1.000 [0.995, 1.009] | ind | live | 21/21 |
| stencil | serial-licensed-native | gfortran-expert | 14.1 | 13.88 | 0.985 [0.974, 1.003] | ind | live | 21/21 |
| stencil | serial-licensed-native | ifx-natural | 18.5 | 18.4 | 0.995 [0.991, 1.003] | ind | live | 21/21 |
| stencil | serial-licensed-native | ifx-expert | 10.87 | 10.86 | 1.000 [0.987, 1.025] | ind | live | 21/21 |
| stencil | mt8-licensed-native | blade-llvm | serial-only: LLVM lane has no OpenMP | serial-only: LLVM lane has no OpenMP | | | | |
| stencil | mt8-licensed-native | blade-cpp | 8.756 | 8.583 | 0.980 [0.938, 1.037] | ind | ind | 21/21 |
| stencil | mt8-licensed-native | gfortran-natural | 8.906 | 8.571 | 0.962 [0.935, 1.048] | ind | ind | 21/21 |
| stencil | mt8-licensed-native | gfortran-expert | 7.635 | 7.751 | 1.015 [0.950, 1.045] | ind | ind | 21/21 |
| stencil | mt8-licensed-native | ifx-natural | 9.954 | 9.965 | 1.001 [0.953, 1.054] | ind | ind | 21/21 |
| stencil | mt8-licensed-native | ifx-expert | 8.094 | 8.329 | 1.029 [0.982, 1.075] | ind | ind | 21/21 |
| recur | serial-repro-native | blade-llvm | 12.95 | 9.646 | 0.745 [0.721, 0.762] | live | live | 21/21 |
| recur | serial-repro-native | blade-cpp | 14.36 | 11.26 | 0.784 [0.777, 0.805] | live | live | 21/21 |
| recur | serial-repro-native | gfortran-natural | 17.93 | 18.02 | 1.005 [0.991, 1.017] | live | live | 21/21 |
| recur | serial-repro-native | gfortran-expert | 12.87 | 12.89 | 1.002 [0.991, 1.014] | live | live | 21/21 |
| recur | serial-repro-native | ifx-natural | 14.85 | 14.96 | 1.008 [0.988, 1.025] | live | live | 21/21 |
| recur | serial-repro-native | ifx-expert | 13.16 | 13.2 | 1.003 [0.993, 1.016] | live | live | 21/21 |
| recur | serial-licensed-native | blade-llvm | 8.452 | 8.724 | 1.032 [0.993, 1.053] | live | live | 21/21 |
| recur | serial-licensed-native | blade-cpp | 10.3 | 10.21 | 0.992 [0.959, 1.025] | live | live | 21/21 |
| recur | serial-licensed-native | gfortran-natural | 18.36 | 18.31 | 0.997 [0.989, 1.006] | live | live | 21/21 |
| recur | serial-licensed-native | gfortran-expert | 9.429 | 9.382 | 0.995 [0.957, 1.036] | live | live | 21/21 |
| recur | serial-licensed-native | ifx-natural | 15.46 | 15.63 | 1.011 [1.002, 1.020] | live | live | 21/21 |
| recur | serial-licensed-native | ifx-expert | 9.857 | 9.717 | 0.986 [0.963, 1.055] | live | live | 21/21 |
| pairkernel | serial-licensed-native | blade-llvm | 29.1 | 27.94 | 0.960 [0.955, 0.965] | live | live | 21/21 |
| pairkernel | serial-licensed-native | blade-cpp | 29.27 | 28.14 | 0.961 [0.953, 0.964] | live | live | 21/21 |
| pairkernel | serial-licensed-native | gfortran-natural | 60.37 | 60.3 | 0.999 [0.996, 1.002] | live | live | 21/21 |
| pairkernel | serial-licensed-native | gfortran-expert | 32.57 | 32.53 | 0.999 [0.997, 1.001] | live | live | 21/21 |
| pairkernel | serial-licensed-native | ifx-natural | 63.87 | 63.94 | 1.001 [0.997, 1.009] | live | live | 21/21 |
| pairkernel | serial-licensed-native | ifx-expert | 30.28 | 30.34 | 1.002 [0.998, 1.005] | live | live | 21/21 |
| pairkernel | mt8-licensed-native | blade-llvm | serial-only: LLVM lane has no OpenMP | serial-only: LLVM lane has no OpenMP | | | | |
| pairkernel | mt8-licensed-native | blade-cpp | 7.686 | 6.157 | 0.801 [0.773, 0.843] | ind | ind | 21/21 |
| pairkernel | mt8-licensed-native | gfortran-natural | 13.58 | 14.49 | 1.067 [0.952, 1.107] | ind | ind | 21/21 |
| pairkernel | mt8-licensed-native | gfortran-expert | 6.125 | 6.28 | 1.025 [0.939, 1.065] | ind | ind | 21/21 |
| pairkernel | mt8-licensed-native | ifx-natural | 15.76 | 16.98 | 1.078 [0.915, 1.128] | ind | ind | 21/21 |
| pairkernel | mt8-licensed-native | ifx-expert | 6.078 | 5.955 | 0.980 [0.954, 1.052] | ind | ind | 21/21 |
| pairkernel | serial-repro-native | blade-llvm | 37.48 | 36.73 | 0.980 [0.970, 0.994] | live | live | 21/21 |
| pairkernel | serial-repro-native | blade-cpp | 37.81 | 37.2 | 0.984 [0.976, 0.993] | live | live | 21/21 |
| pairkernel | serial-repro-native | gfortran-natural | 60.42 | 60.5 | 1.001 [0.994, 1.004] | live | live | 21/21 |
| pairkernel | serial-repro-native | gfortran-expert | 34.31 | 34.23 | 0.998 [0.994, 1.000] | live | live | 21/21 |
| pairkernel | serial-repro-native | ifx-natural | 64.15 | 64.07 | 0.999 [0.990, 1.005] | live | live | 21/21 |
| pairkernel | serial-repro-native | ifx-expert | 32.26 | 32.28 | 1.000 [0.997, 1.006] | live | live | 21/21 |
| pairkernel | mt8-repro-native | blade-llvm | serial-only: LLVM lane has no OpenMP | serial-only: LLVM lane has no OpenMP | | | | |
| pairkernel | mt8-repro-native | blade-cpp | 16.6 | 9.059 | 0.546 [0.534, 0.563] | ind | live | 21/21 |
| pairkernel | mt8-repro-native | gfortran-natural | 13.79 | 14.04 | 1.018 [0.948, 1.081] | ind | live | 21/21 |
| pairkernel | mt8-repro-native | gfortran-expert | 6.362 | 6.227 | 0.979 [0.946, 1.055] | ind | live | 21/21 |
| pairkernel | mt8-repro-native | ifx-natural | 17.3 | 16.66 | 0.963 [0.894, 1.026] | ind | live | 21/21 |
| pairkernel | mt8-repro-native | ifx-expert | 6.173 | 6.139 | 0.994 [0.974, 1.020] | ind | live | 21/21 |
| comoment3 | serial-repro-native | blade-llvm | 14.98 | 14.96 | 0.998 [0.972, 1.022] | live | live | 21/21 |
| comoment3 | serial-repro-native | blade-cpp | 15.17 | 15.19 | 1.001 [0.984, 1.027] | live | live | 21/21 |
| comoment3 | serial-repro-native | gfortran-natural | 600.3 | 600.3 | 1.000 [0.999, 1.000] | live | live | 21/21 |
| comoment3 | serial-repro-native | gfortran-expert | 11.52 | 11.52 | 1.000 [0.976, 1.008] | live | live | 21/21 |
| comoment3 | serial-repro-native | ifx-natural | 718.1 | 718.6 | 1.001 [0.999, 1.003] | live | live | 21/21 |
| comoment3 | serial-repro-native | ifx-expert | 11.58 | 11.66 | 1.007 [0.990, 1.017] | live | live | 21/21 |
| comoment3 | mt8-repro-native | blade-llvm | serial-only: LLVM lane has no OpenMP | serial-only: LLVM lane has no OpenMP | | | | |
| comoment3 | mt8-repro-native | blade-cpp | 9.935 | 4.549 | 0.458 [0.443, 0.472] | live | live | 21/21 |
| comoment3 | mt8-repro-native | gfortran-natural | 92.51 | 88.98 | 0.962 [0.914, 1.030] | live | live | 21/21 |
| comoment3 | mt8-repro-native | gfortran-expert | 4.939 | 4.914 | 0.995 [0.937, 1.050] | live | live | 21/21 |
| comoment3 | mt8-repro-native | ifx-natural | 114.5 | 113.5 | 0.991 [0.942, 1.021] | live | live | 21/21 |
| comoment3 | mt8-repro-native | ifx-expert | 4.332 | 4.32 | 0.997 [0.971, 1.047] | live | live | 21/21 |
| comoment3 | serial-licensed-native | blade-llvm | 8.723 | 8.707 | 0.998 [0.987, 1.012] | live | live | 21/21 |
| comoment3 | serial-licensed-native | blade-cpp | 8.792 | 8.865 | 1.008 [0.988, 1.022] | live | live | 21/21 |
| comoment3 | serial-licensed-native | gfortran-natural | 600.5 | 600.2 | 0.999 [0.999, 1.000] | live | live | 21/21 |
| comoment3 | serial-licensed-native | gfortran-expert | 11.34 | 11.35 | 1.001 [0.987, 1.018] | live | live | 21/21 |
| comoment3 | serial-licensed-native | ifx-natural | 719.1 | 718.5 | 0.999 [0.997, 1.001] | live | live | 21/21 |
| comoment3 | serial-licensed-native | ifx-expert | 11.38 | 11.52 | 1.012 [0.988, 1.026] | live | live | 21/21 |
| maskagg | serial-licensed-native | blade-llvm | 4.691 | 3.73 | 0.795 [0.744, 0.813] | live | live | 21/21 |
| maskagg | serial-licensed-native | blade-cpp | 6.047 | 5.047 | 0.835 [0.781, 0.888] | live | live | 21/21 |
| maskagg | serial-licensed-native | gfortran-natural | 8.321 | 8.359 | 1.005 [0.991, 1.022] | live | live | 21/21 |
| maskagg | serial-licensed-native | gfortran-expert | 4.018 | 3.963 | 0.986 [0.936, 1.005] | live | live | 21/21 |
| maskagg | serial-licensed-native | ifx-natural | 10.52 | 10.37 | 0.986 [0.977, 1.010] | live | live | 21/21 |
| maskagg | serial-licensed-native | ifx-expert | 2.903 | 2.939 | 1.012 [0.990, 1.107] | live | live | 21/21 |
| maskagg | serial-repro-native | blade-llvm | 5.363 | 4.521 | 0.843 [0.822, 0.860] | live | live | 21/21 |
| maskagg | serial-repro-native | blade-cpp | 6.733 | 5.804 | 0.862 [0.799, 0.905] | live | live | 21/21 |
| maskagg | serial-repro-native | gfortran-natural | 8.237 | 8.266 | 1.004 [0.988, 1.011] | live | live | 21/21 |
| maskagg | serial-repro-native | gfortran-expert | 4.787 | 4.803 | 1.003 [0.989, 1.012] | live | live | 21/21 |
| maskagg | serial-repro-native | ifx-natural | 10.46 | 10.48 | 1.002 [0.981, 1.014] | live | live | 21/21 |
| maskagg | serial-repro-native | ifx-expert | 3.69 | 3.677 | 0.997 [0.986, 1.008] | live | live | 21/21 |
| dot | serial-licensed-native | blade-llvm | 12.56 | 11.23 | 0.894 [0.880, 0.914] | live | live | 21/21 |
| dot | serial-licensed-native | blade-cpp | 18.92 | 17.41 | 0.920 [0.904, 0.927] | live | live | 21/21 |
| dot | serial-licensed-native | gfortran-natural | 20.04 | 20.07 | 1.001 [0.994, 1.012] | live | live | 21/21 |
| dot | serial-licensed-native | gfortran-expert | 18.27 | 18.44 | 1.009 [0.984, 1.024] | live | live | 21/21 |
| dot | serial-licensed-native | ifx-natural | 13.89 | 14.06 | 1.013 [0.993, 1.026] | live | live | 21/21 |
| dot | serial-licensed-native | ifx-expert | 12.19 | 12.24 | 1.004 [0.980, 1.022] | live | live | 21/21 |
| dot | serial-repro-native | blade-llvm | 16.45 | 11.77 | 0.715 [0.706, 0.728] | live | live | 21/21 |
| dot | serial-repro-native | blade-cpp | 22.4 | 18.08 | 0.807 [0.799, 0.821] | live | live | 21/21 |
| dot | serial-repro-native | gfortran-natural | 19.99 | 20.04 | 1.003 [0.988, 1.010] | live | live | 21/21 |
| dot | serial-repro-native | gfortran-expert | 19.94 | 19.95 | 1.001 [0.996, 1.007] | live | live | 21/21 |
| dot | serial-repro-native | ifx-natural | 13.88 | 13.96 | 1.006 [0.994, 1.018] | live | live | 21/21 |
| dot | serial-repro-native | ifx-expert | 12.16 | 11.91 | 0.980 [0.959, 1.004] | live | live | 21/21 |
| dot | serial-fastmath-native | blade-llvm | 12.94 | 11.03 | 0.853 [0.831, 0.864] | live | live | 21/21 |
| dot | serial-fastmath-native | blade-cpp | 18.85 | 17.3 | 0.918 [0.907, 0.921] | live | live | 21/21 |
| dot | serial-fastmath-native | gfortran-natural | 18.21 | 17.95 | 0.986 [0.970, 0.994] | live | live | 21/21 |
| dot | serial-fastmath-native | gfortran-expert | 18.05 | 18 | 0.997 [0.991, 1.018] | live | live | 21/21 |
| dot | serial-fastmath-native | ifx-natural | 12.34 | 12.08 | 0.980 [0.956, 0.991] | live | live | 21/21 |
| dot | serial-fastmath-native | ifx-expert | 8.68 | 8.625 | 0.994 [0.935, 1.022] | live | live | 21/21 |
| dot | mt8-repro-native | blade-llvm | serial-only: LLVM lane has no OpenMP | serial-only: LLVM lane has no OpenMP | | | | |
| dot | mt8-repro-native | blade-cpp | 13.84 | 9.787 | 0.707 [0.680, 0.742] | ind | live | 21/21 |
| dot | mt8-repro-native | gfortran-natural | 11.31 | 11.06 | 0.978 [0.956, 1.016] | ind | live | 21/21 |
| dot | mt8-repro-native | gfortran-expert | 11.27 | 11.38 | 1.010 [0.969, 1.046] | ind | live | 21/21 |
| dot | mt8-repro-native | ifx-natural | 11.48 | 11.43 | 0.996 [0.957, 1.022] | ind | live | 21/21 |
| dot | mt8-repro-native | ifx-expert | 11.89 | 11.57 | 0.973 [0.962, 1.018] | ind | live | 21/21 |
| onepass | serial-licensed-native | blade-llvm | 11.45 | 10.24 | 0.894 [0.878, 0.922] | live | live | 21/21 |
| onepass | serial-licensed-native | blade-cpp | 16.27 | 15.21 | 0.935 [0.912, 0.938] | live | live | 21/21 |
| onepass | serial-licensed-native | gfortran-natural | 32.62 | 32.65 | 1.001 [0.997, 1.003] | live | live | 21/21 |
| onepass | serial-licensed-native | gfortran-expert | 16.59 | 16.74 | 1.009 [0.991, 1.031] | live | live | 21/21 |
| onepass | serial-licensed-native | ifx-natural | 12.8 | 12.78 | 0.999 [0.988, 1.021] | live | live | 21/21 |
| onepass | serial-licensed-native | ifx-expert | 11.51 | 11.46 | 0.995 [0.985, 1.023] | live | live | 21/21 |
| onepass | serial-repro-native | blade-llvm | 15.68 | 12.34 | 0.787 [0.774, 0.792] | live | live | 21/21 |
| onepass | serial-repro-native | blade-cpp | 21.05 | 17.42 | 0.827 [0.825, 0.833] | live | live | 21/21 |
| onepass | serial-repro-native | gfortran-natural | 32.64 | 32.64 | 1.000 [0.995, 1.005] | live | live | 21/21 |
| onepass | serial-repro-native | gfortran-expert | 21.13 | 21.14 | 1.000 [0.995, 1.005] | live | live | 21/21 |
| onepass | serial-repro-native | ifx-natural | 12.69 | 12.93 | 1.019 [0.999, 1.034] | live | live | 21/21 |
| onepass | serial-repro-native | ifx-expert | 12.59 | 12.87 | 1.023 [1.004, 1.040] | live | live | 21/21 |
| onepass | serial-fastmath-native | blade-llvm | 11.35 | 10.06 | 0.887 [0.860, 0.900] | live | live | 21/21 |
| onepass | serial-fastmath-native | blade-cpp | 16.31 | 15.18 | 0.931 [0.920, 0.933] | live | live | 21/21 |
| onepass | serial-fastmath-native | gfortran-natural | 20.26 | 20.35 | 1.005 [0.983, 1.022] | live | live | 21/21 |
| onepass | serial-fastmath-native | gfortran-expert | 16.76 | 16.74 | 0.999 [0.987, 1.021] | live | live | 21/21 |
| onepass | serial-fastmath-native | ifx-natural | 8.244 | 8.109 | 0.984 [0.954, 1.018] | live | live | 21/21 |
| onepass | serial-fastmath-native | ifx-expert | 8.452 | 8.254 | 0.977 [0.954, 1.013] | live | live | 21/21 |
| onepass | mt8-repro-native | blade-llvm | serial-only: LLVM lane has no OpenMP | serial-only: LLVM lane has no OpenMP | | | | |
| onepass | mt8-repro-native | blade-cpp | no-parallel-form: kernel.json no_parallel_form (spec: no licensed parallel spelling) | no-parallel-form: kernel.json no_parallel_form (spec: no licensed parallel spelling) | | | | |
| onepass | mt8-repro-native | gfortran-natural | 15.03 | 14.72 | 0.980 [0.950, 1.037] | ind | live | 21/21 |
| onepass | mt8-repro-native | gfortran-expert | 10.9 | 10.61 | 0.973 [0.960, 1.043] | ind | live | 21/21 |
| onepass | mt8-repro-native | ifx-natural | 15.14 | 15.42 | 1.018 [0.987, 1.055] | ind | live | 21/21 |
| onepass | mt8-repro-native | ifx-expert | 11.49 | 11.27 | 0.981 [0.963, 1.011] | ind | live | 21/21 |
| fusedchain | serial-licensed-native | blade-llvm | 4.259 | 4.018 | 0.943 [0.930, 0.961] | ind | live | 21/21 |
| fusedchain | serial-licensed-native | blade-cpp | 6.462 | 6.411 | 0.992 [0.967, 1.024] | ind | live | 21/21 |
| fusedchain | serial-licensed-native | gfortran-natural | 17.14 | 17.13 | 0.999 [0.985, 1.019] | ind | live | 21/21 |
| fusedchain | serial-licensed-native | gfortran-expert | 6.647 | 6.865 | 1.033 [1.001, 1.054] | ind | live | 21/21 |
| fusedchain | serial-licensed-native | ifx-natural | 13.02 | 13.22 | 1.015 [0.989, 1.038] | ind | live | 21/21 |
| fusedchain | serial-licensed-native | ifx-expert | 4.397 | 4.459 | 1.014 [1.000, 1.089] | ind | live | 21/21 |
| fusedchain | serial-repro-native | blade-llvm | 5.917 | 4.463 | 0.754 [0.749, 0.780] | live | live | 21/21 |
| fusedchain | serial-repro-native | blade-cpp | 8.344 | 6.813 | 0.817 [0.800, 0.842] | live | live | 21/21 |
| fusedchain | serial-repro-native | gfortran-natural | 17.06 | 17.29 | 1.013 [0.999, 1.028] | live | live | 21/21 |
| fusedchain | serial-repro-native | gfortran-expert | 8.239 | 8.265 | 1.003 [0.976, 1.020] | live | live | 21/21 |
| fusedchain | serial-repro-native | ifx-natural | 12.96 | 13.22 | 1.020 [0.991, 1.037] | live | live | 21/21 |
| fusedchain | serial-repro-native | ifx-expert | 4.868 | 4.88 | 1.002 [0.994, 1.016] | live | live | 21/21 |
| fusedchain | serial-fastmath-native | blade-llvm | 4.546 | 3.894 | 0.857 [0.820, 0.892] | ind | ind | 21/21 |
| fusedchain | serial-fastmath-native | blade-cpp | 6.784 | 6.316 | 0.931 [0.880, 0.960] | ind | ind | 21/21 |
| fusedchain | serial-fastmath-native | gfortran-natural | 15.42 | 15.27 | 0.990 [0.974, 1.006] | ind | ind | 21/21 |
| fusedchain | serial-fastmath-native | gfortran-expert | 6.724 | 6.574 | 0.978 [0.954, 1.006] | ind | ind | 21/21 |
| fusedchain | serial-fastmath-native | ifx-natural | 9.788 | 9.558 | 0.976 [0.934, 1.027] | ind | ind | 21/21 |
| fusedchain | serial-fastmath-native | ifx-expert | 3.673 | 3.666 | 0.998 [0.994, 1.018] | ind | ind | 21/21 |
| fusedchain | mt8-repro-native | blade-llvm | serial-only: LLVM lane has no OpenMP | serial-only: LLVM lane has no OpenMP | | | | |
| fusedchain | mt8-repro-native | blade-cpp | 5.579 | 4.054 | 0.727 [0.708, 0.744] | ind | ind | 21/21 |
| fusedchain | mt8-repro-native | gfortran-natural | 10.98 | 11.52 | 1.049 [0.995, 1.102] | ind | ind | 21/21 |
| fusedchain | mt8-repro-native | gfortran-expert | 4.076 | 4.124 | 1.012 [0.984, 1.038] | ind | ind | 21/21 |
| fusedchain | mt8-repro-native | ifx-natural | 11.47 | 11.51 | 1.003 [0.957, 1.041] | ind | ind | 21/21 |
| fusedchain | mt8-repro-native | ifx-expert | 4.611 | 4.571 | 0.991 [0.948, 1.032] | ind | ind | 21/21 |
| gram | mt8-licensed-native | blade-llvm | serial-only: LLVM lane has no OpenMP | serial-only: LLVM lane has no OpenMP | | | | |
| gram | mt8-licensed-native | blade-cpp | 16.96 | 17.23 | 1.016 [0.982, 1.047] | live | live | 21/21 |
| gram | mt8-licensed-native | gfortran-natural | 658.5 | 660.2 | 1.003 [0.970, 1.046] | live | live | 21/21 |
| gram | mt8-licensed-native | gfortran-expert | 21.78 | 21.83 | 1.002 [0.981, 1.033] | live | live | 21/21 |
| gram | mt8-licensed-native | ifx-natural | 581.2 | 582.8 | 1.003 [0.994, 1.011] | live | live | 21/21 |
| gram | mt8-licensed-native | ifx-expert | 20.63 | 20.98 | 1.017 [0.974, 1.032] | live | live | 21/21 |

## 2. Input-only twins (same columns)

| kernel | regime | arm | OLD median (ms) | NEW median (ms) | NEW/OLD [95% CI, analyst unpaired bootstrap] | OLD block | NEW block | n OLD/NEW |
|---|---|---|---|---|---|---|---|---|
| saxpy | serial-licensed-native | blade-llvm-inputonly | 7.828 | 7.515 | 0.960 [0.945, 1.003] | live | live | 21/21 |
| saxpy | serial-licensed-native | blade-cpp-inputonly | 11.85 | 11.76 | 0.992 [0.983, 0.998] | live | live | 21/21 |
| saxpy | serial-licensed-native | gfortran-natural-inputonly | 11.81 | 11.86 | 1.004 [0.998, 1.012] | live | live | 21/21 |
| saxpy | serial-licensed-native | gfortran-expert-inputonly | 11.54 | 11.57 | 1.003 [0.994, 1.011] | live | live | 21/21 |
| saxpy | serial-licensed-native | ifx-natural-inputonly | 7.714 | 7.816 | 1.013 [0.981, 1.028] | live | live | 21/21 |
| saxpy | serial-licensed-native | ifx-expert-inputonly | 7.268 | 7.456 | 1.026 [0.996, 1.035] | live | live | 21/21 |
| saxpy | mt8-licensed-native | blade-llvm-inputonly | serial-only: LLVM lane has no OpenMP | serial-only: LLVM lane has no OpenMP | | | | |
| saxpy | mt8-licensed-native | blade-cpp-inputonly | 6.032 | 5.896 | 0.977 [0.857, 1.047] | ind | ind | 21/21 |
| saxpy | mt8-licensed-native | gfortran-natural-inputonly | 5.712 | 5.579 | 0.977 [0.923, 1.039] | ind | ind | 21/21 |
| saxpy | mt8-licensed-native | gfortran-expert-inputonly | 5.163 | 5.093 | 0.986 [0.942, 1.035] | ind | ind | 21/21 |
| saxpy | mt8-licensed-native | ifx-natural-inputonly | 5.889 | 5.942 | 1.009 [0.963, 1.064] | ind | ind | 21/21 |
| saxpy | mt8-licensed-native | ifx-expert-inputonly | 5.506 | 5.561 | 1.010 [0.993, 1.077] | ind | ind | 21/21 |
| saxpy | serial-repro-native | blade-llvm-inputonly | 8.881 | 7.895 | 0.889 [0.864, 0.924] | live | live | 21/21 |
| saxpy | serial-repro-native | blade-cpp-inputonly | 13.31 | 12.16 | 0.914 [0.902, 0.923] | live | live | 21/21 |
| saxpy | serial-repro-native | gfortran-natural-inputonly | 11.69 | 11.82 | 1.011 [1.007, 1.027] | live | live | 21/21 |
| saxpy | serial-repro-native | gfortran-expert-inputonly | 11.68 | 11.87 | 1.016 [1.012, 1.030] | live | live | 21/21 |
| saxpy | serial-repro-native | ifx-natural-inputonly | 7.467 | 7.726 | 1.035 [1.017, 1.061] | live | live | 21/21 |
| saxpy | serial-repro-native | ifx-expert-inputonly | 7.701 | 7.857 | 1.020 [0.988, 1.056] | live | live | 21/21 |
| saxpy | mt8-repro-native | blade-llvm-inputonly | serial-only: LLVM lane has no OpenMP | serial-only: LLVM lane has no OpenMP | | | | |
| saxpy | mt8-repro-native | blade-cpp-inputonly | 7.876 | 6.488 | 0.824 [0.729, 0.905] | ind | live | 21/21 |
| saxpy | mt8-repro-native | gfortran-natural-inputonly | 5.848 | 5.577 | 0.954 [0.889, 1.034] | ind | live | 21/21 |
| saxpy | mt8-repro-native | gfortran-expert-inputonly | 6.071 | 5.493 | 0.905 [0.868, 1.054] | ind | live | 21/21 |
| saxpy | mt8-repro-native | ifx-natural-inputonly | 6.193 | 5.917 | 0.955 [0.872, 1.008] | ind | live | 21/21 |
| saxpy | mt8-repro-native | ifx-expert-inputonly | 6.071 | 6.01 | 0.990 [0.916, 1.086] | ind | live | 21/21 |
| stencil | serial-repro-native | blade-llvm-inputonly | 6.862 | 6.862 | 1.000 [0.981, 1.011] | live | live | 21/21 |
| stencil | serial-repro-native | blade-cpp-inputonly | 10.01 | 10.02 | 1.001 [0.983, 1.008] | live | live | 21/21 |
| stencil | serial-repro-native | gfortran-natural-inputonly | 10.09 | 10.01 | 0.992 [0.973, 1.000] | live | live | 21/21 |
| stencil | serial-repro-native | gfortran-expert-inputonly | 10.08 | 10.07 | 0.999 [0.984, 1.020] | live | live | 21/21 |
| stencil | serial-repro-native | ifx-natural-inputonly | 6.758 | 6.688 | 0.990 [0.948, 1.029] | live | live | 21/21 |
| stencil | serial-repro-native | ifx-expert-inputonly | 6.526 | 6.342 | 0.972 [0.929, 0.987] | live | live | 21/21 |
| stencil | mt8-repro-native | blade-llvm-inputonly | serial-only: LLVM lane has no OpenMP | serial-only: LLVM lane has no OpenMP | | | | |
| stencil | mt8-repro-native | blade-cpp-inputonly | 5.637 | 5.538 | 0.983 [0.913, 1.067] | ind | ind | 21/21 |
| stencil | mt8-repro-native | gfortran-natural-inputonly | 4.282 | 3.993 | 0.932 [0.900, 1.030] | ind | ind | 21/21 |
| stencil | mt8-repro-native | gfortran-expert-inputonly | 4.142 | 3.974 | 0.960 [0.943, 1.015] | ind | ind | 21/21 |
| stencil | mt8-repro-native | ifx-natural-inputonly | 4.542 | 4.508 | 0.992 [0.960, 1.054] | ind | ind | 21/21 |
| stencil | mt8-repro-native | ifx-expert-inputonly | 4.659 | 4.349 | 0.933 [0.915, 0.984] | ind | ind | 21/21 |
| stencil | serial-licensed-native | blade-llvm-inputonly | 5.899 | 5.957 | 1.010 [0.988, 1.028] | ind | live | 21/21 |
| stencil | serial-licensed-native | blade-cpp-inputonly | 9.001 | 8.996 | 0.999 [0.981, 1.011] | ind | live | 21/21 |
| stencil | serial-licensed-native | gfortran-natural-inputonly | 10.04 | 10.04 | 1.000 [0.984, 1.011] | ind | live | 21/21 |
| stencil | serial-licensed-native | gfortran-expert-inputonly | 9.086 | 9.122 | 1.004 [0.959, 1.037] | ind | live | 21/21 |
| stencil | serial-licensed-native | ifx-natural-inputonly | 6.61 | 6.612 | 1.000 [0.984, 1.054] | ind | live | 21/21 |
| stencil | serial-licensed-native | ifx-expert-inputonly | 5.625 | 5.603 | 0.996 [0.981, 1.028] | ind | live | 21/21 |
| stencil | mt8-licensed-native | blade-llvm-inputonly | serial-only: LLVM lane has no OpenMP | serial-only: LLVM lane has no OpenMP | | | | |
| stencil | mt8-licensed-native | blade-cpp-inputonly | 4.384 | 4.407 | 1.005 [0.964, 1.072] | ind | ind | 21/21 |
| stencil | mt8-licensed-native | gfortran-natural-inputonly | 4.307 | 4.144 | 0.962 [0.875, 0.990] | ind | ind | 21/21 |
| stencil | mt8-licensed-native | gfortran-expert-inputonly | 4.315 | 4.177 | 0.968 [0.922, 1.024] | ind | ind | 21/21 |
| stencil | mt8-licensed-native | ifx-natural-inputonly | 4.715 | 4.528 | 0.960 [0.902, 1.002] | ind | ind | 21/21 |
| stencil | mt8-licensed-native | ifx-expert-inputonly | 4.693 | 4.438 | 0.946 [0.908, 0.980] | ind | ind | 21/21 |
| maskagg | serial-licensed-native | blade-llvm-inputonly | 1.743 | 1.713 | 0.983 [0.905, 1.028] | live | live | 21/21 |
| maskagg | serial-licensed-native | blade-cpp-inputonly | 2.849 | 2.817 | 0.989 [0.951, 0.999] | live | live | 21/21 |
| maskagg | serial-licensed-native | gfortran-natural-inputonly | 3.311 | 3.321 | 1.003 [0.995, 1.009] | live | live | 21/21 |
| maskagg | serial-licensed-native | gfortran-expert-inputonly | 2.907 | 2.92 | 1.005 [0.993, 1.013] | live | live | 21/21 |
| maskagg | serial-licensed-native | ifx-natural-inputonly | 2.182 | 2.166 | 0.993 [0.974, 0.997] | live | live | 21/21 |
| maskagg | serial-licensed-native | ifx-expert-inputonly | 1.736 | 1.727 | 0.995 [0.979, 1.011] | live | live | 21/21 |
| maskagg | serial-repro-native | blade-llvm-inputonly | 2.19 | 2.183 | 0.997 [0.988, 1.006] | live | live | 21/21 |
| maskagg | serial-repro-native | blade-cpp-inputonly | 3.297 | 3.298 | 1.000 [0.991, 1.016] | live | live | 21/21 |
| maskagg | serial-repro-native | gfortran-natural-inputonly | 3.336 | 3.3 | 0.990 [0.959, 1.001] | live | live | 21/21 |
| maskagg | serial-repro-native | gfortran-expert-inputonly | 3.302 | 3.315 | 1.004 [0.992, 1.026] | live | live | 21/21 |
| maskagg | serial-repro-native | ifx-natural-inputonly | 2.181 | 2.176 | 0.998 [0.987, 1.005] | live | live | 21/21 |
| maskagg | serial-repro-native | ifx-expert-inputonly | 2.178 | 2.178 | 1.000 [0.993, 1.014] | live | live | 21/21 |
| dot | serial-licensed-native | blade-llvm-inputonly | 11.23 | 11.12 | 0.990 [0.961, 0.999] | live | live | 21/21 |
| dot | serial-licensed-native | blade-cpp-inputonly | 17.42 | 17.33 | 0.995 [0.991, 1.000] | live | live | 21/21 |
| dot | serial-licensed-native | gfortran-natural-inputonly | 17.57 | 17.64 | 1.004 [0.996, 1.009] | live | live | 21/21 |
| dot | serial-licensed-native | gfortran-expert-inputonly | 16.9 | 17.01 | 1.006 [1.003, 1.011] | live | live | 21/21 |
| dot | serial-licensed-native | ifx-natural-inputonly | 11.59 | 11.5 | 0.992 [0.970, 1.012] | live | live | 21/21 |
| dot | serial-licensed-native | ifx-expert-inputonly | 10.74 | 10.93 | 1.018 [0.991, 1.043] | live | live | 21/21 |
| dot | serial-repro-native | blade-llvm-inputonly | 13.76 | 11.69 | 0.849 [0.828, 0.866] | live | live | 21/21 |
| dot | serial-repro-native | blade-cpp-inputonly | 20.01 | 18.14 | 0.907 [0.899, 0.920] | live | live | 21/21 |
| dot | serial-repro-native | gfortran-natural-inputonly | 17.55 | 17.66 | 1.006 [0.998, 1.014] | live | live | 21/21 |
| dot | serial-repro-native | gfortran-expert-inputonly | 17.58 | 17.6 | 1.001 [0.982, 1.014] | live | live | 21/21 |
| dot | serial-repro-native | ifx-natural-inputonly | 11.64 | 11.54 | 0.991 [0.976, 1.017] | live | live | 21/21 |
| dot | serial-repro-native | ifx-expert-inputonly | 11.47 | 11.53 | 1.005 [0.993, 1.026] | live | live | 21/21 |
| dot | serial-fastmath-native | blade-llvm-inputonly | 11.39 | 10.91 | 0.958 [0.936, 0.967] | live | live | 21/21 |
| dot | serial-fastmath-native | blade-cpp-inputonly | 17.49 | 17.24 | 0.986 [0.980, 0.991] | live | live | 21/21 |
| dot | serial-fastmath-native | gfortran-natural-inputonly | 16.77 | 16.63 | 0.991 [0.978, 0.996] | live | live | 21/21 |
| dot | serial-fastmath-native | gfortran-expert-inputonly | 16.75 | 16.63 | 0.993 [0.988, 1.000] | live | live | 21/21 |
| dot | serial-fastmath-native | ifx-natural-inputonly | 8.921 | 8.512 | 0.954 [0.938, 1.015] | live | live | 21/21 |
| dot | serial-fastmath-native | ifx-expert-inputonly | 8.616 | 8.7 | 1.010 [0.957, 1.032] | live | live | 21/21 |
| dot | mt8-repro-native | blade-llvm-inputonly | serial-only: LLVM lane has no OpenMP | serial-only: LLVM lane has no OpenMP | | | | |
| dot | mt8-repro-native | blade-cpp-inputonly | 11.5 | 9.367 | 0.815 [0.765, 0.870] | ind | live | 21/21 |
| dot | mt8-repro-native | gfortran-natural-inputonly | 9.096 | 8.732 | 0.960 [0.918, 1.030] | ind | live | 21/21 |
| dot | mt8-repro-native | gfortran-expert-inputonly | 9.189 | 8.458 | 0.920 [0.896, 1.005] | ind | live | 21/21 |
| dot | mt8-repro-native | ifx-natural-inputonly | 9.588 | 9.06 | 0.945 [0.902, 1.035] | ind | live | 21/21 |
| dot | mt8-repro-native | ifx-expert-inputonly | 8.871 | 9.416 | 1.061 [1.028, 1.088] | ind | live | 21/21 |
| onepass | serial-licensed-native | blade-llvm-inputonly | 9.483 | 9.57 | 1.009 [0.990, 1.046] | live | live | 21/21 |
| onepass | serial-licensed-native | blade-cpp-inputonly | 14.75 | 14.77 | 1.001 [0.982, 1.006] | live | live | 21/21 |
| onepass | serial-licensed-native | gfortran-natural-inputonly | 16.78 | 16.79 | 1.001 [0.994, 1.005] | live | live | 21/21 |
| onepass | serial-licensed-native | gfortran-expert-inputonly | 14.9 | 14.92 | 1.001 [0.994, 1.008] | live | live | 21/21 |
| onepass | serial-licensed-native | ifx-natural-inputonly | 11.36 | 11.55 | 1.017 [1.004, 1.041] | live | live | 21/21 |
| onepass | serial-licensed-native | ifx-expert-inputonly | 9.608 | 9.635 | 1.003 [0.976, 1.025] | live | live | 21/21 |
| onepass | serial-repro-native | blade-llvm-inputonly | 11.42 | 11.4 | 0.998 [0.983, 1.027] | live | live | 21/21 |
| onepass | serial-repro-native | blade-cpp-inputonly | 16.68 | 16.9 | 1.013 [1.001, 1.021] | live | live | 21/21 |
| onepass | serial-repro-native | gfortran-natural-inputonly | 16.8 | 16.8 | 1.000 [0.996, 1.007] | live | live | 21/21 |
| onepass | serial-repro-native | gfortran-expert-inputonly | 16.82 | 16.82 | 1.000 [0.996, 1.008] | live | live | 21/21 |
| onepass | serial-repro-native | ifx-natural-inputonly | 11.46 | 11.58 | 1.011 [0.986, 1.037] | live | live | 21/21 |
| onepass | serial-repro-native | ifx-expert-inputonly | 11.34 | 11.53 | 1.017 [1.001, 1.041] | live | live | 21/21 |
| onepass | serial-fastmath-native | blade-llvm-inputonly | 9.611 | 9.577 | 0.996 [0.976, 1.025] | live | live | 21/21 |
| onepass | serial-fastmath-native | blade-cpp-inputonly | 14.84 | 14.78 | 0.996 [0.988, 1.005] | live | live | 21/21 |
| onepass | serial-fastmath-native | gfortran-natural-inputonly | 14.7 | 14.7 | 1.000 [0.996, 1.005] | live | live | 21/21 |
| onepass | serial-fastmath-native | gfortran-expert-inputonly | 14.77 | 14.71 | 0.996 [0.983, 1.006] | live | live | 21/21 |
| onepass | serial-fastmath-native | ifx-natural-inputonly | 7.495 | 7.372 | 0.984 [0.965, 1.046] | live | live | 21/21 |
| onepass | serial-fastmath-native | ifx-expert-inputonly | 7.431 | 7.24 | 0.974 [0.958, 1.025] | live | live | 21/21 |
| onepass | mt8-repro-native | blade-llvm-inputonly | serial-only: LLVM lane has no OpenMP | serial-only: LLVM lane has no OpenMP | | | | |
| onepass | mt8-repro-native | blade-cpp-inputonly | no-parallel-form: kernel.json no_parallel_form (spec: no licensed parallel spelling) | no-parallel-form: kernel.json no_parallel_form (spec: no licensed parallel spelling) | | | | |
| onepass | mt8-repro-native | gfortran-natural-inputonly | 8.6 | 8.312 | 0.966 [0.905, 1.035] | ind | live | 21/21 |
| onepass | mt8-repro-native | gfortran-expert-inputonly | 8.357 | 8.352 | 0.999 [0.944, 1.047] | ind | live | 21/21 |
| onepass | mt8-repro-native | ifx-natural-inputonly | 8.908 | 8.835 | 0.992 [0.923, 1.017] | ind | live | 21/21 |
| onepass | mt8-repro-native | ifx-expert-inputonly | 8.814 | 8.732 | 0.991 [0.973, 1.025] | ind | live | 21/21 |
| fusedchain | serial-licensed-native | blade-llvm-inputonly | 3.607 | 3.658 | 1.014 [0.943, 1.081] | ind | live | 21/21 |
| fusedchain | serial-licensed-native | blade-cpp-inputonly | 5.998 | 5.894 | 0.983 [0.951, 1.027] | ind | live | 21/21 |
| fusedchain | serial-licensed-native | gfortran-natural-inputonly | 6.704 | 6.579 | 0.981 [0.968, 1.013] | ind | live | 21/21 |
| fusedchain | serial-licensed-native | gfortran-expert-inputonly | 5.868 | 6.185 | 1.054 [1.040, 1.071] | ind | live | 21/21 |
| fusedchain | serial-licensed-native | ifx-natural-inputonly | 4.353 | 4.365 | 1.003 [0.996, 1.038] | ind | live | 21/21 |
| fusedchain | serial-licensed-native | ifx-expert-inputonly | 3.598 | 3.673 | 1.021 [1.006, 1.047] | ind | live | 21/21 |
| fusedchain | serial-repro-native | blade-llvm-inputonly | 4.318 | 4.465 | 1.034 [0.998, 1.064] | live | live | 21/21 |
| fusedchain | serial-repro-native | blade-cpp-inputonly | 6.594 | 6.539 | 0.992 [0.967, 1.042] | live | live | 21/21 |
| fusedchain | serial-repro-native | gfortran-natural-inputonly | 6.569 | 6.699 | 1.020 [0.971, 1.031] | live | live | 21/21 |
| fusedchain | serial-repro-native | gfortran-expert-inputonly | 6.598 | 6.529 | 0.990 [0.967, 1.025] | live | live | 21/21 |
| fusedchain | serial-repro-native | ifx-natural-inputonly | 4.372 | 4.371 | 1.000 [0.986, 1.041] | live | live | 21/21 |
| fusedchain | serial-repro-native | ifx-expert-inputonly | 4.372 | 4.354 | 0.996 [0.971, 1.006] | live | live | 21/21 |
| fusedchain | serial-fastmath-native | blade-llvm-inputonly | 3.744 | 3.526 | 0.942 [0.876, 0.962] | ind | ind | 21/21 |
| fusedchain | serial-fastmath-native | blade-cpp-inputonly | 5.977 | 5.831 | 0.976 [0.936, 1.009] | ind | ind | 21/21 |
| fusedchain | serial-fastmath-native | gfortran-natural-inputonly | 6.01 | 5.873 | 0.977 [0.963, 1.014] | ind | ind | 21/21 |
| fusedchain | serial-fastmath-native | gfortran-expert-inputonly | 6.153 | 6.036 | 0.981 [0.948, 1.008] | ind | ind | 21/21 |
| fusedchain | serial-fastmath-native | ifx-natural-inputonly | 2.827 | 2.841 | 1.005 [0.995, 1.011] | ind | ind | 21/21 |
| fusedchain | serial-fastmath-native | ifx-expert-inputonly | 2.795 | 2.84 | 1.016 [0.998, 1.024] | ind | ind | 21/21 |
| fusedchain | mt8-repro-native | blade-llvm-inputonly | serial-only: LLVM lane has no OpenMP | serial-only: LLVM lane has no OpenMP | | | | |
| fusedchain | mt8-repro-native | blade-cpp-inputonly | 3.99 | 3.987 | 0.999 [0.916, 1.041] | ind | ind | 21/21 |
| fusedchain | mt8-repro-native | gfortran-natural-inputonly | 3.531 | 3.514 | 0.995 [0.915, 1.039] | ind | ind | 21/21 |
| fusedchain | mt8-repro-native | gfortran-expert-inputonly | 3.554 | 3.448 | 0.970 [0.951, 1.021] | ind | ind | 21/21 |
| fusedchain | mt8-repro-native | ifx-natural-inputonly | 4.006 | 3.964 | 0.990 [0.954, 1.012] | ind | ind | 21/21 |
| fusedchain | mt8-repro-native | ifx-expert-inputonly | 4.029 | 4.083 | 1.014 [0.981, 1.050] | ind | ind | 21/21 |

## 2b. Net = median - own twin median (ms)

| kernel | regime | arm | OLD net (ms) | NEW net (ms) | NEW/OLD net |
|---|---|---|---|---|---|
| saxpy | serial-licensed-native | blade-llvm | 3.778 | 3.416 | 0.904 |
| saxpy | serial-licensed-native | blade-cpp | 4.928 | 3.686 | 0.748 |
| saxpy | serial-licensed-native | gfortran-natural | 5.01 | 4.943 | 0.987 |
| saxpy | serial-licensed-native | gfortran-expert | 3.03 | 3.48 | 1.149 |
| saxpy | serial-licensed-native | ifx-natural | 5.497 | 5.455 | 0.992 |
| saxpy | serial-licensed-native | ifx-expert | 3.472 | 3.17 | 0.913 |
| saxpy | mt8-licensed-native | blade-cpp | 4.421 | 3.517 | 0.795 |
| saxpy | mt8-licensed-native | gfortran-natural | 4.28 | 4.414 | 1.031 |
| saxpy | mt8-licensed-native | gfortran-expert | 3.453 | 3.737 | 1.082 |
| saxpy | mt8-licensed-native | ifx-natural | 4.071 | 4.049 | 0.995 |
| saxpy | mt8-licensed-native | ifx-expert | 3.274 | 3.571 | 1.091 |
| saxpy | serial-repro-native | blade-llvm | 5.38 | 3.296 | 0.613 |
| saxpy | serial-repro-native | blade-cpp | 5.074 | 3.719 | 0.733 |
| saxpy | serial-repro-native | gfortran-natural | 4.878 | 5.005 | 1.026 |
| saxpy | serial-repro-native | gfortran-expert | 3.466 | 3.515 | 1.014 |
| saxpy | serial-repro-native | ifx-natural | 5.51 | 5.511 | 1.000 |
| saxpy | serial-repro-native | ifx-expert | 1.815 | 1.965 | 1.083 |
| saxpy | mt8-repro-native | blade-cpp | 4.714 | 4.581 | 0.972 |
| saxpy | mt8-repro-native | gfortran-natural | 4.217 | 4.401 | 1.044 |
| saxpy | mt8-repro-native | gfortran-expert | 3.142 | 3.479 | 1.107 |
| saxpy | mt8-repro-native | ifx-natural | 4.033 | 4.26 | 1.056 |
| saxpy | mt8-repro-native | ifx-expert | 3.54 | 3.635 | 1.027 |
| stencil | serial-repro-native | blade-llvm | 6.643 | 4.772 | 0.718 |
| stencil | serial-repro-native | blade-cpp | 6.988 | 5.394 | 0.772 |
| stencil | serial-repro-native | gfortran-natural | 6.775 | 6.756 | 0.997 |
| stencil | serial-repro-native | gfortran-expert | 6.514 | 6.406 | 0.983 |
| stencil | serial-repro-native | ifx-natural | 11.55 | 11.52 | 0.998 |
| stencil | serial-repro-native | ifx-expert | 6.785 | 6.854 | 1.010 |
| stencil | mt8-repro-native | blade-cpp | 4.217 | 3.985 | 0.945 |
| stencil | mt8-repro-native | gfortran-natural | 4.56 | 4.447 | 0.975 |
| stencil | mt8-repro-native | gfortran-expert | 3.658 | 3.858 | 1.055 |
| stencil | mt8-repro-native | ifx-natural | 5.131 | 5.741 | 1.119 |
| stencil | mt8-repro-native | ifx-expert | 3.77 | 3.986 | 1.057 |
| stencil | serial-licensed-native | blade-llvm | 4.6 | 4.395 | 0.955 |
| stencil | serial-licensed-native | blade-cpp | 4.935 | 5.222 | 1.058 |
| stencil | serial-licensed-native | gfortran-natural | 6.803 | 6.801 | 1.000 |
| stencil | serial-licensed-native | gfortran-expert | 5.009 | 4.758 | 0.950 |
| stencil | serial-licensed-native | ifx-natural | 11.89 | 11.79 | 0.992 |
| stencil | serial-licensed-native | ifx-expert | 5.24 | 5.26 | 1.004 |
| stencil | mt8-licensed-native | blade-cpp | 4.371 | 4.176 | 0.955 |
| stencil | mt8-licensed-native | gfortran-natural | 4.598 | 4.427 | 0.963 |
| stencil | mt8-licensed-native | gfortran-expert | 3.32 | 3.574 | 1.076 |
| stencil | mt8-licensed-native | ifx-natural | 5.239 | 5.437 | 1.038 |
| stencil | mt8-licensed-native | ifx-expert | 3.4 | 3.891 | 1.144 |
| maskagg | serial-licensed-native | blade-llvm | 2.948 | 2.017 | 0.684 |
| maskagg | serial-licensed-native | blade-cpp | 3.198 | 2.229 | 0.697 |
| maskagg | serial-licensed-native | gfortran-natural | 5.01 | 5.039 | 1.006 |
| maskagg | serial-licensed-native | gfortran-expert | 1.111 | 1.043 | 0.939 |
| maskagg | serial-licensed-native | ifx-natural | 8.336 | 8.203 | 0.984 |
| maskagg | serial-licensed-native | ifx-expert | 1.167 | 1.212 | 1.038 |
| maskagg | serial-repro-native | blade-llvm | 3.173 | 2.338 | 0.737 |
| maskagg | serial-repro-native | blade-cpp | 3.436 | 2.506 | 0.729 |
| maskagg | serial-repro-native | gfortran-natural | 4.901 | 4.965 | 1.013 |
| maskagg | serial-repro-native | gfortran-expert | 1.485 | 1.488 | 1.003 |
| maskagg | serial-repro-native | ifx-natural | 8.283 | 8.306 | 1.003 |
| maskagg | serial-repro-native | ifx-expert | 1.512 | 1.498 | 0.991 |
| dot | serial-licensed-native | blade-llvm | 1.325 | 0.1085 | 0.082 |
| dot | serial-licensed-native | blade-cpp | 1.505 | 0.0732 | 0.049 |
| dot | serial-licensed-native | gfortran-natural | 2.469 | 2.431 | 0.985 |
| dot | serial-licensed-native | gfortran-expert | 1.376 | 1.429 | 1.038 |
| dot | serial-licensed-native | ifx-natural | 2.293 | 2.565 | 1.119 |
| dot | serial-licensed-native | ifx-expert | 1.449 | 1.307 | 0.902 |
| dot | serial-repro-native | blade-llvm | 2.682 | 0.0783 | 0.029 |
| dot | serial-repro-native | blade-cpp | 2.392 | -0.0589 | -0.025 |
| dot | serial-repro-native | gfortran-natural | 2.434 | 2.378 | 0.977 |
| dot | serial-repro-native | gfortran-expert | 2.366 | 2.353 | 0.995 |
| dot | serial-repro-native | ifx-natural | 2.236 | 2.424 | 1.084 |
| dot | serial-repro-native | ifx-expert | 0.6934 | 0.387 | 0.558 |
| dot | serial-fastmath-native | blade-llvm | 1.544 | 0.1196 | 0.077 |
| dot | serial-fastmath-native | blade-cpp | 1.354 | 0.0567 | 0.042 |
| dot | serial-fastmath-native | gfortran-natural | 1.442 | 1.325 | 0.919 |
| dot | serial-fastmath-native | gfortran-expert | 1.301 | 1.373 | 1.055 |
| dot | serial-fastmath-native | ifx-natural | 3.414 | 3.571 | 1.046 |
| dot | serial-fastmath-native | ifx-expert | 0.0643 | -0.0755 | -1.174 |
| dot | mt8-repro-native | blade-cpp | 2.342 | 0.4204 | 0.180 |
| dot | mt8-repro-native | gfortran-natural | 2.209 | 2.326 | 1.053 |
| dot | mt8-repro-native | gfortran-expert | 2.077 | 2.925 | 1.408 |
| dot | mt8-repro-native | ifx-natural | 1.895 | 2.374 | 1.253 |
| dot | mt8-repro-native | ifx-expert | 3.017 | 2.151 | 0.713 |
| onepass | serial-licensed-native | blade-llvm | 1.963 | 0.6648 | 0.339 |
| onepass | serial-licensed-native | blade-cpp | 1.521 | 0.4417 | 0.290 |
| onepass | serial-licensed-native | gfortran-natural | 15.84 | 15.85 | 1.001 |
| onepass | serial-licensed-native | gfortran-expert | 1.686 | 1.816 | 1.077 |
| onepass | serial-licensed-native | ifx-natural | 1.445 | 1.23 | 0.851 |
| onepass | serial-licensed-native | ifx-expert | 1.9 | 1.821 | 0.958 |
| onepass | serial-repro-native | blade-llvm | 4.255 | 0.9373 | 0.220 |
| onepass | serial-repro-native | blade-cpp | 4.372 | 0.5194 | 0.119 |
| onepass | serial-repro-native | gfortran-natural | 15.84 | 15.84 | 1.000 |
| onepass | serial-repro-native | gfortran-expert | 4.312 | 4.316 | 1.001 |
| onepass | serial-repro-native | ifx-natural | 1.235 | 1.348 | 1.091 |
| onepass | serial-repro-native | ifx-expert | 1.249 | 1.343 | 1.075 |
| onepass | serial-fastmath-native | blade-llvm | 1.735 | 0.4856 | 0.280 |
| onepass | serial-fastmath-native | blade-cpp | 1.469 | 0.393 | 0.267 |
| onepass | serial-fastmath-native | gfortran-natural | 5.554 | 5.652 | 1.018 |
| onepass | serial-fastmath-native | gfortran-expert | 1.988 | 2.029 | 1.021 |
| onepass | serial-fastmath-native | ifx-natural | 0.7487 | 0.7369 | 0.984 |
| onepass | serial-fastmath-native | ifx-expert | 1.021 | 1.014 | 0.993 |
| onepass | mt8-repro-native | gfortran-natural | 6.432 | 6.413 | 0.997 |
| onepass | mt8-repro-native | gfortran-expert | 2.541 | 2.256 | 0.888 |
| onepass | mt8-repro-native | ifx-natural | 6.232 | 6.584 | 1.056 |
| onepass | mt8-repro-native | ifx-expert | 2.672 | 2.54 | 0.951 |
| fusedchain | serial-licensed-native | blade-llvm | 0.652 | 0.3597 | 0.552 |
| fusedchain | serial-licensed-native | blade-cpp | 0.4641 | 0.5168 | 1.114 |
| fusedchain | serial-licensed-native | gfortran-natural | 10.44 | 10.55 | 1.011 |
| fusedchain | serial-licensed-native | gfortran-expert | 0.7789 | 0.6801 | 0.873 |
| fusedchain | serial-licensed-native | ifx-natural | 8.671 | 8.86 | 1.022 |
| fusedchain | serial-licensed-native | ifx-expert | 0.7992 | 0.7857 | 0.983 |
| fusedchain | serial-repro-native | blade-llvm | 1.598 | -0.0025 | -0.002 |
| fusedchain | serial-repro-native | blade-cpp | 1.751 | 0.274 | 0.157 |
| fusedchain | serial-repro-native | gfortran-natural | 10.49 | 10.59 | 1.009 |
| fusedchain | serial-repro-native | gfortran-expert | 1.641 | 1.736 | 1.058 |
| fusedchain | serial-repro-native | ifx-natural | 8.588 | 8.846 | 1.030 |
| fusedchain | serial-repro-native | ifx-expert | 0.4954 | 0.5253 | 1.060 |
| fusedchain | serial-fastmath-native | blade-llvm | 0.8018 | 0.3681 | 0.459 |
| fusedchain | serial-fastmath-native | blade-cpp | 0.8076 | 0.4846 | 0.600 |
| fusedchain | serial-fastmath-native | gfortran-natural | 9.413 | 9.401 | 0.999 |
| fusedchain | serial-fastmath-native | gfortran-expert | 0.5712 | 0.5383 | 0.942 |
| fusedchain | serial-fastmath-native | ifx-natural | 6.961 | 6.716 | 0.965 |
| fusedchain | serial-fastmath-native | ifx-expert | 0.8776 | 0.8262 | 0.941 |
| fusedchain | mt8-repro-native | blade-cpp | 1.589 | 0.0671 | 0.042 |
| fusedchain | mt8-repro-native | gfortran-natural | 7.454 | 8.006 | 1.074 |
| fusedchain | mt8-repro-native | gfortran-expert | 0.5218 | 0.6764 | 1.296 |
| fusedchain | mt8-repro-native | ifx-natural | 7.469 | 7.545 | 1.010 |
| fusedchain | mt8-repro-native | ifx-expert | 0.5827 | 0.4881 | 0.838 |

## 3. Expert ratios (expert median / Blade ref median; > 1 = Blade faster); harness-printed ratio [harness CI] verdict. Historical = RESULTS 14.8 final table (gf / ifx, verdict there).

| kernel | regime | ref | OLD gf-expert | OLD ifx-expert | NEW gf-expert | NEW ifx-expert | 14.8 hist gf / ifx (verdict) | NEW block (OLD block) | NEW gf / ifx vs blade-cpp (serial) |
|---|---|---|---|---|---|---|---|---|---|
| saxpy | serial-licensed-native | blade-llvm | 1.255 [1.237, 1.296] win | 0.925 [0.899, 0.942] detectable-loss | 1.377 [1.352, 1.411] win | 0.972 [0.954, 1.013] equivalent | 1.244 / 0.903 (indicative) | live (old: live) | 0.974 / 0.688 |
| saxpy | mt8-licensed-native | blade-cpp | 0.824 [0.763, 0.843] suppressed | 0.840 [0.777, 0.878] suppressed | 0.938 [0.866, 0.977] suppressed | 0.970 [0.900, 1.003] suppressed | 0.821 / 0.858 (indicative) | indicative (old: indicative) | (ref is blade-cpp) |
| saxpy | serial-repro-native | blade-llvm | 1.062 [1.047, 1.079] detectable-win | 0.667 [0.661, 0.677] loss | 1.375 [1.348, 1.405] win | 0.878 [0.853, 0.901] loss | 1.082 / 0.686 (indicative) | live (old: live) | 0.969 / 0.619 |
| saxpy | mt8-repro-native | blade-cpp | 0.732 [0.707, 0.753] suppressed | 0.763 [0.742, 0.794] suppressed | 0.811 [0.789, 0.852] loss | 0.871 [0.824, 0.899] loss | 0.765 / 0.748 (indicative) | live (old: indicative) | (ref is blade-cpp) |
| stencil | serial-repro-native | blade-llvm | 1.229 [1.216, 1.240] win | 0.986 [0.974, 0.995] detectable-loss | 1.416 [1.399, 1.440] win | 1.134 [1.115, 1.149] win | 1.224 / 1.001 (indicative) | live (old: live) | 1.068 / 0.856 |
| stencil | mt8-repro-native | blade-cpp | 0.792 [0.759, 0.853] suppressed | 0.855 [0.801, 0.909] suppressed | 0.822 [0.770, 0.849] suppressed | 0.875 [0.834, 0.906] suppressed | 0.815 / 0.891 (indicative) | indicative (old: indicative) | (ref is blade-cpp) |
| stencil | serial-licensed-native | blade-llvm | 1.343 [1.291, 1.392] suppressed | 1.035 [0.989, 1.071] suppressed | 1.341 [1.298, 1.371] win | 1.049 [1.015, 1.069] detectable-win | 1.375 / 1.089 (indicative) | live (old: indicative) | 0.976 / 0.764 |
| stencil | mt8-licensed-native | blade-cpp | 0.872 [0.857, 0.932] suppressed | 0.924 [0.903, 0.968] suppressed | 0.903 [0.843, 0.961] suppressed | 0.970 [0.910, 1.026] suppressed | 0.885 / 0.926 (indicative) | indicative (old: indicative) | (ref is blade-cpp) |
| recur | serial-repro-native | blade-llvm | 0.993 [0.981, 1.012] equivalent | 1.016 [1.005, 1.033] detectable-win | 1.337 [1.312, 1.381] win | 1.369 [1.340, 1.415] win | 0.985 / 1.017 (indicative) | live (old: live) | 1.145 / 1.172 |
| recur | serial-licensed-native | blade-llvm | 1.116 [1.051, 1.131] win | 1.166 [1.084, 1.183] win | 1.075 [1.036, 1.113] detectable-win | 1.114 [1.080, 1.159] win | 1.114 / 1.125 (indicative) | live (old: live) | 0.919 / 0.951 |
| pairkernel | serial-licensed-native | blade-llvm | 1.119 [1.113, 1.124] win | 1.041 [1.035, 1.047] detectable-win | 1.164 [1.162, 1.166] win | 1.086 [1.081, 1.088] detectable-win | 1.122 / 1.047 (live) | live (old: live) | 1.156 / 1.078 |
| pairkernel | mt8-licensed-native | blade-cpp | 0.797 [0.774, 0.838] suppressed | 0.791 [0.757, 0.809] suppressed | 1.020 [0.936, 1.068] suppressed | 0.967 [0.923, 1.031] suppressed | 0.800 / 0.798 (indicative) | indicative (old: indicative) | (ref is blade-cpp) |
| pairkernel | serial-repro-native | blade-llvm | 0.915 [0.907, 0.926] detectable-loss | 0.861 [0.852, 0.869] loss | 0.932 [0.925, 0.937] detectable-loss | 0.879 [0.871, 0.883] loss | 0.903 / 0.840 (live) | live (old: live) | 0.920 / 0.868 |
| pairkernel | mt8-repro-native | blade-cpp | 0.383 [0.365, 0.392] suppressed | 0.372 [0.364, 0.376] suppressed | 0.687 [0.660, 0.721] loss | 0.678 [0.653, 0.697] loss | 0.370 / 0.359 (live) | live (old: indicative) | (ref is blade-cpp) |
| comoment3 | serial-repro-native | blade-llvm | 0.769 [0.747, 0.785] loss | 0.773 [0.753, 0.785] loss | 0.770 [0.753, 0.778] loss | 0.780 [0.763, 0.788] loss | 0.773 / 0.780 (live) | live (old: live) | 0.758 / 0.768 |
| comoment3 | mt8-repro-native | blade-cpp | 0.497 [0.478, 0.526] loss | 0.436 [0.419, 0.451] loss | 1.080 [1.042, 1.138] detectable-win | 0.950 [0.928, 0.994] detectable-loss | 0.478 / 0.427 (live) | live (old: live) | (ref is blade-cpp) |
| comoment3 | serial-licensed-native | blade-llvm | 1.300 [1.279, 1.320] win | 1.305 [1.286, 1.327] win | 1.303 [1.286, 1.324] win | 1.323 [1.294, 1.344] win | 1.325 / 1.338 (live) | live (old: live) | 1.280 / 1.299 |
| maskagg | serial-licensed-native | blade-llvm | 0.856 [0.832, 0.899] loss | 0.619 [0.580, 0.628] loss | 1.062 [1.045, 1.086] detectable-win | 0.788 [0.763, 0.863] loss | 0.871 / 0.628 (indicative) | live (old: live) | 0.785 / 0.582 |
| maskagg | serial-repro-native | blade-llvm | 0.893 [0.872, 0.898] loss | 0.688 [0.670, 0.696] loss | 1.062 [1.037, 1.070] detectable-win | 0.813 [0.796, 0.824] loss | 0.839 / 0.676 (indicative) | live (old: live) | 0.828 / 0.633 |
| dot | serial-licensed-native | blade-llvm | 1.455 [1.441, 1.516] win | 0.971 [0.951, 1.000] equivalent | 1.642 [1.632, 1.670] win | 1.090 [1.079, 1.105] detectable-win | 1.416 / 0.967 (indicative) | live (old: live) | 1.059 / 0.703 |
| dot | serial-repro-native | blade-llvm | 1.213 [1.202, 1.228] win | 0.739 [0.731, 0.751] loss | 1.696 [1.678, 1.715] win | 1.012 [0.989, 1.041] equivalent | 1.219 / 0.755 (live) | live (old: live) | 1.103 / 0.659 |
| dot | serial-fastmath-native | blade-llvm | 1.395 [1.377, 1.410] win | 0.671 [0.659, 0.705] loss | 1.631 [1.618, 1.679] win | 0.782 [0.765, 0.809] loss | 1.369 / 0.712 (live) | live (old: live) | 1.041 / 0.499 |
| dot | mt8-repro-native | blade-cpp | 0.814 [0.776, 0.842] suppressed | 0.859 [0.817, 0.889] suppressed | 1.163 [1.106, 1.191] win | 1.182 [1.146, 1.222] win | 0.832 / 0.842 (indicative) | live (old: indicative) | (ref is blade-cpp) |
| onepass | serial-licensed-native | blade-llvm | 1.449 [1.436, 1.501] win | 1.005 [0.983, 1.036] equivalent | 1.635 [1.622, 1.686] win | 1.119 [1.107, 1.145] win | 1.525 / 1.031 (live) | live (old: live) | 1.100 / 0.753 |
| onepass | serial-repro-native | blade-llvm | 1.348 [1.335, 1.355] win | 0.803 [0.793, 0.816] loss | 1.714 [1.703, 1.742] win | 1.044 [1.036, 1.060] detectable-win | 1.354 / 0.820 (live) | live (old: live) | 1.214 / 0.739 |
| onepass | serial-fastmath-native | blade-llvm | 1.477 [1.430, 1.501] win | 0.745 [0.717, 0.762] loss | 1.664 [1.648, 1.697] win | 0.820 [0.809, 0.834] loss | 1.502 / 0.723 (indicative) | live (old: live) | 1.103 / 0.544 |
| onepass | mt8-repro-native | blade-cpp | -- | -- | -- | -- | -- / -- (no Blade arm) | live (old: indicative) | (ref is blade-cpp) |
| fusedchain | serial-licensed-native | blade-llvm | 1.561 [1.526, 1.594] suppressed | 1.032 [1.011, 1.049] suppressed | 1.709 [1.654, 1.734] win | 1.110 [1.091, 1.188] win | 1.567 / 1.023 (indicative) | live (old: indicative) | 1.071 / 0.696 |
| fusedchain | serial-repro-native | blade-llvm | 1.393 [1.374, 1.420] win | 0.823 [0.816, 0.829] loss | 1.852 [1.792, 1.872] win | 1.093 [1.054, 1.108] detectable-win | 1.381 / 0.839 (indicative) | live (old: live) | 1.213 / 0.716 |
| fusedchain | serial-fastmath-native | blade-llvm | 1.479 [1.416, 1.535] suppressed | 0.808 [0.771, 0.833] suppressed | 1.688 [1.656, 1.736] suppressed | 0.941 [0.929, 0.957] suppressed | 1.496 / 0.788 (indicative) | indicative (old: indicative) | 1.041 / 0.580 |
| fusedchain | mt8-repro-native | blade-cpp | 0.731 [0.704, 0.750] suppressed | 0.827 [0.788, 0.864] suppressed | 1.017 [0.989, 1.035] suppressed | 1.128 [1.095, 1.153] suppressed | 0.753 / 0.825 (indicative) | indicative (old: indicative) | (ref is blade-cpp) |
| gram | mt8-licensed-native | blade-cpp | 1.284 [1.251, 1.310] win | 1.217 [1.185, 1.258] win | 1.267 [1.226, 1.312] win | 1.218 [1.172, 1.251] win | 1.393 / 1.332 (indicative) | live (old: live) | (ref is blade-cpp) |

## 4. Block-level controls and flags (control medians OLD / NEW ms; NEW/OLD in parens)

| kernel | regime | rounds OLD/NEW | OLD | NEW | compute control | memory control | OLD flags | NEW flags | excursions dropped OLD/NEW |
|---|---|---|---|---|---|---|---|---|---|
| saxpy | serial-licensed-native | 21/21 | live | live | 12.835 / 12.824 (0.999) | 5.294 / 5.300 (1.001) | - | - | 0/0 |
| saxpy | mt8-licensed-native | 21/21 | indicative | indicative | 12.907 / 12.824 (0.994) | 5.248 / 5.404 (1.030) | trend: blade-cpp-inputonly first-half vs second-half median moved 9.4% | trend: blade-cpp first-half vs second-half median moved 6.7% | 0/0 |
| saxpy | serial-repro-native | 21/21 | live | live | 12.913 / 12.851 (0.995) | 5.188 / 5.246 (1.011) | - | - | 0/0 |
| saxpy | mt8-repro-native | 21/21 | indicative | live | 12.905 / 12.861 (0.997) | 5.296 / 5.253 (0.992) | trend: gfortran-natural first-half vs second-half median moved 5.8%; trend: gfortran-expert-inputonly first-half vs second-half median moved 6.6%; trend: ifx-expert-inputonly first-half vs second-half median moved 5.9% | - | 0/0 |
| stencil | serial-repro-native | 21/21 | live | live | 12.868 / 12.900 (1.003) | 5.202 / 5.185 (0.997) | - | - | 0/0 |
| stencil | mt8-repro-native | 21/21 | indicative | indicative | 12.885 / 12.903 (1.001) | 5.319 / 5.194 (0.976) | trend: blade-cpp first-half vs second-half median moved 6.1%; trend: blade-cpp-inputonly first-half vs second-half median moved 6.9% | trend: gfortran-expert first-half vs second-half median moved 5.1% | 0/0 |
| stencil | serial-licensed-native | 21/21 | indicative | live | 12.933 / 12.827 (0.992) | 5.217 / 5.176 (0.992) | trend: blade-llvm first-half vs second-half median moved 5.2% | - | 0/0 |
| stencil | mt8-licensed-native | 21/21 | indicative | indicative | 12.876 / 12.907 (1.002) | 5.257 / 5.257 (1.000) | trend: gfortran-natural first-half vs second-half median moved 5.5%; trend: gfortran-natural-inputonly first-half vs second-half median moved 5.4% | trend: ifx-expert first-half vs second-half median moved 5.2% | 0/0 |
| recur | serial-repro-native | 21/21 | live | live | 12.896 / 12.837 (0.995) | 5.191 / 5.191 (1.000) | - | - | 0/0 |
| recur | serial-licensed-native | 21/21 | live | live | 12.720 / 12.884 (1.013) | 5.171 / 5.199 (1.005) | - | - | 0/0 |
| pairkernel | serial-licensed-native | 21/21 | live | live | 12.926 / 12.891 (0.997) | 5.240 / 5.221 (0.996) | - | - | 0/0 |
| pairkernel | mt8-licensed-native | 21/21 | indicative | indicative | 12.893 / 12.865 (0.998) | 5.205 / 5.246 (1.008) | trend: ifx-natural first-half vs second-half median moved 6.8% | trend: blade-cpp first-half vs second-half median moved 7.6%; trend: ifx-natural first-half vs second-half median moved 10.1%; trend: ifx-expert first-half vs second-half median moved 6.4% | 0/0 |
| pairkernel | serial-repro-native | 21/21 | live | live | 12.855 / 12.924 (1.005) | 5.196 / 5.413 (1.042) | - | - | 0/0 |
| pairkernel | mt8-repro-native | 21/21 | indicative | live | 12.823 / 12.929 (1.008) | 5.180 / 5.273 (1.018) | trend: gfortran-expert first-half vs second-half median moved 5.4% | - | 0/0 |
| comoment3 | serial-repro-native | 21/21 | live | live | 12.880 / 12.896 (1.001) | 5.286 / 5.312 (1.005) | - | - | 0/0 |
| comoment3 | mt8-repro-native | 21/21 | live | live | 12.835 / 12.956 (1.009) | 5.338 / 5.322 (0.997) | - | - | 0/0 |
| comoment3 | serial-licensed-native | 21/21 | live | live | 12.889 / 12.803 (0.993) | 5.304 / 5.228 (0.986) | - | - | 0/0 |
| maskagg | serial-licensed-native | 21/21 | live | live | 12.890 / 12.909 (1.001) | 5.231 / 5.226 (0.999) | - | - | 0/0 |
| maskagg | serial-repro-native | 21/21 | live | live | 12.915 / 12.912 (1.000) | 5.246 / 5.244 (1.000) | - | - | 0/0 |
| dot | serial-licensed-native | 21/21 | live | live | 12.914 / 12.886 (0.998) | 5.194 / 5.224 (1.006) | - | - | 0/0 |
| dot | serial-repro-native | 21/21 | live | live | 12.837 / 12.942 (1.008) | 5.228 / 5.205 (0.996) | - | - | 0/0 |
| dot | serial-fastmath-native | 21/21 | live | live | 12.926 / 12.837 (0.993) | 5.442 / 5.158 (0.948) | - | - | 0/0 |
| dot | mt8-repro-native | 21/21 | indicative | live | 12.990 / 12.861 (0.990) | 5.276 / 5.227 (0.991) | trend: gfortran-natural-inputonly first-half vs second-half median moved 5.8% | - | 0/0 |
| onepass | serial-licensed-native | 21/21 | live | live | 12.921 / 12.815 (0.992) | 5.165 / 5.249 (1.016) | - | - | 0/0 |
| onepass | serial-repro-native | 21/21 | live | live | 12.777 / 12.850 (1.006) | 5.146 / 5.242 (1.019) | - | - | 0/0 |
| onepass | serial-fastmath-native | 21/21 | live | live | 12.849 / 12.859 (1.001) | 5.209 / 5.228 (1.004) | - | - | 0/0 |
| onepass | mt8-repro-native | 21/21 | indicative | live | 12.878 / 12.924 (1.004) | 5.259 / 5.211 (0.991) | trend: gfortran-natural first-half vs second-half median moved 10.6%; trend: gfortran-expert first-half vs second-half median moved 8.4%; trend: ifx-natural first-half vs second-half median moved 8.3%; trend: gfortran-expert-inputonly first-half vs second-half median moved 6.9%; trend: ifx-natural-inputonly first-half vs second-half median moved 8.9%; trend: control memory moved 5.6% within the block | - | 0/0 |
| fusedchain | serial-licensed-native | 21/21 | indicative | live | 12.895 / 12.977 (1.006) | 5.282 / 5.261 (0.996) | trend: blade-cpp-inputonly first-half vs second-half median moved 5.8% | - | 0/0 |
| fusedchain | serial-repro-native | 21/21 | live | live | 12.897 / 12.741 (0.988) | 5.210 / 5.317 (1.021) | - | - | 0/0 |
| fusedchain | serial-fastmath-native | 21/21 | indicative | indicative | 12.855 / 12.880 (1.002) | 5.252 / 5.215 (0.993) | trend: blade-llvm-inputonly first-half vs second-half median moved 8.8% | trend: control memory moved 5.7% within the block | 0/0 |
| fusedchain | mt8-repro-native | 21/21 | indicative | indicative | 12.780 / 12.864 (1.007) | 5.281 / 5.196 (0.984) | trend: gfortran-natural first-half vs second-half median moved 7.8%; trend: blade-cpp-inputonly first-half vs second-half median moved 14.3%; trend: gfortran-natural-inputonly first-half vs second-half median moved 5.3% | trend: gfortran-expert-inputonly first-half vs second-half median moved 5.1% | 0/0 |
| gram | mt8-licensed-native | 21/21 | live | live | 12.741 / 13.023 (1.022) | 5.210 / 5.321 (1.021) | - | - | 0/0 |

## 5. NEW slower than OLD beyond the CI (lower bound of NEW/OLD CI > 1; non-twin arms)

| kernel | regime | arm | OLD ms | NEW ms | NEW/OLD [CI] | block class |
|---|---|---|---|---|---|---|
| saxpy | serial-repro-native | gfortran-expert | 15.15 | 15.38 | 1.015 [1.000, 1.040] | live |
| saxpy | serial-repro-native | ifx-natural | 12.98 | 13.24 | 1.020 [1.003, 1.037] | live |
| saxpy | serial-repro-native | ifx-expert | 9.516 | 9.822 | 1.032 [1.001, 1.057] | live |
| recur | serial-licensed-native | ifx-natural | 15.46 | 15.63 | 1.011 [1.002, 1.020] | live |
| onepass | serial-repro-native | ifx-expert | 12.59 | 12.87 | 1.023 [1.004, 1.040] | live |
| fusedchain | serial-licensed-native | gfortran-expert | 6.647 | 6.865 | 1.033 [1.001, 1.054] | ind |

## 6. Sessions (bundle paths)

| kernel | side | bundle | started | aborted | duration (s) | failures | flags |
|---|---|---|---|---|---|---|---|
| saxpy | OLD | C:\bt_r2ab\old\bench\results\2026-10-20-LAPTOP-350H6LDB\session-051328 | 2026-10-20T05:13:28 | None | 744 | none | saxpy/mt8-licensed-native: inference suppressed: trend: blade-cpp-inputonly first-half vs second-half median moved 9.4%; saxpy/mt8-repro-native: inference suppressed: trend: gfortran-natural first-half vs second-half median moved 5.8%; trend: gfortran-expert-inputonly first-half vs second-half median moved 6.6%; trend: ifx-expert-inputonly first-half vs second-half median moved 5.9% |
| saxpy | NEW | C:\bt_r2ab\new\bench\results\2026-10-20-LAPTOP-350H6LDB\session-052603 | 2026-10-20T05:26:03 | None | 745 | none | saxpy/mt8-licensed-native: inference suppressed: trend: blade-cpp first-half vs second-half median moved 6.7% |
| stencil | OLD | C:\bt_r2ab\old\bench\results\2026-10-20-LAPTOP-350H6LDB\session-053839 | 2026-10-20T05:38:39 | None | 743 | none | stencil/serial-licensed-native: inference suppressed: trend: blade-llvm first-half vs second-half median moved 5.2%; stencil/mt8-licensed-native: inference suppressed: trend: gfortran-natural first-half vs second-half median moved 5.5%; trend: gfortran-natural-inputonly first-half vs second-half median moved 5.4%; stencil/mt8-repro-native: inference suppressed: trend: blade-cpp first-half vs second-half median moved 6.1%; trend: blade-cpp-inputonly first-half vs second-half median moved 6.9% |
| stencil | NEW | C:\bt_r2ab\new\bench\results\2026-10-20-LAPTOP-350H6LDB\session-055112 | 2026-10-20T05:51:12 | None | 745 | none | stencil/mt8-licensed-native: inference suppressed: trend: ifx-expert first-half vs second-half median moved 5.2%; stencil/mt8-repro-native: inference suppressed: trend: gfortran-expert first-half vs second-half median moved 5.1% |
| recur | OLD | C:\bt_r2ab\old\bench\results\2026-10-20-LAPTOP-350H6LDB\session-060347 | 2026-10-20T06:03:47 | None | 227 | none | none |
| recur | NEW | C:\bt_r2ab\new\bench\results\2026-10-20-LAPTOP-350H6LDB\session-060744 | 2026-10-20T06:07:44 | None | 227 | none | none |
| pairkernel | OLD | C:\bt_r2ab\old\bench\results\2026-10-20-LAPTOP-350H6LDB\session-061142 | 2026-10-20T06:11:42 | None | 431 | none | pairkernel/mt8-licensed-native: inference suppressed: trend: ifx-natural first-half vs second-half median moved 6.8%; pairkernel/mt8-repro-native: inference suppressed: trend: gfortran-expert first-half vs second-half median moved 5.4% |
| pairkernel | NEW | C:\bt_r2ab\new\bench\results\2026-10-20-LAPTOP-350H6LDB\session-061904 | 2026-10-20T06:19:04 | None | 432 | none | pairkernel/mt8-licensed-native: inference suppressed: trend: blade-cpp first-half vs second-half median moved 7.6%; trend: ifx-natural first-half vs second-half median moved 10.1%; trend: ifx-expert first-half vs second-half median moved 6.4% |
| comoment3 | OLD | C:\bt_r2ab\old\bench\results\2026-10-20-LAPTOP-350H6LDB\session-062626 | 2026-10-20T06:26:26 | None | 390 | none | none |
| comoment3 | NEW | C:\bt_r2ab\new\bench\results\2026-10-20-LAPTOP-350H6LDB\session-063305 | 2026-10-20T06:33:05 | None | 390 | none | none |
| maskagg | OLD | C:\bt_r2ab\old\bench\results\2026-10-20-LAPTOP-350H6LDB\session-063946 | 2026-10-20T06:39:46 | None | 392 | none | none |
| maskagg | NEW | C:\bt_r2ab\new\bench\results\2026-10-20-LAPTOP-350H6LDB\session-064628 | 2026-10-20T06:46:28 | None | 392 | none | none |
| dot | OLD | C:\bt_r2ab\old\bench\results\2026-10-20-LAPTOP-350H6LDB\session-065311 | 2026-10-20T06:53:11 | None | 769 | none | dot/mt8-repro-native: inference suppressed: trend: gfortran-natural-inputonly first-half vs second-half median moved 5.8% |
| dot | NEW | C:\bt_r2ab\new\bench\results\2026-10-20-LAPTOP-350H6LDB\session-070610 | 2026-10-20T07:06:10 | None | 772 | none | none |
| onepass | OLD | C:\bt_r2ab\old\bench\results\2026-10-20-LAPTOP-350H6LDB\session-071912 | 2026-10-20T07:19:12 | None | 743 | none | onepass/mt8-repro-native: inference suppressed: trend: gfortran-natural first-half vs second-half median moved 10.6%; trend: gfortran-expert first-half vs second-half median moved 8.4%; trend: ifx-natural first-half vs second-half median moved 8.3%; trend: gfortran-expert-inputonly first-half vs second-half median moved 6.9%; trend: ifx-natural-inputonly first-half vs second-half median moved 8.9%; trend: control memory moved 5.6% within the block |
| onepass | NEW | C:\bt_r2ab\new\bench\results\2026-10-20-LAPTOP-350H6LDB\session-073146 | 2026-10-20T07:31:46 | None | 746 | none | none |
| fusedchain | OLD | C:\bt_r2ab\old\bench\results\2026-10-20-LAPTOP-350H6LDB\session-074422 | 2026-10-20T07:44:22 | None | 761 | none | fusedchain/serial-licensed-native: inference suppressed: trend: blade-cpp-inputonly first-half vs second-half median moved 5.8%; fusedchain/mt8-repro-native: inference suppressed: trend: gfortran-natural first-half vs second-half median moved 7.8%; trend: blade-cpp-inputonly first-half vs second-half median moved 14.3%; trend: gfortran-natural-inputonly first-half vs second-half median moved 5.3%; fusedchain/serial-fastmath-native: inference suppressed: trend: blade-llvm-inputonly first-half vs second-half median moved 8.8% |
| fusedchain | NEW | C:\bt_r2ab\new\bench\results\2026-10-20-LAPTOP-350H6LDB\session-075713 | 2026-10-20T07:57:13 | None | 763 | none | fusedchain/mt8-repro-native: inference suppressed: trend: gfortran-expert-inputonly first-half vs second-half median moved 5.1%; fusedchain/serial-fastmath-native: inference suppressed: trend: control memory moved 5.7% within the block |
| gram | OLD | C:\bt_r2ab\old\bench\results\2026-10-20-LAPTOP-350H6LDB\session-081007 | 2026-10-20T08:10:07 | None | 153 | none | none |
| gram | NEW | C:\bt_r2ab\new\bench\results\2026-10-20-LAPTOP-350H6LDB\session-081250 | 2026-10-20T08:12:50 | None | 153 | none | none |