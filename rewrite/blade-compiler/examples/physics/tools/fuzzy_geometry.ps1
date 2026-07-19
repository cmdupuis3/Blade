# ============================================================================
# FUZZY GEOMETRY: when the mass jet leaks into WHERE and WHEN.
#
# The collision arc (06-08) made the MASSES moment jets but kept the geometry
# sharp. If the density is known, that is inconsistent: a fuzzy mass IS a
# fuzzy volume, R = (3m / 4 pi rho)^(1/3). Work in units where
# (3/(4 pi rho))^(1/3) = 1, so R = m^(1/3). Ball 1 (radius R1, speed u1 = 1)
# starts with its center at 0; ball 2 (radius R2) rests with center at d.
# Contact when the gap closes:
#     t* = (d - R1 - R2)/u1          (WHEN -- fuzzy through BOTH radii)
#     X_c = d - R2                   (WHERE -- fuzzy through the TARGET only:
#                                     the projectile's size never enters)
# Four findings, in order of depth:
#
# 1. GEOMETRIC BIAS. R = m^(1/3) is concave, so mass variance pulls E[R]
#    BELOW R(E[m]): the mean contact point sits FARTHER from the target
#    center than the naive calculation says, by a sign-definite amount
#    proportional to Var(m). Volume uncertainty shifts geometry means.
#
# 2. THE CORRELATION CREDIT. After contact, x2(T) = d + v2 (T - t*). A
#    heavier target BOTH recoils slower (v2 down) AND sticks out closer
#    (t* down): the timing and velocity channels are driven by the same
#    jet and partially CANCEL in x2. A naive error budget that adds the
#    two channel variances OVERSTATES Var(x2(T)) by ~30%; the joint jet
#    collects the credit automatically. Geometry and dynamics are not
#    independent error sources once density ties them together.
#
# 3. NOETHER IMMUNITY. The center of mass moves uniformly through the
#    whole event no matter when or where the contact happened, so the COM
#    tower -- and the boost invariant G = m1 x1 + m2 x2 - P t of example
#    16 -- are EXACTLY degenerate under geometric fuzz. The conservation
#    structure is blind to the entire rabbit hole: uncertainty about the
#    event's geometry localizes, like everything else, away from the
#    invariants.
#
# 4. TEMPORAL-ORDERING AMBIGUITY. Observe at a time T INSIDE the fuzzy
#    contact band and "has the collision happened yet?" has no definite
#    answer: x2(T) is a mixture of "still at d" and "already recoiling".
#    The tower flags it as a skew/kurtosis SPIKE in T -- the bifurcation
#    detector of example 06, now firing along the TIME axis. (Outlook:
#    with several fuzzy-sized balls, the ORDER of collisions becomes a
#    distribution -- event ordering itself carried by the tower.)
#
# Companions: 20_fuzzy_geometry_contact.blade (the univariate dist_map
# chain: cube root via m^(1/3), bias, timing jet, correlation
# credit in-language) and 21_collision_time_ambiguity.blade (both masses
# fuzzy: COM invisibility + the skew spike through the formers).
# ============================================================================

$invc=[System.Globalization.CultureInfo]::InvariantCulture
$D=10.0; $U1=1.0

function Cum($vals) {
  $n=$vals.Count
  $s1=0.0;$s2=0.0;$s3=0.0;$s4=0.0
  foreach($v in $vals){ $s1+=$v; $s2+=$v*$v; $s3+=$v*$v*$v; $s4+=$v*$v*$v*$v }
  $m1=$s1/$n; $m2=$s2/$n; $m3=$s3/$n; $m4=$s4/$n
  $k2=$m2-$m1*$m1
  $k3=$m3-3.0*$m1*$m2+2.0*$m1*$m1*$m1
  $k4=$m4-4.0*$m1*$m3-3.0*$m2*$m2+12.0*$m1*$m1*$m2-6.0*$m1*$m1*$m1*$m1
  ,@($m1,$k2,$k3,$k4)
}
function Fmt($x){ $t=$x.ToString("G12",$invc); if($t -notmatch '[.eE]'){ $t="$t.0" }; $t }

# ---------------- (1)+(2): m1 SHARP = 1, m2 fuzzy (7-point, sd = 0.2) ------
$m2pts=@(); foreach($cc in @(-1.5,-1.0,-0.5,0.0,0.5,1.0,1.5)){ $m2pts += (1.0+0.2*$cc) }
$Tobs=12.0

$Rv=@();$Xcv=@();$Tsv=@();$V2v=@();$X2v=@()
foreach($m2 in $m2pts){
  $R2=[math]::Pow($m2,1.0/3.0)
  $ts=($D-1.0-$R2)/$U1
  $v2=2.0*$U1/(1.0+$m2)          # m1 = 1
  $Rv+=$R2; $Xcv+=($D-$R2); $Tsv+=$ts; $V2v+=$v2
  $X2v+=($D+$v2*($Tobs-$ts))
}
$kR=Cum $Rv; $kXc=Cum $Xcv; $kTs=Cum $Tsv; $kV2=Cum $V2v; $kX2=Cum $X2v

"==================================================================="
"(1) GEOMETRIC BIAS + TIMING JET  (m1 = 1 sharp, m2 ~ 1 +/- 0.2, d = $D)"
("  R2:  mean = {0}   (naive (E m2)^(1/3) = 1: bias = {1:e3};" -f (Fmt $kR[0]), ($kR[0]-1.0))
("       leading-order prediction -Var(m)/9 = {0:e3} -- concave, sign-definite)" -f (-0.04/9.0))
("  X_c: mean = {0}   var = {1:e3}   (contact point: TARGET size only)" -f (Fmt $kXc[0]), $kXc[1])
("  t*:  mean = {0}   var = {1:e3}  skew k3 = {2:e3}" -f (Fmt $kTs[0]), $kTs[1], $kTs[2])
""
"(2) CORRELATION CREDIT at T = $Tobs :  x2(T) = d + v2 (T - t*)"
$naive=($Tobs-$kTs[0])*($Tobs-$kTs[0])*$kV2[1] + $kV2[0]*$kV2[0]*$kTs[1]
("  Var(v2) = {0:e4}   Var(t*) = {1:e4}" -f $kV2[1], $kTs[1])
("  naive channel sum = {0:e4}   TRUE Var(x2(T)) = {1:e4}   credit = {2:0.#}%" -f $naive, $kX2[1], (100.0*(1.0-$kX2[1]/$naive)))
"  (same jet drives both channels: heavier -> slower AND earlier; the"
"   anti-correlation cancels variance a naive budget double-counts)"
"  exact reference cumulants for example 20:"
("    Xc  k1 = {0}  k2 = {1}" -f (Fmt $kXc[0]), (Fmt $kXc[1]))
("    ts  k1 = {0}  k2 = {1}" -f (Fmt $kTs[0]), (Fmt $kTs[1]))
("    v2  k1 = {0}  k2 = {1}" -f (Fmt $kV2[0]), (Fmt $kV2[1]))
("    x2T k1 = {0}  k2 = {1}  k3 = {2}" -f (Fmt $kX2[0]), (Fmt $kX2[1]), (Fmt $kX2[2]))
("  m2 ensemble points: [{0}]" -f (($m2pts | ForEach-Object { Fmt $_ }) -join ', '))

# ---------------- (3)+(4): BOTH masses fuzzy (5x5 grid, sd = 0.14 each) ----
$mgrid=@(); foreach($cc in @(-1.0,-0.5,0.0,0.5,1.0)){ $mgrid += (1.0+0.2*$cc) }
$M1c=@();$M2c=@();$TSc=@()
foreach($mOne in $mgrid){ foreach($mTwo in $mgrid){
  $M1c+=$mOne; $M2c+=$mTwo
  $TSc+=(($D-[math]::Pow($mOne,1.0/3.0)-[math]::Pow($mTwo,1.0/3.0))/$U1)
}}
$N25=$M1c.Count
$tsMin=($TSc | Measure-Object -Minimum).Minimum
$tsMax=($TSc | Measure-Object -Maximum).Maximum

function X2At($Tq) {
  $out=@()
  for($i=0;$i -lt $N25;$i++){
    $mOne=$M1c[$i]; $mTwo=$M2c[$i]; $ts=$TSc[$i]
    if($Tq -lt $ts){ $out += $D }
    else { $v2=2.0*$mOne*$U1/($mOne+$mTwo); $out += ($D+$v2*($Tq-$ts)) }
  }
  ,$out
}
function X1At($Tq) {
  $out=@()
  for($i=0;$i -lt $N25;$i++){
    $mOne=$M1c[$i]; $mTwo=$M2c[$i]; $ts=$TSc[$i]
    if($Tq -lt $ts){ $out += ($U1*$Tq) }
    else { $v1=($mOne-$mTwo)*$U1/($mOne+$mTwo); $out += ($U1*$ts+$v1*($Tq-$ts)) }
  }
  ,$out
}

""
"==================================================================="
"(3) NOETHER IMMUNITY  (both masses fuzzy, 5x5 grid; t* in [$([math]::Round($tsMin,4)), $([math]::Round($tsMax,4))])"
$TL=12.0
$x1L=X1At $TL; $x2L=X2At $TL
$gdev=@(); $xcmDev=@()
for($i=0;$i -lt $N25;$i++){
  $mOne=$M1c[$i]; $mTwo=$M2c[$i]
  # G(T) - G(0) with P = m1 u1 (ball 2 initially at rest)
  $gdev += (($mOne*$x1L[$i]+$mTwo*$x2L[$i]-$mOne*$U1*$TL) - ($mTwo*$D))
  # X_cm(T) minus the NO-COLLISION prediction (m1 u1 T + m2 d)/M
  $xcmDev += ((($mOne*$x1L[$i]+$mTwo*$x2L[$i])/($mOne+$mTwo)) - (($mOne*$U1*$TL+$mTwo*$D)/($mOne+$mTwo)))
}
$kG=Cum $gdev; $kXm=Cum $xcmDev
("  G(T)-G(0):            mean = {0:e3}   var = {1:e3}   (machine zero)" -f $kG[0], $kG[1])
("  X_cm(T) - free-flight: mean = {0:e3}   var = {1:e3}   (machine zero)" -f $kXm[0], $kXm[1])
"  The COM tower cannot tell that a collision happened AT ALL -- let alone"
"  where or when. The geometric fuzz lives entirely in the non-invariant"
"  coordinates; the boost law of example 16 survives the rabbit hole."

""
"==================================================================="
"(4) TEMPORAL-ORDERING AMBIGUITY: sweep T through the contact band"
"    T      frac collided   var(x2)      skew(x2)    excess kurt"
foreach($Tq in @(7.6,7.8,7.9,7.95,8.0,8.05,8.1,8.2,8.4,9.0,12.0)){
  $xs=X2At $Tq
  $nc=0; for($i=0;$i -lt $N25;$i++){ if($TSc[$i] -le $Tq){ $nc++ } }
  $kk=Cum $xs
  $sk=0.0; $xk=0.0
  if($kk[1] -gt 1e-24){ $sk=$kk[2]/[math]::Pow($kk[1],1.5); $xk=$kk[3]/($kk[1]*$kk[1]) }
  ("  {0,5:0.##}    {1,6:0.##}      {2,10:e2}   {3,9:0.###}   {4,9:0.###}" -f $Tq, ($nc/$N25), $kk[1], $sk, $xk)
}
"  ('has it happened yet' is a fuzzy proposition exactly inside the band:"
"   the skew/kurtosis spike is the example-06 bifurcation detector firing"
"   along the TIME axis. With several fuzzy-sized balls the ORDER of"
"   collisions becomes a distribution -- event ordering as a tower.)"

# ---------------- emit example 21 data ------------------------------------
""
"==================================================================="
"EXAMPLE 21 DATA (both fuzzy; T_early = 7.5, T_mid = 8.0, T_late = 12)"
$x2E=X2At 7.5; $x2M=X2At 8.0
("let M1 = [{0}]" -f (($M1c | ForEach-Object { Fmt $_ }) -join ', '))
("let M2 = [{0}]" -f (($M2c | ForEach-Object { Fmt $_ }) -join ', '))
("let X1L = [{0}]" -f (($x1L | ForEach-Object { Fmt $_ }) -join ', '))
("let X2L = [{0}]" -f (($x2L | ForEach-Object { Fmt $_ }) -join ', '))
("let X2E = [{0}]" -f (($x2E | ForEach-Object { Fmt $_ }) -join ', '))
("let X2M = [{0}]" -f (($x2M | ForEach-Object { Fmt $_ }) -join ', '))
$kE=Cum $x2E; $kM=Cum $x2M; $kL=Cum $x2L
("  x2(7.5): var = {0:e3}  (degenerate at d: NOTHING has happened yet)" -f $kE[1])
("  x2(8.0): var = {0:e4}  skew = {1:0.####}   (the mixture: AMBIGUOUS)" -f $kM[1], ($kM[2]/[math]::Pow($kM[1],1.5)))
("  x2(12):  var = {0:e4}  skew = {1:0.####}   (smooth regime: COLLIDED)" -f $kL[1], ($kL[2]/[math]::Pow($kL[1],1.5)))
("  Xcm dev var = {0:e3}   Gdev var = {1:e3}" -f $kXm[1], $kG[1])
