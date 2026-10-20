# Wave-2 harness A/B: w1 (6b3e5293) vs w2 (wave-2 tip) vs knob-off arms w2a / w2b. Interleaved per cell. Box quiet.
$S = 'C:\Users\cdupu\AppData\Local\Temp\claude\C--Users-cdupu--claude-spares-spare-dUbXep\dbd3f5e5-01ed-41dd-bc92-4bc0ec85152b\scratchpad\round2\gates\w2'
$env:PATH = 'C:\msys64\ucrt64\bin;C:\Users\cdupu\tools\OpenBLAS-0.3.34-x64\bin;' + $env:PATH
$env:OPENBLAS_DIR = 'C:\Users\cdupu\tools\OpenBLAS-0.3.34-x64'
$cells = @(
 # kernel, regimes, mirrors (in run order; w1 first so a box-state drift shows against the control)
 @('pairkernel', 'serial-repro-native,mt8-repro-native',       @('w1','w2','w2a')),   # A: ordered scatter (serial); mt8 = G35 row slots, scatter wavefront only
 @('comoment3',  'serial-repro-native,mt8-repro-native',       @('w1','w2','w2a')),   # A: ordered scatter rank 3
 @('comoment3',  'serial-licensed-native,mt8-licensed-native', @('w1','w2','w2b')),   # D3b: tri_edge 1 vs 0 (w2 vs w2b); w1 = before
 @('antisym',    'serial-licensed-native,mt8-licensed-native', @('w1','w2','w2b')),   # D2: anti_g default 1 (w2) vs auto (w2b) vs before (w1)
 @('onepass',    'mt8-licensed-native',                        @('w1','w2','w2b')),   # G7: the new mt8 arm; w1 = OLD binary with the new spelling (no join team)
 @('dot',        'mt8-licensed-native',                        @('w1','w2','w2b')),   # G7 side effect on an existing mt8 arm (Codex item 7)
 @('fusedchain', 'mt8-licensed-native',                        @('w1','w2','w2b')),
 @('gram',       'serial-licensed-native',                     @('w1','w2'))          # control
)
$log = "$S\ab-progress.txt"
foreach ($c in $cells) { foreach ($m in $c[2]) {
  Set-Location "C:\bt_r2ab\$m"
  $ld = (Get-Counter '\Processor(_Total)\% Processor Time' -SampleInterval 2 -MaxSamples 5 | % { $_.CounterSamples } | Measure-Object CookedValue -Average).Average
  $t0 = Get-Date
  "$($t0.ToString('s')) START $($c[0]) [$($c[1])] $m load_before=$([math]::Round($ld,1))%" | Add-Content $log
  $f = "$S\bench-$($c[0])-$($c[1].Split('-')[1])-$m.txt"
  & C:\Python314\python.exe bench\harness\run.py bench --kernels $c[0] --regimes $c[1] *> $f
  "$((Get-Date).ToString('s')) END $($c[0]) $m exit $LASTEXITCODE (0 clean, 3 flags only, 1 failure) wall=$([math]::Round(((Get-Date)-$t0).TotalMinutes,1))min" | Add-Content $log
} }
"ALL DONE" | Add-Content $log
