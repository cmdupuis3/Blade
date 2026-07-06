(* ===================================================================== *)
(* BladeCompute.v -- the computation model.                              *)
(*                                                                       *)
(* A plan (equivalently, an array -- fusion makes them the same kind of  *)
(* object) is a shape plus a kernel over index tuples.  Evaluation       *)
(* materializes it as a list: veval p = map kernel (enumShape shape).    *)
(* All laws are list equalities, so no functional extensionality is      *)
(* needed anywhere.  Kernels here consume single elements; staged        *)
(* pipelines whose second kernel consumes whole slices are in            *)
(* BladeMonad.v, and typed surface-language versions of these laws are   *)
(* future surface-calculus work.                                         *)
(*                                                                       *)
(* Contents:                                                             *)
(*   veval_pval          embedding a value as a trivial plan and         *)
(*                       evaluating returns it -- the checked core of    *)
(*                       Theorem 2.1 and the round-trip half of the      *)
(*                       conjectured V-P adjunction                      *)
(*   veval_not_injective two different plans, same value: evaluation     *)
(*                       forgets how a value is computed (Theorem 12.7)  *)
(*   fuse2, rank0_convergence (12.2), fuse2_pairs                        *)
(*                       applying a two-argument kernel to two arrays;   *)
(*                       fused evaluation = pairwise product             *)
(*                       evaluation.  rank0_convergence is thin here by  *)
(*                       design: both routes are maps in this semantics  *)
(*   pipe, compose_apply_duality (12.1)                                  *)
(*                       compose kernels then evaluate = evaluate then   *)
(*                       map.  The proof is map_map: the law holds       *)
(*                       because the loop is one reified list            *)
(*   slot_interchange    composing through the kernel slot and through   *)
(*                       the array slot agree under evaluation; a third  *)
(*                       maximal currying would add a third operator     *)
(*                       and law (open; see BladeCurryingGeneral)        *)
(*   wrap0 round trips (12.3/12.4); value-level zero/plus laws           *)
(*                       (completed in BladeMonad.v); deduced            *)
(*                       commutativity (9.19/9.20)                       *)
(*                                                                       *)
(* Adjunction status: round trip (here), monoidality                     *)
(* (BladeTrinityAsym), and non-injectivity are checked; morphisms are    *)
(* still undefined, so the adjunction itself remains a conjecture.       *)
(*                                                                       *)
(* Imports BladeDMWF, BladeShape, BladeTrinityAsym, BladeLowering.       *)
(* Coq 8.18, stdlib only.                                                *)
(* ===================================================================== *)

From Blade Require Import BladeDMWF BladeShape BladeTrinityAsym BladeLowering.
Require Import List Arith Lia.
Import ListNotations.

(* ---------------- list plumbing ---------------- *)

Lemma seq_shift' : forall len start,
  map S (seq start len) = seq (S start) len.
Proof.
  induction len as [|len IH]; intros; simpl; [reflexivity |].
  f_equal. apply IH.
Qed.

Lemma nth_seq_id : forall (U : Type) (d : U) (X : list U),
  map (fun i => nth i X d) (seq 0 (length X)) = X.
Proof.
  intros U d. induction X as [|x X IH]; simpl; [reflexivity |].
  f_equal.
  rewrite <- seq_shift', map_map.
  transitivity (map (fun i => nth i X d) (seq 0 (length X))).
  - apply map_ext. intro i. reflexivity.
  - exact IH.
Qed.

Lemma flat_map_singleton : forall (A B : Type) (g : A -> B) (l : list A),
  flat_map (fun x => [g x]) l = map g l.
Proof.
  induction l as [|a l IH]; simpl; [reflexivity | rewrite IH; reflexivity].
Qed.

Lemma fm_ext_in : forall (A B : Type) (f g : A -> list B) (l : list A),
  (forall x, In x l -> f x = g x) -> flat_map f l = flat_map g l.
Proof.
  induction l as [|a l IH]; intros H; simpl; [reflexivity |].
  rewrite (H a (or_introl eq_refl)), IH; [reflexivity |].
  intros x Hx. apply H. right. exact Hx.
Qed.

Lemma firstn_exact : forall (A : Type) (l1 l2 : list A),
  firstn (length l1) (l1 ++ l2) = l1.
Proof.
  induction l1 as [|a l1 IH]; intros; simpl; [reflexivity |].
  f_equal. apply IH.
Qed.

Lemma skipn_exact : forall (A : Type) (l1 l2 : list A),
  skipn (length l1) (l1 ++ l2) = l2.
Proof.
  induction l1 as [|a l1 IH]; intros; simpl; [reflexivity | apply IH].
Qed.

(* ---------------- plans, values, evaluation ---------------- *)

Definition Arr (U : Type) : Type := (Shape * (list nat -> U))%type.

Definition veval {U : Type} (p : Arr U) : list U :=
  map (snd p) (enumShape (fst p)).

(* the trivial-plan embedding: a value as a rank-1 lookup plan *)
Definition pval {U : Type} (d : U) (X : list U) : Arr U :=
  ([mkIx 1 0 (length X)], fun t => nth (hd 0 t) X d).

(* enumShape of a single record equals the record's enumeration, as     *)
(* LIST EQUALITY (upgrades BladeCurrying's membership version).         *)
Lemma enumShape_single_eq : forall c : IxRec,
  enumShape [c] = enumIx c.
Proof.
  intro c. simpl.
  transitivity (flat_map (fun t : list nat => [t]) (enumIx c)).
  - apply fm_ext_in. intros t _. rewrite app_nil_r. reflexivity.
  - rewrite flat_map_singleton. apply map_id.
Qed.

Lemma enum1_singletons : forall l u,
  enum 1 l u = map (fun i => [i]) (seq l (u - l)).
Proof.
  intros. simpl.
  transitivity (flat_map (fun i : nat => [[i]]) (seq l (u - l))).
  - apply fm_ext_in. intros i _. reflexivity.
  - apply flat_map_singleton.
Qed.

(* V o P = id: the counit identity; the checked core of Theorem 2.1.    *)
Theorem veval_pval : forall (U : Type) (d : U) (X : list U),
  veval (pval d X) = X.
Proof.
  intros U d X. unfold veval, pval. cbn [fst snd].
  rewrite enumShape_single_eq.
  unfold enumIx. cbn [ix_r ix_l ix_u].
  rewrite enum1_singletons, map_map, Nat.sub_0_r.
  transitivity (map (fun i => nth i X d) (seq 0 (length X))).
  - apply map_ext. intro i. reflexivity.
  - apply nth_seq_id.
Qed.

(* Theorem 12.7's core: evaluation forgets computational content.       *)
Theorem veval_not_injective :
  exists p q : Arr nat, p <> q /\ veval p = veval q.
Proof.
  exists ([mkIx 1 0 1], fun _ => 0), ([mkIx 1 0 1], fun t => hd 0 t).
  split.
  - intro E.
    assert (E2 : (fun _ : list nat => 0) = (fun t => hd 0 t))
      by exact (f_equal snd E).
    assert (E3 : 0 = 1) by exact (f_equal (fun h => h [1]) E2).
    discriminate.
  - reflexivity.
Qed.

(* ---------------- application: <@> over two arrays ---------------- *)

Definition swidth (s : Shape) : nat :=
  fold_right (fun c acc => ix_r c + acc) 0 s.

Lemma enumShape_tuple_length : forall s t,
  In t (enumShape s) -> length t = swidth s.
Proof.
  induction s as [|c s IH]; intros t H; simpl in *.
  - destruct H as [<- | []]. reflexivity.
  - apply in_flat_map in H as (th & Hth & Hm).
    apply in_map_iff in Hm as (tr & <- & Htr).
    rewrite app_length.
    apply enum_sound in Hth. apply canonical_length in Hth.
    rewrite Hth. f_equal. apply IH. exact Htr.
Qed.

Section Apply2.
  Variables T1 T2 U : Type.
  Variable f : T1 -> T2 -> U.
  Variable A : (Shape * (list nat -> T1))%type.
  Variable B : (Shape * (list nat -> T2))%type.

  (* fuse-then-enumerate: the fused primitive over the joint shape *)
  Definition fuse2 : Arr U :=
    (fst A ++ fst B,
     fun t => f (snd A (firstn (swidth (fst A)) t))
                (snd B (skipn (swidth (fst A)) t))).

  (* reify-arguments-then-apply: the method route *)
  Definition margs2 : list (T1 * T2) :=
    map (fun t => (snd A (firstn (swidth (fst A)) t),
                   snd B (skipn (swidth (fst A)) t)))
        (enumShape (fst A ++ fst B)).

  (* Theorem 12.2 (Rank-0 Convergence), value level: the two routes     *)
  (* coincide.  Thin by design in this semantics (both are maps); the   *)
  (* typed-path convergence belongs to the surface calculus.            *)
  Theorem rank0_convergence :
    map (fun p => f (fst p) (snd p)) margs2 = veval fuse2.
  Proof.
    unfold margs2, veval, fuse2. simpl.
    rewrite map_map. reflexivity.
  Qed.

  (* The semantic heart of <@>: fused evaluation equals the pairwise    *)
  (* product evaluation.                                                *)
  Theorem fuse2_pairs :
    veval fuse2
    = flat_map (fun t1 => map (fun t2 => f (snd A t1) (snd B t2))
                              (enumShape (fst B)))
               (enumShape (fst A)).
  Proof.
    unfold veval, fuse2. simpl.
    rewrite enumShape_monoid_hom. unfold sprod.
    rewrite map_flat_map.
    apply fm_ext_in. intros t1 Ht1.
    rewrite map_map.
    apply map_ext. intro t2.
    assert (L : length t1 = swidth (fst A))
      by (apply enumShape_tuple_length; exact Ht1).
    rewrite <- L, firstn_exact, skipn_exact. reflexivity.
  Qed.
End Apply2.

(* ---------------- composition: Theorem 12.1 and the slots ----------- *)

(* kernel-slot composition (>>@, elementwise fragment) *)
Definition pipe {U W : Type} (p : Arr U) (g : U -> W) : Arr W :=
  (fst p, fun t => g (snd p t)).

(* Theorem 12.1 (Compose-Apply Duality): compose-then-evaluate =        *)
(* evaluate-then-compose.  The proof is map_map: the duality exists     *)
(* because the loop is one reified list.                                *)
Theorem compose_apply_duality : forall (U W : Type) (p : Arr U) (g : U -> W),
  veval (pipe p g) = map g (veval p).
Proof.
  intros. unfold veval, pipe. simpl. symmetry. apply map_map.
Qed.

(* array-slot composition: materialize, re-embed as a trivial plan,     *)
(* apply.  The interchange law: both slot compositions agree under      *)
(* evaluation -- the 2-slot instance of                                 *)
(* curryings <-> slot-classes <-> composition operators.                *)
Theorem slot_interchange : forall (U W : Type) (d : U) (p : Arr U) (g : U -> W),
  veval (pipe (pval d (veval p)) g) = veval (pipe p g).
Proof.
  intros.
  rewrite !compose_apply_duality, veval_pval. reflexivity.
Qed.

(* ---------------- rank 0: wrap/unwrap (12.3, 12.4) ---------------- *)

Definition wrap0 {U : Type} (v : U) : Arr U := ([], fun _ => v).

Lemma veval_wrap0 : forall (U : Type) (v : U), veval (wrap0 v) = [v].
Proof. reflexivity. Qed.

Lemma unwrap_wrap0 : forall (U : Type) (d v : U),
  hd d (veval (wrap0 v)) = v.
Proof. reflexivity. Qed.

(* Corollary 12.3 (Idempotence): re-wrapping the unwrap is identity.    *)
(* Corollary 12.4 (pseudo-native foundation) follows: rank-0 values     *)
(* commit to neither route (rank0_convergence at empty shapes).         *)
Corollary wrap0_idempotent : forall (U : Type) (d v : U),
  wrap0 (hd d (veval (wrap0 v))) = wrap0 v.
Proof. intros. rewrite unwrap_wrap0. reflexivity. Qed.

(* ---------------- MonadPlus, value level ---------------- *)

(* zero = empty computation; plus = concatenation of results.  The      *)
(* full law set, including plan-level plus, is in BladeMonad.v.          *)

Definition vzero {U : Type} : list U := [].
Definition vplus {U : Type} (X Y : list U) : list U := X ++ Y.

Lemma vplus_zero_l : forall (U : Type) (X : list U), vplus vzero X = X.
Proof. reflexivity. Qed.

Lemma vplus_zero_r : forall (U : Type) (X : list U), vplus X vzero = X.
Proof. intros. apply app_nil_r. Qed.

Lemma vplus_assoc : forall (U : Type) (X Y Z : list U),
  vplus (vplus X Y) Z = vplus X (vplus Y Z).
Proof. intros. symmetry. apply app_assoc. Qed.

(* pipelining distributes over plus *)
Lemma pipe_distributes : forall (U W : Type) (g : U -> W) (X Y : list U),
  map g (vplus X Y) = vplus (map g X) (map g Y).
Proof. intros. apply map_app. Qed.

(* ---------------- deduced commutativity (9.19, 9.20) ---------------- *)

Section DeducedCommutativity.
  (* Corollary 9.19: any g composed with a symmetric accessor is        *)
  (* commutative.                                                       *)
  Variables T W : Type.
  Variable Sy : nat -> nat -> T.
  Hypothesis HSy : forall i j, Sy i j = Sy j i.
  Variable g : T -> W.

  Theorem raise_compose :
    invariant_under nat W (fun v => g (Sy (v 0) (v 1))) swap.
  Proof.
    intro v. simpl. f_equal. apply HSy.
  Qed.
End DeducedCommutativity.

Section DeducedCommutativity2.
  (* Theorem 9.20: S(i,j) * A(i) * A(j) is commutative in (i, j) for    *)
  (* commutative associative *.                                         *)
  Variable W : Type.
  Variable op : W -> W -> W.
  Hypothesis Hc : forall x y, op x y = op y x.
  Hypothesis Ha : forall x y z, op (op x y) z = op x (op y z).
  Variable Sy : nat -> nat -> W.
  Hypothesis HSy : forall i j, Sy i j = Sy j i.
  Variable A : nat -> W.

  Theorem deduced_commutativity :
    invariant_under nat W
      (fun v => op (op (Sy (v 0) (v 1)) (A (v 0))) (A (v 1))) swap.
  Proof.
    intro v. simpl.
    rewrite (HSy (v 1) (v 0)).
    rewrite !Ha. f_equal. apply Hc.
  Qed.
End DeducedCommutativity2.
