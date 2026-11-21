# Recurrence and proof-infrastructure audit — 2026-10-15

Target: `claude/pensive-proskuriakova-297cea`, commit `47e7247fab350fe8557029b3534bf40c3333dbe9`.

This tranche covers the twelve recurrence/algebra modules, the proof manifests, counting script, and proof integration in repository CI. The other three audit reports cover the remaining fifty Coq files and both F# enumeration models. Source review focused on theorem statements, definitions and hypotheses, numerical domains, degenerate dimensions, and the critical proof arguments; every proof body was also compiled in a fresh copy. This is a bounded adversarial audit, not a claim that every possible mathematical or implementation issue has been exhausted.

No false theorem was found in these twelve modules. The latest Prouhet improvements survived the review. The main actionable issues concern an unchecked model interface, the meaning of a refusal, and missing continuous verification.

## R01 — P2: the model compiler requires well-formedness but does not enforce it

References: `BladeReduceCompiler.v:446-448` (`wf`), `:515-526` (`classify`), `:588-592` (`compile`), `:799-804` (`compile_certificate`), `:878-884` (`compile_sound`).

Every soundness theorem correctly requires `wf P=true`. The executable `compile` and `classify` definitions accept any `prog` without running that check. An out-of-scope variable can be mistaken for a zero polynomial because the finite test environment fills unspecified coordinates with zero.

Kernel-checked witness:

```text
P = mkprog 1 0 [PVar 1] (PVar 0)
wf P = false
original one-step state, theta(0)=7, xs=[2] = [7]
compiled one-step summary                    = Some [0]
```

This is not a counterexample to `compile_sound`; its premise fails. It is a concrete hazard for extraction, an executable demo, or a future compiler frontend that treats `Some` as a verified acceptance. The file explicitly says it is a research model, so no shipped Blade miscompile is established.

**Hardening:** provide a checked entry point that first validates `wf`, or accept a dependent pair carrying its proof. Keep the internal unchecked function if useful. Pin the malformed-variable witness and zero-rank programs at the public boundary.

## R02 — P3: a refusal of the chosen moment vector is not a refusal of every observational reduction

References: `BladeReduceCompiler.v:437-441,515-526`; `reals/BladeReduceClassification.v:29-33,153-176`; `docs/proofs.md:2110-2111`; `README.md:780`. Also reported as RF-6 in the real/float audit.

The classifier ignores `p_ob`. A well-formed squaring program with a constant-zero observer is refused, while a one-state observation-preserving reduction has an immediate `BladeSummary.certificate`. Both facts were kernel-checked in the same probe below.

The negative theorem correctly says there is no update **of the fixed first r power sums**, for the witness parameters and sufficiently large extent. It does not exclude a different summary, a weaker observer, a restricted invariant set, a special parameter value, or a smaller extent. The detailed source scope is honest; shorter phrases such as “no reduced program exists” need the same qualifier.

**Hardening:** describe the verdict as fixed-summary nonclosure. If observer-specific optimality is intended, connect recognition to `future_equiv`/observation factorization rather than inferring it from failure of an unnecessarily rich summary.

## I01 — P2: no repository CI lane checks the proof tower

The audited `.github/workflows/ci.yml` and `gr-render.yml` contain no Coq/Rocq build, `coqchk`, assumption audit, or theorem-count invocation. The documented local command is useful but does not prevent a change from merging with stale proofs, new axioms, or inaccurate claims. This finding is confined to repository-defined workflows; external checks not represented in the checkout were not inspected.

**Hardening:** add a dedicated lane that builds all three manifests in a clean output area, independently checks their objects, verifies the theorem counts, and compares theorem dependency assumptions with an explicit allowlist. Preserve the distinction between the axiom-free tower, real-number axioms, and primitive-float computation. A green compiler/test lane is not evidence that these proofs were checked.

## I02 — P2: the count check succeeds when a root proof is absent from the manifest

References: `count-theorems.ps1:98-105,151-155`.

The script deliberately detects root-level `.v` files absent from `_CoqProject`, but only calls `Write-Warning`. They are not added to `$problems`, so `-Check` still prints `OK` and returns success. In a temporary copy, adding `AuditOrphan.v` containing a proved `True` theorem reproduced that success. The same run also warned about the audit's generated assumption probes; those too were correctly detected and still did not fail the check.

**Hardening:** make an orphan a failing check, with an explicit exclusion mechanism if standalone probes are intentionally kept there. Validate coverage of the real/float manifests separately; their exclusion from the 1,960 tower count is deliberate and should remain. Also consider a failure for unterminated comments in the counting scanner, although a real compilation gate catches those independently.

## Scope obligations retained, not new theorem bugs

- **Wrapping arithmetic:** `BladeNumericContract.v:39-45` already discloses the C++ `int64_t`/signed-overflow gap. `int64_observation_exact` is a theorem about arithmetic modulo 2^64, not a blanket theorem about current emitted C++. Its coefficient hypotheses at `:314-317` are also essential: coefficient functions must commute with wrapping. Equality tests, ordering, division, or other arbitrary feedback need not. Representability of the final answer alone does not establish these premises.
- **Polynomial versus arbitrary encodings:** `BladeRankBound.factors_through` means an identity in the dual-number interpretation of polynomial syntax. The rank theorems refute that class, not arbitrary discontinuous encodings of Z^N into Z. The real files separately handle differentiable summaries and reconstructions; the Prouhet collision files separately quantify over every update of a specified summary. These are different statements, not interchangeable proofs.
- **Domains matter:** the nonclosure/rank-transfer results use characteristic zero, nonzero products, or faithful integer embeddings. Newton and affine-moment identities need only ring laws, but their negative corollaries need more. Do not carry a characteristic-zero refusal into modular arithmetic; finite characteristic can collapse powers.
- **Thresholds are sufficient, not uniformly sharp:** latest `power_never_closes_2r` uses N>=2^r, `power_never_closes_seeded` uses N>=3*2^(r-2), and `closure_refused_polynomial` uses N>=2^(d*r-1)+degree(A)*(2^r-1). The real theorem uses N>=d*r. None licenses a universal smaller-extent refusal; r=2 already has a separate sharp integer threshold at N=3, and N<=2 closes. The shifted proof improves the bound without asserting optimality.
- **No finite-sample universal inference:** the complete grid test is supported by degree/root-counting and bounded-variable hypotheses. It is not merely a handful of numerical examples. Removing bounded-variable validation causes R01; removing characteristic/domain assumptions from its current proof is unjustified.
- **Zero-stripping and AD:** `compile_sound_unstripped` and `compile_sound_dual` explicitly require that no coefficient was stripped. Hence the current general AD/compiler bridge does not cover even every valid cancellation recognized by the classifier. This is a disclosed coverage gap, not evidence that the cancellation is wrong. A polynomial-normal-form zero theorem valid over every commutative ring would remove that restriction.
- **Derivative semantics:** `BladeReductionAD` is a model of +/* rules, with explicit variable bounds and a stored trajectory. Its transpose and forward/reverse commutation theorems are meaningful. It is not extracted from or mechanically compared with `Grad*.fs`; the universal generated-program result is at dual numbers, and its syntax-level reverse-mode instantiation is one concrete affine program. Guards, intrinsics, packed reconstruction, and machine arithmetic remain outside that bridge.
- **Guards and invariants:** `BladeSummary` correctly carries invariant closure, guard factorization, and budget exhaustion as separate premises/outcomes. A guard reading a discarded coordinate is not covered. Empty admissible sets can make a certificate vacuous, as expected for a conditional theorem; the concrete moment instances admit arrays and the negative examples provide witnesses.
- **Shifted state and observations:** shifted moments plus a tracked shift reconstruct raw moments exactly. The state includes that extra shift, and the exact certificate cannot itself establish a bound for a rounded shift or cancellation in output reconstruction. The separate float report examines these obligations.
- **No executable complexity claim:** the grid search is exponential in variable count and syntactic degree; recursive list evaluators can recompute prefixes. Their finite semantic proofs do not establish production runtime or memory costs.

## Per-file coverage

| File | Claims | Audit focus and result |
|---|---:|---|
| BladeSummary.v | 41 | Certificates, reachable invariant closure, prefix observations, feedback, guarded/frozen/budget outcomes, composition, coupled products, future equivalence, collision/cardinality bounds, topological DAGs, lag windows including k=0. No theorem defect. Observer distinction in R02. |
| BladeMomentClosure.v | 86 | Abstract ring and dual-ring laws, binomial tower, units covariance, fixed-extent certificate, arbitrary summary feedback, polynomial substitution, derivative factorization, positive collisions and sharp SQ N<=2/N>=3 boundary. No theorem defect; ring and encoding scopes are load-bearing. |
| BladeRankBound.v | 49 | Nonzero solution from underdetermined homogeneous equations, row/column factor bounds, root count, moment and squaring rank witnesses, finite-generation obstruction, polynomial pure-power refusal. Strict dimension inequalities cover zero-dimensional degeneracies. No theorem defect. |
| BladeDescartes.v | 28 | Sign-variation induction, exclusion of zero polynomials, distinct positive integer roots, sparse exponents, transpose-kernel transfer, rank at permitted integer points. Positivity/distinctness hypotheses are explicit. No theorem defect. |
| BladeRankDomain.v | 14 | Integral-domain elimination, decidable equality, faithful Z embedding, nonzero scaled inverse, transfer of trivial kernels. No unproved division/field assumption; no theorem defect. |
| BladeProuhet.v | 37 | Recursive collision construction, equal lengths after padding, top coefficients, leading-coefficient nonvanishing, arbitrary-update refusal, pure-power instance. Characteristic zero and extent premises are retained. No theorem defect. |
| BladeNewton.v | 22 | Division-free Newton identities, coefficient differences, Horner root counting, bump indexing/length, ideal-pair implication. Split-root existence is an explicit external obligation supplied in the real files. No theorem defect. |
| BladeProuhetShift.v | 52 | Affine-parameter polynomial argument, lower-moment preservation, nonzero binomial coefficient, block interpolation/descent, polynomial leading-coefficient bridge, seed recurrence and exact lengths, N bounds and concrete seed pins. Latest additions pass; no theorem defect. |
| BladeNumericContract.v | 39 | Quotient ring representation and proof equality, homomorphism transport, signed read range, coefficient congruence, canonical rationals, arbitrary-precision rounding and finite order witnesses. No theorem defect; existing C++ wrap boundary remains. |
| BladeReduceCompiler.v | 54 | Degree/variable bounds, complete finite-grid test, stripping, no-Unknown proof, generated equations, environments, tailzero, conditional compilation/refusal witnesses. R01 and R02; no violation of stated soundness theorem. |
| BladeReductionAD.v | 24 | Tangent/adjoint rules, accumulation, scoped fresh assignments, reverse final-store reading, trajectory sweep, dual certificate, componentwise transpose, generated affine example. No theorem defect; universal cancellation/implementation bridge remains incomplete as disclosed. |
| BladeShiftedMoments.v | 21 | Both binomial transforms, exact mean invariant, division-free variance and rho identities, ghost shift, certificate, arbitrary initial shift. No theorem defect; real/float initialization and domain obligations are separate. |

## Durable counterexample probe

The following was compiled successfully by Rocq 9.0.1 against a clean build of the audited branch. All three `Print Assumptions` results were `Closed under the global context`. Save it outside the manifested proof directories and run `coqc -Q <compiled-proof-directory> Blade AuditRecurrence.v`.

```coq
From Blade Require Import BladeSummary BladeMomentClosure BladeReduceCompiler.
Require Import List ZArith.
Import ListNotations.
Open Scope Z_scope.
Definition observer_constant : prog :=
 mkprog 1 0 [PConst 0; PConst 0; PConst 1] (PConst 0).
Example constant_observer_refused :
 wf observer_constant = true /\
 classify observer_constant = Refused 2 (PConst 1) [0].
Proof. vm_compute. split; reflexivity. Qed.
Example constant_observer_has_reduction :
 certificate (sstep Z 0 1 Z.add Z.mul Z.opp observer_constant (fun _ => 0))
 (fun _ => tt)
 (sobs Z 0 1 Z.add Z.mul Z.opp observer_constant (fun _ => 0))
 (fun _ => True) (fun (_ : unit) (_ : unit) => tt) (fun _ => 0).
Proof. constructor; intros; [exact I | reflexivity | reflexivity]. Qed.
Definition escaped_variable : prog := mkprog 1 0 [PVar 1] (PVar 0).
Definition bad_reduced_run : option (list Z) :=
 option_map (fun gs => rstep Z 0 1 Z.add Z.mul Z.opp gs 1 0 1
 (fun _ => 7) tt [2]) (compile escaped_variable).
Example compile_does_not_validate_wf :
 wf escaped_variable = false /\
 sstep Z 0 1 Z.add Z.mul Z.opp escaped_variable (fun _ => 7) tt [2] = [7] /\
 bad_reduced_run = Some [0].
Proof. vm_compute. repeat split; reflexivity. Qed.
Print Assumptions constant_observer_refused.
Print Assumptions constant_observer_has_reduction.
Print Assumptions compile_does_not_validate_wf.
```
