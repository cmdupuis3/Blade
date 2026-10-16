(* ===================================================================== *)
(* BladeObject.v -- THE ONE MODEL: the DMWF is the quotient.             *)
(* (docs/plans/plan-one-model-surface-calculus.md, item 1)               *)
(*                                                                       *)
(* The tower has four models: the index universe and its enumeration    *)
(* (BladeDMWF, BladeArrow, BladeShape); the materialized-value           *)
(* semantics Arr U = Shape * (list nat -> U) with veval (BladeCompute,   *)
(* BladeMonad); the oracle model of adaptive programs (BladeOptimal);    *)
(* and the free term model (BladeOrbitWork, BladeDeduceExact).  This     *)
(* file states the object they are all about, and proves that the first *)
(* three are its projections.  (The fourth is the licence bridge in      *)
(* BladeSurface.)                                                        *)
(*                                                                       *)
(* The object is the DMWF itself, with its quotient property made        *)
(* explicit.  The feedback of the Double Metamorphism -- the emitted     *)
(* index becoming the residual's lower bound -- enumerates exactly one   *)
(* representative of every orbit of the symmetric group on the dense     *)
(* box.  The feedback IS the group action, computed.                     *)
(*                                                                       *)
(*   canonical_iff_nondecr      the DMWF's canonical tuples are the      *)
(*                              dense tuples that are nondecreasing      *)
(*   enum_hits_every_orbit      every dense tuple is a Permutation of a  *)
(*                              canonical one (its insertion sort)       *)
(*   enum_hits_each_orbit_once  two canonical tuples in one orbit are    *)
(*                              equal                                    *)
(*   isort_fibres_are_orbits    the canonical-form map's fibres are the  *)
(*                              orbits                                   *)
(*   enum_is_quotient           enum r l u = box r l u / S_r, as the     *)
(*                              image of the box under canonical form    *)
(*   orbit_count                the number of orbits is C(u-l+r-1, r)    *)
(*                              (BladeBinomial's cardinality, read as    *)
(*                              an orbit count)                          *)
(*                                                                       *)
(* Blockwise, for shapes (the Young action: permute within each record): *)
(*                                                                       *)
(*   shape_box_spec             the dense box of a shape                 *)
(*   scanon_in_shape, scanon_block_perm, enumShape_hits_each_orbit_once, *)
(*   scanon_fibres_are_orbits, enumShape_is_quotient                     *)
(*                              the transversal theorem for every shape  *)
(*   shape_box_length, shapeCard_is_orbit_count                          *)
(*                              dense cells vs orbits                    *)
(*                                                                       *)
(* Arrays on the quotient (projection 1, BladeCompute):                  *)
(*                                                                       *)
(*   qread                      the canonicalizing read of formalism     *)
(*                              3.9 and 12.2: A (3, 1) = A (1, 3)        *)
(*   qread_invariant            constant on orbits                       *)
(*   qread_fixes_transversal    the identity on canonical tuples         *)
(*   qread_is_storage           equals the lookup at the tuple's         *)
(*                              position in the materialized list:       *)
(*                              storage, iteration and access are one    *)
(*                              object                                   *)
(*   veval_one_per_orbit        the materialized list holds exactly one  *)
(*                              cell per orbit                           *)
(*                                                                       *)
(* The object as a program (projection 2, BladeOptimal):                 *)
(*                                                                       *)
(*   object_prog                ask the kernel at every canonical tuple  *)
(*   object_prog_run            its result is veval                      *)
(*   object_prog_trace          its questions are the transversal        *)
(*   object_work                it asks exactly shapeCard s questions    *)
(*   young_prog_is_object_prog  BladeOptimal's attaining program is the  *)
(*                              object's program read through the data   *)
(*   object_is_optimal          with uniform_optimality_young_nat: the   *)
(*                              orbit count is forced by every correct   *)
(*                              program and attained by the object       *)
(*                                                                       *)
(* Imports BladeDMWF, BladeShape, BladeTrinityAsym, BladeBinomial,       *)
(* BladeCompute, BladeMonad, BladeMixedRadix, BladeOptimal.              *)
(* Rocq 9.0, stdlib only.                                                *)
(* ===================================================================== *)

From Blade Require Import BladeDMWF BladeShape BladeTrinityAsym BladeBinomial
                          BladeCompute BladeMonad BladeMixedRadix BladeOptimal.
From Stdlib Require Import List Arith Lia Permutation.
Import ListNotations.

(* list plumbing: splitting at a known block length *)
Lemma firstn_app_exact : forall (A : Type) (n : nat) (t1 t2 : list A),
  length t1 = n -> firstn n (t1 ++ t2) = t1.
Proof. intros A n t1 t2 <-. apply BladeCompute.firstn_exact. Qed.

Lemma skipn_app_exact : forall (A : Type) (n : nat) (t1 t2 : list A),
  length t1 = n -> skipn n (t1 ++ t2) = t2.
Proof. intros A n t1 t2 <-. apply BladeCompute.skipn_exact. Qed.

(* ===================================================================== *)
(* PART A.  THE DENSE BOX, and the sorted characterization of the DMWF. *)
(* ===================================================================== *)

(* a dense tuple: r indices, each in [l, u), in any order *)
Definition dense_tuple (r l u : nat) (t : list nat) : Prop :=
  length t = r /\ Forall (fun x => l <= x < u) t.

(* the dense box [l, u)^r, enumerated *)
Fixpoint box (r l u : nat) : list (list nat) :=
  match r with
  | 0 => [ [] ]
  | S r' => flat_map (fun i => map (cons i) (box r' l u)) (seq l (u - l))
  end.

Lemma box_spec : forall r l u t, In t (box r l u) <-> dense_tuple r l u t.
Proof.
  induction r as [|r IH]; intros l u t; unfold dense_tuple in *; simpl.
  - split.
    + intros [<- | []]. split; [reflexivity | constructor].
    + intros [Hl _]. destruct t; [left; reflexivity | discriminate].
  - split.
    + intro H. apply in_flat_map in H as (i & Hi & Ht).
      apply in_map_iff in Ht as (t' & <- & Ht').
      apply in_seq in Hi. apply IH in Ht' as [Hl HF].
      split; [simpl; lia | constructor; [lia | exact HF]].
    + intros [Hl HF]. destruct t as [|i t']; [discriminate |].
      inversion HF as [| ? ? Hi HF']; subst.
      simpl in Hl. injection Hl as Hl.
      apply in_flat_map. exists i. split; [apply in_seq; lia |].
      apply in_map_iff. exists t'. split; [reflexivity |].
      apply IH. split; assumption.
Qed.

Lemma box_NoDup : forall r l u, NoDup (box r l u).
Proof.
  induction r as [|r IH]; intros; simpl.
  - constructor; [simpl; tauto | constructor].
  - apply NoDup_flat_map.
    + apply NoDup_seq.
    + intros i _.
      apply NoDup_map_inj; [apply IH | intros x y _ _ E; injection E; auto].
    + intros x y b _ _ Hbx Hby.
      apply in_map_iff in Hbx as (tx & Ex & _).
      apply in_map_iff in Hby as (ty & Ey & _).
      rewrite <- Ex in Ey. injection Ey; intros; congruence.
Qed.

Lemma box_length : forall r l u, length (box r l u) = (u - l) ^ r.
Proof.
  induction r as [|r IH]; intros; simpl; [reflexivity |].
  rewrite BladeDMWF.flat_map_length.
  transitivity (lsum (map (fun _ : nat => (u - l) ^ r) (seq l (u - l)))).
  { f_equal. apply map_ext. intro i. rewrite length_map. apply IH. }
  rewrite lsum_const, length_seq. apply Nat.mul_comm.
Qed.

(* nondecreasing *)
Fixpoint nondecr (t : list nat) : Prop :=
  match t with
  | a :: ((b :: _) as t') => a <= b /\ nondecr t'
  | _ => True
  end.

Lemma nondecr_tail : forall a t, nondecr (a :: t) -> nondecr t.
Proof. intros a [|b t] H; simpl in *; [exact I | exact (proj2 H)]. Qed.

Lemma nondecr_lb : forall a t, nondecr (a :: t) -> Forall (fun x => a <= x) t.
Proof.
  intros a t. revert a. induction t as [|b t IH]; intros a H.
  - constructor.
  - simpl in H. destruct H as [Hab Hbt].
    constructor; [exact Hab |].
    specialize (IH b Hbt).
    eapply Forall_impl; [| exact IH]. intros x Hx. simpl in Hx. lia.
Qed.

(* THE SORTED CHARACTERIZATION: the DMWF's canonical tuples are exactly *)
(* the dense tuples that are nondecreasing.                             *)
Theorem canonical_iff_nondecr : forall r l u t,
  canonical r l u t <-> dense_tuple r l u t /\ nondecr t.
Proof.
  induction r as [|r IH]; intros l u t; unfold dense_tuple in *.
  - destruct t as [|i t']; simpl.
    + split; [intros _; repeat split; constructor | intros _; exact I].
    + split; [intros [] | intros [[H _] _]; discriminate].
  - destruct t as [|i t']; simpl.
    + split; [intros [] | intros [[H _] _]; discriminate].
    + rewrite (IH i u t'). split.
      * intros [Hb [[Hl HF] Hnd]].
        split; [split |].
        -- rewrite Hl. reflexivity.
        -- constructor; [exact Hb |].
           eapply Forall_impl; [| exact HF]. intros x Hx. simpl in Hx. lia.
        -- destruct t' as [|j t'']; [exact I |].
           split; [| exact Hnd].
           inversion HF; subst. lia.
      * intros [[Hl HF] Hnd].
        inversion HF as [| ? ? Hi HF']; subst.
        injection Hl as Hl.
        pose proof (nondecr_lb i t' Hnd) as Hlb.
        split; [exact Hi |].
        split; [split |].
        -- exact Hl.
        -- rewrite Forall_forall in *. intros x Hx.
           specialize (HF' x Hx). specialize (Hlb x Hx). simpl in *. lia.
        -- exact (nondecr_tail i t' Hnd).
Qed.

(* ===================================================================== *)
(* PART B.  THE TRANSVERSAL THEOREM: the DMWF enumerates one             *)
(* representative per orbit.  The orbit relation of S_r on tuples is     *)
(* list Permutation; the canonical form is BladeOptimal's insertion      *)
(* sort.                                                                 *)
(* ===================================================================== *)

(* the box is closed under the action *)
Lemma dense_perm : forall r l u t t',
  dense_tuple r l u t -> Permutation t t' -> dense_tuple r l u t'.
Proof.
  intros r l u t t' [Hl HF] HP. split.
  - rewrite <- (Permutation_length HP). exact Hl.
  - rewrite Forall_forall in *. intros x Hx. apply HF.
    apply Permutation_in with t'; [apply Permutation_sym; exact HP | exact Hx].
Qed.

(* the general-l form of BladeOptimal's isort_canon *)
Lemma isort_canonical_of_dense : forall t l u,
  Forall (fun x => l <= x < u) t -> canonical (length t) l u (isort t).
Proof.
  induction t as [|a t IH]; intros l u HF; simpl.
  - exact I.
  - inversion HF as [| ? ? Ha HF']; subst.
    pose proof (insert_canon _ _ _ _ a (IH l u HF') (proj2 Ha)) as H.
    rewrite Nat.min_l in H by lia. exact H.
Qed.

Theorem enum_hits_every_orbit : forall r l u t,
  dense_tuple r l u t ->
  In (isort t) (enum r l u) /\ Permutation t (isort t).
Proof.
  intros r l u t [Hl HF]. split.
  - apply enum_complete. rewrite <- Hl. apply isort_canonical_of_dense. exact HF.
  - apply isort_perm.
Qed.

Theorem enum_hits_each_orbit_once : forall r l u c c',
  In c (enum r l u) -> In c' (enum r l u) -> Permutation c c' -> c = c'.
Proof.
  intros r l u c c' Hc Hc' HP.
  apply enum_sound in Hc. apply enum_sound in Hc'.
  exact (canon_perm_eq r l u c c' Hc Hc' HP).
Qed.

(* the canonical-form map's fibres are exactly the orbits *)
Theorem isort_fibres_are_orbits : forall t t',
  isort t = isort t' <-> Permutation t t'.
Proof.
  intros t t'. split.
  - intro E.
    apply perm_trans with (isort t); [apply isort_perm |].
    rewrite E. apply Permutation_sym. apply isort_perm.
  - apply isort_respects.
Qed.

(* THE QUOTIENT: the DMWF's enumeration is the image of the dense box   *)
(* under the canonical-form map.                                        *)
Theorem enum_is_quotient : forall r l u c,
  In c (enum r l u) <-> exists t, In t (box r l u) /\ isort t = c.
Proof.
  intros r l u c. split.
  - intro Hc. exists c.
    apply enum_sound in Hc.
    pose proof (proj1 (canonical_iff_nondecr r l u c) Hc) as [Hd _].
    split; [apply box_spec; exact Hd | apply isort_canonical with r l u; exact Hc].
  - intros (t & Ht & <-).
    apply box_spec in Ht. exact (proj1 (enum_hits_every_orbit r l u t Ht)).
Qed.

(* so the cardinality closed form counts orbits *)
Corollary orbit_count : forall r l u,
  length (enum r l u) = C (u - l + r - 1) r.
Proof. exact enum_length_binom. Qed.

(* ===================================================================== *)
(* PART C.  SHAPES: the Young action permutes within each record's       *)
(* block.  The DMWF over a shape (enumShape) is the quotient of the      *)
(* shape's dense box by that action; BladeOptimal's scanon is the        *)
(* blockwise canonical form.                                             *)
(* ===================================================================== *)

(* the dense box of a shape *)
Fixpoint shape_dense (s : Shape) (t : list nat) : Prop :=
  match s with
  | [] => t = []
  | c :: s' => dense_tuple (ix_r c) (ix_l c) (ix_u c) (firstn (ix_r c) t)
               /\ shape_dense s' (skipn (ix_r c) t)
  end.

Fixpoint shape_box (s : Shape) : list (list nat) :=
  match s with
  | [] => [ [] ]
  | c :: s' => sprod (box (ix_r c) (ix_l c) (ix_u c)) (shape_box s')
  end.

(* the orbit relation of the Young action *)
Fixpoint block_perm (s : Shape) (t t' : list nat) : Prop :=
  match s with
  | [] => t = t'
  | c :: s' => Permutation (firstn (ix_r c) t) (firstn (ix_r c) t')
               /\ block_perm s' (skipn (ix_r c) t) (skipn (ix_r c) t')
  end.

Lemma shape_box_spec : forall s t, In t (shape_box s) <-> shape_dense s t.
Proof.
  induction s as [|c s IH]; intro t; simpl.
  - split; [intros [<- | []]; reflexivity | intros ->; left; reflexivity].
  - rewrite in_sprod. split.
    + intros (t1 & t2 & H1 & H2 & ->).
      apply box_spec in H1. pose proof (proj1 H1) as L1.
      replace (firstn (ix_r c) (t1 ++ t2)) with t1
        by (rewrite <- L1; symmetry; apply firstn_exact).
      replace (skipn (ix_r c) (t1 ++ t2)) with t2
        by (rewrite <- L1; symmetry; apply skipn_exact).
      split; [exact H1 | apply IH; exact H2].
    + intros [H1 H2].
      exists (firstn (ix_r c) t), (skipn (ix_r c) t).
      split; [apply box_spec; exact H1 |].
      split; [apply IH; exact H2 | symmetry; apply firstn_skipn].
Qed.

Lemma sprod_length : forall l1 l2,
  length (sprod l1 l2) = length l1 * length l2.
Proof.
  intros l1 l2. unfold sprod. rewrite BladeDMWF.flat_map_length.
  transitivity (lsum (map (fun _ : list nat => length l2) l1)).
  { f_equal. apply map_ext. intro t. apply length_map. }
  rewrite lsum_const. apply Nat.mul_comm.
Qed.

Theorem shape_box_length : forall s,
  length (shape_box s)
  = fold_right Nat.mul 1 (map (fun c => (ix_u c - ix_l c) ^ ix_r c) s).
Proof.
  induction s as [|c s IH]; simpl; [reflexivity |].
  rewrite sprod_length, box_length, IH. reflexivity.
Qed.

Lemma canonical_dense : forall r l u t,
  canonical r l u t -> dense_tuple r l u t.
Proof. intros. apply canonical_iff_nondecr. assumption. Qed.

(* canonical tuples of a shape are dense tuples of it *)
Lemma enumShape_shape_dense : forall s t, In t (enumShape s) -> shape_dense s t.
Proof.
  induction s as [|c s IH]; intros t Ht; simpl.
  - destruct Ht as [<- | []]. reflexivity.
  - apply enumShape_cons_inv in Ht as (t1 & t2 & -> & H1 & H2).
    unfold enumIx in H1. apply enum_sound in H1.
    pose proof (canonical_dense _ _ _ _ H1) as Hd.
    pose proof (proj1 Hd) as L1.
    replace (firstn (ix_r c) (t1 ++ t2)) with t1
      by (rewrite <- L1; symmetry; apply firstn_exact).
    replace (skipn (ix_r c) (t1 ++ t2)) with t2
      by (rewrite <- L1; symmetry; apply skipn_exact).
    split; [exact Hd | apply IH; exact H2].
Qed.

Lemma shape_dense_sdense : forall s t, shape_dense s t -> sdense s t.
Proof.
  induction s as [|c s IH]; intros t H; simpl in *; [exact I |].
  destruct H as [[Hl HF] H2].
  split; [exact Hl |].
  split; [rewrite Forall_forall in HF; exact HF | apply IH; exact H2].
Qed.

(* the blockwise canonical form lands in the transversal *)
Theorem scanon_in_shape : forall s t,
  shape_dense s t -> In (scanon s t) (enumShape s).
Proof. intros s t H. apply scanon_in. apply shape_dense_sdense. exact H. Qed.

(* and is in the tuple's orbit *)
Theorem scanon_block_perm : forall s t,
  shape_dense s t -> block_perm s t (scanon s t).
Proof.
  induction s as [|c s IH]; intros t H; simpl in *.
  - subst. reflexivity.
  - destruct H as [[Hl HF] H2].
    assert (L : length (isort (firstn (ix_r c) t)) = ix_r c)
      by (rewrite isort_length; exact Hl).
    rewrite (firstn_app_exact _ _ _ _ L), (skipn_app_exact _ _ _ _ L).
    split; [apply isort_perm | apply IH; exact H2].
Qed.

Theorem enumShape_hits_each_orbit_once : forall s c c',
  In c (enumShape s) -> In c' (enumShape s) -> block_perm s c c' -> c = c'.
Proof.
  induction s as [|r s IH]; intros c c' Hc Hc' HP; simpl in HP.
  - exact HP.
  - apply enumShape_cons_inv in Hc as (t1 & t2 & -> & H1 & H2).
    apply enumShape_cons_inv in Hc' as (t1' & t2' & -> & H1' & H2').
    unfold enumIx in H1, H1'.
    pose proof (enum_elem_length _ _ _ _ H1) as L1.
    pose proof (enum_elem_length _ _ _ _ H1') as L1'.
    replace (firstn (ix_r r) (t1 ++ t2)) with t1 in HP
      by (rewrite <- L1; symmetry; apply firstn_exact).
    replace (skipn (ix_r r) (t1 ++ t2)) with t2 in HP
      by (rewrite <- L1; symmetry; apply skipn_exact).
    replace (firstn (ix_r r) (t1' ++ t2')) with t1' in HP
      by (rewrite <- L1'; symmetry; apply firstn_exact).
    replace (skipn (ix_r r) (t1' ++ t2')) with t2' in HP
      by (rewrite <- L1'; symmetry; apply skipn_exact).
    destruct HP as [HP1 HP2].
    f_equal.
    + apply (canon_perm_eq _ _ _ _ _ (enum_sound _ _ _ _ H1)
                                      (enum_sound _ _ _ _ H1') HP1).
    + apply IH; assumption.
Qed.

Lemma scanon_fixes : forall s c, In c (enumShape s) -> scanon s c = c.
Proof.
  induction s as [|r s IH]; intros c Hc; simpl.
  - destruct Hc as [<- | []]. reflexivity.
  - apply enumShape_cons_inv in Hc as (t1 & t2 & -> & H1 & H2).
    unfold enumIx in H1.
    pose proof (enum_elem_length _ _ _ _ H1) as L1.
    replace (firstn (ix_r r) (t1 ++ t2)) with t1
      by (rewrite <- L1; symmetry; apply firstn_exact).
    replace (skipn (ix_r r) (t1 ++ t2)) with t2
      by (rewrite <- L1; symmetry; apply skipn_exact).
    rewrite (isort_canonical _ _ _ _ (enum_sound _ _ _ _ H1)).
    rewrite (IH t2 H2). reflexivity.
Qed.

Lemma block_perm_sym : forall s t t', block_perm s t t' -> block_perm s t' t.
Proof.
  induction s as [|c s IH]; intros t t' H; simpl in *.
  - symmetry. exact H.
  - destruct H as [H1 H2]. split; [apply Permutation_sym; exact H1 | apply IH; exact H2].
Qed.

Lemma block_perm_trans : forall s t t' t'',
  block_perm s t t' -> block_perm s t' t'' -> block_perm s t t''.
Proof.
  induction s as [|c s IH]; intros t t' t'' H H'; simpl in *.
  - congruence.
  - destruct H as [H1 H2]. destruct H' as [H1' H2'].
    split; [apply perm_trans with (firstn (ix_r c) t'); assumption
           | apply IH with (skipn (ix_r c) t'); assumption].
Qed.

Lemma block_perm_scanon_eq : forall s t t',
  block_perm s t t' -> scanon s t = scanon s t'.
Proof.
  induction s as [|c s IH]; intros t t' H; simpl in *; [reflexivity |].
  destruct H as [H1 H2].
  rewrite (isort_respects _ _ H1), (IH _ _ H2). reflexivity.
Qed.

(* the blockwise canonical form's fibres are exactly the Young orbits *)
Theorem scanon_fibres_are_orbits : forall s t t',
  shape_dense s t -> shape_dense s t' ->
  (scanon s t = scanon s t' <-> block_perm s t t').
Proof.
  intros s t t' Hd Hd'. split.
  - intro E.
    apply block_perm_trans with (scanon s t); [apply scanon_block_perm; exact Hd |].
    rewrite E. apply block_perm_sym. apply scanon_block_perm. exact Hd'.
  - apply block_perm_scanon_eq.
Qed.

(* THE QUOTIENT, for every shape. *)
Theorem enumShape_is_quotient : forall s c,
  In c (enumShape s) <-> exists t, In t (shape_box s) /\ scanon s t = c.
Proof.
  intros s c. split.
  - intro Hc. exists c.
    split; [apply shape_box_spec; apply enumShape_shape_dense; exact Hc
           | apply scanon_fixes; exact Hc].
  - intros (t & Ht & <-).
    apply shape_box_spec in Ht. apply scanon_in_shape. exact Ht.
Qed.

(* the orbit count of the Young action *)
Corollary shapeCard_is_orbit_count : forall s,
  shapeCard s = length (enumShape s).
Proof. exact shapeCard_is_length. Qed.

(* ===================================================================== *)
(* PART D.  ARRAYS ON THE QUOTIENT (projection 1: BladeCompute).         *)
(* An Arr U is a function on the transversal.  Reading it at ANY dense   *)
(* tuple canonicalizes first -- formalism 3.9 (cov(3,1) IS cov(1,3))     *)
(* and 12.2's fold phase.                                                *)
(* ===================================================================== *)

Definition qread {U : Type} (A : Arr U) (t : list nat) : U :=
  snd A (scanon (fst A) t).

Theorem qread_invariant : forall (U : Type) (A : Arr U) t t',
  shape_dense (fst A) t -> shape_dense (fst A) t' ->
  block_perm (fst A) t t' -> qread A t = qread A t'.
Proof.
  intros U A t t' Hd Hd' HP. unfold qread. f_equal.
  apply scanon_fibres_are_orbits; assumption.
Qed.

Theorem qread_fixes_transversal : forall (U : Type) (A : Arr U) c,
  In c (enumShape (fst A)) -> qread A c = snd A c.
Proof. intros U A c Hc. unfold qread. rewrite scanon_fixes; [reflexivity | exact Hc]. Qed.

(* storage: the read equals the lookup at the canonical tuple's position *)
(* in the materialized list (the mixed-radix offset is this position in  *)
(* closed form, BladeMixedRadix)                                          *)
Theorem qread_is_storage : forall (U : Type) (A : Arr U) (d : U) t,
  shape_dense (fst A) t ->
  nth (index_of (scanon (fst A) t) (enumShape (fst A))) (veval A) d = qread A t.
Proof.
  intros U A d t Hd. unfold veval, qread.
  apply nth_index_of. apply scanon_in_shape. exact Hd.
Qed.

Theorem veval_one_per_orbit : forall (U : Type) (A : Arr U),
  length (veval A) = shapeCard (fst A).
Proof. intros. unfold veval. rewrite length_map. symmetry. apply shapeCard_is_length. Qed.

(* ===================================================================== *)
(* PART E.  THE OBJECT AS A PROGRAM (projection 2: BladeOptimal).        *)
(* The object's program asks the kernel at every canonical tuple and     *)
(* nowhere else.  Its result is the materialized semantics; its trace    *)
(* is the transversal; its work is the orbit count.                      *)
(* ===================================================================== *)

Definition object_prog {U : Type} (s : Shape) : prog (list nat) U (list U) :=
  ask_all (enumShape s) [].

Theorem object_prog_run : forall (U : Type) (s : Shape) (k : list nat -> U),
  run (object_prog s) k = veval (s, k).
Proof. intros. unfold object_prog. rewrite ask_all_run. reflexivity. Qed.

Theorem object_prog_trace : forall (U : Type) (s : Shape) (k : list nat -> U),
  trace (object_prog s) k = enumShape s.
Proof. intros. unfold object_prog. apply ask_all_trace. Qed.

Theorem object_work : forall (U : Type) (s : Shape) (k : list nat -> U),
  length (trace (object_prog s) k) = shapeCard s.
Proof. intros. rewrite object_prog_trace. symmetry. apply shapeCard_is_length. Qed.

(* BladeOptimal's attaining program for the Young nest is the object's  *)
(* program read through the data: same questions (through the binding), *)
(* same answers.                                                        *)
Theorem young_prog_is_object_prog :
  forall (D : nat -> nat -> nat) (s : Shape) (f : (nat -> nat) -> nat),
    run (young_prog D s) f = run (object_prog s) (fun t => f (shargs D s t))
    /\ trace (young_prog D s) f
       = map (shargs D s) (trace (object_prog s) (fun t => f (shargs D s t))).
Proof.
  intros D s f. unfold young_prog. split.
  - rewrite ask_all_run, object_prog_run. unfold veval. simpl.
    rewrite map_map. reflexivity.
  - rewrite ask_all_trace, object_prog_trace. reflexivity.
Qed.

(* The circle closed: no program correct for every kernel in the law    *)
(* class asks fewer questions than the object, and the object asks      *)
(* exactly as many as it has orbits.                                    *)
Theorem object_is_optimal :
  forall (s : Shape) (O : Type) (out : O -> list nat -> nat)
         (P : prog (nat -> nat) nat O),
  (forall f, Ksym (arity s) f -> forall t, In t (enumShape s) ->
     out (run P f) t = f (shargs cD s t)) ->
  forall f, Ksym (arity s) f ->
    shapeCard s <= length (trace P f)
    /\ (forall (U : Type) (k : list nat -> U),
          length (trace (object_prog s) k) = shapeCard s).
Proof.
  intros s O out P Hcor f Hf. split.
  - exact (proj1 (uniform_optimality_young_nat s O out P Hcor f Hf)).
  - intros U k. apply object_work.
Qed.
