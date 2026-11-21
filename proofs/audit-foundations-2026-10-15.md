# Foundational proof audit — 2026-10-15

Audit target: branch `claude/pensive-proskuriakova-297cea`, commit `47e7247fab350fe8557029b3534bf40c3333dbe9`.
Scope: all 24 entries from `BladeCore.v` through `BladeJacobian.v` in `proofs/_CoqProject`, including definitions, hypotheses, theorem statements, proof bodies, caveats, and the corresponding portions of `docs/proofs.md`. No proof or compiler sources were modified.

## Result

No contradiction or invalid kernel-checked theorem was found in this tranche. The important findings are incorrect implementation recipes and prose that claims more than the proved statement. The strongest examples have been checked as counterexamples in Rocq 9 against the audited modules; the complete reproducible probe is preserved below. Global clean-build and assumption checks are reported by the coordinating audit.

Severity here measures the importance of correcting the proof contract: **P2** means a substantive incorrect claim or an implementation recipe that would produce wrong results if followed; **P3** means narrower accounting/documentation cleanup. None of these findings alone establishes a shipped compiler defect.

## Findings

### F01 — P2: the six-component access recipe reverses noncommuting factors

**Locations:** `proofs/BladeDichotomy.v:381-382`; compare the checked theorem at `proofs/BladeDichotomy.v:415-419` and the multiplication convention at `proofs/BladeDichotomy.v:81-83`.

The comment instructs the reader to access component `pmul (pinv s) t`. The theorem proves access through `pmul t (pinv s)`. These are unequal in S3, so this is not harmless notation.

**Machine-checked witness:** canonical `a=b=[0;1;2]`, `s=[1;0;2]`, `t=[1;2;0]`. Let T be the indicator that `paircodes3 x=[2;4;6]`. Exhaustive computation certifies `diag_symb 3 T=true`. The requested tensor value is 1; the comment's recipe returns 0; the theorem's recipe returns 1. The three pair-code lists are respectively `[2;4;6]`, `[0;5;7]`, `[2;4;6]`.

**Recommendation:** correct the recipe to match the theorem and retain the noncommuting witness as an explicit example next to it. A transposition-only or identity example would not expose this defect.

### F02 — P2: Jacobian equivariance is overstated as ordinary triangular derivative/cotangent storage

**Locations:** `proofs/BladeJacobian.v:456-466`; `docs/proofs.md:596-604`. The correctly qualified tangent theorem is at `proofs/BladeJacobian.v:365-370`; the accumulation theorem is at `proofs/BladeJacobian.v:674-677`.

`jacobian_transfer_semantic` exchanges **both** the inputs and the derivative channel: the a-partial at swapped inputs equals the b-partial at original inputs. It does not establish that either partial individually is a symmetric scalar array. Nevertheless, the accompanying text says the derivative/cotangent field is itself symmetric and ordinary canonical access makes triangular derivative storage lossless.

**Machine-checked witness:** the structurally symmetric kernel `e=x*y` has `d_x e=y`. At `(x,y)=(2,3)` the x-partial is 3; after swapping inputs it is 2. Ordinary `access_exact` for that scalar field cannot apply. A paired representation can be compressed if reconstruction also exchanges its channels; that extra action must be stated and proved. An arbitrary incoming cotangent need not be symmetric either. The existing T3 theorem correctly permits unequal `cot(p,q)` and `cot(q,p)` and sums them, which is stronger and more precise than an unconditional x2 shortcut.

**Recommendation:** describe the derivative result as equivariance, specify a channel-aware canonical read, and prove its round trip. Separately distinguish a symmetric scalar tangent field, an equivariant Jacobian, and the pullback of an arbitrary dense cotangent. Check any compiler storage bridge against these three separate contracts; no compiler defect was established by this source-only finding.

### F03 — P2: the advertised arrow dependence biconditional is false for the actual state model

**Locations:** `proofs/BladeArrow.v:12-14`, `proofs/BladeArrow.v:104-115`; `docs/proofs.md:269-272`. Related reasoning appears in `proofs/BladeCompound.v:187-191`.

`const_state_rectilinear` proves that `step s i=s` implies a rectangular domain. The header and documentation advertise the converse too. No minimality or observational-faithfulness constraint is imposed on the state, so that converse is false.

**Machine-checked witness at every rank:** use state `nat`, `heads s=[0;1]`, and `step s i=S s`. Every tuple of length r over `{0,1}` is admitted, independently in every coordinate, yet the state always changes. More generally, a state may change only a depth counter while representing rectangular factors of different extents. Nonconstant representation state is not the same thing as dependence between coordinates.

**Recommendation:** change the current claim to the proved implication. If a true converse is desired, define residual domains up to observational equivalence, distinguish depth-dependent factors from value-dependent residuals, and formulate the theorem in terms of those denotations. The non-factorability theorem for a genuine symmetric domain (`sym_arrow_not_factorable`) remains sound.

### F04 — P2: nonconstant output types do not imply that no single type can host all arities

**Locations:** `proofs/BladeTrinity.v:18-23`, `proofs/BladeTrinity.v:129-166`; `docs/proofs.md:336-339`. Related prose: `proofs/BladeDMWF.v:313-317` and `proofs/BladeTrinityAsym.v:18-21`.

The checked result is `OutT nat 0 <> OutT nat 1`. It does not rule out a sum, tagged tree, existential package, or universal representation containing all those types. The claim that 'no single non-dependent type hosts all arities' is therefore stronger than the theorem and false without additional representation restrictions.

**Machine-checked construction:** the unindexed type `Host := HScalar nat | HFun (nat -> Host)` admits a recursively defined embedding `OutT nat r -> Host` for every r. It sacrifices the precise static API unless additional checks are carried, which may be a valid design reason to prefer indexed types; it is still a single host type. Similarly, varying residual cardinalities alone do not prohibit a uniform representation containing a bound and data.

**Recommendation:** say the precise, unboxed/uncast output API is a nonconstant family in the chosen encoding, and identify which typing, erasure, and checking restrictions the design-space necessity argument assumes. The existing honesty ledger correctly labels global language-necessity claims as prose; the local explanatory sentences should be equally careful.

### F05 — P2: 'CompoundIdx denotation at every rank' excludes an observable rank-zero exception

**Locations:** `proofs/BladeCompound.v:15-24`, `proofs/BladeCompound.v:136-140`; `docs/proofs.md:299-303`, `docs/proofs.md:2499`.

The theorem deliberately quantifies over `n :: dims`, so the exact denotation result covers positive rank only. At rank zero, `enumA ... 0 ([],[])=[[]]` even for the everywhere-false mask. This is documented for `compoundk_sound`, but the denotation summary and status table still say every rank / FULL.

**Machine-checked witness:** the false-mask rank-zero enumeration is `[[]]`, while the desired set of true mask cells is empty.

**Recommendation:** consistently say 'every positive rank', or supply a separate rank-zero wrapper that tests `M []`, with a unified denotation theorem. If Blade rejects rank-zero CompoundIdx, document that rejection as the bridge premise rather than silently broadening the theorem.

### F06 — P3: finite Reynolds accounting is presented as an exact factorial saving

**Location:** `proofs/BladeCore.v:183-187`.

The informal note writes the exact cell count as `n^(r*d)/(r!)^d`. Diagonals have smaller orbits; the exact count is `C(n+r-1,r)^d` in the equal-extent setting already proved elsewhere in this tower. For r=2, d=2, n=2 the product store has 9 cells, not 4, and evaluating both terms of the displayed Reynolds expression costs 18 kernel evaluations, not 8. Reuse at ties may reduce work, but the note does not define such an algorithm.

**Recommendation:** mark these as leading-order asymptotics for fixed r,d and growing n, or provide exact binomial counts and a tie-aware cost model. Do not cite the earlier arithmetic inequality as checking this separate work estimate.

### F07 — P3: antisymmetric diagonal vanishing needs more than a group

**Location:** `proofs/BladeLowering.v:278-282`.

The comment says `x=-x` makes the diagonal vanish 'over a group'. This needs the absence of nonzero 2-torsion. In the Boolean XOR group, every element is its own inverse, including nonzero `true`. The actual theorem assumes an arbitrary `neg` and correctly concludes only anti-invariance; the stronger prose is not its consequence.

**Recommendation:** require `x=-x -> x=0` explicitly, or name the intended characteristic-zero torsion-free carriers. This matters when carrying these abstractions into modular arithmetic. The Z-specific diagonal proofs in `BladeCauchy` do satisfy the needed property.

## Boundaries and dark corners checked

These are scope/strength limitations, not additional theorem defects:

- **Zero rank and empty extents:** `enum 0 l u=[[]]` and cardinality 1, even for `u<=l`; positive-rank impossible ranges enumerate nothing. The symmetric/strict/affine bijections account for this with explicit rank-zero disjuncts. Mixed-radix proofs avoid division by zero by deriving a positive remaining cardinality from membership or a valid offset. No missing-precondition defect found.
- **Natural subtraction:** `lj`, `alj`, and affine transforms are inverted only under their canonical-domain predicates; invalid tuples can collapse due to truncated subtraction, but the theorems never promise otherwise.
- **Counting versus arbitrary encodings:** `counting_general_C` proves a strict inequality of address counts. It rules out an injective one-cell-per-independent-value layout, not arbitrary encodings into unbounded naturals. `BladeDichotomy` supplies a carefully restricted linear-decoder impossibility even with arbitrary encoding. Keep these two claims distinct.
- **Dichotomy sharpness:** the checked lower bound excludes width 2 and the upper bound supplies width 6. This does not itself exclude widths 3,4,5. General minimal-width r! is identified as prose in `docs/proofs.md:509-515`. Cell counts are computed at (2,2) and (3,3), not universally quantified over extents. Nor is the tie-compressed double-coset store connected by a general reconstruction theorem to `six_component_access`; the latter is the full six-slot representation. The upper bound itself is sound, including ties.
- **Declared versus inferred kernel symmetry:** lowering assumes semantic invariance; deduction files prove algebraic rules. No theorem in these first 24 modules connects F# AST acceptance to the Coq hypotheses. `detects` in `BladeCurryingGeneral` is definability by a Prop predicate, not an executable decision procedure for arbitrary `nat -> nat -> nat` kernels. Its two-way information theorem is appropriately scoped to a full-support array property and a nonconstant kernel property.
- **Finite index extents:** completeness witnesses use unbounded nat indices/free data. They do not prove a largest necessary license for each fixed degenerate extent (e.g. one available index value), although a conservative compiler may intentionally ignore such accidental extra symmetry.
- **Numeric interpretation:** `BladeDeduce` explicitly assumes commutativity/associativity/sign laws; `BladeJacobian` evaluates in the nat semiring and uses association/distribution in ACEq. These establish formal algebraic symmetry, not bitwise IEEE-754 symmetry, signed-zero/NaN behavior, overflow safety, or a derivative's analytic correctness. The latter is explicitly disclaimed and supported by a good negative control. Any machine-number bridge needs its own contract.
- **Storage arithmetic:** `BladeSafety` is a substantive improvement over total-function tautologies: a failing lookup exists, canonical bounds exclude it, and a closed triangular offset is checked. It is still rank 2 over unbounded naturals. General-rank generated binomial offsets, machine-size overflow, allocator capacity, and emitted-code correspondence are not established here.
- **Plan noninjectivity:** `BladeCompute.veval_not_injective` distinguishes its two plans only at out-of-domain tuple `[1]`; their sole valid tuple is `[0]`. It proves noninjectivity of this total-function representation, not that two different operational computations of valid cells were modeled. Cost, side effects, failure, and shape-preserving materialization are outside these list-value semantics. The buffer-elimination result is correctly a pure semantic equality, not a measured allocation theorem.
- **AD freshness and multiplicity:** freshness of seed variables is explicit; allowing equal a/b or equal fresh seed names does not invalidate the stated symmetry result. T3 covers arbitrary unequal incoming cotangents and handles diagonal/off-diagonal disjointness correctly. General rank, analytic differentiation, negative/real cotangents, and the channel-aware storage bridge remain separate work.
- **Trust scan:** no `Admitted`, axiom declarations, unchecked cast commands, aborted proofs, or disabled guard/positivity/universe checks were found in the assigned modules. Matches for the English word 'admit' were comments only. Local theorem hypotheses are exported premises, not silently introduced global axioms.

## Per-file coverage

| File | Audit coverage and outcome |
|---|---|
| BladeCore.v | Group law, product counterexample, count inequality, Reynolds access, r2/r3 left justification, legacy totality signpost, versioning arithmetic; F06 and broad-scope caveats above. |
| BladeDMWF.v | Enumeration soundness/completeness/uniqueness, cardinality recursion, both storage transforms, relationality, shape products; no theorem defect, representation-necessity prose covered by F04. |
| BladeBinomial.v | Pascal recursion, hockey-stick identity, zero-rank/empty-range cases; no finding. |
| BladeCounting.v | Injection, explicit missing witness, pigeonhole proof, dimension induction, binomial conversion; no theorem defect, arbitrary-encoding boundary recorded. |
| BladeMixedRadix.v | Full rank/unrank pair, membership, range, inverses, injectivity, zero cardinalities and zero-width groups; no finding. |
| BladeCurrying.v | Rectangular factorization, non-factorability, identity/comm information witnesses, two canonical specs; no theorem defect. |
| BladeCurryingGeneral.v | Observation respect, full support/locality, property characterization, wastelessness, positive witnesses; no theorem defect, definability/decidability boundary recorded. |
| BladeLowering.v | H-and-Stab transport, closure, binary/diagonal/sign variants, negative controls; F07. |
| BladeArrow.v | Coalgebra enumeration, constant-state implication, symmetric/strict/masked instances and strict storage inverse; F03. |
| BladeAffine.v | General delta feedback, both transforms, delta0/delta1 corollaries, rank-zero behavior; no finding. |
| BladeCompound.v | Completion witness, positive-rank soundness, all-rank completeness, uniqueness, denotation, rank2 subsumption; F05 and F03-related prose. |
| BladeShape.v | Fixed tuple widths, unique concatenation decomposition, duplicate-free product; no finding. |
| BladeLex.v | StronglySorted composition, all arrow instances, nth-order consequence; no theorem defect, generated-code order bridge is informal. |
| BladeTrinity.v | Applicative product, multiplicative size, fold closure, Cantor argument, output-family inequality; F04. |
| BladeTrinityAsym.v | Semantic monoid laws, arbitrary-arity fold, unary decomposition, monoid homomorphism; no theorem defect; F04-related claims and deliberately unproved adjunction noted. |
| BladeCompleteness.v | Constructive finite permutation pairing, sum invariance, stabilizer adversary, kernel adversary, fsum exactness; no theorem defect, degenerate-extent boundary recorded. |
| BladeFusionDuality.v | Fusion identity, full identity to stabilizer bridge, binary joint license, scoped duality corollary; no theorem defect. Header's r+1 candidate count is only suggestive: Spec allows arbitrary subsets of positions, not merely one ordered prefix chain. |
| BladeCompute.v | Value/plan round trip, noninjectivity witness, shape widths, fused products, pipeline laws, wrap/unwrap, commutation; no theorem defect, weak noninjectivity witness and pure-value scope recorded. |
| BladeDeduce.v | Mirror/sign/chain rules, adjacent generation, AC pack folds, signed exchange refusal; no theorem defect, algebraic hypotheses require a numeric interpretation. |
| BladeCauchy.v | Z reconstruction, each swap/sign, diagonal zero, nine canonical ordering cases, exact r2 counts; no theorem defect, bridge to concrete storage intentionally absent. |
| BladeDichotomy.v | S3 tables/conventions, negative witness, generic-ring determinant obstruction, all 36 access cases, matrix reconstruction, finite double-coset counts; F01 and sharpness/count boundaries. |
| BladeMonad.v | Monad/MonadPlus equations, plan sums, block materialization, rank-changing pipe, segment-swap permutation, noncommuting application; no theorem defect. |
| BladeSafety.v | Failing lookup model, block-sum offset, injection/range, sigma-bound packaging, intermediate-buffer elimination; no theorem defect, machine/general-rank boundaries recorded. |
| BladeJacobian.v | Renaming/occurrence, ACEq congruence, derivative transport, joint tangent symmetry, formal-slot counterexample, all alias/multiplicity cases; F02. |

## Reproducible Rocq probe

Save the following as `FoundationProbes.v` outside `proofs/`, compile the audited project, then run `coqc -Q <compiled-proof-directory> Blade FoundationProbes.v`. It uses no new axioms and changes no project files. The probe was successfully checked with the local Rocq 9.0 toolchain against the clean copied audited sources.

```coq
From Blade Require Import BladeArrow BladeDichotomy BladeJacobian.
Require Import List Arith Lia Bool.
Import ListNotations.

Definition hidden_heads (_ : nat) : list nat := [0;1].
Definition hidden_step (s i : nat) : nat := S s.
Lemma hidden_state_changes : hidden_step 0 0 <> 0.
Proof. discriminate. Qed.
Lemma hidden_state_rectangular : forall r s t,
 canonA nat hidden_heads hidden_step r s t <->
 (length t = r /\ Forall (fun i => In i [0;1]) t).
Proof.
 induction r as [|k IH]; intros s t; destruct t as [|i t]; simpl.
 - split; intros; [split; constructor|exact I].
 - split; [contradiction|intros [H _]; discriminate].
 - split; [contradiction|intros [H _]; discriminate].
 - rewrite IH. split.
   + intros [Hi [HL HF]]. split; [now f_equal|now constructor].
   + intros [HL HF]. inversion HF; subst. split; [assumption|split; [lia|assumption]].
Qed.

Definition xor_add : bool -> bool -> bool := xorb.
Definition xor_neg (b:bool) := b.
Example char2_nonzero_self_negative : xor_neg true = true /\ true <> false.
Proof. split; [reflexivity|discriminate]. Qed.
Example char2_inverse : forall b, xor_add b (xor_neg b) = false.
Proof. destruct b; reflexivity. Qed.
Example char2_assoc : forall a b c, xor_add a (xor_add b c) = xor_add (xor_add a b) c.
Proof. destruct a,b,c; reflexivity. Qed.

Definition xy := EMul (EVar 0) (EVar 1).
Definition envxy (i:nat) := if i =? 0 then 2 else 3.
Example partial_not_symmetric :
 eval (fun _ x=>x) envxy (d (fun m=>m) 0 xy) <>
 eval (fun _ x=>x) (fun i=>envxy (swapIdx 0 1 i)) (d (fun m=>m) 0 xy).
Proof. compute; discriminate. Qed.


Eval vm_compute in (paircodes3 (pact [1;0;2] [0;1;2] ++ pact [1;2;0] [0;1;2]),
 paircodes3 ([0;1;2] ++ pact (pmul (pinv [1;0;2]) [1;2;0]) [0;1;2]),
 paircodes3 ([0;1;2] ++ pact (pmul [1;2;0] (pinv [1;0;2])) [0;1;2])).
Require Import ZArith. Close Scope Z_scope.
Definition wrong_order_T (x:list nat) : Z := if nleqb (paircodes3 x) [2;4;6] then 1%Z else 0%Z.
Example wrong_order_T_symmetric : diag_symb 3 wrong_order_T = true.
Proof. vm_compute; reflexivity. Qed.
Example comment_access_wrong :
 wrong_order_T (pact [1;0;2] [0;1;2] ++ pact [1;2;0] [0;1;2]) = 1%Z /\
 wrong_order_T ([0;1;2] ++ pact (pmul (pinv [1;0;2]) [1;2;0]) [0;1;2]) = 0%Z /\
 wrong_order_T ([0;1;2] ++ pact (pmul [1;2;0] (pinv [1;0;2])) [0;1;2]) = 1%Z.
Proof. vm_compute; repeat split; reflexivity. Qed.

From Blade Require Import BladeTrinity BladeCompound.
Inductive Host : Set := HScalar (n:nat) | HFun (f:nat -> Host).
Fixpoint embed_arity (r:nat) : OutT nat r -> Host :=
 match r as k return OutT nat k -> Host with
 | 0 => HScalar
 | S k => fun f => HFun (fun n=>embed_arity k (f n))
 end.
Example compound_rank_zero_ignores_false_mask :
 enumA ckSt (ck_heads (fun _=>false)) ck_step 0 ([],[]) = [[]].
Proof. reflexivity. Qed.
```

