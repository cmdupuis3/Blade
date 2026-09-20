(* ===================================================================== *)
(* BladeSummary.v -- EXACT RECURRENCE REDUCTION, the certificate         *)
(* calculus (docs/research/exact-recurrence-reduction-proofs.md, P1-P3,  *)
(* and a discrete form of its lower-bound half).                         *)
(*                                                                       *)
(* A recurrence  F : U -> X -> X  observed through  h : X -> Y  may be   *)
(* replaced by a recurrence  G : U -> Z -> Z  on summaries  q : X -> Z   *)
(* observed through  hb  when two identities hold on an invariant set:   *)
(*                                                                       *)
(*   cert_step   q (F u x) = G u (q x)          (the draft's C1)         *)
(*   cert_obs    h x       = hb (q x)           (the draft's C2)         *)
(*                                                                       *)
(* Everything here is induction and equality over abstract types; no     *)
(* algebra, no analysis, no decidability.  BladeMomentClosure supplies   *)
(* the algebra that makes a certificate exist.                           *)
(*                                                                       *)
(*   summary_run_sound,       P1: every finite execution, and every      *)
(*   summary_trace_sound      PREFIX observation of it, is preserved;    *)
(*   summary_feedback_sound   P1a: a policy choosing inputs from the     *)
(*                            observation history chooses the same       *)
(*                            inputs in both systems;                    *)
(*   summary_guard_sound      P1b: a guard that factors through the      *)
(*                            summary stops both at the same step, and   *)
(*                            budget exhaustion (BL8010) is preserved    *)
(*                            AS an outcome, not erased;                 *)
(*   summary_guard_certificate  the frozen-after-stop recurrence is      *)
(*                            itself certified, so P1 covers the whole   *)
(*                            budget-extent array (guarded_state ties    *)
(*                            the two readings together).                *)
(*                                                                       *)
(*   summary_compose_sound    P3a: certificates compose along q then r;  *)
(*   summary_product_sound    P3b: a coupled product is certified        *)
(*                            componentwise ONLY with the shared         *)
(*                            arguments in the premise --                *)
(*   isolated_certificates_do_not_compose  a closed witness: each        *)
(*                            subsystem reduces alone, the coupling      *)
(*                            reads a discarded coordinate, and no       *)
(*                            update of the paired summary exists.       *)
(*                                                                       *)
(*   summary_dag_sound        P2: evaluation over a finite dependency    *)
(*                            graph in topological order, per-node       *)
(*                            summaries, shared predecessors allowed;    *)
(*   summary_tree_sound       the same for terms (catamorphism fusion);  *)
(*   lag_window_sound         a k-lag recurrence under Blade's           *)
(*                            zero-history convention IS a first-order   *)
(*                            recurrence on the k-window, so P1 applies. *)
(*                                                                       *)
(* THE LOWER-BOUND HALF, DISCRETE FORM.  The draft's P6 bounds the       *)
(* DIMENSION of a C^1 summary and needs real analysis.  What needs none: *)
(*                                                                       *)
(*   summary_refines_future   a certified summary never identifies two   *)
(*                            states some input word tells apart;        *)
(*   future_equiv_coarsest    future-equivalence is a congruence and the *)
(*                            coarsest one preserving the observation -- *)
(*                            the Myhill-Nerode quotient, relationally;  *)
(*   collision_refutes_update,  so ONE PAIR of states with equal         *)
(*   collision_refutes_reduction  summaries and different next summaries *)
(*                            (or different futures) refutes a candidate *)
(*                            summary against EVERY G and hb -- not just *)
(*                            polynomial or continuous ones.  This is    *)
(*                            the checkable "certified obstruction".     *)
(*   summary_card_lower_bound n pairwise distinguishable states force n  *)
(*                            summary VALUES (countdown_needs_n_values   *)
(*                            is the non-vacuity instance);              *)
(*   identity_observation_forces_injective  a consumer that reads the    *)
(*                            whole state admits no compression.         *)
(*                                                                       *)
(* Scope, stated once.  First-order, pure, total recurrences over an     *)
(* explicit input word; guards and budgets as above.  Equality is        *)
(* EXACT: nothing here speaks to floating point, where the ring laws a   *)
(* certificate is built from do not hold (the draft's sect. 11.2).  No   *)
(* source-language fragment, no recognition, no code generation.  A      *)
(* cardinality bound says nothing about dimension: over an infinite      *)
(* carrier Z^N injects into Z, which is why the draft's smoothness       *)
(* hypotheses are load-bearing and why BladeMomentClosure's rank bounds  *)
(* restrict to polynomial encodings instead.                             *)
(*                                                                       *)
(* No imports from the tower.  Coq 8.18, stdlib only.                    *)
(* ===================================================================== *)

Require Import List Arith Lia.
Import ListNotations.

(* ===================================================================== *)
(* Part A.  Executions of a first-order recurrence over an input word.   *)
(* F_[] = id and F_(w ++ [u]) = F_u o F_w: chronological order.          *)
(* ===================================================================== *)

Definition run {U St : Type} (step : U -> St -> St) (w : list U) (s : St)
  : St :=
  fold_left (fun s' u => step u s') w s.

Lemma run_cons : forall (U St : Type) (step : U -> St -> St) u w s,
  run step (u :: w) s = run step w (step u s).
Proof. reflexivity. Qed.

Lemma run_app : forall (U St : Type) (step : U -> St -> St) w1 w2 s,
  run step (w1 ++ w2) s = run step w2 (run step w1 s).
Proof. intros. unfold run. apply fold_left_app. Qed.

Lemma run_snoc : forall (U St : Type) (step : U -> St -> St) w u s,
  run step (w ++ [u]) s = step u (run step w s).
Proof. intros. rewrite run_app. reflexivity. Qed.

(* The observation at EVERY prefix of the execution, initial state       *)
(* included.                                                             *)
Fixpoint trace {U St Y : Type} (step : U -> St -> St) (obs : St -> Y)
         (w : list U) (s : St) : list Y :=
  obs s :: match w with
           | [] => []
           | u :: w' => trace step obs w' (step u s)
           end.

Lemma trace_length : forall (U St Y : Type) (step : U -> St -> St)
    (obs : St -> Y) w s,
  length (trace step obs w s) = S (length w).
Proof.
  induction w as [|u w IH]; intros s; simpl; [reflexivity|].
  rewrite IH. reflexivity.
Qed.

Lemma trace_nth : forall (U St Y : Type) (step : U -> St -> St)
    (obs : St -> Y) d w k s,
  k <= length w ->
  nth k (trace step obs w s) d = obs (run step (firstn k w) s).
Proof.
  induction w as [|u w IH]; intros k s Hk; simpl in Hk.
  - assert (k = 0) by lia. subst. reflexivity.
  - destruct k as [|k]; [reflexivity|]. simpl. apply IH. lia.
Qed.

(* A closed loop: the input at each step is chosen by a fixed policy     *)
(* from the observation history.  Returns the state, the history and the *)
(* inputs chosen (newest first).                                         *)
Fixpoint closed {U St Y : Type} (step : U -> St -> St) (obs : St -> Y)
         (pi : list Y -> U) (n : nat) (s : St) (hist : list Y)
         (us : list U) : St * list Y * list U :=
  match n with
  | 0 => (s, hist, us)
  | S n' => closed step obs pi n' (step (pi (obs s :: hist)) s)
                   (obs s :: hist) (pi (obs s :: hist) :: us)
  end.

(* A guarded execution over a budget (the input word IS the budget).     *)
(* Blade's `while` arm: frozen once the guard goes false; BL8010 if the  *)
(* budget runs out with the guard still true.                            *)
Inductive outcome (St : Type) : Type :=
| Converged : nat -> St -> outcome St
| Exhausted : St -> outcome St.
Arguments Converged {St} _ _.
Arguments Exhausted {St} _.

Definition omap {St T : Type} (f : St -> T) (o : outcome St) : outcome T :=
  match o with
  | Converged k s => Converged k (f s)
  | Exhausted s => Exhausted (f s)
  end.

Definition ostate {St : Type} (o : outcome St) : St :=
  match o with Converged _ s => s | Exhausted s => s end.

Fixpoint guarded {U St : Type} (step : U -> St -> St) (b : St -> bool)
         (w : list U) (k : nat) (s : St) : outcome St :=
  match w with
  | [] => if b s then Exhausted s else Converged k s
  | u :: w' => if b s then guarded step b w' (S k) (step u s)
               else Converged k s
  end.

(* The same guard read as a recurrence: step while the guard holds,      *)
(* stand still after.                                                    *)
Definition gstep {U St : Type} (step : U -> St -> St) (b : St -> bool)
           (u : U) (s : St) : St :=
  if b s then step u s else s.

Lemma gstep_frozen : forall (U St : Type) (step : U -> St -> St) b w s,
  b s = false -> run (gstep step b) w s = s.
Proof.
  induction w as [|u w IH]; intros s Hb; [reflexivity|].
  rewrite run_cons. unfold gstep at 2. rewrite Hb. apply IH. exact Hb.
Qed.

Theorem guarded_state : forall (U St : Type) (step : U -> St -> St) b w k s,
  ostate (guarded step b w k s) = run (gstep step b) w s.
Proof.
  induction w as [|u w IH]; intros k s.
  - simpl. destruct (b s); reflexivity.
  - cbn [guarded]. rewrite run_cons. unfold gstep at 2.
    destruct (b s) eqn:Hb.
    + apply IH.
    + symmetry. apply gstep_frozen. exact Hb.
Qed.

(* ===================================================================== *)
(* Part B.  The certificate, and P1 with its two corollaries.            *)
(* ===================================================================== *)

Section Reduction.
  Variables U X Z Y : Type.
  Variable F : U -> X -> X.
  Variable q : X -> Z.
  Variable h : X -> Y.
  Variable Adm : X -> Prop.

  (* The invariant set is part of the certificate: initialization in it  *)
  (* is the caller's premise, preservation is cert_adm.                  *)
  Record certificate (G : U -> Z -> Z) (hb : Z -> Y) : Prop :=
    mk_certificate {
      cert_adm  : forall u x, Adm x -> Adm (F u x);
      cert_step : forall u x, Adm x -> q (F u x) = G u (q x);
      cert_obs  : forall x, Adm x -> h x = hb (q x)
    }.

  Variable G : U -> Z -> Z.
  Variable hb : Z -> Y.
  Hypothesis cert : certificate G hb.

  Lemma run_adm : forall w x, Adm x -> Adm (run F w x).
  Proof.
    induction w as [|u w IH]; intros x Hx; [exact Hx|].
    rewrite run_cons. apply IH. apply (cert_adm _ _ cert). exact Hx.
  Qed.

  Theorem summary_step_sound : forall u x, Adm x ->
    q (F u x) = G u (q x) /\ h (F u x) = hb (G u (q x)).
  Proof.
    intros u x Hx. split.
    - apply (cert_step _ _ cert). exact Hx.
    - rewrite <- (cert_step _ _ cert u x Hx). apply (cert_obs _ _ cert).
      apply (cert_adm _ _ cert). exact Hx.
  Qed.

  (* P1, state form.                                                     *)
  Theorem summary_run_sound : forall w x, Adm x ->
    q (run F w x) = run G w (q x).
  Proof.
    induction w as [|u w IH]; intros x Hx; [reflexivity|].
    rewrite !run_cons. rewrite (IH (F u x)).
    - rewrite (cert_step _ _ cert u x Hx). reflexivity.
    - apply (cert_adm _ _ cert). exact Hx.
  Qed.

  (* P1, observation form.                                               *)
  Theorem summary_obs_sound : forall w x, Adm x ->
    h (run F w x) = hb (run G w (q x)).
  Proof.
    intros w x Hx. rewrite <- (summary_run_sound w x Hx).
    apply (cert_obs _ _ cert). apply run_adm. exact Hx.
  Qed.

  (* P1, trace form: every prefix observation, not just the last.        *)
  Theorem summary_trace_sound : forall w x, Adm x ->
    trace F h w x = trace G hb w (q x).
  Proof.
    induction w as [|u w IH]; intros x Hx; simpl.
    - rewrite (cert_obs _ _ cert x Hx). reflexivity.
    - rewrite (cert_obs _ _ cert x Hx). f_equal.
      rewrite <- (cert_step _ _ cert u x Hx). apply IH.
      apply (cert_adm _ _ cert). exact Hx.
  Qed.

  (* P1a: under feedback both systems see the same history and are given *)
  (* the same inputs.                                                    *)
  Theorem summary_feedback_sound : forall pi n x hist us, Adm x ->
    closed G hb pi n (q x) hist us =
    (q (fst (fst (closed F h pi n x hist us))),
     snd (fst (closed F h pi n x hist us)),
     snd (closed F h pi n x hist us)).
  Proof.
    induction n as [|n IH]; intros x hist us Hx; simpl; [reflexivity|].
    rewrite <- (cert_obs _ _ cert x Hx).
    rewrite <- (cert_step _ _ cert _ x Hx).
    apply IH. apply (cert_adm _ _ cert). exact Hx.
  Qed.

  (* P1b: same stopping step, same outcome kind -- exhaustion included.  *)
  Theorem summary_guard_sound : forall (b : X -> bool) (bb : Z -> bool),
    (forall x, Adm x -> b x = bb (q x)) ->
    forall w k x, Adm x ->
      guarded G bb w k (q x) = omap q (guarded F b w k x).
  Proof.
    intros b bb Hb. induction w as [|u w IH]; intros k x Hx; simpl;
      rewrite <- (Hb x Hx); destruct (b x); try reflexivity.
    rewrite <- (cert_step _ _ cert u x Hx). apply IH.
    apply (cert_adm _ _ cert). exact Hx.
  Qed.

End Reduction.

Arguments certificate {U X Z Y} F q h Adm G hb.

(* ===================================================================== *)
(* Part C.  Certificates as values: identity, guard, composition,        *)
(* product -- and the product's premise is not optional.                 *)
(* ===================================================================== *)

Theorem summary_identity_sound : forall (U X Y : Type) (F : U -> X -> X)
    (h : X -> Y) (Adm : X -> Prop),
  (forall u x, Adm x -> Adm (F u x)) ->
  certificate F (fun x => x) h Adm F h.
Proof. intros. constructor; auto. Qed.

(* The frozen-after-stop recurrence is certified by the frozen summary   *)
(* recurrence, so every P1 form applies to it.                           *)
Theorem summary_guard_certificate : forall (U X Z Y : Type)
    (F : U -> X -> X) (q : X -> Z) (h : X -> Y) (Adm : X -> Prop)
    (G : U -> Z -> Z) (hb : Z -> Y) (b : X -> bool) (bb : Z -> bool),
  certificate F q h Adm G hb ->
  (forall x, Adm x -> b x = bb (q x)) ->
  certificate (gstep F b) q h Adm (gstep G bb) hb.
Proof.
  intros U X Z Y F q h Adm G hb b bb c Hb. constructor.
  - intros u x Hx. unfold gstep. destruct (b x); [|exact Hx].
    apply (cert_adm _ _ _ _ _ _ _ _ _ _ c). exact Hx.
  - intros u x Hx. unfold gstep. rewrite <- (Hb x Hx).
    destruct (b x); [|reflexivity].
    apply (cert_step _ _ _ _ _ _ _ _ _ _ c). exact Hx.
  - apply (cert_obs _ _ _ _ _ _ _ _ _ _ c).
Qed.

Corollary summary_frozen_trace_sound : forall (U X Z Y : Type)
    (F : U -> X -> X) (q : X -> Z) (h : X -> Y) (Adm : X -> Prop)
    (G : U -> Z -> Z) (hb : Z -> Y) (b : X -> bool) (bb : Z -> bool),
  certificate F q h Adm G hb ->
  (forall x, Adm x -> b x = bb (q x)) ->
  forall w x, Adm x ->
    trace (gstep F b) h w x = trace (gstep G bb) hb w (q x).
Proof.
  intros U X Z Y F q h Adm G hb b bb c Hb w x Hx.
  apply (summary_trace_sound U X Z Y (gstep F b) q h Adm (gstep G bb) hb).
  - apply summary_guard_certificate; assumption.
  - exact Hx.
Qed.

(* P3a.                                                                  *)
Theorem summary_compose_sound : forall (U X Z W Y : Type)
    (F : U -> X -> X) (G : U -> Z -> Z) (H : U -> W -> W)
    (q : X -> Z) (r : Z -> W) (h : X -> Y) (hb : Z -> Y) (hc : W -> Y)
    (AdmX : X -> Prop) (AdmZ : Z -> Prop),
  certificate F q h AdmX G hb ->
  certificate G r hb AdmZ H hc ->
  (forall x, AdmX x -> AdmZ (q x)) ->
  certificate F (fun x => r (q x)) h AdmX H hc.
Proof.
  intros U X Z W Y F G H q r h hb hc AdmX AdmZ c1 c2 Hq. constructor.
  - apply (cert_adm _ _ _ _ _ _ _ _ _ _ c1).
  - intros u x Hx.
    rewrite (cert_step _ _ _ _ _ _ _ _ _ _ c1 u x Hx).
    apply (cert_step _ _ _ _ _ _ _ _ _ _ c2). apply Hq. exact Hx.
  - intros x Hx.
    rewrite (cert_obs _ _ _ _ _ _ _ _ _ _ c1 x Hx).
    apply (cert_obs _ _ _ _ _ _ _ _ _ _ c2). apply Hq. exact Hx.
Qed.

(* P3b.  Each component's certificate is stated WITH the other           *)
(* component's state as an argument.                                     *)
Theorem summary_product_sound : forall (U X1 X2 Z1 Z2 Y : Type)
    (F1 : U -> X1 -> X2 -> X1) (F2 : U -> X1 -> X2 -> X2)
    (G1 : U -> Z1 -> Z2 -> Z1) (G2 : U -> Z1 -> Z2 -> Z2)
    (q1 : X1 -> Z1) (q2 : X2 -> Z2)
    (h : X1 * X2 -> Y) (hb : Z1 * Z2 -> Y) (Adm : X1 * X2 -> Prop),
  (forall u x1 x2, Adm (x1, x2) -> Adm (F1 u x1 x2, F2 u x1 x2)) ->
  (forall u x1 x2, Adm (x1, x2) ->
     q1 (F1 u x1 x2) = G1 u (q1 x1) (q2 x2)) ->
  (forall u x1 x2, Adm (x1, x2) ->
     q2 (F2 u x1 x2) = G2 u (q1 x1) (q2 x2)) ->
  (forall x1 x2, Adm (x1, x2) -> h (x1, x2) = hb (q1 x1, q2 x2)) ->
  certificate (fun u p => (F1 u (fst p) (snd p), F2 u (fst p) (snd p)))
              (fun p => (q1 (fst p), q2 (snd p))) h Adm
              (fun u z => (G1 u (fst z) (snd z), G2 u (fst z) (snd z))) hb.
Proof.
  intros U X1 X2 Z1 Z2 Y F1 F2 G1 G2 q1 q2 h hb Adm Ha H1 H2 Ho.
  constructor.
  - intros u [x1 x2] Hx. simpl. apply Ha. exact Hx.
  - intros u [x1 x2] Hx. simpl. rewrite (H1 u x1 x2 Hx), (H2 u x1 x2 Hx).
    reflexivity.
  - intros [x1 x2] Hx. simpl. apply Ho. exact Hx.
Qed.

(* ===================================================================== *)
(* Part D.  The lower-bound half, discrete form.                         *)
(* ===================================================================== *)

Section Nerode.
  Variables U X Z Y : Type.
  Variable F : U -> X -> X.
  Variable q : X -> Z.
  Variable h : X -> Y.
  Variable Adm : X -> Prop.

  (* No input word tells the two states apart.                           *)
  Definition future_equiv (x y : X) : Prop :=
    forall w, h (run F w x) = h (run F w y).

  Theorem summary_refines_future : forall G hb,
    certificate F q h Adm G hb ->
    forall x y, Adm x -> Adm y -> q x = q y -> future_equiv x y.
  Proof.
    intros G hb c x y Hx Hy Hq w.
    rewrite (summary_obs_sound U X Z Y F q h Adm G hb c w x Hx).
    rewrite (summary_obs_sound U X Z Y F q h Adm G hb c w y Hy).
    rewrite Hq. reflexivity.
  Qed.

  Theorem future_equiv_obs : forall x y, future_equiv x y -> h x = h y.
  Proof. intros x y H. exact (H []). Qed.

  Theorem future_equiv_congruence : forall x y u,
    future_equiv x y -> future_equiv (F u x) (F u y).
  Proof. intros x y u H w. exact (H (u :: w)). Qed.

  (* Any relation that preserves the observation and is carried along by *)
  (* every step lies inside future_equiv.                                *)
  Theorem future_equiv_coarsest : forall (Rel : X -> X -> Prop),
    (forall x y, Rel x y -> h x = h y) ->
    (forall x y u, Rel x y -> Rel (F u x) (F u y)) ->
    forall x y, Rel x y -> future_equiv x y.
  Proof.
    intros Rel Ho Hs x y Hr w. revert x y Hr.
    induction w as [|u w IH]; intros x y Hr.
    - apply Ho. exact Hr.
    - rewrite !run_cons. apply IH. apply Hs. exact Hr.
  Qed.

  (* THE CHECKABLE OBSTRUCTION.  Two admissible states, one summary, two *)
  (* next summaries: no update function of any kind exists.              *)
  Theorem collision_refutes_update : forall u x y,
    Adm x -> Adm y -> q x = q y -> q (F u x) <> q (F u y) ->
    forall G : U -> Z -> Z,
      ~ (forall u' x', Adm x' -> q (F u' x') = G u' (q x')).
  Proof.
    intros u x y Hx Hy Hq Hne G HG. apply Hne.
    rewrite (HG u x Hx), (HG u y Hy), Hq. reflexivity.
  Qed.

  Theorem collision_refutes_reduction : forall w x y,
    Adm x -> Adm y -> q x = q y -> h (run F w x) <> h (run F w y) ->
    forall G hb, ~ certificate F q h Adm G hb.
  Proof.
    intros w x y Hx Hy Hq Hne G hb c. apply Hne.
    apply (summary_refines_future G hb c x y Hx Hy Hq).
  Qed.

  (* Pairwise distinguishable states have pairwise distinct summaries.   *)
  Theorem distinguishable_summaries_distinct : forall G hb,
    certificate F q h Adm G hb ->
    forall l, NoDup l -> (forall x, In x l -> Adm x) ->
      (forall x y, In x l -> In y l -> x <> y -> ~ future_equiv x y) ->
      NoDup (map q l).
  Proof.
    intros G hb c. induction l as [|a l IH]; intros Hnd Ha Hd; simpl.
    - constructor.
    - inversion Hnd as [|a' l' Hnin Hnd']; subst. constructor.
      + intro Hin. apply in_map_iff in Hin. destruct Hin as [y [Hqy Hy]].
        apply (Hd a y (or_introl eq_refl) (or_intror Hy)).
        * intro E. subst. contradiction.
        * apply (summary_refines_future G hb c); auto using in_eq, in_cons.
      + apply IH; auto using in_cons.
  Qed.

  Theorem summary_card_lower_bound : forall G hb,
    certificate F q h Adm G hb ->
    forall (zs : list Z), (forall z, In z zs) ->
    forall l, NoDup l -> (forall x, In x l -> Adm x) ->
      (forall x y, In x l -> In y l -> x <> y -> ~ future_equiv x y) ->
      length l <= length zs.
  Proof.
    intros G hb c zs Hall l Hnd Ha Hd.
    rewrite <- (map_length q l). apply NoDup_incl_length.
    - apply (distinguishable_summaries_distinct G hb c l Hnd Ha Hd).
    - intros z _. apply Hall.
  Qed.

End Nerode.

Arguments future_equiv {U X Y} F h x y.

(* A consumer that reads the whole state admits no compression.          *)
Theorem identity_observation_forces_injective : forall (U X Z : Type)
    (F : U -> X -> X) (q : X -> Z) (Adm : X -> Prop) G hb,
  certificate F q (fun x => x) Adm G hb ->
  forall x y, Adm x -> Adm y -> q x = q y -> x = y.
Proof.
  intros U X Z F q Adm G hb c x y Hx Hy Hq.
  rewrite (cert_obs _ _ _ _ _ _ _ _ _ _ c x Hx).
  rewrite (cert_obs _ _ _ _ _ _ _ _ _ _ c y Hy).
  rewrite Hq. reflexivity.
Qed.

(* A guard that reads discarded state refutes the guard factorization.   *)
Theorem guard_collision_refutes : forall (X Z : Type) (q : X -> Z)
    (b : X -> bool) (Adm : X -> Prop) x y,
  Adm x -> Adm y -> q x = q y -> b x <> b y ->
  forall bb : Z -> bool, ~ (forall x', Adm x' -> b x' = bb (q x')).
Proof.
  intros X Z q b Adm x y Hx Hy Hq Hne bb Hb. apply Hne.
  rewrite (Hb x Hx), (Hb y Hy), Hq. reflexivity.
Qed.

(* Non-vacuity of the cardinality bound: a countdown observed only at    *)
(* zero.  States 0 .. n-1 are pairwise distinguishable, so every         *)
(* certified summary of it takes at least n values.                      *)
Definition countdown (_ : unit) (x : nat) : nat := pred x.
Definition at_zero (x : nat) : bool := Nat.eqb x 0.

Lemma countdown_run : forall k x, run countdown (repeat tt k) x = x - k.
Proof.
  induction k as [|k IH]; intros x; simpl repeat.
  - simpl. lia.
  - rewrite run_cons, IH. unfold countdown. lia.
Qed.

Lemma countdown_distinguishes : forall x y, x <> y ->
  ~ future_equiv countdown at_zero x y.
Proof.
  intros x y Hne H. destruct (Nat.lt_ge_cases x y) as [Hlt|Hge].
  - specialize (H (repeat tt x)). rewrite !countdown_run in H.
    unfold at_zero in H. rewrite Nat.sub_diag in H. simpl in H.
    symmetry in H. apply Nat.eqb_eq in H. lia.
  - specialize (H (repeat tt y)). rewrite !countdown_run in H.
    unfold at_zero in H. rewrite Nat.sub_diag in H. simpl in H.
    apply Nat.eqb_eq in H. lia.
Qed.

Theorem countdown_needs_n_values : forall (Z : Type) (q : nat -> Z) G hb
    (zs : list Z) n,
  certificate countdown q at_zero (fun _ => True) G hb ->
  (forall z, In z zs) -> n <= length zs.
Proof.
  intros Z q G hb zs n c Hall.
  rewrite <- (seq_length n 0).
  apply (summary_card_lower_bound unit nat Z bool countdown q at_zero
           (fun _ => True) G hb c zs Hall).
  - apply seq_NoDup.
  - intros; exact I.
  - intros x y _ _ Hne. apply countdown_distinguishes. exact Hne.
Qed.

(* The product premise is load-bearing.  Subsystem 1 carries a hidden    *)
(* coordinate its own summary rightly discards; subsystem 2 is reduced   *)
(* by the identity.  Couple them so that 2 reads the hidden coordinate   *)
(* and the paired summary has no update at all.                          *)
Definition iso_F (_ : unit) (p : (nat * nat) * nat) : (nat * nat) * nat :=
  ((S (fst (fst p)), snd (fst p)), snd p + snd (fst p)).
Definition iso_q (p : (nat * nat) * nat) : nat * nat :=
  (fst (fst p), snd p).

Theorem isolated_certificates_do_not_compose :
  (* subsystem 1 alone: x1 = (a, c), a counts up, c is never read        *)
  certificate (fun (_ : unit) (x : nat * nat) => (S (fst x), snd x))
              fst fst (fun _ => True) (fun _ a => S a) (fun a => a)
  (* subsystem 2 alone: a constant, reduced by the identity              *)
  /\ certificate (fun (_ : unit) (y : nat) => y) (fun y => y) (fun y => y)
                 (fun _ => True) (fun _ y => y) (fun y => y)
  (* coupled: no update of the paired summary exists                     *)
  /\ forall G : unit -> nat * nat -> nat * nat,
       ~ (forall u p, True -> iso_q (iso_F u p) = G u (iso_q p)).
Proof.
  split; [|split].
  - constructor; auto.
  - constructor; auto.
  - apply (collision_refutes_update unit _ _ iso_F iso_q (fun _ => True)
             tt ((0, 0), 0) ((0, 1), 0)); auto.
    vm_compute. discriminate.
Qed.

(* ===================================================================== *)
(* Part E.  P2: beyond a time-shaped chain.                              *)
(* ===================================================================== *)

Section Dag.
  Variables X Z : Type.
  (* Nodes are 0, 1, 2, ... in a topological order; pred v lists the     *)
  (* predecessors of v, repeats and sharing allowed.                     *)
  Variable pred : nat -> list nat.
  Hypothesis topo : forall v w, In w (pred v) -> w < v.
  Variable K : nat -> list X -> X.
  Variable Kb : nat -> list Z -> Z.
  Variable q : nat -> X -> Z.
  Variables (dX : X) (dZ : Z).

  (* D1, quantified over assignments of values to nodes: a shared        *)
  (* predecessor denotes ONE value.                                      *)
  Hypothesis D1 : forall v (val : nat -> X),
    q v (K v (map val (pred v)))
    = Kb v (map (fun w => q w (val w)) (pred v)).

  Fixpoint evalN {V : Type} (KK : nat -> list V -> V) (d : V) (n : nat)
    : list V :=
    match n with
    | 0 => []
    | S n' => evalN KK d n'
              ++ [KK n' (map (fun w => nth w (evalN KK d n') d) (pred n'))]
    end.

  Lemma evalN_length : forall (V : Type) (KK : nat -> list V -> V) d n,
    length (evalN KK d n) = n.
  Proof.
    induction n as [|n IH]; simpl; [reflexivity|].
    rewrite app_length, IH. simpl. lia.
  Qed.

  Theorem summary_dag_sound : forall n v, v < n ->
    nth v (evalN Kb dZ n) dZ = q v (nth v (evalN K dX n) dX).
  Proof.
    induction n as [|n IH]; intros v Hv; [lia|]. simpl.
    destruct (Nat.eq_dec v n) as [E|E].
    - subst v.
      rewrite !app_nth2 by (rewrite evalN_length; lia).
      rewrite !evalN_length, Nat.sub_diag. simpl.
      rewrite (D1 n (fun w => nth w (evalN K dX n) dX)).
      f_equal. apply map_ext_in. intros w Hw. apply IH.
      apply (topo n w Hw).
    - rewrite !app_nth1 by (rewrite evalN_length; lia).
      apply IH. lia.
  Qed.

  Corollary summary_dag_obs_sound : forall (Y : Type)
      (hv : nat -> X -> Y) (hbv : nat -> Z -> Y),
    (forall v x, hv v x = hbv v (q v x)) ->
    forall n v, v < n ->
      hv v (nth v (evalN K dX n) dX) = hbv v (nth v (evalN Kb dZ n) dZ).
  Proof.
    intros Y hv hbv Hh n v Hv. rewrite Hh, summary_dag_sound by exact Hv.
    reflexivity.
  Qed.

End Dag.

(* The same for terms: a summary that commutes with every constructor    *)
(* commutes with evaluation.                                             *)
Section Tree.
  Variables A L1 L2 : Type.

  Inductive stree : Type :=
  | SLeaf : A -> stree
  | SUn : L1 -> stree -> stree
  | SBin : L2 -> stree -> stree -> stree.

  Fixpoint seval {V : Type} (lf : A -> V) (k1 : L1 -> V -> V)
           (k2 : L2 -> V -> V -> V) (t : stree) : V :=
    match t with
    | SLeaf a => lf a
    | SUn f t' => k1 f (seval lf k1 k2 t')
    | SBin g t1 t2 => k2 g (seval lf k1 k2 t1) (seval lf k1 k2 t2)
    end.

  Theorem summary_tree_sound : forall (X Z : Type) (q : X -> Z)
      (lf : A -> X) (k1 : L1 -> X -> X) (k2 : L2 -> X -> X -> X)
      (lfb : A -> Z) (kb1 : L1 -> Z -> Z) (kb2 : L2 -> Z -> Z -> Z),
    (forall a, q (lf a) = lfb a) ->
    (forall f x, q (k1 f x) = kb1 f (q x)) ->
    (forall g x y, q (k2 g x y) = kb2 g (q x) (q y)) ->
    forall t, q (seval lf k1 k2 t) = seval lfb kb1 kb2 t.
  Proof.
    intros X Z q lf k1 k2 lfb kb1 kb2 H0 H1 H2.
    induction t as [a|f t IH|g t1 IH1 t2 IH2]; simpl.
    - apply H0.
    - rewrite H1, IH. reflexivity.
    - rewrite H2, IH1, IH2. reflexivity.
  Qed.

End Tree.

(* Bounded lag.  `gen` is the full-prefix recurrence (newest first): the *)
(* next slice may read the whole built prefix.  A k-lag kernel reads the *)
(* k newest slices, zero-padded -- Blade's convention that reads past    *)
(* the built prefix are zero.  `wrun` is the first-order recurrence on   *)
(* the k-window, started from the all-zero window.                       *)
Section Lag.
  Variable X : Type.
  Variable zero : X.
  Variable k : nat.
  Variable g : list X -> X.

  Definition pad (l : list X) : list X := firstn k (l ++ repeat zero k).

  Fixpoint gen (f : list X -> X) (n : nat) : list X :=
    match n with
    | 0 => []
    | S n' => f (gen f n') :: gen f n'
    end.

  Definition lagged (l : list X) : X := g (pad l).
  Definition wstep (w : list X) : list X := firstn k (g w :: w).

  Fixpoint wrun (n : nat) : list X :=
    match n with
    | 0 => repeat zero k
    | S n' => wstep (wrun n')
    end.

  Lemma firstn_cons_firstn : forall (a : X) l,
    firstn k (a :: firstn k l) = firstn k (a :: l).
  Proof.
    intros a l. destruct k as [|k']; [reflexivity|].
    rewrite !firstn_cons. f_equal. rewrite firstn_firstn. f_equal. lia.
  Qed.

  Theorem lag_window_sound : forall n, wrun n = pad (gen lagged n).
  Proof.
    induction n as [|n IH]; simpl.
    - unfold pad. simpl. symmetry. apply firstn_all2.
      rewrite repeat_length. lia.
    - rewrite IH. unfold wstep, lagged, pad at 1 3.
      rewrite firstn_cons_firstn. reflexivity.
  Qed.

  (* The window run is an ordinary first-order recurrence, so P1 applies *)
  (* to it verbatim.                                                     *)
  Theorem lag_window_is_first_order : forall n,
    wrun n = run (fun (_ : unit) w => wstep w) (repeat tt n)
                 (repeat zero k).
  Proof.
    induction n as [|n IH]; [reflexivity|]. simpl wrun. rewrite IH.
    change (repeat tt (S n)) with (tt :: repeat tt n).
    rewrite repeat_cons, run_snoc. reflexivity.
  Qed.

  (* Every slice of the lagged full-prefix recurrence is the head of a   *)
  (* window.                                                             *)
  Corollary lag_slice_from_window : forall n, 1 <= k ->
    hd zero (wrun (S n)) = hd zero (gen lagged (S n)).
  Proof.
    intros n Hk. rewrite lag_window_sound. simpl gen. unfold pad.
    destruct k as [|k']; [lia|]. reflexivity.
  Qed.

End Lag.
