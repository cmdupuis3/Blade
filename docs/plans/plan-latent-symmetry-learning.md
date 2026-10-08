# Latent symmetry learning — discovery as deduction over data

Status: PLANNED 2026-10-08 (Fable + two Opus passes: a 46-program probe campaign
against the 2026-10-07 binary, §2, and a rigor review with numerical checks, §3 —
both preserved under [`docs/research/latent-symmetry/`](../research/latent-symmetry/))
— nothing built. The exact-discovery half (P0) needs no compiler work; the learning
half's compiler items are small and sized in §6. Two compiler bugs the campaign
surfaced: an `AntisymIdx` parameter under `ad.grad` corrupted memory — FIXED
2026-10-08 (element writes into compact storage now fold to the canonical cell in
both lanes, formalism §3.4; corpus `index-types/486`, `ad/041`–`042`); `ppl.moments`
returns flat canonical cells, not the documented `SymIdx` storage — chipped. The verdict this plan rests on is the 2026-10-01 readiness assessment:
static certified groups strong, learned Lie symmetry weak.

## 0. The pitch

LieGAN (Yang et al., ICML 2023) learns Lie-algebra generators of a data symmetry by
pitting a transformer `x ↦ exp(Σ wᵢLᵢ)·x` against a discriminator; LaLiGAN (Yang et
al., ICML 2024) does it in a learned latent `z = φ(x)`, so that a NONLINEAR data
symmetry becomes a LINEAR latent one, decoded back by `ψ`. The adversary is the
kludge: the learned `Lᵢ` need not close under the bracket (so the "group" is not a
group), the objective has no zero-iff-symmetric reading, and training is a
saddle-point search.

Blade's version keeps the latent-linear idea and removes the adversary with three
substitutions, each landing on something the language already has:

1. **The algebra is a nullspace, not a learned parameter.** For a fixed
   representation the admissible generators are the kernel of a quadratic form —
   moment-tensor annihilation `ρₖ(A)·Mₖ = 0` (§3.1), the Hessian of a Gaussian-kernel
   MMD at the identity (§3.2, exact for `sym(P)`), or the Lie-derivative identity
   `Dφ(x)·(Ax) − B·φ(x) = 0` (§3.3). Each kernel is a Lie subalgebra BY THEOREM, so
   closure is never a penalty; the solve is one `svd`/`eigh`, globally optimal (Ky
   Fan). The tensors are `where comm(…)` kernels over `method_for(Zt, …, Zt)` —
   symmetric storage, one fused sweep.
2. **Every two-sample statistic is a kernel MMD built from Gram matrices.**
   Polynomial-kernel MMD² IS weighted moment matching to order K, exactly, even for
   the V-statistic (§3.2); the Gaussian kernel is characteristic (zero iff equal).
   Three `gram(·, ·)` calls plus a range map and a fold — BLAS-routed forward, and
   `gram`'s adjoint is already in the AD lane (§2).
3. **A discovered algebra is decomposed and, when it is a roster group,
   CANONICALIZED into the certified stack.** Structure constants, Killing form, Levi
   and isotypic splits are exact linear algebra over `eigh`/`svd`/`lu_solve` (§3.4).
   A discovered `so(3)` on the latent is conjugated into the compiler's real-harmonic
   basis — verified end to end against `MLLieDischarge.blockGenerator` on a disguised
   `0⊕1⊕1⊕2` representation — the latent is redeclared `IrrepsIdx<spec>`, and the
   downstream model is built with `ml.derive_*` and certified by the EXISTING
   discharger, which never sees a float table.

The organizing idea is the one the compiler already uses for `comm` / `anticomm` /
`ml.equiv`: a lattice proposes, a pin commits. BL4011 proposes `where ml.equiv(SO3)`
for a polynomial body; this plan's discovery lane proposes the same clause (plus a
spec and a change of basis) for DATA. Discovery is deduction over samples; the user
pins what it proposes; the certifier owns the proof.

**The honest headline (§3.5).** What the rigor pass settled is WHICH half of
"latent symmetry learning" is a theorem. Discovery of the symmetry a GIVEN
representation exposes is exact. Learning the representation jointly is not
identifiable by ANY distributional objective — latent or data-space, moment or
kernel or discriminator: a Gaussianizing reparametrization `φ′ = T∘φ, ψ′ = ψ∘T⁻¹`
leaves reconstruction unchanged, makes every term vanish for every `A ∈ so(d)`, and
LaLiGAN's own latent GAN is exactly that vacuous condition. Identifiability has to be
supplied from outside the distribution: a frozen representation (exact, says what it
says), coupling to a task or to dynamics through the functional lane (plausible,
open), or a geometric constraint on `φ` (heuristic). The plan builds the exact half,
makes the coupling the v1 learning route, and reports a jointly learned symmetry as
"consistent with P under this encoder class and regularizer", never as "the symmetry
of P".

## 1. Verdict — why now, and what it is not

- The exact-discovery half (quadratic form → nullspace → decomposition →
  canonicalization → proposal) is forward-only linear algebra, and §2 shows every
  primitive it uses runs on today's binary: `gram`, comm-kernel moment tensors with
  mirrored reads and `decompact`, `m.svd` / `m.eigh` on declared-shape operands,
  `m.eig` (eigenvalues), `transpose`, `ad.jvp` / `ad.vjp` for Jacobian actions, range
  kernels over `SymIdx<3, D>` for the derivation action (verified against a dense
  reference to 1.7e-33).
- The learning half needs reverse AD only through `gram`, range-map kernels with
  scalar intrinsics, bound folds and `transpose` — all in the lane today (§2.2 lists
  the spelling rules). Every objective has an INFINITESIMAL form that needs no
  `expm` (§3.6): group elements are only formed for augmentation, Haar sampling, and
  the finite-t check on an inexact autoencoder.
- The compactness theorem (§3.1) is a free win: a distributional linear symmetry of
  a law with nondegenerate second moments is compact, so in whitened coordinates the
  unknown is `so(d)` — `d(d−1)/2` columns, not `d²`, and Blade's `AntisymIdx<2, Lat>`
  is literally that space. Non-compact candidates (Lorentz, `sl(2)`, `se(n)`) are
  never distributional; they belong to the functional lane, on a task head or on
  dynamics.
- What it is not: a general equivariant-NN-under-learned-group framework. A
  discovered algebra that is NOT a roster member (today: O(3)/SO(3), Sₙ, C4/D4) gets
  analysis and a penalized (soft) equivariance, never a certificate — the
  certifier's exactness is its one unsound failure mode and float tables would be a
  false axiom (`MLLieDischarge.fs` header). §7 D6 holds that line.

## 2. What the binary does today (probe campaign, 2026-10-08)

Forty-six probe programs against the 2026-10-07 binary (compiler 0.20.0, g++
ucrt64, BLAS on via `OPENBLAS_DIR`), every verdict from `blade run` (check + lower +
g++ + execute), hand values from numpy/scipy replays. The programs are preserved in
[`docs/research/latent-symmetry/probes/`](../research/latent-symmetry/probes/) and
become the `lie/` corpus in P0.

### 2.1 Capability table

| Construct | check | run | `ad.grad` | `ad.jvp` |
|---|---|---|---|---|
| polynomial-kernel MMD² from three `gram`s + a range-map kernel + bound folds | ok | ok | ok (FD check) | ok |
| Gaussian-kernel MMD² (norms from the Gram diagonals) | ok | ok | ok (FD check) | ok |
| `expm` as an unrolled scaling-and-squaring Taylor chain of `gram` products | ok | ok | ok (FD check) | ok |
| `expm`-action as an unrolled `gram_apply` chain | ok | ok | ok (vs scipy) | ok |
| matrix-valued `let rec` (squaring) / rank-2 vector-state `let rec` (Taylor action) | ok | ok | BL5500 (rank-1 scalar-slice only) | BL5501 |
| `ppl.moments(Zt, 3)` / `(Zt, 4)` at module level | ok | ok — typed FLAT `Idx<10>` / `Idx<15>` (canonical cells), not `SymIdx`; chip raised | — | — |
| `ppl.comoments(Zt, 3)` | BL5100 (order 2 only) | — | — | — |
| hand order-3 moment: `where comm(a, b, c)` kernel over `method_for(Zt, Zt, Zt)` → `SymIdx<3, D>` storage, mirrored reads, `decompact` | ok | ok | not run | not run |
| order-3 derivation action `ρ₃(A)M` as a kernel over `range<SymIdx<3, D>>` vs a dense reference | ok | ok (1.7e-33) | not run | not run |
| Lie residual `A·M + M·Aᵀ` via `gram`, Frobenius loss | ok | ok | ok (= 4RM, vs numpy) | not run |
| `AntisymIdx<2, D>` parameter under AD | ok | — | **memory corruption** (crash or wrong gradient) — FIXED 2026-10-08, `ad/041` | ok (FD check) |
| `m.svd(C)` nullspace (tall C, declared shape) | ok | ok | — | — |
| `m.eigh` on a compact `gram(Ct, Ct)` | BL5200 / BL3001 | — | — | — |
| `m.eigh` on `decompact(G, 0)` or on `gram(Ct, Ct2)` (two distinct arrays, dense) | ok | ok | — | — |
| `m.eig` (non-symmetric) | ok | ok — eigenvalues ONLY, as `(RE, IM)` | — | — |
| flat-state `let rec` over epochs, `ad.grad` through a helper function with a MATRIX parameter | ok | ok (vs numpy replay) | — | — |
| `r.normal_at` inside a function / inside a rec arm | BL6002 (top-level `let` only) | — | — | — |
| `{ }` block as a rec arm (no AD) / with `ad.grad` inside the block | ok / BL2001 "Unbound variable: ad" | ok / — | — | — |
| `ad.jvp` / `ad.vjp` of an array-valued encoder; `Jᵀr · xᵀ` assembled by hand | ok | ok (hand values) | — | — |
| `ad.grad` over a body that calls `ad.jvp` (reverse over forward) | — | — | BL5500 | — |
| `ad.jvp(ad.grad(g))` (the HVP route) | ok | ok | — | — |
| structure constants, Killing form, `eigh(K)` for `so(3)` | ok | ok (`K = −2I`) | — | — |
| `AntisymIdx<2, I>` literal, signed reads, matvec by range map, `decompact`, projection onto antisym storage | ok | ok | not run | not run |
| compact antisym array as a `gram` / `gram_apply` operand, or a partial row `L(1)` | BL3007 / BL3999 | — | — | — |

### 2.2 Spelling rules for differentiated bodies (each has a minimal refusal probe)

1. **Data enters as parameters.** A module-captured array used as a `gram` operand is
   BL5500 ("initialized by a reindexing combinator, whose shape is not statically
   known here") — contradicting equivariant-nn.md §11's "data enters by module-scope
   capture" for this operand class. Pass `X`, `Z` in; each gets an unused cotangent
   buffer.
2. **`gram` operands are named.** `let lt = transpose(l, [0, 1])` then `gram(z, lt)`;
   the inline `gram(z, transpose(l, [0, 1]))` is BL5500.
3. **No whole-array arithmetic on rank-2 locals.** `(1.0 + g / 3.0) ^ 3` is BL5500;
   `g * 2.0` and `g + h` pass expansion and die in the back end (BL7004 while
   emitting `f__grad`). Every elementwise stage is
   `method_for(range<N>, range<D>) <@> lambda(i: N, j: D) -> <scalar> |> compute`;
   intrinsics (`exp`, `tanh`) are fine inside the scalar body.
4. **No multi-slot `range<N, N>`**; write `method_for(range<N>, range<N>)`.
5. **Folds are bound.** `let rs = reduce(k, (+))` then `let s = reduce(rs, (+))`, or
   `reduce(k, (+), axes = 2)` over a local with static dims; an unbound fold inside an
   expression, or a fold over a `decompact` result, is refused.
6. **No `decompact`, no `prodsum`, no `if`/`else`** (BL5500 each). Mirrored reads of a
   compact `gram(x, x)` through a range map DO differentiate correctly; square in the
   map and fold.
7. **`let rec` is rank 1 with scalar slices and reads `prefix(n − 1)` only** (grad and
   jvp alike); a `zero :: n` seed arm is required. Anything matrix-valued is unrolled.
8. **Anonymous axes.** `gram`, `gram_apply`, `svd`, `eigh` results carry unnamed axes;
   mixing one with a named-axis array is BL3999 — re-tag through a range map, or use
   `[*]` (which keeps the tags) for outer products.
9. **`m.svd` / `m.eigh` need a declared operand shape** (BL5200); a compact gram
   reaches `eigh` only through `decompact` or a two-distinct-arrays `gram`.

### 2.3 What the campaign changes in the design

- **The moment formers are not the discovery lane's tensor source.** `ppl.moments`
  returns the canonical cells as a plain flat array (a `reduce` over it silently sums
  the 10 cells, not the 27 logical ones). The lane uses the comm-kernel spelling
  (`method_for(Zt, …, Zt) <@> mk` with `where comm(…)`), which IS the documented
  elaboration and yields real `SymIdx<k, D>` storage with mirrored reads and
  `decompact`; it agrees with the former's cells. When assembling the constraint
  matrix from symmetric storage, each row is scaled by `√(k!/∏ nᵢ!)` so row norms are
  Frobenius norms (§3.1) — otherwise the perturbation bound is wrong (the population
  nullspace is not).
- **`so(d)`-restricted search can use an antisymmetric-stored UNKNOWN under
  `ad.grad`** since the 2026-10-08 fix: element writes into compact storage fold to
  the canonical cell with the swap sign, and a write to an implicit zero drops —
  exactly the adjoint of the signed read (`ad/041`, with the `SymIdx` twin `ad/042`).
  The dense-vector-to-literal assembly remains a valid spelling.
- **The functional (Lie-derivative) residual is discovery-only in v1.** Its
  `A`-gradient is exact by hand (`2 Jᵀr xᵀ` from `ad.vjp` + `gram`), but a loss that
  calls `ad.jvp` cannot be reverse-differentiated (reverse-over-forward is BL5500),
  so the encoder is not trained against it until that rule exists (§6 P5).
- **Group noise is a module-level table.** `r.normal_at` is legal only as a top-level
  `let`; the indexed RNG makes `let w = r.normal_at(key, stream, 0, Epochs * k)`,
  read at `w(n * k + a)`, the natural spelling (cell identity survives re-chunking).
- **`m.eig` has no eigenvectors**, so the isotypic split (§3.4) uses SYMMETRIC
  elements of the commutant and `m.eigh` — which the rigor pass independently
  requires: a generic element has complex eigenvalues with positive probability.
- **`expm` is Taylor-18 at `θ = 1.09`**, not the probe's Taylor-6 (5e-12 truncation):
  with `s = ⌈log₂(‖A‖₁/θ)⌉` squarings the backward error is `u‖A‖₁` (§3.6).
- Two quirks for the census, not blockers: `ad.grad` inside a `{ }` rec-arm block is
  BL2001 `Unbound variable: ad` (the helper-function arm is the spelling);
  `blade run` prints no warnings (BL4003s show only under `check`).

## 3. The mathematical backbone (rigor review, condensed)

The full review with proofs, a 30-row claim ledger and six numerical check scripts is
[`docs/research/latent-symmetry/RIGOR.md`](../research/latent-symmetry/RIGOR.md).
Tags: THEOREM / HEURISTIC / OPEN. `gl(d)` carries the Frobenius inner product;
`Mₖ = E[z^{⊗k}]`; `ρₖ(A)` is the derivation extension
`(ρₖ(A)M)(i₁…iₖ) = Σₛ Σⱼ A(iₛ, j) M(i₁…j…iₖ)`; `sym(P) = {A : (e^{tA})_#P = P ∀t}`.

### 3.1 Distributional symmetry (B1)

- THEOREM. `d/dt|₀ E[p(e^{tA}z)] = 0` for every polynomial `p` of degree ≤ K ⟺
  `ρₖ(A)Mₖ = 0` for all `k ≤ K` ⟺ invariance of those moments for all `t`. Load-bearing
  step: `g^{⊗k} = e^{tρₖ(A)}` because the slot operators commute. At `k = 2` this is
  `A M₂ + M₂ Aᵀ = 0`.
- THEOREM. `g_K := {A : ρₖ(A)Mₖ = 0, k ≤ K}` is a Lie subalgebra — `ρₖ` is a Lie-algebra
  homomorphism — and equals `Lie(G_K)` for the algebraic group `G_K`. Consequences: no
  closure penalty; no irrational windings (a dense line in a torus brings the torus);
  whitening conjugates the answer and maps back exactly; cumulants give the same
  `g_K` (and are numerically better at `K ≥ 3`).
- THEOREM. `g₁ ⊇ g₂ ⊇ …` stabilizes at a finite `K₀`; `g_∞ = sym(P)` when `P` is
  moment-determinate (`E e^{ε|z|} < ∞` suffices, so every compactly supported law);
  FALSE in general — a log-normal-radius law in `R²` has every moment of a
  rotation-invariant law and is not rotation-invariant. HEURISTIC: concluding the
  chain has stabilized from a plateau.
- THEOREM (compactness). If `M₂` is finite and positive definite, the linear symmetry
  group of `P` is compact and `g_K ⊆ so(M₂) = M₂^{1/2} so(d) M₂^{−1/2}` for every
  `K ≥ 2`. After whitening `g₂ = so(d)` EXACTLY, so `K ≤ 2` carries one fact (the
  symmetry is orthogonal) and all selection lives at `K ≥ 3`; a Gaussian has maximal
  symmetry, which is what drives §3.5. Non-compact algebras are never distributional.
  Degenerate support (singular `M₂`) manufactures junk symmetries fixing every point
  — LaLiGAN's "fallacious symmetry" — removed by restricting to the support.
- THEOREM (finite samples). With `Ĉ = C + E`, gap `g = σ_{p−r}(C)`:
  `‖E‖₂ ≤ (Σₖ wₖ k² ‖M̂ₖ − Mₖ‖²_F)^{1/2}`, moment error `O(E‖z‖^{2k}/N)` (finite
  2K-th moments), rank recovered when `‖E‖ < τ < g − ‖E‖`, and
  `‖sin Θ(ĝ, g_K)‖ ≤ 2‖E‖/g`. The estimated span is closed only to `O(sin θ)`;
  classification (§3.4) and snapping to the identified algebra restore exactness.
  HEURISTIC: `τ` and `K` (the gap is a population quantity); calibrate by bootstrap
  or a spectral-gap ratio.

### 3.2 Kernel MMD as the sample-level form (B2)

- THEOREM. For `k(x, y) = (c + xᵀy)^K`,
  `MMD²(P, Q) = Σⱼ binom(K, j) c^{K−j} ‖Mⱼ(P) − Mⱼ(Q)‖²_F` — exactly, including for
  the V-statistic on empirical measures (relative error 3e-16 in the check). `c > 0`
  weights every order; whiten first so the weights are scale-free. Cost: Gram route
  `O(n²d)`, tensor route `O(n·binom(d+K−1, K))` — crossover at
  `n ≈ binom(d+K−1, K)/d`; both are available.
- THEOREM (CORRECTION to the brief). `F(t) = MMD²(P, e^{tA}_#P)` has `F(0) = F′(0) = 0`
  for EVERY `A` — the first derivative carries no information — and
  `F″(0) = 2 q(A)`, `q(A) = Σⱼ wⱼ ‖ρⱼ(A)Mⱼ‖²_F`. The sample-level statistic at the
  identity, to second order, IS the tensor-level nullspace problem. The Hessian is
  assembled from three Gram matrices for any dot-product kernel (`O(n²d² + nd⁴)`), or
  by polarization `H_pq = [q(B_p + B_q) − q(B_p) − q(B_q)]/2`, which is exact.
- THEOREM (new). For the Gaussian kernel and `E|z| < ∞`,
  `MMD²(P, e^{tA}_#P) = t² q_G(A) + o(t²)` with `q_G(A) = ‖E[∇₁k(z, ·)·Az]‖²_H`, and
  `ker q_G = sym(P)` EXACTLY — no moment determinacy, no `K`, no `expm`. Load-bearing
  step: `q_G(A) = 0` ⟺ `div(Az·P) = 0` as a distribution (Fourier side, `Ĝ > 0`) ⟺
  `(e^{tA})_#P = P` by differentiating test functions along the flow. Gram form:
  `q̂_G(A) = (1/n²) Σ_ab K_ab [⟨Ax_a, Ax_b⟩/σ² − ⟨u_ab, Ax_a⟩⟨u_ab, Ax_b⟩/σ⁴]`,
  `u_ab = x_a − x_b`, every inner product read off `gram(XAᵀ, X)` and `gram(X, X)`.
  Finite-sample caveat: the empirical form is positive definite (finitely many atoms
  are invariant under no flow), so a threshold is always needed; bandwidth `σ` is
  HEURISTIC (median rule); kernel-test power decays polynomially with dimension, so
  keep it in the latent.
- THEOREM (CORRECTION). The paired V-statistic between `X` and `gX` adds a diagonal
  `(2/n²) Σ_a (1 − k(x_a, gx_a)) ≥ 0` that vanishes only at `g = I`, so as a loss it
  strictly prefers `A = 0` to a true symmetry. Use the U-statistic (unbiased even for
  paired samples; its gradient is unbiased for minibatch SGD; it may go negative), or
  disjoint halves. The Gaussian V-Hessian differs from the U-Hessian by an isotropic
  shift after whitening (eigenvectors unmoved); the polynomial one does not — drop
  the `a = b` terms.

### 3.3 Functional (equivariance) symmetry (B3)

- THEOREM. For `f ∈ C²(U, Rᵐ)` on a connected open `U`,
  `s(f) = {(A, B) : Df(x)Ax = Bf(x) ∀x}` is a Lie subalgebra of `gl(d) ⊕ gl(m)`; the
  one identity
  `φ_{[A₁,A₂],[B₁,B₂]} = Dφ₁[A₂x] − Dφ₂[A₁x] + B₁φ₂ − B₂φ₁` does the work. Finite form
  ⟺ infinitesimal form on `U = Rᵈ` (Otto et al. 2025 Thm 4). Each sample contributes
  the block `[Df(xₙ) ⊗ xₙᵀ, −I_m ⊗ f(xₙ)ᵀ]`; the invariance case is LieGG's
  polarization matrix. Junk `B`-dimensions appear when `f(U)` does not span `Rᵐ`
  (restrict `B` to the span); a homogeneous `f` carries the Euler pair `(I, pI)`.
- CORRECTION. The count `Nm ≥ d² + m² − dim s(f)` is NECESSARY only. Generic
  sufficiency for real-analytic `f`: some `N_* ≤ codim s(f)` samples in general
  position make `ker C = s(f)` almost surely; deterministic for polynomial `f` on a
  unisolvent sample set (`binom(d+D, D)` points) — the same Zariski argument that
  makes the certifier's coefficient matching exact. Conditioning is not bought;
  the threshold stays HEURISTIC.
- THEOREM (counterexample). On data concentrated on a lower-dimensional set `V`, the
  sampled constraint space `s_V ⊇ s(f)` can FAIL to be a Lie algebra
  (`f = x₁ + x₂²` on `{x₂ = 0}`: `s_V` is 4-dimensional and not closed; the true
  `s(f)` is 1-dimensional). Remedies: latent dimension ≤ intrinsic dimension, or
  restrict to fields tangent to `V` (Otto et al. §7.1).
- Latent uses: a task head `h(z)` (where non-compact symmetries belong); latent
  dynamics `F̃ = φ∘F∘ψ` (LaLiGAN's setting; `DF̃(z)Az = AF̃(z)` is linear in `A`);
  encoder equivariance under a KNOWN data-space action (`Dφ(x)(A_data x) = A_lat φ(x)`,
  linear in `A_lat`). Only this family carries task information, and §3.5 says task
  information is where identifiability must come from.

### 3.4 Decomposing and canonicalizing (B4)

Every rank decision is a numerical-rank decision: exact for a closed algebra,
`O(θ)`-noisy for an estimate (tolerances HEURISTIC).

- Orthonormal basis: `svd` of the stacked `vec(Lᵢ)`; structure constants
  `c_ijᵏ = ⟨Eₖ, [Eᵢ, Eⱼ]⟩`; closure residual `[Eᵢ, Eⱼ] − Σₖ c_ijᵏ Eₖ` is the
  diagnostic. Killing form `κ_ij = Σ_kl c_ikˡ c_jlᵏ`.
- THEOREM (Cartan). Semisimple ⟺ `κ` nondegenerate; solvable ⟺ `κ(g, [g,g]) = 0`;
  `rad(g) = [g,g]^{⊥κ}` (a nullspace); Levi `g = s ⋉ rad(g)` with `s` non-unique
  (Malcev) — for an abelian radical the complement is a LINEAR solve (Whitehead's
  second lemma), by `gram` normal equations + `lu_solve`. For distributional
  symmetries none of this is needed: the algebra is compact, hence reductive,
  `rad = z(g)` and the Levi factor `[g,g]` is canonical.
- THEOREM. Compact semisimple ⟺ `κ` negative definite; more generally compact ⟺
  `g = z ⊕ [g,g]` with `κ < 0` on `[g,g]` ⟺ an ad-invariant inner product exists.
  Center `z(g) = ker ad`.
- Isotypic decomposition. The commutant `End_g(V) = {M : [M, Lᵢ] = 0}` is a
  nullspace; under complete reducibility (automatic for compact `g`) it is
  `⊕ M_{nᵢ}(Dᵢ)`, `Dᵢ ∈ {R, C, H}` (Frobenius–Schur / Wedderburn). CORRECTION: use a
  generic SYMMETRIC element in a frame where the `Lᵢ` are antisymmetric (the
  commutant is then closed under transpose); its `eigh` eigenspaces are single
  irreducible copies; the isotypic components are the eigenspaces of a generic
  symmetric element of the commutant's CENTER (one more nullspace). Type detection:
  `dim End_g(U) ∈ {1, 2, 4}`; complex/quaternionic types show as complex structures
  `J² = −I`, the "rotation-like blocks". Failure: non-completely-reducible actions
  (`se(n)` on homogeneous coordinates, Heisenberg) give a socle filtration, not a sum.
- Identification by `(dim, dim [g,g], dim z, dim rad, Killing signature, rank)`:
  complete for the compact and semisimple cases that can occur distributionally
  (first ambiguity at dim 21), NOT complete for solvable families (Bianchi VI_h /
  VII_h carry a parameter). `so(2)` vs `R` is decided by the representation
  (imaginary vs real spectrum); `u(1)` charges snap to integers (justified by the
  closed-group theorem; the snapping itself HEURISTIC). `so(4) = so(3) ⊕ so(3)` is
  split by running the commutant step on the adjoint representation.
- THEOREM (canonicalization to the certifier's `so(3)` tables; verified end to end on
  a disguised `0⊕1⊕1⊕2` representation at `d = 12`, `U Jₐ Uᵀ =`
  `MLLieDischarge.blockGenerator` entry by entry). Steps: (1) invariant inner product
  `M ≻ 0` with `Lᵢᵀ M + M Lᵢ = 0` — a random nullspace element may be indefinite, so
  take the spectral projector of the Casimir on bilinear forms (`lu_solve` on the
  left/right null bases), which equals the Haar average and is positive iff `g` is
  compact (a diagnostic); then `R = M^{1/2}` by `eigh` makes `R L R⁻¹` antisymmetric;
  (2) a `(−κ/2)`-orthonormal basis, orientation fixed by `c₁₂³ > 0` (negate all three
  otherwise) — the FRAME is a genuine `SO(3)` choice, harmless because step 4
  absorbs it; (3) Casimir `J_x² + J_y² + J_z²` by `eigh`: clusters `−l(l+1)` of
  multiplicity `n_l(2l+1)`; half-integer `−3/4, −15/4, …` means `SU(2)`, not `SO(3)` —
  the real-harmonic tables do not apply, flag and stop; (4) per block, the
  intertwiners `T Jₐ = Dₐ^{(l)} T` as a stacked nullspace (Schur: dimension `n_l`),
  Löwdin-orthonormalized and scaled by `√(2l+1)`; the residual freedom is `O(n_l)`
  per block (a sign per copy when `n_l = 1` — a true gauge). Equivalent ladder view:
  choose `e_{R₀}` (the sign), climb `e_{R_{m+1}} ∝ P_{m+1} L_y e_{R_m}` (positive
  coefficients by the table), set `e_{I_m} = L_z e_{R_m}/m`; `L_x` is then a CHECK.
  The Condon–Shortley phase is what makes `L_y` block-diagonal on `R/I`.
- THEOREM (impossibility). Parity and every discrete symmetry are invisible to every
  infinitesimal method (`Lie(O(3)) = Lie(SO(3))`). A finite test decides: for a
  multiplicity-free spectrum, `2^{#blocks}` candidates `ρ(−I) = I ⊗ S`, each tested
  by an MMD/moment test of `P` against `ρ(−I)_#P` — the certifier's own separate
  "−I identity" check; with multiplicities the search is non-convex, OPEN.

### 3.5 Joint latent learning: the degeneracy (B5)

- THEOREM (Knothe–Rosenblatt). For a smooth positive latent density `Q = φ_#P` there is
  a `C^r` diffeomorphism `T` with `T_#Q = N(0, I)`. Set `φ′ = T∘φ`, `ψ′ = ψ∘T⁻¹`:
  reconstruction is unchanged, `ψ′` is injective iff `ψ` is, and for EVERY `A ∈ so(d)`
  the latent moment residual, the latent Gaussian Hessian, every latent MMD or
  discriminator, AND the induced data-space action `ψ′∘e^{tA}∘φ′` preserve `P`
  exactly. The `P`-preserving diffeomorphisms form an infinite-dimensional group;
  a learned latent picks a finite-dimensional linear slice, and nothing in the
  distribution says which.
- THEOREM (CORRECTION). The data-space MMD through the decoder is the right OBJECTIVE
  — proper, zero iff the induced action preserves `P`, with an unbiased U-statistic
  and an `expm`-free infinitesimal form needing only decoder JVPs — but for an exact
  injective autoencoder it is EQUIVALENT to the latent condition (Lusin–Souslin), so
  it does not identify. LaLiGAN's adversarial term is in the LATENT (arXiv v3 Eq. 4),
  i.e. exactly the vacuous condition; its orthogonal last layer and latent centering
  remove only the collapse-type trivial solutions.
- Trivial solutions and normalizations: `A = 0` (any normalization); `Az = 0` on the
  support (latent collapse — restrict to the support); `Dψ(z)Az = 0` (motion along
  the decoder's fibers — removed by N3); the transport family (removed by NOTHING:
  it is non-identifiability, not triviality). N1 Stiefel `WᵀW = I`: automatic in the
  eigen-solution (Ky Fan), projection by the polar factor from a thin `svd` if `W` is
  ever gradient-updated — projection, not penalty. N2 fix the family `so(d)`
  (theorem-backed by §3.1). N3 normalize the INDUCED FIELD `E‖Dψ(z)Az‖² = aᵀG_ψa = 1`
  with `G_ψ = E[(DψᵀDψ) ⊗ zzᵀ]`: the discovery half becomes the generalized
  eigenproblem `Ha = λG_ψa` (`eigh` of `G_ψ` for the square root, then `eigh`), and
  it removes all three trivial families at once.
- What could supply identifiability — none a theorem in this setting: (1) freeze the
  representation and compute once (exact for what it is: "which symmetries does this
  representation expose"); (2) couple to a task or dynamics through §3.3 (a
  Gaussianizing `T` generically destroys the equivariance of `F̃ = φ∘F∘ψ` because
  conjugacy invariants constrain `T` — plausible, OPEN); (3) a geometric constraint on
  `φ` (near-isometry; exact only for intrinsically flat data — HEURISTIC); (4)
  architectural bias (what LaLiGAN implicitly relies on — HEURISTIC).
- The alternating scheme, exactly: step 1 (fix `φ, ψ`) assembles `H` (polynomial or
  Gaussian, U-version, plus the §3.3 rows when a task is present) and solves
  `Ha = λG_ψa` — THEOREM: the global minimizer of the step's quadratic (Ky Fan); the
  span is a Lie algebra at the population level and within the Davis–Kahan angle of
  one from samples. Step 2 (fix `W`) descends
  `L_recon + λ₁·MMD²_U(P, Γ_#P) + λ₂·tr(WᵀHW)/tr(WᵀG_ψW) + λ₃·R_identify` — local descent
  only. Safeguard: accept a new `W` only when the full objective decreases; then the
  value is monotone, nothing more. Without `λ₃` or task coupling the loop is
  self-reinforcing and its limit is set by initialization and inductive bias, not by
  `P` — report the output as "a symmetry consistent with `P` under encoder class `Φ`
  and regularizer `R`".

### 3.6 The exponential, and where it is not needed (B6)

- Not needed: §3.1–3.4 (linear or quadratic in `A`) and the realism term in its
  infinitesimal form (decoder JVPs). Needed: finite-`t` realism on an INEXACT
  autoencoder (then `Γ_t` is not a flow), augmentation, Haar sampling, discrete
  elements.
- THEOREM. Taylor degree `m = 18` at `θ₁₈ = 1.0909` with `s = max(0, ⌈log₂(‖A‖₁/θ)⌉)`
  squarings satisfies `T_m(2⁻ˢA)^{2ˢ} = e^{A+ΔA}`, `‖ΔA‖₁ ≤ u‖A‖₁` (Higham 2005's
  argument transplanted; `θ_m` computed with exact rational coefficients: 0.2996 /
  0.7803 / 1.0909 / 1.4383 / 2.2191 / 3.5397 for `m` = 12/16/18/20/24/30). Checked
  against SciPy at 2e-16 … 9e-15 for `‖A‖₁` from 0.27 to 97. Padé-13 (`θ = 5.37`, one
  `lu_solve`) is the alternative. Rounding in the squaring phase is benign for
  near-antisymmetric generators.
- THEOREM. AD through the algorithm (Horner tangents, then `dY ← Y dY + dY Y`) is the
  Fréchet derivative of the computed approximant and equals the exact `L(A+ΔA, E+ΔE)`
  with the same `ΔA` (Al-Mohy–Higham 2009; the Taylor analog via `T_m(X) = e^{X+h(X)}`,
  `‖L_h‖ ≈ 20u‖F‖`); reverse mode is `L(Aᵀ, ∇ℓ)`. Independent pins:
  `exp([[A, E], [0, A]]) = [[e^A, L(A,E)], [0, e^A]]` at 1e-12 for `‖A‖₁ ≤ 25`; the
  adjoint identity `⟨G, L(A,E)⟩ = ⟨L(Aᵀ,G), E⟩`; the `so(2)` closed form
  `L(θJ, E) = αe^{θJ} + βJe^{θJ} + (sin θ/θ)S`.
- THEOREM (CORRECTION). A Gaussian on the algebra has full support on a compact
  connected group (exp is surjective) but is NEVER Haar; on `SO(3)` the wide limit
  is the uniform-angle law, Haar has angle density `(1 − cos θ)/π`. Exact Haar through
  the same `exp`: uniform axis, angle from `(1 − cos θ)/π`. Small `t` certifies the
  identity component `G₀` (a neighborhood generates it) and is blind to `π₀`; after
  dividing by `t²` it loses nothing against the Hessian form — use the Hessian form
  directly and keep a moderate-`t` finite check for inexact autoencoders.

## 4. Architecture

Five layers, bottom up. L0 is compiler work; L1–L4 are Blade source (a stdlib module
plus notebooks), which is the project's own doctrine for the ml ops — elaborate to
ordinary Blade so `ad.grad` differentiates through it and codegen is unchanged.

### 4.1 L0 — compiler items (each small; §6 places them)

| Item | Why | Shape |
|---|---|---|
| `m.expm(A)` forward | finite group elements for augmentation, Haar sampling, the finite-`t` check | Taylor-18 scaling-and-squaring (§3.6) as a synthesized `__math_N` function in the `svdDecl` style (`MathElaborate.fs`), the squarings `gram` products, `(m, s)` fixed at elaboration from a static `‖A‖₁` bound or chosen at run time by a `while`-guarded budget; oracle pins from an F# replay |
| `m.matmul` adjoint | today BL5500 under `ad.grad`; `gram(a, lt)` with a bound transpose is the spelling that differentiates | route the `__math_matmul` marker through the `gram` adjoint (both adjoints are matmuls, BLAS-routed) — a `GradSweeps` arm beside `__math_lu_solve`. Optional: the Gram spelling is adequate |
| `m.expm` derivative (v2) | joint generator training | AD through the synthesized body if grad can inline `__math_N` bodies (§7 D7), else the explicit `L(Aᵀ, G)` rule; the block-triangular identity is the corpus check |
| `eigh` jvp/vjp (v2) | spectral regularizers inside a loss | closed form for distinct eigenvalues; a refusal (not a NaN) below a gap tolerance |
| reverse-over-forward (v2) | training `φ` against the Lie-derivative residual | a `grad` rule for `__jvp` calls, or the hand-assembled `2Jᵀr xᵀ` adjoint as a named-call rule |
| compact folds | `reduce` over `SymIdx` storage is BL3999; residual norms want it | second customer (after cosmology) for `plan-compact-sym-folds.md`; v1 reads through range maps or `decompact`s |
| `AntisymIdx` parameters under `ad.grad` | the memory-corruption hole | FIXED 2026-10-08: canonical element stores in both lanes (`renderIndexStore`, `ArrayOps.writeCompact`) |

### 4.2 L1 — `stdlib/lie.blade`

Objects. `type Lat = Idx<d>`, `type Gen = Idx<k>`; a `k`-dim subspace of `gl(d)` is one
rank-3 array `L: Array<Float like Gen, Lat, Lat>` — `L(a)` is the `a`-th generator by
dimensional currying. Samples are feature-major, sample-minor
(`Array<Float like Lat, Samp>`, the `ppl` fiber convention and what the comm kernels
co-iterate over); one `transpose` gives the `(Samp, Lat)` orientation the Gram
kernels contract over. In whitened coordinates the unknown space is `so(d)`
(§3.1) — `AntisymIdx<2, Lat>` is that space as storage, and the Cartan split
`gl(d) = so(d) ⊕ sym(d)` is `AntisymIdx ⊕ SymIdx`.

Algebra operations (forward, exact; §3.4 gives each one's theorem):

```blade sketch
// sketch -- the stdlib surface; bodies are gram / eigh / svd / reduce / lu_solve
function bracket(a: T^2, b: T^2) -> T^2            // a·b − b·a, two grams
function frob(a: T^2, b: T^2) -> T^0               // bound folds of a * b
function orthonormal_basis(L: T^3) -> T^3          // svd of the stacked vec matrix
function structure_constants(L: T^3) -> T^3        // c(i, j, k) = frob(bracket(L(i), L(j)), L(k))
function closure_residual(L: T^3, c: T^3) -> T^0   // the O(sin θ) defect, printed
function killing(c: T^3) -> T^2                    // Σ_kl c(i,k,l) c(j,l,k)
function radical_basis(L: T^3, c: T^3) -> T^3      // nullspace of x ↦ κ(x, [E_i, E_j])
function center_basis(c: T^3) -> T^2               // nullspace of ad
function invariant_metric(L: T^3) -> T^2           // Casimir projector on bilinear forms; ≻ 0 iff compact
function commutant_basis(L: T^3) -> T^3            // nullspace of M ↦ [M, L(a)], in the antisymmetric frame
function casimir(L: T^3) -> T^2                    // Σ_a L(a)·L(a) under the (−κ/2)-orthonormal basis
function classify(c: T^3) -> ...                   // (dim, [g,g], z, rad, signature, rank) → roster name or "unknown"
function canonicalize_so3(L: T^3) -> ...           // §3.4: metric → frame → Casimir split → intertwiners; spec + U
```

Actions: on vectors (`gram` / `gram_apply`); on `Symᵏ` by the derivation rule as a
kernel over `range<SymIdx<k, Lat>>` summing the slot contractions (the canonical-tuple
range makes the result symmetric BY CONSTRUCTION; verified §2); on an `IrrepsIdx`
block axis. The Lie derivative of an array-valued map is `ad.jvp(f)(x, A·x)` minus
`B·f(x)`.

Statistics: `mmd2_poly_u`, `mmd2_gauss_u` (U-statistics, §3.2), the Hessian forms
`hess_poly(Z, K, c)`, `hess_gauss(Z, σ)` assembled by polarization over the basis of
`so(d)` (exact), and the induced-field metric `G_ψ` from decoder JVPs (N3).

Sampling: `w = r.normal_at(key, stream, 0, Epochs * k)` as a module-level table; Haar on
`SO(3)` by uniform axis + angle from `(1 − cos θ)/π`; `g = m.expm(Σ_a w(a) · L(a))`.

### 4.3 L2 — discovery (`lie.discover`)

One entry point, three quadratic forms, one solver:

1. **Whiten** the latent codes (`eigh` of the covariance; drop null directions — they
   manufacture junk symmetries, §3.1), so the unknown is `so(d)`.
2. **Assemble `H`** on the `d(d−1)/2` basis `E_il − E_li`: the moment form
   `Σ_k w_k ‖ρ_k(B) M̂_k‖²` (rows scaled by `√(k!/∏nᵢ!)`, U-corrected, `k = 3..K` — cheap,
   interpretable, certifier-shaped), the Gaussian-kernel Hessian `q̂_G` (primary at
   the population level: exact for `sym(P)`), and, when a task head / dynamics /
   known data action is present, the §3.3 rows (the only rows that carry
   identifiability, and the only home for non-compact candidates). By polarization
   each form costs `O(p²)` evaluations of a three-`gram` statistic — `p = 28` at
   `d = 8`.
3. **Solve** `H a = λ G_ψ a` (N3 when a decoder exists; plain `eigh(H)` otherwise):
   the bottom eigenvectors below a gap are the generators — Ky Fan global optimum.
4. **Report the ladder.** The eigenvalue ladder IS the evidence; the gap and `τ` are
   printed, never hidden (both HEURISTIC).
5. **Decompose, classify, snap** (§4.2): print `(dim, [g,g], z, rad, signature, rank)`,
   the roster name or "unknown", the closure residual before and after snapping.

Sizes are tiny: `d = 8, K = 4` is 486 rows × 28 columns for the moment form.

### 4.4 L3 — learning a representation, with identifiability declared

Two modes, in order of rigor:

**Mode A — frozen representation (v1).** Train `(φ, ψ)` for reconstruction only (or
take any given encoder), then run L2 once. Exact for what it says: the symmetry
algebra this representation exposes. This is also how a non-learned latent (a
physical state vector, a known embedding) is analyzed.

**Mode B — task-coupled alternating (v1 learning route).** The data come with a
task — a latent dynamics `z_{t+1} = F̃(z_t)` (LaLiGAN's dynamical setting, the
lighthouse's choice), labels, or a known data-space action. Step 1 solves L2 WITH the
§3.3 rows; step 2 descends
`L_recon + λ₁·MMD²_U(data, decoded transformed) + λ₂·tr(WᵀHW)/tr(WᵀG_ψW) + λ₃·R_task`
with the generators frozen and the safeguard of §3.5 (accept `W` only on a
decrease). State is a flat-state `let rec` over epochs with a helper-function arm
(`ml-e2e/001`'s idiom); encoder/decoder layers are `gram(x, wt)` with bound
transposes and range-map activations (`tanh`, `softplus` — `if` is refused in reverse
mode and not wanted). Group elements are constants in step 2, so no `expm`
derivative is needed; the realism term uses the U-statistic at moderate `t`, the
`λ₂` term the Hessian form.

```blade sketch
// one outer round of Mode B (sketch; extents literal in the real file, data as parameters)
let Z0   = encode(w_phi, X)                             // (Lat, Samp), whitened in-lane
let gens = lie.discover(Z0, Fz, K, tol)                 // EXACT: Hessian + dynamics rows, eigh, ladder printed
let G    = sample_group(gens, w_table, round)           // expm of algebra draws, CONSTANT this round
function loss(w_phi: ..., w_psi: ..., x: ..., g: ...) -> Float = {
    let Z   = encode(w_phi, x)
    let Xr  = decode(w_psi, Z)
    let Zg  = act(g, Z)                                 // gram against a constant
    let Xg  = decode(w_psi, Zg)
    recon(Xr, x) + l1 * mmd2_gauss_u(Xg, x, sigma) + l2 * hess_ratio(Z, gens) + l3 * dyn_equiv(Z, gens)
}
let rec wtraj: Array<Float like Epochs, State> = ... | prefix :: n -> prefix :: sgd_step(prefix(n - 1), n)
```

**Mode C — unsupervised joint (research, P5).** The LaLiGAN objective without a
task. §3.5 says its limit is set by initialization and inductive bias; the plan
builds it only as the NEGATIVE CONTROL of §5 (the Gaussianization cell) and as a
testbed for the geometric regularizers of §3.5(3), each reported as a heuristic.

Every printout of a learned symmetry carries the sentence "consistent with `P` under
encoder class Φ and regularizer R".

### 4.5 L4 — the bridge to certificates

When `classify` names a roster group, `canonicalize_so3` runs §3.4's pipeline and the
lane PROPOSES, in the BL4011 shape:

```
proposed: let static latent_spec = [(0, 0, 2), (1, 0, 3), (2, 0, 1)]   // (l, parity, mult); parity set by the finite test or 0
proposed: type Latent = IrrepsIdx<latent_spec>
proposed: where ml.equiv(SO3)                                           // O3 only after the −I test passes
basis:    U written to <store>   (orthogonal, d x d; U J_a Uᵀ = the certifier's tables)
ladder:   σ = [1.2e-13, 3.1e-13, 2.8e-13 | 0.41, 0.77, ...]              // the evidence, verbatim
closure:  residual 2.1e-12 before snapping, 0 after
```

The user pins the spec and `U` (a provider store — a Blade program never writes
Blade source), the latent is re-encoded `z′ = U·z`, and the downstream model is
`ml.derive_linear` / `ml.derive_poly` over `Latent` — certified by the existing
discharger with its own exact tables. Half-integer Casimir clusters stop the bridge
with an `SU(2)` flag; a non-roster algebra stops it with the analysis report (D6).

## 5. The lighthouse experiment (what P0–P4 build and pin)

A synthetic problem whose answer is a theorem, in the NB3 tradition
(`plan-equivariant-nn-notebooks.md` §3b): every cell's pin is predicted before it
runs.

Data. True latent `z ∈ R³` drawn from a ROTATION-INVARIANT, NON-GAUSSIAN law (NB3's
radial scale-mixture — `heavy` — already exists in
`examples/tools/make_moments_zarr.fsx`; it is `so(3)`-invariant with non-Gaussian
order-4 moments, so the order-4 algebra is exactly `so(3)` — dimension 3 — with no
`so(d)` inflation from whitening). A latent DYNAMICS `z_{t+1} = F(z_t)` that is
rotation-EQUIVARIANT and non-linear (a radial-dependent rotation about the
current axis, say) supplies Mode B's task rows. Observation `x = Ψ(z) ∈ R¹⁶` for a
fixed random smooth embedding (two `tanh` layers, full-rank Jacobian — the data
symmetry is NONLINEAR in `x`). `N ≈ 2000` samples, seeded, one `.fsx`, one zarr
store.

| cell | computes | pin (predicted) |
|---|---|---|
| D1 | comm-kernel moments of `Ztrue`, `k = 3, 4`; the moment form on `so(3)`'s basis; `svd` ladder | exactly 3 singular values at machine zero, the 4th bounded away; at `K = 2` ALL of `so(3)` is null (the whitening theorem, pinned) |
| D1′ | the Gaussian-kernel Hessian `q̂_G` by polarization; `eigh` | the same 3-dim nullspace; eigenvalues never exactly zero (finitely many atoms), the gap printed |
| D2 | orthonormal basis, structure constants, closure residual, Killing form | `κ = −2 I` after normalization; `[L_x, L_y] = L_z` residual ≈ 1e-13; `(dim, [g,g], z, rad, sig, rank) = (3, 3, 0, 0, (0,3,0), 1)` → `so(3)` |
| D3 | invariant metric, commutant, Casimir split | metric `≻ 0`; commutant `R·I`; Casimir `−2 = −l(l+1)` at `l = 1`, one block |
| D4 | `canonicalize_so3` vs `MLLieDischarge.blockGenerator` (via the oracle dump) | `U Jₐ Uᵀ` equals the `l = 1` tables entry by entry, up to the named sign gauge |
| D5 | the functional lane on the dynamics: `(A, A)` pairs with `DF(z)Az = AF(z)` | nullspace dimension 3, the same `so(3)`; the Euler pair absent (F not homogeneous) |
| D6 | a non-compact control: the functional lane on a Lorentz-INVARIANT scalar head `h(z) = z₀² − z₁² − z₂²` | `so(2,1)` with signature `(2,1,0)` — found by the functional lane; ABSENT from every distributional form (the compactness theorem as a pin) |
| M1 | polynomial-kernel `MMD²_U` between `Z` and `e^{tA}Z` for `A ∈ so(3)` vs `A ∉ so(3)`; `F″(0)` by finite differences vs `2q(A)` | zero (to U-noise) vs positive; the factor-2 identity to 1e-6 relative |
| M2 | Gaussian `MMD²_U` in DATA space between `X` and `Ψ(e^{tA}Z)`; V- vs U-statistic at a true symmetry | U ≈ 0, V = `O(1/n) > 0` (the pairing trap as a pin) |
| T1 | Mode B on `(X, dynamics)`: alternating rounds with the safeguard | final discovery on `φ(X)`: dimension 3, signature `(0,3,0)`, the dynamics rows' residual at the found algebra ≈ 0; the objective monotone across rounds |
| T2 | the bridge: pin the proposed spec, re-encode by `U`, train `ml.derive_linear` + `ml.gated` on a rotation-invariant target `where ml.equiv(SO3)` | the certificate ACCEPTED at compile time; readout invariant under fresh baked rotations of `z` to 1e-13 |
| N1 | THE NEGATIVE CONTROL: Mode C (no task) with a Gaussianizing-capable encoder on a latent whose true symmetry is `so(2)` | discovery returns `so(3)` at the end of training — the Knothe–Rosenblatt vacuity as a pinned outcome, not a surprise; the printout carries the "consistent with" sentence |
| N2 | an `so(2)`-invariant latent with `z₃` free, Mode A | dimension 1, abelian (`κ = 0`), commutant with a complex-type block (`dim End = 2`), charge snapped to 1 |

T1's honest outcome space is the same as NB3's: a failure is a finding. The findings
this plan already expects: the MMD bandwidth and the ladder threshold are heuristics
(reported as such); the first alternating round may expose `so(16)` inflation if the
decoder starts near-linear — the compactness theorem predicts it and the `K ≥ 3`
rows remove it.

## 6. Phases

| phase | deliverable | compiler work | gate |
|---|---|---|---|
| P0 discovery lane, Blade source | `stdlib/lie.blade` v0 (algebra ops, derivation action, the three quadratic forms, `eigh`/`svd` solve, decomposition, classification, `canonicalize_so3`); corpus `lie/` with pins from a new oracle verb (`oracles/ml`: `lie` dump — Killing, Casimir, canonicalization against `blockGenerator`, in plain F#); cells D1–D6, N2 | none expected (§2) | D1–D6 pins green; `blade test interp lie` agrees |
| P1 the statistics | `mmd2_poly_u`, `mmd2_gauss_u`, `hess_poly`, `hess_gauss`, `G_ψ`; cells M1–M2; the factor-2 and pairing-trap identities as corpus pins; jvp-vs-grad residual pins on both MMDs (`ad-jvp-comb` style) | optional `m.matmul` adjoint | M1–M2 green |
| P2 `m.expm` + sampling | synthesized Taylor-18 `expm`; oracle pins (rotation, nilpotent, the block-triangular identity at 1e-12 even though v1 never differentiates it, the `so(2)` closed form); Haar sampler on `SO(3)` | one `MathElaborate` declaration in the `svdDecl` shape | `math/` pins across the `‖A‖₁` scaling thresholds |
| P3 Mode B trainer | cell T1 on the dynamics data, compiled lane (`blade compile` + run the exe — the 120 s `run` ceiling), `plot.stream` channels for the objective and the ladder per round | whatever it surfaces (expected: literal extents in grad code, census #29/#36; the rec-arm `ad` quirk) | T1 pins |
| P4 the bridge | `canonicalize_so3` proposal printout, the `U` store, cell T2 | none — `IrrepsIdx`, `ml.derive_*`, the discharger untouched | T2: certificate accepted, invariance to 1e-13 |
| P5 research | Mode C + the geometric regularizers (N1 as the control); joint generator training (needs the `expm` derivative); training `φ` against the Lie-derivative residual (needs reverse-over-forward); the multiplicity parity search; non-roster soft equivariance (D6) | §4.1 rows 3–5 | each its own plan row when scheduled |

Sizes relative to recent arcs: P0 ≈ a corpus directory plus one oracle verb (the ml
oracle already holds the Wigner/rotation reference code it reuses); P2 is one
`MathElaborate` declaration; P3 is the notebook build and whatever it surfaces —
the open-ended item.

## 7. Decisions held for the user

- **D1 layout**: latent samples feature-major `(Lat, Samp)` (the comm kernels and
  `ppl` convention; one `transpose` for the Gram kernels). Recommendation: yes.
- **D2 home**: `stdlib/lie.blade` (Blade source, read at run time, no rebuild, AD
  sees it) versus elaborated `ml.lie_*` ops (compiler-side, can stamp index types).
  Recommendation: stdlib for everything whose body is ordinary Blade; elaboration
  only for `expm` — the split the ml ops drew.
- **D3 v1 learning route is Mode B (task-coupled)**, with Mode A as the exact
  default and Mode C research-only. Recommendation: yes — it is the only route the
  rigor pass leaves with a plausible identifiability story, and it needs no new AD
  rule.
- **D4 `so(d)` by default**: whiten and search `so(d)` (theorem-backed), `gl(d)` only
  in the functional lane. Recommendation: yes; the `AntisymIdx` chip decides whether
  the unknown can be antisymmetric-STORED under AD, which the discovery half never
  needs.
- **D5 proposal channel**: the lane prints its proposal (spec, clause, `U` store
  path, ladder, closure residuals) for the user to pin — the BL4011 UX — versus a
  `deduced[]` entry in `ide check --json`. Recommendation: print in v1; the
  structured channel belongs to the compiler's own deduction.
- **D6 non-roster algebras never get a certificate**: analysis plus a penalized
  (soft) equivariance with a printed residual, never `where ml.equiv`.
  Recommendation: hold the line (a float table inside an exact discharger is its
  header's one unsound failure mode).
- **D7 `expm` as Blade source or runtime C++**: synthesized Blade (differentiable
  later for free IF grad can inline `__math_N` bodies — unverified; `svd` under grad
  was not probed) versus a `blade_linalg.hpp` routine behind a marker (faster,
  explicit AD rules). Recommendation: synthesized; `d ≤ 32` makes the runtime
  irrelevant.
- **D8 which identifiability ingredient Mode B leans on first**: latent dynamics
  (the lighthouse), labels, or a known data-space action. Recommendation: dynamics
  — it is LaLiGAN's own showcase setting and the one where the "conjugacy invariants
  constrain `T`" argument is strongest.
- **D9 primary quadratic form**: Gaussian Hessian (exact for `sym(P)`, bandwidth
  heuristic, `O(n²)`) or the moment chain (finite `K`, certifier-shaped, moment-
  indeterminacy caveat). Recommendation: both printed, the Gaussian ladder decides,
  the moment ladder explains which order broke the symmetry.

## 8. Risks and kill conditions

- **Non-identifiability of a learned latent (THEOREM).** The headline risk is not a
  risk but a fact: no distributional objective identifies. Mode B's dynamics
  coupling is plausible and OPEN; N1 pins the failure mode so it can never be
  mistaken for a result. Kill condition for Mode B: T1 returns an algebra whose
  dynamics-row residual is not small while the distributional rows are — the
  coupling then did not bite, and the plan reports it.
- **Thresholds and bandwidths are heuristics** (`τ`, `K`, `σ`); the gap is a
  population quantity. Every printout carries the ladder.
- **Finite-sample closure defect is `O(sin θ)`**, never zero; classification and
  snapping restore exactness, and the before/after residuals are printed.
- **Structured latents break the functional lane** (§3.3 counterexample): keep the
  latent dimension ≤ the intrinsic dimension, or project onto tangent fields.
- **Moment indeterminacy** can make `g_∞ ⊋ sym(P)`; the Gaussian form has no such
  hole, which is why D9 prints both.
- **Parity.** No infinitesimal method sees `−I`; the bridge proposes `SO(3)` and runs
  the finite test on the `2^{#blocks}` candidates for multiplicity-free spectra; with
  multiplicities it is OPEN and the proposal says so.
- **Kernel-test power decays with dimension**: the Gaussian form stays in the latent;
  the data-space realism term is a check, not the discovery operator.
- **Compiler surface.** The expected refusals (block rec-arm bodies, literal extents
  in differentiated code, compact folds, the rec-arm `ad` quirk) are census rows
  with workarounds; the two chipped bugs are the only soundness items.

## 9. Relation to existing plans and docs

- `plan-equivariant-nn-notebooks.md` §3b (NB3): the data generator, the whitening
  theorem cells, the flat-state trainer and the rung-ladder narration are reused
  wholesale; the gap census there (rows 29, 36, 54, 55) bounds what P3 may hit.
- `plan-compact-sym-folds.md`: the derivation-action residual norm is a fold over
  `SymIdx` storage; v1 reads through range maps, and this plan is the second named
  customer.
- `structural/05-structured-operators.md`: `expm_apply(A, x)` (the exponential's
  ACTION, never the matrix) is the natural sibling of `gram_apply` if latents grow;
  not needed at `d ≤ 32`.
- `plan-forward-mode-ad.md` / `plan-ad-combinators.md`: the Lie derivative is
  `ad.jvp` of an array-valued map; reverse-over-forward (P5) is the first consumer
  that needs `grad` to see a `__jvp` call.
- `docs/features/equivariant-nn.md` §12 item 7 ("user-defined representations beyond
  built-in L0..Ln") is answered NEGATIVELY by D6 for float tables and POSITIVELY for
  roster groups by the bridge — the representation is user-DISCOVERED, then
  compiler-OWNED. §11's "data enters by module-scope capture" needs the §2.2 rule-1
  caveat.
- `docs/features/ppl.md` §2.1: the chipped `ppl.moments` type drift; until it lands,
  the comm-kernel spelling is the lane's tensor source.
- `docs/research/latent-symmetry/`: the rigor review (`RIGOR.md`, with its claim
  ledger and references), its numerical checks (`checks/*.py`), and the probe
  programs (`probes/`).
