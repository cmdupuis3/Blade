(* ===================================================================== *)
(* BladeProuhet.v -- EXACT RECURRENCE REDUCTION, P8's refusal against    *)
(* EVERY update function (docs/research/exact-recurrence-reduction-      *)
(* proofs.md, P8, negative half), by Prouhet-Tarry-Escott collisions.    *)
(*                                                                       *)
(* The draft refuses closure of q_r = (p_1 .. p_r) under a degree-d      *)
(* update by moving p_(dr) while p_1 .. p_(dr-1) stand still -- local    *)
(* coordinates, hence the inverse function theorem.  The algebra in that *)
(* argument needs no analysis at all, only a PAIR OF ARRAYS that agree   *)
(* on p_0 .. p_(dr-1) and disagree on p_(dr).  Such pairs have been      *)
(* known since 1851:                                                     *)
(*                                                                       *)
(*   aff_low, aff_top          how power-sum differences move under an   *)
(*                             affine map -- BladeMomentClosure's P4     *)
(*                             theorem read twice;                       *)
(*   pte, pte_spec             PROUHET'S CONSTRUCTION: from a pair       *)
(*                             agreeing on p_0 .. p_(m-1) and            *)
(*                             disagreeing on p_m, (A ++ (B+1), B ++     *)
(*                             (A+1)) agrees one further and its first   *)
(*                             disagreement is -(m+1) times the old one. *)
(*                              Sizes 2^(m-1) (pte_length).              *)
(*                                                                       *)
(*   psum_Fgen, plpow_top      the draft's expansion (P2): for the       *)
(*                             update x_i' = sum_j a_j(q_r x) x_i^j, p_r *)
(*                             of the image is sum_k c_k p_k, c the      *)
(*                             coefficients of phi^r, and the top one is *)
(*                             a_d^r;                                    *)
(*   closure_refused_at_collision  THE REFUSAL: any such pair of arrays, *)
(*                             where a_d does not vanish, refutes every  *)
(*                             update function G -- continuous or not.   *)
(*                             The a_j are ARBITRARY functions of the    *)
(*                             summary;                                  *)
(*   closure_refused_prouhet   with Prouhet's pair: every d >= 2, r >=   *)
(*                             1, every extent N >= 2^(dr-1);            *)
(*   power_never_closes        in particular x -> x^d, unconditionally   *)
(*                             (power_never_closes_Z over the integers); *)
(*   closure_refused_orthogonal_array  the draft's OWN threshold N = d   *)
(*                             r, wherever the ring has an array whose   *)
(*                             first dr - 1 power sums vanish and whose  *)
(*                             (dr)-th does not -- the (dr)-th roots of  *)
(*                             unity.                                    *)
(*                                                                       *)
(* Everything is over an abstract integral domain of characteristic zero *)
(* (hypotheses: ring_theory, no zero divisors, 1 <> 0, n + 1 <> 0), so   *)
(* it reads at Z, Q, R and C alike.                                      *)
(*                                                                       *)
(* Scope, stated once.  THE THRESHOLD IS 2^(dr-1), NOT THE DRAFT'S d r.  *)
(* That gap is not slack in the proof.  A pair of extent m agreeing on   *)
(* p_1 .. p_(m-1) is an IDEAL Prouhet-Tarry-Escott solution; over Z      *)
(* these are known only for small m (m <= 10 and m = 12 at the time of   *)
(* writing), and their existence in general is an open problem.  Over R  *)
(* they exist for every m, but only by analysis (the draft's route, or   *)
(* the roots of T_m(X) = c). Over C they are the roots of unity, which   *)
(* is Part D.  So the draft's threshold is a statement about the REALS   *)
(* and is not reached here; what is reached is the same refusal at a     *)
(* larger extent, with no axioms. For coefficient POLYNOMIALS a_j the    *)
(* leading one may vanish at the Prouhet point; the theorem then asks    *)
(* for another collision point, and "a_d not identically zero gives one" *)
(* is not proved.                                                        *)
(*                                                                       *)
(* Imports BladeBinomial, BladeSummary, BladeMomentClosure.  Coq 8.18,   *)
(* stdlib only.                                                          *)
(* ===================================================================== *)

From Blade Require Import BladeBinomial BladeSummary BladeMomentClosure.
Require Import List Arith Lia Ring.
Import ListNotations.

Lemma C_diag : forall n, C n n = 1.
Proof.
  induction n as [|n IH]; [reflexivity|].
  change (C (S n) (S n)) with (C n n + C n (S n)).
  rewrite IH, (C_small n (S n)) by lia. reflexivity.
Qed.

Lemma C_succ_pred : forall k, C (S k) k = S k.
Proof.
  induction k as [|k IH]; [reflexivity|].
  change (C (S (S k)) (S k)) with (C (S k) k + C (S k) (S k)).
  rewrite IH, C_diag. lia.
Qed.

Section Prouhet.
  Variable R : Type.
  Variables (r0 r1 : R) (radd rmul rsub : R -> R -> R) (ropp : R -> R).
  Hypothesis Rth : ring_theory r0 r1 radd rmul rsub ropp (@eq R).
  Add Ring prouhet_ring : Rth.
  Hypothesis R_integral : forall a b, rmul a b = r0 -> a = r0 \/ b = r0.
  Hypothesis R_nontrivial : r1 <> r0.
  Hypothesis R_char0 : forall n, ofnat R r0 r1 radd (S n) <> r0.

  Local Infix "+!" := radd (at level 50, left associativity).
  Local Infix "*!" := rmul (at level 40, left associativity).
  Local Infix "-!" := rsub (at level 50, left associativity).

  Local Notation Psum := (psum R r0 r1 radd rmul).
  Local Notation Rsum := (rsum R r0 radd).
  Local Notation Rpow := (rpow R r1 rmul).
  Local Notation Ofnat := (ofnat R r0 r1 radd).
  Local Notation Aff := (aff R radd rmul).
  Local Notation Peval := (peval R r0 radd rmul).
  Local Notation Padd := (padd R radd).
  Local Notation Pscale := (pscale R rmul).
  Local Notation Mdot := (mdot R r0 r1 radd rmul).
  Local Notation Qr := (qr R r0 r1 radd rmul).

  Let tower := affine_moment_tower_closed R r0 r1 radd rmul rsub ropp Rth.
  Let sum_app := rsum_app R r0 r1 radd rmul rsub ropp Rth.
  Let p_app := psum_app R r0 r1 radd rmul rsub ropp Rth.

  (* =================================================================== *)
  (* Part A.  Power-sum differences under an affine map.  Dp j is the    *)
  (* difference of the j-th power sums of two arrays.                    *)
  (* =================================================================== *)

  Definition Dp (j : nat) (A B : list R) : R := Psum j A -! Psum j B.

  Lemma D_zero_eq : forall j A B, Dp j A B = r0 -> Psum j A = Psum j B.
  Proof.
    intros j A B H. unfold Dp in H.
    replace (Psum j A) with ((Psum j A -! Psum j B) +! Psum j B) by ring.
    rewrite H. ring.
  Qed.

  Lemma rpow_one : forall k, Rpow r1 k = r1.
  Proof. induction k as [|k IH]; simpl; [reflexivity|]. rewrite IH. ring. Qed.

  Lemma rpow_nonzero : forall a k, a <> r0 -> Rpow a k <> r0.
  Proof.
    intros a k Ha. induction k as [|k IH]; simpl; [exact R_nontrivial|].
    intro E. apply R_integral in E. destruct E; contradiction.
  Qed.

  (* Below the first disagreement an affine map scales the difference.   *)
  Lemma aff_low : forall a b n A B,
    (forall j, j < n -> Dp j A B = r0) ->
    Dp n (map (Aff a b) A) (map (Aff a b) B) = Rpow a n *! Dp n A B.
  Proof.
    intros a b n A B Hlow. unfold Dp.
    rewrite !tower, seq_S, !map_app, !sum_app.
    assert (E : map (fun j => Ofnat (C n j) *! Rpow a j *! Rpow b (n - j)
                              *! Psum j A) (seq 0 n)
                = map (fun j => Ofnat (C n j) *! Rpow a j *! Rpow b (n - j)
                                *! Psum j B) (seq 0 n)).
    { apply map_ext_in. intros j Hj. apply in_seq in Hj.
      rewrite (D_zero_eq j A B) by (apply Hlow; lia). reflexivity. }
    rewrite E. cbn [map rsum Nat.add]. rewrite C_diag, Nat.sub_diag.
    simpl. ring.
  Qed.

  (* One step above it, the map's offset enters, with the binomial       *)
  (* coefficient C(k+1, k) = k + 1.                                      *)
  Lemma aff_top : forall a b k A B,
    (forall j, j < k -> Dp j A B = r0) ->
    Dp (S k) (map (Aff a b) A) (map (Aff a b) B)
    = Rpow a (S k) *! Dp (S k) A B
      +! Ofnat (S k) *! Rpow a k *! b *! Dp k A B.
  Proof.
    intros a b k A B Hlow. unfold Dp.
    rewrite !tower, (seq_S (S k) 0), (seq_S k 0), !map_app, !sum_app.
    assert (E : map (fun j => Ofnat (C (S k) j) *! Rpow a j
                              *! Rpow b (S k - j) *! Psum j A) (seq 0 k)
                = map (fun j => Ofnat (C (S k) j) *! Rpow a j
                                *! Rpow b (S k - j) *! Psum j B) (seq 0 k)).
    { apply map_ext_in. intros j Hj. apply in_seq in Hj.
      rewrite (D_zero_eq j A B) by (apply Hlow; lia). reflexivity. }
    rewrite E.
    match goal with
    | |- context [Rsum (map ?f (seq 0 k))] =>
        set (T := Rsum (map f (seq 0 k)))
    end.
    cbn [map rsum Nat.add]. rewrite C_diag, C_succ_pred.
    replace (S k - k) with 1 by lia. rewrite Nat.sub_diag. simpl. ring.
  Qed.

  Lemma D_app : forall j A B A' B',
    Dp j (A ++ A') (B ++ B') = Dp j A B +! Dp j A' B'.
  Proof. intros. unfold Dp. rewrite !p_app. ring. Qed.

  Lemma D_refl : forall j A, Dp j A A = r0.
  Proof. intros. unfold Dp. ring. Qed.

  Lemma D_swap : forall j A B, Dp j B A = ropp (Dp j A B).
  Proof. intros. unfold Dp. ring. Qed.

  (* =================================================================== *)
  (* Part B.  Prouhet's construction.  From a pair that agrees on        *)
  (* p_0 .. p_(m-1) and disagrees on p_m, shifting and crossing gives a  *)
  (* pair that agrees one further.  The shift is x -> x + 1.             *)
  (* =================================================================== *)

  Definition shift1 (xs : list R) : list R := map (Aff r1 r1) xs.

  Fixpoint pte (m : nat) : list R * list R :=
    match m with
    | 0 => ([r0], [])
    | S m' => (fst (pte m') ++ shift1 (snd (pte m')),
               snd (pte m') ++ shift1 (fst (pte m')))
    end.

  Theorem pte_spec : forall m,
    (forall j, j < m -> Dp j (fst (pte m)) (snd (pte m)) = r0)
    /\ Dp m (fst (pte m)) (snd (pte m)) <> r0.
  Proof.
    induction m as [|m [IHlow IHtop]].
    - split; [intros j Hj; lia|]. unfold Dp, psum. simpl.
      intro E. apply R_nontrivial.
      replace r1 with (r1 +! r0 -! r0) by ring. exact E.
    - set (A := fst (pte m)) in *. set (B := snd (pte m)) in *.
      change (fst (pte (S m))) with (A ++ shift1 B).
      change (snd (pte (S m))) with (B ++ shift1 A).
      split.
      + intros j Hj. rewrite D_app, (D_swap j (shift1 A) (shift1 B)).
        unfold shift1. rewrite aff_low by (intros i Hi; apply IHlow; lia).
        rewrite rpow_one. ring.
      + rewrite D_app, (D_swap (S m) (shift1 A) (shift1 B)).
        unfold shift1. rewrite (aff_top r1 r1 m A B IHlow), !rpow_one.
        intro E.
        assert (E2 : Ofnat (S m) *! Dp m A B = r0).
        { replace (Ofnat (S m) *! Dp m A B)
            with (ropp (Dp (S m) A B
                        +! ropp (r1 *! Dp (S m) A B
                                 +! Ofnat (S m) *! r1 *! r1 *! Dp m A B)))
            by ring.
          rewrite E. ring. }
        apply R_integral in E2. destruct E2 as [E2|E2];
          [exact (R_char0 m E2)|exact (IHtop E2)].
  Qed.

  Lemma pte_length : forall m,
    length (fst (pte (S m))) = 2 ^ m /\ length (snd (pte (S m))) = 2 ^ m.
  Proof.
    induction m as [|m [IH1 IH2]]; [split; reflexivity|].
    change (fst (pte (S (S m))))
      with (fst (pte (S m)) ++ shift1 (snd (pte (S m)))).
    change (snd (pte (S (S m))))
      with (snd (pte (S m)) ++ shift1 (fst (pte (S m)))).
    unfold shift1. rewrite !app_length, !map_length, IH1, IH2.
    simpl Nat.pow. lia.
  Qed.

  (* =================================================================== *)
  (* Part C.  The draft's expansion (P2): for a shared-coefficient       *)
  (* polynomial update, p_r of the image is sum_k c_k p_k with c the     *)
  (* coefficients of phi^r -- functions of the summary alone -- and the  *)
  (* top one is a_d^r.                                                   *)
  (* =================================================================== *)

  Let pe_padd := peval_padd R r0 r1 radd rmul rsub ropp Rth.
  Let pe_pscale := peval_pscale R r0 r1 radd rmul rsub ropp Rth.
  Let n_padd := nth_padd R r0 r1 radd rmul rsub ropp Rth.
  Let n_pscale := nth_pscale R r0 r1 radd rmul rsub ropp Rth.

  Fixpoint pmul (l1 l2 : list R) : list R :=
    match l1 with
    | [] => []
    | c :: l1' => Padd (Pscale c l2) (r0 :: pmul l1' l2)
    end.

  Lemma peval_pmul : forall l1 l2 x,
    Peval (pmul l1 l2) x = Peval l1 x *! Peval l2 x.
  Proof.
    induction l1 as [|c l1 IH]; intros l2 x; simpl; [ring|].
    rewrite pe_padd, pe_pscale. simpl. rewrite IH. ring.
  Qed.

  Lemma pmul_length : forall l1 l2 n1 n2,
    length l1 = S n1 -> length l2 = S n2 ->
    length (pmul l1 l2) = S (n1 + n2).
  Proof.
    induction l1 as [|c l1 IH]; intros l2 n1 n2 H1 H2; [discriminate|].
    cbn [pmul]. rewrite padd_length. unfold pscale. rewrite map_length, H2.
    destruct l1 as [|c' l1'].
    - simpl in H1. assert (n1 = 0) by lia. subst n1. simpl. lia.
    - destruct n1 as [|n1']; [simpl in H1; lia|].
      cbn [length]. rewrite (IH l2 n1' n2) by (simpl in *; lia). lia.
  Qed.

  Lemma nth_single_zero : forall j, nth j [r0] r0 = r0.
  Proof. intros [|[|j]]; reflexivity. Qed.

  Lemma pmul_top : forall l1 l2 n1 n2,
    length l1 = S n1 -> length l2 = S n2 ->
    nth (n1 + n2) (pmul l1 l2) r0 = nth n1 l1 r0 *! nth n2 l2 r0.
  Proof.
    induction l1 as [|c l1 IH]; intros l2 n1 n2 H1 H2; [discriminate|].
    cbn [pmul]. rewrite n_padd, n_pscale.
    destruct l1 as [|c' l1'].
    - simpl in H1. assert (n1 = 0) by lia. subst n1.
      cbn [pmul Nat.add]. rewrite nth_single_zero. cbn [nth]. ring.
    - destruct n1 as [|n1']; [simpl in H1; lia|].
      remember (c' :: l1') as t eqn:Et. cbn [Nat.add nth].
      rewrite (IH l2 n1' n2) by (simpl in H1; lia).
      rewrite (nth_overflow l2) by lia. ring.
  Qed.

  Fixpoint plpow (l : list R) (k : nat) : list R :=
    match k with 0 => [r1] | S k' => pmul l (plpow l k') end.

  Lemma peval_plpow : forall l k x,
    Peval (plpow l k) x = Rpow (Peval l x) k.
  Proof.
    intros l. induction k as [|k IH]; intros x; simpl; [ring|].
    rewrite peval_pmul, IH. reflexivity.
  Qed.

  Lemma plpow_length : forall l d k, length l = S d ->
    length (plpow l k) = S (d * k).
  Proof.
    intros l d k Hl. induction k as [|k IH].
    - rewrite Nat.mul_0_r. reflexivity.
    - cbn [plpow]. rewrite (pmul_length l (plpow l k) d (d * k) Hl IH).
      rewrite Nat.mul_succ_r. f_equal. lia.
  Qed.

  Lemma plpow_top : forall l d k, length l = S d ->
    nth (d * k) (plpow l k) r0 = Rpow (nth d l r0) k.
  Proof.
    intros l d k Hl. induction k as [|k IH].
    - rewrite Nat.mul_0_r. reflexivity.
    - cbn [plpow rpow]. rewrite <- IH.
      replace (d * S k) with (d + d * k) by (rewrite Nat.mul_succ_r; lia).
      apply pmul_top; [exact Hl|apply plpow_length; exact Hl].
  Qed.

  (* Two arrays that agree on p_k0 .. p_(k0+n-1) are told apart by a     *)
  (* polynomial of degree n only through its top coefficient.            *)
  Lemma mdot_diff : forall l k0 n x x',
    (forall k, k < n -> Psum (k0 + k) x = Psum (k0 + k) x') ->
    length l <= S n ->
    Mdot l k0 x -! Mdot l k0 x'
    = nth n l r0 *! (Psum (k0 + n) x -! Psum (k0 + n) x').
  Proof.
    induction l as [|c l IH]; intros k0 n x x' Heq Hlen.
    - simpl. destruct n; simpl; ring.
    - cbn [mdot]. destruct n as [|n].
      + destruct l as [|c' l']; [|simpl in Hlen; lia].
        simpl. rewrite Nat.add_0_r. ring.
      + assert (E0 : Psum k0 x = Psum k0 x').
        { rewrite <- (Nat.add_0_r k0). apply Heq. lia. }
        rewrite E0. cbn [nth].
        replace (k0 + S n) with (S k0 + n) by lia.
        rewrite <- (IH (S k0) n x x').
        * ring.
        * intros k Hk. replace (S k0 + k) with (k0 + S k) by lia.
          apply Heq. lia.
        * simpl in Hlen. lia.
  Qed.

  (* The update  x_i' = sum_(j<=d) a_j(q_r x) x_i^j,  the a_j ARBITRARY  *)
  (* functions of the summary (polynomial or not).                       *)
  Definition coefs (d r : nat) (a : nat -> list R -> R) (x : list R)
    : list R :=
    map (fun j => a j (Qr r x)) (seq 0 (S d)).

  Definition Fgen (d r : nat) (a : nat -> list R -> R) (x : list R)
    : list R :=
    map (fun t => Peval (coefs d r a x) t) x.

  Lemma coefs_length : forall d r a x, length (coefs d r a x) = S d.
  Proof.
    intros. unfold coefs. rewrite map_length, seq_length. reflexivity.
  Qed.

  Lemma coefs_top : forall d r a x,
    nth d (coefs d r a x) r0 = a d (Qr r x).
  Proof.
    intros d r a x. unfold coefs.
    rewrite (nth_map_lt R nat _ _ d r0 0) by (rewrite seq_length; lia).
    rewrite seq_nth by lia. reflexivity.
  Qed.

  Lemma psum_Fgen : forall d r a x,
    Psum r (Fgen d r a x) = Mdot (plpow (coefs d r a x) r) 0 x.
  Proof.
    intros d r a x. unfold Fgen, psum. rewrite map_map.
    rewrite <- (rsum_peval R r0 r1 radd rmul rsub ropp Rth).
    f_equal. apply map_ext. intro t. rewrite peval_plpow. simpl. ring.
  Qed.

  Lemma Fgen_length : forall d r a x, length (Fgen d r a x) = length x.
  Proof. intros. unfold Fgen. apply map_length. Qed.

  (* THE REFUSAL, AT A COLLISION.  Any two arrays that agree on          *)
  (* p_0 .. p_(dr-1), disagree on p_(dr), and sit where the leading      *)
  (* coefficient is nonzero refute every update function of the first r  *)
  (* power sums.                                                         *)
  Theorem closure_refused_at_collision : forall d r N a x x',
    2 <= d -> 1 <= r ->
    length x = N -> length x' = N ->
    (forall k, k < d * r -> Psum k x = Psum k x') ->
    Psum (d * r) x <> Psum (d * r) x' ->
    a d (Qr r x) <> r0 ->
    forall G : unit -> list R -> list R,
      ~ (forall u y, length y = N -> Qr r (Fgen d r a y) = G u (Qr r y)).
  Proof.
    intros d r N a x x' Hd Hr Hx Hx' Heq Hne Hlead.
    assert (Hrm : r < d * r) by nia.
    assert (Hq : Qr r x = Qr r x').
    { unfold qr. apply map_ext_in. intros k Hk. apply in_seq in Hk.
      apply Heq. lia. }
    apply (collision_refutes_update unit (list R) (list R)
             (fun _ y => Fgen d r a y) (Qr r) (fun y => length y = N)
             tt x x' Hx Hx' Hq).
    intro E.
    apply (f_equal (fun l => nth (r - 1) l r0)) in E. unfold qr in E.
    rewrite !(nth_map_lt R nat _ _ (r - 1) r0 0) in E
      by (rewrite seq_length; lia).
    rewrite seq_nth in E by lia. replace (1 + (r - 1)) with r in E by lia.
    rewrite !psum_Fgen in E. unfold coefs in E. rewrite <- Hq in E.
    fold (coefs d r a x) in E.
    set (psi := plpow (coefs d r a x) r) in *.
    pose proof (mdot_diff psi 0 (d * r) x x') as Hdiff.
    cbn [Nat.add] in Hdiff.
    rewrite E in Hdiff.
    assert (Hz : nth (d * r) psi r0
                 *! (Psum (d * r) x -! Psum (d * r) x') = r0).
    { rewrite <- Hdiff.
      - ring.
      - exact Heq.
      - unfold psi.
        rewrite (plpow_length _ d r (coefs_length d r a x)). lia. }
    apply R_integral in Hz. destruct Hz as [Hz|Hz].
    - unfold psi in Hz.
      rewrite (plpow_top _ d r (coefs_length d r a x)), coefs_top in Hz.
      exact (rpow_nonzero _ r Hlead Hz).
    - apply Hne. apply D_zero_eq. exact Hz.
  Qed.

  (* Prouhet pairs, padded with a common tail to any extent.             *)
  Definition pte_x (m N : nat) : list R :=
    fst (pte m) ++ repeat r0 (N - 2 ^ (m - 1)).
  Definition pte_x' (m N : nat) : list R :=
    snd (pte m) ++ repeat r0 (N - 2 ^ (m - 1)).

  (* P8's refusal against EVERY update function, every d >= 2, r >= 1,   *)
  (* for the general fragment, wherever the leading coefficient does not *)
  (* vanish at the Prouhet point.                                        *)
  Theorem closure_refused_prouhet : forall d r N a,
    2 <= d -> 1 <= r -> 2 ^ (d * r - 1) <= N ->
    a d (Qr r (pte_x (d * r) N)) <> r0 ->
    forall G : unit -> list R -> list R,
      ~ (forall u y, length y = N -> Qr r (Fgen d r a y) = G u (Qr r y)).
  Proof.
    intros d r N a Hd Hr HN Hlead.
    assert (Hm : exists m', d * r = S m') by (exists (d * r - 1); nia).
    destruct Hm as [m' Em].
    destruct (pte_spec (d * r)) as [Hlow Htop].
    destruct (pte_length m') as [L1 L2]. rewrite <- Em in L1, L2.
    apply (closure_refused_at_collision d r N a
             (pte_x (d * r) N) (pte_x' (d * r) N) Hd Hr).
    - unfold pte_x. rewrite app_length, repeat_length, L1.
      replace (d * r - 1) with m' by lia.
      replace (d * r - 1) with m' in HN by lia. lia.
    - unfold pte_x'. rewrite app_length, repeat_length, L2.
      replace (d * r - 1) with m' by lia.
      replace (d * r - 1) with m' in HN by lia. lia.
    - intros k Hk. apply D_zero_eq. unfold pte_x, pte_x'.
      rewrite D_app, D_refl, (Hlow k Hk). ring.
    - intro E. apply Htop.
      assert (E2 : Dp (d * r) (pte_x (d * r) N) (pte_x' (d * r) N) = r0)
        by (unfold Dp; rewrite E; ring).
      unfold pte_x, pte_x' in E2. rewrite D_app, D_refl in E2.
      rewrite <- E2. ring.
    - exact Hlead.
  Qed.

  (* In particular whenever the leading coefficient vanishes nowhere --  *)
  (* a nonzero constant, as for the pure power x -> x^d.                 *)
  Corollary closure_refused_nowhere_zero_lead : forall d r N a,
    2 <= d -> 1 <= r -> 2 ^ (d * r - 1) <= N ->
    (forall z, a d z <> r0) ->
    forall G : unit -> list R -> list R,
      ~ (forall u y, length y = N -> Qr r (Fgen d r a y) = G u (Qr r y)).
  Proof.
    intros d r N a Hd Hr HN Hlead.
    apply closure_refused_prouhet; auto.
  Qed.

  (* The pure power x -> x^d, written as it would be written.            *)
  Definition pure (d : nat) : nat -> list R -> R :=
    fun j _ => if Nat.eqb j d then r1 else r0.

  Lemma peval_monomial : forall d k t,
    Peval (map (fun j => if Nat.eqb j (k + d) then r1 else r0)
               (seq k (S d))) t
    = Rpow t d.
  Proof.
    induction d as [|d IH]; intros k t.
    - cbn [seq map peval]. rewrite Nat.add_0_r, Nat.eqb_refl. simpl. ring.
    - change (seq k (S (S d))) with (k :: seq (S k) (S d)).
      cbn [map peval]. destruct (Nat.eqb_spec k (k + S d)); [lia|].
      rewrite (map_ext (fun j => if Nat.eqb j (k + S d) then r1 else r0)
                 (fun j => if Nat.eqb j (S k + d) then r1 else r0))
        by (intro j; replace (k + S d) with (S k + d) by lia; reflexivity).
      rewrite IH. simpl. ring.
  Qed.

  Lemma Fgen_pure : forall d r x,
    Fgen d r (pure d) x = map (fun t => Rpow t d) x.
  Proof.
    intros d r x. unfold Fgen, coefs, pure. apply map_ext. intro t.
    apply (peval_monomial d 0 t).
  Qed.

  (* P8's refusal for x -> x^d against EVERY update function of the      *)
  (* first r power sums: every d >= 2, every r >= 1, every extent from   *)
  (* 2^(dr-1) on.                                                        *)
  Theorem power_never_closes : forall d r N,
    2 <= d -> 1 <= r -> 2 ^ (d * r - 1) <= N ->
    forall G : unit -> list R -> list R,
      ~ (forall u y, length y = N ->
           Qr r (map (fun t => Rpow t d) y) = G u (Qr r y)).
  Proof.
    intros d r N Hd Hr HN G H.
    apply (closure_refused_nowhere_zero_lead d r N (pure d) Hd Hr HN)
      with (G := G).
    - intros z. unfold pure. rewrite Nat.eqb_refl. exact R_nontrivial.
    - intros u y Hy. rewrite Fgen_pure. apply H. exact Hy.
  Qed.

  (* The same from ANY collision pair -- this is what a pair found by    *)
  (* other means (reals/BladeRealCollision) plugs into.                  *)
  Theorem power_refused_at_collision : forall d r N x x',
    2 <= d -> 1 <= r -> length x = N -> length x' = N ->
    (forall k, k < d * r -> Psum k x = Psum k x') ->
    Psum (d * r) x <> Psum (d * r) x' ->
    forall G : unit -> list R -> list R,
      ~ (forall u y, length y = N ->
           Qr r (map (fun t => Rpow t d) y) = G u (Qr r y)).
  Proof.
    intros d r N x x' Hd Hr Hx Hx' Heq Hne G H.
    apply (closure_refused_at_collision d r N (pure d) x x'
             Hd Hr Hx Hx' Heq Hne) with (G := G).
    - unfold pure. rewrite Nat.eqb_refl. exact R_nontrivial.
    - intros u y Hy. rewrite Fgen_pure. apply H. exact Hy.
  Qed.

  (* =================================================================== *)
  (* Part D.  The draft's own threshold N = d r, wherever the ring has   *)
  (* an array whose first dr - 1 power sums vanish and whose (dr)-th     *)
  (* does not -- the (dr)-th roots of unity, where they exist.  Scale it *)
  (* by any s with s^(dr) <> 1 and the two arrays collide.               *)
  (* =================================================================== *)

  Theorem closure_refused_orthogonal_array : forall d r a (Zs : list R) s,
    2 <= d -> 1 <= r ->
    (forall k, 0 < k < d * r -> Psum k Zs = r0) ->
    Psum (d * r) Zs <> r0 ->
    Rpow s (d * r) <> r1 ->
    a d (Qr r Zs) <> r0 ->
    forall G : unit -> list R -> list R,
      ~ (forall u y, length y = length Zs ->
           Qr r (Fgen d r a y) = G u (Qr r y)).
  Proof.
    intros d r a Zs s Hd Hr Horth Htop Hs Hlead.
    assert (Hsc : forall k, Psum k (map (fun x => s *! x) Zs)
                            = Rpow s k *! Psum k Zs).
    { intro k. apply (psum_scale R r0 r1 radd rmul rsub ropp Rth). }
    apply (closure_refused_at_collision d r (length Zs) a Zs
             (map (fun x => s *! x) Zs) Hd Hr eq_refl).
    - apply map_length.
    - intros k Hk. destruct k as [|k].
      + rewrite !(psum_0 R r0 r1 radd rmul), map_length. reflexivity.
      + rewrite Hsc, (Horth (S k)) by lia. ring.
    - rewrite Hsc. intro E.
      assert (E2 : (Rpow s (d * r) -! r1) *! Psum (d * r) Zs = r0).
      { replace ((Rpow s (d * r) -! r1) *! Psum (d * r) Zs)
          with (Rpow s (d * r) *! Psum (d * r) Zs -! Psum (d * r) Zs)
          by ring.
        rewrite <- E. ring. }
      apply R_integral in E2. destruct E2 as [E2|E2]; [|contradiction].
      apply Hs.
      replace (Rpow s (d * r)) with ((Rpow s (d * r) -! r1) +! r1) by ring.
      rewrite E2. ring.
    - exact Hlead.
  Qed.

End Prouhet.

(* ===================================================================== *)
(* Part E.  Closed instances over Z.                                     *)
(* ===================================================================== *)

Require Import ZArith.
Open Scope Z_scope.

Lemma Z_integral : forall a b : Z, a * b = 0 -> a = 0 \/ b = 0.
Proof. intros a b H. apply Z.mul_eq_0. exact H. Qed.

Lemma ofnat_Z_nonzero : forall n, ofnat Z 0 1 Z.add (S n) <> 0.
Proof.
  intros n. assert (E : forall k, ofnat Z 0 1 Z.add k = Z.of_nat k).
  { induction k as [|k IH]; [reflexivity|]. cbn [ofnat]. rewrite IH. lia. }
  rewrite E. lia.
Qed.

(* The draft's P8 over the integers, against every update function.      *)
Theorem power_never_closes_Z : forall d r N,
  (2 <= d)%nat -> (1 <= r)%nat -> (2 ^ (d * r - 1) <= N)%nat ->
  forall G : unit -> list Z -> list Z,
    ~ (forall u y, length y = N ->
         qr Z 0 1 Z.add Z.mul r (map (fun t => rpow Z 1 Z.mul t d) y)
         = G u (qr Z 0 1 Z.add Z.mul r y)).
Proof.
  apply (power_never_closes Z 0 1 Z.add Z.mul Z.sub Z.opp InitialRing.Zth
           Z_integral).
  - discriminate.
  - apply ofnat_Z_nonzero.
Qed.

(* The Prouhet pair at m = 3, computed: sums 6 and 6, squares 12 and     *)
(* 12, cubes 24 and 30.                                                  *)
Example prouhet_pair_3 :
  pte Z 0 1 Z.add Z.mul 3 = ([0; 2; 2; 2], [1; 1; 1; 3]).
Proof. vm_compute. reflexivity. Qed.

(* The draft's threshold N = d r attained over Z at d = 2, r = 1: the    *)
(* square roots of unity (1, -1) against their double.                   *)
Example square_vs_sum_at_threshold :
  forall G : unit -> list Z -> list Z,
    ~ (forall u y, length y = 2%nat ->
         qr Z 0 1 Z.add Z.mul 1
            (Fgen Z 0 1 Z.add Z.mul 2 1 (pure Z 0 1 2) y)
         = G u (qr Z 0 1 Z.add Z.mul 1 y)).
Proof.
  apply (closure_refused_orthogonal_array Z 0 1 Z.add Z.mul Z.sub Z.opp
           InitialRing.Zth Z_integral ltac:(discriminate)
           2 1 (pure Z 0 1 2) [1; -1] 2); try lia.
  - intros k Hk. assert (k = 1%nat) by lia. subst k. reflexivity.
  - vm_compute. discriminate.
  - vm_compute. discriminate.
  - vm_compute. discriminate.
Qed.
