# Rigor review B: latent symmetry discovery by exact linear algebra

**Scope.** This review checks which claims of the plan are theorems and gives the proofs, with
the load-bearing step spelled out. It gives explicit algorithms over Blade's primitives:
moment/cumulant tensors with symmetric storage, `gram`, reductions, `eigh`, `svd`, `eig`,
`lu_solve`, and forward/reverse AD. It also flags every place where the plan relies on a
heuristic.

**Status tags.**
- **[THEOREM]**: proved here or cited.
- **[HEURISTIC]**: a judgment call with no proof behind it.
- **[OPEN]**: not settled.
- **[CORRECTION]**: the brief or the plan states something false or imprecise.

Numerical spot-checks of the non-obvious identities are in `checks/*.py` beside this file
(Appendix A). There is no Blade code here.

---

## 0. Verdict in brief

1. **Closed algebra by construction: true for the population, only approximately true from
   samples.**
   - The moment nullspace g_K and the functional nullspace s(f) are Lie subalgebras
     (theorem; B1.2, B3.1).
   - A finite-sample estimate is a subspace within a Davis-Kahan/Wedin angle θ of the true
     algebra. Its closure defect is O(θ), not zero (B1.5).
   - "No closure penalty needed" is correct. "Exactly closed from data" is not.
   - The exact object is recovered by classifying the estimate (B4) and snapping to the
     identified algebra.

2. **[THEOREM, new to the plan] Exact distributional linear symmetries are compact.**
   - If E[zzᵀ] is finite and positive definite, the linear symmetry group of the law is
     compact. Also, g_K ⊆ so(M₂) for every K ≥ 2, and so(M₂) is conjugate to so(d) (B1.4).
   - In whitened coordinates you can therefore restrict the search to so(d) without losing
     anything.
   - sl(2,R), so(1,3), se(n) and the Heisenberg algebra can never be exact distributional
     symmetries of such data. They can only appear as functional symmetries (B3).
   - So LieGAN's reported Lorentz symmetry on top tagging cannot be exact invariance of the
     joint law. At best it is approximate invariance over the sampled range of group elements.

3. **Polynomial-kernel MMD is exactly moment matching, even for the V-statistic (B2.1).**
   - **[CORRECTION]** The infinitesimal claim is off by a factor of 2 (B2.3).
   - The first t-derivative at the identity is zero for every A, so it carries no
     information.
   - The second derivative is 2·Σ_j w_j‖ρ_j(A)M_j‖², which is twice the B1 quadratic form.

4. **[THEOREM, new to the plan] A characteristic-kernel infinitesimal test recovers sym(P)
   exactly.**
   - For the Gaussian kernel, take the Hessian at t = 0 of t ↦ MMD²(P, e^{tA}_#P). It is a
     PSD quadratic form in A, assembled from three Gram matrices plus row norms.
   - Its kernel is exactly sym(P), assuming only E|z| < ∞ (B2.5). It needs no moment
     determinacy, no choice of K, and no expm.
   - As a population criterion it strictly dominates the moment chain.

5. **[CORRECTION, the most important one] Moving the symmetry condition into data space does
   not cure the latent degeneracy.**
   - The brief is right that the transport (Knothe-Rosenblatt) argument makes "the latent
     distribution has a linear symmetry" vacuous.
   - The brief is wrong that stating the condition in data space, through the decoder, fixes
     this. With an injective decoder and an exact autoencoder, the data-space condition is
     *equivalent* to the latent one. The same transport construction satisfies both, for
     every A ∈ so(d), with reconstruction unchanged (B5.2, B5.3).
   - For a **fixed** autoencoder, the data-space MMD is still the right *objective*: it is
     proper, non-adversarial, and zero iff the action is invariant.
   - Identifiability of a learned latent symmetry must come from somewhere else: a frozen
     encoder, a geometric constraint on the encoder, or coupling to a task or to dynamics.
     As of now that is **[HEURISTIC]/[OPEN]**.

6. **[CORRECTION] LaLiGAN's adversarial term lives in the latent space.**
   - The loss is L_GAN = E[log D(φ(v)) + log(1 − D(π(g)φ(v)))] (arXiv v3, Eq. 4).
   - The paper appeared at **ICML 2024**, not ICLR.
   - So LaLiGAN is exposed to exactly the degeneracy in item 5. Its orthogonal last encoder
     layer and its latent centering only rule out collapse-type trivial solutions (B5.7).

7. **[CORRECTION] B3's sample count is necessary, not sufficient (B3.3).**
   - The count is Nm ≥ d² + m² − dim s(f).
   - On data concentrated on a lower-dimensional set, the sampled constraint space can even
     fail to be a Lie algebra. B3.4 gives an explicit counterexample.

8. **The so(3) canonicalization in B4 is exact, up to choices that are unavoidable and named.**
   - The choices are: the SO(3) frame, an O(n_l) mixing of multiplicity copies, a sign per
     copy, and the O(3) parity bit.
   - The parity bit is invisible to every infinitesimal method.
   - The pipeline was verified end to end against the certifier's own generator tables
     (`MLLieDischarge.blockGenerator`), on a disguised 0⊕1⊕1⊕2 representation (B4.viii,
     Appendix A).

9. **[CORRECTION] Paired V-statistics pull toward the trivial solution.**
   - Computing MMD between X and gX with the V-statistic adds a nonnegative diagonal term
     that vanishes only at g = I.
   - So the V-statistic strictly prefers A = 0 over a true symmetry.
   - Use the U-statistic, or compare two disjoint halves of the data (B2.6).

10. **[CORRECTION] A Gaussian on the algebra is not Haar measure on the group, not even in the
    wide limit.**
    - For so(3), a wide Gaussian tends to a *uniform* rotation-angle law.
    - Haar measure has angle density (1 − cos θ)/π (B6.4).

---

## Conventions

- gl(d) carries the Frobenius inner product ⟨A, B⟩ = tr(AᵀB).
- Vectorization is **row-major**: a = vec(A) with a_{(i,k)} = A_{ik}. Then:
  - Ax = (I_d ⊗ xᵀ) a,
  - ⟨Ax, y⟩ = (y ⊗ x)ᵀ a,
  - Df(x) A x = (Df(x) ⊗ xᵀ) a.
- M_k = E[z^{⊗k}] ∈ Sym^k(R^d) is the raw moment tensor, and κ_k is the cumulant tensor.
- ρ_k(A) = Σ_{s=1}^{k} I^{⊗(s−1)} ⊗ A ⊗ I^{⊗(k−s)} is the derivation (Leibniz) extension. In
  indices:

      (ρ_k(A)M)(i₁…i_k) = Σ_s Σ_j A(i_s, j) M(i₁…i_{s−1} j i_{s+1}…i_k).

- sym(P) := {A : (e^{tA})_# P = P for all t}, and G_P := {g ∈ GL(d) : g_# P = P}.
- u = 2⁻⁵³ is the unit roundoff.

---

## B1. Distributional symmetry as moment-tensor annihilation

### B1.1 Theorem: moment annihilation [THEOREM]

Assume P has finite moments of order ≤ K. Let A ∈ gl(d) and g_t = e^{tA}. The following are
equivalent:

- (a) d/dt|₀ E[p(g_t z)] = 0 for every polynomial p of degree ≤ K;
- (b) ρ_k(A) M_k = 0 for every k ≤ K;
- (c) E[p(g_t z)] = E[p(z)] for every polynomial p of degree ≤ K and every t ∈ R.

The low orders read as follows.
- k = 1: A M₁ = 0, that is, A μ = 0.
- k = 2: (ρ₂(A)M₂)(i₁,i₂) = Σ_j A(i₁,j)M(j,i₂) + Σ_j A(i₂,j)M(i₁,j), that is,
  **A M₂ + M₂ Aᵀ = 0**.

**Proof.**
1. Write p = Σ_{k≤K} ⟨T_k, z^{⊗k}⟩ with T_k ∈ Sym^k. This is possible because every
   polynomial splits uniquely into homogeneous parts.
2. Then E[p(g_t z)] = Σ_k ⟨T_k, g_t^{⊗k} M_k⟩. This is a *finite* combination of moment
   entries whose coefficients are analytic in t, so no dominated-convergence argument is
   needed.
3. The load-bearing step is

       g_t^{⊗k} = e^{t ρ_k(A)}.

   It holds because ρ_k(A) is a sum of the k operators A_s, where A_s means A acting in slot
   s. These commute pairwise, and e^{tA_s} acts as e^{tA} in slot s.
4. Hence d/dt|₀ E[p(g_t z)] = Σ_k ⟨T_k, ρ_k(A)M_k⟩.
5. ρ_k(A) commutes with slot permutations, so ρ_k(A)M_k ∈ Sym^k. The Frobenius pairing
   restricted to Sym^k is nondegenerate, and the T_k range independently over Sym^k. This
   gives (a) ⟺ (b).
6. Finally, (b) ⟺ (c) because e^{tρ_k(A)}M_k = M_k for all t if and only if ρ_k(A)M_k = 0. ∎

### B1.2 Theorem: g_K is a Lie subalgebra, the Lie algebra of a closed algebraic group [THEOREM]

Define

    g_K := {A : ρ_k(A)M_k = 0 for all k ≤ K},
    G_K := {g ∈ GL(d) : g^{⊗k}M_k = M_k for all k ≤ K}.

Then g_K is a Lie subalgebra of gl(d), and g_K = Lie(G_K).

**Proof 1 (bracket).**
1. ρ_k is a Lie-algebra homomorphism:

       [ρ_k(A), ρ_k(B)] = Σ_{s,t} [A_s, B_t] = Σ_s [A,B]_s = ρ_k([A,B]).

   The middle step uses [A_s, B_t] = 0 for s ≠ t.
2. If ρ_k(A)M = 0 and ρ_k(B)M = 0, then
   ρ_k([A,B])M = ρ_k(A)ρ_k(B)M − ρ_k(B)ρ_k(A)M = 0.
3. g_K is an intersection of linear subspaces, each closed under the bracket. ∎

**Proof 2 (group).**
1. G_K is defined by polynomial equations, so it is a closed algebraic subgroup of GL(d).
2. By Cartan's closed-subgroup theorem, its Lie algebra is
   {A : e^{tA} ∈ G_K for all t} = {A : e^{tρ_k(A)}M_k = M_k for all t, k ≤ K} = g_K. ∎

**Consequences.**
- **No closure penalty.** A nullspace computation returns a closed algebra.
- **No irrational windings.** g_K is the Lie algebra of an algebraic group. So it never
  contains a dense line in a torus without also containing the whole torus.
- **Equivariance under change of coordinates.** For any L ∈ GL(d),
  g_K(L_# P) = L g_K(P) L⁻¹, because ρ_k(LAL⁻¹) L^{⊗k} = L^{⊗k} ρ_k(A). In particular, any
  data-dependent whitening just conjugates the answer, and the answer maps back exactly.
- **Cumulants give the same algebra:** g_K = {A : ρ_k(A)κ_k = 0 for all k ≤ K}.
  - Reason: the map (M₁…M_K) ↦ (κ₁…κ_K) is a polynomial bijection with a polynomial
    inverse. It is GL(d)-equivariant, since κ_k(g_#P) = g^{⊗k}κ_k(P). So the two stabilizer
    groups coincide.
  - For K ≥ 3, cumulants are the numerically better input because the Gaussian part is
    removed. The population answer is identical either way.

**Contrast with LieGAN [verified].**
- LieGAN learns the generator weights L_i by gradient descent. Its regularizers are a
  cosine-similarity penalty between channels and an input/output similarity penalty.
- It does not enforce or guarantee closure. The paper concedes that the learned basis
  "inevitably has some numerical error".
- LaLiGAN likewise checks closure after the fact. It does so for Lotka-Volterra, and on top
  tagging it compares structure constants with so(1,3).
- So the plan's advantage over LieGAN is real, at the population level.

### B1.3 The chain g₁ ⊇ g₂ ⊇ … ⊇ g_∞, and when it reaches sym(P)

**Inclusion and stabilization [THEOREM].**
- g_{K+1} ⊆ g_K, because constraints only accumulate. So dim g_K is a nonincreasing integer
  sequence, eventually constant.
- Let g_∞ := ∩_K g_K. Since G_∞ is an algebraic group, the Hilbert basis theorem gives
  G_∞ = G_{K₀} for some finite K₀.
- **K₀ cannot be computed from data.** A plateau in dim g_K can be temporary, so concluding
  that "the dimension has stopped dropping" is **[HEURISTIC]**.

**sym(P) ⊆ g_∞ always [THEOREM].** An invariant law has invariant moments.

**g_∞ = sym(P) when P is moment-determinate [THEOREM].**
- Proof: if A ∈ g_∞, the law (g_t)_#P has moments g_t^{⊗k}M_k = M_k. Since P is determined by
  its moments, (g_t)_#P = P. ∎
- A fully classical sufficient condition is E[e^{ε|z|}] < ∞ for some ε > 0. This covers every
  compactly supported P.
  - Under this condition, each 1-D projection ⟨θ, z⟩ has a moment generating function near 0,
    so its law is determined by its moments (Billingsley 1995, §30). Those moments are the
    contractions of M_k with θ^{⊗k}.
  - The Cramér-Wold device (Billingsley 1995, §29) then determines P from all projections.
- A weaker sufficient condition is the multivariate Carleman condition (Schmüdgen 2017).

**Counterexample: g_∞ can be strictly larger than sym(P), even with all moments finite
[THEOREM].**
- Work in R². Let Z = R(cos Θ, sin Θ), where log R ~ N(0,1). Conditionally on R, let Θ have
  density

      (1 + ε sin(2π log R) cos Θ) / (2π),   with 0 < |ε| ≤ 1.

- Load-bearing step: for every integer n,

      E[R^n sin(2π log R)] = Im exp((n + 2πi)²/2) = e^{(n² − 4π²)/2} sin(2πn) = 0.

- Hence every mixed moment of Z equals the corresponding moment of the rotation-invariant law
  in which Θ is uniform and independent of R. So so(2) ⊆ g_∞.
- But P is not rotation-invariant, because the conditional angle density depends on cos Θ.
- Lesson: moment methods certify symmetries of the *moment sequence*. For heavy-tailed
  (moment-indeterminate) laws this can be strictly larger than sym(P). B2.5 removes this
  caveat.

### B1.4 Compactness theorem and the whitening caveat [THEOREM]

**Theorem.** If M₂ = E[zzᵀ] is finite and positive definite, then:
- G_P is a compact subgroup of GL(d), and sym(P) = Lie(G_P) is a compact Lie algebra;
- for every K ≥ 2,

      g_K ⊆ so(M₂) := {A : AM₂ + M₂Aᵀ = 0} = M₂^{1/2} so(d) M₂^{−1/2}.

**Proof.**
1. G_P is closed. If g_n → g with (g_n)_#P = P, then by dominated convergence
   E f(g_n z) → E f(gz) for every bounded continuous f. So g_#P = P.
2. Every g ∈ G_P satisfies g M₂ gᵀ = M₂. So G_P ⊆ O(M₂) = M₂^{1/2} O(d) M₂^{−1/2}, and O(M₂)
   is compact. A closed subset of a compact set is compact.
3. The k = 2 condition from B1.1 is exactly AM₂ + M₂Aᵀ = 0, which gives g_K ⊆ so(M₂).
4. A subalgebra of a compact Lie algebra is itself compact, hence reductive with a negative
   semidefinite Killing form. ∎

**Consequence 1: the whitening caveat, made exact.**
- After whitening, M₁ = 0 and M₂ = I, so g₂ = so(d) exactly.
- With *sample* whitening, M̂₁ = 0 and M̂₂ = I hold exactly, so ĝ₂ = so(d) exactly. The
  check_b1 script shows three exact zero singular values at K = 2 in R³.
- So K ≤ 2 carries a single fact: the symmetry is orthogonal in the whitened frame. All the
  information that selects a subalgebra lives at K ≥ 3.
- For a Gaussian, κ_k = 0 for every k ≥ 3, so g_∞ = so(M₂). Gaussians have maximal symmetry.
  This is also what drives the degeneracy in B5.

**Relation to `examples/matched_moments.bladenb`.**
- That notebook whitens *each point cloud* separately. So its K ≤ 2 features are constant
  across samples (its Theorem 2). This is the per-sample shadow of the same fact.
- Reading the notebook's classes through B1, assuming the harmonic components named below are
  nonzero in each cloud:
  - Every cloud has ĝ₂ = so(3).
  - The tetra class drops to ĝ₃ = 0, because an l = 3 harmonic proportional to xyz has the
    finite stabilizer T.
  - The octa class keeps ĝ₃ = so(3), because its odd moments vanish. It drops at K = 4,
    because the cubic l = 4 harmonic has the finite stabilizer O.
  - The iso and heavy classes keep so(3).

**Consequence 2: non-compact symmetries are never distributional.**
- sl(2,R), so(1,3), se(n) and the Heisenberg algebra cannot be subalgebras of sym(P) for any P
  with finite, nondegenerate second moments.
- So the moment chain will never return LieGAN's top-tagging Lorentz algebra. Whatever
  LieGAN detected there, it is not exact invariance of the joint law. LieGAN itself notes that
  the distributional condition "often fails in practice".
- The plan should route non-compact candidates to B3: functional or equivariance symmetry of
  a classifier, regressor or dynamics, where such symmetries are meaningful.

**Consequence 3: the search space shrinks at no cost.** In whitened coordinates, the unknown A
can be restricted to so(d). That is d(d−1)/2 unknowns instead of d².

**Consequence 4: degenerate support produces junk symmetries.**
- If supp P lies in a proper subspace S (so M₂ is singular), every A with A|_S = 0 belongs to
  sym(P). Such an A fixes every data point.
- This is exactly LaLiGAN's "fallacious symmetry" caused by latent collapse.
- Remove it by restricting to S, i.e. by dropping the null directions of M₂ before computing.

### B1.5 Finite-sample estimator and perturbation bound

**Algorithm B1 (moment nullspace).** Inputs: samples z₁…z_N ∈ R^d, a maximal order K ≥ 3, and
weights w_k > 0.

1. **Center and whiten:** z ← Ŵ(z − μ̂), with Ŵ = Σ̂^{−1/2} computed by `eigh`. If needed,
   first drop directions whose eigenvalue is ≤ tol. The answer maps back by conjugation
   (B1.2).
2. **Form the tensors.** For k = 3…K, form M̂_k (or κ̂_k) in symmetric storage. Use so(d) as
   the unknown space, as B1.4 allows. If you use gl(d) instead, also include k = 1 and k = 2.
3. **Assemble the constraint matrix** C ∈ R^{(Σ_k dim Sym^k) × p}, where p is the dimension of
   the unknown space. Column j is the stacked vector ⊕_k √w_k ρ_k(B_j)M̂_k, for the basis
   element B_j (for so(d), B_j = E_il − E_li).
   - **With symmetric storage, scale each row by √(k!/∏ n_i!)**, where n₁…n_d are the
     multiplicities of the row's canonical tuple. This makes row norms equal Frobenius norms.
   - Without the scaling, the geometry is wrong, and so is the bound below. The population
     nullspace is unaffected.
4. **Extract the nullspace.** Run `svd` on C. Prefer this to `eigh` on CᵀC, which squares the
   condition number. Set r̂ := #{σ_i ≤ τ}. Take ĝ to be the span of the last r̂ right singular
   vectors, mapped back by Ŵ⁻¹(·)Ŵ.

**Perturbation theorem [THEOREM].**

Setup: C is the population matrix and E = Ĉ − C. Suppose dim ker C = r, and let
g := σ_{p−r}(C) > 0 be its smallest nonzero singular value (the **gap**). Whitening is treated
as fixed; B1.2 handles the conjugation.

- (i) **Operator norm of the error.**

      ‖E‖₂ ≤ (Σ_k w_k k² ‖M̂_k − M_k‖_F²)^{1/2},

  because ‖ρ_k(A)Δ‖_F ≤ k‖A‖₂‖Δ‖_F ≤ k‖A‖_F‖Δ‖_F.
- (ii) **Moment error.** For iid samples,

      E‖M̂_k − M_k‖_F² = (E‖z‖^{2k} − ‖M_k‖_F²)/N ≤ E‖z‖^{2k}/N,

  since ‖z^{⊗k}‖_F = ‖z‖^k. By Markov's inequality, with probability ≥ 1 − δ,

      ‖E‖₂ ≤ (Σ_k w_k k² E‖z‖^{2k} / (Nδ))^{1/2}.

  This needs **finite 2K-th moments**; heavy tails ruin it.
- (iii) **Rank recovery.** If ‖E‖₂ < τ < g − ‖E‖₂, then r̂ = r (Weyl's inequality).
- (iv) **Subspace angle.** Elementary sin-θ bound:

      ‖sin Θ(ĝ, g_K)‖₂ ≤ 2‖E‖₂ / g.

  Proof. Take a unit vector v in the bottom-r right singular space of Ĉ.
  - First, ‖Cv‖ ≤ ‖Ĉv‖ + ‖E‖ ≤ σ̂_{p−r+1} + ‖E‖ ≤ 2‖E‖, using Weyl and σ_{p−r+1}(C) = 0.
  - Second, write v = v₀ + v_⊥ with v₀ ∈ ker C. Then ‖Cv‖ = ‖Cv_⊥‖ ≥ g‖v_⊥‖. ∎

  The classical Wedin (1972) form is ‖E‖₂/(g − ‖E‖₂), valid when ‖E‖₂ < g (Stewart & Sun
  1990, Ch. V).
- (v) **Closure defect of the estimate.** For unit Â, B̂ ∈ ĝ,
  dist([Â, B̂], ĝ) ≤ c·sin θ with c ≤ 8 + O(sin θ). This uses ‖[X,Y]‖_F ≤ 2‖X‖_F‖Y‖_F.
  **The estimated span is closed only up to O(θ).**

**Heuristic flags.**
- The threshold τ and the order K are not determined by the data, because the gap g is a
  population quantity. **[HEURISTIC]** Calibrate τ by bootstrap or permutation, or use a
  spectral-gap ratio.
- Near-symmetries whose gap g is below the noise level cannot be told apart statistically
  from exact symmetries.
- The weights w_k do not change the population nullspace. They do change g, and hence the
  finite-sample estimate.

**Numerical demonstration (check_b1).** The law in R³ is invariant under so(2) but not under
so(3). After sample whitening:
- K = 2 has exactly three zero singular values, i.e. all of so(3).
- K = 3 isolates one direction. Its smallest singular value is 0.105 at N = 10⁴, 0.040 at
  N = 10⁵ and 0.0099 at N = 10⁶, so it decays like N^{−1/2}. The gap is about 3.2.
- At N = 10⁶ the recovered generator is within sin θ ≈ 0.007 of the true one.

---

## B2. Polynomial-kernel MMD is weighted moment matching

### B2.1 The identity [THEOREM]

Assume c ≥ 0 and that P and Q have finite K-th moments. For the kernel k(x,y) = (c + xᵀy)^K,

    MMD²(P,Q) = Σ_{j=1}^{K} w_j ‖M_j(P) − M_j(Q)‖_F²,   with w_j = binom(K,j) c^{K−j}.

**Proof.**
1. By the binomial theorem, (c + ⟨x,y⟩)^K = Σ_j binom(K,j) c^{K−j} ⟨x,y⟩^j.
2. By the trace identity, ⟨x,y⟩^j = ⟨x^{⊗j}, y^{⊗j}⟩_F.
3. So Φ(x) = (√w_j x^{⊗j})_{j=0..K} is an explicit feature map, and the mean embedding is
   μ_P = (√w_j M_j(P))_j.
4. MMD² = ‖μ_P − μ_Q‖². The j = 0 term cancels because M₀ = 1. ∎

**Notes on the weights.**
- c > 0 is needed for every order j ≤ K to get positive weight.
- With c = 0, only j = K survives, and the constraints for j < K are lost. For example,
  K = 2 with c = 0 ignores the mean.
- The relative weights depend on the data scale through c^{K−j}, so whiten first. After
  whitening, with c = 1, the weights are w_j = binom(K,j).

### B2.2 Computing from three Gram matrices; exactness at the V-statistic level [THEOREM]

**The computation.** Form G_XX = `gram`(X,X), G_YY and G_XY. Map each entry by
s ↦ (c + s)^K, then average:

    MMD²_V = mean K_XX + mean K_YY − 2 mean K_XY.

**Exactness.** This V-statistic equals Σ_j w_j‖M̂_j(X) − M̂_j(Y)‖_F² **exactly**: it is the
identity of B2.1 applied to the two empirical measures. check_b2 confirms it to relative error
3·10⁻¹⁶.

**Cost.**
- The Gram route costs O(n²d).
- The tensor route with symmetric storage costs O(n · binom(d+K−1, K)).
- The crossover is at n ≈ binom(d+K−1,K)/d.
  - For d = 8 and K = 4, tensors are much cheaper at n = 10⁴: about 1.3·10⁷ flops versus
    8·10⁸.
  - For d = 64 and K = 4, the Gram route wins.
- So "without ever forming the tensors" is a cost choice, not a mathematical necessity. Blade
  supports both routes.

### B2.3 The infinitesimal expansion, with the claim corrected [THEOREM, CORRECTION]

Let Q_t = (e^{tA})_#P and F(t) = MMD²(P, Q_t). By B2.1 and B1.1,

    F(t) = Σ_j w_j ‖(e^{tρ_j(A)} − I) M_j‖_F²
         = t² q(A) + t³ Σ_j w_j⟨ρ_jM_j, ρ_j²M_j⟩ + O(t⁴),

where

    q(A) := Σ_{j=1}^{K} w_j ‖ρ_j(A) M_j‖_F².

**What this shows.**
- **F(0) = F′(0) = 0 for every A.** Since F ≥ 0, t = 0 is always a global minimum. So "zero to
  first order" holds for every A and carries no information. Equivalently, the gradient signal
  in the group parameter vanishes at the identity.
- **F″(0) = 2q(A).** As a function of A, the Hessian in t is twice the weighted B1 quadratic
  form H_K := Σ_j w_j C_jᵀC_j. When c > 0, its kernel is exactly g_K.
- If A ∈ g_K, then F ≡ 0 for all t, not just up to second order.
- The empirical V-statistic of (X, e^{tA}X) behaves the same way. Its t² coefficient is exactly
  the empirical B1 form q̂(A) = Σ_j w_j‖ρ_j(A)M̂_j‖².
- check_b2: the finite-difference value F″(0) = 13445.74, against 2q = 13445.68.

**This is the bridge.** The sample-level MMD at the identity, to second order, *is* the
tensor-level nullspace problem.

### B2.4 The Hessian from Gram matrices only, for any dot-product kernel [THEOREM]

**Scalar form.** Let k(x,y) = κ(xᵀy) with κ ∈ C². Differentiating the V-statistic twice gives

    ½F″(0) = (1/n²) Σ_{a,b} [ κ′(G_ab)⟨Ax_a, Ax_b⟩ + κ″(G_ab)⟨Ax_a, x_b⟩⟨x_a, Ax_b⟩ ].

- The cross terms in A² cancel by the symmetry a ↔ b.
- Only three Gram matrices are needed: G = `gram`(X,X), `gram`(XAᵀ, XAᵀ) and `gram`(XAᵀ, X).
- For κ(s) = (c+s)^K, we have κ′ = K(c+s)^{K−1} = Σ_j w_j j s^{j−1} and
  κ″ = K(K−1)(c+s)^{K−2}. This reproduces q(A); the check agrees to 13 digits.

**Matrix form.** As a d²×d² matrix acting on the row-major vector a = vec A:

    H = sym( I_d ⊗ (Xᵀ K′ X) + R(Zᵀ K″ Z) ) / n²,

where:
- K′ = κ′(G) and K″ = κ″(G) are applied entrywise;
- Z ∈ R^{n×d²} has rows vec(x_a x_aᵀ);
- R is the index reshuffle (k,i′),(i,k′) → (i,k),(i′,k′).

The cost is O(n²d² + nd⁴), verified in check_b2_assembly. Two alternatives work as well:
- Polarization: since H is exactly quadratic, H_{pq} = [q(B_p + B_q) − q(B_p) − q(B_q)]/2
  holds exactly.
- Forward-over-reverse AD.

### B2.5 Characteristic kernel: an exact infinitesimal test of sym(P) [THEOREM]

**Gaussian MMD from the same Gram matrices.**
- Use the row norms r_X = diag(G_XX). Then

      K_XY = exp(−(r_X 1ᵀ + 1 r_Yᵀ − 2 G_XY) / (2σ²)).

- For a characteristic kernel, MMD(P,Q) = 0 if and only if P = Q.
  - Gretton et al. 2012 show MMD is the integral probability metric over the RKHS unit ball,
    and the supremum has a closed form.
  - Sriperumbudur et al. 2010 [verified]: a bounded continuous translation-invariant kernel
    is characteristic iff its spectral measure has support all of R^d. The Gaussian kernel
    qualifies.
- So MMD is the adversarial objective with the inner maximization over a function class
  solved in closed form. That is the precise sense in which it is "non-adversarial".

**Theorem.** Let k be the Gaussian kernel, assume E|z| < ∞, and let A ∈ gl(d). Then

    MMD²(P, e^{tA}_#P) = t² q_G(A) + o(t²),

where

    q_G(A) = ‖μ′_A‖²_H = E_{z,z′}[(Az)ᵀ ∇₁∇₂ k(z,z′) (Az′)] ≥ 0,

z′ is an independent copy of z, and μ′_A = E[∇₁k(z,·)·Az]. Moreover **ker q_G = sym(P)
exactly**. No moment determinacy, no compactness and no choice of K are needed.

**Proof.**
1. **Differentiability.** For the Gaussian kernel, ‖∇₁k(x,·)·v‖_H = |v|/σ uniformly in x.
   Together with E|z| < ∞, dominated convergence for Bochner integrals makes t ↦ μ_{Q_t}
   differentiable at 0 with derivative μ′_A. So MMD² = t²‖μ′_A‖² + o(t²).
2. **Reduction to a divergence.**
   - μ′_A = 0 iff h(y) := ⟨μ′_A, k(·,y)⟩ = 0 for all y, because the functions k(·,y) span a
     dense subspace of H.
   - Let ν := Az·P(dz). This is a finite vector measure because E|z| < ∞.
   - Then h = Σ_i (∂_iG) * ν_i (up to a reflection), where G is the Gaussian.
   - Taking Fourier transforms, |ĥ(ω)| = Ĝ(ω)·|ω·ν̂(ω)|. Since Ĝ > 0 everywhere, h ≡ 0 iff
     ω·ν̂ ≡ 0 iff div ν = 0 as a tempered distribution.
3. **From zero divergence to invariance (the load-bearing step).** For φ ∈ C_c^∞,

       d/dt ∫ φ(e^{tA}z) dP = ∫ ∇φ(e^{tA}z)·A e^{tA}z dP = ∫ ∇(φ∘e^{tA})(z)·Az dP.

   The last step uses e^{tA}A = A e^{tA}. The right side is 0, because φ∘e^{tA} ∈ C_c^∞ and
   div ν = 0. So ∫ φ∘e^{tA} dP does not depend on t, which gives (e^{tA})_#P = P.
4. The converse is immediate. ∎

**Remarks.**
- The same proof works for any C² translation-invariant kernel whose spectral measure has
  full support and a finite second moment.
- **The plan should treat this criterion as primary at the population level.** It avoids
  moment indeterminacy (B1.3) and the choice of K entirely.
- The moment chain stays valuable anyway: it is cheap, interpretable, certifier-shaped (finite
  K), and it separates the orders.

**Gram form of q_G.** Let u_ab = x_a − x_b. Then

    q̂_G(A) = (1/n²) Σ_{a,b} K_ab [ ⟨Ax_a, Ax_b⟩/σ² − ⟨u_ab, Ax_a⟩⟨u_ab, Ax_b⟩/σ⁴ ].

- With P = `gram`(XAᵀ, X), the inner products are ⟨u_ab, Ax_a⟩ = P_aa − P_ab and
  ⟨u_ab, Ax_b⟩ = P_ba − P_bb.
- As a d²×d² matrix it is I⊗(XᵀKX)/σ² minus a reshuffled sum of four Z-type contractions. The
  cost is O(n²d² + nd⁴).
- Verified against finite differences and against a Kronecker assembly (check_b2,
  check_b2_assembly).

**Finite-sample caveat [THEOREM].**
- For distinct sample points, the empirical form q̂_G is positive definite: the empirical
  measure of finitely many atoms is invariant under no nontrivial flow. **There is never an
  exact empirical nullspace.** You always threshold the smallest eigenvalues.
- By the Davis-Kahan sin-θ theorem, ‖sin Θ‖ ≤ ‖Ĥ − H‖/(λ_{r+1}(H) − ‖Ĥ − H‖), and
  ‖Ĥ − H‖ = O_P(n^{−1/2}) when E|z|² < ∞.
- The bandwidth σ changes the gap but never the population kernel. Choosing σ is
  **[HEURISTIC]**; the median heuristic is the usual default.
- Kernel tests lose power polynomially in the dimension against "fair" alternatives (Ramdas et
  al., AAAI 2015 [verified]). So keep this test in the low-dimensional latent space.

### B2.6 U- versus V-statistics, and the pairing trap [THEOREM, CORRECTION]

**The U-statistic is unbiased even for paired samples.** For Y = gX, paired with X,

    MMD²_U = (1/(n(n−1))) Σ_{a≠b} [k(x_a,x_b) + k(y_a,y_b) − k(x_a,y_b) − k(y_a,x_b)]

is **unbiased for MMD²(P, g_#P)**. All dependence between x_a and y_a = gx_a sits on the
diagonal, which is excluded.

**The V-statistic adds a biased diagonal term.**
- The extra term is (1/n²)Σ_a [k(x_a,x_a) + k(gx_a,gx_a) − 2k(x_a, gx_a)].
- For the Gaussian kernel this equals (2/n²)Σ_a(1 − k(x_a, gx_a)). It is ≥ 0, and it vanishes
  only when g fixes every sample.
- So, as a loss over A, **the V-statistic strictly prefers the trivial solution A = 0 to an
  exact symmetry**. Its expected value is 0 at A = 0 but O(1/n) > 0 at a true symmetry.

**Recommendation.**
- Use the U-statistic, or compare two disjoint halves (X₁ against gX₂).
- The U-statistic gradient is unbiased for the population gradient, which matters for
  minibatch SGD.
- The U-statistic can be negative; that is harmless.

**For the Hessians.**
- The V-Hessian equals (n−1)/n times the U-Hessian, plus a diagonal form.
- For the Gaussian kernel, the diagonal form is (1/(nσ²)) tr(A M̂₂ Aᵀ). After exact sample
  whitening (M̂₂ = I) it becomes (1/(nσ²))‖A‖_F². This is an isotropic shift, so it does not
  move the eigenvectors. Checked: the diagonal term equals tr(G_AA)/(n²σ²).
- For polynomial kernels the diagonal form is not isotropic, so drop the a = b terms. The
  U-version of the B1 form is

      H_U = (n/(n−1)) (ĈᵀĈ − (1/n²) Σ_a C_aᵀC_a),

  where C_a is the single-sample constraint matrix.

---

## B3. Discovering functional (equivariance) symmetry by nullspace

### B3.1 Theorem: the pairs form a Lie subalgebra [THEOREM]

Let U ⊆ R^d be open and connected, and f ∈ C²(U, R^m). Then

    s(f) := {(A,B) ∈ gl(d) × gl(m) : Df(x)Ax = Bf(x) for all x ∈ U}

is a Lie subalgebra of gl(d) ⊕ gl(m), with the componentwise bracket.

**Proof.** One identity does the work, and it also drives B3.4.
1. Let φ_i(x) := Df(x)A_ix − B_if(x).
2. Differentiate φ₁ along A₂x and φ₂ along A₁x. Use the symmetry of D²f, and substitute
   B_iDf(x)A_jx = B_i(φ_j + B_jf). This gives

       φ_{[A₁,A₂],[B₁,B₂]}(x) = Dφ₁(x)[A₂x] − Dφ₂(x)[A₁x] + B₁φ₂(x) − B₂φ₁(x).

3. If φ₁ ≡ φ₂ ≡ 0 on U, the right side vanishes. ∎

**Equivalent views.**
- *Vector fields.* (A,B) ∈ s(f) means the linear field x ↦ Ax is f-related to y ↦ By.
  f-relatedness is preserved by brackets (Lee 2013, Ch. 8). For linear fields
  [ξ_A, ξ_B] = ξ_{−[A,B]} on both sides, so the signs match.
- *Group version.* If U = R^d and f ∈ C¹, then (A,B) ∈ s(f) iff f(e^{tA}x) = e^{tB}f(x) for
  all t. To see this, note that h(t) = f(e^{tA}x) solves h′ = Bh. So s(f) is the Lie algebra
  of the closed group {(g,h) : f∘g = h∘f}.
- Otto et al. state the C¹ version (JMLR 2025; arXiv v3 Thm 4 [verified]): the symmetry group
  is a closed embedded Lie subgroup, and its Lie algebra is the nullspace of the
  Lie-derivative operator.

### B3.2 The linear system

**Blocks.** Each sample x_n contributes one block of size m × (d² + m²):

    C_n = [ Df(x_n) ⊗ x_nᵀ ,  −I_m ⊗ f(x_n)ᵀ ]   (row-major vec of A and of B).

Stack the blocks into C ∈ R^{Nm × (d²+m²)}. Then s_N := ker C ⊇ s(f).

**Invariance case (B = 0).**
- The rows are vec(∇f_p(x_n) x_nᵀ), and C ∈ R^{Nm × d²}.
- This is exactly LieGG's "network polarization matrix" [verified]:
  - its rows are the flattened outer product of the input gradient with the data point;
  - the generators are read off the right singular vectors with near-zero singular values;
  - LieGG treats invariance only.

**Junk dimensions in B.**
- The projection s(f) → gl(d) has kernel {(0,B) : B f(x) = 0 for all x}. That is the set of B
  vanishing on span f(U).
- If f(U) does not span R^m, restrict B to that span first. Otherwise you get junk
  dimensions.

**Homogeneous f.** If f is homogeneous of degree p, then (I, pI) ∈ s(f) by Euler's identity.
This element is real but usually uninteresting: quotient it out, or expect to see it.

### B3.3 Identifiability

Let c := (d² + m²) − dim s(f), the codimension of s(f).

**Necessary condition: Nm ≥ c. [CORRECTION]**
- The brief writes "d² + m² − dim(stabilizer)". The right quantity is dim s(f) itself.
- The count is necessary only, not sufficient.

**Generic sufficiency for real-analytic f on a connected U [THEOREM].**
- There is an N_* with ⌈c/m⌉ ≤ N_* ≤ c such that, for every N ≥ N_*, ker C = s(f) for
  Lebesgue-almost every (x₁…x_N) ∈ U^N.
- In particular this holds almost surely for iid draws from any law with a density on U.
- Proof.
  1. Let R(x) be the row space of C_x. Then Σ_{x∈U} R(x) = s(f)^⊥, which has dimension c.
  2. Choose points greedily, each raising that dimension. This takes p ≤ c points, after which
     some c×c minor of the stacked matrix is nonzero.
  3. That minor is a real-analytic function on U^N that is not identically zero. So its zero
     set is null and nowhere dense. ∎
- Otto et al., arXiv v3 Prop. 11 [verified], gives an almost-sure, eventually-exact version.
  It explicitly does not bound the number of samples needed.

**Deterministic sufficiency for polynomial f [THEOREM].**
- If deg f ≤ D, every φ_{A,B}(x) = Df(x)Ax − Bf(x) is a vector polynomial of degree ≤ D.
- Call a sample set unisolvent for degree ≤ D if only the zero polynomial of degree ≤ D in d
  variables vanishes on it. On a unisolvent sample set, ker C = s(f) exactly.
- binom(d+D, D) points in general position suffice (the Vandermonde matrix is nonsingular),
  e.g. a principal lattice.
- This is Zariski density made concrete. It is also why the certifier's coefficient matching
  on polynomial bodies is exact.

**What "generic" buys, and what it does not.**
- It buys exactness away from a measure-zero set of sample configurations.
- It does not buy conditioning: near-degenerate configurations give small but nonzero singular
  values for directions that are not symmetries.
- So the threshold choice remains **[HEURISTIC]**. LieGG does not fix a threshold either.

**ReLU networks.**
- These are not C². On each open linear region, f(x) = W_R x + b_R. The pairs satisfying
  W_R A = B W_R and B b_R = 0 form a subalgebra.
- The intersection over all regions is again a subalgebra, so the almost-everywhere
  definition still yields a Lie algebra.
- Equivalence with finite (group-level) equivariance needs care at region boundaries.

### B3.4 What fails on structured data [THEOREM, counterexample]

If the samples lie on a set V (a data manifold or a subvariety), what you can compute is

    s_V := {(A,B) : φ_{A,B} = 0 on V} ⊇ s(f).

**Tangency lemma.**
- By the identity in B3.1, on V we have φ_{[1,2]} = Dφ₁[A₂x] − Dφ₂[A₁x].
- This vanishes when A₁x and A₂x are tangent to V, because φ_i ≡ 0 on V kills tangential
  derivatives.
- So **s_V ∩ {A : Ax ∈ T_xV on V} is a subalgebra, but s_V itself need not be.**

**Counterexample.**
- Take d = 2, m = 1, f(x) = x₁ + x₂², and V = {x₂ = 0}.
- On V the constraint reduces to A₁₁ = b. So s_V = {(A,b) : A₁₁ = b} has dimension 4.
- (E₂₁, 0) and (E₁₂, 0) both lie in s_V. Their bracket is (E₂₂ − E₁₁, 0), which has
  A₁₁ = −1 ≠ 0 = b. So s_V is not closed.
- The true s(f) is 1-dimensional: (diag(b, b/2), b), the weighted scaling.
- Structured data thus causes both non-identifiability and loss of closure.

**Remedies.**
- Make sure the data have full-dimensional support in the space where the constraint is
  computed. In a latent space, keep the latent dimension ≤ the intrinsic dimension.
- Or restrict to fields tangent to V. Otto et al. do this with tangent projections (arXiv v3
  Thm 10 and §7.1 [verified]).

### B3.5 Cross-reference: the latent uses

1. **A latent task head h(z)** (classifier logits or a regressor). Use the invariance system
   (B = 0) or the equivariance system on the latent codes. **This is where non-compact
   symmetries belong (B1.4).**
2. **Latent dynamics F̃ = φ∘F∘ψ**, LaLiGAN's dynamical-systems setting.
   - The constraint DF̃(z)Az − AF̃(z) = 0 is linear in A.
   - The solution set s(F̃) ∩ {(A,A)} is a subalgebra.
3. **Encoder equivariance under a known data-space linear action A_data.** The constraint
   Dφ(x)(A_data x) = A_lat φ(x) is linear in the unknown A_lat.

Together with the distributional residual of B1/B2 on the latent, these are the two families
of losses. Only the functional family can carry task information, and B5 shows that task
information is where identifiability will have to come from.

---

## B4. Decomposing a discovered algebra with exact algorithms

**A caveat that applies to all of B4.** Every rank decision below (dim [g,g], the radical, the
center, multiplicities) is a numerical-rank decision.
- For an exactly closed algebra these decisions are exact.
- For an estimated algebra they inherit the O(θ) closure defect of B1.5, so the tolerances are
  **[HEURISTIC]**.

The invariants table in (vii) was computed by check_b4b.

### (i) Orthonormal basis and structure constants

1. Stack the vec(L_i) as columns and run `svd`.
2. Keep the left singular vectors with σ > tol and reshape them to E₁…E_r. These are
   Frobenius-orthonormal.
3. Compute c_ij^k = ⟨E_k, [E_i, E_j]⟩ as reductions of elementwise products.
4. The closure residual ρ_ij = [E_i,E_j] − Σ_k c_ij^k E_k is the diagnostic from item 1 of the
   verdict.

### (ii) Killing form

κ_ij = tr(ad_{E_i} ad_{E_j}) = Σ_{k,l} c_ik^l c_jl^k, since (ad_{E_i})_{lk} = c_ik^l. Its
signature does not depend on the basis (Sylvester).

### (iii) Cartan's criteria, the radical, and Levi [THEOREM]

Everything below is over R. It reduces to C by complexification, because solvability,
semisimplicity and the radical all commute with ⊗C.

**The criteria.**
- *Semisimple ⟺ κ nondegenerate* (Humphreys 1972, §5.1).
- *Solvable ⟺ κ(g, [g,g]) = 0* (Cartan's criterion, Humphreys §4.3).
- *rad(g) = [g,g]^{⊥κ}*: the radical is the Killing-orthocomplement of the derived algebra.
  Sources: Encyclopedia of Mathematics, "Killing form" [verified]; also Bourbaki, Lie Groups
  and Lie Algebras, Ch. I (proposition number not verified).

**Proof sketch for the radical.**
- (⊆)
  1. [g, rad] lies in the nilradical, which acts nilpotently on g.
  2. For an ideal I acting nilpotently, the kernels V_j = ker ad(I)^j are ad(g)-stable. So
     ad x ad n lowers this flag, hence is nilpotent, hence κ(x,n) = 0.
  3. Then κ([x,y], r) = κ(x, [y,r]) = 0.
- (⊇)
  1. J := [g,g]^⊥ is an ideal, and κ(J, [J,J]) ⊆ κ(J, [g,g]) = 0.
  2. Cartan's criterion applied to ad_g(J) ⊂ gl(g) makes ad_g(J) solvable.
  3. The kernel of ad on J is center ∩ J, which is abelian, so J itself is solvable.

**Algorithm.** The radical is the nullspace of x ↦ (κ(x, [E_i,E_j]))_{i<j}. Verified:
rad(se(3)) has dimension 3, rad(gl(2)) dimension 1, and rad(sl(2)⋉R²) dimension 2.

**Levi decomposition.**
- g = s ⋉ rad(g), with s semisimple (Levi).
- The complement s is **not unique**. It is unique up to conjugation by exp(ad n) for
  n ∈ [g, rad] (Malcev-Harish-Chandra; Jacobson 1962, Ch. III — chapter from memory).

*Algorithm for an abelian radical.*
1. Take any vector-space complement W of r, with basis w_i (for example the
   Frobenius-orthogonal complement).
2. Write [w_i,w_j] = Σ c_ij^k w_k + ρ_ij, with ρ_ij ∈ r.
3. Look for s_i = w_i + φ_i with φ_i ∈ r. Because r is abelian, the condition is *linear*:

       [w_i, φ_j] − [w_j, φ_i] − Σ_k c_ij^k φ_k = −ρ_ij.

4. Whitehead's second lemma guarantees a solution. Solve by least squares: normal equations
   via `gram`, then `lu_solve`.
5. Solutions differ by coboundaries; this is exactly the Malcev freedom.

*Non-abelian radical.* Iterate down the derived series of r. Each quotient step is abelian,
hence linear. de Graaf 2000 gives algorithms of this kind.

*Example.* The Levi factor of se(3) is "rotations about a point". Which point is exactly the
non-unique choice.

*For distributional symmetries none of this is needed.* By B1.4 the algebra is compact, hence
reductive. Then rad = z(g), and the Levi factor [g,g] is canonical.

### (iv) Compact semisimple ⟺ κ negative definite [THEOREM]

Source: Knapp 2002, Ch. IV (numbering not verified). More generally, the following are
equivalent:
- g is compact;
- g = z ⊕ [g,g] with κ negative definite on [g,g];
- g admits an ad-invariant inner product.

### (v) Center

z(g) = ker ad = {x : Σ_a x_a c_{a i}^k = 0 for all i, k}. This is the nullspace of an
(r²) × r matrix.

### (vi) Isotypic decomposition via the commutant

**1. The commutant.** End_g(V) = {M : [M, L_i] = 0 for all i} is the kernel of the stacked map
(I ⊗ L_iᵀ − L_i ⊗ I) acting on row-major vec M. That is a (r d²) × d² matrix.

**2. Its structure [THEOREM].**
- Complete reducibility of V is needed. It always holds for compact g, via an invariant inner
  product, and for semisimple g by Weyl's theorem (Humphreys §6.3).
- Under complete reducibility, Schur and Wedderburn give End_g(V) ≅ ⊕_i M_{n_i}(D_i), where
  D_i = End_g(U_i) is a real division algebra.
- By Frobenius' theorem, D_i ∈ {R, C, H}: the representation types real, complex and
  quaternionic. Sources: Bröcker & tom Dieck 1985, §II.6; Lang, Algebra, Ch. XVII
  (numbering from memory).

**3. [CORRECTION] Use a generic *symmetric* element, not a generic element.**
- A generic element of M_n(R), n ≥ 2, has complex eigenvalues with positive probability, even
  in the purely real-type part. So a generic element does not split V cleanly.
- The recipe:
  1. First move to a frame where every L_i is antisymmetric (step 1 of (viii)). The commutant
     is then closed under transpose, because [Mᵀ, L_i] = [M, L_i]ᵀ when L_i is
     antisymmetric.
  2. Draw Gaussian coefficients for a commutant element M, and set M_s = (M + Mᵀ)/2.
  3. Run `eigh` on M_s.
- What the eigenspaces mean:
  - The eigenspaces of a generic M_s are **single irreducible copies**. For complex- and
    quaternionic-type components, each real eigenvalue has multiplicity dim_R U_i.
  - The **isotypic components** are the eigenspaces of a generic symmetric element of the
    *center* of the commutant. That center is one more nullspace computation.
- **Type detection.** On each irreducible block, dim End_g(U) ∈ {1, 2, 4}.
  - Verified: so(2) on R² gives 2; su(2) on H = R⁴ gives 4; so(3) on D_l gives 1.
  - The antisymmetric part of a complex or quaternionic commutant consists of complex
    structures J with J² = −I. These are the "rotation-like blocks without real
    eigenvectors"; `eig` would report them as conjugate pairs.

**4. What "generic" means here.** The coefficients must avoid a proper real-algebraic subset
(a discriminant locus). Gaussian coefficients therefore succeed almost surely. Numerically,
require eigenvalue gaps much larger than the noise, and redraw otherwise; the tolerance is
**[HEURISTIC]**.

**5. Failure modes.**
- If V is not completely reducible (se(n) on homogeneous coordinates, or the Heisenberg
  algebra), the commutant is not semisimple. The construction then yields a socle filtration,
  not a direct sum.
- An estimated algebra that is only approximately closed gives a noisy commutant.

### (vii) Identifying small algebras

Use the tuple (dim, dim [g,g], dim z, dim rad, Killing signature (n₊, n₋, n₀), rank). The rank
is the multiplicity of the eigenvalue 0 of ad_X for a random X, computed with `eig`. Every row
below was computed numerically.

| algebra | dim | [g,g] | z | rad | κ (+,−,0) | rank | note |
|---|---|---|---|---|---|---|---|
| so(2) / R | 1 | 0 | 1 | 1 | (0,0,1) | 1 | intrinsically indistinguishable; the *representation* decides (imaginary spectrum = rotation, real = scaling) |
| so(3) ≅ su(2) | 3 | 3 | 0 | 0 | (0,3,0) | 1 | compact simple; κ = −2·I in the basis with [L_x,L_y] = L_z |
| sl(2,R) ≅ so(2,1) | 3 | 3 | 0 | 0 | (2,1,0) | 1 | non-compact simple |
| se(2) | 3 | 2 | 0 | 3 (solvable) | (0,1,2) | 1 | nilradical R² |
| se(1,1) | 3 | 2 | 0 | 3 | (1,0,2) | 1 | differs from se(2) by the sign of κ |
| heis₃ | 3 | 1 | 1 | 3 (nilpotent) | (0,0,3) | 3 | [g,g] = z |
| u(2) = su(2)⊕u(1) | 4 | 3 | 1 | 1 | (0,3,1) | 2 | compact reductive |
| gl(2) | 4 | 3 | 1 | 1 | (2,1,1) | 2 | |
| so(4) ≅ so(3)⊕so(3) | 6 | 6 | 0 | 0 | (0,6,0) | 2 | the only compact semisimple algebra of dim 6 (compact simple dims are 3, 8, 10, 14, 15, …) |
| so(3,1) | 6 | 6 | 0 | 0 | (3,3,0) | 2 | Lorentz |
| se(3) | 6 | 6 | 0 | 3 | (0,3,3) | 2 | rad = translations R³; Levi factor = rotations about a non-unique point |

**Splitting a semisimple algebra into simple ideals.** Run (vi) on the *adjoint*
representation. The eigenspaces of a generic symmetric element of End_g(g) are the simple
ideals; for example so(4) splits as so(3) ⊕ so(3).

**Completeness [HEURISTIC]/[OPEN].**
- The tuple is a complete invariant for the compact and semisimple cases listed.
- It is *not* complete for solvable algebras. The Bianchi families VI_h and VII_h carry a
  continuous parameter, read off from the eigenvalues of ad on the nilradical.
- In the compact case, which covers all distributional symmetries, the classification is
  complete: g = z ⊕ ⊕(compact simple), and each simple factor is identified by (dim, rank).
  The first ambiguity is at dim 21 (B₃ vs C₃), which is irrelevant for small latents.

**Snapping so(2) charges.**
- With finite samples, the frequencies of a u(1) action on R^d come out slightly irrational.
- At the population level, B1.2 forces the frequency ratios to be rational; otherwise the
  closure torus would also be a symmetry. So snapping to integer charges is justified.
- The snapping procedure itself (continued fractions) is **[HEURISTIC]**.

### (viii) Canonicalizing a 3-dim compact simple algebra to the certifier's so(3) basis

**The certifier's conventions**, read from `src/ml/compiler/MLLieDischarge.fs`:
- The tables `blockGenerator(axis, l)` use the coded basis c = m + l. R_m sits at c = l + m
  and I_m at c = l − m.
- [L_x, L_y] = L_z, cyclically, and Σ L_a² = −l(l+1) I.
- L_z acts on (R_m, I_m) as [[0,−m],[m,0]], i.e. L_z e_{R_m} = m e_{I_m}.
- The Condon-Shortley factor ε_m = (−1)^m in the real↔complex change of basis makes L_y couple
  R↔R and I↔I only, while L_x couples R↔I only.
- At l = 1 the basis order is (y, z, x), and the tables equal P L^{std} Pᵀ for the standard
  generators (L_a)_{jk} = −ε_{ajk} (verified in check_b4).

**Verification of the whole pipeline.** Every step below was run end to end on a randomly
disguised 0⊕1⊕1⊕2 representation with d = 12. The final orthogonal U satisfies
U J_a Uᵀ = the block-diagonal certifier tables for a = x, y, z (check_b4).

**Step 1. Invariant inner product [canonical, THEOREM].**
- Goal: find M = Mᵀ ≻ 0 with L_iᵀM + ML_i = 0. The solutions form a nullspace, but when V is
  reducible a random element of it can be indefinite.
- A construction that guarantees positivity uses the Casimir operator on bilinear forms:

      Ω = −2 Σ_ij (κ⁻¹)_ij ρ_W(L_i) ρ_W(L_j),   with ρ_W(L)(S) = −(LᵀS + SL).

  For semisimple g, Ω is diagonalizable, and its eigenvalue 0 occurs exactly on the invariant
  forms.
- Let P₀ be the spectral projector onto ker Ω along range Ω. Compute it with right and left
  null bases N_r and N_l: solve (N_lᵀN_r) c = N_lᵀ vec(I) with `lu_solve`, then set
  M = N_r c.
- Theorem: P₀ is the unique G-equivariant projection onto the invariants, so it equals the
  Haar average. Hence **M = ∫_G gᵀg dg ≻ 0 when G is compact**. If the result is not positive
  definite, g is not compact: a useful diagnostic.
- Change variables with R := M^{1/2}, computed by `eigh`. Then L̃_i = R L_i R⁻¹ is
  antisymmetric, because L̃ᵀ + L̃ = R⁻¹(LᵀM + ML)R⁻¹ = 0.
- Verified: the invariant forms had dimension 6 = 1 + 4 + 1, M ≻ 0, and the antisymmetry
  residual was 10⁻¹³.

**Step 2. Normalize [canonical up to an SO(3) rotation].**
- Take a (−κ/2)-orthonormal basis, using `eigh` of −κ/2.
- Theorem: any (−κ/2)-orthonormal basis J_a satisfies [J_a, J_b] = s ε_abc J_c with s = ±1.
  - Reason: Aut(so(3)) = SO(3), which is O(3) ∩ {det = 1}. It acts transitively on oriented
    orthonormal frames, and a frame with det = −1 flips the sign of the structure constants.
- If c₁₂³ < 0, negate all three generators. In odd dimension this flips the determinant, and
  gives [L_x, L_y] = +L_z. Then κ = −2I, the normalization in which the l = 1 representation
  is the standard rotation generators.
- **The orientation is canonical**: it is fixed by the + sign.
- **The frame is a genuine choice.** Any SO(3) rotation of (J_x, J_y, J_z) is equally valid.
  It is an inner automorphism, and step 4 absorbs it by conjugating with D_l(R), so it is
  harmless for certification. A canonical frame would need extra data, for example principal
  axes of some symmetry-breaking statistic.

**Step 3. Split by the Casimir [canonical].**
- Compute C = J_x² + J_y² + J_z². It is symmetric and negative semidefinite. Run `eigh`.
  - The basis-free form is C = −2 Σ_ij (κ⁻¹)_ij J_iJ_j.
  - Sign convention: with these antisymmetric J and [J_x,J_y] = +J_z, C = −j(j+1)·I on spin j.
- The eigenvalue clusters −l(l+1), each with multiplicity n_l(2l+1), are the isotypic
  components V_l. Verified: −6 five times, −2 six times, 0 once.
- **Half-integer j shows up as −3/4, −15/4, …** (verified: −3/4 for su(2) acting on H).
  - Such blocks are quaternionic-type real representations of real dimension 2(2j+1).
  - The group is then SU(2), not SO(3), and **the real-harmonic tables do not apply**. Flag
    the block and stop.

**Step 4. Within each V_l, solve for the intertwiners [canonical up to O(n_l)].**
- Solve the linear system T J_a = D_a^{(l)} T for a = x, y, z, with T ∈ R^{(2l+1)×d}. In
  row-major vec form, this is the stacked system (I ⊗ J_aᵀ − D_a ⊗ I) vec T = 0.
- By Schur, the nullspace has dimension n_l.
- Orthonormalize the solutions under ⟨T, T′⟩ = tr(TT′ᵀ) (Löwdin orthonormalization via
  `eigh` of the Gram matrix). Scale each by √(2l+1). Each T then satisfies TTᵀ = I_{2l+1}.
- Stacking all the T over l gives an orthogonal U with U J_a Uᵀ = ⊕ D_a^{(l)}.
- The remaining freedom is O(n_l) on each V_l: the choice of an orthonormal basis of
  Hom_g(V_l, D_l) ≅ R^{n_l}.
  - For n_l = 1 this is a single sign ±1. It commutes with everything and leaves every table
    entry unchanged, so it is a true gauge freedom.

**Step 5. The same result via the eigenstructure of L_z, which explains the ambiguities.**
Within one copy:
1. The eigenspaces of L_z² with eigenvalue −m² are the m-planes, plus the m = 0 line. Once the
   frame is fixed, these are canonical.
2. The centralizer of L_z in O(2l+1) is {±1} × SO(2)^l: a sign on the m = 0 line and one angle
   per m-plane. A reflection within a plane anticommutes with the rotation there, so it is
   excluded.
3. Choose e_{R₀}; this is the sign choice.
4. Climb the ladder: set e_{R_{m+1}} := normalize(P_{m+1} L_y e_{R_m}), where P_{m+1} is the
   L_z² eigenprojector. By the table the coefficient is positive: (1/2)√(2l(l+1)) at m = 0, and
   (1/2)√((l+m+1)(l−m)) for m ≥ 1.
5. Set e_{I_m} := L_z e_{R_m}/m.

Consequences:
- Once the R₀ sign is chosen, L_y fixes all l angles. L_x then serves as a *check*, not a
  fixer. This is the reverse of the brief's phrasing. Either works, since
  L_x = e^{(π/2)L_z} L_y e^{−(π/2)L_z} up to the cyclic convention.
- The Condon-Shortley phase is what makes L_y block-diagonal on R/I. Targeting a convention
  without it changes the signs and the sparsity pattern of the ladder.

**Step 6. Parity cannot be determined infinitesimally [THEOREM].**
- O(3) = SO(3) × {±I}, with −I central. The Lie algebra of O(3) equals that of SO(3).
- So **no infinitesimal method can see the parity bit**: not B1-B3, not B4, not this
  pipeline. The same goes for every other component of π₀ (all discrete symmetries).
- On each isotypic component V_l ≅ D_l ⊗ R^{n_l}, ρ(−I) must equal I ⊗ S, where S is a
  symmetric orthogonal involution.
- For multiplicity-free spectra this leaves 2^{#blocks} finite candidates. Test each one with
  a *finite* criterion:
  - an MMD or moment test of P against ρ(−I)_#P; or
  - comparing f(ρ(−I)x) with ρ_out(−I)f(x).

  This matches the certifier's separate "−I identity" check.
- With multiplicities, the candidates form a union of Grassmannians inside ∏ O(n_l), and the
  search is non-convex. Beyond enumeration this is **[OPEN]**.

**Summary: what is canonical and what is a choice.**
- **Canonical:**
  - the invariant inner product (up to a scale per isotypic component, which orthonormalization
    removes);
  - the orientation;
  - the Casimir split;
  - the m-plane structure.
- **Choices:**
  - the SO(3) frame (harmless);
  - O(n_l) for each l (harmless);
  - the R₀ sign per copy (a gauge freedom).
- **Invisible to infinitesimal data:** the parity bit, and all discrete symmetries.

---

## B5. Learning the latent jointly: degeneracy and the rigorous objective

### B5.1 Transport theorem [THEOREM]

**Statement.** Let Q be a probability measure on R^d with a strictly positive density
q ∈ C^r (r ≥ 1).
- Assume every marginal density q_{≤k}(x₁…x_k) = ∫ q dx_{k+1}…dx_d is C^r and positive.
- A sufficient condition: q and its partial derivatives up to order r are locally uniformly
  dominated by an integrable function.

Then the Knothe-Rosenblatt map T = (T₁,…,T_d), defined by

    T_k(x_{≤k}) = Φ⁻¹(F_k(x_k | x_{<k})),

is a C^r diffeomorphism of R^d with T_#Q = N(0, I_d). Here F_k is the conditional CDF and Φ is
the standard normal CDF.

**Proof sketch.**
- T is triangular, with ∂T_k/∂x_k = q_k(x_k | x_{<k}) / φ(T_k) > 0.
- Each map x_k ↦ T_k(x_{<k}, x_k) is an increasing bijection of R, so T is a bijection.
- Applying the inverse function theorem coordinate by coordinate gives a C^r triangular
  inverse.
- By the probability-integral transform, T_k is N(0,1) conditionally on T_{<k}.

**References.**
- Rosenblatt 1952; Knothe 1957; Bogachev-Kolesnikov-Medvedev 2005 [verified].
- Santambrogio 2015, Ch. 2 on Knothe transport [book verified; section from memory].
- The optimal-transport alternative is Brenier 1991 / Villani 2003. Its regularity requires
  Caffarelli-type hypotheses. The Knothe-Rosenblatt map is enough here.

**Manifold-supported data.** Apply the theorem in the latent space. Q = φ_#P only needs to be
an absolutely continuous law with a smooth positive density on R^d, or on an open set
diffeomorphic to R^d. The data law itself need not be absolutely continuous on R^D.

### B5.2 Corollary: the vacuity covers every objective in the plan [THEOREM]

**Construction.** Let (φ, ψ) satisfy ψ∘φ = id P-a.s., with Q = φ_#P as in B5.1. Set

    φ′ = T∘φ,   ψ′ = ψ∘T⁻¹.

Then:
- ψ′∘φ′ = ψ∘φ, so reconstruction is identical, and ψ′ is injective iff ψ is;
- φ′_#P = N(0, I), which is O(d)-invariant.

**Consequences.** For **every** A ∈ so(d), all of the following hold at once:
- (a) the latent moment residual vanishes: ρ_k(A)M_k(φ′_#P) = 0 for all k;
- (b) the latent Gaussian Hessian vanishes: q_G(A) = 0;
- (c) every latent MMD or discriminator comparing φ′(v) with e^{tA}φ′(v) is at its optimum;
- (d) the induced data-space action Γ′_t = ψ′∘e^{tA}∘φ′ preserves P exactly, because

      (Γ′_t)_#P = ψ_#T⁻¹_#e^{tA}_#N(0,I) = ψ_#T⁻¹_#N(0,I) = ψ_#Q = P;

- (e) every functional constraint from B3 that does not involve task data is unaffected.

**In words.** For every A ∈ so(d), the infimum over (φ, ψ) of reconstruction + latent
residual + data-space MMD is 0. This holds in any encoder class rich enough to approximately
contain T∘φ.

**Why this is unavoidable.** The P-preserving diffeomorphisms of a smooth positive density form
an infinite-dimensional group. So "P has some nonlinear symmetry" is always true. A learned
latent merely picks out a finite-dimensional linear slice of that group.

### B5.3 The data-space MMD is the right objective, and it does not cure B5.2 [THEOREM, CORRECTION]

**Theorem.** Let k be a bounded continuous characteristic kernel on R^D (for example the
Gaussian). Fix Borel maps φ and ψ and a matrix A, and define

    D(t) := MMD²_k(P, (Γ_t)_#P),   with Γ_t := ψ∘e^{tA}∘φ.

- (a) **Proper.** D(t) ≥ 0, and D(t) = 0 iff (Γ_t)_#P = P. D(t) has a closed-form, unbiased
  U-statistic estimator (B2.6), with no inner maximization.
- (b) **Equivalence with the latent condition.** If ψ∘φ = id P-a.s. and ψ is injective, then
  (Γ_t)_#P = P ⟺ (e^{tA})_#Q = Q, where Q = φ_#P.
  - Proof: (Γ_t)_#P = ψ_#(e^{tA})_#Q and P = ψ_#Q. An injective Borel map between Polish
    spaces is a Borel isomorphism onto its image (Lusin-Souslin), so ψ_# is injective on
    measures. ∎
- (c) **Local implies global.** If D(t) = 0 for |t| < ε, then (e^{tA})_#Q = Q for all t. The
  reason: {t : e^{tA}_#Q = Q} is a closed subgroup of R containing an interval. Under the
  hypotheses of (b), D ≡ 0 follows.
- (d) **Infinitesimal form.** Assume φ∘ψ = id on the latent orbit, so that Γ_t is a flow, and
  assume the induced field ξ_A(x) = Dψ(φ(x)) A φ(x) satisfies E|ξ_A| < ∞. Then

      D(t) = t² q_G^ψ(A) + o(t²),   with q_G^ψ(A) = E[ξ_A(x)ᵀ ∇₁∇₂k(x,x′) ξ_A(x′)].

  - Its kernel is exactly the set of A whose flow preserves P. The proof of B2.5 carries over,
    because a flow commutes with its own generator: DΓ_t(x)ξ(x) = ξ(Γ_t x).
  - It needs only decoder JVPs, **and no expm**.

**The correction.**
- Part (b) says that for an exact, injective autoencoder, the data-space and latent conditions
  are equivalent. B5.2(d) exhibits the transport family satisfying both.
- So the brief's "therefore the symmetry must be stated through the decoder … which turns it
  into a proper objective" is right about *properness* and wrong about *identifiability*.

**Why the data-space form still earns its place.**
- (i) It measures what the user cares about: whether decoded, transformed samples look like
  data.
- (ii) It stays meaningful when the autoencoder is inexact or ψ is not injective.
- (iii) It does not depend on the latent coordinates.

### B5.4 Trivial solutions and principled normalizations

| | degenerate solution | removed by |
|---|---|---|
| T1 | A = 0 | any normalization |
| T2 | Az = 0 on supp Q (latent collapse; LaLiGAN's "fallacious symmetry") | restricting to span supp Q (B1.4, consequence 4), or N3 |
| T3 | Dψ(z)Az = 0 on supp Q (motion along the fibers of ψ, where ψ is not immersive or injective) | N3 |
| T4 | the transport family of B5.2 | **none of the normalizations below**: this is non-identifiability, not triviality |

**N1: Frobenius / Stiefel normalization.** Constrain W ∈ R^{p×c}, whose columns are the
vectorized generators, by WᵀW = I.
- In the fixed-encoder half, the eigen-solution satisfies this automatically. It is the
  *global* minimizer of tr(WᵀHW) over the Stiefel manifold (Ky Fan 1949).
- If W is ever updated by gradient, project it back with the polar factor W ← UVᵀ, from a thin
  `svd`. This is the nearest Stiefel point in every unitarily invariant norm (Fan & Hoffman
  1955). As the brief says, use projection, not a penalty.
- Draw the coefficients isotropically, w ~ N(0, σ²I_c). The realism term then depends only on
  span W, i.e. it is a Grassmannian objective, as it should be.

**N2: fix the algebra family.** Restrict A to so(d) in whitened latent coordinates. For
distributional symmetries this is *theorem-backed* (B1.4). Restricting to sl(d) has no such
backing for the distributional term.

**N3: normalize the induced field.** Require

    E‖ξ_A‖² = aᵀ G_ψ a = 1,   with G_ψ = E[(Dψ(z)ᵀDψ(z)) ⊗ (zzᵀ)],

since Dψ(z)Az = (Dψ(z) ⊗ zᵀ)a.
- The fixed-encoder half becomes the generalized symmetric eigenproblem Ha = λG_ψa. Solve it
  with Cholesky plus `eigh`; this is Ky Fan in the G_ψ geometry.
- N3 removes T1, T2 and T3, which all have aᵀG_ψa = 0.
- It normalizes the motion in *data space*, rather than an arbitrary matrix norm.
- Dψᵀ Dψ is the d×d pullback metric, obtainable from d JVPs per sample.

### B5.5 What could supply identifiability [HEURISTIC / OPEN]

None of these is a theorem in the plan's setting. Present them as design choices.

1. **Freeze the encoder.** Train (φ, ψ) for reconstruction only, then compute the algebra
   once.
   - The result is an honest statistic of the pair (P, φ). It answers "which symmetries does
     this representation expose".
   - This is theorem-backed for what it is, but it says nothing about a "true" latent.
2. **Constrain the geometry of φ**, for example with a near-isometry penalty E‖Dφᵀ… − I‖ or a
   conformality penalty.
   - If ψ is an exact isometric embedding of a *flat* latent, reparametrizations are confined
     to the Euclidean group. The discovered algebra is then determined up to conjugation.
   - But exact isometric flat latents exist only when the data manifold is intrinsically flat.
     With a soft penalty it is a trade-off. **[HEURISTIC]**
3. **Couple to a task (B3).** Require the latent dynamics or labels to be equivariant under
   the same A.
   - A Gaussianizing T generically destroys the equivariance of F̃ = φ∘F∘ψ, because conjugacy
     invariants (such as the spectra at fixed points) constrain T.
   - This plausibly identifies the symmetry, but there is **no general theorem**. **[OPEN]**
4. **Architectural inductive bias.** This is what LaLiGAN implicitly relies on.
   **[HEURISTIC]**

### B5.6 The recommended alternating scheme, and exactly what it guarantees

**Step 1: fix (φ, ψ) and compute the algebra exactly.** Work on the latent codes
z_n = φ(x_n).
1. Assemble H: the B2.4 or B2.5 Hessian (polynomial or Gaussian, U-version). Add the B3 rows if
   a task or dynamics is available. Optionally add the data-space form q_G^ψ from B5.3(d).
2. Solve Ha = λG_ψa (N3), or run eigh(H) restricted to so(d) (N2).
3. Take the bottom c eigenvectors, choosing c at a spectral gap. **[HEURISTIC]**
4. Classify the result (B4), and optionally snap to the exact identified algebra.

*Guarantees of step 1 [THEOREM].*
- It finds the global minimizer of the step's quadratic objective under the constraint
  (Ky Fan).
- At the population level, with an exact autoencoder, the minimizer's span is exactly a Lie
  algebra (B1.2 / B2.5 / B3.1).
- With finite samples, the span lies within the Davis-Kahan/Wedin angle of the true algebra,
  and its closure defect is O(angle) (B1.5).

**Step 2: fix W and train (φ, ψ).** Minimize by SGD

    J(φ,ψ; W) = L_recon + λ₁·MMD²_U(P, (Γ_{e^{Σw_iL_i}})_#P)   [or its Hessian form, B5.3(d)]
              + λ₂·tr(WᵀH(φ)W)/tr(WᵀG_ψW) + λ₃·R_identify(φ),

where R_identify comes from B5.5.

*Guarantee of step 2.* Local descent only, since the problem is nonconvex. **[HEURISTIC]**

**What the alternation does not guarantee.**
- **No global convergence and no stationarity.**
- Step 1 minimizes only the quadratic (λ₂) part exactly. The λ₁ term also depends on W through
  the sampled group elements. So step 1 counts as a block-coordinate step for J only if it is
  safeguarded: accept the new W only when J decreases.
- With that safeguard, J is monotone non-increasing and converges *in value*. Nothing more.
- By B5.2, without λ₃ (or task coupling) the loop is self-reinforcing:
  1. step 1 picks whatever symmetry the current φ exposes;
  2. step 2 makes φ more symmetric under that W;
  3. the limit is therefore determined by the initialization and the inductive bias, **not by
     P**.
- So the scheme's output must be reported as "a symmetry consistent with P under encoder class
  Φ and regularizer R", not as "the symmetry of P".

### B5.7 Comparison with LaLiGAN (loss verified from arXiv v3 §§4.2–4.3)

**LaLiGAN's training setup.**
- **Total loss.** L_total = w_GAN·L_GAN + w_recon·L_recon, with L_recon = E‖ψ(φ(v)) − v‖².
- **Adversarial term.** L_GAN = E[log D(φ(v)) + log(1 − D(π(g)φ(v)))]. The text says the
  discriminator distinguishes the "original and transformed distribution in the latent space".
  - Caveat: the Fig. 2 caption ("the decoder reconstructs … original and transformed
    representations") and the D(v), D(gv) notation in App. A.8 are ambiguous. The equation in
    the main text is in the latent space.
- **Group sampling.** π(g) = exp(Σ w_iL_i), with w ~ γ (e.g. Gaussian; σ unspecified).
- **Regularization.** None on the basis. Instead:
  - an orthogonal (Householder) final encoder layer prevents collapse;
  - per-batch zero-mean normalization prevents off-center latents.
- **Closure.** Not enforced. It is checked post hoc, and on top tagging the structure constants
  are compared with so(1,3).
- **Theory.** Its Thm 4.1 is an *expressivity* result: a suitable representation exists.
  - Hypotheses: G compact and acting freely and properly; a compact data manifold; a simply
    connected orbit space; bounded subsets of the group.
  - It says nothing about identifiability, which is consistent with B5.2.

**What the plan gains.**
1. Exact closure at the population level, with an explicit bound on the finite-sample defect.
2. The algebra half is solved globally, by eigen-decomposition or SVD.
3. Every objective is a closed-form statistic with an unbiased estimator. There is no inner
   maximization and no mode-collapse dynamics.
4. Algorithmic classification and canonicalization into the certifier's tables (B4).
5. A theorem-backed restriction of the search space to so(d) (B1.4), and an honest exclusion
   of non-compact distributional symmetries. By B1.4, LaLiGAN's top-tagging comparison with
   so(1,3) is necessarily about approximate invariance.
6. An expm-free infinitesimal path for every term (B5.3(d)).
7. The degeneracy is stated rather than hidden. LaLiGAN's latent GAN is exactly the vacuous
   condition of B5.2(c). Its orthogonal last layer prevents T2 only, and does nothing against
   T4.

**What the plan loses.**
1. A learned discriminator adapts its features, which gives it power; a fixed kernel does not.
   Kernel-test power also decays polynomially with dimension (Ramdas et al. 2015). Learning
   the kernel reintroduces a maximization, though only over a closed-form statistic.
2. Polynomial/moment objectives see only orders ≤ K, and indeterminate laws can fool them
   (B1.3). The Gaussian form fixes this, at O(n²) cost plus a bandwidth choice.
3. As written, nothing in the plan identifies the latent any better than LaLiGAN's inductive
   bias does. B5.5 is where the plan must add something.

---

## B6. The matrix exponential and its derivative

### B6.1 Where expm is needed, and where it is not

**Not needed:**
- B1, which is linear in A;
- B2, whose Hessians are quadratic forms built from Gram matrices;
- B3, which is linear;
- B4, which is linear algebra on the algebra;
- the data-space realism term in its infinitesimal form (B5.3(d)), which needs only decoder
  JVPs.

**Needed:**
1. The finite-t realism check when the autoencoder is inexact. Γ_t is then not a flow, and the
   infinitesimal form only checks an idealized field.
2. Generating augmentations.
3. Sampling group elements, Haar or otherwise.
4. Testing *discrete* elements. (ρ(−I) itself needs no expm at all.)

### B6.2 Algorithms and backward error [THEOREM]

**Option P: Padé degree 13 (Higham 2005).** The thresholds below were verified against Eigen's
implementation of Higham 2005. Choose the [m/m] Padé approximant, m ∈ {3, 5, 7, 9, 13}, by the
first m with ‖A‖₁ < θ_m:

| m | θ_m |
|---|---|
| 3 | 1.4956·10⁻² |
| 5 | 0.25394 |
| 7 | 0.95042 |
| 9 | 2.0978 |
| 13 | 5.3719 |

Otherwise use m = 13 with s = ⌈log₂(‖A‖₁/θ₁₃)⌉ squarings. The cost is 6 matrix products and
one `lu_solve`.

**Option T: truncated Taylor, solve-free** (simpler to differentiate). Choose the degree m and
s = max(0, ⌈log₂(‖A‖₁/θ_m)⌉). Then, in exact arithmetic,

    T_m(2⁻ˢA)^{2ˢ} = e^{A + ΔA},   ‖ΔA‖₁ ≤ u‖A‖₁.

Here θ_m is the largest θ with Σ_{k>m} |δ_k| θ^{k−1} ≤ u, where
h(x) = log(e^{−x}T_m(x)) = Σ_{k≥m+1} δ_k x^k. The values below were computed for this review
with exact rational coefficients:

| m | 12 | 16 | **18** | 20 | 24 | 30 |
|---|---|---|---|---|---|---|
| θ_m | 0.2996 | 0.7803 | **1.0909** | 1.4383 | 2.2191 | 3.5397 |

**Proof** (Higham 2005's argument, transplanted to Taylor).
1. Every function of X commutes with X, so T_m(X) = e^{X + h(X)}.
2. Hence T_m(X)^{2ˢ} = e^{2ˢX + 2ˢh(X)}.
3. ‖2ˢh(X)‖ ≤ 2ˢ Σ|δ_k|‖X‖^k ≤ 2ˢ u‖X‖ = u‖A‖, because θ ↦ Σ|δ_k|θ^{k−1} is increasing. ∎

**Recommendation and notes.**
- Use m = 18 with θ = 1.09. That is about 2√m ≈ 8 products with Paterson-Stockmeyer, or 18
  with Horner.
- These θ_m use the plain ‖X‖^k bound. Al-Mohy & Higham's sharper ‖X^k‖^{1/k} analysis allows
  larger θ; it is not needed here.
- *Rounding.* The squaring phase can lose accuracy for highly non-normal A (Higham 2005;
  Al-Mohy & Higham 2010). It is benign for the near-antisymmetric generators of the compact
  case.
- Checked against SciPy: relative error 2·10⁻¹⁶ to 9·10⁻¹⁵ for ‖A‖₁ from 0.27 to 97
  (check_b6).

### B6.3 The Fréchet derivative

**Definition.** L(A,E) = ∫₀¹ e^{sA} E e^{(1−s)A} ds.

**Forward mode through the algorithm.**
1. Set X = 2⁻ˢA and dX = 2⁻ˢE.
2. Differentiate the Horner recurrence: Y ← I + XY/k, dY ← (dX Y + X dY)/k.
3. Differentiate the squarings: dY ← Y dY + dY Y, then Y ← Y².

This is the derivative of the *computed approximant* (trivially, since that is what AD
differentiates). It costs about 3× the expm.

**Reverse mode.** Since ⟨G, L(A,E)⟩ = ⟨L(Aᵀ,G), E⟩, the gradient of ℓ(e^A) is L(Aᵀ, ∇ℓ). The
same identity holds for the polynomial approximant and through the squarings.

**Backward-error content.**
- For Padé, Al-Mohy & Higham 2009 [verified, abstract-level] show that the computed pair is the
  exact (e^{A+ΔA}, L(A+ΔA, E+ΔE)), with the *same* ΔA in both. SciPy's `expm_frechet` (SPS
  method) implements this paper [verified].
- The Taylor analog follows from T_m(X) = e^{X+h(X)} by the chain rule:

      L_{T_m}(X,F) = L(X + h(X), F + L_h(X,F)).

  - Here ‖L_h(X,F)‖ ≤ Σ k|δ_k|θ^{k−1}‖F‖ ≈ **20u‖F‖ at m = 18, θ = 1.09** (computed).
  - The squaring phase carries this over exactly.
- *Caveat.* s and m are piecewise constant in ‖A‖. AD treats them as constants, so the computed
  map jumps by O(u) at the switch points. This is harmless for optimization.

**Independent checks for test pins.** All three passed in check_b6.
1. **Block-triangular identity** (Najfeld & Havel 1995; Mathias 1996; Higham 2008, Ch. 3 — from
   memory):

       exp([[A, E],[0, A]]) = [[e^A, L(A,E)],[0, e^A]].

   - Compute the left side with the *same* expm routine on the 2d×2d matrix, scaling E so that
     ‖E‖₁ ≈ ‖A‖₁.
   - Observed agreement: 3·10⁻¹⁶ at ‖A‖₁ = 2.4, 2·10⁻¹³ at 24, and 10⁻¹² at 97.
   - **Pin at 10⁻¹² relative error for ‖A‖₁ ≤ 25.**
2. **Adjoint identity** ⟨G, L(A,E)⟩ = ⟨L(Aᵀ,G), E⟩. This checks reverse mode against forward
   mode; they agree to 10⁻¹⁵.
3. **Exact closed form on so(2).** Let J = [[0,−1],[1,0]].
   - Decompose E = αI + βJ + S, with S symmetric and traceless, so that SJ = −JS.
   - Then L(θJ, E) = αe^{θJ} + βJe^{θJ} + (sin θ/θ)S.
   - Reason: f(J)S = Sf(−J), which gives ∫₀¹ e^{(1−2s)θJ}ds = (sin θ/θ)I.
   - Verified to 7·10⁻¹⁶.

### B6.4 Distributions on group elements

**A full-support law on the algebra gives full support on the group, but not Haar
[THEOREM].**
- For compact connected G, exp is surjective (maximal torus theorem; Bröcker & tom Dieck,
  Ch. IV). So the pushforward of any full-support law on the algebra, such as N(0, σ²I), has
  full support on G.
- On SO(3) it is never Haar. In the standard normalization, exp maps every sphere |w| = 2πk
  onto I.
- **As σ → ∞, the rotation angle tends to the uniform law on [0, π].** Haar has angle density
  (1 − cos θ)/π instead. A simulation confirms this: the wide-Gaussian per-bin densities are
  0.314–0.321, against Haar bins ranging from 0.014 to 0.622.
- **Exact Haar sampling on SO(3) through the same exp:** draw the axis uniformly on S², draw
  the angle θ with density (1 − cos θ)/π on [0, π], and set w = θ·axis.
- For a general compact g, Haar measure in exponential coordinates carries the Jacobian
  det((1 − e^{−ad X})/ad X) (Weyl integration).
- A non-compact G has no Haar *probability* measure, and exp may fail to be surjective. For
  example, elements of SL(2,R) with trace < −2 are not exponentials. Full support then needs
  products of exponentials.

**Small t is enough for the realism term [THEOREM],** provided Γ is a group action, i.e.
φ∘ψ = id on the latent orbit.
- The set of g whose Γ_g preserves P is a group. A neighborhood of the identity generates a
  connected group, so if the set contains exp of a neighborhood of 0, it contains the identity
  component G₀.
- So sampling w from a small ball, or from a narrow Gaussian, certifies G₀-invariance.

**Statistical behavior of small t.**
- The finite-t U-statistic equals t² times the Hessian statistic, plus O(t³).
- So after dividing by t², a small t loses nothing compared with the infinitesimal test. A very
  small t only adds cancellation error. Use the Hessian form directly.
- Small t is blind to discrete components (step 6 of B4.viii). For an inexact autoencoder it is
  also blind to failures of the action property at finite t. That is what the finite-t check,
  with moderate t, is for.

---

## B7. Claim ledger

| # | claim | status | hypotheses / note |
|---|---|---|---|
| 1 | B1.1: moment invariance to order K ⟺ ρ_k(A)M_k = 0 ⟺ invariance for all t | THEOREM | finite K-th moments |
| 2 | g_K is a Lie subalgebra, equal to the Lie algebra of an algebraic group; cumulants give the same g_K | THEOREM | none beyond claim 1 |
| 3 | the nullspace estimate is closed | THEOREM only at the population level; the finite-sample defect is O(sin θ) | B1.5 |
| 4 | g_∞ = sym(P) | THEOREM for moment-determinate P (e.g. E e^{ε\|z\|} < ∞); FALSE in general (lognormal counterexample) | — |
| 5 | the chain has stabilized at K | HEURISTIC | K₀ exists but cannot be computed |
| 6 | linear distributional symmetries are compact; g_K ⊆ so(M₂) for K ≥ 2 | THEOREM | M₂ finite and ≻ 0 |
| 7 | after whitening, K ≤ 2 carries no selection information | THEOREM | — |
| 8 | perturbation bound sin θ ≤ 2‖E‖/g; rank recovery when ‖E‖ < τ < g − ‖E‖ | THEOREM | finite 2K-th moments for the rate; g is unknown, so τ is HEURISTIC |
| 9 | poly-kernel MMD² = Σ binom(K,j)c^{K−j}‖ΔM_j‖²_F, exactly, including for V-statistics | THEOREM | c ≥ 0 (c > 0 to weight all orders) |
| 10 | MMD² vanishes to first and second order at a symmetry | CORRECTED: F′(0) = 0 for every A; F″(0) = 2Σw_j‖ρ_jM_j‖² | — |
| 11 | the kernel of the Gaussian-kernel Hessian is sym(P) | THEOREM (new) | E\|z\| < ∞; the finite-sample form is positive definite, so a threshold is needed (HEURISTIC) |
| 12 | the U-statistic is the right training loss; the paired V-statistic is biased toward A = 0 | THEOREM | — |
| 13 | s(f) is a Lie subalgebra; finite form ⟺ infinitesimal form | THEOREM | f ∈ C² (C¹ and U = R^d for the group form) |
| 14 | Nm ≥ d² + m² − dim s(f) samples suffice | CORRECTED: necessary only. Generically sufficient for N ≥ N_* ≤ c (analytic f); deterministic via unisolvence (polynomial f) | — |
| 15 | the sampled s_V is a Lie algebra on structured data | FALSE in general (counterexample); TRUE for fields tangent to V | — |
| 16 | Cartan's criteria; rad = [g,g]^⊥κ; compact ⟺ κ < 0 on [g,g] | THEOREM | characteristic 0 |
| 17 | a Levi factor can be found as a complement | THEOREM (it exists); not unique (Malcev); canonical for compact g (= [g,g]) | — |
| 18 | a generic commutant element gives the isotypic components | CORRECTED: use a generic *symmetric* element (a central one for isotypic components) in an invariant frame | complete reducibility |
| 19 | small algebras can be identified from the invariants table | THEOREM for compact/semisimple; incomplete for solvable families (HEURISTIC) | — |
| 20 | canonicalization to the certifier's so(3) tables | THEOREM up to: the SO(3) frame (choice), O(n_l) (choice), a sign per copy (gauge); half-integer spins excluded | compact, 3-dim simple |
| 21 | O(3) parity / discrete symmetries from infinitesimal data | IMPOSSIBLE (THEOREM); finite tests are needed; the multiplicity case is OPEN | — |
| 22 | latent linear symmetry is vacuous | THEOREM | smooth positive latent density (Knothe-Rosenblatt) |
| 23 | data-space MMD through the decoder fixes the vacuity | FALSE (equivalent to the latent condition for an exact injective autoencoder); proper for a fixed autoencoder | — |
| 24 | the jointly learned latent symmetry is identifiable | OPEN / HEURISTIC | needs an ingredient from B5.5 |
| 25 | the alternating scheme converges | only monotone in value, with the safeguard; no stationarity or global claim | — |
| 26 | the algebra half is globally optimal | THEOREM (Ky Fan) | — |
| 27 | Taylor-18 / Padé-13 backward error ≤ u‖A‖; the AD derivative is the exact L at a nearby point | THEOREM (exact arithmetic) | θ_m as tabulated |
| 28 | the infinitesimal objectives need no expm | THEOREM, extended: also the realism term via B5.3(d) | Γ a flow |
| 29 | Gaussian algebra coefficients give Haar | FALSE; full support only; the wide limit is the uniform-angle law | — |
| 30 | small t is enough | THEOREM for G₀; blind to π₀ | Γ a group action |

---

## References

**Key:** [V] = verified online during this review. All other entries are standard references
cited from memory. Section or theorem numbers marked "from memory" were not verified.

### Symmetry discovery
- [V] J. Yang, R. Walters, N. Dehmamy, R. Yu. *Generative Adversarial Symmetry Discovery*
  (LieGAN). ICML 2023, PMLR 202:39488–39508. arXiv:2302.00236.
- [V] J. Yang, N. Dehmamy, R. Walters, R. Yu. *Latent Space Symmetry Discovery* (LaLiGAN).
  **ICML 2024**, PMLR 235:56047–56070. arXiv:2310.00105. (v3 read: Eq. 4 latent GAN loss,
  §4.3, Thm 4.1, Prop 4.2.)
- [V] A. Moskalev, A. Sepliarskaia, I. Sosnovik, A. Smeulders. *LieGG: Studying Learned Lie
  Group Generators*. NeurIPS 2022. arXiv:2210.04345.
- [V] S. E. Otto, N. Zolman, J. N. Kutz, S. L. Brunton. *A Unified Framework to Enforce,
  Discover, and Promote Symmetry in Machine Learning*. JMLR 26(248):1–83, 2025.
  arXiv:2311.00212. (Theorem numbers above are from arXiv v3.)

### Kernel methods and MMD
- [V] B. K. Sriperumbudur, A. Gretton, K. Fukumizu, B. Schölkopf, G. R. G. Lanckriet. *Hilbert
  Space Embeddings and Metrics on Probability Measures*. JMLR 11:1517–1561, 2010.
- A. Gretton, K. Borgwardt, M. Rasch, B. Schölkopf, A. Smola. *A Kernel Two-Sample Test*. JMLR
  13:723–773, 2012.
- [V] A. Ramdas, S. J. Reddi, B. Póczos, A. Singh, L. Wasserman. *On the Decreasing Power of
  Kernel and Distance Based Nonparametric Hypothesis Tests in High Dimensions*. AAAI 2015.

### Matrix exponential and matrix functions
- [V] N. J. Higham. *The Scaling and Squaring Method for the Matrix Exponential Revisited*.
  SIAM J. Matrix Anal. Appl. 26(4):1179–1193, 2005. (θ_m values cross-checked against Eigen's
  implementation.)
- [V] A. H. Al-Mohy, N. J. Higham. *Computing the Fréchet Derivative of the Matrix Exponential,
  with an Application to Condition Number Estimation*. SIAM J. Matrix Anal. Appl.
  30(4):1639–1657, 2009.
- A. H. Al-Mohy, N. J. Higham. *A New Scaling and Squaring Algorithm for the Matrix
  Exponential*. SIAM J. Matrix Anal. Appl. 31(3):970–989, 2009/10. (Bibliographic data seen in
  NAG documentation.)
- [V] SciPy `scipy.linalg.expm_frechet` documentation. (The SPS method implements Al-Mohy &
  Higham 2009; the page shows the block-triangular identity.)
- N. J. Higham. *Functions of Matrices: Theory and Computation*. SIAM, 2008.
- I. Najfeld, T. F. Havel. *Derivatives of the Matrix Exponential and Their Computation*. Adv.
  Appl. Math. 16:321–375, 1995. (From memory.)
- R. Mathias. *A Chain Rule for Matrix Functions and Applications*. SIAM J. Matrix Anal. Appl.
  17(3):610–620, 1996. (From memory.)

### Lie theory and algebra
- J. E. Humphreys. *Introduction to Lie Algebras and Representation Theory*. GTM 9, Springer,
  1972. (§4.3 Cartan's criterion, §5.1 semisimplicity criterion, §6.3 Weyl's theorem.)
- A. W. Knapp. *Lie Groups Beyond an Introduction*, 2nd ed. Birkhäuser, 2002. (Ch. IV, compact
  Lie algebras; from memory.)
- W. Fulton, J. Harris. *Representation Theory: A First Course*. GTM 129, Springer, 1991.
- N. Jacobson. *Lie Algebras*. Interscience, 1962. (Levi and Malcev-Harish-Chandra theorems;
  chapter from memory.)
- N. Bourbaki. *Lie Groups and Lie Algebras*, Ch. I. [V] The statement used here (the radical
  is the Killing-orthocomplement of [g,g] in characteristic 0) was checked via Encyclopedia of
  Mathematics, "Killing form".
- W. A. de Graaf. *Lie Algebras: Theory and Algorithms*. North-Holland, 2000.
- T. Bröcker, T. tom Dieck. *Representations of Compact Lie Groups*. GTM 98, Springer, 1985.
  (§II.6 real and quaternionic representations; Ch. IV maximal tori; from memory.)
- S. Lang. *Algebra*, rev. 3rd ed. GTM 211, Springer, 2002. (Wedderburn; from memory.)
- J. M. Lee. *Introduction to Smooth Manifolds*, 2nd ed. GTM 218, Springer, 2013. (Ch. 8,
  f-related vector fields.)

### Probability and moments
- P. Billingsley. *Probability and Measure*, 3rd ed. Wiley, 1995. (§29 Cramér-Wold, §30 method
  of moments.)
- K. Schmüdgen. *The Moment Problem*. GTM 277, Springer, 2017.

### Matrix perturbation and optimization
- C. Davis, W. M. Kahan. *The Rotation of Eigenvectors by a Perturbation. III*. SIAM J. Numer.
  Anal. 7(1):1–46, 1970.
- P.-Å. Wedin. *Perturbation Bounds in Connection with Singular Value Decomposition*. BIT
  12:99–111, 1972.
- G. W. Stewart, J.-G. Sun. *Matrix Perturbation Theory*. Academic Press, 1990.
- K. Fan. *On a Theorem of Weyl Concerning Eigenvalues of Linear Transformations I*. PNAS
  35:652–655, 1949.
- K. Fan, A. J. Hoffman. *Some Metric Inequalities in the Space of Matrices*. Proc. AMS
  6:111–116, 1955.

### Transport
- M. Rosenblatt. *Remarks on a Multivariate Transformation*. Ann. Math. Statist. 23:470–472,
  1952.
- H. Knothe. *Contributions to the Theory of Convex Bodies*. Michigan Math. J. 4:39–52, 1957.
- [V] V. I. Bogachev, A. V. Kolesnikov, K. V. Medvedev. *Triangular Transformations of
  Measures*. Sbornik: Math. 196(3):309–335, 2005.
- [V] F. Santambrogio. *Optimal Transport for Applied Mathematicians*. Birkhäuser, 2015.
  (Knothe transport; section number not verified.)
- [V] G. Carlier, A. Galichon, F. Santambrogio. *From Knothe's Transport to Brenier's Map…*
  arXiv:0810.4153.
- Y. Brenier. *Polar Factorization and Monotone Rearrangement of Vector-Valued Functions*. CPAM
  44:375–417, 1991.
- C. Villani. *Topics in Optimal Transportation*. AMS, 2003.

---

## Appendix A — numerical checks

All scripts are in `checks/` and use Python with NumPy and SciPy.

| script | what it checks | result |
|---|---|---|
| check_b2.py | (1) the poly-MMD V-statistic equals the weighted moment distance; (2) F″(0) = 2q; (3) the Gram formula for q; (4) the Gaussian-Hessian Gram formula against finite differences; (5) the Gaussian diagonal term | (1) relative error 3e-16; (2) 13445.74 (FD) vs 13445.68; (3) exact; (4) 0.343134 vs 0.343134; (5) diagonal = tr(G_AA)/(n²σ²) |
| check_b2_assembly.py | the d²×d² Kronecker assembly for the dot-product and Gaussian Hessians | exact to 1e-15 |
| check_b1.py | the finite-sample moment nullspace for so(2) inside so(3), with sample whitening | K = 2: three exact zeros; K = 3: smallest σ ∝ N^{−1/2}, against a gap ≈ 3.2 |
| check_b4.py | the certifier tables for l ≤ 4 (bracket, Casimir, antisymmetry); l = 1 equals the (y,z,x)-permuted standard generators; full canonicalization of a disguised 0⊕1⊕1⊕2 representation | all exact; U J_a Uᵀ equals the certifier tables for a = x, y, z |
| check_b4b.py | Killing signature, [g,g], center, radical as [g,g]^⊥, and rank for 11 algebras; commutant dimensions (R/C/H types); Casimir −3/4 for su(2) on H | the table in (vii) |
| check_b6.py | the Taylor θ_m; Taylor scaling-and-squaring and its forward-mode Fréchet derivative against SciPy and the block-triangular identity; the adjoint identity; the so(2) closed form; wide-Gaussian vs Haar angle law | see B6 |
