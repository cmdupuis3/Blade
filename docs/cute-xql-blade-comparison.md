# Blade × CuTe × xarray-sql: a three-way comparison

*2026-09-29. Synthesized from three independent research passes, one per codebase:
Blade at master 81cdd61b, CuTe/pycute at `NVlabs/CuTe` main, and xarray-sql at
`xqlsystems/xarray-sql` main. The file:line citations come from those passes and have not
been re-verified, so spot-check them before quoting them upstream.*

## 1. Thesis

All three systems handle the same object: a keyed function **R : K → V**, which is the
relation `R(K₁…Kₙ, V)` with the functional dependency K → V. Each system keeps a different
part of that object and discards the rest.

| | What K is | Keeps | Discards |
|---|---|---|---|
| **xql** | primary-key columns (dimension coordinates) | values, arbitrary predicates, value joins, engine ecosystem (DataFusion, DuckDB, Polars) | the physical layout: the engine sees rows plus per-chunk min/max |
| **CuTe** | a box of integers Π[0, sₖ) | an algebra of coordinate → offset maps (composition, divide, product, complement) with explicit divisibility preconditions | names, bounds, non-box domains, non-affine storage, value-keyed coordinates |
| **Blade** | a typed index domain with provenance | domain + storage bijection + lex enumeration, non-box domains, symmetry quotients, **licensing laws** with the core proved in Coq | free strides and swizzles (dense storage is row-major only), strong value-keyed joins |

The xql author has proposed a parallel between CuTe's hierarchical layouts and
primary-key indexes ([NVlabs/CuTe#4](https://github.com/NVlabs/CuTe/issues/4)). That
parallel is correct for the **storage** half. It misses the **licensing** half. Relational
algebra operates on values and is total. Layout algebra operates on positions and is
partial. Replacing a relational operator with a layout operator is sound only with three
certificates:

1. **Value equality ⇔ position equality.** This needs provenance plus an injective,
   monotone coordinate map.
2. **A partiality guard.** Divisibility for divides, and clipping at the bounds for joins.
3. **Laws for when non-injective maps and quotients are legal.** Reductions need fold
   commutativity and associativity. Symmetric storage needs H ∩ Stab.

Blade's type system is a theory of exactly those licenses.

## 2. The three systems

### 2.1 CuTe (pycute)

**Core.** A layout is a congruent pair (Shape, Stride) of hierarchical tuples, evaluated as
`L(c) = Σ cₖ·dₖ` over the colexicographic coordinate ↔ integer bijection (`layout.py:123`,
`shape.py:231-301`). Stride leaves live in integer modules: `int` (Z), `ArithTuple`/`E(i)`
(coordinate-valued, Z^S), or `F2` (XOR), which is how swizzles enter the algebra.

**Operations.**
- `coalesce`.
- Composition (`layout.py:211-278`), with stride-divisibility and shape-divisibility
  admissibility checks.
- `complement` (`layout.py:362-413`). The weak form gives ordered, disjoint output. The
  strong form (a bijection onto a contiguous range) holds only for divisible chains.
- `logical_divide` A ⊘ B = A ∘ (B, B*), and `logical_product` A ⊗ B = (A, A* ∘ B).
- Zipped, blocked and raked variants.
- Left and right inverses.

**Laws.** CuTe forms a *partial* category up to coalesce-equivalence. Composition is
undefined on arithmetic failure, not only on a type mismatch. Associativity is checked on
12 triples. `make_layout` concatenation is a monoid (a direct sum), and `layout_add` is a
partial commutative monoid.

**Gaps and defects found.**
- **No names.** Modes of equal size are indistinguishable (`shape.py:128`), and einsum
  unifies labels by extent alone (`einsum.py:94-97`).
- **No bounds.** Evaluation extrapolates past the last mode, so `composition((3,4):(1,10),
  24:1)` silently leaves |A|. `in_bounds` is opt-in.
- **Box domains only.** Triangular or symmetric packed storage, sparse or hashed layouts,
  ragged extents and label coordinates are all inexpressible. The escape hatch is the
  `Accessor` (`tensor.py:67`), which sits entirely outside the algebra.
- **`nullspace` is incomplete** (`layout.py:416-427`). It returns only the product of the
  stride-0 modes. `(2,2):(1,-1)` maps (1,1)→0, yet `nullspace` returns `1:0`. This is a
  concrete upstream bug.
- **`left_inverse` accepts stride-0 modes** and so returns only a generalized inverse. It
  rejects coprime-but-injective layouts.
- **Swizzles are not composable layouts** in pycute. They enter only as `F2` strides, with
  carve-outs the docs call "not exhaustively tested".

**einsum and einfold.**
- `einsum._classify` partitions labels into M (A, C), N (B, C), K (A, B) and L (A, B, C).
  `_fold` regroups the modes, so the algebra's contribution is a schema rewrite plus
  colexicographic linearization of composite keys.
- einsum refuses diagonals and traces, marginal sums (a label in only one operand), and
  broadcasting.
- einfold is a generalized transpose. Dropping a mode fixes that coordinate at 0 (σ_{j=0},
  not projection). Duplicating a mode gives a coordinate *sum*.
- im2col composes one spatial sublayout twice, giving `x = u·z + s·t + origin`, which is
  the window × tap join.

**Read relationally.** A layout is a table R_L(c…, off) with c → off. Injectivity makes
off a key, and a stride-0 mode is a cross join. Composition is
`π(R_B ⋈_{off_B = idx_A} R_A)`. That is exact over Z(B), but it is refused when the result
is not affine, and it extrapolates rather than inner-joining. A non-injective *output*
layout under `+=` is `GROUP BY … SUM` over the fiber, which `alg/copy.py:59-63` currently
reports as a write-after-write error.

### 2.2 xarray-sql (xql)

**Pivot.** `iter_record_batches` (`df.py:425-549`) ravels each data variable, which is
zero-copy for C-contiguous arrays. It materializes **every dimension coordinate as a full
column**, via `np.repeat`/`np.tile` or strided index arithmetic `(i // strideₖ) % shapeₖ`.
Only dimension coordinates become columns (`df.py:602`), so curvilinear lat/lon cannot be
queried. Variables are grouped by their dimension tuple into one table per group.

**Chunks and partitions.** `block_slices` takes the Cartesian product of per-dimension
chunk bounds. `read_xarray_table` (`reader.py:194-340`) yields `(factory, {dim: (min, max,
dtype)}, num_rows)` per chunk.

**Native provider (`src/lib.rs`).**
- `PrunableStreamingTable` prunes partitions against per-chunk min/max (`lib.rs:377-438`).
- Dimension filters are pushed down as **Inexact** (`lib.rs:879-884`), so every row is
  re-filtered.
- `XarrayScanExec` reports exact row counts and dimension min/max, but drops per-partition
  bounds (`lib.rs:1263-1270`). It declares no output ordering or partitioning.

**Engine-neutral provider.** `XarrayPushdownDataset` (`backends/pyarrow.py`) uses
per-dimension shadow datasets, bucketed two-level at 1024 fragments per level, for pruning.
It also has an arithmetic `count_rows`.

**Round-trip.**
- `_c_order_grid` (`ds.py:604-628`) rediscovers C order by *comparing values*, then either
  reshapes or scatters.
- `_CoordLookup` (`ds.py:276-352`) tries affine, then hash, then sorted lookup.
- Dimensions are inferred by **matching column names** (`ds.py:1183-1224`).
- The round-trip is lossy for encoding keys, sparse integer results (uninitialized cells),
  duplicate coordinates (last write wins), and diagonal results (densified).

**Costs, in the project's own words.** On the case-05 benchmark, "The gap is compute, and it
is about 30×" (geospatial.md:513), and "The paradigm itself is the price, paid where the
relational algebra runs." NDVI runs about 10× slower. The pipeline is mostly
single-threaded. Filters on data variables always scan. Cross-dimension OR ranges read the
cross combinations. Function calls such as `date_part` never prune.

**Where SQL is natural:** dimension-range filters; GROUP BY for climatology and zonal means;
self-JOIN anomalies; computed-key joins (`valid = init + lead`); raster × vector range
joins; and sparse regridding as a weight-table JOIN plus GROUP BY.

**Where SQL is awkward:** stencils and rolling windows (no `OVER`/`LAG` in the tests);
contractions, since the mnist-demo `benchmarks/nn.py` does matmul as `JOIN … GROUP BY`; and
generating regridding weights.

**Layout awareness.**
- xql *knows* the chunk grid, exact counts, per-chunk bounds and C-order strides.
- It *does not* translate predicates into index ranges in the forward scan: a surviving
  chunk is `isel`'d whole and filtered row by row.
- There is no ordering declaration. [xql#249](https://github.com/xqlsystems/xarray-sql/issues/249)
  is open; its timings are 21 ms with ORDER BY versus 13 ms without.
- Layout is rediscovered from data in O(rows) instead of being carried as a plan property.

### 2.3 Blade

**Index-type contract** (formalism §3.2). Every index type has a domain, a cardinality, a
storage bijection onto [0, card), and an enumeration in offset order, which is lex order
(proved at the arrow level: `enum_offset_respects_lex`).

| Type | Domain | coord → offset | Affine (CuTe-expressible)? |
|---|---|---|---|
| `Idx<N>`, dense rank k | box | row-major | yes, but a strict subset of CuTe (no free strides) |
| `EnumIdx<S>` | category ordinals | ordinal | yes |
| `SymIdx<r,N>` | i₁≤…≤iᵣ | combinadic rank after canonicalization (`SimplexBlocksCore.fs:195-233`) | **no** (degree-r polynomial; only the innermost run is affine) |
| `AntisymIdx<r,N>` | i₁<…<iᵣ | same, strict; no diagonal stored; sign on odd parity | no |
| `HermitianIdx<N>` | as SymIdx | shares the SymIdx pool; conjugate on odd swap | no |
| `CompoundIdx<mask>` | tuples where the mask is true | position in a lex-sorted table + reverse hash | no (monotone) |
| `SparseIdx<keys>` | explicit keys | key order + hash | no |
| `RaggedIdx` | (i, j<len(i)) | CSR prefix sum + j | affine in j, table in i |
| `OrbIdx` | orbit representatives of an iterated wreath product | iterated-binomial rank/unrank | no |
| `Chunked<I,K>` | same as I | identity + segment table b_g = gK | yes (a regular logical divide); files level is piecewise affine |
| `halo<I,[o…]>` | a traversal transformer, not storage | out i reads in i+Start+o | yes (an affine access family; composes by Minkowski sum) |

**Provenance** (formalism §3.10). Iteration emits `i : Nat<LatIdx>`. A named tag mismatch
is an error (`TypeCheckInfer.fs:4862`), and an untagged integer gets only a BL4003 warning.
Named aliases mint identity, and anonymous occurrences get fresh identity. The result is
bounds safety by construction, the index-level analogue of physical units.

**Relational reading.**

| Blade construct | Relational operator |
|---|---|
| `A(i,j) ≡ A(i)(j)` | selection on a key prefix + projection |
| `zip` | positional equi-join (needs identity + equal extents, BL3016) |
| `method_for(A,B)` / `A [op] B` | cross product (`<*>` is a proved monoid) |
| `method_for(A,A)` with a `comm` kernel | self-join modulo S₂ |
| `<&!>` | several aggregates in one scan |
| `reduce(axes=n)` | γ (GROUP BY) over the leading keys |
| `reduce(method_for(A,B) <@> f, +)` | join-aggregate pushdown that never materializes \|A\|×\|B\| |

**SQL surface** (docs/features/sql.md):
- WHERE = `compound(A, mask(A, p))`: a selection that keeps the original coordinates.
- GROUP BY = `group_keys`/`group_by`, dispatched positionally, by EnumIdx, or by hash; it
  yields a ragged nest. `group_bucket` + rank unnests it.
- `segments(Chunked<I,K>)` is a fourth, structural grouping with identity permutation, and
  `ungroup ∘ group_by = id`.
- A foreign-key join is a gather, type-safe via element unit = index tag.
- Semijoin via `contains` is O(|A|·|B|). There is no sparse⋈sparse emitter.

**Constrained domains** (formalism §3.5; `StructIdxFence.domainPlanOf`,
`src/StructIdxFence.fs:288`).
- Linear conjuncts normalize to Σaᵢxᵢ + c ≤ 0. `abs` splits, `<` tightens, `==` becomes a
  pair. `!=`, `||`, `%` and non-linear forms are rejected.
- Difference constraints go into a difference-bound matrix (DBM), which is closed and then
  **Fourier–Motzkin projected innermost-first**.
- The output is a lex loop nest with bounds affine in the prefix, visiting exactly |D|
  cells with no dead prefixes.
- It is certified against brute force below a 100k-cell box (`StructIdxSpec.domainRoute`).
  Pins are `tests/corpus/index-types/260-267`.
- Storage in v1 is still hashed. The piecewise-affine offsets were designed but not built.

**Symmetry theory: the codomain quotient CuTe lacks.**
- **Access.** Access is x ↦ (σ, canon(x)) ↦ χ(σ)·pool[rank(canon(x))], with χ = 1
  (symmetric), sgn (antisymmetric) or conjugate (Hermitian).
- **Lowering law** (§11.2). lower(H) = H ∩ Stab(A₁…Aₙ). This is sound
  (`output_symmetry_soundness`) and **exact as an iff** (`license_exactness`).
  - Distinct arrays over the same index space license nothing
    (`shared_units_insufficient`).
  - Input symmetry is consumed, not propagated.
- **Joint symmetry.** d dimensions in one identity group give a speedup of r!, not (r!)^d.
  `counting_general_C` gives ∏C(nⱼ+r−1,r) < C(∏nⱼ+r−1,r), so no lossless per-dimension
  product layout exists.
- **Distinct groups.** Their savings multiply, laid out by a mixed-radix rank
  (`mixed_radix_bijection`).
- **Storage dichotomy.** Minimal per-mode-canonical width is 2 at r = 2 (Cauchy split) and
  r! at r = 3. Both are proved in Coq; general r is prose.
- **Wreath** (`BladeWreath`). Invariance under S_r ≀ S₂ is proved at general r, and
  exactness at r ≤ 3. The degeneracy is kernel-relative, so licensing must come from
  declaration, never from inspecting values.
- **Layout group** (`BladeLayout`). B_d = Z₂^d ⋊ S_d. |Hom(B_d, Z₂)| = 4, and exactly two
  descend to forward layouts (SymIdx, AntisymIdx). Direction is a free gauge iff the fold
  is commutative and associative. The canonical form minimizes Kendall-tau stride cost.
- **Optimality** (`BladeOptimal`). Any program correct for every S_r-invariant kernel needs
  C(n+r−1,r) kernel calls and cells. Canonical enumeration attains that. The cost counted
  is calls and cells, not time.

## 3. Synergy matrix

| From → To | Contribution |
|---|---|
| **CuTe → xql** | A physical rewrite vocabulary (composition, divide, complement, coalesce). The divisibility preconditions serve as planner rewrite guards. `_classify` (M/N/K/L) recognizes `JOIN … GROUP BY SUM(a·b)` as a GEMM. A route to CUTLASS kernels. |
| **xql → CuTe** | Relational semantics for the partial and non-injective cases: inner-join clipping instead of extrapolation, and non-injective output as GROUP BY instead of a write-after-write error. A value-keyed accessor layer for labels and timestamps. Real climate workloads. |
| **CuTe → Blade** | Blade's dense layout is a strict subset of CuTe's. Free strides, F2 swizzles and the tiler algebra are what Blade's CUDA path and blocked compute should target. `Chunked<I,K>` is already a logical divide, and `halo` is im2col's traversal + dilation composition. |
| **xql → Blade** | A mature pruning and statistics layer (shadow datasets, arithmetic `count(*)`). A hash-join fallback for everything Blade cannot license structurally. "Arrays compute the weights, SQL applies them" makes Blade a candidate kernel backend for the array half. |
| **Blade → CuTe** | Provenance tags on modes. Non-box domains via predicate compilation. Group-quotient layouts (a combinadic stride leaf). Reduction legality for non-injective outputs. |
| **Blade → xql** | The license table in §4: when a relational operator may be compiled to a layout operator. Typed index provenance in place of name matching. The proved symmetric self-join rule. |

The architecture this points to is a **tiered planner**. First try a provenance-licensed
layout rewrite (zip, divide, composition, or a symmetric quotient). Only if no license
applies, fall back to hashing.

## 4. What Blade contributes to relational ↔ layout algebra

### 4.1 The unifying object

The unifying object is Blade's **index type**: (nominal identity, domain D ⊆ Zʳ, storage
bijection D → [0,|D|), enumeration order, coordinate map D → labels).

- A CuTe layout is only the storage map, restricted to D = box and affine.
- A SQL primary key is only the label set.
- xarray already declares all five components (dims, coords, chunks). xql currently
  forgets them at the relational boundary.

### 4.2 License table

| Relational | CuTe | Blade | License that makes the rewrite sound |
|---|---|---|---|
| σ on dims (box) | compose with a sub-box | partial application | monotone coordinate map ⇒ exact index interval ⇒ Exact pushdown + `isel` sub-slice |
| σ with linear cross-dim terms (band, `i ≤ j`, `valid − init ≤ w`) | **not expressible** | constrained domain (DBM + Fourier–Motzkin → lex nest) | linear conjuncts only; `OR` refused |
| equi-join on dims | composition / zip | `zip` | same index identity + equal extents (BL3016), **not the same column name** |
| offset join (`valid = init + lead`) | composition with an offset | shifted / halo access | affine coordinate maps; inner-join *clipping* (CuTe extrapolates) |
| cross join | product / stride-0 | `method_for(A,B)` | none needed |
| symmetric self-join `a.k ≤ b.k` | **none** | `comm` + same array → `SymIdx` | **H ∩ Stab**: Coq iff (`license_exactness`) |
| GROUP BY dim subset | divide + reduce modes | `reduce(axes=n)` | fold commutative + associative before scan order may be reordered (`BladeLayout`) |
| GROUP BY calendar bucket (`date_part('hour')` on hourly) | `logical_divide` by 24 | `Chunked<I,K>` / `segments` | divisibility + origin alignment (CuTe's own precondition) |
| GROUP BY value | — | `group_by` (CSR / hash) | fallback tier |
| JOIN + GROUP BY SUM(a·b) | einsum M/N/K/L | `reduce(method_for(A,B) <@> f, +)`, deferred | sum-of-products + dense + same provenance; SQL also covers the diagonals and marginals CuTe's einsum refuses |
| ORDER BY dims | enumeration | lex = offset order (Coq) | **CuTe is colexicographic and xarray is C order**, so mode order must be reversed across the bridge |
| UNION ALL | — | `join` (mints a fresh axis) | a fresh index identity; `ungroup` is the identity-preserving inverse |

### 4.3 Transferable results, in order of leverage

1. **Provenance is the join license.** xql infers dimensions by column name
   (`ds.py:1183`). Blade's `Nat<LatIdx> ≠ Nat<LonIdx>` is the static certificate that turns
   a join into a zip. It also closes the silent-misalignment hole when two grids have equal
   extents. *Status: implemented and corpus-pinned. Not formalized in Coq (surface-calculus
   preservation is prose); bounds safety is proved only at r = 2 (`bidx_access_safe`).*

2. **Licensing comes from declarations, never from inspecting values.** The wreath
   degeneracy is kernel-relative (`additive_locus_is_kernel_relative`), so no inspection of
   values can license a fast path that holds for all data. For xql this means: derive the
   layout at registration from xarray metadata, not from rows. `_c_order_grid` is the
   anti-pattern. This also resolves the tension CuTe#4 raises, that "hints are not a
   programming model": in a *data model*, the declarations already exist. *Status: Coq.*

3. **Quotient layouts extend CuTe beyond stride-0.**
   - A layout becomes a pair (canon: X → X/G × G, rank: X/G → [0,|X/G|)) together with a
     value character χ ∈ Hom(G, Aut T).
   - CuTe's stride-0 is a quotient by a *projection*. Blade's is a quotient by a
     *permutation action*.
   - `counting_general_C` shows symmetric tiles over composite keys cannot be products of
     per-mode triangles.
   - The dichotomy bounds the minimal width: 2 at r = 2, r! at r = 3.
   - In CuTe this is a new stride-leaf type (combinatorial-number offsets), slotting in the
     way `F2` does. In xql it is the proved rule for halving covariance or pairwise-distance
     self-joins.
   - *Status: bijection and cardinality proved in Coq at general r; dichotomy proved at
     r = 2 and r = 3; χ = sgn and conj corpus-pinned.*

4. **Predicate-to-domain compilation turns WHERE into a layout.**
   - Invert dimension predicates through the affine coordinate map into index-space linear
     constraints.
   - Box constraints become an `isel` slice, so pushdown becomes Exact and the row filter
     goes away.
   - Cross-dimension linear constraints become the Blade loop nest.
   - *Status: implemented and pinned (260-267), certified against brute force. The FM
     projection is unproved, and v1 storage is still hashed.*

5. **Mixed-radix product of simplices.** Over distinct identity groups, the composed
   rank/unrank is a bijection with cardinality ∏C(·). In CuTe terms it is a hierarchical
   "layout of simplex modes". *Status: Coq (`mixed_radix_bijection`).*

6. **Layout characters.** |Hom(B_d, Z₂)| = 4, exactly 2 of which descend to forward
   layouts. Direction is a free gauge iff the fold is commutative and associative. This is
   a closed vocabulary for when a planner may permute or flip scan order. *Status: Coq
   (`BladeLayout`).*

7. **Orbit work bound.** Any program correct for every G-invariant kernel needs ω(G,X)
   calls and cells, and for pipelines W = Σ_v ω(G_v, X_v). This is a lower-bound cost model
   for a planner. *Status: Coq for H = S_R and Young subgroups; general H is prose.*

8. **Identity-preserving segmentation.** `Chunked<I,K>` is a structural `group_by` with an
   identity permutation, and `ungroup ∘ group_by = id` on I. Concatenation instead mints a
   fresh key. A licensed per-segment fold is bit-reproducible at a fixed segmentation. For
   xql this is range partitioning with no hash, matching its chunk grid exactly. *Status:
   implemented and pinned; the reproducibility claim is an argument, not a proof.*

9. **Domain/symmetry compatibility.** Compact storage of a D-supported array is sound iff D
   is G-invariant, so a constrained domain never licenses symmetry by itself. *Status:
   designed (structural/06 §2.4), not mechanized.*

## 5. Recommendations

**xql**
1. **Add a layout descriptor to the Arrow schema metadata** (`df.py:580-642`).
   - Per dimension: grid identity, extent, stride, chunk tile, and an affine (origin, step)
     or monotone flag.
   - It is engine-neutral and serves as the lingua franca for all three systems.
   - It replaces name-based dimension inference (`ds.py:1183-1224`).
2. **Declare ordering and partitioning** in `XarrayScanExec.properties`
   (`lib.rs:1231-1235`), derived from the descriptor. This closes #249 and enables
   sort-merge joins and streaming aggregation.
3. **Invert predicates into index intervals** in pruning (`lib.rs:377-620`,
   `pyarrow.py:114-222`), intersect them with the chunk tiler (logical divide), and `isel`
   a sub-slice (`reader.py:289`). Report pushdown as Exact where proven.
4. **Add a DataFusion physical optimizer rule.** Rewrite an equi-join or offset join on
   dimension columns between provenance-compatible scans into a zip or composition exec.
   Rewrite GROUP BY on dimension subsets or affine buckets as divide-then-reduce. Rewrite
   `JOIN + GROUP BY SUM(a·b)` into einsum or GEMM. This targets the 30× gap in case 05.
5. **Emit coordinate columns run-end or dictionary encoded**, or skip them for
   layout-aware consumers. They are closed-form repeat/tile patterns.
6. **Propagate the result layout through the round-trip** instead of rediscovering it
   (`ds.py:604-628`).

**CuTe (upstream)**
1. Fix `nullspace` for negative strides (`layout.py:416-427`). This is a bug; see §2.1.
2. Add bounded composition that clips or refuses B(i) ≥ |A|. This makes composition a
   typed inner join.
3. Add provenance tags on shape leaves, checked by `compatible`, `common_refinement` and
   einsum's extent check.
4. Add a combinatorial-number stride leaf for packed symmetric and triangular storage.
5. Support non-box domains: a predicate-constrained `coordinates` with a closed-form
   rank/unrank.
6. Accept diagonal and marginal einsum. A repeated label becomes an `ArithTuple(1,1)`
   composition.

**Blade**
1. Emit and consume the shared layout descriptor in the Zarr, NetCDF and Icechunk
   providers, and in `blade plan --json`.
2. Target CuTe tilers from the CUDA path and blocked compute rather than hand-rolled tiling.
3. Build the designed piecewise-affine offsets for constrained domains (structural/06
   §3.3). Today storage is hashed.
4. Fix a doc drift: formalism.md §3.4 still says Hermitian stores n², but it shares the
   SymIdx pool (`src/IRStorage.fs:348-354`).

**Shared**
- Write a short "typed layout" spec that states the §4.2 license table, with a citation for
  each row marked *proved*, *pinned* or *designed*.

## 6. Sources

- Blade: `docs/formalism.md` (§3, §4, §6-§12), `docs/features/sql.md`, `docs/proofs.md`,
  `docs/plans/structural/02,06,07`, `src/StructIdxFence.fs`, `src/StructIdxSpec.fs`,
  `src/cpp/index_types.h`, `proofs/`.
- CuTe: `docs/02-06`, `pycute/{layout,algebra,shape,stride,swizzle,tensor,accessor}.py`,
  `examples/{einsum,einfold,im2col}.py`, `test/test_composition.py`.
- xql: `docs/{performance,limitations,geospatial,engines}.md`,
  `xarray_sql/{df,ds,reader,lazyscan}.py`, `xarray_sql/backends/pyarrow.py`, `src/lib.rs`.
- External: [NVlabs/CuTe#4](https://github.com/NVlabs/CuTe/issues/4) ("Parallel between
  CuTe's hierarchical layout and primary key indexes"),
  [xqlsystems/xarray-sql#249](https://github.com/xqlsystems/xarray-sql/issues/249)
  (declare scan ordering), and `benchmarks/nn.py` on the `claude/xarray-sql-mnist-demo`
  branch (an MLP in SQL).
