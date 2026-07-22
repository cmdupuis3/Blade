# ============================================================================
# THE RIND: can a fuzzy ball be INSIDE another one?
#
# Two balls at known density (R = m^(1/3)), mass ratio 1000. The BIG ball
# (M = 1000, R_B = 10, sharp) rests at the origin; the SMALL one approaches
# with a mass known only to two orders of magnitude (7-point log-scale jet,
# m in [0.05, 20], so R_s in [0.37, 2.71]). The uncertainty band of the
# contact surface -- the RIND, width ~2.3 -- exceeds the small ball's own
# likely diameter: marginally, the small ball "could be completely enveloped"
# inside the big one. What does that mean? Pick the observation time T in the
# middle of the fuzzy contact band and split the question three ways:
#
# 1. INSIDE THE ACTUAL BALL: never. Per realization the gap
#        g = x_s - x_B - R_B - R_s >= 0
#    always (hard spheres bounce). The JOINT ensemble assigns probability
#    EXACTLY ZERO to overlap. But the PRODUCT of the marginals -- forget the
#    correlation between where the ball is and how big it is -- assigns a
#    large probability to overlap, including COMPLETE envelopment (pairings
#    where the whole small body sits deeper than its own diameter inside the
#    big one). Impenetrability is INVISIBLE in the marginals and stored
#    entirely in the cross-cumulants: the exclusion principle is a
#    correlation law, exactly as conservation was in example 06. Shuffling
#    the ensemble (decorrelating position from size) resurrects the
#    forbidden region.
#
# 2. INSIDE THE MEAN BALL: genuinely, often. The small ball's center spends
#    real probability mass inside the MEAN exclusion surface R_B + E[R_s].
#    "It is inside, in some sense" resolves to: inside the mean ball with
#    probability ~1/2 at contact, inside the actual ball with probability 0.
#
# 3. THE SOFT WALL. The ensemble of turning points x_turn = R_B + R_s smears
#    the hard wall into a diffuse edge: the effective exclusion profile
#    P(wall > x) is a smooth sigmoid of width ~the rind. An ensemble-averaged
#    hard sphere IS a soft potential -- the classical shadow of the nuclear
#    optical model, where a sharp nucleus plus surface uncertainty gives the
#    Woods-Saxon diffuseness. Every realization is hard; the DESCRIPTION is
#    soft.
#
# The law itself is a realizability statement in the moment language: the
# JOINT gap tower must be a Stieltjes moment sequence (a distribution
# supported on [0, infinity), boundary-saturated at contact); the shuffled
# tower need not be. (Low-order Hankel tests may or may not catch a given
# violation -- the sharp statement is the support constraint; we report the
# shifted-Hankel values honestly either way.)
#
# Companion: 25_envelopment_rind.blade computes the joint and shuffled gap
# towers, the overlap/envelopment counts, the inside-the-mean probability,
# and the Stieltjes data in-language from the embedded ensemble.
# ============================================================================

$invc=[System.Globalization.CultureInfo]::InvariantCulture
function Fmt($x){ $t=$x.ToString("G12",$invc); if($t -notmatch '[.eE]'){ $t="$t.0" }; $t }

# ---------------- the ensemble ---------------------------------------------
$MB=1000.0; $RB=[math]::Pow($MB,1.0/3.0)   # R_B = 10 exactly
$mset=@(0.05,0.15,0.4,1.0,2.7,7.4,20.0)    # small mass: 2.6 decades of fuzz
$X0=15.0; $V0=-1.0                          # small starts right, moving left

# exact elastic bounce off the (initially resting) big ball
function StateAtT($m, $Tq) {
  $Rs=[math]::Pow($m,1.0/3.0)
  $tc=($X0-$RB-$Rs)/(-$V0)                 # contact time
  if($Tq -lt $tc){
    ,@($Rs,$tc,($X0+$V0*$Tq),0.0)          # still approaching; big at rest
  } else {
    $vs=(($m-$MB)/($m+$MB))*$V0            # small bounces
    $vb=(2.0*$m/($m+$MB))*$V0              # big creeps
    $xs=($RB+$Rs)+$vs*($Tq-$tc)
    $xb=$vb*($Tq-$tc)
    ,@($Rs,$tc,$xs,$xb)
  }
}

# choose T mid-band: contact times span [5-2.71, 5-0.37] = [2.29, 4.63]
$TOBS=3.8
$RSv=@();$TCv=@();$XSv=@();$XBv=@();$Gv=@()
foreach($m in $mset){
  $st=StateAtT $m $TOBS
  $RSv+=$st[0]; $TCv+=$st[1]; $XSv+=$st[2]; $XBv+=$st[3]
  $Gv += ($st[2]-$st[3]-$RB-$st[0])
}
$nE=$mset.Count

"==================================================================="
"THE ENSEMBLE at T = $TOBS  (contact band t_c in [$([math]::Round(($TCv|Measure-Object -Minimum).Minimum,3)), $([math]::Round(($TCv|Measure-Object -Maximum).Maximum,3))])"
"    m        R_s      t_c      x_s(T)     gap g(T)"
for($i=0;$i -lt $nE;$i++){
  ("  {0,6:0.###}  {1,7:0.####}  {2,7:0.####}  {3,9:0.#####}  {4,9:0.#####}" -f $mset[$i],$RSv[$i],$TCv[$i],$XSv[$i],$Gv[$i])
}
$gmin=($Gv|Measure-Object -Minimum).Minimum
("  min joint gap = {0:e3}  (>= 0: the JOINT ensemble NEVER overlaps)" -f $gmin)

# ---------------- (1) joint vs product-of-marginals overlap ----------------
$nOver=0; $nEnv=0; $worstDepth=0.0; $worstFrac=0.0
$prodG=@()   # product-ensemble gaps (49 ordered pairs, position-i with size-j)
for($i=0;$i -lt $nE;$i++){ for($j=0;$j -lt $nE;$j++){
  $pgap=$XSv[$i]-$XBv[$i]-$RB-$RSv[$j]   # NOT $gp: case-collides with the array!
  $prodG+=$pgap
  if($pgap -lt 0){
    $nOver++
    $depth=-$pgap
    $frac=$depth/(2.0*$RSv[$j])            # penetration in own-diameter units
    if($depth -gt $worstDepth){ $worstDepth=$depth }
    if($frac -gt $worstFrac){ $worstFrac=$frac }
    if($depth -gt 2.0*$RSv[$j]){ $nEnv++ } # whole body past the surface
  }
}}
""
"==================================================================="
"(1) EXCLUSION IS A CORRELATION LAW"
("  joint ensemble        : P(overlap) = 0/{0} = 0   (exact)" -f $nE)
("  product of marginals  : P(overlap) = {0}/{1} = {2:0.###}" -f $nOver,($nE*$nE),($nOver/($nE*$nE)))
("  complete ENVELOPMENT (deeper than own diameter): {0}/{1} pairs; worst depth {2:0.###} = {3:0.##} diameters" -f $nEnv,($nE*$nE),$worstDepth,$worstFrac)
"  (forgetting the position-size correlation resurrects the forbidden"
"   region; the impenetrability law lives in the cross-cumulants, not in"
"   any marginal -- the exclusion analogue of example 06's conservation-"
"   as-correlation)"

# ---------------- (2) inside the MEAN ball ----------------------------------
$RsMean=0.0; foreach($r in $RSv){ $RsMean+=$r }; $RsMean/=$nE
$wallMean=$RB+$RsMean
$nInMean=0
for($i=0;$i -lt $nE;$i++){ if($XSv[$i] -lt $wallMean){ $nInMean++ } }
""
"==================================================================="
"(2) INSIDE, IN WHAT SENSE?  (the rind = the band of POSSIBLE surfaces,"
"    [R_B + min R_s, R_B + max R_s] -- built from the small ball's OWN"
"    size uncertainty, since the contact surface is at R_B + R_s)"
$RsMean=0.0; foreach($r in $RSv){ $RsMean+=$r }; $RsMean/=$nE
$wallMean=$RB+$RsMean
$rindOuter=$RB+(($RSv|Measure-Object -Maximum).Maximum)
$nInMean=0; $nInRind=0
for($i=0;$i -lt $nE;$i++){
  if($XSv[$i] -lt $wallMean){ $nInMean++ }
  if(($XSv[$i]+$RSv[$i]) -lt $rindOuter){ $nInRind++ }
}
("  mean exclusion surface R_B + E[R_s] = {0:0.####};  outer rind edge = {1:0.####}" -f $wallMean,$rindOuter)
("  P(center inside the MEAN ball)               = {0}/{1} = {2:0.###}" -f $nInMean,$nE,($nInMean/$nE))
("  P(WHOLE BODY below the outer possible surface) = {0}/{1} = {2:0.###}   (enveloped by the rind)" -f $nInRind,$nE,($nInRind/$nE))
("  P(center inside the ACTUAL ball)             = 0   (margin R_s per realization)")
"  ('inside, in some sense' = inside the mean ball and enveloped by its"
"   own rind of possible surfaces -- never inside the actual ball)"

# ---------------- (3) the soft wall -----------------------------------------
""
"==================================================================="
"(3) THE SOFT WALL (ensemble-averaged hard sphere = diffuse edge)"
"    x        P(wall beyond x) = exclusion profile"
foreach($xq in @(10.2,10.5,10.8,11.2,11.6,12.0,12.4,12.8)){
  $pw=0
  for($i=0;$i -lt $nE;$i++){ if(($RB+$RSv[$i]) -gt $xq){ $pw++ } }
  ("  {0,5:0.#}       {1:0.###}" -f $xq,($pw/$nE))
}
"  (a hard wall convolved with the rind: the classical shadow of the"
"   optical-model / Woods-Saxon surface diffuseness -- every realization"
"   is hard, the ensemble DESCRIPTION is soft)"

# ---------------- Stieltjes data --------------------------------------------
function Moms($vals){
  $n=$vals.Count
  $m1=0.0;$m2=0.0;$m3=0.0
  foreach($v in $vals){ $m1+=$v; $m2+=$v*$v; $m3+=$v*$v*$v }
  ,@(($m1/$n),($m2/$n),($m3/$n))
}
$mj=Moms $Gv; $mp=Moms $prodG
$sj=$mj[0]*$mj[2]-$mj[1]*$mj[1]
$sp=$mp[0]*$mp[2]-$mp[1]*$mp[1]
""
"==================================================================="
"STIELTJES DATA (support in [0,inf) is the impenetrability law itself)"
("  joint   gap moments m1..m3 = {0:0.####}, {1:0.####}, {2:0.####}   shifted-Hankel m1 m3 - m2^2 = {3:0.####}" -f $mj[0],$mj[1],$mj[2],$sj)
("  product gap moments m1..m3 = {0:0.####}, {1:0.####}, {2:0.####}   shifted-Hankel m1 m3 - m2^2 = {3:0.####}" -f $mp[0],$mp[1],$mp[2],$sp)
("  joint min gap = {0:e3} (support edge SATURATED at contact);" -f $gmin)
("  product min gap = {0:0.####} < 0 (support constraint violated: not a" -f (($prodG|Measure-Object -Minimum).Minimum))
"   legal gap law; low-order Hankels may pass -- the sharp test is support)"

# ---------------- emit example 25 data --------------------------------------
""
"==================================================================="
"EXAMPLE 25 DATA"
("let XS = [{0}]" -f (($XSv | ForEach-Object { Fmt $_ }) -join ', '))
("let XB = [{0}]" -f (($XBv | ForEach-Object { Fmt $_ }) -join ', '))
("let RS = [{0}]" -f (($RSv | ForEach-Object { Fmt $_ }) -join ', '))
# product ensemble as flat 49-columns (position-realization i, size-realization j)
$XPl=@();$BPl=@();$RPl=@()
for($i=0;$i -lt $nE;$i++){ for($j=0;$j -lt $nE;$j++){ $XPl+=(Fmt $XSv[$i]); $BPl+=(Fmt $XBv[$i]); $RPl+=(Fmt $RSv[$j]) }}
("let XP = [{0}]" -f ($XPl -join ', '))
("let BP = [{0}]" -f ($BPl -join ', '))
("let RP = [{0}]" -f ($RPl -join ', '))
("  refs: P_joint=0  P_prod={0}  envel={1}  worstDepth={2}  P_inMean={3}  wallMean={4}" -f (Fmt ($nOver/49.0)),(Fmt ($nEnv/49.0)),(Fmt $worstDepth),(Fmt ($nInMean/7.0)),(Fmt $wallMean))
("  refs: joint m1={0} m2={1} m3={2} sH={3}" -f (Fmt $mj[0]),(Fmt $mj[1]),(Fmt $mj[2]),(Fmt $sj))
("  refs: prod  m1={0} m2={1} m3={2} sH={3}" -f (Fmt $mp[0]),(Fmt $mp[1]),(Fmt $mp[2]),(Fmt $sp))
