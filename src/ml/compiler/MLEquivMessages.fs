/// THE ENGINE'S FAILURE TEXT, OWNED IN ONE PLACE.
///
/// These four constructors render what the polynomial ENGINE found: a
/// coefficient mismatch at a group element (`failureMessage`), the same at an
/// so(3) generator (`lieFailureMessage`), a pi-0 parity error at the inversion
/// (`inversionFailureMessage`), and the note that says the engine never ran
/// (`capNote`). They are pure functions of `MLPolyExtract.DischargeFailure` /
/// `MLLieDischarge.LieFailure` / `MLLieDischarge.InversionFailure` and a
/// function name, and nothing else.
///
/// WHY THEY LIVE HERE AND NOT IN `MLEquiv.Engine`. Two front halves feed the
/// same two dischargers: the SEAM extractor (`MLPolyExtract`, surface `Expr`,
/// judged by `MLEquiv.judgeFunction`) and the TYPED extractor
/// (`MLPolyExtractTyped`, post-elaboration `TypedExpr`, judged inside
/// `DeduceRep.checkDeclaredRep`). Both reach the SAME failure records, and both
/// have to say the same thing about them — the seam because that is the text
/// the user reads, the typed side because its disagreement report is compared
/// against the seam's, pin for pin, by `blade test rep-reject`.
///
/// C2 originally declined to duplicate this text into `MLPolyExtractTyped` and
/// wrote a shorter internal form instead, precisely because a hand-copied
/// diagnostic drifts. That instinct was right about duplication and wrong about
/// the remedy: the three seam cert walkers in this codebase have already
/// drifted once, and the fix for "two copies drift" is one copy, not two copies
/// of different lengths. So the long form moved HERE, verbatim, and both sides
/// call it. The seam's rendered output is unchanged byte for byte (the
/// `tests/corpus/diagnostics` BL4008 pins are the gate); the typed side's got
/// longer, which is the point.
///
/// Position: after both dischargers (whose failure records it destructures) and
/// before `MLPolyExtractTyped`, which is the earliest consumer.
///
/// NOTHING GROUP-SPECIFIC BELONGS HERE beyond the group NAME that is passed in.
/// The message text is equiv's vocabulary; galilean and perm keep their own,
/// by decision (retired discipline-as-data design note) — this module is shared
/// between two FRONT HALVES of one discipline, not between disciplines.
module Blade.ML.EquivMessages

module PX = Blade.ML.PolyExtract
module LD = Blade.ML.LieDischarge

/// How a coefficient mismatch reads. The near-miss note is §3.5's
/// mandatory one and points at BOTH escape hatches; the rep-degree-0 arm is
/// the constant obligation (a constant term of an equivariant map must be
/// fixed by the whole group — trivial-label supported and π₀-fixed).
let failureMessage (funcName: string) (gn: string) (f: PX.DischargeFailure) : string =
    let where =
        sprintf "function '%s': the body IS a polynomial, and it is not %s-equivariant. The identity f(rho(g) x) = rho(g) f(x) fails at group element %s, in output component %d, at the term %s: the left side has coefficient %s, the right side %s"
            funcName gn f.Element f.Component f.Monomial (PX.Rat.render f.Lhs) (PX.Rat.render f.Rhs)
    let constantNote =
        if f.RepDegree = 0 then
            sprintf ". That term is a CONSTANT: a constant summand of an equivariant map must be fixed by the whole group, i.e. supported on trivial-label cells (every generator matrix the identity) and unmoved by the component group. A constant in a non-trivial-label cell breaks equivariance no matter what the rest of the body does"
        else ""
    let nearMissNote =
        if f.NearMiss then
            sprintf ". NEAR MISS: the residual is %g, negligible against the coefficients — this is the truncated-decimal trap. Coefficients are read EXACTLY AS WRITTEN (a float literal is its exact dyadic value, so 0.3 is not 3/10), while a literal DIVISION is evaluated exactly in the rationals. If you meant an exact rational, write the division (3.0 / 10.0 IS 3/10); if the coefficient is genuinely irrational, no exact checker can certify it — build the layer with the synthesized basis (ml.derive_pg_linear), whose Schur certificate covers precisely that case"
                (abs (PX.Rat.toFloat (PX.Rat.sub f.Lhs f.Rhs)))
        else ""
    where + constantNote + nearMissNote

/// The Lie-generator twin of `failureMessage`. Same family, same near-miss
/// note, three differences forced by the group: the failing object is a
/// GENERATOR rather than a group element, the coefficients are RADICAL
/// VECTORS (rendered componentwise, since componentwise-zero is the
/// acceptance test), and the synthesized-basis escape hatch names the
/// O(3) formers rather than the point-group one.
let lieFailureMessage (funcName: string) (gn: string) (f: LD.LieFailure) : string =
    let where =
        sprintf "function '%s': the body IS a polynomial, and it is not %s-equivariant. The identity Df(x)(A x) = A f(x) fails at Lie generator %s, in output component %d, at the term %s: the left side has coefficient %s, the right side %s"
            funcName gn f.Generator f.Component f.Monomial (LD.Radical.render f.Lhs) (LD.Radical.render f.Rhs)
    let residualNote =
        sprintf ". The residual's nonzero radical components: %s (acceptance is componentwise zero over the rationals — every generator entry is q*sqrt(n), and no product of two irrationals ever occurs, so this is exact)"
            (LD.Radical.render (LD.Radical.sub f.Lhs f.Rhs))
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

/// The π₀ half: the map commutes with every INFINITESIMAL rotation and
/// still fails at the inversion. That is not a coefficient slip, it is a
/// declared-parity error, and there are exactly two honest repairs — which
/// is why this message names both.
let inversionFailureMessage (funcName: string) (f: LD.InversionFailure) : string =
    let par p = if p = 1 then "odd" else "even"
    sprintf "function '%s': the body commutes with every generator of so(3), so it IS SO(3)-equivariant - but it is not O3-equivariant. The inversion identity f(-x) = rho(-I) f(x) fails in output component %d, at the term %s: that monomial is %s under -I (its rep factors contribute %d odd parities, so it picks up (-1)^%d) while the declared output component is %s. Under equiv(O3) the component group is the whole remaining obligation: either declare the parities that make the map a genuine O(3) map (a product of an odd number of odd factors is a pseudo-quantity - the triple product u.(v x w) of three vectors is an (l = 0, ODD) pseudoscalar, not a scalar), or weaken the certificate to `where ml.equiv(SO3)`, under which this body passes as written"
        funcName f.Component f.Monomial (par f.MonoParity) f.ParitySum f.ParitySum (par f.OutParity)

/// The cap note appended to the surfacing composition diagnostic.
let capNote (funcName: string) (why: string) : string =
    sprintf " [the equivariance engine did not run on '%s': %s (retired transforms-as-types plan section 7, stage 6 caps: degree <= %d, <= %d expanded terms); the verdict above is composition's]"
        funcName why PX.maxRepDegree PX.maxTerms
