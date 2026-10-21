# Round-3 wave-1 end gates (integration tree 9da403a8 = the wave-1 tip)

Chain `scratchpad/round3/gates/wave1-end.ps1` (unattended): build -> five gates from C:\bt_r2w1 -> examples sweep -> mirrors -> smoke -> A/B.
Blade.dll SHA256 E84BB4E59A8111432B957B6EE9764B027BE608E6BB8E15DACEE55C0D103AEAEF, unchanged across the gates.

| gate | footer | master 66e04875 |
|---|---|---|
| blade test | TOTAL: 7389 passed, 0 failed | 7252/0 |
| blade test opt-diff | Opt Diff: 2173 passed (1918 optimizer-inert), 0 failed | 2088/0 |
| blade test docs | Docs (executable code blocks): 131 passed, 0 failed, 78 skipped | identical |
| blade test llvm | TOTAL: 841 passed, 0 failed | 825/0 |
| blade test interp | Interp Diff: 3077 passed, 0 failed, 3 skip-unsupported | 3031/0 |

Examples sweep: the same six pre-existing non-zero exits as on master. The Codex merge-level review (reviews/codex-wave1-merged.md)
found no lost hunk, no duplicate-counted test and no cross-item wrong value; its two record fixes (the G51 citation, the G58
wording, the G61 reference) are applied in the same commit.

Chain log:
```
2026-10-21T02:25:34 START tip=9da403a8
2026-10-21T02:25:36 BUILD     0 Warning(s) /     0 Error(s) hash=E84BB4E59A8111432B957B6EE9764B027BE608E6BB8E15DACEE55C0D103AEAEF
2026-10-21T02:40:27 GATE default exit=0 wall_min=14.84 hash=E84BB4E59A8111432B957B6EE9764B027BE608E6BB8E15DACEE55C0D103AEAEF
2026-10-21T02:45:21 GATE opt-diff exit=0 wall_min=4.9 hash=E84BB4E59A8111432B957B6EE9764B027BE608E6BB8E15DACEE55C0D103AEAEF
2026-10-21T02:45:28 GATE docs exit=0 wall_min=0.11 hash=E84BB4E59A8111432B957B6EE9764B027BE608E6BB8E15DACEE55C0D103AEAEF
2026-10-21T03:02:39 GATE llvm exit=0 wall_min=17.18 hash=E84BB4E59A8111432B957B6EE9764B027BE608E6BB8E15DACEE55C0D103AEAEF
2026-10-21T03:40:35 GATE interp exit=0 wall_min=37.94 hash=E84BB4E59A8111432B957B6EE9764B027BE608E6BB8E15DACEE55C0D103AEAEF
FOOTER docs OK: Docs (executable code blocks): 131 passed, 0 failed
FOOTER opt-diff OK: Opt Diff: 2173 passed (1918 optimizer-inert), 0 failed
FOOTER default OK: TOTAL: 7389 passed, 0 failed
FOOTER llvm OK: TOTAL: 841 passed, 0 failed
FOOTER interp OK: Interp Diff: 3077 passed, 0 failed
2026-10-21T03:42:10 EXAMPLES 68 files, 6 failed
2026-10-21T03:42:30 MIRRORS exit=0
2026-10-21T03:57:56 SMOKE exit=0 0 nonzero
2026-10-21T03:57:56 AUDIT stray processes: 
2026-10-21T06:36:37 AB DONE exit=0
2026-10-21T06:36:45 ANALYZE ratio rows 174 arm rows 60 slow 1 missing 0
ALL DONE
```
