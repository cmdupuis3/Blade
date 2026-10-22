# W3-1 cross-version sweep (round 3, gap G71): compare two Blade executables
# over every tests/corpus/sql-group-by file plus the groupagg / groupagg_dyn
# kernels, and write a manifest with one line per file x lane x gate.
#
#   -Mode emit  : `blade emit` stdout of both executables, byte for byte
#                 (the C++ lane, and the LLVM lane requested: BLADE_LLVM=1)
#   -Mode value : `blade run` of both executables, the LLVM lane requested,
#                 BLADE_PRINT_BITS=1: exit status + stdout (raw Float64 bits)
#                 + stderr, minus the timing line and the private build-path
#                 lines; and, beside it, whether the .ll emission is identical
#
# Gates: -Gates 0 (BLADE_GROUP_AGG=0), on (unset), or both.
# Usage (PowerShell 5.1, from any directory; g++ / clang as for `blade run`):
#   powershell -NoProfile -ExecutionPolicy Bypass -File sweep.ps1 `
#       -OldExe <361504b6 build>\Blade.exe -NewExe <candidate>\Blade.exe `
#       -Root <checkout> -Mode emit -Gates 0,on -Lanes cpp,llvm -Out manifest.txt
# Build the baseline from a clean export: `git archive 361504b6 | tar -x` into
# a scratch directory, then `dotnet build Blade.fsproj -c Release` there.
param(
    [Parameter(Mandatory = $true)][string]$OldExe,
    [Parameter(Mandatory = $true)][string]$NewExe,
    [Parameter(Mandatory = $true)][string]$Root,
    [ValidateSet("emit", "value")][string]$Mode = "emit",
    [string[]]$Gates = @("0", "on"),
    [string[]]$Lanes = @("cpp", "llvm"),
    [string[]]$Kernels = @("groupagg", "groupagg_dyn"),
    [Parameter(Mandatory = $true)][string]$Out
)
$ErrorActionPreference = 'Continue'
# `-File` passes a comma list as one string: split it here
$Gates = @($Gates | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$Lanes = @($Lanes | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$Kernels = @($Kernels | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$env:PATH = "C:\msys64\ucrt64\bin;" + $env:PATH
$files = @(Get-ChildItem "$Root\tests\corpus\sql-group-by\*.blade" | Sort-Object Name | ForEach-Object FullName)
foreach ($k in $Kernels) { $files += "$Root\bench\kernels\$k\$k.blade" }
$work = Join-Path ([IO.Path]::GetTempPath()) ("w31sweep-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force $work | Out-Null
Push-Location $work
$keep = { param($path) ((Get-Content $path) | Where-Object { $_ -notmatch " completed in " -and $_ -notmatch "blade-build" -and $_ -notmatch "^\[(Emit|Build|cache)\]" }) -join "`n" }
$lines = New-Object System.Collections.Generic.List[string]
$same = 0; $total = 0
foreach ($lane in $Lanes) {
  foreach ($gate in $Gates) {
    if ($lane -eq "llvm") { $env:BLADE_LLVM = "1" } else { $env:BLADE_LLVM = $null }
    if ($gate -eq "0") { $env:BLADE_GROUP_AGG = "0" } else { $env:BLADE_GROUP_AGG = $null }
    foreach ($f in $files) {
      $leaf = Split-Path $f -Leaf
      $ea = (& $OldExe emit $f 2>$null | Out-String)
      $eb = (& $NewExe emit $f 2>$null | Out-String)
      $emitSame = ($ea -eq $eb)
      if ($Mode -eq "emit") {
        $total++
        if ($emitSame) { $same++ }
        $lines.Add(("{0,-5} gate={1,-3} {2}: emission {3}" -f $lane, $gate, $leaf, $(if ($emitSame) { "identical" } else { "DIFFERS" })))
      } else {
        $env:BLADE_PRINT_BITS = "1"
        cmd /c "`"$OldExe`" run `"$f`" 1> `"$work\a.out`" 2> `"$work\a.err`""
        $ca = $LASTEXITCODE
        cmd /c "`"$NewExe`" run `"$f`" 1> `"$work\b.out`" 2> `"$work\b.err`""
        $cb = $LASTEXITCODE
        $env:BLADE_PRINT_BITS = $null
        $ra = "exit $ca`n" + (& $keep "$work\a.out") + "`n--`n" + (& $keep "$work\a.err")
        $rb = "exit $cb`n" + (& $keep "$work\b.out") + "`n--`n" + (& $keep "$work\b.err")
        $valSame = ($ra -eq $rb)
        $routed = -not ((Get-Content "$work\b.err") -match "falling back to C\+\+")
        $total++
        if ($valSame) { $same++ }
        $lines.Add(("{0,-5} gate={1,-3} {2}: value {3}; emission {4}; new build {5}" -f $lane, $gate, $leaf, $(if ($valSame) { "identical" } else { "DIFFERS" }), $(if ($emitSame) { "identical" } else { "differs" }), $(if ($lane -eq "llvm") { if ($routed) { "ran the LLVM lane" } else { "fell back to C++" } } else { "C++" })))
      }
    }
  }
}
$env:BLADE_LLVM = $null; $env:BLADE_GROUP_AGG = $null
Pop-Location
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
$head = @(
  "# W3-1 sweep, mode $Mode, $(Get-Date -Format 'yyyy-MM-dd HH:mm')",
  "# old: $OldExe",
  "# new: $NewExe",
  "# $($files.Count) programs ($((Get-ChildItem "$Root\tests\corpus\sql-group-by\*.blade").Count) sql-group-by files + $($Kernels.Count) kernels) x lanes [$($Lanes -join ', ')] x gates [$($Gates -join ', ')] = $total comparisons; $same identical"
)
($head + $lines) | Out-File -Encoding utf8 $Out
"$Mode : $same of $total identical -> $Out"
