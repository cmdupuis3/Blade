param([string[]]$Mirrors = @('r3w1','r3w2','r3w2prefer','r3w2pull0','r3w2antig1','r3w2legs0','r3w2retain'))
# Round-3 wave-2 smoke evidence: only the (kernel, regime) pairs the A/B benches; one mirror at a time, box quiet.
$S = 'C:\Users\cdupu\AppData\Local\Temp\claude\C--Users-cdupu--claude-spares-spare-dUbXep\dbd3f5e5-01ed-41dd-bc92-4bc0ec85152b\scratchpad\round3\gates\ab'
$env:PATH = 'C:\msys64\ucrt64\bin;C:\Users\cdupu\tools\OpenBLAS-0.3.34-x64\bin;' + $env:PATH
$env:OPENBLAS_DIR = 'C:\Users\cdupu\tools\OpenBLAS-0.3.34-x64'
$SR = 'serial-repro-native,mt8-repro-native'
$SL = 'serial-licensed-native,mt8-licensed-native'
$SER = 'serial-licensed-native,serial-repro-native'
$ALL = 'serial-licensed-native,mt8-licensed-native,serial-repro-native,mt8-repro-native'
$SR_MT8 = 'mt8-licensed-native,mt8-repro-native'
$plan = @(
  @('r3w1',       'pairkernel,comoment3',     $SR),
  @('r3w1',       'antisym',                  $SL),
  @('r3w1',       'saxpy,haloadjoint',        $ALL),
  @('r3w1',       'gram',                     'serial-licensed-native'),
  @('r3w1',       'stencil,recur',            $SR_MT8),
  @('r3w2',       'pairkernel,comoment3',     $SR),
  @('r3w2',       'antisym',                  $SL),
  @('r3w2',       'saxpy,haloadjoint',        $ALL),
  @('r3w2',       'gram',                     'serial-licensed-native'),
  @('r3w2',       'stencil,recur',            $SR_MT8),
  @('r3w2prefer', 'pairkernel,comoment3',     $SR),
  @('r3w2pull0',  'pairkernel,comoment3',     $SR),
  @('r3w2antig1', 'antisym',                  $SL),
  @('r3w2legs0',  'saxpy',                    $SER),
  @('r3w2retain', 'stencil,recur',            $SR_MT8)
)
$log = "$S\smoke-progress-w2.txt"
foreach ($p in $plan) {
  $m = $p[0]; $k = $p[1]; $r = $p[2]
  if ($Mirrors -notcontains $m) { continue }
  Set-Location "C:\bt_r2ab\$m"
  $t0 = Get-Date
  $f = "$S\smoke-$m-$($k.Split(',')[0]).txt"
  & C:\Python314\python.exe bench\harness\run.py smoke --kernels $k --regimes $r *> $f
  "$((Get-Date).ToString('s')) $m [$k] [$r] exit $LASTEXITCODE wall=$([math]::Round(((Get-Date)-$t0).TotalMinutes,1))min -> $f" | Add-Content $log
}
"SMOKE DONE" | Add-Content $log
