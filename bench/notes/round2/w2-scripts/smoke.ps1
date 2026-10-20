param([string[]]$Mirrors = @('w1','w2','w2a','w2b'))
# Wave-2 smoke evidence, only the (kernel, regime) pairs the A/B benches; one mirror at a time, box quiet.
$S = 'C:\Users\cdupu\AppData\Local\Temp\claude\C--Users-cdupu--claude-spares-spare-dUbXep\dbd3f5e5-01ed-41dd-bc92-4bc0ec85152b\scratchpad\round2\gates\w2'
$env:PATH = 'C:\msys64\ucrt64\bin;C:\Users\cdupu\tools\OpenBLAS-0.3.34-x64\bin;' + $env:PATH
$env:OPENBLAS_DIR = 'C:\Users\cdupu\tools\OpenBLAS-0.3.34-x64'
$SR = 'serial-repro-native,mt8-repro-native'
$SL = 'serial-licensed-native,mt8-licensed-native'
$plan = @(
  # mirror, kernels, regimes
  @('w1',  'onepass',                 $SL),                    # the only kernel whose source changed since 6b3e5293 (evidence for the rest copied from C:\bt_r2ab\new)
  @('w1',  'pairkernel,comoment3,antisym,dot,fusedchain,gram', 'all'),  # re-smoke anyway if the config hash moved (cheap insurance; harness skips nothing, so this is the full cost)
  @('w2',  'pairkernel,comoment3',    $SR),
  @('w2',  'comoment3,antisym,onepass,dot,fusedchain,gram', $SL),
  @('w2a', 'pairkernel,comoment3',    $SR),
  @('w2b', 'comoment3,antisym,onepass,dot,fusedchain', $SL)
)
$log = "$S\smoke-progress.txt"
foreach ($p in $plan) {
  $m = $p[0]; $k = $p[1]; $r = $p[2]
  if ($Mirrors -notcontains $m) { continue }
  Set-Location "C:\bt_r2ab\$m"
  $t0 = Get-Date
  $f = "$S\smoke-$m-$($k.Split(',')[0])-$($r.Split(',')[0].Split('-')[0]).txt"
  & C:\Python314\python.exe bench\harness\run.py smoke --kernels $k --regimes $r *> $f
  "$((Get-Date).ToString('s')) $m [$k] [$r] exit $LASTEXITCODE wall=$([math]::Round(((Get-Date)-$t0).TotalMinutes,1))min -> $f" | Add-Content $log
}
"SMOKE DONE" | Add-Content $log
