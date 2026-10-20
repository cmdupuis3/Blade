# Post-merge W2-D + W2-E sanity gates (integration worktree bench-fortran @ d332f700)

Preconditions: git status clean; HEAD d332f700; Blade.dll SHA256 64E4DD2C...65F339 (matches). No rebuild. ucrt64 g++ first on PATH, OPENBLAS_DIR set.
Command per key: `bin\Release\net10.0\Blade.exe test <key>` (from worktree root, one at a time). All exit 0; footers below (logs: scratchpad\round2\gates\pm_<key>.log, UTF-16).

| key | footer | skipped |
|---|---|---|
| optimize | Optimization Layer: 246 passed, 0 failed | n/a (not printed) |
| omp-pragma | OpenMP Pragma: 103 passed, 0 failed | n/a |
| omp-reduce | OpenMP Reduce: 117 passed, 0 failed | n/a |
| omp-coverage | OpenMP Coverage: 10 passed, 0 error(s), 0 warning(s) | n/a |
| linalg | Row Contraction Tiles 24/0; BLAS Tier Resolution 19/0 (all PASS lines, no FAIL) | n/a |
| syrk-tile | Syrk Tile: 4 passed, 0 failed | n/a |
| row-tile | Row Contraction Tiles: 24 passed, 0 failed | n/a |
| llvm tiles | LLVM Tiles Raw-Bit: 12 passed, 0 failed, 0 skipped (syrk 110, antisym 2048, simplex 904 both lanes) | 0 |
| llvm facts | LLVM Fact Layer: 60 passed, 0 failed, 0 skipped | 0 |
| loops | Verdict 253 passed, 0 failed, 0 skipped | 0 |
| sibling-folds | Verdict 18 passed, 0 failed, 0 skipped | 0 |
| symmetry | Verdict 132 passed, 0 failed, 0 skipped | 0 |
| index-types | Verdict 380 passed, 0 failed, 0 skipped | 0 |
| cell-epilogue | Cell Epilogue: 56 passed, 0 failed, 0 skipped | 0 |
| fold-lanes | Fold Lanes: 4 passed, 0 failed | n/a |
| cli | CLI Smoke: 128 passed, 0 failed | n/a |
| corpus-wiring | Corpus Wiring: 14 passed, 0 failed | n/a |

All counts are within or match the expected ranges. (The "IR Error ... block-spec index mismatch" line in the index-types log is an intentional negative-test diagnostic, not a failure; verdict is 380/0.)

## Bench harness
1. `C:\Python314\python.exe bench\harness\run.py check onepass` -> exit 0, "0 failure(s), 0 flag(s)". NOTE: the default check only covers regime serial-licensed-native (smoke n=50021, pins-B.json): all 12 arms ok. It does NOT exercise the mt8 arm.
2. Extra (to cover the new mt8 arm): `... run.py check onepass --regimes mt8-licensed-native,mt8-repro-native` -> exit 0, "0 failure(s), 0 flag(s)". blade-cpp ok in both (licensed: openmp 3 constructs, reordered 2 SIMD_REDUCTION/K-lane; repro: 1 construct, in-order, threaded maps / serial folds); inputonly twins ok; blade-llvm serial-only ("LLVM lane has no OpenMP") as designed; all gfortran/ifx arms ok. Log: pm_onepass_mt8.log.

Failures: none. git status after runs: clean.
