# ============================================================================
# THE GENERATOR IS THE MOMENT JET OF THE PROPAGATOR (Kramers-Moyal), and the
# admissible truncation order of that jet is 1, 2, or INFINITY (Pawula).
#
# The whole deterministic arc fitted d/dt <g> = L g. For STOCHASTIC dynamics
# the generator is read off the conditional cumulants of the increments:
#     D_n(x) = lim_{tau->0} kappa_n( x(t+tau) - x(t) | x(t)=x ) / (n! tau)
# -- drift is the FIRST conditional cumulant, diffusion the SECOND: the
# Kramers-Moyal expansion literally IS the moment jet of the transition
# kernel, so the tower formers estimate the generator directly from data.
#
# Pawula's theorem is a statement about Blade's favorite parameter: if any
# D_n with n >= 3 is nonzero, ALL higher ones are -- the propagator's
# cumulant tower truncates at order 1 (deterministic), order 2 (diffusion),
# or not at all (jumps). Nothing in between exists. The test is visible in
# one number: the excess kurtosis of increments,
#     kappa_4 / kappa_2^2  ->  0 for a diffusion,   3/(rho tau) for jumps
# -- it DIVERGES as tau -> 0 for any jump process. Two processes with
# IDENTICAL drift and identical diffusion (kappa_2) separate at kappa_4.
#
# Systems (same stationary law by construction at order 2):
#   OU:    dx = -x dt + 0.5 dW                        (diffusion, jet order 2)
#   JUMP:  dx = -x dt + compound Poisson (rate 2.5,   (jet order infinity)
#          Gaussian jumps, size chosen so kappa_2 of increments matches OU)
#
# 19_kramers_moyal_pawula.blade runs the same read through Blade's formers on
# emitted center-bin increments: kappa_2/tau reproduces the diffusion both
# times; kappa_4/kappa_2^2 says 0 vs huge -- the jet order is the verdict.
# ============================================================================

$rng = New-Object System.Random(20260714)
$gHave=$false; $gVal=0.0
function Gauss() {
  if($script:gHave){ $script:gHave=$false; return $script:gVal }
  $u1=$script:rng.NextDouble(); if($u1 -lt 1e-12){ $u1=1e-12 }
  $u2=$script:rng.NextDouble()
  $r=[math]::Sqrt(-2.0*[math]::Log($u1)); $ph=2.0*[math]::PI*$u2
  $script:gVal=$r*[math]::Sin($ph); $script:gHave=$true
  $r*[math]::Cos($ph)
}

$THETA=1.0; $SIG=0.5; $DTS=0.01; $NSTEP=400000
$RATE=2.5; $JSZ=$SIG/[math]::Sqrt($RATE)   # matches kappa_2 of increments

# --- simulate: OU and jump-driven OU (same drift, same kappa_2) ---
$sqdt=[math]::Sqrt($DTS)
$ouX=New-Object 'double[]' $NSTEP
$jpX=New-Object 'double[]' $NSTEP
$xo=0.0; $xj=0.0
for($i=0;$i -lt $NSTEP;$i++){
  $xo = $xo - $THETA*$xo*$DTS + $SIG*$sqdt*(Gauss)
  $ouX[$i]=$xo
  $xj = $xj - $THETA*$xj*$DTS
  if($rng.NextDouble() -lt $RATE*$DTS){ $xj = $xj + $JSZ*(Gauss) }
  $jpX[$i]=$xj
}

function CondKM($xs, $strideK, $tauV) {
  # per-bin conditional cumulants of increments; bins over x in [-0.9, 0.9]
  $edges=@(-0.9,-0.5,-0.2,0.2,0.5,0.9)
  $out=@()
  for($b=0;$b -lt $edges.Count-1;$b++){
    $lo=$edges[$b]; $hi=$edges[$b+1]
    $n=0; $s1=0.0; $s2=0.0; $s3=0.0; $s4=0.0
    for($i=0;$i -lt $xs.Count-$strideK;$i++){
      $xv=$xs[$i]
      if($xv -ge $lo -and $xv -lt $hi){
        $dv=$xs[$i+$strideK]-$xv
        $n++; $s1+=$dv; $s2+=$dv*$dv; $s3+=$dv*$dv*$dv; $s4+=$dv*$dv*$dv*$dv
      }
    }
    if($n -lt 50){ continue }
    $m1=$s1/$n; $m2=$s2/$n; $m3=$s3/$n; $m4=$s4/$n
    $k2=$m2-$m1*$m1
    $k3=$m3-3.0*$m1*$m2+2.0*$m1*$m1*$m1
    $k4=$m4-4.0*$m1*$m3-3.0*$m2*$m2+12.0*$m1*$m1*$m2-6.0*$m1*$m1*$m1*$m1
    $xc=0.5*($lo+$hi)
    $out += ,@($xc,$n,($m1/$tauV),($k2/$tauV),($k4/($k2*$k2)))
  }
  ,$out
}

""
"==================================================================="
"STATE-RESOLVED KRAMERS-MOYAL READ (tau = $DTS): the generator from data"
"  true drift = -1.0 x ; true diffusion kappa_2/tau = $($SIG*$SIG) (both systems)"
foreach($sys in @(@("OU (diffusion)",$ouX),@("JUMP (compound Poisson)",$jpX))) {
  $tag=$sys[0]; $xs=$sys[1]
  ""
  "  $tag"
  "    bin x     n       drift k1/tau   diffusion k2/tau   excess kurt k4/k2^2"
  $tbl = CondKM $xs 1 $DTS
  foreach($rw in $tbl){
    ("    {0,5:0.##} {1,7}   {2,10:0.###}   {3,12:0.####}   {4,12:0.###}" -f $rw[0],$rw[1],$rw[2],$rw[3],$rw[4])
  }
}

""
"==================================================================="
"PAWULA DICHOTOMY: excess kurtosis of increments vs tau"
"  diffusion -> 0 (kappa_4 identically 0: the jet TRUNCATES at order 2);"
"  jumps -> 3/(rho tau): DIVERGES as tau -> 0 (the jet cannot truncate)"
"    tau      OU excess kurt     JUMP excess kurt     3/(rho tau)"
foreach($kk in @(1,2,5,10,20)){
  $tauV=$kk*$DTS
  # pooled (all x) increment cumulants
  $res=@()
  foreach($xs in @($ouX,$jpX)){
    $n=$xs.Count-$kk
    $s1=0.0;$s2=0.0;$s3=0.0;$s4=0.0
    for($i=0;$i -lt $n;$i++){ $dv=$xs[$i+$kk]-$xs[$i]; $s1+=$dv; $s2+=$dv*$dv; $s3+=$dv*$dv*$dv; $s4+=$dv*$dv*$dv*$dv }
    $m1=$s1/$n; $m2=$s2/$n; $m3=$s3/$n; $m4=$s4/$n
    $k2=$m2-$m1*$m1
    $k4=$m4-4.0*$m1*$m3-3.0*$m2*$m2+12.0*$m1*$m1*$m2-6.0*$m1*$m1*$m1*$m1
    $res += ($k4/($k2*$k2))
  }
  ("    {0,5:0.##}   {1,12:0.###}   {2,14:0.###}   {3,12:0.###}" -f $tauV, $res[0], $res[1], (3.0/($RATE*$tauV)))
}

# --- emit center-bin increments for 19_kramers_moyal_pawula.blade ---
$invc=[System.Globalization.CultureInfo]::InvariantCulture
""
"==================================================================="
"EXAMPLE 19 DATA: 400 center-bin (|x| < 0.4) increments at tau = $DTS"
foreach($sys in @(@("DO",$ouX),@("DJ",$jpX))) {
  $tag=$sys[0]; $xs=$sys[1]
  $vals=@()
  for($i=0;$i -lt $xs.Count-1;$i++){
    if([math]::Abs($xs[$i]) -lt 0.4){
      $vtxt=($xs[$i+1]-$xs[$i]).ToString("G12",$invc)
      # force a decimal point: a bare "0" parses as Int64 in Blade
      if($vtxt -notmatch '[.eE]'){ $vtxt="$vtxt.0" }
      $vals += $vtxt
      if($vals.Count -ge 400){ break }
    }
  }
  ("let {0} = [[{1}]]" -f $tag, ($vals -join ', '))
  # reference cumulants on exactly these 400
  $n=400; $s1=0.0;$s2=0.0;$s3=0.0;$s4=0.0
  foreach($vtxt in $vals){
    $dv=[double]::Parse($vtxt,$invc)
    $s1+=$dv; $s2+=$dv*$dv; $s3+=$dv*$dv*$dv; $s4+=$dv*$dv*$dv*$dv
  }
  $m1=$s1/$n; $m2=$s2/$n; $m3=$s3/$n; $m4=$s4/$n
  $k2=$m2-$m1*$m1
  $k4=$m4-4.0*$m1*$m3-3.0*$m2*$m2+12.0*$m1*$m1*$m2-6.0*$m1*$m1*$m1*$m1
  ("  {0}: k2/tau = {1:0.######}   k4/k2^2 = {2:0.######}" -f $tag, ($k2/$DTS), ($k4/($k2*$k2)))
}
