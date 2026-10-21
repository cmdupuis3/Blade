# Round-3 wave-2 end chain: build -> five gates (from C:\bt_r2w1) -> examples sweep -> [all gates clean] -> mirrors -> smoke -> A/B -> analyze.
# PowerShell trap: variables are case-insensitive ($G and $g clash) -- every loop variable here has a distinct name.
$ErrorActionPreference = 'Continue'
$WT = 'C:\Users\cdupu\Documents\GitHub\Blade\.claude\worktrees\bench-fortran'
$BIN = "$WT\bin\Release\net10.0"
$R2 = 'C:\Users\cdupu\AppData\Local\Temp\claude\C--Users-cdupu--claude-spares-spare-dUbXep\dbd3f5e5-01ed-41dd-bc92-4bc0ec85152b\scratchpad\round2\gates\w2'
$S = 'C:\Users\cdupu\AppData\Local\Temp\claude\C--Users-cdupu--claude-spares-spare-dUbXep\dbd3f5e5-01ed-41dd-bc92-4bc0ec85152b\scratchpad\round3\gates'
$GD = "$S\w2-gates"; New-Item -ItemType Directory -Force $GD | Out-Null
$env:PATH = 'C:\msys64\ucrt64\bin;C:\Users\cdupu\tools\OpenBLAS-0.3.34-x64\bin;' + $env:PATH
$env:OPENBLAS_DIR = 'C:\Users\cdupu\tools\OpenBLAS-0.3.34-x64'
$log = "$S\wave2-end-progress.txt"
Set-Location $WT
$tip = (git rev-parse --short HEAD)
"$(Get-Date -Format s) START tip=$tip" | Set-Content $log
$b = dotnet build Blade.fsproj -c Release 2>&1 | Select-String -Pattern " error |warning|Warning\(s\)|Error\(s\)" | ForEach-Object { $_.Line.Substring(0, [Math]::Min(240, $_.Line.Length)) }
$b | Set-Content "$GD\build.txt"
$hash = (Get-FileHash "$BIN\Blade.dll").Hash
"$(Get-Date -Format s) BUILD $($b -join ' / ') hash=$hash" | Add-Content $log
if (($b -join ' ') -notmatch '0 Error') { "BUILD FAILED" | Add-Content $log; exit 1 }
if ($hash -eq 'E84BB4E59A8111432B957B6EE9764B027BE608E6BB8E15DACEE55C0D103AEAEF') { "BUILD IS STILL THE WAVE-1 BINARY" | Add-Content $log; exit 1 }
Set-Location C:\bt_r2w1
$B = "$BIN\Blade.exe"
$gates = @(@('default',@('test')),@('opt-diff',@('test','opt-diff')),@('docs',@('test','docs')),@('llvm',@('test','llvm')),@('interp',@('test','interp')))
foreach ($gt in $gates) {
  $n = $gt[0]; $a = $gt[1]; $t0 = Get-Date
  & $B @a *> "$GD\gate-$n.txt"
  "$(Get-Date -Format s) GATE $n exit=$LASTEXITCODE wall_min=$([math]::Round(((Get-Date)-$t0).TotalMinutes,2)) hash=$((Get-FileHash "$BIN\Blade.dll").Hash)" | Add-Content $log
}
# footers
$ok = $true
$checks = @{ 'default' = 'TOTAL: \d+ passed, 0 failed'; 'opt-diff' = 'Opt Diff: \d+ passed \(\d+ optimizer-inert\), 0 failed'; 'docs' = 'Docs \(executable code blocks\): \d+ passed, 0 failed'; 'llvm' = 'TOTAL: \d+ passed, 0 failed'; 'interp' = 'Interp Diff: \d+ passed, 0 failed' }
foreach ($n in $checks.Keys) {
  $txt = Get-Content "$GD\gate-$n.txt" -Raw
  $m = [regex]::Match($txt, $checks[$n])
  if ($m.Success) { "FOOTER $n OK: $($m.Value)" | Add-Content $log } else { "FOOTER $n FAILED (pattern not found)" | Add-Content $log; $ok = $false }
}
Set-Location $WT
powershell -NoProfile -ExecutionPolicy Bypass -File "$R2\examples-check.ps1" | Out-Null
Copy-Item "$R2\examples-check.txt" "$GD\examples-check.txt"
"$(Get-Date -Format s) EXAMPLES $(Get-Content "$GD\examples-check.txt" | Select-Object -Last 1)" | Add-Content $log
if (-not $ok) { "GATES NOT CLEAN -- stopping before the A/B" | Add-Content $log; exit 2 }
# mirrors + smoke + A/B
$AB = "$S\ab"
$MIR = 'r3w1,r3w2,r3w2prefer,r3w2pull0,r3w2antig1,r3w2legs0,r3w2retain'
powershell -NoProfile -ExecutionPolicy Bypass -Command "& '$AB\mirrors-w2.ps1' -Tip $tip -Mirrors $MIR" *> "$GD\mirrors.txt"
"$(Get-Date -Format s) MIRRORS exit=$LASTEXITCODE" | Add-Content $log
if ((Get-Content "$GD\mirrors.txt" -Raw) -match 'mismatch|exists|not the tip|wave-1 build') { "MIRRORS FAILED -- stopping" | Add-Content $log; exit 4 }
powershell -NoProfile -ExecutionPolicy Bypass -Command "& '$AB\smoke-w2.ps1' -Mirrors $MIR" *> "$GD\smoke.txt"
$bad = (Get-Content "$AB\smoke-progress-w2.txt" | Select-String 'exit [1-9]' | Measure-Object).Count
"$(Get-Date -Format s) SMOKE exit=$LASTEXITCODE $bad nonzero" | Add-Content $log
if ($bad -gt 0) { "SMOKE NOT CLEAN -- stopping before the A/B" | Add-Content $log; exit 3 }
# quiet-box audit
$procs = Get-CimInstance Win32_Process | Where-Object { $_.Name -match '^(find|dotnet|g\+\+|cc1plus|Blade)\.exe$' -and $_.CommandLine -notmatch 'ide serve' }
"$(Get-Date -Format s) AUDIT stray processes: $(($procs | ForEach-Object { $_.Name }) -join ',')" | Add-Content $log
powershell -NoProfile -ExecutionPolicy Bypass -File "$AB\driver-w2.ps1" *> "$GD\driver.txt"
"$(Get-Date -Format s) AB DONE exit=$LASTEXITCODE" | Add-Content $log
Set-Location $AB
C:\Python314\python.exe -I analyze-w2.py *> "$GD\analyze.txt"
"$(Get-Date -Format s) ANALYZE $(Get-Content "$GD\analyze.txt" | Select-Object -Last 1)" | Add-Content $log
"ALL DONE" | Add-Content $log
