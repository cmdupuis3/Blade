(* ===================================================================== *)
(* BladeTrinityAsym.v -- the three pillars are not co-equal.             *)
(*                                                                       *)
(* Loop reification and dimensional currying GENERATE; arity             *)
(* polymorphism is the closure they force.  Witness for the first two    *)
(* without the third: the original unary C++ object_for prototype.       *)
(*                                                                       *)
(*   sprod + unit/assoc laws     the product of iteration spaces is a    *)
(*                               monoid (laws checked, not asserted)     *)
(*   ap_semantics_is_free_fold   the n-ary enumerator is the fold of a   *)
(*                               binary step over reified spaces --      *)
(*                               arity is list length, not a primitive   *)
(*   nary_generated_by_unary     every n-ary loop is a product of        *)
(*                               unary loops                             *)
(*   enumShape_monoid_hom        evaluation preserves the product        *)
(*                               exactly, as list equality               *)
(*                                                                       *)
(* What is NOT emergent: typing the closure needs type families indexed  *)
(* by arity (output_family_not_constant, BladeTrinity) -- the same kind  *)
(* of dependency dimensional currying already requires                   *)
(* (residual_not_constant, BladeDMWF).  No new resource.                 *)
(*                                                                       *)
(* Imports BladeDMWF, BladeArrow, BladeTrinity.  Coq 8.18, stdlib only.  *)
(* ===================================================================== *)

From Blade Require Import BladeDMWF BladeArrow BladeTrinity.
Require Import List Arith Lia Setoid.
Import ListNotations.

(* ---------------- list plumbing ---------------- *)

Lemma fm_app : forall (A B : Type) (f : A -> list B) (l1 l2 : list A),
  flat_map f (l1 ++ l2) = flat_map f l1 ++ flat_map f l2.
Proof.
  induction l1 as [|a l1 IH]; intros; simpl; [reflexivity |].
  rewrite IH, app_assoc. reflexivity.
Qed.

Lemma flat_map_map : forall (A B C : Type) (f : A -> B) (g : B -> list C) l,
  flat_map g (map f l) = flat_map (fun x => g (f x)) l.
Proof.
  induction l as [|a l IH]; simpl; [reflexivity | rewrite IH; reflexivity].
Qed.

Lemma map_flat_map : forall (A B C : Type) (f : A -> list B) (g : B -> C) l,
  map g (flat_map f l) = flat_map (fun x => map g (f x)) l.
Proof.
  induction l as [|a l IH]; simpl; [reflexivity |].
  rewrite map_app, IH. reflexivity.
Qed.

Lemma flat_map_flat_map :
  forall (A B C : Type) (f : A -> list B) (g : B -> list C) l,
  flat_map g (flat_map f l) = flat_map (fun x => flat_map g (f x)) l.
Proof.
  induction l as [|a l IH]; simpl; [reflexivity |].
  rewrite fm_app, IH. reflexivity.
Qed.

Lemma concat_map_singleton : forall (A : Type) (s : list A),
  concat (map (fun c => [c]) s) = s.
Proof.
  induction s as [|a s IH]; simpl; [reflexivity | rewrite IH; reflexivity].
Qed.

Lemma Forall2_map_l :
  forall (A B C : Type) (f : A -> B) (P : B -> C -> Prop) (l : list A) (l' : list C),
  Forall2 P (map f l) l' <-> Forall2 (fun x y => P (f x) y) l l'.
Proof.
  intros A B C f P l. induction l as [|a l IH]; intros l'; simpl;
    split; intro H.
  - inversion H. constructor.
  - inversion H. constructor.
  - inversion H as [| x y lx ly Hp Hf]; subst.
    constructor; [exact Hp | apply IH; exact Hf].
  - inversion H as [| x y lx ly Hp Hf]; subst.
    constructor; [exact Hp | apply IH; exact Hf].
Qed.

(* ---------------- the applicative monoid on semantic plans ----------- *)

(* sprod is <*> on evaluated plans: pairwise concatenation of tuples.    *)
Definition sprod (l1 l2 : list (list nat)) : list (list nat) :=
  flat_map (fun t => map (app t) l2) l1.

Lemma sprod_unit_l : forall l, sprod [ [] ] l = l.
Proof.
  intro l. unfold sprod. simpl. rewrite app_nil_r.
  induction l as [|x l IH]; simpl; [reflexivity |].
  rewrite IH. reflexivity.
Qed.

Lemma sprod_cons : forall x l1 l2,
  sprod (x :: l1) l2 = map (app x) l2 ++ sprod l1 l2.
Proof. reflexivity. Qed.

Lemma sprod_unit_r : forall l, sprod l [ [] ] = l.
Proof.
  induction l as [|x l IH]; [reflexivity |].
  rewrite sprod_cons, IH. simpl. rewrite app_nil_r. reflexivity.
Qed.

Lemma sprod_assoc : forall a b c,
  sprod (sprod a b) c = sprod a (sprod b c).
Proof.
  intros a b c. unfold sprod.
  rewrite flat_map_flat_map.
  apply flat_map_ext. intro t.
  rewrite flat_map_map, map_flat_map.
  apply flat_map_ext. intro x.
  rewrite map_map.
  apply map_ext. intro y. symmetry. apply app_assoc.
Qed.

(* ---------------- AP-emergence ---------------- *)

(* The n-ary enumerator consumes one reified arrow at a time via <*>.   *)
Lemma enumShape_cons : forall c s,
  enumShape (c :: s) = sprod (enumIx c) (enumShape s).
Proof. reflexivity. Qed.

(* THE EMERGENCE THEOREM: the arbitrary-arity enumerator is the free    *)
(* fold of the binary combinator over the list of reified arrows.       *)
(* enumShape is ONE function; nothing in it is indexed by arity --      *)
(* arity is the length of a runtime list of values.  Semantically, AP   *)
(* is a THEOREM about {LR, DC}, not an axiom beside them.               *)
Theorem ap_semantics_is_free_fold : forall s : Shape,
  enumShape s
  = fold_right (fun c acc => sprod (enumIx c) acc) [ [] ] s.
Proof.
  induction s as [|c s IH]; simpl; [reflexivity |].
  rewrite <- IH. apply enumShape_cons.
Qed.

(* Every n-ary loop is generated by UNARY loops under <*>: the direct   *)
(* formalization of the unary object_for prototype generates the        *)
(* language (via BladeCurrying.enumShape_single, In p (enumShape [c])  *)
(* is exactly membership in the one-arrow loop).                        *)
Theorem nary_generated_by_unary : forall (s : Shape) (t : list nat),
  In t (enumShape s) <->
  exists parts : list (list nat),
    t = concat parts /\
    length parts = length s /\
    Forall2 (fun c p => In p (enumShape [c])) s parts.
Proof.
  intros s t.
  rewrite <- (concat_map_singleton _ s) at 1.
  rewrite trinity_fold_closure.
  split; intros (parts & Et & Hlen & HF); exists parts.
  - rewrite map_length in Hlen.
    split; [exact Et | split; [exact Hlen |]].
    exact (proj1 (Forall2_map_l IxRec Shape (list nat)
                    (fun c => [c]) (fun sh p => In p (enumShape sh))
                    s parts) HF).
  - split; [exact Et | split].
    + rewrite map_length. exact Hlen.
    + exact (proj2 (Forall2_map_l IxRec Shape (list nat)
                      (fun c => [c]) (fun sh p => In p (enumShape sh))
                      s parts) HF).
Qed.

(* ---------------- monoidal evaluation (the V -| P fragment) ---------- *)

(* EVALUATION IS A STRICT MONOID HOMOMORPHISM from (Shape, ++, []) to   *)
(* (semantic plans, sprod, [[]]): list equality, not mere membership    *)
(* equivalence.  Categorically: V is a strict monoidal functor.  This   *)
(* is the checked core of the V -| P story; together with               *)
(* ap_semantics_is_free_fold it says the closure that AP names is the   *)
(* image under V of the free monoid on reified arrows.                  *)
Theorem enumShape_monoid_hom : forall s1 s2 : Shape,
  enumShape (s1 ++ s2) = sprod (enumShape s1) (enumShape s2).
Proof.
  induction s1 as [|c s1 IH]; intros s2.
  - symmetry. apply sprod_unit_l.
  - change ((c :: s1) ++ s2) with (c :: (s1 ++ s2)).
    rewrite !enumShape_cons, IH.
    symmetry. apply sprod_assoc.
Qed.

(* ===================================================================== *)
(* Status of the V-P adjunction: monoidality here; the round trip and    *)
(* non-injectivity of evaluation are in BladeCompute.v.  Morphisms are   *)
(* still undefined, so the adjunction itself remains a conjecture.       *)
(* ===================================================================== *)
