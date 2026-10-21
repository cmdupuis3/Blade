# Wave-2 A/B extra arm (merge-level review finding 3): r3w2g35 = r3w2 + BLADE_ORBIT_PULL=0 + BLADE_FOLD_ROW_SLOTS=prefer,
# i.e. the G35 row slots on comoment3 mt8 repro (the `prefer` arm alone still pulls at rank 3). Mirror, smoke, one block, analyze.
$S = 'C:\Users\cdupu\AppData\Local\Temp\claude\C--Users-cdupu--claude-spares-spare-dUbXep\dbd3f5e5-01ed-41dd-bc92-4bc0ec85152b\scratchpad\round3\gates\ab'
$env:PATH = 'C:\msys64\ucrt64\bin;C:\Users\cdupu\tools\OpenBLAS-0.3.34-x64\bin;' + $env:PATH
$env:OPENBLAS_DIR = 'C:\Users\cdupu\tools\OpenBLAS-0.3.34-x64'
$log = "$S\ab-progress-w2.txt"
$root = 'C:\bt_r2ab\r3w2g35'
if (Test-Path $root) { throw "$root exists" }
New-Item -ItemType Directory -Force $root | Out-Null
robocopy 'C:\bt_r2ab\r3w2\bench' "$root\bench" /E /XD results .build __pycache__ /NFL /NDL /NJH /NJS /NP | Out-Null
New-Item -ItemType Directory -Force "$root\bench\results","$root\bench\.build" | Out-Null
robocopy 'C:\bt_r2ab\r3w2\bin' "$root\bin" /E /NFL /NDL /NJH /NJS /NP | Out-Null
$h = (Get-FileHash "$root\bin\Release\net10.0\Blade.dll").Hash
if ($h -ne (Get-FileHash 'C:\bt_r2ab\r3w2\bin\Release\net10.0\Blade.dll').Hash) { throw 'hash mismatch' }
$cfg = "$root\bench\harness\config.json"
$j = Get-Content $cfg -Raw | ConvertFrom-Json
$j.blade_knobs.all | Add-Member -NotePropertyName BLADE_ORBIT_PULL -NotePropertyValue '0' -Force
$j.blade_knobs.all | Add-Member -NotePropertyName BLADE_FOLD_ROW_SLOTS -NotePropertyValue 'prefer' -Force
$j | ConvertTo-Json -Depth 20 | Set-Content $cfg -Encoding utf8
"Round-3 wave-2 A/B mirror r3w2g35 made $(Get-Date -Format s): r3w2's bench and bin (Blade.dll $h); config = BLADE_ORBIT_PULL=0 BLADE_FOLD_ROW_SLOTS=prefer (the G35 row slots at rank 3: the pull off AND the old precedence)" | Set-Content "$root\README.txt"
"r3w2g35 ok $h" | Add-Content "$S\mirror-hashes-w2.txt"
Set-Location $root
$t0 = Get-Date
& C:\Python314\python.exe bench\harness\run.py smoke --kernels comoment3 --regimes mt8-repro-native *> "$S\smoke-r3w2g35-comoment3.txt"
"$((Get-Date).ToString('s')) G35ARM smoke r3w2g35 comoment3 mt8-repro exit $LASTEXITCODE wall=$([math]::Round(((Get-Date)-$t0).TotalMinutes,1))min" | Add-Content $log
if ($LASTEXITCODE -ne 0) { "G35ARM SMOKE FAILED" | Add-Content $log; exit 3 }
$ld = (Get-Counter '\Processor(_Total)\% Processor Time' -SampleInterval 2 -MaxSamples 5 | % { $_.CounterSamples } | Measure-Object CookedValue -Average).Average
$t0 = Get-Date
"$($t0.ToString('s')) START comoment3 [mt8-repro-native] r3w2g35 (G35 arm) load_before=$([math]::Round($ld,1))%" | Add-Content $log
$f = "$S\bench-comoment3-r3w2g35-$($t0.ToString('HHmmss')).txt"
& C:\Python314\python.exe bench\harness\run.py bench --kernels comoment3 --regimes mt8-repro-native *> $f
"$((Get-Date).ToString('s')) END comoment3 r3w2g35 (G35 arm) exit $LASTEXITCODE (0 clean, 3 flags only, 1 failure) wall=$([math]::Round(((Get-Date)-$t0).TotalMinutes,1))min" | Add-Content $log
Set-Location $S
C:\Python314\python.exe -I analyze-w2.py *> "$S\..\w2-gates\analyze-g35arm.txt"
"$((Get-Date).ToString('s')) G35ARM ANALYZE $(Get-Content "$S\..\w2-gates\analyze-g35arm.txt" | Select-Object -Last 1)" | Add-Content $log
"G35ARM DONE" | Add-Content $log
