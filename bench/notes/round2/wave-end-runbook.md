# Wave-end runbook (round 2, wave 1) -- PowerShell, commands only

Vars (paste once per shell): 
```powershell
$S  = 'C:\Users\cdupu\AppData\Local\Temp\claude\C--Users-cdupu--claude-spares-spare-dUbXep\dbd3f5e5-01ed-41dd-bc92-4bc0ec85152b\scratchpad\round2'
$WT = 'C:\Users\cdupu\Documents\GitHub\Blade\.claude\worktrees\bench-fortran'
$BIN = "$WT\bin\Release\net10.0"
$env:PATH = 'C:\msys64\ucrt64\bin;C:\Users\cdupu\tools\OpenBLAS-0.3.34-x64\bin;' + $env:PATH   # ucrt64 FIRST (same as baseline)
```
Box must be quiet (no builders, no other tests) for every step below; one step at a time.

## (a) Build the integration worktree, hash it, make the NEW mirror
1. Build (foreground, ~minutes): `Set-Location $WT; dotnet build Blade.fsproj -c Release 2>&1 | Tee-Object "$S\gates\w1-build.txt" | Select-Object -Last 15`
2. Hash: `(Get-FileHash "$BIN\Blade.dll").Hash | Tee-Object "$S\gates\NEW-hash.txt"; Test-Path "$BIN\blade.toolchain.json"` (must be False; Blade.exe must exist)
3. NEW mirror: `robocopy "$WT\bench" C:\bt_r2ab\new\bench /E /XD results .build __pycache__ /NFL /NDL /NJH /NJS /NP; New-Item -ItemType Directory -Force C:\bt_r2ab\new\bench\results,C:\bt_r2ab\new\bench\.build; robocopy $BIN C:\bt_r2ab\new\bin\Release\net10.0 /E /NFL /NDL /NJH /NJS /NP`
4. Check same hash + same bench: `(Get-FileHash C:\bt_r2ab\new\bin\Release\net10.0\Blade.dll).Hash` equals NEW-hash.txt; `fc.exe C:\bt_r2ab\old\bench\harness\config.json C:\bt_r2ab\new\bench\harness\config.json` (and kernels\ via `git -C $WT diff --stat 4136f0b5 -- bench`) -- any bench diff between old and new mirror must be explained before A/B.
5. Copy the README: `Copy-Item C:\bt_r2ab\old\README.txt C:\bt_r2ab\new\README.txt` (edit "old" -> "new", hash).

## (b) The five gates, from C:\bt_r2w1 (tests+docs junctions to the integration worktree), NEW binary, one at a time
Each: start in the background, poll with `Get-Content -Tail 3`, then grep TOTAL. Never two at once. Exit code is 0 even with failures: GATE ON THE TOTAL LINE and the `, N skipped` suffix.
```powershell
Set-Location C:\bt_r2w1
$B = "$BIN\Blade.exe"
$h0=(Get-FileHash "$BIN\Blade.dll").Hash   # re-hash after each gate: must be unchanged
```
| # | command (run_in_background) | console | expect wall | grep after |
|---|---|---|---|---|
| 1 | `& $B test *> "$S\gates\w1-default.txt"` | gates\w1-default.txt | ~7-16 min (baseline 6.9) | `Select-String 'TOTAL' "$S\gates\w1-default.txt"`; baseline 6800 passed / 0 failed / 1 skipped (docs skip should now be gone -> 0 skipped) |
| 2 | `& $B test interp *> "$S\gates\w1-interp.txt"` | gates\w1-interp.txt | ~48 min | `Select-String 'TOTAL\|interp.*(passed\|failed)' "$S\gates\w1-interp.txt" \| Select -Last 5` |
| 3 | `& $B test opt-diff *> "$S\gates\w1-opt-diff.txt"` | gates\w1-opt-diff.txt | ~2.5 min | `Select-String 'TOTAL\|differ\|mismatch' "$S\gates\w1-opt-diff.txt" \| Select -Last 5` |
| 4 | `& $B test llvm *> "$S\gates\w1-llvm.txt"` | gates\w1-llvm.txt | ~23 min | `Select-String 'TOTAL' "$S\gates\w1-llvm.txt"` |
| 5 | `& $B test docs *> "$S\gates\w1-docs.txt"` | gates\w1-docs.txt | ~0.5 min | `Select-String 'TOTAL' "$S\gates\w1-docs.txt"`; baseline-ish 131 / 0 / 78 sketches |

Polling (every 1-2 min while running): `Get-Content "$S\gates\w1-<gate>.txt" -Tail 3`; failures list: `Select-String 'FAIL' "$S\gates\w1-<gate>.txt" | Select -First 20`. Compare #1 against `gates\baseline-default.txt` (6800/0/1). Stale-corpus check: `Select-String 'BL9001|has no test subdirectories' gates\w1-*.txt` must be empty.

## (c) Harness A/B (OLD vs NEW mirrors; bench refuses without smoke evidence for the current Blade.dll hash)
Smoke both (each ~minutes; one at a time):
```powershell
foreach ($m in 'old','new') { Set-Location "C:\bt_r2ab\$m"; C:\Python314\python.exe bench\harness\run.py smoke --regimes all *> "$S\gates\w1-smoke-$m.txt"; "$m smoke exit $LASTEXITCODE" }
```
Must be exit 0 / `0 failure(s)` in both (an mt8 Blade no-parallel-form or LLVM serial-only is not a failure; unexpected `failed`/`fallback` is). Evidence: C:\bt_r2ab\<m>\bench\.build\smoke-evidence.json.

Bench, INTERLEAVED per kernel/regime-set (old, new, old, new ...); one `bench` process at a time; no other load; `--regimes` is a cross product, so each row is its own pair of invocations. Rounds default (serial 11, mt8 7, 21 if median <50 ms).
```powershell
$cells = @(
 @('saxpy',      'serial-licensed-native,mt8-licensed-native,serial-repro-native,mt8-repro-native'),
 @('stencil',    'serial-repro-native,mt8-repro-native,serial-licensed-native,mt8-licensed-native'),   # licensed = controls
 @('recur',      'serial-repro-native,serial-licensed-native'),                                         # licensed = control
 @('pairkernel', 'serial-licensed-native,mt8-licensed-native'),
 @('maskagg',    'serial-licensed-native,serial-repro-native'),
 @('dot',        'serial-licensed-native,serial-repro-native,serial-fastmath-native,mt8-repro-native'),
 @('onepass',    'serial-licensed-native,serial-repro-native,serial-fastmath-native,mt8-repro-native'),  # Blade mt8 = no-parallel-form (not timed)
 @('fusedchain', 'serial-licensed-native,serial-repro-native,serial-fastmath-native,mt8-repro-native'),
 @('gram',       'mt8-licensed-native')                                                                # control
)
foreach ($c in $cells) { foreach ($m in 'old','new') {
  Set-Location "C:\bt_r2ab\$m"
  C:\Python314\python.exe bench\harness\run.py bench --kernels $c[0] --regimes $c[1] *> "$S\gates\w1-bench-$($c[0])-$m.txt"
  "$($c[0]) $m exit $LASTEXITCODE (0 clean, 3 flags only, 1 failure)" } }
```
(Run in the background and poll `Get-ChildItem $S\gates\w1-bench-*-*.txt | Sort LastWriteTime | Select -Last 2`; abort with a STOP, not a kill, if the harness offers one -- else Stop-Process the python.exe and discard that cell.)
Harness outputs: `C:\bt_r2ab\<m>\bench\results\<date>-<host>\session-<HHMMSS>\{session.json,manifest.json}` + `summary.md` beside it; smoke/check bundles under `C:\bt_r2ab\<m>\bench\.build\sessions\...`. Drift/suppression: `summary.md` (blocks marked `indicative`/`suppressed`, session flags) and `session.json` keys `flags`, `suppressed`, `controls`, `arms`.

## (d) Reporting
- Per cell (kernel, regime, arm incl. both Blade lanes and the Fortran references as context): OLD median, NEW median, ratio NEW/OLD, 95% CI (from each session.json arms[].median / bootstrap; compute ratio of the two session medians, and quote each summary's own CI vs Fortran), verdict `live` (block unflagged) or `indicative` (suppressed/drift-flagged), plus any session flags and excluded rounds.
- Input-only twins (`<arm>-inputonly`) reported in a SEPARATE table with the same columns (item C changes the twins too); also give net = median - twin median per arm for both OLD and NEW.
- Mention the box state (AC power, scheme), the two Blade.dll hashes (OLD-hash.txt / NEW-hash.txt), controls' drift between OLD and NEW sessions, and that the gate #1-5 TOTALs came from the same NEW hash.
