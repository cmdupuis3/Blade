(* ===================================================================== *)
(* BladeBoundary.v -- THE SCOPE BOUNDARY of the orbit bound.             *)
(* (docs/plans/plan-one-model-surface-calculus.md, item 7)               *)
(*                                                                       *)
(* BladeOptimal's orbit bound is a statement about KERNEL-GENERIC        *)
(* programs: programs owing the right answer for every kernel in a law   *)
(* class.  nongeneric_data_beats_bound already shows the bound fails on  *)
(* non-generic DATA.  This file states the kernel-side twin.  The QUERY  *)
(* bound (T1, kernel evaluations) holds on every law class FREE on the   *)
(* orbits, and fails for a structured class -- separable kernels         *)
(* f (a, b) = g a + g b, a subclass of the symmetric kernels whose       *)
(* orbits depend on one another.  The opaque-cell STORAGE bound (T1')    *)
(* still holds for that class: the witness saves kernel evaluations by   *)
(* moving work into its reader (halve two answers and add them), which   *)
(* the oracle model does not count.  The plan asked for an FFT witness;  *)
(* the separable kernel replaces it.  The FFT is an ANALOGY only, not    *)
(* mechanized, and it compares different things: the FFT saves           *)
(* arithmetic in a contraction against a KNOWN kernel, while the oracle  *)
(* model counts evaluations of an UNKNOWN kernel in a map.               *)
(*                                                                       *)
(* Part A -- a width-w generalization of the orbit bound:                *)
(*   support_lower_bound        each query TOUCHES at most w classes     *)
(*                              (its support); cells whose target        *)
(*                              classes are distinct and can be bumped   *)
(*                              alone (sperturbable) cost 1/w query      *)
(*                              each: |R| <= w * |trace|                 *)
(*   perturbable_is_sperturbable  orbit_lower_bound's perturbable is the *)
(*                              width-1 instance (support = [cls v])     *)
(*   orbit_bound_from_support   ... and orbit_lower_bound's conclusion   *)
(*                              follows from the support bound at w = 1  *)
(*                                                                       *)
(* Part B -- the separable class:                                        *)
(*   Ksep_Ksym                  every separable kernel is symmetric      *)
(*   sep_determined             f (i, j) is fixed by f (i, i), f (j, j): *)
(*                              2 f (i, j) = f (i, i) + f (j, j)         *)
(*   sep_prog_correct           asking the n diagonal tuples answers     *)
(*                              EVERY cell of the dense n x n square,    *)
(*                              for every separable kernel               *)
(*   sep_prog_trace             ... with exactly n questions             *)
(*   separable_beats_orbit_bound  n < C (n + 1, 2) for n >= 2            *)
(*   sep_prog_not_sym_correct   so the program is necessarily wrong on   *)
(*                              some symmetric kernel (it would          *)
(*                              otherwise violate sym_nest_lower_bound)  *)
(*   structured_kernel_beats_bound  THE WITNESS (query bound), packaged  *)
(*   sep_lower_bound            any program correct on the n diagonal    *)
(*                              cells for every separable kernel asks    *)
(*                              >= n / 2 questions of EVERY separable    *)
(*                              kernel (support bound, w = 2)            *)
(*   sep_lower_bound_attained_at_zero  and on nat values that is TIGHT   *)
(*                              at the zero kernel: an adaptive program  *)
(*                              correct on every cell asks ceil (n / 2)  *)
(*                              questions there                          *)
(*   shift_exists               fewer signed pair constraints than       *)
(*                              target values leave a -1/0/+1 shift that *)
(*                              keeps them all and moves a target        *)
(*                              (elimination; the tree-component         *)
(*                              argument on the query graph)             *)
(*   sep_positive_lower_bound   so every such program asks >= n          *)
(*                              questions of every separable kernel      *)
(*                              whose g is everywhere >= 1               *)
(*   sep_worst_case_lower_bound  in particular of the constant kernel 2  *)
(*   sep_query_complexity       the worst-case query count is EXACTLY n, *)
(*                              against C (n + 1, 2) orbits              *)
(*   sep_canonical_separate     separable kernels still tell the         *)
(*                              canonical cells apart (indicator g)      *)
(*   sep_storage_bound          so an opaque-cell store read by one      *)
(*                              cell per canonical cell needs            *)
(*                              C (n + 1, 2) cells: T1' is NOT beaten    *)
(*                                                                       *)
(* Part C -- the free class and the boundary.  Most of this part is      *)
(* orbit_lower_bound re-read, not new mathematics:                       *)
(*   free_on                    orbit_lower_bound's perturbability       *)
(*                              hypothesis, under a name                 *)
(*   Ksym_free                  one call to sym_perturbable              *)
(*   free_class_bound           orbit_lower_bound restated for any law   *)
(*                              class K (distinct classes from           *)
(*                              scls_args_inj); see the caveat below     *)
(*   indicator_closed,          an intrinsic freeness: K closed under    *)
(*   indicator_closed_free      adding the indicator of a class; it      *)
(*                              implies free_on on every list of cells   *)
(*   Ksym_indicator_closed      the symmetric class is closed            *)
(*   canonical_task_attained    asking every canonical cell answers the  *)
(*                              canonical cells for ANY kernel with      *)
(*                              C (n + r - 1, r) questions               *)
(* The new content is the negative side:                                 *)
(*   separable_nowhere_free     the separable class is free on NO tuple  *)
(*                              class at all                             *)
(*   separable_not_free,        so not on the canonical cells, and not   *)
(*   Ksep_not_indicator_closed  indicator-closed                         *)
(*   orbit_bound_boundary       the package: the bound holds, and is     *)
(*                              attained on the canonical-cell task, on  *)
(*                              every free class; one non-free subclass  *)
(*                              of Ksym 2 beats the query bound but not  *)
(*                              the storage bound; the converse is OPEN  *)
(*   genericity_two_sides       the data-side and kernel-side            *)
(*                              refutations, at one n (= 3), are the two *)
(*                              hypotheses of orbit_lower_bound failing: *)
(*                              distinct classes (data) and              *)
(*                              perturbability (kernel)                  *)
(*                                                                       *)
(* Part D -- pins: C (4, 2) = 6 versus 3 questions at n = 3, C (6, 2) =  *)
(* 15 versus 5 at n = 5 (and 3 on the zero kernel, adaptively).          *)
(*                                                                       *)
(* The two sides of the genericity hypothesis.  orbit_lower_bound has    *)
(* two hypotheses beyond correctness: the target cells lie in pairwise   *)
(* DISTINCT classes, and each class is PERTURBABLE inside the law class. *)
(* The first is a property of the data (a constant array puts every      *)
(* cell in one class: nongeneric_data_beats_bound, one query).  The      *)
(* second is a property of the law class (separable kernels cannot be    *)
(* moved on one orbit alone: structured_kernel_beats_bound, n queries    *)
(* where the orbits number C (n + 1, 2)).  Both are failures of FREENESS *)
(* -- of the data over the index space, of the kernel over the orbits.   *)
(*                                                                       *)
(* Scope, stated once.                                                   *)
(* (1) The boundary is one-directional: the bound holds on every free    *)
(* class, one non-free class is refuted, and the converse is OPEN.  The  *)
(* naive converse (non-free implies beatable) fails: an inequality law   *)
(* between two orbits (f c1 <= f c2) blocks perturbation at the zero     *)
(* kernel, yet a worst-case program still asks both orbits (an           *)
(* unmechanized remark, recorded so nobody states the converse).         *)
(* (2) Only the QUERY bound is beaten.  The witness's reader combines    *)
(* two answers; a reader that returns one stored cell per canonical      *)
(* cell (BladeOptimal's opaque-cell model) is held to C (n + 1, 2) for   *)
(* the separable class too (sep_storage_bound).                          *)
(* (3) free_class_bound's K need not be symmetric, but the count it      *)
(* proves is the S_r orbit count on the CANONICAL-CELL task.  For a K    *)
(* free on the S_r orbits whose own law is finer (a subgroup H < S_r, or *)
(* no law at all) that is not K's own tight count for the dense space;   *)
(* on the canonical-cell task, asking every canonical cell attains it    *)
(* for any K (canonical_task_attained).                                  *)
(* (4) The separable count: n in the worst case (proved both ways), at   *)
(* least n / 2 at every kernel, and ceil (n / 2) attained at the zero    *)
(* kernel.  That per-kernel gap is an artefact of the NAT domain, not of *)
(* separability: g >= 0 is itself an inequality law, which is what lets  *)
(* a zero sum pin both summands.  At every kernel whose g is at least 1  *)
(* the count is n (sep_positive_lower_bound); over Z the same            *)
(* elimination gives n at every kernel (not built: values are nat,       *)
(* decision D2).                                                         *)
(* (5) Only binary kernels (r = 2).  At general r the separable class    *)
(* (sums of r unary terms) has the same n-query program against          *)
(* C (n + r - 1, r) orbits, but its lower bound needs hyperedge          *)
(* elimination and is not built.                                         *)
(*                                                                       *)
(* Imports BladeDMWF, BladeBinomial, BladeOptimal.                       *)
(* Coq 8.18 / Rocq 9, stdlib only.                                       *)
(* ===================================================================== *)

From Blade Require Import BladeDMWF BladeBinomial BladeOptimal.
Require Import List Arith Lia Permutation ZArith.
Import ListNotations.

(* ===================================================================== *)
(* PART A.  THE SUPPORT BOUND.  A query v touches the classes supp v;    *)
(* a cell x targets the class tgt x.  The cell is s-perturbable when the *)
(* law class lets the kernel move at the cell while every query that     *)
(* does not touch the cell's class stays put.  Then distinct targets     *)
(* cost 1/w query each, w the largest support.  At w = 1 with            *)
(* supp v = [cls v] this is orbit_lower_bound.                           *)
(* ===================================================================== *)

Section SupportBound.
  Variables V Cl X A Res : Type.
  Variable supp : V -> list Cl.
  Hypothesis Cl_dec : forall c c' : Cl, {c = c'} + {c <> c'}.
  Variable K : (V -> A) -> Prop.
  Variable args : X -> V.
  Variable tgt : X -> Cl.
  Variable out : Res -> X -> A.

  Definition sperturbable (x : X) : Prop :=
    exists pert : (V -> A) -> (V -> A),
      (forall k, K k -> K (pert k)) /\
      (forall k v, ~ In (tgt x) (supp v) -> pert k v = k v) /\
      (forall k, pert k (args x) <> k (args x)).

  Lemma supp_flat_length : forall w (l : list V),
    (forall v, length (supp v) <= w) ->
    length (flat_map supp l) <= w * length l.
  Proof.
    intros w l Hw. induction l as [|v l IH]; simpl; [lia |].
    rewrite app_length. specialize (Hw v). lia.
  Qed.

  Theorem support_lower_bound : forall (w : nat) (R : list X) (P : prog V A Res),
    (forall v, length (supp v) <= w) ->
    NoDup (map tgt R) ->
    (forall x, In x R -> sperturbable x) ->
    (forall k, K k -> forall x, In x R -> out (run P k) x = k (args x)) ->
    forall k, K k -> length R <= w * length (trace P k).
  Proof.
    intros w R P Hw Hnd Hpert Hcor k Hk.
    eapply Nat.le_trans; [| apply (supp_flat_length w (trace P k) Hw)].
    rewrite <- (map_length tgt R).
    destruct (incl_or_witness Cl Cl_dec (map tgt R) (flat_map supp (trace P k)))
      as [Hincl | (c & Hc & Hn)].
    - apply NoDup_incl_length; assumption.
    - exfalso.
      apply in_map_iff in Hc as (x & Ex & Hx). subst c.
      destruct (Hpert x Hx) as (pert & HK & Hoff & Hon).
      assert (Hag : forall q, In q (trace P k) -> pert k q = k q).
      { intros q Hq. apply Hoff. intro Hin. apply Hn.
        apply in_flat_map. exists q. split; assumption. }
      destruct (run_agree _ _ _ P k (pert k) Hag) as [Hrun _].
      pose proof (Hcor (pert k) (HK k Hk) x Hx) as H1.
      pose proof (Hcor k Hk x Hx) as H2.
      rewrite Hrun, H2 in H1.
      apply (Hon k). symmetry. exact H1.
  Qed.
End SupportBound.

(* orbit_lower_bound's perturbability is the width-1 instance. *)
Lemma perturbable_is_sperturbable :
  forall (V Cl X A : Type) (cls : V -> Cl) (K : (V -> A) -> Prop)
         (args : X -> V) (x : X),
  perturbable V Cl X A cls K args x ->
  sperturbable V Cl X A (fun v => [cls v]) K args (fun y => cls (args y)) x.
Proof.
  intros V Cl X A cls K args x (pert & HK & Hoff & Hon).
  exists pert. split; [exact HK | split; [| exact Hon]].
  intros k v Hn. apply Hoff. intro E. apply Hn. left. exact E.
Qed.

(* ... and orbit_lower_bound's conclusion is the support bound at w = 1. *)
Corollary orbit_bound_from_support :
  forall (V Cl X A Res : Type) (cls : V -> Cl)
         (Cl_dec : forall c c' : Cl, {c = c'} + {c <> c'})
         (K : (V -> A) -> Prop) (args : X -> V) (out : Res -> X -> A)
         (R : list X) (P : prog V A Res),
  NoDup (map (fun x => cls (args x)) R) ->
  (forall x, In x R -> perturbable V Cl X A cls K args x) ->
  (forall k, K k -> forall x, In x R -> out (run P k) x = k (args x)) ->
  forall k, K k -> length R <= length (trace P k).
Proof.
  intros V Cl X A Res cls Cl_dec K args out R P Hnd Hp Hcor k Hk.
  rewrite <- (Nat.mul_1_l (length (trace P k))).
  apply (support_lower_bound V Cl X A Res (fun v => [cls v]) Cl_dec K args
           (fun x => cls (args x)) out 1 R P); try assumption.
  - intro v. simpl. lia.
  - intros x Hx. apply perturbable_is_sperturbable. apply Hp. exact Hx.
Qed.

(* ===================================================================== *)
(* PART B.  THE SEPARABLE CLASS.  Binary kernels on nat values that are  *)
(* a sum of one function of each argument.  Local by construction (only  *)
(* positions 0 and 1 are read) and symmetric; but the value on an orbit  *)
(* {a, b} is fixed by the values on {a, a} and {b, b}.                   *)
(* ===================================================================== *)

Definition Ksep (f : (nat -> nat) -> nat) : Prop :=
  exists g : nat -> nat, forall v, f v = g (v 0) + g (v 1).

Lemma scls2_eq : forall v v', scls 2 v = scls 2 v' ->
  (v 0 = v' 0 /\ v 1 = v' 1) \/ (v 0 = v' 1 /\ v 1 = v' 0).
Proof.
  intros v v' E. unfold scls in E.
  change (tab 2 v) with [v 0; v 1] in E.
  change (tab 2 v') with [v' 0; v' 1] in E.
  apply Permutation_length_2.
  apply perm_trans with (isort [v 0; v 1]); [apply isort_perm |].
  rewrite E. apply Permutation_sym. apply isort_perm.
Qed.

Lemma Ksep_Ksym : forall f, Ksep f -> Ksym 2 f.
Proof.
  intros f [g Hg]. apply Kcls_Ksym. intros v v' E.
  rewrite (Hg v), (Hg v').
  destruct (scls2_eq v v' E) as [[E0 E1] | [E0 E1]]; rewrite E0, E1; lia.
Qed.

Lemma Ksep_zero : Ksep (fun _ => 0).
Proof. exists (fun _ => 0). intro v. reflexivity. Qed.

Lemma half_double : forall a, (a + a) / 2 = a.
Proof.
  intro a. replace (a + a) with (a * 2) by lia. apply Nat.div_mul. lia.
Qed.

Lemma nth_map_seq : forall (h : nat -> nat) n i, i < n ->
  nth i (map h (seq 0 n)) 0 = h i.
Proof.
  intros h n i Hi.
  rewrite (nth_indep _ 0 (h 0)) by (rewrite map_length, seq_length; exact Hi).
  rewrite map_nth, seq_nth by exact Hi. reflexivity.
Qed.

Section Separable.
  Variable dat : nat -> nat.

  (* the dependency between orbits *)
  Lemma sep_determined : forall f, Ksep f -> forall i j,
    2 * f (argsOf dat [i; j]) = f (argsOf dat [i; i]) + f (argsOf dat [j; j]).
  Proof.
    intros f [g Hg] i j. rewrite !Hg. unfold argsOf. simpl. lia.
  Qed.

  (* the diagonal tuple at index x *)
  Definition dg (x : nat) : nat -> nat := argsOf dat [x; x].

  Definition sep_prog (n : nat) : prog (nat -> nat) nat (list nat) :=
    ask_all (map dg (seq 0 n)) [].

  (* answer (i, j) as f (i, i) / 2 + f (j, j) / 2 *)
  Definition sep_out (o : list nat) (ix : list nat) : nat :=
    nth (nth 0 ix 0) o 0 / 2 + nth (nth 1 ix 0) o 0 / 2.

  Lemma sep_read : forall f, Ksep f -> forall n ix,
    length ix = 2 -> (forall x, In x ix -> x < n) ->
    sep_out (map f (map dg (seq 0 n))) ix = f (argsOf dat ix).
  Proof.
    intros f [g Hg] n ix Hl Hb.
    destruct ix as [|i [|j [|]]]; simpl in Hl; try discriminate.
    assert (Hi : i < n) by (apply Hb; left; reflexivity).
    assert (Hj : j < n) by (apply Hb; right; left; reflexivity).
    unfold sep_out. rewrite map_map.
    change (nth 0 [i; j] 0) with i. change (nth 1 [i; j] 0) with j.
    rewrite (nth_map_seq (fun x => f (dg x)) n i Hi).
    rewrite (nth_map_seq (fun x => f (dg x)) n j Hj).
    rewrite !Hg. unfold dg, argsOf. cbn [nth].
    rewrite !half_double. reflexivity.
  Qed.

  Theorem sep_prog_correct : forall n f, Ksep f -> forall ix,
    length ix = 2 -> (forall x, In x ix -> x < n) ->
    sep_out (run (sep_prog n) f) ix = f (argsOf dat ix).
  Proof.
    intros n f Hf ix Hl Hb. unfold sep_prog.
    rewrite ask_all_run. cbn [rev app].
    apply sep_read; assumption.
  Qed.

  Theorem sep_prog_trace : forall n f, length (trace (sep_prog n) f) = n.
  Proof.
    intros n f. unfold sep_prog.
    rewrite ask_all_trace, map_length, seq_length. reflexivity.
  Qed.
End Separable.

Theorem separable_beats_orbit_bound : forall n, 2 <= n -> n < C (n + 2 - 1) 2.
Proof.
  intros n Hn. replace (n + 2 - 1) with (S n) by lia.
  change (C (S n) 2) with (C n 1 + C n 2). rewrite C_n_1.
  destruct n as [|[|m]]; [lia | lia |].
  change (C (S (S m)) 2) with (C (S m) 1 + C (S m) 2). rewrite C_n_1. lia.
Qed.

(* The structured program is necessarily WRONG on some symmetric kernel: *)
(* were it correct on Ksym 2, sym_nest_lower_bound would force          *)
(* C (n + 1, 2) questions out of it.                                     *)
Theorem sep_prog_not_sym_correct : forall dat,
  (forall i j, dat i = dat j -> i = j) ->
  forall n, 2 <= n -> forall out : list nat -> list nat -> nat,
  ~ (forall f, Ksym 2 f -> forall ix, canonical 2 0 n ix ->
       out (run (sep_prog dat n) f) ix = f (argsOf dat ix)).
Proof.
  intros dat Hinj n Hn out Hcor.
  assert (Hk : Ksym 2 (fun _ => 0)) by (apply Ksep_Ksym; exact Ksep_zero).
  pose proof (sym_nest_lower_bound 2 dat Hinj n (list nat) out (sep_prog dat n)
                Hcor (fun _ => 0) Hk) as H.
  rewrite sep_prog_trace in H.
  pose proof (separable_beats_orbit_bound n Hn). lia.
Qed.

(* ------------------------------------------------------------------ *)
(* THE WITNESS.  Separable kernels are symmetric kernels; a program     *)
(* correct on every cell of the dense square for every separable        *)
(* kernel asks n questions; every program correct for every symmetric   *)
(* kernel asks C (n + 1, 2); and n < C (n + 1, 2).                      *)
(* ------------------------------------------------------------------ *)
Theorem structured_kernel_beats_bound : forall dat,
  (forall i j, dat i = dat j -> i = j) ->
  forall n, 2 <= n ->
  exists (P : prog (nat -> nat) nat (list nat)) (out : list nat -> list nat -> nat),
    (forall f, Ksep f -> forall ix, length ix = 2 -> (forall x, In x ix -> x < n) ->
       out (run P f) ix = f (argsOf dat ix)) /\
    (forall f, length (trace P f) = n) /\
    n < C (n + 2 - 1) 2 /\
    (forall f, Ksep f -> Ksym 2 f) /\
    (forall (Res : Type) (out' : Res -> list nat -> nat) (Q : prog (nat -> nat) nat Res),
       (forall f, Ksym 2 f -> forall ix, canonical 2 0 n ix ->
          out' (run Q f) ix = f (argsOf dat ix)) ->
       forall f, Ksym 2 f -> C (n + 2 - 1) 2 <= length (trace Q f)).
Proof.
  intros dat Hinj n Hn.
  exists (sep_prog dat n), sep_out.
  split; [| split; [| split; [| split]]].
  - intros f Hf ix Hl Hb. apply sep_prog_correct; assumption.
  - intro f. apply sep_prog_trace.
  - apply separable_beats_orbit_bound. exact Hn.
  - exact Ksep_Ksym.
  - intros Res out' Q Hcor f Hf.
    exact (sym_nest_lower_bound 2 dat Hinj n Res out' Q Hcor f Hf).
Qed.

(* ------------------------------------------------------------------ *)
(* THE LOWER BOUND FOR Ksep: the support bound at width 2.  A query     *)
(* (a, b) touches the values a and b; bumping g at one value moves only *)
(* the tuples that mention it, so a value no query mentions is free.    *)
(* ------------------------------------------------------------------ *)
Theorem sep_lower_bound : forall dat,
  (forall i j, dat i = dat j -> i = j) ->
  forall (n : nat) (Res : Type) (out : Res -> nat -> nat)
         (P : prog (nat -> nat) nat Res),
  (forall f, Ksep f -> forall x, x < n -> out (run P f) x = f (dg dat x)) ->
  forall f, Ksep f -> n <= 2 * length (trace P f).
Proof.
  intros dat Hinj n Res out P Hcor f Hf.
  replace n with (length (seq 0 n)) at 1 by apply seq_length.
  apply (support_lower_bound (nat -> nat) nat nat nat Res
           (fun v => [v 0; v 1]) Nat.eq_dec Ksep (dg dat) dat out
           2 (seq 0 n) P).
  - intro v. simpl. lia.
  - apply NoDup_map_inj; [apply NoDup_seq |].
    intros x y _ _ E. apply Hinj. exact E.
  - intros x _.
    exists (fun k v => k v + (if v 0 =? dat x then 1 else 0)
                         + (if v 1 =? dat x then 1 else 0)).
    split; [| split].
    + intros k [g Hg].
      exists (fun a => g a + (if a =? dat x then 1 else 0)).
      intro v. rewrite Hg. lia.
    + intros k v Hn.
      assert (N0 : v 0 <> dat x) by (intro E; apply Hn; left; exact E).
      assert (N1 : v 1 <> dat x) by (intro E; apply Hn; right; left; exact E).
      apply Nat.eqb_neq in N0. apply Nat.eqb_neq in N1.
      rewrite N0, N1. lia.
    + intro k. unfold dg, argsOf. cbn [nth]. rewrite Nat.eqb_refl. lia.
  - intros k Hk x Hx. apply in_seq in Hx. apply Hcor; [exact Hk | lia].
  - exact Hf.
Qed.

(* ------------------------------------------------------------------ *)
(* n / 2 IS TIGHT POINTWISE.  An adaptive program: ask a pair cover of  *)
(* the n indices (ceil (n / 2) queries); if every answer is zero, every *)
(* g value at the data is zero (nat), so every cell is zero; otherwise  *)
(* fall back to the n diagonals.  Correct for every separable kernel on *)
(* every dense cell, and it asks ceil (n / 2) questions on the zero     *)
(* kernel.                                                              *)
(* ------------------------------------------------------------------ *)

Section AskThen.
  Variables V A Res : Type.

  Fixpoint ask_then (qs : list V) (got : list A)
                    (kont : list A -> prog V A Res) : prog V A Res :=
    match qs with
    | [] => kont (rev got)
    | q :: qs' => Ask q (fun a => ask_then qs' (a :: got) kont)
    end.

  Lemma ask_then_run : forall qs got kont k,
    run (ask_then qs got kont) k = run (kont (rev got ++ map k qs)) k.
  Proof.
    induction qs as [|q qs IH]; intros got kont k; simpl.
    - rewrite app_nil_r. reflexivity.
    - rewrite IH. simpl. rewrite <- app_assoc. reflexivity.
  Qed.

  Lemma ask_then_trace : forall qs got kont k,
    trace (ask_then qs got kont) k = qs ++ trace (kont (rev got ++ map k qs)) k.
  Proof.
    induction qs as [|q qs IH]; intros got kont k; simpl.
    - rewrite app_nil_r. reflexivity.
    - rewrite IH. simpl. rewrite <- app_assoc. reflexivity.
  Qed.
End AskThen.

Arguments ask_then {V A Res} _ _ _.

(* a pair cover of 0 .. n-1 with ceil (n / 2) pairs *)
Fixpoint cover (n : nat) : list (nat * nat) :=
  match n with
  | 0 => []
  | S 0 => [(0, 0)]
  | S (S m) => (m, S m) :: cover m
  end.

Lemma cover_spec : forall n,
  n <= 2 * length (cover n) <= S n /\
  (forall x, x < n -> exists a b, In (a, b) (cover n) /\ (x = a \/ x = b)).
Proof.
  induction n as [n IH] using lt_wf_ind.
  destruct n as [|[|m]].
  - simpl. split; [lia | intros x Hx; lia].
  - simpl. split; [lia |]. intros x Hx. exists 0, 0.
    split; [left; reflexivity | left; lia].
  - destruct (IH m ltac:(lia)) as [Hlen Hcov]. simpl cover. simpl length.
    split; [lia |]. intros x Hx.
    destruct (Nat.lt_ge_cases x m) as [Hl | Hg].
    + destruct (Hcov x Hl) as (a & b & Hin & E).
      exists a, b. split; [right; exact Hin | exact E].
    + exists m, (S m). split; [left; reflexivity | lia].
Qed.

Lemma list_sum_zero_in : forall l a, list_sum l = 0 -> In a l -> a = 0.
Proof.
  induction l as [|b l IH]; intros a Hs Ha; [destruct Ha |].
  simpl in Hs. destruct Ha as [E | Ha]; [lia |].
  apply IH; [lia | exact Ha].
Qed.

Lemma list_sum_map_zero : forall (B : Type) (l : list B),
  list_sum (map (fun _ => 0) l) = 0.
Proof. intros B l. induction l as [|b l IH]; simpl; [reflexivity | exact IH]. Qed.

Section ZeroFirst.
  Variable dat : nat -> nat.

  Definition pq (ab : nat * nat) : nat -> nat := argsOf dat [fst ab; snd ab].

  Definition zero_first_prog (n : nat) : prog (nat -> nat) nat (list nat) :=
    ask_then (map pq (cover n)) []
      (fun l => if list_sum l =? 0 then Ret []
                else ask_then (map (dg dat) (seq 0 n)) [] (fun l' => Ret l')).

  Lemma zero_cover_g : forall (f : (nat -> nat) -> nat) g,
    (forall v, f v = g (v 0) + g (v 1)) ->
    forall n x, x < n -> list_sum (map f (map pq (cover n))) = 0 ->
    g (dat x) = 0.
  Proof.
    intros f g Hg n x Hx Hs.
    destruct (proj2 (cover_spec n) x Hx) as (a & b & Hin & E).
    assert (H0 : f (pq (a, b)) = 0).
    { apply (list_sum_zero_in _ _ Hs). rewrite map_map.
      apply in_map with (f := fun ab => f (pq ab)). exact Hin. }
    rewrite Hg in H0. unfold pq, argsOf in H0. simpl in H0.
    destruct E as [E | E]; subst x; lia.
  Qed.

  Theorem zero_first_correct : forall n f, Ksep f -> forall ix,
    length ix = 2 -> (forall x, In x ix -> x < n) ->
    sep_out (run (zero_first_prog n) f) ix = f (argsOf dat ix).
  Proof.
    intros n f Hf ix Hl Hb. unfold zero_first_prog.
    rewrite ask_then_run. cbn [rev app].
    destruct (Nat.eqb_spec (list_sum (map f (map pq (cover n)))) 0) as [Z | NZ].
    - cbn [run]. destruct Hf as [g Hg].
      destruct ix as [|i [|j [|]]]; simpl in Hl; try discriminate.
      assert (Hi : i < n) by (apply Hb; left; reflexivity).
      assert (Hj : j < n) by (apply Hb; right; left; reflexivity).
      rewrite Hg. unfold argsOf. simpl nth.
      rewrite (zero_cover_g f g Hg n i Hi Z), (zero_cover_g f g Hg n j Hj Z).
      unfold sep_out. destruct (nth 0 [i; j] 0), (nth 1 [i; j] 0); reflexivity.
    - rewrite ask_then_run. cbn [rev app run].
      apply sep_read; assumption.
  Qed.

  Lemma zero_first_trace_zero : forall n,
    length (trace (zero_first_prog n) (fun _ => 0)) = length (cover n).
  Proof.
    intro n. unfold zero_first_prog.
    rewrite ask_then_trace. cbn [rev app].
    rewrite list_sum_map_zero. cbn [Nat.eqb trace].
    rewrite app_nil_r, map_length. reflexivity.
  Qed.
End ZeroFirst.

Theorem sep_lower_bound_attained_at_zero : forall dat n,
  exists P : prog (nat -> nat) nat (list nat),
    (forall f, Ksep f -> forall ix, length ix = 2 -> (forall x, In x ix -> x < n) ->
       sep_out (run P f) ix = f (argsOf dat ix)) /\
    n <= 2 * length (trace P (fun _ => 0)) <= S n.
Proof.
  intros dat n. exists (zero_first_prog dat n). split.
  - intros f Hf ix Hl Hb. apply zero_first_correct; assumption.
  - rewrite zero_first_trace_zero. apply cover_spec.
Qed.

(* ------------------------------------------------------------------ *)
(* THE WORST CASE IS n.  Against the constant kernel f = 2 (g = 1      *)
(* everywhere) a trace of fewer than n queries is beaten: the queried   *)
(* pairs are fewer than the n target values, and a -1 / 0 / +1 shift h  *)
(* of g that keeps every queried sum but moves some target always       *)
(* exists.  Proved by eliminating one constraint at a time: a signed    *)
(* constraint s h a + s' h b = 0 either kills a value (a = b, s = s'),  *)
(* is trivial (a = b, s <> s'), or substitutes h b := +-h a, and each   *)
(* step removes one constraint and at most one target.  (This is the    *)
(* tree-component argument on the query graph, done by elimination.)    *)
(* ------------------------------------------------------------------ *)

Definition lval (h : nat -> Z) (l : nat * bool) : Z :=
  if snd l then h (fst l) else (- h (fst l))%Z.

Definition csat (h : nat -> Z) (c : (nat * bool) * (nat * bool)) : Prop :=
  (lval h (fst c) + lval h (snd c) = 0)%Z.

(* substitute h b := (if flip then - h a else h a) *)
Definition lsub (b a : nat) (flip : bool) (l : nat * bool) : nat * bool :=
  if fst l =? b then (a, if flip then negb (snd l) else snd l) else l.

Definition csub (b a : nat) (flip : bool) (c : (nat * bool) * (nat * bool))
  : (nat * bool) * (nat * bool) :=
  (lsub b a flip (fst c), lsub b a flip (snd c)).

Definition hsub (h : nat -> Z) (b a : nat) (flip : bool) : nat -> Z :=
  fun z => if z =? b then (if flip then (- h a)%Z else h a) else h z.

Lemma lval_sub : forall h b a flip l,
  lval (hsub h b a flip) l = lval h (lsub b a flip l).
Proof.
  intros h b a flip [x s]. unfold lval, lsub, hsub. cbn [fst snd].
  destruct (Nat.eqb_spec x b) as [E | N].
  - cbn [fst snd]. destruct flip, s; cbn; lia.
  - reflexivity.
Qed.

(* kill a value: h a := 0 *)
Definition ckill (a : nat) (c : (nat * bool) * (nat * bool))
  : (nat * bool) * (nat * bool) :=
  if fst (fst c) =? a then
    (if fst (snd c) =? a then ((a, true), (a, false))
     else ((fst (snd c), true), (fst (snd c), true)))
  else if fst (snd c) =? a then ((fst (fst c), true), (fst (fst c), true))
  else c.

Definition hkill (h : nat -> Z) (a : nat) : nat -> Z :=
  fun z => if z =? a then 0%Z else h z.

Lemma csat_kill : forall h a c, csat h (ckill a c) -> csat (hkill h a) c.
Proof.
  intros h a [[x s] [y t]]. unfold csat, ckill, lval, hkill. cbn [fst snd].
  destruct (Nat.eqb_spec x a) as [Ex | Nx], (Nat.eqb_spec y a) as [Ey | Ny];
    cbn [fst snd]; intro H.
  - destruct s, t; lia.
  - destruct s, t; lia.
  - destruct s, t; lia.
  - exact H.
Qed.

Lemma remove_NoDup : forall (x : nat) l, NoDup l -> NoDup (remove Nat.eq_dec x l).
Proof.
  intros x l. induction l as [|y l IH]; intro H; simpl; [constructor |].
  inversion H as [| y' l' Hn Hnd]; subst.
  destruct (Nat.eq_dec x y) as [E | N]; [apply IH; exact Hnd |].
  constructor; [| apply IH; exact Hnd].
  intro Hin. apply in_remove in Hin as [Hin _]. contradiction.
Qed.

Lemma remove_length_NoDup : forall (x : nat) l, NoDup l ->
  length l <= S (length (remove Nat.eq_dec x l)).
Proof.
  intros x l. induction l as [|y l IH]; intro H; simpl; [lia |].
  inversion H as [| y' l' Hn Hnd]; subst.
  destruct (Nat.eq_dec x y) as [E | N].
  - subst y. rewrite notin_remove by exact Hn. lia.
  - simpl. specialize (IH Hnd). lia.
Qed.

Lemma shift_exists : forall m (E : list ((nat * bool) * (nat * bool))) (T : list nat),
  length E = m -> NoDup T -> m < length T ->
  exists h : nat -> Z,
    (forall z, (-1 <= h z <= 1)%Z) /\ Forall (csat h) E /\
    exists t, In t T /\ h t <> 0%Z.
Proof.
  induction m as [|m IH]; intros E T HE Hnd Hlt.
  - apply length_zero_iff_nil in HE. subst E.
    destruct T as [|t T]; simpl in Hlt; [lia |].
    exists (fun z => if z =? t then 1%Z else 0%Z).
    split; [intro z; destruct (z =? t); lia |].
    split; [constructor |].
    exists t. split; [left; reflexivity | rewrite Nat.eqb_refl; lia].
  - destruct E as [|e E]; [discriminate |]. simpl in HE. injection HE as HE.
    destruct e as [[a s] [b s']].
    destruct (Nat.eq_dec a b) as [Eab | Nab].
    + subst b. destruct (Bool.bool_dec s s') as [Es | Ns].
      * (* s h a + s h a = 0 forces h a = 0: kill a *)
        subst s'.
        pose proof (remove_length_NoDup a T Hnd) as HlT.
        destruct (IH (map (ckill a) E) (remove Nat.eq_dec a T))
          as (h & Hr & HF & t & Ht & Hh).
        { rewrite map_length. exact HE. }
        { apply remove_NoDup. exact Hnd. }
        { lia. }
        exists (hkill h a). split; [| split].
        -- intro z. unfold hkill. destruct (z =? a); [lia | apply Hr].
        -- constructor.
           ++ unfold csat, lval, hkill. cbn [fst snd]. rewrite Nat.eqb_refl.
              destruct s; lia.
           ++ apply Forall_forall. intros c Hc. apply csat_kill.
              rewrite Forall_forall in HF. apply HF. apply in_map. exact Hc.
        -- apply in_remove in Ht as [Ht Nt].
           exists t. split; [exact Ht |]. unfold hkill.
           apply Nat.eqb_neq in Nt. rewrite Nt. exact Hh.
      * (* s h a - s h a = 0: trivial *)
        destruct (IH E T HE Hnd ltac:(lia)) as (h & Hr & HF & Ht).
        exists h. split; [exact Hr | split; [| exact Ht]].
        constructor; [| exact HF].
        unfold csat, lval. cbn [fst snd]. destruct s, s'; try congruence; lia.
    + (* substitute h b := +- h a *)
      set (flip := Bool.eqb s s').
      pose proof (remove_length_NoDup b T Hnd) as HlT.
      destruct (IH (map (csub b a flip) E) (remove Nat.eq_dec b T))
        as (h & Hr & HF & t & Ht & Hh).
      { rewrite map_length. exact HE. }
      { apply remove_NoDup. exact Hnd. }
      { lia. }
      exists (hsub h b a flip). split; [| split].
      * intro z. unfold hsub. destruct (z =? b); [| apply Hr].
        specialize (Hr a). destruct flip; lia.
      * constructor.
        -- unfold csat, lval, hsub. cbn [fst snd].
           rewrite Nat.eqb_refl. apply Nat.eqb_neq in Nab. rewrite Nab.
           unfold flip. destruct s, s'; cbn; lia.
        -- apply Forall_forall. intros c Hc.
           rewrite Forall_forall in HF. specialize (HF (csub b a flip c)).
           unfold csat in *. rewrite !lval_sub. apply HF. apply in_map. exact Hc.
      * apply in_remove in Ht as [Ht Nt].
        exists t. split; [exact Ht |]. unfold hsub.
        apply Nat.eqb_neq in Nt. rewrite Nt. exact Hh.
Qed.

Lemma to_nat_shift_pair : forall (a b : nat) (u w : Z), 1 <= a -> 1 <= b ->
  (-1 <= u <= 1)%Z -> (-1 <= w <= 1)%Z -> (u + w = 0)%Z ->
  Z.to_nat (Z.of_nat a + u) + Z.to_nat (Z.of_nat b + w) = a + b.
Proof. intros a b u w Ha Hb Hu Hw Hs. lia. Qed.

Lemma Ksep_two : Ksep (fun _ => 2).
Proof. exists (fun _ => 1). intro v. reflexivity. Qed.

(* Every program correct on the n diagonal cells for every separable    *)
(* kernel asks at least n questions of every separable kernel whose g   *)
(* is everywhere at least 1: the -1/0/+1 shift stays in nat there.      *)
Theorem sep_positive_lower_bound : forall dat,
  (forall i j, dat i = dat j -> i = j) ->
  forall (n : nat) (Res : Type) (out : Res -> nat -> nat)
         (P : prog (nat -> nat) nat Res),
  (forall f, Ksep f -> forall x, x < n -> out (run P f) x = f (dg dat x)) ->
  forall f g, (forall v, f v = g (v 0) + g (v 1)) -> (forall z, 1 <= g z) ->
  n <= length (trace P f).
Proof.
  intros dat Hinj n Res out P Hcor f g Hg Hpos.
  assert (Hf : Ksep f) by (exists g; exact Hg).
  destruct (Nat.le_gt_cases n (length (trace P f))) as [H | H];
    [exact H | exfalso].
  set (E := map (fun q : nat -> nat => ((q 0, true), (q 1, true))) (trace P f)).
  destruct (shift_exists (length E) E (map dat (seq 0 n)) eq_refl)
    as (h & Hr & HF & t & Ht & Hh).
  { apply NoDup_map_inj; [apply NoDup_seq |].
    intros x y _ _ Exy. apply Hinj. exact Exy. }
  { unfold E. rewrite !map_length, seq_length. exact H. }
  apply in_map_iff in Ht as (x & Ex & Hx). subst t. apply in_seq in Hx.
  set (g' := fun a => Z.to_nat (Z.of_nat (g a) + h a)).
  set (f' := fun v : nat -> nat => g' (v 0) + g' (v 1)).
  assert (Hf' : Ksep f') by (exists g'; intro v; reflexivity).
  assert (Hag : forall q, In q (trace P f) -> f' q = f q).
  { intros q Hq. rewrite Forall_forall in HF.
    assert (Hc : csat h ((q 0, true), (q 1, true))).
    { apply HF. unfold E.
      apply in_map with (f := fun q : nat -> nat => ((q 0, true), (q 1, true))).
      exact Hq. }
    unfold csat, lval in Hc. cbn [fst snd] in Hc.
    rewrite (Hg q). unfold f', g'.
    apply to_nat_shift_pair; [apply Hpos | apply Hpos | apply Hr | apply Hr | exact Hc]. }
  destruct (run_agree _ _ _ P f f' Hag) as [Hrun _].
  pose proof (Hcor f' Hf' x ltac:(lia)) as H1.
  pose proof (Hcor f Hf x ltac:(lia)) as H2.
  rewrite Hrun, H2, (Hg (dg dat x)) in H1.
  unfold f', g', dg, argsOf in H1. cbn [nth] in H1.
  specialize (Hr (dat x)). specialize (Hpos (dat x)). lia.
Qed.

(* In particular, of the constant kernel 2. *)
Corollary sep_worst_case_lower_bound : forall dat,
  (forall i j, dat i = dat j -> i = j) ->
  forall (n : nat) (Res : Type) (out : Res -> nat -> nat)
         (P : prog (nat -> nat) nat Res),
  (forall f, Ksep f -> forall x, x < n -> out (run P f) x = f (dg dat x)) ->
  n <= length (trace P (fun _ => 2)).
Proof.
  intros dat Hinj n Res out P Hcor.
  apply (sep_positive_lower_bound dat Hinj n Res out P Hcor
           (fun _ => 2) (fun _ => 1)).
  - intro v. reflexivity.
  - intro z. lia.
Qed.

(* The separable class's worst-case query count is EXACTLY n: attained   *)
(* by sep_prog on every cell, forced on the diagonal cells alone.        *)
Corollary sep_query_complexity : forall dat,
  (forall i j, dat i = dat j -> i = j) ->
  forall n,
    (forall f, Ksep f -> forall ix, length ix = 2 -> (forall x, In x ix -> x < n) ->
       sep_out (run (sep_prog dat n) f) ix = f (argsOf dat ix)) /\
    (forall f, length (trace (sep_prog dat n) f) = n) /\
    (forall (Res : Type) (out : Res -> nat -> nat) (P : prog (nat -> nat) nat Res),
       (forall f, Ksep f -> forall x, x < n -> out (run P f) x = f (dg dat x)) ->
       n <= length (trace P (fun _ => 2)) /\
       forall f, Ksep f -> n <= 2 * length (trace P f)).
Proof.
  intros dat Hinj n. split; [| split].
  - intros f Hf ix Hl Hb. apply sep_prog_correct; assumption.
  - intro f. apply sep_prog_trace.
  - intros Res out P Hcor. split.
    + exact (sep_worst_case_lower_bound dat Hinj n Res out P Hcor).
    + exact (sep_lower_bound dat Hinj n Res out P Hcor).
Qed.

(* ------------------------------------------------------------------ *)
(* THE STORAGE HALF IS NOT BEATEN.  The witness answers a cell by       *)
(* combining two answers.  A store that answers each canonical cell by  *)
(* returning ONE stored cell (BladeOptimal's opaque-cell model, T1')    *)
(* still needs C (n + 1, 2) cells for the separable class: indicator    *)
(* kernels separate the canonical cells, so the access map is injective *)
(* on them, and the constant kernel 2 keeps it in range.                *)
(* ------------------------------------------------------------------ *)

Lemma canonical2_inv : forall n ix, canonical 2 0 n ix ->
  exists i j, ix = [i; j] /\ i <= j < n.
Proof.
  intros n ix H.
  destruct ix as [|i [|j [|k t]]]; cbn in H; try tauto.
  exists i, j. split; [reflexivity | destruct H as [_ [H _]]; exact H].
Qed.

Lemma sep_canonical_separate : forall dat,
  (forall i j, dat i = dat j -> i = j) ->
  forall n ix iy, canonical 2 0 n ix -> canonical 2 0 n iy ->
  (forall f, Ksep f -> f (argsOf dat ix) = f (argsOf dat iy)) -> ix = iy.
Proof.
  intros dat Hinj n ix iy Hx Hy Hall.
  destruct (canonical2_inv n ix Hx) as (i & j & -> & Hij).
  destruct (canonical2_inv n iy Hy) as (i' & j' & -> & Hij').
  assert (Hind : forall a,
    (if dat i =? dat a then 1 else 0) + (if dat j =? dat a then 1 else 0)
    = (if dat i' =? dat a then 1 else 0) + (if dat j' =? dat a then 1 else 0)).
  { intro a.
    assert (Hk : Ksep (fun v => (if v 0 =? dat a then 1 else 0)
                                + (if v 1 =? dat a then 1 else 0)))
      by (exists (fun z => if z =? dat a then 1 else 0); intro v; reflexivity).
    exact (Hall _ Hk). }
  assert (Heq : forall a b, (dat a =? dat b) = (a =? b)).
  { intros a b. destruct (Nat.eqb_spec a b) as [E | N].
    - subst b. apply Nat.eqb_refl.
    - apply Nat.eqb_neq. intro E. apply N. apply Hinj. exact E. }
  pose proof (Hind i) as Ei. pose proof (Hind j) as Ej.
  rewrite !Heq, !Nat.eqb_refl in Ei. rewrite !Heq, !Nat.eqb_refl in Ej.
  assert (Hc : i = i' /\ j = j').
  { revert Ei Ej.
    destruct (Nat.eqb_spec j i), (Nat.eqb_spec i' i), (Nat.eqb_spec j' i),
             (Nat.eqb_spec i j), (Nat.eqb_spec i' j), (Nat.eqb_spec j' j);
      intros Ei Ej; split; lia. }
  destruct Hc as [-> ->]. reflexivity.
Qed.

Theorem sep_storage_bound : forall dat,
  (forall i j, dat i = dat j -> i = j) ->
  forall (n : nat) (cells : list (nat -> nat)) (acc : list nat -> nat),
  (forall f, Ksep f -> forall ix, canonical 2 0 n ix ->
     nth (acc ix) (map f cells) 0 = f (argsOf dat ix)) ->
  C (n + 2 - 1) 2 <= length cells.
Proof.
  intros dat Hinj n cells acc Hread.
  rewrite <- storage_cardinality, <- (map_length acc (enum 2 0 n)).
  replace (length cells) with (length (seq 0 (length cells)))
    by apply seq_length.
  apply NoDup_incl_length.
  - apply NoDup_map_inj; [apply enum_NoDup |].
    intros x y Hx Hy E.
    apply enum_sound in Hx. apply enum_sound in Hy.
    apply (sep_canonical_separate dat Hinj n x y Hx Hy).
    intros f Hf. rewrite <- (Hread f Hf x Hx), <- (Hread f Hf y Hy), E.
    reflexivity.
  - intros a Ha. apply in_map_iff in Ha as (ix & <- & Hix).
    apply enum_sound in Hix. apply in_seq. split; [lia |].
    destruct (Nat.lt_ge_cases (acc ix) (length cells)) as [Hl | Hge];
      [lia | exfalso].
    pose proof (Hread (fun _ => 2) Ksep_two ix Hix) as H.
    rewrite nth_overflow in H by (rewrite map_length; exact Hge).
    discriminate H.
Qed.

(* ===================================================================== *)
(* PART C.  THE FREE CLASS AND THE BOUNDARY.                             *)
(* A law class is FREE on a list of cells when every cell's class can be *)
(* moved inside the class without moving any other class                 *)
(* (BladeOptimal's perturbable, Part B there).                           *)
(* ===================================================================== *)

Definition free_on {V Cl X A : Type} (cls : V -> Cl) (K : (V -> A) -> Prop)
  (args : X -> V) (R : list X) : Prop :=
  forall x, In x R -> perturbable V Cl X A cls K args x.

(* the symmetric class is free on the canonical cells (a re-export) *)
Theorem Ksym_free : forall r dat n,
  free_on (scls r) (Ksym r) (argsOf dat) (enum r 0 n).
Proof. intros r dat n x _. apply sym_perturbable. Qed.

(* An intrinsic freeness: K is closed under adding the indicator of a   *)
(* class.  It implies free_on on every list of cells.                   *)
Definition indicator_closed (r : nat) (K : ((nat -> nat) -> nat) -> Prop) : Prop :=
  forall k c, K k ->
    K (fun v => k v + if list_eq_dec Nat.eq_dec (scls r v) c then 1 else 0).

Lemma indicator_closed_free : forall r K, indicator_closed r K ->
  forall dat R, free_on (scls r) K (argsOf dat) R.
Proof.
  intros r K HK dat R x _.
  exists (fun k v => k v + if list_eq_dec Nat.eq_dec (scls r v) (scls r (argsOf dat x))
                           then 1 else 0).
  split; [| split].
  - intros k Hk. apply HK. exact Hk.
  - intros k v Hne.
    destruct (list_eq_dec Nat.eq_dec (scls r v) (scls r (argsOf dat x)));
      [contradiction | lia].
  - intro k.
    destruct (list_eq_dec Nat.eq_dec (scls r (argsOf dat x)) (scls r (argsOf dat x)));
      [lia | contradiction].
Qed.

Lemma Ksym_indicator_closed : forall r, indicator_closed r (Ksym r).
Proof.
  intros r k c Hk. apply Kcls_Ksym. apply Ksym_Kcls in Hk.
  intros v v' E. rewrite (Hk v v' E), E. reflexivity.
Qed.

(* On the canonical-cell task the bound is attained for ANY kernel, no   *)
(* law needed: ask every canonical cell (BladeOptimal's sym_prog) and    *)
(* read a cell at its position.                                          *)
Definition canon_out (r n : nat) (o : list nat) (ix : list nat) : nat :=
  nth (index_of ix (enum r 0 n)) o 0.

Theorem canonical_task_attained : forall r dat n,
  (forall f ix, canonical r 0 n ix ->
     canon_out r n (run (sym_prog r dat n) f) ix = f (argsOf dat ix)) /\
  (forall f, length (trace (sym_prog r dat n) f) = C (n + r - 1) r).
Proof.
  intros r dat n. split.
  - intros f ix Hix. unfold canon_out, sym_prog.
    rewrite ask_all_run. cbn [rev app]. rewrite map_map.
    apply (nth_index_of nat (fun t => f (argsOf dat t)) 0).
    apply enum_complete. exact Hix.
  - intro f. unfold sym_prog. rewrite ask_all_trace, map_length.
    apply storage_cardinality.
Qed.

(* the orbit bound holds for EVERY law class free on the canonical cells *)
(* (orbit_lower_bound restated; the count is the S_r orbit count on the  *)
(* canonical-cell task, whatever K's own law is)                         *)
Theorem free_class_bound : forall r dat,
  (forall i j, dat i = dat j -> i = j) ->
  forall (K : ((nat -> nat) -> nat) -> Prop) (n : nat) (Res : Type)
         (out : Res -> list nat -> nat) (P : prog (nat -> nat) nat Res),
  free_on (scls r) K (argsOf dat) (enum r 0 n) ->
  (forall f, K f -> forall ix, canonical r 0 n ix ->
     out (run P f) ix = f (argsOf dat ix)) ->
  forall f, K f -> C (n + r - 1) r <= length (trace P f).
Proof.
  intros r dat Hinj K n Res out P Hfree Hcor f Hf.
  rewrite <- storage_cardinality.
  apply (orbit_lower_bound (nat -> nat) (list nat) (list nat) nat Res
           (scls r) (list_eq_dec Nat.eq_dec) K (argsOf dat) out (enum r 0 n) P).
  - apply NoDup_map_inj; [apply enum_NoDup |].
    intros x y Hx Hy E.
    apply (scls_args_inj r dat Hinj n); try (apply enum_sound; assumption).
    exact E.
  - exact Hfree.
  - intros k Hk x Hx. apply Hcor; [exact Hk | apply enum_sound; exact Hx].
  - exact Hf.
Qed.

(* The separable class is free on NO class: whatever the tuple, the     *)
(* zero kernel cannot be moved there without moving another class.      *)
Theorem separable_nowhere_free : forall dat ix,
  ~ perturbable (nat -> nat) (list nat) (list nat) nat (scls 2) Ksep (argsOf dat) ix.
Proof.
  intros dat ix (pert & HK & Hoff & Hon).
  set (w := argsOf dat ix) in *.
  destruct (HK (fun _ => 0) Ksep_zero) as [g Hg].
  assert (Hz : forall v,
    ~ ((v 0 = w 0 /\ v 1 = w 1) \/ (v 0 = w 1 /\ v 1 = w 0)) ->
    g (v 0) + g (v 1) = 0).
  { intros v Hv. rewrite <- (Hg v). apply (Hoff (fun _ => 0) v).
    intro E. apply Hv. apply scls2_eq. exact E. }
  pose proof (Hon (fun _ => 0)) as Hne. rewrite (Hg w) in Hne.
  destruct (Nat.eq_dec (w 0) (w 1)) as [E | N].
  - pose proof (Hz (fun p => if p =? 0 then w 0 else S (w 0))) as H.
    cbn beta in H. simpl in H.
    assert (Hg0 : g (w 0) + g (S (w 0)) = 0) by (apply H; lia).
    rewrite <- E in Hne. lia.
  - pose proof (Hz (fun _ => w 0)) as H0. pose proof (Hz (fun _ => w 1)) as H1.
    cbn beta in H0, H1.
    assert (A0 : g (w 0) + g (w 0) = 0) by (apply H0; lia).
    assert (A1 : g (w 1) + g (w 1) = 0) by (apply H1; lia).
    lia.
Qed.

Corollary separable_not_free : forall dat n, 2 <= n ->
  ~ free_on (scls 2) Ksep (argsOf dat) (enum 2 0 n).
Proof.
  intros dat n Hn H.
  apply (separable_nowhere_free dat [0; 1]). apply H.
  apply enum_complete. cbn. repeat split; lia.
Qed.

Corollary Ksep_not_indicator_closed : ~ indicator_closed 2 Ksep.
Proof.
  intro H. apply (separable_nowhere_free (fun i => i) [0; 1]).
  apply (indicator_closed_free 2 Ksep H (fun i => i) [[0; 1]]).
  left. reflexivity.
Qed.

(* ------------------------------------------------------------------ *)
(* THE BOUNDARY.  The bound holds on every law class free on the        *)
(* orbits (free_class_bound) and is attained there on the canonical-    *)
(* cell task (canonical_task_attained); the symmetric class is free     *)
(* (Ksym_free).  The separable class -- inside Ksym 2, free on no class *)
(* -- is answered on every dense cell with fewer QUERIES than the       *)
(* orbit count, but its opaque-cell STORAGE count is not beaten.  One-  *)
(* directional: the converse is open (see the header).                  *)
(* ------------------------------------------------------------------ *)
Theorem orbit_bound_boundary : forall dat,
  (forall i j, dat i = dat j -> i = j) ->
  (forall r (K : ((nat -> nat) -> nat) -> Prop) n (Res : Type)
          (out : Res -> list nat -> nat) (P : prog (nat -> nat) nat Res),
     free_on (scls r) K (argsOf dat) (enum r 0 n) ->
     (forall f, K f -> forall ix, canonical r 0 n ix ->
        out (run P f) ix = f (argsOf dat ix)) ->
     forall f, K f -> C (n + r - 1) r <= length (trace P f)) /\
  (forall r n,
     (forall f ix, canonical r 0 n ix ->
        canon_out r n (run (sym_prog r dat n) f) ix = f (argsOf dat ix)) /\
     (forall f, length (trace (sym_prog r dat n) f) = C (n + r - 1) r)) /\
  (forall r n, free_on (scls r) (Ksym r) (argsOf dat) (enum r 0 n)) /\
  (forall f, Ksep f -> Ksym 2 f) /\
  (forall ix, ~ perturbable (nat -> nat) (list nat) (list nat) nat
                  (scls 2) Ksep (argsOf dat) ix) /\
  (forall n, 2 <= n ->
     (forall f, Ksep f -> forall ix, length ix = 2 -> (forall x, In x ix -> x < n) ->
        sep_out (run (sep_prog dat n) f) ix = f (argsOf dat ix)) /\
     (forall f, length (trace (sep_prog dat n) f) = n) /\
     n < C (n + 2 - 1) 2) /\
  (forall n (cells : list (nat -> nat)) (acc : list nat -> nat),
     (forall f, Ksep f -> forall ix, canonical 2 0 n ix ->
        nth (acc ix) (map f cells) 0 = f (argsOf dat ix)) ->
     C (n + 2 - 1) 2 <= length cells).
Proof.
  intros dat Hinj.
  split; [| split; [| split; [| split; [| split; [| split]]]]].
  - intros r. apply (free_class_bound r dat Hinj).
  - intros r n. apply canonical_task_attained.
  - intros r n. apply Ksym_free.
  - exact Ksep_Ksym.
  - apply separable_nowhere_free.
  - intros n Hn. split; [| split].
    + intros f Hf ix Hl Hb. apply sep_prog_correct; assumption.
    + intro f. apply sep_prog_trace.
    + apply separable_beats_orbit_bound. exact Hn.
  - apply (sep_storage_bound dat Hinj).
Qed.

(* ------------------------------------------------------------------ *)
(* THE TWO SIDES OF THE GENERICITY HYPOTHESIS.  orbit_lower_bound needs *)
(* distinct classes (NoDup) and perturbable classes.  Constant data     *)
(* breaks the first -- the canonical cells collapse into one class, and *)
(* one query answers everything (nongeneric_data_beats_bound).  The     *)
(* separable law breaks the second -- no class can be moved alone, and  *)
(* n queries answer everything.  Both at n = 3, against 6 orbits.       *)
(* ------------------------------------------------------------------ *)
Theorem genericity_two_sides :
  (* data side *)
  ~ NoDup (map (fun ix => scls 2 (argsOf (fun _ => 0) ix)) (enum 2 0 3)) /\
  (exists P : prog (nat -> nat) nat nat,
     (forall f ix, run P f = f (argsOf (fun _ => 0) ix)) /\
     (forall f, length (trace P f) = 1) /\ 1 < C (3 + 2 - 1) 2) /\
  (* kernel side *)
  (forall dat, ~ perturbable (nat -> nat) (list nat) (list nat) nat
                   (scls 2) Ksep (argsOf dat) [0; 1]) /\
  (forall dat, (forall i j, dat i = dat j -> i = j) ->
     exists (P : prog (nat -> nat) nat (list nat)) (out : list nat -> list nat -> nat),
       (forall f, Ksep f -> forall ix, length ix = 2 -> (forall x, In x ix -> x < 3) ->
          out (run P f) ix = f (argsOf dat ix)) /\
       (forall f, length (trace P f) = 3) /\ 3 < C (3 + 2 - 1) 2).
Proof.
  split; [| split; [| split]].
  - intro H. vm_compute in H.
    inversion H as [| a l Hn Hnd]. apply Hn. left. reflexivity.
  - exact nongeneric_data_beats_bound.
  - intro dat. apply separable_nowhere_free.
  - intros dat Hinj.
    destruct (structured_kernel_beats_bound dat Hinj 3 ltac:(lia))
      as (P & out & Hc & Ht & Hlt & _ & _).
    exists P, out. split; [exact Hc | split; [exact Ht | exact Hlt]].
Qed.

(* ===================================================================== *)
(* PART D.  PINS.                                                        *)
(* ===================================================================== *)

Example pin_n3 :
  C (3 + 2 - 1) 2 = 6 /\
  length (trace (sep_prog (fun i => i) 3) (fun _ => 0)) = 3.
Proof. split; vm_compute; reflexivity. Qed.

Example pin_n5 :
  C (5 + 2 - 1) 2 = 15 /\
  length (trace (sep_prog (fun i => i) 5) (fun _ => 0)) = 5 /\
  length (trace (zero_first_prog (fun i => i) 5) (fun _ => 0)) = 3.
Proof. split; [| split]; vm_compute; reflexivity. Qed.
