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
(*                             x + b, renormalized by its own spread;    *)
(*   mcl, mcl_step,            Part F (audit 2026-10-15, RF-1): a MIXED  *)
(*   mixed_seed_input_bound,   relative + absolute seed (input           *)
(*   feedback_bound_read,      coefficients: the absolute part is        *)
(*                             carried by |a_1 .. a_T|^k), and the       *)
(*   normalized_ensemble_bound_seed   feedback theorem parameterized by  *)
(*                             the moments the coefficient READS -- only *)
(*                             those need a relative seed; the           *)
(*                             [3, -1e16, 1e16] obstruction pinned       *)
(*                             (seed_no_relative_radius);                *)
(*   normalized_exact_resets,  Part G (RF-3, RF-2): under g / sqrt(M_2)  *)
(*   normalized_m2_radius_constant,   the true M_2 is g^2 after every    *)
(*   normalized_particles_linear,     step, the float M_2 stays within   *)
(*   normalized_ensemble_bound_dom    6 lu of it (no compounding),       *)
(*                             every other moment's radius is LINEAR in  *)
(*                             T, and no divisor or radicand leaves its  *)
(*                             domain;                                   *)
(*   piter_length              the particle count is invariant (RF-5).   *)
(*                                                                       *)
(* Scope, stated once.  The coefficient may read the moments M_1 .. M_r, *)
(* not the shift c (the mean).  The radius of feedback_bound grows       *)
(* geometrically at rate 1 + r L.  That is a CONSERVATIVE WORST-CASE     *)
(* UPPER BOUND under the abstract stability hypothesis alpha_stable, not *)
(* a proved cost of feedback: it bounds the coefficient and the moment   *)
(* it multiplies separately and discards their correlation.  No lower    *)
(* bound is proved, and for g / sqrt(M_2) Part G proves the radius does  *)
(* not compound at all.  blade plan must still report the generic        *)
(* number when no sharper instance applies.                              *)
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
  (* coefficient is computed from its OWN rounded moments.  rad is a     *)
  (* conservative worst-case upper bound, not a lower bound (Part G).    *)
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
(*                                                                       *)
(* This corollary has no domain hypothesis: it holds over the TOTALIZED  *)
(* reals (/ 0 = 0, sqrt of a negative = 0), so it says nothing about     *)
(* whether an IEEE run stays finite.  normalized_ensemble_bound_dom      *)
(* (Part G) adds M_2 > 0 and g <> 0 and proves both runs stay in the     *)
(* domain at every step.                                                 *)
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

(* ===================================================================== *)
(* Part F.  A SEED THAT IS NOT RELATIVELY ACCURATE (audit 2026-10-15,    *)
(* RF-1).  rcl l xh 0 forces xh = 0, so every theorem above needs the    *)
(* seed EXACT at a zero moment.  A two-pass float seed is not: on        *)
(* [3, -1e16, 1e16] binary64 gives the mean 1 exactly and M_1 = 2, while *)
(* the true M_1 about that shift is 0 (seed_no_relative_radius below;    *)
(* floats/BladeBinary64Witness.v Part E).  The fix has two halves:       *)
(*                                                                       *)
(*   mcl                       MIXED error, xh = th x + e, th            *)
(*                             log-bounded by l and |e| <= d; d = 0 is   *)
(*                             rcl (rcl_mcl, mcl_rcl);                   *)
(*   mcl_step                  one float step keeps the form: the        *)
(*                             relative part grows as before, the        *)
(*                             absolute part is carried by |a|^k -- the  *)
(*                             same factor that scales M_k itself;       *)
(*   mixed_seed_input_bound    iterated over a word of INPUT             *)
(*                             coefficients (BladeShiftedRounding's      *)
(*                             setting): relative radius l + T k lu,     *)
(*                             absolute exp(T k lu) |a_1..a_T|^k d;      *)
(*   feedback_bound_read       the feedback theorem PARAMETERIZED BY THE *)
(*                             MOMENTS THE COEFFICIENT READS (rd): only  *)
(*                             those need a relative seed; every other   *)
(*                             moment may carry an absolute error d_k,   *)
(*                             which after T steps is at most            *)
(*                             exp(rad T - l0) |aprod T|^k d_k, aprod    *)
(*                             the product of the exact coefficients     *)
(*                             (eiter_aprod: M_k(T) = aprod^k M_k(0));   *)
(*   normalized_ensemble_bound_seed   instance: g / sqrt(M_2) reads only *)
(*                             M_2, so M_1 (and every odd moment) may be *)
(*                             seeded with an absolute error.            *)
(* ===================================================================== *)

(* xh approximates x with log-relative error l PLUS an absolute error at *)
(* most d.                                                               *)
Definition mcl (l d xh x : R) : Prop :=
  exists th e, xh = th * x + e /\ exp (- l) <= th <= exp l /\ Rabs e <= d.

Lemma rcl_mcl : forall l xh x, rcl l xh x -> mcl l 0 xh x.
Proof.
  intros l xh x [th [E B]]. exists th, 0.
  split; [rewrite E; ring|]. split; [exact B|]. rewrite Rabs_R0. lra.
Qed.

Lemma mcl_rcl : forall l xh x, mcl l 0 xh x -> rcl l xh x.
Proof.
  intros l xh x [th [e [E [B D]]]].
  assert (He : e = 0).
  { destruct (Req_dec e 0) as [Z|Z]; [exact Z|].
    pose proof (Rabs_pos_lt e Z). lra. }
  exists th. split; [rewrite E, He; ring|exact B].
Qed.

Lemma mcl_abs : forall d xh x, Rabs (xh - x) <= d -> mcl 0 d xh x.
Proof.
  intros d xh x H. exists 1, (xh - x). split; [ring|].
  rewrite Ropp_0, exp_0. split; [lra|exact H].
Qed.

Lemma mcl_d_nonneg : forall l d xh x, mcl l d xh x -> 0 <= d.
Proof.
  intros l d xh x [th [e [_ [_ D]]]]. pose proof (Rabs_pos e). lra.
Qed.

Lemma mcl_mono : forall l l' d d' xh x, l <= l' -> d <= d' ->
  mcl l d xh x -> mcl l' d' xh x.
Proof.
  intros l l' d d' xh x Hl Hd [th [e [E [B D]]]]. exists th, e.
  split; [exact E|]. split; [|lra].
  pose proof (exp_le_mono (- l') (- l) ltac:(lra)).
  pose proof (exp_le_mono l l' Hl). lra.
Qed.

(* Two log-bounded factors multiply into one.                            *)
Lemma factor_mul : forall l1 l2 t1 t2,
  exp (- l1) <= t1 <= exp l1 -> exp (- l2) <= t2 <= exp l2 ->
  exp (- (l1 + l2)) <= t1 * t2 <= exp (l1 + l2).
Proof.
  intros l1 l2 t1 t2 B1 B2.
  pose proof (exp_pos (- l1)). pose proof (exp_pos (- l2)).
  rewrite Ropp_plus_distr, !exp_plus. split.
  - apply Rmult_le_compat; lra.
  - apply Rmult_le_compat; lra.
Qed.

Section MixedStep.
  Variable u : R.
  Hypothesis Hu : 0 <= u < 1.
  Variable rnd : R -> R.
  Hypothesis rnd_rel : forall x, Rabs (rnd x - x) <= u * Rabs x.

  (* One float step M <- fl(fl(a^k) M) from a coefficient at log-error   *)
  (* la: the relative part gains k la + k lu, the absolute part is       *)
  (* scaled by |a|^k and by the same rounding factor.                    *)
  Lemma mcl_step : forall l d la ah a xh x k, (1 <= k)%nat -> 0 <= la ->
    mcl l d xh x -> rcl la ah a ->
    mcl (l + (INR k * la + INR k * lu u))
        (exp (INR k * la + INR k * lu u) * (Rabs a ^ k * d))
        (rnd (fpow rnd ah k * xh)) (a ^ k * x).
  Proof.
    intros l d la ah a xh x k Hk Hla [th [e [E [B D]]]] Ha.
    destruct (fpow_rcl u Hu rnd rnd_rel la ah a k Hk Hla Ha) as [tp [Ep Bp]].
    destruct (rnd_rcl u Hu rnd rnd_rel (fpow rnd ah k * xh)) as [tr [Er Br]].
    assert (Hls : lu u + (INR k * la + INR (k - 1) * lu u)
                  = INR k * la + INR k * lu u).
    { assert (INR (k - 1) + 1 = INR k) by (rewrite <- S_INR; f_equal; lia).
      nra. }
    pose proof (factor_mul _ _ _ _ Br Bp) as Btp. rewrite Hls in Btp.
    pose proof (factor_mul _ _ _ _ Btp B) as Ball.
    exists (tr * tp * th), (tr * tp * a ^ k * e). split.
    { rewrite Er, Ep, E. ring. }
    split.
    - replace (l + (INR k * la + INR k * lu u))
        with (INR k * la + INR k * lu u + l) by ring. exact Ball.
    - assert (Htp0 : 0 < tr * tp)
        by (pose proof (exp_pos (- (INR k * la + INR k * lu u))); lra).
      replace (tr * tp * a ^ k * e) with ((tr * tp) * (a ^ k * e)) by ring.
      rewrite Rabs_mult, (Rabs_right (tr * tp)) by lra.
      rewrite Rabs_mult, <- RPow_abs.
      pose proof (Rabs_pos e). pose proof (pow_le (Rabs a) k (Rabs_pos a)).
      assert (Rabs a ^ k * Rabs e <= Rabs a ^ k * d)
        by (apply Rmult_le_compat_l; lra).
      assert (tr * tp * (Rabs a ^ k * Rabs e)
              <= exp (INR k * la + INR k * lu u) * (Rabs a ^ k * Rabs e))
        by (apply Rmult_le_compat_r; [apply Rmult_le_pos|]; lra).
      assert (exp (INR k * la + INR k * lu u) * (Rabs a ^ k * Rabs e)
              <= exp (INR k * la + INR k * lu u) * (Rabs a ^ k * d))
        by (apply Rmult_le_compat_l; [left; apply exp_pos|]; lra).
      nra.
  Qed.

  (* Coefficients FROM THE INPUT (a word of pairs, as in                 *)
  (* BladeShiftedRounding, whose mex is mexw): a mixed seed stays mixed, *)
  (* the relative part gaining k lu a step, the absolute part carried by *)
  (* |a_1 ... a_T|^k -- so a zero moment needs no exact seed here either.*)
  Fixpoint mflw (k : nat) (w : list (R * R)) (M : R) : R :=
    match w with [] => M | (a, _) :: w' => mflw k w' (rnd (fpow rnd a k * M)) end.
  Fixpoint mexw (k : nat) (w : list (R * R)) (M : R) : R :=
    match w with [] => M | (a, _) :: w' => mexw k w' (a ^ k * M) end.
  Fixpoint wprod (w : list (R * R)) : R :=
    match w with [] => 1 | (a, _) :: w' => a * wprod w' end.

  Theorem mixed_seed_input_bound : forall k w l d Mh M, (1 <= k)%nat ->
    mcl l d Mh M ->
    mcl (l + INR (length w) * (INR k * lu u))
        (exp (INR (length w) * (INR k * lu u)) * (Rabs (wprod w) ^ k * d))
        (mflw k w Mh) (mexw k w M).
  Proof.
    intros k w. induction w as [|[a b] w IH]; intros l d Mh M Hk Hm.
    - cbn [mflw mexw wprod length]. replace (INR 0) with 0 by reflexivity.
      rewrite Rmult_0_l, Rplus_0_r, exp_0, Rabs_R1, pow1, !Rmult_1_l. exact Hm.
    - cbn [mflw mexw wprod length].
      pose proof (mcl_step l d 0 a a Mh M k Hk (Rle_refl 0) Hm (rcl_refl a)) as S1.
      rewrite Rmult_0_r, Rplus_0_l in S1.
      pose proof (IH _ _ _ _ Hk S1) as B.
      eapply mcl_mono; [| |exact B].
      + rewrite S_INR. right. ring.
      + right. rewrite S_INR, Rabs_mult, Rpow_mult_distr.
        replace ((INR (length w) + 1) * (INR k * lu u))
          with (INR (length w) * (INR k * lu u) + INR k * lu u) by ring.
        rewrite exp_plus. ring.
  Qed.

End MixedStep.

Section FeedbackRead.
  Variable u : R.
  Hypothesis Hu : 0 <= u < 1.
  Variable rnd : R -> R.
  Hypothesis rnd_rel : forall x, Rabs (rnd x - x) <= u * Rabs x.

  Variable r : nat.
  (* The moments the coefficient READS.                                  *)
  Variable rd : nat -> Prop.
  Variables alpha alphah : (nat -> R) -> R.
  Variables L eps : R.
  Hypothesis HL0 : 0 <= L.
  Hypothesis He0 : 0 <= eps.
  (* alpha_stable, asking relative accuracy only of the moments read.    *)
  Hypothesis alpha_stable_read : forall l mh m, 0 <= l ->
    (forall k, (1 <= k)%nat -> (k <= r)%nat -> rd k -> rcl l (mh k) (m k)) ->
    rcl (L * l + eps) (alphah mh) (alpha m).

  (* The product of the exact coefficients along the exact run.          *)
  Fixpoint aprod (T : nat) (m : nat -> R) : R :=
    match T with O => 1 | S T' => alpha m * aprod T' (estep alpha m) end.

  Lemma eiter_aprod : forall T m k, eiter alpha T m k = aprod T m ^ k * m k.
  Proof.
    induction T as [|T IH]; intros m k.
    - cbn [eiter aprod]. rewrite pow1. ring.
    - cbn [eiter aprod]. rewrite IH. unfold estep.
      rewrite Rpow_mult_distr. ring.
  Qed.

  Lemma step_read : forall l d mh m, 0 <= l ->
    (forall k, (1 <= k)%nat -> (k <= r)%nat -> mcl l (d k) (mh k) (m k)) ->
    (forall k, (1 <= k)%nat -> (k <= r)%nat -> rd k -> d k = 0) ->
    forall k, (1 <= k)%nat -> (k <= r)%nat ->
      mcl (step_l u r L eps l)
          (exp (step_l u r L eps l - l) * (Rabs (alpha m) ^ k * d k))
          (fstep rnd alphah mh k) (estep alpha m k).
  Proof.
    intros l d mh m Hl Hm Hd k Hk1 Hkr.
    assert (Ha : rcl (L * l + eps) (alphah mh) (alpha m)).
    { apply alpha_stable_read; [exact Hl|]. intros j Hj1 Hjr Hj.
      apply mcl_rcl. rewrite <- (Hd j Hj1 Hjr Hj). exact (Hm j Hj1 Hjr). }
    assert (Hla : 0 <= L * l + eps)
      by (pose proof (Rmult_le_pos L l HL0 Hl); lra).
    pose proof (mcl_step u Hu rnd rnd_rel _ _ _ _ _ _ _ k Hk1 Hla
                  (Hm k Hk1 Hkr) Ha) as S1.
    unfold fstep, estep.
    assert (Hk : INR k <= INR r) by (apply le_INR; exact Hkr).
    pose proof (lu_nonneg u Hu) as Hlu.
    assert (A : INR k * (L * l + eps) + INR k * lu u
                <= step_l u r L eps l - l).
    { unfold step_l.
      assert (INR k * (L * l + eps) <= INR r * (L * l + eps))
        by (apply Rmult_le_compat_r; lra).
      assert (INR k * lu u <= INR r * lu u)
        by (apply Rmult_le_compat_r; lra).
      nra. }
    pose proof (mcl_d_nonneg _ _ _ _ (Hm k Hk1 Hkr)) as Hd0.
    pose proof (pow_le (Rabs (alpha m)) k (Rabs_pos _)) as Hp.
    eapply mcl_mono; [| |exact S1]; [lra|].
    apply Rmult_le_compat_r; [apply Rmult_le_pos; lra|].
    apply exp_le_mono. exact A.
  Qed.

  (* THE FEEDBACK BOUND FROM A MIXED SEED.  The moments read are seeded  *)
  (* relatively (d_k = 0); every other moment may carry an absolute      *)
  (* error d_k.  The relative radius is rad l0 T, as in feedback_bound;  *)
  (* the absolute part is transported by the exact dynamics.             *)
  Theorem feedback_bound_read : forall T l0 d mh m, 0 <= l0 ->
    (forall k, (1 <= k)%nat -> (k <= r)%nat -> mcl l0 (d k) (mh k) (m k)) ->
    (forall k, (1 <= k)%nat -> (k <= r)%nat -> rd k -> d k = 0) ->
    forall k, (1 <= k)%nat -> (k <= r)%nat ->
      mcl (rad u r L eps l0 T)
          (exp (rad u r L eps l0 T - l0) * (Rabs (aprod T m) ^ k * d k))
          (fiter rnd alphah T mh k) (eiter alpha T m k).
  Proof.
    induction T as [|T IH]; intros l0 d mh m Hl0 Hm Hd k Hk1 Hkr.
    - cbn [rad aprod fiter eiter]. rewrite Rminus_diag, exp_0, Rabs_R1, pow1.
      replace (1 * (1 * d k)) with (d k) by ring. exact (Hm k Hk1 Hkr).
    - cbn [fiter eiter aprod]. rewrite rad_shift.
      set (l1 := step_l u r L eps l0).
      set (d1 := fun j => exp (l1 - l0) * (Rabs (alpha m) ^ j * d j)).
      assert (Hl1 : 0 <= l1) by (apply step_l_nonneg; assumption).
      pose proof (IH l1 d1 (fstep rnd alphah mh) (estep alpha m) Hl1) as B.
      specialize (B (step_read l0 d mh m Hl0 Hm Hd)).
      assert (Hd1 : forall j, (1 <= j)%nat -> (j <= r)%nat -> rd j -> d1 j = 0).
      { intros j Hj1 Hjr Hj. unfold d1. rewrite (Hd j Hj1 Hjr Hj). ring. }
      specialize (B Hd1 k Hk1 Hkr).
      replace (exp (rad u r L eps l1 T - l0)
               * (Rabs (alpha m * aprod T (estep alpha m)) ^ k * d k))
        with (exp (rad u r L eps l1 T - l1)
               * (Rabs (aprod T (estep alpha m)) ^ k * d1 k)).
      + exact B.
      + unfold d1.
        replace (rad u r L eps l1 T - l0)
          with ((rad u r L eps l1 T - l1) + (l1 - l0)) by ring.
        rewrite exp_plus, Rabs_mult, Rpow_mult_distr. ring.
  Qed.

End FeedbackRead.

(* The g / sqrt(M_2) normalization reads ONLY M_2: the exact coefficient *)
(* and the one the float program computes.                               *)
Definition alpha_n (g : R) (m : nat -> R) : R := g * / sqrt (m 2%nat).
Definition alphah_n (rnd : R -> R) (g : R) (mh : nat -> R) : R :=
  rnd (g * / rnd (sqrt (mh 2%nat))).

(* normalized_ensemble_bound with a MIXED seed: M_2 relative (d_2 = 0),  *)
(* every other moment -- M_1 in particular -- relative plus absolute.    *)
Corollary normalized_ensemble_bound_seed : forall u rnd (g : R)
    (beta : R -> (nat -> R) -> R) r l0 d mh c xs,
  0 <= u < 1 -> (forall x, Rabs (rnd x - x) <= u * Rabs x) ->
  (2 <= r)%nat -> 0 <= l0 ->
  (forall k, (1 <= k)%nat -> (k <= r)%nat -> mcl l0 (d k) (mh k) (Smom c k xs)) ->
  d 2%nat = 0 ->
  forall T k, (1 <= k)%nat -> (k <= r)%nat ->
    mcl (rad u r (/ 2) (2 * lu u) l0 T)
        (exp (rad u r (/ 2) (2 * lu u) l0 T - l0)
         * (Rabs (aprod (alpha_n g) T (smv c xs)) ^ k * d k))
        (fiter rnd (alphah_n rnd g) T mh k)
        (Smom (fst (piter (alpha_n g) beta T (c, xs))) k
              (snd (piter (alpha_n g) beta T (c, xs)))).
Proof.
  intros u rnd g beta r l0 d mh c xs Hu Hrnd Hr Hl0 Hm Hd2 T k Hk1 Hkr.
  change (Smom (fst (piter (alpha_n g) beta T (c, xs))) k
               (snd (piter (alpha_n g) beta T (c, xs))))
    with (smv (fst (piter (alpha_n g) beta T (c, xs)))
              (snd (piter (alpha_n g) beta T (c, xs))) k).
  rewrite (eiter_is_particles r (alpha_n g)).
  - apply (feedback_bound_read u Hu rnd Hrnd r (fun j => j = 2%nat)
             (alpha_n g) (alphah_n rnd g) (/ 2) (2 * lu u)); try assumption.
    + left. apply Rinv_0_lt_compat. lra.
    + pose proof (lu_nonneg u Hu). lra.
    + intros l mh1 m1 Hl H. unfold alphah_n, alpha_n.
      apply (normalizing_coefficient_stable u Hu rnd Hrnd g l mh1 m1 Hl).
      apply H; [lia|lia|reflexivity].
    + intros j _ _ Hj. subst j. exact Hd2.
  - intros m m' H. unfold alpha_n. rewrite (H 2%nat) by lia. reflexivity.
Qed.

(* THE OBSTRUCTION, pinned.  [3, -1e16, 1e16] about the binary64 two-pass *)
(* mean c = 1: the true M_1 is 0, binary64 computes 2 (floats/ Part E).   *)
(* No relative radius covers it; the mixed predicate does, with d = 2,    *)
(* and normalized_ensemble_bound_seed accepts it because M_1 is not read. *)
Definition seed_xs : list R := [3; -10000000000000000; 10000000000000000].

Example seed_true_first_moment_zero : Smom 1 1 seed_xs = 0.
Proof. unfold seed_xs. cbn [smom psum rpow map rsum]. ring. Qed.

Example seed_no_relative_radius : forall l, ~ rcl l 2 (Smom 1 1 seed_xs).
Proof.
  intros l [th [E _]]. rewrite seed_true_first_moment_zero in E. lra.
Qed.

Example seed_mixed_radius : mcl 0 2 2 (Smom 1 1 seed_xs).
Proof.
  apply mcl_abs. rewrite seed_true_first_moment_zero.
  rewrite Rminus_0_r, Rabs_right by lra. lra.
Qed.

(* ===================================================================== *)
(* Part G.  THE NORMALIZED SECOND MOMENT DOES NOT ACCUMULATE ERROR       *)
(* (audit RF-3), and the normalization never leaves its domain (RF-2).   *)
(*                                                                       *)
(* The geometric radius of feedback_bound is a CONSERVATIVE WORST-CASE   *)
(* UPPER BOUND under the abstract stability hypothesis: it bounds the    *)
(* coefficient and the moment it multiplies separately, and so discards  *)
(* their correlation.  For a = g / sqrt(M_2) the correlation is total:   *)
(*                                                                       *)
(*   normalized_second_moment_resets, normalized_exact_resets   exactly, *)
(*                             (g / sqrt M_2)^2 M_2 = g^2, so M_2 = g^2  *)
(*                             after every step, from ANY positive       *)
(*                             start -- an incoming error is erased in   *)
(*                             one step (normalized_seed_error_erased);  *)
(*   normalized_m2_nongrowing, normalized_m2_radius_constant   rounded,  *)
(*                             the float M_2 is within log-error 6 lu of *)
(*                             g^2 = the true M_2 after EVERY step >= 1, *)
(*                             whatever the seed error: a radius that    *)
(*                             does not compound;                        *)
(*   normalized_linear_bound, normalized_particles_linear   every other  *)
(*                             moment then grows LINEARLY,               *)
(*                             l0 + k (l2 / 2 + 3 lu) + 6 k lu T, from a *)
(*                             mixed seed -- where the generic radius    *)
(*                             compounds at rate 1 + r/2;                *)
(*   normalized_domain, normalized_ensemble_bound_dom   RF-2: with       *)
(*                             M_2 > 0 and g <> 0 every radicand is      *)
(*                             positive and every divisor nonzero, in    *)
(*                             both runs, at every step.                 *)
(*                                                                       *)
(* What the totalized model does at 0.  Coq's reals are total: / 0 = 0   *)
(* (Rinv_0) and sqrt x = 0 for x <= 0 (sqrt_neg_0), and rcl_inv,         *)
(* rcl_sqrt USE those cases.  So normalized_ensemble_bound holds even    *)
(* for a zero-spread ensemble, where the real model's coefficient is 0   *)
(* and IEEE binary64 gives +inf and then NaN (floats/ Part E).  The      *)
(* standard model rnd cannot represent that run.  The _dom corollary     *)
(* removes the reliance: under its hypotheses the totalized cases are    *)
(* never reached, by either run.                                         *)
(* ===================================================================== *)

Lemma normalized_second_moment_resets : forall g x, 0 < x ->
  (g * / sqrt x) ^ 2 * x = g ^ 2.
Proof.
  intros g x Hx.
  assert (Hs : sqrt x <> 0)
    by (apply Rgt_not_eq; apply sqrt_lt_R0; exact Hx).
  pose proof (sqrt_sqrt x (Rlt_le _ _ Hx)) as Hsq.
  replace ((g * / sqrt x) ^ 2 * x) with (g ^ 2 * (x / (sqrt x * sqrt x)))
    by (field; exact Hs).
  rewrite Hsq. field. lra.
Qed.

Lemma normalized_exact_step : forall g m, 0 < m 2%nat ->
  estep (alpha_n g) m 2%nat = g ^ 2.
Proof.
  intros g m H. unfold estep, alpha_n. apply normalized_second_moment_resets.
  exact H.
Qed.

Lemma sq_pos : forall g, g <> 0 -> 0 < g ^ 2.
Proof.
  intros g Hg. replace (g ^ 2) with (Rsqr g) by (unfold Rsqr; ring).
  apply Rsqr_pos_lt. exact Hg.
Qed.

Theorem normalized_exact_resets : forall g T m, g <> 0 -> 0 < m 2%nat ->
  eiter (alpha_n g) (S T) m 2%nat = g ^ 2.
Proof.
  intros g T. induction T as [|T IH]; intros m Hg Hm.
  - cbn [eiter]. apply normalized_exact_step. exact Hm.
  - change (eiter (alpha_n g) (S (S T)) m 2%nat)
      with (eiter (alpha_n g) (S T) (estep (alpha_n g) m) 2%nat).
    apply IH; [exact Hg|]. rewrite normalized_exact_step by exact Hm.
    apply sq_pos. exact Hg.
Qed.

Corollary normalized_seed_error_erased : forall g mh m, g <> 0 ->
  0 < mh 2%nat -> 0 < m 2%nat ->
  eiter (alpha_n g) 1 mh 2%nat = eiter (alpha_n g) 1 m 2%nat.
Proof.
  intros g mh m Hg H1 H2.
  rewrite !(normalized_exact_resets g 0) by assumption. reflexivity.
Qed.

(* After the reset the exact coefficient is g / |g| = +-1.               *)
Lemma alpha_n_unit : forall g m, g <> 0 -> m 2%nat = g ^ 2 ->
  Rabs (alpha_n g m) = 1.
Proof.
  intros g m Hg Hm. unfold alpha_n. rewrite Hm.
  replace (g ^ 2) with (Rsqr g) by (unfold Rsqr; ring).
  rewrite sqrt_Rsqr_abs, Rabs_mult, Rabs_inv, Rabs_Rabsolu.
  apply Rinv_r. apply Rabs_no_R0. exact Hg.
Qed.

Section NormalizedRounded.
  Variable u : R.
  Hypothesis Hu : 0 <= u < 1.
  Variable rnd : R -> R.
  Hypothesis rnd_rel : forall x, Rabs (rnd x - x) <= u * Rabs x.

  Lemma rnd_pos : forall y, 0 < y -> 0 < rnd y.
  Proof.
    intros y Hy. destruct (rnd_rcl u Hu rnd rnd_rel y) as [t [E [B _]]].
    rewrite E. pose proof (exp_pos (- lu u)).
    apply Rmult_lt_0_compat; lra.
  Qed.

  (* One rounded step: fl(fl(a_h^2) M_2h) with a_h = fl(g / fl(sqrt     *)
  (* M_2h)) is g^2 times six rounding factors -- M_2h cancels.           *)
  Lemma normalized_m2_step : forall g mh, g <> 0 -> 0 < mh 2%nat ->
    rcl (6 * lu u) (fstep rnd (alphah_n rnd g) mh 2%nat) (g ^ 2)
    /\ 0 < fstep rnd (alphah_n rnd g) mh 2%nat.
  Proof.
    intros g mh Hg Hm.
    pose proof (normalizing_coefficient_stable u Hu rnd rnd_rel g 0 mh mh
                  (Rle_refl 0) (rcl_refl _)) as Ha.
    pose proof (lu_nonneg u Hu) as Hlu.
    assert (Hla : 0 <= / 2 * 0 + 2 * lu u) by lra.
    pose proof (fpow_rcl u Hu rnd rnd_rel _ _ _ 2 ltac:(lia) Hla Ha) as P.
    pose proof (rcl_rnd u Hu rnd rnd_rel _ _ _
                  (rcl_mul _ _ _ _ _ _ P (rcl_refl (mh 2%nat)))) as S1.
    rewrite normalized_second_moment_resets in S1 by exact Hm.
    assert (R6 : rcl (6 * lu u) (fstep rnd (alphah_n rnd g) mh 2%nat) (g ^ 2)).
    { unfold fstep, alphah_n. eapply rcl_mono; [|exact S1].
      replace (INR 2) with 2 by (simpl; ring).
      replace (INR (2 - 1)) with 1 by (simpl; ring). lra. }
    split; [exact R6|].
    destruct R6 as [th [E [B1 B2]]]. rewrite E.
    apply Rmult_lt_0_compat; [pose proof (exp_pos (- (6 * lu u))); lra|].
    apply sq_pos. exact Hg.
  Qed.

  Theorem normalized_m2_nongrowing : forall g T mh, g <> 0 -> 0 < mh 2%nat ->
    rcl (6 * lu u) (fiter rnd (alphah_n rnd g) (S T) mh 2%nat) (g ^ 2)
    /\ 0 < fiter rnd (alphah_n rnd g) (S T) mh 2%nat.
  Proof.
    intros g T. induction T as [|T IH]; intros mh Hg Hm.
    - cbn [fiter]. apply normalized_m2_step; assumption.
    - change (fiter rnd (alphah_n rnd g) (S (S T)) mh 2%nat)
        with (fiter rnd (alphah_n rnd g) (S T)
                    (fstep rnd (alphah_n rnd g) mh) 2%nat).
      apply IH; [exact Hg|]. apply (normalized_m2_step g mh Hg Hm).
  Qed.

  Corollary normalized_m2_radius_constant : forall g beta T mh c xs,
    g <> 0 -> 0 < mh 2%nat -> 0 < Smom c 2 xs ->
    rcl (6 * lu u) (fiter rnd (alphah_n rnd g) (S T) mh 2%nat)
        (Smom (fst (piter (alpha_n g) beta (S T) (c, xs))) 2
              (snd (piter (alpha_n g) beta (S T) (c, xs)))).
  Proof.
    intros g beta T mh c xs Hg Hmh Hm.
    change (Smom (fst (piter (alpha_n g) beta (S T) (c, xs))) 2
                 (snd (piter (alpha_n g) beta (S T) (c, xs))))
      with (smv (fst (piter (alpha_n g) beta (S T) (c, xs)))
                (snd (piter (alpha_n g) beta (S T) (c, xs))) 2%nat).
    rewrite (eiter_is_particles 2 (alpha_n g)).
    - rewrite normalized_exact_resets by assumption.
      apply normalized_m2_nongrowing; assumption.
    - intros m m' H. unfold alpha_n. rewrite (H 2%nat) by lia. reflexivity.
  Qed.

  Theorem normalized_domain : forall g T m mh, g <> 0 ->
    0 < m 2%nat -> 0 < mh 2%nat ->
    0 < eiter (alpha_n g) T m 2%nat
    /\ 0 < sqrt (eiter (alpha_n g) T m 2%nat)
    /\ 0 < fiter rnd (alphah_n rnd g) T mh 2%nat
    /\ 0 < rnd (sqrt (fiter rnd (alphah_n rnd g) T mh 2%nat)).
  Proof.
    intros g T m mh Hg Hm Hmh.
    assert (E : 0 < eiter (alpha_n g) T m 2%nat).
    { destruct T as [|T]; [exact Hm|].
      rewrite normalized_exact_resets by assumption. apply sq_pos. exact Hg. }
    assert (F : 0 < fiter rnd (alphah_n rnd g) T mh 2%nat).
    { destruct T as [|T]; [exact Hmh|].
      apply (normalized_m2_nongrowing g T mh Hg Hmh). }
    split; [exact E|]. split; [apply sqrt_lt_R0; exact E|].
    split; [exact F|]. apply rnd_pos. apply sqrt_lt_R0. exact F.
  Qed.

  (* After the reset, every moment's radius grows LINEARLY: the          *)
  (* coefficient is known to 3 lu + 2 lu at every step.                  *)
  Lemma normalized_linear_tail : forall g k T l d mh m, (1 <= k)%nat ->
    g <> 0 -> m 2%nat = g ^ 2 -> 0 < mh 2%nat ->
    rcl (6 * lu u) (mh 2%nat) (m 2%nat) ->
    mcl l d (mh k) (m k) ->
    mcl (l + INR T * (6 * INR k * lu u))
        (exp (INR T * (6 * INR k * lu u)) * d)
        (fiter rnd (alphah_n rnd g) T mh k) (eiter (alpha_n g) T m k).
  Proof.
    intros g k T. induction T as [|T IH]; intros l d mh m Hk Hg Hm2 Hmh R2 Hm.
    - cbn [fiter eiter]. replace (INR 0) with 0 by reflexivity.
      rewrite Rmult_0_l, Rplus_0_r, exp_0, Rmult_1_l. exact Hm.
    - cbn [fiter eiter].
      pose proof (lu_nonneg u Hu) as Hlu.
      pose proof (normalizing_coefficient_stable u Hu rnd rnd_rel g (6 * lu u)
                    mh m ltac:(lra) R2) as Ha.
      assert (Hla : 0 <= / 2 * (6 * lu u) + 2 * lu u) by lra.
      pose proof (mcl_step u Hu rnd rnd_rel _ _ _ _ _ _ _ k Hk Hla Hm Ha) as S1.
      fold (alpha_n g m) in S1.
      rewrite (alpha_n_unit g m Hg Hm2), pow1, Rmult_1_l in S1.
      replace (INR k * (/ 2 * (6 * lu u) + 2 * lu u) + INR k * lu u)
        with (6 * INR k * lu u) in S1 by field.
      assert (Hm2' : estep (alpha_n g) m 2%nat = g ^ 2).
      { apply normalized_exact_step. rewrite Hm2. apply sq_pos. exact Hg. }
      destruct (normalized_m2_step g mh Hg Hmh) as [R2' P2'].
      assert (R2'' : rcl (6 * lu u) (fstep rnd (alphah_n rnd g) mh 2%nat)
                         (estep (alpha_n g) m 2%nat))
        by (rewrite Hm2'; exact R2').
      pose proof (IH _ _ (fstep rnd (alphah_n rnd g) mh) (estep (alpha_n g) m)
                    Hk Hg Hm2' P2' R2'' S1) as B.
      eapply mcl_mono; [| |exact B].
      + rewrite S_INR. lra.
      + replace (exp (INR T * (6 * INR k * lu u)) * (exp (6 * INR k * lu u) * d))
          with (exp (INR (S T) * (6 * INR k * lu u)) * d).
        * lra.
        * rewrite S_INR.
          replace ((INR T + 1) * (6 * INR k * lu u))
            with (INR T * (6 * INR k * lu u) + 6 * INR k * lu u) by ring.
          rewrite exp_plus. ring.
  Qed.

  (* From a mixed seed: M_2 at log-error l2, M_k at (l0, d).  After      *)
  (* T + 1 steps the radius is LINEAR in T.                              *)
  Theorem normalized_linear_bound : forall g k T l0 l2 d mh m, (1 <= k)%nat ->
    g <> 0 -> 0 < m 2%nat -> 0 <= l2 ->
    rcl l2 (mh 2%nat) (m 2%nat) -> mcl l0 d (mh k) (m k) ->
    mcl (l0 + INR k * (/ 2 * l2 + 3 * lu u) + INR T * (6 * INR k * lu u))
        (exp (INR k * (/ 2 * l2 + 3 * lu u) + INR T * (6 * INR k * lu u))
         * (Rabs (alpha_n g m) ^ k * d))
        (fiter rnd (alphah_n rnd g) (S T) mh k) (eiter (alpha_n g) (S T) m k).
  Proof.
    intros g k T l0 l2 d mh m Hk Hg Hm2 Hl2 R2 Hm. cbn [fiter eiter].
    pose proof (lu_nonneg u Hu) as Hlu.
    assert (Hmh : 0 < mh 2%nat).
    { destruct R2 as [th [E [B _]]]. rewrite E.
      apply Rmult_lt_0_compat; [pose proof (exp_pos (- l2)); lra|exact Hm2]. }
    pose proof (normalizing_coefficient_stable u Hu rnd rnd_rel g l2 mh m
                  Hl2 R2) as Ha.
    assert (Hla : 0 <= / 2 * l2 + 2 * lu u) by lra.
    pose proof (mcl_step u Hu rnd rnd_rel _ _ _ _ _ _ _ k Hk Hla Hm Ha) as S1.
    fold (alpha_n g m) in S1.
    assert (Hm2' : estep (alpha_n g) m 2%nat = g ^ 2)
      by (apply normalized_exact_step; exact Hm2).
    destruct (normalized_m2_step g mh Hg Hmh) as [R2' P2'].
    assert (R2'' : rcl (6 * lu u) (fstep rnd (alphah_n rnd g) mh 2%nat)
                       (estep (alpha_n g) m 2%nat))
      by (rewrite Hm2'; exact R2').
    pose proof (normalized_linear_tail g k T _ _
                  (fstep rnd (alphah_n rnd g) mh) (estep (alpha_n g) m)
                  Hk Hg Hm2' P2' R2'' S1) as B.
    eapply mcl_mono; [| |exact B].
    - right. ring.
    - right.
      replace (INR k * (/ 2 * l2 + 3 * lu u) + INR T * (6 * INR k * lu u))
        with (INR T * (6 * INR k * lu u)
              + (INR k * (/ 2 * l2 + 2 * lu u) + INR k * lu u)) by ring.
      rewrite !exp_plus. ring.
  Qed.

  Corollary normalized_particles_linear : forall g beta k T l0 l2 d mh c xs,
    (1 <= k)%nat -> g <> 0 -> 0 < Smom c 2 xs -> 0 <= l2 ->
    rcl l2 (mh 2%nat) (Smom c 2 xs) -> mcl l0 d (mh k) (Smom c k xs) ->
    mcl (l0 + INR k * (/ 2 * l2 + 3 * lu u) + INR T * (6 * INR k * lu u))
        (exp (INR k * (/ 2 * l2 + 3 * lu u) + INR T * (6 * INR k * lu u))
         * (Rabs (alpha_n g (smv c xs)) ^ k * d))
        (fiter rnd (alphah_n rnd g) (S T) mh k)
        (Smom (fst (piter (alpha_n g) beta (S T) (c, xs))) k
              (snd (piter (alpha_n g) beta (S T) (c, xs)))).
  Proof.
    intros g beta k T l0 l2 d mh c xs Hk Hg Hm2 Hl2 R2 Hm.
    change (Smom (fst (piter (alpha_n g) beta (S T) (c, xs))) k
                 (snd (piter (alpha_n g) beta (S T) (c, xs))))
      with (smv (fst (piter (alpha_n g) beta (S T) (c, xs)))
                (snd (piter (alpha_n g) beta (S T) (c, xs))) k).
    rewrite (eiter_is_particles 2 (alpha_n g)).
    - apply normalized_linear_bound; assumption.
    - intros m m' H. unfold alpha_n. rewrite (H 2%nat) by lia. reflexivity.
  Qed.

  (* normalized_ensemble_bound WITH ITS DOMAIN (RF-2): the bound, and at *)
  (* every step a positive true M_2, a positive float M_2 and a nonzero  *)
  (* float divisor fl(sqrt M_2h).                                        *)
  Corollary normalized_ensemble_bound_dom : forall g beta r l0 mh c xs,
    (2 <= r)%nat -> 0 <= l0 -> g <> 0 -> 0 < Smom c 2 xs ->
    (forall k, (1 <= k)%nat -> (k <= r)%nat -> rcl l0 (mh k) (Smom c k xs)) ->
    forall T,
      (forall k, (1 <= k)%nat -> (k <= r)%nat ->
         rcl (rad u r (/ 2) (2 * lu u) l0 T)
             (fiter rnd (alphah_n rnd g) T mh k)
             (Smom (fst (piter (alpha_n g) beta T (c, xs))) k
                   (snd (piter (alpha_n g) beta T (c, xs)))))
      /\ 0 < Smom (fst (piter (alpha_n g) beta T (c, xs))) 2
                  (snd (piter (alpha_n g) beta T (c, xs)))
      /\ 0 < fiter rnd (alphah_n rnd g) T mh 2%nat
      /\ 0 < rnd (sqrt (fiter rnd (alphah_n rnd g) T mh 2%nat)).
  Proof.
    intros g beta r l0 mh c xs Hr Hl0 Hg Hm2 Hm T.
    assert (Hmh : 0 < mh 2%nat).
    { destruct (Hm 2%nat ltac:(lia) ltac:(lia)) as [th [E [B _]]]. rewrite E.
      apply Rmult_lt_0_compat; [pose proof (exp_pos (- l0)); lra|exact Hm2]. }
    destruct (normalized_domain g T (smv c xs) mh Hg Hm2 Hmh)
      as [E1 [_ [F1 F2]]].
    split; [|split; [|split]].
    - intros k Hk1 Hkr.
      exact (normalized_ensemble_bound u rnd g beta r l0 mh c xs Hu rnd_rel
               Hr Hl0 Hm T k Hk1 Hkr).
    - change (Smom (fst (piter (alpha_n g) beta T (c, xs))) 2
                   (snd (piter (alpha_n g) beta T (c, xs))))
        with (smv (fst (piter (alpha_n g) beta T (c, xs)))
                  (snd (piter (alpha_n g) beta T (c, xs))) 2%nat).
      rewrite (eiter_is_particles 2 (alpha_n g)); [exact E1|].
      intros m m' H. unfold alpha_n. rewrite (H 2%nat) by lia. reflexivity.
    - exact F1.
    - exact F2.
  Qed.

End NormalizedRounded.

(* The particle iteration keeps the particle count (audit RF-5).         *)
Lemma map_length_R : forall (f : R -> R) (xs : list R),
  length (map f xs) = length xs.
Proof. intros f xs. induction xs as [|x xs IH]; simpl; [reflexivity|]. lia. Qed.

Lemma piter_length : forall alpha beta T c xs,
  length (snd (piter alpha beta T (c, xs))) = length xs.
Proof.
  intros alpha beta T. induction T as [|T IH]; intros c xs; [reflexivity|].
  cbn [piter]. destruct (pstep alpha beta (c, xs)) as [c' xs'] eqn:E.
  rewrite IH. unfold pstep in E. cbn [fst snd] in E. injection E as _ E2.
  subst xs'. apply map_length_R.
Qed.
