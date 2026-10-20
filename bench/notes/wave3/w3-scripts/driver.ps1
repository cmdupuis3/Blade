# Wave-3 harness A/B: w2 (OLD binary + original sources) vs w3 (wave-3 tip) vs knob arms w3a / w3g. Interleaved
# per cell; the anti_g cells are repeated three times (the task's "3 interleaved sessions each"). Box quiet.
$S = 'C:\bt_w3\ab'
$env:PATH = 'C:\msys64\ucrt64\bin;C:\Users\cdupu\tools\OpenBLAS-0.3.34-x64\bin;' + $env:PATH
$env:OPENBLAS_DIR = 'C:\Users\cdupu\tools\OpenBLAS-0.3.34-x64'
$cells = @(
 # kernel, regimes, mirrors (run order; OLD first so a box-state drift shows against it)
 @('recur',   'mt8-licensed-native,mt8-repro-native',       @('w2','w3','w3a')),   # E-G5: the NEW mt8 arms (w2 = no Blade arm; w3a = admission off on the new source)
 @('recur',   'serial-licensed-native,serial-repro-native', @('w2','w3','w3a')),   # controls: the serial emission (region = threads only); w3a serial = per-step copies
 @('stencil', 'mt8-licensed-native,mt8-repro-native',       @('w2','w3')),         # item 2: the read-back licence (licensed: Path B; repro: serial, G48)
 @('stencil', 'serial-licensed-native',                     @('w2','w3')),         # control
 @('antisym', 'serial-licensed-native,mt8-licensed-native', @('w3','w3g')),        # item 4: anti_g 1 vs auto, session 1
 @('gram',    'serial-licensed-native',                     @('w2','w3')),         # control
 @('antisym', 'serial-licensed-native,mt8-licensed-native', @('w3','w3g')),        # item 4, session 2
 @('antisym', 'serial-licensed-native,mt8-licensed-native', @('w3','w3g'))         # item 4, session 3
)
$log = "$S\ab-progress.txt"
foreach ($c in $cells) { foreach ($m in $c[2]) {
  Set-Location "C:\bt_r2ab\$m"
  $ld = (Get-Counter '\Processor(_Total)\% Processor Time' -SampleInterval 2 -MaxSamples 5 | % { $_.CounterSamples } | Measure-Object CookedValue -Average).Average
  $t0 = Get-Date
  "$($t0.ToString('s')) START $($c[0]) [$($c[1])] $m load_before=$([math]::Round($ld,1))%" | Add-Content $log
  $f = "$S\bench-$($c[0])-$($c[1].Split('-')[0])-$m-$($t0.ToString('HHmmss')).txt"
  & C:\Python314\python.exe bench\harness\run.py bench --kernels $c[0] --regimes $c[1] *> $f
  "$((Get-Date).ToString('s')) END $($c[0]) $m exit $LASTEXITCODE (0 clean, 3 flags only, 1 failure) wall=$([math]::Round(((Get-Date)-$t0).TotalMinutes,1))min" | Add-Content $log
} }
"ALL DONE" | Add-Content $log
