(* ===================================================================== *)
(* BladeSymPower.v -- symmetric-power counting for the transforms-as-     *)
(* types elaborator.                                                     *)
(*                                                                       *)
(* Three counting claims the ML elaborator makes about self-tensor and    *)
(* symmetric powers, checked here so its internal asserts have a proof    *)
(* behind them (docs/plan-transforms-as-types.md 3.2, 3.3, 3.3b):         *)
(*                                                                       *)
(*  T1  THE S2 PARTITION.  A self-tensor-product weight space splits into *)
(*      an exchange-symmetric and an exchange-antisymmetric half with no  *)
(*      parameter lost or double counted: a diagonal path's per-output    *)
(*      m x m multiplicity block contributes m(m+s)/2 to one half and     *)
(*      m(m-s)/2 to the other, and a mirror pair contributes q to each of *)
(*      its 2q dense parameters.  This is what sizes the compacted weight *)
(*      buffers -- MLSpec.fs s2TpWeightDimClosed, cross-checked against   *)
(*      the packed enumeration in s2TpWeightDim and asserted globally by  *)
(*      s2TpSplitIsPartition.  Division-free throughout: the halves are   *)
(*      EXHIBITED as the two triangle counts tri_le / tri_lt, whose       *)
(*      doubles are m(m+1) and m(m-1) (the register of BladeCauchy's      *)
(*      cauchy_cell_count).  The free cells are enumerated as             *)
(*      MLSpec.s2TpSkeleton enumerates them and characterized exactly     *)
(*      (s2_cells_spec), so the triangle counts are theorems about the    *)
(*      compiler's list and not about a convenient one.                   *)
(*                                                                       *)
(*  T2  COPY SPLITTING FOR Sym^k.  Splitting a spec into multiplicity-1   *)
(*      copies and factoring over degree compositions -- 3.3b's Move 1,   *)
(*      Sym^k(+_c U_c) = +_{sum k_c = k} (x)_c Sym^{k_c}(U_c) -- does not *)
(*      change the count:                                                 *)
(*        sum over compositions of prod_i C(n_i + k_i - 1, k_i)           *)
(*          = C(sum_i n_i + k - 1, k).                                    *)
(*      The right side is the cardinality MLSpec.powerSpec asserts on     *)
(*      every call, and BladeBinomial's storage_cardinality; so the       *)
(*      sector decomposition the k >= 2 kernels will be emitted from is   *)
(*      exhaustive and non-overlapping at the level of counts.  Stated    *)
(*      over an explicit enumeration of the compositions (sector_list),   *)
(*      not only over the fold that computes them.                        *)
(*                                                                       *)
(*  T3  THE Lambda TWIN: the same sum with C(n_i, k_i) is C(sum_i n_i, k) *)
(*      -- Vandermonde's identity, which the stdlib does not have (it     *)
(*      carries no natural-number binomial at all; BladeBinomial's C is   *)
(*      the tower's).  Guards alt_spec / altPowerSpec's cardinality       *)
(*      assert the way T2 guards sym_spec's.                              *)
(*                                                                       *)
(* Reused from BladeBinomial: C itself, C_zero, C_small,                  *)
(* storage_cardinality.  Pascal's rule and C 0 (S k) = 0 are definitional *)
(* for that C but unnamed there, so they are named here (C_pascal,        *)
(* C_zero_pos) rather than restated.                                      *)
(*                                                                       *)
(* NOT modelled: Clebsch-Gordan machinery.  A path is a two-constructor   *)
(* inductive carrying only the numbers the compaction rule reads, which   *)
(* is exactly what MLSpec.tpPaths + symTpKeptPaths hand it; the rule      *)
(* under test is arithmetic, and the representation theory that produces  *)
(* the paths is cited, not proved (plan 6.1).                             *)
(*                                                                       *)
(* Imports BladeDMWF, BladeBinomial.  Coq 8.18, stdlib only.              *)
(* ===================================================================== *)

From Blade Require Import BladeDMWF BladeBinomial.
Require Import List Arith Lia.
Import ListNotations.

(* ===================================================================== *)
(* Generic sum lemmas (stdlib gaps, in BladeDMWF's lsum register)         *)
(* ===================================================================== *)

Lemma lsum_cons : forall x l, lsum (x :: l) = x + lsum l.
Proof. reflexivity. Qed.

Lemma lsum_app : forall l1 l2, lsum (l1 ++ l2) = lsum l1 + lsum l2.
Proof.
  induction l1 as [|x l1 IH]; intro l2; simpl; [reflexivity | rewrite IH; lia].
Qed.

Lemma lsum_map_add : forall (A : Type) (h1 h2 : A -> nat) (l : list A),
  lsum (map (fun x => h1 x + h2 x) l) = lsum (map h1 l) + lsum (map h2 l).
Proof.
  intros A h1 h2. induction l as [|x l IH]; simpl; [reflexivity | rewrite IH; lia].
Qed.

Lemma lsum_map_scale : forall (A : Type) (c : nat) (h : A -> nat) (l : list A),
  lsum (map (fun x => c * h x) l) = c * lsum (map h l).
Proof.
  intros A c h. induction l as [|x l IH]; simpl.
  - rewrite Nat.mul_0_r. reflexivity.
  - rewrite IH, Nat.mul_add_distr_l. reflexivity.
Qed.

Lemma lsum_flat_map :
  forall (A B : Type) (h : B -> nat) (F : A -> list B) (l : list A),
  lsum (map h (flat_map F l)) = lsum (map (fun x => lsum (map h (F x))) l).
Proof.
  intros A B h F. induction l as [|x l IH]; simpl; [reflexivity |].
  rewrite map_app, lsum_app, IH. reflexivity.
Qed.

Lemma lsum_seq_shift : forall (h : nat -> nat) (len start : nat),
  lsum (map h (seq (S start) len)) = lsum (map (fun a => h (S a)) (seq start len)).
Proof. intros. rewrite <- seq_shift, map_map. reflexivity. Qed.

(* ===================================================================== *)
(* T1.  THE S2 PARTITION OF A SELF-TENSOR-PRODUCT WEIGHT SPACE.          *)
(* ===================================================================== *)

(* The transpose factor a component gives a path: tau = +1 leaves the     *)
(* diagonal multiplicity cells free, tau = -1 forces them to zero.  The   *)
(* symmetric component takes tau = sigma and the antisymmetric one        *)
(* tau = -sigma, so the two components of a path always carry flipped     *)
(* signs (MLSpec.transposeFactor).                                        *)
Inductive S2Sign : Type := SPlus | SMinus.

Definition sflip (t : S2Sign) : S2Sign :=
  match t with SPlus => SMinus | SMinus => SPlus end.

Lemma sflip_involutive : forall t, sflip (sflip t) = t.
Proof. destruct t; reflexivity. Qed.

(* The two triangle counts of an m x m block:                             *)
(*   tri_le m = #{(a, b) : a <= b < m},  tri_lt m = #{(a, b) : a < b < m}. *)
Fixpoint tri_le (m : nat) : nat :=
  match m with 0 => 0 | S m' => S m' + tri_le m' end.

Fixpoint tri_lt (m : nat) : nat :=
  match m with 0 => 0 | S m' => m' + tri_lt m' end.

Definition tri_sign (t : S2Sign) (m : nat) : nat :=
  match t with SPlus => tri_le m | SMinus => tri_lt m end.

(* The halves are well defined without ever dividing: their DOUBLES are   *)
(* the products the compaction rule writes as m(m+1)/2 and m(m-1)/2.      *)
Lemma tri_le_closed : forall m, 2 * tri_le m = m * (m + 1).
Proof.
  induction m as [|m IH]; [reflexivity |].
  replace (tri_le (S m)) with (S m + tri_le m) by reflexivity. nia.
Qed.

Lemma tri_lt_closed : forall m, 2 * tri_lt (S m) = S m * m.
Proof.
  induction m as [|m IH]; [reflexivity |].
  replace (tri_lt (S (S m))) with (S m + tri_lt (S m)) by reflexivity. nia.
Qed.

Lemma tri_lt_closed_sub : forall m, 2 * tri_lt m = m * (m - 1).
Proof.
  destruct m as [|m]; [reflexivity |].
  replace (S m - 1) with m by lia. apply tri_lt_closed.
Qed.

Lemma tri_partition : forall m, tri_le m + tri_lt m = m * m.
Proof.
  induction m as [|m IH]; [reflexivity |].
  replace (tri_le (S m)) with (S m + tri_le m) by reflexivity.
  replace (tri_lt (S m)) with (m + tri_lt m) by reflexivity.
  nia.
Qed.

Lemma tri_sign_partition : forall t m, tri_sign t m + tri_sign (sflip t) m = m * m.
Proof.
  intros t m. destruct t; simpl;
    [apply tri_partition | rewrite Nat.add_comm; apply tri_partition].
Qed.

(* T1, LOCAL FORM, division-free (compare BladeCauchy's                   *)
(* cauchy_cell_count): m(m+sigma) + m(m-sigma) = 2m^2 at sigma = +1 and   *)
(* sigma = -1 together, with the two halves exhibited as integers.        *)
Theorem s2_halves_partition : forall m,
  m * (m + 1) + m * (m - 1) = 2 * (m * m).
Proof.
  intro m. rewrite <- tri_le_closed, <- tri_lt_closed_sub, <- tri_partition. lia.
Qed.

Corollary s2_halves_well_defined : forall m,
  (exists h, m * (m + 1) = 2 * h) /\ (exists h, m * (m - 1) = 2 * h).
Proof.
  intro m. split; [exists (tri_le m) | exists (tri_lt m)];
    symmetry; [apply tri_le_closed | apply tri_lt_closed_sub].
Qed.

(* The FREE CELLS of one diagonal path's per-output-multiplicity block,   *)
(* exactly as MLSpec.s2TpSkeleton enumerates them:                        *)
(*   [ for u1 in 0 .. m-1 do                                              *)
(*       for u2 in (tau = +1 ? u1 : u1+1) .. m-1 -> (u1, u2) ]            *)
(* written with rem = m - u1 as the recursion variable so that the row    *)
(* bound is structural rather than a subtraction.                         *)
Fixpoint s2_cells_aux (t : S2Sign) (u1 rem : nat) : list (nat * nat) :=
  match rem with
  | 0 => []
  | S rem' =>
      map (fun u2 => (u1, u2))
          (match t with SPlus => seq u1 (S rem') | SMinus => seq (S u1) rem' end)
      ++ s2_cells_aux t (S u1) rem'
  end.

Definition s2_cells (t : S2Sign) (m : nat) : list (nat * nat) :=
  s2_cells_aux t 0 m.

Lemma s2_cells_aux_spec : forall t rem u1 a b,
  In (a, b) (s2_cells_aux t u1 rem) <->
  u1 <= a /\ a < u1 + rem /\ b < u1 + rem /\
  (match t with SPlus => a <= b | SMinus => a < b end).
Proof.
  intros t rem. induction rem as [|rem IH]; intros u1 a b.
  - simpl. split; [contradiction | intros (H1 & H2 & _); lia].
  (* cbn, not simpl: simpl would unfold the seq below and break in_seq *)
  - cbn [s2_cells_aux]. rewrite in_app_iff, in_map_iff, (IH (S u1) a b).
    destruct t.
    + split.
      * intros [(u2 & E & Hu2) | H].
        -- injection E as E1 E2. subst. rewrite in_seq in Hu2. lia.
        -- lia.
      * intros (H1 & H2 & H3 & H4).
        destruct (Nat.eq_dec a u1) as [Ea | Hne].
        -- subst a. left. exists b. split; [reflexivity |].
           apply in_seq. lia.
        -- right. lia.
    + split.
      * intros [(u2 & E & Hu2) | H].
        -- injection E as E1 E2. subst. rewrite in_seq in Hu2. lia.
        -- lia.
      * intros (H1 & H2 & H3 & H4).
        destruct (Nat.eq_dec a u1) as [Ea | Hne].
        -- subst a. left. exists b. split; [reflexivity |].
           apply in_seq. lia.
        -- right. lia.
Qed.

(* The compaction's free cells ARE the tau-triangle -- soundness and      *)
(* completeness together, in the register of BladeDMWF's enum_sound /     *)
(* enum_complete.                                                         *)
Lemma s2_cells_spec : forall t m a b,
  In (a, b) (s2_cells t m) <->
  a < m /\ b < m /\ (match t with SPlus => a <= b | SMinus => a < b end).
Proof.
  intros t m a b. unfold s2_cells. rewrite s2_cells_aux_spec. simpl.
  split.
  - intros (_ & H2 & H3 & H4). tauto.
  - intros (H1 & H2 & H3). split; [lia | tauto].
Qed.

Lemma s2_cells_aux_length : forall t rem u1,
  length (s2_cells_aux t u1 rem) = tri_sign t rem.
Proof.
  intros t rem. induction rem as [|rem IH]; intros u1; destruct t; simpl;
    try reflexivity;
    rewrite app_length, map_length, seq_length, IH; reflexivity.
Qed.

Lemma s2_cells_length : forall t m, length (s2_cells t m) = tri_sign t m.
Proof. intros. apply s2_cells_aux_length. Qed.

(* T1, CELL FORM: the two components' free cells exactly account for the  *)
(* dense m x m block -- the closed triangle of one plus the (transposed)  *)
(* strict triangle of the other.                                          *)
Theorem s2_cells_partition : forall t m,
  length (s2_cells t m) + length (s2_cells (sflip t) m) = m * m.
Proof.
  intros t m. rewrite !s2_cells_length. apply tri_sign_partition.
Qed.

(* A kept path of the compaction, carrying only the numbers the rule      *)
(* reads (MLSpec.symTpKeptPaths):                                         *)
(*  - MirrorPair q: an off-diagonal path b1 < b2 together with the mirror *)
(*    (b2, b1, bo) the compaction drops.  q = multOut * m1 * m2 cells go  *)
(*    to EACH component out of the pair's 2q dense parameters.            *)
(*  - DiagPath c m t: a diagonal path b1 = b2 with output multiplicity c, *)
(*    block multiplicity m, and transpose factor t for the symmetric      *)
(*    component (hence sflip t for the antisymmetric one).                *)
Inductive S2Path : Type :=
  | MirrorPair (q : nat)
  | DiagPath (c m : nat) (t : S2Sign).

Definition sym_dim (p : S2Path) : nat :=
  match p with
  | MirrorPair q => q
  | DiagPath c m t => c * tri_sign t m
  end.

Definition alt_dim (p : S2Path) : nat :=
  match p with
  | MirrorPair q => q
  | DiagPath c m t => c * tri_sign (sflip t) m
  end.

Definition dense_dim (p : S2Path) : nat :=
  match p with
  | MirrorPair q => 2 * q
  | DiagPath c m _ => c * (m * m)
  end.

Definition sym_total (ps : list S2Path) : nat := lsum (map sym_dim ps).
Definition alt_total (ps : list S2Path) : nat := lsum (map alt_dim ps).
Definition dense_total (ps : list S2Path) : nat := lsum (map dense_dim ps).

Lemma s2_path_partition : forall p, sym_dim p + alt_dim p = dense_dim p.
Proof.
  destruct p as [q | c m t]; simpl; [lia |].
  rewrite <- Nat.mul_add_distr_l, tri_sign_partition. reflexivity.
Qed.

(* T1, GLOBAL FORM: MLSpec.s2TpSplitIsPartition --                        *)
(* symTpWeightDim s + altTpWeightDim s = tpWeightDim (selfTpConfig s),    *)
(* over any list of kept paths.                                           *)
Theorem s2_split_is_partition : forall ps,
  sym_total ps + alt_total ps = dense_total ps.
Proof.
  unfold sym_total, alt_total, dense_total.
  induction ps as [|p ps IH]; simpl; [reflexivity |].
  assert (H := s2_path_partition p). lia.
Qed.

(* The two worked counts of plan-transforms-as-types 3.2, as path lists.  *)
(* Count 1, s = [(0,e,1); (1,o,1)]: five kept paths -- the diagonal       *)
(* (0,0,0), the mirror pair (0,1,2) || (1,0,2), and the three diagonals   *)
(* (1,1,0), (1,1,1), (1,1,3) -- 10 dense parameters splitting 7 + 3, the  *)
(* Schur cross-check in the doc.  Count 2, s = [(1,o,2)]: three diagonal  *)
(* paths at output l = 0, 1, 2 with multiplicity 2, 48 splitting 28 + 20  *)
(* (12+4+12 and 4+12+4) -- the Cauchy count, whose r = 2 arithmetic       *)
(* shadow is BladeCauchy's cauchy_cell_count.                             *)
Example s2_worked_count_1 :
  let ps := [DiagPath 2 1 SPlus; MirrorPair 2; DiagPath 2 1 SPlus;
             DiagPath 1 1 SMinus; DiagPath 1 1 SPlus] in
  (sym_total ps, alt_total ps, dense_total ps) = (7, 3, 10).
Proof. reflexivity. Qed.

Example s2_worked_count_2 :
  let ps := [DiagPath 4 2 SPlus; DiagPath 4 2 SMinus; DiagPath 4 2 SPlus] in
  (sym_total ps, alt_total ps, dense_total ps) = (28, 20, 48).
Proof. reflexivity. Qed.

(* ===================================================================== *)
(* Convolution machinery for the two Vandermonde identities.             *)
(* ===================================================================== *)

(* sum over a + b = k of f a * g b, indexed by the first component.       *)
Definition conv (f g : nat -> nat) (k : nat) : nat :=
  lsum (map (fun a => f a * g (k - a)) (seq 0 (S k))).

Lemma conv_deg0 : forall f g, conv f g 0 = f 0 * g 0.
Proof. intros. unfold conv. simpl. apply Nat.add_0_r. Qed.

(* Peeling the a = 0 term: the tail is the convolution of the shifted f.  *)
Lemma conv_cons : forall f g k,
  conv f g (S k) = f 0 * g (S k) + conv (fun a => f (S a)) g k.
Proof.
  intros f g k. unfold conv.
  change (seq 0 (S (S k))) with (0 :: seq 1 (S k)).
  rewrite map_cons, lsum_cons, lsum_seq_shift. reflexivity.
Qed.

Lemma conv_ext_l : forall f1 f2 g k,
  (forall a, f1 a = f2 a) -> conv f1 g k = conv f2 g k.
Proof.
  intros f1 f2 g k H. unfold conv. f_equal. apply map_ext.
  intro a. rewrite H. reflexivity.
Qed.

Lemma conv_ext_r : forall f g1 g2 k,
  (forall a, g1 a = g2 a) -> conv f g1 k = conv f g2 k.
Proof.
  intros f g1 g2 k H. unfold conv. f_equal. apply map_ext.
  intro a. rewrite H. reflexivity.
Qed.

Lemma conv_add_l : forall f1 f2 g k,
  conv (fun a => f1 a + f2 a) g k = conv f1 g k + conv f2 g k.
Proof.
  intros f1 f2 g k. unfold conv.
  transitivity (lsum (map (fun a => f1 a * g (k - a) + f2 a * g (k - a))
                          (seq 0 (S k)))).
  - f_equal. apply map_ext. intro a. apply Nat.mul_add_distr_r.
  - apply lsum_map_add.
Qed.

Lemma conv_zero_l : forall g k, conv (fun _ => 0) g k = 0.
Proof.
  intros g k. unfold conv.
  transitivity (lsum (map (fun _ : nat => 0) (seq 0 (S k)))).
  - f_equal; apply map_ext; intro a; reflexivity.
  - rewrite lsum_const. lia.
Qed.

(* A delta sequence is the convolution unit -- the degenerate copy list.  *)
Lemma conv_delta : forall f g k,
  f 0 = 1 -> (forall a, f (S a) = 0) -> conv f g k = g k.
Proof.
  intros f g k H0 HS. destruct k as [|k].
  - rewrite conv_deg0, H0. lia.
  - rewrite conv_cons, H0.
    rewrite (conv_ext_l (fun a => f (S a)) (fun _ => 0) g k HS), conv_zero_l.
    lia.
Qed.

(* --- what BladeBinomial's C leaves unnamed --- *)

Lemma C_pascal : forall n k, C (S n) (S k) = C n k + C n (S k).
Proof. reflexivity. Qed.

Lemma C_zero_pos : forall k, C 0 (S k) = 0.
Proof. reflexivity. Qed.

(* ===================================================================== *)
(* T3.  VANDERMONDE'S IDENTITY (the Lambda^k two-copy core).             *)
(* ===================================================================== *)

Theorem vandermonde : forall p q k, conv (C p) (C q) k = C (p + q) k.
Proof.
  induction p as [|p IH]; intros q k.
  - apply conv_delta; [apply C_zero | exact C_zero_pos].
  - destruct k as [|k].
    + rewrite conv_deg0, !C_zero. reflexivity.
    + rewrite conv_cons.
      rewrite (conv_ext_l (fun a => C (S p) (S a)) (fun a => C p a + C p (S a)))
        by (intro a; apply C_pascal).
      rewrite conv_add_l.
      assert (Hfold : conv (C p) (C q) (S k)
                      = C p 0 * C q (S k) + conv (fun a => C p (S a)) (C q) k)
        by apply conv_cons.
      rewrite (IH q (S k)) in Hfold.
      rewrite (IH q k).
      rewrite !C_zero. rewrite !C_zero in Hfold.
      replace (S p + q) with (S (p + q)) by lia.
      rewrite (C_pascal (p + q) k). lia.
Qed.

(* ===================================================================== *)
(* T2.  THE MULTISET TWIN (the Sym^k two-copy core).                     *)
(* ===================================================================== *)

(* The multiset coefficient: dim Sym^k of an n-dimensional space, which   *)
(* BladeBinomial's storage_cardinality identifies with |SymIdx<k, n>|.    *)
Definition MC (n k : nat) : nat := C (n + k - 1) k.

Lemma MC_deg0 : forall n, MC n 0 = 1.
Proof. intro n. unfold MC. apply C_zero. Qed.

Lemma MC_0 : forall k, MC 0 k = C 0 k.
Proof. intros [|k]; unfold MC; simpl; [reflexivity | apply C_small; lia]. Qed.

Lemma MC_pascal : forall n k, MC (S n) (S k) = MC (S n) k + MC n (S k).
Proof.
  intros n k. unfold MC.
  replace (S n + S k - 1) with (S (n + k)) by lia.
  replace (S n + k - 1) with (n + k) by lia.
  replace (n + S k - 1) with (n + k) by lia.
  apply C_pascal.
Qed.

Theorem multiset_vandermonde : forall n m k,
  conv (MC n) (MC m) k = MC (n + m) k.
Proof.
  induction n as [|n IHn]; intros m k.
  - rewrite Nat.add_0_l. apply conv_delta.
    + apply MC_deg0.
    + intro a. rewrite MC_0. apply C_zero_pos.
  - induction k as [|k IHk].
    + rewrite conv_deg0, !MC_deg0. reflexivity.
    + rewrite conv_cons.
      rewrite (conv_ext_l (fun a => MC (S n) (S a))
                          (fun a => MC (S n) a + MC n (S a)))
        by (intro a; apply MC_pascal).
      rewrite conv_add_l.
      assert (Hfold : conv (MC n) (MC m) (S k)
                      = MC n 0 * MC m (S k) + conv (fun a => MC n (S a)) (MC m) k)
        by apply conv_cons.
      rewrite (IHn m (S k)) in Hfold.
      rewrite IHk.
      rewrite !MC_deg0. rewrite !MC_deg0 in Hfold.
      replace (S n + m) with (S (n + m)) by lia.
      rewrite (MC_pascal (n + m) k). lia.
Qed.

(* ===================================================================== *)
(* T2 / T3 over a COPY LIST: the composition-sector count.               *)
(* ===================================================================== *)

(* Split degree k across the copies ns = [n_1; ...; n_c], weight each     *)
(* sector by the per-copy dimension f n_i k_i, and add.  At f = MC this   *)
(* is 3.3b's Move 1 (Sym^k of a direct sum factors over degree            *)
(* compositions); at f = C it is the exterior twin.                       *)
Fixpoint sector_sum (f : nat -> nat -> nat) (ns : list nat) (k : nat) : nat :=
  match ns with
  | [] => match k with 0 => 1 | S _ => 0 end
  | n :: ns' => conv (f n) (sector_sum f ns') k
  end.

Lemma sector_sum_ext : forall f1 f2 ns k,
  (forall n j, f1 n j = f2 n j) -> sector_sum f1 ns k = sector_sum f2 ns k.
Proof.
  intros f1 f2 ns. induction ns as [|n ns IH]; intros k H; simpl; [reflexivity |].
  rewrite (conv_ext_l (f1 n) (f2 n)) by (intro a; apply H).
  apply conv_ext_r. intro a. apply IH. exact H.
Qed.

(* T2, closed form. *)
Theorem sym_sector_count : forall ns k, sector_sum MC ns k = MC (lsum ns) k.
Proof.
  induction ns as [|n ns IH]; intro k; simpl.
  - destruct k as [|k]; [symmetry; apply MC_deg0 | rewrite MC_0; reflexivity].
  - rewrite (conv_ext_r (MC n) (sector_sum MC ns) (MC (lsum ns)))
      by (intro a; apply IH).
    apply multiset_vandermonde.
Qed.

(* T3, closed form. *)
Theorem alt_sector_count : forall ns k, sector_sum C ns k = C (lsum ns) k.
Proof.
  induction ns as [|n ns IH]; intro k; simpl.
  - destruct k as [|k]; reflexivity.
  - rewrite (conv_ext_r (C n) (sector_sum C ns) (C (lsum ns)))
      by (intro a; apply IH).
    apply vandermonde.
Qed.

(* The compositions themselves: sector_list ns k enumerates the degree    *)
(* vectors (k_1, ..., k_c) with sum k_i = k, ascending in k_1 -- the      *)
(* sector labels of 3.3b -- and sector_weight multiplies the per-copy     *)
(* dimensions along one of them.  With these, the theorem below is        *)
(* literally "sum over compositions of the product", not a fold that      *)
(* happens to compute it.                                                 *)
Fixpoint sector_list (ns : list nat) (k : nat) : list (list nat) :=
  match ns with
  | [] => match k with 0 => [[]] | S _ => [] end
  | _ :: ns' =>
      flat_map (fun a => map (cons a) (sector_list ns' (k - a))) (seq 0 (S k))
  end.

Fixpoint sector_weight (f : nat -> nat -> nat) (ns ks : list nat) : nat :=
  match ns, ks with
  | n :: ns', a :: ks' => f n a * sector_weight f ns' ks'
  | _, _ => 1
  end.

Lemma sector_sum_expand : forall f ns k,
  lsum (map (sector_weight f ns) (sector_list ns k)) = sector_sum f ns k.
Proof.
  intros f ns. induction ns as [|n ns IH]; intro k.
  - destruct k; reflexivity.
  - replace (sector_list (n :: ns) k)
      with (flat_map (fun a => map (cons a) (sector_list ns (k - a)))
                     (seq 0 (S k)))
      by reflexivity.
    replace (sector_sum f (n :: ns) k) with (conv (f n) (sector_sum f ns) k)
      by reflexivity.
    rewrite lsum_flat_map. unfold conv. f_equal. apply map_ext. intro a.
    rewrite map_map.
    transitivity (lsum (map (fun ks => f n a * sector_weight f ns ks)
                            (sector_list ns (k - a)))).
    + f_equal; apply map_ext; intro ks; reflexivity.
    + rewrite lsum_map_scale, IH. reflexivity.
Qed.

(* T2, THE DELIVERABLE: copy splitting is dimension-preserving.  The sum  *)
(* over degree compositions of the per-copy multiset counts is the        *)
(* multiset count of the whole -- the cardinality MLSpec.powerSpec        *)
(* asserts on every call.                                                 *)
Theorem sym_copy_splitting : forall ns k,
  lsum (map (sector_weight MC ns) (sector_list ns k)) = C (lsum ns + k - 1) k.
Proof. intros. rewrite sector_sum_expand, sym_sector_count. reflexivity. Qed.

(* T3, THE DELIVERABLE: the exterior twin, guarding altPowerSpec.         *)
Theorem alt_copy_splitting : forall ns k,
  lsum (map (sector_weight C ns) (sector_list ns k)) = C (lsum ns) k.
Proof. intros. rewrite sector_sum_expand. apply alt_sector_count. Qed.

(* And the same statement at the level of the tower's own enumeration:    *)
(* per-copy symmetric storage, summed over sectors, is the joint          *)
(* symmetric storage (BladeBinomial's storage_cardinality on both ends).  *)
Corollary sym_sector_enum : forall ns k,
  sector_sum (fun n j => length (enum j 0 n)) ns k = length (enum k 0 (lsum ns)).
Proof.
  intros ns k.
  rewrite (sector_sum_ext (fun n j => length (enum j 0 n)) MC)
    by (intros n j; unfold MC; apply storage_cardinality).
  rewrite sym_sector_count, storage_cardinality. reflexivity.
Qed.

(* Two copies of dimensions 2 and 3 at degree 2: the three sectors        *)
(* (0,2), (1,1), (2,0) carry 6 + 6 + 3 = 15 = C(5+2-1, 2) monomials, and  *)
(* 3 + 6 + 1 = 10 = C(5, 2) exterior cells.                               *)
Example sector_list_2_3 : sector_list [2; 3] 2 = [[0; 2]; [1; 1]; [2; 0]].
Proof. reflexivity. Qed.

Example sym_sector_2_3 :
  lsum (map (sector_weight MC [2; 3]) (sector_list [2; 3] 2)) = 15.
Proof. reflexivity. Qed.

Example alt_sector_2_3 :
  lsum (map (sector_weight C [2; 3]) (sector_list [2; 3] 2)) = 10.
Proof. reflexivity. Qed.

(* ===================================================================== *)
(* Notes:                                                                *)
(*  - Scope.  These are COUNTING theorems.  T1 says the compaction loses  *)
(*    and duplicates nothing; it does not say the kept cells parameterize *)
(*    the equivariant maps -- that is Schur, cited (plan 6.1), and the    *)
(*    compiler pins it numerically against the dense kernel instead       *)
(*    (stage 1a/1b, ml-equiv/032-035).  T2 and T3 likewise count the      *)
(*    sectors; that the sectors are ORTHOGONAL subspaces is 3.3b's        *)
(*    construction, not proved here.                                      *)
(*  - T1's global form is stated over an arbitrary path list, so it holds *)
(*    for whatever MLSpec.tpPaths produces; no triangle inequality or     *)
(*    parity rule is needed, because the partition is insensitive to      *)
(*    which paths are valid.                                              *)
(*  - The cross-stage identity the compiler checks --                     *)
(*    poly_weight_dim(s, 2, tp_spec(s,s)) = sym_tp_weight_dim(s) -- is    *)
(*    NOT reachable from here: it equates two counts computed through      *)
(*    Clebsch-Gordan data, which this file deliberately does not model.   *)
(*    It stays a compiler-side sweep (stage 2a, 15 specs to mult 4).      *)
(*  - Sharpness: nothing is assumed of m, k, or the copy list.  At m = 0  *)
(*    or k = 0 every statement degenerates correctly (tri_lt 0 = 0 is why *)
(*    the m = 1 antisymmetric diagonal path carries no parameter at all   *)
(*    and is dropped from the emitted kernel).                            *)
(* ===================================================================== *)
