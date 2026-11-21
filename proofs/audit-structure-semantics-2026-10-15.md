# Structure and semantics proof audit — 2026-10-15

Audit target: branch `claude/pensive-proskuriakova-297cea`, commit `47e7247fab350fe8557029b3534bf40c3333dbe9`.

Scope: the 17 Rocq modules listed in the coverage table below, both F# enumeration/model scripts, and their correspondence claims. Definitions, theorem hypotheses, proof arguments, negative examples, and finite checks were examined. No proof or compiler source was changed. Graft contained no indexed proof symbols; direct source inspection was necessary.

## Result and validation

No inconsistent or invalid kernel-checked theorem was found. Three substantive issues were confirmed: an unrestricted pipeline can destroy the approximate quotient relation, the layout cost helper clips answers at 99, and the recurrence agreement theorem's premise excludes its own illustrative recurrence. The pipeline and recurrence findings concern what the formalization covers; they do not refute the existing, more narrowly stated theorems. None establishes a shipped compiler miscompile.

All four counterexample results were compiled against the clean copied proof tower using Rocq 9.0. The complete probe is preserved below. `Print Assumptions` reports **Closed under the global context** for each. The coordinating audit supplies the whole-project clean-build and trust results.

Executable checks completed:

| Check | Result |
|---|---|
| `dotnet fsi proofs/OrbitEnum.fsx --stress` | 81 passed, 0 failed; includes the normal suite and all-8! stabilizer stress checks. |
| `dotnet fsi proofs/OrbitDeduceModel.fsx` | 26 cases passed; ALL PASS. |
| `AuditStructure.v` reproduced below | Checked successfully; no added axioms. |
| Oversize brute enumeration: `orbitCountBrute [(64,Plus)] 2` | Rejects with arithmetic overflow; it does not silently wrap to an empty domain. Closed-form `foldCellsChecked [(64,Plus)] 2L` returns `Ok 65L`. |

Severity measures the importance of repairing the proof contract: **P2** is a substantive model defect or missing guarantee behind an advertised use; **P3** is a documentation or assurance-boundary correction.

## Findings

### S01 — P2: approximate quotient typing is not closed under its own unrestricted pipelines

**Locations:** `proofs/BladeLicence.v:371-376` (`TR_PipeC`, `TR_PipeA`); compare `eval_sound_R` at 550-552 and `typed_app_is_quotient_R` at 573-587. The broad status description appears at `docs/proofs.md:2515`.

The relaxed type system preserves a computation's compact shape through `EPipe c g` for an arbitrary `g : nat -> nat`. If the compact computation agrees with its dense form only up to relation R, an arbitrary g need not preserve that relation. Shape-preserving pipeline typing therefore does not imply a compositional quotient guarantee.

**Machine-checked witness:** use the existing parity relation `Rmod2` and existing kernel `kmod2`, which computes `x + 3*y`. Let A be the length-two identity array and apply the kernel to A,A. At dense tuple `[1;0]`, the canonicalized read is 3 and the dense read is 1: these have the same parity, as promised. Pipe the result through integer division by 2. The resulting expression still has type `TComp [mkIx 2 0 2]`, but its canonicalized and dense reads are now 1 and 0, which are not R-related. The probe also proves `env_ok`, so this does not rely on a malformed environment.

**What remains sound:** `typed_app_is_quotient_R` explicitly covers a direct method application, not a surrounding pipeline. `eval_sound_R` proves evaluation to the declared denotation; it does not compare that denotation's canonical and dense reads. Neither theorem is contradicted. Exact equality pipelines in `BladeSurface` also remain sound, because every function preserves equality.

**Recommendation:** require `forall x y, R x y -> R (g x) (g y)` for a shape-preserving pipeline, or track a distinct output relation justified by g. Then prove a quotient theorem over the full typed expression grammar. Until then, label the R quotient theorem as direct-application-only and retain this negative example. This matters for any future interpretation of a numerical license as an approximation relation: downstream nonlinear processing can expose the difference.

### S02 — P2: `best_cost` silently caps a genuine minimum at 99

**Locations:** `proofs/BladeLayout.v:1553-1555`; contrast the independent `canon_sched` definition at 1628-1633 and its actual minimality theorem at 1646-1648.

`best_cost` folds `Nat.min` with seed 99. Thus it computes the minimum of the schedule costs **and 99**, rather than the minimum schedule cost. There is no hypothesis bounding all uses below 100.

**Machine-checked witness:** take dimension 2, the trivial license group, and `concat (repeat tp_refs 100)`. Each pair of transpose references contributes one unit under either loop order. `best_cost` evaluates to 99, while `sched_cost (canon_sched ...)` evaluates to 100. The canonical scheduler still chooses a real minimum; only the helper's reported cost is wrong.

**Recommendation:** define the minimum from the first candidate, return an option for an empty candidate space, or use a proved upper bound as the fold seed. Add an agreement theorem between `best_cost` and `sched_cost (canon_sched ...)` for a nonempty space. Existing small finite examples cannot reveal this threshold bug.

### S03 — P2: recurrence agreement excludes the module's own reversed-prefix example

**Locations:** `proofs/BladeInduction.v:678-679` (`orb_inv`), 742-744 (`dense_agrees_with_quotient`), 768-779 (`letrec_commutes_with_quotient`), and 1345-1361 (`sym_step` and its concrete pins). Related coverage summary: `docs/proofs.md:2513`.

`orb_inv cs` asks for equal results on orbit-equivalent tuples for **every arbitrary prefix reader** rd. A natural recurrence that reads its previous slice at the current or reversed coordinate preserves symmetry when that previous slice is symmetric, but need not be invariant for a reader that itself breaks symmetry. The premise is therefore substantially stronger than preservation of the recurrence invariant.

**Machine-checked witness:** `~ orb_inv [mkIx 2 0 3] sym_step`. At n=1, tuples `[0;1]` and `[1;0]` have the same canonical form. With `rd k c = hd 0 c`, the file's `sym_step` returns 2 at the first tuple and 1 at the second. Its dense/quotient example pins nevertheless both return 5 at the selected coordinates, because the actual generated prefix has the required symmetry.

Independent review exposed a second restriction in the companion premise: `rd_dense_ext` at 682-684 quantifies over every input tuple, not just dense tuples. The probe also proves `~ rd_dense_ext [mkIx 2 0 3] sym_step`: at the malformed tuple `[]`, two readers that agree on every valid two-coordinate tuple can disagree on the empty tuple, and the step observes that disagreement. This is another reason the agreement theorem cannot be instantiated with the illustrative step.

**Impact:** the general agreement theorem cannot be applied to this very example, nor to the common pointwise prior-slice recurrence without extra canonicalization in the step definition. The checked concrete pins do not close that universal proof gap. This is an overly narrow hypothesis, not a false theorem, and the documentation does state a conditional orbit-invariance boundary.

**Recommendation:** formulate invariant preservation under an orbit-invariant reader, restrict the obligations to valid dense input tuples, prove that each generated prefix supplies such a reader, and induct on the recurrence length. Alternatively formulate equivariance under a simultaneous action on the coordinate and reader. Prove the general dense/quotient agreement for `sym_step` as the regression target, beyond its fixed-size pins.

### S04 — P3: the F# deduction script tests a standalone model, not the compiler AST

**Locations:** `proofs/OrbitDeduceModel.fsx:1-15` claims AST robustness and testing “THROUGH the AST”; line 23 loads only the enumeration model.

The cases implement standalone tensor constructions and deduction recipes and compare their finite stabilizers. They do not construct the Blade compiler AST, invoke the parser/typechecker, or compare inferred compiler output types. Named compiler seams in comments are not an executable bridge, and some refer to older monolithic source files.

**Impact:** all 26 cases can stay green while the actual AST traversal or compiler rule changes incorrectly. Their mathematical model checks remain useful and passed; calling them AST integration tests overstates the evidence.

**Recommendation:** describe this file as the deduction-model oracle. Pair its important cases with actual Blade corpus tests or a differential harness that invokes the compiler and compares the inferred class. Keep compiler-symbol references current when those seams move.

## Other dark corners and assurance boundaries

- **Abstract optimality versus actual compiler cost:** `BladeOptimal`, `BladeOrbitWork`, `BladeObject`, and `BladeAttain` establish strong oracle/query or one-cell-per-value storage results under explicit freedom, injectivity, or perturbability assumptions. They do not establish a lower bound for arbitrary compressed encodings, shared arithmetic subexpressions, machine arithmetic, cache effects, or elapsed time. Free-oracle and injective-data witnesses make the abstract bounds nonvacuous. The “every input” versus “worst case” distinction matters.
- **Finite and general results:** `BladeWreath` combines general soundness with explicit finite exactness witnesses, not a general factorial-order proof. `BladeLayout` has exhaustive low-rank classifications plus selected higher-rank examples. `BladePointGroup` certifies the frozen C4/D4 tables and associated matrix calculations; it is not a theorem about every future registry entry, and the abstract End/Schur completeness link remains cited mathematics. These limits are generally acknowledged in the sources.
- **Dimensions versus semantic representation bridges:** the SymPower sector-count identities and the partition witness triangularity are useful exact combinatorics. They do not by themselves formalize every Clebsch-Gordan construction or every implementation linear-algebra bridge. `BladeRepStatus` proves algebraic closure under its stated representation hypotheses; it is not an induction over the entire F# expression grammar.
- **Deduction history was checked, not reported as a defect:** `BladeDeduceExact.v:40-54` explicitly separates the pre-2026-09-19 par rule from the current `parfix` behavior. The current F# binary deduction fallback in `src/Deduce.fs:719` agrees with that explanation. The historical witness is intentional. Exactness is relative to the representable contiguous Young-block fragment and free-term model; nonadjacent/wreath symmetry omitted by that representation is expressly illustrated.
- **Transport scope:** `BladeTransport` explicitly records the distinction between value-equivalent stages and independently labelled oracle work, and gives negative examples for treating typed compound work as globally optimal. Its meet/intersection and stage-sharing results should not be promoted to a recursive global optimizer theorem. The actual two-node semantic bridge is present and was inspected.
- **Rank zero, empty domains, invalid tuples:** enumeration/cardinality and sparse zero handling were followed through the assigned modules. Dense membership and well-shaped tuple premises are materially necessary; out-of-domain defaults are not licensed array accesses. No extra missing-precondition counterexample was found here. In particular, the guarded recurrence's zero/singleton budget behavior agrees with the compiler's stop-sentinel/abort construction in `src/TypeCheckInfer.fs:13551-13648`.
- **Numerical contracts:** `BladeLicence` deliberately models a parameter relation and an integer rounding example, not IEEE-754 itself. Its fold results state congruence and algebraic premises. S01 is the separate missing congruence at the general pipeline constructors. No inference from these models to NaN, signed zero, hardware overflow, or generated floating-point behavior is warranted without a bridge.
- **Converse claims require caution:** the layout fold examples show why certain algebraic hypotheses cannot simply be discarded in general; they do not prove each is necessary for every operation. For example a right-projection operation always leaves a right fold at its terminal value, despite not being commutative. `BladeBoundary` correctly leaves the exact converse characterization of attaining separable-query bounds open.
- **Trust inspection:** no admitted proof, axiom declaration, unchecked cast, or disabled kernel guard/positivity/universe check was found in the 17 assigned modules. Exported semantic premises are explicit hypotheses. Whole-tower assumption and build validation is handled by the coordinating report.

## Per-file coverage

| File | Inspected contracts and outcome |
|---|---|
| BladeSymPower.v | S2 block counts, diagonal/off-diagonal cases, symmetric and exterior composition sectors, Vandermonde identities; no new defect. Representation construction remains a separate bridge. |
| BladePartition.v | RGS enumeration, soundness/completeness/uniqueness/order, Bell/Stirling counts, refinement and unitriangular witness; no new defect. |
| BladePointGroup.v | C4/D4 closure, matrix dimensions/products, indicators, J identities and weighted Hom counts; no new defect in fixed tables. |
| BladeWordClosure.v | Arbitrary word closure, generating actions, concrete C4/D4 action links; no new defect. |
| BladeRepStatus.v | Invariant and equivariant algebraic closure, addition/negation and invariant branch condition; no new defect in the formal statements, no compiler AST induction. |
| BladeWreath.v | Nested sign/canonicalization laws, repeated-block symmetry, finite exact stabilizers and degeneracy witnesses; no new defect, general completeness explicitly limited. |
| BladeLayout.v | Sign classification, licensed reference choices, cost/tie-break minimization, folds and negative controls; S02. |
| BladeOptimal.v | Complete finite permutations, canonical oracle representatives, perturbation lower bound, one-cell opaque storage and arbitrary-carrier bridge; no new defect. |
| BladeOrbitWork.v | Programs/traces/memoization, oracle-freedom necessity, explicit nonvacuity witness, class-work lower bound; no new defect. |
| BladeDeduceExact.v | Free commutative binary terms, mirrors/parities, adjacent licensing, old/new par witnesses and representation-limited exactness; no new defect. |
| BladeObject.v | Valid shape enumeration, rank/canonical membership, canonical query program, attained trace bound and dense-read equality; no new defect. |
| BladeSurface.v | Small typed grammar, preservation/normalization, direct-application quotient theorem and rank-1 compact shape constraints; no new defect. |
| BladeTransport.v | Boundary composition, run splitting, class meets, fused/paired queries, stage labels, actual application bridge and explicit optimality counterexamples; no new defect beyond declared limits. |
| BladeAttain.v | Proper subgroup bounds, generalized meet conditions, attainable memo programs, signed/zero orbits, lower bounds and compiler-shaped near misses; no new defect. |
| BladeInduction.v | Prefix legality/default zero, dense/canonical agreement, guards/freeze/budget, read dependence and concrete trajectories; S03. |
| BladeLicence.v | R-based typing, direct application, relation examples, numerics/fold licences, exact-vs-relaxed refusal; S01. |
| BladeBoundary.v | Cell-linear lower bounds, separable oracle model, support/cover arguments, constructive achievement and opaque-storage distinction; no new defect. |
| OrbitEnum.fsx | Canonicalization, signs/zero sets, checked cardinality, brute scan guard, generic stabilizers, degenerate operations and stress checks; 81 passes, oversize rejection confirmed. |
| OrbitDeduceModel.fsx | Unary parity, outer/zip/kernel/reduction/choice/Reynolds model recipes, finite stabilizer adversaries and refusal cases; 26 passes, S04. |

## Reproducible Rocq probe

Save this as `AuditStructure.v` outside the source proof directory. After compiling the audited project, run `coqc -Q <compiled-proof-directory> Blade AuditStructure.v`. This complete source was checked successfully against the clean copied tower with no new axioms.

```coq
From Blade Require Import BladeDMWF BladeCompute BladeShape BladeOptimal BladeObject BladeSurface BladeLicence BladeLayout BladeInduction.
Require Import List Arith Lia.
Import ListNotations.

Definition audit_shape : Shape := [mkIx 1 0 2].
Definition audit_G : BladeSurface.tenv := [(0, audit_shape)].
Definition audit_rho : BladeSurface.venv := [(0, (audit_shape, fun t => hd 0 t))].
Definition audit_app := EAppM (EMethod [0;0]) kmod2.
Definition audit_pipe := EPipe audit_app (fun x => x / 2).

Example audit_pipe_types : has_ty_R Rmod2 audit_G audit_pipe (TComp [mkIx 2 0 2]).
Proof.
  apply TR_PipeC.
  change (has_ty_R Rmod2 audit_G audit_app
    (TComp (out_shape (k_comm kmod2) [(0,audit_shape);(0,audit_shape)]))).
  apply TR_AppM.
  - apply TR_Method. reflexivity.
  - apply kmod2_kwf_R.
  - reflexivity.
Qed.
Example audit_env_valid : env_ok audit_G audit_rho.
Proof.
  intros x s H. unfold audit_G, lookup in H.
  destruct (x =? 0) eqn:E; try discriminate.
  inversion H; subst. exists (fun t => hd 0 t).
  unfold audit_rho, lookup. now rewrite E.
Qed.
Example audit_base_licensed :
  qread (den audit_rho audit_app) [1;0] = 3 /\
  snd (den audit_rho audit_app) [1;0] = 1 /\ Rmod2 3 1.
Proof. repeat split; vm_compute; reflexivity. Qed.
Example audit_pipeline_breaks_R :
  qread (den audit_rho audit_pipe) [1;0] = 1 /\
  snd (den audit_rho audit_pipe) [1;0] = 0 /\
  ~ Rmod2 (qread (den audit_rho audit_pipe) [1;0])
            (snd (den audit_rho audit_pipe) [1;0]).
Proof. repeat split; vm_compute; congruence. Qed.

Definition audit_refs := concat (repeat tp_refs 100).
Example audit_cost_sentinel :
  best_cost 2 (trivG 2) audit_refs = 99 /\
  sched_cost (canon_sched 2 (trivG 2) audit_refs) = 100.
Proof. split; vm_compute; reflexivity. Qed.

Example audit_sym_step_not_orb_inv : ~ orb_inv [mkIx 2 0 3] sym_step.
Proof.
  intro H.
  specialize (H 1 [0;1] [1;0] (fun _ c => hd 0 c)).
  vm_compute in H. specialize (H eq_refl). discriminate.
Qed.
Print Assumptions audit_pipeline_breaks_R.
Print Assumptions audit_cost_sentinel.
Print Assumptions audit_sym_step_not_orb_inv.

Example audit_sym_step_not_rd_dense_ext : ~ rd_dense_ext [mkIx 2 0 3] sym_step.
Proof.
  intro H.
  specialize (H 1 [] (fun _ _ => 0) (fun _ c => match c with [] => 1 | _ => 0 end)).
  assert (Heq : forall (k : BinNums.Z) (u : list nat), shape_dense [mkIx 2 0 3] u ->
    0 = match u with [] => 1 | _ => 0 end).
  { intros k u Hu. destruct u;
      [unfold shape_dense, dense_tuple in Hu; simpl in Hu; intuition discriminate | reflexivity]. }
  specialize (H Heq). vm_compute in H. discriminate.
Qed.
Print Assumptions audit_sym_step_not_rd_dense_ext.
```

## Independent challenge

The foundational tranche's strongest two findings were independently reviewed: the `BladeDichotomy` access recipe uses the opposite product order from the proved theorem, and `BladeJacobian` proves channel-exchanging equivariance rather than invariance of each scalar partial. Both findings survive review. The former has a valid noncommuting S3 witness; the latter does not preclude compression with an explicit channel action. Their detailed probes and recommendations are in `audit-foundations-2026-10-15.md`.

The foundational auditor independently challenged S01-S03 and confirmed the witnesses and their scoped interpretation. Their further observation about `rd_dense_ext` was then proved in the additional probe above.
