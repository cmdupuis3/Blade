# Post-merge W2-F sanity gates (integration worktree bench-fortran @ ea73698f)

Preconditions: git status clean; HEAD ea73698f (merge of W2-F's branch at 29cb3506 = its merge d63293e6 of d332f700 + fixes 9-11);
build 0 errors (a first build printed 2 warnings that an immediate incremental rebuild did not reproduce: 0 warnings, same hash);
Blade.dll SHA256 7DE2D6038C682DC0A4D896F4AB3FA6DA45E3542F8F9B5243BAD346B8070E818B, re-hashed after the run: unchanged.
ucrt64 g++ first on PATH, OPENBLAS_DIR set. Runner: gates\pm3\run.ps1 (one block at a time, from the worktree root);
consoles gates\pm3\<key>.log (UTF-16). All exit 0; footers:

| key | footer | vs W2-A post-merge (12ec4734) |
|---|---|---|
| optimize | Optimization Layer: 257 passed, 0 failed | = |
| orbit-scatter | Orbit Scatter: 17 passed, 0 failed, 0 skipped | = |
| omp-pragma | OpenMP Pragma: 121 passed, 0 failed | 109 (+12: G41 provenance cases) |
| omp-reduce | OpenMP Reduce: 121 passed, 0 failed | 117 (+4: g41 runtime cases; `row_slots_declined_outer_kernel_write` now also forbids `blade_scatter::`) |
| omp-coverage | OpenMP Coverage: 10 passed, 0 error(s), 0 warning(s) | = |
| mutability | Verdict 17 passed, 0 failed | new to this list |
| mutability-errors | Verdict 24 passed, 0 failed (014-024 join refusals; the `IR Error` lines are the pinned refusals) | new to this list |
| tuples | Verdict 33 passed, 0 failed | new (031-033 G42) |
| tuple-views | Verdict 5 passed, 0 failed | new |
| stack-join | Verdict 14 passed, 0 failed | new |
| functions | Verdict 324 passed, 0 failed | new |
| diagnostics | Core 37 / Corpus 103 / Equiv Certificate Suggestions 108, 0 failures | new |
| llvm goldens | Compare Rules 23, Emission Pins 3, Fact Layer 64, Simplex Blocks Agreement 19; 0 failed | new |
| recursive-arrays | Verdict 29 passed, 0 failed | new |
| linalg | LinAlg Probe all passed; Syrk Tile 4; Row Contraction Tiles 24; BLAS Tier Resolution 19; 0 failed | = |
| syrk-tile | Syrk Tile: 4 passed, 0 failed | = |
| row-tile | Row Contraction Tiles: 24 passed, 0 failed | = |
| llvm tiles | LLVM Tiles Raw-Bit: 12 passed, 0 failed, 0 skipped | = |
| llvm facts | LLVM Fact Layer: 64 passed, 0 failed, 0 skipped | 60 (+4) |
| loops | Verdict 259 passed, 0 failed, 0 skipped | 253 (+6: loops/492-498 minus the moved 489) |
| sibling-folds | Verdict 18 passed, 0 failed, 0 skipped | = |
| symmetry | Verdict 137 passed, 0 failed, 0 skipped | = |
| index-types | Verdict 380 passed, 0 failed, 0 skipped | = |
| cell-epilogue | Cell Epilogue: 56 passed, 0 failed, 0 skipped | = |
| fold-lanes | Fold Lanes: 4 passed, 0 failed | = |
| cli | CLI Smoke: 132 passed, 0 failed | = |
| corpus-wiring | Corpus Wiring: 14 passed, 0 failed | = |

## Bench harness
- `run.py check onepass` -> exit 0, 0 failure(s), 0 flag(s).
- `run.py check pairkernel --regimes serial-repro-native,mt8-repro-native` -> exit 0, 0 failure(s), 0 flag(s).
- `run.py check comoment3 --regimes serial-repro-native` -> exit 0, 0 failure(s), 0 flag(s).

Failures: none. git status after the runs: clean.

## Codex re-re-review (codex-diff-W2F-3.md, read-only, on 29cb3506 + the incoming 906659e1)
VERDICT DO NOT MERGE -- not for a regression but because the every-fold-entry audit it demanded turned up four
PRE-EXISTING holes of the same class (a writing kernel / producer under a reordering or rescheduling form):
12 HIGH a named partial array view (`let row = m((0 : I))`) hides its deferred producer from the write analysis
(LLVM keeps the view virtual); 13 BLOCKER the LLVM orbit-class fold runs its canonical-pool pass even when
`FoldOperandWrites` forbids reassociation (evaluates 3 canonical cells for 4 logical reads of a writing map);
14 BLOCKER hot-row scheduling runs the row consumer after each producer row without checking the producer's
writes (a consumer reading a counter the producer bumps: 12 two-pass vs 9 hot-row; fails under repro too);
15 HIGH the flat-epilogue / flat materializer emits `BLADE_IVDEP` on an unannotated writing producer (a real
loop-carried dependence asserted away). Findings 9 and merge requirements (a), (c)-(f), pass order and hunk
preservation CLOSED; 10 and (b) NOT CLOSED only through 12; 11 NOT CLOSED through 13-15. The incoming scatter
needs no `FoldOperandWrites` read (strict stored pool, no licence consumed, its own closed purity audit).
Decision: the merge stays (it fixes real wrong answers and regresses nothing; every block green); findings
12-15 go back to W2-F as one more fix commit on its branch, merged on top before the wave-2 gates; G41's
"every entry" wording is honest only after that commit.
