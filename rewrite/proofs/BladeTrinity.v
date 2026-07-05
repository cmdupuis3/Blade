(* ===================================================================== *)
(* BladeTrinity.v -- Layer 3b: THE TRINITY, positive constructions.      *)
(*                                                                       *)
(* The Trinity theorems (9.1-9.7) claim mutual dependence of loop        *)
(* reification, arity polymorphism, and dimensional currying.  The       *)
(* honest mechanizable content is (i) the positive constructions and     *)
(* (ii) sharp non-constancy anchors; the "requires" directions over all  *)
(* languages remain prose, now grounded on these.                        *)
(*                                                                       *)
(*   enumShape_app         the applicative combination <*> of loops IS   *)
(*                         shape concatenation: tuples of the combined   *)
(*                         loop are exactly the pairwise concatenations  *)
(*                         (positive half of Theorem 9.6 / 12.8)         *)
(*   enumShape_app_length  cardinality is multiplicative under <*>       *)
(*   trinity_fold_closure  the FOLD of <*> over any list of shapes       *)
(*                         builds the arbitrary-arity loop: arity        *)
(*                         polymorphism by construction (9.1/9.6)        *)
(*   nat_not_arrow         Cantor: nat <> (nat -> nat)                   *)
(*   output_family_not_constant   the arity-indexed output type family   *)
(*                         OutT is not a constant family -- no single    *)
(*                         non-dependent type hosts all arities; the     *)
(*                         type-level anchor for Theorems 9.2/9.5,       *)
(*                         mirroring residual_not_constant (BladeDMWF)   *)
(*                         one level up                                  *)
(*                                                                       *)
(* Imports BladeDMWF.  Coq 8.18, stdlib only.                            *)
(* ===================================================================== *)

From Blade Require Import BladeDMWF.
Require Import List Arith Lia.
Import ListNotations.

(* Helper: fold_right mult distributes over app. *)
Lemma fold_mul_app : forall l1 l2 : list nat,
  fold_right Nat.mul 1 (l1 ++ l2)
  = fold_right Nat.mul 1 l1 * fold_right Nat.mul 1 l2.
Proof.
  induction l1 as [|a l1 IH]; intros; simpl; [ring |].
  rewrite IH. ring.
Qed.

(* ===================================================================== *)
(* THE APPLICATIVE COMBINATION: <*> is shape concatenation.              *)
(* ===================================================================== *)

Theorem enumShape_app : forall (s1 s2 : Shape) (t : list nat),
  In t (enumShape (s1 ++ s2)) <->
  exists t1 t2,
    t = t1 ++ t2 /\ In t1 (enumShape s1) /\ In t2 (enumShape s2).
Proof.
  induction s1 as [|c s1 IH]; intros s2 t; simpl.
  - split.
    + intro H. exists [], t.
      split; [reflexivity | split; [left; reflexivity | exact H]].
    + intros (t1 & t2 & Et & H1 & H2).
      destruct H1 as [E1 | []]. subst t1. simpl in Et. subst t. exact H2.
  - split.
    + intro H. apply in_flat_map in H as (th & Hth & Hm).
      apply in_map_iff in Hm as (tr & Etr & Htr).
      apply IH in Htr as (t1 & t2 & Et12 & H1 & H2).
      exists (th ++ t1), t2.
      split; [| split].
      * subst. rewrite app_assoc. reflexivity.
      * apply in_flat_map. exists th. split; [exact Hth |].
        apply in_map_iff. exists t1. split; [reflexivity | exact H1].
      * exact H2.
    + intros (t1 & t2 & Et & H1 & H2).
      apply in_flat_map in H1 as (th & Hth & Hm).
      apply in_map_iff in Hm as (tr & Etr & Htr).
      apply in_flat_map. exists th. split; [exact Hth |].
      apply in_map_iff. exists (tr ++ t2).
      split.
      * subst. rewrite app_assoc. reflexivity.
      * apply IH. exists tr, t2.
        split; [reflexivity | split; [exact Htr | exact H2]].
Qed.

(* Cardinality is multiplicative under <*>. *)
Theorem enumShape_app_length : forall s1 s2 : Shape,
  length (enumShape (s1 ++ s2))
  = length (enumShape s1) * length (enumShape s2).
Proof.
  intros. rewrite !enumShape_length, map_app, fold_mul_app. reflexivity.
Qed.

(* ===================================================================== *)
(* THE FOLD CLOSURE: arity polymorphism by construction.                 *)
(* method_for(A1, ..., An) = fold of <*> over the per-array loops; its   *)
(* tuples are exactly the concatenations of per-shape canonical tuples,  *)
(* for ANY number of shapes.  This is the arbitrary-arity iteration      *)
(* object the T/S fixed-syntax argument (2.5-2.7) says cannot be a       *)
(* single fixed loop nest.                                               *)
(* ===================================================================== *)

Theorem trinity_fold_closure : forall (shapes : list Shape) (t : list nat),
  In t (enumShape (concat shapes)) <->
  exists parts : list (list nat),
    t = concat parts /\
    length parts = length shapes /\
    Forall2 (fun s p => In p (enumShape s)) shapes parts.
Proof.
  induction shapes as [|a shapes IH]; intros t; simpl.
  - split.
    + intro H. destruct H as [E | []]. subst t.
      exists []. split; [reflexivity | split; [reflexivity | constructor]].
    + intros (parts & Et & Hlen & HF).
      destruct parts as [|p ps]; [| inversion HF].
      subst t. simpl. left. reflexivity.
  - split.
    + intro H. apply enumShape_app in H as (t1 & t2 & Et & H1 & H2).
      apply IH in H2 as (parts & Et2 & Hlen & HF).
      exists (t1 :: parts).
      split; [| split].
      * simpl. subst. reflexivity.
      * simpl. rewrite Hlen. reflexivity.
      * constructor; assumption.
    + intros (parts & Et & Hlen & HF).
      destruct parts as [|p ps]; [simpl in Hlen; discriminate |].
      simpl in Hlen. injection Hlen as Hlen.
      inversion HF as [| x y l l' Hp Hps]; subst.
      apply enumShape_app. exists p, (concat ps).
      split; [| split].
      * simpl. reflexivity.
      * exact Hp.
      * apply IH. exists ps.
        split; [reflexivity | split; [exact Hlen | exact Hps]].
Qed.

(* ===================================================================== *)
(* THE ARITY-INDEXED OUTPUT FAMILY (anchors for Theorems 9.2/9.5).       *)
(* OutT T r is the r-ary curried output type.  It is NOT a constant      *)
(* family: nat <> (nat -> nat), by Cantor's diagonal through the         *)
(* transport functions of a hypothetical type equality.  Hence no        *)
(* single non-dependent type hosts all arities -- the output type must   *)
(* be a family indexed by a TERM (the arity), which is dependent typing. *)
(* This is residual_not_constant lifted from cardinalities to types.     *)
(* ===================================================================== *)

Lemma rew_rt : forall (X Y : Set) (e : X = Y) (y : Y),
  eq_rect X (fun T : Set => T)
          (eq_rect Y (fun T : Set => T) y X (eq_sym e)) Y e = y.
Proof. intros X Y e y. destruct e. reflexivity. Qed.

Theorem nat_not_arrow : ~ (@eq Set nat (nat -> nat)).
Proof.
  intro e.
  pose (f := fun x : nat => eq_rect nat (fun T : Set => T) x (nat -> nat) e).
  pose (g := fun h : nat -> nat =>
               eq_rect (nat -> nat) (fun T : Set => T) h nat (eq_sym e)).
  assert (fg : forall h, f (g h) = h) by (intro h; apply rew_rt).
  pose (d := fun x : nat => S (f x x)).
  assert (Ed : f (g d) = d) by apply fg.
  assert (Contra : d (g d) = S (d (g d))).
  { transitivity (S (f (g d) (g d))); [reflexivity |].
    rewrite Ed. reflexivity. }
  lia.
Qed.

Fixpoint OutT (T : Set) (r : nat) : Set :=
  match r with
  | 0 => T
  | S r' => nat -> OutT T r'
  end.

Theorem output_family_not_constant : OutT nat 0 <> OutT nat 1.
Proof. simpl. apply nat_not_arrow. Qed.

(* ===================================================================== *)
(* Trinity ledger (what is checked vs. prose):                           *)
(*  - Loop reification (9.1/9.4): enum / enumShape / enumA are           *)
(*    first-class mathematical objects with laws -- the positive         *)
(*    existence half.  "T/S cannot have them" remains the prose          *)
(*    argument of 2.3/2.10, anchored by tdim_relationality.              *)
(*  - Arity polymorphism (9.6): trinity_fold_closure is the positive     *)
(*    construction; the fixed-syntax impossibility (2.5-2.7) remains     *)
(*    prose.                                                             *)
(*  - Dimensional currying (9.2/9.3/9.5): residual_not_constant          *)
(*    (value level, BladeDMWF) and output_family_not_constant (type      *)
(*    level, here) are the checked anchors: both the residual and the    *)
(*    output are genuinely dependent families.                           *)
(*  - Inseparability (9.7): the bundle of the above; the pairwise        *)
(*    "requires" directions are design-space arguments, not theorems     *)
(*    of this development.                                               *)
(* ===================================================================== *)
