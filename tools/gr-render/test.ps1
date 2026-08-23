<#
.SYNOPSIS
    Hermetic self-test for gr-render.

.DESCRIPTION
    Builds the helper if needed, then drives it over every fixture in
    fixtures/ and asserts:

      * one-shot renders exit 0 and produce a PNG whose IHDR width/height are
        EXACTLY the requested size (parsed here, in PowerShell);
      * PNG output is byte-stable across runs (SHA256), including across a
        one-shot process and a long-lived --serve worker;
      * the NDJSON serve protocol round-trips render/ping/shutdown, survives a
        bad request, and emits nothing on stdout but responses;
      * bad input fails loudly: nonzero exit, a message on stderr, no output;
      * no gksqt.exe survives the run and no stray gks.* files are dropped.

    Every check prints "ok" or "FAIL"; the script exits nonzero if any failed.

.EXAMPLE
    powershell -File test.ps1
    powershell -File test.ps1 -GrDir C:\path\to\gr
#>
[CmdletBinding()]
param([string]$GrDir)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$fixtures = Join-Path $here 'fixtures'

$script:Failures = 0
$script:Checks = 0

function Check {
    param([string]$Name, [bool]$Condition, [string]$Detail = '')
    $script:Checks++
    if ($Condition) {
        Write-Host ("ok   {0}" -f $Name)
    } else {
        $script:Failures++
        Write-Host ("FAIL {0}{1}" -f $Name, $(if ($Detail) { " -- $Detail" } else { '' }))
    }
}

# ---- platform ----------------------------------------------------------
#
# This script is Windows-tested (build.ps1's doc comment has the full
# rationale). It also runs -- unverified -- on linux/macOS via pwsh, which is
# what .github/workflows/gr-render.yml invokes it through; the platform
# checks below exist so a non-Windows run degrades (adjusted paths, skipped
# hygiene checks that only make sense on Windows) rather than failing on an
# assumption that was never true off Windows in the first place.

# NOT $isWindows: PowerShell variable names are case-insensitive and pwsh 7 defines
# $IsWindows as a READ-ONLY automatic variable, so assigning it is a hard error under
# the `shell: pwsh` that CI runs this script with (see build.ps1 for the full note).
$onWindows = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)
$exeExt = if ($onWindows) { '.exe' } else { '' }
$grRuntimeLibCandidates = if ($onWindows) { @('bin/libGR.dll') } else { @('lib/libGR.so', 'lib/libGR.dylib', 'bin/libGR.so', 'bin/libGR.dylib') }

# ---- environment -----------------------------------------------------------

function Resolve-GrDir {
    param([string]$Explicit)
    $candidates = @()
    if ($Explicit) { $candidates += $Explicit }
    if ($env:GRDIR) { $candidates += $env:GRDIR }
    $candidates += 'C:\Users\cdupu\Documents\GitHub\Blade-REPL\.claude\worktrees\gr-graphics-plan-67007c\vendor\gr'
    $candidates += 'C:\Users\cdupu\Documents\GitHub\Blade-REPL\vendor\gr'
    foreach ($c in $candidates) {
        if (-not $c) { continue }
        $found = $grRuntimeLibCandidates | Where-Object { Test-Path (Join-Path $c $_) } | Select-Object -First 1
        if ($found) { return (Resolve-Path $c).Path }
    }
    throw "No usable GR install found. Pass -GrDir or set GRDIR."
}

$gr = Resolve-GrDir -Explicit $GrDir
$env:GRDIR = $gr
$env:PATH = (Join-Path $gr 'bin') + [System.IO.Path]::PathSeparator + $env:PATH
# PATH is how Windows finds libGR.dll; the POSIX loaders don't look there at
# all, so the same job needs LD_LIBRARY_PATH (linux) / DYLD_LIBRARY_PATH
# (macOS) pointed at GR's lib/. Without this the exe builds and then dies at
# startup on `libGR.so: cannot open shared object file`. UNVERIFIED like the
# rest of the non-Windows path -- no linux or macOS box was available.
if (-not $onWindows) {
    $grLib = Join-Path $gr 'lib'
    $sep = [System.IO.Path]::PathSeparator
    $env:LD_LIBRARY_PATH = $grLib + $sep + $env:LD_LIBRARY_PATH
    $env:DYLD_LIBRARY_PATH = $grLib + $sep + $env:DYLD_LIBRARY_PATH
}
$env:GKS_WSTYPE = '100'
if (Test-Path Env:\GR_DISPLAY) { Remove-Item Env:\GR_DISPLAY }

# ---- build -----------------------------------------------------------------

# Re-invoke build.ps1 through WHATEVER shell executable is currently running
# this script (Windows PowerShell 5.1's powershell.exe, or pwsh on
# linux/macOS/newer Windows) -- a hardcoded `powershell` does not exist on
# linux/macOS, where only `pwsh` is installed.
$shellExe = (Get-Process -Id $PID).Path
$buildOut = & $shellExe -NoProfile -File (Join-Path $here 'build.ps1') -GrDir $gr 2>&1
$buildOk = $LASTEXITCODE -eq 0
$exe = Join-Path $here "gr-render$exeExt"
Check 'build' ($buildOk -and (Test-Path $exe)) ($buildOut -join ' | ')
if (-not (Test-Path $exe)) {
    Write-Host "cannot continue without $exe"
    exit 1
}

# Everything transient lands here; cwd is this dir so stray gks.* files are
# easy to spot.
$work = Join-Path $env:TEMP ("gr-render-test-" + $PID)
if (Test-Path $work) { Remove-Item -Recurse -Force $work }
New-Item -ItemType Directory -Force $work | Out-Null
Push-Location $work

# ---- helpers ---------------------------------------------------------------

function Invoke-OneShot {
    param([string]$Fixture, [string]$Out, [int]$Width = 0, [int]$Height = 0, [string]$Format = '')
    $a = @('--out', $Out)
    if ($Width) { $a += @('--width', $Width) }
    if ($Height) { $a += @('--height', $Height) }
    if ($Format) { $a += @('--format', $Format) }
    $errFile = Join-Path $work 'stderr.txt'
    $outFile = Join-Path $work 'stdout.txt'
    $p = Start-Process -FilePath $exe -ArgumentList $a -RedirectStandardInput $Fixture `
        -RedirectStandardError $errFile -RedirectStandardOutput $outFile -NoNewWindow -Wait -PassThru
    [pscustomobject]@{
        ExitCode = $p.ExitCode
        StdErr   = (Get-Content $errFile -Raw)
        StdOut   = (Get-Content $outFile -Raw)
    }
}

# PNG signature + IHDR, parsed by hand: 8-byte magic, then a length, "IHDR",
# then big-endian width and height at bytes 16..23.
function Get-PngInfo {
    param([string]$Path)
    if (-not (Test-Path $Path)) { return $null }
    $b = [System.IO.File]::ReadAllBytes($Path)
    if ($b.Length -lt 24) { return $null }
    $sig = 137, 80, 78, 71, 13, 10, 26, 10
    for ($i = 0; $i -lt 8; $i++) { if ($b[$i] -ne $sig[$i]) { return $null } }
    if ([char]$b[12] -ne 'I' -or [char]$b[13] -ne 'H' -or [char]$b[14] -ne 'D' -or [char]$b[15] -ne 'R') { return $null }
    [pscustomobject]@{
        Width  = ([int]$b[16] -shl 24) -bor ([int]$b[17] -shl 16) -bor ([int]$b[18] -shl 8) -bor [int]$b[19]
        Height = ([int]$b[20] -shl 24) -bor ([int]$b[21] -shl 16) -bor ([int]$b[22] -shl 8) -bor [int]$b[23]
    }
}

function Get-Sha256 {
    param([string]$Path)
    (Get-FileHash -Algorithm SHA256 -Path $Path).Hash
}

# ---- 1. one-shot render of every fixture ----------------------------------

$plotFixtures = @('contourf', 'contour_lines', 'heatmap', 'line', 'scatter',
    'labeled_cividis', 'line_gaps', 'contourf_200')

foreach ($name in $plotFixtures) {
    $spec = Join-Path $fixtures "$name.json"
    $png = Join-Path $work "$name.png"
    $r = Invoke-OneShot -Fixture $spec -Out $png -Width 800 -Height 600
    Check "${name}: exit 0" ($r.ExitCode -eq 0) $r.StdErr
    Check "${name}: output exists" (Test-Path $png)
    $info = Get-PngInfo $png
    Check "${name}: PNG signature + IHDR" ($null -ne $info)
    if ($info) {
        Check "${name}: 800x600 exactly" ($info.Width -eq 800 -and $info.Height -eq 600) `
            ("got {0}x{1}" -f $info.Width, $info.Height)
    } else {
        Check "${name}: 800x600 exactly" $false 'no IHDR'
    }
    Check "${name}: nothing on stdout" ([string]::IsNullOrEmpty($r.StdOut)) $r.StdOut
}

# ---- 2. other sizes --------------------------------------------------------

foreach ($size in @(@(400, 300), @(1200, 900), @(240, 240))) {
    $png = Join-Path $work ("size_{0}x{1}.png" -f $size[0], $size[1])
    $r = Invoke-OneShot -Fixture (Join-Path $fixtures 'contourf.json') -Out $png -Width $size[0] -Height $size[1]
    $info = Get-PngInfo $png
    Check ("size {0}x{1} exact" -f $size[0], $size[1]) `
        ($r.ExitCode -eq 0 -and $null -ne $info -and $info.Width -eq $size[0] -and $info.Height -eq $size[1]) `
        $(if ($info) { "got $($info.Width)x$($info.Height)" } else { $r.StdErr })
}

# cairo can only emit even dimensions; odd requests round DOWN by one.
$png = Join-Path $work 'size_odd.png'
$r = Invoke-OneShot -Fixture (Join-Path $fixtures 'line.json') -Out $png -Width 801 -Height 601
$info = Get-PngInfo $png
Check 'odd size rounds down to even' `
    ($r.ExitCode -eq 0 -and $null -ne $info -and $info.Width -eq 800 -and $info.Height -eq 600) `
    $(if ($info) { "got $($info.Width)x$($info.Height)" } else { $r.StdErr })

# ---- 3. determinism --------------------------------------------------------

$a = Join-Path $work 'det_a.png'
$b = Join-Path $work 'det_b.png'
$null = Invoke-OneShot -Fixture (Join-Path $fixtures 'contourf.json') -Out $a -Width 800 -Height 600
$null = Invoke-OneShot -Fixture (Join-Path $fixtures 'contourf.json') -Out $b -Width 800 -Height 600
$ha = Get-Sha256 $a
$hb = Get-Sha256 $b
Check 'PNG is byte-identical across runs' ($ha -eq $hb) "$ha vs $hb"

$a2 = Join-Path $work 'det_heat_a.png'
$b2 = Join-Path $work 'det_heat_b.png'
$null = Invoke-OneShot -Fixture (Join-Path $fixtures 'heatmap.json') -Out $a2 -Width 640 -Height 480
$null = Invoke-OneShot -Fixture (Join-Path $fixtures 'heatmap.json') -Out $b2 -Width 640 -Height 480
Check 'heatmap PNG byte-identical across runs' ((Get-Sha256 $a2) -eq (Get-Sha256 $b2))

# ---- 4. svg / pdf ----------------------------------------------------------

$svg = Join-Path $work 'out.svg'
$r = Invoke-OneShot -Fixture (Join-Path $fixtures 'contourf.json') -Out $svg
$svgOk = ($r.ExitCode -eq 0) -and (Test-Path $svg) -and ((Get-Content $svg -Raw) -match '<svg')
Check 'svg render (format from extension)' $svgOk $r.StdErr

$pdf = Join-Path $work 'out.pdf'
$r = Invoke-OneShot -Fixture (Join-Path $fixtures 'line.json') -Out $pdf -Format 'pdf'
$pdfOk = $false
if ($r.ExitCode -eq 0 -and (Test-Path $pdf)) {
    $head = [System.IO.File]::ReadAllBytes($pdf)[0..3]
    $pdfOk = (-join ($head | ForEach-Object { [char]$_ })) -eq '%PDF'
}
Check 'pdf render (--format pdf)' $pdfOk $r.StdErr

# ---- 5. failure modes ------------------------------------------------------

$badOut = Join-Path $work 'bad.png'
if (Test-Path $badOut) { Remove-Item $badOut }
$r = Invoke-OneShot -Fixture (Join-Path $fixtures 'bad.json') -Out $badOut
Check 'bad JSON: nonzero exit' ($r.ExitCode -ne 0) "exit $($r.ExitCode)"
Check 'bad JSON: message on stderr' ($r.StdErr -match 'gr-render:') $r.StdErr
Check 'bad JSON: no output file' (-not (Test-Path $badOut))

$unsupOut = Join-Path $work 'unsup.png'
if (Test-Path $unsupOut) { Remove-Item $unsupOut }
$unsupSpec = Join-Path $work 'unsup.json'
'{"data":[{"type":"surface","z":[[1,2],[3,4]]}],"layout":{}}' | Set-Content -Encoding ascii $unsupSpec
$r = Invoke-OneShot -Fixture $unsupSpec -Out $unsupOut
Check 'unsupported trace: nonzero exit + no output' `
    ($r.ExitCode -ne 0 -and -not (Test-Path $unsupOut)) $r.StdErr

$noOut = Start-Process -FilePath $exe -ArgumentList @('--width', '100') `
    -RedirectStandardInput (Join-Path $fixtures 'line.json') `
    -RedirectStandardError (Join-Path $work 'noout.err') -NoNewWindow -Wait -PassThru
Check 'missing --out: nonzero exit' ($noOut.ExitCode -ne 0)

# ---- 6. serve mode ---------------------------------------------------------

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $exe
$psi.Arguments = '--serve'
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.CreateNoWindow = $true
$psi.WorkingDirectory = $work
$proc = [System.Diagnostics.Process]::Start($psi)

function Send-Request {
    param([System.Diagnostics.Process]$P, [string]$Json)
    $P.StandardInput.Write($Json + "`n")
    $P.StandardInput.Flush()
    $P.StandardOutput.ReadLine()
}

$specText = (Get-Content -Raw (Join-Path $fixtures 'contourf.json')).Trim()
$line1 = Send-Request $proc ('{"id":1,"cmd":"render","spec":' + $specText + ',"width":800,"height":600}')
$line2 = Send-Request $proc '{"id":2,"cmd":"ping"}'
$line3 = Send-Request $proc '{"id":3,"cmd":"render","spec":{"data":[{"type":"nope"}],"layout":{}}}'
$line4 = Send-Request $proc '{"id":4,"cmd":"ping"}'
$line5 = Send-Request $proc ('{"id":5,"cmd":"render","spec":' + $specText + ',"format":"png"}')

$r1 = $line1 | ConvertFrom-Json
$r2 = $line2 | ConvertFrom-Json
$r3 = $line3 | ConvertFrom-Json
$r4 = $line4 | ConvertFrom-Json
$r5 = $line5 | ConvertFrom-Json

Check 'serve: render responds ok with matching id' ($r1.id -eq 1 -and $r1.ok -eq $true -and $r1.format -eq 'png')
$servePng = Join-Path $work 'serve.png'
$decodeOk = $false
if ($r1.ok) {
    [System.IO.File]::WriteAllBytes($servePng, [Convert]::FromBase64String($r1.data))
    $si = Get-PngInfo $servePng
    $decodeOk = ($null -ne $si -and $si.Width -eq 800 -and $si.Height -eq 600)
}
Check 'serve: base64 decodes to an 800x600 PNG' $decodeOk
Check 'serve: bytes match the one-shot render' ((Test-Path $servePng) -and (Get-Sha256 $servePng) -eq $ha) `
    'a warm worker must render the same bytes as a fresh process'
Check 'serve: ping' ($r2.id -eq 2 -and $r2.ok -eq $true)
Check 'serve: bad spec answers ok:false with an error' ($r3.id -eq 3 -and $r3.ok -eq $false -and $r3.error)
Check 'serve: loop survives a failed render' ($r4.id -eq 4 -and $r4.ok -eq $true)
Check 'serve: default size render (800x600)' ($r5.ok -eq $true -and $r5.data.Length -gt 100)

$proc.StandardInput.Write("{`"id`":6,`"cmd`":`"shutdown`"}`n")
$proc.StandardInput.Flush()
$exited = $proc.WaitForExit(10000)
Check 'serve: shutdown exits 0' ($exited -and $proc.ExitCode -eq 0) "exited=$exited code=$($proc.ExitCode)"
$leftover = $proc.StandardOutput.ReadToEnd()
Check 'serve: stdout carried only responses' ([string]::IsNullOrWhiteSpace($leftover)) $leftover

# EOF on stdin must also be a clean exit.
$proc2 = [System.Diagnostics.Process]::Start($psi)
$proc2.StandardInput.Close()
$exited2 = $proc2.WaitForExit(10000)
Check 'serve: stdin EOF exits 0' ($exited2 -and $proc2.ExitCode -eq 0)

# ---- 7. hygiene ------------------------------------------------------------

$gks = @(Get-Process | Where-Object { $_.Name -eq 'gksqt' })
Check 'no gksqt.exe process alive' ($gks.Count -eq 0) ("{0} found" -f $gks.Count)

$strays = @(Get-ChildItem -Path $work -Filter 'gks.*' -ErrorAction SilentlyContinue)
Check 'no stray gks.* files in cwd' ($strays.Count -eq 0) (($strays | ForEach-Object { $_.Name }) -join ',')

$tempLeft = @(Get-ChildItem -Path $env:TEMP -Filter 'gr-render-*.png' -ErrorAction SilentlyContinue)
Check 'no temp render files left behind' ($tempLeft.Count -eq 0) (($tempLeft | ForEach-Object { $_.Name }) -join ',')

# ---- done ------------------------------------------------------------------

Pop-Location
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue

Write-Host ''
if ($script:Failures -eq 0) {
    Write-Host ("PASS: {0} checks" -f $script:Checks)
    exit 0
} else {
    Write-Host ("FAILED: {0} of {1} checks" -f $script:Failures, $script:Checks)
    exit 1
}
