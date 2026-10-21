# Wave-2 end gates (final: integration tree f1910060 = THE WAVE-2 TIP)

Six gate runs were made as W2-F's effect-analysis fix commits landed (bd34627b, b161cf10, 8e5764ac, ba585a28, 1cf90428, f1910060);
every run was green. The final run, on f1910060 (Blade.dll SHA256 C7B144976C0935A4C3BD3B55CC32A07DDED6A69271071EFFBAAE4674DD512C7B,
re-hashed after every gate: unchanged; build 0 warnings / 0 errors; gate cwd C:\bt_r2w1; consoles gates\w2\run6-f1910060\):

| # | gate | footer | wave-1 tip 6b3e5293 | baseline 4136f0b5 |
|---|---|---|---|---|
| 1 | `blade test` | `TOTAL: 7180 passed, 0 failed` (no skipped suffix) | 7024/0/0 | 6800/0/1 |
| 2 | `blade test opt-diff` | `Opt Diff: 2072 passed (1879 optimizer-inert), 0 failed, 944 skipped` | 2037/0/915 | 1999/0/915 |
| 3 | `blade test docs` | `Docs (executable code blocks): 131 passed, 0 failed, 78 skipped` | identical | identical |
| 4 | `blade test llvm` | `TOTAL: 814 passed, 0 failed, 691 skipped` | 780/0/680 | 745/0/677 |
| 5 | `blade test interp` | `Interp Diff: 3013 passed, 0 failed, 3 skip-unsupported, 0 skipped` | 2949/0/3 | 2912/0/2 |

Skipped counts are lane-declined / sketch entries, not failures. Earlier runs: bd34627b 7135/2058/131/809/2983; b161cf10 7143/2060/131/812/2988;
8e5764ac 7149/2063/131/814/2992; ba585a28 7157/2064/131/814/2998; 1cf90428 7168/2067/131/814/3006 (all 0 failed).

Extra sweeps on f1910060:
- `blade check` of all 68 examples/*.blade|bladenb: 6 non-zero exits, all identical on master 4136f0b5 and the wave-1 tip (4 absent zarr
  stores BL2008; tools/check_cod_shells.blade BL4003; station_temps.bladenb BL3999): pre-existing.
- Bench emission identity: `blade emit` of all 12 bench kernels x {cpp, llvm} x {licensed, repro}, the 1cf90428 binary (the A/B mirrors'
  binary) vs f1910060: 48 same, 0 differ; src/cpp untouched between them. So the harness A/B, measured on the 1cf90428 binary, measures
  the final tip's bench programs bit for bit.

## Final combined gates (run 7, the tree pushed to master)

Integration tree 66e04875 = the 15 round-2 commits + G45-G47 (2) + G48-G50 (2) + wave 3 (6, rebased; its gap rows renumbered
G51-G53). Blade.dll SHA256 6FF830880B0B76084DE851DC6C4DB42CE1EECB87F70B0E18B87E3B580203A5DE, unchanged across the gates.
`blade test` TOTAL 7252 passed, 0 failed; opt-diff 2088 passed (1890 inert), 0 failed, 946 skipped; docs 131/0/78;
llvm TOTAL 825 passed, 0 failed, 696 skipped; interp 3031 passed, 0 failed, 3 skip-unsupported. Examples sweep: the same six
pre-existing non-zero exits. master fast-forwarded to 66e04875 and pushed 2026-10-20.
