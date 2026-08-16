<#
.SYNOPSIS
    Build gr-render for THIS platform and stamp it into dist/ as
    gr-render-<platform>-<arch>[.exe].

.DESCRIPTION
    A thin wrapper over build.ps1, not a second implementation of it: build.ps1
    already knows the GR-resolution rules, the idempotent-skip rule, and every
    platform's compile flags (see its own doc comment for the win32/linux/macOS
    split and what is verified versus a best-effort guess). package.ps1's only
    job is naming and staging -- run the one build recipe, then copy the
    result into dist/ under the name src/display/GrRender.fs's resolver
    prefers: gr-render-win32-x64.exe, gr-render-linux-x64,
    gr-render-darwin-arm64, etc. (platform spelled the Node
    process.platform/process.arch way -- see GrRender.fs's platformTag).

    Idempotent the same way build.ps1 is: if dist/<stamped-name> already has
    the same sha256 as the freshly built exe, nothing is copied. -Force skips
    that check (and is passed through to build.ps1, so it also forces a
    recompile).

    Prints the artifact path and its sha256 on the last two output lines (in
    that order) so a CI step can capture them with e.g.:

        $lines = & pwsh -File package.ps1
        $path, $sha256 = $lines[-2], $lines[-1]

.EXAMPLE
    powershell -File package.ps1
    powershell -File package.ps1 -GrDir C:\gr -Force
    powershell -File package.ps1 -OutDir C:\out
#>
[CmdletBinding()]
param(
    [string]$GrDir,
    [switch]$Force,
    [string]$OutDir
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $OutDir) { $OutDir = Join-Path $here 'dist' }

# ---------------------------------------------------------------------------
# Platform/arch stamp. Kept in sync BY HAND with three other places that must
# agree on the same vocabulary:
#   - src/display/GrRender.fs's platformTag/stampedHelperLeaf (the consumer:
#     what name it searches for);
#   - deps.json's `gr` asset keys in the Blade-REPL repo (win32-x64,
#     linux-x64, darwin-x64, darwin-arm64);
#   - the Blade-REPL extension's fetch-vendor.js (the same keys, Node-side).
# All four independently compute "win32"/"linux"/"darwin" x "x64"/"arm64"
# rather than sharing code, because they live in different languages
# (PowerShell, F#, Node) with no natural shared module. If you add a platform
# here, add it in GrRender.fs's platformTag too, or the artifact this script
# produces will never be found.
# ---------------------------------------------------------------------------

$isWindows = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)
$isMacOS = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::OSX)
$isLinux = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Linux)
$platform = if ($isWindows) { 'win32' } elseif ($isMacOS) { 'darwin' } elseif ($isLinux) { 'linux' } else { throw "unrecognized platform (not Windows, macOS or Linux)" }

$archRaw = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
$arch =
    switch ($archRaw) {
        'X64'   { 'x64' }
        'Arm64' { 'arm64' }
        default { $archRaw.ToLowerInvariant() }
    }

$exeExt = if ($isWindows) { '.exe' } else { '' }
$stampedName = "gr-render-$platform-$arch$exeExt"

# ---- build via build.ps1 (single source of truth for compiler flags) ------

$builtExe = Join-Path $here ("gr-render$exeExt")
$buildArgs = @()
if ($GrDir) { $buildArgs += @('-GrDir', $GrDir) }
if ($Force) { $buildArgs += '-Force' }

# Re-invoke through whatever shell executable is running THIS script, same
# reasoning as test.ps1: a hardcoded `powershell` doesn't exist on
# linux/macOS, where CI only has `pwsh`.
$shellExe = (Get-Process -Id $PID).Path
$buildLog = & $shellExe -NoProfile -File (Join-Path $here 'build.ps1') @buildArgs 2>&1
$buildExit = $LASTEXITCODE
$buildLog | ForEach-Object { Write-Host $_ }
if ($buildExit -ne 0) { throw "build.ps1 failed with exit code $buildExit" }
if (-not (Test-Path $builtExe)) { throw "build.ps1 reported success but $builtExe is missing" }

# ---- stage into dist/ -------------------------------------------------------

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$dest = Join-Path $OutDir $stampedName

$needsCopy = $true
if ((Test-Path $dest) -and -not $Force) {
    $srcHash = (Get-FileHash -Algorithm SHA256 -Path $builtExe).Hash
    $dstHash = (Get-FileHash -Algorithm SHA256 -Path $dest).Hash
    if ($srcHash -eq $dstHash) { $needsCopy = $false }
}

if ($needsCopy) {
    Copy-Item -Path $builtExe -Destination $dest -Force
    if (-not $isWindows) {
        # Copy-Item's permission-preservation across platforms is not
        # something to trust blindly; make the executable bit explicit.
        try { & chmod +x $dest } catch { Write-Warning "chmod +x $dest failed: $_" }
    }
    Write-Host "gr-render: packaged $dest"
} else {
    Write-Host "gr-render: dist artifact already up to date: $dest"
}

$hash = (Get-FileHash -Algorithm SHA256 -Path $dest).Hash
Write-Host "gr-render: sha256 $hash"

# Last two lines, in this order, are the machine-readable contract (see the
# doc comment above).
Write-Output $dest
Write-Output $hash
