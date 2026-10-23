(* Connection to Blade's existing layout results, checked with Rocq 9.0.1.
   No axiom declarations or Admitted. Section hypotheses below are genuine
   obligations for a compiler instantiation, not already-established facts.

   Compile with -Q . Blade in _CoqProject order.
   This establishes a mathematical bridge, not verification of F# lowering.
*)
From Stdlib Require Import List Arith Lia.
From Blade Require Import BladeLayout.
From Blade Require Import BladeCompositionalOptimization.
Import ListNotations.
Local Open Scope nat_scope.
Module B := BladeLayout.
Module C := BladeCompositionalOptimization.

(* The forward reduction action uses BladeLayout's existing semantic
   certificate, separately from the structural traversal obligation.
   This does not license changing an IEEE tree without its own permission. *)
Theorem forward_reduction_certificate :
  forall (op : nat -> nat -> nat) a n g,
  (forall x y, op x y = op y x) ->
  (forall x y z, op x (op y z) = op (op x y) z) ->
  B.reduce_bwd op a n g = B.reduce_fwd op a n g.
Proof. exact B.direction_is_free_gauge. Qed.

Section ForwardLayout.
  Variable d : nat.
  Variable G refs : list (list nat).

  (* Fixed entry/exit interface and lowering template are parameters of
     this checker. Admission must certify canonical-read semantics,
     legal contiguous curried traversal, numerical permissions, and this
     exact boundary. A conversion kernel may read its source in storage
     order and write permuted destination addresses; the contract is not
     globally increasing destination writes or positive loop increments.
     General strided array views are not implicitly admitted. *)
  Variable admissible : B.sched -> bool.
  Definition admissible_space : list B.sched :=
    filter admissible (B.sched_space d G refs).

  (* The empty fallback has NO admissibility guarantee. Clients must
     establish nonemptiness before treating this as a valid realization. *)
  Definition forward_canon : B.sched :=
    match admissible_space with
    | [] => B.canon_sched d G refs
    | s :: rest => B.foldpick B.sched B.sched_cost B.sched_key s rest
    end.

  Theorem forward_canon_in_space :
    admissible_space <> [] -> In forward_canon admissible_space.
  Proof.
    intro Hne. unfold forward_canon.
    destruct admissible_space as [|s0 rest] eqn:E.
    - exfalso. apply Hne. reflexivity.
    - exact (B.foldpick_in B.sched B.sched_cost B.sched_key rest s0).
  Qed.

  Theorem forward_canon_minimal : forall s,
    In s admissible_space ->
    B.sched_cost forward_canon <= B.sched_cost s.
  Proof.
    intros s Hs. unfold forward_canon.
    destruct admissible_space as [|s0 rest] eqn:E.
    - contradiction.
    - exact (B.foldpick_min B.sched B.sched_cost B.sched_key rest s0 s Hs).
  Qed.

  Theorem forward_canon_certificate :
    admissible_space <> [] ->
    In forward_canon (B.sched_space d G refs) /\
    admissible forward_canon = true /\
    (forall s, In s (B.sched_space d G refs) ->
       admissible s = true ->
       B.sched_cost forward_canon <= B.sched_cost s).
  Proof.
    intro Hne.
    pose proof (forward_canon_in_space Hne) as Hbest.
    unfold admissible_space in Hbest. apply filter_In in Hbest.
    destruct Hbest as [Hi Ha]. split; [exact Hi |].
    split; [exact Ha |]. intros s Hs Has.
    apply forward_canon_minimal. unfold admissible_space.
    apply filter_In; split; assumption.
  Qed.

  (* For a family that requires zero order disagreement, a positive
     minimum is infeasibility, not permission to run a nonzero schedule.
     Zero here is BladeLayout's Kendall metric, not a hardware-time claim. *)
  Theorem zero_order_feasible_iff_selected_zero :
    admissible_space <> [] ->
    ((exists s, In s admissible_space /\ B.sched_cost s = 0) <->
     B.sched_cost forward_canon = 0).
  Proof.
    intro Hne; split.
    - intros [s [Hs Hz]]. pose proof (forward_canon_minimal s Hs). lia.
    - intro Hz. exists forward_canon; split.
      + apply forward_canon_in_space; exact Hne.
      + exact Hz.
  Qed.

  (* Reuse canonical_is_cost_minimal DIRECTLY when its chosen schedule
     passes the checker. Otherwise use the filtered foldpick above. *)
  Theorem existing_canon_if_admissible :
    B.sched_space d G refs <> [] ->
    admissible (B.canon_sched d G refs) = true ->
    In (B.canon_sched d G refs) admissible_space /\
    (forall s, In s admissible_space ->
       B.sched_cost (B.canon_sched d G refs) <= B.sched_cost s).
  Proof.
    intros Hne Ha. split.
    - unfold admissible_space. apply filter_In. split.
      + apply B.canonical_is_in_space; exact Hne.
      + exact Ha.
    - intros s Hs. unfold admissible_space in Hs.
      apply filter_In in Hs. destruct Hs as [Hi _].
      apply B.canonical_is_cost_minimal; exact Hi.
  Qed.

  (* A local stride policy should keep every minimum when later resource
     costs differ, rather than silently throwing away equal-stride ties. *)
  Definition minimum_stride_space : list B.sched :=
    filter
      (fun s => Nat.eqb (B.sched_cost s) (B.sched_cost forward_canon))
      admissible_space.

  Theorem minimum_stride_space_spec : forall s,
    In s minimum_stride_space <->
    In s admissible_space /\
    B.sched_cost s = B.sched_cost forward_canon.
  Proof.
    intro s. unfold minimum_stride_space.
    rewrite filter_In, Nat.eqb_eq. reflexivity.
  Qed.

  Variable realizes_boundary_soundly_and_forward : B.sched -> Prop.
  Hypothesis checker_sound : forall s,
    In s (B.sched_space d G refs) -> admissible s = true ->
    realizes_boundary_soundly_and_forward s.

  Theorem selected_realization_is_sound_and_forward :
    admissible_space <> [] ->
    realizes_boundary_soundly_and_forward forward_canon.
  Proof.
    intro Hne. destruct (forward_canon_certificate Hne) as [Hi [Ha _]].
    apply checker_sound; assumption.
  Qed.

  (* A SUFFICIENT additional resource bridge. BladeLayout proves the
     stride inequality, not this resource factorization or monotonicity.
     If these do not hold, keep alternatives on the resource frontier. *)
  Variable resource : B.sched -> C.cost.
  Variable cost_of_stride : nat -> C.cost.
  Hypothesis resource_factor : forall s, In s admissible_space ->
    resource s = cost_of_stride (B.sched_cost s).
  Hypothesis resource_monotone : forall x y,
    x <= y -> C.cost_le (cost_of_stride x) (cost_of_stride y).

  Theorem layout_minimum_lifts_to_resource_dominance :
    admissible_space <> [] ->
    forall s, In s admissible_space ->
    C.cost_le (resource forward_canon) (resource s).
  Proof.
    intros Hne s Hs.
    rewrite (resource_factor forward_canon (forward_canon_in_space Hne)).
    rewrite (resource_factor s Hs).
    apply resource_monotone. apply forward_canon_minimal; exact Hs.
  Qed.

  Theorem layout_replacement_in_cost_context :
    admissible_space <> [] ->
    forall s prefix suffix, In s admissible_space ->
    C.cost_le
      (C.compose (C.compose prefix (resource forward_canon)) suffix)
      (C.compose (C.compose prefix (resource s)) suffix).
  Proof.
    intros Hne s prefix suffix Hs.
    apply C.contextual_cost_replacement.
    apply layout_minimum_lifts_to_resource_dominance; assumption.
  Qed.
End ForwardLayout.

Print Assumptions forward_canon_certificate.
Print Assumptions forward_reduction_certificate.
Print Assumptions existing_canon_if_admissible.
Print Assumptions selected_realization_is_sound_and_forward.
Print Assumptions layout_minimum_lifts_to_resource_dominance.
Print Assumptions layout_replacement_in_cost_context.
Print Assumptions zero_order_feasible_iff_selected_zero.
