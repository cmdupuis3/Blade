(* ===================================================================== *)
(* BladeJacobian.v -- the Jacobian symmetry transfer theorem and the     *)
(* symmetric-accumulation (multiplicity) rule, at the level the compiler *)
(* actually differentiates: SYMBOLIC differentiation on a small          *)
(* expression language (Grad.fs's derivRule table / product rule as      *)
(* structural recursion), with NO real analysis anywhere.                *)
(*                                                                       *)
(* Backs the AD roadmap's "Jacobian symmetry theorem" and "symmetric     *)
(* gradient accumulation" open items (stage C5 of the retired AD plan;   *)
(* surviving AD-posture notes: src/ml/README.md and                      *)
(* docs/features/equivariant-nn.md sect. 11).                            *)
(*                                                                       *)
(* Sections:                                                             *)
(*   1. Expr            -- variables, constants, +, *, and an OPAQUE     *)
(*                         unary intrinsic with a formal derivative      *)
(*                         slot (the derivRule model); renaming action   *)
(*   2. ACEq            -- structural (ring-law) equivalence: the        *)
(*                         congruence closure of comm/assoc/distrib.     *)
(*                         This is the hypothesis class the compiler's   *)
(*                         parity deduction certifies (parities          *)
(*                         propagate up the AST from primitives)         *)
(*   3. Differentiation -- symbolic d; T1 equivariance (d commutes with  *)
(*                         renaming); d respects ACEq; the JACOBIAN      *)
(*                         SYMMETRY TRANSFER; the tangent-kernel joint   *)
(*                         pair-swap symmetry (T2)                       *)
(*   4. Evaluation      -- nat-semiring semantics; ACEq soundness;       *)
(*                         semantic corollaries of T1/T2                 *)
(*   5. Refutation      -- in the formal-slot model, SEMANTIC primal     *)
(*                         symmetry alone does NOT transfer: the         *)
(*                         structural hypothesis is the right one        *)
(*   6. Accumulation    -- T3: d/d(stored canonical cell) of the         *)
(*                         canonical-access contraction = the orbit sum  *)
(*                         of cotangents (off-diagonal x2, diagonal x1;  *)
(*                         rank 2, reusing BladeCore's canon2)           *)
(*                                                                       *)
(* Doctrine note (product-symmetry correction): every symmetry here is   *)
(* a JOINT swap -- argument slots exchanged wholesale, value and         *)
(* tangent dragged together.  Nothing in this file licenses per-         *)
(* dimension swaps (per_dim_swap_not_symmetry stands).                   *)
(*                                                                       *)
(* Scope caveats, stated up front: rank 2 / one transposition (general   *)
(* r is a roadmap item); the intrinsic derivative is a FORMAL slot       *)
(* (dname), so nothing ties P (dname m) to an analytic derivative of     *)
(* P m -- section 5 shows this is not a gap in the theorems but a        *)
(* genuine boundary of the model; no subtraction/division (nat           *)
(* semiring), so quotient/negation kernels are out of this file.         *)
(*                                                                       *)
(* Self-contained over BladeCore (canon2 only).  Coq 8.18 / Rocq 9.0.    *)
(* ===================================================================== *)

Require Import Arith Lia Bool.
Require Import Blade.BladeCore.

(* ===================================================================== *)
(* 1. THE EXPRESSION LANGUAGE.                                           *)
(* Variables are nat-named (kernel parameters AND tangent seeds live in  *)
(* one namespace); EPrim m u is an opaque unary intrinsic -- the         *)
(* derivRule table's domain.  This is the shape of kernel bodies that    *)
(* Grad.fs differentiates (product rule + chain rule through             *)
(* intrinsics); quotient/power ride the same structure and are omitted   *)
(* (they need a ring/field, not the nat semiring).                       *)
(* ===================================================================== *)

Inductive expr : Type :=
| EVar   : nat -> expr
| EConst : nat -> expr
| EAdd   : expr -> expr -> expr
| EMul   : expr -> expr -> expr
| EPrim  : nat -> expr -> expr.

(* Variable renaming: the permutation action on kernel-argument names. *)
Fixpoint ren (s : nat -> nat) (e : expr) : expr :=
  match e with
  | EVar j    => EVar (s j)
  | EConst c  => EConst c
  | EAdd l r  => EAdd (ren s l) (ren s r)
  | EMul l r  => EMul (ren s l) (ren s r)
  | EPrim m u => EPrim m (ren s u)
  end.

Lemma ren_comp : forall s2 s1 e,
  ren s2 (ren s1 e) = ren (fun x => s2 (s1 x)) e.
Proof. intros s2 s1 e; induction e; simpl; congruence. Qed.

(* The transposition a <-> b as a function on names. *)
Definition swapIdx (a b x : nat) : nat :=
  if x =? a then b else if x =? b then a else x.

Lemma swapIdx_l : forall a b, swapIdx a b a = b.
Proof. intros; unfold swapIdx; rewrite Nat.eqb_refl; reflexivity. Qed.

Lemma swapIdx_r : forall a b, swapIdx a b b = a.
Proof.
  intros; unfold swapIdx.
  destruct (Nat.eqb_spec b a); [congruence | rewrite Nat.eqb_refl; reflexivity].
Qed.

Lemma swapIdx_other : forall a b x, x <> a -> x <> b -> swapIdx a b x = x.
Proof.
  intros a b x Ha Hb; unfold swapIdx.
  destruct (Nat.eqb_spec x a); [congruence |].
  destruct (Nat.eqb_spec x b); [congruence | reflexivity].
Qed.

Lemma swapIdx_invol : forall a b x, swapIdx a b (swapIdx a b x) = x.
Proof.
  intros a b x.
  destruct (Nat.eq_dec x a) as [-> |].
  - rewrite swapIdx_l; apply swapIdx_r.
  - destruct (Nat.eq_dec x b) as [-> |].
    + rewrite swapIdx_r; apply swapIdx_l.
    + rewrite (swapIdx_other a b x) by auto.
      apply swapIdx_other; auto.
Qed.

(* Occurrence of a variable name (decidable). *)
Fixpoint occurs (x : nat) (e : expr) : bool :=
  match e with
  | EVar y    => y =? x
  | EConst _  => false
  | EAdd l r  => occurs x l || occurs x r
  | EMul l r  => occurs x l || occurs x r
  | EPrim _ u => occurs x u
  end.

(* Renamings agreeing on the occurring variables act identically. *)
Lemma ren_agree : forall e s s',
  (forall y, occurs y e = true -> s y = s' y) ->
  ren s e = ren s' e.
Proof.
  induction e; intros s s' H; simpl.
  - rewrite (H n); [reflexivity | simpl; apply Nat.eqb_refl].
  - reflexivity.
  - rewrite (IHe1 s s'), (IHe2 s s'); try reflexivity;
      intros y Hy; apply H; simpl; rewrite Hy;
      [rewrite orb_true_r | ]; reflexivity.
  - rewrite (IHe1 s s'), (IHe2 s s'); try reflexivity;
      intros y Hy; apply H; simpl; rewrite Hy;
      [rewrite orb_true_r | ]; reflexivity.
  - rewrite (IHe s s'); [reflexivity | intros y Hy; apply H; simpl; exact Hy].
Qed.

(* ===================================================================== *)
(* 2. STRUCTURAL EQUIVALENCE (ACEq).                                     *)
(* The congruence closure of the semiring laws: comm/assoc for + and *,  *)
(* left distributivity.  Two roles:                                      *)
(*  (i)  the HYPOTHESIS class of the transfer theorem -- the kernel is   *)
(*       STRUCTURALLY symmetric: ren (swap a b) e is ring-law-equal to   *)
(*       e.  This is what the compiler's parity deduction certifies      *)
(*       (parities propagate up the AST from primitive comm/assoc), and  *)
(*       it covers the paradigm kernels (x*y, g(x)+g(y), ...) that       *)
(*       SYNTACTIC invariance misses;                                    *)
(*  (ii) the equivalence in which the transferred symmetry is           *)
(*       CONCLUDED, whence semantic invariance by soundness (sect. 4).   *)
(* ===================================================================== *)

Inductive aceq : expr -> expr -> Prop :=
| ac_refl      : forall e, aceq e e
| ac_sym       : forall e1 e2, aceq e1 e2 -> aceq e2 e1
| ac_trans     : forall e1 e2 e3, aceq e1 e2 -> aceq e2 e3 -> aceq e1 e3
| ac_add       : forall l l' r r', aceq l l' -> aceq r r' ->
                 aceq (EAdd l r) (EAdd l' r')
| ac_mul       : forall l l' r r', aceq l l' -> aceq r r' ->
                 aceq (EMul l r) (EMul l' r')
| ac_prim      : forall m u u', aceq u u' -> aceq (EPrim m u) (EPrim m u')
| ac_add_comm  : forall l r, aceq (EAdd l r) (EAdd r l)
| ac_add_assoc : forall x y z, aceq (EAdd x (EAdd y z)) (EAdd (EAdd x y) z)
| ac_mul_comm  : forall l r, aceq (EMul l r) (EMul r l)
| ac_mul_assoc : forall x y z, aceq (EMul x (EMul y z)) (EMul (EMul x y) z)
| ac_distrib   : forall x y z,
                 aceq (EMul x (EAdd y z)) (EAdd (EMul x y) (EMul x z)).

(* Right distributivity, derived. *)
Lemma aceq_distrib_r : forall x y z,
  aceq (EMul (EAdd x y) z) (EAdd (EMul x z) (EMul y z)).
Proof.
  intros x y z.
  eapply ac_trans; [apply ac_mul_comm |].
  eapply ac_trans; [apply ac_distrib |].
  apply ac_add; apply ac_mul_comm.
Qed.

(* Middle exchange in a 4-term sum (used for the distributivity and     *)
(* associativity cases of d-congruence).                                 *)
Lemma aceq_add_exchange : forall A B C D,
  aceq (EAdd (EAdd A B) (EAdd C D)) (EAdd (EAdd A C) (EAdd B D)).
Proof.
  intros A B C D.
  eapply ac_trans; [apply ac_sym, ac_add_assoc |].
  eapply ac_trans; [apply ac_add; [apply ac_refl | apply ac_add_assoc] |].
  eapply ac_trans;
    [apply ac_add; [apply ac_refl | apply ac_add; [apply ac_add_comm | apply ac_refl]] |].
  eapply ac_trans; [apply ac_add; [apply ac_refl | apply ac_sym, ac_add_assoc] |].
  apply ac_add_assoc.
Qed.

(* Renaming preserves structural equivalence (the generators are        *)
(* renaming-stable).                                                     *)
Lemma aceq_ren : forall s e1 e2, aceq e1 e2 -> aceq (ren s e1) (ren s e2).
Proof.
  intros s e1 e2 H; induction H; simpl;
    eauto using aceq.
Qed.

(* Structural symmetries compose -- the ACEq analogue of BladeLowering's *)
(* invariant_compose: the certified-symmetry class is a monoid (for      *)
(* finite permutation groups, monoid closure generates the subgroup).    *)
Lemma symclass_compose : forall s1 s2 e,
  aceq (ren s1 e) e -> aceq (ren s2 e) e ->
  aceq (ren (fun x => s1 (s2 x)) e) e.
Proof.
  intros s1 s2 e H1 H2.
  rewrite <- ren_comp.
  eapply ac_trans; [apply aceq_ren, H2 | exact H1].
Qed.

(* ===================================================================== *)
(* 3. SYMBOLIC DIFFERENTIATION.                                          *)
(* d i e is Grad.fs's structural recursion: sum rule, product rule, and  *)
(* the chain rule through opaque intrinsics, where dname : nat -> nat is *)
(* the FORMAL derivative table (derivRule's shape: each intrinsic has a  *)
(* designated derivative intrinsic; no analysis).                        *)
(* ===================================================================== *)

Section Differentiation.
  Variable dname : nat -> nat.

  Fixpoint d (i : nat) (e : expr) : expr :=
    match e with
    | EVar j    => if j =? i then EConst 1 else EConst 0
    | EConst _  => EConst 0
    | EAdd l r  => EAdd (d i l) (d i r)
    | EMul l r  => EAdd (EMul (d i l) r) (EMul l (d i r))
    | EPrim m u => EMul (EPrim (dname m) u) (d i u)
    end.

  (* Differentiation introduces no new variables. *)
  Lemma occurs_d : forall e i x,
    occurs x (d i e) = true -> occurs x e = true.
  Proof.
    induction e; simpl; intros i x H.
    - destruct (n =? i); simpl in H; discriminate.
    - exact H.
    - apply orb_true_iff in H; apply orb_true_iff;
        destruct H as [H | H]; eauto.
    - apply orb_true_iff in H; apply orb_true_iff.
      destruct H as [H | H]; apply orb_true_iff in H; destruct H as [H | H];
        eauto.
    - apply orb_true_iff in H; destruct H as [H | H]; eauto.
  Qed.

  (* ------------------------------------------------------------------ *)
  (* T1 (equivariance of differentiation): d commutes with renaming.    *)
  (* For a renaming with an explicit two-sided inverse (the tower's     *)
  (* permutation convention, BladeCompleteness.perm_pair):              *)
  (*      d i (ren s e) = ren s (d (s' i) e)                            *)
  (* -- the derivative of the renamed expression is the renamed         *)
  (* derivative at the renamed index.  Pure structural induction; this  *)
  (* is a SYNTACTIC identity, prior to any equivalence.                 *)
  (* ------------------------------------------------------------------ *)
  Theorem d_ren_equivariant : forall s s',
    (forall x, s' (s x) = x) ->
    (forall x, s (s' x) = x) ->
    forall e i, d i (ren s e) = ren s (d (s' i) e).
  Proof.
    intros s s' Hs's Hss' e; induction e; intro i; simpl.
    - destruct (Nat.eqb_spec (s n) i); destruct (Nat.eqb_spec n (s' i));
        simpl; try reflexivity.
      + exfalso; apply n0; rewrite <- e; symmetry; apply Hs's.
      + exfalso; apply n0; rewrite e; apply Hss'.
    - reflexivity.
    - rewrite IHe1, IHe2; reflexivity.
    - rewrite IHe1, IHe2; reflexivity.
    - rewrite IHe; reflexivity.
  Qed.

  (* The transposition instance (swapIdx is its own inverse). *)
  Corollary d_swap_equivariant : forall a b e i,
    d i (ren (swapIdx a b) e) = ren (swapIdx a b) (d (swapIdx a b i) e).
  Proof.
    intros; apply d_ren_equivariant; intro x; apply swapIdx_invol.
  Qed.

  (* ------------------------------------------------------------------ *)
  (* Differentiation respects structural equivalence: d is a congruence *)
  (* for ACEq.  Induction over the derivation; the comm/assoc/distrib   *)
  (* generator cases are the Leibniz computations, closed inside ACEq.  *)
  (* ------------------------------------------------------------------ *)
  Theorem d_respects_aceq : forall e1 e2,
    aceq e1 e2 -> forall i, aceq (d i e1) (d i e2).
  Proof.
    intros e1 e2 H; induction H; intro i; simpl.
    - apply ac_refl.
    - apply ac_sym; auto.
    - eapply ac_trans; eauto.
    - apply ac_add; auto.
    - apply ac_add; apply ac_mul; auto.
    - apply ac_mul; [apply ac_prim; assumption | auto].
    - apply ac_add_comm.
    - apply ac_add_assoc.
    - (* mul_comm: d(l*r) = dl*r + l*dr  ~  dr*l + r*dl = d(r*l) *)
      eapply ac_trans; [apply ac_add_comm |].
      apply ac_add; apply ac_mul_comm.
    - (* mul_assoc: distribute, reassociate each product, regroup *)
      eapply ac_trans;
        [apply ac_add; [apply ac_refl | apply ac_distrib] |].
      eapply ac_trans; [apply ac_add_assoc |].
      eapply ac_trans;
        [apply ac_add;
           [apply ac_add; apply ac_mul_assoc | apply ac_mul_assoc] |].
      apply ac_add; [apply ac_sym, aceq_distrib_r | apply ac_refl].
    - (* distrib: two distributions and a middle exchange *)
      eapply ac_trans;
        [apply ac_add; [apply ac_distrib | apply ac_distrib] |].
      apply aceq_add_exchange.
  Qed.

  (* ------------------------------------------------------------------ *)
  (* THE JACOBIAN SYMMETRY TRANSFER (rank 2, joint swap).               *)
  (* If the kernel is structurally symmetric under the swap a <-> b,    *)
  (* its two partials are each other's images under that swap: the      *)
  (* Jacobian row inherits the output symmetry in the corresponding     *)
  (* indices.  Both directions, from T1 + congruence.                   *)
  (* ------------------------------------------------------------------ *)
  Theorem jacobian_symmetry_transfer : forall a b e,
    aceq (ren (swapIdx a b) e) e ->
    aceq (ren (swapIdx a b) (d a e)) (d b e).
  Proof.
    intros a b e Hsym.
    assert (Heq : ren (swapIdx a b) (d a e) = d b (ren (swapIdx a b) e)).
    { rewrite (d_swap_equivariant a b e b), swapIdx_r; reflexivity. }
    rewrite Heq; apply d_respects_aceq; exact Hsym.
  Qed.

  Corollary jacobian_symmetry_transfer_rl : forall a b e,
    aceq (ren (swapIdx a b) e) e ->
    aceq (ren (swapIdx a b) (d b e)) (d a e).
  Proof.
    intros a b e Hsym.
    assert (Heq : ren (swapIdx a b) (d b e) = d a (ren (swapIdx a b) e)).
    { rewrite (d_swap_equivariant a b e a), swapIdx_l; reflexivity. }
    rewrite Heq; apply d_respects_aceq; exact Hsym.
  Qed.

  (* ------------------------------------------------------------------ *)
  (* T2: TANGENT-KERNEL SYMMETRY UNDER THE JOINT PAIR SWAP.             *)
  (* The forward-mode tangent of e w.r.t. the argument pair (a, b) with *)
  (* tangent seeds (da, db) -- exactly the emitted jvp schema           *)
  (* dk1*da + dk2*db of plan sect. 6.4:                                 *)
  (* ------------------------------------------------------------------ *)
  Definition tangent (a da b db : nat) (e : expr) : expr :=
    EAdd (EMul (d a e) (EVar da)) (EMul (d b e) (EVar db)).

  (* The JOINT pair swap: (a, da) <-> (b, db) -- value and tangent      *)
  (* dragged together.  (Per-dimension swaps stay unlicensed:           *)
  (* per_dim_swap_not_symmetry.)                                        *)
  Definition jswap (a da b db x : nat) : nat :=
    swapIdx da db (swapIdx a b x).

  (* If the primal kernel is structurally symmetric in a <-> b, the     *)
  (* tangent kernel is structurally symmetric under the joint pair      *)
  (* swap.  Freshness: the seeds da, db are variables the primal does   *)
  (* not mention (and are distinct from a, b) -- exactly the emission   *)
  (* situation, where seeds are fresh parameters.                       *)
  Theorem tangent_joint_swap : forall a b da db e,
    da <> a -> da <> b -> db <> a -> db <> b ->
    occurs da e = false -> occurs db e = false ->
    aceq (ren (swapIdx a b) e) e ->
    aceq (ren (jswap a da b db) (tangent a da b db e))
         (tangent a da b db e).
  Proof.
    intros a b da db e Hda1 Hda2 Hdb1 Hdb2 Hoa Hob Hsym.
    unfold tangent, jswap; simpl.
    (* the seed variables cross over: jswap da = db, jswap db = da *)
    rewrite (swapIdx_other a b da Hda1 Hda2), swapIdx_l.
    rewrite (swapIdx_other a b db Hdb1 Hdb2), swapIdx_r.
    (* on d a e and d b e the joint swap acts as the plain swap:        *)
    (* their variables occur in e, hence avoid da and db                *)
    assert (Hagree : forall i,
        ren (fun x => swapIdx da db (swapIdx a b x)) (d i e)
        = ren (swapIdx a b) (d i e)).
    { intro i; apply ren_agree; intros y Hy.
      apply occurs_d in Hy.
      assert (Hyda : y <> da) by (intro; subst; congruence).
      assert (Hydb : y <> db) by (intro; subst; congruence).
      apply swapIdx_other; unfold swapIdx;
        destruct (Nat.eqb_spec y a); destruct (Nat.eqb_spec y b); congruence. }
    rewrite (Hagree a), (Hagree b).
    (* transfer both partials, then commute the two tangent terms *)
    eapply ac_trans;
      [apply ac_add;
         [apply ac_mul;
            [apply jacobian_symmetry_transfer; exact Hsym | apply ac_refl]
         | apply ac_mul;
            [apply jacobian_symmetry_transfer_rl; exact Hsym | apply ac_refl]]
      |].
    apply ac_add_comm.
  Qed.
End Differentiation.

(* ===================================================================== *)
(* 4. EVALUATION.                                                        *)
(* nat-semiring semantics (the tower's stdlib-only convention; any       *)
(* commutative semiring would do -- nothing uses subtraction).  P is     *)
(* the intrinsic interpretation; note P (dname m) is arbitrary: the      *)
(* formal derivative slot carries NO analytic meaning here.              *)
(* ===================================================================== *)

Section Evaluation.
  Variable dname : nat -> nat.
  Variable P : nat -> nat -> nat.

  Fixpoint eval (env : nat -> nat) (e : expr) : nat :=
    match e with
    | EVar j    => env j
    | EConst c  => c
    | EAdd l r  => eval env l + eval env r
    | EMul l r  => eval env l * eval env r
    | EPrim m u => P m (eval env u)
    end.

  (* Renaming = environment precomposition. *)
  Lemma eval_ren : forall s env e,
    eval env (ren s e) = eval (fun x => env (s x)) e.
  Proof. intros s env e; induction e; simpl; congruence. Qed.

  (* Structural equivalence is semantically sound. *)
  Theorem aceq_sound : forall e1 e2, aceq e1 e2 ->
    forall env, eval env e1 = eval env e2.
  Proof.
    intros e1 e2 H; induction H; intro env; simpl.
    - reflexivity.
    - symmetry; apply IHaceq.
    - rewrite IHaceq1; apply IHaceq2.
    - rewrite IHaceq1, IHaceq2; reflexivity.
    - rewrite IHaceq1, IHaceq2; reflexivity.
    - rewrite IHaceq; reflexivity.
    - apply Nat.add_comm.
    - apply Nat.add_assoc.
    - apply Nat.mul_comm.
    - apply Nat.mul_assoc.
    - apply Nat.mul_add_distr_l.
  Qed.

  (* Semantic reading of the structural-symmetry hypothesis. *)
  Corollary primal_symmetry_semantic : forall a b e,
    aceq (ren (swapIdx a b) e) e ->
    forall env, eval (fun x => env (swapIdx a b x)) e = eval env e.
  Proof.
    intros a b e H env.
    transitivity (eval env (ren (swapIdx a b) e)).
    - symmetry; apply eval_ren.
    - apply aceq_sound; exact H.
  Qed.

  (* The Jacobian transfer, semantically: the a-partial at the swapped   *)
  (* environment IS the b-partial.  This is the classic form d_a k(y,x)  *)
  (* = d_b k(x,y): the cotangent field over a symmetric primal is        *)
  (* symmetric under the joint relabeling, so canonical (triangular)     *)
  (* storage of derivative/cotangent buffers is lossless (access via     *)
  (* BladeCore.access_exact / raise_1_2 as for any symmetric array).     *)
  Corollary jacobian_transfer_semantic : forall a b e,
    aceq (ren (swapIdx a b) e) e ->
    forall env,
      eval (fun x => env (swapIdx a b x)) (d dname a e)
      = eval env (d dname b e).
  Proof.
    intros a b e H env.
    transitivity (eval env (ren (swapIdx a b) (d dname a e))).
    - symmetry; apply eval_ren.
    - apply aceq_sound, jacobian_symmetry_transfer; exact H.
  Qed.

  (* Plan claim (a), exactly: dk(a, da, b, db) = dk(b, db, a, da) --     *)
  (* the tangent kernel of a comm-symmetric kernel is symmetric under    *)
  (* the joint (value, tangent) pair swap.                               *)
  Corollary tangent_joint_swap_semantic : forall a b da db e,
    da <> a -> da <> b -> db <> a -> db <> b ->
    occurs da e = false -> occurs db e = false ->
    aceq (ren (swapIdx a b) e) e ->
    forall env,
      eval (fun x => env (jswap a da b db x)) (tangent dname a da b db e)
      = eval env (tangent dname a da b db e).
  Proof.
    intros a b da db e Hda1 Hda2 Hdb1 Hdb2 Hoa Hob Hsym env.
    transitivity (eval env (ren (jswap a da b db) (tangent dname a da b db e))).
    - symmetry; apply eval_ren.
    - apply aceq_sound, tangent_joint_swap; assumption.
  Qed.
End Evaluation.

(* ===================================================================== *)
(* Worked hypothesis instances: the structural-symmetry premise is       *)
(* satisfiable for the paradigm commutative kernels (what SYNTACTIC      *)
(* invariance would miss).                                               *)
(* ===================================================================== *)

Example product_kernel_structurally_symmetric :
  aceq (ren (swapIdx 0 1) (EMul (EVar 0) (EVar 1))) (EMul (EVar 0) (EVar 1)).
Proof.
  simpl; rewrite swapIdx_l, swapIdx_r; apply ac_mul_comm.
Qed.

Example intrinsic_sum_kernel_structurally_symmetric : forall g,
  aceq (ren (swapIdx 0 1) (EAdd (EPrim g (EVar 0)) (EPrim g (EVar 1))))
       (EAdd (EPrim g (EVar 0)) (EPrim g (EVar 1))).
Proof.
  intro g; simpl; rewrite swapIdx_l, swapIdx_r; apply ac_add_comm.
Qed.

(* ===================================================================== *)
(* 5. REFUTATION: the SEMANTIC hypothesis does not suffice in the        *)
(* formal-slot model.  A kernel whose intrinsic evaluates to a constant  *)
(* is semantically symmetric however its argument is named -- but its    *)
(* SYMBOLIC tangent reads the formal derivative slot, which the model    *)
(* leaves uninterpreted, and the joint-swap symmetry genuinely fails.    *)
(* Consequence for the compiler: symmetry used by Tier-2 emission must   *)
(* come from the STRUCTURAL judgment (deduced/declared comm; the ACEq    *)
(* class), not from any semantic accident of the primal.  This is the    *)
(* transfer-theorem analogue of per_dim_swap_not_symmetry: the           *)
(* refutation half that fixes where the license comes from.              *)
(* ===================================================================== *)

Module SemanticHypothesisInsufficient.
  (* intrinsic 0 evaluates constantly to 0; its formal derivative slot  *)
  (* (name 1) evaluates as the identity -- legal: no analytic tie.      *)
  Definition dn (m : nat) : nat := S m.
  Definition P0 (m : nat) (x : nat) : nat :=
    match m with 0 => 0 | _ => x end.

  (* e0 = intrinsic0(var 0): semantically symmetric in 0 <-> 1.         *)
  Definition e0 : expr := EPrim 0 (EVar 0).

  Lemma e0_semantically_symmetric :
    forall env, eval P0 env (ren (swapIdx 0 1) e0) = eval P0 env e0.
  Proof. reflexivity. Qed.

  (* witness environment: a = 5, b = 7, da = 1, db = 0 *)
  Definition env0 (x : nat) : nat :=
    match x with 0 => 5 | 1 => 7 | 2 => 1 | _ => 0 end.

  Theorem semantic_hypothesis_insufficient :
    eval P0 env0 (ren (jswap 0 2 1 3) (tangent dn 0 2 1 3 e0))
    <> eval P0 env0 (tangent dn 0 2 1 3 e0).
  Proof. compute; lia. Qed.   (* 0 <> 5 *)
End SemanticHypothesisInsufficient.

(* ===================================================================== *)
(* 6. T3: SYMMETRIC GRADIENT ACCUMULATION (rank 2).                      *)
(* Storage model: the stored buffer's cells are the VARIABLES; logical   *)
(* cell (i, j) reads the stored cell at canon2 i j (BladeCore's          *)
(* canonical access -- the decompact/034 read pattern).  The loss is     *)
(* the cotangent contraction: sum over ALL logical cells of              *)
(* cot(i,j) * M(i,j).  The theorem: the derivative w.r.t. a STORED       *)
(* canonical cell (p, q), p <= q, is the sum of cotangents over the      *)
(* cell's orbit of logical aliases -- cot(p,q) + cot(q,p) off the        *)
(* diagonal, cot(p,p) on it.  This is the multiplicity rule -- gradients *)
(* flow to both positions of an identity group per canonical tuple       *)
(* off-diagonal x2 at rank 2, diagonal x1), proved with the SAME         *)
(* differentiation operator d as T1/T2.                                  *)
(* ===================================================================== *)

(* Finite sums, and sums of expressions. *)
Fixpoint sumf (m : nat) (f : nat -> nat) : nat :=
  match m with 0 => 0 | S k => sumf k f + f k end.

Fixpoint esum (m : nat) (f : nat -> expr) : expr :=
  match m with 0 => EConst 0 | S k => EAdd (esum k f) (f k) end.

Lemma sumf_ext : forall m f g,
  (forall i, i < m -> f i = g i) -> sumf m f = sumf m g.
Proof.
  induction m; intros f g H; simpl; [reflexivity |].
  f_equal; [apply IHm; intros; apply H; lia | apply H; lia].
Qed.

Lemma sumf_zero : forall m, sumf m (fun _ => 0) = 0.
Proof. induction m; simpl; lia. Qed.

Lemma sumf_add : forall m f g,
  sumf m (fun i => f i + g i) = sumf m f + sumf m g.
Proof. induction m; intros; simpl; [reflexivity | rewrite IHm; lia]. Qed.

Lemma sumf_delta : forall m p f, p < m ->
  sumf m (fun i => if i =? p then f i else 0) = f p.
Proof.
  induction m; intros p f Hp; [lia | simpl].
  destruct (Nat.eqb_spec m p).
  - subst p.
    rewrite (sumf_ext m _ (fun _ => 0))
      by (intros i Hi; destruct (Nat.eqb_spec i m); [lia | reflexivity]).
    rewrite sumf_zero; cbn; lia.
  - rewrite IHm by lia; cbn; lia.
Qed.

Lemma eval_esum : forall Pi env m f,
  eval Pi env (esum m f) = sumf m (fun i => eval Pi env (f i)).
Proof.
  intros Pi env m f; induction m; simpl; [reflexivity | rewrite IHm; reflexivity].
Qed.

Lemma d_esum : forall dn x m f,
  d dn x (esum m f) = esum m (fun i => d dn x (f i)).
Proof.
  intros dn x m f; induction m; simpl; [reflexivity | rewrite IHm; reflexivity].
Qed.

(* Boolean bookkeeping. *)
Lemma bool_eq_iff : forall b1 b2 : bool,
  (b1 = true <-> b2 = true) -> b1 = b2.
Proof.
  intros b1 b2 H; destruct b1, b2; try reflexivity.
  - exact (eq_sym ((proj1 H) eq_refl)).
  - exact ((proj2 H) eq_refl).
Qed.

Lemma orb_if_split : forall (x y : bool) (c : nat),
  (x = true -> y = true -> False) ->
  (if x || y then c else 0) = (if x then c else 0) + (if y then c else 0).
Proof. intros [] [] c H; simpl; try lia; exfalso; auto. Qed.

Section SymmetricAccumulation.
  Variable dname : nat -> nat.
  Variable Pi : nat -> nat -> nat.
  Variable n : nat.                     (* extent of each index         *)
  Variable cot : nat -> nat -> nat.     (* cotangent at logical (i, j)  *)

  (* Stored cell (p, q) is the variable p*n + q. *)
  Definition enc (p q : nat) : nat := p * n + q.

  Lemma enc_inj : forall i j p q, j < n -> q < n ->
    enc i j = enc p q -> i = p /\ j = q.
  Proof.
    intros i j p q Hj Hq He; unfold enc in He.
    assert (i = p) by nia.
    split; [assumption | nia].
  Qed.

  (* Logical cell (i, j) reads the stored canonical cell: canonical     *)
  (* access, exactly BladeCore.access_exact's read pattern.             *)
  Definition cvar (i j : nat) : expr :=
    EVar (enc (fst (canon2 i j)) (snd (canon2 i j))).

  (* The cotangent contraction over the full logical (dense) space. *)
  Definition contraction : expr :=
    esum n (fun i => esum n (fun j => EMul (EConst (cot i j)) (cvar i j))).

  Lemma canon2_snd_lt : forall i j, i < n -> j < n -> snd (canon2 i j) < n.
  Proof. intros; unfold canon2; destruct (le_dec i j); simpl; lia. Qed.

  (* The orbit of a canonical cell: exactly its logical aliases. *)
  Lemma canon2_eq_iff : forall p q i j, p <= q ->
    (canon2 i j = (p, q) <-> (i = p /\ j = q) \/ (i = q /\ j = p)).
  Proof.
    intros p q i j Hpq; unfold canon2; destruct (le_dec i j); split; intro H.
    - injection H as H1 H2; subst; left; auto.
    - destruct H as [[-> ->] | [-> ->]]; [reflexivity | f_equal; lia].
    - injection H as H1 H2; subst; right; auto.
    - destruct H as [[-> ->] | [-> ->]]; [f_equal; lia | reflexivity].
  Qed.

  (* The derivative of one contraction term is the alias indicator. *)
  Lemma eval_d_term : forall env c w X,
    eval Pi env (d dname X (EMul (EConst c) (EVar w)))
    = if w =? X then c else 0.
  Proof. intros; simpl; destruct (w =? X); simpl; lia. Qed.

  (* ------------------------------------------------------------------ *)
  (* THE ACCUMULATION MULTIPLICITY RULE.                                *)
  (* d/d(stored (p,q)) of the contraction = the orbit sum:              *)
  (* off-diagonal canonical cells receive BOTH aliases' cotangents,     *)
  (* diagonal cells one.                                                *)
  (* ------------------------------------------------------------------ *)
  Theorem symmetric_accumulation :
    forall p q env, p <= q -> q < n ->
      eval Pi env (d dname (enc p q) contraction)
      = if p =? q then cot p p else cot p q + cot q p.
  Proof.
    intros p q env Hpq Hqn.
    unfold contraction.
    rewrite d_esum, eval_esum.
    rewrite (sumf_ext n _
      (fun i => sumf n (fun j =>
         if ((i =? p) && (j =? q)) || ((i =? q) && (j =? p))
         then cot i j else 0))).
    2: { intros i Hi.
         rewrite d_esum, eval_esum.
         apply sumf_ext; intros j Hj.
         unfold cvar; rewrite eval_d_term.
         assert (Hchar :
             (enc (fst (canon2 i j)) (snd (canon2 i j)) =? enc p q)
           = (((i =? p) && (j =? q)) || ((i =? q) && (j =? p)))).
         { apply bool_eq_iff; split; intro Hb.
           - apply Nat.eqb_eq in Hb.
             destruct (enc_inj _ _ _ _ (canon2_snd_lt i j Hi Hj) Hqn Hb)
               as [Hu Hv].
             assert (Hc : canon2 i j = (p, q)).
             { rewrite (surjective_pairing (canon2 i j)), Hu, Hv; reflexivity. }
             apply (canon2_eq_iff p q i j Hpq) in Hc.
             destruct Hc as [[-> ->] | [-> ->]].
             + rewrite !Nat.eqb_refl; reflexivity.
             + rewrite !Nat.eqb_refl.
               destruct ((q =? p) && (p =? q)); reflexivity.
           - apply Nat.eqb_eq.
             apply orb_true_iff in Hb; destruct Hb as [Hb | Hb];
               apply andb_true_iff in Hb; destruct Hb as [H1 H2];
               apply Nat.eqb_eq in H1; apply Nat.eqb_eq in H2; subst.
             + assert (Hc : canon2 p q = (p, q))
                 by (apply (canon2_eq_iff p q p q Hpq); left; auto).
               rewrite Hc; reflexivity.
             + assert (Hc : canon2 q p = (p, q))
                 by (apply (canon2_eq_iff p q q p Hpq); right; auto).
               rewrite Hc; reflexivity. }
         rewrite Hchar; reflexivity. }
    destruct (Nat.eqb_spec p q) as [<- | Hne].
    - (* diagonal: one alias *)
      rewrite (sumf_ext n _ (fun i => if i =? p then cot i p else 0)).
      2: { intros i Hi.
           rewrite (sumf_ext n _ (fun j =>
             if (i =? p) && (j =? p) then cot i j else 0))
             by (intros j Hj; rewrite orb_diag; reflexivity).
           destruct (Nat.eqb_spec i p) as [-> | Hip].
           - apply (sumf_delta n p (fun j => cot p j) Hqn).
           - cbn; apply sumf_zero. }
      apply (sumf_delta n p (fun i => cot i p) Hqn).
    - (* off-diagonal: both aliases *)
      rewrite (sumf_ext n _
        (fun i => sumf n (fun j => if (i =? p) && (j =? q) then cot i j else 0)
                + sumf n (fun j => if (i =? q) && (j =? p) then cot i j else 0))).
      2: { intros i Hi.
           rewrite <- sumf_add.
           apply sumf_ext; intros j Hj.
           apply orb_if_split; intros Hx Hy.
           apply andb_true_iff in Hx; destruct Hx as [Hx1 _].
           apply andb_true_iff in Hy; destruct Hy as [Hy1 _].
           apply Nat.eqb_eq in Hx1; apply Nat.eqb_eq in Hy1; subst; auto. }
      rewrite sumf_add.
      assert (Hpn : p < n) by lia.
      f_equal.
      + rewrite (sumf_ext n _ (fun i => if i =? p then cot i q else 0)).
        2: { intros i Hi.
             destruct (Nat.eqb_spec i p) as [-> | Hip].
             - apply (sumf_delta n q (fun j => cot p j) Hqn).
             - cbn; apply sumf_zero. }
        apply (sumf_delta n p (fun i => cot i q) Hpn).
      + rewrite (sumf_ext n _ (fun i => if i =? q then cot i p else 0)).
        2: { intros i Hi.
             destruct (Nat.eqb_spec i q) as [-> | Hiq].
             - apply (sumf_delta n p (fun j => cot q j) Hpn).
             - cbn; apply sumf_zero. }
        apply (sumf_delta n q (fun i => cot i p) Hqn).
  Qed.
End SymmetricAccumulation.

(* Concrete pin, matching corpus index-types/034 decompact semantics at  *)
(* n = 3: the off-diagonal stored cell (0,1) accumulates both logical    *)
(* aliases (0,1) and (1,0); the diagonal cell (1,1) accumulates once.    *)
Module AccumulationExample.
  Definition dn (m : nat) : nat := m.
  Definition Pi (m x : nat) : nat := x.
  Definition cot (i j : nat) : nat := 7 * i + j + 1.
  Definition env0 (x : nat) : nat := 0.

  Example off_diagonal_x2 :
    eval Pi env0 (d dn (enc 3 0 1) (contraction 3 cot))
    = cot 0 1 + cot 1 0.               (* 2 + 8 = 10 *)
  Proof. vm_compute; reflexivity. Qed.

  Example diagonal_x1 :
    eval Pi env0 (d dn (enc 3 1 1) (contraction 3 cot))
    = cot 1 1.                          (* 9 *)
  Proof. vm_compute; reflexivity. Qed.
End AccumulationExample.

(* ===================================================================== *)
(* Generalization notes (not mechanized; see the honesty ledger in       *)
(* docs/proofs.md):                                                      *)
(*  - General rank r: transfer under the full diagonal S_r (a list of   *)
(*    transpositions via symclass_compose; the tangent then carries r    *)
(*    seed variables), and the accumulation rule with orbit-size         *)
(*    multiplicities r!/|stabilizer| per canonical tuple.  Roadmap item. *)
(*  - The reduce/loop level: T2 is a KERNEL-level statement; the claim   *)
(*    that a whole materialized tangent ARRAY is symmetric additionally  *)
(*    uses output_symmetry_soundness (H := this file's transferred       *)
(*    invariance, Stab := identical primal/tangent array bindings) --    *)
(*    the composition is stated in docs/proofs.md, not mechanized.       *)
(*  - Subtraction/division kernels (quotient rule) need a ring/field    *)
(*    semantics; the product/chain/sum rules proved here are the ones    *)
(*    the symmetry claims lean on.                                       *)
(* ===================================================================== *)
