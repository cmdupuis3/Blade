<#
.SYNOPSIS
    Find an exit that works, on a machine where opening an HDF5 file breaks the
    normal one.

.DESCRIPTION
    Isolated on purpose: no Blade, no generated code, no test harness -- just
    libnetcdf and a few lines of C.

    ROUND 1 established the fault. Five provider tests print their CORRECT
    answers and then hang until the harness kills them at 120s; `out` and `m2`
    are the last bindings in those programs, so the work had finished and what
    stopped working was returning from main. This probe reproduced it with no
    Blade in the picture:

        version   exit 0                 libnetcdf 4.9.3
        open      HUNG (killed at 30s)   opened and closed; returning from main
        fastexit  HUNG (killed at 30s)   opened and closed; returning from main

    `fastexit` calls _exit(0), which skips atexit handlers and still hangs. So
    it is not atexit. _exit still reaches ExitProcess, which terminates every
    other thread and then runs DLL_PROCESS_DETACH across the loaded closure --
    and a thread killed while holding a lock that a detach handler then wants
    is the classic Windows exit deadlock. Thread-safe HDF5 and curl (netCDF's
    DAP support) are the usual sources of such threads.

    ROUND 2, this script, looks for an exit that survives that, so the fix can
    be chosen from evidence rather than from the shape of the story:

      version    never opens a file, returns 0          -- the control
      open       opens, closes, returns 0               -- the known-bad path
      fastexit   opens, closes, _exit(0)                -- skips atexit only
      terminate  opens, closes, TerminateProcess(self)  -- skips DLL detach too
      finalize   opens, closes, nc_finalize(), returns  -- orderly teardown
                                                           FIRST, if the build
                                                           has that entry point

    `terminate` surviving means a generated program can always leave, at the
    cost of running no teardown at all. `finalize` surviving is the better
    outcome by far: it would mean the closure just needs to be shut down in
    order before the loader gets involved, which is a fix with no collateral.

    Also prints libnetcdf's direct DLL dependencies, because the presence of a
    thread-spawning library in that list is most of the explanation.

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

$root = (Get-Location).Path
$tmp = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [System.IO.Path]::GetTempPath() }

if (-not (Test-Path (Join-Path $root $Fixture))) {
    Write-Host "fixture missing at $Fixture (cwd $root) -- nothing to conclude"
    exit 0
}

# --------------------------------------------------------------------------
# What libnetcdf drags in. A thread-spawning dependency here is most of the
# explanation for an ExitProcess deadlock.
# --------------------------------------------------------------------------
if ($env:NETCDF_DIR) {
    $dll = Get-ChildItem -Path (Join-Path $env:NETCDF_DIR 'bin') -Filter '*netcdf*.dll' -ErrorAction SilentlyContinue |
           Select-Object -First 1
    if ($dll) {
        Write-Host "direct DLL dependencies of $($dll.Name):"
        $deps = & objdump -p $dll.FullName 2>$null | Select-String 'DLL Name:' | ForEach-Object { ($_ -split 'DLL Name:')[1].Trim() }
        if ($deps) { Write-Host ('  ' + ($deps -join ', ')) } else { Write-Host '  (objdump unavailable)' }
        Write-Host ''
    }
}

# --------------------------------------------------------------------------
# The probe. One source, one variant per argv[1]; `finalize` is compiled from
# its own source so that a build without that entry point disables only that
# variant instead of the whole probe.
# --------------------------------------------------------------------------
$common = @(
    '#include <netcdf.h>'
    '#include <stdio.h>'
    '#include <stdlib.h>'
    '#include <string.h>'
    '#include <windows.h>'
    'static int open_and_close(void) {'
    '    int ncid = 0;'
    ('    int st = nc_open("' + $Fixture + '", NC_NOWRITE, &ncid);')
    '    if (st) { printf("nc_open failed: %s\n", nc_strerror(st)); fflush(stdout); return -1; }'
    '    st = nc_close(ncid);'
    '    printf("opened and closed (nc_close -> %d); leaving main\n", st);'
    '    fflush(stdout);'
    '    return 0;'
    '}'
)

$mainSrc = $common + @(
    'int main(int argc, char **argv) {'
    '    const char *mode = argc > 1 ? argv[1] : "version";'
    '    if (!strcmp(mode, "version")) {'
    '        printf("libnetcdf %s\n", nc_inq_libvers());'
    '        fflush(stdout);'
    '        return 0;'
    '    }'
    '    if (open_and_close() != 0) return 2;'
    '    if (!strcmp(mode, "fastexit")) _exit(0);'
    '    if (!strcmp(mode, "terminate")) {'
    '        fflush(NULL);'
    '        TerminateProcess(GetCurrentProcess(), 0);'
    '    }'
    '    return 0;'
    '}'
)

$finalizeSrc = $common + @(
    'int main(void) {'
    '    if (open_and_close() != 0) return 2;'
    '    nc_finalize();'
    '    printf("nc_finalize returned\n");'
    '    fflush(stdout);'
    '    return 0;'
    '}'
)

function Build-Probe([string[]] $lines, [string] $name) {
    $src = Join-Path $tmp "$name.c"
    $exe = Join-Path $tmp "$name.exe"
    Set-Content -Path $src -Value $lines -Encoding ascii
    & g++ -O0 -o $exe $src -lnetcdf 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { return $null }
    $exe
}

function Invoke-Variant([string] $exe, [string] $mode, [string] $label) {
    $out = Join-Path $tmp "nc_probe_$label.out"
    $argList = if ($mode) { @($mode) } else { @() }
    $proc = Start-Process -FilePath $exe -ArgumentList $argList -PassThru -NoNewWindow `
                          -WorkingDirectory $root -RedirectStandardOutput $out
    $verdict =
        if ($proc.WaitForExit($TimeoutSeconds * 1000)) { "exit $($proc.ExitCode)" }
        else { try { $proc.Kill() } catch {}; 'HUNG' }
    $printed = if (Test-Path $out) { ((Get-Content $out -Raw) -replace '\s+$', '') } else { '' }
    if (-not $printed) { $printed = '(printed nothing)' }
    $shown = if ($verdict -eq 'HUNG') { "HUNG (killed at ${TimeoutSeconds}s)" } else { $verdict }
    Write-Host ("  {0,-10} {1,-24} :: {2}" -f $label, $shown, $printed)
    $verdict
}

$verdicts = @{}

$main = Build-Probe $mainSrc 'nc_probe'
if (-not $main) {
    Write-Host 'probe did not build; nothing to conclude'
    exit 0
}
foreach ($mode in 'version', 'open', 'fastexit', 'terminate') {
    $verdicts[$mode] = Invoke-Variant $main $mode $mode
}

$fin = Build-Probe $finalizeSrc 'nc_finalize_probe'
if ($fin) {
    $verdicts['finalize'] = Invoke-Variant $fin '' 'finalize'
} else {
    Write-Host ("  {0,-10} {1}" -f 'finalize', 'unavailable (this build has no nc_finalize)')
}

# --------------------------------------------------------------------------
Write-Host ''
if ($verdicts['open'] -ne 'HUNG') {
    Write-Host 'VERDICT: libnetcdf opens, closes and exits cleanly here, so the fault is in what'
    Write-Host '         Blade emits or links around it rather than in the library.'
} elseif ($verdicts['finalize'] -eq 'exit 0') {
    Write-Host 'VERDICT: nc_finalize() before returning fixes it. That is the fix to emit -- an'
    Write-Host '         orderly shutdown before the loader gets involved, with no collateral.'
} elseif ($verdicts['terminate'] -eq 'exit 0') {
    Write-Host 'VERDICT: only TerminateProcess survives, so the deadlock is in DLL_PROCESS_DETACH'
    Write-Host '         itself and no amount of orderly cleanup reaches it. A provider program'
    Write-Host '         must flush and leave without running teardown.'
} else {
    Write-Host 'VERDICT: nothing tried here exits. Escalate to the dependency list above --'
    Write-Host '         the hang is in loading/unloading the closure, not in how we leave.'
}
exit 0
