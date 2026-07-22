# ============================================================================
# THE SPECTRUM IS A MOMENT PROBLEM: Gauss quadrature on lagged cumulants.
#
# For any signal, the lagged cumulant sequence of a quasiperiodic process is
#     C(n D) = sum_k w_k cos(omega_k n D),  w_k > 0
# and since cos(n theta) = T_n(cos theta) (Chebyshev), the sequence C(n D) IS
# the CHEBYSHEV-MOMENT TOWER of a measure mu = sum_k w_k delta(lambda_k) on
# [-1,1], with lambda_k = cos(omega_k D). Everything the arc wanted from the
# spectrum is a classical truncated-moment-problem read on that tower:
#
#   * Wheeler's modified-moment algorithm turns C(0..2m-1) into the Jacobi
#     recurrence coefficients (alpha_j, beta_j) of mu;
#   * if mu has exactly m atoms (a TORUS: finitely many spectral lines), the
#     recurrence TERMINATES: beta_m ~ 0. Chaos has a continuous spectral
#     measure: beta_k stays O(1). An INTEGER-VALUED integrability verdict --
#     the sharp version of example 15's "autocovariance persists vs decays";
#   * Golub-Welsch (eigenvalues of the tridiagonal) returns the atoms:
#     the NODES of the Gauss quadrature ARE the frequencies, the WEIGHTS are
#     the line powers. No FFT, no bins, no leakage.
#
# And the profound part -- point it at QUANTUM expectation data: for a
# wavepacket in an anharmonic well, <x>(t) = sum A_j cos(Omega_j t) with
# Omega_j = E_{n+1}-E_n. The quadrature nodes of the autocovariance tower are
# the BOHR TRANSITION ENERGIES: the moment tower does spectroscopy. The
# harmonic oscillator is the degenerate case: ALL gaps equal, the spectral
# measure collapses to ONE atom (rank 1) -- which is simultaneously why
# classical SHM has a single frequency and why the harmonic ladder is evenly
# spaced. Anharmonicity = the rank of the autocovariance Hankel matrix.
#
# The arc's koopman_edmd left "frequencies need a general eigensolver" open;
# this closes it with moment methods only (a symmetric tridiagonal eigenvalue
# problem), which is exactly the machinery a cumulant tower is born with.
#
# Companions: 17_hankel_rank_integrability.blade (classical: quartic torus vs
# Henon-Heiles chaos), 18_bohr_spectroscopy.blade (quantum: harmonic rank-1 vs
# anharmonic split lines) -- both compute the lagged towers and the Hankel
# determinants IN Blade; this tool cross-checks them and does the full
# quadrature recovery.
# ============================================================================

$PI = [math]::PI

# ---------- symmetric Jacobi eigensolver (cyclic rotations) ----------
function JacobiEig($S, $n) {
  $A=New-Object 'object[]' $n
  for($i=0;$i -lt $n;$i++){ $row=New-Object 'double[]' $n; for($j=0;$j -lt $n;$j++){ $row[$j]=$S[$i][$j] }; $A[$i]=$row }
  $V=New-Object 'object[]' $n
  for($i=0;$i -lt $n;$i++){ $row=New-Object 'double[]' $n; $row[$i]=1.0; $V[$i]=$row }
  for($sweep=0;$sweep -lt 200;$sweep++){
    $off=0.0
    for($p=0;$p -lt $n-1;$p++){ for($q=$p+1;$q -lt $n;$q++){ $av=[math]::Abs($A[$p][$q]); if($av -gt $off){$off=$av} } }
    if($off -lt 1e-13){ break }
    for($p=0;$p -lt $n-1;$p++){
      for($q=$p+1;$q -lt $n;$q++){
        if([math]::Abs($A[$p][$q]) -lt 1e-15){ continue }
        $theta=0.5*($A[$q][$q]-$A[$p][$p])/$A[$p][$q]
        $tsign=1.0; if($theta -lt 0){$tsign=-1.0}
        $tval=$tsign/([math]::Abs($theta)+[math]::Sqrt($theta*$theta+1.0))
        $cv=1.0/[math]::Sqrt($tval*$tval+1.0); $sv=$tval*$cv
        for($i=0;$i -lt $n;$i++){ $aip=$A[$i][$p]; $aiq=$A[$i][$q]; $A[$i][$p]=$cv*$aip-$sv*$aiq; $A[$i][$q]=$sv*$aip+$cv*$aiq }
        for($j=0;$j -lt $n;$j++){ $apj=$A[$p][$j]; $aqj=$A[$q][$j]; $A[$p][$j]=$cv*$apj-$sv*$aqj; $A[$q][$j]=$sv*$apj+$cv*$aqj }
        for($i=0;$i -lt $n;$i++){ $vip=$V[$i][$p]; $viq=$V[$i][$q]; $V[$i][$p]=$cv*$vip-$sv*$viq; $V[$i][$q]=$sv*$vip+$cv*$viq }
      }
    }
  }
  $ev=New-Object 'double[]' $n; for($i=0;$i -lt $n;$i++){ $ev[$i]=$A[$i][$i] }
  $idx=@(0..($n-1) | Sort-Object { $ev[$_] })
  $evS=New-Object 'double[]' $n
  $VS=New-Object 'object[]' $n; for($i=0;$i -lt $n;$i++){ $VS[$i]=New-Object 'double[]' $n }
  $c2=0
  foreach($ii in $idx){ $evS[$c2]=$ev[$ii]; for($i=0;$i -lt $n;$i++){ $VS[$i][$c2]=$V[$i][$ii] }; $c2++ }
  ,@($evS,$VS)
}

# ---------- Wheeler modified-moment algorithm (monic-Chebyshev auxiliary) ---
# input: raw lagged cumulants C[0..2m-1]; output: Jacobi coeffs alpha[], beta[]
# of the spectral measure (beta[0] = total power). beta[k] ~ 0 <=> the measure
# has exactly k atoms (the recurrence terminates: finite Hankel rank).
function Wheeler($Craw, $mReq) {
  $twoN=$Craw.Count
  # monic Chebyshev modified moments: nu_0 = C_0, nu_n = C_n / 2^(n-1)
  $nu=New-Object 'double[]' $twoN
  $nu[0]=$Craw[0]; $pw=1.0
  for($l=1;$l -lt $twoN;$l++){ $nu[$l]=$Craw[$l]/$pw; $pw*=2.0 }
  $alpha=New-Object 'double[]' $mReq
  $beta=New-Object 'double[]' $mReq
  $sigPrev=New-Object 'double[]' $twoN   # sigma_{k-1}
  $sigPrev2=New-Object 'double[]' $twoN  # sigma_{k-2}
  for($l=0;$l -lt $twoN;$l++){ $sigPrev[$l]=$nu[$l] }
  $alpha[0]=$nu[1]/$nu[0]
  $beta[0]=$nu[0]
  $mGot=$mReq
  for($k=1;$k -lt $mReq;$k++){
    $sigCur=New-Object 'double[]' $twoN
    for($l=$k;$l -le $twoN-$k-1;$l++){
      $bAux=0.25; if($l -eq 1){ $bAux=0.5 }; if($l -eq 0){ $bAux=0.0 }
      $sigCur[$l]=$sigPrev[$l+1] - $alpha[$k-1]*$sigPrev[$l] - $beta[$k-1]*$sigPrev2[$l] + $bAux*$sigPrev[$l-1]
    }
    if([math]::Abs($sigPrev[$k-1]) -lt 1e-300){ $mGot=$k; break }
    $beta[$k]=$sigCur[$k]/$sigPrev[$k-1]
    if([math]::Abs($sigCur[$k]) -lt 1e-300){ $mGot=$k; break }
    $alpha[$k]=$sigCur[$k+1]/$sigCur[$k] - $sigPrev[$k]/$sigPrev[$k-1]
    $sigPrev2=$sigPrev; $sigPrev=$sigCur
  }
  ,@($alpha,$beta,$mGot)
}

# ---------- Golub-Welsch: nodes/weights from the Jacobi coefficients --------
function GaussNodes($alpha, $beta, $mUse) {
  $J=New-Object 'object[]' $mUse
  for($i=0;$i -lt $mUse;$i++){ $J[$i]=New-Object 'double[]' $mUse }
  for($i=0;$i -lt $mUse;$i++){
    $J[$i][$i]=$alpha[$i]
    if($i -lt $mUse-1){ $ob=[math]::Sqrt([math]::Abs($beta[$i+1])); $J[$i][$i+1]=$ob; $J[$i+1][$i]=$ob }
  }
  $eig=JacobiEig $J $mUse
  $nodes=$eig[0]; $vecs=$eig[1]
  $wts=New-Object 'double[]' $mUse
  for($j=0;$j -lt $mUse;$j++){ $wts[$j]=$beta[0]*$vecs[0][$j]*$vecs[0][$j] }
  ,@($nodes,$wts)
}

# ---------- Chebyshev moments -> power moments (for the Hankel determinants)
# m0=C0, m1=C1, m2=(C2+C0)/2, m3=(C3+3 C1)/4, m4=(C4+4 C2+3 C0)/8
function PowMoms($Cr){ ,@($Cr[0], $Cr[1], (($Cr[2]+$Cr[0])/2.0), (($Cr[3]+3.0*$Cr[1])/4.0), (($Cr[4]+4.0*$Cr[2]+3.0*$Cr[0])/8.0)) }
function HankelDets($Cr) {
  $m0=$Cr[0]; $m1=$Cr[1]; $m2=($Cr[2]+$Cr[0])/2.0; $m3=($Cr[3]+3.0*$Cr[1])/4.0; $m4=($Cr[4]+4.0*$Cr[2]+3.0*$Cr[0])/8.0
  $det2=$m0*$m2-$m1*$m1
  $det3=$m0*($m2*$m4-$m3*$m3)-$m1*($m1*$m4-$m3*$m2)+$m2*($m1*$m3-$m2*$m2)
  ,@(($det2/($m0*$m0)),($det3/($m0*$m0*$m0)))
}

function AutoCov($xs, $maxLag) {
  $N=$xs.Count
  $mu=0.0; foreach($v in $xs){$mu+=$v}; $mu/=$N
  $Cout=New-Object 'double[]' ($maxLag+1)
  for($l=0;$l -le $maxLag;$l++){
    $s=0.0; $cnt=$N-$l
    for($i=0;$i -lt $cnt;$i++){ $s+=($xs[$i]-$mu)*($xs[$i+$l]-$mu) }
    $Cout[$l]=$s/$cnt
  }
  $Cout
}

# ---------- classical trajectories (same systems as unknown_invariants) -----
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

"==================================================================="
"SELF-CHECK: JacobiEig on [[2,1],[1,2]] (expect 1, 3)"
$chk=@( @(2.0,1.0), @(1.0,2.0) )
$er=JacobiEig $chk 2
"  eigenvalues: $($er[0] -join ', ')"

# ============================================================
# (A) CLASSICAL: a 2-torus has finitely many spectral lines; chaos has a
# continuous spectral measure. Quartic (integrable; VERY different x/y
# amplitudes so the two fundamentals separate -- Henon-Heiles is useless
# here, its 1:1 resonance keeps the fundamentals nearly degenerate) vs
# chaotic Henon-Heiles. Atomicity is ALIAS-IMMUNE (cos folds atoms onto
# [-1,1] but finitely many stay finitely many), so the beta test tolerates
# coarse lag spacing; only the omega READOUT wants Delta < pi/omega_max.
# ============================================================
$quartF = { param($x,$y) @( -($x + 0.4*$x*$x*$x), -($y + 0.4*$y*$y*$y) ) }
$hhF    = { param($x,$y) @( -($x + 2.0*$x*$y), -($y + ($x*$x - $y*$y)) ) }
$DT=0.002; $EVERY=100; $DELF=$DT*$EVERY   # fine spacing 0.2

# NOTE: at E ~ 0.127-0.14 individual HH trajectories are often STICKY
# (mixed phase space -- islands persist above the chaos threshold); the
# robustly mixing IC below sits at E ~ 0.153, just under escape (1/6).
$quartTr = Verlet $quartF @(1.2,0.0,0.0,0.3)    $DT 400000 $EVERY
$chaTr   = Verlet $hhF    @(0.1,-0.15,0.43,0.3) $DT 400000 $EVERY
$regTr   = Verlet $hhF    @(0.0,0.0,0.15,0.12)  $DT 400000 $EVERY
$quartU=@(); foreach($s in $quartTr){ $quartU += ($s[0]+1.8*$s[1]) }
$chaU=@();   foreach($s in $chaTr){   $chaU   += ($s[0]+0.7*$s[1]) }
$regU=@();   foreach($s in $regTr){   $regU   += ($s[0]+0.7*$s[1]) }

# reference fundamentals from zero upcrossings of x(t) and y(t)
function Fund($tr, $comp, $dtS) {
  $ups=@(); $prev=$tr[0][$comp]
  for($i=1;$i -lt $tr.Count;$i++){
    $cur=$tr[$i][$comp]
    if($prev -lt 0 -and $cur -ge 0){ $frac=-$prev/($cur-$prev); $ups += (($i-1+$frac)*$dtS) }
    $prev=$cur
  }
  if($ups.Count -lt 2){ return 0.0 }
  2.0*$PI*($ups.Count-1)/($ups[$ups.Count-1]-$ups[0])
}
$OmX = Fund $quartTr 0 $DELF
$OmY = Fund $quartTr 1 $DELF

$MQ=6; $STRIDE=5   # beta/readout lag spacing Delta = 1.0
$quartC=New-Object 'double[]' (2*$MQ); $chaC=New-Object 'double[]' (2*$MQ)
$quartCf = AutoCov $quartU ((2*$MQ-1)*$STRIDE)
$chaCf   = AutoCov $chaU   ((2*$MQ-1)*$STRIDE)
for($l=0;$l -lt 2*$MQ;$l++){ $quartC[$l]=$quartCf[$l*$STRIDE]; $chaC[$l]=$chaCf[$l*$STRIDE] }
$DELB=$DELF*$STRIDE

""
"==================================================================="
"(A) CLASSICAL  (u = x + 0.7 y, $($quartU.Count) samples, Delta=$DELB)"
("  quartic reference fundamentals (zero crossings): Omega_x = {0:0.####}, Omega_y = {1:0.####}" -f $OmX, $OmY)
$wq = Wheeler $quartC $MQ
$wh = Wheeler $chaC $MQ
"  Jacobi beta sequence (beta_k ~ 0 <=> the measure has k atoms):"
"    QUARTIC (2-torus):      $((1..($MQ-1) | ForEach-Object { '{0:e2}' -f $wq[1][$_] }) -join '  ')"
"    HENON-HEILES (chaotic): $((1..($MQ-1) | ForEach-Object { '{0:e2}' -f $wh[1][$_] }) -join '  ')"
"  quartic quadrature -> the torus's lines (omega = acos(node)/Delta):"
$gqf = GaussNodes $wq[0] $wq[1] 4
$qlines=@()
for($j=0;$j -lt 4;$j++){
  $nd=$gqf[0][$j]; if([math]::Abs($nd) -le 1.0){ $om=[math]::Acos($nd)/$DELB } else { $om=-1 }
  $qlines += ,@($om,$gqf[1][$j])
}
foreach($ql in ($qlines | Sort-Object { -$_[1] })){ ("    omega = {0,8:0.####}   weight = {1:e2}" -f $ql[0], $ql[1]) }
"  (the two dominant nodes are the fundamentals Omega_x, Omega_y of the"
"   2-torus; their irrational ratio -> frequency-module rank 2 = 2 actions,"
"   matching unknown_invariants' generator count of 2)"
# long-lag persistence references for example 18 (tau ~ 160..176)
$refReg = AutoCov $regU 880
$refCha = AutoCov $chaU 880
$prReg = [math]::Sqrt(($refReg[800]*$refReg[800]+$refReg[840]*$refReg[840]+$refReg[880]*$refReg[880])/3.0)/$refReg[0]
$prCha = [math]::Sqrt(($refCha[800]*$refCha[800]+$refCha[840]*$refCha[840]+$refCha[880]*$refCha[880])/3.0)/$refCha[0]
("  HH persistence RMS(C at tau=160,168,176)/C(0): regular = {0:0.####}   chaotic = {1:0.####}" -f $prReg, $prCha)

# ============================================================
# (B) QUANTUM: Bohr spectroscopy from the moment tower
# ============================================================
function BuildQuantum($lam, $alphaCoh, $NB) {
  # x matrix in the HO basis
  $X=New-Object 'object[]' $NB
  for($i=0;$i -lt $NB;$i++){ $X[$i]=New-Object 'double[]' $NB }
  for($i=0;$i -lt $NB-1;$i++){ $v=[math]::Sqrt(($i+1)/2.0); $X[$i][$i+1]=$v; $X[$i+1][$i]=$v }
  # x^4
  $X2=New-Object 'object[]' $NB
  for($i=0;$i -lt $NB;$i++){ $row=New-Object 'double[]' $NB; for($j=0;$j -lt $NB;$j++){ $s=0.0; for($q=0;$q -lt $NB;$q++){ $s+=$X[$i][$q]*$X[$q][$j] }; $row[$j]=$s }; $X2[$i]=$row }
  $X4=New-Object 'object[]' $NB
  for($i=0;$i -lt $NB;$i++){ $row=New-Object 'double[]' $NB; for($j=0;$j -lt $NB;$j++){ $s=0.0; for($q=0;$q -lt $NB;$q++){ $s+=$X2[$i][$q]*$X2[$q][$j] }; $row[$j]=$s }; $X4[$i]=$row }
  # H = diag(n+1/2) + lam x^4
  $H=New-Object 'object[]' $NB
  for($i=0;$i -lt $NB;$i++){ $row=New-Object 'double[]' $NB; for($j=0;$j -lt $NB;$j++){ $row[$j]=$lam*$X4[$i][$j] }; $row[$i]+=($i+0.5); $H[$i]=$row }
  $eig=JacobiEig $H $NB
  $Ev=$eig[0]; $V=$eig[1]
  # coherent state |alpha> in HO basis
  $c=New-Object 'double[]' $NB
  $c[0]=[math]::Exp(-0.5*$alphaCoh*$alphaCoh)
  for($i=1;$i -lt $NB;$i++){ $c[$i]=$c[$i-1]*$alphaCoh/[math]::Sqrt($i) }
  # d = V^T c ; Xe = V^T X V
  $d=New-Object 'double[]' $NB
  for($j=0;$j -lt $NB;$j++){ $s=0.0; for($i=0;$i -lt $NB;$i++){ $s+=$V[$i][$j]*$c[$i] }; $d[$j]=$s }
  $XV=New-Object 'object[]' $NB
  for($i=0;$i -lt $NB;$i++){ $row=New-Object 'double[]' $NB; for($j=0;$j -lt $NB;$j++){ $s=0.0; for($q=0;$q -lt $NB;$q++){ $s+=$X[$i][$q]*$V[$q][$j] }; $row[$j]=$s }; $XV[$i]=$row }
  $Xe=New-Object 'object[]' $NB
  for($i=0;$i -lt $NB;$i++){ $row=New-Object 'double[]' $NB; for($j=0;$j -lt $NB;$j++){ $s=0.0; for($q=0;$q -lt $NB;$q++){ $s+=$V[$q][$i]*$XV[$q][$j] }; $row[$j]=$s }; $Xe[$i]=$row }
  # spectral lines of <x>(t): amp_ij = 2 d_i d_j Xe_ij at Omega = E_j - E_i
  $lines=@()
  for($i=0;$i -lt $NB-1;$i++){ for($j=$i+1;$j -lt $NB;$j++){
    $amp=2.0*$d[$i]*$d[$j]*$Xe[$i][$j]
    if([math]::Abs($amp) -gt 5e-3){ $lines += ,@(($Ev[$j]-$Ev[$i]),$amp) }
  }}
  ,@($Ev,$lines)
}

function LinesToC($lines, $Del, $nLags) {
  $Cout=New-Object 'double[]' $nLags
  for($l=0;$l -lt $nLags;$l++){
    $s=0.0
    foreach($ln in $lines){ $s+=0.5*$ln[1]*$ln[1]*[math]::Cos($ln[0]*$l*$Del) }
    $Cout[$l]=$s
  }
  $Cout
}

function LinesToSignal($lines, $Del, $nSamp) {
  $xs=New-Object 'double[]' $nSamp
  for($i=0;$i -lt $nSamp;$i++){
    $s=0.0; foreach($ln in $lines){ $s+=$ln[1]*[math]::Cos($ln[0]*$i*$Del) }
    $xs[$i]=$s
  }
  $xs
}

$LAM=0.1; $ALC=1.2; $NBQ=36; $DELQ=0.5
$bq = BuildQuantum $LAM $ALC $NBQ
$EvA=$bq[0]; $linesA=$bq[1]
$bh = BuildQuantum 0.0 $ALC $NBQ
$linesH=$bh[1]

""
"==================================================================="
"(B) QUANTUM  anharmonic H = p^2/2 + x^2/2 + $LAM x^4, coherent alpha=$ALC"
"  exact Bohr gaps (diagonalization): $((0..4 | ForEach-Object { '{0:0.######}' -f ($EvA[$_+1]-$EvA[$_]) }) -join '  ')"
"  participating lines in <x>(t) (|amp| > 5e-3): $($linesA.Count)"
"  true lines (Omega, weight = amp^2/2):"
foreach($ln in ($linesA | Sort-Object { $_[0] })){ ("    Omega = {0,10:0.########}   w = {1:e3}" -f $ln[0], (0.5*$ln[1]*$ln[1])) }

# exact-moment route: m = number of lines -> the quadrature is EXACT
$MB=$linesA.Count
$CA = LinesToC $linesA $DELQ (2*$MB)
$wA = Wheeler $CA $MB
$gA = GaussNodes $wA[0] $wA[1] $MB
"  RECOVERED by Gauss quadrature on the exact lagged-cumulant tower:"
$recA = @()
for($j=0;$j -lt $MB;$j++){
  $nd=$gA[0][$j]
  if([math]::Abs($nd) -le 1.0){ $recA += ,@(([math]::Acos($nd)/$DELQ), $gA[1][$j]) }
}
foreach($rr in ($recA | Sort-Object { $_[0] })){ ("    omega = {0,10:0.########}   w = {1:e3}" -f $rr[0], $rr[1]) }
"  (node-by-node match to the Bohr transition energies AND intensities:"
"   the lagged-cumulant tower IS the spectroscope)"

# windowed-estimate route (a 'measured' finite time series): with noisy
# moments only the DOMINANT structure is recoverable -- ask for 6 nodes
$sigA = LinesToSignal $linesA $DELQ 1200
$nW=6
$CAw = AutoCov $sigA (2*$nW-1)
$wAw = Wheeler $CAw $nW
$gAw = GaussNodes $wAw[0] $wAw[1] $nW
$omsW=@()
for($j=0;$j -lt $nW;$j++){ $nd=$gAw[0][$j]; if([math]::Abs($nd) -le 1.0){ $omsW += ([math]::Acos($nd)/$DELQ) } }
$omsW = @($omsW | Sort-Object)
"  windowed estimate (1200 samples, 6 nodes): omegas = $(($omsW | ForEach-Object { '{0:0.####}' -f $_ }) -join ', ')"
"  (a finite window compresses the ladder onto its dominant transitions)"

# harmonic: ALL gaps equal -> ONE atom -> rank 1
$CH = LinesToC $linesH $DELQ 12
$wH = Wheeler $CH 6
"  HARMONIC (lam=0): beta sequence: $((1..4 | ForEach-Object { '{0:e2}' -f $wH[1][$_] }) -join '  ')"
"    beta_1 ~ 0: the spectral measure is ONE atom although MANY transitions"
"    participate -- all Bohr gaps coincide. Rank 1 of the autocovariance"
"    Hankel = evenly spaced ladder = why classical SHM has one frequency."
"    Anharmonicity IS the Hankel rank climbing above 1."

# ============================================================
# (C) emit data + references for examples 17 and 18
# ============================================================
$invc=[System.Globalization.CultureInfo]::InvariantCulture

""
"==================================================================="
"(C1) EXAMPLE 17 DATA: HH persistence via STRIDED pair-sampling"
# The long-lag persistence needs the statistical power of the whole series,
# but the whole series is too big to embed. Strided pair-sampling fixes it:
# row0 = u[0::10] (310 samples spanning the full 800 time units) and rows
# k = u[lag_k::10]; the covariance of row0 with row k is a full-span
# estimator of C(lag_k). Blade's cumulants() on the like Idx<4> array gives
# the whole battery in one former call.
$PSTRIDE=10; $PROWL=310
$PLAGS=@(0,800,840,880)   # tau = 0, 160, 168, 176
function EmitStridedRow($xs, $off, $strideS, $rowLen) {
  $vals=@()
  for($i=0;$i -lt $rowLen;$i++){ $vals += $xs[$off+$i*$strideS].ToString("G12",$invc) }
  ,($vals -join ', ')
}
function StridedCov($xs, $offA, $offB, $strideS, $rowLen) {
  $mA=0.0;$mB=0.0
  for($i=0;$i -lt $rowLen;$i++){ $mA+=$xs[$offA+$i*$strideS]; $mB+=$xs[$offB+$i*$strideS] }
  $mA/=$rowLen; $mB/=$rowLen
  $cv=0.0
  for($i=0;$i -lt $rowLen;$i++){ $cv+=($xs[$offA+$i*$strideS]-$mA)*($xs[$offB+$i*$strideS]-$mB) }
  $cv/$rowLen
}
foreach($sys in @(@("REG",$regU),@("CHA",$chaU))) {
  $tag=$sys[0]; $xs=$sys[1]
  $rn=0
  foreach($lg in $PLAGS){
    ("let {0}{1} = [{2}]" -f $tag, $rn, (EmitStridedRow $xs $lg $PSTRIDE $PROWL))
    $rn++
  }
  $c0 = StridedCov $xs 0 0 $PSTRIDE $PROWL
  $ca = StridedCov $xs 0 $PLAGS[1] $PSTRIDE $PROWL
  $cb = StridedCov $xs 0 $PLAGS[2] $PSTRIDE $PROWL
  $cc = StridedCov $xs 0 $PLAGS[3] $PSTRIDE $PROWL
  $pr = [math]::Sqrt(($ca*$ca+$cb*$cb+$cc*$cc)/3.0)/$c0
  ("  {0}: C(0)={1:0.########}  C(160)={2:0.######} C(168)={3:0.######} C(176)={4:0.######}  persistence={5:0.####}" -f $tag,$c0,$ca,$cb,$cc,$pr)
}
"  (regular: long-lag persistence O(1) = discrete spectrum = torus: the 2nd"
"   invariant EXISTS though no degree-4 polynomial writes it -- the same"
"   trajectory class where the polynomial null space went blind;"
"   chaotic: collapse = continuous spectrum = mixing)"

""
"==================================================================="
"(C2) EXAMPLE 18 DATA: exact quantum lagged-cumulant towers (Delta=$DELQ)"
$CH10 = LinesToC $linesH $DELQ 10
$CA10 = LinesToC $linesA $DELQ 10
("let CH = [{0}]" -f (($CH10 | ForEach-Object { $_.ToString("G15",$invc) }) -join ', '))
("let CA = [{0}]" -f (($CA10 | ForEach-Object { $_.ToString("G15",$invc) }) -join ', '))
# references the Blade example should reproduce:
foreach($sys in @(@("harmonic",$CH10),@("anharmonic",$CA10))) {
  $tag=$sys[0]; $Cr=$sys[1]
  $pm = PowMoms $Cr
  $det2=$pm[0]*$pm[2]-$pm[1]*$pm[1]
  $det3=$pm[0]*($pm[2]*$pm[4]-$pm[3]*$pm[3])-$pm[1]*($pm[1]*$pm[4]-$pm[3]*$pm[2])+$pm[2]*($pm[1]*$pm[3]-$pm[2]*$pm[2])
  $d2n=$det2/($pm[0]*$pm[0]); $d3n=$det3/($pm[0]*$pm[0]*$pm[0])
  # rank-1 readout: lambda = m1/m0 -> omega
  $lamr=$pm[1]/$pm[0]; $omr=[math]::Acos($lamr)/$DELQ
  # 2-atom Prony from m0..m3: solve [m0 m1; m1 m2][t0;t1]=[m2;m3]
  $den=$pm[0]*$pm[2]-$pm[1]*$pm[1]
  $t0=($pm[2]*$pm[2]-$pm[1]*$pm[3])/$den
  $t1=($pm[0]*$pm[3]-$pm[1]*$pm[2])/$den
  $disc=[math]::Sqrt($t1*$t1+4.0*$t0)
  $lA=0.5*($t1+$disc); $lB=0.5*($t1-$disc)
  $omA=[math]::Acos([math]::Max(-1.0,[math]::Min(1.0,$lA)))/$DELQ
  $omB=[math]::Acos([math]::Max(-1.0,[math]::Min(1.0,$lB)))/$DELQ
  ("  {0}: det2n={1:e3}  det3n={2:e3}  rank1-omega={3:0.########}" -f $tag,$d2n,$d3n,$omr)
  ("  {0}: 2-atom Prony omegas = {1:0.########}, {2:0.########}" -f $tag,$omB,$omA)
}
("  harmonic exact omega = 1 (all Bohr gaps equal); cos(1*Delta) = {0:0.###############}" -f ([math]::Cos($DELQ)))
("  anharmonic dominant gaps: D0 = {0:0.########}, D1 = {1:0.########}" -f ($EvA[1]-$EvA[0]), ($EvA[2]-$EvA[1]))

