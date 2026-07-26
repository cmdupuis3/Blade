(* ===================================================================== *)
(* BladeMixedRadix.v -- the PRODUCT-SHAPE rank/unrank bijection.         *)
(*                                                                       *)
(* Context (2026 literature sweep): the per-group simplicial rank is     *)
(* classical (combinadics: Knuth TAOCP 7.2.1.3; ADOL-C tensor_address;   *)
(* Neidinger 2005, Eq. 7.4), and the SIZE of a multi-group packed        *)
(* layout as a product of per-group binomials ships in CTF's             *)
(* sy_packed_size.  But a named, formalized rank/unrank BIJECTION for    *)
(* the product index set -- per-group ranks composed mixed-radix --      *)
(* appears nowhere in the literature.  This file states and checks it.   *)
(*                                                                       *)
(* This is the POSITIVE complement to BladeCounting.v: within ONE        *)
(* identity group no lossless per-dimension product layout exists        *)
(* (counting_general_C); across DISTINCT identity groups the             *)
(* mixed-radix composition of per-group ranks IS a lossless layout,      *)
(* with cell count  prod_j C(u_j - l_j + r_j - 1, r_j)                   *)
(* (shapeCard_binom, via BladeBinomial).                                 *)
(*                                                                       *)
(* Checked claims:                                                       *)
(*   - srank_in_range   : rank lands in [0, shapeCard s)                 *)
(*   - sunrank_srank    : unrank o rank = id on canonical tuples         *)
(*   - sunrank_in       : unrank lands in the canonical set              *)
(*   - srank_sunrank    : rank o unrank = id on [0, shapeCard s)         *)
(*   - srank_injective  : the layout is collision-free                   *)
(*   - shapeCard_binom  : cell count = product of multiset coefficients  *)
(*   - mixed_radix_bijection : the packaged two-sided statement          *)
(*                                                                       *)
(* Imports BladeDMWF, BladeBinomial.  Stdlib only.                       *)
(* ===================================================================== *)

From Blade Require Import BladeDMWF BladeBinomial.
Require Import List Arith Lia.
Import ListNotations.

(* ===================================================================== *)
(* Position of a tuple in an enumeration (first occurrence).             *)
(* This is the abstract per-group rank: for enum r l u it is the lex     *)
(* position (BladeLex); its r = 2 closed form is checked in BladeSafety  *)
(* (roff).  Working with positions keeps the composition theorem         *)
(* independent of any particular per-group offset formula.               *)
(* ===================================================================== *)

Definition tuple_eq_dec := list_eq_dec Nat.eq_dec.

Fixpoint pos (x : list nat) (L : list (list nat)) : nat :=
  match L with
  | [] => 0
  | y :: L' => if tuple_eq_dec x y then 0 else S (pos x L')
  end.

Lemma pos_lt : forall L x, In x L -> pos x L < length L.
Proof.
  induction L as [|y L IH]; intros x Hin; [destruct Hin |].
  simpl. destruct (tuple_eq_dec x y) as [-> | Hne]; [lia |].
  destruct Hin as [Heq | Hin]; [congruence |].
  specialize (IH x Hin). lia.
Qed.

Lemma nth_pos : forall L x, In x L -> nth (pos x L) L [] = x.
Proof.
  induction L as [|y L IH]; intros x Hin; [destruct Hin |].
  simpl. destruct (tuple_eq_dec x y) as [-> | Hne]; [reflexivity |].
  destruct Hin as [Heq | Hin]; [congruence |].
  apply IH; exact Hin.
Qed.

Lemma pos_nth : forall L i, NoDup L -> i < length L -> pos (nth i L []) L = i.
Proof.
  induction L as [|y L IH]; intros i Hnd Hi; simpl in Hi; [lia |].
  inversion Hnd as [|? ? Hy Hnd']; subst.
  destruct i as [|i]; simpl.
  - destruct (tuple_eq_dec y y); [reflexivity | congruence].
  - destruct (tuple_eq_dec (nth i L []) y) as [Heq | Hne].
    + exfalso. apply Hy. rewrite <- Heq. apply nth_In. lia.
    + f_equal. apply IH; [exact Hnd' | lia].
Qed.

(* --- Exact firstn/skipn splitting (stdlib-version-proof).  ----------- *)

Lemma firstn_exact : forall (t1 t2 : list nat),
  firstn (length t1) (t1 ++ t2) = t1.
Proof.
  induction t1; intros; simpl; [reflexivity | f_equal; apply IHt1].
Qed.

Lemma skipn_exact : forall (t1 t2 : list nat),
  skipn (length t1) (t1 ++ t2) = t2.
Proof.
  induction t1; intros; simpl; [reflexivity | apply IHt1].
Qed.

(* --- Every enumerated tuple of one group has length r.  -------------- *)

Lemma enum_elem_length : forall r l u t, In t (enum r l u) -> length t = r.
Proof.
  induction r as [|r IH]; intros l u t Hin; simpl in Hin.
  - destruct Hin as [E | []]. subst t. reflexivity.
  - apply in_flat_map in Hin as (i & _ & Ht).
    apply in_map_iff in Ht as (t' & Et & Ht'). subst t.
    simpl. f_equal. eapply IH. exact Ht'.
Qed.

(* --- Membership in a product shape decomposes group by group.  ------- *)

Lemma enumShape_cons_inv : forall c s t,
  In t (enumShape (c :: s)) ->
  exists t1 t2, t = t1 ++ t2 /\ In t1 (enumIx c) /\ In t2 (enumShape s).
Proof.
  intros c s t Hin. simpl in Hin.
  apply in_flat_map in Hin as (t1 & H1 & Ht).
  apply in_map_iff in Ht as (t2 & Et & H2). subst t.
  eauto.
Qed.

Lemma enumShape_cons_intro : forall c s t1 t2,
  In t1 (enumIx c) -> In t2 (enumShape s) ->
  In (t1 ++ t2) (enumShape (c :: s)).
Proof.
  intros c s t1 t2 H1 H2. simpl. apply in_flat_map.
  exists t1. split; [exact H1 |].
  apply in_map_iff. eauto.
Qed.

(* ===================================================================== *)
(* Shape cardinality: the product of per-group storage sizes.            *)
(* ===================================================================== *)

Definition groupCard (c : IxRec) : nat := mscard (ix_r c) (ix_l c) (ix_u c).

Definition shapeCard (s : Shape) : nat :=
  fold_right Nat.mul 1 (map groupCard s).

Lemma groupCard_length : forall c, groupCard c = length (enumIx c).
Proof. intro c. unfold groupCard, enumIx. symmetry. apply enum_length. Qed.

Lemma shapeCard_cons : forall c s,
  shapeCard (c :: s) = groupCard c * shapeCard s.
Proof. reflexivity. Qed.

Theorem shapeCard_is_length : forall s, shapeCard s = length (enumShape s).
Proof.
  intro s. unfold shapeCard. rewrite enumShape_length.
  f_equal. apply map_ext. intro c. apply groupCard_length.
Qed.

(* The closed form: the joint packed layout across DISTINCT identity     *)
(* groups has exactly  prod_j C(u_j - l_j + r_j - 1, r_j)  cells.        *)
Corollary shapeCard_binom : forall s,
  shapeCard s
  = fold_right Nat.mul 1
      (map (fun c => C (ix_u c - ix_l c + ix_r c - 1) (ix_r c)) s).
Proof.
  intro s. unfold shapeCard. f_equal. apply map_ext. intro c.
  unfold groupCard. apply mscard_binom.
Qed.

(* ===================================================================== *)
(* THE MIXED-RADIX RANK/UNRANK PAIR.                                     *)
(* srank: per-group rank of each group's slice, composed row-major with  *)
(* radix = cardinality of the remaining shape.  sunrank: div/mod         *)
(* decomposition, inverted group by group.                               *)
(* ===================================================================== *)

Fixpoint srank (s : Shape) (t : list nat) : nat :=
  match s with
  | [] => 0
  | c :: s' =>
      pos (firstn (ix_r c) t) (enumIx c) * shapeCard s'
      + srank s' (skipn (ix_r c) t)
  end.

Fixpoint sunrank (s : Shape) (i : nat) : list nat :=
  match s with
  | [] => []
  | c :: s' =>
      nth (i / shapeCard s') (enumIx c) [] ++ sunrank s' (i mod shapeCard s')
  end.

Lemma srank_cons : forall c s t1 t2,
  In t1 (enumIx c) ->
  srank (c :: s) (t1 ++ t2)
  = pos t1 (enumIx c) * shapeCard s + srank s t2.
Proof.
  intros c s t1 t2 H1. simpl.
  assert (L : length t1 = ix_r c) by (eapply enum_elem_length; exact H1).
  rewrite <- L, firstn_exact, skipn_exact. reflexivity.
Qed.

Lemma sunrank_cons : forall c s i,
  sunrank (c :: s) i
  = nth (i / shapeCard s) (enumIx c) [] ++ sunrank s (i mod shapeCard s).
Proof. reflexivity. Qed.

(* --- Rank lands in range.  ------------------------------------------- *)

Theorem srank_in_range : forall s t,
  In t (enumShape s) -> srank s t < shapeCard s.
Proof.
  induction s as [|c s IH]; intros t Hin.
  - simpl. unfold shapeCard. simpl. lia.
  - apply enumShape_cons_inv in Hin as (t1 & t2 & Et & H1 & H2). subst t.
    rewrite srank_cons by exact H1.
    rewrite shapeCard_cons.
    assert (P1 : pos t1 (enumIx c) < groupCard c)
      by (rewrite groupCard_length; apply pos_lt; exact H1).
    assert (P2 : srank s t2 < shapeCard s) by (apply IH; exact H2).
    nia.
Qed.

(* --- Unrank inverts rank on every canonical tuple.  ------------------ *)

Theorem sunrank_srank : forall s t,
  In t (enumShape s) -> sunrank s (srank s t) = t.
Proof.
  induction s as [|c s IH]; intros t Hin.
  - simpl in Hin. destruct Hin as [E | []]. subst t. reflexivity.
  - apply enumShape_cons_inv in Hin as (t1 & t2 & Et & H1 & H2). subst t.
    rewrite srank_cons by exact H1.
    assert (Hlt : srank s t2 < shapeCard s)
      by (apply srank_in_range; exact H2).
    rewrite sunrank_cons.
    replace ((pos t1 (enumIx c) * shapeCard s + srank s t2) / shapeCard s)
      with (pos t1 (enumIx c)).
    2:{ apply Nat.div_unique with (r := srank s t2); [lia | nia]. }
    replace ((pos t1 (enumIx c) * shapeCard s + srank s t2) mod shapeCard s)
      with (srank s t2).
    2:{ apply Nat.mod_unique with (q := pos t1 (enumIx c)); [lia | nia]. }
    rewrite nth_pos by exact H1.
    rewrite (IH t2 H2). reflexivity.
Qed.

(* --- Unrank lands in the canonical set.  ----------------------------- *)

Theorem sunrank_in : forall s i,
  i < shapeCard s -> In (sunrank s i) (enumShape s).
Proof.
  induction s as [|c s IH]; intros i Hi.
  - simpl. left. reflexivity.
  - rewrite shapeCard_cons in Hi.
    assert (Hnz : shapeCard s <> 0).
    { intro E. rewrite E, Nat.mul_0_r in Hi. lia. }
    assert (Hmod : i mod shapeCard s < shapeCard s)
      by (apply Nat.mod_upper_bound; exact Hnz).
    assert (Heq : i = shapeCard s * (i / shapeCard s) + i mod shapeCard s)
      by (apply Nat.div_mod; exact Hnz).
    assert (Hdiv : i / shapeCard s < groupCard c).
    { destruct (Nat.lt_ge_cases (i / shapeCard s) (groupCard c)) as [| Hge];
        [assumption |].
      exfalso.
      assert (Hm : shapeCard s * groupCard c
                   <= shapeCard s * (i / shapeCard s))
        by (apply Nat.mul_le_mono_l; exact Hge).
      nia. }
    rewrite sunrank_cons.
    apply enumShape_cons_intro.
    + apply nth_In. rewrite <- groupCard_length. exact Hdiv.
    + apply IH. exact Hmod.
Qed.

(* --- Rank inverts unrank on every offset.  --------------------------- *)

Theorem srank_sunrank : forall s i,
  i < shapeCard s -> srank s (sunrank s i) = i.
Proof.
  induction s as [|c s IH]; intros i Hi.
  - unfold shapeCard in Hi. simpl in Hi. simpl. lia.
  - rewrite shapeCard_cons in Hi.
    assert (Hnz : shapeCard s <> 0).
    { intro E. rewrite E, Nat.mul_0_r in Hi. lia. }
    assert (Hmod : i mod shapeCard s < shapeCard s)
      by (apply Nat.mod_upper_bound; exact Hnz).
    assert (Heq : i = shapeCard s * (i / shapeCard s) + i mod shapeCard s)
      by (apply Nat.div_mod; exact Hnz).
    assert (Hdiv : i / shapeCard s < groupCard c).
    { destruct (Nat.lt_ge_cases (i / shapeCard s) (groupCard c)) as [| Hge];
        [assumption |].
      exfalso.
      assert (Hm : shapeCard s * groupCard c
                   <= shapeCard s * (i / shapeCard s))
        by (apply Nat.mul_le_mono_l; exact Hge).
      nia. }
    assert (Hin1 : In (nth (i / shapeCard s) (enumIx c) []) (enumIx c)).
    { apply nth_In. rewrite <- groupCard_length. exact Hdiv. }
    rewrite sunrank_cons.
    rewrite srank_cons by exact Hin1.
    rewrite pos_nth.
    2:{ unfold enumIx. apply enum_NoDup. }
    2:{ rewrite <- groupCard_length. exact Hdiv. }
    rewrite (IH _ Hmod). nia.
Qed.

(* --- Collision-freedom, and the packaged two-sided statement.  ------- *)

Corollary srank_injective : forall s t t',
  In t (enumShape s) -> In t' (enumShape s) ->
  srank s t = srank s t' -> t = t'.
Proof.
  intros s t t' H H' E.
  rewrite <- (sunrank_srank s t H), <- (sunrank_srank s t' H'), E.
  reflexivity.
Qed.

(* The named artifact: mixed-radix composition of per-group simplicial   *)
(* ranks is a bijection between the canonical tuples of a product of     *)
(* distinct identity groups and [0, prod_j C(u_j - l_j + r_j - 1, r_j)). *)
Theorem mixed_radix_bijection : forall s,
  (forall t, In t (enumShape s) ->
     srank s t < shapeCard s /\ sunrank s (srank s t) = t)
  /\
  (forall i, i < shapeCard s ->
     In (sunrank s i) (enumShape s) /\ srank s (sunrank s i) = i).
Proof.
  intro s. split.
  - intros t H. split; [apply srank_in_range | apply sunrank_srank]; exact H.
  - intros i H. split; [apply sunrank_in | apply srank_sunrank]; exact H.
Qed.
