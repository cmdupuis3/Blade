# Round-3 wave-3 A/B mirrors. Usage: powershell -Command "& 'mirrors-w3.ps1' -Tip <sha> [-Recreate]"
#   r3w1       = the wave-1 binary (9da403a8 build, Blade.dll E84BB4E5...) + the wave-3 tip's bench\   (saxpy re-measure baseline, re-benched in this session)
#   r3w2bin    = the wave-2 binary (the r3w2 mirror's bin, the wave-2 chain's build) + the wave-3 tip's bench\  (OLD for every wave-3 cell)
#   r3w3       = the integration worktree's CURRENT bin (the wave-3 tip) + its bench\, default config  (NEW)
#   r3w3t3off  = r3w3 + BLADE_GROUP_AGG=0        (W3-1: the T3 exact state-machine group aggregation off -> today's grouping form)
#   r3w3t3lic  = r3w3 + BLADE_GROUP_AGG=licensed (W3-1: the EXPERIMENTAL licensed-float one pass, opt-in; the default declines it in a licensed build)
#   r3w3t2off  = r3w3 + BLADE_HASH_JOIN=0        (W3-2: the stable CSR equijoin off -> the six-pass form)
#   r3w3strict = r3w3 + BLADE_ORBIT_ACTIONS=strict (W3-4: the strict orbit-action arm)
#   r3w3legs1  = r3w3 + BLADE_EPILOGUE_LEGS=0    (W2-4 deferred saxpy re-measure: the OPT-IN kept inline with producer legs; the default is the joined pass)
param([Parameter(Mandatory=$true)][string]$Tip,
      [string[]]$Mirrors = @('r3w1','r3w2bin','r3w3','r3w3t3off','r3w3t3lic','r3w3t2off','r3w3strict','r3w3legs1'),
      [switch]$Recreate)
$WT = 'C:\Users\cdupu\Documents\GitHub\Blade\.claude\worktrees\bench-fortran'
$BIN = "$WT\bin\Release\net10.0"
$S = 'C:\Users\cdupu\AppData\Local\Temp\claude\C--Users-cdupu--claude-spares-spare-dUbXep\dbd3f5e5-01ed-41dd-bc92-4bc0ec85152b\scratchpad\round3\gates\ab'
$BINS = 'C:\bt_r2ab\bins'
$W1HASH = 'E84BB4E59A8111432B957B6EE9764B027BE608E6BB8E15DACEE55C0D103AEAEF'
$W2HASH = ((Select-String -Path "$S\mirror-hashes-w2.txt" -Pattern '^r3w2/\*' | Select-Object -First 1).Line.Split(' ')[-1])
if (-not $W2HASH) { throw "wave-2 binary hash not found in mirror-hashes-w2.txt" }
# 1. preserve the old binaries OUTSIDE the mirror roots (the mirrors are recreated per wave)
New-Item -ItemType Directory -Force $BINS | Out-Null
foreach ($pair in @(@('w1','C:\bt_r2ab\r3w1\bin\Release\net10.0',$W1HASH), @('w2','C:\bt_r2ab\r3w2\bin\Release\net10.0',$W2HASH))) {
  $dst = "$BINS\$($pair[0])"
  if (-not (Test-Path "$dst\Blade.dll")) {
    if (-not (Test-Path "$($pair[1])\Blade.dll")) { throw "source binary for $($pair[0]) missing: $($pair[1])" }
    robocopy $pair[1] $dst /E /NFL /NDL /NJH /NJS /NP | Out-Null
  }
  $hh = (Get-FileHash "$dst\Blade.dll").Hash
  if ($hh -ne $pair[2]) { throw "$($pair[0]) preserved binary hash $hh != expected $($pair[2])" }
}
$head = (git -C $WT rev-parse --short HEAD)
if (-not $Tip.StartsWith($head) -and -not $head.StartsWith($Tip)) { throw "worktree HEAD $head is not the tip $Tip" }
if (Test-Path "$BIN\blade.toolchain.json") { throw "blade.toolchain.json beside Blade.exe" }
$hNew = (Get-FileHash "$BIN\Blade.dll").Hash
if ($hNew -eq $W1HASH -or $hNew -eq $W2HASH) { throw "the worktree binary is an OLD build: rebuild at the wave-3 tip first" }
"r3w1 (wave-1 tip 9da403a8) Blade.dll $W1HASH" | Add-Content "$S\mirror-hashes-w3.txt"
"r3w2bin (wave-2 chain build) Blade.dll $W2HASH" | Add-Content "$S\mirror-hashes-w3.txt"
"r3w3/* ($Tip) Blade.dll $hNew" | Add-Content "$S\mirror-hashes-w3.txt"
$knobs = @{
  r3w3t3off  = @{ BLADE_GROUP_AGG = '0';          _why = 'round-3 W3-1: T3 exact group aggregation off (the previous grouping form)' }
  r3w3t3lic  = @{ BLADE_GROUP_AGG = 'licensed'; _why = 'round-3 W3-1: the experimental licensed-float one pass (opt-in arm; the default build declines it)' }
  r3w3t2off  = @{ BLADE_HASH_JOIN = '0';          _why = 'round-3 W3-2: stable CSR equijoin off (the six-pass form)' }
  r3w3strict = @{ BLADE_ORBIT_ACTIONS = 'strict'; _why = 'round-3 W3-4: the strict orbit-action arm' }
  r3w3legs1  = @{ BLADE_EPILOGUE_LEGS = '1';      _why = 'round-3 W2-4 (deferred saxpy re-measure): the OPT-IN kept inline + producer-input legs (the user''s fallback after the seventh look)' }
}
foreach ($m in $Mirrors) {
  $root = "C:\bt_r2ab\$m"
  if (Test-Path $root) { if ($Recreate) { Remove-Item -Recurse -Force $root } else { throw "$root exists; pass -Recreate" } }
  New-Item -ItemType Directory -Force $root | Out-Null
  robocopy "$WT\bench" "$root\bench" /E /XD results .build __pycache__ /NFL /NDL /NJH /NJS /NP | Out-Null
  New-Item -ItemType Directory -Force "$root\bench\results","$root\bench\.build" | Out-Null
  $src = switch ($m) { 'r3w1' { "$BINS\w1" } 'r3w2bin' { "$BINS\w2" } default { $BIN } }
  $want = switch ($m) { 'r3w1' { $W1HASH } 'r3w2bin' { $W2HASH } default { $hNew } }
  robocopy $src "$root\bin\Release\net10.0" /E /NFL /NDL /NJH /NJS /NP | Out-Null
  if (Test-Path "$root\bin\Release\net10.0\blade.toolchain.json") { Remove-Item "$root\bin\Release\net10.0\blade.toolchain.json" }
  $hh = (Get-FileHash "$root\bin\Release\net10.0\Blade.dll").Hash
  if ($hh -ne $want) { throw "$m hash mismatch" }
  if ($knobs.ContainsKey($m)) {
    $cfg = "$root\bench\harness\config.json"
    $j = Get-Content $cfg -Raw | ConvertFrom-Json
    foreach ($kv in $knobs[$m].GetEnumerator()) { if ($kv.Key -ne '_why') { $j.blade_knobs.all | Add-Member -NotePropertyName $kv.Key -NotePropertyValue $kv.Value -Force } }
    $j | ConvertTo-Json -Depth 20 | Set-Content $cfg -Encoding utf8
  }
  $cfgtxt = if ($knobs.ContainsKey($m)) { ($knobs[$m].GetEnumerator() | Where-Object { $_.Key -ne '_why' } | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ',' } else { 'default' }
  "Round-3 wave-3 A/B mirror $m made $(Get-Date -Format s): bench\ = $WT\bench at $Tip; bin = $src Blade.dll $hh; config = $cfgtxt" | Set-Content "$root\MIRROR.txt"
  "$m ok $hh" | Add-Content "$S\mirror-hashes-w3.txt"
}
Get-Content "$S\mirror-hashes-w3.txt"
