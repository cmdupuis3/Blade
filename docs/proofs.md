# Blade Proofs

Prose mirror of the machine-checked proof tower in `/proofs/`:
**798 theorems**, Coq 8.18 / Rocq 9.0, stdlib only, verified by both `coqc`
and `coqchk`.

Build: `coq_makefile -f _CoqProject -o Makefile && make`.

Rules of this document:

- The `.v` files are canonical; this document is the map. Every claim here
  names its Coq artifact. Nothing is `Admitted`; there are no axioms.
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
`enumA_complete`, `enumA_NoDup`, and `const_state_rectilinear` — trivial
feedback ⟺ rectilinear domain (the general dependence dichotomy: constant
residual is exactly what factors into separate arrows).

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
at every rank); `rank2_subsumed` (BladeArrow's rank-2 instance is the [n; m]
case).

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
  constant — no single non-dependent type hosts all arities (type-level
  anchor for 9.2/9.5, mirroring `residual_not_constant` one level up).

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
first exist. The general-r statement (minimal width r!, and a genuine
tie-breaking exception at (r,L,M) = (3,2,2), where exactly 4 of 4096
tie-breaking rules admit width 2 — so the general theorem must quantify over
tie-breaks) is the prose development; a general-r mechanization needs a
ℚ[S_r] development, exactly as BladeWreath's general-r exactness. Schemes
with wider stores or multi-cell reads are a different class, not refuted.

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
  indices**. Semantic form `jacobian_transfer_semantic`: ∂ₐe at the
  swapped environment equals ∂ᵦe, i.e. the derivative/cotangent field
  over a symmetric primal is itself symmetric — so canonical
  (triangular) derivative storage is lossless, by the same one-step
  canonical-access argument as any symmetric array (`access_exact`,
  `raise_1_2`; not re-mechanized).
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
symmetric group on all 2r positions
([plan-orbit-index-types.md](plan-orbit-index-types.md)).

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

## BladeLayout.v (163 theorems) — striding parity and the layout group

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
reference set (`canonical_is_in_space`, `canonical_is_cost_minimal`), with
uniqueness decided per instance and `is_canonical` the decision
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

## What remains unproved

Still open: surface-calculus progress/preservation (the one missing species —
deliberately sequenced after the rewrite settles surface syntax), general-r
verified offsets, the storage dichotomy at general r (r = 3 closed in both
directions by BladeDichotomy; the general minimal-width-r! statement and its
(3,2,2) tie-breaking exception are prose), k-slot structure, typed-path
combinator laws, the adjunction proper, and the general-r Jacobian transfer /
accumulation multiplicities (rank-2 forms: BladeJacobian).

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
| 9.19/9.20 deduced commutativity | `deduced_commutativity` | FULL |
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
| Bounds safety | `typed_access_safe`, `bidx_access_safe`, `offset_in_range/injective`, `roff_closed` | FULL at r = 2 (`indexing_total` do-not-cite) |
| Enumeration/order guarantees | `enum_sound/complete/NoDup`, `enumShape_NoDup`, `enumA_lex_sorted` + instances, `enum_offset_respects_lex` | FULL |
| CompoundIdx denotation | `compoundk_denotation`, `compound_arrow_denotes_mask`, `compoundk_lex_sorted` | FULL (every rank) |
| Affine/strict storage | `dlj/dunlj_correct` + δ-corollaries | FULL |
| V ⊣ P adjunction | round trip + monoid hom + non-injectivity | CORE; adjunction proper conjectural |
