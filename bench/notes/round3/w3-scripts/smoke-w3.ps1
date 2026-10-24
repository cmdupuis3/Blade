param([string[]]$Mirrors = @('r3w1','r3w2bin','r3w3','r3w3t3off','r3w3t3lic','r3w3t2off','r3w3strict','r3w3legs1'))
# Round-3 wave-3 smoke evidence: only the (kernel, regime) pairs the A/B benches; one mirror at a time, box quiet.
# DRAFT: the kernel regimes are confirmed against each merged kernel.json before the chain runs.
$S = 'C:\Users\cdupu\AppData\Local\Temp\claude\C--Users-cdupu--claude-spares-spare-dUbXep\dbd3f5e5-01ed-41dd-bc92-4bc0ec85152b\scratchpad\round3\gates\ab'
$env:PATH = 'C:\msys64\ucrt64\bin;C:\Users\cdupu\tools\OpenBLAS-0.3.34-x64\bin;' + $env:PATH
$env:OPENBLAS_DIR = 'C:\Users\cdupu\tools\OpenBLAS-0.3.34-x64'
$SER = 'serial-licensed-native,serial-repro-native'
$ALL = 'serial-licensed-native,mt8-licensed-native,serial-repro-native,mt8-repro-native'
$plan = @(
  @('r3w1',       'saxpy',                 $ALL),
  @('r3w2bin',    'groupagg,groupagg_dyn,hashjoin',     $SER),
  @('r3w2bin',    'pairaction',            $ALL),
  @('r3w2bin',    'pairkernel,comoment3',  'mt8-repro-native'),
  @('r3w2bin',    'gram',                  'serial-licensed-native'),
  @('r3w3',       'groupagg,groupagg_dyn,hashjoin',     $SER),
  @('r3w3',       'pairaction',            $ALL),
  @('r3w3',       'pairkernel,comoment3',  'mt8-repro-native'),
  @('r3w3',       'gram',                  'serial-licensed-native'),
  @('r3w3',       'saxpy',                 $ALL),
  @('r3w3t3off',  'groupagg,groupagg_dyn', $SER),
  @('r3w3t3lic',  'groupagg,groupagg_dyn', 'serial-licensed-native'),
  @('r3w3t2off',  'hashjoin',              $SER),
  @('r3w3strict', 'pairaction',            $ALL),
  @('r3w3legs1',  'saxpy',                 $ALL)
)
$log = "$S\smoke-progress-w3.txt"
foreach ($p in $plan) {
  $m = $p[0]; $k = $p[1]; $r = $p[2]
  if ($Mirrors -notcontains $m) { continue }
  Set-Location "C:\bt_r2ab\$m"
  $t0 = Get-Date
  $f = "$S\smoke-w3-$m-$($k.Split(',')[0]).txt"
  & C:\Python314\python.exe bench\harness\run.py smoke --kernels $k --regimes $r *> $f
  "$((Get-Date).ToString('s')) $m [$k] [$r] exit $LASTEXITCODE wall=$([math]::Round(((Get-Date)-$t0).TotalMinutes,1))min -> $f" | Add-Content $log
}
"SMOKE DONE" | Add-Content $log
