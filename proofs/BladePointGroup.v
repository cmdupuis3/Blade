(* ===================================================================== *)
(* BladePointGroup.v -- THE POINT-GROUP TABLES, CHECKED BY COMPUTATION:   *)
(* the stage 5b-0 obligations of docs/plan-transforms-as-types.md 3.6     *)
(* (the "point groups as the second block-spec member" subsection) and    *)
(* 7's 5b-0 bullet, whose mandate for this file reads: BladePointGroup.v  *)
(* -- all computational over the witnesses: table closure, FS indicators, *)
(* J identities, the e-weighted sum; End-completeness cited,              *)
(* oracle-discharged.                                                     *)
(*                                                                        *)
(* WHAT THE COMPILER DOES, AND WHAT THIS FILE GUARDS.  MLPointSpec.fs     *)
(* ships a FROZEN INTEGER REGISTRY -- FsType / PgIrrep / PointGroup over  *)
(* the witness roster {C4, D4} -- and asserts its integrity ON LOAD.      *)
(* Every load-time assert has a theorem here, over the same matrices:     *)
(*                                                                        *)
(*   MLPointSpec load-time assert        theorem(s) here                  *)
(*   ---------------------------------   ------------------------------- *)
(*   the word set is a group (Cayley      c4_table_is_group,              *)
(*   table well-formed, closed)           d4_table_is_group,              *)
(*                                        c4_word_set_closed,             *)
(*                                        d4_word_set_closed,             *)
(*                                        c4_element_count,               *)
(*                                        d4_element_count                *)
(*   each irrep's generator images        c4_generator_relations,         *)
(*   satisfy the defining relations       d4_generator_relations,         *)
(*   and respect the group law            c4_rep_property,                *)
(*                                        d4_rep_property                 *)
(*   the FsType column is the             c4_fs_sums / d4_fs_sums,        *)
(*   Frobenius-Schur indicator            c4_fs_indicators /              *)
(*   sum_g chi(g^2) = |G| * fs            d4_fs_indicators,               *)
(*                                        c4_fs_exact / d4_fs_exact,      *)
(*                                        c4_fs_computed_eq_declared,     *)
(*                                        d4_fs_computed_eq_declared      *)
(*   J^2 = -Id and J commutes with        J_square_is_neg_id,             *)
(*   the baked generators (the [Id, J]    J_commutes_with_generator,      *)
(*   emitted basis of a C-type label)     J_commutes_with_C4_E            *)
(*   Gram = d * I exactly (independence   c4E_end_gram_is_d_id            *)
(*   with no rank decision)                                               *)
(*   R-Burnside sum_i d_i^2 / e_i = |G|   c4_rburnside / d4_rburnside     *)
(*                                        (+ _exact: the division is)     *)
(*   pgHomDim's e-weighted block count    pg_hom_dim_spec_sum,            *)
(*                                        hom_blocks_spec,                *)
(*                                        pg_hom_dim_c4_contrast (9),     *)
(*                                        pg_hom_dim_d4_contrast (5)      *)
(*                                                                        *)
(* THE CANONICAL TABLES.  These are the 3.6-canonical matrices; the F#    *)
(* registry in src/ml/compiler/MLPointSpec.fs carries the same entries    *)
(* and the two must be KEPT IN SYNC (the tables are frozen table data,    *)
(* never derived at a call site -- 3.6's "baked per-label matrix"):       *)
(*                                                                        *)
(*   C4 (order 4, generator r):                                           *)
(*     A  : 1-dim, r |-> (1)                                              *)
(*     B  : 1-dim, r |-> (-1)                                             *)
(*     E  : 2-dim, r |-> [[0,-1],[1,0]] = R90,  J = R90,  C-type          *)
(*   D4 (order 8, generators r, s):                                       *)
(*     A1 (1,1)   A2 (1,-1)   B1 (-1,1)   B2 (-1,-1)   (values of r, s)   *)
(*     E  : 2-dim, r |-> R90, s |-> [[1,0],[0,-1]],     R-type            *)
(*                                                                        *)
(* Every entry is in {-1, 0, 1}: 3.6 chooses the witness roster by MATRIX *)
(* RATIONALITY, not by crystallography, so the F# oracle is exact-        *)
(* rational with no field extension.  The word sets are FIXED here and    *)
(* used everywhere:                                                       *)
(*   c4_words = [e; r; r^2; r^3]                                          *)
(*   d4_words = [e; r; r^2; r^3; s; rs; r^2 s; r^3 s]                     *)
(* written as generator-index words ([] = e, [0] = r, [0;1] = r s, ...),  *)
(* so the matrix of a word is a fold and the rep property is a finite     *)
(* check against an explicit Cayley table.  What must be KEPT IN SYNC     *)
(* with MLPointSpec.fs is the TABLE DATA -- label roster, dimensions,     *)
(* FsType column, generator order and generator matrices.  The word list  *)
(* is this file's own choice of representatives, in coset order;          *)
(* MLPointSpec derives its own by breadth-first generator closure from    *)
(* the identity, so it may name the same elements by different words.     *)
(* The two enumerate the SAME element set, which is exactly what closure  *)
(* plus the element count assert on each side.                            *)
(*                                                                        *)
(* THE CONTRAST ANCHOR, and why it is a chain and not three asserts.      *)
(* 3.6's thesis is that C4 and D4 differ in ONE number: same E dimension, *)
(* e = 2 (C-type) vs e = 1 (R-type), so the same spec shape sizes 9 at C4 *)
(* and 5 at D4.  Here e is never asserted: irrep_e is e_of_fs applied to  *)
(* the COMPUTED indicator, and pg_ev looks the label up in the registry,  *)
(* so pg_hom_dim_c4_contrast = 9 is literally                             *)
(*   traces of squared word matrices -> sum -> /|G| -> fs -> e -> count.  *)
(* pg_hom_dim_c4_naive_control pins the other end: with e == 1 the C4     *)
(* count is 5, i.e. the FS correction IS the whole difference.            *)
(*                                                                        *)
(* NOT MODELLED, deliberately: END-BASIS COMPLETENESS FOR GENERAL G.      *)
(* That End_G(U) for an R-irreducible U is R, C or H and nothing else     *)
(* (the Schur-over-R trichotomy), hence that [Id] and [Id, J] EXHAUST the *)
(* equivariant endomorphisms and the e-weighted sum is the full           *)
(* dim_R Hom, is CITED -- 6.1's closure ("mathcomp is OUT": everything    *)
(* 5b relies on is either a finite integer computation over baked data,   *)
(* which is this file, or a general theorem whose shipped-group instance  *)
(* the exact oracle discharges).  At the SHIPPED WITNESSES the claim is   *)
(* discharged numerically, not by proof: tests/Test_PgOracle.fs builds    *)
(* the exact-rational Hom-space Reynolds projector over Q and compares it *)
(* entrywise to the emitted basis -- the same cited/computed division,    *)
(* and the same oracle naming, as BladePartition.v's Test_PermOracle.     *)
(* What IS proved here about the End side is the independence half, which *)
(* is all a compiler can certify per call: the two integer asserts        *)
(* (J^2 = -Id, J commutes) plus Gram = d * I, with the negative controls  *)
(* c4E_diag_not_equivariant (the spurious diag(1,-1) column dies at R90)  *)
(* and d4E_J_not_equivariant (J is not available at an R-type label).     *)
(*                                                                        *)
(* Also not modelled: characters as class functions, orthogonality,       *)
(* Clebsch-Gordan / fusion multiplicity (3.6 defers the CG-copy index to  *)
(* 5b-iii), and any group not on the witness roster.  Nothing here is     *)
(* quantified over "all point groups"; every theorem is over C4 or D4.    *)
(*                                                                        *)
(* Imports BladeDMWF (lsum) and BladeSymPower (the lsum register:         *)
(* lsum_app, lsum_flat_map).  Rocq 9.0, stdlib only; no Admitted, no      *)
(* Axiom, no classical reasoning -- the arithmetic is vm_compute over     *)
(* integer matrices.                                                      *)
(* ===================================================================== *)

From Blade Require Import BladeDMWF BladeSymPower.
Require Import List Arith Lia ZArith.
Import ListNotations.
Open Scope nat_scope.

(* ===================================================================== *)
(* INTEGER MATRICES.  Square, dimension = length; entries in {-1, 0, 1}  *)
(* for every matrix this file ships, so nothing ever leaves Z.           *)
(* ===================================================================== *)

Definition mat : Type := list (list Z).

Definition zsum (l : list Z) : Z := fold_right Z.add 0%Z l.

Definition mentry (A : mat) (i j : nat) : Z := nth j (nth i A []) 0%Z.

Definition mmul (A B : mat) : mat :=
  map (fun row : list Z =>
         map (fun j => zsum (map (fun k => Z.mul (nth k row 0%Z) (mentry B k j))
                                 (seq 0 (length B))))
             (seq 0 (length B)))
      A.

Definition mid (n : nat) : mat :=
  map (fun i => map (fun j => if Nat.eqb i j then 1%Z else 0%Z) (seq 0 n)) (seq 0 n).

Definition mneg (A : mat) : mat := map (map Z.opp) A.

Definition mtranspose (A : mat) : mat :=
  map (fun j => map (fun row : list Z => nth j row 0%Z) A) (seq 0 (length A)).

Definition mtrace (A : mat) : Z := zsum (map (fun i => mentry A i i) (seq 0 (length A))).

Definition msq (A : mat) : mat := mmul A A.

Definition m1 (z : Z) : mat := [[z]].

(* --- decidable matrix equality, so the finite checks below are Props -- *)

Fixpoint zrow_eqb (u v : list Z) : bool :=
  match u, v with
  | [], [] => true
  | a :: u', b :: v' => andb (Z.eqb a b) (zrow_eqb u' v')
  | _, _ => false
  end.

Lemma zrow_eqb_eq : forall u v, zrow_eqb u v = true <-> u = v.
Proof.
  induction u as [|a u IH]; intros [|b v]; cbn.
  - split; intros _; reflexivity.
  - split; intro H; discriminate.
  - split; intro H; discriminate.
  - rewrite Bool.andb_true_iff, Z.eqb_eq, IH. split.
    + intros [H1 H2]. subst. reflexivity.
    + intro H. injection H as H1 H2. split; assumption.
Qed.

Fixpoint mat_eqb (A B : mat) : bool :=
  match A, B with
  | [], [] => true
  | u :: A', v :: B' => andb (zrow_eqb u v) (mat_eqb A' B')
  | _, _ => false
  end.

Lemma mat_eqb_eq : forall A B, mat_eqb A B = true <-> A = B.
Proof.
  induction A as [|u A IH]; intros [|v B]; cbn.
  - split; intros _; reflexivity.
  - split; intro H; discriminate.
  - split; intro H; discriminate.
  - rewrite Bool.andb_true_iff, zrow_eqb_eq, IH. split.
    + intros [H1 H2]. subst. reflexivity.
    + intro H. injection H as H1 H2. split; assumption.
Qed.

Lemma mat_eqb_refl : forall A, mat_eqb A A = true.
Proof. intro A. apply mat_eqb_eq. reflexivity. Qed.

(* --- boolean finite checks, and their Prop readings ------------------- *)

Lemma existsb_mat_In : forall A l, existsb (mat_eqb A) l = true -> In A l.
Proof.
  intros A l H. apply existsb_exists in H. destruct H as (B & HB & HE).
  apply mat_eqb_eq in HE. subst B. exact HB.
Qed.

Lemma mat_closed_sound : forall l : list mat,
  forallb (fun A => forallb (fun B => existsb (mat_eqb (mmul A B)) l) l) l = true ->
  forall A B, In A l -> In B l -> In (mmul A B) l.
Proof.
  intros l H A B HA HB. rewrite forallb_forall in H.
  specialize (H A HA). rewrite forallb_forall in H. specialize (H B HB).
  apply existsb_mat_In. exact H.
Qed.

Fixpoint mat_nodup_b (l : list mat) : bool :=
  match l with
  | [] => true
  | A :: l' => andb (negb (existsb (mat_eqb A) l')) (mat_nodup_b l')
  end.

Lemma mat_nodup_b_sound : forall l, mat_nodup_b l = true -> NoDup l.
Proof.
  induction l as [|A l IH]; cbn; intro H; [constructor |].
  apply Bool.andb_true_iff in H as [H1 H2]. apply Bool.negb_true_iff in H1.
  constructor.
  - intro Hin.
    assert (Hex : existsb (mat_eqb A) l = true).
    { apply existsb_exists. exists A. split; [exact Hin | apply mat_eqb_refl]. }
    rewrite H1 in Hex. discriminate.
  - apply IH. exact H2.
Qed.

Lemma forallb_seq : forall (f : nat -> bool) n,
  forallb f (seq 0 n) = true -> forall i, i < n -> f i = true.
Proof.
  intros f n H i Hi. rewrite forallb_forall in H. apply H. apply in_seq. lia.
Qed.

Lemma forallb_seq2 : forall (f : nat -> nat -> bool) n,
  forallb (fun i => forallb (f i) (seq 0 n)) (seq 0 n) = true ->
  forall i j, i < n -> j < n -> f i j = true.
Proof.
  intros f n H i j Hi Hj.
  exact (forallb_seq (f i) n (forallb_seq _ n H i Hi) j Hj).
Qed.

(* ===================================================================== *)
(* THE REGISTRY.  Labels are the frozen table rows of MLPointSpec; an    *)
(* Irrep is a label, a dimension, and the generator images IN GENERATOR  *)
(* ORDER; a PointGroup is an order, a word list, a Cayley table on word  *)
(* indices, and its irreps.                                             *)
(* ===================================================================== *)

Inductive PgLabel : Type :=
  | C4_A | C4_B | C4_E
  | D4_A1 | D4_A2 | D4_B1 | D4_B2 | D4_E.

Definition lab_code (L : PgLabel) : nat :=
  match L with
  | C4_A => 0 | C4_B => 1 | C4_E => 2
  | D4_A1 => 3 | D4_A2 => 4 | D4_B1 => 5 | D4_B2 => 6 | D4_E => 7
  end.

Definition lab_eqb (a b : PgLabel) : bool := Nat.eqb (lab_code a) (lab_code b).

Lemma lab_eqb_refl : forall L, lab_eqb L L = true.
Proof. intro L. unfold lab_eqb. apply Nat.eqb_refl. Qed.

Lemma lab_eqb_eq : forall a b, lab_eqb a b = true <-> a = b.
Proof.
  intros a b. unfold lab_eqb. split.
  - intro H. apply Nat.eqb_eq in H. destruct a, b; cbn in H; try discriminate;
      reflexivity.
  - intro H. subst b. apply Nat.eqb_refl.
Qed.

Record Irrep : Type := mkIrrep {
  ir_label : PgLabel;
  ir_dim   : nat;
  ir_gens  : list mat
}.

Record PointGroup : Type := mkPg {
  pg_order  : nat;
  pg_words  : list (list nat);
  pg_table  : list (list nat);
  pg_irreps : list Irrep
}.

(* --- the canonical matrices (3.6; keep in sync with MLPointSpec.fs) --- *)

Definition R90  : mat := [[0; -1]; [1; 0]]%Z.
Definition Sref : mat := [[1; 0]; [0; -1]]%Z.

(* The baked complex structure of C4's E: J = R90, per 3.6's [Id, J]. *)
Definition Jc4 : mat := R90.

Definition c4_A : Irrep := mkIrrep C4_A 1 [m1 1%Z].
Definition c4_B : Irrep := mkIrrep C4_B 1 [m1 (-1)%Z].
Definition c4_E : Irrep := mkIrrep C4_E 2 [R90].

Definition d4_A1 : Irrep := mkIrrep D4_A1 1 [m1 1%Z;    m1 1%Z].
Definition d4_A2 : Irrep := mkIrrep D4_A2 1 [m1 1%Z;    m1 (-1)%Z].
Definition d4_B1 : Irrep := mkIrrep D4_B1 1 [m1 (-1)%Z; m1 1%Z].
Definition d4_B2 : Irrep := mkIrrep D4_B2 1 [m1 (-1)%Z; m1 (-1)%Z].
Definition d4_E  : Irrep := mkIrrep D4_E  2 [R90; Sref].

(* THE WORD SETS, fixed explicitly: generator index 0 = r, 1 = s. *)
Definition c4_words : list (list nat) := [[]; [0]; [0;0]; [0;0;0]].

Definition d4_words : list (list nat) :=
  [[]; [0]; [0;0]; [0;0;0]; [1]; [0;1]; [0;0;1]; [0;0;0;1]].

(* THE CAYLEY TABLES on word indices.  C4: (i + j) mod 4.  D4: index      *)
(* i + 4e for r^i s^e, with s r^j = r^{-j} s.                             *)
Definition c4_table : list (list nat) :=
  [[0;1;2;3];
   [1;2;3;0];
   [2;3;0;1];
   [3;0;1;2]].

Definition d4_table : list (list nat) :=
  [[0;1;2;3;4;5;6;7];
   [1;2;3;0;5;6;7;4];
   [2;3;0;1;6;7;4;5];
   [3;0;1;2;7;4;5;6];
   [4;7;6;5;0;3;2;1];
   [5;4;7;6;1;0;3;2];
   [6;5;4;7;2;1;0;3];
   [7;6;5;4;3;2;1;0]].

Definition c4 : PointGroup := mkPg 4 c4_words c4_table [c4_A; c4_B; c4_E].

Definition d4 : PointGroup :=
  mkPg 8 d4_words d4_table [d4_A1; d4_A2; d4_B1; d4_B2; d4_E].

Definition tmul (G : PointGroup) (i j : nat) : nat := nth j (nth i (pg_table G) []) 0.

(* The matrix of a word: fold the generator images left to right, so     *)
(* [0;0;1] is (I r) r s = r^2 s.                                          *)
Definition word_mat (R : Irrep) (w : list nat) : mat :=
  fold_left (fun A i => mmul A (nth i (ir_gens R) (mid (ir_dim R)))) w (mid (ir_dim R)).

Definition elts (G : PointGroup) (R : Irrep) : list mat := map (word_mat R) (pg_words G).

(* ===================================================================== *)
(* PG1.  THE MULTIPLICATION TABLE, AND CLOSURE OF THE WORD SET.          *)
(* MLPointSpec asserts on load that its word set is a group and that     *)
(* every irrep's matrices are closed under multiplication.               *)
(* ===================================================================== *)

Definition table_range_b (G : PointGroup) : bool :=
  forallb (fun i => forallb (fun j => Nat.ltb (tmul G i j) (pg_order G))
                            (seq 0 (pg_order G)))
          (seq 0 (pg_order G)).

Definition table_assoc_b (G : PointGroup) : bool :=
  forallb (fun i => forallb (fun j => forallb (fun k =>
              Nat.eqb (tmul G (tmul G i j) k) (tmul G i (tmul G j k)))
              (seq 0 (pg_order G)))
              (seq 0 (pg_order G)))
          (seq 0 (pg_order G)).

Definition table_unit_b (G : PointGroup) : bool :=
  forallb (fun i => andb (Nat.eqb (tmul G 0 i) i) (Nat.eqb (tmul G i 0) i))
          (seq 0 (pg_order G)).

Definition table_inv_b (G : PointGroup) : bool :=
  forallb (fun i => existsb (fun j => andb (Nat.eqb (tmul G i j) 0)
                                           (Nat.eqb (tmul G j i) 0))
                            (seq 0 (pg_order G)))
          (seq 0 (pg_order G)).

Example c4_table_is_group :
  (table_range_b c4, table_assoc_b c4, table_unit_b c4, table_inv_b c4)
  = (true, true, true, true).
Proof. vm_compute. reflexivity. Qed.

Example d4_table_is_group :
  (table_range_b d4, table_assoc_b d4, table_unit_b d4, table_inv_b d4)
  = (true, true, true, true).
Proof. vm_compute. reflexivity. Qed.

(* The order, as the length of the FIXED word list. *)
Example pg_orders :
  (length (pg_words c4), pg_order c4, length (pg_words d4), pg_order d4)
  = (4, 4, 8, 8).
Proof. vm_compute. reflexivity. Qed.

(* THE MULTIPLICATION-TABLE-CLOSURE OBLIGATION, per irrep: every product *)
(* of two enumerated matrices lands back in the enumeration.             *)
Theorem c4_word_set_closed : forall R, In R (pg_irreps c4) ->
  forall A B, In A (elts c4 R) -> In B (elts c4 R) -> In (mmul A B) (elts c4 R).
Proof.
  intros R HR. simpl in HR.
  destruct HR as [E | [E | [E | []]]]; subst R;
    apply mat_closed_sound; vm_compute; reflexivity.
Qed.

Theorem d4_word_set_closed : forall R, In R (pg_irreps d4) ->
  forall A B, In A (elts d4 R) -> In B (elts d4 R) -> In (mmul A B) (elts d4 R).
Proof.
  intros R HR. simpl in HR.
  destruct HR as [E | [E | [E | [E | [E | []]]]]]; subst R;
    apply mat_closed_sound; vm_compute; reflexivity.
Qed.

(* THE ELEMENT COUNT.  Read off a FAITHFUL irrep (E in both groups): the *)
(* enumerated matrices are pairwise distinct, so the word set really has *)
(* 4 / 8 elements and is not a redundant listing.                        *)
Theorem c4_element_count : NoDup (elts c4 c4_E) /\ length (elts c4 c4_E) = 4.
Proof.
  split; [apply mat_nodup_b_sound |]; vm_compute; reflexivity.
Qed.

Theorem d4_element_count : NoDup (elts d4 d4_E) /\ length (elts d4 d4_E) = 8.
Proof.
  split; [apply mat_nodup_b_sound |]; vm_compute; reflexivity.
Qed.

(* ===================================================================== *)
(* PG2.  THE REP PROPERTY: the assignment on words respects the group    *)
(* law, and the generator images satisfy the defining relations.         *)
(* ===================================================================== *)

Definition rel_ok_c4 (R : Irrep) : bool :=
  mat_eqb (word_mat R [0;0;0;0]) (mid (ir_dim R)).

Definition rel_ok_d4 (R : Irrep) : bool :=
  andb (mat_eqb (word_mat R [0;0;0;0]) (mid (ir_dim R)))
  (andb (mat_eqb (word_mat R [1;1]) (mid (ir_dim R)))
        (mat_eqb (word_mat R [1;0;1]) (word_mat R [0;0;0]))).

Example c4_generator_relations : forallb rel_ok_c4 (pg_irreps c4) = true.
Proof. vm_compute. reflexivity. Qed.

(* r^4 = e, s^2 = e, s r s = r^3 = r^{-1} -- the D4 presentation. *)
Example d4_generator_relations : forallb rel_ok_d4 (pg_irreps d4) = true.
Proof. vm_compute. reflexivity. Qed.

Definition respects_cell (G : PointGroup) (R : Irrep) (i j : nat) : bool :=
  mat_eqb (mmul (word_mat R (nth i (pg_words G) []))
                (word_mat R (nth j (pg_words G) [])))
          (word_mat R (nth (tmul G i j) (pg_words G) [])).

Definition respects_b (G : PointGroup) (R : Irrep) : bool :=
  forallb (fun i => forallb (respects_cell G R i) (seq 0 (pg_order G)))
          (seq 0 (pg_order G)).

Theorem c4_rep_property : forall R, In R (pg_irreps c4) ->
  forall i j, i < pg_order c4 -> j < pg_order c4 ->
  mmul (word_mat R (nth i (pg_words c4) [])) (word_mat R (nth j (pg_words c4) []))
  = word_mat R (nth (tmul c4 i j) (pg_words c4) []).
Proof.
  intros R HR i j Hi Hj. apply mat_eqb_eq.
  assert (Hb : respects_b c4 R = true).
  { simpl in HR. destruct HR as [E | [E | [E | []]]]; subst R;
      vm_compute; reflexivity. }
  exact (forallb_seq2 (respects_cell c4 R) (pg_order c4) Hb i j Hi Hj).
Qed.

Theorem d4_rep_property : forall R, In R (pg_irreps d4) ->
  forall i j, i < pg_order d4 -> j < pg_order d4 ->
  mmul (word_mat R (nth i (pg_words d4) [])) (word_mat R (nth j (pg_words d4) []))
  = word_mat R (nth (tmul d4 i j) (pg_words d4) []).
Proof.
  intros R HR i j Hi Hj. apply mat_eqb_eq.
  assert (Hb : respects_b d4 R = true).
  { simpl in HR. destruct HR as [E | [E | [E | [E | [E | []]]]]]; subst R;
      vm_compute; reflexivity. }
  exact (forallb_seq2 (respects_cell d4 R) (pg_order d4) Hb i j Hi Hj).
Qed.

(* ===================================================================== *)
(* PG3.  FROBENIUS-SCHUR INDICATORS, COMPUTED FROM THE MATRICES.         *)
(* sum_g chi(g^2) = |G| * fs, with chi(g^2) the trace of the SQUARED     *)
(* word matrix.  fs = 1 (R-type) for every C4/D4 label except C4's E,    *)
(* whose sum is 0 (C-type).  MLPointSpec's FsType column is the declared *)
(* half; fs_declared is that column and the _computed_eq_declared        *)
(* theorems are the load-time assert.                                    *)
(* ===================================================================== *)

Definition chi (R : Irrep) (w : list nat) : Z := mtrace (word_mat R w).

Definition fs_sum (G : PointGroup) (R : Irrep) : Z :=
  zsum (map (fun w => mtrace (msq (word_mat R w))) (pg_words G)).

Definition fs_indicator (G : PointGroup) (R : Irrep) : Z :=
  Z.div (fs_sum G R) (Z.of_nat (pg_order G)).

(* MLPointSpec's frozen FsType column. *)
Definition fs_declared (L : PgLabel) : Z :=
  match L with C4_E => 0%Z | _ => 1%Z end.

Example c4_fs_sums : map (fs_sum c4) (pg_irreps c4) = [4; 4; 0]%Z.
Proof. vm_compute. reflexivity. Qed.

Example d4_fs_sums : map (fs_sum d4) (pg_irreps d4) = [8; 8; 8; 8; 8]%Z.
Proof. vm_compute. reflexivity. Qed.

Example c4_fs_indicators : map (fs_indicator c4) (pg_irreps c4) = [1; 1; 0]%Z.
Proof. vm_compute. reflexivity. Qed.

Example d4_fs_indicators : map (fs_indicator d4) (pg_irreps d4) = [1; 1; 1; 1; 1]%Z.
Proof. vm_compute. reflexivity. Qed.

(* The division by |G| is EXACT -- the indicator is an integer, not a    *)
(* truncation (the BladeSymPower discipline: exhibit the quotient).      *)
Theorem c4_fs_exact : forall R, In R (pg_irreps c4) ->
  fs_sum c4 R = (Z.of_nat (pg_order c4) * fs_indicator c4 R)%Z.
Proof.
  intros R HR. simpl in HR.
  destruct HR as [E | [E | [E | []]]]; subst R; vm_compute; reflexivity.
Qed.

Theorem d4_fs_exact : forall R, In R (pg_irreps d4) ->
  fs_sum d4 R = (Z.of_nat (pg_order d4) * fs_indicator d4 R)%Z.
Proof.
  intros R HR. simpl in HR.
  destruct HR as [E | [E | [E | [E | [E | []]]]]]; subst R; vm_compute; reflexivity.
Qed.

(* COMPUTED = DECLARED, per label. *)
Theorem c4_fs_computed_eq_declared : forall R, In R (pg_irreps c4) ->
  fs_indicator c4 R = fs_declared (ir_label R).
Proof.
  intros R HR. simpl in HR.
  destruct HR as [E | [E | [E | []]]]; subst R; vm_compute; reflexivity.
Qed.

Theorem d4_fs_computed_eq_declared : forall R, In R (pg_irreps d4) ->
  fs_indicator d4 R = fs_declared (ir_label R).
Proof.
  intros R HR. simpl in HR.
  destruct HR as [E | [E | [E | [E | [E | []]]]]]; subst R; vm_compute; reflexivity.
Qed.

(* ===================================================================== *)
(* PG4.  FS INDICATOR -> e.  e = dim_R End_G(U) in {1 (R), 2 (C),        *)
(* 4 (H)}.  The H value is RESERVED (3.6: it first appears at double     *)
(* groups; the registry keeps the value so counts stay uniform and       *)
(* emission raises a loud internal error), never a dead field.           *)
(* ===================================================================== *)

Definition e_of_fs (z : Z) : nat :=
  if Z.eqb z 1 then 1 else if Z.eqb z 0 then 2 else if Z.eqb z (-1) then 4 else 0.

Definition irrep_e (G : PointGroup) (R : Irrep) : nat := e_of_fs (fs_indicator G R).

Example c4_e_from_fs : map (irrep_e c4) (pg_irreps c4) = [1; 1; 2].
Proof. vm_compute. reflexivity. Qed.

Example d4_e_from_fs : map (irrep_e d4) (pg_irreps d4) = [1; 1; 1; 1; 1].
Proof. vm_compute. reflexivity. Qed.

(* The registry lookup the counting core uses.  Every e it ever returns  *)
(* is e_of_fs of a COMPUTED indicator: pg_ev_is_fs_derived names the     *)
(* link, so no e is asserted independently anywhere below.               *)
Definition pg_ev (G : PointGroup) (L : PgLabel) : nat :=
  match find (fun R => lab_eqb (ir_label R) L) (pg_irreps G) with
  | Some R => irrep_e G R
  | None => 0
  end.

Lemma pg_ev_is_fs_derived : forall G L R,
  find (fun R' => lab_eqb (ir_label R') L) (pg_irreps G) = Some R ->
  pg_ev G L = e_of_fs (fs_indicator G R).
Proof. intros G L R H. unfold pg_ev. rewrite H. reflexivity. Qed.

Example pg_ev_values :
  (pg_ev c4 C4_A, pg_ev c4 C4_B, pg_ev c4 C4_E,
   pg_ev d4 D4_A1, pg_ev d4 D4_E) = (1, 1, 2, 1, 1).
Proof. vm_compute. reflexivity. Qed.

(* ===================================================================== *)
(* PG5.  THE J IDENTITIES (the [Id, J] emitted basis of a C-type label). *)
(* ===================================================================== *)

Theorem J_square_is_neg_id : mmul Jc4 Jc4 = mneg (mid 2).
Proof. vm_compute. reflexivity. Qed.

Theorem J_commutes_with_generator :
  mmul Jc4 (word_mat c4_E [0]) = mmul (word_mat c4_E [0]) Jc4.
Proof. vm_compute. reflexivity. Qed.

Corollary J_commutes_with_C4_E : forall w, In w (pg_words c4) ->
  mmul Jc4 (word_mat c4_E w) = mmul (word_mat c4_E w) Jc4.
Proof.
  intros w Hw. simpl in Hw.
  destruct Hw as [E | [E | [E | [E | []]]]]; subst w; vm_compute; reflexivity.
Qed.

(* Independence with NO RANK DECISION: the Gram matrix of [Id, J] under  *)
(* <A, B> = tr(A^T B) is exactly d * I_2 = 2 * I_2, so the two-element   *)
(* End basis is independent over Z.                                     *)
Definition frob (A B : mat) : Z := mtrace (mmul (mtranspose A) B).

Definition gram (l : list mat) : mat := map (fun A => map (frob A) l) l.

Theorem c4E_end_gram_is_d_id : gram [mid 2; Jc4] = [[2; 0]; [0; 2]]%Z.
Proof. vm_compute. reflexivity. Qed.

Corollary c4E_end_gram_diagonal :
  gram [mid 2; Jc4] = map (map (Z.mul (Z.of_nat (ir_dim c4_E)))) (mid 2).
Proof. vm_compute. reflexivity. Qed.

(* THE TWO NEGATIVE CONTROLS of 3.6's oracle design, as refutations.     *)
(* (a) A spurious diag(1,-1) End column dies at R90: it does NOT commute *)
(* with C4's generator, so it is not an equivariant endomorphism and     *)
(* cannot pad the E block to e = 3.                                      *)
Example c4E_diag_not_equivariant :
  mmul Sref (word_mat c4_E [0]) <> mmul (word_mat c4_E [0]) Sref.
Proof. vm_compute. discriminate. Qed.

(* (b) J is not available at an R-type label: it fails to commute with   *)
(* D4's reflection generator, which is exactly why D4's E has e = 1 and  *)
(* the emitted basis there is [Id] alone.                                *)
Example d4E_J_not_equivariant :
  mmul Jc4 (word_mat d4_E [1]) <> mmul (word_mat d4_E [1]) Jc4.
Proof. vm_compute. discriminate. Qed.

(* ===================================================================== *)
(* PG6.  THE R-BURNSIDE TABLE-INTEGRITY TRAP: sum_i d_i^2 / e_i = |G|.   *)
(* Division-free in the BladeSymPower sense -- the quotient is EXHIBITED *)
(* and its exactness is a separate theorem, so a mis-typed e or a wrong  *)
(* dimension cannot hide behind a truncating division.                   *)
(* ===================================================================== *)

Definition rburnside_term (G : PointGroup) (R : Irrep) : nat :=
  Nat.div (ir_dim R * ir_dim R) (irrep_e G R).

Theorem c4_rburnside_exact : forall R, In R (pg_irreps c4) ->
  irrep_e c4 R * rburnside_term c4 R = ir_dim R * ir_dim R.
Proof.
  intros R HR. simpl in HR.
  destruct HR as [E | [E | [E | []]]]; subst R; vm_compute; reflexivity.
Qed.

Theorem d4_rburnside_exact : forall R, In R (pg_irreps d4) ->
  irrep_e d4 R * rburnside_term d4 R = ir_dim R * ir_dim R.
Proof.
  intros R HR. simpl in HR.
  destruct HR as [E | [E | [E | [E | [E | []]]]]]; subst R; vm_compute; reflexivity.
Qed.

Theorem c4_rburnside : lsum (map (rburnside_term c4) (pg_irreps c4)) = pg_order c4.
Proof. vm_compute. reflexivity. Qed.

Theorem d4_rburnside : lsum (map (rburnside_term d4) (pg_irreps d4)) = pg_order d4.
Proof. vm_compute. reflexivity. Qed.

Example rburnside_pins :
  (lsum (map (rburnside_term c4) (pg_irreps c4)),
   lsum (map (rburnside_term d4) (pg_irreps d4))) = (4, 8).
Proof. vm_compute. reflexivity. Qed.

(* ===================================================================== *)
(* PG7.  THE e-WEIGHTED COUNT OVER ENUMERATED BLOCK PAIRS.               *)
(*                                                                       *)
(* 3.6's FS FORMULA, stated once: over R-irreducible labels U_i with     *)
(* e_i = dim_R End_G(U_i),                                               *)
(*     dim_R Hom_G(+ m_i U_i, + n_i U_i) = sum_i m_i * n_i * e_i.        *)
(* The count is defined over the EXPLICIT block-pair enumeration         *)
(* pgHomBlocks emits, not over a fold that happens to compute it -- the  *)
(* s2_cells_spec discipline of BladeSymPower, and hom_blocks_spec is the *)
(* soundness-and-completeness characterization of that list.             *)
(* ===================================================================== *)

Definition PgSpec : Type := list (PgLabel * nat).

Definition hom_blocks (sin sout : PgSpec) : list (PgLabel * nat * nat) :=
  flat_map (fun p : PgLabel * nat =>
              map (fun q : PgLabel * nat => (fst p, snd p, snd q))
                  (filter (fun q : PgLabel * nat => lab_eqb (fst p) (fst q)) sout))
           sin.

Definition block_dim (ev : PgLabel -> nat) (b : PgLabel * nat * nat) : nat :=
  match b with (L, m, n) => m * n * ev L end.

Definition pg_hom_dim (ev : PgLabel -> nat) (sin sout : PgSpec) : nat :=
  lsum (map (block_dim ev) (hom_blocks sin sout)).

(* The emitted block list IS the set of matching label pairs. *)
Lemma hom_blocks_spec : forall sin sout L m n,
  In (L, m, n) (hom_blocks sin sout) <-> In (L, m) sin /\ In (L, n) sout.
Proof.
  intros sin sout L m n. unfold hom_blocks. rewrite in_flat_map. split.
  - intros (p & Hp & Hin). destruct p as [Lp mp].
    apply in_map_iff in Hin. destruct Hin as (q & Eq & Hq). destruct q as [Lq nq].
    apply filter_In in Hq. destruct Hq as [Hq Hlab]. cbn in Hlab, Eq.
    apply lab_eqb_eq in Hlab. subst Lq.
    injection Eq as E1 E2 E3. subst Lp mp nq. split; assumption.
  - intros [Hin1 Hin2]. exists (L, m). split; [exact Hin1 |].
    apply in_map_iff. exists (L, n). cbn. split; [reflexivity |].
    apply filter_In. cbn. split; [exact Hin2 | apply lab_eqb_refl].
Qed.

Lemma lsum_filter_guard : forall (A : Type) (h : A -> bool) (f : A -> nat) (l : list A),
  lsum (map f (filter h l)) = lsum (map (fun x => if h x then f x else 0) l).
Proof.
  intros A h f. induction l as [|x l IH]; cbn; [reflexivity |].
  destruct (h x); cbn; lia.
Qed.

(* THE GENERIC COUNT, written as the pairwise sum it is meant to be:     *)
(* sum over (in-entry, out-entry) pairs of m * n * e at matching labels. *)
Theorem pg_hom_dim_spec_sum : forall ev sin sout,
  pg_hom_dim ev sin sout
  = lsum (map (fun p : PgLabel * nat =>
                 lsum (map (fun q : PgLabel * nat =>
                              if lab_eqb (fst p) (fst q)
                              then snd p * snd q * ev (fst p) else 0)
                           sout))
              sin).
Proof.
  intros ev sin sout. unfold pg_hom_dim, hom_blocks.
  rewrite lsum_flat_map. f_equal. apply map_ext. intro p.
  rewrite map_map, lsum_filter_guard. reflexivity.
Qed.

(* Biadditivity: a direct sum on either side adds counts -- each          *)
(* multiplicity cell carries e scalars, independently of its neighbours.  *)
Theorem pg_hom_dim_add_l : forall ev s1 s2 sout,
  pg_hom_dim ev (s1 ++ s2) sout = pg_hom_dim ev s1 sout + pg_hom_dim ev s2 sout.
Proof.
  intros ev s1 s2 sout. unfold pg_hom_dim, hom_blocks.
  rewrite flat_map_app, map_app, lsum_app. reflexivity.
Qed.

Theorem pg_hom_dim_add_r : forall ev sin s1 s2,
  pg_hom_dim ev sin (s1 ++ s2) = pg_hom_dim ev sin s1 + pg_hom_dim ev sin s2.
Proof.
  intros ev sin s1 s2. unfold pg_hom_dim, hom_blocks.
  induction sin as [|p sin IH]; [reflexivity |].
  cbn [flat_map]. rewrite filter_app, !map_app, !lsum_app. lia.
Qed.

Corollary pg_hom_dim_single : forall ev L m n,
  pg_hom_dim ev [(L, m)] [(L, n)] = m * n * ev L.
Proof.
  intros ev L m n. unfold pg_hom_dim, hom_blocks.
  cbn [flat_map filter map fst snd app]. rewrite lab_eqb_refl. cbn. lia.
Qed.

(* --------------------------------------------------------------------- *)
(* 3.6's CONTRAST ANCHOR: one spec shape, [A x 1, E x 2] -> itself,       *)
(* sizes 9 at C4 and 5 at D4.  Same E dimension; the ONLY difference is   *)
(* e, and e here is e_of_fs of the computed Frobenius-Schur indicator.    *)
(* --------------------------------------------------------------------- *)

Definition spec_c4_AE : PgSpec := [(C4_A, 1); (C4_E, 2)].
Definition spec_d4_AE : PgSpec := [(D4_A1, 1); (D4_E, 2)].

Example c4_hom_blocks_AE :
  hom_blocks spec_c4_AE spec_c4_AE = [(C4_A, 1, 1); (C4_E, 2, 2)].
Proof. vm_compute. reflexivity. Qed.

Theorem pg_hom_dim_c4_contrast : pg_hom_dim (pg_ev c4) spec_c4_AE spec_c4_AE = 9.
Proof. vm_compute. reflexivity. Qed.

Theorem pg_hom_dim_d4_contrast : pg_hom_dim (pg_ev d4) spec_d4_AE spec_d4_AE = 5.
Proof. vm_compute. reflexivity. Qed.

(* The e == 1 naive-formula control: without the FS correction the C4    *)
(* count collapses onto the D4 one.  The FS indicator is the whole       *)
(* difference between 9 and 5 -- 3.6's thesis as one corpus diff.        *)
Example pg_hom_dim_c4_naive_control :
  pg_hom_dim (fun _ => 1) spec_c4_AE spec_c4_AE = 5.
Proof. vm_compute. reflexivity. Qed.

Corollary contrast_is_fs_only :
  pg_hom_dim (pg_ev c4) spec_c4_AE spec_c4_AE = 9
  /\ pg_hom_dim (pg_ev d4) spec_d4_AE spec_d4_AE = 5
  /\ pg_hom_dim (fun _ => 1) spec_c4_AE spec_c4_AE
     = pg_hom_dim (pg_ev d4) spec_d4_AE spec_d4_AE.
Proof.
  split; [apply pg_hom_dim_c4_contrast | split].
  - apply pg_hom_dim_d4_contrast.
  - vm_compute. reflexivity.
Qed.

(* The trivial-label anchor, for the invariantOffsets arm of 5b-ii: a    *)
(* spec of trivial-label copies counts m * n whatever the group.         *)
Example trivial_label_counts :
  (pg_hom_dim (pg_ev c4) [(C4_A, 3)] [(C4_A, 2)],
   pg_hom_dim (pg_ev d4) [(D4_A1, 3)] [(D4_A1, 2)]) = (6, 6).
Proof. vm_compute. reflexivity. Qed.

(* Labels that do not match contribute nothing: the block enumeration is *)
(* block-DIAGONAL, which is the reason Schur's lemma is what has to be   *)
(* cited and not re-derived.                                            *)
Example cross_label_blocks_empty :
  hom_blocks [(C4_A, 3)] [(C4_E, 2)] = [].
Proof. vm_compute. reflexivity. Qed.

(* ===================================================================== *)
(* Notes:                                                                *)
(*  - Scope.  Everything above is a FINITE INTEGER COMPUTATION over the  *)
(*    two shipped tables, exactly as 6.1's closure predicts; nothing is  *)
(*    quantified over groups.  The general facts the design leans on --  *)
(*    Schur over R (End_G(U) in {R, C, H}), hence that e in {1, 2, 4}    *)
(*    and that [Id] / [Id, J] EXHAUST End_G(U) -- are cited, and         *)
(*    discharged at the shipped witnesses by tests/Test_PgOracle.fs's    *)
(*    exact-rational Reynolds projector (the Test_PermOracle standard:   *)
(*    entrywise equality over Q, no tolerance).  What is proved here is  *)
(*    the independence half plus both negative controls.                 *)
(*  - The FS indicator is computed as sum_g tr(rho(g)^2) over the FIXED  *)
(*    word list, then divided by |G| with the exactness of that division *)
(*    a separate theorem.  Nothing reads a declared e: irrep_e is        *)
(*    e_of_fs of the computed indicator and pg_ev is the registry lookup *)
(*    over irrep_e, so the 9-vs-5 contrast is a chain from traces, not   *)
(*    three coincident asserts.                                          *)
(*  - Sharpness.  The word lists are FIXED DATA, so closure, the rep     *)
(*    property and the element count are claims about the enumeration    *)
(*    MLPointSpec actually ships.  A permuted Cayley table would fail    *)
(*    c4_rep_property / d4_rep_property; a mis-signed generator would    *)
(*    fail the relation checks; a wrong FsType entry would fail          *)
(*    _computed_eq_declared; and a wrong e would fail R-Burnside.  The   *)
(*    four traps are independent.                                        *)
(*  - The H (quaternionic, e = 4) branch of e_of_fs is reachable only    *)
(*    from an indicator of -1, which no single point group produces;     *)
(*    3.6 reserves the value for double groups rather than leaving a     *)
(*    dead field, and the counting theorems are uniform in e.            *)
(*  - Fusion multiplicity (E (x) E containing a label twice) needs a     *)
(*    CG-copy index and is 5b-iii; no tensor product appears here.       *)
(* ===================================================================== *)
