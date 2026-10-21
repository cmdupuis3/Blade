# W2-0 -- two measured decisions from the wave-1 A/B: the scatter wins the mt8-repro precedence; anti_g per regime

Read `BUILDER-PREAMBLE.md` first (it names the integration tip; reset your worktree to it). Evidence: the wave-1 harness A/B,
`bench/notes/round3/w1-ab.md` (committed on the integration branch) -- read its section 0 and the pairkernel / comoment3 /
antisym rows.

## W2-0a: the ordered orbit scatter takes precedence over the G35 row slots when both admit (mt8 repro)

Today `scatterG35Precedence` (src/CodeGenBinding.fs ~4465-4475; `rowSlotAdmission` first, the scatter only when the
row slots decline) lets the threaded complete-row form win under an omp request in a REPRO build. Measured with
`BLADE_FOLD_ROW_SLOTS=0` (the scatter serving the same walk) at full size, 21 rounds, interleaved: pairkernel mt8 repro
9.23 -> 7.76 ms (0.841 [0.811, 0.872]; expert ratio 0.686 / 0.653 -> 0.795 / 0.805), comoment3 4.43 -> 3.74 ms (0.843
[0.817, 0.885]; 1.107 / 0.945 -> 1.303 / 1.120); the serial controls agree across all three mirrors (both strict forms print
the same bits). Both blocks were drift-flagged (indicative) but the effect is large and consistent.

Change: when `OrbitFold.planStrict` admits the walk (a stored SymIdx<2/3> pool, every level strict, the closed purity
whitelist), the scatter is taken even though the row slots would admit -- the row slots remain the form for walks the
scatter does not admit (deferred producers, rank >= 4, AntisymIdx pools, coordinate-dependent terms, mixed nests). Make it
a POLICY knob so the A/B can be re-run either way: `BLADE_ORBIT_SCATTER` keeps its meaning (0 = off); add
`BLADE_FOLD_ROW_SLOTS=prefer` meaning "row slots win when both admit" (the old order) beside unset/1 (admit, scatter wins)
and 0 (off); register in `suiteClearedKnobs`; CLAUDE.md rows for both knobs; `blade plan` records
`fold form: ordered orbit scatter (repro; the threaded complete-row form admitted but yielded: measured slower, round 3)` and,
under `prefer`, the old record. Both forms must stay bitwise identical to the serial walk under REPRO + CONTRACT=off (the
existing omp-reduce identity cases; add the precedence case's inverse: `orbit_scatter_precedes_row_slots` with the needles
`blade_scatter::` present and `__rslot` absent, and `row_slots_prefer_knob` restoring the old emission). omp-coverage: the
census must say why the omp request was not spent on a team ("the ordered orbit scatter runs serially; the threaded
complete-row form admitted but yielded ..."). The LLVM lane is unchanged (it has no row slots).

## W2-0b: anti_g default per regime

Measured (three interleaved sessions per mirror, pooled n = 63): antisym serial licensed, auto (anti_g=0) vs default 1:
1.010 [0.994, 1.016] llvm, 1.020 [1.009, 1.029] cpp -- auto SLOWER serially; antisym mt8 licensed: 0.952 [0.929, 0.987] cpp --
auto FASTER under threads. Change the default of `anti_g` (src/CodeGenExprSupport.fs tile tuning / `Toolchain.tileTune`)
to "1 when the build emits no thread-level OpenMP (BLADE_OMP_THREADS=1/0/off, or the LLVM lane), auto (0) when it does";
an explicit `BLADE_TILE_TUNE anti_g=<G>` forces either; `blade plan` prints the resolved G and why; CLAUDE.md row updated;
`blade test row-tile` and `llvm tiles` stay byte-identical for every G (they do today); add a plan-record case for each
regime in the optimize or linalg block. No change to the kernel arithmetic.

## Tests / blocks

omp-reduce, omp-pragma, omp-coverage, orbit-scatter, symmetry, row-tile, syrk-tile, llvm tiles, linalg, optimize, cli,
corpus-wiring; interp symmetry / loops; opt-diff symmetry. `blade plan bench/kernels/pairkernel/pairkernel.blade` under
`BLADE_FP_REPRO=1` (threads on): the scatter record; `blade plan bench/kernels/antisym/antisym.blade` with and without
`BLADE_OMP_THREADS=1`: the resolved anti_g. One `blade run` A/B of pairkernel at full size, repro, T=8, old vs new binary
interleaved (directional). Gap register: a note on the G35 row and the G44 row (the measured precedence), a line on the
antisym row-group entry (G1's tuning note or wherever anti_g lives).
