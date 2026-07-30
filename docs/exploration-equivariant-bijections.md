# Exploration: condensed storage and index bijections for equivariant types

Status: EXPLORATION (2026-07-28). Decision input for
`plan-equivariance-in-types.md` §6.3 (the `ml.restrict` API surface) and §6.4
question 4 (one restriction principle, or a per-relationship call). No compiler
code changes; every number below is measured, and every claim is marked
MEASURED or ARGUED.

---

## 0. The question, and the one-paragraph answer

§6.3 leaves two surfaces live for restriction: an explicit `ml.restrict(x, G)`
op, or an implicit signature-level license (an `ml.equiv(O3)` pin licenses
viewing the same function/value at point-group types, the way `comm` licenses
viewing a kernel at compact `SymIdx` signatures). The user's framing is that the
choice should turn on whether restriction is a lossless index bijection like
`SymIdx` compaction, or a genuine computation.

**It is a bijection — a signed permutation of indices — for a characterizable
class of groups, and it is genuinely not one outside that class.** For the two
shipped groups the bijection was constructed explicitly and verified against
`pgElementMatrix` to machine precision at every l ≤ 8. But the class boundary is
sharp and lands *inside* the roster the registry is planning to grow into: every
axial point group in standard orientation is in; every cubic and icosahedral
group falls out at l = 3; every misaligned conjugate embedding falls out at the
first l whose m-pair splits. So the recommendation is neither of §6.3's two
options as stated, but a third that subsumes both: **make the verdict DATA**
— compute the view table at group-registration time, exactly as
`certifyPointGroup` already computes the FS indicators, and let the computed
verdict select the surface. Details in §6.

A second finding cuts against the implicit surface independently of the cost
argument, and it is not about bijections at all: the restricted spec — hence the
SIZE of the weight buffer the user must supply — is not determined by the input
type alone. See §6.3.

---

## 1. What was measured, and how

All measurements are `dotnet fsi` probes in this session's scratchpad
(`probe1..probe5.fsx`, plus `shcore.fsx`); the method is stated here in full so
they are reproducible from scratch.

**The O(3) side.** Orthonormal real spherical harmonics `Y_lm`, ordered
`m = -l .. l` — Blade's `IrrepsIdx` block layout. `D^l(R)`, the matrix of
`(ρ(R)f)(v) = f(R⁻¹v)` on that basis, is fitted by least squares against 12·(2l+1)+20
sampled directions. (Any global per-m sign convention — Condon–Shortley or not —
is a diagonal ±1 rescale of the basis and changes none of the verdicts below.)

**The group side.** Point groups are given as 3×3 orthogonal generators and
closed by BFS. For C4 and D4 the elements and their geometric matrices come from
`MLPointSpec.embeddedElements`, so the shipped embedding — and only it — is what
gets tested.

**Test COORD-IRR** (the necessary-and-sufficient subspace test). Compute the
finest coordinate-aligned G-invariant partition of the real-Y basis (connected
components of "some ρ(g) has a nonzero (i, j) entry"). Then test each block for
irreducibility over ℝ: `W` is REDUCIBLE iff its commutant contains a non-scalar
SYMMETRIC element, which a Reynolds average of a random symmetric matrix finds
in a handful of draws — no character table and no division-algebra
classification needed. COORD-IRR holds iff every block is irreducible, i.e. iff
V decomposes into G-irreducibles each spanned by a SUBSET of the real Y basis.

> A cheaper test — "is the cyclic module ⟨G·e_i⟩ a coordinate subspace" — is
> only NECESSARY, and the difference is not academic: a misaligned D4 passes it
> at l = 2 and fails COORD-IRR there, because ⟨G·e⟩ is the whole m = ±2 pair
> while the pair's two irreducible constituents lie along the ±45° diagonals.
> Probe 1 used the weak test and got the wrong answer for that row; probe 2
> fixed it. Recorded because the weak test is the tempting one.

**Test SPERM.** Is every ρ(g) itself a signed permutation matrix in the real-Y
basis? This is a strictly stronger and separate property: it is what makes a
frozen pg table INTEGRAL, and it is exactly `MLPointSpec`'s stated {0, ±1}
rationality boundary.

**The exact construction (probe 4).** For C4 and D4, for each l, build a matrix
`P` by: taking the coordinate blocks; identifying each block's label by matching
its character against the frozen table's; assigning blocks to `pgBlockStarts`
slots in `restrictIrrep`'s spec order; and searching the (at most 8) signed
permutations of each block's coordinates for the one that reproduces the label's
frozen generator matrices. Then verify globally

```
    P · D^l(g) · Pᵀ  ==  pgElementMatrix(grp, restrictIrrep(grp, l, p), g)
```

for EVERY group element, and check that `P` is a signed permutation.

---

## 2. (a) Is signed-permutation a theorem or a coincidence?

### 2.1 The exact result for the shipped groups — MEASURED

For C4 and D4 in their shipped embeddings (`⟨R_z(90°)⟩` and
`⟨R_z(90°), R_x(180°)⟩`), at every l ∈ [0, 8]:

* the signed permutation `P` EXISTS and was constructed;
* `max |P·D^l(g)·Pᵀ − pgElementMatrix(...)|` over all elements and all l is
  **8.9e-16** (worst case, at l = 8); most rows are ~1e-16 or exactly 0;
* `P` is a signed permutation matrix in the strict sense (exactly one nonzero,
  of modulus exactly 1, per row AND per column);
* the signs are NOT decorative. Negative entries first appear at l = 1 for C4
  and l = 2 for D4, and the count grows with l (2 negatives by l = 5).

The specs `restrictIrrep` produced along the way reproduce corpus 096/097's
pinned branching table exactly (C4: A; A+E; A+2B+E; A+2B+2E; 3A+2B+2E — D4: A1;
A2+E; A1+B1+B2+E; A2+B1+B2+2E; 2A1+A2+B1+B2+2E), and the construction filled
every pg slot from independently computed `D^l` blocks, which is a second,
independent confirmation of those multiplicities.

The explicit low-l maps (pg slot ← ± O(3) m-slot):

```
C4, l=1 -> [("A",1); ("E",1)]        O(3) order: [0]=Y(-1) [1]=Y(0) [2]=Y(+1)
    A  : pg[0] = +O3[1]  (m= 0)
    E  : pg[1] = -O3[0]  (m=-1),  pg[2] = +O3[2]  (m=+1)

C4, l=2 -> [("A",1); ("B",2); ("E",1)]
    A  : pg[0] = +O3[2]  (m= 0)
    B  : pg[1] = +O3[0]  (m=-2)
    B  : pg[2] = +O3[4]  (m=+2)
    E  : pg[3] = -O3[1]  (m=-1),  pg[4] = +O3[3]  (m=+1)

D4, l=2 -> [("A1",1); ("B1",1); ("B2",1); ("E",1)]
    A1 : pg[0] = +O3[2]  (m= 0)
    B1 : pg[1] = +O3[4]  (m=+2)
    B2 : pg[2] = +O3[0]  (m=-2)
    E  : pg[3] = -O3[1]  (m=-1),  pg[4] = +O3[3]  (m=+1)

D4, l=3 -> [("A2",1); ("B1",1); ("B2",1); ("E",2)]
    A2 : pg[0] = +O3[3];  B1 : pg[1] = +O3[1];  B2 : pg[2] = +O3[5]
    E  : pg[3] = -O3[6],  pg[4] = +O3[0]        <- a SWAP as well as a sign
    E  : pg[5] = +O3[4],  pg[6] = +O3[2]
```

The C4 l=1 row is exactly the situation corpus 099 pins: the invariant is at
O(3) index 1 and pg index 0. What the measurement adds is that the repair
exists, is `pg[0] ← +O3[1]`, `pg[1] ← −O3[0]`, `pg[2] ← +O3[2]`, and costs one
sign flip.

**This confirms rather than contradicts the existing corpus headers.** 048, 096,
099 and `MLPointSpec`'s restriction block comment all already say the change of
basis is "a genuine permutation (with signs)". The measurement makes that
precise (which signs, at which l, verified against the emitted layout) and
extends it in the direction the headers do not address: whether it stays true as
the registry grows. It does not.

### 2.2 Where genuine mixing first appears — MEASURED

COORD-IRR over 19 (group, embedding) pairs at l ≤ 8:

| embedding | COORD-IRR | SPERM (all ρ(g) signed perms) |
|---|---|---|
| C4z, C2z, C3z, C6z **(any orientation about their own axis)** | yes, all l ≤ 8 | C4z, C2z yes; C3z, C6z **no** (from l = 1) |
| D3(β=0), D3(β=30), D4(β=0), D4(β=45), D6(β=0) | yes, all l ≤ 8 | D4 yes; D3, D6 no |
| C4h, C4v, D4h, S4z (improper axial) | yes, all l ≤ 8 | yes |
| **D4 at β = 22.5°** (misaligned conjugate) | **NO — first mixing at l = 2** | no |
| **C4 about the (1,1,1) axis** | **NO — first mixing at l = 1** | no |
| **T** (tetrahedral rotations) | **NO — first mixing at l = 3** | no (from l = 2) |
| **O** (octahedral rotations) | **NO — first mixing at l = 3** | no (from l = 2) |
| **I** (icosahedral rotations) | **NO — first mixing at l = 3** | no (from l = 1) |

Reading the four sub-questions the task posed:

* **Non-axial groups (T, O, I) — YES, this is where genuine mixing lives.** All
  three first mix at l = 3. Below that they are coordinate-aligned: under O,
  l = 2 splits as E ⊕ T2 with E = {Y₂₀, Y₂₂} and T2 = {Y₂,₋₂, Y₂,₋₁, Y₂₁},
  which IS a subset partition. So s/p/d shells restrict to the cubic groups by
  permutation; f shells do not. The l = 3 failure is the familiar one — the
  cubic T1u/T2u sets are fixed combinations of the m = ±1 and m = ±3 pairs, and
  no reordering separates them.
* **Realified conjugate pairs / FS indicator 0 — NO, this is not the
  obstruction.** C4's E (complex type, e = 2), C3z, C6z, S4z and C4h all carry
  e = 2 labels and all pass COORD-IRR at every l ≤ 8. The FS type governs the
  size of `End_G(U)` (and therefore the weight count) and is orthogonal to
  whether the change of basis is a permutation. Worth stating because the
  natural guess is the opposite.
* **Non-canonical / rotated embeddings — YES, and this is the sharpest
  failure.** A C4 about (1, 1, 1) fails already at l = 1. A D4 whose two-fold
  axes sit at 22.5° fails at l = 2 even though its 4-fold axis is still z.
  Permutation-ness is a property of the (group, ORIENTATION) pair, not of the
  abstract group.
* **l beyond 4 — NO new failures.** Nothing in the aligned axial class breaks
  anywhere in 5 ≤ l ≤ 8, and the argument in §2.4 says nothing will.

### 2.3 The alignment condition, stated and then falsified-tested — MEASURED

For a dihedral group `D_n` with the n-fold axis along z and its two-fold axes at
azimuth β, COORD-IRR holds at every l iff

* n even: β ≡ 0 (mod 180°/n)
* n odd:  β ≡ 0 (mod  90°/n)

β itself is only defined modulo 180°/n (the axes sit at β + k·180°/n), so this
says: at even n exactly ONE conjugacy class of embedding works; at odd n exactly
TWO do (β = 0 and β = 90°/n).

The rule was written down first and then tested: **32 of 32 cases agree**
(D3, D4, D5, D6 × β ∈ {0, 9, 10, 15, 18, 22.5, 30, 45}°). D6 at β = 30° and
D4 at β = 45° are the interesting confirmations — both look "misaligned" but are
the SAME group as their β = 0 twin, and both pass. D5 at β = 18° is the odd-n
second class, and it passes.

### 2.4 Why it is a theorem, not a coincidence — ARGUED (verified to l = 8)

The mechanism is the m-pair grading and nothing else:

1. A rotation about z by α acts on the real-Y basis block-diagonally, rotating
   each pair {Y_lm, Y_l,-m} by the plane rotation R(mα) and fixing Y_l0. So the
   m-pair grading is preserved by the entire cyclic part, for every l. Each pair
   is either irreducible (R(mα) ≠ ±I) or splits into its two coordinate lines
   (R(mα) = ±I).
2. A two-fold axis in the xy-plane at azimuth β sends φ ↦ 2β − φ and
   θ ↦ π − θ, so on the pair {Y_lm, Y_l,-m} it acts as (−1)^(l+m) times the
   reflection at angle 2mβ — again pair-preserving, and DIAGONAL when
   2mβ ≡ 0 (mod 180°). σ_h, σ_v(xz), the inversion and S_2n are likewise
   pair-preserving and diagonal-with-signs.
3. So the only way a coordinate-aligned decomposition can fail in an axial group
   is at an m whose pair the cyclic part leaves unsplit-by-itself but which the
   secondary generator must split — precisely the condition of §2.3.
4. Nothing in 1–3 depends on l, which is why the property is uniform in l and
   why the l ≤ 8 sweep is confirmation rather than the argument.
5. Conversely, for a non-axial group there is no single axis whose m-grading all
   generators respect, so beyond the low-l accidents (where the whole space or a
   large chunk of it is irreducible anyway) the isotypic pieces cut across
   m-pairs. This half is ARGUED and only checked at T, O, I.

**Not checked:** double groups / quaternionic labels (`FsQuat` is reserved, not
shipped); complex-pipeline bases (`WignerTables`' reserved `CGIndexComplex`);
l > 8. None of these is reachable from the current roster.

### 2.5 A separate axis: permutation-ness vs. table rationality

SPERM and COORD-IRR are independent, and confusing them would mis-scope the
feature:

* C3z / C6z / D3 / D6 are COORD-IRR (the view is a signed permutation) but not
  SPERM — their block matrices contain √3/2, so a frozen table for them needs a
  coefficient ring. The VIEW is still exact and free; only the TABLE is
  irrational. This is `MLPointSpec`'s named ℚ(√3) growth boundary, and the
  measurement says it does not obstruct the view.
* T and O at l = 2 are COORD-IRR but not SPERM, for the same reason.
* D4 at β = 22.5° is neither.

So: **the registry's {0, ±1} rationality boundary and the view-existence
boundary are different lines, and the second is the more permissive one.** In
every case measured, SPERM implied COORD-IRR — but that is an observation, not a
theorem, and it is worth not believing too hard: Z₂ acting on ℝ² by the swap
`[[0,1],[1,0]]` is a signed-permutation rep that is reducible with constituents
along the ±45° diagonals, i.e. not coordinate-aligned. That is not an idle
counterexample — it is exactly the block that breaks the misaligned D4 at l = 2.
The implication that does hold and is used above is the trivial one: a signed
permutation matrix sends coordinate vectors to ± coordinate vectors, so cyclic
modules are coordinate subspaces; irreducibility of those blocks still has to be
checked, and COORD-IRR checks it.

---

## 3. (b) Can the index-type tower host it?

Yes, and — this is the notable part — with no new concept in the runtime. Every
ingredient already exists for antisymmetric compact storage.

### 3.1 What is being asked of the type

Both `IrrepsIdx<S>` and `PgIrrepsIdx<G, S'>` are RANK-1 index slots
(`IRIndexTypeG.Rank = 1`) whose `Extent` is the module's total ℝ-dimension.
Crystal-field splitting changes the number of BLOCKS but never the total
dimension (`restrictSpec` asserts exactly that), so the view is dimensionally
inert: same `Rank`, same `Extent`, different `Tag` (`mkIrrepsTag` →
`mkPgIrrepsTag`) and different `IxKind` (`IxKIrreps` → `IxKPgIrreps`). Nothing
about the array's C++ shape changes.

The genuinely new datum is the accessor: a baked pair of tables of length
`total_dim`, `π : slot → slot` and `σ : slot → {±1}`.

### 3.2 Where it belongs on Types.fs's own axes

`Types.fs` already factors index behaviour into two orthogonal axes and says
so explicitly: `PlacementClass` ("which tuples are stored, and how is a tuple
ranked to a flat offset") and `SymmetryClass` (the value transform applied on
non-canonical access). It also notes that the two only DIVERGE at tabulated
types. **A restriction view is the second place they diverge**, and it lands
cleanly:

* placement: a permuted ranking — a `PlaceSignedPermuted` sibling of
  `PlaceTabulated`, carrying (or naming) the baked π;
* transform: a sign — the same axis `SymAntisymmetric` already occupies, but
  keyed on a table lookup rather than on swap parity.

That is the right shape. Reaching for a new `SymmetryClass` case instead would
be a category error: `SymmetryClass` is about the transform under permutation of
a multi-component group, and a restriction view has one component.

### 3.3 The read path already exists

`src/cpp/nested_array_utilities.hpp` ships, for compact symmetric/antisymmetric
storage:

```cpp
enum class ReadTransform { Identity, NegateOnSwap, ConjugateOnSwap };
int  canon_fold(std::array<size_t,R>& idx, bool strict, bool& zero);   // -> parity
auto canon_left_justify(...);                                          // -> storage coords
T    canon_transform(const T& val, int parity, ReadTransform tf);       // parity ? -val : val
```

The header's own summary of the discipline is "the read path never branches on
symmetry class — it folds, fetches, transforms". A restriction view is that
pipeline with `canon_fold`'s sort replaced by a table lookup:

```cpp
// restrict_fold: table-driven; parity comes from sigma, not from inversions
inline size_t restrict_fold(const int* pi, const signed char* sg, size_t i, int& parity)
{ parity = (sg[i] < 0); return pi[i]; }
// ... then canon_transform(val, parity, ReadTransform::NegateOnSwap) unchanged.
```

Cost: one indexed load plus a predicated negate per RANDOM access. And the
header already records the crucial cost-model point for the iteration case —
"iteration-context reads are canonical by construction and bypass canon_access
entirely". A loop over a restricted view can simply be emitted in pg order, so
in bulk compute the view is **literally free**, exactly as `SymIdx` compaction
is. The `zero` out-parameter of `canon_fold` has no analogue here (a signed
permutation never annihilates a cell), but see §4.3 — it does have one in the
general orbit case.

The tables are O(total_dim) and static (specs are `let static`), so they bake
alongside the CG tables the ML elaborator already emits as constant arrays.

### 3.4 What the license must NOT be

`Unify.fs`'s `indexPairIncompatible` currently makes an irreps space and a
pg-irreps space unconditionally incompatible ("an irreps space is never a
pg-irreps space regardless of extent"). **Surface (ii) must not be implemented
by relaxing that arm.** If the unifier simply accepts the coercion, there is no
node in the IR at which the sign table can be applied, and the program silently
reads unsigned data through a signed view — corpus 099's exact failure, only now
invisible. The license belongs where `comm`'s license lives (a signature-level
retyping permission that the pin grants) and its EFFECT must be to insert a real
view node carrying the baked table.

That collapses the §6.3 gap considerably: **even the "implicit" surface needs
the same materialized view the "explicit" surface has.** The residual difference
between (i) and (ii) is only *who writes the call* — the user, or the checker
under a pin.

### 3.5 The Coq obligation

The precedent is `BladePointGroup.v`, whose mandate is "all computational over
the witnesses" — the tables are data, the theorems are checked by computation
over concrete matrices. A `BladeRestrict.v` in the same style would carry, for
each shipped (group, embedding, l) witness:

```coq
(* the view is a signed bijection *)
restrict_perm_bijective   : forall i, i < n -> unpi (pi i) = i  /\  pi (unpi i) = i
restrict_perm_range       : forall i, i < n -> pi i < n
restrict_sign_pm1         : forall i, i < n -> sg i = 1 \/ sg i = -1

(* the view INTERTWINES the two layouts -- the load-bearing statement *)
restrict_intertwines      : forall g i j,  In g (gens G) ->
                              rho_pg  G spec g i j
                            = sg i * sg j * rho_o3 l p g (pi i) (pi j)

(* dimension closure, already asserted in F# by restrictIrrep *)
restrict_dim_preserved    : sum over spec of (mult * dimR) = 2*l + 1

(* the consumer-facing corollary, in BladeArrow's canon_access style *)
restrict_read_commutes    : forall v i, read_pg (view v) i = sg i * read_o3 v (pi i)
```

Two structural notes.

* Generators suffice for `restrict_intertwines`: conjugation is multiplicative
  and `BladeWordClosure.v` / `BladePointGroup.v` already carry the
  word-closure and `rep_property` lemmas that lift a generator statement to the
  whole group. `MLPointSpec.certifyPointGroup` uses the same argument for the J
  identities.
* The obligation is a *signed* bijection, which is new relative to
  `BladeMixedRadix.v`'s `mixed_radix_bijection` (rank/unrank round-trip, no
  signs) but NOT new relative to `BladeArrow.v`, whose Antisym instance already
  carries the strict left-justified storage bijection `alj/aunlj` alongside a
  swap parity. The task's premise — "the SymIdx precedent carries no signs, but
  antisym compact storage does" — is correct and is exactly the file to model on.

---

## 4. (c) Condensed storage for equivariant OBJECTS, more generally

The `SymIdx` analogy is stronger than restriction alone, and Blade already ships
two instances of it without naming them as such.

### 4.1 `derive_linear` — Schur scalars as a condensed weight layout (SHIPPED)

`MLElaborate.deriveLinearDecl` stores an equivariant linear map not as a dense
matrix but as one scalar per `(input block, output block, mult_out, mult_in)`
cell — the `MLSpec.homBlocks` pair-major layout — sized
`Σᵢ mᵢ·nᵢ` at O(3) and `Σᵢ mᵢ·nᵢ·eᵢ` at a point group (`derivePgLinearDecl`,
where the FS correction becomes visible and a cell carries the `[Id]` or
`[Id, J]` End-basis).

The condensation is not marginal. MEASURED with the shipped compiler on
`sh_spec(L)`:

| spec | total_dim | dense matrix entries | `linear_weight_dim` | ratio |
|---|---|---|---|---|
| sh_spec(1) | 4 | 16 | 2 | 8× |
| sh_spec(2) | 9 | 81 | 3 | 27× |
| sh_spec(4) | 25 | 625 | 5 | 125× |
| sh_spec(8) | 81 | 6561 | 9 | 729× |

(For `sh_spec(L)` the pattern is `(L+1)²` dimensions, `L+1` weights, hence
`(L+1)³`.)

**But the obligation shape is DIFFERENT from `SymIdx`'s, and this matters for
the general discipline.** `SymIdx` compaction is a bijection between stored
cells and the canonical tuples of the *same* array — rank/unrank, `BladeDMWF`'s
`lj_correct/unlj_correct`, `BladeMixedRadix`'s `srank/sunrank`. Schur-condensed
weights are a bijection between stored scalars and a linear SUBSPACE of the
dense matrices. Its obligations are INDEPENDENCE and COMPLETENESS, not
round-trip — and the tower already names them that way:
`BladePointGroup.v`'s `c4E_end_gram_is_d_id` ("Gram = d·I exactly —
independence with no rank decision") plus "End-completeness cited,
oracle-discharged".

So the general discipline has two sub-kinds, and they should not be conflated:

| kind | what is condensed | obligation | tower file |
|---|---|---|---|
| **index compaction** | which cells of a value are stored | rank/unrank bijection (+ sign, + zero-guard) | BladeDMWF, BladeArrow, BladeMixedRadix |
| **basis condensation** | a coordinate system on an equivariant SUBSPACE | independence + completeness of the emitted basis | BladePointGroup (Gram, End-completeness) |

Restriction-as-a-view is squarely in the first kind. `derive_linear` is squarely
in the second.

### 4.2 `derive_sym_tp` — where the two kinds already meet (SHIPPED)

`MLElaborate.deriveS2TpDecl` splits the self-tensor-product weight space into
its S₂-symmetric and S₂-alternating halves and emits only the KEPT paths, folding
each dropped mirror path into its partner against the same baked CG entry. The
license is the cross-block CG exchange identity
`realCG(l2,l1,l3)[m2,m1,m3] = σ·realCG(l1,l2,l3)[m1,m2,m3]`, and
`s2TpSplitIsPartition` is asserted on every synthesis.

This is the symmetry × equivariance intersection at full strength, and note the
shape: a PARTITION check (first kind) whose license is an equivariance identity
carrying a SIGN σ (second kind). It is the closest existing thing to what a
general "equivariance pin licenses a condensed layout" rule would look like.

### 4.3 G-invariant tensors by orbit representatives — NOT SHIPPED, and the
### zero-guard is already the right primitive

Storing a G-invariant tensor by orbit representatives is the natural next
member. The accessor would be `(orbit representative, sign, zero-flag)` — and
`canon_fold`'s signature is *already exactly that*:

```cpp
int canon_fold(std::array<size_t,R>& idx, bool strict, bool& zero);
```

The `zero` out-parameter exists because a strict (antisymmetric) group must
store 0 wherever two indices coincide. The general statement is the same one:
**an orbit whose stabilizer contains a sign-reversing element must store zero.**
The antisym diagonal is the R = 2, G = S₂ instance of that rule. So the runtime
primitive generalizes without change; what is missing is the ranking.

Two cautions, both ARGUED rather than measured, and both pointing the same way:

* Orbits under a general G have stabilizers of varying size, so the cell count
  is a Burnside sum, not a product of binomials. `BladeMixedRadix.v`'s positive
  result is explicitly about DISTINCT identity groups composing mixed-radix, and
  `BladeCounting.v`'s `counting_general_C` is the negative result inside ONE
  group. Orbit storage under a non-permutation group is neither of those cases,
  and there is no reason to expect a closed-form rank.
* Absent a closed-form rank, the honest implementation is `PlaceTabulated` (a
  runtime table, like `CompoundIdx`), not a new closed-form placement.

### 4.4 Where Blade already refuses to pretend — `sym_lift`

Corpus 036 rejects `ml.sym_lift` on a rep-typed argument because its output is
the degree-K monomials of the input in the MONOMIAL basis, and "the change of
coordinates between them is exactly what stage 2's derive_poly still has to
bake". That is precisely the "genuine computation, not a bijection" case, and
the shipped answer is an explicit refusal plus a named explicit op. **Blade
already applies the rule this exploration is proposing** — bijection ⇒ view,
change of basis ⇒ explicit op — it just has not stated it as a rule.

### 4.5 A candidate statement of the discipline

> An equivariance pin licenses a condensed layout exactly when the compiler can
> exhibit, at compile time, a *signed index bijection* between the dense
> coordinates and the condensed ones, together with a zero-set. Where it can,
> the condensation is a VIEW (no data movement, free in iteration, one lookup +
> one sign on random access). Where it can only exhibit an independent spanning
> BASIS of an equivariant subspace, the condensation is a REPRESENTATION CHANGE:
> it is still sound and still condensed, but it is a real map with its own
> emitter, and the surface must show it.

Restriction inside the aligned axial class is the first kind. Restriction
outside it, `sym_lift`, and `derive_linear`'s weight buffer are the second.

---

## 5. The claim side is NOT a bijection, and that is on-thesis

Worth stating explicitly because §0's data/claim split predicts it and the
measurement confirms the split is real:

* the DATA side of restriction is invertible — `P` is a signed permutation, `P⁻¹
  = Pᵀ` exists, and the O(3) buffer is recoverable from the pg buffer exactly;
* the CLAIM side is strictly one-way. Restriction forgets parity entirely (a
  proper subgroup contains no improper element; corpus 096 §3 makes a
  pseudoscalar into a genuine C4 invariant) and forgets every rotation outside
  the group. Corpus 098 is the rejection that keeps the arrow one-way.

So the bijection argument licenses a free re-VIEW of the buffer; it licenses
nothing at all about upgrading a point-group certificate to an O(3) one. A
design that lets the view be implicit must keep 098's rejection exactly as it
is.

---

## 6. (d) Recommendation

### 6.1 The matrix

| subgroup relationship | is the layout change a bijection? | evidence | surface the analysis recommends |
|---|---|---|---|
| **O3 → SO3** | It is the IDENTITY. Same layout, same spec, same bytes; only the claim weakens. | trivial — no index type changes at all | **Implicit license (ii)**, as today. There is nothing to make explicit. |
| **O3 → pg, aligned axial class** (C_n, S_2n, C_nh, C_nv, D_n, D_nd, D_nh in standard orientation — includes both shipped groups, and 27 of the 32 crystallographic groups) | YES — an exact signed permutation of indices. | MEASURED: C4/D4, all l ≤ 8, verified against `pgElementMatrix`, residual ≤ 8.9e-16, signs required. COORD-IRR for 12 aligned axial embeddings. Argued uniform in l. | The bijection justification **does** transfer from `comm`/`SymIdx`. But see §6.2 and §6.3 — recommend **explicit op (i) as the mechanism**, with (ii) available as a checker-inserted convenience only if §6.3's sizing objection is answered. |
| **O3 → pg, non-axial (T, Td, Th, O, Oh, I, Ih), l ≤ 2** | YES as a permutation, but the pg table needs irrational entries. | MEASURED: COORD-IRR yes, SPERM no at l = 2 | Moot today — blocked on the coefficient ring, not on the surface. Whichever surface the axial class gets. |
| **O3 → pg, non-axial, l ≥ 3** | **NO.** Genuine orthogonal mixing across m-pairs. | MEASURED: T, O, I all first fail at l = 3 | **Explicit op (i), mandatory.** An implicit license here would hide a real dense change of basis behind a type coercion. |
| **O3 → pg, misaligned/rotated embedding** | **NO**, from the first split m. | MEASURED: C4 about (1,1,1) fails at l = 1; D4 at β = 22.5° at l = 2; alignment rule 32/32 | **Explicit op (i), mandatory.** |

### 6.2 The answer to §6.4 question 4

Neither "one principle for all relationships" nor "a per-pair judgement call".
**The criterion is uniform and mechanical, and the verdict is per-relationship
DATA the registry can compute.**

Concretely: `certifyPointGroup` already runs six integer-exact integrity
families on every registry fetch. Add a seventh — the COORD-IRR test plus the
signed-permutation search of §1 — run per (group, embedding, l) up to the
registry's l cap, and freeze the resulting (π, σ) tables beside the character
table. The test is cheap (|G| ≤ a few dozen, l ≤ 8, exact integer arithmetic for
the SPERM groups) and it is exactly the same kind of thing the file already
does: `restrictIrrep` ALREADY cross-checks the reconstructed character at every
element through the emitted layout. This adds the change of basis to what is
already being checked.

Then the surface follows from the data:

* the view table exists ⇒ `ml.restrict` compiles to a re-view (zero data
  movement; free in iteration; one lookup + one sign on random access), and the
  implicit license is *available* to the design;
* the view table does not exist ⇒ `ml.restrict` compiles to a real change of
  basis, and the implicit license is refused with a diagnostic that names the
  first l at which the group mixes ("O has no signed-permutation view of an
  l = 3 block; T1u and T2u are fixed combinations of the m = ±1 and m = ±3
  pairs").

That diagnostic is worth having on its own. It is the honest version of corpus
048's current message.

### 6.3 The argument that survives the bijection finding — and it is not about cost

If the recommendation stopped at §6.1 it would favour surface (ii) inside the
axial class. One objection does not dissolve, and it is independent of whether
the view is free:

**The restricted spec is not a function of the input type alone, and it changes
the size of a buffer the user has to supply.** `pg_restrict("C4", sh_spec(2))`
is `[A×3, B×2, E×2]`; `pg_restrict("D4", ...)` is something else; and corpus 097
§4 pins the consequence — the same l = 2 space has a 7-dimensional equivariant
endomorphism algebra under C4 and a 4-dimensional one under D4. Under surface
(ii), a user calling a C4-certified layer with an O(3)-typed value must size a
weight buffer with `pg_hom_dim` against a spec produced by a restriction they
never wrote, against a group chosen by the callee. The cost of the view is zero;
the cost of the *invisibility* is a number the user cannot derive from anything
in their source.

This is the same concern review decision 1 raised about folding the CLAIM into
the unifier, but it lands with more force here, because it is not about
diagnostics quality — it is about a buffer length. `sym_lift` and the other
compaction-adjacent primitives are written explicitly for exactly this reason.

### 6.4 What I would recommend the user decide

**Surface (i) — explicit `ml.restrict` — as the shipped mechanism, on the
strength of §6.3, not on the strength of the cost argument, which the
measurement retires.** Three consequences worth taking with it:

1. **Rewrite the *reason* in corpus 048 / 096 / 099** while keeping their
   verdicts. The current headers say the decomposition is not a reinterpretation
   and that "no ORDERING of the restricted spec repairs it" — both true, and
   they already say "(with signs)". What they should now also say is that the
   repair EXISTS, is a signed permutation, has been verified against
   `pgElementMatrix` at every l ≤ 8 for both shipped groups, and that the
   rejection stands because the view has not shipped, not because it cannot
   exist. That is a stronger rejection, not a weaker one: it names what would
   discharge it.
2. **Implement `ml.restrict` as a VIEW where the table exists**, not as a
   materializing change-of-basis. It is a `PlaceSignedPermuted` accessor over
   the existing `canon_fold`/`canon_transform` pipeline, free in iteration
   context. Surface (i) does not have to mean data movement, and the corpus
   should pin that it does not (e.g. a restrict-then-restrict-back identity
   test, and an aliasing test).
3. **Compute the verdict at registration**, per §6.2, so that the ℚ(√3) and
   cubic growth steps arrive with the right surface behaviour already decided
   rather than needing a fresh design round each time.

Surface (ii) can then be added later as pure sugar — a checker-inserted
`ml.restrict` under an `ml.equiv(O3)` pin — once someone answers §6.3's sizing
question (most plausibly by making the restricted spec nameable at the call
site, e.g. requiring the weight buffer to be typed at `ml.pg_hom_dim(G,
ml.pg_restrict(G, S), ...)`, which puts the number back in the user's source
without putting the change of basis there).

---

## 7. Open items / what a follow-up would need

* **Not measured:** l > 8; double groups (`FsQuat`); improper *misaligned*
  embeddings (C4v with mirrors at 22.5° — expected to fail by the same rule, not
  checked); the complex-basis pipeline.
* **Not measured:** whether the signed-permutation table for a group can be
  chosen consistently across l in a way that makes it *generated* rather than
  tabulated per l. The maps in §2.1 look regular (the A/B labels take the
  m ≡ 0 mod n classes, the E copies take the pairs in |m| order, signs land on
  the m < 0 member of the pairs the table's R₉₀ orientation disagrees with), but
  I did not try to prove a closed form. If one exists the table is O(1) rather
  than O(total_dim).
* **Suite run needed:** none for this exploration — no compiler code changed.
  If the corpus header rewrites of §6.4(1) are taken up, the ml-equiv suite
  should be run then.
* The probes live in this session's scratchpad
  (`probe1..probe5.fsx`, `shcore.fsx`); §1 states the method in enough detail to
  rebuild them.
