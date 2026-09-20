(* ===================================================================== *)
(* reals/BladeSmoothRank.v -- EXACT RECURRENCE REDUCTION over the REAL   *)
(* numbers (docs/research/exact-recurrence-reduction-proofs.md: P6, and  *)
(* P4a and P7 against DIFFERENTIABLE summaries).                         *)
(*                                                                       *)
(*       THIS FILE IS NOT PART OF THE AXIOM-FREE TOWER.                  *)
(*                                                                       *)
(* It uses Coq's standard library of real numbers, which is axiomatic.   *)
(* Print Assumptions on its theorems reports exactly two axioms:         *)
(*                                                                       *)
(*   ClassicalDedekindReals.sig_forall_dec                               *)
(*   FunctionalExtensionality.functional_extensionality_dep              *)
(*                                                                       *)
(* It lives in its own directory, is built by its own _CoqProject, and   *)
(* is excluded from count-theorems.ps1 and from the headline count.      *)
(* Every step that could be done without axioms was done elsewhere       *)
(* (BladeRankDomain, BladeDescartes); what is left here is analysis.     *)
(*                                                                       *)
(*   diff_at                   differentiability AT A POINT, two-point   *)
(*                             form.  Weaker than C^1 on a               *)
(*                             neighbourhood, so each theorem below is   *)
(*                             STRONGER than its C^1 reading;            *)
(*   diff_at_unique            the gradient is unique;                   *)
(*   chain_rule                the chain rule at a point, by the         *)
(*                             epsilon-delta argument for Frechet        *)
(*                             derivatives -- no mean value theorem;     *)
(*   smooth_rank_bound         P6: if s observations equal               *)
(*                             reconstructions of m < s summary          *)
(*                             coordinates NEAR x, summary and           *)
(*                             reconstructions differentiable at the     *)
(*                             point, then the gradients of the          *)
(*                             observations at x are linearly dependent  *)
(*                             (chain rule, uniqueness, and              *)
(*                             BladeRankDomain.factor_rows_dependent);   *)
(*   Ppow_diff                 power sums of real arrays are             *)
(*                             differentiable everywhere (the one import *)
(*                             from one-variable calculus is the         *)
(*                             derivative of t -> t^n);                  *)
(*                                                                       *)
(*   moment_summary_needs_r_smooth_coordinates  P4a over R: the first r  *)
(*                             power sums of N >= r reals are not        *)
(*                             carried by fewer than r summary           *)
(*                             coordinates;                              *)
(*   squaring_needs_min_N_H_smooth_coordinates  P7 over R: the sums      *)
(*                             observed over Hor steps of squaring need  *)
(*                             min(N, Hor) summary coordinates, so no    *)
(*                             bounded differentiable summary serves     *)
(*                             every horizon as N grows;                 *)
(*   power_never_closes_R      P8 over R against EVERY update function,  *)
(*                             continuous or not, for x -> x^d from      *)
(*                             extent 2^(dr-1) on.  No analysis:         *)
(*                             BladeProuhet's axiom-free theorem read at *)
(*                             K = R.                                    *)
(*                                                                       *)
(* The ranks are not recomputed here: BladeDescartes and BladeRankBound  *)
(* establish them exactly over Z, and                                    *)
(* BladeRankDomain.left_kernel_transfer carries them along IZR.          *)
(*                                                                       *)
(* Scope, stated once.  "Differentiable at the point" means Frechet      *)
(* differentiable in the l1 norm on the first n coordinates.  The two    *)
(* applications are proved at the integer points (0, 1, 2, ...) and      *)
(* (1, 2, 3, ...); a lower bound needs one point.  The draft's P8 at ITS *)
(* threshold N >= d r needs real arrays agreeing on p_1 .. p_(dr-1) and  *)
(* differing on p_(dr) at extent d r -- the inverse function theorem, or *)
(* the roots of T_m(X) = c -- and is not here.  Exact real arithmetic;   *)
(* nothing about floating point.                                         *)
(*                                                                       *)
(* Imports the Blade tower (BladeRankDomain, BladeProuhet and below) and *)
(* Coq's Reals.                                                          *)
(* ===================================================================== *)

From Blade Require Import BladeBinomial BladeSummary BladeMomentClosure
  BladeRankBound BladeDescartes BladeRankDomain BladeProuhet.
Require Import Reals Lra List Arith Lia ZArith.
Import ListNotations.

Open Scope R_scope.

(* ===================================================================== *)
(* Part A.  Finite sums of reals: BladeRankDomain's sums at K = R, plus  *)
(* the order facts analysis needs.                                       *)
(* ===================================================================== *)

Notation Rsumf := (ksumf R 0 Rplus).

Definition Rsumf_ext := ksumf_ext R 0 Rplus.
Definition Rsumf_zero := ksumf_zero R 0 1 Rplus Rmult Rminus Ropp RTheory.
Definition Rsumf_add := ksumf_add R 0 1 Rplus Rmult Rminus Ropp RTheory.
Definition Rsumf_scale := ksumf_scale R 0 1 Rplus Rmult Rminus Ropp RTheory.
Definition Rsumf_swap := ksumf_swap R 0 1 Rplus Rmult Rminus Ropp RTheory.
Definition Rsumf_single :=
  ksumf_single R 0 1 Rplus Rmult Rminus Ropp RTheory.

Lemma Rsumf_le : forall f g n,
  (forall i, (i < n)%nat -> f i <= g i) -> Rsumf f n <= Rsumf g n.
Proof.
  intros f g. induction n as [|n IH]; intros H; simpl; [lra|].
  assert (Rsumf f n <= Rsumf g n) by (apply IH; intros; apply H; lia).
  assert (f n <= g n) by (apply H; lia). lra.
Qed.

Lemma Rsumf_nonneg : forall f n,
  (forall i, (i < n)%nat -> 0 <= f i) -> 0 <= Rsumf f n.
Proof.
  intros f. induction n as [|n IH]; intros H; simpl; [lra|].
  assert (0 <= Rsumf f n) by (apply IH; intros; apply H; lia).
  assert (0 <= f n) by (apply H; lia). lra.
Qed.

Lemma Rsumf_abs : forall f n,
  Rabs (Rsumf f n) <= Rsumf (fun i => Rabs (f i)) n.
Proof.
  intros f. induction n as [|n IH]; simpl.
  - rewrite Rabs_R0. lra.
  - pose proof (Rabs_triang (Rsumf f n) (f n)). lra.
Qed.

Lemma Rsumf_term_le : forall f n i,
  (forall j, (j < n)%nat -> 0 <= f j) -> (i < n)%nat -> f i <= Rsumf f n.
Proof.
  intros f. induction n as [|n IH]; intros i H Hi; [lia|]. simpl.
  assert (0 <= Rsumf f n) by (apply Rsumf_nonneg; intros; apply H; lia).
  assert (0 <= f n) by (apply H; lia).
  destruct (Nat.eq_dec i n) as [->|Hne]; [lra|].
  assert (f i <= Rsumf f n) by (apply IH; [intros; apply H; lia|lia]). lra.
Qed.

Lemma Rsumf_minus : forall f g n,
  Rsumf (fun i => f i - g i) n = Rsumf f n - Rsumf g n.
Proof.
  intros f g. induction n as [|n IH]; simpl; [lra|]. rewrite IH. lra.
Qed.

Lemma Rsumf_scale_r : forall c f n,
  Rsumf (fun i => f i * c) n = Rsumf f n * c.
Proof.
  intros c f. induction n as [|n IH]; simpl; [lra|]. rewrite IH. lra.
Qed.

(* |a . d| <= |a|_1 |d|_1.                                               *)
Lemma dot_bound : forall (a d : nat -> R) n,
  Rabs (Rsumf (fun i => a i * d i) n)
  <= Rsumf (fun i => Rabs (a i)) n * Rsumf (fun i => Rabs (d i)) n.
Proof.
  intros a d n.
  apply Rle_trans with (Rsumf (fun i => Rabs (a i * d i)) n);
    [apply Rsumf_abs|].
  rewrite <- Rsumf_scale_r. apply Rsumf_le. intros i Hi.
  rewrite Rabs_mult. apply Rmult_le_compat_l; [apply Rabs_pos|].
  apply (Rsumf_term_le (fun j => Rabs (d j)) n i); [|exact Hi].
  intros; apply Rabs_pos.
Qed.

(* Finitely many positive radii have a common one.                       *)
Lemma finite_delta : forall (m : nat) (P : nat -> R -> Prop),
  (forall j d d', P j d -> 0 < d' -> d' <= d -> P j d') ->
  (forall j, (j < m)%nat -> exists d, 0 < d /\ P j d) ->
  exists d, 0 < d /\ forall j, (j < m)%nat -> P j d.
Proof.
  intros m P Hmono. induction m as [|m IH]; intros H.
  - exists 1. split; [lra|]. intros j Hj. lia.
  - destruct IH as [d1 [Hd1 H1]]; [intros j Hj; apply H; lia|].
    destruct (H m ltac:(lia)) as [d2 [Hd2 H2]].
    exists (Rmin d1 d2). split; [apply Rmin_pos; assumption|].
    intros j Hj. destruct (Nat.eq_dec j m) as [->|Hne].
    + apply (Hmono m d2); [exact H2|apply Rmin_pos; assumption|apply Rmin_r].
    + apply (Hmono j d1); [apply H1; lia|apply Rmin_pos; assumption
                          |apply Rmin_l].
Qed.

Lemma Rabs_le_iff : forall u e, Rabs u <= e <-> - e <= u <= e.
Proof.
  intros u e. unfold Rabs. destruct (Rcase_abs u); split; intros; lra.
Qed.

(* ===================================================================== *)
(* Part B.  Differentiability AT A POINT, in two-point form.  A point of *)
(* R^n is a function nat -> R read on [0, n); only those coordinates     *)
(* move.  This is weaker than C^1 on a neighbourhood, so every theorem   *)
(* below is stronger than its C^1 reading.                               *)
(* ===================================================================== *)

Definition dist (n : nat) (x y : nat -> R) : R :=
  Rsumf (fun i => Rabs (y i - x i)) n.

Lemma dist_nonneg : forall n x y, 0 <= dist n x y.
Proof. intros. apply Rsumf_nonneg. intros; apply Rabs_pos. Qed.

Lemma dist_refl : forall n x, dist n x x = 0.
Proof.
  intros n x. apply Rsumf_zero. intros l _.
  replace (x l - x l) with 0 by ring. apply Rabs_R0.
Qed.

Lemma coord_le_dist : forall n x y i, (i < n)%nat ->
  Rabs (y i - x i) <= dist n x y.
Proof.
  intros n x y i Hi.
  apply (Rsumf_term_le (fun j => Rabs (y j - x j)) n i); [|exact Hi].
  intros; apply Rabs_pos.
Qed.

(* g is differentiable at x, in dimension n, with gradient a.            *)
Definition diff_at (n : nat) (g : (nat -> R) -> R) (x a : nat -> R) : Prop :=
  forall eps, 0 < eps -> exists delta, 0 < delta /\
    forall y, (forall i, (n <= i)%nat -> y i = x i) ->
      dist n x y < delta ->
      Rabs (g y - g x - Rsumf (fun i => a i * (y i - x i)) n)
      <= eps * dist n x y.

(* Agreement near the point is enough.                                   *)
Lemma diff_at_local : forall n g g' x a rho, 0 < rho ->
  (forall y, (forall i, (n <= i)%nat -> y i = x i) ->
     dist n x y < rho -> g y = g' y) ->
  diff_at n g' x a -> diff_at n g x a.
Proof.
  intros n g g' x a rho Hrho Heq Hd eps Heps.
  destruct (Hd eps Heps) as [delta [Hdelta Hb]].
  exists (Rmin delta rho). split; [apply Rmin_pos; assumption|].
  intros y Hy Hdist.
  assert (dist n x y < delta) by (pose proof (Rmin_l delta rho); lra).
  assert (dist n x y < rho) by (pose proof (Rmin_r delta rho); lra).
  rewrite (Heq y Hy) by assumption.
  rewrite (Heq x) by (try reflexivity; rewrite dist_refl; exact Hrho).
  apply Hb; assumption.
Qed.

(* The gradient is unique: move along one coordinate.                    *)
Theorem diff_at_unique : forall n g x a b,
  diff_at n g x a -> diff_at n g x b ->
  forall i, (i < n)%nat -> a i = b i.
Proof.
  intros n g x a b Ha Hb i Hi.
  destruct (Req_EM_T (a i) (b i)) as [E|Hne]; [exact E|]. exfalso.
  set (e := Rabs (a i - b i)).
  assert (He : 0 < e) by (apply Rabs_pos_lt; lra).
  destruct (Ha (e / 3) ltac:(lra)) as [d1 [Hd1 H1]].
  destruct (Hb (e / 3) ltac:(lra)) as [d2 [Hd2 H2]].
  set (t := Rmin d1 d2 / 2).
  assert (Ht : 0 < t)
    by (unfold t; pose proof (Rmin_pos d1 d2 Hd1 Hd2); lra).
  set (y := fun j => if Nat.eqb j i then x j + t else x j).
  assert (Hy : forall j, (n <= j)%nat -> y j = x j).
  { intros j Hj. unfold y. destruct (Nat.eqb_spec j i); [lia|reflexivity]. }
  assert (Hdist : dist n x y = t).
  { unfold dist. rewrite (Rsumf_single _ n i Hi).
    - unfold y. rewrite Nat.eqb_refl.
      replace (x i + t - x i) with t by ring. apply Rabs_right. lra.
    - intros j _ Hj. unfold y. destruct (Nat.eqb_spec j i); [contradiction|].
      replace (x j - x j) with 0 by ring. apply Rabs_R0. }
  assert (Hdot : forall c : nat -> R,
             Rsumf (fun j => c j * (y j - x j)) n = c i * t).
  { intros c. rewrite (Rsumf_single _ n i Hi).
    - unfold y. rewrite Nat.eqb_refl. ring.
    - intros j _ Hj. unfold y. destruct (Nat.eqb_spec j i); [contradiction|].
      ring. }
  assert (Hlt1 : dist n x y < d1).
  { rewrite Hdist. unfold t. pose proof (Rmin_l d1 d2). lra. }
  assert (Hlt2 : dist n x y < d2).
  { rewrite Hdist. unfold t. pose proof (Rmin_r d1 d2). lra. }
  specialize (H1 y Hy Hlt1). specialize (H2 y Hy Hlt2).
  rewrite Hdot, Hdist in H1, H2.
  apply Rabs_le_iff in H1. apply Rabs_le_iff in H2.
  assert (Hprod : e / 3 * t = e * t / 3) by field.
  rewrite Hprod in H1, H2.
  assert (Hdiff : Rabs ((a i - b i) * t) <= 2 * (e * t / 3)).
  { apply Rabs_le_iff. lra. }
  rewrite Rabs_mult in Hdiff. fold e in Hdiff.
  rewrite (Rabs_right t) in Hdiff by lra.
  assert (0 < e * t) by (apply Rmult_lt_0_compat; assumption). lra.
Qed.

(* ===================================================================== *)
(* Part C.  The chain rule at a point.  No mean value theorem: this is   *)
(* the epsilon-delta argument for Frechet derivatives.                   *)
(* ===================================================================== *)

(* A reconstruction reads m summary coordinates; the rest read zero.     *)
Definition trunc (m : nat) (z : nat -> R) : nat -> R :=
  fun j => if Nat.ltb j m then z j else 0.

Theorem chain_rule : forall N m (q : nat -> (nat -> R) -> R)
    (Dq : nat -> nat -> R) (Rr : (nat -> R) -> R) (a x : nat -> R),
  (forall j, (j < m)%nat -> diff_at N (q j) x (Dq j)) ->
  diff_at m Rr (trunc m (fun j => q j x)) a ->
  diff_at N (fun y => Rr (trunc m (fun j => q j y))) x
          (fun l => Rsumf (fun j => a j * Dq j l) m).
Proof.
  intros N m q Dq Rr a x Hq HR eps Heps.
  set (Aa := Rsumf (fun j => Rabs (a j)) m).
  assert (HAa : 0 <= Aa) by (apply Rsumf_nonneg; intros; apply Rabs_pos).
  set (e1 := eps / (2 * (Aa + 1))).
  assert (He1 : 0 < e1) by (unfold e1; apply Rdiv_lt_0_compat; lra).
  assert (He1e : e1 * (Aa + 1) = eps / 2) by (unfold e1; field; lra).
  set (L := Rsumf (fun j => Rsumf (fun l => Rabs (Dq j l)) N) m).
  assert (HL : 0 <= L).
  { apply Rsumf_nonneg. intros. apply Rsumf_nonneg. intros. apply Rabs_pos. }
  set (K := L + Rsumf (fun _ => e1) m).
  assert (HK : 0 <= K).
  { unfold K.
    assert (0 <= Rsumf (fun _ : nat => e1) m)
      by (apply Rsumf_nonneg; intros; lra). lra. }
  set (e2 := eps / (2 * (K + 1))).
  assert (He2 : 0 < e2) by (unfold e2; apply Rdiv_lt_0_compat; lra).
  assert (He2e : e2 * (K + 1) = eps / 2) by (unfold e2; field; lra).
  destruct (HR e2 He2) as [dR [HdR HRb]].
  destruct (finite_delta m
              (fun j d => forall y, (forall i, (N <= i)%nat -> y i = x i) ->
                 dist N x y < d ->
                 Rabs (q j y - q j x
                       - Rsumf (fun l => Dq j l * (y l - x l)) N)
                 <= e1 * dist N x y)) as [dq [Hdq Hqb]].
  { intros j d d' HP Hd' Hle y Hy Hlt. apply HP; [exact Hy|lra]. }
  { intros j Hj. destruct (Hq j Hj e1 He1) as [d [Hd Hb]].
    exists d. split; assumption. }
  set (d2 := dR / (K + 1)).
  assert (Hd2 : 0 < d2) by (unfold d2; apply Rdiv_lt_0_compat; lra).
  assert (Hd2e : d2 * (K + 1) = dR) by (unfold d2; field; lra).
  exists (Rmin dq d2). split; [apply Rmin_pos; assumption|].
  intros y Hy Hdist.
  pose proof (dist_nonneg N x y) as HD.
  assert (HDq : dist N x y < dq) by (pose proof (Rmin_l dq d2); lra).
  assert (HD2 : dist N x y < d2) by (pose proof (Rmin_r dq d2); lra).
  set (z := trunc m (fun j => q j x)) in *.
  set (zy := trunc m (fun j => q j y)).
  set (rem := fun j => q j y - q j x
                       - Rsumf (fun l => Dq j l * (y l - x l)) N).
  assert (Hrem : forall j, (j < m)%nat -> Rabs (rem j) <= e1 * dist N x y).
  { intros j Hj. apply (Hqb j Hj y Hy HDq). }
  assert (Hdq_j : forall j, (j < m)%nat ->
            Rabs (q j y - q j x)
            <= Rsumf (fun l => Rabs (Dq j l)) N * dist N x y
               + e1 * dist N x y).
  { intros j Hj.
    replace (q j y - q j x)
      with (rem j + Rsumf (fun l => Dq j l * (y l - x l)) N)
      by (unfold rem; ring).
    pose proof (Rabs_triang (rem j)
                  (Rsumf (fun l => Dq j l * (y l - x l)) N)) as Ht.
    pose proof (dot_bound (Dq j) (fun l => y l - x l) N) as Hdot.
    cbv beta in Hdot. fold (dist N x y) in Hdot.
    pose proof (Hrem j Hj). lra. }
  assert (Hzz : dist m z zy <= K * dist N x y).
  { unfold dist at 1.
    apply Rle_trans
      with (Rsumf (fun j => Rsumf (fun l => Rabs (Dq j l)) N * dist N x y
                            + e1 * dist N x y) m).
    - apply Rsumf_le. intros j Hj. unfold zy, z, trunc.
      destruct (Nat.ltb_spec j m); [|lia]. apply Hdq_j. exact Hj.
    - rewrite Rsumf_add, !Rsumf_scale_r. unfold K, L. lra. }
  assert (Hlt : dist m z zy < dR).
  { assert (dist N x y * (K + 1) < dR).
    { rewrite <- Hd2e. apply Rmult_lt_compat_r; lra. }
    lra. }
  assert (Hside : forall j, (m <= j)%nat -> zy j = z j).
  { intros j Hj. unfold zy, z, trunc.
    destruct (Nat.ltb_spec j m); [lia|reflexivity]. }
  pose proof (HRb zy Hside Hlt) as HR1.
  assert (Hdot_a : Rsumf (fun j => a j * (zy j - z j)) m
                   = Rsumf (fun j => a j * (q j y - q j x)) m).
  { apply Rsumf_ext. intros j Hj. unfold zy, z, trunc.
    destruct (Nat.ltb_spec j m); [reflexivity|lia]. }
  rewrite Hdot_a in HR1.
  assert (Hswap :
    Rsumf (fun l => Rsumf (fun j => a j * Dq j l) m * (y l - x l)) N
    = Rsumf (fun j => a j * Rsumf (fun l => Dq j l * (y l - x l)) N) m).
  { rewrite (Rsumf_ext _
               (fun l => Rsumf (fun j => a j * (Dq j l * (y l - x l))) m) N).
    - rewrite Rsumf_swap. apply Rsumf_ext. intros j _.
      apply (Rsumf_scale (a j) (fun l => Dq j l * (y l - x l)) N).
    - intros l _. rewrite <- Rsumf_scale_r. apply Rsumf_ext.
      intros j _. ring. }
  rewrite Hswap.
  set (S1 := Rr zy - Rr z - Rsumf (fun j => a j * (q j y - q j x)) m) in *.
  set (S2 := Rsumf (fun j => a j * rem j) m).
  assert (Hsplit :
    Rr zy - Rr z
    - Rsumf (fun j => a j * Rsumf (fun l => Dq j l * (y l - x l)) N) m
    = S1 + S2).
  { unfold S1, S2.
    rewrite (Rsumf_ext (fun j => a j * rem j)
               (fun j => a j * (q j y - q j x)
                         - a j * Rsumf (fun l => Dq j l * (y l - x l)) N) m)
      by (intros; unfold rem; ring).
    rewrite Rsumf_minus. ring. }
  rewrite Hsplit.
  assert (HS2 : Rabs S2 <= Aa * (e1 * dist N x y)).
  { unfold S2.
    apply Rle_trans with (Rsumf (fun j => Rabs (a j * rem j)) m);
      [apply Rsumf_abs|].
    unfold Aa. rewrite <- Rsumf_scale_r. apply Rsumf_le. intros j Hj.
    rewrite Rabs_mult. apply Rmult_le_compat_l; [apply Rabs_pos|].
    apply Hrem. exact Hj. }
  assert (HS1 : Rabs S1 <= e2 * (K * dist N x y)).
  { apply Rle_trans with (e2 * dist m z zy); [exact HR1|].
    apply Rmult_le_compat_l; lra. }
  pose proof (Rabs_triang S1 S2) as Htri.
  assert (Hb1 : e2 * (K * dist N x y) <= eps / 2 * dist N x y).
  { replace (e2 * (K * dist N x y)) with (e2 * K * dist N x y) by ring.
    apply Rmult_le_compat_r; lra. }
  assert (Hb2 : Aa * (e1 * dist N x y) <= eps / 2 * dist N x y).
  { replace (Aa * (e1 * dist N x y)) with (Aa * e1 * dist N x y) by ring.
    apply Rmult_le_compat_r; lra. }
  lra.
Qed.

(* ===================================================================== *)
(* Part D.  P6 as the draft states it, in a STRONGER form: the summary   *)
(* and the reconstructions need only be differentiable AT THE POINT, and *)
(* the factorization need only hold near it.                             *)
(* ===================================================================== *)

Theorem smooth_rank_bound : forall N m s
    (q : nat -> (nat -> R) -> R) (Dq : nat -> nat -> R)
    (Rr : nat -> (nat -> R) -> R) (a : nat -> nat -> R)
    (O : nat -> (nat -> R) -> R) (G : nat -> nat -> R)
    (x : nat -> R) (rho : R),
  (m < s)%nat -> 0 < rho ->
  (forall j, (j < m)%nat -> diff_at N (q j) x (Dq j)) ->
  (forall i, (i < s)%nat ->
     diff_at m (Rr i) (trunc m (fun j => q j x)) (a i)) ->
  (forall i, (i < s)%nat -> diff_at N (O i) x (G i)) ->
  (forall i y, (i < s)%nat -> (forall l, (N <= l)%nat -> y l = x l) ->
     dist N x y < rho -> O i y = Rr i (trunc m (fun j => q j y))) ->
  exists lam : nat -> R,
    (exists i, (i < s)%nat /\ lam i <> 0) /\
    forall l, (l < N)%nat -> Rsumf (fun i => lam i * G i l) s = 0.
Proof.
  intros N m s q Dq Rr a O G x rho Hms Hrho Hq HR HO Hfac.
  apply (factor_rows_dependent R 0 1 Rplus Rmult Rminus Ropp RTheory
           R1_neq_R0 Rmult_integral Req_EM_T m s N a Dq G Hms).
  intros i l Hi Hl.
  apply (diff_at_unique N (O i) x (G i)
           (fun l0 => Rsumf (fun j => a i j * Dq j l0) m)); [apply HO; exact Hi
                                                           | |exact Hl].
  apply (diff_at_local N (O i) (fun y => Rr i (trunc m (fun j => q j y)))
           x _ rho Hrho).
  - intros y Hy Hd. apply Hfac; assumption.
  - apply chain_rule; [exact Hq|apply HR; exact Hi].
Qed.

(* ===================================================================== *)
(* Part E.  Power sums of real arrays are differentiable everywhere,     *)
(* with the expected gradient.  The one import from one-variable         *)
(* calculus is the derivative of t -> t^n.                               *)
(* ===================================================================== *)

Definition Ppow (k N : nat) (y : nat -> R) : R :=
  Rsumf (fun i => (y i) ^ k) N.

Lemma pow_two_point : forall x k eps, 0 < eps ->
  exists d, 0 < d /\ forall t, Rabs (t - x) < d ->
    Rabs (t ^ S k - x ^ S k - INR (S k) * x ^ k * (t - x))
    <= eps * Rabs (t - x).
Proof.
  intros x k eps Heps.
  destruct (derivable_pt_lim_pow x (S k) eps Heps) as [delta Hd].
  exists delta. split; [apply cond_pos|]. intros t Ht.
  destruct (Req_EM_T (t - x) 0) as [E|Hne].
  - replace t with x by lra.
    replace (x ^ S k - x ^ S k - INR (S k) * x ^ k * (x - x)) with 0 by ring.
    rewrite Rabs_R0. pose proof (Rabs_pos (x - x)).
    apply Rmult_le_pos; lra.
  - specialize (Hd (t - x) Hne Ht).
    replace (x + (t - x)) with t in Hd by ring. simpl pred in Hd.
    replace (t ^ S k - x ^ S k - INR (S k) * x ^ k * (t - x))
      with ((t - x) * ((t ^ S k - x ^ S k) / (t - x) - INR (S k) * x ^ k))
      by (field; exact Hne).
    rewrite Rabs_mult, Rmult_comm.
    apply Rmult_le_compat_r; [apply Rabs_pos|lra].
Qed.

Theorem Ppow_diff : forall k N x,
  diff_at N (Ppow (S k) N) x (fun l => INR (S k) * (x l) ^ k).
Proof.
  intros k N x eps Heps.
  destruct (finite_delta N
              (fun i d => forall t, Rabs (t - x i) < d ->
                 Rabs (t ^ S k - (x i) ^ S k
                       - INR (S k) * (x i) ^ k * (t - x i))
                 <= eps * Rabs (t - x i))) as [d [Hd Hb]].
  { intros i d0 d' HP Hd' Hle t Ht. apply HP. lra. }
  { intros i _. apply pow_two_point. exact Heps. }
  exists d. split; [exact Hd|]. intros y Hy Hdist.
  unfold Ppow.
  rewrite <- !Rsumf_minus.
  apply Rle_trans
    with (Rsumf (fun i => Rabs ((y i) ^ S k - (x i) ^ S k
                                - INR (S k) * (x i) ^ k * (y i - x i))) N);
    [apply Rsumf_abs|].
  unfold dist. rewrite <- Rsumf_scale. apply Rsumf_le. intros i Hi.
  apply (Hb i Hi). pose proof (coord_le_dist N x y i Hi). lra.
Qed.

Corollary Ppow_diff' : forall K N x, K <> O ->
  diff_at N (Ppow K N) x (fun l => INR K * (x l) ^ (K - 1)).
Proof.
  intros K N x HK. destruct K as [|K]; [contradiction|].
  replace (S K - 1)%nat with K by lia. apply Ppow_diff.
Qed.

(* ===================================================================== *)
(* Part F.  The ranks, carried from Z.  BladeDescartes computes them     *)
(* exactly over the integers; BladeRankDomain's transfer along           *)
(* IZR : Z -> R brings them here.                                        *)
(* ===================================================================== *)

Lemma IZR_zpow : forall n k, IZR (zpow n k) = (IZR n) ^ k.
Proof.
  intros n. induction k as [|k IH]; [reflexivity|].
  rewrite zpow_succ, mult_IZR, IH. reflexivity.
Qed.

Definition real_left_kernel_transfer :=
  left_kernel_transfer R 0 1 Rplus Rmult Rminus Ropp RTheory
    Rmult_integral IZR eq_refl plus_IZR mult_IZR eq_IZR_R0.

(* P4a over the reals.  The first r power sums of N >= r real numbers    *)
(* are not carried by fewer than r summary coordinates, for ANY summary  *)
(* and reconstructions differentiable at the point (0, 1, 2, ...).       *)
Theorem moment_summary_needs_r_smooth_coordinates : forall r N m,
  (r <= N)%nat -> (m < r)%nat ->
  forall (q : nat -> (nat -> R) -> R) (Dq : nat -> nat -> R)
         (Rr : nat -> (nat -> R) -> R) (a : nat -> nat -> R) (rho : R),
  let x := fun i => INR i in
  0 < rho ->
  (forall j, (j < m)%nat -> diff_at N (q j) x (Dq j)) ->
  (forall k, (k < r)%nat ->
     diff_at m (Rr k) (trunc m (fun j => q j x)) (a k)) ->
  ~ (forall k y, (k < r)%nat -> (forall l, (N <= l)%nat -> y l = x l) ->
       dist N x y < rho ->
       Ppow (S k) N y = Rr k (trunc m (fun j => q j y))).
Proof.
  intros r N m HrN Hmr q Dq Rr a rho x Hrho Hq HR Hfac.
  destruct (smooth_rank_bound N m r q Dq Rr a (fun k => Ppow (S k) N)
              (fun k l => INR (S k) * (x l) ^ k) x rho Hmr Hrho Hq HR)
    as [lam [[k0 [Hk0 Hnz]] Hdep]].
  - intros k _. apply Ppow_diff.
  - exact Hfac.
  - apply Hnz.
    apply (real_left_kernel_transfer r
             (fun k l => (Z.of_nat (S k) * zpow (Z.of_nat l) k)%Z));
      [| |exact Hk0].
    + intros lamZ HZ. intros k Hk.
      assert (Hc : (lamZ k * Z.of_nat (S k))%Z = 0%Z).
      { apply (many_roots_fn r (fun j => (lamZ j * Z.of_nat (S j))%Z)
                 (fun t => Z.of_nat t)); [| |exact Hk].
        - intros t t' _ _ E. lia.
        - intros t Ht. etransitivity; [|exact (HZ t Ht)].
          apply zsumf_ext. intros j _. ring. }
      apply Z.mul_eq_0 in Hc. destruct Hc; lia.
    + intros l Hl. etransitivity; [|exact (Hdep l ltac:(lia))].
      apply Rsumf_ext. intros k _.
      rewrite mult_IZR, IZR_zpow, <- !INR_IZR_INZ. reflexivity.
Qed.

(* P7 over the reals.  The sums observed over Hor steps of squaring N    *)
(* real numbers are not carried by fewer than min(N, Hor) summary        *)
(* coordinates, for ANY summary and reconstructions differentiable at    *)
(* the point (1, 2, 3, ...).  No bounded differentiable summary serves   *)
(* every horizon as N grows.                                             *)
Theorem squaring_needs_min_N_H_smooth_coordinates : forall N Hor m,
  (m < Nat.min N Hor)%nat ->
  forall (q : nat -> (nat -> R) -> R) (Dq : nat -> nat -> R)
         (Rr : nat -> (nat -> R) -> R) (a : nat -> nat -> R) (rho : R),
  let x := fun i => INR (S i) in
  0 < rho ->
  (forall j, (j < m)%nat -> diff_at N (q j) x (Dq j)) ->
  (forall t, (t < Hor)%nat ->
     diff_at m (Rr t) (trunc m (fun j => q j x)) (a t)) ->
  ~ (forall t y, (t < Hor)%nat -> (forall l, (N <= l)%nat -> y l = x l) ->
       dist N x y < rho ->
       Ppow (2 ^ t) N y = Rr t (trunc m (fun j => q j y))).
Proof.
  intros N Hor m Hm q Dq Rr a rho x Hrho Hq HR Hfac.
  set (s := Nat.min N Hor) in *.
  assert (Hpow : forall k, (2 ^ k)%nat <> O)
    by (intro k; apply Nat.pow_nonzero; lia).
  destruct (smooth_rank_bound N m s q Dq Rr a (fun t => Ppow (2 ^ t) N)
              (fun t l => INR (2 ^ t) * (x l) ^ (2 ^ t - 1)) x rho Hm Hrho Hq)
    as [lam [[t0 [Ht0 Hnz]] Hdep]].
  - intros t Ht. apply HR. unfold s in Ht. lia.
  - intros t _. apply Ppow_diff'. apply Hpow.
  - intros t y Ht. apply Hfac. unfold s in Ht. lia.
  - apply Hnz.
    apply (real_left_kernel_transfer s
             (fun t l => (Z.of_nat (2 ^ t)
                          * zpow (Z.of_nat (S l)) (2 ^ t - 1))%Z));
      [| |exact Ht0].
    + intros lamZ HZ. intros t Ht.
      assert (Hc : (lamZ t * Z.of_nat (2 ^ t))%Z = 0%Z).
      { apply (generalized_vandermonde s
                 (fun k => (lamZ k * Z.of_nat (2 ^ k))%Z)
                 (fun k => (2 ^ k - 1)%nat)
                 (fun l => Z.of_nat (S l))); [| | | |exact Ht].
        - intros k k' _ _ Ek. pose proof (Hpow k). pose proof (Hpow k').
          apply (Nat.pow_inj_r 2); lia.
        - intros l _. lia.
        - intros l l' _ _ E. lia.
        - intros l Hl. etransitivity; [|exact (HZ l Hl)].
          apply zsumf_ext. intros k _. ring. }
      apply Z.mul_eq_0 in Hc. destruct Hc as [Hc|Hc]; [exact Hc|].
      pose proof (Hpow t). lia.
    + intros l Hl. etransitivity; [|apply (Hdep l); unfold s in Hl; lia].
      apply Rsumf_ext. intros t _.
      rewrite mult_IZR, IZR_zpow, <- !INR_IZR_INZ. reflexivity.
Qed.

(* ===================================================================== *)
(* Part G.  P8 over the reals, against EVERY update function.  No        *)
(* analysis: this is BladeProuhet's axiom-free theorem read at K = R,    *)
(* and it is here only because R itself is axiomatic.                    *)
(* ===================================================================== *)

Lemma ofnat_R : forall n, ofnat R 0 1 Rplus n = INR n.
Proof.
  induction n as [|n IH]; [reflexivity|].
  cbn [ofnat]. rewrite IH, S_INR. lra.
Qed.

(* The draft's P8, negative half, for the pure power x -> x^d on real    *)
(* arrays: no function G of the first r power sums, continuous or not,   *)
(* from extent 2^(dr-1) on.                                              *)
Theorem power_never_closes_R : forall d r N,
  (2 <= d)%nat -> (1 <= r)%nat -> (2 ^ (d * r - 1) <= N)%nat ->
  forall G : unit -> list R -> list R,
    ~ (forall u y, length y = N ->
         qr R 0 1 Rplus Rmult r (map (fun t => rpow R 1 Rmult t d) y)
         = G u (qr R 0 1 Rplus Rmult r y)).
Proof.
  apply (power_never_closes R 0 1 Rplus Rmult Rminus Ropp RTheory
           Rmult_integral R1_neq_R0).
  intros n. rewrite ofnat_R. apply not_0_INR. discriminate.
Qed.
