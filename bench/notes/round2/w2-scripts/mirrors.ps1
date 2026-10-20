# Wave-2 A/B mirrors. Usage (from any shell, box quiet):
#   powershell -NoProfile -ExecutionPolicy Bypass -File mirrors.ps1 -Tip <wave-2 tip sha>
# Makes C:\bt_r2ab\{w1,w2,w2a,w2b}:
#   w1  = Blade.dll of the WAVE-1 tip 6b3e5293 (copied from C:\bt_r2ab\new\bin) + the wave-2 tip's bench\ (same kernels/harness as w2)
#   w2  = the integration worktree's CURRENT bin (must be the build of -Tip) + its bench\, default config
#   w2a = w2 + config blade_knobs.all BLADE_ORBIT_SCATTER=0            (scatter OFF arm: pairkernel/comoment3 repro)
#   w2b = w2 + config blade_knobs.all BLADE_TILE_TUNE=anti_g=0,tri_edge=0 and BLADE_JOIN_THREADS=0
#         (antisym row-group AUTO instead of 1; simplex edge kloops OFF; thread-level <&!> join OFF)
param([Parameter(Mandatory=$true)][string]$Tip, [string[]]$Mirrors = @('w1','w2','w2a','w2b'))
$WT = 'C:\Users\cdupu\Documents\GitHub\Blade\.claude\worktrees\bench-fortran'
$BIN = "$WT\bin\Release\net10.0"
$S = 'C:\Users\cdupu\AppData\Local\Temp\claude\C--Users-cdupu--claude-spares-spare-dUbXep\dbd3f5e5-01ed-41dd-bc92-4bc0ec85152b\scratchpad\round2\gates\w2'
$head = (git -C $WT rev-parse --short HEAD)
if (-not $Tip.StartsWith($head) -and -not $head.StartsWith($Tip)) { throw "worktree HEAD $head is not the tip $Tip" }
if (Test-Path "$BIN\blade.toolchain.json") { throw "blade.toolchain.json beside Blade.exe" }
$h2 = (Get-FileHash "$BIN\Blade.dll").Hash
$h1 = (Get-FileHash 'C:\bt_r2ab\new\bin\Release\net10.0\Blade.dll').Hash
"w1 (6b3e5293) Blade.dll $h1" | Add-Content "$S\mirror-hashes.txt"
"w2/w2a/w2b ($Tip) Blade.dll $h2" | Add-Content "$S\mirror-hashes.txt"
foreach ($m in $Mirrors) {
  $root = "C:\bt_r2ab\$m"
  if (Test-Path $root) { throw "$root exists; remove it first" }
  New-Item -ItemType Directory -Force $root | Out-Null
  robocopy "$WT\bench" "$root\bench" /E /XD results .build __pycache__ /NFL /NDL /NJH /NJS /NP | Out-Null
  New-Item -ItemType Directory -Force "$root\bench\results","$root\bench\.build" | Out-Null
  $src = if ($m -eq 'w1') { 'C:\bt_r2ab\new\bin\Release\net10.0' } else { $BIN }
  robocopy $src "$root\bin\Release\net10.0" /E /NFL /NDL /NJH /NJS /NP | Out-Null
  if (Test-Path "$root\bin\Release\net10.0\blade.toolchain.json") { Remove-Item "$root\bin\Release\net10.0\blade.toolchain.json" }
  $hh = (Get-FileHash "$root\bin\Release\net10.0\Blade.dll").Hash
  $want = if ($m -eq 'w1') { $h1 } else { $h2 }
  if ($hh -ne $want) { throw "$m hash mismatch" }
  if ($m -eq 'w1') {
    # smoke evidence is keyed per kernel|regime|arm on (source, blade_dll, config) hashes: entries for unchanged kernels stay valid
    Copy-Item 'C:\bt_r2ab\new\bench\.build\smoke-evidence.json' "$root\bench\.build\smoke-evidence.json"
  }
  $cfg = "$root\bench\harness\config.json"
  $j = Get-Content $cfg -Raw | ConvertFrom-Json
  if ($m -eq 'w2a') {
    $j.blade_knobs.all | Add-Member -NotePropertyName 'BLADE_ORBIT_SCATTER' -NotePropertyValue '0'
    $j.blade_knobs.all | Add-Member -NotePropertyName '_why' -NotePropertyValue 'wave-2 A/B knob-off arm: ordered orbit scatter OFF (strict logical walk kept)'
  }
  if ($m -eq 'w2b') {
    $j.blade_knobs.all | Add-Member -NotePropertyName 'BLADE_TILE_TUNE' -NotePropertyValue 'anti_g=0,tri_edge=0'
    $j.blade_knobs.all | Add-Member -NotePropertyName 'BLADE_JOIN_THREADS' -NotePropertyValue '0'
    $j.blade_knobs.all | Add-Member -NotePropertyName '_why' -NotePropertyValue 'wave-2 A/B knob-off arm: antisym row groups AUTO (anti_g=0), simplex edge kloops OFF (tri_edge=0), thread-level <&!> join OFF'
  }
  if ($m -eq 'w2a' -or $m -eq 'w2b') { $j | ConvertTo-Json -Depth 20 | Set-Content $cfg -Encoding utf8 }
  @"
Wave-2 A/B mirror root $m (made $(Get-Date -Format s) by the round-2 orchestrator; see C:\bt_r2ab\new\README.txt for the layout rationale)
bench\  = COPY of $WT\bench at $Tip (excluding results\, .build\, __pycache__)
bin\    = $(if ($m -eq 'w1') { 'Blade build of the WAVE-1 tip 6b3e5293 (copied from C:\bt_r2ab\new)' } else { "Blade build of $Tip (copied from the integration worktree)" }); Blade.dll SHA256 $hh
config  = $(if ($m -eq 'w2a') { 'blade_knobs.all BLADE_ORBIT_SCATTER=0' } elseif ($m -eq 'w2b') { 'blade_knobs.all BLADE_TILE_TUNE=anti_g=0,tri_edge=0 BLADE_JOIN_THREADS=0' } else { 'default' })
"@ | Set-Content "$root\README.txt"
  "$m ok $hh" | Add-Content "$S\mirror-hashes.txt"
}
Get-Content "$S\mirror-hashes.txt"
