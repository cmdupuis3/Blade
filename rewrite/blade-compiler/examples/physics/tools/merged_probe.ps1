# ============================================================================
# PROBING A MERGED PAIR: how much mass does the third ball encounter?
#
# Two balls A, B with poorly-measured masses from the SAME overlapping jet
# sit close together (B near, A far) -- a "merged" pair: the unordered mass
# pair is known, WHICH mass is WHERE is not. A probe ball C (m_C = 1 sharp,
# u = 2) strikes the pair. Per realization the encounter is never "a mass":
# it is a CASCADE -- C hits B, B hits A, B may rattle back into C, etc.
# Define the mass C reports from its recoil by the single-scatter inversion
#     m_eff = m_C (u - v_f) / (u + v_f)
# (what single stationary mass would have produced this recoil?). Findings:
#
# 1. THE PAIR NEVER WEIGHS AS ITS SUM. m_eff is a cascade-dependent nonlinear
#    functional interpolating between "the near ball alone" (single hit) and
#    the far ball (many-rattle mediated limit); it approaches m_A + m_B ONLY
#    if the pair is rigidly bound (a constraint force -- different physics).
#    Momentum bookkeeping sees the total mass; kinematic inversion does not.
#    "How much mass is encountered" depends on which question the probe asks.
#
# 2. THE ANSWER IS A TOWER, NOT A NUMBER. The rattle count is itself fuzzy
#    (1, 2, 3... C-hits across the ensemble), so m_eff is a mixture over
#    cascade branches: its higher cumulants encode the branch structure.
#
# 3. THE PROBE RE-SHARPENS MERGED IDENTITY. The merged record lost which-is-
#    where; the cascade has not: it strikes the NEAR ball first. The m_eff
#    towers of the heavy-near and light-near sub-ensembles differ strongly
#    -- a collision is a measurement (ex 06), and it can recover information
#    the merged record erased.
#
# Quantum contrast: scattering off a pair of centers superposes the path
# amplitudes (A-first / B-first interfere); here the cascade orders are a
# classical mixture -- the branch structure shows up as cumulants, not
# fringes. Companion: 27_merged_mass_probe.blade computes m_eff and all
# towers in-language from the embedded ensemble.
# ============================================================================

$invc=[System.Globalization.CultureInfo]::InvariantCulture
function Fmt($x){ $t=$x.ToString("G12",$invc); if($t -notmatch '[.eE]'){ $t="$t.0" }; $t }

$JET=@(0.5, 0.75, 1.0, 1.35, 1.8)
$UC=2.0

function Cascade($mB, $mA) {
  # balls: 0 = C (probe), 1 = B (near), 2 = A (far)
  $ms=@(1.0, $mB, $mA)
  $rad=New-Object 'double[]' 3
  $px=@(-10.0, -1.2, 1.2)
  $pv=@($UC, 0.0, 0.0)
  for($i=0;$i -lt 3;$i++){ $rad[$i]=0.5*[math]::Pow($ms[$i],1.0/3.0) }
  $nC=0; $nAB=0
  for($e=0;$e -lt 40;$e++){
    $bdt=1e18; $bk=-1
    for($k=0;$k -lt 2;$k++){
      $dvk=$pv[$k]-$pv[$k+1]
      if($dvk -gt 1e-15){
        $gapk=$px[$k+1]-$px[$k]-$rad[$k]-$rad[$k+1]
        $dtk=$gapk/$dvk
        if($dtk -lt -1e-12){ continue }
        if($dtk -lt 0){ $dtk=0.0 }
        if($dtk -lt $bdt){ $bdt=$dtk; $bk=$k }
      }
    }
    if($bk -lt 0){ break }
    for($i=0;$i -lt 3;$i++){ $px[$i]+=$pv[$i]*$bdt }
    $ma2=$ms[$bk]; $mb2=$ms[$bk+1]; $va=$pv[$bk]; $vb=$pv[$bk+1]
    $pv[$bk]  =(($ma2-$mb2)*$va+2.0*$mb2*$vb)/($ma2+$mb2)
    $pv[$bk+1]=(($mb2-$ma2)*$vb+2.0*$ma2*$va)/($ma2+$mb2)
    if($bk -eq 0){ $nC++ } else { $nAB++ }
  }
  ,@($pv[0], $nC, $nAB)
}

$cMA=@();$cMB=@();$cVF=@();$cNC=@()
$meffAll=@();$mbAll=@();$sumAll=@()
$heavySum=0.0;$heavyN=0;$lightSum=0.0;$lightN=0
$rattle=@{}
foreach($mB in $JET){ foreach($mA in $JET){
  $cc=Cascade $mB $mA
  $vf=$cc[0]; $nC=$cc[1]
  $meff=1.0*($UC-$vf)/($UC+$vf)
  $cMA+=(Fmt $mA); $cMB+=(Fmt $mB); $cVF+=(Fmt $vf); $cNC+=(Fmt ([double]$nC))
  $meffAll+=$meff; $mbAll+=$mB; $sumAll+=($mA+$mB)
  if($mB -gt $mA){ $heavySum+=$meff; $heavyN++ }
  if($mB -lt $mA){ $lightSum+=$meff; $lightN++ }
  if($rattle.ContainsKey($nC)){ $rattle[$nC]++ } else { $rattle[$nC]=1 }
}}
function MeanOf($vals){ $s=0.0; foreach($v in $vals){$s+=$v}; $s/$vals.Count }
$k1=MeanOf $meffAll
$k2=0.0; foreach($v in $meffAll){ $k2+=($v-$k1)*($v-$k1) }; $k2/=25.0
$mbMean=MeanOf $mbAll; $sumMean=MeanOf $sumAll

"==================================================================="
"THE MERGED-PAIR PROBE (25 mass pairs, C at u = $UC, m_C = 1)"
("  E[m_eff]      = {0:0.####}   var = {1:0.####}" -f $k1,$k2)
("  E[m_near]     = {0:0.####}   (single-hit model)" -f $mbMean)
("  E[m_A + m_B]  = {0:0.####}   (rigid-composite model)" -f $sumMean)
("  => the pair does NOT weigh as its sum: |E m_eff - sum| = {0:0.###}" -f [math]::Abs($k1-$sumMean))
""
"  rattle distribution (number of C-involved collisions):"
foreach($kv in ($rattle.GetEnumerator() | Sort-Object Name)){ ("    {0} hit(s): {1}/25" -f $kv.Name,$kv.Value) }
""
("  heavy-near sub-ensemble: E[m_eff] = {0:0.####}  ({1} pairs)" -f ($heavySum/$heavyN),$heavyN)
("  light-near sub-ensemble: E[m_eff] = {0:0.####}  ({1} pairs)" -f ($lightSum/$lightN),$lightN)
"  (the cascade hits the NEAR ball first: the probe re-measures the"
"   which-is-where bit the merged record erased)"
""
"EXAMPLE 27 DATA"
("let MA = [{0}]" -f ($cMA -join ', '))
("let MB = [{0}]" -f ($cMB -join ', '))
("let VF = [{0}]" -f ($cVF -join ', '))
("let NC = [{0}]" -f ($cNC -join ', '))
