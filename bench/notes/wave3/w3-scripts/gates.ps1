# Wave-3 gates: the five suites from a private cwd (C:\bt_w3 with tests/ and docs/ junctions to the
# wave-3 worktree), one at a time, the NEW binary. Exit codes are 0 even with failures: gate on the
# TOTAL line and the `, N skipped` suffix (bench/notes/round2/wave-end-runbook.md).
param([string]$Out = "C:\bt_w3\gates")
$WT = "C:\Users\cdupu\Documents\GitHub\Blade\.claude\worktrees\magical-cannon-db55bf"
$BIN = "$WT\bin\Release\net10.0"
$env:PATH = "C:\msys64\ucrt64\bin;C:\Users\cdupu\tools\OpenBLAS-0.3.34-x64\bin;" + $env:PATH
$env:OPENBLAS_DIR = "C:\Users\cdupu\tools\OpenBLAS-0.3.34-x64"
New-Item -ItemType Directory -Force $Out | Out-Null
Set-Location C:\bt_w3
if (Test-Path "$BIN\blade.toolchain.json") { throw "blade.toolchain.json beside Blade.exe" }
$B = "$BIN\Blade.exe"
"hash $((Get-FileHash "$BIN\Blade.dll").Hash)" | Add-Content "$Out\gate-timing.txt"
$gates = @(@('default', @('test')), @('opt-diff', @('test', 'opt-diff')), @('docs', @('test', 'docs')), @('llvm', @('test', 'llvm')), @('interp', @('test', 'interp')))
foreach ($g in $gates) {
    $n = $g[0]; $a = $g[1]
    $t0 = Get-Date
    "START $n $($t0.ToString('s'))" | Add-Content "$Out\gate-timing.txt"
    & $B @a *> "$Out\gate-$n.txt"
    $t1 = Get-Date
    $total = (Select-String -Path "$Out\gate-$n.txt" -Pattern "TOTAL|Interp Diff:|Opt Diff:" | Select-Object -Last 1).Line
    "END $n $($t1.ToString('s')) exit=$LASTEXITCODE wall_min=$([math]::Round(($t1 - $t0).TotalMinutes, 2)) hash=$((Get-FileHash "$BIN\Blade.dll").Hash) :: $total" | Add-Content "$Out\gate-timing.txt"
}
"ALLDONE" | Add-Content "$Out\gate-timing.txt"
