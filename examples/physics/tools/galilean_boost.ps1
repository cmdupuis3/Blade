# ============================================================================
# THE MISSING SECTOR: conservation laws LINEAR IN TIME live in the JORDAN
# BLOCK of the Koopman generator at eigenvalue 0 -- and every state-only
# detector in this example set is structurally blind to them.
#
# Two-body system (1D): masses m1, m2, harmonic bond V = k/2 (x1-x2)^2.
# Alongside E and P = p1+p2 there is a TENTH-INVARIANT-type conservation law
#     G = m1 x1 + m2 x2 - P t        (the Galilean boost / center-of-mass law)
# G is NOT a function of the state alone (it needs t), so no dictionary of
# phase-space functions contains it: the null space of the fitted generator L
# cannot see it. But it is not gone -- it is hiding in the STRUCTURE of L:
#
#     L (m1 x1 + m2 x2) = P,   L P = 0
#
# i.e. the eigenvalue-0 sector of L is NOT diagonalizable: (m1 x1 + m2 x2, P)
# form a nilpotent 2x2 Jordan chain. ker(L^2)/ker(L) recovers the chain, and
# the chain IS the time-linear conservation law: G = (chain) - (L chain) t.
#
# The payoff is bigger than the recovery:
#   * the chain vector, normalized so L(chain) = P (with P = p1+p2 read in the
#     data's canonical momentum units), is EXACTLY (m1, 0, m2, 0): the fitted
#     generator's Jordan structure returns THE MASSES from kinematics alone;
#   * the Poisson bracket {G, P} = m1 + m2 is a NUMBER, not a function: the
#     boost sector closes only up to a CENTRAL CHARGE, and that central charge
#     is the total mass (Bargmann's central extension of the Galilei algebra).
#     The algebra of the discovered conservation laws WEIGHS the system.
#
# 16_galilean_boost_tenth_invariant.blade verifies the split through the Blade
# tower (fused zip/reduce): P, E, G degenerate; X_cm spreads.
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

# two-body 1D velocity Verlet; sample = @(x1, p1, x2, p2, t)
function Verlet2B($ma, $mb, $kS, $ic, $dt, $nsteps, $every) {
  $x1=$ic[0]; $p1=$ic[1]; $x2=$ic[2]; $p2=$ic[3]
  $out = New-Object System.Collections.ArrayList
  for ($step=0; $step -lt $nsteps; $step++) {
    $fB = -$kS*($x1-$x2)
    $ph1 = $p1 + 0.5*$dt*$fB; $ph2 = $p2 - 0.5*$dt*$fB
    $x1 = $x1 + $dt*$ph1/$ma; $x2 = $x2 + $dt*$ph2/$mb
    $fB2 = -$kS*($x1-$x2)
    $p1 = $ph1 + 0.5*$dt*$fB2; $p2 = $ph2 - 0.5*$dt*$fB2
    # bind t first: a trailing arithmetic expression inside @(...) makes the
    # comma chain bind before '*', replicating the array 0.002 times -> empty
    if ($step % $every -eq 0) { $tt=($step+1)*$dt; [void]$out.Add(@($x1,$p1,$x2,$p2,$tt)) }
  }
  $out
}

function FitGenerator($trajs, $fns, $dts) {
  $ND=$fns.Count
  $Grows=New-Object System.Collections.ArrayList
  $GDrows=New-Object System.Collections.ArrayList
  foreach($tr in $trajs){
    $M=$tr.Count
    $gv=New-Object 'object[]' $M
    for($k=0;$k -lt $M;$k++){ $row=New-Object 'double[]' $ND; for($j=0;$j -lt $ND;$j++){ $bf=$fns[$j]; $st=$tr[$k]; $row[$j]=(& $bf $st) }; $gv[$k]=$row }
    for($k=1;$k -lt $M-1;$k++){
      $gd=New-Object 'double[]' $ND
      for($j=0;$j -lt $ND;$j++){ $gd[$j]=($gv[$k+1][$j]-$gv[$k-1][$j])/(2.0*$dts) }
      [void]$Grows.Add($gv[$k]); [void]$GDrows.Add($gd)
    }
  }
  $n=$Grows.Count
  $Mmat=New-Object 'object[]' $ND; for($a=0;$a -lt $ND;$a++){ $Mmat[$a]=New-Object 'double[]' $ND }
  for($a=0;$a -lt $ND;$a++){ for($b=0;$b -lt $ND;$b++){ $s=0.0; for($k=0;$k -lt $n;$k++){ $s+=$Grows[$k][$a]*$Grows[$k][$b] }; $Mmat[$a][$b]=$s } }
  $L=New-Object 'object[]' $ND
  for($j=0;$j -lt $ND;$j++){
    $rhs=New-Object 'double[]' $ND
    for($a=0;$a -lt $ND;$a++){ $s=0.0; for($k=0;$k -lt $n;$k++){ $s+=$Grows[$k][$a]*$GDrows[$k][$j] }; $rhs[$a]=$s }
    $L[$j]=Solve $Mmat $rhs
  }
  $L
}

# left null space of L (vectors c with c^T L = 0): RREF of L^T
function LeftNull($L, $ND, $tol) {
  $R=New-Object 'object[]' $ND
  for($j=0;$j -lt $ND;$j++){ $row=New-Object 'double[]' $ND; for($i=0;$i -lt $ND;$i++){ $row[$i]=$L[$i][$j] }; $R[$j]=$row }
  $pivotCol=@();$prow=0
  for($col=0;$col -lt $ND -and $prow -lt $ND;$col++){
    $best=$prow;$bestv=[math]::Abs($R[$prow][$col]); for($ri=$prow+1;$ri -lt $ND;$ri++){ $av=[math]::Abs($R[$ri][$col]); if($av -gt $bestv){$bestv=$av;$best=$ri} }
    if($bestv -lt $tol){ continue }
    if($best -ne $prow){ $tmp=$R[$prow];$R[$prow]=$R[$best];$R[$best]=$tmp }
    $pv=$R[$prow][$col]; for($i=0;$i -lt $ND;$i++){ $R[$prow][$i]=$R[$prow][$i]/$pv }
    for($ri=0;$ri -lt $ND;$ri++){ if($ri -ne $prow){ $ff=$R[$ri][$col]; if($ff -ne 0.0){ for($i=0;$i -lt $ND;$i++){ $R[$ri][$i]=$R[$ri][$i]-$ff*$R[$prow][$i] } } } }
    $pivotCol+=$col;$prow++
  }
  $rank=$pivotCol.Count
  $free=@(); for($c=0;$c -lt $ND;$c++){ if($pivotCol -notcontains $c){ $free+=$c } }
  $vecs=@()
  foreach($fcol in $free){ $v=New-Object 'double[]' $ND; $v[$fcol]=1.0; for($pi=0;$pi -lt $rank;$pi++){ $v[$pivotCol[$pi]]=-$R[$pi][$fcol] }
    $vecs+=,$v }
  ,$vecs
}

# NOTE: loop var must NOT be $a -- case-insensitive collision with param $A
# (the classic $k/$K trap; it zeroes the matrix mid-multiply)
function MatMul($A, $B, $n) {
  $out=New-Object 'object[]' $n
  for($i=0;$i -lt $n;$i++){
    $row=New-Object 'double[]' $n
    for($j=0;$j -lt $n;$j++){ $s=0.0; for($q=0;$q -lt $n;$q++){ $s+=$A[$i][$q]*$B[$q][$j] }; $row[$j]=$s }
    $out[$i]=$row
  }
  $out
}

function RowTimesL($v, $L, $n) {
  $out=New-Object 'double[]' $n
  for($j=0;$j -lt $n;$j++){ $s=0.0; for($i=0;$i -lt $n;$i++){ $s+=$v[$i]*$L[$i][$j] }; $out[$j]=$s }
  $out
}

function VDot($a,$b){ $s=0.0; for($i=0;$i -lt $a.Count;$i++){ $s+=$a[$i]*$b[$i] }; $s }
function VNorm($a){ [math]::Sqrt((VDot $a $a)) }

function ShowRow($r,$names){ $t=@(); for($j=0;$j -lt $names.Count;$j++){ $val=[double]$r[$j]; if([math]::Abs($val) -gt 5e-3){ $t+=("{0:+0.###;-0.###} {1}" -f $val,$names[$j]) } }; if($t.Count -eq 0){"0"}else{($t -join "  ")} }

function Disp($vals) {
  $nn=$vals.Count; $mn=0.0; foreach($v in $vals){$mn+=$v}; $mn/=$nn
  $sv=0.0; foreach($v in $vals){ $dv=$v-$mn; $sv+=$dv*$dv }; $sv/=$nn
  [math]::Sqrt($sv)/([math]::Abs($mn)+1e-9)
}

# equal-time canonical Poisson bracket by central differences; state layout
# (x1, p1, x2, p2), canonical pairs (0,1) and (2,3); t enters as a parameter
function PB($fA, $fB, $pt, $tt) {
  $h=1e-5
  $dA=New-Object 'double[]' 4; $dB=New-Object 'double[]' 4
  for($i=0;$i -lt 4;$i++){
    $up=$pt.Clone(); $dn=$pt.Clone(); $up[$i]+=$h; $dn[$i]-=$h
    $dA[$i]=((& $fA $up $tt)-(& $fA $dn $tt))/(2.0*$h)
    $dB[$i]=((& $fB $up $tt)-(& $fB $dn $tt))/(2.0*$h)
  }
  ($dA[0]*$dB[1]-$dA[1]*$dB[0]) + ($dA[2]*$dB[3]-$dA[3]*$dB[2])
}

$names=@("x1","p1","x2","p2")
$fns=@( {param($s)$s[0]}, {param($s)$s[1]}, {param($s)$s[2]}, {param($s)$s[3]} )

function RunCase($ma, $mb, $kS) {
  ""
  "==================================================================="
  ("TWO-BODY  m1={0}  m2={1}  k={2}   (dictionary: x1, p1, x2, p2)" -f $ma,$mb,$kS)
  # (last IC avoids G=0 exactly: a zero-mean invariant inflates the relative
  #  dispersion readout -- the same trap documented in example 10's header)
  $ICs=@( @(0.5,0.8,-0.3,0.4), @(-0.2,-0.5,0.4,1.2), @(0.0,-1.0,0.6,-0.5), @(0.4,2.0,-0.5,0.1) )
  $trajs=@(); foreach($ic in $ICs){ $trajs += ,(Verlet2B $ma $mb $kS $ic 0.002 8000 5) }
  $L = FitGenerator $trajs $fns 0.01
  "  EQUATIONS OF MOTION (rows of the fitted L):"
  for($j=0;$j -lt 4;$j++){ "    d({0})/dt = {1}" -f $names[$j], (ShowRow $L[$j] $names) }
  # --- ker L: the ordinary (state-function) conserved quantities ---
  $ker1 = LeftNull $L 4 0.02
  "  ker L^T  (ordinary invariants in the dictionary): $($ker1.Count) vector(s)"
  $Pn=$null
  foreach($v in $ker1){
    # normalize so the p1 coefficient is 1 (canonical momentum units)
    $sc=$v[1]; if([math]::Abs($sc) -lt 1e-8){ $sc=$v[3] }
    $vn=New-Object 'double[]' 4; for($i=0;$i -lt 4;$i++){ $vn[$i]=$v[$i]/$sc }
    "    conserved: " + (ShowRow $vn $names)
    $Pn=$vn
  }
  # --- ker L^2 / ker L: the Jordan chain ---
  $L2 = MatMul $L $L 4
  $ker2 = LeftNull $L2 4 0.02
  "  ker (L^2)^T: $($ker2.Count) vector(s)  -- eigenvalue 0 is NOT diagonalizable"
  # pick the ker2 vector with the largest residual v^T L (the chain vector)
  $chain=$null; $bestres=0.0
  foreach($v in $ker2){
    $res = RowTimesL $v $L 4
    $rn = VNorm $res
    if($rn -gt $bestres){ $bestres=$rn; $chain=$v }
  }
  # remove the ker-L component sitting in the momentum slots, then rescale so
  # that L(chain) = P exactly: the chain inherits the data's canonical units
  $beta = 0.5*($chain[1]+$chain[3])
  $vpos=New-Object 'double[]' 4; for($i=0;$i -lt 4;$i++){ $vpos[$i]=$chain[$i]-$beta*$Pn[$i] }
  $img = RowTimesL $vpos $L 4
  $scl = (VDot $img $Pn)/(VDot $Pn $Pn)
  for($i=0;$i -lt 4;$i++){ $vpos[$i]=$vpos[$i]/$scl }
  $img = RowTimesL $vpos $L 4
  "  JORDAN CHAIN at eigenvalue 0:"
  "    chain    = " + (ShowRow $vpos $names)
  "    L(chain) = " + (ShowRow $img $names) + "   (= P: nilpotent 2x2 block)"
  ("    => masses read off the chain: m1 = {0:0.####}, m2 = {1:0.####}  (true: {2}, {3})" -f $vpos[0], $vpos[2], $ma, $mb)
  # --- the time-linear conservation law G = chain - P t ---
  $fG = { param($s,$tt) $vpos[0]*$s[0]+$vpos[1]*$s[1]+$vpos[2]*$s[2]+$vpos[3]*$s[3] - ($Pn[0]*$s[0]+$Pn[1]*$s[1]+$Pn[2]*$s[2]+$Pn[3]*$s[3])*$tt }.GetNewClosure()
  $fP = { param($s,$tt) $Pn[0]*$s[0]+$Pn[1]*$s[1]+$Pn[2]*$s[2]+$Pn[3]*$s[3] }.GetNewClosure()
  $fH = { param($s,$tt) 0.5*$s[1]*$s[1]/$ma + 0.5*$s[3]*$s[3]/$mb + 0.5*$kS*($s[0]-$s[2])*($s[0]-$s[2]) }.GetNewClosure()
  "  G = chain - P t  along each trajectory (dispersion; X_cm shown for contrast):"
  $ti=0
  foreach($tr in $trajs){
    $Gv=@();$Xv=@()
    foreach($s in $tr){ $Gv += (& $fG $s $s[4]); $Xv += (($ma*$s[0]+$mb*$s[2])/($ma+$mb)) }
    ("    traj {0}:  G = {1,8:0.####}  disp(G) = {2:e2}   disp(X_cm) = {3:e2}" -f $ti, $Gv[0], (Disp $Gv), (Disp $Xv))
    $ti++
  }
  # --- the bracket algebra: the central charge ---
  $pts=@( @(0.37,-0.61,0.22,0.9), @(-0.5,1.1,0.4,-0.2), @(0.05,0.33,-0.44,0.77) )
  $tts=@(0.0, 1.7, 4.2)
  "  BRACKET ALGEBRA of the discovered laws (finite-difference, 3 random points):"
  $charges=@()
  for($ii=0;$ii -lt 3;$ii++){
    $gp = PB $fG $fP $pts[$ii] $tts[$ii]
    $gh = PB $fG $fH $pts[$ii] $tts[$ii]
    $ph = PB $fP $fH $pts[$ii] $tts[$ii]
    $pv = & $fP $pts[$ii] $tts[$ii]
    ("    pt {0}:  {{G,P}} = {1:0.######}   {{G,H}} = {2:0.######} (P here = {3:0.######})   {{P,H}} = {4:e1}" -f $ii,$gp,$gh,$pv,$ph)
    $charges += $gp
  }
  $avg=($charges | Measure-Object -Average).Average
  ("  => CENTRAL CHARGE {{G,P}} = {0:0.#####}  =  m1 + m2 = {1}   (Bargmann mass)" -f $avg, ($ma+$mb))
  "     {G,P} is the SAME NUMBER at every phase-space point -- a central term."
  "     The bracket of two discovered conservation laws is not a third function"
  "     but a scalar: the Galilei algebra closes only centrally, and the charge"
  "     that extends it IS the total mass. The pipeline weighed the system."
}

RunCase 1.0 2.0 1.0
RunCase 1.0 3.0 1.0
RunCase 2.5 1.5 2.0

# --- emit the 24-sample trajectory for 16_galilean_boost_tenth_invariant.blade ---
""
"==================================================================="
"EXAMPLE DATA (m1=1, m2=2, k=1, IC x1=0.5 p1=0.8 x2=-0.3 p2=0.4):"
$ex = Verlet2B 1.0 2.0 1.0 @(0.5,0.8,-0.3,0.4) 0.002 6000 250
$inv=[System.Globalization.CultureInfo]::InvariantCulture
$Ts=@();$X1s=@();$P1s=@();$X2s=@();$P2s=@()
foreach($s in $ex){
  $X1s += $s[0].ToString("G12",$inv); $P1s += $s[1].ToString("G12",$inv)
  $X2s += $s[2].ToString("G12",$inv); $P2s += $s[3].ToString("G12",$inv)
  $Ts  += $s[4].ToString("G12",$inv)
}
"let T  = [$($Ts -join ', ')]"
"let X1 = [$($X1s -join ', ')]"
"let P1 = [$($P1s -join ', ')]"
"let X2 = [$($X2s -join ', ')]"
"let P2 = [$($P2s -join ', ')]"
# reference towers for the pins
$cands=@{}
$Gr=@();$Pr=@();$Er=@();$Xr=@()
foreach($s in $ex){
  $Gr += (1.0*$s[0]+2.0*$s[2]-($s[1]+$s[3])*$s[4])
  $Pr += ($s[1]+$s[3])
  $Er += (0.5*$s[1]*$s[1]+0.25*$s[3]*$s[3]+0.5*($s[0]-$s[2])*($s[0]-$s[2]))
  $Xr += ((1.0*$s[0]+2.0*$s[2])/3.0)
}
function K12($vals){
  $nn=$vals.Count; $mn=0.0; foreach($v in $vals){$mn+=$v}; $mn/=$nn
  $sv=0.0; foreach($v in $vals){ $dv=$v-$mn; $sv+=$dv*$dv }; $sv/=$nn
  @($mn,$sv)
}
$kG=K12 $Gr; $kP=K12 $Pr; $kE=K12 $Er; $kX=K12 $Xr
("reference:  G k1={0:e12} k2={1:e3}   P k1={2:e12} k2={3:e3}" -f $kG[0],$kG[1],$kP[0],$kP[1])
("            E k1={0:e12} k2={1:e3}   Xcm k1={2:e6} k2={3:e3}" -f $kE[0],$kE[1],$kX[0],$kX[1])
