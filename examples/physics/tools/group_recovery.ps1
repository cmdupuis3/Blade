# ============================================================================
# SYMMETRY GROUP RECOVERY from discovered conserved quantities.
#
# Each conserved quantity Q generates a symmetry flow (Noether). How those
# flows fail to commute -- the POISSON BRACKET algebra {Q_i, Q_j} -- IS the
# Lie algebra of the symmetry group, expressed by its structure constants.
# So once the invariant detector has found the conserved quantities, the group
# is determined: compute their pairwise Poisson brackets and fit each back
# into the invariant basis. The fit coefficients are the structure constants.
#
# Poisson bracket (unit mass, coords x,y, momenta vx,vy):
#   {f,g} = f_x g_vx - f_vx g_x + f_y g_vy - f_vy g_y
# Derivatives by central finite difference, so this works on ANY invariant
# expression the detector returns -- nothing about the group is hard-coded.
# ============================================================================

$h = 1e-5

function Dpar($f, $p, $idx) {
  $pp = $p.Clone(); $pp[$idx] += $h
  $pm = $p.Clone(); $pm[$idx] -= $h
  ((& $f $pp) - (& $f $pm)) / (2.0 * $h)
}
function PB($f, $g, $p) {
  $fx=Dpar $f $p 0; $fy=Dpar $f $p 1; $fvx=Dpar $f $p 2; $fvy=Dpar $f $p 3
  $gx=Dpar $g $p 0; $gy=Dpar $g $p 1; $gvx=Dpar $g $p 2; $gvy=Dpar $g $p 3
  ($fx*$gvx - $fvx*$gx) + ($fy*$gvy - $fvy*$gy)
}

# Small dense linear solve A c = b (Gaussian elimination, partial pivot).
function Solve($A, $b) {
  $K = $b.Count
  $M = New-Object 'object[]' $K
  for ($i=0;$i -lt $K;$i++){ $row = New-Object 'double[]' ($K+1); for($j=0;$j -lt $K;$j++){ $row[$j]=$A[$i][$j] }; $row[$K]=$b[$i]; $M[$i]=$row }
  for ($col=0;$col -lt $K;$col++){
    $piv=$col; for($rr=$col+1;$rr -lt $K;$rr++){ if([math]::Abs($M[$rr][$col]) -gt [math]::Abs($M[$piv][$col])){$piv=$rr} }
    $tmp=$M[$col]; $M[$col]=$M[$piv]; $M[$piv]=$tmp
    $d=$M[$col][$col]; for($j=$col;$j -le $K;$j++){ $M[$col][$j]=$M[$col][$j]/$d }
    for($rr=0;$rr -lt $K;$rr++){ if($rr -ne $col){ $f=$M[$rr][$col]; for($j=$col;$j -le $K;$j++){ $M[$rr][$j]=$M[$rr][$j]-$f*$M[$col][$j] } } }
  }
  $c = New-Object 'double[]' $K; for($i=0;$i -lt $K;$i++){ $c[$i]=$M[$i][$K] }; $c
}

# Least-squares fit of a target (values at sample points) onto basis functions.
# Returns coefficients; near-zero ones are dropped by the caller.
function Fit($targetVals, $basisFns, $points) {
  $K = $basisFns.Count; $n = $points.Count
  # basis value matrix B (n x K)
  $B = New-Object 'object[]' $n
  for ($i=0;$i -lt $n;$i++){ $row=New-Object 'double[]' $K; for($kk=0;$kk -lt $K;$kk++){ $bf=$basisFns[$kk]; $row[$kk]=(& $bf $points[$i]) }; $B[$i]=$row }
  # normal equations M = B^T B, rhs = B^T y
  $M = New-Object 'object[]' $K
  for ($a=0;$a -lt $K;$a++){ $M[$a]=New-Object 'double[]' $K }
  $rhs = New-Object 'double[]' $K
  for ($a=0;$a -lt $K;$a++){
    for ($bcol=0;$bcol -lt $K;$bcol++){ $s=0.0; for($i=0;$i -lt $n;$i++){ $s+=$B[$i][$a]*$B[$i][$bcol] }; $M[$a][$bcol]=$s }
    $s=0.0; for($i=0;$i -lt $n;$i++){ $s+=$B[$i][$a]*$targetVals[$i] }; $rhs[$a]=$s
  }
  Solve $M $rhs
}

function RecoverAlgebra($title, $gens, $genNames, $basisFns, $basisNames, $points) {
  ""
  "==================================================================="
  "$title"
  "  generators: $($genNames -join ', ')   (over $($points.Count) phase-space points)"
  "  Poisson-bracket structure constants  { A , B }  =  ..."
  $ng = $gens.Count
  for ($i=0;$i -lt $ng;$i++){
    for ($j=$i+1;$j -lt $ng;$j++){
      $vals = New-Object 'double[]' $points.Count
      for ($t=0;$t -lt $points.Count;$t++){ $vals[$t] = PB $gens[$i] $gens[$j] $points[$t] }
      $c = Fit $vals $basisFns $points
      $terms=@(); for($k=0;$k -lt $c.Count;$k++){ if([math]::Abs($c[$k]) -gt 1e-4){ $terms += ("{0:+0.###;-0.###}*{1}" -f $c[$k], $basisNames[$k]) } }
      $rhsStr = if($terms.Count -eq 0){"0"}else{($terms -join " ")}
      "    {{ {0,-3}, {1,-3} }} = {2}" -f $genNames[$i], $genNames[$j], $rhsStr
    }
  }
}

# ---- sample phase-space points (deterministic, r bounded away from 0) ----
$points = @()
for ($i = 1; $i -le 60; $i++) {
  $x  = 1.3 + 0.6 * [math]::Cos(1.7*$i)
  $y  = 0.4 + 0.7 * [math]::Sin(2.3*$i + 1.0)
  $vx = 0.5 * [math]::Cos(0.9*$i + 2.0)
  $vy = 0.7 + 0.5 * [math]::Sin(1.3*$i)
  $rr = [math]::Sqrt($x*$x + $y*$y)
  if ($rr -gt 0.4) { $points += ,(@($x,$y,$vx,$vy)) }
}

# ================= KEPLER (planar, GM=1): discovered invariants =============
$K_Lz = { param($p) $p[0]*$p[3] - $p[1]*$p[2] }
$K_E  = { param($p) 0.5*($p[2]*$p[2]+$p[3]*$p[3]) - 1.0/[math]::Sqrt($p[0]*$p[0]+$p[1]*$p[1]) }
$K_Ax = { param($p) $L=$p[0]*$p[3]-$p[1]*$p[2]; $rr=[math]::Sqrt($p[0]*$p[0]+$p[1]*$p[1]); $p[3]*$L - $p[0]/$rr }
$K_Ay = { param($p) $L=$p[0]*$p[3]-$p[1]*$p[2]; $rr=[math]::Sqrt($p[0]*$p[0]+$p[1]*$p[1]); -$p[2]*$L - $p[1]/$rr }
$K_gens  = @($K_Lz,$K_E,$K_Ax,$K_Ay)
$K_names = @("Lz","E","Ax","Ay")
$K_ELz = { param($p) $LL=$p[0]*$p[3]-$p[1]*$p[2]; $ee=0.5*($p[2]*$p[2]+$p[3]*$p[3])-1.0/[math]::Sqrt($p[0]*$p[0]+$p[1]*$p[1]); $ee*$LL }
$K_one = { param($p) 1.0 }
$K_basis  = @($K_Lz,$K_E,$K_Ax,$K_Ay,$K_ELz,$K_one)
$K_bnames = @("Lz","E","Ax","Ay","E*Lz","1")
RecoverAlgebra "KEPLER (planar): angular momentum + Laplace-Runge-Lenz vector" $K_gens $K_names $K_basis $K_bnames $points
"  E Poisson-commutes with all (it is the Hamiltonian / Casimir)."
"  On the bound shell E<0, rescale B = A / sqrt(-2E):"
"    {Lz,Bx}=By  {Lz,By}=-Bx  {Bx,By}=Lz   ==>   so(3)   (planar Kepler bound-state symmetry;"
"    so(2,1) for E>0). The 3-D problem lifts this to the famous SO(4)."

# ================= 2D ISOTROPIC HARMONIC OSCILLATOR (w=1) ===================
$O_E  = { param($p) 0.5*($p[2]*$p[2]+$p[3]*$p[3]) + 0.5*($p[0]*$p[0]+$p[1]*$p[1]) }
$O_Lz = { param($p) $p[0]*$p[3] - $p[1]*$p[2] }
$O_T1 = { param($p) 0.5*($p[2]*$p[2]-$p[3]*$p[3]) + 0.5*($p[0]*$p[0]-$p[1]*$p[1]) }
$O_T2 = { param($p) $p[2]*$p[3] + $p[0]*$p[1] }
$O_one = { param($p) 1.0 }
$O_gens  = @($O_Lz,$O_E,$O_T1,$O_T2)
$O_names = @("Lz","E","T1","T2")
$O_basis  = @($O_Lz,$O_E,$O_T1,$O_T2,$O_one)
$O_bnames = @("Lz","E","T1","T2","1")
RecoverAlgebra "2D ISOTROPIC OSCILLATOR: angular momentum + Fradkin tensor" $O_gens $O_names $O_basis $O_bnames $points
"  Rescaling K = (Lz,T1,T2)/2:  {K1,K2}=K3 cyclic   ==>   su(2) = so(3)"
"  -- the hidden symmetry that makes the 2-D oscillator's levels degenerate."
