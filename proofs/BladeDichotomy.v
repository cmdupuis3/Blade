(* ===================================================================== *)
(* BladeDichotomy.v -- the r >= 3 storage dichotomy.                     *)
(*                                                                       *)
(* BladeCauchy.v proves the r = 2 split: 2T = Psym + Qalt, both          *)
(* components per-dimension triangular, T recovered anywhere with a      *)
(* sign.  This file closes the question that file left open -- higher    *)
(* ranks involve mixed symmetry types -- NEGATIVELY at r = 3, and pins   *)
(* the repair.  Prose development with the general-r statements:         *)
(* the rank-dichotomy document (2026-08-02); the general theorem is      *)
(* that the minimal width of a scalar-access per-dimension-canonical     *)
(* scheme is r! (the fiber over a free canonical cell carries the        *)
(* regular representation; scalar weights invert only 1x1 blocks; S_r    *)
(* has exactly two linear characters; 2 = r! iff r <= 2).                *)
(*                                                                       *)
(* Checked here, all at r = 3:                                           *)
(*  - PART A (the witness): an explicit integer tensor at extent 2,      *)
(*    slot-pair symmetric, NONZERO, whose sym AND alt components both    *)
(*    vanish identically -- the natural generalization of BladeCauchy's  *)
(*    split confuses it with the zero tensor.  Two-line refutation in    *)
(*    the register of BladeCounting's witness_not_in_image.              *)
(*  - PART B (the lower-bound core): NO two weight functions             *)
(*    eps1, eps2 : S_3 x S_3 -> R, over ANY commutative ring R with      *)
(*    1 <> 0, can reconstruct every symmetric tensor from two scalars    *)
(*    per canonical cell.  Three indicator tensors put three rows of an  *)
(*    identity matrix inside a rank-2 span; the 3x3 determinant kills    *)
(*    it.  Weights arbitrary, storage arbitrary (not even linearity is   *)
(*    assumed) -- the constraint lives entirely in the weight span.      *)
(*  - PART C (the repair): (i) the six-component scheme reconstructs     *)
(*    ANY slot-pair-symmetric tensor at EVERY index -- pure index        *)
(*    algebra, values in an arbitrary type; (ii) the isotypic access     *)
(*    rule with explicit INTEGER S_3 irrep matrices, division-free as    *)
(*    6*T = triv + sgn + 2*trace-block (1 + 1 + 4 = 6 = 3! numbers per   *)
(*    cell, Wedderburn's sum d^2); (iii) cell accounting: the repaired   *)
(*    store holds exactly dim Sym^3(V (x) W) numbers -- the double-      *)
(*    coset sum equals C(LM+2,3) at extents (2,2) and (3,3), the true    *)
(*    generalization of cauchy_cell_count.                               *)
(*                                                                       *)
(* Division-free throughout (factors 2 and 6 exhibited, never divided;   *)
(* the BladeCauchy / BladeSymPower discipline).  Self-contained; Coq     *)
(* 8.18 / Rocq 9.0, stdlib only, no axioms.                              *)
(* ===================================================================== *)

Require Import List Arith Lia ZArith Psatz Ring.
Import ListNotations.
Open Scope Z_scope.

(* ===================================================================== *)
(* S_3 toolkit: the six permutations as image lists, acting on 3-lists.  *)
(* pact p l places l's p[k]-th entry at position k, so pact p (pact q l) *)
(* = pact (pact q p)?? -- composition order is pinned by pact_pact       *)
(* below, checked, not asserted.                                         *)
(* ===================================================================== *)

Definition perms3 : list (list nat) :=
  [ [0;1;2]%nat; [0;2;1]%nat; [1;0;2]%nat;
    [1;2;0]%nat; [2;0;1]%nat; [2;1;0]%nat ].

Definition pact (p l : list nat) : list nat :=
  map (fun k => nth k l 0%nat) p.

Fixpoint nleqb (a b : list nat) : bool :=
  match a, b with
  | [], [] => true
  | x :: a', y :: b' => andb (Nat.eqb x y) (nleqb a' b')
  | _, _ => false
  end.

Lemma nleqb_eq : forall a b, nleqb a b = true -> a = b.
Proof.
  induction a as [|x a IH]; destruct b as [|y b]; simpl; try discriminate.
  - reflexivity.
  - intros H. apply Bool.andb_true_iff in H as [Hx Hb].
    apply Nat.eqb_eq in Hx. apply IH in Hb. now subst.
Qed.

(* Signs, by table over the six lists (in perms3 order). *)
Definition sgn3 (p : list nat) : Z :=
  if nleqb p [0;1;2]%nat then 1 else if nleqb p [1;2;0]%nat then 1
  else if nleqb p [2;0;1]%nat then 1 else -1.

(* Composition and inverse, as tables realized through pact; the checks  *)
(* below pin the convention: pmul a b is "first b, then a" on positions. *)
Definition pmul (a b : list nat) : list nat := pact b a.

Definition pinv (p : list nat) : list nat :=
  if nleqb p [1;2;0]%nat then [2;0;1]%nat
  else if nleqb p [2;0;1]%nat then [1;2;0]%nat
  else p.

Example pact_pact :
  forallb (fun a => forallb (fun b =>
    nleqb (pact a (pact b [3;4;5]%nat)) (pact (pmul b a) [3;4;5]%nat))
    perms3) perms3 = true.
Proof. vm_compute. reflexivity. Qed.

Example pinv_correct :
  forallb (fun p => andb (nleqb (pmul p (pinv p)) [0;1;2]%nat)
                         (nleqb (pmul (pinv p) p) [0;1;2]%nat)) perms3 = true.
Proof. vm_compute. reflexivity. Qed.

Example sgn3_multiplicative :
  forallb (fun a => forallb (fun b =>
    Z.eqb (sgn3 (pmul a b)) (sgn3 a * sgn3 b)) perms3) perms3 = true.
Proof. vm_compute. reflexivity. Qed.

(* Sorting a 3-list by min / mid / max -- the multiset's canonical name. *)
Definition sort3 (l : list nat) : list nat :=
  match l with
  | [a; b; c] =>
      let mn := Nat.min a (Nat.min b c) in
      let mx := Nat.max a (Nat.max b c) in
      [mn; (a + b + c - mn - mx)%nat; mx]
  | _ => l
  end.

(* The diagonal action on a 6-list [i0;i1;i2;j0;j1;j2]: permute the      *)
(* three slot-PAIRS jointly.                                             *)
Definition diagact (p x : list nat) : list nat :=
  match x with
  | [i0;i1;i2;j0;j1;j2] =>
      pact p [i0;i1;i2]%nat ++ pact p [j0;j1;j2]%nat
  | _ => x
  end.

(* The lon-only action: permute the second block alone.                  *)
Definition lonact (p x : list nat) : list nat :=
  match x with
  | [i0;i1;i2;j0;j1;j2] => [i0;i1;i2]%nat ++ pact p [j0;j1;j2]%nat
  | _ => x
  end.

(* All 6-tuples over [0, n).                                             *)
Definition cells6 (n : nat) : list (list nat) :=
  flat_map (fun a => flat_map (fun b => flat_map (fun c =>
  flat_map (fun d => flat_map (fun e =>
    map (fun f => [a;b;c;d;e;f]%nat) (seq 0 n)) (seq 0 n)) (seq 0 n))
    (seq 0 n)) (seq 0 n)) (seq 0 n).

Example cells6_2_card : length (cells6 2) = 64%nat.
Proof. vm_compute. reflexivity. Qed.

Example cells6_3_card : length (cells6 3) = 729%nat.
Proof. vm_compute. reflexivity. Qed.

(* Slot-pair symmetry over the finite domain, as a boolean.              *)
Definition diag_symb (n : nat) (T : list nat -> Z) : bool :=
  forallb (fun x => forallb (fun p => Z.eqb (T (diagact p x)) (T x)) perms3)
          (cells6 n).

(* ===================================================================== *)
(* PART A -- the witness.  Extent 2.  T is supported on two diagonal     *)
(* orbits, named by the sorted list of pair-codes 2*i+j:                 *)
(*   [0;0;3] = {(0,0),(0,0),(1,1)}  with value -2                        *)
(*   [0;1;2] = {(0,0),(0,1),(1,0)}  with value  1                        *)
(* This is the S^(2,1) (x) S^(2,1) witness of the dichotomy document     *)
(* section 5.2: nonzero, symmetric, and INVISIBLE to the sym and alt     *)
(* components alike.                                                     *)
(* ===================================================================== *)

Definition paircodes (x : list nat) : list nat :=
  match x with
  | [i0;i1;i2;j0;j1;j2] =>
      sort3 [2*i0 + j0; 2*i1 + j1; 2*i2 + j2]%nat
  | _ => []
  end.

Definition wT (x : list nat) : Z :=
  if nleqb (paircodes x) [0;0;3]%nat then -2
  else if nleqb (paircodes x) [0;1;2]%nat then 1
  else 0.

(* The two components of the natural r = 3 split: symmetrize the lon     *)
(* block with the trivial character and with the sign character.  At     *)
(* r = 2 these are exactly BladeCauchy's Psym and Qalt (up to the        *)
(* harmless whole-group sum replacing the 2-element sum).                *)
Definition wPsym (x : list nat) : Z :=
  fold_right Z.add 0 (map (fun p => wT (lonact p x)) perms3).
Definition wQalt (x : list nat) : Z :=
  fold_right Z.add 0 (map (fun p => sgn3 p * wT (lonact p x)) perms3).

Theorem witness_symmetric : diag_symb 2 wT = true.
Proof. vm_compute. reflexivity. Qed.

Theorem witness_nonzero : wT [0;0;1;0;0;1]%nat = -2.
Proof. vm_compute. reflexivity. Qed.

Theorem witness_components_vanish :
  forallb (fun x => andb (Z.eqb (wPsym x) 0) (Z.eqb (wQalt x) 0))
          (cells6 2) = true.
Proof. vm_compute. reflexivity. Qed.

(* Packaged, with the boolean carried to Prop: the witness is nonzero    *)
(* yet BOTH stored components vanish at every index -- in particular at  *)
(* every per-dimension canonical cell.  Any access rule whatsoever that  *)
(* reads only (Psym, Qalt) data returns identical answers for wT and     *)
(* for the zero tensor; since wT <> 0, no such rule is lossless.  This   *)
(* is the r = 3 refutation of the naive generalization of               *)
(* cauchy_split_access.                                                  *)
Theorem naive_split_confuses_witness_with_zero :
  wT [0;0;1;0;0;1]%nat <> 0 /\
  (forall x, In x (cells6 2) -> wPsym x = 0 /\ wQalt x = 0).
Proof.
  split.
  - rewrite witness_nonzero. discriminate.
  - intros x Hx.
    assert (H := witness_components_vanish).
    rewrite forallb_forall in H.
    specialize (H x Hx). apply Bool.andb_true_iff in H as [H1 H2].
    apply Z.eqb_eq in H1. apply Z.eqb_eq in H2. auto.
Qed.

(* ===================================================================== *)
(* PART B -- the lower-bound core: width 2 is impossible with ARBITRARY  *)
(* weights over ANY commutative ring with 1 <> 0.                        *)
(*                                                                       *)
(* Shape of the argument (dichotomy document, Theorem 3.1, r = 3 core):  *)
(* at the free canonical cell a = b = (0,1,2) (extent 3), the fiber      *)
(* consists of the 36 indices (s, t), s, t in S_3.  A width-2 scheme     *)
(* reads TWO scalars (p, q) at that cell and reconstructs                *)
(*   T(s ++ t) = eps1(s,t) * p + eps2(s,t) * q                           *)
(* for every fiber index.  The three indicator tensors of the diagonal   *)
(* orbits of (id, mu), mu in {id, (01), (012)}, evaluate on the three    *)
(* fiber points (id, mu') to the 3x3 IDENTITY matrix; the scheme puts    *)
(* its three rows in the span of two fixed vectors, so the determinant   *)
(* must vanish -- but it is 1.  Storage is an arbitrary function of the  *)
(* tensor (per-tensor existential); only the weight span is constrained. *)
(*                                                                       *)
(* Values: tensors are Z-valued 0/1 indicators; the scheme's field is    *)
(* an abstract commutative ring R, the embedding sending 0 to r0 and     *)
(* 1 to r1.  Instantiations: Z (below), Q, IR -- anything with a         *)
(* ring_theory and 1 <> 0.                                               *)
(* ===================================================================== *)

(* The three indicator tensors, extent 3, pair-codes 3*i+j.              *)
Definition paircodes3 (x : list nat) : list nat :=
  match x with
  | [i0;i1;i2;j0;j1;j2] =>
      sort3 [3*i0 + j0; 3*i1 + j1; 3*i2 + j2]%nat
  | _ => []
  end.

(* Orbits of (id,id): codes {0,4,8}; (id,(01)): {1,3,8}; (id,(012)):     *)
(* {1,5,6} -- where (01) = [1;0;2] and (012) = [1;2;0] in image form.    *)
Definition TA (x : list nat) : Z :=
  if nleqb (paircodes3 x) [0;4;8]%nat then 1 else 0.
Definition TB (x : list nat) : Z :=
  if nleqb (paircodes3 x) [1;3;8]%nat then 1 else 0.
Definition TC (x : list nat) : Z :=
  if nleqb (paircodes3 x) [1;5;6]%nat then 1 else 0.

Theorem indicators_symmetric :
  andb (diag_symb 3 TA) (andb (diag_symb 3 TB) (diag_symb 3 TC)) = true.
Proof. vm_compute. reflexivity. Qed.

(* The evaluation matrix at the three fiber points is the identity.      *)
Definition fib (t : list nat) : list nat := [0;1;2]%nat ++ t.

Theorem indicator_matrix_is_identity :
  TA (fib [0;1;2]%nat) = 1 /\ TA (fib [1;0;2]%nat) = 0 /\ TA (fib [1;2;0]%nat) = 0 /\
  TB (fib [0;1;2]%nat) = 0 /\ TB (fib [1;0;2]%nat) = 1 /\ TB (fib [1;2;0]%nat) = 0 /\
  TC (fib [0;1;2]%nat) = 0 /\ TC (fib [1;0;2]%nat) = 0 /\ TC (fib [1;2;0]%nat) = 1.
Proof. vm_compute. repeat split; reflexivity. Qed.

Section Width2Refutation.
  Variable R : Type.
  Variables (r0 r1 : R) (radd rmul rsub : R -> R -> R) (ropp : R -> R).
  Hypothesis Rth : ring_theory r0 r1 radd rmul rsub ropp (@eq R).
  Add Ring width2_ring : Rth.
  Hypothesis one_neq_zero : r1 <> r0.

  Local Infix "+!" := radd (at level 50, left associativity).
  Local Infix "*!" := rmul (at level 40, left associativity).
  Local Infix "-!" := rsub (at level 50, left associativity).

  (* Embed the 0/1 tensor values.                                        *)
  Definition emb (z : Z) : R := if Z.eqb z 0 then r0 else r1.

  (* The two weight functions -- ARBITRARY.                              *)
  Variables eps1 eps2 : list nat -> list nat -> R.

  (* Soundness of a width-2 scheme AT THE ONE FREE CELL: for every       *)
  (* symmetric 0/1 tensor there exist two stored scalars reconstructing  *)
  (* it across the fiber.  This is implied by (hence weaker than, hence  *)
  (* the refutation is stronger than) soundness of any width-2           *)
  (* per-dimension-canonical scheme in the sense of the document:        *)
  (* specialize its soundness equation to the fiber indices of the cell  *)
  (* ((0,1,2),(0,1,2)), where the sorters are unique.                    *)
  Hypothesis sound :
    forall T : list nat -> Z, diag_symb 3 T = true ->
    exists p q : R, forall s t, In s perms3 -> In t perms3 ->
      emb (T (pact s [0;1;2]%nat ++ pact t [0;1;2]%nat))
      = eps1 s t *! p +! eps2 s t *! q.

  (* det of three rows each lying in the span of (u, v) vanishes.        *)
  Lemma det3_span2 :
    forall a1 b1 a2 b2 a3 b3 u1 v1 u2 v2 u3 v3 : R,
      (u1 *! a1 +! v1 *! b1)
        *! ((u2 *! a2 +! v2 *! b2) *! (u3 *! a3 +! v3 *! b3)
            -! (u3 *! a2 +! v3 *! b2) *! (u2 *! a3 +! v2 *! b3))
      -! (u2 *! a1 +! v2 *! b1)
        *! ((u1 *! a2 +! v1 *! b2) *! (u3 *! a3 +! v3 *! b3)
            -! (u3 *! a2 +! v3 *! b2) *! (u1 *! a3 +! v1 *! b3))
      +! (u3 *! a1 +! v3 *! b1)
        *! ((u1 *! a2 +! v1 *! b2) *! (u2 *! a3 +! v2 *! b3)
            -! (u2 *! a2 +! v2 *! b2) *! (u1 *! a3 +! v1 *! b3))
      = r0.
  Proof. intros. ring. Qed.

  Theorem width2_scalar_access_refuted : False.
  Proof.
    (* the three indicator tensors *)
    assert (Hsyms := indicators_symmetric).
    apply Bool.andb_true_iff in Hsyms as [HsA Hs'].
    apply Bool.andb_true_iff in Hs' as [HsB HsC].
    destruct (sound TA HsA) as [pA [qA EA]].
    destruct (sound TB HsB) as [pB [qB EB]].
    destruct (sound TC HsC) as [pC [qC EC]].
    (* the three fiber points: (id,id), (id,(01)), (id,(012)) *)
    assert (Hid : In [0;1;2]%nat perms3) by (simpl; tauto).
    assert (Ht  : In [1;0;2]%nat perms3) by (simpl; tauto).
    assert (Hc  : In [1;2;0]%nat perms3) by (simpl; tauto).
    pose (u1 := eps1 [0;1;2]%nat [0;1;2]%nat).
    pose (v1 := eps2 [0;1;2]%nat [0;1;2]%nat).
    pose (u2 := eps1 [0;1;2]%nat [1;0;2]%nat).
    pose (v2 := eps2 [0;1;2]%nat [1;0;2]%nat).
    pose (u3 := eps1 [0;1;2]%nat [1;2;0]%nat).
    pose (v3 := eps2 [0;1;2]%nat [1;2;0]%nat).
    (* the nine reconstruction equations, with LHS computed *)
    assert (E11 : r1 = u1 *! pA +! v1 *! qA)
      by (specialize (EA _ _ Hid Hid); vm_compute in EA; exact EA).
    assert (E12 : r0 = u2 *! pA +! v2 *! qA)
      by (specialize (EA _ _ Hid Ht); vm_compute in EA; exact EA).
    assert (E13 : r0 = u3 *! pA +! v3 *! qA)
      by (specialize (EA _ _ Hid Hc); vm_compute in EA; exact EA).
    assert (E21 : r0 = u1 *! pB +! v1 *! qB)
      by (specialize (EB _ _ Hid Hid); vm_compute in EB; exact EB).
    assert (E22 : r1 = u2 *! pB +! v2 *! qB)
      by (specialize (EB _ _ Hid Ht); vm_compute in EB; exact EB).
    assert (E23 : r0 = u3 *! pB +! v3 *! qB)
      by (specialize (EB _ _ Hid Hc); vm_compute in EB; exact EB).
    assert (E31 : r0 = u1 *! pC +! v1 *! qC)
      by (specialize (EC _ _ Hid Hid); vm_compute in EC; exact EC).
    assert (E32 : r0 = u2 *! pC +! v2 *! qC)
      by (specialize (EC _ _ Hid Ht); vm_compute in EC; exact EC).
    assert (E33 : r1 = u3 *! pC +! v3 *! qC)
      by (specialize (EC _ _ Hid Hc); vm_compute in EC; exact EC).
    (* determinant of the identity, rewritten into the rank-2 span form *)
    assert (H0 := det3_span2 pA qA pB qB pC qC u1 v1 u2 v2 u3 v3).
    rewrite <- E11, <- E12, <- E13, <- E21, <- E22, <- E23,
            <- E31, <- E32, <- E33 in H0.
    (* det3 of the identity is 1 *)
    assert (H1 : r1 *! (r1 *! r1 -! r0 *! r0)
                 -! r0 *! (r0 *! r1 -! r0 *! r0)
                 +! r0 *! (r0 *! r0 -! r1 *! r0) = r1) by ring.
    rewrite H1 in H0.
    exact (one_neq_zero H0).
  Qed.
End Width2Refutation.

(* Pin at Z: no integer-weight width-2 scheme either (instance of the    *)
(* abstract theorem at the initial ring).                                *)
Corollary width2_refuted_over_Z :
  forall eps1 eps2 : list nat -> list nat -> Z,
    (forall T : list nat -> Z, diag_symb 3 T = true ->
     exists p q : Z, forall s t, In s perms3 -> In t perms3 ->
       emb Z 0 1 (T (pact s [0;1;2]%nat ++ pact t [0;1;2]%nat))
       = eps1 s t * p + eps2 s t * q) -> False.
Proof.
  intros eps1 eps2 H.
  refine (width2_scalar_access_refuted
            Z 0 1 Z.add Z.mul Z.sub Z.opp InitialRing.Zth
            _ eps1 eps2 H).
  discriminate.
Qed.

(* ===================================================================== *)
(* PART C -- the repair.                                                 *)
(* ===================================================================== *)

(* --- C(i): the six-component scheme, abstract values ------------------ *)
(* Store P_mu(a,b) = T(a ++ pact mu b) for the six mu; read T at any     *)
(* index (pact s a ++ pact t b) as P_{pmul (pinv s) t}(a, b).  The       *)
(* identity below IS that scheme's soundness at every index, free or     *)
(* tied, for values in ANY type: it is pure index algebra, the r = 3     *)
(* instance of the document's Lemma 2.1 / Theorem 3.1 upper bound (the   *)
(* m = r! row).  Generated by the transposition and the 3-cycle.         *)

Section SixComponent.
  Variable V : Type.
  Variable T : list nat -> V.
  Hypothesis Tswap :
    forall i0 i1 i2 j0 j1 j2 : nat,
      T [i1;i0;i2;j1;j0;j2]%nat = T [i0;i1;i2;j0;j1;j2]%nat.
  Hypothesis Tcyc :
    forall i0 i1 i2 j0 j1 j2 : nat,
      T [i1;i2;i0;j1;j2;j0]%nat = T [i0;i1;i2;j0;j1;j2]%nat.

  (* the other three relations, derived *)
  Lemma Tcyc2 : forall i0 i1 i2 j0 j1 j2 : nat,
    T [i2;i0;i1;j2;j0;j1]%nat = T [i0;i1;i2;j0;j1;j2]%nat.
  Proof. intros. rewrite Tcyc, Tcyc. reflexivity. Qed.

  Lemma Tswap02 : forall i0 i1 i2 j0 j1 j2 : nat,
    T [i2;i1;i0;j2;j1;j0]%nat = T [i0;i1;i2;j0;j1;j2]%nat.
  Proof. intros. rewrite Tcyc, Tswap. apply Tcyc2. Qed.

  Lemma Tswap12 : forall i0 i1 i2 j0 j1 j2 : nat,
    T [i0;i2;i1;j0;j2;j1]%nat = T [i0;i1;i2;j0;j1;j2]%nat.
  Proof. intros. rewrite Tcyc2, Tswap. apply Tcyc. Qed.

  Lemma Tid : forall i0 i1 i2 j0 j1 j2 : nat,
    T [i0;i1;i2;j0;j1;j2]%nat = T [i0;i1;i2;j0;j1;j2]%nat.
  Proof. reflexivity. Qed.

  Theorem six_component_access :
    forall s t, In s perms3 -> In t perms3 ->
    forall a0 a1 a2 b0 b1 b2 : nat,
      T (pact s [a0;a1;a2]%nat ++ pact t [b0;b1;b2]%nat)
      = T ([a0;a1;a2]%nat ++ pact (pmul t (pinv s)) [b0;b1;b2]%nat).
  Proof.
    intros s t Hs Ht a0 a1 a2 b0 b1 b2.
    simpl in Hs, Ht.
    destruct Hs as [Hs|[Hs|[Hs|[Hs|[Hs|[Hs|[]]]]]]];
    destruct Ht as [Ht|[Ht|[Ht|[Ht|[Ht|[Ht|[]]]]]]];
    subst s t; cbn;
    solve [ apply Tid | apply Tswap | apply Tcyc | apply Tcyc2
          | apply Tswap02 | apply Tswap12 ].
  Qed.
End SixComponent.

(* --- C(ii): the isotypic access rule with integer irrep matrices ------ *)
(* The standard representation of S_3 on the root lattice basis          *)
(* (e0 - e1, e1 - e2) has INTEGER matrices; with the trivial and sign    *)
(* characters this is the full irrep roster, sum of squares              *)
(* 1 + 1 + 4 = 6 = 3!.  Matrices as (m11, m12, m21, m22).                *)

Definition M2 : Type := (Z * Z * Z * Z)%type.
Definition mmul (A B : M2) : M2 :=
  let '(a11,a12,a21,a22) := A in
  let '(b11,b12,b21,b22) := B in
  (a11*b11 + a12*b21, a11*b12 + a12*b22,
   a21*b11 + a22*b21, a21*b12 + a22*b22).
Definition mtr (A : M2) : Z := let '(a11,_,_,a22) := A in a11 + a22.

(* index order matches perms3: e, (12), (01), (012), (021), (02) *)
Definition stdmat (p : list nat) : M2 :=
  if nleqb p [0;1;2]%nat then (1,0,0,1)
  else if nleqb p [0;2;1]%nat then (1,0,1,-1)        (* (12) *)
  else if nleqb p [1;0;2]%nat then (-1,1,0,1)        (* (01) *)
  else if nleqb p [1;2;0]%nat then (0,-1,1,-1)       (* (012) *)
  else if nleqb p [2;0;1]%nat then (-1,1,-1,0)       (* (021) *)
  else (0,-1,-1,0).                                  (* (02) *)

(* the rep property, table closure and characters, all by computation *)
Example stdmat_is_rep :
  forallb (fun a => forallb (fun b =>
    let '(x1,x2,x3,x4) := mmul (stdmat a) (stdmat b) in
    let '(y1,y2,y3,y4) := stdmat (pmul a b) in
    andb (Z.eqb x1 y1) (andb (Z.eqb x2 y2) (andb (Z.eqb x3 y3) (Z.eqb x4 y4))))
    perms3) perms3 = true.
Proof. vm_compute. reflexivity. Qed.

Example std_characters :
  map (fun p => mtr (stdmat p)) perms3 = [2; 0; 0; -1; -1; 0].
Proof. vm_compute. reflexivity. Qed.

(* Column orthogonality, division-free: sum over lambda of               *)
(* d_lambda * chi_lambda(mu^-1 nu) equals 6 exactly on the diagonal.     *)
Example fourier_orthogonality_S3 :
  forallb (fun mu => forallb (fun nu =>
    Z.eqb (1 + sgn3 (pmul (pinv mu) nu) * 1
             + 2 * mtr (stdmat (pmul (pinv mu) nu)))
          (if nleqb mu nu then 6 else 0)) perms3) perms3 = true.
Proof. vm_compute. reflexivity. Qed.

(* The access rule itself.  Cell-local data of a symmetric tensor is a   *)
(* function g on S_3 (Lemma 2.1 of the document; mechanized at r = 3 as  *)
(* six_component_access above).  Store its THREE isotypic blocks:        *)
(*   ptriv = sum g_nu,   psgn = sum sgn(nu) g_nu,                        *)
(*   Pstd  = sum g_nu * stdmat(nu)   (four integers),                    *)
(* six numbers in all, and read back                                     *)
(*   6 * g_mu = ptriv + sgn(mu) * psgn + 2 * tr(stdmat(mu)^-1 * Pstd).   *)
(* Division-free: the 6 is exhibited, exactly as BladeCauchy's 2.        *)

Section IsotypicAccess.
  Variables g0 g1 g2 g3 g4 g5 : Z.   (* g at e,(12),(01),(012),(021),(02) *)

  Let ptriv : Z := g0 + g1 + g2 + g3 + g4 + g5.
  Let psgn  : Z := g0 - g1 - g2 + g3 + g4 - g5.
  Let P11 : Z := g0*1 + g1*1 + g2*(-1) + g3*0 + g4*(-1) + g5*0.
  Let P12 : Z := g0*0 + g1*0 + g2*1 + g3*(-1) + g4*1 + g5*(-1).
  Let P21 : Z := g0*0 + g1*1 + g2*0 + g3*1 + g4*(-1) + g5*(-1).
  Let P22 : Z := g0*1 + g1*(-1) + g2*1 + g3*(-1) + g4*0 + g5*0.

  Definition read (mu : list nat) : Z :=
    let '(m11,m12,m21,m22) := stdmat (pinv mu) in
    ptriv + sgn3 mu * psgn
    + 2 * (m11 * P11 + m12 * P21 + m21 * P12 + m22 * P22).

  Theorem isotypic_access_r3 :
    read [0;1;2]%nat = 6 * g0 /\
    read [0;2;1]%nat = 6 * g1 /\
    read [1;0;2]%nat = 6 * g2 /\
    read [1;2;0]%nat = 6 * g3 /\
    read [2;0;1]%nat = 6 * g4 /\
    read [2;1;0]%nat = 6 * g5.
  Proof.
    unfold read, ptriv, psgn, P11, P12, P21, P22;
    cbn [stdmat pinv sgn3 nleqb Nat.eqb andb];
    repeat split; ring.
  Qed.
End IsotypicAccess.

(* --- C(iii): cell accounting -- the generalization of                  *)
(* cauchy_cell_count, pinned at r = 3.  The repaired store holds, per    *)
(* pair of canonical cells (a, b), one number per double coset of        *)
(* Stab(a) \ S_3 / Stab(b); the total is exactly dim Sym^3(V (x) W),     *)
(* i.e. the number of diagonal orbits, i.e. C(LM + 2, 3).                *)

Definition sorted3 (n : nat) : list (list nat) :=
  flat_map (fun a => flat_map (fun b =>
    map (fun c => [a;b;c]%nat) (seq b (n - b)))
    (seq a (n - a))) (seq 0 n).

Definition stab (l : list nat) : list (list nat) :=
  filter (fun p => nleqb (pact p l) l) perms3.

(* index of a permutation in perms3 *)
Fixpoint idx_of (p : list nat) (l : list (list nat)) : nat :=
  match l with
  | [] => 0%nat
  | q :: l' => if nleqb p q then 0%nat else S (idx_of p l')
  end.

Fixpoint insert_nat (x : nat) (l : list nat) : list nat :=
  match l with
  | [] => [x]
  | y :: l' => if Nat.leb x y then x :: l else y :: insert_nat x l'
  end.
Fixpoint sort_nat (l : list nat) : list nat :=
  match l with [] => [] | x :: l' => insert_nat x (sort_nat l') end.
Fixpoint dedup_nat (l : list nat) : list nat :=
  match l with
  | [] => []
  | x :: l' => match l' with
               | y :: _ => if Nat.eqb x y then dedup_nat l' else x :: dedup_nat l'
               | [] => [x]
               end
  end.

(* the double coset of mu, named by the sorted deduped index set of      *)
(* { a * mu * b : a in alpha, b in beta }                                *)
Definition dcoset (alpha beta : list (list nat)) (mu : list nat) : list nat :=
  dedup_nat (sort_nat
    (flat_map (fun a => map (fun b => idx_of (pmul a (pmul mu b)) perms3) beta)
              alpha)).

Fixpoint dedup_ll (l : list (list nat)) : list (list nat) :=
  match l with
  | [] => []
  | x :: l' => if existsb (nleqb x) l' then dedup_ll l' else x :: dedup_ll l'
  end.

Definition ndc (alpha beta : list (list nat)) : nat :=
  length (dedup_ll (map (dcoset alpha beta) perms3)).

Definition dc_sum (nL nM : nat) : nat :=
  fold_right Nat.add 0%nat
    (flat_map (fun a => map (fun b => ndc (stab a) (stab b)) (sorted3 nM))
              (sorted3 nL)).

(* The number of diagonal orbits, counted through a complete invariant:  *)
(* two indices lie in the same orbit iff their sorted pair-code lists    *)
(* agree -- at extent n the code n*i + j is injective on pairs, so the   *)
(* sorted code list is a complete orbit name.                            *)
Definition orbit_name (n : nat) (x : list nat) : list nat :=
  match x with
  | [i0;i1;i2;j0;j1;j2] => sort3 [n*i0 + j0; n*i1 + j1; n*i2 + j2]%nat
  | _ => []
  end.

Definition orbit_count' (n : nat) : nat :=
  length (dedup_ll (map (orbit_name n) (cells6 n))).

(* binomial, local *)
Fixpoint Cb (n k : nat) : nat :=
  match k with
  | O => 1%nat
  | S k' => match n with O => 0%nat | S n' => (Cb n' k' + Cb n' k)%nat end
  end.

Theorem cell_accounting_2_2 :
  dc_sum 2 2 = 20%nat /\ orbit_count' 2 = 20%nat /\ Cb 6 3 = 20%nat.
Proof. vm_compute. repeat split; reflexivity. Qed.

Theorem cell_accounting_3_3 :
  dc_sum 3 3 = 165%nat /\ orbit_count' 3 = 165%nat /\ Cb 11 3 = 165%nat.
Proof. vm_compute. repeat split; reflexivity. Qed.

(* ===================================================================== *)
(* Closing note.  Together: two scalar components CANNOT serve r = 3     *)
(* (Parts A and B -- the specific natural scheme fails on an explicit    *)
(* witness, and NO width-2 weight assignment over any ring works), six   *)
(* CAN (Part C -- the m = r! scheme reconstructs everywhere, its         *)
(* isotypic form reads through integer irrep matrices with the 6         *)
(* exhibited, and the repaired store is exactly lossless in count).      *)
(* The general-r statement (minimal width r!, with the (3,2,2)           *)
(* tie-breaking exception) is the dichotomy document; general-r          *)
(* mechanization would need a Coq development of Q[S_r] and is future    *)
(* work, exactly as BladeWreath's general-r exactness.                   *)
(* ===================================================================== *)
