# Post-merge W2-A sanity gates (integration worktree bench-fortran @ 12ec4734 = 763cbc3a + the w1-ab.md note)

Preconditions: git status clean; HEAD 12ec4734 (code identical to the W2-A merge 763cbc3a; 12ec4734 only adds bench/notes/round2/w1-ab.md);
build `dotnet build Blade.fsproj -c Release` 0 warnings / 0 errors (45 s); Blade.dll SHA256 F1690954801D0FF0F6889FC8ACA45040DF496358746B6E7A64C6A67251D4137A,
re-hashed after the run: unchanged. ucrt64 g++ first on PATH, OPENBLAS_DIR set. Runner: gates\pm2\run.ps1 (one block at a time, from the worktree root);
consoles gates\pm2\<key>.log (UTF-16). All exit 0; footers:

| key | footer | vs W2-D/E post-merge (d332f700) |
|---|---|---|
| optimize | Optimization Layer: 257 passed, 0 failed | 246 (+11: W2-A's scatter decision cases) |
| orbit-scatter | Orbit Scatter: 17 passed, 0 failed, 0 skipped | new block |
| omp-pragma | OpenMP Pragma: 109 passed, 0 failed | 103 (+6) |
| omp-reduce | OpenMP Reduce: 117 passed, 0 failed | = |
| omp-coverage | OpenMP Coverage: 10 passed, 0 error(s), 0 warning(s) | = |
| linalg | Row Contraction Tiles 24/0; BLAS Tier Resolution 19/0 | = |
| syrk-tile | Syrk Tile: 4 passed, 0 failed | = |
| row-tile | Row Contraction Tiles: 24 passed, 0 failed | = |
| llvm tiles | LLVM Tiles Raw-Bit: 12 passed, 0 failed, 0 skipped | = |
| llvm facts | LLVM Fact Layer: 60 passed, 0 failed, 0 skipped | = |
| loops | Verdict 253 passed, 0 failed, 0 skipped | = |
| sibling-folds | Verdict 18 passed, 0 failed, 0 skipped | = |
| symmetry | Verdict 137 passed, 0 failed, 0 skipped | 132 (+5: symmetry/132-135 scatter twins + 1) |
| index-types | Verdict 380 passed, 0 failed, 0 skipped | = |
| cell-epilogue | Cell Epilogue: 56 passed, 0 failed, 0 skipped | = |
| fold-lanes | Fold Lanes: 4 passed, 0 failed | = |
| cli | CLI Smoke: 132 passed, 0 failed | 128 (+4) |
| corpus-wiring | Corpus Wiring: 14 passed, 0 failed | = |

W2-A's own runs on its branch tip 52d0c4aa (= W2-A + W2-E merged there) agree: cli 130, corpus-wiring 14, interp symmetry 137/0/0,
llvm symmetry 81/0/55 (the usual reject probes + lane-declined), optimize 249, symmetry 136, orbit-scatter 17, omp-pragma 109,
omp-reduce 117, omp-coverage 10, loops 253, sibling-folds 18; `git diff --check 6b3e5293 HEAD` clean there.

## Bench harness
- `run.py check onepass` -> exit 0, 0 failure(s), 0 flag(s) (serial-licensed, all 12 arms).
- `run.py check pairkernel --regimes serial-repro-native,mt8-repro-native` -> exit 0, 0 failure(s), 0 flag(s).
- `run.py check comoment3 --regimes serial-repro-native` -> exit 0, 0 failure(s), 0 flag(s).
Checksums match the pins in every arm (the scatter changes no bit: bitwise the logical walk under REPRO + CONTRACT=off, and the
repro regime pins contraction off).

Failures: none. git status after the runs: clean.
