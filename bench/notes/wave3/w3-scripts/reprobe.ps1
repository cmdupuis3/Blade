# Re-run of the key attribution probes on a QUIET box (the originals ran beside a stray single-core
# `find` from 16:50 to 20:16): fork/barrier cost, write bandwidth, recur region (Blade pre-touched and
# as-is, T=1/8) and the expert's region-only / pre-touched copies, stencil expert region-only.
$env:PATH = "C:\msys64\ucrt64\bin;" + $env:PATH
$P = "C:\Users\cdupu\AppData\Local\Temp\claude\C--Users-cdupu-Documents-GitHub-Blade--claude-worktrees-magical-cannon-db55bf\e4c4311e-f42b-47b9-8d8f-fac1416b45c4\scratchpad\w3\probe"
$H = "C:\Users\cdupu\AppData\Local\Temp\blade-build\recur_omp-8e221d98-30748-d4b3bea8"
function med($a) { $s = $a | Sort-Object; return [math]::Round($s[[int]($s.Count / 2)], 2) }
function tot($exe, $t, $reps) { $env:OMP_NUM_THREADS = "$t"; $r = @(); for ($i = 0; $i -lt $reps; $i++) { $o = (& $exe 2>&1) | ForEach-Object { "$_" }; $m = ($o | Where-Object { $_ -match 'completed in' }) -replace '.*completed in ', ''; $r += [double]($m -replace 's', '') * 1000 }; return $r }
function reg($exe, $t, $reps) { $env:OMP_NUM_THREADS = "$t"; $r = @(); for ($i = 0; $i -lt $reps; $i++) { $o = (& $exe 2>&1) | ForEach-Object { "$_" }; $m = $o | Where-Object { $_ -match 'region ([0-9.]+) ms' }; if ($m -and $m -match 'region ([0-9.]+) ms') { $r += [double]$Matches[1] } }; return $r }
"=== load before: $((Get-Counter '\Processor(_Total)\% Processor Time' -SampleInterval 2 -MaxSamples 3 | % { $_.CounterSamples } | Measure-Object CookedValue -Average).Average)"
"=== fork / barrier cost (forkcost.exe), T=8"
$env:OMP_NUM_THREADS = '8'; Set-Location $P; & .\forkcost.exe; & .\forkcost.exe
"=== write bandwidth (bw.exe)"
foreach ($t in 1, 2, 4, 8) { $env:OMP_NUM_THREADS = "$t"; & .\bw.exe | Select-Object -Skip 2 -First 1 }
"=== recur regions, 7 runs each, T=1 / T=8 (Blade as-is, Blade pre-touched; expert region-only, expert pre-touched)"
Set-Location $H
foreach ($t in 1, 8) {
    "Blade as-is      T=$t region median $(med (reg '.\recur_omp_timed.exe' $t 7)) ms"
    "Blade pre-touched T=$t region median $(med (reg '.\recur_omp_pretouch_timed.exe' $t 7)) ms"
    "expert region    T=$t median $(med (tot "$P\expert\recur_expert_region_mt.exe" $t 7)) ms"
    "expert pretouch  T=$t median $(med (tot "$P\expert\recur_expert_pretouch_region_mt.exe" $t 7)) ms"
    "expert full      T=$t median $(med (tot "$P\expert\recur_expert_mt.exe" $t 7)) ms"
}
"=== stencil: expert full / region-only, T=8; Blade licensed-source phases T=8 (3 runs)"
"stencil expert full   T=8 median $(med (tot "$P\expert\stencil_expert_mt.exe" 8 7)) ms"
"stencil expert region T=8 median $(med (tot "$P\expert\stencil_expert_region_mt.exe" 8 7)) ms"
Set-Location "$P\stencil"; $env:OMP_NUM_THREADS = '8'
for ($i = 0; $i -lt 3; $i++) { (& .\stencil_b_phases.exe 2>&1 | ForEach-Object { "$_" } | Where-Object { $_ -match 'ms$|completed' }) -join ' | ' }
Remove-Item Env:OMP_NUM_THREADS
