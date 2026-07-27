(* ===================================================================== *)
(* BladePartition.v -- SET PARTITIONS AS RESTRICTED GROWTH STRINGS: the   *)
(* counting and triangularity obligations of stage 5a-i                   *)
(* (docs/plan-transforms-as-types.md 3.6, staging item 5).                *)
(*                                                                        *)
(* What the compiler does, and what this file guards.  `derive_perm_      *)
(* linear(K, L, N, ...)` emits one loop nest per PARTITION of the K + L    *)
(* index positions; the basis element of partition gamma is its           *)
(* COARSENING INDICATOR B_gamma, which is 1 on an index tuple exactly     *)
(* when the tuple is constant on each block of gamma.  MLPermSpec must    *)
(* (i) enumerate the partitions, (ii) know how many there are before it   *)
(* allocates (`perm_weight_dim` / `perm_bias_dim`), and (iii) certify --  *)
(* with integers, no float and no rank decision -- that the emitted       *)
(* basis is independent.  Each obligation is a theorem here:              *)
(*                                                                        *)
(*  P1  THE ENUMERATION.  A partition of [0..m) is its restricted growth  *)
(*      string: g[0] = 0 and g[i] <= 1 + max of the prefix, so the        *)
(*      label set is always an initial segment and the string is the      *)
(*      partition's canonical name.  The enumeration is not a new         *)
(*      construction: RGS is the ARROW `heads b = seq 0 (S b)`,           *)
(*      `step b x = max b (S x)` (BladeArrow's enumA), so soundness,      *)
(*      completeness, duplicate-freedom and LEX-SORTEDNESS are the        *)
(*      tower's existing theorems instantiated (rgs_enum_sound /          *)
(*      _complete / _NoDup / _lex_sorted).  The partition enumerator is   *)
(*      the same coalgebra as the symmetric, antisymmetric, affine and    *)
(*      Compound enumerators -- one more member of the family, not a      *)
(*      parallel mechanism.                                               *)
(*                                                                        *)
(*      Counts.  The fibre of the block-count map is a Stirling number    *)
(*      of the second kind (rgs_enum_block_fibres), the whole             *)
(*      enumeration is Bell (rgs_enum_length), and the <= N-block filter  *)
(*      the compiler actually ships is sum_{j <= N} S(m, j)               *)
(*      (rgs_enum_le_count, rgs_enum_le_count_min).  Bell 0..6 =          *)
(*      1,1,2,5,15,52,203 are computed pins, as are 3.6's two anchors:    *)
(*      DeepSets = Bell 2 = 2 and Maron k = l = 2 = Bell 4 = 15 (+ bias   *)
(*      Bell 2 = 2).  perm_weight_dim errors below N = K + L in the       *)
(*      compiler; above it the count is Bell, which is                    *)
(*      perm_weight_dim_is_bell.                                          *)
(*                                                                        *)
(*  P2  THE KEYSTONE: rgs_lex_extends_refinement.  If gamma' COARSENS     *)
(*      gamma then gamma' is lex-<= gamma.  PROVED AS STATED -- the       *)
(*      coarsest-first convention of 3.6 stands and no convention swap    *)
(*      is needed.  The argument is a two-case analysis at the first      *)
(*      position i where the two strings differ (they share a prefix p,   *)
(*      hence the same prefix label count b): if gamma[i] < b then        *)
(*      gamma[i] already occurs in p, coarsening forces gamma'[i] to      *)
(*      equal gamma' at that earlier position -- which is gamma[i] --     *)
(*      contradicting "differ"; so gamma[i] = b is a NEW block, while     *)
(*      gamma'[i] <= b by its own growth bound, hence gamma'[i] < gamma[i]*)
(*      and the strings are lex-ordered at i.  Everything the argument    *)
(*      needs is here: rgs_values_cover (labels below the running count   *)
(*      really do occur in the prefix) and rgs_split_head (the growth     *)
(*      bound at a split).                                                *)
(*                                                                        *)
(*      The FALLBACK convention of 3.6 -- block count ascending, then     *)
(*      lex -- is also discharged, and without needing strictness:        *)
(*      coarsening is non-strictly block-decreasing (coarsens_blocks_le,  *)
(*      by pigeonhole over the induced label surjection) and P2 settles   *)
(*      the tie, so blocks_then_lex extends refinement too                *)
(*      (fallback_order_extends_refinement).  The F# side may pick        *)
(*      either order behind its one function; both are proved extensions. *)
(*                                                                        *)
(*  P3  THE WITNESS LEMMA, over the compiler's list (the s2_cells_spec    *)
(*      discipline of BladeSymPower: the theorem is about the list the    *)
(*      elaborator emits, not about a convenient abstract set).  A        *)
(*      partition's INDEPENDENCE CERTIFICATE is integer: take gamma's own *)
(*      RGS as its witness tuple and evaluate every basis indicator on    *)
(*      it.  B_spec says B gamma' t = true iff t is constant on gamma''s  *)
(*      blocks, i.e. iff t coarsens gamma'; at t = gamma this is exactly  *)
(*      the coarsening relation, so the witness-evaluation matrix is the  *)
(*      refinement matrix (witness_matrix_entry).  Its diagonal is true   *)
(*      (witness_diagonal) and, BY P2 AND THE LEX-SORTEDNESS OF P1, a     *)
(*      true entry forces row <= column (witness_matrix_unitriangular):   *)
(*      unitriangular under the emission order, hence invertible over Z,  *)
(*      hence the emitted basis is independent -- with no float and no    *)
(*      rank decision, as 3.6 requires.  witness_in_range checks the      *)
(*      other half of the certificate's legality: at N >= m every entry   *)
(*      of every witness is a legal Idx<N> value, which is the reason the *)
(*      static N >= K + L guard is a real precondition and not a          *)
(*      convenience.                                                      *)
(*                                                                        *)
(* ORIENTATION, pinned by computation rather than by prose.  3.6 writes   *)
(* "B_{gamma'}(RGS(gamma)) = 1 <-> gamma' <= gamma" with <= read as       *)
(* REFINEMENT (gamma' refines gamma).  Unfolded here that is              *)
(* `coarsens gamma gamma'` -- gamma is the coarser one -- and by P2 it    *)
(* implies gamma <=_lex gamma'.  witness_matrix_2 / _3 compute the        *)
(* matrices at m = 2, 3 so the triangle is fixed by a check, not by a     *)
(* reading: with rows indexed by WITNESS and columns by BASIS the matrix  *)
(* is UPPER unitriangular; transposing swaps the triangle and nothing     *)
(* else.                                                                  *)
(*                                                                        *)
(* NOT modelled, deliberately: that the coarsening indicators SPAN the    *)
(* space of S_n-equivariant maps.  Independence is what a compiler can    *)
(* certify per call and is proved here; completeness of the orbit basis   *)
(* is the cited half of 6.1(a), exactly as Schur is cited for the O(3)    *)
(* member.  Nothing here mentions characters, irreps or Kronecker         *)
(* coefficients -- 3.6's point is that the permutation-module tier does   *)
(* not need them.                                                         *)
(*                                                                        *)
(* Imports BladeDMWF (lsum register), BladeArrow (enumA and its           *)
(* soundness/completeness/NoDup theorems), BladeLex (lexlt and the        *)
(* StronglySorted plumbing).  Coq 8.18 / Rocq 9.0, stdlib only.           *)
(* ===================================================================== *)

From Blade Require Import BladeDMWF BladeArrow BladeLex.
Require Import List Arith Lia Sorted.
Import ListNotations.

(* ===================================================================== *)
(* RESTRICTED GROWTH STRINGS                                             *)
(* ===================================================================== *)

(* A partition of [0..m) IS its restricted growth string: position i      *)
(* carries the label of its block, labels are handed out left to right,   *)
(* and a position may either reuse a label already used to its left or    *)
(* open the next one.  `b` is the number of labels used so far, so the    *)
(* legal entries at a position are exactly 0 .. b.                        *)
Fixpoint rgs_ok (b : nat) (g : list nat) : Prop :=
  match g with
  | [] => True
  | x :: g' => x <= b /\ rgs_ok (Nat.max b (S x)) g'
  end.

Definition is_rgs (g : list nat) : Prop := rgs_ok 0 g.

(* b(gamma): the number of blocks.  0 for the empty string (m = 0 has one *)
(* partition, the empty one -- Bell 0 = 1), otherwise 1 + max.            *)
Fixpoint rgs_blocks_from (b : nat) (g : list nat) : nat :=
  match g with
  | [] => b
  | x :: g' => rgs_blocks_from (Nat.max b (S x)) g'
  end.

Definition rgs_blocks (g : list nat) : nat := rgs_blocks_from 0 g.

(* ===================================================================== *)
(* P1.  THE ENUMERATION IS AN ARROW.                                     *)
(* ===================================================================== *)

(* heads = the legal entries at the current state; step = the feedback    *)
(* (open a block or not).  This is BladeArrow's arrow signature verbatim, *)
(* so the partition enumerator joins Sym, Antisym, affine and Compound as *)
(* an instance rather than a new mechanism.                               *)
Definition rgs_heads (b : nat) : list nat := seq 0 (S b).
Definition rgs_step (b x : nat) : nat := Nat.max b (S x).

Definition rgs_enum (m : nat) : list (list nat) :=
  enumA nat rgs_heads rgs_step m 0.

Lemma in_rgs_heads : forall x b, In x (rgs_heads b) <-> x <= b.
Proof. intros x b. unfold rgs_heads. rewrite in_seq. lia. Qed.

(* The arrow's canonicity predicate IS restricted growth. *)
Lemma canonA_rgs : forall m b g,
  canonA nat rgs_heads rgs_step m b g <-> (length g = m /\ rgs_ok b g).
Proof.
  induction m as [|m IH]; intros b g; destruct g as [|x g'].
  - simpl. split; [intros _; split; [reflexivity | exact I] | intros _; exact I].
  - simpl. split; [contradiction | intros [H _]; discriminate].
  - simpl. split; [contradiction | intros [H _]; discriminate].
  - cbn [canonA rgs_ok length].
    rewrite (IH (rgs_step b x) g'), in_rgs_heads. unfold rgs_step.
    split; intros [H1 [H2 H3]]; (split; [lia | split; [lia | exact H3]]).
Qed.

Theorem rgs_enum_sound : forall m g,
  In g (rgs_enum m) -> length g = m /\ is_rgs g.
Proof.
  intros m g H. unfold is_rgs. apply canonA_rgs.
  apply (enumA_sound nat rgs_heads rgs_step). exact H.
Qed.

Theorem rgs_enum_complete : forall m g,
  length g = m -> is_rgs g -> In g (rgs_enum m).
Proof.
  intros m g Hl Hok. apply (enumA_complete nat rgs_heads rgs_step).
  apply canonA_rgs. split; assumption.
Qed.

Corollary rgs_enum_spec : forall m g,
  In g (rgs_enum m) <-> (length g = m /\ is_rgs g).
Proof.
  intros m g. split.
  - apply rgs_enum_sound.
  - intros [H1 H2]. apply rgs_enum_complete; assumption.
Qed.

Theorem rgs_enum_NoDup : forall m, NoDup (rgs_enum m).
Proof.
  intro m. apply (enumA_NoDup nat rgs_heads rgs_step).
  intro s. unfold rgs_heads. apply NoDup_seq.
Qed.

(* Coarsest-first emission order: the enumeration is strictly increasing  *)
(* in lex order, by BladeLex's general arrow theorem.                     *)
Theorem rgs_enum_lex_sorted : forall m, StronglySorted lexlt (rgs_enum m).
Proof.
  intro m. apply enumA_lex_sorted. intro s. unfold rgs_heads. apply SS_seq.
Qed.

Example rgs_enum_3 :
  rgs_enum 3 = [[0;0;0]; [0;0;1]; [0;1;0]; [0;1;1]; [0;1;2]].
Proof. reflexivity. Qed.

(* ===================================================================== *)
(* P1, COUNTS.  Stirling numbers of the second kind and Bell numbers.    *)
(* ===================================================================== *)

Fixpoint stirling2 (m j : nat) : nat :=
  match m with
  | 0 => match j with 0 => 1 | S _ => 0 end
  | S m' =>
      match j with
      | 0 => 0
      | S j' => S j' * stirling2 m' (S j') + stirling2 m' j'
      end
  end.

Definition bell (m : nat) : nat := lsum (map (stirling2 m) (seq 0 (S m))).

Example bell_pins :
  (bell 0, bell 1, bell 2, bell 3, bell 4, bell 5, bell 6)
  = (1, 1, 2, 5, 15, 52, 203).
Proof. reflexivity. Qed.

Lemma stirling2_zero_hi : forall m j, m < j -> stirling2 m j = 0.
Proof.
  induction m as [|m IH]; intros [|j] Hlt; try lia; [reflexivity |].
  cbn [stirling2]. rewrite (IH (S j)) by lia. rewrite (IH j) by lia. lia.
Qed.

(* The enumeration's own count, in the arrow's state variable: b labels   *)
(* are already open, m positions remain, j is the final block count.      *)
Fixpoint stir_open (b m j : nat) {struct m} : nat :=
  match m with
  | 0 => if Nat.eqb b j then 1 else 0
  | S m' => b * stir_open b m' j + stir_open (S b) m' j
  end.

Lemma stir_open_S : forall b m j,
  stir_open b (S m) j = b * stir_open b m j + stir_open (S b) m j.
Proof. reflexivity. Qed.

Lemma stir_open_zero : forall m b, stir_open b (S m) 0 = 0.
Proof.
  induction m as [|m IH]; intro b.
  - cbn. destruct b; cbn; lia.
  - rewrite stir_open_S, (IH b), (IH (S b)). lia.
Qed.

(* stir_open peels the FIRST position (that is how the arrow runs);       *)
(* Stirling's recurrence peels the LAST.  They agree.                     *)
Lemma stir_open_peel_last : forall m b j,
  stir_open b (S m) (S j) = S j * stir_open b m (S j) + stir_open b m j.
Proof.
  induction m as [|m IH]; intros b j.
  - cbn. destruct (Nat.eqb_spec b (S j)) as [E | _]; [subst b |]; cbn; lia.
  - assert (H1 : stir_open b (S m) (S j)
                 = S j * stir_open b m (S j) + stir_open b m j) by apply IH.
    assert (H2 : stir_open (S b) (S m) (S j)
                 = S j * stir_open (S b) m (S j) + stir_open (S b) m j)
      by apply IH.
    rewrite (stir_open_S b (S m) (S j)), H2, (stir_open_S b m j).
    rewrite H1 at 1. rewrite (stir_open_S b m (S j)). nia.
Qed.

Lemma stir_open_stirling : forall m j, stir_open 0 m j = stirling2 m j.
Proof.
  induction m as [|m IH]; intro j.
  - destruct j; reflexivity.
  - destruct j as [|j].
    + rewrite stir_open_zero. reflexivity.
    + rewrite stir_open_peel_last, (IH (S j)), (IH j). reflexivity.
Qed.

(* --- list plumbing for the fibre count --- *)

Lemma lsum_app : forall l1 l2, lsum (l1 ++ l2) = lsum l1 + lsum l2.
Proof.
  induction l1 as [|x l1 IH]; intro l2; cbn; [reflexivity | rewrite IH; lia].
Qed.

Lemma filter_flat_map :
  forall (A B : Type) (P : B -> bool) (F : A -> list B) (l : list A),
  filter P (flat_map F l) = flat_map (fun x => filter P (F x)) l.
Proof.
  induction l as [|x l IH]; simpl; [reflexivity | rewrite filter_app, IH; reflexivity].
Qed.

Lemma filter_map_cons :
  forall (P : list nat -> bool) (x : nat) (E : list (list nat)),
  filter P (map (cons x) E) = map (cons x) (filter (fun g => P (x :: g)) E).
Proof.
  intros P x. induction E as [|g E IH]; simpl; [reflexivity |].
  destruct (P (x :: g)); simpl; [rewrite IH; reflexivity | exact IH].
Qed.

(* THE FIBRE COUNT, over the compiler's list: the partitions the arrow    *)
(* emits with exactly j blocks number S(m, j).                            *)
Lemma rgs_fibre_count : forall m b j,
  length (filter (fun g => Nat.eqb (rgs_blocks_from b g) j)
                 (enumA nat rgs_heads rgs_step m b))
  = stir_open b m j.
Proof.
  induction m as [|m IH]; intros b j.
  - cbn. destruct (Nat.eqb b j); reflexivity.
  - cbn [enumA]. rewrite filter_flat_map, flat_map_length.
    rewrite (map_ext
      (fun x => length (filter (fun g => Nat.eqb (rgs_blocks_from b g) j)
                               (map (cons x)
                                    (enumA nat rgs_heads rgs_step m (rgs_step b x)))))
      (fun x => stir_open (rgs_step b x) m j)).
    2: { intro x. rewrite filter_map_cons, map_length. apply IH. }
    unfold rgs_heads. rewrite seq_S, map_app, lsum_app.
    rewrite (map_ext_in (fun x => stir_open (rgs_step b x) m j)
                        (fun _ => stir_open b m j)).
    2: { intros x Hx. apply in_seq in Hx. unfold rgs_step.
         replace (Nat.max b (S x)) with b by lia. reflexivity. }
    rewrite lsum_const, seq_length.
    cbn [map lsum]. unfold rgs_step.
    replace (Nat.max b (S (0 + b))) with (S b) by lia.
    rewrite stir_open_S. nia.
Qed.

Theorem rgs_enum_block_fibres : forall m j,
  length (filter (fun g => Nat.eqb (rgs_blocks g) j) (rgs_enum m))
  = stirling2 m j.
Proof.
  intros m j. unfold rgs_enum, rgs_blocks.
  rewrite rgs_fibre_count. apply stir_open_stirling.
Qed.

(* --- summing the fibres --- *)

Lemma lsum_delta_out : forall v N,
  N <= v -> lsum (map (fun j => if Nat.eqb v j then 1 else 0) (seq 0 N)) = 0.
Proof.
  intros v N. induction N as [|N IH]; intro H; [reflexivity |].
  rewrite seq_S, map_app, lsum_app, IH by lia.
  cbn [map lsum]. destruct (Nat.eqb_spec v (0 + N)); lia.
Qed.

Lemma lsum_delta : forall v N,
  v < N -> lsum (map (fun j => if Nat.eqb v j then 1 else 0) (seq 0 N)) = 1.
Proof.
  intros v N. induction N as [|N IH]; intro H; [lia |].
  rewrite seq_S, map_app, lsum_app. cbn [map lsum].
  destruct (Nat.eqb_spec v (0 + N)) as [E | Hne].
  - rewrite lsum_delta_out by lia. lia.
  - rewrite IH by lia. lia.
Qed.

Lemma lsum_map_add2 : forall (A : Type) (h1 h2 : A -> nat) (l : list A),
  lsum (map (fun x => h1 x + h2 x) l) = lsum (map h1 l) + lsum (map h2 l).
Proof.
  intros A h1 h2. induction l as [|x l IH]; simpl; [reflexivity | rewrite IH; lia].
Qed.

Lemma fibre_sum : forall (A : Type) (f : A -> nat) (N : nat) (l : list A),
  (forall x, In x l -> f x < N) ->
  lsum (map (fun j => length (filter (fun x => Nat.eqb (f x) j) l)) (seq 0 N))
  = length l.
Proof.
  intros A f N. induction l as [|a l IH]; intro Hb.
  - cbn [filter length]. rewrite lsum_const. lia.
  - rewrite (map_ext
      (fun j => length (filter (fun x => Nat.eqb (f x) j) (a :: l)))
      (fun j => (if Nat.eqb (f a) j then 1 else 0)
                + length (filter (fun x => Nat.eqb (f x) j) l))).
    2: { intro j. cbn [filter]. destruct (Nat.eqb (f a) j); reflexivity. }
    rewrite lsum_map_add2, lsum_delta by (apply Hb; left; reflexivity).
    rewrite IH by (intros x Hx; apply Hb; right; exact Hx).
    reflexivity.
Qed.

Lemma filter_le_fibres : forall (A : Type) (f : A -> nat) (N : nat) (l : list A),
  length (filter (fun x => Nat.leb (f x) N) l)
  = lsum (map (fun j => length (filter (fun x => Nat.eqb (f x) j) l)) (seq 0 (S N))).
Proof.
  intros A f N. induction l as [|a l IH].
  - cbn [filter length]. rewrite lsum_const. lia.
  - rewrite (map_ext
      (fun j => length (filter (fun x => Nat.eqb (f x) j) (a :: l)))
      (fun j => (if Nat.eqb (f a) j then 1 else 0)
                + length (filter (fun x => Nat.eqb (f x) j) l))).
    2: { intro j. cbn [filter]. destruct (Nat.eqb (f a) j); reflexivity. }
    rewrite lsum_map_add2, <- IH. cbn [filter].
    destruct (Nat.leb_spec (f a) N) as [Hle | Hgt]; cbn [length].
    + rewrite lsum_delta by lia. lia.
    + rewrite lsum_delta_out by lia. lia.
Qed.

(* Block counts are bounded by the length -- each position opens at most  *)
(* one block -- which is what makes the fibre decomposition finite.       *)
Lemma rgs_blocks_from_bound : forall g b,
  rgs_ok b g -> rgs_blocks_from b g <= b + length g.
Proof.
  induction g as [|x g IH]; intros b Hok; cbn [rgs_blocks_from length]; [lia |].
  destruct Hok as [Hx Hok].
  specialize (IH (Nat.max b (S x)) Hok). lia.
Qed.

Theorem rgs_enum_length : forall m, length (rgs_enum m) = bell m.
Proof.
  intro m. unfold bell.
  rewrite <- (fibre_sum (list nat) rgs_blocks (S m) (rgs_enum m)).
  - f_equal. apply map_ext. intro j. apply rgs_enum_block_fibres.
  - intros g Hg. apply rgs_enum_sound in Hg as [Hl Hok].
    unfold rgs_blocks. apply rgs_blocks_from_bound in Hok. lia.
Qed.

Example rgs_enum_lengths :
  (length (rgs_enum 0), length (rgs_enum 2), length (rgs_enum 4),
   length (rgs_enum 6)) = (1, 2, 15, 203).
Proof. reflexivity. Qed.

(* ===================================================================== *)
(* THE COMPILER'S SIZING BUILTINS                                        *)
(* ===================================================================== *)

(* Only partitions with at most N blocks are realizable over Idx<N>.      *)
Definition rgs_enum_le (N m : nat) : list (list nat) :=
  filter (fun g => Nat.leb (rgs_blocks g) N) (rgs_enum m).

Theorem rgs_enum_le_count : forall N m,
  length (rgs_enum_le N m) = lsum (map (stirling2 m) (seq 0 (S N))).
Proof.
  intros N m. unfold rgs_enum_le.
  rewrite filter_le_fibres. f_equal. apply map_ext. intro j.
  apply rgs_enum_block_fibres.
Qed.

Lemma lsum_seq_trunc : forall (h : nat -> nat) (a c : nat),
  (forall j, a <= j -> h j = 0) ->
  lsum (map h (seq 0 (a + c))) = lsum (map h (seq 0 a)).
Proof.
  intros h a. induction c as [|c IH]; intro Hz.
  - rewrite Nat.add_0_r. reflexivity.
  - replace (a + S c) with (S (a + c)) by lia.
    rewrite seq_S, map_app, lsum_app, IH by exact Hz.
    cbn [map lsum]. rewrite Hz by lia. lia.
Qed.

(* The <= N-block count is the truncated Stirling sum. *)
Corollary rgs_enum_le_count_min : forall N m,
  length (rgs_enum_le N m) = lsum (map (stirling2 m) (seq 0 (S (Nat.min N m)))).
Proof.
  intros N m. rewrite rgs_enum_le_count.
  destruct (Nat.le_gt_cases N m) as [Hle | Hgt].
  - rewrite (Nat.min_l N m) by lia. reflexivity.
  - rewrite (Nat.min_r N m) by lia.
    replace (S N) with (S m + (N - m)) by lia.
    apply lsum_seq_trunc. intros j Hj. apply stirling2_zero_hi. lia.
Qed.

(* perm_weight_dim(K, L, N) / perm_bias_dim(L, N): what MLPermSpec must   *)
(* return, and what it errors on below N = K + L.                         *)
Definition perm_weight_dim (K L N : nat) : nat := length (rgs_enum_le N (K + L)).
Definition perm_bias_dim (L N : nat) : nat := length (rgs_enum_le N L).

Corollary perm_weight_dim_is_bell : forall K L N,
  K + L <= N -> perm_weight_dim K L N = bell (K + L).
Proof.
  intros K L N H. unfold perm_weight_dim, bell.
  rewrite rgs_enum_le_count_min, (Nat.min_r N (K + L)) by lia. reflexivity.
Qed.

Corollary perm_bias_dim_is_bell : forall L N,
  L <= N -> perm_bias_dim L N = bell L.
Proof.
  intros L N H. unfold perm_bias_dim, bell.
  rewrite rgs_enum_le_count_min, (Nat.min_r N L) by lia. reflexivity.
Qed.

(* 3.6's anchors, computed.  DeepSets is K = L = 1 over n nodes: Bell 2   *)
(* = 2 weights (a*x + b*sum(x)*1).  Maron k = l = 2: Bell 4 = 15 weights, *)
(* plus Bell 2 = 2 biases.                                                *)
Example perm_weight_dim_deepsets : perm_weight_dim 1 1 2 = 2.
Proof. reflexivity. Qed.

Example perm_weight_dim_maron : perm_weight_dim 2 2 4 = 15.
Proof. reflexivity. Qed.

Example perm_bias_dim_maron : perm_bias_dim 2 2 = 2.
Proof. reflexivity. Qed.

(* Truncation really bites below N = K + L: at m = 3, N = 2 the           *)
(* three-block partition is dropped and 5 becomes S(3,1) + S(3,2) = 4.    *)
Example rgs_enum_le_truncates : (length (rgs_enum_le 2 3), length (rgs_enum 3)) = (4, 5).
Proof. reflexivity. Qed.

(* ===================================================================== *)
(* P2.  COARSENING AND THE EMISSION ORDER.                               *)
(* ===================================================================== *)

(* g' coarsens g: every block of g lies inside a block of g' -- read off  *)
(* the strings, positions that g identifies are identified by g' too.     *)
Definition coarsens (g' g : list nat) : Prop :=
  forall i j, i < length g -> j < length g ->
    nth i g 0 = nth j g 0 -> nth i g' 0 = nth j g' 0.

Lemma coarsens_refl : forall g, coarsens g g.
Proof. intros g i j _ _ H. exact H. Qed.

Definition lex_le (g1 g2 : list nat) : Prop := lexlt g1 g2 \/ g1 = g2.

Lemma lexlt_asym : forall l1 l2, lexlt l1 l2 -> lexlt l2 l1 -> False.
Proof.
  intros l1 l2 H. induction H as [y t | x y s t Hxy | x s t Hst IH];
    intro H2; inversion H2; subst; try lia. apply IH. assumption.
Qed.

Lemma lexlt_irrefl : forall l, ~ lexlt l l.
Proof. intros l H. exact (lexlt_asym l l H H). Qed.

Lemma first_diff : forall (l1 l2 : list nat),
  length l1 = length l2 -> l1 <> l2 ->
  exists p x y t1 t2,
    l1 = p ++ x :: t1 /\ l2 = p ++ y :: t2 /\ x <> y.
Proof.
  induction l1 as [|a l1 IH]; intros l2 Hl Hne.
  - destruct l2 as [|c l2]; [contradiction | cbn in Hl; discriminate].
  - destruct l2 as [|c l2]; cbn in Hl; [discriminate |].
    destruct (Nat.eq_dec a c) as [Eac | Nac].
    + subst c.
      assert (Hne' : l1 <> l2) by (intro E; subst; contradiction).
      assert (Hl' : length l1 = length l2) by lia.
      destruct (IH l2 Hl' Hne') as (p & x & y & t1 & t2 & E1 & E2 & Hxy).
      exists (a :: p), x, y, t1, t2. cbn.
      split; [rewrite E1; reflexivity |].
      split; [rewrite E2; reflexivity | exact Hxy].
    + exists [], a, c, l1, l2. cbn. split; [reflexivity |].
      split; [reflexivity | exact Nac].
Qed.

Lemma lexlt_prefix : forall p x y t1 t2,
  x < y -> lexlt (p ++ x :: t1) (p ++ y :: t2).
Proof.
  induction p as [|a p IH]; intros x y t1 t2 H; cbn.
  - apply lexlt_head. exact H.
  - apply lexlt_tail. apply IH. exact H.
Qed.

(* --- structural facts about restricted growth --- *)

Lemma rgs_blocks_from_app : forall p q b,
  rgs_blocks_from b (p ++ q) = rgs_blocks_from (rgs_blocks_from b p) q.
Proof.
  induction p as [|a p IH]; intros q b; cbn; [reflexivity | apply IH].
Qed.

Lemma rgs_ok_app_l : forall p q b, rgs_ok b (p ++ q) -> rgs_ok b p.
Proof.
  induction p as [|a p IH]; intros q b H; cbn in *; [exact I |].
  destruct H as [Ha H]. split; [exact Ha | exact (IH q _ H)].
Qed.

Lemma rgs_ok_app_r : forall p q b,
  rgs_ok b (p ++ q) -> rgs_ok (rgs_blocks_from b p) q.
Proof.
  induction p as [|a p IH]; intros q b H; cbn in *; [exact H |].
  destruct H as [_ H]. exact (IH q _ H).
Qed.

(* The growth bound, at a split: the entry just after a prefix is at most *)
(* the prefix's block count.                                              *)
Lemma rgs_split_head : forall p x t b,
  rgs_ok b (p ++ x :: t) -> x <= rgs_blocks_from b p.
Proof.
  intros p x t b H. apply rgs_ok_app_r in H. cbn in H. tauto.
Qed.

Lemma rgs_blocks_from_ge : forall g b, b <= rgs_blocks_from b g.
Proof.
  induction g as [|x g IH]; intro b; cbn; [lia |].
  specialize (IH (Nat.max b (S x))). lia.
Qed.

(* Labels below the running block count really do occur in the prefix --  *)
(* the restricted-growth condition makes the label set an initial segment.*)
Lemma rgs_values_cover : forall g b v,
  rgs_ok b g -> b <= v -> v < rgs_blocks_from b g ->
  exists j, j < length g /\ nth j g 0 = v.
Proof.
  induction g as [|x g IH]; intros b v Hok Hbv Hv; cbn in Hv.
  - lia.
  - destruct Hok as [Hx Hok].
    destruct (le_lt_dec (Nat.max b (S x)) v) as [Hge | Hlt].
    + destruct (IH (Nat.max b (S x)) v Hok Hge Hv) as (j & Hj & Hnth).
      exists (S j). cbn. split; [lia | exact Hnth].
    + exists 0. cbn. split; [lia | lia].
Qed.

Lemma rgs_entry_lt_blocks : forall g b v,
  rgs_ok b g -> In v g -> v < rgs_blocks_from b g.
Proof.
  induction g as [|x g IH]; intros b v Hok Hin; cbn in Hin; [contradiction |].
  destruct Hok as [Hx Hok]. cbn [rgs_blocks_from].
  destruct Hin as [E | Hin].
  - subst x. pose proof (rgs_blocks_from_ge g (Nat.max b (S v))). lia.
  - exact (IH (Nat.max b (S x)) v Hok Hin).
Qed.

(* --------------------------------------------------------------------- *)
(* THE KEYSTONE.                                                         *)
(* --------------------------------------------------------------------- *)

(* If gamma' coarsens gamma then gamma' comes first in the emission       *)
(* order.  RGS-lex EXTENDS refinement; the coarsest-first convention of   *)
(* 3.6 is sound as written.                                              *)
Theorem rgs_lex_extends_refinement : forall g1 g2,
  is_rgs g1 -> is_rgs g2 -> length g1 = length g2 ->
  coarsens g1 g2 -> lex_le g1 g2.
Proof.
  intros g1 g2 H1 H2 Hlen Hc.
  destruct (list_eq_dec Nat.eq_dec g1 g2) as [E | Hne];
    [right; exact E | left].
  destruct (first_diff g1 g2 Hlen Hne) as (p & x & y & t1 & t2 & E1 & E2 & Hxy).
  subst g1 g2. apply lexlt_prefix.
  (* both entries are bounded by the shared prefix's block count *)
  assert (Hx : x <= rgs_blocks_from 0 p) by (apply (rgs_split_head p x t1 0); exact H1).
  assert (Hy : y <= rgs_blocks_from 0 p) by (apply (rgs_split_head p y t2 0); exact H2).
  (* if y reused a label, coarsening would force x = y *)
  assert (Hnew : y = rgs_blocks_from 0 p).
  { destruct (Nat.eq_dec y (rgs_blocks_from 0 p)) as [E | Hlt]; [exact E |].
    exfalso.
    assert (Hyp : y < rgs_blocks_from 0 p) by lia.
    destruct (rgs_values_cover p 0 y (rgs_ok_app_l p (y :: t2) 0 H2)
                               (Nat.le_0_l y) Hyp) as (j & Hj & Hnth).
    assert (Hlp : length p < length (p ++ y :: t2))
      by (rewrite app_length; cbn; lia).
    assert (Hjl : j < length (p ++ y :: t2)) by lia.
    specialize (Hc (length p) j Hlp Hjl).
    rewrite nth_middle, (app_nth1 p (y :: t2) 0 Hj), Hnth in Hc.
    specialize (Hc eq_refl).
    rewrite nth_middle, (app_nth1 p (x :: t1) 0 Hj), Hnth in Hc.
    apply Hxy. exact Hc. }
  lia.
Qed.

(* --------------------------------------------------------------------- *)
(* THE FALLBACK CONVENTION, discharged too.                              *)
(* --------------------------------------------------------------------- *)

Fixpoint idx_of (v : nat) (g : list nat) : nat :=
  match g with
  | [] => 0
  | x :: g' => if Nat.eqb x v then 0 else S (idx_of v g')
  end.

Lemma idx_of_spec : forall g v,
  In v g -> idx_of v g < length g /\ nth (idx_of v g) g 0 = v.
Proof.
  induction g as [|x g IH]; intros v Hin; cbn in Hin; [contradiction |].
  cbn [idx_of length nth]. destruct (Nat.eqb_spec x v) as [E | Hne].
  - split; [lia | exact E].
  - assert (Hin' : In v g) by (destruct Hin as [E | H]; [contradiction | exact H]).
    destruct (IH v Hin') as [H1 H2]. split; [lia | exact H2].
Qed.

(* Coarsening cannot increase the block count: the induced map on labels  *)
(* (send gamma's label to gamma''s label at any position carrying it) is  *)
(* onto gamma''s labels, so pigeonhole bounds them.                       *)
Theorem coarsens_blocks_le : forall g1 g2,
  is_rgs g1 -> is_rgs g2 -> length g1 = length g2 ->
  coarsens g1 g2 -> rgs_blocks g1 <= rgs_blocks g2.
Proof.
  intros g1 g2 H1 H2 Hlen Hc.
  assert (Hincl : incl (seq 0 (rgs_blocks g1))
                       (map (fun v => nth (idx_of v g2) g1 0)
                            (seq 0 (rgs_blocks g2)))).
  { intros v' Hv'. apply in_seq in Hv'. destruct Hv' as [_ Hv'].
    destruct (rgs_values_cover g1 0 v' H1 (Nat.le_0_l v') Hv')
      as (i & Hi & Hnth).
    assert (HvIn : In (nth i g2 0) g2) by (apply nth_In; lia).
    destruct (idx_of_spec g2 (nth i g2 0) HvIn) as [Hj Hjn].
    apply in_map_iff. exists (nth i g2 0). split.
    - rewrite <- Hnth. apply Hc; [lia | lia | exact Hjn].
    - apply in_seq. split; [lia |].
      exact (rgs_entry_lt_blocks g2 0 (nth i g2 0) H2 HvIn). }
  apply NoDup_incl_length in Hincl; [| apply NoDup_seq].
  rewrite seq_length, map_length, seq_length in Hincl. exact Hincl.
Qed.

(* 3.6's fallback order (block count ascending, then lex) also extends    *)
(* refinement -- and needs no strictness argument, because P2 settles     *)
(* every tie.  Either convention is sound; the F# side may pick.          *)
Definition blocks_then_lex (g1 g2 : list nat) : Prop :=
  rgs_blocks g1 < rgs_blocks g2
  \/ (rgs_blocks g1 = rgs_blocks g2 /\ lex_le g1 g2).

Corollary fallback_order_extends_refinement : forall g1 g2,
  is_rgs g1 -> is_rgs g2 -> length g1 = length g2 ->
  coarsens g1 g2 -> blocks_then_lex g1 g2.
Proof.
  intros g1 g2 H1 H2 Hlen Hc. unfold blocks_then_lex.
  destruct (Nat.eq_dec (rgs_blocks g1) (rgs_blocks g2)) as [E | Hne].
  - right. split; [exact E | apply rgs_lex_extends_refinement; assumption].
  - left. pose proof (coarsens_blocks_le g1 g2 H1 H2 Hlen Hc). lia.
Qed.

(* ===================================================================== *)
(* P3.  THE WITNESS CERTIFICATE, over the compiler's list.               *)
(* ===================================================================== *)

(* The coarsening indicator of gamma', evaluated at an index tuple t:     *)
(* 1 exactly when t is constant on each block of gamma'.  This is the     *)
(* basis element derive_perm_linear emits as one b(gamma')-deep loop      *)
(* nest, read as a function of the index tuple.                           *)
Definition Bcell (g' t : list nat) (i j : nat) : bool :=
  if Nat.eqb (nth i g' 0) (nth j g' 0)
  then Nat.eqb (nth i t 0) (nth j t 0)
  else true.

Definition B (g' t : list nat) : bool :=
  forallb (fun i => forallb (fun j => Bcell g' t i j) (seq 0 (length g')))
          (seq 0 (length g')).

(* THE WITNESS LEMMA: the indicator's evaluation semantics is exactly the *)
(* coarsening relation -- t constant on gamma''s blocks means t (read as  *)
(* a partition) coarsens gamma'.                                          *)
Theorem B_spec : forall g' t, B g' t = true <-> coarsens t g'.
Proof.
  intros g' t. unfold B, coarsens. split.
  - intros H i j Hi Hj Heq.
    rewrite forallb_forall in H.
    assert (Hi' : In i (seq 0 (length g'))) by (apply in_seq; lia).
    specialize (H i Hi'). rewrite forallb_forall in H.
    assert (Hj' : In j (seq 0 (length g'))) by (apply in_seq; lia).
    specialize (H j Hj'). unfold Bcell in H.
    rewrite Heq, Nat.eqb_refl in H. apply Nat.eqb_eq. exact H.
  - intros H. apply forallb_forall. intros i Hi. apply in_seq in Hi.
    apply forallb_forall. intros j Hj. apply in_seq in Hj.
    unfold Bcell.
    destruct (Nat.eqb_spec (nth i g' 0) (nth j g' 0)) as [E | _];
      [| reflexivity].
    apply Nat.eqb_eq. apply H; [lia | lia | exact E].
Qed.

(* gamma's witness tuple is its own RGS.  The witness-evaluation matrix   *)
(* is therefore the refinement matrix: row = witness, column = basis.     *)
Definition witness_matrix (m : nat) : list (list bool) :=
  map (fun ga => map (fun gb => B gb ga) (rgs_enum m)) (rgs_enum m).

Corollary witness_matrix_entry : forall ga gb,
  B gb ga = true <-> coarsens ga gb.
Proof. intros. apply B_spec. Qed.

Corollary witness_diagonal : forall g, B g g = true.
Proof. intro g. apply B_spec. apply coarsens_refl. Qed.

(* UNITRIANGULARITY under the emission order.  A true entry at (row a,    *)
(* column b) forces a <= b: the certificate matrix is upper unitriangular *)
(* over Z, hence invertible, hence the emitted basis is independent --    *)
(* with no float and no rank decision.                                   *)
Theorem witness_matrix_unitriangular : forall m a b,
  a < length (rgs_enum m) -> b < length (rgs_enum m) ->
  B (nth b (rgs_enum m) []) (nth a (rgs_enum m) []) = true -> a <= b.
Proof.
  intros m a b Ha Hb HB.
  apply B_spec in HB.
  destruct (le_lt_dec a b) as [Hle | Hgt]; [exact Hle | exfalso].
  assert (Hga := nth_In (rgs_enum m) [] Ha).
  assert (Hgb := nth_In (rgs_enum m) [] Hb).
  apply rgs_enum_sound in Hga as [Hla Hoka].
  apply rgs_enum_sound in Hgb as [Hlb Hokb].
  assert (Hord : lexlt (nth b (rgs_enum m) []) (nth a (rgs_enum m) []))
    by (apply SS_nth; [apply rgs_enum_lex_sorted | exact Hgt | exact Ha]).
  destruct (rgs_lex_extends_refinement _ _ Hoka Hokb
              ltac:(lia) HB) as [Hlt | Heq].
  - exact (lexlt_asym _ _ Hlt Hord).
  - rewrite Heq in Hord. exact (lexlt_irrefl _ Hord).
Qed.

(* The diagonal in matrix form, and the two smallest matrices computed so *)
(* the triangle is fixed by a check rather than by a reading.             *)
Corollary witness_matrix_diagonal : forall m a,
  a < length (rgs_enum m) ->
  B (nth a (rgs_enum m) []) (nth a (rgs_enum m) []) = true.
Proof. intros. apply witness_diagonal. Qed.

Example witness_matrix_2 :
  witness_matrix 2 = [[true; true]; [false; true]].
Proof. reflexivity. Qed.

Example witness_matrix_3 :
  witness_matrix 3
  = [[true;  true;  true;  true;  true];
     [false; true;  false; false; true];
     [false; false; true;  false; true];
     [false; false; false; true;  true];
     [false; false; false; false; true]].
Proof. reflexivity. Qed.

(* The matrix is not vacuously triangular, and the modelled relation is   *)
(* really coarsening: at m = 4 it carries 60 true entries, and 60 is      *)
(* sum_j S(4, j) * Bell j = 1*1 + 7*2 + 6*5 + 1*15 -- the number of       *)
(* comparable pairs in the partition lattice of a 4-set, because the      *)
(* coarsenings of a j-block partition are the partitions of its j blocks. *)
(* Two independently computed routes to the same number; a mis-stated     *)
(* `coarsens` or a mis-stated indicator would not meet here.              *)
Definition matrix_true_count (M : list (list bool)) : nat :=
  lsum (map (fun r => length (filter (fun x => x) r)) M).

Example witness_matrix_4_density :
  (length (witness_matrix 4), matrix_true_count (witness_matrix 4),
   lsum (map (fun j => stirling2 4 j * bell j) (seq 0 5)))
  = (15, 60, 60).
Proof. vm_compute. reflexivity. Qed.

(* The other half of the certificate's legality: at N >= m every entry of *)
(* every witness tuple is a legal Idx<N> value.  This is why the static   *)
(* N >= K + L guard is a precondition and not a convenience -- below it   *)
(* the witnesses of the dropped partitions are not even expressible.      *)
Theorem witness_in_range : forall m N g v,
  In g (rgs_enum m) -> m <= N -> In v g -> v < N.
Proof.
  intros m N g v Hg HN Hv.
  apply rgs_enum_sound in Hg as [Hl Hok].
  pose proof (rgs_entry_lt_blocks g 0 v Hok Hv) as Hlt.
  pose proof (rgs_blocks_from_bound g 0 Hok) as Hb. lia.
Qed.

(* ===================================================================== *)
(* Notes:                                                                *)
(*  - Scope.  INDEPENDENCE of the emitted basis is proved (P3, by         *)
(*    unitriangularity over the emission order); SPANNING is not.  That   *)
(*    the coarsening indicators exhaust Hom_{S_n}(R^{n^K}, R^{n^L}) is    *)
(*    the orbit-counting half, cited under 6.1(a) exactly as Schur is     *)
(*    cited for the O(3) member.  The compiler's own check for the other  *)
(*    direction is numeric and per-corpus (the exact-rational Reynolds /  *)
(*    Gram oracle of 3.6), not a theorem.                                 *)
(*  - The whole file is about PLAIN Idx<N> powers.  No character, irrep   *)
(*    or Kronecker coefficient appears, which is 3.6's claim that the     *)
(*    permutation-module tier is character-free.                          *)
(*  - Sharpness.  m = 0 degenerates correctly: rgs_enum 0 = [[]],         *)
(*    rgs_blocks [] = 0, bell 0 = 1 -- the empty partition, counted once. *)
(*    P2 needs BOTH strings to be valid RGSs: coarsening alone does not   *)
(*    order arbitrary label strings, which is why the canonical naming    *)
(*    (rather than any labelling) is load-bearing for the certificate.    *)
(*  - rgs_enum_le is the truncated enumeration; every count theorem is    *)
(*    stated over it as well as over the full one, because the compiler   *)
(*    ships the truncated variant as a diagnostic-guarded deferral        *)
(*    (rgs_enum_le_truncates shows 5 -> 4 at m = 3, N = 2).               *)
(* ===================================================================== *)
