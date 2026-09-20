(* ===================================================================== *)
(* BladeRankBound.v -- EXACT RECURRENCE REDUCTION, the lower bounds at   *)
(* every size (docs/research/exact-recurrence-reduction-proofs.md: P6,   *)
(* P4a, P7, P8's refusal and P10, in their POLYNOMIAL forms, with no     *)
(* determinants and no analysis).                                        *)
(*                                                                       *)
(* BladeMomentClosure proves the rank bound at sizes (1,2) and (2,3),    *)
(* where `ring` can expand the minor.  The general size needs one fact   *)
(* of linear algebra, and it is not a determinant:                       *)
(*                                                                       *)
(*   homogeneous_has_solution  an integer homogeneous system with more   *)
(*                             unknowns than equations has a NONZERO     *)
(*                             solution.  Fraction-free elimination by   *)
(*                             induction on the number of equations      *)
(*                             (elimination_identity is the whole step). *)
(*                                                                       *)
(*   poly_rank_bound_rows      If s observations factor, as identities   *)
(*                             over the dual numbers, through m < s      *)
(*                             polynomial summary coordinates, then at   *)
(*                             EVERY point one nonzero integer           *)
(*                             combination of their differentials        *)
(*                             vanishes along every direction;           *)
(*   poly_rank_bound_cols      and along any s directions one nonzero    *)
(*                             combination of the DIRECTIONS is          *)
(*                             invisible to every observation.           *)
(*   poly_rank_certificate     The soundness theorem of a certificate    *)
(*                             checker: a point and s directions whose   *)
(*                             s x s integer matrix of differentials has *)
(*                             trivial kernel refute every m < s.  For a *)
(*                             concrete matrix that premise is `lia`.    *)
(*                                                                       *)
(*   many_roots                a polynomial with more distinct roots     *)
(*                             than coefficients is zero (Horner         *)
(*                             division whose quotient coefficients are  *)
(*                             tail evaluations, so "quotient zero =>    *)
(*                             dividend zero" is one line).  The only    *)
(*                             place Z being an integral domain is used. *)
(*                                                                       *)
(*   moment_summary_needs_r_coordinates  P4a at EVERY r: the first r     *)
(*                             power sums of N >= r particles need r     *)
(*                             polynomial coordinates (row form at x_i = *)
(*                             i).                                       *)
(*   squaring_needs_min_N_H_coordinates  P7 at EVERY N and horizon: the  *)
(*                             sums observed over Hor steps of squaring  *)
(*                             need min(N, Hor) polynomial coordinates,  *)
(*                             so no bounded polynomial summary serves   *)
(*                             every horizon as N grows.  Column form at *)
(*                             x_i = 2^i along directions 2^l e_l, where *)
(*                             the GENERALIZED Vandermonde matrix of the *)
(*                             draft is an ordinary one in the nodes     *)
(*                             2^(2^t) -- no Descartes, no Rolle.        *)
(*   power_not_poly_closed     P8's refusal for x -> x^d at EVERY d >= 2 *)
(*                             and r >= 1 with the draft's own threshold *)
(*                             N >= d r, against polynomial G (its       *)
(*                             "algebraic mechanization alternative").   *)
(*   power_not_closed_SQ       and at r = 2 against EVERY G, uniformly   *)
(*                             in d, by one collision: 7^k outgrows      *)
(*                             1 + 5^k + 6^k from k = 3 on.              *)
(*   p10_not_finitely_generated  P10 to the end: given ANY finite list   *)
(*                             of elements of the least observable       *)
(*                             algebra, some observable x y^M is not a   *)
(*                             polynomial in them.                       *)
(*                                                                       *)
(* Scope, stated once.  Everything here is against POLYNOMIAL summaries  *)
(* with polynomial reconstructions, read as identities valid in the dual *)
(* numbers over Z -- what a symbolic certificate provides.  That is      *)
(* narrower than the draft's C^1 statements and is not a substitute for  *)
(* them: a continuous non-polynomial encoding is untouched.  The point   *)
(* chosen for P7 is one point; the draft's claim that the rank is        *)
(* min(N, H) at EVERY point with distinct positive coordinates needs its *)
(* generalized-Vandermonde lemma (P7a, P7b) and is NOT proved.  P8 is    *)
(* proved for pure powers, not for the general fragment with coefficient *)
(* polynomials a_j(q_r).  No source fragment, no recognition, no code.   *)
(*                                                                       *)
(* Imports BladeBinomial, BladeSummary, BladeMomentClosure.  Coq 8.18,   *)
(* stdlib only.                                                          *)
(* ===================================================================== *)

From Blade Require Import BladeBinomial BladeSummary BladeMomentClosure.
Require Import List Arith Lia ZArith Ring.
Import ListNotations.

Open Scope Z_scope.

(* ===================================================================== *)
(* Part A.  Finite sums indexed by nat, and the one fact of linear       *)
(* algebra the bound needs: a homogeneous integer system with more       *)
(* unknowns than equations has a nonzero solution.  Fraction-free        *)
(* elimination; no determinants, no field.                               *)
(* ===================================================================== *)

Fixpoint zsumf (f : nat -> Z) (n : nat) : Z :=
  match n with O => 0 | S n' => zsumf f n' + f n' end.

Lemma zsumf_ext : forall f g n,
  (forall l, (l < n)%nat -> f l = g l) -> zsumf f n = zsumf g n.
Proof.
  intros f g. induction n as [|n IH]; intros H; [reflexivity|]. simpl.
  rewrite IH by (intros; apply H; lia). rewrite (H n) by lia. reflexivity.
Qed.

Lemma zsumf_zero : forall f n,
  (forall l, (l < n)%nat -> f l = 0) -> zsumf f n = 0.
Proof.
  intros f. induction n as [|n IH]; intros H; [reflexivity|]. simpl.
  rewrite IH by (intros; apply H; lia). rewrite (H n) by lia. reflexivity.
Qed.

Lemma zsumf_add : forall f g n,
  zsumf (fun l => f l + g l) n = zsumf f n + zsumf g n.
Proof.
  intros f g. induction n as [|n IH]; simpl; [reflexivity|]. rewrite IH.
  ring.
Qed.

Lemma zsumf_scale : forall c f n,
  zsumf (fun l => c * f l) n = c * zsumf f n.
Proof.
  intros c f. induction n as [|n IH]; simpl; [ring|]. rewrite IH. ring.
Qed.

Lemma zsumf_swap : forall (f : nat -> nat -> Z) s m,
  zsumf (fun l => zsumf (fun j => f l j) m) s
  = zsumf (fun j => zsumf (fun l => f l j) s) m.
Proof.
  intros f. induction s as [|s IH]; intros m; simpl.
  - symmetry. apply zsumf_zero. reflexivity.
  - rewrite IH, <- zsumf_add. reflexivity.
Qed.

Lemma zsumf_single : forall f n l, (l < n)%nat ->
  (forall i, (i < n)%nat -> i <> l -> f i = 0) -> zsumf f n = f l.
Proof.
  intros f. induction n as [|n IH]; intros l Hl H; [lia|]. simpl.
  destruct (Nat.eq_dec l n) as [E|E].
  - subst. rewrite zsumf_zero by (intros; apply H; lia). lia.
  - rewrite (IH l) by (try lia; intros; apply H; lia).
    rewrite (H n) by lia. lia.
Qed.

Lemma zrsum_zsumf : forall f n,
  rsum Z 0 Z.add (map f (seq 0 n)) = zsumf f n.
Proof.
  intros f. induction n as [|n IH]; [reflexivity|].
  change (rsum Z 0 Z.add (map f (seq 0 (S n))))
    with (zrsum (map f (seq 0 (S n)))).
  rewrite zrsum_snoc. unfold zrsum. rewrite IH. reflexivity.
Qed.

(* Peel one index out of the middle.                                     *)
Definition skip (p k : nat) : nat := if Nat.ltb k p then k else S k.
Definition unskip (p l : nat) : nat := if Nat.ltb l p then l else pred l.

Lemma skip_neq : forall p k, skip p k <> p.
Proof. intros p k. unfold skip. destruct (Nat.ltb_spec k p); lia. Qed.

Lemma unskip_skip : forall p k, unskip p (skip p k) = k.
Proof.
  intros p k. unfold skip, unskip. destruct (Nat.ltb_spec k p) as [H|H].
  - destruct (Nat.ltb_spec k p); lia.
  - destruct (Nat.ltb_spec (S k) p); simpl; lia.
Qed.

Lemma skip_lt : forall p k n, (k < n)%nat -> (skip p k < S n)%nat.
Proof. intros p k n H. unfold skip. destruct (Nat.ltb_spec k p); lia. Qed.

Lemma zsumf_skip : forall f n p, (p <= n)%nat ->
  zsumf f (S n) = f p + zsumf (fun k => f (skip p k)) n.
Proof.
  intros f. induction n as [|n IH]; intros p Hp.
  - assert (p = 0)%nat by lia. subst. simpl. lia.
  - destruct (Nat.eq_dec p (S n)) as [E|E].
    + subst p. change (zsumf f (S (S n))) with (zsumf f (S n) + f (S n)).
      rewrite (zsumf_ext (fun k => f (skip (S n) k)) f (S n)).
      * lia.
      * intros l Hl. unfold skip. destruct (Nat.ltb_spec l (S n)); [|lia].
        reflexivity.
    + change (zsumf f (S (S n))) with (zsumf f (S n) + f (S n)).
      rewrite (IH p) by lia.
      change (zsumf (fun k => f (skip p k)) (S n))
        with (zsumf (fun k => f (skip p k)) n + f (skip p n)).
      replace (skip p n) with (S n).
      * lia.
      * unfold skip. destruct (Nat.ltb_spec n p); lia.
Qed.

Lemma zfind_nonzero : forall (f : nat -> Z) n,
  (forall l, (l < n)%nat -> f l = 0)
  \/ (exists l, (l < n)%nat /\ f l <> 0).
Proof.
  intros f. induction n as [|n [IH|[l [Hl Hn]]]].
  - left. intros; lia.
  - destruct (Z.eq_dec (f n) 0) as [E|E].
    + left. intros l Hl. destruct (Nat.eq_dec l n); [subst; exact E|].
      apply IH. lia.
    + right. exists n. split; [lia|exact E].
  - right. exists l. split; [lia|exact Hn].
Qed.

(* The elimination step as one identity: with pivot b m p, the combined  *)
(* solution u satisfies row j exactly when u' satisfies the reduced row. *)
Lemma elimination_identity : forall (b : nat -> nat -> Z) m p s
    (u' : nat -> Z) j, (p <= s)%nat ->
  let u := fun l =>
             if Nat.eqb l p
             then - zsumf (fun k => b m (skip p k) * u' k) s
             else b m p * u' (unskip p l) in
  zsumf (fun l => b j l * u l) (S s)
  = zsumf (fun k => (b m p * b j (skip p k) - b j p * b m (skip p k))
                    * u' k) s.
Proof.
  intros b m p s u' j Hp u.
  rewrite (zsumf_skip _ s p Hp). unfold u at 1. rewrite Nat.eqb_refl.
  rewrite (zsumf_ext (fun k => b j (skip p k) * u (skip p k))
             (fun k => b m p * (b j (skip p k) * u' k)) s).
  - rewrite zsumf_scale.
    rewrite (zsumf_ext
               (fun k => (b m p * b j (skip p k) - b j p * b m (skip p k))
                         * u' k)
               (fun k => b m p * (b j (skip p k) * u' k)
                         + (- b j p) * (b m (skip p k) * u' k)) s)
      by (intros; ring).
    rewrite zsumf_add, !zsumf_scale. ring.
  - intros k _. unfold u.
    destruct (Nat.eqb_spec (skip p k) p) as [E|E].
    + exfalso. exact (skip_neq p k E).
    + rewrite unskip_skip. ring.
Qed.

Theorem homogeneous_has_solution : forall m s (b : nat -> nat -> Z),
  (m < s)%nat ->
  exists u : nat -> Z,
    (exists l, (l < s)%nat /\ u l <> 0) /\
    forall j, (j < m)%nat -> zsumf (fun l => b j l * u l) s = 0.
Proof.
  induction m as [|m IH]; intros s b Hms.
  - exists (fun _ => 1). split.
    + exists 0%nat. split; [lia|discriminate].
    + intros j Hj. lia.
  - destruct s as [|s]; [lia|].
    destruct (zfind_nonzero (b m) (S s)) as [Hz|[p [Hp Hnz]]].
    + destruct (IH (S s) b ltac:(lia)) as [u [Hu Hsol]].
      exists u. split; [exact Hu|]. intros j Hj.
      destruct (Nat.eq_dec j m) as [E|E].
      * subst j. apply zsumf_zero. intros l Hl. rewrite (Hz l Hl). ring.
      * apply Hsol. lia.
    + set (b' := fun j k => b m p * b j (skip p k) - b j p * b m (skip p k)).
      destruct (IH s b' ltac:(lia)) as [u' [[k0 [Hk0 Hu0]] Hsol]].
      exists (fun l => if Nat.eqb l p
                       then - zsumf (fun k => b m (skip p k) * u' k) s
                       else b m p * u' (unskip p l)).
      split.
      * exists (skip p k0). split; [apply skip_lt; exact Hk0|].
        destruct (Nat.eqb_spec (skip p k0) p) as [E|E].
        -- exfalso. exact (skip_neq p k0 E).
        -- rewrite unskip_skip. intro E0. apply Z.mul_eq_0 in E0.
           destruct E0; contradiction.
      * intros j Hj.
        rewrite (elimination_identity b m p s u' j ltac:(lia)).
        destruct (Nat.eq_dec j m) as [E|E].
        -- subst j. apply zsumf_zero. intros k _. ring.
        -- apply (Hsol j). lia.
Qed.

(* ===================================================================== *)
(* Part B.  The rank bound at every size.  If s observations factor,     *)
(* as identities over the dual numbers, through m < s polynomial summary *)
(* coordinates, their formal differentials are linearly dependent at     *)
(* EVERY point -- in both readings of "dependent".                       *)
(* ===================================================================== *)

Definition zD : pexp Z -> (nat -> Z) -> (nat -> Z) -> Z := D Z 0 Z.add Z.mul.
Definition zqenvD
  : nat -> (nat -> pexp Z) -> (nat -> Z * Z) -> nat -> Z * Z :=
  qenvD Z 0 Z.add Z.mul.

(* s observations O_i carried by m coordinates q_j with reconstructions  *)
(* Rr_i.                                                                 *)
Definition factors_through (m s : nat) (q O Rr : nat -> pexp Z) : Prop :=
  forall i, (i < s)%nat -> forall rho,
    zpevD (O i) rho = zpevD (Rr i) (zqenvD m q rho).

Definition rcoef (m : nat) (q Rr : nat -> pexp Z) (x : nat -> Z)
           (i j : nat) : Z :=
  snd (zpevD (Rr i) (lift Z (qval Z 0 Z.add Z.mul m q x) (unitR Z 0 1 j))).

Lemma zD_factor : forall m s q O Rr, factors_through m s q O Rr ->
  forall i, (i < s)%nat -> forall x v,
    zD (O i) x v = zsumf (fun j => zD (q j) x v * rcoef m q Rr x i j) m.
Proof.
  intros m s q O Rr H i Hi x v. unfold zD.
  rewrite (D_factor Z 0 1 Z.add Z.mul Z.sub Z.opp InitialRing.Zth
             m q (O i) (Rr i) (H i Hi) x v).
  apply zrsum_zsumf.
Qed.

(* Row form: one nonzero integer combination of the differentials        *)
(* vanishes along EVERY direction.                                       *)
Theorem poly_rank_bound_rows : forall m s q O Rr, (m < s)%nat ->
  factors_through m s q O Rr ->
  forall x, exists lam : nat -> Z,
    (exists i, (i < s)%nat /\ lam i <> 0) /\
    forall v, zsumf (fun i => lam i * zD (O i) x v) s = 0.
Proof.
  intros m s q O Rr Hms H x.
  destruct (homogeneous_has_solution m s (fun j i => rcoef m q Rr x i j) Hms)
    as [lam [Hnz Hsol]].
  exists lam. split; [exact Hnz|]. intros v.
  rewrite (zsumf_ext _
             (fun i => zsumf (fun j => zD (q j) x v
                                       * (rcoef m q Rr x i j * lam i)) m) s).
  - rewrite zsumf_swap. apply zsumf_zero. intros j Hj.
    rewrite zsumf_scale, (Hsol j Hj). ring.
  - intros i Hi. rewrite (zD_factor m s q O Rr H i Hi x v).
    rewrite <- zsumf_scale. apply zsumf_ext. intros j _. ring.
Qed.

(* Column form: along any s directions, one nonzero combination of the   *)
(* DIRECTIONS is invisible to every observation.                         *)
Theorem poly_rank_bound_cols : forall m s q O Rr, (m < s)%nat ->
  factors_through m s q O Rr ->
  forall x (V : nat -> nat -> Z), exists u : nat -> Z,
    (exists l, (l < s)%nat /\ u l <> 0) /\
    forall i, (i < s)%nat -> zsumf (fun l => zD (O i) x (V l) * u l) s = 0.
Proof.
  intros m s q O Rr Hms H x V.
  destruct (homogeneous_has_solution m s (fun j l => zD (q j) x (V l)) Hms)
    as [u [Hnz Hsol]].
  exists u. split; [exact Hnz|]. intros i Hi.
  rewrite (zsumf_ext _
             (fun l => zsumf (fun j => rcoef m q Rr x i j
                                       * (zD (q j) x (V l) * u l)) m) s).
  - rewrite zsumf_swap. apply zsumf_zero. intros j Hj.
    rewrite zsumf_scale, (Hsol j Hj). ring.
  - intros l _. rewrite (zD_factor m s q O Rr H i Hi x (V l)).
    rewrite Z.mul_comm, <- zsumf_scale. apply zsumf_ext. intros j _. ring.
Qed.

(* The soundness theorem of a certificate checker: a point and s         *)
(* directions along which the s x s integer matrix of differentials has  *)
(* trivial kernel certify that no m < s polynomial coordinates suffice.  *)
(* For a concrete matrix the premise is linear integer arithmetic.       *)
Corollary poly_rank_certificate : forall m s (O : nat -> pexp Z) x V,
  (forall u : nat -> Z,
     (forall i, (i < s)%nat ->
        zsumf (fun l => zD (O i) x (V l) * u l) s = 0) ->
     forall l, (l < s)%nat -> u l = 0) ->
  (m < s)%nat -> forall q Rr, ~ factors_through m s q O Rr.
Proof.
  intros m s O x V Hinj Hms q Rr H.
  destruct (poly_rank_bound_cols m s q O Rr Hms H x V)
    as [u [[l [Hl Hnz]] Hsol]].
  apply Hnz. apply (Hinj u Hsol l Hl).
Qed.

(* ===================================================================== *)
(* Part C.  A polynomial with more roots than coefficients is zero.      *)
(* Division by (X - a) in Horner form; the quotient's coefficients are   *)
(* the tail evaluations, which makes "quotient zero => dividend zero"    *)
(* immediate.                                                            *)
(* ===================================================================== *)

Local Notation zpeval := (peval Z 0 Z.add Z.mul).

Fixpoint quot (al : Z) (l : list Z) : list Z :=
  match l with
  | [] => []
  | _ :: l' => match l' with
               | [] => []
               | _ :: _ => zpeval l' al :: quot al l'
               end
  end.

Lemma quot_length : forall al l, length (quot al l) = (length l - 1)%nat.
Proof.
  intros al. induction l as [|c l IH]; [reflexivity|].
  destruct l as [|c' l']; [reflexivity|].
  change (quot al (c :: c' :: l'))
    with (zpeval (c' :: l') al :: quot al (c' :: l')).
  simpl length in *. rewrite IH. lia.
Qed.

Lemma quot_identity : forall al l x,
  zpeval l x - zpeval l al = (x - al) * zpeval (quot al l) x.
Proof.
  intros al. induction l as [|c l IH]; intros x; [simpl; ring|].
  destruct l as [|c' l']; [simpl; ring|].
  change (quot al (c :: c' :: l'))
    with (zpeval (c' :: l') al :: quot al (c' :: l')).
  specialize (IH x). set (t := c' :: l') in *. cbn [peval].
  assert (E : zpeval t x = zpeval t al + (x - al) * zpeval (quot al t) x)
    by lia.
  rewrite E. ring.
Qed.

Lemma quot_zero : forall al l,
  Forall (fun c => c = 0) (quot al l) -> zpeval l al = 0 ->
  Forall (fun c => c = 0) l.
Proof.
  intros al. induction l as [|c l IH]; intros Hq He; [constructor|].
  destruct l as [|c' l'].
  - simpl in He. constructor; [lia|constructor].
  - change (quot al (c :: c' :: l'))
      with (zpeval (c' :: l') al :: quot al (c' :: l')) in Hq.
    remember (c' :: l') as t eqn:Et in *. cbn [peval] in He.
    inversion Hq as [|w ws H0 Hq' Ew]. clear Ew.
    constructor; [rewrite H0 in He; lia|]. apply IH; assumption.
Qed.

Theorem many_roots : forall rs : list Z, NoDup rs ->
  forall l, (length l <= length rs)%nat ->
    (forall a, In a rs -> zpeval l a = 0) -> Forall (fun c => c = 0) l.
Proof.
  induction rs as [|al rs IH]; intros Hnd l Hlen Hroots.
  - destruct l; [constructor|simpl in Hlen; lia].
  - inversion Hnd as [|? ? Hnin Hnd']; subst.
    apply (quot_zero al); [|apply Hroots; left; reflexivity].
    apply (IH Hnd').
    + rewrite quot_length. simpl in Hlen. lia.
    + intros a Ha.
      pose proof (quot_identity al l a) as E.
      rewrite (Hroots a (or_intror Ha)), (Hroots al (or_introl eq_refl))
        in E.
      assert (Hne : a - al <> 0).
      { intro Hz. apply Hnin. replace al with a by lia. exact Ha. }
      symmetry in E. simpl in E. apply Z.mul_eq_0 in E.
      destruct E; [contradiction|assumption].
Qed.

Lemma zsumf_head : forall f n,
  zsumf f (S n) = f 0%nat + zsumf (fun k => f (S k)) n.
Proof. intros f n. apply (zsumf_skip f n 0). lia. Qed.

Lemma zsumf_peval : forall (c : nat -> Z) a s k,
  zsumf (fun i => c (k + i)%nat * zpow a i) s = zpeval (map c (seq k s)) a.
Proof.
  intros c a. induction s as [|s IH]; intros k; [reflexivity|].
  rewrite zsumf_head. cbn [seq map peval]. rewrite <- (IH (S k)).
  rewrite <- zsumf_scale. rewrite Nat.add_0_r. f_equal.
  - change (zpow a 0) with 1. ring.
  - apply zsumf_ext. intros i _. rewrite Nat.add_succ_r, zpow_succ.
    simpl Nat.add. ring.
Qed.

Lemma NoDup_map_in : forall (A B : Type) (f : A -> B) l,
  (forall x y, In x l -> In y l -> f x = f y -> x = y) ->
  NoDup l -> NoDup (map f l).
Proof.
  intros A B f. induction l as [|a l IH]; intros Hinj Hnd; simpl.
  - constructor.
  - inversion Hnd as [|? ? Hnin Hnd']; subst. constructor.
    + intro Hin. apply in_map_iff in Hin. destruct Hin as [y [E Hy]].
      assert (y = a) by (apply Hinj; auto using in_eq, in_cons).
      subst. contradiction.
    + apply IH; auto using in_cons.
Qed.

(* The form the applications use: coefficients and nodes as functions.   *)
Theorem many_roots_fn : forall s (c a : nat -> Z),
  (forall t t', (t < s)%nat -> (t' < s)%nat -> a t = a t' -> t = t') ->
  (forall t, (t < s)%nat -> zsumf (fun i => c i * zpow (a t) i) s = 0) ->
  forall i, (i < s)%nat -> c i = 0.
Proof.
  intros s c a Hinj Hroots i Hi.
  assert (HF : Forall (fun z => z = 0) (map c (seq 0 s))).
  { apply (many_roots (map a (seq 0 s))).
    - apply NoDup_map_in; [|apply seq_NoDup].
      intros t t' Ht Ht'. apply in_seq in Ht. apply in_seq in Ht'.
      apply Hinj; lia.
    - rewrite !map_length, !seq_length. lia.
    - intros al Hal. apply in_map_iff in Hal. destruct Hal as [t [E Ht]].
      subst al. apply in_seq in Ht. rewrite <- (zsumf_peval c (a t) s 0).
      apply Hroots. lia. }
  rewrite Forall_forall in HF. apply HF. apply in_map. apply in_seq. lia.
Qed.

(* ===================================================================== *)
(* Part D.  The two bounds the draft states with Vandermonde matrices,   *)
(* at every size.  Both reduce to many_roots -- P4a through the row      *)
(* form, P7 through the column form at the point x_i = 2^i, where the    *)
(* GENERALIZED Vandermonde matrix of the squaring observations becomes   *)
(* an ordinary one in the nodes 2^(2^t).                                 *)
(* ===================================================================== *)

Local Notation pevDZ := (pevD Z 0 Z.add Z.mul).

(* p_k on the first n variables.                                         *)
Fixpoint pPn (n k : nat) : pexp Z :=
  match n with
  | O => PConst 0
  | S n' => PAdd (pPn n' k) (ppow (PVar n') k)
  end.

Lemma ofnat_Z : forall n, ofnat Z 0 1 Z.add n = Z.of_nat n.
Proof.
  induction n as [|n IH]; [reflexivity|]. cbn [ofnat]. rewrite IH. lia.
Qed.

Lemma pevDZ_ppow_var : forall i k rho,
  pevDZ (ppow (PVar i) k) rho
  = rpow (Z * Z) (d1 Z 0 1) (dmul Z Z.add Z.mul) (rho i) k.
Proof.
  intros i k rho. induction k as [|k IH]; [reflexivity|].
  cbn [ppow pevD rpow]. rewrite IH. reflexivity.
Qed.

Lemma zD_ppow_var : forall i k x v,
  zD (ppow (PVar i) (S k)) x v = Z.of_nat (S k) * zpow (x i) k * v i.
Proof.
  intros i k x v. unfold zD, D. rewrite pevDZ_ppow_var. unfold lift.
  rewrite (rpow_dual Z 0 1 Z.add Z.mul Z.sub Z.opp InitialRing.Zth).
  cbn [snd]. rewrite ofnat_Z. reflexivity.
Qed.

Lemma zD_pPn : forall n k x v,
  zD (pPn n (S k)) x v
  = zsumf (fun i => Z.of_nat (S k) * zpow (x i) k * v i) n.
Proof.
  induction n as [|n IH]; intros k x v; [reflexivity|].
  change (zD (pPn (S n) (S k)) x v)
    with (zD (pPn n (S k)) x v + zD (ppow (PVar n) (S k)) x v).
  rewrite IH, zD_ppow_var. reflexivity.
Qed.

Lemma zD_pPn_unit : forall n k x l c, (l < n)%nat ->
  zD (pPn n (S k)) x (fun i => if Nat.eqb i l then c else 0)
  = Z.of_nat (S k) * zpow (x l) k * c.
Proof.
  intros n k x l c Hl. rewrite zD_pPn. rewrite (zsumf_single _ n l Hl).
  - rewrite Nat.eqb_refl. reflexivity.
  - intros i _ Hne. destruct (Nat.eqb_spec i l); [contradiction|ring].
Qed.

(* P4a at every r, against polynomial encodings: the first r power sums  *)
(* of N >= r particles are not carried by fewer than r polynomial        *)
(* summary coordinates.                                                  *)
Theorem moment_summary_needs_r_coordinates : forall r N m,
  (r <= N)%nat -> (m < r)%nat ->
  forall q Rr, ~ factors_through m r q (fun k => pPn N (S k)) Rr.
Proof.
  intros r N m HrN Hmr q Rr Hf.
  destruct (poly_rank_bound_rows m r q _ Rr Hmr Hf (fun i => Z.of_nat i))
    as [lam [[k0 [Hk0 Hnz]] Hdep]].
  apply Hnz.
  assert (Hc : forall k, (k < r)%nat -> lam k * Z.of_nat (S k) = 0).
  { apply (many_roots_fn r (fun k => lam k * Z.of_nat (S k))
             (fun t => Z.of_nat t)).
    - intros t t' _ _ E. lia.
    - intros t Ht.
      etransitivity;
        [|apply (Hdep (fun i => if Nat.eqb i t then 1 else 0))].
      apply zsumf_ext. intros k Hk. rewrite zD_pPn_unit by lia.
      cbv beta. ring. }
  specialize (Hc k0 Hk0). apply Z.mul_eq_0 in Hc. destruct Hc; lia.
Qed.

Lemma zpow_add : forall b n m, zpow b (n + m) = zpow b n * zpow b m.
Proof.
  intros b n m. induction n as [|n IH].
  - change (zpow b 0) with 1. simpl Nat.add. ring.
  - change (zpow b (S n + m)) with (b * zpow b (n + m)).
    rewrite IH, zpow_succ. ring.
Qed.

Lemma zpow_mul : forall b n m, zpow (zpow b n) m = zpow b (n * m).
Proof.
  intros b n m. induction m as [|m IH].
  - rewrite Nat.mul_0_r. reflexivity.
  - rewrite zpow_succ, IH. replace (n * S m)%nat with (n + n * m)%nat by ring.
    rewrite zpow_add. reflexivity.
Qed.

Lemma zpow_two_nat : forall n, zpow 2 n = Z.of_nat (2 ^ n).
Proof.
  induction n as [|n IH]; [reflexivity|]. rewrite zpow_succ, IH.
  simpl Nat.pow. lia.
Qed.

(* P7 at every N and every horizon, against polynomial encodings: the    *)
(* sums observed at steps 0 .. Hor-1 of the squaring recurrence on N     *)
(* particles (squaring_observes_dyadic_moments: p_1, p_2, p_4, ...) are  *)
(* not carried by fewer than min(N, Hor) polynomial coordinates.  So no  *)
(* bounded polynomial summary serves every horizon as N grows.           *)
Theorem squaring_needs_min_N_H_coordinates : forall N Hor m,
  (m < Nat.min N Hor)%nat ->
  forall q Rr, ~ factors_through m Hor q (fun t => pPn N (2 ^ t)) Rr.
Proof.
  intros N Hor m Hm q Rr Hf.
  set (s := Nat.min N Hor) in *.
  assert (Hf' : factors_through m s q (fun t => pPn N (2 ^ t)) Rr).
  { intros i Hi. apply Hf. unfold s in Hi. lia. }
  revert Hf'.
  apply (poly_rank_certificate m s (fun t => pPn N (2 ^ t))
           (fun i => zpow 2 i)
           (fun l i => if Nat.eqb i l then zpow 2 l else 0));
    [|exact Hm].
  intros u Hu.
  apply (many_roots_fn s u (fun t => zpow 2 (2 ^ t))).
  - intros t t' _ _ E. rewrite !zpow_two_nat in E. apply Nat2Z.inj in E.
    apply Nat.pow_inj_r in E; [|lia]. apply Nat.pow_inj_r in E; [exact E|lia].
  - intros t Ht. specialize (Hu t Ht). cbv beta in Hu.
    destruct (2 ^ t)%nat as [|K] eqn:EK.
    { exfalso. pose proof (Nat.pow_nonzero 2 t). lia. }
    rewrite (zsumf_ext _
               (fun l => Z.of_nat (S K) * (u l * zpow (zpow 2 (S K)) l)) s)
      in Hu.
    + rewrite zsumf_scale in Hu. apply Z.mul_eq_0 in Hu.
      destruct Hu as [Hu|Hu]; [lia|exact Hu].
    + intros l Hl. rewrite zD_pPn_unit by (unfold s in Hl; lia).
      cbv beta. rewrite !zpow_mul.
      replace (S K * l)%nat with (l * K + l)%nat by ring.
      rewrite zpow_add. ring.
Qed.

(* ===================================================================== *)
(* Part E.  P10 to the end, and P8's refusal uniformly in the degree.    *)
(* ===================================================================== *)

Fixpoint maxvar (e : pexp Z) : nat :=
  match e with
  | PVar i => i
  | PConst _ => O
  | PAdd a b => Nat.max (maxvar a) (maxvar b)
  | PMul a b => Nat.max (maxvar a) (maxvar b)
  end.

Lemma pevDZ_agree : forall e rho rho',
  (forall k, (k <= maxvar e)%nat -> rho k = rho' k) ->
  pevDZ e rho = pevDZ e rho'.
Proof.
  induction e as [i|c|e1 IH1 e2 IH2|e1 IH1 e2 IH2]; intros rho rho' H;
    simpl in *.
  - apply H. lia.
  - reflexivity.
  - rewrite (IH1 rho rho'), (IH2 rho rho'); try reflexivity;
      intros; apply H; lia.
  - rewrite (IH1 rho rho'), (IH2 rho rho'); try reflexivity;
      intros; apply H; lia.
Qed.

Lemma pevDZ_subst : forall sigma e rho,
  pevDZ (psubst Z sigma e) rho = pevDZ e (fun i => pevDZ (sigma i) rho).
Proof.
  induction e as [i|c|e1 IH1 e2 IH2|e1 IH1 e2 IH2]; intros rho; simpl;
    auto.
  - rewrite IH1, IH2. reflexivity.
  - rewrite IH1, IH2. reflexivity.
Qed.

Lemma maxvar_subst : forall sigma B e,
  (forall i, (maxvar (sigma i) <= B)%nat) ->
  (maxvar (psubst Z sigma e) <= B)%nat.
Proof.
  intros sigma B e H.
  induction e as [i|c|e1 IH1 e2 IH2|e1 IH1 e2 IH2]; simpl; auto; lia.
Qed.

Lemma maxvar_nth : forall es i,
  (maxvar (nth i es (PConst 0%Z))
   <= fold_right Nat.max O (map maxvar es))%nat.
Proof.
  induction es as [|e es IH]; intros i.
  - destruct i; simpl; lia.
  - destruct i as [|i]; simpl; [lia|]. specialize (IH i). lia.
Qed.

(* Every observable, untruncated.                                        *)
Definition gfull (x y : Z * Z) : nat -> Z * Z :=
  fun k => dmul Z Z.add Z.mul x
             (rpow (Z * Z) (d1 Z 0 1) (dmul Z Z.add Z.mul) y k).

(* THE LEAST OBSERVABLE ALGEBRA OF P10 IS NOT FINITELY GENERATED.  Take  *)
(* any finite list of its elements -- each a polynomial expression es_i  *)
(* in the observables g_k = x y^k.  Some observable is then not a        *)
(* polynomial in them.                                                   *)
Theorem p10_not_finitely_generated : forall es : list (pexp Z),
  exists M : nat, forall E : pexp Z,
    ~ (forall x y : Z * Z,
         zpevD E (fun i => zpevD (nth i es (PConst 0)) (gfull x y))
         = gfull x y M).
Proof.
  intros es. set (T := fold_right Nat.max O (map maxvar es)).
  exists (S T). intros E H.
  apply (p10_next_observable_is_new T
           (psubst Z (fun i => nth i es (PConst 0)) E)).
  intros x y. specialize (H x y). unfold zpevD in *.
  rewrite (pevDZ_agree _ (genv Z 0 1 Z.add Z.mul T x y) (gfull x y)).
  - rewrite pevDZ_subst. exact H.
  - intros k Hk.
    pose proof (maxvar_subst (fun i => nth i es (PConst 0)) T E
                  (maxvar_nth es)) as Hb.
    unfold genv, gfull. destruct (Nat.ltb_spec k (S T)); [reflexivity|lia].
Qed.

(* P8's refusal at r = 2, uniformly in the degree: x -> x^d admits no    *)
(* update of (S, Q), every d >= 2, every extent N >= 3, positive arrays  *)
(* included.  One collision serves all d because 7^k outgrows            *)
(* 1 + 5^k + 6^k from k = 3 on.                                          *)
Lemma zpow_dominance : forall k, (3 <= k)%nat ->
  1 + zpow 5 k + zpow 6 k < zpow 7 k.
Proof.
  induction k as [|k IH]; intros Hk; [lia|].
  destruct (Nat.eq_dec k 2) as [E|E].
  - subst. vm_compute. reflexivity.
  - specialize (IH ltac:(lia)). rewrite !zpow_succ.
    pose proof (zpow_pos 5 k ltac:(lia)).
    pose proof (zpow_pos 6 k ltac:(lia)). lia.
Qed.

Lemma zq2_pow3 : forall d a b c,
  zpsum 2 (map (fun x => zpow x d) [a; b; c])
  = zpow a (d + d) + zpow b (d + d) + zpow c (d + d).
Proof.
  intros d a b c. unfold zpsum, psum. cbn [map rsum rpow].
  rewrite !zpow_add. ring.
Qed.

Theorem power_not_closed_SQ : forall d N, (2 <= d)%nat -> (3 <= N)%nat ->
  forall G : unit -> Z * Z -> Z * Z,
    ~ (forall u xs, pos_adm N xs -> zq2 (powmap d u xs) = G u (zq2 xs)).
Proof.
  intros d N Hd HN.
  apply (collision_refutes_update unit (list Z) (Z * Z) (powmap d) zq2
           (pos_adm N) tt
           ([1; 5; 6] ++ map Z.of_nat (seq 8 (N - 3)))
           ([2; 3; 7] ++ map Z.of_nat (seq 8 (N - 3)))).
  - split; [|apply collision_tail_positive; lia].
    rewrite app_length, map_length, seq_length. simpl. lia.
  - split; [|apply collision_tail_positive; lia].
    rewrite app_length, map_length, seq_length. simpl. lia.
  - unfold zq2. rewrite !zpsum_app. reflexivity.
  - unfold zq2, powmap. rewrite !map_app, !zpsum_app. intro Hc.
    apply (f_equal snd) in Hc. cbn [snd] in Hc.
    apply Z.add_cancel_r in Hc. rewrite !zq2_pow3 in Hc.
    pose proof (zpow_dominance (d + d) ltac:(lia)).
    pose proof (zpow_pos 2 (d + d) ltac:(lia)).
    pose proof (zpow_pos 3 (d + d) ltac:(lia)).
    rewrite zpow_one in Hc. lia.
Qed.

(* ===================================================================== *)
(* Part F.  P8's refusal for x -> x^d at EVERY d >= 2 and r >= 1, with   *)
(* the draft's own threshold N >= d r -- against polynomial G (the       *)
(* draft's "algebraic mechanization alternative").  If p_r of the image  *)
(* were a polynomial in p_1 .. p_r, then r + 1 observations would factor *)
(* through r coordinates; the row form makes                             *)
(*   sum_(k<r) lam_k (k+1) X^k + mu (d r) X^(d r - 1)                    *)
(* vanish at every node of every point, and N >= d r distinct nodes      *)
(* leave it no room.                                                     *)
(* ===================================================================== *)

(* p_r of the image array: sum_i (x_i^d)^r, as written.                  *)
Fixpoint pPnF (n d r : nat) : pexp Z :=
  match n with
  | O => PConst 0
  | S n' => PAdd (pPnF n' d r) (ppow (ppow (PVar n') d) r)
  end.

Lemma pevDZ_ppow : forall e k rho,
  pevDZ (ppow e k) rho
  = rpow (Z * Z) (d1 Z 0 1) (dmul Z Z.add Z.mul) (pevDZ e rho) k.
Proof.
  intros e k rho. induction k as [|k IH]; [reflexivity|].
  cbn [ppow pevD rpow]. rewrite IH. reflexivity.
Qed.

(* ... which is p_(d r), over the dual numbers too.                      *)
Lemma pevDZ_pPnF : forall n d r rho,
  pevDZ (pPnF n d r) rho = pevDZ (pPn n (d * r)) rho.
Proof.
  induction n as [|n IH]; intros d r rho; [reflexivity|].
  cbn [pPnF pPn pevD]. rewrite IH. f_equal.
  rewrite !pevDZ_ppow. cbn [pevD].
  apply (rpow_rpow (Z * Z) (d0 Z 0) (d1 Z 0 1) (dadd Z Z.add)
           (dmul Z Z.add Z.mul) (dsub Z Z.sub) (dopp Z Z.opp)
           (dual_ring_theory Z 0 1 Z.add Z.mul Z.sub Z.opp InitialRing.Zth)).
Qed.

Lemma zsumf_trunc : forall (f : nat -> Z) r s, (r <= s)%nat ->
  zsumf (fun j => if Nat.ltb j r then f j else 0) s = zsumf f r.
Proof.
  intros f r. induction s as [|s IH]; intros Hrs.
  - assert (r = 0)%nat by lia. subst. reflexivity.
  - destruct (Nat.eq_dec r (S s)) as [E|E].
    + subst r. apply zsumf_ext. intros l Hl.
      destruct (Nat.ltb_spec l (S s)); [reflexivity|lia].
    + cbn [zsumf]. rewrite IH by lia.
      destruct (Nat.ltb_spec s r); [lia|]. lia.
Qed.

Lemma zsumf_S : forall f n, zsumf f (S n) = zsumf f n + f n.
Proof. reflexivity. Qed.

Theorem power_not_poly_closed : forall d r N,
  (2 <= d)%nat -> (1 <= r)%nat -> (d * r <= N)%nat ->
  forall G : pexp Z,
    ~ (forall rho,
         zpevD (pPnF N d r) rho
         = zpevD G (zqenvD r (fun k => pPn N (S k)) rho)).
Proof.
  intros d r N Hd Hr HN G HG.
  set (q := fun k => pPn N (S k)).
  set (O := fun i => if Nat.ltb i r then pPn N (S i) else pPn N (d * r)).
  set (Rr := fun i => if Nat.ltb i r then PVar i else G).
  assert (Hf : factors_through r (S r) q O Rr).
  { intros i Hi rho. unfold O, Rr. destruct (Nat.ltb_spec i r) as [Hlt|Hge].
    - unfold zpevD, zqenvD, qenvD. cbn [pevD].
      destruct (Nat.ltb_spec i r); [reflexivity|lia].
    - rewrite <- HG. unfold zpevD. symmetry. apply pevDZ_pPnF. }
  destruct (poly_rank_bound_rows r (S r) q O Rr ltac:(lia) Hf
              (fun i => Z.of_nat i))
    as [lam [[i0 [Hi0 Hnz]] Hdep]].
  destruct (d * r)%nat as [|K] eqn:EK; [nia|].
  assert (HK : (r <= K)%nat) by nia.
  set (c := fun j => (if Nat.ltb j r then lam j * Z.of_nat (S j) else 0)
                     + (if Nat.eqb j K then lam r * Z.of_nat (S K) else 0)).
  assert (Hc : forall j, (j < S K)%nat -> c j = 0).
  { apply (many_roots_fn (S K) c (fun t => Z.of_nat t)).
    - intros t t' _ _ E. lia.
    - intros t Ht.
      etransitivity;
        [|apply (Hdep (fun i => if Nat.eqb i t then 1 else 0))].
      unfold c.
      rewrite (zsumf_ext _
                 (fun j => (if Nat.ltb j r
                            then lam j * Z.of_nat (S j) * zpow (Z.of_nat t) j
                            else 0)
                           + (if Nat.eqb j K
                              then lam r * Z.of_nat (S K)
                                   * zpow (Z.of_nat t) j
                              else 0)) (S K))
        by (intros j _; destruct (Nat.ltb j r), (Nat.eqb j K); ring).
      rewrite zsumf_add, zsumf_trunc by lia.
      rewrite (zsumf_single _ (S K) K) by
        (first [lia
               | intros i _ Hne; destruct (Nat.eqb_spec i K);
                 [contradiction|reflexivity]]).
      rewrite Nat.eqb_refl, (zsumf_S _ r).
      rewrite (zsumf_ext
                 (fun i => lam i * zD (O i) (fun i1 => Z.of_nat i1)
                                     (fun i1 => if Nat.eqb i1 t then 1 else 0))
                 (fun i => lam i * Z.of_nat (S i) * zpow (Z.of_nat t) i) r).
      + unfold O. cbv beta. destruct (Nat.ltb_spec r r); [lia|].
        rewrite zD_pPn_unit by lia. cbv beta. ring.
      + intros i Hi. unfold O. cbv beta.
        destruct (Nat.ltb_spec i r); [|lia].
        rewrite zD_pPn_unit by lia. cbv beta. ring. }
  apply Hnz. destruct (Nat.eq_dec i0 r) as [E|E].
  - subst i0. specialize (Hc K ltac:(lia)). unfold c in Hc.
    destruct (Nat.ltb_spec K r); [lia|]. rewrite Nat.eqb_refl in Hc.
    assert (Hm : lam r * Z.of_nat (S K) = 0) by lia.
    apply Z.mul_eq_0 in Hm. destruct Hm; lia.
  - specialize (Hc i0 ltac:(lia)). unfold c in Hc.
    destruct (Nat.ltb_spec i0 r); [|lia].
    destruct (Nat.eqb_spec i0 K); [lia|].
    assert (Hm : lam i0 * Z.of_nat (S i0) = 0) by lia.
    apply Z.mul_eq_0 in Hm. destruct Hm; lia.
Qed.
