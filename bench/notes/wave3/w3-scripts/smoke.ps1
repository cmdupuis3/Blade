param([string[]]$Mirrors = @('w2','w3','w3a','w3g'))
# Wave-3 smoke evidence, only the (kernel, regime) pairs the A/B benches; one mirror at a time, box quiet.
# (The harness refuses `bench` without smoke evidence for the current Blade.dll hash + source + config.)
$S = 'C:\bt_w3\ab'
$env:PATH = 'C:\msys64\ucrt64\bin;C:\Users\cdupu\tools\OpenBLAS-0.3.34-x64\bin;' + $env:PATH
$env:OPENBLAS_DIR = 'C:\Users\cdupu\tools\OpenBLAS-0.3.34-x64'
$plan = @(
  # mirror, kernels, regimes
  @('w2',  'recur,stencil', 'all'),                                      # OLD binary, ORIGINAL sources (recur was never benched in wave 2)
  @('w2',  'gram',          'serial-licensed-native'),                   # control (evidence may already exist; cheap)
  @('w3',  'recur,stencil', 'all'),
  @('w3',  'antisym',       'serial-licensed-native,mt8-licensed-native'),
  @('w3',  'gram',          'serial-licensed-native'),
  @('w3a', 'recur',         'all'),
  @('w3g', 'antisym',       'serial-licensed-native,mt8-licensed-native')
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
