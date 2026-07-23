# Blade Future Plans

This file serves as a source for designs and speculative features that
haven't been implemented yet.

Ordered by maturity: **committed** (design settled, will be built), 
**designed** (spec draft exists, timing open),
**research** (direction known, design incomplete), **speculative** (ideas worth
recording).

---

## 1. Committed (implementation debt, design settled)

Mostly small items surfaced by the v7 implementation:

1. **Semijoin/antijoin hash fusion.** The O(|A|+|B|) set-hoist for
   `compound(A, mask(A, x -> contains(B, x)))` was attempted, found to be a
   no-op as wired, and removed. Correctness tests are in place
   (`sql-semijoins`, incl. the must-not-misfire conjunction case); redesign the
   optimization against them.
2. **Sort laziness / order in types** — lazy key-map chains enabling sort-skip
   and merge joins; documented in v7 TypeCheck comments.

## 2. Designed (full spec drafts exist in blade_extensions_v10.md)

### 2.1 Automatic differentiation (ext §2.2)

**v1 LANDED in v7 (AD arc, 2026-07-12)**: reverse-mode `grad` as an
AST-level source transform over the differentiable core (lets, additive
accumulation, element construction, additive `reduce(..., (+))` folds and
rank-1 additive recursive arrays, intrinsics, gather-style reads, call
inlining), with same-direction adjoint loops (exact for the accumulation
subset, discipline-checked), mut out-buffer ABI, and the end-to-end
equivariant training example pinned to the ml/ oracle. See the
module doc §11 for the ABI and subset; corpus `ad/` + `ml-e2e/`.

Still open from the original design (ext §2.2, file itself lost — summary
here is the surviving spec):

- Forward mode (`DComp`, tangent propagation).
- AD-through-COMBINATOR rules. Per the author's recollection of the lost
  ext §2.2 draft: differentiation was worked through MOST internal
  functions/combinators (`<@>`, `@>>`, `>>@`, `<&>`, `<*>`; reversed
  composition order in reverse mode), with `<|>` and `<|:>` the NOTABLE
  EXCEPTIONS (the surviving summary's "branch recording" / "used-side
  routing" were the sketched-but-not-settled approaches for them), and
  if/match a further complexity class of the same kind (control flow
  recorded at runtime). v1 rejects combinators inside differentiated code;
  iteration inside `grad` is expressed with additive `reduce(..., (+))` folds
  and rank-1 additive recursive arrays (there is no imperative for-in).
- Triangular tape/storage exploitation: tape size C(n+r-1, r) instead of
  n^r. The route in the v1 architecture is for adjoint code to ride the
  EXISTING symmetry system (generated source, ordinary inference), not
  AD-specific storage logic.
- Symmetric gradient accumulation (gradients flow to both/all positions of
  an identity group per canonical tuple).
- Jacobian symmetry theorem: Jacobians inherit output symmetry in the first
  r indices. NOTE: v10's "(r!)^d for product symmetry" corollary inherits
  the product-symmetry correction — the checked speedup story is r! per
  identity group (joint symmetry); see [proofs.md](proofs.md).
- Arity-polymorphic AD via recursive destructuring.
- v1 subset lifts: wrt-lists, if/match in differentiated code, taping for
  nonlinear loop-carried recurrences, differentiating through mut-param
  callees, non-literal-extent gradient locals.
- Stencil interaction (symmetric scatter-add), decomposition interaction
  (gradient halos), PyTorch/JAX bindings.

### 2.2 Trees and graphs (ext §2.3–2.4)

Module doc: [features/graphs-trees.md](features/graphs-trees.md). Settled:
trees as generalized arrays (shape = index type, flat storage + bijection,
O(path) access, currying as partial paths); graphs via `Trace<N>` accumulate-
and-collapse semantics; algorithm-as-index-type table; `Idx → TreeIdx → DAGIdx
→ Trace` hierarchy. Open: dynamic trees, symmetric-tree storage theory,
tree×array hybrids, AD through trees, distributed trees, `DAGIdx` semantics,
`Stream<T>`/termination policies for non-deterministic iteration
(`for i in Trace<G> take k`, random walks, convergence loops).

## 3. Research directions (ext §3, roadmap)

1. **Stencils and halo exchange**: chunk sizing from `AlignedExpr` metadata,
   halo inference, staggered-grid topologies, triangular-boundary halos.
2. **Distributed tensor contractions**: communication patterns for
   tensor-vector multiplication.
3. **ML framework interop**: PyTorch/JAX tensor bridging, equivariant layers
   as framework modules, automatic equivariant kernel derivation.
4. **Scheduling/runtime**: optimal block sizes, cache-oblivious triangular
   algorithms, dynamic load balancing, fault tolerance/checkpointing.

## 4. Proof-tower open items (Coq ROADMAP, v16)

1. **Surface-calculus progress/preservation** — type soundness for a Blade-core
   surface calculus elaborating into the intrinsically-typed index layer
   (where bounds safety is definitional). Deliberately sequenced AFTER the
   compiler rewrite settles the surface language, or it gets proved twice.
   Estimate 500+ lines.
2. **General-r verified offset arithmetic** — BladeSafety covers rank 2
   (triangular offsets total/correct/in-range/injective, closed form checked);
   general r = nested hockey-stick sums.
3. **Cauchy split beyond r = 2** — r ≥ 3 has mixed Schur components; genuinely
   open whether per-dimension product-structured storage can be recovered.
4. **k-slot structure** — gated on a mixed-source full-support property; would
   force a third composition operator and a triangle of interchange laws
   (slot_interchange is the checked 2-slot instance).
5. **Typed-path combinator laws** — 12.1/12.2 at the surface-calculus level
   (materialized-value level is fully checked).
6. **Plan Hom-sets / adjunction proper** — V ⊣ P beyond the checked strict
   monoidal evaluation homomorphism.
7. **Consolidation** (no action yet): lj2/lj3 and diagonal_swap_is_symmetry are
   subsumed by general theorems; prune only if the tower is published standalone.

## 4b. Arc-1 follow-ups (corrected symmetry)

1. **Fusion over non-dense S-blocks.** Joint-level fusion currently covers
   plain-dense S-blocks only. A comm-repeated argument whose S-block contains
   symmetric records (SymIdx inputs — the sound joint form is the wreath
   product, not S_{2r}), ragged/dep records, or compound-plus-trailing shapes
   conservatively gets NO cross-argument grouping (correct, unoptimized).
   Lifting these needs unrank decode through the record's own bijection.
2. **Decode strength reduction.** The fused level decodes per-dim coordinates
   with div/mod per iteration; the rewrite should strength-reduce to carried
   counters.
3. **r = 2 Cauchy-split storage mode** (per-dim structured storage via the two
   signed components) as an opt-in layout for distribution/file-format needs.
4. **Reynolds self-licensing** (arc 2 design question). Today symmetric
   storage under `reynolds` requires an explicit `comm(...)` on the wrapped
   kernel (comm = iteration-license declaration; reynolds = summed terms;
   reynolds/004 vs 001 pin the two behaviors). Since K = Σ g∘σ is commutative
   BY CONSTRUCTION, the license is derivable — `reynolds` could synthesize the
   comm group over its wrapped positions and compact automatically whenever
   identity holds. Safe against the current corpus (no reynolds-over-identical
   test omits comm except the deliberately-dense probes); decide before the
   rewrite freezes surface semantics.
5. **Index-level (per-dimension) Reynolds operator** (arc 2 finding). The
   proof tower's R — summing over index swaps rather than value-argument
   permutations — genuinely carries per-dimension product symmetry with
   lossless canonical access (BladeCore `reynolds_full_product_symmetry`,
   `reynolds_canonical_access`). It is the ONE sound route to per-dim product
   structure at general rank, at (r!)^d kernel-evaluation cost. The surface
   `reynolds(g)` is the value-level wrapper and cannot express it. A surface
   form (e.g. `reynolds_indexed(g)`) is a candidate feature; spec first.

## 5. Speculative (recorded, no commitment)

1. **Symmetric trees** — commutative children, canonical-path storage; the
   tree analog of triangular storage is unexplored (impact rated modest).
2. **Non-deterministic iteration as a first-class effect** — `Stream<T>`,
   stochastic termination, message-passing-until-convergence as language
   constructs rather than library patterns.
3. **Event-loop/state-machine applications of `Trace`** — cycle collapse as
   stuck-state/oscillation detection.
