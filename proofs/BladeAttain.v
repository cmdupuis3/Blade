(* ===================================================================== *)
(* BladeAttain.v -- THE ORBIT BOUND AT PROPER SUBGROUPS: block           *)
(* licences, the meet, and the sign character at every r                 *)
(* (docs/plans/plan-one-model-surface-calculus.md, item 4, partial).     *)
(*                                                                       *)
(* BladeOptimal proves the orbit bound against every program             *)
(* (orbit_lower_bound, perturbable classes) and attains it at H = S_R    *)
(* for every binding and at the sign character for r = 2.  This file     *)
(* attains it at PROPER subgroups H < S_R (a kernel commutative only     *)
(* inside declared blocks, composed with the cut a binding makes) and    *)
(* at the sign character for EVERY r, over one array.  The machinery is  *)
(* BladeOptimal's (prog/run/trace, ask_all, isort, scanon, svals, sgrp,  *)
(* cD), and the attaining program is object_prog over the meet shape.    *)
(*                                                                       *)
(* PART A.  BLOCK KERNELS AND THE MEET.                                  *)
(*   Klab R lab               the law class of a position labelling:     *)
(*                            local, invariant under every position      *)
(*                            permutation keeping each position in its   *)
(*                            block (H = the Young subgroup of lab)      *)
(*   Klab_Klcls, Klcls_Klab   equal to: constant on lcls, the sorted     *)
(*                            LABEL-TAGGED tabulation (Cantor tags turn  *)
(*                            a list Permutation into a block-preserving *)
(*                            position permutation)                      *)
(*   Klab_const_is_Ksym,      one block is BladeOptimal's Ksym;          *)
(*   Kblock_single_is_Ksym    Kblock bs = Klab with blocks of sizes bs   *)
(*   fb_in_Kblock,            a member: (v0 + v1) * 1000 + v2 is in      *)
(*   fb_not_Ksym              Kblock [2; 1] and not in Ksym 3            *)
(*   svals_sep_gen            separation with no global decoder: data    *)
(*                            injective per record, disjoint across      *)
(*   meet_lower_bound,        THE MEET: shape s, record j reading array  *)
(*   meet_storage_bound,      arr j inside kernel block kb j; when the   *)
(*   meet_attained,           pair (kb j, arr j) names the record        *)
(*   uniform_optimality_meet  (meet_inj), every program correct on the   *)
(*                            canonical cells for every kernel in the    *)
(*                            class asks shapeCard s questions; the      *)
(*                            object's program over s                    *)
(*                            (meet_prog_is_object_prog) asks exactly    *)
(*                            that many, serving every dense cell by     *)
(*                            scanon (attainment needs no meet_inj)      *)
(*   block_lower_bound,       the KERNEL CUT alone: one array in every   *)
(*   block_attained,          position, kernel blocks bs; forced count   *)
(*   uniform_optimality_      prod_j C(n + b_j - 1, b_j).  The data      *)
(*     block(_nat)            cannot separate the blocks; the            *)
(*                            declaration does                           *)
(*   uniform_optimality_      the meet CLOSED with generic data (cD),    *)
(*     subgroup               for every s, kb, arr with meet_inj         *)
(*   meet_full_kernel_is_     kb constant: the data cut alone; the call  *)
(*     Ksym,                  site is shargs, so this is the Young nest  *)
(*   margs_id_is_shargs       of BladeOptimal                            *)
(*   meet_ex_law,             both cuts at once: kernel blocks [3; 2]    *)
(*   meet_ex_forced           bound to (A, A, B | B, B) at n = 4; 400    *)
(*                            questions forced and attained              *)
(*   out_shape_is_meet,       BladeSurface agrees, ABOUT THE TYPING      *)
(*   surface_comm_optimal     RULE: under comm, out_shape is the meet    *)
(*                            with kb constant and record j reading      *)
(*                            identity group j, so its card is forced    *)
(*                            and attained -- when the rle groups have   *)
(*                            pairwise distinct names                    *)
(*   surface_data_bridge,     ... and about the DATA: with Cantor data   *)
(*   surface_den_is_call_site in the environment, apply_kernel (hence    *)
(*                            den of a well-typed L <@> f) is the kernel *)
(*                            at the call site the bound quantifies over *)
(*   surface_nocomm_optimal   without comm, H is trivial and the dense   *)
(*                            shape is forced (and attained) for all     *)
(*                            local kernels: nothing CAN be quotiented   *)
(*                            undeclared                                 *)
(*   aba_beats_surface_rule,  the boundary: method_for(A, B, A) with a   *)
(*   aba_orbit_count_forced,  comm kernel is stored dense (n^3) by the   *)
(*   aba_gap_strict,          neighbour rule (formalism 8.3, 11.1), but  *)
(*   aba_args_is_call_site    a program reordering to the meet [2; 1] is *)
(*                            correct on every dense cell with           *)
(*                            n * C(n + 1, 2) questions, that count is   *)
(*                            forced, and it is strictly less for n >= 2 *)
(*   pins                     blocks [2; 1] at n = 4: 40 cells; [3]: 20; *)
(*                            [1; 1; 1]: 64; meet [2; 1; 2]: 400         *)
(*                                                                       *)
(* PART B.  THE SIGNED BOUND AT GENERAL r (one array).                   *)
(*   Kalt r                   local, and anti-invariant (Z.opp) under    *)
(*                            every ADJACENT transposition sw i with     *)
(*                            i + 1 < r; nothing else is assumed         *)
(*   Kalt_sign_character      ... which is the full sign character:      *)
(*                            f (v o s) = (-1)^inv(s) * f v for EVERY    *)
(*                            position permutation s (perm_pair)         *)
(*   alt_vanishes             every kernel in the class is 0 on any      *)
(*                            tuple with two equal entries at ANY        *)
(*                            positions a < b < r (walk b down by        *)
(*                            adjacent swaps)                            *)
(*   antisym_lower_bound_r,   the strict tuples (BladeArrow's antisym    *)
(*   antisym_storage_bound_r  arrow, strict n r, C(n, r) of them by      *)
(*                            strict_length) are free classes: perturb   *)
(*                            by the inversion sign sgn, which flips     *)
(*                            under every adjacent swap (sgn_swap)       *)
(*   alt_sort_sign            f(l) = (-1)^inv(l) * f(isort l): insertion *)
(*                            sort IS a sequence of adjacent swaps and   *)
(*                            its swap count is inv (isort_sw_inv)       *)
(*   antisym_attained_r,      the strict enumeration attains C(n, r)     *)
(*   uniform_optimality_      and serves every dense cell: sort, look    *)
(*     alt(_nat)              up, multiply by the inversion parity; a    *)
(*                            repeated index answers 0 (alt_vanishes).   *)
(*                            The duplicate test is the transparent      *)
(*                            nodupb, so the read computes               *)
(*   Kalt_2_is_Kanti          at r = 2 this is BladeOptimal's Kanti      *)
(*   vdm_in_Kalt,             the Vandermonde product is in Kalt 3; its  *)
(*   alt_read_pin             signed read computes on non-monotone data  *)
(*                            for an odd order, an even order and a      *)
(*                            repeated index (vm_compute)                *)
(*   pins                     C 5 3 = 10 = length (strict 5 3)           *)
(*                                                                       *)
(* PART C (prose).  Arity above two INSIDE pipelines.  The schema        *)
(* formula W = sum_v omega(G_v, X_v) needs, per node, the one-nest       *)
(* orbit count of that node's licence group on its own index space,      *)
(* and attainment of the whole by the per-node programs.  The first is   *)
(* Part A at any arity (the meet of declared blocks and the binding);    *)
(* the second is the pipeline attainment of item 3 (built separately,    *)
(* not imported).  BladeOrbitWork's FREE-model pipeline bound is proved  *)
(* for unary and binary symbols only; its extension to symbols of        *)
(* arity > 2 is not attempted (a term-model statement about shared       *)
(* subterms, independent of the per-node count proved here).  Folds      *)
(* are out: sharing partial sums across OVERLAPPING fibres is Ensemble   *)
(* Computation, NP-complete (BladeOrbitWork header, plan-uniform-        *)
(* optimality 5.3); rectangular folds are that plan's T3.                *)
(*                                                                       *)
(* Scope, stated once.  Covered: Young licences whose H /\ Stab          *)
(* classes are CONTIGUOUS runs of positions (meet_inj: one record per    *)
(* class -- kernel blocks need not be contiguous when the arrays cut     *)
(* them, e.g. comm(a, c) over (A, B, C)); one split instance,            *)
(* (A, B, A) under a fully comm kernel (aba_orbit_count_forced); the     *)
(* full sign character over one array.  NOT covered: general split       *)
(* classes (e.g. comm(a, c) over (A, B, A)), which need a position-      *)
(* permuted layout; signed kernels over several arrays; mixed blocks     *)
(* (antisymmetric in some, symmetric in others); Hermitian conjugation   *)
(* as the character; pipelines with arity > 2 (Part C); folds.  Values   *)
(* are nat (Z for signed outputs); D2 leaves value equivalences to       *)
(* item 6.  Cost is kernel evaluations and cells, not time.              *)
(*                                                                       *)
(* Imports BladeDMWF, BladeBinomial, BladeMixedRadix, BladeShape,        *)
(* BladeCompute, BladeLowering, BladeCompleteness, BladeOptimal,         *)
(* BladeArrow, BladeObject, BladeSurface.  Rocq 9.0, stdlib.             *)
(* ===================================================================== *)

From Blade Require Import BladeDMWF BladeBinomial BladeMixedRadix BladeShape
                          BladeCompute BladeLowering BladeCompleteness
                          BladeOptimal BladeArrow BladeObject BladeSurface.
From Stdlib Require Import List Arith Lia Permutation FinFun ZArith Cantor Bool.
Import ListNotations.

(* ===================================================================== *)
(* PART A.  A KERNEL COMMUTATIVE ONLY IN DECLARED BLOCKS.                *)
(* ===================================================================== *)

(* --- the law class of a position labelling --------------------------- *)
(* lab p names the declared block of position p.  The licence group H is *)
(* the stabilizer of the labelling: every position permutation that      *)
(* keeps each position inside its block.                                 *)

Definition lab_pres (R : nat) (lab : nat -> nat) (s : nat -> nat) : Prop :=
  forall p, p < R -> lab (s p) = lab p.

Definition Klab (R : nat) (lab : nat -> nat) (f : (nat -> nat) -> nat) : Prop :=
  (forall v v', (forall p, p < R -> v p = v' p) -> f v = f v') /\
  (forall s s', perm_pair R s s' -> lab_pres R lab s ->
     invariant_under nat nat f s).

(* the class of a value tuple: its LABEL-TAGGED tabulation, sorted *)
Definition ltab (R : nat) (lab : nat -> nat) (v : nat -> nat) : list nat :=
  tab R (fun p => cD (lab p) (v p)).

Definition lcls (R : nat) (lab : nat -> nat) (v : nat -> nat) : list nat :=
  isort (ltab R lab v).

Definition Klcls (R : nat) (lab : nat -> nat) (f : (nat -> nat) -> nat) : Prop :=
  forall v v', lcls R lab v = lcls R lab v' -> f v = f v'.

Lemma cD_inj2 : forall a i b k, cD a i = cD b k -> a = b /\ i = k.
Proof.
  intros a i b k E. unfold cD in E. apply to_nat_inj in E.
  injection E as E1 E2. split; assumption.
Qed.

Lemma lcls_local : forall R lab v v',
  (forall p, p < R -> v p = v' p) -> lcls R lab v = lcls R lab v'.
Proof.
  intros R lab v v' H. unfold lcls, ltab, tab. f_equal.
  apply map_ext_in. intros a Ha. apply in_seq in Ha. rewrite H by lia. reflexivity.
Qed.

Lemma lcls_perm : forall R lab s s', perm_pair R s s' -> lab_pres R lab s ->
  forall v, lcls R lab (fun p => v (s p)) = lcls R lab v.
Proof.
  intros R lab s s' Hp Hl v. unfold lcls, ltab, tab.
  apply isort_respects.
  transitivity (map (fun p => cD (lab (s p)) (v (s p))) (seq 0 R)).
  { apply Permutation_refl'. apply map_ext_in. intros a Ha.
    apply in_seq in Ha. rewrite (Hl a) by lia. reflexivity. }
  rewrite <- (map_map s (fun q => cD (lab q) (v q))).
  apply Permutation_map. exact (map_s_perm R s s' Hp).
Qed.

Lemma Klcls_Klab : forall R lab f, Klcls R lab f -> Klab R lab f.
Proof.
  intros R lab f H. split.
  - intros v v' Hv. apply H. apply lcls_local. exact Hv.
  - intros s s' Hp Hl v. apply H. exact (lcls_perm R lab s s' Hp Hl v).
Qed.

(* The converse: a Permutation of tagged tabulations is a position       *)
(* permutation that, because the tags are injective, keeps every         *)
(* position inside its block.                                            *)
Lemma Klab_Klcls : forall R lab f, Klab R lab f -> Klcls R lab f.
Proof.
  intros R lab f [Hloc Hinv] v v' E. unfold lcls in E.
  assert (HP : Permutation (ltab R lab v') (ltab R lab v)).
  { apply perm_trans with (isort (ltab R lab v')); [apply isort_perm |].
    rewrite <- E. apply Permutation_sym. apply isort_perm. }
  apply (Permutation_nth (ltab R lab v') (ltab R lab v) 0) in HP.
  cbv zeta in HP. destruct HP as (_ & g & Hfun & Hinj & Hnth).
  assert (Hlen : length (ltab R lab v') = R)
    by (unfold ltab, tab; rewrite length_map, length_seq; reflexivity).
  rewrite Hlen in Hfun, Hinj, Hnth.
  pose proof (proj1 (bInjective_bSurjective Hfun) Hinj) as Hsurj.
  destruct (bSurjective_bBijective Hfun Hsurj) as (g' & Hfun' & Hbij).
  assert (Hpp : perm_pair R g g').
  { repeat split.
    - exact Hfun.
    - exact Hfun'.
    - intros p Hp. apply (Hbij p Hp).
    - intros p Hp. apply (Hbij p Hp). }
  assert (Hpt : forall p, p < R -> lab (g p) = lab p /\ v p = v' (g p)).
  { intros p Hp. specialize (Hnth p Hp). unfold ltab in Hnth.
    rewrite nth_tab in Hnth by exact Hp.
    rewrite nth_tab in Hnth by (apply Hfun; exact Hp).
    apply cD_inj2 in Hnth as [E1 E2]. split; [symmetry; exact E1 | exact E2]. }
  transitivity (f (fun p => v' (g p))).
  - apply Hloc. intros p Hp. exact (proj2 (Hpt p Hp)).
  - apply (Hinv g g' Hpp). intros p Hp. exact (proj1 (Hpt p Hp)).
Qed.

Lemma Klab_ext : forall R lab lab' f,
  (forall p, p < R -> lab p = lab' p) -> Klab R lab f -> Klab R lab' f.
Proof.
  intros R lab lab' f E [Hloc Hinv]. split; [exact Hloc |].
  intros s s' Hp Hl. apply (Hinv s s' Hp). intros p Hq.
  destruct Hp as (Hs & _). rewrite (E (s p)) by (apply Hs; exact Hq).
  rewrite (E p) by exact Hq. apply Hl. exact Hq.
Qed.

(* a constant labelling is the full symmetric group: Klab is Ksym *)
Theorem Klab_const_is_Ksym : forall R a f, Klab R (fun _ => a) f <-> Ksym R f.
Proof.
  intros R a f. split.
  - intros [Hloc Hinv]. split; [exact Hloc |].
    intros s s' Hp. apply (Hinv s s' Hp). intros p _. reflexivity.
  - intros [Hloc Hinv]. split; [exact Hloc |].
    intros s s' Hp _. exact (Hinv s s' Hp).
Qed.

(* --- declared blocks as a list of sizes: the formalism's comm on some   *)
(* consecutive argument groups and not across them ---------------------- *)

Fixpoint blk (bs : list nat) (p : nat) : nat :=
  match bs with
  | [] => 0
  | b :: bs' => if p <? b then 0 else S (blk bs' (p - b))
  end.

Definition Kblock (bs : list nat) (f : (nat -> nat) -> nat) : Prop :=
  Klab (lsum bs) (blk bs) f.

(* one block of size r is the full symmetric group *)
Theorem Kblock_single_is_Ksym : forall r f, Kblock [r] f <-> Ksym r f.
Proof.
  intros r f. unfold Kblock. simpl lsum. rewrite Nat.add_0_r. split.
  - intro H. apply (Klab_const_is_Ksym r 0 f).
    apply (Klab_ext r (blk [r]) (fun _ => 0)); [| exact H].
    intros p Hp. simpl. destruct (Nat.ltb_spec p r); [reflexivity | lia].
  - intro H. apply (Klab_ext r (fun _ => 0) (blk [r])).
    + intros p Hp. simpl. destruct (Nat.ltb_spec p r); [reflexivity | lia].
    + apply Klab_const_is_Ksym. exact H.
Qed.

(* the block shape of declared blocks over one index space [0, n) *)
Definition bshape (bs : list nat) (n : nat) : Shape :=
  map (fun b => mkIx b 0 n) bs.

Lemma sgrp_shift : forall s j p, sgrp j s p = j + sgrp 0 s p.
Proof.
  induction s as [|c s IH]; intros j p; simpl; [lia |].
  destruct (p <? ix_r c); [lia |].
  rewrite (IH (S j)), (IH 1). lia.
Qed.

Lemma sgrp_bshape : forall bs n p, sgrp 0 (bshape bs n) p = blk bs p.
Proof.
  induction bs as [|b bs IH]; intros n p; simpl; [reflexivity |].
  destruct (p <? b); [reflexivity |].
  rewrite sgrp_shift, IH. reflexivity.
Qed.

Lemma arity_bshape : forall bs n, arity (bshape bs n) = lsum bs.
Proof.
  induction bs as [|b bs IH]; intro n; [reflexivity |].
  unfold arity in *. simpl. rewrite IH. reflexivity.
Qed.

Lemma bshape_card : forall bs n,
  shapeCard (bshape bs n) = fold_right Nat.mul 1 (map (fun b => C (n + b - 1) b) bs).
Proof.
  intros bs n. rewrite shapeCard_binom. unfold bshape. rewrite map_map.
  f_equal. apply map_ext. intro b. simpl. f_equal. lia.
Qed.

(* ===================================================================== *)
(* THE MEET.  A shape s whose record j is a run of positions reading ONE *)
(* array arr j, inside ONE declared kernel block kb j.  The licence      *)
(* group acting on cells is H /\ Stab(binding): positions are            *)
(* interchangeable iff same kernel block AND same array.  When the pair  *)
(* (kb j, arr j) names the record (meet_inj), the records are exactly    *)
(* the classes, and the forced count is shapeCard s.  One array          *)
(* everywhere (arr constant) is the kernel cut alone; a fully symmetric  *)
(* kernel (kb constant) is BladeOptimal's Young nest (the data cut       *)
(* alone); both at once is the meet.                                     *)
(* ===================================================================== *)

(* splitting a permutation of two-part lists along a separating predicate *)
Lemma perm_app_split : forall (P : nat -> Prop) (l1 l2 l1' l2' : list nat),
  (forall x, In x l1 -> P x) -> (forall x, In x l1' -> P x) ->
  (forall x, In x l2 -> ~ P x) -> (forall x, In x l2' -> ~ P x) ->
  Permutation (l1 ++ l2) (l1' ++ l2') ->
  Permutation l1 l1' /\ Permutation l2 l2'.
Proof.
  intros P l1 l2 l1' l2' H1 H1' H2 H2' HP.
  set (q := fun x => if in_dec Nat.eq_dec x (l1 ++ l1') then true else false).
  assert (Q1 : forall x, In x (l1 ++ l1') -> q x = true).
  { intros x Hx. unfold q. destruct (in_dec Nat.eq_dec x (l1 ++ l1')); [reflexivity | contradiction]. }
  assert (Q2 : forall x, In x (l2 ++ l2') -> q x = false).
  { intros x Hx. unfold q. destruct (in_dec Nat.eq_dec x (l1 ++ l1')) as [Hin | _]; [| reflexivity].
    exfalso. apply in_app_iff in Hin, Hx.
    destruct Hx as [Hx | Hx]; [apply (H2 x Hx) | apply (H2' x Hx)];
      (destruct Hin as [Hin | Hin]; [apply H1; exact Hin | apply H1'; exact Hin]). }
  split.
  - pose proof (perm_filter q _ _ HP) as HF. rewrite !filter_app in HF.
    rewrite (filter_all q l1), (filter_all q l1') in HF
      by (intros x Hx; apply Q1; apply in_app_iff; auto).
    rewrite (filter_none q l2), (filter_none q l2') in HF
      by (intros x Hx; apply Q2; apply in_app_iff; auto).
    rewrite !app_nil_r in HF. exact HF.
  - pose proof (perm_filter (fun x => negb (q x)) _ _ HP) as HF. rewrite !filter_app in HF.
    rewrite (filter_none _ l1), (filter_none _ l1') in HF
      by (intros x Hx; rewrite Q1 by (apply in_app_iff; auto); reflexivity).
    rewrite (filter_all _ l2), (filter_all _ l2') in HF
      by (intros x Hx; rewrite Q2 by (apply in_app_iff; auto); reflexivity).
    exact HF.
Qed.

Lemma svals_in : forall D s j t x, In x (svals D j s t) ->
  exists a i, j <= a < j + length s /\ x = D a i.
Proof.
  intros D. induction s as [|c s IH]; intros j t x Hx; simpl in Hx; [destruct Hx |].
  apply in_app_iff in Hx as [Hx | Hx].
  - apply in_map_iff in Hx as (i & <- & _). exists j, i. simpl. split; [lia | reflexivity].
  - destruct (IH _ _ _ Hx) as (a & i & Ha & ->). exists a, i. simpl. split; [lia | reflexivity].
Qed.

(* SEPARATION for data that is injective per record and disjoint across *)
(* records -- svals_separate without a global group decoder.            *)
Lemma svals_sep_gen : forall D s j t t',
  (forall a b i k, j <= a < j + length s -> j <= b < j + length s ->
     D a i = D b k -> a = b /\ i = k) ->
  In t (enumShape s) -> In t' (enumShape s) ->
  Permutation (svals D j s t) (svals D j s t') -> t = t'.
Proof.
  intros D. induction s as [|c s IH]; intros j t t' Hinj Ht Ht' HP.
  - simpl in Ht, Ht'. destruct Ht as [E | []]. destruct Ht' as [E' | []].
    subst. reflexivity.
  - apply enumShape_cons_inv in Ht as (t1 & t2 & E & H1 & H2).
    apply enumShape_cons_inv in Ht' as (t1' & t2' & E' & H1' & H2').
    subst t t'. unfold enumIx in H1, H1'.
    rewrite svals_cons in HP by (eapply enum_elem_length; exact H1).
    rewrite svals_cons in HP by (eapply enum_elem_length; exact H1').
    simpl length in Hinj.
    set (P := fun x => exists i, x = D j i).
    assert (Pin : forall tt x, In x (map (D j) tt) -> P x).
    { intros tt x Hx. apply in_map_iff in Hx as (i & <- & _). exists i. reflexivity. }
    assert (Pout : forall tt x, In x (svals D (S j) s tt) -> ~ P x).
    { intros tt x Hx [i ->]. apply svals_in in Hx as (a & k & Ha & E).
      destruct (Hinj j a i k) as [Ea _]; [lia | lia | exact E | lia]. }
    destruct (perm_app_split P _ _ _ _ (Pin t1) (Pin t1') (Pout t2) (Pout t2') HP)
      as [HP1 HP2].
    f_equal.
    + apply (perm_map_inj (D j)) in HP1.
      * exact (canon_perm_eq _ _ _ _ _
                 (enum_sound _ _ _ _ H1) (enum_sound _ _ _ _ H1') HP1).
      * intros i k E. exact (proj2 (Hinj j j i k ltac:(lia) ltac:(lia) E)).
    + apply (IH (S j) t2 t2'); try assumption.
      intros a b i k Ha Hb E. apply Hinj; [lia | lia | exact E].
Qed.

Section Meet.
  Variable s : Shape.                   (* the meet: one record per class  *)
  Variables kb arr : nat -> nat.        (* record -> kernel block, array   *)
  Variable D : nat -> nat -> nat.       (* array -> index -> value         *)

  (* the record's pair (kernel block, array) names it *)
  Hypothesis meet_inj : forall a b, a < length s -> b < length s ->
    kb a = kb b -> arr a = arr b -> a = b.
  (* generic data on the arrays the binding uses *)
  Hypothesis D_gen : forall a b i k, a < length s -> b < length s ->
    D (arr a) i = D (arr b) k -> arr a = arr b /\ i = k.

  Definition mR : nat := arity s.
  Definition mlab (p : nat) : nat := kb (sgrp 0 s p).   (* the kernel's blocks *)
  Definition marr (p : nat) : nat := arr (sgrp 0 s p).  (* the binding         *)

  (* the call site: position p reads array marr p at the cell's index p *)
  Definition margs (t : list nat) : nat -> nat := fun p => D (marr p) (nth p t 0).

  Lemma margs_is_Out : forall f t,
    f (margs t) = Out nat nat f nat marr D (fun p => nth p t 0).
  Proof. reflexivity. Qed.

  (* the tagged tabulation of a cell's argument tuple is a svals *)
  Definition mD (j i : nat) : nat := cD (kb j) (D (arr j) i).

  Lemma ltab_margs : forall t, sdense s t ->
    ltab mR mlab (margs t) = svals mD 0 s t.
  Proof.
    intros t Hd. unfold ltab, tab, mR.
    rewrite <- (tab_nth (svals mD 0 s t) 0).
    rewrite (svals_length mD s 0 t Hd).
    apply map_ext_in. intros p Hp. apply in_seq in Hp.
    rewrite (svals_nth mD s 0 t p Hd) by lia. reflexivity.
  Qed.

  Lemma mD_inj : forall a b i k, 0 <= a < 0 + length s -> 0 <= b < 0 + length s ->
    mD a i = mD b k -> a = b /\ i = k.
  Proof.
    intros a b i k Ha Hb E. unfold mD in E.
    apply cD_inj2 in E as [E1 E2].
    destruct (D_gen a b i k ltac:(lia) ltac:(lia) E2) as [E3 E4].
    split; [apply meet_inj; lia || assumption | exact E4].
  Qed.

  Lemma mcls_inj : forall t t', In t (enumShape s) -> In t' (enumShape s) ->
    lcls mR mlab (margs t) = lcls mR mlab (margs t') -> t = t'.
  Proof.
    intros t t' Ht Ht' E. unfold lcls in E.
    rewrite !ltab_margs in E by (apply enumShape_dense; assumption).
    apply (svals_sep_gen mD s 0 t t' mD_inj Ht Ht').
    apply isort_fibres_are_orbits. exact E.
  Qed.

  Lemma meet_perturbable : forall x,
    perturbable (nat -> nat) (list nat) (list nat) nat
      (lcls mR mlab) (Klab mR mlab) margs x.
  Proof.
    intro x.
    exists (fun k v => if list_eq_dec Nat.eq_dec (lcls mR mlab v)
                                       (lcls mR mlab (margs x))
                       then S (k v) else k v).
    split; [| split].
    - intros k Hk. apply Klcls_Klab. apply Klab_Klcls in Hk.
      intros v v' E. rewrite E.
      destruct (list_eq_dec Nat.eq_dec (lcls mR mlab v') (lcls mR mlab (margs x)));
        [f_equal |]; apply Hk; exact E.
    - intros k v Hne.
      destruct (list_eq_dec Nat.eq_dec (lcls mR mlab v) (lcls mR mlab (margs x)));
        [contradiction | reflexivity].
    - intro k.
      destruct (list_eq_dec Nat.eq_dec (lcls mR mlab (margs x)) (lcls mR mlab (margs x)));
        [lia | contradiction].
  Qed.

  (* T1 at G = H /\ Stab for a proper H: shapeCard of the meet is forced. *)
  Theorem meet_lower_bound :
    forall (O : Type) (out : O -> list nat -> nat) (P : prog (nat -> nat) nat O),
    (forall f, Klab mR mlab f -> forall t, In t (enumShape s) ->
       out (run P f) t = f (margs t)) ->
    forall f, Klab mR mlab f -> shapeCard s <= length (trace P f).
  Proof.
    intros O out P Hcor f Hf.
    rewrite shapeCard_is_length.
    apply (orbit_lower_bound (nat -> nat) (list nat) (list nat) nat O
             (lcls mR mlab) (list_eq_dec Nat.eq_dec) (Klab mR mlab)
             margs out (enumShape s) P).
    - apply NoDup_map_inj; [apply enumShape_NoDup |].
      intros x y Hx Hy E. exact (mcls_inj x y Hx Hy E).
    - intros x _. apply meet_perturbable.
    - exact Hcor.
    - exact Hf.
  Qed.

  (* T1' : the same count bounds any opaque-cell store *)
  Theorem meet_storage_bound :
    forall (cells : list (nat -> nat)) (acc : list nat -> nat),
    (forall f, Klab mR mlab f -> forall t, In t (enumShape s) ->
       nth (acc t) (map f cells) 0 = f (margs t)) ->
    shapeCard s <= length cells.
  Proof.
    intros cells acc Hread. rewrite shapeCard_is_length.
    apply (orbit_storage_bound (nat -> nat) (list nat) (list nat) nat
             (lcls mR mlab) (list_eq_dec Nat.eq_dec) (Klab mR mlab) margs
             (enumShape s) cells acc 0 (fun _ => 0)).
    - apply Klcls_Klab. intros v v' _. reflexivity.
    - apply NoDup_map_inj; [apply enumShape_NoDup |].
      intros x y Hx Hy E. exact (mcls_inj x y Hx Hy E).
    - intros x _. apply meet_perturbable.
    - exact Hread.
  Qed.

  (* T2: the object's program over the meet attains it, and serves every *)
  (* DENSE cell by reading at the blockwise canonical form (scanon).     *)
  Definition meet_prog : prog (nat -> nat) nat (list nat) :=
    ask_all (map margs (enumShape s)) [].

  Definition meet_out (o : list nat) (t : list nat) : nat :=
    nth (index_of (scanon s t) (enumShape s)) o 0.

  Theorem meet_attained :
    (forall f, Klab mR mlab f -> forall t, sdense s t ->
       meet_out (run meet_prog f) t = f (margs t)) /\
    (forall f, length (trace meet_prog f) = shapeCard s).
  Proof.
    split.
    - intros f Hf t Hd. unfold meet_out, meet_prog.
      rewrite ask_all_run. simpl. rewrite map_map.
      rewrite (nth_index_of nat (fun t => f (margs t)) 0)
        by (apply scanon_in; exact Hd).
      apply (Klab_Klcls mR mlab f Hf). unfold lcls.
      rewrite ltab_margs by (apply enumShape_dense; apply scanon_in; exact Hd).
      rewrite ltab_margs by exact Hd.
      apply isort_respects. apply scanon_vals. exact Hd.
    - intro f. unfold meet_prog. rewrite ask_all_trace, length_map.
      symmetry. apply shapeCard_is_length.
  Qed.

  (* the attaining program IS the object's program, read through the     *)
  (* binding                                                             *)
  Theorem meet_prog_is_object_prog : forall f : (nat -> nat) -> nat,
    run meet_prog f = run (object_prog s) (fun t => f (margs t))
    /\ trace meet_prog f
       = map margs (trace (object_prog s) (fun t => f (margs t))).
  Proof.
    intro f. unfold meet_prog. split.
    - rewrite ask_all_run, object_prog_run. unfold veval. simpl.
      rewrite map_map. reflexivity.
    - rewrite ask_all_trace, object_prog_trace. reflexivity.
  Qed.

  Corollary uniform_optimality_meet :
    forall (O : Type) (out : O -> list nat -> nat) (P : prog (nat -> nat) nat O),
    (forall f, Klab mR mlab f -> forall t, In t (enumShape s) ->
       out (run P f) t = f (margs t)) ->
    forall f, Klab mR mlab f ->
      length (trace meet_prog f) <= length (trace P f).
  Proof.
    intros O out P Hcor f Hf.
    rewrite (proj2 meet_attained f).
    exact (meet_lower_bound O out P Hcor f Hf).
  Qed.
End Meet.

(* ===================================================================== *)
(* INSTANCE 1: THE KERNEL CUT ALONE.  One array in every position, a     *)
(* kernel commutative only inside its declared blocks bs.  The licence   *)
(* group is H itself (Stab of a one-array binding is everything), and    *)
(* the forced count is prod_j C(n + b_j - 1, b_j).  The data cannot      *)
(* separate the blocks here -- every position reads the same array --    *)
(* so the cut comes from the declaration of the kernel, not the data.    *)
(* ===================================================================== *)

Lemma Kblock_meet : forall bs n f,
  Kblock bs f <-> Klab (mR (bshape bs n)) (mlab (bshape bs n) (fun j => j)) f.
Proof.
  intros bs n f. unfold Kblock, mR, mlab. rewrite arity_bshape. split.
  - apply Klab_ext. intros p _. rewrite sgrp_bshape. reflexivity.
  - apply Klab_ext. intros p _. rewrite sgrp_bshape. reflexivity.
Qed.

Lemma bshape_length : forall bs n, length (bshape bs n) = length bs.
Proof. intros. unfold bshape. apply length_map. Qed.

Lemma sdense_bshape : forall bs n t,
  length t = lsum bs -> (forall x, In x t -> x < n) -> sdense (bshape bs n) t.
Proof.
  induction bs as [|b bs IH]; intros n t Hl Hb; simpl; [exact I |].
  simpl in Hl. split; [| split].
  - rewrite length_firstn. lia.
  - intros x Hx. split; [lia |]. apply Hb. rewrite <- (firstn_skipn b t). apply in_or_app. left. exact Hx.
  - apply IH.
    + rewrite length_skipn. lia.
    + intros x Hx. apply Hb. rewrite <- (firstn_skipn b t). apply in_or_app. right. exact Hx.
Qed.

Section BlockNest.
  Variable bs : list nat.
  Variable dat : nat -> nat.
  Hypothesis dat_inj : forall i j, dat i = dat j -> i = j.

  Definition block_prog (n : nat) : prog (nat -> nat) nat (list nat) :=
    meet_prog (bshape bs n) (fun _ => 0) (fun _ i => dat i).

  Definition block_out (n : nat) : list nat -> list nat -> nat :=
    meet_out (bshape bs n).

  Lemma block_meet_inj : forall n a b,
    a < length (bshape bs n) -> b < length (bshape bs n) ->
    (fun j : nat => j) a = (fun j : nat => j) b ->
    (fun _ : nat => 0) a = (fun _ : nat => 0) b -> a = b.
  Proof. intros n a b _ _ E _. exact E. Qed.

  Lemma block_D_gen : forall n a b i k,
    a < length (bshape bs n) -> b < length (bshape bs n) ->
    (fun _ i => dat i) ((fun _ : nat => 0) a) i
    = (fun _ i => dat i) ((fun _ : nat => 0) b) k ->
    (fun _ : nat => 0) a = (fun _ : nat => 0) b /\ i = k.
  Proof. intros n a b i k _ _ E. split; [reflexivity | apply dat_inj; exact E]. Qed.

  (* T1 for a proper subgroup: the block shape's card is forced *)
  Theorem block_lower_bound :
    forall (n : nat) (O : Type) (out : O -> list nat -> nat)
           (P : prog (nat -> nat) nat O),
    (forall f, Kblock bs f -> forall t, In t (enumShape (bshape bs n)) ->
       out (run P f) t = f (argsOf dat t)) ->
    forall f, Kblock bs f -> shapeCard (bshape bs n) <= length (trace P f).
  Proof.
    intros n O out P Hcor f Hf.
    apply (meet_lower_bound (bshape bs n) (fun j => j) (fun _ => 0)
             (fun _ i => dat i) (block_meet_inj n) (block_D_gen n) O out P).
    - intros k Hk t Ht. apply Hcor; [apply (Kblock_meet bs n); exact Hk | exact Ht].
    - apply (Kblock_meet bs n). exact Hf.
  Qed.

  (* T2: attained, serving every dense cell (any index tuple of the      *)
  (* right length over [0, n)) by the blockwise sort                     *)
  Theorem block_attained : forall n,
    (forall f, Kblock bs f -> forall t,
       length t = lsum bs -> (forall x, In x t -> x < n) ->
       block_out n (run (block_prog n) f) t = f (argsOf dat t)) /\
    (forall f, length (trace (block_prog n) f) = shapeCard (bshape bs n)).
  Proof.
    intro n. split.
    - intros f Hf t Hl Hb.
      apply (proj1 (meet_attained (bshape bs n) (fun j => j) (fun _ => 0)
                      (fun _ i => dat i))).
      + apply (Kblock_meet bs n). exact Hf.
      + apply sdense_bshape; assumption.
    - exact (proj2 (meet_attained (bshape bs n) (fun j => j) (fun _ => 0)
                      (fun _ i => dat i))).
  Qed.

  Corollary uniform_optimality_block :
    forall (n : nat) (O : Type) (out : O -> list nat -> nat)
           (P : prog (nat -> nat) nat O),
    (forall f, Kblock bs f -> forall t, In t (enumShape (bshape bs n)) ->
       out (run P f) t = f (argsOf dat t)) ->
    forall f, Kblock bs f ->
      length (trace (block_prog n) f) <= length (trace P f).
  Proof.
    intros n O out P Hcor f Hf.
    rewrite (proj2 (block_attained n) f).
    exact (block_lower_bound n O out P Hcor f Hf).
  Qed.
End BlockNest.

(* Closed: the identity is generic data for one array. *)
Theorem uniform_optimality_block_nat :
  forall (bs : list nat) (n : nat) (O : Type) (out : O -> list nat -> nat)
         (P : prog (nat -> nat) nat O),
  (forall f, Kblock bs f -> forall t, In t (enumShape (bshape bs n)) ->
     out (run P f) t = f (argsOf (fun i => i) t)) ->
  forall f, Kblock bs f ->
    fold_right Nat.mul 1 (map (fun b => C (n + b - 1) b) bs) <= length (trace P f) /\
    length (trace (block_prog bs (fun i => i) n) f)
    = fold_right Nat.mul 1 (map (fun b => C (n + b - 1) b) bs).
Proof.
  intros bs n O out P Hcor f Hf. rewrite <- bshape_card. split.
  - exact (block_lower_bound bs (fun i => i) (fun i j E => E) n O out P Hcor f Hf).
  - exact (proj2 (block_attained bs (fun i => i) n) f).
Qed.

(* ===================================================================== *)
(* INSTANCE 2: BOTH CUTS, CLOSED.  Generic data (Cantor pairing tags a   *)
(* value with its array) discharges D_gen for every binding, so the      *)
(* meet statement is closed: the plan's uniform_optimality_subgroup.     *)
(* ===================================================================== *)

Theorem uniform_optimality_subgroup :
  forall (s : Shape) (kb arr : nat -> nat),
  (forall a b, a < length s -> b < length s -> kb a = kb b -> arr a = arr b -> a = b) ->
  forall (O : Type) (out : O -> list nat -> nat) (P : prog (nat -> nat) nat O),
  (forall f, Klab (mR s) (mlab s kb) f -> forall t, In t (enumShape s) ->
     out (run P f) t = f (margs s arr cD t)) ->
  forall f, Klab (mR s) (mlab s kb) f ->
    shapeCard s <= length (trace P f) /\
    length (trace (meet_prog s arr cD) f) = shapeCard s /\
    (forall t, sdense s t ->
       meet_out s (run (meet_prog s arr cD) f) t = f (margs s arr cD t)).
Proof.
  intros s kb arr Hinj O out P Hcor f Hf.
  assert (Hgen : forall a b i k, a < length s -> b < length s ->
            cD (arr a) i = cD (arr b) k -> arr a = arr b /\ i = k)
    by (intros a b i k _ _ E; exact (cD_inj2 _ _ _ _ E)).
  split; [| split].
  - exact (meet_lower_bound s kb arr cD Hinj Hgen O out P Hcor f Hf).
  - exact (proj2 (meet_attained s kb arr cD) f).
  - exact (proj1 (meet_attained s kb arr cD) f Hf).
Qed.

(* the data cut alone: a fully symmetric kernel is a constant labelling, *)
(* so the meet theorem specializes to the Young nest of BladeOptimal     *)
Theorem meet_full_kernel_is_Ksym : forall s a f,
  Klab (mR s) (mlab s (fun _ => a)) f <-> Ksym (arity s) f.
Proof. intros s a f. unfold mlab, mR. apply Klab_const_is_Ksym. Qed.

(* ... and the call sites agree: with record j reading array j, the meet *)
(* reads exactly what the Young nest of BladeOptimal reads (shargs)      *)
Lemma margs_id_is_shargs : forall D s t p, sdense s t -> p < arity s ->
  margs s (fun j => j) D t p = shargs D s t p.
Proof.
  intros D s t p Hd Hp. unfold margs, marr, shargs. symmetry. apply svals_nth; assumption.
Qed.

(* --- pins ------------------------------------------------------------ *)

(* blocks [2; 1] over n = 4: C(5, 2) * 4 = 40 cells *)
Example block_card_2_1 : shapeCard (bshape [2; 1] 4) = 40.
Proof. vm_compute. reflexivity. Qed.

(* declared fully commutative: C(6, 3) = 20; declared nowhere: 4^3 = 64 *)
Example block_card_vs_sym_dense :
  shapeCard (bshape [3] 4) = 20 /\ shapeCard (bshape [1; 1; 1] 4) = 64.
Proof. vm_compute. split; reflexivity. Qed.

(* the meet, both cuts: kernel blocks [3; 2] bound to (A, A, B | B, B).  *)
(* Records: (A, A) and (B) in kernel block 0, (B, B) in kernel block 1; *)
(* B is cut from A by the data and from itself by the kernel.            *)
Definition meet_ex (n : nat) : Shape := [mkIx 2 0 n; mkIx 1 0 n; mkIx 2 0 n].
Definition meet_ex_kb (j : nat) : nat := match j with 0 | 1 => 0 | _ => 1 end.
Definition meet_ex_arr (j : nat) : nat := match j with 0 => 0 | _ => 1 end.

Lemma meet_ex_inj : forall n a b, a < length (meet_ex n) -> b < length (meet_ex n) ->
  meet_ex_kb a = meet_ex_kb b -> meet_ex_arr a = meet_ex_arr b -> a = b.
Proof.
  intros n a b Ha Hb E1 E2. simpl in Ha, Hb.
  destruct a as [|[|[|a]]]; destruct b as [|[|[|b]]]; simpl in *;
    try lia; try discriminate; reflexivity.
Qed.

Example meet_ex_card : shapeCard (meet_ex 4) = 400.
Proof. vm_compute. reflexivity. Qed.

(* the meet example's law class IS the declared blocks [3; 2] *)
Lemma meet_ex_law : forall n f,
  Klab (mR (meet_ex n)) (mlab (meet_ex n) meet_ex_kb) f <-> Kblock [3; 2] f.
Proof.
  intros n f. unfold Kblock.
  change (mR (meet_ex n)) with 5. change (lsum [3; 2]) with 5.
  split; apply Klab_ext; intros p Hp;
    destruct p as [|[|[|[|[|p]]]]]; try reflexivity; lia.
Qed.

(* ... and 400 is FORCED there: kernel blocks [3; 2], binding            *)
(* (A, A, B, B, B), generic data, n = 4                                   *)
Theorem meet_ex_forced :
  forall (O : Type) (out : O -> list nat -> nat) (P : prog (nat -> nat) nat O),
  (forall f, Kblock [3; 2] f -> forall t, In t (enumShape (meet_ex 4)) ->
     out (run P f) t = f (margs (meet_ex 4) meet_ex_arr cD t)) ->
  forall f, Kblock [3; 2] f ->
    400 <= length (trace P f) /\
    length (trace (meet_prog (meet_ex 4) meet_ex_arr cD) f) = 400.
Proof.
  intros O out P Hcor f Hf.
  destruct (uniform_optimality_subgroup (meet_ex 4) meet_ex_kb meet_ex_arr (meet_ex_inj 4)
              O out P) with (f := f) as (B1 & B2 & _).
  - intros k Hk t Ht. apply Hcor; [apply (meet_ex_law 4); exact Hk | exact Ht].
  - apply (meet_ex_law 4). exact Hf.
  - rewrite meet_ex_card in B1, B2. split; assumption.
Qed.

(* ===================================================================== *)
(* THE SURFACE RULE AGREES.  BladeSurface's kernel carries ONE comm flag *)
(* for all positions, so its only expressible H are S_R (comm) and the   *)
(* trivial group (no comm).  Under comm, out_shape's identity groups are *)
(* the data-side cut of the same meet: with kb constant and record j     *)
(* reading the array named by the j-th identity group, out_shape IS the  *)
(* meet shape, provided no name recurs in two different groups (the     *)
(* rle groups have pairwise distinct names).  So the typing rule's       *)
(* output shape is the forced count, and the object attains it.          *)
(* ===================================================================== *)

Definition rank1 (b : list (nat * Shape)) : Prop :=
  Forall (fun x => exists c, snd x = [c] /\ ix_r c = 1) b.

Lemma rle_in : forall b g, In g (rle b) -> In (g_name g, g_shape g) b.
Proof.
  induction b as [|[x s] b IH]; intros g Hg; simpl in Hg; [destruct Hg |].
  destruct (rle b) as [|g' gs] eqn:E.
  - destruct Hg as [<- | []]. left. reflexivity.
  - destruct (Nat.eq_dec x (g_name g')) as [Ex | Nx].
    + destruct (shape_eq_dec s (g_shape g')) as [Es | Ns].
      * destruct Hg as [<- | Hg]; [left; reflexivity |].
        right. apply IH. right. exact Hg.
      * destruct Hg as [<- | Hg]; [left; reflexivity |]. right. apply IH. exact Hg.
    + destruct Hg as [<- | Hg]; [left; reflexivity |]. right. apply IH. exact Hg.
Qed.

Definition grank1 (g : grp) : Prop := exists c, g_shape g = [c] /\ ix_r c = 1.

Lemma group_shape_rank1 : forall g, grank1 g ->
  exists c, group_shape true g = [mkIx (g_mult g) (ix_l c) (ix_u c)].
Proof.
  intros g (c & Hs & Hr). exists c. apply group_shape_grouped; [| exact Hs].
  unfold grouped. rewrite Hs, Hr. reflexivity.
Qed.

Section SurfaceGroups.
  Variable gs : list grp.
  Hypothesis Hr1 : forall g, In g gs -> grank1 g.

  Definition gshape : Shape := concat (map (group_shape true) gs).

  Lemma gshape_facts :
    length gshape = length gs /\
    arity gshape = lsum (map g_mult gs) /\
    length (concat (map expand gs)) = lsum (map g_mult gs) /\
    (forall p, p < lsum (map g_mult gs) ->
       fst (nth p (concat (map expand gs)) (0, []))
       = g_name (nth (sgrp 0 gshape p) gs (mkG 0 [] 0))).
  Proof.
    unfold gshape. induction gs as [|g gs' IH]; simpl.
    - split; [reflexivity | split; [reflexivity | split; [reflexivity |]]].
      intros p Hp. lia.
    - destruct (group_shape_rank1 g (Hr1 g (or_introl eq_refl))) as [c Ec].
      rewrite Ec.
      destruct IH as (IH1 & IH2 & IH3 & IH4).
      { intros g' Hg'. apply Hr1. right. exact Hg'. }
      assert (Lx : length (expand g) = g_mult g)
        by (unfold expand; apply repeat_length).
      split; [| split; [| split]].
      + simpl. rewrite IH1. reflexivity.
      + unfold arity in *. simpl. rewrite IH2. reflexivity.
      + rewrite length_app, Lx, IH3. reflexivity.
      + intros p Hp. simpl sgrp.
        destruct (Nat.ltb_spec p (g_mult g)).
        * rewrite app_nth1 by lia.
          assert (Hin : In (nth p (expand g) (0, [])) (expand g))
            by (apply nth_In; lia).
          unfold expand in Hin. apply repeat_spec in Hin.
          unfold expand. rewrite Hin. reflexivity.
        * rewrite app_nth2 by lia. rewrite Lx.
          rewrite sgrp_shift. simpl nth at 2.
          apply IH4. lia.
  Qed.
End SurfaceGroups.

Lemma rle_length : forall b, length b = lsum (map g_mult (rle b)).
Proof.
  intro b. rewrite <- (rle_expand b) at 1.
  induction (rle b) as [|g gs IH]; simpl; [reflexivity |].
  rewrite length_app, IH. unfold expand. rewrite repeat_length. reflexivity.
Qed.

(* the array read by record j of out_shape: the name of identity group j *)
Definition sarr (b : list (nat * Shape)) (j : nat) : nat :=
  g_name (nth j (rle b) (mkG 0 [] 0)).

Theorem out_shape_is_meet : forall b,
  rank1 b -> NoDup (map g_name (rle b)) ->
  arity (out_shape true b) = length b /\
  (forall a a', a < length (out_shape true b) -> a' < length (out_shape true b) ->
     (fun _ : nat => 0) a = (fun _ : nat => 0) a' -> sarr b a = sarr b a' -> a = a') /\
  (forall p, p < length b -> sarr b (sgrp 0 (out_shape true b) p) = fst (nth p b (0, []))).
Proof.
  intros b Hr Hnd.
  assert (Hg : forall g, In g (rle b) -> grank1 g).
  { intros g Hin. apply rle_in in Hin. unfold rank1 in Hr. rewrite Forall_forall in Hr.
    destruct (Hr _ Hin) as (c & Hs & Hc). exists c. split; [exact Hs | exact Hc]. }
  destruct (gshape_facts (rle b) Hg) as (F1 & F2 & F3 & F4).
  unfold gshape in *. fold (out_shape true b) in F1, F2, F4.
  rewrite rle_expand in F3, F4.
  split; [| split].
  - rewrite F2. symmetry. apply rle_length.
  - intros a a' Ha Ha' _ E. rewrite F1 in Ha, Ha'. unfold sarr in E.
    pose proof (proj1 (NoDup_nth (map g_name (rle b)) 0) Hnd) as Hnd2.
    apply (Hnd2 a a'); [rewrite length_map; exact Ha | rewrite length_map; exact Ha' |].
    rewrite <- (map_nth g_name (rle b) (mkG 0 [] 0) a) in E.
    rewrite <- (map_nth g_name (rle b) (mkG 0 [] 0) a') in E. exact E.
  - intros p Hp. unfold sarr. symmetry. apply F4. rewrite <- rle_length. exact Hp.
Qed.

(* THE SURFACE RULE IS OPTIMAL (distinct-name bindings).  For a well-   *)
(* formed comm kernel applied to a rank-1 binding whose identity groups *)
(* have distinct names: every program correct on the typed output's     *)
(* cells for every comm kernel of that arity asks at least              *)
(* shapeCard (out_shape true b) questions, and the object's program     *)
(* over out_shape asks exactly that many and is right on every dense    *)
(* cell.  Position p reads the operand named fst (nth p b).             *)
Theorem surface_comm_optimal :
  forall (f : kernel) (b : list (nat * Shape)),
  kwf f -> k_comm f = true -> k_arity f = length b ->
  rank1 b -> NoDup (map g_name (rle b)) ->
  forall (O : Type) (out : O -> list nat -> nat) (P : prog (nat -> nat) nat O),
  (forall k, Ksym (length b) k -> forall t, In t (enumShape (out_shape true b)) ->
     out (run P k) t = k (margs (out_shape true b) (sarr b) cD t)) ->
  shapeCard (out_shape true b) <= length (trace P (k_body f)) /\
  length (trace (meet_prog (out_shape true b) (sarr b) cD) (k_body f))
    = shapeCard (out_shape true b) /\
  (forall t, sdense (out_shape true b) t ->
     meet_out (out_shape true b) (run (meet_prog (out_shape true b) (sarr b) cD) (k_body f)) t
     = k_body f (margs (out_shape true b) (sarr b) cD t)) /\
  (forall t p, p < length b ->
     margs (out_shape true b) (sarr b) cD t p = cD (fst (nth p b (0, []))) (nth p t 0)).
Proof.
  intros f b Hwf Hc Ha Hr Hnd O out P Hcor.
  destruct (out_shape_is_meet b Hr Hnd) as (A1 & A2 & A3).
  assert (Hlaw : forall k, Klab (mR (out_shape true b)) (mlab (out_shape true b) (fun _ => 0)) k
                 <-> Ksym (length b) k).
  { intro k. rewrite meet_full_kernel_is_Ksym, A1. reflexivity. }
  assert (Hf : Klab (mR (out_shape true b)) (mlab (out_shape true b) (fun _ => 0)) (k_body f)).
  { apply Hlaw. rewrite <- Ha. apply kwf_Ksym; assumption. }
  destruct (uniform_optimality_subgroup (out_shape true b) (fun _ => 0) (sarr b) A2
              O out P) with (f := k_body f) as (B1 & B2 & B3).
  - intros k Hk t Ht. apply Hcor; [apply Hlaw; exact Hk | exact Ht].
  - exact Hf.
  - split; [exact B1 | split; [exact B2 | split; [exact B3 |]]].
    intros t p Hp. unfold margs, marr. rewrite A3 by exact Hp. reflexivity.
Qed.


(* --- the data-level bridge: margs IS what BladeSurface computes ------- *)
(* With Cantor data in the environment (the array named x holds cD x i  *)
(* at index [i]), BladeSurface's apply_kernel on the resolved binding,  *)
(* and hence the denotation of a well-typed L <@> f, is the kernel at   *)
(* margs (out_shape true b) (sarr b) cD: the call site the bound above  *)
(* quantifies over is the calculus's own.                                *)

Lemma out_shape_reads : forall b, rank1 b ->
  arity (out_shape true b) = length b /\
  (forall p, p < length b -> sarr b (sgrp 0 (out_shape true b) p) = fst (nth p b (0, []))).
Proof.
  intros b Hr.
  assert (Hg : forall g, In g (rle b) -> grank1 g).
  { intros g Hin. apply rle_in in Hin. unfold rank1 in Hr. rewrite Forall_forall in Hr.
    destruct (Hr _ Hin) as (c & Hs & Hc). exists c. split; [exact Hs | exact Hc]. }
  destruct (gshape_facts (rle b) Hg) as (F1 & F2 & F3 & F4).
  unfold gshape in *. fold (out_shape true b) in F1, F2, F4.
  rewrite rle_expand in F3, F4.
  split.
  - rewrite F2. symmetry. apply rle_length.
  - intros p Hp. unfold sarr. symmetry. apply F4. rewrite <- rle_length. exact Hp.
Qed.

Lemma vals_cantor : forall ro b, rank1 b ->
  (forall x s, In (x, s) b -> fst (ro x) = s) ->
  (forall x s, In (x, s) b -> forall c, snd (ro x) c = cD x (hd 0 c)) ->
  forall t p, p < length b ->
  nth p (vals (as_of ro b) t) 0 = cD (fst (nth p b (0, []))) (nth p t 0).
Proof.
  intros ro b. induction b as [|[x s] b IH]; intros Hr Hsh Hdat t p Hp; [simpl in Hp; lia |].
  inversion Hr as [| ? ? Hx Hr']; subst. destruct Hx as (c & Hs & Hc). simpl in Hs. subst s.
  rewrite as_of_cons. cbn [vals].
  rewrite (Hsh x [c] (or_introl eq_refl)).
  assert (W : swidth [c] = 1) by (unfold swidth; simpl; lia).
  rewrite W.
  destruct p as [|p].
  - simpl nth. rewrite (Hdat x [c] (or_introl eq_refl)).
    destruct t; reflexivity.
  - simpl in Hp.
    pose proof (IH Hr' (fun y s' Hin => Hsh y s' (or_intror Hin))
                   (fun y s' Hin => Hdat y s' (or_intror Hin))) as IH'.
    destruct t as [|i t'].
    + simpl. rewrite (IH' [] p ltac:(lia)). destruct p; reflexivity.
    + simpl. rewrite (IH' t' p ltac:(lia)). reflexivity.
Qed.

Theorem surface_data_bridge : forall f ro b t,
  kwf f -> k_arity f = length b -> rank1 b ->
  (forall x s, In (x, s) b -> fst (ro x) = s) ->
  (forall x s, In (x, s) b -> forall c, snd (ro x) c = cD x (hd 0 c)) ->
  apply_kernel f (as_of ro b) t = k_body f (margs (out_shape true b) (sarr b) cD t).
Proof.
  intros f ro b t [Hloc _] Har Hr Hsh Hdat.
  destruct (out_shape_reads b Hr) as [_ R].
  unfold apply_kernel. apply Hloc. intros p Hp. rewrite Har in Hp.
  rewrite (vals_cantor ro b Hr Hsh Hdat t p Hp).
  unfold margs, marr. rewrite (R p Hp). reflexivity.
Qed.

(* ... so for a well-typed application in a Cantor-data environment, the *)
(* DENOTATION is the kernel at the call site of surface_comm_optimal     *)
Corollary surface_den_is_call_site : forall G rho xs f s' b,
  env_ok G rho -> has_ty G (EAppM (EMethod xs) f) (TComp s') ->
  bind_names G xs = Some b -> rank1 b ->
  (forall x s, In (x, s) b -> forall c, snd (rd rho x) c = cD x (hd 0 c)) ->
  forall t, snd (den rho (EAppM (EMethod xs) f)) t
            = k_body f (margs (out_shape true b) (sarr b) cD t).
Proof.
  intros G rho xs f s' b Hok Ht Hb Hr Hdat t.
  destruct (ty_appM_method _ _ _ _ Ht) as (b' & Hb' & Hwf & Har & _).
  rewrite Hb in Hb'. injection Hb' as <-.
  simpl. rewrite (bind_arrs_as_of G rho xs b Hok Hb). simpl.
  apply surface_data_bridge; try assumption.
  exact (binding_shapes G rho xs b Hok Hb).
Qed.

(* Without comm the kernel's H is trivial: every record of the meet is   *)
(* one position, the meet is BladeSurface's dense shape, and the dense   *)
(* count is FORCED for the class of all local kernels.  So               *)
(* comm_license_needed (nothing is quotiented undeclared) is optimal     *)
(* too: nothing CAN be quotiented undeclared.                            *)
Lemma dense_rank1_facts : forall b, rank1 b ->
  length (dense_shape b) = length b /\ arity (dense_shape b) = length b /\
  (forall p, p < length b -> sgrp 0 (dense_shape b) p = p).
Proof.
  induction b as [|[x s] b IH]; intro Hr.
  - split; [reflexivity | split; [reflexivity | intros p Hp; simpl in Hp; lia]].
  - inversion Hr as [| ? ? Hx Hr']; subst. destruct Hx as (c & Hs & Hc). simpl in Hs. subst s.
    destruct (IH Hr') as (I1 & I2 & I3).
    unfold dense_shape in *. simpl.
    split; [| split].
    + simpl in I1. rewrite I1. reflexivity.
    + unfold arity in *. simpl. rewrite Hc, I2. reflexivity.
    + intros p Hp. rewrite Hc. destruct p as [|p]; [reflexivity |].
      simpl. rewrite sgrp_shift, I3 by lia. lia.
Qed.

Theorem surface_nocomm_optimal :
  forall (b : list (nat * Shape)), rank1 b ->
  forall (O : Type) (out : O -> list nat -> nat) (P : prog (nat -> nat) nat O),
  (forall k, (forall v v', (forall p, p < length b -> v p = v' p) -> k v = k v') ->
     forall t, In t (enumShape (out_shape false b)) ->
     out (run P k) t = k (margs (out_shape false b) (fun j => fst (nth j b (0, []))) cD t)) ->
  forall f : (nat -> nat) -> nat,
  (forall v v', (forall p, p < length b -> v p = v' p) -> f v = f v') ->
  out_shape false b = dense_shape b /\
  shapeCard (dense_shape b) <= length (trace P f) /\
  length (trace (meet_prog (dense_shape b) (fun j => fst (nth j b (0, []))) cD) f)
    = shapeCard (dense_shape b) /\
  (forall t, sdense (dense_shape b) t ->
     meet_out (dense_shape b)
       (run (meet_prog (dense_shape b) (fun j => fst (nth j b (0, []))) cD) f) t
     = f (margs (dense_shape b) (fun j => fst (nth j b (0, []))) cD t)).
Proof.
  intros b Hr O out P Hcor f Hf.
  rewrite comm_license_needed in *.
  destruct (dense_rank1_facts b Hr) as (D1 & D2 & D3).
  assert (Hlaw : forall k, Klab (mR (dense_shape b)) (mlab (dense_shape b) (fun j => j)) k
                 <-> (forall v v', (forall p, p < length b -> v p = v' p) -> k v = k v')).
  { intro k. unfold mR, mlab. rewrite D2. split.
    - intros [Hloc _]. exact Hloc.
    - intro Hloc. split; [exact Hloc |].
      intros s s' [Hs _] Hl v. apply Hloc. intros p Hp.
      specialize (Hl p Hp). cbv beta in Hl. rewrite (D3 p Hp), (D3 (s p) (Hs p Hp)) in Hl.
      rewrite Hl. reflexivity. }
  assert (Hinj : forall a a', a < length (dense_shape b) -> a' < length (dense_shape b) ->
            (fun j : nat => j) a = (fun j : nat => j) a' ->
            (fun j => fst (nth j b (0, []))) a = (fun j => fst (nth j b (0, []))) a' -> a = a')
    by (intros a a' _ _ E _; exact E).
  destruct (uniform_optimality_subgroup (dense_shape b) (fun j => j)
              (fun j => fst (nth j b (0, []))) Hinj O out P) with (f := f)
    as (B1 & B2 & B3).
  - intros k Hk t Ht. apply Hcor; [apply Hlaw; exact Hk | exact Ht].
  - apply Hlaw. exact Hf.
  - split; [reflexivity | split; [exact B1 | split; [exact B2 | exact B3]]].
Qed.
(* --- the boundary of the surface rule: (A, B, A) -------------------- *)
(* Formalism 8.3 groups only NEIGHBOURING equal names, so method_for(A,  *)
(* B, A) <@> comm-kernel is stored dense (BladeSurface.aba_unlicensed). *)
(* The licence group H /\ Stab still swaps the two A positions, and a   *)
(* program that reorders the operands to (A, A, B) -- the meet          *)
(* [2; 1] -- is correct on EVERY dense cell with n * C(n + 1, 2)        *)
(* questions, fewer than n^3 once n >= 2 (aba_gap_strict).  Soundness  *)
(* of the dense store is BladeSurface's; what this file adds is that it *)
(* is not optimal.  The mismatch is INSIDE the formalism: the neighbour *)
(* rule of 8.3 and 11.1 grants strictly less than 11.2's exact          *)
(* H /\ Stab, and closing it needs a position-permuted storage layout.  *)
(* Mechanized at (A, B, A) only.                                        *)
Definition aba_args (t : list nat) : nat -> nat :=
  fun p => cD (match p with 1 => 1 | _ => 0 end) (nth p t 0).

(* the reordered call: positions (0, 2) read A at the first record,     *)
(* position 1 reads B at the second                                      *)
Definition aba_cell (c : list nat) : list nat := [nth 0 c 0; nth 2 c 0; nth 1 c 0].

Definition aba_prog (n : nat) : prog (nat -> nat) nat (list nat) :=
  ask_all (map (fun c => aba_args (aba_cell c)) (enumShape [mkIx 2 0 n; mkIx 1 0 n])) [].

Definition aba_out (n : nat) (o : list nat) (t : list nat) : nat :=
  nth (index_of (scanon [mkIx 2 0 n; mkIx 1 0 n] (aba_cell t))
                (enumShape [mkIx 2 0 n; mkIx 1 0 n])) o 0.

Theorem aba_beats_surface_rule : forall n,
  (forall f, Ksym 3 f -> forall t, length t = 3 -> (forall x, In x t -> x < n) ->
     aba_out n (run (aba_prog n) f) t = f (aba_args t)) /\
  (forall f, length (trace (aba_prog n) f) = n * C (n + 1) 2) /\
  shapeCard (out_shape true [(0, [mkIx 1 0 n]); (1, [mkIx 1 0 n]); (0, [mkIx 1 0 n])])
    = n * (n * n).
Proof.
  intro n. split; [| split].
  - intros f Hf t Hl Hb. unfold aba_out, aba_prog.
    rewrite ask_all_run. simpl rev. simpl app. rewrite map_map.
    destruct t as [|i [|j [|k [|x t']]]]; simpl in Hl; try discriminate.
    assert (Hi : i < n) by (apply Hb; simpl; tauto).
    assert (Hj : j < n) by (apply Hb; simpl; tauto).
    assert (Hk : k < n) by (apply Hb; simpl; tauto).
    assert (Hd : sdense [mkIx 2 0 n; mkIx 1 0 n] (aba_cell [i; j; k])).
    { simpl. repeat split; try lia; intros x Hx; simpl in Hx;
        destruct Hx as [<- | [<- | []]]; lia. }
    rewrite (nth_index_of nat (fun c => f (aba_args (aba_cell c))) 0)
      by (apply scanon_in; exact Hd).
    apply (Ksym_Kcls 3 f Hf). unfold scls. apply isort_respects.
    unfold aba_cell. cbn [scanon firstn skipn nth ix_r isort app].
    unfold insert. destruct (Nat.leb_spec i k) as [Hik | Hik].
    + apply Permutation_refl.
    + unfold tab, aba_args. simpl.
      exact (Permutation_rev [cD 0 k; cD 1 j; cD 0 i]).
  - intro f. unfold aba_prog. rewrite ask_all_trace, length_map.
    rewrite <- shapeCard_is_length. rewrite shapeCard_binom. simpl.
    rewrite Nat.mul_1_r. replace (n - 0 + 1 - 1) with n by lia.
    replace (n - 0 + 2 - 1) with (n + 1) by lia.
    rewrite C_n_1. lia.
  - rewrite aba_unlicensed. rewrite shapeCard_binom. simpl.
    replace (n - 0 + 1 - 1) with n by lia. rewrite C_n_1. lia.
Qed.

Example aba_gap_4 : 4 * C (4 + 1) 2 = 40 /\ 4 * (4 * 4) = 64.
Proof. vm_compute. split; reflexivity. Qed.

(* the gap is strict for every n >= 2, not only at the pin *)
Lemma C2_closed : forall n, 2 * C (S n) 2 = n * S n.
Proof.
  induction n as [|n IH]; [reflexivity |].
  change (C (S (S n)) 2) with (C (S n) 1 + C (S n) 2). rewrite C_n_1. nia.
Qed.

Theorem aba_gap_strict : forall n, 2 <= n -> n * C (n + 1) 2 < n * (n * n).
Proof.
  intros n Hn. replace (n + 1) with (S n) by lia.
  pose proof (C2_closed n) as E.
  assert (C (S n) 2 < n * n) by nia.
  apply Nat.mul_lt_mono_pos_l; lia.
Qed.

(* aba_args is the call site of the surface rule's convention: the      *)
(* binding (A, B, A) read through out_shape true and sarr, as in        *)
(* surface_comm_optimal                                                  *)
Definition b_aba (n : nat) : list (nat * Shape) :=
  [(0, [mkIx 1 0 n]); (1, [mkIx 1 0 n]); (0, [mkIx 1 0 n])].

Lemma aba_args_is_call_site : forall n t p, p < 3 ->
  aba_args t p = margs (out_shape true (b_aba n)) (sarr (b_aba n)) cD t p.
Proof.
  intros n t p Hp. destruct p as [|[|[|p]]]; [reflexivity | reflexivity | reflexivity | lia].
Qed.

(* ... and n * C(n + 1, 2) is FORCED there: a program correct in the    *)
(* position order (A, B, A) is, read through the reorder, correct for   *)
(* the meet [2; 1], so uniform_optimality_subgroup bounds it.           *)
Theorem aba_orbit_count_forced :
  forall (n : nat) (O : Type) (out : O -> list nat -> nat) (P : prog (nat -> nat) nat O),
  (forall f, Ksym 3 f -> forall t, length t = 3 -> (forall x, In x t -> x < n) ->
     out (run P f) t = f (aba_args t)) ->
  forall f, Ksym 3 f -> n * C (n + 1) 2 <= length (trace P f).
Proof.
  intros n O out P Hcor f Hf.
  set (s := [mkIx 2 0 n; mkIx 1 0 n]).
  assert (Hlaw : forall k, Klab (mR s) (mlab s (fun _ => 0)) k <-> Ksym 3 k)
    by (intro k; apply meet_full_kernel_is_Ksym).
  assert (Hinj : forall a b, a < length s -> b < length s ->
            (fun _ : nat => 0) a = (fun _ : nat => 0) b -> (fun j : nat => j) a = (fun j => j) b ->
            a = b) by (intros a b _ _ _ E; exact E).
  destruct (uniform_optimality_subgroup s (fun _ => 0) (fun j => j) Hinj O
              (fun o c => out o (aba_cell c)) P) with (f := f) as [B _].
  - intros k Hk t Ht. apply Hlaw in Hk.
    pose proof (enumShape_tuple_length _ _ Ht) as Hl.
    pose proof (enumShape_shape_dense _ _ Ht) as Hd.
    destruct t as [|i [|j [|k' [|x t']]]]; simpl in Hl; try discriminate.
    simpl in Hd. destruct Hd as [[_ HF1] [[_ HF2] _]].
    inversion HF1 as [| ? ? Hi HF1']; subst. inversion HF1' as [| ? ? Hj _]; subst.
    inversion HF2 as [| ? ? Hk' _]; subst.
    rewrite Hcor; [| exact Hk | reflexivity |].
    + apply (Ksym_Kcls 3 k Hk). unfold scls. apply isort_respects.
      unfold tab, aba_args, aba_cell, margs, marr. simpl.
      apply perm_skip. apply perm_swap.
    + intros x Hx. simpl in Hx. destruct Hx as [<- | [<- | [<- | []]]]; lia.
  - apply Hlaw. exact Hf.
  - unfold s in B. rewrite shapeCard_binom in B. simpl in B.
    replace (n - 0 + 2 - 1) with (n + 1) in B by lia.
    replace (n - 0 + 1 - 1) with n in B by lia.
    rewrite C_n_1 in B. lia.
Qed.

(* ===================================================================== *)
(* PART B.  THE SIGNED BOUND AT GENERAL r.  An ALTERNATING kernel: local *)
(* and anti-invariant (neg = Z.opp, BladeLowering.antiinvariant_under)   *)
(* under every ADJACENT transposition sw i of positions i, i + 1 < r.    *)
(* Adjacent transpositions generate S_r; nothing else is assumed.  The   *)
(* vanishing theorem walks one entry of a repeated pair next to the      *)
(* other by adjacent swaps; the attaining program's sign is the parity   *)
(* of the adjacent swaps insertion sort performs, shown equal to the     *)
(* inversion count; and Kalt_sign_character derives from the same sort   *)
(* that Kalt IS the full sign character on every position permutation.  *)
(* ===================================================================== *)

Definition sw (i p : nat) : nat :=
  if p =? i then S i else if p =? S i then i else p.

Definition Kalt (r : nat) (f : (nat -> nat) -> Z) : Prop :=
  (forall v v', (forall p, p < r -> v p = v' p) -> f v = f v') /\
  (forall i, S i < r -> antiinvariant_under nat Z f Z.opp (sw i)).

Lemma sw_lo : forall i p, p < i -> sw i p = p.
Proof.
  intros i p H. unfold sw.
  destruct (Nat.eqb_spec p i); [lia |]. destruct (Nat.eqb_spec p (S i)); [lia | reflexivity].
Qed.

Lemma sw_hi : forall i p, S i < p -> sw i p = p.
Proof.
  intros i p H. unfold sw.
  destruct (Nat.eqb_spec p i); [lia |]. destruct (Nat.eqb_spec p (S i)); [lia | reflexivity].
Qed.

Lemma sw_i : forall i, sw i i = S i.
Proof. intro i. unfold sw. rewrite Nat.eqb_refl. reflexivity. Qed.

Lemma sw_Si : forall i, sw i (S i) = i.
Proof.
  intro i. unfold sw. destruct (Nat.eqb_spec (S i) i); [lia |].
  rewrite Nat.eqb_refl. reflexivity.
Qed.

Lemma sw_invol : forall i p, sw i (sw i p) = p.
Proof.
  intros i p. destruct (lt_eq_lt_dec p i) as [[Hp | Hp] | Hp].
  - rewrite (sw_lo i p Hp). rewrite (sw_lo i p Hp). reflexivity.
  - subst. rewrite sw_i, sw_Si. reflexivity.
  - destruct (Nat.eq_dec p (S i)) as [-> | Hn].
    + rewrite sw_Si, sw_i. reflexivity.
    + rewrite (sw_hi i p ltac:(lia)). rewrite (sw_hi i p ltac:(lia)). reflexivity.
Qed.

Lemma sw_perm_pair : forall r i, S i < r -> perm_pair r (sw i) (sw i).
Proof.
  intros r i Hi. repeat split.
  - intros p Hp. unfold sw. destruct (p =? i); [lia |]. destruct (p =? S i); lia.
  - intros p Hp. unfold sw. destruct (p =? i); [lia |]. destruct (p =? S i); lia.
  - intros p _. apply sw_invol.
  - intros p _. apply sw_invol.
Qed.

(* --- (1) the forced zeros: repeated entries --------------------------- *)

Section Alternating.
  Variable r : nat.
  Variable f : (nat -> nat) -> Z.
  Hypothesis Hf : Kalt r f.

  Lemma alt_adjacent_zero : forall v i, S i < r -> v i = v (S i) -> f v = 0%Z.
  Proof.
    intros v i Hi E. destruct Hf as [Hloc Hanti].
    pose proof (Hanti i Hi v) as H.
    rewrite (Hloc (fun p => v (sw i p)) v) in H; [lia |].
    intros p _. destruct (lt_eq_lt_dec p i) as [[Hp | Hp] | Hp].
    - rewrite sw_lo by exact Hp. reflexivity.
    - subst. rewrite sw_i. symmetry. exact E.
    - destruct (Nat.eq_dec p (S i)) as [-> | Hn].
      + rewrite sw_Si. exact E.
      + rewrite sw_hi by lia. reflexivity.
  Qed.

  (* Every alternating kernel vanishes on ANY tuple with two equal       *)
  (* entries (not only adjacent ones): move the later one down by         *)
  (* adjacent swaps, each of which only flips the sign.                   *)
  Theorem alt_vanishes : forall v a b, a < b < r -> v a = v b -> f v = 0%Z.
  Proof.
    intros v a b Hab. remember (b - a - 1) as m eqn:Hm.
    revert v b Hab Hm. induction m as [|m IH]; intros v b Hab Hm E.
    - replace b with (S a) in * by lia. apply (alt_adjacent_zero v a); [lia | exact E].
    - destruct Hf as [Hloc Hanti].
      set (w := fun p => v (sw (b - 1) p)).
      assert (Hw : f w = 0%Z).
      { apply (IH w (b - 1)); [lia | lia |].
        unfold w. rewrite sw_lo by lia.
        replace (b - 1) with (pred b) by lia.
        replace (sw (pred b) (pred b)) with b
          by (rewrite sw_i; lia).
        exact E. }
      pose proof (Hanti (b - 1) ltac:(lia) v) as HA.
      fold w in HA. lia.
  Qed.
End Alternating.

(* --- the sign of a list: parity of its inversions --------------------- *)

Definition par (m : nat) : Z := if Nat.even m then 1%Z else (-1)%Z.

Lemma par_S : forall m, par (S m) = (- par m)%Z.
Proof.
  intro m. unfold par. rewrite Nat.even_succ, <- Nat.negb_even.
  destruct (Nat.even m); reflexivity.
Qed.

Lemma par_add : forall a b, par (a + b) = (par a * par b)%Z.
Proof.
  induction a as [|a IH]; intro b.
  - change (0 + b) with b. change (par 0) with 1%Z. ring.
  - change (S a + b) with (S (a + b)). rewrite !par_S, IH. ring.
Qed.

Lemma par_nonzero : forall m, par m <> 0%Z.
Proof. intro m. unfold par. destruct (Nat.even m); discriminate. Qed.

Definition cntlt (x : nat) (l : list nat) : nat := length (filter (fun y => y <? x) l).

Fixpoint inv (l : list nat) : nat :=
  match l with
  | [] => 0
  | x :: l' => cntlt x l' + inv l'
  end.

(* a TRANSPARENT duplicate test, so that signed reads compute *)
Fixpoint nodupb (l : list nat) : bool :=
  match l with
  | [] => true
  | x :: l' => negb (existsb (Nat.eqb x) l') && nodupb l'
  end.

Lemma nodupb_spec : forall l, reflect (NoDup l) (nodupb l).
Proof.
  induction l as [|x l IH]; simpl.
  - constructor. constructor.
  - destruct (existsb (Nat.eqb x) l) eqn:E; simpl.
    + constructor. intro H. inversion H as [| ? ? Hx _]; subst.
      apply existsb_exists in E as (y & Hy & Ey). apply Nat.eqb_eq in Ey. subst y.
      contradiction.
    + destruct IH as [Hn | Hn]; constructor.
      * constructor; [| exact Hn]. intro Hx.
        assert (existsb (Nat.eqb x) l = true)
          by (apply existsb_exists; exists x; split; [exact Hx | apply Nat.eqb_refl]).
        congruence.
      * intro H. inversion H; subst. contradiction.
Qed.

Definition sgn (l : list nat) : Z :=
  if nodupb l then par (inv l) else 0%Z.

Lemma cntlt_perm : forall x l l', Permutation l l' -> cntlt x l = cntlt x l'.
Proof. intros x l l' H. unfold cntlt. apply Permutation_length. apply perm_filter. exact H. Qed.

Lemma par_flip : forall m m', m = S m' \/ m' = S m -> par m = (- par m')%Z.
Proof. intros m m' [-> | ->]; rewrite par_S; lia. Qed.

Lemma par_inv_swap : forall pre a b post, a <> b ->
  par (inv (pre ++ b :: a :: post)) = (- par (inv (pre ++ a :: b :: post)))%Z.
Proof.
  induction pre as [|x pre IH]; intros a b post Hab; simpl.
  - unfold cntlt. simpl.
    destruct (Nat.ltb_spec a b), (Nat.ltb_spec b a); try lia; simpl; apply par_flip; lia.
  - rewrite (cntlt_perm x (pre ++ b :: a :: post) (pre ++ a :: b :: post))
      by (apply Permutation_app_head; apply perm_swap).
    rewrite !par_add, IH by exact Hab. lia.
Qed.

Lemma sgn_swap : forall pre a b post,
  sgn (pre ++ b :: a :: post) = (- sgn (pre ++ a :: b :: post))%Z.
Proof.
  intros pre a b post. unfold sgn.
  assert (HP : Permutation (pre ++ a :: b :: post) (pre ++ b :: a :: post))
    by (apply Permutation_app_head; apply perm_swap).
  destruct (Nat.eq_dec a b) as [<- | Hab].
  - destruct (nodupb_spec (pre ++ a :: a :: post)) as [Hn | Hn]; [| reflexivity].
    exfalso. apply NoDup_remove_2 in Hn. apply Hn. apply in_or_app. right. left. reflexivity.
  - destruct (nodupb_spec (pre ++ b :: a :: post)) as [Hn | Hn];
      destruct (nodupb_spec (pre ++ a :: b :: post)) as [Hn' | Hn'].
    + apply par_inv_swap. exact Hab.
    + exfalso. apply Hn'. apply (Permutation_NoDup (Permutation_sym HP)). exact Hn.
    + exfalso. apply Hn. apply (Permutation_NoDup HP). exact Hn'.
    + reflexivity.
Qed.

(* --- tabulations around an adjacent swap ------------------------------ *)

Lemma tab_split : forall r i v, S i < r ->
  tab r v = map v (seq 0 i) ++ v i :: v (S i) :: map v (seq (S (S i)) (r - S (S i))).
Proof.
  intros r i v Hi. unfold tab.
  replace r with (i + S (S (r - S (S i)))) at 1 by lia.
  rewrite seq_app, map_app. reflexivity.
Qed.

Lemma tab_sw : forall r i v, S i < r ->
  tab r (fun p => v (sw i p))
  = map v (seq 0 i) ++ v (S i) :: v i :: map v (seq (S (S i)) (r - S (S i))).
Proof.
  intros r i v Hi. rewrite (tab_split r i _ Hi). rewrite sw_i, sw_Si.
  f_equal; [| f_equal; f_equal].
  - apply map_ext_in. intros p Hp. apply in_seq in Hp. rewrite sw_lo by lia. reflexivity.
  - apply map_ext_in. intros p Hp. apply in_seq in Hp. rewrite sw_hi by lia. reflexivity.
Qed.

Lemma sgn_tab_sw : forall r i v, S i < r ->
  sgn (tab r (fun p => v (sw i p))) = (- sgn (tab r v))%Z.
Proof.
  intros r i v Hi. rewrite tab_sw, (tab_split r i v) by exact Hi. apply sgn_swap.
Qed.

(* --- the strict enumeration: BladeArrow's antisymmetric arrow --------- *)

Definition strict (n r : nat) : list (list nat) :=
  enumA nat (anti_heads n) anti_step r 0.

Lemma enumA_anti_cons : forall n r l, l < n ->
  enumA nat (anti_heads n) anti_step (S r) l
  = map (cons l) (enumA nat (anti_heads n) anti_step r (S l))
    ++ enumA nat (anti_heads n) anti_step (S r) (S l).
Proof.
  intros n r l Hl.
  change (enumA nat (anti_heads n) anti_step (S r) l)
    with (flat_map (fun i => map (cons i) (enumA nat (anti_heads n) anti_step r (S i)))
                   (seq l (n - l))).
  change (enumA nat (anti_heads n) anti_step (S r) (S l))
    with (flat_map (fun i => map (cons i) (enumA nat (anti_heads n) anti_step r (S i)))
                   (seq (S l) (n - S l))).
  replace (n - l) with (S (n - S l)) by lia. reflexivity.
Qed.

Lemma strict_length_gen : forall n k r l, n - l = k ->
  length (enumA nat (anti_heads n) anti_step r l) = C k r.
Proof.
  intro n. induction k as [|k IH]; intros r l Hk; destruct r as [|r].
  - reflexivity.
  - change (enumA nat (anti_heads n) anti_step (S r) l)
      with (flat_map (fun i => map (cons i) (enumA nat (anti_heads n) anti_step r (S i)))
                     (seq l (n - l))).
    rewrite Hk. reflexivity.
  - rewrite C_zero. reflexivity.
  - rewrite enumA_anti_cons by lia.
    rewrite length_app, length_map.
    rewrite (IH r (S l)) by lia. rewrite (IH (S r) (S l)) by lia.
    reflexivity.
Qed.

Theorem strict_length : forall n r, length (strict n r) = C n r.
Proof. intros n r. unfold strict. apply strict_length_gen. lia. Qed.

Lemma scanonical_canonical : forall u r l t,
  scanonical u r l t -> canonical r l u t.
Proof.
  intros u. induction r as [|r IH]; intros l t H; destruct t as [|i t']; simpl in *;
    try contradiction; [exact I |].
  destruct H as [Hb H]. split; [exact Hb |].
  apply (canonical_weaken r (S i) i u t' (IH (S i) t' H)). lia.
Qed.

Lemma scanonical_bounds : forall u r l t,
  scanonical u r l t -> forall x, In x t -> l <= x < u.
Proof.
  intros u. induction r as [|r IH]; intros l t H x Hx; destruct t as [|i t']; simpl in *;
    try contradiction.
  destruct H as [Hb H]. destruct Hx as [<- | Hx]; [exact Hb |].
  specialize (IH _ _ H x Hx). lia.
Qed.

Lemma scanonical_NoDup : forall u r l t, scanonical u r l t -> NoDup t.
Proof.
  intros u. induction r as [|r IH]; intros l t H; destruct t as [|i t']; simpl in *;
    try contradiction; [constructor |].
  destruct H as [Hb H]. constructor.
  - intro Hin. pose proof (scanonical_bounds u r (S i) t' H i Hin). lia.
  - exact (IH _ _ H).
Qed.

Lemma scanonical_length : forall u r l t, scanonical u r l t -> length t = r.
Proof.
  intros u. induction r as [|r IH]; intros l t H; destruct t as [|i t']; simpl in *;
    try contradiction; [reflexivity |].
  destruct H as [_ H]. f_equal. exact (IH _ _ H).
Qed.

(* --- sorting by adjacent swaps; its parity is the inversion parity ---- *)

Fixpoint ins_sw (x : nat) (l : list nat) : nat :=
  match l with
  | [] => 0
  | y :: l' => if x <=? y then 0 else S (ins_sw x l')
  end.

Fixpoint isort_sw (l : list nat) : nat :=
  match l with
  | [] => 0
  | x :: l' => ins_sw x (isort l') + isort_sw l'
  end.

Lemma nondecr_cons : forall y L, nondecr L ->
  (forall h L', L = h :: L' -> y <= h) -> nondecr (y :: L).
Proof.
  intros y [|h L'] HL H; simpl; [exact I |].
  split; [apply (H h L' eq_refl) | exact HL].
Qed.

Lemma insert_nondecr : forall x s, nondecr s -> nondecr (insert x s).
Proof.
  intro x. induction s as [|y s IH]; intro H; simpl; [exact I |].
  destruct (Nat.leb_spec x y).
  - change (nondecr (x :: y :: s)). simpl. split; [lia |]. exact H.
  - apply nondecr_cons; [apply IH; exact (nondecr_tail y s H) |].
    intros h L' E. destruct s as [|z s'].
    + simpl in E. injection E as <- _. lia.
    + simpl in E. destruct (Nat.leb_spec x z).
      * injection E as <- _. lia.
      * injection E as <- _. simpl in H. lia.
Qed.

Lemma isort_nondecr : forall l, nondecr (isort l).
Proof.
  induction l as [|x l IH]; simpl; [exact I |]. apply insert_nondecr. exact IH.
Qed.

Lemma ins_sw_cnt : forall x s, nondecr s -> ins_sw x s = cntlt x s.
Proof.
  intro x. induction s as [|y s IH]; intro H; [reflexivity |].
  unfold cntlt. simpl.
  destruct (Nat.leb_spec x y).
  - destruct (Nat.ltb_spec y x); [lia |].
    rewrite filter_none; [reflexivity |].
    intros z Hz. pose proof (nondecr_lb y s H) as HF. rewrite Forall_forall in HF.
    specialize (HF z Hz). apply Nat.ltb_ge. lia.
  - destruct (Nat.ltb_spec y x); [| lia]. simpl. f_equal.
    apply IH. exact (nondecr_tail y s H).
Qed.

Lemma isort_sw_inv : forall l, isort_sw l = inv l.
Proof.
  induction l as [|x l IH]; [reflexivity |]. simpl.
  rewrite IH, ins_sw_cnt by apply isort_nondecr.
  f_equal. symmetry. apply cntlt_perm. apply isort_perm.
Qed.

(* nth around an adjacent swap in the middle of a list *)
Lemma nth_sw_mid : forall pre a b post p,
  nth (sw (length pre) p) (pre ++ a :: b :: post) 0 = nth p (pre ++ b :: a :: post) 0.
Proof.
  intros pre a b post p.
  destruct (lt_eq_lt_dec p (length pre)) as [[Hp | Hp] | Hp].
  - rewrite sw_lo by exact Hp. rewrite !app_nth1 by exact Hp. reflexivity.
  - subst. rewrite sw_i. rewrite !app_nth2 by lia.
    replace (S (length pre) - length pre) with 1 by lia.
    rewrite Nat.sub_diag. reflexivity.
  - destruct (Nat.eq_dec p (S (length pre))) as [-> | Hn].
    + rewrite sw_Si. rewrite !app_nth2 by lia.
      replace (S (length pre) - length pre) with 1 by lia.
      rewrite Nat.sub_diag. reflexivity.
    + rewrite sw_hi by lia. rewrite !app_nth2 by lia.
      destruct (p - length pre) as [|[|m]] eqn:E; [lia | lia | reflexivity].
Qed.

Section AltSort.
  Variable r : nat.
  Variable f : (nat -> nat) -> Z.
  Hypothesis Hf : Kalt r f.
  Variable g : nat -> nat.

  Definition FL (l : list nat) : Z := f (fun p => g (nth p l 0)).

  Lemma FL_swap : forall pre a b post, length (pre ++ a :: b :: post) = r ->
    FL (pre ++ b :: a :: post) = (- FL (pre ++ a :: b :: post))%Z.
  Proof.
    intros pre a b post Hl. destruct Hf as [Hloc Hanti].
    rewrite length_app in Hl. simpl in Hl.
    pose proof (Hanti (length pre) ltac:(lia) (fun p => g (nth p (pre ++ a :: b :: post) 0))) as H.
    unfold FL. rewrite <- H. apply Hloc. intros p _. rewrite nth_sw_mid. reflexivity.
  Qed.

  Lemma FL_insert : forall x l pre, length (pre ++ x :: l) = r ->
    FL (pre ++ x :: l) = (par (ins_sw x l) * FL (pre ++ insert x l))%Z.
  Proof.
    intro x. induction l as [|y l IH]; intros pre Hl.
    - cbn [ins_sw insert]. change (par 0) with 1%Z. ring.
    - cbn [ins_sw insert]. destruct (Nat.leb_spec x y).
      + change (par 0) with 1%Z. ring.
      + rewrite (FL_swap pre y x l)
          by (rewrite length_app in *; simpl in *; lia).
        replace (pre ++ y :: x :: l) with ((pre ++ [y]) ++ x :: l)
          by (rewrite <- app_assoc; reflexivity).
        rewrite IH by (rewrite <- app_assoc; rewrite length_app in *; simpl in *; lia).
        rewrite <- app_assoc. change ([y] ++ insert x l) with (y :: insert x l).
        rewrite par_S. ring.
  Qed.

  Lemma FL_isort : forall l pre, length (pre ++ l) = r ->
    FL (pre ++ l) = (par (isort_sw l) * FL (pre ++ isort l))%Z.
  Proof.
    induction l as [|x l IH]; intros pre Hl.
    - cbn [isort_sw isort]. change (par 0) with 1%Z. ring.
    - cbn [isort_sw isort].
      replace (pre ++ x :: l) with ((pre ++ [x]) ++ l) by (rewrite <- app_assoc; reflexivity).
      rewrite IH by (rewrite <- app_assoc; exact Hl).
      rewrite <- app_assoc. change ([x] ++ isort l) with (x :: isort l).
      rewrite FL_insert
        by (rewrite length_app in *; simpl in *; rewrite isort_length; lia).
      rewrite par_add. ring.
  Qed.

  (* THE SIGN CHARACTER on sorting permutations: an alternating kernel at *)
  (* any tuple is the inversion sign times its value at the sorted tuple.  *)
  Theorem alt_sort_sign : forall l, length l = r ->
    FL l = (par (inv l) * FL (isort l))%Z.
  Proof.
    intros l Hl. rewrite <- isort_sw_inv. apply (FL_isort l []). exact Hl.
  Qed.
End AltSort.

Lemma strict_of_sorted : forall u t l,
  nondecr t -> NoDup t -> (forall x, In x t -> l <= x < u) ->
  scanonical u (length t) l t.
Proof.
  intro u. induction t as [|i t IH]; intros l Hnd Hn Hb; simpl; [exact I |].
  split; [apply Hb; left; reflexivity |].
  apply IH.
  - exact (nondecr_tail i t Hnd).
  - inversion Hn; assumption.
  - intros x Hx. pose proof (nondecr_lb i t Hnd) as HF. rewrite Forall_forall in HF.
    specialize (HF x Hx). inversion Hn as [| ? ? Hni _]; subst.
    assert (x <> i) by (intro E; subst; contradiction).
    assert (x < u) by (apply Hb; right; exact Hx). lia.
Qed.

Lemma not_NoDup_dup : forall l, ~ NoDup l ->
  exists a b, a < b < length l /\ nth a l 0 = nth b l 0.
Proof.
  induction l as [|x l IH]; intro H.
  - exfalso. apply H. constructor.
  - destruct (in_dec Nat.eq_dec x l) as [Hin | Hnin].
    + apply (In_nth l x 0) in Hin as (m & Hm & E).
      exists 0, (S m). simpl. split; [lia | symmetry; exact E].
    + destruct IH as (a & b & Hab & E).
      * intro Hn. apply H. constructor; assumption.
      * exists (S a), (S b). simpl. split; [lia | exact E].
Qed.

(* --- (2) the lower bound and (3) attainment, one array ---------------- *)

Section AltNest.
  Variable r : nat.
  Variable dat : nat -> nat.
  Hypothesis dat_inj : forall i j, dat i = dat j -> i = j.

  Lemma alt_perturbable : forall n x, In x (strict n r) ->
    perturbable (nat -> nat) (list nat) (list nat) Z (scls r) (Kalt r) (argsOf dat) x.
  Proof.
    intros n x Hx.
    unfold strict in Hx. apply antisym_enum_sound in Hx.
    exists (fun k v => (k v + if list_eq_dec Nat.eq_dec (scls r v) (scls r (argsOf dat x))
                              then sgn (tab r v) else 0)%Z).
    split; [| split].
    - intros k [Hloc Hanti]. split.
      + intros v v' Hv. rewrite (Hloc v v' Hv).
        rewrite (scls_local r v v' Hv).
        assert (Et : tab r v = tab r v').
        { unfold tab. apply map_ext_in. intros a Ha. apply in_seq in Ha. apply Hv. lia. }
        rewrite Et. reflexivity.
      + intros i Hi v.
        rewrite (scls_perm r (sw i) (sw i) (sw_perm_pair r i Hi) v).
        rewrite (sgn_tab_sw r i v Hi). rewrite (Hanti i Hi v).
        destruct (list_eq_dec Nat.eq_dec (scls r v) (scls r (argsOf dat x))); lia.
    - intros k v Hne.
      destruct (list_eq_dec Nat.eq_dec (scls r v) (scls r (argsOf dat x)));
        [contradiction | lia].
    - intro k.
      destruct (list_eq_dec Nat.eq_dec (scls r (argsOf dat x)) (scls r (argsOf dat x)))
        as [_ | N]; [| contradiction].
      rewrite tab_argsOf by exact (scanonical_length _ _ _ _ Hx).
      unfold sgn.
      destruct (nodupb_spec (map dat x)) as [_ | Hn].
      + pose proof (par_nonzero (inv (map dat x))). lia.
      + exfalso. apply Hn. apply Injective_map_NoDup.
        * intros a b E. apply dat_inj. exact E.
        * exact (scanonical_NoDup _ _ _ _ Hx).
  Qed.

  (* T1, signed, general r: C(n, r) queries are forced. *)
  Theorem antisym_lower_bound_r :
    forall (n : nat) (O : Type) (out : O -> list nat -> Z)
           (P : prog (nat -> nat) Z O),
    (forall f, Kalt r f -> forall x, In x (strict n r) ->
       out (run P f) x = f (argsOf dat x)) ->
    forall f, Kalt r f -> C n r <= length (trace P f).
  Proof.
    intros n O out P Hcor f Hf.
    rewrite <- strict_length.
    apply (orbit_lower_bound (nat -> nat) (list nat) (list nat) Z O
             (scls r) (list_eq_dec Nat.eq_dec) (Kalt r) (argsOf dat) out
             (strict n r) P).
    - apply NoDup_map_inj; [apply antisym_enum_NoDup |].
      intros x y Hx Hy E. unfold strict in Hx, Hy.
      apply antisym_enum_sound in Hx. apply antisym_enum_sound in Hy.
      apply (scls_args_inj r dat dat_inj n x y); [| | exact E];
        apply scanonical_canonical; assumption.
    - intros x Hx. apply (alt_perturbable n x Hx).
    - exact Hcor.
    - exact Hf.
  Qed.


  (* T1', signed: the same count bounds any opaque-cell store, so         *)
  (* AntisymIdx storing C(n, r) cells and no diagonal is forced.          *)
  Theorem antisym_storage_bound_r :
    forall (n : nat) (cells : list (nat -> nat)) (acc : list nat -> nat),
    (forall f, Kalt r f -> forall x, In x (strict n r) ->
       nth (acc x) (map f cells) 0%Z = f (argsOf dat x)) ->
    C n r <= length cells.
  Proof.
    intros n cells acc Hread.
    rewrite <- strict_length.
    apply (orbit_storage_bound (nat -> nat) (list nat) (list nat) Z
             (scls r) (list_eq_dec Nat.eq_dec) (Kalt r) (argsOf dat)
             (strict n r) cells acc 0%Z (fun _ => 0%Z)).
    - split; [intros v v' _; reflexivity | intros i _ v; reflexivity].
    - apply NoDup_map_inj; [apply antisym_enum_NoDup |].
      intros x y Hx Hy E. unfold strict in Hx, Hy.
      apply antisym_enum_sound in Hx. apply antisym_enum_sound in Hy.
      apply (scls_args_inj r dat dat_inj n x y); [| | exact E];
        apply scanonical_canonical; assumption.
    - intros x Hx. apply (alt_perturbable n x Hx).
    - exact Hread.
  Qed.

  (* T2, signed, general r: the strict enumeration attains it and serves *)
  (* every cell of the dense box: sort the index tuple, look it up, and   *)
  (* multiply by the parity of its inversions; a repeated index is 0.     *)
  Definition alt_prog (n : nat) : prog (nat -> nat) Z (list Z) :=
    ask_all (map (argsOf dat) (strict n r)) [].

  Definition alt_out (n : nat) (o : list Z) (ix : list nat) : Z :=
    if nodupb ix
    then (par (inv ix) * nth (index_of (isort ix) (strict n r)) o 0)%Z
    else 0%Z.

  Theorem antisym_attained_r : forall n,
    (forall f, Kalt r f -> forall ix, length ix = r -> (forall x, In x ix -> x < n) ->
       alt_out n (run (alt_prog n) f) ix = f (argsOf dat ix)) /\
    (forall f, length (trace (alt_prog n) f) = C n r).
  Proof.
    intro n. split.
    - intros f Hf ix Hl Hb. unfold alt_out, alt_prog.
      rewrite ask_all_run. simpl rev. simpl app. rewrite map_map.
      destruct (nodupb_spec ix) as [Hn | Hn].
      + rewrite (nth_index_of Z (fun t => f (argsOf dat t)) 0%Z).
        * symmetry. exact (alt_sort_sign r f Hf dat ix Hl).
        * unfold strict. apply antisym_enum_complete.
          rewrite <- Hl, <- (isort_length ix).
          apply strict_of_sorted.
          -- apply isort_nondecr.
          -- exact (Permutation_NoDup (isort_perm ix) Hn).
          -- intros x Hx. split; [lia |]. apply Hb.
             apply (Permutation_in x (Permutation_sym (isort_perm ix))). exact Hx.
      + destruct (not_NoDup_dup ix Hn) as (a & b & Hab & E).
        symmetry. apply (alt_vanishes r f Hf (argsOf dat ix) a b); [lia |].
        unfold argsOf. rewrite E. reflexivity.
    - intro f. unfold alt_prog. rewrite ask_all_trace, length_map.
      apply strict_length.
  Qed.

  Corollary uniform_optimality_alt :
    forall (n : nat) (O : Type) (out : O -> list nat -> Z)
           (P : prog (nat -> nat) Z O),
    (forall f, Kalt r f -> forall x, In x (strict n r) ->
       out (run P f) x = f (argsOf dat x)) ->
    forall f, Kalt r f ->
      length (trace (alt_prog n) f) <= length (trace P f).
  Proof.
    intros n O out P Hcor f Hf.
    rewrite (proj2 (antisym_attained_r n) f).
    exact (antisym_lower_bound_r n O out P Hcor f Hf).
  Qed.
End AltNest.

(* Closed: the identity is generic data. *)
Theorem uniform_optimality_alt_nat :
  forall (r n : nat) (O : Type) (out : O -> list nat -> Z) (P : prog (nat -> nat) Z O),
  (forall f, Kalt r f -> forall x, In x (strict n r) ->
     out (run P f) x = f (argsOf (fun i => i) x)) ->
  forall f, Kalt r f ->
    C n r <= length (trace P f) /\
    length (trace (alt_prog r (fun i => i) n) f) = C n r.
Proof.
  intros r n O out P Hcor f Hf. split.
  - exact (antisym_lower_bound_r r (fun i => i) (fun i j E => E) n O out P Hcor f Hf).
  - exact (proj2 (antisym_attained_r r (fun i => i) n) f).
Qed.

(* at r = 2 the alternating class is BladeOptimal's Kanti *)
Theorem Kalt_2_is_Kanti : forall f, Kalt 2 f <-> Kanti f.
Proof.
  intro f. split.
  - intros [Hloc Hanti]. split.
    + intros v v' E0 E1. apply Hloc. intros p Hp.
      destruct p as [|[|p]]; [exact E0 | exact E1 | lia].
    + intro v. rewrite <- (Hanti 0 ltac:(lia) v). apply Hloc.
      intros p Hp. destruct p as [|[|p]]; [reflexivity | reflexivity | lia].
  - intros [Hloc Hanti]. split.
    + intros v v' H. apply Hloc; apply H; lia.
    + intros i Hi v. assert (i = 0) by lia. subst i.
      rewrite <- (Hanti v). apply Hloc; reflexivity.
Qed.


(* --- the FULL sign character, from the adjacent generators only ------- *)

Lemma seq_canonical : forall r l, canonical r l (l + r) (seq l r).
Proof.
  induction r as [|r IH]; intro l; simpl; [exact I |].
  split; [lia |].
  apply (canonical_weaken r (S l) l (l + S r) (seq (S l) r)); [| lia].
  replace (l + S r) with (S l + r) by lia. apply IH.
Qed.

Lemma nth_map_seq : forall (s : nat -> nat) r p, p < r -> nth p (map s (seq 0 r)) 0 = s p.
Proof.
  intros s r p Hp.
  rewrite (nth_indep _ 0 (s 0)) by (rewrite length_map, length_seq; exact Hp).
  rewrite map_nth, seq_nth by exact Hp. reflexivity.
Qed.

(* Kalt is the sign character: for EVERY position permutation s (a      *)
(* perm_pair), f (v o s) = sgn(s) * f v, where sgn(s) is the parity of  *)
(* the inversions of s read as the list s 0, ..., s (r - 1).            *)
Theorem Kalt_sign_character : forall r f, Kalt r f ->
  forall s s', perm_pair r s s' ->
  forall v, f (fun p => v (s p)) = (par (inv (map s (seq 0 r))) * f v)%Z.
Proof.
  intros r f Hf s s' Hp v.
  pose proof (alt_sort_sign r f Hf v (map s (seq 0 r))
                (ltac:(rewrite length_map, length_seq; reflexivity))) as H.
  unfold FL in H.
  assert (Hs : isort (map s (seq 0 r)) = seq 0 r).
  { rewrite (isort_respects _ _ (map_s_perm r s s' Hp)).
    apply (isort_canonical r 0 r). exact (seq_canonical r 0). }
  rewrite Hs in H.
  destruct Hf as [Hloc _].
  rewrite (Hloc (fun p => v (nth p (seq 0 r) 0)) v) in H
    by (intros p Hp'; rewrite seq_nth by exact Hp'; reflexivity).
  rewrite <- H. apply Hloc. intros p Hp'. rewrite nth_map_seq by exact Hp'. reflexivity.
Qed.

(* --- members of the classes, so the content is visible --------------- *)

(* a kernel commutative in positions (0, 1) and not with position 2 *)
Definition fb (v : nat -> nat) : nat := (v 0 + v 1) * 1000 + v 2.

Lemma fb_in_Kblock : Kblock [2; 1] fb.
Proof.
  split.
  - intros v v' H. unfold fb. simpl in H.
    rewrite (H 0), (H 1), (H 2) by lia. reflexivity.
  - intros s s' (Hs & Hs' & Hi1 & Hi2) Hl v. unfold fb. simpl lsum in *.
    assert (B : forall q, q < 3 -> blk [2; 1] q = (if q <? 2 then 0 else 1)).
    { intros q Hq. destruct q as [|[|[|q]]]; [reflexivity | reflexivity | reflexivity | lia]. }
    assert (H2 : s 2 = 2).
    { pose proof (Hl 2 ltac:(lia)) as E. rewrite B in E by (apply Hs; lia).
      simpl in E. pose proof (Hs 2 ltac:(lia)).
      destruct (Nat.ltb_spec (s 2) 2); [discriminate | lia]. }
    assert (H0 : s 0 < 2).
    { pose proof (Hl 0 ltac:(lia)) as E. rewrite B in E by (apply Hs; lia).
      simpl in E. destruct (Nat.ltb_spec (s 0) 2); [lia | discriminate]. }
    assert (H1 : s 1 < 2).
    { pose proof (Hl 1 ltac:(lia)) as E. rewrite B in E by (apply Hs; lia).
      simpl in E. destruct (Nat.ltb_spec (s 1) 2); [lia | discriminate]. }
    assert (Hne : s 0 <> s 1).
    { intro E. pose proof (Hi1 0 ltac:(lia)) as E0. pose proof (Hi1 1 ltac:(lia)) as E1.
      rewrite E in E0. lia. }
    rewrite H2.
    destruct (s 0) as [|[|?]] eqn:S0; destruct (s 1) as [|[|?]] eqn:S1; lia.
Qed.

Lemma fb_not_Ksym : ~ Ksym 3 fb.
Proof.
  intros [_ Hinv]. pose proof (Hinv (sw 1) (sw 1) (sw_perm_pair 3 1 ltac:(lia)) (fun p => p)) as H.
  unfold fb in H. simpl in H. discriminate.
Qed.

(* the Vandermonde product is alternating at r = 3 *)
Definition vdm (v : nat -> nat) : Z :=
  Z.mul (Z.mul (Z.sub (Z.of_nat (v 1)) (Z.of_nat (v 0))) (Z.sub (Z.of_nat (v 2)) (Z.of_nat (v 0))))
        (Z.sub (Z.of_nat (v 2)) (Z.of_nat (v 1))).

Lemma vdm_in_Kalt : Kalt 3 vdm.
Proof.
  split.
  - intros v v' H. unfold vdm. rewrite (H 0), (H 1), (H 2) by lia. reflexivity.
  - intros i Hi v. unfold vdm.
    destruct i as [|[|i]]; [| | lia]; cbn [sw Nat.eqb]; ring.
Qed.

(* the signed read COMPUTES: non-monotone data, r = 3, n = 4 *)
Definition vdat (i : nat) : nat := match i with 0 => 7 | 1 => 2 | 2 => 9 | _ => 4 end.

Example alt_read_pin :
  (* an odd order: [2; 0; 3] sorts to [0; 2; 3] with one inversion *)
  alt_out 3 4 (run (alt_prog 3 vdat 4) vdm) [2; 0; 3] = vdm (argsOf vdat [2; 0; 3]) /\
  vdm (argsOf vdat [2; 0; 3]) = (-30)%Z /\
  (* an even order *)
  alt_out 3 4 (run (alt_prog 3 vdat 4) vdm) [3; 0; 1] = vdm (argsOf vdat [3; 0; 1]) /\
  (* a repeated index reads 0 *)
  alt_out 3 4 (run (alt_prog 3 vdat 4) vdm) [1; 3; 1] = 0%Z /\
  vdm (argsOf vdat [1; 3; 1]) = 0%Z.
Proof. vm_compute. repeat split. Qed.
(* --- pins ------------------------------------------------------------ *)

Example C_5_3 : C 5 3 = 10.
Proof. vm_compute. reflexivity. Qed.

Example strict_5_3 : length (strict 5 3) = 10.
Proof. vm_compute. reflexivity. Qed.
