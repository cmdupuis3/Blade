# Blade: Quickstart Part 2: Advanced Features

> Assumes [quickstart-1.md](quickstart-1.md). As there, every `blade` block is
> checked by `blade test docs`, and blocks with `// EXPECT:` pins are compiled
> and run.

## Virtual Arrays

Consider two 2D arrays `A` and `B`. For some kernel, we can iterate over the
arrays in an outer-product pattern:

```blade
let A = [[1.0, 2.0], [3.0, 4.0]]
let B = [[10.0, 20.0], [30.0, 40.0]]
function func(a: Float64, b: Float64) -> Float64 = a * b
let loop = object_for(func)
let result = loop <@> (A, B) |> compute     // rank 4: every (A cell, B cell) pair
let r = result(1, 0, 0, 1)                  // A(1, 0) * B(0, 1)
// EXPECT: r = 60
```

This happens seamlessly — but what's happening to the indices? A `method_for`
or `object_for` loop constructs the iteration space and emits the exact
indices into the kernel, so `A` and `B` are indexed correctly at kernel
scope. Usually we don't care what the indices are, just that they are used
correctly.

Sometimes, though, the indices are useful *inside* the kernel. Blade doesn't
allow raw emission of indices; instead we use an array object that has the
shape of an array but no data — slicing it fully just echoes the indices used
to reach it. Since there's no data, it costs nothing at runtime: it simply
becomes part of the loop structure.

This is a **virtual array**. The simplest is `range<I, J, ...>`, which emits
all index tuples of its index space:

```blade
type M = Idx<2>
type N = Idx<3>
let A: Array<Float64 like M, N> = [[1.0, 2.0, 3.0], [4.0, 5.0, 6.0]]
let B: Array<Float64 like M, N> = [[1.0, 0.0, 1.0], [0.0, 1.0, 0.0]]
let viaRange = method_for(range<M, N>) <@> lambda(i, j) -> A(i, j) * B(i, j) |> compute
let direct = A * B
// EXPECT: viaRange = [[1, 0, 3], [0, 5, 0]]
// EXPECT: direct = [[1, 0, 3], [0, 5, 0]]
```

The range pipeline recreates how for-loops work in most languages — not the
efficient way here, but it shows the mechanism: virtual arrays map positions
in the index space to indices emitted into the kernel. Swapping in a different
virtual array changes the traversal. `halo<I, [offsets]>` is the stencil
transformer: it walks the *interior* of `I`, and instead of a bare index the
kernel receives a window `w`, where `w(o)` is the index at signed offset `o`
from the center — so neighbor reads are spelled through the window:

```blade
let A: Array<Float64 like Idx<5>> = [1.0, 2.0, 4.0, 7.0, 11.0]
let d = method_for(halo<Idx<5>, [-1, 0, 1]>) <@> lambda(w) -> A(w(1)) - A(w(-1)) |> compute
// EXPECT: d = [3, 5, 7]
```

Three central differences from five cells: the `[-1, 0, 1]` window shrinks the
traversal to the interior, so no edge clamping ever reaches the kernel.

The other built-in virtual array is `reverse<I>` (the same index space,
descending):

```blade
type I = Idx<4>
let A: Array<Float64 like I> = [1.0, 2.0, 3.0, 4.0]
let backwards = method_for(reverse<I>) <@> lambda(i) -> A(i) |> compute
// EXPECT: backwards = [4, 3, 2, 1]
```

Because index values carry their source index type as a tag (`i : Nat<LatIdx>`),
a kernel can only index a captured array with indices from the *right* index
space — using a `LatIdx` value on a `LonIdx` array is a compile error, even at
equal extents (formalism §3.10).

When no named index type is in play, the **anonymous range** `m..n`
(half-open) is the lightweight spelling, and a plain dense range is an
ordinary rank-1 array value: it lifts elementwise, folds, and materializes
when bound. Index generation that is just arithmetic needs no loop at all:

```blade
let axis = -2.5 + 0.01 * Float64(0..450)   // a 450-point coordinate axis
let total = reduce(0..100, (+))             // 4950
let ids = 0..8                              // materialized iota, [0..7]
// EXPECT: total = 4950
// EXPECT: ids = [0, 1, 2, 3, 4, 5, 6, 7]
```

Reach for `method_for(range<...>) <@> lambda` when the kernel is a real
block, gathers, or composes with other combinators — not for a body that is
merely affine in the index.

## For Loops

`method_for` and `object_for` can be unwieldy, so there's shorthand.
Let-bound `for` loops are sugar for `method_for`/`object_for`:

```blade
let A = [1.0, 2.0]
let B = [3.0, 4.0, 5.0]
function f(a: Float64, b: Float64) -> Float64 = a * b
let loop1 = for (A, B)                 // method_for(A, B)
let result1 = loop1 <@> f |> compute
let loop2 = for f                      // object_for(f)
let result2 = loop2 <@> (A, B) |> compute
// EXPECT: result1 = [[3, 4, 5], [6, 8, 10]]
// EXPECT: result2 = [[3, 4, 5], [6, 8, 10]]
```

Virtual arrays join via `in`, which co-iterates the operands with the index
space:

```blade
type M = Idx<2>
type N = Idx<3>
let A: Array<Float64 like M, N> = [[1.0, 2.0, 3.0], [4.0, 5.0, 6.0]]
let B: Array<Float64 like M, N> = [[1.0, 0.0, 1.0], [0.0, 1.0, 0.0]]
let loop = for (A, B) in range<M, N>
let result = loop <@> lambda(a, b, i, j) -> a * b + Float64(i) |> compute
// EXPECT: result = [[1, 0, 3], [1, 6, 1]]
```

The kernel receives the operands' cells first -- `a` is already `A(i, j)` --
then the loop indices, which you may leave off when the body does not need
them.

The `in` clause takes virtual arrays only (`range<I>`, not a bare `Idx<N>`),
and both orientations work — arrays left / kernel right, or kernel left /
arrays right (formalism §7.4 lists every form).

## Zero Functions and Zero Array Tuples

Blade's combinator algebra has two "zeros", and both are useful.

**The zero array tuple `()`** is the empty argument *pack* — a loop over no
arrays. (It is not a `Tuple<N>` value: `Tuple<N>` annotations require a
literal `N >= 2`, and `()` is unit, not a tuple — see
[formalism.md](formalism.md) §2.8, "Tuples and argument packs".) It is the
identity for joining loops, and the natural base case of arity-polymorphic
recursion — the `| 0 ->` arm below is the kernel applied to `()`:

```blade
from stats import mean
let A = [[1.0, 2.0, 4.0, 7.0], [2.0, 1.0, 0.0, 1.0], [3.0, 3.0, 5.0, 5.0]]

function moment(a: Poly<T^1>) where comm(a) -> T^0 = {
    match arity(a) with
    | 0 -> 1.0                         // the empty pack: identity
    | _ -> let head :: tail = a
           mean(head) * moment(tail)
}
let m1 = moment <@> (A) |> compute     // arity 1: one mean per row
let m2 = moment <@> (A, A) |> compute  // arity 2: products of means, SymIdx<2, 3>
let p01 = m2(0, 1)
// EXPECT: p01 = 3.5
```

**The zero function `zero`** is a kernel that produces zeros while keeping
the iteration structure — S-dimensions from the arrays, no T-dimensions:

```blade
let A = [[1.0, 2.0], [3.0, 4.0]]
let z1 = method_for(A) <@> zero |> compute      // zeros shaped like A
let z2 = object_for(zero) <@> (A, A) |> compute // 2x2x2x2 zeros, dense
// EXPECT: z1 = [[0, 0], [0, 0]]
```

`zero` carries no commutativity claim, so `object_for(zero) <@> (A, A)` is a
dense rank-4 array, not a symmetric one. In arity-polymorphic recursion the
terminating arm must return the surrounding operation's IDENTITY — write it
as a literal (`1` in a product, `0` in a sum). `zero` there is the zero value:
a product recursion ending in `| 0 -> zero` computes 0 everywhere. (Having
`zero` resolve to the identity of its context is a planned refinement, not
today's behavior; `tests/corpus/arity/024` pins the literal form.)

```blade
from stats import mean
function comoment_prod(a: Poly<T^1>) where comm(a) -> T^1 = {
    match arity(a) with
    | 0 -> 1.0                        // the multiplicative identity
    | _ -> let head :: tail = a
           (head - mean(head)) * comoment_prod(tail)
}
let x = [1.0, 2.0, 3.0]
let y = [2.0, 4.0, 6.0]
let centered = comoment_prod(x, y)
// EXPECT: centered = [2, 0, 2]
```

There is also `guard`, which turns a condition into structure-preserving
control flow, and the choice combinator `<|>`:

```blade
let c = [1.0, 2.0]
let kept = guard(true, c) |> compute              // c if the condition holds
let dropped = guard(false, c) |> compute          // zeros of c's shape otherwise
let fallback = guard(false, c) <|> [5.0, 6.0] |> compute   // choice: left if non-zero, else right
// EXPECT: kept = [1, 2]
// EXPECT: dropped = [0, 0]
// EXPECT: fallback = [5, 6]
```

These satisfy clean algebraic laws (they form a MonadPlus with `zero`), so
conditional pipelines compose predictably. One law that does NOT hold: right
distribution of `<|>` through `>>=` — don't rely on it (this is a checked
fact, not a style note).

## Parallelism

Parallelism in Blade is declared per-function, in the `where` clause:

```blade
function weight(a: Float64, b: Float64) where comm(a, b), omp(a: 1) -> Float64 = a * b
let A = [1.0, 2.0, 3.0]
let W = weight <@> (A, A) |> compute      // the loop over a's level may carry threads
```

`omp(a: 1)` means: **up to** 1 level of the S-dimension loops coming from
argument `a` may carry OpenMP threads. Arrays are bound in order, so their
S-dimension loops nest in order — the first argument's loops are outermost and
are the natural parallelization target.

### The clause licenses EXTERNAL loops

An `omp` clause on a signature is about the S-dims that argument **contributes
to a loop built around the function** — the caller's co-iteration when the
function is used in kernel position:

```blade
function cov(a: Float64, b: Float64) where omp(a: 1) -> Float64 = a * b
let A = [1.0, 2.0]
let B = [3.0, 4.0]
let m = object_for(cov) <@> (A, B) |> compute   // <- the licensed nest
```

It says nothing about a loop the **body itself** generates. Those loops belong
to the kernel of the apply that builds them, and are licensed by a clause on
*that* kernel:

```blade sketch
function lsdft(s: T^1, t: T<time>^1, omegas: T<angular_frequency>^1)
where omp(s: 1) = {                    // licenses a CALLER co-iterating series
    ...
    omegas <@> lambda(w) where omp(w: 1) -> {   // licenses THIS loop
        ...scalar work over the captured arrays...
    } |> compute
}
```

Writing `where omp(omegas: 1)` on `lsdft` instead would license nothing:
`omegas` is iterated by a loop `lsdft` builds internally, and that loop reads
its licence from its own kernel. The compiler warns (BL4001) when a licensed
parameter is an operand of a loop inside the body whose kernel carries no
clause of its own, because the emitted C++ for "asked and got serial" is
byte-identical to "never asked". (`examples/lsdft.blade` is the full program.)

A parallel kernel may not write what it captures: an assignment to a captured
binding inside an `omp` or `cuda` body is refused (BL4005), because the
threads would race on it. The same write in a serial kernel is legal.

A `Tuple<N>` parameter may carry a parallel clause: under one-level structural
matching the tuple is **one schema node**, so `omp(p: n)` licenses the first
`n` of the levels that node contributes (`omp(p: 1)` on a `Tuple<2>` threads
one level, not two). `comm`/`anticomm` on a tuple parameter stay refused — they
address parameters by position, and the expansion into row parameters
renumbers those.

The depth is a **licence, not a demand**. It caps the compiler's choice rather
than replacing it: the emitted strategy is still picked from the loop structure
(collapse for rectangular levels, `schedule(dynamic)` when triangular work sits
below), and the licence bounds how far that choice may reach. Three consequences
worth knowing:

- The count is **per argument**. Above, `a` owns exactly one loop level, so
  `omp(a: 2)` still licenses only one — there is no second level *of `a`* to
  license. To thread both loops of the `(a, b)` nest, license both arguments:
  `omp(a: 1, b: 1)`, which permits `collapse(2)`.
- Licensing fewer levels than the structure could use is honoured. `omp(a: 1)`
  on a collapsible two-level nest emits a plain `parallel for` over `a`'s level,
  never `collapse(2)` — collapsing would thread a dimension of `b`, which
  granted nothing.
- Licensing an argument whose loops are **not** outermost moves the pragma
  inward: `omp(b: 1)` parallelizes `b`'s level and leaves `a`'s outer loop
  serial, opening a team per outer iteration.

A rank-2 argument does own two levels, so `omp(a: 2)` on `a: T^2` licenses both
and permits collapsing them.

Two things fall out of the design:

1. **Parallelism is part of the loop level type.** Two loops fuse only if
   their levels match — extent, symmetry state, AND parallelism. You cannot
   accidentally fuse a parallel loop with a serial one.
2. **Backends are substitutable.** `cuda` requests GPU codegen for the
   kernel; the same S/T structure lowers to CUDA kernels (including
   triangular/simplicial iteration spaces). Other backends (e.g. OpenACC)
   would slot into the same clause position.

Because iteration structure is explicit and symmetric spaces are typed,
parallel triangular iteration needs no manual index gymnastics — the loop
object already knows the canonical space it covers.

## Loop Combinators

Blade offers combinators beyond `<@>` for building performant pipelines.

From functional programming:

* `|>` — pipe: pass the left value as the last argument of the right function.
* `>>` — compose: two sequential functions become one.

For loop fusion and composition:

* `<&>` — fuse loop nests where possible (automatic common-prefix fusion),
  run both computations.
* `<&!>` — force complete fusion; error if impossible. Requires both
  computations to come from the *same* `method_for` loop.
* `<*>` — join loops: `method_for(A) <*> method_for(B) == method_for(A, B)`.
* `>>@` — compose kernels already wrapped by `object_for`:
  `oloop1 >>@ oloop2`.
* `@>>` — compose at `method_for` call sites:
  `(mloop <@> f1) @>> (mloop <@> f2)`.

The two composition operators are two sides of one identity —
compose-then-apply equals apply-then-compose:

```blade
let A = [1.0, 2.0, 3.0]
let f = lambda(x) -> x + 1.0
let g = lambda(x) -> x * 2.0
let lhs = (object_for(f) >>@ object_for(g)) <@> A |> compute
let rhs = (method_for(A) <@> f) @>> (method_for(A) <@> g) |> compute
// EXPECT: lhs = [4, 6, 8]
// EXPECT: rhs = [4, 6, 8]
```

While Blade guarantees performance for a single kernel and array tuple, these
combinators build larger pipelines that Blade can't always reason about
globally; it's up to you to compose them well.

Kernels are values: a composed `object_for` pipeline can be bound, passed
around and applied to different arrays later — constructed, inspected,
transformed — until applied. (Lifting the combinators *themselves* into loop
constructors, e.g. `object_for(>>)` to build pipelines from a list, is a design
direction, not implemented: `object_for(>>)` is refused.)

## Array Combinators

Array-level combinators operate on (lazy) array expressions; `|> compute`
materializes with cache-optimal layout:

* `zip(A, B, ...)` — pair up arrays over their shared leading dimensions. By
  default the kernel still takes one flat parameter per array
  (`lambda(a, b) -> ...`); write a `Tuple<2>` (or written-tuple) parameter to
  take the co-iterated pair as one value instead (`lambda(p: Tuple<2>) ->
  p[0] + p[1]`) — see [formalism.md](formalism.md) §2.8. This is what
  elementwise `+` desugars through, in the flat form.
* `stack(A, B, ...)` — new leftmost dimension selecting among the arrays.
* `join(A, B, ..., d)` — concatenate along dimension `d`.
* `transpose(A, perm)` — hard transpose (real data movement on materialize);
  the price of re-ordering a curried index chain.
* `decompact(A, axis)` — expand a symmetric/antisymmetric compact axis to
  dense storage (sign-correct for antisymmetric axes). Useful at the boundary
  with external tools that expect dense data.
* `A <|:> B` — fallback: read `A` where allocated, else `B` (user-managed
  sparsity).

```blade
let A = [[1.0, 2.0], [3.0, 4.0]]
let B = [[1.0, 0.0], [0.0, 1.0]]
let s = stack(A, B)            // rank 3: s(0) is A, s(1) is B
let joined = join(A, B, 0)     // 4 x 2
let t = transpose(A, [1, 0])
let s1 = s(1)
// EXPECT: s1 = [[1, 0], [0, 1]]
// EXPECT: joined = [[1, 2], [3, 4], [1, 0], [0, 1]]
// EXPECT: t = [[1, 3], [2, 4]]
```

Not built yet (see [plans/plan-subset-split.md](plans/plan-subset-split.md)):
`subset` / `split` (range extraction — the inverse of `join`), array-valued
`shift` / `reverse(A, d)`, and `align` / `stencil` (bundled shifted copies).
Today, neighbor access is `halo<I, [offsets]>` (above) and reversal is the
`reverse<I>` virtual array.

Relational operations (`mask`, `compound`, `group_by`, `sort`, `unique`,
`reduce`, ...) are covered in [features/sql.md](features/sql.md) — in short:

```blade
// SELECT temp FROM temps WHERE temp > 25 ORDER BY temp DESC
let temps = [21.0, 30.5, 26.0, 18.0, 28.0]
let hot = compound(temps, mask(temps, lambda(t) -> t > 25.0))
let ordered = sort(hot, lambda(t) -> -t)
// EXPECT: ordered = [30.5, 28, 26]
```

## Arity Polymorphism Semantics

Part 1 §10 showed the mechanics; here is the semantic model.

**Identity groups.** At a call site, *neighboring identical* arguments form
identity groups: `(A, A, B)` groups as `[(A, A), (B)]`; `(A, B, A)` is three
singleton groups. `comm` licenses symmetry only within a group.

**Output deduction.** For `kernel(a: Poly<T^k>) -> T^m`:

1. Each input's last k dimensions are consumed by the kernel (T-dims); they
   must be compatible across inputs.
2. Each input's remaining dimensions are S-dims.
3. A group of size g with `comm` contributes `SymIdx<g, ·>` over its
   *compound* S-space — the g whole index tuples are interchangeable, jointly.
   Without `comm`, or at g = 1, contributions are dense.
4. Group contributions concatenate, in order (no broadcasting).
5. The kernel's `T^m` supplies trailing output dims.

**Scope variables.** Inside a poly kernel: `arity(a)` (pack size), `a[k]`
(structural access — `[]`, not `()`), and `let head :: tail = a`
(left-associative destructuring):

```blade
function first(args: Poly<T^0>) -> T^0 = args[0]
let x = first(4.0, 5.0)
// EXPECT: x = 4
```

**Speedups.** Each comm-ed identity group of size g contributes g!; distinct
groups multiply. One group over a multi-dimensional array gets its factorial
over the compound space — not per dimension (see Part 1 §6 for why).

## Reynolds Operators

Commutativity + identity is one road to symmetric output. The Reynolds
operator is the other: it *makes* symmetry rather than detecting it.

```blade prelude
let A = [1.0, 2.0, 4.0]
```

```blade
let g = lambda(x, y) -> x / y            // NOT commutative

let symmetrized = method_for(A, A) <@> reynolds(g) |> compute                // g(x,y) + g(y,x)
let antisym = method_for(A, A) <@> reynolds(g, Antisymmetric) |> compute     // g(x,y) - g(y,x)
let s01 = symmetrized(0, 1)
let a01 = antisym(0, 1)
// EXPECT: s01 = 2.5
// EXPECT: a01 = -1.5
```

`reynolds(g)` sums `g` over permutations of its *value arguments* (with
signs, in the antisymmetric case), making the wrapped kernel commutative by
construction — every output VALUE is symmetric. Symmetric STORAGE is a
separate license, and it follows the same law as `comm`: with the **same
array** in the wrapped positions AND `where comm(x, y)` on the wrapped kernel,
you get symmetric storage and triangular iteration (equal permutation terms
are deduplicated, so a commutative `g` costs one term). Under `reynolds` the
clause is an iteration license over the permutation sum, not a claim about
the bare `g`, so it is accepted even on a non-commutative `g` like `x / y`
(outside `reynolds` that clause is refused, BL4013):

```blade
let g = lambda(x, y) where comm(x, y) -> x / y
let packed = method_for(A, A) <@> reynolds(g) |> compute   // SymIdx<2, 3> storage
let p01 = packed(0, 1)
// EXPECT: p01 = 2.5
```

Without the clause the same-array result is symmetric in value but stored
dense. With **distinct arrays** you get a commutative kernel but a dense,
non-index-symmetric output — Reynolds does not substitute for array
identity. (Machine-checked: the H ∩ Stab license is exact.)

Antisymmetric Reynolds zeroes diagonals automatically and negates on
transposes — determinant-like structures fall out. Antisymmetrizing
`b · c²` over three copies of one array is the Vandermonde determinant of
each triple:

```blade
let x = [1.0, 2.0, 3.0, 5.0]
let vand = lambda(a, b, c) -> b * c * c
let det = method_for(x, x, x) <@> reynolds(vand, Antisymmetric) |> compute
let d012 = det(0, 1, 2)      // (2-1)(3-1)(3-2)
let d102 = det(1, 0, 2)      // one transposition: negated
let d001 = det(0, 0, 1)      // repeated index: zero
// EXPECT: d012 = 2
// EXPECT: d102 = -2
// EXPECT: d001 = 0
```

Reynolds wraps scalar-argument lambdas; partial symmetrization over a subset
of positions is not provided.

## Equivariance

Where index types handle *discrete* symmetry (permutations — storage and
iteration), equivariance handles *continuous* symmetry (rotations and
friends — pure type checking, zero runtime cost). Representation data lives
in an index type, `IrrepsIdx<spec>` (a list of `(l, parity, multiplicity)`
blocks), and the claim "this function is equivariant" is a `where ml.equiv(G)`
pin on the signature, checked by the compiler:

```blade prelude
import ml as ml
let static S = [(0, 0, 2), (1, 1, 1)]      // two scalars and one vector: 5 cells
```

```blade
function f(x: Array<Float like IrrepsIdx<S>>) where ml.equiv(O3) -> Array<Float like IrrepsIdx<S>> =
    x + x
let out = f([1.0, 2.0, 3.0, 4.0, 5.0])
// EXPECT: out = [2, 4, 6, 8, 10]
```

Mistakes are compile errors with domain-language messages — the elementwise
product of two representation-typed values is basis-dependent, and so is
reading one component of a vector:

```blade rejects
function bad(x: Array<Float like IrrepsIdx<S>>) where ml.equiv(O3) -> Float =
    x(2)
// ERROR: BL4008
```

The two systems compose: a stress tensor is stored as
`Array<Float like SymIdx<2, IrrepsIdx<spec>>>` — triangular storage from the
index type — and the functions that produce or consume it carry the
`ml.equiv` pin. The full equivariant ML stack (irreps, tensor products,
spherical harmonics, message passing) is the subject of
[features/equivariant-nn.md](features/equivariant-nn.md). (An earlier design
annotated VALUES, `with equiv(SO<3>, vector)`; it was superseded on
2026-07-28 and is not the shipped surface.)

## Metaprogramming with Static Functions

`static` marks compile-time computation. Static values and functions can
feed *type positions* — types are computed, not just written:

```blade
let static n = 100
static function triangle(k) = k * (k + 1) / 2
let static packed_n = triangle(n)

type PackedSym = Array<Float like Idx<packed_n>>   // Idx<5050>
let cells = extents(range<Idx<packed_n>>)
// EXPECT: cells = 5050
```

(The call must be bound to a `let static` first: an index type's argument is a
static expression over names and literals, not a general call, so
`Idx<triangle(n)>` does not parse.)

Rules of the game:

- `static function` may capture only `static` values; it runs at
  compile time when its arguments are static.
- No totality proofs, no proof assistant — just the restriction that static
  computation depends only on static inputs (that keeps type checking
  decidable).
- Type providers push this further: `import netcdf as nc` then
  `nc.load("era5.nc")` reads file *metadata* at compile time and mints index
  types and typed array declarations for the file's actual shape
  ([formalism.md](formalism.md) §3.8). The structure is static; the data is
  runtime.

```blade
let static spec = [(0, 0, 16), (1, 1, 8), (2, 0, 4)]  // (l, parity, mult) — static data...
type Features = IrrepsIdx<spec>                       // ...drives a block-structured index type
// extent = 16·1 + 8·3 + 4·5 = 60
```

`IrrepsIdx` is a flat-dense primitive index type whose identity is the spec
itself — two 60-cell arrays with different specs are distinct types. See the
equivariant-NN module doc §6.

This is the same machinery that makes arity polymorphism typable: the type
level can *count and compute*, so output types can depend on how many arrays
you passed — which is where we came in.

---

Next steps: worked end-to-end programs in [examples.md](examples.md); the
full semantics in [formalism.md](formalism.md); what's proved, in
[proofs.md](proofs.md).
