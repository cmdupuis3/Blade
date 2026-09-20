(* ===================================================================== *)
(* BladeOptimal.v -- UNIFORM OPTIMALITY, one nest: the orbit bound on    *)
(* kernel work and storage (docs/plans/plan-uniform-optimality.md,       *)
(* T1, T1', T2).                                                         *)
(*                                                                       *)
(* BladeLowering proved the licence group sound and BladeCompleteness    *)
(* proved it exact: no PERMUTATION outside H-and-Stab can be granted     *)
(* uniformly.  Both compare the compiler's choice against other grants.  *)
(* This file compares it against every PROGRAM.  The kernel is an        *)
(* oracle known only to lie in the declared law class; a program may ask *)
(* it anything, adaptively, in any order, and owes the right output for  *)
(* every kernel in the class.  Then:                                     *)
(*                                                                       *)
(*   orbit_lower_bound        (abstract) cells whose argument tuples lie *)
(*                            in pairwise distinct classes, each class   *)
(*                            PERTURBABLE inside the law class, cost one *)
(*                            query each.  The adversary moves the       *)
(*                            kernel on the one class the program never  *)
(*                            asked about; run_agree says the program    *)
(*                            cannot tell.  No group theory, no counting *)
(*                            -- those live in the instances.            *)
(*   orbit_storage_bound      an opaque-cell store read through a fixed  *)
(*                            access map IS the non-adaptive program     *)
(*                            that asks every cell, so the cell count    *)
(*                            obeys the same bound.                      *)
(*                                                                       *)
(* Instances, each closed by an attaining program and stated as          *)
(* "no correct program asks fewer questions than the canonical one":     *)
(*                                                                       *)
(*   uniform_optimality_sym   H = S_r, one array in every position:      *)
(*                            C(n+r-1, r) forced (sym_nest_lower_bound,  *)
(*                            sym_nest_storage_bound) and attained by    *)
(*                            BladeDMWF's enum, which also answers every *)
(*                            cell of the DENSE index space by sorting   *)
(*                            the index tuple (sym_nest_attained).  The  *)
(*                            law class is the tower's own: local, and   *)
(*                            invariant_under every perm_pair -- shown   *)
(*                            equal to "constant on sorted tabulations"  *)
(*                            (Kcls_Ksym, Ksym_Kcls; the converse is     *)
(*                            where a list Permutation becomes a         *)
(*                            position permutation with an inverse, via  *)
(*                            Permutation_nth and FinFun).               *)
(*   uniform_optimality_young H = S_R over SEVERAL arrays, block j of    *)
(*                            r_j positions reading array j.  G is then  *)
(*                            H-and-Stab proper, the Young subgroup, and *)
(*                            the forced count is BladeMixedRadix's      *)
(*                            shapeCard = prod_j C(n_j + r_j - 1, r_j).  *)
(*                            New content: svals_separate -- generic     *)
(*                            data remembers its array, so a permutation *)
(*                            of the whole value tuple restricts to one  *)
(*                            per block.  shargs_is_Out ties the shape   *)
(*                            to BladeLowering's Out at binding sgrp;    *)
(*                            uniform_optimality_young_nat discharges    *)
(*                            the genericity hypotheses by Cantor        *)
(*                            pairing, so the statement is closed.       *)
(*   antisym_lower_bound,     the SIGNED case at r = 2 (BladeLowering's  *)
(*   antisym_attained,        antiinvariant_under, neg = Z.opp).  A      *)
(*   antisym_diagonal_        class is free when it can be coherently    *)
(*     forced_zero            oriented (sigma); the strict pairs are,    *)
(*                            C(n, 2) of them, and cost a query each;    *)
(*                            the diagonal is not, every kernel in the   *)
(*                            class vanishes there, and the attaining    *)
(*                            program spends nothing on it.  AntisymIdx  *)
(*                            storing no diagonal is forced, not chosen. *)
(*                                                                       *)
(* The refutation that keeps the statement honest:                       *)
(*                                                                       *)
(*   nongeneric_data_beats_bound   on a constant array ONE query answers *)
(*                            every cell.  The bound is about generic    *)
(*                            data -- hence about every data-oblivious   *)
(*                            schedule, which must be right on it.       *)
(*                                                                       *)
(* Scope.  The kernel laws covered are the full symmetric group on the   *)
(* argument positions (any binding), and the sign character at r = 2.    *)
(* A proper subgroup H < S_R, the signed case at general r, and storage  *)
(* that is not opaque cells (finite-U counting, linear encodings -- the  *)
(* setting of BladeDichotomy) are prose in the plan.  Cost is kernel     *)
(* evaluations and cells, NOT time: with BladeLayout's canonical-form    *)
(* minimality this is a second necessary condition for fastest, and      *)
(* still no sufficient one.                                              *)
(*                                                                       *)
(* Imports BladeDMWF, BladeBinomial, BladeMixedRadix, BladeShape,        *)
(* BladeLowering, BladeCompleteness.  Coq 8.18, stdlib only.             *)
(* ===================================================================== *)

From Blade Require Import BladeDMWF BladeBinomial BladeMixedRadix BladeShape
                          BladeLowering
                          BladeCompleteness.
Require Import List Arith Lia Permutation FinFun ZArith Cantor.
Import ListNotations.

(* ===================================================================== *)
(* PART A.  ORACLE PROGRAMS.                                             *)
(* A program interacts with the kernel only by asking for its value at   *)
(* a query; what it asks next may depend on every answer so far (the     *)
(* continuation), so adaptivity is built in.  run is the result, trace   *)
(* the queries actually made against a given oracle.                     *)
(* ===================================================================== *)

Section OraclePrograms.
  Variables Q A O : Type.

  Inductive prog : Type :=
  | Ret : O -> prog
  | Ask : Q -> (A -> prog) -> prog.

  Fixpoint run (P : prog) (k : Q -> A) : O :=
    match P with
    | Ret o => o
    | Ask q c => run (c (k q)) k
    end.

  Fixpoint trace (P : prog) (k : Q -> A) : list Q :=
    match P with
    | Ret _ => []
    | Ask q c => q :: trace (c (k q)) k
    end.

  (* Two oracles that agree on every query the program makes against the *)
  (* first are indistinguishable to it: same result, same trace.         *)
  Lemma run_agree : forall P k k',
    (forall q, In q (trace P k) -> k' q = k q) ->
    run P k' = run P k /\ trace P k' = trace P k.
  Proof.
    induction P as [o | q c IH]; intros k k' H; simpl.
    - split; reflexivity.
    - assert (E : k' q = k q) by (apply H; left; reflexivity).
      rewrite E.
      destruct (IH (k q) k k') as [Hr Ht].
      + intros q' Hq'. apply H. right. exact Hq'.
      + split; [exact Hr | f_equal; exact Ht].
  Qed.
End OraclePrograms.

Arguments Ret {Q A O} _.
Arguments Ask {Q A O} _ _.
Arguments run {Q A O} _ _.
Arguments trace {Q A O} _ _.

(* Decidable pigeonhole: a list either sits inside another or has a      *)
(* member that escapes it.                                               *)
Lemma incl_or_witness : forall (C : Type)
  (C_dec : forall c c' : C, {c = c'} + {c <> c'}) (l t : list C),
  incl l t \/ exists c, In c l /\ ~ In c t.
Proof.
  intros C C_dec l t. induction l as [|a l IH].
  - left. intros x [].
  - destruct (in_dec C_dec a t) as [Ha | Ha].
    + destruct IH as [IH | (c & Hc & Hn)].
      * left. intros x [E | Hx]; [subst; exact Ha | apply IH; exact Hx].
      * right. exists c. split; [right; exact Hc | exact Hn].
    + right. exists a. split; [left; reflexivity | exact Ha].
Qed.

(* ===================================================================== *)
(* PART B.  THE ORBIT LOWER BOUND, abstractly.                           *)
(* V = argument tuples, cls names a tuple's class under the declared     *)
(* laws, K is the law class of kernels, args sends an output cell to the *)
(* tuple the kernel is applied to there.  A cell is PERTURBABLE when the *)
(* law class lets the kernel's value on that cell's class move without   *)
(* moving it anywhere else.  The theorem: cells in pairwise distinct     *)
(* perturbable classes cost one query each.                              *)
(* ===================================================================== *)

Section OrbitBound.
  Variables V C X A O : Type.
  Variable cls : V -> C.
  Hypothesis C_dec : forall c c' : C, {c = c'} + {c <> c'}.
  Variable K : (V -> A) -> Prop.
  Variable args : X -> V.
  Variable out : O -> X -> A.

  Definition perturbable (x : X) : Prop :=
    exists pert : (V -> A) -> (V -> A),
      (forall k, K k -> K (pert k)) /\
      (forall k v, cls v <> cls (args x) -> pert k v = k v) /\
      (forall k, pert k (args x) <> k (args x)).

  Definition correct_on (R : list X) (P : prog V A O) : Prop :=
    forall k, K k -> forall x, In x R -> out (run P k) x = k (args x).

  Theorem orbit_lower_bound : forall (R : list X) (P : prog V A O),
    NoDup (map (fun x => cls (args x)) R) ->
    (forall x, In x R -> perturbable x) ->
    correct_on R P ->
    forall k, K k -> length R <= length (trace P k).
  Proof.
    intros R P Hnd Hpert Hcor k Hk.
    rewrite <- (map_length (fun x => cls (args x)) R).
    rewrite <- (map_length cls (trace P k)).
    destruct (incl_or_witness C C_dec
                (map (fun x => cls (args x)) R) (map cls (trace P k)))
      as [Hincl | (c & Hc & Hn)].
    - apply NoDup_incl_length; assumption.
    - exfalso.
      apply in_map_iff in Hc as (x & Ex & Hx). subst c.
      destruct (Hpert x Hx) as (pert & HK & Hoff & Hon).
      assert (Hag : forall q, In q (trace P k) -> pert k q = k q).
      { intros q Hq. apply Hoff. intro E. apply Hn.
        rewrite <- E. apply in_map. exact Hq. }
      destruct (run_agree _ _ _ P k (pert k) Hag) as [Hrun _].
      pose proof (Hcor (pert k) (HK k Hk) x Hx) as H1.
      pose proof (Hcor k Hk x Hx) as H2.
      rewrite Hrun in H1. rewrite H2 in H1.
      apply (Hon k). symmetry. exact H1.
  Qed.
End OrbitBound.

(* ===================================================================== *)
(* PART C.  STORAGE.  A store of opaque cells -- each holds the kernel's *)
(* value at one fixed tuple -- read through a fixed access map, is the   *)
(* non-adaptive program that asks every cell.  So the cell count obeys   *)
(* the same bound.                                                       *)
(* ===================================================================== *)

Section Storage.
  Variables V A : Type.

  Fixpoint ask_all (cells : list V) (got : list A) : prog V A (list A) :=
    match cells with
    | [] => Ret (rev got)
    | q :: cs => Ask q (fun a => ask_all cs (a :: got))
    end.

  Lemma ask_all_run : forall cells got k,
    run (ask_all cells got) k = rev got ++ map k cells.
  Proof.
    induction cells as [|q cs IH]; intros got k; simpl.
    - rewrite app_nil_r. reflexivity.
    - rewrite IH. simpl. rewrite <- app_assoc. reflexivity.
  Qed.

  Lemma ask_all_trace : forall cells got k,
    trace (ask_all cells got) k = cells.
  Proof.
    induction cells as [|q cs IH]; intros got k; simpl.
    - reflexivity.
    - f_equal. apply IH.
  Qed.
End Storage.

Arguments ask_all {V A} _ _.

Section StorageBound.
  Variables V C X A : Type.
  Variable cls : V -> C.
  Hypothesis C_dec : forall c c' : C, {c = c'} + {c <> c'}.
  Variable K : (V -> A) -> Prop.
  Variable args : X -> V.

  Theorem orbit_storage_bound :
    forall (R : list X) (cells : list V) (acc : X -> nat) (d : A) (k0 : V -> A),
    K k0 ->
    NoDup (map (fun x => cls (args x)) R) ->
    (forall x, In x R -> perturbable V C X A cls K args x) ->
    (forall k, K k -> forall x, In x R ->
       nth (acc x) (map k cells) d = k (args x)) ->
    length R <= length cells.
  Proof.
    intros R cells acc d k0 Hk0 Hnd Hpert Hread.
    rewrite <- (ask_all_trace V A cells [] k0).
    apply (orbit_lower_bound V C X A (list A) cls C_dec K args
             (fun o x => nth (acc x) o d) R (ask_all cells [])); try assumption.
    intros k Hk x Hx. rewrite ask_all_run. simpl. apply Hread; assumption.
  Qed.
End StorageBound.

(* ===================================================================== *)
(* PART D.  The full-symmetry nest: one array in all r positions, a      *)
(* kernel known only to be invariant under every position permutation.   *)
(* ===================================================================== *)

(* --- a canonical form for value tuples: insertion sort ---------------- *)

Fixpoint insert (x : nat) (l : list nat) : list nat :=
  match l with
  | [] => [x]
  | y :: l' => if x <=? y then x :: l else y :: insert x l'
  end.

Fixpoint isort (l : list nat) : list nat :=
  match l with
  | [] => []
  | x :: l' => insert x (isort l')
  end.

Lemma insert_perm : forall x l, Permutation (x :: l) (insert x l).
Proof.
  intros x l. induction l as [|y l IH]; simpl.
  - apply Permutation_refl.
  - destruct (x <=? y).
    + apply Permutation_refl.
    + apply perm_trans with (y :: x :: l).
      * apply perm_swap.
      * apply perm_skip. exact IH.
Qed.

Lemma isort_perm : forall l, Permutation l (isort l).
Proof.
  induction l as [|x l IH]; simpl.
  - apply perm_nil.
  - apply perm_trans with (x :: isort l).
    + apply perm_skip. exact IH.
    + apply insert_perm.
Qed.

Lemma insert_comm : forall x y l,
  insert x (insert y l) = insert y (insert x l).
Proof.
  intros x y l. induction l as [|a l IH]; simpl.
  - destruct (Nat.leb_spec x y), (Nat.leb_spec y x);
      try lia; try reflexivity;
      (assert (x = y) by lia; subst; reflexivity).
  - repeat match goal with
           | |- context [?a <=? ?b] => destruct (Nat.leb_spec a b); simpl
           end;
      try lia; try reflexivity;
      try (rewrite IH; reflexivity);
      try (assert (x = y) by lia; subst; reflexivity).
Qed.

(* The canonical form is a complete invariant of the S_r-orbit.          *)
Lemma isort_respects : forall l l', Permutation l l' -> isort l = isort l'.
Proof.
  induction 1; simpl.
  - reflexivity.
  - rewrite IHPermutation. reflexivity.
  - apply insert_comm.
  - etransitivity; eassumption.
Qed.

Lemma isort_length : forall l, length (isort l) = length l.
Proof. intro l. symmetry. apply Permutation_length. apply isort_perm. Qed.

Lemma canonical_lb : forall r l u t,
  canonical r l u t -> forall x, In x t -> l <= x.
Proof.
  induction r as [|r IH]; intros l u t Hc x Hx;
    destruct t as [|i t']; simpl in Hc; try contradiction.
  destruct Hc as [Hb Hc]. destruct Hx as [E | Hin].
  - subst. lia.
  - specialize (IH _ _ _ Hc _ Hin). lia.
Qed.

Lemma canonical_len : forall r l u t, canonical r l u t -> length t = r.
Proof.
  induction r as [|r IH]; intros l u t Hc;
    destruct t as [|i t']; simpl in Hc; try contradiction.
  - reflexivity.
  - destruct Hc as [_ Hc]. simpl. f_equal. exact (IH _ _ _ Hc).
Qed.

(* A canonical index tuple is already in canonical form. *)
Lemma isort_canonical : forall r l u t, canonical r l u t -> isort t = t.
Proof.
  induction r as [|r IH]; intros l u t Hc;
    destruct t as [|i t']; simpl in Hc; try contradiction.
  - reflexivity.
  - destruct Hc as [Hb Hc]. simpl. rewrite (IH _ _ _ Hc).
    destruct t' as [|j t'']; simpl.
    + reflexivity.
    + assert (i <= j) by (apply (canonical_lb r i u (j :: t'') Hc); left; reflexivity).
      destruct (Nat.leb_spec i j); [reflexivity | lia].
Qed.

Lemma insert_canon : forall r l u t x,
  canonical r l u t -> x < u ->
  canonical (S r) (Nat.min l x) u (insert x t).
Proof.
  induction r as [|r IH]; intros l u t x Hc Hx;
    destruct t as [|i t']; simpl in Hc; try contradiction.
  - simpl. split; [lia | exact I].
  - destruct Hc as [Hb Hc]. simpl insert.
    destruct (Nat.leb_spec x i).
    + simpl. repeat split; try lia. exact Hc.
    + change (canonical (S (S r)) (Nat.min l x) u (i :: insert x t')).
      simpl. split; [lia |].
      specialize (IH i u t' x Hc Hx).
      rewrite Nat.min_l in IH by lia. exact IH.
Qed.

Lemma isort_canon : forall n ix,
  (forall x, In x ix -> x < n) -> canonical (length ix) 0 n (isort ix).
Proof.
  intros n ix. induction ix as [|a ix IH]; intro H; simpl.
  - exact I.
  - assert (Ha : a < n) by (apply H; left; reflexivity).
    assert (Hc : canonical (length ix) 0 n (isort ix))
      by (apply IH; intros x Hx; apply H; right; exact Hx).
    pose proof (insert_canon _ _ _ _ a Hc Ha) as HH.
    rewrite Nat.min_0_l in HH. exact HH.
Qed.

(* --- tabulating a function tuple ------------------------------------- *)

Definition tab (r : nat) (v : nat -> nat) : list nat := map v (seq 0 r).

Lemma tab_nth : forall (l : list nat) d,
  map (fun p => nth p l d) (seq 0 (length l)) = l.
Proof.
  induction l as [|a l IH]; intro d; simpl.
  - reflexivity.
  - f_equal. rewrite <- seq_shift. rewrite map_map. simpl. apply IH.
Qed.

Lemma nth_tab : forall r v x, x < r -> nth x (tab r v) 0 = v x.
Proof.
  intros r v x Hx. unfold tab.
  rewrite (nth_indep _ 0 (v 0)) by (rewrite map_length, seq_length; lia).
  rewrite map_nth. rewrite seq_nth by lia. reflexivity.
Qed.

Lemma map_inj_eq : forall (f : nat -> nat),
  (forall i j, f i = f j -> i = j) ->
  forall l l', map f l = map f l' -> l = l'.
Proof.
  intros f Hf. induction l as [|a l IH]; intros [|b l'] H; simpl in H;
    try discriminate.
  - reflexivity.
  - injection H as E1 E2. f_equal; [apply Hf; exact E1 | apply IH; exact E2].
Qed.

Fixpoint index_of (t : list nat) (l : list (list nat)) : nat :=
  match l with
  | [] => 0
  | t' :: l' => if list_eq_dec Nat.eq_dec t t' then 0 else S (index_of t l')
  end.

Lemma nth_index_of : forall (B : Type) (g : list nat -> B) d t l,
  In t l -> nth (index_of t l) (map g l) d = g t.
Proof.
  intros B g d t l. induction l as [|t' l IH]; intro H; simpl.
  - destruct H.
  - destruct (list_eq_dec Nat.eq_dec t t') as [E | N].
    + subst. reflexivity.
    + destruct H as [E | Hin]; [subst; contradiction | apply IH; exact Hin].
Qed.

Section SymNest.
  Variable r : nat.

  (* the class of a tuple: its sorted tabulation *)
  Definition scls (v : nat -> nat) : list nat := isort (tab r v).

  (* The tower's law class at H = S_r: local, and invariant under every  *)
  (* position permutation (BladeLowering's invariant_under, quantified   *)
  (* over BladeCompleteness's perm_pair).                                *)
  Definition Ksym (f : (nat -> nat) -> nat) : Prop :=
    (forall v v', (forall p, p < r -> v p = v' p) -> f v = f v') /\
    (forall s s', perm_pair r s s' -> invariant_under nat nat f s).

  (* ... which is exactly: constant on classes. *)
  Definition Kcls (f : (nat -> nat) -> nat) : Prop :=
    forall v v', scls v = scls v' -> f v = f v'.

  Lemma scls_local : forall v v',
    (forall p, p < r -> v p = v' p) -> scls v = scls v'.
  Proof.
    intros v v' H. unfold scls, tab. f_equal.
    apply map_ext_in. intros a Ha. apply in_seq in Ha. apply H. lia.
  Qed.

  Lemma scls_perm : forall s s', perm_pair r s s' ->
    forall v, scls (fun p => v (s p)) = scls v.
  Proof.
    intros s s' Hp v. unfold scls, tab.
    apply isort_respects.
    rewrite <- (map_map s v).
    apply Permutation_map. exact (map_s_perm r s s' Hp).
  Qed.

  Lemma Kcls_Ksym : forall f, Kcls f -> Ksym f.
  Proof.
    intros f H. split.
    - intros v v' Hv. apply H. apply scls_local. exact Hv.
    - intros s s' Hp v. apply H. exact (scls_perm s s' Hp v).
  Qed.

  (* The converse is where a list permutation becomes a position          *)
  (* permutation with a two-sided inverse.                                *)
  Lemma Ksym_Kcls : forall f, Ksym f -> Kcls f.
  Proof.
    intros f [Hloc Hinv] v v' E. unfold scls in E.
    assert (HP : Permutation (tab r v') (tab r v)).
    { apply perm_trans with (isort (tab r v')); [apply isort_perm |].
      rewrite <- E. apply Permutation_sym. apply isort_perm. }
    apply (Permutation_nth (tab r v') (tab r v) 0) in HP.
    cbv zeta in HP. destruct HP as (_ & g & Hfun & Hinj & Hnth).
    assert (Hlen : length (tab r v') = r)
      by (unfold tab; rewrite map_length, seq_length; reflexivity).
    rewrite Hlen in Hfun, Hinj, Hnth.
    pose proof (proj1 (bInjective_bSurjective Hfun) Hinj) as Hsurj.
    destruct (bSurjective_bBijective Hfun Hsurj) as (g' & Hfun' & Hbij).
    assert (Hpp : perm_pair r g g').
    { repeat split.
      - exact Hfun.
      - exact Hfun'.
      - intros p Hp. apply (Hbij p Hp).
      - intros p Hp. apply (Hbij p Hp). }
    (* v p = v' (g p) on [0, r) *)
    transitivity (f (fun p => v' (g p))).
    - apply Hloc. intros p Hp.
      specialize (Hnth p Hp).
      rewrite nth_tab in Hnth by exact Hp.
      rewrite nth_tab in Hnth by (apply Hfun; exact Hp).
      exact Hnth.
    - exact (Hinv g g' Hpp v').
  Qed.

  (* --- the index side: one array, injective (generic) data ------------ *)
  Variable dat : nat -> nat.

  Definition argsOf (ix : list nat) : nat -> nat := fun p => dat (nth p ix 0).

  (* argsOf is the tower's call site with one array in every position.   *)
  Lemma argsOf_is_Out : forall f ix,
    f (argsOf ix)
    = Out nat nat f nat (fun _ => 0) (fun _ i => dat i) (fun p => nth p ix 0).
  Proof. reflexivity. Qed.

  Lemma tab_argsOf : forall ix, length ix = r -> tab r (argsOf ix) = map dat ix.
  Proof.
    intros ix Hl. unfold tab, argsOf.
    rewrite <- (map_map (fun p => nth p ix 0) dat).
    rewrite <- Hl. rewrite tab_nth. reflexivity.
  Qed.

  Lemma scls_args_sorted : forall ix, length ix = r ->
    scls (argsOf (isort ix)) = scls (argsOf ix).
  Proof.
    intros ix Hl. unfold scls.
    rewrite tab_argsOf by (rewrite isort_length; exact Hl).
    rewrite tab_argsOf by exact Hl.
    apply isort_respects. apply Permutation_map.
    apply Permutation_sym. apply isort_perm.
  Qed.

  Hypothesis dat_inj : forall i j, dat i = dat j -> i = j.

  (* SEPARATION: distinct canonical cells have distinct classes. *)
  Lemma scls_args_inj : forall n ix iy,
    canonical r 0 n ix -> canonical r 0 n iy ->
    scls (argsOf ix) = scls (argsOf iy) -> ix = iy.
  Proof.
    intros n ix iy Hx Hy E. unfold scls in E.
    rewrite tab_argsOf in E by (apply (canonical_len r 0 n); exact Hx).
    rewrite tab_argsOf in E by (apply (canonical_len r 0 n); exact Hy).
    assert (HP : Permutation (map dat ix) (map dat iy)).
    { apply perm_trans with (isort (map dat ix)); [apply isort_perm |].
      rewrite E. apply Permutation_sym. apply isort_perm. }
    apply Permutation_map_inv in HP as (l3 & E3 & HP3).
    apply (map_inj_eq dat dat_inj) in E3. subst l3.
    rewrite <- (isort_canonical r 0 n ix Hx).
    rewrite <- (isort_canonical r 0 n iy Hy).
    apply isort_respects. apply Permutation_sym. exact HP3.
  Qed.

  Lemma sym_perturbable : forall x,
    perturbable (nat -> nat) (list nat) (list nat) nat scls Ksym argsOf x.
  Proof.
    intro x.
    exists (fun k v => if list_eq_dec Nat.eq_dec (scls v) (scls (argsOf x))
                       then S (k v) else k v).
    split; [| split].
    - intros k Hk. apply Kcls_Ksym. apply Ksym_Kcls in Hk.
      intros v v' E. rewrite E.
      destruct (list_eq_dec Nat.eq_dec (scls v') (scls (argsOf x)));
        [f_equal |]; apply Hk; exact E.
    - intros k v Hne.
      destruct (list_eq_dec Nat.eq_dec (scls v) (scls (argsOf x)));
        [contradiction | reflexivity].
    - intro k.
      destruct (list_eq_dec Nat.eq_dec (scls (argsOf x)) (scls (argsOf x)));
        [lia | contradiction].
  Qed.

  (* ------------------------------------------------------------------ *)
  (* T1: any program correct on the canonical cells, for every kernel in *)
  (* the law class, asks at least C(n+r-1, r) questions -- whatever it   *)
  (* is, adaptive or not, group-structured or not.                       *)
  (* ------------------------------------------------------------------ *)
  Theorem sym_nest_lower_bound :
    forall (n : nat) (O : Type) (out : O -> list nat -> nat)
           (P : prog (nat -> nat) nat O),
    (forall f, Ksym f -> forall ix, canonical r 0 n ix ->
       out (run P f) ix = f (argsOf ix)) ->
    forall f, Ksym f -> C (n + r - 1) r <= length (trace P f).
  Proof.
    intros n O out P Hcor f Hf.
    rewrite <- storage_cardinality.
    apply (orbit_lower_bound (nat -> nat) (list nat) (list nat) nat O
             scls (list_eq_dec Nat.eq_dec) Ksym argsOf out (enum r 0 n) P).
    - apply NoDup_map_inj; [apply enum_NoDup |].
      intros x y Hx Hy E.
      apply (scls_args_inj n); try (apply enum_sound; assumption). exact E.
    - intros x _. apply sym_perturbable.
    - intros k Hk x Hx. apply Hcor; [exact Hk | apply enum_sound; exact Hx].
    - exact Hf.
  Qed.

  (* T1': the same count bounds any opaque-cell store. *)
  Theorem sym_nest_storage_bound :
    forall (n : nat) (cells : list (nat -> nat)) (acc : list nat -> nat),
    (forall f, Ksym f -> forall ix, canonical r 0 n ix ->
       nth (acc ix) (map f cells) 0 = f (argsOf ix)) ->
    C (n + r - 1) r <= length cells.
  Proof.
    intros n cells acc Hread.
    rewrite <- storage_cardinality.
    apply (orbit_storage_bound (nat -> nat) (list nat) (list nat) nat
             scls (list_eq_dec Nat.eq_dec) Ksym argsOf
             (enum r 0 n) cells acc 0 (fun _ => 0)).
    - apply Kcls_Ksym. intros v v' _. reflexivity.
    - apply NoDup_map_inj; [apply enum_NoDup |].
      intros x y Hx Hy E.
      apply (scls_args_inj n); try (apply enum_sound; assumption). exact E.
    - intros x _. apply sym_perturbable.
    - intros k Hk x Hx. apply Hread; [exact Hk | apply enum_sound; exact Hx].
  Qed.
  (* ------------------------------------------------------------------ *)
  (* T2: the canonical enumeration ATTAINS the bound, and answers every  *)
  (* cell of the dense index space, not just the canonical ones: read a  *)
  (* cell by sorting its index tuple.                                    *)
  (* ------------------------------------------------------------------ *)
  Definition sym_prog (n : nat) : prog (nat -> nat) nat (list nat) :=
    ask_all (map argsOf (enum r 0 n)) [].

  Definition sym_out (n : nat) (o : list nat) (ix : list nat) : nat :=
    nth (index_of (isort ix) (enum r 0 n)) o 0.

  Theorem sym_nest_attained : forall n,
    (forall f, Ksym f -> forall ix,
       length ix = r -> (forall x, In x ix -> x < n) ->
       sym_out n (run (sym_prog n) f) ix = f (argsOf ix)) /\
    (forall f, length (trace (sym_prog n) f) = C (n + r - 1) r).
  Proof.
    intro n. split.
    - intros f Hf ix Hl Hb. unfold sym_out, sym_prog.
      rewrite ask_all_run. simpl. rewrite map_map.
      rewrite (nth_index_of nat (fun t => f (argsOf t)) 0).
      + apply (Ksym_Kcls f Hf). apply scls_args_sorted. exact Hl.
      + apply enum_complete. rewrite <- Hl. apply isort_canon. exact Hb.
    - intro f. unfold sym_prog. rewrite ask_all_trace, map_length.
      apply storage_cardinality.
  Qed.

  (* UNIFORM OPTIMALITY, full-symmetry nest: no correct program, of any  *)
  (* shape, asks fewer questions than the canonical enumeration does --  *)
  (* against any kernel in the law class.                                *)
  Corollary uniform_optimality_sym :
    forall (n : nat) (O : Type) (out : O -> list nat -> nat)
           (P : prog (nat -> nat) nat O),
    (forall f, Ksym f -> forall ix, canonical r 0 n ix ->
       out (run P f) ix = f (argsOf ix)) ->
    forall f, Ksym f ->
      length (trace (sym_prog n) f) <= length (trace P f).
  Proof.
    intros n O out P Hcor f Hf.
    rewrite (proj2 (sym_nest_attained n) f).
    exact (sym_nest_lower_bound n O out P Hcor f Hf).
  Qed.
End SymNest.

(* ===================================================================== *)
(* GENERICITY IS LOAD-BEARING.  On degenerate data a program that knows  *)
(* the data beats the orbit count: with a constant array every cell is   *)
(* the same query.  The bound is a statement about generic data (and so  *)
(* about every data-oblivious schedule, which must be right on it).      *)
(* ===================================================================== *)
Theorem nongeneric_data_beats_bound :
  exists P : prog (nat -> nat) nat nat,
    (forall (f : (nat -> nat) -> nat) (ix : list nat),
       run P f = f (argsOf (fun _ => 0) ix)) /\
    (forall f, length (trace P f) = 1) /\
    1 < C (3 + 2 - 1) 2.
Proof.
  exists (Ask (fun _ => 0) (fun a => Ret a)).
  split; [| split].
  - intros f ix. reflexivity.
  - intro f. reflexivity.
  - simpl. lia.
Qed.

(* ===================================================================== *)
(* PART E.  THE SIGNED CASE at r = 2: an antisymmetric kernel over one   *)
(* array.  A class is FREE when it can be coherently oriented; the       *)
(* diagonal cannot, and is forced to zero, so it costs nothing and is    *)
(* not stored.  The strict pairs are free and cost one query each.       *)
(* ===================================================================== *)

Section IndexOf.
  Variable X : Type.
  Hypothesis X_dec : forall x y : X, {x = y} + {x <> y}.

  Fixpoint xindex (t : X) (l : list X) : nat :=
    match l with
    | [] => 0
    | t' :: l' => if X_dec t t' then 0 else S (xindex t l')
    end.

  Lemma nth_xindex : forall (B : Type) (g : X -> B) d t l,
    In t l -> nth (xindex t l) (map g l) d = g t.
  Proof.
    intros B g d t l. induction l as [|t' l IH]; intro H; simpl.
    - destruct H.
    - destruct (X_dec t t') as [E | N].
      + subst. reflexivity.
      + destruct H as [E | Hin]; [subst; contradiction | apply IH; exact Hin].
  Qed.
End IndexOf.

Lemma C_n_1 : forall n, C n 1 = n.
Proof.
  induction n as [|n IH]; [reflexivity |].
  change (C (S n) 1) with (C n 0 + C n 1). rewrite C_zero, IH. reflexivity.
Qed.

(* strict pairs i < j < n, built so the count is a one-line induction *)
Fixpoint spairs (n : nat) : list (nat * nat) :=
  match n with
  | 0 => []
  | S n' => spairs n' ++ map (fun i => (i, n')) (seq 0 n')
  end.

Lemma spairs_length : forall n, length (spairs n) = C n 2.
Proof.
  induction n as [|n IH]; [reflexivity |].
  simpl spairs. rewrite app_length, map_length, seq_length, IH.
  change (C (S n) 2) with (C n 1 + C n 2). rewrite C_n_1. lia.
Qed.

Lemma spairs_in : forall n i j, In (i, j) (spairs n) <-> i < j < n.
Proof.
  induction n as [|n IH]; intros i j; simpl.
  - split; [intros [] | lia].
  - rewrite in_app_iff, IH, in_map_iff. split.
    + intros [H | (x & E & Hx)]; [lia |].
      injection E as E1 E2. subst. apply in_seq in Hx. lia.
    + intro H. destruct (Nat.eq_dec j n) as [E | N].
      * right. exists i. subst. split; [reflexivity | apply in_seq; lia].
      * left. lia.
Qed.

Lemma spairs_NoDup : forall n, NoDup (spairs n).
Proof.
  induction n as [|n IH]; simpl; [constructor |].
  apply NoDup_app_disjoint.
  - exact IH.
  - apply NoDup_map_inj; [apply NoDup_seq |].
    intros x y _ _ E. injection E as E. exact E.
  - intros [i j] H1 H2. apply spairs_in in H1.
    apply in_map_iff in H2 as (x & E & _). injection E as _ E2. lia.
Qed.

Section AntisymPair.
  Variable dat : nat -> nat.
  Hypothesis dat_inj : forall i j, dat i = dat j -> i = j.

  Definition acls (v : nat -> nat) : list nat := isort [v 0; v 1].

  (* BladeLowering's sign-tracked class at r = 2, neg = Z.opp *)
  Definition Kanti (f : (nat -> nat) -> Z) : Prop :=
    (forall v v', v 0 = v' 0 -> v 1 = v' 1 -> f v = f v') /\
    antiinvariant_under nat Z f Z.opp swap.

  Definition pargs (x : nat * nat) : nat -> nat :=
    fun p => dat (nth p [fst x; snd x] 0).

  (* the orientation of a value pair *)
  Definition sigma (v : nat -> nat) : Z :=
    if v 0 <? v 1 then 1%Z else if v 1 <? v 0 then (-1)%Z else 0%Z.

  Lemma acls_swap : forall v, acls (fun p => v (swap p)) = acls v.
  Proof.
    intro v. unfold acls. simpl swap.
    apply isort_respects. apply perm_swap.
  Qed.

  Lemma sigma_swap : forall v, sigma (fun p => v (swap p)) = (- sigma v)%Z.
  Proof.
    intro v. unfold sigma. simpl swap.
    destruct (Nat.ltb_spec (v 1) (v 0)), (Nat.ltb_spec (v 0) (v 1));
      try lia; reflexivity.
  Qed.

  Lemma anti_perturbable : forall x, fst x < snd x ->
    perturbable (nat -> nat) (list nat) (nat * nat) Z acls Kanti pargs x.
  Proof.
    intros x Hlt.
    exists (fun k v => (k v + if list_eq_dec Nat.eq_dec (acls v) (acls (pargs x))
                              then sigma v else 0)%Z).
    split; [| split].
    - intros k [Hloc Hanti]. split.
      + intros v v' E0 E1. unfold acls, sigma. rewrite E0, E1.
        rewrite (Hloc v v' E0 E1). reflexivity.
      + intro v. rewrite acls_swap, sigma_swap. rewrite (Hanti v).
        destruct (list_eq_dec Nat.eq_dec (acls v) (acls (pargs x))); lia.
    - intros k v Hne.
      destruct (list_eq_dec Nat.eq_dec (acls v) (acls (pargs x)));
        [contradiction | lia].
    - intro k.
      destruct (list_eq_dec Nat.eq_dec (acls (pargs x)) (acls (pargs x)));
        [| contradiction].
      unfold sigma, pargs. simpl nth.
      assert (dat (fst x) <> dat (snd x))
        by (intro E; apply dat_inj in E; lia).
      destruct (Nat.ltb_spec (dat (fst x)) (dat (snd x))); [lia |].
      destruct (Nat.ltb_spec (dat (snd x)) (dat (fst x))); lia.
  Qed.

  Lemma acls_pargs_inj : forall n x y,
    In x (spairs n) -> In y (spairs n) ->
    acls (pargs x) = acls (pargs y) -> x = y.
  Proof.
    intros n [i j] [i' j'] Hx Hy E.
    apply spairs_in in Hx. apply spairs_in in Hy.
    unfold acls, pargs in E. simpl nth in E. simpl fst in E. simpl snd in E.
    assert (HP : Permutation (map dat [i; j]) (map dat [i'; j'])).
    { simpl map.
      apply perm_trans with (isort [dat i; dat j]); [apply isort_perm |].
      rewrite E. apply Permutation_sym. apply isort_perm. }
    apply Permutation_map_inv in HP as (l3 & E3 & HP3).
    apply (map_inj_eq dat dat_inj) in E3. subst l3.
    apply isort_respects in HP3. simpl in HP3.
    destruct (Nat.leb_spec i' j'); [| lia].
    destruct (Nat.leb_spec i j); [| lia].
    injection HP3 as E1 E2. subst. reflexivity.
  Qed.

  (* T1, signed: C(n, 2) queries are forced. *)
  Theorem antisym_lower_bound :
    forall (n : nat) (O : Type) (out : O -> nat * nat -> Z)
           (P : prog (nat -> nat) Z O),
    (forall f, Kanti f -> forall x, In x (spairs n) ->
       out (run P f) x = f (pargs x)) ->
    forall f, Kanti f -> C n 2 <= length (trace P f).
  Proof.
    intros n O out P Hcor f Hf.
    rewrite <- spairs_length.
    apply (orbit_lower_bound (nat -> nat) (list nat) (nat * nat) Z O
             acls (list_eq_dec Nat.eq_dec) Kanti pargs out (spairs n) P).
    - apply NoDup_map_inj; [apply spairs_NoDup |].
      intros x y Hx Hy E. exact (acls_pargs_inj n x y Hx Hy E).
    - intros [i j] Hx. apply anti_perturbable.
      apply spairs_in in Hx. simpl. lia.
    - exact Hcor.
    - exact Hf.
  Qed.

  (* The diagonal is not free: every kernel in the class vanishes there. *)
  Theorem antisym_diagonal_forced_zero : forall f, Kanti f ->
    forall i, f (pargs (i, i)) = 0%Z.
  Proof.
    intros f [Hloc Hanti] i.
    pose proof (Hanti (pargs (i, i))) as H.
    rewrite (Hloc (fun p => pargs (i, i) (swap p)) (pargs (i, i))) in H
      by reflexivity.
    lia.
  Qed.

  (* T2, signed: the strict enumeration attains it and serves every cell *)
  (* of the dense square -- mirrored cells negated, the diagonal zero.   *)
  Definition pair_dec : forall x y : nat * nat, {x = y} + {x <> y}.
  Proof. decide equality; apply Nat.eq_dec. Defined.

  Definition anti_prog (n : nat) : prog (nat -> nat) Z (list Z) :=
    ask_all (map pargs (spairs n)) [].

  Definition anti_out (n : nat) (o : list Z) (x : nat * nat) : Z :=
    if fst x <? snd x
    then nth (xindex _ pair_dec x (spairs n)) o 0%Z
    else if snd x <? fst x
         then (- nth (xindex _ pair_dec (snd x, fst x) (spairs n)) o 0)%Z
         else 0%Z.

  Theorem antisym_attained : forall n,
    (forall f, Kanti f -> forall i j, i < n -> j < n ->
       anti_out n (run (anti_prog n) f) (i, j) = f (pargs (i, j))) /\
    (forall f, length (trace (anti_prog n) f) = C n 2).
  Proof.
    intro n. split.
    - intros f Hf i j Hi Hj. unfold anti_out, anti_prog.
      rewrite ask_all_run. simpl rev. simpl app. rewrite map_map.
      simpl fst. simpl snd.
      destruct (Nat.ltb_spec i j).
      + rewrite (nth_xindex _ pair_dec Z (fun t => f (pargs t)) 0%Z).
        * reflexivity.
        * apply spairs_in. lia.
      + destruct (Nat.ltb_spec j i).
        * rewrite (nth_xindex _ pair_dec Z (fun t => f (pargs t)) 0%Z)
            by (apply spairs_in; lia).
          destruct Hf as [Hloc Hanti].
          pose proof (Hanti (pargs (i, j))) as HA.
          rewrite (Hloc (fun p => pargs (i, j) (swap p)) (pargs (j, i))) in HA
            by reflexivity.
          lia.
        * assert (i = j) by lia. subst j.
          symmetry. apply antisym_diagonal_forced_zero. exact Hf.
    - intro f. unfold anti_prog. rewrite ask_all_trace, map_length.
      apply spairs_length.
  Qed.
End AntisymPair.

(* ===================================================================== *)
(* PART F.  H-AND-STAB PROPER: a fully symmetric kernel over SEVERAL     *)
(* arrays.  The kernel's law is all of S_R; the binding repeats array j  *)
(* in a block of r_j positions.  H is everything, so G = H /\ Stab is    *)
(* the Young subgroup that permutes inside the blocks -- exactly the     *)
(* identity groups of BladeMixedRadix's shapes.  Same law class as Part  *)
(* D, same abstract bound; what is new is SEPARATION across blocks:      *)
(* with generic data a value remembers which array it came from, so a    *)
(* permutation of the whole value tuple restricts to one per block.      *)
(* ===================================================================== *)

Lemma perm_filter : forall (p : nat -> bool) l l',
  Permutation l l' -> Permutation (filter p l) (filter p l').
Proof.
  intros p l l' H. induction H; simpl.
  - apply perm_nil.
  - destruct (p x); [apply perm_skip |]; exact IHPermutation.
  - destruct (p x), (p y);
      try apply perm_swap; apply Permutation_refl.
  - eapply perm_trans; eassumption.
Qed.

Lemma filter_all : forall (p : nat -> bool) l,
  (forall x, In x l -> p x = true) -> filter p l = l.
Proof.
  intros p l. induction l as [|a l IH]; intro H; simpl; [reflexivity |].
  rewrite (H a) by (left; reflexivity).
  f_equal. apply IH. intros x Hx. apply H. right. exact Hx.
Qed.

Lemma filter_none : forall (p : nat -> bool) l,
  (forall x, In x l -> p x = false) -> filter p l = [].
Proof.
  intros p l. induction l as [|a l IH]; intro H; simpl; [reflexivity |].
  rewrite (H a) by (left; reflexivity).
  apply IH. intros x Hx. apply H. right. exact Hx.
Qed.

Lemma canonical_ub : forall r l u t,
  canonical r l u t -> forall x, In x t -> x < u.
Proof.
  induction r as [|r IH]; intros l u t Hc x Hx;
    destruct t as [|i t']; simpl in Hc; try contradiction.
  destruct Hc as [Hb Hc]. destruct Hx as [E | Hin].
  - subst. lia.
  - exact (IH _ _ _ Hc _ Hin).
Qed.

Lemma canonical_weaken : forall r l l0 u t,
  canonical r l u t -> l0 <= l -> canonical r l0 u t.
Proof.
  intros r l l0 u t Hc Hl. destruct r as [|r]; destruct t as [|i t'];
    simpl in *; try contradiction; try exact I.
  destruct Hc as [Hb Hc]. split; [lia | exact Hc].
Qed.

Lemma isort_canon_gen : forall l0 u ix,
  (forall x, In x ix -> l0 <= x < u) ->
  canonical (length ix) l0 u (isort ix).
Proof.
  intros l0 u ix. induction ix as [|a ix IH]; intro H; simpl.
  - exact I.
  - assert (Ha : l0 <= a < u) by (apply H; left; reflexivity).
    assert (Hc : canonical (length ix) l0 u (isort ix))
      by (apply IH; intros x Hx; apply H; right; exact Hx).
    pose proof (insert_canon _ _ _ _ a Hc (proj2 Ha)) as HH.
    rewrite Nat.min_l in HH by lia. exact HH.
Qed.

(* two canonical tuples that are permutations of each other are equal *)
Lemma canon_perm_eq : forall r l u t t',
  canonical r l u t -> canonical r l u t' -> Permutation t t' -> t = t'.
Proof.
  intros r l u t t' Hc Hc' HP.
  rewrite <- (isort_canonical r l u t Hc).
  rewrite <- (isort_canonical r l u t' Hc').
  apply isort_respects. exact HP.
Qed.

Lemma perm_map_inj : forall (f : nat -> nat),
  (forall i j, f i = f j -> i = j) ->
  forall l l', Permutation (map f l) (map f l') -> Permutation l l'.
Proof.
  intros f Hf l l' HP.
  apply Permutation_map_inv in HP as (l3 & E3 & HP3).
  apply (map_inj_eq f Hf) in E3. subst l3.
  apply Permutation_sym. exact HP3.
Qed.

Section YoungNest.
  Variable D : nat -> nat -> nat.       (* array identity -> index -> value *)
  Variable grp : nat -> nat.            (* generic data: a value names its array *)
  Hypothesis grp_D : forall a i, grp (D a i) = a.
  Hypothesis D_inj : forall a i j, D a i = D a j -> i = j.

  Definition arity (s : Shape) : nat := lsum (map ix_r s).

  (* the value tuple at a cell: block j reads array j *)
  Fixpoint svals (j : nat) (s : Shape) (t : list nat) : list nat :=
    match s with
    | [] => []
    | c :: s' => map (D j) (firstn (ix_r c) t)
                 ++ svals (S j) s' (skipn (ix_r c) t)
    end.

  Definition shargs (s : Shape) (t : list nat) : nat -> nat :=
    fun p => nth p (svals 0 s t) 0.

  (* a dense cell: block lengths and per-block bounds, nothing sorted *)
  Fixpoint sdense (s : Shape) (t : list nat) : Prop :=
    match s with
    | [] => True
    | c :: s' => length (firstn (ix_r c) t) = ix_r c /\
                 (forall x, In x (firstn (ix_r c) t) -> ix_l c <= x < ix_u c) /\
                 sdense s' (skipn (ix_r c) t)
    end.

  Lemma svals_cons : forall j c s t1 t2, length t1 = ix_r c ->
    svals j (c :: s) (t1 ++ t2) = map (D j) t1 ++ svals (S j) s t2.
  Proof.
    intros j c s t1 t2 Hl. simpl. rewrite <- Hl.
    rewrite firstn_exact, skipn_exact. reflexivity.
  Qed.

  Lemma svals_grp : forall s j t x, In x (svals j s t) -> j <= grp x.
  Proof.
    induction s as [|c s IH]; intros j t x Hx; simpl in Hx; [destruct Hx |].
    apply in_app_iff in Hx as [Hx | Hx].
    - apply in_map_iff in Hx as (i & E & _). subst x. rewrite grp_D. lia.
    - specialize (IH _ _ _ Hx). lia.
  Qed.

  Lemma svals_length : forall s j t, sdense s t ->
    length (svals j s t) = arity s.
  Proof.
    induction s as [|c s IH]; intros j t Hd; simpl; [reflexivity |].
    destruct Hd as (Hl & _ & Hd).
    rewrite app_length, map_length, Hl. unfold arity in IH.
    rewrite (IH _ _ Hd). reflexivity.
  Qed.

  Lemma enumShape_dense : forall s t, In t (enumShape s) -> sdense s t.
  Proof.
    induction s as [|c s IH]; intros t Ht; [exact I |].
    apply enumShape_cons_inv in Ht as (t1 & t2 & E & H1 & H2). subst t.
    unfold enumIx in H1.
    pose proof (enum_elem_length _ _ _ _ H1) as Hl.
    pose proof (enum_sound _ _ _ _ H1) as Hc.
    simpl. rewrite <- Hl. rewrite firstn_exact, skipn_exact.
    repeat split.
    - exact (canonical_lb _ _ _ _ Hc x H).
    - exact (canonical_ub _ _ _ _ Hc x H).
    - apply IH. exact H2.
  Qed.

  (* SEPARATION across blocks. *)
  Lemma svals_separate : forall s j t t',
    In t (enumShape s) -> In t' (enumShape s) ->
    Permutation (svals j s t) (svals j s t') -> t = t'.
  Proof.
    induction s as [|c s IH]; intros j t t' Ht Ht' HP.
    - simpl in Ht, Ht'. destruct Ht as [E | []]. destruct Ht' as [E' | []].
      subst. reflexivity.
    - apply enumShape_cons_inv in Ht as (t1 & t2 & E & H1 & H2).
      apply enumShape_cons_inv in Ht' as (t1' & t2' & E' & H1' & H2').
      subst t t'. unfold enumIx in H1, H1'.
      rewrite svals_cons in HP by (eapply enum_elem_length; exact H1).
      rewrite svals_cons in HP by (eapply enum_elem_length; exact H1').
      assert (Hin : forall (tt : list nat) x, In x (map (D j) tt) ->
                (grp x =? j) = true).
      { intros tt x Hx. apply in_map_iff in Hx as (i & Ei & _). subst x.
        rewrite grp_D. apply Nat.eqb_refl. }
      assert (Hout : forall tt x, In x (svals (S j) s tt) ->
                (grp x =? j) = false).
      { intros tt x Hx. apply svals_grp in Hx. apply Nat.eqb_neq. lia. }
      f_equal.
      + (* this block *)
        pose proof (perm_filter (fun x => grp x =? j) _ _ HP) as HF.
        rewrite !filter_app in HF.
        rewrite (filter_all _ _ (Hin t1)), (filter_all _ _ (Hin t1')) in HF.
        rewrite (filter_none _ _ (Hout t2)), (filter_none _ _ (Hout t2')) in HF.
        rewrite !app_nil_r in HF.
        apply (perm_map_inj (D j) (D_inj j)) in HF.
        exact (canon_perm_eq _ _ _ _ _
                 (enum_sound _ _ _ _ H1) (enum_sound _ _ _ _ H1') HF).
      + (* the remaining blocks *)
        pose proof (perm_filter (fun x => negb (grp x =? j)) _ _ HP) as HF.
        rewrite !filter_app in HF.
        rewrite (filter_none _ (map (D j) t1)),
                (filter_none _ (map (D j) t1')) in HF
          by (intros x Hx; rewrite (Hin _ x Hx); reflexivity).
        rewrite (filter_all _ (svals (S j) s t2)),
                (filter_all _ (svals (S j) s t2')) in HF
          by (intros x Hx; rewrite (Hout _ x Hx); reflexivity).
        simpl in HF. exact (IH (S j) t2 t2' H2 H2' HF).
  Qed.

  Lemma scls_shargs : forall s t, sdense s t ->
    scls (arity s) (shargs s t) = isort (svals 0 s t).
  Proof.
    intros s t Hd. unfold scls, tab, shargs.
    rewrite <- (svals_length s 0 t Hd). rewrite tab_nth. reflexivity.
  Qed.

  Lemma scls_shargs_inj : forall s t t',
    In t (enumShape s) -> In t' (enumShape s) ->
    scls (arity s) (shargs s t) = scls (arity s) (shargs s t') -> t = t'.
  Proof.
    intros s t t' Ht Ht' E.
    rewrite !scls_shargs in E by (apply enumShape_dense; assumption).
    apply (svals_separate s 0 t t' Ht Ht').
    apply perm_trans with (isort (svals 0 s t)); [apply isort_perm |].
    rewrite E. apply Permutation_sym. apply isort_perm.
  Qed.

  Lemma young_perturbable : forall s x,
    perturbable (nat -> nat) (list nat) (list nat) nat
      (scls (arity s)) (Ksym (arity s)) (shargs s) x.
  Proof.
    intros s x.
    exists (fun k v => if list_eq_dec Nat.eq_dec (scls (arity s) v)
                                       (scls (arity s) (shargs s x))
                       then S (k v) else k v).
    split; [| split].
    - intros k Hk. apply Kcls_Ksym. apply Ksym_Kcls in Hk.
      intros v v' E. rewrite E.
      destruct (list_eq_dec Nat.eq_dec (scls (arity s) v')
                                       (scls (arity s) (shargs s x)));
        [f_equal |]; apply Hk; exact E.
    - intros k v Hne.
      destruct (list_eq_dec Nat.eq_dec (scls (arity s) v)
                                       (scls (arity s) (shargs s x)));
        [contradiction | reflexivity].
    - intro k.
      destruct (list_eq_dec Nat.eq_dec (scls (arity s) (shargs s x))
                                       (scls (arity s) (shargs s x)));
        [lia | contradiction].
  Qed.

  (* T1 at G = S_R /\ Stab: the product of the per-block multiset counts *)
  (* is forced.                                                          *)
  Theorem young_nest_lower_bound :
    forall (s : Shape) (O : Type) (out : O -> list nat -> nat)
           (P : prog (nat -> nat) nat O),
    (forall f, Ksym (arity s) f -> forall t, In t (enumShape s) ->
       out (run P f) t = f (shargs s t)) ->
    forall f, Ksym (arity s) f -> shapeCard s <= length (trace P f).
  Proof.
    intros s O out P Hcor f Hf.
    rewrite shapeCard_is_length.
    apply (orbit_lower_bound (nat -> nat) (list nat) (list nat) nat O
             (scls (arity s)) (list_eq_dec Nat.eq_dec) (Ksym (arity s))
             (shargs s) out (enumShape s) P).
    - apply NoDup_map_inj; [apply enumShape_NoDup |].
      intros x y Hx Hy E. exact (scls_shargs_inj s x y Hx Hy E).
    - intros x _. apply young_perturbable.
    - exact Hcor.
    - exact Hf.
  Qed.

  (* --- T2 at G = S_R /\ Stab: canonicalize block by block ------------- *)

  Fixpoint scanon (s : Shape) (t : list nat) : list nat :=
    match s with
    | [] => []
    | c :: s' => isort (firstn (ix_r c) t) ++ scanon s' (skipn (ix_r c) t)
    end.

  Lemma scanon_in : forall s t, sdense s t -> In (scanon s t) (enumShape s).
  Proof.
    induction s as [|c s IH]; intros t Hd; simpl scanon.
    - left. reflexivity.
    - destruct Hd as (Hl & Hb & Hd).
      apply enumShape_cons_intro; [| apply IH; exact Hd].
      unfold enumIx. apply enum_complete.
      rewrite <- Hl at 1. apply isort_canon_gen. exact Hb.
  Qed.

  Lemma scanon_vals : forall s j t, sdense s t ->
    Permutation (svals j s (scanon s t)) (svals j s t).
  Proof.
    induction s as [|c s IH]; intros j t Hd.
    - apply perm_nil.
    - destruct Hd as (Hl & Hb & Hd).
      simpl scanon.
      rewrite svals_cons by (rewrite isort_length; exact Hl).
      simpl svals. apply Permutation_app.
      + apply Permutation_map. apply Permutation_sym. apply isort_perm.
      + apply IH. exact Hd.
  Qed.

  Definition young_prog (s : Shape) : prog (nat -> nat) nat (list nat) :=
    ask_all (map (shargs s) (enumShape s)) [].

  Definition young_out (s : Shape) (o : list nat) (t : list nat) : nat :=
    nth (index_of (scanon s t) (enumShape s)) o 0.

  Theorem young_nest_attained : forall s,
    (forall f, Ksym (arity s) f -> forall t, sdense s t ->
       young_out s (run (young_prog s) f) t = f (shargs s t)) /\
    (forall f, length (trace (young_prog s) f) = shapeCard s).
  Proof.
    intro s. split.
    - intros f Hf t Hd. unfold young_out, young_prog.
      rewrite ask_all_run. simpl. rewrite map_map.
      rewrite (nth_index_of nat (fun t => f (shargs s t)) 0)
        by (apply scanon_in; exact Hd).
      apply (Ksym_Kcls (arity s) f Hf).
      rewrite scls_shargs by (apply enumShape_dense; apply scanon_in; exact Hd).
      rewrite scls_shargs by exact Hd.
      apply isort_respects. apply scanon_vals. exact Hd.
    - intro f. unfold young_prog. rewrite ask_all_trace, map_length.
      symmetry. apply shapeCard_is_length.
  Qed.

  (* UNIFORM OPTIMALITY at G = H /\ Stab. *)
  Corollary uniform_optimality_young :
    forall (s : Shape) (O : Type) (out : O -> list nat -> nat)
           (P : prog (nat -> nat) nat O),
    (forall f, Ksym (arity s) f -> forall t, In t (enumShape s) ->
       out (run P f) t = f (shargs s t)) ->
    forall f, Ksym (arity s) f ->
      length (trace (young_prog s) f) <= length (trace P f).
  Proof.
    intros s O out P Hcor f Hf.
    rewrite (proj2 (young_nest_attained s) f).
    exact (young_nest_lower_bound s O out P Hcor f Hf).
  Qed.
  (* --- the shape IS a tower binding ----------------------------------- *)
  (* sgrp reads off which array a position holds; with it, shargs is     *)
  (* BladeLowering's Out at binding B = sgrp, so the class quantified    *)
  (* over above is the framework's H at H = S_R, and the cells counted   *)
  (* are the orbits of H /\ Stab(B).                                     *)
  Fixpoint sgrp (j : nat) (s : Shape) (p : nat) : nat :=
    match s with
    | [] => j
    | c :: s' => if p <? ix_r c then j else sgrp (S j) s' (p - ix_r c)
    end.

  Lemma svals_nth : forall s j t p, sdense s t -> p < arity s ->
    nth p (svals j s t) 0 = D (sgrp j s p) (nth p t 0).
  Proof.
    induction s as [|c s IH]; intros j t p Hd Hp.
    - unfold arity in Hp. simpl in Hp. lia.
    - destruct Hd as (Hl & _ & Hd). cbn [svals sgrp].
      assert (Et : t = firstn (ix_r c) t ++ skipn (ix_r c) t)
        by (symmetry; apply firstn_skipn).
      remember (firstn (ix_r c) t) as F eqn:HF.
      remember (skipn (ix_r c) t) as K eqn:HK.
      rewrite Et. clear Et HF HK.
      unfold arity in Hp. simpl in Hp.
      destruct (Nat.ltb_spec p (ix_r c)).
      + rewrite app_nth1 by (rewrite map_length; lia).
        rewrite app_nth1 by lia.
        rewrite (nth_indep _ 0 (D j 0)) by (rewrite map_length; lia).
        apply map_nth.
      + rewrite app_nth2 by (rewrite map_length; lia).
        rewrite app_nth2 by lia.
        rewrite map_length, Hl.
        apply IH; [exact Hd | unfold arity; lia].
  Qed.

  Theorem shargs_is_Out : forall s t f, Ksym (arity s) f -> sdense s t ->
    f (shargs s t) = Out nat nat f nat (sgrp 0 s) D (fun p => nth p t 0).
  Proof.
    intros s t f [Hloc _] Hd. unfold Out. apply Hloc.
    intros p Hp. unfold shargs. apply svals_nth; assumption.
  Qed.
End YoungNest.

(* Generic data exists: Cantor pairing tags every value with its array.  *)
(* So Part F's hypotheses are dischargeable and the statement is closed. *)
Lemma to_nat_inj : forall p p', to_nat p = to_nat p' -> p = p'.
Proof.
  intros p p' E. rewrite <- (cancel_of_to p), <- (cancel_of_to p'), E.
  reflexivity.
Qed.

Definition cD (a i : nat) : nat := to_nat (a, i).
Definition cgrp (v : nat) : nat := fst (of_nat v).

Lemma cgrp_cD : forall a i, cgrp (cD a i) = a.
Proof. intros a i. unfold cgrp, cD. rewrite cancel_of_to. reflexivity. Qed.

Lemma cD_inj : forall a i j, cD a i = cD a j -> i = j.
Proof. intros a i j E. unfold cD in E. apply to_nat_inj in E. congruence. Qed.

Theorem uniform_optimality_young_nat :
  forall (s : Shape) (O : Type) (out : O -> list nat -> nat)
         (P : prog (nat -> nat) nat O),
  (forall f, Ksym (arity s) f -> forall t, In t (enumShape s) ->
     out (run P f) t = f (shargs cD s t)) ->
  forall f, Ksym (arity s) f ->
    shapeCard s <= length (trace P f) /\
    length (trace (young_prog cD s) f) = shapeCard s.
Proof.
  intros s O out P Hcor f Hf. split.
  - exact (young_nest_lower_bound cD cgrp cgrp_cD cD_inj s O out P Hcor f Hf).
  - exact (proj2 (young_nest_attained cD s) f).
Qed.
