# ============================================================================
# MOMENT-JET TENSORS AS POLYSPECTRA, and the non-polynomial-invariant question.
#
# Cross-covariance theorem (Wiener-Khinchin): the autocovariance function
# C(tau) = Cov(x(t), x(t+tau)) Fourier-transforms to the power spectrum S(w).
# The moment jet's second-order tensor is the ZERO-LAG slice C(0) = integral of
# S(w). Resolving it in LAG (equivalently, in frequency) is a strictly richer
# object -- and the higher-order lagged cumulants C3(t1,t2), C4(...) transform
# to the bi- and tri-spectrum. This probe asks whether the frequency view sees
# invariants the polynomial null space cannot.
#
# Test on Henon-Heiles (H = 1/2 p^2 + 1/2 r^2 + x^2 y - y^3/3) at two energies:
#   REGULAR (low E): quasiperiodic on a 2-torus -> C(tau) PERSISTS (discrete
#     spectrum, 2 fundamental frequencies -> a SECOND invariant exists, even
#     though it is generically NOT polynomial).
#   CHAOTIC (high E): mixing -> C(tau) DECAYS to 0 (continuous spectrum -> only
#     energy).
# Alongside, the degree-4 polynomial null space (from unknown_invariants) is
# run at each energy to compare what each method reports.
# ============================================================================

$hhF = { param($x,$y) @( -($x + 2.0*$x*$y), -($y + ($x*$x - $y*$y)) ) }
$hhE = { param($s) 0.5*($s[2]*$s[2]+$s[3]*$s[3]) + 0.5*($s[0]*$s[0]+$s[1]*$s[1]) + ($s[0]*$s[0]*$s[1] - $s[1]*$s[1]*$s[1]/3.0) }

function VerletX($force, $s0, $dt, $nsteps, $every) {
  $xx=$s0[0]; $yy=$s0[1]; $ppx=$s0[2]; $ppy=$s0[3]
  $xs = New-Object System.Collections.ArrayList
  for ($step=0; $step -lt $nsteps; $step++) {
    $f = & $force $xx $yy
    $pxh = $ppx + 0.5*$dt*$f[0]; $pyh = $ppy + 0.5*$dt*$f[1]
    $xx = $xx + $dt*$pxh; $yy = $yy + $dt*$pyh
    $f2 = & $force $xx $yy
    $ppx = $pxh + 0.5*$dt*$f2[0]; $ppy = $pyh + 0.5*$dt*$f2[1]
    if ($step % $every -eq 0) { [void]$xs.Add(@($xx,$yy,$ppx,$ppy)) }
  }
  $xs
}

# normalized autocovariance rho(L) of the x-coordinate, at sample-lag L
function AutoCov($states, $lags) {
  $n=$states.Count
  $mu=0.0; foreach($s in $states){ $mu+=$s[0] }; $mu/=$n
  $c0=0.0; foreach($s in $states){ $d=$s[0]-$mu; $c0+=$d*$d }; $c0/=$n
  $out=@()
  foreach($L in $lags){
    $c=0.0; $cnt=$n-$L
    for($i=0;$i -lt $cnt;$i++){ $c += ($states[$i][0]-$mu)*($states[$i+$L][0]-$mu) }
    $out += ($c/$cnt/$c0)
  }
  $out
}

# ---- degree-4 monomial null space over several trajectories (per-traj centered)
function Monos4() { $o=@(); for($a=0;$a -le 4;$a++){for($b=0;$b -le 4-$a;$b++){for($cc=0;$cc -le 4-$a-$b;$cc++){for($dd=0;$dd -le 4-$a-$b-$cc;$dd++){ if($a+$b+$cc+$dd -ge 1){ $o+=,(@($a,$b,$cc,$dd)) } }}}}; $o }
function MonoVal($m,$s){ [math]::Pow($s[0],$m[0])*[math]::Pow($s[1],$m[1])*[math]::Pow($s[2],$m[2])*[math]::Pow($s[3],$m[3]) }
function Nullity($trajs, $monos, $tol) {
  $km=$monos.Count
  $rawStd=New-Object 'double[]' $km
  for($j=0;$j -lt $km;$j++){ $sum=0.0;$sm2=0.0;$cnt=0; foreach($tr in $trajs){foreach($s in $tr){ $v=MonoVal $monos[$j] $s; $sum+=$v;$sm2+=$v*$v;$cnt++ }}; $mu=$sum/$cnt; $var=$sm2/$cnt-$mu*$mu; if($var -lt 0){$var=0}; $sd=[math]::Sqrt($var); if($sd -lt 1e-12){$sd=1.0}; $rawStd[$j]=$sd }
  $rows=New-Object System.Collections.ArrayList
  foreach($tr in $trajs){ $nn=$tr.Count; $cm=New-Object 'double[]' $km
    for($j=0;$j -lt $km;$j++){ $sm=0.0; foreach($s in $tr){ $sm+=MonoVal $monos[$j] $s }; $cm[$j]=$sm/$nn }
    foreach($s in $tr){ $rw=New-Object 'double[]' $km; for($j=0;$j -lt $km;$j++){ $rw[$j]=((MonoVal $monos[$j] $s)-$cm[$j])/$rawStd[$j] }; [void]$rows.Add($rw) } }
  $nn=$rows.Count; $Rm=New-Object 'object[]' $nn; for($i=0;$i -lt $nn;$i++){ $Rm[$i]=$rows[$i] }
  $rank=0; $prow=0
  for($col=0;$col -lt $km -and $prow -lt $nn;$col++){
    $best=$prow;$bestv=[math]::Abs($Rm[$prow][$col]); for($ri=$prow+1;$ri -lt $nn;$ri++){ $av=[math]::Abs($Rm[$ri][$col]); if($av -gt $bestv){$bestv=$av;$best=$ri} }
    if($bestv -lt $tol){ continue }
    if($best -ne $prow){ $tmp=$Rm[$prow];$Rm[$prow]=$Rm[$best];$Rm[$best]=$tmp }
    $pv=$Rm[$prow][$col]; for($j=0;$j -lt $km;$j++){ $Rm[$prow][$j]=$Rm[$prow][$j]/$pv }
    for($ri=0;$ri -lt $nn;$ri++){ if($ri -ne $prow){ $ff=$Rm[$ri][$col]; if($ff -ne 0.0){ for($j=0;$j -lt $km;$j++){ $Rm[$ri][$j]=$Rm[$ri][$j]-$ff*$Rm[$prow][$j] } } } }
    $rank++;$prow++
  }
  $km-$rank
}
$monos=Monos4

$lags = @(0, 50, 100, 200, 400, 800, 1200, 1600)   # sample-lags; dt_sample=0.1 => tau in time = 0.1*L
function Analyze($label, $ICs) {
  # long single trajectory for the autocovariance
  $long = VerletX $hhF $ICs[0] 0.002 400000 50   # dt_sample = 0.1, ~8000 samples
  $Ev=@(); foreach($s in $long){ $Ev+=(& $hhE $s) }
  $Emu=0.0; foreach($e in $Ev){ $Emu+=$e }; $Emu/=$Ev.Count
  $rho = AutoCov $long $lags
  # tail mixing indicator: RMS of rho over the large-lag half
  $tail=0.0;$tc=0; for($i=4;$i -lt $rho.Count;$i++){ $tail+=$rho[$i]*$rho[$i];$tc++ }; $tail=[math]::Sqrt($tail/$tc)
  $verdict = if($tail -lt 0.2){"DECAYS -> continuous spectrum (mixing): only energy is conserved"}else{"PERSISTS -> discrete spectrum (quasiperiodic): a 2nd invariant exists"}
  # several short trajectories for the polynomial null space
  $trajs=@(); foreach($ic in $ICs){ $trajs += ,(VerletX $hhF $ic 0.002 40000 100) }
  $nul = Nullity $trajs $monos 2e-2
  ""
  "==================================================================="
  "$label   (E ~ $('{0:f3}' -f $Emu))"
  "  autocovariance rho_x(tau)  [cross-covariance theorem: FT = power spectrum]"
  ("    tau : " + (($lags | ForEach-Object { '{0,7:f1}' -f (0.1*$_) }) -join ''))
  ("    rho : " + (($rho  | ForEach-Object { '{0,7:f3}' -f $_ }) -join ''))
  "    large-tau RMS |rho| = {0:f3}   =>  {1}" -f $tail, $verdict
  "  polynomial null space (degree-4, $($ICs.Count) trajectories): $nul constant(s) of motion"
}

# REGULAR: low energy, near-integrable torus
Analyze "REGULAR (low energy)" @( @(0.0,0.12,0.12,0.10), @(0.0,0.15,0.10,0.12), @(0.05,0.10,0.13,0.09), @(0.0,0.08,0.14,0.11), @(-0.03,0.13,0.11,0.10) )

# CHAOTIC: high energy, chaotic sea
Analyze "CHAOTIC (high energy)" @( @(0.0,-0.15,0.50,0.10), @(0.0,-0.16,0.48,0.14), @(0.1,-0.12,0.49,0.12), @(-0.05,-0.14,0.50,0.09), @(0.0,-0.15,0.47,0.16) )
