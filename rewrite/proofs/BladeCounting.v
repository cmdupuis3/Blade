(* ===================================================================== *)
(* BladeCounting.v -- THE GENERAL COUNTING THEOREM.                      *)
(*                                                                       *)
(* For d >= 2 dimensions of extents n_j >= 2 and rank r >= 2:            *)
(*                                                                       *)
(*     prod_j |MS(n_j, r)|  <  |MS(prod_j n_j, r)|                       *)
(*                                                                       *)
(* where MS(n, r) = enum r 0 n is the size-r multiset space over [0,n)   *)
(* -- equivalently, prod_j C(n_j + r - 1, r) < C(prod_j n_j + r - 1, r)  *)
(* (counting_general_C, via BladeBinomial).  This is the information-    *)
(* theoretic half of the Group Law at EVERY rank and dimension count:    *)
(* product-simplex storage has strictly fewer cells than the joint       *)
(* multiset space has distinct values, so no lossless per-dimension      *)
(* product layout exists, regardless of indexing scheme.  BladeCore's    *)
(* r = 2, d = 2 arithmetic instance is subsumed.                         *)
(*                                                                       *)
(* Proof architecture (concrete, over the enumerations themselves):      *)
(*                                                                       *)
(* TWO-FACTOR CORE.  The pairing e(x, y) = x*b + y maps a pair of        *)
(* canonical tuples (pointwise) to a canonical tuple over [0, a*b)       *)
(* (zipe_canonical: monotone in both arguments), injectively             *)
(* (zipe_inj: Euclidean uniqueness of (x, y) from x*b + y with y < b).   *)
(* The tuple  w = 1 :: b :: b :: ... :: b  is canonical over [0, a*b)    *)
(* but NOT in the image: its unique pointwise decode starts              *)
(* (0,1), (1,0), and a second component beginning 1, 0 is not            *)
(* nondecreasing (witness_not_in_image).  Injectivity + missing          *)
(* witness + NoDup gives strict inequality by pigeonhole                 *)
(* (NoDup_incl_length on witness :: image).                              *)
(*                                                                       *)
(* GENERAL d.  Induction on the dimension list: multiply the induction   *)
(* hypothesis through (strict monotonicity, MS >= 1) and apply the       *)
(* two-factor core with b := the product of the remaining extents        *)
(* (>= 2 as a product of >= 2's).                                        *)
(*                                                                       *)
(* Imports BladeDMWF, BladeBinomial.  Coq 8.18, stdlib only.             *)
(* ===================================================================== *)

From Blade Require Import BladeDMWF BladeBinomial.
Require Import List Arith Lia Psatz.
Import ListNotations.

(* ---------------- generic helpers ---------------- *)

Lemma canonical_repeat : forall k v l u,
  l <= v -> v < u -> canonical k l u (repeat v k).
Proof.
  induction k as [|k IH]; intros v l u Hl Hu; simpl; [exact I |].
  split; [lia | apply IH; lia].
Qed.

Lemma In_length_pos : forall (A : Type) (x : A) (l : list A),
  In x l -> 1 <= length l.
Proof. intros A x l H. destruct l; [contradiction | simpl; lia]. Qed.

Lemma enum_nonempty : forall r n, 1 <= n -> In (repeat 0 r) (enum r 0 n).
Proof. intros. apply enum_complete. apply canonical_repeat; lia. Qed.

Lemma MS_pos : forall r n, 1 <= n -> 1 <= length (enum r 0 n).
Proof.
  intros r n H. eapply In_length_pos. apply enum_nonempty. exact H.
Qed.

Lemma NoDup_list_prod : forall (A B : Type) (l : list A) (l' : list B),
  NoDup l -> NoDup l' -> NoDup (list_prod l l').
Proof.
  intros A B l l' Hl Hl'.
  induction Hl as [|x l Hnx Hl IH]; simpl; [constructor |].
  apply NoDup_app_disjoint.
  - apply NoDup_map_inj; [exact Hl' |].
    intros u v _ _ E. injection E as E2. exact E2.
  - exact IH.
  - intros p Hp Hp2.
    apply in_map_iff in Hp as (u & <- & _).
    apply in_prod_iff in Hp2 as [Hx _].
    contradiction.
Qed.

Lemma fold_mul_pos : forall dims,
  Forall (fun m => 1 <= m) dims -> 1 <= fold_right Nat.mul 1 dims.
Proof.
  intros dims H. induction H as [|x l Hx Hl IH]; simpl; [lia | nia].
Qed.

Lemma fold_mul_ge2 : forall n dims,
  2 <= n -> Forall (fun m => 2 <= m) dims ->
  2 <= fold_right Nat.mul 1 (n :: dims).
Proof.
  intros n dims Hn HF. simpl.
  assert (Hp : 1 <= fold_right Nat.mul 1 dims).
  { induction HF as [|x l Hx Hl IHl]; simpl; [lia | nia]. }
  nia.
Qed.

(* ===================================================================== *)
(* THE TWO-FACTOR CORE.                                                  *)
(* ===================================================================== *)

Section TwoFactor.
  Variables a b : nat.
  Hypothesis Ha2 : 2 <= a.
  Hypothesis Hb2 : 2 <= b.

  (* pointwise pairing of tuples under e(x, y) = x*b + y *)
  Fixpoint zipe (s t : list nat) : list nat :=
    match s, t with
    | x :: s', y :: t' => (x * b + y) :: zipe s' t'
    | _, _ => []
    end.

  (* monotone in both arguments: canonical tuples pair to a canonical   *)
  (* tuple, with the residual lower bounds pairing along                 *)
  Lemma zipe_canonical : forall r ls lt s t,
    canonical r ls a s -> canonical r lt b t ->
    canonical r (ls * b + lt) (a * b) (zipe s t).
  Proof.
    induction r as [|r IH]; intros ls lt s t Hs Ht;
      destruct s as [|x s']; destruct t as [|y t'];
      simpl in Hs, Ht; try contradiction.
    - simpl. exact I.
    - destruct Hs as [[Hlx Hxa] Hs'].
      destruct Ht as [[Hly Hyb] Ht'].
      simpl. split.
      + split.
        * assert (Hm : ls * b <= x * b)
            by (apply Nat.mul_le_mono_r; lia).
          lia.
        * assert (Hm : x * b + b <= a * b).
          { replace (x * b + b) with ((x + 1) * b) by ring.
            apply Nat.mul_le_mono_r. lia. }
          lia.
      + apply (IH x y s' t'); assumption.
  Qed.

  (* Euclidean uniqueness: injective on canonical pairs *)
  Lemma zipe_inj : forall r ls lt ls' lt' s t s2 t2,
    canonical r ls a s -> canonical r lt b t ->
    canonical r ls' a s2 -> canonical r lt' b t2 ->
    zipe s t = zipe s2 t2 -> s = s2 /\ t = t2.
  Proof.
    induction r as [|r IH]; intros ls lt ls' lt' s t s2 t2 Hs Ht Hs2 Ht2 E;
      destruct s as [|x s']; destruct t as [|y t'];
      destruct s2 as [|x2 s2']; destruct t2 as [|y2 t2'];
      simpl in Hs, Ht, Hs2, Ht2; try contradiction.
    - split; reflexivity.
    - destruct Hs as [[_ Hxa] Hs'].
      destruct Ht as [[_ Hyb] Ht'].
      destruct Hs2 as [[_ Hx2a] Hs2'].
      destruct Ht2 as [[_ Hy2b] Ht2'].
      simpl in E. injection E as E1 E2.
      assert (Ex : x = x2) by nia.
      subst x2.
      assert (Ey : y = y2) by lia.
      subst y2.
      destruct (IH x y x y s' t' s2' t2' Hs' Ht' Hs2' Ht2' E2) as [-> ->].
      split; reflexivity.
  Qed.

  (* the witness: canonical over [0, a*b) ... *)
  Lemma witness_canonical : forall r'',
    canonical (S (S r'')) 0 (a * b) (1 :: b :: repeat b r'').
  Proof.
    intro r''. simpl. split; [split; [lia | nia] |].
    split; [split; [lia | nia] |].
    apply canonical_repeat; [lia | nia].
  Qed.

  (* ... but outside the image: the unique decode's second component    *)
  (* begins 1, 0 -- not nondecreasing.                                   *)
  Lemma witness_not_in_image : forall r'' s t,
    canonical (S (S r'')) 0 a s -> canonical (S (S r'')) 0 b t ->
    zipe s t <> 1 :: b :: repeat b r''.
  Proof.
    intros r'' s t Hs Ht E.
    destruct s as [|x [|x2 s3]]; simpl in Hs;
      [contradiction | destruct Hs as [_ []] |].
    destruct t as [|y [|y2 t3]]; simpl in Ht;
      [contradiction | destruct Ht as [_ []] |].
    simpl in E.
    destruct Hs as [[_ Hxa] [[Hxx2 Hx2a] _]].
    destruct Ht as [[_ Hyb] [[Hyy2 Hy2b] _]].
    injection E as E1 E2 _.
    assert (Hx0 : x = 0).
    { destruct x as [|x']; [reflexivity | exfalso].
      assert (Hge : 1 * b <= S x' * b) by (apply Nat.mul_le_mono_r; lia).
      rewrite Nat.mul_1_l in Hge. lia. }
    subst x. simpl in E1.
    assert (Hy20 : y2 = 0).
    { destruct x2 as [|x2'].
      - simpl in E2. lia.
      - assert (Hge : 1 * b <= S x2' * b) by (apply Nat.mul_le_mono_r; lia).
        rewrite Nat.mul_1_l in Hge. lia. }
    lia.
  Qed.

  Lemma two_factor : forall r, 2 <= r ->
    length (enum r 0 a) * length (enum r 0 b)
    < length (enum r 0 (a * b)).
  Proof.
    intros r Hr.
    destruct r as [|[|r'']]; [lia | lia |].
    set (L1 := enum (S (S r'')) 0 a).
    set (L2 := enum (S (S r'')) 0 b).
    set (LC := enum (S (S r'')) 0 (a * b)).
    set (w := 1 :: b :: repeat b r'').
    set (img := map (fun p : list nat * list nat => zipe (fst p) (snd p))
                    (list_prod L1 L2)).
    (* the image lands in the codomain *)
    assert (Hincl : incl img LC).
    { intros z Hz. apply in_map_iff in Hz as ((s, t) & Ez & Hp).
      apply in_prod_iff in Hp as [Hsm Htm].
      apply enum_sound in Hsm. apply enum_sound in Htm.
      simpl in Ez. subst z.
      apply enum_complete.
      exact (zipe_canonical (S (S r'')) 0 0 s t Hsm Htm). }
    (* the image has no duplicates *)
    assert (Hnd : NoDup img).
    { apply NoDup_map_inj.
      - apply NoDup_list_prod; apply enum_NoDup.
      - intros p q Hp Hq E.
        destruct p as (s, t). destruct q as (s2, t2). simpl in E.
        apply in_prod_iff in Hp as [Hs Ht].
        apply in_prod_iff in Hq as [Hs2 Ht2].
        apply enum_sound in Hs. apply enum_sound in Ht.
        apply enum_sound in Hs2. apply enum_sound in Ht2.
        destruct (zipe_inj (S (S r'')) 0 0 0 0 s t s2 t2
                    Hs Ht Hs2 Ht2 E) as [-> ->].
        reflexivity. }
    (* the witness is in the codomain but not the image *)
    assert (Hw_in : In w LC).
    { apply enum_complete. apply witness_canonical. }
    assert (Hw_out : ~ In w img).
    { intro Hin. apply in_map_iff in Hin as ((s, t) & Ez & Hp).
      apply in_prod_iff in Hp as [Hsm Htm].
      apply enum_sound in Hsm. apply enum_sound in Htm.
      exact (witness_not_in_image r'' s t Hsm Htm Ez). }
    (* pigeonhole: witness :: image fits inside the codomain *)
    assert (Hle : S (length img) <= length LC).
    { change (S (length img)) with (length (w :: img)).
      apply NoDup_incl_length.
      - constructor; [exact Hw_out | exact Hnd].
      - intros z [<- | Hz]; [exact Hw_in | apply Hincl; exact Hz]. }
    unfold img in Hle. rewrite map_length, prod_length in Hle.
    lia.
  Qed.
End TwoFactor.

(* ===================================================================== *)
(* THE GENERAL THEOREM: any number of dimensions.                        *)
(* ===================================================================== *)

Lemma counting_aux : forall r, 2 <= r ->
  forall dims n1,
    2 <= n1 ->
    Forall (fun n => 2 <= n) dims ->
    dims <> [] ->
    fold_right Nat.mul 1 (map (fun n => length (enum r 0 n)) (n1 :: dims))
    < length (enum r 0 (fold_right Nat.mul 1 (n1 :: dims))).
Proof.
  intros r Hr.
  induction dims as [|n2 dims IH]; intros n1 Hn1 HF Hne;
    [congruence |].
  inversion HF as [| ? ? Hn2 HF']; subst.
  destruct dims as [|n3 dims'].
  - (* exactly two dimensions *)
    simpl. rewrite !Nat.mul_1_r.
    apply two_factor; assumption.
  - (* three or more: peel n1 and chain through the two-factor core *)
    assert (Hb2 : 2 <= fold_right Nat.mul 1 (n2 :: n3 :: dims'))
      by (apply fold_mul_ge2; assumption).
    apply Nat.lt_trans with
      (length (enum r 0 n1)
       * length (enum r 0 (fold_right Nat.mul 1 (n2 :: n3 :: dims')))).
    + apply Nat.mul_lt_mono_pos_l.
      * apply MS_pos. lia.
      * apply IH; [exact Hn2 | exact HF' | congruence].
    + apply two_factor; assumption.
Qed.

Theorem counting_general : forall r dims,
  2 <= r ->
  2 <= length dims ->
  Forall (fun n => 2 <= n) dims ->
  fold_right Nat.mul 1 (map (fun n => length (enum r 0 n)) dims)
  < length (enum r 0 (fold_right Nat.mul 1 dims)).
Proof.
  intros r dims Hr Hlen HF.
  destruct dims as [|n1 dims]; [simpl in Hlen; lia |].
  inversion HF as [| ? ? Hn1 HF']; subst.
  apply counting_aux; [exact Hr | exact Hn1 | exact HF' |].
  destruct dims; [simpl in Hlen; lia | congruence].
Qed.

(* The binomial form, via BladeBinomial's closed-form identification.    *)
Corollary counting_general_C : forall r dims,
  2 <= r ->
  2 <= length dims ->
  Forall (fun n => 2 <= n) dims ->
  fold_right Nat.mul 1 (map (fun n => C (n + r - 1) r) dims)
  < C (fold_right Nat.mul 1 dims + r - 1) r.
Proof.
  intros r dims Hr Hlen HF.
  rewrite <- storage_cardinality.
  replace (map (fun n => C (n + r - 1) r) dims)
    with (map (fun n => length (enum r 0 n)) dims).
  - apply counting_general; assumption.
  - apply map_ext. intro n. apply storage_cardinality.
Qed.

(* ===================================================================== *)
(* Notes:                                                                *)
(*  - Hypothesis sharpness: all three lower bounds are necessary.        *)
(*    r = 1 gives equality (both sides prod n_j); d = 1 is trivially     *)
(*    equality; n_j = 1 factors contribute 1 to both sides (a dimension  *)
(*    of extent 1 carries no information), so a single n_j = 1 with      *)
(*    d = 2 collapses to equality.                                       *)
(*  - The witness generalizes BladeCore's 45-vs-36 counterexample: the   *)
(*    missing cell is always the one whose per-dimension decode is       *)
(*    jointly ordered but not independently ordered.                     *)
(* ===================================================================== *)
