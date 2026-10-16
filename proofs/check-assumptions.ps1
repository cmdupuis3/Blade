<#
.SYNOPSIS
    Checks that every counted result in the axiom-free tower is closed.

.DESCRIPTION
    For every Lemma/Theorem/Corollary/Example that count-theorems.ps1 counts
    (same comment stripping, same declaration pattern), runs
    `Print Assumptions` on its fully qualified name and FAILS unless Coq
    answers "Closed under the global context". The tower must already be
    built (its .vo files present); this script compiles nothing in it.

    `coqchk -o` is not a substitute: it lists every axiom anywhere in the
    loaded environment, including the standard library's real-number axioms,
    which are not dependencies of any tower result (audit 2026-10-15).

    The reals/ and floats/ lanes are deliberately NOT checked here: they use
    Coq's Reals axioms and primitive floats by design.

.PARAMETER Coqc
    The coqc to use. Default: `coqc` on PATH.

.PARAMETER EmitProbe
    Write the generated probe (`Print Assumptions` for every counted result)
    to this path and stop, without running coqc. CI uses it: the probe is
    generated where PowerShell is, and compiled in the Rocq container, which
    checks that every answer is "Closed under the global context".

.EXAMPLE
    pwsh -File proofs/check-assumptions.ps1
    pwsh -File proofs/check-assumptions.ps1 -Coqc C:\Rocq-Platform~9.0~2025.08\bin\coqc.exe
#>
[CmdletBinding()]
param(
    [string]$Coqc = 'coqc',
    [string]$EmitProbe = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$proofsDir = $PSScriptRoot

# Same comment stripper as count-theorems.ps1 (comments nest; newlines kept).
function Remove-CoqComments {
    param([string]$Text)
    $out = New-Object System.Text.StringBuilder
    $depth = 0; $i = 0; $n = $Text.Length
    while ($i -lt $n) {
        if ($i + 1 -lt $n -and $Text[$i] -eq '(' -and $Text[$i + 1] -eq '*') { $depth++; $i += 2; continue }
        if ($depth -gt 0 -and $i + 1 -lt $n -and $Text[$i] -eq '*' -and $Text[$i + 1] -eq ')') { $depth--; $i += 2; continue }
        if ($depth -eq 0) { [void]$out.Append($Text[$i]) } elseif ($Text[$i] -eq "`n") { [void]$out.Append("`n") }
        $i++
    }
    $out.ToString()
}

$declRe    = '^\s*(?:Local\s+|Global\s+|#\[[^\]]*\]\s*)*(?:Lemma|Theorem|Corollary|Example)\s+([A-Za-z_][A-Za-z0-9_'']*)'
$moduleRe  = '^\s*Module\s+(?!Type\b)(?:Import\s+|Export\s+)?([A-Za-z_][A-Za-z0-9_'']*)\s*\.\s*$'
$sectionRe = '^\s*Section\s+([A-Za-z_][A-Za-z0-9_'']*)\s*\.'
$endRe     = '^\s*End\s+([A-Za-z_][A-Za-z0-9_'']*)\s*\.'

$files = Get-Content -LiteralPath (Join-Path $proofsDir '_CoqProject') |
    ForEach-Object { $_.Trim() } | Where-Object { $_ -and $_ -notmatch '^-' }

# Fully qualified names, in file order. Modules qualify a name; Sections don't.
$names = New-Object System.Collections.Generic.List[string]
foreach ($f in $files) {
    $lib = 'Blade.' + [IO.Path]::GetFileNameWithoutExtension($f)
    $stack = New-Object System.Collections.Generic.List[object]
    $body = Remove-CoqComments (Get-Content -LiteralPath (Join-Path $proofsDir $f) -Raw)
    foreach ($line in ($body -split "`n")) {
        if ($line -match $moduleRe) { $stack.Add(@('M', $Matches[1])); continue }
        if ($line -match $sectionRe) { $stack.Add(@('S', $Matches[1])); continue }
        if ($line -match $endRe -and $stack.Count -gt 0 -and $stack[$stack.Count - 1][1] -eq $Matches[1]) {
            $stack.RemoveAt($stack.Count - 1); continue
        }
        if ($line -match $declRe) {
            $mods = @($stack | Where-Object { $_[0] -eq 'M' } | ForEach-Object { $_[1] })
            $names.Add((@($lib) + $mods + @($Matches[1])) -join '.')
        }
    }
}

# A Coq module name: letters, digits and underscores only.
$probe = Join-Path ([IO.Path]::GetTempPath()) ("BladeAssumptions_" + [guid]::NewGuid().ToString('N') + '.v')
$sb = New-Object System.Text.StringBuilder
foreach ($f in $files) {
    [void]$sb.AppendLine("From Blade Require " + [IO.Path]::GetFileNameWithoutExtension($f) + '.')
}
foreach ($n in $names) { [void]$sb.AppendLine("Print Assumptions $n.") }

if ($EmitProbe) {
    [IO.File]::WriteAllText($EmitProbe, $sb.ToString())
    Write-Host "Wrote $($names.Count) Print Assumptions queries to $EmitProbe"
    return
}

[IO.File]::WriteAllText($probe, $sb.ToString())

try {
    # coqc writes to stderr; under Windows PowerShell 5.1 a redirected native
    # stderr line is an error record, which 'Stop' would turn into a throw.
    $ErrorActionPreference = 'Continue'
    $output = & $Coqc -Q $proofsDir Blade $probe 2>&1 | ForEach-Object { "$_" }
    $ErrorActionPreference = 'Stop'
    if ($LASTEXITCODE -ne 0) {
        $output | Select-Object -Last 20 | ForEach-Object { Write-Host $_ }
        throw "coqc failed on the assumptions probe (is the tower built?)"
    }
} finally {
    Remove-Item -LiteralPath $probe -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath ([IO.Path]::ChangeExtension($probe, '.vo')), ([IO.Path]::ChangeExtension($probe, '.glob')), ([IO.Path]::ChangeExtension($probe, '.vok')), ([IO.Path]::ChangeExtension($probe, '.vos')) -ErrorAction SilentlyContinue
}

# Each Print Assumptions answer starts with one of these two lines, in order.
$verdicts = @($output | Where-Object { $_ -match '^(Closed under the global context|Axioms:)' })
if ($verdicts.Count -ne $names.Count) {
    throw "expected $($names.Count) Print Assumptions answers, parsed $($verdicts.Count)"
}
$open = @()
for ($k = 0; $k -lt $names.Count; $k++) {
    if ($verdicts[$k] -notmatch '^Closed') { $open += $names[$k] }
}
if ($open.Count -gt 0) {
    foreach ($o in $open) { Write-Host "NOT CLOSED: $o" }
    exit 1
}
Write-Host "OK: all $($names.Count) tower results are closed under the global context."
