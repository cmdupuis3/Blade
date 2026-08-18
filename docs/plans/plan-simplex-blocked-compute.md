# Simplex-blocked compute — running symmetric structure on rectangular backends

**Status: SPEC (design only; nothing implemented on the compute side).** Written
2026-08-17. This is the spec for the "triangular quadtree tiling" follow-on the
retired perf plan left open, prompted by one observation: **the simplex-blocks
decomposition already landed for Zarr storage is also an iteration and layout
strategy for computation.** Decompose a symmetric domain into tile blocks; every
block whose tiles are all distinct is a *dense rectangular brick* with no
symmetry constraint; recurse only the on-diagonal residue. At modest depth, all
but a vanishing fraction of a symmetric computation is plain dense rectangular
work — which is exactly the shape BLAS, the LLVM vectorizer, `linalg`, and
`cuda_tile` want. This is the constructive bridge past the "non-affine ⇒
refuse" verdicts in both backend plans (plan-mlir-backend.md §5,
plan-llvm-backend.md T1.1): it does not make the simplex affine; it **covers**
the simplex with affine pieces and confines the non-affine part to the residue.

## 1. The observation, rank 2

Take the upper-triangle domain Δ₂(n) = {(i,j) : 0 ≤ i ≤ j < n} and split both
axes at the midpoint. Three cells result: two on-diagonal triangles (each a
half-size Δ₂) and one off-diagonal cell {i < m ≤ j} — which is a **full dense
square**: for i and j in disjoint ordered tiles, i < j holds automatically, so
*every* point is canonical. No canonicalization, no mask, no guard — the
constraint has been discharged by the block structure itself. Recurse the two
triangles; the squares accumulate.

Two views of the same recursion: the quadtree view (each triangle → four
congruent sub-triangles, two on-diagonal + two off-diagonal) is what the
recursive-halving *path order* enumerates; the compute view **coarsens the two
off-diagonal triangles into one square**, because rectangular backends want
boxes. Both views share leaf structure — the T = 2^k identity already verified
in the storage plan.

Residue arithmetic: with T tiles at one level, the on-diagonal triangles hold
1/T of the cells (T=4 → 75% dense, T=8 → 87.5%, T=16 → 93.75%); recursive
halving to depth d leaves 2^{-d}. At full recursion the residue is exactly the
diagonal cells i=j — lower order. For **antisym there is no diagonal at all**:
full recursion covers 100% of strict cells with dense bricks (repeated-tile
blocks at B=1 are empty — the rule the storage code already implements).

## 2. The landed math (reuse, don't re-derive)

`module SimplexBlocks` (src/providers/ZarrProvider.fs:133-232), implemented and
gated for the Zarr provider (`blade test zarr` 183/0; plan:
src/providers/ZarrSimplexBlocksPlan.md; format: src/providers/ZarrTriangularSpec.md):

- **Block grid = SymIdx<r, T>** — tile multisets (t₁ ≤ … ≤ t_r) enumerate the
  blocks; C(T+r-1, r) of them; ranking/unranking reuses the combinadics
  (`rankOfCoords`/`unrankToCoords`, :136-169).
- **Each block is a product of smaller simplices** (`blockCellCount`, :178-184):
  group tiles by multiplicity; a tile of width w appearing m times contributes
  C(w+m-1, m) (sym) or C(w, m) (antisym — **zero** when m > w, the empty
  diagonal blocks). All-distinct blocks are dense boxes of B^r cells.
- **Intra-block iteration is branch-free** (`enumBlockCells`, :193-208):
  i_k ∈ [max(tile_k·B, i_{k-1}+strict), min((tile_k+1)·B, n)) — uniform bounds,
  no per-shape codegen.
- **Recursive halving = T = 2^k** with the mixed-radix DFS path order
  (`pathMultisets`/`pathRows`, :213-232) giving subtree-contiguous ranges.
- Measured padding for the *storage* row format: dominated by last-tile
  raggedness, 8.9–41.9% (prefer B | n, T ≳ 10) — relevant to §5's S1 only.

The module is provider-local today. Compute reuse lifts it to a small shared
file placed before its consumers in Blade.fsproj (or, minimally, twins the
identities at the consumer with a property-test pin against the provider's —
the differential-twin discipline either way).

## 3. Generalization: rank r, comm groups, signs

**The unit of the design is the iteration domain, not the array.** Any
comm-group-licensed triangular iteration — symmetric storage traversal,
same-array commuting positions in a kernel, symmetric-output method_for — ranges
over a simplex, and the decomposition applies uniformly. Storage participation
is optional (§5).

For rank r with multiplicity pattern λ (how many times each tile repeats), a
block is ∏ᵢ Δ_{λᵢ}(B): dense along every multiplicity-1 tile, simplex-shaped
along repeated tiles. Three consequences:

1. **Fully dense bricks** are the all-distinct blocks, C(T,r) of them; their
   cell fraction is r!·C(T,r)/T^r ≈ ∏_{k=1}^{r-1}(1 − k/T):

   | | T=4 | T=8 | T=16 | T=32 |
   |---|---|---|---|---|
   | r=2 | 75% | 87.5% | 93.8% | 96.9% |
   | r=3 | 37.5% | 65.6% | 82.0% | 90.8% |
   | r=4 | 9.4% | 41.0% | 66.6% | 82.3% |

   Note T ≥ r is required for *any* dense brick to exist.
2. **Mixed blocks are prisms** — e.g. a rank-3 block (t,t,u) is Δ₂(B) × [B]:
   dense along u's axis, triangular along the repeated pair. Recursion applies
   *per repeated factor*, so mixed blocks progressively shed their constraint
   too; the higher-r fractions above are the depth-1 floor, not the ceiling.
3. **Antisym** uses strict factors and skips empty diagonal blocks (already in
   `blockCellCount`). Sign handling: canonical cells carry +; only *mirror*
   consumption (§4b) applies a character, constant per brick-role. **Hermitian
   stays reserved** (constraint-coupled cells — same status as the storage
   spec), and **OrbIdx depth ≥ 2 is out of scope** for the same reason
   packed-blocks is undefined for orbit heads: a wreath pool has no tile
   multiset (ZarrTriangularSpec.md, version-2 rules).

## 4. Two consumption modes

**(a) Canonical-domain iteration** — each canonical cell exactly once: maps
over symmetric arrays, folds over the canonical domain, symmetric-output
kernels. Bricks *partition* the canonical set; brick order is free for maps
(independent writes to distinct cells), license-gated for folds (§6).

**(b) Full-domain semantics via mirror expansion** — contractions that consume
both (i,j) and (j,i): a dense brick (t₁ < t₂) serves both roles, once as-is and
once transposed (with the antisym character on the mirrored role). This is the
classic blocked-symmetric structure: symmetric matvec = per-brick GEMV +
GEMVᵀ into two output ranges; symmetric-output contraction (gram) = off-diagonal
output bricks are plain GEMMs, diagonal output bricks are half-size SYRKs —
recursion. The license is the same comm group that licensed triangular
iteration in the first place; which mode applies is derivable from the existing
SymcomState/decompaction machinery and must ride on the plan record (§7).

## 5. Storage options (orthogonal to iteration; decide by measurement)

- **S0 — iteration-only over the existing packed pool.** No storage change.
  Within an off-diagonal brick, packed addressing is affine along the last
  axis (row base + j), non-affine only across rows — enough for CPU SIMD on
  the inner axis. Cheapest; entirely emission-side; where P1 starts.
- **S1 — brick-major in-memory layout** = the Zarr `packed-blocks` row format
  brought in-memory, which its plan explicitly anticipated ("the store layout
  is then the in-memory layout, and chunk I/O is a straight block copy").
  Dense bricks become contiguous, alignable, *fully affine* 2-D tiles —
  BLAS-viewable, `linalg`/`cuda_tile`-consumable. Padding cost is the measured
  8.9–41.9% raggedness figure; policy: prefer B | n, T ≳ 10, refuse past a
  threshold (the storage plan's guidance, reused). This is a *deduced layout*
  in the sense of Blade's layout ownership — no surface syntax.
- **S2 — per-call brick packing** (BLAS-style pack-and-compute) for library
  routes that want contiguous operands without committing the array's layout.

## 6. What it buys, per consumer — with the honesty the backend audit demanded

| consumer | today | with bricks |
|---|---|---|
| elementwise maps over sym arrays | flat-pool traversal, already optimal (2.0x banked, zero coordinate math) | **nothing — do not touch this path** |
| coordinate-bearing sym kernels + licensed folds | triangular nest, `schedule(dynamic)` (CodeGen.fs:6261), non-uniform bounds | uniform literal-B trip counts (the 1.77x axis, per brick), static scheduling of equal-size work units, cache blocking, vectorized inner axis |
| symmetric contractions | top-level syrk routing (LinAlgPatterns; packed pool proven == BLAS packed) | blocked GEMV/GEMM/SYRK structure at tile granularity; GPU-fit |
| MLIR / cuda_tile lanes | **whole-kernel refusal** (plan-mlir-backend.md §5: non-affine) | bricks are static-shape dense ops; refusal shrinks to the 2^{-d} residue, which stays on the host path — the standard refusal-with-fallback pattern, now per-residue instead of per-kernel |
| LLVM lane | T1.1 "backend-negative: non-affine defeats SCEV/vectorizer" (plan-llvm-backend.md) | bricks are affine; the verdict flips to backend-positive-except-residue |
| layout algebra (plan-mlir-backend.md §6) | SymIdx packing outside CuTe's multilinear algebra | brick-major is a *tree of affine tiles* — expressible; only the residue stays outside |
| parallel scheduling | `schedule(dynamic)` derived from triangular imbalance | uniform bricks → static schedule; FoldChunkPlan's outermost-rectangular-only restriction (IR.fs:2509-2511) generalizes to triangular domains |
| MPI / out-of-core | landed: block-scoped Zarr reads, BSP ownership, window reads | compute-side blocking aligns with the already-landed I/O blocking — one decomposition, storage and compute |

The interpreter is untouched throughout: blocking is an emission strategy (a
CodeGen-phase-local plan record, like `LoopNestCodeGen`/`FoldChunkPlan`), maps
produce identical results, and licensed folds follow the standing
licensed-path-is-not-the-tested-path policy.

## 7. Licensing and numeric policy

- **Maps**: distinct-cell writes; brick order free; no license needed.
- **Folds**: brick partials reorder the fold ⇒ requires `foldReorderLicensed`
  (the BL4016 family), exactly like OMP chunking. Deterministic form: fixed
  ascending-lex brick order, partials combined in block order — deterministic-
  but-reassociated, the K-lane philosophy at brick granularity. Unlicensed
  folds keep the serial canonical order (no blocking); the default build is
  unchanged. Licensed-path testing uses integer-valued f64 corpus + invariant
  checks, never a float tolerance in InterpDiff (plan-llvm-backend.md §6).
- **Mirror expansion** (§4b) is licensed by the kernel's comm groups; antisym
  mirrored roles carry the character. No new license kind is needed anywhere.

## 8. Compiler seams

1. **Lift `SimplexBlocks`** to a shared module (placement before its consumers;
   `EnableDefaultItems=false` — manual `<Compile>` entry), keeping the provider
   consuming the same code; extend with the per-brick facts compute needs:
   brick-role list for mirror mode, per-brick sign/character, dense-axis mask
   (which λ positions are multiplicity-1).
2. **`BrickPlan`** — a new plan record beside `FoldChunkPlan` on the loop-plan
   layer: block enumeration (closed-form, or materialized tile list for small
   T), consumption mode (§4), per-brick bounds (the `enumBlockCells` identity),
   combine order for folds, leaf policy. Leaf simplex blocks run **serial
   triangular** (they are small by construction; dense-with-mask wastes half of
   a tiny block and creates write hazards for canonical outputs).
3. **B/depth policy is derived, not user-tuned** ("the fastest way is the only
   way"): B a multiple of the vector width, brick working set targeted at a
   cache-level fraction, B | n preferred, T ≥ r required, refuse-to-block below
   profitability thresholds (the r=4/T=4 row of §3's table is a non-starter).
   An env override exists for measurement only, read per-call as a function.
4. **Backend handoff**: the same `BrickPlan` serializes as dense sub-nests
   (C++/LLVM lanes), `linalg` ops on brick views (MLIR lane, S1), or tile
   launches — the `BackendFacts.fs` pattern from the LLVM plan applies.

## 9. Phasing

| phase | deliverable | gate |
|---|---|---|
| **P0** | lift SimplexBlocks + extend with mirror/sign facts; property pins vs the provider module (Σ blockCells = C(n+r-1,r); partition exactness; mode derivation) | pure math, hermetic tests |
| **P1** | S0 iteration blocking for comm-licensed rank-2 folds and coordinate-bearing maps, behind a gate | **the measurement gate**: comoment/covariance flagship shapes plus one large-n case, non-power-of-two extents, 3 rounds/9 reps/medians, arms = current triangular `schedule(dynamic)` vs bricked-static; go/no-go = parity-or-better with ≥1 clear win. The 2.14x packed precedent is the standing warning — the mechanism differs here (bricks pay **zero** per-cell canonicalization, which is what the 2.14x paid), but that is a hypothesis until this gate runs |
| **P2** | contraction bricks via LinAlgPatterns: symmetric matvec GEMV pairs; gram/syrk brick recursion (leverages packed==BLAS-packed) | measured vs the existing top-level syrk route |
| **P3** | S1 brick-major deduced layout; unify with Zarr packed-blocks so provider I/O becomes straight block copies; alignment + padding policy | padding within threshold on flagship shapes; no elementwise regression |
| **P4** | backend consumption: MLIR/`cuda_tile`/LLVM lanes take bricks as dense ops with residue-refusal; revise plan-mlir-backend.md §5 and plan-llvm-backend.md T1.1 verdicts | a symmetric kernel runs on a rectangular backend end-to-end, residue on host, diff-gates green |
| **P5** | rank ≥ 3 prisms (per-factor recursion), adaptive depth (the storage plan's Phase 4 twin), MPI compute ownership aligned with the landed read ownership | demand-driven |

## 10. Risks

1. **Measure-first** (the 2.14x precedent). P1's gate is designed to kill the
   plan cheaply if brick overhead (block-loop bookkeeping, worse locality on
   the packed pool in S0) eats the uniformity win.
2. **Do not touch the flat elementwise path** — it is already optimal and the
   decomposition has nothing to offer it.
3. **Fold-order licensing** — blocking folds without `foldReorderLicensed`
   would silently reassociate; the gate structure in §7 is load-bearing.
4. **Small-n / high-r profitability** — §3's table; refuse-to-block is a
   feature, and the leaf-serial policy bounds the loss at exactly today's
   behavior.
5. **Padding (S1 only)** — measured up to 41.9% at bad tile choices; policy
   inherited from the storage plan.
6. **Scope discipline** — Hermitian and OrbIdx excluded (same as storage);
   ragged/sparse/compound never had simplex domains and are unaffected.
