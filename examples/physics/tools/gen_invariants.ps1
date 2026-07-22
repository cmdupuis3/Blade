# ============================================================================
# INVARIANT DISCOVERY via candidate generation.
#
# Enumerate a DICTIONARY of monomials in the state variables (plus the few
# non-polynomial terms a central force needs), fuzz the parameter, and form
# the covariance of the dictionary under the fuzzing. A linear combination c
# of the dictionary is CONSERVED iff Var(c . dictionary) = 0, i.e. iff c is in
# the NULL SPACE of that covariance. So number of conserved quantities =
# nullity; the null-space vectors ARE the invariants, in the monomial basis.
#
# A subtlety this exposes: fuzzing a SINGLE orbit finds everything constant on
# that one 1-D curve -- the true constants of motion AND kinematic identities
# (e.g. Kepler's velocity-hodograph circle). A genuine constant of motion is
# conserved along EVERY trajectory, so we sample SEVERAL orbits, center each
# separately, and stack: only combinations conserved on all of them survive.
# The orbit-specific identities (whose coefficients depend on eccentricity)
# drop out, leaving energy, angular momentum, and -- at degree 3 -- the
# Laplace-Runge-Lenz vector. No conjectures supplied, only "all monomials up
# to degree D".  (Kepler: p = L^2 = 1, GM = 1, orbits indexed by eccentricity.)
# ============================================================================

# Several distinct orbits: vary BOTH eccentricity e AND angular momentum L, so
# no per-orbit quantity (like Lz) is accidentally constant across the family.
$orbits = @( @(0.30, 0.80), @(0.45, 1.00), @(0.60, 1.20), @(0.70, 0.90), @(0.50, 1.10) )
$phiN   = 41
$phis = @()
for ($i = 0; $i -lt $phiN; $i++) {
  $t = 0.25 + 2.4 * ($i / ($phiN - 1.0))
  $phis += ($t + 0.02 * [math]::Sin(7.0 * $t))    # mild jitter, avoids trig aliasing
}

# State at anomaly p on the Kepler orbit (e, L), GM = 1: r = L^2/(1 + e cos p),
# vx = -sin p / L, vy = (e + cos p)/L, position direction (cos p, sin p).
function State($p, $ecc, $L) {
  $c = [math]::Cos($p); $s = [math]::Sin($p)
  $invr = (1.0 + $ecc * $c) / ($L * $L)
  [pscustomobject]@{ x = $c / $invr; y = $s / $invr; vx = -$s / $L; vy = ($ecc + $c) / $L
                     invr = $invr; xr = $c; yr = $s }
}

# Monomials x^a y^b vx^c vy^d with 1 <= a+b+c+d <= deg (skip the constant).
function Monomials($deg) {
  $out = @()
  for ($a = 0; $a -le $deg; $a++) {
   for ($b = 0; $b -le $deg - $a; $b++) {
    for ($cc = 0; $cc -le $deg - $a - $b; $cc++) {
     for ($dd = 0; $dd -le $deg - $a - $b - $cc; $dd++) {
       if ($a + $b + $cc + $dd -ge 1) {
         $name = ""
         if ($a -gt 0) { $name += "x$(if($a-gt1){'^'+$a})" }
         if ($b -gt 0) { $name += " y$(if($b-gt1){'^'+$b})" }
         if ($cc -gt 0) { $name += " vx$(if($cc-gt1){'^'+$cc})" }
         if ($dd -gt 0) { $name += " vy$(if($dd-gt1){'^'+$dd})" }
         $out += [pscustomobject]@{ a=$a; b=$b; c=$cc; d=$dd; name=$name.Trim() }
       }
     }}}}
  $out
}

function DictNames($deg, $withRhat) {
  $names = @(); foreach ($m in (Monomials $deg)) { $names += $m.name }
  $names += "1/r"; if ($withRhat) { $names += "x/r"; $names += "y/r" }
  $names
}

# Evaluate the dictionary at (p, e, L); returns a k-vector (same order as DictNames).
function DictRow($p, $ecc, $L, $deg, $withRhat) {
  $st = State $p $ecc $L
  $row = @()
  foreach ($m in (Monomials $deg)) {
    $row += [math]::Pow($st.x,$m.a) * [math]::Pow($st.y,$m.b) * [math]::Pow($st.vx,$m.c) * [math]::Pow($st.vy,$m.d)
  }
  $row += $st.invr; if ($withRhat) { $row += $st.xr; $row += $st.yr }
  $row
}

# Build the stacked, PER-ORBIT-CENTERED data matrix (rows = samples).
function BuildCentered($deg, $withRhat) {
  $k = (DictNames $deg $withRhat).Count
  $allRows = New-Object System.Collections.ArrayList
  foreach ($orbit in $orbits) {
    $ecc = $orbit[0]; $L = $orbit[1]
    # evaluate this orbit's rows
    $orb = @()
    foreach ($p in $phis) { $orb += ,(DictRow $p $ecc $L $deg $withRhat) }
    # column means for THIS orbit
    $means = New-Object 'double[]' $k
    for ($j = 0; $j -lt $k; $j++) { $mu = 0.0; foreach ($rw in $orb) { $mu += $rw[$j] }; $means[$j] = $mu / $orb.Count }
    # center and append
    foreach ($rw in $orb) {
      $cr = New-Object 'double[]' $k
      for ($j = 0; $j -lt $k; $j++) { $cr[$j] = $rw[$j] - $means[$j] }
      [void]$allRows.Add($cr)
    }
  }
  [pscustomobject]@{ rows = $allRows; k = $k }
}

# Null space of a centered row-matrix via RREF ($R[$i][$j], jagged).
function NullSpaceRows($rows, $k, $tol) {
  $n = $rows.Count
  $R = New-Object 'object[]' $n
  for ($i = 0; $i -lt $n; $i++) { $R[$i] = $rows[$i].Clone() }
  $pivotCol = @(); $prow = 0
  for ($col = 0; $col -lt $k -and $prow -lt $n; $col++) {
    $best = $prow; $bestv = [math]::Abs($R[$prow][$col])
    for ($ri = $prow + 1; $ri -lt $n; $ri++) { $av = [math]::Abs($R[$ri][$col]); if ($av -gt $bestv) { $bestv = $av; $best = $ri } }
    if ($bestv -lt $tol) { continue }
    if ($best -ne $prow) { $tmp = $R[$prow]; $R[$prow] = $R[$best]; $R[$best] = $tmp }
    $piv = $R[$prow][$col]; for ($j = 0; $j -lt $k; $j++) { $R[$prow][$j] = $R[$prow][$j] / $piv }
    for ($ri = 0; $ri -lt $n; $ri++) {
      if ($ri -ne $prow) { $f = $R[$ri][$col]; if ($f -ne 0.0) { for ($j = 0; $j -lt $k; $j++) { $R[$ri][$j] = $R[$ri][$j] - $f * $R[$prow][$j] } } }
    }
    $pivotCol += $col; $prow++
  }
  $rank = $pivotCol.Count
  $free = @(); for ($c = 0; $c -lt $k; $c++) { if ($pivotCol -notcontains $c) { $free += $c } }
  $basis = @()
  foreach ($fcol in $free) {
    $v = New-Object 'double[]' $k
    $v[$fcol] = 1.0
    for ($pi = 0; $pi -lt $rank; $pi++) { $v[$pivotCol[$pi]] = -$R[$pi][$fcol] }
    $basis += ,$v
  }
  [pscustomobject]@{ rank = $rank; nullity = ($k - $rank); basis = $basis }
}

function Show-Invariant($vec, $names, $tol) {
  $terms = @()
  for ($j = 0; $j -lt $names.Count; $j++) {
    if ([math]::Abs($vec[$j]) -gt $tol) { $terms += ("{0:+0.###;-0.###}*[{1}]" -f $vec[$j], $names[$j]) }
  }
  ($terms -join "  ")
}

foreach ($deg in @(2, 3)) {
  $withRhat = ($deg -ge 3)
  $names = DictNames $deg $withRhat
  $bc = BuildCentered $deg $withRhat
  $ns = NullSpaceRows $bc.rows $bc.k 1e-8
  ""
  "==================================================================="
  "Degree-$deg sweep: $($bc.k) candidate functions, $($orbits.Count) orbits x $phiN samples"
  "  rank $($ns.rank)  =>  DISCOVERED $($ns.nullity) constant(s) of motion:"
  $idx = 1
  foreach ($v in $ns.basis) { "   [$idx]  " + (Show-Invariant $v $names 1e-6); $idx++ }
}
