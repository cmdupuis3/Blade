<#
.SYNOPSIS
    Build tools/gr-render/gr-render[.exe] against a GR install.

.DESCRIPTION
    Resolution order for the GR root: -GrDir, then $env:GRDIR, then the two
    vendored trees (the Blade-REPL worktree subset first, the full tree second).

    The static-runtime flags are MANDATORY on Windows, not an optimisation:
    this machine's MSYS2 UCRT64 g++ is ABI-incompatible with the older MinGW
    runtime DLLs GR ships, and a plain `-lGR` build dies at load with
    STATUS_ENTRYPOINT_NOT_FOUND. Static-linking libgcc/libstdc++ leaves
    libGR.dll as the only runtime dependency.

    This script also runs on linux/macOS (invoked by package.ps1, and by
    .github/workflows/gr-render.yml's CI matrix) via pwsh, which is
    preinstalled on all three GitHub-hosted runner images. The Windows path
    (MSYS2 UCRT64 g++, -static-libgcc/-static-libstdc++, a `libGR.dll.a`
    import library) is exercised and verified on this machine. The
    linux/macOS branches below are UNVERIFIED -- there is no non-Windows
    toolchain or GR install on this machine to build and link against, so
    every choice made for them (compiler selection, which library file marks
    a valid GR root, whether static-linking libstdc++ is safe) is a
    best-effort guess, called out inline. Whoever first runs this on a
    linux/macOS runner (or a real linux/macOS dev box) should treat a build
    failure there as evidence to fix, not a sign the guess was reckless.

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

# ---------------------------------------------------------------------------
# Platform: which OS, which exe suffix, which compile recipe.
#
# Spelled "win32"/"linux"/"darwin" -- Node's process.platform vocabulary, kept
# in sync with deps.json's `gr` asset keys, the Blade-REPL extension's
# fetch-vendor.js, and src/display/GrRender.fs's stampedHelperLeaf -- so
# "which platform" means the same three strings everywhere in the toolchain.
# ---------------------------------------------------------------------------

# NOT $isWindows/$isMacOS/$isLinux: PowerShell variable names are case-insensitive,
# and pwsh 7 defines $IsWindows/$IsMacOS/$IsLinux as READ-ONLY automatic variables,
# so assigning them is a hard error under the `shell: pwsh` that CI runs these
# scripts with. It only ever worked locally because Windows PowerShell 5.1 has no
# such automatic variables.
$onWindows = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)
$onMacOS = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::OSX)
$onLinux = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Linux)
$platform = if ($onWindows) { 'win32' } elseif ($onMacOS) { 'darwin' } elseif ($onLinux) { 'linux' } else { throw "unrecognized platform (not Windows, macOS or Linux)" }
$exeExt = if ($onWindows) { '.exe' } else { '' }

function Resolve-GrDir {
    param([string]$Explicit)
    $candidates = @()
    if ($Explicit) { $candidates += $Explicit }
    if ($env:GRDIR) { $candidates += $env:GRDIR }
    $candidates += 'C:\Users\cdupu\Documents\GitHub\Blade-REPL\.claude\worktrees\gr-graphics-plan-67007c\vendor\gr'
    $candidates += 'C:\Users\cdupu\Documents\GitHub\Blade-REPL\vendor\gr'
    foreach ($c in $candidates) {
        if (-not $c) { continue }
        $header = Join-Path (Join-Path $c 'include') 'gr.h'
        if (-not (Test-Path $header)) { continue }
        # Which library file proves "this is a linkable GR install" is
        # platform-specific. Windows ships the MinGW import library
        # (libGR.dll.a) alongside the DLL; that part is verified (the vendored
        # tree at Blade-REPL/.claude/worktrees/.../vendor/gr has exactly this
        # layout). linux/macOS are a best guess at the sciapp/gr release
        # tarball layout (a .so/.dylib or a static .a under lib/) --
        # UNVERIFIED, no such tarball has been inspected on this machine.
        $hasLib =
            switch ($platform) {
                'win32'  { Test-Path (Join-Path (Join-Path $c 'lib') 'libGR.dll.a') }
                'linux'  { (Test-Path (Join-Path (Join-Path $c 'lib') 'libGR.so')) -or (Test-Path (Join-Path (Join-Path $c 'lib') 'libGR.a')) }
                'darwin' { (Test-Path (Join-Path (Join-Path $c 'lib') 'libGR.dylib')) -or (Test-Path (Join-Path (Join-Path $c 'lib') 'libGR.a')) }
            }
        if ($hasLib) { return (Resolve-Path $c).Path }
    }
    throw "No usable GR install found (looked for include/gr.h + a platform-appropriate lib/libGR.* in: $($candidates -join '; ')). Pass -GrDir or set GRDIR."
}

$gr = Resolve-GrDir -Explicit $GrDir
$exe = Join-Path $here ("gr-render$exeExt")
$sources = @('main.cpp', 'json.hpp', 'figure.hpp', 'render.hpp', 'colormaps.hpp', 'base64.hpp') |
    ForEach-Object { Join-Path $here $_ }

foreach ($s in $sources) {
    if (-not (Test-Path $s)) { throw "missing source file: $s" }
}

# The GR import/link library whose mtime also gates the idempotent skip below
# -- same platform split as Resolve-GrDir's existence check.
$grLinkLib =
    switch ($platform) {
        'win32'  { Join-Path (Join-Path $gr 'lib') 'libGR.dll.a' }
        'linux'  { $l = Join-Path (Join-Path $gr 'lib') 'libGR.so'; if (Test-Path $l) { $l } else { Join-Path (Join-Path $gr 'lib') 'libGR.a' } }
        'darwin' { $l = Join-Path (Join-Path $gr 'lib') 'libGR.dylib'; if (Test-Path $l) { $l } else { Join-Path (Join-Path $gr 'lib') 'libGR.a' } }
    }

# Idempotent: skip the compile when the exe is newer than every source and the
# GR link library it was linked against.
if ((-not $Force) -and (Test-Path $exe)) {
    $exeTime = (Get-Item $exe).LastWriteTimeUtc
    $newest = ($sources + $grLinkLib) |
        ForEach-Object { (Get-Item $_).LastWriteTimeUtc } |
        Sort-Object -Descending | Select-Object -First 1
    if ($exeTime -gt $newest) {
        Write-Host "gr-render: up to date (GR: $gr)"
        Write-Output $exe
        exit 0
    }
}

# Compiler selection: g++ everywhere it's on PATH (on macOS Xcode's command
# line tools alias `g++` to clang, so this still picks up a working compiler
# there), else clang++ as a fallback for a bare-clang macOS/linux box.
$gpp = (Get-Command g++ -ErrorAction SilentlyContinue)
if (-not $gpp) { $gpp = (Get-Command clang++ -ErrorAction SilentlyContinue) }
if (-not $gpp) { throw "no g++ or clang++ found on PATH" }

# Static-runtime flags: MANDATORY on Windows (see the doc comment above).
# Carried over to linux as portability hardening (a binary that doesn't
# depend on the build box's exact libstdc++ version travels better) --
# UNVERIFIED whether it's needed OR safe on a real linux runner; if a
# distro's g++ rejects it or it changes behavior, drop it there first.
# Omitted on macOS: -static-libstdc++ is a GCC flag with no clang/libc++
# equivalent, and Apple's toolchain does not support statically linking the
# system C++ runtime the way MinGW/glibc toolchains do.
$staticFlags =
    switch ($platform) {
        'win32'  { @('-static-libgcc', '-static-libstdc++') }
        'linux'  { @('-static-libgcc', '-static-libstdc++') }
        'darwin' { @() }
    }

$compileArgs = @(
    (Join-Path $here 'main.cpp'),
    '-I', (Join-Path $gr 'include'),
    '-L', (Join-Path $gr 'lib'),
    '-lGR'
) + $staticFlags + @(
    '-std=c++17', '-O2', '-Wall',
    '-o', $exe
)

Write-Host "gr-render: compiling against $gr ($platform)"
& $gpp.Source @compileArgs
if ($LASTEXITCODE -ne 0) { throw "$($gpp.Name) failed with exit code $LASTEXITCODE" }
if (-not (Test-Path $exe)) { throw "$($gpp.Name) reported success but $exe is missing" }
if (-not $onWindows) {
    try { & chmod +x $exe } catch { Write-Warning "chmod +x $exe failed: $_" }
}

Write-Host "gr-render: built $exe"
Write-Output $exe
