# Blade Features Catalog

Exhaustive list of Blade language features. This document is the census; semantics live
in [formalism.md](formalism.md) and the per-module feature docs.

**Status vocabulary.** *Core*: implemented and pinned by a corpus category or test block
(`blade test <key>`). *Partial* / *Partly Done*: implemented with stated gaps. *Experimental*:
implemented behind an opt-in knob. *Planned*: designed, not built — writing it is an error
today. *Speculative*: an idea without a design commitment. Rows were re-verified against the
compiler (`blade check` / the corpus) on 2026-09-27; when a row and the corpus disagree, the
corpus wins.

---

## 1. Scalar values and primitive types

| Feature | Usage | Status | Description / Notes |
|---------|-------|--------|---------------------|
| Scalar types | `Int32/Int64/Float32/Float64/`<br>`Complex64/Complex128`, `Bool`, `String`, `Char`, `Nat`, `Void` | Core | `Int` = `Int32` (an integer literal is `Int64`), `Float` = `Double` = `Float64`; `Nat` is the index-value type (`Nat<I>` tagged, §5a) |
| Explicit numeric casts | `Float32(x)`, `Float64(extents(a))`,<br>`Int64(floor(x))`, `Complex128(r)` | Core | Scalar type name in call position; shadowable plain-call intrinsic. Complex→real refuses (BL3019: project with `real`/`imag`/`abs`/`arg`); float→int only through a floor/ceil visible at the cast site. A cast converts the element only: arrays (and generic `T` / `T^k` operands) keep their shape, symmetry and units. Nothing converts implicitly: an int/float mix in arithmetic or a comparison, an integer into a float parameter or binding, two non-literal widths of one class -- all BL3020 (a literal adapts within its class only) |
| Fused multiply-add | `fma(a, b, c)` | Core | `a*b + c` rounded ONCE; Float64 scalars; shadowable plain-call intrinsic. Its own IR node, so it is bit-identical across the compiled, interpreted and LLVM lanes under any `BLADE_FP_CONTRACT` (unlike `a*b + c`). The building block of error-free transformations / double-double arithmetic. AD: partials b, a, 1 |
| Type variables | `A -> B -> ...` | Core | Same letter = same type in a signature |
| Complex conjugates | `conj(x)` | Core |  |
| Units of measure | `Unit meters`, `Float<velocity>`,<br> unit arithmetic | Core | Annotations on primitive types only |
| Unit-carrying type variables | `T<time>^r`, `T<time>` | Core | The caret marks the head as a VARIABLE rather than a named type; `^0` is optional, so `T<time>` and `T<time>^0` are the same type (one lowering, so units, unification, monomorphization and diagnostics cannot drift between them). A misspelled unit is BL3015 in either spelling. A head naming a real type keeps its ordinary meaning |
| Concrete-element caret | `Float64^1`, `Int64^2`, `Bool^1`, `Speed^1` | Core | The left-hand side of `^` decides: a type gives an array type, a unit a unit power (`meters^2`), a value an arithmetic power (`Idx<N^2>`). A built-in scalar or a DECLARED type (alias, struct) under the caret is the array's ELEMENT, not a type variable: `Float64^1` is a dense rank-1 array of Float64 of any extent (an Int64 array is refused), `Speed^1` one of `Speed`, `Float64^0` is `Float64`; a declared head that is itself an array type is refused (BL1004). Same lowering as `Float<day>^1`. DENSE only: a compact argument (`SymIdx`, `AntisymIdx`, ...) is refused at the call (BL3001), never densified -- write `T^2` to accept any symmetry class. The rank must be an integer literal (BL1004); for any element write `T^1`. `Int^1` is Int32, as `Int` is everywhere, so an Int64 literal array does not fit it |
| Bounded primitives | `Float<min=0, max=1>`, `Float<psu, min=0.0>` | Core | Runtime-checked bounds (BL8001), composing with units; checked at a `let` annotation, an expression ascription `e : Bounded`, a function parameter (on entry) and return, including through a type alias |
| Mutually constrained types | `type V1 ... and V2 ...`<br>`where <constraint>` | Core | Joint assignment required |
| Boolean Operators |  `&&`/`\|\|`/`!` | Core | |

## 2. Bindings, mutability, staticness

| Feature | Usage | Status | Description / Notes |
|---------|-------|--------|---------------------|
| Let-binding | `let static`/`let`/`let mut` | Core | `static` denotes "statically evaluable" and is also the immutable tier — there is no separate `const` keyword |
| Parameter borrowing | `x: T` immutable, `x: mut Array<...>` mutable | Core | `mut` parameters are ARRAY-only; element writes alias the caller's array. A `mut` scalar parameter is BL4005 (return the value instead) |
| `static` literals | `let static a = 5` | Core |  |
| `static function` |  | Core | compile-time evaluable; reaches type positions through a `let static` (`let static m = triangle(n)`, then `Idx<m>`) |
| `static` parameters | `f(N : static Nat)`  | Planned | usable in return types |
| Static type functions | `static type Vec<N> = ...` | Planned | type-returning vs value-returning split keeps the type system decidable |
| Fused assignment | `+=`/`-=`/`*=`/`/=` | Core | Mutable variables only |

## 3. Arrays and array types

| Feature | Usage | Status | Description / Notes |
|---------|-------|--------|---------------------|
| Arrays as functions | `A(i, j) ≡ A(i)(j)` | Core | `Array<T, I, J> ≅ I → J → T` |
| Three-phase type model | abstract `T^r(σ)` =><br> index-typed `T^(I₁,...)` =><br> concrete `Array<V like I₁,...>` | Core |  |
| Array literals | `[1, 2, 3]` | Core | nested for rank ≥ 2 |
| Ragged literals | `[[1, 2, 3], [4, 5]]` | Core | Array literal rows of uneven length build `RaggedIdx`-typed arrays |
| Dimensional currying | `let A: T^3 = ...;`<br>`let B: T^2 = A(i)` | Core | Partial indexing yields lower rank; cache-optimality by construction |
| Poly-indexing | `A(indices)` with tuple;<br> `all_indices(A)` iteration | Planned | Index multiple dimensions at once with a tuple of indices (`all_indices` is unbound today) |
| Computational (lambda) indices | `Dual`, `Symbolic`, thunks | Speculative | v10 §5.5; structural vs computational index separation |
| Arrays of functions | `models(lat, lon)(params)(t)` | Core | Free mixing of functions and indexing |
| Extent tuples | `extents(A)` | Core | Static-first evaluation; rejects ragged/grouped dims with guidance |
| Mutation of array elements | `A(i) = v`, `A(i,j) += v` | Core | Allowed for mutable arrays, but unidiomatic |

## 4. Array combinators (ArrayExpr layer)

| Feature | Usage | Status | Description / Notes |
|---------|-------|--------|---------------------|
| Array type lifting | `pure` | Core | Lifts an `Array` to an `ArrayExpr`. Usually implicit, but available explicitly. |
| Hard transpose | `transpose` | Core | Symmetric identity and antisym negation. |
| Zip | `zip(A, B, C)` (n-ary, tuple elements, symmetry intersection) | Core | Turns a tuple of arrays into an array of tuples. Checks symmetry intersection. |
| Outermost stack | `stack(A, B, C)` (new leftmost dimension, fresh symmetry class) | Core | Stack arrays along a new leftmost dimension. |
| Concatenation | `join(A, B, ..., d)` | Core | Concatenate arrays along dimension `d` |
| Segmentation | `subset(A, d, (s, e))`, `split(A, d, i)` | Planned | `A, B == split(join(A,B,d), d, i)`; [plans/plan-subset-split.md](plans/plan-subset-split.md) |
| Array fallback | `<\|:>` (nullptr-safe sparse access) | Core | `A <\|:> B` reads `A` where allocated, else `B`; `corpus/fallback` |
| Dimension decoupling | `decompact(A, n: Nat)` | Core | Expand symmetric/antisymmetric compact storage to dense along an axis |
| Other array ops | `diag`, `reverse(A, d)` (array op), `shift` | Speculative | Unbound today; `reverse<I>` and `halo<I, ...>` virtual arrays cover the common cases |
| Stencil bundles | `align` / `stencil` (sugar) with `StencilSpec`, boundary modes | Speculative | Unbound today; neighbor access is `halo<I, [offsets]>` |


## 5. Index types

| Feature | Usage | Status | Description / Notes |
|---------|-------|--------|---------------------|
| Simple index type | `Idx<n: Nat>` | Core | Index type of length `n` |
| Enumerated index type | `EnumIdx<S: Enum>` | Core | enumerated categories; string/sparse key domains. Also drives `group_keys` case 2 |
| Bounded index type | `BoundedIdx<l: Nat, u: Nat>` | Core | Internal use only. Erases to runtime bounds |
| Symmetric index type | `SymIdx<r:Nat, n:Nat>`, `SymIdx<r, <I₁, …, I_d>>` | Core | `r` mutually symmetric dimensions of length `n`; over a product frame `<I₁, …, I_d>` (what a `comm` group over a d-dimensional array deduces, formalism §8.4) each position is a whole d-tuple: read with r·d per-dimension coordinates or r flat compound positions (formalism §3.2), nominative in its factors |
| Antisymmetric index type | `AntisymIdx<r, n>` | Core | `r` sign-tracked mutually antisymmetric dimensions |
| Hermitian index type | `HermitianIdx<n>` | Core | 2-D Hermitian index type. `A(i,j) = conj(A(j,i))` |
| Compound index type | `CompoundIdx<mask: bool^r>` | Core | `r`-dimensional sorted (lex-enumerated) semi-dense index type ideal for relatively dense grids; inherits dimensions from the `mask` array. |
| Ragged index type | `RaggedIdx<lengths>` | Core |  |
| Dependent index type | `DepIdx<I, f: Nat -> Idx<N>>` | Partial | The semantic model (formalism §3.6): `RaggedIdx`, `SymIdx` currying remainders and `IrrepsIdx` are its built instances; general user-written `DepIdx` iteration codegen is not landed |
| Equivariant index type | `EquivIdx<n, G, ρ>` | Planned? | group-representation-annotated indices |
| Enumerable constrained domain | `range<R>`, `Array<T like R>` over a `static struct R ... where ...` | Core (v1) | the struct's solution set as the iteration space, lex order, one positional kernel param per field; linear conjuncts (`i - j <= w`, `abs(..) <= w`, `l3 <= l1 + l2`, `m1 + m2 == m_out`) enumerate in CLOSED FORM (Fourier–Motzkin-projected difference constraints, affine level bounds; uncapped), anything else is tabled below the 100,000-cell box cap with a BL4010 advisory or refused BL4020 above it; `Int` and `Nat` fields (`let static` bounds and half-widths in `Int` (Int32) fields are a checked compile-time conversion; past Int32 is BL4001, use `Int64<...>`); SparseIdx-shaped output. CONSUMERS of a closed-form domain, all three lanes: a fold over the map (`reduce(method_for(range<R>) <@> k, op)`, top level or a function body) STREAMS the affine nest -- no array, no key table (C++: nested `for`s with affine `max`/`min` bounds, a licensed fold kernel as `omp simd reduction` on the innermost field; LLVM: the same nest; interpreter: the same plan); a materialized map (`|> compute`) is FILLED by the nest and keeps a sparse key view of the same lex order for keyed reads `R((i, j))`; `blade plan` prints a `[domain-enum v1]` line per consumer. A TABLE-enumerated domain's map cannot be folded unforced (BL7004 by name; LLVM refuses its range by name); force it first. Not yet: the threaded (`omp` parallel-for) form of a domain nest (the licence is spent on simd, a parallel request prints the omp census line), a closed-form keyed-read index in the C++ lane (it keeps the hashed view), static elision of the `(i : I)` cast checks inside a domain kernel (g++ folds them from the loop bounds); docs/plans/structural/06; corpus `index-types/260-267`, `domains/` |
| Sparse index type | `SparseIdx<keys>` | Core | explicit valid-key enumeration (rank-1 array of Nat tuples; `static` list or runtime tuple-array) with hash-table lookup. Tuple indexing with wildcards for partial gathers. Built with  `sparse(values, keys)` |
| Orbit (iterated-wreath) index type | `OrbIdx<[(r₁,s₁), ..., (r_d,s_d)], n>` | Partial | flat list of `(rank, ±)` levels, OUTERMOST-LAST, over one extent; group `S_{r₁} ≀ ... ≀ S_{r_d}` on `∏rᵢ` raw axes, character the product of the level signs. **Depth ≤ 1 is fully supported and is not a new type**: `[]` normalizes to `Idx<n>`, `[(r,+)]` to `SymIdx<r,n>`, `[(r,-)]` to `AntisymIdx<r,n>` — the same records, so the same storage, iteration and printing. Rank-1 levels drop at either sign. **Depth ≥ 2 is DEDUCED-ONLY**: a `comm` tie over a repeated compact argument produces the class — gated for soundness when the inner class carries a `-` level, where the tie additionally requires the kernel provably sign-odd in each argument (`h(-p,q) = -h(p,q)`, e.g. `p * q`; refused with BL4015 otherwise, `p + q` included) — and such a value is allocated (closed-form iterated binomial), filled by the segment-peeled `orb_visit` nest, printed, subscripted at any raw tuple `W(i,j,k,l)` (flat coordinates; a mirrored tuple returns the signed cell, a zero-set tuple returns 0), fully decompacted with `decompact(W, 0)`, and round-tripped through a Zarr store (the spec_version 2 `"orbit"` head over the flat canonical pool — [providers/ZarrTriangularSpec.md](../src/providers/ZarrTriangularSpec.md); depth-1 classes keep their `sym`/`antisym` spelling on disk). Still refused with BL4003: WRITING the class down as an annotation (a Zarr store is now a producer, but the annotation also admits classes nothing produces), `reduce`/`prodsum` over the pool (decompact first), partial subscripts, partial (per-level) decompaction, `transpose`, provider I/O outside Zarr. Design history: `git log -- docs/plans` (the orbit-index-type and OrbIdx-decompaction plans were retired once built); proofs in [proofs.md](proofs.md) |
| Nested/mixed symmetry | `NestedSymIdx` (elasticity),<br> `RiemannIdx` (curvature) | Speculative | Cardinality formulas specified. `RiemannIdx` is shorthand for `OrbIdx<[(2,-), (2,+)], n>` |

### 5a. Index type features

| Feature | Usage | Status | Description / Notes |
|---------|-------|--------|---------------------|
| Named index types | `type LatIdx = Idx<360>` | Core | Named index types are required for comparison. Unnamed index types are always considered distinct. |
| Index type tags | `Idx<n: Nat, Tag: String\|Enum>` | Planned | A second type argument does not parse today; NAMED index types (above) give the same nominal distinction |
| Index type composition | `Sym<I,I>`, `Antisym<I,I>` | Planned |  |
| Index transforms | `flip`, `rename`, `subset`, `align` | Speculative | All explicit, no implicit conversions; none is bound today |


## 6. Virtual Arrays

| Feature | Usage | Status | Description / Notes |
|---------|-------|--------|---------------------|
| Range | `range<Idx<n>>` | Core | Emit raw index as an array argument |
| Reverse | `reverse<Idx<n>>` | Core | Emit indices in reverse order |
| Halo | `halo<Idx<n>, [-1, 0, 1]>` | Core | Apply a rolling window centered at `0` |

## 7. Functions and kernels

| Feature | Status | Notes / sources |
|---------|--------|-----------------|
| Functions | Core |  |
| Commutativity groups → commutativity vector | Core |  |
| Lambdas (`lambda(a, b) -> ...`) | Core | Anonymous functions |
| Sectioned operators `(+)`, `(*)`, ... as kernels; single-wildcard partial application `f(_, y)`; curried under-application `f(5)` | Core | Multiple wildcards rejected by design; a section with a bound operand (`(/) x`) does not parse — write a lambda |
| Reynolds operators — `reynolds(g)`, `reynolds(g, Antisymmetric)` (partial `positions=[...]`: Planned) | Core | The surface combinator is the VALUE-LEVEL wrapper (permutes kernel arguments; H = Sₙ by construction) — output symmetry still follows H ∩ Stab, so identity is required (dense output for distinct arrays, pinned by reynolds/013). The proof tower's per-dimension INDEX-LEVEL Reynolds (`reynolds_full_product_symmetry`, lossless canonical access) is a distinct prospective operator, not currently a surface construct |
| `gram` — Gram-matrix construction (dense / symmetric / Hermitian) | Core | `corpus/index-types` 066–069; differential oracle in `tests/Oracles.fs` (Gram-Hermitian was an oracle lesson); not in v10 |
| `hermitian` — adjoint operator | Core | `corpus/index-types` 070; not in v10 |
| `gram_apply(A, B, x)` — the action of `gram(A, B)` on a vector without forming the Gram matrix (two rank-1 temporaries; BLAS route = transposed gemv + gemv; reverse-mode adjoint action is itself a `gram_apply`) | Core | `corpus/math` 077–082, `corpus/ad-jvp-comb` 109, emission and policy pins in `tests/LinAlgTests.fs`; docs/plans/structural/05 step 1 |
| `zero` kernel (monadic zero: zeros shaped by the loop's S-dims) | Core | In a recursive kernel's `\| 0 ->` arm `zero` is the zero value, not the surrounding operation's identity — write the identity literal (`\| 0 -> 1` for a product; `corpus/arity/024`). Identity resolution and the implicit `f(())` base case are Planned (a recursive `Poly` kernel with NO `\| 0 ->` arm fails IR validation today, BL6001). The zero VALUE is defined at every type that has one (formalism §10.4): String `""`, a struct fieldwise (nested structs, String/Bool/complex fields, static-shape array fields zero-filled), a tuple componentwise; a sum type has none and is refused (BL7004); `corpus/zero-combinators` 010–014 |
| Arithmetic symmetry annotations (`(+)` Symmetric, `(-)` Antisymmetric, ...) driving comm inference | Core | v10 §7.1.2 |
| Elementwise vs bracketed operators: `+` vs `[+]` | Core | Elementwise ops (e.g. `+` or `(+)`) provide sugar for `A op B = method_for(zip(A, B)) <@> op`, as opposed to bracketed ops, which use Blade-native outer-product spaces: `A [op] B = method_for(A, B) <@> op`. |
| Geometric primitives (`norm`, `dot`, `cross`, `sum`, `min`/`max`) with equivariance signatures | Planned | Unbound as core names today (`min`/`max` exist in static evaluation only); use `reduce`, `prodsum`, `stats.mean`, `m.matmul` |

## 8. Loop objects and iteration

| Feature | Usage | Status | Description / Notes |
|---------|-------|--------|---------------------|
| Loop combinators | `method_for(A₁...Aₙ)` <br> `object_for(f)` | Core | Builds a loop nest of deferred depth and type |
| S-dimensions |  | Core | S-dimensions derived from rank gap at kernel call. Rank gap = arg rank - parameter rank |
| T-dimensions |  | Core | Derived from kernel output dimensions |
| Virtual arrays | `range<I>`, `reverse<I>`, etc. | Core | Index type maps to yield or reorder indices. Behaves as an array with no content. |
| Anonymous ranges | `m..n` | Core | Half-open, equivalent to `range<Idx<n-m>> + m`; a first-class rank-1 array value: lifts elementwise (`x0 + dx * Float64(0..n)`), folds (`reduce(0..n, (+))`), materializes when bound bare or `\|> compute`d |
| Multi-dimensional for-loops | `(for (A, B)) <@> f`, `(for f) <@> (A, B)` | Core | Shorthand for `method_for` / `object_for`; allows co-iterations with `in`. The five forms: formalism §7.4 (the poly `for args in SymIdx<...>` form is Planned) |
| Co-iteration | `for (A, B) in range<I> <@> ...` | Core | Iterate elementwise over a shared index space. |


## 9. Polymorphism

| Feature | Usage | Status | Description / Notes |
|---------|-------|--------|---------------------|
| Rank polymorphism | `method_for(A) <@> mean` over any rank of `A` | Core | In KERNEL position every function is rank-polymorphic by S/T classification: `mean(row: T^1)` applied through a loop object consumes one axis and the loop supplies the rest. A DIRECT call does not broadcast or reduce rank (`mean(A)` on a rank-2 `A` is BL3001); scalar (`T^0`) functions called on arrays co-iterate elementwise |
| Arity polymorphism | `function moment(A: Poly<T^k>)` | Core | Stricter than variadic functions, arity-polymorphism guarantees well-typed functions returning different types depending on arity |
| Rank | `rank(A: T^r)` | Core | Static function; integer rank of array |
| Arity | `arity(A: Poly<T^k>)` | Core | Static function; integer arity of poly-pack |
| Poly-pack destructuring | `let head :: tail = args` | Core | Nested tuples; identity groups (neighboring identical arrays only). The tuple-pattern spelling `let (head, tail) = args` fails IR validation today (BL6001) |
| Arg pack indexing | `args[k]` | Core | The `k`th element of poly-pack `args`. Also valid for general tuple arg packs. |
| Recursion depth | `nth` | Planned | Refused today (BL3999; it used to evaluate to 0 at every depth); corpus `arity/056` |

## 10. Combinator algebra

Laws are stated in the formalism. The MonadPlus laws hold exactly as stated — left zero, both identities,
left distribution (plus right zero), and right distribution provably fails for
the computation monad.

| Combinator |  | Role |
|-----------|--|------|
| Apply | `<@>` | Apply kernel to loop / arrays to object-loop |
| Pipe | `\|>` | Apply the preceding arg as the last argument of the subsequent arg; `f(a) == a \|> f` |
| Monadic | `>>=`, `pure`, `<$>` | Monadic bind, pure, functor. Computation monad (bind = loop-nest flat_map at the value level). `<$>` is FUSED into the producing kernel before typecheck (see Compose-apply); `>>=` is not |
| Loop join |`<&>` | Parallel composition with automatic prefix fusion |
| Force join | `<&!>` | Mandatory fusion; same-MethodLoop restriction |
| Reduction join | `object_for(<&!>) <@> (r₁, …, r_k)`<br>`reduce([r₁, …, r_k], (<&!>))` | k REDUCTIONS (`prodsum`, `reduce`) in one traversal → `Tuple<k>`; per-leg fold and seed, so the legs may differ in both. Legs naming the same **deferred** map (`let ct = cos <@> ph`, no `compute`) evaluate it once per cell — sharing declared by the NAME. 1 leg = identity, 0 refused. THREADED (C++ lane, bench gap G7, `BLADE_JOIN_THREADS`): an explicit `where omp` on the join's (or a leg's) fold kernel, with EVERY leg's own reorder licence and a pure, rectangular, one-operator sweep, opens ONE Path B team over the shared traversal (identity-seeded private accumulators, one simd clause per chunk, ascending-chunk combine onto each seed); otherwise the census line names the first failed condition. [plans/structural/03-streaming-reductions.md](plans/structural/03-streaming-reductions.md) |
| Product | `<*>` | Array product = MethodLoop concatenation; identity `method_for()` |
| Compose-apply | `>>@` | ObjectLoop (kernel) composition. **Pipelines are FUSED** — `>>@`, `@>>` and `<$>` collapse into one map over the composed kernel before typecheck (`Grad.fuseProgram`), which is the proved duality below, so chains of any length, multi-operand first stages, and all three spellings inside a function body all emit. A fused kernel carries the first stage's `where` clause with its parameter references renamed, so `comm`-licensed triangular storage and `omp` licences survive. Stages fusion cannot prove (`reynolds(...)`, block bodies, unresolvable operands, a second-stage `where`) fall through to the staged compose path unchanged; in DIFFERENTIATED code the same declines become refusals |
| Apply-compose | `@>>` | Within-MethodLoop sequential composition <br>Compose-apply duality `(o_f >>@ o_g) <@> A ≡ (m <@> f) @>> (m <@> g)` |
| Guard | `guard(p, c)` | Conditional computation; false → zeros of c's shape |
| Choice | `<\|>` | Choice; MonadPlus with `zero` |
| Collections | `sequence`, `replicate` |  |
| Compute | `\|> compute` | Materialization |

### 10a. Combinator Idioms

Some higher-order combinators are particularly useful for applying complex function iteratively.

| Idiom |  | Description |
|-----------|--|------|
| List compose | `object_for(>>)` | Compose each function sequentially — **Planned**: refused today ("Unknown operator in object_for") |
| List apply | `object_for(<@>)` | Apply an array of functions to an array of arguments — Speculative (the former parses; list application is not built) |
| Join all | `object_for(<&>)` | Loop-join an array of computations as much as possible — Speculative, as above |
| First choice | `object_for(<\|>)` | Select the first `true` computation — Speculative, as above |

## 11. Relational (SQL-like) operations

Full semantics in [features/sql.md](features/sql.md). All implemented and tested
(`corpus/sql-*`, 142 tests across 13 categories; `blade test sql`).

| Operation | SQL analogue | One-liner |
|-----------|--------------|-----------|
| `mask(A, pred)` | WHERE predicate | Bool presence array over A's own index space; combine with `&&`/`\|\|`; rank 1, or rank k over a plain dense array (the whole index space; `compound(A, m)` compacts every dimension -- tests/corpus/sql-masks/013_rank2_mask_compound.blade) |
| `compound(A, m)` | WHERE materialization | Compact CompoundIdx view; coordinate-based reads; cardinality = pass count; prints its present cells in storage order (one flat run, nested rows with exactly one trailing dim -- G22; the interpreter twins it). REPRESENTATION (`blade plan`'s `[compound v1]` line, bench gap G9): a rank-1 mask is the COMPACTION -- a count pass, a branchless kept-position fill and a gather by position, no hash node per kept cell; a keyed read `S(k)` binary-searches the kept positions and a key that was not kept aborts BL8006 (corpus `index-types/511-514`). Both compiled lanes build it (the LLVM lane takes a rank-1, trailing-free compound and refuses every other); a rank-k mask keeps the hashed index (`declined -- hashed SparseIdx ...`). COMPACTION FUSION (`[compaction-fusion v1]`, gate `BLADE_COMPACTION_FUSION`, both lanes): a let-bound rank-1 `keep = mask(x, p)` with a pure, total predicate is counted in its own pass, the position fill and the gather become one branchless pass (same table, same BL8006 keyed reads), and `reduce(y * y, (+))` folds `y`'s compact buffer with no `y * y` pool -- the K-lane self-dot licensed, the unseeded serial chain (fed inside the compaction pass) under `BLADE_FP_REPRO=1`, BL8003 at the fold's line on an empty `y`; the LLVM lane emits the self-product with no fast-math flag in every form, so it never contracts into the add (the stored product's rounding); the fold motion and the in-pass feed each decline across any write between their two ends -- a mutating call, a write through an alias (corpus `index-types/515-521`). v2 (round 3, the default; `b4` = the round-2 form): the kept-position table is ELIDED when a fail-safe demand census certifies that every use reads only the compact buffer or the cardinality -- a keyed read, `<|:>`, an argument, an alias, a capture, a side-table operand (a random key, another compound's operand), another module's reference keeps it; `BLADE_COMPACTION_FUSION=gather` adds the opt-in mask-site gather (one sweep of x: per 256-cell block the predicate, then the gather; n + 1 private cells under `BLADE_TILE_TUNE` `sel_cap`; admitted by a five-party write check over each intervening binding's whole payload, a writing party keeping the round-2 pass) (corpus `index-types/522-531`, `multifile/037-040`) |
| `intersect(A, B)` | INTERSECT | Value-based, dedups, first-occurrence order from A |
| `union(A, B)` | UNION | Dedups both sides, A's occurrences first |
| `unique(A)` | DISTINCT | First-occurrence dedup |
| `contains(A, x)` | IN / EXISTS | Membership; linear scan; safe on empty compounds |
| `compound(A, mask(A, lambda(x) -> contains(B, x)))` | Semijoin | Idiom, multiplicity-preserving; hash fusion planned, not implemented |
| `... !contains(B, x)` | Antijoin | Idiom |
| `group_keys(k₁, k₂, ...)` | GROUP BY keys | CSR grouping structure; static (Idx / EnumIdx) and dynamic dispatch |
| `group_by(values, gk)` | GROUP BY | Rank-2 ragged result; per-group kernels/reduces; elementwise map rejected by design |
| `Chunked<I, K>` / `Chunked<s.index.d, store>` / `Chunked<s1.index.d, [[s1, store], [s2, 2]]>` | segmented axis | The alias IS the axis; a regular grid, a store-inherited grid (zarr), or stores tiling the dimension with their own grids. Coordinates never consulted. docs/plans/structural/07, sql.md 7c |
| `segments(A)` / `files(A)` / `segments(C0, C1)` / `segments(a)` | structural GROUP BY | Groupings with no key array and no permutation; `files` is the labelled file level of a tiled axis; the two-alias form is the tile grouping of a rank-2 array in slot order; over a value, the tiling its annotation declared. Statically evaluable for literal grids |
| `.stream` on `Chunked` axes (rank 1 and 2) | out-of-core consumers | `reduce` folds block by block in storage order (bitwise the flat fold), `group_by` under a structural grouping reads one run (rank 2: one tile window) per group, elementwise maps/binops/zips run one block or band of rows at a time, a halo map runs one segment at a time with its ghost cells; each recorded for `blade plan` (`segment-streaming`). Zarr today |
| `ungroup(G[, A])` / `ungroup([r1, ..], A)` | inverse of a structural grouping | Rows written back over the axis; the row form names a multi-store variable over a tiled axis. `join` untouched |
| `sort(A, keyFn)` | ORDER BY | Stable, key-extractor (not comparator); dense result |
| `reduce(A[, kernel[, init]][, axes = n])` | Aggregates | Default `(+)`; a LEFT fold in ascending storage order (`reduce([1, 2, 3, 4], lambda(a, b) -> a - b)` is `-8`), the innermost `n` axes with `n = 1` by default (rank k in, rank k−n out; `axes = rank(A)` is the full fold to a scalar — named slot, since the 3rd positional argument is the seed; `n` must be an integer literal, 1 ≤ n ≤ rank). 3-arg init form seeds EACH folded group and defines the empty result (landed, arc 4) — without init, statically-empty rejected and dynamic extents guarded |
| `extents(A)` | COUNT(*) | Cardinality on compound = post-WHERE count |
| Foreign keys | FK joins | Integer / EnumIdx arrays as references; capture-and-index idiom |


## 12. Symmetry system

| Feature | Status | Notes / sources |
|---------|--------|-----------------|
| Symmetric output deduction: `comm` kernel + the SAME array in the commuting positions → `SymIdx` storage and triangular iteration (joint over the compound S-space; distinct groups multiply) | Core | formalism §8.4, §11.2 (the H ∩ Stab law, machine-checked); `corpus/symmetry`, `corpus/arity` |
| `anticomm` → `AntisymIdx` (strict, sign-tracked, no stored diagonal) | Core | `corpus/symmetry`, `corpus/index-types` |
| `comm` claims checked: a body refuted by a sign law or a concrete counterexample is refused (§2.4 arithmetic, captured scalars sampled; `anticomm` also needs f(a, a) = 0 on the diagonal) | Core | BL4013; formalism §5.2 |
| Deduced-commutativity suggestions (`where comm(a, b)` pin proposals; storage stays dense until pinned) | Core | BL4010 warnings; `--strict-pins` makes them errors |
| Reynolds symmetrization (`reynolds(g[, Antisymmetric])`) | Core | §7 above; `corpus/reynolds` |
| Iterated-wreath (`OrbIdx`) deduction for repeated compact arguments | Partial | §5 above; BL4015 |
| Primitive symmetry annotations driving deduction (`(+)` symmetric, `(-)` antisymmetric) | Core | formalism §6.2 |
| Elementwise ops over compact operands: the sign-character rule (`*`/`/` multiply the characters, so `A * A` is symmetric and `A * S` antisymmetric; `A ± A` antisymmetric; `A ± S` and sign-blind ops dense; `S / A` dense for its x/0 diagonal); a class change gathers over the result's own range, a written zip over antisymmetric operands is certified by the kernel's JOINT sign parity | Core | formalism §8.5; G16 (`R * R` used to keep the antisymmetric class and negate every mirrored read); BL4015; `corpus/symmetry` 096–101 |

## 13. Data model

| Feature | Status | Notes / sources |
|---------|--------|-----------------|
| Tuples: literals, exact + wildcard destructuring, `head :: tail`, unit `()`, positional access `t[0]` | Core | singleton collapse `(a) = a`; bare commas on BOTH sides of a `let` (`let a, b = c, d`), parens optional; a pattern whose name count matches neither the top-level width nor the flattened leaf count is an error |
| Tuple annotations: `Tuple<N>` (width only, elements inferred) and `Tuple<T1, ..., Tk>` (components written) | Core | a lone integer literal is the width, any other list is a component list of width k >= 2; the two may not be mixed. `Tuple<A, B>` is the SAME type as the written `(A, B)`. Only written components are checked at the call (`argument i, component j`) and only they survive to codegen — the width-only form's element slots are inference variables nothing instantiates, so it cannot carry arrays or nested tuples |
| Structs (named fields, no methods); functional update `{ x = 3.0, ..p }` | Core |  |
| Dependent records (later fields' bounds depend on earlier fields) | Core | CGPath example |
| Constrained records (`where` clause on struct) | Core | checked at construction |
| Mutually constrained records (`type P1 = ... and P2 = ... where ...`) | Core |  |
| Sum types / variants with payloads; `Option`, `Result` | Core |  |
| Pattern matching (`match ... with`, guards, tuple patterns, sum-type payloads); `if/then/else` as sugar | Core |  |
| Interfaces + `impl` (signatures only, no inheritance; interface composition) | Core |  |
| Struct FK fields | Core | `corpus/sql-foreign-keys` 006; [features/sql.md](features/sql.md) §12 |
| Type providers | Core | `load` for identifier; lazy `read` with eager `write` for granular streaming I/O |

## 14. Program structure and syntax

| Feature | Status | Notes / sources |
|---------|--------|-----------------|
| Newline statement separation, delimiter-aware; optional `;`; inline vs block bodies | Core |  |
| Modules (`module` declarations) | Core |  |
| Imports (`import` / `from` / `as` keywords) | Core | dotted names resolve to files: `import units.SI` -> `units/SI.blade`, searched under the stdlib roots (`$BLADE_STDLIB`, then `stdlib/` beside the binary and upwards) then the importing file's directory; transitive, cycle- and duplicate-checked (`src/ModuleResolve.fs`, `blade test module-resolve`) |
| Standard library (`stdlib/`) | Partly Done | `units.SI` (the 7 SI base units + the coherent derived units; no prefixed/offset units — the unit algebra carries dimensions, not scale factors), `stats` (`mean` / `variance` / `stddev` over a row, in Float64), `plot` (`line`, `scatter`, `heatmap`, `contour`, `contourf`, `stream` display frames) |
| Pseudo-native mathematics (rank-0 collapse foundation; `A + B` needs no constructor commitment) | Core |  |
| Named infix operators `a :name: b` (uniform lowest precedence) | Core | `a :add: b` parses as `add(a, b)` |
| `print` / expression output | Core | EXPECT-comment test convention |

## 15. Providers and I/O

| Format | Status | Notes / sources |
|---------|--------|-----------------|
| NetCDF | Core |  |
| Zarr | Core |  |
| Missing values at the read boundary | Core | A default read refuses a variable holding cells its store marks missing (BL8015, every lane, the static fold included): NaN/±Inf in any floating variable, NetCDF `_FillValue` / `missing_value` / valid range / default fill; finite Zarr/Icechunk fills are values; provider writes refuse NaN/Inf too. Opt-in: the masked read `alias.read_present` (present cells as a compound array) / `alias.present` (the mask) — docs/features/sql.md §2a |
| Triangular Zarr file format | Partly Done | Provides Zarr storage spec for natively triangular or wreath-shaped tensors |
| Icechunk | Partly Done | Versioned (transactional) Zarr: `repo.checkout` with branch/tag/snapshot unit markers, compile-time snapshot pinning, cross-checkout axis identity. Writes, `read_window`, and packed-blocks reads pending. `.stream` and `load_compound` are REFUSED outright (`GenStreamOpen` / `GenReadCompoundVar` are `None`; the refusals are pinned in the icechunk test lane), not silently degraded — docs/plans/plan-icechunk-provider.md |

## 16. Backends and performance

| Feature | Status | Notes / sources |
|---------|--------|-----------------|
| C++ codegen (via g++, `-std=c++17`) | Core |  |
| Direct LLVM back end (`BLADE_LLVM=1`) | Experimental | whole-program or nothing (falls back to C++); `blade test llvm`; [plans/plan-llvm-backend.md](plans/plan-llvm-backend.md) |
| OpenMP parallelism via `omp(arg: depth)` clause | Core | depth counts S-dim levels per argument, outermost first, and the levels are the EXTERNAL ones the argument contributes to a nest built around the function (a caller's co-iteration) — a loop the body itself generates is licensed on that loop's own kernel; BL4001 warns on the confusion. A `Tuple<k>` parameter is one node: `omp(p: n)` licenses its first n rows (`comm`/`anticomm` on a tuple parameter stay refused) |
| CUDA backend via `cuda` clause | Core | requires x64 Native Tools environment |
| Lazy computation | Core | `compute` and `read` |
| OpenBLAS lowering for `gram()` (`cblas_?syrk`/`cblas_?herk` same-array / `cblas_?gemm` distinct) | Experimental | All four precisions `s`/`d`/`c`/`z` (complex same-array is a Hermitian `herk` rank-k update; distinct is `gemm` with `ConjTrans`); operands must agree in precision — mixed or unit-annotated element types keep the scalar loops; opt-in via `BLADE_BLAS=1` (or `OPENBLAS_DIR` set; `BLADE_BLAS=0` forces off). Output layout unchanged (packed symmetric / dense) — BLAS fills an unfilled staging buffer (beta = 0 overwrites), a per-row repack (threaded under an `omp` licence) lands Blade storage. A real f64 same-array gram at a shape Blade's packed syrk tile takes (literal extents, p >= 16, k >= 4) goes to the TILE instead under the default `BLADE_BLAS_PREFER` (`auto`/`native`; it beat OpenBLAS `dsyrk` at the bench's gram shape); `BLADE_BLAS_PREFER=library` (env or `blade.toolchain.json`, and `auto` with `BLADE_BLAS_FLAVOR=mkl`) keeps the library, and `blade plan` names the preference and its source. Emitted `#include <cblas.h>` keys Build.fs's `-I`/link resolution (`OPENBLAS_DIR`, netcdf-style). Measured: gram p=1024×n=16384, 8.6 s loops → 0.55 s 1-thread / 0.15 s 16-thread; values agree to ~1e-14 rel |
| Blade's packed-triangle tiles: same-array `comm` row contraction and `gram(A, A)` (syrk 6x8, `BLADE_SYRK_TILE`), the antisym pair (`BLADE_ANTISYM_TILE`), the rank-3 simplex (`BLADE_SIMPLEX_TILE`) | Core | One C arithmetic core (`src/cpp/blade_tiles.h`) under two drivers (`blade_packed_gemm.hpp` threaded, `blade_llvm_tiles.c` serial); each cell one ascending chain, bitwise the serial per-cell form under `BLADE_FP_CONTRACT=off` and the one-fma-per-step chain under `=fast` (`blade test syrk-tile`, `row-tile`, `llvm tiles`). Speed-only `BLADE_TILE_TUNE` keys, no bit moves: `syrk_g` (syrk row groups, default 4); `anti_kc` (antisym sample blocks); `anti_g` (antisym row groups: default per regime since round 3 -- 1, the previous order, for a serial tile (threads suppressed, no `omp` licence, a driver static work cap `(p * p * n) >> 20` below 2, or the LLVM lane), the cache budget for a tile ELIGIBLE for the driver's thread team (licence + thread emission + cap >= 2; the runtime team can still be one thread), both measured; `1..8` / `0` force; `0` = compile-time cache budget, G = 4 at n <= 512 for the 6x8, 2 up to 819, else 1; bytes pinned equal for G in {1, 2, 3, 4, 8}; corpus `symmetry/132`, its grouped twin in `blade test cli`); `tri_edge` (syrk / simplex edge tiles run only the accumulators with a real lane, default 1). `blade plan` names each in the fold form |
| Comm-licensed parallel reductions: bare `omp` on a fold kernel | Core | See below; `tests/OmpTests.fs` (`blade test omp-reduce`, `omp-pragma`, `omp-coverage`), corpus `loops/110–111`, `diagnostics/049` |
| Serial-emission build knob `BLADE_OMP_THREADS` | Core | See 16.0; `CodeGen.ompThreadEmissionEnabled` |
| Licensed folds fast by default; `BLADE_FP_REPRO=1` opts into serial association | Core (2026-10-17) | Every fold with a reorder licence (builtin `+`/`*` body, declared `comm`, `where omp`, `prodsum`, builtin reduction-join legs) emits its fastest form by DEFAULT: `omp simd reduction` / K-lane accumulators in the C++ lane -- including folds over DEFERRED computations (a composed `>>@` chain, a zip map, a checksum over a computed map) and `<&!>` joins / fused folds (one `BLADE_OMP_SIMD_REDUCTION(+:a, b, ...)` over every accumulator) -- and `reassoc nsz` in the LLVM lane, which now also contracts by default like g++. `where omp` implies simd: under `BLADE_OMP_THREADS=1` an omp fold vectorizes instead of falling to the serial chain. `BLADE_FP_REPRO=1` (or `BLADE_FP_REASSOC=0`) keeps the ascending serial association in every fold, threaded ones included (census line names the knob); contraction stays `BLADE_FP_CONTRACT`'s. `blade plan` reports `fold form: ...` per fold (rule `fold-form`). Still serial in every build: multi-level fold nests (no simd/lane form; Path B threads the outer level when teams are on), joins with a comm-declared or mixed-operator leg, the expression-position reduce's thread half. [plans/plan-fp-default-fastest.md](plans/plan-fp-default-fastest.md) |
| Threaded `let rec` step: `where omp(v: 1)` on the inductive arm's FINAL map runs the whole time loop on one persistent team (`BLADE_REC_STEP_OMP`) | Experimental (wave 3, E-G5) | The request names the x axis of the step; slice-write fusion keeps it and admits the step only with the proof that every trajectory read is the cell being written, of a row this worker wrote earlier (`prefix(n - c)`, literal `1 <= c <= lo`) or a seed row written before the loop, over a fresh rank-2 trajectory, with no write outside it (the shared `scheduleMayWrite` predicate) and no `where repro` callee spliced. C++ lane: ONE team, contiguous x ownership fixed for every step (`blade_fold::chunk_lo`), no per-step synchronization; a fused `prodsum(row, row)` row fold rides the team in a licensed build (per-worker chunk partials, the self-dot's K-lane form, combined ascending per row; no bit contract across team sizes) and is otherwise a strict post-pass of COMPLETE rows (rows in parallel only under the OUTER fold kernel's own `where omp`, else serially; the time chain in order; bitwise the serial program for every team size -- `blade test rec-step`). A request the fused loop cannot honour leaves the slice unfused (the map keeps its own per-step team); `BLADE_OMP_THREADS=1` prints the census line at the time loop; the LLVM lane and the interpreter run the sequential loop. Corpus `recursive-arrays/031-032`, `mutability-errors/042`; `blade plan` rule `rec-step-parallel v1` |
| Cell epilogues: a scalar consumer fold runs inside its producer's cell loop (`BLADE_CELL_EPILOGUE`) | Experimental (round 2) | Every named output stays stored; the fold stops re-reading it. B1, a dense rank-1 `\|> compute` producer whose cells a later `reduce(method_for(P) <@> lambda(w) -> w * w` (or `w`)`, F, seed)` folds: serial licensed = the `blade_fold` lane state fed behind each `epi_blk`-cell producer block (`feed_sq` / `finish_sq`: the same lane tree as `sq<T, 8>` over the same stored values), threads on with both kernels licensed = the producer runs in the fold's Path B team (one team instead of two, same `chunk_lo` chunks and thread-order combine), serial repro / unlicensed = the ordered chain advanced per cell behind its store (literal seed, builtin combine); the LLVM lane fuses the producer and the fold into one loop. B2r, a `prodsum(row, row)` row fold of a hot-row producer (stencil shape) or of a row-fold-fusion recursion (recur shape), in a build that keeps the serial association: the row's chain advances on each cell behind its store, both lanes. NUMERICS: the CONTRACT is bit identity with the two-pass form under `BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off` (the differential gates' regime). The C++ licensed forms reproduce the two-pass bits only GIVEN identical producer values (P1 fixes the squares' rounding, not the producer's: strip-mining can change contraction under `-ffp-contract=fast`) and the same ACTUAL team; `blade test cell-epilogue` checks exactly that by raw bits (scalars, producer pools, the observed team, T = 3 / 8 and a thread-limited team, both contraction modes) without making it a language guarantee. The LLVM licensed form folds in the vectorizer's own tree (the lanes' 1e-9 tolerance), and repro with fast contraction is held to a tested 1e-12 bound, not bits. Seeds and failures stay at the consumer's site (corpus `loops/483`); the hot-row planner never moves a row kernel ahead of a binding it reads or (unless it is the non-failing `prodsum(row, row)`) ahead of any binding between producer and fold (`loops/484`, `485`). `lanes` is RESERVED: today it is `on` plus a recorded B2l decline. `blade plan` rule `cell-epilogue v1` names the form or the refusal. Corpus `loops/480-485`, `recursive-arrays/030`; `BLADE_TILE_TUNE` keys `epi_blk` / `epi_blk_r` |
| Sibling-fold traversal sharing: sibling scalar folds over one dense literal-extent axis in one function body or binding block (`(kernel(a, b), reduce(a, (+)) + reduce(b, (+)))`, a pure-total scalar callee inlined one level, an existing `<&!>` join extended) become ONE let-bound reduction join -- one traversal, every leg its own seed, operand order and licence | Core (v1, `BLADE_SIBLING_FOLDS`, default on) | `Optimize.shareSiblingFoldsModule`, rule `sibling-folds` in `blade plan`. Pure-total legs only (a closed typed whitelist: typed float `/` admitted; integer `/` `%`, float->int casts, subscripts, lgamma, output, unknown or recursive callees decline), nothing that may write between them; unseeded builtin `(+)`/`(*)` legs seeded with their exact identity (`-0.0` / `1.0` / `0` / `1`, literal extent >= 1; formalism §2.4 on NaN state); declines when a leg requests threads and the threaded join (G7) would not take the group -- some leg unlicensed, the legs not all one builtin `+`/`*`, a leg's map outside the threaded join's own purity/totality whitelist (the SAME predicate the emitter applies, `Blade.IRTotality`; e.g. a negative integer power), or `BLADE_JOIN_THREADS=0` -- so a requesting fold never loses its own team to a join that would then run serial; when every condition holds the synthesized join keeps the leg's own request and threads, on a licence or operator disagreement in a reordering build, for a call whose statement may write (not inlined), a shared tree read by a structural projection, and a streamed provider operand, and leaves the exact rank-1 producer-epilogue shape (a let-bound `w` / `w * w` cell-map fold of a fresh map) to that epilogue. The joined result equals the separate folds' under serial/repro evaluation; a licensed build may reassociate, and its C++ join's simd lanes start at `+0.0` (the sign of an exactly-zero licensed sum is not kept, gap G40). `corpus/sibling-folds` 001–018; needles in `tests/OptimizeTests.fs` |
| Producer-input legs: a sibling join whose legs fold exactly the INPUT operands of a rank-1 `\|> compute` producer with a B1 cell epilogue (saxpy's `reduce(x, (+)) + reduce(y, (+))` beside `y2 = method_for(zip(x, y)) <@> k \|> compute`) rides the producer's loop instead of its own pass (`BLADE_EPILOGUE_LEGS`) | Experimental (round 3 A7c, gap G69) | `Optimize.producerEpilogueLegAdmission` (side table `EpilogueLegTable`, the join stays in the IR), rule `epilogue-legs v1` in `blade plan` with its five-party matrix (producer kernel / leg kernels / outer combine / seeds / intervening bindings, all through `scheduleMayWriteIn`, the producer's omp body also through `callbackTable`; captures and seed inputs judged transitively through every callee). Licences: strict under repro; builtin-licensed otherwise (a builtin `(+)` / `(*)` leg carries its own licence; a leg with none, `a - b`, stays strict). A strict leg is its own ascending chain from its own seed, fed by the operand value the producer loop loads: bitwise the joined pass's chain (raw bits, both lanes); a licensed leg is its own lane state in the C++ lane (`blade_fold::sum`'s tree -- a licensed association change against the joined pass's `omp simd reduction`, agreement at 1e-12 TESTED on the fixtures only) and its own fold flags in the LLVM lane. Results are bound at the join's own statement, so a failure in an intervening binding still comes first (`sibling-folds/026`). A callee holding the producer (saxpy's `kernel(x, y)`) is inlined one level only for an admitted plan that the planner's partial mirror of the lanes' preconditions accepts (an emitter can still decline and record it); the C++ lane releases the callee's pools once per inlined call at a return boundary recorded in a side table -- behind the inlined spine's evaluated scalar return, wherever the joins land (`030`, `031`) -- but that boundary is KNOWN INCOMPLETE (G69; the third Codex diff look and the wave-2 merge-level review): the join rewrite can delete the boundary let, CSE runs after the table and can merge a caller's equal map into the released pool or drop the key, function-body return lets bypass the renderer arm, and the LLVM lane -- which frees an outlined callee's pools at its scalar return -- retains them under the inline (its emitted "frees no pool" record is wrong in general); round 4 replaces the table with an IR-level cleanup event honoured by every pass, renderer and lane. A callee that may fail is never inlined and keeps its frame (`029`). Operands must be fresh storage (an alias or named view declines, `028`). Declines: a threaded producer (strict legs never split per thread), a leg kernel requesting omp in a threads build, a leg over the producer's OUTPUT (B1 owns it), a leg capture or seed bound after the producer, an intervening write, no lane able to carry the legs (a non-literal B1 seed, BLADE_FOLD_LANES=off under a licence), a non-total or writing leg. Corpus `sibling-folds/019-031`; needles in `tests/OptimizeTests.fs`; raw bits, fault texts and ASan release census in `blade test cell-epilogue` |
| Print selection (`blade run --print a,b` / `BLADE_PRINT`) | Core | the compiled program prints only the named top-level bindings instead of every one, in both lanes (the llvm lane honours it too, handing a selection it would not print to the C++ lane for the BL7004 text -- G12); an unknown name refuses (BL7004). What a program prints is its own cost at scale: a 4000 x 4000 map printed 231 MB and took 31 s, of which 28 s was the printing. For benchmarking, large arrays, and pipelines whose inputs are not the answer. `CodeGenState.printSelection`, `CodeGen.genPrintStatements`, `Interp/Run`; pins in `blade test cli` |
| Revision reuse: tile cache across Icechunk snapshots (`BLADE_TILE_CACHE=<dir>`; a pure elementwise map / co-iteration over whole-variable dense icechunk reads runs one leading-axis tile at a time, each tile keyed by the task text, the output geometry and the content identities of exactly the chunks it reads; a warm run skips the recompute and, with the probe hoisted before the read, the chunk reads of every unchanged tile; cold = warm = interpreter stdout; an input nothing observes but the tiled nest -- with `--print` selecting away the array itself -- never fetches the chunks its hit tiles would have needed, which is where the saving becomes real: 2.5x warm over untiled on a 4000 x 4000 field with a transcendental kernel) | Core (v1, opt-in) | `src/CodeGenTiles.fs`, `src/cpp/blade_tilecache.hpp`, `ZarrProvider.genAssembleFlatPhased`; docs/plans/structural/04; `tests/IcechunkTests.fs` §22; `BLADE_TILE_CACHE_VERBOSE=1` prints the `[tiles]`/`[chunks]` census |
| Input manifests + run records (`blade plan` lists every provider read and compile-time fold with element type, axes, storage and identity policy -- `content` hash for folds, `version` size+mtime for runtime reads; `BLADE_RUN_RECORD=<path>` / `blade run --run-record` makes the executable write a JSON record at exit: status incl. BLxxxx aborts, observed inputs, executable + toolchain identity, march/fp policy and linked routes via `-DBLADE_RR_*`, RNG generator) | Core | `src/RunRecord.fs`, `src/cpp/blade_run_record.hpp`; plan-fortran-killer-2 §7; `tests/RunRecordTests.fs` (`blade test run-record`) |

### 16.0 `BLADE_OMP_THREADS` — spending the licence per deployment

`omp` / `omp(a: n)` in source is a **licence**: a statement about the kernel
(which dimensions are safe to thread, which folds are safe to reassociate). It
is part of the program. Whether a given **build** spends that licence is a
property of the machine the binary will run on, and that is what this
environment variable decides. One source, per-deployment builds.

| `BLADE_OMP_THREADS` | Emission |
|---|---|
| unset, or `2`, `8`, … , or anything unparseable | **Default.** Thread pragmas are emitted wherever the source licensed them; the degree of parallelism remains the runtime's `OMP_NUM_THREADS` |
| `1`, `0`, `off` | **Serial emission.** Every thread-level construct is suppressed — `parallel for`, `collapse`, `schedule(dynamic)`, the reductions' explicit teams, and the native `gram`/`matmul` threading macros |

**Why it is not the same as `OMP_NUM_THREADS=1` at runtime.** GCC outlines a
`parallel for` body into a separate function called through the OpenMP runtime;
that is a *compile-time* decision whose cost is paid even when the team turns
out to hold one thread. Measured on a licensed row map:

| | |
|---|---|
| pragma emitted, `OMP_NUM_THREADS=1` | 488 µs |
| **no pragma emitted** | **263 µs** (1.86× faster) |
| pragma emitted, multi-threaded | 187 µs (parity with hand-written C++) |

**`omp simd` is never suppressed.** A `simd` construct creates no team and is
not outlined, so it costs nothing at one thread. Sites that emitted
`parallel for simd` therefore drop to `omp simd`, and the Path A reduction below
drops to `omp simd reduction(<op>:acc)` — the thread half goes, the vector half
stays.

**Guarantees.** Serial emission changes *what is emitted*, never *which programs
compile*, and never the values of a MAP: a suppressed map nest is byte-identical
to the same program with the clause deleted. A licensed FOLD keeps spending its
reorder licence in the vector half (`where omp` implies simd: Path A drops to
`omp simd reduction`, a Path B fold over a one-level deferred computation takes
the `omp simd reduction` / K-lane form, a dot-class one the explicit K-lane
vector tree of `blade_fold_lanes.hpp`), so its last bits may differ from the
threaded build's -- both are the serial fold up to the licensed reassociation
(and since 2026-10-19 the threaded build runs the same fast form INSIDE each
chunk: the vector tree per chunk for a dot-class body, `omp simd reduction` per
chunk for any other builtin `+`/`*` body, `BLADE_FOLD_LANES`);
`BLADE_FP_REPRO=1` is the build that keeps the serial association. Each decline
leaves a
`// [omp] requested but emitted serial: BLADE_OMP_THREADS=…` line in the
generated C++, so a dropped clause is never silent. A numeric value ≥ 2 does
**not** bake `num_threads(n)` into the binary — v1 leaves the degree to the
runtime deliberately, since Path B's determinism contract is stated in terms of
the team size it actually gets. `-fopenmp` stays on the compile line in both
modes (`omp simd` needs it). MPI is out of scope: under `where mpi, omp(...)`
only the `omp` half is gated.

### 16.1 Parallel reductions (`reduce(xs, k)` with `omp` on `k`)

`reduce` is serial by default, like every other loop in Blade. It parallelizes
when the **fold kernel** carries `omp` in its where-clause:

```blade
let xs = [1.0, 2.0, 3.0, 4.0]
let s1 = reduce(xs, lambda(a, b) where omp -> a + b)                         // builtin body
let s2 = reduce(xs, lambda(a, b) where comm(a, b), omp -> a + b + a * b)     // declared comm
function myAdd(a: Float64, b: Float64) where comm(a, b), omp -> Float64 = a + b
let s3 = reduce(xs, myAdd)                                                   // named, same rule
// EXPECT: s1 = 10.0
// EXPECT: s3 = 10.0
```

`omp` here is the **bare** form (no parentheses): a fold walks one axis, so
there is no per-argument depth to name — the only question is whether the axis
may be reordered. The parenthesised `omp(a: n)` form is unchanged and still
means "up to n dimensions of argument `a` may carry threads" for map kernels.

**The licence.** Chunking a fold hands different associations and different
orders to different threads, so the kernel must be commutative *and*
associative. Two things grant it:

- **`comm(a, b)` declared on the kernel** — the same word `<@>` uses to opt into
  symmetric storage, and already cross-checked against the body's deduced parity
  (a provably antisymmetric body is `BL4013`, not a licence).
- **A recognised builtin body** — exactly `a + b`, `a * b`, `a && b`, `a || b`
  over the two parameters. These carry both properties outright and need nothing
  declared.

`omp` on a fold kernel with **neither** is `BL4016`, a hard error, not a silent
serial fallback: "asked and got serial" and "never asked" emit byte-identical
C++, so a dropped clause would be invisible.

**Two emission paths.**

| | When | Emitted shape |
|---|---|---|
| **A** | builtin `+`/`*` body over a real arithmetic element type, flat 1-D sweep (dense rank-1, or a CompoundIdx/SparseIdx `.data` walk) | one `#pragma omp parallel for simd reduction(<op>:acc)`; the accumulator is seeded exactly as the serial fold seeds it and the original value participates in the combine |
| **B** | every other licensed kernel, and every reduce over a deferred computation | an explicit team with contiguous per-thread chunks, each seeded from its own first value, combined in thread order through the fold wrapper. Needs no identity element, so any user kernel works |

Path B over a deferred computation chunks the **outermost** loop level only:
each thread runs the whole inner nest serially over the outer indices it owns,
so an inner triangular level is correct without any special handling. It also
materializes no intermediate array.

**Caveat: the reorder is real.** `comm` is checked; **associativity is not** —
no deduction establishes it, so `omp` on a fold kernel is your explicit reorder
licence, the same trust model as `comm`'s escape hatch. In floating point,
addition is not associative, so a parallel fold can differ from the serial one
in the last ULPs. Path B is deterministic for a fixed `OMP_NUM_THREADS` (chunk
boundaries and combine order are fixed functions of the team size); Path A hands
the combine to the OpenMP runtime, whose order the standard leaves unspecified.
Integer-valued data is exact under every association and reproduces bit-for-bit
either way.

**Not parallelized (deliberately):** the expression form of `reduce` (an IIFE
inside a kernel body or arithmetic — it routinely already sits inside a parallel
region; hoist it to its own `let`), multi-leaf `<&!>` fused fold trees, and
a PARTIAL reduce over compact symmetric/antisymmetric/Hermitian storage
(rejected at typecheck — `decompact` first).

**The orbit-class fold (logical sums over compact storage).** The FULL fold
`reduce(S, (+), axes = r)` over a SymIdx/AntisymIdx group of rank r is the
full-domain sum over the logical (mirrored) cells (it is rewritten into the
logical walk `reduce(method_for(range<I>) <@> lambda(i) -> .. f(S(i, j, ..)) ..,
(+))`, which programs also write directly). Under the reorder licence (every
level a builtin `+`; not `BLADE_FP_REPRO=1`) both back ends fold that walk in
ONE pass over the packed pool: one accumulator per orbit class (weights 1/2 at
rank 2, 1/3/6 at rank 3, `r!/prod(m!)` in general; a strict group's mirrors
evaluate `f(-x)` and its repeated-coordinate cells `f(+0)`), the class sums
combined by additions only (`(S + S) + S`, never `3.0 * S`, which a
contracting compiler could fuse into an fma that is no association of the
logical terms). `blade plan` names it `fold form: orbit-class fold (rank r, k
classes)`, or the reason it was not taken; `BLADE_ORBIT_FOLD=0` keeps the
logical walk. Corpus `symmetry/112`-`115`; proof in `src/OrbitFold.fs`.
COMPACT ROW CLASSES (`[compact-row-classes v1]`, gate `BLADE_COMPACT_ROW_CLASSES`,
both lanes): when the compact pool is a rank-2 map produced by the plain compact
nest right before the walk (nothing reading it or writing in between, the cell
body unable to abort), each packed row's class partials -- diagonal and
off-diagonal; strict: the orbit class and, for a kernel of unknown parity, its
mirror -- are computed from zero right after the row is stored (inside the
producer's `omp` team), and the fold sums them in ascending row order before
the same addition-only combine: no second pass over the pool. Licensed only
(repro keeps the ordered walk), and it CHANGES licensed bits relative to the old
orbit pass (a different regrouping of the same terms); within one lane and build
the result no longer depends on the team size. The LLVM lane is serial and sums
each row with its vectorizer's own tree, not the C++ lane's fixed
`blade_fold::sq`/`sum<8>` tree, so the lanes agree to tolerance, not bits.
Declines when the cell body reads a value bound after the producer, when the
fold itself (its seed or walk) may write, or when anything between producer and
fold reads or writes. (The bench expert folds packed COLUMNS: this shares its
locality and one-team principle, not its schedule.) Corpus `symmetry/129`-`131`.

**Threaded complete rows under `BLADE_FP_REPRO=1` (C++ lane).** The repro
build keeps every fold's serial association, so Path B's per-thread partials
are off; what a `where omp` request can still buy is concurrent evaluation of
INDEPENDENT COMPLETE ROWS. When a fold's one outer level's value is one
complete scalar fold per index (the logical-walk checksum above, with the
licence on the outer fold kernel), each row's fold runs whole on one thread
into a slot and the outer chain runs serially, ascending from its seed: the
serial walk's value, bitwise under `BLADE_FP_CONTRACT=off`. Admitted only for
row bodies AND outer fold kernels on a closed total/pure whitelist (no integer
`/` `%`, no float -> int cast, proven subscripts, no writes, ...: a panic in a
team would be a race, and a kernel write would be reordered past rows) above a
static work gate (`BLADE_TILE_TUNE rowslots_min`, provisional). `blade plan`
names it `fold form: threaded complete rows (repro)`. Where the ordered orbit
scatter below ALSO admits the walk, the DEFAULT takes the scatter (round 3:
measured faster under mt8 repro, pairkernel 0.841 / comoment3 0.843 of the row
slots' time; the row-slot record says `admitted but yielded`; the bits are
pinned equal across the forms by `blade test omp-reduce`'s raw-bit battery),
`BLADE_FOLD_ROW_SLOTS=prefer` takes the row slots (the round-2 order), and
`BLADE_FOLD_ROW_SLOTS=0` (or `off` / `false`) disables the row slots only --
the walk is then the scatter where it admits it, else the serial walk. The
LLVM lane runs the serial walk -- or,
where the ordered orbit scatter below admits the walk, that scatter. Corpus
`symmetry/128`; `blade test omp-pragma` / `omp-reduce`.

**The ordered orbit scatter (strict logical sums over symmetric storage; both
lanes).** The same logical walk over a packed SymIdx<2, n> or SymIdx<3, n> pool, in a
build where EVERY level's fold is strict (`BLADE_FP_REPRO=1`, or no level
carries a reorder licence -- a `where repro` kernel), is ONE pass over the
pool that feeds every chain of the source exactly its source operand
sequence: a canonical cell's term `f(x)` goes to row chain R[i] and, off the
diagonal, to R[j], in an order (G-row groups: the diagonal block, then per
column the G row chains and the column chain in row order) under which every
chain receives its logical row in column order, from its own seed, through its
own kernel applied accumulator-first; the outer chain runs serially over the
completed rows. Rank 3 keeps ONE leaf chain per unordered pair (the same
operation tree for (i, j) and (j, i)): per first coordinate, the slice
scatter feeds H row i and the third event H[j, k] <- i from the same term,
then the middle and outer folds run over H. Nothing is reassociated, so no
licence is consumed and any pure TOTAL kernel (`b - a * 0.5`, an `if`-max)
and any coordinate-free seed (`-0.0`, `0.75`) qualify; the result is bitwise
the walk under `BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off` (`blade test
orbit-scatter`; under contraction the two forms may fuse differently). Taken
only where the licensed orbit-class fold declines; it takes precedence over
an admitted threaded complete-row form (round 3; `BLADE_FOLD_ROW_SLOTS=prefer`
gives the row slots the walk instead); declined (with the reason in `blade plan`) for
AntisymIdx pools, rank >= 4, a mixed licensed/strict nest, a body / kernel /
seed outside the closed total whitelist, a seed that mentions a coordinate.
`blade plan` names it `fold form: ordered orbit scatter (rank r, extent n, row
groups of G)`; `BLADE_ORBIT_SCATTER=0` keeps the walk (`BLADE_ORBIT_FOLD=0`
does not: it disables only the licensed regrouping). Corpus
`symmetry/132`-`135`.

**The row-owned pull (rank 3, threaded strict builds; C++ lane).** When the
rank-3 walk's OUTER level carries a thread request (the outer fold's `where
omp`, or a map `omp(i: 1)`) in a threaded strict build, the C++ lane runs the
scatter's LEAF chains on the team, partitioned by H row: the owner of row a
seeds every H[a, b] once, pulls T(c, a, b) for c < a in ascending c, then runs
slice a's group scatter with no third event -- every pair chain's source
sequence, on one thread -- and after a team-completion barrier the middle and
outer chains run serially from their seeds. Under `BLADE_FP_REPRO=1
BLADE_FP_CONTRACT=off` it is bitwise the serial scatter and the walk at every
team size -- the raw result bits are compared across the pull at T = 1 / 3 / 8,
a restricted team, the pull off, the serial scatter, the walk and the LLVM lane
(`blade test orbit-scatter`, through `BLADE_PRINT_BITS`), with one NaN payload
(a NaN meeting a NaN of another payload is outside the contract: the compiler
may commute an IEEE add, which keeps the value, not the payload); a
contraction-fast build is not verified bitwise (the forms may contract
differently, as the walk does per context). A requested or ACTUAL team of one
runs the serial slice form's leaf stage instead (`serial-leaf3`), and the run
record's `forms` census names each site's executed form with the requested and
actual team. The cell function is evaluated once per read (the serial form's
same-term reuse across rows is not kept). Its admission audits all five parties
(pool producer, walk, outer kernel, seed, intervening bindings) with the shared
write analysis. `blade plan` names it `..., row-owned pull on a thread team`;
`BLADE_ORBIT_PULL=0` keeps the other strict forms; the LLVM lane keeps the
serial scatter. Corpus `symmetry/138`-`139`.

**`where repro` — the reproducibility demand (named functions only).** The
inverse of the reorder licence: the function's emitted body must keep the
interpreter's operation sequence. Discharged as (a) `BLADE_REPRO_FN` on the
emitted definition — `noinline` + `-ffp-contract=off` on GCC, so the body
neither contracts to FMA nor gets re-inlined into a contraction-licensed
caller; (b) call form everywhere a named function already has it (direct
calls, kernel positions via the eta wrapper — loops call the function rather
than inlining its arithmetic); (c) a veto inside `foldReorderLicensed`, which
covers the OpenMP fold paths, every K-lane / `omp simd reduction` fold form, and the LLVM
lane's fast-math flags at once — `where comm(a, b), omp, repro` on a fold
kernel emits serial with a `// [omp] requested but emitted serial: fold
kernel is `where repro`` marker; and (d) BLAS/LAPACK/cuBLAS routing declined
while the body emits (BLAS differs in the last ULP; an eigenbasis is not even
unique). Refusals: on a lambda (the annotation cannot travel into textual
inlining — name the kernel), on a `static function` (compile-time evaluation
has no emitted body), and combined with `cuda` (device contraction/rounding
cannot discharge the demand). A main-local emission (function forced into
`main()` as a `std::function`) keeps the routing veto but cannot carry the
attribute; the emitted text says so rather than staying silent.

## 17. Equivariance and ML (shipped module)

Module doc: [features/equivariant-nn.md](features/equivariant-nn.md), canonical.
Part II (the equivariant ML library) is landed; Part I's original core-language
hook — a value-level `with equiv(G, ρ)` annotation checked per-expression by the
unifier, with `EquivIdx<n, G, ρ>` carrying the rep data — was **superseded on
2026-07-28** and is archival only (module doc status note + §1–4). The adopted
factoring splits it in two: rep DATA lives in index types and unifies like any
other type (`IrrepsIdx<spec>`, `PgIrrepsIdx<G, spec>`), while the rep CLAIM is a
deduced-then-pinned *signature* attribute (`where ml.equiv(G)`), the same
lattice-plus-pins shape `comm`/`anticomm` use. `ml-spec §N` citations below point
into the retired ml-spec plan; its surviving material is in the module doc.

The whole surface is import-gated — `import ml as ml`, then `ml.<name>(...)`;
bare names are unbound and `from ml import ...` is rejected. Checking is
compile-time only, zero runtime cost.

| Feature | Status | Notes / sources |
|---------|--------|-----------------|
| Representations and irreps (parity, multiplicity) | Partly Done | shipped spelling is a `let static` list of `(l, parity, mult)` triples plus the spec-computing statics, not the `2*L1o + 3*L2e` sugar of ml-spec §2 |
| `IrrepsIdx<spec>` (block-structured primitive index type; flat-dense, spec-keyed nominal identity + nominative aliases; block-navigation statics `irreps_len/l/parity/mult/dim/offset`) | Core (v7) | ml-spec §3; module doc §6/§11b (the DepIdx equation is the semantic reading — DepIdx iteration codegen is NOT landed); corpus `index-types/111–119`, `ml-ops/005–008` |
| Clebsch-Gordan machinery: real-basis coupling tables, path validation, CG lookup | Partly Done | tables are compiler-native (`WignerTables.fs`, pinned against the `ml/` oracle) and baked into elaborated ops; the ml-spec §4 user-facing `CGPath`/`CGIdx` dependent records are not built (module doc §11b F1 resolution) |
| Equivariant ops: `tensor_product` (paths, weights, block indices) / `y_to` / `linear` / `gated` / `linear_rows` / `gated_rows` / `scalars` / `norms` (compile-time elaboration, static specs, real-basis CG tables) | Core (v7) | module doc §11b; corpus `ml-ops/` (24 files) + `ml-e2e/002`; elaborated ops stamp `IrrepsIdx` on their signatures and `grad()` differentiates through them |
| Spherical harmonics (`ml.y_to`) | Partly Done | real solid harmonics, lmax ≤ 2 (explicit polynomials); ml-spec §6's recurrence for lmax > 2 pending |
| Certificate disciplines: `where ml.equiv(G)` (O(3), SO(3), and the point groups), `where ml.galilean(u, …)`, `where ml.perm_equiv` | Core | signature attributes, not expression annotations; checked by `MLEquiv.fs` / `MLGalilean.fs` at the `MLElaborate` pass-1/pass-2 seam, with a typecheck-resident second opinion in `DeduceRep.fs`. A pin is a *polymorphism license*, the equivariance analogue of `comm`. Generator-based (Lie-algebra) discharge accepts hand-written bodies that composition-only checking rejected. Corpus `ml-equiv/` (106 files) |
| Certificate deduction: speculative `where ml.equiv` / `ml.galilean` suggestions | Core | BL4011 / BL4014 warning channels plus structured `deduced[]` entries (`kind` "equiv" \| "galilean") in `ide check --json`; propose-don't-export — always warnings, and deliberately no `--strict-pins` arm, since a certificate owns no storage decision |
| Diagnostics | Core | BL4007 no equivariant map exists (Schur-zero hom-space, and tensor-product outputs no CG path reaches); BL4008 / BL4009 / BL4012 equivariance / galilean / permutation-equivariance discipline violations; BL4011 / BL4014 certificate suggestions; BL9004 internal deduction disagreement (typed walker contradicts the seam checker — an ICE that stops the build, never the user's fault) |
| Synthesis, O(3): `ml.derive_linear` (degree 1), `ml.derive_tp` (uncompacted bilinear, output spec = full CG decomposition), `ml.derive_sym_tp` / `ml.derive_alt_tp` (S₂-compacted self-products), `ml.derive_poly` (degree K ≤ 4 homogeneous, K = 1 degenerating to `derive_linear` bit for bit) | Core | each emits the COMPLETE Schur basis of the admissible hypothesis space as ordinary Blade source — the parameter count is a theorem, and the certificate holds by construction, so a pin needs no proof search. This is the primary user surface, in place of hand-derivation. Corpus `ml-equiv/` |
| `ml.sym_lift` (degree-K monomial lift) and the conversions `tensor_to_irreps` / `sym_to_irreps` / `irreps_to_sym` | Core | rep-INTRODUCTION and rep-ESCAPE forms; deliberately left uncertified (a stamp would be a false axiom) — `sym_lift`'s output is a plain `Idx<cells>` monomial space whose O(3) action is `ml.sym_spec(SPEC, K)`, not an irreps space |
| Synthesis, Sₙ index-action: `ml.derive_perm_linear` (complete basis of Hom_Sₙ(ℝ^{N^K}, ℝ^{N^L}) = orbit indicators = partitions of the K+L axis positions into ≤ N blocks), `ml.derive_perm_bias` (K = 0, the invariant-constant space), `ml.perm_matmul` | Core | the `ml.perm_equiv` discipline; flat row-major node-power buffers, L = 0 the invariant readout. `perm_matmul` is PPGN's engine — the one bilinear shipped by name rather than by synthesis, since the Sₙ bilinears at that arity have no orbit-indicator basis (`derive_perm_tp` deferred). Corpus `ml-equiv/` |
| Synthesis, point groups: `ml.derive_pg_linear` + `PgIrrepsIdx<GROUP, spec>` (`C4`, `D4` shipped) | Core | the complete ℝ-Schur basis over a frozen character table, Frobenius–Schur aware (real irreps degenerate to `derive_linear`'s loop nest verbatim, for ulp agreement). Certificates do not transfer between groups. `ml.restrict` (O(3) ↓ point group) is deferred; corpus `ml-equiv/045–052`, `094`, `104` |
| Static sizing builtins | Core | `sh_spec`, `total_dim`, `tp_spec`, `hom_dim`, `sym_spec`, `alt_spec`, `tp_weight_dim`, `tp_full_weight_dim`, `linear_weight_dim`, `sym_tp_weight_dim`, `alt_tp_weight_dim`, `poly_weight_dim`, `perm_weight_dim`, `perm_bias_dim`, `irreps_*`, and the point-group set `pg_total_dim` / `pg_hom_dim` / `pg_irreps_len` / `pg_irreps_dim` / `pg_irreps_mult` / `pg_irreps_fs` / `pg_irreps_offset` / `pg_restrict` (returns a spec, not an int). Value position only (`let static SOUT = ml.tp_spec(S1, S2)`); the annotation then names the static |
| Norm activation | Near-term | ml-spec §8.2; `ml.norms` (invariant norm readout) is shipped and is a different op |
| Message passing: `scatter` / `gather` | Near-term | ml-spec §9 (expressible today as loops — `ml-e2e/00*` do; dedicated ops pending) |
| Reynolds applications: symmetric message passing, higher-order interactions, antisymmetric applications | Near-term | ml-spec §14; the CG exchange-symmetry compaction shipped as `derive_sym_tp` / `derive_alt_tp` (measured 30–42%, not the 2–4× originally claimed — module doc §9) |
| Automatic differentiation (`ad.grad` reverse mode, v1 subset incl. any first-order recursive-array recurrence `prefix :: g(prefix(n-1), ..)` through the direct loop run backwards, O(n) -- structural/01 milestones A+B; `ad.jvp` forward mode, strict superset incl. overwrites/recurrences/product folds/if-else/units/combinators through C7 pipelines; array-valued `ad.jvp` = `(y, J v)` and the seeded `ad.vjp` = `Jᵀ w` into grad's buffers -- a matrix-free linearization, incl. the LU derivative actions through `m.lu_solve[_t]`, plan-fortran-killer-2 §6.3, corpus `ad-jvp-comb/110-114`) | Core | AST-level source transforms, one pass; module doc §11 has both ABIs + subsets; composition `ad.jvp(ad.grad(f))` = HVP, `ad.jvp(ad.jvp(f))` = second-order; corpus `ad/` + `ad-jvp/` + `ad-jvp-comb/` + `ml-e2e/`; remaining work in [features/equivariant-nn.md](features/equivariant-nn.md) §11 |

## 18. Graphs and trees

Module doc: [features/graphs-trees.md](features/graphs-trees.md); plan:
[plans/plan-graphs-trees.md](plans/plan-graphs-trees.md) (P0–P5 landed).

| Feature | Status |
|---------|--------|
| `TreeIdx<shape>` (a `let static` shape value): construction from a flat preorder literal, static whole-path reads `T((c0, c1))` and their composition, `extents`, printing, `reduce`, function-signature door with shape identity; `LeafIdx<S>` / `NodeIdx<S>` dense axes and `leaves(T)` | Core — `corpus/trees`, `blade test trees` |
| `method_for` / `object_for` over a tree slot, non-literal or wildcard paths, partial (internal-node) paths | Planned — refused with a stated reason (module doc) |
| Graphs as adjacency data + `where acyclic(g)`; `let rec` traversal (no `Trace`/`DAGIdx` types — deleted by the design) | Planned (P7) |
| Symmetric trees (commutative children) | Speculative |

