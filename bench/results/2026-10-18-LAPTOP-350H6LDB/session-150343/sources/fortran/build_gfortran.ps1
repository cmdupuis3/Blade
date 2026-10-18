# build_gfortran.ps1 <out.exe> <flags...> -- <sources...>
#
# Runs   gfortran <flags> <sources> -o <out.exe>   with C:\msys64\ucrt64\bin FIRST on PATH
# (Git-for-Windows' mingw64\bin shadows it otherwise and f951 dies silently), and exits with
# gfortran's exit code. Added to every build: -I<this directory> and a 256 MB stack reserve
# (see below); the caller passes the regime's flags verbatim
# (e.g. -O3 -march=native -ffp-contract=fast [-fopenmp]). The exact command line is echoed to
# stderr as "+ ...".
#
# Run-time note: the resulting exe imports libgfortran-5.dll / libquadmath-0.dll /
# libgcc_s_seh-1.dll / libwinpthread-1.dll (+ libgomp-1.dll with -fopenmp) from ucrt64, so it
# must be RUN with C:\msys64\ucrt64\bin first on PATH too (anaconda and Git ship older copies
# of some of these). Pass -static in the flags for a PATH-independent exe if preferred.
#
# Invoke as:  powershell -NoProfile -ExecutionPolicy Bypass -File build_gfortran.ps1 out.exe -O3 -- a.f90 b.f90
# (Arguments are read from the raw process command line, see below; "--" verified from
#  Python subprocess. "---" is accepted as a synonym in case a host ever eats "--"; a missing
#  separator is diagnosed.)

$ErrorActionPreference = 'Stop'
# The arguments are taken from the RAW process command line, not from $args: PowerShell's
# -File binder splits an argument such as "-LC:\dir" at the colon ("-LC" + "\dir"), which
# broke -L<path> link flags. Everything after this script's path is ours.
$raw = [Environment]::GetCommandLineArgs()
$me = [System.IO.Path]::GetFullPath($PSCommandPath)
$ix = -1
for ($i = 0; $i -lt $raw.Count; $i++) {
    try { $full = [System.IO.Path]::GetFullPath($raw[$i]) } catch { $full = '' }
    if ($full -ieq $me) { $ix = $i; break }
}
if ($ix -ge 0) { $argv = @($raw[($ix + 1)..($raw.Count - 1)]) } else { $argv = @($args) }
if ($ix -ge 0 -and $ix -eq $raw.Count - 1) { $argv = @() }
if ($argv.Count -lt 1) {
    [Console]::Error.WriteLine('usage: build_gfortran.ps1 <out.exe> <flags...> -- <sources...>')
    exit 2
}
$out = $argv[0]
$flags = New-Object System.Collections.Generic.List[string]
$srcs = New-Object System.Collections.Generic.List[string]
$mode = 'flags'
for ($i = 1; $i -lt $argv.Count; $i++) {
    $a = [string]$argv[$i]
    if ($mode -eq 'flags' -and ($a -eq '--' -or $a -eq '---')) { $mode = 'srcs'; continue }
    if ($mode -eq 'flags') { $flags.Add($a) } else { $srcs.Add($a) }
}
if ($srcs.Count -eq 0) {
    [Console]::Error.WriteLine('build_gfortran: no sources after the -- separator')
    exit 2
}
$ucrt = 'C:\msys64\ucrt64\bin'
$env:PATH = "$ucrt;$env:PATH"
$gf = Join-Path $ucrt 'gfortran.exe'
if (-not (Test-Path $gf)) {
    [Console]::Error.WriteLine("build_gfortran: $gf not found")
    exit 3
}
# Fixed additions (identical for every arm and regime; see bench/notes/fortran-notes.md):
#  * -I<this directory>: a kernel's "include 'bench_support.f90'" resolves; pass ONLY the
#    kernel source.
#  * -Wl,--stack,268435456: 256 MB main-thread stack RESERVE (virtual; pages commit on touch).
#    With -fopenmp gfortran (-frecursive) puts array-expression temporaries on the stack, and
#    an 8 MB temporary overflows the 2 MB default (measured: 0xC00000FD). This keeps each
#    compiler's default temporary placement while making the natural tier runnable.
#  * -cpp: every kernel source is preprocessed, so the harness can build the per-tier twins
#    from the same file (-DBENCH_INPUTONLY: kernel and checksum skipped; -DBENCH_DUMP: the
#    named output written to <kernel>_dump.txt after the stop line). Preprocessing does not
#    change the generated code of a normal build.
#  * -fno-external-blas -fno-external-blas64: MATMUL never routes to an external 32- or 64-bit
#    BLAS (gfortran defaults; explicit per reviews M22, F11). MATMUL stays libgfortran's _gfortran_matmul_r8 above the
#    default -finline-matmul-limit (30).
$all = @($flags) + @('-cpp', '-fno-external-blas', '-fno-external-blas64', "-I$PSScriptRoot", '-Wl,--stack,268435456') + @($srcs) + @('-o', $out)
[Console]::Error.WriteLine('+ ' + $gf + ' ' + ($all -join ' '))
& $gf @all
exit $LASTEXITCODE
