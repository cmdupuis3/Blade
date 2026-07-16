# Blade Features Catalog

Exhaustive list of Blade language features. Each entry: what it is, its status,
and where it is specified and tested. This document is the census; semantics live
in [formalism.md](formalism.md) and the per-module feature docs.

**Status legend**

| Status | Meaning |
|--------|---------|
| **Core** | Specified in the formalism AND implemented in the v7 prototype (corpus category cited) |
| **v7-only** | Implemented and tested in v7 but missing from the v10 formalism — the gaps this catalog fills; now specified here / in feature docs |
| **Spec-only** | Specified in the v10 formalism, no v7 implementation found |
| **Near-term** | Designed (spec draft exists), implementation upcoming — notably the ML/equivariant module |
| **Planned** | Direction settled, design incomplete — details in [future.md](future.md) |

Speculative material (AD, domain decomposition, stencil/halo, symmetric trees,
triangular file format, non-deterministic iteration) is deliberately excluded here;
see [future.md](future.md).

Source citations: "v10 §n" = `blade_formalism_v10.md` section; `corpus/<dir>` =
v7 test category in `_blade-compiler/tests/corpus/`; `ml-spec §n` =
`blade_ml_spec_v10.md`; `ext §n` = `blade_extensions_v10.md`.

---

## 1. Scalar values and primitive types

| Feature | Status | Notes / sources |
|---------|--------|-----------------|
| Base numeric types `Int32/Int64/Float32/Float64/Complex64/Complex128` | Core | v10 §3.4.1; numeric promotion table §3.4.2 (float beats int, wider beats narrower, complex componentwise) |
| Type variables `A, B, ...` with `cast<A,B>` promotion | Core | v10 §3.4.3; same letter = same type within a signature |
| Complex literals and `conj(x)` | **v7-only** | `corpus/index-types` 060–065; conjugation on scalars and arrays; real identity `conj(x) = x` for real types. Not in v10 §7 operation tables |
| Units of measure (`Unit meters`, `Float<velocity>`, unit arithmetic) | Core | v10 §3.4.4; `corpus/units` (8 tests), `corpus/unit-errors`; annotations on primitives, not separate types |
| Bounded primitives `Float<min=0, max=1>` | Spec-only | v10 §3.4.5; runtime-checked bounds, composable with units |
| Mutually constrained types (`type V1 ... and V2 ... where <constraint>`) | Spec-only | v10 §3.4.6; joint assignment required; assertion semantics (crash on violation) |
| Booleans, comparisons, short-circuit `&&`/`||`/`!` | Core | v10 §17.8; no `and/or/not` keywords, no bitwise operators |

## 2. Bindings, mutability, staticness

| Feature | Status | Notes / sources |
|---------|--------|-----------------|
| Three binding forms: `let const` / `let` / `let mut` | Core | v10 §3.8; mutable-in-scope by default, `mut` required to pass to `mut` params; `corpus/mutability`, `corpus/mutability-errors` |
| Parameter borrowing (`x: T` immutable, `x: mut T` mutable, by-reference) | Core | v10 §17.7 |
| `static` values (compile-time constants) | Core | v10 §17.5; `corpus/static` (12 tests) |
| `static function` (compile-time evaluable; may capture only const/static) | Core | v10 §6.5; usable in type positions (`Idx<triangle(n)>`); no totality proofs required |
| `static` parameters (`N : static Nat`) usable in return types | Spec-only | v10 §17.7 |
| Static type functions (`static type Vec<N> = ...`) | Spec-only | v10 §17.6; type-returning vs value-returning split keeps the type system decidable |
| Fused assignment `+= -= *= /=`, incl. array elements | Core | v10 §17.9 |

## 3. Arrays and array types

| Feature | Status | Notes / sources |
|---------|--------|-----------------|
| Arrays as functions; indexing = application, `A(i, j) ≡ A(i)(j)` | Core | v10 §5.3, §4.2; the foundational isomorphism `Array<T, I, J> ≅ I → J → T` |
| Three-level type model: abstract `T^r(σ)` / index-typed `T^(I₁,...)` / concrete `Array<V like I₁,...>` | Core | v10 §5.1 |
| Array type identity (multi-index ≡ nested) | Core | v10 §5.2; symmetric index types are NOT nested-equivalent (curry to `BoundedIdx`) |
| Array literals `[1, 2, 3]`, nested for rank ≥ 2 | Core | v10 §17.2; `corpus/basic` |
| Ragged literals | **v7-only** | `corpus/index-types` 076–079; literal rows of uneven length build `RaggedIdx`-typed arrays |
| Dimensional currying (partial indexing yields lower rank; cache-optimality by construction) | Core | v10 §11; `corpus/loops` |
| Poly-indexing `A(indices)` with tuple; `all_indices(A)` iteration | Spec-only | v10 §5.4, §17.17; rank-polymorphic trace/sum examples |
| Computational (lambda) indices: `Dual`, `Symbolic`, thunks | Spec-only | v10 §5.5; structural vs computational index separation |
| Arrays of functions; intermixed indexing and application `models(lat, lon)(params)(t)` | Core | v10 §5.3; `corpus/func-arrays` (6 tests incl. 2D arrays of funcs, funcs returning arrays) |
| Tuple views over arrays (flat and structural) | **v7-only** | `corpus/tuple-views` (5 tests); not described in v10 |
| `extents(A)` — scalar for rank-1, tuple for rank-k; cardinality on compound | **v7-only** | See [features/sql.md](features/sql.md) §11; static-first evaluation; rejects ragged/grouped dims with guidance |
| Mutation of array elements (`A(i) = v`, `A(i,j) += v`) | Core | v10 §17.9 |

## 4. Array combinators (ArrayExpr layer)

`ArrayExpr` = unevaluated array transformation; implicit `pure` lift, explicit
`|> compute` materialization (v10 §3.5).

| Combinator | Status | Notes / sources |
|-----------|--------|-----------------|
| `zip` (n-ary, tuple elements, symmetry intersection) | Core | v10 §3.6; keyword present in v7; used by elementwise operator desugaring |
| `stack` (new leftmost dimension, fresh symmetry class) | Core | v10 §3.6; keyword present in v7 |
| `transpose` (hard transpose; permutation composition laws) | Core | v10 §3.6–3.7; `corpus/index-types` 027–033 incl. symmetric identity and antisym negation |
| `diag`, `join`, `subset`, `split`, `reverse` (array op), `shift` | Spec-only | v10 §3.6–3.7; no v7 keywords/tests found |
| `align` / `stencil` (sugar) with `StencilSpec`, boundary modes | Spec-only | v10 §3.6; kernel receives N separate args (vs zip's one tuple) |
| Array fallback `<\|:>` (nullptr-safe sparse access) | Spec-only | v10 §3.6, §11.7; partial-depth allocation is a C++-level API |
| `decompact` — expand symmetric/antisymmetric compact storage to dense along an axis | **v7-only** | `corpus/index-types` 034–049: sym and antisym sources, peel first/mid/last, chained to full dense, sign handling for antisym, reject on plain axes. Differential oracle exists (`tests/Oracles.fs`). Not in v10 |

## 5. Index types

The heart of the type system: an index type defines domain, cardinality, storage
bijection, and enumeration order (v10 §4.2).

| Feature | Status | Notes / sources |
|---------|--------|-----------------|
| `Idx<n>` (≡ `BoundedIdx<0, n>`) | Core | v10 §4.3, §4.13 |
| Named index types (`type LatIdx = Idx<360>`) — nominal identity + unit identity | Core | v10 §4.3.2; `corpus/index-types` 095–102 (alias chains, nominal iteration tags, explicit casts) |
| Tagged index types `Idx<n, Tag>` (enum tags; staggered grids) | Core | v10 §4.3.3 |
| Index values as unit-tagged naturals (`Nat<LatIdx>`); nominal bounds safety | Core | v10 §4.18; `corpus/index-types` 092–102; arithmetic preserves units; literals need explicit units |
| `EnumIdx<S>` (enumerated categories; string/sparse key domains) | Core | v10 §4.3; `corpus/sql-foreign-keys` 009–010; also drives `group_keys` Case 2 |
| `BoundedIdx<l, u>` (dependent, arises from currying symmetric indices) | Core | v10 §4.13; erases to runtime bounds |
| `SymIdx<r, n>` — sorted tuples, cardinality C(n+r−1, r) | Core | v10 §4.14.1; `corpus/symmetry`, `corpus/loops`; cardinality closed form is proved (BladeBinomial) |
| `AntisymIdx<r, n>` — strict tuples, cardinality C(n, r), sign tracking | Core | v10 §4.14.2; `corpus/index-types` 050–058 (iteration, kernels, Reynolds cancellation) |
| `HermitianIdx<n>` — conjugate symmetry, `A(i,j) = conj(A(j,i))` | Core | v10 §4.14.3; `corpus/index-types` 059, 066–071 incl. rectangular variant |
| `CompoundIdx<mask>` — mask-derived sparse compound; curryable `N → N → ...` signature | Core | v10 §4.4–4.5; `corpus/index-types` 001–017; whole-mask hash identity; O(1) lookup |
| `compound(dense, mask)` runtime builder — mask a dense array into a compact CompoundIdx view | **v7-only** | [features/sql.md](features/sql.md) §2; leading-prefix mask over dense dims collapses into one CompoundIdx axis; static `CompoundIdx<mask>` type path reserved but unexercised |
| Partial compound indexing with wildcards; **residual-compound representation** | **v7-only** (semantics), Core (syntax) | v10 §4.5 sketches; v7 implements fully: interior wildcards, trailing wildcards, chained partials, residual CompoundIdx results, reject cases (`corpus/index-types` 002–014). The residual of fixing coordinates in a mask is itself a compound (executable form: `has_completion`, BladeCompound) |
| `RaggedIdx<lengths>` — closed and opaque forms | Core | v10 §4.4, §4.7.2; `corpus/index-types` 018–026, 074, 080–088 (opaque reduce/extents/indexing, function params both forms) |
| `DepIdx<I, f>` — generalized dependent index | Core | v10 §4.7; `corpus/index-types` 072–075, 089–091, 099–100 (lambda + eta forms, triangular literal, per-row reduce) |
| `SparseIdx<entries>` — explicit valid-entry enumeration (CG triples, graph edges) | Spec-only | v10 §4.6; hash-table storage, wildcard queries; superseded in part by `compound`/CompoundIdx in practice |
| Nested/mixed symmetry: `NestedSymIdx` (elasticity), `RiemannIdx` (curvature) | Spec-only | v10 §4.15.2–4.15.3; cardinality formulas specified |
| Compositional index constructors `Sym<I,I>`, `Antisym<I,I>`, products | Spec-only | v10 §4.16.2 (three-tier system) |
| `unsafe indextype` escape hatch (`canonical` returns `Option`, `transform` on access) | Spec-only | v10 §4.16.3; `None` = implicit zero handles antisym diagonals |
| Index transforms: `flip`, `rename`, `subset`, `align` | Spec-only | v10 §4.8; all explicit, no implicit conversions |
| Structural matching (duck typing) across files: extent + tag + hash | Core | v10 §4.3.1; enables cross-file operations on same grid |
| Type providers: `NetCDFProvider<"file.nc">` — file metadata → index types at compile time | Core | v10 §4.9; v7 `providers/`, `tests/NetcdfTests.fs`, `read` keyword; quasi-static file structure assumption |
| `EquivIdx<n, G, ρ>` — group-representation-annotated indices | Near-term | v10 §4.15.4; foundation for the ML module, see [features/equivariant-nn.md](features/equivariant-nn.md) |

## 6. Functions and kernels

| Feature | Status | Notes / sources |
|---------|--------|-----------------|
| Function declarations with `where` metadata: `comm(...)`, `omp(arg: depth)`, `cuda`, `tdim({extent, symm, name})` | Core | v10 §6.1–6.2; return type after `where` (may depend on constraints); `corpus/functions` |
| Commutativity groups → commutativity vector | Core | v10 §6.3 |
| Lambdas (`lambda(a, b) -> ...`), where-clauses on lambdas, array captures under nominal index typing | Core | v10 §6.2.1, §17.4; `corpus/functions` |
| Sectioned operators `(+)`, `(/) x`; single-wildcard partial application `f(_, y)` | Spec-only | v10 §6.2.2–6.2.3; multiple wildcards rejected by design |
| Nested `function` declarations (desugar to `let const` lambdas) | Core | v10 §17.3 |
| Reynolds operators — `reynolds(g)`, `reynolds(g, Antisymmetric)`, partial positions | Core (**clarified, arc 2**) | v10 §6.4; `corpus/reynolds` (23 tests incl. SQL composition, antisym cancellation, joint 2D over the fused path). The surface combinator is the VALUE-LEVEL wrapper (permutes kernel arguments; H = Sₙ by construction) — output symmetry still follows H ∩ Stab, so identity is required (dense output for distinct arrays, pinned by reynolds/013). The proof tower's per-dimension INDEX-LEVEL Reynolds (`reynolds_full_product_symmetry`, lossless canonical access) is a distinct prospective operator — future.md |
| `gram` — Gram-matrix construction (dense / symmetric / Hermitian) | **v7-only** | `corpus/index-types` 066–069; differential oracle in `tests/Oracles.fs` (Gram-Hermitian was an oracle lesson); not in v10 |
| `hermitian` — adjoint operator | **v7-only** | `corpus/index-types` 070; not in v10 |
| `zero` kernel and zero-arity base cases (`f(())` = identity element) | Core | v10 §12.9, §10.4.7; `corpus/zero-combinators` (7 tests) |
| Arithmetic symmetry annotations (`(+)` Symmetric, `(-)` Antisymmetric, ...) driving comm inference | Core | v10 §7.1.2 |
| Elementwise vs outer operator pairs: `+` vs `[+]` (full table incl. comparisons and logical ops) | Core | v10 §7.1.1; `corpus/bracketed` (13 tests); `A [*] A` auto-triangular |
| Geometric primitives (`norm`, `dot`, `cross`) with equivariance signatures | Spec-only | v10 §7.2; equivariance layer is Near-term |
| Reductions `sum/mean/min/max` (rank-reducing; min/max invariant-only) | Core (sum via `reduce`) / Spec-only (equivariance rules) | v10 §7.3; v7 exposes `reduce` (see §10 below) |

## 7. Loop objects and iteration

| Feature | Status | Notes / sources |
|---------|--------|-----------------|
| `method_for(A₁...Aₙ)` / `object_for(f)` — the dual constructors | Core | v10 §9.1–9.5; `corpus/loops` (59 tests); uniqueness of the two maximal curryings is proved (BladeCurrying 9.26) |
| S-dimensions vs T-dimensions; `irank`; output rank = S + T | Core | v10 §9.2 |
| Virtual arrays: `range<I>`, `reverse<I>` | Core | v10 §9.8; erase completely in codegen |
| `blocked<I, K>` cache-blocked traversal | Spec-only | v10 §9.8; no v7 keyword |
| Anonymous ranges (zero-based, offset, literal) | **v7-only** | `corpus/anon-ranges` (4 tests); range forms without named index typedefs |
| `for` syntax (dual forms; `in` clause takes virtual arrays only; let-bound loops) | Core | v10 §9.9; `corpus/for-in` incl. poly-pack form |
| Arity polymorphism: `Poly<T^k>` kernels; arity determines output rank, nesting depth, symmetry | Core | v10 §10; `corpus/arity` (14 tests); distinct from rank polymorphism and from variadics (fixed output type) |
| Poly-pack destructuring `let (head, tail) = args`, `args[k]`, `arity`, `nth`; nested tuples; identity groups (neighboring identical arrays only) | Core | v10 §10.4, §10.7 |
| Kernel signatures live in T-world (kernels see slices, never S-dims) | Core | v10 §10.8 |
| Type deduction workflow (T-dim match → identity groups → S-dims per group → concatenate) | Core, **corrected** | v10 §10.9 — but the per-dimension `SymIdx` output for multi-dim arrays in one identity group ((r!)^d) is **refuted by the Coq tower**; a single identity group licenses joint symmetry over compound index tuples only. See formalism §12 and [proofs.md](proofs.md). Speedup table: r! per identity group, multiplying across groups |
| Virtual-array + real-array composition in one loop | Core | v10 §9.8.2 |
| Index emission into kernels via `range` (index anonymity preserved) | Core | v10 §4.18, quickstart p2 |

## 8. Dimensional currying

| Feature | Status | Notes / sources |
|---------|--------|-----------------|
| Type-level rank tracking (`promote<T, r>`); currying peels exactly one dimension | Core | v10 §11.2 |
| Cache-optimality by construction (non-optimal order = type error) | Core | v10 §11.3; dimension-alignment errors with `#[allow(unaligned_symmetry)]` escape (v10 §14.6.5) |
| Currying symmetric indices → dependent `BoundedIdx` / lower-rank `SymIdx` | Core | v10 §4.14.1 |
| Contiguity guarantee vs slicing views | Core | v10 §11.4 |

## 9. Combinator algebra

All Core (v10 §12; `corpus/guard-combinators`, `corpus/sequence-combinators`,
`corpus/replicate`, `corpus/zero-combinators`, `corpus/loops`). Laws are stated in
the formalism; checked artifacts listed in [proofs.md](proofs.md) (BladeMonad,
BladeCompute).

| Combinator | Role |
|-----------|------|
| `<@>` | Apply kernel to loop / arrays to object-loop |
| `>>=`, `pure`, `<$>` | Computation monad (bind = loop-nest flat_map at the value level) |
| `<&>` | Parallel composition with automatic prefix fusion (fusion depth = longest common prefix of loop level types) |
| `<&!>` | Mandatory fusion; same-MethodLoop restriction |
| `<*>` | Array product = MethodLoop concatenation; identity `method_for()`; fold enables runtime-arity loops; **shape concatenation, proved** (BladeTrinity) |
| `>>@` | ObjectLoop (kernel) composition |
| `@>>` | Within-MethodLoop sequential composition; Compose-Apply duality `(o_f >>@ o_g) <@> A ≡ (m <@> f) @>> (m <@> g)` proved (BladeCompute 12.1) |
| `guard(p, c)` | Conditional computation; false → zeros of c's shape |
| `<\|>` | Choice; MonadPlus with `zero` |
| `sequence`, `replicate` | Collection combinators (bootstrap/Monte Carlo) |
| `\|> compute` | Materialization |

**Corrected law note**: the MonadPlus laws hold exactly as stated — left zero, both
identities, LEFT distribution (plus right zero) — and **right distribution provably
fails** for the computation monad (BladeMonad). Do not assume it.

## 10. Relational (SQL-like) operations — **v7-only, formalism gap now filled**

Full semantics in [features/sql.md](features/sql.md). All implemented and tested
(`corpus/sql-*`, 81 tests across 12 categories).

| Operation | SQL analogue | One-liner |
|-----------|--------------|-----------|
| `mask(A, pred)` | WHERE predicate | Bool presence array over A's own index space; combine with `&&`/`||` |
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

Canonical SELECT-WHERE-ORDERBY: `sort(compound(temps, mask(temps, t -> t > 25.0)), t -> -t)`.

## 11. Symmetry system

| Feature | Status | Notes / sources |
|---------|--------|-----------------|
| SymcomState (Neither/Symmetric/Commutative/Both) per (array, dimension) | Core | v10 §13.1–13.2; array **identity** required for the Commutative state — shared index spaces are NOT sufficient (proved: `shared_units_insufficient`, BladeLowering; this **removes** v10 §14.6.2's middle example) |
| Output symmetry inference = lowering `lower₂₁(H) = H ∩ Stab` | Core, now **exact** | v10 §13.3; exactness (iff) proved — the largest sound grant is exactly H ∩ Stab (BladeCompleteness `license_exactness`) |
| Input symmetry consumed on read (`lower₁₀` trivial) | Core | v10 §9.13–9.14, §13.4; proved (BladeLowering) |
| Raising (`raise₀₁`, `raise₁₂`, deduced commutativity) | Core | v10 §9.6.6; proved (BladeLowering 9.16/9.18, BladeCompute 9.19/9.20) |
| Triangular iteration; left-justified indexing; two-phase fold/left-justify access transform | Core | v10 §14.1–14.3; `corpus/symmetry`; left-justified bijections proved at r=2,3 and generally (BladeCore, BladeDMWF); verified offset arithmetic + bounds safety at r=2 (BladeSafety) |
| **Product symmetry — corrected** | Core (corrected form; **implemented in v7, arc 1**) | The v10 (r!)^d claim for one identity group over d-dim arrays is refuted: only the **diagonal** (joint) swap is sound (`diagonal_group_law`); no lossless per-dimension product layout exists for r,d ≥ 2 (BladeCounting). Sound statement: r! speedup per identity group over compound index tuples; factors multiply across distinct groups. v7 now lowers this via fused joint levels (`corpus/symmetry` 012–016 value-pin it; shared-index-space licensing removed). At r=2 the **Cauchy split** recovers per-dimension product-canonical storage via sym⊗sym ⊕ antisym⊗antisym with exact cell accounting (BladeCauchy); r ≥ 3 open |
| Lex-order guarantee: iteration order = storage order = lexicographic | Core | proved once at the arrow level, inherited by enum/affine/compound instances (BladeLex) |
| Dimension alignment errors (shared dims must be adjacent) | Spec-only | v10 §14.6.5; retained as a diagnostic for the corrected identity-based detection |

## 12. Data model

| Feature | Status | Notes / sources |
|---------|--------|-----------------|
| Tuples: literals, exact + wildcard destructuring, `head :: tail`, unit `()`; no positional access | Core | v10 §17.11; singleton collapse `(a) = a` |
| Structs (named fields, no methods); functional update `{ x = 3.0, ..p }` | Core | v10 §17.13; `corpus/structs` (15), `corpus/struct-aborts` |
| Dependent records (later fields' bounds depend on earlier fields) | Spec-only | v10 §17.13.1 (CGPath example) |
| Constrained records (`where` clause on struct) | Spec-only | v10 §17.13.2; checked at construction |
| Mutually constrained records (`type P1 = ... and P2 = ... where ...`) | Spec-only | v10 §17.13.3 |
| Sum types / variants with payloads; `Option`, `Result` | Core | v10 §17.12; `corpus/sum-types` (7 tests) |
| Pattern matching (`match ... with`, guards, tuple patterns, sum-type payloads); `if/then/else` as sugar | Core | v10 §17.10 |
| Interfaces + `impl` (signatures only, no inheritance; interface composition) | Core | v10 §17.14; `corpus/interfaces` (4 tests) |
| Struct FK fields (`ETIndexRef`) | Core | `corpus/sql-foreign-keys` 006; [features/sql.md](features/sql.md) §12 |

## 13. Program structure and syntax

| Feature | Status | Notes / sources |
|---------|--------|-----------------|
| Newline statement separation, delimiter-aware; optional `;`; inline vs block bodies | Core | v10 §17.0 |
| Modules (`module` declarations) | **v7-only** | `corpus/modules` (2 tests); not described in v10 |
| Imports (`import` / `from` / `as` keywords) | Planned | keywords reserved in v7; `corpus/multifile` exists but empty — multifile compilation not landed |
| Pseudo-native mathematics (rank-0 collapse foundation; `A + B` needs no constructor commitment) | Core | v10 §17.18; foundation proved (rank-0 convergence, BladeCompute 12.2) |
| Named infix operators `a :name: b` (uniform lowest precedence) | Spec-only | v10 §17.19 |
| `print` / expression output | Core | v7 codegen; EXPECT-comment test convention |

## 14. Providers and I/O

| Feature | Status | Notes / sources |
|---------|--------|-----------------|
| NetCDF type provider (`NetCDFProvider<"f.nc">` → index types + typed arrays) | Core | v10 §4.9; v7 `providers/`, `NetcdfTests.fs`, `read` keyword, `sample.nc` |
| HDF5 / Zarr providers | Planned | provider interface slot (audit §4); [future.md](future.md) |
| Triangular file format (block-aligned symmetric tensor I/O) | Planned | ext §2.7; [future.md](future.md) |

## 15. Backends and performance

| Feature | Status | Notes / sources |
|---------|--------|-----------------|
| C++ codegen (via g++) | Core | v7 pipeline; EXPECT-based value tests |
| OpenMP parallelism via `omp(arg: depth)` clause | Core | v10 §6.1, §17.3; `tests/OmpTests.fs`; depth counts S-dim levels per argument, outermost first |
| CUDA backend via `cuda` clause (incl. simplicial/triangular kernels, split compilation) | Core | v7 `CudaTests.fs`, split-timing machinery; requires x64 Native Tools environment |
| Loop fusion analysis (fusion depth = common prefix of loop level types incl. parallelism annotations) | Core | v10 §16.2–16.3 |
| Lazy computation graph; `compute` semantics | Core | v10 §16.1, §16.4 |
| Alternative parallel backends (`acc`, ...) | Planned | v10 §6.1 note |

## 16. Equivariance and ML (near-term module)

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
| Automatic differentiation (`grad`, reverse mode, v1 subset) | Core (v7) | AST-level source transform; module doc §11 has the ABI + subset; corpus `ad/` + `ml-e2e/`; remaining work in [future.md](future.md) §2.1 |

## 17. Graphs and trees (planned module)

Design drafts in ext §2.3–2.4; module doc: [features/graphs-trees.md](features/graphs-trees.md).

| Feature | Status |
|---------|--------|
| Tree structures (arrays as fixed-depth trees; path indexing) | Planned |
| Graph types via trace indices | Planned |
| Symmetric trees (commutative children) | Planned (speculative end; see future.md) |

---

## Gap summary (what v10 was missing, now recorded)

Implemented-and-tested v7 features absent from the v10 formalism:

1. The entire relational suite (§10 above; 81 tests) — `mask`, `compound`,
   `intersect`, `union`, `unique`, `contains`, `group_keys`, `group_by`, `sort`,
   `reduce`, `extents`, FK idioms
2. `decompact` (compact → dense expansion with sign handling)
3. `gram` and `hermitian` (adjoint) operators
4. Complex literals and `conj`
5. Ragged literals
6. Modules; reserved `import`/`from`/`as`
7. Tuple views (flat/structural)
8. Anonymous ranges
9. Residual-compound partial indexing semantics (v10 §4.5 sketched; v7 pinned the
   full behavior incl. reject cases)
10. EnumIdx as element type / FK domain

Formalism claims corrected against the Coq tower (details in [proofs.md](proofs.md)):
product symmetry (joint, not per-dimension), shared-index-space symmetry (refuted;
identity required), H ∩ Stab exactness, MonadPlus right-distribution failure,
Trinity as generators + closure.
