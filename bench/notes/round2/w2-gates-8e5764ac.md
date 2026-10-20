# Wave-2 end gates, third run (integration tree 8e5764ac = b161cf10 + notes + the cherry-pick of W2-F's d9289387, findings 20-21)

Tree at 8e5764ac (gate-head.txt); build 0 warnings / 0 errors; Blade.dll SHA256 in gate-head.txt, re-hashed after every gate: unchanged.
Gate cwd C:\bt_r2w1; runner gates\w2\gates.ps1; consoles gates\w2\run3-8e5764ac\gate-<gate>.txt.

| # | gate | footer | b161cf10 run | bd34627b run |
|---|---|---|---|---|
| 1 | `blade test` | `TOTAL: 7149 passed, 0 failed` | 7143/0 | 7135/0 |
| 2 | `blade test opt-diff` | `Opt Diff: 2063 passed (1870 optimizer-inert), 0 failed, 932 skipped` | 2060/0/931 | 2058/0/928 |
| 3 | `blade test docs` | `Docs (executable code blocks): 131 passed, 0 failed, 78 skipped` | identical | identical |
| 4 | `blade test llvm` | `TOTAL: 814 passed, 0 failed, 682 skipped` | 812/0/681 | 809/0/681 |
| 5 | `blade test interp` | `Interp Diff: 2992 passed, 0 failed, 3 skip-unsupported, 0 skipped` | 2988/0/3 | 2983/0/3 |

The Codex review of d9289387 (codex-diff-W2F-6.md) closed 21 and sent two more back (22 one-candidate callback resolution; 23 the
recursion cutoff is not top and is per callable), so the final tip is one more fix commit later; the gates run a fourth time on it.
