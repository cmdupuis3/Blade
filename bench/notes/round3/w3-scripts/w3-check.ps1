# Light check after a wave-3 item merge: build + the item's blocks (no five-gate run: the user's call for the tail of a round).
# Usage: powershell -File w3-check.ps1 -Item W3-2   (items: W3-2, W3-4, W3-1, W2-4)
param([Parameter(Mandatory=$true)][string]$Item)
$ErrorActionPreference = 'Continue'
$WT = 'C:\Users\cdupu\Documents\GitHub\Blade\.claude\worktrees\bench-fortran'
$BIN = "$WT\bin\Release\net10.0"
$S = 'C:\Users\cdupu\AppData\Local\Temp\claude\C--Users-cdupu--claude-spares-spare-dUbXep\dbd3f5e5-01ed-41dd-bc92-4bc0ec85152b\scratchpad\round3\gates'
$GD = "$S\w3-check-$Item"; New-Item -ItemType Directory -Force $GD | Out-Null
$env:PATH = 'C:\msys64\ucrt64\bin;C:\Users\cdupu\tools\OpenBLAS-0.3.34-x64\bin;' + $env:PATH
$env:OPENBLAS_DIR = 'C:\Users\cdupu\tools\OpenBLAS-0.3.34-x64'
$log = "$S\w3-check-$Item.txt"
$blocks = @{
  'W3-2' = @(@('sql',@('test','sql')), @('optimize',@('test','optimize')), @('cli',@('test','cli')), @('index-types',@('test','index-types')),
             @('corpus-wiring',@('test','corpus-wiring')), @('docs',@('test','docs')), @('interp-sql-joins',@('test','interp','sql-joins')),
             @('opt-diff-sql-joins',@('test','opt-diff','sql-joins')), @('llvm-sql-joins',@('test','llvm','sql-joins')))
  'W3-4' = @(@('symmetry',@('test','symmetry')), @('orbit-scatter',@('test','orbit-scatter')), @('optimize',@('test','optimize')), @('cli',@('test','cli')),
             @('corpus-wiring',@('test','corpus-wiring')), @('docs',@('test','docs')), @('interp-symmetry',@('test','interp','symmetry')),
             @('opt-diff-symmetry',@('test','opt-diff','symmetry')), @('llvm-symmetry',@('test','llvm','symmetry')), @('omp-reduce',@('test','omp-reduce')))
  'W3-1' = @(@('sql',@('test','sql')), @('optimize',@('test','optimize')), @('cli',@('test','cli')), @('corpus-wiring',@('test','corpus-wiring')),
             @('docs',@('test','docs')), @('interp-sql-group-by',@('test','interp','sql-group-by')), @('opt-diff-sql-group-by',@('test','opt-diff','sql-group-by')),
             @('llvm-sql-group-by',@('test','llvm','sql-group-by')))
  'W2-4' = @(@('sibling-folds',@('test','sibling-folds')), @('cell-epilogue',@('test','cell-epilogue')), @('optimize',@('test','optimize')), @('cli',@('test','cli')),
             @('corpus-wiring',@('test','corpus-wiring')), @('docs',@('test','docs')), @('llvm-facts',@('test','llvm','facts')),
             @('llvm-sibling-folds',@('test','llvm','sibling-folds')), @('interp-sibling-folds',@('test','interp','sibling-folds')),
             @('opt-diff-sibling-folds',@('test','opt-diff','sibling-folds')), @('run-record',@('test','run-record')), @('surface',@('test','surface')), @('diagnostics',@('test','diagnostics')))
  'W4-1' = @(@('tuples',@('test','tuples')), @('loops',@('test','loops')), @('cli',@('test','cli')), @('docs',@('test','docs')), @('corpus-wiring',@('test','corpus-wiring')), @('interp-loops',@('test','interp','loops')), @('llvm-loops',@('test','llvm','loops')), @('opt-diff-loops',@('test','opt-diff','loops')),
             @('interp-tuples',@('test','interp','tuples')), @('llvm-tuples',@('test','llvm','tuples')), @('opt-diff-tuples',@('test','opt-diff','tuples')))
  'W4-2' = @(@('functions',@('test','functions')), @('memfree',@('test','memfree')), @('omp-reduce',@('test','omp-reduce')), @('omp-coverage',@('test','omp-coverage')), @('cell-epilogue',@('test','cell-epilogue')), @('index-types',@('test','index-types')), @('optimize',@('test','optimize')), @('orbit-scatter',@('test','orbit-scatter')),
             @('symmetry',@('test','symmetry')), @('cli',@('test','cli')), @('docs',@('test','docs')), @('corpus-wiring',@('test','corpus-wiring')),
             @('interp-functions',@('test','interp','functions')), @('llvm-functions',@('test','llvm','functions')), @('opt-diff-functions',@('test','opt-diff','functions')),
             @('interp-memfree',@('test','interp','memfree')), @('llvm-memfree',@('test','llvm','memfree')), @('opt-diff-memfree',@('test','opt-diff','memfree')), @('interp-index-types',@('test','interp','index-types')), @('llvm-index-types',@('test','llvm','index-types')), @('sql-group-by',@('test','sql-group-by')))
  'W4-1b' = @(@('tuples',@('test','tuples')), @('corpus-wiring',@('test','corpus-wiring')))
  'W4-3' = @(@('orbit-scatter',@('test','orbit-scatter')), @('omp-reduce',@('test','omp-reduce')), @('symmetry',@('test','symmetry')), @('fold-lanes',@('test','fold-lanes')),
             @('cli',@('test','cli')), @('docs',@('test','docs')), @('corpus-wiring',@('test','corpus-wiring')), @('interp-symmetry',@('test','interp','symmetry')),
             @('llvm-symmetry',@('test','llvm','symmetry')), @('llvm-tiles',@('test','llvm','tiles')), @('opt-diff-symmetry',@('test','opt-diff','symmetry')),
             @('functions',@('test','functions')), @('basic',@('test','basic')), @('sql-reduce',@('test','sql-reduce')), @('loops',@('test','loops')),
             @('interp-functions',@('test','interp','functions')), @('interp-sql-reduce',@('test','interp','sql-reduce')), @('llvm-functions',@('test','llvm','functions')),
             @('optimize',@('test','optimize')), @('cell-epilogue',@('test','cell-epilogue')), @('sibling-folds',@('test','sibling-folds')), @('guards',@('test','guards')))
  'W4-4' = @(@('access',@('test','access')), @('ad',@('test','ad')), @('ad-jvp-comb',@('test','ad-jvp-comb')), @('cli',@('test','cli')), @('docs',@('test','docs')), @('corpus-wiring',@('test','corpus-wiring')),
             @('interp-ad',@('test','interp','ad')), @('llvm-ad',@('test','llvm','ad')), @('opt-diff-ad',@('test','opt-diff','ad')))
}
if (-not $blocks.ContainsKey($Item)) { throw "unknown item $Item" }
Set-Location $WT
"$(Get-Date -Format s) START item=$Item tip=$(git rev-parse --short HEAD)" | Set-Content $log
$b = dotnet build Blade.fsproj -c Release 2>&1 | Select-String -Pattern " error |warning|Warning\(s\)|Error\(s\)" | ForEach-Object { $_.Line.Substring(0, [Math]::Min(240, $_.Line.Length)) }
"$(Get-Date -Format s) BUILD $($b -join ' / ')" | Add-Content $log
if (($b -join ' ') -notmatch '0 Error') { "BUILD FAILED" | Add-Content $log; exit 1 }
Set-Location C:\bt_r2w1
$B = "$BIN\Blade.exe"
foreach ($rn in $blocks[$Item]) {
  $nm = $rn[0]; $ar = $rn[1]; $t0 = Get-Date
  & $B @ar *> "$GD\$nm.txt"
  $foot = (Get-Content "$GD\$nm.txt" | Select-String -Pattern 'passed|PASS|FAIL' | Select-Object -Last 2 | ForEach-Object { $_.Line.Trim() }) -join ' || '
  "$(Get-Date -Format s) BLOCK $nm exit=$LASTEXITCODE wall_min=$([math]::Round(((Get-Date)-$t0).TotalMinutes,2)) :: $foot" | Add-Content $log
}
"ALL DONE" | Add-Content $log
