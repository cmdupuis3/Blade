(* ===================================================================== *)
(* reals/BladeShiftedVariance.v -- EXACT RECURRENCE REDUCTION: the       *)
(* VARIANCE in a floating-point coefficient.                             *)
(*                                                                       *)
(*       THIS FILE IS NOT PART OF THE AXIOM-FREE TOWER.                  *)
(*                                                                       *)
(* The four axioms of BladeShiftedFeedback (sig_forall_dec, sig_not_dec, *)
(* functional_extensionality_dep, Classical_Prop.classic).  Own          *)
(* directory, own _CoqProject, not counted.  The standard rounding model *)
(* is a section HYPOTHESIS.                                              *)
(*                                                                       *)
(* BladeShiftedFeedback bounds coefficients built from the tracked sums  *)
(* by products, powers, reciprocals, square roots and positive sums.     *)
(* The variance M_2/N - (M_1/N)^2 has a SUBTRACTION.  It is harmless     *)
(* because the subtrahend is dominated: rho = M_1^2/(N M_2) is exactly   *)
(* invariant under affine steps (BladeShiftedMoments.rho_invariant) and  *)
(* at rounding level after a two-pass seed.                              *)
(*                                                                       *)
(*   Lo, rcl_diff_dominant     SUBTRACTION WITH A DOMINANT MINUEND: for  *)
(*                             0 <= z <= rho y, rho < 1, operands at     *)
(*                             log-errors ly, lz give y - z to log-error *)
(*                             -ln Lo, Lo = (exp(-ly) - exp(lz) rho) /   *)
(*                             (1 - rho), whenever Lo > 0;               *)
(*   Lo_le_1, neg_ln_nonneg    so the error is a genuine one;            *)
(*   vex, vfl, DV,             THE VARIANCE AS A FLOAT PROGRAM COMPUTES  *)
(*                             IT:                                       *)
(*   variance_rcl              moments at log-error l give it to DV rho  *)
(*                             l;                                        *)
(*   grad, feedback_bound_inv  the feedback theorem with a GENERAL error *)
(*                             function and an INVARIANT of the true     *)
(*                             run, valid while the radius stays where   *)
(*                             the error rule holds -- the variance rule *)
(*                             is not linear, so BladeShiftedFeedback's  *)
(*                             form does not apply;                      *)
(*   alpha_sd, Inv_sd,         THE INSTANCE: x' = (g / sd) x + b, sd the *)
(*   Inv_sd_step,              float standard deviation of the float     *)
(*   alpha_sd_stable,          moments, against the TRUE particle system *)
(*   renormalized_by_stddev_bound     renormalized by its true standard  *)
(*                             deviation.                                *)
(*                                                                       *)
(* Scope, stated once.  The radius is computed by iterating the error    *)
(* rule (grad), not in closed form; blade plan evaluates it.  The bound  *)
(* needs Lo > 0 at the final radius: it fails, honestly, once the        *)
(* accumulated error is comparable to 1 - rho.  Coefficients reading the *)
(* mean remain uncovered.                                                *)
(* ===================================================================== *)

From Blade Require Import BladeBinomial BladeSummary BladeMomentClosure
  BladeShiftedMoments.
From BladeReals Require Import BladeShiftedFeedback.
Require Import Reals Lra List Lia.
Import ListNotations.
Open Scope R_scope.

Local Notation Smom := (smom R 0 1 Rplus Rmult Rminus).

(* ===================================================================== *)
(* Part A.  SUBTRACTION WITH A DOMINANT MINUEND.  y - z with             *)
(* 0 <= z <= rho y, rho < 1: the log-relative error of the difference is *)
(* -ln Lo, Lo = (exp(-ly) - exp(lz) rho) / (1 - rho), as long as Lo > 0. *)
(* ===================================================================== *)

Definition Lo (ly lz rho : R) : R := (exp (- ly) - exp lz * rho) / (1 - rho).

Lemma exp_inv_pair : forall l, exp l * exp (- l) = 1.
Proof.
  intro l. rewrite <- exp_plus. replace (l + - l) with 0 by ring.
  apply exp_0.
Qed.

Theorem rcl_diff_dominant : forall ly lz rho yh y zh z,
  0 < y -> 0 <= z -> z <= rho * y -> 0 <= rho < 1 ->
  rcl ly yh y -> rcl lz zh z -> 0 < Lo ly lz rho ->
  rcl (- ln (Lo ly lz rho)) (yh - zh) (y - z).
Proof.
  intros ly lz rho yh y zh z Hy Hz0 Hzr Hrho Ry Rz HLo.
  pose proof (rcl_nonneg _ _ _ Ry) as Nly.
  pose proof (rcl_nonneg _ _ _ Rz) as Nlz.
  destruct Ry as [ty [Ey [By1 By2]]]. destruct Rz as [tz [Ez [Bz1 Bz2]]].
  set (P := exp ly) in *. set (P' := exp (- ly)) in *.
  set (Q := exp lz) in *. set (Q' := exp (- lz)) in *.
  assert (PP : P * P' = 1) by apply exp_inv_pair.
  assert (QQ : Q * Q' = 1) by apply exp_inv_pair.
  destruct (exp_ge1 ly Nly) as [P1 P'1]. destruct (exp_ge1 lz Nlz) as [Q1 Q'1].
  fold P P' in P1, P'1. fold Q Q' in Q1, Q'1.
  assert (HP' : 0 < P') by apply exp_pos.
  assert (HQ' : 0 < Q') by apply exp_pos.
  set (L0 := Lo ly lz rho) in *.
  assert (EL : L0 * (1 - rho) = P' - Q * rho)
    by (unfold L0, Lo, P', Q; field; lra).
  (* the actual ratio                                                    *)
  set (ra := z / y).
  assert (Hra0 : 0 <= ra) by (unfold ra; apply Rmult_le_pos; [lra|];
                              left; apply Rinv_0_lt_compat; lra).
  assert (Hrar : ra <= rho).
  { unfold ra. apply (Rmult_le_reg_r y); [lra|].
    unfold Rdiv. rewrite Rmult_assoc, Rinv_l by lra. lra. }
  assert (Ez' : z = ra * y) by (unfold ra; field; lra).
  set (th := (ty - tz * ra) / (1 - ra)).
  exists th. split.
  { rewrite Ey, Ez, Ez'. unfold th. field. lra. }
  rewrite Ropp_involutive, exp_ln by exact HLo.
  rewrite exp_Ropp, exp_ln by exact HLo.
  assert (Hth : th * (1 - ra) = ty - tz * ra) by (unfold th; field; lra).
  split.
  - (* L0 <= th *)
    assert (S1 : ty - tz * ra >= P' - Q * ra).
    { assert (tz * ra <= Q * ra) by (apply Rmult_le_compat_r; lra). lra. }
    assert (S2 : (P' - Q * ra) * (1 - rho) - (P' - Q * rho) * (1 - ra)
                 = (Q - P') * (rho - ra)) by ring.
    assert (S3 : 0 <= (Q - P') * (rho - ra)) by (apply Rmult_le_pos; lra).
    assert (S4 : th * (1 - ra) * (1 - rho) >= L0 * (1 - rho) * (1 - ra)).
    { rewrite Hth, EL. nra. }
    assert (S5 : 0 < (1 - ra) * (1 - rho)) by (apply Rmult_lt_0_compat; lra).
    nra.
  - (* th <= / L0 *)
    assert (U1 : ty - tz * ra <= P - Q' * ra).
    { assert (Q' * ra <= tz * ra) by (apply Rmult_le_compat_r; lra). lra. }
    (* (P - Q' ra)(1 - rho) <= (P - Q' rho)(1 - ra)                      *)
    assert (U2 : (P - Q' * rho) * (1 - ra) - (P - Q' * ra) * (1 - rho)
                 = (P - Q') * (rho - ra)) by ring.
    assert (U3 : 0 <= (P - Q') * (rho - ra)) by (apply Rmult_le_pos; lra).
    (* (P - Q' rho)(P' - Q rho) <= (1 - rho)^2, by P Q + P' Q' >= 2      *)
    assert (A2 : P * Q + P' * Q' >= 2).
    { assert (PQ : (P * Q) * (P' * Q') = 1) by
        (replace ((P * Q) * (P' * Q')) with ((P * P') * (Q * Q')) by ring;
         rewrite PP, QQ; ring).
      assert (0 < P * Q) by (apply Rmult_lt_0_compat; lra).
      nra. }
    assert (U4 : (P - Q' * rho) * (P' - Q * rho) <= (1 - rho) * (1 - rho)).
    { replace ((P - Q' * rho) * (P' - Q * rho))
        with (P * P' - rho * (P * Q + P' * Q') + (Q * Q') * (rho * rho))
        by ring.
      rewrite PP, QQ. nra. }
    (* combine: th (1-ra)(1-rho) L0 (1-rho) <= (1-rho)^2 (1-ra)          *)
    assert (Hr1 : 0 < 1 - rho) by lra. assert (Hra1 : 0 < 1 - ra) by lra.
    assert (Hp : 0 <= P - Q' * rho).
    { assert (Q' * rho <= 1 * 1) by (apply Rmult_le_compat; lra). lra. }
    assert (T1 : th * (1 - ra) * (1 - rho) <= (P - Q' * rho) * (1 - ra)).
    { rewrite Hth.
      assert ((ty - tz * ra) * (1 - rho) <= (P - Q' * ra) * (1 - rho))
        by (apply Rmult_le_compat_r; lra).
      lra. }
    assert (T2 : th * (1 - ra) * (1 - rho) * (L0 * (1 - rho))
                 <= (P - Q' * rho) * (1 - ra) * (P' - Q * rho)).
    { assert (0 < P' - Q * rho)
        by (rewrite <- EL; apply Rmult_lt_0_compat; lra).
      rewrite EL. apply Rmult_le_compat_r; [lra|exact T1]. }
    assert (T3 : th * L0 * ((1 - ra) * (1 - rho) * (1 - rho))
                 <= 1 * ((1 - ra) * (1 - rho) * (1 - rho))).
    { replace (th * L0 * ((1 - ra) * (1 - rho) * (1 - rho)))
        with (th * (1 - ra) * (1 - rho) * (L0 * (1 - rho))) by ring.
      eapply Rle_trans; [exact T2|].
      replace ((P - Q' * rho) * (1 - ra) * (P' - Q * rho))
        with ((P - Q' * rho) * (P' - Q * rho) * (1 - ra)) by ring.
      replace (1 * ((1 - ra) * (1 - rho) * (1 - rho)))
        with ((1 - rho) * (1 - rho) * (1 - ra)) by ring.
      apply Rmult_le_compat_r; lra. }
    assert (T4 : th * L0 <= 1).
    { apply (Rmult_le_reg_r ((1 - ra) * (1 - rho) * (1 - rho))).
      - apply Rmult_lt_0_compat; [apply Rmult_lt_0_compat|]; lra.
      - exact T3. }
    apply (Rmult_le_reg_r L0); [exact HLo|].
    rewrite Rinv_l by lra. lra.
Qed.

(* Lo is at most 1, so -ln Lo is a genuine (non-negative) error.         *)
Lemma Lo_le_1 : forall ly lz rho, 0 <= ly -> 0 <= lz -> 0 <= rho < 1 ->
  Lo ly lz rho <= 1.
Proof.
  intros ly lz rho H1 H2 Hr. unfold Lo.
  destruct (exp_ge1 ly H1) as [_ A]. destruct (exp_ge1 lz H2) as [B _].
  apply (Rmult_le_reg_r (1 - rho)); [lra|].
  unfold Rdiv. rewrite Rmult_assoc, Rinv_l by lra.
  assert (rho <= exp lz * rho) by nra. lra.
Qed.

Lemma neg_ln_nonneg : forall x, x <= 1 -> 0 <= - ln x.
Proof.
  intros x Hx. destruct (Rlt_or_le 0 x) as [H|H].
  - destruct (Rle_lt_or_eq_dec x 1 Hx) as [Hl|He].
    + assert (ln x < ln 1) by (apply ln_increasing; lra).
      rewrite ln_1 in H0. lra.
    + rewrite He, ln_1. lra.
  - unfold ln. destruct (Rlt_dec 0 x) as [H'|H'].
    + exfalso. lra.
    + apply Req_le. ring.
Qed.

Lemma rcl_scale_const : forall l xh x k, rcl l xh x -> rcl l (xh * k) (x * k).
Proof.
  intros l xh x k [t [E B]]. exists t. split; [rewrite E; ring|exact B].
Qed.

(* ===================================================================== *)
(* Part B.  The variance, as a float program computes it.                *)
(* ===================================================================== *)

Section Variance.
  Variable u : R.
  Hypothesis Hu : 0 <= u < 1.
  Variable rnd : R -> R.
  Hypothesis rnd_rel : forall x, Rabs (rnd x - x) <= u * Rabs x.

  Local Notation lam := (lu u).

  (* M_2 / N - (M_1 / N)^2, exact and in floating point.                 *)
  Definition vex (N : R) (m : nat -> R) : R :=
    m 2%nat * / N - (m 1%nat * / N) * (m 1%nat * / N).

  Definition vfl (N : R) (mh : nat -> R) : R :=
    let q := rnd (mh 1%nat * / N) in
    rnd (rnd (mh 2%nat * / N) - rnd (q * q)).

  (* The log-error of the float variance, from moments at log-error l.   *)
  Definition DV (rho l : R) : R :=
    lam + - ln (Lo (lam + l) (lam + ((lam + l) + (lam + l))) rho).

  Theorem variance_rcl : forall N rho l mh m,
    0 < N -> 0 < m 2%nat -> m 1%nat * m 1%nat <= rho * (N * m 2%nat) ->
    0 <= rho < 1 -> 0 <= l ->
    rcl l (mh 1%nat) (m 1%nat) -> rcl l (mh 2%nat) (m 2%nat) ->
    0 < Lo (lam + l) (lam + ((lam + l) + (lam + l))) rho ->
    rcl (DV rho l) (vfl N mh) (vex N m).
  Proof.
    intros N rho l mh m HN Hm2 Hrho Hr Hl H1 H2 HLo.
    unfold vfl, vex, DV.
    apply (rcl_rnd u Hu rnd rnd_rel).
    apply rcl_diff_dominant.
    - apply Rmult_lt_0_compat; [lra|]. apply Rinv_0_lt_compat. lra.
    - nra.
    - (* (m1/N)^2 <= rho (m2/N) *)
      assert (Hi : 0 < / N) by (apply Rinv_0_lt_compat; lra).
      replace (m 1%nat * / N * (m 1%nat * / N))
        with (m 1%nat * m 1%nat * (/ N * / N))
        by ring.
      replace (rho * (m 2%nat * / N)) with (rho * (N * m 2%nat) * (/ N * / N))
        by (field; lra).
      apply Rmult_le_compat_r; [nra|exact Hrho].
    - exact Hr.
    - apply (rcl_rnd u Hu rnd rnd_rel). apply rcl_scale_const. exact H2.
    - apply (rcl_rnd u Hu rnd rnd_rel). apply rcl_mul;
        apply (rcl_rnd u Hu rnd rnd_rel); apply rcl_scale_const; exact H1.
    - exact HLo.
  Qed.

  (* ------------------------------------------------------------------- *)
  (* Part C.  The feedback theorem with a GENERAL error function phi and *)
  (* an INVARIANT of the true run.  phi need not be linear: the variance *)
  (* rule is not.  The radius iterates phi and is checked against the    *)
  (* range where phi is valid.                                           *)
  (* ------------------------------------------------------------------- *)

  Section General.
    Variable r : nat.
    Variables alpha alphah : (nat -> R) -> R.
    Variable phi : R -> R.
    Variable lmax : R.
    Variable Inv : (nat -> R) -> Prop.

    Hypothesis phi_nonneg : forall l, 0 <= l -> 0 <= phi l.
    Hypothesis alpha_stable_on : forall l mh m, 0 <= l -> l <= lmax -> Inv m ->
      (forall k, (1 <= k)%nat -> (k <= r)%nat -> rcl l (mh k) (m k)) ->
      rcl (phi l) (alphah mh) (alpha m).
    Hypothesis Inv_step : forall m, Inv m -> Inv (estep alpha m).

    Definition gstep (l : R) : R := l + INR r * (phi l + lam).

    Fixpoint grad (l : R) (T : nat) : R :=
      match T with O => l | S T' => grad (gstep l) T' end.

    Lemma gstep_ge : forall l, 0 <= l -> l <= gstep l.
    Proof.
      intros l Hl. unfold gstep. pose proof (phi_nonneg l Hl).
      pose proof (lu_nonneg u Hu). pose proof (pos_INR r).
      assert (0 <= INR r * (phi l + lam)) by (apply Rmult_le_pos; lra). lra.
    Qed.

    Lemma grad_ge : forall T l, 0 <= l -> l <= grad l T.
    Proof.
      induction T as [|T IH]; intros l Hl; cbn [grad]; [lra|].
      pose proof (gstep_ge l Hl).
      pose proof (IH (gstep l) ltac:(lra)). lra.
    Qed.

    Lemma gstep_rcl : forall l mh m, 0 <= l -> l <= lmax -> Inv m ->
      (forall k, (1 <= k)%nat -> (k <= r)%nat -> rcl l (mh k) (m k)) ->
      forall k, (1 <= k)%nat -> (k <= r)%nat ->
        rcl (gstep l) (fstep rnd alphah mh k) (estep alpha m k).
    Proof.
      intros l mh m Hl Hlm HI Hm k Hk1 Hkr. unfold fstep, estep, gstep.
      pose proof (alpha_stable_on l mh m Hl Hlm HI Hm) as Ha.
      pose proof (phi_nonneg l Hl) as Hp.
      pose proof (fpow_rcl u Hu rnd rnd_rel _ _ _ k Hk1 Hp Ha) as P.
      pose proof (rcl_rnd u Hu rnd rnd_rel _ _ _
                    (rcl_mul _ _ _ _ _ _ P (Hm k Hk1 Hkr))) as S1.
      eapply rcl_mono; [|exact S1].
      assert (Hk : INR k <= INR r) by (apply le_INR; exact Hkr).
      assert (Hk' : INR (k - 1) + 1 = INR k)
        by (rewrite <- S_INR; f_equal; lia).
      pose proof (lu_nonneg u Hu).
      assert (A : INR k * phi l <= INR r * phi l)
        by (apply Rmult_le_compat_r; lra).
      assert (B : INR k * lam <= INR r * lam)
        by (apply Rmult_le_compat_r; lra).
      nra.
    Qed.

    (* THE GENERAL FEEDBACK BOUND: valid as long as the radius stays in  *)
    (* the range where the coefficient's error rule holds.               *)
    Theorem feedback_bound_inv : forall T l0 mh m, 0 <= l0 -> Inv m ->
      grad l0 T <= lmax ->
      (forall k, (1 <= k)%nat -> (k <= r)%nat -> rcl l0 (mh k) (m k)) ->
      forall k, (1 <= k)%nat -> (k <= r)%nat ->
        rcl (grad l0 T) (fiter rnd alphah T mh k) (eiter alpha T m k).
    Proof.
      induction T as [|T IH]; intros l0 mh m Hl0 HI Hmax Hm k Hk1 Hkr.
      - exact (Hm k Hk1 Hkr).
      - cbn [fiter eiter grad] in *.
        assert (Hle : l0 <= lmax).
        { pose proof (grad_ge T (gstep l0)
                        (Rle_trans _ _ _ Hl0 (gstep_ge l0 Hl0))).
          pose proof (gstep_ge l0 Hl0). lra. }
        apply IH.
        + pose proof (gstep_ge l0 Hl0). lra.
        + apply Inv_step. exact HI.
        + exact Hmax.
        + apply gstep_rcl; assumption.
        + exact Hk1.
        + exact Hkr.
    Qed.

  End General.

  (* ------------------------------------------------------------------- *)
  (* Part D.  THE INSTANCE: an ensemble renormalized each step by its    *)
  (* own floating-point STANDARD DEVIATION, x' = (g / sd) x + b.         *)
  (* ------------------------------------------------------------------- *)

  Variables (N g rho : R).
  Hypothesis HN : 0 < N.
  Hypothesis Hg : g <> 0.
  Hypothesis Hrho : 0 <= rho < 1.

  Definition alpha_sd (m : nat -> R) : R := g * / sqrt (vex N m).
  Definition alphah_sd (mh : nat -> R) : R := rnd (g * / rnd (sqrt (vfl N mh))).

  (* rho = M_1^2 / (N M_2) is invariant under the exact affine step.     *)
  Definition Inv_sd (m : nat -> R) : Prop :=
    0 < m 2%nat /\ m 1%nat * m 1%nat <= rho * (N * m 2%nat).

  Definition phi_sd (l : R) : R := lam + (lam + DV rho l / 2).

  Lemma vex_pos : forall m, Inv_sd m -> 0 < vex N m.
  Proof.
    intros m [H2 H1]. unfold vex.
    assert (Hi : 0 < / N) by (apply Rinv_0_lt_compat; lra).
    replace (m 1%nat * / N * (m 1%nat * / N))
      with (m 1%nat * m 1%nat * (/ N * / N))
      by ring.
    assert (m 1%nat * m 1%nat * (/ N * / N)
            <= rho * (N * m 2%nat) * (/ N * / N))
      by (apply Rmult_le_compat_r; nra).
    replace (rho * (N * m 2%nat) * (/ N * / N)) with (rho * (m 2%nat * / N))
      in H by (field; lra).
    assert (0 < m 2%nat * / N) by (apply Rmult_lt_0_compat; lra).
    nra.
  Qed.

  Lemma Inv_sd_step : forall m, Inv_sd m -> Inv_sd (estep alpha_sd m).
  Proof.
    intros m HI. pose proof (vex_pos m HI) as Hv. destruct HI as [H2 H1].
    assert (Ha : alpha_sd m <> 0).
    { unfold alpha_sd.
      apply Rmult_integral_contrapositive_currified; [exact Hg|].
      apply Rinv_neq_0_compat. apply Rgt_not_eq. apply sqrt_lt_R0. exact Hv. }
    unfold Inv_sd, estep. set (a := alpha_sd m) in *.
    assert (Ha2 : 0 < a * a) by (apply Rsqr_pos_lt; exact Ha).
    split.
    - simpl. nra.
    - simpl. replace (a * 1 * m 1%nat * (a * 1 * m 1%nat))
        with ((a * a) * (m 1%nat * m 1%nat)) by ring.
      replace (rho * (N * (a * (a * 1) * m 2%nat)))
        with ((a * a) * (rho * (N * m 2%nat))) by ring.
      apply Rmult_le_compat_l; lra.
  Qed.

  (* The coefficient's error rule, valid while the difference rule is.   *)
  Lemma alpha_sd_stable : forall lmax l mh m, 0 <= l -> l <= lmax ->
    0 < Lo (lam + lmax) (lam + ((lam + lmax) + (lam + lmax))) rho ->
    Inv_sd m ->
    (forall k, (1 <= k)%nat -> (k <= 2)%nat -> rcl l (mh k) (m k)) ->
    rcl (phi_sd l) (alphah_sd mh) (alpha_sd m).
  Proof.
    intros lmax l mh m Hl Hlm HLo HI Hm.
    destruct HI as [H2 H1].
    pose proof (lu_nonneg u Hu) as Hlam.
    (* Lo is decreasing in l, so it stays positive below lmax            *)
    assert (HLo' : 0 < Lo (lam + l) (lam + ((lam + l) + (lam + l))) rho).
    { unfold Lo in *.
      assert (E1 : exp (- (lam + lmax)) <= exp (- (lam + l)))
        by (apply exp_le_mono; lra).
      assert (E2 : exp (lam + ((lam + l) + (lam + l)))
                   <= exp (lam + ((lam + lmax) + (lam + lmax))))
        by (apply exp_le_mono; lra).
      assert (0 < 1 - rho) by lra.
      assert (N1 : 0 < exp (- (lam + lmax))
                   - exp (lam + ((lam + lmax) + (lam + lmax))) * rho).
      { apply (Rmult_lt_reg_r (/ (1 - rho))); [apply Rinv_0_lt_compat; lra|].
        rewrite Rmult_0_l. exact HLo. }
      apply Rdiv_lt_0_compat; [|lra]. nra. }
    pose proof (variance_rcl N rho l mh m HN H2 H1 Hrho Hl
                  (Hm 1%nat ltac:(lia) ltac:(lia))
                  (Hm 2%nat ltac:(lia) ltac:(lia))
                  HLo') as V.
    pose proof (rcl_rnd u Hu rnd rnd_rel _ _ _ (rcl_sqrt _ _ _ V)) as S1.
    pose proof (rcl_inv _ _ _ S1) as S2.
    pose proof (rcl_mul 0 _ g g _ _ (rcl_refl g) S2) as S3.
    pose proof (rcl_rnd u Hu rnd rnd_rel _ _ _ S3) as S4.
    unfold alphah_sd, alpha_sd, phi_sd. eapply rcl_mono; [|exact S4]. lra.
  Qed.

  (* THE CONTRACT for renormalization by the float standard deviation:   *)
  (* the float reduced run against the TRUE particle system, whose       *)
  (* coefficient is g / (its true standard deviation).                   *)
  Theorem renormalized_by_stddev_bound :
    forall (beta : R -> (nat -> R) -> R) lmax l0 mh c xs T,
      0 < Lo (lam + lmax) (lam + ((lam + lmax) + (lam + lmax))) rho ->
      0 <= l0 ->
      Inv_sd (smv c xs) ->
      grad 2 phi_sd l0 T <= lmax ->
      (forall k, (1 <= k)%nat -> (k <= 2)%nat -> rcl l0 (mh k) (Smom c k xs)) ->
      forall k, (1 <= k)%nat -> (k <= 2)%nat ->
        rcl (grad 2 phi_sd l0 T) (fiter rnd alphah_sd T mh k)
            (Smom (fst (piter alpha_sd beta T (c, xs))) k
                  (snd (piter alpha_sd beta T (c, xs)))).
  Proof.
    intros beta lmax l0 mh c xs T HLo Hl0 HI Hmax Hm k Hk1 Hkr.
    change (Smom (fst (piter alpha_sd beta T (c, xs))) k
                 (snd (piter alpha_sd beta T (c, xs))))
      with (smv (fst (piter alpha_sd beta T (c, xs)))
                (snd (piter alpha_sd beta T (c, xs))) k).
    rewrite (eiter_is_particles 2 alpha_sd).
    - apply (feedback_bound_inv 2 alpha_sd alphah_sd phi_sd lmax Inv_sd).
      + intros l Hl. unfold phi_sd, DV. pose proof (lu_nonneg u Hu).
        assert (0 <= - ln (Lo (lam + l) (lam + ((lam + l) + (lam + l))) rho)).
        { apply neg_ln_nonneg. apply Lo_le_1; lra. }
        lra.
      + intros l mh1 m1 Hl Hlm HI1 Hm1.
        exact (alpha_sd_stable lmax l mh1 m1 Hl Hlm HLo HI1 Hm1).
      + exact Inv_sd_step.
      + exact Hl0.
      + exact HI.
      + exact Hmax.
      + exact Hm.
      + exact Hk1.
      + exact Hkr.
    - intros m m' H. unfold alpha_sd, vex.
      rewrite (H 1%nat), (H 2%nat) by lia. reflexivity.
  Qed.

End Variance.
