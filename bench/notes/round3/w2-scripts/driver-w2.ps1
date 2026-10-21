# Round-3 wave-2 harness A/B. Interleaved per cell. Box quiet (audit Get-Process first: no builds, no tests, no stray find).
$S = 'C:\Users\cdupu\AppData\Local\Temp\claude\C--Users-cdupu--claude-spares-spare-dUbXep\dbd3f5e5-01ed-41dd-bc92-4bc0ec85152b\scratchpad\round3\gates\ab'
$env:PATH = 'C:\msys64\ucrt64\bin;C:\Users\cdupu\tools\OpenBLAS-0.3.34-x64\bin;' + $env:PATH
$env:OPENBLAS_DIR = 'C:\Users\cdupu\tools\OpenBLAS-0.3.34-x64'
$cells = @(
 # kernel, regimes, mirrors (run order)
 @('pairkernel',  'mt8-repro-native,serial-repro-native',  @('r3w1','r3w2','r3w2prefer','r3w2pull0')),   # W2-0a precedence (scatter default) + W2-3 pull (rank 2 here: pull falls through; the control)
 @('comoment3',   'mt8-repro-native,serial-repro-native',  @('r3w1','r3w2','r3w2prefer','r3w2pull0')),   # W2-3 pull (rank 3) vs serial scatter (pull0) vs row slots (prefer); serial = control
 @('antisym',     'serial-licensed-native,mt8-licensed-native', @('r3w1','r3w2','r3w2antig1','r3w2','r3w2antig1','r3w2','r3w2antig1')),  # W2-0b per-regime anti_g: 3 sessions each + the old binary once
 @('saxpy',       'serial-repro-native,serial-licensed-native,mt8-repro-native,mt8-licensed-native', @('r3w1','r3w2','r3w2legs0')),  # W2-4 legs (serial cells); mt8 = decline control
 @('haloadjoint', 'serial-licensed-native,mt8-licensed-native,serial-repro-native,mt8-repro-native', @('r3w1','r3w2')),  # new kernel N02: first measurement
 @('stencil',     'mt8-repro-native,mt8-licensed-native',  @('r3w1','r3w2','r3w2retain')),   # control for the precedence change (row slots unaffected) + the A4 retention arm (24 MB lap)
 @('recur',       'mt8-licensed-native,mt8-repro-native',  @('r3w1','r3w2','r3w2retain')),   # W2-6 / A4 retention arm (48 MB trajectory): time AND peak working set per arm
 @('gram',        'serial-licensed-native',                @('r3w1','r3w2'))    # control
)
$log = "$S\ab-progress-w2.txt"
foreach ($c in $cells) { foreach ($m in $c[2]) {
  Set-Location "C:\bt_r2ab\$m"
  $ld = (Get-Counter '\Processor(_Total)\% Processor Time' -SampleInterval 2 -MaxSamples 5 | % { $_.CounterSamples } | Measure-Object CookedValue -Average).Average
  $t0 = Get-Date
  "$($t0.ToString('s')) START $($c[0]) [$($c[1])] $m load_before=$([math]::Round($ld,1))%" | Add-Content $log
  $f = "$S\bench-$($c[0])-$m-$($t0.ToString('HHmmss')).txt"
  & C:\Python314\python.exe bench\harness\run.py bench --kernels $c[0] --regimes $c[1] *> $f
  "$((Get-Date).ToString('s')) END $($c[0]) $m exit $LASTEXITCODE (0 clean, 3 flags only, 1 failure) wall=$([math]::Round(((Get-Date)-$t0).TotalMinutes,1))min" | Add-Content $log
} }
"ALL DONE" | Add-Content $log
