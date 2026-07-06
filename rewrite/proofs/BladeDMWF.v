(* ===================================================================== *)
(* BladeDMWF.v -- Layers 0-1 of the Blade proof system                   *)
(*                                                                       *)
(* L0: index-type universe (deep embedding of symmetric index records,   *)
(*     shapes as products) with the canonical-tuple denotation.          *)
(* L1: the Double Metamorphism With Feedback, mechanized as a structural *)
(*     unfold whose seed is the RESIDUAL INDEX TYPE.  Checked claims:    *)
(*       - dmwf_equation: the feedback recursion itself (formalism 2.7)  *)
(*       - enum_sound / enum_complete / enum_NoDup: the index            *)
(*         metamorphism emits every canonical tuple exactly once         *)
(*       - enum_length: cardinality by the same recursion (binomial      *)
(*         identification: BladeBinomial.v)                              *)
(*       - lj_correct / unlj_correct: the GENERAL left-justified         *)
(*         storage bijection (subsumes BladeCore.v's r=2,3 instances;    *)
(*         formalism 14.2-14.3)                                          *)
(*       - dmwf_index_level_kernel_independent: the index level does     *)
(*         not depend on the kernel -- the bridge to Lemma 2.2 and       *)
(*         Theorem 2.3 (structure exists before kernels)                 *)
(*       - tdim_relationality: the S/T split does not factor through     *)
(*         arrays alone (Lemma 2.2's mechanizable core)                  *)
(*       - residual_not_constant: the seed family is not a constant      *)
(*         family -- dependent typing is forced (anchor for Thm 9.3)     *)
(*                                                                       *)
(* Coq 8.18, stdlib only.                                                *)
(* ===================================================================== *)

Require Import List Arith Lia.
Import ListNotations.

(* ===================================================================== *)
(* Generic list lemmas (stdlib gaps)                                     *)
(* ===================================================================== *)

Fixpoint lsum (l : list nat) : nat :=
  match l with [] => 0 | x :: t => x + lsum t end.

Lemma lsum_const : forall (A : Type) (k : nat) (l : list A),
  lsum (map (fun _ => k) l) = k * length l.
Proof.
  induction l; simpl; [lia | rewrite IHl, Nat.mul_succ_r; lia].
Qed.

Lemma lsum_zero_len : forall c : list nat, length c = 0 -> lsum c = 0.
Proof. destruct c; [reflexivity | discriminate]. Qed.

Lemma flat_map_length : forall (A B : Type) (f : A -> list B) (l : list A),
  length (flat_map f l) = lsum (map (fun x => length (f x)) l).
Proof.
  induction l; simpl; [reflexivity | rewrite app_length, IHl; reflexivity].
Qed.

Lemma NoDup_seq : forall len start, NoDup (seq start len).
Proof.
  induction len; intros; simpl; constructor.
  - rewrite in_seq; lia.
  - apply IHlen.
Qed.

Lemma NoDup_map_inj : forall (A B : Type) (f : A -> B) (l : list A),
  NoDup l ->
  (forall x y, In x l -> In y l -> f x = f y -> x = y) ->
  NoDup (map f l).
Proof.
  intros A B f l Hnd.
  induction Hnd as [|a l Hnotin Hnd IH]; intros Hinj; simpl; constructor.
  - intro Hin. apply in_map_iff in Hin as (x & Ex & Hx).
    apply Hinj in Ex; [subst; exact (Hnotin Hx) | now right | now left].
  - apply IH. intros x y Hx Hy E.
    apply Hinj; [right; exact Hx | right; exact Hy | exact E].
Qed.

Lemma NoDup_app_disjoint : forall (A : Type) (l1 l2 : list A),
  NoDup l1 -> NoDup l2 -> (forall x, In x l1 -> ~ In x l2) ->
  NoDup (l1 ++ l2).
Proof.
  intros A l1 l2 H1.
  induction H1 as [|a l1 Hnotin H1 IH]; intros H2 Hd; simpl.
  - exact H2.
  - constructor.
    + rewrite in_app_iff. intros [Hl1 | Hl2]; [contradiction |].
      exact (Hd a (or_introl eq_refl) Hl2).
    + apply IH; [exact H2 | intros x Hx; apply Hd; right; exact Hx].
Qed.

Lemma NoDup_flat_map : forall (A B : Type) (f : A -> list B) (l : list A),
  NoDup l ->
  (forall x, In x l -> NoDup (f x)) ->
  (forall x y b, In x l -> In y l -> In b (f x) -> In b (f y) -> x = y) ->
  NoDup (flat_map f l).
Proof.
  intros A B f l Hnd.
  induction Hnd as [|a l Hnotin Hnd IH]; intros Hin Hdisj; simpl.
  - constructor.
  - apply NoDup_app_disjoint.
    + apply Hin; left; reflexivity.
    + apply IH.
      * intros x Hx; apply Hin; right; exact Hx.
      * intros x y b Hx Hy Hbx Hby.
        apply (Hdisj x y b); [right; exact Hx | right; exact Hy | exact Hbx | exact Hby].
    + intros x Hx Hflat.
      apply in_flat_map in Hflat as (y & Hy & Hfy).
      assert (a = y)
        by (apply (Hdisj a y x);
            [left; reflexivity | right; exact Hy | exact Hx | exact Hfy]).
      subst y. contradiction.
Qed.

(* ===================================================================== *)
(* L0: THE INDEX UNIVERSE.                                               *)
(* A symmetric index record is a triple (r, l, u): r nondecreasing       *)
(* indices in [l, u).  This single form subsumes the base cases:         *)
(*   Idx<n>          = (1, 0, n)                                         *)
(*   BoundedIdx l u  = (1, l, u)   -- the residual of currying           *)
(*   SymIdx<r, n>    = (r, 0, n)                                         *)
(* The lower bound l is exactly the residual state that currying         *)
(* produces (formalism 4.13-4.14); making it a first-class parameter is  *)
(* what lets the DMWF below be a plain structural recursion.             *)
(* canonical is the DENOTATION: which tuples inhabit the code.           *)
(* ===================================================================== *)

Fixpoint canonical (r l u : nat) (t : list nat) : Prop :=
  match r, t with
  | 0, [] => True
  | S r', i :: t' => l <= i < u /\ canonical r' i u t'
  | _, _ => False
  end.

(* ===================================================================== *)
(* L1: THE DOUBLE METAMORPHISM WITH FEEDBACK.                            *)
(* The five phases of formalism 2.7, located in the code:                *)
(*   phase 1 (index-cata: structure remaining space) = the residual      *)
(*     code (r', i, u) passed to the recursive call;                     *)
(*   phase 2 (index-ana: emit next index) = i drawn from seq l (u-l);    *)
(*   THE FEEDBACK = the emitted i becoming the residual's lower bound.   *)
(*   phases 3-5 (data cata/homo/ana) = the dmwf wrapper further below.   *)
(* The "double metamorphism with feedback" is thus precisely: a          *)
(* structural unfold whose seed is the residual index type, driving a    *)
(* per-tuple read/transform/write.                                       *)
(* ===================================================================== *)

Fixpoint enum (r l u : nat) : list (list nat) :=
  match r with
  | 0 => [ [] ]
  | S r' => flat_map (fun i => map (cons i) (enum r' i u)) (seq l (u - l))
  end.

(* The feedback equation itself -- definitional, stated for the record.  *)
Theorem dmwf_equation : forall r l u,
  enum (S r) l u = flat_map (fun i => map (cons i) (enum r i u)) (seq l (u - l)).
Proof. reflexivity. Qed.

(* --- The index metamorphism emits every canonical tuple, only          *)
(*     canonical tuples, and each exactly once. ------------------------ *)

Theorem enum_sound : forall r l u t,
  In t (enum r l u) -> canonical r l u t.
Proof.
  induction r as [|r IH]; intros l u t H; simpl in H.
  - destruct H as [H | []]. subst t. exact I.
  - apply in_flat_map in H as (i & Hi & Ht).
    apply in_map_iff in Ht as (t' & Et & Ht').
    apply in_seq in Hi. subst t. simpl.
    split; [lia | apply IH; exact Ht'].
Qed.

Theorem enum_complete : forall r l u t,
  canonical r l u t -> In t (enum r l u).
Proof.
  induction r as [|r IH]; intros l u t Hc;
    destruct t as [|i t']; simpl in Hc; try contradiction.
  - left; reflexivity.
  - destruct Hc as [Hb Hc]. simpl.
    apply in_flat_map. exists i. split.
    + apply in_seq; lia.
    + apply in_map_iff. exists t'. split; [reflexivity | apply IH; exact Hc].
Qed.

Theorem enum_NoDup : forall r l u, NoDup (enum r l u).
Proof.
  induction r as [|r IH]; intros; simpl.
  - constructor; [simpl; tauto | constructor].
  - apply NoDup_flat_map.
    + apply NoDup_seq.
    + intros i _.
      apply NoDup_map_inj; [apply IH | intros x y _ _ E; injection E; auto].
    + intros x y b _ _ Hbx Hby.
      apply in_map_iff in Hbx as (tx & Ex & _).
      apply in_map_iff in Hby as (ty & Ey & _).
      rewrite <- Ex in Ey. injection Ey; intros; congruence.
Qed.

(* --- Cardinality follows the same recursion; the closed form           *)
(*     C(u - l + r - 1, r) is proved in BladeBinomial.v. --------------- *)

Fixpoint mscard (r l u : nat) : nat :=
  match r with
  | 0 => 1
  | S r' => lsum (map (fun i => mscard r' i u) (seq l (u - l)))
  end.

Theorem enum_length : forall r l u,
  length (enum r l u) = mscard r l u.
Proof.
  induction r as [|r IH]; intros; simpl; [reflexivity |].
  rewrite flat_map_length. f_equal.
  apply map_ext. intro i. rewrite map_length. apply IH.
Qed.

(* ===================================================================== *)
(* THE GENERAL LEFT-JUSTIFIED STORAGE BIJECTION (formalism 14.2-14.3).   *)
(* lj converts a canonical tuple to storage coordinates (successive      *)
(* differences, relative to the group base); unlj is the inverse         *)
(* (cumulative sums).  storageOK is the left-justified loop domain:      *)
(* per-level bounds a_k < u - l - sum of prior coordinates, which for    *)
(* nat coordinates is equivalent to the single constraint on the total.  *)
(* This generalizes BladeCore.v's lj2/lj3 to every rank at once.         *)
(* ===================================================================== *)

Fixpoint lj (prev : nat) (t : list nat) : list nat :=
  match t with [] => [] | i :: t' => (i - prev) :: lj i t' end.

Fixpoint unlj (prev : nat) (c : list nat) : list nat :=
  match c with [] => [] | a :: c' => (prev + a) :: unlj (prev + a) c' end.

Definition storageOK (r l u : nat) (c : list nat) : Prop :=
  length c = r /\ (r = 0 \/ l + lsum c < u).

Theorem lj_correct : forall r l u t,
  canonical r l u t ->
  storageOK r l u (lj l t) /\ unlj l (lj l t) = t.
Proof.
  induction r as [|r IH]; intros l u t Hc;
    destruct t as [|i t']; simpl in Hc; try contradiction.
  - simpl. unfold storageOK. simpl.
    split; [split; [reflexivity | left; reflexivity] | reflexivity].
  - destruct Hc as [Hb Hc].
    destruct (IH i u t' Hc) as [[Hlen Hbd] Hrt].
    unfold storageOK in *. simpl.
    split; [split |].
    + rewrite Hlen. reflexivity.
    + right. destruct Hbd as [Hr0 | Hs]; [| lia].
      (* r = 0 forces the residual storage empty; NOTE: do not `subst r`
         here -- with two defining equations for r in scope, subst may
         consume Hlen instead of Hr0. *)
      rewrite Hr0 in Hlen. rewrite (lsum_zero_len _ Hlen). lia.
    + replace (l + (i - l)) with i by lia. rewrite Hrt. reflexivity.
Qed.

Theorem unlj_correct : forall r l u c,
  storageOK r l u c ->
  canonical r l u (unlj l c) /\ lj l (unlj l c) = c.
Proof.
  induction r as [|r IH]; intros l u c [Hlen Hb];
    destruct c as [|a c']; simpl in Hlen; try discriminate.
  - simpl. split; [exact I | reflexivity].
  - injection Hlen as Hlen.
    assert (Hau : l + a < u).
    { destruct Hb as [E | Hb]; [discriminate | simpl in Hb; lia]. }
    assert (Hb' : r = 0 \/ (l + a) + lsum c' < u).
    { destruct r as [|r'']; [left; reflexivity | right].
      destruct Hb as [E | Hb]; [discriminate | simpl in Hb; lia]. }
    specialize (IH (l + a) u c' (conj Hlen Hb')) as [Hcan Hlj].
    simpl. split.
    + split; [lia | exact Hcan].
    + replace (l + a - l) with a by lia. rewrite Hlj. reflexivity.
Qed.

(* ===================================================================== *)
(* THE DATA LEVEL (phases 3-5), and the bridge to relationality.         *)
(* ===================================================================== *)

Definition dmwf {V : Type} (kernel : list nat -> V) (r l u : nat)
  : list (list nat * V) :=
  map (fun t => (t, kernel t)) (enum r l u).

(* Every canonical cell is computed. *)
Theorem dmwf_complete : forall (V : Type) (k : list nat -> V) r l u t,
  canonical r l u t -> In (t, k t) (dmwf k r l u).
Proof.
  intros. apply (in_map (fun t => (t, k t))). apply enum_complete; assumption.
Qed.

(* BRIDGE THEOREM 1 (toward Lemma 2.2b / Theorem 2.3, positive half):    *)
(* the index level is IDENTICAL for every kernel -- iteration structure  *)
(* exists prior to and independent of the kernel.  This is what makes    *)
(* method_for constructible before <@>: the S-side of the metamorphism   *)
(* is a value on its own.                                                *)
Theorem dmwf_index_level_kernel_independent :
  forall (V W : Type) (k1 : list nat -> V) (k2 : list nat -> W) r l u,
    map fst (dmwf k1 r l u) = map fst (dmwf k2 r l u).
Proof.
  intros. unfold dmwf. rewrite !map_map. reflexivity.
Qed.

(* BRIDGE THEOREM 2 (Lemma 2.2, mechanizable core): the S/T split of the *)
(* SAME arrays varies with the kernel's input rank, so no function of    *)
(* arrays alone computes it.  T-dimensions are relational.  (Theorem 2.3 *)
(* follows in L2: a completed iteration structure requires the split,    *)
(* hence cannot be a kernel-free object; what CAN exist kernel-free is   *)
(* exactly the index level above.)                                       *)
Definition s_dims (arr_rank irank : nat) : nat := arr_rank - irank.

Theorem tdim_relationality :
  forall g : nat -> nat,
    ~ (forall arr_rank irank, irank <= arr_rank -> g arr_rank = s_dims arr_rank irank).
Proof.
  intros g H.
  assert (H0 : g 2 = 2) by (apply (H 2 0); lia).
  assert (H1 : g 2 = 1) by (apply (H 2 1); lia).
  lia.
Qed.

(* BRIDGE THEOREM 3 (anchor for Trinity Theorem 9.3): the residual seed  *)
(* family is NOT a constant family -- the cardinality of the space      *)
(* remaining after emitting i depends on i.  A non-dependent type        *)
(* cannot express the residual; dimensional currying with dependent      *)
(* index types is forced.                                                *)

Lemma mscard1 : forall l u, mscard 1 l u = u - l.
Proof.
  intros. simpl.
  transitivity (lsum (map (fun _ : nat => 1) (seq l (u - l)))).
  { f_equal; try (apply map_ext; intro; reflexivity). }
  rewrite lsum_const, seq_length. lia.
Qed.

Theorem residual_not_constant :
  forall l u, l + 1 < u ->
    mscard 1 l u <> mscard 1 (u - 1) u.
Proof.
  intros. rewrite !mscard1. lia.
Qed.

(* ===================================================================== *)
(* SHAPES: products of records (an array's S-dimension list).            *)
(* Enumeration is cartesian concatenation; cardinality multiplies.       *)
(* (Per-shape soundness/completeness/NoDup are mechanical extensions of  *)
(* the single-record proofs -- L2 work.)                                 *)
(* ===================================================================== *)

Record IxRec := mkIx { ix_r : nat; ix_l : nat; ix_u : nat }.
Definition Shape := list IxRec.

Definition enumIx (c : IxRec) : list (list nat) :=
  enum (ix_r c) (ix_l c) (ix_u c).

Fixpoint enumShape (s : Shape) : list (list nat) :=
  match s with
  | [] => [ [] ]
  | c :: s' => flat_map (fun t => map (app t) (enumShape s')) (enumIx c)
  end.

Theorem enumShape_length : forall s,
  length (enumShape s) =
  fold_right Nat.mul 1 (map (fun c => length (enumIx c)) s).
Proof.
  induction s as [|c s IH]; simpl; [reflexivity |].
  rewrite flat_map_length.
  transitivity (lsum (map (fun _ : list nat => length (enumShape s)) (enumIx c))).
  { f_equal. apply map_ext. intro t. apply map_length. }
  rewrite lsum_const, IH. apply Nat.mul_comm.
Qed.

(* ===================================================================== *)
(* Everything sketched here as future work is now built: binomial        *)
(* closed form (BladeBinomial), lex order (BladeLex), strict and affine  *)
(* variants (BladeArrow, BladeAffine), detection and the two maximal     *)
(* curryings (BladeCurrying, BladeCurryingGeneral), fold closure and     *)
(* arity results (BladeTrinity, BladeTrinityAsym).                       *)
(* ===================================================================== *)
