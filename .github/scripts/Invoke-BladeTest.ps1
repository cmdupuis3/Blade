<#
.SYNOPSIS
    Run one `blade test` verb and decide, defensively, whether it was green.

.DESCRIPTION
    The exit code is the primary gate, and every dispatch arm in
    src/CliSelfTests.fs does return 1 on a failure. It is not the only gate
    here, because a run of this harness has been misread as green before: the
    report of record is the "TOTAL: N passed, N failed" line and the "Failed
    tests:" roll-up under it, and a block that forgets to propagate its count
    is a silent regression rather than a loud one. So the log is re-read for
    those markers too, and any of them disagreeing with a zero exit code fails
    the step.

    Skips are reported but never fail the step -- that is what
    Assert-Toolchain.ps1 is for, and it runs first.

.EXAMPLE
    .\Invoke-BladeTest.ps1 -Blade $exe -Name 'C++ back end' -BladeArgs test, --omp, --mpi
#>
[CmdletBinding()]
param(
    # Path to Blade.exe.
    [Parameter(Mandatory)]
    [string] $Blade,

    # Human label for the log file and the job summary.
    [Parameter(Mandatory)]
    [string] $Name,

    # Where the tee'd console log lands, for upload as a build artifact.
    [string] $LogDirectory = 'ci-logs',

    # The blade verb and its arguments, e.g. test, --omp, --mpi. Not marked
    # Mandatory on purpose: an unbound mandatory parameter PROMPTS, and a
    # prompt on a runner is a job that hangs until its timeout instead of a
    # job that fails in a second.
    [Parameter(ValueFromRemainingArguments)]
    [string[]] $BladeArgs = @()
)

if ($BladeArgs.Count -eq 0) { throw 'Invoke-BladeTest.ps1: no blade arguments given.' }

# A native tool writing to stderr is not, by itself, an error condition.
$ErrorActionPreference = 'Continue'
$PSNativeCommandUseErrorActionPreference = $false

New-Item -ItemType Directory -Force -Path $LogDirectory | Out-Null
$slug = ($Name -replace '[^A-Za-z0-9]+', '-').Trim('-').ToLower()
$log = Join-Path $LogDirectory "$slug.log"

Write-Host "::group::blade $($BladeArgs -join ' ')"
$started = Get-Date
& $Blade @BladeArgs 2>&1 | ForEach-Object { "$_" } | Tee-Object -FilePath $log
$code = $LASTEXITCODE
$elapsed = (Get-Date) - $started
Write-Host '::endgroup::'

$lines = @(Get-Content -Path $log -ErrorAction SilentlyContinue)

# The report of record, in the harness's own words.
$totals = @($lines | Where-Object { $_ -match '^\s*(TOTAL:|Verdict:)' })

$problems = @()
if ($code -ne 0) { $problems += "blade exits $code" }

# The grand-total roll-up. Printed only when something failed.
if ($lines | Where-Object { $_ -match '^\s*Failed tests:' }) {
    $problems += 'the run printed a "Failed tests:" roll-up'
}

# Per-test failure lines, which every failing block prints.
$failLines = @($lines | Where-Object { $_ -match '^\s*\[FAIL\]:' })
if ($failLines.Count -gt 0) {
    $problems += "$($failLines.Count) [FAIL] line(s) in the log"
}

# A non-zero count on a totals line, whatever the exit code claimed.
foreach ($t in $totals) {
    if ($t -match '(\d+)\s+failed' -and [int]$Matches[1] -ne 0) {
        $problems += "totals line reports failures: $($t.Trim())"
    }
}

$summary = @("### $Name", '', "``blade $($BladeArgs -join ' ')`` -- $([int]$elapsed.TotalMinutes)m$($elapsed.Seconds)s, exit $code", '')
if ($totals.Count -gt 0) { $summary += @('```', ($totals | ForEach-Object { $_.Trim() }), '```', '') }
if ($problems.Count -eq 0) { $summary += ':white_check_mark: green' }
else { $summary += @(':x: red', '', ($problems | ForEach-Object { "- $_" })) }
if ($failLines.Count -gt 0) {
    $summary += @('', '<details><summary>failing tests</summary>', '', '```',
                  ($failLines | Select-Object -First 40 | ForEach-Object { $_.Trim() }), '```', '', '</details>')
}
if ($env:GITHUB_STEP_SUMMARY) {
    Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value (($summary -join "`n") + "`n") -Encoding utf8
}

foreach ($t in $totals) { Write-Host "==> $($t.Trim())" }

if ($problems.Count -gt 0) {
    foreach ($p in $problems) { Write-Host "::error title=$Name::$p" }
    exit 1
}

Write-Host "$Name : green"
exit 0
