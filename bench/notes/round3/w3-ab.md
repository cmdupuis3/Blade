# Round 3, waves 3 + 4: the harness A/B (one combined chain after wave 4)

Chain `gates/w3-scripts`-equivalent: `wave3-end.ps1` (build, five gates from `C:\bt_r2w1`, examples sweep, mirrors `-Recreate`,
smoke, A/B driver, `analyze-w3.py`), run from the integration tip f46fdfc0 (the W4-3 merge) on 2026-10-24 from 00:20. The
measured tip carries waves 3 AND 4 (the wave-4 items are correctness / lifetime / provenance changes with no benched cell of their
own; their emission changes are stated per item in 17.7). Honesty rules as in `w1-ab.md` / `w2-ab.md`: MEASURED names the
command; a block the harness flagged is `indicative`; a Blade arm slower than its OLD binary beyond its CI is said so.

## Mirrors (every mirror = one binary + one knob set; `mirrors-w3.ps1 -Recreate`)

| mirror | binary | knobs | role |
|---|---|---|---|
| r3w1 | wave-1 tip 9da403a8 | defaults | the saxpy re-measure's OLD (the legs' pre-W2-4 baseline) |
| r3w2bin | wave-2 chain binary (3a3925ac) | defaults | OLD for every other cell |
| r3w3 | f46fdfc0 | defaults | NEW |
| r3w3t3off | f46fdfc0 | `BLADE_GROUP_AGG=0` | T3 knob-off (the previous grouping form) |
| r3w3t3lic | f46fdfc0 | `BLADE_GROUP_AGG=licensed` | T3's EXPERIMENTAL licensed one pass (opt-in; the default declines it in a licensed build) |
| r3w3t2off | f46fdfc0 | `BLADE_HASH_JOIN=0` | T2 knob-off (the six-pass join) |
| r3w3strict | f46fdfc0 | `BLADE_ORBIT_ACTIONS=strict` | P2 row actions in strict builds only |
| r3w3legs1 | f46fdfc0 | `BLADE_EPILOGUE_LEGS=1` | W2-4's OPT-IN kept inline with producer legs (default = the joined pass, BL7005 warns when retained) |

## Cells (driver-w3.ps1; `run.py bench --kernels K --regimes R` per mirror, interleaved by mirror within a session)

groupagg and groupagg_dyn (serial repro: r3w2bin / r3w3 / r3w3t3off; serial licensed: r3w2bin / r3w3 / r3w3t3lic), hashjoin (serial
licensed + repro: r3w2bin / r3w3 / r3w3t2off), pairaction (four regimes: r3w2bin / r3w3 / r3w3strict), pairkernel mt8 repro
(r3w2bin / r3w3, the P1 baseline), saxpy (four regimes: r3w1 / r3w3 / r3w3legs1), controls comoment3 mt8 repro and gram serial
licensed (r3w2bin / r3w3).

## Headline (Blade arms; NEW / OLD < 1 = NEW faster; analyst bootstrap CI on the ratio of pooled medians; class = the harness's block class, `live` = no flag, `ind` = a within-block trend flag suppressed the harness's own inference)

| cell | item | blade-llvm | blade-cpp | class | expert ratio moved (gfortran / ifx expert over the Blade reference; > 1 Blade faster) |
|---|---|---|---|---|---|
| groupagg serial repro, r3w3 / r3w2bin | W3-1 T3 one pass (default ON under REPRO) | **0.435** [0.426, 0.442] (15.23 -> 6.62 ms) | **0.471** [0.455, 0.485] (16.89 -> 7.96) | live / live | gf 0.574 -> **1.331 WIN**; ifx 0.385 -> 0.889 |
| groupagg serial repro, knob off / r3w3 | `BLADE_GROUP_AGG=0` = the previous grouping form | 2.338 | 2.183 | ind | -- |
| groupagg serial licensed, r3w3 / r3w2bin | the default DECLINES the licensed one pass (control) | 1.010 [0.980, 1.053] | 1.025 [0.955, 1.057] | ind | unchanged (0.57 / 0.33) |
| groupagg serial licensed, `BLADE_GROUP_AGG=licensed` / r3w3 | the EXPERIMENTAL licensed-float one pass, opt-in | **0.431** [0.412, 0.447] | **0.453** [0.443, 0.480] | ind | gf -> 1.278 WIN, ifx -> 0.745 |
| groupagg_dyn serial repro, r3w3 / r3w2bin | T3 dynamic keys (first-occurrence groups through the C core) | **0.050** [0.049, 0.050] (248.2 -> 12.3 ms: the old LLVM arm ran the slow dynamic path) | **0.491** [0.476, 0.503] (24.71 -> 12.13) | live / live | gf 0.077 -> **1.554 WIN**; ifx 0.076 -> **1.533 WIN** |
| groupagg_dyn serial licensed, r3w3 / r3w2bin | default (declines the one pass) | 0.126 (248.2 -> 31.2: the old slow path gone even where the one pass declines) | 0.989 [0.952, 1.007] | ind | -- |
| groupagg_dyn serial licensed, `=licensed` / r3w3 | experimental arm | 0.394 [0.383, 0.408] | 0.478 [0.473, 0.493] | ind | gf -> 1.536, ifx -> 1.474 (ind) |
| hashjoin serial licensed / repro, r3w3 / r3w2bin | W3-2 T2 stable CSR equijoin, PILOT size | the LLVM lane declines the rank-2 compound initializer in both binaries (C++ fallback) | **0.010** [0.010, 0.011] (8.456 -> 0.087 ms) / **0.009** [0.008, 0.010] (9.41 -> 0.085) | ind / ind (cpp) | gf 0.008 -> 0.778 (lic) / 0.804 (repro); ifx 0.009 -> 0.886 / 0.887 -- still a LOSS to the expert's 0.068 ms at pilot size |
| hashjoin, knob off / r3w3 | `BLADE_HASH_JOIN=0` = the six-pass form | -- | 100.1 [95.3, 116.2] / 100.4 [98.2, 107.6] | ind / live | -- |
| pairaction serial licensed, r3w3 / r3w2bin | W3-4 P2 row orbit actions | **0.589** [0.585, 0.596] (60.8 -> 35.9 ms) | **0.446** [0.435, 0.451] (78.3 -> 34.9) | ind 11/21 | gf 0.653 -> **1.110 WIN**; ifx 0.579 -> 0.951 (detectable loss) |
| pairaction serial repro, r3w3 / r3w2bin | P2 under REPRO | **0.551** [0.491, 0.564] (66.8 -> 36.8) | **0.519** [0.494, 0.533] (68.3 -> 35.4) | ind 11/21 | gf 0.559 -> 1.017; ifx 0.608 -> **1.113 WIN** |
| pairaction, `BLADE_ORBIT_ACTIONS=strict` / r3w3 | the walk in a licensed build (the knob keeps the action for strict builds only) | 1.680 (licensed) / 0.992 (repro: a repro build IS strict, same form) | 2.308 / 1.002 | ind | -- |
| pairaction mt8 licensed / repro, r3w3 / r3w2bin | the action declines under a thread team (the walk threads) | serial-only lane | 0.984 [0.960, 1.051] / 0.972 [0.944, 1.007] | ind | unchanged (0.58-0.61) |
| saxpy serial licensed, r3w3 / r3w1 | W2-4 re-measure: the DEFAULT (joined pass, no inline) vs the wave-1 binary | **1.046** [1.004, 1.180] (10.86 -> 11.36) | **1.079** [1.026, 1.163] (15.40 -> 16.61) | ind (Blade medians moved 10-15% within the block) | SLOWER than the OLD binary beyond the CI -- indicative, not live; see the decisions |
| saxpy serial licensed, `BLADE_EPILOGUE_LEGS=1` / r3w1 | the OPT-IN kept inline with producer legs | 0.933 [0.913, 1.017] (10.86 -> 10.13) | 0.993 [0.968, 1.089] (15.40 -> 15.30) | ind | gf 1.357 -> 1.501 |
| saxpy serial repro, r3w3 / r3w1 | default | 0.976 [0.938, 1.073] | 0.996 [0.956, 1.038] | ind | -- |
| saxpy serial repro, legs1 / r3w1 | opt-in legs | **0.869** [0.831, 0.930] (11.89 -> 10.33) | **0.863** [0.835, 0.881] (16.80 -> 14.50) | live / live | gf 1.367 -> **1.488 WIN**; ifx 0.872 -> 0.952 |
| saxpy mt8 (both FP), every mirror | the legs decline under a thread team | serial-only lane | 0.967-1.013 | live / ind | unchanged |
| pairkernel mt8 repro, r3w3 / r3w2bin | control (P1 baseline) | serial-only lane | 1.003 [0.969, 1.034] | ind | unchanged (0.78-0.80) |
| comoment3 mt8 repro, r3w3 / r3w2bin | control (the pull) | serial-only lane | 1.049 [0.971, 1.083] | ind | 1.585 -> 1.526 WIN kept |
| gram serial licensed, r3w3 / r3w2bin | control | 0.995 [0.975, 1.008] | 1.014 [0.988, 1.029] | ind / live | 1.36 / 1.24 WIN kept |

Peak working set (section 8): groupagg and groupagg_dyn 35.1 / 35.5 MB -> 19.8 / 20.6 MB under the one pass (no gathered pool;
the knob-off and licensed-declined arms keep 35 MB); hashjoin blade-cpp 25.4 -> 5.4 MB (one CSR allocation, the relation sized
by the counting pass; knob-off 25.4); every other cell unchanged to 0.1 MB. Section 5 (r3w3 slower than r3w2bin beyond the CI,
Blade arms) is EMPTY; the saxpy default's slower-than-OLD cells are against r3w1 (the deferred re-measure's baseline) and are
indicative, stated above.

## Decisions the measurement settles

1. **T3 (W3-1) stays ON by default** -- under REPRO the one pass is 0.44-0.49 of the old grouping in both lanes (live blocks) and
   turns groupagg's serial repro cell from a 0.57 loss into a 1.33 win over gfortran-expert; the dynamic-key kernel goes from a
   20x loss to a 1.5x win over both experts (the old LLVM arm's 248 ms was its slow dynamic path). The LICENSED one pass remains
   the opt-in `BLADE_GROUP_AGG=licensed` (0.43-0.45 of the declining default; the default declines because the licensed-float
   pass changes association -- a user's decision to promote, with the pins the W3-1 record names). Peak WS halves under the one pass.
2. **T2 (W3-2) at pilot size: the mechanism is proved, the size is not.** 100x over the six-pass form in the C++ lane, but the
   pilot's absolute times (0.085-0.087 ms) are below the harness's resolution for a win claim and still 0.78-0.89 of the
   experts' 0.068 ms; the LLVM lane declines the rank-2 compound initializer in both binaries (C++ fallback, as recorded in
   G72). A full-size equijoin kernel and the LLVM route are the follow-ups (G72 stays BUILT, PILOT SIZE ONLY).
3. **P2 (W3-4) stays ON by default** -- serial pairaction 0.45-0.59 of the old walk in both lanes and both FP modes; the
   action declines under a thread team (mt8 unchanged, 0.97-1.03). `strict` is the A/B arm, not a candidate default.
4. **W2-4 legs (G69): the default joined pass buys NOTHING over the wave-1 binary** -- serial licensed saxpy is 1.05 / 1.08 of
   r3w1 in the two lanes (indicative, beyond the CI; the block's Blade medians drifted 10-15% within the session, so this is a
   watched regression candidate, not a live one), serial repro 0.98 / 1.00. The OPT-IN kept inline (`BLADE_EPILOGUE_LEGS=1`)
   delivers the saxpy gain: 0.869 / 0.863 of r3w1 under REPRO (live) and 0.93 / 0.99 licensed (indicative), 0.87-0.92 of the
   default. The path to making it the default is the scope model A (real lifetime scopes, the G69 design note), a later item;
   until then the arm stays opt-in with BL7005.
5. **Controls hold**: pairkernel 1.003, gram 0.995 / 1.014, comoment3 1.049 [0.971, 1.083] (indicative; the pull's 1.53-1.59
   win over the experts kept). Nothing in section 5.

## Analyst tables (generated by `w3-scripts/analyze-w3.py` from the 27 session bundles under `C:\bt_r2ab\<mirror>\bench\results\2026-10-24-LAPTOP-350H6LDB`)

Mirrors: r3w1 = wave-1 tip 9da403a8 binary + wave-3 bench; r3w2bin = wave-2 chain binary + wave-3 bench (OLD); r3w3 = wave-3 tip, defaults (NEW); r3w3t3off = BLADE_GROUP_AGG=0 (T3 one-pass group aggregation off: the previous grouping form); r3w3t3lic = BLADE_GROUP_AGG=licensed (the EXPERIMENTAL licensed-float one pass, opt-in; the default declines it in a licensed build); r3w3t2off = BLADE_HASH_JOIN=0 (T2 stable CSR equijoin off: the six-pass form); r3w3strict = BLADE_ORBIT_ACTIONS=strict (P2 row actions in strict builds only); r3w3legs1 = BLADE_EPILOGUE_LEGS=1 (W2-4 deferred saxpy re-measure: the OPT-IN kept inline with producer legs; default = joined pass)

## 1. Pairwise medians: B/A < 1 = B faster (samples pooled across sessions of a mirror). CI = analyst unpaired bootstrap.

| kernel | regime | item | B/A | arm | A -> B median (ms) | B/A [95% CI] | class n_A/n_B |
|---|---|---|---|---|---|---|---|
| groupagg | serial-repro-native | W3-1 T3 one pass under REPRO (knob-off = today) | r3w3/r3w2bin | blade-llvm | 15.23 -> 6.619 | 0.435 [0.426, 0.442] | live 21/21 |
| groupagg | serial-repro-native | W3-1 T3 one pass under REPRO (knob-off = today) | r3w3/r3w2bin | blade-cpp | 16.89 -> 7.958 | 0.471 [0.455, 0.485] | live 21/21 |
| groupagg | serial-repro-native | W3-1 T3 one pass under REPRO (knob-off = today) | r3w3/r3w2bin | gfortran-natural | 1249 -> 1265 | 1.013 [0.999, 1.040] | live 21/21 |
| groupagg | serial-repro-native | W3-1 T3 one pass under REPRO (knob-off = today) | r3w3/r3w2bin | gfortran-expert | 8.733 -> 8.808 | 1.009 [0.991, 1.024] | live 21/21 |
| groupagg | serial-repro-native | W3-1 T3 one pass under REPRO (knob-off = today) | r3w3/r3w2bin | ifx-natural | 474.6 -> 491.1 | 1.035 [1.014, 1.068] | live 21/21 |
| groupagg | serial-repro-native | W3-1 T3 one pass under REPRO (knob-off = today) | r3w3/r3w2bin | ifx-expert | 5.867 -> 5.886 | 1.003 [0.979, 1.032] | live 21/21 |
| groupagg | serial-repro-native | W3-1 T3 one pass under REPRO (knob-off = today) | r3w3t3off/r3w3 | blade-llvm | 6.619 -> 15.47 | 2.338 [2.276, 2.411] | ind 21/21 |
| groupagg | serial-repro-native | W3-1 T3 one pass under REPRO (knob-off = today) | r3w3t3off/r3w3 | blade-cpp | 7.958 -> 17.37 | 2.183 [2.107, 2.319] | ind 21/21 |
| groupagg | serial-repro-native | W3-1 T3 one pass under REPRO (knob-off = today) | r3w3t3off/r3w3 | gfortran-natural | 1265 -> 1265 | 1.000 [0.991, 1.046] | ind 21/21 |
| groupagg | serial-repro-native | W3-1 T3 one pass under REPRO (knob-off = today) | r3w3t3off/r3w3 | gfortran-expert | 8.808 -> 8.682 | 0.986 [0.973, 1.002] | ind 21/21 |
| groupagg | serial-repro-native | W3-1 T3 one pass under REPRO (knob-off = today) | r3w3t3off/r3w3 | ifx-natural | 491.1 -> 490.4 | 0.999 [0.965, 1.077] | ind 21/21 |
| groupagg | serial-repro-native | W3-1 T3 one pass under REPRO (knob-off = today) | r3w3t3off/r3w3 | ifx-expert | 5.886 -> 6.058 | 1.029 [0.980, 1.052] | ind 21/21 |
| groupagg | serial-licensed-native | W3-1 licensed: default declines (control) vs the experimental arm | r3w3/r3w2bin | blade-llvm | 14.58 -> 14.72 | 1.010 [0.980, 1.053] | ind 21/21 |
| groupagg | serial-licensed-native | W3-1 licensed: default declines (control) vs the experimental arm | r3w3/r3w2bin | blade-cpp | 16.68 -> 17.1 | 1.025 [0.955, 1.057] | ind 21/21 |
| groupagg | serial-licensed-native | W3-1 licensed: default declines (control) vs the experimental arm | r3w3/r3w2bin | gfortran-natural | 1305 -> 1278 | 0.979 [0.963, 1.043] | ind 21/21 |
| groupagg | serial-licensed-native | W3-1 licensed: default declines (control) vs the experimental arm | r3w3/r3w2bin | gfortran-expert | 8.313 -> 8.397 | 1.010 [0.986, 1.047] | ind 21/21 |
| groupagg | serial-licensed-native | W3-1 licensed: default declines (control) vs the experimental arm | r3w3/r3w2bin | ifx-natural | 491.8 -> 505.3 | 1.028 [0.963, 1.047] | ind 21/21 |
| groupagg | serial-licensed-native | W3-1 licensed: default declines (control) vs the experimental arm | r3w3/r3w2bin | ifx-expert | 4.836 -> 4.954 | 1.024 [0.980, 1.078] | ind 21/21 |
| groupagg | serial-licensed-native | W3-1 licensed: default declines (control) vs the experimental arm | r3w3t3lic/r3w3 | blade-llvm | 14.72 -> 6.341 | 0.431 [0.412, 0.447] | ind 21/21 |
| groupagg | serial-licensed-native | W3-1 licensed: default declines (control) vs the experimental arm | r3w3t3lic/r3w3 | blade-cpp | 17.1 -> 7.744 | 0.453 [0.443, 0.480] | ind 21/21 |
| groupagg | serial-licensed-native | W3-1 licensed: default declines (control) vs the experimental arm | r3w3t3lic/r3w3 | gfortran-natural | 1278 -> 1255 | 0.982 [0.945, 0.993] | ind 21/21 |
| groupagg | serial-licensed-native | W3-1 licensed: default declines (control) vs the experimental arm | r3w3t3lic/r3w3 | gfortran-expert | 8.397 -> 8.103 | 0.965 [0.943, 0.990] | ind 21/21 |
| groupagg | serial-licensed-native | W3-1 licensed: default declines (control) vs the experimental arm | r3w3t3lic/r3w3 | ifx-natural | 505.3 -> 480.2 | 0.950 [0.939, 0.983] | ind 21/21 |
| groupagg | serial-licensed-native | W3-1 licensed: default declines (control) vs the experimental arm | r3w3t3lic/r3w3 | ifx-expert | 4.954 -> 4.722 | 0.953 [0.906, 0.994] | ind 21/21 |
| groupagg_dyn | serial-repro-native | W3-1 dynamic keys under REPRO | r3w3/r3w2bin | blade-llvm | 248.2 -> 12.3 | 0.050 [0.049, 0.050] | live 11/21 |
| groupagg_dyn | serial-repro-native | W3-1 dynamic keys under REPRO | r3w3/r3w2bin | blade-cpp | 24.71 -> 12.13 | 0.491 [0.476, 0.503] | live 11/21 |
| groupagg_dyn | serial-repro-native | W3-1 dynamic keys under REPRO | r3w3/r3w2bin | gfortran-natural | 1299 -> 1322 | 1.018 [1.005, 1.035] | live 11/21 |
| groupagg_dyn | serial-repro-native | W3-1 dynamic keys under REPRO | r3w3/r3w2bin | gfortran-expert | 19.08 -> 19.1 | 1.001 [0.980, 1.009] | live 11/21 |
| groupagg_dyn | serial-repro-native | W3-1 dynamic keys under REPRO | r3w3/r3w2bin | ifx-natural | 563.6 -> 574.1 | 1.019 [0.985, 1.052] | live 11/21 |
| groupagg_dyn | serial-repro-native | W3-1 dynamic keys under REPRO | r3w3/r3w2bin | ifx-expert | 18.8 -> 18.85 | 1.003 [0.994, 1.009] | live 11/21 |
| groupagg_dyn | serial-repro-native | W3-1 dynamic keys under REPRO | r3w3t3off/r3w3 | blade-llvm | 12.3 -> 32.35 | 2.631 [2.599, 2.678] | ind 21/21 |
| groupagg_dyn | serial-repro-native | W3-1 dynamic keys under REPRO | r3w3t3off/r3w3 | blade-cpp | 12.13 -> 25.96 | 2.141 [2.090, 2.180] | ind 21/21 |
| groupagg_dyn | serial-repro-native | W3-1 dynamic keys under REPRO | r3w3t3off/r3w3 | gfortran-natural | 1322 -> 1403 | 1.061 [1.019, 1.086] | ind 21/21 |
| groupagg_dyn | serial-repro-native | W3-1 dynamic keys under REPRO | r3w3t3off/r3w3 | gfortran-expert | 19.1 -> 19.06 | 0.998 [0.990, 1.008] | ind 21/21 |
| groupagg_dyn | serial-repro-native | W3-1 dynamic keys under REPRO | r3w3t3off/r3w3 | ifx-natural | 574.1 -> 624.2 | 1.087 [0.996, 1.172] | ind 21/21 |
| groupagg_dyn | serial-repro-native | W3-1 dynamic keys under REPRO | r3w3t3off/r3w3 | ifx-expert | 18.85 -> 18.88 | 1.002 [0.996, 1.010] | ind 21/21 |
| groupagg_dyn | serial-licensed-native | W3-1 dynamic, licensed control + experimental arm | r3w3/r3w2bin | blade-llvm | 248.2 -> 31.18 | 0.126 [0.123, 0.129] | ind 11/21 |
| groupagg_dyn | serial-licensed-native | W3-1 dynamic, licensed control + experimental arm | r3w3/r3w2bin | blade-cpp | 25.15 -> 24.87 | 0.989 [0.952, 1.007] | ind 11/21 |
| groupagg_dyn | serial-licensed-native | W3-1 dynamic, licensed control + experimental arm | r3w3/r3w2bin | gfortran-natural | 1512 -> 1332 | 0.881 [0.847, 0.991] | ind 11/21 |
| groupagg_dyn | serial-licensed-native | W3-1 dynamic, licensed control + experimental arm | r3w3/r3w2bin | gfortran-expert | 18.94 -> 18.93 | 0.999 [0.990, 1.008] | ind 11/21 |
| groupagg_dyn | serial-licensed-native | W3-1 dynamic, licensed control + experimental arm | r3w3/r3w2bin | ifx-natural | 714.9 -> 582.1 | 0.814 [0.746, 0.971] | ind 11/21 |
| groupagg_dyn | serial-licensed-native | W3-1 dynamic, licensed control + experimental arm | r3w3/r3w2bin | ifx-expert | 18.16 -> 18.12 | 0.998 [0.983, 1.014] | ind 11/21 |
| groupagg_dyn | serial-licensed-native | W3-1 dynamic, licensed control + experimental arm | r3w3t3lic/r3w3 | blade-llvm | 31.18 -> 12.3 | 0.394 [0.383, 0.408] | ind 21/21 |
| groupagg_dyn | serial-licensed-native | W3-1 dynamic, licensed control + experimental arm | r3w3t3lic/r3w3 | blade-cpp | 24.87 -> 11.89 | 0.478 [0.473, 0.493] | ind 21/21 |
| groupagg_dyn | serial-licensed-native | W3-1 dynamic, licensed control + experimental arm | r3w3t3lic/r3w3 | gfortran-natural | 1332 -> 1316 | 0.988 [0.900, 1.013] | ind 21/21 |
| groupagg_dyn | serial-licensed-native | W3-1 dynamic, licensed control + experimental arm | r3w3t3lic/r3w3 | gfortran-expert | 18.93 -> 18.89 | 0.998 [0.990, 1.004] | ind 21/21 |
| groupagg_dyn | serial-licensed-native | W3-1 dynamic, licensed control + experimental arm | r3w3t3lic/r3w3 | ifx-natural | 582.1 -> 562.8 | 0.967 [0.861, 1.020] | ind 21/21 |
| groupagg_dyn | serial-licensed-native | W3-1 dynamic, licensed control + experimental arm | r3w3t3lic/r3w3 | ifx-expert | 18.12 -> 18.13 | 1.001 [0.991, 1.007] | ind 21/21 |
| hashjoin | serial-licensed-native | W3-2 T2 stable CSR, pilot size | r3w3/r3w2bin | blade-llvm | -- | -- | (arm not ok in a block) |
| hashjoin | serial-licensed-native | W3-2 T2 stable CSR, pilot size | r3w3/r3w2bin | blade-cpp | 8.456 -> 0.087 | 0.010 [0.010, 0.011] | ind 21/21 |
| hashjoin | serial-licensed-native | W3-2 T2 stable CSR, pilot size | r3w3/r3w2bin | gfortran-natural | 1.288 -> 1.293 | 1.004 [1.000, 1.010] | ind 21/21 |
| hashjoin | serial-licensed-native | W3-2 T2 stable CSR, pilot size | r3w3/r3w2bin | gfortran-expert | 0.0677 -> 0.0677 | 1.000 [0.996, 1.012] | ind 21/21 |
| hashjoin | serial-licensed-native | W3-2 T2 stable CSR, pilot size | r3w3/r3w2bin | ifx-natural | 0.6494 -> 0.6487 | 0.999 [0.990, 1.010] | ind 21/21 |
| hashjoin | serial-licensed-native | W3-2 T2 stable CSR, pilot size | r3w3/r3w2bin | ifx-expert | 0.0764 -> 0.0771 | 1.009 [0.959, 1.013] | ind 21/21 |
| hashjoin | serial-licensed-native | W3-2 T2 stable CSR, pilot size | r3w3t2off/r3w3 | blade-llvm | -- | -- | (arm not ok in a block) |
| hashjoin | serial-licensed-native | W3-2 T2 stable CSR, pilot size | r3w3t2off/r3w3 | blade-cpp | 0.087 -> 8.707 | 100.082 [95.326, 116.158] | ind 21/21 |
| hashjoin | serial-licensed-native | W3-2 T2 stable CSR, pilot size | r3w3t2off/r3w3 | gfortran-natural | 1.293 -> 1.294 | 1.001 [0.995, 1.002] | ind 21/21 |
| hashjoin | serial-licensed-native | W3-2 T2 stable CSR, pilot size | r3w3t2off/r3w3 | gfortran-expert | 0.0677 -> 0.0703 | 1.038 [1.029, 1.043] | ind 21/21 |
| hashjoin | serial-licensed-native | W3-2 T2 stable CSR, pilot size | r3w3t2off/r3w3 | ifx-natural | 0.6487 -> 0.6536 | 1.008 [0.992, 1.010] | ind 21/21 |
| hashjoin | serial-licensed-native | W3-2 T2 stable CSR, pilot size | r3w3t2off/r3w3 | ifx-expert | 0.0771 -> 0.0768 | 0.996 [0.986, 1.022] | ind 21/21 |
| hashjoin | serial-repro-native | W3-2 T2 stable CSR, pilot size | r3w3/r3w2bin | blade-llvm | -- | -- | (arm not ok in a block) |
| hashjoin | serial-repro-native | W3-2 T2 stable CSR, pilot size | r3w3/r3w2bin | blade-cpp | 9.41 -> 0.0853 | 0.009 [0.008, 0.010] | ind 21/21 |
| hashjoin | serial-repro-native | W3-2 T2 stable CSR, pilot size | r3w3/r3w2bin | gfortran-natural | 1.292 -> 1.287 | 0.996 [0.991, 1.000] | ind 21/21 |
| hashjoin | serial-repro-native | W3-2 T2 stable CSR, pilot size | r3w3/r3w2bin | gfortran-expert | 0.0681 -> 0.0686 | 1.007 [0.966, 1.019] | ind 21/21 |
| hashjoin | serial-repro-native | W3-2 T2 stable CSR, pilot size | r3w3/r3w2bin | ifx-natural | 0.6479 -> 0.6514 | 1.005 [0.996, 1.012] | ind 21/21 |
| hashjoin | serial-repro-native | W3-2 T2 stable CSR, pilot size | r3w3/r3w2bin | ifx-expert | 0.0765 -> 0.0757 | 0.990 [0.968, 0.997] | ind 21/21 |
| hashjoin | serial-repro-native | W3-2 T2 stable CSR, pilot size | r3w3t2off/r3w3 | blade-llvm | -- | -- | (arm not ok in a block) |
| hashjoin | serial-repro-native | W3-2 T2 stable CSR, pilot size | r3w3t2off/r3w3 | blade-cpp | 0.0853 -> 8.565 | 100.407 [98.159, 107.612] | live 21/21 |
| hashjoin | serial-repro-native | W3-2 T2 stable CSR, pilot size | r3w3t2off/r3w3 | gfortran-natural | 1.287 -> 1.292 | 1.004 [0.996, 1.005] | live 21/21 |
| hashjoin | serial-repro-native | W3-2 T2 stable CSR, pilot size | r3w3t2off/r3w3 | gfortran-expert | 0.0686 -> 0.0676 | 0.985 [0.975, 0.993] | live 21/21 |
| hashjoin | serial-repro-native | W3-2 T2 stable CSR, pilot size | r3w3t2off/r3w3 | ifx-natural | 0.6514 -> 0.648 | 0.995 [0.988, 0.997] | live 21/21 |
| hashjoin | serial-repro-native | W3-2 T2 stable CSR, pilot size | r3w3t2off/r3w3 | ifx-expert | 0.0757 -> 0.0765 | 1.011 [1.005, 1.015] | live 21/21 |
| pairaction | serial-licensed-native | W3-4 P2 row actions (mt8: the action declines, the walk threads) | r3w3/r3w2bin | blade-llvm | 60.83 -> 35.85 | 0.589 [0.585, 0.596] | ind 11/21 |
| pairaction | serial-licensed-native | W3-4 P2 row actions (mt8: the action declines, the walk threads) | r3w3/r3w2bin | blade-cpp | 78.29 -> 34.93 | 0.446 [0.435, 0.451] | ind 11/21 |
| pairaction | serial-licensed-native | W3-4 P2 row actions (mt8: the action declines, the walk threads) | r3w3/r3w2bin | gfortran-natural | 79.72 -> 79.91 | 1.002 [0.999, 1.010] | ind 11/21 |
| pairaction | serial-licensed-native | W3-4 P2 row actions (mt8: the action declines, the walk threads) | r3w3/r3w2bin | gfortran-expert | 39.74 -> 39.78 | 1.001 [0.964, 1.005] | ind 11/21 |
| pairaction | serial-licensed-native | W3-4 P2 row actions (mt8: the action declines, the walk threads) | r3w3/r3w2bin | ifx-natural | 77.07 -> 77.82 | 1.010 [0.995, 1.030] | ind 11/21 |
| pairaction | serial-licensed-native | W3-4 P2 row actions (mt8: the action declines, the walk threads) | r3w3/r3w2bin | ifx-expert | 35.24 -> 34.08 | 0.967 [0.953, 0.984] | ind 11/21 |
| pairaction | serial-licensed-native | W3-4 P2 row actions (mt8: the action declines, the walk threads) | r3w3strict/r3w3 | blade-llvm | 35.85 -> 60.23 | 1.680 [1.659, 1.715] | ind 21/11 |
| pairaction | serial-licensed-native | W3-4 P2 row actions (mt8: the action declines, the walk threads) | r3w3strict/r3w3 | blade-cpp | 34.93 -> 80.63 | 2.308 [2.258, 2.439] | ind 21/11 |
| pairaction | serial-licensed-native | W3-4 P2 row actions (mt8: the action declines, the walk threads) | r3w3strict/r3w3 | gfortran-natural | 79.91 -> 79.91 | 1.000 [0.992, 1.029] | ind 21/11 |
| pairaction | serial-licensed-native | W3-4 P2 row actions (mt8: the action declines, the walk threads) | r3w3strict/r3w3 | gfortran-expert | 39.78 -> 39.83 | 1.001 [0.994, 1.038] | ind 21/11 |
| pairaction | serial-licensed-native | W3-4 P2 row actions (mt8: the action declines, the walk threads) | r3w3strict/r3w3 | ifx-natural | 77.82 -> 77.06 | 0.990 [0.967, 1.041] | ind 21/11 |
| pairaction | serial-licensed-native | W3-4 P2 row actions (mt8: the action declines, the walk threads) | r3w3strict/r3w3 | ifx-expert | 34.08 -> 35.01 | 1.027 [1.009, 1.049] | ind 21/11 |
| pairaction | mt8-licensed-native | W3-4 P2 row actions (mt8: the action declines, the walk threads) | r3w3/r3w2bin | blade-llvm | -- | -- | (arm not ok in a block) |
| pairaction | mt8-licensed-native | W3-4 P2 row actions (mt8: the action declines, the walk threads) | r3w3/r3w2bin | blade-cpp | 21.23 -> 20.9 | 0.984 [0.960, 1.051] | ind 21/21 |
| pairaction | mt8-licensed-native | W3-4 P2 row actions (mt8: the action declines, the walk threads) | r3w3/r3w2bin | gfortran-natural | 24.26 -> 23.44 | 0.967 [0.938, 0.999] | ind 21/21 |
| pairaction | mt8-licensed-native | W3-4 P2 row actions (mt8: the action declines, the walk threads) | r3w3/r3w2bin | gfortran-expert | 12.32 -> 12.32 | 1.000 [0.980, 1.023] | ind 21/21 |
| pairaction | mt8-licensed-native | W3-4 P2 row actions (mt8: the action declines, the walk threads) | r3w3/r3w2bin | ifx-natural | 25.07 -> 25.2 | 1.005 [0.990, 1.020] | ind 21/21 |
| pairaction | mt8-licensed-native | W3-4 P2 row actions (mt8: the action declines, the walk threads) | r3w3/r3w2bin | ifx-expert | 11.13 -> 11.22 | 1.008 [0.972, 1.035] | ind 21/21 |
| pairaction | mt8-licensed-native | W3-4 P2 row actions (mt8: the action declines, the walk threads) | r3w3strict/r3w3 | blade-llvm | -- | -- | (arm not ok in a block) |
| pairaction | mt8-licensed-native | W3-4 P2 row actions (mt8: the action declines, the walk threads) | r3w3strict/r3w3 | blade-cpp | 20.9 -> 20.94 | 1.002 [0.944, 1.041] | ind 21/21 |
| pairaction | mt8-licensed-native | W3-4 P2 row actions (mt8: the action declines, the walk threads) | r3w3strict/r3w3 | gfortran-natural | 23.44 -> 24.2 | 1.032 [1.002, 1.080] | ind 21/21 |
| pairaction | mt8-licensed-native | W3-4 P2 row actions (mt8: the action declines, the walk threads) | r3w3strict/r3w3 | gfortran-expert | 12.32 -> 12.52 | 1.016 [0.990, 1.043] | ind 21/21 |
| pairaction | mt8-licensed-native | W3-4 P2 row actions (mt8: the action declines, the walk threads) | r3w3strict/r3w3 | ifx-natural | 25.2 -> 25.34 | 1.005 [0.987, 1.015] | ind 21/21 |
| pairaction | mt8-licensed-native | W3-4 P2 row actions (mt8: the action declines, the walk threads) | r3w3strict/r3w3 | ifx-expert | 11.22 -> 11.4 | 1.016 [0.981, 1.041] | ind 21/21 |
| pairaction | serial-repro-native | W3-4 P2 row actions (mt8: the action declines, the walk threads) | r3w3/r3w2bin | blade-llvm | 66.81 -> 36.82 | 0.551 [0.491, 0.564] | ind 11/21 |
| pairaction | serial-repro-native | W3-4 P2 row actions (mt8: the action declines, the walk threads) | r3w3/r3w2bin | blade-cpp | 68.25 -> 35.41 | 0.519 [0.494, 0.533] | ind 11/21 |
| pairaction | serial-repro-native | W3-4 P2 row actions (mt8: the action declines, the walk threads) | r3w3/r3w2bin | gfortran-natural | 80.06 -> 79.74 | 0.996 [0.987, 1.004] | ind 11/21 |
| pairaction | serial-repro-native | W3-4 P2 row actions (mt8: the action declines, the walk threads) | r3w3/r3w2bin | gfortran-expert | 37.35 -> 37.46 | 1.003 [0.981, 1.014] | ind 11/21 |
| pairaction | serial-repro-native | W3-4 P2 row actions (mt8: the action declines, the walk threads) | r3w3/r3w2bin | ifx-natural | 77.37 -> 77.32 | 0.999 [0.984, 1.016] | ind 11/21 |
| pairaction | serial-repro-native | W3-4 P2 row actions (mt8: the action declines, the walk threads) | r3w3/r3w2bin | ifx-expert | 40.6 -> 40.99 | 1.010 [1.004, 1.029] | ind 11/21 |
| pairaction | serial-repro-native | W3-4 P2 row actions (mt8: the action declines, the walk threads) | r3w3strict/r3w3 | blade-llvm | 36.82 -> 36.51 | 0.992 [0.976, 1.010] | ind 21/21 |
| pairaction | serial-repro-native | W3-4 P2 row actions (mt8: the action declines, the walk threads) | r3w3strict/r3w3 | blade-cpp | 35.41 -> 35.49 | 1.002 [0.981, 1.012] | ind 21/21 |
| pairaction | serial-repro-native | W3-4 P2 row actions (mt8: the action declines, the walk threads) | r3w3strict/r3w3 | gfortran-natural | 79.74 -> 79.8 | 1.001 [0.993, 1.005] | ind 21/21 |
| pairaction | serial-repro-native | W3-4 P2 row actions (mt8: the action declines, the walk threads) | r3w3strict/r3w3 | gfortran-expert | 37.46 -> 37.5 | 1.001 [0.988, 1.017] | ind 21/21 |
| pairaction | serial-repro-native | W3-4 P2 row actions (mt8: the action declines, the walk threads) | r3w3strict/r3w3 | ifx-natural | 77.32 -> 77.21 | 0.999 [0.986, 1.005] | ind 21/21 |
| pairaction | serial-repro-native | W3-4 P2 row actions (mt8: the action declines, the walk threads) | r3w3strict/r3w3 | ifx-expert | 40.99 -> 41.46 | 1.011 [0.991, 1.022] | ind 21/21 |
| pairaction | mt8-repro-native | W3-4 P2 row actions (mt8: the action declines, the walk threads) | r3w3/r3w2bin | blade-llvm | -- | -- | (arm not ok in a block) |
| pairaction | mt8-repro-native | W3-4 P2 row actions (mt8: the action declines, the walk threads) | r3w3/r3w2bin | blade-cpp | 20.56 -> 19.99 | 0.972 [0.944, 1.007] | ind 21/21 |
| pairaction | mt8-repro-native | W3-4 P2 row actions (mt8: the action declines, the walk threads) | r3w3/r3w2bin | gfortran-natural | 23.82 -> 23.95 | 1.006 [0.952, 1.034] | ind 21/21 |
| pairaction | mt8-repro-native | W3-4 P2 row actions (mt8: the action declines, the walk threads) | r3w3/r3w2bin | gfortran-expert | 12.15 -> 12.24 | 1.007 [0.986, 1.021] | ind 21/21 |
| pairaction | mt8-repro-native | W3-4 P2 row actions (mt8: the action declines, the walk threads) | r3w3/r3w2bin | ifx-natural | 25.01 -> 25.06 | 1.002 [0.986, 1.023] | ind 21/21 |
| pairaction | mt8-repro-native | W3-4 P2 row actions (mt8: the action declines, the walk threads) | r3w3/r3w2bin | ifx-expert | 11.36 -> 11.15 | 0.982 [0.962, 1.008] | ind 21/21 |
| pairaction | mt8-repro-native | W3-4 P2 row actions (mt8: the action declines, the walk threads) | r3w3strict/r3w3 | blade-llvm | -- | -- | (arm not ok in a block) |
| pairaction | mt8-repro-native | W3-4 P2 row actions (mt8: the action declines, the walk threads) | r3w3strict/r3w3 | blade-cpp | 19.99 -> 20.5 | 1.025 [0.992, 1.061] | ind 21/21 |
| pairaction | mt8-repro-native | W3-4 P2 row actions (mt8: the action declines, the walk threads) | r3w3strict/r3w3 | gfortran-natural | 23.95 -> 23.94 | 1.000 [0.966, 1.058] | ind 21/21 |
| pairaction | mt8-repro-native | W3-4 P2 row actions (mt8: the action declines, the walk threads) | r3w3strict/r3w3 | gfortran-expert | 12.24 -> 12.19 | 0.996 [0.983, 1.020] | ind 21/21 |
| pairaction | mt8-repro-native | W3-4 P2 row actions (mt8: the action declines, the walk threads) | r3w3strict/r3w3 | ifx-natural | 25.06 -> 24.89 | 0.993 [0.979, 1.011] | ind 21/21 |
| pairaction | mt8-repro-native | W3-4 P2 row actions (mt8: the action declines, the walk threads) | r3w3strict/r3w3 | ifx-expert | 11.15 -> 11.55 | 1.036 [0.984, 1.077] | ind 21/21 |
| pairkernel | mt8-repro-native | P1 baseline / control | r3w3/r3w2bin | blade-llvm | -- | -- | (arm not ok in a block) |
| pairkernel | mt8-repro-native | P1 baseline / control | r3w3/r3w2bin | blade-cpp | 8.059 -> 8.082 | 1.003 [0.969, 1.034] | ind 21/21 |
| pairkernel | mt8-repro-native | P1 baseline / control | r3w3/r3w2bin | gfortran-natural | 15.97 -> 16.35 | 1.024 [0.944, 1.126] | ind 21/21 |
| pairkernel | mt8-repro-native | P1 baseline / control | r3w3/r3w2bin | gfortran-expert | 6.255 -> 6.494 | 1.038 [1.015, 1.079] | ind 21/21 |
| pairkernel | mt8-repro-native | P1 baseline / control | r3w3/r3w2bin | ifx-natural | 17.7 -> 17.48 | 0.988 [0.979, 1.014] | ind 21/21 |
| pairkernel | mt8-repro-native | P1 baseline / control | r3w3/r3w2bin | ifx-expert | 6.38 -> 6.399 | 1.003 [0.980, 1.031] | ind 21/21 |
| saxpy | serial-licensed-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3/r3w1 | blade-llvm | 10.86 -> 11.36 | 1.046 [1.004, 1.180] | ind 21/21 |
| saxpy | serial-licensed-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3/r3w1 | blade-cpp | 15.4 -> 16.61 | 1.079 [1.026, 1.163] | ind 21/21 |
| saxpy | serial-licensed-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3/r3w1 | gfortran-natural | 16.84 -> 17.26 | 1.025 [1.012, 1.109] | ind 21/21 |
| saxpy | serial-licensed-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3/r3w1 | gfortran-expert | 14.73 -> 15.62 | 1.060 [1.000, 1.124] | ind 21/21 |
| saxpy | serial-licensed-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3/r3w1 | ifx-natural | 13.14 -> 13.58 | 1.033 [1.015, 1.128] | ind 21/21 |
| saxpy | serial-licensed-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3/r3w1 | ifx-expert | 10.74 -> 11.29 | 1.051 [1.005, 1.261] | ind 21/21 |
| saxpy | serial-licensed-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3legs1/r3w3 | blade-llvm | 11.36 -> 10.13 | 0.892 [0.799, 0.978] | ind 21/21 |
| saxpy | serial-licensed-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3legs1/r3w3 | blade-cpp | 16.61 -> 15.3 | 0.921 [0.850, 1.013] | ind 21/21 |
| saxpy | serial-licensed-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3legs1/r3w3 | gfortran-natural | 17.26 -> 17.13 | 0.992 [0.907, 1.014] | ind 21/21 |
| saxpy | serial-licensed-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3legs1/r3w3 | gfortran-expert | 15.62 -> 15.2 | 0.974 [0.918, 1.053] | ind 21/21 |
| saxpy | serial-licensed-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3legs1/r3w3 | ifx-natural | 13.58 -> 13.44 | 0.989 [0.899, 1.060] | ind 21/21 |
| saxpy | serial-licensed-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3legs1/r3w3 | ifx-expert | 11.29 -> 10.96 | 0.971 [0.819, 1.040] | ind 21/21 |
| saxpy | serial-licensed-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3legs1/r3w1 | blade-llvm | 10.86 -> 10.13 | 0.933 [0.913, 1.017] | ind 21/21 |
| saxpy | serial-licensed-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3legs1/r3w1 | blade-cpp | 15.4 -> 15.3 | 0.993 [0.968, 1.089] | ind 21/21 |
| saxpy | serial-licensed-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3legs1/r3w1 | gfortran-natural | 16.84 -> 17.13 | 1.017 [0.991, 1.038] | ind 21/21 |
| saxpy | serial-licensed-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3legs1/r3w1 | gfortran-expert | 14.73 -> 15.2 | 1.032 [1.006, 1.098] | ind 21/21 |
| saxpy | serial-licensed-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3legs1/r3w1 | ifx-natural | 13.14 -> 13.44 | 1.023 [0.992, 1.100] | ind 21/21 |
| saxpy | serial-licensed-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3legs1/r3w1 | ifx-expert | 10.74 -> 10.96 | 1.021 [0.990, 1.082] | ind 21/21 |
| saxpy | mt8-licensed-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3/r3w1 | blade-llvm | -- | -- | (arm not ok in a block) |
| saxpy | mt8-licensed-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3/r3w1 | blade-cpp | 10.18 -> 10.15 | 0.997 [0.938, 1.027] | ind 21/21 |
| saxpy | mt8-licensed-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3/r3w1 | gfortran-natural | 10.4 -> 10.27 | 0.987 [0.937, 1.041] | ind 21/21 |
| saxpy | mt8-licensed-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3/r3w1 | gfortran-expert | 9.046 -> 8.597 | 0.950 [0.937, 0.992] | ind 21/21 |
| saxpy | mt8-licensed-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3/r3w1 | ifx-natural | 10.42 -> 10.41 | 0.999 [0.964, 1.045] | ind 21/21 |
| saxpy | mt8-licensed-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3/r3w1 | ifx-expert | 9.172 -> 8.96 | 0.977 [0.946, 1.022] | ind 21/21 |
| saxpy | mt8-licensed-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3legs1/r3w3 | blade-llvm | -- | -- | (arm not ok in a block) |
| saxpy | mt8-licensed-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3legs1/r3w3 | blade-cpp | 10.15 -> 10.14 | 0.999 [0.974, 1.030] | ind 21/21 |
| saxpy | mt8-licensed-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3legs1/r3w3 | gfortran-natural | 10.27 -> 10.26 | 0.999 [0.973, 1.040] | ind 21/21 |
| saxpy | mt8-licensed-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3legs1/r3w3 | gfortran-expert | 8.597 -> 8.671 | 1.009 [0.966, 1.049] | ind 21/21 |
| saxpy | mt8-licensed-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3legs1/r3w3 | ifx-natural | 10.41 -> 10.01 | 0.961 [0.936, 1.007] | ind 21/21 |
| saxpy | mt8-licensed-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3legs1/r3w3 | ifx-expert | 8.96 -> 9.123 | 1.018 [0.964, 1.053] | ind 21/21 |
| saxpy | mt8-licensed-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3legs1/r3w1 | blade-llvm | -- | -- | (arm not ok in a block) |
| saxpy | mt8-licensed-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3legs1/r3w1 | blade-cpp | 10.18 -> 10.14 | 0.995 [0.942, 1.029] | ind 21/21 |
| saxpy | mt8-licensed-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3legs1/r3w1 | gfortran-natural | 10.4 -> 10.26 | 0.986 [0.949, 1.036] | ind 21/21 |
| saxpy | mt8-licensed-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3legs1/r3w1 | gfortran-expert | 9.046 -> 8.671 | 0.959 [0.941, 0.997] | ind 21/21 |
| saxpy | mt8-licensed-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3legs1/r3w1 | ifx-natural | 10.42 -> 10.01 | 0.961 [0.948, 1.005] | ind 21/21 |
| saxpy | mt8-licensed-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3legs1/r3w1 | ifx-expert | 9.172 -> 9.123 | 0.995 [0.940, 1.033] | ind 21/21 |
| saxpy | serial-repro-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3/r3w1 | blade-llvm | 11.89 -> 11.6 | 0.976 [0.938, 1.073] | ind 21/21 |
| saxpy | serial-repro-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3/r3w1 | blade-cpp | 16.8 -> 16.73 | 0.996 [0.956, 1.038] | ind 21/21 |
| saxpy | serial-repro-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3/r3w1 | gfortran-natural | 17.54 -> 17.38 | 0.991 [0.951, 1.057] | ind 21/21 |
| saxpy | serial-repro-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3/r3w1 | gfortran-expert | 16.24 -> 16.1 | 0.992 [0.945, 1.049] | ind 21/21 |
| saxpy | serial-repro-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3/r3w1 | ifx-natural | 13.89 -> 13.63 | 0.982 [0.950, 1.028] | ind 21/21 |
| saxpy | serial-repro-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3/r3w1 | ifx-expert | 10.36 -> 10.39 | 1.003 [0.897, 1.063] | ind 21/21 |
| saxpy | serial-repro-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3legs1/r3w3 | blade-llvm | 11.6 -> 10.33 | 0.890 [0.835, 0.923] | ind 21/21 |
| saxpy | serial-repro-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3legs1/r3w3 | blade-cpp | 16.73 -> 14.5 | 0.867 [0.838, 0.888] | ind 21/21 |
| saxpy | serial-repro-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3legs1/r3w3 | gfortran-natural | 17.38 -> 16.86 | 0.970 [0.934, 1.000] | ind 21/21 |
| saxpy | serial-repro-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3legs1/r3w3 | gfortran-expert | 16.1 -> 15.37 | 0.954 [0.908, 0.980] | ind 21/21 |
| saxpy | serial-repro-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3legs1/r3w3 | ifx-natural | 13.63 -> 13.12 | 0.963 [0.921, 0.981] | ind 21/21 |
| saxpy | serial-repro-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3legs1/r3w3 | ifx-expert | 10.39 -> 9.833 | 0.946 [0.889, 1.014] | ind 21/21 |
| saxpy | serial-repro-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3legs1/r3w1 | blade-llvm | 11.89 -> 10.33 | 0.869 [0.831, 0.930] | live 21/21 |
| saxpy | serial-repro-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3legs1/r3w1 | blade-cpp | 16.8 -> 14.5 | 0.863 [0.835, 0.881] | live 21/21 |
| saxpy | serial-repro-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3legs1/r3w1 | gfortran-natural | 17.54 -> 16.86 | 0.961 [0.941, 0.993] | live 21/21 |
| saxpy | serial-repro-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3legs1/r3w1 | gfortran-expert | 16.24 -> 15.37 | 0.946 [0.908, 0.975] | live 21/21 |
| saxpy | serial-repro-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3legs1/r3w1 | ifx-natural | 13.89 -> 13.12 | 0.945 [0.918, 0.973] | live 21/21 |
| saxpy | serial-repro-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3legs1/r3w1 | ifx-expert | 10.36 -> 9.833 | 0.949 [0.872, 0.987] | live 21/21 |
| saxpy | mt8-repro-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3/r3w1 | blade-llvm | -- | -- | (arm not ok in a block) |
| saxpy | mt8-repro-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3/r3w1 | blade-cpp | 12.42 -> 12.58 | 1.013 [0.975, 1.049] | ind 21/21 |
| saxpy | mt8-repro-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3/r3w1 | gfortran-natural | 10.33 -> 10.4 | 1.007 [0.978, 1.056] | ind 21/21 |
| saxpy | mt8-repro-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3/r3w1 | gfortran-expert | 9.376 -> 9.567 | 1.020 [0.987, 1.060] | ind 21/21 |
| saxpy | mt8-repro-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3/r3w1 | ifx-natural | 10.38 -> 10.54 | 1.016 [0.988, 1.041] | ind 21/21 |
| saxpy | mt8-repro-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3/r3w1 | ifx-expert | 9.604 -> 9.869 | 1.028 [0.999, 1.060] | ind 21/21 |
| saxpy | mt8-repro-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3legs1/r3w3 | blade-llvm | -- | -- | (arm not ok in a block) |
| saxpy | mt8-repro-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3legs1/r3w3 | blade-cpp | 12.58 -> 12.16 | 0.967 [0.936, 1.000] | live 21/21 |
| saxpy | mt8-repro-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3legs1/r3w3 | gfortran-natural | 10.4 -> 10.04 | 0.965 [0.929, 1.003] | live 21/21 |
| saxpy | mt8-repro-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3legs1/r3w3 | gfortran-expert | 9.567 -> 9.342 | 0.977 [0.952, 1.005] | live 21/21 |
| saxpy | mt8-repro-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3legs1/r3w3 | ifx-natural | 10.54 -> 10.18 | 0.966 [0.940, 0.993] | live 21/21 |
| saxpy | mt8-repro-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3legs1/r3w3 | ifx-expert | 9.869 -> 9.547 | 0.967 [0.929, 0.985] | live 21/21 |
| saxpy | mt8-repro-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3legs1/r3w1 | blade-llvm | -- | -- | (arm not ok in a block) |
| saxpy | mt8-repro-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3legs1/r3w1 | blade-cpp | 12.42 -> 12.16 | 0.979 [0.949, 1.009] | ind 21/21 |
| saxpy | mt8-repro-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3legs1/r3w1 | gfortran-natural | 10.33 -> 10.04 | 0.972 [0.952, 1.015] | ind 21/21 |
| saxpy | mt8-repro-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3legs1/r3w1 | gfortran-expert | 9.376 -> 9.342 | 0.996 [0.967, 1.033] | ind 21/21 |
| saxpy | mt8-repro-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3legs1/r3w1 | ifx-natural | 10.38 -> 10.18 | 0.982 [0.959, 1.003] | ind 21/21 |
| saxpy | mt8-repro-native | W2-4 legs re-measure: r3w3 default = joined pass, legs1 = the opt-in inline (serial = target, mt8 = decline control) | r3w3legs1/r3w1 | ifx-expert | 9.604 -> 9.547 | 0.994 [0.959, 1.013] | ind 21/21 |
| comoment3 | mt8-repro-native | control (the pull) | r3w3/r3w2bin | blade-llvm | -- | -- | (arm not ok in a block) |
| comoment3 | mt8-repro-native | control (the pull) | r3w3/r3w2bin | blade-cpp | 3.259 -> 3.417 | 1.049 [0.971, 1.083] | ind 21/21 |
| comoment3 | mt8-repro-native | control (the pull) | r3w3/r3w2bin | gfortran-natural | 97.4 -> 97.49 | 1.001 [0.962, 1.030] | ind 21/21 |
| comoment3 | mt8-repro-native | control (the pull) | r3w3/r3w2bin | gfortran-expert | 5.166 -> 5.215 | 1.009 [0.952, 1.063] | ind 21/21 |
| comoment3 | mt8-repro-native | control (the pull) | r3w3/r3w2bin | ifx-natural | 125.7 -> 123.7 | 0.984 [0.945, 1.010] | ind 21/21 |
| comoment3 | mt8-repro-native | control (the pull) | r3w3/r3w2bin | ifx-expert | 4.221 -> 4.249 | 1.007 [0.979, 1.068] | ind 21/21 |
| gram | serial-licensed-native | control | r3w3/r3w2bin | blade-llvm | 57.05 -> 56.79 | 0.995 [0.975, 1.008] | ind 11/11 |
| gram | serial-licensed-native | control | r3w3/r3w2bin | blade-cpp | 58.08 -> 58.92 | 1.014 [0.988, 1.029] | ind 11/11 |
| gram | serial-licensed-native | control | r3w3/r3w2bin | gfortran-natural | 526.2 -> 495.2 | 0.941 [0.844, 0.986] | ind 11/11 |
| gram | serial-licensed-native | control | r3w3/r3w2bin | gfortran-expert | 77.77 -> 77.21 | 0.993 [0.971, 1.008] | ind 11/11 |
| gram | serial-licensed-native | control | r3w3/r3w2bin | ifx-natural | 570.5 -> 570.1 | 0.999 [0.997, 1.014] | ind 11/11 |
| gram | serial-licensed-native | control | r3w3/r3w2bin | ifx-expert | 70.86 -> 70.36 | 0.993 [0.980, 1.005] | ind 11/11 |

## 2. Arm medians per block (ms)

| kernel | regime | mirror | block | blade-llvm | blade-cpp | gfortran-natural | gfortran-expert | ifx-natural | ifx-expert |
|---|---|---|---|---|---|---|---|---|---|
| groupagg | serial-repro-native | r3w2bin | live | 15.23 | 16.89 | 1249 | 8.733 | 474.6 | 5.867 |
| groupagg | serial-repro-native | r3w3 | live | 6.619 | 7.958 | 1265 | 8.808 | 491.1 | 5.886 |
| groupagg | serial-repro-native | r3w3t3off | indicative | 15.47 | 17.37 | 1265 | 8.682 | 490.4 | 6.058 |
| groupagg | serial-licensed-native | r3w2bin | live | 14.58 | 16.68 | 1305 | 8.313 | 491.8 | 4.836 |
| groupagg | serial-licensed-native | r3w3 | indicative | 14.72 | 17.1 | 1278 | 8.397 | 505.3 | 4.954 |
| groupagg | serial-licensed-native | r3w3t3lic | live | 6.341 | 7.744 | 1255 | 8.103 | 480.2 | 4.722 |
| groupagg_dyn | serial-repro-native | r3w2bin | live | 248.2 | 24.71 | 1299 | 19.08 | 563.6 | 18.8 |
| groupagg_dyn | serial-repro-native | r3w3 | live | 12.3 | 12.13 | 1322 | 19.1 | 574.1 | 18.85 |
| groupagg_dyn | serial-repro-native | r3w3t3off | indicative | 32.35 | 25.96 | 1403 | 19.06 | 624.2 | 18.88 |
| groupagg_dyn | serial-licensed-native | r3w2bin | indicative | 248.2 | 25.15 | 1512 | 18.94 | 714.9 | 18.16 |
| groupagg_dyn | serial-licensed-native | r3w3 | indicative | 31.18 | 24.87 | 1332 | 18.93 | 582.1 | 18.12 |
| groupagg_dyn | serial-licensed-native | r3w3t3lic | indicative | 12.3 | 11.89 | 1316 | 18.89 | 562.8 | 18.13 |
| hashjoin | serial-licensed-native | r3w2bin | live | fallback: a rank-2 compound-index initializer (the hashed in | 8.456 | 1.288 | 0.0677 | 0.6494 | 0.0764 |
| hashjoin | serial-licensed-native | r3w3 | indicative | fallback: a rank-2 compound-index initializer (llvm lane: th | 0.087 | 1.293 | 0.0677 | 0.6487 | 0.0771 |
| hashjoin | serial-licensed-native | r3w3t2off | indicative | fallback: a rank-2 compound-index initializer (the hashed in | 8.707 | 1.294 | 0.0703 | 0.6536 | 0.0768 |
| hashjoin | serial-repro-native | r3w2bin | indicative | fallback: a rank-2 compound-index initializer (the hashed in | 9.41 | 1.292 | 0.0681 | 0.6479 | 0.0765 |
| hashjoin | serial-repro-native | r3w3 | live | fallback: a rank-2 compound-index initializer (llvm lane: th | 0.0853 | 1.287 | 0.0686 | 0.6514 | 0.0757 |
| hashjoin | serial-repro-native | r3w3t2off | live | fallback: a rank-2 compound-index initializer (the hashed in | 8.565 | 1.292 | 0.0676 | 0.648 | 0.0765 |
| pairaction | serial-licensed-native | r3w2bin | indicative | 60.83 | 78.29 | 79.72 | 39.74 | 77.07 | 35.24 |
| pairaction | serial-licensed-native | r3w3 | live | 35.85 | 34.93 | 79.91 | 39.78 | 77.82 | 34.08 |
| pairaction | serial-licensed-native | r3w3strict | indicative | 60.23 | 80.63 | 79.91 | 39.83 | 77.06 | 35.01 |
| pairaction | mt8-licensed-native | r3w2bin | indicative | serial-only: LLVM lane has no OpenMP | 21.23 | 24.26 | 12.32 | 25.07 | 11.13 |
| pairaction | mt8-licensed-native | r3w3 | indicative | serial-only: LLVM lane has no OpenMP | 20.9 | 23.44 | 12.32 | 25.2 | 11.22 |
| pairaction | mt8-licensed-native | r3w3strict | indicative | serial-only: LLVM lane has no OpenMP | 20.94 | 24.2 | 12.52 | 25.34 | 11.4 |
| pairaction | serial-repro-native | r3w2bin | indicative | 66.81 | 68.25 | 80.06 | 37.35 | 77.37 | 40.6 |
| pairaction | serial-repro-native | r3w3 | indicative | 36.82 | 35.41 | 79.74 | 37.46 | 77.32 | 40.99 |
| pairaction | serial-repro-native | r3w3strict | live | 36.51 | 35.49 | 79.8 | 37.5 | 77.21 | 41.46 |
| pairaction | mt8-repro-native | r3w2bin | indicative | serial-only: LLVM lane has no OpenMP | 20.56 | 23.82 | 12.15 | 25.01 | 11.36 |
| pairaction | mt8-repro-native | r3w3 | indicative | serial-only: LLVM lane has no OpenMP | 19.99 | 23.95 | 12.24 | 25.06 | 11.15 |
| pairaction | mt8-repro-native | r3w3strict | indicative | serial-only: LLVM lane has no OpenMP | 20.5 | 23.94 | 12.19 | 24.89 | 11.55 |
| pairkernel | mt8-repro-native | r3w2bin | live | serial-only: LLVM lane has no OpenMP | 8.059 | 15.97 | 6.255 | 17.7 | 6.38 |
| pairkernel | mt8-repro-native | r3w3 | indicative | serial-only: LLVM lane has no OpenMP | 8.082 | 16.35 | 6.494 | 17.48 | 6.399 |
| saxpy | serial-licensed-native | r3w1 | live | 10.86 | 15.4 | 16.84 | 14.73 | 13.14 | 10.74 |
| saxpy | serial-licensed-native | r3w3 | indicative | 11.36 | 16.61 | 17.26 | 15.62 | 13.58 | 11.29 |
| saxpy | serial-licensed-native | r3w3legs1 | indicative | 10.13 | 15.3 | 17.13 | 15.2 | 13.44 | 10.96 |
| saxpy | mt8-licensed-native | r3w1 | indicative | serial-only: LLVM lane has no OpenMP | 10.18 | 10.4 | 9.046 | 10.42 | 9.172 |
| saxpy | mt8-licensed-native | r3w3 | indicative | serial-only: LLVM lane has no OpenMP | 10.15 | 10.27 | 8.597 | 10.41 | 8.96 |
| saxpy | mt8-licensed-native | r3w3legs1 | live | serial-only: LLVM lane has no OpenMP | 10.14 | 10.26 | 8.671 | 10.01 | 9.123 |
| saxpy | serial-repro-native | r3w1 | live | 11.89 | 16.8 | 17.54 | 16.24 | 13.89 | 10.36 |
| saxpy | serial-repro-native | r3w3 | indicative | 11.6 | 16.73 | 17.38 | 16.1 | 13.63 | 10.39 |
| saxpy | serial-repro-native | r3w3legs1 | live | 10.33 | 14.5 | 16.86 | 15.37 | 13.12 | 9.833 |
| saxpy | mt8-repro-native | r3w1 | indicative | serial-only: LLVM lane has no OpenMP | 12.42 | 10.33 | 9.376 | 10.38 | 9.604 |
| saxpy | mt8-repro-native | r3w3 | live | serial-only: LLVM lane has no OpenMP | 12.58 | 10.4 | 9.567 | 10.54 | 9.869 |
| saxpy | mt8-repro-native | r3w3legs1 | live | serial-only: LLVM lane has no OpenMP | 12.16 | 10.04 | 9.342 | 10.18 | 9.547 |
| comoment3 | mt8-repro-native | r3w2bin | live | serial-only: LLVM lane has no OpenMP | 3.259 | 97.4 | 5.166 | 125.7 | 4.221 |
| comoment3 | mt8-repro-native | r3w3 | indicative | serial-only: LLVM lane has no OpenMP | 3.417 | 97.49 | 5.215 | 123.7 | 4.249 |
| gram | serial-licensed-native | r3w2bin | indicative | 57.05 | 58.08 | 526.2 | 77.77 | 570.5 | 70.86 |
| gram | serial-licensed-native | r3w3 | live | 56.79 | 58.92 | 495.2 | 77.21 | 570.1 | 70.36 |

## 3. Expert ratios (expert median / Blade ref median; > 1 Blade faster); harness ratio [CI] verdict

| kernel | regime | mirror | ref | gfortran-expert | ifx-expert | block |
|---|---|---|---|---|---|---|
| groupagg | serial-repro-native | r3w2bin | blade-llvm | 0.574 [0.570, 0.581] loss | 0.385 [0.378, 0.391] loss | live |
| groupagg | serial-repro-native | r3w3 | blade-llvm | 1.331 [1.310, 1.362] win | 0.889 [0.870, 0.911] loss | live |
| groupagg | serial-repro-native | r3w3t3off | blade-llvm | 0.561 [0.550, 0.575] suppressed | 0.392 [0.373, 0.403] suppressed | indicative |
| groupagg | serial-licensed-native | r3w2bin | blade-llvm | 0.570 [0.557, 0.582] loss | 0.332 [0.324, 0.340] loss | live |
| groupagg | serial-licensed-native | r3w3 | blade-llvm | 0.570 [0.549, 0.586] suppressed | 0.336 [0.319, 0.347] suppressed | indicative |
| groupagg | serial-licensed-native | r3w3t3lic | blade-llvm | 1.278 [1.239, 1.308] win | 0.745 [0.725, 0.766] loss | live |
| groupagg_dyn | serial-repro-native | r3w2bin | blade-llvm | 0.077 [0.076, 0.079] loss | 0.076 [0.076, 0.076] loss | live |
| groupagg_dyn | serial-repro-native | r3w3 | blade-llvm | 1.554 [1.535, 1.567] win | 1.533 [1.518, 1.542] win | live |
| groupagg_dyn | serial-repro-native | r3w3t3off | blade-llvm | 0.589 [0.579, 0.595] suppressed | 0.584 [0.574, 0.589] suppressed | indicative |
| groupagg_dyn | serial-licensed-native | r3w2bin | blade-llvm | 0.076 [0.076, 0.077] suppressed | 0.073 [0.073, 0.074] suppressed | indicative |
| groupagg_dyn | serial-licensed-native | r3w3 | blade-llvm | 0.607 [0.590, 0.618] suppressed | 0.581 [0.567, 0.592] suppressed | indicative |
| groupagg_dyn | serial-licensed-native | r3w3t3lic | blade-llvm | 1.536 [1.493, 1.549] suppressed | 1.474 [1.439, 1.486] suppressed | indicative |
| hashjoin | serial-licensed-native | r3w2bin | blade-llvm | 0.008 [0.008, 0.008] loss | 0.009 [0.009, 0.010] loss | live |
| hashjoin | serial-licensed-native | r3w3 | blade-llvm | 0.778 [0.772, 0.785] suppressed | 0.886 [0.873, 0.891] suppressed | indicative |
| hashjoin | serial-licensed-native | r3w3t2off | blade-llvm | 0.008 [0.007, 0.008] suppressed | 0.009 [0.008, 0.009] suppressed | indicative |
| hashjoin | serial-repro-native | r3w2bin | blade-llvm | 0.007 [0.007, 0.008] suppressed | 0.008 [0.007, 0.009] suppressed | indicative |
| hashjoin | serial-repro-native | r3w3 | blade-llvm | 0.804 [0.800, 0.814] loss | 0.887 [0.883, 0.895] loss | live |
| hashjoin | serial-repro-native | r3w3t2off | blade-llvm | 0.008 [0.007, 0.008] loss | 0.009 [0.008, 0.009] loss | live |
| pairaction | serial-licensed-native | r3w2bin | blade-llvm | 0.653 [0.650, 0.683] suppressed | 0.579 [0.569, 0.590] suppressed | indicative |
| pairaction | serial-licensed-native | r3w3 | blade-llvm | 1.110 [1.100, 1.115] win | 0.951 [0.941, 0.959] detectable-loss | live |
| pairaction | serial-licensed-native | r3w3strict | blade-llvm | 0.661 [0.648, 0.690] suppressed | 0.581 [0.564, 0.597] suppressed | indicative |
| pairaction | mt8-licensed-native | r3w2bin | blade-cpp | 0.580 [0.571, 0.607] suppressed | 0.524 [0.518, 0.549] suppressed | indicative |
| pairaction | mt8-licensed-native | r3w3 | blade-cpp | 0.589 [0.562, 0.606] suppressed | 0.537 [0.506, 0.557] suppressed | indicative |
| pairaction | mt8-licensed-native | r3w3strict | blade-cpp | 0.598 [0.580, 0.620] suppressed | 0.544 [0.521, 0.564] suppressed | indicative |
| pairaction | serial-repro-native | r3w2bin | blade-llvm | 0.559 [0.500, 0.578] suppressed | 0.608 [0.540, 0.616] suppressed | indicative |
| pairaction | serial-repro-native | r3w3 | blade-llvm | 1.017 [1.001, 1.029] suppressed | 1.113 [1.099, 1.136] suppressed | indicative |
| pairaction | serial-repro-native | r3w3strict | blade-llvm | 1.027 [1.013, 1.041] detectable-win | 1.135 [1.114, 1.145] win | live |
| pairaction | mt8-repro-native | r3w2bin | blade-cpp | 0.591 [0.576, 0.611] suppressed | 0.553 [0.536, 0.569] suppressed | indicative |
| pairaction | mt8-repro-native | r3w3 | blade-cpp | 0.612 [0.597, 0.621] suppressed | 0.558 [0.541, 0.571] suppressed | indicative |
| pairaction | mt8-repro-native | r3w3strict | blade-cpp | 0.595 [0.574, 0.618] suppressed | 0.564 [0.539, 0.585] suppressed | indicative |
| pairkernel | mt8-repro-native | r3w2bin | blade-cpp | 0.776 [0.763, 0.793] loss | 0.792 [0.772, 0.809] loss | live |
| pairkernel | mt8-repro-native | r3w3 | blade-cpp | 0.804 [0.783, 0.849] suppressed | 0.792 [0.764, 0.821] suppressed | indicative |
| saxpy | serial-licensed-native | r3w1 | blade-llvm | 1.357 [1.330, 1.389] win | 0.989 [0.951, 1.017] equivalent | live |
| saxpy | serial-licensed-native | r3w3 | blade-llvm | 1.375 [1.251, 1.403] suppressed | 0.994 [0.955, 1.138] suppressed | indicative |
| saxpy | serial-licensed-native | r3w3legs1 | blade-llvm | 1.501 [1.399, 1.562] suppressed | 1.082 [1.013, 1.123] suppressed | indicative |
| saxpy | mt8-licensed-native | r3w1 | blade-cpp | 0.888 [0.842, 0.912] suppressed | 0.901 [0.854, 0.925] suppressed | indicative |
| saxpy | mt8-licensed-native | r3w3 | blade-cpp | 0.847 [0.826, 0.883] suppressed | 0.883 [0.868, 0.913] suppressed | indicative |
| saxpy | mt8-licensed-native | r3w3legs1 | blade-cpp | 0.855 [0.829, 0.890] loss | 0.900 [0.853, 0.936] loss | live |
| saxpy | serial-repro-native | r3w1 | blade-llvm | 1.367 [1.326, 1.455] win | 0.872 [0.866, 0.928] loss | live |
| saxpy | serial-repro-native | r3w3 | blade-llvm | 1.388 [1.325, 1.426] suppressed | 0.896 [0.839, 0.927] suppressed | indicative |
| saxpy | serial-repro-native | r3w3legs1 | blade-llvm | 1.488 [1.452, 1.528] win | 0.952 [0.907, 0.979] detectable-loss | live |
| saxpy | mt8-repro-native | r3w1 | blade-cpp | 0.755 [0.726, 0.777] suppressed | 0.773 [0.758, 0.803] suppressed | indicative |
| saxpy | mt8-repro-native | r3w3 | blade-cpp | 0.761 [0.737, 0.790] loss | 0.785 [0.763, 0.820] loss | live |
| saxpy | mt8-repro-native | r3w3legs1 | blade-cpp | 0.768 [0.752, 0.788] loss | 0.785 [0.762, 0.804] loss | live |
| comoment3 | mt8-repro-native | r3w2bin | blade-cpp | 1.585 [1.476, 1.638] win | 1.295 [1.183, 1.323] win | live |
| comoment3 | mt8-repro-native | r3w3 | blade-cpp | 1.526 [1.465, 1.596] suppressed | 1.244 [1.208, 1.305] suppressed | indicative |
| gram | serial-licensed-native | r3w2bin | blade-llvm | 1.363 [1.336, 1.394] suppressed | 1.242 [1.218, 1.259] suppressed | indicative |
| gram | serial-licensed-native | r3w3 | blade-llvm | 1.360 [1.350, 1.381] win | 1.239 [1.227, 1.250] win | live |

## 4. Blocks

| kernel | regime | mirror | rounds | class | compute ctl | memory ctl | flags | excursions | bundle |
|---|---|---|---|---|---|---|---|---|---|
| groupagg | serial-repro-native | r3w2bin | 21 | live | 12.788 | 5.297 | - | 0 | session-021805 |
| groupagg | serial-repro-native | r3w3 | 21 | live | 12.955 | 5.489 | - | 0 | session-022217 |
| groupagg | serial-repro-native | r3w3t3off | 21 | indicative | 12.876 | 5.439 | trend: blade-cpp first-half vs second-half median moved 7.0% | 0 | session-022628 |
| groupagg | serial-licensed-native | r3w2bin | 21 | live | 13.074 | 5.368 | - | 0 | session-023041 |
| groupagg | serial-licensed-native | r3w3 | 21 | indicative | 12.868 | 5.415 | trend: gfortran-natural first-half vs second-half median moved 8.3% | 0 | session-023453 |
| groupagg | serial-licensed-native | r3w3t3lic | 21 | live | 12.883 | 5.336 | - | 0 | session-023907 |
| groupagg_dyn | serial-repro-native | r3w2bin | 11 | live | 12.776 | 5.303 | - | 0 | session-024318 |
| groupagg_dyn | serial-repro-native | r3w3 | 21 | live | 12.752 | 5.249 | - | 0 | session-024559 |
| groupagg_dyn | serial-repro-native | r3w3t3off | 21 | indicative | 12.857 | 5.214 | trend: gfortran-natural first-half vs second-half median moved 5.6%; trend: ifx-natural first-half vs second-half median moved 9.9% | 0 | session-025014 |
| groupagg_dyn | serial-licensed-native | r3w2bin | 11 | indicative | 12.822 | 5.194 | trend: ifx-natural first-half vs second-half median moved 16.3% | 0 | session-025433 |
| groupagg_dyn | serial-licensed-native | r3w3 | 21 | indicative | 12.758 | 5.250 | trend: gfortran-natural first-half vs second-half median moved 13.1%; trend: ifx-natural first-half vs second-half median moved 15.8%; trend: control memory moved 5.8% within the block | 0 | session-025720 |
| groupagg_dyn | serial-licensed-native | r3w3t3lic | 21 | indicative | 12.853 | 5.323 | trend: ifx-natural first-half vs second-half median moved 6.8% | 0 | session-030138 |
| hashjoin | serial-licensed-native | r3w2bin | 21 | live | 12.672 | 5.189 | - | 0 | session-030554 |
| hashjoin | serial-licensed-native | r3w3 | 21 | indicative | 12.999 | 5.219 | trend: gfortran-natural-inputonly first-half vs second-half median moved 7.2%; trend: control compute moved 8.9% within the block | 0 | session-031218 |
| hashjoin | serial-licensed-native | r3w3t2off | 21 | indicative | 12.682 | 5.346 | trend: blade-cpp first-half vs second-half median moved 11.7% | 0 | session-031843 |
| hashjoin | serial-repro-native | r3w2bin | 21 | indicative | 14.843 | 5.790 | trend: blade-cpp first-half vs second-half median moved 6.0%; control compute median 0.01484s moved +17.1% vs the session baseline 0.01267s; control memory median 0.00579s moved +11.6% vs the session baseline 0.005189s | 0 | session-030554 |
| hashjoin | serial-repro-native | r3w3 | 21 | live | 12.715 | 5.281 | - | 0 | session-031218 |
| hashjoin | serial-repro-native | r3w3t2off | 21 | live | 12.700 | 5.303 | - | 0 | session-031843 |
| pairaction | serial-licensed-native | r3w2bin | 11 | indicative | 12.734 | 5.264 | trend: ifx-natural-inputonly first-half vs second-half median moved 8.4% | 0 | session-032510 |
| pairaction | serial-licensed-native | r3w3 | 21 | live | 12.776 | 5.390 | - | 0 | session-033531 |
| pairaction | serial-licensed-native | r3w3strict | 11 | indicative | 12.912 | 5.367 | trend: ifx-natural first-half vs second-half median moved 5.2%; trend: ifx-natural-inputonly first-half vs second-half median moved 12.9%; trend: control memory moved 14.3% within the block | 0 | session-034831 |
| pairaction | mt8-licensed-native | r3w2bin | 21 | indicative | 12.842 | 5.268 | trend: control compute moved 5.9% within the block | 0 | session-032510 |
| pairaction | mt8-licensed-native | r3w3 | 21 | indicative | 13.062 | 5.467 | trend: control compute moved 10.8% within the block | 0 | session-033531 |
| pairaction | mt8-licensed-native | r3w3strict | 21 | indicative | 13.756 | 5.474 | trend: ifx-expert-inputonly first-half vs second-half median moved 5.8%; trend: control compute moved 8.8% within the block; trend: control memory moved 8.0% within the block | 0 | session-034831 |
| pairaction | serial-repro-native | r3w2bin | 11 | indicative | 12.871 | 5.218 | trend: blade-llvm first-half vs second-half median moved 5.6%; trend: blade-cpp first-half vs second-half median moved 5.7%; trend: gfortran-natural-inputonly first-half vs second-half median moved 5.1%; trend: ifx-natural-inputonly first-half vs second-half median moved 5.1% | 0 | session-032510 |
| pairaction | serial-repro-native | r3w3 | 21 | indicative | 12.941 | 5.404 | trend: control compute moved 10.2% within the block | 0 | session-033531 |
| pairaction | serial-repro-native | r3w3strict | 21 | live | 13.006 | 5.329 | - | 0 | session-034831 |
| pairaction | mt8-repro-native | r3w2bin | 21 | indicative | 12.788 | 5.269 | trend: gfortran-natural first-half vs second-half median moved 7.8% | 0 | session-032510 |
| pairaction | mt8-repro-native | r3w3 | 21 | indicative | 13.553 | 5.366 | trend: control compute moved 5.7% within the block | 0 | session-033531 |
| pairaction | mt8-repro-native | r3w3strict | 21 | indicative | 12.866 | 5.330 | trend: blade-cpp-inputonly first-half vs second-half median moved 5.6% | 0 | session-034831 |
| pairkernel | mt8-repro-native | r3w2bin | 21 | live | 12.715 | 5.318 | - | 0 | session-040014 |
| pairkernel | mt8-repro-native | r3w3 | 21 | indicative | 12.754 | 5.268 | trend: gfortran-natural first-half vs second-half median moved 7.8% | 0 | session-040229 |
| saxpy | serial-licensed-native | r3w1 | 21 | live | 12.702 | 5.236 | - | 0 | session-040444 |
| saxpy | serial-licensed-native | r3w3 | 21 | indicative | 13.204 | 5.484 | trend: blade-llvm first-half vs second-half median moved 15.0%; trend: blade-cpp first-half vs second-half median moved 10.2%; trend: gfortran-natural first-half vs second-half median moved 9.4%; trend: gfortran-expert first-half vs second-half median moved 14.3%; trend: ifx-natural first-half vs second-half median moved 10.4%; trend: ifx-expert first-half vs second-half median moved 23.4%; trend: blade-llvm-inputonly first-half vs second-half median moved 10.6%; trend: blade-cpp-inputonly first-half vs second-half median moved 5.1%; trend: gfortran-natural-inputonly first-half vs second-half median moved 5.5%; trend: gfortran-expert-inputonly first-half vs second-half median moved 7.8%; trend: ifx-natural-inputonly first-half vs second-half median moved 8.0%; trend: ifx-expert-inputonly first-half vs second-half median moved 5.3%; trend: control compute moved 12.7% within the block; trend: control memory moved 10.3% within the block | 0 | session-041724 |
| saxpy | serial-licensed-native | r3w3legs1 | 21 | indicative | 13.132 | 5.385 | trend: ifx-expert first-half vs second-half median moved 6.2%; trend: ifx-expert-inputonly first-half vs second-half median moved 9.4% | 0 | session-043007 |
| saxpy | mt8-licensed-native | r3w1 | 21 | indicative | 12.835 | 5.256 | trend: blade-cpp first-half vs second-half median moved 9.0%; trend: gfortran-natural first-half vs second-half median moved 9.3%; trend: ifx-natural first-half vs second-half median moved 5.8%; trend: blade-cpp-inputonly first-half vs second-half median moved 5.2%; trend: gfortran-natural-inputonly first-half vs second-half median moved 7.3%; trend: ifx-natural-inputonly first-half vs second-half median moved 8.0%; trend: ifx-expert-inputonly first-half vs second-half median moved 7.9% | 0 | session-040444 |
| saxpy | mt8-licensed-native | r3w3 | 21 | indicative | 12.785 | 5.479 | trend: ifx-natural first-half vs second-half median moved 5.6%; trend: blade-cpp-inputonly first-half vs second-half median moved 7.9%; trend: ifx-expert-inputonly first-half vs second-half median moved 8.0% | 0 | session-041724 |
| saxpy | mt8-licensed-native | r3w3legs1 | 21 | live | 12.757 | 5.302 | - | 0 | session-043007 |
| saxpy | serial-repro-native | r3w1 | 21 | live | 13.568 | 5.349 | - | 0 | session-040444 |
| saxpy | serial-repro-native | r3w3 | 21 | indicative | 13.651 | 5.503 | trend: blade-llvm first-half vs second-half median moved 8.3%; trend: blade-cpp first-half vs second-half median moved 5.8%; trend: gfortran-natural first-half vs second-half median moved 5.5%; trend: ifx-expert first-half vs second-half median moved 7.3%; trend: blade-llvm-inputonly first-half vs second-half median moved 10.5%; trend: ifx-natural-inputonly first-half vs second-half median moved 6.5%; trend: control compute moved 9.1% within the block | 0 | session-041724 |
| saxpy | serial-repro-native | r3w3legs1 | 21 | live | 12.816 | 5.322 | - | 0 | session-043007 |
| saxpy | mt8-repro-native | r3w1 | 21 | indicative | 12.977 | 5.423 | trend: gfortran-expert first-half vs second-half median moved 5.1% | 0 | session-040444 |
| saxpy | mt8-repro-native | r3w3 | 21 | live | 13.232 | 5.349 | - | 0 | session-041724 |
| saxpy | mt8-repro-native | r3w3legs1 | 21 | live | 12.806 | 5.321 | - | 0 | session-043007 |
| comoment3 | mt8-repro-native | r3w2bin | 21 | live | 13.013 | 5.257 | - | 0 | session-044249 |
| comoment3 | mt8-repro-native | r3w3 | 21 | indicative | 13.443 | 5.588 | trend: control compute moved 7.3% within the block | 0 | session-044511 |
| gram | serial-licensed-native | r3w2bin | 11 | indicative | 12.726 | 5.248 | trend: control memory moved 7.3% within the block | 0 | session-044735 |
| gram | serial-licensed-native | r3w3 | 11 | live | 13.073 | 5.258 | - | 0 | session-044917 |

## 5. r3w3 slower than r3w2bin beyond the CI (Blade arms)

none

## 6. Missing cells

none

## 7. Sessions

| kernel | mirror | bundle | started | aborted | duration (s) | failures | flags |
|---|---|---|---|---|---|---|---|
| saxpy | r3w1 | C:\bt_r2ab\r3w1\bench\results\2026-10-24-LAPTOP-350H6LDB\session-040444 | 2026-10-24T04:04:44 | None | 749 | none | saxpy/mt8-licensed-native: inference suppressed: trend: blade-cpp first-half vs second-half median moved 9.0%; trend: gfortran-natural first-half vs second-half median moved 9.3%; trend: ifx-natural first-half vs second-half median moved 5.8%; trend: blade-cpp-inputonly first-half vs second-half median moved 5.2%; trend: gfortran-natural-inputonly first-half vs second-half median moved 7.3%; trend: ifx-natural-inputonly first-half vs second-half median moved 8.0%; trend: ifx-expert-inputonly first-half vs second-half median moved 7.9%; saxpy/mt8-repro-native: inference suppressed: trend: gfortran-expert first-half vs second-half median moved 5.1% |
| groupagg | r3w2bin | C:\bt_r2ab\r3w2bin\bench\results\2026-10-24-LAPTOP-350H6LDB\session-021805 | 2026-10-24T02:18:05 | None | 240 | none | none |
| groupagg | r3w2bin | C:\bt_r2ab\r3w2bin\bench\results\2026-10-24-LAPTOP-350H6LDB\session-023041 | 2026-10-24T02:30:41 | None | 242 | none | none |
| groupagg_dyn | r3w2bin | C:\bt_r2ab\r3w2bin\bench\results\2026-10-24-LAPTOP-350H6LDB\session-024318 | 2026-10-24T02:43:18 | None | 151 | none | none |
| groupagg_dyn | r3w2bin | C:\bt_r2ab\r3w2bin\bench\results\2026-10-24-LAPTOP-350H6LDB\session-025433 | 2026-10-24T02:54:33 | None | 156 | none | groupagg_dyn/serial-licensed-native: inference suppressed: trend: ifx-natural first-half vs second-half median moved 16.3% |
| hashjoin | r3w2bin | C:\bt_r2ab\r3w2bin\bench\results\2026-10-24-LAPTOP-350H6LDB\session-030554 | 2026-10-24T03:05:54 | None | 374 | none | hashjoin/serial-repro-native: inference suppressed: trend: blade-cpp first-half vs second-half median moved 6.0%; control compute median 0.01484s moved +17.1% vs the session baseline 0.01267s; control memory median 0.00579s moved +11.6% vs the session baseline 0.005189s |
| pairaction | r3w2bin | C:\bt_r2ab\r3w2bin\bench\results\2026-10-24-LAPTOP-350H6LDB\session-032510 | 2026-10-24T03:25:10 | None | 610 | none | pairaction/serial-licensed-native: inference suppressed: trend: ifx-natural-inputonly first-half vs second-half median moved 8.4%; pairaction/mt8-licensed-native: inference suppressed: trend: control compute moved 5.9% within the block; pairaction/serial-repro-native: inference suppressed: trend: blade-llvm first-half vs second-half median moved 5.6%; trend: blade-cpp first-half vs second-half median moved 5.7%; trend: gfortran-natural-inputonly first-half vs second-half median moved 5.1%; trend: ifx-natural-inputonly first-half vs second-half median moved 5.1%; pairaction/mt8-repro-native: inference suppressed: trend: gfortran-natural first-half vs second-half median moved 7.8% |
| pairkernel | r3w2bin | C:\bt_r2ab\r3w2bin\bench\results\2026-10-24-LAPTOP-350H6LDB\session-040014 | 2026-10-24T04:00:14 | None | 124 | none | none |
| comoment3 | r3w2bin | C:\bt_r2ab\r3w2bin\bench\results\2026-10-24-LAPTOP-350H6LDB\session-044249 | 2026-10-24T04:42:49 | None | 132 | none | none |
| gram | r3w2bin | C:\bt_r2ab\r3w2bin\bench\results\2026-10-24-LAPTOP-350H6LDB\session-044735 | 2026-10-24T04:47:35 | None | 92 | none | gram/serial-licensed-native: inference suppressed: trend: control memory moved 7.3% within the block |
| groupagg | r3w3 | C:\bt_r2ab\r3w3\bench\results\2026-10-24-LAPTOP-350H6LDB\session-022217 | 2026-10-24T02:22:17 | None | 240 | none | none |
| groupagg | r3w3 | C:\bt_r2ab\r3w3\bench\results\2026-10-24-LAPTOP-350H6LDB\session-023453 | 2026-10-24T02:34:53 | None | 244 | none | groupagg/serial-licensed-native: inference suppressed: trend: gfortran-natural first-half vs second-half median moved 8.3% |
| groupagg_dyn | r3w3 | C:\bt_r2ab\r3w3\bench\results\2026-10-24-LAPTOP-350H6LDB\session-024559 | 2026-10-24T02:45:59 | None | 244 | none | none |
| groupagg_dyn | r3w3 | C:\bt_r2ab\r3w3\bench\results\2026-10-24-LAPTOP-350H6LDB\session-025720 | 2026-10-24T02:57:20 | None | 248 | none | groupagg_dyn/serial-licensed-native: inference suppressed: trend: gfortran-natural first-half vs second-half median moved 13.1%; trend: ifx-natural first-half vs second-half median moved 15.8%; trend: control memory moved 5.8% within the block |
| hashjoin | r3w3 | C:\bt_r2ab\r3w3\bench\results\2026-10-24-LAPTOP-350H6LDB\session-031218 | 2026-10-24T03:12:18 | None | 375 | none | hashjoin/serial-licensed-native: inference suppressed: trend: gfortran-natural-inputonly first-half vs second-half median moved 7.2%; trend: control compute moved 8.9% within the block |
| pairaction | r3w3 | C:\bt_r2ab\r3w3\bench\results\2026-10-24-LAPTOP-350H6LDB\session-033531 | 2026-10-24T03:35:31 | None | 770 | none | pairaction/mt8-licensed-native: inference suppressed: trend: control compute moved 10.8% within the block; pairaction/serial-repro-native: inference suppressed: trend: control compute moved 10.2% within the block; pairaction/mt8-repro-native: inference suppressed: trend: control compute moved 5.7% within the block |
| pairkernel | r3w3 | C:\bt_r2ab\r3w3\bench\results\2026-10-24-LAPTOP-350H6LDB\session-040229 | 2026-10-24T04:02:29 | None | 125 | none | pairkernel/mt8-repro-native: inference suppressed: trend: gfortran-natural first-half vs second-half median moved 7.8% |
| saxpy | r3w3 | C:\bt_r2ab\r3w3\bench\results\2026-10-24-LAPTOP-350H6LDB\session-041724 | 2026-10-24T04:17:24 | None | 753 | none | saxpy/serial-licensed-native: inference suppressed: trend: blade-llvm first-half vs second-half median moved 15.0%; trend: blade-cpp first-half vs second-half median moved 10.2%; trend: gfortran-natural first-half vs second-half median moved 9.4%; trend: gfortran-expert first-half vs second-half median moved 14.3%; trend: ifx-natural first-half vs second-half median moved 10.4%; trend: ifx-expert first-half vs second-half median moved 23.4%; trend: blade-llvm-inputonly first-half vs second-half median moved 10.6%; trend: blade-cpp-inputonly first-half vs second-half median moved 5.1%; trend: gfortran-natural-inputonly first-half vs second-half median moved 5.5%; trend: gfortran-expert-inputonly first-half vs second-half median moved 7.8%; trend: ifx-natural-inputonly first-half vs second-half median moved 8.0%; trend: ifx-expert-inputonly first-half vs second-half median moved 5.3%; trend: control compute moved 12.7% within the block; trend: control memory moved 10.3% within the block; saxpy/mt8-licensed-native: inference suppressed: trend: ifx-natural first-half vs second-half median moved 5.6%; trend: blade-cpp-inputonly first-half vs second-half median moved 7.9%; trend: ifx-expert-inputonly first-half vs second-half median moved 8.0%; saxpy/serial-repro-native: inference suppressed: trend: blade-llvm first-half vs second-half median moved 8.3%; trend: blade-cpp first-half vs second-half median moved 5.8%; trend: gfortran-natural first-half vs second-half median moved 5.5%; trend: ifx-expert first-half vs second-half median moved 7.3%; trend: blade-llvm-inputonly first-half vs second-half median moved 10.5%; trend: ifx-natural-inputonly first-half vs second-half median moved 6.5%; trend: control compute moved 9.1% within the block |
| comoment3 | r3w3 | C:\bt_r2ab\r3w3\bench\results\2026-10-24-LAPTOP-350H6LDB\session-044511 | 2026-10-24T04:45:11 | None | 133 | none | comoment3/mt8-repro-native: inference suppressed: trend: control compute moved 7.3% within the block |
| gram | r3w3 | C:\bt_r2ab\r3w3\bench\results\2026-10-24-LAPTOP-350H6LDB\session-044917 | 2026-10-24T04:49:17 | None | 91 | none | none |
| groupagg | r3w3t3off | C:\bt_r2ab\r3w3t3off\bench\results\2026-10-24-LAPTOP-350H6LDB\session-022628 | 2026-10-24T02:26:28 | None | 242 | none | groupagg/serial-repro-native: inference suppressed: trend: blade-cpp first-half vs second-half median moved 7.0% |
| groupagg_dyn | r3w3t3off | C:\bt_r2ab\r3w3t3off\bench\results\2026-10-24-LAPTOP-350H6LDB\session-025014 | 2026-10-24T02:50:14 | None | 248 | none | groupagg_dyn/serial-repro-native: inference suppressed: trend: gfortran-natural first-half vs second-half median moved 5.6%; trend: ifx-natural first-half vs second-half median moved 9.9% |
| groupagg | r3w3t3lic | C:\bt_r2ab\r3w3t3lic\bench\results\2026-10-24-LAPTOP-350H6LDB\session-023907 | 2026-10-24T02:39:07 | None | 240 | none | none |
| groupagg_dyn | r3w3t3lic | C:\bt_r2ab\r3w3t3lic\bench\results\2026-10-24-LAPTOP-350H6LDB\session-030138 | 2026-10-24T03:01:38 | None | 245 | none | groupagg_dyn/serial-licensed-native: inference suppressed: trend: ifx-natural first-half vs second-half median moved 6.8% |
| hashjoin | r3w3t2off | C:\bt_r2ab\r3w3t2off\bench\results\2026-10-24-LAPTOP-350H6LDB\session-031843 | 2026-10-24T03:18:43 | None | 375 | none | hashjoin/serial-licensed-native: inference suppressed: trend: blade-cpp first-half vs second-half median moved 11.7% |
| pairaction | r3w3strict | C:\bt_r2ab\r3w3strict\bench\results\2026-10-24-LAPTOP-350H6LDB\session-034831 | 2026-10-24T03:48:31 | None | 692 | none | pairaction/serial-licensed-native: inference suppressed: trend: ifx-natural first-half vs second-half median moved 5.2%; trend: ifx-natural-inputonly first-half vs second-half median moved 12.9%; trend: control memory moved 14.3% within the block; pairaction/mt8-licensed-native: inference suppressed: trend: ifx-expert-inputonly first-half vs second-half median moved 5.8%; trend: control compute moved 8.8% within the block; trend: control memory moved 8.0% within the block; pairaction/mt8-repro-native: inference suppressed: trend: blade-cpp-inputonly first-half vs second-half median moved 5.6% |
| saxpy | r3w3legs1 | C:\bt_r2ab\r3w3legs1\bench\results\2026-10-24-LAPTOP-350H6LDB\session-043007 | 2026-10-24T04:30:07 | None | 751 | none | saxpy/serial-licensed-native: inference suppressed: trend: ifx-expert first-half vs second-half median moved 6.2%; trend: ifx-expert-inputonly first-half vs second-half median moved 9.4% |

## 8. Peak working set per block (max over samples, MB)

| kernel | regime | mirror | class | blade-llvm | blade-cpp | gfortran-natural | gfortran-expert | ifx-natural | ifx-expert |
|---|---|---|---|---|---|---|---|---|---|
| groupagg | serial-repro-native | r3w2bin | live | 35.1 | 35.5 | 20.3 | 20.3 | 19.9 | 19.8 |
| groupagg | serial-repro-native | r3w3 | live | 19.8 | 20.6 | 20.3 | 20.3 | 19.9 | 19.8 |
| groupagg | serial-repro-native | r3w3t3off | indicative | 35.1 | 35.5 | 20.3 | 20.3 | 19.9 | 19.8 |
| groupagg | serial-licensed-native | r3w2bin | live | 35.1 | 35.5 | 20.3 | 20.3 | 19.9 | 19.8 |
| groupagg | serial-licensed-native | r3w3 | indicative | 35.1 | 35.5 | 20.3 | 20.3 | 19.9 | 19.8 |
| groupagg | serial-licensed-native | r3w3t3lic | live | 19.9 | 20.6 | 20.3 | 20.3 | 19.9 | 19.8 |
| groupagg_dyn | serial-repro-native | r3w2bin | live | 35.1 | 35.6 | 20.3 | 20.3 | 19.9 | 19.9 |
| groupagg_dyn | serial-repro-native | r3w3 | live | 19.9 | 20.6 | 20.3 | 20.3 | 19.9 | 19.9 |
| groupagg_dyn | serial-repro-native | r3w3t3off | indicative | 35.1 | 35.6 | 20.3 | 20.3 | 19.9 | 19.9 |
| groupagg_dyn | serial-licensed-native | r3w2bin | indicative | 35.1 | 35.6 | 20.3 | 20.3 | 19.9 | 19.9 |
| groupagg_dyn | serial-licensed-native | r3w3 | indicative | 35.1 | 35.6 | 20.3 | 20.3 | 19.9 | 19.9 |
| groupagg_dyn | serial-licensed-native | r3w3t3lic | indicative | 19.9 | 20.6 | 20.3 | 20.3 | 19.9 | 19.9 |
| hashjoin | serial-licensed-native | r3w2bin | live | n/a | 25.4 | 5.2 | 5.2 | 5.0 | 5.0 |
| hashjoin | serial-licensed-native | r3w3 | indicative | n/a | 5.4 | 5.2 | 5.2 | 4.9 | 5.0 |
| hashjoin | serial-licensed-native | r3w3t2off | indicative | n/a | 25.4 | 5.2 | 5.2 | 4.9 | 5.0 |
| hashjoin | serial-repro-native | r3w2bin | indicative | n/a | 25.4 | 5.2 | 5.2 | 5.0 | 5.0 |
| hashjoin | serial-repro-native | r3w3 | live | n/a | 5.4 | 5.2 | 5.2 | 4.9 | 4.9 |
| hashjoin | serial-repro-native | r3w3t2off | live | n/a | 25.5 | 5.2 | 5.2 | 4.9 | 4.9 |
| pairaction | serial-licensed-native | r3w2bin | indicative | 28.7 | 29.5 | 53.1 | 29.2 | 52.6 | 28.7 |
| pairaction | serial-licensed-native | r3w3 | live | 28.7 | 29.5 | 53.1 | 29.2 | 52.6 | 28.7 |
| pairaction | serial-licensed-native | r3w3strict | indicative | 28.7 | 29.5 | 53.1 | 29.2 | 52.6 | 28.7 |
| pairaction | mt8-licensed-native | r3w2bin | indicative | n/a | 30.0 | 53.5 | 29.6 | 55.0 | 31.2 |
| pairaction | mt8-licensed-native | r3w3 | indicative | n/a | 30.0 | 53.5 | 29.7 | 55.0 | 31.1 |
| pairaction | mt8-licensed-native | r3w3strict | indicative | n/a | 30.0 | 53.5 | 29.7 | 55.0 | 31.1 |
| pairaction | serial-repro-native | r3w2bin | indicative | 28.7 | 29.5 | 53.1 | 29.2 | 52.6 | 28.7 |
| pairaction | serial-repro-native | r3w3 | indicative | 28.7 | 29.5 | 53.1 | 29.2 | 52.6 | 28.7 |
| pairaction | serial-repro-native | r3w3strict | live | 28.7 | 29.5 | 53.1 | 29.2 | 52.6 | 28.7 |
| pairaction | mt8-repro-native | r3w2bin | indicative | n/a | 30.0 | 53.5 | 29.7 | 55.0 | 31.1 |
| pairaction | mt8-repro-native | r3w3 | indicative | n/a | 30.0 | 53.5 | 29.6 | 55.0 | 31.2 |
| pairaction | mt8-repro-native | r3w3strict | indicative | n/a | 30.0 | 53.5 | 29.6 | 55.0 | 31.1 |
| pairkernel | mt8-repro-native | r3w2bin | live | n/a | 29.8 | 53.3 | 29.4 | 54.8 | 30.9 |
| pairkernel | mt8-repro-native | r3w3 | indicative | n/a | 29.8 | 53.3 | 29.4 | 54.8 | 30.9 |
| saxpy | serial-licensed-native | r3w1 | live | 50.3 | 50.7 | 50.8 | 50.8 | 50.4 | 50.4 |
| saxpy | serial-licensed-native | r3w3 | indicative | 50.3 | 50.7 | 50.8 | 50.8 | 50.4 | 50.4 |
| saxpy | serial-licensed-native | r3w3legs1 | indicative | 49.8 | 50.7 | 50.8 | 50.8 | 50.4 | 50.4 |
| saxpy | mt8-licensed-native | r3w1 | indicative | n/a | 51.3 | 51.2 | 51.2 | 52.9 | 52.9 |
| saxpy | mt8-licensed-native | r3w3 | indicative | n/a | 51.3 | 51.2 | 51.2 | 52.9 | 52.9 |
| saxpy | mt8-licensed-native | r3w3legs1 | live | n/a | 51.3 | 51.2 | 51.2 | 52.9 | 52.9 |
| saxpy | serial-repro-native | r3w1 | live | 50.3 | 50.7 | 50.8 | 50.8 | 50.4 | 50.4 |
| saxpy | serial-repro-native | r3w3 | indicative | 50.3 | 50.7 | 50.8 | 50.8 | 50.4 | 50.4 |
| saxpy | serial-repro-native | r3w3legs1 | live | 49.8 | 50.7 | 50.8 | 50.8 | 50.4 | 50.4 |
| saxpy | mt8-repro-native | r3w1 | indicative | n/a | 51.3 | 51.2 | 51.2 | 52.9 | 52.9 |
| saxpy | mt8-repro-native | r3w3 | live | n/a | 51.3 | 51.3 | 51.2 | 52.9 | 52.9 |
| saxpy | mt8-repro-native | r3w3legs1 | live | n/a | 51.3 | 51.3 | 51.3 | 52.9 | 52.9 |
| comoment3 | mt8-repro-native | r3w2bin | live | n/a | 13.3 | 43.9 | 14.5 | 45.4 | 15.7 |
| comoment3 | mt8-repro-native | r3w3 | indicative | n/a | 13.2 | 43.9 | 14.5 | 45.4 | 15.6 |
| gram | serial-licensed-native | r3w2bin | indicative | 30.8 | 31.4 | 36.8 | 51.6 | 55.9 | 51.2 |
| gram | serial-licensed-native | r3w3 | live | 30.8 | 31.4 | 36.8 | 51.6 | 55.9 | 51.2 |