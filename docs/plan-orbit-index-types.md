# Orbit index types — a flat generalization of `SymIdx`

**Status: the SURFACE TYPE is implemented (v1, 2026-08-02) — front end complete,
hard refusal at the storage boundary for a WRITTEN class. (A DEDUCED one is now
allocated, written and printed; see the status update below.)**
`OrbIdx<[(r₁,s₁), ..., (r_d,s_d)], n>`
parses (a dedicated sub-parser in `Parser.fs`, no new expression grammar),
normalizes per §7.2 at lowering, and lowers three ways: the empty class to the
plain `Idx<n>` record, a single surviving level to the *exact* `SymIdx<r,n>` /
`AntisymIdx<r,n>` record (so depth 1 is fully supported on day one through the
existing compact machinery), and depth ≥ 2 to a new `SymWreath` record carrying
the level list on the Extent slot (`IROrbitClass`, the `IRSparseKeys` pattern).
§4's cardinality fold is live through `Blade.OrbRank.cellCountChecked`, and the
level list is compared as type identity in `indexPairIncompatible`.

**Status update (2026-08-02): §9 step 4's DEDUCTION, the allocator, and the
WRITE-side traversal nest are implemented.** `deduceWreathTie` (`IR.fs`) is the
one rule: `k ≥ 2` argument positions holding the same identity (`sameIdentity`,
so bare variables only — §7.1's caveat), all contributing the same compact class
`L` over the same extent, all spanned by one declared `comm`/`anticomm` group,
not under `reynolds`, and no kernel T-dims → the output is `L ++ [(k,s)]`,
normalized through the same `orbitNormalForm` the surface type uses. So
`func(A,A)` with `A : SymIdx<2,n>` now *has* a type, and `let P = f(A,A) in
g(P,P)` composes to depth 3 because a wreath input contributes its own level
list. Reynolds earns **no** tie, per §8.1.

Storage follows: a sole depth ≥ 2 group over a compile-time extent allocates a
FLAT pool of exactly `OrbRank.cellCountChecked levels n` cells (`AllocWreath`),
and both backends fill it through the *verified* emitters rather than a second
hand-written nest — codegen instantiates
`orb_visit<orb_level<r,s>...>(n, visitor)` from
`src/cpp/orbit_wreath_utilities.hpp`, the interpreter walks
`OrbRank.visitStream`. Printing walks the pool in that same ascending-lex
canonical order. Reading a tied argument at a canonical sub-key is the existing
compact read at depth 1 and `orb_rank` into the input pool at depth ≥ 2.

**Status update (2026-08-02, second): the READ half is implemented** —
[plan-orbidx-decompaction.md](plan-orbidx-decompaction.md) §2 in both backends.
`W(i,j,k,l)` is a legal subscript at an ARBITRARY raw tuple (flat coordinates,
one per raw axis, exactly the rank-k `SymIdx` spelling): a mirrored tuple
returns the signed cell, a zero-set tuple returns 0, and both are *values*, not
errors. `decompact(W, 0)` produces the full dense tensor. Neither backend
re-derives the semantics — codegen instantiates the verified
`orb_read<T, orb_level<...>...>` and the interpreter drives
`OrbRank.orbReadPlan` (the shared core of the reference `orbRead`), so the
character is the per-level product by construction rather than by a second
implementation of §5's fold. The dispatch arm that had to *refuse* the subscript
form now handles it, with an explicit `Error` at every other arity — the
catch-all vacuity §9 step 6 warns about is closed by handling, not by a guard.

What is **still** not implemented: an OrbIdx **annotation** (no producer exists
for a written class, so a binding that names one is refused at the let/signature
gate); `reduce`/`prodsum` over the pool, which needs orbit multiplicities and
now steers to `decompact(W, 0)`; PARTIAL (per-level) decompaction — the
decompaction plan's §3 lattice; a partial/sub-array subscript, which has no
residual class to be typed at; `transpose`; and provider I/O. An un-tied wreath
INPUT (a unary map, a non-comm binary) is refused too: only the segment-peeled
nest walks a wreath pool and it is driven by the output class.
§9's steps 0–3 and 5–8 remain undone. The scaffolding of §9 step 1
(`PositionGroup`, `placementOfOrbit`) is still unwired — `SymmetryClass` grew
the fifth case instead, deliberately, so that every exhaustive match in the
compiler had to decide.

**Soundness caveat the read path made visible — now CLOSED (2026-08-02).**
`deduceWreathTie` used to append a level from `comm` alone, without checking
that the kernel commutes with the INNER class's mirror. For an inner `-` level
that check is per-argument oddness — `h(-p, q) = -h(p, q)` — the wreath
analogue of BL4015's inheritance gate. `method_for(B,B) <@> (p*q)` over
antisymmetric `B` satisfies it; `method_for(B,B) <@> (p+q)` does not, yet both
deduced `[(2,-),(2,+)]`. While only canonical cells were reachable the
difference was unobservable; a mirrored read or a decompaction of the additive
one returned the class's dense image, which is not the kernel's pointwise
value. The gate is now condition 6 INSIDE `deduceWreathTie` (so all four call
sites — deduction, typecheck, codegen, interpreter — share one answer): when
the inner level list carries any `-`, the tie requires the kernel recorded
provably sign-odd (`KspOdd`) in every tied argument, else refuses with BL4015
(`WreathTieKernelNotOdd`). The per-argument summary is
`Deduce.deduceSignParities`, computed once at the typecheck apply seam and
recorded through `TypedLambdaInfo.SignParities` → `IRCallable.SignParities` so
the downstream seams judge from the same values. The trust-model decision,
made deliberately AGAINST §8.1's depth-1 status quo: provably-even AND
unknown parity both refuse. Depth-1 trusts a *declared* `comm` it cannot
prove; per-argument oddness is a claim no clause declares (`where comm(p, q)`
says `h(p,q) = h(q,p)`, nothing about `h(-p,q)`), so there is no user word to
trust, and the precedent is BL4015's inheritance gate, which refuses UNKNOWN
and steers to decompact-first. Note `p + q` is the UNKNOWN case, not the even
one (`h(-p,q) = -p+q` is neither `±h(p,q)`); corpus 201 pins that refusal,
220 pins a provably-even kernel (`p*p + q*q`, the `CommContradictsBody`
analog one level up), and 213/214 keep the sound multiplicative kernel green.
All-`+` inner levels need no certificate (their mirror is the identity), so
every previously sound tie is untouched.

**Proof status, scoped.** The group theory is machine-checked, but not uniformly.
[BladeWreath.v](../proofs/BladeWreath.v) proves the *invariance* — that the
block-wise product action and the full wreath action both fix the value — at
**general rank `r`** (`block_product_symmetry_soundness`, :238-244;
`wreath_full_invariance`, :346-355). The exact **orders** `(r!)²` and `2(r!)²` are
pinned by complete enumeration at `r = 2` (§§7, 9; extents 2 and 6) and at
`r = 3` (§10; all 720 permutations of `S_6` against all `3⁶` tuples, with the
stabilizer coming out at exactly 72 for a repeated commutative argument and
exactly 36 for distinct inputs). Beyond `r = 3` the enumeration leaves Coq's
reach, and `r = 4`/`r = 5` are confirmed only by external exhaustive
enumeration — evidence, not proof; **general-`r` exactness is open**, as the
file's own comments say (:218-224, :1220-1257).
[BladeLayout.v](../proofs/BladeLayout.v)'s
`canonicalization_preserves_value_up_to_character` (:1744-1751) is the
**r = 2, single-transposition** instance — one swap inside one two-factor
product; §5's fold is that argument iterated over sorts of `rᵢ` sub-blocks, and
the iterated statement is not mechanized. Neither is §4's iterated binomial — extending
[BladeBinomial.v](../proofs/BladeBinomial.v) is future work, not done.

**Reproducing the numbers.** The cardinalities (§4), character counts (§6) and
group orders (§7, §7.1) are enumerated by
[`proofs/OrbitEnum.fsx`](../proofs/OrbitEnum.fsx), run as
`dotnet fsi proofs/OrbitEnum.fsx` — dependency-free brute force over the wreath
action, checking each closed form against a direct orbit count (add `--stress` for
§7.1's full axis-permutation sweeps). Timings are measurements, labelled as such.

## 1. The problem

**Deduction produces types the type system cannot write down.** That is the
primary motivation; the missing spellings are secondary.

Take `A : Array<Float like SymIdx<2,n>>` and a kernel declared `where comm`. Blade
already deduces everything it needs about `func(A, A)`: the same identity in both
argument positions and a commutative kernel, so the two symmetric blocks may be
exchanged, and the licensed group on the output's **four raw axes** is `S_2 ≀ S_2`.
What Blade cannot do is *name that result*. `SymIdx<2,n>` ×2 describes the blocks
and silently drops the tie; `SymIdx<4,n>` claims a symmetry that is not there.
There is no third option, so the deduced fact lives only inside one expression: it
cannot cross a `let`, a signature, or a return annotation, because there is
nothing to ascribe.

The second gap is **mixed character**: every class Blade can write today is
uniformly symmetric or uniformly antisymmetric, and "antisymmetric within a block,
symmetric between blocks" — the Riemann shape — has no spelling at all.

The internal shape for both is half-present. `Types.fs` pairs a `PositionGroup`
(`PgTrivial | PgFullSym of rank | PgProduct of ranks | PgOpaque of tag`) with a
`SymmetryClass` character (`Types.fs:84-98`) — exactly the (G, χ) split this needs
— but **that machinery is not wired in** and says so: "NOT WIRED IN: nothing
constructs or consumes this yet" (`Types.fs:50-52`), and `placementOfOrbit`
(`Types.fs:93-98`) has no callers. The live path is `placementClassOf`
(`Types.fs:43-46`), which decides placement from `SymmetryClass` alone — a nullary
four-case enum (`Types.fs:13-17`) — and even that has exactly **one live
consumer**: `bufferGroupCardinality` (`IR.fs:2467`). Its other caller,
`placementOf` (`IR.fs:425`, call at :437-438), has no callers of its own, and the
allocator never asks — `hasRealSymmetry` picks the routine (`IR.fs:2827`) and
`allocRoutineFor` (`IR.fs:2648`) receives a hard-coded
`PlaceCombinatorial SymAntisymmetric` at its single call site (`IR.fs:2833`).
So `PgOpaque` → `PlaceTabulated` is the **designed** fallback
for a tie, in scaffolding that does not run; it is not live routing. Today a tie
has no route at all, because it has no type.

Nesting is the usual proposed answer and does not work either.
`docs/formalism.md` §3.4 (:225; the nested/mixed prose at :250-254) sketches
*nested/mixed* symmetry as something users
would compose via `Sym<I,I>` / `Antisym<I,I>`, but that is prose:
`parseSymIdxBase` (`Parser.fs:842-847`) accepts an `Idx`/`IrrepsIdx` base or an
extent expression and nothing else, so `SymIdx<2, SymIdx<2,n>>` does not parse and
never has. The observation behind this proposal is that the nesting is
**unnecessary**: every group Blade can license is a generalized Young/wreath
subgroup, and those are describable by a flat list of integers and signs.

## 2. The type

A class is a **flat list of levels**, outermost-last, over one extent:

```
OrbIdx<[(r₁,s₁), (r₂,s₂), ..., (r_d,s_d)], n>
```

Level `i` contributes a symmetric group `S_{r_i}` with character
`s_i ∈ {+,-}`; the class's group is the **iterated wreath product**

```
G = S_{r₁} ≀ S_{r₂} ≀ ... ≀ S_{r_d},     |G₀| = 1,   |Gᵢ| = |Gᵢ₋₁|^{rᵢ} · rᵢ!,   |G| = |G_d|
```

acting on `∏ᵢ r_i` axes. An array type juxtaposes classes exactly as today
(`Array<Float like OrbIdx<[(2,+)],M>, OrbIdx<[(2,+)],K>>`), and the total group
is the product across classes — `docs/formalism.md` §12.4's doctrine that
multiplicative speedups come from *distinct identity groups* (:1046), unchanged.

Sugar for depth 1: `OrbIdx<r,n,s>` ≡ `OrbIdx<[(r,s)],n>`. Whether deeper sugar
is worth its parsing weight is a §9.3 surface-syntax decision, not settled here.

**Why a list, not a fixed pair.** Composition depth is unbounded: `f(A,A)` is
two levels, `let P = f(A,A) in P [*] P` is three, and each further commutative
combination of a *named* object with itself adds one (§7.1 explains why the
name matters). A fixed `(r,s,k,t)` quadruple caps the design at depth 2 and
cannot type `P [*] P` (enumerated group order 128 = S₂≀S₂≀S₂; see §7.1).
Notation: `[*]` is the outer/tensor combination — ranks add, and it is the
operation that can earn a tie. Plain `*` is **elementwise**: the operands
share axes, exchanging them permutes nothing, and the result keeps the
operands' class regardless of identity — comm on an elementwise op is an
iteration/fusion license only, never a level. The list is still **flat** — index types never
appear as arguments to index types, so this is not recursive typing; the
recursion lives in the *data* (a list), where it costs nothing.

The level list is also self-validating: a tie is only meaningful between
identical sub-blocks, and a list position necessarily describes identical
sub-blocks, so an ill-formed tie is unrepresentable rather than checked.

## 3. Every ±1 class is a special case

| where it comes from | `OrbIdx` form | group |
|---|---|---|
| `Idx<n>` | `OrbIdx<[],n>` (normal form — `[(1,+)]` normalizes to it, §7.2) | `1` |
| `SymIdx<r,n>` | `OrbIdx<[(r,+)],n>` | `S_r` |
| `AntisymIdx<r,n>` | `OrbIdx<[(r,-)],n>` | `S_r`, χ = sgn |
| product form | juxtaposition | `∏_j S_{r_j}` (= `PgProduct`) |
| *deduced, unwritable*: `func(A,A)`, `A : SymIdx<2,n>`, `comm` | `OrbIdx<[(2,+),(2,+)],n>` | `S_2 ≀ S_2` |
| *deduced, unwritable*: `let P = f(A,A) in P [*] P` (§7.1) | `OrbIdx<[(2,+),(2,+),(2,+)],n>` | `S_2 ≀ S_2 ≀ S_2`, depth 3 |
| `RiemannIdx<n>` — spec prose, never implemented | `OrbIdx<[(2,-),(2,+)],n>` | `S_2 ≀ S_2`, χ mixed |

The two rows marked *deduced* are §1's point: not types anyone can write, but
types the compiler *computes* and then has nowhere to put. The depth-2
all-symmetric class is a deduction outcome of `func(A,A)`, not a declared form.
(`HermitianIdx` is deliberately absent: its character is complex conjugation,
outside `Hom(G,±1)`, so §6 does not reach it and `SymHermitian` is unaffected.)

**`RiemannIdx`, reassessed.** It has never been implemented — spec-level prose in
`docs/formalism.md` §3.4, marked *Speculative* in `docs/features.md:78`. It is
warranted **iff** three things matter together: raw-axis addressing (indexing
`R[i,j,k,l]`, not a hand-rolled pair encoding), per-level sign accounting (the
inner `s = -` contributing its sign on non-canonical access and zeroing the
`i = j` diagonal), and deducibility (an `OrbIdx` result of that shape should *be*
this type, not merely resemble it). If only the storage cardinality is wanted,
manual encodings already give it. It stays here because it is the sharpest
mixed-character case available: `s = -, t = +`.

The `func(A,A)` row and the `RiemannIdx` row carry the **same group**, `S_2 ≀ S_2`,
and differ only in character — §6's claim in miniature: the group is fixed by the
ranks, and the sign list selects among the `2^d` characters it admits.

## 4. Cardinality is a closed form

Fold the level list, starting from the extent:

```
M₀ = n
Mᵢ = C(Mᵢ₋₁ + rᵢ - 1, rᵢ)   if sᵢ = +
     C(Mᵢ₋₁,           rᵢ)   if sᵢ = -
```

The class has `M_d` cells — an iterated binomial, no runtime table, one
`C(·,·)` per level. Worked cases: `OrbIdx<[(2,-),(2,+)],4>` (the Riemann shape)
folds `4 → C(4,2) = 6 → C(7,2) = 21`, which is the 21 `docs/formalism.md` §3.4
states for `n = 4`. The depth-2 all-symmetric class that `func(A,A)` deduces folds
`n → S = n(n+1)/2 → S(S+1)/2`. Depth 3 at `n = 4`: 1540 cells against 65536 dense,
a 42.6× reduction. `proofs/OrbitEnum.fsx` checks the fold against direct orbit
counting on these and the other configurations this doc cites, including both
antisymmetric-level cases.

This is the payoff: a deduced tie moves from `PgOpaque` / `PlaceTabulated` — §1's
designed fallback, and today not even that — to a closed-form placement. A formal
one-level extension of [BladeBinomial.v](../proofs/BladeBinomial.v)'s `C(n+r−1,r)`
by the same hockey-stick machinery looks routine, but it does not exist: there is
no iterated-binomial theorem in `proofs/` today.

**Cell reduction is footprint, not speed.** Measured on this project, a packed
symmetric layout ran **2.14× slower** than the dense compute it replaced — the
combinadic rank is arithmetic on the hot path and gives up the contiguity a dense
loop gets free — while loop **reordering**, which costs nothing, beat the symmetry
rewrite outright. Treat `M_d` as an allocation budget; any *time* claim needs its
own measurement, and never take one at a power-of-two extent, where the alignment
artifact is worth roughly 7× on its own.

## 5. Canonicalization — one canonical orbit, two flat sorts

Canonicalize level by level, innermost first — a fold of sorts, one per level:

```
canonical = sortₙ ∘ ... ∘ sort₂ ∘ sort₁
```

At level `i`, sort the `rᵢ` sub-blocks against each other by their already-
canonical keys; if `sᵢ = -`, accumulate the permutation sign. Cost is a sort
per level, `O(∏rᵢ · log)` overall. The value character is the product of the
per-level signs. `canonicalization_preserves_value_up_to_character` in
BladeLayout.v (:1744-1751) is the r = 2, single-transposition instance of one
level; the fold is that argument iterated, and the iterated form is not
mechanized (see the status note above).

**Zero set**, uniformly: a tuple whose stabilizer contains an element with
χ = −1 stores zero. At any level, `sᵢ = -` kills tuples with two equal sub-blocks
at that level. The antisymmetric zero-diagonal is the depth-1 instance and the only
instance live today; the general rule is *stated* — in the unwired scaffolding's
commentary at `Types.fs:79-83` — but nothing implements it above depth 1.

## 6. Why one sign per level is the complete parameterization

Not a design convenience, but not a free-standing theorem either: the H/Stab
framework is *defined over* a ±1 grading — a grant is invariance or
anti-invariance and nothing else (`grant_forms_exhausted`,
`BladeLayout.v:378-389`, is that grading's two-case restatement, not an
exhaustiveness proof over all possible characters; §3's `HermitianIdx` note is
the reminder that conjugation lives outside it). Within that grading every
licensed move carries a ±1 character and the relevant object is `Hom(G, ±1)`.
For an iterated wreath that group has order `2^d` in the number of nontrivial
levels — enumerated by computing `|G^ab|` (`proofs/OrbitEnum.fsx`):

| class | \|G\| | #characters |
|---|---|---|
| `S_2` | 2 | 2 |
| `S_2 ≀ S_2` | 8 | 4 |
| `S_2 ≀ S_2 ≀ S_2` | 128 | 8 |

(and `Hom(S_r ≀ S_k, ±1)` = 4 for all `r,k ≤ 3` with `r,k ≥ 2`, degenerating to
2 or 1 exactly when `r = 1` or `k = 1`).

So the sign list expresses **every** character and **no** spurious ones, at
every depth. One bit per level is not an approximation of the symmetry lattice;
it is the whole of it.

## 7. The worked example

`A, B` symmetric 2-D; `C, D` non-symmetric; `func` commutative:

| call | output index type | group | order |
|---|---|---|---|
| `func(C,D)` | `OrbIdx<[],n>` ×4 | `1` | 1 |
| `func(C,A)` | `OrbIdx<[],n>` ×2, `OrbIdx<[(2,+)],n>` | `S_2` | 2 |
| `func(A,C)` | `OrbIdx<[(2,+)],n>`, `OrbIdx<[],n>` ×2 | `S_2` | 2 |
| `func(A,B)` | `OrbIdx<[(2,+)],n>` ×2 | `S_2 × S_2` | 4 |
| `func(A,A)` | `OrbIdx<[(2,+),(2,+)],n>` | `S_2 ≀ S_2` | 8 |

The last two are BladeWreath.v's dichotomy: distinct symmetric inputs get the
block-wise product for *any* kernel, and only a **repeated** argument plus
commutativity earns the tie. The invariance in both cases is proved at general `r`
(:238-244, :346-355); the orders `(r!)²` and `2(r!)²` that the order column above
instantiates at `r = 2` are enumerated there at `r = 2` and `r = 3`, and
classical beyond (:218-224, :1220-1257). The `r = 3` sweep also isolates *which*
hypothesis buys the tie: with the argument still repeated but the kernel
noncommutative, the stabilizer drops from 72 back to exactly the 36-element
block group (`noncomm_r3_loses_the_swap`), so the `S_2` factor of the wreath
product is the commutativity license and nothing else.

`func(C,A)` vs `func(A,C)` is the case the type system should *not* conflate
with symmetry. Same group, different axis order; commutativity makes them the
same value, so argument order is a **gauge freedom** — a licensed layout
choice, not an output symmetry. It belongs to the canonical-orbit selection of
BladeLayout.v §5, not in the index type. Keeping it out is what stops
`func(A,B)` from being wrongly typed as tied.

### 7.1 Composite stress cases

`A,B,C,D` symmetric 2-D over extent `n`; `f,g,h` distinct commutative kernels.
Rows 1-2 were enumerated over all `8!` axis permutations at `n = 3`
(`OrbitEnum.fsx --stress`); row 3's group is confirmed by generators — `16!` is
out of enumeration's reach.

| expression | rank | index type | \|G\| |
|---|---|---|---|
| `h(f(A,A), g(B,B))` | 8 | `OrbIdx<[(2,+),(2,+)],n>` ×2, untied | 64 |
| `let P = f(A,A) in P [*] P` | 8 | `OrbIdx<[(2,+),(2,+),(2,+)],n>` | 128 |
| `f(h(A,B),g(C,D)) * h(C,D) * g(A,B)` | 16 | `OrbIdx<[(2,+)],n>` ×8, untied | 256 |

The first two are the design's whole point in one comparison: **identical rank,
identical building block, groups differing by a factor of two** — and the
difference is entirely whether the two operands are the *same object*. `h`
combines two *different* objects, so its blocks stay untied (64 = 8·8); `[*]`
combines `P = f(A,A)` with *itself*, so a third level is earned (128). Shape
cannot distinguish them; only identity can.

The third shows the converse: the pairings are deliberately crossed —
`h(A,B)` and `g(C,D)` inside, `h(C,D)` and `g(A,B)` outside — so all four
sub-objects are distinct, every tie is refused, and the group collapses to the
inputs' own symmetry `(S_2)^8`. Confirmed by generators: all 8 within-block
swaps hold, and an exhaustive sweep of all 112 cross-block transpositions finds
none that do. (Transpositions alone do not pin `|G|` exactly — a composite
permutation could in principle survive — so the 256 is the *licensed* group
read off the type, with every single-swap escape ruled out.)

Two cautions this exercise surfaced:

- **Degeneracy is real here too.** With `f` = scalar multiplication,
  `f(A,A) [*] f(A,A)` collapses to `A^⊗4`, whose actual stabilizer is `S_2 ≀ S_4` of order **384**,
  not 128. The type system must keep claiming 128: it cannot see the collapse,
  and 128 ⊂ 384 means the conservative answer stays sound. Measured 128 for
  three independent generic kernel choices and 384 only for the mult/mult one.
- **The tie depends on identity resolution, which is coarser than it looks.**
  `sameIdentity` (`Types.fs:113-125`) compares `ArrayIdentity` values, and an
  argument acquires a stable one only when it is a **bare variable**: every apply
  site builds identities as `TExprVar name → AIDVariable name`, everything else →
  `AIDLiteral (FreshId())` (`TypeCheck.fs:5872-5873`, repeated at :8463,
  :8505-8506, :8529, :8959). A fresh literal id never equals another, so identity
  comparison can only succeed on repeated *names*. (The one comparison in the
  tree today, `TypeCheck.fs:6212-6213`, sits in the confirm-and-pin *suggestion*
  path, gated at `:6208` to kernels with **no** declared groups — under
  `where comm` nothing compares identities at all yet; §9.4's deduction rule
  would be the first consumer.) So `let P = f(A,A) in P [*] P` earns the third level
  (the `let` binds `AIDVariable "P"`, `TypeCheck.fs:7854`), and `f(A,A) [*] f(A,A)`
  written inline **never** does, however obviously identical the operands are:
  there is no CSE. The inline form's honest type is the untied
  `OrbIdx<[(2,+),(2,+)],n>` ×2 at order 64 — sound, and a factor of two short.
  Closing the gap needs a CSE pass over pure expressions; until then "bind it to a
  name" is the user-facing rule and should be documented, not discovered.

### 7.2 How deep can the AST drive this?

Deduction appends a level every time a commutative kernel is applied to a
repeated identity, so in principle an AST can keep building. In practice depth
is **self-limiting, and the stack is never what limits it.**

**Depth is logarithmic in rank.** A level with `r = 1` is the trivial group and
a no-op (`M` unchanged), so it must be normalized away; every surviving level
has `r_i ≥ 2`, hence

```
rank = ∏ᵢ rᵢ ≥ 2^depth      ⇒      depth ≤ log₂(rank)
```

Each level at least *doubles* the axis count. Rank 64 caps depth at 6; rank
1024 at 10. That normalization is the load-bearing safeguard: without it a
naive AST could append trivial levels forever at rank 1 (measured: 1000
`(1,+)` levels leave the cell count unchanged).

**The binding constraint is int64 overflow in cell/offset arithmetic**, which
bites well before rank does. Last depth whose cell count fits in `int64`, all
levels `r = 2`:

| extent `n` | 2 | 4 | 8 | 16 | 64 | 100 | 360 | 1000 |
|---|---|---|---|---|---|---|---|---|
| max depth | 7 | 5 | 4 | 4 | 3 | 3 | 3 | 2 |

For realistic extents (`n ≥ 100`) the ceiling is **depth 3**. At `n = 360` a
depth-3 class is already 2.2×10¹⁸ cells and depth 4 overflows.

**Costs are benign.** The canonicalizer is a fold over the level list and can be
written fully iteratively — zero recursion in depth. Even a naive recursive one
recurses once per *level*, i.e. ≤ 10. Measured cost per tuple follows §5's
`O(∏rᵢ · log)`: 1.1 µs at depth 1 through 4.6 ms at depth 12 / rank 4096 —
≈4180× over a 2048× rank increase, the ~2× excess being the log factor. Depth
enters only through rank, never on its own. (These timings are ad-hoc
measurements — the one set of numbers in this doc `OrbitEnum.fsx` does not
reproduce.)

**Width, not depth, is what scales with program size.** The number of *classes*
in a type grows linearly with the number of distinct sub-objects — §7.1's third
expression already has 8 untied classes — while depth per class stays
logarithmic. Sizing decisions should budget for wide types, not deep ones.

**Implementation consequence.** The failure mode to guard is *not* stack
exhaustion but silent `int64` wraparound in offset arithmetic, which yields wrong
addresses rather than a crash. This is new work, not a tightening: `binomI64`
(`IR.fs:2438-2447`) is plain `int64` — its mid-loop division is exact, but nothing
checks the multiply — and there is no checked arithmetic anywhere on the placement
or lowering path today. Cell counts and offsets must use checked arithmetic and
emit a diagnostic at overflow; a depth cap (say 6) is a cheap secondary guard, but
the overflow check is the one that matters.

## 8. What this deliberately does not cover

Only permutation symmetries with ±1 characters. **Linear relations among
distinct orbits are out of scope** — the cyclic Bianchi identity is the sharp
example: physical Riemann in 4-D has 20 independent components, `OrbIdx` gives
21, and the missing relation is not a permutation with a sign. That is not an
oversight in the encoding: H/Stab grants carry only ±1 characters (the grading
§6 rests on), a linear relation among distinct orbits is not such a grant, so
no index type built on H/Stab can express it. `PgOpaque` /
`unsafe indextype` remains the escape hatch, now for a genuinely smaller set of
cases.

**Inexpressible residual groups diagnose, they do not silently degrade.**
Deduction can *arrive at* a group outside the wreath family even though no
type can spell one: a non-odd map over a `-` level leaves the value invariant
under exactly `ker χ` — `A_r` for `[(r,-)]`, an order-4 kernel for the
Riemann class (`proofs/OrbitDeduceModel.fsx` T7/T8). The deduced claim must
round down to the largest expressible subgroup (sound), and the compiler
should say so: a not-implemented diagnostic in the BL4015 family — "residual
group G is not representable; claiming H ⊂ G" — rather than a silent loss.
On whether to close the family instead: the closure under `ker χ` is
bounded — exactly one new flavor, *parity-split* ("chiral") classes, a
symmetric pool plus one parity bit on all-distinct tuples — so it is not
"implementing all of group theory". But it is a genuinely new storage family
(its own canonical-form tiebreak, placement, zero-set), and BL4015's
architecture finding applies to it too: iteration follows the *input*
record, so a chiral *output* class could not be filled from a compact
input's loop nest anyway. Diagnose-and-round-down is the right v1; chiral
classes stay here as the named future extension if the diagnostic turns out
to fire often.

Also unchanged: this describes **storage** groups. Iteration-order selection is
the separate canonical-orbit machinery, and `OrbIdx` is its input.

### 8.1 What it trusts rather than checks

The wreath tie is **declared, not verified**, and inherits `comm`'s existing trust
model wholesale. `comm` is taken on the user's word: the clause is read at
`TypeCheck.fs:6070` (`lambdaInfo.CommGroups`) and becomes the storage and
iteration groups at `:6079-6080`, and the
only thing between a false `comm` and wrong storage is `CommContradictsBody`
(`TypeCheck.fs:6192-6198`), which fires when the kernel is *provably* anti-invariant
in a declared pair. Provably-neither passes silently. A wreath tie needs that check
extended **per level** — a level-`i` sign of `+` that the body provably negates is
the same contradiction one level up. (Done, 2026-08-02, as `deduceWreathTie`'s
condition 6 — see the status block at the top. One deliberate divergence from
this section's trust model: the per-level check refuses UNKNOWN parity too,
not just the provable contradiction, because per-argument oddness — unlike the
swap — is a claim no clause declares, so there is nothing to take on the
user's word; the precedent is BL4015's inheritance gate.)

One case should be closed rather than extended. Under `reynolds` the wrapper owns
the output symmetry, so a declared clause degrades to an **iteration license** over
the signed permutation sum and validation stands down entirely
(`TypeCheck.fs:6086-6090`); `anticomm` already respects this, emptying
`antisymStorageGroups` under reynolds (`TypeCheck.fs:6080`). The recommendation
here is that a reynolds-wrapped kernel earn **no storage tie at all**, at any
level. A license to iterate is not a claim about the bare kernel, and building a
wreath storage class out of one reads a permission as a proof.

## 9. Implementation sketch

0. **Wire the existing scaffolding in, first and separately.** Construct a
   `PositionGroup` during type checking — beside the `IRIndexType`, from the same
   deduction that sets `Symmetry` — and route placement through `placementOfOrbit`
   where placement is actually consumed. That is less than it sounds (§1): the one
   live consumer is `bufferGroupCardinality` (`IR.fs:2467`); `placementOf`
   (`IR.fs:425`) is currently uncalled and comes along for free; and the allocator
   decision (`IR.fs:2827-2833`) today bypasses the placement axis entirely —
   `hasRealSymmetry` picks the routine and `allocRoutineFor` (`IR.fs:2648`)
   receives a hard-coded `PlaceCombinatorial SymAntisymmetric`. "Wire in" means
   giving those two bypassed sites a real placement input, not just swapping a
   function name at the one live one. On today's four classes `placementOfOrbit`
   and `placementClassOf` agree by construction, so the step stays
   behavior-preserving and lands and reverts independently of everything below.

1. `PositionGroup` gains one flat case, `PgWreath of levels: (int * Sign) list`,
   with `placementOfOrbit` mapping it to `PlaceCombinatorial` (§4's closed form)
   instead of `PlaceTabulated`. **The signs live on the levels, not on
   `SymmetryClass`**, which stays the nullary four-case enum it is
   (`Types.fs:13-17`): a nullary case has nowhere to put a list, and widening it
   touches every match in the compiler. Two decisions this forces up front. `Sign`
   is a new two-case type — none exists (`Deduce.SignParity`, `Deduce.fs:65-68`, is
   a three-valued lattice for a different job). And `PlaceCombinatorial of
   SymmetryClass` (`Types.fs:35`) needs a payload that can carry a level list,
   either by widening that case or by adding a sibling; that payload shape is the
   choice in this step that is expensive to revisit later.

2. `PgFullSym r` ≡ `PgWreath [(r,+)]` and `PgProduct` stays as-is, so this is a
   strict extension — the same "generalization, not a rewrite" property the
   existing comment at `Types.fs:92` claims.

3. Surface syntax, with `SymIdx`/`AntisymIdx` retained as aliases so no existing
   program changes. This needs a **new grammar production**, not a new argument to
   an old one: there are no tuple or sign literals (`Literal` is
   int/float/bool/string/char/unit, `Ast.fs:42-48`), so `[(2,+),(2,-)]` has no
   parse today. Follow `SymIdx`/`AntisymIdx`: a keyword arm in `parseTypeExpr`
   delegating to `parseIndexType` (`Parser.fs:653-657`) plus the arm itself
   (`Parser.fs:859-875`), with a new keyword token and an explicit choice of sign
   spelling (`+`/`-` tokens need the lexer to yield them inside a type context;
   `sym`/`antisym` words do not).

4. Canonicalizer: per-level sort fold per §5, sign accumulated multiplicatively.
   Deduction appends a level when a commutative kernel is applied to a repeated
   identity and juxtaposes untied classes otherwise — the §7.1 rule, bounded by
   §7.1's identity caveat.

5. **The level list needs its own `TypeEnv` side-channel.** Named-function kernels
   are eta-expanded with no where-clause, so an attribute without a channel is
   silently dropped and the failure is a wrong-but-plausible type, not an error.
   The recipe, verbatim from precedent: add a `Dictionary` field to `TypeEnv` keyed
   by function name (or binder id, if shadowing must not borrow it); populate it in
   `checkFunctionDecl`; consult it at *every* eta-expansion site. Copy
   `FuncCommGroups`/`FuncAntisymGroups`/`FuncParallel` (`TypeEnv.fs:196-282`
   carries every field built this way, each with a note on the silent failure it
   fixed), consulted at `TypeCheck.fs:5631` (comm), `:5641` (antisym), `:5645`
   (parallel) for the fixed-arity wrapper and `:5965`/`:5976` for the poly/pack
   one. Missing one site is the whole failure mode; no single seam covers them.

6. **All four enforcement seams need the `PgWreath` compatibility check**, and they
   share no code: `unify` via `indexPairIncompatible` (`Unify.fs:529-541`, called
   at `Unify.fs:666`, `:764`, and `TypeCheck.fs:2118`); the bespoke direct
   array-index arm, which never calls `unify` (`TypeCheck.fs:1969-2095`); the
   let-ascription arms of `checkExprInner` (`TypeCheck.fs:6972-7151`); and the
   post-pass tag recheck (described in the doc comment at `TypeCheck.fs:2267-2278`,
   above its implementation). Two specifics. **Extents are never compared** by any
   of them (`Unify.fs:527` says so outright), and a wreath class is meaningless
   without agreeing extents since §4's fold starts from `n` — level lists alone
   would accept `OrbIdx<[(2,+)],3>` against `OrbIdx<[(2,+)],4>`. And **dispatch must
   not rely on a `when` guard failing**: with no arm matching, `dispatchAppOrIndex`
   falls to a catch-all that mints a fresh inference variable and returns `Ok`
   (`TypeCheck.fs:2263-2265`), so a malformed `OrbIdx` application *passes*, at a
   type nothing later contradicts. The `OrbIdx` arms need explicit `Error` returns.

7. **Literal shapes must come from the declared type, per level.** `getShape`
   inside `inferArrayLitType` (`TypeCheck.fs:1457-1467`) reads each nesting depth's
   length as that axis's extent and stamps `Symmetry = SymNone` — right for a
   symmetric level, wrong for an antisymmetric one, where the elements written
   number `C(n,r)`, not `n`. The annotated path already does it correctly, taking
   the shape from the expected type's index list (`TypeCheck.fs:7049-7076`);
   `OrbIdx` literals should use that per level rather than infer.

8. **All three ML certificate walkers need the guard.** `MLEquiv`, `MLPerm` and
   `MLGalilean` (`src/ml/compiler/`) each walk typed expressions to discharge an
   equivariance certificate, and their own post-mortem states the rule: "the copies
   drift in the GUARDS, not in the rules … a guard only one copy has is a bug in
   the other two until argued otherwise" (`MLPerm.fs:43-48`). A new `PositionGroup`
   case is such a guard; add it to all three at once.

9. **Overflow checking is new work.** `binomI64` (`IR.fs:2438-2447`) is unchecked
   `int64` and no checked arithmetic exists on the placement or lowering path today
   (§7.2). §4's fold makes this materially worse than the single binomial it
   inherits, because each level's output is the next level's `n`.
