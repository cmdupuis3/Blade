(* ===================================================================== *)
(* reals/BladeRoundingBound.v -- EXACT RECURRENCE REDUCTION, the third   *)
(* numerical contract of section 11.2 of the draft: a finite-precision   *)
(* error theorem.                                                        *)
(*                                                                       *)
(*       THIS FILE IS NOT PART OF THE AXIOM-FREE TOWER.                  *)
(*                                                                       *)
(* Coq's standard-library real numbers (sig_forall_dec,                  *)
(* functional_extensionality_dep).  Own directory, own _CoqProject, not  *)
(* counted.                                                              *)
(*                                                                       *)
(* The standard model of rounded arithmetic -- every operation returns   *)
(* the exact result with relative error at most u, no underflow, no      *)
(* overflow -- is a HYPOTHESIS of the section, not an axiom.             *)
(*                                                                       *)
(*   g, g_le_gamma             g n = (1 + u)^n - 1, at most              *)
(*                             n u / (1 - n u);                          *)
(*   fsum_error                recursive summation: within               *)
(*                             g n * sum |x_i| of the exact sum;         *)
(*   original_error,           the particle-wise fold of a x_i + b and   *)
(*   reduced_error             the reduced form a S + N b each lie       *)
(*                             within g (N + 2) * sum (|a| |x_i| + |b|)  *)
(*                             of the exact value -- the SAME radius     *)
(*                             against the SAME condition number;        *)
(*   reduction_rounded_distance   so the two programs differ by at most  *)
(*                             twice that.                               *)
(*                                                                       *)
(* Scope, stated once.  Rank one only (the S update); the Q update and   *)
(* general r are not done.  The bound says the reformulation is no worse *)
(* conditioned than the original; it does not make them bitwise equal -- *)
(* BladeNumericContract and floats/BladeBinary64Witness show they are    *)
(* not.                                                                  *)
(* ===================================================================== *)

Require Import Reals List Lra Lia.
Import ListNotations.
Open Scope R_scope.

(* ===================================================================== *)
(* The standard model of rounded arithmetic: every operation returns the *)
(* exact result with relative error at most u.  No underflow, no         *)
(* overflow.  The model is a HYPOTHESIS of this section, not an axiom.   *)
(* ===================================================================== *)

Section Rounding.
  Variable u : R.
  Hypothesis Hu : 0 <= u.
  Variable rnd : R -> R.
  Hypothesis rnd_rel : forall x, Rabs (rnd x - x) <= u * Rabs x.

  (* g n = (1 + u)^n - 1: the growth of n nested rounded operations.     *)
  Definition g (n : nat) : R := (1 + u) ^ n - 1.

  Lemma g_S : forall n, g (S n) = (1 + u) * g n + u.
  Proof. intro n. unfold g. simpl. ring. Qed.

  Lemma g_nonneg : forall n, 0 <= g n.
  Proof.
    induction n as [|n IH]; [unfold g; simpl; lra|].
    rewrite g_S. nra.
  Qed.

  Lemma g_le_S : forall n, g n <= g (S n).
  Proof. intro n. rewrite g_S. pose proof (g_nonneg n). nra. Qed.

  Lemma g_mono : forall n k, g n <= g (n + k).
  Proof.
    intros n. induction k as [|k IH].
    - rewrite Nat.add_0_r. lra.
    - rewrite Nat.add_succ_r. pose proof (g_le_S (n + k)). lra.
  Qed.

  Lemma g_1 : g 1 = u.
  Proof. unfold g. simpl. ring. Qed.

  Lemma rnd_abs : forall x, Rabs (rnd x) <= (1 + u) * Rabs x.
  Proof.
    intro x. pose proof (rnd_rel x) as H.
    replace (rnd x) with ((rnd x - x) + x) by ring.
    pose proof (Rabs_triang (rnd x - x) x). lra.
  Qed.

  (* One rounded operation applied to approximate operands.              *)
  Lemma rnd_step : forall xh x e c, Rabs (xh - x) <= e -> Rabs x <= c ->
    Rabs (rnd xh - x) <= u * c + (1 + u) * e.
  Proof.
    intros xh x e c He Hc.
    replace (rnd xh - x) with ((rnd xh - xh) + (xh - x)) by ring.
    pose proof (Rabs_triang (rnd xh - xh) (xh - x)) as T.
    pose proof (rnd_rel xh) as Hr.
    assert (Hxh : Rabs xh <= c + e).
    { replace xh with ((xh - x) + x) by ring.
      pose proof (Rabs_triang (xh - x) x). lra. }
    assert (u * Rabs xh <= u * (c + e)) by (apply Rmult_le_compat_l; lra).
    lra.
  Qed.

  Fixpoint Rsum (l : list R) : R :=
    match l with [] => 0 | x :: l' => x + Rsum l' end.

  Definition Rasum (l : list R) : R := Rsum (map Rabs l).

  Fixpoint fsum (l : list R) : R :=
    match l with [] => 0 | x :: l' => rnd (x + fsum l') end.

  Lemma Rasum_nonneg : forall l, 0 <= Rasum l.
  Proof.
    induction l as [|x l IH]; unfold Rasum in *; simpl; [lra|].
    pose proof (Rabs_pos x). lra.
  Qed.

  Lemma Rsum_abs : forall l, Rabs (Rsum l) <= Rasum l.
  Proof.
    induction l as [|x l IH]; unfold Rasum in *; simpl.
    - rewrite Rabs_R0. lra.
    - pose proof (Rabs_triang x (Rsum l)). lra.
  Qed.

  (* Recursive summation: the classical bound.                           *)
  Theorem fsum_error : forall l,
    Rabs (fsum l - Rsum l) <= g (length l) * Rasum l.
  Proof.
    induction l as [|x l IH].
    - simpl. unfold Rasum. simpl. rewrite Rminus_0_r, Rabs_R0. lra.
    - cbn [fsum Rsum length]. unfold Rasum in *. cbn [map Rsum].
      fold (Rasum l) in *.
      pose proof (rnd_step (x + fsum l) (x + Rsum l)
                    (g (length l) * Rasum l) (Rabs x + Rasum l)) as St.
      assert (H1 : Rabs (x + fsum l - (x + Rsum l))
                   <= g (length l) * Rasum l).
      { replace (x + fsum l - (x + Rsum l)) with (fsum l - Rsum l) by ring.
        exact IH. }
      assert (H2 : Rabs (x + Rsum l) <= Rabs x + Rasum l).
      { pose proof (Rabs_triang x (Rsum l)). pose proof (Rsum_abs l). lra. }
      specialize (St H1 H2). rewrite g_S.
      pose proof (g_nonneg (length l)) as Hg. pose proof (Rasum_nonneg l).
      pose proof (Rabs_pos x) as Hx.
      assert (0 <= (1 + u) * (g (length l) * Rabs x)).
      { apply Rmult_le_pos; [lra|]. apply Rmult_le_pos; assumption. }
      lra.
  Qed.

  (* ------------------------------------------------------------------- *)
  (* The shared affine update and its sum, both ways.                    *)
  (* ------------------------------------------------------------------- *)

  Variables a b : R.

  Definition cond (xs : list R) : R :=
    Rsum (map (fun x => Rabs a * Rabs x + Rabs b) xs).

  Definition exact (xs : list R) : R := Rsum (map (fun x => a * x + b) xs).

  (* The original: update every particle, then fold.                     *)
  Definition origf (xs : list R) : R :=
    fsum (map (fun x => rnd (rnd (a * x) + b)) xs).

  (* The reduced form: S' = a S + N b.                                   *)
  Definition redf (xs : list R) : R :=
    rnd (rnd (a * fsum xs) + rnd (INR (length xs) * b)).

  Lemma cond_nonneg : forall xs, 0 <= cond xs.
  Proof.
    induction xs as [|x xs IH]; unfold cond in *; simpl; [lra|].
    pose proof (Rabs_pos a). pose proof (Rabs_pos x). pose proof (Rabs_pos b).
    nra.
  Qed.

  Lemma exact_abs : forall xs, Rabs (exact xs) <= cond xs.
  Proof.
    induction xs as [|x xs IH]; unfold exact, cond in *; simpl.
    - rewrite Rabs_R0. lra.
    - pose proof (Rabs_triang (a * x + b) (Rsum (map (fun x => a * x + b) xs))).
      pose proof (Rabs_triang (a * x) b). rewrite Rabs_mult in *. lra.
  Qed.

  Lemma exact_closed : forall xs,
    exact xs = a * Rsum xs + INR (length xs) * b.
  Proof.
    induction xs as [|x xs IH]; unfold exact in *.
    - simpl. ring.
    - cbn [map Rsum length]. rewrite IH, S_INR. ring.
  Qed.

  Lemma cond_closed : forall xs,
    cond xs = Rabs a * Rasum xs + INR (length xs) * Rabs b.
  Proof.
    induction xs as [|x xs IH]; unfold cond, Rasum in *.
    - simpl. ring.
    - cbn [map Rsum length]. rewrite IH, S_INR. ring.
  Qed.

  Lemma term_error : forall x,
    Rabs (rnd (rnd (a * x) + b) - (a * x + b))
    <= g 2 * (Rabs a * Rabs x + Rabs b).
  Proof.
    intro x.
    pose proof (rnd_step (rnd (a * x) + b) (a * x + b)
                  (u * (Rabs a * Rabs x)) (Rabs a * Rabs x + Rabs b)) as St.
    assert (H1 : Rabs (rnd (a * x) + b - (a * x + b))
                 <= u * (Rabs a * Rabs x)).
    { replace (rnd (a * x) + b - (a * x + b)) with (rnd (a * x) - a * x)
        by ring.
      pose proof (rnd_rel (a * x)) as H. rewrite Rabs_mult in H. exact H. }
    assert (H2 : Rabs (a * x + b) <= Rabs a * Rabs x + Rabs b).
    { pose proof (Rabs_triang (a * x) b). rewrite Rabs_mult in *. lra. }
    specialize (St H1 H2). unfold g. simpl.
    pose proof (Rabs_pos a). pose proof (Rabs_pos x). pose proof (Rabs_pos b).
    assert (0 <= Rabs a * Rabs x) by nra. nra.
  Qed.

  Theorem original_error : forall xs,
    Rabs (origf xs - exact xs) <= g (length xs + 2) * cond xs.
  Proof.
    induction xs as [|x xs IH]; unfold origf, exact, cond in *.
    - simpl. rewrite Rminus_0_r, Rabs_R0. lra.
    - cbn [map fsum Rsum length].
      set (th := rnd (rnd (a * x) + b)).
      set (oh := fsum (map (fun x0 => rnd (rnd (a * x0) + b)) xs)) in *.
      set (O := Rsum (map (fun x0 => a * x0 + b) xs)) in *.
      set (B := Rsum (map (fun x0 => Rabs a * Rabs x0 + Rabs b) xs)) in *.
      set (c := Rabs a * Rabs x + Rabs b).
      pose proof (term_error x) as Ht. fold th c in Ht.
      pose proof (rnd_step (th + oh) (a * x + b + O)
                    (g 2 * c + g (length xs + 2) * B) (c + B)) as St.
      assert (H1 : Rabs (th + oh - (a * x + b + O))
                   <= g 2 * c + g (length xs + 2) * B).
      { replace (th + oh - (a * x + b + O))
          with ((th - (a * x + b)) + (oh - O)) by ring.
        pose proof (Rabs_triang (th - (a * x + b)) (oh - O)). lra. }
      assert (H2 : Rabs (a * x + b + O) <= c + B).
      { pose proof (Rabs_triang (a * x + b) O).
        pose proof (Rabs_triang (a * x) b). rewrite Rabs_mult in *.
        pose proof (exact_abs xs) as He. unfold exact, cond in He.
        fold O B in He. unfold c. lra. }
      specialize (St H1 H2).
      replace (S (length xs) + 2)%nat with (S (length xs + 2)) by lia.
      rewrite g_S.
      pose proof (g_mono 2 (length xs)) as Hm.
      replace (2 + length xs)%nat with (length xs + 2)%nat in Hm by lia.
      pose proof (g_nonneg 2). pose proof (g_nonneg (length xs + 2)).
      pose proof (cond_nonneg xs) as HB. unfold cond in HB. fold B in HB.
      assert (Hc : 0 <= c).
      { unfold c. pose proof (Rabs_pos a). pose proof (Rabs_pos x).
        pose proof (Rabs_pos b). nra. }
      assert ((1 + u) * (g 2 * c) <= (1 + u) * (g (length xs + 2) * c)).
      { apply Rmult_le_compat_l; [lra|].
        apply Rmult_le_compat_r; assumption. }
      lra.
  Qed.

  Theorem reduced_error : forall xs,
    Rabs (redf xs - exact xs) <= g (length xs + 2) * cond xs.
  Proof.
    intro xs. unfold redf. rewrite exact_closed, cond_closed.
    set (n := length xs). set (X := Rasum xs). set (S0 := Rsum xs).
    pose proof (fsum_error xs) as Hs. fold n X S0 in Hs.
    pose proof (Rsum_abs xs) as HS. fold X S0 in HS.
    pose proof (Rasum_nonneg xs) as HX. fold X in HX.
    pose proof (g_nonneg n) as Hgn. pose proof (Rabs_pos a) as Ha.
    pose proof (Rabs_pos b) as Hb. pose proof (pos_INR n) as Hn.
    (* the product a * S *)
    pose proof (rnd_step (a * fsum xs) (a * S0)
                  (Rabs a * (g n * X)) (Rabs a * X)) as Sp.
    assert (P1 : Rabs (a * fsum xs - a * S0) <= Rabs a * (g n * X)).
    { replace (a * fsum xs - a * S0) with (a * (fsum xs - S0)) by ring.
      rewrite Rabs_mult. apply Rmult_le_compat_l; lra. }
    assert (P2 : Rabs (a * S0) <= Rabs a * X).
    { rewrite Rabs_mult. apply Rmult_le_compat_l; lra. }
    specialize (Sp P1 P2).
    (* the product N * b *)
    pose proof (rnd_rel (INR n * b)) as Sn.
    rewrite Rabs_mult, (Rabs_pos_eq (INR n)) in Sn by exact Hn.
    (* the final sum *)
    set (ph := rnd (a * fsum xs)) in *. set (nh := rnd (INR n * b)) in *.
    pose proof (rnd_step (ph + nh) (a * S0 + INR n * b)
                  (g (S n) * (Rabs a * X + INR n * Rabs b))
                  (Rabs a * X + INR n * Rabs b)) as Sf.
    assert (F1 : Rabs (ph + nh - (a * S0 + INR n * b))
                 <= g (S n) * (Rabs a * X + INR n * Rabs b)).
    { replace (ph + nh - (a * S0 + INR n * b))
        with ((ph - a * S0) + (nh - INR n * b)) by ring.
      pose proof (Rabs_triang (ph - a * S0) (nh - INR n * b)) as T.
      rewrite g_S.
      assert (0 <= (1 + u) * (g n * (INR n * Rabs b)))
        by (repeat apply Rmult_le_pos; lra).
      lra. }
    assert (F2 : Rabs (a * S0 + INR n * b) <= Rabs a * X + INR n * Rabs b).
    { pose proof (Rabs_triang (a * S0) (INR n * b)) as T.
      rewrite (Rabs_mult (INR n) b), (Rabs_pos_eq (INR n)) in T by exact Hn.
      lra. }
    specialize (Sf F1 F2).
    replace (n + 2)%nat with (S (S n)) by lia. rewrite (g_S (S n)).
    lra.
  Qed.

  (* THE ROUNDED CONTRACT.  The two programs are not bitwise equal, but  *)
  (* they sit inside the same error ball around the exact value, whose   *)
  (* radius is the usual one for a sum of N + 2 rounded operations       *)
  (* against the SAME condition number  sum (|a| |x_i| + |b|).           *)
  Theorem reduction_rounded_distance : forall xs,
    Rabs (origf xs - redf xs) <= 2 * g (length xs + 2) * cond xs.
  Proof.
    intro xs.
    replace (origf xs - redf xs)
      with ((origf xs - exact xs) - (redf xs - exact xs)) by ring.
    pose proof (Rabs_triang (origf xs - exact xs)
                  (- (redf xs - exact xs))) as T.
    rewrite Rabs_Ropp in T.
    pose proof (original_error xs). pose proof (reduced_error xs).
    unfold Rminus in *. lra.
  Qed.

  (* The familiar form of the radius: gamma_n = n u / (1 - n u).         *)
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

  Theorem g_le_gamma : forall n, INR n * u < 1 ->
    g n <= INR n * u / (1 - INR n * u).
  Proof.
    intros n Hn. pose proof (pow_le_gamma n Hn) as H. unfold g.
    assert (Hd : 0 < 1 - INR n * u) by lra.
    apply (Rmult_le_reg_r (1 - INR n * u)); [exact Hd|].
    unfold Rdiv. rewrite Rmult_assoc, Rinv_l by lra. lra.
  Qed.

End Rounding.
