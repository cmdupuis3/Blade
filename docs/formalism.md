# Blade: Language Formalism (v11)

**Status**: canonical semantics for the Blade rewrite. Supersedes
`blade_formalism_v10.md` for language semantics. This document deliberately
contains *only* semantics: proofs live in [proofs.md](proofs.md) (mirroring the
machine-checked Coq tower), the feature census in [features.md](features.md),
feature modules under [features/](features/), related work in
`blade_literature_survey.md`.

**Corrections from v10** (details in proofs.md and the doc-set
[README](README.md)): product symmetry is *joint* per identity group, not
per-dimension (v10 §14.5/§14.6/§10.9.5 corrected); shared index spaces without
array identity license no symmetry; the H ∩ Stab lowering law is an exactness;
compound-index application is the single joint-tuple form; MonadPlus right distribution is
not a law.

**Implemented vs specified.** This document specifies the language; a few
constructs it names are designed but not built. Those are marked
**(planned)** where they appear (tables carry a Status column), and the
implementation status of everything else is pinned by `tests/corpus/`.
Code blocks tagged `blade` are checked by `blade test docs`; blocks written
in the formalism's own notation (metavariables such as `T₁^r₁`, `A*`, `...`)
are marked `sketch` and are not programs.

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

```blade sketch
method_for(A, B)   // from structure: arrays determine iteration+indexing
object_for(f)      // from operation: kernel arity determines iteration+indexing
<@>                // application connects them, producing a Computation
```

The fusion rests on the isomorphism `Array<T, I, J> ≅ I → J → T`: arrays are
curried functions from indices to values (§4.3). Because the loop object owns
both iteration and indexing, kernels receive values anonymously — no index
naming — which is what lets one kernel serve every arity.

S/T and T/S compose rather than compete: S/T governs outer structure
(iteration, parallelism, symmetry); T/S combinators govern reduction strategy
inside kernels (`reduce` today; `scan` and `tree_reduce` are **(planned)**).

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

### 2.4 Value types, casts, and promotion

Base types: `Int32/Int64/Float32/Float64/Complex64/Complex128`. Conversions
are EXPLICIT: a scalar type name in call position is a numeric cast —
`Float32(x)`, `Float64(extents(a))`, `Complex128(r)` — a plain-call
intrinsic, shadowable like `abs`/`complex`, with the type-position aliases
included (`Int` casts to Int32, `Float`/`Double` to Float64). Legal
conversions: int↔int, int→float, int→complex, float↔float, float→complex,
complex↔complex, and the identity; units ride through unchanged. Refused
(BL3019): a complex source into a real/int target — which real is meant is
exactly what the cast cannot say; project with `real`/`imag`/`abs`/`arg` —
and a float source into an int target unless the rounding is visible at the
cast site, `Int64(floor(x))` / `Int64(ceil(x))`, so truncation is always
spelled (a rounded value bound to a name and cast later refuses on
purpose). Array operands lift elementwise like `cos(A)`; `Int64(floor(A))`
fuses the rounding and the cast into one kernel. A cast of a value whose type
is a function's own type variable (`Float64(reduce(row, (+)))` over
`row: T^1`) is a GENERIC cast: its legality depends on the instance, so it is
judged at every call against the type the call gives `T` (and again after
monomorphization) -- `stats.mean` casts this way, so an Int64 row averages in
Float64 and a complex row is refused at the call. Likewise a generic body that
returns `sqrt(x)` (or another complex-preserving math intrinsic) AS `x`'s own
type is refused at an integer instance, where the Float64 result would be
truncated. Arithmetic between a bare parameter variable and a real scalar
(`function addone(x: T) = x + 1.0`) is typed `T`: `T` may be an array, which
the scalar broadcasts against, so `addone([1.0, 2.0])` is `[2.0, 3.0]` and
`addone(3.0)` is `4.0`. That is exact for every instance whose element the
scalar promotes into (Float64, complex, Float32 beside a float literal); an
instance it would promote away (an integer element, or Float32 beside a
Float64 value) is refused at the call (BL3019): the conversion is the
caller's to spell, with the type in call position (`addone(Float64(n))`,
`addone(Float64(xs))`).
(`x: T^0` is the same variable as `x: T`.) A variable that is only ever a
scalar -- an array's element, the `reduce(row, (+))` of a `row: T^1` --
keeps the promotion rules' scalar result, so an Int64 row's sum plus `0.5`
is a Float64.

**Arithmetic semantics — one contract, every lane.** The compiled program
(g++), the interpreter (`src/Interp/Numerics.fs`), the LLVM lane
(`src/EmitLlvm.fs` + `src/cpp/blade_llvm_shim.c`) and compile-time static
evaluation compute the same value, or fail with the same code (a `let static`
fold refuses at compile time instead):

| Operation | Result |
|---|---|
| Int `+` `-` `*` | two's complement modulo 2³² / 2⁶⁴ — WRAPS (interpreter: unchecked .NET integers; C++: `-fwrapv`; LLVM: no `nsw`). `x + 1 > x` is `false` at the maximum, and a wrapped sum still carries the true value modulo 2⁶⁴ (`proofs/BladeNumericContract.v`, `int64_observation_exact`) |
| Int `/` `%` | truncate toward zero. A zero divisor PANICS **BL8013** (`integer division by zero` / `integer modulo by zero`). `MIN / -1` wraps to `MIN` and `MIN % -1` is `0` (C++ UB, an x86 trap, a .NET exception — defined here) |
| Int `^` Int | EXACT, wrapping like `*` (square-and-multiply modulo 2ʷ); `0 ^ 0 = 1`; a negative exponent PANICS **BL8013** (`integer power with a negative exponent`) — convert to Float64 first for a real power |
| Real `^` | `x * x` when the exponent is exactly 2, otherwise the platform libm's `pow` in double; a Float32 result is rounded once from the double |
| float → int cast (`Int64(floor(x))`) | truncation of a value the target can hold; NaN, ±∞, or anything outside `[-2ʷ⁻¹, 2ʷ⁻¹)` PANICS **BL8014** — never a saturated value or a platform sentinel |
| Int64 → Int32 cast | wraps (two's complement) |
| transcendental intrinsics (`exp log log10 sin cos tan sinh cosh tanh asin acos atan atan2`, and `pow`) | the PLATFORM libm's value, computed AT RUN TIME — never folded at compile time, so a literal argument and the same value read from an array agree. A Float32 operand is evaluated by the double function and rounded once to Float32; an integer operand widens to double |
| complex transcendental intrinsics (`exp log sqrt sin cos tan sinh cosh tanh asin acos atan` on a complex operand) and complex `^` | the PLATFORM C library's complex functions (`cexp clog csqrt csin ccos ctan csinh ccosh ctanh casin cacos catan cpow` — libmingwex's on Windows), computed AT RUN TIME under the same barrier, so a constant operand and a run-time one agree bit for bit. Complex `^` is libstdc++'s algorithm over those functions: complex ^ complex is `cpow`; complex ^ real (an integer exponent is cast to the component type) is `pow(re, y)` for a positive real base, else `polar(exp(y · log(z).re), y · log(z).im)`; real ^ complex is `polar(pow(x, w.re), w.im · log(x))` for `x > 0`, else `cpow`. A Complex64 operand is evaluated by the Complex128 function and each component rounded once to Float32 |
| `sqrt` `floor` `ceil` `abs` `fma` (real operand) | IEEE correctly rounded (so a compile-time fold is the run-time value); Float32 operands use the float operation |
| integer literals | exact, including array-literal leaves (never routed through a double) |

A panic is an ordinary runtime failure (`error[BL8013]: ...`, exit 1) that
names the faulting operator's source position (`  --> file:line`) and the
call stack; nothing in the table is undefined behavior in any lane. The
stack's frames (`  at <name>`, innermost first) say only names the program
wrote: a function its declaration's name; a lambda bound directly by a `let`
that binding's name; any other lambda `lambda in <s>`, where `<s>` is the
nearest enclosing binding or function (`lambda in mk` for a closure `mk`
returns, `lambda in r` for a kernel passed in `let r = ...`), or plain
`lambda` where there is none; an operator section `(/) in <s>`. The
position is a compile-time constant read only on the panic path, so the
guard's hot path is unchanged by it. The
fast paths are kept by construction: a nonzero literal divisor other than
`-1` compiles to a plain `/`, a literal nonnegative exponent to a multiply
chain (`x ^ 2` is one multiply in both the integer and the real case), and
the libm functions are declared `const` under a non-builtin name
(`blade_libm::`, `src/cpp/blade_runtime.hpp`), so a loop-invariant call is
still hoisted — only the fold is gone.

"The platform libm" is a per-platform claim: the interpreter P/Invokes the
same library the compiled program links (ucrtbase on Windows), so the
differential gates are byte-exact on one machine; two operating systems may
legitimately differ in a transcendental's last ulp. The complex functions
are reproduced in the interpreter by porting the platform library's own
code (libmingwex's `c*` objects, with libgcc's `__muldc3`/`__divdc3`) over
the same P/Invoked real functions. On a platform without that port the
interpreter keeps best-effort textbook forms for complex `exp`/`log`/`sqrt`/`^`
(not bit-verified) and declines the rest rather than guess. Outside the
contract, documented rather than hidden: CUDA device bodies (device libm,
`thrust::` complex, plain integer `/`), a host
compiler without asm labels (MSVC, the nvcc host pass: `blade_libm::`
forwards to `std::` there), and `lgamma`/`digamma`, which are Blade's own
series on both sides (BL8008 outside `x > 0`).

Mixed-type arithmetic still promotes — float beats int, wider beats
narrower within a category, complex promotes componentwise, and a mixed
int/float op computes at the float operand's width (`Int64 × Float32 →
Float32`, C++'s usual arithmetic conversions) — but the conversion of a
NON-literal operand now warns (BL3020), naming the explicit cast. Literals
adapt silently by design (`a32 * 1.0` stays Float32), as does the exact
same-component-width real→complex embedding (`w * z` over Float64 and
Complex128).

Type variables (single capitals) are universally quantified within a
signature; the same letter denotes the same type. There is no promotion TYPE
in signatures: where two type variables would mix, the caller converts one
with the target type in call position, so the signature names one variable
(arithmetic between two bare variables -- `x + y` over `x: T, y: U` -- makes
them that one variable, §5.1, rather than typing the result as the left one).
Arguments sharing a variable agree EXACTLY: neither order converts and no
literal adapts across them (`add(2.5, 1)` is refused like `add(1, 2.5)`; a
literal's type is its spelling), and the same holds for two caret
variables whose ELEMENTS meet through arithmetic (`a + b` over `a: T^1, b:
U^1` refuses an Int64 and a Float64 array at the call). The caller converts
with the type in call position:

```blade sketch
function add(a: A^0, b: A^0) -> A^0
function scale(s: B^0, v: B^1) -> B^1      // scale(Float64(k), v) for an Int64 k
```

Complex values support literals and `conj(x)`; `conj` is the identity on real
types and distributes elementwise over arrays.

**Units of measure** are annotations on primitives, not types: `Unit meters`,
`Unit velocity = meters / seconds`, `Float<velocity>`. Unit arithmetic checks
addition (same unit) and composes under `*`/`/` -- elementwise, outer
(`a [*] b` is meters·seconds), in `prodsum`, and across the elements of an
array literal (one element type, one unit). Through a GENERIC function the
body's demands are recorded once per declaration and judged at every call:
`function add(x: T^0, y: T^0) = x + y` requires its two arguments to share a
unit (as do comparisons, branch results, and a transcendental's argument
being dimensionless), so `add(meters, seconds)` is BL3006 at the call.
**Bounded primitives** (`Float<min=0, max=1>`) carry runtime-checked bounds
and compose with units; the bound is checked wherever the annotation stands
-- a `let`, an expression ascription `e : T`, a function parameter (on entry),
a function return -- including through a type alias
(`type Sal = Float64<psu, min=0.0>`).
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

| Combinator | Signature sketch | Semantics | Status |
|-----------|------------------|-----------|--------|
| `zip(A₁..Aₙ)` | → Tuple elements over shared k = min rank prefix | `zip(A,B)(i..) = Tuple(A(i..), B(i..))`; output symmetry = intersection where all inputs agree; kernel receives one flat parameter per array by default (`lambda(a,b)`), or the whole `n`-tuple as one value if its parameter is written `Tuple<n>` (§2.8) | Implemented |
| `align(A₁..Aₙ, spec)` | → `AlignedExpr` | zip + stencil metadata (dims, offsets, boundary ∈ Shrink/Pad/Periodic/Reflect); kernel receives N separate arguments | **(planned)** |
| `stencil(A, {d: offsets}, boundary)` | sugar | desugars to `align` of `shift`s | **(planned)** — neighbor access today is `halo<I, [offsets]>` (§7.3) |
| `stack(A₁..Aₙ)` | → rank+1, fresh leading symmetry class | `stack(A,B,C)(k)` selects array k | Implemented |
| `transpose(A, p)` | permutation p | hard transpose (data movement on materialize); `transpose(transpose(A,p),q) ≡ transpose(A, q∘p)`; on symmetric arrays with the identity-under-σ permutation it is the identity, on antisymmetric it negates per parity | Implemented |
| `diag(A, (d₁,d₂))` | rank−1 | collapse two dims to their diagonal | **(planned)** |
| `join(A, B, d)` / `subset(A, d, (s,e))` / `split(A, d, i)` | | concatenate / range-extract / split = two subsets; split-join round-trips | `join` Implemented; `subset`/`split` **(planned)** ([plans/plan-subset-split.md](plans/plan-subset-split.md)) |
| `reverse(A, d)` / `shift(A, d, k, boundary)` | | index reversal (involution) / offset with boundary handling | **(planned)** — the `reverse<I>` virtual array (§7.3) is Implemented |
| `A <\|:> B` | fallback | `A(i)` if allocated else `B(i)`, checked per curry level; A's layout dominates iteration order; symmetric A requires symmetric allocation | Implemented |
| `decompact(A, axis)` | compact → dense | expand a symmetric/antisymmetric compact axis to dense storage; sign-correct for antisymmetric sources; chainable to full dense; error on non-compact axes | Implemented |

### 2.7 Binding forms

| Syntax | Mutable in scope | Passable to `mut` param |
|--------|------------------|-------------------------|
| `let static x = e` | no | no |
| `let x = e` | yes | no |
| `let mut x = e` | yes | yes |

All parameters pass by reference; `mut` on a parameter permits callee mutation.

### 2.8 Tuples and argument packs

An operand list — a loop former's arguments, a kernel's call-site pack — is
matched against a kernel's parameter list on its **top-level spine**: each
written group or tuple-typed value (alias-invariant by its static type, not
by how many `let`s named it) is one *node*, and nesting **inside** a node is
preserved, not flattened. `f(a, (b, c))` and `f(a, b, c)` are therefore
different packs in general — two nodes vs. three — and become the *same*
program only when the parameter schema licenses it (below); written
parenthesization is otherwise meaningful. Alias depth is unobservable the
same way: given `let P = (A, B); let Q = P`, the pack `K <@> Q` equals
`K <@> (A, B)` — naming a pack does not change what it means.

`Tuple<N>` (`N` an integer literal, `N >= 2`) is the width-only tuple
annotation. It fixes only how many *top-level* slots a value or parameter
occupies — nesting inside the annotated value is not counted, so a
`Tuple<4>` cannot re-count across two nested `Tuple<2>`s (`((a,b),(c,d))` is
two nodes, not four; write the flat spelling `(a,b,c,d)` instead — the one
thing this ruling gives up). Element types stay inferred.
**`Tuple<T1, ..., Tk>`** is the same annotation with the component types
*written*: the argument list disambiguates (a lone integer literal is a
width, anything else is a component list of width `k >= 2`; the two may not
be mixed), and it denotes exactly the type the parenthesized
`(T1, ..., Tk)` denotes — one node, not two that agree. Prefer it whenever a
component is anything but a plain scalar, because written components are the
only ones that are actually *checked* (a wrong unit or rank inside a
component is reported at the call as `argument i, component j`) and the only
ones that survive to codegen.
A bare comma **constructs** a tuple at any binding site — `let t = b, c` has
type `Tuple<2>` — matching the parenthesized literal `(b, c)` byte-for-byte
from the checker down, and it **destructures** on the pattern side the same
way: `let a, b = t` binds what `let (a, b) = t` binds. The two halves compose
without a disambiguating rule — `let a, b = c, d` destructures the pair built
from `c` and `d` — because the pattern list is bounded by the `:` or `=` that
must follow it and the value list only begins after that `=`. A pattern whose
name count matches neither the value's top-level width nor its flattened leaf
count is an error, not a partial binding. A literal `(a, b)` written directly
as an argument is a tuple value the same way — it needn't be let-bound first.

**Kernel and function parameter lists are width schemas**, matched
**greedily, left to right**, against the pack's top-level spine, with no
backtracking. At each schema position: an unannotated parameter takes one
plain node; if that node is instead a tuple, it is a hard error (`BL3002`,
"tuple-ness is never inferred") demanding either an annotation or a flat
rewrite. A `Tuple<k>` parameter tries, in order: **(1) direct bind** — one
tuple node whose own top-level width is exactly `k` binds as a single value,
preserving whatever is nested inside it; **(2) splice** — failing that, if
the schema wants `k` separate plain parameters there and the pack instead
offers one tuple node of width `k`, it unpacks once into its `k` components
(this is `f((a,b)) == f(a,b)` and the `K <@> P` alias case); **(3) regroup**
— failing both, `k` *consecutive plain* nodes combine into the one `Tuple<k>`
parameter (no tuple node was written at all, e.g. `(A, B, C)` sliced 1 + 2).
A leftover node or parameter after the scan is an arity error. Because
matching never backtracks, it is fully determined by what was written —
`f(t1, a, b)` (a tuple node, then two plain values) and `f(1.0, 2.0, a)`
(three plain values) read differently against `f(y: Tuple<2>, z: Float64)`,
and the reading you don't get is available by writing the other grouping
explicitly (`f((t1, a), b)` against a matching written type) — parentheses
disambiguate precisely because structure survives. Components read with
`t[k]` projection, chainable into nested structure (`t[0][1]`; `t.0` does not
exist; tuple *patterns* like `lambda((a, b))` still don't parse). This makes
the pack/tuple distinction **always a written one** — never inferred from a
kernel body — because a kernel's parameters are matched against the pack only
*after* its body has already been type-checked against fresh variables; an
inferred reading would make that matching order-dependent and unsound.

Nested tuples are real data under direct binding — a `Tuple<2>` parameter
facing one 2-wide tuple node receives the whole node, not its splice, so a
fully written nested type such as `((Float64,Float64),(Float64,Float64))`
carries a pair of pairs through to the body and `r[0][1]` chains correctly.
(The width-only `Tuple<2>` spelling does *not* carry this: its element
slots are fresh inference variables nothing at a direct call writes into, so
a nested value passed through one defaults its slot to a scalar and the
projection chain fails in codegen, not in `check` — a known, separate gap in
the direct-call argument seam, not of one-level matching itself. Writing the
components — `Tuple<Tuple<Float64,Float64>,Tuple<Float64,Float64>>`, the same
type as the parenthesized spelling above — fills the slots and is the
supported way to say it; the same applies to a tuple of *arrays*, which the
width-only form cannot express at all.) Symmetrically, **a direct call never splices**: at
`two(x: Float64, y: Float64)`, one tuple argument (`two(pair)`) is read as
the whole first parameter under currying's existing partial-application
rule, never as two scalars for `x` and `y` — so it lands as an ordinary type
mismatch against `x`'s declared type rather than a splice. The paren-dropping
splice (`f((a,b)) == f(a,b)`) is a property of the operand/kernel seam
(`K <@> P`, loop formers) only; write direct calls flat, or against a
parameter `Tuple`-annotated to receive the whole argument.

The same annotation reads one level down inside a loop former: given
`zip(B, C)`, `method_for(zip(B, C)) <@> lambda(p: Tuple<2>) -> ...` binds `p`
to the co-iterated pair per iteration, equivalent to the flattened
`lambda(a, b) -> ...` spelling over the same zip — an opt-in way to receive
"one tuple argument" per iteration, rather than a second competing default.

Two shapes are refused rather than guessed at:

- A `where` clause (`comm`, `omp`, ...) together with a `Tuple<N>` kernel
  parameter is refused (`BL3999`): `comm`/`anticomm` address parameters by
  position and the parallel strategies by name, and realizing a `Tuple<N>`
  parameter as its `k` row parameters renumbers both. Write the parameters
  flat to use a `where` clause.
- `zip(...)` cannot appear beside another array in one operand pack —
  `(A, zip(B, C))` is refused (`BL3999`), both loop orientations. Co-iterating
  a zip nested inside an outer loop is a real feature, just not implemented
  yet; hoist the zip to its own `<@>`, or pass its arrays as separate
  operands.

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
reserved for poly-tuple structural access. A COMPOUND axis indexes FLAT and
full-arity like SymIdx: a rank-k compound consumes k positional subscripts
(`B(lat, lon)`), with trailing regular dims appended. A SPARSE axis is one
slot whose domain is k-tuples, applied with ONE tuple value: `S((lat, lon))`,
wildcards inside the tuple (`S((lat, _))`), short tuples pinning a leading
prefix; a rank-1 sparse takes a bare scalar (1-tuples collapse in the
parser). On a compound, the tuple spelling and every wildcard form are type
errors steering to SparseIdx — partial reads over an irregular valid set are
the hash-based sparse type's regime. (Supersedes the earlier tuple-form
compound convention: with full-arity-only reads, the two-slots ambiguity that
motivated it is gone.)

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
index types** `Idx<n, Tag>` (**(planned)**; named index types give the same
nominal distinction today) distinguish same-extent spaces
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

**Nested/mixed symmetry** (spec level, **(planned)**): `NestedSymIdx<n>` (symmetric pairs of
symmetric pairs; S = n(n+1)/2, cardinality S(S+1)/2; elasticity tensors),
`RiemannIdx<n>` (antisym pairs, symmetric between them; n=4 → 21). Users
compose via `Sym<I,I>`, `Antisym<I,I>`, products; the `unsafe indextype`
escape hatch supplies custom `canonical : indices → Option<canonical>` (None =
implicit zero) and `transform` (value adjustment on non-canonical access).

### 3.5 Compound and sparse index types

**`CompoundIdx<mask>`** — for mutually-dependent sparsity (ocean points, not a
product of valid lats × valid lons). Identity = whole-mask hash (O(1) type
equality); storage is contiguous over mask-true tuples in lex order — the
rectilinear mask makes the valid-tuple table **sorted by construction**, which
is what buys contiguous layout and device-friendly slices; per-element hash
gives O(1) coordinate lookup. Enumeration = exactly the in-bounds mask-true
tuples, each once, in lex order (proofs.md §Compound).

Two construction routes:

```blade sketch
type OceanIdx = CompoundIdx<ocean_mask>    // static type route
let view = compound(dense, mask)           // runtime builder route (§15, sql.md)
```

**Indexing is flat and full-arity, like SymIdx.** A rank-k compound axis
consumes k positional subscripts — `B(lat, lon)`, with trailing regular dims
appended (`B(lat, lon, t)`; omitting the trailing index yields the contiguous
trailing-row sub-view). Reading an absent (mask-false) tuple is a runtime
error; the storage-keyed fallback is `<|:>`. The historical tuple spelling
`B((lat, lon))` and every wildcard/partial form (short prefixes, interior
holes, residual reads) are **type errors steering to SparseIdx** — partial
indexing over an irregular valid set is the hash-based sparse type's regime,
where every wildcard position costs the same gather. The residual currying
table below lives on SparseIdx.

**`SparseIdx<keys>`** — explicit enumeration of valid tuples (CG triples,
edge lists). `keys` is a rank-1 array of Nat tuples: a `let static` tuple list
(entries baked at compile time — desync-proof) or a runtime tuple-array
variable (index built at runtime, mirroring the compound mask). Rank is
implicit from the tuple arity; there is no rank parameter and no per-axis
extents — lookup hashes the tuple directly. Keys keep their **given order**
(never sorted): iteration visits |keys| entries in key order, and the compact
buffer is laid out in that order. Duplicate keys are a construction error.

**A `static struct` name as an index type** (enumerable constrained domains,
docs/plans/structural/06) — `range<R>` and `Array<T like R>` accept the name of
a `static struct R { f₁: Int<min=a, max=b>, ... } where p₁, p₂, ...` whose
fields are `Int` or `Nat` with static bounds: the iteration space is the
struct's SOLUTION SET, in lex order of the fields (first field outermost), and
the kernel takes one positional parameter per field. When every conjunct is a
linear inequality on the fields (`i - j <= w`, `abs(i - j) <= w`,
`l3 <= l1 + l2`, `m1 + m2 == m_out`; `<`, `>`, `>=`, `==`, `&&` accepted) the
solutions are enumerated in CLOSED FORM — nested loops whose bounds are affine
in the earlier fields, difference constraints Fourier–Motzkin-projected so no
prefix is dead — exactly the solutions are visited and the box is never scanned
nor capped. Otherwise the solutions are enumerated once at compile time by the
counting layer (`idx_card`'s certified routes, box ≤ 100,000 cells) and baked
as a key table, with a BL4010 advisory; a non-linear domain over the cap is
refused (BL4020). Either way the slot is SparseIdx-shaped: the output is a
sparse array over the solution set (full-key reads `R((i, j))`, flat folds in
lex order), a domain slot is the whole iteration space (`range<R, J>` is
refused), and an empty domain warns and iterates zero times.

Tuple indexing with wildcards: a full key is an O(1) hash lookup (a missing
key is a runtime error); a wildcard/short-prefix partial returns the matching
entries **by gather** — with no sorted table there is no contiguous-window
family, so every partial (prefix or scattered alike) is one pass over the
entry list in key order. Residuals follow the compound currying table: one
free axis → dense `Idx`, ≥ 2 → a **residual SparseIdx** (the residual of a
key set is a key set). Construction routes: `range<SparseIdx<keys>>`
(iterate the key set) and `sparse(values, keys)` (bundle rank-1 values, in
key order — no scatter). Prefer `CompoundIdx` when validity derives from a
mask over a rectilinear grid: its lex-sorted table is what buys contiguous
prefix windows and device-friendly layout.

### 3.6 Bounded and dependent index types

`BoundedIdx<l, u>` (l ≤ v < u; `Idx<n> ≡ BoundedIdx<0, n>`) is not user-declared;
it arises from currying symmetric indices — `S(i) : Array<... BoundedIdx<i, n>>`.
The dependence erases to runtime bounds in C++.

`DepIdx<I, f>` generalizes: inner index type depends on the outer index value;
extent = Σᵢ |f(i)|; iteration yields (i, j : f(i)); the storage bijection is
the general left-justified one (proofs.md §DMWF). Instances:

```blade sketch
type RaggedIdx = DepIdx<Idx<n>, lambda(i) -> Idx<lengths(i)>>   // ragged
type TriIdx<n> = DepIdx<Idx<n>, lambda(i) -> Idx<n - i>>        // triangular
type IrrepsIdx<spec> = DepIdx<...>                              // ML blocks
```

(`DepIdx` is the semantic model, not a surface type: the instances are
written with their own names, and ragged arrays come from literals and
`group_by`.) `RaggedIdx` exists in closed form (lengths visible) and opaque form (lengths
abstract at function boundaries); both support reduce/extents/indexing, and
ragged literals construct them directly.

### 3.7 Index transforms

**(planned)** — none of these is built; `rename` has no surface spelling yet,
and `subset`/`align` share the §2.6 status. `flip(A, dim)` (reverses ordering, changes hash), `rename(A, old -> new)` (tag
change), `subset(A, dim=lo..hi)` (new extent + hash), `align(A, B, dim)` (join
on common indices). All explicit; no implicit conversions; mismatches are type
errors.

### 3.8 Files as type providers

```blade sketch
import netcdf as nc
let era5 = nc.load("era5.nc")        // metadata read at COMPILE time
let t2m = era5.vars.t2m |> nc.read   // era5.index.lat : Idx<721>, ...; t2m typed from the file
```

Compile-time metadata inspection instantiates index types (`era5.index.<dim>`)
and typed variables (`era5.vars.<name>`); runtime reads values. Valid because
file *structure* is quasi-static and structure (not values) determines types.
NetCDF, Zarr, CSV and Icechunk providers are implemented (`blade test
netcdf|zarr|csv|icechunk`); HDF5 is planned. (The earlier spelling
`type ERA5 = NetCDFProvider<"era5.nc">` is gone.)

### 3.9 Symmetry lives in the index type

`Array<Float like SymIdx<2, 1000>>` — not a dense array plus an annotation.
The index type fixes storage (triangular), iteration (triangular), and access
(canonicalizing): `cov(3, 1)` and `cov(1, 3)` are the same location. The
symmetry system (§11) *infers* symmetric index types for outputs from kernel
commutativity and array identity.

A **literal** for such an array is written in the storage's own shape: one
bracket level per dimension of the group, each row starting where its parent
left off. A rank-`r` group over extent `n` has an outer level of `n` rows, and
a row seeded at coordinate `p` holds `n - p` cells (`n - p - 1` for the strict
antisymmetric group, whose diagonal is not stored).

```blade
let A: Array<Int64 like SymIdx<2, 3>>    = [[1, 2, 3], [4, 5], [6]]  // 6 cells
let K: Array<Int64 like AntisymIdx<2, 3>> = [[1, 2], [3], []]        // 3 cells
```

`A(1, 0)` is `A(0, 1)` is `2`; `K(1, 0)` is `-K(0, 1)`; `K(1, 1)` is `0`. The
flat pool is deliberately NOT a second spelling — the nesting is what says
which cell is which — and neither is the rectangular nest, which names cells
(`(1, 3)` above) the axis does not have. A HermitianIdx literal takes the
inclusive triangle with a real leading cell per row: the diagonal is read
unconjugated, so `A(i,i) = conj(A(i,i))` forces it real.

**The triangular shape is symmetric by default.** An unannotated nest whose
rows are `n, n-1, ..., 1` infers `SymIdx<2, n>` — that profile IS the class's
storage, so the literal that spells a symmetric matrix out reads back as one
(`R(1, 0)` folds to `(0, 1)`). The shape is genuinely shared with ragged data,
and this rule hands it to the compact class, so ragged data of exactly this
profile takes its annotation: `Array<T like Idx<n>, RaggedIdx<lens>>`. Any
other row profile — equal lengths, or lengths that don't step down by one to a
final 1 — is untouched and still infers rectangular or inline-ragged.

**A ragged literal's shape is the literal's.** `RaggedIdx<lens>` over a literal
does not size anything: the row lengths and offsets are built from the nesting
itself, and the annotation's `lens` is checked against them rather than read.
So a `lens` that disagrees is refused (BL4018), as is one the compiler cannot
hold — a lens computed at run time can be neither honoured nor compared, and
sizing a ragged array from one is not yet a thing the language does. When the
lengths are meant to come from the data, write the nest with no annotation at
all and let it infer.

The rule is matched at any rank — `[[[1,2,3],[4,5],[6]], [[7,8],[9]], [[10]]]`
is `SymIdx<3, 3>` — but it is INCLUSIVE only: the strict profile
(`n-1, ..., 1, 0`) is `AntisymIdx`'s storage, and antisymmetry is a claim about
SIGNS, which no shape on its own justifies inferring.

Printing agrees with the literal. Every RANK-2 array — compact, ragged or
dense — prints its rows bracketed, so `A` above echoes as it was written;
rank 1 is already one level of brackets and ranks ≥ 3 stay flat. The REPL
additionally shows only the first five entries per level and elides the rest
as `...`; that cap is the echo's alone, and `blade run` prints every cell.

Printing also agrees with the literal's TYPE. As in source, the decimal point
is what tells a float from an integer: a `Float64`/`Float32` value, and each
component of a `Complex128`/`Complex64`, prints with one — `2.0`, `-0.0`,
`[2.0, 3.0]`, `(1.0,-2.0)` — while an `Int64` prints bare, so `[2, 3]` is only
ever an integer array. Otherwise a float keeps its 15-significant-digit
rendering (`0.1`, `2.5`); the exponent forms (`1e+20`, `1e-07`) read back as
float literals already and print unmarked, as do `nan`, `inf` and `-inf`. Every
lane prints the same bytes (the compiled program, the interpreter, the LLVM
back end, and the REPL/notebook echo, which shows the program's own output).

### 3.10 Index values and nominal typing

Iteration emits values tagged with their source index type as a **unit**:
`method_for(range<LatIdx>) <@> lambda(i) -> ...` gives `i : Nat<LatIdx>`.

- Array indexing requires unit match: `A : Array<T, LatIdx>` accepts
  `Nat<LatIdx>`, rejects `Nat<LonIdx>` even at equal extent. Lambda captures
  and named-function kernels are checked the same way once their parameters
  meet the iteration (a function `g(i) = A(i)` whose unannotated `i` is used
  only as a subscript into `LatIdx` IS a `Nat<LatIdx>` parameter).

**The subscript judgment.** Every subscript `A(e)` into a slot of index type
`I` is judged by three rules, eagerly and again once inference is complete:

1. *Class.* `e` is an integer or an index value. `Float`, `Bool`, `Complex`
   and `String` subscripts are refused (BL4003); an otherwise unconstrained
   variable in subscript position defaults to `Int64`, not `Float64`.
   Keyed slots (`SparseIdx`, `EnumIdx`) and halo window offsets stand down.
2. *Nominal.* An index value of a DIFFERENT index type is refused (BL4003 at
   a subscript, BL3001 at a call).
3. *Range.* A literal position — a subscript `3` / `-1`, a cast `(3 : I)`,
   or a literal argument to a `Nat<I>` parameter — is checked at compile time
   against the static extent (BL4003); a negative literal subscript is
   refused on every plain slot. A literal SUBSCRIPT into a plain slot with no
   static extent -- a `T^k` or `Idx<n>` parameter, a pack element, a set
   operation's result -- is checked at run time against the array's own
   extent instead (BL8006), named slot or anonymous alike. A literal index-typed VALUE -- a `Nat<I>`
   `let`, a cell of an index-typed foreign-key column -- is range-checked the
   same way, with one exception: `-1`, group_by's "excluded" key. A string
   `EnumIdx` literal must be one of the type's labels.

**Positions and casts.** Arithmetic on an index value yields a *position*:
never a PROVEN index value (`i + 1` is not proved to lie in `I`). An
annotated index operand refuses the arithmetic outright; an unannotated
kernel parameter's arithmetic (`lambda(i) -> u(i + 3)`) is a position, which
keeps its operand's index type for the nominal rule: into a slot of that
same type it is checked at run time (BL8006), and into a slot of a
DIFFERENT named index type it is refused (BL4003) exactly as the bare index
is -- two named tags that disagree are a type error, whether or not
arithmetic intervened. `lambda(k) -> a((k * 8) / 4)` over `range<Half>`,
with `a` over `Src`, is refused; the conversion is spelled,
`a(((k * 8) / 4 : Src))`.
`(e : I)` is the one door from integers into `I`: a literal is range-checked
at compile time, a computed integer is a CHECKED conversion (run-time guard
`0 <= e < extent(I)`, BL8006; it needs `I`'s static extent). A plain integer
passed to a `Nat<I>` parameter goes through the same door. The cast
constrains its RESULT, never its operand: an unannotated kernel parameter
keeps the type its feed gives it, so `lambda(k) -> s((k : State))` over
`range<Idx<91>>` or `0..91` converts `k`, and `(W1 + k : State)` converts a
position, whatever index type its operand carried. A bare index value of a
DIFFERENT named index type is not an integer and the cast refuses it too
(BL4003, nominal); a deliberate re-tagging says so,
`(Int64(j) : I)`, and is checked like any other integer.

**What is guaranteed.** A read or write of a slot whose index type is NAMED
is bounds-safe: its subscript is either PROVEN or CHECKED at run time
(BL8006, in both lanes; against the static extent, or the array's own
`extents` when the extent is only known at run time). Proven is a closed
list that trusts no type: a compile-time-checked literal, a bare variable of
exactly that index type that is not bound to an unproven value (an iteration
index -- a lambda parameter a `range<I>` / `0..n` operand feeds -- a named
function's parameter, whose callers pass through the same checks, or a
`let` of a proven value), an emitted cast or guard, and a halo window read.
Any other lambda parameter of an index type (a kernel over a key column, a
`mask` predicate, a `sort` key, a `>>@` stage) receives data, as does each
leaf of a destructured `let`.
Everything else -- a position, a plain `Int64`, a `Nat<_>` wildcard, a
branch, a call, an element read out of an index-typed array (foreign-key
DATA, including a kernel parameter the loop feeds from such an array) -- is
checked. A string key subscripting a string `EnumIdx` slot is mapped to its
label's ordinal, a key that is no label stopping with BL8006. The checks
are what the BL4003 untagged-integer advice points at: iterating with `range<I>` (or `halo<I, ...>` for neighbors)
removes them. The guarantee follows a named array into a generic parameter:
an array parameter whose UNNAMED axis (`m: T^1`, `Float64^1`, an anonymous
`Idx<n>`) the body reads at a computed position -- or hands to a parameter
that does -- is checked again for each call passing a NAMED axis there, as
an ARRAY INSTANCE (§4.3) with the parameter typed as the argument, so the
read is judged, proven or guarded against the name; a parameter pack's
element (`P[0](k)`) is checked against its own extent for every caller. Not
covered: a computed subscript into an ANONYMOUS index slot
(an array without a named index type) is not checked -- name the index type
to get the guarantee; compact, compound, sparse and ragged slots keep their
own disciplines (a compact group's LITERAL coordinates are range-checked at
compile time, each against the group's extent); compiler-synthesized buffers and indices (`let rec`
prefixes, which read zero past the prefix by design, reduce desugars, AD
sweeps) own their walks. (The rank-2 offset arithmetic behind the proven
case is verified against a failure model; proofs.md §Safety.)

This is the index-level mirror of physical units (§2.4): same mechanism, same
error class.

## 4. Array Types

### 4.1 Three levels

| Level | Form | Known | Use |
|-------|------|-------|-----|
| Fully abstract | `T^r(σ)` | rank, symmetry class | arity-polymorphic signatures, typing rules |
| Index-typed | `T^(I₁, I₂, ...)` | index structure | kernel bodies, combinators |
| Fully concrete | `Array<V like I₁, ...>` | value type, indices, extents | data declarations |

`T^r ≡ T^r(1, 2, ..., r)` (dense).

**What `^` builds is decided by its left-hand side.** A TYPE gives an array
type; a UNIT gives a unit power (`Unit area = meters^2`, `Float<meters^2>`,
`seconds^-1`); a VALUE -- a literal or a static, including inside an extent
such as `Idx<N^2>` -- gives an arithmetic power. Among types, a single
capital that names no declared type is a type VARIABLE (`T^1`, any element);
a type that IS declared or built in fixes the element instead (§13.2's
Array-Intro with T a base type): `Float64^1` is a dense rank-1 array of
`Float64`, any extent, `Speed^1` (after `type Speed = Float<mps>`) one of
`Speed`, a declared struct's name the same, and `Float64^0` is `Float64`.
The rank must be an integer literal, and a head that is itself an array type
(`type Row = Array<...>`, then `Row^1`) is refused rather than nested (both
BL1004). The concrete caret is DENSE only, like `Float<day>^2`: there is no
abstract-level symmetry spelling, so a compact argument (`SymIdx`,
`AntisymIdx`, ...) is refused at the call (BL3001) rather than densified --
write `T^2` to accept any symmetry class. Transitions: symmetry inference (§11) takes
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

```blade sketch
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

```blade sketch
let models: Array<(Params → TimeSeries) like LatIdx, LonIdx>
models(lat, lon)(params)(t) : Float
```

**Applied unannotated parameters.** Applying a parameter the program never
annotated (`c(0)`) says only that it is an ARROW; whether it is an array or a
function is decided by what is passed to it. An application to integer-valued
arguments admits both readings -- an array whose rank is the argument count,
read element-wise, or a function of integers -- and an application to any
other argument (`f(2.0)`) only the function. A `function` declaration's such
parameter is generic: each call decides. A call passing a function calls the
declaration, whose application is a call; a call passing an array calls an
ARRAY INSTANCE of it -- the declaration checked with the parameter typed as
that array -- whose application is an ordinary subscript, checked like any
other (section 3.10): an unproven position into a named axis is guarded
(BL8006), a literal is judged against a known extent, and an unannotated
parameter used as the subscript is pinned to the axis's index type. An array
instance is not a second declaration: whatever names it -- a run-time panic's
frames in either lane, a diagnostic, `blade plan`, the editor's bindings and
references, the REPL -- names the declaration as written, so the arrow reading
surfaces only as the call's implicit equivalence of the parameter with the
array it is given (a generic declaration's per-type specializations are named
the same way). A top-level `let` of a lambda that applies such a parameter IS that declaration
when nothing can tell them apart -- the program only ever APPLIES it, and it
has no `mut`, annotation, `where` clause or parameter defaults, and captures no
binding that is ever assigned -- and is generic likewise. Any other lambda (one
used as a VALUE: a kernel, an argument, an alias) is one body, so its first use
decides for every later one; an array makes its application a subscript,
checked the same way. Either way the body reads
ONE value from the application: a higher-rank array (a partial read) or a
function returning an array is refused at the argument, as is a scalar, or a
function whose parameter is not an integer where the body passes one. An
application nothing ever reaches is read as a function.

```blade
function first(c) = c(0) + 1.0
let a = first([1.0, 2.0, 3.0])
let b = first(lambda(i) -> 10.0 * Float64(i))
// EXPECT: a = 2
// EXPECT: b = 1
```

```blade
let u = lambda(c) -> c(0) + 1.0
let ua = u([1.0, 2.0, 3.0])
let ub = u(lambda(i) -> 10.0 * Float64(i))
// EXPECT: ua = 2
// EXPECT: ub = 1
```

An application of such an application's result (`c(i)(j)`) continues the same
arrow: dimensional currying makes it the read `c(i, j)` of an array of rank 2
(each application reads a view, the last one value), or two calls of a function
returning a function. A call judges the whole chain at once, so a vector, a
rank-3 array, a function returning a value and a function of two parameters
(the body calls it with one) are refused at the argument.

```blade
function cc(c) = c(0)(1)
function f0(j: Int64) = Float64(j)
function f1(j: Int64) = 10.0 + Float64(j)
function pick(i: Int64) = if i == 0 then f0 else f1
let a = cc([[1.0, 2.0], [3.0, 4.0]])
let b = cc(pick)
// EXPECT: a = 2
// EXPECT: b = 1
```

**Lambda arguments.** A lambda literal passed where the callee DECLARES a
function type is checked against that type: its unannotated parameters take
the declared parameter types before its body is typed (for a generic
declaration, this call's instance of them, which the call's other arguments
teach first). An annotated parameter keeps its annotation, and a conflict, or a
parameter count other than the slot's, is refused at the lambda. A `let`-bound
lambda was typed at its binding: a parameter its body left open is bound by the
first declared slot or arrow parameter it reaches -- if the body only applied
it. One the body used as a value (`lambda(v) -> v * 2.0`) was typed as a single
value, and an array or function slot refuses it: annotate the parameter.

A function type's parenthesized list is its PARAMETER list: `(A, B) -> C` takes
two arguments -- the type a two-parameter function or lambda has as a value --
and `A -> B -> C` is the curried function returning a function. A function of
ONE tuple writes the tuple as its one parameter: `((A, B)) -> C`, or
`Tuple<A, B> -> C` (section 2.8).

```blade
function ap2(f: (Float64^1, Float64) -> Float64, x: Float64^1, s: Float64) = f(x, s)
let t = ap2(lambda(v, k) -> reduce(v, (+)) * k, [1.0, 2.0], 3.0)
// EXPECT: t = 9
```

```blade
function ap(f: (Float64^1) -> Float64, x: Float64^1) = f(x)
let r = ap(lambda(v) -> reduce(v * 2.0, (+)), [1.0, 2.0])
let g = lambda(v) -> v(0) + v(1)
let s = ap(g, [3.0, 4.0])
// EXPECT: r = 6
// EXPECT: s = 7
```

**Poly-indexing** **(planned)**: `A(indices)` with a tuple of length rank(A);
`all_indices(A)` iterates all valid tuples respecting structure. Use for rank-polymorphic
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
licenses UP TO `depth` S-dim levels of argument x, outermost first, to carry
threads; a cap on the structural strategy, not a demand, so the emitted pragma
is the structural choice restricted to licensed levels, and the pragma sits on
the outermost licensed level even when that is not level 0; the licensed levels
are the EXTERNAL ones — those x contributes to a nest built AROUND f, i.e. a
caller's co-iteration when f is used in kernel position — never a loop f's own
body generates over x, which is licensed by a clause on that loop's kernel;
a `Tuple<k>` parameter is one schema node whose levels are its k rows in order,
so `omp(p: n)` licenses its first n rows; `cuda` and
other backends substitute), and T-dimension spec (`tdim({extent, symm, name})`
records) when output dims don't derive from inputs.

```blade sketch
function name(x₁: T₁^r₁, ..., xₙ: Tₙ^rₙ)
where comm(xᵢ, xⱼ), omp(x₁: 2), tdim({ extent: e, symm: k, name: "freq" })
-> T_out^r_out
= body
```

Return type follows `where` because it may depend on constraints (`comm` can
produce `SymIdx` outputs). Nested `function` declarations desugar to
immutable lambda bindings (internally the same marker `let static` uses).

**Unannotated parameters.** A parameter written without a type is an
anonymous type variable -- `function inc(x) = x + 1` is `function inc(x: T) =
x + 1` -- so each call's argument decides its type (one monomorphized body per
instance), never a default. A literal's type is its spelling (`1` is an Int64,
`1.0` a Float64), so `inc(6)` is the Int64 7 and `inc(6) / 4` is the integer
quotient 1, while `inc(2.5)` is 3.5. Everything §2.4 says of `x: T` holds:
beside a float (`x + 1.0`) an Int64 argument is refused at that argument
(BL3019; the caller writes `3.0` or `Float64(n)`). Two generic variables mixed
by arithmetic are ONE variable -- there is no promotion type -- so `function
add(x, y) = x + y` takes one type per call, and its arguments must agree
EXACTLY: no argument converts implicitly, in either order and literals
included, so `add(2.5, 1)` and `add(1, 2.5)` are both refused at the second
(BL3999), as is an Int64 and a Float64 variable -- the caller writes
`add(2.5, 1.0)` or `add(2.5, Float64(n))`. A
parameter the body uses as a subscript is pinned to that index (§3.10), one it
applies is an arrow (§4.3); a top-level `let` of a lambda that is only applied
is the declaration, and a lambda used as a value is one body whose first call
decides its open parameters.

```blade
function inc(x) = x + 1
let a = inc(6) / 4
let b = inc(2.5)
function add(x, y) = x + y
let c = add(2.5, Float64(1))
// EXPECT: a = 1
// EXPECT: b = 3.5
// EXPECT: c = 3.5
```

### 5.2 Lambdas

`lambda(a, b) -> expr`, optional type/rank annotations, `where` clauses
(`comm`, return type) as on functions, block bodies in braces. Lambdas are
values that may capture; they are NOT required to be pure. Array captures are
unit-checked (§3.10). Parameter types infer from context; array-typed
parameters need explicit rank annotations.

The one purity rule the checker enforces is about races: a PARALLEL kernel body
(`where omp(...)` / `cuda`, and anything nested in one) may not write a
binding captured from outside it -- its cells run concurrently, so the store
would be a data race (BL4005). A serial body's write to a captured `let mut`
(or a named function's write to a module-level one) is still accepted as an
ordered side effect; the optimizer treats such calls as barriers (CSE never
merges across them).

A lambda is a value that may outlive the scope that made it: returned from a
function (`function mk(i) = lambda(j) -> i * 10 + j`), stored in a tuple, array
or struct, passed on. Its captures therefore have VALUE semantics -- the
closure holds the captured bindings' values, so `mk(2)(3)` is 23 after `mk`
has returned -- with one exception: a captured binding that is REASSIGNED (by
the lambda or by its scope; an element store into an array is not a
reassignment) is SHARED between the lambda and its scope, so each sees the
other's writes. A lambda sharing a binding lives only as long as the scope
that defines the binding: it may be called, used as a kernel, or named by a
`let` used that way, but any use through which it could leave the scope --
a function's result, a tuple/array/struct element, an argument to a call whose
result can hold a function, an assigned value, a capture of another such
lambda -- is refused (BL4005). Module scope lives as long as the program:
sharing a module-level binding never limits a lambda, and a module-level
`let`'s own value (`let c = { let mut n = 0; lambda(k) -> ... }`) is not an
escape.

A `where comm(x, y)` clause is TRUSTED when the body's symmetry cannot be
decided, and REFUSED (BL4013) when it is refuted: by a proved sign law
(antisymmetric body) or by a concrete counterexample -- the body evaluated at
sample points and their swaps (`x / y` is 2 at (2, 1) and 0.5 at (1, 2)).
Under `reynolds(...)` the clause is an iteration license, not a claim about
the bare kernel, and is never refuted (§5.3).

Sections and partial application: operator sections `(+)`, `(*)`, ... as
kernels, and single-wildcard `f(_, y, z)` (multiple wildcards rejected — use a
lambda). A function applied to fewer arguments than it declares is curried
(`f(5)` of a 4-parameter `f` awaits the other three).

### 5.3 Reynolds operators

`reynolds(g)` is the VALUE-LEVEL symmetrizing wrapper: it builds the kernel
`K(x₁..xₙ) = Σ_σ g(x_σ(1)..x_σ(n))` (with `Antisymmetric`, the sign-weighted
sum), permuting the kernel's value arguments. (Restricting to a subset of
positions, `positions=[...]`, is **(planned)**; the wrapped kernel must be a
lambda over scalar values.) K is commutative by construction — reynolds manufactures H = Sₙ. What
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
  question. Antisymmetric Reynolds zeroes diagonals and negates on transposes
  by storage construction.
- **Distinct arrays**: K is commutative but Stab = {id} — the output is DENSE
  and not index-symmetric (`Out(i,j) = g(A(i),B(j)) + g(B(j),A(i))`, which is
  not `Out(j,i)`; pinned by corpus reynolds/013). Reynolds does not substitute
  for identity.

Distinguish this from the INDEX-LEVEL (per-dimension) Reynolds of the proof
tower — `R(i₁,i₂,j₁,j₂) = Σ over index swaps` — which genuinely has
per-dimension product symmetry with lossless canonical access (proofs.md
§Core, `reynolds_full_product_symmetry`). That is a different, stronger
operator (it reads every array at every permuted index, n!^d terms) and is
not currently a surface construct.

### 5.4 Static functions and type-level computation

`static function` may capture only `let static`/static values and is callable
at compile time; `let static` values close over literals, other statics, and
static applications. Static functions reach type positions through a
`let static` (`let static m = triangle(n)` then `Idx<m>`; an index type's
argument is a static expression over names and literals, plus `arity(p)`, not
a general call, so `Idx<triangle(n)>` itself does not parse). No totality proofs (vs Idris/Agda); explicit marking
(vs C++ constexpr's syntactic restrictions). `static type` functions
(`Vec<N>`, **(planned)**; not parsed today) are compile-time-only: not storable, not passable, not returnable
at runtime — keeping type-level computation decidable.

## 6. Core Operations

### 6.1 Elementwise and outer operator pairs

| Elementwise | Outer | Op |
|-------------|-------|----|
| `+` `-` `*` `/` `%` `^` | `[+]` `[-]` `[*]` `[/]` `[%]` `[^]` | arithmetic |
| `==` `!=` `<` `<=` `>` `>=` | `[==]` ... `[>=]` | comparison |
| `&&` `\|\|` | `[&&]` `[\|\|]` | logical |

```blade sketch
A + B    =  method_for(zip(A, B)) <@> lambda((a, b)) -> a + b   // co-iteration
A [+] B  =  method_for(A, B) <@> (+)                             // cross-iteration
```

`(+)` remains the scalar kernel for combinator use; all three coincide at
rank 0. Bracketed ops inherit primitive symmetry: `A [*] A` iterates
triangularly automatically.

### 6.2 Primitive symmetry annotations

`(+)`/`(*)` Symmetric, `(-)` Antisymmetric, `(/)` Asymmetric — the compiler
infers `comm`/`anticomm` for kernels built from them (`a + b` ⇒ comm(a, b)).

### 6.3 Geometric primitives and reductions

**(planned)** as core names: `norm` (equivariant → invariant), `dot`
(symmetric; invariant result), `cross` (antisymmetric; representation per
domain library), `sum`/`mean` (rank-reducing, equivariance-preserving),
`min`/`max` (invariant-only — ordering requires invariance; today `min`/`max`
exist only in static evaluation). What exists today: `reduce` (§6.4),
`prodsum(a, b)` (the fused dot product), `gram`/`gram_apply`, the standard
library's `stats.mean`/`variance`/`stddev`, and `import math as m` for
`m.matmul`, `m.solve`, `m.lu`/`m.lu_solve`, `m.eigh`. Equivariance signatures live in the ML module
([features/equivariant-nn.md](features/equivariant-nn.md)); the core carries
the annotation hook only.

### 6.4 Additional value operators (v7-established)

- `gram(...)` — Gram matrix construction over dense, symmetric, or Hermitian
  structure (value-checked against independent oracles).
- `gram_apply(A, B, x)` — the ACTION of `gram(A, B)` on a vector, `A·(Bᴴ·x)`,
  without forming the Gram matrix: A is m × n, B is p × n, x has p cells,
  the result m; two rank-1 temporaries and never an m × p pool. The factors
  obey `gram`'s rules (rank 2, plain axes, one element type, contracted
  extents agree), x must have B's leading extent (static: refused; dynamic:
  BL8011), units multiply through both contractions, and complex factors
  conjugate B exactly as `gram` does. Its reverse-mode adjoint action is
  itself a `gram_apply` (`x̄ += gram_apply(B, A, ȳ)`); the factor cotangents
  are outer products of the cotangent with the two n-cell intermediates.
- `hermitian(A)` — adjoint.
- `conj(x)` — componentwise conjugation (identity on reals).
- `reduce(A[, kernel[, init]][, axes = n])` — a LEFT fold, in ascending
  storage order, of the innermost `n` dimensions
  (`reduce([1, 2, 3, 4], lambda(a, b) -> a - b)` is `((1 - 2) - 3) - 4 = -8`), `n = 1` by default (rank k in, rank k−n out;
  `n = rank(A)` is the full fold to a scalar); default kernel `(+)`; see
  [features/sql.md](features/sql.md) §10 for typing details, the axis-count
  rules and the empty-input rule. Under the default `n = 1`, an anonymous
  deferred outer product `reduce(method_for(A, B) <@> lambda(a, b) -> f(a, b),
  op[, init])` (named rank-1 sources, no `where` clause on the map kernel, a
  `(+)`/`(*)` section or a seeded fold) is evaluated row by row -- an outer
  apply over `A` whose row kernel is the fused fold over `B` -- and the
  |A| × |B| product is never materialized; the values are those of the
  materialized route (the same left fold per row).
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

```blade sketch
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

```blade sketch
range<I>       // enumerate I in storage (= lex) order:  λi:I. i
reverse<I>     // reversed
```

(`blocked<I, K>` was a spec-level placeholder for block iteration; it never had a
parser arm and is gone. Block structure is a property of the AXIS: `Chunked<I, K>`
and `segments(A)`, plans/structural/07.)

`range<CompoundIdx<...>>` emits mask-true tuples. Virtual and real arrays
compose in one loop:
`method_for(range<I>, A, B) <@> lambda(i.., a, b) -> ...` — this is how
kernels receive indices without breaking index anonymity.

**Anonymous ranges.** `m..n` (half-open, Int64 elements) is a virtual array
equivalent to `range<Idx<n-m>> + m`: `0..5` enumerates `0,1,2,3,4`. Bounds
must be static (a `let static` name folds). The index type is anonymous, so
its elements index any plain-`Idx` slot without a tag cast — which makes it
the natural spelling for index generation feeding *arithmetic*, where
`range<I>` would tag the result and demand `(k : I)` casts downstream.

A plain dense range — `m..n` or single-slot `range<I>` — is an ordinary
rank-1 array value: it lifts elementwise (`(0..5) + 10`,
`x0 + dx * Float64(0..n)` — the coordinate-axis idiom), folds
(`reduce(0..n, (+))`), and materializes when bound bare or forced
(`let xs = 0..n`, `|> compute`). Inside a loop nest it never materializes —
the nest peels it as induction values. Compound/sparse/halo ranges and
multi-slot `range<I, J>` enumerate coordinate *sets*, not element values, and
exist only as nest inputs.

**`range<SymIdx<r,N>>` / `range<AntisymIdx<r,N>>` hand the kernel PREFIX
OFFSETS, not canonical indices.** A multi-rank slot contributes one param per
rank component (§7.2's rank rule), and each param is bound to its own raw
triangular loop counter — the cell's *packed storage coordinate*, left-justified
into a shrinking row (`canon_left_justify`) — with the level's bound
dependencies and strict offset left out. The canonical tuple is the running sum:

```
SymIdx<r,N>     (inclusive i₀ ≤ … ≤ i_{r-1}):   canonical[m] = p₀ + p₁ + … + p_m
AntisymIdx<r,N> (strict   i₀ < … < i_{r-1}):    canonical[m] = p₀ + p₁ + … + p_m + m
```

So the natural reading `A(p₀) * A(p₁)` is silently WRONG — it agrees with the
canonical one only where every earlier param is 0, i.e. on the first row, which
is exactly enough to make a spot check look right. The symmetric outer product
is spelled `A(p₀) * A(p₀ + p₁)`. Pinned at r = 2 and r = 3 in
`tests/corpus/loops/170`–`173` (both the correct and the naive spelling), worked
example in `172`.

This is **observed behaviour, not endorsed semantics** — the intended reading is
canonical, and this paragraph documents the divergence so nothing is built on
the current convention by accident:

- `TypeCheck.expandedRows` (the seam that widens a multi-rank slot into r params)
  describes each param as "the index value at that slot".
- The lowering (`CodeGen.genElementBindingNew`, `VirtualRange` arm) is written
  for rank-1 `range<I>` — "kernel param gets the loop index" — and never consults
  `level.BoundDependencies` / `level.StrictOffset`, which its sibling dense and
  fused arms both apply explicitly to reach the absolute coordinate. The
  interpreter (`Interp.Loops.peelElement`) mirrors it arm-for-arm, so the two
  differential twins agree on the same omission and no diff gate fires.
- The correction is the `deps + strict` expression already present two arms down;
  the risk is not cost but that any pre-existing `range<Sym…>` kernel written
  against the offsets flips meaning, hence the pins first.

Two further defects sat at the same seam. Both are now fixed; the mechanism and
the residual limits are recorded here because the fix trades one property away.

- ~~The component params carry the *group's* tag, not the component space's.~~
  FIXED. An index record has ONE `Tag` field for the whole group
  (`IRIndexTypeG.Tag`, "name (index space matching)") and
  `elemTypeForIterationIndex` hands it to EVERY component param, so overwriting
  it with the group's name typed both params of `type S2 = SymIdx<2, I3>` as
  `Nat<S2>` — a type no component index of S2 can inhabit — making a hard
  BL4003 out of indexing the `I3`-tagged array they range over, while the same
  group spelled inline had `Tag = None`, untagged `Int64` params, and merely
  warned. Whether the group happens to be *named* is not a semantic
  distinction.

  The minimal fix named here is the one taken: a multi-rank record's `Tag` now
  names the COMPONENT space, which is what "index space matching" means for the
  values the slot actually produces, and both spellings check with no warning.
  It is the fourth carve-out from the nominative-alias rule, beside the irreps,
  bad-spec and wreath ones, each there for the same reason — on those records
  `Tag` is carrying something the alias name would destroy.

  THE TRADE: like the wreath carve-out, a multi-rank alias now names the class
  for readability but mints no distinct nominal identity, so
  `Array<F like S2>` and `Array<F like SymIdx<2, I3>>` are the SAME type
  (pinned in `175`). Giving a group alias its own identity *and* keeping the
  component tag needs a second tag field on every index record — a type-system
  change, not a fix. Pinned as `tests/corpus/loops/174` (anonymous group, still
  warns: it has no component tag to inherit) and `175` (named group, silent).
- Naming only the component — `range<SymIdx<2, I3>>` — is the spelling that
  works, and it is the bullet above's "minimal fix" already realized for this
  one form. The base slot resolves to `I3`'s record and inherits its `Tag`, so
  both component params type as `Nat<I3>` — the space they actually range over
  — and the program checks with NO warning, then runs to 174's values. Pinned
  as `tests/corpus/index-types/239`.

  Until that resolution existed the same text typechecked and then failed
  codegen outright, emitting `S_extents[0] = __range0.extents[0];` against an
  `__range0` that is never declared: `Parser.parseSymIdxBase` admits only the
  `Idx`/`IrrepsIdx` KEYWORDS as an index-type base, so the bare name `I3` was
  read as an extent EXPRESSION, and a name that resolves to no value fell
  through to a symbolic extent that a VIRTUAL range has no runtime object to
  read. A bare name that resolves to neither a value nor an index type still
  has no reading, and is now refused at the range seam rather than left to g++
  (`tests/corpus/index-types/240`).

  Component-naming and group-naming were fixed separately and by different
  means — this bullet by resolving the base slot to `I3`'s record, the bullet
  above by stopping the alias name from overwriting a multi-rank tag — but they
  now agree: `range<SymIdx<2, I3>>`, `range<S2>` and the inline anonymous form
  all iterate the same space, and the two that have a component tag to inherit
  both check silently.

### 7.4 For-loop syntax

`for` is surface syntax over the constructors — it builds iteration objects,
not imperative control flow. One side carries arrays/indices, the other the
kernel; `in` accepts virtual arrays only:

```blade
let static N = 3
type I = Idx<N>
let A: Array<Float64 like I> = [1.0, 2.0, 3.0]
let B: Array<Float64 like I> = [4.0, 5.0, 6.0]

let r1 = for (A, B) in range<I> <@> lambda(a, b, i) -> a * b + Float64(i) |> compute  // method_for style: cells, then indices
let r2 = (for lambda(a, b) -> a * b) <@> (A, B) |> compute                            // object_for style
let loop = for (A, A) in range<SymIdx<2, N>>                    // let-bound, awaits kernel
let op   = for lambda(a, b) where comm(a, b) -> a * b          // let-bound, awaits arrays
let r3 = op <@> (A, A) |> compute                              // SymIdx<2, N> storage
```

A `for <kernel>` former's inline lambda body extends through the apply level
(§15.1), so the object_for style parenthesizes the former before `<@>`. A
poly former over a symmetric pack (`for args in SymIdx<arity(args), N> ...`)
is **(planned)**; arity-polymorphic kernels are applied with `object_for`.

A co-iteration kernel over `for (A, B) in range<I, J>` takes the operands'
CELLS first -- `a` is `A(i, j)`, already indexed -- and then one parameter per
range slot, the loop indices (`lambda(a, b, i, j)`; the indices may be
omitted). The in-clause range is a trailing virtual operand, which is why its
parameters come last (tests/corpus/loops/094). Applying a cell or an index
value to arguments (`a(i, j)`) is refused (BL3003).

### 7.5 Recursive arrays

Arrays are functions (§4.3) and functions recurse, so arrays recurse. A
sequential recurrence — time-stepping, training epochs, an RNG stream — is a
**self-referential array definition by structural induction on the extent**,
not imperative control flow:

```blade sketch
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
  §8.2's identity base case for recursive kernels (`f(())` returns f's
  identity element; today that arm is written explicitly, §8.2).

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

#### 7.5.1 The `while` guard: iterate to convergence

The inductive arm may carry a **convergence guard**, which turns the declared
extent from a trip count into a **budget**:

```blade sketch
type It = Idx<200>                       // a BUDGET, not a trip count
let rec u: Array<Float like It, Y, X> =
    match u with
    | zero -> zero
    | zero :: s -> zero :: u0
    | prefix :: n while residual(prefix(n - 1)) > tol -> prefix :: sweep(prefix(n - 1))
```

Semantics, in three parts:

- **Defined** up to the first `n` at which the guard is false. The guard is
  a predicate (it unifies with `Bool`) and reads the prefix under exactly the
  rules the slice does — same legal index shapes, same implicit zero history,
  so a guard reading `prefix(n - 1)` at `n = 0` reads zero rather than
  garbage.
- **Frozen** afterwards: the last written slice repeats to the end of the
  extent. The family therefore stays total at its declared type — every
  consumer, fold, and interior read sees a full trajectory, and the
  hand-written idempotent-freeze idiom this replaces agrees with it bitwise.
- **Aborts** (BL8010, naming the array and the budget) if the guard is still
  true when the budget is exhausted. This is the point of the construct: a
  solve that did not converge cannot silently pretend it did, which is
  precisely what a fixed-trip-count loop offers no way to distinguish.

The guard can only stop the sweep EARLY, so the extent stays static and the
decidability fence above is untouched. What it buys is cost (the emitted
sweep exits instead of running the full budget) and diagnosis, not
expressiveness. A `while`-guarded array is not differentiable in v1: the
stopping ordinal is data-dependent, so `grad` refuses it rather than
linearizing a trip count that depends on primal values.

## 8. Arity Polymorphism

### 8.1 The concept

Rank polymorphism varies the shape of one input (`sum : T^r → T^0`). Arity
polymorphism varies the NUMBER of inputs, and the arity determines output
rank, loop depth, and symmetry:

```blade sketch
let moment = object_for(comoment)   // comoment(a: Poly<T^1>) where comm(a) -> T^0
moment <@> (data, data)             // covariance   (rank 2)
moment <@> (data, data, data)       // coskewness   (rank 3)
```

(docs/quickstart-1.md §10 has the complete, compiled program.)

Variadic functions cannot express this: their output type is fixed regardless
of argument count. Arity-dependent output typing requires type-level arity —
which Blade provides through the loop-object judgment (§13), not through
general dependent types.

### 8.2 Kernel syntax

```blade sketch
function kernel(a: Poly<T^k>) -> T^m
where comm(a)
```

`Poly<T^k>`: a pack of rank-k slices. In the body: destructuring
`let head :: tail = args` (left-associative), indexing `args[k]` (`[]` =
structural access), `arity(args)` (pack size), and iteration over the pack via
the poly former `method_for(range<Idx<arity(p)>>)`. A recursive kernel
matches `arity(args)` and writes its `| 0 ->` arm with the kernel's identity
element as a literal (`1` for a product; `zero` there is the zero VALUE, not
the identity — resolving it to the surrounding operation's identity is
**(planned)**). (The spec's base-case-free
recursion, the tuple-pattern spelling `let (head, tail) = args`, and an `nth`
recursion-depth variable are **(planned)**: today each is refused with a
diagnostic naming the built form -- the missing base arm (BL7004), the cons
pattern (BL3999), an explicit depth parameter (BL3999).) Nested tuples preserve structure (`arity` counts
top level; `comm` does not penetrate sub-tuples; no deep indexing —
destructure instead): `object_for(f) <@> (A, (B, C))` is arity **2**, not 3 —
`(B, C)` is one tuple-typed argument, distinct from
`object_for(f) <@> (A, B, C)`'s arity 3 (§2.8). A tuple-typed operand at a
`Poly` position is one argument, not iterable components, so passing an
actual tuple *of arrays* there is refused (`BL3002`): pass the components as
separate operands, or use a kernel whose parameter there is annotated
`Tuple<k>` instead of folded into the `Poly` pack.

A DIRECT call of a function with a pack parameter reads the pack off the
argument list: a lone pack takes every argument (`f(a, b, c)`); one pack beside
fixed parameters takes either the argument in its place -- a parenthesized group
`pk((a, b), k)` or a lone element `pk(a, k)` (a one-element pack) -- or, when
there are more arguments than parameters, the surplus written flat in its place
(`pk(a, b, k)`); several packs take one argument each, a group or a lone
element. Anything else is refused at the call (`BL3002`): a group among flat
elements, too few arguments, a flat list against two packs, and a pack passed as
a tuple-typed variable (`let t = (a, b); pk(t, k)`) -- write the group out at
the call.

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

```blade sketch
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

**Reduction join.** `<&!>` also joins REDUCTIONS, not only maps:

```
object_for(<&!>) <@> (r₁, …, r_k)   :  Reduction σ₁ × … × Reduction σ_k → σ₁ × … × σ_k
reduce([r₁, …, r_k], (<&!>))        :  the same, as an associative join chain
```

where each `rᵢ` is `prodsum(x₁ .. x_m)` or `reduce(c, op[, init])`. Each leg
normalizes to its `(traversal, fold, seed)` triple, the traversals fuse into one
nest, and the legs accumulate side by side — so a join is the shared-fold
terminal generalized to a fold PER leg. Unlike the map form, the legs need not
come from the same MethodLoop: they must only agree on the joint index space
(equal rank, and equal extents where statically known), because a reduction
writes no cells and so has no output shape to reconcile.

Legs referring to the same **named deferred** computation evaluate it once per
joint cell; the name is the declaration, whether it stands in an operand slot
(`prodsum(e, v)`) or is a leg's own traversal (`reduce(e, (+))`) -- the leg
folds the shared cell, not a second evaluation. One leg is the identity (a scalar, not
a 1-tuple); zero legs has no index space and is refused. Both spellings are
pinned in `tests/corpus/`; note that `object_for(<&!>) <@> (c₁, …, c_k)`
over deferred MAPS keeps its existing reading (n-ary map fusion answering k
arrays) — the legs, not the operator, say which join is meant.
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

```blade sketch
(object_for(f) >>@ object_for(g)) <@> A  ≡  (method_for(A) <@> f) @>> (method_for(A) <@> g)
```

**Rank-0 convergence** (proved): for rank-0 kernels,
`object_for(f) <@> (A, B) ≡ method_for(A, B) <@> f`; wrapping is idempotent.
This is the license for pseudo-native syntax (§15.6): `A + B` commits to
neither constructor. Compose-Apply is the inductive case; rank-0 convergence
the base case; together they characterize when the two entry points coincide.

### 10.4 Choice, zero, guard (MonadPlus)

- `()` / `method_for()` — the empty loop, identity for `<*>`, base case for
  arity recursion (the `| 0 ->` arm of a recursive `Poly` kernel is the
  kernel applied to `()`).
- `zero` — the zero kernel: S-dims from arrays, no T-dims. (Resolving `zero`
  to the operation-appropriate identity — 1 under `*`, 0 under `+` — in arity
  recursion base cases is **(planned)**: today it is the zero value, so a
  product base case is written `| 0 -> 1`.)
- The zero VALUE `zero` denotes at a type (in value position, at an
  annotation such as `let p: P = zero`, or as a generic `zero` once
  monomorphization fixes its type) is defined structurally:
  - numeric scalars: `0` at their own width (`0.0f` for Float32, the complex
    zero for a complex type); Bool: `false`; String: the empty string `""`;
  - a struct: the struct with EVERY field `zero`, recursively (a nested
    struct, a String field `""`, a Bool field `false`, a complex field the
    complex zero);
  - a tuple: componentwise;
  - an array: the zero-filled array of its declared shape -- at an array
    annotation, and for an array-typed struct field or tuple component. A
    field or component needs a STATIC shape (every axis a plain rank-1 index
    with a literal extent, at most 65536 cells), and so does an array
    annotation whose element is a struct, String or tuple; an array
    annotation of a numeric element takes any nominal or static plain axes.
  - A sum type has no zero: no rule picks a canonical zero variant, so
    `zero` at a sum type -- or at a composite with such a part, or with an
    array part whose shape is not static -- is refused (BL7004 in code
    generation, the same refusal in the interpreter).
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

```blade sketch
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

**Statement terminators.** A statement or declaration ends at a newline, a
`;`, or a closer (`}` `)` `]` `|` `,`, end of file). Anything else on the
same line is refused (BL1001): two expressions side by side have no meaning --
there is no implicit multiplication and application needs parentheses -- so
`let a = 1 b` and `{ let a = 2.0 x ... }` are errors, not a statement plus a
silently printed or discarded `b`/`x`. Inside braces, where the lexer has
dropped the newline tokens, "on a later line" is decided from source lines.
`;` separates statements on one line at top level as in blocks.

**Line continuation.** A line that opens with a binary operator (`+ - * / %
^ == != < <= > >= && || :: ..`, the bracketed outer forms, or any combinator
such as `|>` `<@>` `>>@`) continues the expression on the line above, as if
the line break were a space -- it joins the innermost expression still open,
so after `if c then a else b` it extends `b`. For the arithmetic / comparison
/ logical operators the line must be indented PAST the column where the
statement began; at that column (or left of it) the line is refused (BL1001),
because `let y = x` over `- 3` reads as two statements to some readers and as
`x - 3` to others. Write `(-3)` for a statement that begins with a negation.
Inside `()`/`[]` opened within the statement a line break is always
whitespace. A line opening with `(` or `[` begins a new statement (it never
calls or indexes the line above); `.field` chains are line-insensitive.

**Operator precedence**, loosest first (`e : T` is the postfix type
annotation):

| Level | Operators | Associativity |
|---|---|---|
| assignment | `=` `+=` `-=` `*=` `/=` | right |
| annotation | `e : T` | postfix |
| named infix | `:name:` | left |
| pipeline | `\|>` `\|@>` | left |
| choice | `<\|>` `<\|:>` | left |
| parallel | `<&>` `<&!>` | left |
| bind / compose | `>>=` `>>@` `@>>` `>>` | left |
| apply | `<@>` `<$>` | left |
| array product | `<*>` | left |
| or | `\|\|` `[\|\|]` | left |
| and | `&&` `[&&]` | left |
| equality | `==` `!=` `[==]` `[!=]` | none |
| comparison | `<` `<=` `>` `>=` (and `[<]` ...) | none |
| cons | `::` | none |
| range | `..` | none |
| additive | `+` `-` `[+]` `[-]` | left |
| multiplicative | `*` `/` `%` `[*]` `[/]` `[%]` | left |
| prefix | `-` `!` | prefix |
| power | `^` `[^]` | right |
| postfix | `f(x)`, `t[k]`, `.field` | left |

Prefix minus binds LOOSER than `^`, as in mathematics: `-t^2` is `-(t^2)`
(so `exp(-t^2)` is the Gaussian) and `-2.0 ^ 2` is `-4`. The exponent is
itself a prefix operand, so `2 ^ -1` parses. Equality and comparison do not
chain: `0 < x < 3` and `a == b == c` are refused with a steer -- write
`0 < x && x < 3`.

**Lambda bodies.** An inline (braceless) lambda body extends through the
apply level and no further: `lambda(x) -> a <@> b |> compute` is
`(lambda(x) -> a <@> b) |> compute`. This holds THROUGH an `if`'s else
branch, a final match arm or a `let` at the body's own nesting depth, so

```blade
type I = Idx<3>
let E = method_for(range<I, I>) <@> lambda(i, j) -> if i == j then 1.0 else 0.0 |> compute
```

computes the whole map (it does not pipe `0.0` into `compute`). Positions a
keyword fences -- an `if` condition and then-branch, a match scrutinee, a
non-final match arm -- and anything inside parentheses keep the full grammar.
A `for <kernel>` former binds its kernel the same way.

**Numeric literals.** Decimal integers and floats (`12`, `1.5`, `2e-3`) take
`_` digit separators between two digits (`1_000_000`, `0.000_1`). `0x` / `0b`
introduce hexadecimal / binary integers (`0xFF_FF`, `0b1010`), read as 64-bit
bit patterns: values up to `2^64 - 1` are accepted and interpreted two's
complement (`0xFFFFFFFFFFFFFFFF` is `-1`). A number glued to a name (`2x`) is
a malformed literal (BL0003), not an implicit product.

### 15.2 Declarations

```blade sketch
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
`static type` functions (**(planned)**); local `function` = an immutable lambda binding
(internally the same marker `let static` uses).

### 15.4 Control and data

- `match ... with | pat -> expr` (values, tuples, guards, sum-type payloads,
  brace blocks); match is an expression; `if c then a else b` is sugar for
  Bool match.
- Sequential recurrences: there is no imperative `for x in RANGE { body }`
  statement — it is expressed as a recursive array (structural induction on
  extent; see §7.5), with folds as `reduce(...)` and parallel maps as
  `method_for(range<...>) <@> lambda(...)`. Iterate-to-convergence is the
  inductive arm's `while` guard, `| prefix :: n while COND -> prefix :: e`
  (§7.5.1): defined until the guard goes false, frozen after, BL8010 if the
  budget runs out with the guard still true. `while` is not a keyword — it is
  recognized positionally between the step variable and `->`, so a program
  may still bind the name.
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
- Modules: `module Name` groups declarations; `import M [as a]` / `from M
  import x, y` resolve other files (stdlib and search roots) into ONE
  program. A non-main module's members are namespaced in the emitted C++
  (`M__x`), and every top-level name is declared once per module (BL2009).

### 15.5 Loops and combinators

```blade sketch
let loop = method_for(A, B)         let obj = object_for(f)
loop <@> f                          obj <@> (A, B)
for (A, B) in range<I> <@> lambda(a, b, i) -> ...
c₁ <&> c₂    (M<@>f) <&!> (M<@>g)    L₁ <*> L₂    o₁ >>@ o₂    c₁ @>> c₂
c >>= k      pure v     f <$> c      guard(p, c)  sequence [..]  replicate n c
c |> compute
0..n         // anonymous range (§7.3): a rank-1 Int64 array value —
             // method_for(0..n), reduce(0..n, (+)), x0 + dx * Float64(0..n)
```

### 15.6 Pseudo-native mathematics

Rank-0 collapse (§10.3) makes conventional notation sound without paradigm
commitment: `a + b`, `a * b`, `-a` lift elementwise over arrays at any rank
(the primitive is always elementwise; rank never changes its meaning);
`[+]`-family gives outer products; contractions are named functions built
from primitives (`reduce`, `prodsum`, `gram`, `gram_apply`, and the math
module's `m.matmul`; `sum`, `dot`, `matvec` as core names are **(planned)**). The S/T machinery stays
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
application; sectioned operators `(+)`, `(*)`, ... in kernel position.

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
| `zip` `stack` `transpose` `join` `decompact` | array combinators (planned: `align` `stencil` `diag` `subset` `split` `reverse(A, d)` `shift`, §2.6) |
| `guard` `sequence` `replicate` | conditional / collection combinators |
| `\|> compute` | materialize |
| `comm(...)` `Poly<T^k>` `arity(a)` | commutativity, arity polymorphism |
| `omp(x: n)` `cuda` `tdim(...)` | backend/parallelism/T-dim clauses |
| `mask` `compound` `intersect` `union` `unique` `contains` `group_keys` `group_by` `sort` `reduce` `extents` | relational forms |
| `gram` `gram_apply` `hermitian` `conj` | linear-algebra value operators |
| `reynolds(g[, Antisymmetric])` | symmetrizing kernel wrapper |
| `range<I>` `reverse<I>` `m..n` | virtual arrays (`m..n` anonymous, half-open) |
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
