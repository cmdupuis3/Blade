# Wave-3 gates -- GREEN on b4a21003 (Blade.dll SHA256 2B9E12ACDD6522DF29C17600CA8ADA9DD184A108F6E1998B6E501593EB940BC6)

Run 2026-10-20 17:57-18:38 from `C:\bt_w3` (tests/ and docs/ junctions to the wave-3
worktree), `w3-scripts/gates.ps1`, one suite at a time, box quiet; consoles in
`C:\bt_w3\gates\gate-<name>.txt`; the hash unchanged after every gate.

| gate | TOTAL | wall | wave-2 final (RESULTS 15.2) |
|---|---|---|---|
| default | 7234 passed, 0 failed (no skips) | 13.7 min | 7180 / 0 |
| opt-diff | 2074 passed (1879 optimizer-inert), 0 failed, 945 skipped | 3.5 min | 2072 / 0 / 944 |
| docs | 131 passed, 0 failed, 78 skipped (sketches) | 0.3 min | 131 / 0 / 78 |
| llvm | 816 passed, 0 failed, 691 skipped | 7.6 min | 814 / 0 / 691 |
| interp | 3016 passed, 0 failed, 3 skip-unsupported | 15.3 min | 3013 / 0 / 3 |

The +54 default / +2 opt-diff / +2 llvm / +3 interp are the wave-3 tests (recursive-arrays
031-032, mutability-errors 042, the 15 optimize cases, the 36-case `rec-step` block).
