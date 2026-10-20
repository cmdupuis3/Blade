# Wave-1 end gates (integration tree 6b3e5293)

Tree: C:\Users\cdupu\Documents\GitHub\Blade\.claude\worktrees\bench-fortran at 6b3e5293 (git log --oneline -1 verified), not rebuilt.
Blade.dll SHA256 3061E9002F30D268A6B9E5BDC1A65C69AF6AE24BE6AF2EFB634475BF9B64F260, verified before the run and re-hashed after every gate: unchanged.
Gate cwd C:\bt_r2w1; `tests` and `docs` junctions verified -> the worktree's tests and docs.
Runner: gates\w1-run-gates.ps1 (sequential, one gate at a time); timing log gates\w1-timing.txt. Consoles gates\w1-<gate>.txt.
All five exit codes were 0, but the verdict below comes from the footer lines.
Note: `blade test` printed "ignoring ambient OPENBLAS_DIR" (suites pin the default emission), as designed.

| # | gate | footer line | wall | failures |
|---|---|---|---|---|
| 1 | `blade test` | `TOTAL: 7024 passed, 0 failed` (no `skipped` suffix = 0 skipped) | 18.5 min (03:07:11-03:25:41) | none |
| 2 | `blade test opt-diff` | `Opt Diff: 2037 passed (1845 optimizer-inert), 0 failed, 915 skipped, 328s` | 5.46 min | none |
| 3 | `blade test docs` | `Docs (executable code blocks): 131 passed, 0 failed, 78 skipped` (78 = sketch blocks; 13 pages, 30 checked / 90 run / 10 rejects) | 0.59 min | none |
| 4 | `blade test llvm` | `TOTAL: 780 passed, 0 failed, 680 skipped` (LLVM Differential 649 passed / 0 failed / 680 skipped) | 24.28 min | none |
| 5 | `blade test interp` | `Interp Diff: 2949 passed, 0 failed, 3 skip-unsupported, 0 skipped` | 48.59 min | none |

## Versus baselines (4136f0b5)
- default: 6800/0/1 -> 7024/0/0. +224 tests; the one skip (docs Surface) is gone now that `docs` is junctioned (Surface block: 7 passed, 0 failed).
- opt-diff: 1999/915 skipped -> 2037/915 skipped (+38 pairs, includes new BLADE_SIBLING_FOLDS / BLADE_COMPACTION_FUSION / BLADE_HOIST pairs; all output-identical).
- docs: 131/0/78 -> 131/0/78 (identical).
- llvm: 745/0/677 -> 780/0/680 (+35 passed, +3 skipped; skipped count is the sweep's own lane-declined entries, not failures).
- interp: 2912/0/2 -> 2949/0/3 skip-unsupported (+37; skip-unsupported went 2 -> 3).

## Facts worth knowing
- Zero failures in all five gates. `[FAIL]` count in the default console is 0; the many `IR fail` lines are `(rejects)` probes that are correctly refused, and `BL9001` matches (2) are test names, not errors. No `BL9002`, no `Value Error` in any console; stale-corpus check (`has no test subdirectories`) empty.
- interp's 3 skip-unsupported: `Fill Random Symmetric`, `Fill Random Antisymmetric` (fill_random(mod) nondeterministic) and `Compaction fusion declines across writes: a mutated compound keeps the as-written values` (IRAssign to non-array index target). The first two were presumably the baseline 2; the third (new W1-C test) is the +1: the interpreter does not evaluate it.
- interp notes: 4 reject-probes lack `// ERROR:` pins (informational, not failures); slowest interp tests: ML E2E Ops Elaboration Training 24.2 s, Simplex Tile Three K Blocks Omp 8.3 s, Spectra fft2 Parseval canary 7.0 s.
- default suite's 116 `[SKIP]` lines are informational diagnostic rows (e.g. equiv-verdict tables), not counted in the TOTAL (which has no skipped suffix).
- Box was shared with an unrelated `Blade.exe ide serve` (PID 20660, master checkout) and other builders; none affected correctness. Wall times are therefore not timing-grade.
- Nothing timing/bench was run.
