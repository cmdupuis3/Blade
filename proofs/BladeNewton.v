(* ===================================================================== *)
(* BladeNewton.v -- EXACT RECURRENCE REDUCTION, Newton's identities and  *)
(* what they buy (docs/research/exact-recurrence-reduction-proofs.md,    *)
(* P8; the axiom-free half of the draft's threshold N >= d r).           *)
(*                                                                       *)
(* BladeProuhet refuses closure from any pair of arrays that agree on    *)
(* p_0 .. p_(m-1) and disagree on p_m, and builds such pairs over Z at   *)
(* extent 2^(m-1).  At extent m they are "ideal" solutions and none is   *)
(* known over Z in general.  Over R they come from a polynomial, not     *)
(* from a list: take prod (1 - x_i Y), bump ONE coefficient, and ask for *)
(* the roots again.  Everything in that sentence except "ask for the     *)
(* roots again" is algebra, and is here, over an abstract commutative    *)
(* ring:                                                                 *)
(*                                                                       *)
(*   elist, cf                 E(Y) = prod_i (1 - x_i Y) as a            *)
(*                             coefficient list; c_j its coefficients    *)
(*                             (the signed elementary symmetric          *)
(*                             functions);                               *)
(*   newton_identities         NEWTON:  sum_(j<k) c_j p_(k-j) + k c_k =  *)
(*                             0 for every k.  By induction on the       *)
(*                             roots: adding a root x sends N(k) to      *)
(*                             N(k) - x N(k-1) (newton_cons), the cross  *)
(*                             terms telescoping to nothing;             *)
(*   newton_difference         so the top m coefficients determine       *)
(*                             p_1 .. p_(m-1), and p_m moves with c_m at *)
(*                             rate -m;                                  *)
(*   many_roots_dom,           a polynomial with more distinct roots     *)
(*   coefficients_from_values  than coefficients is zero, over any       *)
(*                             integral domain -- hence two coefficient  *)
(*                             lists that agree as FUNCTIONS at enough   *)
(*                             points agree coefficient by coefficient;  *)
(*   ideal_pair_from_bump      if E(xs) with its m-th coefficient bumped *)
(*                             by t <> 0 is again some E(ys), then xs    *)
(*                             and ys agree on p_0 .. p_(m-1) and differ *)
(*                             on p_m (by m t): an ideal pair of their   *)
(*                             common extent.                            *)
(*                                                                       *)
(* Scope, stated once.  Nothing here produces the second array: that a   *)
(* bumped polynomial still splits into real linear factors is analysis   *)
(* (reals/BladeRealCollision.v, by the intermediate value theorem).      *)
(* Characteristic zero and "no zero divisors" are hypotheses, used only  *)
(* from Part C on; Newton's identities themselves hold over any          *)
(* commutative ring.                                                     *)
(*                                                                       *)
(* Imports BladeBinomial, BladeSummary, BladeMomentClosure,              *)
(* BladeRankBound, BladeRankDomain.  Coq 8.18, stdlib only.              *)
(* ===================================================================== *)

From Blade Require Import BladeBinomial BladeSummary BladeMomentClosure
  BladeRankBound BladeRankDomain.
Require Import List Arith Lia Ring Wf_nat.
Import ListNotations.

Section Newton.
  Variable R : Type.
  Variables (r0 r1 : R) (radd rmul rsub : R -> R -> R) (ropp : R -> R).
  Hypothesis Rth : ring_theory r0 r1 radd rmul rsub ropp (@eq R).
  Add Ring newton_ring : Rth.

  Local Infix "+!" := radd (at level 50, left associativity).
  Local Infix "*!" := rmul (at level 40, left associativity).
  Local Infix "-!" := rsub (at level 50, left associativity).

  Local Notation Psum := (psum R r0 r1 radd rmul).
  Local Notation Rpow := (rpow R r1 rmul).
  Local Notation Ofnat := (ofnat R r0 r1 radd).
  Local Notation Peval := (peval R r0 radd rmul).
  Local Notation Padd := (padd R radd).
  Local Notation Pscale := (pscale R rmul).
  Local Notation Ksumf := (ksumf R r0 radd).

  Let ks_ext := ksumf_ext R r0 radd.
  Let ks_zero := ksumf_zero R r0 r1 radd rmul rsub ropp Rth.
  Let ks_add := ksumf_add R r0 r1 radd rmul rsub ropp Rth.
  Let ks_scale := ksumf_scale R r0 r1 radd rmul rsub ropp Rth.
  Let ks_skip := ksumf_skip R r0 r1 radd rmul rsub ropp Rth.
  Let pe_padd := peval_padd R r0 r1 radd rmul rsub ropp Rth.
  Let pe_pscale := peval_pscale R r0 r1 radd rmul rsub ropp Rth.
  Let n_padd := nth_padd R r0 r1 radd rmul rsub ropp Rth.
  Let n_pscale := nth_pscale R r0 r1 radd rmul rsub ropp Rth.

  (* =================================================================== *)
  (* Part A.  The polynomial E(Y) = prod_i (1 - x_i Y) as a coefficient  *)
  (* list.  Its coefficients c_j are the signed elementary symmetric     *)
  (* functions; equally, the coefficients of prod (X - x_i) read from    *)
  (* the top.                                                            *)
  (* =================================================================== *)

  Fixpoint elist (xs : list R) : list R :=
    match xs with
    | [] => [r1]
    | x :: xs' => Padd (elist xs') (r0 :: Pscale (ropp x) (elist xs'))
    end.

  Definition cf (xs : list R) (j : nat) : R := nth j (elist xs) r0.

  Definition sh (c : nat -> R) (j : nat) : R :=
    match j with 0 => r0 | S j' => c j' end.

  Lemma cf_cons : forall x xs j,
    cf (x :: xs) j = cf xs j -! x *! sh (cf xs) j.
  Proof.
    intros x xs j. unfold cf. cbn [elist]. rewrite n_padd.
    destruct j as [|j]; cbn [nth sh]; [ring|]. rewrite n_pscale. ring.
  Qed.

  Lemma cf_zero : forall xs, cf xs 0 = r1.
  Proof.
    induction xs as [|x xs IH]; [reflexivity|]. rewrite cf_cons, IH.
    cbn [sh]. ring.
  Qed.

  Lemma elist_length : forall xs, length (elist xs) = S (length xs).
  Proof.
    induction xs as [|x xs IH]; [reflexivity|]. cbn [elist length].
    rewrite padd_length. cbn [length]. unfold pscale.
    rewrite map_length, IH. lia.
  Qed.

  Lemma peval_elist_cons : forall x xs Y,
    Peval (elist (x :: xs)) Y = Peval (elist xs) Y *! (r1 -! x *! Y).
  Proof.
    intros x xs Y. cbn [elist]. rewrite pe_padd. cbn [peval].
    rewrite pe_pscale. ring.
  Qed.

  Lemma peval_elist_zero : forall xs, Peval (elist xs) r0 = r1.
  Proof.
    induction xs as [|x xs IH]; [simpl; ring|].
    rewrite peval_elist_cons, IH. ring.
  Qed.

  Lemma peval_elist_root : forall xs x rho,
    In x xs -> x *! rho = r1 -> Peval (elist xs) rho = r0.
  Proof.
    induction xs as [|y xs IH]; intros x rho Hin Hx; [contradiction|].
    rewrite peval_elist_cons. destruct Hin as [->|Hin].
    - rewrite Hx. ring.
    - rewrite (IH x rho Hin Hx). ring.
  Qed.

  (* =================================================================== *)
  (* Part B.  NEWTON'S IDENTITIES.  For every k,                         *)
  (*     sum_(j<k) c_j p_(k-j)  +  k c_k  =  0.                          *)
  (* By induction on the roots: adding a root x sends N(k) to            *)
  (* N(k) - x N(k-1), the cross terms telescoping to nothing.            *)
  (* =================================================================== *)

  Definition newton (c p : nat -> R) (k : nat) : R :=
    Ksumf (fun j => c j *! p (k - j)) k +! Ofnat k *! c k.

  Lemma shift_sum : forall (c g : nat -> R) k,
    Ksumf (fun j => sh c j *! g (S k - j)) (S k)
    = Ksumf (fun i => c i *! g (k - i)) k.
  Proof.
    intros c g k. rewrite (ks_skip _ k 0) by lia. cbv beta. cbn [sh].
    rewrite (ks_ext (fun i => sh c (skip 0 i) *! g (S k - skip 0 i))
               (fun i => c i *! g (k - i)) k) by (intros; reflexivity).
    ring.
  Qed.

  Lemma newton_cons : forall x (c p : nat -> R) k,
    newton (fun j => c j -! x *! sh c j) (fun i => Rpow x i +! p i) (S k)
    = newton c p (S k) -! x *! newton c p k.
  Proof.
    intros x c p k. unfold newton.
    rewrite (ks_ext
               (fun j => (c j -! x *! sh c j)
                         *! (Rpow x (S k - j) +! p (S k - j)))
               (fun j => (c j *! p (S k - j) +! c j *! Rpow x (S k - j))
                         +! (ropp x *! (sh c j *! p (S k - j))
                             +! ropp x *! (sh c j *! Rpow x (S k - j))))
               (S k)) by (intros; ring).
    rewrite !ks_add, !ks_scale, !shift_sum.
    cbn [ksumf].
    rewrite (ks_ext (fun j => c j *! Rpow x (S k - j))
               (fun j => x *! (c j *! Rpow x (k - j))) k).
    - rewrite ks_scale. replace (S k - k) with 1 by lia.
      cbn [ofnat sh rpow]. ring.
    - intros j Hj. replace (S k - j) with (S (k - j)) by lia.
      cbn [rpow]. ring.
  Qed.

  Lemma newton_ext : forall c c' p p' k,
    (forall j, j <= k -> c j = c' j) ->
    (forall i, 1 <= i -> i <= k -> p i = p' i) ->
    newton c p k = newton c' p' k.
  Proof.
    intros c c' p p' k Hc Hp. unfold newton. rewrite (Hc k) by lia.
    f_equal. apply ks_ext. intros j Hj.
    rewrite (Hc j) by lia. rewrite (Hp (k - j)) by lia. reflexivity.
  Qed.

  Theorem newton_identities : forall xs k,
    newton (cf xs) (fun i => Psum i xs) k = r0.
  Proof.
    induction xs as [|x xs IH]; intros k.
    - unfold newton. rewrite ks_zero.
      + destruct k as [|k]; [simpl; ring|].
        unfold cf. simpl. destruct k; ring.
      + intros j _. unfold psum. simpl. ring.
    - destruct k as [|k]; [unfold newton; simpl; ring|].
      rewrite (newton_ext _ (fun j => cf xs j -! x *! sh (cf xs) j)
                 _ (fun i => Rpow x i +! Psum i xs) (S k)).
      + rewrite newton_cons, (IH (S k)), (IH k). ring.
      + intros j _. apply cf_cons.
      + intros i _ _. reflexivity.
  Qed.

  (* What is used: the top m coefficients determine p_1 .. p_(m-1), and  *)
  (* p_m moves with c_m at rate -m.                                      *)
  Theorem newton_difference : forall xs ys m,
    (forall j, j < m -> cf xs j = cf ys j) ->
    forall k, 1 <= k -> k <= m ->
      Psum k xs -! Psum k ys = ropp (Ofnat k *! (cf xs k -! cf ys k)).
  Proof.
    intros xs ys m Hc k. induction k as [k IHk] using lt_wf_ind.
    intros Hk1 Hkm. destruct k as [|k']; [lia|].
    pose proof (newton_identities xs (S k')) as Nx.
    pose proof (newton_identities ys (S k')) as Ny.
    unfold newton in Nx, Ny.
    rewrite (ks_skip _ k' 0) in Nx by lia.
    rewrite (ks_skip _ k' 0) in Ny by lia. cbv beta in Nx, Ny.
    rewrite cf_zero, Nat.sub_0_r in Nx, Ny.
    assert (ET : Ksumf (fun i => cf xs (skip 0 i)
                                 *! Psum (S k' - skip 0 i) xs) k'
                 = Ksumf (fun i => cf ys (skip 0 i)
                                   *! Psum (S k' - skip 0 i) ys) k').
    { apply ks_ext. intros i Hi.
      change (skip 0 i) with (S i). change (S k' - S i) with (k' - i).
      rewrite (Hc (S i)) by lia. f_equal.
      assert (Ed : Psum (k' - i) xs -! Psum (k' - i) ys = r0).
      { rewrite (IHk (k' - i)) by lia. rewrite (Hc (k' - i)) by lia. ring. }
      replace (Psum (k' - i) xs)
        with ((Psum (k' - i) xs -! Psum (k' - i) ys) +! Psum (k' - i) ys)
        by ring.
      rewrite Ed. ring. }
    rewrite ET in Nx.
    set (T := Ksumf (fun i => cf ys (skip 0 i)
                              *! Psum (S k' - skip 0 i) ys) k') in *.
    replace (Psum (S k') xs -! Psum (S k') ys)
      with ((r1 *! Psum (S k') xs +! T +! Ofnat (S k') *! cf xs (S k'))
            -! (r1 *! Psum (S k') ys +! T +! Ofnat (S k') *! cf ys (S k'))
            -! Ofnat (S k') *! (cf xs (S k') -! cf ys (S k')))
      by ring.
    rewrite Nx, Ny. ring.
  Qed.

  (* =================================================================== *)
  (* Part C.  A polynomial with more distinct roots than coefficients is *)
  (* zero -- BladeRankBound's many_roots over any integral domain.       *)
  (* =================================================================== *)

  Hypothesis R_integral : forall a b, rmul a b = r0 -> a = r0 \/ b = r0.

  Fixpoint kquot (al : R) (l : list R) : list R :=
    match l with
    | [] => []
    | _ :: l' => match l' with
                 | [] => []
                 | _ :: _ => Peval l' al :: kquot al l'
                 end
    end.

  Lemma kquot_length : forall al l, length (kquot al l) = length l - 1.
  Proof.
    intros al. induction l as [|c l IH]; [reflexivity|].
    destruct l as [|c' l']; [reflexivity|].
    change (kquot al (c :: c' :: l'))
      with (Peval (c' :: l') al :: kquot al (c' :: l')).
    simpl length in *. rewrite IH. lia.
  Qed.

  Lemma kquot_identity : forall al l x,
    Peval l x -! Peval l al = (x -! al) *! Peval (kquot al l) x.
  Proof.
    intros al. induction l as [|c l IH]; intros x; [simpl; ring|].
    destruct l as [|c' l']; [simpl; ring|].
    change (kquot al (c :: c' :: l'))
      with (Peval (c' :: l') al :: kquot al (c' :: l')).
    specialize (IH x). remember (c' :: l') as t eqn:Et. cbn [peval].
    assert (E : Peval t x = Peval t al +! (x -! al) *! Peval (kquot al t) x)
      by (rewrite <- IH; ring).
    rewrite E. ring.
  Qed.

  Lemma kquot_zero : forall al l,
    Forall (fun c => c = r0) (kquot al l) -> Peval l al = r0 ->
    Forall (fun c => c = r0) l.
  Proof.
    intros al. induction l as [|c l IH]; intros Hq He; [constructor|].
    destruct l as [|c' l'].
    - simpl in He. constructor; [|constructor].
      replace c with (c +! al *! r0) by ring. exact He.
    - change (kquot al (c :: c' :: l'))
        with (Peval (c' :: l') al :: kquot al (c' :: l')) in Hq.
      remember (c' :: l') as t eqn:Et. cbn [peval] in He.
      inversion Hq as [|w ws H0 Hq' Ew]. clear Ew.
      constructor; [|apply IH; assumption].
      rewrite H0 in He. replace c with (c +! al *! r0) by ring. exact He.
  Qed.

  Theorem many_roots_dom : forall rs : list R, NoDup rs ->
    forall l, length l <= length rs ->
      (forall a, In a rs -> Peval l a = r0) -> Forall (fun c => c = r0) l.
  Proof.
    induction rs as [|al rs IH]; intros Hnd l Hlen Hroots.
    - destruct l; [constructor|simpl in Hlen; lia].
    - inversion Hnd as [|? ? Hnin Hnd']; subst.
      apply (kquot_zero al); [|apply Hroots; left; reflexivity].
      apply (IH Hnd').
      + rewrite kquot_length. simpl in Hlen. lia.
      + intros a Ha. pose proof (kquot_identity al l a) as E.
        rewrite (Hroots a (or_intror Ha)), (Hroots al (or_introl eq_refl))
          in E.
        assert (E2 : (a -! al) *! Peval (kquot al l) a = r0)
          by (rewrite <- E; ring).
        apply R_integral in E2. destruct E2 as [E2|E2]; [|exact E2].
        exfalso. apply Hnin. replace al with a; [exact Ha|].
        replace a with ((a -! al) +! al) by ring. rewrite E2. ring.
  Qed.

  (* Two coefficient lists that agree as FUNCTIONS at enough distinct    *)
  (* points agree coefficient by coefficient.                            *)
  Theorem coefficients_from_values : forall (l1 l2 rs : list R),
    NoDup rs -> length l1 <= length rs -> length l2 <= length rs ->
    (forall a, In a rs -> Peval l1 a = Peval l2 a) ->
    forall j, nth j l1 r0 = nth j l2 r0.
  Proof.
    intros l1 l2 rs Hnd H1 H2 Hv j.
    set (d := Padd l1 (Pscale (ropp r1) l2)).
    assert (HF : Forall (fun c => c = r0) d).
    { apply (many_roots_dom rs Hnd).
      - unfold d. rewrite padd_length. unfold pscale. rewrite map_length.
        lia.
      - intros a Ha. unfold d. rewrite pe_padd, pe_pscale, (Hv a Ha). ring. }
    assert (Hj : nth j d r0 = r0).
    { destruct (le_lt_dec (length d) j) as [Hge|Hlt].
      - apply nth_overflow. exact Hge.
      - rewrite Forall_forall in HF. apply HF. apply nth_In. exact Hlt. }
    unfold d in Hj. rewrite n_padd, n_pscale in Hj.
    replace (nth j l1 r0)
      with ((nth j l1 r0 +! ropp r1 *! nth j l2 r0) +! nth j l2 r0) by ring.
    rewrite Hj. ring.
  Qed.

  (* =================================================================== *)
  (* Part D.  Bump one coefficient of E by t.  If the bumped polynomial  *)
  (* is again some E(ys), then (xs, ys) agree on p_0 .. p_(m-1) and      *)
  (* differ on p_m by m t: an IDEAL pair of their common extent.         *)
  (* =================================================================== *)

  Definition bump (m : nat) (t : R) (l : list R) : list R :=
    Padd l (repeat r0 m ++ [t]).

  Lemma peval_spike : forall m t Y,
    Peval (repeat r0 m ++ [t]) Y = t *! Rpow Y m.
  Proof.
    induction m as [|m IH]; intros t Y; simpl; [ring|]. rewrite IH. ring.
  Qed.

  Lemma nth_spike : forall m t j,
    nth j (repeat r0 m ++ [t]) r0 = if Nat.eqb j m then t else r0.
  Proof.
    induction m as [|m IH]; intros t j.
    - destruct j as [|[|j]]; reflexivity.
    - destruct j as [|j]; [reflexivity|]. simpl. apply IH.
  Qed.

  Lemma peval_bump : forall m t l Y,
    Peval (bump m t l) Y = Peval l Y +! t *! Rpow Y m.
  Proof. intros. unfold bump. rewrite pe_padd, peval_spike. reflexivity. Qed.

  Lemma nth_bump : forall m t l j,
    nth j (bump m t l) r0
    = nth j l r0 +! (if Nat.eqb j m then t else r0).
  Proof. intros. unfold bump. rewrite n_padd, nth_spike. reflexivity. Qed.

  Lemma bump_length : forall m t l, m < length l ->
    length (bump m t l) = length l.
  Proof.
    intros m t l Hm. unfold bump.
    rewrite padd_length, app_length, repeat_length. simpl. lia.
  Qed.

  Hypothesis R_char0 : forall n, Ofnat (S n) <> r0.

  Theorem ideal_pair_from_bump : forall xs ys m t,
    1 <= m -> length xs = length ys -> t <> r0 ->
    (forall j, nth j (elist ys) r0 = nth j (bump m t (elist xs)) r0) ->
    (forall k, k < m -> Psum k xs = Psum k ys)
    /\ Psum m xs <> Psum m ys.
  Proof.
    intros xs ys m t Hm Hlen Ht Hco.
    assert (Hcf : forall j, cf ys j
                            = cf xs j +! (if Nat.eqb j m then t else r0)).
    { intro j. unfold cf. rewrite Hco, nth_bump. reflexivity. }
    assert (Hlow : forall j, j < m -> cf xs j = cf ys j).
    { intros j Hj. rewrite Hcf. destruct (Nat.eqb_spec j m); [lia|]. ring. }
    split.
    - intros k Hk. destruct k as [|k].
      + rewrite !(psum_0 R r0 r1 radd rmul), Hlen. reflexivity.
      + pose proof (newton_difference xs ys m Hlow (S k) ltac:(lia) ltac:(lia))
          as Hd.
        rewrite (Hlow (S k) Hk) in Hd.
        replace (Psum (S k) xs)
          with ((Psum (S k) xs -! Psum (S k) ys) +! Psum (S k) ys) by ring.
        rewrite Hd. ring.
    - intro E.
      pose proof (newton_difference xs ys m Hlow m Hm (le_n m)) as Hd.
      rewrite (Hcf m), Nat.eqb_refl, E in Hd.
      assert (Hz : Ofnat m *! t = r0).
      { replace (Ofnat m *! t)
          with (ropp (Ofnat m *! (cf xs m -! (cf xs m +! t)))) by ring.
        rewrite <- Hd. ring. }
      apply R_integral in Hz. destruct Hz as [Hz|Hz]; [|contradiction].
      destruct m as [|m']; [lia|]. exact (R_char0 m' Hz).
  Qed.

End Newton.
