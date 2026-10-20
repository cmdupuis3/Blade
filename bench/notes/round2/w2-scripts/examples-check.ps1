# blade check of every examples/ program and notebook (the wave-end gates do not cover notebooks)
$WT = 'C:\Users\cdupu\Documents\GitHub\Blade\.claude\worktrees\bench-fortran'
$S = 'C:\Users\cdupu\AppData\Local\Temp\claude\C--Users-cdupu--claude-spares-spare-dUbXep\dbd3f5e5-01ed-41dd-bc92-4bc0ec85152b\scratchpad\round2\gates\w2'
$env:PATH = 'C:\msys64\ucrt64\bin;' + $env:PATH
$B = "$WT\bin\Release\net10.0\Blade.exe"
$out = "$S\examples-check.txt"
"examples check on $(git -C $WT rev-parse --short HEAD) $(Get-Date -Format s)" | Set-Content $out
$files = Get-ChildItem "$WT\examples" -Recurse -Include *.blade,*.bladenb | Where-Object { $_.FullName -notlike '*\legacy\*' }
$bad = 0
foreach ($f in $files) {
  $r = & $B check $f.FullName 2>&1 | Out-String
  $code = $LASTEXITCODE
  $err = ($r -split "`n" | Where-Object { $_ -match 'BL\d{4}' } | Select-Object -First 1)
  if ($code -ne 0) { $bad++; "FAIL exit=$code $($f.FullName.Substring($WT.Length+1)) :: $($err.Trim())" | Add-Content $out }
}
"$($files.Count) files, $bad failed" | Add-Content $out

