# Exact recurrence reduction: proof drafts and research handoff

**Status 2026-09-19: PAPER PROOF DRAFTS, PARTLY MACHINE-CHECKED the same day
(section 15: P1-P3, P4, P5a, P9 in full; P4a, P6, P7 and P10 in full in their
POLYNOMIAL forms; P7a and P7b over Z by Descartes' rule; P8's refusal for pure
powers at every degree; 218 axiom-free theorems in `proofs/BladeSummary.v`,
`BladeMomentClosure.v`, `BladeRankBound.v`, `BladeDescartes.v` and
`BladeRankDomain.v`. P6, P4a and P7 against DIFFERENTIABLE summaries are
proved too, but OUTSIDE the axiom-free tower: 22 theorems in
`proofs/reals/BladeSmoothRank.v`, conditional on two axioms of Coq's real
numbers). No compiler implementation. No claim of mathematical priority.**

Requested as a handoff for Claude. The proofs below are intended to be audited,
tightened, and then selectively mechanized. Sections 1-14 are the draft as
handed over; section 15 is the audit and records exactly which statements
entered the checked theorem count. Section 12 identifies the research claim that remains
open; the basic reduction, algebra, and observability arguments are established
mathematical techniques, specialized here to a possible Blade contract.

## 0. What this would enable, in one paragraph

Recursive arrays already express recurrences beyond physical time, including
dynamic programs. This proposal would let Blade prove that a large recursive
state can be replaced by a smaller summary without changing the results the
program requests. For example, a million evolving particle values might be
replaced by their sum and sum of squares if those two totals determine their
own next values and every requested output. The same principle applies to
finite tree and dependency-graph computations. Blade would derive the smaller
computation when it has a proof, explain an obstruction when it can prove one,
and otherwise retain the original computation. Supported derivatives could be
preserved as well.

### Relation to the repository

- [Recursive arrays](../formalism.md#75-recursive-arrays) already give an
  explicit inductive structure. The initial target below is a first-order,
  pure, total recurrence, not every possible prefix-consuming `let rec`.
- [The proof map](../proofs.md) contains symmetry, layout, enumeration, and
  symbolic AD results. This document does not imply that surface compilation
  or real-analysis AD correctness has already been proved.
- [PPL order accounting](../features/ppl.md#4-order-accounting) already tracks
  the growth of required moments under polynomial transformations. Sections 8
  and 9 develop refusal proofs rather than silently closing that hierarchy.
- [World models](../plans/plan-world-models.md) discusses fitted dynamics and
  moment limitations. Here the source recurrence is given and reductions are
  exact identities, rather than fitted laws.
- [Uniform optimality](../plans/plan-uniform-optimality.md) concerns work under
  an oracle/law contract. The reductions here use additional algebraic facts
  and an explicit observation contract; their savings do not refute that bound.

## 1. Mathematical contract

Let X be a microscopic state set, Z a summary state set, U a set of controls
or external inputs, and Y an observation set. A first-order recurrence has

    F : U x X -> X                 microscopic update
    h : X -> Y                     requested observation
    q : X -> Z                     summary construction
    G : U x Z -> Z                 proposed summary update
    h_bar : Z -> Y                 summary observation.

Parameters theta can be fixed initially and then made explicit in Section 6.
If an update depends on the step ordinal, include that ordinal in U. For each
input word w = [u_0, ..., u_(t-1)], write F_w for chronological execution:
F_[] = id and F_(w ++ [u]) = F_u composed with F_w; likewise for G_w.

A **reduction certificate** consists of the two identities

    q(F_u(x)) = G_u(q(x))                 for every admissible x and u,  (C1)
    h(x)      = h_bar(q(x))               for every admissible x.        (C2)

The state sets must be closed under the stated updates. A proof only on an
invariant subset X_adm is acceptable if initialization and preservation of
X_adm are also proved. Never infer an invariant subset from sampled trajectories.

The identities may quantify over every extent N, with families X_N, q_N, G_N.
An **extent-independent dimension** means dim Z_N is bounded by a constant
independent of N. The formula for G_N may still use N as a known parameter.
It does not mean that the initial state can be read in constant time.

Observations include every result that must be preserved. A consumer requesting
individual particles, a full intermediate array, or a convergence guard can
prevent a reduction that is valid for only a few final aggregates. Section 11
states the compiler and numerical obligations separately from the mathematics.

## 2. P1: preservation of every finite observed execution

**Theorem P1.** Under C1 and C2, for every x and every finite input word w,

    q(F_w(x)) = G_w(q(x)),
    h(F_w(x)) = h_bar(G_w(q(x))).

Consequently all prefix observations of any finite execution agree, not just
the final observation.

**Proof.** Induct on the length of w, using right extension of words. For the
empty word both state expressions equal q(x). If the claim holds for w, then

    q(F_(w ++ [u])(x))
      = q(F_u(F_w(x)))
      = G_u(q(F_w(x)))                  by C1
      = G_u(G_w(q(x)))                  by the induction hypothesis
      = G_(w ++ [u])(q(x)).

Apply C2 at F_w(x) to obtain the observation equality. Apply the same argument
to every prefix of w to obtain the trace equality. QED.

This is a semiconjugacy/induction argument. It is a foundation, not a novelty
claim. It does not assert that q is injective or that x can be reconstructed.

**Corollary P1a (feedback).** Suppose the input at a step is chosen by a fixed
deterministic policy from the previous observation history. If both executions
use that policy, their chosen inputs and observations agree at every step.

**Proof.** Simultaneously induct on the histories. Equal histories give equal
next controls; P1's induction step gives equal next observations. QED.

**Corollary P1b (guard and budget).** Let the decision to continue be b(x), and
suppose b = b_bar composed with q. With the same budget and guard-check
convention, both executions stop or exhaust the budget at the same step.

**Proof.** Their states remain q-related at each step by C1, so their guard
values are equal. Induct until the first false guard or budget exhaustion.
The same frozen-summary result follows if both semantics freeze after stopping.
QED. This is a guard-preservation theorem, not a derivative theorem at a
guard boundary; an actual BL8010 result must be part of the failure semantics.

## 3. P2: the same principle covers finite trees and dependency graphs

Use a finite acyclic graph V in a topological order. Each node v has an ordered
predecessor list pred(v), a state space X_v, and a total constructor

    K_v : product_(w in pred(v)) X_w -> X_v.

Leaves have no predecessors and thus a constant constructor, with any external
data treated as fixed arguments for the execution. Let q_v : X_v -> Z_v and
let K_bar_v operate on predecessor summaries. Require a local certificate

    q_v(K_v(x_1, ..., x_k))
      = K_bar_v(q_(w_1)(x_1), ..., q_(w_k)(x_k)).             (D1)

At every observed node, also require h_v = h_bar_v composed with q_v.

**Theorem P2.** Evaluating the graph with K_bar computes the q_v-summary of
the original value at every node, and preserves all designated observations.

**Proof.** Induct along the topological order. All predecessor summaries are
correct by the induction hypothesis. Substitute them into D1 to obtain the
correct summary at v. For a leaf the equation has no inductive premises. Apply
the observation equation at each observed node. Shared predecessors do not
change the proof: the graph is pure and each predecessor denotes one value.
QED.

This is the generalization beyond a time-shaped chain. A dynamic-programming
table fits only when its actual dependencies are included and the relevant
local equations are certified. A cyclic equation system is outside this theorem
unless separately given a well-founded unfolding or a fixed-point semantics.

**Concrete non-time example.** Consider a tree expression denoting a finite
sequence of scalars. Its constructors are a singleton, concatenation, and an
affine map of all elements. Summarize a sequence by

    q(xs) = (length(xs), sum(xs), sum(x*x for x in xs)).

The summary constructors are

    singleton(x)        -> (1, x, x*x)
    concatenate(z1,z2)  -> componentwise addition
    affine(a,b,(n,s,r)) -> (n, a*s+n*b, a*a*r+2*a*b*s+n*b*b).

Each local equation follows by finite-sum algebra. P2 therefore evaluates any
such tree using these summaries when its observations factor through them.
This establishes semantic elimination of intermediate sequences; a work saving
still depends on how many leaves and constructors the input representation has.

**Bounded-lag corollary.** A recurrence depending on k previous slices can be
written as a first-order recurrence on the k-slice window. P1 then applies to
that window state. Blade's initial zero-history convention must be represented
in the initial window. An arbitrary full-prefix recurrence has not thereby
acquired a bounded window; proving sufficient history is a separate obligation.

## 4. P3: summaries compose, but only with compatible dependencies

**Theorem P3a (successive reduction).** Suppose q reduces F to G, and r reduces
G to H, for the same input set. Then r composed with q reduces F to H.

**Proof.** For each x and u,

    r(q(F_u(x))) = r(G_u(q(x))) = H_u(r(q(x))).

Observation factorizations compose in the same way. QED.

**Theorem P3b (coupled product).** Suppose

    F_u(x_1,x_2) = (F1_u(x_1,x_2), F2_u(x_1,x_2)),

and each component has a certificate

    q_i(Fi_u(x_1,x_2)) = Gi_u(q_1(x_1), q_2(x_2)).

Then (q_1,q_2) reduces the coupled product update to (G1,G2).

**Proof.** Apply the two certificates componentwise. QED.

The shared arguments are essential. A reduction of each subsystem in isolation
does not authorize a reduction after an interaction reads discarded information.
This is a useful compiler rule: dependency compatibility is a proof premise,
not something implied by equal shapes or permutation symmetry.

## 5. P4: exact, extent-independent moment recurrence

Work first over a commutative ring R with a unit. Let x be an N-element array,
let p_k(x) = sum_i x_i^k, and let p_0(x) = N interpreted in R. Define

    q_r(x) = (p_1(x), ..., p_r(x))                  for a fixed r >= 1.

Suppose the update has the form

    x_i' = a(q_r(x),u) * x_i + b(q_r(x),u),

where a and b are arbitrary total scalar functions and are shared across all
positions in this array. They need not be linear in the moments.

**Theorem P4.** q_r admits the closed update

    p_k' = sum_(j=0)^k binom(k,j) * a^j * b^(k-j) * p_j,
                                               for k = 1, ..., r.      (M1)

Thus r summary coordinates suffice at every N for observations factoring
through q_r. For r = 2, writing S = p_1 and Q = p_2,

    S' = a*S + N*b,
    Q' = a*a*Q + 2*a*b*S + N*b*b.                            (M2)

**Proof.** At a fixed state, a and b are the same scalars for all i. Apply the
binomial theorem to each (a*x_i+b)^k, sum over i, and exchange the two finite
sums. Pull the shared coefficients outside the sum over i. The inner sum is
p_j, and no index j greater than k is introduced. For k <= r every required
moment is in the summary or is the known p_0. This proves C1; a chosen summary
observation supplies C2, so P1 gives every-step preservation. QED.

**Work statement.** For fixed r and constant-cost evaluation of a and b,
initializing the moments takes O(N) scalar operations and T reduced steps take
O(T). An explicit particle update takes O(NT). Summary working state is O(1)
in N; materializing T summary outputs takes O(T) storage. Input storage, input
reading, integer bit complexity, and arbitrary-precision arithmetic costs are
not hidden in these claims. This is an arithmetic-operation comparison, not a
measured speedup or a universal lower bound on all algorithms for the example.

**Corollary P4a (minimality when all r moments are observed).** Over the reals,
if N >= r, any C1 summary with C1 observation reconstruction that preserves
q_r on an open neighborhood of a state with at least r distinct coordinates
needs at least r real coordinates.

**Proof.** The Jacobian of q_r has entries k*x_i^(k-1). Its r-column minor on
r distinct coordinates is a Vandermonde matrix times nonzero row scalars, so
has rank r. Apply P6 below with the time-zero observation q_r. QED.

This proves coordinate minimality only for the specified observation contract.
If only S is observed and a,b depend only on S, one coordinate suffices instead.

## 6. P5: tangent, adjoint, and parameter preservation

Let X and Z now be open subsets of finite-dimensional real vector spaces.
Assume F_u, q, G_u and the observation maps are C1 and C1 holds as an identity
on the relevant open set. Suppress u temporarily.

**Theorem P5a (tangent compatibility).** For every x and perturbation v,

    Dq(F(x)) * DF(x) * v = DG(q(x)) * Dq(x) * v.             (AD1)

**Proof.** Differentiate the identity q composed with F = G composed with q
and apply the chain rule. QED.

Thus propagate the microscopic tangent and then summarize it, or summarize
the tangent and propagate it in the reduced system: the result is identical.
This claim concerns the abstract derivative, not yet Blade's emitted AD code.

**Theorem P5b (cotangent compatibility).** For every reduced cotangent lambda,

    DF(x)^T * Dq(F(x))^T * lambda
      = Dq(x)^T * DG(q(x))^T * lambda.                     (AD2)

**Proof.** Transpose AD1's matrix identity. QED.

These are coordinate cotangents with the stated Euclidean pairing. If a packed
array represents a larger logical array, the reconstruction map and its
transpose belong in the chain. In particular, orbit multiplicities cannot be
replaced by an unconditional factorial or by an average. Connecting this to
Blade's existing canonical-storage AD is a separate mechanization obligation.

**Theorem P5c (parameter gradients of observed losses).** Suppose the reduction
identities hold for all theta in an open set, all relevant maps are jointly C1,
and x_0(theta) is C1. For a fixed input word and fixed horizon, any C1 scalar
loss of the observed trajectory is the same function of theta in the full and
reduced systems; therefore their parameter gradients agree.

**Proof.** P1 gives equality of every trajectory observation for every theta
in the open set. Compose with the same loss and differentiate equal C1
functions. QED.

The initialization is z_0(theta) = q(x_0(theta)). If the summary itself depends
on theta, initialize with q_theta(x_0(theta)) and retain BOTH contributions

    dz_0/dtheta = (partial q_theta / partial theta)(x_0)
                  + D_x q_theta(x_0) * dx_0/dtheta.

The same identity-of-functions proof applies to parameter-dependent summaries
when their reduction identities hold jointly; the local AD1/AD2 formulas above
were stated for parameter-independent q. Equality at one parameter value alone
is insufficient to conclude equal derivatives.

For a fixed initial array and q_2 = (S,Q), a reduced loss gradient (lambda_S,
lambda_Q) pulls back to

    partial loss / partial x_i = lambda_S + 2*x_i*lambda_Q.

Hence returning all N initial-state derivatives still takes Omega(N) output
work. The reduced trajectory may need O(T) reverse-mode storage or recomputation.
Neither P5 nor P4 promises constant-memory reverse AD.

Guarded recurrences require locally stable execution paths or an explicitly
specified derivative semantics at branch changes. An exact fixed-point
derivative is not the derivative of a finite iterative algorithm by default.

## 7. P6: a certificate that no smaller smooth state can suffice

Fix a finite list of input words w_1, ..., w_L, possibly including the empty
word, and form the observation map

    O(x) = (h(F_(w_1)(x)), ..., h(F_(w_L)(x))) in R^M.

Here M counts scalar observation components. A proposed reduction with summary
q : X -> R^m and C1 reduced maps gives a C1 factorization O = R_O composed
with q by P1.

**Theorem P6 (observation-rank lower bound).** If rank DO(x_*) = s at an
admissible interior point, every such C1 reduction valid near x_* satisfies
m >= s.

**Proof.** The chain rule gives DO(x_*) = DR_O(q(x_*)) * Dq(x_*).
The rank of this matrix product is at most the intermediate dimension m.
Therefore s <= m. QED.

A nonzero s-by-s minor is a finite witness. For polynomial maps with rational
coefficients and a rational point, its value can be checked using rational
arithmetic. Its nonvanishing must be established at an admissible point.

This is a lower bound, not a universal construction of minimal coordinates.
Singular quotients, topology, and polynomial-generator counts can prevent a
matching global coordinate representation. Failure to find a full-rank minor
is not evidence that a reduction exists.

The C1 restrictions are load-bearing. This theorem is not about arbitrary
discontinuous encodings of many reals into one real, finite-precision bit
encodings, or a summary given extra access to the original x at each step.

## 8. P7: squaring defeats every uniformly bounded smooth summary

Let X_N = (0,infinity)^N, define F_N(x)_i = x_i^2, and observe
h_N(x) = sum_i x_i. For a horizon H >= 1, observe steps 0 through H-1:

    O_(N,H)(x) = (p_1(x), p_2(x), p_4(x), ..., p_(2^(H-1))(x)).

### Lemma P7a: a sparse polynomial has few distinct positive roots

A nonzero real polynomial with at most s nonzero monomials has at most s-1
distinct roots in (0,infinity).

**Proof.** Induct on the number of nonzero monomials. A single nonzero monomial
has no positive root. For s > 1, divide by the lowest power of x, which does
not change positive roots. The result has a nonzero constant term and s-1
higher monomials. Its derivative has s-1 nonzero monomials, so by induction
at most s-2 distinct positive roots. If the original quotient had s distinct
positive roots, Rolle's theorem would give at least s-1 distinct positive roots
of its derivative, a contradiction. QED.

### Lemma P7b: generalized Vandermonde nonsingularity

For distinct positive a_1,...,a_s and distinct nonnegative integer exponents
e_1,...,e_s, the matrix V_(j,i) = a_i^(e_j) is nonsingular.

**Proof.** A nontrivial row dependence would give a nonzero polynomial with
at most s monomials vanishing at all s positive a_i. P7a rules this out. QED.

### Theorem P7: horizon-sensitive dimension obstruction

At any x with pairwise distinct positive coordinates,

    rank D O_(N,H)(x) = min(N,H).

Every C1 reduction preserving those H observations therefore needs at least
min(N,H) summary coordinates. In particular, one valid for arbitrary horizons
needs at least N coordinates, so no uniform constant-dimensional smooth summary
exists as N grows.

**Proof.** Set s = min(N,H). The Jacobian entries are

    (D O_(N,H))_(t,i) = 2^t * x_i^(2^t-1),       0 <= t < H.

Take the first s rows and any s columns. Divide row t by the nonzero 2^t.
The remaining matrix is nonsingular by P7b, with exponents 2^t-1. Thus the
Jacobian has rank at least s; its shape bounds the rank above by s. P6 yields
the dimension bound. For the all-horizons conclusion take H = N. QED.

This is stronger than saying that one particular moment tower fails. It rules
out all C1 summary coordinates with C1 reconstruction under the stated contract.
For a single fixed observation horizon, claim only min(N,H), not N. The theorem
does not rule out special initial manifolds, approximations, or reductions
preserving only one final output with a horizon-specific initialization.

Although this system is permutation-equivariant and its observation is
permutation-invariant, its local continuous observation dimension is N. A
finite permutation quotient does not itself lower that dimension.

### Exact finite counterexample to retaining only S and Q

The integer arrays

    A = (3,-3,4,-4),        B = (5,-5,0,0)

have equal S = 0 and Q = 50. After one squaring update their sums are both 50,
but their sums of squares are respectively

    3^4 + (-3)^4 + 4^4 + (-4)^4 = 674,
    5^4 + (-5)^4                = 1250.

Thus no function of (S,Q) alone supplies the next (S,Q) on unrestricted real
arrays. This counterexample uses a different domain from P7's positive chamber;
the rank argument supplies the stronger obstruction on that chamber itself.

## 9. P8: a complete closure criterion for one restricted polynomial fragment

Fix r >= 1 and N. Let a_0,...,a_d be real polynomials in r variables, with
a_d not identically zero; d is the actual degree in the local particle value.
Consider the shared-coefficient update

    F(x)_i = sum_(j=0)^d a_j(q_r(x)) * x_i^j.                (P1)

N and any parameters have been fixed here. If parameters specialize the leading
coefficient to zero, recompute d for that specialization. The zero polynomial
update is included separately in the degree-at-most-one case.

**Theorem P8 (closure/refusal dichotomy).**

- If d <= 1, q_r has an exact update for every N, by P4.
- If d >= 2 and N >= d*r, there is NO function G with q_r(F(x)) = G(q_r(x))
  for all x in R^N. This refusal does not even require G to be continuous.

For a fixed polynomial update formula independent of N and nonzero leading
coefficient, exact closure of the first r moments at every N is therefore
equivalent to degree at most one in the local particle value.

**Proof of the negative half.** Set m = d*r. Expand the r-th power of (P1):

    p_r(F(x)) = sum_(k=0)^m c_k(p_1,...,p_r) * p_k(x),
    c_m = a_d(p_1,...,p_r)^r.                              (P2)

The coefficient polynomials depend only on the first r moments. The map

    P_m(x) = (p_1(x),...,p_m(x))

has Jacobian entries k*x_i^(k-1). At a state with distinct coordinates its
first m columns have nonzero determinant by the ordinary Vandermonde formula.
Thus P_m is a submersion there: after fixing the remaining N-m coordinates,
the inverse function theorem makes the first m moments local coordinates.

Choose such a point with a_d(q_r(x)) != 0. It exists even in a positive,
strictly ordered chamber: the same rank argument for P_r makes its image
contain an open subset of R^r, and a nonzero real polynomial cannot vanish on
that entire open subset.

Use the local moment coordinates to vary p_m a small amount while keeping
p_1,...,p_(m-1) fixed. The resulting two states have the same q_r, since
m > r. In (P2) all coefficients and all lower moments are unchanged, whereas
the p_m term changes with nonzero coefficient a_d(q_r)^r. Therefore their next
r-th moments differ. No function of q_r alone can produce both answers. QED.

**Scope.** This is completeness for the candidate summary q_r and the specified
update fragment. It does not rule out another choice of summary, a restricted
initial manifold, finite-support particle values, or a specially small N. At a
fixed N, the first N power sums determine every symmetric polynomial through
Newton identities; that familiar finite-N fact is consistent with the
N >= d*r condition above. The squaring result P7, not P8, supplies the stronger
lower bound against every smooth summary for its particular observation.

For formulas polynomial in N as well, a nonzero symbolic leading coefficient
can vanish identically in the moments at finitely many special N. The negative
argument applies at each N >= d*r where the specialized leading coefficient
is nonzero; do not report the generic refusal at a degenerate specialization.

**Algebraic mechanization alternative.** Over Q, the first m power sums are
algebraically independent when N >= m. Equation (P2) then has a nonzero
coefficient of the independent generator p_m, so is not in Q[p_1,...,p_r].
This gives an algebraic proof against POLYNOMIAL G. It is weaker than the
arbitrary-function refusal proved above, but avoids the inverse function theorem
and may fit the present proof tower more naturally. Keep the two scopes distinct.

## 10. P9 and P10: observable algebras, and a trap for synthesis

### P9: the certificate for polynomial summaries is finite

Fix a polynomial F : R^N -> R^N, a polynomial observation h with finitely many
components, and a proposed polynomial summary q = (q_1,...,q_m). Let

    B = R[q_1,...,q_m] be the subalgebra of R[x_1,...,x_N],
    F* p = p composed with F.

**Theorem P9.** Polynomial maps G and h_bar satisfying C1 and C2 exist if and
only if

    q_j composed with F belongs to B for every j,
    h_k belongs to B for every observation component k.     (A1)

Equivalently, B contains the observation components and is closed under F*.

**Proof.** If the reduced maps exist, their coordinate expressions give the
memberships. Conversely, each membership supplies a polynomial g_j or h_bar_k
in m variables. Collect the g_j to form G and the h_bar_k to form h_bar. The
resulting identities are exactly C1 and C2. Because substitution preserves
addition and multiplication, checking F* on the generators q_j proves closure
on every polynomial in those generators. QED.

The candidate generators may be algebraically dependent. G only needs the
correct behavior on q(R^N), which C1 makes invariant; a chosen polynomial
extension outside that image is not an additional physical claim.

For rational-coefficient polynomial input, supplied expressions for G and
h_bar yield finite polynomial-identity certificates. A checker can substitute
and normalize. This does not require trusting a heuristic synthesis procedure.
It also does not require claiming that a full expanded polynomial representation
is a practical way to handle large arrays.

For controlled updates, require (A1) for every allowed input. A polynomial
identity in symbolic inputs is a finite way to certify that universal claim;
testing a few inputs is not.

### P10: the least observable algebra need not be finitely generated

For an autonomous polynomial recurrence define

    A_obs = R[h_k composed with F^t : every k and every t >= 0].

Every polynomial reduction algebra B satisfying P9 contains A_obs: apply F*
repeatedly to its observation components. It is tempting to keep adjoining
these future observations and expect to reach finitely many generators.

**Counterexample P10.** Let

    F(x,y) = (x*y, y),         h(x,y) = x.

Then h(F^t(x,y)) = x*y^t, and

    A_obs = R[x, x*y, x*y^2, x*y^3, ...]

is NOT finitely generated as an R-algebra. Nevertheless the two-coordinate
summary q(x,y) = (x,y) is an exact polynomial state for every horizon.

**Proof.** The trajectory formula follows by induction. Suppose A_obs were
generated by finitely many polynomials f_1,...,f_l. Every nonconstant monomial
in any member of A_obs has positive x-degree. Choose M bounding the y-degree
of every x-degree-one monomial appearing in those finitely many f_i. In any
polynomial expression in the f_i, a resulting monomial of x-degree one can
come from only one positive-x-degree factor, multiplied by constants from all
other factors. Hence its y-degree is at most M. But x*y^(M+1) belongs to A_obs,
a contradiction. The identity summary plainly reproduces F and h. QED.

Moreover two coordinates are locally necessary wherever x != 0: the first two
observations (x,x*y) have Jacobian determinant x, so P6 applies.

This example invalidates two proposed shortcuts:

1. Hilbert's basis theorem does not make ascending chains of SUBALGEBRAS
   stabilize. It concerns ideals in a Noetherian ring.
2. Failure of finite generation of A_obs does not imply that no finite summary
   exists. A finite summary can generate a larger stable algebra containing
   extra state information, as R[x,y] does here.

An implementation must distinguish a sufficient summary, a summary of minimal
coordinate dimension, and a quotient identifying exactly all states with the
same future observations. These are different requirements. The first is the
initial compiler target; P6 supplies lower bounds for the second. No general
existence or coordinate theorem for the third is claimed here.

## 11. What would make these proofs valid compiler transformations

The paper equalities are necessary evidence, not a complete compiler theorem.
An accepted implementation must also discharge the following concrete premises.

### 11.1 Observation and effect contract

The first fragment should be pure and total, with fixed budgets and explicit
output demand. All observable outputs must factor through the summaries.
An error, abort, print, external write, requested materialization, or guard must
either be excluded from the fragment or preserved by an explicit semantics.
Purity alone does not justify skipping an expression that can fail.

No surface syntax is proposed here. Decide separately whether the user requests
an exact algebraic summary explicitly or an optimizer derives it under a
documented contract. Follow Blade's existing advice-versus-rewrite policy;
these proofs do not silently change it.

### 11.2 Numerical contract

Finite-sum algebra and the binomial theorem hold over the exact rings stated.
Floating-point addition and multiplication do not satisfy those ring laws.
Even the S' formula can differ from first updating all particles and then
reducing them in the original order. A mathematical certificate therefore does
not establish bitwise preservation, `where repro` compatibility, unchanged
overflow/NaN behavior, or a rounding-error bound.

Choose one explicit contract before implementation: an exact arithmetic
fragment; a user-selected algebraic reformulation with stated numerical
semantics; or a separate finite-precision/error theorem. An OMP license is not
blanket permission for distributivity-based recurrence reduction.

### 11.3 Units, identities, and array structure

S has the unit of x; Q has its square. For x_i' = a*x_i+b, a is dimensionless
and b has the unit of x. Consequently (S,Q) is naturally a heterogeneous product,
not a homogeneous array coerced to one element unit.

Named index identity must survive where observations require it. Summary
coefficients must actually be shared over the summarized index set. Equal
extents, equal units, or input permutation symmetry do not prove that property.
Blockwise summaries need certificates for interactions across blocks (P3b).

### 11.4 AD contract

P5 establishes derivatives of mathematical functions. A compiler theorem must
connect Blade's AD transformation to those derivatives on the admitted fragment,
including initialization, parameter-dependent summaries, packed reconstruction,
and supported intrinsics. Preserve the executed-algorithm interpretation of
recurrences. Do not replace it with an implicit fixed-point derivative.

### 11.5 Cost contract

Report summary initialization, per-step work, requested output size, and
reverse-mode storage separately. A high-cost summary could save no work.
Reducing state dimension does not automatically reduce all computations, and
compiler-time expansion in N defeats the intended uniform construction.

## 12. The actual research target, and where the drafts stop

### Candidate Blade contribution

Define a typed certificate calculus for a restricted fragment of array maps,
aggregate observations, products, and structural recurrences. Show that its
local rules construct q_N and G_N uniformly in the named extents and that
accepted programs satisfy a simulation relation between their original and
reduced executions. Connect that relation to observation demand and to the
supported AD transformation. Pair accepted reductions with dimension lower
bounds or explicit counterexamples where available.

The first complete fragment could be the shared polynomial particle update
and contiguous moment summaries in P8, with products and compositions certified
by P2/P3. Here a sharp positive/negative classification has already been drafted;
the missing Blade theorem is that source recognition, certificate construction,
summary typing, and execution all implement exactly that classification.

**Do not claim a terminating complete search over arbitrary recursive arrays,
arbitrary nonlinear summaries, or all polynomial invariant subalgebras.** P10
already defeats a natural candidate search. Outside a proved complete fragment,
use three outcomes:

    certified reduction
    certified obstruction, naming its exact scope
    unknown / unsupported.

### Distinct proof obligations for Claude

| Item | Draft status | Work remaining |
|---|---|---|
| P1 trace, feedback, and guard preservation | Full paper arguments | Define execution/failure semantics precisely; mechanize induction |
| P2 finite graph and bounded-lag lifting | Full paper arguments | Model typed predecessor lists and zero-history initialization |
| P3 successive/coupled composition | Full paper arguments | Package compositional certificate rules |
| P4 moment recurrence | Full finite-sum argument | Mechanize generic-r binomial identity and unit-compatible product state |
| P4a minimality | Paper proof using Vandermonde + P6 | Separate algebraic and analytic foundations |
| P5 derivatives | Full chain-rule arguments | Prove connection to the actual supported AD transformation |
| P6 dimension lower bound | Full rank-factorization argument | Decide analysis library/assumption policy or state an algebraic restriction |
| P7 squaring obstruction | Full argument, including sparse-polynomial lemma | Audit domains, horizons, regularity, and mechanism for checked witnesses |
| P8 restricted closure classification | Full paper proof | Formalize local-coordinate proof or explicitly choose weaker polynomial refusal |
| P9 polynomial certificate criterion | Full algebra argument | Small identity checker and soundness proof; scalable index-level representation open |
| P10 nontermination counterexample | Full paper proof | Mechanize if useful; preserve as a synthesis design constraint |
| Uniform typed compiler construction | Research objective only | Specify a fragment and prove source-to-summary correctness |
| General minimal-summary discovery | Not claimed | New research; P10 blocks naive completion arguments |
| IEEE numerical preservation | Not proved | Select a contract before allowing rewrites |

### Suggested mechanization sequence

1. Start with P1 and P3 over abstract types and explicit certificate hypotheses.
   These require only ordinary induction and equality. Candidate names:
   `summary_step_sound`, `summary_run_sound`, `summary_trace_sound`,
   `summary_compose_sound`, `summary_product_sound`.
2. Mechanize the rank-2 P4 formulas over an abstract commutative ring using
   lists or finite index sets, then generalize to r by a binomial lemma.
   Candidate names: `affine_sum_closed`, `affine_square_sum_closed`,
   `affine_moment_tower_closed`. Add the integer 674/1250 counterexample.
3. Mechanize P9 as a finite symbolic certificate check. Prove correctness of
   substitution/normalization, rather than trusting a symbolic package's output.
4. Choose the scope of negative certificates. Exact rational minors are cheap
   to CHECK, but deriving a lower bound against all C1 encodings invokes real
   analysis. A polynomial-encoding/transcendence-degree version is another
   legitimate theorem, with a narrower claim.
5. Establish the typed source fragment and the compiler/interpreter simulation
   theorem. Only then attach the proof to a code-generation path.

The current proof tower advertises an axiom-free, stdlib-only discipline.
Do not import a real-analysis development and silently carry that claim across.
Audit `Print Assumptions`/`coqchk` and the library foundations. In particular,
paper uses of Rolle's theorem, the inverse function theorem, and the chain rule
are not made machine-checked by checking a determinant. Conditional symbolic
lemmas must be labeled conditional; do not hide these premises in axioms.

Possible eventual files are `proofs/BladeSummary.v` for the abstract certificate
calculus and `proofs/BladeMomentClosure.v` for finite-sum algebra. These names
are suggestions, not files already created. Add them to `_CoqProject` and update
the count only after the chosen statements actually compile and are checked.

### Adversarial cases to retain

- Same (S,Q), different next Q: the 674/1250 example.
- Permutation-equivariant dynamics with no bounded summary: P7.
- Single fixed horizon versus all horizons: use min(N,H) correctly.
- Special parameter values where a polynomial's leading coefficient vanishes.
- Fixed small N where Newton identities close a tower that fails uniformly.
- Individual-particle output added to an otherwise aggregate-only program.
- A guard or abort depending on discarded state.
- Parameters entering the summary or initial state: differentiate both paths.
- A full-prefix recurrence incorrectly treated as first-order or bounded-lag.
- Search divergence despite a finite exact state: P10.
- Equal exact answers with different floating-point rounding histories.

## 13. Prior art and originality boundary

These sources establish nearby work, not exhaustive priority coverage:

- Ovchinnikov, Perez Verona, Pogudin, and Tribastone,
  [CLUE: Exact maximal reduction of kinetic models by constrained lumping of
  differential equations](https://arxiv.org/abs/2004.11961), 2020/2021. Computes
  minimal constrained LINEAR lumpings for polynomial ODEs. The proposal here
  must distinguish nonlinear moment summaries, discrete source recurrences,
  and uniform array-extent certificates from that established result.
- Brunton, Brunton, Proctor, and Kutz,
  [Koopman invariant subspaces and finite linear representations of nonlinear
  dynamical systems for control](https://arxiv.org/abs/1510.03007), 2015/2016.
  Finite closed spaces of observables have substantial precedent. P9 allows a
  nonlinear G and concerns an invariant algebra, so it is not asserting a
  finite-dimensional LINEAR Koopman representation.
- [An Algebraic Approach to Local Observability at an Initial State for
  Discrete-Time Polynomial Systems](https://skoge.folk.ntnu.no/prost/proceedings/ifac11-proceedings/data/html/papers/0336.pdf),
  IFAC 2011. Observability and algebraic/rank obstructions are existing fields;
  P6 should be presented as an application of that machinery, not its invention.

The plausible research claim is the extent-parametric, typed, mechanized
compiler connection in Section 12. A prior-art review should also cover
nonlinear lumping, algebraic realization, homomorphism-based program derivation,
incremental computation, and compiler treatment of AD under exact reduction.
Neither a novel name nor the combination of standard lemmas establishes
priority. The useful deliverable can still be a rigorous new capability for
Blade even if all underlying mathematics has precedent.

## 14. Validation record

No compiler code, corpus behavior, or checked proof files were changed for this
draft. The statements are paper proofs and require independent review and
mechanization. Exact-arithmetic spot checks, when recorded below, test examples
and formulas only; they do not establish the quantified theorems.

Checks run for this draft using Python's standard-library rational arithmetic:

- 50 moment-identity checks: N = 0 through 9, r = 1 through 5, with coefficients
  depending on the current moments.
- Four successive steps of a nonlinear, moment-dependent affine particle update,
  comparing the full array and the reduced two-moment state exactly.
- 49 Jacobian-rank checks for the squaring example: N,H = 1 through 7,
  verifying rank = min(N,H) at positive distinct integer states.
- The same-(S,Q), different-next-Q counterexample, including exact 674 and 1250.
- Local file-link targets resolve. No Blade build or compiler tests were needed
  for this document-only handoff; no Coq/Rocq checking is claimed.

## 15. Review and mechanization record (Claude, 2026-09-19)

Audit of sections 1-13, then four mechanization rounds. Five files were added
to the proof tower: `proofs/BladeSummary.v` (41 theorems),
`proofs/BladeMomentClosure.v` (86), `proofs/BladeRankBound.v` (49, second
round), `proofs/BladeDescartes.v` (28, third round) and
`proofs/BladeRankDomain.v` (14, fourth round) -- all `coqc` + `coqchk` clean on
Rocq 9.0.1, `Print Assumptions` closed (no axioms), stdlib only; the tower
total is 1165 and `count-theorems.ps1 -Check` passes. One further file,
`proofs/reals/BladeSmoothRank.v` (22), is deliberately OUTSIDE the tower and
its count: it depends on two axioms of Coq's real numbers (15.8). Sections
15.6-15.8 are the later rounds. `docs/proofs.md` carries the prose
mirror. Nothing in `src/` changed; this still backs no compiler feature.

### 15.1 Audit verdict

No mathematical error found. Each of P1-P10 is correct as stated, including
the three arguments most likely to hide one: P7a (the sparse-polynomial root
bound: the divided polynomial keeps a nonzero constant term, so its derivative
is nonzero with s-1 monomials and Rolle applies), P8's negative half (the
coefficient of p_m in (P2) is a_d^r, every other ingredient is a function of
q_r, and m = d r > r needs only d >= 2 and r >= 1), and P10 (x-degree-one
parts of a product of elements "constant + positive x-degree" are linear
combinations of the factors' x-degree-one parts).

Corrections and sharpenings, none of which changes a theorem:

1. **"C1" means two things.** It labels the certificate identity (C1) and the
   smoothness class C^1. Section 6 opens with both in one clause ("...are C1
   and C1 holds as an identity"), and P4a's "any C1 summary with C1
   observation reconstruction" is only readable by context. Rename the
   identities (say R1/R2) or write C^1 for smoothness.
2. **Section 9 labels its displays (P1) and (P2)**, colliding with theorems P1
   and P2 -- "by P2" is ambiguous inside that section.
3. **P8's threshold N >= d r is sufficient, not sharp** -- worth saying where
   it is stated, because the adversarial list reads it as the boundary. For
   squaring against (S, Q) (d = r = 2, so d r = 4) the exact threshold is
   N >= 3: `square_SQ_threshold` proves the functional relation holds iff
   N <= 2. For pure powers against the sum alone (r = 1) refusal starts at
   N = 2 for every d (`power_not_closed_S`). Both are r + 1; whether r + 1 is
   the threshold in general is not claimed.
4. **The (S, Q) refusal does live inside P7's positive chamber.** The
   674/1250 pair leaves it, as section 8 says, but (1,5,6) / (2,3,7) has equal
   (S, Q) = (12, 62), strictly positive pairwise-distinct coordinates, and
   next Q = 1922 vs 2498 -- already at N = 3 (`same_SQ_positive_N3`), and
   padded with common positive entries at every N >= 3
   (`square_not_closed_SQ`).
5. **Section 0's relation to uniform optimality can be made exact.** The
   saving is sum_i (a x_i + b) = a sum_i x_i + N b: distributivity through a
   fold, with a kernel that is NOT opaque. That is precisely the frontier
   `plan-uniform-optimality.md` names as unreachable by `where` clauses. In
   the oracle model the update kernel is a black box and N T calls are forced
   (BladeOrbitWork); the reduction exists only because the kernel is KNOWN
   affine over a ring. By Blade's own doctrine that knowledge enters as a
   named node or an explicit declaration, not as a silent optimizer rewrite
   -- and section 11.2 already forces the same conclusion from the numerical
   side, since the default policy is byte-identity with the interpreter.

### 15.2 What is now machine-checked

| Draft item | Status | Coq artifacts |
|---|---|---|
| P1 | FULL, with the invariant-set form of section 1 | `certificate`, `summary_run_sound`, `summary_obs_sound`, `summary_trace_sound`, `trace_nth` |
| P1a feedback | FULL | `summary_feedback_sound` (state, history AND chosen inputs) |
| P1b guard/budget | FULL; BL8010 is an outcome, not erased | `summary_guard_sound`, `summary_guard_certificate`, `summary_frozen_trace_sound`, `guarded_state` |
| P2 graph | FULL, single carrier with node-indexed q, shared predecessors | `summary_dag_sound`, `summary_dag_obs_sound`, `summary_tree_sound` |
| P2 tree example | FULL over any commutative ring | `tree_summary_sound` |
| Bounded lag | FULL, zero-history window | `lag_window_sound`, `lag_window_is_first_order`, `lag_slice_from_window` |
| P3a / P3b | FULL, plus the refutation P3b's prose asks for | `summary_compose_sound`, `summary_product_sound`, `isolated_certificates_do_not_compose` |
| P4 (M1), (M2) | FULL, every k, every N, any commutative ring, a and b arbitrary functions of (u, summary) | `affine_moment_tower_closed`, `bcl_binomial`, `affine_sum_closed`, `affine_square_sum_closed`, `moment_certificate`, `moment_reduction_sound` |
| P4a | FULL at every r, against POLYNOMIAL encodings (15.6) | `moment_summary_needs_r_coordinates`; r = 2: `SQ_needs_two_coordinates` |
| P5a | FULL for the P4 fragment, forward mode as dual numbers, every finite execution | `dual_ring_theory`, `moment_tangent_sound`, `dual_psum` |
| P5b | the q_2 pullback formula only | `pullback_SQ` |
| P5c | covered by P5a's generality (parameters enter through the tangent parts of a, b and x_0) -- prose on that anchor | -- |
| P6 | FULL at every (m, s), against POLYNOMIAL encodings, no determinants (15.6) | `homogeneous_has_solution`, `poly_rank_bound_rows`, `poly_rank_bound_cols`, `poly_rank_certificate`; fixed sizes: `poly_rank_bound_1_2`, `poly_rank_bound_2_3` |
| P7 | FULL against POLYNOMIAL encodings at every N and horizon (15.6); the RANK statement at every integer point with distinct positive coordinates (15.7) | `squaring_needs_min_N_H_coordinates`, `squaring_jacobian_full_rank`, `many_roots`; N = H = 3: `squaring_minor_N3` (= 96), `squaring_three_steps_need_three_coordinates`; `squaring_observes_dyadic_moments` |
| P7a, P7b | FULL over Z (15.7), by Descartes' rule -- NOT by the draft's Rolle argument, which has no axiom-free reading | `mulxc_adds_a_variation`, `descartes_bound`, `sparse_roots`, `generalized_vandermonde`, `generalized_vandermonde_transpose`, `square_kernel_transpose` |
| P8 positive half | FULL (it is P4) | as P4 |
| P8 negative half | PURE POWERS x^d: every d >= 2, r >= 1, N >= d r against polynomial G (15.6); r = 2 every d against EVERY G; squaring vs (S, Q) with exact threshold; x^d vs S | `power_not_poly_closed`, `power_not_closed_SQ`, `square_not_closed_SQ`, `square_SQ_threshold`, `two_point_newton`, `power_not_closed_S`, `same_SQ_674_1250` |
| P9 | FULL (generators suffice; supplied expressions are a certificate), one worked `ring` check | `generators_closed_algebra_closed`, `poly_certificate_sound`, `poly_certificate_affine_N3` |
| P10 | FULL (15.6): given any finite list of elements of A_obs, some observable is not a polynomial in them | `p10_not_finitely_generated`, `p10_next_observable_is_new`, `p10_trajectory`, `p10_needs_two_coordinates`, `p10_no_identification` |
| Units (11.3) | the grading, as scaling covariance (15.6) | `affine_update_unit_covariant`, `psum_scale` |

### 15.3 Three things the mechanization found

**(a) The lower-bound half has an analysis-free core, and it is the stronger
one where it applies.** A certified summary never identifies two states some
input word distinguishes (`summary_refines_future`), and future-equivalence is
the coarsest congruence preserving the observation (`future_equiv_coarsest`) --
section 10's "third requirement", the quotient by future observations, exists
relationally with no coordinates at all. Consequence: ONE pair of states with
equal summaries and different next summaries refutes a candidate summary
against every update function whatsoever (`collision_refutes_update`). That is
P8's "does not even require G to be continuous", obtained without the inverse
function theorem, and it is the natural payload for section 12's "certified
obstruction, naming its exact scope": a witness pair, checkable by exact
integer evaluation, scoped to the candidate summary and the extent (a padding
lemma lifts it to all larger extents). What it cannot do is bound DIMENSION:
Z^N injects into Z, so a cardinality argument is silent there. The draft's
smoothness hypotheses are load-bearing exactly as it says.

**(b) P5a came for free, because P4 was proved over an arbitrary commutative
ring.** The dual numbers R[eps]/(eps^2) are a commutative ring; instantiate
the closure theorem there and its tangent component is "push (x, v) through
the particle system then summarize = summarize then push (z, dz) through the
reduced system", over every finite execution. No chain rule is invoked and no
real analysis is imported. Design consequence for section 11.4: if a
certificate is a POLYNOMIAL identity (the P9 form) it is valid at the dual
numbers automatically, so forward-mode compatibility never needs a separate
proof. Reverse mode is the transpose and is not covered beyond `pullback_SQ`.

**(c) The same device gives dimension bounds against polynomial encodings
without analysis.** Evaluating a polynomial expression at dual numbers
computes its formal directional derivative, the chain rule is evaluation, and
"three observations through two polynomial coordinates have a vanishing
3 x 3 Jacobian minor" is a `ring` identity (`poly_rank_bound_2_3`). This is
mechanization step 4's "polynomial-encoding version" -- narrower than C^1, but
axiom-free. In the first round it stopped at fixed size for want of determinant
theory; 15.6 removes that limit without determinants.
A hypothesis to keep honest: the rank bounds assume the factorization holds
as an identity over the dual numbers, which a symbolic certificate provides;
an equation between functions on Z is weaker and is not what they assume.

### 15.4 Not reached, and why

- ~~**Anything against C^1 encodings** (P4a, P6, P7).~~ Closed in 15.8, in a
  separately labelled file outside the axiom-free tower. What remains of this
  item is **P8's negative half as stated** (against an arbitrary update
  function, by local coordinates): it needs the inverse function theorem,
  which Coq's standard library does not provide in several variables.
- ~~**P7a / P7b** (generalized Vandermonde), hence P7's "rank = min(N, H) at
  EVERY point with distinct positive coordinates".~~ Closed in 15.7, over Z.
  Real points are part of the C^1 item above.
- **P8's refusal for the general fragment** (coefficient polynomials
  a_j(q_r)): the pure-power case is closed at every d and r; the general case
  needs a formal-derivative computation through the coefficient polynomials
  and a point where the leading coefficient is nonzero.
- **Reverse-mode AD as `Grad*.fs` emits it** (11.4) -- the dual-number
  semantics here is not tied to BladeJacobian's symbolic `d`; P5b in paired
  form is a one-line corollary of P5a (apply the cotangent to both sides) and
  was not separately stated. **IEEE behaviour** (11.2), untouched by
  construction. The **source fragment and the compiler theorem** (section 12),
  which remains the research target.

### 15.5 Suggested next round

1. Decide the surface contract before more mathematics (11.1, 11.2): given
   15.1(5), the candidates are an explicit user-requested summary or a named
   node, not a silent rewrite. Everything proved here is contract-agnostic.
2. Make `collision_refutes_update` the refusal format and P9-style polynomial
   identities the acceptance format; both are checkable by exact evaluation,
   and 15.3(b) makes the second carry forward-mode AD.
3. ~~If a dimension lower bound is wanted in the tower, do determinants once
   (Cauchy-Binet at general size) rather than importing Reals.~~ Done in
   15.6, and it did not take determinants.

### 15.6 Second round: the gaps (same day)

`proofs/BladeRankBound.v` (49 theorems), plus nine in BladeMomentClosure.

**The rank bound at every size, without determinants.** Section 15.5 proposed
doing determinants once. A weaker fact is enough: an integer homogeneous system
with more unknowns than equations has a NONZERO solution
(`homogeneous_has_solution`; fraction-free elimination by induction on the
number of equations). If s observations factor through m < s polynomial
coordinates, the s x m matrix of reconstruction coefficients has a nonzero
left-kernel vector, and that vector annihilates the observations' differentials
at every point along every direction (`poly_rank_bound_rows`); dually, along
any s directions some nonzero combination of the DIRECTIONS is invisible to
every observation (`poly_rank_bound_cols`). `poly_rank_certificate` is what a
checker would rely on: exhibit a point and s directions whose s x s integer
matrix of differentials has trivial kernel -- for a concrete matrix that is
linear integer arithmetic -- and every m < s is refuted.

**P4a and P7 at every size from one lemma.** `many_roots`: a polynomial with
more distinct roots than coefficients is zero. P4a takes the row form at
x_i = i: the dependency is a polynomial with coefficients lam_k (k+1) and r
distinct roots. P7 takes the column form, and the choice of point does the
work the draft assigns to P7a/P7b: at x_i = 2^i, along directions 2^l e_l, the
entries 2^t x_i^(2^t - 1) 2^i become 2^t (2^(2^t))^i -- the draft's GENERALIZED
Vandermonde matrix is an ORDINARY one in the nodes 2^(2^t). So
`squaring_needs_min_N_H_coordinates` holds at every N and horizon with neither
Descartes' rule nor Rolle. What this does not give is the draft's stronger
sentence that the rank is min(N, H) at EVERY point with distinct positive
coordinates; a lower bound only ever needed one point.

**P8's refusal at every d and r, with the draft's threshold.** If p_r of the
image under x -> x^d were a polynomial in p_1..p_r, then r + 1 observations
would factor through r coordinates; the row form makes
sum_(k<r) lam_k (k+1) X^k + mu (d r) X^(d r - 1) vanish at every node of every
point, and N >= d r distinct nodes force it to zero (`power_not_poly_closed`).
This is section 9's "algebraic mechanization alternative", and N >= d r is
exactly where it bites -- so the threshold that 15.1(3) showed is not sharp
against arbitrary G is the natural one against polynomial G. Against EVERY G,
`power_not_closed_SQ` covers r = 2 uniformly in d with one collision
((1,5,6) / (2,3,7): 7^k outgrows 1 + 5^k + 6^k from k = 3 on).

**P10 to the end** (`p10_not_finitely_generated`): given any finite list of
elements of A_obs -- each a polynomial expression in the observables
g_k = x y^k -- take M past every variable they mention; x y^M is not a
polynomial in them.

**Units** (`affine_update_unit_covariant`): rescale x and b by c with a
dimensionless, and p_k rescales by c^k. That is 11.3's "heterogeneous product"
as a theorem: the summary is graded and the reduced update respects the
grading. It is a covariance statement, not a link to Blade's unit checker.

### 15.7 Third round: the P7 thread (2026-09-20)

`proofs/BladeDescartes.v` (28 theorems): P7a, P7b, and the rank statements of
P7 and P4a at every integer point.

**A correction to the draft's proof, not to its statement.** Section 8 proves
P7a by induction with Rolle's theorem. Over the reals that is fine. But the
tower is axiom-free, its numbers are Z and Q, and **Rolle's theorem is false
over Q**: the root of the derivative between two rational roots need not be
rational (x^3 - x vanishes at -1, 0, 1; its derivative 3x^2 - 1 has no rational
root). So the draft's argument cannot be transcribed; any mechanization that
"follows section 8" over an ordered field is unsound unless the field is real
closed. The statement survives, because it has a purely algebraic proof:
Descartes' rule of signs. Multiplying by (X - a), a > 0, adds at least one sign
variation to the coefficient sequence (`mulxc_adds_a_variation`); the factor
theorem is exact over an integral domain; so a nonzero polynomial has at most V
distinct positive roots (`descartes_bound`), and s monomials give V <= s - 1
(`sparse_roots`). No root of a derivative is ever requested.

**P7b in both forms.** `generalized_vandermonde`: distinct positive nodes and
distinct exponents (no ordering needed) give a trivial kernel -- the row form,
which is what Descartes proves directly. The column form
(`generalized_vandermonde_transpose`) follows from `square_kernel_transpose`:
a square integer matrix with a nonzero left kernel vector has a nonzero right
one. That is "row rank = column rank" for square matrices, again with no
determinants: drop a row the left vector weights, solve the remaining s - 1
equations in s unknowns (`homogeneous_has_solution`), and the dropped row
follows because its weight is a nonzero integer.

**P7 at every point.** `squaring_jacobian_full_rank`: wherever the coordinates
are pairwise distinct and positive, the differentials of the sums observed over
min(N, H) steps of squaring are linearly independent. This is the draft's
"rank D O_(N,H)(x) = min(N,H)", for integer x; a rational x reduces to it by
homogeneity of the p_k, a real x does not. `moment_jacobian_full_rank` is the
companion for p_1..p_r (ordinary Vandermonde, positivity not needed).

What this round does NOT change: a rank at a point forbids only what
BladeRankBound's theorem says it forbids -- polynomial encodings. Turning the
same ranks into a bound against C^1 encodings is P6 as stated, and is the next
thread.

### 15.8 Fourth round: the C^1 thread (2026-09-20)

Decision taken by the author: a file that depends on Coq's axiomatic real
numbers is admitted, provided it is separately labelled and stays out of the
axiom-free count. The work was split so that the axiom-dependent file contains
nothing but analysis.

**Axiom-free half: `proofs/BladeRankDomain.v` (14 theorems, in the tower).**
P6 is two facts. One is the chain rule. The other -- "a matrix that factors
through m columns has rank at most m" -- is algebra, proved over ANY integral
domain with decidable equality (`homogeneous_has_solution_dom`,
`factor_rows_dependent`). Also algebra is the step that lets an exact integer
computation speak about a field one cannot compute in:
`integer_right_inverse_col` (a square integer matrix with trivial left kernel
has a right inverse up to a nonzero scalar) and `left_kernel_transfer` (along
any ring map Z -> K killing no nonzero integer, trivial left kernel over Z gives
trivial left kernel over K).

**Analysis half: `proofs/reals/BladeSmoothRank.v` (22 theorems, OUTSIDE the
tower).** `Print Assumptions` reports exactly two axioms,
`ClassicalDedekindReals.sig_forall_dec` and
`FunctionalExtensionality.functional_extensionality_dep`; `coqc` + `coqchk`
clean; own `_CoqProject`; not counted.

- The notion of smoothness is `diff_at`: Frechet differentiability AT THE POINT,
  in two-point form. This is weaker than the draft's C^1-near-the-point, so the
  theorems are stronger than the draft's statements. A pleasant consequence: no
  mean value theorem is needed anywhere. `chain_rule` is the plain epsilon-delta
  argument; `diff_at_unique` moves along one coordinate.
- `smooth_rank_bound` is P6 as stated in section 7, in that stronger form: the
  factorization need only hold NEAR x_*, and summary and reconstructions need
  only be differentiable AT x_* and q(x_*).
- `moment_summary_needs_r_smooth_coordinates` (P4a) and
  `squaring_needs_min_N_H_smooth_coordinates` (P7). The ranks are not recomputed
  over R: they are 15.6 / 15.7's exact integer ranks, carried along IZR. For P7
  this is where 15.7 pays off -- the rank at the point (1, 2, 3, ...) is
  `generalized_vandermonde`, a fact about integers.

**What the C^1 thread did NOT reach.** P8's negative half against an arbitrary
update function. The draft's argument varies p_m while holding p_1..p_(m-1)
fixed, which is the inverse (or implicit) function theorem in several
variables; Coq's standard library has one-variable calculus only. That is the
remaining analytic debt, and it is the P8 thread.
