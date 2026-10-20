# Post-merge sanity, W1-A (G35) on integration worktree

Worktree: C:\Users\cdupu\Documents\GitHub\Blade\.claude\worktrees\bench-fortran, branch worktree-bench-fortran

1. `git status --short`: empty. `git log --oneline -1`: `6c096060 Merge W1-A (G35 row-slot repro folds) into worktree-bench-fortran`.
2. `dotnet build Blade.fsproj -c Release`: Build succeeded, 0 Warning(s), 0 Error(s), 45.86 s, exit 0.
   Blade.dll SHA256 = 127A15E67E9A49B9BF884F3D2B5D41FC56DB6D459ACBD13EB5D477AD9970B6B5 (13677568 bytes)
3. Targeted blocks (bin\Release\net10.0\Blade.exe test <key>, one at a time, ucrt64 + OPENBLAS_DIR set; all process exit 0):

| key | footer |
|---|---|
| optimize | Optimization Layer: 152 passed, 0 failed |
| linalg | BLAS Tier Resolution: 19 passed, 0 failed |
| cli | CLI Smoke: 105 passed, 0 failed |
| corpus-wiring | Corpus Wiring: 14 passed, 0 failed |
| symmetry | Verdict: 128 passed, 0 failed, 0 skipped (Total 128; Full Pass 89/128 IR+Compile+Run, Value Check 89/89) |
| omp-pragma | OpenMP Pragma: 102 passed, 0 failed |
| omp-reduce | OpenMP Reduce: 93 passed, 0 failed |
| omp-coverage | OpenMP Coverage: 8 passed, 0 error(s), 0 warning(s) |

Skips: only symmetry prints a skipped count (0). The other footers print none; scans of all logs found no [FAIL] and no skip lines.
4. Failures: none. omp-pragma 102/0, omp-reduce 93/0, omp-coverage 8 match W1-A's own-branch figures.

Logs: scratchpad\round2\gates\postmerge-<key>.log, postmerge-build.log, postmerge-summary-exits.txt
Not run (per instructions): full suite, interp, opt-diff, llvm, docs, timing.
