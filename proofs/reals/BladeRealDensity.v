(* ===================================================================== *)
(* reals/BladeRealDensity.v -- EXACT RECURRENCE REDUCTION, the last      *)
(* sentence of the draft's P8                                            *)
(* (docs/research/exact-recurrence-reduction-proofs.md, section 9):      *)
(* "Choose such a point with a_d(q_r(x)) <> 0.  It exists."              *)
(*                                                                       *)
(*       THIS FILE IS NOT PART OF THE AXIOM-FREE TOWER.                  *)
(*                                                                       *)
(* Coq's standard-library real numbers; the same three axioms as         *)
(* BladeRealCollision (sig_forall_dec, sig_not_dec,                      *)
(* functional_extensionality_dep -- the last is also used directly here, *)
(* for upd_same).  Own directory, own _CoqProject, not counted.          *)
(*                                                                       *)
(* BladeRealCollision refuses closure at any admissible array --         *)
(* distinct positive reals -- where the leading coefficient a_d is       *)
(* nonzero.  The draft says such an array exists whenever a_d is a       *)
(* POLYNOMIAL in the summary that is not identically zero, arguing by an *)
(* open image and the identity theorem.  Here:                           *)
(*                                                                       *)
(*   SP, SP_box                a function of an environment that is a    *)
(*                             polynomial in each variable SEPARATELY    *)
(*                             and nonzero somewhere is nonzero          *)
(*                             somewhere with its first r coordinates in *)
(*                             any box.  One coordinate at a time, by    *)
(*                             root counting in one variable             *)
(*                             (univariate_nonroot) -- no multivariate   *)
(*                             polynomial normal form;                   *)
(*   ptab, phi, phi_psum       THE NEWTON MAP: the power sums of an      *)
(*                             array are phi of the coefficients of      *)
(*                             prod (1 - x_i Y) -- BladeNewton's         *)
(*                             identities read as a recursion;           *)
(*   SP_phi, phi_onto          it is separately polynomial, and ONTO:    *)
(*                             every (z_1 .. z_r) is phi of some         *)
(*                             coefficients, solving for c_k one index   *)
(*                             at a time (where division by k enters);   *)
(*   perturbed_roots_multi,    bump the coefficients of Y^1 .. Y^r all   *)
(*                             at                                        *)
(*   chamber_family            once, each by at most eps: the real roots *)
(*                             persist, so the coefficient vectors of    *)
(*                             admissible arrays FILL A BOX around the   *)
(*                             base ones;                                *)
(*   nonzero_at_admissible_array  hence a polynomial in the summary that *)
(*                             is nonzero somewhere is nonzero at the    *)
(*                             summary of an admissible array of any     *)
(*                             extent N >= r;                            *)
(*   closure_refused_R_polynomial  P8, NEGATIVE HALF, AS THE DRAFT       *)
(*                             STATES IT: leading coefficient a          *)
(*                             polynomial in the summary, not            *)
(*                             identically zero; N >= d r; no function G *)
(*                             of the first r power sums, continuous or  *)
(*                             not.  The lower coefficients a_j stay     *)
(*                             arbitrary functions.                      *)
(*                                                                       *)
(* Scope, stated once.  "Not identically zero" is semantic: the          *)
(* polynomial is nonzero at some r-vector of reals.  The polynomial is a *)
(* pexp read on its first r variables.  Real arrays, exact arithmetic.   *)
(* The draft's threshold N >= d r remains sufficient, not sharp (section *)
(* 15.1).                                                                *)
(*                                                                       *)
(* Imports the Blade tower, BladeSmoothRank, BladeRealCollision, Coq's   *)
(* Reals and FunctionalExtensionality.                                   *)
(* ===================================================================== *)

From Blade Require Import BladeBinomial BladeSummary BladeMomentClosure
  BladeRankBound BladeDescartes BladeRankDomain BladeProuhet BladeNewton.
From BladeReals Require Import BladeSmoothRank BladeRealCollision.
Require Import Reals Lra Psatz List Arith Lia FunctionalExtensionality.
Import ListNotations.

Open Scope R_scope.

Local Notation Rpeval := (peval R 0 Rplus Rmult).
Local Notation Relist := (elist R 0 1 Rplus Rmult Ropp).
Local Notation Rpsum := (psum R 0 1 Rplus Rmult).
Local Notation Rpadd := (padd R Rplus).
Local Notation Rpmul := (pmul R 0 Rplus Rmult).
Local Notation Rcf := (cf R 0 1 Rplus Rmult Ropp).
Local Notation Rpev := (pev R Rplus Rmult).

(* ===================================================================== *)
(* Part A.  Functions of an environment that are polynomial in each      *)
(* variable separately, and the identity theorem on a box, one           *)
(* coordinate at a time.                                                 *)
(* ===================================================================== *)

Definition upd (rho : nat -> R) (v : nat) (s : R) : nat -> R :=
  fun i => if Nat.eqb i v then s else rho i.

Lemma upd_same : forall rho v, upd rho v (rho v) = rho.
Proof.
  intros rho v. apply functional_extensionality. intro i. unfold upd.
  destruct (Nat.eqb_spec i v) as [->|]; reflexivity.
Qed.

Definition SP (g : (nat -> R) -> R) : Prop :=
  forall v rho, exists l : list R, forall s, g (upd rho v s) = Rpeval l s.

Lemma SP_const : forall c, SP (fun _ => c).
Proof. intros c v rho. exists [c]. intro s. simpl. ring. Qed.

Lemma SP_proj : forall i, SP (fun rho => rho i).
Proof.
  intros i v rho. destruct (Nat.eqb_spec i v) as [->|Hne].
  - exists [0; 1]. intro s. unfold upd. rewrite Nat.eqb_refl. simpl. ring.
  - exists [rho i]. intro s. unfold upd.
    destruct (Nat.eqb_spec i v); [contradiction|]. simpl. ring.
Qed.

Lemma SP_add : forall g h, SP g -> SP h -> SP (fun rho => g rho + h rho).
Proof.
  intros g h Hg Hh v rho. destruct (Hg v rho) as [l1 H1].
  destruct (Hh v rho) as [l2 H2]. exists (Rpadd l1 l2). intro s.
  rewrite (peval_padd R 0 1 Rplus Rmult Rminus Ropp RTheory), H1, H2.
  reflexivity.
Qed.

Lemma SP_mul : forall g h, SP g -> SP h -> SP (fun rho => g rho * h rho).
Proof.
  intros g h Hg Hh v rho. destruct (Hg v rho) as [l1 H1].
  destruct (Hh v rho) as [l2 H2]. exists (Rpmul l1 l2). intro s.
  rewrite (peval_pmul R 0 1 Rplus Rmult Rminus Ropp RTheory), H1, H2.
  reflexivity.
Qed.

Lemma SP_sum : forall (g : nat -> (nat -> R) -> R) n,
  (forall j, (j < n)%nat -> SP (g j)) ->
  SP (fun rho => Rsumf (fun j => g j rho) n).
Proof.
  intros g. induction n as [|n IH]; intros H.
  - exact (SP_const 0).
  - change (SP (fun rho => Rsumf (fun j => g j rho) n + g n rho)).
    apply SP_add; [apply IH; intros; apply H; lia|]. apply H. lia.
Qed.

Lemma SP_pev : forall (A : pexp R) (h : nat -> (nat -> R) -> R),
  (forall i, SP (h i)) -> SP (fun rho => Rpev A (fun i => h i rho)).
Proof.
  intros A h Hh.
  induction A as [i|c|A1 IH1 A2 IH2|A1 IH1 A2 IH2]; cbn [pev].
  - apply Hh.
  - apply SP_const.
  - apply SP_add; assumption.
  - apply SP_mul; assumption.
Qed.

Lemma peval_all_zero : forall l s,
  Forall (fun c => c = 0) l -> Rpeval l s = 0.
Proof.
  induction l as [|c l IH]; intros s H; [reflexivity|].
  inversion H; subst. cbn [peval]. rewrite IH by assumption. ring.
Qed.

Lemma list_root_dec : forall (f : R -> R) (rs : list R),
  (exists a, In a rs /\ f a <> 0) \/ (forall a, In a rs -> f a = 0).
Proof.
  intros f. induction rs as [|a rs [IH|IH]].
  - right. intros a [].
  - left. destruct IH as [b [Hb Hf]]. exists b. split; [right|]; assumption.
  - destruct (Req_EM_T (f a) 0) as [E|E].
    + right. intros b [<-|Hb]; [exact E|apply IH; exact Hb].
    + left. exists a. split; [left; reflexivity|exact E].
Qed.

(* A nonzero polynomial in one variable has a non-root in any interval.  *)
Lemma univariate_nonroot : forall (l : list R) lo hi, lo < hi ->
  (exists s, Rpeval l s <> 0) ->
  exists s, lo <= s <= hi /\ Rpeval l s <> 0.
Proof.
  intros l lo hi Hlt [s0 Hs0].
  set (n := length l).
  assert (Hn : 0 < INR (S n)) by (apply lt_0_INR; lia).
  set (dl := (hi - lo) / INR (S n)).
  assert (Hdl : 0 < dl) by (unfold dl; apply Rdiv_lt_0_compat; lra).
  assert (Hdle : dl * INR (S n) = hi - lo) by (unfold dl; field; lra).
  set (pt := fun i => lo + INR i * dl).
  destruct (list_root_dec (fun s => Rpeval l s) (lstN pt (S n)))
    as [[a [Ha Hf]]|Hall].
  - destruct (lstN_In pt (S n) a Ha) as [i [Hi ->]].
    exists (pt i). split; [|exact Hf]. unfold pt.
    pose proof (pos_INR i). pose proof (le_INR i n ltac:(lia)) as Hin.
    rewrite S_INR in Hdle. split; nra.
  - exfalso. apply Hs0. apply peval_all_zero.
    apply (many_roots_dom R 0 1 Rplus Rmult Rminus Ropp RTheory
             Rmult_integral (lstN pt (S n))).
    + apply NoDup_lstN. intros i j Hij _. unfold pt.
      pose proof (lt_INR i j Hij). nra.
    + rewrite lstN_length. unfold n. lia.
    + exact Hall.
Qed.

(* THE IDENTITY THEOREM ON A BOX.  If g is separately polynomial and     *)
(* nonzero somewhere, it is nonzero somewhere with its first r           *)
(* coordinates in any prescribed box.                                    *)
Theorem SP_box : forall g, SP g ->
  forall r (lo hi : nat -> R), (forall k, lo k < hi k) ->
  forall z, g z <> 0 ->
  exists z', (forall k, (k < r)%nat -> lo k <= z' k <= hi k) /\
             (forall k, (r <= k)%nat -> z' k = z k) /\ g z' <> 0.
Proof.
  intros g Hg. induction r as [|r IH]; intros lo hi Hbox z Hz.
  - exists z. split; [intros k Hk; lia|]. split; [reflexivity|exact Hz].
  - destruct (IH lo hi Hbox z Hz) as [z1 [Hin [Hout Hz1]]].
    destruct (Hg r z1) as [l Hl].
    destruct (univariate_nonroot l (lo r) (hi r) (Hbox r)) as [s [Hs Hne]].
    { exists (z1 r). rewrite <- Hl, upd_same. exact Hz1. }
    exists (upd z1 r s). split; [|split].
    + intros k Hk. unfold upd. destruct (Nat.eqb_spec k r) as [->|Hne'].
      * exact Hs.
      * apply Hin. lia.
    + intros k Hk. unfold upd. destruct (Nat.eqb_spec k r); [lia|].
      apply Hout. lia.
    + rewrite Hl. exact Hne.
Qed.

Lemma SP_ext : forall g h, (forall rho, g rho = h rho) -> SP g -> SP h.
Proof.
  intros g h Heq Hg v rho. destruct (Hg v rho) as [l Hl]. exists l.
  intro s. rewrite <- Heq. apply Hl.
Qed.

Lemma SP_opp : forall g, SP g -> SP (fun rho => - g rho).
Proof.
  intros g Hg. apply (SP_ext (fun rho => (-1) * g rho)).
  - intro rho. ring.
  - apply SP_mul; [apply SP_const|exact Hg].
Qed.

(* ===================================================================== *)
(* Part B.  The Newton map: power sums as functions of the coefficients  *)
(* of prod (1 - x_i Y).  Triangular, separately polynomial, and ONTO.    *)
(* ===================================================================== *)

(* [p_n; ..; p_1], newest first, by Newton's recursion.                  *)
Fixpoint ptab (c : nat -> R) (n : nat) : list R :=
  match n with
  | O => []
  | S n' => (- (Rsumf (fun j => c (S j) * nth j (ptab c n') 0) n'
               + INR (S n') * c (S n'))) :: ptab c n'
  end.

Definition phi (c : nat -> R) (k : nat) : R := nth 0 (ptab c k) 0.

Lemma ptab_nth : forall c n i, (1 <= i)%nat -> (i <= n)%nat ->
  nth (n - i) (ptab c n) 0 = phi c i.
Proof.
  intros c. induction n as [|n IH]; intros i H1 Hn; [lia|].
  destruct (Nat.eq_dec i (S n)) as [->|Hne].
  - rewrite Nat.sub_diag. reflexivity.
  - replace (S n - i)%nat with (S (n - i)) by lia. cbn [ptab nth].
    apply IH; lia.
Qed.

Lemma ptab_ext : forall c c' n,
  (forall j, (1 <= j)%nat -> (j <= n)%nat -> c j = c' j) ->
  ptab c n = ptab c' n.
Proof.
  intros c c'. induction n as [|n IH]; intros H; [reflexivity|].
  cbn [ptab]. rewrite IH by (intros; apply H; lia).
  rewrite (H (S n)) by lia. f_equal. f_equal. f_equal.
  apply Rsumf_ext. intros j Hj. rewrite (H (S j)) by lia. reflexivity.
Qed.

Lemma phi_ext : forall c c' k,
  (forall j, (1 <= j)%nat -> (j <= k)%nat -> c j = c' j) ->
  phi c k = phi c' k.
Proof.
  intros c c' k H. unfold phi. rewrite (ptab_ext c c' k H). reflexivity.
Qed.

Lemma phi_S : forall c n,
  phi c (S n) = - (Rsumf (fun j => c (S j) * phi c (n - j)) n
                   + INR (S n) * c (S n)).
Proof.
  intros c n. unfold phi at 1. cbn [ptab nth]. f_equal. f_equal.
  apply Rsumf_ext. intros j Hj.
  rewrite <- (ptab_nth c n (n - j)) by lia.
  replace (n - (n - j))%nat with j by lia. reflexivity.
Qed.

(* Newton's identities say exactly that the power sums of an array are   *)
(* phi of its coefficients.                                              *)
Theorem phi_psum : forall xs k, (1 <= k)%nat -> phi (Rcf xs) k = Rpsum k xs.
Proof.
  intros xs k. induction k as [k IH] using lt_wf_ind. intros Hk.
  destruct k as [|n]; [lia|]. rewrite phi_S.
  pose proof (newton_identities R 0 1 Rplus Rmult Rminus Ropp RTheory
                xs (S n)) as Nw.
  unfold newton in Nw.
  rewrite (ksumf_skip R 0 1 Rplus Rmult Rminus Ropp RTheory _ n 0) in Nw
    by lia.
  cbv beta in Nw.
  rewrite (cf_zero R 0 1 Rplus Rmult Rminus Ropp RTheory), Nat.sub_0_r in Nw.
  rewrite ofnat_R in Nw.
  rewrite (Rsumf_ext (fun j => Rcf xs (S j) * phi (Rcf xs) (n - j))
             (fun i => Rcf xs (skip 0 i) * Rpsum (S n - skip 0 i) xs) n).
  - lra.
  - intros j Hj. change (skip 0 j) with (S j).
    change (S n - S j)%nat with (n - j)%nat.
    rewrite (IH (n - j)%nat) by lia. reflexivity.
Qed.

Lemma SP_ptab : forall n i, SP (fun c => nth i (ptab c n) 0).
Proof.
  induction n as [|n IH]; intros i.
  - destruct i; exact (SP_const 0).
  - destruct i as [|i]; [|exact (IH i)].
    change (SP (fun c => - (Rsumf (fun j => c (S j) * nth j (ptab c n) 0) n
                            + INR (S n) * c (S n)))).
    apply SP_opp. apply SP_add.
    + apply SP_sum. intros j _. apply SP_mul; [apply SP_proj|apply IH].
    + apply SP_mul; [apply SP_const|apply SP_proj].
Qed.

Lemma SP_phi : forall k, SP (fun c => phi c k).
Proof. intros k. exact (SP_ptab k 0). Qed.

(* Every target (z_1 .. z_r) is phi of some coefficients: solve for c_k  *)
(* one index at a time -- this is where division by k enters.            *)
Theorem phi_onto : forall r (z : nat -> R), exists c : nat -> R,
  forall k, (1 <= k)%nat -> (k <= r)%nat -> phi c k = z k.
Proof.
  induction r as [|r IH]; intros z.
  - exists (fun _ => 0). intros k H1 H2. lia.
  - destruct (IH z) as [c' Hc'].
    set (Sg := Rsumf (fun j => c' (S j) * phi c' (r - j)) r).
    set (s := - (z (S r) + Sg) / INR (S r)).
    exists (upd c' (S r) s). intros k H1 Hk.
    assert (Hsame : forall j, (1 <= j)%nat -> (j <= r)%nat ->
                      upd c' (S r) s j = c' j).
    { intros j _ Hj. unfold upd. destruct (Nat.eqb_spec j (S r)); [lia|].
      reflexivity. }
    destruct (Nat.eq_dec k (S r)) as [->|Hne].
    + rewrite phi_S.
      rewrite (Rsumf_ext _ (fun j => c' (S j) * phi c' (r - j)) r).
      * fold Sg. unfold upd at 1. rewrite Nat.eqb_refl. unfold s.
        assert (Hn : INR (S r) <> 0) by (apply not_0_INR; discriminate).
        field. exact Hn.
      * intros j Hj. rewrite (Hsame (S j)) by lia. f_equal.
        apply phi_ext. intros i Hi1 Hi2. apply Hsame; lia.
    + rewrite (phi_ext _ c' k) by (intros j Hj1 Hj2; apply Hsame; lia).
      apply Hc'; lia.
Qed.

(* ===================================================================== *)
(* Part C.  Bump the coefficients of Y^1 .. Y^r ALL AT ONCE, by amounts  *)
(* bounded by one small eps: the real roots persist.  So the coefficient *)
(* vectors (c_1 .. c_r) of admissible arrays fill a box.                 *)
(* ===================================================================== *)

Definition bl (ts : nat -> R) (r : nat) : list R := 0 :: map ts (seq 1 r).

Lemma nth_bl : forall ts r k, (1 <= k)%nat -> (k <= r)%nat ->
  nth k (bl ts r) 0 = ts k.
Proof.
  intros ts r k H1 Hr. destruct k as [|k]; [lia|]. unfold bl. cbn [nth].
  rewrite (nth_map_lt R nat ts (seq 1 r) k 0 0%nat)
    by (rewrite seq_length; lia).
  rewrite seq_nth by lia. reflexivity.
Qed.

Lemma bl_length : forall ts r, length (bl ts r) = S r.
Proof.
  intros. unfold bl. cbn [length]. rewrite map_length, seq_length.
  reflexivity.
Qed.

Lemma bl_small : forall ts r eps, 0 <= eps ->
  (forall k, Rabs (ts k) <= eps) ->
  Forall (fun c => Rabs c <= eps) (bl ts r).
Proof.
  intros ts r eps He H. unfold bl. constructor; [rewrite Rabs_R0; exact He|].
  apply Forall_forall. intros c Hc. apply in_map_iff in Hc.
  destruct Hc as [k [<- _]]. apply H.
Qed.

Definition ones (l : list R) : list R := map (fun _ => 1) l.

Lemma ones_bl : forall ts ts' r, ones (bl ts r) = ones (bl ts' r).
Proof.
  intros ts ts' r. unfold ones, bl. cbn [map]. rewrite !map_map. reflexivity.
Qed.

Lemma peval_abs_bound : forall l eps t, 0 <= eps ->
  Forall (fun c => Rabs c <= eps) l ->
  Rabs (Rpeval l t) <= eps * Rpeval (ones l) (Rabs t).
Proof.
  induction l as [|c l IH]; intros eps t He HF.
  - simpl. rewrite Rabs_R0. lra.
  - inversion HF as [|? ? Hc HF']; subst. cbn [peval ones map].
    pose proof (Rabs_triang c (t * Rpeval l t)) as Ht.
    rewrite Rabs_mult in Ht.
    pose proof (IH eps t He HF') as Hi. fold (ones l).
    assert (Rabs t * Rabs (Rpeval l t)
            <= Rabs t * (eps * Rpeval (ones l) (Rabs t)))
      by (apply Rmult_le_compat_l; [apply Rabs_pos|exact Hi]).
    lra.
Qed.

Lemma perturbed_roots_multi : forall (rt : nat -> R),
  (forall i, 0 < rt i) -> (forall i, rt i < rt (S i)) ->
  forall N r, (1 <= N)%nat ->
  exists eps, 0 < eps /\
    forall ts : nat -> R, (forall k, Rabs (ts k) <= eps) ->
    exists rho : nat -> R,
      (forall j, (j < N)%nat -> tau rt j < rho j < tau rt (S j)) /\
      (forall j, (j < N)%nat ->
         Rpeval (Rpadd (Relist (lstN (xN rt) N)) (bl ts r)) (rho j) = 0).
Proof.
  intros rt Hpos Hincr N r HN.
  destruct (finite_lower (S N) (fun j => Rabs (EN rt N (tau rt j))))
    as [beta [Hbeta Hlow]].
  { intros j Hj. apply Rabs_pos_lt.
    apply (EN_tau_nonzero rt Hpos Hincr); lia. }
  destruct (finite_upper (S N)
              (fun j => Rpeval (ones (bl (fun _ => 0) r)) (Rabs (tau rt j))))
    as [M [HM Hup]].
  set (eps := beta / (2 * (M + 1))).
  assert (Heps : 0 < eps) by (unfold eps; apply Rdiv_lt_0_compat; lra).
  assert (Hepse : eps * (M + 1) = beta / 2) by (unfold eps; field; lra).
  exists eps. split; [exact Heps|]. intros ts Hts.
  set (Et := fun Y => Rpeval (Rpadd (Relist (lstN (xN rt) N)) (bl ts r)) Y).
  assert (HEt : forall Y, Et Y = EN rt N Y + Rpeval (bl ts r) Y).
  { intro Y. unfold Et, EN.
    apply (peval_padd R 0 1 Rplus Rmult Rminus Ropp RTheory). }
  assert (Hpert : forall j, (j <= N)%nat ->
            Rabs (Rpeval (bl ts r) (tau rt j)) <= beta / 2).
  { intros j Hj.
    pose proof (peval_abs_bound (bl ts r) eps (tau rt j) ltac:(lra)
                  (bl_small ts r eps ltac:(lra) Hts)) as Hb.
    rewrite (ones_bl ts (fun _ => 0) r) in Hb.
    pose proof (Hup j ltac:(lia)) as H1. cbv beta in H1.
    assert (eps * Rpeval (ones (bl (fun _ => 0) r)) (Rabs (tau rt j))
            <= eps * M) by (apply Rmult_le_compat_l; lra).
    lra. }
  assert (Hsign : forall j, (j < N)%nat ->
            Et (tau rt j) * Et (tau rt (S j)) < 0).
  { intros j Hj. rewrite !HEt. apply (sign_stable _ _ _ _ beta Hbeta).
    - apply (Hlow j). lia.
    - apply (Hlow (S j)). lia.
    - apply Hpert. lia.
    - apply Hpert. lia.
    - apply (EN_sign_change rt Hpos Hincr). exact Hj. }
  destruct (finite_choice N
              (fun j z => tau rt j < z < tau rt (S j) /\ Et z = 0))
    as [rho Hrho].
  { intros j Hj. pose proof (Hsign j Hj) as Hs.
    destruct (IVT_cor Et (tau rt j) (tau rt (S j))) as [z [[Hz1 Hz2] Hz0]].
    - unfold Et. apply peval_continuous.
    - apply (tau_le rt Hpos Hincr). lia.
    - lra.
    - exists z. split; [|exact Hz0]. split.
      + destruct Hz1 as [Hlt|Heq]; [exact Hlt|]. exfalso.
        rewrite Heq, Hz0 in Hs. lra.
      + destruct Hz2 as [Hlt|Heq]; [exact Hlt|]. exfalso.
        rewrite <- Heq, Hz0 in Hs. lra. }
  exists rho.
  split; intros j Hj; destruct (Hrho j Hj) as [H1 H2]; assumption.
Qed.

Lemma lstN_ext : forall f g N, (forall j, (j < N)%nat -> f j = g j) ->
  lstN f N = lstN g N.
Proof.
  intros f g. induction N as [|N IH]; intros H; [reflexivity|]. simpl.
  rewrite (H N) by lia. rewrite IH by (intros; apply H; lia). reflexivity.
Qed.

(* The arrays whose coefficients c_1 .. c_r sit within eps of the base   *)
(* ones are all admissible: distinct positive reals, with their own      *)
(* increasing root sequence.                                             *)
Theorem chamber_family : forall (rt : nat -> R),
  (forall i, 0 < rt i) -> (forall i, rt i < rt (S i)) ->
  forall N r, (1 <= N)%nat -> (r <= N)%nat ->
  exists eps, 0 < eps /\
    forall ts : nat -> R, (forall k, Rabs (ts k) <= eps) ->
    exists rt' : nat -> R,
      (forall i, 0 < rt' i) /\ (forall i, rt' i < rt' (S i)) /\
      forall k, (1 <= k)%nat -> (k <= r)%nat ->
        Rcf (lstN (xN rt') N) k = Rcf (lstN (xN rt) N) k + ts k.
Proof.
  intros rt Hpos Hincr N r HN HrN.
  destruct (perturbed_roots_multi rt Hpos Hincr N r HN) as [eps [Heps Hfam]].
  exists eps. split; [exact Heps|]. intros ts Hts.
  destruct (Hfam ts Hts) as [rho [Hint Hroot]].
  assert (Hrpos : forall j, (j < N)%nat -> 0 < rho j).
  { intros j Hj. destruct (Hint j Hj) as [H1 _].
    pose proof (tau_pos rt Hpos Hincr j). lra. }
  assert (Hrinc : forall i j, (i < j)%nat -> (j < N)%nat -> rho i < rho j).
  { intros i j Hij Hj. destruct (Hint i ltac:(lia)) as [_ Hi2].
    destruct (Hint j Hj) as [Hj1 _].
    pose proof (tau_le rt Hpos Hincr (S i) j ltac:(lia)). lra. }
  set (rt' := fun j => if Nat.ltb j N then rho j
                       else rho (N - 1)%nat + INR (S (j - N))).
  exists rt'. split; [|split].
  - intros i. unfold rt'. destruct (Nat.ltb_spec i N); [apply Hrpos; lia|].
    pose proof (Hrpos (N - 1)%nat ltac:(lia)).
    pose proof (pos_INR (S (i - N))).
    lra.
  - intros i. unfold rt'.
    destruct (Nat.ltb_spec i N), (Nat.ltb_spec (S i) N); try lia.
    + apply Hrinc; lia.
    + replace i with (N - 1)%nat by lia.
      pose proof (lt_0_INR (S (S (N - 1) - N)) ltac:(lia)). lra.
    + pose proof (lt_INR (S (i - N)) (S (S i - N)) ltac:(lia)). lra.
  - intros k Hk1 Hkr.
    assert (Hys : lstN (xN rt') N = lstN (fun j => / rho j) N).
    { apply lstN_ext. intros j Hj. unfold xN, rt'.
      destruct (Nat.ltb_spec j N); [reflexivity|lia]. }
    rewrite Hys. unfold cf.
    rewrite (coefficients_from_values R 0 1 Rplus Rmult Rminus Ropp RTheory
               Rmult_integral (Relist (lstN (fun j => / rho j) N))
               (Rpadd (Relist (lstN (xN rt) N)) (bl ts r))
               (0 :: lstN rho N)).
    + rewrite (nth_padd R 0 1 Rplus Rmult Rminus Ropp RTheory).
      rewrite (nth_bl ts r k Hk1 Hkr). reflexivity.
    + constructor.
      * intro Hin. destruct (lstN_In rho N 0 Hin) as [j [Hj E]].
        pose proof (Hrpos j Hj). lra.
      * apply NoDup_lstN. exact Hrinc.
    + rewrite (elist_length R 0 1 Rplus Rmult Ropp). cbn [length].
      rewrite !lstN_length. lia.
    + rewrite padd_length, (elist_length R 0 1 Rplus Rmult Ropp), bl_length.
      cbn [length]. rewrite !lstN_length. lia.
    + intros a [<-|Ha].
      * rewrite (peval_elist_zero R 0 1 Rplus Rmult Rminus Ropp RTheory).
        rewrite (peval_padd R 0 1 Rplus Rmult Rminus Ropp RTheory).
        rewrite (peval_elist_zero R 0 1 Rplus Rmult Rminus Ropp RTheory).
        unfold bl. cbn [peval]. ring.
      * destruct (lstN_In rho N a Ha) as [j [Hj ->]].
        rewrite (Hroot j Hj).
        apply (peval_elist_root R 0 1 Rplus Rmult Rminus Ropp RTheory
                 (lstN (fun j0 => / rho j0) N) (/ rho j) (rho j)).
        -- apply (In_lstN (fun j0 => / rho j0) N j Hj).
        -- apply Rinv_l. pose proof (Hrpos j Hj). lra.
Qed.

(* ===================================================================== *)
(* Part D.  THE DRAFT'S P8, NEGATIVE HALF, AS STATED.  The leading       *)
(* coefficient is a POLYNOMIAL in the summary that is not identically    *)
(* zero.  Then it is nonzero at some admissible array, and section 9     *)
(* goes through.                                                         *)
(* ===================================================================== *)

Lemma nth_qr : forall r (xs : list R) i,
  nth i (qr R 0 1 Rplus Rmult r xs) 0
  = if Nat.ltb i r then Rpsum (S i) xs else 0.
Proof.
  intros r xs i. unfold qr. destruct (Nat.ltb_spec i r) as [Hlt|Hge].
  - rewrite (nth_map_lt R nat _ (seq 1 r) i 0 0%nat)
      by (rewrite seq_length; exact Hlt).
    rewrite seq_nth by exact Hlt. reflexivity.
  - apply nth_overflow. rewrite map_length, seq_length. exact Hge.
Qed.

(* The existence sentence of section 9: a polynomial in the summary that *)
(* is nonzero SOMEWHERE is nonzero at the summary of an admissible array *)
(* -- distinct positive reals -- of any extent N >= r.                   *)
Theorem nonzero_at_admissible_array : forall (A : pexp R) r N,
  (1 <= N)%nat -> (r <= N)%nat ->
  (exists z : list R, length z = r /\ Rpev A (fun i => nth i z 0) <> 0) ->
  exists rt' : nat -> R,
    (forall i, 0 < rt' i) /\ (forall i, rt' i < rt' (S i)) /\
    Rpev A (fun i => nth i (qr R 0 1 Rplus Rmult r (lstN (xN rt') N)) 0)
    <> 0.
Proof.
  intros A r N HN HrN [zl [Hzl Hne]].
  destruct harmonic_roots as [Hpos Hincr].
  set (rt := fun i => INR (S i)) in *.
  set (c0 := Rcf (lstN (xN rt) N)).
  destruct (chamber_family rt Hpos Hincr N r HN HrN) as [eps [Heps Hfam]].
  set (Gf := fun c => Rpev A (fun i => if Nat.ltb i r then phi c (S i)
                                       else 0)).
  assert (HSP : SP Gf).
  { unfold Gf. apply SP_pev. intros i. destruct (Nat.ltb i r).
    - exact (SP_phi (S i)).
    - exact (SP_const 0). }
  destruct (phi_onto r (fun k => nth (k - 1) zl 0)) as [cs Hcs].
  assert (Hstart : Gf cs <> 0).
  { assert (Eq1 : Rpev A (fun i => nth i zl 0) = Gf cs).
    { unfold Gf. apply (pev_ext R Rplus Rmult). intros i.
      destruct (Nat.ltb_spec i r) as [Hlt|Hge].
      - rewrite (Hcs (S i)) by lia. simpl. rewrite Nat.sub_0_r.
        reflexivity.
      - apply nth_overflow. lia. }
    rewrite <- Eq1. exact Hne. }
  destruct (SP_box Gf HSP (S r) (fun k => c0 k - eps) (fun k => c0 k + eps)
              ltac:(intro k; lra) cs Hstart) as [c' [Hin [_ Hc']]].
  set (ts := fun k => if Nat.leb k r then c' k - c0 k else 0).
  assert (Hts : forall k, Rabs (ts k) <= eps).
  { intros k. unfold ts. destruct (Nat.leb_spec k r) as [Hle|Hgt].
    - apply Rabs_le_iff. destruct (Hin k ltac:(lia)). lra.
    - rewrite Rabs_R0. lra. }
  destruct (Hfam ts Hts) as [rt' [Hpos' [Hincr' Hcf]]].
  exists rt'. split; [exact Hpos'|]. split; [exact Hincr'|].
  assert (Eq2 : Rpev A (fun i => nth i (qr R 0 1 Rplus Rmult r
                                          (lstN (xN rt') N)) 0)
                = Gf c').
  { unfold Gf. apply (pev_ext R Rplus Rmult). intros i. rewrite nth_qr.
    destruct (Nat.ltb_spec i r) as [Hlt|Hge]; [|reflexivity].
    rewrite <- (phi_psum (lstN (xN rt') N) (S i)) by lia.
    apply phi_ext. intros j Hj1 Hj2.
    rewrite (Hcf j Hj1 ltac:(lia)).
    unfold ts. destruct (Nat.leb_spec j r); [|lia]. unfold c0. ring. }
  rewrite Eq2. exact Hc'.
Qed.

(* P8, negative half, for the general fragment with a POLYNOMIAL leading *)
(* coefficient that is not identically zero: no function G of the first  *)
(* r power sums, continuous or not, closes the update, for N >= d r.     *)
(* The lower coefficients a_j remain ARBITRARY functions of the summary. *)
Theorem closure_refused_R_polynomial : forall d r N
    (a : nat -> list R -> R) (A : pexp R),
  (2 <= d)%nat -> (1 <= r)%nat -> (d * r <= N)%nat ->
  (forall z, length z = r -> a d z = Rpev A (fun i => nth i z 0)) ->
  (exists z : list R, length z = r /\ Rpev A (fun i => nth i z 0) <> 0) ->
  forall G : unit -> list R -> list R,
    ~ (forall u y, length y = N ->
         qr R 0 1 Rplus Rmult r (Fgen R 0 1 Rplus Rmult d r a y)
         = G u (qr R 0 1 Rplus Rmult r y)).
Proof.
  intros d r N a A Hd Hr HN Ha Hnz.
  destruct (nonzero_at_admissible_array A r N ltac:(nia) ltac:(nia) Hnz)
    as [rt' [Hpos' [Hincr' Hne]]].
  apply (closure_refused_R_at_point rt' d r N a Hpos' Hincr' Hd Hr HN).
  rewrite Ha; [exact Hne|].
  unfold qr. rewrite map_length, seq_length. reflexivity.
Qed.
