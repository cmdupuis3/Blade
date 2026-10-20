$S='C:\Users\cdupu\AppData\Local\Temp\claude\C--Users-cdupu--claude-spares-spare-dUbXep\dbd3f5e5-01ed-41dd-bc92-4bc0ec85152b\scratchpad\round2'
$WT='C:\Users\cdupu\Documents\GitHub\Blade\.claude\worktrees\bench-fortran'
$BIN="$WT\bin\Release\net10.0"
$env:PATH='C:\msys64\ucrt64\bin;C:\Users\cdupu\tools\OpenBLAS-0.3.34-x64\bin;'+$env:PATH
$env:OPENBLAS_DIR='C:\Users\cdupu\tools\OpenBLAS-0.3.34-x64'
Set-Location C:\bt_r2w1
$B="$BIN\Blade.exe"
$gates=@(@('default',@('test')),@('opt-diff',@('test','opt-diff')),@('docs',@('test','docs')),@('llvm',@('test','llvm')),@('interp',@('test','interp')))
foreach($g in $gates){
  $n=$g[0]; $a=$g[1]
  $t0=Get-Date
  "START $n $($t0.ToString('s'))" | Add-Content "$S\gates\w2\gate-timing.txt"
  & $B @a *> "$S\gates\w2\gate-$n.txt"
  $t1=Get-Date
  "END $n $($t1.ToString('s')) exit=$LASTEXITCODE wall_min=$([math]::Round(($t1-$t0).TotalMinutes,2)) hash=$((Get-FileHash "$BIN\Blade.dll").Hash)" | Add-Content "$S\gates\w2\gate-timing.txt"
}
"ALLDONE" | Add-Content "$S\gates\w2\gate-timing.txt"
