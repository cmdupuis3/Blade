(* ===================================================================== *)
(* BladeMonad.v -- monad and combinator laws.                            *)
(*                                                                       *)
(* Completes the combinator coverage: every law asserted in formalism    *)
(* sections 12.1-12.5 and the MonadPlus table now has a checked          *)
(* artifact at the materialized-value semantics of BladeCompute          *)
(* (typed surface-language versions are future surface-calculus work).  *)
(*                                                                       *)
(* 1. The computation monad: values are materialized lists, bind is      *)
(*    flat_map.  The four MonadPlus laws exactly as the text states      *)
(*    them (left zero, both identities, LEFT distribution), plus the     *)
(*    monad laws and a bonus right zero.  Right distribution is FALSE    *)
(*    for this monad (results interleave); the text does not claim it    *)
(*    and the rewrite must not add it.                                   *)
(* 2. Plan-level plus: a multi-plan is a list of loop blocks, plus is    *)
(*    concatenation, and evaluation distributes over it                  *)
(*    (mveval_plus_hom), as does kernel pipelining (mpipe_hom).          *)
(* 3. Staged pipelines whose second kernel consumes whole slices of the  *)
(*    first: veval_blocks (evaluation over a joined shape is a           *)
(*    block-major traversal of slice evaluations), curry_concat (the     *)
(*    blocks concatenate back to the full evaluation -- currying loses   *)
(*    nothing), pipeT and rank_changing_pipe, pipe_pipe (associativity   *)
(*    of sequential composition).  Slice kernels take materialized       *)
(*    lists, keeping the file free of functional extensionality.         *)
(* 4. Section 12.5: parallel composition is associative EXACTLY (flat    *)
(*    tuples make the up-to-reassociation qualifier unnecessary),        *)
(*    commutative up to an explicit tuple-segment swap                   *)
(*    (parallel_commutative, a genuine Permutation proof), and           *)
(*    application is not commutative (witness).  The fusion-hint law     *)
(*    is a performance guarantee, not a semantic identity.               *)
(*                                                                       *)
(* Array-level operations (zip / stack / transpose, sections 3.6-3.7)    *)
(* are out of scope here; transpose laws belong with the compiler's      *)
(* behavior layer.                                                       *)
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
