# ============================================================================
# BELL'S THEOREM FOR TEMPORAL ORDER (ZCPB-style), both sides of the divide.
#
# Zych-Costa-Pikovski-Brukner (Nat. Commun. 10, 3772, 2019) showed that a
# mass configuration can control the ORDER of two operations, and that a
# coherent superposition of orders produces correlations no classical
# (even probabilistic, even dynamical) definite-order model can match.
# Our fuzzy billiards are the CLASSICAL side of that construction: a fuzzy
# mass controls the order of two collision events -- a convex MIXTURE of
# orders, weight eta = P(E1 before E2) read straight off the race tower.
#
# (A) THE TIE SWEEP. Slide ball C's start position so the two first-contact
#     time dispersions go from disjoint to exact overlap: eta runs 1 -> 1/2.
#     At the tie: order entropy is MAXIMAL, the causal-inequality margins
#     are MAXIMAL (min(eta,1-eta): the polytope CENTER -- maximum ignorance
#     is maximum causal safety, not a violation), and the downstream
#     branch-mixture variance is maximal (the causal graph bifurcates).
#     Ontic ties (t1 = t2 exactly, genuine triple collisions where the
#     DYNAMICS is singular) live on a measure-zero surface: demonstrated on
#     a symmetric grid that strikes it 25/125 times; the asymmetric sweep
#     never does. "Looks tied" is certifiable; "is tied" is not.
#
# (B) THE WITNESS, BOTH SIDES. Take the SAME order statistics eta and wire
#     them into the ZCPB pair construction: each of two wings holds a qubit
#     |0>, operations U_A = sigma_z then U_B = H applied in the order set by
#     a SHARED order variable (our billiard race). Order AB gives |+>, order
#     BA gives |-> (orthogonal): the wings' joint state is
#       classical mixture:  rho = eta |++><++| + (1-eta) |--><--|
#       coherent order:     |Psi> = sqrt(eta)|++> + sqrt(1-eta)|-->
#     Same order marginals, same eta. CHSH maximum (Horodecki criterion,
#     S = 2 sqrt(u1+u2), u_i = top eigenvalues of T^T T):
#       classical: S = 2 EXACTLY for every eta  (separable -- the bound)
#       coherent:  S = 2 sqrt(1 + 4 eta(1-eta)) -> 2 sqrt2 at the TIE
#     The tie point is where the divide is WIDEST: classically the safest,
#     quantum-coherently the maximally order-entangled. Warm-up: the local
#     switch-interference witness D = p(+)-p(-) (control in the +/- basis)
#     is 0 for every mixture and -1 for the coherent switch with
#     anticommuting operations -- but note each WING of |Psi> is locally
#     mixed (D_local = 0): like ZCPB, the nonclassicality lives ONLY in the
#     correlations, which is why it takes a Bell test to see it.
#
# Companion: 23_bell_for_temporal_order.blade computes the classical side
# from embedded race data (eta, order entropy, downstream branch mixture)
# and the closed-form witness values for both sides from the SAME eta.
# ============================================================================

$invc=[System.Globalization.CultureInfo]::InvariantCulture
function Fmt($x){ $t=$x.ToString("G12",$invc); if($t -notmatch '[.eE]'){ $t="$t.0" }; $t }

# ---------------- (A) the billiard race: analytic first-contact times ------
# A: x=0, v=+1, m_A in 1+0.2c ; B: x=7 at rest, m_B in 0.4+0.05c ;
# C: x=xC, v=-1.5, m_C in 1+0.2c    (radii R = m^(1/3))
$gridA=@(); foreach($cc in @(-1.0,-0.5,0.0,0.5,1.0)){ $gridA += (1.0+0.2*$cc) }
$gridB=@(); foreach($cc in @(-1.0,-0.5,0.0,0.5,1.0)){ $gridB += (0.4+0.05*$cc) }
$gridC=$gridA

function RaceStats($xC) {
  # returns eta, tie count, downstream vB mixture stats, and per-real columns
  $ind=@(); $vB=@(); $t1s=@(); $t2s=@(); $ties=0
  foreach($mA in $gridA){ foreach($mB in $gridB){ foreach($mC in $gridC){
    $RA=[math]::Pow($mA,1.0/3.0); $RB=[math]::Pow($mB,1.0/3.0); $RC=[math]::Pow($mC,1.0/3.0)
    $t1=(7.0-$RA-$RB)/1.0                # A-B first contact
    $t2=($xC-7.0-$RB-$RC)/1.5            # C-B first contact
    $t1s+=$t1; $t2s+=$t2
    if([math]::Abs($t1-$t2) -lt 1e-12){ $ties++ }
    if($t1 -lt $t2){
      $ind += 1.0
      $vB += (2.0*$mA*1.0/($mA+$mB))     # B recoils right
    } else {
      $ind += 0.0
      $vB += (2.0*$mC*(-1.5)/($mC+$mB))  # B kicked left
    }
  }}}
  $n=$ind.Count
  $eta=0.0; foreach($v in $ind){ $eta+=$v }; $eta/=$n
  $m1=0.0; foreach($v in $vB){ $m1+=$v }; $m1/=$n
  $m2=0.0; foreach($v in $vB){ $m2+=($v-$m1)*($v-$m1) }; $m2/=$n
  ,@($eta,$ties,$m1,$m2,$ind,$vB,$t1s,$t2s)
}

"==================================================================="
"(A) THE TIE SWEEP  (slide C's start xC; 125-realization grid each)"
"    xC     eta = P(AB first)   entropy(bits)   margin min(eta,1-eta)   Var(vB) downstream"
$sweep=@(17.2,17.0,16.85,16.75,16.65,16.55,16.45,16.3,16.1)
$tieRow=$null; $tieDist=99.0
foreach($xC in $sweep){
  $rs=RaceStats $xC
  $eta=$rs[0]
  $hh=0.0
  if($eta -gt 1e-12 -and $eta -lt 1.0-1e-12){ $hh=-($eta*[math]::Log($eta)+(1.0-$eta)*[math]::Log(1.0-$eta))/[math]::Log(2.0) }
  $mg=[math]::Min($eta,1.0-$eta)
  ("  {0,6:0.##}      {1,6:0.###}            {2,6:0.###}          {3,6:0.###}              {4,8:0.####}" -f $xC,$eta,$hh,$mg,$rs[3])
  $dd=[math]::Abs($eta-0.5)
  if($dd -lt $tieDist){ $tieDist=$dd; $tieRow=@($xC,$rs) }
}
"  (entropy and margins PEAK at the tie: maximum ignorance about the order"
"   is the CENTER of the classical polytope -- maximally far from any"
"   causal-inequality violation. What fails at eta=1/2 is any definite"
"   causal NARRATIVE -- wrong half the time -- not causality.)"

# ontic-tie surface: symmetric configuration strikes it
$tiesSym=0
foreach($mA in $gridA){ foreach($mB in $gridB){ foreach($mC in $gridC){
  $RA=[math]::Pow($mA,1.0/3.0); $RB=[math]::Pow($mB,1.0/3.0); $RC=[math]::Pow($mC,1.0/3.0)
  $t1=(7.0-$RA-$RB)/1.0
  $t2=(14.0-7.0-$RB-$RC)/1.0    # symmetric: v_C = -1, mirror distance
  if([math]::Abs($t1-$t2) -lt 1e-12){ $tiesSym++ }
}}}
""
("  ONTIC ties (t1 = t2 exactly = triple collision, dynamics singular):")
("    asymmetric sweep: 0/125 at every xC;  symmetric config: {0}/125" -f $tiesSym)
"    (the m_A = m_C diagonal -- a measure-zero surface that a continuum"
"     mass distribution never certifies: every observable tie is an"
"     ensemble tie; 'it really tied' is undecidable from the tower)"

# ---------------- (B) the witness: quantum side vs classical side ----------
# real 4x4 machinery (all states/operators real in this construction)
function MatMul4($A4, $B4) {
  $out=New-Object 'object[]' 4
  for($i=0;$i -lt 4;$i++){ $row=New-Object 'double[]' 4
    for($j=0;$j -lt 4;$j++){ $s=0.0; for($q=0;$q -lt 4;$q++){ $s+=$A4[$i][$q]*$B4[$q][$j] }; $row[$j]=$s }
    $out[$i]=$row }
  ,$out
}
function Kron2($P2, $Q2) {
  $out=New-Object 'object[]' 4
  for($i=0;$i -lt 4;$i++){ $out[$i]=New-Object 'double[]' 4 }
  for($i=0;$i -lt 2;$i++){ for($j=0;$j -lt 2;$j++){ for($k=0;$k -lt 2;$k++){ for($l=0;$l -lt 2;$l++){
    $out[2*$i+$k][2*$j+$l]=$P2[$i][$j]*$Q2[$k][$l]
  }}}}
  ,$out
}
function Trace4($A4){ $s=0.0; for($i=0;$i -lt 4;$i++){ $s+=$A4[$i][$i] }; $s }
function Outer4($v){ $out=New-Object 'object[]' 4; for($i=0;$i -lt 4;$i++){ $row=New-Object 'double[]' 4; for($j=0;$j -lt 4;$j++){ $row[$j]=$v[$i]*$v[$j] }; $out[$i]=$row }; ,$out }

function JacobiEig3($S3) {
  $n=3
  $A=New-Object 'object[]' $n
  for($i=0;$i -lt $n;$i++){ $row=New-Object 'double[]' $n; for($j=0;$j -lt $n;$j++){ $row[$j]=$S3[$i][$j] }; $A[$i]=$row }
  for($sweepI=0;$sweepI -lt 100;$sweepI++){
    $off=0.0
    for($p=0;$p -lt $n-1;$p++){ for($q=$p+1;$q -lt $n;$q++){ $av=[math]::Abs($A[$p][$q]); if($av -gt $off){$off=$av} } }
    if($off -lt 1e-14){ break }
    for($p=0;$p -lt $n-1;$p++){ for($q=$p+1;$q -lt $n;$q++){
      if([math]::Abs($A[$p][$q]) -lt 1e-15){ continue }
      $theta=0.5*($A[$q][$q]-$A[$p][$p])/$A[$p][$q]
      $tsign=1.0; if($theta -lt 0){$tsign=-1.0}
      $tval=$tsign/([math]::Abs($theta)+[math]::Sqrt($theta*$theta+1.0))
      $cv=1.0/[math]::Sqrt($tval*$tval+1.0); $sv=$tval*$cv
      for($i=0;$i -lt $n;$i++){ $aip=$A[$i][$p]; $aiq=$A[$i][$q]; $A[$i][$p]=$cv*$aip-$sv*$aiq; $A[$i][$q]=$sv*$aip+$cv*$aiq }
      for($j=0;$j -lt $n;$j++){ $apj=$A[$p][$j]; $aqj=$A[$q][$j]; $A[$p][$j]=$cv*$apj-$sv*$aqj; $A[$q][$j]=$sv*$apj+$cv*$aqj }
    }}
  }
  $ev=New-Object 'double[]' $n; for($i=0;$i -lt $n;$i++){ $ev[$i]=$A[$i][$i] }
  ,$ev
}

# Pauli matrices (sy \otimes sy is real; single-y correlators vanish for
# real states, so the full T matrix is real-computable)
$sx=@( @(0.0,1.0), @(1.0,0.0) )
$sz=@( @(1.0,0.0), @(0.0,-1.0) )
$syy=@( @(0.0,0.0,0.0,-1.0), @(0.0,0.0,1.0,0.0), @(0.0,1.0,0.0,0.0), @(-1.0,0.0,0.0,0.0) )

function Horodecki($rho) {
  # T_ij = Tr[rho sigma_i x sigma_j]; y-cross terms vanish here (real rho,
  # single-y operators are imaginary): T = diag-block over {x,z} plus T_yy
  $paulis=@($sx,$sz)
  $Tm=New-Object 'object[]' 3
  for($i=0;$i -lt 3;$i++){ $Tm[$i]=New-Object 'double[]' 3 }
  for($i=0;$i -lt 2;$i++){ for($j=0;$j -lt 2;$j++){
    $op=Kron2 $paulis[$i] $paulis[$j]
    $ri=$i; if($i -eq 1){$ri=2}
    $rj=$j; if($j -eq 1){$rj=2}
    $Tm[$ri][$rj]=Trace4 (MatMul4 $rho $op)
  }}
  $Tm[1][1]=Trace4 (MatMul4 $rho $syy)
  # S = 2 sqrt(u1+u2), u = top two eigenvalues of T^T T
  $TT=New-Object 'object[]' 3
  for($i=0;$i -lt 3;$i++){ $row=New-Object 'double[]' 3
    for($j=0;$j -lt 3;$j++){ $s=0.0; for($q=0;$q -lt 3;$q++){ $s+=$Tm[$q][$i]*$Tm[$q][$j] }; $row[$j]=$s }
    $TT[$i]=$row }
  $ev=JacobiEig3 $TT
  $sorted=@($ev | Sort-Object -Descending)
  2.0*[math]::Sqrt($sorted[0]+$sorted[1])
}

# order AB: U_B U_A |0> with U_A = sz, U_B = H -> |+>;  order BA -> |->
$plus=@(0.7071067811865476,0.7071067811865476)
$minus=@(0.7071067811865476,-0.7071067811865476)
$vPP=New-Object 'double[]' 4; $vMM=New-Object 'double[]' 4
for($i=0;$i -lt 2;$i++){ for($j=0;$j -lt 2;$j++){ $vPP[2*$i+$j]=$plus[$i]*$plus[$j]; $vMM[2*$i+$j]=$minus[$i]*$minus[$j] } }

""
"==================================================================="
"(B) THE WITNESS, BOTH SIDES  (same eta from the same race)"
# warm-up: single-switch interference witness with anticommuting ops
# D = Re<0|(sz sx)^2|0> = -1 coherent; any mixture of orders: D = 0
# (no commas next to arithmetic in array literals -- comma binds first!)
$szsx=@( @(0.0,1.0), @(-1.0,0.0) )   # sz sx (real)
$dcoh = $szsx[0][0]*$szsx[0][0] + $szsx[0][1]*$szsx[1][0]
("  warm-up local switch witness D (U_A=sx, U_B=sz): coherent = {0}   any mixture = 0" -f $dcoh)
"  (each WING of the pair state below is locally mixed too -- D_local = 0:"
"   the order-nonclassicality lives only in the CORRELATIONS, hence Bell)"
""
"    xC     eta      S_classical (Horodecki)   S_coherent (Horodecki)   2sqrt(1+4eta(1-eta))   margin"
foreach($xC in $sweep){
  $rs=RaceStats $xC
  $eta=$rs[0]
  # classical mixture of orders
  $rhoC=New-Object 'object[]' 4
  $oPP=Outer4 $vPP; $oMM=Outer4 $vMM
  for($i=0;$i -lt 4;$i++){ $row=New-Object 'double[]' 4
    for($j=0;$j -lt 4;$j++){ $row[$j]=$eta*$oPP[$i][$j]+(1.0-$eta)*$oMM[$i][$j] }
    $rhoC[$i]=$row }
  # coherent superposition of orders (same weights)
  $vQ=New-Object 'double[]' 4
  $se=[math]::Sqrt($eta); $sf=[math]::Sqrt(1.0-$eta)
  for($i=0;$i -lt 4;$i++){ $vQ[$i]=$se*$vPP[$i]+$sf*$vMM[$i] }
  $rhoQ=Outer4 $vQ
  $Scl=Horodecki $rhoC
  $Sq=Horodecki $rhoQ
  $Sform=2.0*[math]::Sqrt(1.0+4.0*$eta*(1.0-$eta))
  ("  {0,6:0.##}   {1,6:0.###}        {2,8:0.######}                 {3,8:0.######}               {4,8:0.######}        {5,6:0.###}" -f $xC,$eta,$Scl,$Sq,$Sform,($Sq-2.0))
}
"  Classical mixtures sit AT the separable bound S = 2 for every eta; the"
"  coherent order violates it whenever the order is ambiguous at all, and"
"  MAXIMALLY (2 sqrt 2) exactly at the tie. The tie point is where the"
"  two sides of the divide are farthest apart: maximum classical safety,"
"  maximum quantum order-entanglement -- same marginals, same eta."

# ---------------- emit example 23 data (the near-tie configuration) --------
""
"==================================================================="
$xTie=$tieRow[0]; $rsT=$tieRow[1]
("EXAMPLE 23 DATA (xC = {0}: eta = {1})" -f $xTie, (Fmt $rsT[0]))
("let IND = [{0}]" -f (($rsT[4] | ForEach-Object { Fmt $_ }) -join ', '))
("let VB = [{0}]" -f (($rsT[5] | ForEach-Object { Fmt $_ }) -join ', '))
$etaT=$rsT[0]
$hT=-($etaT*[math]::Log($etaT)+(1.0-$etaT)*[math]::Log(1.0-$etaT))/[math]::Log(2.0)
$SqT=2.0*[math]::Sqrt(1.0+4.0*$etaT*(1.0-$etaT))
("  refs: eta={0}  entropy={1}  Var(vB)={2}  S_cl=2  S_q={3}  margin={4}" -f (Fmt $etaT),(Fmt $hT),(Fmt $rsT[3]),(Fmt $SqT),(Fmt ($SqT-2.0)))
