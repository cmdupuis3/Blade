(* ===================================================================== *)
(* BladeWreath.v -- product symmetry over DECLARED-SYMMETRIC inputs:     *)
(*                  the sound joint form is the WREATH product, and at   *)
(*                  r = 2 the licensed group is computed EXACTLY.        *)
(*                                                                       *)
(* Setting (formalism 3.4 / 12.5, the declared-symmetric-data row).  A    *)
(* and B are rank-r tensors over one extent, each invariant under         *)
(* permuting ITS OWN r indices -- input symmetry that the SOURCE          *)
(* declares (SymIdx<r,n> data), not symmetry deduced from a kernel.  A    *)
(* pointwise kernel f builds the rank-2r output                           *)
(*                                                                       *)
(*     T[I, J] = f (A[I], B[J])       I, J r-tuples of indices.           *)
(*                                                                       *)
(* The question this file settles is exactly which permutations of the    *)
(* 2r output slots are licensed.  Three regimes, all checked:             *)
(*                                                                       *)
(*   1. Block-wise S_r x S_r is ALWAYS licensed, for ANY kernel -- no     *)
(*      hypothesis on f whatsoever.  It is bought entirely by the         *)
(*      declared input symmetry (block_product_symmetry_soundness).       *)
(*      Order (r!)^2.  This is the sound content of the (r!)^d family of  *)
(*      claims, and it lives on the input side: 12.4 withdrew per-dim     *)
(*      product symmetry for ONE identity group of a comm kernel, and     *)
(*      12.5 keeps per-dimension SymIdx sound exactly here.               *)
(*                                                                       *)
(*   2. Commutativity of f does NOT add the BLOCK SWAP for DISTINCT A, B. *)
(*      Concrete r = 2 refutation with f = multiplication over            *)
(*      symmetric tables (block_swap_not_licensed).  This is the rank-r   *)
(*      generalization of BladeLowering's SharedUnitsInsufficient module  *)
(*      (Theorem 9.17), whose r = 1 seed is the same phenomenon: shared   *)
(*      index spaces make a swap WELL-TYPED, identity is what makes it    *)
(*      SOUND.                                                           *)
(*                                                                       *)
(*   3. With the argument REPEATED (B := A, one identity group) and f     *)
(*      commutative, the block swap joins in at general r by a one-line   *)
(*      proof, and the licensed group becomes the wreath product          *)
(*      S_r wr S_2 of order 2 (r!)^2 (wreath_full_invariance).  It is     *)
(*      STRICTLY smaller than S_{2r}: docs/future.md section 4b.1 states  *)
(*      the sound joint form is the wreath product, not S_{2r}, and       *)
(*      s4_orbit_not_licensed is the refutation of the S_{2r} reading.    *)
(*                                                                       *)
(* At r = 2 over extent 2 the whole question is FINITE (24 permutations   *)
(* of 4 slots, 16 index tuples), so the two group orders are not just     *)
(* lower bounds -- they are computed exactly by enumeration:              *)
(*                                                                       *)
(*   distinct_stabilizer_is_block_group    |stab| = 4 = (2!)^2            *)
(*   repeated_stabilizer_is_wreath         |stab| = 8 = 2 (2!)^2          *)
(*                                                                       *)
(* and the exactness is not witness luck: over every symmetric 2x2 table  *)
(* with entries below 5, the stabilizer of the repeated form is the       *)
(* 8-element wreath group EXCEPT when the table is rank one (a c = b^2),  *)
(* where the output degenerates to a 4-fold tensor power of one vector    *)
(* and all of S_4 stabilizes it (degeneracy_criterion).                   *)
(*                                                                       *)
(* Extent 2 is also where accidental symmetry is cheapest, so section 9   *)
(* re-runs the exactness enumeration over extent 6 (24 permutations x     *)
(* 6^4 = 1296 tuples, kernel a parameter): same groups exactly, for       *)
(* multiplication AND addition; the rank-one degeneracy scales (u u^T     *)
(* at 6 gives all of S_4); and the degeneracy locus is KERNEL-RELATIVE    *)
(* -- the additive analogue u (+) u degenerates under f = addition while  *)
(* staying exactly wreath under f = multiplication.                       *)
(*                                                                       *)
(* Sections:                                                             *)
(*   1. PermMachinery      -- permutes with an explicit two-sided         *)
(*                            inverse; id and composition; a genuinely    *)
(*                            symmetric tensor at every r (non-vacuity)   *)
(*   2. BlockProduct       -- fact 1, any kernel, general r; plus the     *)
(*                            general-r canonical-access license          *)
(*   3. WreathUpgrade      -- fact 3, general r, with the swap flag       *)
(*   4. FiniteMachinery    -- r = 2, n = 2 witnesses and the decidable    *)
(*                            stabilizer enumerator                       *)
(*   5. CommInsufficient   -- fact 2, the distinct-tensor refutation      *)
(*   6. NotFullS2r         -- fact 4, wreath < S_{2r}                     *)
(*   7. Exactness          -- fact 5, both orders computed exactly        *)
(*   8. CanonicalAccess    -- fact 6, lossless per-block canonical        *)
(*                            access (the SymIdx (x) SymIdx storage       *)
(*                            license), r = 2 seed                        *)
(*   9. Extent6Exactness   -- fact 5 re-enumerated over extent 6, with    *)
(*                            the kernel a parameter; degeneracy scales   *)
(*                            and is kernel-relative                      *)
(*                                                                       *)
(* Self-contained (stdlib only), like BladeCore and BladeLowering; no     *)
(* Blade file is required, so build order is unconstrained.  The          *)
(* permutes predicate is the same notion as BladeCompleteness's           *)
(* perm_pair (two-sided inverse on the position range); it is restated    *)
(* here rather than imported to keep the file standalone.  Checked with   *)
(* Rocq 9.0.1 (the tower targets Coq 8.18; the only diagnostics are the   *)
(* deprecated-missing-stdlib warnings every file in the tower emits).     *)
(* ===================================================================== *)

Require Import Arith Lia List Permutation.
Import ListNotations.

(* ===================================================================== *)
(* 1. PERMUTATION MACHINERY.                                             *)
(* An r-tuple index is a function nat -> nat (the tower's abstract style: *)
(* BladeLowering's ix, BladeCompleteness's argument tuples).  A           *)
(* permutation of the r positions is a function s : nat -> nat that maps  *)
(* [0, r) into itself and has a two-sided inverse there -- the            *)
(* constructive reading of s permutes the positions, with no functional   *)
(* extensionality and no dependence on s outside [0, r).                 *)
(* ===================================================================== *)

Definition permutes (r : nat) (s : nat -> nat) : Prop :=
  (forall p, p < r -> s p < r) /\
  exists s',
    (forall p, p < r -> s' p < r) /\
    (forall p, p < r -> s' (s p) = p) /\
    (forall p, p < r -> s (s' p) = p).

Lemma permutes_id : forall r, permutes r (fun p => p).
Proof.
  intro r. split; [ intros p Hp; exact Hp |].
  exists (fun p => p). repeat split; intros p Hp; auto.
Qed.

Lemma permutes_compose : forall r s t,
  permutes r s -> permutes r t -> permutes r (fun p => s (t p)).
Proof.
  intros r s t (Hs & s' & Hs' & Hs1 & Hs2) (Ht & t' & Ht' & Ht1 & Ht2).
  split.
  - intros p Hp. apply Hs. apply Ht. exact Hp.
  - exists (fun p => t' (s' p)). repeat split.
    + intros p Hp. apply Ht'. apply Hs'. exact Hp.
    + intros p Hp. rewrite (Hs1 (t p) (Ht p Hp)). apply Ht1. exact Hp.
    + intros p Hp. rewrite (Ht2 (s' p) (Hs' p Hp)). apply Hs2. exact Hp.
Qed.

(* --------------------------------------------------------------------- *)
(* NON-VACUITY.  The soundness theorems below take the input symmetry of *)
(* A and B as a hypothesis, so they are worth nothing unless a symmetric  *)
(* rank-r tensor exists at every r.  symsum -- the sum over the tuple's   *)
(* first r positions -- is one, and its symmetry is permutation           *)
(* reindexing of a finite sum (the same argument as                       *)
(* BladeCompleteness.fsum_max_symmetric; reproved here for standalone).   *)
(* --------------------------------------------------------------------- *)

(* Local copy of BladeDMWF.NoDup_map_inj (the tower's, not the stdlib's -- *)
(* stdlib has no injective-on-the-list version).  Restated to keep this    *)
(* file free of Blade imports.                                            *)
Lemma NoDup_map_injective : forall (X Y : Type) (g : X -> Y) (l : list X),
  NoDup l ->
  (forall x y, In x l -> In y l -> g x = g y -> x = y) ->
  NoDup (map g l).
Proof.
  intros X Y g l Hnd.
  induction Hnd as [|a l Hnotin Hnd IH]; intros Hinj; simpl; constructor.
  - intro Hin. apply in_map_iff in Hin as (x & Ex & Hx).
    apply Hinj in Ex; [subst; exact (Hnotin Hx) | now right | now left].
  - apply IH. intros x y Hx Hy E.
    apply Hinj; [right; exact Hx | right; exact Hy | exact E].
Qed.

Lemma perm_map_seq : forall r s,
  permutes r s -> Permutation (map s (seq 0 r)) (seq 0 r).
Proof.
  intros r s (Hs & s' & Hs' & Hi1 & Hi2).
  apply NoDup_Permutation.
  - apply NoDup_map_injective; [apply seq_NoDup |].
    intros x y Hx Hy E.
    apply in_seq in Hx. apply in_seq in Hy.
    rewrite <- (Hi1 x) by lia. rewrite E. apply Hi1. lia.
  - apply seq_NoDup.
  - intro x. split; intro H.
    + apply in_map_iff in H as (p & Ep & Hp).
      apply in_seq in Hp. subst x.
      assert (Hpr : p < r) by lia.
      specialize (Hs p Hpr). apply in_seq. lia.
    + apply in_seq in H.
      assert (Hxr : x < r) by lia.
      apply in_map_iff. exists (s' x). split.
      * apply Hi2. exact Hxr.
      * specialize (Hs' x Hxr). apply in_seq. lia.
Qed.

Lemma sum_perm : forall l1 l2 : list nat,
  Permutation l1 l2 -> fold_right Nat.add 0 l1 = fold_right Nat.add 0 l2.
Proof. induction 1; simpl; lia. Qed.

Definition symsum (r : nat) (I : nat -> nat) : nat :=
  fold_right Nat.add 0 (map I (seq 0 r)).

Lemma symsum_symmetric : forall r s I,
  permutes r s -> symsum r (fun p => I (s p)) = symsum r I.
Proof.
  intros r s I Hp. unfold symsum.
  rewrite <- map_map. apply sum_perm.
  apply Permutation_map. apply perm_map_seq. exact Hp.
Qed.

(* ===================================================================== *)
(* 2. FACT 1 -- BLOCK-WISE S_r x S_r SOUNDNESS, GENERAL r, ANY KERNEL.    *)
(*                                                                       *)
(* f is an unconstrained variable of the section: no commutativity, no    *)
(* associativity, no locality, no invariance -- nothing.  The licensed    *)
(* symmetry comes entirely from the DECLARED input symmetry of A and B,   *)
(* and it moves the r indices INSIDE a block, never across blocks.  This  *)
(* is the sound half of the product-symmetry doctrine (formalism 12.4 /   *)
(* 12.5): per-dimension SymIdx types stay sound exactly where the         *)
(* symmetry is genuinely per-dimension, and declared symmetric data       *)
(* (3.4) is the first entry on that list.                                *)
(*                                                                       *)
(* Group order: the licensed set contains a copy of S_r x S_r, order      *)
(* (r!)^2 -- permutes_id and permutes_compose make the block-wise family  *)
(* a submonoid, and finiteness of the position permutations supplies      *)
(* inverses (the same remark as BladeLowering's licensed_* closure).      *)
(* The ORDER itself is pinned by enumeration at r = 2 in section 7        *)
(* (|stab| = 4); at general r the factorial count is classical and is     *)
(* cited, not proved here.                                               *)
(* ===================================================================== *)

Section BlockProduct.
  Variable r : nat.
  Variables T U : Type.
  Variable f : T -> T -> U.                       (* NO hypothesis on f *)
  Variables A B : (nat -> nat) -> T.

  Hypothesis A_sym : forall s I, permutes r s -> A (fun p => I (s p)) = A I.
  Hypothesis B_sym : forall s I, permutes r s -> B (fun p => I (s p)) = B I.

  Definition Tout (I J : nat -> nat) : U := f (A I) (B J).

  Theorem block_product_symmetry_soundness :
    forall s t I J, permutes r s -> permutes r t ->
      Tout (fun p => I (s p)) (fun p => J (t p)) = Tout I J.
  Proof.
    intros s t I J Hs Ht. unfold Tout.
    rewrite (A_sym s I Hs), (B_sym t J Ht). reflexivity.
  Qed.

  (* The two generators separately, for the record: each block moves on  *)
  (* its own, which is precisely what a PRODUCT of symmetry groups means *)
  (* (contrast BladeCore's per_dim_swap_not_symmetry, where the two      *)
  (* factors are dimensions of ONE argument and the product FAILS).      *)
  Corollary left_block_symmetry :
    forall s I J, permutes r s -> Tout (fun p => I (s p)) J = Tout I J.
  Proof.
    intros s I J Hs.
    exact (block_product_symmetry_soundness s (fun p => p) I J Hs
             (permutes_id r)).
  Qed.

  Corollary right_block_symmetry :
    forall t I J, permutes r t -> Tout I (fun p => J (t p)) = Tout I J.
  Proof.
    intros t I J Ht.
    exact (block_product_symmetry_soundness (fun p => p) t I J
             (permutes_id r) Ht).
  Qed.

  (* ------------------------------------------------------------------- *)
  (* FACT 6 AT GENERAL r, abstractly.  A canonicalizer is ANY map that    *)
  (* returns a permuted copy of its argument tuple (sorting is one; the    *)
  (* r = 2 min/max instance is section 8).  Reading T at canonicalized     *)
  (* per-block indices returns the true value, so the product-simplex      *)
  (* store SymIdx<r,n> (x) SymIdx<r,n>, with C(n+r-1,r)^2 cells            *)
  (* (BladeBinomial's closed form, squared), is LOSSLESS for the           *)
  (* distinct-inputs case.  No hypothesis on f here either.               *)
  (* ------------------------------------------------------------------- *)
  Theorem block_canonical_access_general :
    forall can : (nat -> nat) -> (nat -> nat),
      (forall I, exists s, permutes r s /\ can I = (fun p => I (s p))) ->
      forall I J, Tout (can I) (can J) = Tout I J.
  Proof.
    intros can Hcan I J.
    destruct (Hcan I) as (s & Hs & EI).
    destruct (Hcan J) as (t & Ht & EJ).
    rewrite EI, EJ.
    apply block_product_symmetry_soundness; assumption.
  Qed.
End BlockProduct.

(* The section theorem instantiated at a CONCRETE symmetric tensor and an *)
(* arbitrary kernel: the hypotheses of section 2 are satisfiable at every *)
(* r, so the theorem is not vacuous.                                     *)
Corollary block_product_symmetry_nonvacuous :
  forall r (f : nat -> nat -> nat) s t I J,
    permutes r s -> permutes r t ->
    f (symsum r (fun p => I (s p))) (symsum r (fun p => J (t p)))
      = f (symsum r I) (symsum r J).
Proof.
  intros r f s t I J Hs Ht.
  exact (block_product_symmetry_soundness r nat nat f (symsum r) (symsum r)
           (fun s0 I0 H => symsum_symmetric r s0 I0 H)
           (fun t0 J0 H => symsum_symmetric r t0 J0 H)
           s t I J Hs Ht).
Qed.

(* ===================================================================== *)
(* 3. FACT 3 -- THE WREATH UPGRADE (repeated argument, general r).        *)
(*                                                                       *)
(* B := A (ONE identity group) and f commutative.  Then the BLOCK SWAP    *)
(* joins the licensed set by a one-liner -- f (A J) (A I) = f (A I) (A J) *)
(* -- and composing it with the block-wise S_r x S_r of section 2 gives   *)
(* invariance under the full wreath product S_r wr S_2, order 2 (r!)^2.   *)
(* The swap flag b : bool IS the S_2 factor; the theorem is stated for    *)
(* an arbitrary (b, s, t), i.e. for an arbitrary element of the wreath    *)
(* group.                                                                *)
(*                                                                       *)
(* Note the division of labour, which is the whole doctrinal point:       *)
(* the BLOCK-INTERNAL symmetry is bought by the input declaration and     *)
(* needs nothing from f; the BLOCK SWAP is bought by commutativity of f   *)
(* AND identity of the two arguments, and section 5 shows that dropping   *)
(* identity loses it even with commutativity retained.                    *)
(* ===================================================================== *)

Section WreathUpgrade.
  Variable r : nat.
  Variables T U : Type.
  Variable f : T -> T -> U.
  Hypothesis f_comm : forall x y, f x y = f y x.
  Variable A : (nat -> nat) -> T.
  Hypothesis A_sym : forall s I, permutes r s -> A (fun p => I (s p)) = A I.

  Definition Trep (I J : nat -> nat) : U := f (A I) (A J).

  (* The S_2 factor. *)
  Theorem wreath_block_swap : forall I J, Trep J I = Trep I J.
  Proof. intros I J. unfold Trep. apply f_comm. Qed.

  (* The S_r x S_r factor, inherited from section 2 (A used twice). *)
  Theorem wreath_block_product :
    forall s t I J, permutes r s -> permutes r t ->
      Trep (fun p => I (s p)) (fun p => J (t p)) = Trep I J.
  Proof.
    intros s t I J Hs Ht. unfold Trep.
    rewrite (A_sym s I Hs), (A_sym t J Ht). reflexivity.
  Qed.

  (* THE WREATH THEOREM: any (swap flag, sigma, tau) is licensed. *)
  Theorem wreath_full_invariance :
    forall (b : bool) s t I J, permutes r s -> permutes r t ->
      (if b then Trep (fun p => J (t p)) (fun p => I (s p))
            else Trep (fun p => I (s p)) (fun p => J (t p))) = Trep I J.
  Proof.
    intros b s t I J Hs Ht. destruct b.
    - rewrite (wreath_block_swap (fun p => I (s p)) (fun p => J (t p))).
      apply wreath_block_product; assumption.
    - apply wreath_block_product; assumption.
  Qed.
End WreathUpgrade.

(* ===================================================================== *)
(* 4. FINITE MACHINERY (r = 2, extent n = 2).                            *)
(*                                                                       *)
(* Everything from here down is decidable: 4 output slots, 24 slot        *)
(* permutations, 2^4 = 16 index tuples.  A symmetric rank-2 tensor over   *)
(* extent 2 is three numbers; tab builds it as a total function on nat    *)
(* (values off [0,2) are irrelevant -- the enumeration never looks).      *)
(* A slot permutation is a 4-element list of slot indices, and it acts on *)
(* a tuple by gathering: slot m of the image reads slot p_m of the        *)
(* source, the same convention as the numeric pre-check.                  *)
(* ===================================================================== *)

Definition tab (a00 a01 a11 : nat) (i j : nat) : nat :=
  match i, j with
  | 0, 0     => a00
  | 0, S _   => a01
  | S _, 0   => a01
  | S _, S _ => a11
  end.

(* tab is symmetric at EVERY pair of naturals: this is the declared      *)
(* input symmetry of section 2, concretely realized.                     *)
Lemma tab_sym : forall a00 a01 a11 i j,
  tab a00 a01 a11 i j = tab a00 a01 a11 j i.
Proof. intros. destruct i; destruct j; reflexivity. Qed.

(* --- the witnesses ---------------------------------------------------- *)
(* Adist / Bdist: fact 2's distinct pair, A[0,0]=1, A[0,1]=A[1,0]=2 and   *)
(* B[0,0]=3, B[0,1]=B[1,0]=5 (the diagonal corners are free -- the        *)
(* refutation never reads them).                                         *)
Definition Adist : nat -> nat -> nat := tab 1 2 3.
Definition Bdist : nat -> nat -> nat := tab 3 5 7.

(* Arep: fact 5b's repeated witness.  a c - b^2 = 5 - 4 = 1, so the table *)
(* is NOT rank one -- see degeneracy_criterion for why that matters.      *)
Definition Arep : nat -> nat -> nat := tab 1 2 5.

(* Aidm: fact 4's witness, the 2x2 identity matrix.                       *)
Definition Aidm : nat -> nat -> nat := tab 1 0 1.

(* Adeg: the DEGENERATE control, a c = b^2 (rank one, 1*4 = 2*2).          *)
Definition Adeg : nat -> nat -> nat := tab 1 2 4.

(* --- the enumerator --------------------------------------------------- *)

Definition apply_perm (p c : list nat) : list nat :=
  map (fun k => nth k c 0) p.

(* T[I,J] = f(A[I], B[J]) with f = multiplication and r = 2, read off a   *)
(* 4-slot tuple [i1; i2; j1; j2].                                        *)
Definition evalT (Atab Btab : nat -> nat -> nat) (c : list nat) : nat :=
  match c with
  | i1 :: i2 :: j1 :: j2 :: nil => Atab i1 i2 * Btab j1 j2
  | _ => 0
  end.

(* The bridge: the enumerated object IS the pointwise-kernel output, not  *)
(* a list-shaped lookalike.                                              *)
Lemma evalT_is_kernel : forall Atab Btab i1 i2 j1 j2,
  evalT Atab Btab [i1; i2; j1; j2] = Atab i1 i2 * Btab j1 j2.
Proof. intros. reflexivity. Qed.

Definition bits : list nat := [0; 1].

Definition cells4 : list (list nat) :=
  flat_map (fun a =>
    flat_map (fun b =>
      flat_map (fun c =>
        map (fun d => [a; b; c; d]) bits) bits) bits) bits.

(* All 24 permutations of 4 slots, in lex order, FIXED AS DATA (the      *)
(* BladePointGroup discipline: the table is data, its group properties   *)
(* are theorems).                                                       *)
Definition perms4 : list (list nat) :=
  [ [0;1;2;3]; [0;1;3;2]; [0;2;1;3]; [0;2;3;1]; [0;3;1;2]; [0;3;2;1];
    [1;0;2;3]; [1;0;3;2]; [1;2;0;3]; [1;2;3;0]; [1;3;0;2]; [1;3;2;0];
    [2;0;1;3]; [2;0;3;1]; [2;1;0;3]; [2;1;3;0]; [2;3;0;1]; [2;3;1;0];
    [3;0;1;2]; [3;0;2;1]; [3;1;0;2]; [3;1;2;0]; [3;2;0;1]; [3;2;1;0] ].

(* p stabilizes T iff T o p = T on every one of the 16 index tuples. *)
Definition stab4 (Atab Btab : nat -> nat -> nat) (p : list nat) : bool :=
  forallb (fun c => Nat.eqb (evalT Atab Btab (apply_perm p c))
                            (evalT Atab Btab c)) cells4.

Definition stabilizer (Atab Btab : nat -> nat -> nat) : list (list nat) :=
  filter (stab4 Atab Btab) perms4.

Definition lst_eqb (a b : list nat) : bool :=
  if list_eq_dec Nat.eq_dec a b then true else false.

Definition grp_eqb (a b : list (list nat)) : bool :=
  if list_eq_dec (list_eq_dec Nat.eq_dec) a b then true else false.

(* Composition of slot permutations, in the same gathering convention. *)
Definition closed_under_pcomp (g : list (list nat)) : bool :=
  forallb (fun p => forallb (fun q => existsb (lst_eqb (apply_perm p q)) g) g) g.

(* is_perm4 p: p has 4 slots and hits every slot -- hence is a bijection. *)
Definition is_perm4 (p : list nat) : bool :=
  Nat.eqb (length p) 4 && forallb (fun k => existsb (Nat.eqb k) p) [0;1;2;3].

(* The two candidate groups, as data.                                    *)
(* blockS2xS2: the block-wise product of section 2 at r = 2 -- swap the   *)
(* two slots of the first block, of the second block, or both.           *)
Definition blockS2xS2 : list (list nat) :=
  [ [0;1;2;3]; [0;1;3;2]; [1;0;2;3]; [1;0;3;2] ].

(* wreathS2wrS2: those four, plus the same four composed with the BLOCK  *)
(* SWAP [2;3;0;1].                                                      *)
Definition wreathS2wrS2 : list (list nat) :=
  blockS2xS2 ++ [ [2;3;0;1]; [2;3;1;0]; [3;2;0;1]; [3;2;1;0] ].

(* --- sanity of the machinery ----------------------------------------- *)

Example perms4_card : length perms4 = 24.
Proof. reflexivity. Qed.

Example cells4_card : length cells4 = 16.
Proof. reflexivity. Qed.

(* Every listed 4-list is a bijection of the slots ... *)
Theorem perms4_are_bijections : forallb is_perm4 perms4 = true.
Proof. vm_compute. reflexivity. Qed.

(* ... and they are pairwise distinct.  24 distinct bijections of a       *)
(* 4-element set is ALL of S_4, so the enumerations below are exhaustive. *)
Theorem perms4_distinct :
  length (nodup (list_eq_dec Nat.eq_dec) perms4) = 24.
Proof. vm_compute. reflexivity. Qed.

(* Both candidate sets are groups (closed under composition, contain the  *)
(* identity), of orders 4 and 8; the block group is a subgroup of the     *)
(* wreath group.                                                        *)
Theorem block_group_is_group :
  closed_under_pcomp blockS2xS2 = true /\ length blockS2xS2 = 4.
Proof. split; [vm_compute | ]; reflexivity. Qed.

Theorem wreath_group_is_group :
  closed_under_pcomp wreathS2wrS2 = true /\ length wreathS2wrS2 = 8.
Proof. split; [vm_compute | ]; reflexivity. Qed.

Theorem block_subgroup_of_wreath : incl blockS2xS2 wreathS2wrS2.
Proof. unfold wreathS2wrS2. apply incl_appl. apply incl_refl. Qed.

Theorem groups_are_permutations :
  forallb is_perm4 wreathS2wrS2 = true.
Proof. vm_compute. reflexivity. Qed.

(* ===================================================================== *)
(* 5. FACT 2 -- COMMUTATIVITY DOES NOT ADD THE BLOCK SWAP FOR DISTINCT    *)
(*    TENSORS.                                                          *)
(*                                                                       *)
(* Both inputs are symmetric (tab_sym), the kernel is multiplication --   *)
(* commutative, associative, the weakest possible adversary -- and the    *)
(* BLOCK SWAP still fails:                                               *)
(*                                                                       *)
(*   T[(0,0),(0,1)] = A[0,0] * B[0,1] = 1 * 5 = 5                         *)
(*   T[(0,1),(0,0)] = A[0,1] * B[0,0] = 2 * 3 = 6                         *)
(*                                                                       *)
(* while block-wise S_2 x S_2 holds for the same object (section 2        *)
(* instantiated).  This is BladeLowering's Theorem 9.17 raised from r = 1 *)
(* to r = 2: commutativity is in H, but the binding is not stabilized by  *)
(* a permutation that moves an index from A's block into B's, so no       *)
(* license.  The compiler consequence: comm alone must NOT be allowed to  *)
(* collapse the two blocks of a distinct-input product into a joint       *)
(* simplex.                                                              *)
(* ===================================================================== *)

Definition Tdist (i1 i2 j1 j2 : nat) : nat := Adist i1 i2 * Bdist j1 j2.

Lemma mult_is_commutative : forall x y : nat, x * y = y * x.
Proof. intros. apply Nat.mul_comm. Qed.

Lemma Adist_sym : forall i j, Adist i j = Adist j i.
Proof. intros. unfold Adist. apply tab_sym. Qed.

Lemma Bdist_sym : forall i j, Bdist i j = Bdist j i.
Proof. intros. unfold Bdist. apply tab_sym. Qed.

(* The licensed part: block-wise S_2 x S_2, for this concrete witness. *)
Theorem dist_block_product_holds :
  forall i1 i2 j1 j2, Tdist i2 i1 j2 j1 = Tdist i1 i2 j1 j2.
Proof.
  intros. unfold Tdist.
  rewrite (Adist_sym i2 i1), (Bdist_sym j2 j1). reflexivity.
Qed.

(* The refused part: the block swap.  5 <> 6. *)
Theorem block_swap_not_licensed : Tdist 0 1 0 0 <> Tdist 0 0 0 1.
Proof. compute. lia. Qed.

(* Same statement inside the enumerator, so section 7's count is about   *)
(* this very failure: the block swap is not in the stabilizer.           *)
Example block_swap_absent_distinct : stab4 Adist Bdist [2;3;0;1] = false.
Proof. vm_compute. reflexivity. Qed.

(* ===================================================================== *)
(* 6. FACT 4 -- THE WREATH GROUP IS STRICTLY SMALLER THAN S_{2r}.         *)
(*                                                                       *)
(* Repeated argument (B = A), f = multiplication, A = the 2x2 identity    *)
(* matrix -- symmetric, and non-degenerate (1*1 - 0*0 = 1).  The two      *)
(* index tuples (0,0,1,1) and (0,1,0,1) are the SAME MULTISET, hence one  *)
(* S_4 orbit, hence identified by any S_{2r}-symmetric storage scheme --  *)
(* yet                                                                   *)
(*                                                                       *)
(*   T[(0,0),(1,1)] = A[0,0] * A[1,1] = 1 * 1 = 1                         *)
(*   T[(0,1),(0,1)] = A[0,1] * A[0,1] = 0 * 0 = 0.                        *)
(*                                                                       *)
(* So a joint SymIdx<2r, n> over all 2r slots would ALIAS two distinct    *)
(* values.  docs/future.md 4b.1: the sound joint form is the wreath       *)
(* product, not S_{2r}.                                                  *)
(* ===================================================================== *)

Definition Tidm (i1 i2 j1 j2 : nat) : nat := Aidm i1 i2 * Aidm j1 j2.

Lemma Aidm_sym : forall i j, Aidm i j = Aidm j i.
Proof. intros. unfold Aidm. apply tab_sym. Qed.

Theorem s4_orbit_not_licensed : Tidm 0 0 1 1 <> Tidm 0 1 0 1.
Proof. compute. lia. Qed.

(* The two tuples really are in one S_4 orbit -- as multisets ... *)
Theorem same_s4_orbit : Permutation [0;0;1;1] [0;1;0;1].
Proof. apply perm_skip. apply perm_swap. Qed.

(* ... and, concretely, the slot permutation that carries one to the      *)
(* other is the transposition of slots 1 and 2, i.e. exactly a swap       *)
(* ACROSS the block boundary -- the one kind of move the wreath group     *)
(* never makes.                                                          *)
Theorem s4_orbit_witness : apply_perm [0;2;1;3] [0;0;1;1] = [0;1;0;1].
Proof. reflexivity. Qed.

Example cross_block_swap_absent_repeated : stab4 Aidm Aidm [0;2;1;3] = false.
Proof. vm_compute. reflexivity. Qed.

(* ===================================================================== *)
(* 7. FACT 5 -- EXACTNESS AT r = 2 BY FINITE ENUMERATION.                 *)
(*                                                                       *)
(* Sections 2 and 3 give LOWER bounds on the licensed group ((r!)^2 and   *)
(* 2 (r!)^2); sections 5 and 6 give refutations of two candidate          *)
(* enlargements.  Here the question is closed: over extent 2 the          *)
(* stabilizer is a finite object, and it is COMPUTED.                     *)
(*                                                                       *)
(*   distinct inputs:  exactly 4 = (2!)^2 survivors, and they are exactly *)
(*                     the block-wise S_2 x S_2                           *)
(*   repeated input:   exactly 8 = 2 (2!)^2 survivors, and they are       *)
(*                     exactly the wreath group S_2 wr S_2                *)
(*                                                                       *)
(* Nothing is assumed about which permutations could work: all 24 are     *)
(* tried against all 16 tuples.                                          *)
(* ===================================================================== *)

(* --- 5a: the distinct witness -- the block group, EXACTLY ------------- *)

Theorem distinct_stabilizer_count : length (stabilizer Adist Bdist) = 4.
Proof. vm_compute. reflexivity. Qed.

Theorem distinct_stabilizer_is_block_group :
  stabilizer Adist Bdist = blockS2xS2.
Proof. vm_compute. reflexivity. Qed.

(* --- 5b: the repeated witness -- the wreath group, EXACTLY ------------ *)

Theorem repeated_stabilizer_count : length (stabilizer Arep Arep) = 8.
Proof. vm_compute. reflexivity. Qed.

Theorem repeated_stabilizer_is_wreath :
  stabilizer Arep Arep = wreathS2wrS2.
Proof. vm_compute. reflexivity. Qed.

(* Fact 4's witness gives the same answer, so the wreath count is not     *)
(* special to Arep.                                                      *)
Example idm_stabilizer_is_wreath : stabilizer Aidm Aidm = wreathS2wrS2.
Proof. vm_compute. reflexivity. Qed.

(* The strict chain block < wreath < S_4, i.e. 4 < 8 < 24: the two        *)
(* refutations of sections 5 and 6, read as cardinalities.               *)
Theorem block_lt_wreath_lt_s4 :
  length (stabilizer Adist Bdist) < length (stabilizer Arep Arep) /\
  length (stabilizer Arep Arep) < length perms4.
Proof. vm_compute. split; lia. Qed.

(* --- the degeneracy criterion (why the witness values matter) --------- *)
(* A rank-one symmetric table (a c = b^2, e.g. A = u u^T) makes the       *)
(* repeated output the 4-fold tensor power of u, which IS S_4-symmetric.  *)
(* So exactness at 8 requires a non-degenerate witness -- and off that    *)
(* locus the answer is always the wreath group, never anything in         *)
(* between.  Checked over every symmetric table with entries below 5      *)
(* (125 tables x 24 permutations x 16 tuples).                           *)

Definition upto (m : nat) : list nat := seq 0 m.

Definition triples (m : nat) : list (nat * nat * nat) :=
  flat_map (fun a =>
    flat_map (fun b =>
      map (fun c => (a, b, c)) (upto m)) (upto m)) (upto m).

Definition biff (x y : bool) : bool := if x then y else negb y.

(* rank one  <->  all of S_4 stabilizes; otherwise the stabilizer is      *)
(* EXACTLY the 8-element wreath group.                                    *)
Definition degeneracy_check (t : nat * nat * nat) : bool :=
  let '(a, b, c) := t in
  let st := stabilizer (tab a b c) (tab a b c) in
  biff (Nat.eqb (a * c) (b * b)) (Nat.eqb (length st) 24)
  && (if Nat.eqb (a * c) (b * b) then true else grp_eqb st wreathS2wrS2).

Theorem degeneracy_criterion : forallb degeneracy_check (triples 5) = true.
Proof. vm_compute. reflexivity. Qed.

Example degenerate_witness_is_full_s4 : length (stabilizer Adeg Adeg) = 24.
Proof. vm_compute. reflexivity. Qed.

(* --- fact 1, corroborated computationally over a whole box ------------ *)
(* Section 2 proves block-wise licensing for ARBITRARY symmetric inputs   *)
(* and an arbitrary kernel.  Here is the same statement as a finite       *)
(* check, over every PAIR of symmetric tables with entries below 3 (729   *)
(* pairs): the block group is always in the stabilizer.  A disagreement   *)
(* between this and section 2 would mean the enumerator does not model    *)
(* the theorem's object.                                                 *)

Definition pairs_of_triples (m : nat)
  : list ((nat * nat * nat) * (nat * nat * nat)) :=
  flat_map (fun x => map (fun y => (x, y)) (triples m)) (triples m).

Definition block_licensed_check (m : nat) : bool :=
  forallb (fun xy =>
    let '((a, b, c), (d, e, g)) := xy in
    forallb (stab4 (tab a b c) (tab d e g)) blockS2xS2)
    (pairs_of_triples m).

Theorem block_group_always_licensed : block_licensed_check 3 = true.
Proof. vm_compute. reflexivity. Qed.

(* ===================================================================== *)
(* 8. FACT 6 -- LOSSLESS PER-BLOCK CANONICAL ACCESS (r = 2 seed).         *)
(*                                                                       *)
(* Written in the style of BladeCore's reynolds_canonical_access, and     *)
(* deliberately so, because the contrast is the point:                    *)
(*                                                                       *)
(*   - BladeCore.counting_lemma_r2 refutes product storage for the        *)
(*     JOINTLY symmetric output of ONE identity group over a              *)
(*     multi-dimensional array -- product-simplex cells are strictly      *)
(*     fewer than the distinct values, so no per-dimension layout can be  *)
(*     lossless there, and BladeCore.reynolds_canonical_access recovers   *)
(*     losslessness only after Reynolds symmetrization.                   *)
(*                                                                       *)
(*   - HERE the object is different: two INDEPENDENT symmetric inputs,    *)
(*     each with its own index block, and the symmetry is per-block by    *)
(*     construction.  Sorting each block independently is lossless with   *)
(*     NO symmetrization and NO hypothesis on f.  Storage is              *)
(*     SymIdx<2,n> (x) SymIdx<2,n>, C(n+1,2)^2 cells.                     *)
(*                                                                       *)
(* No contradiction: different objects, different groups.  The general-r  *)
(* form of this theorem is block_canonical_access_general in section 2.   *)
(* ===================================================================== *)

Section CanonicalAccess.
  Variables T U : Type.
  Variable f : T -> T -> U.                       (* NO hypothesis on f *)
  Variables A B : nat -> nat -> T.
  Hypothesis A_sym : forall i j, A i j = A j i.
  Hypothesis B_sym : forall i j, B i j = B j i.

  Definition Tp (i1 i2 j1 j2 : nat) : U := f (A i1 i2) (B j1 j2).

  Theorem first_block_swap :
    forall i1 i2 j1 j2, Tp i2 i1 j1 j2 = Tp i1 i2 j1 j2.
  Proof. intros. unfold Tp. rewrite (A_sym i2 i1). reflexivity. Qed.

  Theorem second_block_swap :
    forall i1 i2 j1 j2, Tp i1 i2 j2 j1 = Tp i1 i2 j1 j2.
  Proof. intros. unfold Tp. rewrite (B_sym j2 j1). reflexivity. Qed.

  Corollary both_blocks_swap :
    forall i1 i2 j1 j2, Tp i2 i1 j2 j1 = Tp i1 i2 j1 j2.
  Proof.
    intros. rewrite first_block_swap. apply second_block_swap.
  Qed.

  Definition blockCanon2 (a b : nat) : nat * nat :=
    if le_dec a b then (a, b) else (b, a).

  Lemma canon2_sorted : forall a b,
    let (c1, c2) := blockCanon2 a b in c1 <= c2.
  Proof. intros. unfold blockCanon2. destruct (le_dec a b); lia. Qed.

  (* Reading at the per-block sorted index returns the TRUE value. *)
  Theorem block_canonical_access :
    forall i1 i2 j1 j2,
      let (c1, c2) := blockCanon2 i1 i2 in
      let (d1, d2) := blockCanon2 j1 j2 in
      Tp c1 c2 d1 d2 = Tp i1 i2 j1 j2.
  Proof.
    intros. unfold blockCanon2.
    destruct (le_dec i1 i2); destruct (le_dec j1 j2);
      [ reflexivity
      | apply second_block_swap
      | apply first_block_swap
      | apply both_blocks_swap ].
  Qed.
End CanonicalAccess.

(* ===================================================================== *)
(* 9. EXTENT-6 EXACTNESS -- the same enumeration away from the thin       *)
(*    extent-2 value space.                                              *)
(*                                                                       *)
(* Extent 2 is where accidental symmetry is CHEAPEST: a symmetric 2x2     *)
(* table is three numbers, and the rank-one locus a c = b^2 isolated by   *)
(* degeneracy_criterion is dense among small tables.  This section        *)
(* re-runs section 7's exactness questions over extent 6 -- 24            *)
(* permutations x 6^4 = 1296 tuples -- with the kernel a PARAMETER of     *)
(* the enumerator, and pins four more facts:                              *)
(*                                                                       *)
(*   - distinct inputs: stabilizer EXACTLY the block group at 6x6;        *)
(*   - repeated input: stabilizer EXACTLY the wreath group at 6x6, for    *)
(*     BOTH f = multiplication and f = addition -- the count is not       *)
(*     special to one kernel;                                             *)
(*   - the rank-one degeneracy SCALES: u u^T at extent 6 (u = 1..6)       *)
(*     degenerates to all 24 elements of S_4, exactly as a c = b^2 did    *)
(*     at extent 2;                                                       *)
(*   - the locus is KERNEL-RELATIVE: the additive analogue u (+) u        *)
(*     degenerates under f = addition (T collapses to a sum of four       *)
(*     univariate terms, fully S_4-symmetric) while the SAME table under  *)
(*     f = multiplication is exactly wreath.  Degeneracy is               *)
(*     decomposability of the input relative to the kernel's operation,   *)
(*     not a property of the input alone.                                 *)
(*                                                                       *)
(* The generic witnesses were drawn at random and pre-checked             *)
(* numerically before being frozen here as data.  The symtab accessor     *)
(* makes their input symmetry DEFINITIONAL -- the (min, max) gather       *)
(* reads only the upper triangle -- so no 21-entry symmetry lemma is      *)
(* needed per witness.                                                    *)
(* ===================================================================== *)

(* Symmetric-by-construction accessor over a row-major value table.      *)
Definition symtab (rows : list (list nat)) (i j : nat) : nat :=
  nth (Nat.max i j) (nth (Nat.min i j) rows []) 0.

Lemma symtab_sym : forall rows i j, symtab rows i j = symtab rows j i.
Proof.
  intros. unfold symtab.
  rewrite Nat.min_comm, Nat.max_comm. reflexivity.
Qed.

(* --- the extent-6 witnesses ------------------------------------------- *)

Definition A6 : nat -> nat -> nat := symtab
  [ [2;2;8;5;6;6]; [2;1;5;2;4;9]; [8;5;5;2;7;9];
    [5;2;2;4;2;5]; [6;4;7;2;8;2]; [6;9;9;5;2;5] ].

Definition B6 : nat -> nat -> nat := symtab
  [ [9;8;8;5;9;9]; [8;2;3;5;8;5]; [8;3;9;6;7;3];
    [5;5;6;8;9;2]; [9;8;7;9;1;1]; [9;5;3;2;1;2] ].

Definition R6 : nat -> nat -> nat := symtab
  [ [8;7;1;2;6;9]; [7;2;6;1;2;2]; [1;6;7;5;4;9];
    [2;1;5;4;9;1]; [6;2;4;9;1;5]; [9;2;9;1;5;1] ].

(* u = 1..6 on positions 0..5; U6 = u u^T is the multiplicative rank-one  *)
(* table, V6 = u (+) u its additive analogue.  Both are symmetric by a    *)
(* one-line commutativity rewrite -- the degenerate witnesses satisfy     *)
(* the same input declaration as the generic ones.                        *)
Definition u6 (i : nat) : nat := S i.
Definition U6 (i j : nat) : nat := u6 i * u6 j.
Definition V6 (i j : nat) : nat := u6 i + u6 j.

Lemma U6_sym : forall i j, U6 i j = U6 j i.
Proof. intros. unfold U6. apply Nat.mul_comm. Qed.

Lemma V6_sym : forall i j, V6 i j = V6 j i.
Proof. intros. unfold V6. apply Nat.add_comm. Qed.

(* --- the kernel-parameterized enumerator ------------------------------ *)

Definition cells6 : list (list nat) :=
  flat_map (fun a =>
    flat_map (fun b =>
      flat_map (fun c =>
        map (fun d => [a; b; c; d]) (upto 6)) (upto 6)) (upto 6)) (upto 6).

Definition evalTf (f : nat -> nat -> nat) (Atab Btab : nat -> nat -> nat)
  (c : list nat) : nat :=
  match c with
  | i1 :: i2 :: j1 :: j2 :: nil => f (Atab i1 i2) (Btab j1 j2)
  | _ => 0
  end.

(* The bridge, as in section 4: the enumerated object IS the pointwise-  *)
(* kernel output for an ARBITRARY kernel this time.                      *)
Lemma evalTf_is_kernel : forall f Atab Btab i1 i2 j1 j2,
  evalTf f Atab Btab [i1; i2; j1; j2] = f (Atab i1 i2) (Btab j1 j2).
Proof. intros. reflexivity. Qed.

Definition stab6 (f : nat -> nat -> nat) (Atab Btab : nat -> nat -> nat)
  (p : list nat) : bool :=
  forallb (fun c => Nat.eqb (evalTf f Atab Btab (apply_perm p c))
                            (evalTf f Atab Btab c)) cells6.

Definition stabilizer6 (f : nat -> nat -> nat)
  (Atab Btab : nat -> nat -> nat) : list (list nat) :=
  filter (stab6 f Atab Btab) perms4.

Example cells6_card : length cells6 = 1296.
Proof. vm_compute. reflexivity. Qed.

(* --- exactness at 6x6 -------------------------------------------------- *)

Theorem distinct6_stabilizer_is_block_group :
  stabilizer6 Nat.mul A6 B6 = blockS2xS2.
Proof. vm_compute. reflexivity. Qed.

Corollary distinct6_stabilizer_count :
  length (stabilizer6 Nat.mul A6 B6) = 4.
Proof. rewrite distinct6_stabilizer_is_block_group. reflexivity. Qed.

Theorem repeated6_stabilizer_is_wreath :
  stabilizer6 Nat.mul R6 R6 = wreathS2wrS2.
Proof. vm_compute. reflexivity. Qed.

(* Same repeated witness, ADDITIVE kernel: the wreath count is a          *)
(* property of the regime (repeated symmetric input, commutative          *)
(* kernel), not of multiplication.                                        *)
Theorem repeated6_add_stabilizer_is_wreath :
  stabilizer6 Nat.add R6 R6 = wreathS2wrS2.
Proof. vm_compute. reflexivity. Qed.

(* --- the degeneracy locus at 6x6 --------------------------------------- *)

(* Multiplicative rank one scales: T = u (x) u (x) u (x) u, all of S_4.   *)
Theorem rank1_6_degenerates_to_s4 :
  length (stabilizer6 Nat.mul U6 U6) = 24.
Proof. vm_compute. reflexivity. Qed.

(* Additive analogue under the additive kernel: same collapse.            *)
Theorem additive_rank1_6_degenerates_to_s4 :
  length (stabilizer6 Nat.add V6 V6) = 24.
Proof. vm_compute. reflexivity. Qed.

(* ... and the SAME table under multiplication is exactly wreath: the     *)
(* degeneracy locus depends on the kernel, so a compiler cannot detect    *)
(* it from the input alone -- one more reason licensing is by identity    *)
(* and declaration, not by value inspection.                              *)
Theorem additive_locus_is_kernel_relative :
  stabilizer6 Nat.mul V6 V6 = wreathS2wrS2.
Proof. vm_compute. reflexivity. Qed.

(* ===================================================================== *)
(* Generalization notes (not mechanized here).                            *)
(*                                                                       *)
(*  - GROUP ORDERS at general r.  (r!)^2 and 2 (r!)^2 are the classical   *)
(*    orders of S_r x S_r and S_r wr S_2; this file proves the            *)
(*    INVARIANCE at general r (sections 2, 3) and computes the ORDERS     *)
(*    exactly at r = 2 (section 7).  A general-r order proof needs a      *)
(*    factorial-counting development over the permutes predicate.         *)
(*                                                                       *)
(*  - EXACTNESS at general r.  Sections 7 and 9 are exactness at r = 2    *)
(*    (extents 2 and 6) by enumeration.  A general-r statement would need *)
(*    a detection argument in BladeCompleteness's style (a maximally      *)
(*    symmetric probe kernel plus free data witnessing every violation),  *)
(*    with the degeneracy locus (rank-one inputs) excluded by hypothesis  *)
(*    rather than by computation.                                        *)
(*                                                                       *)
(*  - k BLOCKS.  Nothing in sections 2 and 3 is special to two blocks:    *)
(*    k distinct symmetric inputs of ranks r_1..r_k give the block-wise   *)
(*    product of the S_{r_i}, and repeating an argument replaces the S_2  *)
(*    factor by the symmetric group of the repeat multiplicity -- the     *)
(*    full wreath form.  Formalizing it wants an indexed family of        *)
(*    blocks (BladeMixedRadix's shape lists are the natural vehicle,      *)
(*    and its per-group mixed-radix ranks are the matching STORAGE        *)
(*    statement for the same regime).                                    *)
(*                                                                       *)
(*  - ANTISYMMETRIC INPUTS.  With declared ANTISYMMETRIC blocks the same  *)
(*    proof runs with a sign, giving a signed wreath action; the sign     *)
(*    bookkeeping is BladeLowering's output_antisymmetry_soundness and    *)
(*    BladeDeduce's table-2 sign composition.                            *)
(* ===================================================================== *)
