(* ===================================================================== *)
(* BladeShiftedMoments.v -- EXACT RECURRENCE REDUCTION in the            *)
(* coordinates a floating-point implementation needs: shifted power sums *)
(* M_k(c) = sum_i (x_i - c)^k about a tracked shift c.                   *)
(*                                                                       *)
(* BladeMomentClosure reduces the affine particle fragment to raw power  *)
(* sums.  That is exact over any ring and useless in floating point: raw *)
(* moments cancel catastrophically (floats/BladeBinary64Witness.v, Part  *)
(* D: variance 2 instead of 2/3).  Shifted sums carry the same           *)
(* information and make the update a pure rescaling.                     *)
(*                                                                       *)
(*   smom, smom_aff            THE UPDATE: move the shift like a         *)
(*                             particle, c' = a c + b, and M_k' = a^k    *)
(*                             M_k -- no b, no c, no N; over any         *)
(*                             commutative ring, no division;            *)
(*   raw_from_shifted,         the same information as the raw power     *)
(*                             sums,                                     *)
(*   shifted_from_raw          in both directions (binomial expansions), *)
(*                             so every refusal and lower bound of the   *)
(*                             raw summary applies unchanged;            *)
(*   central_preserved,        started at the mean (M_1 = 0), the shift  *)
(*   mean_tracked              stays the mean: central moments are an    *)
(*                             invariant of the reduced run;             *)
(*   variance_numerator_shift_free,   N M_2 - M_1^2 = N Q - S^2 for      *)
(*   variance_numerator_aff,   EVERY shift, and an affine step scales    *)
(*   rho_invariant             it by a^2 -- as it scales M_1^2, so       *)
(*                             rho = M_1^2 / (N M_2) is invariant (the   *)
(*                             fact reals/BladeShiftedVariance.v needs); *)
(*   qs, Gs, shifted_step,     THE CERTIFICATE for the ORIGINAL program  *)
(*                             of                                        *)
(*   shifted_certificate       BladeMomentClosure (coefficients reading  *)
(*                             the raw power sums, unchanged) with the   *)
(*                             shift as a GHOST beside it -- ANY         *)
(*                             starting shift, e.g. a rounded mean;      *)
(*   shifted_reduction_sound   every prefix observation of the raw       *)
(*                             moments of every finite execution agrees; *)
(*   shifted_run_is_shifted_sums      the reduced state after w IS the   *)
(*                             shifted sums of the true array about the  *)
(*                             propagated shift -- what the rounding     *)
(*                             analysis in reals/BladeShiftedRounding.v  *)
(*                             compares against.                         *)
(*                                                                       *)
(* Scope, stated once.  Exact arithmetic, as everywhere in this tower.   *)
(* The coefficients may depend on the input and on the raw moments (read *)
(* back from the reduced state by rawobs, which needs the extent N).     *)
(*                                                                       *)
(* Stdlib only, no axioms.  Imports BladeBinomial, BladeSummary,         *)
(* BladeMomentClosure.                                                   *)
(* ===================================================================== *)

From Blade Require Import BladeBinomial BladeSummary BladeMomentClosure.
Require Import List Arith Lia Ring.
Import ListNotations.

(* ===================================================================== *)
(* Part A.  Shifted power sums  M_k(c) = sum_i (x_i - c)^k.              *)
(* ===================================================================== *)

Section Shifted.
  Variable R : Type.
  Variables (r0 r1 : R) (radd rmul rsub : R -> R -> R) (ropp : R -> R).
  Hypothesis Rth : ring_theory r0 r1 radd rmul rsub ropp (@eq R).
  Add Ring shifted_ring : Rth.

  Local Infix "+!" := radd (at level 50, left associativity).
  Local Infix "*!" := rmul (at level 40, left associativity).
  Local Infix "-!" := rsub (at level 50, left associativity).
  Local Notation Psum := (psum R r0 r1 radd rmul).
  Local Notation Rpow := (rpow R r1 rmul).
  Local Notation Rsum := (rsum R r0 radd).
  Local Notation Ofnat := (ofnat R r0 r1 radd).
  Local Notation Aff := (aff R radd rmul).
  Local Notation Qr := (qr R r0 r1 radd rmul).

  Definition smom (c : R) (k : nat) (xs : list R) : R :=
    Psum k (map (fun x => x -! c) xs).

  Lemma rpow_r1 : forall j, Rpow r1 j = r1.
  Proof.
    induction j as [|j IH]; cbn [rpow]; [reflexivity|]. rewrite IH. ring.
  Qed.

  (* THE UPDATE.  Move the shift with the particles and every            *)
  (* shifted sum is only rescaled: no b, no c, no N, nothing to          *)
  (* cancel.                                                             *)
  Theorem smom_aff : forall a b c k xs,
    smom (Aff a b c) k (map (Aff a b) xs) = Rpow a k *! smom c k xs.
  Proof.
    intros a b c k xs. unfold smom.
    rewrite <- (psum_scale R r0 r1 radd rmul rsub ropp Rth). f_equal.
    rewrite !map_map. apply map_ext. intro x. unfold aff. ring.
  Qed.

  Lemma smom_0 : forall c xs, smom c 0 xs = Ofnat (length xs).
  Proof.
    intros c xs. unfold smom.
    rewrite (psum_0 R r0 r1 radd rmul), map_length.
    reflexivity.
  Qed.

  Lemma smom_1 : forall c xs,
    smom c 1 xs = Psum 1 xs -! Ofnat (length xs) *! c.
  Proof.
    intros c. induction xs as [|x xs IH].
    - unfold smom, psum. cbn [map rsum length ofnat]. ring.
    - change (smom c 1 (x :: xs))
        with (Rpow (x -! c) 1 +! smom c 1 xs).
      rewrite IH. change (Psum 1 (x :: xs)) with (Rpow x 1 +! Psum 1 xs).
      cbn [rpow length ofnat]. ring.
  Qed.

  Lemma unshift : forall c xs, map (Aff r1 c) (map (fun x => x -! c) xs) = xs.
  Proof.
    intros c xs. rewrite map_map.
    transitivity (map (fun x => x) xs); [|apply map_id].
    apply map_ext. intro x. unfold aff. ring.
  Qed.

  (* The same information as the raw power sums, in both directions.     *)
  Theorem raw_from_shifted : forall c k xs,
    Psum k xs
    = Rsum (map (fun j => Ofnat (C k j) *! Rpow c (k - j) *! smom c j xs)
                (seq 0 (S k))).
  Proof.
    intros c k xs. rewrite <- (unshift c xs) at 1.
    rewrite (affine_moment_tower_closed R r0 r1 radd rmul rsub ropp Rth).
    f_equal. apply map_ext. intro j. unfold smom. rewrite rpow_r1. ring.
  Qed.

  Theorem shifted_from_raw : forall c k xs,
    smom c k xs
    = Rsum (map (fun j => Ofnat (C k j) *! Rpow (ropp c) (k - j)
                          *! Psum j xs)
                (seq 0 (S k))).
  Proof.
    intros c k xs. unfold smom.
    replace (map (fun x => x -! c) xs) with (map (Aff r1 (ropp c)) xs)
      by (apply map_ext; intro x; unfold aff; ring).
    rewrite (affine_moment_tower_closed R r0 r1 radd rmul rsub ropp Rth).
    f_equal. apply map_ext. intro j. rewrite rpow_r1. ring.
  Qed.

  (* The mean is the shift with M_1 = 0, and the update keeps it the     *)
  (* mean: central moments are an INVARIANT of the reduced run.          *)
  Corollary central_preserved : forall a b c xs,
    smom c 1 xs = r0 -> smom (Aff a b c) 1 (map (Aff a b) xs) = r0.
  Proof. intros a b c xs H. rewrite smom_aff, H. ring. Qed.

  Corollary mean_tracked : forall a b c xs,
    Psum 1 xs = Ofnat (length xs) *! c ->
    Psum 1 (map (Aff a b) xs) = Ofnat (length xs) *! Aff a b c.
  Proof.
    intros a b c xs H.
    assert (H1 : smom c 1 xs = r0) by (rewrite smom_1, H; ring).
    pose proof (central_preserved a b c xs H1) as H2.
    rewrite smom_1, map_length in H2.
    transitivity (Psum 1 (map (Aff a b) xs) -! Ofnat (length xs) *! Aff a b c
                  +! Ofnat (length xs) *! Aff a b c); [ring|].
    rewrite H2. ring.
  Qed.

  (* ------------------------------------------------------------------- *)
  (* The variance, exactly.  N M_2 - M_1^2 is N^2 times the variance:    *)
  (* it does not depend on the shift, and an affine step scales it by    *)
  (* a^2 -- as it scales M_1^2, so rho = M_1^2 / (N M_2) is INVARIANT.   *)
  (* ------------------------------------------------------------------- *)

  Lemma smom_2_expand : forall c xs,
    smom c 2 xs
    = Psum 2 xs -! (r1 +! r1) *! c *! Psum 1 xs +! Ofnat (length xs) *! c *! c.
  Proof.
    intros c. induction xs as [|x xs IH].
    - unfold smom, psum. cbn [map rsum length ofnat]. ring.
    - change (smom c 2 (x :: xs)) with (Rpow (x -! c) 2 +! smom c 2 xs).
      rewrite IH. change (Psum 2 (x :: xs)) with (Rpow x 2 +! Psum 2 xs).
      change (Psum 1 (x :: xs)) with (Rpow x 1 +! Psum 1 xs).
      cbn [rpow length ofnat]. ring.
  Qed.

  Theorem variance_numerator_shift_free : forall c xs,
    Ofnat (length xs) *! smom c 2 xs -! smom c 1 xs *! smom c 1 xs
    = Ofnat (length xs) *! Psum 2 xs -! Psum 1 xs *! Psum 1 xs.
  Proof. intros c xs. rewrite smom_2_expand, smom_1. ring. Qed.

  Theorem variance_numerator_aff : forall a b c xs,
    let xs' := map (Aff a b) xs in
    Ofnat (length xs') *! smom (Aff a b c) 2 xs'
      -! smom (Aff a b c) 1 xs' *! smom (Aff a b c) 1 xs'
    = a *! a
      *! (Ofnat (length xs) *! smom c 2 xs -! smom c 1 xs *! smom c 1 xs).
  Proof.
    intros a b c xs xs'. unfold xs'. rewrite map_length, !smom_aff.
    cbn [rpow]. ring.
  Qed.

  (* rho is invariant, in cross-multiplied form (no division).           *)
  Corollary rho_invariant : forall a b c xs,
    smom (Aff a b c) 1 (map (Aff a b) xs)
      *! smom (Aff a b c) 1 (map (Aff a b) xs) *! smom c 2 xs
    = smom c 1 xs *! smom c 1 xs
      *! smom (Aff a b c) 2 (map (Aff a b) xs).
  Proof. intros a b c xs. rewrite !smom_aff. cbn [rpow]. ring. Qed.

  (* ------------------------------------------------------------------- *)
  (* Part B.  THE CERTIFICATE.  The ORIGINAL program is the moment       *)
  (* fragment of BladeMomentClosure unchanged: coefficients read the raw *)
  (* power sums.  The shift c is a GHOST carried beside it -- any value, *)
  (* e.g. a rounded mean -- and the reduced state is                     *)
  (* (c, M_1 .. M_r).                                                    *)
  (* ------------------------------------------------------------------- *)

  Section Certificate.
    Variable U : Type.
    Variables r N : nat.
    Variables alpha beta : U -> list R -> R.

    Definition qs (st : R * list R) : list R :=
      fst st :: map (fun k => smom (fst st) k (snd st)) (seq 1 r).

    (* Raw power sums read back from the reduced state, at extent N.     *)
    Definition mz (z : list R) (j : nat) : R :=
      match j with O => Ofnat N | S _ => nth j z r0 end.

    Definition rawobs (z : list R) : list R :=
      map (fun k => Rsum (map (fun j => Ofnat (C k j) *! Rpow (hd r0 z) (k - j)
                                        *! mz z j)
                              (seq 0 (S k))))
          (seq 1 r).

    Lemma nth_qs : forall c xs j, 1 <= j -> j <= r ->
      nth j (qs (c, xs)) r0 = smom c j xs.
    Proof.
      intros c xs j H1 H2. destruct j as [|j]; [lia|]. unfold qs.
      cbn [nth fst snd].
      rewrite (nth_map_lt R nat _ (seq 1 r) j r0 0)
        by (rewrite seq_length; lia).
      rewrite seq_nth by lia. reflexivity.
    Qed.

    Theorem rawobs_qs : forall c xs, length xs = N ->
      rawobs (qs (c, xs)) = Qr r xs.
    Proof.
      intros c xs HN. unfold rawobs, qr. apply map_ext_in.
      intros k Hk. apply in_seq in Hk.
      rewrite (raw_from_shifted c k xs). f_equal. apply map_ext_in.
      intros j Hj. apply in_seq in Hj. f_equal.
      destruct j as [|j].
      - cbn [mz]. rewrite smom_0, HN. reflexivity.
      - cbn [mz]. rewrite nth_qs by lia. reflexivity.
    Qed.

    (* The original step, with the ghost shift moved like a particle.    *)
    Definition Fs (u : U) (st : R * list R) : R * list R :=
      let p := Qr r (snd st) in
      (Aff (alpha u p) (beta u p) (fst st),
       Fm R r0 r1 radd rmul U r alpha beta u (snd st)).

    (* The reduced step: r multiplications and one affine map.           *)
    Definition Gs (u : U) (z : list R) : list R :=
      let p := rawobs z in
      let a := alpha u p in
      Aff a (beta u p) (hd r0 z)
        :: map (fun k => Rpow a k *! nth k z r0) (seq 1 r).

    Theorem shifted_step : forall u c xs, length xs = N ->
      qs (Fs u (c, xs)) = Gs u (qs (c, xs)).
    Proof.
      intros u c xs HN. unfold Fs, Gs. cbn [fst snd].
      rewrite (rawobs_qs c xs HN).
      change (hd r0 (qs (c, xs))) with c.
      unfold qs at 1. cbn [fst snd].
      f_equal. apply map_ext_in. intros k Hk.
      apply in_seq in Hk. unfold Fm. rewrite smom_aff.
      rewrite nth_qs by lia. reflexivity.
    Qed.

    Theorem shifted_certificate : forall (Y : Type) (hb : list R -> Y),
      certificate Fs qs (fun st => hb (Qr r (snd st)))
                  (fun st => length (snd st) = N)
                  Gs (fun z => hb (rawobs z)).
    Proof.
      intros Y hb. constructor.
      - intros u [c xs] HN. cbn [snd] in *. unfold Fs. cbn [snd].
        unfold Fm. rewrite map_length. exact HN.
      - intros u [c xs] HN. apply shifted_step. exact HN.
      - intros [c xs] HN. cbn [snd] in *. rewrite rawobs_qs by exact HN.
        reflexivity.
    Qed.

    Lemma run_Fs_snd : forall w c xs,
      snd (run Fs w (c, xs)) = run (Fm R r0 r1 radd rmul U r alpha beta) w xs.
    Proof.
      induction w as [|u w IH]; intros c xs; [reflexivity|].
      rewrite !run_cons. unfold Fs at 1. cbn [snd]. apply IH.
    Qed.

    Lemma trace_Fs : forall (Y : Type) (h : list R -> Y) w c xs,
      trace Fs (fun st => h (snd st)) w (c, xs)
      = trace (Fm R r0 r1 radd rmul U r alpha beta) h w xs.
    Proof.
      intros Y h. induction w as [|u w IH]; intros c xs; [reflexivity|].
      cbn [trace]. f_equal. unfold Fs at 1. cbn [snd]. apply IH.
    Qed.

    (* P1 for the ORIGINAL program: whatever shift the reduced           *)
    (* run starts from, every prefix observation of the raw              *)
    (* moments agrees.                                                   *)
    Theorem shifted_reduction_sound : forall (Y : Type) (hb : list R -> Y)
        w c xs,
      length xs = N ->
      trace (Fm R r0 r1 radd rmul U r alpha beta) (fun xs => hb (Qr r xs)) w xs
      = trace Gs (fun z => hb (rawobs z)) w (qs (c, xs)).
    Proof.
      intros Y hb w c xs HN.
      rewrite <- (trace_Fs Y (fun xs => hb (Qr r xs)) w c xs).
      apply (summary_trace_sound U (R * list R) (list R) Y Fs qs
               (fun st => hb (Qr r (snd st))) (fun st => length (snd st) = N)
               Gs (fun z => hb (rawobs z))).
      - apply shifted_certificate.
      - exact HN.
    Qed.

    (* The reduced state after w is the shifted sums of the              *)
    (* original array about the ghost shift: what a rounding             *)
    (* analysis compares to.                                             *)
    Theorem shifted_run_is_shifted_sums : forall w c xs, length xs = N ->
      run Gs w (qs (c, xs)) = qs (run Fs w (c, xs)).
    Proof.
      intros w c xs HN. symmetry.
      apply (summary_run_sound U (R * list R) (list R) (list R) Fs qs
               (fun st => Qr r (snd st)) (fun st => length (snd st) = N)
               Gs rawobs).
      - apply (shifted_certificate (list R) (fun p => p)).
      - exact HN.
    Qed.

  End Certificate.

End Shifted.
