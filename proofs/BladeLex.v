(* ===================================================================== *)
(* BladeLex.v -- LEX-SORTEDNESS: iteration order = storage order.        *)
(*                                                                       *)
(* Closes the last enumeration-order item.  The theorem is proved ONCE   *)
(* at the arrow level: any arrow whose heads are strictly increasing     *)
(* enumerates its tuples in strictly increasing lexicographic order      *)
(* (enumA_lex_sorted).  Every instance then inherits it:                 *)
(*   enum_lex_sorted        the symmetric enumeration (via the list      *)
(*                          EQUALITY sym_arrow_correct)                  *)
(*   affine_lex_sorted      the affine arrow (hence Antisym at delta=1)  *)
(*   compoundk_lex_sorted   the rank-k Compound arrow (filtered seq      *)
(*                          heads stay sorted) -- the order-agreement    *)
(*                          customer from BladeCompound: the semantic    *)
(*                          enumeration is lex, matching the compiler's  *)
(*                          dense scan order over mask cells             *)
(*                                                                       *)
(* Payoff (enum_offset_respects_lex): earlier enumeration position       *)
(* implies lex-smaller tuple.  Since the storage offset IS the           *)
(* enumeration position (BladeDMWF's bijections), offset order embeds    *)
(* lex order; with NoDup the embedding is strict.  The converse          *)
(* direction (lex-smaller implies earlier) follows from trichotomy of    *)
(* lexlt on equal-length tuples plus NoDup and is routine; it is left    *)
(* as a remark since no downstream theorem consumes it.                  *)
(*                                                                       *)
(* Imports BladeDMWF, BladeArrow, BladeAffine, BladeCompound.            *)
(* Coq 8.18, stdlib only.                                                *)
(* ===================================================================== *)

From Blade Require Import BladeDMWF BladeArrow BladeAffine BladeCompound.
Require Import List Arith Lia Sorted Setoid.
Import ListNotations.

(* Strict lexicographic order on index tuples. *)
Inductive lexlt : list nat -> list nat -> Prop :=
| lexlt_nil  : forall y t, lexlt [] (y :: t)
| lexlt_head : forall x y s t, x < y -> lexlt (x :: s) (y :: t)
| lexlt_tail : forall x s t, lexlt s t -> lexlt (x :: s) (x :: t).

(* ---------------- StronglySorted plumbing ---------------- *)

Lemma Forall_app_i : forall (A : Type) (P : A -> Prop) (l1 l2 : list A),
  Forall P l1 -> Forall P l2 -> Forall P (l1 ++ l2).
Proof.
  induction l1 as [|a l1 IH]; intros l2 H1 H2; simpl; [exact H2 |].
  constructor; [exact (Forall_inv H1) |].
  apply IH; [exact (Forall_inv_tail H1) | exact H2].
Qed.

Lemma SS_app : forall (A : Type) (R : A -> A -> Prop) (l1 l2 : list A),
  StronglySorted R l1 -> StronglySorted R l2 ->
  (forall x y, In x l1 -> In y l2 -> R x y) ->
  StronglySorted R (l1 ++ l2).
Proof.
  intros A R l1 l2 H1.
  induction H1 as [|a l1 Hs IH HF]; intros H2 Hx; simpl; [exact H2 |].
  constructor.
  - apply IH; [exact H2 |].
    intros x y Hx1 Hy. apply Hx; [right; exact Hx1 | exact Hy].
  - apply Forall_app_i; [exact HF |].
    apply Forall_forall. intros y Hy.
    apply Hx; [left; reflexivity | exact Hy].
Qed.

Lemma SS_flat_map :
  forall (A B : Type) (RA : A -> A -> Prop) (RB : B -> B -> Prop)
         (f : A -> list B) (l : list A),
  StronglySorted RA l ->
  (forall x, In x l -> StronglySorted RB (f x)) ->
  (forall x y, In x l -> In y l -> RA x y ->
     forall u v, In u (f x) -> In v (f y) -> RB u v) ->
  StronglySorted RB (flat_map f l).
Proof.
  intros A B RA RB f l Hl.
  induction Hl as [|a l Hs IH HF]; intros Hb Hc; simpl; [constructor |].
  apply SS_app.
  - apply Hb. left. reflexivity.
  - apply IH.
    + intros x Hxl. apply Hb. right. exact Hxl.
    + intros x y Hxl Hyl HR. apply Hc; [right; exact Hxl | right; exact Hyl | exact HR].
  - intros u v Hu Hv.
    apply in_flat_map in Hv as (y & Hy & Hvy).
    rewrite Forall_forall in HF.
    exact (Hc a y (or_introl eq_refl) (or_intror Hy) (HF y Hy) u v Hu Hvy).
Qed.

Lemma SS_map_cons : forall (i : nat) (L : list (list nat)),
  StronglySorted lexlt L -> StronglySorted lexlt (map (cons i) L).
Proof.
  intros i L H.
  induction H as [|x L Hs IH HF]; simpl; [constructor |].
  constructor; [exact IH |].
  rewrite Forall_forall in HF.
  apply Forall_forall. intros z Hz.
  apply in_map_iff in Hz as (w & <- & Hw).
  apply lexlt_tail. exact (HF w Hw).
Qed.

Lemma SS_seq : forall n start, StronglySorted lt (seq start n).
Proof.
  induction n as [|n IH]; intros start; simpl; constructor.
  - apply IH.
  - apply Forall_forall. intros x Hx. apply in_seq in Hx. lia.
Qed.

Lemma SS_filter : forall (A : Type) (R : A -> A -> Prop) (f : A -> bool) l,
  StronglySorted R l -> StronglySorted R (filter f l).
Proof.
  intros A R f l H.
  induction H as [|a l Hs IH HF]; simpl; [constructor |].
  destruct (f a); [| exact IH].
  constructor; [exact IH |].
  rewrite Forall_forall in HF.
  apply Forall_forall. intros x Hx.
  apply filter_In in Hx as [Hx _]. exact (HF x Hx).
Qed.

(* ---------------- THE GENERAL THEOREM ---------------- *)

Theorem enumA_lex_sorted :
  forall (St : Type) (heads : St -> list nat) (step : St -> nat -> St),
  (forall s, StronglySorted lt (heads s)) ->
  forall r s, StronglySorted lexlt (enumA St heads step r s).
Proof.
  intros St heads step Hh.
  induction r as [|r IH]; intro s; simpl.
  - repeat constructor.
  - apply (SS_flat_map nat (list nat) lt lexlt).
    + apply Hh.
    + intros i _. apply SS_map_cons. apply IH.
    + intros i j _ _ Hij u v Hu Hv.
      apply in_map_iff in Hu as (su & <- & _).
      apply in_map_iff in Hv as (sv & <- & _).
      apply lexlt_head. exact Hij.
Qed.

(* ---------------- INSTANCES ---------------- *)

Corollary enum_lex_sorted : forall r l u,
  StronglySorted lexlt (enum r l u).
Proof.
  intros. rewrite <- (sym_arrow_correct u r l).
  apply enumA_lex_sorted. intro s. apply SS_seq.
Qed.

Corollary affine_lex_sorted : forall u delta r l,
  StronglySorted lexlt (enumA nat (d_heads u) (d_step delta) r l).
Proof.
  intros. apply enumA_lex_sorted. intro s. apply SS_seq.
Qed.

Corollary compoundk_lex_sorted : forall (M : list nat -> bool) r s,
  StronglySorted lexlt (enumA ckSt (ck_heads M) ck_step r s).
Proof.
  intros. apply enumA_lex_sorted.
  intros (ds, pf). destruct ds as [|n ds]; simpl.
  - constructor.
  - apply SS_filter. apply SS_seq.
Qed.

(* ---------------- THE PAYOFF ---------------- *)

Lemma SS_nth : forall (A : Type) (R : A -> A -> Prop) (l : list A),
  StronglySorted R l ->
  forall i j d, i < j -> j < length l -> R (nth i l d) (nth j l d).
Proof.
  intros A R l H.
  induction H as [|a l Hs IH HF]; intros i j d Hij Hj;
    simpl in Hj; [lia |].
  destruct i as [|i']; destruct j as [|j']; try lia; simpl.
  - rewrite Forall_forall in HF. apply HF. apply nth_In. lia.
  - apply IH; lia.
Qed.

(* Earlier enumeration position => lex-smaller tuple.  Storage offset    *)
(* is enumeration position, so offset order embeds lex order.            *)
Corollary enum_offset_respects_lex : forall r l u i j d,
  i < j -> j < length (enum r l u) ->
  lexlt (nth i (enum r l u) d) (nth j (enum r l u) d).
Proof.
  intros. apply SS_nth; [apply enum_lex_sorted | assumption | assumption].
Qed.
