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
(* invariant under affine steps (BladeShiftedMoments.rho_invariant), and *)
(* typically at rounding level after a two-pass seed (an estimate, not a *)
(* theorem: Part F certifies the bound the program actually uses).       *)
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
(*                             deviation;                                *)
(*   variance_rcl_mixed,       Part E (audit 2026-10-15, RF-1): M_1      *)
(*   renormalized_by_stddev_mixed     seeded with an ABSOLUTE error,     *)
(*                             which enters the variance squared;        *)
(*   rho_certified_upper,      Part F (RF-4): the run-time rho, inflated *)
(*   stddev_admission_sound,   by exp(2 l1 + l2), is a certified upper   *)
(*   renormalized_by_stddev_admitted  bound on the TRUE rho, and the     *)
(*                             admission check implies Inv_sd and Lo > 0;*)
(*   vex_is_population_variance,      Part G (RF-5): with N the particle *)
(*   renormalized_particles_admitted  count, vex is the particles'       *)
(*                             population variance at every step.        *)
(*                                                                       *)
(* Scope, stated once.  The radius is computed by iterating the error    *)
(* rule (grad), not in closed form; blade plan evaluates it.  The bound  *)
(* needs Lo > 0 at the final radius: it fails, honestly, once the        *)
(* accumulated error is comparable to 1 - rho.  The radius is a          *)
(* conservative worst-case upper bound, not a proved cost.  Coefficients *)
(* reading the mean remain uncovered.  N is a free positive real in      *)
(* Parts B-E; Part G binds it to the particle count.                     *)
(* ===================================================================== *)

From Blade Require Import BladeBinomial BladeSummary BladeMomentClosure
  BladeShiftedMoments.
From BladeReals Require Import BladeShiftedFeedback.
From Stdlib Require Import Reals Lra List Lia.
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

Local Notation Psum := (psum R 0 1 Rplus Rmult).

(* ===================================================================== *)
(* Part E.  THE VARIANCE FROM AN ABSOLUTE M_1 ERROR (audit 2026-10-15,   *)
(* RF-1).  A two-pass seed need not give M_1 to any relative error: on   *)
(* [3, -1e16, 1e16] the true M_1 is 0 and binary64 computes 2            *)
(* (BladeShiftedFeedback.seed_no_relative_radius).  M_1 enters the       *)
(* variance only through (M_1 / N)^2, so an absolute error on it is      *)
(* harmless:                                                             *)
(*                                                                       *)
(*   rcl_diff_dominant_mixed   the dominant subtraction with an absolute *)
(*                             error |ez| <= delta y on the subtrahend:  *)
(*                             log-error -ln(Lo - delta / (1 - rho));    *)
(*   deltam, variance_rcl_mixed   M_1 seeded as mcl l (s sqrt(N M_2)):   *)
(*                             delta = e^(3 lu) (2 e^l sqrt(rho) s +     *)
(*                             s^2).  At an exact mean (rho = 0) only    *)
(*                             s^2 remains (deltam_exact_mean): the M_1  *)
(*                             error enters SQUARED;                     *)
(*   feedback_bound_rel        the feedback theorem over any             *)
(*                             radius-indexed relation;                  *)
(*   Rel_sd, Rel_sd_step,      the stddev instance with M_2 relative and *)
(*   renormalized_by_stddev_mixed   M_1 mixed: s grows only by the       *)
(*                             rounding factor, s(l) = s0 exp(l - l0),   *)
(*                             because s is measured in units of         *)
(*                             sqrt(N M_2), which the exact step scales  *)
(*                             like M_1.                                 *)
(* ===================================================================== *)

Theorem rcl_diff_dominant_mixed : forall ly lz rho delta yh y zh0 z ez,
  0 < y -> 0 <= z -> z <= rho * y -> 0 <= rho < 1 -> 0 <= delta ->
  rcl ly yh y -> rcl lz zh0 z -> Rabs ez <= delta * y ->
  0 < Lo ly lz rho - delta / (1 - rho) ->
  rcl (- ln (Lo ly lz rho - delta / (1 - rho))) (yh - (zh0 + ez)) (y - z).
Proof.
  intros ly lz rho delta yh y zh0 z ez Hy Hz0 Hzr Hrho Hd Ry Rz He HL.
  pose proof (rcl_nonneg _ _ _ Ry) as Nly.
  pose proof (rcl_nonneg _ _ _ Rz) as Nlz.
  pose proof (Lo_le_1 ly lz rho Nly Nlz Hrho) as HL1.
  set (x := delta / (1 - rho)) in *.
  assert (Hx : 0 <= x).
  { unfold x. apply Rmult_le_pos; [lra|]. left. apply Rinv_0_lt_compat. lra. }
  set (a := Lo ly lz rho) in *.
  assert (Ha : 0 < a) by lra.
  destruct (rcl_diff_dominant ly lz rho yh y zh0 z Hy Hz0 Hzr Hrho Ry Rz Ha)
    as [th [E [B1 B2]]].
  fold a in B1, B2.
  rewrite Ropp_involutive, exp_ln in B1 by exact Ha.
  rewrite exp_Ropp, exp_ln in B2 by exact Ha.
  assert (Hyz : (1 - rho) * y <= y - z) by lra.
  assert (Hyz0 : 0 < y - z) by nra.
  assert (Hez : Rabs ez <= x * (y - z)).
  { unfold x. apply (Rmult_le_reg_r (1 - rho)); [lra|].
    replace (delta / (1 - rho) * (y - z) * (1 - rho)) with (delta * (y - z))
      by (field; lra).
    assert (delta * ((1 - rho) * y) <= delta * (y - z))
      by (apply Rmult_le_compat_l; lra).
    assert (Rabs ez * (1 - rho) <= delta * y * (1 - rho))
      by (apply Rmult_le_compat_r; lra).
    nra. }
  assert (Hq : - x <= ez / (y - z) <= x).
  { pose proof (Rle_abs ez) as P1. pose proof (Rle_abs (- ez)) as P2.
    rewrite Rabs_Ropp in P2.
    split; apply (Rmult_le_reg_r (y - z)); try lra;
      unfold Rdiv; rewrite Rmult_assoc, Rinv_l by lra; lra. }
  exists (th - ez / (y - z)). split.
  { replace (yh - (zh0 + ez)) with ((yh - zh0) - ez) by ring.
    rewrite E. field. lra. }
  rewrite Ropp_involutive, exp_ln by exact HL.
  rewrite exp_Ropp, exp_ln by exact HL.
  split; [lra|].
  assert (Key : (/ a + x) * (a - x) <= 1).
  { replace ((/ a + x) * (a - x)) with (1 - x * (/ a - a + x)) by (field; lra).
    assert (1 <= / a) by (rewrite <- Rinv_1; apply Rinv_le_contravar; lra).
    assert (0 <= x * (/ a - a + x)) by (apply Rmult_le_pos; lra).
    lra. }
  apply (Rmult_le_reg_r (a - x)); [exact HL|]. rewrite Rinv_l by lra.
  assert ((th - ez / (y - z)) * (a - x) <= (/ a + x) * (a - x))
    by (apply Rmult_le_compat_r; lra).
  lra.
Qed.

Section VarianceMixed.
  Variable u : R.
  Hypothesis Hu : 0 <= u < 1.
  Variable rnd : R -> R.
  Hypothesis rnd_rel : forall x, Rabs (rnd x - x) <= u * Rabs x.

  Local Notation lam := (lu u).

  Definition deltam (rho l s : R) : R :=
    exp (3 * lam) * (2 * exp l * sqrt rho * s + s * s).

  Definition DVm (rho l s : R) : R :=
    lam + - ln (Lo (lam + l) (lam + ((lam + l) + (lam + l))) rho
                - deltam rho l s / (1 - rho)).

  Lemma deltam_nonneg : forall rho l s, 0 <= s -> 0 <= deltam rho l s.
  Proof.
    intros rho l s Hs. unfold deltam.
    apply Rmult_le_pos; [left; apply exp_pos|].
    pose proof (sqrt_pos rho). pose proof (exp_pos l).
    assert (0 <= 2 * exp l * sqrt rho * s)
      by (repeat apply Rmult_le_pos; lra).
    nra.
  Qed.

  Lemma deltam_exact_mean : forall l s, deltam 0 l s = exp (3 * lam) * (s * s).
  Proof. intros l s. unfold deltam. rewrite sqrt_0. ring. Qed.

  Theorem variance_rcl_mixed : forall N rho l s mh m,
    0 < N -> 0 < m 2%nat -> m 1%nat * m 1%nat <= rho * (N * m 2%nat) ->
    0 <= rho < 1 -> 0 <= l -> 0 <= s ->
    mcl l (s * sqrt (N * m 2%nat)) (mh 1%nat) (m 1%nat) ->
    rcl l (mh 2%nat) (m 2%nat) ->
    0 < Lo (lam + l) (lam + ((lam + l) + (lam + l))) rho
        - deltam rho l s / (1 - rho) ->
    rcl (DVm rho l s) (vfl rnd N mh) (vex N m).
  Proof.
    intros N rho l s mh m HN Hm2 Hrho Hr Hl Hs [t1 [e1 [E1 [B1 D1]]]] H2 HLo.
    unfold vfl, vex, DVm.
    apply (rcl_rnd u Hu rnd rnd_rel).
    destruct (rnd_rcl u Hu rnd rnd_rel (mh 1%nat * / N)) as [tq [Eq Bq]].
    destruct (rnd_rcl u Hu rnd rnd_rel
                (rnd (mh 1%nat * / N) * rnd (mh 1%nat * / N))) as [tr [Er Br]].
    set (z := m 1%nat * / N * (m 1%nat * / N)).
    set (zh0 := tr * tq * tq * t1 * t1 * z).
    set (ez := tr * tq * tq * (2 * t1 * m 1%nat * e1 + e1 * e1) * (/ N * / N)).
    assert (Ez : rnd (rnd (mh 1%nat * / N) * rnd (mh 1%nat * / N)) = zh0 + ez).
    { rewrite Er, Eq, E1. unfold zh0, ez, z. field. lra. }
    rewrite Ez.
    assert (HNi : 0 < / N) by (apply Rinv_0_lt_compat; lra).
    pose proof (exp_pos (- lam)) as Pe.
    apply rcl_diff_dominant_mixed.
    - apply Rmult_lt_0_compat; lra.
    - unfold z. nra.
    - unfold z.
      replace (m 1%nat * / N * (m 1%nat * / N))
        with (m 1%nat * m 1%nat * (/ N * / N)) by ring.
      replace (rho * (m 2%nat * / N)) with (rho * (N * m 2%nat) * (/ N * / N))
        by (field; lra).
      apply Rmult_le_compat_r; [nra|exact Hrho].
    - exact Hr.
    - apply deltam_nonneg. exact Hs.
    - apply (rcl_rnd u Hu rnd rnd_rel). apply rcl_scale_const. exact H2.
    - exists (tr * tq * tq * t1 * t1). split; [reflexivity|].
      replace (tr * tq * tq * t1 * t1) with (tr * ((tq * t1) * (tq * t1)))
        by ring.
      apply factor_mul; [exact Br|]. apply factor_mul; apply factor_mul; assumption.
    - (* the absolute part of the square, against y *)
      set (K := tr * tq * tq).
      set (W := 2 * t1 * m 1%nat * e1 + e1 * e1).
      assert (Eez : ez = K * W * (/ N * / N)) by (unfold ez, K, W; ring).
      assert (Htr : 0 < tr) by lra. assert (Htq : 0 < tq) by lra.
      assert (HK0 : 0 < K) by (unfold K; repeat apply Rmult_lt_0_compat; lra).
      assert (HK : K <= exp (3 * lam)).
      { unfold K. replace (3 * lam) with (lam + lam + lam) by ring.
        rewrite !exp_plus.
        apply Rmult_le_compat; [nra|lra| |lra].
        apply Rmult_le_compat; lra. }
      set (S := sqrt (N * m 2%nat)) in *.
      set (SR := sqrt rho).
      assert (HS0 : 0 <= S) by apply sqrt_pos.
      assert (HSR : 0 <= SR) by apply sqrt_pos.
      assert (HSS : S * S = N * m 2%nat) by (apply sqrt_sqrt; nra).
      assert (HRR : SR * SR = rho) by (apply sqrt_sqrt; lra).
      assert (Hm1 : Rabs (m 1%nat) <= SR * S).
      { pose proof (Rsqr_abs (m 1%nat)) as Q. unfold Rsqr in Q.
        pose proof (Rabs_pos (m 1%nat)) as P0.
        assert (E2 : SR * S * (SR * S) = rho * (N * m 2%nat)).
        { replace (SR * S * (SR * S)) with ((SR * SR) * (S * S)) by ring.
          rewrite HRR, HSS. reflexivity. }
        assert (P1 : 0 <= SR * S) by (apply Rmult_le_pos; lra).
        destruct (Rle_or_lt (Rabs (m 1%nat)) (SR * S)) as [Hle|Hlt];
          [exact Hle|exfalso]. nra. }
      assert (Ht1 : 0 < t1 <= exp l)
        by (pose proof (exp_pos (- l)); lra).
      assert (HW : Rabs W <= (2 * exp l * SR * s + s * s) * (N * m 2%nat)).
      { unfold W. eapply Rle_trans; [apply Rabs_triang|].
        rewrite !Rabs_mult, (Rabs_right 2) by lra.
        rewrite (Rabs_right t1) by lra.
        rewrite <- HSS.
        pose proof (Rabs_pos (m 1%nat)). pose proof (Rabs_pos e1).
        assert (A1 : t1 * Rabs (m 1%nat) <= exp l * (SR * S))
          by (apply Rmult_le_compat; lra).
        assert (A2 : t1 * Rabs (m 1%nat) * Rabs e1
                     <= exp l * (SR * S) * (s * S))
          by (apply Rmult_le_compat; [nra|lra|exact A1|exact D1]).
        assert (A3 : Rabs e1 * Rabs e1 <= (s * S) * (s * S))
          by (apply Rmult_le_compat; lra).
        nra. }
      rewrite Eez, Rabs_mult, Rabs_mult, (Rabs_right K) by lra.
      rewrite (Rabs_right (/ N * / N)) by nra.
      assert (P : K * Rabs W
                  <= exp (3 * lam) * ((2 * exp l * SR * s + s * s) * (N * m 2%nat))).
      { apply Rmult_le_compat; [lra|apply Rabs_pos|exact HK|exact HW]. }
      replace (deltam rho l s * (m 2%nat * / N))
        with (exp (3 * lam) * ((2 * exp l * SR * s + s * s) * (N * m 2%nat))
              * (/ N * / N)) by (unfold deltam, SR; field; lra).
      apply Rmult_le_compat_r; [nra|exact P].
    - exact HLo.
  Qed.

  (* ------------------------------------------------------------------- *)
  (* The feedback theorem over an arbitrary radius-indexed relation.     *)
  (* ------------------------------------------------------------------- *)

  Section FeedbackRel.
    Variable r : nat.
    Variables alpha alphah : (nat -> R) -> R.
    Variable phi : R -> R.
    Variable lmax : R.
    Variable Inv : (nat -> R) -> Prop.
    Variable Rel : R -> (nat -> R) -> (nat -> R) -> Prop.
    Hypothesis phi_nonneg : forall l, 0 <= l -> 0 <= phi l.
    Hypothesis Rel_step : forall l mh m, 0 <= l -> l <= lmax -> Inv m ->
      Rel l mh m -> Rel (gstep u r phi l) (fstep rnd alphah mh) (estep alpha m).
    Hypothesis Inv_step : forall m, Inv m -> Inv (estep alpha m).

    Theorem feedback_bound_rel : forall T l0 mh m, 0 <= l0 -> Inv m ->
      grad u r phi l0 T <= lmax -> Rel l0 mh m ->
      Rel (grad u r phi l0 T) (fiter rnd alphah T mh) (eiter alpha T m).
    Proof.
      induction T as [|T IH]; intros l0 mh m Hl0 HI Hmax HR.
      - exact HR.
      - cbn [fiter eiter grad] in *.
        pose proof (gstep_ge u Hu r phi phi_nonneg l0 Hl0) as G1.
        pose proof (grad_ge u Hu r phi phi_nonneg T (gstep u r phi l0)
                      ltac:(lra)) as G2.
        apply IH; [lra| |exact Hmax|].
        + apply Inv_step. exact HI.
        + apply Rel_step; [exact Hl0|lra|exact HI|exact HR].
    Qed.
  End FeedbackRel.

  (* ------------------------------------------------------------------- *)
  (* The stddev instance with an ABSOLUTE M_1 seed error.                *)
  (* ------------------------------------------------------------------- *)

  (* M_2 at log-error l, M_1 at log-error l plus an absolute error       *)
  (* sig(l) sqrt(N M_2).                                                 *)
  Section StddevMixed.
    Variables (N g rho s0 l0 : R).
    Hypothesis HN : 0 < N.
    Hypothesis Hg : g <> 0.
    Hypothesis Hrho : 0 <= rho < 1.
    Hypothesis Hs0 : 0 <= s0.

    Definition sig (l : R) : R := s0 * exp (l - l0).

    Definition phi_m (l : R) : R := lam + (lam + DVm rho l (sig l) / 2).

    Definition Rel_sd (l : R) (mh m : nat -> R) : Prop :=
      rcl l (mh 2%nat) (m 2%nat)
      /\ mcl l (sig l * sqrt (N * m 2%nat)) (mh 1%nat) (m 1%nat).

    Lemma sig_nonneg : forall l, 0 <= sig l.
    Proof.
      intro l. unfold sig. apply Rmult_le_pos; [lra|]. left. apply exp_pos.
    Qed.

    Lemma phi_m_nonneg : forall l, 0 <= l -> 0 <= phi_m l.
    Proof.
      intros l Hl. unfold phi_m, DVm. pose proof (lu_nonneg u Hu).
      assert (0 <= - ln (Lo (lam + l) (lam + ((lam + l) + (lam + l))) rho
                         - deltam rho l (sig l) / (1 - rho))).
      { apply neg_ln_nonneg.
        pose proof (Lo_le_1 (lam + l) (lam + ((lam + l) + (lam + l))) rho
                      ltac:(lra) ltac:(lra) Hrho).
        assert (0 <= deltam rho l (sig l) / (1 - rho)).
        { apply Rmult_le_pos; [apply deltam_nonneg, sig_nonneg|].
          left. apply Rinv_0_lt_compat. lra. }
        lra. }
      lra.
    Qed.

    Lemma Lom_mono : forall l lmax, 0 <= l -> l <= lmax ->
      0 < Lo (lam + lmax) (lam + ((lam + lmax) + (lam + lmax))) rho
          - deltam rho lmax (sig lmax) / (1 - rho) ->
      0 < Lo (lam + l) (lam + ((lam + l) + (lam + l))) rho
          - deltam rho l (sig l) / (1 - rho).
    Proof.
      intros l lmax Hl Hlm H.
      assert (Hq : forall a b, a <= b -> a / (1 - rho) <= b / (1 - rho)).
      { intros a b Hab. apply Rmult_le_compat_r; [|exact Hab].
        left. apply Rinv_0_lt_compat. lra. }
      assert (E1 : exp (- (lam + lmax)) <= exp (- (lam + l)))
        by (apply exp_le_mono; lra).
      assert (E2 : exp (lam + ((lam + l) + (lam + l)))
                   <= exp (lam + ((lam + lmax) + (lam + lmax))))
        by (apply exp_le_mono; lra).
      assert (HLo : Lo (lam + lmax) (lam + ((lam + lmax) + (lam + lmax))) rho
                    <= Lo (lam + l) (lam + ((lam + l) + (lam + l))) rho).
      { unfold Lo. apply Hq. nra. }
      assert (HD : deltam rho l (sig l) <= deltam rho lmax (sig lmax)).
      { unfold deltam, sig. apply Rmult_le_compat_l; [left; apply exp_pos|].
        pose proof (sqrt_pos rho).
        assert (X1 : exp l <= exp lmax) by (apply exp_le_mono; lra).
        assert (X2 : exp (l - l0) <= exp (lmax - l0)) by (apply exp_le_mono; lra).
        pose proof (exp_pos l). pose proof (exp_pos (l - l0)).
        assert (X3 : s0 * exp (l - l0) <= s0 * exp (lmax - l0))
          by (apply Rmult_le_compat_l; lra).
        assert (0 <= s0 * exp (l - l0)) by (apply Rmult_le_pos; lra).
        assert (X4 : exp l * sqrt rho <= exp lmax * sqrt rho)
          by (apply Rmult_le_compat_r; lra).
        assert (0 <= exp l * sqrt rho) by (apply Rmult_le_pos; lra).
        assert (X5 : exp l * sqrt rho * (s0 * exp (l - l0))
                     <= exp lmax * sqrt rho * (s0 * exp (lmax - l0)))
          by (apply Rmult_le_compat; lra).
        assert (X6 : s0 * exp (l - l0) * (s0 * exp (l - l0))
                     <= s0 * exp (lmax - l0) * (s0 * exp (lmax - l0)))
          by (apply Rmult_le_compat; lra).
        nra. }
      pose proof (Hq _ _ HD). lra.
    Qed.

    Lemma alpha_sd_stable_mixed : forall lmax l mh m, 0 <= l -> l <= lmax ->
      0 < Lo (lam + lmax) (lam + ((lam + lmax) + (lam + lmax))) rho
          - deltam rho lmax (sig lmax) / (1 - rho) ->
      Inv_sd N rho m -> Rel_sd l mh m ->
      rcl (phi_m l) (alphah_sd rnd N g mh) (alpha_sd N g m).
    Proof.
      intros lmax l mh m Hl Hlm HLo [H2 H1] [R2 M1].
      pose proof (Lom_mono l lmax Hl Hlm HLo) as HL.
      pose proof (variance_rcl_mixed N rho l (sig l) mh m HN H2 H1 Hrho Hl
                    (sig_nonneg l) M1 R2 HL) as V.
      pose proof (rcl_rnd u Hu rnd rnd_rel _ _ _ (rcl_sqrt _ _ _ V)) as S1.
      pose proof (rcl_inv _ _ _ S1) as S2.
      pose proof (rcl_mul 0 _ g g _ _ (rcl_refl g) S2) as S3.
      pose proof (rcl_rnd u Hu rnd rnd_rel _ _ _ S3) as S4.
      unfold alphah_sd, alpha_sd, phi_m. eapply rcl_mono; [|exact S4]. lra.
    Qed.

    Lemma Rel_sd_step : forall lmax l mh m, 0 <= l -> l <= lmax ->
      0 < Lo (lam + lmax) (lam + ((lam + lmax) + (lam + lmax))) rho
          - deltam rho lmax (sig lmax) / (1 - rho) ->
      Inv_sd N rho m -> Rel_sd l mh m ->
      Rel_sd (gstep u 2 phi_m l) (fstep rnd (alphah_sd rnd N g) mh)
             (estep (alpha_sd N g) m).
    Proof.
      intros lmax l mh m Hl Hlm HLo HI HR.
      pose proof (alpha_sd_stable_mixed lmax l mh m Hl Hlm HLo HI HR) as Ha.
      destruct HR as [R2 M1].
      pose proof (phi_m_nonneg l Hl) as Hp.
      pose proof (lu_nonneg u Hu) as Hlam.
      assert (E2 : INR 2 = 2) by (simpl; ring).
      assert (E1 : INR 1 = 1) by reflexivity.
      split.
      - unfold fstep, estep.
        pose proof (fpow_rcl u Hu rnd rnd_rel _ _ _ 2 ltac:(lia) Hp Ha) as P.
        pose proof (rcl_rnd u Hu rnd rnd_rel _ _ _ (rcl_mul _ _ _ _ _ _ P R2))
          as S1.
        eapply rcl_mono; [|exact S1]. unfold gstep.
        replace (INR (2 - 1)) with 1 by (simpl; ring). rewrite E2. lra.
      - pose proof (mcl_step u Hu rnd rnd_rel _ _ _ _ _ _ _ 1 (le_n 1) Hp M1 Ha)
          as S1.
        unfold fstep, estep. rewrite E1, !Rmult_1_l in S1.
        eapply mcl_mono; [| |exact S1].
        + unfold gstep. rewrite E2. lra.
        + set (a := alpha_sd N g m) in *.
          assert (Hsq : sqrt (N * (a ^ 2 * m 2%nat))
                        = Rabs a * sqrt (N * m 2%nat)).
          { replace (N * (a ^ 2 * m 2%nat)) with (Rsqr a * (N * m 2%nat))
              by (unfold Rsqr; ring).
            rewrite sqrt_mult_alt by apply Rle_0_sqr.
            rewrite sqrt_Rsqr_abs. reflexivity. }
          rewrite Hsq, pow_1.
          unfold sig, gstep. rewrite E2.
          replace (l + 2 * (phi_m l + lam) - l0)
            with ((phi_m l + lam) + (l - l0 + (phi_m l + lam))) by ring.
          rewrite (exp_plus (phi_m l + lam)).
          pose proof (Rabs_pos a). pose proof (sqrt_pos (N * m 2%nat)).
          assert (X : exp (l - l0) <= exp (l - l0 + (phi_m l + lam)))
            by (apply exp_le_mono; lra).
          assert (X2 : s0 * exp (l - l0) <= s0 * exp (l - l0 + (phi_m l + lam)))
            by (apply Rmult_le_compat_l; lra).
          assert (X3 : 0 <= Rabs a * sqrt (N * m 2%nat))
            by (apply Rmult_le_pos; lra).
          assert (X4 : s0 * exp (l - l0) * (Rabs a * sqrt (N * m 2%nat))
                       <= s0 * exp (l - l0 + (phi_m l + lam))
                          * (Rabs a * sqrt (N * m 2%nat)))
            by (apply Rmult_le_compat_r; lra).
          pose proof (exp_pos (phi_m l + lam)).
          assert (X5 : exp (phi_m l + lam)
                       * (s0 * exp (l - l0) * (Rabs a * sqrt (N * m 2%nat)))
                       <= exp (phi_m l + lam)
                          * (s0 * exp (l - l0 + (phi_m l + lam))
                             * (Rabs a * sqrt (N * m 2%nat))))
            by (apply Rmult_le_compat_l; lra).
          replace (exp (phi_m l + lam) * (Rabs a * (s0 * exp (l - l0)
                     * sqrt (N * m 2%nat))))
            with (exp (phi_m l + lam)
                  * (s0 * exp (l - l0) * (Rabs a * sqrt (N * m 2%nat))))
            by ring.
          replace (s0 * (exp (phi_m l + lam) * exp (l - l0 + (phi_m l + lam)))
                   * (Rabs a * sqrt (N * m 2%nat)))
            with (exp (phi_m l + lam)
                  * (s0 * exp (l - l0 + (phi_m l + lam))
                     * (Rabs a * sqrt (N * m 2%nat))))
            by ring.
          exact X5.
    Qed.

    (* THE CONTRACT FROM A MIXED SEED: M_2 relative, M_1 relative plus   *)
    (* an absolute error s0 sqrt(N M_2) -- the two-pass seed's shape.    *)
    Theorem renormalized_by_stddev_mixed :
      forall (beta : R -> (nat -> R) -> R) lmax mh c xs T,
        0 < Lo (lam + lmax) (lam + ((lam + lmax) + (lam + lmax))) rho
            - deltam rho lmax (sig lmax) / (1 - rho) ->
        0 <= l0 ->
        Inv_sd N rho (smv c xs) ->
        grad u 2 phi_m l0 T <= lmax ->
        rcl l0 (mh 2%nat) (Smom c 2 xs) ->
        mcl l0 (s0 * sqrt (N * Smom c 2 xs)) (mh 1%nat) (Smom c 1 xs) ->
        let st := piter (alpha_sd N g) beta T (c, xs) in
        rcl (grad u 2 phi_m l0 T) (fiter rnd (alphah_sd rnd N g) T mh 2%nat)
            (Smom (fst st) 2 (snd st))
        /\ mcl (grad u 2 phi_m l0 T)
               (sig (grad u 2 phi_m l0 T) * sqrt (N * Smom (fst st) 2 (snd st)))
               (fiter rnd (alphah_sd rnd N g) T mh 1%nat)
               (Smom (fst st) 1 (snd st)).
    Proof.
      intros beta lmax mh c xs T HLo Hl0 HI Hmax R2 M1 st.
      assert (HR : Rel_sd l0 mh (smv c xs)).
      { split; [exact R2|]. unfold sig. rewrite Rminus_diag, exp_0, Rmult_1_r.
        exact M1. }
      pose proof (feedback_bound_rel 2 (alpha_sd N g) (alphah_sd rnd N g) phi_m
                    lmax (Inv_sd N rho) Rel_sd phi_m_nonneg
                    (fun l mh m Hl Hlm HI HR => Rel_sd_step lmax l mh m Hl Hlm HLo HI HR)
                    (Inv_sd_step N g rho HN Hg Hrho)
                    T l0 mh (smv c xs) Hl0 HI Hmax HR) as [A B].
      assert (Ep : forall k, Smom (fst st) k (snd st)
                             = eiter (alpha_sd N g) T (smv c xs) k).
      { intro k. apply (eiter_is_particles 2 (alpha_sd N g)).
        intros m m' H. unfold alpha_sd, vex.
        rewrite (H 1%nat), (H 2%nat) by lia. reflexivity. }
      rewrite !Ep. split; assumption.
    Qed.

  End StddevMixed.

End VarianceMixed.

(* The obstruction seed, for the variance: the true M_1 about the         *)
(* computed shift is 0, so the true rho is 0 and the M_1 error enters    *)
(* squared (deltam_exact_mean).                                          *)
Example seed_witness_rho_zero : Inv_sd 3 0 (smv 1 seed_xs).
Proof.
  unfold Inv_sd, smv. rewrite seed_true_first_moment_zero. split; [|lra].
  unfold seed_xs. cbn [smom psum rpow map rsum]. nra.
Qed.

(* ===================================================================== *)
(* Part F.  RHO ADMISSION AT RUN TIME (audit RF-4).  The theorems need   *)
(* M_1^2 <= rho N M_2 for the TRUE moments.  The program only has the    *)
(* float seed, and the raw ratio Mh_1^2 / (N Mh_2) can UNDERESTIMATE the *)
(* true one (uninflated_rho_fails_true_invariant).  Inflated, it cannot: *)
(*                                                                       *)
(*   rho_certified_upper       from Mh_1 = th M_1 + e (log-error l1,     *)
(*                             |e| <= D) and Mh_2 at log-error l2:       *)
(*                             true rho <= exp(2 l1 + l2)                *)
(*                             (|Mh_1| + D)^2 / (N Mh_2);                *)
(*   rho_certified_upper_rel   D = 0, l1 = l2 = l: the factor exp(3 l);  *)
(*   stddev_admission_sound    a run-time check on float data -- Mh_2 >  *)
(*                             0, the inflated ratio <= rhoc < 1, Lo > 0 *)
(*                             at rhoc -- implies Inv_sd for the TRUE    *)
(*                             ensemble and the positive-Lo premise;     *)
(*   renormalized_by_stddev_admitted,  the contract under that check;    *)
(*   renormalized_by_stddev_mixed_admitted   the same from a MIXED M_1   *)
(*                             seed, the check also bounding D by s0.    *)
(*                                                                       *)
(* rhoc may be any value >= the inflated ratio, so a ratio rounded       *)
(* OUTWARD is admitted; a raw float ratio is not.                        *)
(* ===================================================================== *)

Lemma rho_certified_upper : forall l1 l2 D N mh1 m1 mh2 m2,
  0 < N -> 0 < mh2 -> mcl l1 D mh1 m1 -> rcl l2 mh2 m2 ->
  0 < m2
  /\ m1 * m1 <= exp (2 * l1 + l2)
                * ((Rabs mh1 + D) * (Rabs mh1 + D) / (N * mh2)) * (N * m2).
Proof.
  intros l1 l2 D N mh1 m1 mh2 m2 HN Hmh2 [t1 [e [E1 [B1 D1]]]] [t2 [E2 B2]].
  pose proof (exp_pos (- l1)) as P1. pose proof (exp_pos (- l2)) as P2.
  assert (Ht1 : 0 < t1) by lra. assert (Ht2 : 0 < t2) by lra.
  assert (Hm2 : 0 < m2).
  { rewrite E2 in Hmh2. destruct (Rlt_or_le 0 m2) as [H|H]; [exact H|].
    assert (t2 * m2 <= 0) by nra. lra. }
  split; [exact Hm2|].
  assert (Hinv : 1 <= exp l1 * t1).
  { pose proof (exp_inv_pair l1). pose proof (exp_pos l1).
    assert (exp l1 * exp (- l1) <= exp l1 * t1)
      by (apply Rmult_le_compat_l; lra). lra. }
  assert (Hm1 : Rabs m1 <= exp l1 * (Rabs mh1 + D)).
  { assert (Hm1t : Rabs m1 * t1 <= Rabs mh1 + D).
    { replace (Rabs m1 * t1) with (Rabs (mh1 - e)).
      - pose proof (Rabs_triang mh1 (- e)). rewrite Rabs_Ropp in H.
        unfold Rminus. lra.
      - rewrite E1. replace (t1 * m1 + e - e) with (m1 * t1) by ring.
        rewrite Rabs_mult, (Rabs_right t1) by lra. reflexivity. }
    pose proof (Rabs_pos m1) as Pm. pose proof (exp_pos l1) as Pe.
    assert (Rabs m1 * 1 <= Rabs m1 * (exp l1 * t1))
      by (apply Rmult_le_compat_l; lra).
    assert (exp l1 * (Rabs m1 * t1) <= exp l1 * (Rabs mh1 + D))
      by (apply Rmult_le_compat_l; lra).
    nra. }
  assert (Hm2b : mh2 <= exp l2 * m2)
    by (rewrite E2; apply Rmult_le_compat_r; lra).
  set (A := exp l1 * (Rabs mh1 + D)) in *.
  assert (HA : Rabs m1 * Rabs m1 <= A * A).
  { pose proof (Rabs_pos m1). apply Rmult_le_compat; lra. }
  pose proof (Rsqr_abs m1) as Q. unfold Rsqr in Q.
  replace (exp (2 * l1 + l2) * ((Rabs mh1 + D) * (Rabs mh1 + D) / (N * mh2))
           * (N * m2))
    with (A * A * (exp l2 * m2 / mh2))
    by (unfold A; replace (2 * l1 + l2) with (l1 + l1 + l2) by ring;
        rewrite !exp_plus; field; lra).
  assert (1 <= exp l2 * m2 / mh2).
  { apply (Rmult_le_reg_r mh2); [lra|]. unfold Rdiv.
    rewrite Rmult_assoc, Rinv_l by lra. lra. }
  assert (0 <= A * A) by nra.
  nra.
Qed.

Corollary rho_certified_upper_rel : forall l N mh1 m1 mh2 m2,
  0 < N -> 0 < mh2 -> rcl l mh1 m1 -> rcl l mh2 m2 ->
  0 < m2 /\ m1 * m1 <= exp (3 * l) * (mh1 * mh1 / (N * mh2)) * (N * m2).
Proof.
  intros l N mh1 m1 mh2 m2 HN Hmh2 H1 H2.
  destruct (rho_certified_upper l l 0 N mh1 m1 mh2 m2 HN Hmh2 (rcl_mcl _ _ _ H1) H2)
    as [Hm2 B].
  split; [exact Hm2|].
  replace (exp (3 * l) * (mh1 * mh1 / (N * mh2)))
    with (exp (2 * l + l) * ((Rabs mh1 + 0) * (Rabs mh1 + 0) / (N * mh2))).
  - exact B.
  - rewrite Rplus_0_r. pose proof (Rsqr_abs mh1) as Q. unfold Rsqr in Q.
    rewrite <- Q. f_equal. f_equal. ring.
Qed.

Theorem stddev_admission_sound : forall u N l0 lmax rhoc mh c xs,
  0 < N -> rcl l0 (mh 1%nat) (Smom c 1 xs) -> rcl l0 (mh 2%nat) (Smom c 2 xs) ->
  0 < mh 2%nat ->
  exp (3 * l0) * (mh 1%nat * mh 1%nat / (N * mh 2%nat)) <= rhoc ->
  rhoc < 1 ->
  0 < Lo (lu u + lmax) (lu u + ((lu u + lmax) + (lu u + lmax))) rhoc ->
  Inv_sd N rhoc (smv c xs) /\ 0 <= rhoc < 1
  /\ 0 < Lo (lu u + lmax) (lu u + ((lu u + lmax) + (lu u + lmax))) rhoc.
Proof.
  intros u N l0 lmax rhoc mh c xs HN H1 H2 Hmh Hchk Hlt HLo.
  destruct (rho_certified_upper_rel l0 N _ _ _ _ HN Hmh H1 H2) as [Hm2 B].
  assert (Hr0 : 0 <= exp (3 * l0) * (mh 1%nat * mh 1%nat / (N * mh 2%nat))).
  { apply Rmult_le_pos; [left; apply exp_pos|].
    apply Rmult_le_pos; [nra|]. left. apply Rinv_0_lt_compat. nra. }
  split; [|split; [lra|exact HLo]].
  split; [exact Hm2|].
  assert (0 < N * Smom c 2 xs) by nra.
  assert (exp (3 * l0) * (mh 1%nat * mh 1%nat / (N * mh 2%nat)) * (N * Smom c 2 xs)
          <= rhoc * (N * Smom c 2 xs)) by (apply Rmult_le_compat_r; lra).
  unfold smv. lra.
Qed.

Theorem renormalized_by_stddev_admitted :
  forall u rnd N g (beta : R -> (nat -> R) -> R) lmax l0 rhoc mh c xs T,
    0 <= u < 1 -> (forall x, Rabs (rnd x - x) <= u * Rabs x) ->
    0 < N -> g <> 0 -> 0 <= l0 ->
    rcl l0 (mh 1%nat) (Smom c 1 xs) -> rcl l0 (mh 2%nat) (Smom c 2 xs) ->
    0 < mh 2%nat ->
    exp (3 * l0) * (mh 1%nat * mh 1%nat / (N * mh 2%nat)) <= rhoc ->
    rhoc < 1 ->
    0 < Lo (lu u + lmax) (lu u + ((lu u + lmax) + (lu u + lmax))) rhoc ->
    grad u 2 (phi_sd u rhoc) l0 T <= lmax ->
    forall k, (1 <= k)%nat -> (k <= 2)%nat ->
      rcl (grad u 2 (phi_sd u rhoc) l0 T) (fiter rnd (alphah_sd rnd N g) T mh k)
          (Smom (fst (piter (alpha_sd N g) beta T (c, xs))) k
                (snd (piter (alpha_sd N g) beta T (c, xs)))).
Proof.
  intros u rnd N g beta lmax l0 rhoc mh c xs T Hu Hrnd HN Hg Hl0 H1 H2 Hmh
    Hchk Hlt HLo Hmax k Hk1 Hkr.
  destruct (stddev_admission_sound u N l0 lmax rhoc mh c xs HN H1 H2 Hmh Hchk
              Hlt HLo) as [HI [Hr _]].
  apply (renormalized_by_stddev_bound u Hu rnd Hrnd N g rhoc HN Hg Hr beta lmax
           l0 mh c xs T HLo Hl0 HI Hmax); [|exact Hk1|exact Hkr].
  intros j Hj1 Hj2. destruct j as [|[|[|j]]]; [lia|exact H1|exact H2|lia].
Qed.

Theorem renormalized_by_stddev_mixed_admitted :
  forall u rnd N g s0 (beta : R -> (nat -> R) -> R) lmax l0 rhoc D mh c xs T,
    0 <= u < 1 -> (forall x, Rabs (rnd x - x) <= u * Rabs x) ->
    0 < N -> g <> 0 -> 0 <= l0 -> 0 <= s0 ->
    mcl l0 D (mh 1%nat) (Smom c 1 xs) -> rcl l0 (mh 2%nat) (Smom c 2 xs) ->
    0 < mh 2%nat ->
    exp (3 * l0) * ((Rabs (mh 1%nat) + D) * (Rabs (mh 1%nat) + D)
                    / (N * mh 2%nat)) <= rhoc ->
    rhoc < 1 ->
    D * D <= s0 * s0 * (N * mh 2%nat * exp (- l0)) ->
    0 < Lo (lu u + lmax) (lu u + ((lu u + lmax) + (lu u + lmax))) rhoc
        - deltam u rhoc lmax (sig s0 l0 lmax) / (1 - rhoc) ->
    grad u 2 (phi_m u rhoc s0 l0) l0 T <= lmax ->
    let st := piter (alpha_sd N g) beta T (c, xs) in
    rcl (grad u 2 (phi_m u rhoc s0 l0) l0 T)
        (fiter rnd (alphah_sd rnd N g) T mh 2%nat) (Smom (fst st) 2 (snd st))
    /\ mcl (grad u 2 (phi_m u rhoc s0 l0) l0 T)
           (sig s0 l0 (grad u 2 (phi_m u rhoc s0 l0) l0 T)
            * sqrt (N * Smom (fst st) 2 (snd st)))
           (fiter rnd (alphah_sd rnd N g) T mh 1%nat)
           (Smom (fst st) 1 (snd st)).
Proof.
  intros u rnd N g s0 beta lmax l0 rhoc D mh c xs T Hu Hrnd HN Hg Hl0 Hs0 M1 R2
    Hmh Hchk Hlt HD HLo Hmax st.
  destruct (rho_certified_upper l0 l0 D N _ _ _ _ HN Hmh M1 R2) as [Hm2 B].
  pose proof (mcl_d_nonneg _ _ _ _ M1) as HD0.
  assert (Hr0 : 0 <= exp (3 * l0) * ((Rabs (mh 1%nat) + D)
                  * (Rabs (mh 1%nat) + D) / (N * mh 2%nat))).
  { pose proof (Rabs_pos (mh 1%nat)).
    apply Rmult_le_pos; [left; apply exp_pos|].
    apply Rmult_le_pos; [nra|]. left. apply Rinv_0_lt_compat. nra. }
  assert (HI : Inv_sd N rhoc (smv c xs)).
  { split; [exact Hm2|]. unfold smv.
    replace (exp (3 * l0)) with (exp (2 * l0 + l0)) in Hchk
      by (f_equal; ring).
    assert (0 < N * Smom c 2 xs) by nra.
    assert (exp (2 * l0 + l0) * ((Rabs (mh 1%nat) + D) * (Rabs (mh 1%nat) + D)
              / (N * mh 2%nat)) * (N * Smom c 2 xs)
            <= rhoc * (N * Smom c 2 xs)) by (apply Rmult_le_compat_r; lra).
    lra. }
  (* D <= s0 sqrt(N M_2): the true M_2 is at least exp(-l0) mh2.          *)
  assert (M1' : mcl l0 (s0 * sqrt (N * Smom c 2 xs)) (mh 1%nat) (Smom c 1 xs)).
  { eapply mcl_mono; [| |exact M1]; [lra|].
    destruct R2 as [t2 [E2 [B2 C2]]].
    assert (Hlow : mh 2%nat * exp (- l0) <= Smom c 2 xs).
    { rewrite E2. pose proof (exp_inv_pair l0). pose proof (exp_pos (- l0)).
      assert (t2 * exp (- l0) <= exp l0 * exp (- l0))
        by (apply Rmult_le_compat_r; lra).
      nra. }
    assert (HX : 0 <= N * mh 2%nat * exp (- l0))
      by (pose proof (exp_pos (- l0)); apply Rmult_le_pos; nra).
    assert (Hs : D <= s0 * sqrt (N * mh 2%nat * exp (- l0))).
    { rewrite <- (sqrt_square D) by exact HD0.
      rewrite <- (sqrt_square s0) by exact Hs0.
      rewrite <- sqrt_mult_alt by nra.
      apply sqrt_le_1_alt. lra. }
    eapply Rle_trans; [exact Hs|]. apply Rmult_le_compat_l; [exact Hs0|].
    apply sqrt_le_1_alt. nra. }
  exact (renormalized_by_stddev_mixed u Hu rnd Hrnd N g rhoc s0 l0 HN Hg
           ltac:(lra) Hs0 beta lmax mh c xs T HLo Hl0 HI Hmax R2 M1').
Qed.

(* The [0, 1] witness (c = 0, N = 2, true M_1 = M_2 = 1, true rho 1/2):  *)
(* Mh_1 = 1/2 and Mh_2 = 2 are both within log-error ln 2, their raw     *)
(* ratio 1/16 is NOT an upper bound on the true rho, the inflated one    *)
(* exp(3 ln 2) / 16 = 1/2 is -- and is tight.                            *)
Example underestimating_m1_has_valid_log_radius : rcl (ln 2) (/ 2) 1.
Proof.
  exists (/ 2). split; [ring|].
  rewrite exp_Ropp, exp_ln by lra. split; lra.
Qed.

Example overestimating_m2_has_valid_log_radius : rcl (ln 2) 2 1.
Proof.
  exists 2. split; [ring|].
  rewrite exp_Ropp, exp_ln by lra. split; lra.
Qed.

Example uninflated_rho_fails_true_invariant :
  ~ Inv_sd 2 ((/ 2 * / 2) / (2 * 2)) (smv 0 [0; 1]).
Proof.
  unfold Inv_sd, smv. cbn [smom psum rpow map rsum]. intros [_ H]. nra.
Qed.

Example inflated_rho_meets_true_invariant :
  exp (3 * ln 2) * ((/ 2 * / 2) / (2 * 2)) = / 2
  /\ Inv_sd 2 (/ 2) (smv 0 [0; 1]).
Proof.
  split.
  - replace (3 * ln 2) with (ln 2 + ln 2 + ln 2) by ring.
    rewrite !exp_plus, exp_ln by lra. field.
  - unfold Inv_sd, smv. cbn [smom psum rpow map rsum]. split; lra.
Qed.

(* ===================================================================== *)
(* Part G.  N IS THE PARTICLE COUNT (audit RF-5).  N is a free section   *)
(* variable above, so renormalized_by_stddev_bound is a theorem about a  *)
(* variance-like statistic with an arbitrary divisor.  With              *)
(* N = INR (length xs) and xs nonempty it is the particles' variance:    *)
(*                                                                       *)
(*   vex_is_population_variance   vex N (smv c xs) is the population     *)
(*                             variance about the true mean, for every   *)
(*                             shift c;                                  *)
(*   renormalized_particles_bound   the contract with N the count, which *)
(*                             piter keeps (BladeShiftedFeedback.        *)
(*                             piter_length), so at EVERY step the true  *)
(*                             system divides by its own population      *)
(*                             standard deviation;                       *)
(*   renormalized_particles_admitted   the same under the run-time rho   *)
(*                             check of Part F: the particle-facing      *)
(*                             contract.                                 *)
(* ===================================================================== *)

Lemma ofnat_R : forall n, ofnat R 0 1 Rplus n = INR n.
Proof.
  induction n as [|n IH]; [reflexivity|]. cbn [ofnat]. rewrite IH, S_INR. ring.
Qed.

Lemma vex_is_population_variance : forall c xs, xs <> [] ->
  vex (INR (length xs)) (smv c xs)
  = Smom (Psum 1 xs / INR (length xs)) 2 xs / INR (length xs).
Proof.
  intros c xs Hne.
  assert (HN : 0 < INR (length xs)).
  { apply lt_0_INR. destruct xs as [|x xs]; [congruence|]. simpl. lia. }
  unfold vex, smv.
  rewrite !(smom_2_expand R 0 1 Rplus Rmult Rminus Ropp RTheory).
  rewrite (smom_1 R 0 1 Rplus Rmult Rminus Ropp RTheory).
  rewrite !ofnat_R. field. lra.
Qed.

Corollary renormalized_particles_bound :
  forall u rnd g rho (beta : R -> (nat -> R) -> R) lmax l0 mh c xs T,
    let N := INR (length xs) in
    0 <= u < 1 -> (forall x, Rabs (rnd x - x) <= u * Rabs x) ->
    g <> 0 -> 0 <= rho < 1 -> xs <> [] ->
    0 < Lo (lu u + lmax) (lu u + ((lu u + lmax) + (lu u + lmax))) rho ->
    0 <= l0 ->
    Inv_sd N rho (smv c xs) ->
    grad u 2 (phi_sd u rho) l0 T <= lmax ->
    (forall k, (1 <= k)%nat -> (k <= 2)%nat -> rcl l0 (mh k) (Smom c k xs)) ->
    let st := piter (alpha_sd N g) beta T (c, xs) in
    INR (length (snd st)) = N
    /\ vex N (smv (fst st) (snd st))
       = Smom (Psum 1 (snd st) / N) 2 (snd st) / N
    /\ (forall k, (1 <= k)%nat -> (k <= 2)%nat ->
          rcl (grad u 2 (phi_sd u rho) l0 T)
              (fiter rnd (alphah_sd rnd N g) T mh k)
              (Smom (fst st) k (snd st))).
Proof.
  intros u rnd g rho beta lmax l0 mh c xs T N Hu Hrnd Hg Hrho Hne HLo Hl0 HI
    Hmax Hm st.
  assert (HL : length (snd st) = length xs) by apply piter_length.
  assert (HN : 0 < N).
  { unfold N. apply lt_0_INR. destruct xs as [|x xs]; [congruence|]. simpl. lia. }
  split; [|split].
  - unfold N. rewrite HL. reflexivity.
  - unfold N. rewrite <- HL. apply vex_is_population_variance.
    intro E. rewrite E in HL. destruct xs; [congruence|discriminate].
  - intros k Hk1 Hkr.
    exact (renormalized_by_stddev_bound u Hu rnd Hrnd N g rho HN Hg Hrho beta
             lmax l0 mh c xs T HLo Hl0 HI Hmax Hm k Hk1 Hkr).
Qed.

Corollary renormalized_particles_admitted :
  forall u rnd g (beta : R -> (nat -> R) -> R) lmax l0 rhoc mh c xs T,
    let N := INR (length xs) in
    0 <= u < 1 -> (forall x, Rabs (rnd x - x) <= u * Rabs x) ->
    g <> 0 -> xs <> [] -> 0 <= l0 ->
    rcl l0 (mh 1%nat) (Smom c 1 xs) -> rcl l0 (mh 2%nat) (Smom c 2 xs) ->
    0 < mh 2%nat ->
    exp (3 * l0) * (mh 1%nat * mh 1%nat / (N * mh 2%nat)) <= rhoc ->
    rhoc < 1 ->
    0 < Lo (lu u + lmax) (lu u + ((lu u + lmax) + (lu u + lmax))) rhoc ->
    grad u 2 (phi_sd u rhoc) l0 T <= lmax ->
    let st := piter (alpha_sd N g) beta T (c, xs) in
    INR (length (snd st)) = N
    /\ vex N (smv (fst st) (snd st))
       = Smom (Psum 1 (snd st) / N) 2 (snd st) / N
    /\ (forall k, (1 <= k)%nat -> (k <= 2)%nat ->
          rcl (grad u 2 (phi_sd u rhoc) l0 T)
              (fiter rnd (alphah_sd rnd N g) T mh k)
              (Smom (fst st) k (snd st))).
Proof.
  intros u rnd g beta lmax l0 rhoc mh c xs T N Hu Hrnd Hg Hne Hl0 H1 H2 Hmh
    Hchk Hlt HLo Hmax st.
  assert (HN : 0 < N).
  { unfold N. apply lt_0_INR. destruct xs as [|x xs]; [congruence|]. simpl. lia. }
  destruct (stddev_admission_sound u N l0 lmax rhoc mh c xs HN H1 H2 Hmh Hchk
              Hlt HLo) as [HI [Hr _]].
  apply (renormalized_particles_bound u rnd g rhoc beta lmax l0 mh c xs T Hu Hrnd
           Hg Hr Hne HLo Hl0 HI Hmax).
  intros j Hj1 Hj2. destruct j as [|[|[|j]]]; [lia|exact H1|exact H2|lia].
Qed.
