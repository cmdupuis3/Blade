(* A standalone proposed Rocq development for compositional optimization.
   Intended for Rocq 9.x, stdlib only. No Blade imports, axioms or Admitted.

   Checked with Rocq 9.0.1. No compiler implementation is assumed verified.

   The state is the COMPLETE continuation interface. Its equality must
   preserve available continuations and their incremental costs. This is
   a substantive instantiation obligation, not something proved here for
   Blade's compiler. The theorem is relative to the supplied transition
   system at each fixed depth; a bound can collect these frontiers or use
   certified zero-cost terminal padding. It is not hardware-time optimality.
*)

From Stdlib Require Import List Arith Lia Bool.
Import ListNotations.

(* Structural functions are pointwise read transformations. This module
   formalizes the read and unsigned packing laws, not a complete typed
   AST or decidable normalization of arbitrary coordinate functions. *)
Module StructuralReindex.
  Definition reindex {X Y V : Type} (pi : Y -> X) (f : X -> V) : Y -> V :=
    fun y => f (pi y).

  Theorem reindex_composes : forall (X Y Z V : Type)
      (pi : Y -> X) (sigma : Z -> Y) (f : X -> V) z,
    reindex sigma (reindex pi f) z =
    reindex (fun z => pi (sigma z)) f z.
  Proof. intros; reflexivity. Qed.

  Theorem reindex_inverse : forall (X Y V : Type)
      (pi : Y -> X) (rho : X -> Y) (f : X -> V),
    (forall x, pi (rho x) = x) ->
    forall x, reindex rho (reindex pi f) x = f x.
  Proof. intros X Y V pi rho f H x; unfold reindex; rewrite H; reflexivity. Qed.

  (* The producer's canonicalization remains inside the transported read. *)
  Theorem canonical_transpose_read : forall (X Y R V : Type)
      (canon : X -> R) (cells : R -> V) (pi : Y -> X) y,
    reindex pi (fun x => cells (canon x)) y = cells (canon (pi y)).
  Proof. intros; reflexivity. Qed.

  (* A target representative need not be the transported source
     representative. It suffices to land in the same source orbit.
     Values, including machine scalars, are never re-evaluated here. *)
  Theorem transported_canonical_invariance :
    forall (X Y R V : Type) (canonX : X -> R)
      (canonY : Y -> Y) (pi : Y -> X) (cells : R -> V),
    (forall y, canonX (pi (canonY y)) = canonX (pi y)) ->
    forall y,
      reindex pi (fun x => cells (canonX x)) (canonY y) =
      reindex pi (fun x => cells (canonX x)) y.
  Proof.
    intros X Y R V canonX canonY pi cells H y.
    unfold reindex. rewrite H. reflexivity.
  Qed.

  Section Packing.
    Context {X R Slot V : Type}.
    Variable embed : R -> X.
    Variable canon : X -> R.
    Variable rank : R -> Slot.
    Variable enumerate : Slot -> R.
    Hypothesis enumerate_rank : forall r, enumerate (rank r) = r.
    Hypothesis rank_enumerate : forall k, rank (enumerate k) = k.
    Variable f : X -> V.
    Hypothesis reconstruction : forall x, f (embed (canon x)) = f x.

    Definition pack (k : Slot) : V := f (embed (enumerate k)).
    Definition unpack (p : Slot -> V) (x : X) : V := p (rank (canon x)).

    Theorem pack_readback : forall x, unpack pack x = f x.
    Proof.
      intro x. unfold unpack, pack. rewrite enumerate_rank.
      apply reconstruction.
    Qed.

    (* Slot may be a finite ordinal, including an empty ordinal.
       No total natural-number enumeration of an empty R is required.
       A finite slot traversal in order gives exactly that output order. *)
    Theorem packing_emits_in_slot_order : forall slots : list Slot,
      map rank (map enumerate slots) = slots.
    Proof.
      induction slots as [|k ks IH]; simpl; [reflexivity |].
      rewrite rank_enumerate, IH. reflexivity.
    Qed.
  End Packing.

  (* Ordered segments of one canonical slot traversal compose. This is
     not a requirement that every conversion kernel's destination writes
     occur in increasing address order. *)
  Theorem forward_segments_compose : forall k j,
    seq 0 k ++ seq k j = seq 0 (k + j).
  Proof. intros; rewrite seq_app; simpl; reflexivity. Qed.
End StructuralReindex.

Record cost : Type := mkCost {
  work : nat;
  traffic : nat;
  peak : nat
}.

Definition cost_le (a b : cost) : Prop :=
  work a <= work b /\ traffic a <= traffic b /\ peak a <= peak b.

Definition zero_cost : cost := mkCost 0 0 0.

(* peak b is the absolute live footprint during the continuation/step,
   including incoming resident values. It is not just new allocations. *)
Definition compose (a b : cost) : cost :=
  mkCost (work a + work b) (traffic a + traffic b)
         (Nat.max (peak a) (peak b)).

Lemma cost_le_refl : forall c, cost_le c c.
Proof. intros; unfold cost_le; auto. Qed.

Lemma cost_le_trans : forall a b c,
  cost_le a b -> cost_le b c -> cost_le a c.
Proof. unfold cost_le; intros; lia. Qed.

Definition cost_le_dec (a b : cost) : {cost_le a b} + {~ cost_le a b}.
Proof.
  destruct (le_dec (work a) (work b));
  destruct (le_dec (traffic a) (traffic b));
  destruct (le_dec (peak a) (peak b));
  unfold cost_le; [left | right | right | right | right | right | right | right];
  tauto.
Defined.

Lemma compose_monotone : forall a a' b b',
  cost_le a a' -> cost_le b b' -> cost_le (compose a b) (compose a' b').
Proof.
  intros a a' b b' [Hw [Ht Hp]] [Hw' [Ht' Hp']].
  unfold cost_le, compose; simpl.
  repeat split; try lia.
Qed.

Lemma compose_assoc : forall a b c,
  compose (compose a b) c = compose a (compose b c).
Proof.
  intros [aw atra ap] [bw bt bp] [cw ct cp].
  unfold compose; simpl.
  f_equal; try lia; rewrite Nat.max_assoc; reflexivity.
Qed.

(* A fixed continuation, summarized by k, cannot reverse dominance. *)
Theorem continuation_preserves_dominance : forall a b k,
  cost_le a b -> cost_le (compose a k) (compose b k).
Proof.
  intros. apply compose_monotone; auto using cost_le_refl.
Qed.

Theorem contextual_cost_replacement : forall prefix local_best local_old suffix,
  cost_le local_best local_old ->
  cost_le (compose (compose prefix local_best) suffix)
          (compose (compose prefix local_old) suffix).
Proof.
  intros prefix local_best local_old suffix H.
  apply compose_monotone; [| apply cost_le_refl].
  apply compose_monotone; [apply cost_le_refl | exact H].
Qed.

(* For use by a concrete lowerer: admission is an executable filter, and
   admissible_sound must establish semantic and structural traversal legality.
   Completeness relative to all legal actions is a separate obligation. *)
Section CertifiedSuccessors.
  Context {state : Type}.
  Variable candidates : state -> list (state * cost).
  Variable admit : state -> (state * cost) -> bool.
  Variable legal : state -> (state * cost) -> Prop.
  Hypothesis admissible_sound : forall s edge,
    In edge (candidates s) -> admit s edge = true -> legal s edge.
  Definition certified_successors (s : state) :=
    filter (admit s) (candidates s).
  Theorem certified_successors_legal : forall s edge,
    In edge (certified_successors s) -> legal s edge.
  Proof.
    intros s edge H. unfold certified_successors in H.
    apply filter_In in H. destruct H as [Hi Ha].
    eapply admissible_sound; eauto.
  Qed.
End CertifiedSuccessors.

Section Frontier.
  Context {state : Type}.
  Variable state_eq_dec : forall a b : state, {a = b} + {a <> b}.

  Definition label := (state * cost)%type.

  Definition dominates (a b : label) : Prop :=
    fst a = fst b /\ cost_le (snd a) (snd b).

  Lemma dominates_refl : forall a, dominates a a.
  Proof. intros; split; auto using cost_le_refl. Qed.

  Lemma dominates_trans : forall a b c,
    dominates a b -> dominates b c -> dominates a c.
  Proof.
    intros a b c [Hab Hc1] [Hbc Hc2].
    split; [congruence | eapply cost_le_trans; eauto].
  Qed.

  Definition dominates_dec (a b : label) :
    {dominates a b} + {~ dominates a b}.
  Proof.
    destruct (state_eq_dec (fst a) (fst b));
    destruct (cost_le_dec (snd a) (snd b));
    unfold dominates; [left | right | right | right]; tauto.
  Defined.

  Definition dominatesb (a b : label) : bool :=
    if dominates_dec a b then true else false.

  Lemma dominatesb_true : forall a b,
    dominatesb a b = true <-> dominates a b.
  Proof.
    intros; unfold dominatesb; destruct (dominates_dec a b);
      split; intro H; try assumption; try reflexivity; try discriminate;
      contradiction.
  Qed.

  (* Ties keep the old representative. If nothing dominates x, insert x
     and remove those old labels that x dominates. *)
  Definition insert (x : label) (xs : list label) : list label :=
    if existsb (fun y => dominatesb y x) xs then xs
    else x :: filter (fun y => negb (dominatesb x y)) xs.

  Fixpoint prune (xs : list label) : list label :=
    match xs with
    | [] => []
    | x :: rest => insert x (prune rest)
    end.

  Definition covers (xs ys : list label) : Prop :=
    forall y, In y ys -> exists x, In x xs /\ dominates x y.

  Lemma covers_refl : forall xs, covers xs xs.
  Proof.
    intros xs y Hy; exists y; split; auto using dominates_refl.
  Qed.

  Lemma covers_trans : forall xs ys zs,
    covers xs ys -> covers ys zs -> covers xs zs.
  Proof.
    intros xs ys zs Hxy Hyz z Hz.
    destruct (Hyz z Hz) as [y [Hy Hdy]].
    destruct (Hxy y Hy) as [x [Hx Hdx]].
    exists x; split; [exact Hx | eapply dominates_trans; eauto].
  Qed.

  Lemma insert_sound : forall x xs z,
    In z (insert x xs) -> In z (x :: xs).
  Proof.
    intros x xs z H; unfold insert in H.
    destruct (existsb (fun y => dominatesb y x) xs).
    - right; exact H.
    - simpl in H. destruct H as [H | H].
      + left; exact H.
      + right; apply filter_In in H; tauto.
  Qed.

  Lemma insert_covers : forall x xs, covers (insert x xs) (x :: xs).
  Proof.
    intros x xs y Hy; unfold insert.
    destruct (existsb (fun z => dominatesb z x) xs) eqn:He.
    - apply existsb_exists in He.
      destruct He as [z [Hz Hd]].
      simpl in Hy. destruct Hy as [Hy | Hy].
      + subst y. exists z; split; [exact Hz | apply dominatesb_true; exact Hd].
      + exists y; split; auto using dominates_refl.
    - simpl in Hy. destruct Hy as [Hy | Hy].
      + subst y. exists x; split; [left; reflexivity | apply dominates_refl].
      + destruct (dominatesb x y) eqn:Hd.
        * exists x; split; [left; reflexivity | apply dominatesb_true; exact Hd].
        * exists y; split; [right | apply dominates_refl].
          apply filter_In; split; [exact Hy | rewrite Hd; reflexivity].
  Qed.

  Lemma prune_sound : forall xs, incl (prune xs) xs.
  Proof.
    induction xs as [|x xs IH]; intros z Hz; simpl in *.
    - contradiction.
    - apply insert_sound in Hz. simpl in Hz.
      destruct Hz as [Hz | Hz]; [left; exact Hz | right; apply IH; exact Hz].
  Qed.

  Lemma prune_covers : forall xs, covers (prune xs) xs.
  Proof.
    induction xs as [|x xs IH]; intros y Hy; simpl in *.
    - contradiction.
    - destruct Hy as [Hy | Hy].
      + subst y. apply insert_covers. left; reflexivity.
      + destruct (IH y Hy) as [z [Hz Hd]].
        destruct (insert_covers x (prune xs) z (or_intror Hz))
          as [w [Hw Hdw]].
        exists w; split; [exact Hw | eapply dominates_trans; eauto].
  Qed.

  (* A compiler instantiation must retain whatever the continuation needs:
     root/version, coordinate map, ordered structural frame, fixed canonical
     evaluation, resident/shared layouts, numerical commitments and effects.
     At element-level granularity it also needs traversal cursors; at whole
     kernel boundaries a per-cell cursor may be unnecessary. succ contains
     only certified steps satisfying the structural traversal contract.
     Costs depend on this state alone. *)
  Variable succ : state -> list (state * cost).

  Definition advance (xs : list label) : list label :=
    flat_map
      (fun sc => map (fun edge => (fst edge, compose (snd sc) (snd edge)))
                     (succ (fst sc))) xs.

  Lemma advance_in : forall xs z,
    In z (advance xs) <->
    exists p edge, In p xs /\ In edge (succ (fst p)) /\
                   z = (fst edge, compose (snd p) (snd edge)).
  Proof.
    intros xs z; unfold advance. rewrite in_flat_map. split.
    - intros [p [Hp Hz]]. apply in_map_iff in Hz.
      destruct Hz as [edge [He Hi]].
      exists p, edge. split; [exact Hp |].
      split; [exact Hi | symmetry; exact He].
    - intros [p [edge [Hp [Hi He]]]].
      exists p; split; [exact Hp |].
      apply in_map_iff. exists edge; split; [symmetry; exact He | exact Hi].
  Qed.

  Lemma advance_sound : forall xs ys,
    incl xs ys -> incl (advance xs) (advance ys).
  Proof.
    intros xs ys H z Hz. apply advance_in in Hz.
    destruct Hz as [p [edge [Hp [Hi He]]]].
    apply advance_in. exists p, edge; repeat split; auto.
  Qed.

  Lemma advance_covers : forall xs ys,
    covers xs ys -> covers (advance xs) (advance ys).
  Proof.
    intros xs ys H z Hz. apply advance_in in Hz.
    destruct Hz as [p [edge [Hp [Hi He]]]].
    destruct (H p Hp) as [q [Hq [Hs Hc]]].
    exists (fst edge, compose (snd q) (snd edge)). split.
    - apply advance_in. exists q, edge; repeat split; auto.
      rewrite Hs. exact Hi.
    - subst z. split; [reflexivity |].
      simpl. apply compose_monotone; auto using cost_le_refl.
  Qed.

  Variable start : state.
  Variable initial_cost : cost.

  Fixpoint exhaustive (n : nat) : list label :=
    match n with
    | 0 => [(start, initial_cost)]
    | S k => advance (exhaustive k)
    end.

  Fixpoint frontier (n : nat) : list label :=
    match n with
    | 0 => [(start, initial_cost)]
    | S k => prune (advance (frontier k))
    end.

  Theorem frontier_sound : forall n, incl (frontier n) (exhaustive n).
  Proof.
    induction n as [|n IH].
    - intros z Hz; exact Hz.
    - simpl. intros z Hz. apply prune_sound in Hz.
      apply (advance_sound _ _ IH z Hz).
  Qed.

  Theorem frontier_complete_up_to_dominance : forall n,
    covers (frontier n) (exhaustive n).
  Proof.
    induction n as [|n IH].
    - apply covers_refl.
    - simpl. eapply covers_trans.
      + apply prune_covers.
      + apply advance_covers; exact IH.
  Qed.

  Variable terminal : state -> Prop.
  Variable objective : cost -> nat.
  Hypothesis objective_monotone : forall a b,
    cost_le a b -> objective a <= objective b.

  Theorem optimal_frontier_is_globally_optimal : forall n best,
    In best (frontier n) -> terminal (fst best) ->
    (forall q, In q (frontier n) -> terminal (fst q) ->
       objective (snd best) <= objective (snd q)) ->
    In best (exhaustive n) /\
    (forall p, In p (exhaustive n) -> terminal (fst p) ->
       objective (snd best) <= objective (snd p)).
  Proof.
    intros n best Hb Hbt Hmin. split.
    - apply frontier_sound; exact Hb.
    - intros p Hp Hpt.
      destruct (frontier_complete_up_to_dominance n p Hp)
        as [q [Hq [Hs Hc]]].
      assert (Hqt : terminal (fst q)) by (rewrite Hs; exact Hpt).
      specialize (Hmin q Hq Hqt).
      pose proof (objective_monotone _ _ Hc). lia.
  Qed.

  (* Cost optimality alone says nothing about result correctness.
     A separate invariant establishes semantic soundness of every step. *)
  Variable invariant : state -> Prop.
  Hypothesis initial_sound : invariant start.
  Hypothesis step_sound : forall s edge,
    invariant s -> In edge (succ s) -> invariant (fst edge).

  Lemma advance_invariant : forall xs,
    (forall p, In p xs -> invariant (fst p)) ->
    forall q, In q (advance xs) -> invariant (fst q).
  Proof.
    intros xs H q Hq. apply advance_in in Hq.
    destruct Hq as [p [edge [Hp [Hi He]]]].
    subst q; simpl. eapply step_sound.
    - exact (H p Hp).
    - exact Hi.
  Qed.

  Lemma exhaustive_invariant : forall n p,
    In p (exhaustive n) -> invariant (fst p).
  Proof.
    induction n as [|n IH]; intros p Hp.
    - simpl in Hp. destruct Hp as [Hp | Hp]; [subst p; exact initial_sound | contradiction].
    - simpl in Hp. eapply advance_invariant; [exact IH | exact Hp].
  Qed.

  Theorem frontier_invariant : forall n p,
    In p (frontier n) -> invariant (fst p).
  Proof.
    intros n p Hp. apply (exhaustive_invariant n p).
    apply frontier_sound; exact Hp.
  Qed.
End Frontier.

(* A concrete necessary condition for using an abstract interface as the
   sole input to the continuation-cost model. One collision refutes it. *)
Theorem interface_collision_refutes_cost_factorization :
  forall (concrete abstract : Type) (summary : concrete -> abstract)
         (future_cost : concrete -> nat) x y,
  summary x = summary y -> future_cost x <> future_cost y ->
  ~ exists predicted : abstract -> nat,
      forall s, predicted (summary s) = future_cost s.
Proof.
  intros concrete abstract summary future_cost x y He Hne [predicted Hp].
  apply Hne. rewrite <- (Hp x), <- (Hp y), He. reflexivity.
Qed.

(* Failure of ignoring the continuation interface: same logical result,
   different resident layout/cache state, different future work. *)
Inductive mode := CheapLayout | ReusableLayout.
Definition prefix_cost (m : mode) : nat :=
  match m with CheapLayout => 1 | ReusableLayout => 2 end.
Definition suffix_cost (m : mode) : nat :=
  match m with CheapLayout => 10 | ReusableLayout => 0 end.

Example locally_cheapest_is_globally_worse :
  prefix_cost CheapLayout < prefix_cost ReusableLayout /\
  prefix_cost ReusableLayout + suffix_cost ReusableLayout <
  prefix_cost CheapLayout + suffix_cost CheapLayout.
Proof. cbv; lia. Qed.

Example a_scalar_cannot_select_both_resource_minima :
  let p := mkCost 1 0 10 in
  let q := mkCost 2 0 1 in
  ~ cost_le p q /\ ~ cost_le q p.
Proof. unfold cost_le; simpl; lia. Qed.
