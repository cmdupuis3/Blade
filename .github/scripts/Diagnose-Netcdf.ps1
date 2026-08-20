<#
.SYNOPSIS
    Does opening an HDF5 file poison process exit on this machine?

.DESCRIPTION
    Isolated on purpose: no Blade, no generated code, no test harness -- just
    libnetcdf and twenty lines of C.

    The question comes from a specific failure. Five provider tests on the CI
    runner print their CORRECT answers and then hang until the harness kills
    them at 120s. Every one of them has opened tests/fixtures/sample.nc, which
    is HDF5-format. `blade doctor`'s netcdf probe, which only asks the library
    its version and never opens a file, exits cleanly on the same runner.

    So the hypothesis is that returning from main stops working once the
    netcdf/HDF5 closure has actually been used, and that is answerable without
    any of Blade in the picture.

    Three variants, and the pattern names the fix:

      version   never opens a file, returns 0     -- the control
      open      opens, closes, returns 0          -- runs the exit handlers
      fastexit  opens, closes, calls _exit(0)     -- skips them

    control exits and `open` hangs  => exit-time teardown of the netcdf/HDF5
    closure is the culprit, and `fastexit` surviving proves the handlers are
    where it hangs rather than merely suggesting it.
    all three exit                  => the library is fine here and the fault
    is in what Blade emits or links around it.

    Never fails the job: this reports, it does not judge.
#>
[CmdletBinding()]
param(
    # Seconds to let each variant run before calling it hung.
    [int] $TimeoutSeconds = 30,

    # The HDF5-format fixture to open, relative to the working directory.
    [string] $Fixture = 'tests/fixtures/sample.nc'
)

$ErrorActionPreference = 'Continue'

$lines = @(
    '#include <netcdf.h>'
    '#include <stdio.h>'
    '#include <stdlib.h>'
    '#include <string.h>'
    'int main(int argc, char **argv) {'
    '    const char *mode = argc > 1 ? argv[1] : "version";'
    '    if (!strcmp(mode, "version")) {'
    '        printf("libnetcdf %s\n", nc_inq_libvers());'
    '        fflush(stdout);'
    '        return 0;'
    '    }'
    '    int ncid = 0;'
    ('    int st = nc_open("' + $Fixture + '", NC_NOWRITE, &ncid);')
    '    if (st) { printf("nc_open failed: %s\n", nc_strerror(st)); fflush(stdout); return 2; }'
    '    st = nc_close(ncid);'
    '    printf("opened and closed (nc_close -> %d); returning from main\n", st);'
    '    fflush(stdout);'
    '    if (!strcmp(mode, "fastexit")) _exit(0);'
    '    return 0;'
    '}'
)

$root = (Get-Location).Path
$tmp = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [System.IO.Path]::GetTempPath() }
$src = Join-Path $tmp 'nc_probe.c'
$exe = Join-Path $tmp 'nc_probe.exe'
Set-Content -Path $src -Value $lines -Encoding ascii

if (-not (Test-Path (Join-Path $root $Fixture))) {
    Write-Host "fixture missing at $Fixture (cwd $root) -- nothing to conclude"
    exit 0
}

Write-Host "building the probe with g++ -lnetcdf ..."
& g++ -O0 -o $exe $src -lnetcdf
if ($LASTEXITCODE -ne 0) {
    Write-Host "probe did not build; nothing to conclude"
    exit 0
}

$verdicts = @{}
foreach ($mode in 'version', 'open', 'fastexit') {
    $out = Join-Path $tmp "nc_probe_$mode.out"
    $proc = Start-Process -FilePath $exe -ArgumentList $mode -PassThru -NoNewWindow `
                          -WorkingDirectory $root -RedirectStandardOutput $out
    $said =
        if ($proc.WaitForExit($TimeoutSeconds * 1000)) {
            $verdicts[$mode] = "exit $($proc.ExitCode)"
            "exit $($proc.ExitCode)"
        } else {
            try { $proc.Kill() } catch {}
            $verdicts[$mode] = 'HUNG'
            "HUNG (killed at ${TimeoutSeconds}s)"
        }
    $printed = if (Test-Path $out) { ((Get-Content $out -Raw) -replace '\s+$', '') } else { '' }
    if (-not $printed) { $printed = '(printed nothing)' }
    Write-Host ("  {0,-9} {1,-24} :: {2}" -f $mode, $said, $printed)
}

Write-Host ''
if ($verdicts['version'] -ne 'HUNG' -and $verdicts['open'] -eq 'HUNG') {
    if ($verdicts['fastexit'] -ne 'HUNG') {
        Write-Host 'VERDICT: opening an HDF5 file poisons process exit here, and _exit(0) steps around it.'
        Write-Host '         The hang is in the exit handlers of the netcdf/HDF5 closure, not in Blade.'
    } else {
        Write-Host 'VERDICT: opening an HDF5 file poisons process exit here, and _exit(0) does NOT help,'
        Write-Host '         so the hang is not (only) in atexit -- suspect the DLL unload path.'
    }
} elseif ($verdicts['open'] -ne 'HUNG') {
    Write-Host 'VERDICT: libnetcdf opens, closes and exits cleanly here. The fault is in what Blade'
    Write-Host '         emits or links around it, not in the library.'
} else {
    Write-Host 'VERDICT: inconclusive -- even the control variant did not finish.'
}
exit 0
