(* ===================================================================== *)
(* BladeCurrying.v -- Layer 2 of the Blade proof system                  *)
(*                                                                       *)
(* Part A -- THE ARROW DEPENDENCE BOUNDARY (Chris's principle):          *)
(*   an arrow is the maximal dependence-closed unit of iteration.        *)
(*   - rect_arrow_redundant: trivial feedback factors -- a rectilinear   *)
(*     2-dim arrow is extensionally two independent 1-dim arrows, so     *)
(*     RectIdx is unnecessary (HM currying composes the factors).        *)
(*   - sym_arrow_not_factorable: SymIdx's domain is not a rectangle --   *)
(*     no per-dimension predicates reproduce i <= j.  Dependence forces  *)
(*     the dimensions into ONE arrow.                                    *)
(*                                                                       *)
(* Part B -- RELATIONALITY AND THE TWO MAXIMAL CURRYINGS:                *)
(*   - identity_needs_every_position (Lemma 9.23): any detector that     *)
(*     ignores even one array position cannot compute the identity       *)
(*     relation.                                                         *)
(*   - comm_needs_kernel (Lemma 9.24): no function of arrays alone       *)
(*     decides kernel commutativity.                                     *)
(*   - two_maximal_curryings (Theorem 9.26): over the currying lattice,  *)
(*     the only proper, non-wasteful, detection-bearing specifications   *)
(*     are all-arrays-no-kernel (method_for) and kernel-only             *)
(*     (object_for).  method_spec_ok / object_spec_ok witness existence. *)
(*                                                                       *)
(* Imports BladeDMWF (Layers 0-1).  Coq 8.18, stdlib only.               *)
(* ===================================================================== *)

From Blade Require Import BladeDMWF.
Require Import List Arith Lia.
Import ListNotations.

(* ===================================================================== *)
(* Part A: the arrow dependence boundary                                 *)
(* ===================================================================== *)

(* Helper: a single-arrow shape enumerates exactly the arrow. *)
Lemma enumShape_single : forall (c : IxRec) (t : list nat),
  In t (enumShape [c]) <-> In t (enumIx c).
Proof.
  intros c t. simpl. split.
  - intro H. apply in_flat_map in H as (t' & Ht' & Hm).
    destruct Hm as [Et | []].
    rewrite app_nil_r in Et. subst t'. exact Ht'.
  - intro H. apply in_flat_map. exists t. split; [exact H |].
    left. apply app_nil_r.
Qed.

(* Trivial feedback factors: a hypothetical rectilinear 2-dim arrow      *)
(* (RectIdx) has exactly the same tuples as the two-arrow shape          *)
(* [Idx<n>; Idx<m>].  This is why RectIdx is unnecessary: orthogonal     *)
(* dimensions separate into independent arrows, composed by the curried  *)
(* (HM) array type.                                                      *)
Theorem rect_arrow_redundant : forall n m i j,
  In [i; j] (enumShape [mkIx 1 0 n; mkIx 1 0 m]) <-> (i < n /\ j < m).
Proof.
  intros n m i j. simpl. split.
  - intro H. apply in_flat_map in H as (t & Ht & Hm).
    apply in_map_iff in Hm as (t2 & Et & Ht2).
    (* t is a canonical 1-tuple of the first arrow *)
    apply enum_sound in Ht.
    destruct t as [|x [|y ys]]; simpl in Ht;
      [contradiction | | destruct Ht as [_ []]].
    destruct Ht as [Hx _].
    (* t2 is a canonical 1-tuple of the second arrow *)
    assert (Ht2' : In t2 (enumIx (mkIx 1 0 m))).
    { apply enumShape_single. exact Ht2. }
    apply enum_sound in Ht2'.
    destruct t2 as [|z [|w ws]]; simpl in Ht2';
      [contradiction | | destruct Ht2' as [_ []]].
    destruct Ht2' as [Hy _].
    simpl in Et. injection Et as -> ->.
    split; lia.
  - intros [Hi Hj]. apply in_flat_map. exists [i]. split.
    + apply enum_complete. simpl. split; [lia | exact I].
    + apply in_map_iff. exists [j]. split; [reflexivity |].
      apply enumShape_single. apply enum_complete. simpl. split; [lia | exact I].
Qed.

(* Non-trivial feedback does NOT factor: the symmetric arrow's domain    *)
(* is not a rectangle.  No per-dimension predicates P, Q reproduce the   *)
(* constraint i <= j: (0,0) and (1,1) are canonical, so a product form   *)
(* would be forced to admit (1,0).  Dependence between dimensions is     *)
(* exactly what makes them ONE arrow.                                    *)
Theorem sym_arrow_not_factorable : forall n, 2 <= n ->
  ~ exists (P Q : nat -> Prop),
      forall i j, canonical 2 0 n [i; j] <-> (P i /\ Q j).
Proof.
  intros n Hn (P & Q & H).
  assert (H00 : P 0 /\ Q 0).
  { apply H. simpl. repeat split; lia. }
  assert (H11 : P 1 /\ Q 1).
  { apply H. simpl. repeat split; lia. }
  assert (H10 : canonical 2 0 n [1; 0]).
  { apply H. split; [apply H11 | apply H00]. }
  simpl in H10. lia.
Qed.

(* ===================================================================== *)
(* Part B: relationality -- the two information sources are disjoint     *)
(* ===================================================================== *)

(* Array bindings at a call site, as a function position -> array id.   *)
(* (Functional representation avoids list-index bookkeeping.)            *)

(* Lemma 9.23 (Identity Detection): any relation-detector that IGNORES   *)
(* even one position p cannot compute the identity relation.  Hence      *)
(* identity is detectable only when ALL arrays are bound.                *)
Definition updpos (p v : nat) (A : nat -> nat) : nat -> nat :=
  fun q => if q =? p then v else A q.

Theorem identity_needs_every_position :
  forall r p, 2 <= r -> p < r ->
  forall g : (nat -> nat) -> nat -> nat -> bool,
    (forall A A', (forall q, q <> p -> A q = A' q) ->
                  forall a b, g A a b = g A' a b) ->
    ~ (forall A a b, a < r -> b < r -> (g A a b = true <-> A a = A b)).
Proof.
  intros r p Hr Hp g Hign Hspec.
  set (A1 := fun _ : nat => 0).
  set (A2 := updpos p 1 A1).
  set (q0 := if p =? 0 then 1 else 0).
  assert (Hq0p : q0 <> p).
  { unfold q0. destruct (p =? 0) eqn:E.
    - apply Nat.eqb_eq in E. lia.
    - apply Nat.eqb_neq in E. lia. }
  assert (Hq0r : q0 < r).
  { unfold q0. destruct (p =? 0); lia. }
  assert (Hagree : forall q, q <> p -> A1 q = A2 q).
  { intros q Hq. unfold A2, updpos.
    destruct (q =? p) eqn:E; [apply Nat.eqb_eq in E; congruence | reflexivity]. }
  assert (E12 : g A1 p q0 = g A2 p q0) by (apply (Hign A1 A2 Hagree)).
  assert (T1 : g A1 p q0 = true).
  { apply (proj2 (Hspec A1 p q0 Hp Hq0r)). reflexivity. }
  assert (T2 : A2 p = A2 q0).
  { apply (proj1 (Hspec A2 p q0 Hp Hq0r)). rewrite <- E12. exact T1. }
  unfold A2, updpos in T2. rewrite Nat.eqb_refl in T2.
  destruct (q0 =? p) eqn:E; [apply Nat.eqb_eq in E; congruence |].
  unfold A1 in T2. discriminate.
Qed.

(* Lemma 9.24 (Commutativity Detection): no function of the ARRAYS       *)
(* decides commutativity of the (unseen) kernel.                         *)
Definition commutative (f : nat -> nat -> nat) := forall x y, f x y = f y x.

Theorem comm_needs_kernel :
  forall g : (nat -> nat) -> bool,
    ~ (forall (f : nat -> nat -> nat) (A : nat -> nat),
         g A = true <-> commutative f).
Proof.
  intros g H.
  assert (Hplus : commutative Nat.add) by (intros x y; apply Nat.add_comm).
  assert (Ht : g (fun _ => 0) = true).
  { apply (proj2 (H Nat.add (fun _ => 0))). exact Hplus. }
  assert (Hfst : commutative (fun x _ => x)).
  { apply (proj1 (H (fun x _ => x) (fun _ => 0))). exact Ht. }
  specialize (Hfst 0 1). simpl in Hfst. discriminate.
Qed.

(* (Lemma 9.25, disjoint sources, is the conjunction: 9.23 shows arrays  *)
(* carry the identity information and nothing less suffices; 9.24 shows  *)
(* the kernel carries the commutativity information and arrays carry     *)
(* none.  Both are now checked; 9.26 builds on exactly these two facts.) *)

(* ===================================================================== *)
(* Theorem 9.26: THE TWO MAXIMAL CURRYINGS.                              *)
(* A currying specification chooses which of the r array positions and   *)
(* whether the kernel are bound.  Justified by 9.23/9.24:                *)
(*   - identity is detected iff ALL positions are bound (detects_id),    *)
(*   - commutativity is detected iff the kernel is bound (detects_comm). *)
(* A spec is PROPER if it is not the full application (which is          *)
(* terminal, not a currying), and WASTELESS if removing any bound        *)
(* element would lose some detection it contributes to.                  *)
(* The theorem: the only proper, wasteless, detection-bearing specs are  *)
(*   { all positions, no kernel }  -- method_for                         *)
(*   { kernel only }               -- object_for                         *)
(* ===================================================================== *)

Section TwoMaximalCurryings.
  Variable r : nat.
  Hypothesis Hr : 1 <= r.

  Record Spec := mkSpec { hasK : bool; hasPos : nat -> bool }.

  Definition allPos (S : Spec) : Prop :=
    forall p, p < r -> hasPos S p = true.

  Definition detects_id (S : Spec) : Prop := allPos S.
  Definition detects_comm (S : Spec) : Prop := hasK S = true.

  Definition full (S : Spec) : Prop := hasK S = true /\ allPos S.
  Definition proper (S : Spec) : Prop := ~ full S.

  Definition dropK (S : Spec) : Spec :=
    {| hasK := false; hasPos := hasPos S |}.
  Definition dropP (p : nat) (S : Spec) : Spec :=
    {| hasK := hasK S; hasPos := fun q => if q =? p then false else hasPos S q |}.

  Definition wastedK (S : Spec) : Prop :=
    (detects_id (dropK S) <-> detects_id S) /\
    (detects_comm (dropK S) <-> detects_comm S).
  Definition wastedP (p : nat) (S : Spec) : Prop :=
    (detects_id (dropP p S) <-> detects_id S) /\
    (detects_comm (dropP p S) <-> detects_comm S).

  Definition wasteless (S : Spec) : Prop :=
    (hasK S = true -> ~ wastedK S) /\
    (forall p, p < r -> hasPos S p = true -> ~ wastedP p S).

  Theorem two_maximal_curryings :
    forall S : Spec,
      proper S ->
      (detects_id S \/ detects_comm S) ->
      wasteless S ->
      (hasK S = true /\ (forall p, p < r -> hasPos S p = false))   (* object_for *)
      \/ (hasK S = false /\ allPos S).                             (* method_for *)
  Proof.
    intros S Hproper Hdet [HwK HwP].
    destruct (hasK S) eqn:HK.
    - (* kernel bound: every bound position would be waste *)
      left. split; [reflexivity |].
      intros p Hpr. destruct (hasPos S p) eqn:HP; [exfalso | reflexivity].
      (* detects_id S must already fail, else S is full *)
      assert (Hnid : ~ allPos S).
      { intro Hall. apply Hproper. split; [exact HK | exact Hall]. }
      apply (HwP p Hpr HP). split.
      + (* identity detection: false on both sides *)
        split; intro Hx; exfalso.
        * specialize (Hx p Hpr). simpl in Hx.
          rewrite Nat.eqb_refl in Hx. discriminate.
        * exact (Hnid Hx).
      + (* comm detection: unchanged by dropping a position *)
        unfold detects_comm. simpl. tauto.
    - (* kernel unbound: the detection must be identity *)
      right. split; [reflexivity |].
      destruct Hdet as [Hid | Hcm]; [exact Hid |].
      unfold detects_comm in Hcm. rewrite HK in Hcm. discriminate.
  Qed.

  (* Existence: both canonical curryings satisfy the premises. *)
  Definition method_spec : Spec := {| hasK := false; hasPos := fun _ => true |}.
  Definition object_spec : Spec := {| hasK := true; hasPos := fun _ => false |}.

  Lemma method_spec_ok :
    proper method_spec /\ detects_id method_spec /\ wasteless method_spec.
  Proof.
    split; [| split].
    - intros [HK _]. simpl in HK. discriminate.
    - intros p _. reflexivity.
    - split.
      + intro HK. simpl in HK. discriminate.
      + intros p Hpr _ [Hid _].
        assert (Ha : allPos (dropP p method_spec)).
        { apply (proj2 Hid). intros q _. reflexivity. }
        specialize (Ha p Hpr). simpl in Ha.
        rewrite Nat.eqb_refl in Ha. discriminate.
  Qed.

  Lemma object_spec_ok :
    proper object_spec /\ detects_comm object_spec /\ wasteless object_spec.
  Proof.
    split; [| split].
    - intros [_ Hall].
      assert (H0 : 0 < r) by lia.
      specialize (Hall 0 H0). simpl in Hall. discriminate.
    - reflexivity.
    - split.
      + intros _ [_ Hc].
        assert (Hx : hasK (dropK object_spec) = true).
        { apply (proj2 Hc). reflexivity. }
        simpl in Hx. discriminate.
      + intros p _ Hpres. simpl in Hpres. discriminate.
  Qed.
End TwoMaximalCurryings.

(* ===================================================================== *)
(* Notes toward L3 (Trinity):                                            *)
(*  - The <*> fold closure (positive half of Theorem 9.6): concatenation *)
(*    of Shapes is the monoid, fold builds arbitrary-arity iteration --  *)
(*    already latent in enumShape over list append.                      *)
(*  - Output type family Out r (Theorems 9.2/9.5): non-constancy across  *)
(*    arity, mirroring residual_not_constant across depth.               *)
(*  - The arrow-as-coalgebra generalization (heads + residual map) to    *)
(*    admit Antisym (residual lower bound i+1) and CompoundIdx           *)
(*    (mask-conditioned residual), subsuming Part A's dichotomy:         *)
(*    constant residual <-> factorable into separate arrows.             *)
(* ===================================================================== *)
