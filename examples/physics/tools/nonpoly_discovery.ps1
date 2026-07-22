# ============================================================================
# NON-POLYNOMIAL INVARIANT DISCOVERY -- and the tie to equation discovery.
#
# The polynomial null space can only find polynomial invariants. The fix is
# not deeper machinery but a richer DICTIONARY: add the non-polynomial atoms
# (trig, 1/r, ...) the invariant is built from, and the SAME zero-variance null
# space returns it -- as a formula, in that basis.
#
# Test: the pendulum  x'' = -sin(x),  whose energy  E = 1/2 x'^2 - cos(x)  is
# genuinely non-polynomial. We run the discovery with two dictionaries over
# several LARGE-amplitude (strongly anharmonic) librations:
#   (A) polynomial in (x, x')  -- cannot represent cos(x); finds nothing exact.
#   (B) polynomial + {cos x, sin x} -- finds 1/2 x'^2 - cos(x), the exact
#       non-polynomial invariant, and prints its formula.
#
# Choosing which atoms to add IS the SINDy / equation-discovery step: a library
# of candidate terms + a sparsity/zero-variance criterion. Invariant discovery
# and equation discovery are the eigenvalue-1 and the full spectrum of the same
# Koopman picture.
# ============================================================================

function VerletPend($x0, $p0, $dt, $nsteps, $every) {
  $x=$x0; $p=$p0
  $out = New-Object System.Collections.ArrayList
  for($step=0;$step -lt $nsteps;$step++){
    $ph = $p - 0.5*$dt*[math]::Sin($x)
    $x = $x + $dt*$ph
    $p = $ph - 0.5*$dt*[math]::Sin($x)
    if($step % $every -eq 0){ [void]$out.Add(@($x,$p)) }
  }
  $out
}

# null space of the dictionary covariance over several trajectories (per-traj
# centered, scale-normalized), returning the null VECTORS in the original basis.
function NullVectors($trajs, $fns, $names, $tol) {
  $km=$fns.Count
  $rawStd=New-Object 'double[]' $km
  for($j=0;$j -lt $km;$j++){ $sum=0.0;$sm2=0.0;$cnt=0; $bf=$fns[$j]; foreach($tr in $trajs){foreach($s in $tr){ $v=(& $bf $s); $sum+=$v;$sm2+=$v*$v;$cnt++ }}; $mu=$sum/$cnt; $var=$sm2/$cnt-$mu*$mu; if($var -lt 0){$var=0}; $sd=[math]::Sqrt($var); if($sd -lt 1e-12){$sd=1.0}; $rawStd[$j]=$sd }
  $rows=New-Object System.Collections.ArrayList
  foreach($tr in $trajs){ $nn=$tr.Count; $cm=New-Object 'double[]' $km
    for($j=0;$j -lt $km;$j++){ $bf=$fns[$j]; $sm=0.0; foreach($s in $tr){ $sm+=(& $bf $s) }; $cm[$j]=$sm/$nn }
    foreach($s in $tr){ $rw=New-Object 'double[]' $km; for($j=0;$j -lt $km;$j++){ $bf=$fns[$j]; $rw[$j]=((& $bf $s)-$cm[$j])/$rawStd[$j] }; [void]$rows.Add($rw) } }
  $nn=$rows.Count; $Rm=New-Object 'object[]' $nn; for($i=0;$i -lt $nn;$i++){ $Rm[$i]=$rows[$i] }
  $pivotCol=@(); $prow=0
  for($col=0;$col -lt $km -and $prow -lt $nn;$col++){
    $best=$prow;$bestv=[math]::Abs($Rm[$prow][$col]); for($ri=$prow+1;$ri -lt $nn;$ri++){ $av=[math]::Abs($Rm[$ri][$col]); if($av -gt $bestv){$bestv=$av;$best=$ri} }
    if($bestv -lt $tol){ continue }
    if($best -ne $prow){ $tmp=$Rm[$prow];$Rm[$prow]=$Rm[$best];$Rm[$best]=$tmp }
    $pv=$Rm[$prow][$col]; for($j=0;$j -lt $km;$j++){ $Rm[$prow][$j]=$Rm[$prow][$j]/$pv }
    for($ri=0;$ri -lt $nn;$ri++){ if($ri -ne $prow){ $ff=$Rm[$ri][$col]; if($ff -ne 0.0){ for($j=0;$j -lt $km;$j++){ $Rm[$ri][$j]=$Rm[$ri][$j]-$ff*$Rm[$prow][$j] } } } }
    $pivotCol+=$col; $prow++
  }
  $rank=$pivotCol.Count
  $free=@(); for($c=0;$c -lt $km;$c++){ if($pivotCol -notcontains $c){ $free+=$c } }
  $vecs=@()
  foreach($fcol in $free){
    $v=New-Object 'double[]' $km; $v[$fcol]=1.0
    for($pi=0;$pi -lt $rank;$pi++){ $v[$pivotCol[$pi]]=-$Rm[$pi][$fcol] }
    # convert from normalized-column space to the ORIGINAL dictionary basis
    $orig=New-Object 'double[]' $km; for($j=0;$j -lt $km;$j++){ $orig[$j]=$v[$j]/$rawStd[$j] }
    # normalize so the largest-magnitude coefficient is 1
    $mx=0.0; for($j=0;$j -lt $km;$j++){ if([math]::Abs($orig[$j]) -gt $mx){ $mx=[math]::Abs($orig[$j]) } }
    if($mx -gt 0){ for($j=0;$j -lt $km;$j++){ $orig[$j]=$orig[$j]/$mx } }
    $vecs += ,$orig
  }
  [pscustomobject]@{ nullity=($km-$rank); vecs=$vecs }
}
function ShowVec($v,$names){ $t=@(); for($j=0;$j -lt $names.Count;$j++){ if([math]::Abs($v[$j]) -gt 1e-3){ $t += ("{0:+0.###;-0.###}*[{1}]" -f $v[$j],$names[$j]) } }; ($t -join "  ") }

# large-amplitude pendulum librations (strongly anharmonic): E = -cos(x0)
$trajs=@()
foreach($x0 in @(1.5, 2.0, 2.5, 1.8)){ $trajs += ,(VerletPend $x0 0.0 0.002 40000 100) }

# (A) polynomial dictionary in (x, x')
$pA = @(
  @{n="x";      f={param($s) $s[0]}},
  @{n="x'";     f={param($s) $s[1]}},
  @{n="x^2";    f={param($s) $s[0]*$s[0]}},
  @{n="x*x'";   f={param($s) $s[0]*$s[1]}},
  @{n="x'^2";   f={param($s) $s[1]*$s[1]}},
  @{n="x^3";    f={param($s) $s[0]*$s[0]*$s[0]}},
  @{n="x^4";    f={param($s) $s[0]*$s[0]*$s[0]*$s[0]}} )
$nA=@(); $fA=@(); foreach($e in $pA){ $nA+=$e.n; $fA+=$e.f }
$rA = NullVectors $trajs $fA $nA 5e-3
""
"==================================================================="
"PENDULUM x'' = -sin(x)  (4 large-amplitude librations)"
"  (A) POLYNOMIAL dictionary [x, x', x^2, x*x', x'^2, x^3, x^4]:"
"      => $($rA.nullity) exact invariant(s)" + $(if($rA.nullity -eq 0){"  -- blind: energy is not polynomial"}else{""})
foreach($v in $rA.vecs){ "        " + (ShowVec $v $nA) }

# (B) polynomial + trig atoms
$pB = @(
  @{n="x'^2";   f={param($s) $s[1]*$s[1]}},
  @{n="cos(x)"; f={param($s) [math]::Cos($s[0])}},
  @{n="sin(x)"; f={param($s) [math]::Sin($s[0])}},
  @{n="x^2";    f={param($s) $s[0]*$s[0]}},
  @{n="x*x'";   f={param($s) $s[0]*$s[1]}} )
$nB=@(); $fB=@(); foreach($e in $pB){ $nB+=$e.n; $fB+=$e.f }
$rB = NullVectors $trajs $fB $nB 5e-3
"  (B) + trig atoms [x'^2, cos(x), sin(x), x^2, x*x']:"
"      => $($rB.nullity) exact invariant(s)"
foreach($v in $rB.vecs){ "        DISCOVERED: " + (ShowVec $v $nB) + "   (= the energy 1/2 x'^2 - cos x, non-polynomial)" }
