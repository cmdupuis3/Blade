<#
.SYNOPSIS
    Build tools/gr-render/gr-render.exe against a GR install.

.DESCRIPTION
    Resolution order for the GR root: -GrDir, then $env:GRDIR, then the two
    vendored trees (the Blade-REPL worktree subset first, the full tree second).

    The static-runtime flags are MANDATORY, not an optimisation: this machine's
    MSYS2 UCRT64 g++ is ABI-incompatible with the older MinGW runtime DLLs GR
    ships, and a plain `-lGR` build dies at load with STATUS_ENTRYPOINT_NOT_FOUND.
    Static-linking libgcc/libstdc++ leaves libGR.dll as the only runtime
    dependency.

.EXAMPLE
    powershell -File build.ps1
    powershell -File build.ps1 -GrDir C:\path\to\gr -Force
#>
[CmdletBinding()]
param(
    [string]$GrDir,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

function Resolve-GrDir {
    param([string]$Explicit)
    $candidates = @()
    if ($Explicit) { $candidates += $Explicit }
    if ($env:GRDIR) { $candidates += $env:GRDIR }
    $candidates += 'C:\Users\cdupu\Documents\GitHub\Blade-REPL\.claude\worktrees\gr-graphics-plan-67007c\vendor\gr'
    $candidates += 'C:\Users\cdupu\Documents\GitHub\Blade-REPL\vendor\gr'
    foreach ($c in $candidates) {
        if ($c -and (Test-Path (Join-Path $c 'include\gr.h')) -and (Test-Path (Join-Path $c 'lib\libGR.dll.a'))) {
            return (Resolve-Path $c).Path
        }
    }
    throw "No usable GR install found (looked for include\gr.h + lib\libGR.dll.a in: $($candidates -join '; ')). Pass -GrDir or set GRDIR."
}

$gr = Resolve-GrDir -Explicit $GrDir
$exe = Join-Path $here 'gr-render.exe'
$sources = @('main.cpp', 'json.hpp', 'figure.hpp', 'render.hpp', 'colormaps.hpp', 'base64.hpp') |
    ForEach-Object { Join-Path $here $_ }

foreach ($s in $sources) {
    if (-not (Test-Path $s)) { throw "missing source file: $s" }
}

# Idempotent: skip the compile when the exe is newer than every source and the
# GR import library it was linked against.
if ((-not $Force) -and (Test-Path $exe)) {
    $exeTime = (Get-Item $exe).LastWriteTimeUtc
    $newest = ($sources + (Join-Path $gr 'lib\libGR.dll.a')) |
        ForEach-Object { (Get-Item $_).LastWriteTimeUtc } |
        Sort-Object -Descending | Select-Object -First 1
    if ($exeTime -gt $newest) {
        Write-Host "gr-render: up to date (GR: $gr)"
        Write-Output $exe
        exit 0
    }
}

$gpp = (Get-Command g++ -ErrorAction SilentlyContinue)
if (-not $gpp) { throw "g++ not found on PATH (MSYS2 UCRT64 g++ is the tested toolchain)." }

$args = @(
    (Join-Path $here 'main.cpp'),
    '-I', (Join-Path $gr 'include'),
    '-L', (Join-Path $gr 'lib'),
    '-lGR',
    '-static-libgcc', '-static-libstdc++',
    '-std=c++17', '-O2', '-Wall',
    '-o', $exe
)

Write-Host "gr-render: compiling against $gr"
& $gpp.Source @args
if ($LASTEXITCODE -ne 0) { throw "g++ failed with exit code $LASTEXITCODE" }
if (-not (Test-Path $exe)) { throw "g++ reported success but $exe is missing" }

Write-Host "gr-render: built $exe"
Write-Output $exe
