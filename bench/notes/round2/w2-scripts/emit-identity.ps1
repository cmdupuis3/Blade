# Bench emission identity: does the final tip emit byte-identical C++ / LLVM for every bench kernel vs the wave-2 feature tip bd34627b
# (i.e. are W2-F's effect-analysis fixes inert on the bench)? Both binaries see the SAME environment (harness-like: BLADE_* cleared,
# OPENBLAS_DIR cleared, lane/regime knobs set per arm).
param([string]$A = 'C:\bt_r2emit\bin\Release\net10.0\Blade.exe', [string]$B = 'C:\Users\cdupu\Documents\GitHub\Blade\.claude\worktrees\bench-fortran\bin\Release\net10.0\Blade.exe')
$S = 'C:\Users\cdupu\AppData\Local\Temp\claude\C--Users-cdupu--claude-spares-spare-dUbXep\dbd3f5e5-01ed-41dd-bc92-4bc0ec85152b\scratchpad\round2\gates\w2\emit'
$KDIR = 'C:\Users\cdupu\Documents\GitHub\Blade\.claude\worktrees\bench-fortran\bench\kernels'
New-Item -ItemType Directory -Force $S | Out-Null
Get-ChildItem Env: | Where-Object { $_.Name -like 'BLADE_*' -or $_.Name -eq 'OPENBLAS_DIR' } | ForEach-Object { Remove-Item "Env:$($_.Name)" }
$env:PATH = 'C:\msys64\ucrt64\bin;' + $env:PATH
$out = "$S\..\emit-identity.txt"
"emit identity A=$A B=$B $(Get-Date -Format s)" | Set-Content $out
"A hash $((Get-FileHash ($A -replace 'Blade.exe$','Blade.dll')).Hash)" | Add-Content $out
"B hash $((Get-FileHash ($B -replace 'Blade.exe$','Blade.dll')).Hash)" | Add-Content $out
$kernels = Get-ChildItem $KDIR -Directory | ForEach-Object { $_.Name }
$arms = @(
  @('cpp-licensed', @{}),
  @('cpp-repro',    @{ BLADE_FP_REPRO = '1'; BLADE_FP_REASSOC = '0' }),
  @('llvm-licensed', @{ BLADE_LLVM = '1'; BLADE_FP_CONTRACT = 'fast' }),
  @('llvm-repro',   @{ BLADE_LLVM = '1'; BLADE_FP_CONTRACT = 'fast'; BLADE_FP_REPRO = '1'; BLADE_FP_REASSOC = '0' })
)
$same = 0; $diff = 0; $fail = 0
"kernels: $($kernels.Count) [$($kernels -join ',')]" | Add-Content $out
foreach ($k in $kernels) {
  $src = Join-Path $KDIR "$k\$k.blade"
  if (-not (Test-Path $src)) { "missing $src" | Add-Content $out; continue }
  foreach ($arm in $arms) {
    $name = $arm[0]; $knobs = $arm[1]
    foreach ($kv in $knobs.GetEnumerator()) { Set-Item "Env:$($kv.Key)" $kv.Value }
    $fa = "$S\$k-$name-A.txt"; $fb = "$S\$k-$name-B.txt"
    & $A emit $src -o $fa *> "$S\$k-$name-A.log"; $ca = $LASTEXITCODE
    & $B emit $src -o $fb *> "$S\$k-$name-B.log"; $cb = $LASTEXITCODE
    foreach ($kv in $knobs.GetEnumerator()) { Remove-Item "Env:$($kv.Key)" -ErrorAction SilentlyContinue }
    if ($ca -ne 0 -or $cb -ne 0 -or -not (Test-Path $fa) -or -not (Test-Path $fb)) { $fail++; "FAIL $k $name exitA=$ca exitB=$cb" | Add-Content $out; continue }
    $ha = (Get-FileHash $fa).Hash; $hb = (Get-FileHash $fb).Hash
    if ($ha -eq $hb) { $same++; "same $k $name" | Add-Content $out }
    else { $diff++; $d = (Compare-Object (Get-Content $fa) (Get-Content $fb) | Measure-Object).Count; "DIFF $k $name ($d differing lines)" | Add-Content $out }
  }
}
"$same same, $diff differ, $fail failed" | Add-Content $out
Get-Content $out
