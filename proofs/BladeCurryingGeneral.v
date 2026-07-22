(* ===================================================================== *)
(* BladeCurryingGeneral.v -- Layer 2b: the GENERALIZED maximal-currying  *)
(* theorem.                                                              *)
(*                                                                       *)
(* Upgrade over BladeCurrying.v's Theorem 9.26 in two directions:        *)
(*                                                                       *)
(* 1. DETECTION IS INFORMATION-THEORETIC, not by fiat.  A currying S     *)
(*    detects a property iff SOME predicate of the data S exposes        *)
(*    defines that property.  The characterizations                      *)
(*      detects S (array property)  <->  all positions bound            *)
(*      detects S (kernel property) <->  kernel bound                   *)
(*    are now THEOREMS (detects_arr_char, detects_ker_char), derived     *)
(*    from two hypotheses about the property itself:                     *)
(*      - FULL RELATIONAL SUPPORT: at every position, the property can   *)
(*        flip while everything else is held fixed;                      *)
(*      - nonconstancy of the kernel property.                           *)
(*                                                                       *)
(* 2. THE PROPERTY IS ABSTRACT.  Identity/commutativity (the symmetry    *)
(*    case) is ONE instance (two_maximal_identity_comm); any future      *)
(*    "exotic" output-relevant property is covered by supplying a new    *)
(*    support witness.  Conversely, positional_detection_example shows   *)
(*    per-position properties do NOT force full binding: full relational *)
(*    support is exactly the class of properties that motivates          *)
(*    method_for.                                                        *)
(*                                                                       *)
(* Imports BladeDMWF, BladeCurrying.  Coq 8.18, stdlib only.             *)
(* ===================================================================== *)

From Blade Require Import BladeDMWF BladeCurrying.
Require Import List Arith Lia Setoid.
Import ListNotations.

Definition Kern := nat -> nat -> nat.
Definition Args := nat -> nat.

Section InformationDetection.
  Variable r : nat.

  (* -------- what a currying exposes, and what it can define --------- *)

  (* P respects S: P cannot distinguish call sites that agree on         *)
  (* everything S exposes (the kernel if bound; the bound positions).    *)
  Definition respects (S : Spec) (P : Kern -> Args -> Prop) : Prop :=
    forall (f f' : Kern) (A A' : Args),
      (hasK S = true -> f = f') ->
      (forall q, q < r -> hasPos S q = true -> A q = A' q) ->
      (P f A <-> P f' A').

  (* S detects Q: some S-respecting predicate coincides with Q.          *)
  Definition detects (S : Spec) (Q : Kern -> Args -> Prop) : Prop :=
    exists P, respects S P /\ (forall f A, P f A <-> Q f A).

  (* A property with per-position support IS detectable from a partial   *)
  (* binding -- the delineating counterpoint: only fully-relational      *)
  (* properties force method_for's all-or-nothing binding. *)
  Lemma positional_detection_example :
    0 < r ->
    detects {| hasK := false; hasPos := fun q => q =? 0 |}
            (fun _ A => A 0 = 0).
  Proof.
    intro Hr. exists (fun _ A => A 0 = 0). split; [| tauto].
    intros f f' A A' _ Hag.
    assert (E : A 0 = A' 0) by (apply Hag; [lia | reflexivity]).
    rewrite E. tauto.
  Qed.

  (* -------- the abstract array-tuple property ------------------------ *)

  Variable ArrProp : Args -> Prop.
  (* locality: the property concerns positions < r only *)
  Hypothesis Harr_local :
    forall A A', (forall q, q < r -> A q = A' q) ->
    (ArrProp A <-> ArrProp A').
  (* FULL RELATIONAL SUPPORT: every position can flip the property *)
  Hypothesis Harr_support :
    forall p, p < r ->
      exists A A',
        (forall q, q <> p -> A q = A' q) /\ ArrProp A /\ ~ ArrProp A'.

  (* -------- the abstract kernel property ----------------------------- *)

  Variable KerProp : Kern -> Prop.
  Hypothesis Hker_nonconst :
    exists f1 f2, KerProp f1 /\ ~ KerProp f2.

  Definition ArrQ : Kern -> Args -> Prop := fun _ A => ArrProp A.
  Definition KerQ : Kern -> Args -> Prop := fun f _ => KerProp f.

  (* -------- the characterization theorems ---------------------------- *)

  Lemma detects_arr_char : forall S, detects S ArrQ <-> allPos r S.
  Proof.
    intro S; split.
    - intros (P & Hresp & Hiff) p Hp.
      destruct (hasPos S p) eqn:HP; [reflexivity | exfalso].
      destruct (Harr_support p Hp) as (A & A' & Hag & HA & HnA').
      assert (HPP : P (fun _ _ => 0) A <-> P (fun _ _ => 0) A').
      { apply Hresp.
        - intros _. reflexivity.
        - intros q Hq Hq'. apply Hag. intro E. subst q. congruence. }
      apply HnA'.
      apply (proj1 (Hiff (fun _ _ => 0) A')).
      apply (proj1 HPP).
      apply (proj2 (Hiff (fun _ _ => 0) A)).
      exact HA.
    - intro Hall. exists (fun _ A => ArrProp A). split.
      + intros f f' A A' _ Hag. apply Harr_local.
        intros q Hq. apply Hag; [exact Hq | apply Hall; exact Hq].
      + intros f A. unfold ArrQ. tauto.
  Qed.

  Lemma detects_ker_char : forall S, detects S KerQ <-> hasK S = true.
  Proof.
    intro S; split.
    - intros (P & Hresp & Hiff).
      destruct (hasK S) eqn:HK; [reflexivity | exfalso].
      destruct Hker_nonconst as (f1 & f2 & H1 & H2).
      assert (HPP : P f1 (fun _ => 0) <-> P f2 (fun _ => 0)).
      { apply Hresp.
        - intro E. congruence.
        - intros q _ _. reflexivity. }
      apply H2.
      apply (proj1 (Hiff f2 (fun _ => 0))).
      apply (proj1 HPP).
      apply (proj2 (Hiff f1 (fun _ => 0))).
      exact H1.
    - intro HK. exists (fun f _ => KerProp f). split.
      + intros f f' A A' Hf _. rewrite (Hf HK). tauto.
      + intros f A. unfold KerQ. tauto.
  Qed.

  (* -------- the generalized two-maximal-curryings theorem ------------ *)

  Definition gwastedK (S : Spec) : Prop :=
    (detects (dropK S) ArrQ <-> detects S ArrQ) /\
    (detects (dropK S) KerQ <-> detects S KerQ).
  Definition gwastedP (p : nat) (S : Spec) : Prop :=
    (detects (dropP p S) ArrQ <-> detects S ArrQ) /\
    (detects (dropP p S) KerQ <-> detects S KerQ).
  Definition gwasteless (S : Spec) : Prop :=
    (hasK S = true -> ~ gwastedK S) /\
    (forall p, p < r -> hasPos S p = true -> ~ gwastedP p S).

  Theorem two_maximal_curryings_general :
    forall S : Spec,
      proper r S ->
      (detects S ArrQ \/ detects S KerQ) ->
      gwasteless S ->
      (hasK S = true /\ (forall p, p < r -> hasPos S p = false))   (* object_for *)
      \/ (hasK S = false /\ allPos r S).                           (* method_for *)
  Proof.
    intros S Hproper Hdet [HwK HwP].
    destruct (hasK S) eqn:HK.
    - left. split; [reflexivity |].
      intros p Hpr. destruct (hasPos S p) eqn:HP; [exfalso | reflexivity].
      assert (Hnid : ~ allPos r S).
      { intro Hall. apply Hproper. split; [exact HK | exact Hall]. }
      apply (HwP p Hpr HP). split.
      + rewrite !detects_arr_char.
        split; intro Hx; exfalso.
        * specialize (Hx p Hpr). simpl in Hx.
          rewrite Nat.eqb_refl in Hx. discriminate.
        * exact (Hnid Hx).
      + rewrite !detects_ker_char. simpl. tauto.
    - right. split; [reflexivity |].
      destruct Hdet as [Hid | Hcm].
      + apply detects_arr_char in Hid. exact Hid.
      + apply detects_ker_char in Hcm. congruence.
  Qed.

  (* -------- existence: both canonical curryings qualify -------------- *)

  Lemma method_spec_general_ok :
    proper r method_spec /\ detects method_spec ArrQ /\ gwasteless method_spec.
  Proof.
    split; [| split].
    - intros [HK _]. simpl in HK. discriminate.
    - apply detects_arr_char. intros p _. reflexivity.
    - split.
      + intro HK. simpl in HK. discriminate.
      + intros p Hpr _ [Hid _].
        rewrite !detects_arr_char in Hid.
        assert (Ha : allPos r (dropP p method_spec)).
        { apply (proj2 Hid). intros q _. reflexivity. }
        specialize (Ha p Hpr). simpl in Ha.
        rewrite Nat.eqb_refl in Ha. discriminate.
  Qed.

  Lemma object_spec_general_ok :
    0 < r ->
    proper r object_spec /\ detects object_spec KerQ /\ gwasteless object_spec.
  Proof.
    intro Hr.
    split; [| split].
    - intros [_ Hall]. specialize (Hall 0 Hr). simpl in Hall. discriminate.
    - apply detects_ker_char. reflexivity.
    - split.
      + intros _ [_ Hc].
        rewrite !detects_ker_char in Hc.
        assert (Hx : hasK (dropK object_spec) = true).
        { apply (proj2 Hc). reflexivity. }
        simpl in Hx. discriminate.
      + intros p _ Hpres. simpl in Hpres. discriminate.
  Qed.

End InformationDetection.

(* ===================================================================== *)
(* THE SYMMETRY INSTANCE: identity + commutativity.                      *)
(* all_same (every position holds the same array) has full relational    *)
(* support for r >= 2; commutativity is nonconstant.  Theorem 9.26 as    *)
(* previously stated is recovered as a corollary of the general theorem. *)
(* ===================================================================== *)

Section IdentityCommInstance.
  Variable r : nat.
  Hypothesis Hr2 : 2 <= r.

  Definition all_same (A : Args) : Prop :=
    forall p q, p < r -> q < r -> A p = A q.

  Lemma all_same_local :
    forall A A', (forall q, q < r -> A q = A' q) ->
    (all_same A <-> all_same A').
  Proof.
    intros A A' Hag; split; intros H p q Hp Hq.
    - rewrite <- (Hag p Hp), <- (Hag q Hq). apply H; assumption.
    - rewrite (Hag p Hp), (Hag q Hq). apply H; assumption.
  Qed.

  Lemma all_same_support :
    forall p, p < r ->
      exists A A',
        (forall q, q <> p -> A q = A' q) /\ all_same A /\ ~ all_same A'.
  Proof.
    intros p Hp.
    exists (fun _ => 0), (updpos p 1 (fun _ => 0)).
    split; [| split].
    - intros q Hq. unfold updpos.
      destruct (q =? p) eqn:E;
        [apply Nat.eqb_eq in E; congruence | reflexivity].
    - intros a b _ _. reflexivity.
    - intro H.
      set (q0 := if p =? 0 then 1 else 0).
      assert (Hq0p : q0 <> p).
      { unfold q0. destruct (p =? 0) eqn:E;
          [apply Nat.eqb_eq in E; lia | apply Nat.eqb_neq in E; lia]. }
      assert (Hq0r : q0 < r).
      { unfold q0. destruct (p =? 0); lia. }
      specialize (H p q0 Hp Hq0r).
      unfold updpos in H. rewrite Nat.eqb_refl in H.
      destruct (q0 =? p) eqn:E;
        [apply Nat.eqb_eq in E; congruence |].
      discriminate.
  Qed.

  Lemma comm_nonconst :
    exists f1 f2 : Kern, commutative f1 /\ ~ commutative f2.
  Proof.
    exists Nat.add, (fun x _ => x). split.
    - intros x y. apply Nat.add_comm.
    - intro Hc. specialize (Hc 0 1). simpl in Hc. discriminate.
  Qed.

  Corollary two_maximal_identity_comm :
    forall S : Spec,
      proper r S ->
      (detects r S (ArrQ all_same) \/ detects r S (KerQ commutative)) ->
      gwasteless r all_same commutative S ->
      (hasK S = true /\ (forall p, p < r -> hasPos S p = false))
      \/ (hasK S = false /\ allPos r S).
  Proof.
    apply (two_maximal_curryings_general r all_same
             all_same_local all_same_support commutative comm_nonconst).
  Qed.
End IdentityCommInstance.

(* ===================================================================== *)
(* Notes:                                                                *)
(*  - The two-ness of the maximal curryings rests on Blade's output-     *)
(*    relevant properties partitioning into exactly {full-support array  *)
(*    properties} and {kernel properties}.  A property drawing on the    *)
(*    kernel AND a proper subset of positions jointly (a "mixed source") *)
(*    would make a THIRD spec non-wasteful.  Whether Blade's semantics   *)
(*    ever surfaces such a property (equivariance checking against a     *)
(*    group carried by one array's index type is a candidate) is an      *)
(*    open design question; the general theorem localizes exactly what   *)
(*    would have to be true for a third combinator to earn existence.    *)
(*  - Lowering/raising theorems (9.9-9.22) enter as the next layer:      *)
(*    BladeCore.v already holds r=2 seeds (diagonal_swap_is_symmetry is  *)
(*    the 9.9/9.16 instance; access_exact is lower-1-0's consumption).   *)
(*    The general H-intersect-Stab forms need finite-permutation         *)
(*    machinery over [0, r).                                             *)
(* ===================================================================== *)
