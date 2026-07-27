<#
.SYNOPSIS
    Counts the machine-checked results in the Blade proof tower.

.DESCRIPTION
    THE COUNTING CONVENTION (the one this repo documents and uses):

      One item per `Lemma`, `Theorem`, `Corollary`, or `Example` declaration
      that begins a line -- optionally preceded by `Local`, `Global`, or an
      `#[...]` attribute -- in the .v files listed in `_CoqProject`, after
      Coq comments (nested `(* ... *)`) are stripped.

      `Definition` and `Fixpoint` are constructions, not claims, and are NOT
      counted. `Example` IS counted: in this tower the Examples are the
      concrete computed pins (`off_diagonal_x2`, `s2_worked_count_1`, ...),
      which are checked facts like any other.

    The same number is quoted in two places, and both must agree with this
    script:

      - proofs/README.md -- the `## Contents (N theorems total)` heading and
        every per-file `- BladeX.v (n):` entry.
      - docs/proofs.md   -- the `**N theorems**` headline.

.PARAMETER Check
    Verify the numbers written in proofs/README.md and docs/proofs.md against
    the mechanical count. Exits 1 on any mismatch. Use this after adding a
    file or a theorem.

.EXAMPLE
    pwsh -File proofs/count-theorems.ps1
    pwsh -File proofs/count-theorems.ps1 -Check
#>
[CmdletBinding()]
param(
    [switch]$Check
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$proofsDir = $PSScriptRoot
$repoRoot  = Split-Path -Parent $proofsDir

# Strip Coq comments, which nest. Newlines are preserved so that line-anchored
# matching still sees a declaration that followed a comment on the same line.
function Remove-CoqComments {
    param([string]$Text)

    $out   = New-Object System.Text.StringBuilder
    $depth = 0
    $i     = 0
    $n     = $Text.Length

    while ($i -lt $n) {
        if ($i + 1 -lt $n -and $Text[$i] -eq '(' -and $Text[$i + 1] -eq '*') {
            $depth++; $i += 2; continue
        }
        if ($depth -gt 0 -and $i + 1 -lt $n -and $Text[$i] -eq '*' -and $Text[$i + 1] -eq ')') {
            $depth--; $i += 2; continue
        }
        if ($depth -eq 0) {
            [void]$out.Append($Text[$i])
        } elseif ($Text[$i] -eq "`n") {
            [void]$out.Append("`n")
        }
        $i++
    }
    return $out.ToString()
}

$declPattern = '^\s*(?:Local\s+|Global\s+|#\[[^\]]*\]\s*)*(?:Lemma|Theorem|Corollary|Example)\b'

# Build order is _CoqProject's; it is also the authoritative file list.
$coqProject = Join-Path $proofsDir '_CoqProject'
$files = Get-Content -LiteralPath $coqProject |
    ForEach-Object { $_.Trim() } |
    Where-Object { $_ -and $_ -notmatch '^-' }

$counts = [ordered]@{}
foreach ($f in $files) {
    $path = Join-Path $proofsDir $f
    if (-not (Test-Path -LiteralPath $path)) {
        throw "_CoqProject lists $f but it is not in $proofsDir"
    }
    $body = Remove-CoqComments (Get-Content -LiteralPath $path -Raw)
    $counts[$f] = ([regex]::Matches($body, $declPattern, 'Multiline')).Count
}

$total = ($counts.Values | Measure-Object -Sum).Sum

foreach ($f in $counts.Keys) {
    '{0,5}  {1}' -f $counts[$f], $f
}
'{0,5}  TOTAL ({1} files)' -f $total, $counts.Count

# A .v file that never made it into _CoqProject is not built, not checked, and
# not counted -- and that is exactly how BladeJacobian.v went missing from the
# README. Flag it either way.
$orphans = Get-ChildItem -LiteralPath $proofsDir -Filter '*.v' |
    Where-Object { $files -notcontains $_.Name } |
    ForEach-Object { $_.Name }
if ($orphans) {
    Write-Warning ("Not in _CoqProject (unbuilt, uncounted): " + ($orphans -join ', '))
}

if (-not $Check) { return }

$problems = New-Object System.Collections.Generic.List[string]

$readmePath = Join-Path $proofsDir 'README.md'
$readme     = Get-Content -LiteralPath $readmePath -Raw

if ($readme -match '##\s+Contents\s+\((\d+)\s+theorems\s+total\)') {
    $stated = [int]$Matches[1]
    if ($stated -ne $total) {
        $problems.Add("proofs/README.md: heading says $stated theorems total, mechanical count is $total")
    }
} else {
    $problems.Add("proofs/README.md: no '## Contents (N theorems total)' heading found")
}

foreach ($f in $counts.Keys) {
    $entry = [regex]::Match($readme, ('(?m)^-\s+' + [regex]::Escape($f) + '\s+\((\d+)\)'))
    if (-not $entry.Success) {
        $problems.Add("proofs/README.md: no contents entry for $f")
    } elseif ([int]$entry.Groups[1].Value -ne $counts[$f]) {
        $problems.Add("proofs/README.md: $f entry says $($entry.Groups[1].Value), mechanical count is $($counts[$f])")
    }
}

$proseDoc = Join-Path $repoRoot 'docs/proofs.md'
$prose    = Get-Content -LiteralPath $proseDoc -Raw

if ($prose -match '\*\*(\d+)\s+theorems\*\*') {
    $stated = [int]$Matches[1]
    if ($stated -ne $total) {
        $problems.Add("docs/proofs.md: headline says $stated theorems, mechanical count is $total")
    }
} else {
    $problems.Add("docs/proofs.md: no '**N theorems**' headline found")
}

# docs/proofs.md carries one per-file count, on the BladeCore heading.
$coreHeading = [regex]::Match($prose, '(?m)^##\s+BladeCore\.v\s+\((\d+)\s+theorems\)')
if ($coreHeading.Success -and [int]$coreHeading.Groups[1].Value -ne $counts['BladeCore.v']) {
    $problems.Add("docs/proofs.md: BladeCore.v heading says $($coreHeading.Groups[1].Value), mechanical count is $($counts['BladeCore.v'])")
}

''
if ($problems.Count -eq 0) {
    Write-Host "OK: proofs/README.md and docs/proofs.md agree with the mechanical count ($total)."
} else {
    foreach ($p in $problems) { Write-Host "MISMATCH: $p" }
    exit 1
}
