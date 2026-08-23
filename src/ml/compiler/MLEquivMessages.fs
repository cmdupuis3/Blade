/// THE ENGINE'S FAILURE TEXT, OWNED IN ONE PLACE.
///
/// Four constructors render what the polynomial ENGINE found: a coefficient
/// mismatch at a group element (`failureMessage`), the same at an so(3)
/// generator (`lieFailureMessage`), a pi-0 parity error at the inversion
/// (`inversionFailureMessage`), and the note that the engine never ran
/// (`capNote`). Pure functions of `MLPolyExtract.DischargeFailure` /
/// `MLLieDischarge.LieFailure` / `MLLieDischarge.InversionFailure` and a
/// function name, nothing else.
///
/// Lives here, not in `MLEquiv.Engine`, because two front halves feed the
/// same dischargers -- the SEAM extractor (`MLPolyExtract`, judged by
/// `MLEquiv.judgeFunction`) and the TYPED extractor (`MLPolyExtractTyped`,
/// judged inside `DeduceRep.checkDeclaredRep`) -- and must say the same
/// thing: the seam because that is the text the user reads, the typed side
/// because its disagreement report is compared against the seam's, pin for
/// pin, by `blade test rep-reject`.
///
/// The three seam cert walkers in this codebase have already drifted once;
/// a hand-copied diagnostic is exactly how that happens, so this is the one
/// copy both sides call, unchanged byte for byte on the seam side
/// (`tests/corpus/diagnostics` BL4008 pins are the gate).
///
/// Position: after both dischargers (whose failure records it destructures)
/// and before `MLPolyExtractTyped`, the earliest consumer. Nothing
/// group-specific belongs here beyond the group NAME passed in -- galilean
/// and perm keep their own message vocabulary.
module Blade.ML.EquivMessages

module PX = Blade.ML.PolyExtract
module LD = Blade.ML.LieDischarge

/// How a coefficient mismatch reads. The near-miss note is section 3.5's
/// mandatory one and points at BOTH escape hatches; the rep-degree-0 arm is
/// the constant obligation (a constant term of an equivariant map must be
/// fixed by the whole group -- trivial-label supported and pi-0-fixed).
let failureMessage (funcName: string) (gn: string) (f: PX.DischargeFailure) : string =
    let where =
        sprintf "function '%s': the body IS a polynomial, and it is not %s-equivariant. The identity f(rho(g) x) = rho(g) f(x) fails at group element %s, in output component %d, at the term %s: the left side has coefficient %s, the right side %s"
            funcName gn f.Element f.Component f.Monomial (PX.Rat.render f.Lhs) (PX.Rat.render f.Rhs)
    let constantNote =
        if f.RepDegree = 0 then
            ". That term is a CONSTANT: a constant summand of an equivariant map must be fixed by the whole group, i.e. supported on trivial-label cells (every generator matrix the identity) and unmoved by the component group. A constant in a non-trivial-label cell breaks equivariance no matter what the rest of the body does"
        else ""
    let nearMissNote =
        if f.NearMiss then
            sprintf ". NEAR MISS: the residual is %g, negligible against the coefficients -- this is the truncated-decimal trap. Coefficients are read EXACTLY AS WRITTEN (a float literal is its exact dyadic value, so 0.3 is not 3/10), while a literal DIVISION is evaluated exactly in the rationals. If you meant an exact rational, write the division (3.0 / 10.0 IS 3/10); if the coefficient is genuinely irrational, no exact checker can certify it -- build the layer with the synthesized basis (ml.derive_pg_linear), whose Schur certificate covers precisely that case"
                (abs (PX.Rat.toFloat (PX.Rat.sub f.Lhs f.Rhs)))
        else ""
    where + constantNote + nearMissNote

/// The Lie-generator twin of `failureMessage`: same family and near-miss
/// note, but the failing object is a GENERATOR, the coefficients are
/// RADICAL VECTORS (componentwise-zero is the acceptance test), and the
/// escape hatch names the O(3) formers rather than the point-group one.
let lieFailureMessage (funcName: string) (gn: string) (f: LD.LieFailure) : string =
    let where =
        sprintf "function '%s': the body IS a polynomial, and it is not %s-equivariant. The identity Df(x)(A x) = A f(x) fails at Lie generator %s, in output component %d, at the term %s: the left side has coefficient %s, the right side %s"
            funcName gn f.Generator f.Component f.Monomial (LD.Radical.render f.Lhs) (LD.Radical.render f.Rhs)
    let residualNote =
        $". The residual's nonzero radical components: {(LD.Radical.render (LD.Radical.sub f.Lhs f.Rhs))} (acceptance is componentwise zero over the rationals -- every generator entry is q*sqrt(n), and no product of two irrationals ever occurs, so this is exact)"
    let constantNote =
        if f.RepDegree = 0 then
            ". That term is a CONSTANT: a constant summand of an equivariant map must be fixed by the whole group, i.e. supported on (l = 0, even) cells. A constant in an l > 0 block breaks equivariance no matter what the rest of the body does"
        else ""
    let nearMissNote =
        if f.NearMiss then
            sprintf ". NEAR MISS: the residual is %g, negligible against the coefficients - this is the truncated-decimal trap. Coefficients are read EXACTLY AS WRITTEN (a float literal is its exact dyadic value, so 0.3 is not 3/10), while a literal DIVISION is evaluated exactly in the rationals. If you meant an exact rational, write the division (3.0 / 10.0 IS 3/10); if the coefficient is genuinely irrational, no exact checker can certify it - build the layer with the synthesized basis (ml.derive_linear / ml.derive_tp / ml.derive_poly), whose Schur certificate covers precisely that case"
                (abs (LD.Radical.toFloat (LD.Radical.sub f.Lhs f.Rhs)))
        else ""
    where + residualNote + constantNote + nearMissNote

/// The pi-0 half: the map commutes with every INFINITESIMAL rotation and
/// still fails at the inversion. That is not a coefficient slip, it is a
/// declared-parity error, and there are exactly two honest repairs, which
/// is why this message names both.
let inversionFailureMessage (funcName: string) (f: LD.InversionFailure) : string =
    let par p = if p = 1 then "odd" else "even"
    sprintf "function '%s': the body commutes with every generator of so(3), so it IS SO(3)-equivariant - but it is not O3-equivariant. The inversion identity f(-x) = rho(-I) f(x) fails in output component %d, at the term %s: that monomial is %s under -I (its rep factors contribute %d odd parities, so it picks up (-1)^%d) while the declared output component is %s. Under equiv(O3) the component group is the whole remaining obligation: either declare the parities that make the map a genuine O(3) map (a product of an odd number of odd factors is a pseudo-quantity - the triple product u.(v x w) of three vectors is an (l = 0, ODD) pseudoscalar, not a scalar), or weaken the certificate to `where ml.equiv(SO3)`, under which this body passes as written"
        funcName f.Component f.Monomial (par f.MonoParity) f.ParitySum f.ParitySum (par f.OutParity)

/// The cap note appended to the surfacing composition diagnostic.
let capNote (funcName: string) (why: string) : string =
    $" [the equivariance engine did not run on '{funcName}': {why} (caps: degree <= {PX.maxRepDegree}, <= {PX.maxTerms} expanded terms); the verdict above is composition's]"
