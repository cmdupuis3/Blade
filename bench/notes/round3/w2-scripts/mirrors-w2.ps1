# Round-3 wave-2 A/B mirrors. Usage: powershell -Command "& 'mirrors-w2.ps1' -Tip <sha> -Mirrors r3w1,r3w2,r3w2prefer,r3w2pull0,r3w2antig1,r3w2legs0"
#   r3w1        = OLD binary (the wave-1 tip build 9da403a8, Blade.dll E84BB4E5..., kept in C:\bt_r2ab\r3new\bin) + the wave-2 tip's bench\ (haloadjoint runs on both)
#   r3w2        = the integration worktree's CURRENT bin (the wave-2 tip) + its bench\, default config
#   r3w2prefer  = r3w2 + BLADE_FOLD_ROW_SLOTS=prefer   (W2-0a: the OLD precedence -- row slots win over the scatter under mt8 repro)
#   r3w2pull0   = r3w2 + BLADE_ORBIT_PULL=0            (W2-3: the serial ordered scatter instead of the row-owned pull, rank 3)
#   r3w2antig1  = r3w2 + BLADE_TILE_TUNE=anti_g=1      (W2-0b: force the old default 1 in every regime; the new default is 1 serial / auto under threads)
#   r3w2legs0   = r3w2 + BLADE_EPILOGUE_LEGS=0         (W2-4: the separate joined input-sum pass instead of the producer legs)
#   r3w2retain  = r3w2 + BLADE_POOL_RETAIN=1           (W2-6 / A4: the opt-in pool-retention arm, 64 MB / 96 MB per thread; the user's decision 2026-10-21)
param([Parameter(Mandatory=$true)][string]$Tip, [string[]]$Mirrors = @('r3w1','r3w2','r3w2prefer','r3w2pull0','r3w2antig1','r3w2legs0','r3w2retain'))
$WT = 'C:\Users\cdupu\Documents\GitHub\Blade\.claude\worktrees\bench-fortran'
$BIN = "$WT\bin\Release\net10.0"
$OLDBIN = 'C:\bt_r2ab\r3new\bin\Release\net10.0'
$OLDHASH = 'E84BB4E59A8111432B957B6EE9764B027BE608E6BB8E15DACEE55C0D103AEAEF'
$S = 'C:\Users\cdupu\AppData\Local\Temp\claude\C--Users-cdupu--claude-spares-spare-dUbXep\dbd3f5e5-01ed-41dd-bc92-4bc0ec85152b\scratchpad\round3\gates\ab'
$head = (git -C $WT rev-parse --short HEAD)
if (-not $Tip.StartsWith($head) -and -not $head.StartsWith($Tip)) { throw "worktree HEAD $head is not the tip $Tip" }
if (Test-Path "$BIN\blade.toolchain.json") { throw "blade.toolchain.json beside Blade.exe" }
$hNew = (Get-FileHash "$BIN\Blade.dll").Hash; $hOld = (Get-FileHash "$OLDBIN\Blade.dll").Hash
if ($hOld -ne $OLDHASH) { throw "r3new mirror binary is not the wave-1 build ($hOld)" }
if ($hNew -eq $hOld) { throw "the worktree binary IS the wave-1 build: rebuild at the wave-2 tip first" }
"r3w1 (wave-1 tip 9da403a8) Blade.dll $hOld" | Add-Content "$S\mirror-hashes-w2.txt"
"r3w2/* ($Tip) Blade.dll $hNew" | Add-Content "$S\mirror-hashes-w2.txt"
$knobs = @{
  r3w2prefer = @{ BLADE_FOLD_ROW_SLOTS = 'prefer'; _why = 'round-3 W2-0a: the old precedence (row slots over the scatter under mt8 repro)' }
  r3w2pull0  = @{ BLADE_ORBIT_PULL = '0'; _why = 'round-3 W2-3: row-owned pull scatter off (serial ordered scatter)' }
  r3w2antig1 = @{ BLADE_TILE_TUNE = 'anti_g=1'; _why = 'round-3 W2-0b: anti_g forced to 1 in every regime (the old default)' }
  r3w2legs0  = @{ BLADE_EPILOGUE_LEGS = '0'; _why = 'round-3 W2-4: producer-input legs off (separate joined input-sum pass)' }
  r3w2retain = @{ BLADE_POOL_RETAIN = '1'; _why = 'round-3 W2-6 / A4: the opt-in pool-retention arm (64 MB / 96 MB per thread)' }
}
foreach ($m in $Mirrors) {
  $root = "C:\bt_r2ab\$m"
  if (Test-Path $root) { throw "$root exists; remove it first" }
  New-Item -ItemType Directory -Force $root | Out-Null
  robocopy "$WT\bench" "$root\bench" /E /XD results .build __pycache__ /NFL /NDL /NJH /NJS /NP | Out-Null
  New-Item -ItemType Directory -Force "$root\bench\results","$root\bench\.build" | Out-Null
  $src = if ($m -eq 'r3w1') { $OLDBIN } else { $BIN }
  robocopy $src "$root\bin\Release\net10.0" /E /NFL /NDL /NJH /NJS /NP | Out-Null
  if (Test-Path "$root\bin\Release\net10.0\blade.toolchain.json") { Remove-Item "$root\bin\Release\net10.0\blade.toolchain.json" }
  $hh = (Get-FileHash "$root\bin\Release\net10.0\Blade.dll").Hash
  $want = if ($m -eq 'r3w1') { $hOld } else { $hNew }
  if ($hh -ne $want) { throw "$m hash mismatch" }
  if ($knobs.ContainsKey($m)) {
    $cfg = "$root\bench\harness\config.json"
    $j = Get-Content $cfg -Raw | ConvertFrom-Json
    foreach ($kv in $knobs[$m].GetEnumerator()) { $j.blade_knobs.all | Add-Member -NotePropertyName $kv.Key -NotePropertyValue $kv.Value -Force }
    $j | ConvertTo-Json -Depth 20 | Set-Content $cfg -Encoding utf8
  }
  "Round-3 wave-2 A/B mirror $m made $(Get-Date -Format s): bench\ = $WT\bench at $Tip; bin = $(if ($m -eq 'r3w1') { 'wave-1 tip 9da403a8 build' } else { "build of $Tip" }) Blade.dll $hh; config = $(if ($knobs.ContainsKey($m)) { ($knobs[$m].GetEnumerator() | Where-Object { $_.Key -ne '_why' } | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ' ' } else { 'default' })" | Set-Content "$root\README.txt"
  "$m ok $hh" | Add-Content "$S\mirror-hashes-w2.txt"
}
Get-Content "$S\mirror-hashes-w2.txt"
