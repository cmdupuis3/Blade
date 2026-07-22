# ============================================================================
# CLOSING THE LOOP: EDMD / Koopman -- one operator gives BOTH the law and the
# conserved quantity.
#
# Fit the Koopman GENERATOR L from trajectory data on a dictionary g(x): L is
# the linear map with  d/dt g = L g,  estimated by regressing finite-difference
# derivatives of the dictionary onto the dictionary itself. Then, from the SAME L:
#   * EQUATIONS OF MOTION: the rows of L for the coordinate observables give
#     dx/dt and dp/dt as combinations of dictionary terms -- equation discovery.
#   * CONSERVED QUANTITIES: the LEFT null space of L (vectors c with c^T L = 0)
#     are the invariants -- the eigenvalue-0 (frequency-0) eigenfunctions.
# Invariant discovery and equation discovery are the same operator read two ways.
# ============================================================================

function Solve($A, $b) {
  $K=$b.Count; $M=New-Object 'object[]' $K
  for($i=0;$i -lt $K;$i++){ $row=New-Object 'double[]' ($K+1); for($j=0;$j -lt $K;$j++){ $row[$j]=$A[$i][$j] }; $row[$K]=$b[$i]; $M[$i]=$row }
  for($col=0;$col -lt $K;$col++){
    $piv=$col; for($rr=$col+1;$rr -lt $K;$rr++){ if([math]::Abs($M[$rr][$col]) -gt [math]::Abs($M[$piv][$col])){$piv=$rr} }
    $tmp=$M[$col];$M[$col]=$M[$piv];$M[$piv]=$tmp
    $d=$M[$col][$col]; if([math]::Abs($d) -lt 1e-14){$d=1e-14}; for($j=$col;$j -le $K;$j++){ $M[$col][$j]=$M[$col][$j]/$d }
    for($rr=0;$rr -lt $K;$rr++){ if($rr -ne $col){ $f=$M[$rr][$col]; for($j=$col;$j -le $K;$j++){ $M[$rr][$j]=$M[$rr][$j]-$f*$M[$col][$j] } } }
  }
  $c=New-Object 'double[]' $K; for($i=0;$i -lt $K;$i++){ $c[$i]=$M[$i][$K] }; $c
}

function Verlet1D($force, $x0, $p0, $dt, $nsteps, $every) {
  $x=$x0;$p=$p0; $out=New-Object System.Collections.ArrayList
  for($step=0;$step -lt $nsteps;$step++){
    $ph=$p+0.5*$dt*(& $force $x); $x=$x+$dt*$ph; $p=$ph+0.5*$dt*(& $force $x)
    if($step % $every -eq 0){ [void]$out.Add(@($x,$p)) }
  }
  $out
}

# Fit L (K x K): d/dt g = L g, from finite-difference derivatives along trajs.
function FitGenerator($trajs, $fns, $dts) {
  $ND=$fns.Count
  $Grows=New-Object System.Collections.ArrayList
  $GDrows=New-Object System.Collections.ArrayList
  foreach($tr in $trajs){
    $M=$tr.Count
    # dictionary values along the trajectory
    $gv=New-Object 'object[]' $M
    for($k=0;$k -lt $M;$k++){ $row=New-Object 'double[]' $ND; for($j=0;$j -lt $ND;$j++){ $bf=$fns[$j]; $st=$tr[$k]; $row[$j]=(& $bf $st) }; $gv[$k]=$row }
    for($k=1;$k -lt $M-1;$k++){
      $gd=New-Object 'double[]' $ND
      for($j=0;$j -lt $ND;$j++){ $gd[$j]=($gv[$k+1][$j]-$gv[$k-1][$j])/(2.0*$dts) }
      [void]$Grows.Add($gv[$k]); [void]$GDrows.Add($gd)
    }
  }
  $n=$Grows.Count
  # normal matrix M = G^T G
  $Mmat=New-Object 'object[]' $ND; for($a=0;$a -lt $ND;$a++){ $Mmat[$a]=New-Object 'double[]' $ND }
  for($a=0;$a -lt $ND;$a++){ for($b=0;$b -lt $ND;$b++){ $s=0.0; for($k=0;$k -lt $n;$k++){ $s+=$Grows[$k][$a]*$Grows[$k][$b] }; $Mmat[$a][$b]=$s } }
  # rows of L: for each observable j, regress its derivative onto the dictionary
  $L=New-Object 'object[]' $ND
  for($j=0;$j -lt $ND;$j++){
    $rhs=New-Object 'double[]' $ND
    for($a=0;$a -lt $ND;$a++){ $s=0.0; for($k=0;$k -lt $n;$k++){ $s+=$Grows[$k][$a]*$GDrows[$k][$j] }; $rhs[$a]=$s }
    $L[$j]=Solve $Mmat $rhs
  }
  $L
}

# left null space of L (vectors c with c^T L = 0): RREF of L^T.
function LeftNull($L, $names, $tol) {
  $K=$names.Count
  $R=New-Object 'object[]' $K
  for($j=0;$j -lt $K;$j++){ $row=New-Object 'double[]' $K; for($i=0;$i -lt $K;$i++){ $row[$i]=$L[$i][$j] }; $R[$j]=$row }
  $pivotCol=@();$prow=0
  for($col=0;$col -lt $K -and $prow -lt $K;$col++){
    $best=$prow;$bestv=[math]::Abs($R[$prow][$col]); for($ri=$prow+1;$ri -lt $K;$ri++){ $av=[math]::Abs($R[$ri][$col]); if($av -gt $bestv){$bestv=$av;$best=$ri} }
    if($bestv -lt $tol){ continue }
    if($best -ne $prow){ $tmp=$R[$prow];$R[$prow]=$R[$best];$R[$best]=$tmp }
    $pv=$R[$prow][$col]; for($i=0;$i -lt $K;$i++){ $R[$prow][$i]=$R[$prow][$i]/$pv }
    for($ri=0;$ri -lt $K;$ri++){ if($ri -ne $prow){ $ff=$R[$ri][$col]; if($ff -ne 0.0){ for($i=0;$i -lt $K;$i++){ $R[$ri][$i]=$R[$ri][$i]-$ff*$R[$prow][$i] } } } }
    $pivotCol+=$col;$prow++
  }
  $rank=$pivotCol.Count
  $free=@(); for($c=0;$c -lt $K;$c++){ if($pivotCol -notcontains $c){ $free+=$c } }
  $vecs=@()
  foreach($fcol in $free){ $v=New-Object 'double[]' $K; $v[$fcol]=1.0; for($pi=0;$pi -lt $rank;$pi++){ $v[$pivotCol[$pi]]=-$R[$pi][$fcol] }
    $mx=0.0; for($i=0;$i -lt $K;$i++){ if([math]::Abs($v[$i]) -gt $mx){$mx=[math]::Abs($v[$i])} }; if($mx -gt 0){ for($i=0;$i -lt $K;$i++){ $v[$i]=$v[$i]/$mx } }
    $vecs+=,$v }
  $vecs
}

function ShowRow($r,$names){ $t=@(); for($j=0;$j -lt $names.Count;$j++){ $val=0.0; try { $val=[double]$r[$j] } catch { $val=0.0 }; if([math]::Abs($val) -gt 1e-2){ $t+=("{0:+0.##;-0.##} {1}" -f $val,$names[$j]) } }; if($t.Count -eq 0){"0"}else{($t -join "  ")} }

function Report($title, $force, $ampls, $names, $fns) {
  $trajs=@(); foreach($a in $ampls){ $trajs += ,(Verlet1D $force $a 0.0 0.002 12000 5) }
  $L = FitGenerator $trajs $fns 0.01
  ""
  "==================================================================="
  "$title   (dictionary: $($names -join ', '))"
  "  EQUATIONS OF MOTION recovered from L's coordinate rows:"
  "    dx/dt = " + (ShowRow $L[0] $names)
  "    dp/dt = " + (ShowRow $L[1] $names)
  "  CONSERVED QUANTITY from L's left null space (c^T L = 0):"
  $nulls=@(LeftNull $L $names 0.03)
  foreach($v in $nulls){ "    conserved: " + (ShowRow $v $names) }
}

# PENDULUM: dx/dt = p, dp/dt = -sin x, invariant 1/2 p^2 - cos x  (non-polynomial)
$pendF = { param($s) -[math]::Sin($s[0]) }
$pendNames = @("x","p","p^2","cos(x)","sin(x)","p sin(x)")
$pendFns = @(
  {param($s) $s[0]}, {param($s) $s[1]}, {param($s) $s[1]*$s[1]},
  {param($s) [math]::Cos($s[0])}, {param($s) [math]::Sin($s[0])}, {param($s) $s[1]*[math]::Sin($s[0])} )
Report "PENDULUM  x'' = -sin(x)" $pendF @(0.8,1.4,2.0,2.5) $pendNames $pendFns

# DUFFING: dx/dt = p, dp/dt = -x - x^3, invariant 1/2 p^2 + 1/2 x^2 + 1/4 x^4
$dufF = { param($s) -($s[0] + $s[0]*$s[0]*$s[0]) }
$dufNames = @("x","p","x^2","x p","p^2","x^3","x^4","x^3 p")
$dufFns = @(
  {param($s) $s[0]}, {param($s) $s[1]}, {param($s) $s[0]*$s[0]}, {param($s) $s[0]*$s[1]},
  {param($s) $s[1]*$s[1]}, {param($s) $s[0]*$s[0]*$s[0]}, {param($s) $s[0]*$s[0]*$s[0]*$s[0]},
  {param($s) $s[0]*$s[0]*$s[0]*$s[1]} )
Report "DUFFING  x'' = -x - x^3" $dufF @(0.5,1.0,1.5,0.8) $dufNames $dufFns
