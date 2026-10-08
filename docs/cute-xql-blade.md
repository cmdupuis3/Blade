# Blade, CuTe and xarray-sql: how they fit together, and what Blade adds to xql's layout-algebra question

All three systems work with the same object: a keyed function R: K → V, the relational `R(K₁…Kₙ, V)` with the dependency K → V. Each one keeps a different part of it and drops the rest.

| | What K is | What it keeps | What it drops |
|---|---|---|---|
| **xql** | Primary-key columns (the dimension coordinates) | Values, arbitrary predicates, joins by value, engine ecosystem | The whole physical layout. The engine sees rows plus per-chunk min/max. C order gets rediscovered from values (`ds.py:604`), coordinate columns are materialized at rows × 8 B × dims, and pushdown stays Inexact. |
| **CuTe** | A box of integers, Π[0, sₖ) | An algebra of coordinate → offset maps: composition, divide, product, complement, with explicit divisibility preconditions | Names (modes of equal size can't be told apart), bounds (composition extrapolates past \|A\|), non-box domains, non-affine storage, value-keyed coordinates |
| **Blade** | A typed index domain carrying provenance (which grid it belongs to) | Domain + storage bijection + lex enumeration; non-box domains; symmetry quotients; *licensing laws*; the core proved in Coq | Only row-major dense strides, no swizzles or free strides; weak value-keyed joins (semijoin costs O(\|A\|·\|B\|), no sparse⋈sparse join) |

## What each system can give the others

- **CuTe → xql:** the physical rewrite vocabulary.
  - Its divisibility preconditions can serve as the planner's rewrite guards.
  - `_classify` (M/N/K/L by which operands a label occurs in) is literally a rule for recognising a `JOIN … GROUP BY SUM(a·b)` as a GEMM.
  - It is a route to CUTLASS kernels.
- **xql → CuTe:**
  - Relational semantics for the partial and non-injective cases: an inner join should clip, not extrapolate, and an overlapping output under `+=` is a GROUP BY (CuTe currently flags it as a write-after-write error).
  - A value-keyed accessor layer for labels and timestamps.
  - Real climate workloads.
- **CuTe → Blade:**
  - Blade's dense layout is a strict subset of CuTe's.
  - Free strides, F2 swizzles and the tiler algebra (logical divide/product) are what Blade's CUDA path and blocked compute should target, instead of hand-rolled tiling.
  - `Chunked<I,K>` is already a regular logical divide, and `halo` is im2col's traversal-plus-dilation composition.
- **xql → Blade:**
  - A mature pruning and statistics layer (shadow datasets, arithmetic `count(*)`).
  - A hash-join fallback for everything Blade can't license structurally.
  - A natural split: "arrays compute the weights, SQL applies them" (geospatial.md) makes Blade a candidate kernel backend for the array half.
- **Blade → both:** the theory of *when* a structural fast path is sound. That is the core of the answer to xql's question below.

## What Blade's theory contributes to relational ↔ layout algebra

The xql author opened [NVlabs/CuTe#4](https://github.com/NVlabs/CuTe/issues/4) proposing that CuTe's hierarchical layouts parallel primary-key indexes. That parallel is right about the storage half and misses the licensing half.

Relational algebra works on values and is total. Layout algebra works on positions and is partial. Swapping a relational operator for a layout operator needs three certificates:

1. **Value equality ⇔ position equality.** This is provenance plus an injective, monotone coordinate map.
2. **A guard for partiality:** divisibility for divides, and clipping at the bounds for joins.
3. **Laws for when non-injective maps and quotients are legal**, i.e. when many coordinates may land on one output cell.

Blade's type system is a theory of exactly these licenses. Its unifying object is the **index type**: (nominal identity, a domain D ⊆ Zʳ, a storage bijection D → [0, |D|), an enumeration order, and a coordinate map from D to labels). A CuTe layout is only the storage map with D a box and the map affine. A SQL primary key is only the label set.

| Relational | CuTe | Blade | License that makes the rewrite sound |
|---|---|---|---|
| σ on dims (box) | compose with a sub-box | partial application | monotone coordinate map ⇒ exact index interval ⇒ Exact pushdown |
| σ with linear cross-dim terms (band, `i ≤ j`, `valid − init ≤ w`) | **not expressible** | constrained domain: difference-bound matrix + Fourier–Motzkin → lex loop nest, no dead prefixes | linear conjuncts only; `OR` is refused |
| equi-join on dims | composition / zip | `zip` | same index identity + equal extents (BL3016), **not the same column name** |
| offset join (`valid = init + lead`) | composition with an offset | shifted / halo access | affine maps; inner-join *clipping* (CuTe extrapolates) |
| symmetric self-join, `a.k ≤ b.k` | **none** | `comm` + same array → `SymIdx` | **H ∩ Stab**: Coq iff (`license_exactness`) |
| GROUP BY a dim subset | divide + reduce modes | `reduce(axes=n)` | fold must be commutative and associative before scan order may be reordered (`BladeLayout`) |
| GROUP BY a calendar bucket (`date_part` hour on hourly data) | `logical_divide` by 24 | `Chunked<I,K>` / `segments` | divisibility + origin alignment (CuTe's own precondition) |
| GROUP BY value | — | `group_by` via a compressed-sparse-row (CSR) partition or hash | fallback tier |
| JOIN + GROUP BY SUM(a·b) | einsum M/N/K/L | `reduce(method_for(A,B) <@> f, +)`, deferred, never materialized | sum-of-products + dense + same provenance. SQL also covers the diagonals and marginal sums that CuTe's einsum refuses |
| ORDER BY dims | enumeration | lex = offset order (Coq) | **CuTe is colexicographic; xarray is C order**, so mode order must be reversed across the bridge |

# Attempt at a Unified Theory

At its core, a theory covering all three would be **functorial data migration over structured index domains**. Arrays, relations and layouts are all functions on an index domain. Every operation any of the three systems performs is one of a few universal moves along maps between domains. Each system is that theory restricted to one class of maps, and "licensing" is the question of when a map given by *values* factors through a *structured* map.

## 1. Objects: index domains

An index domain is a finite set D equipped with four pieces of structure:

- **Nominal identity.** The category is *not* skeletal: two domains of equal size are different objects unless an explicit map relates them. This is Blade's provenance, `Nat<LatIdx> ≠ Nat<LonIdx>`.
- **A presentation** as a subset of ℤʳ, drawn from a ladder of classes:
  - box (CuTe);
  - polyhedral / Presburger (Blade's constrained domains);
  - dependent (Ragged);
  - tabulated (CompoundIdx, SparseIdx).
- **A group action** G ↷ D, possibly trivial. This is where symmetry lives.
- **An enumeration order**, which gives ORDER BY, fold direction and reproducibility.

## 2. Three maps out of every domain

(This bit seems like somewhat motivated reasoning)

```
        ℓ (labels)                v (data)
  L  ◄────────────  D  ────────────►  K   (a commutative semiring / monoid)
                    │
                    │ s (storage), factoring through D/G
                    ▼
                  Addr  (ℤ, ℤˢ, or F₂ⁿ)
```

Each system sees only one of these maps:

| System | Sees | Blind to |
|---|---|---|
| **SQL / xql** | the image of (ℓ, v) in L × K, i.e. rows. A table is a K-relation. | D and s. Structure must be rediscovered from values. |
| **CuTe** | s, for D a box and s quasi-affine in normal form. | ℓ, G, nominal identity. v lives off to the side in the Accessor. |
| **Blade** | D with its identity, G and domain class; v as a function (dimensional currying); a few kinds of s (row-major, combinadic). | Rich ℓ (labels are thin: units, EnumIdx) and rich s (no free strides or swizzles). |

## 3. Operations: four universal constructions

For a map of domains f : D → E there are two migrations:

- **Δ_f (pullback):** precomposition, `(Δ_f v)(d) = v(f d)`.
- **Σ_f (pushforward):** sum over fibers, `(Σ_f v)(e) = ⊕_{f d = e} v(d)`.

Add products and subobjects and the operator zoo collapses into one table:

| Construction | SQL | CuTe | Blade |
|---|---|---|---|
| subobject D′ ↪ D (pullback of a predicate) | WHERE | sub-box composition (boxes only) | `compound`/`mask`, constrained `range<Band>` |
| Δ along a domain map | projection by reindexing, a view | **composition**, einfold, slicing | partial application `A(i)`, transpose, halo |
| fiber product D ×_L E, with v_A ⊗ v_B | **JOIN** | composition with the graph of ℓ_B⁻¹∘ℓ_A | `zip` (when that map is the identity) |
| product D × E | CROSS JOIN | logical product, stride-0 | `method_for(A,B)` |
| Σ along a surjection | GROUP BY + aggregate, bag projection | logical divide + reduce; non-injective layout under `+=` (currently a write-after-write error) | `reduce(axes=n)`, `group_by`, `segments` |
| quotient D → D/G | (none: stores both (a,b) and (b,a)) | stride-0 is only the *projection* special case | SymIdx/AntisymIdx/Hermitian with character χ |

Several familiar facts fall out of this:

- **Einsum** is `C = Σ_π (Δ_{p_A} A ⊗ Δ_{p_B} B)` over the joint domain J. CuTe's M/N/K/L classification is simply the factorization of J: M, N and L are kept by the output projection π, and K is summed away. SQL covers the diagonal and marginal cases CuTe's einsum refuses, because in this form they are just other choices of maps.
- **Joins become layout composition** exactly when the fiber product is the graph of a function f = ℓ_B⁻¹∘ℓ_A. An inner join then means f's domain of definition clips the result. That is the "bounded composition" CuTe lacks, since today it extrapolates.
- **Regridding** is Σ along a *weighted* relation, i.e. a sparse matrix. That explains why xql's "arrays compute the weights, SQL applies them" split is natural.

## 4. Laws: licensing as theorems about these constructions

1. **Value ↔ position factorization.** A relational operator given by labels lowers to a layout operator iff the relevant ℓ is injective and ℓ_B⁻¹∘ℓ_A lies in a structured map class. Provenance is the special case ℓ_A = ℓ_B *on the same object*, which makes the map the identity, so the join is a zip. Equal extents alone prove nothing.
2. **Σ needs a commutative monoid**, because fibers are unordered. Otherwise it needs *ordered fibers* and a fold that respects the order. This is Blade's layout-group result that direction is a free choice iff the fold is commutative and associative. It is also why floating-point reproducibility needs a fixed segmentation.
3. **Beck–Chevalley.** Σ and Δ commute across pullback squares. This is the formal statement of pushing aggregation past a join: Blade's deferred `reduce(method_for(A,B) <@> f, +)`, and variable elimination in sum-product (FAQ) queries.
4. **Frobenius.** `Σ_f(Δ_f x ⊗ y) = x ⊗ Σ_f y`: factor constants out of sums. Blade's proved left distribution is an instance.
5. **Equivariance.** Take v to be a χ-twisted G-equivariant section. The largest group under which a composite `k∘(A₁…Aₙ)` is equivariant for *all* data is **H ∩ Stab**. Blade has proved this as an iff, and it is the one law with no counterpart in either other system.
6. **Cost.** The work of a diagram is Σ over its nodes of the orbit count |D_v/G_v|. That is Blade's W = Σ ω(G_v, X_v), a lower bound for any program correct for all equivariant kernels.

## 5. The ladder of map classes is the physical semantics

The theory is the same at every rung; only the price of evaluating Δ and Σ changes. Physical planning means finding the lowest rung a map factors through.

| Map class | Who has it | Δ costs | Σ costs | Closed under |
|---|---|---|---|---|
| affine over ℤ or F₂, box domain | CuTe | stride arithmetic, zero-copy | dense reduce (divide) | composition, **only in normal form** |
| quasi-affine / Presburger | ISL, Blade domains | loop-nest bounds | dense reduce | composition, intersection, projection (decidable) |
| polynomial ranks | Blade SymIdx/OrbIdx | combinadic arithmetic | orbit-sum | not Presburger at all |
| tabulated | CompoundIdx, CSR, SparseIdx | gather | segmented reduce | everything |
| arbitrary | SQL engines | hash probe | hash aggregate | everything |

Two consequences stand out:

- **CuTe's partiality is a normal-form failure, not a semantic one.** Every CuTe layout is already a quasi-affine map, because `idx2crd` is div/mod. Its divisibility conditions are exactly the cases where the composite stays expressible as shape:stride.
- **Symmetric storage sits strictly outside Presburger.** That is the part of the unified theory only Blade contributes.

