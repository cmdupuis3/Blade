(* ===================================================================== *)
(* reals/BladeShiftedRounding.v -- EXACT RECURRENCE REDUCTION: the error *)
(* bound that makes the floating-point reduction a contract.             *)
(*                                                                       *)
(*       THIS FILE IS NOT PART OF THE AXIOM-FREE TOWER.                  *)
(*                                                                       *)
(* Coq's standard-library real numbers (sig_forall_dec,                  *)
(* functional_extensionality_dep).  Own directory, own _CoqProject, not  *)
(* counted.  The standard model of rounded arithmetic -- every operation *)
(* exact up to relative error u, no underflow, no overflow -- is a       *)
(* HYPOTHESIS of the section, not an axiom.                              *)
(*                                                                       *)
(*   prun, crun, mex,          exact runs driven by a word of            *)
(*   mex_is_shifted_sum        coefficient pairs (a_t, b_t); the exact   *)
(*                             reduced run IS the shifted sums of the    *)
(*                             true particle array about the propagated  *)
(*                             shift (BladeShiftedMoments.smom_aff at    *)
(*                             R);                                       *)
(*   crun_is_mean              started at the mean, the propagated shift *)
(*                             is the true mean at every step;           *)
(*   rc, Ee, rc_rnd, rc_mul,   relative error, composed through rounding *)
(*   fpow_rc                   and products; a^k by repeated             *)
(*                             multiplication;                           *)
(*   mfl, mfl_rc               the FLOAT reduced run,                    *)
(*                             M <- fl(fl(a^k) * M);                     *)
(*   shifted_moment_float_bound       THE FLOAT CONTRACT: from a start   *)
(*                             known to relative error e0, after |w|     *)
(*                             steps the float M_k is within relative    *)
(*                             error (1 + e0)(1 + u)^(k |w|) - 1 of the  *)
(*                             true shifted sum -- independent of N, of  *)
(*                             the spread, of any condition number;      *)
(*   central_moment_float_bound       the same against the TRUE CENTRAL  *)
(*                             moment when started at the mean;          *)
(*   shifted_moment_gamma_bound       the radius in its familiar form,   *)
(*                             n u / (1 - n u) with n = k |w|.           *)
(*                                                                       *)
(* Scope, stated once.  Coefficients come from the INPUT, shared by the  *)
(* float and the exact run.  Coefficients computed from the rounded      *)
(* summary feed its error back, and need a Lipschitz hypothesis on the   *)
(* coefficient functions -- not done.  The shift itself is computed like *)
(* one particle of the original program; its error is that particle's,   *)
(* and the M_k are about the exactly propagated shift.                   *)
(* ===================================================================== *)

From Blade Require Import BladeBinomial BladeSummary BladeMomentClosure
  BladeShiftedMoments.
Require Import Reals Lra List Lia.
Import ListNotations.
Open Scope R_scope.

Local Notation Aff := (aff R Rplus Rmult).
Local Notation Smom := (smom R 0 1 Rplus Rmult Rminus).
Local Notation Psum := (psum R 0 1 Rplus Rmult).

Lemma rpow_R : forall a k, rpow R 1 Rmult a k = a ^ k.
Proof.
  intros a k. induction k as [|k IH]; simpl; [reflexivity|].
  rewrite IH. ring.
Qed.

Lemma ofnat_INR : forall n, ofnat R 0 1 Rplus n = INR n.
Proof.
  induction n as [|n IH]; [reflexivity|]. cbn [ofnat]. rewrite IH, S_INR. ring.
Qed.

(* ===================================================================== *)
(* Part A.  Exact runs, driven by a word of coefficient pairs (a_t, b_t) *)
(* shared by both programs -- coefficients that come from the INPUT.     *)
(* ===================================================================== *)

Fixpoint prun (w : list (R * R)) (xs : list R) : list R :=
  match w with [] => xs | (a, b) :: w' => prun w' (map (Aff a b) xs) end.

Fixpoint crun (w : list (R * R)) (c : R) : R :=
  match w with [] => c | (a, b) :: w' => crun w' (Aff a b c) end.

Fixpoint mex (k : nat) (w : list (R * R)) (M : R) : R :=
  match w with [] => M | (a, _) :: w' => mex k w' (a ^ k * M) end.

(* The exact reduced run IS the shifted sums of the true particle array  *)
(* about the propagated shift -- BladeShiftedMoments.smom_aff at R.      *)
Theorem mex_is_shifted_sum : forall k w c xs,
  Smom (crun w c) k (prun w xs) = mex k w (Smom c k xs).
Proof.
  intros k. induction w as [|[a b] w IH]; intros c xs; [reflexivity|].
  cbn [crun prun mex]. rewrite IH.
  rewrite (smom_aff R 0 1 Rplus Rmult Rminus Ropp RTheory), rpow_R.
  reflexivity.
Qed.

Lemma prun_length : forall w xs, length (prun w xs) = length xs.
Proof.
  induction w as [|[a b] w IH]; intros xs; [reflexivity|].
  cbn [prun]. rewrite IH, map_length. reflexivity.
Qed.

(* Started at the mean, the propagated shift IS the mean, at every step. *)
Theorem crun_is_mean : forall w c xs,
  Psum 1 xs = INR (length xs) * c ->
  Psum 1 (prun w xs) = INR (length xs) * crun w c.
Proof.
  induction w as [|[a b] w IH]; intros c xs H; [exact H|].
  cbn [prun crun].
  replace (INR (length xs)) with (INR (length (map (Aff a b) xs)))
    by (rewrite map_length; reflexivity).
  apply IH. rewrite map_length, <- ofnat_INR.
  apply (mean_tracked R 0 1 Rplus Rmult Rminus Ropp RTheory).
  rewrite ofnat_INR. exact H.
Qed.

(* ===================================================================== *)
(* Part B.  Rounded arithmetic, the standard model as a HYPOTHESIS.      *)
(* ===================================================================== *)

Section Rounding.
  Variable u : R.
  Hypothesis Hu : 0 <= u.
  Variable rnd : R -> R.
  Hypothesis rnd_rel : forall x, Rabs (rnd x - x) <= u * Rabs x.

  (* xh approximates x with RELATIVE error e.                            *)
  Definition rc (e xh x : R) : Prop := Rabs (xh - x) <= e * Rabs x.

  (* (1 + e0)(1 + u)^n - 1: an initial error e0, then n roundings.       *)
  Definition Ee (e0 : R) (n : nat) : R := (1 + e0) * (1 + u) ^ n - 1.

  Lemma pow_ge1 : forall n, 1 <= (1 + u) ^ n.
  Proof.
    induction n as [|n IH]; simpl; [lra|].
    assert (1 * 1 <= (1 + u) * (1 + u) ^ n)
      by (apply Rmult_le_compat; lra). lra.
  Qed.

  Lemma Ee_nonneg : forall e0 n, 0 <= e0 -> 0 <= Ee e0 n.
  Proof.
    intros e0 n He. unfold Ee. pose proof (pow_ge1 n).
    assert (1 * 1 <= (1 + e0) * (1 + u) ^ n)
      by (apply Rmult_le_compat; lra). lra.
  Qed.

  Lemma rc_rnd : forall e0 n xh x, 0 <= e0 ->
    rc (Ee e0 n) xh x -> rc (Ee e0 (S n)) (rnd xh) x.
  Proof.
    intros e0 n xh x He H. unfold rc in *.
    pose proof (Ee_nonneg e0 n He) as Hn.
    replace (rnd xh - x) with ((rnd xh - xh) + (xh - x)) by ring.
    pose proof (Rabs_triang (rnd xh - xh) (xh - x)) as T.
    pose proof (rnd_rel xh) as Hr.
    assert (Hxh : Rabs xh <= (1 + Ee e0 n) * Rabs x).
    { replace xh with ((xh - x) + x) by ring.
      pose proof (Rabs_triang (xh - x) x). lra. }
    assert (u * Rabs xh <= u * ((1 + Ee e0 n) * Rabs x))
      by (apply Rmult_le_compat_l; lra).
    assert (E : Ee e0 (S n) = u * (1 + Ee e0 n) + Ee e0 n)
      by (unfold Ee; simpl; ring).
    rewrite E. pose proof (Rabs_pos x). nra.
  Qed.

  Lemma rc_mul : forall e1 e2 yh y zh z, 0 <= e1 -> 0 <= e2 ->
    rc e1 yh y -> rc e2 zh z -> rc ((1 + e1) * (1 + e2) - 1) (yh * zh) (y * z).
  Proof.
    intros e1 e2 yh y zh z H1 H2 Hy Hz. unfold rc in *.
    replace (yh * zh - y * z) with ((yh - y) * zh + y * (zh - z)) by ring.
    pose proof (Rabs_triang ((yh - y) * zh) (y * (zh - z))) as T.
    rewrite !Rabs_mult in T. rewrite Rabs_mult.
    assert (Hzh : Rabs zh <= (1 + e2) * Rabs z).
    { replace zh with ((zh - z) + z) by ring.
      pose proof (Rabs_triang (zh - z) z). lra. }
    pose proof (Rabs_pos y). pose proof (Rabs_pos z). pose proof (Rabs_pos zh).
    pose proof (Rabs_pos (yh - y)). pose proof (Rabs_pos (zh - z)).
    assert (A : Rabs (yh - y) * Rabs zh <= (e1 * Rabs y) * ((1 + e2) * Rabs z))
      by (apply Rmult_le_compat; lra).
    assert (B : Rabs y * Rabs (zh - z) <= Rabs y * (e2 * Rabs z))
      by (apply Rmult_le_compat_l; lra).
    nra.
  Qed.

  Lemma Ee_mul : forall e0 m n,
    (1 + Ee 0 m) * (1 + Ee e0 n) - 1 = Ee e0 (m + n).
  Proof. intros e0 m n. unfold Ee. rewrite pow_add. ring. Qed.

  (* a^k by repeated multiplication: k - 1 roundings.                    *)
  Fixpoint fpow (a : R) (k : nat) : R :=
    match k with
    | O => 1
    | S O => a
    | S k' => rnd (a * fpow a k')
    end.

  Lemma fpow_rc : forall a k, rc (Ee 0 (k - 1)) (fpow a k) (a ^ k).
  Proof.
    intros a. induction k as [|k IH].
    - unfold rc, Ee. simpl. rewrite Rminus_diag, Rabs_R0. lra.
    - destruct k as [|k].
      + unfold rc, Ee. simpl. replace (a - a * 1) with 0 by ring.
        rewrite Rabs_R0. lra.
      + change (fpow a (S (S k))) with (rnd (a * fpow a (S k))).
        replace (S (S k) - 1)%nat with (S k) by lia.
        apply rc_rnd; [lra|].
        replace (S k - 1)%nat with k in IH by lia.
        pose proof (rc_mul 0 (Ee 0 k) a a (fpow a (S k)) (a ^ S k)
                      ltac:(lra) (Ee_nonneg 0 k ltac:(lra))) as M.
        replace ((1 + 0) * (1 + Ee 0 k) - 1) with (Ee 0 k) in M
          by (unfold Ee; ring).
        change (a ^ S (S k)) with (a * a ^ S k). apply M; [|exact IH].
        unfold rc. rewrite Rminus_diag, Rabs_R0. lra.
  Qed.

  (* The FLOAT reduced run: M <- fl(fl(a^k) * M), k roundings a step.    *)
  Fixpoint mfl (k : nat) (w : list (R * R)) (M : R) : R :=
    match w with
    | [] => M
    | (a, _) :: w' => mfl k w' (rnd (fpow a k * M))
    end.

  Theorem mfl_rc : forall k w e0 n Mh M, (1 <= k)%nat -> 0 <= e0 ->
    rc (Ee e0 n) Mh M -> rc (Ee e0 (n + k * length w)) (mfl k w Mh) (mex k w M).
  Proof.
    intros k. induction w as [|[a b] w IH]; intros e0 n Mh M Hk He H.
    - cbn [mfl mex length]. rewrite Nat.mul_0_r, Nat.add_0_r. exact H.
    - cbn [mfl mex length].
      replace (n + k * S (length w))%nat with ((n + k) + k * length w)%nat
        by nia.
      apply IH; [exact Hk|exact He|].
      replace (n + k)%nat with (S ((k - 1) + n)) by lia.
      apply rc_rnd; [exact He|].
      rewrite <- Ee_mul.
      apply rc_mul; [apply Ee_nonneg; lra|apply Ee_nonneg; exact He| |exact H].
      apply fpow_rc.
  Qed.

  (* THE FLOAT CONTRACT.  Run the reduced program in rounded arithmetic, *)
  (* from a k-th shifted sum known to relative error e0.  After |w|      *)
  (* steps it is within relative error (1 + e0)(1 + u)^(k |w|) - 1 of    *)
  (* the k-th shifted sum of the TRUE particle array about the           *)
  (* propagated shift.  No N, no spread, no condition number: the bound  *)
  (* is relative to the quantity itself.                                 *)
  Theorem shifted_moment_float_bound : forall k w c xs Mh e0,
    (1 <= k)%nat -> 0 <= e0 ->
    rc e0 Mh (Smom c k xs) ->
    rc ((1 + e0) * (1 + u) ^ (k * length w) - 1)
       (mfl k w Mh) (Smom (crun w c) k (prun w xs)).
  Proof.
    intros k w c xs Mh e0 Hk He H.
    rewrite mex_is_shifted_sum.
    pose proof (mfl_rc k w e0 0 Mh (Smom c k xs) Hk He) as B.
    unfold Ee in B. rewrite Nat.add_0_l in B. apply B.
    unfold Ee. simpl. replace ((1 + e0) * 1 - 1) with e0 by ring. exact H.
  Qed.

  (* Started at the mean: the bound is on the TRUE CENTRAL moment of the *)
  (* true particle array, and the propagated shift is its true mean.     *)
  Corollary central_moment_float_bound : forall k w c xs Mh e0,
    (1 <= k)%nat -> 0 <= e0 ->
    Psum 1 xs = INR (length xs) * c ->
    rc e0 Mh (Smom c k xs) ->
    rc ((1 + e0) * (1 + u) ^ (k * length w) - 1)
       (mfl k w Mh) (Smom (crun w c) k (prun w xs))
    /\ Psum 1 (prun w xs) = INR (length (prun w xs)) * crun w c.
  Proof.
    intros k w c xs Mh e0 Hk He Hm H. split.
    - apply shifted_moment_float_bound; assumption.
    - rewrite prun_length. apply crun_is_mean. exact Hm.
  Qed.

  (* The familiar radius: (1 + u)^n - 1 <= n u / (1 - n u).              *)
  Lemma pow_le_gamma : forall n, INR n * u < 1 ->
    (1 + u) ^ n * (1 - INR n * u) <= 1.
  Proof.
    induction n as [|n IH]; intro Hn.
    - simpl. lra.
    - rewrite S_INR in *. simpl.
      assert (Hn' : INR n * u < 1) by nra.
      specialize (IH Hn').
      assert (Hp : 0 <= (1 + u) ^ n) by (apply pow_le; lra).
      pose proof (pos_INR n).
      assert ((1 + u) * (1 - (INR n + 1) * u) <= 1 - INR n * u) by nra.
      assert (0 <= 1 - (INR n + 1) * u) by lra.
      nra.
  Qed.

  Corollary shifted_moment_gamma_bound : forall k w c xs Mh,
    (1 <= k)%nat -> INR (k * length w) * u < 1 ->
    Mh = Smom c k xs ->
    Rabs (mfl k w Mh - Smom (crun w c) k (prun w xs))
    <= INR (k * length w) * u / (1 - INR (k * length w) * u)
       * Rabs (Smom (crun w c) k (prun w xs)).
  Proof.
    intros k w c xs Mh Hk Hg HM.
    pose proof (shifted_moment_float_bound k w c xs Mh 0 Hk ltac:(lra))
      as B.
    assert (H0 : rc 0 Mh (Smom c k xs))
      by (unfold rc; rewrite HM, Rminus_diag, Rabs_R0; lra).
    specialize (B H0). unfold rc in B.
    replace ((1 + 0) * (1 + u) ^ (k * length w) - 1)
      with ((1 + u) ^ (k * length w) - 1) in B by ring.
    set (n := (k * length w)%nat) in *.
    pose proof (pow_le_gamma n Hg) as P.
    assert (Hd : 0 < 1 - INR n * u) by lra.
    assert (G : (1 + u) ^ n - 1 <= INR n * u / (1 - INR n * u)).
    { apply (Rmult_le_reg_r (1 - INR n * u)); [exact Hd|].
      unfold Rdiv. rewrite Rmult_assoc, Rinv_l by lra. nra. }
    pose proof (Rabs_pos (Smom (crun w c) k (prun w xs))).
    eapply Rle_trans; [exact B|]. apply Rmult_le_compat_r; lra.
  Qed.

End Rounding.
