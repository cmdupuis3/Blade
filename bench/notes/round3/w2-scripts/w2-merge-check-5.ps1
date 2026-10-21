# Round-3 wave-2 merge check 5: T4c on top of W2-6 + W2-4 -- the blocks T4c touches plus the shared ones.
$ErrorActionPreference = 'Continue'
$WT = 'C:\Users\cdupu\Documents\GitHub\Blade\.claude\worktrees\bench-fortran'
$BIN = "$WT\bin\Release\net10.0"
$S = 'C:\Users\cdupu\AppData\Local\Temp\claude\C--Users-cdupu--claude-spares-spare-dUbXep\dbd3f5e5-01ed-41dd-bc92-4bc0ec85152b\scratchpad\round3\gates'
$GD = "$S\w2-merge-5"; New-Item -ItemType Directory -Force $GD | Out-Null
$env:PATH = 'C:\msys64\ucrt64\bin;C:\Users\cdupu\tools\OpenBLAS-0.3.34-x64\bin;' + $env:PATH
$env:OPENBLAS_DIR = 'C:\Users\cdupu\tools\OpenBLAS-0.3.34-x64'
$log = "$S\w2-merge-check-5.txt"
Set-Location $WT
$tip = (git rev-parse --short HEAD)
"$(Get-Date -Format s) START tip=$tip" | Set-Content $log
$b = dotnet build Blade.fsproj -c Release 2>&1 | Select-String -Pattern " error |warning|Warning\(s\)|Error\(s\)" | ForEach-Object { $_.Line.Substring(0, [Math]::Min(240, $_.Line.Length)) }
$b | Set-Content "$GD\build.txt"
"$(Get-Date -Format s) BUILD $($b -join ' / ') hash=$((Get-FileHash "$BIN\Blade.dll").Hash)" | Add-Content $log
if (($b -join ' ') -notmatch '0 Error') { "BUILD FAILED" | Add-Content $log; exit 1 }
Set-Location C:\bt_r2w1
$B = "$BIN\Blade.exe"
$runs = @(
  @('index-types', @('test','index-types')), @('sql', @('test','sql')), @('optimize', @('test','optimize')), @('cli', @('test','cli')),
  @('run-record', @('test','run-record')), @('corpus-wiring', @('test','corpus-wiring')), @('docs', @('test','docs')),
  @('llvm-facts', @('test','llvm','facts')), @('llvm-index-types', @('test','llvm','index-types')),
  @('interp-index-types', @('test','interp','index-types')), @('opt-diff-index-types', @('test','opt-diff','index-types'))
)
foreach ($rn in $runs) {
  $nm = $rn[0]; $ar = $rn[1]; $t0 = Get-Date
  & $B @ar *> "$GD\$nm.txt"
  $foot = (Get-Content "$GD\$nm.txt" | Select-String -Pattern 'passed|PASS|FAIL' | Select-Object -Last 2 | ForEach-Object { $_.Line.Trim() }) -join ' || '
  "$(Get-Date -Format s) BLOCK $nm exit=$LASTEXITCODE wall_min=$([math]::Round(((Get-Date)-$t0).TotalMinutes,2)) :: $foot" | Add-Content $log
}
"ALL DONE" | Add-Content $log
