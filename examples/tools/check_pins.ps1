# check_pins.ps1 -- verify an example program's `// EXPECT:` pins in place.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File examples/tools/check_pins.ps1 <file.blade> [-Blade <exe>] [-Quiet]
#
# The examples are not part of `blade test` (several read data by a path relative
# to their own directory, which the harness's cwd-relative runner cannot honour),
# but they keep the corpus pin conventions. This script runs `blade run <file>`
# FROM THE FILE'S DIRECTORY and checks every `// EXPECT: name = value` line against
# the printed `name = value` lines with the harness's own rule (tests/Expect.fs):
# numbers at 1e-9 relative tolerance (absolute below 1e-12), arrays flattened and
# compared element-wise, bools true/false ~ 1/0, quoted strings against the
# unquoted printed text, everything else exact. A name printed twice with
# different values fails its pin, as in the harness.
#
# Output: one PASS/FAIL/MISSING line per pin (FAIL/MISSING only under -Quiet),
# then `PINS: p passed, f failed, m missing (n pins); exit=<code>; <secs>; <file>`.
# A non-zero exit with no pins passed means the program did not compile or
# panicked; the BL lines are echoed. -Blade overrides the compiler (default: the
# Release build beside this checkout). MSYS2 ucrt64 g++ is put on PATH.
param(
  [Parameter(Mandatory=$true)][string]$File,
  [string]$Blade = "",
  [switch]$Quiet
)
$ErrorActionPreference = "Continue"
if (-not $Blade) {
  # the Release build of the checkout this script lives in ($PSScriptRoot is not
  # yet bound while parameter defaults are evaluated under PowerShell 5.1)
  $here = Split-Path -Parent $MyInvocation.MyCommand.Path
  $Blade = Join-Path $here "..\..\bin\Release\net10.0\Blade.exe"
}
$env:PATH = "C:\msys64\ucrt64\bin;" + $env:PATH
$inv = [System.Globalization.CultureInfo]::InvariantCulture
$tol = 1e-9

function ParseF([string]$s) {
  $t = $s.Trim()
  $v = 0.0
  if ([double]::TryParse($t, [System.Globalization.NumberStyles]::Float, $inv, [ref]$v)) { return $v }
  switch -Regex ($t) { '^(nan|NaN)$' { return [double]::NaN }; '^(inf|Infinity|\+inf)$' { return [double]::PositiveInfinity }; '^(-inf|-Infinity)$' { return [double]::NegativeInfinity } }
  return $null
}
function FEq([double]$e, [double]$a) {
  if ([double]::IsNaN($e) -and [double]::IsNaN($a)) { return $true }
  if ([double]::IsInfinity($e) -or [double]::IsInfinity($a)) { return $e -eq $a }
  $d = [math]::Abs($e - $a); $scale = [math]::Max([math]::Abs($e), [math]::Abs($a))
  if ($scale -lt 1e-12) { return $d -le $tol } else { return ($d / $scale) -le $tol }
}
function Flatten([string]$s) {
  $inner = ($s -replace '[\[\]\(\)]', ' ')
  return ,@($inner.Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' })
}
function Same([string]$exp, [string]$act) {
  $e = $exp.Trim(); $a = $act.Trim()
  if ($e -ceq $a) { return $true }
  if ($e.Length -ge 2 -and $e.StartsWith('"') -and $e.EndsWith('"')) { return ($e.Substring(1, $e.Length - 2) -ceq $a) }
  $ef = ParseF $e; $af = ParseF $a
  if ($null -ne $ef -and $null -ne $af) { return FEq $ef $af }
  if ($null -ne $ef -and ($a -eq 'true' -or $a -eq 'false')) { return FEq $ef $(if ($a -eq 'true') {1.0} else {0.0}) }
  if ($null -ne $af -and ($e -eq 'true' -or $e -eq 'false')) { return FEq $(if ($e -eq 'true') {1.0} else {0.0}) $af }
  if (($e.StartsWith('[') -or $e.StartsWith('(')) -and ($a.StartsWith('[') -or $a.StartsWith('('))) {
    $ee = Flatten $e; $aa = Flatten $a
    if ($ee.Count -ne $aa.Count) { return $false }
    for ($i = 0; $i -lt $ee.Count; $i++) { if (-not (Same $ee[$i] $aa[$i])) { return $false } }
    return $true
  }
  return $false
}

$full = (Resolve-Path $File).Path
$src = Get-Content -Raw -Encoding UTF8 $full
$pins = @()
foreach ($line in ($src -split "`r?`n")) {
  if ($line -match '^\s*//\s*EXPECT:\s*([^\s=]+)\s*=\s*(.*)$') { $pins += ,@($Matches[1], $Matches[2].Trim()) }
}
$sw = [System.Diagnostics.Stopwatch]::StartNew()
Push-Location (Split-Path -Parent $full)
try { $out = & $Blade run $full 2>&1 | Out-String; $code = $LASTEXITCODE } finally { Pop-Location }
$sw.Stop()
# case-sensitive tables: a PowerShell @{} is not, and c0 / C0 are different bindings
$actual = New-Object System.Collections.Hashtable
$collapsed = New-Object System.Collections.Hashtable
foreach ($line in ($out -split "`r?`n")) {
  $t = $line.Trim()
  if ($t.Contains(' = ') -and -not $t.Contains('completed in')) {
    $parts = $t -split ' = ', 2
    $n = $parts[0].Trim(); $v = $parts[1].Trim()
    if ($actual.ContainsKey($n) -and $actual[$n] -ne $v) { $collapsed[$n] = $true }
    $actual[$n] = $v
  }
}
$pass = 0; $fail = 0; $missing = 0
foreach ($p in $pins) {
  $n = $p[0]; $e = $p[1]
  if (-not $actual.ContainsKey($n)) { $missing++; Write-Output "MISSING $n (expected $e)"; continue }
  if ($collapsed.ContainsKey($n)) { $fail++; Write-Output "FAIL $n printed twice with different values"; continue }
  if (Same $e $actual[$n]) { $pass++; if (-not $Quiet) { Write-Output "PASS $n" } }
  else { $fail++; Write-Output "FAIL $n`n   expected: $e`n   actual:   $($actual[$n])" }
}
if ($code -ne 0) {
  Write-Output "RUN EXIT CODE $code"
  Write-Output ($out -split "`r?`n" | Where-Object { $_ -match 'BL\d{4}|error|Error|panic|abort' } | Select-Object -First 12 | Out-String)
}
Write-Output ("PINS: {0} passed, {1} failed, {2} missing ({3} pins); exit={4}; {5:N1}s; {6}" -f $pass, $fail, $missing, $pins.Count, $code, $sw.Elapsed.TotalSeconds, (Split-Path -Leaf $full))
