(* ===================================================================== *)
(* BladeDescartes.v -- EXACT RECURRENCE REDUCTION, the P7 thread         *)
(* (docs/research/exact-recurrence-reduction-proofs.md: P7a, P7b, and    *)
(* the rank statement of P7 and P4a AT EVERY POINT).                     *)
(*                                                                       *)
(* BladeRankBound proves P7's lower bound at one well-chosen point,      *)
(* where the generalized Vandermonde matrix happens to be an ordinary    *)
(* one.  The draft claims more: the rank is min(N, H) at EVERY point     *)
(* with pairwise distinct positive coordinates.  Its proof goes through  *)
(* Rolle's theorem (P7a), and Rolle's theorem is FALSE over Q -- the     *)
(* root of the derivative need not be rational -- so that proof has no   *)
(* axiom-free reading.  The statement does, by Descartes' rule of signs, *)
(* whose algebraic proof never asks for a root of anything but the       *)
(* polynomial itself:                                                    *)
(*                                                                       *)
(*   mulxc_adds_a_variation    multiplying by (X - a), a > 0, adds at    *)
(*                             least one sign variation to the           *)
(*                             coefficient sequence.  descartes_step     *)
(*                             scans W and (X - a) W together from the   *)
(*                             low end with a two-state invariant: AHEAD *)
(*                             (last signs agree, one variation up) or   *)
(*                             TIED (last signs opposite, the pending    *)
(*                             coefficient prev - a w will settle it);   *)
(*   descartes_bound           DESCARTES: a nonzero integer polynomial   *)
(*                             has at most V distinct positive roots.    *)
(*                             The factor theorem is BladeRankBound's    *)
(*                             Horner division, exact over Z             *)
(*                             (mulxc_quot);                             *)
(*   sparse_roots              P7a: at most s nonzero monomials and s    *)
(*                             distinct positive roots force zero.       *)
(*                                                                       *)
(*   generalized_vandermonde   P7b, row form: distinct positive nodes    *)
(*                             and DISTINCT exponents (no order needed)  *)
(*                             give a trivial kernel;                    *)
(*   square_kernel_transpose   a square integer matrix with a nonzero    *)
(*                             left kernel vector has a nonzero right    *)
(*                             one (drop a row the left vector weights,  *)
(*                             solve s - 1 equations in s unknowns by    *)
(*                             BladeRankBound's                          *)
(*                             homogeneous_has_solution, and the dropped *)
(*                             row follows) -- "row rank = column rank"  *)
(*                             for square matrices, without              *)
(*                             determinants;                             *)
(*   generalized_vandermonde_transpose  P7b, column form: nonsingular on *)
(*                             the other side too.                       *)
(*                                                                       *)
(*   squaring_jacobian_full_rank  P7 AT EVERY POINT: wherever the        *)
(*                             coordinates are pairwise distinct and     *)
(*                             positive, the differentials of the sums   *)
(*                             observed over min(N, Hor) steps of        *)
(*                             squaring are linearly independent;        *)
(*   moment_jacobian_full_rank  P4a at every point with pairwise         *)
(*                             distinct coordinates (an ordinary         *)
(*                             Vandermonde matrix; positivity not        *)
(*                             needed).                                  *)
(*                                                                       *)
(* Scope, stated once.  Points are INTEGER points.  A rational point     *)
(* reduces to an integer one by clearing denominators (every p_k is      *)
(* homogeneous, so the rows of the Jacobian rescale by nonzero factors); *)
(* a real point does not, and nothing here speaks to it.  "Linearly      *)
(* independent" is over Z, equivalently over Q.  These are rank          *)
(* statements about formal differentials; what a rank at a point forbids *)
(* is BladeRankBound's theorem, against POLYNOMIAL encodings only.       *)
(* Nothing here is about C^1 encodings.                                  *)
(*                                                                       *)
(* Imports BladeBinomial, BladeSummary, BladeMomentClosure,              *)
(* BladeRankBound.  Coq 8.18, stdlib only.                               *)
(* ===================================================================== *)

From Blade Require Import BladeBinomial BladeSummary BladeMomentClosure
  BladeRankBound.
Require Import List Arith Lia ZArith Ring Bool.
Import ListNotations.

Open Scope Z_scope.

Local Notation zpeval := (peval Z 0 Z.add Z.mul).

(* ===================================================================== *)
(* Part A.  Sign variations, and the one step of Descartes' rule:        *)
(* multiplying by (X - a) with a > 0 adds at least one.                  *)
(* ===================================================================== *)

(* s and c have strictly opposite signs.                                 *)
Definition opp (s c : Z) : bool :=
  ((s <? 0) && (0 <? c)) || ((0 <? s) && (c <? 0)).

Lemma opp_true : forall s c,
  opp s c = true <-> (s < 0 /\ 0 < c) \/ (0 < s /\ c < 0).
Proof.
  intros s c. unfold opp.
  destruct (Z.ltb_spec s 0), (Z.ltb_spec 0 c), (Z.ltb_spec 0 s),
           (Z.ltb_spec c 0); simpl; split; intro Hx;
    first [reflexivity | discriminate | lia | (exfalso; lia)].
Qed.

Lemma opp_false : forall s c,
  opp s c = false <-> ~ ((s < 0 /\ 0 < c) \/ (0 < s /\ c < 0)).
Proof.
  intros s c. rewrite <- opp_true. destruct (opp s c); split; intro Hx;
    first [reflexivity | discriminate | congruence
          | (exfalso; apply Hx; reflexivity)].
Qed.

Lemma opp_zero_l : forall c, opp 0 c = false.
Proof. reflexivity. Qed.

(* Sign variations of a coefficient list, zeros skipped; s is the last   *)
(* nonzero coefficient seen (0 if none yet).                             *)
Fixpoint var_from (s : Z) (l : list Z) : nat :=
  match l with
  | [] => O
  | c :: l' => if c =? 0 then var_from s l'
               else if opp s c then S (var_from c l') else var_from c l'
  end.

Definition V (l : list Z) : nat := var_from 0 l.

(* Coefficients of (X - a) W, low degree first; prev is the coefficient  *)
(* of W one degree down.                                                 *)
Fixpoint mulxc_aux (a prev : Z) (W : list Z) : list Z :=
  match W with
  | [] => [prev]
  | w :: W' => (prev - a * w) :: mulxc_aux a w W'
  end.

Definition mulxc (a : Z) (W : list Z) : list Z := mulxc_aux a 0 W.

Ltac sign_facts :=
  repeat match goal with
         | H : (_ =? _) = true |- _ => apply Z.eqb_eq in H
         | H : (_ =? _) = false |- _ => apply Z.eqb_neq in H
         | H : opp _ _ = true |- _ => apply opp_true in H
         | H : opp _ _ = false |- _ => apply opp_false in H
         end.

(* Use one instance of the induction hypothesis, whichever half applies. *)
(* The hypothesis stays quantified so that lia never sees it.            *)
Ltac try_ih IH w s1 s2 :=
  first [ (let X := fresh in
           pose proof (proj1 (IH w s1 s2) ltac:(lia) ltac:(lia)) as X; lia)
        | (let X := fresh in
           pose proof (proj2 (IH w s1 s2) ltac:(lia) ltac:(lia)) as X; lia) ].

(* Scan W and (X - a) W together from the low end.  Once W has shown a   *)
(* nonzero coefficient the product is in one of two states: AHEAD (its   *)
(* last sign agrees with W's and it is one variation up) or TIED (its    *)
(* last sign is opposite, and the pending coefficient prev - a w will    *)
(* settle it).  Stated for the variations still to come.                 *)
Lemma descartes_step : forall a, 0 < a -> forall W prev sW sP,
  (((0 < sP /\ 0 < sW) \/ (sP < 0 /\ sW < 0)) ->
   (prev = 0 \/ (0 < prev /\ 0 < sW) \/ (prev < 0 /\ sW < 0)) ->
   (var_from sW W <= var_from sP (mulxc_aux a prev W))%nat)
  /\
  (((0 < sP /\ sW < 0) \/ (sP < 0 /\ 0 < sW)) ->
   ((0 < prev /\ 0 < sW) \/ (prev < 0 /\ sW < 0)) ->
   (var_from sW W + 1 <= var_from sP (mulxc_aux a prev W))%nat).
Proof.
  intros a Ha. induction W as [|w W IH]; intros prev sW sP.
  - split; intros H1 H2; cbn [var_from mulxc_aux]; [lia|].
    destruct (prev =? 0) eqn:E0; destruct (opp sP prev) eqn:E1;
      sign_facts; lia.
  - assert (Haw : (0 < w -> 0 < a * w) /\ (w < 0 -> a * w < 0)
                  /\ (w = 0 -> a * w = 0)) by nia.
    cbn [var_from mulxc_aux].
    remember (a * w) as aw eqn:Eaw. remember (prev - aw) as p eqn:Ep.
    clear Eaw.
    split; intros H1 H2;
      destruct (w =? 0) eqn:Ew; destruct (p =? 0) eqn:Ep0;
      destruct (opp sW w) eqn:EoW; destruct (opp sP p) eqn:EoP;
      sign_facts;
      first [ lia
            | try_ih IH w sW sP | try_ih IH w sW p
            | try_ih IH w w sP | try_ih IH w w p ].
Qed.

(* Before W has shown a nonzero coefficient both scans are idle; the     *)
(* first one puts the product in the TIED state.                         *)
Lemma descartes_start : forall a, 0 < a -> forall W,
  ~ Forall (fun c => c = 0) W ->
  (var_from 0 W + 1 <= var_from 0 (mulxc_aux a 0 W))%nat.
Proof.
  intros a Ha. induction W as [|w W IH]; intros Hnz.
  - exfalso. apply Hnz. constructor.
  - cbn [var_from mulxc_aux]. destruct (Z.eqb_spec w 0) as [Hw|Hw].
    + subst w. replace (0 - a * 0) with 0 by ring.
      change (0 =? 0) with true. cbv iota. apply IH. intro HF. apply Hnz.
      constructor; [reflexivity|exact HF].
    + rewrite !opp_zero_l.
      destruct (Z.eqb_spec (0 - a * w) 0) as [Hp|Hp]; [nia|].
      apply (proj2 (descartes_step a Ha W w w (0 - a * w))); nia.
Qed.

(* (X - a) W in one line.                                                *)
Theorem mulxc_adds_a_variation : forall a W, 0 < a ->
  ~ Forall (fun c => c = 0) W -> (V W + 1 <= V (mulxc a W))%nat.
Proof. intros a W Ha Hnz. apply (descartes_start a Ha W Hnz). Qed.

(* ===================================================================== *)
(* Part B.  Descartes' bound and the draft's P7a.  The factor theorem is *)
(* BladeRankBound's Horner division, and it is EXACT over Z: no root of  *)
(* a derivative is ever asked for, which is why this works where Rolle   *)
(* does not (Rolle's theorem is false over Q).                           *)
(* ===================================================================== *)

(* The Horner quotient really is the cofactor of (X - a).                *)
Lemma mulxc_quot_tail : forall a l, l <> [] ->
  mulxc_aux a (zpeval l a) (quot a l) = l.
Proof.
  intros a. induction l as [|c l IH]; intros Hl; [contradiction|].
  destruct l as [|c' l'].
  - simpl. f_equal. ring.
  - change (quot a (c :: c' :: l'))
      with (zpeval (c' :: l') a :: quot a (c' :: l')).
    remember (c' :: l') as t eqn:Et. cbn [mulxc_aux peval].
    rewrite IH by (subst t; discriminate). f_equal. ring.
Qed.

Lemma mulxc_quot : forall a l, l <> [] -> zpeval l a = 0 ->
  mulxc a (quot a l) = l.
Proof.
  intros a l Hl H0. destruct l as [|c l]; [contradiction|].
  destruct l as [|c' l'].
  - simpl in H0. unfold mulxc. simpl. f_equal. lia.
  - change (quot a (c :: c' :: l'))
      with (zpeval (c' :: l') a :: quot a (c' :: l')).
    remember (c' :: l') as t eqn:Et. cbn [peval] in H0.
    unfold mulxc. cbn [mulxc_aux].
    rewrite mulxc_quot_tail by (subst t; discriminate). f_equal. lia.
Qed.

(* DESCARTES: a nonzero polynomial has at most V distinct positive       *)
(* roots, V the sign variations of its coefficients.                     *)
Theorem descartes_bound : forall rs : list Z, NoDup rs ->
  (forall a, In a rs -> 0 < a) ->
  forall l, ~ Forall (fun c => c = 0) l ->
    (forall a, In a rs -> zpeval l a = 0) -> (length rs <= V l)%nat.
Proof.
  induction rs as [|a rs IH]; intros Hnd Hpos l Hnz Hroots; [simpl; lia|].
  inversion Hnd as [|? ? Hnin Hnd']; subst.
  assert (Hl : l <> []) by (intro E; subst; apply Hnz; constructor).
  assert (Ha0 : zpeval l a = 0) by (apply Hroots; left; reflexivity).
  assert (HW : ~ Forall (fun c => c = 0) (quot a l)).
  { intro HF. apply Hnz. apply (quot_zero a); assumption. }
  assert (IHW : (length rs <= V (quot a l))%nat).
  { apply (IH Hnd'); [intros b Hb; apply Hpos; right; exact Hb|exact HW|].
    intros b Hb. pose proof (quot_identity a l b) as E.
    rewrite (Hroots b (or_intror Hb)), Ha0 in E.
    assert (Hne : b - a <> 0).
    { intro Hz. apply Hnin. replace a with b by lia. exact Hb. }
    symmetry in E. simpl in E. apply Z.mul_eq_0 in E.
    destruct E; [contradiction|assumption]. }
  pose proof (mulxc_adds_a_variation a (quot a l)
                (Hpos a (or_introl eq_refl)) HW) as Hs.
  rewrite (mulxc_quot a l Hl Ha0) in Hs. simpl length. lia.
Qed.

(* Number of nonzero coefficients.                                       *)
Fixpoint nnz (l : list Z) : nat :=
  match l with
  | [] => O
  | c :: l' => if c =? 0 then nnz l' else S (nnz l')
  end.

Lemma var_from_le_nnz : forall l s, (var_from s l <= nnz l)%nat.
Proof.
  induction l as [|c l IH]; intros s; cbn [var_from nnz]; [lia|].
  destruct (c =? 0); [apply IH|].
  destruct (opp s c); specialize (IH c); lia.
Qed.

Lemma V_lt_nnz : forall l, (nnz l = 0 \/ V l + 1 <= nnz l)%nat.
Proof.
  unfold V. induction l as [|c l IH]; cbn [var_from nnz];
    [left; reflexivity|].
  destruct (c =? 0); [exact IH|]. right. rewrite opp_zero_l.
  pose proof (var_from_le_nnz l c). lia.
Qed.

Lemma nnz_zero : forall l, nnz l = O -> Forall (fun c => c = 0) l.
Proof.
  induction l as [|c l IH]; cbn [nnz]; intros H; [constructor|].
  destruct (Z.eqb_spec c 0); [|discriminate]. constructor; auto.
Qed.

Lemma zero_nnz : forall l, Forall (fun c => c = 0) l -> nnz l = O.
Proof.
  induction l as [|c l IH]; intros H; [reflexivity|].
  inversion H; subst. cbn [nnz]. simpl. apply IH. assumption.
Qed.

(* P7a: a polynomial with at most s nonzero monomials and s distinct     *)
(* positive roots is zero.                                               *)
Theorem sparse_roots : forall rs l, NoDup rs ->
  (forall a, In a rs -> 0 < a) ->
  (forall a, In a rs -> zpeval l a = 0) ->
  (nnz l <= length rs)%nat -> Forall (fun c => c = 0) l.
Proof.
  intros rs l Hnd Hpos Hroots Hn.
  destruct (Nat.eq_dec (nnz l) 0) as [E|E]; [apply nnz_zero; exact E|].
  exfalso.
  assert (Hnz : ~ Forall (fun c => c = 0) l).
  { intro HF. apply E. apply zero_nnz. exact HF. }
  pose proof (descartes_bound rs Hnd Hpos l Hnz Hroots).
  destruct (V_lt_nnz l); lia.
Qed.

(* ===================================================================== *)
(* Part C.  The draft's P7b: a generalized Vandermonde matrix with       *)
(* distinct positive nodes and distinct exponents has trivial kernel --  *)
(* on both sides.                                                        *)
(* ===================================================================== *)

(* Dense coefficient of X^j in  sum_(t<s) c_t X^(E t).                   *)
Definition dcoef (c : nat -> Z) (E : nat -> nat) (s j : nat) : Z :=
  zsumf (fun t => if Nat.eqb (E t) j then c t else 0) s.

Fixpoint nmaxf (E : nat -> nat) (s : nat) : nat :=
  match s with O => O | S s' => Nat.max (nmaxf E s') (E s') end.

Lemma nmaxf_bound : forall E s t, (t < s)%nat -> (E t <= nmaxf E s)%nat.
Proof.
  intros E. induction s as [|s IH]; intros t Ht; [lia|]. cbn [nmaxf].
  destruct (Nat.eq_dec t s) as [->|Hne]; [lia|].
  specialize (IH t ltac:(lia)). lia.
Qed.

Lemma sparse_eval : forall c E s n x,
  (forall t, (t < s)%nat -> (E t < n)%nat) ->
  zsumf (fun j => dcoef c E s j * zpow x j) n
  = zsumf (fun t => c t * zpow x (E t)) s.
Proof.
  intros c E s n x Hb. unfold dcoef.
  rewrite (zsumf_ext _
             (fun j => zsumf (fun t => (if Nat.eqb (E t) j then c t else 0)
                                       * zpow x j) s) n).
  - rewrite zsumf_swap. apply zsumf_ext. intros t Ht.
    rewrite (zsumf_single _ n (E t) (Hb t Ht)).
    + rewrite Nat.eqb_refl. reflexivity.
    + intros j _ Hne. destruct (Nat.eqb_spec (E t) j); [congruence|ring].
  - intros j _. rewrite Z.mul_comm, <- zsumf_scale. apply zsumf_ext.
    intros t _. ring.
Qed.

Lemma dcoef_at : forall c E s t, (t < s)%nat ->
  (forall t1 t2, (t1 < s)%nat -> (t2 < s)%nat -> E t1 = E t2 -> t1 = t2) ->
  dcoef c E s (E t) = c t.
Proof.
  intros c E s t Ht Hinj. unfold dcoef. rewrite (zsumf_single _ s t Ht).
  - rewrite Nat.eqb_refl. reflexivity.
  - intros i Hi Hne.
    destruct (Nat.eqb_spec (E i) (E t)) as [Eq|]; [|reflexivity].
    exfalso. apply Hne. apply Hinj; assumption.
Qed.

Lemma nnz_map_add : forall (f g : nat -> Z) L,
  (nnz (map (fun j => (f j + g j)%Z) L)
   <= nnz (map f L) + nnz (map g L))%nat.
Proof.
  intros f g. induction L as [|j L IH]; [simpl; lia|]. cbn [map nnz].
  destruct (Z.eqb_spec (f j + g j) 0), (Z.eqb_spec (f j) 0),
           (Z.eqb_spec (g j) 0); lia.
Qed.

Lemma nnz_spike_out : forall e v L, ~ In e L ->
  nnz (map (fun j => if Nat.eqb e j then v else 0) L) = O.
Proof.
  intros e v. induction L as [|j L IH]; intros Hn; [reflexivity|].
  cbn [map nnz]. destruct (Nat.eqb_spec e j) as [->|Hne].
  - exfalso. apply Hn. left. reflexivity.
  - simpl. apply IH. intro H. apply Hn. right. exact H.
Qed.

Lemma nnz_spike : forall e v L, NoDup L ->
  (nnz (map (fun j => if Nat.eqb e j then v else 0%Z) L) <= 1)%nat.
Proof.
  intros e v. induction L as [|j L IH]; intros Hnd; [simpl; lia|].
  inversion Hnd as [|? ? Hnin Hnd']; subst. cbn [map nnz].
  destruct (Nat.eqb_spec e j) as [->|Hne].
  - rewrite (nnz_spike_out j v L Hnin). destruct (v =? 0); lia.
  - simpl. apply IH. exact Hnd'.
Qed.

Lemma nnz_dcoef : forall c E s L, NoDup L ->
  (nnz (map (dcoef c E s) L) <= s)%nat.
Proof.
  intros c E. induction s as [|s IH]; intros L Hnd.
  - unfold dcoef. simpl zsumf. clear Hnd.
    induction L as [|j L IHL]; [simpl; lia|]. cbn [map nnz]. simpl. exact IHL.
  - pose proof (nnz_map_add (dcoef c E s)
                  (fun j => if Nat.eqb (E s) j then c s else 0) L) as Ha.
    pose proof (nnz_spike (E s) (c s) L Hnd). specialize (IH L Hnd).
    assert (Em : map (dcoef c E (S s)) L
                 = map (fun j => dcoef c E s j
                                 + (if Nat.eqb (E s) j then c s else 0)) L)
      by (apply map_ext; intro j; reflexivity).
    rewrite Em. lia.
Qed.

(* Row form (the Descartes form): a sparse polynomial vanishing at every *)
(* node is zero.  Exponents need only be DISTINCT.                       *)
Theorem generalized_vandermonde : forall s (c : nat -> Z) (E : nat -> nat)
    (x : nat -> Z),
  (forall t t', (t < s)%nat -> (t' < s)%nat -> E t = E t' -> t = t') ->
  (forall l, (l < s)%nat -> 0 < x l) ->
  (forall l l', (l < s)%nat -> (l' < s)%nat -> x l = x l' -> l = l') ->
  (forall l, (l < s)%nat -> zsumf (fun t => c t * zpow (x l) (E t)) s = 0) ->
  forall t, (t < s)%nat -> c t = 0.
Proof.
  intros s c E x HE Hpos Hx Hroots t Ht.
  set (n := S (nmaxf E s)).
  assert (Hb : forall t0, (t0 < s)%nat -> (E t0 < n)%nat).
  { intros t0 H0. pose proof (nmaxf_bound E s t0 H0). unfold n. lia. }
  assert (HF : Forall (fun z => z = 0) (map (dcoef c E s) (seq 0 n))).
  { apply (sparse_roots (map x (seq 0 s))).
    - apply NoDup_map_in; [|apply seq_NoDup].
      intros l l' Hl Hl'. apply in_seq in Hl. apply in_seq in Hl'.
      apply Hx; lia.
    - intros a Ha. apply in_map_iff in Ha. destruct Ha as [l [<- Hl]].
      apply in_seq in Hl. apply Hpos. lia.
    - intros a Ha. apply in_map_iff in Ha. destruct Ha as [l [<- Hl]].
      apply in_seq in Hl.
      rewrite <- (zsumf_peval (dcoef c E s) (x l) n 0).
      simpl Nat.add. rewrite (sparse_eval c E s n (x l) Hb).
      apply Hroots. lia.
    - rewrite map_length, seq_length. apply nnz_dcoef. apply seq_NoDup. }
  rewrite Forall_forall in HF. rewrite <- (dcoef_at c E s t Ht HE).
  apply HF. apply in_map. apply in_seq. specialize (Hb t Ht). lia.
Qed.

(* A square integer matrix with a nonzero LEFT kernel vector has a       *)
(* nonzero RIGHT one: drop a row the left vector weights, solve the      *)
(* remaining s-1 equations in s unknowns, and the dropped row follows.   *)
Lemma skip_unskip : forall p l, l <> p -> skip p (unskip p l) = l.
Proof.
  intros p l Hne. unfold skip, unskip.
  destruct (Nat.ltb_spec l p) as [H|H].
  - destruct (Nat.ltb_spec l p); lia.
  - destruct (Nat.ltb_spec (pred l) p); lia.
Qed.

Lemma square_kernel_transpose : forall s (M : nat -> nat -> Z)
    (u : nat -> Z),
  (exists l0, (l0 < s)%nat /\ u l0 <> 0) ->
  (forall t, (t < s)%nat -> zsumf (fun l => u l * M l t) s = 0) ->
  exists c : nat -> Z,
    (exists t, (t < s)%nat /\ c t <> 0) /\
    forall l, (l < s)%nat -> zsumf (fun t => M l t * c t) s = 0.
Proof.
  intros s M u [l0 [Hl0 Hu0]] Hleft.
  destruct s as [|s']; [lia|].
  destruct (homogeneous_has_solution s' (S s')
              (fun k t => M (skip l0 k) t) ltac:(lia)) as [c [Hc Hsol]].
  exists c. split; [exact Hc|].
  set (r := fun l => zsumf (fun t => M l t * c t) (S s')).
  assert (Hall : zsumf (fun l => u l * r l) (S s') = 0).
  { unfold r.
    rewrite (zsumf_ext _
               (fun l => zsumf (fun t => c t * (u l * M l t)) (S s'))
               (S s')).
    - rewrite zsumf_swap. apply zsumf_zero. intros t Ht.
      rewrite zsumf_scale, (Hleft t Ht). ring.
    - intros l _. rewrite <- zsumf_scale. apply zsumf_ext.
      intros t _. ring. }
  assert (Hrest : forall k, (k < s')%nat -> r (skip l0 k) = 0).
  { intros k Hk. apply (Hsol k Hk). }
  assert (Hr0 : r l0 = 0).
  { rewrite (zsumf_skip _ s' l0 ltac:(lia)) in Hall.
    rewrite (zsumf_zero (fun k => u (skip l0 k) * r (skip l0 k)) s') in Hall.
    - assert (E0 : u l0 * r l0 = 0) by lia.
      apply Z.mul_eq_0 in E0. destruct E0; [contradiction|assumption].
    - intros k Hk. rewrite (Hrest k Hk). ring. }
  intros l Hl. destruct (Nat.eq_dec l l0) as [->|Hne]; [exact Hr0|].
  rewrite <- (skip_unskip l0 l Hne). apply Hrest.
  unfold unskip. destruct (Nat.ltb_spec l l0); lia.
Qed.

(* Column form: the matrix is nonsingular on the other side too.         *)
Theorem generalized_vandermonde_transpose : forall s (u : nat -> Z)
    (E : nat -> nat) (x : nat -> Z),
  (forall t t', (t < s)%nat -> (t' < s)%nat -> E t = E t' -> t = t') ->
  (forall l, (l < s)%nat -> 0 < x l) ->
  (forall l l', (l < s)%nat -> (l' < s)%nat -> x l = x l' -> l = l') ->
  (forall t, (t < s)%nat -> zsumf (fun l => u l * zpow (x l) (E t)) s = 0) ->
  forall l, (l < s)%nat -> u l = 0.
Proof.
  intros s u E x HE Hpos Hx Hcols.
  destruct (zfind_nonzero u s) as [Hz|Hnz]; [exact Hz|]. exfalso.
  destruct (square_kernel_transpose s (fun l t => zpow (x l) (E t)) u
              Hnz Hcols) as [c [[t [Ht Hct]] Hc]].
  apply Hct. apply (generalized_vandermonde s c E x HE Hpos Hx); [|exact Ht].
  intros l Hl. rewrite <- (Hc l Hl). apply zsumf_ext. intros t0 _. ring.
Qed.

(* ===================================================================== *)
(* Part D.  P7 and P4a AT EVERY POINT: the rank statements themselves.   *)
(* ===================================================================== *)

Lemma zD_pPn_unit' : forall n K x l c, (l < n)%nat -> K <> O ->
  zD (pPn n K) x (fun i => if Nat.eqb i l then c else 0)
  = Z.of_nat K * zpow (x l) (K - 1) * c.
Proof.
  intros n K x l c Hl HK. destruct K as [|K]; [contradiction|].
  replace (S K - 1)%nat with K by lia. apply zD_pPn_unit. exact Hl.
Qed.

(* P7: at EVERY point with pairwise distinct positive coordinates the    *)
(* differentials of the sums observed over min(N, Hor) steps of squaring *)
(* are linearly independent -- rank D O = min(N, Hor) there.             *)
Theorem squaring_jacobian_full_rank : forall N Hor (x : nat -> Z),
  (forall i, (i < N)%nat -> 0 < x i) ->
  (forall i i', (i < N)%nat -> (i' < N)%nat -> x i = x i' -> i = i') ->
  forall lam : nat -> Z,
    (forall v, zsumf (fun t => lam t * zD (pPn N (2 ^ t)) x v)
                     (Nat.min N Hor) = 0) ->
    forall t, (t < Nat.min N Hor)%nat -> lam t = 0.
Proof.
  intros N Hor x Hpos Hx lam Hdep t Ht.
  set (s := Nat.min N Hor) in *.
  assert (Hpow : forall k, (2 ^ k)%nat <> O)
    by (intro k; apply Nat.pow_nonzero; lia).
  assert (Hc : lam t * Z.of_nat (2 ^ t) = 0).
  { apply (generalized_vandermonde s (fun k => lam k * Z.of_nat (2 ^ k))
             (fun k => (2 ^ k - 1)%nat) x); [| | | |exact Ht].
    - intros k k' _ _ Ek. pose proof (Hpow k). pose proof (Hpow k').
      apply (Nat.pow_inj_r 2); lia.
    - intros l Hl. apply Hpos. unfold s in Hl. lia.
    - intros l l' Hl Hl'. apply Hx; unfold s in *; lia.
    - intros l Hl.
      etransitivity;
        [|apply (Hdep (fun i => if Nat.eqb i l then 1 else 0))].
      apply zsumf_ext. intros k _.
      rewrite zD_pPn_unit' by (try apply Hpow; unfold s in Hl; lia). ring. }
  apply Z.mul_eq_0 in Hc. destruct Hc as [Hc|Hc]; [exact Hc|].
  pose proof (Hpow t). lia.
Qed.

(* P4a: at every point with pairwise distinct coordinates (positivity    *)
(* not needed -- an ordinary Vandermonde matrix) the differentials of    *)
(* p_1 .. p_r are linearly independent.                                  *)
Theorem moment_jacobian_full_rank : forall r N (x : nat -> Z),
  (r <= N)%nat ->
  (forall i i', (i < r)%nat -> (i' < r)%nat -> x i = x i' -> i = i') ->
  forall lam : nat -> Z,
    (forall v, zsumf (fun k => lam k * zD (pPn N (S k)) x v) r = 0) ->
    forall k, (k < r)%nat -> lam k = 0.
Proof.
  intros r N x HrN Hx lam Hdep k Hk.
  assert (Hc : lam k * Z.of_nat (S k) = 0).
  { apply (many_roots_fn r (fun j => lam j * Z.of_nat (S j)) x Hx);
      [|exact Hk].
    intros t Ht.
    etransitivity;
      [|apply (Hdep (fun i => if Nat.eqb i t then 1 else 0))].
    apply zsumf_ext. intros j _. rewrite zD_pPn_unit by lia. ring. }
  apply Z.mul_eq_0 in Hc. destruct Hc; lia.
Qed.
