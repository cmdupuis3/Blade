(* ===================================================================== *)
(* BladeRankDomain.v -- EXACT RECURRENCE REDUCTION, the algebra of P6    *)
(* over any integral domain, and the transfer of an integer rank         *)
(* (docs/research/exact-recurrence-reduction-proofs.md, P6; the          *)
(* axiom-free half of the C^1 thread).                                   *)
(*                                                                       *)
(* The draft's P6 is two facts: the chain rule, DO = DR . Dq, and "a     *)
(* matrix that factors through m columns has rank at most m".  The first *)
(* is analysis and lives in reals/BladeSmoothRank.v, which depends on    *)
(* the axioms of Coq's real numbers.  The second is algebra, and so is   *)
(* the step that carries a rank computed exactly over Z to a field one   *)
(* cannot compute in.  Both are here, with no axioms, so that the file   *)
(* which needs axioms contains nothing but analysis.                     *)
(*                                                                       *)
(*   integer_right_inverse_col  over Z: a square integer matrix with     *)
(*                             trivial left kernel has a right inverse   *)
(*                             up to a nonzero scalar, one column at a   *)
(*                             time (homogeneous_has_solution for the    *)
(*                             column,                                   *)
(*                             BladeDescartes.square_kernel_transpose    *)
(*                             for the scalar being nonzero);            *)
(*                                                                       *)
(*   homogeneous_has_solution_dom  BladeRankBound's elimination over ANY *)
(*                             integral domain with decidable equality;  *)
(*   factor_rows_dependent     THE ALGEBRA OF P6: if A = C . B with      *)
(*                             fewer columns in C than rows, the rows of *)
(*                             A are linearly dependent;                 *)
(*   left_kernel_transfer      along any ring map Z -> K that kills no   *)
(*                             nonzero integer, a square integer matrix  *)
(*                             with trivial left kernel over Z has       *)
(*                             trivial left kernel over K.               *)
(*                                                                       *)
(* Scope, stated once.  K is a commutative ring with 1 <> 0, no zero     *)
(* divisors and DECIDABLE equality, all as hypotheses; nothing is        *)
(* assumed about order or completeness.  Over the reals decidable        *)
(* equality is itself a consequence of the axioms -- that is where they  *)
(* enter, not here.                                                      *)
(*                                                                       *)
(* Imports BladeBinomial, BladeSummary, BladeMomentClosure,              *)
(* BladeRankBound, BladeDescartes.  Coq 8.18, stdlib only.               *)
(* ===================================================================== *)

From Blade Require Import BladeBinomial BladeSummary BladeMomentClosure
  BladeRankBound BladeDescartes.
Require Import List Arith Lia ZArith Ring.
Import ListNotations.

(* ===================================================================== *)
(* Part A.  Over Z: a square integer matrix with trivial LEFT kernel has *)
(* a right inverse up to a nonzero scalar, one column at a time.         *)
(* ===================================================================== *)

Open Scope Z_scope.

Theorem integer_right_inverse_col : forall s (A : nat -> nat -> Z),
  (forall lam : nat -> Z,
     (forall l, (l < s)%nat -> zsumf (fun i => lam i * A i l) s = 0) ->
     forall i, (i < s)%nat -> lam i = 0) ->
  forall k, (k < s)%nat ->
  exists (b : nat -> Z) (d : Z), d <> 0 /\
    forall i, (i < s)%nat ->
      zsumf (fun l => A i l * b l) s = if Nat.eqb i k then d else 0.
Proof.
  intros s A Hleft k Hk. destruct s as [|s']; [lia|].
  destruct (homogeneous_has_solution s' (S s')
              (fun j l => A (skip k j) l) ltac:(lia)) as [b [Hb Hsol]].
  set (d := zsumf (fun l => A k l * b l) (S s')).
  assert (Hrows : forall i, (i < S s')%nat -> i <> k ->
                    zsumf (fun l => A i l * b l) (S s') = 0).
  { intros i Hi Hne. rewrite <- (skip_unskip k i Hne). apply Hsol.
    unfold unskip. destruct (Nat.ltb_spec i k); lia. }
  exists b, d. split.
  - intro Hd0.
    destruct (square_kernel_transpose (S s') (fun l t => A t l) b Hb)
      as [c [[t [Ht Hct]] Hc]].
    + intros t Ht. destruct (Nat.eq_dec t k) as [->|Hne].
      * etransitivity; [|exact Hd0]. apply zsumf_ext. intros l _.
        unfold d. ring.
      * etransitivity; [|exact (Hrows t Ht Hne)]. apply zsumf_ext.
        intros l _. ring.
    + apply Hct. apply (Hleft c); [|exact Ht]. intros l Hl.
      etransitivity; [|exact (Hc l Hl)]. apply zsumf_ext. intros i _. ring.
  - intros i Hi. destruct (Nat.eqb_spec i k) as [->|Hne]; [reflexivity|].
    apply Hrows; assumption.
Qed.

Close Scope Z_scope.

(* ===================================================================== *)
(* Part B.  The same linear algebra over ANY integral domain with        *)
(* decidable equality -- in particular a field one cannot compute in.    *)
(* ===================================================================== *)

Section Domain.
  Variable K : Type.
  Variables (k0 k1 : K) (kadd kmul ksub : K -> K -> K) (kopp : K -> K).
  Hypothesis Kth : ring_theory k0 k1 kadd kmul ksub kopp (@eq K).
  Add Ring domain_ring : Kth.
  Hypothesis K_nontrivial : k1 <> k0.
  Hypothesis K_integral : forall a b, kmul a b = k0 -> a = k0 \/ b = k0.
  Hypothesis K_eq_dec : forall a b : K, {a = b} + {a <> b}.

  Local Infix "+!" := kadd (at level 50, left associativity).
  Local Infix "*!" := kmul (at level 40, left associativity).
  Local Infix "-!" := ksub (at level 50, left associativity).

  Fixpoint ksumf (f : nat -> K) (n : nat) : K :=
    match n with O => k0 | S n' => ksumf f n' +! f n' end.

  Lemma ksumf_ext : forall f g n,
    (forall l, l < n -> f l = g l) -> ksumf f n = ksumf g n.
  Proof.
    intros f g. induction n as [|n IH]; intros H; [reflexivity|]. simpl.
    rewrite IH by (intros; apply H; lia). rewrite (H n) by lia.
    reflexivity.
  Qed.

  Lemma ksumf_zero : forall f n,
    (forall l, l < n -> f l = k0) -> ksumf f n = k0.
  Proof.
    intros f. induction n as [|n IH]; intros H; [reflexivity|]. simpl.
    rewrite IH by (intros; apply H; lia). rewrite (H n) by lia. ring.
  Qed.

  Lemma ksumf_add : forall f g n,
    ksumf (fun l => f l +! g l) n = ksumf f n +! ksumf g n.
  Proof.
    intros f g. induction n as [|n IH]; simpl; [ring|]. rewrite IH. ring.
  Qed.

  Lemma ksumf_scale : forall c f n,
    ksumf (fun l => c *! f l) n = c *! ksumf f n.
  Proof.
    intros c f. induction n as [|n IH]; simpl; [ring|]. rewrite IH. ring.
  Qed.

  Lemma ksumf_swap : forall (f : nat -> nat -> K) s m,
    ksumf (fun l => ksumf (fun j => f l j) m) s
    = ksumf (fun j => ksumf (fun l => f l j) s) m.
  Proof.
    intros f. induction s as [|s IH]; intros m; simpl.
    - symmetry. apply ksumf_zero. reflexivity.
    - rewrite IH, <- ksumf_add. reflexivity.
  Qed.

  Lemma ksumf_single : forall f n l, l < n ->
    (forall i, i < n -> i <> l -> f i = k0) -> ksumf f n = f l.
  Proof.
    intros f. induction n as [|n IH]; intros l Hl H; [lia|]. simpl.
    destruct (Nat.eq_dec l n) as [E|E].
    - subst l. rewrite ksumf_zero by (intros; apply H; lia). ring.
    - rewrite (IH l) by (try lia; intros; apply H; lia).
      rewrite (H n) by lia. ring.
  Qed.

  Lemma ksumf_skip : forall f n p, p <= n ->
    ksumf f (S n) = f p +! ksumf (fun k => f (skip p k)) n.
  Proof.
    intros f. induction n as [|n IH]; intros p Hp.
    - assert (Ep : p = 0) by lia. subst p. simpl. ring.
    - destruct (Nat.eq_dec p (S n)) as [E|E].
      + subst p.
        change (ksumf f (S (S n))) with (ksumf f (S n) +! f (S n)).
        rewrite (ksumf_ext (fun k => f (skip (S n) k)) f (S n)).
        * ring.
        * intros l Hl. unfold skip.
          destruct (Nat.ltb_spec l (S n)); [reflexivity|lia].
      + change (ksumf f (S (S n))) with (ksumf f (S n) +! f (S n)).
        rewrite (IH p) by lia.
        change (ksumf (fun k => f (skip p k)) (S n))
          with (ksumf (fun k => f (skip p k)) n +! f (skip p n)).
        replace (skip p n) with (S n).
        * ring.
        * unfold skip. destruct (Nat.ltb_spec n p); lia.
  Qed.

  Lemma kfind_nonzero : forall (f : nat -> K) n,
    (forall l, l < n -> f l = k0) \/ (exists l, l < n /\ f l <> k0).
  Proof.
    intros f. induction n as [|n [IH|[l [Hl Hn]]]].
    - left. intros; lia.
    - destruct (K_eq_dec (f n) k0) as [E|E].
      + left. intros l Hl. destruct (Nat.eq_dec l n) as [El|El];
          [subst l; exact E|].
        apply IH. lia.
      + right. exists n. split; [lia|exact E].
    - right. exists l. split; [lia|exact Hn].
  Qed.

  Lemma elimination_identity_dom : forall (b : nat -> nat -> K) m p s
      (u' : nat -> K) j, p <= s ->
    let u := fun l =>
               if Nat.eqb l p
               then kopp (ksumf (fun k => b m (skip p k) *! u' k) s)
               else b m p *! u' (unskip p l) in
    ksumf (fun l => b j l *! u l) (S s)
    = ksumf (fun k => (b m p *! b j (skip p k) -! b j p *! b m (skip p k))
                      *! u' k) s.
  Proof.
    intros b m p s u' j Hp u.
    rewrite (ksumf_skip _ s p Hp). unfold u at 1. rewrite Nat.eqb_refl.
    rewrite (ksumf_ext (fun k => b j (skip p k) *! u (skip p k))
               (fun k => b m p *! (b j (skip p k) *! u' k)) s).
    - rewrite ksumf_scale.
      rewrite (ksumf_ext
                 (fun k => (b m p *! b j (skip p k)
                            -! b j p *! b m (skip p k)) *! u' k)
                 (fun k => b m p *! (b j (skip p k) *! u' k)
                           +! kopp (b j p) *! (b m (skip p k) *! u' k)) s)
        by (intros; ring).
      rewrite ksumf_add, !ksumf_scale. ring.
    - intros k _. unfold u.
      destruct (Nat.eqb_spec (skip p k) p) as [E|E].
      + exfalso. exact (skip_neq p k E).
      + rewrite unskip_skip. ring.
  Qed.

  Theorem homogeneous_has_solution_dom : forall m s (b : nat -> nat -> K),
    m < s ->
    exists u : nat -> K,
      (exists l, l < s /\ u l <> k0) /\
      forall j, j < m -> ksumf (fun l => b j l *! u l) s = k0.
  Proof.
    induction m as [|m IH]; intros s b Hms.
    - exists (fun _ => k1). split.
      + exists 0. split; [lia|exact K_nontrivial].
      + intros j Hj. lia.
    - destruct s as [|s]; [lia|].
      destruct (kfind_nonzero (b m) (S s)) as [Hz|[p [Hp Hnz]]].
      + destruct (IH (S s) b ltac:(lia)) as [u [Hu Hsol]].
        exists u. split; [exact Hu|]. intros j Hj.
        destruct (Nat.eq_dec j m) as [E|E].
        * subst j. apply ksumf_zero. intros l Hl. rewrite (Hz l Hl). ring.
        * apply Hsol. lia.
      + set (b' := fun j k => b m p *! b j (skip p k)
                              -! b j p *! b m (skip p k)).
        destruct (IH s b' ltac:(lia)) as [u' [[i0 [Hi0 Hu0]] Hsol]].
        exists (fun l => if Nat.eqb l p
                         then kopp (ksumf (fun k => b m (skip p k) *! u' k) s)
                         else b m p *! u' (unskip p l)).
        split.
        * exists (skip p i0). split; [apply skip_lt; exact Hi0|].
          destruct (Nat.eqb_spec (skip p i0) p) as [E|E].
          -- exfalso. exact (skip_neq p i0 E).
          -- rewrite unskip_skip. intro E0. apply K_integral in E0.
             destruct E0; contradiction.
        * intros j Hj.
          rewrite (elimination_identity_dom b m p s u' j ltac:(lia)).
          destruct (Nat.eq_dec j m) as [E|E].
          -- subst j. apply ksumf_zero. intros k _. ring.
          -- apply (Hsol j). lia.
  Qed.

  (* THE ALGEBRA OF P6.  A matrix that factors through fewer columns     *)
  (* than it has rows has linearly dependent rows.  This is everything   *)
  (* in the draft's P6 that is not the chain rule.                       *)
  Theorem factor_rows_dependent : forall m s N (C B A : nat -> nat -> K),
    m < s ->
    (forall i l, i < s -> l < N ->
       A i l = ksumf (fun j => C i j *! B j l) m) ->
    exists lam : nat -> K,
      (exists i, i < s /\ lam i <> k0) /\
      forall l, l < N -> ksumf (fun i => lam i *! A i l) s = k0.
  Proof.
    intros m s N C B A Hms HA.
    destruct (homogeneous_has_solution_dom m s (fun j i => C i j) Hms)
      as [lam [Hnz Hsol]].
    exists lam. split; [exact Hnz|]. intros l Hl.
    rewrite (ksumf_ext _
               (fun i => ksumf (fun j => B j l *! (C i j *! lam i)) m) s).
    - rewrite ksumf_swap. apply ksumf_zero. intros j Hj.
      rewrite ksumf_scale, (Hsol j Hj). ring.
    - intros i Hi. rewrite (HA i l Hi Hl), <- ksumf_scale.
      apply ksumf_ext. intros j _. ring.
  Qed.

  (* ------------------------------------------------------------------- *)
  (* Transfer.  Along any ring map Z -> K that kills no nonzero integer  *)
  (* (characteristic zero), a square integer matrix with trivial left    *)
  (* kernel over Z has trivial left kernel over K.  This is how a rank   *)
  (* computed exactly over Z is carried to a field one cannot compute    *)
  (* in.                                                                 *)
  (* ------------------------------------------------------------------- *)

  Variable phi : Z -> K.
  Hypothesis phi_zero : phi 0%Z = k0.
  Hypothesis phi_add : forall a b, phi (a + b)%Z = phi a +! phi b.
  Hypothesis phi_mul : forall a b, phi (a * b)%Z = phi a *! phi b.
  Hypothesis phi_faithful : forall d, phi d = k0 -> d = 0%Z.

  Lemma phi_zsumf : forall f n,
    phi (zsumf f n) = ksumf (fun i => phi (f i)) n.
  Proof.
    intros f. induction n as [|n IH]; simpl; [exact phi_zero|].
    rewrite phi_add, IH. reflexivity.
  Qed.

  Theorem left_kernel_transfer : forall s (A : nat -> nat -> Z),
    (forall lam : nat -> Z,
       (forall l, l < s -> zsumf (fun i => (lam i * A i l)%Z) s = 0%Z) ->
       forall i, i < s -> lam i = 0%Z) ->
    forall lamK : nat -> K,
      (forall l, l < s -> ksumf (fun i => lamK i *! phi (A i l)) s = k0) ->
      forall i, i < s -> lamK i = k0.
  Proof.
    intros s A Hleft lamK HK k Hk.
    destruct (integer_right_inverse_col s A Hleft k Hk)
      as [b [d [Hd Hb]]].
    assert (E : ksumf (fun l => ksumf (fun i => lamK i *! phi (A i l)) s
                                *! phi (b l)) s = k0).
    { apply ksumf_zero. intros l Hl. rewrite (HK l Hl). ring. }
    rewrite (ksumf_ext _
               (fun l => ksumf (fun i => lamK i
                                         *! (phi (A i l) *! phi (b l))) s) s)
      in E.
    - rewrite ksumf_swap in E.
      rewrite (ksumf_ext _
                 (fun i => lamK i
                           *! phi (if Nat.eqb i k then d else 0%Z)) s) in E.
      + rewrite (ksumf_single _ s k Hk) in E.
        * rewrite Nat.eqb_refl in E. apply K_integral in E.
          destruct E as [E|E]; [exact E|]. exfalso. apply Hd.
          apply phi_faithful. exact E.
        * intros i _ Hne. destruct (Nat.eqb_spec i k); [contradiction|].
          rewrite phi_zero. ring.
      + intros i Hi. rewrite ksumf_scale. f_equal.
        rewrite <- (Hb i Hi), phi_zsumf. apply ksumf_ext. intros l _.
        rewrite phi_mul. reflexivity.
    - intros l _. rewrite Kth.(Rmul_comm), <- ksumf_scale.
      apply ksumf_ext. intros i _. ring.
  Qed.

End Domain.
