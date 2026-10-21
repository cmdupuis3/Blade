# The Codex second-look experiments (box quiet): G49 retention arm, B2l hand patch, D1 with 201 panels, READ bandwidth.
$env:PATH = "C:\msys64\ucrt64\bin;" + $env:PATH
$P = "C:\Users\cdupu\AppData\Local\Temp\claude\C--Users-cdupu-Documents-GitHub-Blade--claude-worktrees-magical-cannon-db55bf\e4c4311e-f42b-47b9-8d8f-fac1416b45c4\scratchpad\w3\probe"
$H = "C:\Users\cdupu\AppData\Local\Temp\blade-build\recur_omp-8e221d98-30748-d4b3bea8"
$F = @("-std=c++17", "-O3", "-march=native", "-ffp-contract=fast", "-fno-math-errno", "-fwrapv", "-fopenmp")
function med($a) { $s = $a | Sort-Object; return [math]::Round($s[[int]($s.Count / 2)], 2) }
function phase($exe, $t, $reps) {
    $env:OMP_NUM_THREADS = "$t"
    $rows = @()
    for ($i = 0; $i -lt $reps; $i++) { $o = (& $exe 2>&1) | ForEach-Object { "$_" }; $rows += ,($o | Where-Object { $_ -match 'ms$' }) }
    $names = @('inputs','allocate','row0','region','combine','dealloc','main-end','insum','lapalloc','producer')
    $out = @{}
    foreach ($n in $names) { $v = @(); foreach ($r in $rows) { $m = $r | Where-Object { $_ -match "^$n\s+([0-9.]+) ms" }; if ($m -and $m -match "([0-9.]+) ms") { $v += [double]$Matches[1] } }; if ($v.Count -gt 0) { $out[$n] = med $v } }
    return $out
}
"=== 1. G49 retention arm (kMaxBlock 64 MB, kBudget 96 MB) vs default; phase medians (cumulative ms)"
# ret_build holds ALL runtime headers with the overridable pool header (quoted includes search the
# source's own directory first, so a -I shadow never applied); def = the same sources with the
# defaults (the overrides left at their default values).
Set-Location "$P\ret_build"
g++ @F -I . -o recur_omp_phases_def.exe recur_omp_phases.cpp 2>&1 | Select-Object -First 2
g++ @F -I . "-DBLADE_POOL_MAX_BLOCK=(size_t(64)<<20)" "-DBLADE_POOL_BUDGET=(size_t(96)<<20)" -o recur_omp_phases_ret.exe recur_omp_phases.cpp 2>&1 | Select-Object -First 2
g++ @F -I . -o stencil_b_phases_def.exe stencil_b_phases.cpp 2>&1 | Select-Object -First 2
g++ @F -I . "-DBLADE_POOL_MAX_BLOCK=(size_t(64)<<20)" "-DBLADE_POOL_BUDGET=(size_t(96)<<20)" -o stencil_b_phases_ret.exe stencil_b_phases.cpp 2>&1 | Select-Object -First 2
foreach ($t in 1, 8) { foreach ($v in 'def', 'ret', 'def', 'ret') { $ph = phase ".\recur_omp_phases_$v.exe" $t 5; "recur $v T=$t : region $($ph['region']) combine $($ph['combine']) dealloc $($ph['dealloc']) end $($ph['main-end'])" } }
foreach ($v in 'def', 'ret', 'def', 'ret') { $ph = phase ".\stencil_b_phases_$v.exe" 8 5; "stencil $v T=8 : inputs $($ph['inputs']) insum $($ph['insum']) producer $($ph['producer']) combine $($ph['combine']) dealloc $($ph['dealloc']) end $($ph['main-end'])" }
"=== 2. B2l hand patch, serial licensed recur, interleaved x7, T=1"
Set-Location $P
& C:\Python314\python.exe -I b2l_patch.py $P
foreach ($v in 'recur_orig_raw', 'recur_b2l_128', 'recur_b2l_256', 'recur_b2l_512') { g++ @F -I $H -o "$v.exe" "$v.cpp" 2>&1 | Select-Object -First 2 }
$env:OMP_NUM_THREADS = "1"
$times = @{}; $raws = @{}
foreach ($v in 'recur_orig_raw', 'recur_b2l_128', 'recur_b2l_256', 'recur_b2l_512') { $times[$v] = @() }
for ($i = 0; $i -lt 7; $i++) { foreach ($v in 'recur_orig_raw', 'recur_b2l_128', 'recur_b2l_256', 'recur_b2l_512') { $o = (& ".\$v.exe" 2>&1) | ForEach-Object { "$_" }; $m = ($o | Where-Object { $_ -match 'completed in' }) -replace '.*completed in ', ''; $times[$v] += [double]($m -replace 's', '') * 1000; $raws[$v] = ($o | Where-Object { $_ -match '^RAW' }) } }
foreach ($v in 'recur_orig_raw', 'recur_b2l_128', 'recur_b2l_256', 'recur_b2l_512') { "$v : median $(med $times[$v]) ms; all $(($times[$v] | ForEach-Object { [math]::Round($_, 2) }) -join ' '); $($raws[$v])" }
"=== 3. D1 instrument with 201 pack panels"
Set-Location "$P\expert"
g++ -O2 -fopenmp -o d1cost2.exe d1cost2.cpp 2>&1 | Select-Object -First 2
$env:OMP_NUM_THREADS = "8"; & .\d1cost2.exe
"=== 4. READ bandwidth"
Set-Location $P
g++ -O3 -march=native -fopenmp -o rd.exe rd.cpp 2>&1 | Select-Object -First 2
foreach ($t in 1, 2, 4, 8) { $env:OMP_NUM_THREADS = "$t"; & .\rd.exe }
Remove-Item Env:OMP_NUM_THREADS
