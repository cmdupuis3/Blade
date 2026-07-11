# Blade: Quickstart Guide (Part 1)

> Supersedes `rewrite/QUICKSTART.md`. Sections 6–9 are rewritten to match the
> corrected symmetry semantics (see [formalism.md](formalism.md) §12 and
> [proofs.md](proofs.md)); the rest is carried over with light cleanup.

Blade is an array-oriented functional programming language, designed for
scientific applications. Blade is useful for general-purpose array math, and
particularly for math involving symmetric calculations.

Transparent access to optimal coding patterns is a core concern. The type
system guarantees that array computations are cache-optimal, and Blade uses
type deduction to encode data symmetry and function commutativity, which
together yield extreme accelerations for certain kinds of problems.

Blade can look and behave very similarly to Python and R with numpy and
xarray. However, it is built on a radically different foundation.

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

## 1. Basic Concepts

Surface math syntax feels like numpy/R/MATLAB; the rest of the grammar is
ML-style (Rust, OCaml, F#).

```F#
let a = 2
let b = 3
let mySum = a + b
mySum
```
```
5
```

Conditionals: `if`/`then`/`else` and `match` are expressions; results can be
let-bound.

```F#
let test1 = if a == 2 then True else False

let test2(a: Nat) = {
    match a with
    | 1 -> True
    | 2 -> False
    | 3 -> False
    | _ -> True
}
test2(2)
```
```
False
```

## 2. Array and Index Types

Arrays are the main collection type. "Index types" define the
multidimensional space an array spans. The simplest works like Python's
`range`:

```F#
Idx<N>
```

Index types are types; define them with `type`:

```F#
type LatIdx = Idx<180>
type LonIdx = Idx<360>

type EarthArray = Array<Float like LatIdx, LonIdx>
```

Index types don't evaluate to values — you cannot see the numbers inside —
but they guarantee that the `Array` types they define iterate in the fastest
order.

```F#
let array: EarthArray = readArray("file_with_array_inside.nc")
```

We may not know what values `array` holds, but we know exactly what shape it
has.

## 3. Tuples and Currying

Tuples are the other collection type:

```F#
let myTuple = (a, b, c)
let e, d, f = myTuple          // destructuring
let head :: tail = (a, b, c)   // partial destructuring
tail == (b, c)
```
```
True
```

Destructuring a collection one element at a time is "currying". It applies to
function arguments and to array indexing:

```F#
let view1 = A(i)(j)(k) // curried
let view2 = A(i, j, k) // uncurried (same thing)
```

A curried function takes one argument at a time; as long as arguments arrive
in order, it doesn't matter when they arrive:

```F#
function myFunc(a: T^0, b: T^0, c: U^0, d: U^0) -> T^0 = ...
myFunc            // T^0 -> T^0 -> U^0 -> U^0 -> T^0
myFunc(5)         // T^0 -> U^0 -> U^0 -> T^0
myFunc(5)(3)      // U^0 -> U^0 -> T^0
```

With array indexing, currying means you index *in order*. To index a
different dimension first, transpose the array. Transposition can be
expensive, but it is the price of dimensional currying — which is what
guarantees cache-optimal iteration.

## 4. Function Basics

Blade has two parallel type systems: *concrete types* and *abstract types*.
`EarthArray` is concrete — a 180 × 360 `Array` of `Float`. Functions are on a
need-to-know basis; they see only abstract types:

```F#
type EarthArray = Array<Float like LatIdx, LonIdx>   // concrete
Float^2(1,2)                                          // what a function sees
```

Abstract types have the form `T^rank(symm)`: a value type, a rank, and a
symmetry vector of length `rank` (ignore symmetry for now).

```F#
function add1(array: T^2) -> T^2 = {
   array + 1
}
```

Scoping and mutability work intuitively; a caller's variable can only be
modified if the parameter is marked `mut`:

```F#
let mut a = 2
function tryToChange(a: mut T^0) = {
    a = a + 10
}
tryToChange(a)
a
```
```
12
```

The opposite end is `let const` (immutable everywhere); plain `let` is the
middle: mutable in its own scope, protected from callees. Reassigning a
`const`, or mutating a non-`mut` parameter, is a compile error.

Anonymous functions use `lambda`:

```F#
function myFunc = lambda(x: Float) -> x + 10
```

## 5. Structure-First Math

Say we want an average global temperature:

```F#
let temps = getTemperatures("temperatures.nc")
// temps : Array<Float like Idx<100>, Idx<120>>
```

The primitive `+` has type `T^0 -> T^0 -> T^0`. Blade functions look at *all*
the dimensions in a call before iterating. Adding a scalar to a 2D array
wraps `+` in two loops. But:

```F#
let mySum = add(A, B)   // both 2D
```

Here Blade sees FOUR dimensions that need consuming, and iterates over all of
them — every element of `A` against every element of `B`:

```
T^2 -> T^2 -> T^4
```

Not what numpy does! That cross-iteration operator is spelled `[+]` in Blade;
plain `+` stays elementwise, like other languages:

```F#
let add((a: T^0, b: T^0)) = a + b
method_for(zip(A, B)) <@> add   // A + B   (zip first: elementwise)

let add(a: T^0, b: T^0) = a + b
method_for(A, B) <@> add        // A [+] B (outer-product style)
```

`zip` reduces what Blade sees to one shared index space before iteration. So
why would we ever want the bracketed operators?

## 6. Comoments: The Motivating Problem

Consider covariance:

$$cov(x, y) = \frac{1}{n}\Sigma_n (x_n - \mu_x)(y_n - \mu_y)$$

For a 2D array $A(x, t)$ — a 1D collection of time series — the covariance
matrix needs $\forall m,n: cov(A_m, A_n)$: very nearly an outer product,
i.e. `[*]` territory.

Covariance is *commutative*: $cov(x, y) = cov(y, x)$. So of all pairs
$(m, n)$, only about half are unique — triangular iteration visits just those:

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

Note carefully what moved: the *whole coordinate pairs* $(x_1, y_1)$ and
$(x_2, y_2)$ swapped together. That is the **joint (diagonal) symmetry**, and
it is the only symmetry commutativity buys here. It is tempting to go
further and also swap components independently —
$cov(B(x_1, y_2), B(x_2, y_1))$ and so on — hoping for a $1/4$ unique
fraction from two independent triangles. **That is not a real symmetry**,
and the two-triangles loop nest doesn't even visit all the unique work: this
is machine-checked, both as a direct counterexample and as a counting theorem
(per-dimension triangular storage has strictly fewer cells than the joint
computation has unique values — see [proofs.md](proofs.md), BladeCore and
BladeCounting).

The right mental model: the pair $(x, y)$ acts as ONE compound spatial index
$p$. Commutativity makes $cov(B_p, B_q)$ symmetric in $(p, q)$, so the unique
fraction is about $1/2$ over compound points — triangular iteration over the
*compound* space:

```python
for p in range(0, xmax*ymax):        # compound spatial index
    for q in range(p, xmax*ymax):
        cov(Bflat[p], Bflat[q])
```

For coskewness (three copies, still one array), the joint symmetry is the
full permutation group on the three compound indices — $1/6$ of the compound
space, a $3! = 6\times$ speedup. In general, $r$ copies of one array give
$r!$ — over the compound index, not per dimension.

Where do *bigger* products come from? From **distinct commutative groups**:
e.g. a kernel commutative in $(a, b)$ and separately in $(c, d)$, applied to
$(A, A, C, C)$, gets $2! \times 2! = 4\times$ — one factor per group. And at
$r = 2$ there is a beautiful refinement (the Cauchy split) that recovers
per-dimension *structure* without changing the count — see
[formalism.md](formalism.md) §12.4.

These are still major speedups — if only we could exploit them easily...

## 7. Symmetry Optimization

In Blade, the fastest way is the *only* way to iterate. That includes cache
behavior and the symmetry above. But how can triangular iteration live in a
type like this?

```F#
let array = Array<Float like Idx<100>, Idx<100>>
```

It can't — the bounds of a triangular loop depend on each other. We need an
index type that describes multiple symmetric positions simultaneously:

```F#
SymIdx<r, N>
```

`SymIdx<2, 100>` stores symmetric pairs over 100 elements — about half the
memory of a dense 100×100 array — and encodes the triangular iteration
pattern:

```F#
type SymArray = Array<Float like SymIdx<2, 100>>
```

Now the covariance of a 2D+time array, concretely:

```F#
type Btype = Array<Float like Idx<100>, Idx<200>, Idx<T>>
let B: Btype = ...
let calc = cov(B, B)
```

`cov` consumes the time dimension from each copy; each copy contributes its
two spatial dimensions; commutativity symmetrizes the two *compound* spatial
positions jointly:

```
calc : Array<Float like SymIdx<2, <Idx<100>, Idx<200>>>>
```

— symmetric pairs over 100×200 = 20,000 compound points: ~200 million unique
elements instead of 400 million, iterated and stored triangularly. (Not
`SymIdx<2,100>, SymIdx<2,200>` — that type has only ~101 million cells, too
few to hold the answer; that's the counting theorem at work.)

## 8. Loop Objects

That six-deep coskewness loop nest from section 6 is a mess. Blade condenses
it with two constructors:

- `object_for` wraps a *function*, and awaits arrays.
- `method_for` wraps *arrays*, and awaits a function.

```F#
let objectLoop = object_for(func)
let methodLoop = method_for(A, B, C)
```

Both return "loop objects" — partially specified iteration patterns, stored
as first-class values. The apply combinator `<@>` completes them:

```F#
let result1 = object_for(func) <@> (A, B, C) |> compute
let result2 = method_for(A, B, C) <@> func |> compute
result1 == result2
```
```
True
```

The point is reuse:

```F#
let covLoop = object_for(cov)

let covA = covLoop <@> (A, A)   // A : Array<T like Idx<M>, Idx<T>>
let covB = covLoop <@> (B, B)   // B : Array<T like Idx<M>, Idx<N>, Idx<T>>
let covC = covLoop <@> (C, C)   // C : rank 4, three spatial dims + time

let LoopA = method_for(A)
let avg = LoopA <@> mean
let var = LoopA <@> variance
let q95 = LoopA <@> quantile(95)
```

If `A`, `B`, `C` have different ranks, each application needs a different
number of loops with different symmetry — the loop object handles all of it,
and the output types stay exact:

```
covA : Array<T like SymIdx<2, M>>
covB : Array<T like SymIdx<2, <Idx<M>, Idx<N>>>>
covC : Array<T like SymIdx<2, <Idx<M>, Idx<N>, Idx<P>>>>
```

One symmetric pair index per application — over whatever compound spatial
space each array has. Most array languages cannot track this at the type
level at all; users would have to allocate symmetric storage by hand and know
the symmetry theory themselves.

## 9. Kernel Functions

How does the compiler know commutativity is available? `where` clauses:

```F#
function covariance(a: T^1, b: T^1)
where comm(a, b) = {
    (a - mean(a)) * (b - mean(b))
}
```

`comm(a, b)` declares the arguments interchangeable. Two things must combine
for triangular iteration (both are machine-checked necessities, not
heuristics):

1. **The kernel must be commutative** in those positions (`comm`), and
2. **The same array must occupy them** at the call site.

```F#
let sameArray = covariance(B, B)    // triangular: SymIdx output
let different = covariance(B, D)    // rectangular: dense output, even with comm
```

Commutativity without identity buys nothing — even when `B` and `D` share
the same index types and extents. (Checked: `shared_units_insufficient` in
[proofs.md](proofs.md).) Identity without commutativity also buys nothing.
The compiler detects identity from the call site and commutativity from the
kernel — which, incidentally, is *why* Blade has exactly these two loop
constructors: `method_for` binds all the arrays (identity is detectable),
`object_for` binds the kernel (commutativity is detectable). That's a
theorem, too.

## 10. Arity Polymorphism

Possibly the most powerful feature in Blade: functions over any number of
arrays, with correct iteration and output types for each arity.

```F#
function comoment_prod(A: Poly<T^1>)
where comm(A) -> T^1 = {
    match arity(A) with
    | 0 -> 1 // recursion terminator: identity
    | _ ->
        let head :: tail = A
        (head - mean(head)) * comoment_prod(tail)
}

function comoment_generator(A: Poly<T^1>)
where comm(A) -> T^0 = {
    mean(comoment_prod(A))
}
```

One generator, every comoment:

```F#
type Array2D = Array<Float like Idx<X>, Idx<T>>
let A: Array2D = ...

let comomentLoop = object_for(comoment_generator)
let covariance = comomentLoop <@> (A, A)
let coskewness = comomentLoop <@> (A, A, A)
let cokurtosis = comomentLoop <@> (A, A, A, A)
```

```
covariance : Array<Float like SymIdx<2, X>>    // 2!  = 2x  speedup
coskewness : Array<Float like SymIdx<3, X>>    // 3!  = 6x
cokurtosis : Array<Float like SymIdx<4, X>>    // 4!  = 24x
```

Arity determines loop depth, output rank, and symmetry — deduced, typed, and
optimized automatically.

## 11. Units of Measure

Primitive types can carry units:

```F#
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
let speed3 = dist / time         // meters/seconds = mps: OK

(speed1 + speed2 + speed3) / 3   // 3.0
speed1 + dist                    // Error! mps + meters
```

Units can carry valid ranges (`Float<meters, min=0.0>`), encoding physical
constraints checked at runtime — salinity that can never go negative, and so
on.

---

Continue with [quickstart-2.md](quickstart-2.md): virtual arrays, for-loop
sugar, zero elements, parallelism, combinators, Reynolds operators,
equivariance, and metaprogramming.
