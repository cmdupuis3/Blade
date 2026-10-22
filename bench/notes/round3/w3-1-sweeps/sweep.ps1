# W3-1 cross-version sweep (round 3, gap G71): compare two Blade executables
# over every tests/corpus/sql-group-by file plus the groupagg / groupagg_dyn
# kernels, and write a manifest with one line per file x lane x gate.
#
#   -Mode emit  : `blade emit <file>` of both executables (the C++ lane, and the
#                 LLVM lane requested: BLADE_LLVM=1). A SUCCESSFUL pair (both exit
#                 0) is compared as raw stdout BYTES, ordinally; a pair where
#                 either side exits non-zero is NOT an emission comparison -- it is
#                 recorded as rejected, with each side's exit status and
#                 diagnostics, and counted apart.
#   -Mode value : `blade run <file> --verbose` of both executables with
#                 BLADE_PRINT_BITS=1: the exit status, stdout and stderr compared
#                 ORDINALLY after exactly two normalizations -- (1) the private
#                 build-directory SUBSTRING `<drive>:\...\blade-build\<dir>` is
#                 replaced by `<build>` (nothing else on its line is touched) and
#                 (2) these explicitly identified records are removed, whole line:
#                 the timing line `<stem> completed in <t>s` (stdout; under
#                 BLADE_PRINT_BITS it reads `<t> #<16 hex>s`) and the
#                 driver records `[Emit] `, `[cache] `, `[Build] `,
#                 `[blade] llvm lane refused: ...; falling back to C++` (stderr).
#                 The removed driver records are not discarded: they are the
#                 POSITIVE evidence each build's outcome is classified from:
#                   front-end rejection    no `[Emit]` record, a BL1xxx-BL6xxx/BL8xxx diagnostic
#                   backend refusal        no `[Emit]` record, a BL7xxx/BL9xxx diagnostic,
#                                          or an `[Emit]` record and no `[cache] hit|store`
#                                          record (no executable was produced)
#                   LLVM execution         last `[Emit]` names a .ll, a `[cache] hit|store`
#                                          record, no refusal record
#                   C++ fallback execution the refusal record, last `[Emit]` names a .cpp,
#                                          a `[cache]` record
#                   C++ execution          the C++ lane: `[Emit]` names a .cpp, a `[cache]` record
#                 ("execution" = an executable was produced and launched; the exit
#                 status beside it is the program's). The emission of the same
#                 file is compared beside it, as in emit mode.
#
# Gates: -Gates 0 (BLADE_GROUP_AGG=0), on (unset), or both.
# Every BLADE_* variable inherited from the caller is CLEARED for the sweep
# (named in the header); BLADE_BLAS is then pinned `off` (as `blade test` does),
# BLADE_PRINT_BITS / BLADE_LLVM / BLADE_GROUP_AGG are set per run as stated.
# The exe cache stays at its default (on): it keys on the emitted text and the
# CPU, so an identical emission reuses one executable.
#
# Usage (PowerShell 5.1, from any directory; g++ / clang as for `blade run`):
#   powershell -NoProfile -ExecutionPolicy Bypass -File sweep.ps1 `
#       -OldExe <baseline>\Blade.exe -OldSource "git archive 361504b6 (clean export)" `
#       -NewExe <candidate>\Blade.exe -NewSource "git archive 862bf814 (clean export)" `
#       -Root <checkout> -Mode emit -Gates 0,on -Lanes cpp,llvm -Out manifest.txt
# -OldSource / -NewSource are the OPERATOR's statement of what each build was
# built from; the script adds what it can measure (SHA-256 of Blade.exe and
# Blade.dll, a toolchain file beside either) and never infers a commit itself.
# Build a baseline from a clean export: `git archive <sha> | tar -x` into a
# scratch directory, then `dotnet build Blade.fsproj -c Release` there.
param(
    [Parameter(Mandatory = $true)][string]$OldExe,
    [Parameter(Mandatory = $true)][string]$OldSource,
    [Parameter(Mandatory = $true)][string]$NewExe,
    [Parameter(Mandatory = $true)][string]$NewSource,
    [Parameter(Mandatory = $true)][string]$Root,
    [ValidateSet("emit", "value")][string]$Mode = "emit",
    [string[]]$Gates = @("0", "on"),
    [string[]]$Lanes = @("cpp", "llvm"),
    [string[]]$Kernels = @("groupagg", "groupagg_dyn"),
    [string]$Only = "",   # a regex over file names, for trying the script on a subset
    [Parameter(Mandatory = $true)][string]$Out
)
$ErrorActionPreference = 'Continue'
$DebugDir = $env:W31_SWEEP_DEBUG   # a directory: keep each pair's normalized texts there
# `-File` passes a comma list as one string: split it here
$Gates = @($Gates | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$Lanes = @($Lanes | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$Kernels = @($Kernels | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$OldExe = (Resolve-Path $OldExe).Path
$NewExe = (Resolve-Path $NewExe).Path
$Root = (Resolve-Path $Root).Path
$env:PATH = "C:\msys64\ucrt64\bin;" + $env:PATH

# ---- knobs: clear every inherited BLADE_*, then pin BLADE_BLAS=off ----
$cleared = @(Get-ChildItem env: | Where-Object { $_.Name -like "BLADE_*" } | ForEach-Object { "$($_.Name)=$($_.Value)" })
Get-ChildItem env: | Where-Object { $_.Name -like "BLADE_*" } | ForEach-Object { Remove-Item "env:$($_.Name)" }
$env:BLADE_BLAS = "off"
$inherited = @(Get-ChildItem env: | Where-Object { $_.Name -match "^(OPENBLAS_DIR|OMP_NUM_THREADS|OMP_THREAD_LIMIT|NETCDF_DIR|LOCALAPPDATA)$" } | ForEach-Object { "$($_.Name)=$($_.Value)" })

# ---- provenance ----
function Sha([string]$p) { if (Test-Path $p) { (Get-FileHash -Algorithm SHA256 $p).Hash.ToLower() } else { "absent" } }
function BuildLine([string]$tag, [string]$exe, [string]$src) {
    $dir = Split-Path $exe -Parent
    @("# ${tag}: $exe",
      "#   built from (operator's statement): $src",
      "#   SHA-256 Blade.exe $(Sha $exe)",
      "#   SHA-256 Blade.dll $(Sha (Join-Path $dir 'Blade.dll'))",
      "#   blade.toolchain.json beside it: $(Sha (Join-Path $dir 'blade.toolchain.json'))")
}
$rootHead = (git -C $Root rev-parse HEAD 2>$null)
$porcelain = @(git -C $Root status --porcelain 2>$null)
if ($porcelain.Count -eq 0) { $rootState = "clean" } else {
    $patch = Join-Path ([IO.Path]::GetTempPath()) ("w31patch-" + [Guid]::NewGuid().ToString("N") + ".diff")
    cmd /c "git -C `"$Root`" diff HEAD 1> `"$patch`""
    $rootState = "DIRTY: $($porcelain.Count) status entries [$((@($porcelain | Select-Object -First 10) | ForEach-Object { $_.Trim() }) -join '; ')]; SHA-256 of ``git diff HEAD`` $(Sha $patch)"
    Remove-Item $patch -ErrorAction SilentlyContinue
}
$gxx = (& g++ --version 2>$null | Select-Object -First 1)
$clangRow = (& $NewExe doctor 2>&1 | Where-Object { $_ -match "clang / LLVM" } | Select-Object -First 1)
if ($clangRow) { $clangRow = ($clangRow -replace '^\s*\[[^\]]*\]\s*clang / LLVM\s*', '') -replace ' -- .*$', '' } else { $clangRow = "unknown (doctor row absent)" }

$files = @(Get-ChildItem "$Root\tests\corpus\sql-group-by\*.blade" | Sort-Object Name | ForEach-Object FullName)
foreach ($k in $Kernels) { $files += "$Root\bench\kernels\$k\$k.blade" }
if ($Only) { $files = @($files | Where-Object { (Split-Path $_ -Leaf) -match $Only }) }
$inputHash = (($files | ForEach-Object { Sha $_ }) -join "`n")
$inputSha = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::ASCII.GetBytes($inputHash))).Replace("-", "").ToLower()

$work = Join-Path ([IO.Path]::GetTempPath()) ("w31sweep-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force $work | Out-Null
Push-Location $work

# ---- helpers ----
function RunTo([string]$cmdline, [string]$o, [string]$e) {
    cmd /c "`"$cmdline 1> `"$o`" 2> `"$e`"`""
    return $LASTEXITCODE
}
function Bytes([string]$p) { if (Test-Path $p) { return ,([IO.File]::ReadAllBytes($p)) } else { return ,([byte[]]@()) } }
function Text([string]$p) { [Text.Encoding]::UTF8.GetString((Bytes $p)) }
$buildRx = '[A-Za-z]:\\[^\s"'']*?\\blade-build\\[^\\\s"'':]+'
function Codes([string]$t) { @([regex]::Matches($t, 'error\[(BL\d{4})\]') | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique) }
function FirstDiag([string]$t) {
    $l = @($t -split "`n" | Where-Object { $_ -match 'error\[BL\d{4}\]' } | Select-Object -First 1)
    if ($l.Count -eq 0) { return "" }
    $s = ($l[0].TrimEnd("`r") -replace $buildRx, '<build>')
    if ($s.Length -gt 140) { $s = $s.Substring(0, 140) + "..." }
    return $s
}
# value-mode normalization: the build-path SUBSTRING, then the identified records
function Norm([string]$t, [string]$stem, [bool]$isErr) {
    $t = $t -replace $buildRx, '<build>'
    $ls = $t -split "`n"
    $timing = '^' + [regex]::Escape($stem) + ' completed in [0-9.eE+-]+( #[0-9a-f]{16})?s\r?$'
    $kept = foreach ($l in $ls) {
        if (-not $isErr -and $l -match $timing) { continue }
        if ($isErr -and ($l -match '^\[(Emit|cache|Build)\] ' -or $l -match '^\[blade\] llvm lane refused: .*; falling back to C\+\+\r?$')) { continue }
        $l
    }
    return (@($kept) -join "`n")
}
function Outcome([string]$lane, [int]$code, [string]$o, [string]$e) {
    $el = @($e -split "`n" | ForEach-Object { $_.TrimEnd("`r") })
    $emits = @($el | Where-Object { $_ -match '^\[Emit\] ' })
    $built = @($el | Where-Object { $_ -match '^\[cache\] (hit|store)\b' }).Count -gt 0
    $refused = @($el | Where-Object { $_ -match '^\[blade\] llvm lane refused: ' }).Count -gt 0
    $cs = Codes ($o + "`n" + $e)
    $cstr = if ($cs.Count) { " " + ($cs -join ",") } else { "" }
    if ($emits.Count -eq 0) {
        if ($cs | Where-Object { $_ -match '^BL[79]' }) { return "backend refusal before emission$cstr (exit $code)" }
        return "front-end rejection$cstr (exit $code)"
    }
    $last = $emits[-1]
    if (-not $built) { return "backend refusal: emitted $([IO.Path]::GetExtension($last)), no executable$cstr (exit $code)" }
    $multi = if ($emits.Count -gt 1) { "; $($emits.Count) [Emit] records" } else { "" }
    if ($last -match '\.ll\s*$' -and -not $refused) { return "LLVM execution (exit $code$multi)" }
    if ($last -match '\.cpp\s*$' -and $refused) { return "C++ fallback execution (exit $code$multi)" }
    if ($last -match '\.cpp\s*$' -and $lane -eq "cpp") { return "C++ execution (exit $code$multi)" }
    return "UNCLASSIFIED: lane $lane, last [Emit] $last, refusal $refused (exit $code$multi)"
}
function EmitKind([string]$lane, [int]$code, [byte[]]$ob, [string]$e) {
    if ($code -ne 0) {
        $cs = Codes $e
        return "rejected exit $code" + $(if ($cs.Count) { " " + ($cs -join ",") } else { "" })
    }
    $t = [Text.Encoding]::UTF8.GetString($ob)
    $isLl = ($t -match '(?m)^target triple' -or $t -match '(?m)^define ')
    $isCpp = ($t -match '(?m)^#include')
    $refused = ($e -match '(?m)^\[blade\] llvm lane refused: ')
    if ($isLl -and -not $refused) { return "LLVM IR" }
    if ($isCpp -and $refused) { return "C++ (LLVM refused)" }
    if ($isCpp -and $lane -eq "cpp") { return "C++" }
    return "UNCLASSIFIED (ll $isLl, cpp $isCpp, refused $refused)"
}
function DropKept([string]$e) {
    foreach ($l in ($e -split "`n")) {
        if ($l -match '^\[Build\] kept (.+?)\r?$') {
            $d = $Matches[1]
            if ($d -match '\\blade-build\\' -and (Test-Path $d)) { Remove-Item -Recurse -Force $d -ErrorAction SilentlyContinue }
        }
    }
}
function Same([byte[]]$a, [byte[]]$b) { [System.Linq.Enumerable]::SequenceEqual($a, $b) }

$lines = New-Object System.Collections.Generic.List[string]
$tally = @{}
function Bump([string]$k) { if ($tally.ContainsKey($k)) { $tally[$k]++ } else { $tally[$k] = 1 } }
$total = 0
foreach ($lane in $Lanes) {
  foreach ($gate in $Gates) {
    if ($lane -eq "llvm") { $env:BLADE_LLVM = "1" } else { Remove-Item env:BLADE_LLVM -ErrorAction SilentlyContinue }
    if ($gate -eq "0") { $env:BLADE_GROUP_AGG = "0" } else { Remove-Item env:BLADE_GROUP_AGG -ErrorAction SilentlyContinue }
    foreach ($f in $files) {
      $leaf = Split-Path $f -Leaf
      $stem = [IO.Path]::GetFileNameWithoutExtension($f)
      $total++
      # ---- emission ----
      $ca = RunTo "`"$OldExe`" emit `"$f`"" "$work\ea.out" "$work\ea.err"
      $cb = RunTo "`"$NewExe`" emit `"$f`"" "$work\eb.out" "$work\eb.err"
      $oa = Bytes "$work\ea.out"; $ob = Bytes "$work\eb.out"
      $erra = Text "$work\ea.err"; $errb = Text "$work\eb.err"
      $ka = EmitKind $lane $ca $oa $erra; $kb = EmitKind $lane $cb $ob $errb
      if ($ca -eq 0 -and $cb -eq 0) {
        if (Same $oa $ob) { $emitRes = "identical bytes"; $ek = "identical" } else { $emitRes = "DIFFERS"; $ek = "differs" }
        $emitRes = "$emitRes [$ka / $kb]"
      } else {
        $ek = "rejected"
        $da = (Text "$work\ea.err") -replace $buildRx, '<build>'; $db = (Text "$work\eb.err") -replace $buildRx, '<build>'
        $dsame = if ([string]::Equals($da, $db, [StringComparison]::Ordinal)) { "diagnostics identical" } else { "diagnostics DIFFER" }
        $emitRes = "REJECTED (not an emission comparison): old $ka '$(FirstDiag $erra)'; new $kb '$(FirstDiag $errb)'; $dsame"
      }
      if ($Mode -eq "emit") {
        Bump "$lane gate=$gate emission $ek"
        $lines.Add(("{0,-5} gate={1,-3} {2}: emission {3}" -f $lane, $gate, $leaf, $emitRes))
        continue
      }
      # ---- value ----
      $env:BLADE_PRINT_BITS = "1"
      $ra = RunTo "`"$OldExe`" run `"$f`" --verbose" "$work\a.out" "$work\a.err"
      $rb = RunTo "`"$NewExe`" run `"$f`" --verbose" "$work\b.out" "$work\b.err"
      Remove-Item env:BLADE_PRINT_BITS -ErrorAction SilentlyContinue
      $ao = Text "$work\a.out"; $ae = Text "$work\a.err"; $bo = Text "$work\b.out"; $be = Text "$work\b.err"
      DropKept $ae; DropKept $be
      $outA = Outcome $lane $ra $ao $ae; $outB = Outcome $lane $rb $bo $be
      $na = "exit $ra`n" + (Norm $ao $stem $false) + "`n--`n" + (Norm $ae $stem $true)
      $nb = "exit $rb`n" + (Norm $bo $stem $false) + "`n--`n" + (Norm $be $stem $true)
      $vs = if ([string]::Equals($na, $nb, [StringComparison]::Ordinal)) { "identical" } else { "DIFFERS" }
      if ($DebugDir) { [IO.File]::WriteAllText("$DebugDir\$lane-$gate-$stem.old.txt", $na); [IO.File]::WriteAllText("$DebugDir\$lane-$gate-$stem.new.txt", $nb) }
      Bump "value $vs"
      Bump ("old: " + ($outA -replace ' \(exit.*$', '' -replace ' BL\d{4}.*$', ''))
      Bump ("new: " + ($outB -replace ' \(exit.*$', '' -replace ' BL\d{4}.*$', ''))
      Bump "$lane gate=$gate emission $ek"
      $lines.Add(("{0,-5} gate={1,-3} {2}: normalized output {3}; old {4}; new {5}; emission {6}" -f $lane, $gate, $leaf, $vs, $outA, $outB, $emitRes))
    }
  }
}
Remove-Item env:BLADE_LLVM -ErrorAction SilentlyContinue; Remove-Item env:BLADE_GROUP_AGG -ErrorAction SilentlyContinue
Pop-Location
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue

$cmdText = if ($Mode -eq "emit") { "<exe> emit <file>" } else { "<exe> emit <file>; BLADE_PRINT_BITS=1 <exe> run <file> --verbose" }
$head = @(
  "# W3-1 sweep, mode $Mode, $(Get-Date -Format 'yyyy-MM-dd HH:mm')"
) + (BuildLine "old" $OldExe $OldSource) + (BuildLine "new" $NewExe $NewSource) + @(
  "# checkout (programs, -Root): $Root at $rootHead, $rootState",
  "#   SHA-256 over the per-file SHA-256 list of the $($files.Count) programs: $inputSha",
  "# commands, per file: $cmdText (cmd.exe, stdout and stderr to separate files)",
  "#   lane cpp: BLADE_LLVM unset; lane llvm: BLADE_LLVM=1. gate 0: BLADE_GROUP_AGG=0; gate on: unset",
  "# knobs: BLADE_BLAS=off pinned; cleared from the caller: $(if ($cleared.Count) { $cleared -join '; ' } else { 'none set' }); exe cache default (on)",
  "#   inherited: $($inherited -join '; ')",
  "# toolchain: $gxx (C:\msys64\ucrt64\bin first on PATH); $clangRow (doctor row of the new build)",
  "# script: sweep.ps1 SHA-256 $(Sha $PSCommandPath)",
  "# file filter: $(if ($Only) { $Only } else { 'none' })",
  "# $($files.Count) programs ($((Get-ChildItem "$Root\tests\corpus\sql-group-by\*.blade").Count) sql-group-by files + $($Kernels.Count) kernels: $($Kernels -join ', '), before the file filter) x lanes [$($Lanes -join ', ')] x gates [$($Gates -join ', ')] = $total comparisons"
)
foreach ($k in ($tally.Keys | Sort-Object)) { $head += "#   $($k): $($tally[$k])" }
$text = (($head + $lines) -join "`n") + "`n"
[IO.File]::WriteAllText($Out, $text, (New-Object Text.UTF8Encoding($false)))
"$Mode : $total comparisons -> $Out"
$tally.GetEnumerator() | Sort-Object Name | ForEach-Object { "  $($_.Name): $($_.Value)" }
