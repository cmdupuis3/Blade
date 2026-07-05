(* ===================================================================== *)
(* BladeCompleteness.v -- Layer 3c: H-AND-STAB COMPLETENESS.             *)
(*                                                                       *)
(* Closes the deferred exactness half of Theorem 9.11.  BladeLowering    *)
(* proved soundness: H + Stab license a permutation.  This file proves   *)
(* that no larger UNIFORM grant is sound, in both directions:            *)
(*                                                                       *)
(*   kernel_invariance_necessary (H is necessary): if the kernel is not  *)
(*     invariant under s, free data realizes the distinguishing tuple,   *)
(*     so the output is not s-symmetric.                                 *)
(*                                                                       *)
(*   stab_violation_detected (Stab is necessary EVEN FOR the maximally   *)
(*     symmetric kernel): for any position permutation s violating the   *)
(*     stabilizer at p0, the fully symmetric kernel fsum (proved         *)
(*     invariant under EVERY permutation: fsum_max_symmetric) together   *)
(*     with indicator data distinguishes Out from Out o s.  The data:    *)
(*     D reads array B(s p0) as the identity series and every other      *)
(*     array as zero; ix is the indicator of position-index s p0.  Then  *)
(*     Out ix = 1 but Out (ix o s) = 0, because the only position whose  *)
(*     s-image is s p0 is p0 itself (injectivity) -- and p0 holds the    *)
(*     WRONG array (the violation).                                      *)
(*                                                                       *)
(*   license_exactness: for the maximally symmetric kernel, the          *)
(*     uniformly licensed permutations are EXACTLY the stabilizer.       *)
(*                                                                       *)
(* Precise reading for the formalism (Theorem 9.11 can now say           *)
(* EXACTLY): the largest set of position permutations the compiler     *)
(* may grant -- soundly for EVERY kernel with declared invariance H and  *)
(* ALL data -- is H intersect Stab.  (A SPECIFIC degenerate kernel,      *)
(* e.g. a constant one, can be accidentally symmetric beyond the grant;  *)
(* exactness is about the uniform grant, which is the compiler's         *)
(* epistemic situation.)                                                 *)
(*                                                                       *)
(* Permutations carry an explicit two-sided inverse on [0, r)            *)
(* (perm_pair) -- the constructive meaning of s permutes the argument   *)
(* positions.                                                           *)
(*                                                                       *)
(* Imports BladeDMWF, BladeLowering.  Coq 8.18, stdlib only.             *)
(* ===================================================================== *)

From Blade Require Import BladeDMWF BladeLowering.
Require Import List Arith Lia Permutation.
Import ListNotations.

(* Generic: lsum respects permutation of the list. *)
Lemma lsum_perm : forall l1 l2 : list nat,
  Permutation l1 l2 -> lsum l1 = lsum l2.
Proof. induction 1; simpl; lia. Qed.

(* Generic: a pointwise-zero map sums to zero. *)
Lemma lsum_zero_all : forall (A : Type) (f : A -> nat) (l : list A),
  (forall x, In x l -> f x = 0) -> lsum (map f l) = 0.
Proof.
  intros A f l H.
  transitivity (lsum (map (fun _ : A => 0) l)).
  { f_equal. apply map_ext_in. exact H. }
  rewrite lsum_const. lia.
Qed.

(* Indicator sums over seq. *)
Lemma lsum_ind_out : forall n a k,
  k < a \/ a + n <= k ->
  lsum (map (fun p => if p =? k then 1 else 0) (seq a n)) = 0.
Proof.
  induction n as [|n IH]; intros a k Hk; simpl; [reflexivity |].
  destruct (a =? k) eqn:E.
  - apply Nat.eqb_eq in E. lia.
  - simpl. apply IH. lia.
Qed.

Lemma lsum_ind_in : forall n a k,
  a <= k < a + n ->
  lsum (map (fun p => if p =? k then 1 else 0) (seq a n)) = 1.
Proof.
  induction n as [|n IH]; intros a k Hk; simpl; [lia |].
  destruct (a =? k) eqn:E.
  - apply Nat.eqb_eq in E. subst a.
    rewrite lsum_ind_out by lia. reflexivity.
  - apply Nat.eqb_neq in E. simpl. apply IH. lia.
Qed.

Section Completeness.
  Variable r : nat.

  (* s permutes [0, r): explicit two-sided inverse. *)
  Definition perm_pair (s s' : nat -> nat) : Prop :=
    (forall p, p < r -> s p < r) /\
    (forall p, p < r -> s' p < r) /\
    (forall p, p < r -> s' (s p) = p) /\
    (forall p, p < r -> s (s' p) = p).

  (* THE MAXIMALLY SYMMETRIC KERNEL: sum of the argument tuple. *)
  Definition fsum (v : nat -> nat) : nat := lsum (map v (seq 0 r)).

  (* fsum reads only positions < r (the framework's locality). *)
  Lemma fsum_ext : forall v v',
    (forall p, p < r -> v p = v' p) -> fsum v = fsum v'.
  Proof.
    intros v v' H. unfold fsum. f_equal.
    apply map_ext_in. intros a Ha. apply in_seq in Ha. apply H. lia.
  Qed.

  Lemma map_s_perm : forall s s', perm_pair s s' ->
    Permutation (map s (seq 0 r)) (seq 0 r).
  Proof.
    intros s s' (Hs & Hs' & Hi1 & Hi2).
    apply NoDup_Permutation.
    - apply NoDup_map_inj; [apply NoDup_seq |].
      intros x y Hx Hy E.
      apply in_seq in Hx. apply in_seq in Hy.
      rewrite <- (Hi1 x) by lia. rewrite E. apply Hi1. lia.
    - apply NoDup_seq.
    - intro x. split; intro H.
      + apply in_map_iff in H as (p & Ep & Hp).
        apply in_seq in Hp. subst x.
        assert (Hpr : p < r) by lia.
        specialize (Hs p Hpr). apply in_seq. lia.
      + apply in_seq in H.
        assert (Hxr : x < r) by lia.
        apply in_map_iff. exists (s' x). split.
        * apply Hi2. exact Hxr.
        * apply in_seq. specialize (Hs' x Hxr). lia.
  Qed.

  (* fsum is invariant under EVERY position permutation: it is in H for  *)
  (* the maximal H.                                                      *)
  Lemma fsum_max_symmetric : forall s s', perm_pair s s' ->
    forall v, fsum (fun p => v (s p)) = fsum v.
  Proof.
    intros s s' Hperm v. unfold fsum.
    rewrite <- map_map.
    apply lsum_perm. apply Permutation_map. apply (map_s_perm s s' Hperm).
  Qed.

  Variable B : nat -> nat.   (* the binding: position -> array identity *)

  (* ------------------------------------------------------------------ *)
  (* THE CORE COUNTEREXAMPLE, in fsum form.                              *)
  (* ------------------------------------------------------------------ *)
  Lemma stab_violation_core : forall s s', perm_pair s s' ->
    forall p0, p0 < r -> B (s p0) <> B p0 ->
    exists (D : nat -> nat -> nat) (ix : nat -> nat),
      fsum (fun p => D (B p) (ix (s p))) <> fsum (fun p => D (B p) (ix p)).
  Proof.
    intros s s' (Hs & Hs' & Hi1 & Hi2) p0 Hp0 Hviol.
    exists (fun a i => if a =? B (s p0) then i else 0),
           (fun q => if q =? s p0 then 1 else 0).
    (* right side sums the indicator of position s p0 over its own      *)
    (* identity class: value 1 *)
    assert (HR : fsum (fun p =>
                   (fun a i => if a =? B (s p0) then i else 0)
                     (B p) (if p =? s p0 then 1 else 0)) = 1).
    { unfold fsum.
      transitivity (lsum (map (fun p => if p =? s p0 then 1 else 0)
                              (seq 0 r))).
      { f_equal. apply map_ext_in. intros p _. cbn beta.
        destruct (p =? s p0) eqn:E.
        - apply Nat.eqb_eq in E. subst p. rewrite Nat.eqb_refl. reflexivity.
        - destruct (B p =? B (s p0)); reflexivity. }
      apply lsum_ind_in.
      assert (Hsp0 : s p0 < r) by (apply Hs; exact Hp0). lia. }
    (* left side: every term vanishes -- the only p with s p = s p0 is  *)
    (* p0 itself, and p0 holds the wrong array *)
    assert (HL : fsum (fun p =>
                   (fun a i => if a =? B (s p0) then i else 0)
                     (B p) (if s p =? s p0 then 1 else 0)) = 0).
    { unfold fsum. apply lsum_zero_all.
      intros p Hp. apply in_seq in Hp. cbn beta.
      destruct (B p =? B (s p0)) eqn:EB; [| reflexivity].
      destruct (s p =? s p0) eqn:ES; [| reflexivity].
      exfalso.
      apply Nat.eqb_eq in ES. apply Nat.eqb_eq in EB.
      assert (Ep : p = p0).
      { rewrite <- (Hi1 p) by lia. rewrite ES. apply Hi1. exact Hp0. }
      subst p. apply Hviol. symmetry. exact EB. }
    intro Heq.
    rewrite HR in Heq. rewrite HL in Heq. discriminate.
  Qed.

  (* ------------------------------------------------------------------ *)
  (* STAB IS NECESSARY, in the framework's Out form: even the maximally  *)
  (* symmetric kernel detects a stabilizer violation.                    *)
  (* ------------------------------------------------------------------ *)
  Theorem stab_violation_detected : forall s s', perm_pair s s' ->
    forall p0, p0 < r -> B (s p0) <> B p0 ->
    exists (D : nat -> nat -> nat) (ix : nat -> nat),
      Out nat nat fsum nat B D (fun p => ix (s p))
      <> Out nat nat fsum nat B D ix.
  Proof.
    intros s s' Hperm p0 Hp0 Hviol.
    destruct (stab_violation_core s s' Hperm p0 Hp0 Hviol)
      as (D & ix & Hne).
    exists D, ix. exact Hne.
  Qed.

  (* ------------------------------------------------------------------ *)
  (* H IS NECESSARY: a kernel not invariant under s is distinguished by  *)
  (* free data (D realizes the witnessing tuple; positions index it).   *)
  (* Holds for ANY kernel and any s -- no permutation structure needed. *)
  (* ------------------------------------------------------------------ *)
  Theorem kernel_invariance_necessary :
    forall (T U : Type) (f : (nat -> T) -> U) (s : nat -> nat),
      (exists v : nat -> T, f (fun p => v (s p)) <> f v) ->
      exists (D : nat -> nat -> T) (ix : nat -> nat),
        Out T U f nat B D (fun p => ix (s p)) <> Out T U f nat B D ix.
  Proof.
    intros T U f s (v & Hv).
    exists (fun _ i => v i), (fun q => q).
    exact Hv.
  Qed.

  (* ------------------------------------------------------------------ *)
  (* EXACTNESS: for the maximally symmetric kernel, the uniformly        *)
  (* licensed permutations are EXACTLY the stabilizer.                   *)
  (* ------------------------------------------------------------------ *)
  Corollary license_exactness : forall s s', perm_pair s s' ->
    ((forall (D : nat -> nat -> nat) (ix : nat -> nat),
        Out nat nat fsum nat B D (fun p => ix (s p))
        = Out nat nat fsum nat B D ix)
     <-> stabilizes r B s).
  Proof.
    intros s s' Hperm. split.
    - (* licensed uniformly -> stabilizes *)
      intros Hlic p0 Hp0.
      destruct (Nat.eq_dec (B (s p0)) (B p0)) as [E | N]; [exact E |].
      exfalso.
      destruct (stab_violation_detected s s' Hperm p0 Hp0 N)
        as (D & ix & Hne).
      apply Hne. apply Hlic.
    - (* stabilizes -> licensed (soundness, imported) *)
      intros Hstab D ix.
      exact (output_symmetry_soundness r nat nat fsum fsum_ext nat B D s
               (fsum_max_symmetric s s' Hperm) Hstab ix).
  Qed.
End Completeness.

(* ===================================================================== *)
(* Notes:                                                                *)
(*  - With soundness (BladeLowering), stab_violation_detected, and       *)
(*    kernel_invariance_necessary, Theorem 9.11 may be stated as         *)
(*    EXACTNESS in the formalism: the uniformly-sound licensed group is  *)
(*    H intersect Stab.  The per-kernel caveat (a degenerate kernel can  *)
(*    be accidentally symmetric beyond the grant) is inherent and should *)
(*    appear as a remark, not hedging.                                   *)
(*  - The construction generalizes verbatim to composite indices          *)
(*    (Ix := nat * nat) since only the INDICATOR structure of ix and D   *)
(*    is used; the nat instance suffices for the exactness claim.        *)
(* ===================================================================== *)
