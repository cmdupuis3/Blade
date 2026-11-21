# Audit: real-number and primitive-float proofs

Date: 2026-10-15. Target: `claude/pensive-proskuriakova-297cea`.

Reviewed all 178 named claims in the eight `proofs/reals/*.v` files and all seven claims in `proofs/floats/BladeBinary64Witness.v`, including their definitions, section hypotheses, proof bodies, and corresponding descriptions in `docs/proofs.md`, `proofs/README.md`, the recurrence-reduction research draft, and the float-summary implementation plan. These are research models, not a verification of a shipped compiler transformation.

The main mathematical collision, density, differentiability, and error-propagation arguments withstand this audit. The significant findings are at the boundary between conditional real-arithmetic theorems and the proposed executable float contract. None below demonstrates an inconsistency or a false theorem accepted by the kernel.

Severity is relative to adopting the proposed contract: **P1** would prevent an ordinary accepted input from receiving the advertised guarantee; **P2** needs a theorem-interface or claim correction before implementation; **P3** is useful strengthening or precision in scope.

## Findings

### RF-1 — P1: the two-pass seed can make the required relative-error premise impossible

References: `BladeShiftedFeedback.v:68` (`rcl`), `:445` (`feedback_bound`), `:548` (`feedback_particles`), `:582` (`normalized_ensemble_bound`); `BladeShiftedVariance.v:426` (`renormalized_by_stddev_bound`); `BladeShiftedRounding.v:238`; plan `docs/plans/plan-float-summary-reduction.md:258-266,337,360-361`.

The feedback theorems require a finite multiplicative/log-relative error for **every** moment from 1 through r at initialization. In particular, `rcl l mh 0` entails `mh = 0` for every radius l. The ordinary relative predicate `rc` has the same exact-zero requirement. The plan proposes a two-pass mean followed by a fused pass over shifted powers, and treats the seed's error as the starting radius. Such a radius need not exist.

Concrete binary64 example, with the right-to-left fold used by Blade:

```text
xs = [3, -10^16, 10^16]
c  = fl((3 + fl(-10^16 + 10^16)) / 3) = 1
```

The exact mean is 1 and the exact first shifted sum is zero. Both large centered differences round back to their original large values; the small difference is 2. The second pass therefore returns `Mh1 = 2`. No finite initial radius can satisfy the existing hypothesis. There is no overflow, underflow, NaN, or infinity in this example.

The mean and nonzero first shifted sum were independently kernel-checked using primitive floats; the executable probe is included below. This is a concrete applicability failure of the proposed seed contract, not a counterexample to a theorem whose premises hold.

Recommended hardening:

- Prove a seed theorem with absolute or mixed absolute/relative errors for signed and odd moments. Do not assume that a two-pass seed supplies relative accuracy at a zero or near-zero moment.
- Parameterize coefficient stability by the moments it actually reads. The `g/sqrt(M2)` instance need not require relative accuracy of M1 or every unused odd moment.
- For variance, propagate a bound on the absolute M1 error; its contribution is squared and can remain small even when relative error is infinite.
- Pin this example, exact zero odd moments, sign changes near zero, and near-cancellation in the future seed corpus.

### RF-2 — P2: normalization silently includes totalized division-by-zero and negative-square-root semantics

References: `BladeShiftedFeedback.v:163-179` (`rcl_inv`), `:217-231` (`rcl_sqrt`), `:302-307` (`normalizing_coefficient_stable`), `:582-592` (`normalized_ensemble_bound`); plan `:349-352`.

`rcl_inv` deliberately proves its zero case using Coq's `Rinv_0`; `rcl_sqrt` proves the nonpositive case using `sqrt_neg_0`. Thus the real model assigns `1/0 = 0` and `sqrt(x) = 0` for negative x. The normalization corollary has no positive-M2 hypothesis, no nonzero-denominator invariant, and no exceptional outcome.

For the constant ensemble `xs=[0,0]`, `c=0`, `g=1`, exact seed, and identity rounding, every stated moment premise holds. The real coefficient `g/sqrt(M2)` is zero and the model remains finite. Primitive binary64 instead gives infinity for the coefficient and NaN for its multiplication by a zero particle/moment. Both the zero-spread NaN and `sqrt(-1)` NaN were kernel-checked.

The stated standard relative-rounding model cannot represent those IEEE runs. That is the precise limitation: the theorem remains correct over totalized reals, and it does **not** certify that a run stays in the standard model. Describing the instantiated theorem as a float normalization contract without a domain condition obscures this distinction. The variance sibling already provides a useful example of the required discipline: `Inv_sd`, `vex_pos`, and `g<>0` establish positivity and its preservation.

Recommended hardening: expose a positive-denominator/radicand domain in the normalization corollary; prove preservation or return a checked exception; make the coefficient syntax rules carry domain obligations alongside error radii. Add IEEE witnesses for empty/constant ensembles and negative intermediate radicands. Global `rnd_rel` alone is not a runtime domain checker.

### RF-3 — P2: geometric growth is a valid upper bound, not an inherent feedback cost

References: `BladeShiftedFeedback.v:47-52,363-366,397-428`; `docs/plans/plan-float-summary-reduction.md:86-89`; `docs/research/exact-recurrence-reduction-proofs.md:1375-1376`; `docs/proofs.md:2370`.

The prose says the geometric rate `1+rL` is the real price of feedback and “not an artefact” or “not slack in the proof.” The proofs establish an upper bound by separately bounding a coefficient and the moment it multiplies; they do not establish a lower bound or preserve the correlation between the two errors.

Counterexample to the necessity claim: take the supplied normalizing coefficient `a = g/sqrt(M2)`, `r=2`, exact arithmetic, and any positive approximate second moment `Mh2 = theta*M2`. Its next value is

```text
(g / sqrt(Mh2))^2 * Mh2 = g^2
```

which equals the exact next second moment. Incoming error is erased in one step, while the generic radius has rate `1+2*(1/2)=2`. With rounding, dependence between the coefficient and its source moment can still cancel the incoming error; independent triangle inequalities discard that information. The plan itself observes at `:411-413` that exact standard-deviation normalization gives variance g² every step.

Recommended hardening: replace the necessity language with “conservative worst-case upper bound under the abstract stability hypothesis”; keep the generic theorem; prove a specialized normalized-M2 theorem exploiting the cancellation. A worst-case sharpness theorem would require a separate adversarial coefficient family.

### RF-4 — P2: a rounded seed's rho is not automatically the true-run upper bound required by the theorem

References: `BladeShiftedVariance.v:219-224,349-350,426-435`; plan `docs/plans/plan-float-summary-reduction.md:365-376`.

The theorem requires a bound on the **true** moments, `M1^2 <= rho*(N*M2)`, with `0 <= rho < 1`. The plan says to compute rho from the seed row at runtime and use it to evaluate the radius. The float row contains approximate moments. Directly substituting `Mh1^2/(N*Mh2)` need not satisfy the invariant.

Even under the stronger existing log-relative premises, take `Mh1 = exp(-l)*M1` and `Mh2 = exp(l)*M2`. The computed ratio before its own rounding is `exp(-3l)` times the true ratio. It underestimates the needed upper bound. This can make the `Lo > 0` check succeed for a radius where the proved theorem is inapplicable. RF-1 also shows that finite relative seed bounds do not always exist.

An additional kernel-checked witness uses `xs=[0,1]`, c=0, N=2, hence true M1=M2=1. Approximate moments Mh1=1/2 and Mh2=2 both meet `rcl (ln 2)`. Their ratio is 1/16, but `Inv_sd 2 (1/16)` is false for the true ensemble (whose ratio is 1/2). This isolates the admission error independently of any implementation of the seed.

Recommended hardening: derive a certified upper bound for true rho from certified seed intervals, rounding the bound outward. With valid common log-error l, the exact approximate ratio must be inflated by at least `exp(3l)` before accounting for arithmetic used to compute it. Mixed-error treatment is needed near M1=0. Prove the runtime admission check implies `Inv_sd` and the final positive-Lo premise; do not treat a raw ratio estimate as that proof.

### RF-5 — P2: the “true standard deviation” instance does not bind N to the ensemble length

References: `BladeShiftedVariance.v:208-214,340-350,426-435`; `docs/proofs.md:2389-2391`.

N is an arbitrary positive real section variable. `renormalized_by_stddev_bound` quantifies an independent list xs but never requires `N = INR(length xs)`. Its formula is therefore a variance-like expression with an arbitrary divisor, not necessarily the particle ensemble's variance.

For `xs=[-1,1]`, `c=0`, `N=1`, and `rho=0`, `Inv_sd` holds: M1=0 and M2=2. The theorem's `vex N m` is 2, while the population variance is 1. The corresponding normalization uses `g/sqrt(2)` instead of g. This is a correct generic scaled-statistic theorem, but its advertised concrete interpretation needs the missing equality.

Recommended hardening: keep the generic theorem and add a particle-facing corollary setting N to `INR(length xs)` and requiring a nonempty ensemble. Prove length preservation once for `piter` and name this specialized corollary in the documentation.

### RF-6 — P3: distinguish a fixed moment-summary refusal from observer-specific impossibility

References: `BladeReduceClassification.v:29-33,100-108,153-176`; `docs/proofs.md:2110-2111`; `proofs/README.md:780`.

The refused branch says there is no update of the chosen first r power sums at the witness parameters and the sufficient extent threshold N>=d*r. It does not say that every observation-preserving reduced program is impossible. An observer that always returns a constant admits a one-state implementation even when the particle update is squaring and the chosen moment vector does not close.

The real proof's own scope paragraph correctly states the distinction; the detailed research summary also says “of the summary.” Shorter descriptions saying “no reduced program exists” should retain that qualification. Special parameter values, small extents, restricted invariant sets, and alternative summaries are likewise outside this negative conclusion.

## Per-file coverage and adversarial checks

| File | Claims | Checked boundary and result |
|---|---:|---|
| `reals/BladeSmoothRank.v` | 24 | Read finite sums, differentiability definition, unique gradient, full epsilon-delta chain rule, factor-rank transfer, moment and squaring lower bounds. Tail coordinates are fixed and reconstruction inputs truncated; no hidden infinite-dimensional escape. Zero dimensions/horizons are guarded by strict dimension inequalities. The lower bounds require differentiable summaries **and reconstructions**, local factorization, and only one witness point. No defect found. |
| `reals/BladeRealCollision.v` | 30 | Read midpoint signs, finite choice/radii, strict IVT endpoints, perturbation degree, Newton coefficient reconstruction, reciprocal positivity, collision reduction. `m>=1` excludes an impossible p0 collision; `m<=N` supports degree counting; d>=2 and r>=1 ensure the collided degree exceeds stored degrees. The infinite positive increasing root sequence is satisfiable, and finite ordered positive data can be extended. No defect found. |
| `reals/BladeRealDensity.v` | 32 | Read separately-polynomial box argument, Newton map surjectivity, simultaneous perturbations, root-sequence extension, summary-variable truncation, and nonzero leading-coefficient bridge. Semantic nonzero witnesses exclude syntactic cancellations. `perturbed_roots_multi` allows r>N but only promises N roots; the coefficient-identification consumer correctly adds r<=N. No defect found. |
| `reals/BladeReduceClassification.v` | 6 | Read parameter freezing, witness interpretation, well-formedness, degree stripping, accepted trace theorem, rejected closure theorem, and special theta=0/theta=1 case. Accepted guarantee is observational simulation; refused guarantee is fixed-qr nonclosure at witness parameters and sufficiently large N. RF-6 is a wording/scope issue. |
| `reals/BladeRoundingBound.v` | 20 | Read summation recurrence, shared condition numerator, two program definitions, triangle bound, and gamma denominator condition. Global relative rounding, exact input coefficients and exact INR(length xs) are hypotheses/model choices; overflow/subnormal behavior and coefficient conversion are not certified. Empty list behaves consistently. Rank-one, one-update scope is stated. No false bound found. |
| `reals/BladeShiftedRounding.v` | 16 | Read exact particle/shift bridge, pow rounding count, multistep bound, mean specialization, gamma bound. k>=1 excludes erroneous zero-order rounding count. Zero exact moments require exact zero seed; coefficients are shared exact inputs; rounded shift/output reconstruction not modeled. RF-1 concerns obtaining the starting premise. |
| `reals/BladeShiftedFeedback.v` | 36 | Read all log-relative primitives, zero/negative branches, coefficient stability, step-radius arithmetic, closed form, exact particle bridge, normalization instance. The nonnegative-radius and r>=1 conditions are adequate. Findings RF-1, RF-2, RF-3. |
| `reals/BladeShiftedVariance.v` | 14 | Read dominant-subtraction inequalities, Lo positivity and upper side, variance rounding counts, nonlinear-radius induction, positivity invariant, nonzero-g preservation, and particle instance. The final radius bounds all previous radii via monotone accumulation, so checking final lmax is justified. Findings RF-1, RF-4, RF-5. |
| `floats/BladeBinary64Witness.v` | 7 | Read literal magnitudes, both two-particle errors, six permutations/two parenthesizations, overflow and NaN witnesses, raw/central variance witness. Twelve original fold trees and 24 listed reduced orderings are nonempty and concretely evaluated. Primitive equality/eqb establishes these finite witnesses, not a general bit-pattern theorem for signed zeros or NaNs. FMA caveat is explicit for the variance example. No defect found. |

## Additional hardening boundaries, not theorem failures

- The real files intentionally depend on standard-library real axioms and are excluded from the axiom-free tower. Primitive float computation is separately trusted. No `Admitted`, custom axiom declaration, or unfinished proof was found in these nine files. Their advertised counts sum correctly: 178 real + 7 float claims.
- A single global `rnd : R -> R` with a relative error bound is an ideal arithmetic abstraction, not a total implementation of binary64. A future bridge should quantify only reachable safe operations or model exceptions, subnormal absolute error, rounding mode, and representability explicitly. Existing headers already exclude underflow/overflow; this is an implementation obligation, not a newly discovered contradiction.
- Exact N/conversion, exact shared input coefficients, integer powers computed by the stated multiplication chain, and noncontracted operation graphs are model choices. Preserve them in emission or prove refinement for an alternative graph. The floating witnesses do not establish compiler/codegen correspondence.
- The moment bounds compare against exact particle dynamics. They do not directly bound the distance to a separately rounded particle simulation, nor the accuracy of mean/central-moment reconstruction through a rounded shift. The plan's proposed particle-twin test at `:407-409` needs a triangle bound incorporating the twin's error, or a sufficiently precise reference.
- The real collision theorems establish global failure of the chosen summary. They do not show failure on every initial state, every parameter value, finite-support data, or every smaller extent. The source comments generally preserve those quantifiers.
- `rcl_pow` has a natural-number exponent. The plan's `|p|` rule for general literal powers needs an explicit domain-restricted extension for negative powers and any intended noninteger powers.

## Independently executed edge probes

Rocq 9.0.1 successfully compiled the following standalone file on 2026-10-15. It does not depend on the Blade tower; all four results reduce by `reflexivity`. `Print Assumptions seed_first_moment_nonzero` reported the primitive float carrier. The temporary artifact is `C:/Users/cdupu/AppData/Local/Temp/blade-audit-reals-floats-20261015/AuditFloatEdges.v`.

```coq
Require Import Floats.
Open Scope float_scope.
Definition bigx : float := 10000000000000000.
Definition mean3 : float := (3 + ((-bigx) + bigx)) / 3.
Definition seeded_m1 : float :=
  (3 - mean3) + (((-bigx) - mean3) + (bigx - mean3)).
Example seed_mean_exact : mean3 = 1.
Proof. reflexivity. Qed.
Example seed_first_moment_nonzero : seeded_m1 = 2.
Proof. reflexivity. Qed.
Example normalized_zero_spread_nan :
  PrimFloat.is_nan ((1 / PrimFloat.sqrt 0) * 0) = true.
Proof. reflexivity. Qed.
Example sqrt_negative_nan :
  PrimFloat.is_nan (PrimFloat.sqrt (-1)) = true.
Proof. reflexivity. Qed.
```

Nine further real-arithmetic probes successfully compiled against the parent's clean-built proof tree, for thirteen additional probes in total. They use the repository's actual `rcl`, `smv`, `vex`, and `Inv_sd`, and confirm the zero-seed obstruction, normalization cancellation, totalized zero denominator, independent-N mismatch, and uncertified-rho issue. `Print Assumptions normalized_second_moment_resets` lists the standard real-number axioms `sig_not_dec`, `sig_forall_dec`, and dependent functional extensionality. The executable source is also saved temporarily as `C:/Users/cdupu/AppData/Local/Temp/blade-audit-reals-floats-20261015/AuditRealEdges.v`.

```coq
From Blade Require Import BladeSummary BladeMomentClosure BladeShiftedMoments.
From BladeReals Require Import BladeShiftedFeedback BladeShiftedVariance.
Require Import Reals Lra List.
Import ListNotations.
Open Scope R_scope.

Example no_relative_radius_for_nonzero_at_zero : forall l, ~ rcl l 2 0.
Proof. intros l [th [H _]]. nra. Qed.

Example exact_seed_first_moment_zero :
  smv 1 [3; -10000000000000000; 10000000000000000] 1%nat = 0.
Proof. cbn [smv smom psum rpow map rsum]. ring. Qed.

Example normalized_second_moment_resets : forall g x, 0 < x ->
  (g / sqrt x)^2 * x = g^2.
Proof.
  intros g x Hx.
  assert (Hs : sqrt x <> 0)
    by (apply Rgt_not_eq; apply sqrt_lt_R0; exact Hx).
  pose proof (sqrt_def x (Rlt_le _ _ Hx)) as Hsq.
  field_simplify.
  - replace (sqrt x ^ 2) with x by (simpl; nra). field. lra.
  - exact Hs.
Qed.

Example zero_normalizing_coefficient_is_zero :
  1 * / sqrt ((smv 0 [0; 0]) 2%nat) = 0.
Proof.
  cbn [smv smom psum rpow map rsum].
  replace (0 - 0) with 0 by ring.
  replace (0 * (0 * 1) + (0 * (0 * 1) + 0)) with 0 by ring.
  rewrite sqrt_0, Rinv_0. ring.
Qed.

Example arbitrary_divisor_is_not_particle_variance :
  vex 1 (smv 0 [-1;1]) = 2 /\ vex 2 (smv 0 [-1;1]) = 1.
Proof. split; unfold vex, smv, smom, psum; simpl; field. Qed.

Example arbitrary_divisor_meets_invariant : Inv_sd 1 0 (smv 0 [-1;1]).
Proof. unfold Inv_sd, smv, smom, psum; simpl. split; nra. Qed.

Example underestimating_m1_has_valid_log_radius : rcl (ln 2) (/2) 1.
Proof.
  exists (/2). split; [ring|].
  rewrite exp_Ropp, exp_ln by lra. split; [lra|]. lra.
Qed.

Example overestimating_m2_has_valid_log_radius : rcl (ln 2) 2 1.
Proof.
  exists 2. split; [ring|].
  rewrite exp_Ropp, exp_ln by lra. split; lra.
Qed.

Example approximate_rho_fails_true_invariant :
  ~ Inv_sd 2 (((/2) * (/2)) / (2 * 2)) (smv 0 [0;1]).
Proof. unfold Inv_sd, smv, smom, psum; simpl. intros [_ H]. nra. Qed.
```

After building the repository proofs, the real probe can be compiled from the repository root with `coqc -Q proofs Blade -Q proofs/reals BladeReals AuditRealEdges.v`. The float probe needs only `coqc AuditFloatEdges.v`.

The parent audit owns the clean full-build, kernel-recheck, and global trust inventory. All source files completed its clean build before these real probes were compiled; consult the parent report for final `coqchk` and assumptions-audit results.
