(* ===================================================================== *)
(* BladeAffine.v -- Layer 1.5c: THE AFFINE FEEDBACK DESCRIPTOR.          *)
(*                                                                       *)
(* Closes the deferred lj/alj unification.  The symmetric and strict     *)
(* arrows differ only in the offset their feedback induces:              *)
(*   Sym      step l i = i        (delta = 0)                            *)
(*   Antisym  step l i = i + 1    (delta = 1)                            *)
(* The affine arrow step l i = i + delta generalizes both; its storage   *)
(* transform is a_{k+1} = i_{k+1} - i_k - delta with the domain          *)
(*   l + sum(a) + delta * (r - 1) < u                                    *)
(* (the delta * (r - 1) term is the general StrictOffset).  The round    *)
(* trips are proved ONCE, and the delta = 0 / delta = 1 corollaries      *)
(* recover BladeDMWF's canonical/storageOK and BladeArrow's              *)
(* scanonical/astorageOK exactly -- lj and alj become instances.         *)
(* Also covers hypothetical strided-strict index types (delta >= 2)      *)
(* for free.                                                             *)
(*                                                                       *)
(* Proof note: delta * (r - 1) is nonlinear in two variables, so the     *)
(* previous instances' pure-lia proofs do not transfer; the degree-2     *)
(* arithmetic leaves are discharged by nia (after eliminating the tail   *)
(* sum in the rank-zero branch).                                         *)
(*                                                                       *)
(* Imports BladeDMWF, BladeArrow.  Coq 8.18, stdlib only.                *)
(* ===================================================================== *)

From Blade Require Import BladeDMWF BladeArrow.
Require Import List Arith Lia Setoid.
Import ListNotations.

Section AffineArrow.
  Variables u delta : nat.

  Definition d_heads (l : nat) : list nat := seq l (u - l).
  Definition d_step (l i : nat) : nat := i + delta.

  Fixpoint dcanonical (r l : nat) (t : list nat) : Prop :=
    match r, t with
    | 0, [] => True
    | S r', i :: t' => l <= i < u /\ dcanonical r' (i + delta) t'
    | _, _ => False
    end.

  Theorem affine_arrow_correct : forall r l t,
    canonA nat d_heads d_step r l t <-> dcanonical r l t.
  Proof.
    induction r as [|r IH]; intros l t;
      destruct t as [|i t']; simpl; try tauto.
    unfold d_heads. rewrite in_seq.
    try change (d_step l i) with (i + delta).
    rewrite (IH (i + delta) t').
    split; intros [Ha Hb]; (split; [lia | assumption]).
  Qed.

  Corollary affine_enum_NoDup : forall r l,
    NoDup (enumA nat d_heads d_step r l).
  Proof. intros. apply enumA_NoDup. intro s. apply NoDup_seq. Qed.

  (* -------- the affine left-justified storage bijection -------------- *)

  Fixpoint dlj (prev : nat) (t : list nat) : list nat :=
    match t with [] => [] | i :: t' => (i - prev) :: dlj (i + delta) t' end.
  Fixpoint dunlj (prev : nat) (c : list nat) : list nat :=
    match c with
    | [] => []
    | a :: c' => (prev + a) :: dunlj (prev + a + delta) c'
    end.

  Definition dstorageOK (r l : nat) (c : list nat) : Prop :=
    length c = r /\ (r = 0 \/ l + lsum c + delta * (r - 1) < u).

  Theorem dlj_correct : forall r l t,
    dcanonical r l t ->
    dstorageOK r l (dlj l t) /\ dunlj l (dlj l t) = t.
  Proof.
    induction r as [|r IH]; intros l t Hc;
      destruct t as [|i t']; simpl in Hc; try contradiction.
    - simpl. unfold dstorageOK. simpl.
      split; [split; [reflexivity | left; reflexivity] | reflexivity].
    - destruct Hc as [Hb Hc].
      destruct (IH (i + delta) t' Hc) as [[Hlen Hbd] Hrt].
      unfold dstorageOK in *. simpl.
      split; [split |].
      + rewrite Hlen. reflexivity.
      + right.
        destruct Hbd as [Hr0 | Hs].
        * rewrite Hr0 in Hlen. rewrite (lsum_zero_len _ Hlen). nia.
        * nia.
      + replace (l + (i - l)) with i by lia. rewrite Hrt. reflexivity.
  Qed.

  Theorem dunlj_correct : forall r l c,
    dstorageOK r l c ->
    dcanonical r l (dunlj l c) /\ dlj l (dunlj l c) = c.
  Proof.
    induction r as [|r IH]; intros l c [Hlen Hb];
      destruct c as [|a c']; simpl in Hlen; try discriminate.
    - simpl. split; [exact I | reflexivity].
    - injection Hlen as Hlen.
      assert (Hau : l + a < u).
      { destruct Hb as [E | Hb]; [discriminate | simpl in Hb; lia]. }
      assert (Hb' : r = 0 \/ (l + a + delta) + lsum c' + delta * (r - 1) < u).
      { destruct r as [|r'']; [left; reflexivity | right].
        destruct Hb as [E | Hb]; [discriminate |].
        simpl in Hb. nia. }
      destruct (IH (l + a + delta) c' (conj Hlen Hb')) as [Hcan Hlj].
      simpl. split.
      + split; [lia | exact Hcan].
      + replace (l + a - l) with a by lia. rewrite Hlj. reflexivity.
  Qed.
End AffineArrow.

(* ===================================================================== *)
(* THE INSTANCES: delta = 0 is the symmetric arrow, delta = 1 the        *)
(* strict one -- lj and alj are subsumed.                                *)
(* ===================================================================== *)

Corollary delta0_recovers_canonical : forall u r l t,
  dcanonical u 0 r l t <-> canonical r l u t.
Proof.
  intro u. induction r as [|r IH]; intros l t;
    destruct t as [|i t']; simpl; try tauto.
  rewrite Nat.add_0_r. rewrite (IH i t'). tauto.
Qed.

Corollary delta1_recovers_scanonical : forall u r l t,
  dcanonical u 1 r l t <-> scanonical u r l t.
Proof.
  intro u. induction r as [|r IH]; intros l t;
    destruct t as [|i t']; simpl; try tauto.
  rewrite Nat.add_1_r. rewrite (IH (S i) t'). tauto.
Qed.

Corollary delta0_recovers_storageOK : forall u r l c,
  dstorageOK u 0 r l c <-> storageOK r l u c.
Proof.
  intros. unfold dstorageOK, storageOK.
  rewrite Nat.mul_0_l, Nat.add_0_r. tauto.
Qed.

Corollary delta1_recovers_astorageOK : forall u r l c,
  dstorageOK u 1 r l c <-> astorageOK u r l c.
Proof.
  intros. unfold dstorageOK, astorageOK.
  rewrite Nat.mul_1_l. tauto.
Qed.
