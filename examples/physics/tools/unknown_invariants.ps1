# ============================================================================
# THE REAL TEST: point the discovery engine at systems whose invariants are
# NOT known in advance, including a chaotic one, and see whether the tower
# finds anything beyond energy.
#
#   (A) Separable quartic:  H = 1/2 p^2 + 1/2 r^2 + lam (x^4 + y^4)
#       Integrable: x and y decouple, so E_x and E_y are BOTH conserved.
#       Expect the engine to find a SECOND invariant (E_x - E_y). Angular
#       momentum is NOT conserved (x^4+y^4 is not rotationally symmetric).
#
#   (B) Henon-Heiles:  H = 1/2 p^2 + 1/2 r^2 + lam (x^2 y - y^3/3)
#       The textbook CHAOTIC system at moderate energy. Only E is conserved.
#       Expect the engine to find NOTHING beyond energy -- the honest negative.
#
#   (C) Duffing (1-DOF), degree 4 vs degree 8: the COUNT IS A RING, not a
#       vector-space dimension. Products of invariants are invariants, so the
#       nullity inflates as soon as the dictionary degree admits them: at
#       degree 8 the null space contains E AND E^2 (nullity 2) although the
#       system has exactly ONE constant of motion. The honest count is the
#       number of FUNCTIONALLY INDEPENDENT generators = the rank of the
#       Jacobian of the null-space combinations at generic points. (A)/(B)'s
#       degree-4 counts were safe only because their generators have degree
#       3-4, so products first appear at degree 6 (HH: E^2) and 8 (quartic).
#
# No closed form, so we integrate real trajectories with a symplectic
# (velocity-Verlet) step, sample them, and (1) read the dispersion of candidate
# quantities [detector] and (2) take the null space of a monomial dictionary's
# covariance [discovery]. A conserved combination has ~0 variance. NULLITY =
# dimension of the space of invariant polynomials up to the dictionary degree;
# GENERATORS = how many functionally independent constants of motion span it.
# ============================================================================

function Verlet($force, $s0, $dt, $nsteps, $every) {
  $xx=$s0[0]; $yy=$s0[1]; $ppx=$s0[2]; $ppy=$s0[3]
  $samples = New-Object System.Collections.ArrayList
  for ($step=0; $step -lt $nsteps; $step++) {
    $f = & $force $xx $yy
    $pxh = $ppx + 0.5*$dt*$f[0]; $pyh = $ppy + 0.5*$dt*$f[1]
    $xx = $xx + $dt*$pxh; $yy = $yy + $dt*$pyh
    $f2 = & $force $xx $yy
    $ppx = $pxh + 0.5*$dt*$f2[0]; $ppy = $pyh + 0.5*$dt*$f2[1]
    if ($step % $every -eq 0) { [void]$samples.Add(@($xx,$yy,$ppx,$ppy)) }
  }
  $samples
}

# 1-DOF Verlet: state (x, p), force = f(x)
function Verlet1($force, $s0, $dt, $nsteps, $every) {
  $xx=$s0[0]; $ppx=$s0[1]
  $samples = New-Object System.Collections.ArrayList
  for ($step=0; $step -lt $nsteps; $step++) {
    $pxh = $ppx + 0.5*$dt*(& $force $xx)
    $xx = $xx + $dt*$pxh
    $ppx = $pxh + 0.5*$dt*(& $force $xx)
    if ($step % $every -eq 0) { [void]$samples.Add(@($xx,$ppx)) }
  }
  $samples
}

function Disp($vals) {
  $nn=$vals.Count; $mn=0.0; foreach($v in $vals){$mn+=$v}; $mn/=$nn
  $sv=0.0; foreach($v in $vals){ $dv=$v-$mn; $sv+=$dv*$dv }; $sv/=$nn
  [math]::Sqrt($sv)/([math]::Abs($mn)+1e-9)
}

# all monomials in $nv variables (2 or 4) with 1 <= total degree <= $deg
function Monos($nv, $deg) {
  $out=@()
  if ($nv -eq 2) {
    for($a=0;$a -le $deg;$a++){ for($b=0;$b -le $deg-$a;$b++){
      if($a+$b -ge 1){ $out += ,(@($a,$b)) }
    }}
  } else {
    for($a=0;$a -le $deg;$a++){ for($b=0;$b -le $deg-$a;$b++){ for($cc=0;$cc -le $deg-$a-$b;$cc++){ for($dd=0;$dd -le $deg-$a-$b-$cc;$dd++){
      if($a+$b+$cc+$dd -ge 1){ $out += ,(@($a,$b,$cc,$dd)) }
    }}}}
  }
  $out
}

function MonoVal($m, $s) {
  $v = 1.0
  for ($vi=0; $vi -lt $m.Count; $vi++) {
    if ($m[$vi] -ne 0) { $v *= [math]::Pow($s[$vi], $m[$vi]) }
  }
  $v
}

# gradient of the combination (coeffs over monomials) at state $s
function CombGrad($coeffs, $monos, $s) {
  $nv = $monos[0].Count
  $grad = New-Object 'double[]' $nv
  for ($j=0; $j -lt $monos.Count; $j++) {
    if ($coeffs[$j] -eq 0.0) { continue }
    $m = $monos[$j]
    for ($vi=0; $vi -lt $nv; $vi++) {
      if ($m[$vi] -eq 0) { continue }
      $g = $coeffs[$j] * $m[$vi]
      for ($vj=0; $vj -lt $nv; $vj++) {
        $pw = $m[$vj]; if ($vj -eq $vi) { $pw = $pw - 1 }
        if ($pw -ne 0) { $g *= [math]::Pow($s[$vj], $pw) }
      }
      $grad[$vi] += $g
    }
  }
  $grad
}

# rank of a small matrix (array of double[] rows), rows normalized first
function MatRank($rowsIn, $tol) {
  $work = @()
  foreach ($rw in $rowsIn) {
    $nrm = 0.0; foreach ($v in $rw) { $nrm += $v*$v }; $nrm = [math]::Sqrt($nrm)
    if ($nrm -lt 1e-300) { continue }
    $cp = New-Object 'double[]' $rw.Count
    for ($j=0; $j -lt $rw.Count; $j++) { $cp[$j] = $rw[$j]/$nrm }
    $work += ,$cp
  }
  if ($work.Count -eq 0) { return 0 }
  $ncols = $work[0].Count
  $rank = 0; $prow = 0
  for ($col=0; $col -lt $ncols -and $prow -lt $work.Count; $col++) {
    $best = $prow; $bestv = [math]::Abs($work[$prow][$col])
    for ($ri=$prow+1; $ri -lt $work.Count; $ri++) {
      $av = [math]::Abs($work[$ri][$col]); if ($av -gt $bestv) { $bestv=$av; $best=$ri }
    }
    if ($bestv -lt $tol) { continue }
    if ($best -ne $prow) { $tmp=$work[$prow]; $work[$prow]=$work[$best]; $work[$best]=$tmp }
    $pv = $work[$prow][$col]
    for ($j=0; $j -lt $ncols; $j++) { $work[$prow][$j] /= $pv }
    for ($ri=0; $ri -lt $work.Count; $ri++) {
      if ($ri -ne $prow) {
        $ff = $work[$ri][$col]
        if ($ff -ne 0.0) { for ($j=0; $j -lt $ncols; $j++) { $work[$ri][$j] -= $ff*$work[$prow][$j] } }
      }
    }
    $rank++; $prow++
  }
  $rank
}

# Null-space BASIS of the dictionary over SEVERAL trajectories: each column is
# centered PER TRAJECTORY (so a quantity constant along each trajectory -- even
# with a different value on each -- reads as zero), then normalized by its raw
# pooled scale, then stacked. A combination is a genuine constant of motion iff
# it is constant on EVERY trajectory: that kills spurious single-trajectory
# coincidences. Returns raw-coefficient null vectors (normalization undone).
function NullBasis($trajs, $monos, $tol) {
  $km = $monos.Count
  $rawStd = New-Object 'double[]' $km
  for ($j=0; $j -lt $km; $j++) {
    $sum=0.0; $sm2=0.0; $cnt=0
    foreach ($tr in $trajs) { foreach ($s in $tr) { $v = MonoVal $monos[$j] $s; $sum+=$v; $sm2+=$v*$v; $cnt++ } }
    $mu=$sum/$cnt; $var=$sm2/$cnt-$mu*$mu; if ($var -lt 0) { $var=0 }
    $sd=[math]::Sqrt($var); if ($sd -lt 1e-12) { $sd=1.0 }; $rawStd[$j]=$sd
  }
  $rows = New-Object System.Collections.ArrayList
  foreach ($tr in $trajs) {
    $nn = $tr.Count
    $cm = New-Object 'double[]' $km
    for ($j=0; $j -lt $km; $j++) { $sm=0.0; foreach ($s in $tr) { $sm += MonoVal $monos[$j] $s }; $cm[$j]=$sm/$nn }
    foreach ($s in $tr) {
      $rw = New-Object 'double[]' $km
      for ($j=0; $j -lt $km; $j++) { $rw[$j] = ((MonoVal $monos[$j] $s) - $cm[$j])/$rawStd[$j] }
      [void]$rows.Add($rw)
    }
  }
  $nn = $rows.Count
  $Rm = New-Object 'object[]' $nn
  for ($i=0; $i -lt $nn; $i++) { $Rm[$i] = $rows[$i] }
  $rank = 0; $prow = 0
  $pivCols = New-Object System.Collections.ArrayList
  $pivRows = New-Object System.Collections.ArrayList
  for ($col=0; $col -lt $km -and $prow -lt $nn; $col++) {
    $best = $prow; $bestv = [math]::Abs($Rm[$prow][$col])
    for ($ri=$prow+1; $ri -lt $nn; $ri++) { $av=[math]::Abs($Rm[$ri][$col]); if ($av -gt $bestv) { $bestv=$av; $best=$ri } }
    if ($bestv -lt $tol) { continue }
    if ($best -ne $prow) { $tmp=$Rm[$prow]; $Rm[$prow]=$Rm[$best]; $Rm[$best]=$tmp }
    $pv = $Rm[$prow][$col]
    for ($j=0; $j -lt $km; $j++) { $Rm[$prow][$j] = $Rm[$prow][$j]/$pv }
    for ($ri=0; $ri -lt $nn; $ri++) {
      if ($ri -ne $prow) {
        $ff = $Rm[$ri][$col]
        if ($ff -ne 0.0) { for ($j=0; $j -lt $km; $j++) { $Rm[$ri][$j] = $Rm[$ri][$j] - $ff*$Rm[$prow][$j] } }
      }
    }
    [void]$pivCols.Add($col); [void]$pivRows.Add($prow)
    $rank++; $prow++
  }
  # free columns -> null vectors; undo the rawStd normalization to raw coeffs
  $basis = @()
  for ($col=0; $col -lt $km; $col++) {
    if ($pivCols.Contains($col)) { continue }
    $vec = New-Object 'double[]' $km
    $vec[$col] = 1.0/$rawStd[$col]
    for ($pi=0; $pi -lt $pivCols.Count; $pi++) {
      $pc = $pivCols[$pi]; $prw = $pivRows[$pi]
      $vec[$pc] = -$Rm[$prw][$col]/$rawStd[$pc]
    }
    $basis += ,$vec
  }
  # leading comma guards against PS unrolling a 1-vector basis into its doubles
  ,$basis
}

# functionally independent generator count: max Jacobian rank of the null-space
# combinations over a handful of generic trajectory points
function GenCount($basis, $monos, $trajs) {
  if ($basis.Count -eq 0) { return 0 }
  $pts = @()
  foreach ($tr in $trajs) { $pts += ,($tr[[int]($tr.Count/3)]); $pts += ,($tr[[int](2*$tr.Count/3)]) }
  $bestRank = 0
  foreach ($pt in $pts) {
    $jac = @()
    foreach ($vec in $basis) { $jac += ,(CombGrad $vec $monos $pt) }
    $rk = MatRank $jac 1e-6
    if ($rk -gt $bestRank) { $bestRank = $rk }
  }
  $bestRank
}

function Probe($title, $force, $ICs, $Efn, $ExmEyFn, $LzFn, $deg) {
  $dt=0.002; $nsteps=40000; $every=100
  $trajs = @()
  foreach ($ic in $ICs) { $trajs += ,(Verlet $force $ic $dt $nsteps $every) }
  $s1 = $trajs[0]
  $Ev=@();$Dv=@();$Lv=@()
  foreach ($s in $s1) { $Ev+=(& $Efn $s); $Dv+=(& $ExmEyFn $s); $Lv+=(& $LzFn $s) }
  ""
  "==================================================================="
  "$title"
  "  detector dispersions (along one trajectory, $($s1.Count) samples):"
  "    E        : {0:e2}   (energy)" -f (Disp $Ev)
  "    E_x - E_y: {0:e2}   (candidate 2nd invariant)" -f (Disp $Dv)
  "    Lz       : {0:e2}   (angular momentum)" -f (Disp $Lv)
  $monos = Monos 4 $deg
  $basis = NullBasis $trajs $monos 2e-2
  $gens = GenCount $basis $monos $trajs
  "  discovery: degree-$deg dictionary ($($monos.Count) monomials, $($ICs.Count) trajectories,"
  "             centered per trajectory)"
  "    => nullity $($basis.Count) (invariant polynomials up to degree $deg)"
  "    => $gens functionally independent constant(s) of motion"
}

# --- (A) separable quartic, lam = 0.1 -- several ICs (varied E_x, E_y) ---
$quartF = { param($x,$y) @( -($x + 0.4*$x*$x*$x), -($y + 0.4*$y*$y*$y) ) }
$quartE   = { param($s) 0.5*($s[2]*$s[2]+$s[3]*$s[3]) + 0.5*($s[0]*$s[0]+$s[1]*$s[1]) + 0.1*($s[0]*$s[0]*$s[0]*$s[0]+$s[1]*$s[1]*$s[1]*$s[1]) }
$quartD   = { param($s) 0.5*($s[2]*$s[2]-$s[3]*$s[3]) + 0.5*($s[0]*$s[0]-$s[1]*$s[1]) + 0.1*($s[0]*$s[0]*$s[0]*$s[0]-$s[1]*$s[1]*$s[1]*$s[1]) }
$LzFn     = { param($s) $s[0]*$s[3] - $s[1]*$s[2] }
$quartICs = @( @(0.6,0.3,0.2,0.5), @(0.4,0.5,0.3,0.2), @(0.7,0.2,0.1,0.4), @(0.3,0.6,0.35,0.3), @(0.5,0.45,0.25,0.15) )
Probe "(A) SEPARABLE QUARTIC  (integrable -- expect a 2nd invariant)" $quartF $quartICs $quartE $quartD $LzFn 4

# --- (B) Henon-Heiles, lam = 1 -- several chaotic ICs (E ~ 0.12-0.15) ---
$hhF = { param($x,$y) @( -($x + 2.0*$x*$y), -($y + ($x*$x - $y*$y)) ) }
$hhE = { param($s) 0.5*($s[2]*$s[2]+$s[3]*$s[3]) + 0.5*($s[0]*$s[0]+$s[1]*$s[1]) + ($s[0]*$s[0]*$s[1] - $s[1]*$s[1]*$s[1]/3.0) }
$hhD = { param($s) 0.5*($s[2]*$s[2]-$s[3]*$s[3]) + 0.5*($s[0]*$s[0]-$s[1]*$s[1]) }
$hhICs = @( @(0.0,0.0,0.42,0.28), @(0.1,-0.1,0.40,0.25), @(0.0,0.15,0.44,0.20), @(-0.1,0.05,0.43,0.30), @(0.12,0.1,0.40,0.30) )
Probe "(B) HENON-HEILES  (chaotic -- expect ONLY energy)" $hhF $hhICs $hhE $hhD $LzFn 4

# --- (C) Duffing 1-DOF: the count is a RING -- nullity inflates with degree ---
$dufF = { param($x) -($x + $x*$x*$x) }
$dufICs = @( @(0.8,0.0), @(0.5,0.4), @(1.1,0.2), @(0.9,0.3), @(0.65,0.15) )
$dufTrajs = @()
foreach ($ic in $dufICs) { $dufTrajs += ,(Verlet1 $dufF $ic 0.002 40000 100) }
""
"==================================================================="
"(C) DUFFING x'' = -x - x^3  (1-DOF: EXACTLY one constant of motion, E)"
"    E = 1/2 p^2 + 1/2 x^2 + 1/4 x^4  (degree 4). E^2 has degree 8."
foreach ($dg in @(4, 8)) {
  $monos1 = Monos 2 $dg
  $basis1 = NullBasis $dufTrajs $monos1 5e-3
  $gens1 = GenCount $basis1 $monos1 $dufTrajs
  "  degree-$dg dictionary ($($monos1.Count) monomials):"
  "    => nullity $($basis1.Count) (invariant polynomials up to degree $dg)"
  "    => $gens1 functionally independent constant(s) of motion"
}
""
"The degree-8 nullity is 2 -- the null space holds E AND E^2 -- but the"
"Jacobian rank exposes them as ONE generator: grad(E^2) = 2 E grad(E) is"
"parallel to grad(E) everywhere. 'Nullity = number of invariants' is only"
"true below the degree where products of invariants enter the dictionary;"
"the generator count is the honest, degree-stable answer."
