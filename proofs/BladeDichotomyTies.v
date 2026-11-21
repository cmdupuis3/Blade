(* ===================================================================== *)
(* BladeDichotomyTies.v -- the storage dichotomy BELOW the free cell:    *)
(* tie-breaking rules at (r, L, M) = (3, 2, 2) and (3, 2, 3).            *)
(*                                                                       *)
(* BladeDichotomy.v proves the minimal width of a scalar-access per-     *)
(* dimension-canonical scheme is r! = 6 at r = 3 -- AT A FREE CELL,      *)
(* i.e. for extents L, M >= 3.  Below that (L = 2: every lat tuple over  *)
(* {0,1} has a repeated entry) no cell is free, every tuple has several  *)
(* sorting permutations, and the scheme's weights eps_k(sigma, tau) are  *)
(* read at the sorters a TIE-BREAKING RULE picks.  The 2026-08-02 prose  *)
(* claimed, unmechanized: <<at (3,2,2) width 2 is achievable for exactly  *)
(* 4 of 4096 rules (parity-graded transversals); the exception dies at   *)
(* (3,2,3)>>.  This file settles it (scripts: proofs/scripts/             *)
(* dichotomy-ties/).                                                     *)
(*                                                                       *)
(* The model (the one BladeDichotomy's Part B specializes): a rule is a  *)
(* pair of functions rho_lat, rho_lon : tuple -> sorter; a width-m       *)
(* scheme is m weight functions on S_3 x S_3 such that every symmetric   *)
(* tensor is, on every fiber {x : (sort lat x, sort lon x) = (a, b)},    *)
(* the combination sum_k eps_k (rho_lat (lat x)) (rho_lon (lon x)) * P_k *)
(* of m scalars stored at the cell.  Storage arbitrary; the constraint   *)
(* lives in the weights, exactly as in Part B.  R_a = the set of sorters *)
(* the rule assigns to the tuples sorting to a.  The decisive structure: *)
(* a point (sigma, tau) of S_3 x S_3 is READ by every cell (a, b) with   *)
(* sigma in R_a and tau in R_b, so two canonical tuples a <> a' with      *)
(* overlapping R_a, R_a' share weight entries -- and the shared row must  *)
(* then serve both cells' orbit structures at once.                      *)
(*                                                                       *)
(* Checked:                                                              *)
(*  - PART A (the census, computed): of the 64 ways to break the ties    *)
(*    of the six non-constant lat tuples, exactly 2 make R_001 and R_011 *)
(*    disjoint, and they are the PARITY-GRADED ones (every sorter of a   *)
(*    001-type tuple even and every sorter of a 011-type tuple odd, or   *)
(*    the reverse: R_001 = A_3, R_011 = S_3 \ A_3, or swapped);          *)
(*    2 x 2 = 4 of the 4096 two-dimensional choices.                     *)
(*  - PART B (the negative half, any commutative ring with 1 <> 0): if   *)
(*    the lat rule gives a 001-type and a 011-type tuple the SAME sorter *)
(*    (R_001 meets R_011), no two weight functions reconstruct every     *)
(*    symmetric tensor at extent (2, 2) -- lat_overlap_refutes_width2 -- *)
(*    and, at extent (2, 3), no THREE do: lat_overlap_refutes_width3.    *)
(*    Both are triangular-determinant certificates on the shared row:    *)
(*    three (four) weight points read by two cells, three (four)         *)
(*    indicator tensors whose evaluation matrix is unitriangular, and    *)
(*    BladeDichotomy's det3_span2 (here det4_span3) kills it.  Lon       *)
(*    overlaps are the mirror image (not restated).                      *)
(*  - PART C (the positive half, pure index algebra): for each parity-   *)
(*    graded lat AND lon rule, with the constant tuples' sorters          *)
(*    ARBITRARY, the explicit width-2 scheme eps1 = 1, eps2 = chi (the   *)
(*    indicator of <<small orbit of the unique full block reading this    *)
(*    point>>) reconstructs every symmetric tensor at every index of      *)
(*    extent (2, 2): scheme322_sound.  And for each parity-graded lat    *)
(*    rule and EVERY lon rule, the width-3 scheme that stores one scalar *)
(*    per coset of the lat stabilizer reconstructs every symmetric       *)
(*    tensor at every index of extent (2, 3): scheme323_sound.           *)
(*                                                                       *)
(* So the correct statement is not <<an exception at (3,2,2) that dies at *)
(* (3,2,3)>>.  Below the free cell the counting bound max_c |Stab(a) \    *)
(* S_3 / Stab(b)| (2 at (3,2,2), 3 at (3,2,3)) is attained exactly by    *)
(* the parity-graded rules, at both extents; the r! theorem is a         *)
(* theorem about free cells and is simply not in force here.  Values in  *)
(* Z (Part C) or an abstract ring (Part B); division-free; Rocq 9.0,     *)
(* stdlib + BladeDichotomy, no axioms.                                   *)
(* ===================================================================== *)

From Blade Require Import BladeDichotomy.
From Stdlib Require Import List Arith Lia ZArith Psatz Ring Bool.
Import ListNotations.
Open Scope Z_scope.

(* ===================================================================== *)
(* Shared toolkit.                                                       *)
(* ===================================================================== *)

Definition lat (x : list nat) : list nat := firstn 3 x.
Definition lon (x : list nat) : list nat := skipn 3 x.

Definition inb (x : list nat) (l : list (list nat)) : bool := existsb (nleqb x) l.

Lemma inb_In : forall x l, inb x l = true -> In x l.
Proof.
  intros x l H. unfold inb in H. apply existsb_exists in H as [y [Hy Heq]].
  apply nleqb_eq in Heq. now subst.
Qed.

Lemma nleqb_refl : forall l, nleqb l l = true.
Proof.
  induction l as [|a l IH]; simpl; auto. rewrite Nat.eqb_refl. exact IH.
Qed.

Definition is_const (l : list nat) : bool :=
  match l with
  | [a; b; c] => andb (Nat.eqb a b) (Nat.eqb b c)
  | _ => false
  end.

(* the sorters of a tuple, and the parity-graded rule's sorters:        *)
(* sig = true means <<001-type tuples sort by EVEN permutations, 011-type *)
(* by odd>>; sig = false the reverse; constant tuples are unconstrained.  *)
Definition sorters (i : list nat) : list (list nat) :=
  filter (fun p => nleqb (pact p i) (sort3 i)) perms3.

Definition sorters_rule (sig : bool) (i : list nat) : list (list nat) :=
  filter (fun p =>
    andb (nleqb (pact p i) (sort3 i))
         (if is_const i then true
          else if nleqb (sort3 i) [0;0;1]%nat
               then Z.eqb (sgn3 p) (if sig then 1 else -1)
               else Z.eqb (sgn3 p) (if sig then -1 else 1))) perms3.

(* the six non-constant lat tuples at extent 2, 001-types first          *)
Definition type001 : list (list nat) := [[0;0;1]; [0;1;0]; [1;0;0]]%nat.
Definition type011 : list (list nat) := [[0;1;1]; [1;0;1]; [1;1;0]]%nat.

(* ===================================================================== *)
(* PART A -- the census.  A one-dimension choice is a list of six        *)
(* sorters, one per tuple of type001 ++ type011.                         *)
(* ===================================================================== *)

Definition choices : list (list (list nat)) :=
  fold_right (fun i acc => flat_map (fun s => map (cons s) acc) (sorters i))
             [[]] (type001 ++ type011).

Definition R001 (ch : list (list nat)) := firstn 3 ch.
Definition R011 (ch : list (list nat)) := skipn 3 ch.

Definition disjoint (ch : list (list nat)) : bool :=
  forallb (fun s => negb (inb s (R011 ch))) (R001 ch).

Definition all_sign (z : Z) (l : list (list nat)) : bool :=
  forallb (fun p => Z.eqb (sgn3 p) z) l.

Definition parity_graded (ch : list (list nat)) : bool :=
  orb (andb (all_sign 1 (R001 ch)) (all_sign (-1) (R011 ch)))
      (andb (all_sign (-1) (R001 ch)) (all_sign 1 (R011 ch))).

(* the two parity-graded choices, spelled out: E = 001-even, O = 001-odd *)
Definition choiceE : list (list nat) :=
  [[0;1;2]; [2;0;1]; [1;2;0]; [0;2;1]; [1;0;2]; [2;1;0]]%nat.
Definition choiceO : list (list nat) :=
  [[1;0;2]; [0;2;1]; [2;1;0]; [0;1;2]; [1;2;0]; [2;0;1]]%nat.

Theorem choices_count : length choices = 64%nat.
Proof. vm_compute. reflexivity. Qed.

(* every choice is a valid tie-break (the k-th sorter sorts the k-th    *)
(* tuple), so the 64 ARE the tie-breaking rules of the non-constant      *)
(* tuples; the constant tuples (6 sorters each) are the factor the 4096  *)
(* count leaves out -- Parts B and C quantify over them                  *)
Theorem choices_valid :
  forallb (fun ch =>
    forallb (fun k => nleqb (pact (nth k ch []) (nth k (type001 ++ type011) []))
                            (sort3 (nth k (type001 ++ type011) [])))
            (seq 0 6)) choices = true.
Proof. vm_compute. reflexivity. Qed.

Theorem disjoint_choices_are_parity_graded :
  filter disjoint choices = [choiceE; choiceO] /\
  filter parity_graded choices = [choiceE; choiceO].
Proof. vm_compute. split; reflexivity. Qed.

(* the 4096 two-dimensional rules: exactly 4 have both dimensions        *)
(* disjoint (equivalently, both parity-graded)                           *)
Theorem four_of_4096 :
  length (list_prod choices choices) = 4096%nat /\
  filter (fun lm => andb (disjoint (fst lm)) (disjoint (snd lm)))
         (list_prod choices choices)
  = [(choiceE, choiceE); (choiceE, choiceO); (choiceO, choiceE); (choiceO, choiceO)].
Proof. vm_compute. split; reflexivity. Qed.

(* the parity-graded sorter sets are the two cosets of A_3               *)
Theorem parity_graded_sets :
  R001 choiceE = [[0;1;2]; [2;0;1]; [1;2;0]]%nat /\
  R011 choiceE = [[0;2;1]; [1;0;2]; [2;1;0]]%nat /\
  R001 choiceO = [[1;0;2]; [0;2;1]; [2;1;0]]%nat /\
  R011 choiceO = [[0;1;2]; [1;2;0]; [2;0;1]]%nat.
Proof. vm_compute. repeat split; reflexivity. Qed.

(* ===================================================================== *)
(* PART B -- the negative half.                                          *)
(* ===================================================================== *)

(* The two indicator tensors of the (3,2,2) certificate, by orbit name   *)
(* (BladeDichotomy.orbit_name 2 : sorted pair-codes 2*i + j):           *)
(*   chi1 = orbit of (001, 001) = pairs {(0,0),(0,0),(1,1)} -> [0;0;3]   *)
(*          (the 3-point orbit of cell (001, 001))                      *)
(*   chi2 = orbit of (011, 100) = pairs {(0,1),(1,0),(1,0)} -> [1;2;2]   *)
(*          (the 3-point orbit of cell (011, 001))                      *)
Definition chi1 (x : list nat) : Z :=
  if nleqb (orbit_name 2 x) [0;0;3]%nat then 1 else 0.
Definition chi2 (x : list nat) : Z :=
  if nleqb (orbit_name 2 x) [1;2;2]%nat then 1 else 0.
Definition one (x : list nat) : Z := 1.

Theorem cert3_tensors_symmetric :
  andb (diag_symb 2 chi1) (andb (diag_symb 2 chi2) (diag_symb 2 one)) = true.
Proof. vm_compute. reflexivity. Qed.

(* The certificate for a shared lat sorter: i0 of type 001 and i0' of    *)
(* type 011 with rho_lat i0 = rho_lat i0'.  Three lon tuples ja, jb, jc  *)
(* of type 001; the six indices i0 ++ j (cell (001,001)) and i0' ++ j   *)
(* (cell (011,001)) read the weights at the SAME three points           *)
(* (rho_lat i0, rho_lon j).  Evaluation matrix (rows ja, jb, jc;        *)
(* columns chi1 at (001,001), chi2 at (011,001), one at (001,001)):     *)
(*   [1 0 1; 0 1 1; 0 0 1], determinant 1.                              *)
Definition cert3 (i0 i0' ja jb jc : list nat) : bool :=
  inb (i0 ++ ja)%list (cells6 2) && inb (i0 ++ jb)%list (cells6 2) && inb (i0 ++ jc)%list (cells6 2)
  && inb (i0' ++ ja)%list (cells6 2) && inb (i0' ++ jb)%list (cells6 2) && inb (i0' ++ jc)%list (cells6 2)
  && nleqb (lat (i0 ++ ja)%list) i0 && nleqb (lat (i0 ++ jb)%list) i0 && nleqb (lat (i0 ++ jc)%list) i0
  && nleqb (lon (i0 ++ ja)%list) ja && nleqb (lon (i0 ++ jb)%list) jb && nleqb (lon (i0 ++ jc)%list) jc
  && nleqb (lat (i0' ++ ja)%list) i0' && nleqb (lat (i0' ++ jb)%list) i0' && nleqb (lat (i0' ++ jc)%list) i0'
  && nleqb (lon (i0' ++ ja)%list) ja && nleqb (lon (i0' ++ jb)%list) jb && nleqb (lon (i0' ++ jc)%list) jc
  && nleqb (sort3 i0) [0;0;1]%nat && nleqb (sort3 i0') [0;1;1]%nat
  && nleqb (sort3 ja) [0;0;1]%nat && nleqb (sort3 jb) [0;0;1]%nat && nleqb (sort3 jc) [0;0;1]%nat
  && Z.eqb (chi1 (i0 ++ ja)%list) 1 && Z.eqb (chi1 (i0 ++ jb)%list) 0 && Z.eqb (chi1 (i0 ++ jc)%list) 0
  && Z.eqb (chi2 (i0' ++ ja)%list) 0 && Z.eqb (chi2 (i0' ++ jb)%list) 1 && Z.eqb (chi2 (i0' ++ jc)%list) 0.

(* choosing the certificate from the overlap: ja = i0 itself (the 1s    *)
(* aligned -- chi1's orbit), jb = the 001-tuple whose 1 sits under the  *)
(* 0 of i0' (chi2's orbit), jc = the third                              *)
Definition pick3 (i0 i0' : list nat) : list nat * list nat * list nat :=
  let jb := hd [] (filter (fun j => Z.eqb (chi2 (i0' ++ j)%list) 1) type001) in
  let jc := hd [] (filter (fun j => andb (negb (nleqb j i0)) (negb (nleqb j jb))) type001) in
  (i0, jb, jc).

(* ... which works whenever the two tuples really share a sorter: the   *)
(* 1 of i0 and the 0 of i0' then sit at different positions, so the two *)
(* small orbits meet the shared row at different points                 *)
Theorem pick3_ok :
  forallb (fun i0 => forallb (fun i0' => forallb (fun s0 =>
    implb (andb (nleqb (pact s0 i0) [0;0;1]%nat) (nleqb (pact s0 i0') [0;1;1]%nat))
          (let '(ja, jb, jc) := pick3 i0 i0' in cert3 i0 i0' ja jb jc))
    perms3) type011) type001 = true.
Proof. vm_compute. reflexivity. Qed.

(* The 4 x 4 twin of BladeDichotomy's det3_span2: rows in a 3-span have  *)
(* zero determinant.  Entries ent u v w a b c = u a + v b + w c (point   *)
(* (u, v, w), tensor (a, b, c)); Laplace along the first row.  Pure ring *)
(* identity.                                                             *)
Section Det4.
  Variable R : Type.
  Variables (r0 r1 : R) (radd rmul rsub : R -> R -> R) (ropp : R -> R).
  Hypothesis Rth : ring_theory r0 r1 radd rmul rsub ropp (@eq R).
  Add Ring det4_ring : Rth.
  Local Infix "+!" := radd (at level 50, left associativity).
  Local Infix "*!" := rmul (at level 40, left associativity).
  Local Infix "-!" := rsub (at level 50, left associativity).

  Definition det3r (a b c d e f g h i : R) : R :=
    a *! (e *! i -! f *! h) -! b *! (d *! i -! f *! g) +! c *! (d *! h -! e *! g).

  Definition det4r (m11 m12 m13 m14 m21 m22 m23 m24
                    m31 m32 m33 m34 m41 m42 m43 m44 : R) : R :=
    m11 *! det3r m22 m23 m24 m32 m33 m34 m42 m43 m44
    -! m12 *! det3r m21 m23 m24 m31 m33 m34 m41 m43 m44
    +! m13 *! det3r m21 m22 m24 m31 m32 m34 m41 m42 m44
    -! m14 *! det3r m21 m22 m23 m31 m32 m33 m41 m42 m43.

  Definition ent (u v w a b c : R) : R := u *! a +! v *! b +! w *! c.

  Lemma det4_span3 :
    forall u1 v1 w1 u2 v2 w2 u3 v3 w3 u4 v4 w4
           a1 b1 c1 a2 b2 c2 a3 b3 c3 a4 b4 c4 : R,
      det4r (ent u1 v1 w1 a1 b1 c1) (ent u1 v1 w1 a2 b2 c2) (ent u1 v1 w1 a3 b3 c3) (ent u1 v1 w1 a4 b4 c4)
            (ent u2 v2 w2 a1 b1 c1) (ent u2 v2 w2 a2 b2 c2) (ent u2 v2 w2 a3 b3 c3) (ent u2 v2 w2 a4 b4 c4)
            (ent u3 v3 w3 a1 b1 c1) (ent u3 v3 w3 a2 b2 c2) (ent u3 v3 w3 a3 b3 c3) (ent u3 v3 w3 a4 b4 c4)
            (ent u4 v4 w4 a1 b1 c1) (ent u4 v4 w4 a2 b2 c2) (ent u4 v4 w4 a3 b3 c3) (ent u4 v4 w4 a4 b4 c4)
      = r0.
  Proof. intros. unfold det4r, det3r, ent. ring. Qed.

  Lemma det4_identity : det4r r1 r0 r0 r0 r0 r1 r0 r0 r0 r0 r1 r0 r0 r0 r0 r1 = r1.
  Proof. unfold det4r, det3r. ring. Qed.
End Det4.

(* --- the (3,2,2) refutation ------------------------------------------- *)
Section NoWidth2.
  Variable R : Type.
  Variables (r0 r1 : R) (radd rmul rsub : R -> R -> R) (ropp : R -> R).
  Hypothesis Rth : ring_theory r0 r1 radd rmul rsub ropp (@eq R).
  Add Ring nw2_ring : Rth.
  Hypothesis one_neq_zero : r1 <> r0.
  Local Infix "+!" := radd (at level 50, left associativity).
  Local Infix "*!" := rmul (at level 40, left associativity).
  Local Infix "-!" := rsub (at level 50, left associativity).

  (* the rule and the weights -- ARBITRARY functions *)
  Variables rho_lat rho_lon : list nat -> list nat.
  Variables eps1 eps2 : list nat -> list nat -> R.

  (* Soundness of a width-2 per-dimension-canonical scheme at extent     *)
  (* (2, 2): every symmetric tensor is, on every fiber, a fixed          *)
  (* combination of two stored scalars with weights read at the rule's   *)
  (* sorters.  Specializing to the free cell at extent 3 gives           *)
  (* BladeDichotomy's `sound`; this is the tied-extent twin.             *)
  Hypothesis sound :
    forall T : list nat -> Z, diag_symb 2 T = true ->
    forall a b, In a (sorted3 2) -> In b (sorted3 2) ->
    exists p q : R, forall x, In x (cells6 2) ->
      sort3 (lat x) = a -> sort3 (lon x) = b ->
      emb R r0 r1 (T x) = eps1 (rho_lat (lat x)) (rho_lon (lon x)) *! p
                          +! eps2 (rho_lat (lat x)) (rho_lon (lon x)) *! q.

  Lemma cert3_refutes :
    forall i0 i0' ja jb jc, cert3 i0 i0' ja jb jc = true ->
    rho_lat i0 = rho_lat i0' -> False.
  Proof.
    intros i0 i0' ja jb jc Hc Hrho.
    unfold cert3 in Hc.
    repeat match goal with
           | H : andb _ _ = true |- _ => apply andb_true_iff in H as [? ?]
           | H : nleqb _ _ = true |- _ => apply nleqb_eq in H
           | H : Z.eqb _ _ = true |- _ => apply Z.eqb_eq in H
           | H : inb _ _ = true |- _ => apply inb_In in H
           end.
    assert (Hsym := cert3_tensors_symmetric).
    apply andb_true_iff in Hsym as [HsA Hsym].
    apply andb_true_iff in Hsym as [HsB HsC].
    assert (Ha : In [0;0;1]%nat (sorted3 2)) by (vm_compute; tauto).
    assert (Hb : In [0;1;1]%nat (sorted3 2)) by (vm_compute; tauto).
    destruct (sound chi1 HsA _ _ Ha Ha) as [pA [qA EA]].
    destruct (sound chi2 HsB _ _ Hb Ha) as [pB [qB EB]].
    destruct (sound one  HsC _ _ Ha Ha) as [pC [qC EC]].
    pose (u := fun j => eps1 (rho_lat i0) (rho_lon j)).
    pose (v := fun j => eps2 (rho_lat i0) (rho_lon j)).
    (* an entry of the matrix: tensor T stored at the cell of i (i0 or   *)
    (* i0'), read at the point of lon tuple j                            *)
    assert (row : forall T p q i a,
        (forall x, In x (cells6 2) -> sort3 (lat x) = a -> sort3 (lon x) = [0;0;1]%nat ->
          emb R r0 r1 (T x) = eps1 (rho_lat (lat x)) (rho_lon (lon x)) *! p
                              +! eps2 (rho_lat (lat x)) (rho_lon (lon x)) *! q) ->
        sort3 i = a -> rho_lat i = rho_lat i0 ->
        forall j, In (i ++ j)%list (cells6 2) -> lat (i ++ j)%list = i -> lon (i ++ j)%list = j ->
          sort3 j = [0;0;1]%nat ->
          emb R r0 r1 (T (i ++ j)%list) = u j *! p +! v j *! q).
    { intros T p q i a ET Hsa Hi j Hin Hl Hn Hs. specialize (ET _ Hin).
      rewrite Hl, Hn, Hi in ET. unfold u, v. apply ET; auto. }
    assert (E11 : r1 = u ja *! pA +! v ja *! qA).
    { rewrite <- (row chi1 pA qA i0 _ EA ltac:(assumption) eq_refl ja); try (assumption || congruence).
      match goal with H : chi1 (i0 ++ ja)%list = _ |- _ => rewrite H end. reflexivity. }
    assert (E12 : r0 = u jb *! pA +! v jb *! qA).
    { rewrite <- (row chi1 pA qA i0 _ EA ltac:(assumption) eq_refl jb); try (assumption || congruence).
      match goal with H : chi1 (i0 ++ jb)%list = _ |- _ => rewrite H end. reflexivity. }
    assert (E13 : r0 = u jc *! pA +! v jc *! qA).
    { rewrite <- (row chi1 pA qA i0 _ EA ltac:(assumption) eq_refl jc); try (assumption || congruence).
      match goal with H : chi1 (i0 ++ jc)%list = _ |- _ => rewrite H end. reflexivity. }
    assert (E21 : r0 = u ja *! pB +! v ja *! qB).
    { rewrite <- (row chi2 pB qB i0' _ EB ltac:(assumption) (eq_sym Hrho) ja); try (assumption || congruence).
      match goal with H : chi2 (i0' ++ ja)%list = _ |- _ => rewrite H end. reflexivity. }
    assert (E22 : r1 = u jb *! pB +! v jb *! qB).
    { rewrite <- (row chi2 pB qB i0' _ EB ltac:(assumption) (eq_sym Hrho) jb); try (assumption || congruence).
      match goal with H : chi2 (i0' ++ jb)%list = _ |- _ => rewrite H end. reflexivity. }
    assert (E23 : r0 = u jc *! pB +! v jc *! qB).
    { rewrite <- (row chi2 pB qB i0' _ EB ltac:(assumption) (eq_sym Hrho) jc); try (assumption || congruence).
      match goal with H : chi2 (i0' ++ jc)%list = _ |- _ => rewrite H end. reflexivity. }
    assert (E31 : r1 = u ja *! pC +! v ja *! qC).
    { rewrite <- (row one pC qC i0 _ EC ltac:(assumption) eq_refl ja); try (assumption || congruence). reflexivity. }
    assert (E32 : r1 = u jb *! pC +! v jb *! qC).
    { rewrite <- (row one pC qC i0 _ EC ltac:(assumption) eq_refl jb); try (assumption || congruence). reflexivity. }
    assert (E33 : r1 = u jc *! pC +! v jc *! qC).
    { rewrite <- (row one pC qC i0 _ EC ltac:(assumption) eq_refl jc); try (assumption || congruence). reflexivity. }
    (* det [1 0 1; 0 1 1; 0 0 1] = 1, but the rows lie in a 2-span *)
    assert (Hdet := det3_span2 R r0 r1 radd rmul rsub ropp Rth
                    pA qA pB qB pC qC (u ja) (v ja) (u jb) (v jb) (u jc) (v jc)).
    rewrite <- E11, <- E12, <- E13, <- E21, <- E22, <- E23,
            <- E31, <- E32, <- E33 in Hdet.
    ring_simplify in Hdet.
    exact (one_neq_zero Hdet).
  Qed.

  (* THE THEOREM: a lat rule that lets a 001-type and a 011-type tuple    *)
  (* share their sorter admits no width-2 scheme over any ring.          *)
  Theorem lat_overlap_refutes_width2 :
    forall i0 i0', In i0 type001 -> In i0' type011 ->
    In (rho_lat i0) perms3 ->
    pact (rho_lat i0) i0 = [0;0;1]%nat -> pact (rho_lat i0') i0' = [0;1;1]%nat ->
    rho_lat i0 = rho_lat i0' -> False.
  Proof.
    intros i0 i0' Hi Hi' Hs Hp Hp' Heq.
    rewrite <- Heq in Hp'.
    assert (Hok := pick3_ok).
    rewrite forallb_forall in Hok. specialize (Hok i0 Hi).
    rewrite forallb_forall in Hok. specialize (Hok i0' Hi').
    rewrite forallb_forall in Hok. specialize (Hok _ Hs).
    assert (Hpre : andb (nleqb (pact (rho_lat i0) i0) [0;0;1]%nat)
                        (nleqb (pact (rho_lat i0) i0') [0;1;1]%nat) = true).
    { rewrite Hp, Hp'. reflexivity. }
    rewrite Hpre in Hok. cbn [implb] in Hok.
    destruct (pick3 i0 i0') as [[ja jb] jc].
    exact (cert3_refutes i0 i0' ja jb jc Hok Heq).
  Qed.
End NoWidth2.

(* --- the (3,2,3) refutation ------------------------------------------- *)

(* indices with lat over {0,1} and lon over {0,1,2} *)
Definition cells23 : list (list nat) :=
  flat_map (fun a => flat_map (fun b => flat_map (fun c =>
  flat_map (fun d => flat_map (fun e =>
    map (fun f => [a;b;c;d;e;f]%nat) (seq 0 3)) (seq 0 3)) (seq 0 3))
    (seq 0 2)) (seq 0 2)) (seq 0 2).

Example cells23_card : length cells23 = 216%nat.
Proof. vm_compute. reflexivity. Qed.

Definition diag_symb23 (T : list nat -> Z) : bool :=
  forallb (fun x => forallb (fun p => Z.eqb (T (diagact p x)) (T x)) perms3) cells23.

(* The four indicator tensors of the certificate, by pair-code name     *)
(* (BladeDichotomy.paircodes3 : sorted 3*i + j).  In cell (001, 012) the *)
(* orbit of i0 ++ j is fixed by the lon value v under the 1 of i0: A_v   *)
(* has codes {3 + v} u ({0,1,2} \ {v}).  In cell (011, 012) it is fixed *)
(* by the value w under the 0 of i0': B_w has codes {w} u {3+u : u <> w}. *)
Definition indic3 (name : list nat) (x : list nat) : Z :=
  if nleqb (paircodes3 x) name then 1 else 0.
Definition A0 := indic3 [1;2;3]%nat.    (* v = 0 *)
Definition A2 := indic3 [0;1;5]%nat.    (* v = 2 *)
Definition B0 := indic3 [0;4;5]%nat.    (* w = 0 *)
Definition B2 := indic3 [2;3;4]%nat.    (* w = 2 *)

Theorem cert4_tensors_symmetric :
  andb (andb (diag_symb23 A0) (diag_symb23 A2))
       (andb (diag_symb23 B0) (diag_symb23 B2)) = true.
Proof. vm_compute. reflexivity. Qed.

(* the four points: lon permutations j of 012 with (value under the 1 of *)
(* i0, value under the 0 of i0') = (0,1), (1,0), (2,1), (1,2) -- against *)
(* the tensors (A0, B0, A2, B2) their evaluation matrix is the identity   *)
Definition cert4 (i0 i0' j1 j2 j3 j4 : list nat) : bool :=
  inb (i0 ++ j1)%list cells23 && inb (i0 ++ j2)%list cells23
  && inb (i0 ++ j3)%list cells23 && inb (i0 ++ j4)%list cells23
  && inb (i0' ++ j1)%list cells23 && inb (i0' ++ j2)%list cells23
  && inb (i0' ++ j3)%list cells23 && inb (i0' ++ j4)%list cells23
  && nleqb (lat (i0 ++ j1)%list) i0 && nleqb (lat (i0 ++ j2)%list) i0
  && nleqb (lat (i0 ++ j3)%list) i0 && nleqb (lat (i0 ++ j4)%list) i0
  && nleqb (lon (i0 ++ j1)%list) j1 && nleqb (lon (i0 ++ j2)%list) j2
  && nleqb (lon (i0 ++ j3)%list) j3 && nleqb (lon (i0 ++ j4)%list) j4
  && nleqb (lat (i0' ++ j1)%list) i0' && nleqb (lat (i0' ++ j2)%list) i0'
  && nleqb (lat (i0' ++ j3)%list) i0' && nleqb (lat (i0' ++ j4)%list) i0'
  && nleqb (lon (i0' ++ j1)%list) j1 && nleqb (lon (i0' ++ j2)%list) j2
  && nleqb (lon (i0' ++ j3)%list) j3 && nleqb (lon (i0' ++ j4)%list) j4
  && nleqb (sort3 i0) [0;0;1]%nat && nleqb (sort3 i0') [0;1;1]%nat
  && nleqb (sort3 j1) [0;1;2]%nat && nleqb (sort3 j2) [0;1;2]%nat
  && nleqb (sort3 j3) [0;1;2]%nat && nleqb (sort3 j4) [0;1;2]%nat
  && Z.eqb (A0 (i0 ++ j1)%list) 1 && Z.eqb (A0 (i0 ++ j2)%list) 0
  && Z.eqb (A0 (i0 ++ j3)%list) 0 && Z.eqb (A0 (i0 ++ j4)%list) 0
  && Z.eqb (B0 (i0' ++ j1)%list) 0 && Z.eqb (B0 (i0' ++ j2)%list) 1
  && Z.eqb (B0 (i0' ++ j3)%list) 0 && Z.eqb (B0 (i0' ++ j4)%list) 0
  && Z.eqb (A2 (i0 ++ j1)%list) 0 && Z.eqb (A2 (i0 ++ j2)%list) 0
  && Z.eqb (A2 (i0 ++ j3)%list) 1 && Z.eqb (A2 (i0 ++ j4)%list) 0
  && Z.eqb (B2 (i0' ++ j1)%list) 0 && Z.eqb (B2 (i0' ++ j2)%list) 0
  && Z.eqb (B2 (i0' ++ j3)%list) 0 && Z.eqb (B2 (i0' ++ j4)%list) 1.

Definition pos_of (v : nat) (l : list nat) : nat := idx_of [v] (map (fun a => [a]) l).

Definition pick4 (i0 i0' : list nat) : list (list nat) :=
  map (fun vw => hd [] (filter (fun j : list nat => andb (Nat.eqb (nth (pos_of 1 i0) j 9%nat) (fst vw))
                                             (Nat.eqb (nth (pos_of 0 i0') j 9%nat) (snd vw)))
                               perms3))
      [(0, 1); (1, 0); (2, 1); (1, 2)]%nat.

Theorem pick4_ok :
  forallb (fun i0 => forallb (fun i0' => forallb (fun s0 =>
    implb (andb (nleqb (pact s0 i0) [0;0;1]%nat) (nleqb (pact s0 i0') [0;1;1]%nat))
          (match pick4 i0 i0' with
           | [j1; j2; j3; j4] => cert4 i0 i0' j1 j2 j3 j4
           | _ => false
           end))
    perms3) type011) type001 = true.
Proof. vm_compute. reflexivity. Qed.

Section NoWidth3.
  Variable R : Type.
  Variables (r0 r1 : R) (radd rmul rsub : R -> R -> R) (ropp : R -> R).
  Hypothesis Rth : ring_theory r0 r1 radd rmul rsub ropp (@eq R).
  Add Ring nw3_ring : Rth.
  Hypothesis one_neq_zero : r1 <> r0.
  Local Infix "+!" := radd (at level 50, left associativity).
  Local Infix "*!" := rmul (at level 40, left associativity).
  Local Infix "-!" := rsub (at level 50, left associativity).

  Variables rho_lat rho_lon : list nat -> list nat.
  Variables eps1 eps2 eps3 : list nat -> list nat -> R.

  Hypothesis sound :
    forall T : list nat -> Z, diag_symb23 T = true ->
    forall a b, In a (sorted3 2) -> In b (sorted3 3) ->
    exists p q s : R, forall x, In x cells23 ->
      sort3 (lat x) = a -> sort3 (lon x) = b ->
      emb R r0 r1 (T x) = eps1 (rho_lat (lat x)) (rho_lon (lon x)) *! p
                          +! eps2 (rho_lat (lat x)) (rho_lon (lon x)) *! q
                          +! eps3 (rho_lat (lat x)) (rho_lon (lon x)) *! s.

  Lemma cert4_refutes :
    forall i0 i0' j1 j2 j3 j4, cert4 i0 i0' j1 j2 j3 j4 = true ->
    rho_lat i0 = rho_lat i0' -> False.
  Proof.
    intros i0 i0' j1 j2 j3 j4 Hc Hrho.
    unfold cert4 in Hc.
    repeat match goal with
           | H : andb _ _ = true |- _ => apply andb_true_iff in H as [? ?]
           | H : nleqb _ _ = true |- _ => apply nleqb_eq in H
           | H : Z.eqb _ _ = true |- _ => apply Z.eqb_eq in H
           | H : inb _ _ = true |- _ => apply inb_In in H
           end.
    assert (Hsym := cert4_tensors_symmetric).
    apply andb_true_iff in Hsym as [HA HB].
    apply andb_true_iff in HA as [HA0 HA2].
    apply andb_true_iff in HB as [HB0 HB2].
    assert (Ha : In [0;0;1]%nat (sorted3 2)) by (vm_compute; tauto).
    assert (Ha' : In [0;1;1]%nat (sorted3 2)) by (vm_compute; tauto).
    assert (Hb : In [0;1;2]%nat (sorted3 3)) by (vm_compute; tauto).
    destruct (sound A0 HA0 _ _ Ha Hb)  as [a1 [b1 [c1 EA0]]].
    destruct (sound B0 HB0 _ _ Ha' Hb) as [a2 [b2 [c2 EB0]]].
    destruct (sound A2 HA2 _ _ Ha Hb)  as [a3 [b3 [c3 EA2]]].
    destruct (sound B2 HB2 _ _ Ha' Hb) as [a4 [b4 [c4 EB2]]].
    pose (u := fun j => eps1 (rho_lat i0) (rho_lon j)).
    pose (v := fun j => eps2 (rho_lat i0) (rho_lon j)).
    pose (w := fun j => eps3 (rho_lat i0) (rho_lon j)).
    assert (row : forall T a b c i a',
        (forall x, In x cells23 -> sort3 (lat x) = a' -> sort3 (lon x) = [0;1;2]%nat ->
          emb R r0 r1 (T x) = eps1 (rho_lat (lat x)) (rho_lon (lon x)) *! a
                              +! eps2 (rho_lat (lat x)) (rho_lon (lon x)) *! b
                              +! eps3 (rho_lat (lat x)) (rho_lon (lon x)) *! c) ->
        sort3 i = a' -> rho_lat i = rho_lat i0 ->
        forall j, In (i ++ j)%list cells23 -> lat (i ++ j)%list = i -> lon (i ++ j)%list = j ->
          sort3 j = [0;1;2]%nat ->
          emb R r0 r1 (T (i ++ j)%list) = ent R radd rmul (u j) (v j) (w j) a b c).
    { intros T a b c i a' ET Hsa Hi j Hin Hl Hn Hs. specialize (ET _ Hin).
      rewrite Hl, Hn, Hi in ET. unfold ent, u, v, w. apply ET; auto. }
    assert (D := det4_span3 R r0 r1 radd rmul rsub ropp Rth
                   (u j1) (v j1) (w j1) (u j2) (v j2) (w j2)
                   (u j3) (v j3) (w j3) (u j4) (v j4) (w j4)
                   a1 b1 c1 a2 b2 c2 a3 b3 c3 a4 b4 c4).
    (* the sixteen entries, each replaced by its known 0/1 value *)
    rewrite <- (row A0 a1 b1 c1 i0 _ EA0 ltac:(assumption) eq_refl j1) in D by (assumption || congruence).
    rewrite <- (row A0 a1 b1 c1 i0 _ EA0 ltac:(assumption) eq_refl j2) in D by (assumption || congruence).
    rewrite <- (row A0 a1 b1 c1 i0 _ EA0 ltac:(assumption) eq_refl j3) in D by (assumption || congruence).
    rewrite <- (row A0 a1 b1 c1 i0 _ EA0 ltac:(assumption) eq_refl j4) in D by (assumption || congruence).
    rewrite <- (row B0 a2 b2 c2 i0' _ EB0 ltac:(assumption) (eq_sym Hrho) j1) in D by (assumption || congruence).
    rewrite <- (row B0 a2 b2 c2 i0' _ EB0 ltac:(assumption) (eq_sym Hrho) j2) in D by (assumption || congruence).
    rewrite <- (row B0 a2 b2 c2 i0' _ EB0 ltac:(assumption) (eq_sym Hrho) j3) in D by (assumption || congruence).
    rewrite <- (row B0 a2 b2 c2 i0' _ EB0 ltac:(assumption) (eq_sym Hrho) j4) in D by (assumption || congruence).
    rewrite <- (row A2 a3 b3 c3 i0 _ EA2 ltac:(assumption) eq_refl j1) in D by (assumption || congruence).
    rewrite <- (row A2 a3 b3 c3 i0 _ EA2 ltac:(assumption) eq_refl j2) in D by (assumption || congruence).
    rewrite <- (row A2 a3 b3 c3 i0 _ EA2 ltac:(assumption) eq_refl j3) in D by (assumption || congruence).
    rewrite <- (row A2 a3 b3 c3 i0 _ EA2 ltac:(assumption) eq_refl j4) in D by (assumption || congruence).
    rewrite <- (row B2 a4 b4 c4 i0' _ EB2 ltac:(assumption) (eq_sym Hrho) j1) in D by (assumption || congruence).
    rewrite <- (row B2 a4 b4 c4 i0' _ EB2 ltac:(assumption) (eq_sym Hrho) j2) in D by (assumption || congruence).
    rewrite <- (row B2 a4 b4 c4 i0' _ EB2 ltac:(assumption) (eq_sym Hrho) j3) in D by (assumption || congruence).
    rewrite <- (row B2 a4 b4 c4 i0' _ EB2 ltac:(assumption) (eq_sym Hrho) j4) in D by (assumption || congruence).
    repeat match goal with
           | H : A0 _ = _ |- _ => rewrite H in D; clear H
           | H : A2 _ = _ |- _ => rewrite H in D; clear H
           | H : B0 _ = _ |- _ => rewrite H in D; clear H
           | H : B2 _ = _ |- _ => rewrite H in D; clear H
           end.
    change (emb R r0 r1 1) with r1 in D. change (emb R r0 r1 0) with r0 in D.
    rewrite (det4_identity R r0 r1 radd rmul rsub ropp Rth) in D.
    exact (one_neq_zero D).
  Qed.

  Theorem lat_overlap_refutes_width3 :
    forall i0 i0', In i0 type001 -> In i0' type011 ->
    In (rho_lat i0) perms3 ->
    pact (rho_lat i0) i0 = [0;0;1]%nat -> pact (rho_lat i0') i0' = [0;1;1]%nat ->
    rho_lat i0 = rho_lat i0' -> False.
  Proof.
    intros i0 i0' Hi Hi' Hs Hp Hp' Heq.
    rewrite <- Heq in Hp'.
    assert (Hok := pick4_ok).
    rewrite forallb_forall in Hok. specialize (Hok i0 Hi).
    rewrite forallb_forall in Hok. specialize (Hok i0' Hi').
    rewrite forallb_forall in Hok. specialize (Hok _ Hs).
    assert (Hpre : andb (nleqb (pact (rho_lat i0) i0) [0;0;1]%nat)
                        (nleqb (pact (rho_lat i0) i0') [0;1;1]%nat) = true).
    { rewrite Hp, Hp'. reflexivity. }
    rewrite Hpre in Hok. cbn [implb] in Hok.
    destruct (pick4 i0 i0') as [|j1 [|j2 [|j3 [|j4 [|? ?]]]]]; try discriminate.
    exact (cert4_refutes i0 i0' j1 j2 j3 j4 Hok Heq).
  Qed.
End NoWidth3.

(* ===================================================================== *)
(* PART C -- the positive half.                                          *)
(* ===================================================================== *)

(* symmetric tensors factor through the orbit name: two indices with the *)
(* same name are related by a diagonal permutation (computed), so a      *)
(* symmetric tensor takes the same value at both                         *)
Definition same_orbit_connected (cells : list (list nat)) (name : list nat -> list nat) : bool :=
  forallb (fun x => forallb (fun y =>
    implb (nleqb (name x) (name y)) (existsb (fun p => nleqb (diagact p x) y) perms3))
    cells) cells.

Theorem connected_2 : same_orbit_connected (cells6 2) (orbit_name 2) = true.
Proof. vm_compute. reflexivity. Qed.

Theorem connected_23 : same_orbit_connected cells23 paircodes3 = true.
Proof. vm_compute. reflexivity. Qed.

Lemma symb_value_by_name :
  forall cells name (T : list nat -> Z),
    same_orbit_connected cells name = true ->
    forallb (fun x => forallb (fun p => Z.eqb (T (diagact p x)) (T x)) perms3) cells = true ->
    forall x y, In x cells -> In y cells -> name x = name y -> T x = T y.
Proof.
  intros cells name T Hc Hs x y Hx Hy Hn.
  unfold same_orbit_connected in Hc. rewrite forallb_forall in Hc.
  specialize (Hc x Hx). rewrite forallb_forall in Hc. specialize (Hc y Hy).
  rewrite Hn, nleqb_refl in Hc. cbn [implb] in Hc.
  apply existsb_exists in Hc as [p [Hp Hpy]]. apply nleqb_eq in Hpy.
  rewrite forallb_forall in Hs. specialize (Hs x Hx).
  rewrite forallb_forall in Hs. specialize (Hs p Hp).
  apply Z.eqb_eq in Hs. rewrite <- Hpy. exact (eq_sym Hs).
Qed.

(* --- C(i): the width-2 scheme at (3,2,2) ------------------------------ *)

(* the fiber of a cell and its orbit representatives: a FULL cell (both  *)
(* tuples non-constant) has a 3-point orbit (small) and a 6-point orbit *)
(* (big); every other cell is one orbit                                 *)
Definition fiber2 (a b : list nat) : list (list nat) :=
  filter (fun x => andb (nleqb (sort3 (lat x)) a) (nleqb (sort3 (lon x)) b)) (cells6 2).

Definition orbit_size2 (x : list nat) : nat :=
  length (filter (fun y => nleqb (orbit_name 2 y) (orbit_name 2 x)) (cells6 2)).

Definition small2 (a b : list nat) : list nat :=
  hd [] (filter (fun x => Nat.eqb (orbit_size2 x) 3) (fiber2 a b)).
Definition big2 (a b : list nat) : list nat :=
  hd [] (filter (fun x => Nat.eqb (orbit_size2 x) 6) (fiber2 a b)).
Definition rep2 (a b : list nat) : list nat := hd [] (fiber2 a b).

Definition full_cell (a b : list nat) : bool :=
  andb (negb (is_const a)) (negb (is_const b)).

(* the second weight: 1 at (sigma, tau) iff some small-orbit index of a  *)
(* full cell reads its weights there under the rule (sl, sm)             *)
Definition chi (sl sm : bool) (sigma tau : list nat) : bool :=
  existsb (fun x => andb (full_cell (sort3 (lat x)) (sort3 (lon x)))
                    (andb (Nat.eqb (orbit_size2 x) 3)
                          (andb (inb sigma (sorters_rule sl (lat x)))
                                (inb tau (sorters_rule sm (lon x))))))
          (cells6 2).

(* the scheme's read: P1 + chi * P2 with P1 = T (big), P2 = T (small) -   *)
(* T (big) on a full cell; the single stored value elsewhere (P2 = 0)    *)
Definition read322 (sl sm : bool) (T : list nat -> Z) (x sigma tau : list nat) : Z :=
  if full_cell (sort3 (lat x)) (sort3 (lon x))
  then T (big2 (sort3 (lat x)) (sort3 (lon x)))
       + (if chi sl sm sigma tau then 1 else 0)
         * (T (small2 (sort3 (lat x)) (sort3 (lon x))) - T (big2 (sort3 (lat x)) (sort3 (lon x))))
  else T (rep2 (sort3 (lat x)) (sort3 (lon x))).

(* the computed core: the index the scheme reads has the same orbit     *)
(* name as x, for every x and every pair of rule-sorters                 *)
Definition check322_one (sl sm : bool) (x sigma tau : list nat) : bool :=
  if full_cell (sort3 (lat x)) (sort3 (lon x))
  then andb (inb (small2 (sort3 (lat x)) (sort3 (lon x))) (cells6 2))
       (andb (inb (big2 (sort3 (lat x)) (sort3 (lon x))) (cells6 2))
             (nleqb (orbit_name 2 (if chi sl sm sigma tau
                                   then small2 (sort3 (lat x)) (sort3 (lon x))
                                   else big2 (sort3 (lat x)) (sort3 (lon x))))
                    (orbit_name 2 x)))
  else andb (inb (rep2 (sort3 (lat x)) (sort3 (lon x))) (cells6 2))
            (nleqb (orbit_name 2 (rep2 (sort3 (lat x)) (sort3 (lon x)))) (orbit_name 2 x)).

Theorem check322 :
  forallb (fun sl => forallb (fun sm =>
    forallb (fun x => forallb (fun sigma => forallb (fun tau =>
      check322_one sl sm x sigma tau)
      (sorters_rule sm (lon x))) (sorters_rule sl (lat x))) (cells6 2))
    [true; false]) [true; false] = true.
Proof. vm_compute. reflexivity. Qed.

Lemma In_bools : forall b : bool, In b [true; false].
Proof. destruct b; cbn; tauto. Qed.

(* THE THEOREM: under any of the four parity-graded rules (constant      *)
(* tuples' sorters free: sorters_rule leaves them unconstrained), every  *)
(* symmetric tensor at extent (2,2) is read back exactly at every index *)
(* from two scalars per cell with weights (1, chi (sigma, tau)).         *)
Theorem scheme322_sound :
  forall sl sm (T : list nat -> Z), diag_symb 2 T = true ->
  forall x, In x (cells6 2) ->
  forall sigma tau, In sigma (sorters_rule sl (lat x)) -> In tau (sorters_rule sm (lon x)) ->
  T x = read322 sl sm T x sigma tau.
Proof.
  intros sl sm T HT x Hx sigma tau Hs Ht.
  assert (Hc := check322).
  rewrite forallb_forall in Hc. specialize (Hc sl (In_bools sl)).
  rewrite forallb_forall in Hc. specialize (Hc sm (In_bools sm)).
  rewrite forallb_forall in Hc. specialize (Hc x Hx).
  rewrite forallb_forall in Hc. specialize (Hc sigma Hs).
  rewrite forallb_forall in Hc. specialize (Hc tau Ht).
  unfold check322_one in Hc. unfold read322.
  destruct (full_cell (sort3 (lat x)) (sort3 (lon x))).
  - apply andb_true_iff in Hc as [Hsm Hc]. apply andb_true_iff in Hc as [Hbg Hc].
    apply inb_In in Hsm. apply inb_In in Hbg.
    destruct (chi sl sm sigma tau); apply nleqb_eq in Hc.
    + rewrite (symb_value_by_name _ _ T connected_2 HT x _ Hx Hsm (eq_sym Hc)). ring.
    + rewrite (symb_value_by_name _ _ T connected_2 HT x _ Hx Hbg (eq_sym Hc)). ring.
  - apply andb_true_iff in Hc as [Hr Hc]. apply inb_In in Hr. apply nleqb_eq in Hc.
    exact (symb_value_by_name _ _ T connected_2 HT x _ Hx Hr (eq_sym Hc)).
Qed.

(* --- C(ii): the width-3 scheme at (3,2,3) ----------------------------- *)

(* stabilizers of the two non-constant lat types, and the coset          *)
(* representative the scheme reads: the first g in perms3 with g in      *)
(* mu H (right cosets in pmul order).  T (a ++ mu . b) depends on mu only  *)
(* through mu H_a, since pact h (pact mu b) = pact (pmul mu h) b and a is  *)
(* fixed by h.                                                           *)
Definition H001 : list (list nat) := [[0;1;2]; [1;0;2]]%nat.
Definition H011 : list (list nat) := [[0;1;2]; [0;2;1]]%nat.

Definition Htype (sl : bool) (sigma : list nat) : list (list nat) :=
  if Z.eqb (sgn3 sigma) (if sl then 1 else -1) then H001 else H011.

Definition coset_rep (H : list (list nat)) (mu : list nat) : list nat :=
  hd mu (filter (fun g => existsb (fun h => nleqb (pmul mu h) g) H) perms3).

Theorem coset_rep_three_values :
  andb (forallb (fun mu => inb (coset_rep H001 mu) [[0;1;2]; [0;2;1]; [1;2;0]]%nat) perms3)
       (forallb (fun mu => inb (coset_rep H011 mu) [[0;1;2]; [1;0;2]; [2;0;1]]%nat) perms3) = true.
Proof. vm_compute. reflexivity. Qed.

(* the read: T (a ++ g . b) with g the coset representative of           *)
(* mu = tau^-1 sigma in the stabilizer of sigma's type -- three stored   *)
(* scalars per cell, one per coset                                       *)
Definition target323 (sl : bool) (x sigma tau : list nat) : list nat :=
  (sort3 (lat x) ++ pact (coset_rep (Htype sl sigma) (pmul (pinv tau) sigma)) (sort3 (lon x)))%list.

Definition read323 (sl : bool) (T : list nat -> Z) (x sigma tau : list nat) : Z :=
  T (target323 sl x sigma tau).

Theorem check323 :
  forallb (fun sl =>
    forallb (fun x => forallb (fun sigma => forallb (fun tau =>
      andb (inb (target323 sl x sigma tau) cells23)
           (nleqb (paircodes3 (target323 sl x sigma tau)) (paircodes3 x)))
      (sorters (lon x))) (sorters_rule sl (lat x))) cells23)
    [true; false] = true.
Proof. vm_compute. reflexivity. Qed.

(* THE THEOREM: under either parity-graded LAT rule and ANY lon tie-break *)
(* (tau ranges over every sorter of lon x), every symmetric tensor at    *)
(* extent (2,3) is read back exactly at every index from three scalars  *)
(* per cell.  The counting bound 3 = |S_2 \ S_3| is attained: the        *)
(* exception does not die at (3,2,3), it moves up with the bound.       *)
Theorem scheme323_sound :
  forall sl (T : list nat -> Z), diag_symb23 T = true ->
  forall x, In x cells23 ->
  forall sigma tau, In sigma (sorters_rule sl (lat x)) -> In tau (sorters (lon x)) ->
  T x = read323 sl T x sigma tau.
Proof.
  intros sl T HT x Hx sigma tau Hs Ht.
  assert (Hc := check323).
  rewrite forallb_forall in Hc. specialize (Hc sl (In_bools sl)).
  rewrite forallb_forall in Hc. specialize (Hc x Hx).
  rewrite forallb_forall in Hc. specialize (Hc sigma Hs).
  rewrite forallb_forall in Hc. specialize (Hc tau Ht).
  apply andb_true_iff in Hc as [Hin Hn]. apply inb_In in Hin. apply nleqb_eq in Hn.
  exact (symb_value_by_name _ _ T connected_23 HT x _ Hx Hin (eq_sym Hn)).
Qed.

(* ===================================================================== *)
(* Closing note.  Part A: the 4-of-4096 census is exact and the four     *)
(* rules are the parity-graded ones.  Part B: an overlapping rule has   *)
(* no width-2 scheme at (3,2,2) and no width-3 scheme at (3,2,3); since *)
(* R_001 and R_011 are disjoint iff parity-graded (Part A), the counting *)
(* bound is attained at both extents EXACTLY by the parity-graded rules *)
(* (Part C gives the schemes).  Not here: the minimal width of the OTHER *)
(* rules (3, 4 or 5 at (3,2,2) by the scripts' exact certificates and    *)
(* witnesses) and the lon mirror of Part B.                              *)
(* ===================================================================== *)
