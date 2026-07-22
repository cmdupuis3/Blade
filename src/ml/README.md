# ml/ — everything ML in one place

This directory holds BOTH halves of the equivariant ML work, kept apart by
project boundary:

| Layer | Where | Compiled by | Role |
|---|---|---|---|
| **Reference implementation** (this README's main subject) | `ml/*.fs`, `BladeML.fsproj` | `dotnet run --project ml` — its own exe; `Blade.fsproj` does NOT reference it | executable semantics + value oracle |
| **Compiler ML layer** (`Blade.ML.*`) | `ml/compiler/*.fs` | `Blade.fsproj` (the main compiler) | the ops in the language |

`ml/compiler/` contents (in compile order):
- `MLSpec.fs` (`Blade.ML.Spec`) — the static irreps-spec model: (l, parity,
  mult) entries, dims/starts, TP paths, weight sizing. Pure.
- `WignerTables.fs` (`Blade.ML.WignerTables`) — compiler-native 3j/CG and
  the REAL-basis coupling tables (port of `ml/Wigner.fs`, which stays the
  oracle; pinned by `tests/Test_WignerTables.fs`).
- `MLStatics.fs` (`Blade.ML.Statics`) — StaticValue↔spec conversions and
  the sizing builtins (`sh_spec`, `total_dim`, `tp_weight_dim`,
  `linear_weight_dim`), registered through `StaticEval.registerStaticBuiltin`
  so the core evaluator stays ML-free.
- `MLElaborate.fs` (`Blade.ML.Elaborate`) — the ops elaboration pass
  (`y_to` / `tensor_product` / `linear` / `gated` → synthesized Blade
  functions with baked CG tables), running before Grad expansion.

Core-compiler touchpoints are exactly two: `StaticEval`'s builtin registry
(generic) and the `Blade.ML.Elaborate.expand` pipeline stage in
`TypeCheck.typeCheck`. Corpus tests live with the rest of the corpus:
`tests/corpus/ml-ops/` (op pins + rejects) and `tests/corpus/ml-e2e/`
(training runs pinned to this project's oracle values).

## Calling the ops — `import ml`

The ML surface is a real module: reachable only through an import alias, not
as language-wide bare names.

    import ml as ml            // or e.g. `import ml as m`

    let static sh2 = ml.sh_spec(2)
    let y          = ml.y_to(2, x, y, z)
    let out        = ml.linear(SPEC_IN, SPEC_OUT, w, x)

Both the ops (`y_to`, `tensor_product`, `linear`, `gated`, `linear_rows`,
`gated_rows`) and the static sizing builtins (`total_dim`, `sh_spec`,
`tp_weight_dim`, `linear_weight_dim`, `tp_spec`, `hom_dim`, `irreps_len`,
`irreps_*`) are called as
`<alias>.<name>(...)`. The elaborator is import-gated: with no `import ml` in
the module, these names are unbound (no longer injected globally), and
`from ml import ...` is rejected — a selective import would reintroduce global
names. Internally, qualified sizing calls normalize to mangled registry names
(`Blade.ML.Statics.statName`, e.g. `__ml_stat_total_dim`); a bare
`total_dim(...)` no longer resolves.

CG-typed contraction surface (2026-07-18): `ml.tp_spec(s1, s2)` computes the
FULL Clebsch-Gordan decomposition spec of `s1 ⊗ s2` — merged-canonical
(multiplicities aggregated per `(l, parity)`, sorted ascending; completeness
`total_dim(tp_spec(a,b)) = total_dim(a)·total_dim(b)`) — and
`ml.hom_dim(sIn, sOut)` the Schur dimension of `Hom_G(V_in, V_out)`
(aggregated `Σ multIn·multOut`; `0` ⇔ only the zero map is equivariant).
Like `sh_spec`, both appear in VALUE position (`let static SOUT =
ml.tp_spec(S1, S2)`), never inside a type annotation — the annotation then
names the static (`IrrepsIdx<SOUT>`). Schur violations are first-class
diagnostics: `BL4007 "no equivariant map exists"` fires for `linear` over
specs sharing no `(l, parity)` (with the all-blocks-vs-some-blocks grades)
and for `tensor_product` output blocks unreachable by any CG path.

---

# BladeML — reference implementation of the equivariant ML module

**Status**: standalone executable semantics + test oracle. Deliberately
separate from the compiler (`Blade.fsproj` does not reference this project).

**Sources of truth**:
`docs/features/equivariant-nn.md` (module doc, canonical; §11b
documents the elaboration surface implemented by `ml/compiler/`), on top of
`docs/formalism.md` v11. (The old `blade_ml_spec_v10.md` is
superseded and no longer on disk.)

**Run**: `dotnet run --project ml` from the repo root (or `dotnet run` inside
`ml/`). Prints per-section progress and a pass/fail summary; exit code 0 on
success. Current baseline: **170 passed, 0 failed** (149 forward + 21
autodiff/training). `dotnet run --project ml -- dump-oracle` prints the
training oracle's dataset, CG tables, and pinned trajectory for authoring the
Blade e2e example.

---

## 1. What this is

The ML module spec is a *design* document: Blade syntax, index types, and
static functions, none of which execute today. This project pins down the
**semantics** the eventual Blade implementation must have, as runnable F# with
numerical verification. It plays the same role for the ML module that the
oracle/differential harness plays for the core compiler: when codegen for
`tensor_product` etc. lands, generated C++ gets value-checked against these
functions and these tests.

Two layers exist in the docs; this project implements the second:

| Layer | What it is | Status here |
|---|---|---|
| Part I: core-language hook | `with equiv(G, rep)` annotations + inference rules — a type-checker feature | **not here** — belongs in `TypeCheck.fs` of the main compiler when scheduled |
| Part II: equivariant ML library | irreps, `IrrepsIdx`, CG machinery, tensor product, spherical harmonics, linear, activations, scatter/gather, the conv example | **implemented + tested** |

## 2. Deep dive: the formal structure

### 2.1 The two symmetry systems don't overlap

Blade's index types (`SymIdx`, `AntisymIdx`) act on **discrete** Sₙ
permutations of index positions and buy real storage/iteration savings. The
equivariance layer acts on **continuous** groups (SO(3)/O(3)) and is
annotation-only: zero runtime representation cost. They compose (a stress
tensor is `SymIdx<2,3>` storage *plus* `equiv(SO<3>, L2_even)` checking). The
implementation reflects the split: nothing in this project touches storage
symmetry; everything is about representation-correct *values*.

### 2.2 Irreps and specs are static data

An O(3) irrep is `(L, parity)` with dimension 2L+1. A feature space is an
ordered spec `[(L0e,16); (L1o,8); (L2e,4)]` — compile-time data in Blade
(`static` arrays), plain arrays here. Everything downstream (index layout,
weight shapes, path sets) is a pure function of specs, which is exactly what
makes the Blade version compile-time-checkable: wrong-shaped weights are type
errors because `WeightIdx<cfg>` is computed from the config.

### 2.3 `IrrepsIdx` is a DepIdx instance — and satisfies the core contract

`IrrepsIdx<spec>` = `DepIdx<Idx<blocks>, λb. Idx<mult(b)> × Idx<dim(b)>>`.
The core formalism (§3.2) requires every index type to define domain,
cardinality, storage bijection, and lexicographic enumeration in offset
order. `IrrepsIdx.fs` implements exactly that contract and
`Tests_Core.fs` verifies it (enumeration = offset order, bijection
round-trips, domain violations rejected). This is the piece the compiler's
existing `DepIdx` lowering (left-justified bijection, proofs.md §DMWF) will
subsume; the block/mult/m decomposition here is its ML specialization.

### 2.4 The CG machinery is where the real content lives

The spec encodes selection rules as dependent records:

- `CGPath`: `l_out ∈ [|l1−l2|, l1+l2]`, `p_out = p1·p2` — the triangle
  inequality and parity rule as record constraints.
- `CGIndex<path>`: the sparse support of the coefficient tensor, constrained
  by `m1 + m2 == m_out`.

The first is exactly right and implemented as the path filter in
`TensorProduct.paths`. The second is **basis-inconsistent with the rest of
the spec** — see Finding F1 below, the most important thing this
implementation surfaced.

Coefficients: `Wigner.fs` computes Wigner 3j via the Racah formula
(double-precision factorials; exact to ~1e-12 for the small L in scope),
complex CG from 3j, then the **real-basis coupling tensor** by conjugating
with the unitary complex→real change of basis U_l, with a global −i phase fix
for odd l1+l2+l3 (harmless for SO(3); same trick e3nn uses). Realness is
asserted, sparsity extracted, everything cached per (l1,l2,l3).

### 2.5 Operations are thin once the CG tensor is right

- `tensor_product<cfg>`: loop paths → multiplicities → sparse CG support;
  weights are `(mult_out × mult1 × mult2)` per path ("uvw" fully-connected in
  e3nn terms). A dense-box reference implementation exists purely as a
  differential oracle.
- `linear<spec_in, spec_out>`: block-diagonal multiplicity mixing, shared
  across m. Cross-irrep mixing is unrepresentable by construction.
- `Y<L>` / `Y_to<L_max>`: real *solid* harmonics via the sin-free
  associated-Legendre recurrence + `(x+iy)^m` — closed form for all L, no
  coordinate singularities, exactly reproducing the spec's explicit L≤2
  polynomial table. The only L-raising primitive, as the module doc says.
- `gated_activation` / `norm_activation`: nonlinearity on invariants only.
- `scatter_add` / `gather`: plain index plumbing.
- `equivariant_conv`: the §12 composition, gather → Y → TP → scatter.

### 2.6 How the tests earn trust (the differential-oracle stance)

The point is not "the code runs" but "independent routes agree":

1. **CG ⟷ spherical harmonics**: the Gaunt identity — contracting
   Y_l1(v)·Y_l2(v) with the real CG tensor must reproduce k·Y_l3(v) with the
   closed-form constant k = √((2l1+1)(2l2+1)/(4π(2l3+1)))·⟨l1 0 l2 0|l3 0⟩,
   and must vanish identically for odd l1+l2+l3. This cross-validates the
   entire U-matrix/phase pipeline against the completely separate SH
   recurrence. (`Tests_Wigner.fs`)
2. **Equivariance via fitted Wigner D**: D_l(R) is *solved for* from
   Y_l(Rv) = D·Y_l(v) on sample points — derived from the harmonics, no CG
   involved — then used to verify tensor-product/linear/activation/conv
   equivariance. CG bugs cannot hide from D fitted off an independent object.
   (`Rotations.fs`, `Tests_Ops.fs`, `Tests_Graph.fs`)
3. **Sparse ⟷ dense**: the production sparse iteration is checked against
   the dense-box reference on the spec's §12 config. (`Tests_Ops.fs`)
4. **Closed forms**: 3j/CG table values, CG orthogonality (both bases),
   exchange symmetry signs, the addition theorem
   Σ_m Y_lm(v)² = (2l+1)/(4π)·r^2l (pins normalization exactly),
   homogeneity Y_l(sv) = s^l·Y_l(v), and 1⊗1→1 ∝ cross product with
   antisymmetry. (`Tests_Wigner.fs`, `Tests_SphericalHarmonics.fs`)

## 3. Findings — spec issues surfaced by implementing it

**F1 (substantive): `CGIndex`'s `m1 + m2 == m_out` is a complex-basis rule,
but the spec's own spherical harmonics are real-basis.**
ml-spec §4.2 constrains the CG support by `m1 + m2 == m_out`; §6.2's explicit
Y formulas (0.48860251·(y,z,x), 1.09254843·xy, …) are *real* spherical
harmonics, and §1.2 positions the whole design as e3nn-but-typed (e3nn is
real-basis). In the real basis the support is characterized by
`|m3| ∈ { ||m1|−|m2||, |m1|+|m2| }` (plus a sign-parity constraint), NOT by
m1+m2=m3. Concretely: real (l1,l2,l3) = (1,1,2) has a nonzero coefficient at
(m1,m2,m3) = (−1,+1,−2) — the y·x → xy coupling — where m1+m2 = 0 ≠ −2.
`Tests_Wigner.fs` demonstrates this. Resolution options for the spec:
(a) keep `CGIndex` complex and insert basis changes at the boundaries
(rejects zero-cost claims: complex arithmetic + conversions), or
(b) redefine the `CGIndex` constraint as the real-basis support (recommended;
the sparse iteration then matches what `tensor_product` actually needs).
Either way §4.2 as written type-checks the wrong sparse set.

**F2: gate scalars do double duty and their parity is unconstrained.**
§8.1 draws gates from feature block 0 (`gate_idx = m % num_gates`) — the
scalars are simultaneously features (silu'd) and gates (sigmoid'd), unlike
e3nn's separated gate irreps; worth an explicit design note. And nothing
requires block 0 to be L0**e**: gating by sigmoid of an L0o scalar is fine
for SO(3) but breaks O(3) equivariance (sigmoid isn't odd). If O(3) is the
claimed group, `gated_activation` needs `spec(0) = (L0e, _)` as a type
constraint. Implementation follows the spec (requires L=0, any parity) and
documents the caveat.

**F3: `find_block_idx` is first-match; duplicate irrep entries are silently
unreachable in `linear`.** Specs like `[(L0e,8); (L1o,4); (L0e,8)]` are not
forbidden anywhere, but `linear` will only ever read the first L0e block.
Either forbid duplicate irreps in specs used by `linear`, or sum over all
matching input blocks.

**F4: tensor-product normalization is unspecified.** e3nn applies path
normalization (weights scaled by fan-in) so variance is stable at init; the
spec's §5.4 accumulates raw. Fine for semantics, but the spec should say
"normalization is the trainer's job" explicitly or define it — silent
divergence from e3nn numerics will otherwise surprise users porting models.

**F5 (confirmation, not a bug): the §14.2 claim "CG antisym → self-TP path
vanishes" is real.** Verified: 1⊗1→1 with identical inputs is exactly zero,
and the exchange-symmetry sign (−1)^(l1+l2−l3) survives the real basis.
Likewise the corrected product-symmetry doctrine note in the module doc
(§9, post-Coq): the n-body factorials are *joint* per identity group — the
implementation takes no per-dimension shortcuts anywhere.

**F6 (minor): §6.2's `Y_2^0` line writes `3z²−(x²+y²+z²)` against constant
0.31539157** — consistent and correct (= √(5/16π)); noted only because the
`0.28209479` L0 line and this one silently fix the normalization convention
("orthonormalized real solid harmonics") that the rest of the spec never
states. The addition-theorem test pins it.

## 4. File map

| File | Implements | Spec ref |
|---|---|---|
| `Irreps.fs` | `Parity`, `Irrep<L,p>`, spec entries, `dim`/`total_dim`/`parity_mul`, `sh_spec` | §2 |
| `IrrepsIdx.fs` | `IrrepsIdx<spec>` index-type contract: bijection + lex enumeration | §3; formalism §3.2 |
| `Wigner.fs` | 3j (Racah), complex CG, real-basis coupling tensors (dense + sparse), caching | §4 |
| `SphericalHarmonics.fs` | real solid harmonics `Y<L>`, `Y_to<L_max>`, closed-form recurrence | §6 |
| `Rotations.fs` | SO(3) sampling, fitted real Wigner D, block-diagonal rep action | test oracle |
| `TensorProduct.fs` | `TensorPaths<cfg>`, `WeightIdx<cfg>` layout, `all_valid_outputs`, sparse TP + dense reference | §5, §11.1 |
| `Linear.fs` | `LinearWeightIdx`, `all_irreps_present`, block-diagonal linear | §7, §11.2 |
| `Activations.fs` | `silu`/`sigmoid`/`relu`, gated + norm activations | §8 |
| `MessagePassing.fs` | `gather`, `scatter_add`, `equivariant_conv` | §9, §12 |
| `Autodiff.fs` | hand-written VJPs: linear/TP/gated/gather⇄scatter/conv/MSE | §10; module doc §11 |
| `TrainingOracle.fs` | 27-param invariant-regression model + fixed-seed GD run (e2e pins) | module doc §10 |
| `OracleDump.fs` | `-- dump-oracle`: dataset/CG tables/trajectory as pasteable literals | — |
| `TestHarness.fs`, `Tests_*.fs`, `Program.fs` | runner + 170 checks | — |

## 5. Autodiff layer (added with the compiler-AD arc)

`Autodiff.fs` holds hand-written VJPs for every op (linear, tensor product,
gated activation, gather⇄scatter, conv, MSE) — the executable semantics the
compiler's `grad` transform must reproduce. `TrainingOracle.fs` is a complete
27-parameter invariant-regression model (2 conv layers, gates, linear,
invariant readout) trained by full-batch GD from a fixed seed; its loss
trajectory and gradient snapshots are the value pins for the Blade e2e
example. `Tests_Autodiff.fs` verifies: per-op VJPs and the full model against
central finite differences, the gather/scatter adjoint duality, the
"dW nonzero at w = 0" trap (the forward's skip must not leak into the
backward), and rotation invariance of loss AND gradients. `OracleDump.fs`
prints everything (`-- dump-oracle`).

## 6. The equiv certificate (implemented 2026-07-18)

The Part I deferral is over: `where <alias>.equiv(O3)` / `equiv(SO3)` on a
function makes the compiler PROVE the body composes only
equivariance-preserving operations. Machinery (all compiler-side, in
`ml/compiler/MLEquiv.fs`, invoked by `MLElaborate.expand` at the seam
between sizing normalization and op rewriting, where `ml.*` calls are still
surface-visible):

- **Judgment**: abstract interpretation over the surface AST with the
  domain `Rep spec | Inv | Opaque`. Params/return seed from annotations
  (certified functions must be fully annotated; `Array<_ like
  IrrepsIdx<spec>>` → `Rep`, scalars/plain arrays → `Inv`). Admissible on
  reps: the ml ops (with their premises — under O3, `gated`/`scalars` need
  parity-even l=0 blocks), same-spec `+`/`-`, scaling by invariants,
  static reads inside invariant l=0 blocks, calls to same-group certified
  functions, and the invariant exits `ml.scalars`/`ml.norms`. Everything
  else — Hadamard products, componentwise nonlinearities, raw l>0 reads,
  escapes to uncertified callees, rep-capturing lambdas, destructuring —
  is **BL4008** at the offending expression's span. Schur violations
  (`hom_dim = 0` requests, unreachable TP outputs) are **BL4007**.
- **The certificate is a conditional theorem**: IF every rep-typed argument
  actually transforms as its declared spec (and y_to's coordinate scalars
  are the components of the standard vector), with invariants held fixed,
  THEN the output transforms as its declared spec. The plain-array seam
  (irreps-tag vs untagged unify) means uncertified callers can feed
  anything — that does not break the theorem, only its premise.
- **Derived layers**: `ml.derive_linear(SIN, SOUT[, w, x])` synthesizes the
  COMPLETE Schur basis of Hom_G (all (l,parity)-matched block pairs,
  pair-major weights, `hom_dim` total, unmatched output blocks zero-filled;
  duplicate input irreps reachable — F3 resolved);
  `ml.derive_tp(S1, S2[, x, y, w])` derives the full-CG output spec. The
  2-argument forms return the layer as a function value
  (`let layer = ml.derive_linear(SIN, SOUT)`). Weight buffers stay plain
  arrays, so `grad()` trains them unchanged.
- **Validated once, not per network**: `Rotations.applyRepImproper` (the
  parity extension) + O(3) improper-equivariance oracle tests (including
  the gated-odd-head COUNTEREXAMPLE showing the premise is real), and the
  corpus certificate tests (`tests/corpus/ml-equiv/018-021`) evaluating
  `f(D·x)` vs `D·f(x)` inside compiled Blade against `dump-equiv` pins.
  User networks need no empirical equivariance tests — their certificate is
  the type check.
- Grad interaction: adjoints are not re-judged (equivariance of the
  gradient is a theorem given the primal); ml-equiv/021 pins it.

Still deliberately absent:
- **normAct VJP**: the e2e example uses `gated`; add when consumed.
- **Open items** §13 / module doc §12: path filtering, attention, GPU fused
  kernels, r ≥ 3 Schur-component storage — all future work.
- Pseudoscalar algebra on invariants (an `InvOdd` status admitting
  odd×odd→even under O3), `y_to_v` (closing the coordinate premise into the
  type via a `[(1,1,1)]`-typed argument), rows variants inside certified
  bodies, `TyEquivIdx` generalization to further groups (Reynolds/finite).

## 7. Integration path into the compiler

1. `StaticEval.fs` gains irrep/spec/path evaluation (all pure functions of
   static data — `Irreps.fs` + `TensorProduct.paths` port directly).
2. `IndexTypeValidator.fs`/`Lowering.fs` treat `IrrepsIdx` as the DepIdx
   instance it is; the bijection here is the reference.
3. CG tables: `Wigner.realCGSparse` output becomes compile-time-emitted
   constant tables in generated C++ (the spec's "compiler generates CG tables
   for all paths actually used").
4. The differential harness gets a new oracle family: generated-C++
   `tensor_product`/`linear`/`Y_to` vs this project's values, plus the
   rotation-equivariance property tests as corpus entries.
