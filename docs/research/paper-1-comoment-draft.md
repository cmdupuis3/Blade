# One Function, Every Comoment: Index Types, Loop Objects, and Arity Polymorphism in Blade

Christopher Dupuis — draft of 2026-10-05

> **Draft status.** First full draft of 2026-10-05 (master `4fe4e1a4`), revised
> 2026-10-27 against master `5960337a` for the alias and transpose rules, the
> structure-first calculus and the Fortran benchmark. Plan:
> `docs/plans/plan-paper-1-comoment.md`. Target venue: Journal of Functional
> Programming (decided 2026-10-07; ECOOP as the conference alternative). Assumed
> where the plan's other decisions are still open: the optimality theorem in the
> main text, both program forms shown.
>
> *Checked.* Every `blade` listing below is compiled, run and value-checked by
> `blade test docs paper-1`; the types in Table 1, the plan lines in Figure 5,
> the diagnostics in §3.6, the loop nest in Figure 4 and the kernel-call counts
> in Table 3 were produced by the compiler on that commit. Theorem statements
> were read from the Rocq sources named in Appendix A.
>
> *Not yet real.* (1) §6.2 reports the repository's Blade-vs-Fortran suite as
> measured on 2026-10-20 to 10-24, stitched from three compiler commits; a
> single-commit re-run is owed, and the definitional kernel of §3.4 is not
> timed. (2) The interleaved-operand disagreement with §4.5 was fixed on
> master on 2026-10-12 (`f882eb22`): `(R, S, R)` is dense in operand order with
> an advisory, as §4.5 states. (3) The
> bibliography was assembled from memory and a search pass and needs a
> DOI-level check. (4) The tower is 2 205 statements in 59 files under Rocq
> 9.0.1; the subset concerning this paper has not been recounted since the
> revision. (5) Wording of
> the history paragraph (§7.6) and of the AI-assistance disclosure is the
> author's.

All listings share the following data: four assets observed on six days, and a
second data set over the same index types.

```blade prelude
from stats import mean
type AssetIdx = Idx<4>
type DayIdx = Idx<6>
let R: Array<Float64 like AssetIdx, DayIdx> = [
    [1.0, -2.0, 3.0, 0.5, -1.5, 2.0], [0.5, 0.8, -0.2, 0.4, 0.1, 0.6],
    [2.0, 1.0, 0.0, 1.0, 3.0, -1.0], [-1.0, 0.0, 1.5, 2.5, 0.5, 1.0]]
let S: Array<Float64 like AssetIdx, DayIdx> = [
    [0.0, 1.0, 0.0, 1.0, 0.0, 1.0], [2.0, 2.0, 1.0, 1.0, 0.0, 0.0],
    [1.0, 2.0, 3.0, 4.0, 5.0, 6.0], [3.0, 1.0, 4.0, 1.0, 5.0, 9.0]]
```

**Listing 1.** The data used throughout.

## Abstract

The order-r comoment tensor of n series (covariance, coskewness, cokurtosis)
has nʳ entries, of which only C(n+r−1, r) are distinct. Array languages make
the dense computation a one-liner and leave the saving to hand-written
triangular loop nests, one per order. We present Blade, an array-functional
language in which the programmer declares structure and the compiler derives
iteration and storage from it. Four mechanisms compose. *Index types* make an
array a curried function over named domains, among them the sorted-tuple
domain `SymIdx<r, I>`. *Loop objects* make a loop nest a first-class value,
separate from the kernel it will apply. A *commutativity declaration* on a
kernel, together with the identity of the arrays at a call site, licenses
symmetric output. *Arity polymorphism* lets the number of array arguments
determine output rank, loop depth and symmetry. Ten lines of Blade then
yield the comoment tensor of every order, each computed and stored once per
distinct entry, and the compiler declines the saving, with a stated reason,
where the program does not license it. We give a small formal core; state the
licensing law, that output symmetry is the kernel's invariance group
intersected with the stabilizer of the argument binding; characterize the
subgroup the compiler deduces; and prove in Rocq that the cell count is forced:
any program correct for every kernel obeying the declared law makes at least
C(n+r−1, r) kernel calls. The implementation compiles to C++17 and, through a
second back end, to LLVM. On the order-3 comoment of 171 series the code
derived from the ten-line program runs 1.3× faster than reviewed expert
Fortran serially and 1.3 to 1.6× faster on eight threads, in both the
reassociating and the bit-reproducible regimes, and 70× faster than the
natural dense transcription.

## 1. Introduction

The covariance matrix of n time series is among the first things a statistics
library computes. Its higher-order relatives are less well served. The
coskewness and cokurtosis tensors price asymmetric risk in a portfolio
[Jondeau and Rockinger 2006; Martellini and Ziemann 2010]; third- and
fourth-order velocity correlations close turbulence models [Hanjalić and
Launder 1972]; the bispectrum and trispectrum are how cosmologists test for
primordial non-Gaussianity [Fergusson et al. 2010]; and low-order moment
tensors are the input to tensor methods for latent-variable models [Anandkumar
et al. 2014]. Each is an order-r *comoment tensor*, and each is symmetric
under every permutation of its indices. Of its nʳ entries only C(n+r−1, r) are
distinct: at r = 4 and n = 61, 635 376 of 13 845 841.

A programmer who wants that factor today has three options, and Figure 1
shows the first two. The dense contraction (a) is one line and computes every
entry. The triangular nest (b) computes each distinct entry once, but it is
written again for every order, and it brings a packed-offset formula that
every later read must repeat. The third option is a library with a symmetric
tensor class [Schatz et al. 2014; Domino et al. 2018], which serves the
operations its authors anticipated.

```python
# (a) dense: n**3 kernel evaluations, n**3 cells
X  = R - R.mean(axis=1, keepdims=True)
C3 = np.einsum('it,jt,kt->ijk', X, X, X) / T

# (b) triangular, by hand, for order 3 only
C3 = np.empty(n * (n + 1) * (n + 2) // 6)
p = 0
for i in range(n):
    for j in range(i, n):
        for k in range(j, n):
            C3[p] = np.mean(X[i] * X[j] * X[k]); p += 1
```

```blade
function centered_prod(xs: Poly<T^1>) -> T^1 = {
    match arity(xs) with
    | 1 ->
        let head :: tail = xs
        head - mean(head)
    | _ ->
        let head :: tail = xs
        (head - mean(head)) * centered_prod(tail)
}
function comoment(xs: Poly<T^1>) where comm(xs) -> T^0 = mean(centered_prod(xs))

let M = object_for(comoment)
let C2 = M <@> (R, R) |> compute           // covariance: 10 cells, not 16
let C3 = M <@> (R, R, R) |> compute        // coskewness: 20 cells, not 64
let C4 = M <@> (R, R, R, R) |> compute     // cokurtosis: 35 cells, not 256
let cell = C3(2, 0, 1)                     // any index order reads the one stored cell
// EXPECT: cell = 0.308333333333333
```

**Figure 1.** Three ways to a coskewness tensor: (a) dense NumPy, (b) a
hand-written triangular nest, (c) Blade, where the same function also gives
covariance and cokurtosis.

This paper is about a fourth option, a language in which the saving follows
from what the programmer has already said. The symmetry of a comoment tensor
is not a property of the data. It follows from two facts about the *program*:
the kernel that combines r series does not depend on their order, and one
array supplies all r of them. Both facts are available at compile time,
provided the language gives the first a place to be stated and keeps the
second in view. Neither holds in Figure 1(a), where the kernel is implicit in
an index string, and neither survives in (b), where they have been compiled
by hand into loop bounds.

Blade is built around that observation. Four mechanisms carry it.

- **Index types.** An array's type names the domain it is defined on:
  `Array<Float64 like AssetIdx, DayIdx>` is the curried function
  `AssetIdx → DayIdx → Float64`. Domains are nominal, so two index types of
  equal extent do not mix, and they are not all boxes: `SymIdx<r, I>` is the
  domain of sorted r-tuples over `I`, with its own cardinality, storage
  bijection and enumeration order.
- **Loop objects.** `method_for(A, B)` is a loop nest over two arrays with no
  kernel yet; `object_for(f)` is a kernel with no arrays yet. Both are values.
  Applying one to its missing half with `<@>` gives a deferred computation,
  and `compute` runs it.
- **Commutativity.** `where comm(a, b)` on a kernel states that it is
  invariant under exchanging those arguments. The compiler confirms the claim
  where it can decide it and refutes it where it can find a counterexample.
- **Arity polymorphism.** A kernel may take a pack of arguments,
  `Poly<T^1>`. The number of arrays at the call site then determines the
  output's rank, the depth of the nest and, through the identity of the
  arrays, its symmetry.

Figure 1(c) uses all four. `comoment` is one function over a pack of series.
Applied through a stored loop object to two, three or four copies of `R` it
yields a rank-2, rank-3 or rank-4 result whose type is `SymIdx<r, AssetIdx>`,
stored in 10, 20 and 35 cells and filled by 10, 20 and 35 kernel calls.

The compiler's side of the bargain has three parts. It *derives*: the
triangular bounds, the packed layout and the canonicalizing read all come from
the output's index type, and no part of Figure 1(c) mentions them. It
*reports*: `blade plan` lists every structural decision as applied or
declined, with the reason. And it *refuses*: when the same kernel is applied
to two different arrays over the same index space, the output is dense and the
plan says why; when a kernel is declared commutative and is not, the program
does not compile.

The tracking earns its keep when the operands vary. For one array, a packed
routine could be written once by hand. Decompose the series into blocks, as a
distributed or out-of-core computation must, and the sub-problems run through
every combination of commutativity: three copies of block A; two of A and one
of B; one each of A, B and C. Each has its own symmetry, each is a different
type, and all are applications of the same kernel to different operands. The
compiler derives the symmetry of each from the identity of its operands, and
the sub-tensors' cells add up to exactly the distinct entries of the whole
(§3.4).

**Contributions.**

1. A language design in which index types, reified loops, kernel laws and
   argument count compose, shown end to end on one problem (§3).
2. A licensing law for arrays generated by loops: the output is invariant
   under G = H ∩ Stab(B), where H is the kernel's invariance group and
   Stab(B) the permutations fixing the binding of arrays to argument
   positions. Soundness is proved in general; necessity is shown by closed
   counterexamples; and the subgroup the compiler deduces is characterized
   exactly (§4).
3. Arity polymorphism with concatenating frames, by which argument count
   determines output rank and symmetry under a single typing rule (§4.2).
4. A lower bound in a uniform model: any program correct for every kernel
   obeying the declared law makes at least C(n+r−1, r) kernel calls, and the
   emitted nest makes exactly that many (§4.7).
5. An implementation with two back ends, C++17 and LLVM, exact structural
   counts, and a measured comparison against reviewed expert Fortran in which
   the derived code is faster in every regime (§5, §6).
6. A mechanized artifact for the statements of §4, in Rocq, with no axioms and
   no admitted lemmas, including progress and preservation for the
   structure-first fragment of the calculus and the packed output as a theorem
   about typing derivations.

We claim no new mathematics. Packed symmetric storage and the count
C(n+r−1, r) are classical, and that a repeated operand yields a symmetric
result is the textbook reading of BLAS `SYRK` [Dongarra et al. 1990]. What is
new is where the knowledge lives: in declarations and types the compiler can
check, not in loop bounds the programmer must get right. The mechanism at the
centre of the paper, triangular iteration for a commutative kernel whose
arguments are the same array, has been in Blade's predecessor since 2019
(§7.6).

## 2. The comoment tensor

Let x be n series observed at T times, with x(i, t) the value of series i at
time t and μᵢ its mean. The order-r central comoment tensor is

    M_r(i₁, …, i_r) = (1/T) Σ_t Π_k (x(i_k, t) − μ(i_k)).

Order 2 is the covariance matrix, order 3 the coskewness tensor, order 4 the
cokurtosis tensor. The product inside the sum is commutative, so M_r is
invariant under all r! permutations of (i₁, …, i_r), and its distinct entries
are indexed by the sorted tuples i₁ ≤ … ≤ i_r, of which there are C(n+r−1, r).
For n = 100 that is 171 700 of 10⁶ at order 3 and 4 421 275 of 10⁸ at order 4.
The ratio nʳ / C(n+r−1, r) rises toward r! as n grows and is well below it at
the sizes one can afford (Table 2).

Three features make this a demanding test for a language.

*The order varies.* Applications use r = 2, 3 and 4 together, and a program
written per order is the thing to avoid. The loop depth and the output rank
both depend on r.

*The kernel is user code.* Each entry is a reduction over a fiber (the time
axis) of a product of centered series. It is not a built-in scalar operator
whose algebraic properties a compiler could look up, and variants abound: raw
moments, standardized moments, weighted or masked means.

*The symmetry is conditional.* The cross-comoment of two different data sets,
`mean((x − x̄)(y − ȳ))` over series of x and of y, uses the same kernel and is
not symmetric. A system that packs the output must distinguish the two cases,
and index types alone cannot: x and y may share them. The conditional case is
not a corner. Decompose the n series into blocks and the comoment of the whole
is assembled from sub-tensors of every mixed kind, (A, A, B), (A, B, C), each
symmetric in exactly the positions that hold the same block; a system that
handles only the fully symmetric case handles only the diagonal blocks.

Current practice splits accordingly. Array libraries offer the dense form of
Figure 1(a). Specialist packages implement symmetric storage and the
algorithms that fill it [Schatz et al. 2014; Domino et al. 2018]. Tensor
compilers have begun to exploit symmetry declared on inputs, and in special
cases symmetry arising from a repeated operand [Shi et al. 2021; Ghorbani et
al. 2023; Patel et al. 2025]; §7 compares them in detail.

## 3. Blade by example

This section builds Figure 1(c) one mechanism at a time. Each step ends in
something observable: a value, a type, a line of `blade plan`, or a refusal.

### 3.1 Arrays are functions over typed indices

The type of `R` in Listing 1 is `Array<Float64 like AssetIdx, DayIdx>`. Blade
reads it as a curried function, `AssetIdx → DayIdx → Float64`. Indexing is
application, and partial application yields an array of lower rank:

```blade
let row = R(1)          // one asset's series: Array<Float64 like DayIdx>
let x = R(1, 2)         // a scalar
let y = R(1)(2)         // the same scalar: R(i, t) is R(i)(t)
// EXPECT: x = -0.2
// EXPECT: y = -0.2
```

`AssetIdx` and `DayIdx` are *named index types*. A name gives an index type
nominal identity: an index value drawn from `DayIdx` is not accepted where an
`AssetIdx` is expected, whatever their extents (§3.6). Functions do not
mention concrete index types at all. They are written over abstract types
`T^r`, an element type with a rank, so that `mean : T^1 → T^0` reduces a
rank-1 array of any element type over any index type.

### 3.2 Kernels and loop objects

A *kernel* is an ordinary function whose parameter ranks say how much of each
argument it consumes. `mean` consumes rank 1. Iterating it over `R`, whose
rank is 2, leaves one dimension to iterate over, which we call the *frame*:

```blade
let means = method_for(R) <@> mean |> compute      // one mean per asset

function cov(a: T^1, b: T^1) -> T^0 = mean((a - mean(a)) * (b - mean(b)))
let pairs = method_for(R, R)                       // the pair space, as a value
let D = pairs <@> cov |> compute                   // 4 x 4 = 16 kernel calls
// EXPECT: means = [0.5, 0.366666666666667, 1.0, 0.75]
```

`method_for(R, R)` is a *loop object*: a loop nest over the frames of its
arrays, with no body. Its type is `MethodLoop<2>`. The operator `<@>` supplies
the kernel, giving a deferred computation, and `|> compute` materializes it.
With two arrays the frames are iterated independently, so `pairs` ranges over
all (i, j) and `D` has type `Array<Float64 like AssetIdx, AssetIdx>`: frames
concatenate. This is the point at which Blade departs from rank-polymorphic
lifting in the APL tradition, where the frames of the arguments must agree and
are traversed together. In Blade, traversing together is the explicit
`method_for(zip(A, B))`, and the default is the outer product, because the
outer product is what an r-ary statistic over one data set needs.

### 3.3 Commutativity and identity

`D` holds 16 numbers of which 10 are distinct. Nothing in the program so far
entitles the compiler to know that. One clause does:

```blade
function cov(a: T^1, b: T^1) where comm(a, b) -> T^0 = mean((a - mean(a)) * (b - mean(b)))
let C = method_for(R, R) <@> cov |> compute        // 10 kernel calls, 10 cells
let c01 = C(0, 1)
let c10 = C(1, 0)                                  // sorted to (0, 1) on the way in
// EXPECT: C = [[3.16666666666667, -0.258333333333333, -1.5, 0.625], [0.108888888888889, -0.05, -0.133333333333333], [1.66666666666667, -0.583333333333333], [1.22916666666667]]
// EXPECT: c10 = -0.258333333333333
```

The type of `C` is now `Array<Float64 like SymIdx<2, AssetIdx>>`. Its printed
value has rows of length 4, 3, 2 and 1: the storage is the upper triangle.
Reads take subscripts in any order and canonicalize them. The packed type is
what the compiler infers; it is not imposed. Ascribing the dense
`Array<Float64 like AssetIdx, AssetIdx>` to the same binding is accepted and
elaborates the dense array, because a declared type fixes the layout at that
boundary: writing one is how a programmer opts out.

Two things had to be true for this to happen, and the clause supplied only one
of them. The other is that both arguments are `R`. Apply the same kernel to
two different arrays over the same index types and the clause licenses
nothing:

```blade cont
let cross = method_for(R, S) <@> cov |> compute    // same index types, different array
let x01 = cross(0, 1)
let x10 = cross(1, 0)
// EXPECT: x01 = -0.25
// EXPECT: x10 = 0.116666666666667
```

`cross` is dense, with type `Array<Float64 like AssetIdx, AssetIdx>`, and it
has to be: `cross(0, 1)` is the covariance of asset 0 in `R` with asset 1 in
`S`, and `cross(1, 0)` is a different number. The compiler states its
reasoning (Figure 5): packed storage is "licensed only when the SAME array
fills" the commuting positions. Identity is by binding, not by type and not by
value: `R` and `S` have the same type, and two arrays with equal contents are
still two arrays.

An alias is a binding, and the question is whether it names the same array.
It does when nothing can ever write through either name, which the compiler
proves fail-closed over the whole module before it grants anything:

```blade
let R2 = R                                              // an alias, never written
let viaAlias = method_for(R, R2) <@> lambda(a, b) where comm(a, b) -> prodsum(a, b) |> compute
let v01 = viaAlias(0, 1)
let v10 = viaAlias(1, 0)
// EXPECT: v01 = -0.45
// EXPECT: v10 = -0.45
```

`viaAlias` has type `SymIdx<2, AssetIdx>` and the plan records "`R2` = `R`:
one array for the identity groups", with the proof: the storage is a literal,
no statement assigns either name or calls anything that could. The proof is
syntactic and conservative: a definition it cannot see into counts as
writing. `cov` names `mean` from the standard library's `stats` module, so
with `cov` as the kernel the proof declines and `R2` stays a second array,
and the plan says so; with a kernel defined in the same module it packs.
The same mechanism reaches through a call: an unannotated helper
`pairs_of(x, y) = method_for(x, y) <@> cov |> compute` is specialized on
whether its arguments are one array, so `pairs_of(R, R)` returns a packed
`SymIdx<2, _>` and `pairs_of(R, S)` a dense one, from one definition. A
declared return type fixes the layout and switches this off, because a
declaration is the programmer's decision.

The identity groups are *runs of neighbouring* operands. Interleave the
repeat and the compiler keeps the result dense, in operand order, and says
what spelling would pack it:

```blade
let K = method_for(R, S, R) <@> lambda(a, b, c) where comm(a, b, c) -> prodsum(a, b, c) |> compute
let k = K(0, 1, 2)                                      // f(R(0), S(1), R(2)), in operand order
let t = prodsum(R(0), S(1), R(2))
// WARN: BL4010
// EXPECT: k = 0.5
// EXPECT: t = 0.5
```

```text
warning[BL4010]: `where comm` pairs `a` with `c` (operands 1 and 3, both `R`), but a
commuting pair compacts only when its two slots are ADJACENT -- in the operand list
and in the parameter list (formalism 8.3: identity groups are runs of neighbouring
identical operands) -- so `method_for(R, S, R)` is stored dense in operand order.
The compact form stores one cell per orbit (n*C(n+1, 2) cells instead of n^3 for
three rank-1 operands): put the pair side by side -- `method_for(R, R, S) <@>
lambda(a, c, b) where comm(a, c, b)` -- and read the result in that order.
```

§4.5 says why the rule is adjacency and not the full group.

### 3.4 One function, every order

`cov` takes exactly two series. The comoment of Figure 1(c) takes any number:

```blade prelude
function centered_prod(xs: Poly<T^1>) -> T^1 = {
    match arity(xs) with
    | 1 ->
        let head :: tail = xs
        head - mean(head)
    | _ ->
        let head :: tail = xs
        (head - mean(head)) * centered_prod(tail)
}
function comoment(xs: Poly<T^1>) where comm(xs) -> T^0 = mean(centered_prod(xs))
```

A parameter of type `Poly<T^1>` is a *pack* of rank-1 arguments. `arity(xs)`
is its size, a compile-time quantity; `head :: tail` splits it; and recursion
on the tail is unrolled when the kernel is specialized to a call site.
`comoment` is the mean of the elementwise product of the centered series,
which is the definition of §2 transcribed.

```blade
let M = object_for(comoment)
let C3 = M <@> (R, R, R) |> compute
let direct = comoment(R(2), R(1), R(0))    // the kernel itself, called at arity 3
let cell = C3(0, 1, 2)
// EXPECT: direct = 0.308333333333333
// EXPECT: cell = 0.308333333333333
```

`object_for(comoment)` is the other kind of loop object: the kernel is fixed
and the arrays are awaited. Its type is `ObjectLoop<_>`, the underscore
standing for an arity not yet chosen. Each application chooses one. The number
of arrays sets the arity of the kernel instance, the depth of the nest and the
rank of the result, and the identity of the arrays sets the symmetry. Table 1
lists the call sites we have checked; the types are the compiler's own.

| Call | Output type | Cells | Kernel calls |
|---|---|---|---|
| `M <@> (R, R)` | `SymIdx<2, AssetIdx>` | 10 | 10 |
| `M <@> (R, R, R)` | `SymIdx<3, AssetIdx>` | 20 | 20 |
| `M <@> (R, R, R, R)` | `SymIdx<4, AssetIdx>` | 35 | 35 |
| `M <@> (R, R, S)` | `SymIdx<2, AssetIdx>, AssetIdx` | 40 | 40 |
| `M <@> (R, R, S, S)` | `SymIdx<2, AssetIdx>, SymIdx<2, AssetIdx>` | 100 | 100 |
| `M <@> (G, G, G)`, `G` over `XIdx, YIdx, DayIdx` | `SymIdx<3, <XIdx, YIdx>>` | 20 | 20 |
| `method_for(R, S) <@> cov` | `AssetIdx, AssetIdx` | 16 | 16 |

**Table 1.** One kernel, seven call sites. Types as displayed by the compiler,
with `Array<Float64 like …>` elided. The emitted nest makes one kernel call per
stored cell (Figure 4), so the last two columns agree.

Two rows deserve comment. In `(R, R, S)` the first two positions hold the same
array and the third does not, so the result is symmetric in its first two
indices only. In `(G, G, G)` each argument is a 2 × 2 grid of series. The
kernel still consumes rank 1, so each argument's frame is now two-dimensional,
and the symmetry is over whole frame *tuples*: exchanging the (x, y) position
of one argument with that of another. The output is a symmetric rank-3 tensor
over the four grid points, typed `SymIdx<3, <XIdx, YIdx>>` and readable either
by grid position or by one (x, y) pair per argument. It is not symmetric in x
and in y separately, and §4.3 shows that no per-dimension packed layout could
store it.

```blade
type XIdx = Idx<2>
type YIdx = Idx<2>
let G: Array<Float64 like XIdx, YIdx, DayIdx> = [
    [[1.0, -2.0, 3.0, 0.5, -1.5, 2.0], [0.5, 0.8, -0.2, 0.4, 0.1, 0.6]],
    [[2.0, 1.0, 0.0, 1.0, 3.0, -1.0], [-1.0, 0.0, 1.5, 2.5, 0.5, 1.0]]]
let M = object_for(comoment)
let G3 = M <@> (G, G, G) |> compute        // a 2 x 2 grid of series: still 20 cells
let RRS = M <@> (R, R, S) |> compute       // symmetric in the first two slots: 10 x 4 cells
let g = G3(0, 1, 2)                        // by grid position ...
let g6 = G3(0, 0, 0, 1, 1, 0)              // ... or by (x, y) per argument
let q = RRS(1, 0, 3)
let q2 = RRS(0, 1, 3)
// EXPECT: g = 0.308333333333333
// EXPECT: g6 = 0.308333333333333
// EXPECT: q = 0.868055555555556
// EXPECT: q2 = 0.868055555555556
```

**Decomposition.** The mixed rows of Table 1 are not curiosities; they are
what a decomposed computation consists of. Split the four assets into two
blocks of two, as a distributed or out-of-core computation would, and the
order-3 comoment of the whole is the union of four sub-problems, one per
multiset of blocks. Every sub-problem is the same `M` applied to a different
combination of operands, and each receives exactly the symmetry its operands
license:

```blade
type BlockIdx = Idx<2>
let Ra: Array<Float64 like BlockIdx, DayIdx> = [
    [1.0, -2.0, 3.0, 0.5, -1.5, 2.0], [0.5, 0.8, -0.2, 0.4, 0.1, 0.6]]
let Rb: Array<Float64 like BlockIdx, DayIdx> = [
    [2.0, 1.0, 0.0, 1.0, 3.0, -1.0], [-1.0, 0.0, 1.5, 2.5, 0.5, 1.0]]
let M = object_for(comoment)
let AAA = M <@> (Ra, Ra, Ra) |> compute     // SymIdx<3, BlockIdx>:            4 cells
let AAB = M <@> (Ra, Ra, Rb) |> compute     // SymIdx<2, BlockIdx>, BlockIdx:  6 cells
let ABB = M <@> (Ra, Rb, Rb) |> compute     // BlockIdx, SymIdx<2, BlockIdx>:  6 cells
let BBB = M <@> (Rb, Rb, Rb) |> compute     // SymIdx<3, BlockIdx>:            4 cells
let whole = M <@> (R, R, R) |> compute      // SymIdx<3, AssetIdx>:           20 cells
let c = AAB(0, 1, 0)                         // assets 0 and 1 of block A, asset 0 of block B
let w = whole(0, 1, 2)                       // the same entry of the whole tensor
// EXPECT: c = 0.308333333333333
// EXPECT: w = 0.308333333333333
```

The four sub-tensors hold 4 + 6 + 6 + 4 = 20 cells, the C(6, 3) distinct
entries of the whole, with no entry computed twice and none missed. The
identity holds in general: summed over multisets of blocks, the products of
per-block cell counts in Theorem 6 add up to C(n+r−1, r). Nothing was written
per combination. A single-tensor packed routine, however good, covers only the
diagonal blocks of this picture; the tracking of symmetry through operands is
what covers the rest.

**Deduced, then pinned.** The `comm(xs)` on `comoment` is not taken on trust.
The compiler deduces that the kernel is invariant under every permutation of
its pack, at every arity, from the shape of the recursion: an associative and
commutative operation folded over the pack (§4.6). It does not act on a
deduction silently. Without the clause the result is dense and the compiler
says what it found:

```blade
function comoment_unpinned(xs: Poly<T^1>) -> T^0 = mean(centered_prod(xs))
let U3 = object_for(comoment_unpinned) <@> (R, R, R) |> compute    // dense: 64 cells
let u = U3(2, 1, 0)
// WARN: BL4010
// EXPECT: u = 0.308333333333333
```

```text
warning[BL4010]: kernel `comoment_unpinned` deduces commutative over its argument
pack `xs` (at every arity) and all 3 positions receive the same array: output
storage is DENSE today. Pin `where comm(xs)` on `comoment_unpinned` to opt into
compact symmetric (triangular) storage.
```

The pin is deliberate. Whether a result is packed changes its type, and a type
should not change because an optimizer became cleverer. The declaration is the
programmer's decision; the deduction is the compiler's evidence for it.

### 3.5 Loops as values

`comoment` reads like the definition, and it pays for that: each of the r rows
is centered again in every cell. The efficient program centers once. Because
loops are values, the two stages can be named, composed and reused before
anything runs:

```blade
function center(x: T^1) -> T^1 = x - mean(x)
function unit_scale(x: T^1) -> T^1 = x / sqrt(mean(x * x))

let standardize = object_for(center) >>@ object_for(unit_scale)    // a pipeline; nothing runs
let Z = standardize <@> R |> compute                               // standardized rows

let pairs = method_for(Z, Z)                                       // loop nests, unapplied
let triples = method_for(Z, Z, Z)
let corr = pairs <@> lambda(a, b) where comm(a, b) -> prodsum(a, b) / 6.0 |> compute
let skew = triples <@> lambda(a, b, c) where comm(a, b, c) -> prodsum(a, b, c) / 6.0 |> compute
let r01 = corr(0, 1)
let s000 = skew(0, 0, 0)
// EXPECT: r01 = -0.439933961453747
// EXPECT: s000 = -0.133093773224767
```

`>>@` composes two object loops into one, of type `ObjectLoop<1>`; applied to
`R` it runs as a single pass over the rows, with the two means hoisted out of
the elementwise loops. `Z` keeps the type of `R`, named indices included.
`pairs` and `triples` have types `MethodLoop<2>` and `MethodLoop<3>` and
generate no code until a kernel arrives. The kernels here are lambdas over
`prodsum`, the sum of an elementwise product, and their results are the
correlation matrix (`SymIdx<2, AssetIdx>`) and the standardized coskewness
tensor (`SymIdx<3, AssetIdx>`). At order 2 the nest is a symmetric rank-k
update: above a size threshold the compiler runs it through a packed register
tile of its own, and can hand it to a BLAS `dsyrk` instead when one is
configured and preferred; at order 3, where no BLAS routine exists, it runs a
simplex tile of its own (§5). Below the thresholds, as here, the per-cell nest
is emitted with its inner fold vectorized. The programmer wrote the same
thing in every case.

### 3.6 What the compiler refuses

The savings above are worth having only if they cannot be obtained wrongly.
Three refusals guard them.

*An index from the wrong domain.* A loop over days cannot subscript a tensor
over assets, even when the extents happen to agree:

```blade rejects
function cov(a: T^1, b: T^1) where comm(a, b) -> T^0 = mean((a - mean(a)) * (b - mean(b)))
let C = method_for(R, R) <@> cov |> compute
let oops = method_for(range<DayIdx>) <@> lambda(t) -> C(t, t) |> compute
// ERROR: BL4003
```

```text
error[BL4003]: Array index tag mismatch: slot expects 'AssetIdx' but argument
has type 'DayIdx'.
```

*Series over different time axes.* The body of `cov` multiplies its two
arguments elementwise, which walks them as one index space. Rows of an array
over days and rows of an array over hours do not qualify, although both have
six entries:

```blade rejects
type HourIdx = Idx<6>
let H: Array<Float64 like AssetIdx, HourIdx> = [
    [0.0, 1.0, 0.0, 1.0, 0.0, 1.0], [2.0, 2.0, 1.0, 1.0, 0.0, 0.0],
    [1.0, 2.0, 3.0, 4.0, 5.0, 6.0], [3.0, 1.0, 4.0, 1.0, 5.0, 9.0]]
function cov(a: T^1, b: T^1) where comm(a, b) -> T^0 = mean((a - mean(a)) * (b - mean(b)))
let X = method_for(R, H) <@> cov |> compute
// ERROR: BL3027
```

*A commutativity claim that is false.* The kernel below is not symmetric in
its arguments. Packed storage would compute one of `m(i, j)`, `m(j, i)` and
answer both reads with it, so the program is rejected with a witness:

```blade rejects
let v = R(0)
let m = method_for(v, v) <@> lambda(x, y) where comm(x, y) -> x * x * y |> compute
// ERROR: BL4013
```

```text
error[BL4013]: `where comm(x, y)` contradicts the kernel body, which is provably
NOT commutative under that swap: at x = 2, y = 1 the body is 4, but with the two
exchanged (x = 1, y = 2) it is 2. [...] Remove the comm clause (dense storage
computes both triangles), or wrap the kernel in reynolds(...) if symmetrizing it
is what you intend.
```

The suggested repair manufactures a commutative kernel by summing the given
one over the permutations of its arguments:

```blade
let v = R(0)
let m = method_for(v, v) <@> reynolds(lambda(x, y) where comm(x, y) -> x * x * y) |> compute
let m01 = m(0, 1)
let m10 = m(1, 0)
// EXPECT: m01 = 2.0
// EXPECT: m10 = 2.0
```

The check has a boundary, and we state it here and again in §8. For kernels
over scalars the compiler decides a `comm` claim by a sign analysis and by
evaluating the body at sample points. For the pack kernel of §3.4 it deduces
the claim structurally. For a kernel over rows that fits neither pattern, such
as the two-argument `cov` of §3.3, it currently accepts the declaration as
given.

## 4. Formal core

We present the smallest fragment that carries the claims of §3: enough syntax
to write the comoment program, four typing rules, one semantic equation, and
the theorems that relate them. The full language (masks, sparse and ragged
index types, recursive arrays, automatic differentiation, units) is outside
this paper.

### 4.1 Syntax

```text
Index types   I ::= Idx<n> | N | SymIdx<r, I> | I₁ × … × I_s        (N a declared name)
Types         τ ::= B | Array<B like I₁, …, I_k> | T^r | Poly<T^k>
                  | MethodLoop | ObjectLoop | Comp[τ]
Kernels       f ::= function f(x₁: T^k₁, …, xₙ: T^kₙ) [where comm(xᵢ, …, xⱼ)] -> T^m = e
                  | function f(xs: Poly<T^k>)           [where comm(xs)]      -> T^m = e
Expressions   e ::= x | c | e(e₁, …, e_k) | e₁ ⊕ e₂ | f(e₁, …, eₙ)
                  | method_for(A₁, …, Aₙ) | object_for(f)
                  | e <@> e | e <*> e | e >>@ e | e |> compute
```

**Figure 2.** Syntax of the fragment. `B` ranges over base types, `⊕` over
elementwise operators.

An index type denotes a finite domain of tuples together with a bijection onto
an initial segment of the naturals (its storage offsets) whose order is the
lexicographic order on the domain. `Idx<n>` denotes {0, …, n−1}. `SymIdx<r, I>`
denotes the sorted r-tuples over I, {(i₁, …, i_r) : i₁ ≤ … ≤ i_r}. A product
denotes the product domain in row-major order. `Array<B like I₁, …, I_k>`
denotes the functions I₁ → … → I_k → B.

### 4.2 Typing

```text
Γ ⊢ A : Array<B like I₁, I₂, …, I_k>      Γ ⊢ e : Nat<I₁>
──────────────────────────────────────────────────────────  (Sub)
Γ ⊢ A(e) : Array<B like I₂, …, I_k>

Γ ⊢ Aᵢ : Array<Bᵢ like Īᵢ>   (i = 1..n)             Γ ⊢ f kernel
──────────────────────────────────────────  (Method)   ──────────────────────────────  (Object)
Γ ⊢ method_for(A₁, …, Aₙ) : MethodLoop[A₁ … Aₙ]        Γ ⊢ object_for(f) : ObjectLoop[f]

Γ ⊢ M : MethodLoop[A₁ … Aₙ]      f : (T^k₁, …, T^kₙ) → T^m
Īᵢ = S̄ᵢ · T̄ᵢ with |T̄ᵢ| = kᵢ        compatible(f, T̄₁ … T̄ₙ)
J̄ = OutIdx(f; A₁ … Aₙ)
───────────────────────────────────────────────────────  (App)
Γ ⊢ M <@> f : Comp[Array<B like J̄, T̄_f>]

OutIdx(f; A₁ … Aₙ) = contrib(g₁) · … · contrib(g_p)       where g₁ … g_p are the identity groups:
    maximal runs of neighbouring positions whose operands have the same key (§4.4) and lie in one comm block of f
contrib(g) = S̄_g                    if |g| = 1
contrib(g) = SymIdx<|g|, ×S̄_g>      if |g| > 1                                          (Group)
```

**Figure 3.** Typing rules. `Īᵢ = S̄ᵢ · T̄ᵢ` splits an array's index types into
its frame and the trailing kᵢ that the kernel consumes. The rule for
`O <@> (A₁, …, Aₙ)` with `O : ObjectLoop[f]` has the same premises and
conclusion as (App).

(Sub) is the subscript rule: a subscript into a slot of index type I must be a
`Nat<I>`, an index value whose provenance is I. Iteration produces such values
(`range<I>`, and the frame variables of a loop object). An integer of unknown
provenance is admitted with a run-time bounds check, and an index value of a
different named type is refused; this is the first refusal of §3.6.

(App) is where arity polymorphism lives. A pack kernel `f(xs: Poly<T^k>)` is
instantiated at n, the number of operands, with every kᵢ = k. The output rank
is then r′ = Σᵢ (rank Aᵢ − kᵢ) + m: the frames *concatenate*, and the kernel's
output rank is appended. Nothing in the rule requires the frames to agree,
which is the difference from rank-polymorphic lifting [Slepak et al. 2014]:
there a 3-ary kernel of cell rank 1 over three copies of an n × T array yields
a vector, the diagonal of our result; here it yields the rank-3 tensor.

(Group) assigns the output its index types. Within an identity group of size
g > 1 the g frame positions are interchangeable, so they contribute the single
index type `SymIdx<g, ×S̄>` over the *product* of the frame's index types.
Groups of size 1 contribute their frames unchanged. Two points of the rule are
essential and are justified in §4.3: the group is defined by the identity of
the array, not by its type; and for a multi-dimensional frame the symmetric
index type is over the compound frame, not one per dimension.

### 4.3 The licensing law

Fix a call site with kernel f of arity n and binding B, the map from argument
positions to arrays. Write D(A)(i) for the fiber of array A at frame index i.
The output of the call is the function

    Out_B(i₁, …, iₙ) = f(D(B 1)(i₁), …, D(B n)(iₙ)).

Let H be the invariance group of the kernel, the permutations s of positions
with f ∘ s = f, and Stab(B) the stabilizer of the binding, the s with
B ∘ s = B.

**Theorem 1 (soundness of the licence).** If s ∈ H ∩ Stab(B), then
Out_B(i_{s(1)}, …, i_{s(n)}) = Out_B(i₁, …, iₙ) for all frame indices.

The proof is two lines, and it holds for any arity, any permutation and any
frame type; in particular, when the frame is a product, s acts on whole frame
tuples. For the comoment, H is the full symmetric group; at `(R, R, R)` so is
Stab(B), and the output is fully symmetric; at `(R, R, S)` the stabilizer is
the group exchanging the first two positions; at `(R, S)` it is trivial.

Each ingredient is also necessary, in the sense that dropping it admits a
counterexample.

**Theorem 2 (necessity).** (a) There is a commutative kernel and a pair of
distinct arrays over one index space for which Out(1, 0) ≠ Out(0, 1): a shared
index space is not identity. (b) There are arrays whose data are themselves
symmetric and a non-invariant kernel for which the output is not symmetric:
symmetry of the input is consumed by the kernel, not propagated. (c) For
arrays with a two-dimensional frame, exchanging the indices of one frame
dimension between two positions, while leaving the other dimension fixed, is
not a symmetry of the output. (d) For d ≥ 2 frame dimensions of extents
nⱼ ≥ 2 and r ≥ 2,

    Πⱼ C(nⱼ + r − 1, r)  <  C(Πⱼ nⱼ + r − 1, r),

so no layout built from one triangular factor per dimension has enough cells
to store the jointly symmetric output.

Parts (a) and (b) are why (Group) keys on identity, and why `cross` in §3.3 is
dense. Parts (c) and (d) are why the `(G, G, G)` row of Table 1 has the type
`SymIdx<3, <XIdx, YIdx>>` over the four grid points, with 20 cells, and not a
product of two symmetric types over x and over y, which would have 4 · 4 = 16.
The counting inequality is the width-one case of a general statement: over two
frame dimensions large enough to contain a free cell (extents L, M ≥ r), a
scheme that canonicalizes each dimension separately and reads cells through
scalar weights needs exactly r! stored scalars per free cell, and two suffice
only for r ≤ 2 (`minimal_width_is_factorial`, `width2_iff_rank_le_2`). The
2 × 2 grid of Table 1 lies below that threshold, where particular tie-breaking
rules do reach width two; the example is covered by the inequality, the
general claim by the dichotomy.

### 4.4 Operand keys: aliases and transposes

Theorem 1 was stated for a binding of positions to arrays. The compiler
computes Stab(B) on *operand keys*. The key of an operand is a pair (ν, φ):
ν is its *root*, the array it ultimately names, and φ is a *structural map*,
an axis permutation together with a payload action, drawn from the group
S_k × C₂ in which the action (negation for antisymmetric data, conjugation
for Hermitian) is an involution that commutes with every permutation. A swap
of two positions is in Stab(B) exactly when their keys are equal.

*Roots.* A name is its own root. A top-level alias of an array that is never
written, `let R2 = R` where the root is a literal, a computed value or a
declared call, has the root of its target; inside a function specialized on a
sharing partition, a parameter has the class of its argument. Whether an
alias is never written is a syntactic, module-wide, fail-closed proof, and a
definition the proof cannot see into counts as writing (§3.3). Identity is
never decided by type or by value; Theorem 2(a) is the reason.

*Maps.* Transposes compose as permutations and actions by parity, so two
transposes cancel and two negations cancel, and an index type absorbs the
maps its own symmetry certifies: for a `SymIdx` operand a transpose is the
identity, for an `AntisymIdx` operand it is a negation, for a Hermitian one a
conjugation. A chain of transposes is normalized to its shortest word and its
action parity before keys are compared, and a surviving action is applied to
the stored representatives only; a structural zero of an antisymmetric
operand is never negated.

*Consequences.* For a dense square X, `method_for(X, transpose(X))` has keys
(ν, id) and (ν, τ) at its two positions, no swap is in Stab(B), and the pair
space is dense, as it must be: Out(i, j) = f(X(i, ·), X(·, j)) is not
exchange-invariant. For a symmetric operand the transpose is absorbed, the
keys agree, and the call is the shared one. Alias transparency is sound
because Theorem 1 needs only equal fibres at the positions a permutation
exchanges, and a never-written alias has equal fibres for its whole lifetime.
Identity stays intensional on purpose: a packed cell is evaluated once, at the
sorted representative of its orbit, and two arrays that happen to hold equal
contents are still two arrays, so that the result never depends on which copy
was named.

What is proved here is the layer beneath: that signs compose and only two
characters of the layout group descend to storage (`SymIdx` and
`AntisymIdx`), that the signed licence is sound, and that an action applies
only to stored representatives with structural zeros ahead of it
(`BladeLayout.v`, `BladeLowering.v`, `BladeMaterialization.v`); the key
algebra itself is mechanized at rank 2 as definitions (`BladeStructuralIdentity.v`).
The normal form, the cancellation, the sharing partitions and the alias proof
are implemented and pinned by the corpus, not proved.

### 4.5 What the compiler grants

H ∩ Stab(B) is the largest group Theorem 1 could license. The compiler grants
a specific subgroup of it, by design, and the design has two layers.

*On the kernel side* it deduces invariance by testing adjacent transpositions
only. For a kernel of arity n that is n − 1 tests, each a structural
comparison of the body with itself under an exchange of two neighbouring
parameters, modulo the commutativity of the operators involved. Testing all
permutations would cost n! comparisons and, in the recursive setting of pack
kernels, would have no inductive structure to follow.

*At the call site* an identity group is a maximal run of *neighbouring*
positions holding the same array. `(R, R, S)` has the groups {1, 2} and {3};
`(R, S, R)` has three groups of one, and §3.3 shows the compiler saying so.

The second layer is a decision about which spellings receive the
optimization, and it is proved sound and, for the interleaved spelling, not
optimal. A symmetry between non-adjacent positions is available by reordering
the kernel's parameters so that the positions become neighbours, and that
spelling's packed block is a contiguous, curried sub-array of the output. The
interleaved spelling could be packed with the same orbit count: for
`(A, B, A)` under a fully commutative kernel the by-name quotient has
n · C(n+1, 2) cells where the rule stores n³, that count is forced for any
program that answers in position order, and the gap is strict for n ≥ 2
(`aba_orbit_count_forced`, `aba_beats_surface_rule`, `aba_gap_strict`).
Presenting those cells in the operands' own axis order needs a layout with
permuted positions, which Blade does not build; it names the spelling that
attains the count instead (§3.3). The rule is optimal exactly when the
identity groups have pairwise distinct names (`surface_comm_optimal`), which
is every call site in this paper. Blade's design invariant is that the
fastest way to write a computation is the only way that receives the
optimization; here the invariant has a price, and the proofs state it.

The kernel-side layer is characterized exactly. Call a transposition a
*uniform symmetry* of a kernel body if exchanging the two parameters leaves
the body's value unchanged under every interpretation of its operators
consistent with their declared commutativity.

**Theorem 3 (the deduced group).** In the fragment of kernels built from
parameters and unary and binary operators, some declared commutative: the
deduction rule certifies a transposition exactly when it is a uniform
symmetry; the group the compiler deduces is generated by the certified
adjacent transpositions; and it contains every group generated by adjacent
transpositions that are uniform symmetries. It is therefore the largest
Young subgroup with contiguous blocks inside the kernel's uniform symmetry
group.

The same development exhibits what lies outside by design: a kernel
g(h(a, c), b) whose symmetry (a c) is not adjacent and is reached only by
reordering parameters. It also paid for itself. Proving exactness showed that
the rule as first shipped was sound but incomplete: it missed the symmetry of
`(x + y) / (y + x)`, because a mirrored pair of operands under a
non-commutative operator ended the analysis early. The compiler was corrected,
and Theorem 3 is about the corrected rule.

### 4.6 Packs

For pack kernels the question "invariant at which arity?" has a uniform
answer.

**Theorem 4 (pack licence).** Let ⊗ be associative and commutative. The fold
of ⊗ over a non-empty pack is invariant under every permutation of the pack,
at every arity. The proof reduces an arbitrary permutation to adjacent
transpositions, which is the reason adjacent tests suffice. There is no signed
analogue: an operation that changes sign under exchange of adjacent pack
elements forces x ⊗ (x ⊗ r) = −(x ⊗ (x ⊗ r)).

`centered_prod` is such a fold, of elementwise multiplication over the pack of
centered rows, and this is the deduction reported by the warning in §3.4. The
theorem is stated for exact arithmetic. For floating point the consequence we
rely on is weaker and sufficient: each orbit of cells is computed once, from
one ordering of its arguments, so the stored tensor is symmetric by
construction. It need not be bitwise equal to the dense result in cells whose
dense evaluation used a different argument order.

Two further facts about floating point are proved and shape the design. A
packed cell is evaluated once, at the sorted representative of its orbit, and
that is what the type means: two reads of one orbit return one number, where
a dense evaluation can return two that differ under cancellation. And a
licence to reorder a fold cannot be read off float arithmetic: any
equivalence under which rounded addition is associative already identifies
2ᵖ with 2ᵖ + 2 for every p ≥ 2 (`rounded_licence_coarse`), so a float
licence is a declaration, necessarily coarse, and the typing and quotient
theorems are proved relative to a declared equivalence on scalars
(`typed_app_is_quotient_R`).

### 4.7 Storage and optimality

**Theorem 5 (storage).** The enumeration of sorted r-tuples below n is sound,
complete and duplicate-free, and has C(n+r−1, r) elements. The map `lj` that
replaces each coordinate after the first by its difference from its
predecessor is a bijection from sorted tuples onto storage coordinates, and
offset order under it is the lexicographic order on tuples.

A read is therefore `A(i) = buf[off(lj(sort i))]`: sort the subscript, take
differences, index. During bulk construction no transform is needed at all,
because the emitted nest iterates in `lj` coordinates directly (§5).

The count in Theorem 5 is not only achieved; it is forced. Model a program as
a decision tree that may query the kernel at any argument tuples, adaptively
and in any order, and must then report the output. The kernel is an oracle
known only to obey the declared law.

**Theorem 6 (uniform optimality).** Let the data be generic (distinct frame
indices carry distinct fibers). Any program that reports the correct output on
the sorted index tuples for *every* kernel invariant under all permutations of
its r arguments makes at least C(n+r−1, r) kernel queries. The canonical
enumeration makes exactly that many and answers every cell of the dense index
space by sorting the subscript. With several identity groups, of sizes rⱼ over
extents nⱼ, the bound is the product Πⱼ C(nⱼ + rⱼ − 1, rⱼ).

The argument is an adversary one: a program that skips an orbit cannot
distinguish the kernel from one that differs only on that orbit. The
hypotheses matter. The bound counts kernel calls and cells, not time. It is
for programs correct for *every* kernel in the class, which is exactly a
compiler's position when the kernel is user code. And genericity cannot be
dropped: for constant data one query suffices, and we prove that as a
refutation. For the four-way call `(R, R, S, S)` of Table 1 the theorem gives
10 · 10 = 100, the number of cells the compiler allocates.

The bound is a statement in a black-box model: it counts kernel evaluations
at index tuples and stored cells, for a program that must be correct on every
kernel in the law class, and both halves of that qualifier carry weight. A
kernel the program may look inside can be cheaper. For the separable class
f(a, b) = g(a) + g(b) a program that knows the form needs n evaluations of g
and never asks the kernel at a pair, beating the query bound; it cannot beat
the storage bound, which stays C(n+1, 2) (`structured_kernel_beats_bound`,
`sep_storage_bound`; proved at r = 2, the converse open). And the bilinear
algorithms for symmetric contractions of Solomonik and Demmel [2021] use
fewer multiplications than there are orbits by forming products of sums,
which are not kernel evaluations at index tuples; the bound stands in its
model and not in theirs. The bound also holds for block licences, the
subgroup a `comm` block meets with the binding's cut, and in the signed case
at every rank, where C(n, r) is forced and attained from adjacent
anti-invariance alone (`uniform_optimality_subgroup`, `antisym_lower_bound_r`,
`antisym_attained_r`).

**Mechanization.** Theorems 1 to 6 are proved in Rocq 9.0.1 with the standard
library only, no axioms and no admitted lemmas; the continuous build runs
`coqc` and `coqchk` and prints the assumptions of every result. Appendix A
maps each theorem to its source. They sit in a development of 2 205
statements in 59 files. The structure-first core also has a surface calculus
with progress, preservation and normalization, a denotation into the objects
of §4.3 that every reduction step preserves, and the quotient stated as a
theorem about typing derivations: the typed output of `L <@> f` holds one
cell per orbit, every dense cell is recovered by canonicalizing access, and
nothing is quotiented without `comm` (`typed_app_is_quotient`,
`BladeSurface.v`). Its scope is the fragment of Figure 3 with rank-0 kernels,
rank-1 operands, fixed arity, one `comm` flag for the whole argument list and
top-level names. The rules beyond it, (App) with kernel output dimensions and
packs, (Group) over multi-dimensional frames, and the rank deduction in
general, are implemented and tested, not proved; nor is the asymptotic ratio
nʳ / C(n+r−1, r) → r! stated in the development, which is one reason we
report realized ratios (§6).

**Worked instance.** For `M <@> (R, R, R)`: Theorem 4 gives H = S₃ for
`comoment`. The binding is constant, so Stab(B) = S₃ and Theorem 1 licenses
full symmetry. The three positions form one identity group, so (Group) assigns
`SymIdx<3, AssetIdx>`. Theorem 5 gives C(6, 3) = 20 cells and the read
transform. Theorem 6 says that no program correct for every commutative kernel
can fill the tensor with fewer than 20 kernel calls. For the decomposition of
§3.4 the product form of Theorem 6 gives each sub-problem its own bound,
C(3, 3), C(3, 2) · 2, 2 · C(3, 2) and C(3, 3), and the four bounds sum to the
20 of the whole.

## 5. Compilation

The compiler is written in F# and emits C++17, which it compiles with g++. A
tree-walking interpreter over the same intermediate representation serves the
REPL and as a differential oracle: the test corpus is run through both and
the outputs compared.

**From type to nest.** The (Group) rule fixes the output's index types during
type checking. Lowering turns each `SymIdx<g, ·>` into g loop levels with
dependent bounds and one packed pool. Figure 4 shows what is emitted for `C3`
of Figure 1.

```cpp
double* pool = pool_base(C3.data);                        // 20 cells = C(6, 3)
for (size_t i0 = 0; i0 < 4; i0++) {
    const size_t end0 = 6 - i0;
    const size_t off0 = 20 - (end0 * (end0 - 1) * (end0 - 2)) / 6;
    Array<double, 1> R_i0 = { R.data[i0], R.extents + 1 };
    for (size_t i1 = 0; i1 < 4 - i0; i1++) {
        const size_t hi1  = end0 - 1;
        const size_t end1 = hi1 - i1;
        const size_t off1 = off0 + (hi1 * (hi1 - 1)) / 2 - (end1 * (end1 - 1)) / 2;
        Array<double, 1> R_i1 = { R.data[i1 + i0], R.extents + 1 };
        double* restrict row = pool + off1;
        for (size_t i2 = 0; i2 < 4 - i1 - i0; i2++) {
            Array<double, 1> R_i2 = { R.data[i2 + i1 + i0], R.extents + 1 };
            row[i2] = comoment_arity_3(R_i0, R_i1, R_i2);
        }
    }
}
```

**Figure 4.** The loop nest emitted for `C3` (identifiers shortened, otherwise
verbatim).

Three properties of this code are consequences of §4.7. The loop variables are
the `lj` coordinates of Theorem 5: every loop starts at zero, the bounds
shrink (`i2 < 4 - i1 - i0`), and the data row is recovered by adding the
enclosing variables (`i2 + i1 + i0`). Because iteration coordinates equal
storage coordinates, the innermost loop writes a contiguous run of the pool.
And the offset of that run is a closed form in the outer variables, hoisted
one level at a time, so no per-cell address arithmetic remains. The more
common convention, loops with rising lower bounds, covers the same cells but
needs an offset computation or a running pointer to place each write.

**Reads.** A subscripted read such as `C3(2, 0, 1)` emits a sort of the three
subscripts followed by the difference transform and one load.

**Kernels.** A pack kernel is specialized per arity: `comoment_arity_3` above
is the unrolled recursion of Figure 1 at three arguments. Inside a kernel,
subexpressions that do not depend on the element being computed are bound once
before the elementwise loop; in `(a - mean(a)) * (b - mean(b))` both means are
hoisted, so the kernel is linear in the length of the series.

**Routes that follow from structure.** The nest of Figure 4 is the general
form; a kernel whose body is `prodsum` over the rows of one array is
recognized as a contraction and takes a faster form of the same traversal.
At order 2 it is a symmetric rank-k update, and from p ≥ 16 series of length
k ≥ 4 the compiler emits its own packed-triangle register tile (6 × 8, GCC
vector extensions, no intrinsics), ahead of a BLAS `dsyrk` even when one is
configured, unless the build prefers the library; the plan line names the
choice. At order 3 it is the simplex of §6's benchmark: from p ≥ 24 the
compiler forms, for each pair of outer indices, the panel W = X(i) X(j) once
and multiplies it through the third index with the same register tile, each
cell remaining one ascending chain. Below those sizes the per-cell nest is
kept, with the inner `prodsum` run as eight independent accumulator lanes.
A whole-tensor fold of a packed result, `reduce(C3, (+), axes = 3)`, is one
pass over the C(n+2, 3) stored cells with the orbit multiplicities 1, 3 and
6 as weights, licensed by the fold's own `+`. A parallelism licence on the
kernel places an OpenMP pragma on the outermost triangular level with dynamic
scheduling, since the work per outer iteration is skewed. The LLVM back end
emits the same forms through a shared C core.

**Floating point.** Since October 2026 the default build takes the fastest
form every licence admits: a fold whose kernel licenses reordering, a builtin
`+` or `*` (the one inside `mean` included), a `prodsum`, a `comm` kernel or
a `where omp` fold, may be reassociated into vector lanes or thread partials,
and `a * b + c` may contract. `BLADE_FP_REPRO=1` (or `where repro` on a
function) restores the serial association in both back ends and for every
thread count; bit identity with the interpreter additionally needs
contraction off, which is how the repository's differential tests run.

**Observability.** None of the above is visible in the source, which is the
point; it must therefore be visible somewhere. `blade plan` prints every
structural decision with its justification:

```text
[symmetric-storage v1]    C3: applied [packed SymSymmetric storage over 3 levels
                          (one canonical cell per orbit); operands: R, R, R]
[triangular-iteration v1] C3: applied [triangular levels 1, 2; symcom states:
                          SCNeither, SCCommutative, SCCommutative; speedup x6]
[symmetric-storage v1]    cross: declined -- the commuting positions hold different
                          arrays (R, S); packed storage is licensed only when the SAME
                          array fills them [operands: R, S]
[triangular-iteration v1] cross: declined -- every level iterates the full box
                          (symcom states: SCNeither, SCNeither) [operands: R, S]
[invariant-hoist v1]      cov: applied [`mean(a)` does not read the element: bound once,
                          before the nest; `mean(b)` ...]
[blas-routing v1]         corr: applied [Syrk (L3, d); ...]
```

**Figure 5.** Plan output for `C3` (Figure 1), `cross` and `cov` (§3.3) and
`corr` (§3.5), abridged: source positions removed and generated names replaced
by the source names they stand for. The "speedup" printed is the asymptotic
r!.

## 6. Evaluation

We ask three questions. Does the compiler deliver the structural saving
exactly? How does the code it emits for the comoment compare with the best
hand-written Fortran for the same computation? And what is still owed? The
first two are answered by measurement below; the third is listed in §6.3.

### 6.1 Structural counts

The ceiling quoted for symmetric computation is r!. At finite n the available
ratio is nʳ / C(n+r−1, r), which is smaller (Table 2), and it is the number
against which any measurement should be read.

| n | r = 2 | r = 3 | r = 4 | r = 5 |
|---|---|---|---|---|
| 7 | 1.75 | 4.08 | 11.4 | 36.4 |
| 11 | 1.83 | 4.65 | 14.6 | 53.6 |
| 61 | 1.97 | 5.72 | 21.8 | 102 |
| 301 | 1.99 | 5.94 | 23.5 | 116 |
| r! | 2 | 6 | 24 | 120 |

**Table 2.** Available ratio nʳ / C(n+r−1, r).

To check that the emitted code attains it, we instrumented the kernel with a
counter and ran the program of Appendix B for n = 7 at orders 2 to 5, once
with the `comm` clause and once with it deleted.

| r | kernel calls with `comm` | C(n+r−1, r) | kernel calls without | nʳ |
|---|---|---|---|---|
| 2 | 28 | 28 | 49 | 49 |
| 3 | 84 | 84 | 343 | 343 |
| 4 | 210 | 210 | 2 401 | 2 401 |
| 5 | 462 | 462 | 16 807 | 16 807 |

**Table 3.** Kernel calls measured at n = 7. The packed and dense results agree
on every cell compared.

The counts equal the bound of Theorem 6 exactly, and a separate census at
n = 7, 11, 23 and 45 finds the same on every call site whose operands are one
array: C(n+1, 2), C(n+2, 3), and C(n+1, 2) · C(m+1, 2) for two identity groups,
with the negatives (no `comm`; two different arrays) at nʳ. For comparison,
NumPy does nʳ work and storage on every one of these rows and reaches the
orbit count only through hand-written `triu_indices` or
`combinations_with_replacement`, which return flat, untyped results; and
BLAS `dsyrk` writes C(n+1, 2) cells but returns an n × n array.

### 6.2 Against hand-written Fortran

**Setup.** The comparison is a benchmark suite kept in the repository
(`bench/`), run on one machine: an AMD Ryzen 7 5800HS (Zen 3, 8 cores, 16
threads, 15.4 GB), Windows 11, on AC power with the processor uncapped.
Blade's C++ lane is compiled by g++ 16.2 (`-O3 -march=native
-ffp-contract=fast`) and its LLVM lane by clang 22.1; the Fortran arms are
GNU Fortran 16.2 (`-O3 -march=native -ffp-contract=fast -fopenmp-simd`) and
Intel ifx 2026.1 (`/O3 /QxHost /fp:precise -fp-contract=fast /Qopenmp-simd`).
Each kernel has two Fortran tiers. The *natural* tier is a literal dense
transcription with array syntax and intrinsics, exploiting no symmetry. The
*expert* tier is the fastest Fortran we could write and have reviewed: packed
triangular storage, panel packing, register tiling, outer-product order and
a checksum fused into the producer are allowed; external BLAS, assembly,
vendor intrinsics, per-kernel flags and algebraic shortcuts are not. The
experts were written by one AI agent and reviewed by another; no human HPC
specialist took part, which we return to below.

Two floating-point regimes are compared. In the *licensed* regime, Blade's
default, every fold the source licenses may be reassociated, and the Fortran
expert receives exactly the same permission: an `!$omp simd reduction` on the
loops whose Blade counterpart is a licensed fold (for the comoment these are
the checksum and input sums, never the kernel loop). In the *repro* regime
(`BLADE_FP_REPRO=1`) no serial arm reorders anything, and Blade's result does
not depend on the thread count (bit-identical when contraction is also off;
the benchmark keeps contraction on). Serial arms are pinned to one
core; the eight-thread arms (`mt8`) are unbound, since libgomp cannot bind
threads on Windows, and every arm threads its input generation, kernel and
checksum alike. The timed region of one sample is a fresh process from before
the first allocation through the checksums: allocation and first touch, input
generation from an exact integer generator (so inputs are bit-identical in
every arm), the kernel, a checksum over the full logical index space, and an
input checksum; every result is checked against an independent NumPy
reference to 1e-12 relative, with no disqualifications in any session. Each
session runs a warm-up and then 21 rounds, each a seeded random permutation
of all arms plus two control programs; the 95% intervals are a round-block
bootstrap with 10 000 resamples. A block is reported as *live* only if no arm
or control drifted by more than 5% between the halves of the session and no
rolling control median moved more than 7%; otherwise it is *indicative*, with
medians shown but no verdict. A verdict needs the interval to clear 1 and the
median to clear 1.10.

**The kernel.** The benchmark's comoment is the order-3 raw moment of
p = 171 series over n = 201 samples, written as `method_for(x, x, x) <@>
lambda(a, b, c) where comm(a, b, c) -> prodsum(a, b, c) / Float64(n)`: the
`skew` form of §3.5 without the standardizing pass. The packed result has
848 046 cells against 5 000 211 dense, an available ratio of 5.89. The expert
Fortran forms, for each middle index, the panel W(i, s) = X(i, s) X(j, s) and
multiplies it through the k panels with an 8 × 6 register micro-kernel,
keeping every cell a single ascending chain; it reads the checksum back with
multiplicities 1, 3 and 6.

| Regime | Blade reference | blade-llvm | blade-cpp | gfortran expert | ifx expert | gfortran natural | ifx natural | vs gfortran expert | vs ifx expert |
|---|---|---|---|---|---|---|---|---|---|
| serial, licensed | llvm | **8.50** | 8.64 | 11.35 | 11.54 | 600 | 718 | 1.34 [1.32, 1.36] | 1.36 [1.34, 1.37] |
| serial, repro | llvm | **8.83** | 8.91 | 11.38 | 11.39 | 600 | 718 | 1.29 [1.28, 1.30] | 1.29 [1.28, 1.30] |
| mt8, licensed | cpp | — | **3.18** | 4.94 | 4.28 | 92 | 118 | 1.55 [1.51, 1.66] | 1.35 [1.28, 1.43] |
| mt8, repro | cpp | — | **3.09** | 4.80 | 4.03 | 86.6 | 113 | 1.55 [1.50, 1.60] | 1.31 [1.24, 1.34] |

**Table 4.** Order-3 comoment, p = 171, n = 201: medians in milliseconds over
21 rounds, and the ratio of the Fortran expert's median to Blade's reference
lane with its 95% interval (above 1, Blade is faster). All four blocks are
live and every ratio is a verdict win. The LLVM lane has no thread runtime,
so the eight-thread reference is the C++ lane. Sessions of 2026-10-20 and
2026-10-21; compiler commits f1910060, 6b3e5293 and 3a3925ac.

The result is that for this kernel both Blade lanes are faster than either
expert in every regime: 1.3× serially, 1.3 to 1.6× on eight threads, and 70
to 85× faster than the natural transcription. The LLVM lane and the C++ lane
are within 2% of each other serially. Peak working set is 11.5 MB for the
LLVM lane against 12.2 MB for the gfortran expert and 43 MB for the naturals.

Table 5 shows how the number was reached, because the trajectory is the
honest statement of what a compiler of this kind has to do. The first
measurement, three days after the suite was built, had Blade's LLVM lane
3.8× slower than the experts: its triangular nest was correct and simple, and
it was load-bound. Each subsequent row is one structural change to the
compiler, applied from the same source program.

| Compiler change | blade-llvm | blade-cpp | vs gfortran expert |
|---|---|---|---|
| Pilot: the per-cell triangular nest (2026-10-17) | 42.2 | 108.6 | 0.26 |
| C++ lane: simplex register tile, orbit-class checksum fold, canonical reads | 25.0 | 9.5 | 0.45 |
| LLVM lane: the same tiles through a shared C core | 9.31 | 9.59 | 1.22 |
| Top-aligned panels, the `/n` fused into the last tile store | 8.73 | 8.95 | 1.33 |
| Live-accumulator edge tiles (2026-10-20) | 8.50 | 8.64 | 1.34 |

**Table 5.** Serial licensed comoment, medians in ms; the ratio is against
the gfortran expert with the LLVM lane as reference. The repro regime moved
from 157 ms (0.08) to 8.8 ms (1.29) over the same period, the last 6 ms of it
from an ordered orbit scatter that keeps every chain's source order; the
eight-thread repro regime from 93 ms (0.06) to 3.1 ms (1.55).

None of these changes touched the source program. The simplex tile is
selected from the `comm` declaration and the `prodsum` shape of the kernel
(§5); the orbit-class fold replaces a logical walk over nʳ cells of the
packed result by one pass over its C(n+2, 3) cells with orbit weights, and
is licensed by the fold's own `+`; the ordered scatter is the repro form of
the same checksum, feeding every row chain its source sequence from one pass.
The programmer's statement did not change; what the compiler was entitled to
derive from it did.

The three related symmetric kernels of the suite tell the same story at order
2 (Table 6). `gram` is the symmetric rank-k update that §3.5's `corr` becomes
at size; `antisym` is its antisymmetric counterpart (`anticomm`, strict
storage with no diagonal); `pairkernel` is a Matérn covariance over 2 503
points, a transcendental kernel with no tile.

| Kernel, serial licensed | blade-llvm | blade-cpp | gfortran expert | ifx expert | vs gfortran | vs ifx |
|---|---|---|---|---|---|---|
| gram, p = 1601, n = 1001 | 56.8 | 58.9 | 77.2 | 70.4 | 1.36 [1.35, 1.38] | 1.24 [1.23, 1.25] |
| antisym, p = 1201, n = 401 | 27.8 | 28.8 | 31.1 | 29.7 | 1.12 [1.10, 1.14] | 1.07 [1.05, 1.08] |
| pairkernel, 2 503 points | 28.0 | 28.1 | 32.5 | 30.3 | 1.16 [1.16, 1.17] | 1.09 [1.08, 1.09] |

**Table 6.** The other symmetric kernels, serial licensed, live blocks,
medians in ms. Over the whole matrix the one remaining loss among these four
kernels is `pairkernel` on eight threads in the repro regime (0.78 and 0.79),
where Blade's checksum fold has no thread-level reproducible form yet.
Against OpenBLAS `dsyrk` on the Fortran side, Blade's own packed tile for
`gram` is 9% and 4% ahead serially.

**Reading the numbers.** Several things qualify them, and the paper's final
version will repeat the matrix on one commit before claiming more.

1. *What is measured.* On the three tiled kernels both Blade lanes run the
   same runtime tile library, 560 lines of C99 with GCC vector extensions and
   no intrinsics or assembly, compiled by g++ or clang; the compiler selects
   it from the declaration. The comparison is therefore between what a
   declaration buys and what an expert writes by hand, not between code
   generators on a naive nest. `pairkernel` has no tile and is the one
   measurement of the plain emitted nest.
2. *The experts.* They were written and reviewed by AI agents under the
   rules above. One anchor: OpenBLAS `dsyrk`, a human-tuned library routine,
   ran 10 to 14% faster than the hand-written `gram` experts, which places the
   experts near but not at library quality.
3. *Not the paper's kernel.* The benchmark kernel is `prodsum` over raw rows,
   the form of §3.5; the definitional recursive kernel of §3.4 is not timed,
   and the tile matcher accepts only ordered `prodsum` arithmetic, so it
   would not receive the tile. Timing it is owed (§6.3).
4. *Stitched sessions.* The cells of Table 4 come from sessions on three
   consecutive days and three compiler commits; the emission of the
   benchmark kernels did not change across them, but the final version will
   be one session on one commit.
5. *One host, one shape.* A single Zen 3 laptop with an uncapped clock (hence
   the drift rules); ifx's `/QxHost` tunes for an AMD processor; one size per
   kernel, frozen under 64 MB and 2.5 s caps, so there is no scaling study.
6. *Asymmetries.* Blade frees its storage inside the timed region and Fortran
   deallocates after it; team creation (0.4 to 0.9 ms) is inside the
   eight-thread region, a noticeable share of a 3 ms kernel; Fortran's
   eight-thread repro arm keeps a thread-level `reduction(+:)` while Blade's is
   bitwise for every team size.

### 6.3 Still owed

1. *The matrix on one commit*, with the definitional kernel of §3.4 timed
   beside the `prodsum` form, and a scaling sweep over p.
2. *The specialists.* `Cumulants.jl` [Domino et al. 2018], which computes the
   same tensors in block-symmetric storage, could not be run on the benchmark
   host (no Julia); it remains the comparison the application community will
   ask for, together with R's PerformanceAnalytics moment routines.
3. *The generalists at size.* NumPy `einsum` and `opt_einsum` at p = 171 for
   the dense arm, and SySTeC [Patel et al. 2025] if its artifact can be made to
   run the kernel.
4. *Economy.* Lines of source per order for each system.

## 7. Related work

### 7.1 Index-typed arrays

Dex [Paszke et al. 2021] is the closest relative of Blade's index types. It
treats arrays as tables over typed index sets, makes indexing application and
partial indexing natural, and lets users define index sets, including
dependent ones. Blade shares the view that an array is a function on its
index type. It adds index types that are *quotients* deduced from a kernel law
(`SymIdx`, and its antisymmetric and Hermitian relatives), for which the type
fixes storage and enumeration, and it makes nominal identity at equal extent
the basis for deciding which subscripts are proven in bounds and which are
checked. Sized and dependently typed arrays have a long history [Xi and
Pfenning 1998; Trojahner and Grelck 2009; Henriksen and Elsman 2021], as do
arrays as Naperian functors [Gibbons 2017]; named dimensions in the
array-library world [Hoyer and Hamman 2017] give provenance without static
checking. The view of an index type as a finite set with a group action has
a theory of its own, quotient containers [Abbott et al. 2004] and
combinatorial species [Joyal 1981], in which positions carry a group action
but no storage or compiler follows. Mullin's Mathematics of Arrays [Mullin
1988] derives layout from the array's denotation in the dense case, and
Chapel domains and ZPL regions [Chamberlain et al. 2000] make dense index
sets first-class values.

### 7.2 Deferred arrays and loop abstractions

A deferred map is an old idea: expression templates [Veldhuizen 1995], delayed
arrays in Repa [Keller et al. 2010], pull and push arrays [Claessen et al.
2012], Accelerate [Chakravarty et al. 2011], Feldspar [Axelsson et al. 2010].
`method_for(A, B) <@> f |> compute` can be read as a partially applied
delayed map, and we concede the reading. What Blade reifies separately is the
*traversal*: which arrays, outer product or zip, over which domain, with which
symmetry licence. Either half may be bound first, the halves compose with an
algebra (`<*>`, `>>@`), and the symmetry analysis needs exactly the two
halves: identity comes from the arrays and invariance from the kernel. Halide
[Ragan-Kelley et al. 2013] separates an algorithm from its schedule and gives
the programmer control of the latter; Blade has no schedule language and
derives the nest from types. Kokkos [Edwards et al. 2014] pairs an execution
policy with a functor for single loops. Looplets [Ahrens et al. 2023] and
Finch [Ahrens et al. 2025] are languages of structured iteration over sparse
and structured formats, and are complementary: Blade's structure comes from
index types and is dense. Lift [Steuwer et al. 2015] and multi-dimensional
homomorphisms [Rasch et al. 2018] derive loop nests from functional patterns
by rewriting.

### 7.3 Symmetric tensors

Packed storage for symmetric tensors with C(n+r−1, r) cells [Ballard et al.
2011], blocked compact storage and algorithms for multiplying a symmetric
tensor by the same matrix in every mode [Schatz et al. 2014], and distributed
contraction of tensors with declared symmetries [Solomonik et al. 2013, 2014]
are established. Bilinear algorithms can go below the orbit count in
multiplications for particular contractions [Solomonik and Demmel 2021], at a
cost in communication [Solomonik et al. 2021]; Theorem 6 does not contradict
them: their products of sums are not kernel evaluations at index tuples, so
the bound stands in its black-box model and not in the bilinear one (§4.7).
The group H ∩ Stab(B) is the object of double-coset canonicalization of tensor
expressions [Manssur et al. 2002; Martín-García 2008], where it decides
whether two index expressions are equal; here it licenses a storage quotient.
The Tensor Contraction Engine [Hirata 2003] propagates declared symmetries
through generated code without typing them; DASTAC [Ghorbani et al. 2024]
compresses structured tensors and records that its layouts may be suboptimal;
Liu et al. [2022] verify tensor rewrites for correctness, not optimality.
Decision-tree lower bounds [Ben-Or 1983] are the model of Theorem 6, and the
nearest mechanized instance is Eberl's comparison-sorting bound [Eberl 2017],
which has no group action.

Among compilers, Shi et al. [2021] derive output symmetry from symmetries
declared on input tensors and observe that a computation in which the same
tensor appears more than once can be symmetric in a way their definitions do
not detect. StructTensor [Ghorbani et al. 2023] infers structure, symmetry
included, through an intermediate language with inference rules, among them a
rule for self-multiplication, and uses covariance and higher-degree
interaction tensors as motivation. SySTeC [Patel et al. 2025] takes one
assignment and a list of symmetric input tensors, builds the set of
permutable indices from the declared input partitions, and generates
triangular code through Finch; it observes in prose that output symmetry
arises when an assignment has several copies of one operand, and evaluates
`SSYRK` among its kernels. Its formal development states no rule for that
case, its dense symmetric outputs are computed on the canonical triangle and
then replicated, and it supports partition symmetry but not antisymmetry.

The overlap with Blade is real: iteration over the canonical simplex, output
symmetry from a repeated operand under commutativity, and care about
diagonals. The differences run both ways. SySTeC handles sparse and structured
formats, exploits symmetric *inputs* to reduce reads, and has a peer-reviewed
evaluation against established systems; Blade is dense only. Blade attaches
the licence to a declaration on an arbitrary user kernel, makes the result's
symmetry a type with packed storage, states the identity rule and refuses when
it fails, covers antisymmetric and Hermitian cases, and proves soundness and a
lower bound. The characterization of the largest symmetry group valid for a
system, of which Theorem 1 with its converse is an instance, has a precedent
in symmetry reduction for model checking [Donaldson and Miller 2005].

### 7.4 Arity and rank

Rank polymorphism in APL and J [Hui and Kromberg 2020] and its static
treatment in Remora [Slepak et al. 2014] vary the rank of each argument and
lift a function over agreeing frames. Blade varies the *number* of arguments
and concatenates frames, so that arity determines output rank; APL's outer
product `∘.f` is the two-argument case. Variable-arity functions have been
typed in several ways [Dzeng and Haynes 1994; Strickland et al. 2009; Weirich
and Casinghino 2010], and Blade's implementation is plain monomorphization per
call site, as with variadic templates. The contribution is not the mechanism
but the link it carries: from arity to rank, and through identity to
symmetry.

### 7.5 Comoments and cumulants

Domino et al. [2018] compute moment and cumulant tensors of arbitrary order in
block-symmetric storage, with savings approaching r! and a parallel
implementation; it is the system closest to ours in what it computes, and the
head-to-head of §6.3 is owed to it. Pébay [2008] gives one-pass update
formulas for moments of arbitrary order. Sherman and Kolda [2020] avoid
forming the moment tensor at all when the goal is a low-rank decomposition,
which wins asymptotically at large n and r. Blade's claim is about mechanism:
the saving of the specialist follows, for any commutative kernel the user
writes, from a declaration and a call.

### 7.6 History of the mechanism

Blade began in 2019 as a C++ template library of nested-array iterators with
a pragma preprocessor, and was rewritten as a compiler in 2026. Reified loops
(`method_for`, `object_for`) and triangular iteration from declared input
symmetry are in its first commit (April 2019). Triangular iteration across
arguments for a kernel declared commutative in positions that receive the same
array was sketched in June 2019 and generated code in the release of
November 2019; that release is in the Software Heritage archive from May 2020.
This precedes Shi et al. (2021), StructTensor (2022) and SySTeC (2023–2025),
which arose independently, and the underlying observation is older than all
of them. Named index types, the `comm` syntax, the stated identity rule and
its refusal, flat packed storage and the proofs date from 2026, and we claim
no priority for them. [For double-blind submission the repository and archive
identifiers are withheld.]

## 8. Limitations

**Trusted declarations.** A `comm` claim is checked where the compiler can
decide it: by sign analysis and sampling for scalar kernels, structurally for
pack kernels. A claim on a kernel over rows that fits neither pattern is
accepted as given, and a false one silently yields a tensor in which some
reads return the value of a different cell. Soundness of packed storage is
therefore conditional on such declarations being true. Extending the sampling
check to short vectors would close the common cases.

**A deliberate subgroup.** The compiler grants neighbouring identity groups
within contiguous commutativity blocks (§4.5). A program that interleaves
identical operands, `(R, S, R)`, is not given the symmetry between its first
and third positions and is told to write `(R, R, S)`. This is proved sound and
proved not optimal: the interleaved spelling has a smaller orbit count that a
position-permuted layout would attain, and that layout is not built.

**Identity is conservative.** An alias shares its root's identity only under
a syntactic, module-wide proof that nothing writes either name, and a
definition the proof cannot see into counts as writing; a kernel that calls a
function from another module keeps the alias a second array (§3.3). Compound
identity groups and symmetric operands are also left dense, which matters for
the optimality count.

**Type soundness is proved for a fragment.** Progress, preservation and the
packed output as a theorem about typing derivations hold for the
structure-first calculus of rank-0 kernels over rank-1 operands at fixed
arity (§4.7). Kernel output dimensions, packs, multi-dimensional frames and
the rank deduction in general are implemented and tested, not proved.

**Floating point.** Packed results are symmetric by construction and are
evaluated at the sorted representative of each orbit; they are not bitwise
equal to a dense computation, and the default build reassociates licensed
folds (§4.6, §5).

**Dense only.** Blade has masks, compound and hashed index types, but no
general sparse tensor formats. Symmetric *sparse* computation is the province
of SySTeC and Finch.

**The definitional form costs more.** `comoment` as written centers r rows per
cell. The compiler hoists within a kernel, not yet across the cells of a nest,
and its single-consumer elisions decline recursive and pack callees, so the
standardized pipeline of §3.5 is the form to time (§6 times it). A
mechanized count of the difference exists for covariance: n centerings plus
C(n+1, 2) products when the stages are shared, against 3n² operations for the
fused dense nest, and the calculus program meets that count
(`two_node_translation`).

**Optimality is of counts in a black-box model.** Theorem 6 bounds kernel
evaluations at index tuples and stored cells, under generic data, for
programs correct on every kernel of the class. It says nothing about time;
a program that may inspect the kernel can beat the query bound (§4.7), and
bilinear algorithms and methods that never form the tensor can do better for
the kernels they apply to.

## 9. Conclusion

A comoment tensor is symmetric because of two facts about the program that
computes it, and both can be stated in the program: one as a law of the
kernel, one by passing the same array twice. Blade's index types, loop
objects, commutativity declarations and arity polymorphism are arranged so
that stating them is enough. One function then covers every order; the
compiler derives the triangular nest and the packed layout, reports what it
did, and refuses when the facts are absent or false; when the problem is
decomposed, the same kernel yields each sub-tensor with exactly the symmetry
its operands license; and the number of cells it computes is the least any
compiler in its position could.

## References

*To be verified entry by entry before submission.*

- Abbott, M., Altenkirch, T., Ghani, N., McBride, C. 2004. Constructing polymorphic programs with quotient types. MPC.
- Ahrens, W., Donenfeld, D., Kjolstad, F., Amarasinghe, S. 2023. Looplets: A language for structured coiteration. CGO.
- Ahrens, W., Collin, T. F., Patel, R., Deeds, K., Hong, C., Amarasinghe, S. 2025. Finch: Sparse and structured tensor programming with control flow. OOPSLA.
- Anandkumar, A., Ge, R., Hsu, D., Kakade, S. M., Telgarsky, M. 2014. Tensor decompositions for learning latent variable models. JMLR 15.
- Axelsson, E., Claessen, K., Dévai, G., et al. 2010. Feldspar: A domain specific language for digital signal processing algorithms. MEMOCODE.
- Ben-Or, M. 1983. Lower bounds for algebraic computation trees. STOC.
- Ballard, G., Kolda, T. G., Plantenga, T. 2011. Efficiently computing tensor eigenvalues on a GPU. IPDPS Workshops.
- Chamberlain, B. L., Choi, S.-E., Lewis, E. C., Lin, C., Snyder, L., Weathersby, W. D. 2000. ZPL: A machine independent programming language for parallel computers. IEEE Trans. Software Eng. 26(3).
- Chakravarty, M. M. T., Keller, G., Lee, S., McDonell, T. L., Grover, V. 2011. Accelerating Haskell array codes with multicore GPUs. DAMP.
- Claessen, K., Sheeran, M., Svensson, B. J. 2012. Expressive array constructs in an embedded GPU kernel programming language. DAMP.
- Domino, K., Gawron, P., Pawela, Ł. 2018. Efficient computation of higher-order cumulant tensors. SIAM J. Sci. Comput. 40(3).
- Donaldson, A. F., Miller, A. 2005. Automatic symmetry detection for model checking using computational group theory. FM.
- Dongarra, J. J., Du Croz, J., Hammarling, S., Duff, I. S. 1990. A set of level 3 basic linear algebra subprograms. ACM TOMS 16(1).
- Eberl, M. 2017. Lower bound on comparison-based sorting. Archive of Formal Proofs.
- Dzeng, H., Haynes, C. T. 1994. Type reconstruction for variable-arity procedures. LFP.
- Edwards, H. C., Trott, C. R., Sunderland, D. 2014. Kokkos: Enabling manycore performance portability through polymorphic memory access patterns. JPDC 74(12).
- Fergusson, J. R., Liguori, M., Shellard, E. P. S. 2010. General CMB and primordial bispectrum estimation. Phys. Rev. D 82.
- Ghorbani, M., Bauer, B., Grosser, T., Shaikhha, A. 2024. DASTAC: compressing structured tensor algebra. arXiv:2407.13726 [title to verify].
- Ghorbani, M., Huot, M., Hashemian, S., Shaikhha, A. 2023. Compiling structured tensor algebra. OOPSLA.
- Gibbons, J. 2017. APLicative programming with Naperian functors. ESOP.
- Hanjalić, K., Launder, B. E. 1972. A Reynolds stress model of turbulence and its application to thin shear flows. J. Fluid Mech. 52.
- Hirata, S. 2003. Tensor Contraction Engine: abstraction and automated parallel implementation of configuration-interaction, coupled-cluster, and many-body perturbation theories. J. Phys. Chem. A 107(46).
- Henriksen, T., Elsman, M. 2021. Towards size-dependent types for array programming. ARRAY.
- Hoyer, S., Hamman, J. 2017. xarray: N-D labeled arrays and datasets in Python. J. Open Research Software 5.
- Hui, R. K. W., Kromberg, M. J. 2020. APL since 1978. HOPL.
- Joyal, A. 1981. Une théorie combinatoire des séries formelles. Advances in Mathematics 42.
- Jondeau, E., Rockinger, M. 2006. Optimal portfolio allocation under higher moments. European Financial Management 12(1).
- Keller, G., Chakravarty, M. M. T., Leshchinskiy, R., Peyton Jones, S., Lippmeier, B. 2010. Regular, shape-polymorphic, parallel arrays in Haskell. ICFP.
- Liu, A., Bernstein, G. L., Chlipala, A., Ragan-Kelley, J. 2022. Verified tensor-program optimization via high-level scheduling rewrites. POPL.
- Manssur, L. R. U., Portugal, R., Svaiter, B. F. 2002. Group-theoretic approach for symbolic tensor manipulation. Int. J. Modern Physics C 13(7).
- Martín-García, J. M. 2008. xPerm: fast index canonicalization for tensor computer algebra. Computer Physics Communications 179(8).
- Martellini, L., Ziemann, V. 2010. Improved estimates of higher-order comoments and implications for portfolio selection. Review of Financial Studies 23(4).
- Mullin, L. M. R. 1988. A Mathematics of Arrays. PhD thesis, Syracuse University.
- Paszke, A., Johnson, D. D., Duvenaud, D., Vytiniotis, D., Radul, A., Johnson, M. J., Ragan-Kelley, J., Maclaurin, D. 2021. Getting to the point: Index sets and parallelism-preserving autodiff for pointful array programming. ICFP.
- Patel, R., Ahrens, W., Amarasinghe, S. 2025. SySTeC: A symmetric sparse tensor compiler. CGO.
- Pébay, P. 2008. Formulas for robust, one-pass parallel computation of covariances and arbitrary-order statistical moments. Sandia report SAND2008-6212.
- Ragan-Kelley, J., Barnes, C., Adams, A., Paris, S., Durand, F., Amarasinghe, S. 2013. Halide: A language and compiler for optimizing parallelism, locality, and recomputation in image processing pipelines. PLDI.
- Rasch, A., Schulze, R., Gorlatch, S. 2018. Multi-dimensional homomorphisms and their implementation in OpenCL. Int. J. Parallel Programming 46(1).
- Schatz, M. D., Low, T. M., van de Geijn, R. A., Kolda, T. G. 2014. Exploiting symmetry in tensors for high performance: Multiplication with symmetric tensors. SIAM J. Sci. Comput. 36(5).
- Sherman, S., Kolda, T. G. 2020. Estimating higher-order moments using symmetric tensor decomposition. SIAM J. Matrix Anal. Appl. 41(3).
- Shi, J., Chou, S., Kjolstad, F., Amarasinghe, S. 2021. An attempt to generate code for symmetric tensor computations. arXiv:2110.00186.
- Slepak, J., Shivers, O., Manolios, P. 2014. An array-oriented language with static rank polymorphism. ESOP.
- Solomonik, E., Demmel, J. 2021. Fast bilinear algorithms for symmetric tensor contractions. Comput. Methods Appl. Math. 21(1).
- Solomonik, E., Demmel, J., Hoefler, T. 2021. Communication lower bounds of bilinear algorithms for symmetric tensor contractions. SIAM J. Sci. Comput. 43(5).
- Solomonik, E., Matthews, D., Hammond, J., Demmel, J. 2013. Cyclops Tensor Framework: Reducing communication and eliminating load imbalance in massively parallel contractions. IPDPS.
- Solomonik, E., Matthews, D., Hammond, J. R., Stanton, J. F., Demmel, J. 2014. A massively parallel tensor contraction framework for coupled-cluster computations. JPDC 74(12).
- Steuwer, M., Fensch, C., Lindley, S., Dubach, C. 2015. Generating performance portable code using rewrite rules. ICFP.
- Strickland, T. S., Tobin-Hochstadt, S., Felleisen, M. 2009. Practical variable-arity polymorphism. ESOP.
- Trojahner, K., Grelck, C. 2009. Dependently typed array programs don't go wrong. J. Logic and Algebraic Programming 78(7).
- Veldhuizen, T. 1995. Expression templates. C++ Report 7(5).
- Weirich, S., Casinghino, C. 2010. Arity-generic datatype-generic programming. PLPV.
- Xi, H., Pfenning, F. 1998. Eliminating array bound checking through dependent types. PLDI.

## Appendix A. Mechanized statements

| Theorem | Rocq file | Statements |
|---|---|---|
| 1 Soundness of the licence | `BladeLowering.v` | `output_symmetry_soundness`; over compound frames `diagonal_group_law` |
| 2 Necessity | `BladeLowering.v`, `BladeCore.v`, `BladeCounting.v` | (a) `shared_units_insufficient`; (b) `input_symmetry_not_sufficient`; (c) `per_dim_swap_not_symmetry`; (d) `counting_general_C` |
| 3 The deduced group | `BladeDeduceExact.v` | `parfix_exact`, `deduced_group_largest`, `young_generated`; outside by design: `nonadjacent_symmetry_missed` |
| 4 Pack licence | `BladeDeduce.v` | `packfold_permutation`, `adjacent_transpositions_generate`, `signed_exchange_collapse` |
| 5 Storage | `BladeDMWF.v`, `BladeBinomial.v`, `BladeLex.v` | `enum_sound`, `enum_complete`, `enum_NoDup`, `storage_cardinality`, `lj_correct`, `enum_offset_respects_lex` |
| 6 Uniform optimality | `BladeOptimal.v` | `sym_nest_lower_bound`, `sym_nest_attained`, `uniform_optimality_sym`, `uniform_optimality_young_nat`; refutation `nongeneric_data_beats_bound` |
| 6, subgroups and signs | `BladeAttain.v` | `uniform_optimality_subgroup`, `surface_comm_optimal`, `antisym_lower_bound_r`, `antisym_attained_r`, `Kalt_sign_character` |
| 6, the model boundary | `BladeBoundary.v` | `orbit_bound_boundary`, `structured_kernel_beats_bound`, `sep_query_complexity`, `sep_storage_bound` |
| §4.5, the interleaved spelling | `BladeAttain.v` | `aba_orbit_count_forced`, `aba_beats_surface_rule`, `aba_gap_strict` |
| §4.3, the width dichotomy | `BladeDichotomyGeneral.v`, `BladeDichotomyTies.v` | `minimal_width_is_factorial`, `width2_iff_rank_le_2`; below the free cell `scheme322_sound` |
| §4.4, signs and actions | `BladeLayout.v`, `BladeLowering.v`, `BladeMaterialization.v`, `BladeStructuralIdentity.v` | `graded_invariant_compose`, `exactly_two_characters_descend`, `graded_output_soundness`, `output_antisymmetry_soundness`, `packed_read_equals_virtual`, `structural_zero_survives_elimination`; definitional: `transpose_key_involutive`, `alias_preserves_key`, `symmetry_licenses_transpose_erasure` |
| §4.6, float licences | `BladeLicence.v` | `typed_app_is_quotient_R`, `reassoc_licence`, `rounded_licence_coarse`, `assoc_congruence_collapses` |
| §4.7, the calculus | `BladeSurface.v`, `BladeObject.v`, `BladeTransport.v` | `progress`, `preservation`, `normalizes`, `eval_sound`, `typed_app_is_quotient`, `comm_license_needed`; `enum_is_quotient`, `isort_fibres_are_orbits`, `object_is_optimal`; the licence through combinators `group_of_laws`, `shapeCard_product_seam`, `zip_transport`, `pipe_transport`, `fuse_meet` |
| §8, shared stages | `BladeOrbitWork.v`, `BladeTransport.v` | `two_node_orbit_work`, `two_node_optimal`, `two_node_translation` |

Scope notes. Theorem 2 (a) to (c) are closed witnesses at r = 2. Theorem 3 is
for the unsigned, fixed-arity fragment: no sign-tracked or conjugate
symmetries, no calls, folds or conditionals in the kernel body. Theorem 6 is
for the full symmetric group on each identity group, generic data and opaque
cells. The converse of Theorem 1 (`license_exactness`, `BladeCompleteness.v`)
is mechanized as an equivalence only for the fully symmetric kernel. The
calculus of `BladeSurface.v` covers rank-0 kernels, rank-1 operands, fixed
arity, one `comm` flag and top-level names; `BladeTransport.v` covers rank-1
identity groups; the model boundary is proved at r = 2; the key algebra of
`BladeStructuralIdentity.v` is rank 2 with no mutation.

## Appendix B. The counting program

Table 3 is produced by the following program and its twin without the `comm`
clauses. The counters are captured mutable bindings incremented in the kernel
body.

```blade
type SeriesIdx = Idx<7>
type TimeIdx = Idx<5>
let A: Array<Float64 like SeriesIdx, TimeIdx> = [
    [1.0, 2.0, 3.0, 4.0, 5.0], [2.0, 1.0, 0.0, 1.0, 2.0], [0.5, 0.5, 1.5, 2.5, 3.5],
    [3.0, 1.0, 4.0, 1.0, 5.0], [9.0, 2.0, 6.0, 5.0, 3.0], [5.0, 8.0, 9.0, 7.0, 9.0],
    [3.0, 2.0, 3.0, 8.0, 4.0]]
let mut s2 = 0
let mut s3 = 0
let mut s4 = 0
let mut s5 = 0
let mut d3 = 0
let S2 = method_for(A, A) <@> lambda(a, b) where comm(a, b) -> { s2 = s2 + 1
    prodsum(a, b) } |> compute
let S3 = method_for(A, A, A) <@> lambda(a, b, c) where comm(a, b, c) -> { s3 = s3 + 1
    prodsum(a, b, c) } |> compute
let S4 = method_for(A, A, A, A) <@> lambda(a, b, c, d) where comm(a, b, c, d) -> { s4 = s4 + 1
    prodsum(a, b, c, d) } |> compute
let S5 = method_for(A, A, A, A, A) <@> lambda(a, b, c, d, e) where comm(a, b, c, d, e) -> { s5 = s5 + 1
    prodsum(a, b, c, d, e) } |> compute
let D3 = method_for(A, A, A) <@> lambda(a, b, c) -> { d3 = d3 + 1
    prodsum(a, b, c) } |> compute
let agree = S3(6, 0, 3) - D3(3, 6, 0)
// EXPECT: s2 = 28
// EXPECT: s3 = 84
// EXPECT: s4 = 210
// EXPECT: s5 = 462
// EXPECT: d3 = 343
// EXPECT: agree = 0.0
```

## Appendix C. How the listings are checked

This draft lives in `docs/research/`, which the repository's documentation
test lane covers. `blade test docs paper-1` assembles each `blade` block with
Listing 1 (and, within §3, with the kernel of §3.4), runs it through the full
pipeline, and compares the printed values with the `EXPECT` lines; blocks
marked as refusals must be rejected with the pinned diagnostic code, and the
pinned warning must fire. The lane runs the default build, so licensed folds
are reassociated and the pins are read to 1e-9 relative; the pinned values
are therefore association-invariant. For submission the listings move to a corpus
directory and are included into the LaTeX source from there.
