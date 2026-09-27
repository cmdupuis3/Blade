# Blade: Quickstart Guide (Part 1)

Blade is an array-oriented functional programming language, designed for
scientific applications. Blade is useful for general-purpose array math, and
particularly for math involving symmetric calculations.

Transparent access to optimal coding patterns is a core concern. The type
system guarantees that individual array computations are cache-optimal, and Blade uses
type deduction to encode data symmetry and function commutativity, which
together yield extreme accelerations for certain kinds of problems.

Blade can look and behave very similarly to Python with numpy and
xarray or other array-oriented languages like R. However, it is built 
on a radically different foundation.

The three most important concepts in Blade (beyond basic programming) are:

1. **Loop reification**: iteration patterns (for-loops) are first-class
   objects. You can store a partially evaluated loop in a variable, join it
   with other loops, and apply it to multiple computations.
2. **Dimensional currying**: arrays are functions that take indices and
   evaluate to their elements — not just containers.
3. **Arity polymorphism**: a function can take an arbitrary number of input
   arrays and still deduce the correct output shape and type.

Together these form "structure-first" programming, as opposed to the
"collection-first" orientation of most languages.

Every `blade` code block on this page is checked by `blade test docs`; blocks
with `// EXPECT:` lines are also compiled and run, and the pinned values are
the ones the program prints. A block marked `cont` continues the program built
by the blocks before it in the same section.

## 1. Basic Concepts

Surface math syntax feels like numpy/R/MATLAB; the rest of the grammar is
ML-style (Rust, OCaml, F#). There is no `main`: a file is a sequence of
declarations, and every top-level binding — and every bare top-level
expression — prints when the program runs.

```blade
let a = 2
let b = 3
let mySum = a + b
mySum
// EXPECT: mySum = 5
```
```
a = 2
b = 3
mySum = 5
__expr1 = 5
```

Conditionals: `if`/`then`/`else` and `match` are expressions; results can be
let-bound.

```blade cont
let test1 = if a == 2 then True else False

function test2(n: Nat) -> Bool = {
    match n with
    | 1 -> True
    | 2 -> False
    | 3 -> False
    | _ -> True
}
let t2 = test2(2)
// EXPECT: test1 = true
// EXPECT: t2 = false
```

A top-level name is bound once per module: a second `let a = ...` at top level
is refused (BL2009). Plain assignment `a = 7` updates an existing `let`.

## 2. Array and Index Types

Arrays are the main collection type. "Index types" define the
multidimensional space an array spans. The simplest, `Idx<N>`, works somewhat
like Python's `range`. Index types are types; define them with `type`:

```blade
let static N = 100
type Stations = Idx<N>

type LatIdx = Idx<180>
type LonIdx = Idx<360>

type EarthArray = Array<Float like LatIdx, LonIdx>
```

Index types don't evaluate to values, so you cannot see the numbers inside,
but they guarantee that the `Array` types they define iterate in the fastest
order. Data usually arrives from a file whose metadata fixes the shape at
compile time (the NetCDF provider, [formalism.md](formalism.md) §3.8):

```blade sketch
import netcdf as nc
let era5 = nc.load("era5.nc")          // reads the file's METADATA at compile time
let t2m = era5.vars.t2m |> nc.read     // typed Array<Float32 like era5.index.time, ...>
```

We may not know what values `t2m` holds, but we know exactly what shape it
has.

## 3. Tuples and Currying

Tuples are the other collection type. A bare comma constructs one, parentheses
aren't strictly needed:

```blade
let a = 1
let b = 2
let c = 3
let myTuple = a, b, c          // construction
let e, d, f = myTuple          // destructuring — the same spelling, mirrored
let head :: tail = myTuple     // partial destructuring
let same = tail == (b, c)
// EXPECT: same = true
```

Parentheses on the pattern side are optional: `let (e, d, f) = myTuple` binds
exactly the same thing. Both halves of one statement may use the bare form at
once: `let a, b = c, d`.

A tuple doesn't need to be destructured; tuples can be indexed with `[ ]` to extract
their components.

```blade cont
let t = b, c        // width-2 tuple, same construction
let s = t[0] + t[1]
// EXPECT: s = 5
```

However, arrays are indexed with parentheses.

Destructuring a collection one element at a time is called "currying". It applies to
function arguments and to array indexing:

```blade
let A = [[[1, 2], [3, 4]], [[5, 6], [7, 8]]]
let view1 = A(1)(0)(1)   // curried
let view2 = A(1, 0, 1)   // uncurried (same thing)
let slab = A(1)          // partial indexing: a rank-2 view
// EXPECT: view1 = 6
// EXPECT: view2 = 6
// EXPECT: slab = [[5, 6], [7, 8]]
```

A curried function takes one argument at a time; as long as arguments arrive
in order, it doesn't matter when they arrive:

```blade
function myFunc(a: Int64, b: Int64, c: Float64, d: Float64) -> Float64 =
    Float64(a + b) * c * d
let f1 = myFunc(5)        // Int64 -> Float64 -> Float64 -> Float64
let f2 = myFunc(5)(3)     // Float64 -> Float64 -> Float64
let r = f2(2.0, 0.5)
// EXPECT: r = 8
```

With array indexing, currying means you index *in order*. To index a
different dimension first, transpose the array (`transpose(A, [1, 0])` on a
matrix). Transposition can be expensive, but it is the price of dimensional
currying — which is what guarantees cache-optimal iteration.

## 4. Function Basics

Blade has two parallel type systems: *concrete types* and *abstract types*.
`EarthArray` is concrete — a 180 × 360 `Array` of `Float`. Functions are on a
need-to-know basis; they only need abstract types. A function written over
`T^2` accepts any rank-2 array:

```blade
function add1(array: T^2) -> T^2 = {
   array + 1
}
let M = add1([[1, 2], [3, 4]])
// EXPECT: M = [[2, 3], [4, 5]]
```

An abstract type is a value type and a rank, plus a *symmetry vector* of length
`rank` that the compiler tracks for you (the formalism writes it
`T^r(σ)`: a dense matrix is `Float^2(1, 2)`, a symmetric one `Float^2(1, 1)`).
You write only `T^r`; ignore symmetry for now.

Scoping and mutability work intuitively. A caller's array can only be
modified if the parameter is marked `mut`; `mut` parameters are array-only and
mutate through element writes (a scalar result is simply returned):

```blade
let mut xs = [1.0, 2.0, 3.0]
function bump(v: mut Array<Float64 like Idx<3>>) -> Float64 = {
    v(0) = v(0) + 10.0
    v(0)
}
let first = bump(xs)
let after = xs
// EXPECT: first = 11
// EXPECT: after = [11, 2, 3]
```

The opposite end is `let static` (a compile-time value, immutable
everywhere); plain `let` is the middle: reassignable in its own scope,
protected from callees. Reassigning a `static` binding, writing through a
non-`mut` parameter, or marking a scalar parameter `mut`, is a compile error
(BL4005):

```blade rejects
function tryToChange(a: mut Float64) -> Float64 = a + 10.0
// ERROR: BL4005
```

Anonymous functions use `lambda`, and are values like any other:

```blade
let addTen = lambda(x: Float64) -> x + 10.0
let twelve = addTen(2.0)
// EXPECT: twelve = 12
```

## 5. Structure-First Math

Say we want an average global temperature over a
`temps : Array<Float like Idx<100>, Idx<120>>`. The primitive `+` has type
`T^0 -> T^0 -> T^0`; Blade looks at *all* the dimensions in a call before
iterating. Adding a scalar to a 2D array wraps `+` in two loops, and so does
calling a scalar function on two arrays of one shape — the arrays are
co-iterated, element by element:

```blade prelude
let A = [[1.0, 2.0], [3.0, 4.0]]
let B = [[10.0, 20.0], [30.0, 40.0]]
function add(a: T^0, b: T^0) -> T^0 = a + b
```

```blade
let elementwise = add(A, B)       // A(i,j) + B(i,j): same as A + B
// EXPECT: elementwise = [[11, 22], [33, 44]]
```

To visit *every pair* of elements instead — every element of `A` against every
element of `B`, four dimensions to consume — say so with a loop object, or
with the bracketed operator `[+]`:

```blade
let pairs1 = method_for(A, B) <@> add |> compute    // T^2 -> T^2 -> T^4
let pairs2 = A [+] B
let pick = pairs2(0, 1, 1, 0)                       // A(0,1) + B(1,0)
// EXPECT: pick = 32
```

These aren't two spellings of one operation. They differ in *which pairs of
elements get visited*:

```
A  +  B     A(i,j) + B(i,j)      one pair per index    -> 2D
A [+] B     A(i,j) + B(k,l)      every pair            -> 4D
```

`zip` is the explicit form of the first one: it fuses `A` and `B` into a
single shared index space, so only two dimensions are left for Blade to
consume. A kernel may take the co-iterated pair as one `Tuple<2>`:

```blade
function addPair(p: Tuple<2>) = p[0] + p[1]
let zipped = method_for(zip(A, B)) <@> addPair |> compute
// EXPECT: zipped = [[11, 22], [33, 44]]
```

So why would we ever want the bracketed operators?

## 6. Comoments: The Motivating Problem

Consider covariance:

$$cov(x, y) = \frac{1}{n}\Sigma_n (x_n - \mu_x)(y_n - \mu_y)$$

For a 2D array $A(x, t)$ — a 1D collection of time series — the covariance
matrix needs $\forall m,n: cov(A_m, A_n)$: very nearly an outer product,
i.e. `[*]` territory.

Covariance is *commutative*: $cov(x, y) = cov(y, x)$. So of all pairs
$(m, n)$, only about half are unique. With triangular iteration, we can visit only those. In Python:

```python
for x1 in range(0, xmax):
    for x2 in range(x1, xmax):
        cov(A[x1], A[x2])
```

Now take a 2D+time array $B(x, y, t)$. Four spatial loops:

```python
for x1 in range(0, xmax):
    for y1 in range(0, ymax):
        for x2 in range(0, xmax):
            for y2 in range(0, ymax):
                cov(B[x1, y1], B[x2, y2])
```

Commutativity gives us exactly this symmetry:

$$cov(B(x_1, y_1), B(x_2, y_2)) = cov(B(x_2, y_2), B(x_1, y_1))$$

Commutativity makes $cov(B_p, B_q)$ symmetric in $(p, q)$, so the unique fraction is about $1/2$ over the *compound* space:

```python
for p in range(0, xmax*ymax):        # compound spatial index
    for q in range(p, xmax*ymax):
        cov(Bflat[p], Bflat[q])
```

For coskewness (three copies, still one array), the joint symmetry is the
full permutation group on the three compound indices — $1/6$ of the compound space, a $3! = 6\times$ speedup. In general, $r$ copies of one array give $r!$ over the compound index.


```python
for x1 in range(0, xmax):
    for y1 in range(0, ymax):
        for x2 in range(0, xmax):
            for y2 in range(0, ymax):
                for x3 in range(0, xmax):
                    for y3 in range(0, ymax):
                        skw(B[x1, y1], B[x2, y2], B[x3, y3])
```

Where do *bigger* products come from? From **distinct commutative groups**:
e.g. a kernel commutative in $(a, b)$ and separately in $(c, d)$, applied to $(A, A, C, C)$, gets $2! \times 2! = 4$, so one factor per group. 

These can be major speedups — if only we could exploit them easily...

## 7. Symmetry Optimization

In Blade, the fastest way is the *only* way to iterate. That includes cache
behavior and the symmetry above. But how can triangular iteration live in a
type like this?

```blade
type Square = Array<Float like Idx<100>, Idx<100>>
```

It can't! The bounds of a triangular loop depend on each other. We need an
index type that describes multiple symmetric positions simultaneously,
`SymIdx<r, N>`: `SymIdx<2, 100>` stores symmetric pairs over 100 elements —
about half the memory of a dense 100×100 array — and encodes the triangular
iteration pattern:

```blade
type SymArray = Array<Float like SymIdx<2, 100>>
```

Now the covariance of a 2D+time array, concretely (a 3 × 2 grid, four time
steps; `mean` comes from the standard library's `stats` module):

```blade prelude
from stats import mean

function cov(a: T^1, b: T^1) where comm(a, b) -> T^0 =
    mean((a - mean(a)) * (b - mean(b)))

type X = Idx<3>
type Y = Idx<2>
type Time = Idx<4>
let B: Array<Float64 like X, Y, Time> = [[[1.0, 2.0, 4.0, 7.0], [0.0, 1.0, 0.0, 1.0]],
                                         [[2.0, 1.0, 0.0, 1.0], [5.0, 5.0, 3.0, 3.0]],
                                         [[3.0, 3.0, 5.0, 5.0], [1.0, 2.0, 3.0, 4.0]]]
```

```blade
let calc = cov <@> (B, B) |> compute
let c00 = calc(0, 0)     // flat compound positions p = x·2 + y: the variance of the series at (0, 0)
// EXPECT: c00 = 5.25
```

`cov` consumes the time dimension from each copy; each copy contributes its
two spatial dimensions; commutativity symmetrizes the two *compound* spatial
positions jointly — six grid points, so 21 stored pairs instead of 36:

```
calc : Array<Float64 like SymIdx<2, 6>>      // pairs over the compound (X, Y) space
```

## 8. Loop Objects

That six-deep coskewness loop nest from section 6 is a mess. Blade condenses
it with two constructors:

- `object_for` wraps a *function*, and awaits arrays.
- `method_for` wraps *arrays*, and awaits a function.

Both return "loop objects" — partially specified iteration patterns, stored
as first-class values. The apply combinator `<@>` completes them, and
`|> compute` materializes the result:

```blade
let P = [1.0, 2.0]
let Q = [10.0, 20.0, 30.0]
let R = [100.0, 200.0]
function func(a: Float64, b: Float64, c: Float64) -> Float64 = a + b * c

let result1 = object_for(func) <@> (P, Q, R) |> compute
let result2 = method_for(P, Q, R) <@> func |> compute
let same = reduce(result1 == result2, (&&), true, axes = 3)
// EXPECT: same = true
```

`result1 == result2` on its own is elementwise — an `Array<Bool>` of the same
2 × 3 × 2 shape; folding it with `(&&)` over all three axes gives the single
`Bool`.

The point is reuse:

```blade sketch
let covLoop = object_for(cov)

let covA = covLoop <@> (A, A)   // A : Array<T like Idx<M>, Idx<T>>
let covB = covLoop <@> (B, B)   // B : Array<T like Idx<M>, Idx<N>, Idx<T>>
let covC = covLoop <@> (C, C)   // C : rank 4, three spatial dims + time

let LoopA = method_for(A)
let avg = LoopA <@> mean
let var = LoopA <@> variance
let sd  = LoopA <@> stddev
```

If `A`, `B`, `C` have different ranks, each application needs a different
number of loops with different symmetry — the loop object handles all of it,
and the output types stay exact:

```
covA : Array<T like SymIdx<2, M>>
covB : Array<T like SymIdx<2, M·N>>        // over the compound (M, N) space
covC : Array<T like SymIdx<2, M·N·P>>
```

One symmetric pair index per application — over whatever compound spatial
space each array has. Most array languages cannot track this at the type
level at all; users would have to allocate symmetric storage by hand and know the symmetry theory themselves.

In practice though, we can imagine that *every* array is wrapped in a trivial `method_for` loop, and *every* function, including combinators, is wrapped in a trivial `object_for` loop. This simplifies the syntax to the point that explicit `method_for` and `object_for` are rarely needed... but they are there. The clearest hint is the `<@>` combinator:

```
cov <@> (A, A)
    == object_for(cov) <@> (A, A)

method_for(A, A) <@> cov
    == (method_for(A) <*> method_for(A)) <@> cov
```

## 9. Kernel Functions

How does the compiler know commutativity is available? `where` clauses, which
sit between the parameter list and the return type:

```blade prelude
from stats import mean

function cov(a: T^1, b: T^1) where comm(a, b) -> T^0 =
    mean((a - mean(a)) * (b - mean(b)))
```

`comm(a, b)` declares the arguments interchangeable (and the compiler checks
the claim: a body it can refute with a concrete counterexample is refused,
BL4013). Two things must combine for triangular iteration (both are
machine-checked necessities, not heuristics):

1. **The kernel must be commutative** in those positions (`comm`), and
2. **The same array must occupy them** at the call site.

```blade
let B = [[1.0, 2.0, 4.0, 7.0], [2.0, 1.0, 0.0, 1.0], [3.0, 3.0, 5.0, 5.0]]    // 3 series x 4 steps
let D = [[1.0, 0.0, 1.0, 0.0], [2.0, 2.0, 1.0, 1.0], [0.0, 1.0, 2.0, 3.0]]
let sameArray = cov <@> (B, B) |> compute    // triangular: SymIdx<2, 3> storage
let different = cov <@> (B, D) |> compute    // rectangular: dense 3 x 3, even with comm
let s01 = sameArray(0, 1)
let d01 = different(0, 1)
// EXPECT: s01 = -0.75
// EXPECT: d01 = -1
```

Commutativity without identity buys nothing, and vice versa. The compiler detects identity from the call site and commutativity from the kernel, which is why Blade has exactly the two loop combinators `method_for` and `object_for`. Each of them detect one property and defer the other.

Note the `<@>`: `cov` takes rank-1 rows, so a *direct* call `cov(B, B)` on the
rank-2 `B` is refused (BL3001 — a call site neither broadcasts nor reduces
rank). Scalar functions are the exception: `add(A, B)` in §5 co-iterates.

## 10. Arity Polymorphism

Possibly the most powerful feature in Blade: functions over any number of
arrays, with correct iteration and output types for each arity.

```blade
from stats import mean

function comoment_prod(A: Poly<T^1>) where comm(A) -> T^1 = {
    match arity(A) with
    | 0 -> 1.0 // recursion terminator: identity
    | _ ->
        let head :: tail = A
        (head - mean(head)) * comoment_prod(tail)
}

function comoment_generator(A: Poly<T^1>) where comm(A) -> T^0 = {
    mean(comoment_prod(A))
}

let A = [[1.0, 2.0, 4.0, 7.0], [2.0, 1.0, 0.0, 1.0], [3.0, 3.0, 5.0, 5.0]]    // 3 series x 4 steps

let comomentLoop = object_for(comoment_generator)
let covariance = comomentLoop <@> (A, A) |> compute
let coskewness = comomentLoop <@> (A, A, A) |> compute
let cokurtosis = comomentLoop <@> (A, A, A, A) |> compute
let var0 = covariance(0, 0)
// EXPECT: var0 = 5.25
```

One generator, every comoment:

```
covariance : Array<Float64 like SymIdx<2, 3>>    // 2!  = 2x  speedup
coskewness : Array<Float64 like SymIdx<3, 3>>    // 3!  = 6x
cokurtosis : Array<Float64 like SymIdx<4, 3>>    // 4!  = 24x
```

Arity determines loop depth, output rank, and symmetry — deduced, typed, and optimized automatically.

## 11. Units of Measure

Primitive types can carry units:

```blade prelude
Unit meters
Unit seconds
Unit mps = meters / seconds
type Distance = Float<meters>
type Time = Float<seconds>
type Speed = Float<mps>

let speed1 = 4.0: Speed
let speed2 = 3.0: Speed
let dist = 4.0: Distance
let time = 2.0: Time
```

```blade
let speed3 = dist / time                      // meters/seconds = mps: OK
let avg = (speed1 + speed2 + speed3) / 3.0
// EXPECT: avg = 3
```

```blade rejects
let wrong = speed1 + dist                     // mps + meters
// ERROR: BL3006
```

Types can carry valid ranges (`Float<meters, min=0.0>`), encoding physical
constraints — salinity that can never go negative, and so on. A binding
annotated with a bounded type is checked when it is bound, and a function's
bounded parameters and return value at every call; a violation stops the
program with BL8001:

```blade
Unit psu
type Salinity = Float<psu, min=0.0>
let s: Salinity = 35.0
function freshen(x: Salinity, dilution: Float<psu>) -> Salinity = x - dilution
let fresher = freshen(s, 5.0)        // freshen(s, 40.0) would stop with BL8001
// EXPECT: fresher = 30
```

---

Continue with [quickstart-2.md](quickstart-2.md) for more complex concepts: virtual arrays, for-loop sugar, zero elements, parallelism, combinators, Reynolds operators, equivariance, and metaprogramming.
