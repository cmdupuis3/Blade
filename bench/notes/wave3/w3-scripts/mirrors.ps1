# Wave-3 A/B mirrors (one root per binary; run.py hardcodes REPO_DIR\bin). Box quiet.
#   powershell -NoProfile -ExecutionPolicy Bypass -Command "& 'mirrors.ps1' -Tip <sha> -Mirrors w3,w3a,w3g"
# Makes C:\bt_r2ab\{w3,w3a,w3g}:
#   w3  = the wave-3 worktree's CURRENT bin (the build of -Tip) + its bench\ (recur / stencil with their wave-3 spellings), default config
#   w3a = w3 + config blade_knobs.all BLADE_REC_STEP_OMP=0   (E-G5 admission OFF on the NEW source: the step's requesting map keeps
#         its own per-step team and the row is copied -- Codex objection 14's "polluted baseline")
#   w3g = w3 + config blade_knobs.all BLADE_TILE_TUNE=anti_g=0 (antisym row groups AUTO instead of the default 1; nothing else)
# OLD = the existing C:\bt_r2ab\w2 (the wave-2 binary 1cf90428 = bench-identical to the wave-2 tip, ORIGINAL recur / stencil sources).
param([Parameter(Mandatory=$true)][string]$Tip, [string[]]$Mirrors = @('w3','w3a','w3g'))
$WT = 'C:\Users\cdupu\Documents\GitHub\Blade\.claude\worktrees\magical-cannon-db55bf'
$BIN = "$WT\bin\Release\net10.0"
$S = 'C:\bt_w3\ab'
New-Item -ItemType Directory -Force $S | Out-Null
$head = (git -C $WT rev-parse --short HEAD)
if (-not $Tip.StartsWith($head) -and -not $head.StartsWith($Tip)) { throw "worktree HEAD $head is not the tip $Tip" }
if (Test-Path "$BIN\blade.toolchain.json") { throw "blade.toolchain.json beside Blade.exe" }
$h3 = (Get-FileHash "$BIN\Blade.dll").Hash
"w3/w3a/w3g ($Tip) Blade.dll $h3" | Add-Content "$S\mirror-hashes.txt"
foreach ($m in $Mirrors) {
  $root = "C:\bt_r2ab\$m"
  if (Test-Path $root) { throw "$root exists; remove it first" }
  New-Item -ItemType Directory -Force $root | Out-Null
  robocopy "$WT\bench" "$root\bench" /E /XD results .build __pycache__ /NFL /NDL /NJH /NJS /NP | Out-Null
  New-Item -ItemType Directory -Force "$root\bench\results","$root\bench\.build" | Out-Null
  robocopy $BIN "$root\bin\Release\net10.0" /E /NFL /NDL /NJH /NJS /NP | Out-Null
  if (Test-Path "$root\bin\Release\net10.0\blade.toolchain.json") { Remove-Item "$root\bin\Release\net10.0\blade.toolchain.json" }
  $hh = (Get-FileHash "$root\bin\Release\net10.0\Blade.dll").Hash
  if ($hh -ne $h3) { throw "$m hash mismatch" }
  $cfg = "$root\bench\harness\config.json"
  $j = Get-Content $cfg -Raw | ConvertFrom-Json
  if ($m -eq 'w3a') {
    $j.blade_knobs.all | Add-Member -NotePropertyName 'BLADE_REC_STEP_OMP' -NotePropertyValue '0'
    $j.blade_knobs.all | Add-Member -NotePropertyName '_why' -NotePropertyValue 'wave-3 A/B knob-off arm: E-G5 admission OFF (the requesting step map keeps its own per-step team; the row is copied)'
  }
  if ($m -eq 'w3g') {
    $j.blade_knobs.all | Add-Member -NotePropertyName 'BLADE_TILE_TUNE' -NotePropertyValue 'anti_g=0'
    $j.blade_knobs.all | Add-Member -NotePropertyName '_why' -NotePropertyValue 'wave-3 A/B arm: antisym row groups AUTO (anti_g=0) vs the default 1'
  }
  if ($m -ne 'w3') { $j | ConvertTo-Json -Depth 20 | Set-Content $cfg -Encoding utf8 }
  @"
Wave-3 A/B mirror root $m (made $(Get-Date -Format s) by the wave-3 session; see C:\bt_r2ab\new\README.txt for the layout rationale)
bench\  = COPY of $WT\bench at $Tip (excluding results\, .build\, __pycache__)
bin\    = Blade build of $Tip (copied from the wave-3 worktree); Blade.dll SHA256 $hh
config  = $(if ($m -eq 'w3a') { 'blade_knobs.all BLADE_REC_STEP_OMP=0' } elseif ($m -eq 'w3g') { 'blade_knobs.all BLADE_TILE_TUNE=anti_g=0' } else { 'default' })
"@ | Set-Content "$root\README.txt"
  "$m ok $hh" | Add-Content "$S\mirror-hashes.txt"
}
Get-Content "$S\mirror-hashes.txt"
