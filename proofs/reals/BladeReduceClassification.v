(* ===================================================================== *)
(* reals/BladeReduceClassification.v -- EXACT RECURRENCE REDUCTION: the  *)
(* classifier of BladeReduceCompiler is EXACT over the reals.            *)
(*                                                                       *)
(*       THIS FILE IS NOT PART OF THE AXIOM-FREE TOWER.                  *)
(*                                                                       *)
(* Coq's standard-library real numbers; the three axioms of              *)
(* BladeRealDensity (sig_forall_dec, sig_not_dec,                        *)
(* functional_extensionality_dep).  Own directory, own _CoqProject, not  *)
(* counted.                                                              *)
(*                                                                       *)
(*   real_compile_sound        accepted programs: the compiler theorem   *)
(*                             at K = R;                                 *)
(*   refused_no_reduction      refused programs: at the parameter values *)
(*                             of the witness point there is NO update   *)
(*                             function of the summary, continuous or    *)
(*                             not, at any extent N >= d r -- by         *)
(*                             BladeRealDensity's                        *)
(*                             closure_refused_R_polynomial applied to   *)
(*                             the leading coefficient with its          *)
(*                             parameters frozen;                        *)
(*   classification_exact      on every well-formed program the          *)
(*                             classifier answers, and the answer is     *)
(*                             right in both directions;                 *)
(*   special_value_program_refused   x' = x + theta x^2 is affine at     *)
(*                             theta = 0 and is still refused: at        *)
(*                             theta = 1 no update function of S exists  *)
(*                             for N >= 2.                               *)
(*                                                                       *)
(* Scope, stated once.  "No reduced program" means no function G of the  *)
(* first r power sums closing the update at the witness parameters; a    *)
(* reduced program must serve every parameter value, so none exists.     *)
(* The threshold N >= d r is sufficient, not sharp (section 15.1).       *)
(* ===================================================================== *)

From Blade Require Import BladeBinomial BladeSummary BladeMomentClosure
  BladeRankDomain BladeProuhet BladeNewton BladeReduceCompiler.
From BladeReals Require Import BladeSmoothRank BladeRealCollision
  BladeRealDensity.
Require Import Reals Lra List Arith Lia ZArith Bool.
Import ListNotations.
Local Open Scope nat_scope.

Local Notation Rpev := (pev R Rplus Rmult).
Local Notation Rzev := (zev R 0%R 1%R Rplus Rmult Ropp).
(* BladeRealDensity also has a phi -- the Newton map.  This is the       *)
(* compiler's: the reading of an integer constant in R.                  *)
Local Notation Rphi := (BladeReduceCompiler.phi R 0%R 1%R Rplus Rmult Ropp).
Local Notation Rqr := (qr R 0%R 1%R Rplus Rmult).
Local Notation Rsstep := (sstep R 0%R 1%R Rplus Rmult Ropp).
Local Notation Rsobs := (sobs R 0%R 1%R Rplus Rmult Ropp).
Local Notation Rrstep := (rstep R 0%R 1%R Rplus Rmult Ropp).
Local Notation Rrobs := (robs R 0%R 1%R Rplus Rmult Ropp).

(* ===================================================================== *)
(* Part A.  Accepted programs: the compiler theorem over the reals.      *)
(* ===================================================================== *)

Theorem real_compile_sound : forall P gs, wf P = true ->
  compile P = Some gs ->
  forall N th w xs, length xs = N ->
    trace (Rsstep P th) (Rsobs P th) w xs
    = trace (Rrstep gs (p_r P) (p_m P) N th) (Rrobs P N th) w
            (Rqr (p_r P) xs).
Proof.
  intros P gs Hwf Hc.
  apply (compile_sound R 0%R 1%R Rplus Rmult Rminus Ropp RTheory
           Rmult_integral R_char0 P gs Hwf Hc).
Qed.

(* ===================================================================== *)
(* Part B.  Refused programs: at the witness parameters there is NO      *)
(* update function of the summary, at any extent N >= d r.               *)
(* ===================================================================== *)

(* The leading coefficient with the parameters frozen: a polynomial in   *)
(* the summary alone.                                                    *)
Definition freeze (r : nat) (th : nat -> R) (e : pexp R) : pexp R :=
  psubst R (fun i => if Nat.ltb i r then PVar i else PConst (th (i - r))) e.

Lemma freeze_ev : forall r th e (z : list R),
  Rpev (freeze r th e) (fun i => nth i z 0%R)
  = Rpev e (cenv R 0%R r z th).
Proof.
  intros r th e z. unfold freeze. rewrite (pev_subst R Rplus Rmult).
  apply (pev_ext R Rplus Rmult). intro i. unfold cenv.
  destruct (Nat.ltb i r); reflexivity.
Qed.

Lemma wf_parts : forall P, wf P = true ->
  1 <= p_r P /\
  (forall e, In e (p_co P) -> pbelow (p_n P) e = true) /\
  pbelow (p_n P) (p_ob P) = true.
Proof.
  intros P H. unfold wf in H. apply andb_true_iff in H. destruct H as [H Hob].
  apply andb_true_iff in H. destruct H as [Hr Hco].
  rewrite forallb_forall in Hco. apply Nat.leb_le in Hr. auto.
Qed.

Theorem refused_no_reduction : forall P d A z, wf P = true ->
  classify P = Refused d A z ->
  forall N, d * p_r P <= N ->
  forall G : unit -> list R -> list R,
    ~ (forall u y, length y = N ->
         Rqr (p_r P) (Rsstep P (wtheta R 0%R 1%R Rplus Rmult Ropp (p_r P) z)
                             u y)
         = G u (Rqr (p_r P) y)).
Proof.
  intros P d A z Hwf Hcl N HN G Hclosed.
  destruct (wf_parts P Hwf) as [Hr [Hco _]].
  destruct (classify_refused P d A z Hcl) as [Hd [Hlen [HA Hnr]]].
  set (r := p_r P) in *. set (th := wtheta R 0%R 1%R Rplus Rmult Ropp r z).
  pose proof (tailzero_domain R 0%R 1%R Rplus Rmult Rminus Ropp RTheory
                Rmult_integral R_char0 P Hwf) as Hz.
  (* A is one of the program's coefficients, so its variables are bound. *)
  assert (HdL : d < length (p_co P)).
  { destruct (strip_split (p_n P) (p_co P)) as [E _].
    rewrite E, app_length. lia. }
  assert (HAb : pbelow (p_n P) A = true)
    by (rewrite HA; apply Hco; apply nth_In; exact HdL).
  set (a := coefK R 0%R 1%R Rplus Rmult Ropp P th).
  set (A' := freeze r th (zmap R 0%R 1%R Rplus Rmult Ropp A)).
  refine (closure_refused_R_polynomial d r N a A' Hd Hr HN _ _ G _).
  - intros zz _. unfold a, coefK, A'. rewrite freeze_ev.
    fold r. rewrite <- HA. reflexivity.
  - exists (wsummary R 0%R 1%R Rplus Rmult Ropp r z). split.
    + unfold wsummary. rewrite map_length, firstn_length.
      destruct (nonroot_some A (p_n P) z Hnr) as [Lz _].
      unfold p_n in Lz. fold r in Lz. lia.
    + unfold A'. rewrite freeze_ev.
      change (Rpev (zmap R 0%R 1%R Rplus Rmult Ropp A)) with (Rzev A).
      rewrite (zev_below R 0%R 1%R Rplus Rmult Ropp (p_n P) A _
                 (fun i => Rphi (zenv z i)) HAb).
      * apply (nonroot_some_K R 0%R 1%R Rplus Rmult Rminus Ropp RTheory
                 R_char0 A (p_n P) z Hnr).
      * intros i Hi. unfold th.
        apply (cenv_witness R 0%R 1%R Rplus Rmult Ropp r (p_m P) z i).
        exact Hi.
  - intros u y Hy. destruct u. unfold a, r.
    rewrite <- (sstep_truncated R 0%R 1%R Rplus Rmult Rminus Ropp RTheory
                  P th d y Hz Hlen).
    apply Hclosed. exact Hy.
Qed.

(* ===================================================================== *)
(* Part C.  THE CLASSIFICATION IS EXACT.  On every well-formed program   *)
(* of the fragment the compiler answers, and its answer is right: an     *)
(* accepted program has a reduced program that simulates it at every     *)
(* extent and every parameter vector; a refused one has none, at the     *)
(* witness parameters, at any extent N >= d r.                           *)
(* ===================================================================== *)

Theorem classification_exact : forall P, wf P = true ->
  match classify P with
  | Reduced _ _ =>
      exists gs, compile P = Some gs /\
        forall N th w xs, length xs = N ->
          trace (Rsstep P th) (Rsobs P th) w xs
          = trace (Rrstep gs (p_r P) (p_m P) N th) (Rrobs P N th) w
                  (Rqr (p_r P) xs)
  | Refused d A z =>
      2 <= d /\ compile P = None /\
      forall N, d * p_r P <= N ->
      forall G : unit -> list R -> list R,
        ~ (forall u y, length y = N ->
             Rqr (p_r P)
                 (Rsstep P (wtheta R 0%R 1%R Rplus Rmult Ropp (p_r P) z) u y)
             = G u (Rqr (p_r P) y))
  | Unknown => False
  end.
Proof.
  intros P Hwf. destruct (classify P) as [a1 a0|d A z|] eqn:Hcl.
  - exists (rprog_of (p_r P) (p_m P) a1 a0). split.
    + unfold compile. rewrite Hcl. reflexivity.
    + apply real_compile_sound; [exact Hwf|].
      unfold compile. rewrite Hcl. reflexivity.
  - destruct (classify_refused P d A z Hcl) as [Hd _]. split; [exact Hd|].
    split; [unfold compile; rewrite Hcl; reflexivity|].
    intros N HN. apply (refused_no_reduction P d A z Hwf Hcl N HN).
  - exact (classify_total P Hcl).
Qed.

(* The draft's adversarial case "special parameter values": the program  *)
(* x' = x + theta_0 x^2 is affine at theta_0 = 0, yet must be refused,   *)
(* because at theta_0 = 1 no update function of S exists for N >= 2.     *)
Corollary special_value_program_refused : forall N, 2 <= N ->
  forall G : unit -> list R -> list R,
    ~ (forall u y, length y = N ->
         Rqr 1 (Rsstep ex_special_value
                       (wtheta R 0%R 1%R Rplus Rmult Ropp 1 [0%Z; 1%Z]) u y)
         = G u (Rqr 1 y)).
Proof.
  intros N HN.
  assert (Hcl : classify ex_special_value = Refused 2 (PVar 1) [0%Z; 1%Z])
    by (vm_compute; reflexivity).
  assert (Hwf : wf ex_special_value = true) by (vm_compute; reflexivity).
  apply (refused_no_reduction ex_special_value 2 (PVar 1) [0%Z; 1%Z] Hwf Hcl N).
  change (p_r ex_special_value) with 1. lia.
Qed.
