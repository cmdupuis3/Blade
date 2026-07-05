# Blade Proof System -- Roadmap for Open Items

Status as of blade-proofs-v13 (18 files, 214 theorems, coqc + coqchk).
This document lists every remaining deferred item, with a concrete
design where the direction is settled and an honest confidence note
where it is not.

## Closed in v13

- **The r = 2 CAUCHY STORAGE SPLIT** (BladeCauchy.v, 15 theorems):
  the v11 conjecture is now theorem. For ANY jointly-symmetric T
  (the only symmetry one identity group grants), Psym = T + lon-swap
  and Qalt = T - lon-swap satisfy: 2T = Psym + Qalt; Psym has FULL
  S2 x S2 product symmetry and Qalt is antisym (x) antisym (both lat
  directions via joint symmetry -- where the decomposition earns its
  keep); Qalt vanishes on either diagonal; cauchy_split_access: the
  two per-dim-canonical component stores losslessly determine T at
  every logical index with product-of-sort-signs; cauchy_cell_count:
  cells(P) + cells(Q) = joint count exactly, division-free;
  cauchy_cells_3_3: 36 + 9 = 45 computed from the live enumerations
  (sym enum + antisym arrow). Consequence: single-identity-group
  r = 2 (covariance-class) regains EXACT per-dimension
  product-structured storage via SymIdx (x) SymIdx plus sign-tracked
  AntisymIdx (x) AntisymIdx; the "flattening is forced" conclusion
  is amended at r = 2. Honest scope: r = 2 only (r >= 3 has mixed
  Schur components -- genuinely open); totals equal the joint count
  (the win is per-dim structure, not fewer cells); bridge to
  concrete lj/alj layouts mechanical and deferred.

## Closed in v12

- **Computation model + combinator laws** (BladeCompute.v, 24
  theorems): materialized-list semantics (veval p = map kernel over
  enumShape; funext-free). Duality/adjunction facts: veval_pval
  (V o P = id -- the counit identity AND the checked constructive
  core of Theorem 2.1: T/S is the trivial-plan fragment);
  veval_not_injective (Theorem 12.7's core). Theorem 12.1
  (compose_apply_duality) mechanized -- the proof IS map_map, making
  the fusion connection exact. slot_interchange: kernel-slot and
  array-slot composition agree under evaluation -- the 2-slot
  instance of curryings <-> slot-classes <-> composition operators;
  a third maximal currying (mixed-source property) would force a
  third operator and a triangle of interchange laws. Theorem 12.2
  (rank0_convergence, thin by design in the shallow semantics) +
  fuse2_pairs (fused evaluation = pairwise product evaluation, the
  semantic heart of <@>). Corollaries 12.3/12.4 via wrap0 round
  trips. MonadPlus value-level laws (zero/plus identities,
  associativity, pipe distributes). Theorems 9.19/9.20 (deduced
  commutativity) in the invariance vocabulary. Coverage-audit row
  updates implied: 2.1 PROSE->PARTIAL; 12.1 FULL (fragment); 12.2/
  12.3/12.4 INSTANCE (elementwise fragment); 12.7 core FULL;
  MonadPlus PARTIAL->FULL (value level); 9.19/9.20 FULL. Scope:
  elementwise/rank-0 fragment; rank-changing pipelines (slice-typed
  values) and plan-level plus remain with the surface-calculus item.

## Research note (v11): Cauchy decomposition

The counting theorem is the shadow of the classical Cauchy
decomposition Sym^r(V (x) W) ~= sum over partitions of
S^lambda(V) (x) S^lambda(W); product storage is the leading term.
Recorded in BladeCounting.v (cite Cauchy; Reynolds = projection onto
the leading term). The r = 2 constructive storage split (NOW PROVED in v13, BladeCauchy.v) (jointly-symmetric = sym(x)sym + antisym(x)antisym,
each product-storable, totals exact: 36 + 9 = 45). Mechanization
would need a x2/sum formulation over nat or a move to Z;
dimension- and character-checked only so far.

## Closed in v10

- **Lex-sortedness** (BladeLex.v, 12 theorems): iteration order =
  storage order, proved ONCE at the arrow level -- any arrow with
  strictly increasing heads enumerates in strictly increasing
  lexicographic order (enumA_lex_sorted). Instances inherited: enum
  (via the sym_arrow_correct list equality), the affine arrow (hence
  Antisym), and the rank-k Compound arrow (filtered heads stay
  sorted) -- discharging BladeCompound's order-agreement note: the
  semantic enumeration is lex, matching the compiler's dense mask
  scan. Payoff corollary enum_offset_respects_lex: earlier
  enumeration position implies lex-smaller tuple; the converse
  (trichotomy + NoDup) is routine and noted as a remark since no
  downstream theorem consumes it.

## Closed in v9

- **GENERAL COUNTING THEOREM** (BladeCounting.v, 15 theorems): for
  d >= 2 dimensions of extents >= 2 and rank r >= 2,
  prod_j |MS(n_j, r)| < |MS(prod_j n_j, r)|, with the binomial form
  prod_j C(n_j + r - 1, r) < C(prod n_j + r - 1, r)
  (counting_general_C). Full proof, no conjecture remaining: the
  two-factor core is an injection-plus-missing-witness argument on the
  enumerations themselves (pairing e(x,y) = x*b + y is canonical-
  preserving and injective; the tuple 1 :: b :: ... decodes to a
  non-canonical second component), lifted to arbitrary d by strict
  multiplicative induction, pigeonhole via NoDup_incl_length.
  BladeCore's r = 2, d = 2 arithmetic instance is subsumed; the
  no-lossless-product-layout half of the Group Law now holds at every
  rank and dimension count. Hypothesis sharpness noted in-file
  (r = 1, d = 1, or any n_j = 1 give equality).

## Closed in v8

- **Affine feedback descriptor** (BladeAffine.v, 8 theorems): the
  delta-parameterized arrow (step l i = i + delta) unifies lj (delta
  = 0) and alj (delta = 1); storage domain l + sum + delta * (r - 1)
  < u with round trips proved once; four instance corollaries recover
  canonical/storageOK and scanonical/astorageOK exactly. Strided
  strictness (delta >= 2) covered for free. Nonlinear delta * (r - 1)
  leaves discharged by nia.
- **Shape-level NoDup** (BladeShape.v, 3 theorems): enumShape
  enumerates each tuple exactly once, via canonical_length (fixed
  per-record lengths) and unique app-splitting. Completes the
  enumShape theorem set (membership was trinity_fold_closure).

## Closed in v7

- **Rank-k Compound arrow** (BladeCompound.v, 7 theorems): CompoundIdx
  denotation at every rank. has_completion is the mask-conditioned
  residual (FilteredIdx) in executable form; compoundk_denotation:
  the arrow enumerates exactly the in-bounds mask-true tuples;
  soundness stated for nonempty dims (the designed base-case
  asymmetry), completeness unconditional, uniqueness via filtered
  NoDup; rank2_subsumed shows BladeArrow's concrete rank-2 instance
  is the [n; m] case. Value-dependent step through has_completion
  makes the dichotomy apply: Compound factors iff the mask is a
  product.

## Closed in v6

- **Fusion => duality** (BladeFusionDuality.v, 5 theorems): the
  method_for / object_for duality DERIVED from loop-index fusion.
  fused_form names the fusion equation (Out = loop welded to indexing,
  kernel and arrays abstract); all_same_stabilizes bridges the currying
  layer's identity detection to the lowering layer's Stab premise;
  detections_jointly_license closes the loop (the two detected
  properties instantiate the H-and-Stab license on the fused
  primitive); fusion_duality restates the two-maximal theorem under
  the fusion reading. Duality = fusion's two-sorted slot structure +
  detection pruning (9.23/9.24); exactness is why nothing less
  suffices. Formalism section 2 can now open with fusion and derive
  the duality by citation.

## Closed in v5

- **Trinity asymmetry + applicative laws + monoidal evaluation**
  (BladeTrinityAsym.v, 14 theorems): the Trinity restated as two
  generators (loop reification, dimensional currying) plus forced
  closure (arity polymorphism). ap_semantics_is_free_fold: the n-ary
  enumerator is the free fold of a binary combinator over reified
  arrows -- a single non-arity-indexed function. nary_generated_by_unary:
  every n-ary loop is generated by unary loops under the applicative
  product (the formal echo of the unary C++ object_for prototype).
  sprod unit/associativity laws checked (section 12's assertions).
  enumShape_monoid_hom: evaluation is a STRICT monoid homomorphism --
  the checkable core of the V-P discussion (V is strict monoidal);
  the adjunction proper remains conjectural pending Plan Hom-sets.
  Formalism action: restate 9.7 as generators + closure (mutual
  presence under the design goal, not co-equality); present 12.5-12.8
  as monoidal evaluation (checked) + adjunction conjecture.

## Closed in v4

- **H-and-Stab COMPLETENESS** (BladeCompleteness.v, 11 theorems):
  Theorem 9.11 may now be stated as EXACTNESS. Both necessity halves
  are checked: kernel_invariance_necessary (a kernel not invariant
  under s is distinguished by free data -- three lines, data realizes
  the witnessing tuple) and stab_violation_detected (the maximally
  symmetric kernel fsum, proved invariant under EVERY permutation via
  Permutation reindexing, detects any stabilizer violation with
  indicator data: Out ix = 1 vs Out (ix o s) = 0). license_exactness
  packages the iff: for fsum, uniformly licensed permutations =
  stabilizer, exactly. Precise formalism reading: the largest grant
  sound for every H-kernel and all data is H intersect Stab; a
  degenerate specific kernel can be accidentally symmetric beyond the
  grant (remark, not hedge). Permutations carry explicit two-sided
  inverses on [0, r) (perm_pair).

## Closed in v3

- **Binomial identification** (BladeBinomial.v): mscard r l u =
  C(u - l + r - 1, r) via the hockey-stick identity, proved by
  induction over the sum the dmwf_equation itself produces. Headline
  corollary: |SymIdx<r, n>| = C(n + r - 1, r).
- **Composite-index unification** (BladeLowering.v): the H-and-Stab
  framework's index type is now an opaque parameter Ix; the diagonal
  Group Law for one identity group of rank-2 arrays is the swap
  instance at Ix := nat * nat (diagonal_group_law), unifying
  BladeCore's concrete diagonal_swap_is_symmetry into the tower.
- **Sign-tracked lowering** (BladeLowering.v):
  output_antisymmetry_soundness (kernel anti-invariance => output
  antisymmetry, same two-step proof) with the r = 2 antisymmetric
  instance (antisym_lower_2_1). Hermitian is the identical statement
  with neg := conjugation; the diagonal-vanishing corollary needs
  group structure on U and is intentionally not claimed.

## Open items

### 1. Surface-calculus progress/preservation (large; sequencing note)

The one species of theorem the tower still lacks: type soundness for
a Blade-core surface calculus elaborating into BladeCore section 7's
intrinsic types (where bounds safety is definitional). Estimate 500+
lines and, more importantly, it hard-codes surface syntax -- so it
should WAIT until the compiler rewrite settles the surface language,
or it will be proved twice.

## Consolidation note (no action yet)

Three BladeCore results are now subsumed by general theorems:
lj2/lj3 (by BladeDMWF's lj/unlj), diagonal_swap_is_symmetry (by
diagonal_group_law). Keeping them costs nothing and preserves the
readable r = 2 entry points; prune only if the tower is ever
published as a standalone artifact where redundancy reads as padding.

## Standing tactic-trap ledger (Coq)

1. `subst x` with multiple defining equations for x may consume the
   wrong hypothesis (three occurrences to date). After `inversion`,
   prefer Forall_inv-style projections or targeted substitution.
2. Fixpoints unfold only when the struct argument is
   constructor-headed (C n 0 does not reduce for variable n).
3. `repeat split` / `constructor` can close goals by conversion,
   desynchronizing bullet scripts. Use explicit `split; [| split]`
   when bullets carry structure.
4. `simpl` pre-reduces singleton maps and under binders; `in_map_iff`
   and staged brackets may then fail. Destruct literal membership;
   fuse brackets into single sentences with `;`.
5. Quartic `nia` can OOM; factor inequalities to quadratic cores.
6. Rewriting with a lemma whose LHS pattern matches its own RHS
   (swap lemmas) loops under `repeat`.
