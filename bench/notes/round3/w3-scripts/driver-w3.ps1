# Round-3 wave-3 harness A/B. Interleaved per cell. Box quiet (audit Get-Process first: no builds, no tests, no stray find).
# DRAFT: cells confirmed against the merged kernel.json files before the chain runs.
$S = 'C:\Users\cdupu\AppData\Local\Temp\claude\C--Users-cdupu--claude-spares-spare-dUbXep\dbd3f5e5-01ed-41dd-bc92-4bc0ec85152b\scratchpad\round3\gates\ab'
$env:PATH = 'C:\msys64\ucrt64\bin;C:\Users\cdupu\tools\OpenBLAS-0.3.34-x64\bin;' + $env:PATH
$env:OPENBLAS_DIR = 'C:\Users\cdupu\tools\OpenBLAS-0.3.34-x64'
$SER = 'serial-licensed-native,serial-repro-native'
$ALL = 'serial-licensed-native,mt8-licensed-native,serial-repro-native,mt8-repro-native'
$cells = @(
 # kernel, regimes, mirrors (run order)
 @('groupagg',   'serial-repro-native',    @('r3w2bin','r3w3','r3w3t3off')),       # W3-1 T3: the one pass applies under REPRO; knob-off = today's grouping
 @('groupagg',   'serial-licensed-native', @('r3w2bin','r3w3','r3w3t3lic')),       # W3-1: the default DECLINES in a licensed build (control) vs the experimental licensed arm
 @('groupagg_dyn', 'serial-repro-native',  @('r3w2bin','r3w3','r3w3t3off')),       # W3-1 dynamic keys (OLD binary's LLVM lane lacks the C-core discovery)
 @('groupagg_dyn', 'serial-licensed-native', @('r3w2bin','r3w3','r3w3t3lic')),     # W3-1 dynamic, licensed control + the experimental arm
 @('hashjoin',   $SER,                @('r3w2bin','r3w3','r3w3t2off')),            # W3-2 T2 pilot: stable CSR vs the six passes (OLD binary and knob-off)
 @('pairaction', $ALL,                @('r3w2bin','r3w3','r3w3strict')),           # W3-4 P2: orbit actions, default vs the strict arm; OLD binary = pre-item form
 @('pairkernel', 'mt8-repro-native',  @('r3w2bin','r3w3')),                        # P1 baseline (W3-3's prototype decision rides the quiet-box matrix, not this)
 @('saxpy',      $ALL,                @('r3w1','r3w3','r3w3legs1')),               # W2-4 deferred re-measure: wave-1 binary vs final default (joined pass) vs the OPT-IN legs arm, four regimes
 @('comoment3',  'mt8-repro-native',  @('r3w2bin','r3w3')),                        # control (the pull)
 @('gram',       'serial-licensed-native', @('r3w2bin','r3w3'))                    # control
)
$log = "$S\ab-progress-w3.txt"
foreach ($c in $cells) { foreach ($m in $c[2]) {
  Set-Location "C:\bt_r2ab\$m"
  $ld = (Get-Counter '\Processor(_Total)\% Processor Time' -SampleInterval 2 -MaxSamples 5 | % { $_.CounterSamples } | Measure-Object CookedValue -Average).Average
  $t0 = Get-Date
  "$($t0.ToString('s')) START $($c[0]) [$($c[1])] $m load_before=$([math]::Round($ld,1))%" | Add-Content $log
  $f = "$S\bench-w3-$($c[0])-$m-$($t0.ToString('HHmmss')).txt"
  & C:\Python314\python.exe bench\harness\run.py bench --kernels $c[0] --regimes $c[1] *> $f
  "$((Get-Date).ToString('s')) END $($c[0]) $m exit $LASTEXITCODE (0 clean, 3 flags only, 1 failure) wall=$([math]::Round(((Get-Date)-$t0).TotalMinutes,1))min" | Add-Content $log
} }
"ALL DONE" | Add-Content $log
