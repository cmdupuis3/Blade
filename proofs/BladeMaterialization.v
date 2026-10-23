(* Materialization elimination for finite canonical producer graphs.
   Nodes have frozen semantic keys, descriptors, and scalar expression trees.
   A retained node stores a real finite list; an eliminated node retains a
   reader over its lexical predecessor environment. The proof covers arbitrary
   retention policies, signed/conjugating reconstruction, structural zeros,
   adaptive scalar reads, and an ordered effect/snapshot skeleton.

   Scope: scalar operations are pure total functions. Effectful/partial kernels
   must remain opaque obligations or have a separately proved extraction into
   the effect skeleton. Snapshot readers capture immutable mathematical stores;
   this does not manufacture a free physical copy of an overwritten input.
   Type correctness, source-to-graph lowering, storage availability, legal
   contiguous traversal, and resource costs remain compiler obligations.
   No claim is made that every value-correct reader is a legal Blade schedule.
*)
From Stdlib Require Import List Arith Lia Bool.
From Blade Require Import BladeStructuralIdentity.
Import ListNotations.

Section Materialization.
  Context {Coord Value : Type}.
  Variable zero : Value.
  Definition reader := Coord -> Value.
  Definition environment := value_key -> reader.
  Definition env_equiv (a b : environment) := forall key x, a key x = b key x.

  Lemma env_equiv_refl : forall env, env_equiv env env.
  Proof. intros env key x; reflexivity. Qed.

  Definition bind (env : environment) key (value : reader) : environment :=
    fun other => if key_eq_dec other key then value else env other.

  Lemma bind_congruent : forall a b key u v,
    env_equiv a b -> (forall x, u x = v x) ->
    env_equiv (bind a key u) (bind b key v).
  Proof.
    intros a b key u v H Hu other x; unfold bind.
    destruct (key_eq_dec other key); auto.
  Qed.

  (* The finite payload is separate from the complete logical read. Actions
     are applied only to stored representatives, never to structural zeros. *)
  Record descriptor := Descriptor {
    slot_count : nat;
    locate : Coord -> option (nat * (Value -> Value));
    locate_bound : forall x k action, locate x = Some (k, action) -> k < slot_count
  }.

  Definition reconstruct (d : descriptor) (cells : nat -> Value) x :=
    match locate d x with
    | None => zero
    | Some (k, action) => action (cells k)
    end.

  Definition buffer (n : nat) (cells : nat -> Value) := map cells (seq 0 n).

  Lemma nth_tabulation : forall n start k (cells : nat -> Value) default,
    k < n -> nth k (map cells (seq start n)) default = cells (start + k).
  Proof.
    induction n as [|n IH]; intros start k cells default H; [lia |].
    destruct k as [|k]; simpl.
    - now rewrite Nat.add_0_r.
    - rewrite IH by lia. f_equal; lia.
  Qed.

  Theorem buffer_readback : forall n cells k,
    k < n -> nth k (buffer n cells) zero = cells k.
  Proof. intros; unfold buffer; rewrite nth_tabulation by assumption; reflexivity. Qed.

  Theorem buffer_cardinality : forall n cells, length (buffer n cells) = n.
  Proof. intros; unfold buffer; rewrite length_map, length_seq; reflexivity. Qed.

  Definition packed_read d cells :=
    reconstruct d (fun k => nth k (buffer (slot_count d) cells) zero).

  Theorem packed_read_equals_virtual : forall d cells x,
    packed_read d cells x = reconstruct d cells x.
  Proof.
    intros d cells x; unfold packed_read, reconstruct.
    destruct (locate d x) as [[k action]|] eqn:E; [|reflexivity].
    rewrite buffer_readback; [reflexivity | eapply locate_bound; exact E].
  Qed.

  Theorem structural_zero_survives_elimination : forall d cells x,
    locate d x = None -> packed_read d cells x = zero /\ reconstruct d cells x = zero.
  Proof. intros; unfold packed_read, reconstruct; rewrite H; split; reflexivity. Qed.

  Theorem elimination_under_reindex : forall (Target : Type) (pi : Target -> Coord) d cells x,
    packed_read d cells (pi x) = reconstruct d cells (pi x).
  Proof. intros; apply packed_read_equals_virtual. Qed.

  (* Exact finite scalar trees: no reassociation or new canonical evaluation.
     DynamicRead and Choose include value-dependent indexing and branching.
     The coordinate and scalar functions must be total in this pure fragment. *)
  Inductive scalar :=
  | Literal : Value -> scalar
  | Read : value_key -> Coord -> scalar
  | DynamicRead : value_key -> (Value -> Coord) -> scalar -> scalar
  | Unary : (Value -> Value) -> scalar -> scalar
  | Binary : (Value -> Value -> Value) -> scalar -> scalar -> scalar
  | Choose : (Value -> bool) -> scalar -> scalar -> scalar -> scalar.

  Fixpoint eval_scalar (t : scalar) (env : environment) : Value :=
    match t with
    | Literal v => v
    | Read key x => env key x
    | DynamicRead key index t => env key (index (eval_scalar t env))
    | Unary f t => f (eval_scalar t env)
    | Binary f l r => f (eval_scalar l env) (eval_scalar r env)
    | Choose test condition yes no =>
        if test (eval_scalar condition env) then eval_scalar yes env else eval_scalar no env
    end.

  Lemma scalar_congruent : forall t a b,
    env_equiv a b -> eval_scalar t a = eval_scalar t b.
  Proof.
    induction t as [v|key x|key index t IHt|f t IHt|
                    f l IHt1 r IHt2|test condition IHt1 yes IHt2 no IHt3];
      intros a b H; simpl; try reflexivity.
    - apply H.
    - rewrite (IHt a b H); apply H.
    - now rewrite (IHt a b H).
    - now rewrite (IHt1 a b H), (IHt2 a b H).
    - rewrite (IHt1 a b H); destruct (test (eval_scalar condition b)); auto.
  Qed.

  Record node := Node {
    result_key : value_key;
    result_descriptor : descriptor;
    canonical_body : nat -> scalar
  }.

  Definition cells (env : environment) n k := eval_scalar (canonical_body n k) env.
  Definition realize (retain : bool) env n : reader :=
    if retain then packed_read (result_descriptor n) (cells env n)
    else reconstruct (result_descriptor n) (cells env n).

  Theorem realization_policy_irrelevant : forall retain env n x,
    realize retain env n x = reconstruct (result_descriptor n) (cells env n) x.
  Proof. intros; destruct retain; [apply packed_read_equals_virtual | reflexivity]. Qed.

  Lemma realization_congruent : forall retain1 retain2 a b n,
    env_equiv a b -> forall x, realize retain1 a n x = realize retain2 b n x.
  Proof.
    intros retain1 retain2 a b n H x; rewrite !realization_policy_irrelevant.
    unfold reconstruct; destruct (locate (result_descriptor n) x) as [[k action]|]; [|reflexivity].
    unfold cells; rewrite (scalar_congruent (canonical_body n k) a b H); reflexivity.
  Qed.

  Definition retention_policy := node -> bool.
  Definition retain_all : retention_policy := fun _ => true.
  Definition eliminate_all : retention_policy := fun _ => false.

  Fixpoint run_graph (policy : retention_policy) (graph : list node) (env : environment) :=
    match graph with
    | [] => env
    | n :: rest => run_graph policy rest (bind env (result_key n) (realize (policy n) env n))
    end.

  Theorem graph_policy_equivalence : forall graph p q a b,
    env_equiv a b -> env_equiv (run_graph p graph a) (run_graph q graph b).
  Proof.
    induction graph as [|n rest IH]; intros p q a b H; simpl; [exact H |].
    apply IH; apply bind_congruent; [exact H | apply realization_congruent; exact H].
  Qed.

  Theorem graph_concatenation : forall first second policy env,
    run_graph policy (first ++ second) env = run_graph policy second (run_graph policy first env).
  Proof. induction first; intros; simpl; [reflexivity | apply IHfirst]. Qed.

  Definition query := (value_key * Coord)%type.
  Definition observe (env : environment) (queries : list query) :=
    map (fun q => env (fst q) (snd q)) queries.

  Lemma observations_congruent : forall a b queries,
    env_equiv a b -> observe a queries = observe b queries.
  Proof. intros; unfold observe; apply map_ext; intro q; apply H. Qed.

  Theorem pure_graph_materialization_elimination : forall graph policy env queries,
    observe (run_graph retain_all graph env) queries =
    observe (run_graph policy graph env) queries.
  Proof. intros; apply observations_congruent; apply graph_policy_equivalence; apply env_equiv_refl. Qed.

  (* An explicit, unchanged effect skeleton. Logical snapshots capture the
     store at this point. Their backing storage/lifetime is not modeled.
     Partial/effectful scalar producers are NOT silently made pure by this
     syntax: they require a separate lowering proof or an opaque boundary. *)
  Definition store := nat -> Value.
  Definition write_store (s : store) address v :=
    fun other => if Nat.eq_dec other address then v else s other.

  Inductive event :=
  | Output : Value -> event
  | StoreWrite : nat -> Value -> event
  | SnapshotTaken : value_key -> event
  | Failure : nat -> event.

  Inductive result := Returned : list Value -> result | Aborted : nat -> result.
  Record observation := Observation {
    final_store : store;
    trace : list event;
    answer : result
  }.
  Definition prefix_event e o := Observation (final_store o) (e :: trace o) (answer o).

  Inductive program :=
  | Return : list query -> program
  | PureBlock : list node -> program -> program
  | Emit : query -> program -> program
  | Write : nat -> query -> program -> program
  | Snapshot : value_key -> (Coord -> nat) -> program -> program
  | Check : query -> (Value -> bool) -> nat -> program -> program
  | Abort : nat -> program.

  Fixpoint run_program policy (p : program) env s : observation :=
    match p with
    | Return queries => Observation s [] (Returned (observe env queries))
    | PureBlock graph rest => run_program policy rest (run_graph policy graph env) s
    | Emit (key, x) rest => prefix_event (Output (env key x)) (run_program policy rest env s)
    | Write address (key, x) rest =>
        prefix_event (StoreWrite address (env key x))
          (run_program policy rest env (write_store s address (env key x)))
    | Snapshot key address rest =>
        prefix_event (SnapshotTaken key)
          (run_program policy rest (bind env key (fun x => s (address x))) s)
    | Check (key, x) predicate error rest =>
        if predicate (env key x) then run_program policy rest env s
        else Observation s [Failure error] (Aborted error)
    | Abort error => Observation s [Failure error] (Aborted error)
    end.

  Theorem program_policy_equivalence : forall p policy1 policy2 a b s,
    env_equiv a b -> run_program policy1 p a s = run_program policy2 p b s.
  Proof.
    induction p as [queries|graph rest IH|[key x] rest IH|address [key x] rest IH|
                    key address rest IH|[key x] predicate error rest IH|error];
      intros policy1 policy2 a b s H; simpl.
    - rewrite (observations_congruent a b queries H); reflexivity.
    - apply IH; apply graph_policy_equivalence; exact H.
    - rewrite (H key x), (IH policy1 policy2 a b s H); reflexivity.
    - rewrite (H key x); rewrite (IH policy1 policy2 a b (write_store s address (b key x)) H); reflexivity.
    - f_equal; apply IH; apply bind_congruent; [exact H | reflexivity].
    - rewrite (H key x); destruct (predicate (b key x)); [apply IH; exact H | reflexivity].
    - reflexivity.
  Qed.

  Theorem materialization_elimination : forall program policy env s,
    run_program retain_all program env s = run_program policy program env s.
  Proof. intros; apply program_policy_equivalence; apply env_equiv_refl. Qed.

  Corollary elimination_preserves_effect_trace : forall program policy env s,
    trace (run_program retain_all program env s) = trace (run_program policy program env s).
  Proof. intros; rewrite materialization_elimination with (policy := policy); reflexivity. Qed.

  Corollary elimination_preserves_result_and_failure : forall program policy env s,
    answer (run_program retain_all program env s) = answer (run_program policy program env s).
  Proof. intros; rewrite materialization_elimination with (policy := policy); reflexivity. Qed.

  Theorem snapshot_read_preserved_by_later_write : forall s key address env policy target q value,
    answer (run_program policy
      (Snapshot key address (Write target q (Return [(key, value)]))) env s) =
    Returned [s (address value)].
  Proof.
    intros s key address env policy target [source coordinate] value.
    simpl; unfold observe, bind; simpl.
    destruct (key_eq_dec key key); [reflexivity | contradiction].
  Qed.

  Theorem unconsumed_failure_cannot_be_erased : forall policy env s error,
    answer (run_program policy (Abort error) env s) <>
    answer (run_program policy (Return []) env s).
  Proof. intros; simpl; discriminate. Qed.
End Materialization.

Print Assumptions packed_read_equals_virtual.
Print Assumptions pure_graph_materialization_elimination.
Print Assumptions materialization_elimination.
Print Assumptions snapshot_read_preserved_by_later_write.
Print Assumptions unconsumed_failure_cannot_be_erased.
