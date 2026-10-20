# Round 2 drop experiments (directional, N interleaved rounds, no drift controls)

Binary: round2\old-bin-4136f0b5\Blade.exe (Blade.dll SHA256 462B9D92...19E1 == gates\OLD-hash.txt, verified).
Scratch: C:\bt_r2drop\ (exe\, res\*.json, build.log, build.ps1, drive.ps1, timer.py, analyze2.py).

## Commands / env per arm
- Build: `Blade.exe compile <src> -o C:\bt_r2drop\exe\<name>.exe`, PATH=ucrt64 first, BLADE_EXE_CACHE=0, BLADE_BLAS=off, OPENBLAS_DIR unset. Built ONCE per arm (32 exes, all compiled OK; the LLVM-lane stderr "[omp] requested but emitted serial" is a note, not a failure).
  - C++ lane: BLADE_LLVM unset. LLVM lane: BLADE_LLVM=1 BLADE_FP_CONTRACT=fast.
  - licensed: no knob. repro: BLADE_FP_REPRO=1 BLADE_FP_REASSOC=0.
  - Serial exes were built WITHOUT BLADE_OMP_THREADS=1 (config.json blade_knobs sets none), so the C++ exes contain omp pragmas and run single-threaded via OMP_NUM_THREADS=1.
  - gram sweep: BLADE_TILE_TUNE=syrk_g=G (G=2,4,6,8), C++ lane; `Blade.exe plan gram.blade` under each setting printed `fold form: syrk tile 6x8, KC=334, row groups of G` for all 8 builds (lic and repro), see build.log.
- Run env (config.json run_env): serial = OMP_NUM_THREADS=1 OPENBLAS_NUM_THREADS=1 OMP_DYNAMIC=FALSE; mt8 = OMP_NUM_THREADS=8 OPENBLAS_NUM_THREADS=8 OMP_DYNAMIC=FALSE KMP_AFFINITY=disabled (unpinned). BLADE_* knobs removed from the run env.
- Timing: `C:\Python314\python.exe timer.py <json {name:exe}> serial|mt8 <rounds> <out.json>`: time.perf_counter around subprocess.run, 1 untimed warmup per exe, then per round A,B (or G2,G4,G6,G8) in order, 0.5 s cooldown after every run. Serial: the python parent is pinned to logical CPU 2 via SetProcessAffinityMask(1<<2), children inherit it (the same CPU the harness uses). Rounds: 11 serial, 7 mt8. One timed process at a time; nothing else run during timing.
- "wall" = whole process wall (includes start-up, input generation, folds). "int" = the program's own `completed in` line; I kept those per exe as a multiset only (not round-aligned), over the 12 runs incl. warmup, so int min/max include the warmup run.
- Caveat: absolute times are small (12-56 ms wall; gram mt8 ~34-38 ms), so a ~1-2 ms spread is 5-10%.
## EXPERIMENT 1 (serial, process pinned to CPU 2; ms; directional, 11 interleaved rounds, no drift controls)

Columns: wall = perf_counter around subprocess.run (includes process start + input generation + fold); int = the program's own 'completed in' line (per-run values not round-aligned: I stored the multiset of outputs, so int is median/min/max over the 12 runs incl. warmup).

| kernel | lane | regime | wall med A | wall med B | A/B wall | wall A min-max | wall B min-max | int med A | int med B | A/B int | checksum A vs B |
|---|---|---|---|---|---|---|---|---|---|---|---|
| dot | cpp | lic | 51.7 | 51.0 | 1.014 | 48.6-53.9 | 47.1-52.2 | 35.4 | 33.7 | 1.049 | DIFFERS rel 2.00e-14 |
| dot | cpp | rep | 55.9 | 51.5 | 1.085 | 52.4-57.5 | 47.8-53.1 | 38.7 | 34.3 | 1.130 | IDENTICAL |
| dot | llvm | lic | 25.8 | 24.6 | 1.050 | 22.3-27.0 | 21.5-26.0 | 13.3 | 12.2 | 1.086 | DIFFERS rel 1.40e-15 |
| dot | llvm | rep | 27.1 | 22.2 | 1.222 | 26.3-28.2 | 21.5-22.8 | 16.9 | 12.4 | 1.363 | IDENTICAL |
| onepass | cpp | lic | 47.2 | 46.1 | 1.025 | 42.4-86.7 | 43.0-52.9 | 29.7 | 28.8 | 1.030 | IDENTICAL |
| onepass | cpp | rep | 50.1 | 47.8 | 1.048 | 47.5-53.1 | 44.2-48.9 | 34.5 | 30.6 | 1.127 | IDENTICAL |
| onepass | llvm | lic | 23.4 | 23.0 | 1.018 | 20.9-25.9 | 19.5-24.0 | 12.1 | 10.9 | 1.111 | IDENTICAL |
| onepass | llvm | rep | 28.4 | 22.4 | 1.271 | 25.7-29.8 | 21.0-25.5 | 16.4 | 12.6 | 1.303 | IDENTICAL |
| fusedchain | cpp | lic | 28.0 | 28.9 | 0.970 | 24.8-29.4 | 23.7-29.4 | 12.2 | 12.0 | 1.024 | IDENTICAL |
| fusedchain | cpp | rep | 30.1 | 29.0 | 1.037 | 26.4-30.7 | 24.6-30.7 | 13.8 | 12.5 | 1.103 | IDENTICAL |
| fusedchain | llvm | lic | 16.5 | 16.3 | 1.016 | 13.3-20.7 | 12.1-18.0 | 5.3 | 4.9 | 1.094 | DIFFERS rel 1.29e-14 |
| fusedchain | llvm | rep | 18.9 | 16.5 | 1.148 | 13.9-19.0 | 13.3-17.5 | 7.1 | 5.3 | 1.324 | IDENTICAL |

### printed checksums

- dot cpp lic: A ('checksum = 749475.148712748', 'input_checksum = 2998681.67744271') | B ('checksum = 749475.148712763', 'input_checksum = 2998681.67744271')
- dot cpp rep: A ('checksum = 749475.148712715', 'input_checksum = 2998681.67744271') | B ('checksum = 749475.148712715', 'input_checksum = 2998681.67744271')
- dot llvm lic: A ('checksum = 749475.148712753', 'input_checksum = 2998681.67744271') | B ('checksum = 749475.148712754', 'input_checksum = 2998681.67744271')
- dot llvm rep: A ('checksum = 749475.148712715', 'input_checksum = 2998681.67744271') | B ('checksum = 749475.148712715', 'input_checksum = 2998681.67744271')
- onepass cpp lic: A ('checksum = 112257923.888802', 'input_checksum = 4998570.40460638') | B ('checksum = 112257923.888802', 'input_checksum = 4998570.40460638')
- onepass cpp rep: A ('checksum = 112257923.888799', 'input_checksum = 4998570.40460638') | B ('checksum = 112257923.888799', 'input_checksum = 4998570.40460638')
- onepass llvm lic: A ('checksum = 112257923.888801', 'input_checksum = 4998570.40460638') | B ('checksum = 112257923.888801', 'input_checksum = 4998570.40460638')
- onepass llvm rep: A ('checksum = 112257923.888799', 'input_checksum = 4998570.40460638') | B ('checksum = 112257923.888799', 'input_checksum = 4998570.40460638')
- fusedchain cpp lic: A ('checksum = 4640.89121018634', 'input_checksum = 999516.086692066') | B ('checksum = 4640.89121018634', 'input_checksum = 999516.086692066')
- fusedchain cpp rep: A ('checksum = 4640.89121018634', 'input_checksum = 999516.086692066') | B ('checksum = 4640.89121018634', 'input_checksum = 999516.086692066')
- fusedchain llvm lic: A ('checksum = 4640.89121018633', 'input_checksum = 999516.086692066') | B ('checksum = 4640.89121018627', 'input_checksum = 999516.086692066')
- fusedchain llvm rep: A ('checksum = 4640.89121018634', 'input_checksum = 999516.086692066') | B ('checksum = 4640.89121018634', 'input_checksum = 999516.086692066')

### raw per-round wall ms (round order; A,B interleaved within each round)

- dot cpp lic A wall: [50.8, 52.4, 52.3, 52.5, 53.9, 51.7, 48.8, 50.2, 50.2, 48.6, 53.4]
- dot cpp lic B wall: [47.1, 51.9, 51.2, 52.2, 52.0, 51.0, 49.9, 47.8, 48.7, 51.0, 51.4]
- dot cpp rep A wall: [55.3, 56.2, 53.6, 52.4, 52.7, 54.6, 56.3, 56.2, 55.9, 56.0, 57.5]
- dot cpp rep B wall: [48.0, 52.4, 49.0, 49.6, 48.5, 52.7, 52.0, 51.5, 53.1, 52.7, 47.8]
- dot llvm lic A wall: [23.5, 22.9, 25.8, 25.9, 27.0, 25.5, 22.3, 26.5, 26.1, 26.6, 22.4]
- dot llvm lic B wall: [21.5, 24.8, 25.4, 26.0, 24.6, 24.0, 25.0, 23.6, 24.3, 21.6, 25.0]
- dot llvm rep A wall: [27.3, 26.7, 27.2, 27.1, 27.6, 27.1, 26.3, 26.4, 28.2, 27.5, 26.6]
- dot llvm rep B wall: [21.9, 21.9, 22.8, 22.2, 22.3, 21.9, 22.0, 22.5, 22.3, 22.6, 21.5]
- onepass cpp lic A wall: [47.4, 47.7, 47.1, 45.7, 86.7, 60.8, 42.4, 48.0, 43.6, 47.2, 45.5]
- onepass cpp lic B wall: [43.6, 45.3, 47.7, 46.5, 43.7, 52.9, 45.0, 46.9, 46.3, 43.0, 46.1]
- onepass cpp rep A wall: [51.5, 48.1, 48.2, 48.0, 49.1, 50.6, 53.1, 51.4, 51.8, 47.5, 50.1]
- onepass cpp rep B wall: [48.1, 47.2, 44.2, 44.8, 44.4, 48.5, 48.9, 47.8, 48.0, 48.3, 44.2]
- onepass llvm lic A wall: [22.0, 20.9, 21.7, 25.9, 25.9, 25.6, 20.9, 25.1, 23.1, 23.9, 23.4]
- onepass llvm lic B wall: [23.0, 23.8, 21.0, 23.9, 22.8, 23.4, 22.4, 23.5, 24.0, 21.3, 19.5]
- onepass llvm rep A wall: [28.9, 29.6, 29.8, 29.3, 29.2, 26.2, 28.4, 25.9, 25.8, 26.7, 25.7]
- onepass llvm rep B wall: [22.4, 25.5, 25.1, 21.0, 21.8, 25.5, 25.2, 22.0, 21.6, 21.8, 22.7]
- fusedchain cpp lic A wall: [25.2, 24.9, 29.4, 25.5, 29.1, 29.2, 27.8, 29.4, 28.0, 24.8, 29.3]
- fusedchain cpp lic B wall: [25.3, 24.5, 24.2, 28.9, 29.4, 29.0, 29.2, 29.3, 23.7, 29.3, 28.4]
- fusedchain cpp rep A wall: [30.4, 26.6, 30.6, 26.6, 28.0, 26.4, 30.7, 30.5, 29.8, 30.4, 30.1]
- fusedchain cpp rep B wall: [29.0, 30.3, 28.7, 24.6, 27.0, 29.6, 25.0, 28.5, 29.8, 30.7, 29.2]
- fusedchain llvm lic A wall: [13.3, 17.2, 17.1, 13.4, 13.6, 16.5, 17.0, 13.3, 16.3, 20.7, 16.6]
- fusedchain llvm lic B wall: [16.3, 16.8, 18.0, 17.1, 17.0, 12.1, 16.6, 13.3, 15.7, 14.8, 16.1]
- fusedchain llvm rep A wall: [19.0, 15.6, 19.0, 19.0, 18.9, 19.0, 18.9, 18.2, 15.0, 18.4, 13.9]
- fusedchain llvm rep B wall: [13.3, 14.0, 17.0, 17.5, 14.3, 17.3, 15.8, 13.8, 16.7, 16.5, 16.5]

## EXPERIMENT 2: gram mt8 syrk_g sweep (OMP_NUM_THREADS=8, OMP_DYNAMIC=FALSE, KMP_AFFINITY=disabled, unpinned; ms; directional, 7 interleaved rounds, no drift controls)

| regime | G | wall med | wall min | wall max | int med (all 8 runs) | int min-max | checksum |
|---|---|---|---|---|---|---|---|
| lic | G2 | 33.9 | 32.0 | 37.0 | 17.9 | 16.0-19.9 | stable |
| lic | G4 | 35.3 | 32.7 | 36.6 | 17.3 | 15.9-21.7 | stable |
| lic | G6 | 34.6 | 33.1 | 37.3 | 18.4 | 16.2-23.7 | stable |
| lic | G8 | 35.9 | 33.7 | 36.4 | 20.0 | 16.0-22.0 | stable |
|  | all G: IDENTICAL ('checksum = 160344246688.889', 'input_checksum = 800824.015624528') | | | | | | |
| rep | G2 | 36.0 | 33.1 | 46.1 | 19.8 | 18.5-31.5 | stable |
| rep | G4 | 37.9 | 36.1 | 46.9 | 20.3 | 19.5-25.9 | stable |
| rep | G6 | 36.2 | 35.1 | 39.0 | 20.9 | 18.0-23.8 | stable |
| rep | G8 | 36.3 | 35.1 | 43.1 | 20.8 | 19.1-28.5 | stable |
|  | all G: IDENTICAL ('checksum = 160344246688.889', 'input_checksum = 800824.015624528') | | | | | | |

### raw per-round wall ms

- lic G2 wall: [33.9, 32.6, 32.7, 34.1, 32.0, 37.0, 33.9]
- lic G4 wall: [32.8, 35.4, 36.6, 32.7, 33.4, 35.3, 35.3]
- lic G6 wall: [34.4, 33.1, 36.2, 34.9, 34.6, 37.3, 33.3]
- lic G8 wall: [36.4, 33.9, 36.4, 33.7, 36.2, 35.9, 33.7]
- rep G2 wall: [46.1, 33.5, 36.3, 35.3, 38.4, 33.1, 36.0]
- rep G4 wall: [46.9, 38.8, 38.0, 36.1, 37.9, 37.1, 36.1]
- rep G6 wall: [35.1, 36.2, 36.2, 39.0, 36.2, 36.3, 36.5]
- rep G8 wall: [36.3, 35.4, 35.1, 43.1, 36.6, 36.1, 37.9]

## Verdicts

### Experiment 1 (item C, joined sibling folds)
Rule: build if blade-llvm serial-repro dot shows joined clearly faster (outside spread) AND blade-cpp serial-licensed does not regress beyond its spread.
- blade-llvm serial-repro dot: A 27.1 ms vs B 22.2 ms wall, A/B = 1.222. Ranges A 26.3-28.2, B 21.5-22.8: DO NOT overlap in 11/11 rounds (B won every round; internal time 16.9 vs 12.4 ms, 1.36x). Checksum IDENTICAL. CONDITION 1 MET.
- blade-cpp serial-licensed dot: A 51.7 vs B 51.0 ms, A/B = 1.014, ranges 48.6-53.9 vs 47.1-52.2 overlap; joined is not slower (internal 35.4 vs 33.7). CONDITION 2 MET (no regression). Note: the licensed C++ checksum differs in the last digits between A and B (749475.148712748 vs ...763, rel 2.0e-14, well inside the 1e-12 licensed tolerance) because the fold association differs (expected under licensed folds). Repro checksums are bit-identical A vs B for dot, onepass, fusedchain in both lanes.
- Same rule across the other cells: blade-llvm repro onepass 1.271 and fusedchain 1.148 (disjoint-ish spread for onepass 25.7-29.8 vs 21.0-25.5 only partly: medians clearly apart; fusedchain 13.9-19.0 vs 13.3-17.5 overlaps heavily, so directional only); blade-cpp repro gains smaller: dot 1.085 (ranges 52.4-57.5 vs 47.8-53.1, just disjoint), onepass 1.048, fusedchain 1.037 (inside spread). Licensed cells (both lanes) all within +-5% (1.014-1.050, fusedchain cpp 0.970) = inside spread, no regression.
- Verdict: DROP TEST PASSES, the rule is worth building (its payoff is concentrated in the repro lane, esp. blade-llvm; licensed is flat). Output equivalence: drop-in in repro everywhere; in licensed, dot (both lanes) and fusedchain llvm checksums differ at 1e-15..1e-14 relative (reassociation), onepass and fusedchain-cpp identical. The brief's "printed values must be IDENTICAL" therefore FAILS strictly for 3 of 6 licensed cells (dot cpp, dot llvm, fusedchain llvm), at the licensed rounding level; flagging loudly as asked.

### Experiment 2 (item D1-0, mt8 syrk_g sweep on gram)
- licensed wall medians G2/G4/G6/G8 = 33.9/35.3/34.6/35.9 ms (ranges 32.0-37.0, 32.7-36.6, 33.1-37.3, 33.7-36.4); repro = 36.0/37.9/36.2/36.3 ms (ranges 33.1-46.1, 36.1-46.9, 35.1-39.0, 35.1-43.1). Internal times show the same flatness (lic 17.3-20.0, rep 19.8-20.9 ms medians).
- No G is outside the spread of G4 (today's default); at most G2 is ~4% under G4 in licensed and ~5% in repro, with fully overlapping ranges, and the repro first-round outliers (46 ms) inflate max. Checksums are identical across all G in both regimes (checksum = 160344246688.889, input_checksum = 800824.015624528).
- Verdict: NULL RESULT. The syrk_g sweep gives no evidence that changing the default G=4 helps at this size on this box under mt8; if D1-0 needs a decision, it is "keep 4" unless a longer / drift-controlled run says otherwise. (At p=1601, n=1001 the whole run is ~17-20 ms internal, close to thread fork/join cost, so the instrument is weak.)

## Anything that went wrong
- First draft of timer.py had literal `\n` from a string replace; fixed before any timing. First analysis flagged every pair MISMATCH only because the programs print a `completed in <t>s` line; analyze2.py compares only checksum lines.
- Harness-protocol deviations: no drift controls, no cooldown between regimes, python-parent affinity pin instead of per-process pin, mt8 unpinned as in config.
- Stray earlier background sleep command (bjxst7r2a) ran while building but before any timing.
