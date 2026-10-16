# Blade Proofs

Prose mirror of the machine-checked proof tower in `/proofs/`:
**2044 theorems**, Rocq 9.0, stdlib only, verified by both `coqc`
and `coqchk`.

Build: `coq_makefile -f _CoqProject -o Makefile && make`.

Rules of this document:

- The `.v` files are canonical; this document is the map. Every claim here
  names its Coq artifact. Nothing is `Admitted`; there are no axioms.
- **Nine files sit outside the tower.** Eight in `proofs/reals/` use Coq's
  axiomatic real numbers: `BladeSmoothRank.v` (24 theorems),
  `BladeRealCollision.v` (30), `BladeRealDensity.v` (32),
  `BladeRoundingBound.v` (20), `BladeReduceClassification.v` (6),
  `BladeShiftedRounding.v` (16), `BladeShiftedFeedback.v` (68) and
  `BladeShiftedVariance.v` (39). One in
  `proofs/floats/`, `BladeBinary64Witness.v` (10), trusts the kernel's
  primitive binary64 evaluation. Each directory has its own `_CoqProject`;
  all nine are
  excluded from the count above and from the "no axioms" claim, and are
  described under their own headings near the end, with the axioms named.
- Scope caveats are stated where the files state them (rank-2 only,
  materialized fragment, do-not-cite, etc.).
- Remaining open items are collected under "What remains unproved" at the end
  of this document.
- The count above is mechanical, on the convention documented in
  [../proofs/README.md](../proofs/README.md): line-initial `Lemma` /
  `Theorem` / `Corollary` / `Example` in the `_CoqProject` files. Run
  `proofs/count-theorems.ps1 -Check` to verify it and the per-file numbers
  in that README.

## The tower at a glance

| Layer | Files | Content |
|-------|-------|---------|
| 0–1: index universe + DMWF | BladeDMWF | canonical tuples, enumeration, the general left-justified bijection, kernel-independence |
| 1.5: arrows | BladeArrow, BladeAffine, BladeCompound, BladeShape, BladeLex | the arrow as coalgebra; Sym/Antisym/affine/Compound instances; uniqueness; lex order |
| Cardinality | BladeBinomial, BladeCounting, BladeMixedRadix | C(n+r−1, r) closed form; the no-lossless-product-layout theorem; the mixed-radix product bijection across distinct groups |
| 2: currying | BladeCurrying, BladeCurryingGeneral | dependence boundary; the two maximal curryings (information-theoretic form) |
| 3: symmetry | BladeCore, BladeLowering, BladeCompleteness, BladeFusionDuality | group law both halves, H-and-Stab soundness AND exactness, sign-tracked variant, fusion ⇒ duality |
| Trinity | BladeTrinity, BladeTrinityAsym | `<*>` = shape concatenation; generators + forced closure |
| Computation | BladeCompute, BladeMonad, BladeSafety | materialized semantics, V∘P = id, 12.x laws, MonadPlus, verified offsets + bounds safety + buffer-elimination fusion |
| Storage split | BladeCauchy, BladeDichotomy | the r = 2 Cauchy split; the r ≥ 3 dichotomy — witness, width-2 refutation over any ring, the r! isotypic repair |
| Input symmetry + layout | BladeWreath, BladeLayout | wreath product S_r wr S_2 for repeated declared-symmetric inputs, block-product storage, exactness enumerated at r = 2 and r = 3; hyperoctahedral layout group B_d, striding-parity character, canonical-form guarantee |
| Deduction exactness | BladeDeduceExact | uniform symmetry of a kernel body = its syntactic automorphism group modulo the declared laws (free-model faithfulness); `src/Deduce.fs`'s mirror walk and parity rule modelled and proved EXACT per transposition; the granted group is the largest contiguous-block Young subgroup; the precision gap this found in the shipped rule, and the two gaps that are by design |
| Recurrence reduction (research; backs no shipped feature) | BladeSummary, BladeMomentClosure, BladeRankBound, BladeDescartes, BladeRankDomain, BladeProuhet, BladeNewton, BladeProuhetShift, BladeNumericContract, BladeReduceCompiler, BladeReductionAD, BladeShiftedMoments | a summary certificate preserves every prefix observation of every finite execution, under feedback, guards and budgets, over chains, dependency graphs and bounded lags; the affine moment tower closes at every order and extent over any commutative ring, and at the dual numbers that IS forward-mode compatibility; refusals by one collision (against every update function) and by formal Jacobian rank (against polynomial encodings); the chain of observable subalgebras that never stabilizes; and the polynomial rank bound at EVERY size without determinants, giving P4a at every r, P7 at every N and horizon, P8's refusal for pure powers at every d and r, and P10 to "not finitely generated"; Descartes' rule of signs over Z (Rolle is false over Q), the generalized Vandermonde kernel on both sides, and the rank of P7 and P4a at EVERY integer point; the algebra of P6 over any integral domain and the transfer of an integer rank to a characteristic-0 domain (the analysis half is outside the tower, in `proofs/reals/`); and the closure refusal against EVERY update function at every degree and order, by Prouhet–Tarry–Escott collisions; Newton's identities over any commutative ring, and the theorem that turns one bumped coefficient into an ideal pair; the numerical contract (wrapping integers and rationals are exact fragments, rounded arithmetic is not one at any precision, and a reassociation license is not a distributivity license); a model source fragment with a complete zero test, a classifier that never answers Unknown, a generated reduced program and the compiler theorem at every extent; and the forward and reverse rules of `Grad*.fs` modelled, the reverse sweep over a stored trajectory as the transpose of the tangent run, and reduction commuting with both modes; and the same reduction in shifted coordinates, where the update is a pure rescaling |
| Optimality | BladeOptimal, BladeOrbitWork | the orbit bound on kernel work and storage against ANY program (oracle adversary), attained by canonical enumeration at H = S_R for every binding and at the sign character r = 2; the pipeline form — one question per subterm class modulo the laws, attained by the memoized evaluator, with the orbit work formula checked on a two-node pipeline |
| The one model, and the surface calculus | BladeObject, BladeSurface | the DMWF IS the quotient: its enumeration is the image of the dense box under canonical form, with fibres exactly the orbits (one record, then every shape under the Young action); arrays as functions on the transversal with canonicalizing access = the storage lookup; the object as the program that asks one question per orbit, which is BladeOptimal's attaining program read through the data. The structure-first core as a calculus: progress, preservation, normalization; a denotation into the object that every step preserves on the nose; and the quotient as a theorem about TYPING DERIVATIONS — the typed output of `L <@> f` is `out_shape`, every dense cell is recovered from it by canonicalizing access, and it holds one cell per orbit; nothing is quotiented without `comm`; a deduced swap-symmetry (BladeDeduceExact's `teq`) is a licence |
| The theory's remaining items (plan items 3–7) | BladeTransport, BladeAttain, BladeInduction, BladeLicence, BladeBoundary | the licence group through every combinator (`<*>` glues identity groups at the seam, so the typed output of a product is a quotient of the product; zip, pipe, `<&>`, `<&!>` with fusion's one-traversal cost and its evaluation cost on mismatched licences; the orbit work of a pipeline attained by the composed object program and bridged program-level to BladeOrbitWork's free-model bound on the two-node schema); the orbit bound at proper subgroups (block licences composed with the binding's cut — the meet — forced and attained; the sign character at every r from adjacent anti-invariance alone; the neighbour rule of §8.3 proved sound but not optimal at (A, B, A)); the inductive side (`let rec` as the unique solution of the zero-extended recurrence on the leading axis, its work N × orbits forced and attained, the quotient surviving on the remaining axes, the `while` guard); numerics as a parameter (typing, soundness and the quotient theorem relative to a declared relation R; reordering needs a congruence, re-bracketing an identity too; a rounded reduce's licence is necessarily coarse); the scope boundary (the bound holds on every free class; separable kernels beat the query bound with n questions but not the storage bound; the converse is open) |
| AD seam | BladeJacobian | symbolic differentiation: renaming equivariance, the Jacobian symmetry transfer, joint-pair-swap tangent symmetry, the accumulation multiplicity rule |
| ML seam | BladeSymPower, BladePartition, BladePointGroup | the S₂ partition of a self-tensor weight space; the Sym^k/Λ^k composition-sector counts (Vandermonde, both flavours); set partitions as restricted growth strings — Bell/Stirling counts, RGS-lex extends refinement, the unitriangular witness certificate; the C₄/D₄ point-group registry — table closure, computed Frobenius–Schur indicators, the J identities, the e-weighted Hom count |

Import structure is a DAG rooted at BladeDMWF (BladeCore and BladeLowering are
self-contained); build order per `_CoqProject`.

---

## BladeCore.v (16 theorems) — the symmetry kernel at r = 2, d = 2

The six audit-identified results at the smallest contentful rank/dimension,
with generalization notes. **This is the file that settles product symmetry.**

**Group Law, soundness half.** For one identity group — `method_for(A, A)`
with `comm` — over a d-dimensional array, the *diagonal* swap (exchange whole
argument slots, dragging each slot's indices together) is a symmetry of the
output for ANY commutative kernel:

- `diagonal_swap_is_symmetry`: Out(i₂,i₁,j₂,j₁) = Out(i₁,i₂,j₁,j₂). Licenses
  the joint simplex and the r! quotient. (Generalized to opaque composite
  index types as `diagonal_group_law` in BladeLowering.)

**Group Law, refutation half.** `per_dim_swap_not_symmetry`:
Out(1,0,0,1) ≠ Out(0,1,0,1) — permuting one dimension's indices across
arguments independently is NOT an output symmetry. This is the constructive
refutation of v10 §10.9 step 3 / §14.5's (r!)^d claim.

**Counting lemma (r = 2 instance).** `counting_lemma_r2`: for n₁, n₂ ≥ 2,
(n₁(n₁+1))·(n₂(n₂+1)) < 2·(n₁n₂)(n₁n₂+1) — per-dimension product storage has
strictly fewer cells than the joint space has distinct values.
(`counting_3_3`: 144 < 180.) General form: BladeCounting.

**Reynolds.** Per-dimension INDEX-LEVEL Reynolds symmetrization genuinely has
product symmetry, and canonical access is lossless:
`reynolds_lat_swap`, `reynolds_lon_swap`, `reynolds_full_product_symmetry`,
`reynolds_canonical_access` (R at the per-dimension-sorted cell equals R
anywhere in the orbit). NOTE: this R sums over index swaps (reading every
array at every permuted index) — the one sound route to per-dimension product
symmetry, at (r!)^d kernel-evaluation cost. The surface `reynolds(g)`
combinator is the weaker VALUE-LEVEL wrapper (permutes kernel arguments); its
output symmetry follows H ∩ Stab like any commutative kernel (formalism §5.3;
pinned by corpus reynolds/013).

**Left-justified bijections (r = 2, 3).** `lj2_forward`/`lj2_backward`,
`lj3_forward`/`lj3_backward`: canonical coordinates ↔ storage coordinates
(a + b < n; a + b + c < n) are mutually inverse — the cumulative-bound
structure. Subsumed by BladeDMWF's general `lj_correct`/`unlj_correct`; kept
as readable entry points.

**Canonicalization exactness.** `access_exact`: fold + left-justify access
returns exactly the stored symmetric value; `canon_identifies_orbit`:
canon(i,j) = canon(j,i).

**Bounds-safety skeleton.** `indexing_total` — **do-not-cite**: proves only
that a total function is total (the v15 audit conceded the original section
header oversold it). Retained as a signpost; the real bounds-safety result is
BladeSafety.v.

**Versioning explosion.** `versioning_explosion`: 2^k ≤ c^k for c ≥ 2 —
specialization count under composition explodes (trichotomy support lemma).

## BladeDMWF.v — index universe and the double metamorphism

L0: deep embedding of symmetric index records; shapes as products; canonical-
tuple denotation `canonical r l u t` (r indices, each in [prev, u)). L1: the
Double Metamorphism With Feedback as a structural unfold whose seed is the
**residual index type**.

- `dmwf_equation`: enum(S r) l u = flat_map(i ↦ map (cons i) (enum r i u))
  (seq l (u−l)) — the feedback recursion itself (formalism §2.7's checked
  core: the emitted index structures the remaining space).
- `enum_sound` / `enum_complete` / `enum_NoDup`: the index metamorphism emits
  every canonical tuple exactly once.
- `enum_length`: cardinality by the same recursion (`mscard`); closed form in
  BladeBinomial.
- `lj_correct` / `unlj_correct`: the GENERAL left-justified storage bijection
  (formalism §12.1–12.2): canonical tuples ↔ storage tuples with shrinking
  bounds, round trips both ways, any rank.
- `dmwf_index_level_kernel_independent`: map fst (dmwf k₁) = map fst (dmwf k₂)
  — the index level does not depend on the kernel. Bridge to Lemma 2.2 /
  Theorem 2.3: structure exists before kernels.
- `tdim_relationality`: no function of array rank alone computes S-dims —
  the S/T split does not factor through arrays (Lemma 2.2's mechanizable
  core; why T/S admits no iteration objects).
- `residual_not_constant`: the residual seed family is not constant —
  dependent typing is forced (anchor for Theorem 9.3).
- `enumShape_length`: shape cardinality is the product over components.

## BladeBinomial.v — storage cardinality closed form

`mscard_binom`: mscard r l u = C(u−l+r−1, r), by the hockey-stick identity
(`hockey`) proved by induction over the sum the dmwf_equation produces.
`enum_length_binom`, and the headline `storage_cardinality`:
|SymIdx<r, n>| = C(n+r−1, r).

## BladeCounting.v — the general counting theorem

For d ≥ 2 dimensions of extents nⱼ ≥ 2 and rank r ≥ 2:

```
∏ⱼ |MS(nⱼ, r)|  <  |MS(∏ⱼ nⱼ, r)|          (counting_general)
∏ⱼ C(nⱼ+r−1, r) <  C(∏ⱼ nⱼ + r − 1, r)      (counting_general_C)
```

No lossless product layout exists, at any rank and dimension count. Proof
over the enumerations themselves: the pairing e(x,y) = x·b + y is canonical-
preserving and injective (`zipe_canonical`, `zipe_inj`); the sorted tuple
1 :: b :: ... :: b decodes to a non-sorted second component so it escapes the
image (`witness_canonical`, `witness_not_in_image`); pigeonhole
(NoDup_incl_length) gives the two-factor strict inequality (`two_factor`);
strict multiplicative induction lifts to d. Hypothesis sharpness noted
in-file: r = 1, d = 1, or any nⱼ = 1 give equality.

Classical context (recorded in-file): the inequality is the leading-term
deficit of the Cauchy decomposition Sym^r(V⊗W) ≅ ⊕_λ S^λ(V)⊗S^λ(W); product
storage captures only λ = (r); per-dimension Reynolds is the projection onto
it; the constructive r = 2 split is BladeCauchy.

## BladeMixedRadix.v — the product-shape rank/unrank bijection

The positive complement to BladeCounting. Within one identity group no
lossless per-dimension product layout exists (counting_general_C); across
**distinct** identity groups, per-group ranks composed mixed-radix ARE a
lossless layout. For a `Shape` (list of index groups, each an arity-rⱼ
symmetric group over [lⱼ, uⱼ)):

```
srank   : Shape → tuple → nat        (per-group rank · radix + rest)
sunrank : Shape → nat → tuple        (div/mod, inverted group by group)
```

`srank_in_range` (rank < shapeCard), `sunrank_srank` and `srank_sunrank`
(both round trips), `sunrank_in` (unrank lands in the canonical set),
`srank_injective`, packaged as `mixed_radix_bijection`; cell count
`shapeCard_binom`: ∏ⱼ C(uⱼ−lⱼ+rⱼ−1, rⱼ). The per-group rank is abstract
(position in the enumeration; lex by BladeLex, closed form at r = 2 in
BladeSafety), so the theorem is independent of any particular offset
formula.

Context (2026 literature sweep): per-group simplicial ranks are classical
(combinadics; Knuth TAOCP §7.2.1.3; ADOL-C `tensor_address`; Neidinger 2005
Eq. 7.4), and the *size* formula ships in CTF's `sy_packed_size` — but the
composed rank/unrank bijection for the product index set appears nowhere.
This file is the named artifact.

## BladeCurrying.v — dependence boundary; two maximal curryings

**Part A — the arrow dependence boundary** (an arrow is the maximal
dependence-closed unit of iteration):

- `rect_arrow_redundant`: a rectilinear 2-dim arrow is extensionally two
  independent 1-dim arrows — RectIdx is unnecessary; currying composes the
  factors.
- `sym_arrow_not_factorable`: for n ≥ 2 no per-dimension predicates P, Q
  reproduce i ≤ j — dependence forces the dimensions into ONE arrow.

**Part B — relationality and the two maximal curryings:**

- `identity_needs_every_position` (Lemma 9.23): a detector ignoring even one
  array position cannot compute the identity relation.
- `comm_needs_kernel` (Lemma 9.24): no function of arrays alone decides
  kernel commutativity.
- `two_maximal_curryings` (Theorem 9.26): over the currying lattice, the only
  proper, non-wasteful, detection-bearing specifications are
  all-arrays-no-kernel (method_for) and kernel-only (object_for);
  `method_spec_ok` / `object_spec_ok` witness existence.

## BladeCurryingGeneral.v — the generalized maximal-currying theorem

Two upgrades: (1) detection is **information-theoretic** — S detects a
property iff some predicate of the data S exposes defines it; the
characterizations `detects_arr_char` (array property ⟺ all positions bound)
and `detects_ker_char` (kernel property ⟺ kernel bound) are theorems derived
from FULL RELATIONAL SUPPORT (the property can flip at every position) plus
kernel-property nonconstancy. (2) the property is **abstract**:
`two_maximal_curryings_general` covers any output-relevant property with a
support witness; identity/commutativity is the instance
(`two_maximal_identity_comm`, via `all_same_support`, `comm_nonconst`);
`positional_detection_example` shows per-position properties do NOT force
full binding — full relational support is exactly the class that motivates
method_for.

## BladeLowering.v — symmetry lowering and raising, corrected

The organizing result is Theorem 9.11 in sound general form, over an OPAQUE
index type: a position permutation is licensed iff it is in H (kernel
invariance) AND stabilizes the array binding.

- `output_symmetry_soundness`: H + Stab ⇒ Out∘s = Out (any r, any s).
- `invariant_id/compose`, `stabilizes_id/compose`: licensed permutations form
  a monoid (for finite groups this generates the subgroup — s^k = id supplies
  inverses).
- `lower_2_1` (Theorem 9.9): comm kernel + identical arrays ⇒ symmetric
  output — derived AS AN INSTANCE of the framework.
- `shared_units_insufficient` (Theorem 9.17): distinct arrays over the SAME
  index space, commutative kernel — output NOT symmetric. H alone is not
  enough; **identity generates symmetry**. (This kills v10 §14.6.2's
  shared-index-space example.)
- `input_symmetry_not_sufficient` (Theorems 9.13/9.14): maximally symmetric
  input DATA, no H ⇒ no output symmetry. Input symmetry is consumed, not
  propagated.
- `raise_1_2` (Theorems 9.16/9.18): a symmetric array's accessor IS a
  commutative kernel — the raising direction, closing the round trip with
  lower_2_1.
- `diagonal_group_law`: the diagonal Group Law over composite pair indices —
  BladeCore's concrete instance at Ix := nat × nat, unified into the tower.
- Sign-tracked variant: `output_antisymmetry_soundness` (kernel
  anti-invariance ⇒ output antisymmetry, same two-step proof),
  `antisym_lower_2_1` (r = 2 instance). Hermitian is the identical statement
  with neg := conjugation; the diagonal-vanishing corollary needs group
  structure on U and is deliberately not claimed.

## BladeArrow.v — the arrow as coalgebra

An arrow is (state St, heads : St → list nat, step : St → nat → St); the DMWF
is the induced unfold; the FEEDBACK is the step.

General theorems for ANY arrow: `arrow_dmwf_equation`, `enumA_sound`,
`enumA_complete`, `enumA_NoDup`, and `const_state_rectilinear`: trivial
feedback ⟹ rectilinear domain. Only that direction is proved, and only it
holds for this state model. A state that changes only a depth counter
(heads s = [0;1], step s i = S s) has a rectangular domain, so non-constant
state does not imply dependence between coordinates (audit 2026-10-15, F03).
A true converse would need states taken up to observational equivalence.

Instances: **Sym** (step l i = i) — `sym_arrow_correct` proves the arrow
enumeration EQUALS BladeDMWF's enum as lists; **Antisym** (step l i = S i,
the compiler's StrictOffset) — `antisym_arrow_correct` + sound/complete/NoDup
corollaries + the strict left-justified storage bijection
`alj_correct`/`aunlj_correct`; **Compound** (masked, rank 2) —
`compound_arrow_denotes_mask`: the arrow enumerates exactly the true cells.

## BladeAffine.v — the affine feedback descriptor

`step l i = i + δ` unifies lj (δ = 0) and alj (δ = 1); storage domain
l + Σa + δ(r−1) < u. Round trips proved once (`dlj_correct`,
`dunlj_correct`, over `affine_arrow_correct`); four corollaries recover the
Sym and Antisym transforms exactly (`delta0_recovers_canonical/storageOK`,
`delta1_recovers_scanonical/astorageOK`). Strided strictness (δ ≥ 2) covered
for free. (Nonlinear δ(r−1) leaves discharged by nia.)

## BladeCompound.v — the rank-k Compound arrow

CompoundIdx over a rank-k mask M : list nat → bool, as the arrow whose state
is (remaining dims, prefix), whose heads admit SOME true completion, and
whose step appends. `has_completion` is the mask-conditioned residual — the
formalism's residual-compound / FilteredIdx — in executable form; it is why
the Compound feedback is value-dependent, hence (dependence dichotomy)
non-factorable unless the mask is a product.

`has_completion_witness`; `compoundk_sound` (every enumerated tuple in-bounds
and mask-true; stated for nonempty dims — designed base-case asymmetry);
`compoundk_complete` (unconditional); `compoundk_NoDup`;
`compoundk_denotation` (the arrow enumerates EXACTLY the mask's true cells,
at every POSITIVE rank: at rank zero the false mask still enumerates `[[]]`,
`compound_rank_zero_ignores_mask`); `compound_enum_denotation` (every rank,
zero included, via a wrapper that tests `M []`; audit 2026-10-15, F05);
`rank2_subsumed` (BladeArrow's rank-2 instance is the [n; m] case).

## BladeShape.v — shape-level uniqueness

`enumShape_NoDup`: shape enumeration emits each tuple exactly once, via
`canonical_length` (canonical tuples have fixed per-record length) and
`app_split_length` (unique app-decomposition). Completes the enumShape
theorem set (membership: BladeTrinity's `trinity_fold_closure`).

## BladeLex.v — iteration order = storage order

`enumA_lex_sorted`, proved ONCE at the arrow level: any arrow with strictly
increasing heads enumerates in strictly increasing lexicographic order.
Inherited: `enum_lex_sorted` (Sym), `affine_lex_sorted` (hence Antisym),
`compoundk_lex_sorted` (filtered heads stay sorted — the semantic enumeration
matches the compiler's dense mask-scan order). Payoff
`enum_offset_respects_lex`: earlier enumeration position ⇒ lex-smaller tuple;
since storage offset IS enumeration position, offset order embeds lex order.
(Converse routine via trichotomy + NoDup; noted as remark, no consumer.)

## BladeTrinity.v — positive constructions

The Trinity theorems (9.1–9.7) claim mutual dependence of loop reification,
arity polymorphism, dimensional currying. The honest mechanizable content is
(i) positive constructions and (ii) sharp non-constancy anchors; the
"requires" directions over all languages remain prose, grounded on these:

- `enumShape_app`: `<*>` IS shape concatenation — tuples of the combined loop
  are exactly the pairwise concatenations (positive half of 9.6/12.8).
- `enumShape_app_length`: cardinality is multiplicative under `<*>`.
- `trinity_fold_closure`: the fold of `<*>` over ANY list of shapes builds
  the arbitrary-arity loop — arity polymorphism by construction (9.1/9.6).
- `nat_not_arrow` (Cantor: nat ≠ nat → nat) and
  `output_family_not_constant`: the arity-indexed output type family is not
  constant, so the PRECISE (unboxed, uncast) output type must be indexed by
  the arity. This is the type-level anchor for 9.2/9.5, mirroring
  `residual_not_constant` one level up. It does not rule out every
  encoding: a tagged universal type such as
  `Host := HScalar nat | HFun (nat → Host)` embeds every arity, at the cost
  of run-time tag checks (audit 2026-10-15, F04).

## BladeTrinityAsym.v — the pillars are not co-equal

Loop reification and dimensional currying GENERATE; arity polymorphism is the
closure they force (witness that the first two exist without the third: the
original unary C++ object_for prototype).

- `sprod_unit_l/r`, `sprod_assoc`: the product of iteration spaces is a
  monoid — laws checked, not asserted (§12's assertions).
- `ap_semantics_is_free_fold`: the n-ary enumerator is the free fold of a
  binary combinator over reified arrows — a single non-arity-indexed
  function; arity is list length, not a primitive.
- `nary_generated_by_unary`: every n-ary loop is a product of unary loops.
- `enumShape_monoid_hom`: evaluation preserves the product EXACTLY (list
  equality) — evaluation is a strict monoid homomorphism, the checkable core
  of V–P monoidality (adjunction proper: conjecture, pending Plan Hom-sets).

Formalism action (adopted in v11 §10.2): state 9.7 as generators + closure,
not co-equality.

## BladeCompleteness.v — H-and-Stab EXACTNESS

The exactness half of Theorem 9.11 (soundness is BladeLowering). Both
necessity directions checked:

- `kernel_invariance_necessary`: a kernel not invariant under s is
  distinguished by free data (the data realizes the witnessing tuple).
- `fsum_max_symmetric`: the summation kernel is invariant under EVERY
  permutation (via Permutation reindexing) — the maximally symmetric kernel.
- `stab_violation_detected`: for any s violating the stabilizer at p₀, fsum
  plus indicator data distinguishes Out from Out∘s (Out ix = 1 vs
  Out (ix∘s) = 0 — the only position mapping to s(p₀) is p₀, which holds the
  wrong array).
- `license_exactness`: for fsum, uniformly licensed permutations = the
  stabilizer, as an **iff**.

Precise formalism reading (adopted in v11 §11.2): the largest grant sound for
EVERY H-kernel and ALL data is exactly H ∩ Stab. A specific degenerate kernel
can be accidentally symmetric beyond the grant — remark, not hedge; exactness
is about the uniform grant, the compiler's epistemic situation. Permutations
carry explicit two-sided inverses on [0, r) (`perm_pair`).

## BladeFusionDuality.v — fusion ⇒ duality

The method/object duality is DERIVED, not assumed. `fused_form` names the
fusion equation (Out = loop welded to indexing, kernel and arrays abstract);
`all_same_stabilizes` bridges the currying layer's identity detection to the
lowering layer's Stab premise; `detections_jointly_license`: the two detected
properties (commutativity via object_for, full identity via method_for) are
exactly the premises licensing symmetric iteration on the fused primitive;
`fusion_duality` restates the two-maximal-curryings theorem under the fusion
reading. Duality = fusion's two-sorted slot structure + detection pruning
(9.23/9.24); exactness (BladeCompleteness) is why nothing less suffices.
Formalism §1 opens with fusion and derives the duality by citation.

## BladeCompute.v — the computation model

Materialized-list semantics: a plan (equivalently an array — fusion makes
them the same kind of object) is a shape plus kernel; veval p = map kernel
(enumShape shape). All laws are list equalities — no functional
extensionality anywhere. Kernels consume single elements; slice-consuming
staged pipelines are BladeMonad; typed surface versions are future work.

- `veval_pval`: V ∘ P = id — embedding a value as a trivial plan and
  evaluating returns it. The counit identity; the checked constructive core
  of Theorem 2.1 (T/S is the trivial-plan fragment of S/T).
- `veval_not_injective`: two plans, same value — evaluation forgets how
  (Theorem 12.7's core).
- `rank0_convergence` (12.2) + `fuse2_pairs`: applying a 2-ary kernel via
  margs2 equals fused evaluation; fused evaluation = pairwise product
  evaluation (the semantic heart of `<@>` on pairs). Thin by design in this
  shallow semantics: both routes are maps.
- `compose_apply_duality` (12.1): veval (pipe p g) = map g (veval p) — the
  mechanized proof IS map_map, making the fusion connection exact.
- `slot_interchange`: composing through the kernel slot and through the
  array slot agree under evaluation — the 2-slot instance of the
  curryings ↔ slot-classes ↔ composition-operators correspondence; a third
  maximal currying (mixed-source property) would force a third operator and
  a triangle of interchange laws (open — BladeCurryingGeneral).
- `wrap0` round trips (12.3/12.4): `veval_wrap0`, `unwrap_wrap0`,
  `wrap0_idempotent`.
- Value-level MonadPlus pieces: `vplus_zero_l/r`, `vplus_assoc`,
  `pipe_distributes`.
- Deduced commutativity (9.19/9.20) in the invariance vocabulary:
  `raise_compose` (g ∘ symmetric-accessor is swap-invariant),
  `deduced_commutativity` (S(i,j)·A(i)·A(j)-style kernels are commutative).

Adjunction status: round trip (here), monoidality (BladeTrinityAsym), and
non-injectivity are checked; morphisms undefined, so the V ⊣ P adjunction
remains a conjecture.

## BladeCauchy.v — the r = 2 Cauchy storage split

For ANY rank-4 tensor T(i₁,j₁,i₂,j₂) symmetric under exchanging its two
(i,j) slots — the only symmetry one identity group grants — define (over ℤ,
factor 2 avoids division) Psym = T + lon-swapped copy, Qalt = T − lon-swapped
copy. Checked:

- `reconstruction`: 2T = Psym + Qalt.
- `Psym_lon`, `Psym_lat`, `Psym_both`: Psym has FULL S₂×S₂ product symmetry —
  the _lat direction uses the slot symmetry; that is where the split earns
  its keep. `Qalt_lon/lat` (sign flips), `Qalt_both`, `Qalt_diag_lat/lon`
  (vanishes on either diagonal).
- `cauchy_split_access`: T at ANY index is recovered from the two components
  at the per-dimension-SORTED cell with sign = product of the two sort
  parities (`qsign` lemmas) — per-dimension triangular storage of the two
  components is lossless.
- `cauchy_cell_count` / `cauchy_cells_3_3`: component stores together have
  exactly the joint count — division-free identity; 36 + 9 = 45 at extent 3,
  computed from the live enumerations (sym enum × sym enum + antisym arrow ×
  antisym arrow = joint enum).

Consequence (v11 §12.4): single-identity-group r = 2 (covariance class)
regains exact per-dimension product-STRUCTURED storage via SymIdx⊗SymIdx plus
sign-tracked AntisymIdx⊗AntisymIdx; the "flattening is forced" conclusion is
amended at r = 2. Honest scope: r = 2 only — and that boundary is now a
theorem, not an assumption: BladeDichotomy.v closes r ≥ 3 negatively and
shows this split optimal, not merely correct. Totals equal the joint count
(the win is structure, not cells); each read costs two lookups plus a
halving; the bridge to concrete lj/alj layouts is mechanical and not done.

## BladeDichotomy.v (26 theorems) — the r ≥ 3 storage dichotomy

Closes BladeCauchy's boundary in both directions at r = 3, following the
rank-dichotomy development (2026-08-02). The general theorem: the minimal
width of a scalar-access per-dimension-canonical scheme is **r!** — the fiber
over a free canonical cell carries the REGULAR representation of S_r, scalar
weights invert only 1×1 blocks, and S_r has exactly two linear characters;
2 = r! iff r ≤ 2. Self-contained, division-free (the factors 2 and 6
exhibited, never divided).

- **The witness** (`witness_symmetric`, `witness_nonzero`,
  `witness_components_vanish`, packaged as
  `naive_split_confuses_witness_with_zero`): an explicit integer tensor at
  extent 2 — value −2 on the diagonal orbit {(0,0),(0,0),(1,1)}, +1 on
  {(0,0),(0,1),(1,0)} — slot-pair symmetric, nonzero, and its sym AND alt
  components vanish at every index. Any access rule reading only those two
  components confuses it with the zero tensor: the natural r = 3
  generalization of `cauchy_split_access` is refuted by computed pin, in the
  register of BladeCounting's `witness_not_in_image`. (This is the
  S^{(2,1)} ⊗ S^{(2,1)} obstruction made concrete: 20 = 16 + 4 at extent 2.)
- **The width-2 refutation** (`width2_scalar_access_refuted`; ℤ instance
  `width2_refuted_over_Z`): over ANY commutative ring with 1 ≠ 0, no two
  weight functions ε₁, ε₂ : S₃ × S₃ → R reconstruct every symmetric tensor
  from two scalars per canonical cell. Three indicator tensors evaluate to
  the identity matrix at three fiber points of the free cell; soundness
  forces those rows into the span of two fixed vectors; the 3×3 determinant
  (`det3_span2`, a ring identity) vanishes on any such span but equals 1 on
  the identity. Storage is an arbitrary per-tensor function — not even
  linearity is assumed; the entire constraint lives in the weight span.
  Section-local `Add Ring` over an abstract `ring_theory`, so ℚ/ℝ instances
  are immediate.
- **The positive half** (`six_component_access`): the m = 3! = 6 scheme —
  store T(a, μ·b) for the six μ — reconstructs any slot-pair-symmetric
  tensor at EVERY index, tied or free, values in an arbitrary type; pure
  index algebra generated by the transposition and 3-cycle relations.
- **The isotypic access rule** (`isotypic_access_r3`, with `stdmat_is_rep`
  and the division-free column orthogonality `fourier_orthogonality_S3`):
  INTEGER matrices for the standard representation on the root-lattice
  basis; 6·g_μ = p_triv + sgn(μ)·p_sgn + 2·tr(ρ(μ⁻¹)·P_std) — 1 + 1 + 4 =
  6 = 3! numbers per cell (Wedderburn), the 6 exhibited exactly as
  BladeCauchy's 2. BladeCauchy's sort-parity product is the ρ^{(1,1)}
  instance of the same rule — the r = 2 and r ≥ 3 stories are one statement.
- **Cell accounting** (`cell_accounting_2_2`, `cell_accounting_3_3`): the
  repaired store is exactly lossless in count — the double-coset sum
  Σ_{(a,b)} |Stab(a) \ S₃ / Stab(b)| equals the diagonal-orbit count equals
  C(LM+2, 3): 20 at extent (2,2), 165 at (3,3) — the generalization of
  `cauchy_cell_count`, pinned from the live enumerations.

Honest scope: r = 3 — the load-bearing rank, where mixed Schur components
first exist — at a FREE cell (extents L, M ≥ 3, so some cell has all its
coordinates distinct). The general-r statement (minimal width r!, again at a
free cell) is prose; a general-r mechanization needs a ℚ[S_r] development,
exactly as BladeWreath's general-r exactness. Schemes with wider stores or
multi-cell reads are a different class, not refuted.

Below the free cell (L or M < r) every tuple has several sorting
permutations and a TIE-BREAKING RULE picks one; BladeDichotomyTies.v (next
section) settles that regime. The older prose called (3,2,2) a "genuine
exception" that "dies at (3,2,3)" and concluded the general theorem must
quantify over tie-breaks. Both conclusions were wrong: the r! theorem simply
needs a free cell, and below it the binding bound is a different one, which
the parity-graded rules attain at both extents.

## BladeDichotomyTies.v (27 theorems) — the dichotomy below the free cell

The model is BladeDichotomy's Part B with a tie-breaking rule: a rule picks
one sorting permutation (sorter) per lat and per lon tuple, and a width-m
scheme is m weight functions on S₃ × S₃ read at the picked sorters, with
arbitrary per-tensor storage. At (r, L, M) = (3, 2, 2) the six non-constant
tuples per dimension have two sorters each: 64 choices per dimension, 4096
rules. (The original count fixed the constant tuples' sorters; these
theorems quantify over them too.)

- **The census** (`choices_count`, `disjoint_choices_are_parity_graded`,
  `four_of_4096`, `parity_graded_sets`): exactly 2 of the 64 choices per
  dimension keep the 001-type and 011-type sorter sets disjoint, and they
  are the PARITY-GRADED ones (all sorters of 001-type tuples even and of
  011-type tuples odd, or the reverse: the two cosets of A₃). So 4 of the
  4096 rules.
- **The negative half** (`lat_overlap_refutes_width2`,
  `lat_overlap_refutes_width3`; any commutative ring with 1 ≠ 0, rule and
  weights arbitrary functions): if a 001-type and a 011-type tuple share a
  sorter, no two weight functions reconstruct every symmetric tensor at
  extent (2, 2), and no three at extent (2, 3). A shared sorter puts one
  weight row inside two cells' blocks; a 3×3 (4×4) unitriangular
  evaluation matrix lies in a 2-span (3-span), killed by `det3_span2`
  (new `det4_span3`).
- **The positive half** (`scheme322_sound`, `scheme323_sound`; values in ℤ,
  every constant-tuple sorter): for each parity-graded rule the explicit
  scheme ε = (1, χ), χ the small-orbit indicator of the one block reading
  each point, reconstructs every symmetric tensor at extent (2, 2); and a
  parity-graded lat rule with ANY lon rule admits width 3 at extent (2, 3).

So below the free cell the binding bound is max over cells of
|Stab(a) \ S₃ / Stab(b)| (2 at (3,2,2), 3 at (3,2,3)), and the
parity-graded rules attain it exactly at both extents: the "exception" does
not die at (3,2,3). Not mechanized: the lon mirror of the negative half,
and the widths of the non-attaining rules. Computed, not proved
(`proofs/scripts/dichotomy-ties/`, exact rational and GF(p) arithmetic):
at (3,2,2) the other rules need 3 (3036 rules), 4 (720) or 5 (48), with 288
between 4 and 5; at (3,2,3) non-parity lat rules need 5; at (4,2,2) the
bound ⌊4/2⌋ + 1 = 3 is attained by rules with disjoint sorter sets, none of
them sign-graded.

## BladeMonad.v — monad and combinator laws

Every law asserted in formalism 12.1–12.5 and the MonadPlus table has a
checked artifact at the materialized-value semantics.

1. **The computation monad** (bind = flat_map — the loop-nest monad):
   `vbind_ret_l/r`, `vbind_assoc`; MonadPlus EXACTLY as the text states:
   `vbind_zero_l` (left zero), `vplus_zero_l/r` (both identities),
   `vbind_plus_l` (LEFT distribution), bonus `vbind_zero_r` (right zero).
   **Right distribution provably FAILS for this monad** (results interleave);
   the text does not claim it and the rewrite must not add it.
2. **Plan-level plus**: multi-plans as block lists; `mveval_zero`,
   `mveval_plus_hom` (evaluation is a monoid homomorphism), `mpipe_hom`
   (12.1 at the plan-bag level).
3. **Rank-changing pipelines** (staged, second kernel consumes slices):
   `veval_blocks` (evaluation over a joined shape is a block-major traversal
   of slice evaluations — dimensional currying at the value level),
   `curry_concat` (blocks concatenate back — currying loses nothing),
   `pipeT` + `rank_changing_pipe`, `pipe_pipe` (associativity of `>>`).
4. **Section 12.5**: `parallel_associative` EXACT (flattened semantics
   strictifies the up-to-reassociation law), `parallel_commutative` as a
   genuine Permutation with explicit segment-swap reindexing,
   `application_not_commutative` (witness). Fusion-distributes (`<&!>`) is a
   performance guarantee — prose by design.

Out of scope here: array-level zip/stack/transpose laws (behavior layer /
compiler rewrite); k-slot structure and typed-path laws (surface calculus).

## BladeSafety.v — bounds safety with a failure model

Written in response to the external audit: a bounds-safety claim has content
only if the semantics contains a failure mode the typing provably avoids.
Store = materialized buffer; lookups via nth_error (CAN return None);
address = the compiler's triangular offset `roff`.

- `roff_closed`: 2·roff u s d = d(2(u−s) − d + 1) — the running block sum
  equals the closed-form polynomial the compiler emits.
- `typed_access_safe`: every in-bounds ordered pair's offset lookup returns
  exactly Some [i; j] — never None.
- `offset_in_range`, `offset_injective`: in range; collision-free.
- `bidx_access_safe`: with sigma-typed indices (BIdx/RIdx), the safety
  premises are discharged by **typability alone** — no runtime check, and an
  out-of-bounds index cannot be constructed. The type discipline is exercised
  against a real failure mode.
- `fusion_eliminates_buffer`: compose-apply (12.1) between genuinely
  different computations — the T/S route materializes stage one and indexes
  it through nth_error; the S/T route fuses the kernels and never builds the
  buffer; results agree. Loop-fusion soundness with a real store, not a
  map_map restatement.

Scope: rank 2 (the general-r offset is nested hockey-stick sums, still
unproved); surface progress/preservation is the remaining open species.

## BladeJacobian.v — the Jacobian symmetry transfer and symmetric accumulation

The AD-seam file: the theorems the AD plan leans on
(the retired AD-module plan's §3.3 consequence 2, §6.4/§6.5's symmetric
bullets, stage C5), closing the
"Jacobian symmetry theorem" and "symmetric gradient accumulation" open items
at rank 2. Formalized at the level the compiler actually differentiates
(Grad.fs is a syntactic transform): SYMBOLIC
differentiation on a minimal expression language — variables, constants,
+, ×, and opaque unary intrinsics with a FORMAL derivative slot (the
derivRule model; no real analysis anywhere) — with nat-semiring
evaluation and the structural ring-law congruence `aceq` (comm/assoc/
distrib closure). `aceq`-invariance under the swap is the hypothesis
class the compiler's parity deduction certifies (parities propagate up
the AST from primitives); it covers the paradigm commutative kernels
that syntactic invariance misses (`product_kernel_structurally_symmetric`,
`intrinsic_sum_kernel_structurally_symmetric`).

- `d_ren_equivariant` (equivariance): differentiation commutes with
  renaming — d i (ren s e) = ren s (d (s′ i) e) for any renaming with an
  explicit two-sided inverse; `d_swap_equivariant` is the transposition
  instance. A SYNTACTIC identity by structural induction, prior to any
  equivalence.
- `d_respects_aceq`: symbolic differentiation is a congruence for the
  ring-law equivalence — the comm/assoc/distrib generator cases are the
  Leibniz computations, closed inside `aceq`.
- `jacobian_symmetry_transfer` (+ `_rl`): if ren (swap a b) e ~ e, then
  ren (swap a b) (∂ₐe) ~ ∂ᵦe — the partials are each other's swap
  images: **Jacobians inherit the output symmetry in the corresponding
  indices**. Semantic form `jacobian_transfer_semantic` (+ `_rl`): ∂ₐe at
  the swapped environment equals ∂ᵦe. This is EQUIVARIANCE (inputs and
  derivative channel exchanged together), not symmetry of one partial:
  for x·y, ∂ₓ = y is not symmetric (`single_partial_not_symmetric`), and a
  plain canonical read of one partial is wrong (`naive_scalar_read_wrong`).
  Lossless storage is the PAIR of partials (`jpair`) read through
  `chan_read`, which exchanges the channels off the canonical order
  (`jpair_equivariant`, `channel_aware_round_trip`). An incoming cotangent
  is a separate contract (T3 below). Audit 2026-10-15, F02; the compiler's
  AD was probed and does not rely on the per-partial reading
  (`proofs/audit-compiler-bridge-2026-10-15.md`).
- `tangent_joint_swap` (+ `_semantic`): the emitted tangent kernel
  ∂ₐe·da + ∂ᵦe·db (§6.4's jvp schema) of a structurally symmetric primal
  is invariant under the JOINT pair swap (a,da) ↔ (b,db) — plan claim
  dk(a,da,b,db) = dk(b,db,a,da) exactly. Joint swaps only, per the
  product-symmetry correction; `per_dim_swap_not_symmetry` stands.
- `symclass_compose`: certified structural symmetries compose — the
  `aceq` analogue of `invariant_compose` (monoid closure; finite orders
  supply inverses).
- `semantic_hypothesis_insufficient` — the refutation half: in the
  formal-slot model, a SEMANTICALLY symmetric primal (constant-valued
  intrinsic, arbitrary formal-derivative interpretation) has a tangent
  that genuinely breaks the joint-swap symmetry. The transfer license
  must come from the STRUCTURAL judgment (declared/deduced comm — the
  `aceq` class), never from semantic accident: fixes where Tier-2
  emission may look, the transfer-theorem analogue of
  `per_dim_swap_not_symmetry`.
- `symmetric_accumulation` (the multiplicity rule): stored canonical
  cells as variables, logical cell (i,j) reading stored `canon2 i j`
  (BladeCore's canonical access; the decompact read pattern pinned by
  corpus index-types/034), loss = Σᵢⱼ cot(i,j)·M(i,j): the derivative
  w.r.t. stored cell (p,q) with p ≤ q is **cot(p,q) + cot(q,p)
  off-diagonal and cot(p,p) on the diagonal** — the orbit sum of the
  cell's logical aliases, proved with the same `d` as the transfer
  theorems. Concrete n = 3 pins: `off_diagonal_x2`, `diagonal_x1`.

Honest scope: rank 2 / one transposition (general-r transfer via
transposition lists and orbit multiplicities r!/|stab| per canonical
tuple are roadmap items); kernel-level statements — lifting to whole
materialized tangent ARRAYS composes with `output_symmetry_soundness`
(H := the transferred invariance, Stab := identical primal/tangent
bindings) in prose, not mechanized; nat semiring, so quotient/negation
kernels (Grad.fs's quotient/power rules) are outside this file; and the
intrinsic-derivative slot is formal by design — the refutation shows
that boundary is essential to the statement, not an accident of the
model.

## BladeSymPower.v — the S₂ partition and the copy-splitting counts

The counting obligations of the retired transforms-as-types plan
§3.2/§3.3b (listed
in its §8 as the σ-symmetric weight count and the cardinality half of the
Sym^k monomial-basis bijection), checked so the elaborator's internal asserts
have a proof behind them. Division-free throughout; no Clebsch–Gordan
machinery is modelled — a path is a two-constructor inductive carrying only
the numbers the compaction rule reads, which is what MLSpec.fs `tpPaths` +
`symTpKeptPaths` hand it.

**T1 — the S₂ partition** (this is what sizes stage 1's weight buffers):

- `s2_cells` enumerates a diagonal path's free multiplicity cells exactly as
  MLSpec.fs `s2TpSkeleton` does — the closed triangle u₁ ≤ u₂ at transpose
  factor τ = +1, the strict one u₁ < u₂ at τ = −1 — `s2_cells_spec`
  characterizes the enumeration both ways, and `s2_cells_length` identifies
  the count as `tri_le`/`tri_lt`.
- `tri_le_closed`, `tri_lt_closed_sub`: 2·tri_le m = m(m+1) and
  2·tri_lt m = m(m−1) — the two halves EXHIBITED as integers rather than
  divided (`s2_halves_well_defined`). `s2_halves_partition`:
  m(m+1) + m(m−1) = 2m², the local statement in the division-free register of
  BladeCauchy's `cauchy_cell_count`.
- `s2_cells_partition`, `tri_sign_partition`: at every m and either sign the
  two components' free cells account for exactly the m² dense cells.
- `s2_split_is_partition`: over a list of kept paths — a mirror pair giving q
  to each half of its 2q, a diagonal path giving the two triangles —
  sym_total + alt_total = dense_total. That is MLSpec.fs
  `s2TpSplitIsPartition`: the two closed-form component dimensions
  (`s2TpWeightDimClosed`, itself cross-checked against the packed enumeration
  on every call) add up to the dense `tpWeightDim`. `s2_worked_count_1`/`_2`
  compute §3.2's two worked tables: 10 = 7 + 3 and 48 = 28 + 20.

**T2/T3 — the copy-splitting counts** (the counting shadow of §3.3b's Move 1,
Sym^k(⊕_c U_c) = ⊕_{Σk_c = k} ⊗_c Sym^{k_c}(U_c)):

```
Σ over k₁+…+k_c = k of ∏ᵢ C(nᵢ+kᵢ−1, kᵢ) = C(Σnᵢ+k−1, k)    (sym_copy_splitting)
Σ over k₁+…+k_c = k of ∏ᵢ C(nᵢ, kᵢ)      = C(Σnᵢ, k)        (alt_copy_splitting)
```

`sector_list` enumerates the degree compositions and `sector_weight`
multiplies the per-copy dimensions along one, so the left sides are literally
sums over compositions rather than folds that compute them
(`sector_sum_expand` connects the two forms). The two-copy cores are
`multiset_vandermonde` and `vandermonde` — Vandermonde's identity, which the
stdlib does not carry (it has no natural-number binomial at all) — both by
convolution peeling (`conv_cons`, `conv_add_l`) off Pascal's rule. The right
sides are what MLSpec.fs `powerSpec` asserts on every call; `sym_sector_enum`
restates T2 over the tower's own enumeration through `storage_cardinality`.

Reused from BladeBinomial: `C`, `C_zero`, `C_small`, `storage_cardinality`.
Pascal's rule and C 0 (S k) = 0 are definitional for that C but unnamed there,
so they are named here (`C_pascal`, `C_zero_pos`) rather than restated.

Honest scope: counting only. T1 says the compaction loses and duplicates
nothing, not that the kept cells parameterize the equivariant maps — that is
Schur, cited (§6.1) and pinned numerically against the dense kernel
(ml-equiv/032–035). T2/T3 count the sectors; that the sectors are orthogonal
subspaces is §3.3b's construction. The cross-stage identity
`poly_weight_dim(s, 2, tp_spec(s,s)) = sym_tp_weight_dim(s)` is out of reach
here — it equates two counts computed through Clebsch–Gordan data — and stays
a compiler-side sweep (stage 2a, 15 specs to multiplicity 4).

## BladePartition.v — set partitions, RGS order, and the witness certificate

The stage 5a-i obligations of the retired transforms-as-types plan
§3.6 (the Sₙ
index-action member; staging item 5). `derive_perm_linear(K, L, N, …)` emits
one loop nest per **partition of the K + L index positions**, with the basis
element of a partition γ being its **coarsening indicator** B_γ — 1 on an
index tuple exactly when the tuple is constant on each block of γ.
MLPermSpec must enumerate the partitions, size them before allocating, and
certify independence *with integers*. Each of those is a theorem here.

A partition of [0..m) is modelled as its **restricted growth string**:
γ[0] = 0 and γ[i] ≤ 1 + max of the prefix, so the label set is always an
initial segment and the string is the partition's canonical name.
b(γ) = 1 + max, and 0 for the empty string (Bell 0 = 1, the empty partition).

**P1 — the enumeration is an arrow, not a new mechanism.** RGS is exactly
BladeArrow's coalgebra at `heads b = seq 0 (S b)`, `step b x = max b (S x)`,
so `rgs_enum_sound` / `_complete` / `_NoDup` / `_lex_sorted` are
`enumA_sound` / `enumA_complete` / `enumA_NoDup` / `enumA_lex_sorted`
instantiated (`canonA_rgs` identifies the arrow's canonicity predicate with
restricted growth). The partition enumerator joins Sym, Antisym, affine and
Compound in the same family. `rgs_enum_3` computes the five length-3 strings
in lex order.

Counts (all over the emitted list, not an abstract set):

- `rgs_enum_block_fibres`: the partitions with exactly j blocks number
  S(m, j). The enumeration recurses on the *first* position and Stirling's
  recurrence on the *last*; `stir_open_peel_last` proves the two agree, and
  `stir_open_stirling` closes it.
- `rgs_enum_length`: the whole enumeration is Bell m, by summing the fibres
  (`fibre_sum`, with `rgs_blocks_from_bound` supplying the finite range).
  `bell_pins` computes Bell 0..6 = 1, 1, 2, 5, 15, 52, 203;
  `rgs_enum_lengths` checks 1, 2, 15, 203 against the live enumeration at
  m = 0, 2, 4, 6.
- `rgs_enum_le_count` / `rgs_enum_le_count_min`: the ≤ N-block filter, which
  is what is realizable over `Idx<N>`, has length Σ_{j ≤ min(N, m)} S(m, j).
  `perm_weight_dim_is_bell` / `perm_bias_dim_is_bell`: at N ≥ K + L the count
  collapses to Bell(K + L) — the regime `perm_weight_dim` is defined on, the
  compiler erroring below it. §3.6's anchors are pins:
  `perm_weight_dim_deepsets` = 2 (DeepSets, Bell 2),
  `perm_weight_dim_maron` = 15 and `perm_bias_dim_maron` = 2 (Maron k = l = 2).
  `rgs_enum_le_truncates` shows the truncation biting: 5 → 4 at m = 3, N = 2.

**P2 — `rgs_lex_extends_refinement`, the triangularity keystone. Proved as
stated; no convention swap.** If γ′ coarsens γ (both valid RGSs of the same
length) then γ′ ≤ γ in lex order, so **coarsest-first emission extends
refinement**. The proof is a two-case analysis at the first position i where
the strings differ: they share a prefix p, hence the same prefix block count
b. If γ[i] < b then γ[i] already occurs in p (`rgs_values_cover` — the
restricted-growth condition is exactly what makes the label set an initial
segment), so coarsening forces γ′[i] to equal γ′ at that earlier position,
which is γ[i] — contradicting "differ". Hence γ[i] = b opens a new block,
while γ′[i] ≤ b by its own growth bound (`rgs_split_head`), so γ′[i] < γ[i]
and `lexlt_prefix` finishes.

§3.6's **fallback** convention (block count ascending, then lex) is
discharged as well, and needs no strictness argument:
`coarsens_blocks_le` shows coarsening never increases the block count (the
induced map on labels is onto, so `NoDup_incl_length` bounds them), and P2
settles every tie — `fallback_order_extends_refinement`. Both orders are
proved extensions of refinement, so the F# side's single order function may
pick either.

**P3 — the witness certificate, over the compiler's list** (the
`s2_cells_spec` discipline of BladeSymPower: theorems about the list the
elaborator emits). γ's witness tuple is its own RGS. `B_spec` unfolds the
indicator's evaluation semantics — `B γ′ t = true` iff t is constant on γ′'s
blocks, i.e. iff t coarsens γ′ — so the witness-evaluation matrix *is* the
refinement matrix (`witness_matrix_entry`). Its diagonal is true
(`witness_diagonal`), and `witness_matrix_unitriangular` combines P2 with P1's
lex-sortedness: a true entry at (row a, column b) forces a ≤ b. Unitriangular
over the emission order ⇒ invertible over ℤ ⇒ the emitted basis is
independent, with no float and no rank decision, exactly as §3.6 requires.
`witness_in_range` checks the other half of the certificate's legality: at
N ≥ m every entry of every witness is a legal `Idx<N>` value — which is why
the static N ≥ K + L guard is a real precondition, not a convenience.

Orientation is pinned by computation rather than prose: §3.6's
`B_{γ′}(RGS(γ)) = 1 ⇔ γ′ ≤ γ` reads ≤ as *refinement*, which unfolds here to
`coarsens γ γ′`; `witness_matrix_2` and `witness_matrix_3` compute the 2×2 and
5×5 matrices, so the triangle is fixed by a check (rows = witness, columns =
basis ⇒ upper unitriangular; transposing swaps the triangle and nothing else).

Honest scope: **independence** is proved, **spanning** is not. That the
coarsening indicators exhaust Hom_{Sₙ}(ℝ^{n^K}, ℝ^{n^L}) is the orbit-counting
half, cited under §6.1(a) exactly as Schur is cited for the O(3) member; the
compiler's own check in that direction is the numeric exact-rational
Reynolds/Gram oracle, not a theorem. Nothing in the file mentions characters,
irreps or Kronecker coefficients — which is §3.6's claim that the
permutation-module tier is character-free.

## BladePointGroup.v — the point-group registry, checked by computation

The stage 5b-0 obligations of the retired transforms-as-types plan
§3.6 (point groups
as the second block-spec member) and §7's 5b-0 bullet, whose mandate for this
file is *all computational over the witnesses: table closure, FS indicators, J
identities, the e-weighted sum; End-completeness cited, oracle-discharged*.
`MLPointSpec.fs` ships a **frozen integer registry** — FsType / PgIrrep /
PointGroup over the witness roster {C₄, D₄} — and asserts its integrity on
load. Every load-time assert has a theorem here, over the same matrices.

The **§3.6-canonical tables**, which the `.v` file and `MLPointSpec.fs` must be
kept in sync on (frozen table data, never derived at a call site):

| group | label | dim | generator images |
|-------|-------|-----|------------------|
| C₄ (order 4, gen r) | A | 1 | r ↦ (1) |
| | B | 1 | r ↦ (−1) |
| | E | 2 | r ↦ [[0,−1],[1,0]] = R₉₀; J = R₉₀; ℂ-type |
| D₄ (order 8, gens r, s) | A1 / A2 / B1 / B2 | 1 | (1,1) / (1,−1) / (−1,1) / (−1,−1) |
| | E | 2 | r ↦ R₉₀, s ↦ [[1,0],[0,−1]]; ℝ-type |

Every entry lies in {−1, 0, 1}: §3.6 picks the roster by **matrix
rationality**, not crystallography, so the F# oracle is exact-rational with no
field extension. Matrices are `list (list Z)`, multiplication/identity/
transpose/trace are defined outright, and the word sets are **fixed data**
written as generator-index words — `c4_words = [e; r; r²; r³]`,
`d4_words = [e; r; r²; r³; s; rs; r²s; r³s]` — so "the matrix of a word" is a
fold and every claim below is a finite check over an explicit list.
`mat_eqb_eq`, `mat_closed_sound`, `mat_nodup_b_sound` and `forallb_seq2` carry
each boolean check to its Prop reading; no theorem is left as a `= true`.
What has to be kept in sync with `MLPointSpec.fs` is the **table data** — label
roster, dimensions, FsType column, generator order, generator matrices. The word
list is this file's own choice of representatives (coset order); MLPointSpec
derives its own by breadth-first generator closure, so it may name the same
elements by different words. Both enumerate the same element set, which is
exactly what closure plus the element count assert on each side.

**Table closure and order.** `c4_table_is_group` / `d4_table_is_group` compute
that the Cayley tables are in range, associative, unital and closed under
inverses (512 triples at D₄). `c4_word_set_closed` / `d4_word_set_closed` are
the multiplication-table-closure obligation per irrep: a product of two
enumerated matrices lands back in the enumeration. `c4_element_count` /
`d4_element_count` read the order off a **faithful** irrep — E's matrices are
pairwise distinct and number 4 and 8 — so the word list is not a redundant
listing (`pg_orders` pins 4/4, 8/8).

**The rep property.** `c4_generator_relations` / `d4_generator_relations` check
the presentations (r⁴ = e; s² = e, srs = r³). `c4_rep_property` /
`d4_rep_property` are the group-law half proper: for every irrep and every pair
of word indices, ρ(w_i)·ρ(w_j) = ρ(w_{i·j}) against the table entry.

**Frobenius–Schur indicators computed = declared.** χ(g²) is the trace of the
squared word matrix; `c4_fs_sums` = (4, 4, 0) and `d4_fs_sums` = (8, 8, 8, 8, 8)
are Σ_g χ(g²) over the fixed word list. `c4_fs_exact` / `d4_fs_exact` show the
division by |G| is exact (the quotient is exhibited, the BladeSymPower
discipline), giving `c4_fs_indicators` = (1, 1, 0) and `d4_fs_indicators` =
(1, 1, 1, 1, 1) — **fs = 1 everywhere except C₄'s E, whose sum is 0: the one
ℂ-type label on the roster.** `c4_fs_computed_eq_declared` /
`d4_fs_computed_eq_declared` are the load-time assert: computed indicator =
MLPointSpec's declared FsType column.

**The chain FS → e → count.** `e_of_fs` maps indicator 1/0/−1 to e = 1/2/4 (the
ℍ value is *reserved* for double groups per §3.6, never a dead field), `irrep_e`
is `e_of_fs` of the **computed** indicator, and `pg_ev` is the registry lookup
over `irrep_e` (`pg_ev_is_fs_derived` names the link). So `c4_e_from_fs` =
(1, 1, 2) and `d4_e_from_fs` = (1, 1, 1, 1, 1) are consequences of traces, not
assertions, and the contrast anchor below is a chain rather than three
coincident asserts.

**The J identities**, which size the [Id, J] emitted basis of a ℂ-type label:
`J_square_is_neg_id` (J² = −Id₂ as integer matrices), `J_commutes_with_generator`
(J·ρ(r) = ρ(r)·J) and `J_commutes_with_C4_E` (over every word).
`c4E_end_gram_is_d_id` computes the Gram matrix of [Id, J] under ⟨A, B⟩ =
tr(AᵀB) as exactly 2·I₂ = d·I — independence over ℤ **with no rank decision**,
which is what §3.6 demands. Both of the design's negative controls are
refutations here: `c4E_diag_not_equivariant` — a spurious diag(1, −1) End column
dies at R₉₀, so it cannot pad the E block — and `d4E_J_not_equivariant` — J
fails to commute with D₄'s reflection, which is exactly why D₄'s E has e = 1 and
emits [Id] alone.

**ℝ-Burnside**, the table-integrity trap: `c4_rburnside` and `d4_rburnside` give
Σᵢ dᵢ²/eᵢ = 4 and 8, with `c4_rburnside_exact` / `d4_rburnside_exact` exhibiting
each quotient so a mis-typed e cannot hide behind a truncating division.

**The e-weighted count over enumerated block pairs.** §3.6's FS formula,
dim_ℝ Hom_G(⊕mᵢUᵢ, ⊕nᵢUᵢ) = Σᵢ mᵢ·nᵢ·eᵢ, is defined over the explicit block-pair
enumeration `hom_blocks` that `pgHomBlocks` emits — the `s2_cells_spec`
discipline again — and `hom_blocks_spec` characterizes that list exactly
(a block (L, m, n) is emitted iff (L, m) is an input entry and (L, n) an output
entry). `pg_hom_dim_spec_sum` rewrites the count as the pairwise sum it is meant
to be; `pg_hom_dim_add_l` / `pg_hom_dim_add_r` give biadditivity and
`pg_hom_dim_single` the one-block case. §3.6's **contrast anchor** is then two
computed theorems on one spec shape, [A × 1, E × 2] → itself:
`pg_hom_dim_c4_contrast` = **9** and `pg_hom_dim_d4_contrast` = **5**, with e
read from the computed indicators. `pg_hom_dim_c4_naive_control` closes the
argument: with e ≡ 1 the C₄ count collapses to 5, so **the FS correction is the
entire difference** (`contrast_is_fs_only` states the three together).
`trivial_label_counts` and `cross_label_blocks_empty` pin the trivial-label arm
(for 5b-ii's `invariantOffsets`) and the block-diagonality of the enumeration.

Honest scope: **End-basis completeness for general G is not modelled.** That
End_G(U) for an ℝ-irreducible U is ℝ, ℂ or ℍ and nothing else — the Schur-over-ℝ
trichotomy, hence that [Id] and [Id, J] *exhaust* the equivariant endomorphisms
and the e-weighted sum is the full dim_ℝ Hom — is **cited**, under §6.1's closure
("mathcomp is OUT": everything 5b relies on is either a finite integer
computation over baked data, which is this file, or a general theorem whose
shipped-group instance the exact oracle discharges). At the shipped witnesses it
is discharged numerically by `tests/Test_PgOracle.fs`, which builds the
exact-rational Hom-space Reynolds projector over ℚ and compares it entrywise to
the emitted basis — the same cited/computed division, and the same oracle
naming, as BladePartition.v's `Test_PermOracle`. What *is* proved on the End side
is the independence half plus both negative controls. Characters as class
functions, orthogonality, Clebsch–Gordan/fusion multiplicity (the CG-copy index
is §3.6's 5b-iii deferral) and any group off the roster are likewise absent:
nothing here is quantified over "all point groups".

## BladeWreath.v (85 theorems) — the wreath group for input-side product symmetry

The input-side companion to BladeCore's output-side refutations (formalism
3.4/12.5): two rank-r symmetric tensors combined by a pointwise kernel
`T[I,J] = f(A[I], B[J])`. Where BladeCore refutes per-dimension product
symmetry for one array's own indices, this file asks what symmetry the
*declared* symmetry of the inputs licenses on the combined object.

**Block-wise soundness, general r, unconstrained kernel.**
`block_product_symmetry_soundness`: for ANY `f` — no commutativity,
associativity, or any other hypothesis — permuting the r indices inside
block A and independently inside block B (any `s, t` with `permutes r s`,
`permutes r t`) leaves the output unchanged; the symmetry comes entirely
from the declared input symmetry of A and B. `left_block_symmetry` /
`right_block_symmetry` isolate the two generators, and
`block_product_symmetry_nonvacuous` instantiates the hypotheses at a
concrete symmetric tensor (`symsum`) to show they are satisfiable at every
r. `block_canonical_access_general`: reading the output at any per-block
canonicalization (any map returning a permuted copy of its tuple) recovers
the true value — the product-simplex store SymIdx<r,n> ⊗ SymIdx<r,n>,
C(n+r−1,r)² cells (BladeBinomial's closed form, squared), is lossless for
distinct symmetric inputs. No contradiction with BladeCore's
`counting_lemma_r2`: that theorem refutes product storage for the
JOINT-symmetric object of one identity group; this is a different object
(two identity groups), a different group.

**The wreath upgrade (repeated argument, general r).** With the second
input equal to the first (one identity group) and `f` commutative, the
block swap joins the licensed set for free (`wreath_block_swap`:
`f (A J) (A I) = f (A I) (A J)`), and composing it with the block-wise
S_r × S_r gives invariance under the full wreath product S_r wr S_2, order
2·(r!)² — `wreath_full_invariance`, stated for an arbitrary
`(b : bool, s, t)`, i.e. an arbitrary wreath-group element. The division of
labour is the doctrinal point: block-internal symmetry comes from the
input declaration alone; the block swap needs BOTH commutativity of `f`
AND identity of the two arguments — dropping identity loses it even with
commutativity retained (`block_swap_not_licensed`, Theorem 9.17 /
BladeLowering's `shared_units_insufficient` raised from r = 1 to r = 2,
distinct arguments, comm kernel).

**The wreath group is strictly inside S_{2r}.** `s4_orbit_not_licensed`:
two index tuples in the same S_4 orbit (`same_s4_orbit`, via
`s4_orbit_witness`) carry different values — so the sound joint form for a
comm-repeated symmetric argument is the wreath product, not the full
symmetric group on all 2r positions (the `OrbIdx` index types built on this
are in [features.md](features.md) §5; their retired plan is in git history).

**Exactness at r = 2, by finite enumeration.** Over 24 slot permutations
and 16 index tuples at extent n = 2: `distinct_stabilizer_is_block_group`
(the distinct-input stabilizer is EXACTLY the 4-element block group,
`distinct_stabilizer_count`) and `repeated_stabilizer_is_wreath` (the
repeated-input stabilizer is EXACTLY the 8-element wreath group,
`repeated_stabilizer_count`) — both checked to be composition-closed
permutation groups (`block_group_is_group`, `wreath_group_is_group`,
`block_subgroup_of_wreath`). `degeneracy_criterion` sweeps every symmetric
2×2 table with entries < 5: the stabilizer jumps to all of S_4 exactly on
the rank-one locus a·c = b² (`degenerate_witness_is_full_s4`, where the
output is a 4-fold tensor power), and is the wreath group everywhere else —
the exactness result is not witness luck.

**Extent-6 re-enumeration.** The same result at 6×6 (24 permutations ×
1296 tuples, kernel a parameter): `distinct6_stabilizer_is_block_group`,
`repeated6_stabilizer_is_wreath` (multiplication),
`repeated6_add_stabilizer_is_wreath` (addition) reproduce the r = 2 groups
exactly at a different extent. `rank1_6_degenerates_to_s4` /
`additive_rank1_6_degenerates_to_s4` scale the degeneracy, and
`additive_locus_is_kernel_relative` shows the degeneracy locus is
KERNEL-RELATIVE — the additive analogue collapses to full S_4 under
`f = +` while staying exactly wreath under `f = *` — so a compiler cannot
detect the collapse from the input alone; licensing must be by identity and
declaration, not by value inspection.

**Exactness at r = 3 — one rank past the seed.** r = 2 is a thin margin:
the wreath group has order 8 inside S_4's 24, and there every generator is
a transposition, so a reader may reasonably suspect the exactness is an
artifact of how little room S_4 leaves. Section 10 removes that doubt by
re-running the whole enumeration at r = 3, where 648 of the 720
permutations of S_6 must fail — including 6-cycles and every way of
interleaving the two index blocks — against all 3⁶ = 729 index tuples
(`perms720_card`, `cells_r3_card`). Nothing is assumed about which
permutations could work: the permutation list is GENERATED rather than
written out as data (720 six-lists is past the point where a literal table
is auditable) and then certified to be all of S_6 — 720 entries, pairwise
distinct, each a bijection of the 6 slots (`perms720_are_bijections`,
`perms720_distinct`). The two candidate groups are likewise DEFINED by the
block-preserving predicate and then shown to have the right orders and to
be composition-closed (`block36_card`, `wreath72_card`, `block36_is_group`,
`wreath72_is_group`, `block36_subgroup_of_wreath72`), rather than asserted
as tables. The rank-3 symmetric accessor reaches its table through
(min, median, max), so full S_3 input symmetry is definitional rather than
a property of the particular witness (`symtab3_perm`, `symtab3_S3`); table
entries are kept in 1..4 deliberately, since coincidences are cheapest at
small values, making exactness there the stronger result, not the weaker.

The four regimes come out exactly as sections 2, 3, 5 and 7 predict:

```
repeated + commutative      72 = 2·(3!)²   exactly the wreath group
distinct inputs             36 = (3!)²     exactly the block group
repeated + NONcommutative   36 = (3!)²     the swap dies
rank-one (degenerate)      720 = |S_6|     total collapse
```

`repeated_r3_stabilizer_is_wreath` is the headline — the stabilizer of a
repeated symmetric argument under a commutative kernel is EXACTLY the
72-element S_3 wr S_2, not a lower bound and not a sample — and
`repeated_r3_add_stabilizer_is_wreath` reproves it under `Nat.add`, so the
wreath answer is a property of the regime rather than of multiplication.
`distinct_r3_stabilizer_is_block_group` is section 5's refutation at r = 3
(exactly 36 = (3!)²). The sharpest of the four is
`noncomm_r3_loses_the_swap`: same repeated symmetric input, same
enumeration, and the only change is that the kernel (x²y) stops
commuting — whereupon exactly the 36 swap-containing permutations drop
out. That isolates the Z₂ factor of the wreath product as precisely the
commutativity license of section 3, and nothing else. The strict chain
`block36_lt_wreath72_lt_s6` (36 < 72 < 720) is derived from those
theorems rather than re-enumerated.

Three named permutations that S_6 allows and the wreath group forbids are
pinned with the exact tuple where the output disagrees
(`r3_pinned_are_not_wreath` plus `r3_cross_one_violates`,
`r3_interleave_violates`, `r3_sixcycle_violates`): a 6-cycle, a full
interleaving of the two blocks, and a single cross-block exchange — the
last being the MINIMAL departure from the wreath group, moving just one
slot across. `rank1_r3_admits_the_pinned_refutations` is the degenerate
control: at rank one (U3 = u_i·u_j·u_k, whose repeated product is a 6-fold
product of u-values) all three turn back into symmetries, so exactness at
72 genuinely depends on the witness being off the degenerate locus, exactly
as at r = 2 — and, as there, a compiler cannot license the extra symmetry
from the DECLARATION, since U3 and A3 satisfy the identical rank-3
symmetric declaration.

Honest scope (in-file generalization notes, BladeWreath.v §§2–3 and the
closing remarks): the invariance halves
(`block_product_symmetry_soundness`, `wreath_full_invariance`) are proved
at GENERAL r; the exact group orders (r!)² and 2·(r!)² are computed by
COMPLETE ENUMERATION at r = 2 (extents 2 and 6) and r = 3 (extent 3), and
beyond that are the classical orders of S_r × S_r and S_r wr S_2, cited
rather than proved. Past r = 3 the enumeration leaves Coq's reach — r = 4
is 40320 permutations × 3⁸ tuples — and the orders 2·(r!)² have been
confirmed at r = 4 and r = 5 only EXTERNALLY, by exhaustive exact-integer
witness enumeration outside this development (every non-wreath permutation
refuted by a concrete counterexample). That is evidence, not a Coq proof,
and the file records it as such. **General-r exactness remains open**: it
needs a BladeCompleteness-style detection argument (a maximally symmetric
probe kernel plus free data witnessing every violation) with the degeneracy
locus excluded by hypothesis rather than by computation, and a general-r
order proof needs a factorial-counting development over `permutes`.
k-block and antisymmetric-input generalizations are noted in-file but not
mechanized. Reproducible enumeration outside Coq: `proofs/OrbitEnum.fsx`
(dotnet fsi).

## BladeLayout.v (173 theorems) — striding parity and the layout group

The physical-layout companion to the H/Stab framework: a layout assigns
each of d axes to a memory level and a direction, so layouts are the
hyperoctahedral group B_d = Z₂^d ⋊ S_d, order 2^d·d! (orders 8, 48, 384
computed; closure, inverses, associativity, and normality of the flip
subgroup all checked as theorems over the enumerated tables).

**The headline: the licensing vocabulary has exactly 4 elements.**
BladeLowering's framework has exactly two grant forms (invariant,
anti-invariant), so the grade of a licensed move composes by XOR
(`graded_invariant_compose`, `licensed_compose`) — anti composed with anti
is invariant (`licensed_anti_anti`) — the grade is unique off the
degenerate locus where `neg` has no non-fixed point (`sign_determined`),
and there is no third form (`grant_forms_exhausted`). All of that holds at
arbitrary rank, arbitrary kernel, arbitrary index type. So the licensing
vocabulary is Hom(B_d, Z₂), and that group has EXACTLY 4 elements: trivial,
permutation sign, flip parity, and their xor — exhibited, checked pairwise
distinct, and shown complete by a generator argument. B_d is generated by
the Coxeter set (all d−1 adjacent transpositions plus one flip;
`b2_generated_by_coxeter`, `b3_generated_by_coxeter`, with a word carried
for every element); one transposition plus one flip is NOT enough past
d = 2 (`tau_phi_does_not_generate_b3`, 8 of 48); conjugacy forces one value
on all transpositions and one on all flips while keeping the two classes
apart (`transposition_not_conjugate_to_flip_3`), hence
`b2_character_classified` / `b3_character_classified`: every character
equals `chi_of` on its two generator values.

**Forward canonicalization is a quotient, not a restriction.** Direction is
free gauge exactly when the reduce monoid is commutative AND associative
(`direction_is_free_gauge`, with both hypotheses separately refuted); Z₂^d
is normal, and B_d/Z₂^d has d! cosets (`quotient_coset_count` at
d = 2, 3, 4); the rule-out factor is MULTIPLICATIVE — 2^d from direction
with no declaration at all, times |G_S| from the declaration
(`ruleout_no_declaration`, `ruleout_full_symmetry`, with orbit
representatives proved stable). Exactly the 2 characters trivial on the
flips descend (`exactly_two_characters_descend`) — they are SymIdx (+1) and
AntisymIdx (−1), so the whole representation theory needed for forward
layout selection is already in the type system; the other 2 are the sign a
backward layout picks up on the way to its forward representative.
CONTRAST: 2 (resp. 4) characters, constant in d, against #irreps = the
partition count 1, 2, 3, 5, 7, 11, 15, 22, 30, 42 (resp. the bipartition
count 2, 5, 10, 20, 36, 65, 110, 185, 300, 481) — both computed here and
both growing without bound; the irrep classifications themselves are
classical and cited.

**The guarantee (canonical form).** Reference cost is the Kendall-tau
inversion count between a reference's memory-axis order and the loop
order; cost 0 is formalism §9's outermost-slowest ideal and is attained
(`kendall_self_is_zero`). The licensed search space — every combination of
licensed moves crossed with every loop order — is enumerated EXHAUSTIVELY,
and canonical form (minimum cost, lex tie-break) is proved to lie in that
space and to be a cost minimizer of it, at ANY d, ANY licensed group, ANY
reference set (`canonical_is_in_space`, `canonical_is_cost_minimal`). The
reported minimum `best_cost` equals the canonical cost on every nonempty
space (`best_cost_agrees_canon`); it used to fold from a 99 sentinel and
report 99 for a true minimum of 100 (`best_cost_no_sentinel_cap`; audit
2026-10-15, S02). Uniqueness is decided per instance, with `is_canonical` the decision
procedure — the layout analogue of asking whether an index tuple is
sorted.

**Worked cases.** `C[i,j] = A[i,j]·A[j,i]`: no loop order reaches cost 0
without the declaration
(`tpair_zero_unreachable_without_declaration`), and the symmetry rewrite
does (`tpair_symmetry_reaches_zero`), value-preserving up to the character
(`canonicalization_preserves_value_up_to_character`) — the declaration is
load-bearing here. Matrix square is the honest CONTRAST: loop reordering
alone already reaches 0 (`msq_loop_reordering_suffices`), so it is NOT
evidence for the declaration. Propagation closes it: characters compose by
xor along a pipeline (`character_composes`), instantiated on the transpose
bridge (AB)ᵀ = ε_A·ε_B·(BA)ᵀ in all four sign cases at general n and pinned
at 3×3 over ℤ.

Honest scope (stated in-file): total cost 0 is NECESSARY but not
SUFFICIENT for fastest — the metric ranks stride coherence, not reuse or
vectorization — and no claim about cache hardware is made anywhere.

## BladeOptimal.v (73 theorems) — uniform optimality, one nest

Soundness (BladeLowering) and exactness (BladeCompleteness) compare the
compiler's choice against other **grants**. This file compares it against
every **program** ([plan-uniform-optimality.md](plans/plan-uniform-optimality.md),
T1/T1′/T2). The kernel is an oracle known only to lie in the declared law
class; a program (`prog`: adaptive, any order, any structure) owes the right
output for every kernel in the class.

**The abstract bound.** `orbit_lower_bound`: cells whose argument tuples lie
in pairwise distinct classes, each class *perturbable* inside the law class,
cost one query each. The adversary moves the kernel on the one class the
program never asked about; `run_agree` says the program cannot tell. No
group theory and no counting — those live in the instances.
`orbit_storage_bound`: an opaque-cell store read through a fixed access map
*is* the non-adaptive program that asks every cell, so the cell count obeys
the same bound.

**H = S_r, one array.** C(n+r−1, r) questions are forced
(`sym_nest_lower_bound`) and as many cells (`sym_nest_storage_bound`);
BladeDMWF's `enum` attains both and serves every cell of the *dense* index
space by sorting the index tuple (`sym_nest_attained`). Headline
`uniform_optimality_sym`: no correct program asks fewer questions than the
canonical enumeration, against any kernel in the class. The class is the
tower's own — local, and `invariant_under` every `perm_pair` — proved equal
to "constant on sorted tabulations" (`Kcls_Ksym`, `Ksym_Kcls`; the converse
turns a list `Permutation` into a position permutation with a two-sided
inverse via `Permutation_nth` and `FinFun`).

**H ∩ Stab proper.** H = S_R over *several* arrays, block j of r_j positions
reading array j, so G is the Young subgroup and the forced count is
BladeMixedRadix's `shapeCard` = ∏ⱼ C(nⱼ+rⱼ−1, rⱼ)
(`young_nest_lower_bound`, `young_nest_attained`,
`uniform_optimality_young`). The new content is `svals_separate`: generic
data remembers which array it came from, so a permutation of the whole value
tuple restricts to one per block. `shargs_is_Out` ties the shape to
BladeLowering's `Out` at binding `sgrp`; `uniform_optimality_young_nat`
discharges the genericity hypotheses by Cantor pairing, so the statement is
closed.

**Signed, r = 2** (BladeLowering's `antiinvariant_under`, neg = `Z.opp`). A
class is *free* when it can be coherently oriented (`sigma`). The strict
pairs are, and cost C(n, 2) questions (`antisym_lower_bound`,
`antisym_attained`). The diagonal is not: every kernel in the class vanishes
there (`antisym_diagonal_forced_zero`) and the attaining program spends
nothing on it — AntisymIdx storing no diagonal is forced, not chosen.

**Refutation.** `nongeneric_data_beats_bound`: on a constant array one query
answers every cell. The bound is about generic data, hence about every
data-oblivious schedule, which must be right on it.

Honest scope (stated in-file): the kernel law is the full symmetric group on
the argument positions, at any binding, plus the sign character at r = 2. A
proper subgroup H < S_R, the signed case at general r, and non-opaque storage
(finite-U counting, linear encodings — BladeDichotomy's setting) are prose.
Cost is kernel evaluations and cells, **not time**: with BladeLayout's
`canonical_is_cost_minimal` the tower now holds two necessary conditions for
fastest and still no sufficient one.

## BladeOrbitWork.v (42 theorems) — uniform optimality, pipelines

The composed case (plan T3). Output cells are terms over data leaves and
several kernel symbols — unary or binary, some declared commutative — and
every symbol is an oracle. Perturbing one kernel changes the arguments every
downstream kernel sees, so the one-nest adversary does not transfer; the
proof runs in a **free model**: `kfree` is a perfect hash of its canonical
query, so a value *is* the name of a subterm class.

- `min_bad`: a class the program never asked about has a minimal unasked
  subterm beneath it.
- `below_same`, `root_fresh`: moving the oracle on that one class leaves
  everything below unchanged and sends the subterm itself to a spare value.
- `taint_up`, `clean_not_taint`: injectivity carries the change to the root —
  a tainted argument taints the result, and no free-model value is tainted.
- `orbit_work_lower_bound`: a program correct for every oracle in the law
  class asks at least one question per distinct subterm class modulo the
  laws — the size of the maximally shared term DAG.
- `memo_prog` (evaluate bottom-up, look the *canonical* query up before
  asking) is correct for every oracle in the class and never asks a class
  twice (`memo_prog_correct`, `memo_prog_cost`); in the free model it asks
  exactly one question per class (`orbit_work_attained`), so no correct
  program beats it (`uniform_optimality_pipeline`).

**The orbit work formula** W = Σᵥ ω(G_v, X_v) — each node counted over its own
support, modulo its own group; the attaining program is dimensional currying
read as a cost statement. Checked on Out(i, j) = g(h(A i), h(A j)) with g
declared commutative: `two_node_orbit_work` gives W = n + C(n+1, 2) (node h
over {i} with no symmetry, node g over {i, j} modulo S₂) against
`two_node_fused_work`'s 3n² class occurrences in the fused dense nest — the
support gap and the group gap in one number; `two_node_optimal` is forced and
attained. `orbit_work_nat` / `two_node_orbit_work_nat` close every hypothesis
over nat (leaves 3i, spare value 2, hash 3·code+1 by Cantor pairing);
`two_node_computed` pins n = 3 by `vm_compute` (9 questions, 27 occurrences).

Honest scope (stated in-file): arity ≤ 2 and commutativity only — arity r and
general H are BladeOptimal's subject, one nest at a time. **Folds are not
modelled**: sharing partial sums across overlapping fibers is Ensemble
Computation, NP-complete, outside any theorem of this shape. The bound is on
queries in the free model; the general schema formula is prose, this file
proves the term form and one instance.

## BladeDeduceExact.v (34 theorems) — what symmetry deduction is exact for

BladeDeduce proves every rule that *answers* sound. This file asks the
converse (plan T4): when the deduction says nothing, was there really nothing
to say? It answers in BladeOrbitWork's term model — a kernel body is a term
over its parameters, operator symbols uninterpreted up to a declared
commutativity.

- `uniform_equality_exact`: two bodies agree under **every** interpretation
  respecting the declared laws, on all data, iff they are equal modulo those
  laws (`teq`). Soundness is an induction; completeness is the free model,
  which is faithful (`free_faithful`). Transitivity of `teq` comes for free —
  it is transitivity of equality in the models (`teq_trans`).
- `uniform_symmetry_is_automorphism`: so the uniform symmetry group of a body
  is its syntactic automorphism group modulo the laws, and membership is
  decidable by a structural walk (`teqb`, `teqb_spec`).
- `mirror_spec`: `src/Deduce.fs`'s `mirrorEq`, modelled node for node, **is**
  that walk composed with the swap — `mirror i j l r = teqb (swap l) r`.
- `parfix_exact`: the deduction rule answers PInv on a transposition
  **exactly** when that transposition is a uniform symmetry.
- `deduced_group_sound`, `deduced_is_maximal`, `deduced_group_largest`: the
  group the compiler grants is the one *generated* by the adjacent
  transpositions it certifies; it is sound, and it contains every group
  generated by adjacent transpositions inside the symmetry group — the largest
  contiguous-block Young subgroup. `young_generated` closes the loop in the
  tower's list semantics: such a subgroup is generated by its interior adjacent
  transpositions (BladeDeduce's `adjacent_transpositions_generate`, from one
  block to a shape).

**The finding.** `par` models the rule in the order it shipped until
2026-09-19: mirror first, a mirror hit answering from the op's swap class
alone. That was sound (`par_sound`, `par_le_parfix`) but incomplete —
`shipped_rule_incomplete`: under a non-commuting op the mirror hit answered
"no claim" without looking at the operands, so `(x + y) / (y + x)` deduced
nothing. The compiler confirmed it: no BL4010 on that body, and a *false*
`where anticomm(x, y)` pin on it accepted where BL4013 should refuse.
`parityOf` now falls through to the chain rule, and
`tests/corpus/symmetry/049`–`051` pin the deduction, the pinned twin, and the
refusal. `parfix` is the rule as it now stands.

**Out of reach by design**, each a closed witness: `nonadjacent_symmetry_missed`
(g(h(a, c), b) — the transposition (0 2) is a symmetry, no adjacent one is) and
`wreath_symmetry_missed` (g(h(a, b), h(c, d)) — S₂ × S₂ deduced, BladeWreath's
order-8 wreath product present). n − 1 adjacent checks buy exactly the
contiguous-block Young subgroups, and the theorems above say the deduction
gets all of those.

Honest scope (stated in-file): the **unsigned** fragment — variables, unary and
binary operator symbols, commutativity as the only law. PNeg (the sign chain
rule), PConj, call summaries, reduce, if/match and let-flattening are not
modelled; BladeDeduce covers their soundness and nothing here speaks to their
completeness. Associativity is deliberately not a law: it is not licensed for
floating point, and the free model agrees.

## BladeObject.v (40 theorems) — the one model: the DMWF is the quotient

The tower had four models, each the right one for its file: the index universe
and its enumeration (BladeDMWF, BladeArrow, BladeShape); the materialized-value
semantics `Arr U = Shape × (list nat → U)` with `veval` (BladeCompute,
BladeMonad); the oracle model of adaptive programs (BladeOptimal); and the free
term model (BladeOrbitWork, BladeDeduceExact). This file
(`docs/plans/plan-one-model-surface-calculus.md`, item 1) states the object
they are all about and proves that the first three are its projections; the
fourth is the licence bridge in BladeSurface.

The object is the DMWF itself, with its quotient property made explicit. The
feedback of the Double Metamorphism — the emitted index becoming the residual's
lower bound — enumerates exactly one representative of every orbit of the
symmetric group on the dense box. The feedback *is* the group action, computed.

- `canonical_iff_nondecr`: the DMWF's canonical tuples are exactly the dense
  tuples (`dense_tuple`, enumerated by `box`) that are nondecreasing.
- The transversal theorem. The orbit relation of S_r on tuples is list
  `Permutation`; the canonical form is BladeOptimal's insertion sort.
  `enum_hits_every_orbit` (every dense tuple is a permutation of a canonical
  one), `enum_hits_each_orbit_once` (two canonical tuples in one orbit are
  equal), `isort_fibres_are_orbits` (the canonical-form map's fibres are the
  orbits), and `enum_is_quotient`: `enum r l u` is the image of `box r l u`
  under `isort`. `orbit_count` reads BladeBinomial's `C(u − l + r − 1, r)` as
  the number of orbits. `isort_canonical_of_dense` is the general-`l` form of
  BladeOptimal's `isort_canon`.
- Blockwise, for shapes: the Young action permutes within each record's block
  (`block_perm`), the dense box of a shape is `shape_box` / `shape_dense`, and
  BladeOptimal's `scanon` is the blockwise canonical form. `scanon_in_shape`,
  `scanon_block_perm`, `enumShape_hits_each_orbit_once`,
  `scanon_fibres_are_orbits`, `enumShape_is_quotient` restate the transversal
  theorem for every shape; `shape_box_length` counts dense cells
  (∏ (u − l)^r) against `shapeCard_is_orbit_count`.
- Arrays on the quotient (projection 1). An `Arr U` is a function on the
  transversal; `qread A t = snd A (scanon (fst A) t)` is the canonicalizing read
  of formalism §3.9 and §12.2 (`cov(3, 1)` IS `cov(1, 3)`). `qread_invariant`
  (constant on orbits), `qread_fixes_transversal` (the identity on canonical
  tuples), `qread_is_storage` (equal to the lookup at the canonical tuple's
  position in `veval` — storage, iteration and access as one object),
  `veval_one_per_orbit`.
- The object as a program (projection 2). `object_prog s = ask_all (enumShape s)`:
  `object_prog_run` (its result is `veval`), `object_prog_trace` (its questions
  are the transversal), `object_work` (it asks exactly `shapeCard s`
  questions), `young_prog_is_object_prog` (BladeOptimal's attaining program for
  the Young nest is the object's program read through the data), and
  `object_is_optimal`, which with `uniform_optimality_young_nat` closes the
  circle: the orbit count is forced by every correct program and attained by
  the object.

## BladeSurface.v (68 theorems) — the surface calculus: progress, preservation, and the quotient as a theorem about typing derivations

The structure-first core of Blade (formalism §7, §8, §10, §13, §14), closed over
a global binding environment — Blade's top level, where identity is by name
(§8.3). Plan item 2. Arrays `EArr`, names `EVar`, loop objects from either side
(`EMethod` = the outer product, `EZip` = co-iteration, `EObject`), application
`EAppM` / `EAppO` (`<@>` from either side, §7.1), `ECompute`, and the
elementwise `EPipe` (`>>@`). A `kernel` is an arity, a body over
position-indexed values and a `comm` flag; `kwf` is Fun-Intro's "metadata
well-formed" premise: locality, and `comm` only when the body is invariant under
every permutation of its positions (BladeLowering's `invariant_under` over
BladeCompleteness's `perm_pair`; `kwf_Ksym` ties it to BladeOptimal's law
class).

The licence lives in the typing rule for application. `out_shape` groups
neighbouring equal names (`rle`, §8.3) and, under `comm`, replaces a group of
`g` rank-1 operands over `[l, u)` by the ONE DMWF record `(g, l, u)`
(`group_shape`) — §8.4 step 3 at `s = 1`, §11.2's `H ∩ Stab` as a function of
the derivation. That is the only place structure is introduced, and what it
introduces is exactly a DMWF record: the sense in which the DMWF is the core.

- Type soundness: `progress`, `preservation`, `step_size` (a size measure
  strictly decreases), `normalizes` — under `env_ok G rho`. Canonical forms
  (`canon_loop`, `canon_zip`, `canon_obj`, `value_arr`, `value_comp`) and the
  inversion of the three application typings (`ty_appM_method`, `ty_appM_zip`,
  `ty_appO`).
- Denotation into the one object: `den rho e : Arr nat`; `step_den` (every step
  preserves it on the nose — no extensionality anywhere); `eval_sound` and
  `eval_veval` (every well-typed array term evaluates to an `EArr` carrying its
  denotation, so `veval` of the denotation IS what the program computes: the
  materialized-value semantics is the calculus's).
- `typed_app_is_quotient`: for a well-typed `L <@> f`, the typed output shape is
  `out_shape`; every cell of the DENSE output (what a collection-first system
  computes) satisfies `shape_dense` of the typed shape and is recovered from the
  typed array by `qread`; and the typed array has exactly one cell per orbit of
  the identity-group action, which is what the object's program asks. The
  discharge is `apply_kernel_canon`, by induction over the identity groups
  (`group_quotient`, `groups_quotient`): sorting a group's index segment
  permutes the kernel's value list (`vals_repeat1`, `Permutation_map`), and
  BladeOptimal's `Ksym_Kcls` turns the declared position-invariance into
  constancy on the sorted value tabulation — the position permutation the sort
  induces is never constructed.
- `comm_license_needed`: with `comm` off the typed shape is the dense one.
  `covariance_shape` / `covariance_cells_5`: `method_for(A, A) <@> f` over
  `Idx<n>` types at `SymIdx<2, n>`, `C(n + 1, 2)` cells, 15 at `n = 5`.
  `aba_unlicensed`: `(A, B, A)` is three singleton groups. `swidth_out_shape`:
  grouping never changes the output RANK, only which tuples are stored;
  `method_vs_zip_rank`: the outer product and co-iteration of the same operands
  differ in output rank (the documented bug class).
- The free-model bridge, `deduced_comm_is_licence`: a binary kernel whose body
  is a term equal modulo the declared laws to its own swap (BladeDeduceExact's
  `teq`, what `src/Deduce.fs`'s mirror walk decides) is a well-formed `comm`
  kernel under every interpretation respecting the laws (`perm_pair_2`,
  `eval_local`, `teq_sound`) — deduction is a licence source.

Scope, stated in-file: rank-0 kernels (no T-dimensions, no `irank` slices);
identity groups quotiented over rank-1 operands only (§8.4's `s = 1`; the
compound group is plan item 3 / decision D1); values are `nat`; no local `let`
(expressions are closed over the top-level environment); no folds; no `let rec`
(plan item 5). Each is a listed item of the plan, not a hole in the argument.

## BladeTransport.v (103 theorems) — functoriality of the quotient; the orbit work of a pipeline

A shape IS the group data (`BladeObject.enumShape_is_quotient`), so the group
of a computation is the shape its derivation assigns. This file (plan item 3)
states how that shape is transported through each combinator (formalism
§10.2–10.5) and what a composed computation costs.

- *`<*>`.* `rle_app`: the identity groups of `method_for(xs1 ++ xs2)` are the
  factors' encodings glued at the seam. `out_shape_app_clean`: with distinct
  boundary names the typed shape is the concatenation of the factors'
  out_shapes; `block_perm_app`: the orbit relation of `s1 ++ s2` is the
  product; `shapeCard_product_clean`: the count multiplies.
  `shapeCard_product_seam`: in general the glued group is larger and the count
  never exceeds the product (`seam_merge_pin`: 15 vs 25). `dense_shape_app`:
  the dense loop always splits.
- *zip.* `zip_transport`: the operands' own (common) group, arity-independent;
  the canonical read is the diagonal of the operands' reads. The pullback is
  trivial: `T_Zip` refuses mismatched shapes.
- *`>>@`.* `pipe_transport`: the first stage's group; `qread` commutes with the
  stage; `pipe_object_prog`: no new traversal step (Part E counts its one
  kernel application per orbit).
- *`<&>` / `<&!>`* (a pair layer above BladeSurface: `has_ty2`, `den2`,
  `step2`, `pair_sound`). `par_group_product`: σ(C₁ <&> C₂) = σ₁ × σ₂.
  `fuse_meet`: both outputs carry the out_shape of the conjunction of the
  licences (necessary: `meet_necessary_pin`); `fuse_is_par`: cellwise equal to
  the separate applications. `fusion_one_traversal` / `fuse_object_prog`: the
  fused object program visits each cell of the meet once and its run is
  `den2`'s pair; with agreeing licences the unfused pair takes twice the
  traversal steps. `fusion_traversals_never_cost`, but `fusion_evaluations`:
  fusion never saves kernel evaluations and costs some when licences disagree
  (`fusion_mismatch_pin`: 50 vs 40).
- *Orbit work.* `orbit_work` sums `shapeCard` over a pipeline's stages.
  `pipeline_attains_orbit_work`: the composed object program (each stage
  materialized once, read back from storage) asks exactly `orbit_work`
  questions and every stored stage is `veval` of its denotation.
  `stage_is_surface`: every stage, `Prev` sources included, IS the surface
  term `method_for(names) <@> k` over its local operand environment; under
  `pipe_ok` (kwf, arity) it is typed, `group_of` reads off its shape, and it
  is the quotient of its dense stage. `two_node_translation`: for
  `g(h(A i), h(A j))`, g comm, the calculus's program relabelled to
  BladeOrbitWork's symbols is a free-model program correct for every oracle in
  the law class, asking exactly `n + C(n+1, 2)` — the free-model lower bound
  applies to it and it attains it (`pipe_prog_shape_only`: bodies live in the
  oracle).
- *`group_of`.* The group as a function on the syntax of `expr`;
  `group_of_den`, `group_of_laws`.
- Pins: `three_stage_orbit_work` (35 / 55), `three_stage_run`,
  `fused_work_pin`, and the scope pins `d1_not_built_pin` (16 vs D1's 10),
  `typed_work_not_optimal_pin` (12 vs the free model's 9).

Scope: compound identity groups and groups of symmetric operands are NOT
quotiented (D1 not built; such stages count densely). Values nat; folds out
(Ensemble Computation). A general schema-to-free-model translation would give
free lower bound ≤ orbit_work, with equality only when each comm stage's typed
group is its whole licence; the program-level bridge is built on the two-node
schema only. The stage language is untyped (attainment needs no premise); the
pair layer's meet is all-or-nothing. Parts C/D count traversal steps, Part E
kernel applications.

## BladeAttain.v (115 theorems) — the orbit bound at proper subgroups (plan item 4, partial)

BladeOptimal attains the orbit bound at `H = S_R` and at the sign character for
`r = 2`. This file attains it at proper subgroups (block licences composed with
the binding's cut: the meet) and at the full sign character for every `r` over
one array. It reuses BladeOptimal's oracle model (`orbit_lower_bound`,
`perturbable`, `ask_all`, `isort`, `scanon`, `svals`, `sgrp`, `cD`).

- *Block kernels.* `Klab R lab`: local, and invariant under every position
  permutation that keeps each position in its declared block.
  `Klab_Klcls`/`Klcls_Klab`: equal to "constant on the sorted label-tagged
  tabulation"; Cantor tags turn a list permutation into a block-preserving
  position permutation. `Kblock bs` lists blocks by size.
  `Kblock_single_is_Ksym`: one block is `Ksym`. Members: `fb_in_Kblock`,
  `fb_not_Ksym` (`(v0+v1)·1000 + v2` is in `Kblock [2;1]` and not in
  `Ksym 3`).
- *The meet.* A shape `s` whose record `j` reads array `arr j` inside kernel
  block `kb j`; `meet_inj` says the pair names the record, i.e. H ∩ Stab's
  classes are contiguous runs. `meet_lower_bound` / `meet_storage_bound`:
  `shapeCard s` is forced. `meet_attained` / `meet_prog_is_object_prog`: the
  object's program attains it and serves every dense cell via `scanon`.
  Separation needs no global decoder (`svals_sep_gen`). Instances:
  `uniform_optimality_block(_nat)` (the kernel cut alone, one array, forced
  count `prod_j C(n+b_j-1, b_j)`; pin: blocks [2;1] at n = 4 give 40);
  `uniform_optimality_subgroup` (the meet, closed with generic data);
  `meet_full_kernel_is_Ksym` + `margs_id_is_shargs` (the data cut alone is
  BladeOptimal's Young nest, same class and same call site);
  `meet_ex_forced` (blocks [3;2] bound to (A,A,B,B,B) at n = 4: 400 forced).
- *The surface rule.* `out_shape_is_meet`, `surface_comm_optimal`: under
  comm, BladeSurface's `out_shape` is the meet, so its card is forced and
  attained when the rle groups have distinct names. `surface_data_bridge` /
  `surface_den_is_call_site`: with Cantor data, `apply_kernel` (hence `den` of
  a well-typed `L <@> f`) is the kernel at exactly that call site.
  `surface_nocomm_optimal`: without comm, the dense shape is forced and
  attained for all local kernels.
- *The boundary (A, B, A).* `aba_beats_surface_rule`, `aba_orbit_count_forced`,
  `aba_gap_strict`, `aba_args_is_call_site`. Under a comm kernel the neighbour
  rule (formalism §8.3/§11.1) stores n³ cells; soundness of that store is
  BladeSurface's. Here, n·C(n+1, 2) questions suffice on every dense cell,
  that count is forced, and it is strictly smaller for n ≥ 2.
- *Signed, general r.* `Kalt r` is anti-invariance under adjacent
  transpositions only. `Kalt_sign_character`: this is the full sign character,
  f(v∘s) = (−1)^inv(s)·f(v) for every position permutation. `alt_vanishes`:
  zero on any tuple with two equal entries. `antisym_lower_bound_r` /
  `antisym_storage_bound_r`: C(n, r) forced, with BladeArrow's strict arrow as
  transversal (`strict_length`). `alt_sort_sign`: insertion sort is a product
  of adjacent swaps, and its swap count is the inversion count
  (`isort_sw_inv`). `antisym_attained_r`: sort, look up, multiply by the
  parity; a repeated index answers 0. The test is the transparent `nodupb`, so
  the read computes (`alt_read_pin`, Vandermonde `vdm_in_Kalt`).
  `Kalt_2_is_Kanti` recovers r = 2.

Scope: not covered are general split classes (e.g. `comm(a, c)` over
(A, B, A)), which need a position-permuted layout; signed kernels over several
arrays; mixed symmetric/antisymmetric blocks; Hermitian conjugation; pipelines
with arity > 2 (prose); and folds (overlapping-fibre sharing is Ensemble
Computation, NP-complete; see BladeOrbitWork).

## BladeInduction.v (101 theorems) — the inductive side: recursive arrays (plan item 5)

An index type is a finite set with a group action **and** a well-founded order
on its leading axis; a `let rec` is the structural induction along that order
(formalism §7.5). Built on the one object (D3), not on `BladeReduceCompiler`'s
fragment.

- **Semantics.** A step `nat -> (Z -> V) -> V` gets the ordinal and the prefix
  read; `lr_buf` is the one buffer, one slice appended per ordinal (TRMC);
  `zread` is a bounds test on the ordinal, zero outside `[0, n)`.
  `letrec_is_wf_fixpoint` (close to definitional: the prefix is the family at
  extent n; its content is coherence of extents); `letrec_fixpoint_self`:
  `rec_build N` solves the self-referential, zero-extended recurrence;
  `letrec_unique` (induction on `lt_wf`): it is the only solution;
  `letrec_prefix_stable`: extents agree on their common prefix.
- **Implicit zero history.** `letrec_respects_zero_history`,
  `letrec_zero_history`: an unconditional `prefix(n - j)` builds what the
  hand-guarded form builds (the AB3 law). Reads are integer-indexed: a nat read
  would truncate `n - 3` to slice 0. (Zero past the declared extent is only
  `nth`'s default; external reads there are BL8006.)
- **Work.** In BladeOptimal's oracle model, `letrec_work`: the sweep asks the
  step N times, ordinals in order, each with its prefix; `letrec_lower_bound` /
  `letrec_work_optimal`: N is forced for every R-local class (causal,
  windowed, Markov, all) and attained. With slices over a shape,
  `letrec_work_shape(_lower_bound, _optimal)`: `N * shapeCard s`, one question
  per (ordinal, orbit), forced for orbit-invariant steps and attained by the
  sweep. The bounds re-prove the adversary argument over
  `prog/run/trace/run_agree` rather than instantiate `orbit_lower_bound`: the
  output at n depends on earlier answers, so they need `lr_buf_agree_below`
  (perturbing ordinal n leaves the prefix below n untouched).
- **Composition with the quotient.** `traj` is defined with the recursion axis
  leading (`mkIx 1 0 N :: s`), slices stored one cell per orbit and read
  through `qread`. The theorems: a rank-1 leading record is never permuted
  (`block_perm_lead`); and for an orbit-invariant step reading its prefix at
  dense tuples, the quotient agrees with the DENSE recurrence (`dbuf`, slices
  on all tuples, no canonicalization) at every dense cell
  (`dense_agrees_with_quotient`). `letrec_commutes_with_quotient` packages it:
  storage `N * shapeCard s`, agreement, storage lookup, the canonicalizing read
  equal to the plain read (`qread_traj_is_plain_read`), and each dense slice
  symmetric on the remaining axes. `orb_inv_needed_pin` shows that without
  invariance the two reads differ. These theorems need `orb_inv` (invariance
  for EVERY prefix reader) and `rd_dense_ext` (over every tuple), which the
  file's own `sym_step` fails (`sym_step_not_orb_inv`,
  `sym_step_not_rd_dense_ext`; audit 2026-10-15, S03). Under the weaker
  `orb_inv_sym` / `rd_dense_ext_sym` (orbit-invariant readers, dense tuples
  only), `dense_prefix_is_orbit_invariant` and
  `letrec_commutes_with_quotient_sym` hold, and
  `sym_step_commutes_with_quotient` proves the agreement for every record,
  every N and every ordinal.
- **`while` guard.** `gbuf_spec` (closed form of the guarded sweep),
  `letrec_guard_total`, `letrec_guard_prefix_agrees` (stops only early),
  `letrec_guard_frozen` (last written slice, zero at stop 0),
  `letrec_guard_budget` (BL8010 iff the guard never fails),
  `letrec_seeded_guard` (with a seed arm the guard is not consulted at 0).
- **Calculus hook.** `rec_arr` is the object an `ERec` would denote;
  `letrec_veval`, `letrec_scalar_is_shape_nil` (scalar = shape `[]` instance).

Scope: values nat / Arr nat; the storage policy (rolling window), nested
`let rec`, AD through recurrences and the `ERec` constructor itself are not
modelled.

## BladeLicence.v (96 theorems) — numerics as a parameter (plan item 6)

The group acts on indices and promises nothing about values; Blade's
bit-changing choices (`omp` on a `reduce`, `BLADE_FP_REASSOC`) are *licences*:
the result need only equal the canonical one up to a declared relation `R`.
This file restates the law class, Fun-Intro's premise, the typing relation and
the quotient theorem relative to `R` (values stay `nat`, decision D2).

- `invariant_under_R`, `Ksym_R`, `Kcls_R`, `kwf_R`: the tower's definitions
  with `R` for `=`; `Ksym_R_iff_Kcls_R` adapts `Ksym_Kcls`; `Ksym_eq_iff`,
  `kwf_eq_iff`.
- `has_ty_R R`: BladeSurface's `has_ty` with `kwf_R R` in the three kernel
  rules. `has_ty_R_eq_iff` (`R = eq` is `has_ty`), `has_ty_has_ty_R`;
  `progress_R`, `preservation_R`, `normalizes_R`, `eval_sound_R` (copied,
  since they never consult `kwf`).
- `typed_app_is_quotient_R` / `typed_appO_is_quotient_R`: for a `has_ty_R R`
  derivation of `L <@> f`, the canonical read is the dense value up to `R` on
  every dense cell, one cell per orbit; `R` need only be reflexive and
  transitive. `typed_app_is_quotient_eq` recovers BladeSurface's theorem from
  `has_ty`. This is DIRECT APPLICATION only: `has_ty_R` types any post-pipe,
  and an arbitrary pipe can break R (`pipe_breaks_R_old`: parity, then
  division by 2; audit 2026-10-15, S01).
- `has_ty_RC`: pipes must be R-congruent (R x y → R (g x) (g y)).
  `typed_is_quotient_RC` gives the quotient theorem over the FULL typed
  grammar; `has_ty_RC_eq_iff` (R = eq is the tower again),
  `pipe_refused_RC`, `congruent_pipe_keeps_R`.
- `licensed_not_exact`: `v0 + 3 v1` declared comm is `kwf` up to parity, not
  `kwf`; `has_ty` refuses it (`not_kwf_untypable`), `has_ty_R Rmod2` types it,
  and the quotient theorem applies to that derivation.
- Reduce: `fold_perm_invariant` (an associative-commutative `op` folds
  identically in every order); `fold_perm_invariant_R`, `kfold_kwf_R`,
  `kfold_quotient_R` (the same up to a congruence: reordering);
  `fold_split_R`, `fold_chunks_R`, `tree_reduce_R`, `reassoc_licence` (with
  `e` an `R`-identity: every bracketing of every reordering, which is the
  omp-on-reduce licence; `fold_split_needs_identity` shows the premise is
  needed).
- Rounding (BladeNumericContract's `fadd`, every p ≥ 2):
  `rounded_fold_not_exact`, `kfold_opr_not_kwf` (arity 3, data (2^p, 1, 1):
  2^p vs 2^p + 2); `rounded_fold2_not_exact` (arity 2 needs
  non-representable data) and `rounded_fold2_exact` (arity 2 on representable
  data is exact); `rounded_licence_coarse`, `assoc_congruence_collapses` (any
  licence, or any `R` under which `fadd` is associative, relates 2^p and
  2^p + 2; with the congruence at p = 2, 4 and 24). So a float licence is
  declared as `kwf_R`, not derived from `op`'s algebra.
- The `R` of the reduce theorems relates one `op` to no second operation: it
  licenses reordering and re-bracketing, never distributing
  (BladeNumericContract's `no_ordering_recovers_the_reduced_value`).

Scope: `R` is a parameter, not an IEEE model; the error bounds are
`proofs/reals/`, not imported; a tolerance `|x - y| <= eps` is not transitive
and is not a licence here. Only attainment is restated up to `R`; the orbit
lower bound is not (it would need `R` to separate perturbed values). Rank-0
kernels, s = 1 groups.

## BladeBoundary.v (51 theorems) — the scope boundary of the orbit bound (plan item 7)

The orbit bound is for KERNEL-GENERIC programs; this file states where it
stops.

- `support_lower_bound` generalizes `orbit_lower_bound` to queries touching up
  to `w` classes: distinct, separately movable targets cost `1/w` query each
  (`perturbable_is_sperturbable`, `orbit_bound_from_support`: the orbit bound
  is `w = 1`).
- Part C is mostly `orbit_lower_bound` re-read: `free_on` is its
  perturbability hypothesis under a name, `Ksym_free` re-exports
  `sym_perturbable`, `free_class_bound` restates it for any law class `K` (the
  count is the S_r orbit count on the canonical-cell task, not K's own tight
  count when K's law is finer), and `canonical_task_attained` attains it for
  any kernel. `indicator_closed` (K closed under adding a class indicator) is
  an intrinsic freeness implying `free_on` (`indicator_closed_free`,
  `Ksym_indicator_closed`).
- The structured counterexample: separable kernels `Ksep` (`f(a,b) = g a + g b`)
  are symmetric (`Ksep_Ksym`) but tie orbits together (`sep_determined`), and
  are free on no class at all (`separable_nowhere_free`, `separable_not_free`,
  `Ksep_not_indicator_closed`). `sep_prog` asks the `n` diagonal tuples and
  answers every dense cell (`sep_prog_correct`, `sep_prog_trace`),
  `n < C(n+1, 2)` (`separable_beats_orbit_bound`), and is necessarily wrong on
  some symmetric kernel (`sep_prog_not_sym_correct`):
  `structured_kernel_beats_bound`.
- Only the QUERY bound (T1) is beaten: a single-cell opaque store still needs
  `C(n+1, 2)` cells for `Ksep` (`sep_storage_bound`, via
  `sep_canonical_separate`). The witness moves work into its reader (halve and
  add two answers), which the oracle model does not count.
- The `Ksep` query count: worst case exactly `n` (`shift_exists` by constraint
  elimination; `sep_positive_lower_bound` at every kernel with `g >= 1`,
  `sep_worst_case_lower_bound`, `sep_query_complexity`); `>= n/2` at every
  kernel (`sep_lower_bound`), with `ceil(n/2)` attained at the zero kernel
  (`sep_lower_bound_attained_at_zero`) — a nat-domain artefact (`g >= 0` is
  itself an inequality law), not a property of separability.
- `orbit_bound_boundary` packages it; `genericity_two_sides` (at n = 3) pairs
  the data side (`nongeneric_data_beats_bound`: distinct classes fail) with
  the kernel side (perturbability fails). Pins: `C 4 2 = 6` vs 3,
  `C 6 2 = 15` vs 5.

Scope: one-directional — the converse is open (an inequality law between
orbits blocks perturbation without saving worst-case queries: prose). The FFT
is an analogy only; the plan's FFT witness is replaced by the separable kernel.
r = 2, nat values.

## BladeSummary.v (41 theorems) — exact recurrence reduction: the certificate calculus

Mechanizes the abstract half of the research draft
[exact-recurrence-reduction-proofs.md](research/exact-recurrence-reduction-proofs.md)
(P1–P3, and a discrete form of its lower-bound half). **It backs no shipped
compiler feature**: there is no source fragment, no recognition and no code
generation behind it. Abstract types, induction and equality only.

A recurrence `F : U → X → X` observed through `h` may be replaced by `G` on
summaries `q : X → Z` observed through `hb` when `q (F u x) = G u (q x)` and
`h x = hb (q x)` hold on an invariant set (`certificate`; preservation of the
set is a field, initialization is the caller's premise).

- `summary_run_sound`, `summary_obs_sound`, `summary_trace_sound` — P1: every
  finite execution, and every *prefix* observation of it.
- `summary_feedback_sound` — P1a: a policy choosing inputs from the observation
  history chooses the same inputs in both systems.
- `summary_guard_sound` — P1b: a guard that factors through the summary stops
  both at the same step, and budget exhaustion (BL8010) is preserved *as an
  outcome*. `summary_guard_certificate` certifies the frozen-after-stop
  recurrence, so P1 covers the whole budget-extent array; `guarded_state` ties
  the two readings together.
- `summary_compose_sound`, `summary_product_sound` — P3. The product rule
  carries the other component's state in its premise, and
  `isolated_certificates_do_not_compose` is the closed witness that it must:
  each subsystem reduces alone, the coupling reads a discarded coordinate, and
  no update of the paired summary exists.
- `summary_dag_sound`, `summary_tree_sound`, `lag_window_sound` — P2: a finite
  dependency graph in topological order with per-node summaries and shared
  predecessors; terms; and a k-lag recurrence under Blade's zero-history
  convention as a first-order recurrence on its window
  (`lag_window_is_first_order`).

**The lower-bound half without analysis.** The draft's P6 bounds the
*dimension* of a C¹ summary and needs real analysis. What needs none:
`summary_refines_future` (a certified summary never identifies two states some
input word tells apart), `future_equiv_coarsest` (future-equivalence is the
coarsest congruence preserving the observation — the Myhill–Nerode quotient,
relationally), and therefore `collision_refutes_update` /
`collision_refutes_reduction`: **one pair of states** with equal summaries and
different next summaries refutes a candidate summary against *every* update
function, not just polynomial or continuous ones. That is the checkable
"certified obstruction". `summary_card_lower_bound` counts values
(`countdown_needs_n_values` is the non-vacuity instance);
`identity_observation_forces_injective` and `guard_collision_refutes` are the
draft's two demand-side adversarial cases. A cardinality bound says nothing
about dimension — ℤᴺ injects into ℤ — which is why the draft's smoothness
hypotheses are load-bearing.

## BladeMomentClosure.v (86 theorems) — exact recurrence reduction: the algebra

Same draft, P4, P5, P8–P10 and polynomial forms of P4a, P6, P7, over an
**abstract commutative ring** (`ring_theory`; ℤ, ℚ, the dual numbers — never
floating point, which is not a ring).

- `affine_moment_tower_closed` — P4, the draft's (M1), at every order and every
  extent: under `xᵢ' = a xᵢ + b`, `p_k' = Σⱼ C(k,j) aʲ b^(k−j) pⱼ`, with the
  tower's own Pascal `C` (`bcl_binomial`). `affine_sum_closed` and
  `affine_square_sum_closed` are (M2).
- `moment_certificate`, `moment_reduction_sound` — with `a` and `b` *arbitrary*
  functions of the input and the current summary, shared across the array,
  this is a BladeSummary certificate: r coordinates carry every prefix
  observation of every finite execution at every extent.
- `tree_summary_sound` — P2's non-time example, (n, S, Q) over singleton /
  concatenate / affine trees.
- `generators_closed_algebra_closed`, `poly_certificate_sound` — P9. The worked
  `poly_certificate_affine_N3` is checked by `ring` at a *fixed* extent: a P9
  certificate is a finite identity and does not scale in N; the tower theorem
  is the uniform statement.

**Derivatives without analysis.** The dual numbers R[ε]/(ε²) are a commutative
ring (`dual_ring_theory`), so every theorem above holds *at* them and the
tangent part of an identity is its derivative. `moment_tangent_sound` is P5a
for the fragment over every finite execution — push (x, v) through the
particle system then summarize = summarize then push through the reduced
system — and no chain rule was invoked: it is the closure theorem at another
ring. `dual_psum` is Dq; `pullback_SQ` is the draft's cotangent formula
∂loss/∂xᵢ = λ_S + 2 xᵢ λ_Q.

**Refusals.** By one collision, against every update function:
`same_SQ_674_1250` pins the draft's counterexample; `square_not_closed_SQ`
shows squaring admits no update of (S, Q) at any extent N ≥ 3 even on strictly
positive arrays ((1,5,6)/(2,3,7), padded); and `square_SQ_threshold` makes the
threshold **exact** — the relation holds iff N ≤ 2 (`two_point_newton`). The
draft's P8 threshold N ≥ d·r = 4 is sufficient, not sharp.
`power_not_closed_S`: x ↦ xᵈ never closes the sum alone, every d ≥ 2, N ≥ 2.
By formal Jacobian rank, against every *polynomial* encoding — the chain rule
is evaluation in the dual numbers and the rank bound is `ring` at a fixed
size: `poly_rank_bound_1_2`, `poly_rank_bound_2_3`, hence
`SQ_needs_two_coordinates` (P4a at r = 2) and
`squaring_three_steps_need_three_coordinates` (P7 at N = H = 3; minor 96 at
(1,2,3), `squaring_minor_N3`; `squaring_observes_dyadic_moments` says the
observed sums are p₁, p₂, p₄).

**P10.** `p10_next_observable_is_new`: x·y^(T+1) is not a polynomial in
x, xy, …, x·y^T, so the ascending chain of observable subalgebras never
stabilizes and "adjoin future observations until done" does not terminate —
although (x, y) is an exact two-coordinate state, which cannot be shrunk
(`p10_needs_two_coordinates`, `p10_no_identification`).

**Units** (the draft's §11.3): `affine_update_unit_covariant` — rescale x and
b by c, keep a dimensionless, and p_k rescales by cᵏ. The summary is a graded
product (S carries the unit, Q its square) and the reduced update respects
the grading.

Honest scope (stated in-file): nothing against C¹ encodings (Rolle, inverse
function theorem — Coq's Reals are axiomatic and this tower is axiom-free);
the rank bound here only at (1,2) and (2,3), where `ring` expands the minor —
BladeRankBound takes it to every size, on `D_factor` and `lift_basis`; P5
for forward mode as dual numbers — the rules `Grad*.fs` applies, reverse mode
included, are modelled and tied to it in BladeReductionAD. A
polynomial identity is read as valid in the dual numbers over the base ring —
what a symbolic certificate provides; an equation between *functions* on ℤ is
a weaker hypothesis.

## BladeRankBound.v (49 theorems) — exact recurrence reduction: the lower bounds at every size

Same draft: P6, P4a, P7, P8's refusal and P10 in their **polynomial** forms —
with no determinants and no analysis. The draft's §15 suggested "do
determinants once"; one weaker fact turned out to be enough.

- `homogeneous_has_solution`: an integer homogeneous system with more unknowns
  than equations has a **nonzero** solution. Fraction-free elimination by
  induction on the number of equations; `elimination_identity` is the whole
  step.
- `poly_rank_bound_rows`, `poly_rank_bound_cols`: if s observations factor, as
  identities over the dual numbers, through m < s polynomial summary
  coordinates, then at **every** point one nonzero integer combination of their
  differentials vanishes along every direction — and along any s directions one
  nonzero combination of the *directions* is invisible to every observation.
- `poly_rank_certificate`: the soundness theorem of a certificate checker. A
  point and s directions whose s×s integer matrix of differentials has trivial
  kernel refute every m < s; for a concrete matrix that premise is `lia`.
- `many_roots` (`many_roots_fn`): a polynomial with more distinct roots than
  coefficients is zero. Horner division whose quotient coefficients are the
  tail evaluations, so "quotient zero ⇒ dividend zero" is one line. The only
  place ℤ being an integral domain is used.

From those two:

- `moment_summary_needs_r_coordinates` — **P4a at every r**: the first r power
  sums of N ≥ r particles need r polynomial coordinates (row form at xᵢ = i).
- `squaring_needs_min_N_H_coordinates` — **P7 at every N and horizon**: the
  sums observed over H steps of squaring need min(N, H) polynomial coordinates,
  so no bounded polynomial summary serves every horizon as N grows. The column
  form at the point xᵢ = 2ⁱ along directions 2ˡ·e_l turns the draft's
  *generalized* Vandermonde matrix into an ordinary one in the nodes 2^(2ᵗ):
  neither Descartes' rule nor Rolle is needed.
- `power_not_poly_closed` — **P8's refusal for x ↦ xᵈ at every d ≥ 2 and
  r ≥ 1**, with the draft's own threshold N ≥ d·r, against polynomial G (its
  "algebraic mechanization alternative"): p_r of the image would make r + 1
  observations factor through r coordinates, and N ≥ d·r distinct nodes leave
  the resulting polynomial no room. `power_not_closed_SQ` is the r = 2 case
  against **every** G, uniformly in d, by one collision (7ᵏ outgrows
  1 + 5ᵏ + 6ᵏ from k = 3 on).
- `p10_not_finitely_generated` — **P10 to the end**: given any finite list of
  elements of the least observable algebra, some observable x·y^M is not a
  polynomial in them.

Honest scope (stated in-file): polynomial summaries with polynomial
reconstructions only — narrower than the draft's C¹ statements and not a
substitute for them; a continuous non-polynomial encoding is untouched. P7 is
proved here at *one* point, which is all a lower bound needs; the draft's
stronger claim — rank min(N, H) at every point with distinct positive
coordinates — is BladeDescartes. P8 is proved for pure powers, not for the
general fragment with coefficient polynomials a_j(q_r).

## BladeDescartes.v (28 theorems) — exact recurrence reduction: the P7 thread

Same draft: P7a, P7b, and the rank statements of P7 and P4a **at every point**.
The draft proves P7a (a polynomial with s monomials has at most s − 1 positive
roots) by Rolle's theorem. **Rolle's theorem is false over ℚ** — the root of
the derivative need not be rational — so that proof has no axiom-free reading.
The statement does, by Descartes' rule of signs, whose algebraic proof never
asks for a root of anything but the polynomial itself.

- `mulxc_adds_a_variation`: multiplying by (X − a), a > 0, adds at least one
  sign variation to the coefficient sequence. `descartes_step` scans W and
  (X − a)·W together from the low end with a two-state invariant: AHEAD (last
  signs agree, one variation up) or TIED (last signs opposite, and the pending
  coefficient prev − a·w will settle it).
- `descartes_bound`: **Descartes** — a nonzero integer polynomial has at most V
  distinct positive roots. The factor theorem is BladeRankBound's Horner
  division, exact over ℤ (`mulxc_quot`).
- `sparse_roots` — **P7a**: at most s nonzero monomials and s distinct positive
  roots force zero.
- `generalized_vandermonde` — **P7b**, row form: distinct positive nodes and
  *distinct* exponents (no order needed) give a trivial kernel.
  `generalized_vandermonde_transpose` is the column form, by
  `square_kernel_transpose`: a square integer matrix with a nonzero left kernel
  vector has a nonzero right one — "row rank = column rank" for square
  matrices, without determinants (drop a row the left vector weights, solve
  s − 1 equations in s unknowns with `homogeneous_has_solution`, and the
  dropped row follows).
- `squaring_jacobian_full_rank` — **P7 at every point**: wherever the
  coordinates are pairwise distinct and positive, the differentials of the sums
  observed over min(N, H) steps of squaring are linearly independent.
  `moment_jacobian_full_rank` is the same for p₁ … p_r at every point with
  pairwise distinct coordinates (ordinary Vandermonde; positivity not needed).

Honest scope (stated in-file): points are **integer** points. A rational point
reduces to an integer one by clearing denominators, since every p_k is
homogeneous; a real point does not, and nothing here speaks to it. Independence
is over ℤ, equivalently ℚ. What a rank at a point *forbids* is still
BladeRankBound's theorem — against polynomial encodings only.

## BladeRankDomain.v (14 theorems) — exact recurrence reduction: the algebra of P6, anywhere

The draft's P6 (a C¹ summary needs at least rank-many coordinates) is two
facts: the chain rule DO = DR·Dq, and "a matrix that factors through m columns
has rank at most m". The first is analysis and lives outside the tower. The
second is algebra, and so is the step that carries a rank computed exactly
over ℤ into a field one cannot compute in. Both are here, axiom-free, so that
the file which needs axioms contains nothing but analysis.

- `integer_right_inverse_col` (over ℤ): a square integer matrix with trivial
  left kernel has a right inverse up to a nonzero scalar, one column at a time
  — `homogeneous_has_solution` finds the column,
  `square_kernel_transpose` shows the scalar is nonzero.
- `homogeneous_has_solution_dom`: BladeRankBound's fraction-free elimination
  over **any** integral domain with decidable equality.
- `factor_rows_dependent` — **the algebra of P6**: if A = C·B with fewer
  columns in C than rows, the rows of A are linearly dependent.
- `left_kernel_transfer`: along any ring map ℤ → K that kills no nonzero
  integer, a square integer matrix with trivial left kernel over ℤ has trivial
  left kernel over K.

Honest scope (stated in-file): K is a commutative ring with 1 ≠ 0, no zero
divisors and *decidable equality*, all as hypotheses; nothing about order or
completeness. Over ℝ, decidable equality is itself a consequence of the
axioms — that is where they enter, not here.

## BladeProuhet.v (37 theorems) — exact recurrence reduction: P8's refusal against every update function

The draft refuses closure of q_r = (p₁ … p_r) under a degree-d update by moving
p_(dr) while p₁ … p_(dr−1) stand still — local coordinates, hence the inverse
function theorem. The algebra in that argument needs no analysis at all, only a
**pair of arrays** that agree on p₀ … p_(dr−1) and disagree on p_(dr). Such
pairs have been known since Prouhet (1851).

- `aff_low`, `aff_top`: how power-sum differences move under an affine map —
  BladeMomentClosure's P4 theorem read twice.
- `pte`, `pte_spec` — **Prouhet's construction**: from a pair agreeing on
  p₀ … p_(m−1) and disagreeing on p_m, the pair (A ++ (B+1), B ++ (A+1)) agrees
  one power further, and its first disagreement is −(m+1) times the old one.
  Sizes 2^(m−1) (`pte_length`); `prouhet_pair_3` pins ([0;2;2;2], [1;1;1;3]).
- `psum_Fgen`, `plpow_top`: the draft's expansion (P2). For the update
  xᵢ' = Σⱼ aⱼ(q_r x)·xᵢʲ, p_r of the image is Σ_k c_k p_k with c the
  coefficients of φʳ — functions of the summary alone — and top one a_dʳ.
- `closure_refused_at_collision` — **the refusal**: any such pair of arrays,
  where a_d does not vanish, refutes *every* update function G, continuous or
  not. The aⱼ are **arbitrary** functions of the summary, not just polynomials.
- `power_refused_at_collision`: the pure-power refusal from *any* collision
  pair — what a pair found by other means (`proofs/reals/`) plugs into.
- `closure_refused_prouhet`: with Prouhet's pair — every d ≥ 2, r ≥ 1, every
  extent N ≥ 2^(dr−1). `power_never_closes` (and `power_never_closes_Z`): in
  particular x ↦ xᵈ, unconditionally. (BladeProuhetShift lowers the
  pure-power extent to 2^r.)
- `closure_refused_orthogonal_array`: the draft's **own** threshold N = d·r,
  wherever the ring has an array whose first dr − 1 power sums vanish and whose
  (dr)-th does not — the (dr)-th roots of unity, where they exist
  (`square_vs_sum_at_threshold`: (1, −1) over ℤ).

Everything is over an abstract integral domain of characteristic zero
(hypotheses), so it reads at ℤ, ℚ, ℝ and ℂ alike.

Honest scope (stated in-file): **the threshold is 2^(dr−1), not the draft's
d·r, and that gap is not slack in the proof.** A pair of extent m agreeing on
p₁ … p_(m−1) is an *ideal* Prouhet–Tarry–Escott solution; over ℤ these are
known only for small m (m ≤ 10 and m = 12, to our knowledge) and their
existence in general is an open problem. Over ℝ they exist for every m, but
only by analysis (the draft's route, or the roots of T_m(X) = c); over ℂ they
are the roots of unity. So the draft's threshold is a statement about the
reals and is **not** reached; what is reached is the same refusal at a larger
extent, with no axioms. For coefficient *polynomials* whose leading one
vanishes at the Prouhet point another collision point is needed.

What this file left open is closed in `BladeProuhetShift.v` (below): for the
pure power a pair need only agree on p₀ … p_r and differ at *some* p_m with
r < m ≤ d·r, which brings the extent down from 2^(dr−1) to **2^r**, and to
**3·2^(r−2)** from the seed (0,3,3)/(1,1,4); and a polynomial leading
coefficient that is nonzero at some r-vector gets its collision point
(`closure_refused_polynomial`), at the price of deg(a_d)·(2^r − 1) extra
entries. The sharp thresholds — the draft's d·r, and below it N ≥ r + 2 over ℝ
(Waring) and N = r + 1 over ℂ (Ritt) — remain the reals' business.

## BladeNewton.v (22 theorems) — exact recurrence reduction: Newton's identities, and what they buy

The axiom-free half of the draft's P8 threshold N ≥ d·r. BladeProuhet builds
collision pairs over ℤ at extent 2^(m−1); at extent m they are *ideal*
solutions and none is known over ℤ in general. Over ℝ they come from a
polynomial, not from a list: take ∏(1 − xᵢY), bump **one** coefficient, and
ask for the roots again. Everything in that sentence except "ask for the roots
again" is algebra, and is here.

- `elist`, `cf`: E(Y) = ∏ᵢ (1 − xᵢY) as a coefficient list; cⱼ its
  coefficients, the signed elementary symmetric functions.
- `newton_identities` — **Newton**: Σ_(j<k) cⱼ p_(k−j) + k·c_k = 0 for every
  k, over **any commutative ring**. By induction on the roots: adding a root x
  sends N(k) to N(k) − x·N(k−1) (`newton_cons`), the cross terms telescoping
  to nothing.
- `newton_difference`: so the top m coefficients determine p₁ … p_(m−1), and
  p_m moves with c_m at rate −m.
- `many_roots_dom`, `coefficients_from_values`: a polynomial with more distinct
  roots than coefficients is zero, over any integral domain — hence two
  coefficient lists that agree as *functions* at enough points agree
  coefficient by coefficient.
- `ideal_pair_from_bump`: if E(xs) with its m-th coefficient bumped by t ≠ 0 is
  again some E(ys), then xs and ys agree on p₀ … p_(m−1) and differ on p_m
  (by m·t): an ideal pair of their common extent.

Honest scope (stated in-file): nothing here *produces* the second array — that
a bumped polynomial still splits into real linear factors is analysis.
Characteristic zero and "no zero divisors" are hypotheses used only from the
root-counting part on; Newton's identities themselves need neither.

## BladeProuhetShift.v (52 theorems) — exact recurrence reduction: P8's refusal from extent 2^r, and for every polynomial leading coefficient

BladeProuhet's refusal needs a pair whose *first* disagreement is at p_(dr).
Two observations shrink that extent and close the file's scope gap.

- `power_refused_translated` — **the translation lemma**: for the pure power
  x ↦ xᵈ, a pair that agrees on p₀ … p_r and disagrees at *some* p_m with
  r < m ≤ d·r already refutes every update function G. Move both arrays by
  y ↦ t·y + 1: they still agree on p₀ … p_r (the affine tower), and Δp_(dr)
  of the images is the polynomial Σ_l C(dr, l)·Δp_l·tˡ (`dp_aff_poly`), whose
  tᵐ coefficient C(dr, m)·Δp_m is nonzero. A closing G would make it vanish at
  t = 0, 1, …, dr, so it would be zero (`line_coefs_zero`, on BladeNewton's
  `many_roots_dom`).
- `power_never_closes_2r` (and `power_never_closes_2r_Z`): with Prouhet's
  stage r + 1 — every d ≥ 2, r ≥ 1, every extent **N ≥ 2^r** (was 2^(dr−1)).
  `squaring_refused_at_3`: the ideal pair (0,3,3)/(1,1,4) refutes closure of
  (p₁, p₂) under squaring at N = 3 (`ideal_pair_squared` pins the summaries:
  (6, 18) for both inputs, (18, 162) against (18, 258) after squaring).
- `prouhet_step`, `pgen`, `pgen_spec`: Prouhet's step from *any* seed, with the
  exact new disagreement −(m+1)·Δp_m. `seed_closed_form`: from (0,3,3)/(1,1,4)
  (Δp₃ = −12) the first disagreement at p_m is 2(−1)ᵐ·m! at extent 3·2^(m−3)
  (`seeded_pair_4` pins the next stage: Δp₄ = 819 − 771 = 48).
  `power_never_closes_seeded` (and `_Z`): every d ≥ 2, r ≥ 2, every extent
  **N ≥ 3·2^(r−2)** — a quarter below 2^r.
- `lead_forced_zero`: `closure_refused_at_collision` read positively — a
  closing G forces a_d to vanish at every collision.
- `blocks_vanishing_is_identical` — **the descent**: append w_k copies of
  Prouhet's stage-(k+1) array A_k and D − w_k copies of its partner B_k, for
  k < r. The summary moves by Σ_k w_k·Δ(A_k, B_k), and those directions are
  *triangular*: Δ(A_k, B_k) vanishes below coordinate k and not at it. So a
  polynomial of degree ≤ D that vanishes at every grid choice w ∈ {0…D}^r
  vanishes at every r-vector, one coordinate freed at a time from the top
  (`descent_step`, `pev_line_vanish`). No determinant, and no algebraic
  independence of p₁ … p_r.
- `closure_refused_polynomial` — **P8's refusal as the draft states it,
  axiom-free**: the leading coefficient a_d a polynomial in the summary (a
  `pexp` read on its first r variables) that is nonzero at *some* r-vector, the
  lower aⱼ arbitrary functions, every extent
  N ≥ 2^(dr−1) + deg(a_d)·(2^r − 1), against every G.

Over an abstract integral domain of characteristic zero, like BladeProuhet.

Honest scope (stated in-file): the sharp real and complex thresholds —
N ≥ r + 2 by Waring's formula, N = r + 1 by Ritt's theorem — need the
fundamental theorem of algebra or Ritt and are not here. The translation lemma
is for the pure power only: when the coefficients are functions of the
summary, the translated output is no longer a polynomial in t. The
free-entries route pays deg(a_d)·(2^r − 1) extra entries, not the r that
algebraic independence of the power sums would give, and in exchange needs no
independence argument.

## BladeNumericContract.v (39 theorems) — exact recurrence reduction: the numerical contract

Section 11.2 of the draft asks which arithmetic the certificate theorems may be
run in, and names three possible contracts. The certificates are ring
identities, so the question is which machine arithmetics are rings.

- **Wrapping integers are an exact fragment.** `Zm` is the integers modulo m as
  a ring with *Leibniz* equality — a sigma type over a boolean equation, whose
  proofs are unique (`Eqdep_dec`; no axiom). `Zm_ring_theory`, and then
  `wrapped_moment_reduction_sound` is BladeMomentClosure's theorem instantiated:
  reduction is exact in wrapping arithmetic, every overflow included.
- `wrapped_reduced_run_is_residue`, `wrapped_observation_exact`,
  `int64_observation_exact` — the wrapped *reduced* run carries the residues of
  the true *original* moments; whenever a true moment is representable, the
  signed reading returns it exactly, whatever overflowed on the way (m = 2⁶⁴).
- `rational_moment_reduction_sound` — the same over canonical rationals (`Qc`).
- **Rounded arithmetic is not a fragment, at any precision.** `rne` is round to
  nearest, ties to even, precision p, unbounded exponent, on integer data.
  `rounding_breaks_update_original_inexact`: a = b = 1, x = (2ᵖ, −2ᵖ), exact
  answer 2 — the particle-wise fold returns 1, the reduced form 2.
  `rounding_breaks_update_reduced_inexact`: x = (−2ᵖ, −1) — now the original is
  exact and the reduced form is off by one. Both for every p ≥ 2, so neither
  program is the accurate one.
- `no_ordering_recovers_the_reduced_value` — three particles, p ∈ {24, 53, 64,
  113}: every ordering and parenthesization of the original fold gives one
  value, every ordering of the reduced form another. **A reassociation license
  (`where omp`, `BLADE_FP_REASSOC`) is not a distributivity license.**

Finding: `Build.fs` does not pass `-fwrapv`, so signed `int64_t` overflow is
undefined behaviour in the emitted C++. The wrapping theorems describe the
emitted code only where nothing overflows, or under a wrapping build. The
real binary64 check and the rounding-error bound are outside the tower (below).

## BladeReduceCompiler.v (63 theorems) — exact recurrence reduction: a source fragment and its compiler theorem

The draft's section 12 names the research target: a typed fragment whose local
rules construct the summary and the reduced update *uniformly in the extent*,
with a simulation theorem, and a classification that source recognition,
certificate construction and execution all implement. This file does that for
the fragment the draft proposes first — P8's.

- **Source.** `prog`: r moments read, m run-constant parameters, a coefficient
  list a₀ … a_d and an observation, all integer-coefficient polynomial
  expressions (`pexp Z`) in the moments and parameters. `sstep` runs it on all N
  particles, in any commutative ring (`phi`, `zmap`, `zev` read integer syntax
  there). The extent is not part of the source program.
- **The zero test.** `nonroot e n` searches the integer grid {0..deg e}ⁿ.
  `nonroot_some_K`: a found point is a nonroot in K. `nonroot_none`: no point
  found means e is zero *as a function on K*, for any integral domain of
  characteristic zero — `grid_complete`, one coordinate at a time through
  `uni_rep` and BladeNewton's `many_roots_dom`. Sound and complete, so
  identically-zero top coefficients are stripped (`strip`) rather than refused.
- **The classifier.** `classify` returns `Reduced a₁ a₀`, `Refused d A z` or
  `Unknown`; `classify_total` — on this fragment `Unknown` never occurs;
  `classify_refused` — a refusal carries the effective degree d ≥ 2, the leading
  coefficient A = a_d, and an integer point z with A(z) ≠ 0.
- **The generated program.** `Gk` / `rprog_of` / `compile`: r expressions
  p_k′ = Σⱼ C(k,j) a₁ʲ a₀^(k−j) p_j over the summary, the parameters and one
  variable for the extent — built once, not per N.
- **The compiler theorem.** `compile_certificate`, `compile_sound_gen`,
  `compile_sound`: for every extent, parameter vector and input word, every
  prefix observation of the source execution equals that of the generated
  program on r numbers. Unconditional over characteristic-zero domains;
  `compile_sound_unstripped` over *any* commutative ring when nothing was
  stripped (wrapping integers, dual numbers).
- **The checked entry.** Every theorem above assumes `wf P = true`, which
  `compile` does not check: an escaped variable compiles to the wrong
  summary (`compile_does_not_validate_wf`; audit 2026-10-15, R01).
  `compile_checked` tests `wf` first; `compile_checked_sound` and
  `compile_checked_certificate` have no `wf` premise, and it refuses the
  escaped-variable and rank-zero programs.
- `sstep_truncated` — a refused program *is* the degree-d update the negative
  theorems speak about. `compiler_runs`, `compiled_affine_runs` — the kernel
  runs the classifier on four programs (one with an identically-zero quadratic
  term, one the draft's "special parameter value" case) and the generated
  program on a concrete state.

Honest scope (stated in-file): a *model* fragment in Coq, not Blade's surface
syntax or F# compiler — nothing in `src/` implements or is checked against it.
The zero test is exponential in r + m. What a refusal means is proved over ℝ,
outside the tower (below): the FIXED summary, the first r power sums, does not
close at the witness parameters from a sufficient extent on. It does not mean
that no reduced program exists. The classifier ignores the observer, so a
squaring program with a constant observer is refused even though a one-state
reduction exists (audit 2026-10-15, R02). Special parameter values, smaller
extents, restricted invariant sets and other summaries are likewise outside
the refusal.

## BladeReductionAD.v (24 theorems) — exact recurrence reduction: the AD contract

P5 is proved in BladeMomentClosure for forward mode *as dual numbers*. Blade's
AD is a source transformation: `synthesizeJvp` emits a tangent statement beside
every primal one (`tangentOfExpr`); `synthesizeRev` sweeps the normalized
statements in reverse accumulating cotangents (`adjointOf`); a `let rec`
recurrence is replayed backwards over its stored trajectory; the `while`-guard
arm is refused (BL5500). This file models those rules on polynomial expressions.

- `tan_is_dual` — the forward rules (sum; d(l·r) = dl·r + l·dr) compute exactly
  the dual-number tangent.
- `adj_pairing` — the reverse rules (add: both sides get the cotangent; mul:
  c·r to the left, c·l to the right; accumulate at a variable) are the
  **transpose** of the forward rules: ⟨adj e c g, v⟩ = ⟨g, v⟩ + c·tan e v.
- `sweep_pairing` — for a straight-line block, the reverse sweep reading the
  forward values left behind is the transpose of the tangent block.
- `carry_pairing` — for a recurrence, the descending sweep over the *stored*
  trajectory is the transpose of the T-step tangent run: the executed
  algorithm, as section 11.4 requires, never an implicit fixed point.
- `reduction_commutes_forward`, `reduction_commutes_reverse` — a certificate
  q∘F = G∘q read in the dual numbers yields the chain-rule instance with no
  chain rule. Hence jvp on the reduced program carries the pushed tangent at
  every horizon; and sweeping the reduced program then pulling back through q
  at the *initial* state equals pulling back at the *final* state then sweeping
  the original — parameters and initial state included, as variables.
- `compile_sound_dual` — the compiler theorem in the dual numbers, every N.
  `ex_affine_reverse_commutes` — both modes on a concrete program with the
  actual output of `compile` as the reduced side.

Honest scope (stated in-file): a model written from a reading of `Grad*.fs`,
over + and × only — no intrinsics, units or packed reconstruction; a map over N
particles is N expression instances, not an `NFor`. The link to the F# is by
inspection, like the rest of this document.

## BladeShiftedMoments.v (21 theorems) — exact recurrence reduction: the coordinates floating point needs

The reduction to raw power sums is exact over any ring and useless in floating
point: raw moments cancel catastrophically — at 10⁸, 10⁸+1, 10⁸+2 in binary64
the raw variance formula returns 2 instead of 2/3
(`floats/BladeBinary64Witness.v`, Part D). Shifted sums
Mₖ(c) = Σ(xᵢ − c)ᵏ about a tracked shift c carry the same information and make
the update a pure rescaling.

- `smom_aff` — move the shift like a particle, c′ = a·c + b, and
  Mₖ′ = aᵏ·Mₖ: no b, no c, no N, nothing to cancel. Over any commutative ring;
  no division, so no field is needed.
- `raw_from_shifted`, `shifted_from_raw` — the binomial expansions both ways:
  the same information as the raw sums, so every refusal and lower bound of
  the raw summary applies unchanged.
- `central_preserved`, `mean_tracked` — started at the mean (M₁ = 0), the shift
  stays the mean: central moments are an invariant of the reduced run.
- `variance_numerator_shift_free`, `variance_numerator_aff`, `rho_invariant` —
  N·M₂ − M₁² equals N·Q − S² for every shift and scales by a² under an affine
  step, as M₁² does: ρ = M₁²/(N·M₂) is invariant. The variance is therefore
  M₂/N − (M₁/N)² with a subtraction that stays as small as the seed made it.
- `shifted_certificate`, `shifted_reduction_sound` — the certificate for the
  *original* moment-fragment program of BladeMomentClosure, unchanged
  (coefficients read the raw power sums), with the shift as a ghost beside it.
  Any starting shift works — a rounded mean included: the rounding of c affects
  conditioning, never exactness.
- `shifted_run_is_shifted_sums` — the reduced state after a word is the shifted
  sums of the true array about the propagated shift: the quantity the rounding
  analysis below compares against.

## Outside the tower: `proofs/reals/BladeSmoothRank.v` (24 theorems, conditional)

**Not counted above. Not in `_CoqProject`. Not covered by "no axioms".** It
uses Coq's standard-library real numbers; `Print Assumptions` on its theorems
reports exactly two axioms:

    ClassicalDedekindReals.sig_forall_dec
    FunctionalExtensionality.functional_extensionality_dep

`coqc` and `coqchk` clean. Build:
`cd proofs/reals && coqc -Q .. Blade -Q . BladeReals BladeSmoothRank.v`.

It closes the draft's P6, and P4a and P7 **against differentiable summaries**:

- `diff_at` is differentiability *at a point*, in two-point form, in the ℓ¹
  norm on the first n coordinates. That is weaker than C¹ on a neighbourhood,
  so each theorem below is **stronger** than its C¹ reading.
  `diff_at_unique`: the gradient is unique.
- `chain_rule`: the chain rule at a point, by the ε–δ argument for Fréchet
  derivatives. No mean value theorem is used anywhere in the file.
- `smooth_rank_bound` — **P6**: if s observations equal reconstructions of
  m < s summary coordinates *near* x, summary and reconstructions
  differentiable at the point, then the gradients of the observations at x are
  linearly dependent. Chain rule, uniqueness, and
  `BladeRankDomain.factor_rows_dependent`.
- `Ppow_diff`: power sums of real arrays are differentiable everywhere with
  gradient k·xᵢ^(k−1); the one import from one-variable calculus is the
  derivative of t ↦ tⁿ.
- `moment_summary_needs_r_smooth_coordinates` — **P4a over ℝ**, and
  `squaring_needs_min_N_H_smooth_coordinates` — **P7 over ℝ**: the sums
  observed over H steps of squaring N reals are not carried by fewer than
  min(N, H) summary coordinates, for *any* summary and reconstructions
  differentiable at the point. No bounded differentiable summary serves every
  horizon as N grows.

The ranks are not recomputed over ℝ: BladeDescartes and BladeRankBound
establish them exactly over ℤ, and `left_kernel_transfer` carries them along
`IZR`. `power_never_closes_R` is BladeProuhet's axiom-free P8 theorem read at
K = ℝ — no analysis, and here only because ℝ itself is axiomatic. Not here:
the draft's P8 at *its* threshold N ≥ d·r, which needs real arrays agreeing
on p₁ … p_(dr−1) at extent d·r (analysis); anything about floating point.

## Outside the tower: `proofs/reals/BladeRealCollision.v` (30 theorems, conditional)

**Not counted above. Not in `_CoqProject`. Not covered by "no axioms".** Three
axioms — the two named above plus `ClassicalDedekindReals.sig_not_dec`,
because the intermediate value theorem is used. `coqc` and `coqchk` clean.

It closes the draft's **P8, negative half, at its own threshold N ≥ d·r**. The
draft moves p_(dr) while p₁ … p_(dr−1) stand still by the inverse function
theorem in several variables, which Coq's standard library does not have.
**One-variable calculus is enough**:

- `EN_sign_change`: E(Y) = ∏(1 − Y/rtᵢ), for any increasing positive roots,
  changes sign between consecutive midpoints.
- `perturbed_roots`: bump the coefficient of Yᵐ by a small t; every one of
  those signs survives (`sign_stable`), so the intermediate value theorem
  returns N real roots, one in each interval.
- `ideal_partner`: the bumped polynomial has N distinct real roots and the
  value 1 at 0, so it *is* ∏(1 − Y/ρⱼ)
  (`BladeNewton.coefficients_from_values`), and Newton's identities read off
  the power sums of the reciprocals. **Every array of distinct positive reals
  has a partner of the same extent agreeing on p₀ … p_(m−1) and differing on
  p_m, for every m ≤ N.**
- `closure_refused_R_at_point` — P8 as §9 argues it: at *any* such array where
  the leading coefficient a_d is nonzero, no function G of the first r power
  sums, continuous or not, closes the update, for N ≥ d·r. The aⱼ are
  arbitrary functions of the summary.
  `power_never_closes_R_at_threshold`: x ↦ xᵈ, unconditionally.
  `closure_refused_R_at_threshold`: any a_d that vanishes nowhere.

The draft's *existence* claim for coefficient polynomials — that "a_d not
identically zero" yields such an array — is the next file.

## Outside the tower: `proofs/reals/BladeRealDensity.v` (32 theorems, conditional)

**Not counted above. Not in `_CoqProject`. Not covered by "no axioms".** The
same three axioms. `coqc` and `coqchk` clean.

It proves the last sentence of the draft's P8 — *"Choose such a point with
a_d(q_r(x)) ≠ 0. It exists"* — and with it **P8's negative half as the draft
states it**. The draft argues by an open image and the identity theorem for
polynomials in several variables. Here:

- `SP`, `SP_box`: a function of an environment that is a polynomial in each
  variable *separately* and nonzero somewhere is nonzero somewhere with its
  first r coordinates in any box — one coordinate at a time, by root counting
  in one variable (`univariate_nonroot`). No multivariate normal form.
- `ptab`, `phi`, `phi_psum` — **the Newton map**: the power sums of an array
  are φ of the coefficients of ∏(1 − xᵢY); BladeNewton's identities read as
  a recursion. `SP_phi`: it is separately polynomial. `phi_onto`: it is
  **onto** — every (z₁ … z_r) is φ of some coefficients, solving for c_k one
  index at a time, which is where division by k enters.
- `perturbed_roots_multi`, `chamber_family`: bump the coefficients of
  Y¹ … Yʳ all at once, each by at most ε; the real roots persist, so the
  coefficient vectors of admissible arrays **fill a box** around the base
  ones.
- `nonzero_at_admissible_array`: hence a polynomial in the summary that is
  nonzero somewhere is nonzero at the summary of an admissible array of any
  extent N ≥ r.
- `closure_refused_R_polynomial` — **P8, negative half**: leading coefficient a
  polynomial in the summary, not identically zero; N ≥ d·r; no function G of
  the first r power sums, continuous or not. The lower coefficients stay
  arbitrary functions of the summary.

"Not identically zero" is semantic (nonzero at some r-vector of reals); the
polynomial is a `pexp` read on its first r variables.

## Outside the tower: `proofs/reals/BladeRoundingBound.v` (20 theorems, conditional)

**Not counted above.** Two axioms (`sig_forall_dec`,
`functional_extensionality_dep`). The third contract of section 11.2: a
finite-precision error theorem. The standard model of rounded arithmetic — every
operation exact up to relative error u, no underflow or overflow — is a
*hypothesis* of the section, not an axiom.

- `fsum_error` — recursive summation is within ((1+u)ⁿ − 1)·Σ|xᵢ| of the sum.
- `original_error`, `reduced_error` — the particle-wise fold of a·xᵢ + b and the
  reduced form a·S + N·b each lie within ((1+u)^(N+2) − 1)·Σ(|a||xᵢ| + |b|) of
  the exact value: the same radius, against the same condition number.
- `reduction_rounded_distance` — so they differ by at most twice that;
  `g_le_gamma` — the radius is at most n·u/(1 − n·u).

Rank one only (the S update). The bound says the reformulation is no worse
conditioned than the original; it does not make them bitwise equal.

## Outside the tower: `proofs/reals/BladeReduceClassification.v` (6 theorems, conditional)

**Not counted above.** Three axioms (those of BladeRealDensity).
BladeReduceCompiler's classifier is *exact* over ℝ.

- `real_compile_sound` — accepted programs: the compiler theorem at K = ℝ.
- `refused_no_reduction` — refused programs: at the parameter values of the
  witness there is **no** update function of the summary, continuous or not, at
  any extent N ≥ d·r. `closure_refused_R_polynomial` applied to the leading
  coefficient with its parameters frozen; a reduced program must serve every
  parameter value, so none exists.
- `classification_exact` — on every well-formed program the classifier answers,
  and the answer is right in both directions.
- `special_value_program_refused` — x′ = x + θx² is affine at θ = 0 and is still
  refused: at θ = 1 no update function of S exists for N ≥ 2.

## Outside the tower: `proofs/reals/BladeShiftedRounding.v` (16 theorems, conditional)

**Not counted above.** Two axioms (`sig_forall_dec`,
`functional_extensionality_dep`). The float contract for the shifted reduction.
The standard model of rounded arithmetic — every operation exact up to relative
error u, no underflow or overflow — is a *hypothesis* of the section.

- `mex_is_shifted_sum` — the exact reduced run, driven by the coefficient pairs
  (a_t, b_t), is the shifted sums of the true particle array about the
  propagated shift; `crun_is_mean` — started at the mean, that shift is the true
  mean at every step.
- `mfl` is the float reduced run, M ← fl(fl(aᵏ)·M), with aᵏ by repeated
  multiplication: k roundings a step.
- `shifted_moment_float_bound` — from a start known to relative error e₀, after
  T steps the float Mₖ is within relative error (1 + e₀)(1 + u)^(kT) − 1 of the
  true shifted sum. Independent of N, of the spread, of any condition number:
  the bound is relative to the quantity itself.
- `central_moment_float_bound` — the same against the true *central* moment
  when started at the mean; `shifted_moment_gamma_bound` — the radius in its
  familiar form n·u/(1 − n·u), n = kT.

Scope (stated in-file): coefficients come from the input and are shared by the
float and the exact run. Coefficients computed from the rounded summary feed
its error back and need a Lipschitz hypothesis on the coefficient functions —
not done. The shift itself is computed like one particle of the original
program, and its error is that particle's.

## Outside the tower: `proofs/reals/BladeShiftedFeedback.v` (68 theorems, conditional)

**Not counted above.** Four axioms: the reals' `sig_forall_dec`, `sig_not_dec`,
`functional_extensionality_dep`, and `Classical_Prop.classic`, which the
standard library's lemmas about `exp` use. The float contract when the
multiplier a is computed **from the summary itself** — by the float program,
from its own rounded moments. Because b never enters the moments
(Mₖ′ = aᵏMₖ), the moments form a closed feedback loop.

- `rcl` — log-relative error: x̂ = θx with e^(−ℓ) ≤ θ ≤ e^ℓ. Products add
  (`rcl_mul`), powers scale (`rcl_pow`), reciprocals keep (`rcl_inv`), square
  roots halve (`rcl_sqrt`), sums of non-negative terms keep the worst
  (`rcl_add_pos`); `rcl_rel` turns ℓ into an ordinary relative error e^ℓ − 1;
  one rounding costs λ = −ln(1 − u) (`rnd_rcl`).
- `alpha_stable` — the one new hypothesis: moments known to log-error ℓ give
  the coefficient to L·ℓ + ε. `normalizing_coefficient_stable` discharges it
  for a = fl(g / fl(√M₂)) with L = ½, ε = 2λ.
- `feedback_bound`, `rad_closed` — after T steps the float moments are within
  log-error (1 + rL)^T·ℓ₀ + r(ε + λ)·((1 + rL)^T − 1)/(rL); `rad_no_feedback` —
  linear in T when L = 0.
- `feedback_particles`, `feedback_particles_rel` — against the true particle
  system, whose coefficient is computed from the true moments, b arbitrary;
  `normalized_ensemble_bound` — the instance x′ = (g/√M₂)·x + b.
- `mcl`, `mcl_step`, `mixed_seed_input_bound`, `feedback_bound_read`,
  `normalized_ensemble_bound_seed` — a MIXED seed x̂ = θx + e. Only the
  moments the coefficient reads need a relative seed; any other moment's
  absolute error is carried by |∏aₜ|^k. Needed because a two-pass seed need
  not be relatively accurate at a zero moment: on [3, −10¹⁶, 10¹⁶] the true
  M₁ is 0, binary64 computes 2, and no relative radius exists
  (`seed_no_relative_radius`; audit 2026-10-15, RF-1).
- `normalized_exact_resets`, `normalized_m2_radius_constant`,
  `normalized_particles_linear` — under a = g/√M₂, M₂ = g² exactly after every
  step. Rounded, the float M₂ stays within 6λ of it at every step, with no
  seed accuracy needed, and every other moment's radius is linear in T.
- `normalized_ensemble_bound_dom` — with M₂ > 0 and g ≠ 0, no zero divisor or
  nonpositive radicand occurs in either run. The plain corollary relies on
  the totalized reals (1/0 = 0, √ of a negative = 0), where binary64 gives
  ∞ or NaN (audit 2026-10-15, RF-2).

The generic radius grows geometrically at rate 1 + rL. That is a conservative
worst-case upper bound under `alpha_stable`, not a lower bound: for g/√M₂ it
does not compound (above; audit 2026-10-15, RF-3). Not covered: a coefficient
that reads the mean (the shift), or subtracts moment-dependent terms.

## Outside the tower: `proofs/reals/BladeShiftedVariance.v` (39 theorems, conditional)

**Not counted above.** The four axioms of BladeShiftedFeedback. The variance in
a floating-point coefficient.

- `rcl_diff_dominant` — **subtraction with a dominant minuend**: for
  0 ≤ z ≤ ρ·y with ρ < 1, operands at log-errors ℓ_y, ℓ_z give y − z to
  log-error −ln Lo with Lo = (e^(−ℓ_y) − e^(ℓ_z)·ρ)/(1 − ρ), whenever Lo > 0.
  The upper side reduces to A + 1/A ≥ 2.
- `variance_rcl` — the float variance fl(fl(M₂/N) − fl(q·q)), q = fl(M₁/N),
  from moments at log-error ℓ: log-error λ − ln Lo(λ+ℓ, 3λ+2ℓ, ρ).
- `feedback_bound_inv` — the feedback theorem for a general, non-linear error
  rule φ and an invariant of the true run, valid while the iterated radius
  stays where φ holds. The invariant here is ρ ≤ ρ̄, which the true run
  preserves exactly (`BladeShiftedMoments.rho_invariant`).
- `renormalized_by_stddev_bound` — x′ = (g/sd)·x + b, sd the float standard
  deviation of the float moments, against the true particle system
  renormalized by its true standard deviation.
- `rcl_diff_dominant_mixed`, `variance_rcl_mixed`,
  `renormalized_by_stddev_mixed` — M₁ seeded with an absolute error
  s·√(N·M₂). It enters the variance as δ = e^(3λ)(2e^ℓ√ρ·s + s²), so only s²
  remains at ρ = 0 (`deltam_exact_mean`).
- `rho_certified_upper`, `stddev_admission_sound`,
  `renormalized_by_stddev_admitted`, `renormalized_by_stddev_mixed_admitted` —
  the true ρ is at most e^(2ℓ₁+ℓ₂)(|M̂₁| + D)²/(N·M̂₂), so a run-time check on
  float data implies `Inv_sd` and Lo > 0. The raw ratio M̂₁²/(N·M̂₂) can
  underestimate the true ρ (`uninflated_rho_fails_true_invariant`; audit
  2026-10-15, RF-4).
- `piter_length`, `vex_is_population_variance`,
  `renormalized_particles_bound`, `renormalized_particles_admitted` — with
  N = INR(length xs), N stays the particle count and vex is the population
  variance. The generic theorems leave N free (audit 2026-10-15, RF-5).

The radius is iterated, not closed-form, and depends on ρ, a property of the
data. ρ must be CERTIFIED (above): the raw two-pass estimate is not a theorem.
The mixed admission theorem takes the bound D on M̂₁'s absolute error as an
input; nothing yet derives D from a seed computation.

## Outside the tower: `proofs/floats/BladeBinary64Witness.v` (10 theorems, primitive floats)

**Not counted above.** No axiom, but `Print Assumptions` lists the primitive
float type and operations: the values are computed by the kernel's binary64
evaluation and are trusted to that extent.

- `binary64_original_inexact`, `binary64_reduced_inexact` — the two witnesses of
  BladeNumericContract at p = 53, on real binary64.
- `binary64_no_ordering_recovers_the_reduced_value` — all twelve orderings of
  the original give 2⁵³ + 6; all orderings of the reduced form give 2⁵³ + 8.
- `binary64_reduced_overflows`, `binary64_reduced_nan` — the reduced form
  returns ∞, or NaN, where the original is finite.

With a = 1 the product a·x is exact, so a fused multiply-add computes the same
values: these do not depend on `-ffp-contract`.

- `binary64_raw_moments_lose_the_variance` — why a float summary must be
  shifted: at 10⁸, 10⁸+1, 10⁸+2 the raw formula Q/N − (S/N)² returns 2 (and 1
  when g++ contracts to FMA); the central form returns 2/3, correctly rounded.
- `binary64_two_pass_seed_first_moment` — the two-pass seed on
  [3, −10¹⁶, 10¹⁶] gives mean 1 and M₁ = 2 where the true M₁ is 0.
  `binary64_zero_spread_normalization_nan`, `binary64_negative_radicand_nan`
  — NaN where the totalized reals give 0.

## What remains unproved

Still open, from `docs/plans/plan-one-model-surface-calculus.md`: the surface
calculus beyond its structure-first fragment (T-dimensions and `irank` slices,
local `let`, an `ERec` constructor — BladeInduction gives the object it would
denote); compound identity groups (§8.4 at s > 1) and groups of symmetric
operands, which the typing rule leaves dense (decision D1 not built;
`d1_not_built_pin`, 16 vs 10); the free-model pipeline lower bound beyond the
two-node schema (a general translation gives ≤ `orbit_work`, equality only when
each stage's typed group is its whole licence); general split identity classes
(`comm(a, c)` over (A, B, A) needs a position-permuted layout), signed kernels
over several arrays, mixed symmetric/antisymmetric blocks and Hermitian
conjugation; the orbit lower bound relative to a value relation R (only
attainment is restated); the converse of the boundary theorem (not-free does
not imply beatable); the storage policy of recursive arrays, nested `let rec`
and AD through recurrences; general-r
verified offsets, the storage dichotomy at general r (r = 3 closed in both
directions by BladeDichotomy, and the regime below the free cell by
BladeDichotomyTies; the general minimal-width-r! statement is prose), k-slot
structure, typed-path
combinator laws, the adjunction proper, the general-r Jacobian transfer /
accumulation multiplicities (rank-2 forms: BladeJacobian), and — for uniform
optimality — a proper subgroup H < S_R, the signed bound at general r,
non-opaque storage, kernels of arity > 2 and folds inside a pipeline, and the
orbit work formula over an arbitrary schema (BladeOptimal / BladeOrbitWork
prove H = S_R at any binding, the sign character at r = 2, and the term form
with one two-node instance); and completeness of the SIGNED deduction rules
(PNeg, PConj, call summaries — BladeDeduceExact is exact for the unsigned
fragment only). For exact recurrence reduction (a research draft, no shipped
feature): everything that would make it a Blade feature. BladeReduceCompiler is
a model fragment and BladeReductionAD a model of the AD rules — neither is
checked against `src/`; the AD model has no intrinsics, units or packed
reconstruction, and restates the compiled fragment through the emitted rules
for one concrete program only; the rounding-error bound for the raw form is rank
one; the shifted form's bounds cover coefficients from the input and, with a
stability hypothesis, coefficients computed from the tracked moments and the
variance, but not from the mean; which numerical contract Blade
adopts is a design decision — `docs/plans/plan-float-summary-reduction.md`
proposes one.
(Every P-item of the draft is machine-checked. P8's refusal is
axiom-free at N ≥ 2^(dr−1) and, at the draft's threshold N ≥ d·r and in the
draft's full generality, proved over ℝ OUTSIDE the tower; P6, P4a and P7
against differentiable summaries likewise — all conditional on the axioms of
Coq's reals, see above.)

---

## Coverage: formalism claims ↔ checked artifacts

Status legend: **FULL** = checked as stated · **INSTANCE** = checked at
specific rank/fragment, general form prose · **CORE** = the load-bearing part
checked, framing prose · **PROSE** = argument grounded on checked anchors but
itself unmechanized · **CORRECTED** = v10 claim refuted/amended by the tower.

| Formalism claim (v10 numbering) | Artifact(s) | Status |
|---------------------------------|-------------|--------|
| 2.1 S/T completeness | `veval_pval` (V∘P = id; T/S = trivial-plan fragment) | CORE |
| 2.2 T-dim relationality; 2.3 no iteration objects in T/S | `tdim_relationality`, `dmwf_index_level_kernel_independent` | CORE (2.3 prose on these anchors) |
| 2.5–2.9 syntactic impossibility / reification necessity | `residual_not_constant`, `output_family_not_constant`, `nat_not_arrow` | PROSE on checked anchors |
| 2.7 double metamorphism | `dmwf_equation` | FULL (index level) |
| 9.1–9.7 Trinity | `trinity_fold_closure`, `enumShape_app(_length)`, non-constancy anchors; asymmetry: `ap_semantics_is_free_fold`, `nary_generated_by_unary`, `enumShape_monoid_hom` | CORE; restated as generators + closure |
| 9.9 lower₂₁ | `lower_2_1` (instance of `output_symmetry_soundness`) | FULL |
| 9.11 H ∩ Stab | soundness `output_symmetry_soundness` + exactness `license_exactness` | FULL, now an IFF |
| 9.13/9.14 input symmetry consumed | `input_symmetry_not_sufficient` | FULL |
| 9.16/9.18 raising | `raise_1_2`, `raise_compose` | FULL |
| 9.17 shared units insufficient | `shared_units_insufficient` | FULL |
| 9.19/9.20 deduced commutativity | `deduced_commutativity`; exactness of the deduction: `uniform_equality_exact`, `parfix_exact`, `deduced_group_largest` | FULL (soundness); exactness at the unsigned fragment |
| 9.23/9.24/9.26 two maximal curryings | `identity_needs_every_position`, `comm_needs_kernel`, `two_maximal_curryings(_general)` | FULL (information-theoretic form) |
| 9.29 uniqueness / duality derivation | `fusion_duality`, `detections_jointly_license` | FULL (fused-primitive form) |
| 10.9 step 3 per-dimension SymIdx output | `per_dim_swap_not_symmetry` | **CORRECTED** (refuted) |
| 12.1 compose-apply | `compose_apply_duality`, `mpipe_hom`, `fusion_eliminates_buffer` | FULL (fragment + real-store form) |
| 12.2 rank-0 convergence | `rank0_convergence`, `fuse2_pairs` | INSTANCE (elementwise fragment) |
| 12.3/12.4 wrap round trips | `veval_wrap0`, `wrap0_idempotent` | INSTANCE |
| 12.5 parallel laws | `parallel_associative` (exact), `parallel_commutative` (Permutation), `application_not_commutative` | FULL |
| 12.7 evaluation non-faithful | `veval_not_injective` | FULL (core) |
| MonadPlus table | `vbind_*`, `vplus_*` | FULL (value level); right distribution REFUTED |
| 14.1–14.3 left-justified storage/access | `lj_correct`/`unlj_correct` (general), `lj2/lj3_*`, `access_exact` | FULL |
| 14.4 cardinality | `storage_cardinality` (C(n+r−1, r)) | FULL |
| 14.5 product symmetry (r!)^d | `diagonal_group_law` (joint r! sound), `per_dim_swap_not_symmetry` + `counting_general(_C)` (per-dim refuted); `reynolds_full_product_symmetry` (Reynolds route genuine); `cauchy_split_access` (+`cauchy_cell_count`) (r = 2 structural recovery) | **CORRECTED** — see formalism §12.4 |
| 14.6 partial product symmetry via shared spaces | `shared_units_insufficient` | **CORRECTED** (identity required) |
| "The fastest way is the only way" (work and storage halves) | `uniform_optimality_sym`, `uniform_optimality_young(_nat)`, `antisym_lower_bound` + `antisym_attained`, `sym_nest_storage_bound`; pipelines: `uniform_optimality_pipeline`, `two_node_optimal` | CORE — kernel evaluations and cells against any uniform program, H = S_R at any binding; time is NOT claimed (BladeLayout's scope line stands) |
| Bounds safety | `typed_access_safe`, `bidx_access_safe`, `offset_in_range/injective`, `roff_closed` | FULL at r = 2 (`indexing_total` do-not-cite) |
| Enumeration/order guarantees | `enum_sound/complete/NoDup`, `enumShape_NoDup`, `enumA_lex_sorted` + instances, `enum_offset_respects_lex` | FULL |
| CompoundIdx denotation | `compoundk_denotation`, `compound_enum_denotation`, `compound_arrow_denotes_mask`, `compoundk_lex_sorted` | FULL (positive rank: `compoundk_denotation`; every rank: `compound_enum_denotation`) |
| Affine/strict storage | `dlj/dunlj_correct` + δ-corollaries | FULL |
| V ⊣ P adjunction | round trip + monoid hom + non-injectivity | CORE; adjunction proper conjectural |
| 2.7 / 3.9 the DMWF is the quotient (the feedback IS the group action) | `enum_is_quotient`, `isort_fibres_are_orbits`, `enumShape_is_quotient`, `scanon_fibres_are_orbits`, `orbit_count` | FULL (one record, and every shape under the Young action) |
| 3.9 / 12.2 canonicalizing access = storage | `qread_invariant`, `qread_fixes_transversal`, `qread_is_storage` | FULL |
| 13.2 typing rules; 14.4 compute | `progress`, `preservation`, `normalizes`, `step_den`, `eval_sound` | CORE (the structure-first fragment: rank-0 kernels, rank-1 identity groups, top-level binding) |
| 8.4 step 3 / 11.2 as a function of the derivation | `typed_app_is_quotient`, `comm_license_needed`, `covariance_shape`, `aba_unlicensed` | FULL at `s = 1`; compound groups (`s > 1`) are plan item 3 |
| 9.19/9.20 deduced commutativity is a licence | `deduced_comm_is_licence` | INSTANCE (binary kernels; general arity via `deduced_group_largest` open) |
| 10.2/10.5 combinator symmetry laws (σ(C₁<&>C₂)=σ₁×σ₂, `<*>` = shape concatenation, zip common shape, `>>@` first stage, `<&!>` same loop / meet) | `BladeTransport.out_shape_app_clean`, `block_perm_app`, `shapeCard_product_seam`, `zip_transport`, `pipe_transport`, `par_group_product`, `fuse_meet`, `fuse_is_par`, `group_of_laws` | FULL for rank-1 identity groups; `<*>` concatenation is a fact about the LOOP, the typed output glues the seam (`out_shape_app`); compound and symmetric-operand groups left dense (D1 not built) |
| Orbit work formula W = Σ ω(G_v, X_v) for map pipelines; fusion cost | `BladeTransport.pipeline_attains_orbit_work`, `stage_is_surface`, `two_node_translation`, `fuse_object_prog`, `fusion_evaluations` + BladeOrbitWork's lower bound | Attainment for every pipeline; free-model bound bridged program-level on the two-node schema; typed W exceeds the free count when the type rule under-quotients (12 vs 9); folds out |
| Orbit bound attained at proper subgroups H < S_R: comm on declared blocks composed with the binding (the meet) | `BladeAttain.uniform_optimality_subgroup`, `meet_lower_bound`, `meet_attained`, `uniform_optimality_block_nat`, `meet_ex_forced`, `surface_comm_optimal`, `surface_den_is_call_site` | FULL when H ∩ Stab's classes are contiguous; one split instance (A, B, A); general split classes open |
| 8.3 neighbour rule: sound, not optimal | `BladeSurface.aba_unlicensed` (sound) + `BladeAttain.aba_beats_surface_rule`, `aba_orbit_count_forced`, `aba_gap_strict` | **CORRECTED** at (A, B, A): the rule stores n³ where n·C(n+1, 2) is forced and attained; see §8.3's new remark and decision E14 |
| Signed orbit bound C(n, r) at every r | `BladeAttain.Kalt_sign_character`, `antisym_lower_bound_r`, `alt_vanishes`, `antisym_attained_r`, `alt_read_pin` | FULL (one array; several arrays, mixed characters, Hermitian open) |
| 7.5 recursive arrays: one-buffer fixpoint, unique solution, implicit zero history, work N × orbits | `BladeInduction.letrec_is_wf_fixpoint`, `letrec_fixpoint_self`, `letrec_unique`, `letrec_zero_history`, `letrec_work(_shape)(_optimal)` | FULL (values nat / Arr nat; storage policy not modelled) |
| Recursion axis leads; the quotient survives on the remaining axes | `BladeInduction.block_perm_lead`, `dense_agrees_with_quotient`, `letrec_commutes_with_quotient`, `letrec_commutes_with_quotient_sym` | FULL for steps invariant under orbit-invariant readers at dense tuples (`sym_step` included) |
| 7.5.1 `while` guard: defined to the first false guard, frozen after, BL8010 on budget exhaustion, seed arm unguarded | `BladeInduction.gbuf_spec`, `letrec_guard_prefix_agrees`, `letrec_guard_frozen`, `letrec_guard_budget`, `letrec_seeded_guard` | FULL |
| Bit-changing optimizations (`omp` on reduce, FP_REASSOC) are licences up to a declared relation; typing, soundness and the quotient theorem hold relative to it | `BladeLicence.has_ty_R`, `typed_app_is_quotient_R`, `has_ty_RC`, `typed_is_quotient_RC`, `progress_R`/`preservation_R`, `reassoc_licence` | FULL for a parameter R (reflexive + transitive; congruence + identity for re-bracketing); the `has_ty_R` quotient is direct-application only, the full grammar needs R-congruent pipes (`has_ty_RC`); attainment only, not the lower bound |
| Rounded reduction needs a declared, necessarily coarse licence | `BladeLicence.kfold_opr_not_kwf`, `rounded_licence_coarse`, `assoc_congruence_collapses`, `rounded_fold2_exact`, BladeNumericContract | FULL (integer-data rounding model, every p ≥ 2) |
| The orbit bound holds on law classes free on the orbits (kernel genericity) | `BladeBoundary.free_class_bound`, `canonical_task_attained`, `orbit_bound_boundary` | One direction (holds and is attained on every free class); converse open |
| Structured kernels beat the query bound, not the storage bound | `BladeBoundary.structured_kernel_beats_bound`, `sep_query_complexity`, `sep_storage_bound`, `genericity_two_sides` | FULL (separable class, r = 2: n queries vs C(n+1, 2); storage still C(n+1, 2)) |
| "The fastest way is the only way", measured | `docs/research/orbit-work-measurements.md` | Blade's kernel-call and cell counts equal the orbit count on every row with distinct-name groups (28/66/276/1035 at n = 7/11/23/45, and the rank-3 and two-group rows); NumPy is dense on every row; BLAS `dsyrk` writes C(n+1, 2) cells; the (A, B, A) row shows the compiler's by-name quotient with a reindexing DEFECT (wrong reads, a crash at mixed extents) |
