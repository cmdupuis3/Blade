(* A small semantic model, not a verification of F# alias analysis.
   Identifiers resolve to value keys; storage addresses are deliberately
   absent. Epochs are logical value versions, not required runtime counters.
   The Boolean frame models a rank-two transpose. General permutations and
   index-type-specific reconstruction actions require a richer descriptor.
   Equality of keys is sufficient evidence of equal oriented values; it is
   not a decision procedure for extensional equality of arbitrary programs. *)
From Stdlib Require Import List Arith Bool ZArith Lia.
Import ListNotations.

Record value_key := Key {
  origin : nat;
  epoch : nat;
  swapped : bool
}.

Definition key_eq_dec (a b : value_key) : {a = b} + {a <> b}.
Proof. decide equality; [apply Bool.bool_dec | apply Nat.eq_dec | apply Nat.eq_dec]. Defined.

Definition transpose_key (k : value_key) :=
  Key (origin k) (epoch k) (negb (swapped k)).

Theorem transpose_key_involutive : forall k,
  transpose_key (transpose_key k) = k.
Proof. intros [r v b]; destruct b; reflexivity. Qed.

Theorem transpose_keeps_origin : forall k,
  origin (transpose_key k) = origin k /\ epoch (transpose_key k) = epoch k.
Proof. intros; split; reflexivity. Qed.

Theorem transpose_changes_oriented_key : forall k, transpose_key k <> k.
Proof. intros [r v b]; destruct b; discriminate. Qed.

Definition read_key {V : Type}
    (values : nat -> nat -> nat -> nat -> V) (k : value_key) i j :=
  if swapped k then values (origin k) (epoch k) j i
  else values (origin k) (epoch k) i j.

Theorem transpose_key_read : forall (V : Type) values k i j,
  @read_key V values (transpose_key k) i j = read_key values k j i.
Proof. intros V values [r v b] i j; destruct b; reflexivity. Qed.

(* A licensed symmetric index type can erase the orientation change.
   The raw key normalization itself does not presume this certificate. *)
Theorem symmetry_licenses_transpose_erasure : forall (V : Type) values k,
  (forall i j, @read_key V values k i j = read_key values k j i) ->
  forall i j, read_key values (transpose_key k) i j = read_key values k i j.
Proof. intros V values k H i j; rewrite transpose_key_read; apply H. Qed.

Definition identifier := nat.
Definition environment := identifier -> value_key.
Definition bind_alias (env : environment) (destination source : identifier) :=
  fun name => if Nat.eq_dec name destination then env source else env name.

Theorem alias_preserves_key : forall env destination source,
  bind_alias env destination source destination = env source.
Proof. intros; unfold bind_alias; destruct (Nat.eq_dec destination destination); congruence. Qed.

Theorem alias_preserves_other_bindings : forall env destination source name,
  name <> destination -> bind_alias env destination source name = env name.
Proof. intros; unfold bind_alias; destruct (Nat.eq_dec name destination); congruence. Qed.

Theorem alias_preserves_read : forall (V : Type) values env destination source i j,
  @read_key V values (bind_alias env destination source destination) i j =
  read_key values (env source) i j.
Proof. intros; rewrite alias_preserves_key; reflexivity. Qed.

Definition sharing_partition (a b : value_key) : list nat :=
  if key_eq_dec a b then [0; 0] else [0; 1].

Theorem sharing_partition_same : forall a, sharing_partition a a = [0; 0].
Proof. intros; unfold sharing_partition; destruct (key_eq_dec a a); congruence. Qed.

Theorem sharing_partition_distinct : forall a b,
  a <> b -> sharing_partition a b = [0; 1].
Proof. intros; unfold sharing_partition; destruct (key_eq_dec a b); congruence. Qed.

Theorem sharing_partition_ignores_alias_spelling : forall env destination source,
  sharing_partition (bind_alias env destination source destination) (env source) = [0; 0].
Proof. intros; rewrite alias_preserves_key; apply sharing_partition_same. Qed.

Inductive pair_layout := SymmetricPair | DensePair.

(* The kernel's exchange law is independently licensed. Distinct keys
   conservatively retain the dense layout; they need not denote unequal data. *)
Definition pair_result_layout (exchange_licensed : bool) (a b : value_key) :=
  if exchange_licensed then
    if key_eq_dec a b then SymmetricPair else DensePair
  else DensePair.

Theorem same_operand_selects_compact_layout : forall a,
  pair_result_layout true a a = SymmetricPair.
Proof. intros; unfold pair_result_layout; destruct (key_eq_dec a a); congruence. Qed.

Theorem distinct_operands_select_dense_layout : forall a b,
  a <> b -> pair_result_layout true a b = DensePair.
Proof. intros; unfold pair_result_layout; destruct (key_eq_dec a b); congruence. Qed.

(* All keys here have the same ordinary input type. Hence a specialization
   keyed only by that ordinary type cannot implement both return layouts. *)
Theorem ordinary_types_do_not_determine_result_layout :
  pair_result_layout true (Key 0 0 false) (Key 0 0 false) <>
  pair_result_layout true (Key 0 0 false) (Key 1 0 false).
Proof. vm_compute; discriminate. Qed.

Definition add_outer (a b : nat -> Z) i j := (a i + b j)%Z.

Theorem shared_add_outer_is_symmetric : forall a i j,
  add_outer a a i j = add_outer a a j i.
Proof. intros; unfold add_outer; apply Z.add_comm. Qed.

Example distinct_add_outer_need_not_be_symmetric :
  let a := fun i => Z.of_nat i in
  let b := fun i => (2 * Z.of_nat i)%Z in
  add_outer a b 0 1 <> add_outer a b 1 0.
Proof. vm_compute; discriminate. Qed.

Definition snapshot_transpose {V : Type} (before : nat -> nat -> V) :=
  fun i j => before j i.

Theorem stable_source_allows_delayed_transpose : forall (V : Type) before after,
  (forall i j, before i j = after i j) ->
  forall i j, @snapshot_transpose V before i j = snapshot_transpose after i j.
Proof. intros; unfold snapshot_transpose; auto. Qed.

Example mutation_distinguishes_snapshot_from_live_view :
  let before := fun (_ _ : nat) => 0 in
  let after := fun i j => if (Nat.eqb i 0 && Nat.eqb j 1)%bool then 7 else 0 in
  snapshot_transpose before 1 0 <> snapshot_transpose after 1 0.
Proof. vm_compute; discriminate. Qed.

Print Assumptions alias_preserves_read.
Print Assumptions sharing_partition_ignores_alias_spelling.
Print Assumptions ordinary_types_do_not_determine_result_layout.
Print Assumptions stable_source_allows_delayed_transpose.
