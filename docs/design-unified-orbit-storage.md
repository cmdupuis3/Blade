# Unified orbit storage: `comm`/`anticomm` and `ml.perm_equiv` as one theorem

Status: **measurement + design**. Nothing here is implemented. Every claim is
tagged **[M]** measured (a number this file produced, reproducibly) or **[A]**
argued (a derivation, not a run).

The charter question: `comm`/`anticomm` is a symmetry of INDEX POSITIONS and
becomes a type (`SymIdx<K,N>`); `ml.perm_equiv(N)` is a symmetry of DATA ENTRIES
and licenses nothing about layout. The user's insight is that TYING arguments —
passing the same thing twice — moves the symmetry from the value domain back into
the index domain, where it should become statically compactable again. This file
measures whether that is true, and it is, with one correction to the mechanism
and one sharp precondition.

---

## 0. The one-paragraph answer

Tying the arguments of an S_N-equivariant layer compacts **three** buffers at
once, and the compaction is real. **[M]** At the Maron point (K = L = 2) the
weight buffer drops from `perm_weight_dim = Bell(4) = 15` to **9**; at K + L = 6
it drops from **203 to 11–31** depending on the (K, L) split. **[M]** The cell
buffers drop from `N^K` to `C(N+K−1, K)` — 21.9× at K = 4, N = 64 — which is
exactly `SymIdx<K,N>`, already shipped. **[M]** The correct weight index is the
set of **S_K × S_L orbits of the partition weight basis**, verified against the
shipped `ml.derive_perm_linear` emission cell-for-cell. But the hypothesis's
stated mechanism needs one correction (§2.3): **only the S_L half of
"constant on orbits" produces the symmetric output; the S_K half is not a
symmetry condition at all, it is the statement that the tie has made those
weights redundant.** And the whole thing has a precondition that turns out to be
the guard the compiler *already* enforces: **[M]** completeness holds exactly
when `N ≥ K + L`, which is `MLPermSpec.checkPermSizing`'s existing v1 rule.

---

## 1. What was measured, and how

Four experiments. The model of the layer is not a paraphrase — it is
byte-for-byte `MLElaborate.permNestStmts`' emitted nest (positions `0..K−1`
input row-major with coefficient `N^(K−1−i)`, positions `K..K+L−1` output with
`N^(K+L−1−i)`, one nest per partition in `MLPermSpec.permPartitions` order), and
§2.4 pins the model against the shipped compiler on real output values.

| # | what | route |
|---|---|---|
| 1 | orbit counts, two independent routes | partition enumeration vs. vector-partition counting |
| 2 | the hypothesis, numerically at 44 (K,L,N) points | explicit basis matrices, ranks, residuals |
| 3 | which half of the condition does the tie buy; the antisym zero-guard | signed/unsigned projectors |
| 4 | `blade run` on the shipped `ml.derive_perm_linear` | real compiler, real values |

Definitions used throughout. `F_γ : ℝ^{[N]^K} → ℝ^{[N]^L}` is the basis map of
partition γ of the m = K + L positions: `(F_γ x)[o] = Σ_i B_γ(i ++ o) · x[i]`,
where `B_γ(t) = 1` iff t is constant on every block of γ. `P_K` is the S_K
symmetrizer on the K input positions, `A_K` the signed (alternating) one. "The
tie" means the input is in the image of `P_K` — i.e. it is symmetric in its K
node axes, i.e. it is a `SymIdx<K,N>` value.

---

## 2. The hypothesis, measured

### 2.1 Orbit counts — the weight buffer compaction **[M]**

`ml.perm_weight_dim(K,L,N) = Bell(K+L)` at N ≥ K+L (this is `MLPermSpec`'s own
theorem, and the table's third column reproduces it). The fourth column is the
number of orbits of the slot group S_K × S_L acting on those partitions.

Route B is genuinely independent of the partition picture: it counts orbits of
S_N on (K-multiset of [N]) × (L-multiset of [N]) directly, as multisets of
profile pairs (a,b) ≠ (0,0) with Σa = K, Σb = L. **All 27 cells agree.**

| K | L | m | Bell(m) = `perm_weight_dim` | S_K×S_L orbits | compaction | route B | agree |
|---|---|---|---|---|---|---|---|
| 1 | 1 | 2 | 2 | 2 | 1.00× | 2 | yes |
| 2 | 1 | 3 | 5 | 4 | 1.25× | 4 | yes |
| 1 | 2 | 3 | 5 | 4 | 1.25× | 4 | yes |
| 3 | 0 | 3 | 5 | 3 | 1.67× | 3 | yes |
| 2 | 2 | 4 | **15** | **9** | 1.67× | 9 | yes |
| 3 | 1 | 4 | 15 | 7 | 2.14× | 7 | yes |
| 4 | 0 | 4 | 15 | 5 | 3.00× | 5 | yes |
| 3 | 2 | 5 | 52 | 16 | 3.25× | 16 | yes |
| 4 | 1 | 5 | 52 | 12 | 4.33× | 12 | yes |
| 5 | 0 | 5 | 52 | 7 | 7.43× | 7 | yes |
| 3 | 3 | 6 | **203** | **31** | 6.55× | 31 | yes |
| 4 | 2 | 6 | 203 | 29 | 7.00× | 29 | yes |
| 5 | 1 | 6 | 203 | 19 | 10.68× | 19 | yes |
| 6 | 0 | 6 | 203 | **11** | **18.45×** | 11 | yes |

Two readings worth keeping:

* **the K = 0 column is the integer partition function.** `perm_bias_dim(L,N)`
  = Bell(L) drops to p(L) = 1, 2, 3, 5, 7, 11. The S_N-invariant constants in a
  *symmetric* rank-L node tensor are indexed by the partitions of the integer L.
* **the general count is a vector-partition count**, not a binomial: the number
  of multisets of nonzero vectors in ℤ≥0² summing to (K, L). This matters in §4 —
  it is why the weight index has no closed-form rank while the cell index does.

Cell-side compaction, for scale (`SymIdx<K,N>` = C(N+K−1, K), already shipped):

| K | N | `N^K` | `SymIdx<K,N>` | ratio |
|---|---|---|---|---|
| 2 | 64 | 4096 | 2080 | 1.97× |
| 3 | 64 | 262144 | 45760 | 5.73× |
| 4 | 16 | 65536 | 3876 | 16.91× |
| 4 | 64 | 16777216 | 766480 | **21.89×** |

### 2.2 The hypothesis holds, and is stronger than stated **[M]**

44 (K, L, N) points, K + L ≤ 6, N ∈ {3,4,5,6}. All residuals are exact zero or
machine epsilon (worst 2.5e-16 relative).

| check | result |
|---|---|
| every `F_γ` is S_N-equivariant | residual **exactly 0.0** at every point |
| `F_γ ∘ P_K` depends only on the S_K-orbit of γ | residual **exactly 0.0** at every point |
| **w constant on S_K×S_L orbits ⇒ symmetric input maps to symmetric output** | **holds at every point**, ≤ 2.5e-16 |
| `rank {P_L F_γ P_K}` = orbit count | **iff N ≥ K+L** (see below) |
| `rank {orbit-sum family}` = `rank {P_L F_γ P_K}` | equal at **every** point |

The last two lines are the part that is *stronger* than the hypothesis asked
for. "Constant on orbits" is not merely sufficient for symmetry-preservation; the
constant-on-orbits family **spans the whole of** `Hom_{S_N}(M_K, M_L)` and does
so **injectively**. So it is a complete, non-redundant coordinate system on the
tied layer — which is exactly what a storage layout must be.

The precondition is sharp, and it is already in the compiler:

| (K,L) | orbits | rank at N=3 | N=4 | N=5 | N=6 |
|---|---|---|---|---|---|
| (2,2) | 9 | 8 | **9** | 9 | 9 |
| (1,3) | 7 | 6 | **7** | 7 | 7 |
| (2,3) | 16 | 12 | 15 | **16** | 16 |
| (3,3) | 31 | 19 | 27 | 30 | **31** |
| (0,4) | 5 | 4 | **5** | 5 | 5 |

**[M] Full rank is reached exactly at N = K + L.** That is verbatim
`MLPermSpec.checkPermSizing`'s v1 rule ("N ≥ m so the basis is the FULL partition
lattice"), which was written for a different reason — to avoid a silent
basis fork. It happens to be precisely the condition under which the tied
compaction is lossless. Nothing new has to be guarded.

### 2.3 The correction: which half of the condition does the tie buy? **[M]**

The hypothesis bundles S_K and S_L into one condition. They do different jobs,
and separating them is the whole design.

| variant | tested | result |
|---|---|---|
| w constant on **S_L** orbits only, input **not** symmetric | 10 points | output symmetric, residual ≤ 1.9e-16 — **always holds** |
| w constant on **S_K** orbits only, input **symmetric** | 10 points | **FAILS**, relative residual 0.30 – 0.92 |

So:

* **The output symmetry is bought entirely by S_L-constancy of the weights, and
  does not need the tie at all.** `τ·F(x) = Σ_γ w_{τ⁻¹γ} F_γ(x)`, so S_L-invariant
  weights give a symmetric output for *any* input. **[A]**
* **The tie buys REDUNDANCY, not symmetry.** Because `F_γ ∘ P_K` depends only on
  the S_K-orbit of γ (measured exactly zero), once the input is tied only the
  S_K-orbit *sums* of w are observable. Collapsing each S_K-orbit to one slot is
  therefore free of information.

Combine them and you get the S_K × S_L orbit index — but for two different
reasons, and a design that conflates them will state the wrong obligation.

The precise necessary-and-sufficient condition for "tied input ⇒ symmetric
output", which is *weaker* than the hypothesis's sufficient condition:

> the **S_K-orbit sums** of w are constant on S_K × S_L orbits.

**[M]** verified as a dimension count. `dim {w : tied-in ⇒ sym-out}` equals
`Bell(m) − #(S_K orbits) + #(S_K×S_L orbits)` exactly, at every point with
N ≥ K+L (and is larger below the threshold, as expected):

| (K,L) | Bell | S_K-orbits | S_K×S_L orbits | predicted dim | **measured dim** |
|---|---|---|---|---|---|
| (2,2) | 15 | 11 | 9 | 13 | **13** |
| (2,3) | 52 | 36 | 16 | 32 | **32** |
| (3,2) | 52 | 21 | 16 | 47 | **47** |
| (3,3) | 203 | 74 | 31 | 160 | **160** |

At K = L = 2 the sym-out weight set is 13-dimensional but carries only **9**
distinct maps. **Constant-on-orbits is a canonical transversal of that 13-space,
not the whole of it.** For storage that is exactly right — one representative per
class — but the design must not claim the converse. Writing "symmetric output
⇒ weights constant on orbits" would be false.

### 2.4 Against the shipped compiler, not a model **[M]**

`ml.derive_perm_linear(2, 2, 4, x, w)` with a symmetric 4×4 input and weights
constant on the 9 orbits, run through `blade run` on the Release build of this
worktree:

```
W = 15
ys = [4019, 2614, 2798, 2956, 2614, 4787, 3132, 3290,
      2798, 3132, 5264, 3496, 2956, 3290, 3496, 5675]
asym = 0                 <- the 6 off-diagonal (i,j)/(j,i) squared differences
wbad = ...               <- ONE slot of a 4-element orbit bumped by 1.0
asym_bad = 219
bad_breaks = 1
model_err = 0            <- cell-for-cell against the independent model
```

Three things at once: the output is exactly symmetric; the negative control
(perturbing a single slot of a multi-element orbit) breaks symmetry; and the
model used for §2.1–2.3 reproduces the shipped emission to the last bit.

**This is also the first increment's acceptance test, already passing.** The
increment of §5 changes only *where the coefficient is read from* — the arithmetic
above is what it will emit.

### 2.5 The antisymmetric tie, and the zero-guard generalizes verbatim **[M]**

`exploration-equivariant-bijections.md` §4.3 states the rule "an orbit whose
stabilizer contains a sign-reversing element must store zero", and observes that
the antisym zero-diagonal is its R = 2, G = S₂ instance. The measurement shows
the *same rule*, unchanged, governs the WEIGHT buffer of an antisymmetrically
tied layer.

Tied with the sign character instead of the trivial one (`A_K` in place of `P_K`):

| (K,L) | Bell | γ with `F_γ A_K = 0`, measured | γ with a χ-reversing S_K-stabilizer, predicted | agree | live orbits | `rank A_L F A_K` | agree |
|---|---|---|---|---|---|---|---|
| (2,2) | 15 | 7 | 7 | yes | 2 | 2 | yes |
| (2,1) | 5 | 3 | 3 | yes | 1 | 1 | yes |
| (2,3) | 52 | 20 | 20 | yes | 1 | 1 | yes |
| (3,2) | 52 | 46 | 46 | yes | 1 | 1 | yes |
| (3,1) | 15 | 15 | 15 | yes | 0 | 0 | yes |
| (3,3) | 203 | 161 | 161 | yes | 2 | 2 | yes |

**16 of 16 rows agree on both columns.** The predicate that computes the dead set
is one line of group theory and it is the same predicate `canon_fold`'s `zero`
out-parameter already implements for the strict case.

The full character table, `live orbits` for each choice of trivial/sign on each
side. This is the general "G-graded tie" and it is a single formula:

| K | L | Bell(K+L) | sym→sym | anti→anti | sym→anti | anti→sym |
|---|---|---|---|---|---|---|
| 1 | 1 | 2 | 2 | 2 | 2 | 2 |
| 2 | 1 | 5 | 4 | 1 | 4 | 1 |
| 1 | 2 | 5 | 4 | 1 | 1 | 4 |
| 2 | 2 | 15 | 9 | 2 | 2 | 2 |
| 3 | 1 | 15 | 7 | 0 | 7 | 0 |
| 3 | 2 | 52 | 16 | 1 | 5 | 0 |
| 3 | 3 | 203 | 31 | 2 | 1 | 1 |
| 5 | 1 | 203 | 19 | 0 | 19 | 0 |

The zeros are real theorems, not gaps: there is **no** nonzero S_N-equivariant
map from antisymmetric rank-3 node tensors to antisymmetric rank-1 ones. A
compiler that can compute this column can *refuse* such a layer with a reason
rather than emitting a buffer of zeros.

---

## 3. The cardinality / multiset framing, assessed

The framing under test: an S_N-invariant function of an array depends only on the
multiset of entries, so its orbits are VALUE-dependent and cannot be a static
layout, whereas `comm`'s orbits are INDEX-dependent and can.

**The framing is correct about what it names, and it names the wrong object.**
Short version, three points:

1. **It is right about the DATA.** The orbits of S_N acting on the *contents* of
   a node buffer are value-dependent (`GROUP BY value, COUNT`), the stabilizer
   varies with the data, and no static index type can express that. Blade should
   not try. **[A]**
2. **But the compactable object was never the data-value orbit.** It is the orbit
   of S_N on **index tuples** `[N]^{K+L}`, which is a partition of positions — an
   entirely static, data-independent object. That is *why*
   `ml.perm_weight_dim` is already a compile-time Bell number and not a runtime
   query. The static/dynamic line therefore does not run between `comm` and
   `perm`; it runs between the **weight/basis side** of perm (static, index-
   indexed, compactable — this whole document) and the **value side** (dynamic,
   not a layout). **[M]** — the entire §2 measurement lives on the static side and
   never touches a data value.
3. **The dividing line that survives contact with the code** is about the DOMAIN
   OF THE ACTION, not about invariance:
   * a group acting on **index positions** (a subgroup of S_rank permuting slots)
     is static: it is `comm`/`anticomm`, `SymIdx`, and — after a tie — the S_K
     and S_L factors here. It compacts.
   * a group acting on **index values** (a subgroup of S_extent relabelling one
     axis' coordinates) is not a layout at all. What it *does* give you is a
     static Hom-space whose basis is indexed by orbits on index tuples, and
     **that** basis compacts under position groups. One level up, same rule.

**Is there a useful middle (a dynamic/histogram representation)?** For this
problem, no, and that is a positive finding rather than a deferral: the tie makes
a histogram unnecessary. The data compacts because the *tie* is a position
symmetry (→ `SymIdx`), and the weights compact because the *weight index* is a
position-orbit set. Neither needs a value histogram. Firmly out of scope, and
nothing in §2 wanted it.

---

## 4. The unified storage design

One mechanism, three existing customers plus one new one.

### 4.1 What the index type carries

An orbit placement is a triple:

* **base tuple space** — R positions over given extents (for `SymIdx<K,N>`:
  `[N]^K`);
* **position group** `G ≤ S_R` — which slot permutations are declared
  interchangeable;
* **linear character** `χ : G → {+1, −1}` — the value transform on non-canonical
  access.

That is all. Every shipped case is an instance:

| customer | base | G | χ |
|---|---|---|---|
| `Idx<N>^R` dense | `[N]^R` | trivial | + |
| `SymIdx<R,N>` (`comm`) | `[N]^R` | S_R | + |
| `AntisymIdx<R,N>` (`anticomm`) | `[N]^R` | S_R | sgn |
| `HermIdx` | `[N]^R` | S_R | conj (the third character) |
| a multi-group `comm` pin | `[N]^R` | ∏_j S_{R_j} | + per group |
| **tied perm cells (new)** | `[N]^{K+L}` | S_K × S_L | +/sgn per side |
| **tied perm weights (new)** | partitions of `[K+L]` | S_K × S_L | +/sgn per side |

### 4.2 Where it lands on `Types.fs`'s own axes

`Types.fs` already has the right split, and it already anticipates the shape of
the generalization:

> "PlacementClass … answers *which tuples are stored, and how is a tuple ranked
> to a flat offset*, independent of any value transform applied on non-canonical
> access … `PlaceCombinatorial` … carr[ies] their `SymmetryClass` so
> ranking/cardinality can distinguish inclusive (sym/herm) from strict (antisym)
> combinadics."

Placement is `(base, G, zeroSet(G,χ))`; transform is `χ`. The zero-set depends on
χ, which is exactly why `PlaceCombinatorial` already carries the `SymmetryClass`
— the existing design comment is the general statement in the R = full-S_R case.
The types-only skeleton is small:

```fsharp
/// Which subgroup of S_R permutes the R index positions of one storage group.
type PositionGroup =
    | PgTrivial                    // G = 1           -> PlaceDense
    | PgFullSym  of rank: int      // G = S_R         -> combinadic, CLOSED FORM
    | PgProduct  of ranks: int list// G = prod_j S_Rj -> MIXED RADIX, closed form
    | PgOpaque   of tag: string    // any other finite G -> PlaceTabulated

let placementOfOrbit (g: PositionGroup) (chi: SymmetryClass) : PlacementClass =
    match g with
    | PgTrivial     -> PlaceDense
    | PgFullSym _   -> PlaceCombinatorial chi
    | PgProduct _   -> PlaceCombinatorial chi
    | PgOpaque _    -> PlaceTabulated
```

`SymmetryClass` is unchanged and *is* χ. `PlaceCombinatorial` is unchanged.
Nothing existing is reclassified: `placementClassOf` is `placementOfOrbit
(PgFullSym r)` at every current call site.

### 4.3 What the accessor does — it already exists

`canon_fold` / `canon_left_justify` / `canon_transform` in
`src/cpp/nested_array_utilities.hpp` are already the general three-phase accessor,
and the header already states the cost model that makes this free:

1. **FOLD** — map a dense tuple to its G-orbit representative, returning the
   group element used. For `G = S_R` this is a sort plus inversion parity;
   `canon_fold` returns exactly that parity, which *is* `χ(g)` encoded for
   χ ∈ {1, sgn}.
2. **ZERO** — if `Stab_G(t)` contains an element with `χ = −1` the cell is
   structurally zero. **[M] §2.5**: this single predicate reproduces the antisym
   diagonal *and* the antisym weight death, 16/16.
3. **TRANSFORM** — multiply by `χ(g)`. `canon_transform` already dispatches
   Identity / NegateOnSwap / ConjugateOnSwap.

**No C++ signature change is needed for the {1, sgn, conj} character family** —
only a different FOLD for a different G. And per the header, "iteration-context
reads are canonical by construction and bypass `canon_access` entirely", so the
compaction stays free in bulk compute; only random access pays the fold.

### 4.4 Where a closed-form rank exists, and where it does not

This is the part not to over-promise. Four tiers, and the proof tower already
decides three of them.

| tier | G | rank | cell count | status |
|---|---|---|---|---|
| 1 | `S_R` | combinadic / left-justify | `C(N+R−1,R)`, `C(N,R)` strict | **SHIPPED**; `BladeDMWF.lj_correct/unlj_correct`, `BladeBinomial` |
| 2 | `∏_j S_{R_j}` on disjoint position blocks | **mixed radix of the per-group ranks** | `∏_j C(N_j+R_j−1, R_j)` | **SHIPPED as multi-group `comm`**; `BladeMixedRadix.mixed_radix_bijection`, `shapeCard_binom` |
| 3 | `S_K × S_L` on the **partition** lattice (the weight index) | **none** — a compile-time table | vector partitions of (K,L) | NEW; bounded by the existing `K+L ≤ 6` cap ⇒ ≤ 203 entries |
| 4 | general finite `G ≤ S_R` | **none** — Burnside sum, runtime table | Burnside | NOT PROPOSED; `PlaceTabulated`, as §4.3 of the exploration cautions |

The load-bearing finding is **tier 2**, and it is the reason this design is small
rather than large:

> **[A]** The tied-perm CELL layout is *already* tier 2. Input `SymIdx<K,N>`,
> output `SymIdx<L,N>`, position group `S_K × S_L` on disjoint blocks of the
> K+L positions — that is verbatim `BladeMixedRadix`'s hypothesis ("across
> DISTINCT identity groups the mixed-radix composition of per-group ranks IS a
> lossless layout"). **The tie introduces no new cell layout and no new proof
> obligation.** It is the same type multi-group `comm` already produces. The
> shipped tie mechanism for that family is corpus
> `arity/027_symmetric_pack_comm.blade` — `object_for(packprod) <@> (A, A)` on a
> `where comm(a)` kernel, whose pinned expectation is `SymIdx<2,3>`, 6 cells not
> 9. (Cited from the test source, not re-run here; corpus batches are outside
> this task's remit — see §7.)

`BladeCounting.v`'s negative result does **not** bite here: it forbids a lossless
*per-dimension product* layout inside ONE identity group across several
dimensions. The tied-perm case is the opposite configuration — distinct groups on
disjoint positions — which is the case `BladeMixedRadix` was written to cover.

Tier 3 is where the honesty is required. The weight index is not a tuple space,
so no combinadic applies, and the count (vector partitions of (K, L)) has no
useful closed form. But it does not need one: the surface is capped at
`K + L ≤ 6`, so the orbit table is at most 203 entries computed at compile time
by pure integer code. **A compile-time table is not `PlaceTabulated`'s runtime
mask** — it costs nothing at run time and needs no allocator. If it is given a
placement case at all it should be a distinct one; more likely it never becomes
an index type (see §5, increment 1).

### 4.5 The Coq obligation shape

Three obligations, two of which are discharged.

* **Cells (tiers 1–2): nothing new.** `BladeDMWF` + `BladeMixedRadix` already
  cover the tied layout. **[A]**
* **Weights: independence + completeness, not a bijection.** This is the
  `derive_linear` / `BladePointGroup` sub-kind of `exploration-equivariant-
  bijections.md` §4.1's table, not the `SymIdx` sub-kind. Two statements, both
  measured:
  * **counting** — `#(S_K×S_L orbits on partitions of [K+L] with ≤ N blocks)
    = #(multisets of nonzero (a,b) ∈ ℤ≥0² with Σa=K, Σb=L, ≤ N parts)`.
    **[M]** 27/27 cells. This is a `BladePartition.v`-shaped statement.
  * **independence** — `MLPermSpec.certify`'s unitriangular witness-evaluation
    matrix, lifted to the quotient. **[M]** the numerical shadow: `rank {orbit-sum
    family} = #orbits` iff `N ≥ K+L`, at every tested point.
* **The zero-guard: one lemma, and it unifies two shipped behaviours.**
  If `h ∈ Stab_G(t)` and `χ(h) = −1` then `v = χ(h)·v = −v`, so `v = 0`.
  **[M]** twice: it is the antisym diagonal (R=2, G=S₂ — the exploration's
  observation) *and* the antisym weight death of §2.5. A three-line Coq lemma
  that retires a special case.

---

## 5. Staged plan

Explicitly **not** a plan to unify the type system first. The measurement says the
cell side needs no new type at all (§4.4 tier 2), so the cheap win is on the
weight side and it costs one expression.

### Increment 1 (the named smallest useful one) — `derive_perm_linear_sym`

**Zero new index types. Zero storage change. Zero C++ change.**

`ml.derive_perm_linear_sym(K, L, N, x, w)` and its sizing partner
`ml.perm_weight_dim_sym(K, L, N)`. Buffers stay flat `Idx<N^K>` / `Idx<N^L>`
exactly as today; only `w` shrinks, from `Bell(K+L)` to the orbit count.

The emission diff is one expression in `MLElaborate.permNestStmts`:

```
idx coefName (iLit g)          ->     idx coefName (iLit (orbitId g))
```

because `w_γ = c_{orbit(γ)}` makes `Σ_γ w_γ F_γ = Σ_O c_O (Σ_{γ∈O} F_γ)`. The new
integer layer is a `permOrbits` function beside `permPartitions` in
`MLPermSpec.fs`, certified on every call by the same house pattern already there
— now with the *two independent counting routes* of §2.1 as the assert, which is
strictly stronger than the single Stirling cross-check the file uses today.

What it ships, and what it does not:

* it delivers the **weight compaction** (1.25× to 18.45×) and the *theorem*
  "this is the complete basis of `Hom_{S_N}(Sym^K, Sym^L)`" — **[M]** complete and
  independent at N ≥ K+L, which is the guard `checkPermSizing` already applies;
* it does **not** compact the cells. The result is provably symmetric but still
  stored in `N^L` flat cells, redundantly. Say so in the diagnostic.

Its acceptance test **already exists and already passes** — §2.4's probe is
exactly this computation done by hand. That is why this is the first increment:
the behaviour is measured before the code is written.

### Increment 2 — the seam classifies `SymIdx`

Today `MLPerm.statusOfType` refuses a `SymIdx<K,N>` axis by surface syntax
(re-measured in this worktree; the BL4012 text is unchanged), and
`census-perm-layer.md` §6.2 measured that an extent-only typed classifier would
read `SymIdx<2,4>` as `Pow 1` — a *wrong status in the covariant direction*. Fix
that at the seam: classify `SymIdx<K,N>` as `Pow K` from `ix.Rank`/`ix.Symmetry`
rather than from the flat extent. This is a safety fix on its own and it is the
prerequisite for the cell compaction. No new type; `SymIdx` already exists.

### Increment 3 — cells compact: tied in, tied out

Accept `Array<Float like SymIdx<K,N>>` as `derive_perm_linear_sym`'s `x`, and
return `SymIdx<L,N>`. Tier 2 of §4.4, so the layout and its Coq obligation are
already discharged; the work is emission (restricting the output nest to the
canonical region, which is the one genuinely open engineering question in this
document) plus the `canon_access` read on `x`.

### Increment 4 — the character axis: antisymmetric ties

Add χ = sgn on either side. The counting is §2.5's table, the zero-guard is
§4.5's lemma, and the runtime is `canon_fold`'s existing `zero`/parity. This is
where the design earns its keep: the same code path now refuses
"antisym rank-3 → antisym rank-1" with *zero live orbits* as a theorem.

### Increment 5 (optional, and only if a customer appears) — `PgOpaque`

General finite `G`. `PlaceTabulated`, Burnside cell count, **no closed-form
rank**. Do not build this speculatively; §4.4 tier 4 exists in this document so
that nobody promises it by accident.

---

## 6. What would sharpen this further

* **The output-nest restriction (increment 3).** Not measured. The input side is
  a straightforward `canon_access` read; the output side needs a nest over
  canonical output tuples, and it is not a loop-bound change in general because
  the output index is a function of the shared block variables. This is the one
  place where "obvious" should not be trusted.
* **The truncated-basis regime (N < K+L).** **[M]** the rank is strictly below the
  orbit count there (e.g. 27 vs 31 at (3,3), N=4). v1 refuses that regime already,
  so it is out of scope — but if the truncated basis is ever admitted, the orbit
  quotient must be recomputed against the truncated lattice, not the full one.
* **Hermitian ties.** χ = conj is the third character and `canon_transform`
  already implements it, but nothing here measured a Hermitian tie.

---

---

## 7. What this task did not run, and should be run

No corpus batch was run (outside this task's remit). Two would sharpen the file:

* **`blade test arity`** — confirms the `comm` tie mechanism cited in §4.4
  (`arity/027_symmetric_pack_comm.blade`) is green on this worktree, which is the
  premise of the "tier 2 is already shipped" argument.
* **`blade test ml-equiv`** — confirms the `Types.fs` `PositionGroup` addition
  (additive, unwired, Release build clean with 0 warnings) has not disturbed the
  perm layer whose emission §2.4 measured.

Neither is expected to move. They are premise checks, not risk checks.

---

## Appendix: reproducing the measurements

The four experiment scripts are self-contained (Python 3 + numpy) and were run
against the Release build of this worktree; the blade probe is generated from
the same model that produces the tables, which is how §2.4's `model_err = 0`
cross-check is possible. Experiment scripts live in the session scratchpad, not
the repo; the probe they emit is reproduced in §2.4. The single dependency on the
compiler is `MLPermSpec.permPartitions`' RGS-lex order, which the model
re-implements and §2.4 pins.
