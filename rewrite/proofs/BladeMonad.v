(* ===================================================================== *)
(* BladeMonad.v -- Layer 4b: MONAD AND COMBINATOR LAWS.                  *)
(*                                                                       *)
(* Completes the combinator coverage check: every law asserted in        *)
(* formalism sections 12.1-12.5 and the proofs-doc MonadPlus table now   *)
(* has a checked artifact (at the materialized-value semantics of        *)
(* BladeCompute; typed-path versions belong to the surface calculus).    *)
(*                                                                       *)
(* 1. THE COMPUTATION MONAD (values = materialized lists; bind =         *)
(*    flat_map -- the loop-nest monad).  The four MonadPlus laws         *)
(*    EXACTLY as the text states them:                                   *)
(*      vbind_zero_l    mzero >>= k  =  mzero          (left zero)       *)
(*      vplus_zero_l/r  (BladeCompute)                 (identities)      *)
(*      vbind_plus_l    (a <|> b) >>= k = (a >>= k) <|> (b >>= k)        *)
(*                                                     (left distrib.)   *)
(*    plus the monad laws proper (ret/bind/assoc) and the bonus          *)
(*    right-zero.  HONEST NOTE: right distribution                       *)
(*    (m >>= fun x => k x <|> h x  vs  (m >>= k) <|> (m >>= h))          *)
(*    FAILS for this monad (interleaving); the text does not claim it,   *)
(*    and the rewrite should not add it.                                 *)
(*                                                                       *)
(* 2. PLAN-LEVEL PLUS (closes the deferred item): a multi-plan is a      *)
(*    list of loop blocks; plus is block concatenation; evaluation is a  *)
(*    monoid homomorphism (mveval_plus_hom), and kernel pipelining       *)
(*    distributes over plan plus (mpipe_hom -- Theorem 12.1 at the       *)
(*    plan-bag level).                                                   *)
(*                                                                       *)
(* 3. RANK-CHANGING PIPELINES (closes the deferred item, in the          *)
(*    materialized-slice form):                                          *)
(*      veval_blocks   dimensional currying at the value level:          *)
(*                     evaluation over a fused shape s1 ++ s2 is the     *)
(*                     block-major traversal of slice evaluations        *)
(*      curry_concat   the curried blocks concatenate back to the full   *)
(*                     evaluation -- currying loses nothing              *)
(*      pipeT + rank_changing_pipe   a second-stage kernel consuming     *)
(*                     materialized inner slices; the composite          *)
(*                     evaluates to h mapped over stage-1's blocks.      *)
(*    Slice kernels are taken in materialized form (h : list U -> W)     *)
(*    to stay funext-free; the extensional-kernel form is equivalent     *)
(*    under an f2_ext-style hypothesis.                                  *)
(*                                                                       *)
(* 4. SECTION 12.5 LAWS:                                                 *)
(*      parallel_associative   EXACT in the flattened tuple semantics    *)
(*                     (the up-to-reassociation qualifier strictifies:   *)
(*                     list concatenation is associative on the nose)    *)
(*      parallel_commutative   commutativity up to tuple reordering,     *)
(*                     as a Permutation with the explicit segment-swap   *)
(*                     reindexing map                                    *)
(*      application_not_commutative   witness                            *)
(*      pipe_pipe      associativity of >> at the value level            *)
(*    The fusion-distributes law (<&!> vs <&>) is a performance          *)
(*    guarantee, not a semantic identity: prose by design.               *)
(*                                                                       *)
(* Array combinators (sec 3.6/3.7: zip, stack, transpose, join/subset)   *)
(* are ARRAY-level operations, not loop combinators; transpose's laws    *)
(* live against the behavior layer (compiler-side) and the storage       *)
(* bijections; recorded as out of scope here.                            *)
(*                                                                       *)
(* Imports BladeDMWF, BladeShape, BladeTrinityAsym, BladeCompute.        *)
(* Coq 8.18, stdlib only.                                                *)
(* ===================================================================== *)

From Blade Require Import BladeDMWF BladeShape BladeTrinityAsym BladeCompute.
Require Import List Arith Lia Setoid Permutation.
Import ListNotations.

(* ---------------- 1. the computation monad ---------------- *)

Definition vret {A : Type} (x : A) : list A := [x].
Definition vbind {A B : Type} (m : list A) (k : A -> list B) : list B :=
  flat_map k m.

Theorem vbind_ret_l : forall (A B : Type) (x : A) (k : A -> list B),
  vbind (vret x) k = k x.
Proof. intros. unfold vbind, vret. simpl. apply app_nil_r. Qed.

Theorem vbind_ret_r : forall (A : Type) (m : list A),
  vbind m vret = m.
Proof.
  intros. unfold vbind, vret.
  induction m as [|a m IH]; simpl; [reflexivity | rewrite IH; reflexivity].
Qed.

Theorem vbind_assoc : forall (A B C : Type) (m : list A)
                             (k : A -> list B) (h : B -> list C),
  vbind (vbind m k) h = vbind m (fun x => vbind (k x) h).
Proof. intros. unfold vbind. apply flat_map_flat_map. Qed.

(* left zero: mzero >>= k = mzero *)
Theorem vbind_zero_l : forall (A B : Type) (k : A -> list B),
  vbind vzero k = vzero.
Proof. reflexivity. Qed.

(* bonus (true, unclaimed by the text): right zero *)
Theorem vbind_zero_r : forall (A B : Type) (m : list A),
  vbind m (fun _ : A => @vzero B) = vzero.
Proof.
  intros. unfold vbind, vzero.
  induction m as [|a m IH]; simpl; [reflexivity | exact IH].
Qed.

(* left distribution: (a <|> b) >>= k = (a >>= k) <|> (b >>= k) *)
Theorem vbind_plus_l : forall (A B : Type) (m n : list A) (k : A -> list B),
  vbind (vplus m n) k = vplus (vbind m k) (vbind n k).
Proof. intros. unfold vbind, vplus. apply fm_app. Qed.

(* ---------------- 2. plan-level plus ---------------- *)

Definition MPlan (U : Type) : Type := list (Arr U).
Definition mveval {U : Type} (P : MPlan U) : list U := flat_map veval P.
Definition pzero {U : Type} : MPlan U := [].
Definition pplus {U : Type} (P Q : MPlan U) : MPlan U := P ++ Q.
Definition mpipe {U W : Type} (P : MPlan U) (g : U -> W) : MPlan W :=
  map (fun p => pipe p g) P.

Theorem mveval_zero : forall U : Type, @mveval U pzero = vzero.
Proof. reflexivity. Qed.

(* evaluation is a monoid homomorphism from plan plus to value plus *)
Theorem mveval_plus_hom : forall (U : Type) (P Q : MPlan U),
  mveval (pplus P Q) = vplus (mveval P) (mveval Q).
Proof. intros. unfold mveval, pplus, vplus. apply fm_app. Qed.

(* Theorem 12.1 at the plan-bag level: pipelining distributes *)
Theorem mpipe_hom : forall (U W : Type) (P : MPlan U) (g : U -> W),
  mveval (mpipe P g) = map g (mveval P).
Proof.
  intros. unfold mpipe, mveval.
  rewrite flat_map_map, map_flat_map.
  apply fm_ext_in. intros p _.
  apply compose_apply_duality.
Qed.

(* ---------------- 3. rank-changing pipelines ---------------- *)

(* dimensional currying at the value level: fused evaluation is the     *)
(* block-major traversal of inner-slice evaluations                     *)
Theorem veval_blocks : forall (U : Type) (s1 s2 : Shape) (k : list nat -> U),
  veval (s1 ++ s2, k)
  = flat_map (fun t => map (fun tc => k (t ++ tc)) (enumShape s2))
             (enumShape s1).
Proof.
  intros. unfold veval. cbn [fst snd].
  rewrite enumShape_monoid_hom. unfold sprod.
  rewrite map_flat_map.
  apply fm_ext_in. intros t _.
  rewrite map_map. reflexivity.
Qed.

(* the curried blocks concatenate back to the full evaluation *)
Theorem curry_concat : forall (U : Type) (s1 s2 : Shape) (k : list nat -> U),
  concat (map (fun t => map (fun tc => k (t ++ tc)) (enumShape s2))
              (enumShape s1))
  = veval (s1 ++ s2, k).
Proof.
  intros. rewrite veval_blocks. symmetry. apply flat_map_concat_map.
Qed.

(* rank-changing >>@: the second stage consumes materialized inner      *)
(* slices of the first                                                   *)
Definition pipeT {U W : Type} (s1 s2 : Shape)
                 (k : list nat -> U) (h : list U -> W) : Arr W :=
  (s1, fun t => h (map (fun tc => k (t ++ tc)) (enumShape s2))).

Theorem rank_changing_pipe : forall (U W : Type) (s1 s2 : Shape)
                                    (k : list nat -> U) (h : list U -> W),
  veval (pipeT s1 s2 k h)
  = map h (map (fun t => map (fun tc => k (t ++ tc)) (enumShape s2))
               (enumShape s1)).
Proof.
  intros. unfold veval, pipeT. cbn [fst snd].
  rewrite map_map. reflexivity.
Qed.

(* associativity of >> at the value level *)
Theorem pipe_pipe : forall (U V W : Type) (p : Arr U)
                           (g : U -> V) (h : V -> W),
  veval (pipe (pipe p g) h) = veval (pipe p (fun x => h (g x))).
Proof.
  intros. rewrite !compose_apply_duality, map_map. reflexivity.
Qed.

(* ---------------- 4. section 12.5 laws ---------------- *)

(* membership characterization of the applicative product *)
Lemma in_sprod : forall (L1 L2 : list (list nat)) (x : list nat),
  In x (sprod L1 L2) <->
  exists t1 t2, In t1 L1 /\ In t2 L2 /\ x = t1 ++ t2.
Proof.
  intros. unfold sprod. rewrite in_flat_map. split.
  - intros (t1 & H1 & Hm).
    apply in_map_iff in Hm as (t2 & <- & H2).
    exists t1, t2. repeat split; auto.
  - intros (t1 & t2 & H1 & H2 & ->).
    exists t1. split; [exact H1 |].
    apply in_map_iff. exists t2. split; [reflexivity | exact H2].
Qed.

Lemma in_enumShape_app : forall (s1 s2 : Shape) (x : list nat),
  In x (enumShape (s1 ++ s2)) <->
  exists t1 t2, In t1 (enumShape s1) /\ In t2 (enumShape s2) /\ x = t1 ++ t2.
Proof.
  intros. rewrite enumShape_monoid_hom. apply in_sprod.
Qed.

(* Parallel composition is associative -- EXACTLY, in the flattened     *)
(* tuple semantics: the text's up-to-reassociation qualifier            *)
(* strictifies because list concatenation is associative on the nose.   *)
Theorem parallel_associative : forall s1 s2 s3 : Shape,
  enumShape ((s1 ++ s2) ++ s3) = enumShape (s1 ++ (s2 ++ s3)).
Proof. intros. rewrite <- app_assoc. reflexivity. Qed.

(* Parallel composition is commutative up to tuple reordering: the      *)
(* explicit reindexing map is the segment swap.                         *)
Definition segswap (w : nat) (t : list nat) : list nat :=
  skipn w t ++ firstn w t.

Theorem parallel_commutative : forall s1 s2 : Shape,
  Permutation (enumShape (s1 ++ s2))
              (map (segswap (swidth s2)) (enumShape (s2 ++ s1))).
Proof.
  intros. apply NoDup_Permutation.
  - apply enumShape_NoDup.
  - apply NoDup_map_inj; [apply enumShape_NoDup |].
    intros t t' Ht Ht' E.
    assert (Lt : length t = swidth (s2 ++ s1))
      by (apply enumShape_tuple_length; exact Ht).
    assert (Lt' : length t' = swidth (s2 ++ s1))
      by (apply enumShape_tuple_length; exact Ht').
    unfold segswap in E.
    assert (LS : length (skipn (swidth s2) t)
                 = length (skipn (swidth s2) t')).
    { rewrite !skipn_length, Lt, Lt'. reflexivity. }
    assert (Es : skipn (swidth s2) t = skipn (swidth s2) t').
    { eapply app_split_length; [exact LS | exact E]. }
    rewrite Es in E. apply app_inv_head in E.
    rewrite <- (firstn_skipn (swidth s2) t),
            <- (firstn_skipn (swidth s2) t'), E, Es.
    reflexivity.
  - intro x. split.
    + intro H. apply in_enumShape_app in H as (t1 & t2 & H1 & H2 & ->).
      apply in_map_iff. exists (t2 ++ t1). split.
      * assert (L2 : length t2 = swidth s2)
          by (apply enumShape_tuple_length; exact H2).
        unfold segswap. rewrite <- L2, skipn_exact, firstn_exact.
        reflexivity.
      * apply in_enumShape_app. exists t2, t1. repeat split; auto.
    + intro H. apply in_map_iff in H as (t & <- & Ht).
      apply in_enumShape_app in Ht as (t2 & t1 & H2 & H1 & ->).
      assert (L2 : length t2 = swidth s2)
        by (apply enumShape_tuple_length; exact H2).
      unfold segswap. rewrite <- L2, skipn_exact, firstn_exact.
      apply in_enumShape_app. exists t1, t2. repeat split; auto.
Qed.

(* Application is not commutative: witness. *)
Theorem application_not_commutative :
  exists (p : Arr nat) (f g : nat -> nat),
    veval (pipe (pipe p f) g) <> veval (pipe (pipe p g) f).
Proof.
  exists (wrap0 0), S, (fun x => 2 * x).
  intro E. cbn in E. discriminate E.
Qed.
