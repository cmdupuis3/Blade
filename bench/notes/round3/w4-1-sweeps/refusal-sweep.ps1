# W4-1 refusal sweep (round 3 wave 4). Run from the repository root after a
# Release build:  powershell -File bench/notes/round3/w4-1-sweeps/refusal-sweep.ps1
#
# `blade check` (typecheck + lowering, no C++) over EVERY .blade file found by
# RECURSIVE enumeration of tests/corpus and examples (examples/physics
# included), recording each file that the two W4-1 lowering refusals reject:
#   BL4005 "a write through ..., a deferred computation" / "a write whose target's storage could not be traced"
#   BL6002 "an `if` value that selects between computed arrays" / "an `if` value whose arm reads"
# NOT covered: stdlib/*.blade (library modules, imported by programs that are
# covered), the code fences in CLAUDE.md / docs (`blade test docs` checks those
# separately), and multi-file corpus members checked as single files (a member
# that fails to resolve its imports reports that error instead -- counted
# under `other-error`, never as a hit).
param([string]$Manifest = "bench/notes/round3/w4-1-sweeps/refusal-sweep-manifest.txt")
$b = (Resolve-Path ".\bin\Release\net10.0\Blade.exe").Path
$roots = @("tests/corpus", "examples")
$files = foreach ($r in $roots) { Get-ChildItem $r -Recurse -Filter *.blade }
$hits = New-Object System.Collections.Generic.List[string]
$counts = @{}
$otherErr = 0
foreach ($f in $files) {
    $rel = $f.FullName.Substring((Get-Location).Path.Length + 1).Replace('\', '/')
    $top = ($rel -split '/')[0..1] -join '/'
    if ($rel.StartsWith("examples/physics/")) { $top = "examples/physics" }
    $counts[$top] = 1 + $(if ($counts.ContainsKey($top)) { $counts[$top] } else { 0 })
    $out = (& $b check $f.FullName 2>&1 | Out-String)
    $hit = $out -match 'a write through .*, a deferred computation' -or $out -match "a write whose target's storage could not be traced" -or $out -match 'an `if` value (that selects between computed arrays|whose arm reads)'
    if ($hit) {
        $first = ($out -split "`n" | Where-Object { $_ -match 'error\[' } | Select-Object -First 1)
        $hits.Add("$rel :: $first".Trim())
    } elseif ($out -match 'error\[') { $otherErr++ }
}
$lines = @("W4-1 refusal sweep -- blade check over tests/corpus + examples (recursive)", "binary: $b", "files checked: $($files.Count)", "files refusing with another error (rejects probes, multi-file members, ...): $otherErr", "files hitting a W4-1 refusal: $($hits.Count)", "", "per top-level directory:")
$lines += ($counts.GetEnumerator() | Sort-Object Name | ForEach-Object { "  $($_.Name): $($_.Value)" })
$lines += @("", "hits:")
$lines += $hits
[IO.File]::WriteAllLines((Join-Path (Get-Location) $Manifest), $lines)
$lines | Select-Object -First 8
