# Wave-2 end gates, fifth run (integration tree 1cf90428 = ba585a28 + notes + the cherry-pick of W2-F's 804516d1, findings 24-25)

Tree at 1cf90428; build 0 warnings / 0 errors; Blade.dll SHA256 2293124A3BF60296C89DB1A5CF9388FBC09DF37178B4FCED1E207FBF4CE05B81, re-hashed
after every gate: unchanged. Gate cwd C:\bt_r2w1; runner gates\w2\gates.ps1; consoles gates\w2\run5-1cf90428\.

| # | gate | footer | ba585a28 | 8e5764ac | b161cf10 | bd34627b |
|---|---|---|---|---|---|---|
| 1 | `blade test` | `TOTAL: 7168 passed, 0 failed` | 7157 | 7149 | 7143 | 7135 |
| 2 | `blade test opt-diff` | `Opt Diff: 2067 passed (1874 optimizer-inert), 0 failed, 942 skipped` | 2064/937 | 2063/932 | 2060/931 | 2058/928 |
| 3 | `blade test docs` | `Docs (executable code blocks): 131 passed, 0 failed, 78 skipped` | = | = | = | = |
| 4 | `blade test llvm` | `TOTAL: 814 passed, 0 failed, 686 skipped` | 814/683 | 814/682 | 812/681 | 809/681 |
| 5 | `blade test interp` | `Interp Diff: 3006 passed, 0 failed, 3 skip-unsupported, 0 skipped` | 2998 | 2992 | 2988 | 2983 |

Examples sweep: the same 6 pre-existing non-zero exits (identical on master 4136f0b5). Bench emission identity vs bd34627b: 48 same, 0 differ.

The Codex review of 804516d1 (codex-diff-W2F-8.md) closed 24 and found two REGRESSIONS of accepted pure programs introduced by the new
callback collector's tainting (26 a module-level alias of a function taken as an escape; 28 discovery-order taint of a not-yet-populated
slot) plus 27 (an alias of a callback parameter hides the indirect edge): one more fix commit follows (gates run 6).
