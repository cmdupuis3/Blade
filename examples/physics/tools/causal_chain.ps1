# ============================================================================
# THE CAUSAL CHAIN: what many balls show that three cannot.
#
# Eight balls, two projectiles driving cascades inward from both ends,
# six masses fuzzy (3-point jets, 3^6 = 729-realization TRUE PRODUCT grid),
# radii R = 0.5 m^(1/3). Per realization: exact event-driven cascade
# (~10-25 collisions). Three effects, each new at this scale:
#
# (a) THE MULTIVARIATE ORDER TOWER. Three balls had ONE ambiguous order bit
#     (a lone Bernoulli). Here several races run concurrently: BL (left
#     block: does bond 0 re-fire before bond 1 does?), BR (the mirror
#     race in the right block), KM (global: ball 3's final direction). Their joint distribution is the S_n measure's second-
#     moment sector -- order bits with covariances.
#
# (b) SPACELIKE FACTORIZATION, EXACTLY. Before the cascades meet, BL is a
#     function of left masses only and BR of right masses only; over the
#     product grid their covariance and MUTUAL INFORMATION are EXACTLY zero
#     (machine precision) -- the tower factorizes outside the causal
#     diamond. Post-merge observables (KM) couple to both: I > 0. The
#     collision fronts define an effective causal cone, and the cone edge
#     is itself fuzzy: the front's arrival time DISPERSES with depth.
#
# (c) CAUSAL ENTROPY PRODUCTION. The itinerary (event-label sequence) is a
#     random word; its prefix entropy H(k) grows along the cascade -- the
#     chain converts mass fuzz into causal-structure entropy at a
#     measurable rate (bits/event). The event COUNT itself is fuzzy.
#
# The event iteration is inherently sequential (done here, milliseconds);
# every ensemble statistic is computed IN BLADE in the companion
# 26_causal_chain.blade from an embedded 81-realization product sub-grid.
# ============================================================================

$invc=[System.Globalization.CultureInfo]::InvariantCulture
function Fmt($x){ $t=$x.ToString("G12",$invc); if($t -notmatch '[.eE]'){ $t="$t.0" }; $t }

# ---------------- configuration --------------------------------------------
$NB=8
$X0=@(0.0, 3.0, 6.0, 9.0, 12.0, 15.0, 18.0, 21.0)
$V0=@(1.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, -1.3)
$MFIX=@(1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0)
$JET=@(0.8, 1.0, 1.25)
$FUZZY=@(0,1,2,5,6,7)          # six fuzzy masses; 3 and 4 fixed
$MAXEV=30

# one realization: returns event labels+times and summary observables
function RunChain($mArr) {
  $rad=New-Object 'double[]' $NB
  $px=New-Object 'double[]' $NB
  $pv=New-Object 'double[]' $NB
  for($i=0;$i -lt $NB;$i++){ $rad[$i]=0.5*[math]::Pow($mArr[$i],1.0/3.0); $px[$i]=$X0[$i]; $pv[$i]=$V0[$i] }
  $evB=New-Object System.Collections.ArrayList   # bond index per event
  $evT=New-Object System.Collections.ArrayList
  $tnow=0.0; $tieCt=0
  for($e=0;$e -lt $MAXEV;$e++){
    $bdt=1e18; $bk=-1
    for($k=0;$k -lt $NB-1;$k++){
      $dvk=$pv[$k]-$pv[$k+1]
      if($dvk -gt 1e-15){
        $gapk=$px[$k+1]-$px[$k]-$rad[$k]-$rad[$k+1]
        $dtk=$gapk/$dvk
        if($dtk -lt -1e-12){ continue }
        if($dtk -lt 0){ $dtk=0.0 }
        if([math]::Abs($dtk-$bdt) -lt 1e-12 -and $bk -ge 0 -and [math]::Abs($k-$bk) -le 1){ $tieCt++ }   # only ADJACENT-bond ties are singular (shared ball)
        if($dtk -lt $bdt){ $bdt=$dtk; $bk=$k }
      }
    }
    if($bk -lt 0){ break }
    for($i=0;$i -lt $NB;$i++){ $px[$i]+=$pv[$i]*$bdt }
    $tnow+=$bdt
    $ma=$mArr[$bk]; $mb=$mArr[$bk+1]; $va=$pv[$bk]; $vb=$pv[$bk+1]
    $pv[$bk]  =(($ma-$mb)*$va+2.0*$mb*$vb)/($ma+$mb)
    $pv[$bk+1]=(($mb-$ma)*$vb+2.0*$ma*$va)/($ma+$mb)
    [void]$evB.Add($bk); [void]$evT.Add($tnow)
  }
  ,@($evB,$evT,$pv,$tieCt)
}

# observables from an event record
function FirstT($evB,$evT,$bond,$occ){
  $c=0
  for($i=0;$i -lt $evB.Count;$i++){
    if($evB[$i] -eq $bond){ $c++; if($c -eq $occ){ return $evT[$i] } }
  }
  1e18
}

# ---------------- full 3^6 product grid ------------------------------------
$rows=New-Object System.Collections.ArrayList
$tiesTot=0
foreach($a0 in $JET){ foreach($a1 in $JET){ foreach($a2 in $JET){
foreach($a5 in $JET){ foreach($a6 in $JET){ foreach($a7 in $JET){
  $mArr=@($a0,$a1,$a2,0.95,1.05,$a5,$a6,$a7)
  $rr=RunChain $mArr
  $evB=$rr[0]; $evT=$rr[1]; $pvF=$rr[2]; $tiesTot+=$rr[3]
  # race bits (chosen empirically: P strictly inside (0,1))
  $t01b=FirstT $evB $evT 0 2      # second contact of bond 0
  $t12b=FirstT $evB $evT 1 2      # second contact of bond 1
  $t67b=FirstT $evB $evT 6 2      # second contact of bond 6
  $t56b=FirstT $evB $evT 5 2      # second contact of bond 5
  $bl=0.0; if($t01b -lt $t12b){ $bl=1.0 }
  $br=0.0; if($t67b -lt $t56b){ $br=1.0 }
  $km=0.0; if($pvF[3] -gt 0.0){ $km=1.0 }
  $t12=FirstT $evB $evT 1 1
  $cnt3=0; for($i=0;$i -lt $evB.Count;$i++){ if($evB[$i] -eq 2 -or $evB[$i] -eq 3){ $cnt3++ } }
  # itinerary prefix string (first 12 events)
  $kmax=12; if($evB.Count -lt 12){ $kmax=$evB.Count }
  $pref=""
  $prefs=New-Object 'string[]' 13
  for($k=0;$k -lt $kmax;$k++){ $pref+=("{0}." -f $evB[$k]); $prefs[$k+1]=$pref }
  [void]$rows.Add(@($a0,$a1,$a2,$a5,$a6,$a7,$bl,$br,$km,$t12,$cnt3,$prefs,$evB.Count))
}}}}}}
$NR=$rows.Count

"==================================================================="
"CHAIN: $NR realizations, ties = $tiesTot (must be 0)"

# --- (a) the order bits and their covariance matrix ---
function BitStats($idxA,$idxB){
  $pa=0.0;$pb=0.0;$pab=0.0
  foreach($rw in $rows){ $pa+=$rw[$idxA]; $pb+=$rw[$idxB]; $pab+=$rw[$idxA]*$rw[$idxB] }
  $pa/=$NR;$pb/=$NR;$pab/=$NR
  ,@($pa,$pb,($pab-$pa*$pb),$pab)
}
$sBLBR=BitStats 6 7
$sBLKM=BitStats 6 8
$sBRKM=BitStats 7 8
""
"(a) MULTIVARIATE ORDER TOWER (bits: BL left race, BR right race, KM ball-3 fate)"
("  P(BL) = {0:0.####}   P(BR) = {1:0.####}   P(KM) = {2:0.####}" -f $sBLBR[0],$sBLBR[1],$sBLKM[1])
("  cov(BL,BR) = {0:e3}   cov(BL,KM) = {1:0.#####}   cov(BR,KM) = {2:0.#####}" -f $sBLBR[2],$sBLKM[2],$sBRKM[2])

# --- (b) mutual information: zero outside the cone, positive inside ---
function MutInf($idxA,$idxB){
  $st=BitStats $idxA $idxB
  $pa=$st[0];$pb=$st[1];$p11=$st[3]
  $p10=$pa-$p11;$p01=$pb-$p11;$p00=1.0-$pa-$pb+$p11
  $mi=0.0
  foreach($cell in @(@($p11,($pa*$pb)),@($p10,($pa*(1.0-$pb))),@($p01,((1.0-$pa)*$pb)),@($p00,((1.0-$pa)*(1.0-$pb))))){
    $pc=$cell[0]; $pi=$cell[1]
    if($pc -gt 1e-15 -and $pi -gt 1e-15){ $mi+=$pc*[math]::Log($pc/$pi)/[math]::Log(2.0) }
  }
  $mi
}
""
"(b) SPACELIKE FACTORIZATION / THE FUZZY CONE"
("  I(BL;BR) = {0:e3} bits  (spacelike: EXACT product measure -> 0)" -f (MutInf 6 7))
("  I(BL;KM) = {0:0.#####} bits   I(BR;KM) = {1:0.#####} bits  (inside the cone)" -f (MutInf 6 8),(MutInf 7 8))
# front arrival dispersion vs depth (left-side pass, right block fixed)
$fr0=@();$fr1=@();$fr2=@()
foreach($a0 in $JET){ foreach($a1 in $JET){ foreach($a2 in $JET){
  $mArr=@($a0,$a1,$a2,1.0,1.0,1.0,1.0,1.0)
  $rr=RunChain $mArr
  $fr0+=(FirstT $rr[0] $rr[1] 0 1)
  $fr1+=(FirstT $rr[0] $rr[1] 1 1)
  $fr2+=(FirstT $rr[0] $rr[1] 2 1)
}}}
function MeanStd($vals){
  $n=$vals.Count;$m=0.0; foreach($v in $vals){$m+=$v}; $m/=$n
  $s=0.0; foreach($v in $vals){ $s+=($v-$m)*($v-$m) }; $s=[math]::Sqrt($s/$n)
  ,@($m,$s)
}
$f0=MeanStd $fr0; $f1=MeanStd $fr1; $f2=MeanStd $fr2
("  left front arrival (mean, std): bond0 ({0:0.###}, {1:0.####})  bond1 ({2:0.###}, {3:0.####})  bond2 ({4:0.###}, {5:0.####})" -f $f0[0],$f0[1],$f1[0],$f1[1],$f2[0],$f2[1])
"  (the cone edge thickens with depth: the causal boundary is itself fuzzy)"

# --- (c) itinerary entropy production ---
""
"(c) CAUSAL ENTROPY PRODUCTION (prefix entropy of the event word)"
"    k    #itineraries    H(k) bits"
for($k=1;$k -le 12;$k++){
  $tab=@{}
  foreach($rw in $rows){
    $pf=$rw[11][$k]
    if($null -eq $pf){ $pf="(short)" }
    if($tab.ContainsKey($pf)){ $tab[$pf]++ } else { $tab[$pf]=1 }
  }
  $hh=0.0
  foreach($kv in $tab.GetEnumerator()){ $pq=$kv.Value/[double]$NR; $hh-=$pq*[math]::Log($pq)/[math]::Log(2.0) }
  ("  {0,3}      {1,6}        {2:0.###}" -f $k,$tab.Count,$hh)
}
$cntStats=MeanStd (@($rows | ForEach-Object { [double]$_[10] }))
$totStats=MeanStd (@($rows | ForEach-Object { [double]$_[12] }))
("  middle-bond collision count: mean {0:0.###} std {1:0.###}; total events: mean {2:0.###} std {3:0.###}" -f $cntStats[0],$cntStats[1],$totStats[0],$totStats[1])

# ---------------- emit the 81-realization product sub-grid for example 26 ---
# vary m1, m2 (left) x m5, m6 (right); m0 = m7 = 1.0 fixed
""
"==================================================================="
"EXAMPLE 26 DATA (3^4 = 81 product sub-grid: m1,m2 x m5,m6)"
$cIL=@();$cIR=@();$cKM=@();$cT12=@();$cCN=@()
foreach($a1 in $JET){ foreach($a2 in $JET){ foreach($a5 in $JET){ foreach($a6 in $JET){
  $mArr=@(1.0,$a1,$a2,1.0,1.0,$a5,$a6,1.0)
  $rr=RunChain $mArr
  $evB=$rr[0]; $evT=$rr[1]; $pvF=$rr[2]
  $t01b=FirstT $evB $evT 0 2
  $t12b=FirstT $evB $evT 1 2
  $t67b=FirstT $evB $evT 6 2
  $t56b=FirstT $evB $evT 5 2
  $bl=0.0; if($t01b -lt $t12b){ $bl=1.0 }
  $br=0.0; if($t67b -lt $t56b){ $br=1.0 }
  $km=0.0; if($pvF[3] -gt 0.0){ $km=1.0 }
  $cIL+=(Fmt $bl); $cIR+=(Fmt $br); $cKM+=(Fmt $km)
  $cT12+=(Fmt (FirstT $evB $evT 1 1))
  $cn=0; for($i=0;$i -lt $evB.Count;$i++){ if($evB[$i] -eq 2 -or $evB[$i] -eq 3){ $cn++ } }
  $cCN+=(Fmt ([double]$cn))
}}}}
("let IL = [{0}]" -f ($cIL -join ', '))
("let IR = [{0}]" -f ($cIR -join ', '))
("let KM = [{0}]" -f ($cKM -join ', '))
("let T12 = [{0}]" -f ($cT12 -join ', '))
("let CN = [{0}]" -f ($cCN -join ', '))
# subgrid references
$pa=0.0;$pb=0.0;$pk=0.0;$pab=0.0;$pak=0.0
for($i=0;$i -lt 81;$i++){
  $pa+=[double]$cIL[$i]; $pb+=[double]$cIR[$i]; $pk+=[double]$cKM[$i]
  $pab+=[double]$cIL[$i]*[double]$cIR[$i]; $pak+=[double]$cIL[$i]*[double]$cKM[$i]
}
$pa/=81;$pb/=81;$pk/=81;$pab/=81;$pak/=81
("  refs: P(IL)={0}  P(IR)={1}  P(KM)={2}  cov(IL,IR)={3}  cov(IL,KM)={4}" -f (Fmt $pa),(Fmt $pb),(Fmt $pk),(Fmt ($pab-$pa*$pb)),(Fmt ($pak-$pa*$pk)))
