# Plan: Transforms-As Types ("representation theory as a type discipline")

> **Status:** design / plan. Speculative tier — not canonical spec. Extends the
> shipped `where ml.equiv(G)` / `where sgs.galilean(...)` disciplines and the
> `derive_linear`/`derive_tp` synthesis seam. The deduction axis here is the
> fourth lattice of the deduction triad, which is now IMPLEMENTED
> (src/Deduce.fs; plan doc: [plan-implicit-formers-and-deduction.md](plan-implicit-formers-and-deduction.md)).
> Canonicality order still applies: Coq proofs > formalism > compiler > this note.
> **Date:** 2026-07-26. Every file:line cite verified against the tree at
> commit `18b1f06` (post-deduction-triad, post-scope-exit-frees).

## 1. Goal

Reach a surface where an equivariant network layer is **declared, not
written** —

```blade
let static SIN  = [(0, 0, 8), (1, 1, 4)]      // 8 scalars, 4 vectors; dim 20

function layer(w, x) where ml.equiv(O3) = ml.derive_poly(SIN, 2, x, w)
// layer : (w: Array<Float like Idx<W2>>, x: Array<Float like IrrepsIdx<SIN>>)
//         -> Array<Float like IrrepsIdx<sym_spec(SIN, 2)>>
//   W2 = ml.poly_weight_dim(SIN, 2) = dim Hom_G(Sym²(V_in), Sym²(V_in))
```

with **no** CG path enumeration, **no** hand-derived weight layout, **no**
mirror-path redundancy, and **no** training-time equivariance test — yet where
the weight count is provably the exact dimension of the admissible hypothesis
space.

The honest target is **not** "the compiler writes your network." It is:

> **You declare what the layer must respect; the compiler writes the complete
> basis of everything that respects it — and the parameter count *is* the
> theorem.**

Today `derive_linear` does this for degree 1 — **and, as of 2026-07-27,
`ml.derive_poly` does it for degree k ≤ 4** (§7 stages 2b-i/ii/iii; the §1
sketch above predates the surface decision — the shipped form takes SOUT
explicitly: `ml.derive_poly(SIN, K, SOUT, x, w)`). The design identified the
machine that makes degree k possible as one already in the tower:
**triangular symmetric storage**. The thesis in one line: the
discrete symmetry system (`SymIdx`, Coq-proved) and the continuous
equivariance system (irreps, Schur synthesis, F#-only) are the same
construction — isotypic projection — and their fusion point is the symmetric
power.

## 2. Background: what is already true

### 2.1 Schur synthesis is shipped at degree 1

- `Blade.ML.Spec.homBlocks` / `homDim` (src/ml/compiler/MLSpec.fs) give the
  complete Schur basis of `Hom_G(V_in, V_out)` as (input, output) block pairs
  of equal `(l, parity)`, dimension `Σ_{(l,p)} m_in·m_out`; the F# reference
  twin is `homLinear` (src/ml/Linear.fs:84-105, "the unique equivariant
  completion" — unmatched output blocks stay exactly zero).
- `deriveLinearDecl` (src/ml/compiler/MLElaborate.fs:316) emits exactly that
  basis as ordinary Blade source, so `grad`, symmetry inference, and fusion
  apply to it unchanged.
- `homDim = 0` is a **compile error** (BL4007 "no equivariant map exists",
  src/Diagnostics.fs:146): "every admissible map is zero" is a *design* error,
  not a silent training failure.

### 2.2 `derive_tp` is Schur-complete for bilinear maps — and only that

`tpWeightDim` (MLSpec.fs:95-98) sums `multOut·m1·m2` over `tpPaths`. Because
SO(3) tensor products are multiplicity-free (`dim Hom_G(V_{l1}⊗V_{l2}, V_{l3})
= 1` under the triangle rule), one scalar weight per `(path, mo, u1, u2)` is a
complete basis of `Hom_G(V⊗V, W)`.

What is missing: **no `spec1 = spec2` exploitation**. Mirror paths
`(b1,b2,bo)` and `(b2,b1,bo)` get separate weight slots (`tpDecl`,
MLElaborate.fs:146-223), and the CG exchange identity
`C[m1,m2,m3] = (−1)^{l1+l2−l3} C[m2,m1,m3]` is pinned as a **test**
(src/ml/Tests_Wigner.fs:81-93) and never used for compression.

### 2.3 "Transforms-as" is already a family, not one feature

Two independent certification lattices ship, registered through the same
extension point (`Blade.Constraints.registerConstraint`):

| Discipline | Module | Lattice | Group |
|---|---|---|---|
| `where ml.equiv(G)` | MLEquiv.fs | `{Rep spec, Inv, Opaque}` | O(3)/SO(3): compact, irrep-based |
| `where sgs.galilean(u,…)` | MLGalilean.fs | `{BVar, BInv, BOpaque}` | boosts: **non-compact**, cancellation-based |
| `where ml.perm_equiv(N)` (5a, designed) | MLPerm.fs | `{Pow k (k ∈ ℕ)} ∪ {Opaque}`, Pow 0 = Inv | Sₙ: finite, **permutation-module** (index-action) |

Both run at the same elaboration seam (MLElaborate.fs:901-926), and one
function may carry both certificates (corpus
`ml-equiv/026_dual_certificate.blade`). The Galilean lattice's central rule —
`BVar − BVar → BInv` (boost cancellation) — is not a Schur argument, which is
exactly right: non-compact groups have no finite-dimensional unitary irreps.
**The family predates the generalization**; user-declared groups slot into
the same registry.

The units triptych completes the framing. formalism §3.10 already calls index
units "the index-level mirror of physical units — same mechanism, same error
class." The representation reading:

| System | Group | Reps used | Composition law |
|---|---|---|---|
| Units of measure | scalings `(ℝ₊)^d` | 1-dim characters `λ ↦ λ^a` | exponents add (characters multiply) |
| `Nat<I>` index units | nominal identity | 1-dim trivial | match or error |
| `IrrepsIdx<spec>` | O(3) | `⊕ (l,p)` blocks | Clebsch–Gordan |

Units-of-measure IS transforms-as for 1-dimensional representations; a
unit-mismatch error IS `homDim = 0`. Zero code in this row — it is the pitch.

### 2.4 The discrete half is proved, and the tower already speaks plethysm

- `|SymIdx<r,n>| = C(n+r−1, r)` — proofs/BladeBinomial.v (`hockey`,
  `storage_cardinality`).
- H∩Stab lowering exactness **as an iff** — proofs/BladeCompleteness.v
  (`license_exactness`).
- `∏ⱼ C(nⱼ+r−1, r) < C(∏ⱼ nⱼ+r−1, r)` — proofs/BladeCounting.v; and
  proofs.md already records it as "the leading-term deficit of the Cauchy
  decomposition `Sym^r(V⊗W) ≅ ⊕_λ S^λ(V)⊗S^λ(W); product storage captures
  only λ=(r); per-dimension Reynolds is the projection onto it."
- The constructive r = 2 Cauchy split — proofs/BladeCauchy.v
  (`cauchy_split_access`, `cauchy_cell_count`).
- Jacobian symmetry inheritance is now also in the tower
  (proofs/BladeJacobian.v), which is what lets synthesized layers'
  *gradients* ride the same storage.

Meanwhile the continuous dimension count (`homDim`, `Irreps.fs:87-98`) exists
only as unverified F#. **The two counts sit on opposite sides of the
verification boundary; they are instances of one character-inner-product
fact.** Closing that gap is §8.

### 2.5 The rank-2 bridge already ships, and it is already Sym²

`src/ml/compiler/CartesianBridge.fs`: `tauSpec = [(0,e,1),(2,e,1)]`, dim 6,
over the packed pairs `(0,0),(0,1),(0,2),(1,1),(1,2),(2,2)`. That is
*literally* `Sym²(ℝ³) = V₀ ⊕ V₂` stored on `SymIdx<2, Idx<3>>` in lex order.
The symmetric-power bridge of §3.1 is not new machinery — it is this, at
`k = 2, V = ℝ³`, generalized to arbitrary k and arbitrary spec.

### 2.6 The deduction triad is implemented — the fourth lattice has a socket

Since this note was first drafted, the deduction triad landed
(PRs #25/#26): implicit formers at the `<@>` seam, interprocedural rank
deduction, and stage-3 symmetry deduction. The parts this plan builds on:

- **src/Deduce.fs** — pure pre-TypeCheck analysis; per-subtree parity lattice
  `{PInv, PNeg, PBottom}` driven by two per-primitive tables (swap class +
  sign behavior), with interprocedural sign-parity summaries
  (`{SOdd, SEven, SUnknown}` per parameter). Soundness bias is explicit:
  anything unrecognized collapses to PBottom = "no claim" — dense, never
  compact-and-corrupt.
- **Confirm-and-pin is real**: pin suggestions are structured diagnostics
  (BL4010 "confirm-and-pin storage suggestion", Diagnostics.fs:149), the pin
  spellings are `where comm(...)` / `where anticomm(...)`, and
  `--strict-pins` fails CI on unpinned storage-changing deductions.
- **proofs/BladeDeduce.v** discharges the stage-3 obligations — the
  adjacent-transpositions-generate-Sₙ argument is now machine-checked.

This changes the status of §3.5 below from "mirror a sibling plan" to
"add a fourth lattice to a shipped framework with a checked proof pattern."

### 2.7 The three seams (verified at HEAD)

| # | Seam | Current state |
|---|---|---|
| S1 | Parser.fs:756-763, `parseIndexType` `KwSymIdx` arm | second argument is `parseSimpleExpr` — an **int expression only**; cannot parse an index type |
| S2 | TypeCheck.fs:590-592, `lowerIndexType` `TySymIdx` arm | hardcodes `Tag = None; IxKind = IxKPlain` (value-position twin at :475-478) |
| S3 | IR.fs:1338-1381, `fuseJointSLevels` | eligibility requires `IxKind = IxKPlain` per factor (:1366); fused record discards `Tag` (:1374) |

Downstream is provenance-agnostic: `IrrepsIdx` is IR-shape-identical to `Idx`
(Rank = 1, scalar `Extent = totalDim(spec)`, spec carried in `Tag`,
`IxKind = IxKIrreps`), classified dense; the canonicalize/left-justify and
fused row-major decode paths in CodeGen read extents at **runtime** from
`.extents[...]`.

**Stronger than "three seams" (read, not yet executed — verify in stage 3):**
the IR *target record* is already reachable. `deduceOutputType`'s size-≥2
group path (IR.fs:2126) builds `{ rep with Rank = groupRank; Symmetry =
groupSymmetry }` — **`Tag` and `IxKind` survive verbatim** — and the
symmetry-vs-kind classification tests `Symmetry` first, while cross-argument
grouping does not consult `IxKind` at all. So `SymIdx<2, IrrepsIdx<s>>` is
already *representable*, and plausibly already *inferred*, for `comm` over
two identical rank-1 irreps-typed arrays. The seams block **writing** the
type, not representing it. (The multi-axis compound path, IR.fs:2091-2096,
sets `Tag = None` — correctly: a batch × irreps product is not an irreps
space.)

## 3. The mechanism

### 3.1 The symmetric-power bridge (the centerpiece)

Over a field of characteristic 0:

> degree-k homogeneous polynomial maps `V → W`
> ≅ symmetric k-multilinear maps `V^k → W`
> ≅ linear maps `Sym^k(V) → W`

and, taking G-fixed vectors (exact for compact G in char 0):

> **degree-k homogeneous equivariant polynomial maps `V → W`
> ≅ `Hom_G(Sym^k V, W)`.**

Polarization/restitution costs a `1/k!`; the char-0 hypothesis is where it
lives. Blade computes over Float — char 0 as algebra; the numeric caveat is
§6.5.

**The storage identification.** For a basis `e₁…e_n` of V, `Sym^k(V)` has
basis `{e_{i1}···e_{ik} : i1 ≤ … ≤ ik}` — canonical multisets,
`C(n+k−1, k)` of them. With `n = total_dim(spec)`, that is *literally*
`SymIdx<k, IrrepsIdx<spec>>`, whose cardinality is the Coq-proved
`storage_cardinality`. Therefore:

```
derive_poly<k>(spec_in, spec_out)  ==  derive_linear(sym_spec(spec_in, k), spec_out)
                                       ∘  sym_lift<k>          // x ↦ its symmetric power
```

**The discrete symmetry system becomes load-bearing for continuous
synthesis** — storage AND correctness, not an optimization. The graded
ladder:

| k | `Hom_G(Sym^k V, W)` | is |
|---|---|---|
| 0 | `W^G` | the invariant bias vector |
| 1 | `Hom_G(V, W)` | **`derive_linear` — shipped** |
| 2 | `Hom_G(Sym²V, W)` | `derive_sym_tp` (stage 1) |
| k | — | `derive_poly<k>` (stage 2) |

**The k = 2 wedge is four known objects at once.** `V⊗V = Sym²V ⊕ Λ²V` is
simultaneously (i) the S₂ split of `derive_tp`'s mirror-path weight space,
(ii) the r = 2 Cauchy split already in the tower (BladeCauchy.v), (iii)
`SymIdx<2,·>` vs `AntisymIdx<2,·>` storage, and (iv) the shipped
`sym_to_irreps` bridge (§2.5). Four names, one object.

### 3.2 The S₂ compaction of `derive_tp` — exact counts

Let `spec1 = spec2 = s`. For a valid path `(b1, b2, bo)` write
`σ = (−1)^{l_{b1}+l_{b2}−l_{bo}}` (the exchange sign).

> **Compaction rule.**
> - **b1 < b2 (mirror pair):** keep one path, drop its mirror. The
>   S₂-symmetric subspace of the pair has dimension `multOut·m_{b1}·m_{b2}` —
>   exactly half the pair. (Λ² takes the other half.)
> - **b1 = b2 = b (diagonal):** here σ = `(−1)^{l_bo}` depends only on the
>   output l. The `m×m` weight matrix per `mo` is constrained σ-symmetric:
>   dimension `multOut · m(m+σ)/2`; the Λ² part takes `multOut · m(m−σ)/2`.

Worked count 1 — `s = [(0,e,1), (1,o,1)]` (dim V = 4, `tpWeightDim = 10`):

| path | l_bo | σ | dense | Sym² | Λ² |
|---|---|---|---|---|---|
| (0,0,0) | 0 | + | 2 | 2 | 0 |
| (0,1,2) ∥ (1,0,2) | 1 | − | 2+2 | 2 | 2 |
| (1,1,0) | 0 | + | 2 | 2 | 0 |
| (1,1,1) | 1 | − | 1 | **0** | 1 |
| (1,1,3) | 2 | + | 1 | 1 | 0 |
| | | | **10** | **7** | **3** |

Independent cross-check by Schur (no path model):
`Sym²(V) = 2·L0e ⊕ 1·L1o ⊕ 1·L2e` ⇒ `dim Hom_G(Sym²V, tp_spec) = 2·2+1·2+1·1
= 7` ✓; `Λ²(V) = 1·L1o ⊕ 1·L1e` ⇒ 3 ✓; 7+3 = 10 ✓.

Worked count 2 — `s = [(1,o,2)]` (multiplicity 2, dim V = 6,
`tpWeightDim = 48`): per output l = 0,1,2 the rule gives 12+4+12 = **28**.
Cross-check by the Cauchy formula `Sym²(A⊗U) = Sym²A⊗Sym²U ⊕ Λ²A⊗Λ²U` (this
IS BladeCauchy): `Sym²(V) = 3·L0e ⊕ 1·L1e ⊕ 3·L2e` ⇒ 12+4+12 = 28 ✓.

Worked count 3 — `s = [(0,e,2), (1,o,1)]`: dense 43, Sym² 29, Λ² 14; both
routes agree.

**What this buys, honestly:**
- Not a halving: 10→7 (30%), 48→28 (42%), 43→29 (33%). The 1/2 is asymptotic
  in multiplicity (`ratio = [(2l+1)m+1]/[2(2l+1)m]`); real specs
  (m ∈ [2, 64]) see 33–49%.
- **Correction to [equivariant-nn.md](features/equivariant-nn.md) §9:**
  "antisymmetric paths vanish" on self-TP holds only at multiplicity 1. At
  m > 1 the m-antisymmetric coupling pairs with the antisymmetric
  multiplicity component (the Λ²⊗Λ² Cauchy term) and **survives** — in count
  2 it contributes 4 of the 28. The module doc now carries this correction.

Closed form at k = 2 (no character machinery):
`Sym²(V_l) = ⊕_{j=0}^{l} V_{2j}`, `Λ²(V_l) = ⊕_{j=1}^{l} V_{2j−1}`,
`Sym²(⊕ᵢWᵢ) = ⊕ᵢSym²(Wᵢ) ⊕ ⊕_{i<j} Wᵢ⊗Wⱼ`, and on tensor blocks the Cauchy
formula above. Parity: even throughout on a diagonal block.

### 3.3 `sym_spec(spec, k)` for general k — integer arithmetic, no Wigner

O(3) ≅ SO(3) × {±I}, so an irrep is `(l, parity)` and parity is an
independent Z₂ grading. Algorithm (all integer):

1. **Weight table**: each block `(l, p, m)` contributes m copies of the
   weight multiset `{−l..l}` in parity sector p.
2. **Sym^k = size-k multisets**: maintain `f[0..k]` as Z₂-graded Laurent
   polynomials in q; for each entry do
   `for j = 1..k: f[j] += f[j−1]·q^w·z^p` (unbounded-knapsack update ⇒
   multisets). Cost `O(total_dim · k · width)`.
3. **Peel**: per parity sector, repeatedly take the highest weight L with
   multiplicity c, emit `(L, p, c)`, subtract c copies of `{−L..L}`.

Free cross-checks at every k: `k = 1` reproduces spec; `k = 2` reproduces
§3.2's closed form; `total_dim(sym_spec(s,k)) = C(total_dim(s)+k−1, k)` — the
Coq-proved cardinality, asserted at elaboration.

Real-vs-complex: this computes the complex decomposition; it is valid over ℝ
because every O(3) irrep has Frobenius–Schur indicator +1 (real type). **That
hypothesis is exactly what fails for general finite groups** — §3.6, §6.2.

### 3.3b The Sym^k label basis (stage 2b design, settled 2026-07-26)

The canonical parameterization of `Hom_G(Sym^k V, W)` behind `derive_poly<k>`,
k ∈ {2, 3, 4} — ONE convention for every k (`derive_sym_tp`/`derive_alt_tp`
remain the separate binary ops with their kept-path layout). Two moves make
it exact:

**Move 1 — copy-splitting.** Split every spec entry (l, p, m) into m copies
of (l, p, 1) and factor over degree-compositions of k across copies:
`Sym^k(⊕_c U_c) = ⊕_{Σk_c=k} ⊗_c Sym^{k_c}(U_c)`. Only single-row plethysms
`Sym^j(V_l)`, j ≤ 4, ever appear (no Schur functors, no SSYT layer); cross-
copy coupling is pairwise CG — multiplicity-free at every step; sectors are
orthogonal subspaces. The missing-label problem is confined to a universal
table family `T_{j,l}` (generated-and-cached per (j, l), like CG tables).

**Move 2 — exact rational T-tables.** In the divided-power basis the sl₂
raising operator E has integer matrix entries on degree-j monomials; the
V_L-multiplicity space of Sym^j(V_l) is ker E ∩ (weight L), computed over ℚ
(fraction-free), with **RREF in lex monomial order** as the occurrence
labeling (unique given column order; pivot monomials are the documented
artifact). Rational Gram–Schmidt within each multiplicity space (dims ≤ 3
at k ≤ 4; independence already proved by pivots — no rank decisions),
integer lowering, diagonal unitarization, real-ization via uMatrix. Floats
appear only evaluating an exact object — the same status as realCGDense.

**Label** = (sector: nondecreasing copy-multiset, lex order — reproduces
stage 1's kept-cell enumeration at k = 2; per-copy occurrence in
**L-descending, RREF-index-ascending** order — exactly the order
symPowerTable emits, so a label's occ is a direct T-table index; left-comb
intermediate L's ascending; output (L,P) ↦ W-copy per homBlocks layout).
Enumeration significance is **blocked**: the odometer is
(occ₀…occ_{r−1}, chain₁…chain_{r−1}), rightmost fastest, every occurrence
choice outranking every chain choice (nested, not product — chain ranges
depend on the occurrences). k = 0 is the single trivial label. The emitted
basis is globally ORTHONORMAL by construction: T rows orthonormal per copy,
CG chains unitary, sectors orthogonal. (Convention details fixed at 2b-ii;
MLSpec.polyLabels' header comment is the normative record.)

**The evaluation identity** (the §6.6 convention, made precise; ⟨·,·⟩ = the
inner product with orthonormal {e_i}, s_I = the unit-coefficient sum of the
N_I arrangements of cell I):

1. v_label = √(k!/∏_c k_c!) · P_sym(chain-coupled ⊗_c v_{r_c}); the
   cross-copy multinomial appears here and only here. The sector constant
   is NOT a free normalization: it is exactly what makes the emitted basis
   orthonormal (verified at 2b-iii: dropping it deviates the Gram by
   0.5–0.83 and collapses the k=2 M-ratios √2→1); it is per-sector.
2. feature_label(x) = ⟨v_label, x^⊗k⟩ = Σ_I T[label, I]·∏_j x[i_j] with
   **T[label, I] = ⟨v_label, s_I⟩** — evaluation carries no N_I factor.
3. Orthonormality in table terms: Σ_I T[r,I]·T[r′,I]/N_I = δ — the
   multinomial lives in the Gram identity, not in evaluation. Equivalently
   (made explicit at 2b-iii, since every independent consumer re-derived
   it): the ORTHONORMAL FRAME is ŝ_I = s_I/√N_I, and the coordinate in
   which the invariant inner product is a plain dot is T[·, I]/√N_I —
   "real monomial coordinates" means this frame. Two further conventions
   consumers need, normative in SymPowerTables.fs's doc-comment: the
   equivariant monomial↔tensor identification is mono_A ↦ (∏αᵢ!)·s_A, and
   the divided-power basis is w_m = c_m·|l,m⟩ (that direction — the other
   choice flips E's coefficient to (l+m+1)).
4. Cells split uniquely across copies, so feature_label(x) =
   √(k!/∏k_c!) · [CG-chain of per-copy features] — every global monomial
   counted exactly once; the runtime never symmetrizes (P_sym is
   self-adjoint and fixes x^⊗k).

**Phase rule (DERIVED in 2b-i — conjecture confirmed):** the complex table
for V_L ⊂ Sym^j(V_l) is real iff j·l + L is even; the canonical realization
multiplies by −i exactly in the odd case. Derivation against the coded
conventions lives as SymPowerTables.fs's module doc-comment; the key move:
coefficient conjugation K in the divided-power basis factors as
K_S = (−1)^{j·l}·J_S∘R_S with R_S a GROUP element, so conjugation preserves
each occurrence copy — multiplicity spaces cannot mix, and
conj(T_real) = (−1)^{jl+L}·T_real follows copy by copy. At j = 2 this
reduces to the shipped realCG parity rule; V₃ ⊂ Sym³(V₂) realized −i as
predicted. The gapped guard remains as a compiler-bug assert (observed
gap ~16 orders); parity never enters (O(3) parity acts by (−1)^{j·p} at
spec level).

**k = 2 correspondence (stage 1 as oracle):** both conventions decompose the
weight space into the SAME 1-dim lines, via the THREE-row alignment
(corrected at 2b-ii — the two-row form under-counted at multiplicity > 1):
kept mirror cell ↔ two-distinct-copy sector across blocks; diagonal-path
cell with u1 < u2 ↔ two-distinct-copy sector within a block (covers BOTH τ
signs, all L in 0..2l); σ=+1 diagonal cell with u1 = u2 ↔ same-copy sector
× even-L T_{2,l} occurrence. Count-level bijection verified on the anchors
(2b-ii); value-level bijection + M verified at 2b-iii: M is a label-aligned
scaled permutation with ratio multiset exactly **{+1, +√2}** (the earlier
"{1, √2, 2}" overstated — 2 cannot occur: mirror and diagonal-u1<u2 cells
both give √2, derived not fitted; u1=u2 cells give |1|, and the observed
sign is uniformly + — |1| is derived, the + sign observed-not-derived on
every even-L case tested). Cross-pairs orthogonal at ~1e-15.

**Prefix-stability, stated honestly (the achievable guarantee, which k = 1
and k = 2 already exhibit):** extending the spec never changes an old
label's vector AS A MAP and never re-mixes old vectors with new ones;
positional offsets shift, as they already do for derive_linear and tpSpec.

### 3.4 Identity groups ARE symmetric powers

formalism §8.1 already writes the arity-polymorphic moment kernel; for a
**multilinear** kernel over an identity group of k rep-typed arrays, H∩Stab
puts the coefficient tensor on `SymIdx<k,·>`, and the map it denotes is a
linear map on Sym^k. Restitution (all arguments identical) yields the
degree-k homogeneous map. So:

> **`poly(...)` + `comm` + identity group over `IrrepsIdx`-typed arrays
> = `derive_poly<k>`.** The arity-polymorphic surface and the symmetric power
> are the same object seen from the value side and the type side.

This closes equivariant-nn.md §12 open item #6 (poly × equivariance) — for
the multilinear fragment. (A general comm kernel, e.g. `max(a,b)`, is
symmetric but not multilinear; no claim is made for it.) It is also the ACE/
MACE body-order construction arrived at through H∩Stab: the §5 example's
`Sym³` weight space is what that literature calls the 3-body basis.

### 3.5 Generator-based certification — the continuous twin of Deduce.fs

The implemented symmetry deduction rests on: **Sₙ is generated by adjacent
transpositions** — check n−1 pairs, get n! permutations (now machine-checked,
BladeDeduce.v). The continuous analogue:

> A polynomial map commuting with the dim 𝔤 Lie-algebra generators is
> equivariant under the whole **connected** group.

For SO(3): three generators; the condition `Df(x)·(A·x) = A·f(x)` per
generator is a polynomial identity — finite coefficient comparison. Two
mandatory caveats:

- **O(3) is not connected.** Generators certify SO(3); the parity component
  needs one extra check at −I (diagonal `(−1)^{parity_b}` per block). This is
  not a formality — it is exactly the SO3-vs-O3 distinction MLEquiv already
  enforces on pseudoscalar gates (MLEquiv.fs:471-477). Without it, generator
  deduction would *certify* programs the shipped judgment *rejects*.
- **Exactness.** Real CG coefficients are algebraic irrationals computed
  numerically (Wigner.fs:121-141). The generator check is exact only over an
  exact ring. Route chosen: restrict it to **user-written bodies with
  rational coefficients**; synthesized bases stay certified by Schur (a
  theorem, not a check). A certified-numeric variant would be a *test*, not a
  certificate.

Representation then becomes the **fourth deduction lattice**, riding the
shipped framework:

| Axis | Local rule | Lattice | Status |
|---|---|---|---|
| Rank | min rank per primitive | (ℕ, max) | shipped |
| Symmetry | swap class + sign behavior | `{PInv, PNeg, PBottom}` | shipped (Deduce.fs) |
| Arity | pack length | list length | shipped |
| **Representation** | per-op rep transformer | `{Rep spec, Inv, Opaque}` | **this plan** |

MLEquiv is already the lattice — running in *checking* mode (signature
declared, body verified). Deduction mode is the same judgment run
Deduce.fs-style (pure pre-pass, consumers decide meaning), proposing a
certificate instead of demanding one. Same confirm-and-pin economics, one
difference (§4b): an unconfirmed rep deduction costs a *guarantee*, not a
*speedup*.

### 3.6 Finite groups — the two-family taxonomy (design round 2026-07-27)

The original framing here ("character-table-driven derive_linear; Sₙ
first") was WRONG for Sₙ, in two ways the design round settled. First,
`⟨χ_V, χ_W⟩` yields counts with no basis — the house rule is that the count
is the theorem BECAUSE the basis is emitted, and a character table cannot
ship an emitting stage. Second, Sₙ's layer algebra never needs the irrep
picture: for permutation modules, `dim Hom_G(ℝ^X, ℝ^Y) = #orbits on X×Y`,
basis = orbit indicators — and the family `{ℝ^{n^k}}` is MONOIDALLY CLOSED
(⊗ and Sym^d stay inside), so the entire graded tower is character-free
with no Kronecker coefficient ever appearing (the Sₙ tensor-product problem
is multiplicity-nonfree with no closed form — permanently out of scope at
this tier, and unneeded).

Stage 5 therefore splits into TWO FAMILIES, per §2.3's own lesson that the
family predates the generalization:

**5a — Sₙ as the first INDEX-ACTION discipline** (a sibling registered like
MLGalilean; zero refactor of the O(3) member):
- `ml.derive_perm_linear(K, L, N, x, w)` over plain `Idx<N>`-power axes;
  weight dim = #partitions of [K+L] with ≤ N blocks (= Bell(K+L) at
  N ≥ K+L; v1 requires static N ≥ K+L, diagnostic naming the truncated
  variant as a deferral); basis = COARSENING INDICATORS B_γ, one
  b(γ)-deep loop nest per partition (input-only blocks sum, mixed blocks
  gather, output-only blocks broadcast — the deriveLinearDecl idiom, no
  new index machinery). `derive_perm_bias(L, N, b)` is the
  rep-introduction form; `perm_matmul` (PPGN's engine) is the one
  bilinear shipped early, BY NAME, not by synthesis.
- Canonical order: partitions by RGS (restricted growth string), lex
  ascending, input positions before output positions. The independence
  certificate is INTEGER: γ's witness tuple is its own RGS,
  `B_{γ'}(RGS(γ)) = 1 ⇔ γ' ≤ γ`, and RGS-lex extends refinement — the
  witness-evaluation matrix is unitriangular under the emission order.
  No float, no rank decision. (If the Coq order lemma resists, the
  fallback — block-count ascending, then RGS-lex — has a one-line
  extension proof; a convention swap, never a design hole.)
- The certification lattice is NEW and ℕ-graded: `{Pow k} ∪ {Opaque}`,
  Pow 0 = Inv, and its rules have OPPOSITE POLARITY to MLEquiv's at
  almost every arm (pointwise nonlinearities LEGAL — permutations commute
  with every pointwise map; elementwise products of reps LEGAL; component
  access by bound index LEGAL — the node basis is real). That polarity
  table is the argument that a parameterized `Rep of GroupSpec` payload
  would be two judgments wearing one type: sibling lattice, third
  registry member. v1: Pow k = rank-k, ALL axes node-covariant;
  mixed-axis arrays (batch × node, node × channel, node × IrrepsIdx) are
  REJECTED at the certified signature (the MLEquiv.fs:135 precedent),
  diagnostic naming per-axis status vectors as the v2 shape — the same
  upgrade that unlocks O(3)×Sₙ dual certificates; the two deferrals
  cross-reference. v1 keys covariance on the STATIC EXTENT N (a
  coincidental extent-N axis classifies covariant — the usual
  conditional-theorem reading; `Nat<Node>` nominal keying is the named
  upgrade, and the bridge to §2.3's index-units row).
- Oracle: exact rational Reynolds — `P_ref = (1/n!)Σ_σ σ^{⊗(k+l)}` and
  `B(BᵀB)⁻¹Bᵀ` over ℚ with the closed-form integer Gram
  `⟨B_γ, B_π⟩ = n^{b(γ∨π)}`; STRONGER than the O(3) oracle — no tolerance
  anywhere. Anchors: DeepSets (Bell(2) = 2, pinned vs hand-written
  a·x + b·sum(x)·1), matrix invariants (sum, trace), the Maron k=l=2
  15 (+ bias 2); integer data so equivariance pins are exact equality.
- Sizing builtins (`perm_weight_dim`, `perm_bias_dim`) error on
  N < K+L exactly as the ops do — no silent convention fork.

**5b — point groups as the second BLOCK-SPEC member (DESIGNED 2026-07-27,
second two-agent round; staging in §7).** The original mandate (label
abstraction against {O(3), point groups}, never Sₙ; FS as a stated
formula; §6.1 decided here) stands; the round settled its shape:

- **THE FS FORMULA (stated once, no direction words):** over ℝ-irreducible
  labels U_i with e_i = dim_ℝ End_G(U_i) ∈ {1 (ℝ), 2 (ℂ), 4 (ℍ)}:
  `dim_ℝ Hom_G(⊕mᵢUᵢ, ⊕nᵢUᵢ) = Σᵢ mᵢ·nᵢ·eᵢ` — each multiplicity cell
  carries e scalars; emitted basis [Id, J] for ℂ-type with J a BAKED
  per-label matrix (basis-relative data, never derived at a call site).
  Independence: two integer asserts (J² = −Id, J commutes with the baked
  generators; Gram = d·I exactly) — no rank decision anywhere.
- **Twin-not-reroute (5c's discipline again):** the abstraction is the
  tag grammar (second frozen prefix `__pgirreps:`; the O(3) `__irreps:`
  format is BYTE-FROZEN — diagnostics are differentially gated), a
  generalized `(|BlockSpecTag|_|)` Unify arm, and a small generic
  e-weighted counting core added as a TEST-PINNED TWIN (generic@O3 =
  MLSpec.homDim on the sweep) — MLSpec stays byte-untouched; rerouting
  is earned at the third block-spec member.
- **Witnesses chosen by MATRIX RATIONALITY: C₄ + D₄** — every generator
  entry in {0,±1}, so the group-average oracle is exact-rational with no
  field extension (the Test_PermOracle zero-tolerance standard) and
  runtime equivariance pins are EXACT float equality. Minimal contrast:
  same E dimension, e = 2 vs 1 — the FS correction is the ONLY sizing
  difference (pg_hom_dim 9 vs 5 on one spec shape; the thesis as one
  corpus diff). Table-integrity traps: ℝ-Burnside Σdᵢ²/eᵢ = |G| (4 and 8
  ✓). Roster boundary = rationality, NOT crystallography: ℚ(√3) families
  (trigonal/hexagonal/cubic-E) are the named first growth. FS ∈ {ℝ,ℂ}
  for ALL single point groups; ℍ first appears at double groups —
  FsType reserves the VALUE (counts uniform, emission a loud internal
  error), never a dead field.
- **Fusion multiplicity is real but not where first guessed**: C₄ᵥ's E⊗E
  is multiplicity-FREE; the forcing instances are C₃'s E⊗E ⊇ 2A over ℝ
  and chiral T's T⊗T ⊇ 2T — hence the point-group TP path model needs a
  CG-copy index (5b-iii, deferred; NOT a tpDecl table-swap).
- **The lattice arm lives IN MLEquiv — no fourth walker**: the polarity
  table MATCHES at every arm (the one candidate divergence, 1-dim
  character products B1·B1 = A1, is a sound rule ADDITION deferred for
  v1 rule-parity, not a reversal). `Group` grows `Point of id`;
  invariantOffsets generalizes to trivial-label offsets; C₄ᵥ/C₄ replays
  the O3/SO3 pseudoscalar asymmetry as data.
- **§6.1 CLOSED: mathcomp is OUT.** Everything 5b relies on is either a
  finite integer computation over baked data (FS indicators Σχ(g²)/|G|,
  J identities, table closure, the e-weighted count, ℝ-Burnside) —
  vm_compute territory — or a general theorem whose shipped-group
  instance the exact oracle discharges as entrywise projector equality.
  Same cited/computed division as BladePartition.v. Revisit only for
  user-declared groups wanting compile-time completeness on unseen
  groups (honest answer: propose-don't-certify).
- Surface encoding SETTLED (post-round check): spec entries are
  (LABEL_NAME, mult) tuples — `SVString` is already first-class in
  StaticEval (:14, :221), so the name surface costs no new static
  machinery; a `pgSpecOfStatic` mirroring `specOfStatic` owns the
  unknown-label diagnostic; the tag payload carries names
  (`__pgirreps:C4::A1,1|E,2` — names are frozen table data and the tag
  is the diagnostic identity). No 5b-i static builtin returns a pg spec
  (ints only) until the TP stage.
- Named-so-it-can't-sneak-in: `ml.restrict(SPEC, G)` — O(3) ↓ G
  branching (crystal-field splitting), the hottest adjacent feature.

**Post-5a cleanup (separate stage, earned at three copies): extract the
abstract-interpretation WALKER SHELL** (freeVars, stmt/block folds,
cert-table pre-scan) shared by MLEquiv/MLGalilean/MLPerm. The shell is
rule-of-three-ripe after 5a; the LABEL abstraction is not until 5b.

What 5a does not deliver, recorded once: Sₙ irrep coordinates in any form
(Specht bases, Sₙ-Fourier features for rankings ML, named isotypic-pooling
sugar — the isotypic projectors are expressible as weight settings of
derive_perm_linear, so nothing is lost in map-space); symbolic/runtime N;
channels and the factored (sum-pushdown) emission — staged like 1a→1b,
naive-and-pinnable first; `derive_perm_tp` (whose S₂ self-TP compaction
replays stage 1's move as an INTEGER orbit quotient) and `derive_perm_poly`
(Burnside); bipartite/multi-node-set graphs.

### 3.7 Speculative tier: Kondor–Trivedi

Translation-equivariant linear maps between functions on a homogeneous space
are exactly group convolutions. Blade's stencil/`AlignedExpr` machinery
(future.md §3.1) is therefore the translation-equivariant fragment of this
same discipline — `derive_conv` would be the `G = ℤ^d` instance of
`derive_linear`, with halo/chunk metadata as the spec. Recorded, not planned.

## 4. Safety model

Everything in §3.1–§3.4 is **synthesis** — the compiler emits the basis;
there is no user body to get wrong, so no confirm-and-pin is needed there.
Two places do need a safety story:

**(a) Compaction is a storage-correctness claim.** `derive_sym_tp` claims
"these slots span the whole hypothesis space"; if the rule were wrong the
user silently trains in a subspace — a wrong answer, not a slow one. The pin
is **mechanical**: emit the explicit linear injection `embed : W_sym ↪
W_dense` and value-pin `derive_sym_tp(S,x,w) == derive_tp(S,S,x,x,embed(w))`
on the corpus. The dense route is the oracle; no external oracle needed.

**(b) Deduced representation is confirm-and-pin, with a twist each way.**
Mirroring the shipped model (BL4010, `--strict-pins`):

> Unconfirmed rep deduction ⇒ the function is **uncertified** (correct, just
> not proved equivariant). Confirming ⇒ the user writes `where ml.equiv(G)`
> and gets the theorem.

Easier than the symmetry axis: wrong-guess state is uncertified-and-correct —
nothing about emitted code changes, so batch builds have a safe terminal
state with zero correctness exposure. Harder than the symmetry axis: `where
ml.equiv(G)` is part of the signature and participates in call-site
discharge, so an imported function's certificate is load-bearing across
module boundaries in a way an inferred `comm` is not. Rule: **deduction
proposes; only source-written `where` clauses export.** Suggestions ride the
structured-diagnostic channel with their own code (BL4011 at time of
writing — BL4010 is taken by storage pins, and certificates are not
storage).

## 5. Worked example

```blade
import ml as ml

let static SIN = [(0, 0, 8), (1, 1, 4)]         // dim 20
let static S2  = ml.sym_spec(SIN, 2)            // Sym²(V_in), dim C(21,2) = 210
let static W2  = ml.poly_weight_dim(SIN, 2)     // dim Hom_G(Sym²V, ·) — the theorem

function layer(w: Array<Float like Idx<W2>>,
               x: Array<Float like IrrepsIdx<SIN>>) where ml.equiv(O3) =
    ml.derive_poly(SIN, 2, x, w)

// Reported (shown, pinnable):
//   bridge   : Sym²(V_in) = SymIdx<2, IrrepsIdx<SIN>>   (210 cells, triangular)
//   basis    : dim Hom_G(Sym²V_in, out) = W2            (Schur, complete)
//   dropped  : the Λ² summand — 0 parameters reach it from x alone
//   vs       : derive_tp(SIN, SIN, x, x, ·) exposes tp_weight_dim > W2 slots,
//              of which the excess are provably dead on the diagonal
//   theorem  : f(D·x) = D'·f(x) for all g ∈ O(3), by construction —
//              no training-time equivariance test exists to run
```

The poly-surface twin (§3.4):

```blade
function tri(xs: Poly<T^1>) where comm(xs), ml.equiv(O3) = ml.tp_chain(SIN, xs)
tri <@> (h, h, h)
// identity group of 3 over IrrepsIdx<SIN> → SymIdx<3, IrrepsIdx<SIN>>
// = Sym³(V_in), C(22,3) = 1540 cells, triangular
// weight space = dim Hom_G(Sym³V_in, ·) — the degree-3 body-order term
```

## 6. Open questions / risks

1. **The mathcomp decision.** The tower is stdlib-only. Options: (a) formalize
   only the *combinatorial* obligations — the multiset↔Sym^k basis bijection,
   the σ-symmetric weight count, Bell(k+l) — all finite-set stdlib material,
   and exactly the ones guarding storage correctness; Schur completeness is
   then *cited*, with its *lowering* proved (the division of labor the tower
   already uses elsewhere). (b) Take mathcomp as a dependency for genuine
   `Hom_G` completeness. **Leaning (a).** Needs deciding before stage 5's
   proofs are scoped.
2. **Real-basis subtleties, three distinct ones.** (i) O(3) real ≡ complex
   decomposition — settled (all FS +1), it is what makes §3.3 sound.
   (ii) The mirror-pair rule of §3.2 relies on the **cross-block** exchange
   identity `C^{l2l1l3} = σ·(C^{l1l2l3})ᵀ` — and stage 1b's fused kernel reads
   the kept path's table for the dropped one, so it depends on it pointwise.
   Both the `l1 = l2` case (Tests_Wigner.fs:81-93) and the cross-block case
   (:95-122) are now pinned bit-exact for every l ≤ 2 triple: precondition
   discharged.
   (iii) Finite groups: the FS correction (§3.6) — Sₙ safe, point groups not.
3. **Nominal identity under nesting — DISCHARGED at stage 3.** (i) Unify's
   spec-mismatch arm keys on Tag/Symmetry, never Rank — rank-k irreps
   records handled with ZERO Unify changes (verified by accept/reject
   corpus, not assumed). (ii) A-vs-B strictness at equal total_dim holds
   (corpus 136); plain-adoption permissiveness holds (135). One adjacent
   fix was needed: registerTypeDecl overwrote Tag with the alias name,
   silently erasing the spec through whole-type aliases — now folds the
   name into the irreps tag like TyIrrepsIdx's own arm. (iii) The fused
   multi-axis compound's Tag = None stance is unchanged (stage 4 decides
   its diagnostic). New hazard found while attempting the sym_lift
   retyping, chipped for a separate session: the unifier accepts a rank-K
   symmetric annotation against a rank-1 flat emission (SymNone wildcard,
   extents uncompared) — only g++ catches it.
4. **Separate compilation** (mirrors the deduction plan §6.1): resolved here
   by propose-don't-export (§4b) — deduced certificates never cross module
   boundaries unpinned.
5. **Numeric polarization at large k.** Multinomial factors `k!/∏mⱼ!`
   (SymTensor.multiplicity) are ≤ 24 at k ≤ 4 and harmless; conditioning of
   the monomial basis degrades beyond. **Scope: k ≤ 4** — also where
   body-order expansions live in practice.
6. **Convention fork, decided:** `SymIdx` cell = coefficient of the
   **monomial** `e_{i1}···e_{ik}`; `sym_lift` emits `∏ⱼ x[iⱼ]` with no
   multiplicity weights. The alternative (symmetrized-tensor values) differs
   by exactly `multiplicity(idx)` per cell; same space, different pins.
   Stated once, here.
7. **Compaction changes float association.** Mirror CG tables
   `realCGDense(l1,l2,l3)` vs `(l2,l1,l3)` are independently summed and may
   differ in the last ulp — so stage 1 splits into 1a (parameter compaction,
   dense arithmetic, **ulp-exact** pin) and 1b (arithmetic compaction,
   tolerance pin).
8. **Ranking discipline.** src/ppl/SymTensor.fs `rankOf` is **colex**; the
   compiler's SymIdx bijection is **lex** (BladeSafety.v, CodeGen unrank; the
   shipped `packPairs` agrees with lex). Reuse `SymTensor.enumerate` (lex)
   for reference models; never its `rankOf`. (Its docstring overclaims —
   out of scope here, noted.)
9. **The Sym^k basis problem (k ≥ 3) — RESOLVED by design round 2026-07-26**
   (two-agent adversarial round; construction in §3.3b, staging in §7 3b).
   The original dilemma — tolerance-GS arbitrariness (route a) vs recoupling
   nullspaces (route b) — was a false choice: route (b) is rejected outright
   (the S_k action is 6j-mixed and irrational in path coordinates, so no
   combinatorial selection rule exists there), and route (a)'s GS objection
   dissolves once the irrational core is confined to UNIVERSAL per-(j, l)
   tables where exact rational arithmetic is cheap. Key reframe: the
   construction is a constructive direct sum (every label emits exactly one
   basis vector; counts per (L, P) equal the `powerSpec` multiplicity as a
   theorem), not spanning-then-selection — no float decision exists anywhere
   in the convention. Residual open items, tracked for stage 2b:
   (i) ~~the realization phase rule~~ DISCHARGED 2026-07-27: derived and
   confirmed (§3.3b; SymPowerTables.fs doc-comment is the record); the
   gapped assert stays as a compiler-bug guard; (ii) ~~sector-constant placement~~ DECIDED at 2b-iii: explicit per-sector
   baked scalar (and 2b-iii's oracle showed it is load-bearing for
   orthonormality, not a free choice — §3.3b identity 1);
   (iii) ~~chains need `realCGSparse` pins up to L = k·lmax~~ DISCHARGED
   2026-07-27: completeness pinned over l1 ≤ 9, l2 ≤ 3, all l3 ≤ 12
   (worst 9.99e-16); note the extended-range exchange identity is ~1-ulp
   class, not bit-exact; (iv) the copy-split label basis
   is not GL(m)-channel-covariant — document so label indices are not
   mistaken for channel structure; (v) ~~the k = 2 cross-pin's alignment table~~ DISCHARGED at 2b-iii:
   emitted computationally from the three-row rule and asserted a
   bijection; value-level M verified (see §3.3b). (vi) Recorded
   observation, no action bound: WignerTables' prose "Y^real = U·Y^complex"
   and realCGDense's conj-on-input placement read as opposite conventions;
   they differ by conjugating the complex tensor — after the −i phase fix a
   global sign per odd table — self-consistent and unobservable while all
   consumers share the tables (the 2b-iii projector oracle is insensitive
   by construction). Tidy the WignerTables doc-comment at next touch.
   (vii) Observed-not-derived: T_{2,l}'s realized sign agrees with the CG
   realization (+) on every even-L case tested; only |ratio| = 1 is
   derived. Derive or keep pinned.

## 7. Implementation staging

Stages 1–2 need **none** of S1/S2/S3 — elaborator-internal, which is why they
go first.

1. **Stage 1a — `derive_sym_tp` / `derive_alt_tp`, parameter compaction.**
   **LANDED 2026-07-26** (branch feat/derive-sym-tp): MLSpec `s2TpCompaction`
   (enumeration + closed form cross-checked, partition asserted), shared
   `tpBodyStmts` builder, sizing builtins, MLEquiv arms; corpus
   `ml-equiv/032-035` — ulp-exact vs embedded-dense `derive_tp`,
   `alt(x,x) = 0` exact, anchor pins 7/3, 28/20, 29/14; cross-block exchange
   identity pinned bit-exact in Tests_Wigner (the §6.2(ii) precondition —
   held with zero deviation for all l ≤ 2 triples).
   Seams: none. `symTpBlocks`/`altTpBlocks` beside `homBlocks` (MLSpec.fs);
   `sym_tp_weight_dim`/`alt_tp_weight_dim` sizing builtins; a
   `deriveSymTpDecl` beside `deriveLinearDecl` (MLElaborate.fs:316) emitting
   the existing dense `tpDecl` loop with the compacted buffer read through
   `embed`; MLEquiv op arms (~5 lines each, group-agnostic). **Pin:
   ulp-exact** against `derive_tp(S,S,x,x,embed(w))`. Corpus: `ml-equiv/03x`.
   Smallest value-pinnable wedge; lands the entire semantic claim.
2. **Stage 1b — arithmetic compaction.**
   **LANDED 2026-07-26** (branch feat/derive-sym-tp): the compacted kernels no
   longer route through the dense loop at all. MLSpec gained a shared
   free-cell skeleton (`s2TpSkeleton`) that both the stage-1a embed table and a
   new fused cell table (`S2TpCell` / `s2TpCells`) are built from, so the
   packed layout cannot drift between them; `deriveS2TpDecl` emits one term
   pair per kept cell — the dropped mirror path collapses onto the kept path's
   own CG table with the single sign στ (+1 Sym² / −1 Λ²), and a diagonal
   path's (u2, u1) half folds in with sign τ, the u1 = u2 cell being its own
   partner (single term). `tpBodyStmts` is untouched; `tensor_product` /
   `derive_tp` still emit the dense loop. Observed vs `derive_tp` on the
   embedded dense weights, anchors A/B/C: **max 1.5e-16 relative** (per spec,
   sym/alt: 9.8e-17 / 9.8e-17, 1.5e-16 / 1.1e-16, 1.2e-16 / 1.2e-16) — three
   orders inside the 1e-13 pin. `alt(x, x) = 0` and the exchange identities
   `sym(x,y) = sym(y,x)`, `alt(x,y) = −alt(y,x)` stay EXACT (spec B's
   identities at ~3e-17/8e-17 from the mirror-cell fusion of unequal blocks);
   the association is `(coef·w)·(x·y)` per term precisely so that a mirror
   cell's two products are bit-identical at x = y. Emitted size, spec A: the
   sym kernel's baked tables go 6-path/27-CG-entry (the dense loop stage 1a
   embedded verbatim, plus a 10-slot dense buffer and a 9-entry expansion
   table) → 4-cell/18-CG-entry with no dense buffer; the alt kernel →
   2-cell/9-CG-entry. Corpus 032/033's `sq_diff = 0.0` pins hold unchanged
   (squared residuals ~1e-31/~1e-30), reworded there as tolerance pins;
   `ad.grad` through the fused two-term accumulation matches central
   differences to 4.3e-10 relative (FD-limited).
   Seams: none. Drop mirror paths and
   the m = 1 σ = −1 diagonal paths from the emitted loop. **First pin the
   cross-block exchange identity** (§6.2ii) in Tests_Wigner.fs. Pin 1b vs 1a
   at relative 1e-13 (§6.7).
3. **Stage 2a — the counting half: `sym_spec` / `alt_spec` /
   `poly_weight_dim` / `sym_lift`.**
   **LANDED 2026-07-26** (branch feat/derive-sym-tp): weight-peel in MLSpec
   (`gradedPowerHist`/`peelSector`/`powerSpec`, histogram nonnegativity +
   w↔−w symmetry + peels-to-zero guards, cardinality asserts on every call),
   static builtins in MLStatics (K gated 1..4; bad K reports via the
   BL3999 static-eval channel; `alt_spec` with K > dim V is a clean error),
   `symLiftDecl` + MLEquiv arm (Inv→Inv, Rep→targeted BL4008). Anchors all
   exact incl. Λ⁴(A) = [(0,1,1)] (the odd determinant line); cross-stage
   identity `poly_weight_dim(s,2,tp_spec(s,s)) = sym_tp_weight_dim(s)` (and
   the Λ² twin) verified on a 15-spec sweep to mult 4, l ≤ 3 — the stage-1
   counts re-derived Wigner-free. Corpus ml-ops/013-015, ml-equiv/036; full
   suite 1364/0. Note: spec-valued statics do not echo in program output —
   corpus pins go through `irreps_len/l/parity/mult` + `total_dim`, plus the
   round-trip "sym_spec result feeds IrrepsIdx<> annotations and
   derive_linear".
   Seams: none. The §3.3 weight-peel as
   spec-valued static builtins (Sym^k via ascending-j graded knapsack, Λ^k
   via descending-j); `poly_weight_dim(s, k, s_out) = hom_dim(sym_spec(s,k),
   s_out)` — the degree-k parameter-count theorem as a static builtin;
   `sym_lift(s, k, x)` = the §6.6 unweighted monomial lift over lex canonical
   multisets (§6.8). `total_dim(sym_spec) = C(n+k−1,k)` and
   `total_dim(alt_spec) = C(n,k)` asserted on every call; scope k ≤ 4.
   **Cross-stage pins**: `poly_weight_dim(s, 2, tp_spec(s,s)) =
   sym_tp_weight_dim(s)` and `hom_dim(alt_spec(s,2), tp_spec(s,s)) =
   alt_tp_weight_dim(s)` — the stage-1 counts re-derived by a
   Wigner-table-free route.
3b. **Stage 2b — `derive_poly<k>` kernel synthesis (DESIGNED 2026-07-26;
   construction §3.3b, resolution record §6.9).** Uniform label convention
   for ALL k ∈ {2, 3, 4} — k = 2 is NOT routed to `derive_sym_tp`; it runs
   the same machinery and stage 1 becomes its oracle (§3.3b's M-pin). The
   emitted kernel: per-(copy, degree) monomial lift + T_{j,l} matvec, chain
   features via pairwise CG contractions SHARED BY PREFIX (labels form a
   tree; one let per node — code size ~ O(#labels)), sector constant baked
   as an explicit auditable scalar, then homBlocks-layout weight mixing. No
   spec-sized change-of-basis matrix is ever materialized. Cap: #labels =
   C(n+k−1, k) ≤ 100000 (symLiftDecl precedent), diagnostic naming the
   future channel-shared op. Sub-stages, counts before arithmetic:
   - **2b-i**: derive the phase rule (§3.3b conjecture; V₃ ⊂ Sym³(V₂) test
     vector) — gates everything; T_{j,l} generator with exact pins (integer
     E·v = 0 re-verification, occurrence counts vs `powerSpec [(l,p,1)] j`,
     rational norm identities, Gram = I to 1e-14, bit-pins for l ≤ 3);
     extend realCGSparse unitarity pins to L ≤ k·lmax.
     **LANDED 2026-07-27** (branch feat/derive-sym-tp): phase rule
     CONFIRMED BY DERIVATION (see §3.3b, updated); SymPowerTables.fs — the
     exact pipeline (BigInteger rationals, RREF labeling, exact rational
     Gram–Schmidt with the diagonal Gram
     ⟨mono_A, mono_A⟩ = j!·∏αᵢ!·∏c², derived and stated) with floats only
     at realization; T_{3,1} L=1 = w₋₁w₊₁² − w₀²w₊₁ exactly (norm² 15, the
     design round's predicted line); occurrence counts = powerSpec on all
     j ≤ 4, l ≤ 3 (multiplicity spaces to dim 3, e.g. Sym⁴(V₃) 6:3, 4:3);
     exact Gram = I before floats, float Gram worst 2.0e-15 global;
     completeness relation Σ_{l3,c3} T·T = δ⊗δ pinned over l1 ≤ 9, l2 ≤ 3
     (worst 9.99e-16); higher-l exchange spot checks are ~1-ulp class, NOT
     bit-exact (the §6.7 association effect — the l ≤ 2 bit-exactness was
     the special case); uMatrix made public as the single source of truth
     (drift warning added at the definition). Test block "SymPower Tables"
     (`blade test sympower`), 89/0; full suite 1453/0.
   - **2b-ii**: label enumeration + integer counting asserts in MLSpec (no
     emission): per-(L,P) label count = `powerSpec` mult, total =
     `polyWeightDim`, on every call.
     **LANDED 2026-07-27**: MLSpec `PolyLabel`/`polyLabels`/`polyLabelCounts`
     /`polyWeightDimViaLabels` (+ `Multinomial` = k!/∏j_c! as a sector
     integer; where it bakes stays §6.9(ii)'s call). Anchor A k=3: 6 labels,
     sectors 1/1/2/2, per-(L,P) 2/2/1/1. k=2 three-row count bijection vs
     `s2TpCells` verified on A/B/C (7/28/29 re-derived a third way). Sweep:
     15 specs × k ∈ {2,3,4}, 1993 labels, counts = sym_spec everywhere.
     Convention gaps found and fixed (occurrence order, blocked odometer,
     k=0, the three-row alignment) — folded back into §3.3b. Block 107/0;
     full suite 1471/0.
   - **2b-iii**: emission + the layered oracle — projector-equality pin
     (independent route; ‖P_ref − Σvvᵀ‖ < 1e-10 on anchors); k = 2 M-pin vs
     stage 1; k = 1 ulp-exact vs `derive_linear`; closed-form invariant
     anchors; corpus + full gates.
     **LANDED 2026-07-27**, both halves:
     *Emission*: `ml.derive_poly(SPEC, K, SOUT, x, w)`, K ∈ 1..4, uniform
     label convention; per-copy lifts + T matvecs, prefix-shared CG chain
     nodes (couplings ≈ #labels: e.g. 568 labels → 598 couplings vs 898
     naive), explicit per-sector constant (`__w_sc`), label-major weight
     mixing. **Weight layout is LABEL-MAJOR** (polyWeightDimViaLabels
     order) — agrees with homBlocks on count always, on ORDER exactly when
     no (l,p) has both multIn > 1 and multOut > 1; the K=1 degeneration to
     `derive_linear` is **ulp-exact** (sector constant literally 1.0, same
     accumulation order). Closed-form anchors matched with derived
     constants (1/√5 for |x|⁴, √(3/5) for |v|²v, √(108/35) for the
     traceless det). Completeness self-check Σ‖feature‖² = |x|^{2K} at
     1.5e-15 over 6 specs × K ≤ 4; rotation equivariance 1.3e-15;
     ad.grad FD-limited. Corpus ml-ops/016-017, ml-equiv/037-038.
     *Oracle* (block "Poly Oracle", `blade test polyoracle`, 48/0):
     Casimir–Lagrange isotypic projectors in exact rationals over the
     divided-power monomial basis (E/F/H re-derived locally; interpolation
     over the FULL L superset — absent-(L,P) numerators vanish
     identically), tr(P_ref) = (2L+1)·mult exact pre-float;
     ‖P_ref − P_conv‖_max ≤ 1.1e-15 across 6 cases; NEGATIVE CONTROLS run
     (1e-6 T-row rotation → 4e-6 failure; dropped sector constant → 18
     failures). M-pin: bijection emitted computationally; ratio multiset
     exactly {+1, +√2}, cross-orthogonality ~1e-15. (The planned SVD
     reference was superseded by Casimir–Lagrange — stronger: exact,
     convention-free, no rank decisions.)
4. **Stage 3 — writable `SymIdx<k, IrrepsIdx<spec>>`.** Seams: **S1 + S2.**
   Parser: second argument accepts `parseIndexType | parseSimpleExpr`
   (Parser.fs:756-763); `TySymIdx` grows an index-type payload. TypeCheck:
   the lowering arm (TypeCheck.fs:590-592) carries the inner record's
   Tag/IxKind with `Extent = totalDim(spec)`. Verify §2.7's IR-reachability
   claim (IR.fs:2126) and audit Unify per §6.3. Downstream: nothing.
   **LANDED 2026-07-27**: `SymIdxBase` payload DU (illegal states
   unrepresentable); one-token-peek grammar (KwIrrepsIdx/KwIdx → index
   type; bare names stay the legacy int reading forever — named-alias-as-
   base out of scope); shared `symPowerIndexRecord` backs both lowering
   arms; ANTISYMIDX INCLUDED (same seam). Unify needed nothing (§6.3
   discharged); F10 confirmed — the inferred comm-over-irreps record and
   the written annotation lower to identity-field-identical records (the
   round trip is by construction). Index printers now show the power
   (`SymIdx<2, IrrepsIdx<[…]>>`), not the bare base. sym_lift retyping
   attempted and REVERTED (rank-1 emission vs rank-K type; g++-level
   failure exposed a silent-unification hazard, chipped; follow-up in
   symLiftDecl's doc-comment). Corpus index-types/134-138; full suite
   1530/0.
5. **Stage 4 — multi-axis irreps under joint fusion.** Seam: **S3.** Relax
   the `IxKPlain` predicate (IR.fs:1366) to admit `IxKIrreps` — sound
   precisely because IrrepsIdx is dense (extent = cardinality), so the joint
   compound is an honest product and the wreath-product subtlety deferred for
   SymIdx-typed inputs (future.md §4b.1) does not arise. Decide §6.3(iii)
   tag anonymity here. Unlocks `comm` over per-node/row-stacked irreps arrays.
   **LANDED 2026-07-27**: `isDenseFusableKind` (IxKPlain | IxKIrreps) is the
   whole eligibility diff; the SymIdx exclusion is stated as coded (stores
   only canonical cells ⇒ extent ≠ cardinality ⇒ not a dense product; sound
   joint form = wreath). The §2.7-predicted Tag/IxKind hazard EXISTED:
   deduceOutputType's compound path inherited the head factor's IxKind —
   demonstrated by a hazard build (BL6001 fires exactly when the LEADING
   factor is irreps); fixed by stamping `IxKind = IxKPlain` beside the
   existing `Tag = None` (the §6.3(iii) anonymous-product stance, now
   enforced not just stated). Decode paths 0 lines changed (runtime
   extents, provenance-agnostic as §2.7 claimed). Corpus symmetry/025-027:
   compact-vs-dense twins byte-identical, before/after measured (36 dense
   → 21 compact cells); stage-3 single-axis pins bit-identical. Full suite
   1538/0.
6. **Stage 5 — finite groups (REDESIGNED 2026-07-27; two-agent round;
   full design §3.6).** The original sentence promised a label refactor
   that 5a deliberately does not perform.
   - **5a — the Sₙ index-action member** (additive-only, sibling to
     MLEquiv/MLGalilean): 5a-i counting layer (MLPermSpec: RGS partition
     order, Stirling-vs-odometer count asserts, witness-unitriangularity
     certificate, `perm_weight_dim`/`perm_bias_dim` erroring on N < K+L);
     **5a-i LANDED 2026-07-27.** MLPermSpec.fs (dependency-free integer;
     `orderPartitions` is the single swappable order function — and the
     swap is now moot: **the Coq keystone PROVED AS STATED**,
     `rgs_lex_extends_refinement` in BladePartition.v, with the fallback
     extension property also discharged as a bonus). m = 0 convention:
     Bell(0) = 1 via S summed from j = 0 — `perm_bias_dim(0, N) = 1`, the
     L = 0 readout is the constant map. F# strict-triangularity shadow:
     zero violations over all pairs at the cap; third-route
     block-insertion enumerator agrees as sets, m ≤ 6. BladePartition.v
     (61 items): the RGS enumeration is an ARROW INSTANCE (canonA_rgs) —
     partitions join Sym/Antisym/Compound in the tower's proved
     enumeration family; fibre counts = Stirling rows (peel-first vs
     peel-last reconciled); witness matrix upper-unitriangular over the
     emitted list; orientation pinned by computation (no doc error); the
     m = 4 density double-count 60 = Σ S(4,j)·Bell(j). Tower 25 files,
     430 items, coqchk axiom-free. Blocks "Perm Spec" 33/0; full suite
     1573/0.
     5a-ii naive emission (`derive_perm_linear`/`derive_perm_bias`,
     one loop nest per partition) + the exact-rational Reynolds/Gram
     oracle block + corpus anchors (DeepSets, matrix invariants, Maron
     15+2) with integer-data exact-equality equivariance pins;
     **5a-ii LANDED 2026-07-27.** Emission: flat row-major (the `_rows`
     precedent), L=0 = one-cell array, sum/gather/broadcast falls out of
     which index expression uses each block variable (no classification
     code); one-hot Maron pins hand-derived (identity/transpose/
     diag-gather-broadcast; note slot 0 = TRACE under coarsest-first);
     equivariance pins exact integer equality; ad.grad FD-limited both
     sides; inside `ml.equiv(O3)` bodies the default arm is already
     sound (rep args BL4008, invariant args Inv — node axes carry no
     O(3) action; no reject-list edit until 5a-iii). Oracle (block
     "Perm Oracle", 45/0, 159 ms): P_basis = P_ref as EXACT RATIONAL
     ENTRYWISE EQUALITY at seven anchors to dim 256 (no float token in
     the file); truncation cases (2,3) and (3,4) certified exactly.
     LOAD-BEARING FINDING: incompleteness is INVISIBLE to the Gram
     closed form (a dropped column leaves surviving entries exactly
     n^{b(γ∨π)}) — the projector-equality pin, not the Gram, detects
     completeness; recorded in the test file. Two orientation traps
     caught and pinned definitionally (coarsens argument direction —
     the tuple's pattern is the COARSE side; σ^{⊗m} = relabel-values,
     not permute-positions). Full suite 1621/0.
     **5a-iii LANDED 2026-07-27 — the Sₙ member is complete.** MLPerm.fs
     (the deliberate THIRD walker copy — 5c's witnesses now exist),
     conjunct `__ml_perm_equiv`, BL4012 (BL4011 stays reserved,
     unregistered, for §4b). TWO CONVENTION DELTAS from §3.6's prose,
     both forced and now normative: (1) v1 keys Pow k on FLAT
     `Idx<N^k>` buffers — the as-landed op ABI — not rank-k arrays;
     rank ≥ 2 certified signatures reject with the per-axis-v2 pointer;
     k uniqueness needs N ≥ 2, rejected at the conjunct. (2) The
     polarity headline's writable form is whole-array arithmetic
     (softsign `h/(1.0+h*h)` — BL4008 twice under equiv(O3), accepted
     here); scalar intrinsics are scalar-only in Blade so `exp(A)` is
     unwritable at ANY certificate — the pointwise-builtin rule is
     implemented and commented but corpus-pinned via arithmetic.
     Sound edge rules: literal aggregates in node-power spaces REJECT
     (an arbitrary constant is not Sₙ-fixed; Pow 0 means fixed —
     pointer at derive_perm_bias); indexing/writes/formers/reduce over
     Pow k ≥ 1 reject with targeted v2/readout messages. perm_matmul
     emitted (own N ≥ 1 gate — no false K+L constraint). Dual
     certificate perm_equiv + galilean pinned exact. Parser:
     open-conjunct args now accept int literals (comm/antisymm
     unchanged). Corpus ml-equiv/039-044; full suite 1627/0. Proofs: new BladePartition.v — RGS enumeration
     exhaustive/duplicate-free, `rgs_lex_extends_refinement` (the
     triangularity keystone; fallback convention has a one-line proof),
     witness lemma over the compiler's list (the s2_cells_spec
     discipline); orbit-indicator completeness cited-not-proved (§6.1(a)
     split). Factored emission is the 1b-twin, staged after, pinned
     against naive. Diagnostics: next free BL code AT LAND TIME (the
     "BL4011" here and in §4b were double-booked — §4b's certificate-
     suggestion channel keeps the earlier claim; stage-5 takes the next
     free).
   - **5b — point groups, the second block-spec member (DESIGNED
     2026-07-27; full design §3.6).** Sub-stages, counts before emission
     before lattice (the proven cadence):
     * **5b-0** — MLPointSpec.fs (FsType/PgIrrep/PointGroup registry =
       {C4, D4}, frozen integer tables; the generic e-weighted counting
       core as MLSpec's test-pinned twin; pgHomDim/pgHomBlocks); table
       integrity asserted on load (J² = −Id, generator commutation,
       ℝ-Burnside Σd²/e = |G|); the exact-rational Hom-space Reynolds
       oracle with the three negative controls (dropped J → trace
       deficit; e ≡ 1 naive-formula control; spurious diag(1,−1) End
       column dies at R₉₀); BladePointGroup.v (all computational over
       the witnesses: table closure, FS indicators, J identities, the
       e-weighted sum; End-completeness cited, oracle-discharged).
       **LANDED 2026-07-27, both halves.** F#: FS indicators COMPUTED
       from the enumeration (C4 E = 0 → e = 2; D4 E = 1 → e = 1); the
       9-vs-5 thesis pin with BOTH E's sharing the same R₉₀ generator
       matrix — e is the only differing input; twin pin over 225
       ordered pairs, MLSpec byte-untouched; oracle 42/0 incl. the
       homDim = 0 zero-projector anchor; all three negative controls
       live-failing then standing. FINDING: over REAL characters the
       indicator triple is ν = 2 − e (1/0/−2 — ℍ is the
       complexification's double); both shipped types agree with the
       old triple, and the Gram-blindness phenomenon recurred (the
       spurious column is invisible to Gram; only cell size and P_ref
       see it). RECONCILE at 5b-i: BladePointGroup.v's unreachable
       ℍ arm vs F#'s ν = 2 − e. Coq: 58 items, the 9 derived as a
       CHAIN (traces → indicator → e → count, nothing asserted);
       negative controls as refutations; tower 26 files / 488 items,
       coqchk axiom-free. Blocks PG Spec 32/0, PG Oracle 42/0; full
       suite 1701/0.
     * **5b-i** — the pg index-type former (distinct keyword; stage-3
       registerTypeDecl alias fix replayed for the pg tag — checklist
       item), (LABEL_NAME, mult) spec statics, pg_* sizing builtins,
       `ml.derive_pg_linear(GROUP, SIN, SOUT, x, w)` (e = 1 =
       deriveLinearDecl idiom verbatim, ulp-pinned; e = 2 = the [Id, J]
       two-term form); gates = full suite + the 5c byte-differential
       (Unify/printers are shared paths); corpus = the C4-vs-D4
       contrast file (9 vs 5) + the E-plane anchor (f = w₁x + w₂Jx;
       at (0,1) the output IS the 90° rotation, pinned exactly) +
       exact-float equivariance pins under all generators.
       **LANDED 2026-07-27.** `PgIrrepsIdx<GROUP, SPEC>` with GROUP a
       bare identifier in type position (frozen registry data, like
       Idx's number), a string in statics, both in the op. Tag frozen
       `__pgirreps:<group>:<alias>:<L,m|…>`; Unify's `(|BlockSpecTag|_|)`
       arm keys on alias-erased re-serialization (injective, no %A
       hazard); cross-member mismatch renders BOTH identities under
       BL4003; BL4007 correctly REUSED for pgHomDim = 0 (same meaning —
       the opposite of the BL4011 double-booking). E-plane pins: (0,1)
       → the 90° rotation exact; J² = −Id at value level; (2,−1) acts
       as (2−i); D4's same plane has ONE weight and can only scale.
       e = 1 ulp pin exact vs an in-language twin. BLOCKING FIX rode
       along: Lowering's SVString → IRLitString stub (one line,
       pre-existing IR/CodeGen machinery, no corpus had string statics
       — gate-confirmed). Deliberate exclusions recorded in-code:
       IxKPgIrreps NOT admitted to fuseJointSLevels (a second member
       needs its own stage-4-style pin) and SymIdx<k, PgIrrepsIdx>
       unparseable until 5b-iii (poor parse error noted). Certification
       default arms verified sound in all three lattices (a pg space is
       opaque data to the O(3) certificate — correct, not a false
       accept). Byte-differential PASS (1080 files, 311915 bytes
       identical); full suite 1709/0.
     * **5b-ii** — the lattice arm inside MLEquiv (Group grows
       `Point of id`, Rep payload the two-case union, invariantOffsets
       → trivial-label offsets); gate = byte-identical diagnostics on
       every existing O(3)/SO(3) corpus file + dual-certificate pins.
       **LANDED 2026-07-27.** The no-fourth-walker bet paid in full:
       `Rep of RepSpec` took ZERO pattern-site changes (23 op arms: 3
       new Point-guarded, 14 mechanical payload insertions, 6
       untouched); byte-differential PASS twice (1088 files, 332116
       bytes identical). THE ASYMMETRY, verified sound with the sharp
       criterion: restriction goes along subgroup INCLUSIONS only —
       IrrepsIdx under Point g REJECTS toward ml.restrict (a real
       C4-action exists by g ↪ O(3); Inv would be false invariance,
       decided by the x(1)-readout counterexample); PgIrrepsIdx under
       O3/SO3 stays Inv (no functor the other way; held-fixed is the
       only reading — with the honest residual recorded that a
       physical crystal rotation DOES move the buffer, the certificate
       staying literally true; future tightening defensible once
       restrict/induction exists). Pg-vs-pg mismatch rejects
       conservatively (the registry carries tables, no inclusion
       maps — C4 ⊂ D4 is real but unrepresented). Trivial label read
       from the TABLE (every generator = Id), never the name — D4's
       A2/B1/B2 are 1-dim pseudoscalars and their cell reads reject
       (corpus 050, the pseudoscalar-asymmetry anchor, admissible A1
       read beside it). gated/scalars/norms reject by name under
       Point (scalars' CONTENT is already delivered via trivial-label
       offsets; the ops are 5b-iii emission work). New pre-existing
       codegen bug found on the pristine baseline and chipped
       (let-in-broadcast scoping). ml-equiv 52/0; full suite 1717/0.
     * **5b-iii** — TP with the CG-copy multiplicity index / Sym^k via
       Molien-Newton / user-declared groups (constructor promotion,
       three named problems), ordered by demand.
   - **5c — walker-shell extraction** (post-5a cleanup; three copies =
     earned; strictly no behavior change).
     **LANDED 2026-07-27.** MLCertShell.fs: only the verbatim-shared
     surface extracted (freeVars/patternVars byte-identical by diff;
     judgeEach; conjunctsOf; bindPatternVars — max ONE callback);
     judgeStmts deliberately left as three copies (six moving parts,
     past the line); net −78 lines. Bit-neutrality PROVEN
     differentially: byte-identical `blade check` output over all 1080
     corpus files vs a sha1-verified HEAD baseline build; full suite
     exactly 1627/0. THE PAYOFF: the three-way diff cataloged 11
     divergences — 6 intentional (the polarity table doing its job,
     incl. unary minus and the two builtin lists), 4 DRIFT (chipped:
     MLPerm's former-source false-accept — the only unsound path found;
     unjudged element-write indices in 2 of 3; the shared nested-for
     freeVars gap, now one-place-fixable; MLEquiv's missing
     post-imperative arms = false-reject on reduce/range in certified
     bodies), 1 two-copy duplication left for the rule of three
     (staticArgValue/aliasMapOf, tied to MLElaborate.staticArg's
     keep-in-sync note). Three independently-written walkers over one
     AST were a natural drift experiment; the catalog is the result.
7. **Stage 6 — generator-based deduction.** MLEquiv gains an inference mode
   in the Deduce.fs pattern (pure pre-pass; consumers decide); generator
   check for rational-coefficient polynomial bodies; the −I check for O(3);
   propose-don't-export per §4b.

## 8. New proof obligations

- **Schur completeness as the continuous twin of `license_exactness`.**
  BladeCompleteness.v proves lowering exactness as an iff; the synthesis
  counterpart has the same shape: the maps expressible by the emitted basis
  are **exactly** the G-equivariant ones — soundness structurally,
  completeness by Schur. Proved vs cited = §6.1.
- **The σ-symmetric weight count** (stdlib-provable): for an involution with
  sign σ on a finite basis, fixed-subspace dimensions `m(m+σ)/2` /
  `m1·m2` per block shape. **This is the obligation that guards stage 1.**
- **Sym^k monomial-basis bijection** (stdlib-provable): canonical multisets ↔
  monomial basis; the cardinality half is already `storage_cardinality`.
- **Cauchy k = 2 as the bridge lemma**: state BladeCauchy.v's split as
  `Sym²(A⊗U) ≅ Sym²A⊗Sym²U ⊕ Λ²A⊗Λ²U` and point §3.2's multiplicity-space
  count at it — the ML module's first connection to the tower. Worked count 2
  is its numerical shadow.
- **Bell(k+l) for Sₙ, with the n ≥ k+l hypothesis** (stdlib-provable).
- **Generator soundness**: commutation with a generating set of 𝔤 ⇒
  equivariance under the identity component, plus the O(3) = SO(3) × Z₂
  reduction and the finite coefficient-comparison step (the Lie-theoretic
  core is cite-not-prove under §6.1(a); the reduction and the finite check
  are provable). BladeDeduce.v is the pattern: the discrete twin of this
  exact argument is already in the tower.
- **Composition**: as with the deduction triad, all of the above produce the
  *inputs* to H∩Stab and to `homBlocks`; they modify neither law.
