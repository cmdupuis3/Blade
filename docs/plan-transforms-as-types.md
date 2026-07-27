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

Today `derive_linear` does this for degree 1. This plan does it for degree
*k*, and identifies the machine that makes degree *k* possible as one already
in the tower: **triangular symmetric storage**. The thesis in one line: the
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
stage 1's kept-cell enumeration at k = 2; per-copy occurrence in RREF order;
left-comb intermediate L's ascending; output (L,P) ↦ W-copy per homBlocks
layout). The emitted basis is globally ORTHONORMAL by construction: T rows
orthonormal per copy, CG chains unitary, sectors orthogonal.

**The evaluation identity** (the §6.6 convention, made precise; ⟨·,·⟩ = the
inner product with orthonormal {e_i}, s_I = the unit-coefficient sum of the
N_I arrangements of cell I):

1. v_label = √(k!/∏_c k_c!) · P_sym(chain-coupled ⊗_c v_{r_c}); the
   cross-copy multinomial appears here and only here.
2. feature_label(x) = ⟨v_label, x^⊗k⟩ = Σ_I T[label, I]·∏_j x[i_j] with
   **T[label, I] = ⟨v_label, s_I⟩** — evaluation carries no N_I factor.
3. Orthonormality in table terms: Σ_I T[r,I]·T[r′,I]/N_I = δ — the
   multinomial lives in the Gram identity, not in evaluation.
4. Cells split uniquely across copies, so feature_label(x) =
   √(k!/∏k_c!) · [CG-chain of per-copy features] — every global monomial
   counted exactly once; the runtime never symmetrizes (P_sym is
   self-adjoint and fixes x^⊗k).

**Phase rule (conjecture, to be derived in 2b-i before any table ships):**
the complex table for V_L ⊂ Sym^j(V_l) is real iff j·l + L is even; the
canonical realization multiplies by −i exactly in the odd case. At j = 2
this is the shipped realCG realness rule; first new case V₃ ⊂ Sym³(V₂).
Guard: per table, assert min(residual_real, residual_imag) < 1e-10·‖T‖ AND
the other branch > 0.1·‖T‖ — five orders of magnitude of gap, loud failure
in between.

**k = 2 correspondence (stage 1 as oracle):** both conventions decompose the
weight space into the SAME 1-dim lines (kept mirror cell ↔ two-copy sector;
σ=+1 diagonal cell ↔ even-L T_{2,l} occurrence), so the change of basis M is
a label-aligned scaled permutation with norm-ratio entries ({1, √2, 2}
class). Pin M numerically AND against the closed-form ratios; pin
`derive_poly(s,2,x,w′) ≈ derive_sym_tp(s,x,x,M·w′)` at 1e-13 — the new
machinery validated end-to-end against the doubly-pinned stage-1 kernels
before k = 3 has any oracle.

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

### 3.6 Finite groups first

For finite G, `dim Hom_G(V,W) = ⟨χ_V, χ_W⟩` — integer arithmetic over a
character table; no Wigner machinery. Targets in order:

1. **Sₙ-equivariant layers** (invariant graph networks):
   `dim Hom_{Sₙ}(ℝ^{n^k}, ℝ^{n^l}) = Bell(k+l)` for `n ≥ k+l`, basis indexed
   by set partitions — and `setPartitions`/`bell` already exist
   (src/ppl/Combinatorics.fs:30-54). Sₙ irreps are all real-type: the naive
   inner product is correct. The `n < k+l` degeneracy (partitions collapse,
   dimension drops) must be guarded, or the parameter-count theorem is false
   for small n.
2. **Point groups** (crystallographic ML) second, because of the
   **Frobenius–Schur trap**: `C₃, C₄, S₄, …` have complex-type irreps, where
   `End_G = ℂ` and `dim_ℝ Hom ≠ ⟨χ, χ'⟩` — the naive formula under-counts
   real parameters by 2× exactly there. The FS indicator correction is
   mandatory before any point-group release.

Honest cost note: MLEquiv's group-specific *judgment* is ~12 lines, but the
representation *model* is O(3)-shaped across modules — `SpecEntry = {L;
Parity; Mult}` (MLSpec.fs), the `l,p,m|…` payload of `mkIrrepsTag`
(Types.fs), the `irreps_*` static builtins, TypeCheck's `TyIrrepsIdx`
lowering, and Unify's spec-mismatch arm. Stage 5 is a multi-module refactor
(abstract irrep label), not a dozen lines.

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
3. **Nominal identity under nesting.** (i) Does Unify's spec-mismatch arm
   handle rank-k irreps records, or assume Rank = 1? (ii)
   `SymIdx<2, IrrepsIdx<A>>` vs `SymIdx<2, IrrepsIdx<B>>` at equal total_dim
   must stay distinct — same argument that motivated IrrepsIdx. (iii) The
   fused multi-axis compound (IR.fs:2091-2096) sets Tag = None — correct
   (batch × irreps is not an irreps space), but the diagnostic must say so.
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
   (i) the realization phase rule (§3.3b) is conjectured, not derived —
   2b-i's first work item, test vector V₃ ⊂ Sym³(V₂), gapped discrete
   assert as the bug-guard either way; (ii) sector-constant placement
   (explicit baked scalar recommended, decide at 2b-iii); (iii) chains need
   `realCGSparse` up to L = k·lmax — extend unitarity/orthogonality pins
   beyond l ≤ 2 before chains consume them; (iv) the copy-split label basis
   is not GL(m)-channel-covariant — document so label indices are not
   mistaken for channel structure; (v) the k = 2 cross-pin's label↔kept-cell
   alignment table must be asserted by the test, not assumed.

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
   - **2b-ii**: label enumeration + integer counting asserts in MLSpec (no
     emission): per-(L,P) label count = `powerSpec` mult, total =
     `polyWeightDim`, on every call.
   - **2b-iii**: emission + the layered oracle — projector-equality pin in
     ml/ (independent route: ordered-tuple chains + explicit symmetrizer +
     SVD, deliberately the rejected construction, fine as a test;
     ‖P_ref − Σvvᵀ‖ < 1e-10 on anchors); k = 2 M-pin vs stage 1; k = 1
     ulp-exact vs `derive_linear`; closed-form invariant anchors
     ((|x|²)² at [(1,o,1)] k=4, det at [(2,e,1)] k=3, |v|²·v at k=3);
     rotation/parity pins via Rotations.fs Wigner-D; corpus + full gates.
4. **Stage 3 — writable `SymIdx<k, IrrepsIdx<spec>>`.** Seams: **S1 + S2.**
   Parser: second argument accepts `parseIndexType | parseSimpleExpr`
   (Parser.fs:756-763); `TySymIdx` grows an index-type payload. TypeCheck:
   the lowering arm (TypeCheck.fs:590-592) carries the inner record's
   Tag/IxKind with `Extent = totalDim(spec)`. Verify §2.7's IR-reachability
   claim (IR.fs:2126) and audit Unify per §6.3. Downstream: nothing.
5. **Stage 4 — multi-axis irreps under joint fusion.** Seam: **S3.** Relax
   the `IxKPlain` predicate (IR.fs:1366) to admit `IxKIrreps` — sound
   precisely because IrrepsIdx is dense (extent = cardinality), so the joint
   compound is an honest product and the wreath-product subtlety deferred for
   SymIdx-typed inputs (future.md §4b.1) does not arise. Decide §6.3(iii)
   tag anonymity here. Unlocks `comm` over per-node/row-stacked irreps arrays.
6. **Stage 5 — finite groups.** Abstract the irrep label out of `SpecEntry`,
   `mkIrrepsTag`, the `irreps_*` builtins, and Unify's spec arm; add a
   character-table Group case; `homDim` by `⟨χ,χ'⟩` **with the FS
   correction**. Ship Sₙ (real-type, Bell machinery exists) before point
   groups. Diagnostics: next free code (BL4011 at time of writing).
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
