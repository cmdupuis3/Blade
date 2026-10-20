# Post-merge W1-C sanity (integration worktree bench-fortran @ 46007b87)

Setup: status --short empty; `git log --oneline -1` = 46007b87; Blade.dll SHA256 = 5245E0095A91EADC4D9BF02E227E4E4A2BB42C358969146060CBE24B4E50A7C2. ucrt64 + OpenBLAS (OPENBLAS_DIR set) on PATH. No rebuild.
Commands: `bin\Release\net10.0\Blade.exe test <key>` one at a time (sequential script), logs in scratchpad\round2\gates\pm\<key>.log.

| key | footer | exit |
|---|---|---|
| optimize | Optimization Layer: 213 passed, 0 failed | 0 |
| cell-epilogue | Cell Epilogue: 56 passed, 0 failed, 0 skipped | 0 |
| fold-lanes | Fold Lanes: 4 passed, 0 failed | 0 |
| index-types | Verdict: 380 passed, 0 failed, 0 skipped | 0 |
| symmetry | Verdict: 131 passed, 0 failed, 0 skipped | 0 |
| loops | Verdict: 247 passed, 0 failed, 0 skipped | 0 |
| recursive-arrays | Verdict: 29 passed, 0 failed, 0 skipped | 0 |
| linalg | LinAlg Dispatch 153/0; Probe 21/21; Syrk Tile 4/0; Row Contraction Tiles 19/0; BLAS Tier Resolution 19/0 | 0 |
| cli | CLI Smoke: 126 passed, 0 failed | 0 |
| corpus-wiring | Corpus Wiring: 14 passed, 0 failed | 0 |
| omp-pragma | OpenMP Pragma: 101 passed, 1 FAILED | 1 |
| omp-reduce | OpenMP Reduce: 93 passed, 0 failed | 0 |
| omp-coverage | OpenMP Coverage: 8 passed, 0 error(s), 0 warning(s) | 0 |

(Blocks whose footer prints no skipped count (optimize, fold-lanes, linalg, cli, corpus-wiring, omp-*) report none.)

## Failure: omp-pragma / row_slots_not_in_default_build (tests/OmpTests.fs:961-966)
Pin wants `fold-form` Applied with evidence starting `fold form: orbit-class fold` for rowSlotSym2 521 rowSlotOmpOuter "g(i, j) * g(i, j)" in the DEFAULT (licensed) build. First diagnostic line:
`wanted orbit-class fold applied (no row slots); the record: [fold-form v1] g @ 4:1: applied [fold form: simd K-lanes(8) dot; prodsum] | [fold-form v1] s @ 5:1: applied [fold form: compact row classes (rank 2, 2 classes); the orbit-class sums of `g` (rank 2, extent 521) taken per packed row behind the producer, combined in row order by additions only (licensed regrouping); no pass over the pool] | [omp v1] s @ 5:1: declined -- compact row classes: ... the row combine is a serial pass over the row partials [[omp] requested but emitted serial] ...`
Diagnosis: W1-C's new "compact row classes" form now wins over the orbit-class fold in the default build, so the old pin is stale (no row slots are used; `__rslot` absence presumably still holds). Likely needs the pin updated to accept `fold form: compact row classes` (a W1-C test-update omission), not a miscompile. All 12 other omp-pragma-adjacent/ listed gates are green.
