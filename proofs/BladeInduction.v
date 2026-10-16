(* ===================================================================== *)
(* BladeInduction.v -- THE INDUCTIVE SIDE: recursive arrays as the       *)
(* induction along the leading axis.                                     *)
(* (docs/plans/plan-one-model-surface-calculus.md, item 5;               *)
(*  docs/formalism.md 7.5 and 7.5.1)                                     *)
(*                                                                       *)
(* An index type is a finite set with a group action (BladeObject: the   *)
(* DMWF is the quotient) AND a well-founded order on its leading axis.   *)
(* A let rec is the structural induction along that order.  This file    *)
(* builds it on the one object (decision D3: not on the fragment of      *)
(* BladeReduceCompiler).                                                 *)
(*                                                                       *)
(* PART A.  The semantics as a well-founded fixpoint.  A step            *)
(* step : nat -> (Z -> V) -> V  receives the new ordinal and the PREFIX  *)
(* READ; lr_buf is the one buffer, each slice appended once (TRMC);      *)
(* the read is zread: a bounds test on the ordinal, zero outside [0, n). *)
(*   letrec_is_wf_fixpoint   rec_build step N n = step n (prefix_read n) *)
(*                           for n < N, the prefix read being the family *)
(*                           at extent n (prefix_read_spec: zero outside *)
(*                           [0, n), rec_build at any N >= n inside,     *)
(*                           prefix_read_at).  Close to definitional:    *)
(*                           its content is the coherence of extents.    *)
(*   letrec_fixpoint_self    the SELF-referential equation: rec_build N  *)
(*                           solves the recurrence whose prefix read is  *)
(*                           rec_build N itself, zero-extended           *)
(*   letrec_unique           any x : nat -> V solving those equations on *)
(*                           [0, N) agrees with rec_build there          *)
(*                           (induction on lt_wf).  With the previous    *)
(*                           line: rec_build is THE unique solution      *)
(*   letrec_prefix_stable    extents N <= N' agree on [0, N): reading    *)
(*                           the binding at a smaller extent             *)
(*                           instantiates the family                     *)
(*   letrec_respects_zero_history  steps that agree on every zero-       *)
(*                           extended read build the same buffer         *)
(*   letrec_zero_history     the AB3 law: reading prefix (n - j)         *)
(*                           UNCONDITIONALLY builds what the hand-       *)
(*                           guarded if n >= j then .. else ZERO builds  *)
(*   seeded, seeded_zero     the seed arm  | zero :: s -> zero :: seed   *)
(*                                                                       *)
(* PART B.  Work, in BladeOptimal's oracle model (the step is the        *)
(* oracle; a question is an ordinal and the read it is applied to).      *)
(*   letrec_work             the sweep computes lr_buf and asks exactly  *)
(*                           N questions, ordinals 0 .. N-1 in order,    *)
(*                           each with its prefix binding                *)
(*   letrec_lower_bound      (abstract) if every ordinal is perturbable  *)
(*                           in the law class, every correct program     *)
(*                           asks at every ordinal: N questions at least *)
(*   letrec_work_optimal     instance: every R-local class (causal,      *)
(*                           windowed, Markov, all) -- forced and        *)
(*                           attained, N                                 *)
(*   letrec_work_shape       slices over a shape s: N * shapeCard s      *)
(*                           questions, one per (ordinal, orbit)         *)
(*   letrec_work_shape_lower_bound   for orbit-invariant cell steps      *)
(*                           every (ordinal, orbit) pair is a            *)
(*                           perturbable class: N * shapeCard s forced   *)
(*   letrec_work_shape_optimal  the sweep meets that bound's correctness *)
(*                           premise (sweep_out) and asks exactly        *)
(*                           N * shapeCard s: forced and attained        *)
(*   The lower bounds are a RE-PROOF over prog / run / trace / run_agree *)
(*   in the adversary style of orbit_lower_bound, not an instance of it: *)
(*   the output at ordinal n is not k (args n) for a fixed args -- it    *)
(*   depends on the oracle's answers at earlier ordinals -- so the       *)
(*   argument needs lr_buf_agree_below (tbuf_agree_below): perturbing    *)
(*   ordinal n leaves the prefix below n untouched.                      *)
(*                                                                       *)
(* PART C.  Composition with the group side.  The quotient side: slices  *)
(* are BladeObject arrays over s stored one cell per orbit (tbuf), the   *)
(* step reads the stored prefix through the canonicalizing read, and the *)
(* trajectory traj is DEFINED as the Arr over mkIx 1 0 N :: s -- that    *)
(* the recursion axis leads is the definition.  The dense side: dbuf,    *)
(* slices as functions on ALL tuples, reads not canonicalized -- what    *)
(* the program means.  The theorems:                                     *)
(*   scanon_lead, block_perm_lead   a rank-1 leading record is never     *)
(*                           permuted by the orbit relation              *)
(*   tprefix_is_qread        the stored prefix read IS qread of the      *)
(*                           stored slice (through qread_is_storage)     *)
(*   dense_agrees_with_quotient  for an orbit-invariant step (orb_inv)   *)
(*                           that reads its prefix at dense tuples       *)
(*                           (rd_dense_ext), the dense recurrence at a   *)
(*                           dense cell (n, t) equals the quotient's     *)
(*                           qread at (n :: t): induction along the      *)
(*                           leading axis, invariance within each slice  *)
(*   qread_traj_is_plain_read  under orb_inv the canonicalizing read is  *)
(*                           the plain read; orb_inv_needed_pin shows a  *)
(*                           non-invariant step where they differ        *)
(*   letrec_commutes_with_quotient  under orb_inv and rd_dense_ext: the  *)
(*                           trajectory stores exactly N * shapeCard s   *)
(*                           cells (the concatenated buffer), agrees     *)
(*                           with the dense recurrence at every dense    *)
(*                           cell, reads by storage lookup, never moves  *)
(*                           the ordinal, and each dense slice carries   *)
(*                           the symmetry of the remaining axes          *)
(*   CAVEAT: orb_inv asks invariance under EVERY prefix reader and       *)
(*   rd_dense_ext quantifies over malformed tuples too; this file's own  *)
(*   sym_step meets neither (sym_step_not_orb_inv,                       *)
(*   sym_step_not_rd_dense_ext), so the theorems above do not apply to   *)
(*   it.  The WEAKER premises, which they imply (orb_inv_gives_sym,      *)
(*   rd_dense_ext_gives_sym):                                            *)
(*   orb_inv_sym             invariance only for ORBIT-INVARIANT readers *)
(*                           (inv_reader: symmetric on valid dense       *)
(*                           tuples), at valid dense cells only          *)
(*   rd_dense_ext_sym        at dense cells, invariant readers agreeing  *)
(*                           on dense tuples give the same answer        *)
(*   dense_prefix_is_orbit_invariant  under them, EVERY generated dense  *)
(*                           prefix read is an orbit-invariant reader    *)
(*                           and agrees with the stored prefix on dense  *)
(*                           tuples (induction on the ordinal)           *)
(*   dense_agrees_with_quotient_sym, letrec_commutes_with_quotient_sym   *)
(*                           the same agreement and the same five-part   *)
(*                           composition law under the weaker premises   *)
(*   sym_step_commutes_with_quotient  sym_step meets them over ANY single *)
(*                           record [mkIx r l u]: dense = quotient at    *)
(*                           every N, ordinal and dense cell, slices     *)
(*                           symmetric, every prefix orbit-invariant --  *)
(*                           the pins below are instances                *)
(*                                                                       *)
(* PART D.  The while guard (7.5.1).  gbuf is the guarded sweep with a   *)
(* running flag; gstop the first ordinal whose guard is false.           *)
(*   gbuf_spec               closed form: lr_buf to gstop, then the      *)
(*                           frozen slice repeated, flag = no stop       *)
(*   letrec_guard_total      N slices at the declared extent             *)
(*   letrec_guard_prefix_agrees  below the stop, guarded = unguarded     *)
(*                           (the guard only stops EARLY)                *)
(*   letrec_guard_frozen     from the stop on, the slice at stop - 1     *)
(*                           (zero if the guard failed at ordinal 0)     *)
(*   letrec_guard_budget     the BL8010 abort (status None) iff the      *)
(*                           guard held at every ordinal of the budget   *)
(*   letrec_guard_stops      a stop is a false guard after true ones     *)
(*   letrec_seeded_guard     seed arm under a guard (seeded_guard: the   *)
(*                           guard is not consulted at 0, as the         *)
(*                           compiler's loop starts at 1): the stop is   *)
(*                           at least 1 and slice 0 is the seed          *)
(*                                                                       *)
(* PART E.  The calculus hook: rec_arr, the Arr nat over [mkIx 1 0 N]    *)
(* an ERec constructor would denote.                                     *)
(*   letrec_veval            veval (rec_arr step N) is the buffer, the   *)
(*                           slices in order; qread at [n] is the        *)
(*                           recurrence                                  *)
(*   letrec_scalar_is_shape_nil  the scalar recurrence is the s = []     *)
(*                           instance of the shaped one                  *)
(*                                                                       *)
(* Pins (vm_compute): fib_pin (seeded Fibonacci with zero history, 55 at *)
(* ordinal 9), guard_frozen_pin (doubling frozen at 16 from ordinal 5;   *)
(* budget 4 aborts), seeded_guard_pin, rec_arr_pin, sym_traj_pin (24     *)
(* cells, (3,2,0) and (3,0,2) read alike), sym_dense_pin (the dense      *)
(* recurrence agrees), orb_inv_needed_pin.  The buffers let-bind the     *)
(* prefix, so the pins evaluate in time linear in N.                     *)
(*                                                                       *)
(* Scope, stated once.  Reads are INTEGER-indexed (Z): a nat read would  *)
(* truncate n - 3 to 0 at n < 3 and silently read slice 0, so the        *)
(* implicit-zero rule needs the integer ordinal (the task's              *)
(* nat -> (nat -> V) -> V signature becomes nat -> (Z -> V) -> V).       *)
(* Slices are a generic V with a zero (Parts A, B-abstract, D) or nat    *)
(* and Arr nat over a shape (B-shape, C, E); values stay nat (D2).  The  *)
(* step is modelled as a function of (ordinal, prefix read), so          *)
(* productivity and termination are by construction (structural          *)
(* recursion on the ordinal) and the static-extent fence is the nat N.   *)
(* NOT modelled: the storage policy (rolling window, K+1 slots --        *)
(* formalism: storage is policy), nested let rec inside a slice (DP      *)
(* tables), AD through recurrences, the guard's Bool typing, and an      *)
(* ERec constructor in BladeSurface (Part E shows the object it would    *)
(* denote).  Hypotheses: ext_step (the step sees its read pointwise) is  *)
(* assumed by letrec_fixpoint_self, letrec_unique and                    *)
(* letrec_scalar_is_shape_nil; orb_inv and rd_dense_ext by the original  *)
(* Part C agreement theorems, the weaker orb_inv_sym and rd_dense_ext_sym *)
(* by their _sym versions (discharged for sym_step); nothing else       *)
(* assumes anything.                                                     *)
(*                                                                       *)
(* Imports BladeDMWF, BladeShape, BladeTrinityAsym, BladeBinomial,       *)
(* BladeCompute, BladeMonad, BladeMixedRadix, BladeOptimal, BladeObject. *)
(* Coq 8.18 / Rocq 9, stdlib only.                                       *)
(* ===================================================================== *)

From Blade Require Import BladeDMWF BladeShape BladeTrinityAsym BladeBinomial
                          BladeCompute BladeMonad BladeMixedRadix BladeOptimal
                          BladeObject.
Require Import List Arith Lia ZArith Bool Permutation.
Import ListNotations.

(* ===================================================================== *)
(* PART A.  THE SEMANTICS AS A WELL-FOUNDED FIXPOINT.                    *)
(* ===================================================================== *)

Section Rec.
  Variable V : Type.
  Variable vzero : V.

  (* zero-extended read of a buffer holding the slices [0, n): a bounds   *)
  (* test on the recursion ordinal, zero outside (implicit zero history). *)
  Definition zread (b : list V) (n : nat) (k : Z) : V :=
    if ((0 <=? k)%Z && (k <? Z.of_nat n)%Z)%bool
    then nth (Z.to_nat k) b vzero else vzero.

  (* the one buffer: slice m is written once, from the read of the       *)
  (* buffer at extent m (the prefix), and appended (the snoc of TRMC).    *)
  Fixpoint lr_buf (step : nat -> (Z -> V) -> V) (n : nat) : list V :=
    match n with
    | O => []
    | S m => let b := lr_buf step m in b ++ [step m (zread b m)]
    end.

  Definition rec_build (step : nat -> (Z -> V) -> V) (N n : nat) : V :=
    nth n (lr_buf step N) vzero.

  (* the prefix binding of the inductive arm: the same family at extent n *)
  Definition prefix_read (step : nat -> (Z -> V) -> V) (n : nat) : Z -> V :=
    zread (lr_buf step n) n.

  Lemma lr_buf_length : forall step n, length (lr_buf step n) = n.
  Proof.
    intros step n. induction n as [|n IH]; simpl; [reflexivity |].
    rewrite app_length, IH. simpl. lia.
  Qed.

  Lemma lr_buf_extends : forall step n m, n <= m ->
    exists rest, lr_buf step m = lr_buf step n ++ rest.
  Proof.
    intros step n m Hle. induction Hle as [|m Hle IH].
    - exists []. rewrite app_nil_r. reflexivity.
    - destruct IH as (rest & E). simpl. rewrite E.
      exists (rest ++ [step m (zread (lr_buf step n ++ rest) m)]).
      rewrite app_assoc. reflexivity.
  Qed.

  (* coherence of the family: extents n <= m agree on [0, n) *)
  Lemma rec_build_stable : forall step n m k, n <= m -> k < n ->
    rec_build step n k = rec_build step m k.
  Proof.
    intros step n m k Hle Hk. unfold rec_build.
    destruct (lr_buf_extends step n m Hle) as (rest & E). rewrite E.
    rewrite app_nth1; [reflexivity | rewrite lr_buf_length; exact Hk].
  Qed.

  Lemma rec_build_last : forall step n,
    rec_build step (S n) n = step n (prefix_read step n).
  Proof.
    intros step n. unfold rec_build, prefix_read. simpl.
    rewrite app_nth2 by (rewrite lr_buf_length; lia).
    rewrite lr_buf_length, Nat.sub_diag. reflexivity.
  Qed.

  (* the prefix read is the family at extent n, zero outside [0, n) *)
  Lemma prefix_read_spec : forall step n k,
    prefix_read step n k
    = if ((0 <=? k)%Z && (k <? Z.of_nat n)%Z)%bool
      then rec_build step n (Z.to_nat k) else vzero.
  Proof. reflexivity. Qed.

  Lemma prefix_read_at : forall step n N k, n <= N ->
    (0 <= k < Z.of_nat n)%Z ->
    prefix_read step n k = rec_build step N (Z.to_nat k).
  Proof.
    intros step n N k Hle Hk. rewrite prefix_read_spec.
    destruct Hk as [H0 H1].
    apply Z.leb_le in H0. apply Z.ltb_lt in H1. rewrite H0, H1. simpl.
    apply rec_build_stable; [exact Hle |].
    apply Z.ltb_lt in H1. lia.
  Qed.

  Theorem letrec_is_wf_fixpoint : forall step N n, n < N ->
    rec_build step N n = step n (prefix_read step n).
  Proof.
    intros step N n Hn.
    rewrite <- (rec_build_stable step (S n) N n) by lia.
    apply rec_build_last.
  Qed.

  (* past the declared extent the BUFFER reads zero (nth default); this  *)
  (* is a modelling convenience, not language semantics: an external    *)
  (* read past a named extent is BL8006 (formalism 3.10), and only the   *)
  (* prefix reads inside the arms are zero-extended                      *)
  Lemma rec_build_beyond : forall step N n, N <= n -> rec_build step N n = vzero.
  Proof.
    intros step N n Hn. unfold rec_build. apply nth_overflow.
    rewrite lr_buf_length. exact Hn.
  Qed.

  Theorem letrec_prefix_stable : forall step N N', N <= N' ->
    forall n, n < N -> rec_build step N n = rec_build step N' n.
  Proof. intros. apply rec_build_stable; assumption. Qed.

  (* a step is extensional when it sees its read only pointwise; every   *)
  (* step a Blade term denotes is (no function-valued equality is        *)
  (* observable).  Uniqueness needs it and nothing else does.            *)
  Definition ext_step (step : nat -> (Z -> V) -> V) : Prop :=
    forall n p p', (forall k, p k = p' k) -> step n p = step n p'.

  Theorem letrec_unique : forall step, ext_step step -> forall N (x : nat -> V),
    (forall n, n < N ->
       x n = step n (fun k => if ((0 <=? k)%Z && (k <? Z.of_nat n)%Z)%bool
                              then x (Z.to_nat k) else vzero)) ->
    forall n, n < N -> x n = rec_build step N n.
  Proof.
    intros step Hext N x Hx n. induction n as [n IH] using lt_wf_ind. intro Hn.
    rewrite (Hx n Hn), (letrec_is_wf_fixpoint step N n Hn).
    apply Hext. intro k. rewrite prefix_read_spec.
    destruct ((0 <=? k)%Z && (k <? Z.of_nat n)%Z)%bool eqn:Ek; [| reflexivity].
    apply andb_true_iff in Ek as [E0 E1].
    apply Z.leb_le in E0. apply Z.ltb_lt in E1.
    rewrite (rec_build_stable step n N (Z.to_nat k)) by lia.
    apply IH; lia.
  Qed.

  (* the SELF-referential equation: rec_build at extent N solves the     *)
  (* recurrence whose prefix read is rec_build N itself, zero-extended;  *)
  (* with letrec_unique, rec_build is the unique solution               *)
  Theorem letrec_fixpoint_self : forall step, ext_step step -> forall N n, n < N ->
    rec_build step N n
    = step n (fun k => if ((0 <=? k)%Z && (k <? Z.of_nat n)%Z)%bool
                       then rec_build step N (Z.to_nat k) else vzero).
  Proof.
    intros step Hext N n Hn. rewrite (letrec_is_wf_fixpoint step N n Hn).
    apply Hext. intro k. rewrite prefix_read_spec.
    destruct ((0 <=? k)%Z && (k <? Z.of_nat n)%Z)%bool eqn:Ek; [| reflexivity].
    apply andb_true_iff in Ek as [E0 E1]. apply Z.leb_le in E0. apply Z.ltb_lt in E1.
    apply rec_build_stable; lia.
  Qed.

  Lemma zread_outside : forall b n k, ~ (0 <= k < Z.of_nat n)%Z -> zread b n k = vzero.
  Proof.
    intros b n k Hk. unfold zread.
    destruct ((0 <=? k)%Z && (k <? Z.of_nat n)%Z)%bool eqn:E; [| reflexivity].
    exfalso. apply Hk. apply andb_true_iff in E as [E0 E1].
    apply Z.leb_le in E0. apply Z.ltb_lt in E1. lia.
  Qed.

  (* two steps that agree on every zero-extended prefix read build the  *)
  (* same family: the zero history is part of the semantics, so a step  *)
  (* never has to defend against a read off the start of the axis       *)
  Theorem letrec_respects_zero_history : forall step step',
    (forall n p, (forall k, ~ (0 <= k < Z.of_nat n)%Z -> p k = vzero) ->
       step n p = step' n p) ->
    forall n, lr_buf step n = lr_buf step' n.
  Proof.
    intros step step' H n. induction n as [|n IH]; simpl; [reflexivity |].
    rewrite IH. f_equal. f_equal. apply H. intros k Hk. apply zread_outside. exact Hk.
  Qed.

  (* formalism 7.5's AB3 example: a step that reads prefix (n - j)      *)
  (* UNCONDITIONALLY -- an integer read, negative at n < j -- builds    *)
  (* exactly what the hand-guarded if n >= j then prefix (n - j) else   *)
  (* ZERO builds.                                                       *)
  Theorem letrec_zero_history : forall (j : nat) (h : nat -> V -> (Z -> V) -> V) N n,
    rec_build (fun n p => h n (p (Z.of_nat n - Z.of_nat j)%Z) p) N n
    = rec_build (fun n p => h n (if j <=? n then p (Z.of_nat (n - j)) else vzero) p) N n.
  Proof.
    intros j h N n. unfold rec_build. f_equal.
    apply letrec_respects_zero_history. intros m p Hp. f_equal.
    destruct (j <=? m) eqn:E.
    - apply Nat.leb_le in E. f_equal. lia.
    - apply Nat.leb_gt in E. apply Hp. lia.
  Qed.

  (* and the read itself: off the start of the axis is zero, no guard *)
  Lemma prefix_read_negative : forall step n j, n < j ->
    prefix_read step n (Z.of_nat n - Z.of_nat j)%Z = vzero.
  Proof. intros. apply zread_outside. lia. Qed.

  Lemma prefix_read_not_yet : forall step n k, (Z.of_nat n <= k)%Z ->
    prefix_read step n k = vzero.
  Proof. intros. apply zread_outside. lia. Qed.

  (* the seed arm  | zero :: s -> zero :: seed  is a step that answers  *)
  (* the seed at ordinal 0                                               *)
  Definition seeded (seed : V) (step : nat -> (Z -> V) -> V) : nat -> (Z -> V) -> V :=
    fun n p => match n with O => seed | S _ => step n p end.

  Lemma seeded_zero : forall seed step N, 0 < N -> rec_build (seeded seed step) N 0 = seed.
  Proof. intros. rewrite letrec_is_wf_fixpoint by assumption. reflexivity. Qed.

  (* =================================================================== *)
  (* PART B.  WORK.  The step is an oracle (BladeOptimal's prog): a      *)
  (* question is an ordinal and the prefix read the step is applied to.  *)
  (* =================================================================== *)

  Definition kstep (step : nat -> (Z -> V) -> V) : nat * (Z -> V) -> V :=
    fun q => step (fst q) (snd q).

  (* the canonical sweep: ask the step at the next ordinal with the      *)
  (* zero-extended read of the buffer so far, append the answer          *)
  Fixpoint lr_sweep (f n : nat) (b : list V) : prog (nat * (Z -> V)) V (list V) :=
    match f with
    | O => Ret b
    | S f' => Ask (n, zread b n) (fun a => lr_sweep f' (S n) (b ++ [a]))
    end.

  Definition sweep_prog (N : nat) : prog (nat * (Z -> V)) V (list V) := lr_sweep N 0 [].

  Lemma lr_sweep_run : forall step f n,
    run (lr_sweep f n (lr_buf step n)) (kstep step) = lr_buf step (n + f).
  Proof.
    intros step f. induction f as [|f IH]; intro n; simpl.
    - rewrite Nat.add_0_r. reflexivity.
    - unfold kstep at 1. simpl.
      change (lr_buf step n ++ [step n (zread (lr_buf step n) n)]) with (lr_buf step (S n)).
      rewrite IH. f_equal. lia.
  Qed.

  Lemma lr_sweep_trace : forall step f n,
    trace (lr_sweep f n (lr_buf step n)) (kstep step)
    = map (fun m => (m, prefix_read step m)) (seq n f).
  Proof.
    intros step f. induction f as [|f IH]; intro n; simpl; [reflexivity |].
    unfold kstep at 1. simpl.
    change (lr_buf step n ++ [step n (zread (lr_buf step n) n)]) with (lr_buf step (S n)).
    rewrite IH. reflexivity.
  Qed.

  (* the sweep computes the family and asks the step exactly N times,   *)
  (* once per ordinal, in order, each time with the prefix binding      *)
  Theorem letrec_work : forall step N,
    run (sweep_prog N) (kstep step) = lr_buf step N
    /\ trace (sweep_prog N) (kstep step) = map (fun m => (m, prefix_read step m)) (seq 0 N)
    /\ map fst (trace (sweep_prog N) (kstep step)) = seq 0 N
    /\ length (trace (sweep_prog N) (kstep step)) = N.
  Proof.
    intros step N. unfold sweep_prog.
    pose proof (lr_sweep_run step N 0) as Hr. pose proof (lr_sweep_trace step N 0) as Ht.
    simpl lr_buf in Hr, Ht. simpl in Hr.
    split; [exact Hr |]. split; [exact Ht |].
    rewrite Ht, map_map. simpl. rewrite map_id.
    split; [reflexivity | rewrite map_length, seq_length; reflexivity].
  Qed.

  Lemma sweep_correct : forall step N n,
    nth n (run (sweep_prog N) (kstep step)) vzero = rec_build step N n.
  Proof. intros. rewrite (proj1 (letrec_work step N)). reflexivity. Qed.

  (* --- the lower bound: every ordinal is a perturbable class ---------- *)

  Lemma lr_buf_agree_below : forall step step' n,
    (forall m p, m < n -> step m p = step' m p) -> lr_buf step n = lr_buf step' n.
  Proof.
    intros step step' n. induction n as [|n IH]; intro H; simpl; [reflexivity |].
    rewrite IH by (intros; apply H; lia). f_equal. f_equal. apply H. lia.
  Qed.

  Section LowerBound.
    Variable K : (nat -> (Z -> V) -> V) -> Prop.

    (* the law class lets the step move at ordinal n, everywhere along   *)
    (* that ordinal, without moving it at any other ordinal              *)
    Definition ord_perturbable (n : nat) : Prop :=
      exists pert : (nat -> (Z -> V) -> V) -> (nat -> (Z -> V) -> V),
        (forall s, K s -> K (pert s)) /\
        (forall s m p, m <> n -> pert s m p = s m p) /\
        (forall s p, pert s n p <> s n p).

    Theorem letrec_lower_bound :
      forall (O : Type) (out : O -> nat -> V) (P : prog (nat * (Z -> V)) V O) (N : nat),
      (forall n, n < N -> ord_perturbable n) ->
      (forall step, K step -> forall n, n < N -> out (run P (kstep step)) n = rec_build step N n) ->
      forall step, K step ->
        (forall n, n < N -> In n (map fst (trace P (kstep step))))
        /\ N <= length (trace P (kstep step)).
    Proof.
      intros O out P N Hpert Hcor step Hk.
      assert (Hall : forall n, n < N -> In n (map fst (trace P (kstep step)))).
      { intros n Hn.
        destruct (in_dec Nat.eq_dec n (map fst (trace P (kstep step)))) as [Hi | Hni];
          [exact Hi | exfalso].
        destruct (Hpert n Hn) as (pert & HK & Hoff & Hon).
        assert (Hag : forall q, In q (trace P (kstep step)) ->
                        kstep (pert step) q = kstep step q).
        { intros q Hq. unfold kstep. apply Hoff. intro E. apply Hni.
          rewrite <- E. apply in_map. exact Hq. }
        destruct (run_agree _ _ _ P (kstep step) (kstep (pert step)) Hag) as [Hrun _].
        pose proof (Hcor (pert step) (HK step Hk) n Hn) as H1.
        pose proof (Hcor step Hk n Hn) as H2.
        rewrite Hrun, H2 in H1.
        rewrite !(letrec_is_wf_fixpoint _ N n Hn) in H1.
        unfold prefix_read in H1.
        rewrite (lr_buf_agree_below (pert step) step n) in H1
          by (intros m p Hm; apply Hoff; lia).
        apply (Hon step (zread (lr_buf step n) n)). symmetry. exact H1. }
      split; [exact Hall |].
      rewrite <- (map_length fst (trace P (kstep step))).
      rewrite <- (seq_length N 0) at 1.
      apply NoDup_incl_length; [apply seq_NoDup |].
      intros n Hin. apply in_seq in Hin. apply Hall. lia.
    Qed.
  End LowerBound.
End Rec.

(* --- the nat instance: every R-local law class is perturbable --------- *)
(* R n k says which prefix ordinals the step at n may read: causal       *)
(* (0 <= k < n), a depth-w window (n - w <= k < n), Markov (k = n - 1), *)
(* or everything (all extensional steps).  Bumping the step by one at    *)
(* ordinal n keeps it in every such class.                               *)

Definition local_in (R : nat -> Z -> Prop) (step : nat -> (Z -> nat) -> nat) : Prop :=
  forall n p p', (forall k, R n k -> p k = p' k) -> step n p = step n p'.

Definition bump (n0 : nat) (step : nat -> (Z -> nat) -> nat) : nat -> (Z -> nat) -> nat :=
  fun m p => if m =? n0 then S (step m p) else step m p.

Lemma local_in_perturbable : forall R n, ord_perturbable nat (local_in R) n.
Proof.
  intros R n. exists (bump n). split; [| split].
  - intros s Hs m p p' Hp. unfold bump. rewrite (Hs m p p' Hp). reflexivity.
  - intros s m p Hm. unfold bump. apply Nat.eqb_neq in Hm. rewrite Hm. reflexivity.
  - intros s p. unfold bump. rewrite Nat.eqb_refl. lia.
Qed.

(* No program correct for every R-local step asks the step fewer than N *)
(* times, and the sweep asks exactly N: the leading axis costs one      *)
(* question per ordinal, and that is attained.                           *)
Theorem letrec_work_optimal :
  forall (R : nat -> Z -> Prop) (N : nat) (O : Type) (out : O -> nat -> nat)
         (P : prog (nat * (Z -> nat)) nat O),
  (forall step, local_in R step -> forall n, n < N ->
     out (run P (kstep nat step)) n = rec_build nat 0 step N n) ->
  forall step, local_in R step ->
    N <= length (trace P (kstep nat step))
    /\ length (trace (sweep_prog nat 0 N) (kstep nat step)) = N.
Proof.
  intros R N O out P Hcor step Hs. split.
  - apply (proj2 (letrec_lower_bound nat 0 (local_in R) O out P N
                    (fun n _ => local_in_perturbable R n) Hcor step Hs)).
  - apply (letrec_work nat 0 step N).
Qed.

Corollary letrec_work_optimal_causal :
  forall (N : nat) (O : Type) (out : O -> nat -> nat) (P : prog (nat * (Z -> nat)) nat O),
  (forall step, local_in (fun n k => 0 <= k < Z.of_nat n)%Z step -> forall n, n < N ->
     out (run P (kstep nat step)) n = rec_build nat 0 step N n) ->
  forall step, local_in (fun n k => 0 <= k < Z.of_nat n)%Z step ->
    N <= length (trace P (kstep nat step)).
Proof. intros N O out P Hcor step Hs. exact (proj1 (letrec_work_optimal _ N O out P Hcor step Hs)). Qed.

(* --- pin: Fibonacci with a seed and implicit zero history ------------ *)
(* slice 1 reads prefix (-1) = 0 unguarded: 1, 1, 2, 3, 5, ...           *)
Definition fib_step : nat -> (Z -> nat) -> nat :=
  seeded nat 1 (fun n p => p (Z.of_nat n - 1)%Z + p (Z.of_nat n - 2)%Z).

Example fib_pin : rec_build nat 0 fib_step 10 9 = 55
                  /\ lr_buf nat 0 fib_step 10 = [1; 1; 2; 3; 5; 8; 13; 21; 34; 55].
Proof. split; vm_compute; reflexivity. Qed.

(* ===================================================================== *)
(* PART C.  COMPOSITION WITH THE GROUP SIDE.                             *)
(* The quotient side: slices are arrays over a shape s, functions on its *)
(* transversal (BladeObject's Arr nat), stored as their veval -- one     *)
(* cell per orbit (tbuf); the step reads the stored prefix through       *)
(* cread, which canonicalizes the tuple BY DEFINITION and is zero off    *)
(* the prefix; the trajectory traj is DEFINED as the Arr over            *)
(* mkIx 1 0 N :: s.  The dense side: dbuf, slices on all tuples, reads   *)
(* not canonicalized.  The content is that they agree, under orbit       *)
(* invariance of the step (dense_agrees_with_quotient), and that the     *)
(* rank-1 leading record is never permuted (block_perm_lead).            *)
(* ===================================================================== *)

Lemma my_map_flat_map : forall (A B C : Type) (f : B -> C) (g : A -> list B) (l : list A),
  map f (flat_map g l) = flat_map (fun x => map f (g x)) l.
Proof.
  intros A B C f g l. induction l as [|a l IH]; simpl; [reflexivity |].
  rewrite map_app, IH. reflexivity.
Qed.

Lemma enum_rank1 : forall l u, enum 1 l u = map (fun i => [i]) (seq l (u - l)).
Proof.
  intros l u. simpl. generalize (seq l (u - l)). intro xs.
  induction xs as [|x xs IH]; simpl; [reflexivity | rewrite IH; reflexivity].
Qed.

Lemma enumShape_lead : forall N s,
  enumShape (mkIx 1 0 N :: s) = flat_map (fun i => map (cons i) (enumShape s)) (seq 0 N).
Proof.
  intros N s. simpl enumShape. unfold enumIx. simpl ix_r. simpl ix_l. simpl ix_u.
  rewrite enum_rank1, Nat.sub_0_r. generalize (seq 0 N). intro xs.
  induction xs as [|x xs IH]; simpl; [reflexivity | rewrite IH; reflexivity].
Qed.

(* a rank-1 leading record: canonical form never moves the ordinal *)
Lemma scanon_lead : forall l u s n t,
  scanon (mkIx 1 l u :: s) (n :: t) = n :: scanon s t.
Proof. reflexivity. Qed.

Lemma block_perm_lead : forall l u s n n' t t',
  block_perm (mkIx 1 l u :: s) (n :: t) (n' :: t') <-> n = n' /\ block_perm s t t'.
Proof.
  intros l u s n n' t t'. simpl. split.
  - intros [HP Hb]. split; [| exact Hb].
    apply Permutation_length_1 in HP. exact HP.
  - intros [-> Hb]. split; [apply Permutation_refl | exact Hb].
Qed.

Lemma shape_dense_lead : forall N s n t,
  shape_dense (mkIx 1 0 N :: s) (n :: t) <-> n < N /\ shape_dense s t.
Proof.
  intros N s n t. simpl. unfold dense_tuple. split.
  - intros [[_ HF] Hd]. inversion HF as [|x l Hx Hrest]. subst. split; [lia | exact Hd].
  - intros [Hn Hd]. split; [split; [reflexivity | constructor; [lia | constructor]] | exact Hd].
Qed.

Lemma groupCard_lead : forall N, groupCard (mkIx 1 0 N) = N.
Proof. intro N. unfold groupCard. cbn [ix_r ix_l ix_u]. rewrite mscard1. lia. Qed.

Section Shaped.
  Variable s : Shape.

  (* the dense read of a prefix buffer of stored slices: a bounds test  *)
  (* on the ordinal, then the canonicalizing storage access             *)
  Definition cread (b : list (list nat)) (n : nat) (k : Z) (t : list nat) : nat :=
    if ((0 <=? k)%Z && (k <? Z.of_nat n)%Z)%bool
    then nth (index_of (scanon s t) (enumShape s)) (nth (Z.to_nat k) b []) 0
    else 0.

  (* the cell step: ordinal, canonical tuple of the slice, dense prefix read *)
  Definition cell_step : Type := nat -> list nat -> (Z -> list nat -> nat) -> nat.

  (* the one buffer: slice m is the step at every transversal tuple *)
  Fixpoint tbuf (cs : cell_step) (n : nat) : list (list nat) :=
    match n with
    | O => []
    | S m => let b := tbuf cs m in b ++ [map (fun c => cs m c (cread b m)) (enumShape s)]
    end.

  Definition tprefix (cs : cell_step) (n : nat) : Z -> list nat -> nat := cread (tbuf cs n) n.

  (* slice n as the one object's array over s *)
  Definition slice_arr (cs : cell_step) (n : nat) : Arr nat :=
    (s, fun c => cs n c (tprefix cs n)).

  (* the trajectory: the array over  mkIx 1 0 N :: s  *)
  Definition traj (cs : cell_step) (N : nat) : Arr nat :=
    (mkIx 1 0 N :: s,
     fun t => match t with [] => 0 | n :: t' => snd (slice_arr cs n) t' end).

  Lemma tbuf_length : forall cs n, length (tbuf cs n) = n.
  Proof.
    intros cs n. induction n as [|n IH]; simpl; [reflexivity |].
    rewrite app_length, IH. simpl. lia.
  Qed.

  Lemma tbuf_extends : forall cs n m, n <= m -> exists rest, tbuf cs m = tbuf cs n ++ rest.
  Proof.
    intros cs n m Hle. induction Hle as [|m Hle IH].
    - exists []. rewrite app_nil_r. reflexivity.
    - destruct IH as (rest & E). simpl. rewrite E.
      eexists. rewrite <- app_assoc. reflexivity.
  Qed.

  (* the stored slice k is the slice array's materialization *)
  Lemma tbuf_nth : forall cs N k, k < N -> nth k (tbuf cs N) [] = veval (slice_arr cs k).
  Proof.
    intros cs N k Hk. destruct (tbuf_extends cs (S k) N Hk) as (rest & E). rewrite E.
    rewrite app_nth1 by (rewrite tbuf_length; lia).
    simpl tbuf. rewrite app_nth2 by (rewrite tbuf_length; lia).
    rewrite tbuf_length, Nat.sub_diag. reflexivity.
  Qed.

  (* the step reads the prefix only through qread *)
  Theorem tprefix_is_qread : forall cs n k t, shape_dense s t ->
    (0 <= k < Z.of_nat n)%Z ->
    tprefix cs n k t = qread (slice_arr cs (Z.to_nat k)) t.
  Proof.
    intros cs n k t Hd [H0 H1]. unfold tprefix, cread.
    replace ((0 <=? k)%Z && (k <? Z.of_nat n)%Z)%bool with true
      by (symmetry; apply andb_true_iff; split; [apply Z.leb_le | apply Z.ltb_lt]; lia).
    rewrite (tbuf_nth cs n (Z.to_nat k)) by lia.
    apply (qread_is_storage nat (slice_arr cs (Z.to_nat k)) 0 t). exact Hd.
  Qed.

  Lemma tprefix_outside : forall cs n k t, ~ (0 <= k < Z.of_nat n)%Z -> tprefix cs n k t = 0.
  Proof.
    intros cs n k t Hk. unfold tprefix, cread.
    destruct ((0 <=? k)%Z && (k <? Z.of_nat n)%Z)%bool eqn:E; [| reflexivity].
    exfalso. apply Hk. apply andb_true_iff in E as [E0 E1].
    apply Z.leb_le in E0. apply Z.ltb_lt in E1. lia.
  Qed.

  Lemma traj_veval : forall cs N, veval (traj cs N) = concat (tbuf cs N).
  Proof.
    intros cs N. unfold veval. simpl fst. rewrite enumShape_lead.
    rewrite my_map_flat_map.
    transitivity (flat_map (fun i => veval (slice_arr cs i)) (seq 0 N)).
    { apply flat_map_ext. intro i. rewrite map_map. reflexivity. }
    induction N as [|N IH]; [reflexivity |].
    rewrite seq_S, flat_map_app, IH. cbn [flat_map]. rewrite app_nil_r.
    simpl tbuf. rewrite concat_app. cbn [concat].
    rewrite app_nil_r. reflexivity.
  Qed.

  (* the recursion axis is a rank-1 record: N cells, times the orbit    *)
  (* count of the rest                                                  *)
  Theorem traj_storage : forall cs N,
    length (veval (traj cs N)) = N * shapeCard s.
  Proof.
    intros cs N. rewrite veval_one_per_orbit. simpl fst.
    rewrite shapeCard_cons, groupCard_lead. reflexivity.
  Qed.

  (* --- the DENSE recurrence ------------------------------------------- *)
  (* What the program means: slices are functions on ALL tuples, the     *)
  (* prefix is read without canonicalizing.  tbuf / traj above is what   *)
  (* is stored: one cell per orbit, read through qread.  The composition *)
  (* law says the two agree at every dense cell -- under the group       *)
  (* side's law class.                                                   *)

  (* the step sees the cell only through its canonical form (a kernel    *)
  (* invariant under the Young action on the remaining axes), for EVERY  *)
  (* reader -- stronger than needed; orb_inv_sym below is the premise a   *)
  (* prior-slice recurrence like sym_step actually meets                 *)
  Definition orb_inv (cs : cell_step) : Prop :=
    forall n t t' rd, scanon s t = scanon s t' -> cs n t rd = cs n t' rd.

  (* the step reads the prefix only at dense tuples *)
  Definition rd_dense_ext (cs : cell_step) : Prop :=
    forall n t rd rd', (forall k u, shape_dense s u -> rd k u = rd' k u) ->
      cs n t rd = cs n t rd'.

  Definition dread (b : list (list nat -> nat)) (n : nat) (k : Z) (t : list nat) : nat :=
    if ((0 <=? k)%Z && (k <? Z.of_nat n)%Z)%bool
    then nth (Z.to_nat k) b (fun _ => 0) t else 0.

  Fixpoint dbuf (cs : cell_step) (n : nat) : list (list nat -> nat) :=
    match n with
    | O => []
    | S m => let b := dbuf cs m in b ++ [fun t => cs m t (dread b m)]
    end.

  Lemma dbuf_length : forall cs n, length (dbuf cs n) = n.
  Proof.
    intros cs n. induction n as [|n IH]; simpl; [reflexivity |].
    rewrite app_length, IH. simpl. lia.
  Qed.

  Lemma dbuf_extends : forall cs n m, n <= m -> exists rest, dbuf cs m = dbuf cs n ++ rest.
  Proof.
    intros cs n m Hle. induction Hle as [|m Hle IH].
    - exists []. rewrite app_nil_r. reflexivity.
    - destruct IH as (rest & E). simpl. rewrite E.
      eexists. rewrite <- app_assoc. reflexivity.
  Qed.

  Lemma dbuf_nth : forall cs N k, k < N ->
    nth k (dbuf cs N) (fun _ => 0) = (fun t => cs k t (dread (dbuf cs k) k)).
  Proof.
    intros cs N k Hk. destruct (dbuf_extends cs (S k) N Hk) as (rest & E). rewrite E.
    rewrite app_nth1 by (rewrite dbuf_length; lia).
    simpl dbuf. rewrite app_nth2 by (rewrite dbuf_length; lia).
    rewrite dbuf_length, Nat.sub_diag. reflexivity.
  Qed.

  Lemma scanon_idem : forall t, shape_dense s t -> scanon s (scanon s t) = scanon s t.
  Proof. intros t Hd. apply scanon_fixes. apply scanon_in_shape. exact Hd. Qed.

  (* the dense step at a dense cell is the stored step at the canonical *)
  (* tuple: induction along the leading axis, orbit invariance within   *)
  (* each slice                                                          *)
  Lemma dense_is_canonical : forall cs, orb_inv cs -> rd_dense_ext cs ->
    forall n t, shape_dense s t ->
      cs n t (dread (dbuf cs n) n) = cs n (scanon s t) (tprefix cs n).
  Proof.
    intros cs Hinv Hext n. induction n as [n IH] using lt_wf_ind. intros t Hd.
    rewrite (Hinv n t (scanon s t) _ (eq_sym (scanon_idem t Hd))).
    apply Hext. intros k u Hu. unfold dread.
    destruct ((0 <=? k)%Z && (k <? Z.of_nat n)%Z)%bool eqn:Ek.
    - apply andb_true_iff in Ek as [E0 E1]. apply Z.leb_le in E0. apply Z.ltb_lt in E1.
      rewrite (tprefix_is_qread cs n k u Hu (conj E0 E1)).
      rewrite (dbuf_nth cs n (Z.to_nat k)) by lia. cbv beta.
      rewrite (IH (Z.to_nat k) ltac:(lia) u Hu). reflexivity.
    - unfold tprefix, cread. rewrite Ek. reflexivity.
  Qed.

  (* THE AGREEMENT: the dense recurrence, read at a dense cell, is the  *)
  (* quotient trajectory's canonicalizing read                          *)
  Theorem dense_agrees_with_quotient : forall cs N, orb_inv cs -> rd_dense_ext cs ->
    forall n t, n < N -> shape_dense s t ->
      nth n (dbuf cs N) (fun _ => 0) t = qread (traj cs N) (n :: t).
  Proof.
    intros cs N Hinv Hext n t Hn Hd. rewrite (dbuf_nth cs N n Hn). cbv beta.
    rewrite (dense_is_canonical cs Hinv Hext n t Hd). reflexivity.
  Qed.

  (* and under orbit invariance alone the canonicalizing read of the    *)
  (* trajectory is its plain read                                       *)
  Corollary qread_traj_is_plain_read : forall cs N, orb_inv cs ->
    forall n t, shape_dense s t -> qread (traj cs N) (n :: t) = snd (traj cs N) (n :: t).
  Proof.
    intros cs N Hinv n t Hd.
    change (cs n (scanon s t) (tprefix cs n) = cs n t (tprefix cs n)).
    apply Hinv. apply scanon_idem. exact Hd.
  Qed.

  (* THE COMPOSITION LAW.  traj is DEFINED with the recursion axis       *)
  (* leading.  The theorem: for an orbit-invariant step reading its     *)
  (* prefix at dense tuples, the quotient -- N * shapeCard s stored     *)
  (* cells read through qread -- agrees at every dense cell with the    *)
  (* dense recurrence; the canonicalizing read is the plain read; every *)
  (* read is a storage lookup; the rank-1 leading record is never       *)
  (* permuted; and each dense slice carries the symmetry of the         *)
  (* remaining axes.                                                     *)
  Theorem letrec_commutes_with_quotient : forall cs N,
    orb_inv cs -> rd_dense_ext cs ->
    length (veval (traj cs N)) = N * shapeCard s
    /\ veval (traj cs N) = concat (tbuf cs N)
    /\ (forall n t, n < N -> shape_dense s t ->
          nth n (dbuf cs N) (fun _ => 0) t = qread (traj cs N) (n :: t)
          /\ qread (traj cs N) (n :: t) = snd (traj cs N) (n :: t)
          /\ nth (index_of (n :: scanon s t) (enumShape (fst (traj cs N))))
                 (veval (traj cs N)) 0 = qread (traj cs N) (n :: t))
    /\ (forall n n' t t', block_perm (fst (traj cs N)) (n :: t) (n' :: t') -> n = n')
    /\ (forall n t t', n < N -> shape_dense s t -> shape_dense s t' -> block_perm s t t' ->
          nth n (dbuf cs N) (fun _ => 0) t = nth n (dbuf cs N) (fun _ => 0) t').
  Proof.
    intros cs N Hinv Hext. split; [apply traj_storage |]. split; [apply traj_veval |].
    split; [| split].
    - intros n t Hn Hd. split; [exact (dense_agrees_with_quotient cs N Hinv Hext n t Hn Hd) |].
      split; [exact (qread_traj_is_plain_read cs N Hinv n t Hd) |].
      rewrite <- (scanon_lead 0 N s n t).
      apply (qread_is_storage nat (traj cs N) 0 (n :: t)).
      apply shape_dense_lead. split; assumption.
    - intros n n' t t' Hb. apply block_perm_lead in Hb. apply Hb.
    - intros n t t' Hn Hd Hd' Hb.
      rewrite (dense_agrees_with_quotient cs N Hinv Hext n t Hn Hd).
      rewrite (dense_agrees_with_quotient cs N Hinv Hext n t' Hn Hd').
      apply qread_invariant.
      + apply shape_dense_lead. split; assumption.
      + apply shape_dense_lead. split; assumption.
      + apply block_perm_lead. split; [reflexivity | exact Hb].
  Qed.

  (* --- THE WEAKER, CORRECT PREMISE (audit S03) ------------------------ *)
  (* orb_inv above asks for invariance under EVERY prefix reader, and     *)
  (* rd_dense_ext quantifies over EVERY cell tuple, malformed ones        *)
  (* included.  This file's own sym_step meets neither (pinned below:     *)
  (* sym_step_not_orb_inv, sym_step_not_rd_dense_ext): reading the        *)
  (* previous slice at the reversed tuple is invariant only when the      *)
  (* reader itself is symmetric.  The premise below asks exactly that:    *)
  (* invariance for ORBIT-INVARIANT readers (symmetric on valid dense     *)
  (* tuples), obligations at valid dense cell tuples only, and read-      *)
  (* extensionality between orbit-invariant readers that agree on dense   *)
  (* tuples.  The induction then proves that every generated dense prefix *)
  (* IS such a reader (dense_prefix_is_orbit_invariant), so the premise   *)
  (* is met where the step is actually applied.                           *)

  Definition inv_reader (rd : Z -> list nat -> nat) : Prop :=
    forall k u u', shape_dense s u -> shape_dense s u' -> block_perm s u u' ->
      rd k u = rd k u'.

  Definition dense_agree (rd rd' : Z -> list nat -> nat) : Prop :=
    forall k u, shape_dense s u -> rd k u = rd' k u.

  Definition orb_inv_sym (cs : cell_step) : Prop :=
    forall n t t' rd, inv_reader rd -> shape_dense s t -> shape_dense s t' ->
      block_perm s t t' -> cs n t rd = cs n t' rd.

  Definition rd_dense_ext_sym (cs : cell_step) : Prop :=
    forall n t rd rd', shape_dense s t -> inv_reader rd -> inv_reader rd' ->
      dense_agree rd rd' -> cs n t rd = cs n t rd'.

  (* the new premises are WEAKER: the old ones imply them *)
  Lemma orb_inv_gives_sym : forall cs, orb_inv cs -> orb_inv_sym cs.
  Proof.
    intros cs H n t t' rd _ _ _ Hb. apply H. apply block_perm_scanon_eq. exact Hb.
  Qed.

  Lemma rd_dense_ext_gives_sym : forall cs, rd_dense_ext cs -> rd_dense_ext_sym cs.
  Proof. intros cs H n t rd rd' _ _ _ Hag. apply H. exact Hag. Qed.

  (* the stored (quotient) prefix is orbit-invariant by construction:    *)
  (* cread sees the tuple only through its canonical form                *)
  Lemma tprefix_inv_reader : forall cs n, inv_reader (tprefix cs n).
  Proof.
    intros cs n k u u' _ _ Hb. unfold tprefix, cread.
    rewrite (block_perm_scanon_eq s u u' Hb). reflexivity.
  Qed.

  Lemma inv_reader_of_agree : forall rd rd', inv_reader rd' -> dense_agree rd rd' ->
    inv_reader rd.
  Proof.
    intros rd rd' Hinv Hag k u u' Hu Hu' Hb.
    rewrite (Hag k u Hu), (Hag k u' Hu'). apply Hinv; assumption.
  Qed.

  (* if every earlier dense slice is the stored slice's canonical read,  *)
  (* the dense prefix agrees with the stored prefix on dense tuples      *)
  Lemma prefix_agree_from : forall cs n,
    (forall m t, m < n -> shape_dense s t ->
       cs m t (dread (dbuf cs m) m) = cs m (scanon s t) (tprefix cs m)) ->
    dense_agree (dread (dbuf cs n) n) (tprefix cs n).
  Proof.
    intros cs n IH k u Hu. unfold dread.
    destruct ((0 <=? k)%Z && (k <? Z.of_nat n)%Z)%bool eqn:Ek.
    - apply andb_true_iff in Ek as [E0 E1]. apply Z.leb_le in E0. apply Z.ltb_lt in E1.
      rewrite (tprefix_is_qread cs n k u Hu (conj E0 E1)).
      rewrite (dbuf_nth cs n (Z.to_nat k)) by lia. cbv beta.
      rewrite (IH (Z.to_nat k) u ltac:(lia) Hu). reflexivity.
    - unfold tprefix, cread. rewrite Ek. reflexivity.
  Qed.

  Lemma dense_is_canonical_sym : forall cs, orb_inv_sym cs -> rd_dense_ext_sym cs ->
    forall n t, shape_dense s t ->
      cs n t (dread (dbuf cs n) n) = cs n (scanon s t) (tprefix cs n).
  Proof.
    intros cs Hinv Hext n. induction n as [n IH] using lt_wf_ind. intros t Hd.
    assert (Hag : dense_agree (dread (dbuf cs n) n) (tprefix cs n))
      by (apply prefix_agree_from; intros m u Hm Hu; apply IH; assumption).
    pose proof (tprefix_inv_reader cs n) as Htp.
    rewrite (Hext n t _ _ Hd (inv_reader_of_agree _ _ Htp Hag) Htp Hag).
    apply Hinv; [exact Htp | exact Hd | |].
    - apply enumShape_shape_dense. apply scanon_in_shape. exact Hd.
    - apply scanon_block_perm. exact Hd.
  Qed.

  (* EACH GENERATED PREFIX SUPPLIES AN ORBIT-INVARIANT READER: the dense  *)
  (* prefix read the step is applied to is symmetric on dense tuples and  *)
  (* agrees there with the stored (quotient) prefix.                      *)
  Theorem dense_prefix_is_orbit_invariant : forall cs,
    orb_inv_sym cs -> rd_dense_ext_sym cs -> forall n,
      inv_reader (dread (dbuf cs n) n)
      /\ dense_agree (dread (dbuf cs n) n) (tprefix cs n).
  Proof.
    intros cs Hinv Hext n.
    assert (Hag : dense_agree (dread (dbuf cs n) n) (tprefix cs n)).
    { apply prefix_agree_from. intros m t _ Hd.
      apply dense_is_canonical_sym; assumption. }
    split; [| exact Hag].
    exact (inv_reader_of_agree _ _ (tprefix_inv_reader cs n) Hag).
  Qed.

  Theorem dense_agrees_with_quotient_sym : forall cs N,
    orb_inv_sym cs -> rd_dense_ext_sym cs ->
    forall n t, n < N -> shape_dense s t ->
      nth n (dbuf cs N) (fun _ => 0) t = qread (traj cs N) (n :: t).
  Proof.
    intros cs N Hinv Hext n t Hn Hd. rewrite (dbuf_nth cs N n Hn). cbv beta.
    rewrite (dense_is_canonical_sym cs Hinv Hext n t Hd). reflexivity.
  Qed.

  Corollary qread_traj_is_plain_read_sym : forall cs N, orb_inv_sym cs ->
    forall n t, shape_dense s t -> qread (traj cs N) (n :: t) = snd (traj cs N) (n :: t).
  Proof.
    intros cs N Hinv n t Hd.
    change (cs n (scanon s t) (tprefix cs n) = cs n t (tprefix cs n)).
    symmetry. apply Hinv; [apply tprefix_inv_reader | exact Hd | |].
    - apply enumShape_shape_dense. apply scanon_in_shape. exact Hd.
    - apply scanon_block_perm. exact Hd.
  Qed.

  (* THE COMPOSITION LAW under the weaker premise: the same five         *)
  (* conclusions as letrec_commutes_with_quotient.                       *)
  Theorem letrec_commutes_with_quotient_sym : forall cs N,
    orb_inv_sym cs -> rd_dense_ext_sym cs ->
    length (veval (traj cs N)) = N * shapeCard s
    /\ veval (traj cs N) = concat (tbuf cs N)
    /\ (forall n t, n < N -> shape_dense s t ->
          nth n (dbuf cs N) (fun _ => 0) t = qread (traj cs N) (n :: t)
          /\ qread (traj cs N) (n :: t) = snd (traj cs N) (n :: t)
          /\ nth (index_of (n :: scanon s t) (enumShape (fst (traj cs N))))
                 (veval (traj cs N)) 0 = qread (traj cs N) (n :: t))
    /\ (forall n n' t t', block_perm (fst (traj cs N)) (n :: t) (n' :: t') -> n = n')
    /\ (forall n t t', n < N -> shape_dense s t -> shape_dense s t' -> block_perm s t t' ->
          nth n (dbuf cs N) (fun _ => 0) t = nth n (dbuf cs N) (fun _ => 0) t').
  Proof.
    intros cs N Hinv Hext. split; [apply traj_storage |]. split; [apply traj_veval |].
    split; [| split].
    - intros n t Hn Hd.
      split; [exact (dense_agrees_with_quotient_sym cs N Hinv Hext n t Hn Hd) |].
      split; [exact (qread_traj_is_plain_read_sym cs N Hinv n t Hd) |].
      rewrite <- (scanon_lead 0 N s n t).
      apply (qread_is_storage nat (traj cs N) 0 (n :: t)).
      apply shape_dense_lead. split; assumption.
    - intros n n' t t' Hb. apply block_perm_lead in Hb. apply Hb.
    - intros n t t' Hn Hd Hd' Hb.
      rewrite (dense_agrees_with_quotient_sym cs N Hinv Hext n t Hn Hd).
      rewrite (dense_agrees_with_quotient_sym cs N Hinv Hext n t' Hn Hd').
      apply qread_invariant.
      + apply shape_dense_lead. split; assumption.
      + apply shape_dense_lead. split; assumption.
      + apply block_perm_lead. split; [reflexivity | exact Hb].
  Qed.
End Shaped.

(* pin: without orb_inv the canonicalizing read is NOT the plain read.  *)
(* bad_cs answers the first coordinate of the cell: at (0, 2, 0) the    *)
(* quotient reads the canonical (0, 0, 2) and answers 0, the plain read *)
(* answers 2.                                                            *)
Definition bad_cs : cell_step := fun n c rd => hd 0 c.

Example orb_inv_needed_pin :
  qread (traj [mkIx 2 0 3] bad_cs 1) [0; 2; 0] = 0
  /\ snd (traj [mkIx 2 0 3] bad_cs 1) [0; 2; 0] = 2.
Proof. split; vm_compute; reflexivity. Qed.

(* --- work with array slices: N * shapeCard s, forced and attained ----- *)
(* A question is now an ordinal, a cell tuple and the prefix's dense    *)
(* read; the sweep asks every transversal tuple of every slice, in      *)
(* order.  The law class is the orbit-invariant cell steps (the step    *)
(* sees the cell only through its canonical form); every (ordinal,      *)
(* orbit) pair is a perturbable class.                                  *)

Lemma my_NoDup_app : forall (A : Type) (a b : list A),
  NoDup a -> NoDup b -> (forall x, In x a -> ~ In x b) -> NoDup (a ++ b).
Proof.
  intros A a b Ha Hb Hd. induction a as [|x a IH]; simpl; [exact Hb |].
  inversion Ha as [|x' a' Hx Ha']. subst.
  constructor.
  - intro Hin. apply in_app_or in Hin as [H | H]; [exact (Hx H) |].
    exact (Hd x (or_introl eq_refl) H).
  - apply IH; [exact Ha' |]. intros y Hy. apply Hd. right. exact Hy.
Qed.

Lemma my_NoDup_list_prod : forall (A B : Type) (l : list A) (l' : list B),
  NoDup l -> NoDup l' -> NoDup (list_prod l l').
Proof.
  intros A B l l' Hl Hl'. induction l as [|x l IH]; simpl; [constructor |].
  inversion Hl as [|x' l0 Hx Hl0]. subst.
  apply my_NoDup_app.
  - clear -Hl'. induction l' as [|y l' IH]; simpl; [constructor |].
    inversion Hl' as [|y' l1 Hy Hl1]. subst. constructor.
    + intro Hin. apply in_map_iff in Hin as (z & E & Hz). inversion E. subst. exact (Hy Hz).
    + apply IH. exact Hl1.
  - apply IH. exact Hl0.
  - intros [a b] Ha Hb. apply in_map_iff in Ha as (z & E & _). inversion E. subst.
    apply in_prod_iff in Hb as [Hb _]. exact (Hx Hb).
Qed.

Section ShapedWork.
  Variable s : Shape.

  Definition cquery : Type := (nat * list nat * (Z -> list nat -> nat))%type.

  Definition kcell (cs : cell_step) : cquery -> nat :=
    fun q => cs (fst (fst q)) (snd (fst q)) (snd q).

  (* ask the step at every cell of one slice, then continue *)
  Fixpoint slice_ask (m : nat) (rd : Z -> list nat -> nat) (cells : list (list nat))
           (got : list nat) (cont : list nat -> prog cquery nat (list (list nat)))
           : prog cquery nat (list (list nat)) :=
    match cells with
    | [] => cont got
    | c :: cells' => Ask (m, c, rd) (fun a => slice_ask m rd cells' (got ++ [a]) cont)
    end.

  Lemma slice_ask_run : forall k m rd cells got cont,
    run (slice_ask m rd cells got cont) k
    = run (cont (got ++ map (fun c => k (m, c, rd)) cells)) k.
  Proof.
    intros k m rd cells. induction cells as [|c cells IH]; intros got cont; simpl.
    - rewrite app_nil_r. reflexivity.
    - rewrite IH, <- app_assoc. reflexivity.
  Qed.

  Lemma slice_ask_trace : forall k m rd cells got cont,
    trace (slice_ask m rd cells got cont) k
    = map (fun c => (m, c, rd)) cells
      ++ trace (cont (got ++ map (fun c => k (m, c, rd)) cells)) k.
  Proof.
    intros k m rd cells. induction cells as [|c cells IH]; intros got cont; simpl.
    - rewrite app_nil_r. reflexivity.
    - rewrite IH, <- app_assoc. reflexivity.
  Qed.

  Fixpoint tsweep (f n : nat) (b : list (list nat)) : prog cquery nat (list (list nat)) :=
    match f with
    | O => Ret b
    | S f' => slice_ask n (cread s b n) (enumShape s) []
                        (fun v => tsweep f' (S n) (b ++ [v]))
    end.

  Definition tsweep_prog (N : nat) : prog cquery nat (list (list nat)) := tsweep N 0 [].

  Lemma tsweep_run : forall cs f n,
    run (tsweep f n (tbuf s cs n)) (kcell cs) = tbuf s cs (n + f).
  Proof.
    intros cs f. induction f as [|f IH]; intro n; simpl tsweep.
    - rewrite Nat.add_0_r. reflexivity.
    - rewrite slice_ask_run. simpl app at 1. unfold kcell at 1. simpl.
      change (tbuf s cs n ++ [map (fun c => cs n c (cread s (tbuf s cs n) n)) (enumShape s)])
        with (tbuf s cs (S n)).
      rewrite IH. f_equal. lia.
  Qed.

  Lemma tsweep_trace : forall cs f n,
    map (fun q => (fst (fst q), snd (fst q))) (trace (tsweep f n (tbuf s cs n)) (kcell cs))
    = list_prod (seq n f) (enumShape s).
  Proof.
    intros cs f. induction f as [|f IH]; intro n; simpl tsweep; [reflexivity |].
    rewrite slice_ask_trace. simpl app at 2. unfold kcell at 2. simpl.
    change (tbuf s cs n ++ [map (fun c => cs n c (cread s (tbuf s cs n) n)) (enumShape s)])
      with (tbuf s cs (S n)).
    rewrite map_app, IH, map_map. simpl. reflexivity.
  Qed.

  (* the sweep computes the trajectory with N * shapeCard s questions,  *)
  (* one per (ordinal, orbit) pair, ordinal-major                       *)
  Theorem letrec_work_shape : forall cs N,
    run (tsweep_prog N) (kcell cs) = tbuf s cs N
    /\ map (fun q => (fst (fst q), snd (fst q))) (trace (tsweep_prog N) (kcell cs))
       = list_prod (seq 0 N) (enumShape s)
    /\ length (trace (tsweep_prog N) (kcell cs)) = N * shapeCard s.
  Proof.
    intros cs N. unfold tsweep_prog.
    pose proof (tsweep_run cs N 0) as Hr. pose proof (tsweep_trace cs N 0) as Ht.
    simpl tbuf in Hr, Ht. simpl in Hr.
    split; [exact Hr |]. split; [exact Ht |].
    pose proof (f_equal (@length _) Ht) as Hl. rewrite map_length in Hl.
    etransitivity; [exact Hl |].
    rewrite length_prod, seq_length, shapeCard_is_length. reflexivity.
  Qed.

  (* --- the lower bound --- *)

  Definition bumpc (n0 : nat) (c0 : list nat) (cs : cell_step) : cell_step :=
    fun n t rd => if Nat.eq_dec n n0 then
                    if list_eq_dec Nat.eq_dec (scanon s t) c0 then S (cs n t rd) else cs n t rd
                  else cs n t rd.

  Lemma tbuf_agree_below : forall cs cs' n,
    (forall m c rd, m < n -> cs m c rd = cs' m c rd) -> tbuf s cs n = tbuf s cs' n.
  Proof.
    intros cs cs' n. induction n as [|n IH]; intro H; simpl; [reflexivity |].
    rewrite IH by (intros; apply H; lia). f_equal. f_equal.
    apply map_ext. intro c. apply H. lia.
  Qed.

  Theorem letrec_work_shape_lower_bound :
    forall (Ob : Type) (out : Ob -> nat -> list nat -> nat)
           (P : prog cquery nat Ob) (N : nat),
    (forall cs, orb_inv s cs -> forall n c, n < N -> In c (enumShape s) ->
       out (run P (kcell cs)) n c = cs n c (tprefix s cs n)) ->
    forall cs, orb_inv s cs ->
      (forall n c, n < N -> In c (enumShape s) ->
         In (n, c) (map (fun q => (fst (fst q), scanon s (snd (fst q))))
                        (trace P (kcell cs))))
      /\ N * shapeCard s <= length (trace P (kcell cs)).
  Proof.
    intros Ob out P N Hcor cs Hinv.
    set (cls := fun q : cquery => (fst (fst q), scanon s (snd (fst q)))).
    assert (Hdec : forall x y : nat * list nat, {x = y} + {x <> y}).
    { intros x y. decide equality; [apply (list_eq_dec Nat.eq_dec) | apply Nat.eq_dec]. }
    assert (Hall : forall n c, n < N -> In c (enumShape s) ->
                     In (n, c) (map cls (trace P (kcell cs)))).
    { intros n c Hn Hc.
      destruct (in_dec Hdec (n, c) (map cls (trace P (kcell cs)))) as [Hi | Hni];
        [exact Hi | exfalso].
      assert (HK : orb_inv s (bumpc n c cs)).
      { intros m t t' rd E. unfold bumpc. rewrite E, (Hinv m t t' rd E). reflexivity. }
      assert (Hag : forall q, In q (trace P (kcell cs)) ->
                      kcell (bumpc n c cs) q = kcell cs q).
      { intros [[m t] rd] Hq. unfold kcell, bumpc. simpl.
        destruct (Nat.eq_dec m n) as [-> | Hm]; [| reflexivity].
        destruct (list_eq_dec Nat.eq_dec (scanon s t) c) as [Et | Ht]; [| reflexivity].
        exfalso. apply Hni. replace (n, c) with (cls (n, t, rd)) by (unfold cls; simpl; rewrite Et; reflexivity).
        apply in_map. exact Hq. }
      destruct (run_agree _ _ _ P (kcell cs) (kcell (bumpc n c cs)) Hag) as [Hrun _].
      pose proof (Hcor (bumpc n c cs) HK n c Hn Hc) as H1.
      pose proof (Hcor cs Hinv n c Hn Hc) as H2.
      rewrite Hrun, H2 in H1.
      unfold tprefix in H1.
      rewrite (tbuf_agree_below (bumpc n c cs) cs n) in H1.
      2:{ intros m c' rd Hm. unfold bumpc. destruct (Nat.eq_dec m n); [lia | reflexivity]. }
      unfold bumpc in H1.
      destruct (Nat.eq_dec n n) as [_ | Hnn]; [| apply Hnn; reflexivity].
      rewrite (scanon_fixes s c Hc) in H1.
      destruct (list_eq_dec Nat.eq_dec c c) as [_ | Hcc]; [| apply Hcc; reflexivity].
      unfold tprefix in H1. lia. }
    split; [exact Hall |].
    rewrite <- (map_length cls (trace P (kcell cs))).
    rewrite shapeCard_is_length, <- (seq_length N 0) at 1.
    rewrite <- length_prod.
    apply NoDup_incl_length.
    - apply my_NoDup_list_prod; [apply seq_NoDup | apply enumShape_NoDup].
    - intros [n c] Hin. apply in_prod_iff in Hin as [Hn Hc].
      apply in_seq in Hn. apply Hall; [lia | exact Hc].
  Qed.

  (* the sweep's output read: slice n of the buffer at the cell's       *)
  (* transversal position                                                *)
  Definition sweep_out (b : list (list nat)) (n : nat) (c : list nat) : nat :=
    nth (index_of c (enumShape s)) (nth n b []) 0.

  (* packaged like letrec_work_optimal: the sweep meets the correctness *)
  (* premise of letrec_work_shape_lower_bound (for every cell step, so  *)
  (* in particular every orbit-invariant one), every program meeting it *)
  (* asks at least N * shapeCard s questions, and the sweep asks        *)
  (* exactly that many: forced and attained                             *)
  Theorem letrec_work_shape_optimal : forall N,
    (forall cs n c, n < N -> In c (enumShape s) ->
       sweep_out (run (tsweep_prog N) (kcell cs)) n c = cs n c (tprefix s cs n))
    /\ (forall (Ob : Type) (out : Ob -> nat -> list nat -> nat) (P : prog cquery nat Ob),
          (forall cs, orb_inv s cs -> forall n c, n < N -> In c (enumShape s) ->
             out (run P (kcell cs)) n c = cs n c (tprefix s cs n)) ->
          forall cs, orb_inv s cs -> N * shapeCard s <= length (trace P (kcell cs)))
    /\ (forall cs, length (trace (tsweep_prog N) (kcell cs)) = N * shapeCard s).
  Proof.
    intro N. split; [| split].
    - intros cs n c Hn Hc. unfold sweep_out. rewrite (proj1 (letrec_work_shape cs N)).
      rewrite (tbuf_nth s cs N n Hn). unfold veval, slice_arr. simpl.
      apply nth_index_of. exact Hc.
    - intros Ob out P Hcor cs Hinv.
      exact (proj2 (letrec_work_shape_lower_bound Ob out P N Hcor cs Hinv)).
    - intro cs. apply letrec_work_shape.
  Qed.
End ShapedWork.

(* ===================================================================== *)
(* PART D.  THE while GUARD (formalism 7.5.1).                           *)
(* The guarded sweep carries a running flag: while the guard (reading    *)
(* the prefix under the same zero-history rule) holds it writes the      *)
(* step's slice; at the first false guard it stops, and every later      *)
(* slot gets the last written slice (prefix read at n - 1: zero when     *)
(* nothing was written).  The flag still up at the end of the budget is  *)
(* the BL8010 abort.                                                     *)
(* ===================================================================== *)

(* the first m < n with g m = false, else n *)
Fixpoint first_false (g : nat -> bool) (n : nat) : nat :=
  match n with
  | O => O
  | S m => let f := first_false g m in
           if f <? m then f else if g m then S m else m
  end.

Lemma first_false_le : forall g n, first_false g n <= n.
Proof.
  intros g n. induction n as [|n IH]; simpl; [lia |].
  destruct (first_false g n <? n) eqn:E; [lia |]. destruct (g n); lia.
Qed.

Lemma first_false_true : forall g n m, m < first_false g n -> g m = true.
Proof.
  intros g n. induction n as [|n IH]; intros m Hm; simpl in Hm; [lia |].
  destruct (first_false g n <? n) eqn:E; [apply IH; exact Hm |].
  apply Nat.ltb_ge in E. pose proof (first_false_le g n).
  destruct (g n) eqn:G.
  - destruct (Nat.eq_dec m n) as [-> | Hne]; [exact G | apply IH; lia].
  - apply IH. lia.
Qed.

Lemma first_false_false : forall g n, first_false g n < n -> g (first_false g n) = false.
Proof.
  intros g n. induction n as [|n IH]; intro H; simpl in *; [lia |].
  destruct (first_false g n <? n) eqn:E.
  - apply Nat.ltb_lt in E. apply IH. exact E.
  - apply Nat.ltb_ge in E. pose proof (first_false_le g n).
    destruct (g n) eqn:G; [lia | exact G].
Qed.

Lemma first_false_full : forall g n,
  first_false g n = n <-> (forall m, m < n -> g m = true).
Proof.
  intros g n. split.
  - intros E m Hm. apply (first_false_true g n). lia.
  - intro H. pose proof (first_false_le g n) as Hle.
    destruct (Nat.eq_dec (first_false g n) n) as [E | Hne]; [exact E | exfalso].
    pose proof (first_false_false g n ltac:(lia)) as F.
    rewrite (H (first_false g n) ltac:(lia)) in F. discriminate.
Qed.

Section Guard.
  Variable V : Type.
  Variable vzero : V.
  Variable step : nat -> (Z -> V) -> V.
  Variable guard : nat -> (Z -> V) -> bool.

  Fixpoint gbuf (n : nat) : list V * bool :=
    match n with
    | O => ([], true)
    | S m => match gbuf m with
             | (b, go) =>
                 if (go && guard m (zread V vzero b m))%bool
                 then (b ++ [step m (zread V vzero b m)], true)
                 else (b ++ [zread V vzero b m (Z.of_nat m - 1)%Z], false)
             end
    end.

  (* the guard as the unguarded build sees it, and its stopping ordinal *)
  Definition gp (m : nat) : bool := guard m (prefix_read V vzero step m).
  Definition gstop (N : nat) : nat := first_false gp N.
  Definition frz (s : nat) : V := prefix_read V vzero step s (Z.of_nat s - 1)%Z.

  Definition rec_build_guard (N n : nat) : V := nth n (fst (gbuf N)) vzero.
  Definition guard_aborts (N : nat) : bool := snd (gbuf N).        (* BL8010 *)
  Definition rec_guard_result (N : nat) : option (list V) :=
    match gbuf N with (b, go) => if go then None else Some b end.

  Lemma nth_repeat_tail : forall (b : list V) (x : V) k n,
    length b <= n -> n < length b + k -> nth n (b ++ repeat x k) vzero = x.
  Proof.
    intros b x k n H1 H2. rewrite app_nth2 by lia.
    apply nth_repeat_lt. lia.
  Qed.

  Lemma gstop_S : forall n, gstop (S n)
    = if gstop n <? n then gstop n else if gp n then S n else n.
  Proof. reflexivity. Qed.

  (* the guarded sweep, in closed form: the unguarded buffer up to the  *)
  (* stopping ordinal, then the frozen slice repeated to the extent     *)
  Theorem gbuf_spec : forall n,
    gbuf n = (lr_buf V vzero step (gstop n) ++ repeat (frz (gstop n)) (n - gstop n),
              gstop n =? n).
  Proof.
    induction n as [|n IH]; [reflexivity |].
    cbn [gbuf]. rewrite IH. rewrite gstop_S.
    pose proof (first_false_le gp n) as Hle. change (first_false gp n) with (gstop n) in Hle.
    destruct (gstop n <? n) eqn:E.
    - apply Nat.ltb_lt in E.
      assert (Hg : (gstop n =? n) = false) by (apply Nat.eqb_neq; lia).
      rewrite Hg. simpl andb. cbv iota.
      assert (Hz : zread V vzero (lr_buf V vzero step (gstop n)
                       ++ repeat (frz (gstop n)) (n - gstop n)) n (Z.of_nat n - 1)%Z
                   = frz (gstop n)).
      { unfold zread at 1.
        replace ((0 <=? Z.of_nat n - 1)%Z && (Z.of_nat n - 1 <? Z.of_nat n)%Z)%bool with true
          by (symmetry; apply andb_true_iff; split; [apply Z.leb_le | apply Z.ltb_lt]; lia).
        apply nth_repeat_tail; rewrite lr_buf_length; lia. }
      rewrite Hz. rewrite <- app_assoc.
      replace (S n - gstop n) with ((n - gstop n) + 1) by lia.
      rewrite repeat_app. simpl.
      assert (Hg' : (gstop n =? S n) = false) by (apply Nat.eqb_neq; lia).
      rewrite Hg'. reflexivity.
    - apply Nat.ltb_ge in E.
      assert (Hs : gstop n = n) by lia. rewrite Hs.
      rewrite Nat.sub_diag, Nat.eqb_refl. simpl repeat. rewrite app_nil_r. simpl andb.
      change (zread V vzero (lr_buf V vzero step n) n) with (prefix_read V vzero step n).
      change (guard n (prefix_read V vzero step n)) with (gp n).
      destruct (gp n) eqn:G.
      + rewrite Nat.sub_diag, Nat.eqb_refl. simpl repeat. rewrite app_nil_r. reflexivity.
      + change (match n with 0 => S n | S l => n - l end) with (S n - n).
        replace (S n - n) with 1 by lia.
        assert (Hn : (n =? S n) = false) by (apply Nat.eqb_neq; lia).
        rewrite Hn. reflexivity.
  Qed.

  Lemma gstop_le : forall N, gstop N <= N.
  Proof. intro N. apply first_false_le. Qed.

  (* total at the declared extent: every ordinal holds a slice *)
  Theorem letrec_guard_total : forall N, length (fst (gbuf N)) = N.
  Proof.
    intro N. rewrite gbuf_spec. simpl. rewrite app_length, lr_buf_length, repeat_length.
    pose proof (gstop_le N). lia.
  Qed.

  (* the guard can only stop EARLY: below the stopping ordinal the      *)
  (* guarded and unguarded builds coincide                              *)
  Theorem letrec_guard_prefix_agrees : forall N n, n < gstop N ->
    rec_build_guard N n = rec_build V vzero step N n.
  Proof.
    intros N n Hn. unfold rec_build_guard. rewrite gbuf_spec. simpl.
    rewrite app_nth1 by (rewrite lr_buf_length; exact Hn).
    apply (rec_build_stable V vzero step (gstop N) N n (gstop_le N) Hn).
  Qed.

  (* frozen after the stop: the slot holds the last written slice, the *)
  (* prefix read at stop - 1 (zero when the guard failed at ordinal 0)  *)
  Theorem letrec_guard_frozen : forall N n, gstop N <= n < N ->
    rec_build_guard N n = frz (gstop N)
    /\ rec_build_guard N n = rec_build_guard N (gstop N - 1)
    /\ (gstop N = 0 -> rec_build_guard N n = vzero).
  Proof.
    intros N n Hn.
    assert (Hfz : forall m, gstop N <= m < N -> rec_build_guard N m = frz (gstop N)).
    { intros m Hm. unfold rec_build_guard. rewrite gbuf_spec. simpl.
      apply nth_repeat_tail; rewrite lr_buf_length; lia. }
    split; [apply Hfz; exact Hn |].
    split.
    - rewrite (Hfz n Hn). destruct (gstop N) as [|s] eqn:Es.
      + simpl. symmetry. apply Hfz. lia.
      + replace (S s - 1) with s by lia.
        rewrite letrec_guard_prefix_agrees by lia.
        unfold frz. rewrite (prefix_read_at V vzero step (S s) N (Z.of_nat (S s) - 1)%Z)
          by (try (rewrite <- Es; apply gstop_le); lia).
        f_equal. lia.
    - intro E0. rewrite (Hfz n Hn), E0. unfold frz. apply zread_outside. simpl. lia.
  Qed.

  (* the budget: the sweep ends with its flag up -- BL8010 -- exactly   *)
  (* when the guard held at every ordinal of the budget                 *)
  Theorem letrec_guard_budget : forall N,
    (guard_aborts N = true <-> forall n, n < N -> guard n (prefix_read V vzero step n) = true)
    /\ (rec_guard_result N = None <-> guard_aborts N = true)
    /\ (guard_aborts N = false -> rec_guard_result N = Some (fst (gbuf N))).
  Proof.
    intro N. unfold guard_aborts, rec_guard_result.
    rewrite gbuf_spec. simpl. split; [| split].
    - rewrite Nat.eqb_eq. apply first_false_full.
    - destruct (gstop N =? N); split; intro H; congruence.
    - intro H. rewrite H. reflexivity.
  Qed.

  (* when it stops, it stops at a false guard with the guard true before *)
  Theorem letrec_guard_stops : forall N, gstop N < N ->
    guard (gstop N) (prefix_read V vzero step (gstop N)) = false
    /\ (forall n, n < gstop N -> guard n (prefix_read V vzero step n) = true)
    /\ guard_aborts N = false.
  Proof.
    intros N HN. split; [| split].
    - apply (first_false_false gp N HN).
    - intros n Hn. apply (first_false_true gp N n Hn).
    - unfold guard_aborts. rewrite gbuf_spec. simpl. apply Nat.eqb_neq. lia.
  Qed.
End Guard.

(* the seed arm under a guard: with a seed arm the compiler writes the  *)
(* seed before the loop and starts the guarded loop at ordinal 1, so    *)
(* the guard is never evaluated at 0.  In the model that is the guard   *)
(* that holds at 0.                                                      *)
Definition seeded_guard {V : Type} (g : nat -> (Z -> V) -> bool) : nat -> (Z -> V) -> bool :=
  fun n p => ((n =? 0) || g n p)%bool.

Theorem letrec_seeded_guard : forall (V : Type) (vzero seed : V)
    (step : nat -> (Z -> V) -> V) (g : nat -> (Z -> V) -> bool) N, 0 < N ->
  1 <= gstop V vzero (seeded V seed step) (seeded_guard g) N
  /\ rec_build_guard V vzero (seeded V seed step) (seeded_guard g) N 0 = seed.
Proof.
  intros V vzero seed step g N HN.
  assert (Hs : 1 <= gstop V vzero (seeded V seed step) (seeded_guard g) N).
  { destruct (gstop V vzero (seeded V seed step) (seeded_guard g) N) as [|k] eqn:E; [| lia].
    exfalso.
    pose proof (first_false_false (gp V vzero (seeded V seed step) (seeded_guard g)) N)
      as F.
    unfold gstop in E. rewrite E in F. specialize (F HN).
    unfold gp, seeded_guard in F. simpl in F. discriminate. }
  split; [exact Hs |].
  rewrite letrec_guard_prefix_agrees by lia.
  apply seeded_zero. exact HN.
Qed.

(* --- pin: a frozen guard and a budget abort --------------------------- *)
(* doubling from the seed 1 while the previous slice is below 10: the   *)
(* guard fails at ordinal 5 (it reads 16), the slice 16 freezes; on a   *)
(* budget of 4 the guard never fails and the sweep aborts (BL8010).      *)
Definition dbl_step : nat -> (Z -> nat) -> nat :=
  seeded nat 1 (fun n p => 2 * p (Z.of_nat n - 1)%Z).
Definition dbl_guard : nat -> (Z -> nat) -> bool :=
  fun n p => p (Z.of_nat n - 1)%Z <? 10.

Example guard_frozen_pin :
  fst (gbuf nat 0 dbl_step dbl_guard 8) = [1; 2; 4; 8; 16; 16; 16; 16]
  /\ gstop nat 0 dbl_step dbl_guard 8 = 5
  /\ rec_guard_result nat 0 dbl_step dbl_guard 8 = Some [1; 2; 4; 8; 16; 16; 16; 16]
  /\ rec_guard_result nat 0 dbl_step dbl_guard 4 = None.
Proof. repeat split; vm_compute; reflexivity. Qed.

(* the seed arm under a guard that is false everywhere: the guard is    *)
(* not consulted at 0, the seed is written, the stop is 1, and the seed *)
(* freezes                                                               *)
Example seeded_guard_pin :
  fst (gbuf nat 0 dbl_step (seeded_guard (fun _ _ => false)) 4) = [1; 1; 1; 1]
  /\ gstop nat 0 dbl_step (seeded_guard (fun _ _ => false)) 4 = 1.
Proof. split; vm_compute; reflexivity. Qed.

(* ===================================================================== *)
(* PART E.  THE CALCULUS HOOK.                                           *)
(* The object an ERec constructor of BladeSurface would denote: the      *)
(* rank-1 Arr nat over the leading axis [mkIx 1 0 N] reading the built   *)
(* family (scalar slices), and BladeInduction's traj over                *)
(* mkIx 1 0 N :: s (array slices).  The scalar recurrence is the s = []  *)
(* instance of the shaped one.                                           *)
(* ===================================================================== *)

Definition rec_arr (step : nat -> (Z -> nat) -> nat) (N : nat) : Arr nat :=
  ([mkIx 1 0 N], fun t => rec_build nat 0 step N (hd 0 t)).

Lemma my_map_nth_seq : forall (l : list nat) d,
  map (fun i => nth i l d) (seq 0 (length l)) = l.
Proof.
  intros l d. rewrite <- (rev_involutive l). induction (rev l) as [|x r IH]; [reflexivity |].
  simpl. rewrite app_length, Nat.add_1_r, seq_S, map_app, Nat.add_0_l. simpl.
  rewrite app_nth2 by lia. rewrite Nat.sub_diag. simpl. f_equal.
  rewrite <- IH at 2. apply map_ext_in. intros i Hi. apply in_seq in Hi.
  rewrite app_nth1 by lia. reflexivity.
Qed.

Theorem letrec_veval : forall step N,
  veval (rec_arr step N) = lr_buf nat 0 step N
  /\ veval (rec_arr step N) = map (rec_build nat 0 step N) (seq 0 N)
  /\ (forall n, n < N -> qread (rec_arr step N) [n] = step n (prefix_read nat 0 step n)).
Proof.
  intros step N.
  assert (E : veval (rec_arr step N) = map (rec_build nat 0 step N) (seq 0 N)).
  { unfold veval, rec_arr. simpl fst. rewrite enumShape_lead. simpl enumShape.
    generalize (seq 0 N). intro xs.
    induction xs as [|x xs IH]; [reflexivity |]. simpl flat_map. simpl map. simpl in IH. rewrite IH. reflexivity. }
  split; [| split; [exact E |]].
  - rewrite E. unfold rec_build. pose proof (my_map_nth_seq (lr_buf nat 0 step N) 0) as M.
    rewrite lr_buf_length in M. exact M.
  - intros n Hn. unfold qread, rec_arr. simpl.
    apply letrec_is_wf_fixpoint. exact Hn.
Qed.

(* the scalar recurrence as a cell step over the empty shape *)
Definition lift_step (step : nat -> (Z -> nat) -> nat) : cell_step :=
  fun n _ rd => step n (fun k => rd k []).

Theorem letrec_scalar_is_shape_nil : forall step, ext_step nat step -> forall N,
  tbuf [] (lift_step step) N = map (fun x => [x]) (lr_buf nat 0 step N)
  /\ veval (traj [] (lift_step step) N) = veval (rec_arr step N).
Proof.
  intros step Hext N.
  assert (Hb : forall N, tbuf [] (lift_step step) N = map (fun x => [x]) (lr_buf nat 0 step N)).
  { intro M. induction M as [|M IH]; [reflexivity |].
    simpl tbuf. simpl lr_buf. rewrite IH, map_app. simpl. f_equal. f_equal. f_equal.
    unfold lift_step. apply Hext. intro k. unfold cread, zread. simpl scanon.
    destruct ((0 <=? k)%Z && (k <? Z.of_nat M)%Z)%bool eqn:Ek; [| reflexivity].
    apply andb_true_iff in Ek as [E0 E1]. apply Z.leb_le in E0. apply Z.ltb_lt in E1.
    simpl index_of.
    assert (Hk : Z.to_nat k < length (lr_buf nat 0 step M))
      by (rewrite lr_buf_length; lia).
    rewrite (nth_indep _ [] [0]) by (rewrite map_length; exact Hk).
    rewrite (map_nth (fun x => [x]) _ 0). reflexivity. }
  split; [apply Hb |].
  rewrite traj_veval, Hb, (proj1 (letrec_veval step N)).
  generalize (lr_buf nat 0 step N). intro l.
  induction l as [|x l IH]; simpl; [reflexivity | rewrite IH; reflexivity].
Qed.

Example rec_arr_pin : veval (rec_arr fib_step 10) = [1; 1; 2; 3; 5; 8; 13; 21; 34; 55]
                      /\ qread (rec_arr fib_step 10) [9] = 55.
Proof. split; vm_compute; reflexivity. Qed.

(* pin: slices symmetric on the rest.  Shape [mkIx 2 0 3] has 6 orbits *)
(* (of 9 dense pairs); the step reads the previous slice at the         *)
(* REVERSED tuple, a non-canonical dense read, and the canonicalizing   *)
(* read answers it.  4 ordinals store 24 cells, and the trajectory's    *)
(* reads at (3, 2, 0) and (3, 0, 2) agree.                              *)
Definition sym_step : cell_step :=
  fun n c rd => match n with
                | O => lsum c
                | S m => rd (Z.of_nat m) (rev c) + 1
                end.

Example sym_traj_pin :
  length (veval (traj [mkIx 2 0 3] sym_step 4)) = 24
  /\ qread (traj [mkIx 2 0 3] sym_step 4) [3; 2; 0] = 5
  /\ qread (traj [mkIx 2 0 3] sym_step 4) [3; 0; 2] = 5.
Proof. repeat split; vm_compute; reflexivity. Qed.

(* the dense recurrence answers the same: slice 3 at (2, 0), read     *)
(* without canonicalizing anywhere                                     *)
Example sym_dense_pin :
  nth 3 (dbuf sym_step 4) (fun _ => 0) [2; 0] = 5
  /\ nth 3 (dbuf sym_step 4) (fun _ => 0) [0; 2] = 5.
Proof. split; vm_compute; reflexivity. Qed.

(* --- AUDIT S03 REGRESSION: sym_step under the old and new premises ---- *)
(* Negative: sym_step is NOT orb_inv -- at n = 1 the tuples (0,1), (1,0)  *)
(* share a canonical form, and the symmetry-breaking reader               *)
(* rd k c = hd 0 c makes the step answer 2 and 1 -- and NOT rd_dense_ext: *)
(* two readers agreeing on every valid two-coordinate tuple differ at the *)
(* malformed tuple [], and the step at [] observes it.  So the general    *)
(* agreement theorem under the old premises cannot be applied to it.     *)
Example sym_step_not_orb_inv : ~ orb_inv [mkIx 2 0 3] sym_step.
Proof.
  intro H.
  specialize (H 1 [0;1] [1;0] (fun _ c => hd 0 c)).
  vm_compute in H. specialize (H eq_refl). discriminate.
Qed.

Example sym_step_not_rd_dense_ext : ~ rd_dense_ext [mkIx 2 0 3] sym_step.
Proof.
  intro H.
  specialize (H 1 [] (fun _ _ => 0) (fun _ c => match c with [] => 1 | _ => 0 end)).
  assert (Heq : forall (k : Z) (u : list nat), shape_dense [mkIx 2 0 3] u ->
    0 = match u with [] => 1 | _ => 0 end).
  { intros k u Hu. destruct u;
      [unfold shape_dense, dense_tuple in Hu; simpl in Hu; intuition discriminate
      | reflexivity]. }
  specialize (H Heq). vm_compute in H. discriminate.
Qed.

(* Positive: over ANY single symmetric record [mkIx r l u], sym_step     *)
(* meets the weaker premises, so the general agreement holds for EVERY   *)
(* extent N, ordinal n and dense cell t -- not just the fixed pins.       *)

Lemma ind_lsum_perm : forall l1 l2 : list nat, Permutation l1 l2 -> lsum l1 = lsum l2.
Proof.
  intros l1 l2 HP. induction HP; simpl; lia.
Qed.

Lemma single_dense : forall r l u t,
  shape_dense [mkIx r l u] t <-> dense_tuple r l u t.
Proof.
  intros r l u t. simpl. unfold dense_tuple. split.
  - intros [[HL HF] Hs].
    assert (E : firstn r t = t).
    { apply firstn_all2. pose proof (firstn_skipn r t) as FS.
      rewrite Hs, app_nil_r in FS. rewrite <- FS, HL. lia. }
    rewrite E in HL, HF. split; assumption.
  - intros [HL HF]. rewrite <- HL, firstn_all, skipn_all.
    split; [split; [reflexivity | exact HF] | reflexivity].
Qed.

Lemma single_block_perm : forall r l u t t',
  dense_tuple r l u t -> dense_tuple r l u t' ->
  (block_perm [mkIx r l u] t t' <-> Permutation t t').
Proof.
  intros r l u t t' [HL _] [HL' _]. simpl.
  replace (firstn r t) with t by (rewrite <- HL; symmetry; apply firstn_all).
  replace (skipn r t) with (@nil nat) by (rewrite <- HL; symmetry; apply skipn_all).
  replace (firstn r t') with t' by (rewrite <- HL'; symmetry; apply firstn_all).
  replace (skipn r t') with (@nil nat) by (rewrite <- HL'; symmetry; apply skipn_all).
  split; [intros [HP _]; exact HP | intro HP; split; [exact HP | reflexivity]].
Qed.

Lemma dense_tuple_rev : forall r l u t, dense_tuple r l u t -> dense_tuple r l u (rev t).
Proof.
  intros r l u t [HL HF]. split; [rewrite rev_length; exact HL |].
  rewrite Forall_forall in HF |- *. intros x Hx. apply HF. apply in_rev. exact Hx.
Qed.

Lemma sym_step_orb_inv_sym : forall r l u, orb_inv_sym [mkIx r l u] sym_step.
Proof.
  intros r l u n t t' rd Hrd Hd Hd' Hb.
  apply single_dense in Hd. apply single_dense in Hd'.
  apply (single_block_perm r l u t t' Hd Hd') in Hb.
  destruct n as [|m]; simpl.
  - apply ind_lsum_perm. exact Hb.
  - f_equal. apply Hrd.
    + apply single_dense. apply dense_tuple_rev. exact Hd.
    + apply single_dense. apply dense_tuple_rev. exact Hd'.
    + apply single_block_perm; [apply dense_tuple_rev; assumption
                               | apply dense_tuple_rev; assumption |].
      apply Permutation_trans with t; [apply Permutation_sym, Permutation_rev |].
      apply Permutation_trans with t'; [exact Hb | apply Permutation_rev].
Qed.

Lemma sym_step_rd_dense_ext_sym : forall r l u, rd_dense_ext_sym [mkIx r l u] sym_step.
Proof.
  intros r l u n t rd rd' Hd _ _ Hag. destruct n as [|m]; simpl; [reflexivity |].
  f_equal. apply Hag. apply single_dense. apply dense_tuple_rev.
  apply single_dense. exact Hd.
Qed.

(* THE GENERAL AGREEMENT FOR sym_step: every record, extent, ordinal and *)
(* dense cell (sym_traj_pin and sym_dense_pin are instances); each dense *)
(* slice is symmetric; each generated dense prefix is orbit-invariant.   *)
Theorem sym_step_commutes_with_quotient : forall r l u N,
  length (veval (traj [mkIx r l u] sym_step N)) = N * shapeCard [mkIx r l u]
  /\ (forall n t, n < N -> shape_dense [mkIx r l u] t ->
        nth n (dbuf sym_step N) (fun _ => 0) t
        = qread (traj [mkIx r l u] sym_step N) (n :: t))
  /\ (forall n t t', n < N -> shape_dense [mkIx r l u] t -> shape_dense [mkIx r l u] t' ->
        Permutation t t' ->
        nth n (dbuf sym_step N) (fun _ => 0) t
        = nth n (dbuf sym_step N) (fun _ => 0) t')
  /\ (forall n, inv_reader [mkIx r l u] (dread (dbuf sym_step n) n)).
Proof.
  intros r l u N.
  pose proof (sym_step_orb_inv_sym r l u) as Hinv.
  pose proof (sym_step_rd_dense_ext_sym r l u) as Hext.
  destruct (letrec_commutes_with_quotient_sym [mkIx r l u] sym_step N Hinv Hext)
    as (Hlen & _ & Hag & _ & Hsym).
  split; [exact Hlen |]. split; [| split].
  - intros n t Hn Hd. apply (Hag n t Hn Hd).
  - intros n t t' Hn Hd Hd' HP. apply Hsym; try assumption.
    apply single_block_perm; [apply single_dense; exact Hd | apply single_dense; exact Hd' |].
    exact HP.
  - intro n. apply (dense_prefix_is_orbit_invariant [mkIx r l u] sym_step Hinv Hext n).
Qed.
