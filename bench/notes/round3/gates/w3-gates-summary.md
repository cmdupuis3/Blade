# Round 3, waves 3 + 4: the end-chain gates on f46fdfc0 (one combined chain, 2026-10-24)

Chain `w3-scripts/wave3-end.ps1`, tip f46fdfc0 (the W4-3 merge), Blade.dll 33B1F99174FBEAB80167100023BCF9E3E120C88F93726A1CAB7CCF7F410CA849,
built 00:20-00:21, hash unchanged after every gate. Run from `C:\bt_r2w1` (a private working directory; the scratch dir is cwd-relative).

| gate | footer | wall |
|---|---|---|
| default (`blade test`) | **TOTAL: 8223 passed, 0 failed** | 33.9 min |
| opt-diff | Opt Diff: 2364 passed (1967 optimizer-inert), 0 failed, 966 skipped | 7.1 min |
| docs | Docs (executable code blocks): 133 passed, 0 failed, 79 skipped | 0.1 min |
| llvm | TOTAL: 950 passed, 0 failed, 730 skipped | 14.0 min |
| interp | Interp Diff: 3229 passed, 0 failed, 4 skip-unsupported, 0 skipped | 30.7 min |

Examples sweep (`examples-check.ps1`): 68 files, 6 failed -- the SAME six pre-existing failures as waves 1 and 2 (four notebooks
whose zarr stores are not in the checkout: aspirin_energy, cod_crystals, matched_moments, tetris_shapes; `tools/check_cod_shells`'s
BL4003 warning; station_temps' BL3999). Mirrors: eight recreated from the tip (`mirrors-w3.ps1 -Recreate`, 01:49-01:50, no
mismatch). Smoke (`smoke-w3.ps1`): every benched kernel x regime x mirror, 0 nonzero exits (01:50-02:17). Quiet-box audit before
the A/B: no stray `find` / `dotnet` / `g++` / `Blade` process. A/B (`driver-w3.ps1`): 27 sessions 02:18-04:50, none aborted, the
harness reports no failures; analysis (`analyze-w3.py`): 210 ratio rows, 48 arm rows, slower-than-old (r3w3 vs r3w2bin) 0,
missing cells 0 -> `../w3-ab.md`.

Merge-level light checks (block runs on the integration tree after each code merge, `w3-scripts/w3-check.ps1 -Item <W>`): W3-4,
W2-4 (rounds 4-9), W3-2, W3-1, W4-4, W4-1 (+ the tuples re-run after its witness was trimmed), W4-2, W4-3 -- every block green;
the footers are in the respective commit messages.
