(* ===================================================================== *)
(* reals/BladeRealCollision.v -- EXACT RECURRENCE REDUCTION, the draft's *)
(* P8 (negative half) AT ITS OWN THRESHOLD N >= d r, over the real       *)
(* numbers (docs/research/exact-recurrence-reduction-proofs.md, section  *)
(* 9).                                                                   *)
(*                                                                       *)
(*       THIS FILE IS NOT PART OF THE AXIOM-FREE TOWER.                  *)
(*                                                                       *)
(* It uses Coq's standard-library real numbers.  Print Assumptions on    *)
(* its theorems reports three axioms -- one more than BladeSmoothRank,   *)
(* because the intermediate value theorem is used:                       *)
(*                                                                       *)
(*   ClassicalDedekindReals.sig_forall_dec                               *)
(*   ClassicalDedekindReals.sig_not_dec                                  *)
(*   FunctionalExtensionality.functional_extensionality_dep              *)
(*                                                                       *)
(* Own directory, own _CoqProject, excluded from count-theorems.ps1 and  *)
(* from the headline count.                                              *)
(*                                                                       *)
(* The draft moves p_(dr) while p_1 .. p_(dr-1) stand still by the       *)
(* inverse function theorem in several variables, which Coq's standard   *)
(* library does not have.  ONE-variable calculus is enough:              *)
(*                                                                       *)
(*   EN_sign_change            E(Y) = prod (1 - Y / rt_i), for any       *)
(*                             increasing positive roots rt, changes     *)
(*                             sign between consecutive midpoints;       *)
(*   perturbed_roots           bump the coefficient of Y^m by a small t: *)
(*                             every one of those signs survives         *)
(*                             (sign_stable), so the INTERMEDIATE VALUE  *)
(*                             THEOREM returns N real roots, one in each *)
(*                             interval;                                 *)
(*   ideal_partner             the bumped polynomial has N distinct real *)
(*                             roots and the value 1 at 0, so it IS      *)
(*                             prod (1 - Y / rho_j)                      *)
(*                             (BladeNewton.coefficients_from_values),   *)
(*                             and Newton's identities read off the      *)
(*                             power sums of the reciprocals: EVERY      *)
(*                             array of distinct positive reals has a    *)
(*                             partner of the same extent agreeing on    *)
(*                             p_0 .. p_(m-1) and differing on p_m, for  *)
(*                             every m <= N;                             *)
(*                                                                       *)
(*   closure_refused_R_at_point  P8 as section 9 argues it: at ANY such  *)
(*                             array where the leading coefficient a_d   *)
(*                             is nonzero, no function G of the first r  *)
(*                             power sums -- continuous or not -- closes *)
(*                             the update, for N >= d r.  The a_j are    *)
(*                             arbitrary functions of the summary;       *)
(*   power_never_closes_R_at_threshold  x -> x^d, unconditionally;       *)
(*   closure_refused_R_at_threshold  any a_d that vanishes nowhere.      *)
(*                                                                       *)
(* Scope, stated once.  The draft's existence claim for coefficient      *)
(* POLYNOMIALS -- that "a_d not identically zero" yields an array of     *)
(* distinct positive reals where a_d(q_r(x)) <> 0 -- is not here; it is  *)
(* BladeRealDensity.v.  Arrays are listed in decreasing order (lstN); a  *)
(* collision at one ordering refutes G on all arrays of that extent.     *)
(* Exact real arithmetic.                                                *)
(*                                                                       *)
(* Imports the Blade tower (BladeNewton, BladeProuhet and below),        *)
(* BladeSmoothRank, and Coq's Reals.                                     *)
(* ===================================================================== *)

From Blade Require Import BladeBinomial BladeSummary BladeMomentClosure
  BladeRankBound BladeDescartes BladeRankDomain BladeProuhet BladeNewton.
From BladeReals Require Import BladeSmoothRank.
Require Import Reals Lra Psatz List Arith Lia.
Import ListNotations.

Open Scope R_scope.

Local Notation Rpeval := (peval R 0 Rplus Rmult).
Local Notation Relist := (elist R 0 1 Rplus Rmult Ropp).
Local Notation Rbump := (bump R 0 Rplus).
Local Notation Rrpow := (rpow R 1 Rmult).
Local Notation Rpsum := (psum R 0 1 Rplus Rmult).

(* ===================================================================== *)
(* Part A.  Polynomial functions are continuous; three finite-choice     *)
(* facts.                                                                *)
(* ===================================================================== *)

Lemma peval_continuous : forall l, continuity (fun Y => Rpeval l Y).
Proof.
  induction l as [|c l IH]; intros Y.
  - apply continuity_pt_const. intros a b. reflexivity.
  - change (continuity_pt
              ((fun _ => c) + (id * (fun y => Rpeval l y)))%F Y).
    apply continuity_pt_plus.
    + apply continuity_pt_const. intros a b. reflexivity.
    + apply continuity_pt_mult.
      * apply derivable_continuous_pt. apply derivable_pt_id.
      * apply IH.
Qed.

Lemma finite_choice : forall (n : nat) (P : nat -> R -> Prop),
  (forall j, (j < n)%nat -> exists z, P j z) ->
  exists f : nat -> R, forall j, (j < n)%nat -> P j (f j).
Proof.
  induction n as [|n IH]; intros P H.
  - exists (fun _ => 0). intros j Hj. lia.
  - destruct (IH P) as [f Hf]; [intros j Hj; apply H; lia|].
    destruct (H n ltac:(lia)) as [z Hz].
    exists (fun j => if Nat.eqb j n then z else f j). intros j Hj.
    destruct (Nat.eqb_spec j n) as [->|Hne]; [exact Hz|apply Hf; lia].
Qed.

Lemma finite_lower : forall (n : nat) (f : nat -> R),
  (forall j, (j < n)%nat -> 0 < f j) ->
  exists b, 0 < b /\ forall j, (j < n)%nat -> b <= f j.
Proof.
  induction n as [|n IH]; intros f H.
  - exists 1. split; [lra|]. intros j Hj. lia.
  - destruct (IH f) as [b [Hb Hle]]; [intros j Hj; apply H; lia|].
    exists (Rmin b (f n)). split.
    + apply Rmin_pos; [exact Hb|apply H; lia].
    + intros j Hj. destruct (Nat.eq_dec j n) as [->|Hne].
      * apply Rmin_r.
      * apply Rle_trans with b; [apply Rmin_l|apply Hle; lia].
Qed.

Lemma finite_upper : forall (n : nat) (f : nat -> R),
  exists M, 0 <= M /\ forall j, (j < n)%nat -> f j <= M.
Proof.
  induction n as [|n IH]; intros f.
  - exists 0. split; [lra|]. intros j Hj. lia.
  - destruct (IH f) as [M [HM Hle]].
    exists (Rmax M (f n)). split.
    + apply Rle_trans with M; [exact HM|apply Rmax_l].
    + intros j Hj. destruct (Nat.eq_dec j n) as [->|Hne].
      * apply Rmax_r.
      * apply Rle_trans with M; [apply Hle; lia|apply Rmax_l].
Qed.

(* ===================================================================== *)
(* Part B.  The base polynomial  E_N(Y) = prod_(i<N) (1 - Y / rt i)  for *)
(* ANY increasing sequence rt of positive roots, and its signs at the    *)
(* midpoints between consecutive roots.                                  *)
(* ===================================================================== *)

Fixpoint lstN (f : nat -> R) (N : nat) : list R :=
  match N with O => [] | S n => f n :: lstN f n end.

Lemma lstN_length : forall f N, length (lstN f N) = N.
Proof.
  intros f. induction N as [|N IH]; simpl; [reflexivity|].
  rewrite IH. reflexivity.
Qed.

Lemma In_lstN : forall f N j, (j < N)%nat -> In (f j) (lstN f N).
Proof.
  intros f. induction N as [|N IH]; intros j Hj; [lia|]. simpl.
  destruct (Nat.eq_dec j N) as [->|Hne]; [left; reflexivity|].
  right. apply IH. lia.
Qed.

Lemma lstN_In : forall f N z, In z (lstN f N) ->
  exists j, (j < N)%nat /\ z = f j.
Proof.
  intros f. induction N as [|N IH]; intros z Hz; [contradiction|].
  simpl in Hz. destruct Hz as [<-|Hz].
  - exists N. split; [lia|reflexivity].
  - destruct (IH z Hz) as [j [Hj E]]. exists j. split; [lia|exact E].
Qed.

Lemma NoDup_lstN : forall f N,
  (forall i j, (i < j)%nat -> (j < N)%nat -> f i < f j) ->
  NoDup (lstN f N).
Proof.
  intros f. induction N as [|N IH]; intros Hmono; simpl; [constructor|].
  constructor.
  - intro Hin. destruct (lstN_In f N (f N) Hin) as [j [Hj E]].
    pose proof (Hmono j N Hj ltac:(lia)). lra.
  - apply IH. intros i j Hij Hj. apply Hmono; lia.
Qed.

Section Roots.
Variable rt : nat -> R.
Hypothesis rt_pos : forall i, 0 < rt i.
Hypothesis rt_incr : forall i, rt i < rt (S i).

Lemma rt_mono : forall a b, (a <= b)%nat -> rt a <= rt b.
Proof.
  intros a b H. induction H as [|b H IH]; [lra|].
  pose proof (rt_incr b). lra.
Qed.

Definition xN (i : nat) : R := / rt i.

Definition EN (N : nat) (Y : R) : R := Rpeval (Relist (lstN xN N)) Y.

Lemma EN_0 : forall Y, EN 0 Y = 1.
Proof. intros Y. unfold EN. simpl. ring. Qed.

Lemma EN_S : forall n Y, EN (S n) Y = EN n Y * (1 - xN n * Y).
Proof.
  intros n Y. unfold EN. cbn [lstN].
  apply (peval_elist_cons R 0 1 Rplus Rmult Rminus Ropp RTheory).
Qed.

(* Test points: below the first root, then between consecutive roots.    *)
Definition tau (j : nat) : R :=
  match j with O => rt 0 / 2 | S j' => (rt j' + rt (S j')) / 2 end.

Lemma tau_below : forall j, tau j < rt j.
Proof.
  intros [|j]; simpl; [pose proof (rt_pos 0)|pose proof (rt_incr j)]; lra.
Qed.

Lemma tau_above : forall j, rt j < tau (S j).
Proof. intros j. simpl. pose proof (rt_incr j). lra. Qed.

Lemma tau_lt : forall i j, (j <= i)%nat -> tau j < rt i.
Proof.
  intros i j H. pose proof (tau_below j). pose proof (rt_mono j i H). lra.
Qed.

Lemma tau_gt : forall i j, (S i <= j)%nat -> rt i < tau j.
Proof.
  intros i j H. destruct j as [|j]; [lia|].
  pose proof (tau_above j). pose proof (rt_mono i j ltac:(lia)). lra.
Qed.

Lemma tau_le : forall a b, (a <= b)%nat -> tau a <= tau b.
Proof.
  intros a b H. induction H as [|b H IH]; [lra|].
  pose proof (tau_below b). pose proof (tau_above b). lra.
Qed.

Lemma tau_pos : forall j, 0 < tau j.
Proof.
  intros j. pose proof (tau_le 0 j ltac:(lia)). pose proof (rt_pos 0).
  simpl in H. lra.
Qed.

Lemma factor_pos : forall i t, t < rt i -> 0 < 1 - xN i * t.
Proof.
  intros i t H. unfold xN. pose proof (rt_pos i) as Hp.
  replace (1 - / rt i * t) with ((rt i - t) * / rt i) by (field; lra).
  apply Rmult_lt_0_compat; [lra|apply Rinv_0_lt_compat; exact Hp].
Qed.

Lemma factor_neg : forall i t, rt i < t -> 1 - xN i * t < 0.
Proof.
  intros i t H. unfold xN. pose proof (rt_pos i) as Hp.
  replace (1 - / rt i * t) with (- ((t - rt i) * / rt i)) by (field; lra).
  assert (0 < (t - rt i) * / rt i)
    by (apply Rmult_lt_0_compat; [lra|apply Rinv_0_lt_compat; exact Hp]).
  lra.
Qed.

(* Beyond every root so far, all factors are negative together.          *)
Lemma EN_same_side : forall n a b,
  (forall i, (i < n)%nat -> rt i < a) -> (forall i, (i < n)%nat -> rt i < b) ->
  0 < EN n a * EN n b.
Proof.
  induction n as [|n IH]; intros a b Ha Hb.
  - rewrite !EN_0. lra.
  - rewrite !EN_S.
    assert (H1 : 0 < EN n a * EN n b)
      by (apply IH; intros i Hi; [apply Ha|apply Hb]; lia).
    assert (Hfa : 1 - xN n * a < 0) by (apply factor_neg, Ha; lia).
    assert (Hfb : 1 - xN n * b < 0) by (apply factor_neg, Hb; lia).
    assert (H2 : 0 < (1 - xN n * a) * (1 - xN n * b)) by nra.
    replace (EN n a * (1 - xN n * a) * (EN n b * (1 - xN n * b)))
      with ((EN n a * EN n b) * ((1 - xN n * a) * (1 - xN n * b))) by ring.
    apply Rmult_lt_0_compat; assumption.
Qed.

Lemma EN_sign_change : forall N j, (j < N)%nat ->
  EN N (tau j) * EN N (tau (S j)) < 0.
Proof.
  induction N as [|n IH]; intros j Hj; [lia|].
  rewrite !EN_S.
  replace (EN n (tau j) * (1 - xN n * tau j)
           * (EN n (tau (S j)) * (1 - xN n * tau (S j))))
    with ((EN n (tau j) * EN n (tau (S j)))
          * ((1 - xN n * tau j) * (1 - xN n * tau (S j)))) by ring.
  destruct (Nat.eq_dec j n) as [->|Hne].
  - assert (H1 : 0 < EN n (tau n) * EN n (tau (S n))).
    { apply EN_same_side; intros i Hi; apply tau_gt; lia. }
    assert (Hf1 : 0 < 1 - xN n * tau n) by (apply factor_pos, tau_lt; lia).
    assert (Hf2 : 1 - xN n * tau (S n) < 0) by (apply factor_neg, tau_gt; lia).
    assert (H2 : (1 - xN n * tau n) * (1 - xN n * tau (S n)) < 0) by nra.
    nra.
  - assert (H1 : EN n (tau j) * EN n (tau (S j)) < 0) by (apply IH; lia).
    assert (Hf1 : 0 < 1 - xN n * tau j) by (apply factor_pos, tau_lt; lia).
    assert (Hf2 : 0 < 1 - xN n * tau (S j)) by (apply factor_pos, tau_lt; lia).
    assert (H2 : 0 < (1 - xN n * tau j) * (1 - xN n * tau (S j)))
      by (apply Rmult_lt_0_compat; assumption).
    nra.
Qed.

Lemma EN_tau_nonzero : forall N j, (1 <= N)%nat -> (j <= N)%nat ->
  EN N (tau j) <> 0.
Proof.
  intros N j HN Hj E.
  destruct (Nat.eq_dec j N) as [->|Hne].
  - destruct N as [|n]; [lia|].
    pose proof (EN_sign_change (S n) n ltac:(lia)) as H. rewrite E in H. lra.
  - pose proof (EN_sign_change N j ltac:(lia)) as H. rewrite E in H. lra.
Qed.

(* A perturbation smaller than half the size of two values of opposite   *)
(* sign leaves them of opposite sign.                                    *)
Lemma sign_stable : forall A B pa pb beta,
  0 < beta -> beta <= Rabs A -> beta <= Rabs B ->
  Rabs pa <= beta / 2 -> Rabs pb <= beta / 2 ->
  A * B < 0 -> (A + pa) * (B + pb) < 0.
Proof.
  intros A B pa pb beta Hb HA HB Hpa Hpb HAB.
  apply Rabs_le_iff in Hpa. apply Rabs_le_iff in Hpb.
  unfold Rabs in HA, HB.
  destruct (Rcase_abs A), (Rcase_abs B); nra.
Qed.

(* ===================================================================== *)
(* Part C.  Bump the coefficient of Y^m by a small t.  Every sign at the *)
(* half-integers survives, so the intermediate value theorem returns N   *)
(* real roots, one in each interval.                                     *)
(* ===================================================================== *)

Lemma perturbed_roots : forall N m, (1 <= m)%nat -> (m <= N)%nat ->
  exists (t : R) (rho : nat -> R), 0 < t /\
    (forall j, (j < N)%nat -> tau j < rho j < tau (S j)) /\
    (forall j, (j < N)%nat ->
       Rpeval (Rbump m t (Relist (lstN xN N))) (rho j) = 0).
Proof.
  intros N m Hm1 HmN. assert (HN : (1 <= N)%nat) by lia.
  destruct (finite_lower (S N) (fun j => Rabs (EN N (tau j))))
    as [beta [Hbeta Hlow]].
  { intros j Hj. apply Rabs_pos_lt. apply EN_tau_nonzero; lia. }
  destruct (finite_upper (S N) (fun j => Rabs (Rrpow (tau j) m)))
    as [M [HM Hup]].
  set (t := beta / (2 * (M + 1))).
  assert (Ht : 0 < t) by (unfold t; apply Rdiv_lt_0_compat; lra).
  assert (Hte : t * (M + 1) = beta / 2) by (unfold t; field; lra).
  exists t.
  set (Et := fun Y => Rpeval (Rbump m t (Relist (lstN xN N))) Y).
  assert (HEt : forall Y, Et Y = EN N Y + t * Rrpow Y m).
  { intro Y. unfold Et, EN.
    apply (peval_bump R 0 1 Rplus Rmult Rminus Ropp RTheory). }
  assert (Hpert : forall j, (j <= N)%nat ->
            Rabs (t * Rrpow (tau j) m) <= beta / 2).
  { intros j Hj. rewrite Rabs_mult, (Rabs_right t) by lra.
    pose proof (Hup j ltac:(lia)) as H1. cbv beta in H1.
    assert (t * Rabs (Rrpow (tau j) m) <= t * M)
      by (apply Rmult_le_compat_l; lra).
    lra. }
  assert (Hsign : forall j, (j < N)%nat -> Et (tau j) * Et (tau (S j)) < 0).
  { intros j Hj. rewrite !HEt. apply (sign_stable _ _ _ _ beta Hbeta).
    - apply (Hlow j). lia.
    - apply (Hlow (S j)). lia.
    - apply Hpert. lia.
    - apply Hpert. lia.
    - apply EN_sign_change. exact Hj. }
  destruct (finite_choice N
              (fun j z => tau j < z < tau (S j) /\ Et z = 0)) as [rho Hrho].
  { intros j Hj. pose proof (Hsign j Hj) as Hs.
    destruct (IVT_cor Et (tau j) (tau (S j))) as [z [[Hz1 Hz2] Hz0]].
    - unfold Et. apply peval_continuous.
    - apply tau_le. lia.
    - lra.
    - exists z. split; [|exact Hz0]. split.
      + destruct Hz1 as [Hlt|Heq]; [exact Hlt|]. exfalso.
        rewrite Heq, Hz0 in Hs. lra.
      + destruct Hz2 as [Hlt|Heq]; [exact Hlt|]. exfalso.
        rewrite <- Heq, Hz0 in Hs. lra. }
  exists rho. split; [exact Ht|].
  split; intros j Hj; destruct (Hrho j Hj) as [H1 H2]; assumption.
Qed.

(* ===================================================================== *)
(* Part D.  IDEAL PAIRS OVER R, at every extent.  The perturbed          *)
(* polynomial has N distinct real roots and the value 1 at 0, so it IS   *)
(* prod (1 - Y / rho_j) (BladeNewton.coefficients_from_values); Newton's *)
(* identities then read off the power sums of the reciprocals.           *)
(* ===================================================================== *)

Lemma R_char0 : forall n, ofnat R 0 1 Rplus (S n) <> 0.
Proof. intros n. rewrite ofnat_R. apply not_0_INR. discriminate. Qed.

(* EVERY array of distinct positive reals, listed in decreasing order,   *)
(* has a partner of the same extent that agrees with it on               *)
(* p_0 .. p_(m-1) and differs on p_m.                                    *)
Theorem ideal_partner : forall m N, (1 <= m)%nat -> (m <= N)%nat ->
  exists ys : list R, length ys = N /\
    (forall k, (k < m)%nat -> Rpsum k (lstN xN N) = Rpsum k ys) /\
    Rpsum m (lstN xN N) <> Rpsum m ys.
Proof.
  intros m N Hm1 HmN.
  destruct (perturbed_roots N m Hm1 HmN) as [t [rho [Ht [Hint Hroot]]]].
  set (xs := lstN xN N). set (ys := lstN (fun j => / rho j) N).
  exists ys. split; [apply lstN_length|].
  assert (Hpos : forall j, (j < N)%nat -> 0 < rho j).
  { intros j Hj. destruct (Hint j Hj) as [H1 _].
    pose proof (tau_pos j). lra. }
  apply (ideal_pair_from_bump R 0 1 Rplus Rmult Rminus Ropp RTheory
           Rmult_integral R_char0 xs ys m t Hm1).
  - unfold xs, ys. rewrite !lstN_length. reflexivity.
  - lra.
  - apply (coefficients_from_values R 0 1 Rplus Rmult Rminus Ropp RTheory
             Rmult_integral (Relist ys) (Rbump m t (Relist xs))
             (0 :: lstN rho N)).
    + constructor.
      * intro Hin. destruct (lstN_In rho N 0 Hin) as [j [Hj E]].
        pose proof (Hpos j Hj). lra.
      * apply NoDup_lstN. intros i j Hij Hj.
        destruct (Hint i ltac:(lia)) as [_ Hi2].
        destruct (Hint j Hj) as [Hj1 _].
        pose proof (tau_le (S i) j ltac:(lia)). lra.
    + rewrite (elist_length R 0 1 Rplus Rmult Ropp). unfold ys.
      cbn [length]. rewrite !lstN_length. lia.
    + rewrite (bump_length R 0 Rplus)
        by (rewrite (elist_length R 0 1 Rplus Rmult Ropp); unfold xs;
            rewrite lstN_length; lia).
      rewrite (elist_length R 0 1 Rplus Rmult Ropp). unfold xs.
      cbn [length]. rewrite !lstN_length. lia.
    + intros a [<-|Ha].
      * rewrite (peval_elist_zero R 0 1 Rplus Rmult Rminus Ropp RTheory).
        rewrite (peval_bump R 0 1 Rplus Rmult Rminus Ropp RTheory).
        rewrite (peval_elist_zero R 0 1 Rplus Rmult Rminus Ropp RTheory).
        destruct m as [|m']; [lia|]. simpl. ring.
      * destruct (lstN_In rho N a Ha) as [j [Hj ->]].
        unfold xs. rewrite (Hroot j Hj).
        apply (peval_elist_root R 0 1 Rplus Rmult Rminus Ropp RTheory
                 ys (/ rho j) (rho j)).
        -- unfold ys. apply (In_lstN (fun j0 => / rho j0) N j Hj).
        -- apply Rinv_l. pose proof (Hpos j Hj). lra.
Qed.

End Roots.

(* ===================================================================== *)
(* Part E.  THE DRAFT'S P8, negative half, at ITS threshold N >= d r.    *)
(* ===================================================================== *)

(* The general fragment, as section 9 argues it: pick ANY array of       *)
(* distinct positive reals where the leading coefficient is nonzero.     *)
(* The coefficient functions are ARBITRARY functions of the summary.     *)
Theorem closure_refused_R_at_point : forall (rt : nat -> R) d r N
    (a : nat -> list R -> R),
  (forall i, 0 < rt i) -> (forall i, rt i < rt (S i)) ->
  (2 <= d)%nat -> (1 <= r)%nat -> (d * r <= N)%nat ->
  a d (qr R 0 1 Rplus Rmult r (lstN (xN rt) N)) <> 0 ->
  forall G : unit -> list R -> list R,
    ~ (forall u y, length y = N ->
         qr R 0 1 Rplus Rmult r (Fgen R 0 1 Rplus Rmult d r a y)
         = G u (qr R 0 1 Rplus Rmult r y)).
Proof.
  intros rt d r N a Hpos Hincr Hd Hr HN Hlead.
  destruct (ideal_partner rt Hpos Hincr (d * r) N ltac:(nia) HN)
    as [ys [Hy [Heq Hne]]].
  apply (closure_refused_at_collision R 0 1 Rplus Rmult Rminus Ropp RTheory
           Rmult_integral R1_neq_R0 d r N a (lstN (xN rt) N) ys Hd Hr
           (lstN_length _ N) Hy Heq Hne Hlead).
Qed.

Lemma harmonic_roots :
  (forall i, 0 < INR (S i)) /\ (forall i, INR (S i) < INR (S (S i))).
Proof.
  split; intro i; [apply lt_0_INR; lia|]. rewrite (S_INR (S i)). lra.
Qed.

(* Unconditionally for the pure power x -> x^d ...                       *)
Theorem power_never_closes_R_at_threshold : forall d r N,
  (2 <= d)%nat -> (1 <= r)%nat -> (d * r <= N)%nat ->
  forall G : unit -> list R -> list R,
    ~ (forall u y, length y = N ->
         qr R 0 1 Rplus Rmult r (map (fun t => Rrpow t d) y)
         = G u (qr R 0 1 Rplus Rmult r y)).
Proof.
  intros d r N Hd Hr HN. destruct harmonic_roots as [Hpos Hincr].
  destruct (ideal_partner (fun i => INR (S i)) Hpos Hincr (d * r) N
              ltac:(nia) HN) as [ys [Hy [Heq Hne]]].
  apply (power_refused_at_collision R 0 1 Rplus Rmult Rminus Ropp RTheory
           Rmult_integral R1_neq_R0 d r N _ ys Hd Hr (lstN_length _ N)
           Hy Heq Hne).
Qed.

(* ... and whenever the leading coefficient vanishes nowhere.            *)
Theorem closure_refused_R_at_threshold : forall d r N
    (a : nat -> list R -> R),
  (2 <= d)%nat -> (1 <= r)%nat -> (d * r <= N)%nat ->
  (forall z, a d z <> 0) ->
  forall G : unit -> list R -> list R,
    ~ (forall u y, length y = N ->
         qr R 0 1 Rplus Rmult r (Fgen R 0 1 Rplus Rmult d r a y)
         = G u (qr R 0 1 Rplus Rmult r y)).
Proof.
  intros d r N a Hd Hr HN Hlead. destruct harmonic_roots as [Hpos Hincr].
  apply (closure_refused_R_at_point (fun i => INR (S i)) d r N a
           Hpos Hincr Hd Hr HN). apply Hlead.
Qed.
