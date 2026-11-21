(* ===================================================================== *)
(* BladeDichotomyGeneral.v -- the storage dichotomy at EVERY rank r.      *)
(*                                                                       *)
(* BladeDichotomy.v proves, at r = 3, that no width-2 scalar-access      *)
(* per-dimension-canonical scheme reconstructs every slot-pair-symmetric *)
(* tensor at a free cell, and that the width-3! scheme does.  Its header  *)
(* states the general theorem -- minimal width r! at a free cell -- as    *)
(* prose, and says a general-r mechanization <<would need a Coq           *)
(* development of Q[S_r]>>.  It does not.  The r! bound needs no group    *)
(* algebra at all: the r! diagonal orbits that meet the fiber over a free *)
(* cell have indicator tensors whose evaluation matrix at r! fiber points *)
(* is the identity, so a width-m scheme factors the r! x r! identity      *)
(* through m scalars; over an integral domain that is impossible for      *)
(* m < r! (BladeRankDomain.homogeneous_has_solution_dom gives a nonzero   *)
(* vector the stored matrix kills, and the identity cannot kill it).  The *)
(* representation theory enters only when one wants the ISOTYPIC form of  *)
(* the r! scheme; the plain r! scheme is pure index algebra.  This file   *)
(* mechanizes the theorem at every r.                                     *)
(*                                                                       *)
(* The model (BladeDichotomy's Part B, generalized faithfully):           *)
(*  - an index is (i, j), i in [L]^r, j in [M]^r, as two length-r lists;  *)
(*    S_r acts diagonally by image lists (pact, BladeDichotomy's);        *)
(*    T is slot-pair symmetric on the box iff T (pact p i) (pact p j) =   *)
(*    T i j for every permutation p and every (i, j) in the box           *)
(*    (diag_sym_box);                                                     *)
(*  - a width-m scheme is m weight functions eps_k (s, t) of the two      *)
(*    sorting permutations (at a free cell unique; here, as in Part B,    *)
(*    the weights are arbitrary functions of the fiber point), plus PER   *)
(*    TENSOR and PER CELL m stored scalars -- an arbitrary function of    *)
(*    the tensor, not assumed linear -- such that T (pact s a) (pact t b) *)
(*    = sum_k eps_k (s, t) * P_k for every fiber point (reconstructs,     *)
(*    sound_at, sound).                                                   *)
(*  Differences from Part B, stated once: tensors take the two tuples as  *)
(*  separate arguments (Part B: one 6-list; the bridge at the end         *)
(*  converts); the lower bound needs an INTEGRAL DOMAIN WITH DECIDABLE    *)
(*  EQUALITY where Part B's 3x3 determinant certificate needed only a     *)
(*  commutative ring with 1 <> 0 -- elimination picks a pivot and cancels *)
(*  it -- so at r = 3 Part B remains the stronger statement and this is   *)
(*  the one that scales; the upper bound is stated at EVERY cell of the   *)
(*  box, tied or free, canonical or not.                                  *)
(*                                                                       *)
(* Checked, at every r:                                                   *)
(*  - PART 1 (permutations): image-list permutations of [0, r) with      *)
(*    composition (pact_assoc), identity (pact_id), inverse (inv_perm,   *)
(*    pact_inv_l/r), closure, injectivity; the list perms r of ALL        *)
(*    permutations by insertion -- length r! (perms_length), no          *)
(*    duplicates (perms_NoDup), sound and complete (perms_spec).          *)
(*  - PART 2 (the lower bound): free_cell_needs_factorial -- at a free   *)
(*    cell (L, M >= r), over any integral domain with decidable           *)
(*    equality, no width-m scheme with m < r! reconstructs every 0/1      *)
(*    symmetric tensor; free_cell_width_bound: a sound scheme at the      *)
(*    free cell has width >= r!.                                          *)
(*  - PART 3 (the upper bound): factorial_component_access -- store       *)
(*    P_mu (a, b) = T a (pact mu b) for the r! permutations mu and read   *)
(*    T (pact s a) (pact t b) as P_{s^-1 t} (a, b); pure index algebra,  *)
(*    values in ANY type, EVERY index (BladeDichotomy's                   *)
(*    six_component_access at every r); factorial_scheme_sound: the same  *)
(*    as a width-r! scheme in the model's own shape, at every cell.       *)
(*  - PART 4 (the dichotomy): minimal_width_is_factorial (exactly r! at   *)
(*    L, M >= r); width2_iff_rank_le_2 -- a width-2 scheme exists iff     *)
(*    r <= 2, BladeCauchy's split at r = 2 and BladeDichotomy's refusal   *)
(*    at r = 3, now at every r >= 3 (width2_refuted_at_every_rank_ge_3); *)
(*    the Z instance minimal_width_over_Z; and the sanity tie-back:       *)
(*    part_b_hypothesis_refuted_via_general re-derives BladeDichotomy's   *)
(*    width2_scalar_access_refuted (its exact hypothesis shape) from the  *)
(*    general theorem at r = 3, m = 2.                                    *)
(*  - PART 5 (cell accounting, general definitions, pinned): the double-  *)
(*    coset sum over canonical cells and the diagonal-orbit count, now    *)
(*    defined at general r through perms r, agree with each other and     *)
(*    with C(LM + r - 1, r) at (r, L, M) = (3, 3, 3) (BladeDichotomy's    *)
(*    165, recomputed through the general definitions), (4, 2, 2) and     *)
(*    (4, 2, 3).  The general identity is not proved (see closing note).  *)
(*                                                                       *)
(* Division-free; no determinants.  Imports BladeDichotomy,               *)
(* BladeDichotomyTies, BladeRankDomain.  Rocq 9.0, stdlib only, no axioms. *)
(* ===================================================================== *)

From Blade Require Import BladeDichotomy BladeDichotomyTies BladeRankDomain.
From Stdlib Require Import List Arith Lia ZArith Ring Bool Permutation.
Import ListNotations.
Open Scope nat_scope.

(* ===================================================================== *)
(* PART 1 -- image-list permutations at general r.                       *)
(* A permutation of the r positions is a list p that is a Permutation of *)
(* seq 0 r; pact p l = map (fun k => nth k l 0) p places l's p[k]-th      *)
(* entry at position k (BladeDichotomy's pact, now with its laws).        *)
(* ===================================================================== *)

Definition is_perm (r : nat) (p : list nat) : Prop := Permutation p (seq 0 r).

Lemma is_perm_length : forall r p, is_perm r p -> length p = r.
Proof. intros r p H. rewrite (Permutation_length H). apply length_seq. Qed.

Lemma is_perm_in : forall r p k, is_perm r p -> (In k p <-> k < r).
Proof.
  intros r p k H. split; intro Hk.
  - apply (Permutation_in _ H) in Hk. apply in_seq in Hk. lia.
  - apply (Permutation_in _ (Permutation_sym H)). apply in_seq. lia.
Qed.

Lemma is_perm_nth : forall r p i, is_perm r p -> i < r -> nth i p 0 < r.
Proof.
  intros r p i H Hi. apply (is_perm_in r p _ H). apply nth_In.
  rewrite (is_perm_length r p H). exact Hi.
Qed.

Lemma is_perm_NoDup : forall r p, is_perm r p -> NoDup p.
Proof.
  intros r p H. apply (Permutation_NoDup (Permutation_sym H)). apply seq_NoDup.
Qed.

Lemma is_perm_id : forall r, is_perm r (seq 0 r).
Proof. intro r. apply Permutation_refl. Qed.

Lemma pact_length : forall p l, length (pact p l) = length p.
Proof. intros. apply length_map. Qed.

Lemma pact_nth : forall p l i, i < length p ->
  nth i (pact p l) 0 = nth (nth i p 0) l 0.
Proof.
  intros p l i Hi. unfold pact.
  rewrite (nth_indep _ 0 (nth 0 l 0)) by (rewrite length_map; exact Hi).
  exact (map_nth (fun k => nth k l 0) p 0 i).
Qed.

(* identity *)
Lemma pact_id : forall r l, length l = r -> pact (seq 0 r) l = l.
Proof.
  intros r l Hl. apply (nth_ext _ _ 0 0).
  - rewrite pact_length, length_seq. auto.
  - intros i Hi. rewrite pact_length, length_seq in Hi.
    rewrite (pact_nth _ _ i) by (rewrite length_seq; exact Hi).
    rewrite seq_nth by exact Hi. reflexivity.
Qed.

(* a permutation applied to the identity tuple is its own image list *)
Lemma pact_seq : forall r p, is_perm r p -> pact p (seq 0 r) = p.
Proof.
  intros r p Hp. assert (Hl := is_perm_length r p Hp).
  apply (nth_ext _ _ 0 0).
  - rewrite pact_length. reflexivity.
  - intros i Hi. rewrite pact_length in Hi.
    rewrite (pact_nth p _ i Hi).
    rewrite seq_nth by (apply is_perm_nth; [exact Hp|lia]). reflexivity.
Qed.

(* composition: pact p (pact q l) = pact (pact p q) l *)
Lemma pact_assoc : forall p q l, (forall k, In k p -> k < length q) ->
  pact p (pact q l) = pact (pact p q) l.
Proof.
  intros p q l Hb. apply (nth_ext _ _ 0 0).
  - rewrite !pact_length. reflexivity.
  - intros i Hi. rewrite pact_length in Hi.
    rewrite (pact_nth p (pact q l) i Hi).
    rewrite (pact_nth q l (nth i p 0)) by (apply Hb, nth_In; exact Hi).
    rewrite (pact_nth (pact p q) l i) by (rewrite pact_length; exact Hi).
    rewrite (pact_nth p q i Hi). reflexivity.
Qed.

Lemma is_perm_pact : forall r p q, is_perm r p -> is_perm r q -> is_perm r (pact p q).
Proof.
  intros r p q Hp Hq. unfold is_perm, pact.
  apply perm_trans with (map (fun k => nth k q 0) (seq 0 r)).
  - apply Permutation_map. exact Hp.
  - change (map (fun k => nth k q 0) (seq 0 r)) with (pact (seq 0 r) q).
    rewrite (pact_id r q (is_perm_length r q Hq)). exact Hq.
Qed.

(* the position of k in l (length l if absent), and the inverse image list *)
Fixpoint pos (k : nat) (l : list nat) : nat :=
  match l with
  | [] => 0
  | x :: l' => if Nat.eqb x k then 0 else S (pos k l')
  end.

Lemma pos_nth : forall k l, In k l -> nth (pos k l) l 0 = k.
Proof.
  intros k l. induction l as [|x l IH]; intro H; [destruct H|].
  simpl. destruct (Nat.eqb_spec x k) as [E|E]; [exact E|].
  apply IH. destruct H as [H|H]; [congruence|exact H].
Qed.

Lemma pos_lt : forall k l, In k l -> pos k l < length l.
Proof.
  intros k l. induction l as [|x l IH]; intro H; [destruct H|].
  simpl. destruct (Nat.eqb_spec x k) as [E|E]; [lia|].
  assert (pos k l < length l) by (apply IH; destruct H; [congruence|assumption]).
  lia.
Qed.

Lemma nth_pos : forall l i, NoDup l -> i < length l -> pos (nth i l 0) l = i.
Proof.
  intros l. induction l as [|x l IH]; intros i Hnd Hi; [simpl in Hi; lia|].
  inversion Hnd as [|? ? Hnotin Hnd']; subst.
  destruct i as [|i].
  - simpl. rewrite Nat.eqb_refl. reflexivity.
  - simpl in Hi. simpl.
    destruct (Nat.eqb_spec x (nth i l 0)) as [E|E].
    + exfalso. apply Hnotin. rewrite E. apply nth_In. lia.
    + f_equal. apply IH; [exact Hnd'|lia].
Qed.

Definition inv_perm (r : nat) (p : list nat) : list nat :=
  map (fun k => pos k p) (seq 0 r).

Lemma inv_perm_length : forall r p, length (inv_perm r p) = r.
Proof. intros. unfold inv_perm. rewrite length_map, length_seq. reflexivity. Qed.

Lemma inv_perm_nth : forall r p k, k < r -> nth k (inv_perm r p) 0 = pos k p.
Proof.
  intros r p k Hk. unfold inv_perm.
  rewrite (nth_indep _ 0 (pos 0 p)) by (rewrite length_map, length_seq; exact Hk).
  etransitivity; [exact (map_nth (fun k => pos k p) (seq 0 r) 0 k)|].
  cbn beta. rewrite seq_nth by exact Hk. reflexivity.
Qed.

Lemma pact_inv_l : forall r p, is_perm r p -> pact (inv_perm r p) p = seq 0 r.
Proof.
  intros r p Hp. apply (nth_ext _ _ 0 0).
  - rewrite pact_length, inv_perm_length, length_seq. reflexivity.
  - intros i Hi. rewrite pact_length, inv_perm_length in Hi.
    rewrite (pact_nth _ _ i) by (rewrite inv_perm_length; exact Hi).
    rewrite inv_perm_nth by exact Hi.
    rewrite pos_nth by (apply (is_perm_in r p i Hp); exact Hi).
    rewrite seq_nth by exact Hi. reflexivity.
Qed.

Lemma pact_inv_r : forall r p, is_perm r p -> pact p (inv_perm r p) = seq 0 r.
Proof.
  intros r p Hp. assert (Hl := is_perm_length r p Hp).
  apply (nth_ext _ _ 0 0).
  - rewrite pact_length, length_seq. exact Hl.
  - intros i Hi. rewrite pact_length in Hi.
    rewrite (pact_nth p _ i Hi).
    rewrite inv_perm_nth by (apply is_perm_nth; [exact Hp|lia]).
    rewrite nth_pos; [|exact (is_perm_NoDup r p Hp)|exact Hi].
    rewrite seq_nth by lia. reflexivity.
Qed.

Lemma is_perm_inv : forall r p, is_perm r p -> is_perm r (inv_perm r p).
Proof.
  intros r p Hp. assert (Hl := is_perm_length r p Hp).
  unfold is_perm. apply NoDup_Permutation_bis.
  - apply (proj2 (NoDup_nth (inv_perm r p) 0)). intros i j Hi Hj E.
    rewrite inv_perm_length in Hi, Hj.
    rewrite !inv_perm_nth in E by assumption.
    assert (E' : nth (pos i p) p 0 = nth (pos j p) p 0) by (rewrite E; reflexivity).
    rewrite !pos_nth in E' by (apply (is_perm_in r p _ Hp); assumption). exact E'.
  - rewrite inv_perm_length, length_seq. lia.
  - intros x Hx. unfold inv_perm in Hx. apply in_map_iff in Hx as [k [Ek Hk]].
    apply in_seq in Hk. subst x. apply in_seq.
    assert (pos k p < length p) by (apply pos_lt; apply (is_perm_in r p k Hp); lia).
    lia.
Qed.

Lemma inv_perm_bound : forall r p (q : list nat), is_perm r p -> length q = r ->
  forall k, In k (inv_perm r p) -> k < length q.
Proof.
  intros r p q Hp Hq k Hk. rewrite Hq.
  apply (is_perm_in r _ k (is_perm_inv r p Hp)). exact Hk.
Qed.

Lemma pact_inj : forall r p j j', is_perm r p -> length j = r -> length j' = r ->
  pact p j = pact p j' -> j = j'.
Proof.
  intros r p j j' Hp Hj Hj' E.
  assert (Hb := inv_perm_bound r p p Hp (is_perm_length r p Hp)).
  transitivity (pact (inv_perm r p) (pact p j)).
  - rewrite (pact_assoc _ _ _ Hb), (pact_inv_l r p Hp), (pact_id r j Hj). reflexivity.
  - rewrite E, (pact_assoc _ _ _ Hb), (pact_inv_l r p Hp), (pact_id r j' Hj'). reflexivity.
Qed.

(* the box [0, n)^r, and its stability under the action *)
Definition inbox (n : nat) (l : list nat) : Prop := Forall (fun x => x < n) l.

Lemma inbox_seq : forall r n, r <= n -> inbox n (seq 0 r).
Proof.
  intros r n H. unfold inbox. apply Forall_forall. intros x Hx. apply in_seq in Hx. lia.
Qed.

Lemma inbox_pact : forall n r p i, is_perm r p -> length i = r ->
  inbox n i -> inbox n (pact p i).
Proof.
  intros n r p i Hp Hi HF. unfold inbox, pact in *.
  rewrite Forall_map, Forall_forall. intros k Hk.
  rewrite Forall_forall in HF. apply HF. apply nth_In.
  rewrite Hi. apply (is_perm_in r p k Hp). exact Hk.
Qed.

(* --- all permutations of a list, by insertion -------------------------- *)

Fixpoint insert_everywhere (x : nat) (l : list nat) : list (list nat) :=
  match l with
  | [] => [[x]]
  | y :: l' => (x :: y :: l') :: map (cons y) (insert_everywhere x l')
  end.

Fixpoint all_perms (l : list nat) : list (list nat) :=
  match l with
  | [] => [[]]
  | x :: l' => flat_map (insert_everywhere x) (all_perms l')
  end.

Definition perms (r : nat) : list (list nat) := all_perms (seq 0 r).

Lemma insert_everywhere_length : forall x l,
  length (insert_everywhere x l) = S (length l).
Proof.
  intros x l. induction l as [|y l IH]; simpl; [reflexivity|].
  rewrite length_map, IH. reflexivity.
Qed.

Lemma insert_everywhere_perm : forall x l q,
  In q (insert_everywhere x l) -> Permutation q (x :: l).
Proof.
  intros x l. induction l as [|y l IH]; intros q Hq; simpl in Hq.
  - destruct Hq as [Hq|[]]. subst. apply Permutation_refl.
  - destruct Hq as [Hq|Hq]; [subst; apply Permutation_refl|].
    apply in_map_iff in Hq as [q' [Eq Hq']]. subst q.
    apply perm_trans with (y :: x :: l).
    + apply perm_skip. apply IH. exact Hq'.
    + apply perm_swap.
Qed.

Lemma all_perms_perm : forall l p, In p (all_perms l) -> Permutation p l.
Proof.
  intros l. induction l as [|x l IH]; intros p Hp; simpl in Hp.
  - destruct Hp as [Hp|[]]. subst. apply Permutation_refl.
  - apply in_flat_map in Hp as [q [Hq Hp]].
    apply perm_trans with (x :: q).
    + apply insert_everywhere_perm. exact Hp.
    + apply perm_skip. apply IH. exact Hq.
Qed.

Lemma length_flat_map_uniform : forall (A B : Type) (f : A -> list B) (l : list A) c,
  (forall x, In x l -> length (f x) = c) -> length (flat_map f l) = c * length l.
Proof.
  intros A B f l c H. induction l as [|x l IH]; simpl; [lia|].
  rewrite length_app, (H x (or_introl eq_refl)), IH
    by (intros; apply H; right; assumption).
  lia.
Qed.

Lemma all_perms_length : forall l, length (all_perms l) = fact (length l).
Proof.
  intros l. induction l as [|x l IH]; [reflexivity|].
  change (length (flat_map (insert_everywhere x) (all_perms l)) = fact (S (length l))).
  rewrite (length_flat_map_uniform _ _ _ _ (S (length l))).
  - rewrite IH. reflexivity.
  - intros p Hp. rewrite insert_everywhere_length.
    rewrite (Permutation_length (all_perms_perm l p Hp)). reflexivity.
Qed.

Lemma in_insert_everywhere_app : forall x p1 p2,
  In (p1 ++ x :: p2) (insert_everywhere x (p1 ++ p2)).
Proof.
  intros x p1. induction p1 as [|y p1 IH]; intro p2; simpl.
  - destruct p2; simpl; left; reflexivity.
  - right. apply in_map. apply IH.
Qed.

Lemma all_perms_complete : forall l p, Permutation p l -> In p (all_perms l).
Proof.
  intros l. induction l as [|x l IH]; intros p Hp.
  - apply Permutation_sym, Permutation_nil in Hp. subst. left. reflexivity.
  - assert (Hx : In x p)
      by (apply (Permutation_in _ (Permutation_sym Hp)); left; reflexivity).
    apply in_split in Hx as [p1 [p2 Ep]]. subst p.
    simpl. apply in_flat_map. exists (p1 ++ p2). split.
    + apply IH. apply Permutation_sym. apply (@Permutation_cons_app_inv _ _ p1 p2 x).
      apply Permutation_sym. exact Hp.
    + apply in_insert_everywhere_app.
Qed.

Lemma NoDup_app_disjoint : forall (A : Type) (l1 l2 : list A),
  NoDup l1 -> NoDup l2 -> (forall y, In y l1 -> In y l2 -> False) -> NoDup (l1 ++ l2).
Proof.
  intros A l1 l2 H1 H2 Hd. induction l1 as [|a l1 IH]; simpl; [exact H2|].
  inversion H1 as [|? ? Hna Hnd]; subst. constructor.
  - intro Hin. apply in_app_or in Hin as [Hin|Hin]; [exact (Hna Hin)|].
    exact (Hd a (or_introl eq_refl) Hin).
  - apply IH; [exact Hnd|]. intros y Hy. apply Hd. right. exact Hy.
Qed.

Lemma NoDup_map_cons : forall y (l : list (list nat)),
  NoDup l -> NoDup (map (cons y) l).
Proof.
  intros y l H. induction H as [|a l Hna Hnd IH]; simpl; constructor.
  - intro Hin. apply in_map_iff in Hin as [b [Eb Hb]].
    injection Eb as Eb. subst. exact (Hna Hb).
  - exact IH.
Qed.

Lemma insert_everywhere_NoDup : forall x l, ~ In x l -> NoDup (insert_everywhere x l).
Proof.
  intros x l. induction l as [|y l IH]; intro Hn; simpl.
  - constructor; [intros []|constructor].
  - constructor.
    + intro Hin. apply in_map_iff in Hin as [q [Eq Hq]].
      injection Eq as Ey _. apply Hn. left. exact Ey.
    + apply NoDup_map_cons. apply IH. intro H. apply Hn. right. exact H.
Qed.

Fixpoint remove_first (x : nat) (l : list nat) : list nat :=
  match l with
  | [] => []
  | y :: l' => if Nat.eqb y x then l' else y :: remove_first x l'
  end.

Lemma remove_first_insert : forall x l q,
  ~ In x l -> In q (insert_everywhere x l) -> remove_first x q = l.
Proof.
  intros x l. induction l as [|y l IH]; intros q Hn Hq; simpl in Hq.
  - destruct Hq as [Hq|[]]. subst. simpl. rewrite Nat.eqb_refl. reflexivity.
  - destruct Hq as [Hq|Hq].
    + subst. simpl. rewrite Nat.eqb_refl. reflexivity.
    + apply in_map_iff in Hq as [q' [Eq Hq']]. subst q. simpl.
      destruct (Nat.eqb_spec y x) as [E|E].
      * exfalso. apply Hn. left. exact E.
      * rewrite IH; [reflexivity| |exact Hq']. intro H. apply Hn. right. exact H.
Qed.

Lemma NoDup_flat_map_disjoint : forall (A B : Type) (f : A -> list B) (l : list A),
  NoDup l -> (forall a, In a l -> NoDup (f a)) ->
  (forall a b y, In a l -> In b l -> In y (f a) -> In y (f b) -> a = b) ->
  NoDup (flat_map f l).
Proof.
  intros A B f l Hnd Hf Hdis.
  induction Hnd as [|a l Hna Hnd IH]; simpl; [constructor|].
  apply NoDup_app_disjoint.
  - apply Hf. left. reflexivity.
  - apply IH; [intros; apply Hf; right; assumption|].
    intros b c y Hb Hc. apply Hdis; right; assumption.
  - intros y Hy Hy'. apply in_flat_map in Hy' as [b [Hb Hyb]].
    assert (E : a = b)
      by (apply (Hdis a b y); [left; reflexivity|right; exact Hb|exact Hy|exact Hyb]).
    subst b. exact (Hna Hb).
Qed.

Lemma all_perms_NoDup : forall l, NoDup l -> NoDup (all_perms l).
Proof.
  intros l Hnd. induction Hnd as [|x l Hnx Hnd IH]; simpl.
  - constructor; [intros []|constructor].
  - apply NoDup_flat_map_disjoint.
    + exact IH.
    + intros p Hp. apply insert_everywhere_NoDup. intro Hx. apply Hnx.
      apply (Permutation_in _ (all_perms_perm l p Hp)). exact Hx.
    + intros p p' q Hp Hp' Hq Hq'.
      assert (Hnp : ~ In x p)
        by (intro Hx; apply Hnx; apply (Permutation_in _ (all_perms_perm l p Hp)); exact Hx).
      assert (Hnp' : ~ In x p')
        by (intro Hx; apply Hnx; apply (Permutation_in _ (all_perms_perm l p' Hp')); exact Hx).
      rewrite <- (remove_first_insert x p q Hnp Hq).
      rewrite <- (remove_first_insert x p' q Hnp' Hq'). reflexivity.
Qed.

(* the r! permutations: exactly the list of all of them, each once *)
Theorem perms_length : forall r, length (perms r) = fact r.
Proof. intro r. unfold perms. rewrite all_perms_length, length_seq. reflexivity. Qed.

Theorem perms_NoDup : forall r, NoDup (perms r).
Proof. intro r. apply all_perms_NoDup, seq_NoDup. Qed.

Theorem perms_spec : forall r p, In p (perms r) <-> is_perm r p.
Proof. intros r p. split; [apply all_perms_perm|apply all_perms_complete]. Qed.

Lemma perms_nth_perm : forall r i, i < fact r -> is_perm r (nth i (perms r) []).
Proof. intros r i Hi. apply perms_spec, nth_In. rewrite perms_length. exact Hi. Qed.

Lemma perms_nth_inj : forall r i j, i < fact r -> j < fact r ->
  nth i (perms r) [] = nth j (perms r) [] -> i = j.
Proof.
  intros r i j Hi Hj E.
  apply (proj1 (NoDup_nth (perms r) []) (perms_NoDup r));
    [rewrite perms_length; exact Hi|rewrite perms_length; exact Hj|exact E].
Qed.

(* the r = 3 list agrees with BladeDichotomy's perms3 as a set *)
Lemma perms3_is_perm : forall p, In p perms3 -> is_perm 3 p.
Proof. intros p Hp. apply perms_spec. revert Hp. vm_compute. tauto. Qed.

Lemma perm3_in_perms3 : forall p, is_perm 3 p -> In p perms3.
Proof. intros p Hp. apply perms_spec in Hp. revert Hp. vm_compute. tauto. Qed.

Example perms_4_card : length (perms 4) = 24.
Proof. vm_compute. reflexivity. Qed.

Example perms_5_card : length (perms 5) = 120.
Proof. vm_compute. reflexivity. Qed.

(* ===================================================================== *)
(* The model.  Slot-pair symmetry on the box, for values in any type.    *)
(* ===================================================================== *)

Definition diag_sym_box {V : Type} (r L M : nat) (T : list nat -> list nat -> V) : Prop :=
  forall p i j, is_perm r p -> length i = r -> length j = r ->
    inbox L i -> inbox M j -> T (pact p i) (pact p j) = T i j.

(* --- the indicator tensors of the diagonal orbits through the free cell *)
(* The orbit of ((0..r-1), h) is {(p, pact p h) : p a permutation}: the   *)
(* indicator is 1 iff i is a permutation and j = pact i h.  Symmetric at  *)
(* every (i, j) of length r (hence on every box).                          *)

Definition indic (r : nat) (h i j : list nat) : Z :=
  if andb (inb i (perms r)) (nleqb j (pact i h)) then 1%Z else 0%Z.

Lemma nleqb_iff : forall a b, nleqb a b = true <-> a = b.
Proof. intros a b. split; [apply nleqb_eq|intro E; subst; apply nleqb_refl]. Qed.

Lemma inb_iff : forall x l, inb x l = true <-> In x l.
Proof.
  intros x l. split; [apply inb_In|]. intro H. unfold inb.
  apply existsb_exists. exists x. split; [exact H|apply nleqb_refl].
Qed.

Lemma inb_perms_pact : forall r p i, is_perm r p -> length i = r ->
  inb (pact p i) (perms r) = inb i (perms r).
Proof.
  intros r p i Hp Hi. apply eq_iff_eq_true. rewrite !inb_iff, !perms_spec.
  assert (Hb := inv_perm_bound r p p Hp (is_perm_length r p Hp)).
  split; intro H.
  - replace i with (pact (inv_perm r p) (pact p i)).
    + apply is_perm_pact; [apply is_perm_inv; exact Hp|exact H].
    + rewrite (pact_assoc _ _ _ Hb), (pact_inv_l r p Hp), (pact_id r i Hi). reflexivity.
  - apply is_perm_pact; assumption.
Qed.

Lemma nleqb_pact : forall r p i j h, is_perm r p -> length i = r -> length j = r ->
  nleqb (pact p j) (pact (pact p i) h) = nleqb j (pact i h).
Proof.
  intros r p i j h Hp Hi Hj.
  assert (Hb : forall k, In k p -> k < length i).
  { intros k Hk. rewrite Hi. apply (is_perm_in r p k Hp). exact Hk. }
  rewrite <- (pact_assoc p i h Hb).
  apply eq_iff_eq_true. rewrite !nleqb_iff. split; intro E.
  - apply (pact_inj r p j (pact i h) Hp Hj); [rewrite pact_length; exact Hi|exact E].
  - rewrite E. reflexivity.
Qed.

Lemma indic_sym : forall r L M h, diag_sym_box r L M (indic r h).
Proof.
  intros r L M h p i j Hp Hi Hj _ _. unfold indic.
  rewrite (inb_perms_pact r p i Hp Hi), (nleqb_pact r p i j h Hp Hi Hj). reflexivity.
Qed.

(* at the fiber point ((0..r-1), g) the indicator of orbit h is delta_gh *)
Lemma indic_fiber : forall r g h, is_perm r g -> is_perm r h ->
  indic r h (seq 0 r) g = if nleqb g h then 1%Z else 0%Z.
Proof.
  intros r g h Hg Hh. unfold indic.
  rewrite (proj2 (inb_iff _ _) (proj2 (perms_spec r _) (is_perm_id r))).
  rewrite (pact_id r h (is_perm_length r h Hh)). reflexivity.
Qed.

(* bounded choice: one witness per index below n, assembled into a function *)
Lemma bounded_choice : forall (A : Type) (d : A) (n : nat) (Q : nat -> A -> Prop),
  (forall i, i < n -> exists a, Q i a) ->
  exists F : nat -> A, forall i, i < n -> Q i (F i).
Proof.
  intros A d n Q. induction n as [|n IH]; intro H.
  - exists (fun _ => d). intros i Hi. lia.
  - destruct (IH (fun i Hi => H i (Nat.lt_lt_succ_r _ _ Hi))) as [F HF].
    destruct (H n (Nat.lt_succ_diag_r n)) as [a Ha].
    exists (fun i => if Nat.eqb i n then a else F i). intros i Hi.
    destruct (Nat.eqb_spec i n) as [E|E]; [subst; exact Ha|apply HF; lia].
Qed.

(* --- the r! scheme as pure index algebra, values in ANY type --------- *)
(* Store P_mu (a, b) = T a (pact mu b); read T (pact s a) (pact t b) as   *)
(* P_{pact (inv_perm r s) t} (a, b).  Every index of the box, tied or     *)
(* free: this is six_component_access at every r.                        *)
Theorem factorial_component_access :
  forall (V : Type) r L M (T : list nat -> list nat -> V), diag_sym_box r L M T ->
  forall s t a b, is_perm r s -> is_perm r t -> length a = r -> length b = r ->
    inbox L a -> inbox M b ->
    T (pact s a) (pact t b) = T a (pact (pact (inv_perm r s) t) b).
Proof.
  intros V r L M T HT s t a b Hs Ht Ha Hb HaL HbM.
  assert (Hsi := is_perm_inv r s Hs).
  rewrite <- (HT (inv_perm r s) (pact s a) (pact t b) Hsi).
  - rewrite (pact_assoc _ s a (inv_perm_bound r s s Hs (is_perm_length r s Hs))).
    rewrite (pact_inv_l r s Hs), (pact_id r a Ha).
    rewrite (pact_assoc _ t b (inv_perm_bound r s t Hs (is_perm_length r t Ht))).
    reflexivity.
  - rewrite pact_length. apply is_perm_length. exact Hs.
  - rewrite pact_length. apply is_perm_length. exact Ht.
  - apply (inbox_pact L r s a Hs Ha HaL).
  - apply (inbox_pact M r t b Ht Hb HbM).
Qed.

Lemma is_perm_read : forall r s t, is_perm r s -> is_perm r t ->
  is_perm r (pact (inv_perm r s) t).
Proof. intros. apply is_perm_pact; [apply is_perm_inv|]; assumption. Qed.

(* ===================================================================== *)
(* Schemes over a ring K.                                                 *)
(* ===================================================================== *)

Section Scheme.
  Variable K : Type.
  Variables (k0 k1 : K) (kadd kmul ksub : K -> K -> K) (kopp : K -> K).
  Hypothesis Kth : ring_theory k0 k1 kadd kmul ksub kopp (@eq K).
  Add Ring scheme_ring : Kth.
  Hypothesis K_nontrivial : k1 <> k0.
  Hypothesis K_integral : forall a b, kmul a b = k0 -> a = k0 \/ b = k0.
  Hypothesis K_eq_dec : forall a b : K, {a = b} + {a <> b}.

  Local Infix "+!" := kadd (at level 50, left associativity).
  Local Infix "*!" := kmul (at level 40, left associativity).
  Local Notation ksum := (ksumf K k0 kadd).

  (* A width-m scheme reconstructs T at the cell (a, b): m stored scalars *)
  (* P (an arbitrary function of T -- existential per tensor), and the    *)
  (* fiber point (pact s a, pact t b) reads sum_k eps k s t * P k.         *)
  Definition reconstructs (r m : nat) (eps : nat -> list nat -> list nat -> K)
                          (a b : list nat) (T : list nat -> list nat -> K) : Prop :=
    exists P : nat -> K, forall s t, is_perm r s -> is_perm r t ->
      T (pact s a) (pact t b) = ksum (fun k => eps k s t *! P k) m.

  Definition sound_at (r L M m : nat) (eps : nat -> list nat -> list nat -> K)
                      (a b : list nat) : Prop :=
    forall T : list nat -> list nat -> K, diag_sym_box r L M T -> reconstructs r m eps a b T.

  Definition sound (r L M m : nat) (eps : nat -> list nat -> list nat -> K) : Prop :=
    forall a b, length a = r -> length b = r -> inbox L a -> inbox M b ->
      sound_at r L M m eps a b.

  (* =================================================================== *)
  (* PART 2 -- the lower bound.                                           *)
  (* At the free cell a = b = (0, 1, ..., r-1) -- it lies in the box when  *)
  (* L, M >= r -- the fiber points ((0..r-1), g), g in perms r, are r!    *)
  (* points in r! distinct diagonal orbits; the indicator tensors of      *)
  (* those orbits evaluate to the r! x r! identity on them.  Soundness   *)
  (* writes delta_gh = sum_k W_gk P_kh with W the weights at the points   *)
  (* and P the stored scalars: the identity factored through m numbers.  *)
  (* For m < r! the m x r! matrix P has a nonzero kernel vector v         *)
  (* (homogeneous_has_solution_dom); then v = I v = W (P v) = 0.          *)
  (* The hypothesis quantifies over 0/1 tensors only (Part B's emb        *)
  (* shape), the weakest form, so the refutation is the strongest.        *)
  (* =================================================================== *)

  Theorem free_cell_needs_factorial :
    forall r L M m (eps : nat -> list nat -> list nat -> K),
      r <= L -> r <= M -> m < fact r ->
      (forall T : list nat -> list nat -> Z, diag_sym_box r L M T ->
         exists P : nat -> K, forall s t, is_perm r s -> is_perm r t ->
           emb K k0 k1 (T (pact s (seq 0 r)) (pact t (seq 0 r)))
           = ksum (fun k => eps k s t *! P k) m) ->
      False.
  Proof.
    intros r L M m eps HL HM Hm Hsound.
    set (n := fact r).
    set (pm := fun i => nth i (perms r) []).
    assert (Hpm : forall i, i < n -> is_perm r (pm i))
      by (intros; apply perms_nth_perm; assumption).
    assert (Hinj : forall i j, i < n -> j < n -> pm i = pm j -> i = j)
      by (intros; apply (perms_nth_inj r); assumption).
    (* the stored scalars of the indicator of orbit hi, read at point gi *)
    assert (Hstore : forall hi, hi < n -> exists P : nat -> K, forall gi, gi < n ->
              (if Nat.eqb gi hi then k1 else k0)
              = ksum (fun k => eps k (seq 0 r) (pm gi) *! P k) m).
    { intros hi Hhi.
      destruct (Hsound (indic r (pm hi)) (indic_sym r L M (pm hi))) as [P HP].
      exists P. intros gi Hgi.
      specialize (HP (seq 0 r) (pm gi) (is_perm_id r) (Hpm gi Hgi)).
      rewrite (pact_id r (seq 0 r) (length_seq r 0)) in HP.
      rewrite (pact_seq r (pm gi) (Hpm gi Hgi)) in HP.
      rewrite (indic_fiber r (pm gi) (pm hi) (Hpm gi Hgi) (Hpm hi Hhi)) in HP.
      rewrite <- HP.
      destruct (Nat.eqb_spec gi hi) as [E|E].
      - subst gi. rewrite nleqb_refl. reflexivity.
      - destruct (nleqb (pm gi) (pm hi)) eqn:En; [|reflexivity].
        exfalso. apply E. apply Hinj; [exact Hgi|exact Hhi|apply nleqb_eq; exact En]. }
    destruct (bounded_choice (nat -> K) (fun _ => k0) n _ Hstore) as [Pm HPm].
    (* the m x n stored matrix (row k, column hi) kills a nonzero vector *)
    destruct (homogeneous_has_solution_dom K k0 k1 kadd kmul ksub kopp Kth
                K_nontrivial K_integral K_eq_dec m n (fun k hi => Pm hi k) Hm)
      as [u [[l0 [Hl0 Hu0]] Hsol]].
    apply Hu0.
    (* u l0 = sum_hi delta(l0, hi) u hi = sum_k W_{l0 k} (sum_hi P_{k hi} u hi) = 0 *)
    transitivity (ksum (fun hi => (if Nat.eqb l0 hi then k1 else k0) *! u hi) n).
    { symmetry.
      rewrite (ksumf_single K k0 k1 kadd kmul ksub kopp Kth _ n l0 Hl0).
      - rewrite Nat.eqb_refl. ring.
      - intros i _ Hne. destruct (Nat.eqb_spec l0 i); [congruence|ring]. }
    transitivity (ksum (fun hi => ksum (fun k =>
                    (eps k (seq 0 r) (pm l0) *! Pm hi k) *! u hi) m) n).
    { apply ksumf_ext. intros hi Hhi. rewrite (HPm hi Hhi l0 Hl0).
      transitivity (u hi *! ksum (fun k => eps k (seq 0 r) (pm l0) *! Pm hi k) m); [ring|].
      rewrite <- (ksumf_scale K k0 k1 kadd kmul ksub kopp Kth).
      apply ksumf_ext. intros k _. ring. }
    rewrite (ksumf_swap K k0 k1 kadd kmul ksub kopp Kth).
    apply (ksumf_zero K k0 k1 kadd kmul ksub kopp Kth). intros k Hk.
    rewrite (ksumf_ext K k0 kadd _
               (fun hi => eps k (seq 0 r) (pm l0) *! (Pm hi k *! u hi)) n)
      by (intros; ring).
    rewrite (ksumf_scale K k0 k1 kadd kmul ksub kopp Kth).
    rewrite (Hsol k Hk). ring.
  Qed.

  (* the same in the model's own shape: a sound scheme at the free cell *)
  (* has width at least r!                                               *)
  Theorem free_cell_width_bound : forall r L M m eps, r <= L -> r <= M ->
    sound_at r L M m eps (seq 0 r) (seq 0 r) -> fact r <= m.
  Proof.
    intros r L M m eps HL HM Hs.
    destruct (le_lt_dec (fact r) m) as [Hle|Hlt]; [exact Hle|exfalso].
    apply (free_cell_needs_factorial r L M m eps HL HM Hlt).
    intros T HT.
    destruct (Hs (fun i j => emb K k0 k1 (T i j))) as [P HP].
    - intros p i j Hp Hi Hj HiL HjM. cbn beta.
      rewrite (HT p i j Hp Hi Hj HiL HjM). reflexivity.
    - exists P. exact HP.
  Qed.

  (* =================================================================== *)
  (* PART 3 -- the upper bound in the model's shape: the width-r! scheme *)
  (* with weights eps_k (s, t) = [perms r [k] = pact (inv_perm r s) t]    *)
  (* and stored scalars P_k = T a (pact (perms r [k]) b).  Sound at       *)
  (* EVERY cell of the box -- tied or free -- for every choice of sorters. *)
  (* =================================================================== *)

  Definition feps (r k : nat) (s t : list nat) : K :=
    if nleqb (nth k (perms r) []) (pact (inv_perm r s) t) then k1 else k0.

  Theorem factorial_scheme_sound : forall r L M, sound r L M (fact r) (feps r).
  Proof.
    intros r L M a b Ha Hb HaL HbM T HT.
    exists (fun k => T a (pact (nth k (perms r) []) b)).
    intros s t Hs Ht.
    assert (Hmu := is_perm_read r s t Hs Ht).
    destruct (In_nth (perms r) _ [] (proj2 (perms_spec r _) Hmu)) as [mi [Hmi Emi]].
    rewrite perms_length in Hmi.
    rewrite (ksumf_single K k0 k1 kadd kmul ksub kopp Kth _ (fact r) mi Hmi).
    - unfold feps. rewrite Emi, nleqb_refl.
      rewrite (factorial_component_access K r L M T HT s t a b Hs Ht Ha Hb HaL HbM).
      ring.
    - intros i Hi Hne. unfold feps.
      destruct (nleqb (nth i (perms r) []) (pact (inv_perm r s) t)) eqn:E; [|ring].
      exfalso. apply Hne. apply (perms_nth_inj r i mi Hi Hmi).
      rewrite Emi. apply nleqb_eq. exact E.
  Qed.

  (* =================================================================== *)
  (* PART 4 -- the dichotomy.                                             *)
  (* =================================================================== *)

  (* THE THEOREM: at a free cell the minimal width is exactly r!.        *)
  Theorem minimal_width_is_factorial : forall r L M, r <= L -> r <= M ->
    (forall m eps, sound_at r L M m eps (seq 0 r) (seq 0 r) -> fact r <= m) /\
    sound r L M (fact r) (feps r).
  Proof.
    intros r L M HL HM. split.
    - intros m eps. apply free_cell_width_bound; assumption.
    - apply factorial_scheme_sound.
  Qed.

  (* padding a scheme with zero weights *)
  Lemma ksum_pad : forall (f : nat -> K) m m', m <= m' ->
    (forall k, m <= k -> k < m' -> f k = k0) -> ksum f m' = ksum f m.
  Proof.
    intros f m m' Hle Hz. induction m' as [|m' IH].
    - assert (m = 0) by lia. subst. reflexivity.
    - destruct (Nat.eq_dec m (S m')) as [E|E]; [subst; reflexivity|].
      simpl. rewrite IH by (try lia; intros; apply Hz; lia).
      rewrite (Hz m') by lia. ring.
  Qed.

  Theorem sound_pad : forall r L M m m' eps, m <= m' -> sound r L M m eps ->
    sound r L M m' (fun k s t => if Nat.ltb k m then eps k s t else k0).
  Proof.
    intros r L M m m' eps Hle Hs a b Ha Hb HaL HbM T HT.
    destruct (Hs a b Ha Hb HaL HbM T HT) as [P HP]. exists P. intros s t Hst Htt.
    rewrite (HP s t Hst Htt).
    rewrite (ksum_pad _ m m' Hle).
    - apply ksumf_ext. intros k Hk. destruct (Nat.ltb_spec k m); [reflexivity|lia].
    - intros k Hk1 Hk2. destruct (Nat.ltb_spec k m); [lia|ring].
  Qed.

  Lemma fact_le_2_iff : forall r, fact r <= 2 <-> r <= 2.
  Proof.
    intro r. split; intro H.
    - destruct r as [|[|[|r]]]; [lia|lia|lia|]. exfalso.
      assert (6 <= fact (S (S (S r)))) by (change 6 with (fact 3); apply fact_le; lia).
      lia.
    - destruct r as [|[|[|r]]]; simpl; lia.
  Qed.

  (* THE DICHOTOMY: two scalars per cell suffice iff r <= 2 -- BladeCauchy's *)
  (* split at r = 2 (and the trivial r <= 1), BladeDichotomy's refusal at  *)
  (* r = 3, and the refusal at every r >= 3.                              *)
  Theorem width2_iff_rank_le_2 : forall r L M, r <= L -> r <= M ->
    (exists eps, sound r L M 2 eps) <-> r <= 2.
  Proof.
    intros r L M HL HM. split.
    - intros [eps Hs]. apply fact_le_2_iff.
      apply (free_cell_width_bound r L M 2 eps HL HM).
      apply Hs; [apply length_seq|apply length_seq|apply inbox_seq; exact HL
                |apply inbox_seq; exact HM].
    - intro Hr. exists (fun k s t => if Nat.ltb k (fact r) then feps r k s t else k0).
      apply sound_pad; [apply fact_le_2_iff; exact Hr|apply factorial_scheme_sound].
  Qed.

  Corollary width2_refuted_at_every_rank_ge_3 : forall r L M eps,
    3 <= r -> r <= L -> r <= M ->
    sound_at r L M 2 eps (seq 0 r) (seq 0 r) -> False.
  Proof.
    intros r L M eps Hr HL HM Hs.
    assert (H := free_cell_width_bound r L M 2 eps HL HM Hs).
    assert (6 <= fact r) by (change 6 with (fact 3); apply fact_le; exact Hr). lia.
  Qed.

  (* --- the sanity tie-back to BladeDichotomy at r = 3 ------------------ *)
  (* BladeDichotomy's Part B hypothesis `sound` (6-list tensors, boolean  *)
  (* symmetry over cells6 3, the two weights eps1 eps2 at the fiber of    *)
  (* ((0,1,2),(0,1,2))) IS the r = 3, m = 2, L = M = 3 instance of the    *)
  (* general hypothesis; the general theorem re-derives                   *)
  (* width2_scalar_access_refuted from it (under this section's domain   *)
  (* hypotheses -- Part B's own proof needs only the ring).               *)

  Lemma cells6_shape : forall n x, In x (cells6 n) ->
    exists a b c d e f, x = [a;b;c;d;e;f] /\
      a < n /\ b < n /\ c < n /\ d < n /\ e < n /\ f < n.
  Proof.
    intros n x Hx. unfold cells6 in Hx.
    apply in_flat_map in Hx as [a [Ha Hx]]. apply in_flat_map in Hx as [b [Hb Hx]].
    apply in_flat_map in Hx as [c [Hc Hx]]. apply in_flat_map in Hx as [d [Hd Hx]].
    apply in_flat_map in Hx as [e [He Hx]]. apply in_map_iff in Hx as [f [Ex Hf]].
    apply in_seq in Ha, Hb, Hc, Hd, He, Hf.
    exists a, b, c, d, e, f. subst x. split; [reflexivity|]. repeat split; lia.
  Qed.

  Lemma firstn_exact : forall (l l' : list nat), firstn (length l) (l ++ l') = l.
  Proof.
    intros l l'. induction l as [|x l IH]; simpl; [reflexivity|]. rewrite IH. reflexivity.
  Qed.

  Lemma skipn_exact : forall (l l' : list nat), skipn (length l) (l ++ l') = l'.
  Proof. intros l l'. induction l as [|x l IH]; simpl; [reflexivity|]. exact IH. Qed.

  Lemma inbox3 : forall n a b c, a < n -> b < n -> c < n -> inbox n [a;b;c].
  Proof.
    intros. unfold inbox. constructor; [assumption|]. constructor; [assumption|].
    constructor; [assumption|]. constructor.
  Qed.

  Lemma firstn3_app : forall (l l' : list nat), length l = 3 -> firstn 3 (l ++ l') = l.
  Proof. intros l l' H. rewrite <- H. apply firstn_exact. Qed.

  Lemma skipn3_app : forall (l l' : list nat), length l = 3 -> skipn 3 (l ++ l') = l'.
  Proof. intros l l' H. rewrite <- H. apply skipn_exact. Qed.

  (* a Z-tensor symmetric on the (3,3,3) box, read as a 6-list tensor,   *)
  (* passes BladeDichotomy's boolean symmetry census                     *)
  Lemma diag_symb3_of_box : forall T : list nat -> list nat -> Z,
    diag_sym_box 3 3 3 T ->
    diag_symb 3 (fun x => T (firstn 3 x) (skipn 3 x)) = true.
  Proof.
    intros T HT. unfold diag_symb. apply forallb_forall. intros x Hx.
    apply forallb_forall. intros p Hp. apply Z.eqb_eq.
    destruct (cells6_shape 3 x Hx) as (a & b & c & d & e & f & Ex & Ha & Hb & Hc & Hd & He & Hf).
    subst x.
    assert (Hp3 := perms3_is_perm p Hp).
    assert (Hl : length p = 3) by (apply (is_perm_length 3 p Hp3)).
    cbn [diagact].
    assert (Hl3 : length (pact p [a;b;c]) = 3) by (rewrite pact_length; exact Hl).
    rewrite (firstn3_app _ _ Hl3), (skipn3_app _ _ Hl3).
    cbn [firstn skipn].
    apply (HT p [a;b;c] [d;e;f] Hp3 eq_refl eq_refl); apply inbox3; assumption.
  Qed.

  Theorem part_b_hypothesis_refuted_via_general :
    forall eps1 eps2 : list nat -> list nat -> K,
    (forall T : list nat -> Z, diag_symb 3 T = true ->
       exists p q : K, forall s t, In s perms3 -> In t perms3 ->
         emb K k0 k1 (T (pact s [0;1;2] ++ pact t [0;1;2])) = eps1 s t *! p +! eps2 s t *! q) ->
    False.
  Proof.
    intros eps1 eps2 Hsound.
    apply (free_cell_needs_factorial 3 3 3 2
             (fun k s t => if Nat.eqb k 0 then eps1 s t else eps2 s t));
      [lia|lia|cbn; lia|].
    intros T HT.
    destruct (Hsound (fun x => T (firstn 3 x) (skipn 3 x)) (diag_symb3_of_box T HT))
      as [p [q Hpq]].
    exists (fun k => if Nat.eqb k 0 then p else q).
    intros s t Hs Ht.
    specialize (Hpq s t (perm3_in_perms3 s Hs) (perm3_in_perms3 t Ht)).
    assert (Hl3 : length (pact s [0;1;2]) = 3)
      by (rewrite pact_length; apply (is_perm_length 3 s Hs)).
    rewrite (firstn3_app _ _ Hl3), (skipn3_app _ _ Hl3) in Hpq.
    change (seq 0 3) with [0;1;2]. cbn [ksumf Nat.eqb].
    rewrite Hpq. ring.
  Qed.

End Scheme.

(* ===================================================================== *)
(* The Z instance: at every rank the minimal width over the integers is   *)
(* r! (the initial ring; Q and any computable field are the same call).   *)
(* ===================================================================== *)

Theorem minimal_width_over_Z : forall r L M, r <= L -> r <= M ->
  (forall m eps, sound_at Z 0%Z Z.add Z.mul r L M m eps (seq 0 r) (seq 0 r) -> fact r <= m) /\
  sound Z 0%Z Z.add Z.mul r L M (fact r) (feps Z 0%Z 1%Z r).
Proof.
  intros r L M HL HM.
  apply (minimal_width_is_factorial Z 0%Z 1%Z Z.add Z.mul Z.sub Z.opp InitialRing.Zth);
    [discriminate| |exact Z.eq_dec|exact HL|exact HM].
  intros a b H. apply Z.mul_eq_0. exact H.
Qed.

Corollary width2_iff_rank_le_2_over_Z : forall r L M, r <= L -> r <= M ->
  (exists eps, sound Z 0%Z Z.add Z.mul r L M 2 eps) <-> r <= 2.
Proof.
  intros r L M HL HM.
  apply (width2_iff_rank_le_2 Z 0%Z 1%Z Z.add Z.mul Z.sub Z.opp InitialRing.Zth);
    [discriminate| |exact Z.eq_dec|exact HL|exact HM].
  intros a b H. apply Z.mul_eq_0. exact H.
Qed.

(* ===================================================================== *)
(* PART 5 -- cell accounting through the general definitions.             *)
(* BladeDichotomy's C(iii) pins sum_{(a,b)} |Stab(a) \ S_3 / Stab(b)| =    *)
(* #orbits = C(LM+2, 3) at extents (2,2) and (3,3) with r = 3 hard-wired.  *)
(* The same quantities defined at general r through perms r, pact and     *)
(* the general stabilizer; pinned at (3,3,3) (recovering 165), (4,2,2)    *)
(* and (4,2,3).  The identity itself at general r is not proved here.     *)
(* ===================================================================== *)

Fixpoint tuples (r n : nat) : list (list nat) :=
  match r with
  | 0 => [[]]
  | S r' => flat_map (fun x => map (cons x) (tuples r' n)) (seq 0 n)
  end.

Definition sorted_tuples (r n : nat) : list (list nat) :=
  filter (fun l => nleqb (sort_nat l) l) (tuples r n).

Definition stabr (r : nat) (a : list nat) : list (list nat) :=
  filter (fun p => nleqb (pact p a) a) (perms r).

(* the double coset of mu, named by the sorted deduped index set of     *)
(* { x . mu . y : x in alpha, y in beta } in BladeDichotomy's pmul order  *)
(* (pmul a b = pact b a, so a . mu . b = pact (pact b mu) a)              *)
Definition dcosetr (r : nat) (alpha beta : list (list nat)) (mu : list nat) : list nat :=
  dedup_nat (sort_nat
    (flat_map (fun x => map (fun y => idx_of (pact (pact y mu) x) (perms r)) beta) alpha)).

Definition ndcr (r : nat) (alpha beta : list (list nat)) : nat :=
  length (dedup_ll (map (dcosetr r alpha beta) (perms r))).

Definition dc_sumr (r nL nM : nat) : nat :=
  fold_right Nat.add 0
    (flat_map (fun a => map (fun b => ndcr r (stabr r a) (stabr r b)) (sorted_tuples r nM))
              (sorted_tuples r nL)).

(* the diagonal-orbit count through the complete invariant: the sorted   *)
(* list of pair codes nM * i_k + j_k                                      *)
Definition orbit_namer (nM : nat) (i j : list nat) : list nat :=
  sort_nat (map (fun ij => nM * fst ij + snd ij) (combine i j)).

Definition orbit_countr (r nL nM : nat) : nat :=
  length (dedup_ll
    (flat_map (fun i => map (fun j => orbit_namer nM i j) (tuples r nM)) (tuples r nL))).

Example tuples_card : length (tuples 4 2) = 16 /\ length (sorted_tuples 4 2) = 5
                      /\ length (sorted_tuples 3 3) = 10.
Proof. vm_compute. repeat split; reflexivity. Qed.

(* r = 3, extent (3,3): BladeDichotomy's cell_accounting_3_3 recovered *)
Theorem cell_accounting_general_3_3_3 :
  dc_sumr 3 3 3 = 165 /\ orbit_countr 3 3 3 = 165 /\ Cb 11 3 = 165.
Proof. vm_compute. repeat split; reflexivity. Qed.

(* r = 4: C(4 + 3, 4) = 35 and C(6 + 3, 4) = 126 *)
Theorem cell_accounting_4_2_2 :
  dc_sumr 4 2 2 = 35 /\ orbit_countr 4 2 2 = 35 /\ Cb 7 4 = 35.
Proof. vm_compute. repeat split; reflexivity. Qed.

Theorem cell_accounting_4_2_3 :
  dc_sumr 4 2 3 = 126 /\ orbit_countr 4 2 3 = 126 /\ Cb 9 4 = 126.
Proof. vm_compute. repeat split; reflexivity. Qed.

(* ===================================================================== *)
(* Closing note.  The minimal width of a scalar-access per-dimension-     *)
(* canonical scheme at a free cell is r!, at every rank, over any         *)
(* integral domain with decidable equality (Parts 2-4); the r! scheme is  *)
(* sound at every index for values in any type (Part 3); two scalars     *)
(* suffice iff r <= 2 (Part 4).  No Q[S_r]: the bound is a rank argument *)
(* on the identity matrix of the regular orbit set, and the scheme is    *)
(* index algebra.  What the group algebra IS needed for -- the isotypic   *)
(* reading of the r! numbers through the irreps of S_r, BladeDichotomy's *)
(* isotypic_access_r3 at general r -- is not here.  Also not here: the   *)
(* general identity sum_{(a,b)} |Stab(a) \ S_r / Stab(b)| = C(LM+r-1, r), *)
(* pinned by computation at (3,3,3), (4,2,2), (4,2,3) through the general *)
(* definitions; and a lower bound over a commutative ring with zero       *)
(* divisors, where Part B's determinant certificate works at r = 3 but   *)
(* the elimination here does not.                                        *)
(* ===================================================================== *)
