# Base-vs-head EMISSION sweep: two compiler builds emit the same corpus files
# and the emitted bytes are compared, per file, per lane, per build.
#
# Written for gap G74 (round 3 wave 4b): a change that must leave the LICENSED
# emission untouched shows it here on named corpus directories instead of
# claiming it from unchanged emitter source. Reusable for any such change.
#
#   powershell -NoProfile -File bench/harness/emission-sweep.ps1 `
#       -Base <base Blade.exe> -Head <head Blade.exe> -Root <repo checkout> `
#       -BaseRev <git rev of the base> -Dirs sibling-folds,symmetry,loops `
#       [-Lanes cpp,llvm] [-Builds default,repro] [-Out <dir>]
#
# (Lists are comma-separated; `-File` hands `a,b` over as one string, split
# here.) Per (lane, build) it runs `blade emit <file>` with each binary and
# captures STDOUT and STDERR SEPARATELY, as raw text (no line normalization),
# plus the exit code. Each side is classified:
#   emitted  -- exit 0 and a non-empty stdout (the C++ text or the LLVM module);
#   refused  -- a non-zero exit with a non-empty stderr (a diagnostic);
#   broken   -- anything else: an empty output, a crash, an interrupted run.
# A file is
#   identical -- both sides emitted (or both refused) and stdout, stderr and
#                exit are equal byte for byte, at the FIRST attempt;
#   DIFF      -- they differ at the first attempt AND again at a second one
#                (both attempts are kept under <Out>\diffs\: *.a1 / *.a2);
#   UNSTABLE  -- the attempts disagree with each other, or a side is broken:
#                fail closed, never counted as identical (both attempts kept);
#   excluded  -- the file does not exist at -BaseRev (new at the head), unless
#                -IncludeNew (then the base compiler emits the head's file too).
# A -Dirs entry may name a multi-file test directory (multifile/<test>): its
# files are emitted one by one, imports resolved beside them.
# The ROUTE is recorded per side: `llvm` when the LLVM lane emitted the
# program, `fallback` when it refused (its `llvm lane refused` stderr line --
# the C++ text is then what is compared), `cpp` for the C++ lane; with the
# class (`emitted` / `refused` / `broken`). Knobs: BLADE_BLAS=off,
# BLADE_EXE_CACHE=0, OPENBLAS_DIR removed and every other emission knob the
# default suite clears removed; `repro` adds BLADE_FP_REPRO=1
# BLADE_FP_CONTRACT=off; `llvm` adds BLADE_LLVM=1. The manifest
# (<Out>\manifest-<lane>-<build>.txt) starts with the totals (identical
# emitted / identical refused / DIFF / UNSTABLE / excluded, and the head's
# llvm / fallback / cpp routes), both binaries' Blade.dll SHA-256, the base
# revision, the knobs and the directory list; one line per file follows. The
# script exits 1 when any file is UNSTABLE and 2 when an artifact under diffs\
# could not be written (a nested -Dirs entry such as multifile/<test> gets a
# FLAT artifact name, its separators as `~`). Nothing is written in the repo.
param(
    [Parameter(Mandatory = $true)][string]$Base,
    [Parameter(Mandatory = $true)][string]$Head,
    [Parameter(Mandatory = $true)][string]$Root,
    [Parameter(Mandatory = $true)][string]$BaseRev,
    [Parameter(Mandatory = $true)][string[]]$Dirs,
    [string[]]$Lanes = @('cpp', 'llvm'),
    [string[]]$Builds = @('default'),
    [string]$Out = (Join-Path ([IO.Path]::GetTempPath()) 'blade-emission-sweep'),
    # compare files absent at -BaseRev too (a new witness: the base compiler on the head's file)
    [switch]$IncludeNew
)
# native stderr is DATA here (compared), never a terminating error
$ErrorActionPreference = 'Continue'
$env:PATH = "C:\msys64\ucrt64\bin;" + $env:PATH
$split = { param($xs) @($xs | ForEach-Object { $_ -split ',' } | Where-Object { $_ -ne '' }) }
$Dirs = & $split $Dirs; $Lanes = & $split $Lanes; $Builds = & $split $Builds
New-Item -ItemType Directory -Force $Out | Out-Null
New-Item -ItemType Directory -Force (Join-Path $Out 'diffs') | Out-Null
$cleared = @('OPENBLAS_DIR', 'BLADE_FP_REPRO', 'BLADE_FP_REASSOC', 'BLADE_FP_CONTRACT', 'BLADE_MARCH', 'BLADE_OMP_THREADS',
    'BLADE_PRINT', 'BLADE_PRINT_BITS', 'BLADE_TILE_TUNE', 'BLADE_TILE_CACHE', 'BLADE_LLVM', 'BLADE_CUBLAS', 'BLADE_BLAS_PREFER',
    'BLADE_FUSION', 'BLADE_FREEZE_IDIOM', 'BLADE_CSE', 'BLADE_POOL_REUSE', 'BLADE_HOIST', 'BLADE_HOT_ROW', 'BLADE_CELL_EPILOGUE',
    'BLADE_COMPACTION_FUSION', 'BLADE_COMPACT_ROW_CLASSES', 'BLADE_SIBLING_FOLDS', 'BLADE_REC_STEP_OMP', 'BLADE_MEMBER_HOIST',
    'BLADE_HASH_JOIN', 'BLADE_JOIN_REPORT', 'BLADE_EPILOGUE_LEGS', 'BLADE_GROUP_AGG', 'BLADE_SYRK_TILE', 'BLADE_ANTISYM_TILE',
    'BLADE_SIMPLEX_TILE', 'BLADE_ORBIT_FOLD', 'BLADE_DOMAIN_DIAGONAL', 'BLADE_FOLD_LANES', 'BLADE_FOLD_ROW_SLOTS',
    'BLADE_ORBIT_SCATTER', 'BLADE_ORBIT_PULL', 'BLADE_ORBIT_ACTIONS', 'BLADE_JOIN_THREADS', 'BLADE_AD_HALO_GATHER',
    'BLADE_POOL_RETAIN', 'BLADE_MEMCHECK')
foreach ($k in $cleared) { Remove-Item "env:$k" -ErrorAction SilentlyContinue }
$env:BLADE_BLAS = 'off'
$env:BLADE_EXE_CACHE = '0'
$dllHash = { param($exe) (Get-FileHash -Algorithm SHA256 ([IO.Path]::ChangeExtension($exe, '.dll'))).Hash }
$baseHash = & $dllHash $Base
$headHash = & $dllHash $Head
# one `blade emit`: raw stdout, raw stderr, exit code (the process's environment is this one)
$emit = {
    param($exe, $file)
    $psi = New-Object System.Diagnostics.ProcessStartInfo $exe, ('emit "' + $file + '"')
    $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true; $psi.UseShellExecute = $false
    $psi.WorkingDirectory = (Split-Path $file)
    $p = [System.Diagnostics.Process]::Start($psi)
    $errTask = $p.StandardError.ReadToEndAsync()
    $o = $p.StandardOutput.ReadToEnd()
    $p.WaitForExit()
    $e = $errTask.Result
    [pscustomobject]@{ Out = $o; Err = $e; Exit = $p.ExitCode }
}
$classOf = {
    param($r)
    if ($r.Exit -eq 0 -and $r.Out.Length -gt 0) { 'emitted' }
    elseif ($r.Exit -ne 0 -and $r.Exit -gt 0 -and $r.Err.Length -gt 0) { 'refused' }
    else { 'broken' }
}
$routeOf = {
    param($r, $lane)
    if ($lane -eq 'llvm') { if ($r.Err.Contains('llvm lane refused')) { 'fallback' } else { 'llvm' } } else { 'cpp' }
}
$same = { param($x, $y) [string]::Equals($x.Out, $y.Out, [StringComparison]::Ordinal) -and [string]::Equals($x.Err, $y.Err, [StringComparison]::Ordinal) -and $x.Exit -eq $y.Exit }
# an artifact that cannot be written FAILS the sweep (exit 2): a DIFF or
# UNSTABLE verdict is only as good as the attempts it keeps
$script:artifactFailures = 0
$keep = {
    param($stem, $tag, $r)
    try {
        [IO.File]::WriteAllText((Join-Path $Out "diffs\$stem.$tag.out"), $r.Out)
        [IO.File]::WriteAllText((Join-Path $Out "diffs\$stem.$tag.err"), $r.Err + "`nexit=" + $r.Exit)
    } catch { $script:artifactFailures++; Write-Warning "artifact write failed for $stem.$tag : $_" }
}
$summary = New-Object System.Collections.Generic.List[string]
$anyUnstable = $false
foreach ($lane in $Lanes) {
    foreach ($build in $Builds) {
        if ($lane -eq 'llvm') { $env:BLADE_LLVM = '1' } else { Remove-Item env:BLADE_LLVM -ErrorAction SilentlyContinue }
        if ($build -eq 'repro') { $env:BLADE_FP_REPRO = '1'; $env:BLADE_FP_CONTRACT = 'off' }
        else { Remove-Item env:BLADE_FP_REPRO, env:BLADE_FP_CONTRACT -ErrorAction SilentlyContinue }
        $lines = New-Object System.Collections.Generic.List[string]
        $idEm = 0; $idRef = 0; $diff = 0; $unstable = 0; $excluded = 0
        $routes = @{ llvm = 0; fallback = 0; cpp = 0 }
        foreach ($d in $Dirs) {
            foreach ($f in (Get-ChildItem (Join-Path $Root "tests\corpus\$d") -Filter *.blade | Sort-Object Name)) {
                $rel = "tests/corpus/$d/$($f.Name)"
                & git -C $Root cat-file -e "${BaseRev}:$rel" 2>$null
                if ($LASTEXITCODE -ne 0 -and -not $IncludeNew) { $excluded++; $lines.Add("$d/$($f.Name) excluded (new at the head)"); continue }
                # a FLAT artifact name: a nested -Dirs entry (multifile/<test>) carries a
                # path separator, encoded as `~` (no corpus name uses it), so distinct
                # entries stay distinct names directly under diffs\
                $stem = "$lane-$build-" + ($d -replace '[\\/]', '~') + "-$($f.BaseName)"
                $a1 = & $emit $Base $f.FullName
                $b1 = & $emit $Head $f.FullName
                $ca = & $classOf $a1; $cb = & $classOf $b1
                $verdict = $null
                if ($ca -ne 'broken' -and $cb -ne 'broken' -and (& $same $a1 $b1)) {
                    $verdict = 'identical'
                    if ($ca -eq 'emitted') { $idEm++ } else { $idRef++ }
                } else {
                    # a second attempt on both sides; both attempts are kept
                    $a2 = & $emit $Base $f.FullName
                    $b2 = & $emit $Head $f.FullName
                    & $keep $stem 'base.a1' $a1; & $keep $stem 'head.a1' $b1
                    & $keep $stem 'base.a2' $a2; & $keep $stem 'head.a2' $b2
                    $stable = (& $same $a1 $a2) -and (& $same $b1 $b2)
                    $broken = @($ca, $cb, (& $classOf $a2), (& $classOf $b2)) -contains 'broken'
                    if ($stable -and -not $broken) {
                        $verdict = 'DIFF'; $diff++
                        $pa = Join-Path $Out "diffs\$stem.base.a1.out"; $pb = Join-Path $Out "diffs\$stem.head.a1.out"
                        try { & git -c core.autocrlf=false diff --no-index --no-color $pa $pb 2>$null | Out-File -Encoding utf8 -ErrorAction Stop (Join-Path $Out "diffs\$stem.diff") }
                        catch { $script:artifactFailures++; Write-Warning "artifact write failed for $stem.diff : $_" }
                    } else { $verdict = 'UNSTABLE'; $unstable++; $anyUnstable = $true }
                }
                $rb = & $routeOf $b1 $lane
                if ($cb -eq 'emitted') { $routes[$rb]++ }
                $lines.Add("$d/$($f.Name) $verdict base:exit=$($a1.Exit),$ca,route=$(& $routeOf $a1 $lane) head:exit=$($b1.Exit),$cb,route=$rb")
            }
        }
        $total = "$lane/$build : $($idEm + $idRef + $diff + $unstable) compared -- $idEm identical emitted, $idRef identical refusals, $diff DIFF, $unstable UNSTABLE, $excluded excluded; head emitted by route: llvm $($routes.llvm), C++ fallback $($routes.fallback), cpp $($routes.cpp)"
        $hdr = @(
            "# totals: $total",
            "# base Blade.dll SHA-256: $baseHash ($Base)",
            "# head Blade.dll SHA-256: $headHash ($Head)",
            "# base revision (exclusion test): $BaseRev",
            "# knobs: BLADE_BLAS=off BLADE_EXE_CACHE=0" + $(if ($lane -eq 'llvm') { ' BLADE_LLVM=1' } else { '' }) + $(if ($build -eq 'repro') { ' BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off' } else { '' }) + '; every other emission knob unset',
            "# dirs: $($Dirs -join ',')")
        [IO.File]::WriteAllLines((Join-Path $Out "manifest-$lane-$build.txt"), [string[]]($hdr + $lines))
        $summary.Add($total)
    }
}
Remove-Item env:BLADE_LLVM, env:BLADE_FP_REPRO, env:BLADE_FP_CONTRACT -ErrorAction SilentlyContinue
$summary | ForEach-Object { $_ }
"manifests and diffs: $Out"
if ($script:artifactFailures -gt 0) { "ARTIFACT WRITE FAILURES: $($script:artifactFailures) -- the sweep is not evidence"; exit 2 }
if ($anyUnstable) { exit 1 } else { exit 0 }
