# Validates examples/physics/*.blade against their // EXPECT: pins.
#   powershell -File validate_examples.ps1            # all examples
#   powershell -File validate_examples.ps1 -Filter 16 # just 16_*
# Pins: numbers (1e-9 relative tol, 1e-12 absolute floor), quoted strings
# (exact), arrays (elementwise numeric). Run on the RELEASE build; Debug
# overflows its stack on the deep dist_map chains.
param(
    [string]$Filter = "*",
    [string]$BladeExe = ""
)
$ErrorActionPreference = "Stop"
$physDir = Split-Path -Parent $PSScriptRoot
if ($BladeExe -eq "") {
    $repoRoot = Split-Path -Parent (Split-Path -Parent $physDir)
    $BladeExe = Join-Path $repoRoot "bin\Release\net7.0\Blade.exe"
}
$inv = [System.Globalization.CultureInfo]::InvariantCulture

function Parse-Num([string]$txt) {
    return [double]::Parse($txt.Trim(), [System.Globalization.NumberStyles]::Float, $script:inv)
}
function Num-Match([double]$got, [double]$want) {
    $tol = [Math]::Max(1e-12, 1e-9 * [Math]::Abs($want))
    return ([Math]::Abs($got - $want) -le $tol)
}

$grandPass = 0; $grandFail = 0; $badFiles = 0
$bladeFiles = @(Get-ChildItem (Join-Path $physDir "*.blade") | Where-Object { $_.Name -like "$Filter*" } | Sort-Object Name)
foreach ($bfile in $bladeFiles) {
    $pins = @()
    foreach ($lineTxt in (Get-Content $bfile.FullName)) {
        if ($lineTxt -match '^\s*//\s*EXPECT:\s*(\S+)\s*=\s*(.+?)\s*$') {
            $pins += , @($Matches[1], $Matches[2])
        }
    }
    if ($pins.Count -eq 0) { Write-Host ("{0}: no pins, skipped" -f $bfile.Name); continue }

    $outTxt = & $BladeExe run $bfile.FullName
    if ($LASTEXITCODE -ne 0) {
        Write-Host ("{0}: RUN FAILED (exit {1})" -f $bfile.Name, $LASTEXITCODE)
        $grandFail += $pins.Count; $badFiles++
        continue
    }
    $outMap = @{}
    foreach ($lineTxt in $outTxt) {
        if ($lineTxt -match '^(\S+) = (.*)$') { $outMap[$Matches[1]] = $Matches[2] }
    }

    $nPass = 0; $nFail = 0
    foreach ($pin in $pins) {
        $pname = $pin[0]; $pval = $pin[1]
        if (-not $outMap.ContainsKey($pname)) {
            Write-Host ("  MISSING {0}" -f $pname); $nFail++; continue
        }
        $got = $outMap[$pname]
        $ok = $false
        if ($pval.StartsWith('"')) {
            $ok = ($got.Trim('"') -ceq $pval.Trim('"'))
        }
        elseif ($pval.StartsWith('[')) {
            $wantArr = $pval.Trim('[', ']') -split ','
            $gotArr = $got.Trim('[', ']') -split ','
            if ($wantArr.Count -eq $gotArr.Count) {
                $ok = $true
                for ($ii = 0; $ii -lt $wantArr.Count; $ii++) {
                    if (-not (Num-Match (Parse-Num $gotArr[$ii]) (Parse-Num $wantArr[$ii]))) { $ok = $false; break }
                }
            }
        }
        else {
            $ok = Num-Match (Parse-Num $got) (Parse-Num $pval)
        }
        if ($ok) { $nPass++ }
        else { Write-Host ("  FAIL {0}: want {1} got {2}" -f $pname, $pval, $got); $nFail++ }
    }
    $grandPass += $nPass; $grandFail += $nFail
    if ($nFail -eq 0) { Write-Host ("{0}: PASS {1}/{1}" -f $bfile.Name, $nPass) }
    else { Write-Host ("{0}: FAIL ({1} pass, {2} fail)" -f $bfile.Name, $nPass, $nFail); $badFiles++ }
}
Write-Host ""
Write-Host ("TOTAL: {0} pass, {1} fail across {2} files" -f $grandPass, $grandFail, $bladeFiles.Count)
if ($grandFail -gt 0) { exit 1 } else { exit 0 }
