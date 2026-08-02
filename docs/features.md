# Blade Features Catalog

Exhaustive list of Blade language features. This document is the census; semantics live
in [formalism.md](formalism.md) and the per-module feature docs.

---

## 1. Scalar values and primitive types

| Feature | Usage | Status | Description / Notes |
|---------|-------|--------|---------------------|
| Base numeric types | `Int32/Int64/Float32/Float64/`<br>`Complex64/Complex128` | Core | Double-check for exhaustiveness |
| Type variables | `A -> B -> ...` | Core | Same letter = same type in a signature |
| Complex conjugates | `conj(x)` | Core |  |
| Units of measure | `Unit meters`, `Float<velocity>`,<br> unit arithmetic | Core | Annotations on primitive types only |
| Bounded primitives | `Float<min=0, max=1>` | Planned | Runtime-checked bounds |
| Mutually constrained types | `type V1 ... and V2 ...`<br>`where <constraint>` | Core | Joint assignment required |
| Boolean Operators |  `&&`/`\|\|`/`!` | Core | |

## 2. Bindings, mutability, staticness

| Feature | Usage | Status | Description / Notes |
|---------|-------|--------|---------------------|
| Let-binding | `let static`/`let`/`let mut` | Core | `static` denotes "statically evaluable" and is conflated with "const" |
| Parameter borrowing | `x: T` immutable, `x: mut T` mutable | Core |  |
| `static` literals | `let static a = 5` | Core |  |
| `static function` |  | Core | compile-time evaluable; usable in type positions (`Idx<triangle(n)>`) |
| `static` parameters | `f(N : static Nat)`  | Planned | usable in return types |
| Static type functions | `static type Vec<N> = ...` | Planned | type-returning vs value-returning split keeps the type system decidable |
| Fused assignment | `+=`/`-=`/`*=`/`/=` | Core |  |

## 3. Arrays and array types

| Feature | Usage | Status | Description / Notes |
|---------|-------|--------|---------------------|
| Arrays as functions | `A(i, j) ≡ A(i)(j)` | Core | `Array<T, I, J> ≅ I → J → T` |
| Three-phase type model | abstract `T^r(σ)` =><br> index-typed `T^(I₁,...)` =><br> concrete `Array<V like I₁,...>` | Core |  |
| Array literals | `[1, 2, 3]` | Core | nested for rank ≥ 2 |
| Ragged literals | `[[1, 2, 3], [4, 5]]` | Core | Array literal rows of uneven length build `RaggedIdx`-typed arrays |
| Dimensional currying | `let A: T^3 = ...;`<br>`let B: T^2 = A(i)` | Core | Partial indexing yields lower rank; cache-optimality by construction |
| Poly-indexing | `A(indices)` with tuple;<br> `all_indices(A)` iteration | Core | Index multiple dimensions at once with a tuple of indices |
| Computational (lambda) indices | `Dual`, `Symbolic`, thunks | Speculative | v10 §5.5; structural vs computational index separation |
| Arrays of functions | `models(lat, lon)(params)(t)` | Core | Free mixing of functions and indexing |
| Extent tuples | `extents(A)` | Core | Static-first evaluation; rejects ragged/grouped dims with guidance |
| Mutation of array elements | `A(i) = v`, `A(i,j) += v` | Core | Allowed, but unidiomatic |

## 4. Array combinators (ArrayExpr layer)

| Feature | Usage | Status | Description / Notes |
|---------|-------|--------|---------------------|
| Array type lifting | `pure` | Core | Lifts an `Array` to an `ArrayExpr`. Usually implicit, but available explicitly. |
| Hard transpose | `transpose` | Core | Symmetric identity and antisym negation. |
| Zip | `zip(A, B, C)` (n-ary, tuple elements, symmetry intersection) | Core | Turns a tuple of arrays into an array of tuples. Checks symmetry intersection. |
| Outermost stack | `stack(A, B, C)` (new leftmost dimension, fresh symmetry class) | Core | Stack arrays along a new leftmost dimension. |
| Concatenation | `join(A, B, ..., d)` | Core | Concatenate arrays along dimension `d` |
| Segmentation | `split(A, d, i)` | Speculative | `A, B == split(join(A,B,d), d, i)` |
| Array fallback | `<\|:>` (nullptr-safe sparse access) | Planned | Partial-depth allocation in C++ codegen for non-final dims |
| Dimension decoupling | `decompact(A, n: Nat)` | Core | Expand symmetric/antisymmetric compact storage to dense along an axis |
|  | `diag`, `subset`, `split`, `reverse` (array op), `shift` | Speculative | v10 §3.6–3.7 |
|  | `align` / `stencil` (sugar) with `StencilSpec`, boundary modes | Speculative | v10 §3.6; kernel receives N separate args (vs zip's one tuple) |


## 5. Index types

| Feature | Usage | Status | Description / Notes |
|---------|-------|--------|---------------------|
| Simple index type | `Idx<n: Nat>` | Core | Index type of length `n` |
| Enumerated index type | `EnumIdx<S: Enum>` | Core | enumerated categories; string/sparse key domains. Also drives `group_keys` case 2 |
| Bounded index type | `BoundedIdx<l: Nat, u: Nat>` | Core | Internal use only. Erases to runtime bounds |
| Symmetric index type | `SymIdx<r:Nat, n:Nat>` | Core | `r` mutually symmetric dimensions of length `n` |
| Antisymmetric index type | `AntisymIdx<r, n>` | Core | `r` sign-tracked mutually antisymmetric dimensions |
| Hermitian index type | `HermitianIdx<n>` | Core | 2-D Hermitian index type. `A(i,j) = conj(A(j,i))` |
| Compound index type | `CompoundIdx<mask: bool^r>` | Core | `r`-dimensional sorted sparse index type ideal for relatively dense grids. Inherits dimensions from static `mask` array. |
| Ragged index type | `RaggedIdx<lengths>` | Core |  |
| Dependent index type | `DepIdx<I, f: Nat -> Idx<N>>` | Core |  Static function `f` maps each index of `I` to a new `Idx` |
| Equivariant index type | `EquivIdx<n, G, ρ>` | Planned? | group-representation-annotated indices |
| Sparse index type | `SparseIdx<entries>` | Planned | explicit valid-entry enumeration with hash-table storage; partly overlaps with `CompoundIdx` in practice |
| Nested/mixed symmetry | `NestedSymIdx` (elasticity),<br> `RiemannIdx` (curvature) | Speculative | v10 §4.15.2–4.15.3; cardinality formulas specified |

### 5a. Index type features

| Feature | Usage | Status | Description / Notes |
|---------|-------|--------|---------------------|
| Named index types | `type LatIdx = Idx<360>` | Core | Named index types are required for comparison. Unnamed index types are always considered distinct. |
| Index type tags | `Idx<n: Nat, Tag: String\|Enum>` | Core | String or enum-valued. Tags are for type comparison only. (Maybe redundant?) |
| Index type composition | `Sym<I,I>`, `Antisym<I,I>` | Planned | v10 §4.16.2 |
| Index transforms | `flip`, `rename`, `subset`, `align` | Speculative | v10 §4.8; all explicit, no implicit conversions |


## 6. Virtual Arrays

| Feature | Usage | Status | Description / Notes |
|---------|-------|--------|---------------------|
| Range | `range<Idx<n>>` | Core | Emit raw index as an array argument |
| Reverse | `reverse<Idx<n>>` | Core | Emit indices in reverse order |
| Halo | `halo<Idx<n>, [-1, 0, 1]>` | Core | Apply a rolling window centered at `0` |

## 7. Functions and kernels

| Feature | Status | Notes / sources |
|---------|--------|-----------------|
| Function declarations with `where` metadata: `comm(...)`, `omp(arg: depth)`, `cuda`, `tdim({extent, symm, name})` | Core | v10 §6.1–6.2; return type after `where` (may depend on constraints); `corpus/functions` |
| Commutativity groups → commutativity vector | Core | v10 §6.3 |
| Lambdas (`lambda(a, b) -> ...`), where-clauses on lambdas, array captures under nominal index typing | Core | v10 §6.2.1, §17.4; `corpus/functions` |
| Sectioned operators `(+)`, `(/) x`; single-wildcard partial application `f(_, y)` | Spec-only | v10 §6.2.2–6.2.3; multiple wildcards rejected by design |
| Nested `function` declarations (desugar to `let const` lambdas) | Core | v10 §17.3 |
| Reynolds operators — `reynolds(g)`, `reynolds(g, Antisymmetric)`, partial positions | Core (**clarified, arc 2**) | v10 §6.4; `corpus/reynolds` (23 tests incl. SQL composition, antisym cancellation, joint 2D over the fused path). The surface combinator is the VALUE-LEVEL wrapper (permutes kernel arguments; H = Sₙ by construction) — output symmetry still follows H ∩ Stab, so identity is required (dense output for distinct arrays, pinned by reynolds/013). The proof tower's per-dimension INDEX-LEVEL Reynolds (`reynolds_full_product_symmetry`, lossless canonical access) is a distinct prospective operator, not currently a surface construct |
| `gram` — Gram-matrix construction (dense / symmetric / Hermitian) | **v7-only** | `corpus/index-types` 066–069; differential oracle in `tests/Oracles.fs` (Gram-Hermitian was an oracle lesson); not in v10 |
| `hermitian` — adjoint operator | **v7-only** | `corpus/index-types` 070; not in v10 |
| `zero` kernel and zero-arity base cases (`f(())` = identity element) | Core | v10 §12.9, §10.4.7; `corpus/zero-combinators` (7 tests) |
| Arithmetic symmetry annotations (`(+)` Symmetric, `(-)` Antisymmetric, ...) driving comm inference | Core | v10 §7.1.2 |
| Elementwise vs outer operator pairs: `+` vs `[+]` (full table incl. comparisons and logical ops) | Core | v10 §7.1.1; `corpus/bracketed` (13 tests); `A [*] A` auto-triangular |
| Geometric primitives (`norm`, `dot`, `cross`) with equivariance signatures | Spec-only | v10 §7.2; equivariance layer is Near-term |
| Reductions `sum/mean/min/max` (rank-reducing; min/max invariant-only) | Core (sum via `reduce`) / Spec-only (equivariance rules) | v10 §7.3; v7 exposes `reduce` (see §10 below) |

## 8. Loop objects and iteration

| Feature | Usage | Status | Description / Notes |
|---------|-------|--------|---------------------|
| Loop combinators | `method_for(A₁...Aₙ)` <br> `object_for(f)` | Core | Builds a loop nest of deferred depth and type |
| S-dimensions |  | Core | S-dimensions derived from rank gap at kernel call. Rank gap = arg rank - parameter rank |
| T-dimensions |  | Core | Derived from kernel output dimensions |
| Virtual arrays | `range<I>`, `reverse<I>`, etc. | Core | Index type maps to yield or reorder indices. Behaves as an array with no content. |
| Anonymous ranges | `m..n` | Core | Shorthand equivalent to `range<Idx<n-m>> + m` |
| Multi-dimensional for-loops | `for (A, B) <@> ...` | Core | Shorthand for `object_for` and `method_for`; allows co-iterations with `in` |
| Co-iteration | `for (A, B) in range<I> <@> ...` | Core | Iterate elementwise over a shared index space. |


## 9. Polymorphism

| Feature | Usage | Status | Description / Notes |
|---------|-------|--------|---------------------|
| Rank polymorphism | `let mean1 = mean(A: T^2)`<br>`let mean2 = mean(B: T^3)` | Core | All kernels are implicitly rank-polymorphic according to S/T dimension classification |
| Arity polymorphism | `function moment(A: Poly<T^k>)` | Core | Stricter than variadic functions, arity-polymorphism guarantees well-typed functions returning different types depending on arity |
| Rank | `rank(A: T^r)` | Core | Static function; integer rank of array |
| Arity | `arity(A: Poly<T^k>)` | Core | Static function; integer arity of poly-pack |
| Poly-pack destructuring | `let (head, tail) = args` |, `args[k]`, `nth`; nested tuples; identity groups (neighboring identical arrays only) | Core | |
| Arg pack indexing | `args[k]` | Core | The `k`th element of poly-pack `args`. Also valid for general tuple arg packs. |
|  | `nth()` | Core |  |
| Type deduction workflow | T-dim match → identity groups → S-dims per group → concatenate | Core |  |

## 10. Combinator algebra

L aws are stated in the formalism; checked artifacts listed in [proofs.md](proofs.md) (BladeMonad,
BladeCompute). The MonadPlus laws hold exactly as stated — left zero, both identities,
LEFT distribution (plus right zero), and **right distribution provably fails** for
the computation monad (BladeMonad).

| Combinator |  | Role |
|-----------|--|------|
| Apply | `<@>` | Apply kernel to loop / arrays to object-loop |
| Pipe | `\|>` | Apply the preceding arg as the last argument of the subsequent arg; `f(a) == a \|> f` |
| Monadic | `>>=`, `pure`, `<$>` | Monadic bind, pure, functor. Computation monad (bind = loop-nest flat_map at the value level) |
| Loop join |`<&>` | Parallel composition with automatic prefix fusion |
| Force join | `<&!>` | Mandatory fusion; same-MethodLoop restriction |
| Product | `<*>` | Array product = MethodLoop concatenation; identity `method_for()` |
| Compose-apply | `>>@` | ObjectLoop (kernel) composition |
| Apply-compose | `@>>` | Within-MethodLoop sequential composition <br>Compose-apply duality `(o_f >>@ o_g) <@> A ≡ (m <@> f) @>> (m <@> g)` |
| Guard | `guard(p, c)` | Conditional computation; false → zeros of c's shape |
| Choice | `<\|>` | Choice; MonadPlus with `zero` |
| Collections | `sequence`, `replicate` |  |
| Compute | `\|> compute` | Materialization |

### 10a. Combinator Idioms

Some higher-order combinators are particularly useful for applying complex function iteratively.

| Idiom |  | Description |
|-----------|--|------|
| List compose | `object_for(>>)` | Compose each function sequentially |
| List apply | `object_for(<@>)` | Apply an array of functions to an array of arguments |
| Join all | `object_for(<&>)` | Loop-join an array of computations as much as possible |
| First choice | `object_for(<\|>)` | Select the first `true` computation |

## 11. Relational (SQL-like) operations — **v7-only, formalism gap now filled**

Full semantics in [features/sql.md](features/sql.md). All implemented and tested
(`corpus/sql-*`, 81 tests across 12 categories).

| Operation | SQL analogue | One-liner |
|-----------|--------------|-----------|
| `mask(A, pred)` | WHERE predicate | Bool presence array over A's own index space; combine with `&&`/`\|\|` |
| `compound(A, m)` | WHERE materialization | Compact CompoundIdx view; coordinate-based reads; cardinality = pass count |
| `intersect(A, B)` | INTERSECT | Value-based, dedups, first-occurrence order from A |
| `union(A, B)` | UNION | Dedups both sides, A's occurrences first |
| `unique(A)` | DISTINCT | First-occurrence dedup |
| `contains(A, x)` | IN / EXISTS | Membership; linear scan; safe on empty compounds |
| `compound(A, mask(A, x -> contains(B, x)))` | Semijoin | Idiom, multiplicity-preserving; hash fusion planned, not implemented |
| `... !contains(B, x)` | Antijoin | Idiom |
| `group_keys(k₁, k₂, ...)` | GROUP BY keys | CSR grouping structure; static (Idx / EnumIdx) and dynamic dispatch |
| `group_by(values, gk)` | GROUP BY | Rank-2 ragged result; per-group kernels/reduces; elementwise map rejected by design |
| `sort(A, keyFn)` | ORDER BY | Stable, key-extractor (not comparator); dense result |
| `reduce(A[, kernel[, init]])` | Aggregates | Default `(+)`; folds innermost dim; 3-arg init form seeds the fold and defines the empty result (landed, arc 4) — without init, statically-empty rejected and dynamic extents guarded |
| `extents(A)` | COUNT(*) | Cardinality on compound = post-WHERE count |
| Foreign keys | FK joins | Integer / EnumIdx arrays as references; capture-and-index idiom |


## 12. Symmetry system

| Feature | Status | Notes / sources |
|---------|--------|-----------------|

## 13. Data model

| Feature | Status | Notes / sources |
|---------|--------|-----------------|
| Tuples: literals, exact + wildcard destructuring, `head :: tail`, unit `()`; no positional access | Core | singleton collapse `(a) = a` |
| Structs (named fields, no methods); functional update `{ x = 3.0, ..p }` | Core |  |
| Dependent records (later fields' bounds depend on earlier fields) | Core | CGPath example |
| Constrained records (`where` clause on struct) | Core | checked at construction |
| Mutually constrained records (`type P1 = ... and P2 = ... where ...`) | Core | v10 §17.13.3 |
| Sum types / variants with payloads; `Option`, `Result` | Core | v10 §17.12; `corpus/sum-types` (7 tests) |
| Pattern matching (`match ... with`, guards, tuple patterns, sum-type payloads); `if/then/else` as sugar | Core | v10 §17.10 |
| Interfaces + `impl` (signatures only, no inheritance; interface composition) | Core | v10 §17.14; `corpus/interfaces` (4 tests) |
| Struct FK fields (`ETIndexRef`) | Core | `corpus/sql-foreign-keys` 006; [features/sql.md](features/sql.md) §12 |
| Type providers: `NetCDFProvider<"file.nc">` — file metadata → index types at compile time | Core | v10 §4.9; v7 `providers/`, `tests/NetcdfTests.fs`, `read` keyword; quasi-static file structure assumption |

## 14. Program structure and syntax

| Feature | Status | Notes / sources |
|---------|--------|-----------------|
| Newline statement separation, delimiter-aware; optional `;`; inline vs block bodies | Core | v10 §17.0 |
| Modules (`module` declarations) | **v7-only** | `corpus/modules` (2 tests); not described in v10 |
| Imports (`import` / `from` / `as` keywords) | Planned | keywords reserved in v7; `corpus/multifile` exists but empty — multifile compilation not landed |
| Pseudo-native mathematics (rank-0 collapse foundation; `A + B` needs no constructor commitment) | Core | v10 §17.18; foundation proved (rank-0 convergence, BladeCompute 12.2) |
| Named infix operators `a :name: b` (uniform lowest precedence) | Spec-only | v10 §17.19 |
| `print` / expression output | Core | v7 codegen; EXPECT-comment test convention |

## 15. Providers and I/O

| Feature | Status | Notes / sources |
|---------|--------|-----------------|
| NetCDF type provider (`NetCDFProvider<"f.nc">` → index types + typed arrays) | Core | v10 §4.9; v7 `providers/`, `NetcdfTests.fs`, `read` keyword, `sample.nc` |
| HDF5 / Zarr providers | Planned | provider interface slot (audit §4) |
| Triangular file format (block-aligned symmetric tensor I/O) | Planned | ext §2.7 |

## 16. Backends and performance

| Feature | Status | Notes / sources |
|---------|--------|-----------------|
| C++ codegen (via g++) | Core | v7 pipeline; EXPECT-based value tests |
| OpenMP parallelism via `omp(arg: depth)` clause | Core | v10 §6.1, §17.3; `tests/OmpTests.fs`; depth counts S-dim levels per argument, outermost first |
| CUDA backend via `cuda` clause (incl. simplicial/triangular kernels, split compilation) | Core | v7 `CudaTests.fs`, split-timing machinery; requires x64 Native Tools environment |
| Loop fusion analysis (fusion depth = common prefix of loop level types incl. parallelism annotations) | Core | v10 §16.2–16.3 |
| Lazy computation graph; `compute` semantics | Core | v10 §16.1, §16.4 |
| OpenBLAS lowering for `gram()` (`cblas_dsyrk` same-array / `cblas_dgemm` distinct) | Experimental | Real `Float64` only; opt-in via `BLADE_BLAS=1` (or `OPENBLAS_DIR` set; `BLADE_BLAS=0` forces off). Output layout unchanged (packed symmetric / dense) — BLAS fills a staging buffer, repack lands Blade storage. Emitted `#include <cblas.h>` keys Build.fs's `-I`/link resolution (`OPENBLAS_DIR`, netcdf-style). Measured: gram p=1024×n=16384, 8.6 s loops → 0.55 s 1-thread / 0.15 s 16-thread; values agree to ~1e-14 rel |
| Alternative parallel backends (`acc`, ...) | Planned | v10 §6.1 note |

## 17. Equivariance and ML (near-term module)

Spec draft: `blade_ml_spec_v10.md`; module doc: [features/equivariant-nn.md](features/equivariant-nn.md).
The core-language hook (annotation syntax + inference framework, v10 §8) is
Spec-only; domain rules live in libraries.

| Feature | Status | Notes / sources |
|---------|--------|-----------------|
| `with equiv(G, ρ)` / `with invariant(G)` annotations; inference through expressions; error detection (rep mismatch, equivariance breaking, wrong output rep, index/equiv incompatibility) | Near-term | v10 §8.2–8.4; zero runtime cost, type-checking only |
| Representations and irreps (`Rep`, parity, `2*L1o + 3*L2e` specs) | Near-term | ml-spec §2 |
| `IrrepsIdx<spec>` (block-structured primitive index type; flat-dense, spec-keyed nominal identity + nominative aliases; block-navigation statics `irreps_len/l/parity/mult/dim/offset`) | Core (v7) | ml-spec §3; module doc §6/§11b (the DepIdx equation is the semantic reading — DepIdx iteration codegen is NOT landed); corpus `index-types/111–119`, `ml-ops/005–008` |
| Clebsch-Gordan paths, `CGIdx` (via SparseIdx/constrained records), CG lookup | Near-term | ml-spec §4 |
| Tensor product operation (paths, weights, block indices) | Near-term | ml-spec §5 |
| Spherical harmonics | Near-term | ml-spec §6 |
| Equivariant tensor product / `Y_to` / linear / gated (compile-time elaboration, static specs, real-basis CG tables) | Core (v7) | module doc §11b; corpus `ml-ops/` + `ml-e2e/002`; norm activation + lmax>2 pending |
| Norm activation | Near-term | ml-spec §8.2 |
| Message passing: `scatter` / `gather` | Near-term | ml-spec §9 (expressible today as loops — `ml-e2e/00*` do; dedicated ops pending) |
| Reynolds applications: symmetric message passing, CG speedups, higher-order interactions, antisymmetric applications | Near-term | ml-spec §14 |
| Automatic differentiation (`grad`, reverse mode, v1 subset) | Core (v7) | AST-level source transform; module doc §11 has the ABI + subset; corpus `ad/` + `ml-e2e/`; remaining work listed in [features/equivariant-nn.md](features/equivariant-nn.md) §11 |

## 18. Graphs and trees (planned module)

Design drafts in ext §2.3–2.4; module doc: [features/graphs-trees.md](features/graphs-trees.md).

| Feature | Status |
|---------|--------|
| Tree structures (arrays as fixed-depth trees; path indexing) | Planned |
| Graph types via trace indices | Planned |
| Symmetric trees (commutative children) | Planned (speculative end) |

