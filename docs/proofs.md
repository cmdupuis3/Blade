# Blade Proofs

Prose mirror of the machine-checked proof tower **blade-proofs-v16**: 20 files,
**241 theorems**, Coq 8.18, stdlib only, verified by both `coqc` and `coqchk`.
Sources: `proofs/` (`proofs.zip`). Build:
`coq_makefile -f _CoqProject -o Makefile && make`.

Rules of this document:

- The `.v` files are canonical; this document is the map. Every claim here
  names its Coq artifact. Nothing is `Admitted`; there are no axioms.
- Scope caveats are stated where the files state them (rank-2 only,
  materialized fragment, do-not-cite, etc.). Where the formalism's prose
  claims outrun the checked artifact, this document is the arbiter of what is
  actually proved.
- Formalism theorem numbers (2.x, 9.x, 12.x, 14.x) refer to the v10 numbering,
  which the checked comments cite; [formalism.md](formalism.md) sections cite
  back into this document.
- Remaining open items live in [future.md](future.md) §4 (from the tower's
  ROADMAP).

## The tower at a glance

| Layer | Files | Content |
|-------|-------|---------|
| 0–1: index universe + DMWF | BladeDMWF | canonical tuples, enumeration, the general left-justified bijection, kernel-independence |
| 1.5: arrows | BladeArrow, BladeAffine, BladeCompound, BladeShape, BladeLex | the arrow as coalgebra; Sym/Antisym/affine/Compound instances; uniqueness; lex order |
| Cardinality | BladeBinomial, BladeCounting | C(n+r−1, r) closed form; the no-lossless-product-layout theorem |
| 2: currying | BladeCurrying, BladeCurryingGeneral | dependence boundary; the two maximal curryings (information-theoretic form) |
| 3: symmetry | BladeCore, BladeLowering, BladeCompleteness, BladeFusionDuality | group law both halves, H-and-Stab soundness AND exactness, sign-tracked variant, fusion ⇒ duality |
| Trinity | BladeTrinity, BladeTrinityAsym | `<*>` = shape concatenation; generators + forced closure |
| Computation | BladeCompute, BladeMonad, BladeSafety | materialized semantics, V∘P = id, 12.x laws, MonadPlus, verified offsets + bounds safety + buffer-elimination fusion |
| Storage split | BladeCauchy | the r = 2 Cauchy split |

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

## BladeDMWF.v (20 theorems) — index universe and the double metamorphism

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

## BladeBinomial.v (7) — storage cardinality closed form

`mscard_binom`: mscard r l u = C(u−l+r−1, r), by the hockey-stick identity
(`hockey`) proved by induction over the sum the dmwf_equation produces.
`enum_length_binom`, and the headline `storage_cardinality`:
|SymIdx<r, n>| = C(n+r−1, r).

## BladeCounting.v (15) — the general counting theorem

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

## BladeCurrying.v (8) — dependence boundary; two maximal curryings

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

## BladeCurryingGeneral.v (10) — the generalized maximal-currying theorem

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

## BladeLowering.v (18) — symmetry lowering and raising, corrected

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

## BladeArrow.v (14) — the arrow as coalgebra

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

## BladeAffine.v (8) — the affine feedback descriptor

`step l i = i + δ` unifies lj (δ = 0) and alj (δ = 1); storage domain
l + Σa + δ(r−1) < u. Round trips proved once (`dlj_correct`,
`dunlj_correct`, over `affine_arrow_correct`); four corollaries recover the
Sym and Antisym transforms exactly (`delta0_recovers_canonical/storageOK`,
`delta1_recovers_scanonical/astorageOK`). Strided strictness (δ ≥ 2) covered
for free. (Nonlinear δ(r−1) leaves discharged by nia.)

## BladeCompound.v (7) — the rank-k Compound arrow

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

## BladeShape.v (3) — shape-level uniqueness

`enumShape_NoDup`: shape enumeration emits each tuple exactly once, via
`canonical_length` (canonical tuples have fixed per-record length) and
`app_split_length` (unique app-decomposition). Completes the enumShape
theorem set (membership: BladeTrinity's `trinity_fold_closure`).

## BladeLex.v (12) — iteration order = storage order

`enumA_lex_sorted`, proved ONCE at the arrow level: any arrow with strictly
increasing heads enumerates in strictly increasing lexicographic order.
Inherited: `enum_lex_sorted` (Sym), `affine_lex_sorted` (hence Antisym),
`compoundk_lex_sorted` (filtered heads stay sorted — the semantic enumeration
matches the compiler's dense mask-scan order). Payoff
`enum_offset_respects_lex`: earlier enumeration position ⇒ lex-smaller tuple;
since storage offset IS enumeration position, offset order embeds lex order.
(Converse routine via trichotomy + NoDup; noted as remark, no consumer.)

## BladeTrinity.v (7) — positive constructions

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

## BladeTrinityAsym.v (14) — the pillars are not co-equal

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

## BladeCompleteness.v (11) — H-and-Stab EXACTNESS

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

## BladeFusionDuality.v (5) — fusion ⇒ duality

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

## BladeCompute.v (24) — the computation model

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

## BladeCauchy.v (15) — the r = 2 Cauchy storage split

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
amended at r = 2. Honest scope: r = 2 only (r ≥ 3 has mixed Schur components
— genuinely open); totals equal the joint count (the win is structure, not
cells); each read costs two lookups plus a halving; the bridge to concrete
lj/alj layouts is mechanical and not done.

## BladeMonad.v (18) — monad and combinator laws

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

## BladeSafety.v (9) — bounds safety with a failure model

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

Scope: rank 2 (general-r offset = nested hockey-stick sums — future.md §4);
surface progress/preservation is the remaining open species (future.md §4.1).

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

## What remains unproved

See [future.md](future.md) §4: surface-calculus progress/preservation (the
one missing species — deliberately sequenced after the rewrite settles
surface syntax), general-r verified offsets, r ≥ 3 storage splits, k-slot
structure, typed-path combinator laws, the adjunction proper.
