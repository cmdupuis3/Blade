# Wave-2 end gates, fourth run (integration tree ba585a28 = 8e5764ac + notes + the cherry-pick of W2-F's 463c3fee, findings 22-23)

Tree at ba585a28; build 0 warnings / 0 errors; Blade.dll SHA256 3796F2FDCBD7590015F8914D26BA6752A6230B05E406ACC9C0C729C6D4195AEE, re-hashed
after every gate: unchanged. Gate cwd C:\bt_r2w1; runner gates\w2\gates.ps1; consoles gates\w2\run4-ba585a28\.

| # | gate | footer | 8e5764ac | b161cf10 | bd34627b |
|---|---|---|---|---|---|
| 1 | `blade test` | `TOTAL: 7157 passed, 0 failed` | 7149/0 | 7143/0 | 7135/0 |
| 2 | `blade test opt-diff` | `Opt Diff: 2064 passed (1871 optimizer-inert), 0 failed, 937 skipped` | 2063/0/932 | 2060/0/931 | 2058/0/928 |
| 3 | `blade test docs` | `Docs (executable code blocks): 131 passed, 0 failed, 78 skipped` | = | = | = |
| 4 | `blade test llvm` | `TOTAL: 814 passed, 0 failed, 683 skipped` | 814/0/682 | 812/0/681 | 809/0/681 |
| 5 | `blade test interp` | `Interp Diff: 2998 passed, 0 failed, 3 skip-unsupported, 0 skipped` | 2992/0/3 | 2988/0/3 | 2983/0/3 |

Extra sweeps on the same binary:
- `blade check` of all 68 examples/*.blade|bladenb (examples-check.ps1): 6 non-zero exits, ALL identical on the master 4136f0b5 and
  wave-1 6b3e5293 binaries (4 absent zarr stores BL2008; tools/check_cod_shells.blade BL4003; station_temps.bladenb BL3999) -- pre-existing.
- Bench emission identity (emit-identity.ps1): `blade emit` of all 12 bench kernels x {cpp, llvm} x {licensed, repro}, a fresh build of
  bd34627b vs ba585a28: 48 same, 0 differ -- W2-F's effect-analysis fixes 12-23 are byte-inert on the bench.

The Codex review of 463c3fee (codex-diff-W2F-7.md) closed 23 and sent back 24 (REGRESSION: nullary callbacks newly refused) and 25
(second-order callback propagation missing); one last fix commit follows, with a fifth gate run.
