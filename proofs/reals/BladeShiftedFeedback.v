(* ===================================================================== *)
(* reals/BladeShiftedFeedback.v -- EXACT RECURRENCE REDUCTION: the float *)
(* contract when the coefficient READS the summary.                      *)
(*                                                                       *)
(*       THIS FILE IS NOT PART OF THE AXIOM-FREE TOWER.                  *)
(*                                                                       *)
(* Coq's standard-library real numbers and exp / ln / sqrt:              *)
(* sig_forall_dec, sig_not_dec, functional_extensionality_dep and        *)
(* Classical_Prop.classic (the stdlib's exp lemmas are proved            *)
(* classically).  Own directory, own _CoqProject, not counted.  The      *)
(* standard rounding model is a section HYPOTHESIS.                      *)
(*                                                                       *)
(* BladeShiftedRounding takes the coefficients from the input.  Here the *)
(* multiplier a is computed from the moments -- by the float program     *)
(* from its OWN rounded moments.  The shift's update (through b) never   *)
(* enters the moments, M_k' = a^k M_k, so the moments are a closed       *)
(* feedback loop.                                                        *)
(*                                                                       *)
(*   rcl, rcl_mul, rcl_trans,  LOG-RELATIVE error, xh = theta x with     *)
(*   rcl_rel                   exp(-l) <= theta <= exp l: products add,  *)
(*                             and it bounds the ordinary relative error *)
(*                             by exp l - 1;                             *)
(*   rcl_pow, rcl_inv,         powers scale it, reciprocals keep it,     *)
(*   rcl_sqrt, rcl_add_pos     square roots halve it; a sum of           *)
(*                             non-negative terms keeps the worst;       *)
(*   lu, rnd_rcl               one rounding costs -ln(1 - u);            *)
(*   normalizing_coefficient_stable   the hypothesis DISCHARGED for      *)
(*                             fl(g / fl(sqrt M_2)): L = 1/2, eps = 2    *)
(*                             lu;                                       *)
(*   alpha_stable              THE ONE NEW HYPOTHESIS: moments to        *)
(*                             log-error l give the coefficient to       *)
(*                             L l + eps;                                *)
(*   feedback_bound, rad       after T steps the float moments are       *)
(*                             within log-error rad T, where rad grows   *)
(*                             by (1 + r L) rad + r (eps + lu) a step;   *)
(*   rad_closed, gsum_geometric,      rad T = (1 + r L)^T l0             *)
(*   rad_no_feedback           + r (eps + lu) ((1 + r L)^T - 1) / (r L); *)
(*                             LINEAR in T when L = 0;                   *)
(*   eiter_is_particles,       against the TRUE particle system, whose   *)
(*   feedback_particles,       coefficient is computed from the TRUE     *)
(*   feedback_particles_rel    moments, b arbitrary;                     *)
(*   normalized_ensemble_bound        instantiated: x' = (g / sqrt M_2)  *)
(*                             x + b, renormalized by its own spread.    *)
(*                                                                       *)
(* Scope, stated once.  The coefficient may read the moments M_1 .. M_r, *)
(* not the shift c (the mean).  Feedback amplifies: the radius grows     *)
(* geometrically at rate 1 + r L, which is the price of a coefficient    *)
(* that reads rounded data, not an artefact of the proof -- blade plan   *)
(* must report it, and a program with large L T is outside any useful    *)
(* contract.                                                             *)
(* ===================================================================== *)

From Blade Require Import BladeBinomial BladeSummary BladeMomentClosure
  BladeShiftedMoments.
Require Import Reals Lra List Lia.
Import ListNotations.
Open Scope R_scope.

Local Notation Aff := (aff R Rplus Rmult).
Local Notation Smom := (smom R 0 1 Rplus Rmult Rminus).

(* ===================================================================== *)
(* Part A.  Log-relative error.  xh = theta x with                       *)
(* exp(-l) <= theta <= exp l.  Products add, powers scale, and a zero is *)
(* only approximated by zero.                                            *)
(* ===================================================================== *)

Definition rcl (l xh x : R) : Prop :=
  exists th, xh = th * x /\ exp (- l) <= th <= exp l.

Lemma exp_le_mono : forall x y, x <= y -> exp x <= exp y.
Proof.
  intros x y H. destruct (Rle_lt_or_eq_dec x y H) as [Hl|He].
  - left. apply exp_increasing. exact Hl.
  - rewrite He. lra.
Qed.

Lemma rcl_refl : forall x, rcl 0 x x.
Proof.
  intro x. exists 1. split; [ring|]. rewrite Ropp_0, exp_0. lra.
Qed.

Lemma rcl_eq : forall xh x, rcl 0 xh x -> xh = x.
Proof.
  intros xh x [th [E B]]. rewrite Ropp_0, exp_0 in B.
  replace th with 1 in E by lra. rewrite E. ring.
Qed.

Lemma rcl_mono : forall l l' xh x, l <= l' -> rcl l xh x -> rcl l' xh x.
Proof.
  intros l l' xh x Hl [th [E B]]. exists th. split; [exact E|].
  pose proof (exp_le_mono (- l') (- l) ltac:(lra)).
  pose proof (exp_le_mono l l' Hl). lra.
Qed.

Lemma rcl_mul : forall l1 l2 yh y zh z,
  rcl l1 yh y -> rcl l2 zh z -> rcl (l1 + l2) (yh * zh) (y * z).
Proof.
  intros l1 l2 yh y zh z [t1 [E1 B1]] [t2 [E2 B2]].
  exists (t1 * t2). split; [rewrite E1, E2; ring|].
  pose proof (exp_pos (- l1)). pose proof (exp_pos (- l2)).
  rewrite Ropp_plus_distr, !exp_plus. split.
  - apply Rmult_le_compat; lra.
  - apply Rmult_le_compat; lra.
Qed.

(* Composition: approximating an approximation.                          *)
Lemma rcl_trans : forall l1 l2 zh yh x,
  rcl l1 zh yh -> rcl l2 yh x -> rcl (l1 + l2) zh x.
Proof.
  intros l1 l2 zh yh x [t1 [E1 B1]] [t2 [E2 B2]].
  exists (t1 * t2). split; [rewrite E1, E2; ring|].
  pose proof (exp_pos (- l1)). pose proof (exp_pos (- l2)).
  rewrite Ropp_plus_distr, !exp_plus. split.
  - apply Rmult_le_compat; lra.
  - apply Rmult_le_compat; lra.
Qed.

(* What a log-relative error means as an ordinary relative error.        *)
Lemma rcl_rel : forall l xh x, 0 <= l -> rcl l xh x ->
  Rabs (xh - x) <= (exp l - 1) * Rabs x.
Proof.
  intros l xh x Hl [th [E [B1 B2]]].
  assert (Hm : 1 - exp (- l) <= exp l - 1).
  { rewrite exp_Ropp. pose proof (exp_pos l).
    assert (1 <= exp l) by (rewrite <- exp_0; apply exp_le_mono; lra).
    assert (H2 : exp l + / exp l >= 2).
    { assert (Eq2 : exp l + / exp l - 2 = (exp l - 1) * (exp l - 1) / exp l)
        by (field; lra).
      assert (0 <= (exp l - 1) * (exp l - 1) / exp l).
      { unfold Rdiv. apply Rmult_le_pos; [nra|].
        left. apply Rinv_0_lt_compat. lra. }
      lra. }
    lra. }
  rewrite E. replace (th * x - x) with ((th - 1) * x) by ring.
  rewrite Rabs_mult. apply Rmult_le_compat_r; [apply Rabs_pos|].
  apply Rabs_le. lra.
Qed.

(* The coefficients people write satisfy the stability hypothesis:       *)
(* powers scale the error, reciprocals and square roots keep or halve    *)
(* it.                                                                   *)
Lemma rcl_pow : forall l xh x p, rcl l xh x -> rcl (INR p * l) (xh ^ p) (x ^ p).
Proof.
  intros l xh x p H. induction p as [|p IH].
  - simpl. replace (0 * l) with 0 by ring. apply rcl_refl.
  - rewrite S_INR. replace ((INR p + 1) * l) with (l + INR p * l) by ring.
    simpl. apply rcl_mul; assumption.
Qed.

Lemma rcl_nonneg : forall l xh x, rcl l xh x -> 0 <= l.
Proof.
  intros l xh x [th [_ [B1 B2]]].
  destruct (Rle_or_lt 0 l) as [H|H]; [exact H|].
  assert (exp l < exp (- l)) by (apply exp_increasing; lra). lra.
Qed.

Lemma exp_ge1 : forall l, 0 <= l -> 1 <= exp l /\ exp (- l) <= 1.
Proof.
  intros l H. rewrite <- exp_0. split; apply exp_le_mono; lra.
Qed.

Lemma rcl_inv : forall l xh x, rcl l xh x -> rcl l (/ xh) (/ x).
Proof.
  intros l xh x Hr. pose proof (rcl_nonneg _ _ _ Hr) as Hl.
  destruct Hr as [th [E [B1 B2]]].
  pose proof (exp_pos (- l)) as P.
  destruct (Req_dec x 0) as [Hx|Hx].
  - exists 1. rewrite E, Hx, Rmult_0_r, Rinv_0. split; [ring|].
    pose proof (exp_ge1 l Hl). lra.
  - exists (/ th). split.
    + rewrite E, Rinv_mult. reflexivity.
    + assert (Ht : 0 < th) by lra.
      split.
      * rewrite exp_Ropp. apply Rinv_le_contravar; lra.
      * assert (Ei : exp l = / exp (- l))
          by (rewrite exp_Ropp, Rinv_inv; reflexivity).
        rewrite Ei. apply Rinv_le_contravar; lra.
Qed.

(* A sum of NON-NEGATIVE terms is no worse than its worst term; with a   *)
(* subtraction in it, no such bound exists (cancellation).               *)
Lemma rcl_add_pos : forall l1 l2 ah a bh b, 0 <= a -> 0 <= b ->
  rcl l1 ah a -> rcl l2 bh b -> rcl (Rmax l1 l2) (ah + bh) (a + b).
Proof.
  intros l1 l2 ah a bh b Ha Hb H1 H2.
  pose proof (rcl_nonneg _ _ _ H1) as N1.
  pose proof (rcl_nonneg _ _ _ H2) as N2.
  apply (rcl_mono l1 (Rmax l1 l2)) in H1; [|apply Rmax_l].
  apply (rcl_mono l2 (Rmax l1 l2)) in H2; [|apply Rmax_r].
  set (l := Rmax l1 l2) in *.
  destruct H1 as [t1 [E1 [B1 C1]]]. destruct H2 as [t2 [E2 [B2 C2]]].
  destruct (Req_dec (a + b) 0) as [Z|Z].
  - assert (a = 0) by lra. assert (b = 0) by lra.
    exists 1. rewrite E1, E2. split; [subst; ring|].
    assert (Hl : 0 <= l)
      by (unfold l; apply (Rle_trans _ l1); [lra|apply Rmax_l]).
    pose proof (exp_ge1 l Hl).
    lra.
  - exists ((t1 * a + t2 * b) / (a + b)).
    split; [rewrite E1, E2; field; exact Z|].
    assert (Hp : 0 < a + b) by lra.
    split.
    + apply (Rmult_le_reg_r (a + b)); [exact Hp|].
      unfold Rdiv. rewrite Rmult_assoc, Rinv_l by lra. nra.
    + apply (Rmult_le_reg_r (a + b)); [exact Hp|].
      unfold Rdiv. rewrite Rmult_assoc, Rinv_l by lra. nra.
Qed.

Lemma sqrt_exp : forall y, sqrt (exp y) = exp (y / 2).
Proof.
  intro y. replace (exp y) with (exp (y / 2) * exp (y / 2))
    by (rewrite <- exp_plus; f_equal; field).
  apply sqrt_square. left. apply exp_pos.
Qed.

Lemma rcl_sqrt : forall l xh x, rcl l xh x -> rcl (l / 2) (sqrt xh) (sqrt x).
Proof.
  intros l xh x Hr. pose proof (rcl_nonneg _ _ _ Hr) as Hl.
  destruct Hr as [th [E [B1 B2]]].
  pose proof (exp_pos (- l)) as P.
  destruct (Rle_or_lt x 0) as [Hx|Hx].
  - assert (xh <= 0) by (rewrite E; nra).
    rewrite (sqrt_neg_0 x Hx), (sqrt_neg_0 xh H).
    exists 1. split; [ring|].
    pose proof (exp_ge1 (l / 2) ltac:(lra)). lra.
  - exists (sqrt th). split.
    + rewrite E. apply sqrt_mult_alt. lra.
    + replace (- (l / 2)) with (- l / 2) by field.
      rewrite <- !sqrt_exp. split; apply sqrt_le_1_alt; lra.
Qed.


(* ===================================================================== *)
(* Part B.  Rounded arithmetic, the standard model as a HYPOTHESIS.      *)
(* ===================================================================== *)

Section Feedback.
  Variable u : R.
  Hypothesis Hu : 0 <= u < 1.
  Variable rnd : R -> R.
  Hypothesis rnd_rel : forall x, Rabs (rnd x - x) <= u * Rabs x.

  (* One rounding, in log-relative form: -ln(1 - u), at most u/(1-u).    *)
  Definition lu : R := - ln (1 - u).

  Lemma lu_nonneg : 0 <= lu.
  Proof.
    unfold lu. assert (ln (1 - u) <= 0).
    { rewrite <- ln_1. destruct (Rle_lt_or_eq_dec (1 - u) 1 ltac:(lra))
        as [Hl|He].
      - left. apply ln_increasing; lra.
      - rewrite He. lra. }
    lra.
  Qed.

  Lemma exp_lu : exp lu = / (1 - u).
  Proof. unfold lu. rewrite exp_Ropp, exp_ln by lra. reflexivity. Qed.

  Lemma exp_mlu : exp (- lu) = 1 - u.
  Proof. unfold lu. rewrite Ropp_involutive, exp_ln by lra. reflexivity. Qed.

  Lemma rnd_rcl : forall y, rcl lu (rnd y) y.
  Proof.
    intro y. pose proof (rnd_rel y) as H.
    destruct (Req_dec y 0) as [Hy|Hy].
    - subst y. rewrite Rabs_R0, Rmult_0_r, Rminus_0_r in H.
      assert (rnd 0 = 0).
      { destruct (Req_dec (rnd 0) 0) as [E0|E0]; [exact E0|].
        pose proof (Rabs_pos_lt _ E0). lra. }
      exists 1. split; [rewrite H0; ring|].
      rewrite exp_mlu, exp_lu.
      assert (1 <= / (1 - u)).
      { rewrite <- Rinv_1. apply Rinv_le_contravar; lra. }
      lra.
    - exists (rnd y / y). split; [field; exact Hy|].
      rewrite exp_mlu, exp_lu.
      assert (Hq : Rabs (rnd y / y - 1) <= u).
      { replace (rnd y / y - 1) with ((rnd y - y) / y) by (field; exact Hy).
        unfold Rdiv. rewrite Rabs_mult, Rabs_inv.
        apply (Rmult_le_reg_r (Rabs y)); [apply Rabs_pos_lt; exact Hy|].
        rewrite Rmult_assoc, Rinv_l by (apply Rabs_no_R0; exact Hy).
        lra. }
      assert (Hq2 : - u <= rnd y / y - 1 <= u).
      { split.
        - pose proof (Rle_abs (- (rnd y / y - 1))) as Hn.
          rewrite Rabs_Ropp in Hn. lra.
        - pose proof (Rle_abs (rnd y / y - 1)). lra. }
      split; [lra|].
      assert (1 + u <= / (1 - u)).
      { apply (Rmult_le_reg_r (1 - u)); [lra|].
        rewrite Rinv_l by lra. nra. }
      lra.
  Qed.

  Lemma rcl_rnd : forall l xh x, rcl l xh x -> rcl (lu + l) (rnd xh) x.
  Proof. intros l xh x H. exact (rcl_trans lu l _ _ _ (rnd_rcl xh) H). Qed.

  (* THE HYPOTHESIS, DISCHARGED for a normalizing coefficient: a = g /   *)
  (* sqrt(M_2), computed as fl(g / fl(sqrt(M_2))), satisfies the         *)
  (* stability hypothesis below with L = 1/2 and eps = 2 lu.             *)
  Lemma normalizing_coefficient_stable : forall g l mh m, 0 <= l ->
    rcl l (mh 2%nat) (m 2%nat) ->
    rcl (/ 2 * l + 2 * lu) (rnd (g * / rnd (sqrt (mh 2%nat))))
        (g * / sqrt (m 2%nat)).
  Proof.
    intros g l mh m Hl H.
    pose proof (rcl_rnd _ _ _ (rcl_sqrt _ _ _ H)) as S1.
    pose proof (rcl_inv _ _ _ S1) as S2.
    pose proof (rcl_mul 0 _ g g _ _ (rcl_refl g) S2) as S3.
    pose proof (rcl_rnd _ _ _ S3) as S4.
    eapply rcl_mono; [|exact S4]. lra.
  Qed.

  (* a^k by repeated multiplication: k - 1 roundings.                    *)
  Fixpoint fpow (a : R) (k : nat) : R :=
    match k with
    | O => 1
    | S O => a
    | S k' => rnd (a * fpow a k')
    end.

  Lemma fpow_rcl : forall la ah a k, (1 <= k)%nat -> 0 <= la ->
    rcl la ah a ->
    rcl (INR k * la + INR (k - 1) * lu) (fpow ah k) (a ^ k).
  Proof.
    intros la ah a k Hk Hla Ha. induction k as [|k IH]; [lia|].
    destruct k as [|k].
    - cbn [fpow]. replace (1 - 1)%nat with 0%nat by lia.
      replace (INR 1 * la + INR 0 * lu) with la by (simpl; ring).
      replace (a ^ 1) with a by ring. exact Ha.
    - change (fpow ah (S (S k))) with (rnd (ah * fpow ah (S k))).
      specialize (IH ltac:(lia)).
      pose proof (rcl_mul _ _ _ _ _ _ Ha IH) as M.
      pose proof (rcl_rnd _ _ _ M) as R1.
      change (a ^ S (S k)) with (a * a ^ S k).
      eapply rcl_mono; [|exact R1].
      replace (S (S k) - 1)%nat with (S k) by lia.
      replace (S k - 1)%nat with k by lia.
      rewrite !S_INR. lra.
  Qed.

  (* ------------------------------------------------------------------- *)
  (* Part C.  THE FEEDBACK LOOP.  The moments m = (M_1 .. M_r) and a     *)
  (* coefficient a = alpha(m) that READS them.  b never enters the       *)
  (* moments (M_k' = a^k M_k), so this subsystem is closed.              *)
  (* ------------------------------------------------------------------- *)

  Variable r : nat.
  Hypothesis Hr : (1 <= r)%nat.

  (* The exact coefficient, and the one the float program computes.      *)
  Variables alpha alphah : (nat -> R) -> R.

  (* STABILITY OF THE COEFFICIENT, the one new hypothesis: moments known *)
  (* to log-relative error l give the coefficient to L l + eps.  eps     *)
  (* absorbs the coefficient's own rounding.  Monomials in the moments   *)
  (* (with any real exponents -- 1 / M_2, sqrt M_2) satisfy it with L    *)
  (* the sum of |exponents|.                                             *)
  Variables L eps : R.
  Hypothesis HL0 : 0 <= L.
  Hypothesis He0 : 0 <= eps.
  Hypothesis alpha_stable : forall l mh m, 0 <= l ->
    (forall k, (1 <= k)%nat -> (k <= r)%nat -> rcl l (mh k) (m k)) ->
    rcl (L * l + eps) (alphah mh) (alpha m).

  Definition estep (m : nat -> R) : nat -> R :=
    fun k => alpha m ^ k * m k.
  Definition fstep (mh : nat -> R) : nat -> R :=
    fun k => rnd (fpow (alphah mh) k * mh k).

  Fixpoint eiter (T : nat) (m : nat -> R) : nat -> R :=
    match T with O => m | S T' => eiter T' (estep m) end.
  Fixpoint fiter (T : nat) (mh : nat -> R) : nat -> R :=
    match T with O => mh | S T' => fiter T' (fstep mh) end.

  (* The error radius, one step at a time.                               *)
  Fixpoint rad (l0 : R) (T : nat) : R :=
    match T with
    | O => l0
    | S T' => (1 + INR r * L) * rad l0 T' + INR r * (eps + lu)
    end.

  Lemma rad_nonneg : forall l0 T, 0 <= l0 -> 0 <= rad l0 T.
  Proof.
    intros l0 T H. induction T as [|T IH]; cbn [rad]; [exact H|].
    pose proof (pos_INR r). pose proof lu_nonneg.
    assert (0 <= INR r * L) by (apply Rmult_le_pos; lra).
    assert (0 <= (1 + INR r * L) * rad l0 T) by (apply Rmult_le_pos; lra).
    assert (0 <= INR r * (eps + lu)) by (apply Rmult_le_pos; lra).
    lra.
  Qed.

  Lemma step_rcl : forall l mh m, 0 <= l ->
    (forall k, (1 <= k)%nat -> (k <= r)%nat -> rcl l (mh k) (m k)) ->
    forall k, (1 <= k)%nat -> (k <= r)%nat ->
      rcl ((1 + INR r * L) * l + INR r * (eps + lu))
          (fstep mh k) (estep m k).
  Proof.
    intros l mh m Hl Hm k Hk1 Hkr. unfold fstep, estep.
    pose proof (alpha_stable l mh m Hl Hm) as Ha.
    assert (Hla : 0 <= L * l + eps)
      by (pose proof (Rmult_le_pos L l HL0 Hl); lra).
    pose proof (fpow_rcl _ _ _ k Hk1 Hla Ha) as P.
    pose proof (rcl_rnd _ _ _ (rcl_mul _ _ _ _ _ _ P (Hm k Hk1 Hkr))) as S1.
    eapply rcl_mono; [|exact S1].
    assert (Hk : INR k <= INR r) by (apply le_INR; exact Hkr).
    assert (Hk' : INR (k - 1) <= INR k) by (apply le_INR; lia).
    pose proof (pos_INR (k - 1)). pose proof lu_nonneg.
    (* lu + (k (L l + eps) + (k-1) lu + l) <= (1 + r L) l + r (eps + lu) *)
    assert (A : INR k * (L * l + eps) <= INR r * (L * l + eps))
      by (apply Rmult_le_compat_r; lra).
    assert (B : INR (k - 1) * lu + lu <= INR r * lu).
    { replace (k - 1)%nat with (k - 1)%nat by reflexivity.
      assert (INR (k - 1) + 1 = INR k).
      { rewrite <- S_INR. f_equal. lia. }
      assert (INR k * lu <= INR r * lu) by (apply Rmult_le_compat_r; lra).
      nra. }
    nra.
  Qed.

  Definition step_l (l : R) : R := (1 + INR r * L) * l + INR r * (eps + lu).

  Lemma step_l_nonneg : forall l, 0 <= l -> 0 <= step_l l.
  Proof.
    intros l Hl. unfold step_l. pose proof (pos_INR r). pose proof lu_nonneg.
    assert (0 <= INR r * L) by (apply Rmult_le_pos; lra).
    assert (0 <= (1 + INR r * L) * l) by (apply Rmult_le_pos; lra).
    assert (0 <= INR r * (eps + lu)) by (apply Rmult_le_pos; lra).
    lra.
  Qed.

  (* Peeling the FIRST step instead of the last.                         *)
  Lemma rad_shift : forall T l0, rad l0 (S T) = rad (step_l l0) T.
  Proof.
    induction T as [|T IH]; intro l0; [reflexivity|].
    change (rad l0 (S (S T))) with ((1 + INR r * L) * rad l0 (S T)
                                    + INR r * (eps + lu)).
    rewrite IH. reflexivity.
  Qed.

  (* THE FEEDBACK BOUND.  Moments known to log-relative error l0 at the  *)
  (* seed stay within rad l0 T after T steps of the float program, whose *)
  (* coefficient is computed from its OWN rounded moments.               *)
  Theorem feedback_bound : forall T l0 mh m, 0 <= l0 ->
    (forall k, (1 <= k)%nat -> (k <= r)%nat -> rcl l0 (mh k) (m k)) ->
    forall k, (1 <= k)%nat -> (k <= r)%nat ->
      rcl (rad l0 T) (fiter T mh k) (eiter T m k).
  Proof.
    induction T as [|T IH]; intros l0 mh m Hl0 Hm k Hk1 Hkr.
    - exact (Hm k Hk1 Hkr).
    - cbn [fiter eiter]. rewrite rad_shift.
      apply IH; [apply step_l_nonneg; exact Hl0| |exact Hk1|exact Hkr].
      exact (step_rcl l0 mh m Hl0 Hm).
  Qed.

  (* The radius in closed form.  gsum q T = 1 + q + ... + q^(T-1), in    *)
  (* Horner form.                                                        *)
  Fixpoint gsum (q : R) (T : nat) : R :=
    match T with O => 0 | S T' => 1 + q * gsum q T' end.

  Lemma gsum_geometric : forall q T, q <> 1 -> gsum q T = (q ^ T - 1) / (q - 1).
  Proof.
    intros q T Hq. induction T as [|T IH]; cbn [gsum pow].
    - field. lra.
    - rewrite IH. field. lra.
  Qed.

  Lemma rad_closed : forall l0 T,
    rad l0 T = (1 + INR r * L) ^ T * l0
               + INR r * (eps + lu) * gsum (1 + INR r * L) T.
  Proof.
    intros l0 T. induction T as [|T IH]; cbn [rad gsum pow]; [ring|].
    rewrite IH. ring.
  Qed.

  (* No feedback (L = 0): the radius grows LINEARLY in T.                *)
  Corollary rad_no_feedback : L = 0 -> forall l0 T,
    rad l0 T = l0 + INR T * INR r * (eps + lu).
  Proof.
    intros H0 l0 T. induction T as [|T IH]; cbn [rad]; [simpl; ring|].
    rewrite IH, H0, S_INR. ring.
  Qed.

  (* ------------------------------------------------------------------- *)
  (* Part D.  The exact run IS the true particle system.  Particles move *)
  (* by x' = a x + b with a = alpha(shifted sums), b = beta(anything):   *)
  (* the exact reduced run tracks their shifted sums about the           *)
  (* propagated shift -- BladeShiftedMoments.smom_aff at R.              *)
  (* ------------------------------------------------------------------- *)

  Hypothesis alpha_ext : forall m m',
    (forall k, (1 <= k)%nat -> (k <= r)%nat -> m k = m' k) ->
    alpha m = alpha m'.

  Variable beta : R -> (nat -> R) -> R.

  Definition smv (c : R) (xs : list R) : nat -> R := fun k => Smom c k xs.

  Definition pstep (st : R * list R) : R * list R :=
    let m := smv (fst st) (snd st) in
    let a := alpha m in
    let b := beta (fst st) m in
    (Aff a b (fst st), map (Aff a b) (snd st)).

  Fixpoint piter (T : nat) (st : R * list R) : R * list R :=
    match T with O => st | S T' => piter T' (pstep st) end.

  Lemma rpow_R : forall a k, rpow R 1 Rmult a k = a ^ k.
  Proof.
    intros a k. induction k as [|k IH]; simpl; [reflexivity|].
    rewrite IH. ring.
  Qed.

  Lemma eiter_ext : forall T f g, (forall k, f k = g k) ->
    forall k, eiter T f k = eiter T g k.
  Proof.
    induction T as [|T IH]; intros f g H k; [apply H|].
    cbn [eiter]. apply IH. intro j. unfold estep.
    rewrite (alpha_ext f g (fun i _ _ => H i)), H. reflexivity.
  Qed.

  Lemma smv_step : forall c xs k,
    smv (fst (pstep (c, xs))) (snd (pstep (c, xs))) k
    = estep (smv c xs) k.
  Proof.
    intros c xs k. unfold pstep, smv, estep. cbn [fst snd].
    rewrite (smom_aff R 0 1 Rplus Rmult Rminus Ropp RTheory), rpow_R.
    reflexivity.
  Qed.

  (* The exact reduced run IS the shifted sums of the true particles,    *)
  (* whose coefficient is computed from their TRUE moments.              *)
  Theorem eiter_is_particles : forall T c xs k,
    smv (fst (piter T (c, xs))) (snd (piter T (c, xs))) k
    = eiter T (smv c xs) k.
  Proof.
    induction T as [|T IH]; intros c xs k; [reflexivity|].
    cbn [piter eiter].
    destruct (pstep (c, xs)) as [c' xs'] eqn:E.
    rewrite IH. apply eiter_ext. intro j.
    rewrite <- (smv_step c xs j), E. reflexivity.
  Qed.

  (* THE CONTRACT WITH FEEDBACK.  The float reduced run, its coefficient *)
  (* computed from its own rounded moments, against the true particle    *)
  (* system with the coefficient computed from the true moments.         *)
  Theorem feedback_particles : forall l0 mh c xs, 0 <= l0 ->
    (forall k, (1 <= k)%nat -> (k <= r)%nat -> rcl l0 (mh k) (Smom c k xs)) ->
    forall T k, (1 <= k)%nat -> (k <= r)%nat ->
      rcl (rad l0 T) (fiter T mh k)
          (Smom (fst (piter T (c, xs))) k (snd (piter T (c, xs)))).
  Proof.
    intros l0 mh c xs Hl0 Hm T k Hk1 Hkr.
    change (Smom (fst (piter T (c, xs))) k (snd (piter T (c, xs))))
      with (smv (fst (piter T (c, xs))) (snd (piter T (c, xs))) k).
    rewrite eiter_is_particles.
    apply feedback_bound; assumption.
  Qed.

  (* The same as an ordinary relative error: exp(rad) - 1.               *)
  Corollary feedback_particles_rel : forall l0 mh c xs, 0 <= l0 ->
    (forall k, (1 <= k)%nat -> (k <= r)%nat -> rcl l0 (mh k) (Smom c k xs)) ->
    forall T k, (1 <= k)%nat -> (k <= r)%nat ->
      let M := Smom (fst (piter T (c, xs))) k (snd (piter T (c, xs))) in
      Rabs (fiter T mh k - M) <= (exp (rad l0 T) - 1) * Rabs M.
  Proof.
    intros l0 mh c xs Hl0 Hm T k Hk1 Hkr M.
    apply rcl_rel; [|apply feedback_particles; assumption].
    induction T as [|T IH]; cbn [rad]; [exact Hl0|].
    apply (step_l_nonneg (rad l0 T)). apply IH.
  Qed.

End Feedback.

(* ===================================================================== *)
(* Part E.  The contract, instantiated: an ensemble RENORMALIZED by its  *)
(* own spread each step, x' = (g / sqrt(M_2)) x + b, with the float      *)
(* program computing the coefficient from its own rounded M_2.           *)
(* ===================================================================== *)

Corollary normalized_ensemble_bound : forall u rnd (g : R)
    (beta : R -> (nat -> R) -> R) r l0 mh c xs,
  0 <= u < 1 -> (forall x, Rabs (rnd x - x) <= u * Rabs x) ->
  (2 <= r)%nat -> 0 <= l0 ->
  (forall k, (1 <= k)%nat -> (k <= r)%nat -> rcl l0 (mh k) (Smom c k xs)) ->
  forall T k, (1 <= k)%nat -> (k <= r)%nat ->
    rcl (rad u r (/ 2) (2 * lu u) l0 T)
        (fiter rnd (fun mh => rnd (g * / rnd (sqrt (mh 2%nat)))) T mh k)
        (Smom (fst (piter (fun m => g * / sqrt (m 2%nat)) beta T (c, xs))) k
              (snd (piter (fun m => g * / sqrt (m 2%nat)) beta T (c, xs)))).
Proof.
  intros u rnd g beta r l0 mh c xs Hu Hrnd Hr Hl0 Hm T k Hk1 Hkr.
  apply (feedback_particles u Hu rnd Hrnd r ltac:(lia)
           (fun m => g * / sqrt (m 2%nat))
           (fun mh => rnd (g * / rnd (sqrt (mh 2%nat))))
           (/ 2) (2 * lu u)); try assumption.
  - left. apply Rinv_0_lt_compat. lra.
  - pose proof (lu_nonneg u Hu). lra.
  - intros l mh1 m1 Hl H.
    apply (normalizing_coefficient_stable u Hu rnd Hrnd g l mh1 m1 Hl).
    apply H; lia.
  - intros m m' H. rewrite (H 2%nat) by lia. reflexivity.
Qed.
