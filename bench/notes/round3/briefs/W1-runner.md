# W1-1 + W1-6 -- runner tasks (measurement only, no code)

You are the round-3 test runner. Box rules: one timing process at a time; before any timing, audit `Get-Process` for stray
work (a backgrounded `find /` once burned a core beside an A/B); never time while a build or test runs; ucrt64 first on
PATH; `OPENBLAS_DIR=C:\Users\cdupu\tools\OpenBLAS-0.3.34-x64`. Harness: `bench/harness/run.py` needs one MIRROR ROOT per
binary / config (`C:\bt_r2ab\<name>\{bench, bin\Release\net10.0}`, no .git, no blade.toolchain.json); knob arms are a
mirror whose `bench/harness/config.json` `blade_knobs.all` carries the knob (the harness strips inherited BLADE_* env);
smoke evidence is keyed per kernel|regime|arm on (source, Blade.dll, config) hashes -> smoke only the pairs you bench.
Scripts to adapt: `bench/notes/round2/w2-scripts/{mirrors.ps1, smoke.ps1, driver.ps1, analyze.py}` (PowerShell `-File`
passes a list as ONE string -- call with `-Command "& 'script' -Mirrors a,b,c"`; `$K` and `$k` are the same variable).

## W1-1 -- A1 experiment: the ordered scatter instead of the G35 row slots under mt8 repro (zero code)

Binary: the integration tip (the orchestrator names it). Mirrors: `r3a-def` (default config) and `r3a-noslots`
(`blade_knobs.all = { "BLADE_FOLD_ROW_SLOTS": "0" }`; BLADE_ORBIT_SCATTER stays on). Cells: pairkernel and comoment3,
`mt8-repro-native` (and `serial-repro-native` as the control that both mirrors must agree on). Before timing, verify from
the smoke bundles' `plan_fold_form` evidence that the default takes `threaded complete rows` and the knob-off mirror takes
`ordered orbit scatter` (G35 declined by its knob), and that the producer form is unchanged; compare the two arms' printed
checksums bit for bit (they must be identical: both are strict forms). Interleave def / noslots / def / noslots, 21 rounds,
and report per cell: medians, NEW/OLD ratio with a bootstrap CI (analyze.py), expert ratios, block class, and -- from the
run records if the harness exposes phases -- the consumer phase separately. Verdict per kernel: scatter wins / row slots
win / within noise (review A/2: judge each kernel independently; do not adopt a threshold from two shapes).

## W1-6 -- anti_g quiet re-measure

Mirrors: `r3a-def` (anti_g default 1) and `r3a-antig0` (`blade_knobs.all = { "BLADE_TILE_TUNE": "anti_g=0" }`). Cells:
antisym `serial-licensed-native` and `mt8-licensed-native`, THREE interleaved sessions per mirror per regime (the signal
is 1-5%). Report medians, ratios with CIs, and whether any block is live; the default flips only if auto wins beyond the CI
on LIVE blocks in both regimes (round 2: auto 0.954 [0.904, 0.996] mt8 indicative; wave 3: auto 1.3-1.4% slower serially,
live).

Write `scratchpad/round3/gates/w1-runner-report.md` with the tables, the bundle paths, the Blade.dll hash, the box state,
and the two verdicts.
