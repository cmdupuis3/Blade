# One Function, Every Comoment: Index Types, Loop Objects, and Arity Polymorphism in Blade

Christopher Dupuis — draft of 2026-10-05

> **Draft status.** First full draft, written against master `4fe4e1a4`. Plan:
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
> *Not yet real.* (1) §6.2 is a preliminary measurement from August on a scalar
> kernel, and §6.3 is a plan: no timing against an external baseline exists.
> (2) The compiler disagrees with §4.4 on interleaved operands: for
> `(R, S, R)` it regroups the loops as `(R, R, S)` and the output axes follow
> the regrouped order. §4.4 and §8 describe the designed rule; the compiler
> must be brought in line (or the rule restated) before submission. (3) The
> bibliography was assembled from memory and a search pass and needs a
> DOI-level check. (4) The count "about 540 statements" in §4.6 needs a
> recount, and the whole tower a rerun under one Rocq version. (5) Wording of
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
C(n+r−1, r) kernel calls. The implementation compiles to C++17. We report
exact structural counts and a preliminary same-compiler control in which the
realized saving reaches 91% of the finite-n ceiling at order 3.

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
   emitted nest makes exactly that many (§4.6).
5. An implementation that compiles to C++17, with exact structural counts and
   a preliminary measurement of the realized saving (§5, §6).
6. A mechanized artifact for the statements of §4, in Rocq, with no axioms and
   no admitted lemmas.

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
Reads take subscripts in any order and canonicalize them. The dense spelling
is no longer a type for this value: ascribing
`Array<Float64 like AssetIdx, AssetIdx>` to it is a type error whose message
names the deduced `SymIdx<2, AssetIdx>`.

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
value; `R` and `S` have the same type, and a copy of `R` under another name is
treated as a different array.

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
| `M <@> (G, G, G)`, `G` over `XIdx, YIdx, DayIdx` | `SymIdx<3, 4>` | 20 | 20 |
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
over the four grid points. It is not symmetric in x and in y separately, and
§4.3 shows that no per-dimension packed layout could store it.

```blade
type XIdx = Idx<2>
type YIdx = Idx<2>
let G: Array<Float64 like XIdx, YIdx, DayIdx> = [
    [[1.0, -2.0, 3.0, 0.5, -1.5, 2.0], [0.5, 0.8, -0.2, 0.4, 0.1, 0.6]],
    [[2.0, 1.0, 0.0, 1.0, 3.0, -1.0], [-1.0, 0.0, 1.5, 2.5, 0.5, 1.0]]]
let M = object_for(comoment)
let G3 = M <@> (G, G, G) |> compute        // a 2 x 2 grid of series: still 20 cells
let RRS = M <@> (R, R, S) |> compute       // symmetric in the first two slots: 10 x 4 cells
let g = G3(0, 1, 2)
let q = RRS(1, 0, 3)
let q2 = RRS(0, 1, 3)
// EXPECT: g = 0.308333333333333
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
commutative operation folded over the pack (§4.5). It does not act on a
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
tensor (`SymIdx<3, AssetIdx>`). At order 2 the nest is recognizable as a
symmetric rank-k update, and when a BLAS is configured the compiler routes it
to `dsyrk`; at order 3 no BLAS routine exists and the triangular nest is
emitted directly. The programmer wrote the same thing in both cases.

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
// ERROR: BL3999
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
    maximal runs of neighbouring positions that hold the same array and lie in one comm block of f
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
`SymIdx<3, 4>` over the four grid points, with 20 cells, and not a product of
two symmetric types over x and over y, which would have 4 · 4 = 16.

### 4.4 What the compiler grants

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
`(R, S, R)` has three groups of one.

> *Draft note.* The compiler at `4fe4e1a4` does not implement this rule for
> interleaved operands. For `(R, S, R)` under a fully commutative kernel it
> groups the two `R` positions, emits the nest in the order (R, R, S), and
> gives the output the type `SymIdx<2, AssetIdx>, AssetIdx`, so that the
> result's second index belongs to the *third* operand. The values are those
> of a correct tensor with permuted axes; nothing in the program or its
> diagnostics says the axes moved. Either the compiler changes to match this
> section, or this section changes to describe regrouping and its axis order.

The second layer is a decision about which programs to accept as fast, not an
approximation. A symmetry between non-adjacent positions is always available
by reordering the kernel's parameters so that the positions become
neighbours, and that spelling is the one whose packed block is a contiguous,
curried sub-array of the output. Admitting the interleaved spelling as well
would give a second route to the same orbits with a worse layout. Blade's
stated design invariant is that the fastest way to write a computation is the
only way that receives the optimization.

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

### 4.5 Packs

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

### 4.6 Storage and optimality

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

**Mechanization.** Theorems 1 to 6 are proved in Rocq 9.0.1 with the standard
library only, no axioms and no admitted lemmas. Appendix A maps each to its
source. They sit in a larger development of 1 362 statements, of which about
540 are in the files concerning this paper. Two things are *not* mechanized
and we do not claim them: the typing rules of Figure 3 have no formal
counterpart, so there is no type-soundness theorem; and the asymptotic ratio
nʳ / C(n+r−1, r) → r! is not stated in the development, which is one reason we
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

Three properties of this code are consequences of §4.6. The loop variables are
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

**Routes that follow from structure.** At order 2, a kernel of the form
`prodsum(a, b)` over two rows of one array makes the whole nest a symmetric
rank-k update, and when a BLAS is available the compiler emits a call to
`dsyrk` in its place. A parallelism licence on the kernel places an
OpenMP pragma on the outermost triangular level with dynamic scheduling, since
the work per outer iteration is skewed.

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
exactly? How much of it survives as time? How does the result compare with
what practitioners use? The first is answered below. For the second we have a
preliminary measurement on a simpler kernel. The third is planned and not yet
run.

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

The counts equal the bound of Theorem 6 exactly. Storage holds the same number
of data cells. For ranks above 2 the C++ back end also allocates a pointer
skeleton over the pool, one pointer per sorted prefix, which the paper's final
measurements will report in bytes.

### 6.2 Realized saving: a preliminary control

The cleanest control for the cost of triangular iteration is the compiler
against itself: one source file, compiled with the `comm` clause and with the
clause deleted. Table 4 reports such a pair, measured in August 2026 on an
earlier version of the compiler, for a scalar kernel mapped over a vector at
orders 3 and 4 (27 interleaved samples per arm, medians, non-power-of-two
extents).

| Shape | Cells, packed / dense | Packed | Dense | Ratio | Of available |
|---|---|---|---|---|---|
| r = 3, n = 301 | 4 590 551 / 27 270 901 | 8.46 ms | 45.59 ms | 5.39× | 91% |
| r = 4, n = 61 | 635 376 / 13 845 841 | 1.58 ms | 25.29 ms | 16.0× | 73% |

**Table 4.** Preliminary, to be re-measured: scalar kernel, C++ back end,
August 2026.

At order 3 the nest delivers 91% of the available 5.94×. At order 4 it
delivers 73% of 21.8×. Follow-up measurements against hand-written C++ twins
of the packed arm, with flat addressing and the same kernel, put the twins 7%
ahead at order 3 (7.67 ms) and 36% ahead at order 4 (0.98 ms), and attributed
most of the order-4 gap to allocating the pointer skeleton inside the timed
region, not to the loop nest. The emitted nest has since been changed to write
the flat pool directly, as in Figure 4. These numbers are for a kernel that
does almost no work per cell, the case in which loop overhead weighs most; the
comoment kernel does a reduction of length T per cell.

### 6.3 Planned measurements

The following have not been run. They are listed so that the draft is clear
about what its final claims will rest on.

1. *The comoment itself.* The standardized program of §3.5 with `prodsum`
   kernels at orders 2 to 4, n ∈ {61, 101}, T ≈ 2 000, serial and OpenMP, with
   and without `comm`.
2. *The ceiling.* A hand-written C nest with flat packed addressing, as the
   best a programmer could do for a fixed order.
3. *The specialist.* `Cumulants.jl` [Domino et al. 2018], which computes the
   same tensors in block-symmetric storage, and the moment routines of R's
   PerformanceAnalytics.
4. *The generalists.* NumPy `einsum` and `opt_einsum` (dense), `dsyrk` at
   order 2, and SySTeC [Patel et al. 2025] if its artifact can be made to run
   the kernels.
5. *Memory.* Peak working set against the dense arm.
6. *Economy.* Lines of source per order for each system.

All timings will use non-power-of-two extents, interleaved arms, and medians
over at least nine repetitions in three rounds, with outputs checksummed across
systems.

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
checking.

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
them, since those algorithms depend on the kernel being a product, and our
bound is for programs correct for every kernel in a law class.

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
within contiguous commutativity blocks (§4.4). A program that interleaves
identical operands, `(R, S, R)`, is not given the symmetry between its first
and third positions; the remedy is to write `(R, R, S)`.

**No soundness theorem for the type system.** The mechanized results concern
the semantic objects the types describe: enumerations, bijections, output
functions, query counts. The typing rules themselves and the rank deduction of
(App) are implemented and tested, not proved.

**Floating point.** Packed results are symmetric by construction, not bitwise
equal to the dense computation (§4.5).

**Dense only.** Blade has masks, compound and hashed index types, but no
general sparse tensor formats. Symmetric *sparse* computation is the province
of SySTeC and Finch.

**The definitional form costs more.** `comoment` as written centers r rows per
cell. The compiler hoists within a kernel, not yet across the cells of a nest,
so the standardized pipeline of §3.5 is the form to time. A mechanized
count of the difference exists for covariance: n centerings plus C(n+1, 2)
products when the stages are shared, against 3n² operations for the fused
dense nest.

**Compound frames lose their names.** The symmetric output over a
multi-dimensional frame is typed over an anonymous product extent
(`SymIdx<3, 4>` in Table 1) and is subscripted by flat grid position.

**Optimality is of counts.** Theorem 6 bounds kernel calls and cells under
generic data. It says nothing about time, and methods that exploit the
kernel's own algebra, or avoid forming the tensor, can do better for the
kernels they apply to.

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

- Ahrens, W., Donenfeld, D., Kjolstad, F., Amarasinghe, S. 2023. Looplets: A language for structured coiteration. CGO.
- Ahrens, W., Collin, T. F., Patel, R., Deeds, K., Hong, C., Amarasinghe, S. 2025. Finch: Sparse and structured tensor programming with control flow. OOPSLA.
- Anandkumar, A., Ge, R., Hsu, D., Kakade, S. M., Telgarsky, M. 2014. Tensor decompositions for learning latent variable models. JMLR 15.
- Axelsson, E., Claessen, K., Dévai, G., et al. 2010. Feldspar: A domain specific language for digital signal processing algorithms. MEMOCODE.
- Ballard, G., Kolda, T. G., Plantenga, T. 2011. Efficiently computing tensor eigenvalues on a GPU. IPDPS Workshops.
- Chakravarty, M. M. T., Keller, G., Lee, S., McDonell, T. L., Grover, V. 2011. Accelerating Haskell array codes with multicore GPUs. DAMP.
- Claessen, K., Sheeran, M., Svensson, B. J. 2012. Expressive array constructs in an embedded GPU kernel programming language. DAMP.
- Domino, K., Gawron, P., Pawela, Ł. 2018. Efficient computation of higher-order cumulant tensors. SIAM J. Sci. Comput. 40(3).
- Donaldson, A. F., Miller, A. 2005. Automatic symmetry detection for model checking using computational group theory. FM.
- Dongarra, J. J., Du Croz, J., Hammarling, S., Duff, I. S. 1990. A set of level 3 basic linear algebra subprograms. ACM TOMS 16(1).
- Dzeng, H., Haynes, C. T. 1994. Type reconstruction for variable-arity procedures. LFP.
- Edwards, H. C., Trott, C. R., Sunderland, D. 2014. Kokkos: Enabling manycore performance portability through polymorphic memory access patterns. JPDC 74(12).
- Fergusson, J. R., Liguori, M., Shellard, E. P. S. 2010. General CMB and primordial bispectrum estimation. Phys. Rev. D 82.
- Ghorbani, M., Huot, M., Hashemian, S., Shaikhha, A. 2023. Compiling structured tensor algebra. OOPSLA.
- Gibbons, J. 2017. APLicative programming with Naperian functors. ESOP.
- Hanjalić, K., Launder, B. E. 1972. A Reynolds stress model of turbulence and its application to thin shear flows. J. Fluid Mech. 52.
- Henriksen, T., Elsman, M. 2021. Towards size-dependent types for array programming. ARRAY.
- Hoyer, S., Hamman, J. 2017. xarray: N-D labeled arrays and datasets in Python. J. Open Research Software 5.
- Hui, R. K. W., Kromberg, M. J. 2020. APL since 1978. HOPL.
- Jondeau, E., Rockinger, M. 2006. Optimal portfolio allocation under higher moments. European Financial Management 12(1).
- Keller, G., Chakravarty, M. M. T., Leshchinskiy, R., Peyton Jones, S., Lippmeier, B. 2010. Regular, shape-polymorphic, parallel arrays in Haskell. ICFP.
- Martellini, L., Ziemann, V. 2010. Improved estimates of higher-order comoments and implications for portfolio selection. Review of Financial Studies 23(4).
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
| §8, shared stages | `BladeOrbitWork.v` | `two_node_orbit_work`, `two_node_optimal` |

Scope notes. Theorem 2 (a) to (c) are closed witnesses at r = 2. Theorem 3 is
for the unsigned, fixed-arity fragment: no sign-tracked or conjugate
symmetries, no calls, folds or conditionals in the kernel body. Theorem 6 is
for the full symmetric group on each identity group, generic data and opaque
cells. The converse of Theorem 1 (`license_exactness`, `BladeCompleteness.v`)
is mechanized as an equivalence only for the fully symmetric kernel.

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
pinned warning must fire. For submission the listings move to a corpus
directory and are included into the LaTeX source from there.
