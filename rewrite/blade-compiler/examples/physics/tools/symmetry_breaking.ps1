# ============================================================================
# SYMMETRY-BREAKING METER.
#
# Perturb Kepler: V(r) = -1/r - beta/r^2. The force stays central and time-
# independent, so ENERGY and ANGULAR MOMENTUM remain exactly conserved, but the
# hidden symmetry breaks -- the orbit precesses and the Laplace-Runge-Lenz
# vector no longer stands still. The invariant detector becomes a METER: fuzz
# the orbit angle and read the DISPERSION of each candidate. A conserved
# quantity has ~0 dispersion; a broken one spreads, and the size of the spread
# measures how badly the symmetry is broken.
#
# Exact solution (L=1, e=0.5): Ltilde^2 = 1 - 2 beta, k = Ltilde, p = Ltilde^2,
#   r = p/(1 + e cos(k phi)), vr = (e/Ltilde) sin(k phi), vphi = 1/r,
#   vx = vr cos phi - vphi sin phi, vy = vr sin phi + vphi cos phi.
# Sweep beta (including 0): watch A_x, A_y spread in proportion to beta while
# E_pert and Lz stay pinned, and see the exact Kepler invariants return at
# beta -> 0.
# ============================================================================
function Disp($vals) {
  $n=$vals.Count; $m=0.0; foreach($v in $vals){$m+=$v}; $m/=$n
  $s=0.0; foreach($v in $vals){ $dd=$v-$m; $s+=$dd*$dd }; $s/=$n
  [math]::Sqrt($s)/([math]::Abs($m)+1e-9)
}
function F5($x){ ('{0:e2}' -f $x) }

$L=1.0; $e=0.5
# fixed fuzzing window on the orbit angle (uniform grid; dispersion measures
# variation across the window, independent of beta)
$phis = @(); for($i=0;$i -lt 25;$i++){ $phis += (1.1 + 0.8*($i/24.0)) }

"Kepler + (-beta/r^2), fuzzing orbit angle phi over [1.1, 1.9]"
"{0,-7} {1,-13} {2,-11} {3,-11} {4,-11} {5,-11} {6}" -f "beta","precess/orb","disp(Epert)","disp(Lz)","disp(A_x)","disp(A_y)","disp(Ekep)"
foreach ($beta in @(0.0, 0.005, 0.01, 0.02, 0.05, 0.10, 0.15)) {
  $Lt=[math]::Sqrt($L*$L - 2.0*$beta); $k=$Lt/$L; $p=$Lt*$Lt
  $Ep=@();$Lz=@();$Ax=@();$Ay=@();$Ek=@()
  foreach($phi in $phis){
    $r=$p/(1.0+$e*[math]::Cos($k*$phi))
    $vr=($e/$Lt)*[math]::Sin($k*$phi); $vphi=$L/$r
    $cs=[math]::Cos($phi); $sn=[math]::Sin($phi)
    $x=$r*$cs; $y=$r*$sn; $vx=$vr*$cs-$vphi*$sn; $vy=$vr*$sn+$vphi*$cs
    $Ep+=(0.5*($vx*$vx+$vy*$vy)-1.0/$r-$beta/($r*$r))
    $Lz+=($x*$vy-$y*$vx)
    $lz=$x*$vy-$y*$vx
    $Ax+=($vy*$lz-$x/$r); $Ay+=(-$vx*$lz-$y/$r)
    $Ek+=(0.5*($vx*$vx+$vy*$vy)-1.0/$r)
  }
  $prec = 2.0*[math]::PI*(1.0/$k-1.0)
  "{0,-7} {1,-13} {2,-11} {3,-11} {4,-11} {5,-11} {6}" -f (('{0:f3}' -f $beta)),(('{0:f4}' -f $prec)),(F5 (Disp $Ep)),(F5 (Disp $Lz)),(F5 (Disp $Ax)),(F5 (Disp $Ay)),(F5 (Disp $Ek))
}
""
"Reading: E_pert and Lz stay pinned (~1e-15) at every beta -- energy and angular"
"momentum are unbroken. A_x, A_y (the Laplace-Runge-Lenz vector) spread in"
"proportion to beta -- the hidden symmetry breaks smoothly, and at beta = 0 the"
"exact Kepler invariants return (A dispersion collapses to ~0). The detector"
"reads out not just WHICH symmetry broke but HOW MUCH."
