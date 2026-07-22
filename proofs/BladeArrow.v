(* ===================================================================== *)
(* BladeArrow.v -- Layer 1.5: THE ARROW AS A COALGEBRA.                  *)
(*                                                                       *)
(* An arrow is the maximal dependence-closed unit of iteration.  Its     *)
(* general form is a coalgebra: a state type St, a heads function        *)
(* (which first indices are valid in the current state), and a step      *)
(* function (the residual state after emitting an index).  The DMWF is   *)
(* the induced unfold; the FEEDBACK is the step.                         *)
(*                                                                       *)
(* General theorems (any arrow):                                         *)
(*   arrow_dmwf_equation, enumA_sound, enumA_complete, enumA_NoDup       *)
(*   const_state_rectilinear -- trivial feedback <=> rectilinear         *)
(*     domain (the general form of BladeCurrying's dichotomy: constant   *)
(*     residual is exactly what factors into separate arrows)            *)
(*                                                                       *)
(* Instances:                                                            *)
(*   Sym      (step l i = i)     -- proved EQUAL to BladeDMWF's enum     *)
(*   Antisym  (step l i = S i)   -- the compiler's StrictOffset; strict  *)
(*     canonical characterization + sound/complete/NoDup corollaries +   *)
(*     the strict left-justified storage bijection (alj/aunlj)           *)
(*   Compound (mask-conditioned) -- the arrow enumerates exactly the     *)
(*     true cells of the mask (CompoundIdx denotation, rank 2)           *)
(*                                                                       *)
(* Imports BladeDMWF.  Coq 8.18, stdlib only.                            *)
(* ===================================================================== *)

From Blade Require Import BladeDMWF.
Require Import List Arith Lia Bool Setoid.
Import ListNotations.

(* Helper: pointwise-equal flat_map. *)
Lemma flat_map_ext : forall (A B : Type) (f g : A -> list B) (l : list A),
  (forall x, In x l -> f x = g x) -> flat_map f l = flat_map g l.
Proof.
  induction l as [|a l IH]; intros H; simpl; [reflexivity |].
  rewrite (H a (or_introl eq_refl)), IH; [reflexivity |].
  intros x Hx. apply H. right. exact Hx.
Qed.

(* ===================================================================== *)
(* THE GENERAL ARROW.                                                    *)
(* ===================================================================== *)

Section ArrowCoalgebra.
  Variable St : Type.
  Variable heads : St -> list nat.
  Variable step : St -> nat -> St.

  Fixpoint enumA (r : nat) (s : St) : list (list nat) :=
    match r with
    | 0 => [ [] ]
    | S r' => flat_map (fun i => map (cons i) (enumA r' (step s i))) (heads s)
    end.

  Fixpoint canonA (r : nat) (s : St) (t : list nat) : Prop :=
    match r, t with
    | 0, [] => True
    | S r', i :: t' => In i (heads s) /\ canonA r' (step s i) t'
    | _, _ => False
    end.

  (* The DMWF equation in coalgebraic generality: emit i (index-ana),   *)
  (* recurse on the residual state (index-cata); the feedback is step.  *)
  Theorem arrow_dmwf_equation : forall r s,
    enumA (S r) s
    = flat_map (fun i => map (cons i) (enumA r (step s i))) (heads s).
  Proof. reflexivity. Qed.

  Theorem enumA_sound : forall r s t, In t (enumA r s) -> canonA r s t.
  Proof.
    induction r as [|r IH]; intros s t H; simpl in H.
    - destruct H as [H | []]. subst t. exact I.
    - apply in_flat_map in H as (i & Hi & Ht).
      apply in_map_iff in Ht as (t' & Et & Ht').
      subst t. simpl. split; [exact Hi | apply IH; exact Ht'].
  Qed.

  Theorem enumA_complete : forall r s t, canonA r s t -> In t (enumA r s).
  Proof.
    induction r as [|r IH]; intros s t Hc;
      destruct t as [|i t']; simpl in Hc; try contradiction.
    - left; reflexivity.
    - destruct Hc as [Hi Hc]. simpl.
      apply in_flat_map. exists i. split; [exact Hi |].
      apply in_map_iff. exists t'. split; [reflexivity | apply IH; exact Hc].
  Qed.

  Hypothesis heads_NoDup : forall s, NoDup (heads s).

  Theorem enumA_NoDup : forall r s, NoDup (enumA r s).
  Proof.
    induction r as [|r IH]; intros; simpl.
    - constructor; [simpl; tauto | constructor].
    - apply NoDup_flat_map.
      + apply heads_NoDup.
      + intros i _.
        apply NoDup_map_inj; [apply IH | intros x y _ _ E; injection E; auto].
      + intros x y b _ _ Hbx Hby.
        apply in_map_iff in Hbx as (tx & Ex & _).
        apply in_map_iff in Hby as (ty & Ey & _).
        rewrite <- Ex in Ey. injection Ey; intros; congruence.
  Qed.

  (* ------------------------------------------------------------------ *)
  (* THE DEPENDENCE DICHOTOMY, general form: an arrow with TRIVIAL       *)
  (* feedback (constant state) has a rectilinear domain -- membership    *)
  (* is per-dimension independent, so the arrow factors (RectIdx is      *)
  (* redundant in general).  Non-trivial feedback is exactly what fuses  *)
  (* dimensions into one arrow (BladeCurrying's                          *)
  (* sym_arrow_not_factorable is the Sym witness).                       *)
  (* ------------------------------------------------------------------ *)
  Theorem const_state_rectilinear :
    (forall s i, step s i = s) ->
    forall r s t,
      canonA r s t <-> (length t = r /\ Forall (fun i => In i (heads s)) t).
  Proof.
    intros Hconst.
    induction r as [|r IH]; intros s t; destruct t as [|i t']; simpl.
    - split; [intros _; split; [reflexivity | constructor] | intros _; exact I].
    - split; [intros [] | intros [Hlen _]; discriminate].
    - split; [intros [] | intros [Hlen _]; discriminate].
    - rewrite (Hconst s i). rewrite (IH s t'). split.
      + intros [Hi [Hlen HF]].
        split; [rewrite Hlen; reflexivity | constructor; assumption].
      + intros [Hlen HF]. injection Hlen as Hlen.
        split; [exact (Forall_inv HF) |
                split; [exact Hlen | exact (Forall_inv_tail HF)]].
  Qed.
End ArrowCoalgebra.

(* ===================================================================== *)
(* INSTANCE 1: THE SYMMETRIC ARROW (step l i = i).                       *)
(* Proved EQUAL (as a list, not merely extensionally) to BladeDMWF's     *)
(* enum: the concrete Layer-1 development is the Sym instance of the     *)
(* coalgebra.                                                            *)
(* ===================================================================== *)

Section SymInstance.
  Variable u : nat.
  Definition sym_heads (l : nat) : list nat := seq l (u - l).
  Definition sym_step (l i : nat) : nat := i.

  Theorem sym_arrow_correct : forall r l,
    enumA nat sym_heads sym_step r l = enum r l u.
  Proof.
    induction r as [|r IH]; intros l; simpl; [reflexivity |].
    apply flat_map_ext. intros i _. f_equal. exact (IH i).
  Qed.
End SymInstance.

(* ===================================================================== *)
(* INSTANCE 2: THE ANTISYMMETRIC ARROW (step l i = S i).                 *)
(* The feedback is shifted by one -- the compiler's StrictOffset.  The   *)
(* general theorems specialize for free; scanonical characterizes the   *)
(* domain (strictly increasing tuples), and alj/aunlj is the strict     *)
(* left-justified storage bijection.                                     *)
(* ===================================================================== *)

Section AntisymInstance.
  Variable u : nat.
  Definition anti_heads (l : nat) : list nat := seq l (u - l).
  Definition anti_step (l i : nat) : nat := S i.

  Fixpoint scanonical (r l : nat) (t : list nat) : Prop :=
    match r, t with
    | 0, [] => True
    | S r', i :: t' => l <= i < u /\ scanonical r' (S i) t'
    | _, _ => False
    end.

  Theorem antisym_arrow_correct : forall r l t,
    canonA nat anti_heads anti_step r l t <-> scanonical r l t.
  Proof.
    induction r as [|r IH]; intros l t;
      destruct t as [|i t']; simpl; try tauto.
    unfold anti_heads. rewrite in_seq.
    try change (anti_step l i) with (S i).
    rewrite (IH (S i) t').
    split; intros [Ha Hb]; (split; [lia | assumption]).
  Qed.

  Corollary antisym_enum_sound : forall r l t,
    In t (enumA nat anti_heads anti_step r l) -> scanonical r l t.
  Proof.
    intros r l t H. apply (proj1 (antisym_arrow_correct r l t)).
    apply enumA_sound. exact H.
  Qed.

  Corollary antisym_enum_complete : forall r l t,
    scanonical r l t -> In t (enumA nat anti_heads anti_step r l).
  Proof.
    intros r l t H. apply enumA_complete.
    apply (proj2 (antisym_arrow_correct r l t)). exact H.
  Qed.

  Corollary antisym_enum_NoDup : forall r l,
    NoDup (enumA nat anti_heads anti_step r l).
  Proof. intros. apply enumA_NoDup. intro s. apply NoDup_seq. Qed.

  (* -------- the STRICT left-justified storage bijection -------------- *)
  (* a1 = i1 - l; a_{k+1} = i_{k+1} - i_k - 1.  A strict r-tuple in      *)
  (* [l, u) exists iff l + sum(a) + (r - 1) < u: the r - 1 is the        *)
  (* StrictOffset.                                                       *)

  Fixpoint alj (prev : nat) (t : list nat) : list nat :=
    match t with [] => [] | i :: t' => (i - prev) :: alj (S i) t' end.
  Fixpoint aunlj (prev : nat) (c : list nat) : list nat :=
    match c with [] => [] | a :: c' => (prev + a) :: aunlj (S (prev + a)) c' end.

  Definition astorageOK (r l : nat) (c : list nat) : Prop :=
    length c = r /\ (r = 0 \/ l + lsum c + (r - 1) < u).

  Theorem alj_correct : forall r l t,
    scanonical r l t ->
    astorageOK r l (alj l t) /\ aunlj l (alj l t) = t.
  Proof.
    induction r as [|r IH]; intros l t Hc;
      destruct t as [|i t']; simpl in Hc; try contradiction.
    - simpl. unfold astorageOK. simpl.
      split; [split; [reflexivity | left; reflexivity] | reflexivity].
    - destruct Hc as [Hb Hc].
      destruct (IH (S i) t' Hc) as [[Hlen Hbd] Hrt].
      unfold astorageOK in *. simpl.
      split; [split |].
      + rewrite Hlen. reflexivity.
      + right. destruct Hbd as [Hr0 | Hs].
        * rewrite Hr0 in Hlen. rewrite (lsum_zero_len _ Hlen).
          rewrite Hr0. lia.
        * lia.
      + replace (l + (i - l)) with i by lia. rewrite Hrt. reflexivity.
  Qed.

  Theorem aunlj_correct : forall r l c,
    astorageOK r l c ->
    scanonical r l (aunlj l c) /\ alj l (aunlj l c) = c.
  Proof.
    induction r as [|r IH]; intros l c [Hlen Hb];
      destruct c as [|a c']; simpl in Hlen; try discriminate.
    - simpl. split; [exact I | reflexivity].
    - injection Hlen as Hlen.
      assert (Hau : l + a < u).
      { destruct Hb as [E | Hb]; [discriminate | simpl in Hb; lia]. }
      assert (Hb' : r = 0 \/ S (l + a) + lsum c' + (r - 1) < u).
      { destruct r as [|r'']; [left; reflexivity | right].
        destruct Hb as [E | Hb]; [discriminate | simpl in Hb; lia]. }
      destruct (IH (S (l + a)) c' (conj Hlen Hb')) as [Hcan Hlj].
      simpl. split.
      + split; [lia | exact Hcan].
      + replace (l + a - l) with a by lia. rewrite Hlj. reflexivity.
  Qed.
End AntisymInstance.

(* ===================================================================== *)
(* INSTANCE 3: THE COMPOUND (MASKED) ARROW.                              *)
(* State = the prefix chosen so far.  heads at the outer level are the   *)
(* rows with at least one true cell; heads inside row i are the true     *)
(* columns.  The arrow enumerates EXACTLY the true cells of the mask --  *)
(* the CompoundIdx denotation at rank 2.  The residual depends on the    *)
(* emitted value (via the mask), so by the dichotomy this arrow does     *)
(* not factor unless the mask is a product.                              *)
(* ===================================================================== *)

Section CompoundInstance.
  Variables n m : nat.
  Variable M : nat -> nat -> bool.

  Definition cSt := option nat.
  Definition c_heads (s : cSt) : list nat :=
    match s with
    | None => filter (fun i => existsb (M i) (seq 0 m)) (seq 0 n)
    | Some i => filter (M i) (seq 0 m)
    end.
  Definition c_step (s : cSt) (i : nat) : cSt :=
    match s with None => Some i | Some j => Some j end.

  Theorem compound_arrow_denotes_mask : forall i j,
    canonA cSt c_heads c_step 2 None [i; j] <->
    (i < n /\ j < m /\ M i j = true).
  Proof.
    intros i j. simpl. split.
    - intros (Hi & Hj & _).
      apply filter_In in Hi as [Hin _].
      apply filter_In in Hj as [Hjm HM].
      apply in_seq in Hin. apply in_seq in Hjm.
      repeat split; [lia | lia | exact HM].
    - intros (Hi & Hj & HM).
      split; [| split; [| exact I]].
      + apply filter_In. split; [apply in_seq; lia |].
        apply existsb_exists. exists j.
        split; [apply in_seq; lia | exact HM].
      + apply filter_In. split; [apply in_seq; lia | exact HM].
  Qed.
End CompoundInstance.

(* ===================================================================== *)
(* Notes:                                                                *)
(*  - The micro-level origin (a primitive for-loop wrapping an indexing  *)
(*    function) is the rank-1 arrow; loop-indexing fusion sits at the    *)
(*    bottom of the tower as the degenerate case.                        *)
(*  - The Sym storage bijection (lj, BladeDMWF) and the strict one       *)
(*    (alj, here) differ only in the offset the feedback induces;        *)
(*    a residual-parameterized common generalization is possible once    *)
(*    the arrow carries an affine feedback descriptor.                   *)
(*  - Rank-k Compound and mask-conditioned residual types (FilteredIdx)  *)
(*    generalize the rank-2 instance mechanically (state = prefix list). *)
(* ===================================================================== *)
