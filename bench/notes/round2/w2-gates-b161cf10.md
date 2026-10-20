# Wave-2 end gates, second run (integration tree b161cf10 = bd34627b + notes + the cherry-pick of W2-F's 843cb654, findings 16-19)

Tree: C:\Users\cdupu\Documents\GitHub\Blade\.claude\worktrees\bench-fortran at b161cf10 (gate-head.txt). Build 0 warnings / 0 errors.
Blade.dll SHA256 DCDCDE0C7AEF23EF9DB21858AA8253E86F67646BE326464C0E3C84EB77964C99, re-hashed after every gate: unchanged.
Gate cwd C:\bt_r2w1 (`tests` / `docs` junctions to the worktree); runner gates\w2\gates.ps1; consoles gates\w2\gate-<gate>.txt; timing gate-timing.txt.
Wall times are short because the content-addressed exe cache served every program whose emitted text did not change since the bd34627b run.

| # | gate | footer | wall | bd34627b run | wave-1 (6b3e5293) |
|---|---|---|---|---|---|
| 1 | `blade test` | `TOTAL: 7143 passed, 0 failed` (no skipped suffix) | 8.8 min | 7135/0 | 7024/0 |
| 2 | `blade test opt-diff` | `Opt Diff: 2060 passed (1867 optimizer-inert), 0 failed, 931 skipped, 131s` | 2.2 min | 2058/0/928 | 2037/0/915 |
| 3 | `blade test docs` | `Docs (executable code blocks): 131 passed, 0 failed, 78 skipped` | 0.1 min | identical | identical |
| 4 | `blade test llvm` | `TOTAL: 812 passed, 0 failed, 681 skipped` | 2.1 min | 809/0/681 | 780/0/680 |
| 5 | `blade test interp` | `Interp Diff: 2988 passed, 0 failed, 3 skip-unsupported, 0 skipped` | 3.5 min | 2983/0/3 | 2949/0/3 |

No stale-corpus line, no `[FAIL]`. The Codex review of 843cb654 (codex-diff-W2F-5.md) closed 16, 17, 19 and sent two more back
(20 unresolved callback in a parallel body; 21 the new refusal rejects a legal write to an OWN mut parameter), so the final tip is
one more fix commit later and the gates run a third time on it.
