<#
.SYNOPSIS
    Fail the job when the CI machine is quietly missing a native dependency.

.DESCRIPTION
    Blade's test harness turns a missing toolchain piece into a SKIP, not a
    failure, and skips do not affect the exit code. That is the right behaviour
    for a developer box and the wrong one for CI: a run where libnetcdf never
    installed, or clang went missing, is a green run that tested nothing.

    `blade doctor --json` is the designed CI surface for exactly this -- it
    compiles and runs a probe per dependency rather than sniffing for files --
    so assert on its rows before any test verb gets to skip its way to green.

    Doctor's own exit code only covers the g++ core ("optional rows never fail
    the exit code; CI gates on the core, reads the rest from --json"), which is
    why -Require exists.
#>
[CmdletBinding()]
param(
    # Path to Blade.exe.
    [Parameter(Mandatory)]
    [string] $Blade,

    # Doctor row keys that must report "ok". Known keys: dotnet, gpp, llvm,
    # blas, lapack, netcdf, mpi, cuda, stdlib.
    [Parameter(Mandatory)]
    [string[]] $Require
)

$ErrorActionPreference = 'Stop'

$raw = & $Blade doctor --json
if ($LASTEXITCODE -ne 0) {
    Write-Host $raw
    throw "blade doctor exits $LASTEXITCODE -- the g++ core is unhealthy, so nothing below it can be trusted."
}

$line = @($raw) | Where-Object { $_ -match '^\s*\{' } | Select-Object -Last 1
if (-not $line) {
    Write-Host $raw
    throw 'blade doctor --json produced no JSON object.'
}
$report = $line | ConvertFrom-Json

$rows = foreach ($c in $report.checks) {
    [pscustomobject]@{
        Status   = $c.status
        Required = if ($Require -contains $c.key) { 'yes' } else { '' }
        Key      = $c.key
        Detail   = $c.detail
    }
}
$rows | Format-Table -AutoSize | Out-String -Width 200 | Write-Host

if ($env:GITHUB_STEP_SUMMARY) {
    $md = @("### Toolchain (``blade doctor``) on $($report.os)/$($report.arch)", '',
            '| | check | detail |', '|---|---|---|')
    foreach ($r in $rows) {
        $mark = switch ($r.Status) { 'ok' { ':white_check_mark:' } 'off' { ':heavy_minus_sign:' }
                                     'warn' { ':warning:' } default { ':x:' } }
        $md += "| $mark | ``$($r.Key)`` | $($r.Detail -replace '\|', '\|') |"
    }
    $md += ''
    Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value ($md -join "`n") -Encoding utf8
}

$missing = foreach ($key in $Require) {
    $row = $report.checks | Where-Object { $_.key -eq $key } | Select-Object -First 1
    if (-not $row) { "$key (no such doctor row)" }
    elseif ($row.status -ne 'ok') { "$key is '$($row.status)': $($row.detail)" }
}

if ($missing) {
    Write-Host '::error::the CI toolchain is incomplete; the suites below would SKIP rather than fail'
    foreach ($m in $missing) { Write-Host "::error::$m" }
    throw "required toolchain rows are not ok: $($missing -join '; ')"
}

Write-Host "toolchain ok: $($Require -join ', ')"
