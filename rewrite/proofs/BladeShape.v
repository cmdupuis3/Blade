(* ===================================================================== *)
(* BladeShape.v -- shape-level uniqueness.                               *)
(*                                                                       *)
(* Closes the last enumShape gap: membership was characterized by        *)
(* trinity_fold_closure (sound + complete via the Forall2                *)
(* decomposition); this file adds UNIQUENESS -- enumShape enumerates     *)
(* each tuple exactly once.  The key fact is that canonical tuples of a  *)
(* record have fixed length (canonical_length), so app-decompositions    *)
(* against a shape are unique (app_split_length).                        *)
(*                                                                       *)
(* Imports BladeDMWF.  Coq 8.18, stdlib only.                            *)
(* ===================================================================== *)

From Blade Require Import BladeDMWF.
Require Import List Arith Lia.
Import ListNotations.

Lemma canonical_length : forall r l u t,
  canonical r l u t -> length t = r.
Proof.
  induction r as [|r IH]; intros l u t H;
    destruct t as [|i t']; simpl in H; try contradiction.
  - reflexivity.
  - destruct H as [_ H]. simpl. f_equal. eapply IH. exact H.
Qed.

Lemma app_split_length : forall (A : Type) (t1 t2 x1 x2 : list A),
  length t1 = length t2 -> t1 ++ x1 = t2 ++ x2 -> t1 = t2.
Proof.
  induction t1 as [|a t1 IH]; intros t2 x1 x2 HL HE;
    destruct t2 as [|b t2]; simpl in *; try discriminate.
  - reflexivity.
  - injection HE as -> HE. injection HL as HL.
    f_equal. eapply IH; eauto.
Qed.

Theorem enumShape_NoDup : forall s : Shape, NoDup (enumShape s).
Proof.
  induction s as [|c s IH]; simpl.
  - constructor; [simpl; tauto | constructor].
  - apply NoDup_flat_map.
    + apply enum_NoDup.
    + intros t _.
      apply NoDup_map_inj; [exact IH |].
      intros x y _ _ E. exact (app_inv_head _ _ _ E).
    + intros t1 t2 b Ht1 Ht2 Hb1 Hb2.
      apply in_map_iff in Hb1 as (x1 & E1 & _).
      apply in_map_iff in Hb2 as (x2 & E2 & _).
      apply enum_sound in Ht1. apply enum_sound in Ht2.
      apply canonical_length in Ht1. apply canonical_length in Ht2.
      assert (L : length t1 = length t2) by congruence.
      rewrite <- E1 in E2.
      exact (app_split_length _ t1 t2 x1 x2 L (eq_sym E2)).
Qed.
