(* ===================================================================== *)
(* BladeReductionAD.v -- EXACT RECURRENCE REDUCTION, the AD contract     *)
(* (docs/research/exact-recurrence-reduction-proofs.md, 11.4).           *)
(*                                                                       *)
(* BladeMomentClosure proves P5 for forward mode AS DUAL NUMBERS.        *)
(* Blade's AD is a source transformation (src/Grad*.fs): a tangent       *)
(* statement beside every primal one (synthesizeJvp / tangentOfExpr), or *)
(* a reverse sweep over the normalized statements accumulating           *)
(* cotangents (synthesizeRev / adjointOf), with a let rec recurrence     *)
(* replayed backwards over its stored trajectory.  This file models      *)
(* those rules on polynomial expressions and proves they are the P5      *)
(* derivatives.                                                          *)
(*                                                                       *)
(*   tan, tan_is_dual          the forward rules -- sum, and             *)
(*                             d(l r) = dl r + l dr -- compute exactly   *)
(*                             the dual-number tangent;                  *)
(*   adj, adj_pairing          the reverse rules -- add: both sides get  *)
(*                             the cotangent; mul: c r to the left, c l  *)
(*                             to the right; accumulate at a variable -- *)
(*                             are the TRANSPOSE of the forward rules:   *)
(*                             <adj e c g, v> = <g, v> + c tan e v;      *)
(*   exec, texec, rsweep,      straight-line blocks: the reverse sweep,  *)
(*   sweep_pairing             reading the forward values left behind,   *)
(*                             is the transpose of the tangent block;    *)
(*   tape, carry_sweep,        recurrences: the descending sweep over    *)
(*                             the                                       *)
(*   carry_pairing             STORED trajectory is the transpose of the *)
(*                             T-step tangent run -- the executed        *)
(*                             algorithm, never an implicit fixed point; *)
(*   reduction_commutes_forward,      q o F = G o q read in the dual     *)
(*   reduction_commutes_reverse       numbers gives the chain-rule       *)
(*                             instance with no chain rule; hence jvp on *)
(*                             the reduced program carries the pushed    *)
(*                             tangent at every horizon, and sweeping    *)
(*                             the reduced program then pulling back     *)
(*                             through q at the INITIAL state equals     *)
(*                             pulling back at the FINAL state then      *)
(*                             sweeping the original;                    *)
(*   compile_sound_dual        the compiler theorem read in the dual     *)
(*                             numbers, at every extent;                 *)
(*   ex_affine_reverse_commutes       both modes on a concrete program   *)
(*                             with the ACTUAL output of                 *)
(*                             BladeReduceCompiler.compile as the        *)
(*                             reduced side.                             *)
(*                                                                       *)
(* Scope, stated once.  A MODEL of the rules, written from a reading of  *)
(* Grad*.fs, over + and * only: no intrinsics, no units, no packed       *)
(* reconstruction; a map over N particles is N expression instances, not *)
(* an NFor.  Nothing here is checked against the F# code -- the link is  *)
(* by inspection, like every row of docs/proofs.md.  The while-guard arm *)
(* is outside: Blade refuses to differentiate it (BL5500).  For the      *)
(* compiled fragment at general N the dual-number certificate is         *)
(* compile_sound_dual; its restatement through tan/adj on N-particle     *)
(* syntax is done for one concrete program only.                         *)
(*                                                                       *)
(* Stdlib only, no axioms.  Imports the recurrence-reduction files of    *)
(* the tower.                                                            *)
(* ===================================================================== *)

From Blade Require Import BladeBinomial BladeSummary BladeMomentClosure
  BladeRankBound BladeRankDomain BladeProuhet BladeNewton BladeReduceCompiler.
Require Import List Arith Lia ZArith Ring Bool.
Import ListNotations.
Local Open Scope nat_scope.

Section EmittedRules.
  Variable K : Type.
  Variables (k0 k1 : K) (kadd kmul ksub : K -> K -> K) (kopp : K -> K).
  Hypothesis Kth : ring_theory k0 k1 kadd kmul ksub kopp (@eq K).
  Add Ring ad_ring : Kth.

  Local Infix "+!" := kadd (at level 50, left associativity).
  Local Infix "*!" := kmul (at level 40, left associativity).
  Local Notation Pev := (pev K kadd kmul).
  Local Notation PevD := (pevD K k0 kadd kmul).
  Local Notation Dd := (D K k0 kadd kmul).
  Local Notation Lift := (lift K).
  Local Notation Ksumf := (ksumf K k0 kadd).

  (* =================================================================== *)
  (* Part A.  The two expression rules, as Grad*.fs applies them.        *)
  (* =================================================================== *)

  (* tangentOfExpr: sum rule, and  d(l r) = dl r + l dr.                 *)
  Fixpoint tan (e : pexp K) (x v : nat -> K) : K :=
    match e with
    | PVar i => v i
    | PConst _ => k0
    | PAdd e1 e2 => tan e1 x v +! tan e2 x v
    | PMul e1 e2 => tan e1 x v *! Pev e2 x +! Pev e1 x *! tan e2 x v
    end.

  (* adjointOf: push the cotangent c down the expression, ACCUMULATING   *)
  (* into the cotangent of every variable reached.  add: both sides get  *)
  (* c.  mul: the left side gets c r, the right side c l.                *)
  Fixpoint adj (e : pexp K) (x : nat -> K) (c : K) (g : nat -> K)
    : nat -> K :=
    match e with
    | PVar i => setv g i (g i +! c)
    | PConst _ => g
    | PAdd e1 e2 => adj e2 x c (adj e1 x c g)
    | PMul e1 e2 => adj e2 x (c *! Pev e1 x) (adj e1 x (c *! Pev e2 x) g)
    end.

  (* The forward rule computes exactly the dual-number tangent of P5.    *)
  Theorem tan_is_dual : forall e x v, tan e x v = Dd e x v.
  Proof.
    unfold D.
    induction e as [i|c|e1 IH1 e2 IH2|e1 IH1 e2 IH2]; intros x v;
      cbn [tan pevD]; try reflexivity.
    - rewrite IH1, IH2. reflexivity.
    - rewrite IH1, IH2. unfold dmul. cbn [snd].
      rewrite !(pevD_fst_lift K k0 kadd kmul). ring.
  Qed.

  Corollary pevD_is_tan : forall e x v,
    PevD e (Lift x v) = (Pev e x, tan e x v).
  Proof.
    intros e x v. rewrite tan_is_dual. unfold D.
    rewrite <- (pevD_fst_lift K k0 kadd kmul e x v).
    apply surjective_pairing.
  Qed.

  Lemma tan_below : forall n e x x' v v', pbelow n e = true ->
    (forall i, i < n -> x i = x' i) -> (forall i, i < n -> v i = v' i) ->
    tan e x v = tan e x' v'.
  Proof.
    intros n.
    induction e as [i|c|e1 IH1 e2 IH2|e1 IH1 e2 IH2];
      intros x x' v v' Hb Hx Hv; cbn [pbelow tan] in *.
    - apply Hv. apply Nat.ltb_lt. exact Hb.
    - reflexivity.
    - apply andb_true_iff in Hb. destruct Hb as [H1 H2].
      rewrite (IH1 x x' v v' H1 Hx Hv), (IH2 x x' v v' H2 Hx Hv). reflexivity.
    - apply andb_true_iff in Hb. destruct Hb as [H1 H2].
      rewrite (IH1 x x' v v' H1 Hx Hv), (IH2 x x' v v' H2 Hx Hv).
      rewrite (pev_below K kadd kmul n e1 x x' H1 Hx).
      rewrite (pev_below K kadd kmul n e2 x x' H2 Hx). reflexivity.
  Qed.

  Lemma pbelow_mono : forall n M (e : pexp K), n <= M ->
    pbelow n e = true -> pbelow M e = true.
  Proof.
    intros n M.
    induction e as [i|c|e1 IH1 e2 IH2|e1 IH1 e2 IH2]; intros Hle Hb;
      cbn [pbelow] in *.
    - apply Nat.ltb_lt. apply Nat.ltb_lt in Hb. lia.
    - reflexivity.
    - apply andb_true_iff in Hb. destruct Hb as [H1 H2].
      rewrite (IH1 Hle H1), (IH2 Hle H2). reflexivity.
    - apply andb_true_iff in Hb. destruct Hb as [H1 H2].
      rewrite (IH1 Hle H1), (IH2 Hle H2). reflexivity.
  Qed.

  (* The pairing <g, v> over the first M variables.                      *)
  Definition dot (M : nat) (g v : nat -> K) : K :=
    Ksumf (fun i => g i *! v i) M.

  Lemma dot_accum : forall M g v i c, i < M ->
    dot M (setv g i (g i +! c)) v = dot M g v +! c *! v i.
  Proof.
    unfold dot. induction M as [|M IH]; intros g v i c Hi; [lia|].
    cbn [ksumf]. destruct (Nat.eq_dec i M) as [->|Hne].
    - rewrite (ksumf_ext K k0 kadd (fun j => setv g M (g M +! c) j *! v j)
                 (fun j => g j *! v j) M).
      + unfold setv. rewrite Nat.eqb_refl. ring.
      + intros j Hj. unfold setv.
        destruct (Nat.eqb_spec j M); [lia|reflexivity].
    - rewrite (IH g v i c ltac:(lia)). unfold setv.
      destruct (Nat.eqb_spec M i); [lia|]. ring.
  Qed.

  (* Consuming the cotangent of a variable that is being (re)defined.    *)
  Lemma dot_consume : forall M g v n t, n < M ->
    dot M (setv g n k0) v +! g n *! t = dot M g (setv v n t).
  Proof.
    unfold dot. induction M as [|M IH]; intros g v n t Hn; [lia|].
    cbn [ksumf]. destruct (Nat.eq_dec n M) as [->|Hne].
    - rewrite (ksumf_ext K k0 kadd (fun i => setv g M k0 i *! v i)
                 (fun i => g i *! setv v M t i) M).
      + unfold setv. rewrite Nat.eqb_refl. ring.
      + intros j Hj. unfold setv.
        destruct (Nat.eqb_spec j M); [lia|reflexivity].
    - assert (E := IH g v n t ltac:(lia)).
      unfold setv at 2 4. destruct (Nat.eqb_spec M n); [lia|].
      transitivity ((Ksumf (fun i => setv g n k0 i *! v i) M +! g n *! t)
                    +! g M *! v M); [ring|].
      rewrite E. reflexivity.
  Qed.

  (* THE ADJOINT RULE IS THE TRANSPOSE OF THE TANGENT RULE.              *)
  Theorem adj_pairing : forall e x c g v M, pbelow M e = true ->
    dot M (adj e x c g) v = dot M g v +! c *! tan e x v.
  Proof.
    induction e as [i|a|e1 IH1 e2 IH2|e1 IH1 e2 IH2]; intros x c g v M Hb;
      cbn [pbelow adj tan] in *.
    - apply dot_accum. apply Nat.ltb_lt. exact Hb.
    - ring.
    - apply andb_true_iff in Hb. destruct Hb as [H1 H2].
      rewrite (IH2 x c _ v M H2), (IH1 x c g v M H1). ring.
    - apply andb_true_iff in Hb. destruct Hb as [H1 H2].
      rewrite (IH2 x _ _ v M H2), (IH1 x _ g v M H1). ring.
  Qed.

  (* =================================================================== *)
  (* Part B.  Straight-line blocks (NLet lists): statement k binds       *)
  (* variable n + k and reads earlier variables only.                    *)
  (* =================================================================== *)

  Definition block : Type := list (pexp K).

  Fixpoint bscoped (n : nat) (b : block) : bool :=
    match b with
    | [] => true
    | e :: b' => pbelow n e && bscoped (S n) b'
    end.

  Fixpoint exec (n : nat) (b : block) (x : nat -> K) : nat -> K :=
    match b with
    | [] => x
    | e :: b' => exec (S n) b' (setv x n (Pev e x))
    end.

  (* synthesizeJvp: a tangent statement beside every primal statement.   *)
  Fixpoint texec (n : nat) (b : block) (x v : nat -> K) : nat -> K :=
    match b with
    | [] => v
    | e :: b' => texec (S n) b' (setv x n (Pev e x)) (setv v n (tan e x v))
    end.

  (* synthesizeRev: List.rev stmts |> adjointOfStmt, reading the forward *)
  (* values xf the primal pass left behind.                              *)
  Fixpoint rsweep (n : nat) (b : block) (xf : nat -> K) (g : nat -> K)
    : nat -> K :=
    match b with
    | [] => g
    | e :: b' => let g' := rsweep (S n) b' xf g in
                 adj e xf (g' n) (setv g' n k0)
    end.

  Lemma exec_below : forall b n x i, i < n -> exec n b x i = x i.
  Proof.
    induction b as [|e b IH]; intros n x i Hi; [reflexivity|].
    cbn [exec]. rewrite IH by lia. unfold setv.
    destruct (Nat.eqb_spec i n); [lia|reflexivity].
  Qed.

  (* REVERSE MODE IS THE TRANSPOSE OF FORWARD MODE, block by block.      *)
  Theorem sweep_pairing : forall b n x v g M, bscoped n b = true ->
    n + length b <= M ->
    dot M (rsweep n b (exec n b x) g) v = dot M g (texec n b x v).
  Proof.
    induction b as [|e b IH]; intros n x v g M Hs HM; [reflexivity|].
    cbn [bscoped length] in Hs, HM. apply andb_true_iff in Hs.
    destruct Hs as [He Hb]. cbn [rsweep exec texec].
    set (x1 := setv x n (Pev e x)). set (xf := exec (S n) b x1).
    set (g' := rsweep (S n) b xf g).
    rewrite (adj_pairing e xf (g' n) (setv g' n k0) v M
               (pbelow_mono n M e ltac:(lia) He)).
    rewrite (tan_below n e xf x v v He).
    - rewrite dot_consume by lia. unfold g', xf.
      apply IH; [exact Hb|lia].
    - intros i Hi. unfold xf. rewrite exec_below by lia. unfold x1, setv.
      destruct (Nat.eqb_spec i n); [lia|reflexivity].
    - reflexivity.
  Qed.

  (* =================================================================== *)
  (* Part C.  Vector maps and recurrences.  A map has `mo` outputs, each *)
  (* an expression over `mi` inputs.  A recurrence iterates a map with   *)
  (* mo = mi; its reverse mode replays the STORED TRAJECTORY backwards   *)
  (* -- the executed algorithm, never an implicit fixed point.           *)
  (* =================================================================== *)

  Definition vmap (Fs : nat -> pexp K) (x : nat -> K) : nat -> K :=
    fun j => Pev (Fs j) x.

  Definition vtan (Fs : nat -> pexp K) (x v : nat -> K) : nat -> K :=
    fun j => tan (Fs j) x v.

  Fixpoint vacc (Fs : nat -> pexp K) (x g : nat -> K) (k : nat)
      (acc : nat -> K) : nat -> K :=
    match k with
    | O => acc
    | S k' => vacc Fs x g k' (adj (Fs k') x (g k') acc)
    end.

  Definition vadj (Fs : nat -> pexp K) (mo : nat) (x g : nat -> K)
    : nat -> K :=
    vacc Fs x g mo (fun _ => k0).

  Lemma dot_zero : forall M v, dot M (fun _ => k0) v = k0.
  Proof.
    intros M v. unfold dot. apply (ksumf_zero K k0 k1 kadd kmul ksub kopp Kth).
    intros l _. ring.
  Qed.

  Lemma vacc_pairing : forall Fs x g mi v k acc,
    (forall j, j < k -> pbelow mi (Fs j) = true) ->
    dot mi (vacc Fs x g k acc) v = dot mi acc v +! dot k g (vtan Fs x v).
  Proof.
    intros Fs x g mi v. induction k as [|k IH]; intros acc Hb.
    - cbn [vacc]. change (dot 0 g (vtan Fs x v)) with k0. ring.
    - cbn [vacc]. rewrite IH by (intros j Hj; apply Hb; lia).
      rewrite adj_pairing by (apply Hb; lia).
      change (dot (S k) g (vtan Fs x v))
        with (dot k g (vtan Fs x v) +! g k *! tan (Fs k) x v).
      ring.
  Qed.

  Theorem vadj_pairing : forall Fs mi mo x g v,
    (forall j, j < mo -> pbelow mi (Fs j) = true) ->
    dot mi (vadj Fs mo x g) v = dot mo g (vtan Fs x v).
  Proof.
    intros Fs mi mo x g v Hb. unfold vadj.
    rewrite (vacc_pairing Fs x g mi v mo _ Hb), dot_zero. ring.
  Qed.

  (* T steps forward, with tangents.                                     *)
  Fixpoint frun (Fs : nat -> pexp K) (T : nat) (x : nat -> K) : nat -> K :=
    match T with O => x | S T' => frun Fs T' (vmap Fs x) end.

  Fixpoint trun (Fs : nat -> pexp K) (T : nat) (x v : nat -> K) : nat -> K :=
    match T with O => v | S T' => trun Fs T' (vmap Fs x) (vtan Fs x v) end.

  (* The tape: the states x_0 .. x_(T-1) the forward pass went through.  *)
  Fixpoint tape (Fs : nat -> pexp K) (T : nat) (x : nat -> K)
    : list (nat -> K) :=
    match T with O => [] | S T' => x :: tape Fs T' (vmap Fs x) end.

  (* The descending sweep: the LAST stored state is used FIRST.          *)
  Definition carry_sweep (Fs : nat -> pexp K) (m : nat)
      (tp : list (nat -> K)) (g : nat -> K) : nat -> K :=
    fold_right (fun xt acc => vadj Fs m xt acc) g tp.

  Theorem carry_pairing : forall Fs m T x v g,
    (forall j, j < m -> pbelow m (Fs j) = true) ->
    dot m (carry_sweep Fs m (tape Fs T x) g) v = dot m g (trun Fs T x v).
  Proof.
    intros Fs m. induction T as [|T IH]; intros x v g Hb; [reflexivity|].
    cbn [tape trun]. unfold carry_sweep. cbn [fold_right].
    fold (carry_sweep Fs m (tape Fs T (vmap Fs x)) g).
    rewrite (vadj_pairing Fs m m x _ v Hb). apply IH. exact Hb.
  Qed.

  (* =================================================================== *)
  (* Part D.  REDUCTION COMMUTES WITH BOTH MODES.  F on mx variables, a  *)
  (* summary q with mz coordinates, a reduced map G on those.  The       *)
  (* certificate q o F = G o q is assumed in the dual-number reading --  *)
  (* which is what a ring-generic certificate theorem provides.          *)
  (* =================================================================== *)

  Section Commute.
    Variables Fs qs Gs : nat -> pexp K.
    Variables mx mz : nat.
    Hypothesis HF : forall j, j < mx -> pbelow mx (Fs j) = true.
    Hypothesis Hq : forall j, j < mz -> pbelow mx (qs j) = true.
    Hypothesis HG : forall j, j < mz -> pbelow mz (Gs j) = true.
    Hypothesis Hcert : forall (rho : nat -> dual K) j, j < mz ->
      PevD (qs j) (fun i => PevD (Fs i) rho)
      = PevD (Gs j) (fun i => PevD (qs i) rho).

    Lemma dual_env : forall Es x v i,
      PevD (Es i) (Lift x v) = Lift (vmap Es x) (vtan Es x v) i.
    Proof. intros Es x v i. apply pevD_is_tan. Qed.

    Lemma cert_value : forall x j, j < mz ->
      Pev (qs j) (vmap Fs x) = Pev (Gs j) (vmap qs x).
    Proof.
      intros x j Hj.
      pose proof (f_equal fst (Hcert (Lift x (fun _ => k0)) j Hj)) as E.
      rewrite (pevD_ext K k0 kadd kmul (qs j) _
                 (Lift (vmap Fs x) (vtan Fs x (fun _ => k0)))) in E
        by (intro i; apply dual_env).
      rewrite (pevD_ext K k0 kadd kmul (Gs j) _
                 (Lift (vmap qs x) (vtan qs x (fun _ => k0)))) in E
        by (intro i; apply dual_env).
      rewrite !pevD_is_tan in E. exact E.
    Qed.

    (* The chain rule instance, with no chain rule: read the certificate *)
    (* in the dual numbers.                                              *)
    Lemma cert_tangent : forall x v j, j < mz ->
      tan (qs j) (vmap Fs x) (vtan Fs x v)
      = tan (Gs j) (vmap qs x) (vtan qs x v).
    Proof.
      intros x v j Hj.
      pose proof (f_equal snd (Hcert (Lift x v) j Hj)) as E.
      rewrite (pevD_ext K k0 kadd kmul (qs j) _
                 (Lift (vmap Fs x) (vtan Fs x v))) in E
        by (intro i; apply dual_env).
      rewrite (pevD_ext K k0 kadd kmul (Gs j) _
                 (Lift (vmap qs x) (vtan qs x v))) in E
        by (intro i; apply dual_env).
      rewrite !pevD_is_tan in E. exact E.
    Qed.

    (* FORWARD MODE.  Run jvp on the reduced program from the pushed     *)
    (* tangent: at every horizon it carries the pushed tangent of the    *)
    (* original run.                                                     *)
    Theorem reduction_commutes_forward : forall T x v j, j < mz ->
      frun Gs T (vmap qs x) j = vmap qs (frun Fs T x) j /\
      trun Gs T (vmap qs x) (vtan qs x v) j
      = vtan qs (frun Fs T x) (trun Fs T x v) j.
    Proof.
      induction T as [|T IH]; intros x v j Hj; [split; reflexivity|].
      cbn [frun trun]. destruct (IH (vmap Fs x) (vtan Fs x v) j Hj) as [I1 I2].
      assert (Ev : forall i, i < mz ->
                 vmap Gs (vmap qs x) i = vmap qs (vmap Fs x) i)
        by (intros i Hi; unfold vmap at 1 3; symmetry; apply cert_value;
            exact Hi).
      assert (Et : forall i, i < mz ->
                 vtan Gs (vmap qs x) (vtan qs x v) i
                 = vtan qs (vmap Fs x) (vtan Fs x v) i)
        by (intros i Hi; unfold vtan at 1 3; symmetry; apply cert_tangent;
            exact Hi).
      split.
      - rewrite <- I1. clear I1 I2. revert j Hj.
        generalize (vmap Gs (vmap qs x)) (vmap qs (vmap Fs x)) Ev.
        clear Ev Et IH. induction T as [|T IHT]; intros a b Hab j Hj.
        + apply Hab. exact Hj.
        + cbn [frun]. apply IHT; [|exact Hj]. intros i Hi. unfold vmap.
          apply (pev_below K kadd kmul mz); [apply HG; exact Hi|exact Hab].
      - rewrite <- I2. clear I1 I2. revert j Hj.
        generalize (vmap Gs (vmap qs x)) (vmap qs (vmap Fs x)) Ev
                   (vtan Gs (vmap qs x) (vtan qs x v))
                   (vtan qs (vmap Fs x) (vtan Fs x v)) Et.
        clear Ev Et IH. induction T as [|T IHT]; intros a b Hab s t Hst j Hj.
        + apply Hst. exact Hj.
        + cbn [trun]. apply IHT; [| |exact Hj].
          * intros i Hi. unfold vmap.
            apply (pev_below K kadd kmul mz); [apply HG; exact Hi|exact Hab].
          * intros i Hi. unfold vtan.
            apply (tan_below mz); [apply HG; exact Hi|exact Hab|exact Hst].
    Qed.

    Lemma dot_ext : forall M g g' v v',
      (forall i, i < M -> g i = g' i) -> (forall i, i < M -> v i = v' i) ->
      dot M g v = dot M g' v'.
    Proof.
      intros M g g' v v' Hg Hv. unfold dot. apply (ksumf_ext K k0 kadd).
      intros i Hi. rewrite (Hg i Hi), (Hv i Hi). reflexivity.
    Qed.

    Lemma dot_unit : forall M g i, i < M ->
      dot M g (unitR K k0 k1 i) = g i.
    Proof.
      intros M g i Hi. unfold dot.
      rewrite (ksumf_single K k0 k1 kadd kmul ksub kopp Kth _ M i Hi).
      - unfold unitR. rewrite Nat.eqb_refl. ring.
      - intros l Hl Hne. unfold unitR.
        destruct (Nat.eqb_spec l i); [contradiction|]. ring.
    Qed.

    (* REVERSE MODE.  Sweep the reduced program backwards from a summary *)
    (* cotangent h, then pull back through q at the INITIAL state; or    *)
    (* pull h back through q at the FINAL state, then sweep the original *)
    (* program backwards.  Same cotangent on every original variable.    *)
    Theorem reduction_commutes_reverse : forall T x h i, i < mx ->
      vadj qs mz x (carry_sweep Gs mz (tape Gs T (vmap qs x)) h) i
      = carry_sweep Fs mx (tape Fs T x) (vadj qs mz (frun Fs T x) h) i.
    Proof.
      intros T x h i Hi.
      rewrite <- (dot_unit mx _ i Hi).
      rewrite <- (dot_unit mx (carry_sweep Fs mx (tape Fs T x)
                                 (vadj qs mz (frun Fs T x) h)) i Hi).
      set (v := unitR K k0 k1 i).
      rewrite (vadj_pairing qs mx mz x _ v Hq).
      rewrite (carry_pairing Gs mz T (vmap qs x) (vtan qs x v) h HG).
      rewrite (carry_pairing Fs mx T x v _ HF).
      rewrite (vadj_pairing qs mx mz (frun Fs T x) h _ Hq).
      apply dot_ext; [reflexivity|].
      intros j Hj. apply reduction_commutes_forward. exact Hj.
    Qed.

  End Commute.

End EmittedRules.

(* ===================================================================== *)
(* Part E.  The compiled fragment.                                       *)
(* ===================================================================== *)

(* Forward mode, every extent: the generated program preserves the       *)
(* dual-number semantics of the source program -- the compiler theorem   *)
(* read in the ring of dual numbers, which is not a domain, so this is   *)
(* the form with nothing stripped.                                       *)
Section CompiledDual.
  Variable K : Type.
  Variables (k0 k1 : K) (kadd kmul ksub : K -> K -> K) (kopp : K -> K).
  Hypothesis Kth : ring_theory k0 k1 kadd kmul ksub kopp (@eq K).

  Local Notation DK := (dual K).
  Local Notation D0 := (d0 K k0).
  Local Notation D1 := (d1 K k0 k1).
  Local Notation Dadd := (dadd K kadd).
  Local Notation Dmul := (dmul K kadd kmul).
  Local Notation Dopp := (dopp K kopp).

  Theorem compile_sound_dual : forall P gs, wf P = true ->
    compile P = Some gs -> strip (p_n P) (p_co P) = p_co P ->
    forall N (th : nat -> DK) w (xs : list DK), length xs = N ->
      trace (sstep DK D0 D1 Dadd Dmul Dopp P th)
            (sobs DK D0 D1 Dadd Dmul Dopp P th) w xs
      = trace (rstep DK D0 D1 Dadd Dmul Dopp gs (p_r P) (p_m P) N th)
              (robs DK D0 D1 Dadd Dmul Dopp P N th) w
              (qr DK D0 D1 Dadd Dmul (p_r P) xs).
  Proof.
    intros P gs Hwf Hc Hs.
    apply (compile_sound_unstripped DK D0 D1 Dadd Dmul (dsub K ksub) Dopp
             (dual_ring_theory K k0 k1 kadd kmul ksub kopp Kth) P gs Hwf Hc Hs).
  Qed.

End CompiledDual.

(* Both modes on a concrete source program, with the ACTUAL compiler     *)
(* output as the reduced side: ex_affine, x' = (1 + S) x + theta, on     *)
(* three particles.  Variables 0 1 2 are the particles, 3 is theta; the  *)
(* summary is (S, Q, theta, N).                                          *)

Definition ex_S : pexp Z := PAdd (PVar 0) (PAdd (PVar 1) (PVar 2)).
Definition ex_Q : pexp Z :=
  PAdd (PMul (PVar 0) (PVar 0))
       (PAdd (PMul (PVar 1) (PVar 1)) (PMul (PVar 2) (PVar 2))).

Definition ex_Fs (i : nat) : pexp Z :=
  if Nat.ltb i 3
  then PAdd (PMul (PAdd (PConst 1%Z) ex_S) (PVar i)) (PVar 3)
  else PVar i.

Definition ex_qs (j : nat) : pexp Z :=
  match j with
  | 0 => ex_S
  | 1 => ex_Q
  | 2 => PVar 3
  | _ => PConst 3%Z
  end.

Definition ex_Gs (j : nat) : pexp Z :=
  match compile ex_affine with
  | Some gs => zmap Z 0%Z 1%Z Z.add Z.mul Z.opp (nth j gs (PVar j))
  | None => PVar j
  end.

(* What the compiler emitted, as closed syntax.                          *)
Definition ex_G0 : pexp Z := Eval vm_compute in ex_Gs 0.
Definition ex_G1 : pexp Z := Eval vm_compute in ex_Gs 1.

Lemma ex_Gs_cases :
  ex_Gs 0 = ex_G0 /\ ex_Gs 1 = ex_G1 /\ ex_Gs 2 = PVar 2 /\ ex_Gs 3 = PVar 3.
Proof. repeat split; vm_compute; reflexivity. Qed.

Lemma ex_certificate_dual : forall (rho : nat -> dual Z) j, j < 4 ->
  pevD Z 0%Z Z.add Z.mul (ex_qs j)
       (fun i => pevD Z 0%Z Z.add Z.mul (ex_Fs i) rho)
  = pevD Z 0%Z Z.add Z.mul (ex_Gs j)
         (fun i => pevD Z 0%Z Z.add Z.mul (ex_qs i) rho).
Proof.
  intros rho j Hj. destruct ex_Gs_cases as [G0 [G1 [G2 G3]]].
  destruct (rho 0) as [x0 v0] eqn:E0. destruct (rho 1) as [x1 v1] eqn:E1.
  destruct (rho 2) as [x2 v2] eqn:E2. destruct (rho 3) as [x3 v3] eqn:E3.
  destruct j as [|[|[|[|j]]]]; [| | | |lia].
  - rewrite G0. unfold ex_G0.
    cbn [pevD ex_qs ex_Fs ex_S ex_Q Nat.ltb Nat.leb].
    rewrite E0, E1, E2, E3. unfold dadd, dmul. cbn [fst snd]. f_equal; ring.
  - rewrite G1. unfold ex_G1.
    cbn [pevD ex_qs ex_Fs ex_S ex_Q Nat.ltb Nat.leb].
    rewrite E0, E1, E2, E3. unfold dadd, dmul. cbn [fst snd]. f_equal; ring.
  - rewrite G2. cbn [pevD ex_qs ex_Fs Nat.ltb Nat.leb]. reflexivity.
  - rewrite G3. cbn [pevD ex_qs]. reflexivity.
Qed.

(* Reverse mode: a gradient with respect to the three particles and      *)
(* theta, through T steps, computed by sweeping FOUR summary numbers.    *)
Theorem ex_affine_reverse_commutes : forall T x h i, i < 4 ->
  vadj Z 0%Z Z.add Z.mul ex_qs 4 x
       (carry_sweep Z 0%Z Z.add Z.mul ex_Gs 4
                    (tape Z Z.add Z.mul ex_Gs T (vmap Z Z.add Z.mul ex_qs x))
                    h) i
  = carry_sweep Z 0%Z Z.add Z.mul ex_Fs 4 (tape Z Z.add Z.mul ex_Fs T x)
                (vadj Z 0%Z Z.add Z.mul ex_qs 4
                      (frun Z Z.add Z.mul ex_Fs T x) h) i.
Proof.
  intros T x h i Hi.
  apply (reduction_commutes_reverse Z 0%Z 1%Z Z.add Z.mul Z.sub Z.opp Zth
           ex_Fs ex_qs ex_Gs 4 4).
  - intros j Hj. destruct j as [|[|[|[|j]]]]; [| | | |lia]; reflexivity.
  - intros j Hj. destruct j as [|[|[|[|j]]]]; [| | | |lia]; reflexivity.
  - intros j Hj. destruct j as [|[|[|[|j]]]]; [| | | |lia]; reflexivity.
  - exact ex_certificate_dual.
  - exact Hi.
Qed.
