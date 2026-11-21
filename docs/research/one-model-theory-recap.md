# The one model: a recap of Blade's theory

Written 2026-10-21 as a self-contained statement of the theory behind Blade,
for a reader who has not seen the repository. Blade is an array-functional
language (ML-style syntax, F# compiler, C++17 and LLVM back ends) with a
Coq/Rocq proof tower of about 1,900 theorems, standard library only, no axioms.
Everything below marked *proved* has a Qed; everything marked *open* does not.
The reader is invited to attack it.

## 1. The object

Every Blade program is a function on an **index type**. An index type is a
finite set carrying two structures:

1. **A permutation-group action.** This is the symmetry. The dense box
   `[0, n)^r` is the free case with the trivial action. `SymIdx<r, n>` is the
   same box under the symmetric group `S_r` acting on coordinate positions;
   `AntisymIdx<r, n>` is the same action with a sign character (the strictly
   increasing tuples, no stored diagonal); `HermitianIdx` adds conjugation;
   orbit index types take an arbitrary declared permutation group; product
   index types carry the product (wreath) of their factors' groups.
2. **A well-founded order on the leading axis.** This is the direction of
   recursion. It is what makes a recursive array well defined.

An **array is literally a function on its index type**. `Array<T like I, J>`
is the function `I -> J -> T`; `A(i, j)` and `A(i)(j)` are the same term, and
partial application is a lower-rank view. This is called dimensional currying.
Index types are nominal: two index types of equal extent do not unify, so a
subscript either carries a proof of its index type (a literal, an iteration
variable of that type, a cast) or is checked at run time.

A **kernel** is a scalar function with declared metadata. `where comm(a, b)`
declares that the kernel's value is invariant under swapping argument positions
a and b; `anticomm` declares it changes sign; `omp` licenses a parallel
schedule; `reynolds(g)` manufactures an invariant kernel by group averaging
when the programmer cannot annotate one.

Functions over arrays are **arity polymorphic**: one definition typechecks and
compiles differently depending on how many array arguments it receives, with
output rank and output symmetry deduced from the argument count, the arguments'
identities, and the kernel's declared group. `method_for(A, B)` is the outer
product traversal; `method_for(zip(A, B))` is co-iteration. The traversal is a
first-class value (a loop object) that is stored, composed with combinators,
and applied to a kernel later; either side can be bound first.

## 2. Computation: the double metamorphism with feedback

The language has no imperative loop. Its founding slogan: *parallel structure
is a loop object; sequential structure is a recursive array; there is nothing
else.* The core, which the author holds to be the whole language, is one
scheme, the **Double Metamorphism With Feedback (DMWF)**:

- **The unfold.** Applying a kernel to a loop object is an unfold over the
  index type. For a symmetric index type the unfold is *structural with a
  dependent residual*: emitting coordinate `i` restructures the remaining
  space, since the next coordinate ranges over `[i, n)`. Mechanized as the
  recursion equation

  ```text
  enum (r+1) l u = flat_map (fun i -> map (cons i) (enum r i u)) (seq l (u - l))
  ```

  whose seed is the residual index type. *Proved*: it emits every canonical
  tuple exactly once (`enum_sound`, `enum_complete`, `enum_NoDup`); its length
  is `C(n + r - 1, r)`; its offset order is lexicographic; the index level does
  not depend on the kernel (structure exists before kernels); and the residual
  is constant exactly when the domain is rectilinear, i.e. a product of
  independent ranges (the dependence dichotomy).
- **The fold.** `reduce` and the reduction joins (`<&!>`, several accumulators
  in one traversal) fold back down. Fusion of map chains, one-pass multi-fold
  joins and kernel-invariant hoisting are the standard fold/unfold laws
  instantiated over index types.
- **The feedback.** A recursive array `let rec x = match x with | zero -> ...
  | prefix :: n -> prefix :: f(prefix, n)` is defined by structural induction
  on the leading axis, reads its own already-built prefix (reads past the
  prefix are implicitly zero), and compiles to one pre-allocated buffer. A
  `while` guard arm gives iterate-to-convergence over a budget extent.
  *Proved*: `let rec` is the unique solution of the zero-extended recurrence
  on the leading axis; its work is `N x orbits`, forced and attained; the
  quotient survives on the remaining axes.

Everything else is a special case: elementwise arithmetic is an unfold with a
trivial kernel, a comoment tensor is a fold of an unfold, time-stepping is the
feedback arm.

A naming caveat the reader should know: in Gibbons' usage a *metamorphism* is
a fold followed by an unfold. The DMWF is an unfold with a state-dependent
coalgebra, a fold, and a histomorphic feedback arm, so it is closer to a
hylomorphism with course-of-value feedback than to Gibbons' scheme. The name
predates the comparison.

## 3. Compilation is the quotient

The claim that turns the object into a compiler: **the packed enumeration of a
symmetric index type is the image of the dense box under the canonical-form
map (sorting), its fibres are exactly the orbits of the group, and the stored
array is a function on that transversal with canonicalizing access** (read `A`
at a dense tuple = sort the tuple, then look up). *Proved* for `S_r` and for
the Young (contiguous-block) subgroups, with the storage bijection from sorted
tuples to flat offsets (`lj_correct` / `unlj_correct`, the combinatorial number
system with a proof attached).

The compiler stores and iterates exactly one representative per orbit, so:

- storage is `C(n + r - 1, r)` cells for a rank-r symmetric result, not `n^r`;
- iteration is the canonical simplex, with no hand-written triangular index
  arithmetic anywhere in user code;
- layout, iteration order, OpenMP placement and BLAS routing are functions of
  the types and the kernel annotations. The design invariant is **"the fastest
  way is the only way"**: there is no schedule language by design.

*Proved* also: no product layout of packed symmetric storage is lossless
(`counting_general_C`), so dimensional currying is what preserves product
structure; the mixed-radix product bijection across distinct groups.

## 4. The licence: what is allowed to be quotiented

A kernel's `comm`/`anticomm` clause declares a group `H` on argument
positions. The operand tuple has a stabilizer `Stab`: which positions hold the
*same* array. The **licence group is `H ∩ Stab`**. The compiler quotients by
the licence group and by nothing else:

- a commutative kernel over two *different* arrays on the same index space
  gets no packed storage (*proved* not symmetric, `shared_units_insufficient`);
- symmetric input without `H` gets nothing (`input_symmetry_not_sufficient`);
- a per-dimension swap is not a symmetry (`per_dim_swap_not_symmetry`);
- the licence passes through every combinator: an outer product glues identity
  groups at the seam so the typed output of a product is a quotient of the
  product; zip, pipe and reduction joins carry it; a deduced swap-symmetry is
  itself a licence (*proved*, `BladeTransport`, `BladeSurface`).

The surface rule the compiler implements is that **identity groups are runs of
*neighbouring* identical operands**. The grant is the largest contiguous-block
Young subgroup generated by certified adjacent transpositions (*proved* exact,
`deduced_group_largest`). For a non-adjacent repeat, `method_for(A, B, A)`
with `comm(x, z)`, the rule stores the result dense in operand order and the
compiler names the adjacent spelling in a diagnostic. *Proved*: this rule is
sound and **not optimal**. The by-name quotient has fewer orbits; taking it in
source axis order needs a position-permuted layout, which is not built.

There is a **surface calculus** for the structure-first core (progress,
preservation, normalization) with a denotation into the object that every
reduction step preserves on the nose, and the quotient stated as a theorem
about typing derivations: the typed output of `L <@> f` holds one cell per
orbit, every dense cell is recovered by canonicalizing access, and nothing is
quotiented without `comm`.

Numerics are a parameter: typing, soundness and the quotient theorem are
proved relative to a declared equivalence `R` on scalars; reordering a fold
needs `R` to be a congruence, re-bracketing needs an identity too, and a
rounded fold's licence is necessarily coarse (`BladeLicence`).

## 5. Optimality

The claim that makes this a theory rather than a design: the quotient is not a
compression, it is the **minimum**. The model is an **oracle model**: a program
is a decision procedure that asks *questions*, kernel evaluations at index
tuples, of an oracle. Cells in different orbits are independently
*perturbable*: one can change the kernel's value on one orbit without changing
it on the others. Therefore any program correct for every kernel in the class
that asks fewer questions than the orbit count can be fooled, and the canonical
enumeration asks exactly the orbit count.

*Proved* (`uniform_optimality_sym`, `uniform_optimality_young_nat`, and the
`BladeAttain` / `BladeBoundary` files):

- no correct program asks fewer than `C(n + r - 1, r)` questions against the
  class of `S_r`-invariant kernels; the canonical enumeration attains it; with
  several identity groups the bound is the product;
- at proper subgroups: block licences composed with the binding's cut (the
  meet) are forced and attained;
- the sign character at every rank follows from adjacent anti-invariance
  alone;
- pipelines: the orbit work of a composition is attained by the composed
  program (instantiated at centered covariance: `n + C(n+1, 2)` against
  `3n^2`);
- recursive arrays: `N x orbits`, forced and attained;
- the bound holds on every free class.

**Scope, stated exactly.** The bound counts *kernel evaluations at index
tuples in a black-box model* and *cells stored*. It does not bound arithmetic.
Two ways past the query bound are known:

1. **Separable kernels** (`f(i, j) = g(i) h(j)`) beat the query bound with `n`
   questions but not the storage bound (*proved*; the converse is open).
2. **Bilinear algebra.** Solomonik and Demmel (CMAM 2021) compute a symmetric
   tensor contraction with fewer multiplications than distinct output cells,
   by a factor up to `(s+t+v)!/(s!t!v!)`, by forming products of sums and
   subtracting unwanted terms. Those products are not kernel evaluations at
   index tuples, so the orbit bound stands in its own model and not in theirs.
   Any statement of the bound must carry the model qualifier.

Measured: on distinct-name rows Blade attains the orbit count exactly; NumPy
computes the dense `n^r`; BLAS `dsyrk` writes `C(n+1, 2)` cells.

## 6. What the mechanization is and is not

- The tower: index universe and DMWF; arrows as coalgebras with Sym, Antisym,
  affine and compound instances; counting (binomial closed forms, the
  no-lossless-product-layout theorem, mixed radix); currying (the dependence
  boundary, the two maximal curryings); the object and the surface calculus;
  transport through combinators; attainment at subgroups and pipelines;
  induction (`let rec`); licence relative to a numeric relation; the scope
  boundary; the oracle model; an AD seam (renaming equivariance, Jacobian
  symmetry transfer); an ML seam (`S_2` partition of a self-tensor weight
  space, `Sym^k`/`Λ^k` sector counts, point-group registries with computed
  Frobenius–Schur indicators).
- Not mechanized: type soundness of the full language; the rank deduction;
  the `r!` ratio (only the exact cell counts); any time or cache claim; the
  `(r!)^d` product-symmetry speedup (withdrawn).

## 7. Open

- Compound and symmetric-operand identity groups are left dense (a design
  decision, matters for optimality).
- Presenting the by-name `(A, B, A)` quotient in source axis order needs a
  position-permuted layout.
- The converse of the separable-kernel boundary (does beating the query bound
  force separability?).
- Whether the bilinear mechanism and the separable mechanism are the same
  boundary seen twice.
- A clean recursion-schemes name for the DMWF, or a proof that it is a known
  scheme (conjugate hylomorphisms have not been checked).

## 8. Where it sits in the literature (2026-10-13 search)

No single ingredient is new; the combination and the mechanization are, as far
as a four-slice web search could find. Nearest neighbours: Dex (arrays as
functions on typed index sets, no symmetric index sets); Remora (rank
polymorphism by frame lifting, no symmetry deduction); Naperian functors and
Mullin's Mathematics of Arrays (array = function on its index; layout from the
denotation); quotient containers (Abbott, Altenkirch, Ghani, McBride 2004) and
species (Joyal; Yorgey) for "positions carry a group action", with no storage
or compiler; double-coset tensor canonicalization (Portugal; xPerm; Cadabra)
for the same `H ∩ Stab` computation used to decide expression equality rather
than to license a quotient; SySTeC, CTF and Schatz et al. for symmetry-driven
triangular iteration with symmetry outside the type system and no proof;
histomorphisms / dynamorphisms and `Data.Vector.constructN` for the feedback
arm; Ben-Or and Eberl's AFP sorting bound for the oracle model, without a group
action. Nothing found states the orbit bound in terms of a group action,
proves a compiler's packed layout to be the orbit quotient, or proves a storage
or iteration decision optimal in a proof assistant.

## 9. Questions for a second reader

1. Is "index type = finite G-set with a well-founded leading order, array =
   function on it, compilation = quotient by the licence group" a known
   framework under another name?
2. Is the oracle-model bound trivial once stated, or does the attainment half
   (one program meets it uniformly over the whole kernel class, through
   combinators and recursion) carry content?
3. Is the DMWF a conjugate hylomorphism, a dynamorphism, or something that
   deserves its own name?
4. Does the licence group `H ∩ Stab` have a cleaner statement as a double
   coset, and would that statement give the position-permuted layout for free?
5. What is the right single sentence that concedes Solomonik–Demmel and keeps
   the theorem?
