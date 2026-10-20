# Wave-2 end gates, first run (integration tree bd34627b = wave-2 tip before the last W2-F fix round)

Tree: C:\Users\cdupu\Documents\GitHub\Blade\.claude\worktrees\bench-fortran at bd34627b (git rev-parse verified in gate-head.txt).
Build: `dotnet build Blade.fsproj -c Release` 0 errors, 2 warnings (FS0046: the identifier `pure` is reserved, tests/LlvmTests.fs:871 and :876 -- W2-F's new golden; renamed in the next fix commit).
Blade.dll SHA256 28EA3E0AA13FC2AE035C532F1DA49AF8437D960E98051C8C5D7DB2E1198810DB, re-hashed after every gate: unchanged.
Gate cwd C:\bt_r2w1 (`tests` and `docs` junctions to the worktree). Runner gates\w2\gates.ps1 (sequential); timing gate-timing.txt; consoles gate-<gate>.txt (UTF-16).
Box shared with nothing else during the run (the Codex review only reads files). All exit codes 0; verdicts from the footer lines.

| # | gate | footer | wall | wave-1 (6b3e5293) |
|---|---|---|---|---|
| 1 | `blade test` | `TOTAL: 7135 passed, 0 failed` (no skipped suffix) | 14.5 min | 7024/0/0 |
| 2 | `blade test opt-diff` | `Opt Diff: 2058 passed (1865 optimizer-inert), 0 failed, 928 skipped, 216s` | 3.6 min | 2037/0/915 |
| 3 | `blade test docs` | `Docs (executable code blocks): 131 passed, 0 failed, 78 skipped` | 0.5 min | identical |
| 4 | `blade test llvm` | `TOTAL: 809 passed, 0 failed, 681 skipped` (LLVM Differential 667/0/681) | 16.6 min | 780/0/680 |
| 5 | `blade test interp` | `Interp Diff: 2983 passed, 0 failed, 3 skip-unsupported, 0 skipped` | 39.9 min | 2949/0/3 |

+111 default tests, +21 opt-diff pairs (+13 skipped = lane-declined), +29 llvm, +34 interp since the wave-1 tip. The one `BL9002` match in the
default console is a test name (`ICE: rendered as BL9002 ...`); no stale-corpus line. The 3 interp skip-unsupported are the wave-1 ones.

This run gates bd34627b; the Codex review of that commit (codex-diff-W2F-4.md) sent four more same-class effect holes (16-19) back to W2-F,
so the FINAL wave-2 tip is one fix commit later and the five gates are re-run on it (w2-gates-summary.md).
