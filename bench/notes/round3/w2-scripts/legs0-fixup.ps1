# Wave-2 A/B fix-up: the r3w2legs0 saxpy block was refused (no smoke evidence for the mt8 regimes -- the smoke plan covered serial only).
# Smoke the two mt8 pairs on r3w2legs0, re-bench saxpy there in all four regimes, re-run the analysis. Run only when the chain is DONE.
$S = 'C:\Users\cdupu\AppData\Local\Temp\claude\C--Users-cdupu--claude-spares-spare-dUbXep\dbd3f5e5-01ed-41dd-bc92-4bc0ec85152b\scratchpad\round3\gates\ab'
$env:PATH = 'C:\msys64\ucrt64\bin;C:\Users\cdupu\tools\OpenBLAS-0.3.34-x64\bin;' + $env:PATH
$env:OPENBLAS_DIR = 'C:\Users\cdupu\tools\OpenBLAS-0.3.34-x64'
$log = "$S\ab-progress-w2.txt"
Set-Location C:\bt_r2ab\r3w2legs0
$t0 = Get-Date
& C:\Python314\python.exe bench\harness\run.py smoke --kernels saxpy --regimes mt8-licensed-native,mt8-repro-native *> "$S\smoke-r3w2legs0-saxpy-mt8.txt"
"$((Get-Date).ToString('s')) FIXUP smoke r3w2legs0 saxpy mt8 exit $LASTEXITCODE wall=$([math]::Round(((Get-Date)-$t0).TotalMinutes,1))min" | Add-Content $log
if ($LASTEXITCODE -ne 0) { "FIXUP SMOKE FAILED" | Add-Content $log; exit 3 }
$ld = (Get-Counter '\Processor(_Total)\% Processor Time' -SampleInterval 2 -MaxSamples 5 | % { $_.CounterSamples } | Measure-Object CookedValue -Average).Average
$t0 = Get-Date
"$($t0.ToString('s')) START saxpy [serial-repro-native,serial-licensed-native,mt8-repro-native,mt8-licensed-native] r3w2legs0 (fix-up) load_before=$([math]::Round($ld,1))%" | Add-Content $log
$f = "$S\bench-saxpy-r3w2legs0-$($t0.ToString('HHmmss')).txt"
& C:\Python314\python.exe bench\harness\run.py bench --kernels saxpy --regimes serial-repro-native,serial-licensed-native,mt8-repro-native,mt8-licensed-native *> $f
"$((Get-Date).ToString('s')) END saxpy r3w2legs0 (fix-up) exit $LASTEXITCODE (0 clean, 3 flags only, 1 failure) wall=$([math]::Round(((Get-Date)-$t0).TotalMinutes,1))min" | Add-Content $log
Set-Location $S
C:\Python314\python.exe -I analyze-w2.py *> "$S\..\w2-gates\analyze-fixup.txt"
"$((Get-Date).ToString('s')) FIXUP ANALYZE $(Get-Content "$S\..\w2-gates\analyze-fixup.txt" | Select-Object -Last 1)" | Add-Content $log
"FIXUP DONE" | Add-Content $log
