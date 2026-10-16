(* ===================================================================== *)
(* BladeProuhetShift.v -- EXACT RECURRENCE REDUCTION, P8's refusal at a  *)
(* smaller extent, and for a POLYNOMIAL leading coefficient that is not  *)
(* identically zero (docs/research/exact-recurrence-reduction-           *)
(* proofs.md, P8, negative half).  BladeProuhet's sequel.                *)
(*                                                                       *)
(* BladeProuhet refuses closure from a pair of arrays whose FIRST        *)
(* disagreement is at p_(dr), and Prouhet's construction reaches that at *)
(* extent 2^(dr-1).  Two observations shrink and widen that:             *)
(*                                                                       *)
(*   Part A.  TRANSLATION.  For the pure power x -> x^d a pair that      *)
(*   agrees only on p_0 .. p_r and disagrees at SOME p_m, r < m <= d r,  *)
(*   already refutes closure.  Move both arrays by the same affine map   *)
(*   y -> t y + 1: they still agree on p_0 .. p_r (the affine tower),    *)
(*   and Delta p_(dr) of the images is the polynomial                    *)
(*   sum_l C(dr, l) Delta p_l t^l in t, whose t^m coefficient is         *)
(*   C(dr, m) Delta p_m <> 0.  If every G closed, that polynomial would  *)
(*   vanish at t = 0, 1, .., dr -- so it would be zero:                  *)
(*                                                                       *)
(*     power_refused_translated  the refusal from such a pair;           *)
(*     power_never_closes_2r     with Prouhet's stage r+1: every d >= 2, *)
(*                               r >= 1, every extent N >= 2^r (was      *)
(*                               2^(dr-1));                              *)
(*     squaring_refused_at_3     the ideal pair (0,3,3) / (1,1,4) at     *)
(*                               r = 2, d = 2, N = 3, computed.          *)
(*                                                                       *)
(*   Part B.  FREE ENTRIES.  BladeProuhet's general-coefficient refusal  *)
(*   asked for a collision point where a_d does not vanish, and that a   *)
(*   nonzero a_d gives one was not proved.  For a_d a POLYNOMIAL         *)
(*   in the summary it is: append common blocks -- w_k copies of         *)
(*   Prouhet's stage-(k+1) array A_k and D - w_k copies of its partner   *)
(*   B_k -- to a collision pair.  The pair stays a collision; the        *)
(*   summary moves by sum_k w_k Delta(A_k, B_k), and those directions    *)
(*   are TRIANGULAR (Delta(A_k, B_k) vanishes below coordinate k and not *)
(*   at it).  A polynomial of degree <= D that vanishes at every grid    *)
(*   choice w in {0..D}^r then vanishes everywhere, freeing one          *)
(*   coordinate at a time from the top (blocks_vanishing_is_identical);  *)
(*   no determinant, no algebraic independence:                          *)
(*                                                                       *)
(*     closure_refused_polynomial  P8's refusal for every update whose   *)
(*                               leading coefficient a_d is a polynomial *)
(*                               in the summary, nonzero at SOME         *)
(*                               r-vector; the lower a_j arbitrary       *)
(*                               functions; every extent                 *)
(*                               N >= 2^(dr-1) + deg(a_d) (2^r - 1).     *)
(*                                                                       *)
(*   Part C.  A SMALLER SEED.  (0,3,3) / (1,1,4) agree on p_0 .. p_2 and *)
(*   differ by -12 at p_3; Prouhet's recursion from it reaches a first   *)
(*   disagreement at p_m with extent 3 2^(m-3), and Delta p_m =          *)
(*   2 (-1)^m m! (seed_closed_form).  With Part A:                       *)
(*                                                                       *)
(*     power_never_closes_seeded  every d >= 2, r >= 2, every extent     *)
(*                               N >= 3 2^(r-2) -- a quarter below 2^r.  *)
(*                                                                       *)
(* Everything is over an abstract integral domain of characteristic zero *)
(* (BladeProuhet's hypotheses), so it reads at Z, Q, R and C alike.      *)
(*                                                                       *)
(* Scope, stated once.  The sharp real and complex thresholds (N >= r+2  *)
(* by Waring's formula, N = r+1 by Ritt's theorem) need the fundamental  *)
(* theorem of algebra or Ritt and are not here.  Part A is for the pure  *)
(* power only: with coefficients that are functions of the summary, the  *)
(* translated output is no longer a polynomial in t.  Part B pays        *)
(* deg(a_d) (2^r - 1) extra entries, not the r that algebraic            *)
(* independence of p_1 .. p_r would give; it does not need that          *)
(* independence.                                                         *)
(*                                                                       *)
(* Imports BladeBinomial, BladeSummary, BladeMomentClosure,              *)
(* BladeProuhet, BladeNewton.  Rocq 9.0, stdlib only.                    *)
(* ===================================================================== *)

From Blade Require Import BladeBinomial BladeSummary BladeMomentClosure
  BladeProuhet BladeNewton.
From Stdlib Require Import List Arith Lia Ring.
Import ListNotations.

Lemma C_pos : forall n k, k <= n -> C n k <> 0.
Proof.
  induction n as [|n IH]; intros k Hk.
  - assert (k = 0) by lia. subst k. discriminate.
  - destruct k as [|k]; [rewrite C_zero; discriminate|].
    change (C (S n) (S k)) with (C n k + C n (S k)).
    specialize (IH k ltac:(lia)). lia.
Qed.

Section ProuhetShift.
  Variable R : Type.
  Variables (r0 r1 : R) (radd rmul rsub : R -> R -> R) (ropp : R -> R).
  Hypothesis Rth : ring_theory r0 r1 radd rmul rsub ropp (@eq R).
  Add Ring prouhet_shift_ring : Rth.
  Hypothesis R_integral : forall a b, rmul a b = r0 -> a = r0 \/ b = r0.
  Hypothesis R_nontrivial : r1 <> r0.
  Hypothesis R_char0 : forall n, ofnat R r0 r1 radd (S n) <> r0.

  Local Infix "+!" := radd (at level 50, left associativity).
  Local Infix "*!" := rmul (at level 40, left associativity).
  Local Infix "-!" := rsub (at level 50, left associativity).

  Local Notation Psum := (psum R r0 r1 radd rmul).
  Local Notation Rsum := (rsum R r0 radd).
  Local Notation Rpow := (rpow R r1 rmul).
  Local Notation Ofnat := (ofnat R r0 r1 radd).
  Local Notation Aff := (aff R radd rmul).
  Local Notation Peval := (peval R r0 radd rmul).
  Local Notation Padd := (padd R radd).
  Local Notation Pmul := (pmul R r0 radd rmul).
  Local Notation Plpow := (plpow R r0 r1 radd rmul).
  Local Notation Mdot := (mdot R r0 r1 radd rmul).
  Local Notation Qr := (qr R r0 r1 radd rmul).
  Local Notation Dp := (Dp R r0 r1 radd rmul rsub).
  Local Notation Pte := (pte R r0 r1 radd rmul).
  Local Notation Pte_x := (pte_x R r0 r1 radd rmul).
  Local Notation Pte_x' := (pte_x' R r0 r1 radd rmul).
  Local Notation Shift1 := (shift1 R r1 radd rmul).
  Local Notation Coefs := (coefs R r0 r1 radd rmul).
  Local Notation FgenR := (Fgen R r0 r1 radd rmul).
  Local Notation Pev := (pev R radd rmul).
  Local Notation Lenv := (lenv R r0).

  Let tower := affine_moment_tower_closed R r0 r1 radd rmul rsub ropp Rth.
  Let p_app := psum_app R r0 r1 radd rmul rsub ropp Rth.
  Let o_add := ofnat_add R r0 r1 radd rmul rsub ropp Rth.
  Let r_rpow := rpow_rpow R r0 r1 radd rmul rsub ropp Rth.
  Let r_one := rpow_one R r0 r1 radd rmul rsub ropp Rth.
  Let pe_padd := peval_padd R r0 r1 radd rmul rsub ropp Rth.
  Let pe_pmul := peval_pmul R r0 r1 radd rmul rsub ropp Rth.
  Let d_zero := D_zero_eq R r0 r1 radd rmul rsub ropp Rth.
  Let d_app := D_app R r0 r1 radd rmul rsub ropp Rth.
  Let d_refl := D_refl R r0 r1 radd rmul rsub ropp Rth.
  Let d_swap := D_swap R r0 r1 radd rmul rsub ropp Rth.
  Let a_low := aff_low R r0 r1 radd rmul rsub ropp Rth.
  Let a_top := aff_top R r0 r1 radd rmul rsub ropp Rth.
  Let p_spec := pte_spec R r0 r1 radd rmul rsub ropp Rth R_integral
                  R_nontrivial R_char0.
  Let p_len := pte_length R r0 r1 radd rmul.
  Let p_Fgen := psum_Fgen R r0 r1 radd rmul rsub ropp Rth.
  Let m_diff := mdot_diff R r0 r1 radd rmul rsub ropp Rth.
  Let pl_top := plpow_top R r0 r1 radd rmul rsub ropp Rth.
  Let pl_len := plpow_length R r0 r1 radd rmul.
  Let c_len := coefs_length R r0 r1 radd rmul.
  Let c_top := coefs_top R r0 r1 radd rmul.
  Let roots := many_roots_dom R r0 r1 radd rmul rsub ropp Rth R_integral.

  (* =================================================================== *)
  (* Small facts.                                                        *)
  (* =================================================================== *)

  Lemma opp_eq_zero : forall q, ropp q = r0 -> q = r0.
  Proof.
    intros q E. replace q with (ropp (ropp q)) by ring. rewrite E. ring.
  Qed.

  Lemma add_r0_eq : forall q, q +! r0 = r0 -> q = r0.
  Proof. intros q E. replace q with (q +! r0) by ring. exact E. Qed.

  Lemma rpow_eq_zero : forall a k, Rpow a k = r0 -> a = r0.
  Proof.
    intros a k. induction k as [|k IH]; cbn [rpow]; intro E.
    - exfalso. exact (R_nontrivial E).
    - apply R_integral in E. destruct E as [E|E]; [exact E|exact (IH E)].
  Qed.

  Lemma ofnat_nonzero : forall n, n <> 0 -> Ofnat n <> r0.
  Proof. intros [|n] Hn; [congruence|apply R_char0]. Qed.

  Lemma ofnat_injective : forall n m, Ofnat n = Ofnat m -> n = m.
  Proof.
    assert (H : forall n k, Ofnat n <> Ofnat (n + S k)).
    { intros n k E. rewrite o_add in E. apply (R_char0 k).
      replace (Ofnat (S k)) with ((Ofnat n +! Ofnat (S k)) -! Ofnat n)
        by ring.
      rewrite <- E. ring. }
    intros n m E. destruct (lt_eq_lt_dec n m) as [[Hlt|Heq]|Hgt].
    - exfalso. replace m with (n + S (m - n - 1)) in E by lia. exact (H _ _ E).
    - exact Heq.
    - exfalso. replace n with (m + S (n - m - 1)) in E by lia.
      exact (H _ _ (eq_sym E)).
  Qed.

  Lemma ofnat_mul : forall a b, Ofnat (a * b) = Ofnat a *! Ofnat b.
  Proof.
    induction a as [|a IH]; intros b; [cbn [Nat.mul ofnat]; ring|].
    cbn [Nat.mul]. rewrite o_add, IH. cbn [ofnat]. ring.
  Qed.

  Lemma Forall_nth_zero : forall l j,
    Forall (fun c => c = r0) l -> nth j l r0 = r0.
  Proof.
    intros l j H. destruct (lt_dec j (length l)) as [Hj|Hj].
    - rewrite Forall_forall in H. apply H. apply nth_In. exact Hj.
    - apply nth_overflow. lia.
  Qed.

  Lemma peval_Forall_zero : forall l x,
    Forall (fun c => c = r0) l -> Peval l x = r0.
  Proof.
    induction l as [|c l IH]; intros x H; [reflexivity|].
    apply Forall_cons_iff in H. destruct H as [Hc H].
    cbn [peval]. rewrite Hc, (IH x H). ring.
  Qed.

  Lemma NoDup_map_injective : forall (f : nat -> R) l,
    (forall a b, f a = f b -> a = b) -> NoDup l -> NoDup (map f l).
  Proof.
    intros f l Hinj Hnd. induction Hnd as [|a l Hnin Hnd IH]; simpl;
      constructor; [|exact IH].
    intro Hin. apply in_map_iff in Hin. destruct Hin as [b [E Hb]].
    apply Hinj in E. subst b. contradiction.
  Qed.

  (* A coefficient list of length <= D + 1 that vanishes at the D + 1    *)
  (* points beta, beta + delta, .., beta + D delta (delta <> 0) is zero. *)
  Lemma line_coefs_zero : forall L D beta delta,
    length L <= S D -> delta <> r0 ->
    (forall n, n <= D -> Peval L (beta +! Ofnat n *! delta) = r0) ->
    Forall (fun c => c = r0) L.
  Proof.
    intros L D beta delta HL Hd Hz.
    apply (roots (map (fun n => beta +! Ofnat n *! delta) (seq 0 (S D)))).
    - apply NoDup_map_injective; [|apply seq_NoDup].
      intros n m E. apply ofnat_injective.
      assert (E2 : (Ofnat n -! Ofnat m) *! delta = r0).
      { replace ((Ofnat n -! Ofnat m) *! delta)
          with ((beta +! Ofnat n *! delta) -! (beta +! Ofnat m *! delta))
          by ring.
        rewrite E. ring. }
      apply R_integral in E2. destruct E2 as [E2|E2]; [|contradiction].
      replace (Ofnat n) with ((Ofnat n -! Ofnat m) +! Ofnat m) by ring.
      rewrite E2. ring.
    - rewrite length_map, length_seq. exact HL.
    - intros a Ha. apply in_map_iff in Ha. destruct Ha as [n [E Hn]].
      subst a. apply in_seq in Hn. apply Hz. lia.
  Qed.

  Lemma line_vanish : forall L D beta delta,
    length L <= S D -> delta <> r0 ->
    (forall n, n <= D -> Peval L (beta +! Ofnat n *! delta) = r0) ->
    forall x, Peval L x = r0.
  Proof.
    intros L D beta delta HL Hd Hz x. apply peval_Forall_zero.
    exact (line_coefs_zero L D beta delta HL Hd Hz).
  Qed.

  Lemma qr_nth : forall r j y, 1 <= j <= r ->
    nth (j - 1) (Qr r y) r0 = Psum j y.
  Proof.
    intros r j y Hj. unfold qr.
    rewrite (nth_map_lt R nat _ _ (j - 1) r0 0) by (rewrite length_seq; lia).
    rewrite seq_nth by lia. f_equal. lia.
  Qed.

  Lemma qr_length : forall r y, length (Qr r y) = r.
  Proof. intros. unfold qr. rewrite length_map, length_seq. reflexivity. Qed.

  (* =================================================================== *)
  (* Part A.  The translation lemma.  The affine map y -> t y + 1 turns  *)
  (* Delta p_n into a polynomial in t with LOW-first coefficients        *)
  (* C(n, l) Delta p_l.                                                  *)
  (* =================================================================== *)

  Lemma rsum_scale_map : forall (A : Type) (f : A -> R) c l,
    c *! Rsum (map f l) = Rsum (map (fun j => c *! f j) l).
  Proof.
    intros A f c. induction l as [|a l IH]; cbn [map rsum]; [ring|].
    rewrite <- IH. ring.
  Qed.

  Lemma rsum_sub_map : forall (A : Type) (f g : A -> R) l,
    Rsum (map f l) -! Rsum (map g l) = Rsum (map (fun j => f j -! g j) l).
  Proof.
    intros A f g. induction l as [|a l IH]; cbn [map rsum]; [ring|].
    rewrite <- IH. ring.
  Qed.

  Lemma peval_map_seq : forall (g : nat -> R) s k t,
    Peval (map g (seq k s)) t
    = Rsum (map (fun j => g (k + j) *! Rpow t j) (seq 0 s)).
  Proof.
    intros g s. induction s as [|s IH]; intros k t; [reflexivity|].
    cbn [seq map peval rsum]. rewrite IH, rsum_scale_map.
    rewrite <- (seq_shift s 0), map_map. rewrite Nat.add_0_r.
    cbn [rpow]. f_equal; [ring|]. f_equal. apply map_ext. intro j.
    replace (k + S j) with (S k + j) by lia. cbn [rpow]. ring.
  Qed.

  Lemma dp_aff_poly : forall n t A B,
    Dp n (map (Aff t r1) A) (map (Aff t r1) B)
    = Peval (map (fun l => Ofnat (C n l) *! Dp l A B) (seq 0 (S n))) t.
  Proof.
    intros n t A B. unfold BladeProuhet.Dp at 1.
    rewrite !tower, rsum_sub_map, peval_map_seq.
    f_equal. apply map_ext. intro l. cbn [Nat.add]. rewrite r_one.
    unfold BladeProuhet.Dp. ring.
  Qed.

  Lemma psum_pow : forall d j y,
    Psum j (map (fun t => Rpow t d) y) = Psum (d * j) y.
  Proof.
    intros d j y. unfold psum. rewrite map_map. f_equal. apply map_ext.
    intro t. apply r_rpow.
  Qed.

  (* A closing G forces p_(dj) to agree wherever the summary does.       *)
  Lemma power_closure_moments :
    forall d r N (G : unit -> list R -> list R) x x',
    (forall u y, length y = N ->
       Qr r (map (fun t => Rpow t d) y) = G u (Qr r y)) ->
    length x = N -> length x' = N -> Qr r x = Qr r x' ->
    forall j, 1 <= j <= r -> Psum (d * j) x = Psum (d * j) x'.
  Proof.
    intros d r N G x x' H Hx Hx' Hq j Hj.
    assert (E : Qr r (map (fun t => Rpow t d) x)
                = Qr r (map (fun t => Rpow t d) x'))
      by (rewrite (H tt x Hx), (H tt x' Hx'), Hq; reflexivity).
    apply (f_equal (fun l => nth (j - 1) l r0)) in E.
    rewrite !qr_nth, !psum_pow in E by exact Hj. exact E.
  Qed.

  (* THE TRANSLATION LEMMA.  Agreement on p_0 .. p_r and a disagreement *)
  (* at SOME p_m with r < m <= d r refute every update function of the  *)
  (* first r power sums for x -> x^d.                                    *)
  Theorem power_refused_translated : forall d r N m x x',
    length x = N -> length x' = N ->
    (forall k, k <= r -> Psum k x = Psum k x') ->
    r < m <= d * r -> Psum m x <> Psum m x' ->
    forall G : unit -> list R -> list R,
      ~ (forall u y, length y = N ->
           Qr r (map (fun t => Rpow t d) y) = G u (Qr r y)).
  Proof.
    intros d r N m x x' Hx Hx' Hlow Hm Hne G H.
    assert (Hr : 1 <= r).
    { destruct r as [|r']; [rewrite Nat.mul_0_r in Hm; lia|lia]. }
    set (cl := map (fun l => Ofnat (C (d * r) l) *! Dp l x x')
                   (seq 0 (S (d * r)))).
    assert (Hcl : Forall (fun c => c = r0) cl).
    { apply (line_coefs_zero cl (d * r) r0 r1);
        [unfold cl; rewrite length_map, length_seq; lia|exact R_nontrivial|].
      intros n _. set (t := r0 +! Ofnat n *! r1).
      assert (Hq : Qr r (map (Aff t r1) x) = Qr r (map (Aff t r1) x')).
      { unfold qr. apply map_ext_in. intros k Hk. apply in_seq in Hk.
        apply d_zero. rewrite a_low.
        - unfold BladeProuhet.Dp. rewrite (Hlow k) by lia. ring.
        - intros j Hj. unfold BladeProuhet.Dp. rewrite (Hlow j) by lia.
          ring. }
      pose proof (power_closure_moments d r N G _ _ H
                    ltac:(rewrite length_map; exact Hx)
                    ltac:(rewrite length_map; exact Hx') Hq r
                    ltac:(lia)) as E.
      unfold cl. rewrite <- (dp_aff_poly (d * r) t x x').
      unfold BladeProuhet.Dp. rewrite E. ring. }
    apply (Forall_nth_zero cl m) in Hcl. unfold cl in Hcl.
    rewrite (nth_map_lt R nat _ _ m r0 0) in Hcl
      by (rewrite length_seq; lia).
    rewrite seq_nth in Hcl by lia. cbn [Nat.add] in Hcl.
    apply R_integral in Hcl. destruct Hcl as [Hc|Hc].
    - exact (ofnat_nonzero _ (C_pos (d * r) m ltac:(lia)) Hc).
    - exact (Hne (d_zero m x x' Hc)).
  Qed.

  (* With Prouhet's stage r + 1, of extent 2^r: P8's refusal for        *)
  (* x -> x^d at every extent from 2^r on.                               *)
  Theorem power_never_closes_2r : forall d r N,
    2 <= d -> 1 <= r -> 2 ^ r <= N ->
    forall G : unit -> list R -> list R,
      ~ (forall u y, length y = N ->
           Qr r (map (fun t => Rpow t d) y) = G u (Qr r y)).
  Proof.
    intros d r N Hd Hr HN.
    destruct (p_spec (S r)) as [Hlow Htop].
    destruct (p_len r) as [L1 L2].
    apply (power_refused_translated d r N (S r) (Pte_x (S r) N)
             (Pte_x' (S r) N)).
    - unfold pte_x. rewrite length_app, repeat_length, L1.
      replace (S r - 1) with r by lia. lia.
    - unfold pte_x'. rewrite length_app, repeat_length, L2.
      replace (S r - 1) with r by lia. lia.
    - intros k Hk. apply d_zero. unfold pte_x, pte_x'.
      rewrite d_app, d_refl, (Hlow k) by lia. ring.
    - nia.
    - intro E. apply Htop.
      assert (E2 : Dp (S r) (Pte_x (S r) N) (Pte_x' (S r) N) = r0)
        by (unfold BladeProuhet.Dp; rewrite E; ring).
      unfold pte_x, pte_x' in E2. rewrite d_app, d_refl in E2.
      exact (add_r0_eq _ E2).
  Qed.

  (* =================================================================== *)
  (* Part B.  Free entries.  A polynomial leading coefficient that is    *)
  (* nonzero at some r-vector is nonzero at some collision point.        *)
  (* =================================================================== *)

  (* A closing G forces the leading coefficient to vanish at EVERY       *)
  (* collision (closure_refused_at_collision, read positively).          *)
  Lemma lead_forced_zero :
    forall d r N a (G : unit -> list R -> list R) x x',
    (forall u y, length y = N -> Qr r (FgenR d r a y) = G u (Qr r y)) ->
    2 <= d -> 1 <= r -> length x = N -> length x' = N ->
    (forall k, k < d * r -> Psum k x = Psum k x') ->
    Psum (d * r) x <> Psum (d * r) x' ->
    a d (Qr r x) = r0.
  Proof.
    intros d r N a G x x' H Hd Hr Hx Hx' Heq Hne.
    assert (Hrm : r < d * r) by nia.
    assert (Hq : Qr r x = Qr r x').
    { unfold qr. apply map_ext_in. intros k Hk. apply in_seq in Hk.
      apply Heq. lia. }
    assert (E : Qr r (FgenR d r a x) = Qr r (FgenR d r a x'))
      by (rewrite (H tt x Hx), (H tt x' Hx'), Hq; reflexivity).
    apply (f_equal (fun l => nth (r - 1) l r0)) in E.
    rewrite !(qr_nth r r) in E by lia.
    rewrite !p_Fgen in E. unfold coefs in E. rewrite <- Hq in E.
    fold (Coefs d r a x) in E.
    set (psi := Plpow (Coefs d r a x) r) in *.
    pose proof (m_diff psi 0 (d * r) x x') as Hdiff.
    cbn [Nat.add] in Hdiff. rewrite E in Hdiff.
    assert (Hz : nth (d * r) psi r0
                 *! (Psum (d * r) x -! Psum (d * r) x') = r0).
    { rewrite <- Hdiff.
      - ring.
      - exact Heq.
      - unfold psi. rewrite (pl_len _ d r (c_len d r a x)). lia. }
    apply R_integral in Hz. destruct Hz as [Hz|Hz].
    - unfold psi in Hz. rewrite (pl_top _ d r (c_len d r a x)), c_top in Hz.
      exact (rpow_eq_zero _ r Hz).
    - exfalso. apply Hne. exact (d_zero (d * r) x x' Hz).
  Qed.

  (* Polynomial expressions, read as polynomials in ONE variable.        *)
  Fixpoint pxdeg (e : pexp R) : nat :=
    match e with
    | PVar _ => 1
    | PConst _ => 0
    | PAdd e1 e2 => Nat.max (pxdeg e1) (pxdeg e2)
    | PMul e1 e2 => pxdeg e1 + pxdeg e2
    end.

  Definition rupd (rho : nat -> R) (i : nat) (v : R) : nat -> R :=
    fun l => if Nat.eqb l i then v else rho l.

  Fixpoint pline (i : nat) (rho : nat -> R) (e : pexp R) : list R :=
    match e with
    | PVar j => if Nat.eqb j i then [r0; r1] else [rho j]
    | PConst c => [c]
    | PAdd e1 e2 => Padd (pline i rho e1) (pline i rho e2)
    | PMul e1 e2 => Pmul (pline i rho e1) (pline i rho e2)
    end.

  Lemma peval_pline : forall i rho e v,
    Peval (pline i rho e) v = Pev e (rupd rho i v).
  Proof.
    intros i rho e v.
    induction e as [j|c|e1 IH1 e2 IH2|e1 IH1 e2 IH2]; cbn [pline pev].
    - unfold rupd. destruct (Nat.eqb j i); cbn [peval]; ring.
    - cbn [peval]. ring.
    - rewrite pe_padd, IH1, IH2. reflexivity.
    - rewrite pe_pmul, IH1, IH2. reflexivity.
  Qed.

  Lemma pmul_cons : forall c l1 l2,
    Pmul (c :: l1) l2 = Padd (pscale R rmul c l2) (r0 :: Pmul l1 l2).
  Proof. reflexivity. Qed.

  Lemma pmul_length_le : forall l1 l2 a b,
    length l1 <= S a -> length l2 <= S b ->
    length (Pmul l1 l2) <= S (a + b).
  Proof.
    induction l1 as [|c l1 IH]; intros l2 a b H1 H2; [simpl; lia|].
    rewrite pmul_cons, padd_length. unfold pscale. rewrite length_map.
    cbn [length]. destruct l1 as [|c' l1'].
    - cbn [pmul length]. lia.
    - destruct a as [|a']; [cbn [length] in H1; lia|].
      specialize (IH l2 a' b ltac:(cbn [length] in H1 |- *; lia) H2). lia.
  Qed.

  Lemma pline_length : forall i rho e, length (pline i rho e) <= S (pxdeg e).
  Proof.
    intros i rho.
    induction e as [j|c|e1 IH1 e2 IH2|e1 IH1 e2 IH2]; cbn [pline pxdeg].
    - destruct (Nat.eqb j i); cbn [length]; lia.
    - cbn [length]. lia.
    - rewrite padd_length. lia.
    - apply pmul_length_le; assumption.
  Qed.

  (* One coordinate at a time: a polynomial of degree <= D vanishing at  *)
  (* D + 1 points of a line vanishes on the whole line.                  *)
  Lemma pev_line_vanish : forall e rho i D beta delta,
    pxdeg e <= D -> delta <> r0 ->
    (forall n, n <= D -> Pev e (rupd rho i (beta +! Ofnat n *! delta)) = r0) ->
    forall v, Pev e (rupd rho i v) = r0.
  Proof.
    intros e rho i D beta delta HD Hd Hz v. rewrite <- peval_pline.
    apply (line_vanish (pline i rho e) D beta delta);
      [pose proof (pline_length i rho e); lia|exact Hd|].
    intros n Hn. rewrite peval_pline. apply Hz, Hn.
  Qed.

  (* The blocks.  Stage k + 1 of Prouhet's construction: A_k and B_k    *)
  (* agree on p_0 .. p_k and differ at p_(k+1).                          *)
  Definition PA (k : nat) : list R := fst (Pte (S k)).
  Definition PB (k : nat) : list R := snd (Pte (S k)).

  Fixpoint rep (n : nat) (L : list R) : list R :=
    match n with 0 => [] | S n' => L ++ rep n' L end.

  Lemma rep_length : forall n L, length (rep n L) = n * length L.
  Proof.
    induction n as [|n IH]; intros L; [reflexivity|].
    cbn [rep]. rewrite length_app, IH. reflexivity.
  Qed.

  Lemma psum_rep : forall j n L, Psum j (rep n L) = Ofnat n *! Psum j L.
  Proof.
    intros j n L. induction n as [|n IH]; cbn [rep ofnat].
    - unfold psum. cbn [map rsum]. ring.
    - rewrite p_app, IH. ring.
  Qed.

  (* w_k copies of A_k and D - w_k of B_k, for k < K.                    *)
  Fixpoint blocks (D : nat) (w : nat -> nat) (K : nat) : list R :=
    match K with
    | 0 => []
    | S K' => blocks D w K' ++ rep (w K') (PA K') ++ rep (D - w K') (PB K')
    end.

  Lemma blocks_length : forall D w K, (forall m, w m <= D) ->
    length (blocks D w K) = D * (2 ^ K - 1).
  Proof.
    intros D w K Hw. induction K as [|K IH]; [simpl; lia|].
    cbn [blocks]. rewrite !length_app, !rep_length, IH.
    destruct (p_len K) as [L1 L2]. unfold PA, PB. rewrite L1, L2.
    specialize (Hw K).
    assert (HP : 2 ^ K <> 0) by (apply Nat.pow_nonzero; discriminate).
    cbn [Nat.pow]. set (P := 2 ^ K) in *.
    replace (w K * P + (D - w K) * P) with (D * P)
      by (rewrite <- Nat.mul_add_distr_r; f_equal; lia).
    rewrite <- Nat.mul_add_distr_l. f_equal. lia.
  Qed.

  Lemma blocks_ext : forall D w w' K, (forall m, m < K -> w m = w' m) ->
    blocks D w K = blocks D w' K.
  Proof.
    intros D w w' K H. induction K as [|K IH]; [reflexivity|].
    cbn [blocks]. rewrite IH by (intros m Hm; apply H; lia).
    rewrite (H K) by lia. reflexivity.
  Qed.

  Definition nupd (w : nat -> nat) (k n : nat) : nat -> nat :=
    fun m => if Nat.eqb m k then n else w m.

  Lemma nupd_same : forall w k n, nupd w k n k = n.
  Proof. intros. unfold nupd. rewrite Nat.eqb_refl. reflexivity. Qed.

  Lemma nupd_other : forall w k n m, m <> k -> nupd w k n m = w m.
  Proof.
    intros w k n m H. unfold nupd. destruct (Nat.eqb_spec m k); [lia|].
    reflexivity.
  Qed.

  Lemma nupd_id : forall w k m, nupd w k (w k) m = w m.
  Proof.
    intros w k m. unfold nupd. destruct (Nat.eqb_spec m k) as [->|_];
      reflexivity.
  Qed.

  (* Setting w_k to n moves every power sum by n Delta(A_k, B_k).        *)
  Lemma psum_blocks_upd : forall j D w k n K, k < K -> n <= D ->
    Psum j (blocks D (nupd w k n) K)
    = Psum j (blocks D (nupd w k 0) K) +! Ofnat n *! Dp j (PA k) (PB k).
  Proof.
    intros j D w k n K Hk Hn. revert Hk.
    induction K as [|K IH]; intro Hk; [lia|].
    cbn [blocks]. rewrite !p_app, !psum_rep.
    destruct (Nat.eq_dec k K) as [<-|Hne].
    - rewrite (blocks_ext D (nupd w k n) (nupd w k 0) k)
        by (intros m Hm; rewrite !nupd_other by lia; reflexivity).
      rewrite !nupd_same, Nat.sub_0_r.
      assert (HD : Ofnat D = Ofnat n +! Ofnat (D - n))
        by (rewrite <- o_add; f_equal; lia).
      rewrite HD. unfold BladeProuhet.Dp. cbn [ofnat]. ring.
    - rewrite IH by lia.
      rewrite (nupd_other w k n K), (nupd_other w k 0 K) by lia. ring.
  Qed.

  (* The point read by the descent: coordinates below k are those of an  *)
  (* actual summary, coordinates k .. r-1 are free.                      *)
  Definition ptv (r D : nat) (X : list R) (k : nat) (w : nat -> nat)
    (t : nat -> R) : nat -> R :=
    fun l => if l <? r then (if l <? k then Psum (S l) (X ++ blocks D w r)
                             else t l)
             else r0.

  Lemma lenv_qr : forall r y l,
    Lenv (Qr r y) l = if l <? r then Psum (S l) y else r0.
  Proof.
    intros r y l. unfold lenv. cbv beta.
    destruct (Nat.ltb_spec l r) as [Hl|Hl].
    - replace l with (S l - 1) at 1 by lia. apply qr_nth. lia.
    - apply nth_overflow. rewrite qr_length. exact Hl.
  Qed.

  Lemma descent_step : forall A r D X k, pxdeg A <= D -> k < r ->
    (forall w t, (forall m, w m <= D) -> Pev A (ptv r D X (S k) w t) = r0) ->
    forall w t, (forall m, w m <= D) -> Pev A (ptv r D X k w t) = r0.
  Proof.
    intros A r D X k HD Hk Hinv w t Hw.
    destruct (p_spec (S k)) as [Hlow Htop].
    set (rho := ptv r D X (S k) w t).
    set (beta := Psum (S k) (X ++ blocks D (nupd w k 0) r)).
    assert (Hline : forall v, Pev A (rupd rho k v) = r0).
    { apply (pev_line_vanish A rho k D beta (Dp (S k) (PA k) (PB k)) HD Htop).
      intros n Hn.
      transitivity (Pev A (ptv r D X (S k) (nupd w k n) t)).
      2: { apply Hinv. intro m. unfold nupd. destruct (Nat.eqb m k);
           [exact Hn|apply Hw]. }
      apply pev_ext. intro l. unfold rupd.
      destruct (Nat.eqb_spec l k) as [->|Hlk].
      - unfold ptv. rewrite (proj2 (Nat.ltb_lt k r) Hk),
          (proj2 (Nat.ltb_lt k (S k)) (Nat.lt_succ_diag_r k)).
        unfold beta. rewrite !p_app, (psum_blocks_upd (S k) D w k n r Hk Hn).
        ring.
      - unfold rho, ptv. destruct (Nat.ltb_spec l r) as [Hlr|Hlr];
          [|reflexivity].
        destruct (Nat.ltb_spec l (S k)) as [Hls|Hls]; [|reflexivity].
        rewrite !p_app, (psum_blocks_upd (S l) D w k n r Hk Hn).
        rewrite (blocks_ext D w (nupd w k (w k)) r)
          by (intros m _; symmetry; apply nupd_id).
        rewrite (psum_blocks_upd (S l) D w k (w k) r Hk (Hw k)).
        assert (Hz : Dp (S l) (PA k) (PB k) = r0) by (apply Hlow; lia).
        rewrite Hz. ring. }
    transitivity (Pev A (rupd rho k (t k))); [|exact (Hline (t k))].
    apply pev_ext. intro l.
    unfold rupd, rho, ptv. destruct (Nat.eqb_spec l k) as [->|Hlk].
    - rewrite (proj2 (Nat.ltb_lt k r) Hk), Nat.ltb_irrefl. reflexivity.
    - destruct (l <? r); [|reflexivity].
      destruct (Nat.ltb_spec l k) as [Hl|Hl];
        destruct (Nat.ltb_spec l (S k)) as [Hl'|Hl']; try reflexivity; lia.
  Qed.

  (* THE DESCENT.  A polynomial of degree <= D that vanishes at the      *)
  (* summary of X ++ blocks for every grid choice w in {0..D}^r vanishes *)
  (* at every r-vector.                                                  *)
  Theorem blocks_vanishing_is_identical : forall A r D X, pxdeg A <= D ->
    (forall w, (forall m, w m <= D) ->
       Pev A (Lenv (Qr r (X ++ blocks D w r))) = r0) ->
    forall z, length z = r -> Pev A (Lenv z) = r0.
  Proof.
    intros A r D X HD H0 z Hz.
    assert (Hall : forall i, i <= r -> forall w t, (forall m, w m <= D) ->
                     Pev A (ptv r D X (r - i) w t) = r0).
    { induction i as [|i IH]; intros Hi w t Hw.
      - rewrite Nat.sub_0_r.
        transitivity (Pev A (Lenv (Qr r (X ++ blocks D w r))));
          [|exact (H0 w Hw)].
        apply pev_ext. intro l.
        rewrite lenv_qr. unfold ptv. destruct (Nat.ltb_spec l r);
          reflexivity.
      - apply (descent_step A r D X (r - S i) HD ltac:(lia)); [|exact Hw].
        replace (S (r - S i)) with (r - i) by lia. apply IH. lia. }
    transitivity
      (Pev A (ptv r D X (r - r) (fun _ => 0) (fun l => nth l z r0))).
    - apply pev_ext. intro l. unfold ptv, lenv. rewrite Nat.sub_diag.
      destruct (Nat.ltb_spec l r) as [Hl|Hl]; [reflexivity|].
      apply nth_overflow. lia.
    - apply Hall; [lia|]. intro m. lia.
  Qed.

  (* P8's refusal, AS THE DRAFT STATES IT, over any characteristic-0     *)
  (* integral domain: the leading coefficient a polynomial in the        *)
  (* summary that is not identically zero, the lower ones arbitrary      *)
  (* functions, against EVERY update function G.                         *)
  Theorem closure_refused_polynomial : forall d r N a (A : pexp R),
    2 <= d -> 1 <= r -> 2 ^ (d * r - 1) + pxdeg A * (2 ^ r - 1) <= N ->
    (forall z, length z = r -> a d z = Pev A (Lenv z)) ->
    (exists z, length z = r /\ Pev A (Lenv z) <> r0) ->
    forall G : unit -> list R -> list R,
      ~ (forall u y, length y = N -> Qr r (FgenR d r a y) = G u (Qr r y)).
  Proof.
    intros d r N a A Hd Hr HN Ha [z0 [Hz0 Hnz]] G H. apply Hnz.
    set (D := pxdeg A) in *.
    set (N0 := N - D * (2 ^ r - 1)).
    assert (Hm : exists m', d * r = S m') by (exists (d * r - 1); nia).
    destruct Hm as [m' Em].
    destruct (p_spec (d * r)) as [Hlow Htop].
    destruct (p_len m') as [L1 L2]. rewrite <- Em in L1, L2.
    replace (d * r - 1) with m' in HN by lia.
    apply (blocks_vanishing_is_identical A r D (Pte_x (d * r) N0) (le_n D));
      [|exact Hz0].
    intros w Hw. rewrite <- Ha by apply qr_length.
    assert (HB : length (blocks D w r) = D * (2 ^ r - 1))
      by exact (blocks_length D w r Hw).
    apply (lead_forced_zero d r N a G _ (Pte_x' (d * r) N0 ++ blocks D w r)
             H Hd Hr).
    - unfold pte_x. rewrite !length_app, repeat_length, L1, HB.
      replace (d * r - 1) with m' by lia. unfold N0. lia.
    - unfold pte_x'. rewrite !length_app, repeat_length, L2, HB.
      replace (d * r - 1) with m' by lia. unfold N0. lia.
    - intros k Hk. apply d_zero. unfold pte_x, pte_x'.
      rewrite !d_app, !d_refl, (Hlow k Hk). ring.
    - intro E. apply Htop.
      assert (E2 : Dp (d * r) (Pte_x (d * r) N0 ++ blocks D w r)
                              (Pte_x' (d * r) N0 ++ blocks D w r) = r0)
        by (unfold BladeProuhet.Dp; rewrite E; ring).
      unfold pte_x, pte_x' in E2. rewrite !d_app, !d_refl in E2.
      exact (add_r0_eq _ (add_r0_eq _ E2)).
  Qed.

  (* =================================================================== *)
  (* Part C.  A smaller seed.  Prouhet's step from ANY pair, with the    *)
  (* exact new disagreement, run from (0,3,3) / (1,1,4).                 *)
  (* =================================================================== *)

  Lemma prouhet_step : forall m A B,
    (forall j, j < m -> Dp j A B = r0) ->
    (forall j, j < S m -> Dp j (A ++ Shift1 B) (B ++ Shift1 A) = r0)
    /\ Dp (S m) (A ++ Shift1 B) (B ++ Shift1 A)
       = ropp (Ofnat (S m) *! Dp m A B).
  Proof.
    intros m A B Hlow. split.
    - intros j Hj. rewrite d_app, (d_swap j (Shift1 A) (Shift1 B)).
      unfold shift1. rewrite a_low by (intros i Hi; apply Hlow; lia).
      rewrite r_one. ring.
    - rewrite d_app, (d_swap (S m) (Shift1 A) (Shift1 B)).
      unfold shift1. rewrite (a_top r1 r1 m A B Hlow), !r_one. ring.
  Qed.

  Fixpoint pgen (A0 B0 : list R) (k : nat) : list R * list R :=
    match k with
    | 0 => (A0, B0)
    | S k' => (fst (pgen A0 B0 k') ++ Shift1 (snd (pgen A0 B0 k')),
               snd (pgen A0 B0 k') ++ Shift1 (fst (pgen A0 B0 k')))
    end.

  Theorem pgen_spec : forall A0 B0 m0,
    (forall j, j < m0 -> Dp j A0 B0 = r0) -> Dp m0 A0 B0 <> r0 ->
    forall k,
      (forall j, j < m0 + k ->
         Dp j (fst (pgen A0 B0 k)) (snd (pgen A0 B0 k)) = r0)
      /\ Dp (m0 + k) (fst (pgen A0 B0 k)) (snd (pgen A0 B0 k)) <> r0.
  Proof.
    intros A0 B0 m0 Hlow Htop k. induction k as [|k [IHlow IHtop]].
    - rewrite Nat.add_0_r. split; assumption.
    - destruct (prouhet_step (m0 + k) _ _ IHlow) as [Hl Ht].
      cbn [pgen fst snd]. rewrite Nat.add_succ_r. split; [exact Hl|].
      rewrite Ht. intro E. apply opp_eq_zero, R_integral in E.
      destruct E as [E|E]; [exact (R_char0 _ E)|exact (IHtop E)].
  Qed.

  Lemma pgen_length : forall A0 B0 n, length A0 = n -> length B0 = n ->
    forall k, length (fst (pgen A0 B0 k)) = 2 ^ k * n
              /\ length (snd (pgen A0 B0 k)) = 2 ^ k * n.
  Proof.
    intros A0 B0 n HA HB k. induction k as [|k [IH1 IH2]].
    - cbn [pgen fst snd Nat.pow]. lia.
    - cbn [pgen fst snd]. unfold shift1.
      rewrite !length_app, !length_map, IH1, IH2. cbn [Nat.pow]. lia.
  Qed.

  Definition seedA : list R := [r0; Ofnat 3; Ofnat 3].
  Definition seedB : list R := [r1; r1; Ofnat 4].

  Lemma seed_spec : (forall j, j < 3 -> Dp j seedA seedB = r0)
                    /\ Dp 3 seedA seedB = ropp (Ofnat 12).
  Proof.
    split.
    - intros j Hj. destruct j as [|[|[|j]]]; [| | |lia];
        unfold BladeProuhet.Dp, psum, seedA, seedB; cbn; ring.
    - unfold BladeProuhet.Dp, psum, seedA, seedB. cbn. ring.
  Qed.

  Lemma seed_nonzero : Dp 3 seedA seedB <> r0.
  Proof.
    rewrite (proj2 seed_spec). intro E. apply opp_eq_zero in E.
    exact (R_char0 11 E).
  Qed.

  (* Stage k of the seeded construction: extent 3 2^k, first             *)
  (* disagreement at p_(3+k), and it is 2 (-1)^(3+k) (3+k)!.             *)
  Theorem seed_closed_form : forall k,
    Dp (3 + k) (fst (pgen seedA seedB k)) (snd (pgen seedA seedB k))
    = Ofnat 2 *! Rpow (ropp r1) (3 + k) *! Ofnat (fact (3 + k)).
  Proof.
    induction k as [|k IH].
    - cbn [pgen fst snd Nat.add]. rewrite (proj2 seed_spec).
      cbn [rpow fact Nat.mul Nat.add ofnat]. ring.
    - destruct (pgen_spec seedA seedB 3 (proj1 seed_spec) seed_nonzero k)
        as [Hl _].
      replace (3 + S k) with (S (3 + k)) by lia.
      cbn [pgen fst snd]. rewrite (proj2 (prouhet_step (3 + k) _ _ Hl)), IH.
      change (fact (S (3 + k))) with (S (3 + k) * fact (3 + k)).
      rewrite ofnat_mul. cbn [rpow]. ring.
  Qed.

  (* With Part A: P8's refusal for x -> x^d at every extent from          *)
  (* 3 2^(r-2) on, r >= 2.                                               *)
  Theorem power_never_closes_seeded : forall d r N,
    2 <= d -> 2 <= r -> 3 * 2 ^ (r - 2) <= N ->
    forall G : unit -> list R -> list R,
      ~ (forall u y, length y = N ->
           Qr r (map (fun t => Rpow t d) y) = G u (Qr r y)).
  Proof.
    intros d r N Hd Hr HN.
    set (k := r - 2) in *.
    destruct (pgen_spec seedA seedB 3 (proj1 seed_spec) seed_nonzero k)
      as [Hlow Htop].
    destruct (pgen_length seedA seedB 3 eq_refl eq_refl k) as [L1 L2].
    set (pad := repeat r0 (N - 3 * 2 ^ k)).
    apply (power_refused_translated d r N (3 + k)
             (fst (pgen seedA seedB k) ++ pad)
             (snd (pgen seedA seedB k) ++ pad)).
    - unfold pad. rewrite length_app, repeat_length, L1. lia.
    - unfold pad. rewrite length_app, repeat_length, L2. lia.
    - intros j Hj. apply d_zero. rewrite d_app, d_refl, (Hlow j) by lia.
      ring.
    - unfold k. nia.
    - intro E. apply Htop.
      assert (E2 : Dp (3 + k) (fst (pgen seedA seedB k) ++ pad)
                              (snd (pgen seedA seedB k) ++ pad) = r0)
        by (unfold BladeProuhet.Dp; rewrite E; ring).
      rewrite d_app, d_refl in E2. exact (add_r0_eq _ E2).
  Qed.

End ProuhetShift.

(* ===================================================================== *)
(* Part D.  Closed instances over Z.                                     *)
(* ===================================================================== *)

From Stdlib Require Import ZArith.
Open Scope Z_scope.

(* P8 for x -> x^d over the integers from extent 2^r on.                *)
Theorem power_never_closes_2r_Z : forall d r N,
  (2 <= d)%nat -> (1 <= r)%nat -> (2 ^ r <= N)%nat ->
  forall G : unit -> list Z -> list Z,
    ~ (forall u y, length y = N ->
         qr Z 0 1 Z.add Z.mul r (map (fun t => rpow Z 1 Z.mul t d) y)
         = G u (qr Z 0 1 Z.add Z.mul r y)).
Proof.
  apply (power_never_closes_2r Z 0 1 Z.add Z.mul Z.sub Z.opp InitialRing.Zth
           Z_integral).
  - discriminate.
  - apply ofnat_Z_nonzero.
Qed.

(* ... and from 3 2^(r-2) on, r >= 2.                                    *)
Theorem power_never_closes_seeded_Z : forall d r N,
  (2 <= d)%nat -> (2 <= r)%nat -> (3 * 2 ^ (r - 2) <= N)%nat ->
  forall G : unit -> list Z -> list Z,
    ~ (forall u y, length y = N ->
         qr Z 0 1 Z.add Z.mul r (map (fun t => rpow Z 1 Z.mul t d) y)
         = G u (qr Z 0 1 Z.add Z.mul r y)).
Proof.
  apply (power_never_closes_seeded Z 0 1 Z.add Z.mul Z.sub Z.opp
           InitialRing.Zth Z_integral).
  - discriminate.
  - apply ofnat_Z_nonzero.
Qed.

(* The ideal pair (0,3,3) / (1,1,4): same extent, sum and sum of squares *)
(* (6 and 18); squared, the sums of squares are 162 and 258.             *)
Example ideal_pair_squared :
  qr Z 0 1 Z.add Z.mul 2 [0; 3; 3] = [6; 18]
  /\ qr Z 0 1 Z.add Z.mul 2 [1; 1; 4] = [6; 18]
  /\ qr Z 0 1 Z.add Z.mul 2 (map (fun t => rpow Z 1 Z.mul t 2) [0; 3; 3])
     = [18; 162]
  /\ qr Z 0 1 Z.add Z.mul 2 (map (fun t => rpow Z 1 Z.mul t 2) [1; 1; 4])
     = [18; 258].
Proof. vm_compute. repeat split. Qed.

(* So no G of (p_1, p_2) follows squaring at extent 3 = 3 2^(2-2) --     *)
(* through the translation lemma, from the disagreement at p_3 (54 vs    *)
(* 66); BladeProuhet's route needed extent 2^3 = 8.                      *)
Example squaring_refused_at_3 :
  forall G : unit -> list Z -> list Z,
    ~ (forall u y, length y = 3%nat ->
         qr Z 0 1 Z.add Z.mul 2 (map (fun t => rpow Z 1 Z.mul t 2) y)
         = G u (qr Z 0 1 Z.add Z.mul 2 y)).
Proof.
  apply (power_refused_translated Z 0 1 Z.add Z.mul Z.sub Z.opp
           InitialRing.Zth Z_integral ltac:(discriminate) ofnat_Z_nonzero
           2 2 3 3 [0; 3; 3] [1; 1; 4]); try reflexivity.
  - intros k Hk. destruct k as [|[|[|k]]]; try reflexivity; lia.
  - lia.
  - vm_compute. discriminate.
Qed.

(* The seeded construction one step on: extent 6, Delta p_4 = 819 - 771  *)
(* = 48 = 2 4!.                                                          *)
Example seeded_pair_4 :
  pgen Z 1 Z.add Z.mul (seedA Z 0 1 Z.add) (seedB Z 0 1 Z.add) 1
  = ([0; 3; 3; 2; 2; 5], [1; 1; 4; 1; 4; 4])
  /\ psum Z 0 1 Z.add Z.mul 4 [0; 3; 3; 2; 2; 5] = 819
  /\ psum Z 0 1 Z.add Z.mul 4 [1; 1; 4; 1; 4; 4] = 771.
Proof. vm_compute. repeat split. Qed.
