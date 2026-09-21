(* ===================================================================== *)
(* BladeReduceCompiler.v -- EXACT RECURRENCE REDUCTION, the research     *)
(* target of section 12 of the draft                                     *)
(* (docs/research/exact-recurrence-reduction-proofs.md): a source        *)
(* fragment, a classifier, a generated reduced program, and the theorem  *)
(* that they agree.                                                      *)
(*                                                                       *)
(* The fragment is P8's: one array of particles, every particle updated  *)
(* by x' = sum_j a_j x^j, the a_j integer-coefficient polynomial         *)
(* expressions in the moments p_1 .. p_r of the current array and in     *)
(* run-constant parameters; the observation such an expression too.  The *)
(* extent N is not part of the source program.                           *)
(*                                                                       *)
(*   phi, zmap, zev            integer syntax read in any commutative    *)
(*                             ring;                                     *)
(*   uni_rep, grid_complete    over an integral domain of characteristic *)
(*                             zero, an expression vanishing on the grid *)
(*                             {0..D}^n, D its degree, vanishes          *)
(*                             everywhere -- one coordinate at a time,   *)
(*                             by root counting                          *)
(*                             (BladeNewton.many_roots_dom);             *)
(*   nonroot, nonroot_some_K,  THE ZERO TEST: a computable grid search,  *)
(*   nonroot_none              sound and COMPLETE;                       *)
(*   prog, wf, strip           the fragment, and the coefficient list    *)
(*                             with its identically-zero top stripped;   *)
(*   classify, classify_total, three outcomes as the draft asks; on this *)
(*   classify_refused          fragment Unknown never occurs, and a      *)
(*                             refusal carries the effective degree, the *)
(*                             leading coefficient and an integer point  *)
(*                             where it is nonzero;                      *)
(*   Gk, rprog_of, compile     the GENERATED reduced program: r          *)
(*                             expressions over the summary, the         *)
(*                             parameters and one variable for the       *)
(*                             extent -- built once, uniformly in N;     *)
(*   compile_certificate,      THE COMPILER THEOREM: for every extent    *)
(*                             and                                       *)
(*   compile_sound_gen,        parameter vector the generated program is *)
(*   compile_sound             a reduction certificate, hence every      *)
(*                             prefix observation of every finite        *)
(*                             execution agrees; unconditional over      *)
(*                             integral domains of characteristic zero;  *)
(*   compile_sound_unstripped  over ANY commutative ring when the zero   *)
(*                             test stripped nothing -- wrapping machine *)
(*                             integers, dual numbers;                   *)
(*   sstep_truncated           a refused program IS the degree-d update  *)
(*                             the negative theorems speak about;        *)
(*   compiler_runs,            the classifier and the generated program, *)
(*   compiled_affine_runs      run by the kernel on four programs,       *)
(*                             including an identically-zero quadratic   *)
(*                             term and the draft's "special parameter   *)
(*                             value" case.                              *)
(*                                                                       *)
(* Scope, stated once.  This is a model fragment in Coq, not Blade's     *)
(* surface syntax or its F# compiler: nothing in src/ implements or is   *)
(* checked against it.  The zero test is exponential in r + m.  That a   *)
(* refusal means NO reduced program exists is proved over the reals, in  *)
(* reals/BladeReduceClassification.v, outside this tower.                *)
(*                                                                       *)
(* Stdlib only, no axioms.  Imports the recurrence-reduction files of    *)
(* the tower.                                                            *)
(* ===================================================================== *)

From Blade Require Import BladeBinomial BladeSummary BladeMomentClosure
  BladeRankBound BladeRankDomain BladeProuhet BladeNewton.
Require Import List Arith Lia ZArith Ring Bool InitialRing Setoid.
Import ListNotations.
Local Open Scope nat_scope.

(* ===================================================================== *)
(* Part A.  Integer syntax, read in any commutative ring.                *)
(* ===================================================================== *)

Fixpoint pdeg {T : Type} (e : pexp T) : nat :=
  match e with
  | PVar _ => 1
  | PConst _ => 0
  | PAdd e1 e2 => Nat.max (pdeg e1) (pdeg e2)
  | PMul e1 e2 => pdeg e1 + pdeg e2
  end.

(* Every variable of e is below n.                                       *)
Fixpoint pbelow {T : Type} (n : nat) (e : pexp T) : bool :=
  match e with
  | PVar i => Nat.ltb i n
  | PConst _ => true
  | PAdd e1 e2 => pbelow n e1 && pbelow n e2
  | PMul e1 e2 => pbelow n e1 && pbelow n e2
  end.

Definition setv {T : Type} (rho : nat -> T) (v : nat) (s : T) : nat -> T :=
  fun i => if Nat.eqb i v then s else rho i.

Section Reading.
  Variable K : Type.
  Variables (k0 k1 : K) (kadd kmul ksub : K -> K -> K) (kopp : K -> K).
  Hypothesis Kth : ring_theory k0 k1 kadd kmul ksub kopp (@eq K).
  Add Ring reading_ring : Kth.

  Local Infix "+!" := kadd (at level 50, left associativity).
  Local Infix "*!" := kmul (at level 40, left associativity).
  Local Notation Pev := (pev K kadd kmul).
  Local Notation Peval := (peval K k0 kadd kmul).
  Local Notation Ofnat := (ofnat K k0 k1 kadd).
  Local Notation Zev := (pev Z Z.add Z.mul).

  Definition phi : Z -> K := gen_phiZ k0 k1 kadd kmul kopp.

  Lemma phi_morph :
    ring_morph k0 k1 kadd kmul ksub kopp (@eq K)
               0%Z 1%Z Z.add Z.mul Z.sub Z.opp Zeq_bool phi.
  Proof. apply gen_phiZ_morph; [apply Eqsth|apply Eq_ext|exact Kth]. Qed.

  Lemma phi_0 : phi 0%Z = k0.
  Proof. exact (morph0 phi_morph). Qed.
  Lemma phi_1 : phi 1%Z = k1.
  Proof. exact (morph1 phi_morph). Qed.
  Lemma phi_add : forall x y, phi (x + y)%Z = phi x +! phi y.
  Proof. exact (morph_add phi_morph). Qed.
  Lemma phi_mul : forall x y, phi (x * y)%Z = phi x *! phi y.
  Proof. exact (morph_mul phi_morph). Qed.

  Lemma phi_of_nat : forall n, phi (Z.of_nat n) = Ofnat n.
  Proof.
    induction n as [|n IH]; [exact phi_0|].
    rewrite Nat2Z.inj_succ. unfold Z.succ. rewrite phi_add, phi_1, IH.
    cbn [ofnat]. ring.
  Qed.

  Fixpoint zmap (e : pexp Z) : pexp K :=
    match e with
    | PVar i => PVar i
    | PConst c => PConst (phi c)
    | PAdd e1 e2 => PAdd (zmap e1) (zmap e2)
    | PMul e1 e2 => PMul (zmap e1) (zmap e2)
    end.

  Definition zev (e : pexp Z) (rho : nat -> K) : K := Pev (zmap e) rho.

  Lemma zev_phi : forall e (z : nat -> Z),
    zev e (fun i => phi (z i)) = phi (Zev e z).
  Proof.
    unfold zev.
    induction e as [i|c|e1 IH1 e2 IH2|e1 IH1 e2 IH2]; intro z;
      cbn [zmap pev]; try reflexivity.
    - rewrite IH1, IH2, phi_add. reflexivity.
    - rewrite IH1, IH2, phi_mul. reflexivity.
  Qed.

  Lemma zmap_pdeg : forall e, pdeg (zmap e) = pdeg e.
  Proof.
    induction e as [i|c|e1 IH1 e2 IH2|e1 IH1 e2 IH2]; cbn [zmap pdeg];
      congruence.
  Qed.

  Lemma zmap_pbelow : forall n e, pbelow n (zmap e) = pbelow n e.
  Proof.
    intros n.
    induction e as [i|c|e1 IH1 e2 IH2|e1 IH1 e2 IH2]; cbn [zmap pbelow];
      congruence.
  Qed.

  Lemma pev_below : forall n (e : pexp K) rho rho',
    pbelow n e = true -> (forall i, i < n -> rho i = rho' i) ->
    Pev e rho = Pev e rho'.
  Proof.
    intros n.
    induction e as [i|c|e1 IH1 e2 IH2|e1 IH1 e2 IH2]; intros rho rho' Hb H;
      cbn [pbelow pev] in *.
    - apply H. apply Nat.ltb_lt. exact Hb.
    - reflexivity.
    - apply andb_true_iff in Hb. destruct Hb as [H1 H2].
      rewrite (IH1 rho rho' H1 H), (IH2 rho rho' H2 H). reflexivity.
    - apply andb_true_iff in Hb. destruct Hb as [H1 H2].
      rewrite (IH1 rho rho' H1 H), (IH2 rho rho' H2 H). reflexivity.
  Qed.

  Lemma zev_below : forall n e rho rho',
    pbelow n e = true -> (forall i, i < n -> rho i = rho' i) ->
    zev e rho = zev e rho'.
  Proof.
    intros n e rho rho' Hb H. unfold zev.
    apply (pev_below n); [rewrite zmap_pbelow; exact Hb|exact H].
  Qed.

  (* ------------------------------------------------------------------- *)
  (* One variable at a time, a polynomial expression is a coefficient    *)
  (* list of length 1 + its degree.                                      *)
  (* ------------------------------------------------------------------- *)

  Lemma uni_rep : forall (e : pexp K) v rho,
    exists l, length l = S (pdeg e) /\
              forall s, Pev e (setv rho v s) = Peval l s.
  Proof.
    induction e as [i|c|e1 IH1 e2 IH2|e1 IH1 e2 IH2]; intros v rho.
    - destruct (Nat.eqb_spec i v) as [E|E].
      + exists [k0; k1]. split; [reflexivity|]. intro s.
        cbn [pev peval]. unfold setv. subst i. rewrite Nat.eqb_refl. ring.
      + exists [rho i; k0]. split; [reflexivity|]. intro s.
        cbn [pev peval]. unfold setv.
        destruct (Nat.eqb_spec i v); [contradiction|]. ring.
    - exists [c]. split; [reflexivity|]. intro s. cbn [pev peval]. ring.
    - destruct (IH1 v rho) as [l1 [L1 E1]].
      destruct (IH2 v rho) as [l2 [L2 E2]].
      exists (padd K kadd l1 l2). split.
      + rewrite padd_length, L1, L2. cbn [pdeg]. lia.
      + intro s. cbn [pev].
        rewrite (peval_padd K k0 k1 kadd kmul ksub kopp Kth), E1, E2.
        reflexivity.
    - destruct (IH1 v rho) as [l1 [L1 E1]].
      destruct (IH2 v rho) as [l2 [L2 E2]].
      exists (pmul K k0 kadd kmul l1 l2). split.
      + cbn [pdeg]. apply pmul_length; assumption.
      + intro s. cbn [pev].
        rewrite (peval_pmul K k0 k1 kadd kmul ksub kopp Kth), E1, E2.
        reflexivity.
  Qed.

  Lemma setv_same : forall (rho : nat -> K) v i, setv rho v (rho v) i = rho i.
  Proof.
    intros rho v i. unfold setv. destruct (Nat.eqb_spec i v); congruence.
  Qed.

  (* ------------------------------------------------------------------- *)
  (* Part B.  THE ZERO TEST.  Over an integral domain of characteristic  *)
  (* zero, an expression that vanishes on the grid {0..D}^n, D its       *)
  (* degree, vanishes everywhere: one coordinate at a time, by root      *)
  (* counting.                                                           *)
  (* ------------------------------------------------------------------- *)

  Hypothesis K_integral : forall a b, a *! b = k0 -> a = k0 \/ b = k0.
  Hypothesis K_char0 : forall n, Ofnat (S n) <> k0.

  Lemma ofnat_plus : forall n m, Ofnat (n + m) = Ofnat n +! Ofnat m.
  Proof.
    induction n as [|n IH]; intro m; cbn [ofnat Nat.add]; [ring|].
    rewrite IH. ring.
  Qed.

  Lemma ofnat_inj : forall n m, Ofnat n = Ofnat m -> n = m.
  Proof.
    assert (Hlt : forall n m, n < m -> Ofnat n <> Ofnat m).
    { intros n m Hnm E. replace m with (n + S (m - n - 1)) in E by lia.
      rewrite ofnat_plus in E. apply (K_char0 (m - n - 1)).
      assert (Ofnat n +! Ofnat (S (m - n - 1)) = Ofnat n +! k0)
        by (rewrite <- E; ring).
      transitivity ((Ofnat n +! Ofnat (S (m - n - 1))) +! kopp (Ofnat n));
        [ring|]. rewrite H. ring. }
    intros n m E. destruct (Nat.lt_trichotomy n m) as [H|[H|H]];
      [exfalso; exact (Hlt n m H E)|exact H|
       exfalso; exact (Hlt m n H (eq_sym E))].
  Qed.

  Definition gridK (D : nat) : list K := map Ofnat (seq 0 (S D)).

  Lemma gridK_NoDup : forall D, NoDup (gridK D).
  Proof.
    intro D. unfold gridK. apply NoDup_map_in.
    - intros n m _ _. apply ofnat_inj.
    - apply seq_NoDup.
  Qed.

  Lemma gridK_length : forall D, length (gridK D) = S D.
  Proof.
    intro D. unfold gridK. rewrite map_length, seq_length. reflexivity.
  Qed.

  Lemma Forall_zero_peval : forall l s,
    Forall (fun c => c = k0) l -> Peval l s = k0.
  Proof.
    induction l as [|c l IH]; intros s H; [reflexivity|].
    inversion H as [|? ? Hc Hl]; subst. cbn [peval]. rewrite (IH s Hl). ring.
  Qed.

  Theorem grid_complete : forall (e : pexp K) n,
    (forall rho, (forall i, i < n -> In (rho i) (gridK (pdeg e))) ->
                 Pev e rho = k0) ->
    forall rho, Pev e rho = k0.
  Proof.
    intros e. induction n as [|n IH]; intros H rho.
    - apply H. intros i Hi. lia.
    - apply IH. clear rho. intros rho Hrho.
      destruct (uni_rep e n rho) as [l [Ll El]].
      assert (HF : Forall (fun c => c = k0) l).
      { apply (many_roots_dom K k0 k1 kadd kmul ksub kopp Kth K_integral
                 (gridK (pdeg e)) (gridK_NoDup (pdeg e))).
        - rewrite gridK_length, Ll. lia.
        - intros a Ha. rewrite <- El. apply H. intros i Hi. unfold setv.
          destruct (Nat.eqb_spec i n) as [->|Hne]; [exact Ha|].
          apply Hrho. lia. }
      rewrite (pev_ext K kadd kmul e rho (setv rho n (rho n)))
        by (intro i; symmetry; apply setv_same).
      rewrite El. apply Forall_zero_peval. exact HF.
  Qed.

  Lemma phi_inj_zero : forall c, phi c = k0 -> c = 0%Z.
  Proof.
    intros c Hc. destruct (Z.eq_dec c 0) as [E|E]; [exact E|]. exfalso.
    assert (Hn : forall n, phi (Z.of_nat (S n)) <> k0)
      by (intro n; rewrite phi_of_nat; apply K_char0).
    destruct (Z_lt_ge_dec c 0) as [Hneg|Hpos].
    - apply (Hn (Z.to_nat (- c) - 1)).
      replace (Z.of_nat (S (Z.to_nat (- c) - 1))) with (- c)%Z by lia.
      replace (- c)%Z with (0 - c)%Z by ring.
      rewrite (morph_sub phi_morph), phi_0, Hc. ring.
    - apply (Hn (Z.to_nat c - 1)).
      replace (Z.of_nat (S (Z.to_nat c - 1))) with c by lia. exact Hc.
  Qed.

End Reading.

(* ===================================================================== *)
(* The computable side: search the integer grid for a nonroot.           *)
(* ===================================================================== *)

Fixpoint tuples (n D : nat) : list (list Z) :=
  match n with
  | O => [[]]
  | S n' => flat_map (fun t => map (fun c => Z.of_nat c :: t) (seq 0 (S D)))
                     (tuples n' D)
  end.

Definition zenv (z : list Z) : nat -> Z := fun i => nth i z 0%Z.

Definition nonroot (e : pexp Z) (n : nat) : option (list Z) :=
  find (fun z => negb (Z.eqb (pev Z Z.add Z.mul e (zenv z)) 0))
       (tuples n (pdeg e)).

Lemma tuples_length : forall n D z, In z (tuples n D) -> length z = n.
Proof.
  induction n as [|n IH]; intros D z H; cbn [tuples] in H.
  - destruct H as [<-|[]]. reflexivity.
  - apply in_flat_map in H. destruct H as [t [Ht Hz]].
    apply in_map_iff in Hz. destruct Hz as [c [<- _]].
    cbn [length]. rewrite (IH D t Ht). reflexivity.
Qed.

(* Every grid point, read through any function on coordinates, is there. *)
Lemma tuples_complete : forall n D (f : nat -> nat),
  (forall i, i < n -> f i <= D) ->
  In (map (fun i => Z.of_nat (f i)) (seq 0 n)) (tuples n D).
Proof.
  induction n as [|n IH]; intros D f Hf; [left; reflexivity|].
  change (tuples (S n) D)
    with (flat_map (fun t => map (fun c => Z.of_nat c :: t) (seq 0 (S D)))
                   (tuples n D)).
  replace (map (fun i => Z.of_nat (f i)) (seq 0 (S n)))
    with (Z.of_nat (f 0) :: map (fun i => Z.of_nat (f (S i))) (seq 0 n)).
  2:{ cbn [seq map]. f_equal. rewrite <- seq_shift, map_map. reflexivity. }
  apply in_flat_map.
  exists (map (fun i => Z.of_nat (f (S i))) (seq 0 n)). split.
  - apply IH. intros i Hi. apply Hf. lia.
  - apply in_map_iff. exists (f 0). split; [reflexivity|].
    apply in_seq. pose proof (Hf 0 ltac:(lia)). lia.
Qed.

Lemma nonroot_some : forall e n z, nonroot e n = Some z ->
  length z = n /\ pev Z Z.add Z.mul e (zenv z) <> 0%Z.
Proof.
  intros e n z H. unfold nonroot in H. apply find_some in H.
  destruct H as [Hin Hne]. split; [exact (tuples_length _ _ _ Hin)|].
  apply negb_true_iff in Hne. apply Z.eqb_neq. exact Hne.
Qed.

Section ZeroTest.
  Variable K : Type.
  Variables (k0 k1 : K) (kadd kmul ksub : K -> K -> K) (kopp : K -> K).
  Hypothesis Kth : ring_theory k0 k1 kadd kmul ksub kopp (@eq K).
  Hypothesis K_integral : forall a b, kmul a b = k0 -> a = k0 \/ b = k0.
  Hypothesis K_char0 : forall n, ofnat K k0 k1 kadd (S n) <> k0.

  Local Notation Zv := (zev K k0 k1 kadd kmul kopp).
  Local Notation Phi := (phi K k0 k1 kadd kmul kopp).

  (* COMPLETENESS of the search: no nonroot on the grid means the        *)
  (* expression is zero as a function on K.                              *)
  Theorem nonroot_none : forall e n, pbelow n e = true ->
    nonroot e n = None -> forall rho, Zv e rho = k0.
  Proof.
    intros e n Hb Hnone. unfold zev.
    apply (grid_complete K k0 k1 kadd kmul ksub kopp Kth K_integral K_char0
             (zmap K k0 k1 kadd kmul kopp e) n).
    intros rho Hrho. rewrite zmap_pdeg in Hrho.
    (* read the grid coordinates back as naturals *)
    assert (Hf : exists f : nat -> nat, forall i, i < n ->
               f i <= pdeg e /\ rho i = ofnat K k0 k1 kadd (f i)).
    { clear Hb Hnone. revert rho Hrho. induction n as [|n IH]; intros rho Hrho.
      - exists (fun _ => 0). intros i Hi. lia.
      - destruct (IH rho ltac:(intros i Hi; apply Hrho; lia)) as [f Hf].
        pose proof (Hrho n ltac:(lia)) as Hn. unfold gridK in Hn.
        apply in_map_iff in Hn. destruct Hn as [c [Hc Hin]].
        apply in_seq in Hin.
        exists (fun i => if Nat.eqb i n then c else f i). intros i Hi.
        destruct (Nat.eqb_spec i n) as [->|Hne].
        + split; [lia|symmetry; exact Hc].
        + apply Hf. lia. }
    destruct Hf as [f Hf].
    set (z := map (fun i => Z.of_nat (f i)) (seq 0 n)).
    assert (Hz : In z (tuples n (pdeg e)))
      by (apply tuples_complete; intros i Hi; apply (Hf i Hi)).
    pose proof (find_none _ _ Hnone z Hz) as Hzero. cbv beta in Hzero.
    apply negb_false_iff, Z.eqb_eq in Hzero.
    fold (zev K k0 k1 kadd kmul kopp e rho).
    rewrite (zev_below K k0 k1 kadd kmul kopp n e rho
               (fun i => Phi (zenv z i)) Hb).
    - rewrite (zev_phi K k0 k1 kadd kmul ksub kopp Kth), Hzero.
      apply (phi_0 K k0 k1 kadd kmul ksub kopp Kth).
    - intros i Hi. destruct (Hf i Hi) as [_ ->].
      unfold zenv, z.
      rewrite (nth_indep _ 0%Z (Z.of_nat (f 0)))
        by (rewrite map_length, seq_length; exact Hi).
      rewrite (map_nth (fun i0 => Z.of_nat (f i0)) (seq 0 n) 0 i).
      rewrite seq_nth by exact Hi.
      symmetry. apply (phi_of_nat K k0 k1 kadd kmul ksub kopp Kth).
  Qed.

  (* SOUNDNESS of a found nonroot, in K.                                 *)
  Theorem nonroot_some_K : forall e n z, nonroot e n = Some z ->
    Zv e (fun i => Phi (zenv z i)) <> k0.
  Proof.
    intros e n z H. destruct (nonroot_some e n z H) as [_ Hne].
    rewrite (zev_phi K k0 k1 kadd kmul ksub kopp Kth). intro E.
    apply Hne. apply (phi_inj_zero K k0 k1 kadd kmul ksub kopp Kth K_char0).
    exact E.
  Qed.

End ZeroTest.

(* ===================================================================== *)
(* Part C.  THE SOURCE FRAGMENT.  One array of particles; every particle *)
(* gets  x' = sum_j a_j x^j,  the a_j integer-coefficient polynomials in *)
(* the moments p_1 .. p_r of the current array and in run-constant       *)
(* parameters theta_0 .. theta_(m-1).  The observation is such a         *)
(* polynomial too.  Variables: i < r is p_(i+1); r <= i < r + m is       *)
(* theta_(i-r).  The extent N is NOT part of the source program.         *)
(* ===================================================================== *)

Record prog : Type := mkprog {
  p_r : nat;
  p_m : nat;
  p_co : list (pexp Z);
  p_ob : pexp Z
}.

Definition p_n (P : prog) : nat := p_r P + p_m P.

Definition wf (P : prog) : bool :=
  Nat.leb 1 (p_r P) && forallb (pbelow (p_n P)) (p_co P)
  && pbelow (p_n P) (p_ob P).

(* The zero test, and the coefficient list with its identically-zero     *)
(* top stripped.                                                         *)
Definition iszero (n : nat) (e : pexp Z) : bool :=
  match nonroot e n with None => true | Some _ => false end.

Fixpoint strip (n : nat) (l : list (pexp Z)) : list (pexp Z) :=
  match l with
  | [] => []
  | a :: l' => match strip n l' with
               | [] => if iszero n a then [] else [a]
               | b :: s => a :: b :: s
               end
  end.

Definition stail (n : nat) (l : list (pexp Z)) : list (pexp Z) :=
  skipn (length (strip n l)) l.

Lemma strip_split : forall n l,
  l = strip n l ++ stail n l /\
  Forall (fun e => iszero n e = true) (stail n l).
Proof.
  intros n. unfold stail. induction l as [|a l [E Ht]].
  - split; [reflexivity|constructor].
  - cbn [strip]. destruct (strip n l) as [|b s] eqn:Es.
    + cbn [length skipn app] in E, Ht. destruct (iszero n a) eqn:Ez.
      * cbn [length skipn app]. split; [reflexivity|].
        constructor; assumption.
      * cbn [length skipn app]. split; [reflexivity|exact Ht].
    + cbn [length skipn]. cbn [length] in E, Ht. split; [|exact Ht].
      cbn [app]. f_equal. exact E.
Qed.

Lemma strip_last : forall n l, strip n l <> [] ->
  iszero n (last (strip n l) (PConst 0%Z)) = false.
Proof.
  intros n. induction l as [|a l IH]; intro H; [contradiction|].
  cbn [strip] in *. destruct (strip n l) as [|b s] eqn:Es.
  - destruct (iszero n a) eqn:Ez; [contradiction|exact Ez].
  - change (last (a :: b :: s) (PConst 0%Z)) with (last (b :: s) (PConst 0%Z)).
    apply IH. discriminate.
Qed.

Lemma iszero_const0 : forall n, iszero n (PConst 0%Z) = true.
Proof.
  intro n. unfold iszero. destruct (nonroot (PConst 0%Z) n) eqn:E;
    [|reflexivity].
  apply nonroot_some in E. destruct E as [_ E]. exfalso. apply E. reflexivity.
Qed.

Lemma last_as_nth : forall (A : Type) (l : list A) d,
  last l d = nth (length l - 1) l d.
Proof.
  intros A. induction l as [|a l IH]; intro d; [reflexivity|].
  destruct l as [|b l']; [reflexivity|].
  change (last (a :: b :: l') d) with (last (b :: l') d). rewrite IH.
  cbn [length Nat.sub]. rewrite Nat.sub_0_r. reflexivity.
Qed.

(* Three outcomes, as the draft asks.  On this fragment the third never  *)
(* occurs (classify_total).                                              *)
Inductive verdict : Type :=
| Reduced (a1 a0 : pexp Z)
| Refused (d : nat) (A : pexp Z) (z : list Z)
| Unknown.

Definition classify (P : prog) : verdict :=
  match strip (p_n P) (p_co P) with
  | [] => Reduced (PConst 0%Z) (PConst 0%Z)
  | [a0] => Reduced (PConst 0%Z) a0
  | [a0; a1] => Reduced a1 a0
  | a0 :: a1 :: a2 :: s =>
      let A := last (a2 :: s) (PConst 0%Z) in
      match nonroot A (p_n P) with
      | Some z => Refused (S (S (length s))) A z
      | None => Unknown
      end
  end.

Theorem classify_total : forall P, classify P <> Unknown.
Proof.
  intros P. unfold classify.
  pose proof (strip_last (p_n P) (p_co P)) as HL.
  destruct (strip (p_n P) (p_co P)) as [|a0 [|a1 [|a2 s]]]; try discriminate.
  specialize (HL ltac:(discriminate)).
  change (last (a0 :: a1 :: a2 :: s) (PConst 0%Z))
    with (last (a2 :: s) (PConst 0%Z)) in HL.
  unfold iszero in HL.
  destruct (nonroot (last (a2 :: s) (PConst 0%Z)) (p_n P)); discriminate.
Qed.

(* What a refusal certifies, syntactically: the effective degree d >= 2, *)
(* the leading coefficient A = a_d with an integer point where it is     *)
(* nonzero, and everything above d identically zero on the grid.         *)
Theorem classify_refused : forall P d A z, classify P = Refused d A z ->
  2 <= d /\ S d = length (strip (p_n P) (p_co P)) /\
  A = nth d (p_co P) (PConst 0%Z) /\
  nonroot A (p_n P) = Some z.
Proof.
  intros P d A z H. unfold classify in H.
  destruct (strip_split (p_n P) (p_co P)) as [E _].
  destruct (strip (p_n P) (p_co P)) as [|a0 [|a1 [|a2 s]]] eqn:Es;
    try discriminate.
  destruct (nonroot (last (a2 :: s) (PConst 0%Z)) (p_n P)) as [z'|] eqn:En;
    [|discriminate].
  injection H as Hd HA Hz'. subst d A z'. repeat split.
  - lia.
  - rewrite E, app_nth1 by (cbn [length]; lia).
    change (nth (S (S (length s))) (a0 :: a1 :: a2 :: s) (PConst 0%Z))
      with (nth (length s) (a2 :: s) (PConst 0%Z)).
    change (last (a2 :: s) (PConst 0%Z)
            = nth (length s) (a2 :: s) (PConst 0%Z)).
    rewrite (last_as_nth _ (a2 :: s) (PConst 0%Z)).
    replace (length (a2 :: s) - 1) with (length s) by (cbn [length]; lia).
    reflexivity.
  - exact En.
Qed.

(* --------------------------------------------------------------------- *)
(* The generated reduced program: r expressions over the summary, the    *)
(* parameters, and ONE extra variable -- index r + m -- for the extent.  *)
(* --------------------------------------------------------------------- *)

Fixpoint psumE (l : list (pexp Z)) : pexp Z :=
  match l with [] => PConst 0%Z | e :: l' => PAdd e (psumE l') end.

Definition Mv (r m j : nat) : pexp Z :=
  match j with O => PVar (r + m) | S j' => PVar j' end.

Definition Gk (r m : nat) (a1 a0 : pexp Z) (k : nat) : pexp Z :=
  psumE (map (fun j => PMul (PMul (PMul (PConst (Z.of_nat (C k j)))
                                        (ppow a1 j))
                                  (ppow a0 (k - j)))
                            (Mv r m j))
             (seq 0 (S k))).

Definition rprog_of (r m : nat) (a1 a0 : pexp Z) : list (pexp Z) :=
  map (Gk r m a1 a0) (seq 1 r).

Definition compile (P : prog) : option (list (pexp Z)) :=
  match classify P with
  | Reduced a1 a0 => Some (rprog_of (p_r P) (p_m P) a1 a0)
  | _ => None
  end.

Section Semantics.
  Variable K : Type.
  Variables (k0 k1 : K) (kadd kmul ksub : K -> K -> K) (kopp : K -> K).
  Hypothesis Kth : ring_theory k0 k1 kadd kmul ksub kopp (@eq K).
  Add Ring semantics_ring : Kth.

  Local Infix "+!" := kadd (at level 50, left associativity).
  Local Infix "*!" := kmul (at level 40, left associativity).
  Local Notation Zv := (zev K k0 k1 kadd kmul kopp).
  Local Notation Phi := (phi K k0 k1 kadd kmul kopp).
  Local Notation Peval := (peval K k0 kadd kmul).
  Local Notation Ofnat := (ofnat K k0 k1 kadd).
  Local Notation Rpow := (rpow K k1 kmul).
  Local Notation Rsum := (rsum K k0 kadd).
  Local Notation Qr := (qr K k0 k1 kadd kmul).

  Definition cenv (r : nat) (z : list K) (th : nat -> K) : nat -> K :=
    fun i => if Nat.ltb i r then nth i z k0 else th (i - r).

  Definition renv (r m N : nat) (z : list K) (th : nat -> K) : nat -> K :=
    fun i => if Nat.ltb i r then nth i z k0
             else if Nat.ltb i (r + m) then th (i - r) else Ofnat N.

  (* The ORIGINAL execution: all N particles.                            *)
  Definition coefK (P : prog) (th : nat -> K) (j : nat) (z : list K) : K :=
    Zv (nth j (p_co P) (PConst 0%Z)) (cenv (p_r P) z th).

  Definition sstep (P : prog) (th : nat -> K) (_ : unit) (xs : list K)
    : list K :=
    Fgen K k0 k1 kadd kmul (length (p_co P) - 1) (p_r P) (coefK P th) xs.

  Definition sobs (P : prog) (th : nat -> K) (xs : list K) : K :=
    Zv (p_ob P) (cenv (p_r P) (Qr (p_r P) xs) th).

  (* The REDUCED execution: r numbers, whatever N is.                    *)
  Definition rstep (gs : list (pexp Z)) (r m N : nat) (th : nat -> K)
      (_ : unit) (z : list K) : list K :=
    map (fun g => Zv g (renv r m N z th)) gs.

  Definition robs (P : prog) (N : nat) (th : nat -> K) (z : list K) : K :=
    Zv (p_ob P) (renv (p_r P) (p_m P) N z th).

  Lemma zev_const : forall c rho, Zv (PConst c) rho = Phi c.
  Proof. reflexivity. Qed.

  Lemma zev_add : forall e1 e2 rho,
    Zv (PAdd e1 e2) rho = Zv e1 rho +! Zv e2 rho.
  Proof. reflexivity. Qed.

  Lemma zev_mul : forall e1 e2 rho,
    Zv (PMul e1 e2) rho = Zv e1 rho *! Zv e2 rho.
  Proof. reflexivity. Qed.

  Lemma zev_psumE : forall l rho,
    Zv (psumE l) rho = Rsum (map (fun e => Zv e rho) l).
  Proof.
    induction l as [|e l IH]; intro rho; cbn [psumE map rsum].
    - rewrite zev_const. apply (phi_0 K k0 k1 kadd kmul ksub kopp Kth).
    - rewrite zev_add, IH. reflexivity.
  Qed.

  Lemma zev_ppow : forall e k rho, Zv (ppow e k) rho = Rpow (Zv e rho) k.
  Proof.
    intros e. induction k as [|k IH]; intro rho; cbn [ppow rpow].
    - rewrite zev_const. apply (phi_1 K k0 k1 kadd kmul ksub kopp Kth).
    - rewrite zev_mul, IH. reflexivity.
  Qed.

  Lemma env_agree : forall r m N z th i, i < r + m ->
    renv r m N z th i = cenv r z th i.
  Proof.
    intros r m N z th i Hi. unfold renv, cenv.
    destruct (Nat.ltb_spec i r); [reflexivity|].
    destruct (Nat.ltb_spec i (r + m)); [reflexivity|lia].
  Qed.

  Lemma zev_Gk : forall r m N a1 a0 k z th, k <= r ->
    pbelow (r + m) a1 = true -> pbelow (r + m) a0 = true ->
    Zv (Gk r m a1 a0 k) (renv r m N z th)
    = Rsum (map (fun j => Ofnat (C k j)
                          *! Rpow (Zv a1 (cenv r z th)) j
                          *! Rpow (Zv a0 (cenv r z th)) (k - j)
                          *! mom K k0 k1 kadd N z j)
                (seq 0 (S k))).
  Proof.
    intros r m N a1 a0 k z th Hk H1 H0. unfold Gk.
    rewrite zev_psumE, map_map. f_equal. apply map_ext_in.
    intros j Hj. apply in_seq in Hj.
    rewrite !zev_mul, !zev_ppow, zev_const.
    rewrite (phi_of_nat K k0 k1 kadd kmul ksub kopp Kth).
    rewrite (zev_below K k0 k1 kadd kmul kopp (r + m) a1 _ (cenv r z th) H1)
      by (intros i Hi; apply env_agree; exact Hi).
    rewrite (zev_below K k0 k1 kadd kmul kopp (r + m) a0 _ (cenv r z th) H0)
      by (intros i Hi; apply env_agree; exact Hi).
    f_equal. destruct j as [|j']; cbn [Mv mom].
    - unfold zev. cbn [zmap pev]. unfold renv.
      destruct (Nat.ltb_spec (r + m) r); [lia|].
      destruct (Nat.ltb_spec (r + m) (r + m)); [lia|reflexivity].
    - unfold zev. cbn [zmap pev]. unfold renv.
      destruct (Nat.ltb_spec j' r); [reflexivity|lia].
  Qed.

  Lemma rstep_Gm : forall r m N a1 a0 th z,
    pbelow (r + m) a1 = true -> pbelow (r + m) a0 = true ->
    rstep (rprog_of r m a1 a0) r m N th tt z
    = Gm K k0 k1 kadd kmul unit r N
         (fun _ z => Zv a1 (cenv r z th)) (fun _ z => Zv a0 (cenv r z th))
         tt z.
  Proof.
    intros r m N a1 a0 th z H1 H0. unfold rstep, rprog_of, Gm.
    rewrite map_map. apply map_ext_in. intros k Hk. apply in_seq in Hk.
    apply zev_Gk; [lia|exact H1|exact H0].
  Qed.

  Lemma peval_app_zero : forall l t x,
    Forall (fun c => c = k0) t -> Peval (l ++ t) x = Peval l x.
  Proof.
    induction l as [|c l IH]; intros t x Ht; cbn [app peval].
    - apply (Forall_zero_peval K k0 k1 kadd kmul ksub kopp Kth). exact Ht.
    - rewrite (IH t x Ht). reflexivity.
  Qed.

  Lemma map_nth_seq : forall (A B : Type) (f : A -> B) (l : list A) d,
    map (fun j => f (nth j l d)) (seq 0 (length l)) = map f l.
  Proof.
    intros A B f. induction l as [|a l IH]; intro d; [reflexivity|].
    cbn [length seq map nth]. f_equal.
    rewrite <- seq_shift, map_map. apply IH.
  Qed.

  (* The tail the zero test stripped really is zero on K.                *)
  Definition tailzero (P : prog) : Prop :=
    Forall (fun e => forall rho, Zv e rho = k0)
           (stail (p_n P) (p_co P)).

  Lemma sstep_stripped : forall P th xs t, tailzero P ->
    Peval (coefs K k0 k1 kadd kmul (length (p_co P) - 1) (p_r P)
                 (coefK P th) xs) t
    = Peval (map (fun e => Zv e (cenv (p_r P) (Qr (p_r P) xs) th))
                 (strip (p_n P) (p_co P))) t.
  Proof.
    intros P th xs t Hz. unfold coefs, coefK.
    set (ev := fun e => Zv e (cenv (p_r P) (Qr (p_r P) xs) th)).
    destruct (strip_split (p_n P) (p_co P)) as [E _].
    assert (Hfull : Peval (map (fun j => ev (nth j (p_co P) (PConst 0%Z)))
                               (seq 0 (S (length (p_co P) - 1)))) t
                    = Peval (map ev (p_co P)) t).
    { destruct (p_co P) as [|c l] eqn:Ec.
      - cbn [length Nat.sub seq map nth peval]. unfold ev.
        rewrite zev_const, (phi_0 K k0 k1 kadd kmul ksub kopp Kth). ring.
      - replace (S (length (c :: l) - 1)) with (length (c :: l))
          by (cbn [length]; lia).
        rewrite map_nth_seq. reflexivity. }
    fold ev. change (fun j : nat => Zv (nth j (p_co P) (PConst 0%Z))
                       (cenv (p_r P) (Qr (p_r P) xs) th))
      with (fun j : nat => ev (nth j (p_co P) (PConst 0%Z))).
    rewrite Hfull. rewrite E at 1. rewrite map_app.
    apply peval_app_zero. unfold tailzero in Hz.
    apply Forall_forall. intros c Hc. apply in_map_iff in Hc.
    destruct Hc as [e [<- He]]. rewrite Forall_forall in Hz.
    unfold ev. apply Hz. exact He.
  Qed.

  Lemma reduced_below : forall P a1 a0, wf P = true ->
    classify P = Reduced a1 a0 ->
    pbelow (p_n P) a1 = true /\ pbelow (p_n P) a0 = true.
  Proof.
    intros P a1 a0 Hwf H. unfold wf in Hwf.
    apply andb_true_iff in Hwf. destruct Hwf as [Hwf _].
    apply andb_true_iff in Hwf. destruct Hwf as [_ Hco].
    rewrite forallb_forall in Hco.
    destruct (strip_split (p_n P) (p_co P)) as [E _].
    assert (Hin : forall e, In e (strip (p_n P) (p_co P)) ->
                    pbelow (p_n P) e = true).
    { intros e He. apply Hco. rewrite E. apply in_or_app. left. exact He. }
    unfold classify in H.
    destruct (strip (p_n P) (p_co P)) as [|b0 [|b1 [|b2 s]]].
    - inversion H; subst. split; reflexivity.
    - inversion H; subst. split; [reflexivity|]. apply Hin. left. reflexivity.
    - inversion H; subst. split; apply Hin; cbn [In]; auto.
    - destruct (nonroot (last (b2 :: s) (PConst 0%Z)) (p_n P)); discriminate.
  Qed.

  Lemma sstep_affine : forall P a1 a0 th xs, tailzero P ->
    classify P = Reduced a1 a0 ->
    sstep P th tt xs
    = Fm K k0 k1 kadd kmul unit (p_r P)
         (fun _ z => Zv a1 (cenv (p_r P) z th))
         (fun _ z => Zv a0 (cenv (p_r P) z th)) tt xs.
  Proof.
    intros P a1 a0 th xs Hz H. unfold sstep, Fgen, Fm.
    apply map_ext. intro t. rewrite (sstep_stripped P th xs t Hz).
    unfold classify in H. unfold aff.
    destruct (strip (p_n P) (p_co P)) as [|b0 [|b1 [|b2 s]]].
    - inversion H; subst. cbn [map peval].
      rewrite zev_const, (phi_0 K k0 k1 kadd kmul ksub kopp Kth). ring.
    - inversion H; subst. cbn [map peval].
      rewrite zev_const, (phi_0 K k0 k1 kadd kmul ksub kopp Kth). ring.
    - inversion H; subst. cbn [map peval]. ring.
    - destruct (nonroot (last (b2 :: s) (PConst 0%Z)) (p_n P)); discriminate.
  Qed.

  (* THE COMPILER THEOREM, certificate form.  For every extent N and     *)
  (* every parameter vector, the generated program and the identity      *)
  (* observation are a reduction certificate for the source program.     *)
  Theorem compile_certificate : forall P gs, wf P = true ->
    compile P = Some gs -> tailzero P ->
    forall N th,
      certificate (sstep P th) (Qr (p_r P)) (sobs P th)
                  (fun xs => length xs = N)
                  (rstep gs (p_r P) (p_m P) N th) (robs P N th).
  Proof.
    intros P gs Hwf Hc Hz N th. unfold compile in Hc.
    destruct (classify P) as [a1 a0| |] eqn:Hcl; try discriminate.
    inversion Hc; subst gs. clear Hc.
    destruct (reduced_below P a1 a0 Hwf Hcl) as [H1 H0].
    constructor.
    - intros [] xs HN. unfold sstep.
      rewrite (Fgen_length K k0 k1 kadd kmul). exact HN.
    - intros [] xs HN. rewrite (sstep_affine P a1 a0 th xs Hz Hcl).
      rewrite (rstep_Gm (p_r P) (p_m P) N a1 a0 th _ H1 H0).
      apply (moment_step_closed K k0 k1 kadd kmul ksub kopp Kth). exact HN.
    - intros xs _. unfold sobs, robs. symmetry.
      unfold wf in Hwf. apply andb_true_iff in Hwf. destruct Hwf as [_ Hob].
      apply (zev_below K k0 k1 kadd kmul kopp (p_n P)); [exact Hob|].
      intros i Hi. apply env_agree. exact Hi.
  Qed.

  (* Simulation: every prefix observation of every finite execution.     *)
  Theorem compile_sound_gen : forall P gs, wf P = true ->
    compile P = Some gs -> tailzero P ->
    forall N th w xs, length xs = N ->
      trace (sstep P th) (sobs P th) w xs
      = trace (rstep gs (p_r P) (p_m P) N th) (robs P N th) w
              (Qr (p_r P) xs).
  Proof.
    intros P gs Hwf Hc Hz N th w xs HN.
    apply (summary_trace_sound unit (list K) (list K) K (sstep P th)
             (Qr (p_r P)) (sobs P th) (fun xs => length xs = N)
             (rstep gs (p_r P) (p_m P) N th) (robs P N th)).
    - apply compile_certificate; assumption.
    - exact HN.
  Qed.

  (* Any commutative ring at all -- wrapping machine integers, dual      *)
  (* numbers -- when the zero test had nothing to strip.                 *)
  Corollary compile_sound_unstripped : forall P gs, wf P = true ->
    compile P = Some gs -> strip (p_n P) (p_co P) = p_co P ->
    forall N th w xs, length xs = N ->
      trace (sstep P th) (sobs P th) w xs
      = trace (rstep gs (p_r P) (p_m P) N th) (robs P N th) w
              (Qr (p_r P) xs).
  Proof.
    intros P gs Hwf Hc Hs. apply compile_sound_gen; try assumption.
    unfold tailzero, stail. rewrite Hs, skipn_all. constructor.
  Qed.

End Semantics.

(* Over an integral domain of characteristic zero -- Z, Q, R -- the zero *)
(* test is exact, so the theorem is unconditional.                       *)
Section DomainSemantics.
  Variable K : Type.
  Variables (k0 k1 : K) (kadd kmul ksub : K -> K -> K) (kopp : K -> K).
  Hypothesis Kth : ring_theory k0 k1 kadd kmul ksub kopp (@eq K).
  Hypothesis K_integral : forall a b, kmul a b = k0 -> a = k0 \/ b = k0.
  Hypothesis K_char0 : forall n, ofnat K k0 k1 kadd (S n) <> k0.

  Lemma tailzero_domain : forall P, wf P = true ->
    tailzero K k0 k1 kadd kmul kopp P.
  Proof.
    intros P Hwf. unfold tailzero.
    destruct (strip_split (p_n P) (p_co P)) as [E Ht].
    unfold wf in Hwf. apply andb_true_iff in Hwf. destruct Hwf as [Hwf _].
    apply andb_true_iff in Hwf. destruct Hwf as [_ Hco].
    rewrite forallb_forall in Hco.
    apply Forall_forall. intros e He rho.
    rewrite Forall_forall in Ht. specialize (Ht e He). unfold iszero in Ht.
    destruct (nonroot e (p_n P)) eqn:En; [discriminate|].
    apply (nonroot_none K k0 k1 kadd kmul ksub kopp Kth K_integral K_char0
             e (p_n P)); [|exact En].
    apply Hco. rewrite E. apply in_or_app. right. exact He.
  Qed.

  Theorem compile_sound : forall P gs, wf P = true -> compile P = Some gs ->
    forall N th w xs, length xs = N ->
      trace (sstep K k0 k1 kadd kmul kopp P th)
            (sobs K k0 k1 kadd kmul kopp P th) w xs
      = trace (rstep K k0 k1 kadd kmul kopp gs (p_r P) (p_m P) N th)
              (robs K k0 k1 kadd kmul kopp P N th) w
              (qr K k0 k1 kadd kmul (p_r P) xs).
  Proof.
    intros P gs Hwf Hc.
    apply (compile_sound_gen K k0 k1 kadd kmul ksub kopp Kth);
      try assumption.
    apply tailzero_domain. exact Hwf.
  Qed.

End DomainSemantics.

(* ===================================================================== *)
(* Part D.  What a refusal says about the source program, in a form the  *)
(* negative theorems consume: the program IS the degree-d polynomial     *)
(* update with leading coefficient A, and A is nonzero at the witness.   *)
(* ===================================================================== *)

Section Refusal.
  Variable K : Type.
  Variables (k0 k1 : K) (kadd kmul ksub : K -> K -> K) (kopp : K -> K).
  Hypothesis Kth : ring_theory k0 k1 kadd kmul ksub kopp (@eq K).

  Local Notation Zv := (zev K k0 k1 kadd kmul kopp).
  Local Notation Phi := (phi K k0 k1 kadd kmul kopp).
  Local Notation Peval := (peval K k0 kadd kmul).
  Local Notation Qr := (qr K k0 k1 kadd kmul).

  (* The parameter vector read off a witness point.                      *)
  Definition wtheta (r : nat) (z : list Z) : nat -> K :=
    fun i => Phi (zenv z (r + i)).

  Definition wsummary (r : nat) (z : list Z) : list K :=
    map Phi (firstn r z).

  Lemma nth_firstn_lt : forall (A : Type) (l : list A) r i d, i < r ->
    nth i (firstn r l) d = nth i l d.
  Proof.
    intros A. induction l as [|a l IH]; intros r i d Hi.
    - rewrite firstn_nil. reflexivity.
    - destruct r as [|r]; [lia|]. destruct i as [|i]; [reflexivity|].
      cbn [firstn nth]. apply IH. lia.
  Qed.

  Lemma cenv_witness : forall r m z i, i < r + m ->
    cenv K k0 r (wsummary r z) (wtheta r z) i = Phi (zenv z i).
  Proof.
    intros r m z i Hi. unfold cenv, wsummary, wtheta.
    destruct (Nat.ltb_spec i r) as [Hlt|Hge].
    - transitivity (nth i (map Phi (firstn r z)) (Phi 0%Z)).
      + reflexivity.
      + rewrite map_nth. unfold zenv. f_equal. apply nth_firstn_lt.
        exact Hlt.
    - replace (r + (i - r)) with i by lia. reflexivity.
  Qed.

  Theorem sstep_truncated : forall P th d xs,
    tailzero K k0 k1 kadd kmul kopp P ->
    S d = length (strip (p_n P) (p_co P)) ->
    sstep K k0 k1 kadd kmul kopp P th tt xs
    = Fgen K k0 k1 kadd kmul d (p_r P)
           (coefK K k0 k1 kadd kmul kopp P th) xs.
  Proof.
    intros P th d xs Hz Hd. unfold sstep, Fgen. apply map_ext. intro t.
    rewrite (sstep_stripped K k0 k1 kadd kmul ksub kopp Kth P th xs t Hz).
    f_equal. unfold coefs, coefK.
    destruct (strip_split (p_n P) (p_co P)) as [E _].
    rewrite Hd. rewrite <- (map_nth_seq _ _ _ (strip (p_n P) (p_co P))
                              (PConst 0%Z)).
    apply map_ext_in. intros j Hj. apply in_seq in Hj.
    rewrite E at 2. rewrite app_nth1 by lia. reflexivity.
  Qed.

End Refusal.

(* ===================================================================== *)
(* Part E.  The compiler, run.                                           *)
(* ===================================================================== *)

(* x' = (1 + p_1) x + theta_0, observing p_2: reduced.                   *)
Definition ex_affine : prog :=
  mkprog 2 1 [PVar 2; PAdd (PConst 1%Z) (PVar 0)] (PVar 1).

(* x' = x^2, observing p_2: refused, degree 2.                           *)
Definition ex_square : prog :=
  mkprog 2 0 [PConst 0%Z; PConst 0%Z; PConst 1%Z] (PVar 1).

(* x' = x + (p_1 - p_1) x^2: the quadratic term is identically zero, and *)
(* the zero test sees it.                                                *)
Definition ex_fake_square : prog :=
  mkprog 2 0 [PConst 0%Z; PConst 1%Z;
              PAdd (PVar 0) (PMul (PConst (-1)%Z) (PVar 0))] (PVar 1).

(* x' = x + theta_0 x^2: affine at theta_0 = 0 only.  Refused, with the  *)
(* witness theta_0 = 1.                                                  *)
Definition ex_special_value : prog :=
  mkprog 1 1 [PConst 0%Z; PConst 1%Z; PVar 1] (PVar 0).

Theorem compiler_runs :
  classify ex_affine = Reduced (PAdd (PConst 1%Z) (PVar 0)) (PVar 2) /\
  classify ex_square = Refused 2 (PConst 1%Z) [0%Z; 0%Z] /\
  classify ex_fake_square = Reduced (PConst 1%Z) (PConst 0%Z) /\
  classify ex_special_value = Refused 2 (PVar 1) [0%Z; 1%Z] /\
  wf ex_affine = true /\ wf ex_square = true /\
  wf ex_fake_square = true /\ wf ex_special_value = true.
Proof. repeat split; vm_compute; reflexivity. Qed.

(* The generated program for ex_affine has two expressions, for S' and   *)
(* Q'; read over Z at N = 3, theta_0 = 5, (S, Q) = (6, 14) -- the array  *)
(* [1; 2; 3] -- it returns the moments of [12; 19; 26].                  *)
Theorem compiled_affine_runs :
  option_map
    (fun gs => rstep Z 0%Z 1%Z Z.add Z.mul Z.opp gs 2 1 3
                     (fun _ => 5%Z) tt [6%Z; 14%Z])
    (compile ex_affine)
  = Some [57%Z; 1181%Z].
Proof. vm_compute. reflexivity. Qed.
