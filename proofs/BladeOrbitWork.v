(* ===================================================================== *)
(* BladeOrbitWork.v -- UNIFORM OPTIMALITY, pipelines: the orbit work of  *)
(* a composed computation (docs/plans/plan-uniform-optimality.md, T3).   *)
(*                                                                       *)
(* BladeOptimal bounds one nest.  Here the output cells are TERMS over   *)
(* data leaves and several kernel symbols, some declared commutative,    *)
(* and every symbol is an oracle.  Perturbing one kernel now changes the *)
(* arguments every downstream kernel sees, so the one-nest adversary     *)
(* does not transfer; the proof runs in a FREE MODEL instead:            *)
(*                                                                       *)
(*   kfree                    the oracle that is a perfect hash of its   *)
(*                            canonical query.  In it a value is the     *)
(*                            name of a subterm class, so distinct       *)
(*                            classes are distinct queries.              *)
(*   min_bad                  a class the program never asked about has  *)
(*                            a MINIMAL unasked subterm beneath it.      *)
(*   below_same, root_fresh   moving the oracle on that one class leaves *)
(*                            everything below unchanged and sends the   *)
(*                            subterm itself to a spare value;           *)
(*   taint_up,                injectivity then carries the change to the *)
(*   clean_not_taint          root: a tainted argument taints the        *)
(*                            result, and no free-model value is         *)
(*                            tainted.                                   *)
(*   orbit_work_lower_bound   so a program correct for every oracle in   *)
(*                            the law class asks at least as many        *)
(*                            questions as its targets have distinct     *)
(*                            subterm classes modulo the laws.           *)
(*                                                                       *)
(*   memo_prog                the attaining program: evaluate bottom-up, *)
(*                            look the CANONICAL query up before asking. *)
(*   memo_prog_correct/_cost  correct for every oracle in the class, and *)
(*                            at most one question per class, always;    *)
(*   orbit_work_attained,     exactly one per class in the free model,   *)
(*   uniform_optimality_      hence beaten by no correct program.        *)
(*     pipeline                                                          *)
(*                                                                       *)
(* The count is the size of the maximally shared term DAG modulo the     *)
(* laws.  For an index-parametrized schema that is the ORBIT WORK        *)
(* FORMULA  W = sum over nodes v of omega(G_v, X_v)  -- each node        *)
(* counted over its own support, modulo its own group -- and the         *)
(* attaining program is dimensional currying read as a cost statement.   *)
(* Checked on the two-node pipeline Out(i,j) = g(h(A i), h(A j)), g      *)
(* declared commutative:                                                 *)
(*                                                                       *)
(*   two_node_orbit_work      W = n + C(n+1, 2): node h over {i} with no *)
(*                            symmetry, node g over {i, j} modulo S_2;   *)
(*   two_node_fused_work      the fused dense nest meets 3 n^2 class     *)
(*                            occurrences -- the support gap and the     *)
(*                            group gap, both visible in one number;     *)
(*   two_node_optimal         forced and attained.                       *)
(*                                                                       *)
(* orbit_work_nat / two_node_orbit_work_nat close every hypothesis over  *)
(* nat (leaves 3i, spare value 2, hash 3*code+1 by Cantor pairing), and  *)
(* two_node_computed is a vm_compute pin at n = 3: 9 questions, 27       *)
(* occurrences.                                                          *)
(*                                                                       *)
(* Scope, stated once.  Kernel symbols are unary or binary and the only  *)
(* law is commutativity of a declared binary symbol; arity r and general *)
(* H are BladeOptimal's subject, one nest at a time.  FOLDS ARE NOT      *)
(* MODELLED: sharing partial sums across overlapping fibers is Ensemble  *)
(* Computation, NP-complete, and outside any theorem of this shape.  The *)
(* bound is on queries in the free model (generic data and kernels);     *)
(* against a degenerate oracle a value-inspecting program can do better, *)
(* as BladeOptimal's nongeneric_data_beats_bound already shows.  The     *)
(* general formula W = sum omega(G_v, X_v) over an arbitrary schema is   *)
(* prose; this file proves the term form and one instance.               *)
(*                                                                       *)
(* Imports BladeDMWF, BladeBinomial, BladeOptimal.  Coq 8.18, stdlib     *)
(* only.                                                                 *)
(* ===================================================================== *)

From Blade Require Import BladeDMWF BladeBinomial BladeOptimal.
Require Import List Arith Lia Cantor.
Import ListNotations.

(* ===================================================================== *)
(* TERMS AND QUERIES.                                                    *)
(* A pipeline's output cells are terms over data leaves and kernel       *)
(* symbols (unary and binary here; see the header for why that is the    *)
(* honest scope).  A query is one kernel application to VALUES.  Sharing *)
(* is not part of the syntax: a DAG is its unfolding, and what the       *)
(* theorem counts is the distinct subterm classes -- so a DAG's sharing, *)
(* and any sharing the laws add, is counted once automatically.          *)
(* ===================================================================== *)

Inductive tm : Type :=
| Leaf : nat -> tm
| App1 : nat -> tm -> tm
| App2 : nat -> tm -> tm -> tm.

Inductive qry : Type :=
| Q1 : nat -> nat -> qry
| Q2 : nat -> nat -> nat -> qry.

Definition qry_dec : forall q q' : qry, {q = q'} + {q <> q'}.
Proof. decide equality; apply Nat.eq_dec. Defined.

Inductive sub (u : tm) : tm -> Prop :=
| sub_refl : sub u u
| sub_1  : forall s t1, sub u t1 -> sub u (App1 s t1)
| sub_2l : forall s t1 t2, sub u t1 -> sub u (App2 s t1 t2)
| sub_2r : forall s t1 t2, sub u t2 -> sub u (App2 s t1 t2).

Lemma map_eq_in : forall (A B : Type) (f g : A -> B) l,
  map f l = map g l -> forall x, In x l -> f x = g x.
Proof.
  intros A B f g l. induction l as [|a l IH]; intros E x Hx; simpl in E.
  - destruct Hx.
  - injection E as E1 E2. destruct Hx as [-> | Hx]; [exact E1 | exact (IH E2 x Hx)].
Qed.

Section OrbitWork.
  Variable comm : nat -> bool.   (* which binary symbols are declared comm *)
  Variable leaf : nat -> nat.    (* the data *)

  (* the canonical form of a query under the declared laws *)
  Definition cq (q : qry) : qry :=
    match q with
    | Q1 s a => Q1 s a
    | Q2 s a b => if comm s
                  then (if a <=? b then Q2 s a b else Q2 s b a)
                  else Q2 s a b
    end.

  Lemma cq_shape : forall s a b, exists x y,
    cq (Q2 s a b) = Q2 s x y /\ ((x = a /\ y = b) \/ (x = b /\ y = a)).
  Proof.
    intros s a b. unfold cq. destruct (comm s); [destruct (a <=? b) |]; eauto 6.
  Qed.

  Lemma cq_swap : forall s a b, comm s = true ->
    cq (Q2 s a b) = cq (Q2 s b a).
  Proof.
    intros s a b H. unfold cq. rewrite H.
    destruct (Nat.leb_spec a b), (Nat.leb_spec b a);
      try lia; try reflexivity.
    assert (a = b) by lia. subst. reflexivity.
  Qed.

  (* THE LAW CLASS: the declared symbols really are commutative. *)
  Definition Kc (k : qry -> nat) : Prop :=
    forall s a b, comm s = true -> k (Q2 s a b) = k (Q2 s b a).

  Lemma Kc_cq : forall k, Kc k -> forall q, k (cq q) = k q.
  Proof.
    intros k Hk [s a | s a b]; simpl; [reflexivity |].
    destruct (comm s) eqn:E; [| reflexivity].
    destruct (a <=? b); [reflexivity | apply Hk; exact E].
  Qed.

  Fixpoint eval (k : qry -> nat) (t : tm) : nat :=
    match t with
    | Leaf i => leaf i
    | App1 s t1 => k (Q1 s (eval k t1))
    | App2 s t1 t2 => k (Q2 s (eval k t1) (eval k t2))
    end.

  (* the canonical queries an evaluation of t meets, root first *)
  Fixpoint classes (k : qry -> nat) (t : tm) : list qry :=
    match t with
    | Leaf _ => []
    | App1 s t1 => cq (Q1 s (eval k t1)) :: classes k t1
    | App2 s t1 t2 =>
        cq (Q2 s (eval k t1) (eval k t2)) :: classes k t1 ++ classes k t2
    end.

  Definition rootc (k : qry -> nat) (t : tm) : option qry :=
    match t with
    | Leaf _ => None
    | App1 s t1 => Some (cq (Q1 s (eval k t1)))
    | App2 s t1 t2 => Some (cq (Q2 s (eval k t1) (eval k t2)))
    end.

  Definition kidc (k : qry -> nat) (t : tm) : list qry :=
    match t with
    | Leaf _ => []
    | App1 _ t1 => classes k t1
    | App2 _ t1 t2 => classes k t1 ++ classes k t2
    end.

  Definition all_classes (k : qry -> nat) (ts : list tm) : list qry :=
    flat_map (classes k) ts.

  (* =================================================================== *)
  (* ATTAINMENT.  The memoized evaluator: evaluate bottom-up, and before *)
  (* asking, look the CANONICAL query up in what has been asked.  This   *)
  (* is every intermediate materialized once, over exactly the values it *)
  (* depends on, modulo its own symmetry.                                *)
  (* =================================================================== *)
  Definition memo := list (qry * nat).

  Fixpoint lookup (q : qry) (m : memo) : option nat :=
    match m with
    | [] => None
    | (q', v) :: m' => if qry_dec q q' then Some v else lookup q m'
    end.

  Lemma lookup_some : forall q m v, lookup q m = Some v -> In (q, v) m.
  Proof.
    intros q m v. induction m as [|[q' v'] m IH]; simpl; intro H.
    - discriminate.
    - destruct (qry_dec q q') as [E | _].
      + injection H as H. subst. left. reflexivity.
      + right. exact (IH H).
  Qed.

  Lemma lookup_none : forall q m, lookup q m = None -> ~ In q (map fst m).
  Proof.
    intros q m. induction m as [|[q' v'] m IH]; simpl; intro H.
    - intros [].
    - destruct (qry_dec q q') as [E | N]; [discriminate |].
      intros [E | Hin]; [apply N; symmetry; exact E | exact (IH H Hin)].
  Qed.

  Definition askM {O : Type} (q : qry) (m : memo)
    (cont : nat -> memo -> prog qry nat O) : prog qry nat O :=
    match lookup (cq q) m with
    | Some v => cont v m
    | None => Ask (cq q) (fun v => cont v ((cq q, v) :: m))
    end.

  Fixpoint evalM {O : Type} (t : tm) (m : memo)
    (cont : nat -> memo -> prog qry nat O) : prog qry nat O :=
    match t with
    | Leaf i => cont (leaf i) m
    | App1 s t1 => evalM t1 m (fun v1 m1 => askM (Q1 s v1) m1 cont)
    | App2 s t1 t2 =>
        evalM t1 m (fun v1 m1 =>
        evalM t2 m1 (fun v2 m2 => askM (Q2 s v1 v2) m2 cont))
    end.

  Fixpoint evalAll (ts : list tm) (m : memo) (acc : list nat)
    : prog qry nat (list nat) :=
    match ts with
    | [] => Ret (rev acc)
    | t :: ts' => evalM t m (fun v m' => evalAll ts' m' (v :: acc))
    end.

  Definition memo_prog (targets : list tm) : prog qry nat (list nat) :=
    evalAll targets [] [].

  Section MemoSpec.
    Variable k : qry -> nat.
    Hypothesis Hk : Kc k.

    Definition mok (m : memo) : Prop := forall q v, In (q, v) m -> k q = v.

    Lemma askM_spec : forall (O : Type) q m
      (cont : nat -> memo -> prog qry nat O),
      mok m -> NoDup (map fst m) ->
      exists m' newq,
        mok m' /\ NoDup (map fst m') /\
        map fst m' = rev newq ++ map fst m /\
        incl newq [cq q] /\
        run (askM q m cont) k = run (cont (k q) m') k /\
        trace (askM q m cont) k = newq ++ trace (cont (k q) m') k.
    Proof.
      intros O q m cont Hok Hnd. unfold askM.
      destruct (lookup (cq q) m) as [v |] eqn:E.
      - apply lookup_some in E. apply Hok in E.
        rewrite (Kc_cq k Hk q) in E. subst v.
        exists m, []. simpl. repeat split; auto. intros x [].
      - exists ((cq q, k q) :: m), [cq q]. cbn [run trace].
        rewrite (Kc_cq k Hk q). simpl. repeat split.
        + intros q' v [E' | Hin].
          * injection E' as E1 E2. subst. apply (Kc_cq k Hk).
          * exact (Hok q' v Hin).
        + constructor; [apply lookup_none; exact E | exact Hnd].
        + intros x Hx. exact Hx.
    Qed.

    Lemma evalM_spec : forall t (O : Type)
      (cont : nat -> memo -> prog qry nat O) m,
      mok m -> NoDup (map fst m) ->
      exists m' newq,
        mok m' /\ NoDup (map fst m') /\
        map fst m' = rev newq ++ map fst m /\
        incl newq (classes k t) /\
        run (evalM t m cont) k = run (cont (eval k t) m') k /\
        trace (evalM t m cont) k = newq ++ trace (cont (eval k t) m') k.
    Proof.
      induction t as [i | s t1 IH1 | s t1 IH1 t2 IH2];
        intros O cont m Hok Hnd; cbn [evalM eval classes].
      - exists m, []. simpl. repeat split; auto. intros x [].
      - destruct (IH1 O (fun v1 m1 => askM (Q1 s v1) m1 cont) m Hok Hnd)
          as (m1 & n1 & Hok1 & Hnd1 & Hk1 & Hin1 & Hr1 & Ht1).
        destruct (askM_spec O (Q1 s (eval k t1)) m1 cont Hok1 Hnd1)
          as (m2 & n2 & Hok2 & Hnd2 & Hk2 & Hin2 & Hr2 & Ht2).
        exists m2, (n1 ++ n2). repeat split; auto.
        + rewrite Hk2, Hk1, rev_app_distr, app_assoc. reflexivity.
        + intros x Hx. apply in_app_or in Hx as [Hx | Hx].
          * right. exact (Hin1 x Hx).
          * left. destruct (Hin2 x Hx) as [Ex | []]. exact Ex.
        + rewrite Hr1. exact Hr2.
        + rewrite Ht1. cbn beta. rewrite Ht2. rewrite app_assoc. reflexivity.
      - destruct (IH1 O (fun v1 m1 =>
                     evalM t2 m1 (fun v2 m2 => askM (Q2 s v1 v2) m2 cont))
                    m Hok Hnd)
          as (m1 & n1 & Hok1 & Hnd1 & Hk1 & Hin1 & Hr1 & Ht1).
        destruct (IH2 O (fun v2 m2 => askM (Q2 s (eval k t1) v2) m2 cont)
                    m1 Hok1 Hnd1)
          as (m2 & n2 & Hok2 & Hnd2 & Hk2 & Hin2 & Hr2 & Ht2).
        destruct (askM_spec O (Q2 s (eval k t1) (eval k t2)) m2 cont Hok2 Hnd2)
          as (m3 & n3 & Hok3 & Hnd3 & Hk3 & Hin3 & Hr3 & Ht3).
        exists m3, (n1 ++ n2 ++ n3). repeat split; auto.
        + rewrite Hk3, Hk2, Hk1, !rev_app_distr, !app_assoc. reflexivity.
        + intros x Hx. apply in_app_or in Hx as [Hx | Hx].
          * right. apply in_or_app. left. exact (Hin1 x Hx).
          * apply in_app_or in Hx as [Hx | Hx].
            -- right. apply in_or_app. right. exact (Hin2 x Hx).
            -- left. destruct (Hin3 x Hx) as [Ex | []]. exact Ex.
        + rewrite Hr1. cbn beta. rewrite Hr2. exact Hr3.
        + rewrite Ht1. cbn beta. rewrite Ht2. cbn beta. rewrite Ht3.
          rewrite !app_assoc. reflexivity.
    Qed.

    Lemma evalAll_spec : forall ts m acc,
      mok m -> NoDup (map fst m) ->
      run (evalAll ts m acc) k = rev acc ++ map (eval k) ts /\
      NoDup (rev (trace (evalAll ts m acc) k) ++ map fst m) /\
      incl (trace (evalAll ts m acc) k) (all_classes k ts).
    Proof.
      induction ts as [|t ts IH]; intros m acc Hok Hnd; cbn [evalAll].
      - simpl. rewrite app_nil_r. repeat split; auto. intros x [].
      - destruct (evalM_spec t (list nat)
                    (fun v m' => evalAll ts m' (v :: acc)) m Hok Hnd)
          as (m1 & n1 & Hok1 & Hnd1 & Hk1 & Hin1 & Hr1 & Ht1).
        destruct (IH m1 (eval k t :: acc) Hok1 Hnd1) as (Hr2 & Hnd2 & Hin2).
        rewrite Hr1, Ht1. cbn beta. repeat split.
        + rewrite Hr2. simpl. rewrite <- app_assoc. reflexivity.
        + rewrite rev_app_distr, <- app_assoc, <- Hk1. exact Hnd2.
        + intros x Hx. unfold all_classes. cbn [flat_map].
          apply in_or_app. apply in_app_or in Hx as [Hx | Hx].
          * left. exact (Hin1 x Hx).
          * right. exact (Hin2 x Hx).
    Qed.

    (* the memoized evaluator is correct ... *)
    Theorem memo_prog_correct : forall targets,
      run (memo_prog targets) k = map (eval k) targets.
    Proof.
      intro targets. unfold memo_prog.
      destruct (evalAll_spec targets [] []) as (Hr & _ & _);
        [intros q v [] | constructor | exact Hr].
    Qed.

    (* ... and asks each class at most once. *)
    Theorem memo_prog_cost : forall targets,
      length (trace (memo_prog targets) k)
        <= length (nodup qry_dec (all_classes k targets)).
    Proof.
      intro targets. unfold memo_prog.
      destruct (evalAll_spec targets [] []) as (_ & Hnd & Hin);
        [intros q v [] | constructor |].
      simpl in Hnd. rewrite app_nil_r in Hnd.
      apply NoDup_incl_length.
      - apply NoDup_rev in Hnd. rewrite rev_involutive in Hnd. exact Hnd.
      - intros x Hx. apply nodup_In. exact (Hin x Hx).
    Qed.
  End MemoSpec.

  (* =================================================================== *)
  (* THE LOWER BOUND.                                                    *)
  (* The free model: an oracle that is a perfect hash of its canonical   *)
  (* query, with a range that misses the data and one spare value.  In   *)
  (* it, a value IS the name of a subterm class, so distinct classes are *)
  (* distinct queries.                                                   *)
  (* =================================================================== *)
  Variable hash : qry -> nat.
  Variable fresh : nat.
  Hypothesis hash_inj : forall q q', hash q = hash q' -> q = q'.
  Hypothesis hash_leaf : forall q i, hash q <> leaf i.
  Hypothesis fresh_leaf : forall i, fresh <> leaf i.
  Hypothesis fresh_hash : forall q, fresh <> hash q.

  Definition kfree (q : qry) : nat := hash (cq q).

  Lemma kfree_Kc : Kc kfree.
  Proof. intros s a b H. unfold kfree. rewrite (cq_swap s a b H). reflexivity. Qed.

  (* the adversary: the free oracle, moved to the spare value on one class *)
  Definition kp (c0 : qry) (q : qry) : nat :=
    if qry_dec (cq q) c0 then fresh else kfree q.

  Lemma kp_Kc : forall c0, Kc (kp c0).
  Proof.
    intros c0 s a b H. unfold kp. rewrite (cq_swap s a b H).
    destruct (qry_dec (cq (Q2 s b a)) c0); [reflexivity | apply kfree_Kc; exact H].
  Qed.

  (* below the perturbed class nothing changes *)
  Lemma below_same : forall c0 x,
    ~ In c0 (classes kfree x) -> eval (kp c0) x = eval kfree x.
  Proof.
    intros c0 x. induction x as [i | s x1 IH1 | s x1 IH1 x2 IH2];
      intro Hn; cbn [eval classes] in *.
    - reflexivity.
    - rewrite IH1 by (intro H; apply Hn; right; exact H).
      unfold kp at 1.
      destruct (qry_dec (cq (Q1 s (eval kfree x1))) c0) as [E | _];
        [exfalso; apply Hn; left; exact E | reflexivity].
    - rewrite IH1 by (intro H; apply Hn; right; apply in_or_app; left; exact H).
      rewrite IH2 by (intro H; apply Hn; right; apply in_or_app; right; exact H).
      unfold kp at 1.
      destruct (qry_dec (cq (Q2 s (eval kfree x1) (eval kfree x2))) c0)
        as [E | _];
        [exfalso; apply Hn; left; exact E | reflexivity].
  Qed.

  (* at it, the value becomes the spare one *)
  Lemma root_fresh : forall c0 u,
    rootc kfree u = Some c0 -> ~ In c0 (kidc kfree u) ->
    eval (kp c0) u = fresh.
  Proof.
    intros c0 u Hr Hn. destruct u as [i | s u1 | s u1 u2];
      cbn [rootc kidc eval] in *.
    - discriminate.
    - injection Hr as Hr. rewrite below_same by exact Hn.
      unfold kp. destruct (qry_dec (cq (Q1 s (eval kfree u1))) c0);
        [reflexivity | contradiction].
    - injection Hr as Hr.
      rewrite below_same by (intro H; apply Hn; apply in_or_app; left; exact H).
      rewrite below_same by (intro H; apply Hn; apply in_or_app; right; exact H).
      unfold kp.
      destruct (qry_dec (cq (Q2 s (eval kfree u1) (eval kfree u2))) c0);
        [reflexivity | contradiction].
  Qed.

  (* above it, the change cannot be hidden: the hash is injective, so a  *)
  (* tainted argument taints the result.                                 *)
  Inductive taint : nat -> Prop :=
  | taint_fresh : taint fresh
  | taint_1  : forall s a, taint a -> taint (hash (cq (Q1 s a)))
  | taint_2l : forall s a b, taint a -> taint (hash (cq (Q2 s a b)))
  | taint_2r : forall s a b, taint b -> taint (hash (cq (Q2 s a b))).

  Inductive clean : nat -> Prop :=
  | clean_leaf : forall i, clean (leaf i)
  | clean_1 : forall s a, clean a -> clean (hash (cq (Q1 s a)))
  | clean_2 : forall s a b, clean a -> clean b -> clean (hash (cq (Q2 s a b))).

  Lemma clean_eval : forall t, clean (eval kfree t).
  Proof.
    induction t as [i | s t1 IH1 | s t1 IH1 t2 IH2]; cbn [eval]; unfold kfree.
    - apply clean_leaf.
    - apply clean_1. exact IH1.
    - apply clean_2; assumption.
  Qed.

  Lemma taint_up : forall c0 u t, sub u t ->
    eval (kp c0) u = fresh -> taint (eval (kp c0) t).
  Proof.
    intros c0 u t Hs Hu. induction Hs as [| s t1 _ IH | s t1 t2 _ IH | s t1 t2 _ IH].
    - rewrite Hu. apply taint_fresh.
    - cbn [eval]. unfold kp at 1.
      destruct (qry_dec _ c0); [apply taint_fresh |].
      unfold kfree. apply taint_1. exact IH.
    - cbn [eval]. unfold kp at 1.
      destruct (qry_dec _ c0); [apply taint_fresh |].
      unfold kfree. apply taint_2l. exact IH.
    - cbn [eval]. unfold kp at 1.
      destruct (qry_dec _ c0); [apply taint_fresh |].
      unfold kfree. apply taint_2r. exact IH.
  Qed.

  Lemma taint_inv : forall v, taint v ->
    v = fresh \/
    (exists s a, taint a /\ v = hash (cq (Q1 s a))) \/
    (exists s a b, (taint a \/ taint b) /\ v = hash (cq (Q2 s a b))).
  Proof.
    intros v H. destruct H as [| s a H | s a b H | s a b H].
    - left. reflexivity.
    - right. left. exists s, a. split; [exact H | reflexivity].
    - right. right. exists s, a, b. split; [left; exact H | reflexivity].
    - right. right. exists s, a, b. split; [right; exact H | reflexivity].
  Qed.

  Lemma cq_Q2_not_Q1 : forall s a b s' a', cq (Q2 s a b) <> Q1 s' a'.
  Proof.
    intros s a b s' a' E.
    destruct (cq_shape s a b) as (x & y & Ex & _).
    rewrite Ex in E. discriminate.
  Qed.

  Lemma cq_Q2_eq : forall s a b s' a' b',
    cq (Q2 s a b) = cq (Q2 s' a' b') ->
    (a' = a /\ b' = b) \/ (a' = b /\ b' = a).
  Proof.
    intros s a b s' a' b' E.
    destruct (cq_shape s a b) as (x & y & Ex & Hxy).
    destruct (cq_shape s' a' b') as (x' & y' & Ex' & Hxy').
    rewrite Ex, Ex' in E. injection E as _ E1 E2. subst x' y'.
    destruct Hxy as [[? ?] | [? ?]], Hxy' as [[? ?] | [? ?]]; subst; auto.
  Qed.

  Lemma clean_not_taint : forall v, clean v -> taint v -> False.
  Proof.
    intros v Hc. induction Hc as [i | s a Ha IHa | s a b Ha IHa Hb IHb];
      intro Ht; apply taint_inv in Ht;
      destruct Ht as [E | [(s' & a' & Ht' & E) | (s' & a' & b' & Ht' & E)]].
    - exact (fresh_leaf i (eq_sym E)).
    - exact (hash_leaf _ i (eq_sym E)).
    - exact (hash_leaf _ i (eq_sym E)).
    - exact (fresh_hash _ (eq_sym E)).
    - apply hash_inj in E. cbn [cq] in E. injection E as _ E. subst a'.
      exact (IHa Ht').
    - apply hash_inj in E. symmetry in E. exact (cq_Q2_not_Q1 _ _ _ _ _ E).
    - exact (fresh_hash _ (eq_sym E)).
    - apply hash_inj in E. exact (cq_Q2_not_Q1 _ _ _ _ _ E).
    - apply hash_inj in E. apply cq_Q2_eq in E as [[Ea Eb] | [Ea Eb]];
        subst; destruct Ht' as [Ht' | Ht'];
        first [exact (IHa Ht') | exact (IHb Ht')].
  Qed.

  (* every term either has all its classes in S, or contains a MINIMAL   *)
  (* subterm whose class escapes S while everything below it is in S     *)
  Lemma min_bad : forall (S : list qry) t,
    (forall c, In c (classes kfree t) -> In c S) \/
    (exists u c0, sub u t /\ rootc kfree u = Some c0 /\ ~ In c0 S /\
                  forall c, In c (kidc kfree u) -> In c S).
  Proof.
    intros S t. induction t as [i | s t1 IH1 | s t1 IH1 t2 IH2].
    - left. intros c [].
    - destruct IH1 as [G1 | (u & c0 & Hs & Hr & Hn & Hk)].
      + destruct (in_dec qry_dec (cq (Q1 s (eval kfree t1))) S) as [Hi | Hni].
        * left. intros c [E | Hc]; [subst; exact Hi | exact (G1 c Hc)].
        * right. exists (App1 s t1), (cq (Q1 s (eval kfree t1))).
          repeat split; [apply sub_refl | exact Hni | exact G1].
      + right. exists u, c0. repeat split; try assumption. apply sub_1. exact Hs.
    - destruct IH1 as [G1 | (u & c0 & Hs & Hr & Hn & Hk)].
      + destruct IH2 as [G2 | (u & c0 & Hs & Hr & Hn & Hk)].
        * destruct (in_dec qry_dec
                      (cq (Q2 s (eval kfree t1) (eval kfree t2))) S)
            as [Hi | Hni].
          -- left. intros c [E | Hc]; [subst; exact Hi |].
             apply in_app_or in Hc as [Hc | Hc]; [exact (G1 c Hc) | exact (G2 c Hc)].
          -- right. exists (App2 s t1 t2),
                           (cq (Q2 s (eval kfree t1) (eval kfree t2))).
             repeat split; [apply sub_refl | exact Hni |].
             intros c Hc. cbn [kidc] in Hc.
             apply in_app_or in Hc as [Hc | Hc]; [exact (G1 c Hc) | exact (G2 c Hc)].
        * right. exists u, c0. repeat split; try assumption. apply sub_2r. exact Hs.
      + right. exists u, c0. repeat split; try assumption. apply sub_2l. exact Hs.
  Qed.

  (* ------------------------------------------------------------------ *)
  (* T3, lower bound: a program correct for every oracle in the law      *)
  (* class asks at least as many questions as there are distinct subterm *)
  (* classes among its targets.                                          *)
  (* ------------------------------------------------------------------ *)
  Theorem orbit_work_lower_bound :
    forall (targets : list tm) (P : prog qry nat (list nat)),
    (forall k, Kc k -> run P k = map (eval k) targets) ->
    length (nodup qry_dec (all_classes kfree targets))
      <= length (trace P kfree).
  Proof.
    intros targets P Hcor.
    rewrite <- (map_length cq (trace P kfree)).
    apply NoDup_incl_length; [apply NoDup_nodup |].
    intros c Hc. apply nodup_In in Hc.
    destruct (in_dec qry_dec c (map cq (trace P kfree))) as [Hi | Hni];
      [exact Hi | exfalso].
    unfold all_classes in Hc. apply in_flat_map in Hc as (t & Ht & Hct).
    destruct (min_bad (map cq (trace P kfree)) t)
      as [G | (u & c0 & Hs & Hr & Hn & Hk)].
    - exact (Hni (G c Hct)).
    - assert (Hag : forall q, In q (trace P kfree) -> kp c0 q = kfree q).
      { intros q Hq. unfold kp.
        destruct (qry_dec (cq q) c0) as [E | _]; [| reflexivity].
        exfalso. apply Hn. rewrite <- E. apply in_map. exact Hq. }
      destruct (run_agree _ _ _ P kfree (kp c0) Hag) as [Hrun _].
      rewrite (Hcor (kp c0) (kp_Kc c0)), (Hcor kfree kfree_Kc) in Hrun.
      pose proof (map_eq_in _ _ _ _ _ Hrun t Ht) as Et.
      assert (Hu : eval (kp c0) u = fresh).
      { apply root_fresh; [exact Hr |]. intro H. exact (Hn (Hk c0 H)). }
      apply (clean_not_taint (eval kfree t)); [apply clean_eval |].
      rewrite <- Et. exact (taint_up c0 u t Hs Hu).
  Qed.
  (* T3, attainment: in the free model the memoized evaluator asks       *)
  (* EXACTLY one question per class ...                                  *)
  Theorem orbit_work_attained : forall targets,
    length (trace (memo_prog targets) kfree)
      = length (nodup qry_dec (all_classes kfree targets)).
  Proof.
    intro targets. apply Nat.le_antisymm.
    - apply memo_prog_cost. exact kfree_Kc.
    - apply orbit_work_lower_bound.
      intros k Hk. apply memo_prog_correct. exact Hk.
  Qed.

  (* ... so no correct program beats it.  UNIFORM OPTIMALITY, pipelines. *)
  Corollary uniform_optimality_pipeline :
    forall (targets : list tm) (P : prog qry nat (list nat)),
    (forall k, Kc k -> run P k = map (eval k) targets) ->
    length (trace (memo_prog targets) kfree) <= length (trace P kfree).
  Proof.
    intros targets P Hcor. rewrite orbit_work_attained.
    exact (orbit_work_lower_bound targets P Hcor).
  Qed.

  (* =================================================================== *)
  (* THE ORBIT WORK FORMULA, on the two-node pipeline                    *)
  (*     Out(i, j) = g(h(A(i)), h(A(j))),   g declared commutative.      *)
  (* Node h has support {i} and no symmetry: n classes.  Node g has      *)
  (* support {i, j} and S_2: C(n+1, 2) classes.  The count is their SUM, *)
  (* although the DENSE output has n^2 cells and the fused nest asks     *)
  (* 3 n^2 questions.                                                    *)
  (* =================================================================== *)
  Hypothesis leaf_inj : forall i j, leaf i = leaf j -> i = j.
  Variables sh sg : nat.
  Hypothesis sg_comm : comm sg = true.

  Definition cell (ij : nat * nat) : tm :=
    App2 sg (App1 sh (Leaf (fst ij))) (App1 sh (Leaf (snd ij))).

  Definition dense (n : nat) : list (nat * nat) :=
    list_prod (seq 0 n) (seq 0 n).

  Definition targets2 (n : nat) : list tm := map cell (dense n).

  Definition hval (i : nat) : nat := hash (Q1 sh (leaf i)).

  Lemma hval_inj : forall i j, hval i = hval j -> i = j.
  Proof.
    intros i j E. unfold hval in E. apply hash_inj in E.
    injection E as E. exact (leaf_inj i j E).
  Qed.

  Lemma classes_cell : forall i j,
    classes kfree (cell (i, j))
    = [cq (Q2 sg (hval i) (hval j)); Q1 sh (leaf i); Q1 sh (leaf j)].
  Proof. intros i j. reflexivity. Qed.

  (* the explicit list of classes: node h's, then node g's *)
  Definition hnode (n : nat) : list qry :=
    map (fun i => Q1 sh (leaf i)) (seq 0 n).

  Definition gnode (n : nat) : list qry :=
    map (fun t => cq (Q2 sg (hval (nth 0 t 0)) (hval (nth 1 t 0))))
        (enum 2 0 n).

  Lemma canonical2 : forall n t, canonical 2 0 n t ->
    exists i j, t = [i; j] /\ i <= j /\ j < n.
  Proof.
    intros n t H. destruct t as [|i [|j [|x t]]]; simpl in H; try tauto.
    exists i, j. repeat split; lia.
  Qed.

  Lemma gnode_NoDup : forall n, NoDup (gnode n).
  Proof.
    intro n. apply NoDup_map_inj; [apply enum_NoDup |].
    intros t t' Ht Ht' E.
    apply enum_sound in Ht. apply enum_sound in Ht'.
    apply canonical2 in Ht as (i & j & -> & Hij & _).
    apply canonical2 in Ht' as (i' & j' & -> & Hij' & _).
    simpl nth in E. apply cq_Q2_eq in E as [[E1 E2] | [E1 E2]];
      apply hval_inj in E1; apply hval_inj in E2; subst.
    - reflexivity.
    - assert (i = j) by lia. subst. reflexivity.
  Qed.

  Lemma nodes_NoDup : forall n, NoDup (hnode n ++ gnode n).
  Proof.
    intro n. apply NoDup_app_disjoint.
    - apply NoDup_map_inj; [apply NoDup_seq |].
      intros x y _ _ E. injection E as E. exact (leaf_inj x y E).
    - apply gnode_NoDup.
    - intros q H1 H2. unfold hnode in H1. unfold gnode in H2.
      apply in_map_iff in H1 as (i & E1 & _).
      apply in_map_iff in H2 as (t & E2 & _).
      subst q. exact (cq_Q2_not_Q1 _ _ _ _ _ E2).
  Qed.

  Lemma g_class_in : forall n i j, i < n -> j < n ->
    In (cq (Q2 sg (hval i) (hval j))) (gnode n).
  Proof.
    intros n i j Hi Hj. unfold gnode. apply in_map_iff.
    destruct (Nat.le_gt_cases i j).
    - exists [i; j]. split; [reflexivity |].
      apply enum_complete. simpl. lia.
    - exists [j; i]. split.
      + simpl nth. apply cq_swap. exact sg_comm.
      + apply enum_complete. simpl. lia.
  Qed.

  Lemma nodes_are_the_classes : forall n q,
    In q (all_classes kfree (targets2 n)) <-> In q (hnode n ++ gnode n).
  Proof.
    intros n q. unfold all_classes, targets2. split; intro H.
    - apply in_flat_map in H as (t & Ht & Hq).
      apply in_map_iff in Ht as ([i j] & <- & Hij).
      apply in_prod_iff in Hij as [Hi Hj].
      apply in_seq in Hi. apply in_seq in Hj.
      rewrite classes_cell in Hq. apply in_or_app.
      destruct Hq as [<- | [<- | [<- | []]]].
      + right. apply g_class_in; lia.
      + left. apply in_map_iff. exists i. split; [reflexivity | apply in_seq; lia].
      + left. apply in_map_iff. exists j. split; [reflexivity | apply in_seq; lia].
    - apply in_flat_map. apply in_app_or in H as [H | H].
      + apply in_map_iff in H as (i & <- & Hi).
        exists (cell (i, i)). split.
        * apply in_map. apply in_prod; exact Hi.
        * rewrite classes_cell. right. left. reflexivity.
      + apply in_map_iff in H as (t & <- & Ht).
        apply enum_sound in Ht.
        apply canonical2 in Ht as (i & j & -> & Hij & Hj).
        exists (cell (i, j)). split.
        * apply in_map. apply in_prod; apply in_seq; lia.
        * rewrite classes_cell. left. reflexivity.
  Qed.

  (* W(P) = omega(1, [n]) + omega(S_2, [n]^2) = n + C(n+1, 2). *)
  Theorem two_node_orbit_work : forall n,
    length (nodup qry_dec (all_classes kfree (targets2 n)))
      = n + C (n + 1) 2.
  Proof.
    intro n.
    transitivity (length (hnode n ++ gnode n)).
    - apply Nat.le_antisymm; apply NoDup_incl_length.
      + apply NoDup_nodup.
      + intros q Hq. apply nodup_In in Hq. apply nodes_are_the_classes. exact Hq.
      + apply nodes_NoDup.
      + intros q Hq. apply nodup_In. apply nodes_are_the_classes. exact Hq.
    - unfold hnode, gnode.
      rewrite app_length, !map_length, seq_length, storage_cardinality.
      replace (n + 2 - 1) with (n + 1) by lia. reflexivity.
  Qed.

  (* ... against the fused nest, which asks every class occurrence. *)
  Theorem two_node_fused_work : forall n,
    length (all_classes kfree (targets2 n)) = 3 * (n * n).
  Proof.
    intro n. unfold all_classes, targets2, dense.
    assert (H : forall l : list (nat * nat),
              length (flat_map (classes kfree) (map cell l)) = 3 * length l).
    { induction l as [|[i j] l IH]; [reflexivity |].
      cbn [map flat_map]. rewrite app_length, IH, classes_cell. simpl. lia. }
    rewrite H, prod_length, seq_length. reflexivity.
  Qed.

  (* the two together, for any correct program *)
  Corollary two_node_optimal :
    forall (n : nat) (P : prog qry nat (list nat)),
    (forall k, Kc k -> run P k = map (eval k) (targets2 n)) ->
    n + C (n + 1) 2 <= length (trace P kfree) /\
    length (trace (memo_prog (targets2 n)) kfree) = n + C (n + 1) 2.
  Proof.
    intros n P Hcor. split.
    - rewrite <- two_node_orbit_work.
      exact (orbit_work_lower_bound (targets2 n) P Hcor).
    - rewrite orbit_work_attained. apply two_node_orbit_work.
  Qed.
End OrbitWork.

(* ===================================================================== *)
(* THE FREE MODEL EXISTS OVER nat.  Leaves are the multiples of 3, the   *)
(* spare value is 2, and the hash is 3 * code + 1 with code built from   *)
(* Cantor pairing -- so every hypothesis of the section is discharged    *)
(* and the theorems below are closed.                                    *)
(* ===================================================================== *)

Definition cleaf (i : nat) : nat := 3 * i.
Definition cfresh : nat := 2.

Definition ccode (q : qry) : nat :=
  match q with
  | Q1 s a => 2 * to_nat (s, a)
  | Q2 s a b => 2 * to_nat (s, to_nat (a, b)) + 1
  end.

Definition chash (q : qry) : nat := 3 * ccode q + 1.

Lemma chash_inj : forall q q', chash q = chash q' -> q = q'.
Proof.
  intros q q' E. unfold chash in E.
  assert (Ec : ccode q = ccode q') by lia. clear E.
  destruct q as [s a | s a b], q' as [s' a' | s' a' b']; cbn [ccode] in Ec;
    try lia.
  - assert (E : to_nat (s, a) = to_nat (s', a')) by lia.
    apply to_nat_inj in E. congruence.
  - assert (E : to_nat (s, to_nat (a, b)) = to_nat (s', to_nat (a', b')))
      by lia.
    apply to_nat_inj in E.
    assert (E2 : to_nat (a, b) = to_nat (a', b')) by congruence.
    apply to_nat_inj in E2. congruence.
Qed.

Lemma chash_cleaf : forall q i, chash q <> cleaf i.
Proof. intros q i. unfold chash, cleaf. lia. Qed.

Lemma cfresh_cleaf : forall i, cfresh <> cleaf i.
Proof. intro i. unfold cfresh, cleaf. lia. Qed.

Lemma cfresh_chash : forall q, cfresh <> chash q.
Proof. intro q. unfold cfresh, chash. lia. Qed.

(* T3 over nat, closed: for every declaration of which symbols commute   *)
(* and every target family, the memoized evaluator is correct and no     *)
(* correct program asks fewer questions in the free model.               *)
Theorem orbit_work_nat :
  forall (comm : nat -> bool) (targets : list tm),
  (forall k, Kc comm k ->
     run (memo_prog comm cleaf targets) k = map (eval cleaf k) targets) /\
  (forall P : prog qry nat (list nat),
     (forall k, Kc comm k -> run P k = map (eval cleaf k) targets) ->
     length (trace (memo_prog comm cleaf targets) (kfree comm chash))
       <= length (trace P (kfree comm chash))).
Proof.
  intros comm targets. split.
  - intros k Hk. apply memo_prog_correct. exact Hk.
  - intros P Hcor.
    exact (uniform_optimality_pipeline comm cleaf chash cfresh
             chash_inj chash_cleaf cfresh_cleaf cfresh_chash targets P Hcor).
Qed.

(* the two-node count, closed: h = symbol 0, g = symbol 1 declared comm *)
Theorem two_node_orbit_work_nat :
  forall (n : nat) (P : prog qry nat (list nat)),
  let comm := fun s => s =? 1 in
  (forall k, Kc comm k ->
     run P k = map (eval cleaf k) (targets2 0 1 n)) ->
  n + C (n + 1) 2 <= length (trace P (kfree comm chash)) /\
  length (trace (memo_prog comm cleaf (targets2 0 1 n)) (kfree comm chash))
    = n + C (n + 1) 2.
Proof.
  intros n P comm Hcor.
  apply (two_node_optimal comm cleaf chash cfresh
           chash_inj chash_cleaf cfresh_cleaf cfresh_chash
           (fun i j E => ltac:(unfold cleaf in E; lia)) 0 1 eq_refl n P Hcor).
Qed.

(* An illustration that computes: a cheap commutative test oracle, the   *)
(* 3 x 3 dense output.  The memoized evaluator asks 9 questions where    *)
(* the fused nest's 27 class occurrences would each be a call.           *)
Example two_node_computed :
  let comm := fun s => s =? 1 in
  let ktest := fun q => match q with
                        | Q1 _ a => 10 + a
                        | Q2 _ a b => 100 + 7 * (a + b) + a * b
                        end in
  length (trace (memo_prog comm (fun i => i) (targets2 0 1 3)) ktest) = 9 /\
  length (all_classes comm (fun i => i) ktest (targets2 0 1 3)) = 27 /\
  3 + C (3 + 1) 2 = 9.
Proof. vm_compute. repeat split. Qed.
