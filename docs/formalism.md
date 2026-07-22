# Blade: Language Formalism (v11)

**Status**: canonical semantics for the Blade rewrite. Supersedes
`blade_formalism_v10.md` for language semantics. This document deliberately
contains *only* semantics: proofs live in [proofs.md](proofs.md) (mirroring the
machine-checked Coq tower), the feature census in [features.md](features.md),
feature modules under [features/](features/), plans in [future.md](future.md),
related work in `blade_literature_survey.md`.

**Corrections from v10** (details in proofs.md and the doc-set
[README](README.md)): product symmetry is *joint* per identity group, not
per-dimension (v10 §14.5/§14.6/§10.9.5 corrected); shared index spaces without
array identity license no symmetry; the H ∩ Stab lowering law is an exactness;
compound-index application is the single joint-tuple form; MonadPlus right distribution is
not a law.

---

## 1. The S/T model

Blade is **structure-first (S/T)**: iteration structure is primary and
explicit; element operations are applied to it. Traditional array programming
is **collection-first (T/S)**: operations over collections, iteration implicit.

```
T/S:  Collection × Operation → Result     (iteration derived)
S/T:  Structure × Kernel → Result          (structure is a value)
```

The two fundamental array operations — iteration (enumerating positions) and
indexing (accessing positions) — are fused in Blade into a single concept, the
**loop object**, which can be constructed from either side:

```blade
method_for(A, B)   // from structure: arrays determine iteration+indexing
object_for(f)      // from operation: kernel arity determines iteration+indexing
<@>                // application connects them, producing a Computation
```

The fusion rests on the isomorphism `Array<T, I, J> ≅ I → J → T`: arrays are
curried functions from indices to values (§4.3). Because the loop object owns
both iteration and indexing, kernels receive values anonymously — no index
naming — which is what lets one kernel serve every arity.

S/T and T/S compose rather than compete: S/T governs outer structure
(iteration, parallelism, symmetry); T/S combinators (`fold`, `scan`,
`tree_reduce`) govern reduction strategy inside kernels.

Why S/T is *required* (not merely chosen) for symmetric-tensor speedups is a
theorem package, not doctrine: iteration-object impossibility in T/S,
fixed-text impossibility for variable-arity triangular nests, runtime
reification necessity, zero-cost requiring compile-time symmetry tracking, and
the two-generators-plus-closure structure of the Trinity (loop reification,
dimensional currying, arity polymorphism). Statements, proofs, and their
checked Coq artifacts: proofs.md §2 and §9.

## 2. Preliminaries

### 2.1 Notation

| Symbol | Meaning |
|--------|---------|
| ℕ | Natural numbers |
| T | Base types (float, int, complex, ...) |
| r, n ∈ ℕ | Ranks |
| σ, τ ∈ ℕ* | Symmetry vectors |
| ε, δ ∈ ℕ* | Extent vectors |
| c ∈ ℕ* | Commutativity vectors |
| A* | Sequences of arrays |

### 2.2 Arrays

An array is a tuple A = (T, r, σ, ε): element type, rank, symmetry vector
(|σ| = r), extent vector (|ε| = r). σᵢ = σⱼ means dimensions i and j are
interchangeable; values are local to each array.

- Dense matrix: σ = ⟨1, 2⟩ · Symmetric matrix: σ = ⟨1, 1⟩ ·
  Partial: σ = ⟨1, 1, 2⟩ (dims 0,1 symmetric, dim 2 independent).

### 2.3 Extents

Extents are runtime values intrinsic to arrays — inferred from data sources,
declared in construction, or computed for T-dimensions. Extent-passing is
opaque to the user; extents flow from bound arrays. T-dimension extents may be
expressions over input extents:

```
tdim_extent ::= literal | input.extent(dim) | tdim_extent op tdim_extent
                where op ∈ {+, -, *, /}
```

### 2.4 Value types and promotion

Base types: `Int32/Int64/Float32/Float64/Complex64/Complex128`. Promotion:
float beats int (implicit int→float only), wider beats narrower within a
category, complex promotes componentwise. `Int64 × Float32 → Float64`.

Type variables (single capitals) are universally quantified within a
signature; the same letter denotes the same type; `cast<A,B>` is the promotion
result:

```blade
function add(a: A^0, b: A^0) -> A^0
function scale(s: A^0, v: B^1) -> cast<A,B>^1
```

Complex values support literals and `conj(x)`; `conj` is the identity on real
types and distributes elementwise over arrays.

**Units of measure** are annotations on primitives, not types: `Unit meters`,
`Unit velocity = meters / seconds`, `Float<velocity>`. Unit arithmetic checks
addition (same unit) and composes under `*`/`/`. **Bounded primitives**
(`Float<min=0, max=1>`) carry runtime-checked bounds and compose with units.
**Mutually constrained types** (`type V1 ... and V2 ... where <expr>`) require
joint assignment and assert (not solve) the constraint at runtime.

### 2.5 Array expressions

`ArrayExpr<T, r, σ>` is an unevaluated array transformation. `pure` (implicit)
lifts arrays; `|> compute` materializes with cache-optimal layout; round trip
`pure A |> compute ≡ A`. `method_for` accepts both `Array` and `ArrayExpr`,
materializing the latter before loop construction so iteration always runs
over optimal layout.

### 2.6 Array combinators

Over `ArrayExpr` (results are `ArrayExpr`):

| Combinator | Signature sketch | Semantics |
|-----------|------------------|-----------|
| `zip(A₁..Aₙ)` | → Tuple elements over shared k = min rank prefix | `zip(A,B)(i..) = Tuple(A(i..), B(i..))`; output symmetry = intersection where all inputs agree; kernel receives ONE tuple argument |
| `align(A₁..Aₙ, spec)` | → `AlignedExpr` | zip + stencil metadata (dims, offsets, boundary ∈ Shrink/Pad/Periodic/Reflect); kernel receives N separate arguments |
| `stencil(A, {d: offsets}, boundary)` | sugar | desugars to `align` of `shift`s |
| `stack(A₁..Aₙ)` | → rank+1, fresh leading symmetry class | `stack(A,B,C)(k)` selects array k |
| `transpose(A, p)` | permutation p | hard transpose (data movement on materialize); `transpose(transpose(A,p),q) ≡ transpose(A, q∘p)`; on symmetric arrays with the identity-under-σ permutation it is the identity, on antisymmetric it negates per parity |
| `diag(A, (d₁,d₂))` | rank−1 | collapse two dims to their diagonal |
| `join(A, B, d)` / `subset(A, d, (s,e))` / `split(A, d, i)` | | concatenate / range-extract / split = two subsets; split-join round-trips |
| `reverse(A, d)` / `shift(A, d, k, boundary)` | | index reversal (involution) / offset with boundary handling |
| `A <\|:> B` | fallback | `A(i)` if allocated else `B(i)`, checked per curry level; A's layout dominates iteration order; symmetric A requires symmetric allocation |
| `decompact(A, axis)` | compact → dense | expand a symmetric/antisymmetric compact axis to dense storage; sign-correct for antisymmetric sources; chainable to full dense; error on non-compact axes |

### 2.7 Binding forms

| Syntax | Mutable in scope | Passable to `mut` param |
|--------|------------------|-------------------------|
| `let const x = e` | no | no |
| `let x = e` | yes | no |
| `let mut x = e` | yes | yes |

All parameters pass by reference; `mut` on a parameter permits callee mutation.

## 3. Index Types

### 3.1 Design principles

1. Bounds are values, not type parameters (runtime shapes, static safety).
2. Index types compose (products, nesting, symmetry combinators).
3. Constraints live in the index type itself, not side metadata.
4. Erasure to simple C++ (dependent structure → runtime bounds, no template
   explosion).
5. Currying is preserved uniformly; partial indexing yields dependent types.

Dimensions (coordinate arrays: latitudes, timestamps) are ordinary 1-D arrays;
index types determine iteration and storage. The association between them is
user convention.

### 3.2 The index-type contract

Every index type of arity r is a curried signature `N → N → ... → N` (r
positions) defining:

- **Domain**: the valid r-tuples;
- **Cardinality**: |domain|;
- **Storage bijection**: valid tuples ↔ offsets [0, cardinality);
- **Enumeration**: iteration in offset order — which is guaranteed to be
  **lexicographic** order on tuples for every index type in this section
  (proved once at the arrow level; proofs.md §Lex).

| Index type | Arity | Domain | Bijection |
|------------|-------|--------|-----------|
| `Idx<N>` | 1 | 0 ≤ i < N | i ↦ i |
| `SymIdx<2,N>` | 2 | i ≤ j < N | triangular |
| `SymIdx<3,N>` | 3 | i ≤ j ≤ k < N | tetrahedral |
| `<Idx<N>, Idx<M>>` | 2 | i < N, j < M | i·M + j |
| `CompoundIdx<mask>` | rank(mask) | mask-true tuples | hash of valid tuples |

Indexing is function application with `()`; `A(i, j, k) ≡ A(i)(j)(k)`; `[]` is
reserved for poly-tuple structural access. A COMPOUND axis is one slot whose
domain is k-tuples, so it is applied with ONE tuple value: `B((lat, lon))`,
wildcards inside the tuple (`B((lat, _))`), short tuples pinning a leading
prefix; a rank-1 compound takes a bare scalar (1-tuples collapse in the
parser). The flat form `B(lat, lon)` is a type error with a steering
diagnostic 
—
 under the `A(i, j) 
≡
 A(i)(j)` sugar it would claim two slots,
and it is ambiguous once wildcards meet trailing dims. (Resolves the v10
§
4.5-vs-
§
5.3 inconsistency in favor of 
§
4.5's tuple form.)

### 3.3 Base index types

| Type | Signature | Description | Hashable |
|------|-----------|-------------|----------|
| `Idx<n>` | `N` | contiguous 0..n−1 | trivially (extent) |
| `EnumIdx<S>` | `N` | enumerated categories (incl. string domains) | from S |
| `RaggedIdx<lengths>` | `N` | variable inner extent | from lengths |
| `CompoundIdx<mask>` | `N → N → ...` | sparse combinations from a k-dim mask | whole-mask hash |

Float indices are forbidden (not safely hashable); coordinates are data.

**Structural matching (duck typing)**: index types are equal iff extent, tag,
and hash agree — this is what lets two files with the same grid interoperate.

**Named index types** (`type LatIdx = Idx<360>`) add nominal identity and unit
identity (§3.10); anonymous occurrences each get fresh identity. **Tagged
index types** `Idx<n, Tag>` (enum tags) distinguish same-extent spaces
(staggered/Arakawa grids).

### 3.4 Symmetric index types

| Type | Constraint | Cardinality | Use |
|------|------------|-------------|-----|
| `SymIdx<r, n>` | i₁ ≤ ... ≤ iᵣ | C(n+r−1, r) | comoments, covariance |
| `AntisymIdx<r, n>` | i₁ < ... < iᵣ | C(n, r) | forms, determinants |
| `HermitianIdx<n>` | A(i,j) = conj(A(j,i)) | n² stored | complex Hermitian |

The `SymIdx` cardinality closed form C(n+r−1, r) is proved (hockey-stick;
proofs.md §Binomial).

- `make_sym` sorts; `component(idx, k)` projects in nondecreasing order.
- `make_antisym` sorts and returns the permutation sign; access through
  non-canonical coordinates applies the sign (`transform`); diagonals are
  implicit zeros (not stored).
- `HermitianIdx` stores the full matrix with conjugation semantics
  (`canonical` returns a needs-conj flag); a rectangular variant exists for
  factor storage.
- Currying: `S : SymIdx<3,n>` gives `S(i) : SymIdx<2, n−i>`-like remainders
  and finally `BoundedIdx` (§3.6); the isomorphism
  `SymIdx<2,n> ≅ DepIdx<Idx<n>, λi. BoundedIdx<i,n>>` holds but r > 2 is not
  expressible as nested DepIdx (scope of recursive bounds) — SymIdx is
  primitive.

**Nested/mixed symmetry** (spec level): `NestedSymIdx<n>` (symmetric pairs of
symmetric pairs; S = n(n+1)/2, cardinality S(S+1)/2; elasticity tensors),
`RiemannIdx<n>` (antisym pairs, symmetric between them; n=4 → 21). Users
compose via `Sym<I,I>`, `Antisym<I,I>`, products; the `unsafe indextype`
escape hatch supplies custom `canonical : indices → Option<canonical>` (None =
implicit zero) and `transform` (value adjustment on non-canonical access).

### 3.5 Compound and sparse index types

**`CompoundIdx<mask>`** — for mutually-dependent sparsity (ocean points, not a
product of valid lats × valid lons). Signature matches mask rank, so currying
passes through the compound. Identity = whole-mask hash (O(1) type equality);
storage is contiguous over mask-true tuples; per-element hash gives O(1)
coordinate lookup. Enumeration = exactly the in-bounds mask-true tuples, each
once, in lex order (proofs.md §Compound).

Two construction routes:

```blade
type OceanIdx = CompoundIdx<ocean_mask>    // static type route
let view = compound(dense, mask)           // runtime builder route (§15, sql.md)
```

**Partial indexing and residual compounds.** Fixing coordinates (including
wildcards, interior or trailing) yields:

| Free dims remaining | Result index |
|---------------------|--------------|
| 1 | plain `Idx<n>` (n = count of valid completions) |
| ≥ 2 | **residual CompoundIdx** — the residual of a mask is a mask |

Cost is O(valid combinations) to reconstitute (must enumerate completions);
the curried identity derives from (mask hash, fixed coordinates). Executable
semantics of the residual: `has_completion` (proofs.md §Compound). Reject
cases (all-free wildcard application, arity-short wildcard tuples, interior
holes in trailing windows) are type errors.

**`SparseIdx<entries>`** — explicit enumeration of valid tuples (CG triples,
edge lists). Tuple indexing only; wildcards return matching entries; iteration
visits |entries|. Prefer `CompoundIdx` when validity derives from data.

### 3.6 Bounded and dependent index types

`BoundedIdx<l, u>` (l ≤ v < u; `Idx<n> ≡ BoundedIdx<0, n>`) is not user-declared;
it arises from currying symmetric indices — `S(i) : Array<... BoundedIdx<i, n>>`.
The dependence erases to runtime bounds in C++.

`DepIdx<I, f>` generalizes: inner index type depends on the outer index value;
extent = Σᵢ |f(i)|; iteration yields (i, j : f(i)); the storage bijection is
the general left-justified one (proofs.md §DMWF). Instances:

```blade
type RaggedIdx = DepIdx<Idx<n>, lambda(i) -> Idx<lengths(i)>>   // ragged
type TriIdx<n> = DepIdx<Idx<n>, lambda(i) -> Idx<n - i>>        // triangular
type IrrepsIdx<spec> = DepIdx<...>                              // ML blocks
```

`RaggedIdx` exists in closed form (lengths visible) and opaque form (lengths
abstract at function boundaries); both support reduce/extents/indexing, and
ragged literals construct them directly.

### 3.7 Index transforms

`flip(A, dim)` (reverses ordering, changes hash), `rename(A, old -> new)` (tag
change), `subset(A, dim=lo..hi)` (new extent + hash), `align(A, B, dim)` (join
on common indices). All explicit; no implicit conversions; mismatches are type
errors.

### 3.8 Files as type providers

```blade
type ERA5 = NetCDFProvider<"era5.nc">
// ERA5.lat_idx : Idx<721>, ERA5.t2m : Array<Float like Idx<721>, Idx<1440>, Idx<8760>>
```

Compile-time metadata inspection instantiates index types; runtime reads
values. Valid because file *structure* is quasi-static and structure (not
values) determines types. Provider slots (NetCDF now; HDF5/Zarr planned) sit
behind one interface.

### 3.9 Symmetry lives in the index type

`Array<Float like SymIdx<2, 1000>>` — not a dense array plus an annotation.
The index type fixes storage (triangular), iteration (triangular), and access
(canonicalizing): `cov(3, 1)` and `cov(1, 3)` are the same location. The
symmetry system (§11) *infers* symmetric index types for outputs from kernel
commutativity and array identity.

### 3.10 Index values and nominal typing

Iteration emits values tagged with their source index type as a **unit**:
`method_for(range<LatIdx>) <@> lambda(i) -> ...` gives `i : Nat<LatIdx>`.

- Array indexing requires unit match: `A : Array<T, LatIdx>` accepts
  `Nat<LatIdx>`, rejects `Nat<LonIdx>` even at equal extent.
- Literals need explicit units (`A(10 : Nat<LatIdx>)`); arithmetic preserves
  units; mixed-unit arithmetic is an error.
- Bounds safety by construction: emitted indices are in range, indexing
  requires matching units, therefore `A(i)` is always valid. (The rank-2
  offset arithmetic behind this is verified against a failure model;
  proofs.md §Safety.)
- Explicit casts (`i as Nat<LonIdx>`) are the escape hatch, and lambda
  captures are checked by unit — a captured array is only indexable by
  iteration variables of its own index type.

This is the index-level mirror of physical units (§2.4): same mechanism, same
error class.

## 4. Array Types

### 4.1 Three levels

| Level | Form | Known | Use |
|-------|------|-------|-----|
| Fully abstract | `T^r(σ)` | rank, symmetry class | arity-polymorphic signatures, typing rules |
| Index-typed | `T^(I₁, I₂, ...)` | index structure | kernel bodies, combinators |
| Fully concrete | `Array<V like I₁, ...>` | value type, indices, extents | data declarations |

`T^r ≡ T^r(1, 2, ..., r)` (dense). Transitions: symmetry inference (§11) takes
abstract → index-typed; value instantiation takes index-typed → concrete.
Lowering table for inferred σ:

| Abstract σ | Concrete index types |
|------------|----------------------|
| (1, 1) | `SymIdx<2, n>` |
| (1, 2) | `Idx<n>, Idx<m>` |
| (1, 1, 1) | `SymIdx<3, n>` |
| (1, 1, 2, 2) | `SymIdx<2, n>, SymIdx<2, m>` |

Extents are values: the type system tracks structure; extents flow at the
value level.

### 4.2 Type identity

```blade
Array<T like I₁, I₂, ..., Iₙ> ≡ Array<Array<T like I₁, ..., Iₙ₋₁> like Iₙ>
```

Currying is projection; rank is nesting depth; storage is flattened nesting.
Symmetric indices are NOT nested-equivalent:
`Array<T like SymIdx<2,n>> ≢ Array<Array<T like Idx<n>> like Idx<n>>` —
currying them yields dependent `BoundedIdx` remainders.

### 4.3 Arrays are functions

`A : Array<T like I₁, ..., Iₙ>` is semantically `I₁ → ... → Iₙ → T`. Any
expression producing a valid index is a valid index (literals, arithmetic,
function results, conditionals). Indexing and function application intermix
freely because they are the same thing:

```blade
let models: Array<(Params → TimeSeries) like LatIdx, LonIdx>
models(lat, lon)(params)(t) : Float
```

**Poly-indexing**: `A(indices)` with a tuple of length rank(A); `all_indices(A)`
iterates all valid tuples respecting structure. Use for rank-polymorphic
operations (trace, sum-all); for standard arrays prefer curried/loop access to
preserve cache order.

**Computational indices**: the structural index type defines the address
domain; what you pass may be richer (`Dual(i, di)` for AD, `Symbolic`,
thunks) so long as it resolves to a valid address. Fast access requires the
structural bijection (forward: position → offset; backward: offset →
position).

## 5. Functions

### 5.1 Signatures and metadata

```
f : (T₁^r₁, ..., Tₙ^rₙ) → T_out^r_out
```

with commutativity vector c (cᵢ = cⱼ iff arguments i, j share a `comm` group;
non-listed arguments are singletons), parallelism spec (`omp(x: depth)` —
parallelize `depth` S-dim levels of argument x, outermost first; `cuda` and
other backends substitute), and T-dimension spec (`tdim({extent, symm, name})`
records) when output dims don't derive from inputs.

```blade
function name(x₁: T₁^r₁, ..., xₙ: Tₙ^rₙ)
where comm(xᵢ, xⱼ), omp(x₁: 2), tdim({ extent: e, symm: k, name: "freq" })
-> T_out^r_out
= body
```

Return type follows `where` because it may depend on constraints (`comm` can
produce `SymIdx` outputs). Nested `function` declarations desugar to
`let const` lambdas.

### 5.2 Lambdas

`lambda(a, b) -> expr`, optional type/rank annotations, `where` clauses
(`comm`, return type) as on functions, block bodies in braces. Pure by
definition. Array captures are unit-checked (§3.10). Parameter types infer
from context; array-typed parameters need explicit rank annotations.

Sections and partial application: `(+)`, `(/) x`, single-wildcard `f(_, y, z)`
(multiple wildcards rejected — use a lambda).

### 5.3 Reynolds operators

`reynolds(g)` is the VALUE-LEVEL symmetrizing wrapper: it builds the kernel
`K(x₁..xₙ) = Σ_σ g(x_σ(1)..x_σ(n))` (with `Antisymmetric`, the sign-weighted
sum), permuting the kernel's value arguments; `positions=[...]` restricts to a
subset. K is commutative by construction — reynolds manufactures H = Sₙ. What
that buys still follows the H ∩ Stab law (§11.2):

- **Identical arrays**: full transfer — symmetric (or strict antisymmetric)
  output storage and triangular iteration over canonical tuples (joint over
  the compound axis for multi-dim arrays, §12.4). Each canonical cell sums the
  n! permutation terms, deduplicated when structurally equal (multiplicity ×
  representative), so a commutative g costs one term. In the current language
  the license is DECLARED: the wrapped kernel carries `comm(...)` — an
  interchangeable-for-iteration declaration, not a truth claim about g (a
  comm-declared g may be Reynolds-antisymmetrized to nonzero). `reynolds`
  without `comm` yields dense symmetrized values (corpus reynolds/022–023 pin
  both behaviors). Whether `reynolds` should SELF-license (K = Σ g∘σ has
  H = Sₙ by construction, so the declaration is derivable) is an open design
  question — future.md. Antisymmetric Reynolds
  zeroes diagonals and negates on transposes by storage construction.
- **Distinct arrays**: K is commutative but Stab = {id} — the output is DENSE
  and not index-symmetric (`Out(i,j) = g(A(i),B(j)) + g(B(j),A(i))`, which is
  not `Out(j,i)`; pinned by corpus reynolds/013). Reynolds does not substitute
  for identity.

Distinguish this from the INDEX-LEVEL (per-dimension) Reynolds of the proof
tower — `R(i₁,i₂,j₁,j₂) = Σ over index swaps` — which genuinely has
per-dimension product symmetry with lossless canonical access (proofs.md
§Core, `reynolds_full_product_symmetry`). That is a different, stronger
operator (it reads every array at every permuted index, n!^d terms) and is
not currently a surface construct — see future.md.

### 5.4 Static functions and type-level computation

`static function` may capture only `let const`/static values and is callable
at compile time; `let const` values close over literals, other consts, and
static applications. Static functions appear in type positions
(`Idx<triangle(n)>`). No totality proofs (vs Idris/Agda); explicit marking
(vs C++ constexpr's syntactic restrictions). `static type` functions
(`Vec<N>`) are compile-time-only: not storable, not passable, not returnable
at runtime — keeping type-level computation decidable.

## 6. Core Operations

### 6.1 Elementwise and outer operator pairs

| Elementwise | Outer | Op |
|-------------|-------|----|
| `+` `-` `*` `/` `%` `^` | `[+]` `[-]` `[*]` `[/]` `[%]` `[^]` | arithmetic |
| `==` `!=` `<` `<=` `>` `>=` | `[==]` ... `[>=]` | comparison |
| `&&` `\|\|` | `[&&]` `[\|\|]` | logical |

```blade
A + B    =  method_for(zip(A, B)) <@> lambda((a, b)) -> a + b   // co-iteration
A [+] B  =  method_for(A, B) <@> (+)                             // cross-iteration
```

`(+)` remains the scalar kernel for combinator use; all three coincide at
rank 0. Bracketed ops inherit primitive symmetry: `A [*] A` iterates
triangularly automatically.

### 6.2 Primitive symmetry annotations

`(+)`/`(*)` Symmetric, `(-)` Antisymmetric, `(/)` Asymmetric — the compiler
infers `comm`/`antisymm` for kernels built from them (`a + b` ⇒ comm(a, b)).

### 6.3 Geometric primitives and reductions

`norm` (equivariant → invariant), `dot` (symmetric; invariant result), `cross`
(antisymmetric; representation per domain library), `sum`/`mean` (rank-
reducing, equivariance-preserving), `min`/`max` (invariant-only — ordering
requires invariance). Equivariance signatures live in the ML module
([features/equivariant-nn.md](features/equivariant-nn.md)); the core carries
the annotation hook only.

### 6.4 Additional value operators (v7-established)

- `gram(...)` — Gram matrix construction over dense, symmetric, or Hermitian
  structure (value-checked against independent oracles).
- `hermitian(A)` — adjoint.
- `conj(x)` — componentwise conjugation (identity on reals).
- `reduce(A[, kernel[, init]])` — innermost-dimension fold; default kernel
  `(+)`; see [features/sql.md](features/sql.md) §10 for typing details and the
  empty-input rule.
- `extents(A)` — rank-1: scalar; dense rank-k: tuple, outermost first;
  compound: cardinality. Rejected where a per-dimension scalar doesn't exist
  (ragged/grouped) — use `extents(row)`.

### 6.5 Relational operations

`mask`, `compound`, `intersect`, `union`, `unique`, `contains`, `group_keys`,
`group_by`, `sort` are specified in [features/sql.md](features/sql.md). They
are ordinary array-level operations riding the index-type system: masks are
Bool arrays over the source's own index space, `compound` materializes
CompoundIdx views, grouping produces ragged rank-2 arrays consumed by loop
objects.

## 7. Loop Objects

### 7.1 The two constructors

```blade
method_for : A* → MethodLoop        // arrays bound, kernel awaited
object_for : Function → ObjectLoop  // kernel bound, arrays awaited
```

Both produce the same kind of value — a reified iteration pattern. The
distinction is construction order only. Completion produces a `Computation`:

```
MethodLoop × Function → Computation
ObjectLoop × A*       → Computation
```

That exactly these two curryings exist is forced: identity detection needs all
arrays; commutativity detection needs the kernel; the sources are disjoint;
any other partial specification is redundant or detection-incomplete
(two-maximal-curryings theorem, proofs.md §Currying). `nested_for` (fully
specified) achieves the speedup but cannot compose; composition requires this
decomposition.

### 7.2 S-dimensions and T-dimensions

- **S-dimensions**: from iterating input arrays; count =
  Σᵢ (rank(Aᵢ) − irank(f, i)), where `irank(f, i)` is the rank the kernel
  declares for argument i (the slice rank it receives).
- **T-dimensions**: introduced by kernel output (FFT: time → frequency);
  count = f.ORank; trailing in the output.
- Output rank = S + T. T-dimensions are *relational* — they depend on kernel
  and array signatures jointly, which is exactly why T/S systems cannot form
  iteration objects (proofs.md §2).

Kernels live in T-world: they see slices, never S-dims; `comm` is metadata for
the loop object, not the kernel body. S-dims are deduced at application sites.

### 7.3 Virtual arrays

Type-level iteration sources with `Void` element type; they erase completely:

```blade
range<I>       // enumerate I in storage (= lex) order:  λi:I. i
reverse<I>     // reversed
blocked<I, K>  // K-sized cache blocks (spec level)
```

`range<SymIdx<2,N>>` emits canonical pairs; `range<CompoundIdx<...>>` emits
mask-true tuples. Virtual and real arrays compose in one loop:
`method_for(range<I>, A, B) <@> lambda(i.., a, b) -> ...` — this is how
kernels receive indices without breaking index anonymity. Anonymous range
forms (zero-based, offset, literal extents) are supported.

### 7.4 For-loop syntax

`for` is surface syntax over the constructors — it builds iteration objects,
not imperative control flow. One side carries arrays/indices, the other the
kernel; `in` accepts virtual arrays only:

```blade
for (A, B) in range<I> <@> lambda(i, j, a, b) -> ...       // method_for style
for lambda(a, b) -> a * b <@> (A, B)                        // object_for style
let loop = for (A, A) in SymIdx<2,N> where comm             // let-bound, awaits kernel
let op   = for lambda(a, b) where comm -> a * b             // let-bound, awaits arrays
for args in SymIdx<arity(args), N> where comm(args) <@> lambda(is, xs) -> ...  // poly
```

### 7.5 Recursive arrays

Arrays are functions (§4.3) and functions recurse, so arrays recurse. A
sequential recurrence — time-stepping, training epochs, an RNG stream — is a
**self-referential array definition by structural induction on the extent**,
not imperative control flow:

```blade
type Times = Idx<1600>
let rec qh: Array<Complex128 like Times, Y, X> =
    match qh with
    | zero        -> zero                            // extent 0: the empty array
    | zero :: s   -> zero :: initial_field(...)      // extent 1: the seed slice
    | prefix :: n -> prefix :: step(n, prefix)       // extent n+1 from extent n
```

Semantics: the binding denotes a family `(n : ℕ) → Array<T like Idx<n>, ...>`
— arrays-as-functions lifted one level, to functions of the extent. The match
destructures the family: `prefix` binds the same array at extent n, `n` the
new step ordinal. Reading the binding at its declared extent (or any smaller
one) instantiates the family; interior reads `qh(k)` and final-segment reads
compose with every combinator.

Rules, all checked syntactically:

- **Recursion axis = the leading axis, always.** Match destructuring is
  co-currying: application `A(i)` peels the first dimension going down, the
  pattern `prefix :: slice` peels it going up. No axis annotation exists.
  Multi-dimensional recurrences nest: the slice expression may itself be a
  `let rec` over *its* leading axis, capturing `prefix` (DP tables).
- **Productivity**: the inductive arm must literally have the shape
  `prefix :: e` with `e` one rank-reduced slice — exactly one new slice per
  step, the inverse of the pattern. `::` is array snoc along the leading
  axis and exists only inside these arms; `join` (§2.6) remains the general
  concatenation.
- **Termination by construction**: the recursive occurrence sits at a
  strictly smaller extent, and extents are finite — the definition walks
  down to the base case. There is no lag arithmetic to verify and no
  halting question to answer; ill-founded definitions are unwritable, not
  detected.
- **Base cases**: `| zero -> zero` is required (the empty array is the §10.4
  monadic zero along the recursion axis); one `| zero :: s -> zero :: seed`
  arm may follow. A definition without a seed arm must handle the empty
  prefix inside the slice expression.
- **Implicit zero history.** A prefix read that falls outside the prefix
  built so far denotes the element type's **zero** — `prefix(n - k)` at
  `n < k` is a zero slice, and so is a read at or beyond the current step
  (`prefix(n)`, `prefix(n + 1)`), where nothing has been written yet. This
  extends the base case rather than adding a rule: the empty-array boundary
  yields zero slices, so §10.4's monadic zero governs not just the whole
  axis but every read that runs off its start. It is the array-side twin of
  §8.2's implicit identity base case for recursive kernels (`f(())` returns
  f's identity element).

  The consequence is that a multi-lag scheme states its startup transient in
  its *weights* instead of defending it at the call site. An AB3 integrator
  writes `prefix(n - 3)` unconditionally — the zero-weight bootstrap
  annihilates the term — where a hand-guarded
  `if n >= 3 then prefix(n - 3) else ZERO` says the same thing twice.

  This is a guarantee about the language, not about the current storage: it
  compiles to a bounds test on the recursion ordinal, and it holds
  independently of the storage policy below. The rolling window in
  particular must preserve it — under a K+1-slot buffer an out-of-range lag
  must still read zero, not a recycled slot.
- **Sequentiality is derived, not commanded.** The prefix dependence forces
  serial enumeration of the recursion axis; the compiler schedules the
  scheme as one serial sweep. Storage is policy, not semantics: consumers
  that read only a trailing segment get a rolling window; materializing
  consumers (delay embedding, `|> compute`) get the full trajectory.
- **Compilation is tail-call elimination, totally.** The productivity rule
  makes every definition tail-recursive *modulo the snoc* (TRMC): the
  inductive arm is a tail call wrapped in one constructor whose result
  position is known. The scheme therefore compiles to a constant-stack
  sweep writing each slice into its contiguous block of one pre-allocated
  buffer — no recursion frames, no prefix copies — and this is guaranteed
  for every well-formed definition, not best-effort. The rolling window is
  the same elimination applied to storage: when the prefix's consumption
  is bounded at depth K, the buffer itself shrinks to K+1 reused slots.
- **v1 bounds** (the decidability fence): the declared extent is static;
  the annotation is mandatory (a self-referential definition cannot infer
  its own type — recursive functions declare return types for the same
  reason).

Running diagnostics ride the same sweep: a `reduce` over the recursive
array (a CFL max, a loss trace) folds in enumeration order without a second
pass. State continuation is a second definition seeded from the first's
final slice.

## 8. Arity Polymorphism

### 8.1 The concept

Rank polymorphism varies the shape of one input (`sum : T^r → T^0`). Arity
polymorphism varies the NUMBER of inputs, and the arity determines output
rank, loop depth, and symmetry:

```blade
let moment = for lambda(is, xs) where comm(xs) -> product(xs)
moment <@> (data, data)        // covariance   (rank 2)
moment <@> (data, data, data)  // coskewness   (rank 3)
```

Variadic functions cannot express this: their output type is fixed regardless
of argument count. Arity-dependent output typing requires type-level arity —
which Blade provides through the loop-object judgment (§13), not through
general dependent types.

### 8.2 Kernel syntax

```blade
function kernel(a: Poly<T^k>) -> T^m
where comm(a)
```

`Poly<T^k>`: a pack of rank-k slices. In the body: destructuring
`let (head, tail) = args` (left-associative; excess names bind `()`; warning
outside poly scope), indexing `args[k]` (`[]` = structural access), scope
variables `arity` (pack size) and `nth` (recursion depth), and iteration over
the pack via the poly former `method_for(range<Idx<arity(p)>>)`. Recursive
kernels need no explicit base case: `f(())`
returns f's identity element. Nested tuples preserve structure (`arity` counts
top level; `comm` does not penetrate sub-tuples; no deep indexing —
destructure instead).

### 8.3 Identity groups

At a call site, **neighboring identical arguments** form identity groups
(syntactic identity by name; `(A, B, A)` is three singleton groups). `comm`
licenses symmetry only *within* an identity group.

### 8.4 Output type deduction

Given `object_for(kernel) <@> (A₁, ..., Aₙ)` with `kernel(a: Poly<T^k>) -> T^m`:

1. **T-dims**: last k indices of each input; must be compatible across inputs.
2. **Identity groups**: partition inputs by neighboring syntactic identity.
3. **S-dim contribution per group of arity g** over per-array S-dim index
   types (I₁, ..., I_s):
   - no `comm` or g = 1 → the group's S-dim types repeated g times (dense);
   - `comm` and g > 1 → **`SymIdx<g, I₁ × ... × I_s>` over the compound
     S-tuple**: the g whole index *tuples* are interchangeable. When s = 1
     this is the familiar `SymIdx<g, I₁>`.
4. **Concatenate** group contributions in order (concatenation, not
   broadcasting).
5. **T-dims of output**: from the kernel's `T^m` (and `tdim` spec).

**Correction vs v10 §10.9.** v10 step 3 emitted `SymIdx<g, extent>` *per
S-dimension* (e.g. `(A, A)` over `Array<M, N, T>` → `SymIdx<2,M>, SymIdx<2,N>`,
"(g!)² speedup"). That is unsound: with one identity group, only the
**diagonal** action — permuting whole argument tuples (mᵢ, nᵢ) — leaves the
output invariant; permuting one dimension's indices independently does not
(refuted constructively; and no per-dimension product layout can losslessly
store the joint-symmetric output at all: strict counting inequality
∏ⱼ C(nⱼ+r−1, r) < C(∏ⱼ nⱼ + r − 1, r)). See §12.5 and proofs.md
§Core/§Counting.

Correct examples:

```blade
// Self-covariance, 1 S-dim: unchanged
object_for(cov) <@> (A, A)      // A : Array<Float like Idx<N>, Idx<Time>>
// Output: Array<Float like SymIdx<2, N>>

// Multi-dim S-space: JOINT symmetry over compound tuples
object_for(comoment) <@> (A, A, A)   // A : Array<Float like Idx<M>, Idx<N>>
// Output: Array<Float like SymIdx<3, <Idx<M>, Idx<N>>>>
// Speedup: 3! = 6× (joint), NOT (3!)² = 36×

// Distinct groups multiply
object_for(k) <@> (A, A, B, B)  // A over Idx<M>; B over Idx<K>; comm within groups
// Output: Array<Float like SymIdx<2, M>, SymIdx<2, K>>
// Speedup: 2! × 2! = 4× — product across GROUPS, never across one group's dims
```

At r = 2, per-dimension product-*structured* storage is recoverable via the
Cauchy split (§12.5); it does not change cardinality or this typing rule.

## 9. Dimensional Currying

Arrays are functions; indexing is partial application; each application peels
exactly one dimension at the type level (`promote<T, r> → promote<T, r−1>`).

- **Cache optimality by construction**: with outermost-slowest layout, curried
  access at each loop depth touches contiguous memory; a cache-pessimal
  traversal is not expressible — it is a type error (cf. dimension-alignment
  diagnostics, §11.4).
- **Vs slicing**: `A(i)` is a contiguous pointer with a reduced-rank type, not
  a strided view of the same type.
- **Fusion enabler**: `(loop <@> f) <&!> (loop <@> g)` hands both kernels the
  same curried arrays at each depth — fusion at iteration level, no
  materialized intermediates, because partially-curried arrays of equal depth
  share types.
- **Symmetry composability**: currying (type-level rank reduction) is
  orthogonal to canonicalization (coordinate transform, §12.2); symmetric
  arrays curry to dependent-bound remainders.
- **Sparsity**: Blade is not a sparse-tensor system; `<|:>` plus partial-depth
  allocation provides user-managed sparsity without sparse formats.

## 10. Combinator Algebra

### 10.1 Core

```
(<@>)  : MethodLoop × Function → Computation | ObjectLoop × A* → Computation
(>>=)  : Computation α × (α → Computation β) → Computation β     // monad laws hold
pure   : α → Computation α
(<$>)  : (α → β) × Computation α → Computation β                  // f <$> c ≡ c >>= pure ∘ f
(|> compute) : Computation α → α                                  // trigger evaluation
```

### 10.2 Parallel and product

```
(<&>)  : Computation α × Computation β → Computation (α × β)
(<&!>) : same-MethodLoop computations → fused Computation
(<*>)  : MethodLoop × MethodLoop → MethodLoop
```

- `<&>` fuses isomorphic loop *prefixes* automatically (fusion depth §14.3),
  then splits.
- `<&!>` demands full fusion; restricted to computations from the same
  MethodLoop (ObjectLoop fixes S-dims only at application, so structural
  identity can't be verified).
- `<*>` concatenates array lists: `method_for(A) <*> method_for(B) ≡
  method_for(A, B)`; identity `method_for()`; commutative up to index
  reordering; associative. It is proved to be exactly shape concatenation with
  multiplicative cardinality (proofs.md §Trinity). `<*>` is purely structural
  — commutativity comes from the kernel later; the same MethodLoop under
  different kernels yields triangular or rectangular iteration accordingly.
- Runtime-arity loops: `fold(<*>, map(method_for, arrays))`; for object loops
  the fold is implicit in `object_for(f) <@> arrays`. Every n-ary loop is
  generated by unary loops under the product — arity polymorphism is the
  forced closure of {loop reification, currying} under `<*>`, which is the
  corrected reading of the "Trinity" (two generators + closure; proofs.md
  §TrinityAsym).

### 10.3 Composition

```
(>>@) : ObjectLoop × ObjectLoop → ObjectLoop          // compose kernels, then apply
(@>>) : Computation × Computation → Computation        // apply, then compose (same MethodLoop)
```

Both associative, with `object_for(id)` / `M <@> id` as identities.
**Compose-Apply duality** (proved; the mechanized proof is literally map
fusion):

```blade
(object_for(f) >>@ object_for(g)) <@> A  ≡  (method_for(A) <@> f) @>> (method_for(A) <@> g)
```

**Rank-0 convergence** (proved): for rank-0 kernels,
`object_for(f) <@> (A, B) ≡ method_for(A, B) <@> f`; wrapping is idempotent.
This is the license for pseudo-native syntax (§15.6): `A + B` commits to
neither constructor. Compose-Apply is the inductive case; rank-0 convergence
the base case; together they characterize when the two entry points coincide.

### 10.4 Choice, zero, guard (MonadPlus)

- `()` / `method_for()` — the empty loop, identity for `<*>`, base case for
  arity recursion (`method_for() <@> moment ≡ pure 1`).
- `zero` — the zero kernel: S-dims from arrays, no T-dims; resolves to the
  operation-appropriate identity (1 under `*`, 0 under `+`) in arity
  recursion base cases.
- `guard(p, c)` — `c` if p, else zeros of c's shape; `guard(p, guard(q, c)) ≡
  guard(p && q, c)`; exhaustive guards compose to plain choice.
- `c₁ <|> c₂` — first non-zero; associative, idempotent, `M <@> zero` is the
  identity.

**Laws (the checked set — proofs.md §Monad):**

```
mzero >>= k               ≡ mzero            // left zero      ✓ (also right zero holds)
mzero <|> m ≡ m ≡ m <|> mzero                // identities     ✓
(a <|> b) >>= k ≡ (a >>= k) <|> (b >>= k)    // LEFT distribution ✓
m >>= (λx. k x <|> h x)   ≢ (m >>= k) <|> (m >>= h)   // RIGHT distribution FAILS
```

Right distribution fails for this monad (interleaving); v10 never claimed it,
and the rewrite must not assume it.

Zero-function laws: `(M <@> zero) >>= k ≡ M <@> zero`; `zero` absorbs `>>@`
composition both sides; `shape(M <@> zero) = S-dims(M)`; `σ(M <@> zero) = σ(M)`.

### 10.5 Other laws

Functor identity/composition for `<$>`; applicative homomorphism/interchange/
identity; symmetry preservation:
`σ(C₁ <&> C₂) = σ(C₁) × σ(C₂)`, `σ(M <@> f) = OutputSymmetry(M.arrays, f)`.
`sequence : [Computation α] → Computation [α]` and `replicate : ℕ ×
Computation α → Computation [α]` (bootstrap/Monte Carlo) round out the
collection layer. Parallel associativity is exact in the flattened semantics;
parallel commutativity holds as a permutation; application does not commute.

## 11. Symmetry System

### 11.1 States

Per (array, dimension) position in a loop:

```
SymcomState = Neither | Symmetric | Commutative | Both
```

```
state(i, j) =
    sym = (j > 0) ∧ (σᵢ[j] = σᵢ[j−1])                       // within-array symmetry
    com = (i > 0) ∧ (cᵢ = cᵢ₋₁) ∧ (Aᵢ = Aᵢ₋₁)               // kernel comm + ARRAY IDENTITY
```

Commutativity yields triangular iteration only when the SAME array occupies
the commutative positions. **Array identity is required, full stop**: shared
index spaces (same named index types) with distinct arrays license nothing —
checked (`shared_units_insufficient`, proofs.md §Lowering). v10 §14.6's
"shared index spaces are the payoff" example is withdrawn.

### 11.2 The lowering law (exact)

Function commutativity (Level 2 symmetry) lowers to output array symmetry
(Level 1) via

```
lower₂₁(H) = H ∩ Stab(A₁, ..., Aₙ),   Stab = {σ ∈ Sₙ : ∀j. A_σ(j) = Aⱼ}
```

- Identical arrays: Stab = Sₙ → full transfer.
- Distinct arrays: Stab = {id} → no transfer.
- This is an **exactness**, not just soundness: the largest grant sound for
  every H-kernel and all data is exactly H ∩ Stab (a maximally symmetric
  kernel detects every stabilizer violation; a non-invariant kernel is
  distinguished by free data). A degenerate specific kernel may be
  accidentally more symmetric — remark, not hedge. (proofs.md §Completeness.)
- Sign-tracked variant: kernel anti-invariance ⇒ output antisymmetry
  (Hermitian = same statement with neg := conjugation). (proofs.md §Lowering.)

Input array symmetry, dually, is CONSUMED on read (`lower₁₀` is trivial —
index permutations become element identity); it does not propagate through
non-commutative kernels. Raising (`raise₀₁`, `raise₁₂` — symmetric arrays ARE
commutative access functions; deduced commutativity for kernels over
symmetric arrays) is subsumed in S/T by nominal typing + identity detection +
commutativity checking.

### 11.3 OutputSymmetry

```blade
OutputSymmetry(A₁...Aₙ, f) =
    groups = identity groups under c            // §8.3
    for each group: joint symmetry over the group's compound S-tuple (§8.4)
    reindex group contributions disjointly; append T-dim symmetry from f.tdim
```

The result guides index-type selection (§4.1 lowering table, extended by
`SymIdx<g, compound>` for multi-dim groups).

### 11.4 Alignment diagnostics

When declared commutativity cannot be exploited because structure prevents it
(same identity group but transposed or split dimension orders between
positions — impossible under literal identity, but reachable through views),
the compiler errors with a fix suggestion rather than silently iterating
rectangularly; `#[allow(unaligned_symmetry)]` suppresses.

## 12. Triangular Iteration and Storage

### 12.1 Bounds and left-justification

Within a symmetry group, iterate canonical tuples. Standard form (`j ≥ i`,
`k ≥ j`) and left-justified form (all loops from 0, bounds shrink:
`i₂ < n − i₀ − i₁`) cover the same canonical set; Blade uses
**left-justified** because iteration coordinates then EQUAL storage
coordinates — zero-overhead writes during bulk computation, with a coordinate
transform only for random access. (The literature default is the rising-bound
form plus per-access offset formulas; this choice is deliberate and
non-obvious.) The general left-justified storage bijection is proved
(proofs.md §DMWF; r = 2, 3 instances §Core; the affine descriptor unifies the
`lj` and strict/antisymmetric `alj` variants, δ = 0/1).

### 12.2 Access transform

Two phases, per symmetry group independently:

```
fold (canonicalize):  sort indices within the group          (5,2,7) → (2,5,7)
left-justify:         subtract predecessor within the group  (2,5,7) → (2,3,2)
```

`transformIndices = leftJustify ∘ foldIndices`; then direct storage indexing.
The rank-2 offset arithmetic is verified total/correct/in-range/injective
against a failure-model semantics, with typability alone discharging the
safety premises (proofs.md §Safety).

### 12.3 Cardinality and speedup

Unique elements of a rank-r symmetric space over extent N: C(N+r−1, r) ≈ Nʳ/r!
— the r! factor is the cube-to-simplex volume ratio. Iteration and storage
both shrink by r!.

### 12.4 Product symmetry — the corrected doctrine

Setting: one identity group of r identical arrays, each with d S-dimensions
(extents n₁, ..., n_d); commutative kernel.

**What holds (all machine-checked):**

1. **Diagonal group law**: the output is invariant under permuting whole
   argument index *tuples* — symmetry `SymIdx<r, compound(I₁ × ... × I_d)>`.
   Speedup r!.
2. **Per-dimension swap refuted**: permuting one dimension's indices across
   arguments independently is NOT an output symmetry (concrete
   counterexample).
3. **No lossless product layout** (general counting theorem): for d ≥ 2,
   nⱼ ≥ 2, r ≥ 2,
   `∏ⱼ C(nⱼ + r − 1, r) < C(∏ⱼ nⱼ + r − 1, r)` —
   per-dimension triangular factors cannot store the joint-symmetric output.
   Flattening to the compound space is forced in general.
4. **Cauchy storage split (r = 2 amendment)**: any jointly-symmetric T
   decomposes as 2T = Psym + Qalt with Psym carrying full S₂ × S₂ product
   symmetry and Qalt antisym ⊗ antisym; both components are per-dimension
   product-canonical; signed access through the two component stores is
   lossless; cell counts are exact (36 + 9 = 45 at 3×3). So r = 2 regains
   per-dimension product-STRUCTURED storage (SymIdx⊗SymIdx plus sign-tracked
   AntisymIdx⊗AntisymIdx) — the win is structure (per-dim layout, iteration,
   distribution), not fewer cells. r ≥ 3: open (mixed Schur components);
   the counting theorem is the shadow of the classical Cauchy decomposition,
   product storage being the leading term and Reynolds the projection onto it.

**What v10 claimed and is withdrawn**: independent S_r per data dimension for
one identity group, output `SymIdx<r, n>` per dimension, speedup (r!)^d
(v10 §14.5, §14.6, §10.9.5, and the (r!)^d framing in the abstract/intro).

**Where multiplicative speedups DO come from**: distinct identity groups.
k groups of sizes g₁, ..., g_k yield ∏ᵢ gᵢ! — block symmetry
(`SymIdx<g₁, ·>, SymIdx<g₂, ·>, ...`), sound because each factor permutes
whole argument slots of its own group. Reynolds adds its own factor (§5.3).

**Implementation status**: the v7 prototype implements the corrected lowering
(rewrite Phase 5, arc 1): a comm-covered identity group's plain-dense S-block
is fused into one compound loop level (`IR.fuseJointSLevels`), grouped jointly,
stored as `SymIdx<r, prod(n_j)>`, with per-dim coordinates decoded row-major inside
the loop; nominal index-space matching was removed as a symmetry license.
Value-pinned by `tests/corpus/symmetry/012-016`; the differential harness
asserts intentional divergence from pre-correction builds
(`DiffOracle.correctedSlice`).

### 12.5 Practical consequences

- Output index types for multi-dim identity groups are compound-joint
  (§8.4); storage is triangular over the compound extent ∏nⱼ.
- The JMCA-scale numbers change accordingly: rank-r comoments over flattened
  spatial grids get r! — which is what made the v7 prototype's numbers real,
  since v7 computed over flattened spatial indices.
- The r = 2 covariance-class regains per-dim structure via the split when
  layout/distribution wants it (file format, domain decomposition).
- Blade's per-dimension `SymIdx` types remain fully sound where the symmetry
  is genuinely per-dimension: declared symmetric data (§3.4), distinct
  identity groups (§12.4), Reynolds-symmetrized outputs per group.

## 13. Type System

### 13.1 Judgments

```
Δ ⊢ e : τ         expression typing
Δ ⊢ L : Loop[S]   loop with structure S
Δ ⊢ C : Comp[τ]   computation producing τ
```

### 13.2 Rules

```
Δ ⊢ T : BaseType   r ∈ ℕ   σ ∈ ℕʳ   ε ∈ ℕʳ
────────────────────────────────────────────  (Array-Intro)
Δ ⊢ array(T, r, σ, ε) : T^r(σ)

Δ, x₁:T₁^r₁, ..., xₙ:Tₙ^rₙ ⊢ body : T^r    metadata = (c, p, tdim) well-formed
────────────────────────────────────────────────────────────────  (Fun-Intro)
Δ ⊢ (fn(x₁...xₙ) where metadata -> T^r = body) : Function

Δ ⊢ Aᵢ : Tᵢ^rᵢ(σᵢ)   S = computeStructure(A₁...Aₙ)
────────────────────────────────────────────────  (MethodLoop-Intro)
Δ ⊢ method_for(A₁...Aₙ) : MethodLoop[S]

Δ ⊢ f : Function
─────────────────────────────  (ObjectLoop-Intro)
Δ ⊢ object_for(f) : ObjectLoop[f]

Δ ⊢ M : MethodLoop[S]   Δ ⊢ f : (T₁^r₁...Tₙ^rₙ) → T^r   compatible(S, f)
σ' = OutputSymmetry(M.arrays, f)   r' = S.dims + f.ORank
────────────────────────────────────────────────────────  (App-Method)
Δ ⊢ M <@> f : Comp[T^r'(σ')]

Δ ⊢ O : ObjectLoop[f]   Δ ⊢ Aᵢ : Tᵢ^rᵢ(σᵢ)   compatible(f, A₁...Aₙ)
S = computeStructure(A₁...Aₙ)   σ' = OutputSymmetry(A₁...Aₙ, f)   r' = S.dims + f.ORank
────────────────────────────────────────────────────────  (App-Object)
Δ ⊢ O <@> (A₁...Aₙ) : Comp[T^r'(σ')]

Δ ⊢ C₁ : Comp[α]   Δ ⊢ C₂ : Comp[β]
──────────────────────────────  (Parallel)
Δ ⊢ C₁ <&> C₂ : Comp[α × β]

Δ ⊢ M : MethodLoop[S]   Δ ⊢ M <@> f : Comp[α]   Δ ⊢ M <@> g : Comp[β]
──────────────────────────────────────────────  (Fusion)
Δ ⊢ (M <@> f) <&!> (M <@> g) : Comp[α × β]

Δ ⊢ C : Comp[α]   Δ ⊢ k : α → Comp[β]          Δ ⊢ v : α
────────────────────────────  (Bind)           ──────────────  (Pure)
Δ ⊢ C >>= k : Comp[β]                          Δ ⊢ pure v : Comp[α]
```

Arity-polymorphic application (§8.4) instantiates App-Method/App-Object with
r from input counting and σ' from identity groups; OutputSymmetry is the
computational form of §11.2's exact lowering law.

## 14. Operational Semantics

High-level model; full reduction rules and fusion-correctness proofs are
future work at the surface level (the materialized-value core is checked —
proofs.md §Compute).

### 14.1 Computation graph

Lazy until `|> compute`:

```
CompGraph = MethodLeaf(LoopSpec, Function) | ObjectLeaf(LoopSpec, Function)
          | Parallel(CompGraph, CompGraph, FusionDepth)
          | MethodFused(LoopSpec, [Function]) | ObjectFused(LoopSpec, [Function])
          | Bind(CompGraph, Value → CompGraph) | Pure(Value)
          | Choice(CompGraph, CompGraph) | Guard(Predicate, CompGraph)
```

The two leaves differ only in binding order; both lower to the same loop
structure.

### 14.2 Loop level types

```
LoopLevelType = { extent : ℕ, symcomState : SymcomState, parallelism : ParallelKind }
```

Two levels are fusable iff ALL components match — an OpenMP level and a serial
level are different types, as are Symmetric vs Commutative states and
different extents.

### 14.3 Fusion depth

`fusionDepth = longestCommonPrefix` of level-type lists; `<&>` fuses to that
depth and splits; `<&!>` requires full-depth equality (same MethodLoop).

### 14.4 Compute

```
compute(Pure v)              = v
compute(MethodLeaf(L, f))    = run L applying f
compute(Parallel(g₁,g₂,d))   = (compute g₁, compute g₂) fused to depth d
compute(*Fused(L, fs))       = run L applying all fs per point
compute(Bind(g, k))          = compute(k(compute g))
compute(Choice(g₁, g₂))      = compute g₁ <|> compute g₂
compute(Guard(p, g))         = p ? compute g : zero
```

At the value level this semantics is the loop-nest monad (bind = flat_map);
evaluation is a strict monoid homomorphism from plans, `V ∘ P = id`, and
evaluation is not injective (many plans, one value) — the checked core of the
S/T–T/S relationship (proofs.md §Compute, §TrinityAsym).

## 15. Concrete Syntax

### 15.1 Fundamentals

Newlines separate statements at top level and in blocks; ignored inside
`()`/`[]`/`{}`; consecutive newlines collapse; `;` optional. Bodies after `=`
(functions) or `->` (lambdas) are inline expressions unless `{` opens a block;
a block's final expression is its value.

### 15.2 Declarations

```blade
type LatIdx = Idx<180>                       // type aliases
type OceanIdx = CompoundIdx<ocean_mask>

let data: Array<Float like LatIdx, LonIdx, TimeIdx>
let cov:  Array<Float like SymIdx<2, n>>
let v = [1, 2, 3]                            // literals; nested for rank ≥ 2
let ragged = [[1, 2, 3], [4], [5, 6]]        // ragged literal → RaggedIdx
let empty : Array<Float like Idx<0>> = []    // empty needs annotation
```

No separate List type — static-size arrays suffice; dynamic collections are a
library concern.

### 15.3 Functions, lambdas, statics

§5 covers semantics. Grammar reminders: `where` before return type; `omp`/
`cuda`/`tdim` clauses; `lambda(args) -> body`; `static` values/functions;
`static type` functions; local `function` = `let const` lambda.

### 15.4 Control and data

- `match ... with | pat -> expr` (values, tuples, guards, sum-type payloads,
  brace blocks); match is an expression; `if c then a else b` is sugar for
  Bool match.
- Sequential recurrences: there is no imperative `for x in RANGE { body }`
  statement — it is expressed as a recursive array (structural induction on
  extent; see §7.5), with folds as `reduce(...)` and parallel maps as
  `method_for(range<...>) <@> lambda(...)`.
- Tuples: `(a, b)` literals; destructuring exact / wildcard / `head :: tail`;
  `()` unit; `(e)` is grouping, not a 1-tuple.
- Sum types: `type Option<T> = Some : T | None`; construction `Some(42)`;
  matched by payload pattern.
- Structs: named fields, construction `Point { x = 1.0, y = 2.0 }`, field
  access, destructuring, functional update `{ x = 3.0, ..p }`; dependent
  records (field bounds referencing earlier fields), constrained records
  (struct-level `where`), mutually constrained records (`type A = S and B = S
  where ...`, joint assignment) — all checked at construction.
- Interfaces: signatures only; `impl I for S { ... }`; interface composition
  `interface P : M, T { ... }`.
- Modules: `module` groups declarations; `import`/`from`/`as` reserved for
  multifile (in progress — future.md).

### 15.5 Loops and combinators

```blade
let loop = method_for(A, B)         let obj = object_for(f)
loop <@> f                          obj <@> (A, B)
for (A, B) in range<I> <@> lambda(i, j, a, b) -> ...
c₁ <&> c₂    (M<@>f) <&!> (M<@>g)    L₁ <*> L₂    o₁ >>@ o₂    c₁ @>> c₂
c >>= k      pure v     f <$> c      guard(p, c)  sequence [..]  replicate n c
c |> compute
```

### 15.6 Pseudo-native mathematics

Rank-0 collapse (§10.3) makes conventional notation sound without paradigm
commitment: `a + b`, `a * b`, `-a` lift elementwise over arrays at any rank
(the primitive is always elementwise; rank never changes its meaning);
`[+]`-family gives outer products; contractions are named functions built
from primitives (`sum`, `dot`, `matvec`, `matmul`). The S/T machinery stays
explicit at structure level (`method_for`, `comm`, `compute`); kernels read
pseudo-natively. Equivariance annotations flow through pseudo-native ops
(inference-failure ⇒ non-equivariant, not error).

### 15.7 Relational surface forms

`mask(A, p)`, `compound(A, m)`, `intersect/union/unique/contains`,
`group_keys/group_by`, `sort(A, key)`, `reduce(A[, k])`, `extents(A)` — all
call-shaped special forms; semantics in [features/sql.md](features/sql.md).

### 15.8 Named infix operators

`a :name: b` parses as `name(a, b)`; uniformly lowest precedence;
left-associative; parenthesize when mixing with native operators. Domain
notation without operator extension.

### 15.9 Assorted

Boolean ops `&& || !` (short-circuit; no keywords, no bitwise); fused
assignment `+= -= *= /=` including array elements; single-wildcard partial
application; sectioned operators `(+)`, `(/) x`.

---

## Appendix A: Notation summary

| Symbol | Meaning |
|--------|---------|
| `T^r(σ)` | array type: element T, rank r, symmetry σ |
| `method_for` / `object_for` | the two loop constructors |
| `()` / `zero` | empty array tuple (identity for `<*>`) / zero kernel |
| `<@>` `>>=` `<$>` `pure` | application, bind, map, lift |
| `<&>` `<&!>` | parallel composition, mandatory fusion |
| `<*>` | array product (MethodLoop concatenation; outer product on arrays) |
| `>>@` / `@>>` | compose-then-apply / apply-then-compose |
| `<\|>` / `<\|:>` | computation choice / array fallback |
| `zip` `align` `stencil` `stack` `transpose` `diag` `join` `subset` `split` `reverse` `shift` `decompact` | array combinators |
| `guard` `sequence` `replicate` | conditional / collection combinators |
| `\|> compute` | materialize |
| `comm(...)` `poly(args)` `arity` `nth` | commutativity, arity polymorphism |
| `omp(x: n)` `cuda` `tdim(...)` | backend/parallelism/T-dim clauses |
| `mask` `compound` `intersect` `union` `unique` `contains` `group_keys` `group_by` `sort` `reduce` `extents` | relational forms |
| `gram` `hermitian` `conj` | linear-algebra value operators |
| `reynolds(g[, Antisymmetric])` | symmetrizing kernel wrapper |
| `range<I>` `reverse<I>` `blocked<I,K>` | virtual arrays |
| `Nat<I>` | unit-tagged index value |

## Appendix B: Glossary

| Term | Definition |
|------|------------|
| Arity polymorphism | Input count determines output rank/depth/symmetry |
| Commutativity | Kernel argument interchangeability; licenses triangular iteration under array identity |
| Computation | Unevaluated loop application; materialized by `compute` |
| Diagonal (joint) symmetry | Invariance under permuting whole argument index tuples — the symmetry one identity group licenses |
| Dimensional currying | Partial indexing lowers rank; arrays as curried functions |
| Identity group | Maximal run of neighboring syntactically-identical loop arguments |
| Kernel | Function applied within a loop; receives values (T-world), not indices |
| Left-justified iteration | All loops start at 0 with shrinking bounds; iteration coords = storage coords |
| Loop object | Reified iteration pattern (`method_for` / `object_for`) |
| Product symmetry | Independent symmetry factors across *identity groups* (NOT across one group's data dimensions); multiplies factorial speedups |
| Residual compound | The compound index left after partially indexing a CompoundIdx |
| S-dimension / T-dimension | From iterating inputs / introduced by kernel output |
| SymcomState | Per-position symmetry/commutativity state (Neither/Symmetric/Commutative/Both) |
| Virtual array | Type-level iteration source; erases in codegen |
