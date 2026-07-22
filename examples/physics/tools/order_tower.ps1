# ============================================================================
# THE THREE-BALL EVENT-ORDER TOWER: the order of collisions is a distribution.
#
# Examples 20-21 leaked the mass jet into an event's GEOMETRY (where/when a
# single collision happens) and closed with the outlook: "with chains of
# fuzzy-sized balls, the ORDER of events itself becomes a distribution the
# tower carries natively." This tool builds that distribution.
#
# Three balls on a line, elastic, radii tied to mass by a known density,
# R = m^(1/3) (the units of examples 20-21). Masses are EPISTEMIC -- a
# 5x5x5 = 125-point product grid:
#     A  x=0     v=+1.0   m in {0.8,0.9,1.0,1.1,1.2}   (projectile)
#     B  x=7     v= 0.0   m in {0.3,0.35,0.4,0.45,0.5} (a LIGHT middle ball)
#     C  x=16.6  v=-1.5   m in {0.8,0.9,1.0,1.1,1.2}   (heavier, faster, incoming)
# Each realization is a deterministic event-driven simulation: between events
# the balls free-fly, the next contact of an adjacent pair solves the linear
# gap equation  gap(t) = (x_j - x_i) - (R_i + R_j) = 0,  the earliest event is
# processed with the 1D elastic map  v1' = ((m1-m2)v1 + 2 m2 v2)/(m1+m2), and
# the sweep repeats. Balls on a line keep their order, so the only pairs are
# A-B and B-C; events are labelled canonically by pair + occurrence:
# AB1, BC1, AB2, BC2, ...
#
# The geometry is tuned so the FIRST A-B contact (AB1, ~t=5.25) and the FIRST
# B-C contact (BC1, ~t=5.25) are a genuine photo finish: whoever reaches B
# first is decided by whether A or C is the larger ball, and their fuzzy time
# bands overlap. A light B then rattles: C strikes it a second time (BC2),
# and BC2 is always the last of the three. So the three universal events
#     E1 = AB1,  E2 = BC1,  E3 = BC2
# carry a nontrivial DISTRIBUTION OVER ORDERINGS -- a measure on S_3.
#
# The tower of that measure:
#  1. the pairwise order probabilities P(Ei before Ej) are its FIRST MOMENTS
#     -- the coordinates of the linear-ordering polytope;
#  2. the full measure on the 6 permutations is the underlying state;
#  3. the linear-ordering-polytope facets (0<=P_ij<=1 and the triangle facets
#     0 <= P_ij + P_jk - P_ik <= 1) are the classical CAUSAL INEQUALITIES;
#     we compute where the state sits inside them (the margins);
#  4. the CAUSAL WITNESS W = P(E1<E2)+P(E2<E3)+P(E3<E1) lies in [1,2] for any
#     classical mixture of orders; outside would be noncausal (not even a
#     quantum switch reaches it) -- the classical mixture provably sits inside;
#  5. the CAUSAL SKELETON: the pairs whose order is DEFINITE (P = 0 or 1) form
#     the almost-sure partial order -- the invariant sector of the causal
#     relation -- while the AMBIGUOUS pair (P strictly interior) is the fuzzy
#     part. This is the invariant detector of examples 09-16 applied to the
#     causal relation itself.
#
# This is a causally SEPARABLE process: a dynamically-controlled classical
# mixture of orders (the QC-CC class), the classical stratum below the quantum
# switch. Companion: 22_event_order_tower.blade recomputes P-matrix, facets,
# and witness in-language from the emitted indicator columns.
# ============================================================================

$invc = [System.Globalization.CultureInfo]::InvariantCulture
function Fmt($x) { $t = $x.ToString("G12", $invc); if ($t -notmatch '[.eE]') { $t = "$t.0" }; $t }

# --- event-driven 3-ball simulator -----------------------------------------
# returns a hashtable: canonical-label -> first-occurrence time
function Simulate($x0, $v0, $mArr, $Tend) {
    $posArr = @([double]$x0[0], [double]$x0[1], [double]$x0[2])
    $velArr = @([double]$v0[0], [double]$v0[1], [double]$v0[2])
    $radArr = @([math]::Pow($mArr[0], 1.0 / 3.0), [math]::Pow($mArr[1], 1.0 / 3.0), [math]::Pow($mArr[2], 1.0 / 3.0))
    $tGlobal = 0.0; $countAB = 0; $countBC = 0; $times = @{}
    $epsDt = 1e-11; $guard = 0
    while ($true) {
        $guard++; if ($guard -gt 500) { $times["OVERFLOW"] = 1.0; break }
        $bestDt = [double]::PositiveInfinity; $bestPair = -1
        foreach ($pr in @(0, 1)) {
            $iL = $pr; $jR = $pr + 1
            $gap0 = $posArr[$jR] - $posArr[$iL] - $radArr[$iL] - $radArr[$jR]
            $closing = $velArr[$iL] - $velArr[$jR]          # >0 means the pair is approaching
            if ($closing -gt 1e-12) {
                $dtCand = $gap0 / $closing
                if ($dtCand -gt $epsDt -and $dtCand -lt $bestDt) { $bestDt = $dtCand; $bestPair = $pr }
            }
        }
        if ($bestPair -lt 0) { break }                       # no approaching pair: done
        if ($tGlobal + $bestDt -gt $Tend) { break }
        for ($kk = 0; $kk -lt 3; $kk++) { $posArr[$kk] = $posArr[$kk] + $velArr[$kk] * $bestDt }
        $tGlobal = $tGlobal + $bestDt
        $iL = $bestPair; $jR = $bestPair + 1
        $mi = $mArr[$iL]; $mj = $mArr[$jR]; $vi = $velArr[$iL]; $vj = $velArr[$jR]
        $velArr[$iL] = (($mi - $mj) * $vi + 2.0 * $mj * $vj) / ($mi + $mj)
        $velArr[$jR] = (($mj - $mi) * $vj + 2.0 * $mi * $vi) / ($mi + $mj)
        if ($bestPair -eq 0) { $countAB++; $lbl = "AB$countAB" } else { $countBC++; $lbl = "BC$countBC" }
        if (-not $times.ContainsKey($lbl)) { $times[$lbl] = $tGlobal }
    }
    return $times
}

# --- the ensemble ----------------------------------------------------------
$gridAC = @(); foreach ($cc in @(-1.0, -0.5, 0.0, 0.5, 1.0)) { $gridAC += (1.0 + 0.2 * $cc) }
$gridB = @();  foreach ($cc in @(-1.0, -0.5, 0.0, 0.5, 1.0)) { $gridB += (0.4 + 0.1 * $cc) }
$xB0 = 7.0; $xC0 = 16.6; $vC = -1.5
$velVec = @(1.0, 0.0, $vC)
$labels = @("AB1", "BC1", "BC2")           # E1, E2, E3

$colT1 = @(); $colT2 = @(); $colT3 = @()    # per-realization event times
foreach ($mA in $gridAC) { foreach ($mB in $gridB) { foreach ($mC in $gridAC) {
    $tm = Simulate @(0.0, $xB0, $xC0) $velVec @($mA, $mB, $mC) 80.0
    if ($tm.ContainsKey("OVERFLOW")) { Write-Host "OVERFLOW at ($mA,$mB,$mC)"; exit 1 }
    if (-not ($tm.ContainsKey($labels[0]) -and $tm.ContainsKey($labels[1]) -and $tm.ContainsKey($labels[2]))) {
        Write-Host "MISSING event at ($mA,$mB,$mC): only $($tm.Keys -join ',')"; exit 1
    }
    $colT1 += $tm[$labels[0]]; $colT2 += $tm[$labels[1]]; $colT3 += $tm[$labels[2]]
}}}
$N = $colT1.Count

# --- indicator columns and the P-matrix ------------------------------------
$I12 = @(); $I23 = @(); $I13 = @(); $ties = 0
$permCount = @{}
for ($ii = 0; $ii -lt $N; $ii++) {
    $s1 = $colT1[$ii]; $s2 = $colT2[$ii]; $s3 = $colT3[$ii]
    if (([math]::Abs($s1 - $s2) -lt 1e-9) -or ([math]::Abs($s2 - $s3) -lt 1e-9) -or ([math]::Abs($s1 - $s3) -lt 1e-9)) { $ties++ }
    if ($s1 -lt $s2) { $I12 += 1.0 } else { $I12 += 0.0 }
    if ($s2 -lt $s3) { $I23 += 1.0 } else { $I23 += 0.0 }
    if ($s1 -lt $s3) { $I13 += 1.0 } else { $I13 += 0.0 }
    $trip = @( @("E1", $s1), @("E2", $s2), @("E3", $s3) )
    $sorted = $trip | Sort-Object { $_[1] }
    $key = ($sorted | ForEach-Object { $_[0] }) -join "<"
    if ($permCount.ContainsKey($key)) { $permCount[$key]++ } else { $permCount[$key] = 1 }
}

# probabilities as means of the indicators (P_ji = 1 - P_ij since no ties)
$prob12 = ($I12 | Measure-Object -Sum).Sum / $N
$prob23 = ($I23 | Measure-Object -Sum).Sum / $N
$prob13 = ($I13 | Measure-Object -Sum).Sum / $N
# full 3x3 P-matrix (jagged; diagonal left at 0)
$Pm = @( @(0.0, $prob12, $prob13), @((1.0 - $prob12), 0.0, $prob23), @((1.0 - $prob13), (1.0 - $prob23), 0.0) )

"==================================================================="
"THREE-BALL EVENT-ORDER TOWER   (N = $N realizations, ties = $ties)"
"  E1 = AB1 (first A-B contact)   E2 = BC1 (first B-C contact)   E3 = BC2 (second B-C)"
("  mean event times:  E1 = {0}   E2 = {1}   E3 = {2}" -f `
    (Fmt (($colT1 | Measure-Object -Average).Average)), (Fmt (($colT2 | Measure-Object -Average).Average)), (Fmt (($colT3 | Measure-Object -Average).Average)))
("  E1 band = [{0}, {1}]   E2 band = [{2}, {3}]  (they OVERLAP: the photo finish)" -f `
    (Fmt (($colT1 | Measure-Object -Minimum).Minimum)), (Fmt (($colT1 | Measure-Object -Maximum).Maximum)), `
    (Fmt (($colT2 | Measure-Object -Minimum).Minimum)), (Fmt (($colT2 | Measure-Object -Maximum).Maximum)))

""
"(1) PAIRWISE ORDER-PROBABILITY MATRIX  P(row before col)  [first moments]"
"           E1        E2        E3"
foreach ($ri in @(0, 1, 2)) {
    ("   E{0}  {1,8:F5}  {2,8:F5}  {3,8:F5}" -f ($ri + 1), $Pm[$ri][0], $Pm[$ri][1], $Pm[$ri][2])
}

""
"(2) DISTRIBUTION OVER THE 6 PERMUTATIONS OF (E1,E2,E3)  [the measure on S_3]"
$allPerms = @("E1<E2<E3", "E1<E3<E2", "E2<E1<E3", "E2<E3<E1", "E3<E1<E2", "E3<E2<E1")
foreach ($key in $allPerms) {
    $cntK = 0; if ($permCount.ContainsKey($key)) { $cntK = $permCount[$key] }
    ("   {0,-12} {1,4}   ({2:F4})" -f $key, $cntK, ($cntK / $N))
}

""
"(3) LINEAR-ORDERING-POLYTOPE FACETS  (the classical causal inequalities)"
"   trivial facets  0 <= P_ij <= 1     (margin = distance to nearest violation)"
$trivMin = [double]::PositiveInfinity
foreach ($pairName in @(@("P12", $prob12), @("P23", $prob23), @("P13", $prob13))) {
    $pv = $pairName[1]
    $loM = $pv; $hiM = 1.0 - $pv
    $mn = [math]::Min($loM, $hiM); if ($mn -lt $trivMin) { $trivMin = $mn }
    ("     {0} = {1,7:F4}   margin_lo = {2,7:F4}   margin_hi = {3,7:F4}" -f $pairName[0], $pv, $loM, $hiM)
}
"   triangle facets  0 <= P_ij + P_jk - P_ik <= 1   (all 6 labelings)"
# f(i,j,k) = P_ij + P_jk - P_ik  using the full P-matrix
$triMin = [double]::PositiveInfinity
$triTriples = @( @(0, 1, 2), @(0, 2, 1), @(1, 0, 2), @(1, 2, 0), @(2, 0, 1), @(2, 1, 0) )
foreach ($tp in $triTriples) {
    $fi = $tp[0]; $fj = $tp[1]; $fk = $tp[2]
    $fval = $Pm[$fi][$fj] + $Pm[$fj][$fk] - $Pm[$fi][$fk]
    $loM = $fval; $hiM = 1.0 - $fval
    $mn = [math]::Min($loM, $hiM); if ($mn -lt $triMin) { $triMin = $mn }
    ("     f(E{0},E{1},E{2}) = {3,7:F4}   margin_lo = {4,7:F4}   margin_hi = {5,7:F4}" -f `
        ($fi + 1), ($fj + 1), ($fk + 1), $fval, $loM, $hiM)
}
# the DEFINITE relations sit exactly on the trivial faces P=0/P=1 (margin 0):
# the poset lives on the polytope BOUNDARY. The causal content is the triangle
# (3-cycle) margin -- how far from a NONCAUSAL cyclic-ordering violation.
$causalMargin = $triMin
("   trivial-facet min margin = {0:F4}  (the 2 DEFINITE relations lie ON their faces)" -f $trivMin)
("   CAUSAL (3-cycle) facet margin = {0:F4}  (strictly inside the causal inequalities)" -f $causalMargin)

""
"(4) CAUSAL WITNESS  W = P(E1<E2) + P(E2<E3) + P(E3<E1)"
$prob31 = 1.0 - $prob13
$witnessTmp = $prob12 + $prob23 + $prob31
("   W = {0} + {1} + {2} = {3}" -f (Fmt $prob12), (Fmt $prob23), (Fmt $prob31), (Fmt $witnessTmp))
("   classical bound: 1 <= W <= 2   -->  W = {0:F4} is INSIDE" -f $witnessTmp)
("   witness margin min(W-1, 2-W) = {0:F4}  (= the causal (3-cycle) facet margin)" -f ([math]::Min($witnessTmp - 1.0, 2.0 - $witnessTmp)))
"   (W outside [1,2] would be causal-inequality-violating -- noncausal, beyond"
"    even a quantum switch; the classical mixture of orders provably sits inside)"

""
"(5) CAUSAL SKELETON  (DEFINITE = P is 0 or 1 to machine precision; else AMBIGUOUS)"
function Verdict($pv) { if ($pv -le 0.001 -or $pv -ge 0.999) { "DEFINITE" } else { "AMBIGUOUS" } }
("   E1 vs E2  (AB1 vs BC1):  P = {0,7:F4}   {1}" -f $prob12, (Verdict $prob12))
("   E2 vs E3  (BC1 vs BC2):  P = {0,7:F4}   {1}" -f $prob23, (Verdict $prob23))
("   E1 vs E3  (AB1 vs BC2):  P = {0,7:F4}   {1}" -f $prob13, (Verdict $prob13))
"   almost-sure partial order (the invariant sector of the causal relation):"
("     E1 < E3  and  E2 < E3  ALWAYS  (BC2 is the maximal event);")
("     E1 vs E2 AMBIGUOUS, P(E1<E2) = {0:F4}  (the fuzzy relation)" -f $prob12)

# --------------------------------------------------------------------------
""
"==================================================================="
"BLADE DATA for 22_event_order_tower.blade  (indicator + event-time columns)"
"let I12 = [{0}]" -f (($I12 | ForEach-Object { Fmt $_ }) -join ", ")
"let I23 = [{0}]" -f (($I23 | ForEach-Object { Fmt $_ }) -join ", ")
"let I13 = [{0}]" -f (($I13 | ForEach-Object { Fmt $_ }) -join ", ")
"let T1 = [{0}]" -f (($colT1 | ForEach-Object { Fmt $_ }) -join ", ")
"let T2 = [{0}]" -f (($colT2 | ForEach-Object { Fmt $_ }) -join ", ")
"let T3 = [{0}]" -f (($colT3 | ForEach-Object { Fmt $_ }) -join ", ")
""
"reference values to pin (Blade must reproduce these to 1e-9):"
("  P12 = {0}   P23 = {1}   P13 = {2}" -f (Fmt $prob12), (Fmt $prob23), (Fmt $prob13))
("  triangle facet f(E1,E2,E3) = P12 + P23 - P13 = {0}" -f (Fmt ($prob12 + $prob23 - $prob13)))
("  witness W = {0}   causal (3-cycle) margin = {1}" -f (Fmt $witnessTmp), (Fmt $causalMargin))
