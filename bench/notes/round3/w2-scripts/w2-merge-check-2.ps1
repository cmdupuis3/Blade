# Round-3 wave-2 merge check 2: after W2-1 lands on top of W2-5 + W2-0 + W2-3, build and run the blocks W2-1 touches plus the run-record / scatter blocks it shares with W2-3.
$ErrorActionPreference = 'Continue'
$WT = 'C:\Users\cdupu\Documents\GitHub\Blade\.claude\worktrees\bench-fortran'
$BIN = "$WT\bin\Release\net10.0"
$S = 'C:\Users\cdupu\AppData\Local\Temp\claude\C--Users-cdupu--claude-spares-spare-dUbXep\dbd3f5e5-01ed-41dd-bc92-4bc0ec85152b\scratchpad\round3\gates'
$GD = "$S\w2-merge-2"; New-Item -ItemType Directory -Force $GD | Out-Null
$env:PATH = 'C:\msys64\ucrt64\bin;C:\Users\cdupu\tools\OpenBLAS-0.3.34-x64\bin;' + $env:PATH
$env:OPENBLAS_DIR = 'C:\Users\cdupu\tools\OpenBLAS-0.3.34-x64'
$log = "$S\w2-merge-check-2.txt"
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
  @('sql', @('test','sql')), @('index-types', @('test','index-types')), @('optimize', @('test','optimize')), @('cli', @('test','cli')),
  @('run-record', @('test','run-record')), @('orbit-scatter', @('test','orbit-scatter')), @('omp-coverage', @('test','omp-coverage')),
  @('subst', @('test','subst')), @('corpus-wiring', @('test','corpus-wiring')), @('docs', @('test','docs')),
  @('llvm-facts', @('test','llvm','facts')), @('llvm-shim', @('test','llvm','shim')),
  @('llvm-sql-unique-contains', @('test','llvm','sql-unique-contains')), @('llvm-sql-masks', @('test','llvm','sql-masks')), @('llvm-sql-semijoins', @('test','llvm','sql-semijoins')),
  @('llvm-symmetry', @('test','llvm','symmetry')),
  @('interp-sql-unique-contains', @('test','interp','sql-unique-contains')), @('interp-sql-masks', @('test','interp','sql-masks')), @('interp-sql-semijoins', @('test','interp','sql-semijoins')),
  @('opt-diff-sql-unique-contains', @('test','opt-diff','sql-unique-contains')), @('opt-diff-sql-masks', @('test','opt-diff','sql-masks')), @('opt-diff-sql-semijoins', @('test','opt-diff','sql-semijoins'))
)
foreach ($rn in $runs) {
  $nm = $rn[0]; $ar = $rn[1]; $t0 = Get-Date
  & $B @ar *> "$GD\$nm.txt"
  $foot = (Get-Content "$GD\$nm.txt" | Select-String -Pattern 'passed|PASS|FAIL' | Select-Object -Last 2 | ForEach-Object { $_.Line.Trim() }) -join ' || '
  "$(Get-Date -Format s) BLOCK $nm exit=$LASTEXITCODE wall_min=$([math]::Round(((Get-Date)-$t0).TotalMinutes,2)) :: $foot" | Add-Content $log
}
"ALL DONE" | Add-Content $log
