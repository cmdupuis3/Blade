# Post-merge sanity, W1-B merged into worktree-bench-fortran (e5e24e17)

- `git status --short`: empty. `git log --oneline -1`: e5e24e17 Merge W1-B (cell epilogues ...).
- Build `dotnet build Blade.fsproj -c Release`: succeeded, 0 warnings, 0 errors (45 s). The hand-resolved src/CodeGenExprSupport.fs compiles.
- Blade.dll SHA256: 3089A0281F272BEE987D826F3E80EFD8F9A46199A071F4F6ADF194F69A495F14 (mtime 2026-10-20 02:25:37)
- Env: ucrt64 first on PATH; OPENBLAS_DIR=C:\Users\cdupu\tools\OpenBLAS-0.3.34-x64 (bin on PATH). Each run: `bin\Release\net10.0\Blade.exe test <key>` from the worktree root, run serially.

| key | footer | exit |
|---|---|---|
| optimize | Optimization Layer: 190 passed, 0 failed | 0 |
| cell-epilogue | Cell Epilogue: 56 passed, 0 failed, 0 skipped | 0 |
| fold-lanes | Fold Lanes: 4 passed, 0 failed | 0 |
| loops | 247 passed, 0 failed, 0 skipped | 0 |
| recursive-arrays | 29 passed, 0 failed, 0 skipped | 0 |
| symmetry | 128 passed, 0 failed, 0 skipped | 0 |
| linalg | BLAS Tier Resolution: 19 passed, 0 failed | 0 |
| cli | CLI Smoke: 105 passed, 0 failed | 0 |
| corpus-wiring | Corpus Wiring: 14 passed, 0 failed | 0 |
| omp-pragma | OpenMP Pragma: 102 passed, 0 failed | 0 |
| omp-reduce | OpenMP Reduce: 93 passed, 0 failed | 0 |
| omp-coverage | OpenMP Coverage: 8 passed, 0 error(s), 0 warning(s) | 0 |

No failures. Footers without a "skipped" count do not print one (optimize, fold-lanes, linalg, cli, corpus-wiring, omp-*); the ones that do print it show 0 skipped.
Notes: optimize 190 equals W1-B's count (W1-A additions not visible as a delta; the W1-A post-merge baseline was in postmerge-W1A.md). omp-pragma 102 matches W1-A's count. Logs are UTF-16: gates\test-<key>.log.
