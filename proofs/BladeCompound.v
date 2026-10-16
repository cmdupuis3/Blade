(* ===================================================================== *)
(* BladeCompound.v -- Layer 1.5b: THE RANK-k COMPOUND ARROW.             *)
(*                                                                       *)
(* The rank-k masked (Compound) arrow.  A CompoundIdx over a       *)
(* rank-k mask M : list nat -> bool is the arrow whose state is          *)
(* (remaining dims, prefix chosen so far), whose heads at each level     *)
(* are the coordinates admitting SOME true completion, and whose step    *)
(* appends to the prefix.  has_completion is the mask-conditioned        *)
(* residual (FilteredIdx) in executable form: it IS the reason the       *)
(* Compound arrow's feedback is value-dependent, hence (by the           *)
(* dependence dichotomy) non-factorable unless the mask is a product.    *)
(*                                                                       *)
(* Checked:                                                              *)
(*   has_completion_witness   a valid completion certifies the residual  *)
(*   compoundk_sound          every enumerated tuple is in-bounds and    *)
(*                            mask-true (stated for nonempty dims: at    *)
(*                            empty dims canonA is vacuously True while  *)
(*                            M prefix may be false -- the base-case     *)
(*                            asymmetry noted in the ROADMAP design)     *)
(*   compoundk_complete       every in-bounds mask-true tuple is         *)
(*                            enumerated (no nonemptiness needed)        *)
(*   compoundk_NoDup          each exactly once                          *)
(*   compoundk_denotation     the arrow enumerates EXACTLY the true      *)
(*                            cells of the mask, at every POSITIVE rank  *)
(*                            (at rank zero the bare arrow enumerates    *)
(*                            [[]] even for the false mask --            *)
(*                            compound_rank_zero_ignores_mask)           *)
(*   compound_enum,           the rank-zero wrapper (tests M [] at no    *)
(*   compound_enum_denotation dims, the arrow otherwise): exactly the    *)
(*                            true cells at EVERY rank, zero included;   *)
(*                            compound_enum_NoDup                        *)
(*   rank2_subsumed           BladeArrow's concrete rank-2 instance is   *)
(*                            the [n; m] case of this arrow              *)
(*                                                                       *)
(* Imports BladeDMWF, BladeArrow.  Coq 8.18, stdlib only.                *)
(* ===================================================================== *)

From Blade Require Import BladeDMWF BladeArrow.
Require Import List Arith Lia Bool.
Import ListNotations.

Lemma NoDup_filter' : forall (A : Type) (f : A -> bool) (l : list A),
  NoDup l -> NoDup (filter f l).
Proof.
  intros A f l H. induction H as [|a l Hnotin H IH]; simpl; [constructor |].
  destruct (f a); [| exact IH].
  constructor; [| exact IH].
  intro Hin. apply filter_In in Hin as [Hin _]. contradiction.
Qed.

Section CompoundK.
  Variable M : list nat -> bool.

  (* The mask-conditioned residual: does the prefix admit any true       *)
  (* completion over the remaining dims?  This function IS FilteredIdx   *)
  (* in executable form.                                                 *)
  Fixpoint has_completion (dims prefix : list nat) : bool :=
    match dims with
    | [] => M prefix
    | n :: dims' =>
        existsb (fun i => has_completion dims' (prefix ++ [i])) (seq 0 n)
    end.

  Definition ckSt : Type := (list nat * list nat)%type.

  Definition ck_heads (s : ckSt) : list nat :=
    match s with
    | ([], _) => []
    | (n :: dims', prefix) =>
        filter (fun i => has_completion dims' (prefix ++ [i])) (seq 0 n)
    end.

  Definition ck_step (s : ckSt) (i : nat) : ckSt :=
    (tl (fst s), snd s ++ [i]).

  Definition in_bounds (t dims : list nat) : Prop :=
    Forall2 (fun i d => i < d) t dims.

  (* A valid completion certifies the residual. *)
  Lemma has_completion_witness : forall dims prefix t,
    in_bounds t dims ->
    M (prefix ++ t) = true ->
    has_completion dims prefix = true.
  Proof.
    induction dims as [|n dims IH]; intros prefix t HF HM.
    - inversion HF; subst. simpl. rewrite app_nil_r in HM. exact HM.
    - inversion HF as [| i d t' dims0 Hi Ht]; subst.
      simpl. apply existsb_exists. exists i. split.
      + apply in_seq. lia.
      + apply (IH (prefix ++ [i]) t'); [exact Ht |].
        rewrite <- app_assoc. simpl. exact HM.
  Qed.

  Theorem compoundk_sound : forall dims n prefix t,
    canonA ckSt ck_heads ck_step (length (n :: dims)) (n :: dims, prefix) t ->
    in_bounds t (n :: dims) /\ M (prefix ++ t) = true.
  Proof.
    induction dims as [|n' dims IH]; intros n prefix t Hc;
      destruct t as [|i t']; simpl in Hc; try contradiction.
    - (* rank 1 *)
      destruct Hc as [Hi Hc'].
      destruct t' as [|x xs]; [| simpl in Hc'; contradiction].
      apply filter_In in Hi as [Hin HM].
      apply in_seq in Hin. simpl in HM.
      split.
      + constructor; [lia | constructor].
      + exact HM.
    - (* rank >= 2 *)
      destruct Hc as [Hi Hc'].
      apply filter_In in Hi as [Hin HM'].
      apply in_seq in Hin.
      destruct (IH n' (prefix ++ [i]) t' Hc') as [HF HMt].
      split.
      + constructor; [lia | exact HF].
      + rewrite <- app_assoc in HMt. simpl in HMt. exact HMt.
  Qed.

  Theorem compoundk_complete : forall dims prefix t,
    in_bounds t dims ->
    M (prefix ++ t) = true ->
    canonA ckSt ck_heads ck_step (length dims) (dims, prefix) t.
  Proof.
    induction dims as [|n dims IH]; intros prefix t HF HM.
    - inversion HF; subst. simpl. exact I.
    - inversion HF as [| i d t' dims0 Hi Ht]; subst.
      simpl. split.
      + apply filter_In. split; [apply in_seq; lia |].
        apply (has_completion_witness dims (prefix ++ [i]) t'); [exact Ht |].
        rewrite <- app_assoc. simpl. exact HM.
      + apply (IH (prefix ++ [i]) t'); [exact Ht |].
        rewrite <- app_assoc. simpl. exact HM.
  Qed.

  Corollary compoundk_NoDup : forall r s,
    NoDup (enumA ckSt ck_heads ck_step r s).
  Proof.
    intros r s. apply enumA_NoDup.
    intros (ds, pf). destruct ds as [|n ds]; simpl.
    - constructor.
    - apply NoDup_filter'. apply NoDup_seq.
  Qed.

  (* THE DENOTATION THEOREM: at every POSITIVE rank, the Compound arrow  *)
  (* enumerates exactly the true cells of the mask (all ranks:           *)
  (* compound_enum_denotation below).                                    *)
  Corollary compoundk_denotation : forall n dims t,
    In t (enumA ckSt ck_heads ck_step (length (n :: dims)) (n :: dims, []))
    <-> (in_bounds t (n :: dims) /\ M t = true).
  Proof.
    intros n dims t. split.
    - intro H. apply enumA_sound in H.
      destruct (compoundk_sound dims n [] t H) as [HF HM].
      split; [exact HF | exact HM].
    - intros [HF HM]. apply enumA_complete.
      apply (compoundk_complete (n :: dims) [] t HF).
      exact HM.
  Qed.

  (* RANK ZERO.  At dims = [] the bare arrow enumerates [[]] whatever the *)
  (* mask (enumA at rank 0), so compoundk_denotation is stated at         *)
  (* positive rank only (compound_rank_zero_ignores_mask below).  The     *)
  (* wrapper tests the mask on the one empty tuple at rank zero and is    *)
  (* the arrow at every positive rank; its denotation theorem holds at    *)
  (* EVERY rank, zero included.                                           *)
  Definition compound_enum (dims : list nat) : list (list nat) :=
    match dims with
    | [] => if M [] then [[]] else []
    | _ :: _ => enumA ckSt ck_heads ck_step (length dims) (dims, [])
    end.

  Theorem compound_enum_denotation : forall dims t,
    In t (compound_enum dims) <-> (in_bounds t dims /\ M t = true).
  Proof.
    intros [|n dims] t.
    - unfold compound_enum, in_bounds. split.
      + destruct (M []) eqn:E; simpl; [| contradiction].
        intros [<- | []]. split; [constructor | exact E].
      + intros [HF HM]. inversion HF; subst. rewrite HM. left. reflexivity.
    - exact (compoundk_denotation n dims t).
  Qed.

  Corollary compound_enum_NoDup : forall dims, NoDup (compound_enum dims).
  Proof.
    intros [|n dims]; unfold compound_enum.
    - destruct (M []); [constructor; [intros [] | constructor] | constructor].
    - apply compoundk_NoDup.
  Qed.

  (* at positive rank the wrapper IS the arrow *)
  Lemma compound_enum_pos : forall n dims,
    compound_enum (n :: dims)
    = enumA ckSt ck_heads ck_step (length (n :: dims)) (n :: dims, []).
  Proof. reflexivity. Qed.
End CompoundK.

(* AUDIT F05 REGRESSION: the bare arrow at rank zero ignores an          *)
(* everywhere-false mask; the wrapper does not.                          *)
Example compound_rank_zero_ignores_mask :
  enumA ckSt (ck_heads (fun _ => false)) ck_step 0 ([], []) = [[]] /\
  compound_enum (fun _ => false) [] = [] /\
  compound_enum (fun _ => true) [] = [[]].
Proof. repeat split. Qed.

(* ===================================================================== *)
(* SUBSUMPTION: BladeArrow's concrete rank-2 masked instance is the      *)
(* dims = [n; m] case of the rank-k arrow (masks related by lift2).      *)
(* ===================================================================== *)

Section Rank2Subsumption.
  Variables n m : nat.
  Variable M2 : nat -> nat -> bool.

  Definition lift2 (t : list nat) : bool :=
    match t with [i; j] => M2 i j | _ => false end.

  Corollary rank2_subsumed : forall i j,
    canonA cSt (c_heads n m M2) c_step 2 None [i; j] <->
    canonA ckSt (ck_heads lift2) ck_step 2 ([n; m], []) [i; j].
  Proof.
    intros i j. split.
    - intro H.
      destruct (proj1 (compound_arrow_denotes_mask n m M2 i j) H)
        as (Hi & Hj & HM).
      apply (compoundk_complete lift2 [n; m] [] [i; j]).
      + constructor; [lia | constructor; [lia | constructor]].
      + exact HM.
    - intro H.
      destruct (compoundk_sound lift2 [m] n [] [i; j] H) as [HF HM].
      apply (proj2 (compound_arrow_denotes_mask n m M2 i j)).
      inversion HF as [| a d t2 d2 Ha Hrest]; subst.
      inversion Hrest as [| a2 d3 t3 d4 Hb Hnil]; subst.
      simpl in HM.
      repeat split; [exact Ha | exact Hb | exact HM].
  Qed.
End Rank2Subsumption.

(* ===================================================================== *)
(* Notes:                                                                *)
(*  - The dependence dichotomy (BladeArrow.const_state_rectilinear)      *)
(*    applies: the Compound arrow's step is value-dependent through      *)
(*    has_completion, so it factors into separate arrows exactly when    *)
(*    the mask is a product -- the general form of: CompoundIdx fuses    *)
(*    its dimensions.                                                   *)
(*  - Compiler correspondence: has_completion is the semantic spec of    *)
(*    the FilteredIdx residual; the compiler's dense scan over           *)
(*    materialized true cells enumerates the same set by                 *)
(*    compoundk_denotation, and in the same order: both are              *)
(*    lexicographic (BladeLex.compoundk_lex_sorted).                     *)
(* ===================================================================== *)
