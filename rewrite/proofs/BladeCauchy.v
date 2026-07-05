(* ===================================================================== *)
(* BladeCauchy.v -- THE r = 2 CAUCHY STORAGE SPLIT.                      *)
(*                                                                       *)
(* Upgrades the v11 research note from conjecture to theorem.  For ANY   *)
(* jointly-symmetric rank-4 tensor T over a 2D product index space --    *)
(* joint (diagonal) symmetry being the ONLY symmetry the Group Law       *)
(* grants a commutative kernel over one identity group -- define         *)
(*                                                                       *)
(*   Psym = T + (lon-swapped T)      Qalt = T - (lon-swapped T)          *)
(*                                                                       *)
(* Checked here, over Z (the x2 convention avoids division):             *)
(*   reconstruction        2*T = Psym + Qalt pointwise                   *)
(*   Psym_{lon,lat,both}   Psym has FULL product symmetry S2 x S2        *)
(*                         (the lat direction uses joint symmetry --     *)
(*                         that is where the decomposition earns its     *)
(*                         keep)                                         *)
(*   Qalt_{lon,lat,both}   Qalt is antisym (x) antisym, sign-tracked     *)
(*   Qalt_diag_*           Qalt vanishes when either dimension's         *)
(*                         indices coincide                              *)
(*   cauchy_split_access   THE HEADLINE: 2*T at ANY logical index        *)
(*                         equals Psym at the per-dimension-sorted cell  *)
(*                         plus (product of per-dim sort signs) * Qalt   *)
(*                         at the sorted cell.  Per-dimension canonical  *)
(*                         product storage of the two components is      *)
(*                         LOSSLESS for the joint tensor.                *)
(*   cauchy_cell_count     exact accounting, division-free:              *)
(*                         L(L+1)M(M+1) + L(L-1)M(M-1) = 2 LM(LM+1),     *)
(*                         i.e. cells(Psym) + cells(Qalt) equals the     *)
(*                         joint multiset count exactly -- no overhead,  *)
(*                         no loss                                       *)
(*   cauchy_cells_3_3      36 + 9 = 45, with all three counts COMPUTED   *)
(*                         from the actual enumerations (enum and the    *)
(*                         antisymmetric arrow)                          *)
(*                                                                       *)
(* Consequence: the single-identity-group r = 2 case (covariance-class   *)
(* statistics -- the flagship second moment) regains EXACT               *)
(* per-dimension product-structured storage: SymIdx (x) SymIdx for       *)
(* Psym, sign-tracked AntisymIdx (x) AntisymIdx for Qalt.  The audit's   *)
(* "flattening is forced" conclusion is hereby AMENDED at r = 2: naive   *)
(* single-component product storage is lossy (BladeCounting), but the    *)
(* two-component split is exact.  This is the Cauchy decomposition       *)
(*   Sym^2(V (x) W) ~= Sym^2 V (x) Sym^2 W  (+)  Wedge^2 V (x) Wedge^2 W *)
(* made storage-operational.                                             *)
(*                                                                       *)
(* Honest scope:                                                         *)
(*  - r = 2 ONLY.  For r >= 3 the Cauchy decomposition contains MIXED    *)
(*    Schur components (e.g. S^(2,1)), which are not plain sym/antisym   *)
(*    products; extending the split needs Young-symmetrizer storage      *)
(*    types and is genuinely open.                                       *)
(*  - Storage TOTALS equal the joint count; the win is per-dimension     *)
(*    STRUCTURE (independent per-dim triangular iteration, per-dim       *)
(*    currying/addressing, and componentwise interpretation), not fewer  *)
(*    cells.  Each logical read costs one Psym read, one signed Qalt     *)
(*    read, and a halving.                                               *)
(*  - Composition with the concrete layouts (lj for Psym per dim, alj    *)
(*    with sign for Qalt) is mechanical and deferred.                    *)
(*                                                                       *)
(* Imports BladeDMWF, BladeArrow.  Coq 8.18, stdlib only.                *)
(* ===================================================================== *)

From Blade Require Import BladeDMWF BladeArrow.
Require Import List Arith Lia ZArith Psatz.
Import ListNotations.

Section CauchySplit.
  Variable T : nat -> nat -> nat -> nat -> Z.
  (* joint (diagonal) symmetry: slots (i1,j1) and (i2,j2) exchange *)
  Hypothesis Tjoint : forall i1 j1 i2 j2, T i2 j2 i1 j1 = T i1 j1 i2 j2.

  Definition Psym (i1 j1 i2 j2 : nat) : Z :=
    (T i1 j1 i2 j2 + T i1 j2 i2 j1)%Z.
  Definition Qalt (i1 j1 i2 j2 : nat) : Z :=
    (T i1 j1 i2 j2 - T i1 j2 i2 j1)%Z.

  Lemma reconstruction : forall i1 j1 i2 j2,
    (2 * T i1 j1 i2 j2)%Z = (Psym i1 j1 i2 j2 + Qalt i1 j1 i2 j2)%Z.
  Proof. intros. unfold Psym, Qalt. ring. Qed.

  (* ---- Psym: full product symmetry ---- *)

  Lemma Psym_lon : forall i1 j1 i2 j2, Psym i1 j2 i2 j1 = Psym i1 j1 i2 j2.
  Proof. intros. unfold Psym. ring. Qed.

  Lemma Psym_lat : forall i1 j1 i2 j2, Psym i2 j1 i1 j2 = Psym i1 j1 i2 j2.
  Proof.
    intros. unfold Psym.
    rewrite (Tjoint i1 j2 i2 j1), (Tjoint i1 j1 i2 j2). ring.
  Qed.

  Lemma Psym_both : forall i1 j1 i2 j2, Psym i2 j2 i1 j1 = Psym i1 j1 i2 j2.
  Proof.
    intros. unfold Psym.
    rewrite (Tjoint i1 j1 i2 j2), (Tjoint i1 j2 i2 j1). ring.
  Qed.

  (* ---- Qalt: antisym (x) antisym ---- *)

  Lemma Qalt_lon : forall i1 j1 i2 j2,
    Qalt i1 j2 i2 j1 = (- Qalt i1 j1 i2 j2)%Z.
  Proof. intros. unfold Qalt. ring. Qed.

  Lemma Qalt_lat : forall i1 j1 i2 j2,
    Qalt i2 j1 i1 j2 = (- Qalt i1 j1 i2 j2)%Z.
  Proof.
    intros. unfold Qalt.
    rewrite (Tjoint i1 j2 i2 j1), (Tjoint i1 j1 i2 j2). ring.
  Qed.

  Lemma Qalt_both : forall i1 j1 i2 j2, Qalt i2 j2 i1 j1 = Qalt i1 j1 i2 j2.
  Proof.
    intros. unfold Qalt.
    rewrite (Tjoint i1 j1 i2 j2), (Tjoint i1 j2 i2 j1). ring.
  Qed.

  Lemma Qalt_diag_lat : forall i j1 j2, Qalt i j1 i j2 = 0%Z.
  Proof. intros. assert (H := Qalt_lat i j1 i j2). lia. Qed.

  Lemma Qalt_diag_lon : forall i1 i2 j, Qalt i1 j i2 j = 0%Z.
  Proof. intros. assert (H := Qalt_lon i1 j i2 j). lia. Qed.

  (* ---- signed canonical access ---- *)

  Definition qsign (a b : nat) : Z :=
    if Nat.ltb a b then 1%Z else if Nat.ltb b a then (-1)%Z else 0%Z.

  Lemma qsign_lt : forall a b, a < b -> qsign a b = 1%Z.
  Proof.
    intros a b H. unfold qsign.
    rewrite (proj2 (Nat.ltb_lt a b) H). reflexivity.
  Qed.

  Lemma qsign_gt : forall a b, b < a -> qsign a b = (-1)%Z.
  Proof.
    intros a b H. unfold qsign.
    replace (Nat.ltb a b) with false
      by (symmetry; apply Nat.ltb_ge; lia).
    rewrite (proj2 (Nat.ltb_lt b a) H). reflexivity.
  Qed.

  Lemma qsign_eq : forall a, qsign a a = 0%Z.
  Proof. intro. unfold qsign. rewrite Nat.ltb_irrefl. reflexivity. Qed.

  (* THE HEADLINE: the two per-dimension-canonical component stores      *)
  (* losslessly determine the joint tensor at every logical index.       *)
  Theorem cauchy_split_access : forall i1 j1 i2 j2,
    (2 * T i1 j1 i2 j2)%Z
    = (Psym (Nat.min i1 i2) (Nat.min j1 j2) (Nat.max i1 i2) (Nat.max j1 j2)
       + qsign i1 i2 * qsign j1 j2
         * Qalt (Nat.min i1 i2) (Nat.min j1 j2)
                (Nat.max i1 i2) (Nat.max j1 j2))%Z.
  Proof.
    intros.
    destruct (Nat.lt_trichotomy i1 i2) as [Hi | [Hi | Hi]];
      destruct (Nat.lt_trichotomy j1 j2) as [Hj | [Hj | Hj]].
    - (* i1 < i2, j1 < j2 *)
      rewrite (Nat.min_l i1 i2) by lia. rewrite (Nat.max_r i1 i2) by lia.
      rewrite (Nat.min_l j1 j2) by lia. rewrite (Nat.max_r j1 j2) by lia.
      rewrite (qsign_lt i1 i2 Hi), (qsign_lt j1 j2 Hj).
      rewrite reconstruction. ring.
    - (* i1 < i2, j1 = j2 *)
      subst j2.
      rewrite (Nat.min_l i1 i2) by lia. rewrite (Nat.max_r i1 i2) by lia.
      rewrite Nat.min_id, Nat.max_id.
      rewrite (qsign_lt i1 i2 Hi), (qsign_eq j1).
      rewrite reconstruction, (Qalt_diag_lon i1 i2 j1). ring.
    - (* i1 < i2, j2 < j1 *)
      rewrite (Nat.min_l i1 i2) by lia. rewrite (Nat.max_r i1 i2) by lia.
      rewrite (Nat.min_r j1 j2) by lia. rewrite (Nat.max_l j1 j2) by lia.
      rewrite (qsign_lt i1 i2 Hi), (qsign_gt j1 j2 Hj).
      rewrite (Psym_lon i1 j1 i2 j2), (Qalt_lon i1 j1 i2 j2).
      rewrite reconstruction. ring.
    - (* i1 = i2, j1 < j2 *)
      subst i2.
      rewrite Nat.min_id, Nat.max_id.
      rewrite (Nat.min_l j1 j2) by lia. rewrite (Nat.max_r j1 j2) by lia.
      rewrite (qsign_eq i1), (qsign_lt j1 j2 Hj).
      rewrite reconstruction, (Qalt_diag_lat i1 j1 j2). ring.
    - (* i1 = i2, j1 = j2 *)
      subst i2. subst j2.
      rewrite !Nat.min_id, !Nat.max_id.
      rewrite !qsign_eq.
      rewrite reconstruction, (Qalt_diag_lat i1 j1 j1). ring.
    - (* i1 = i2, j2 < j1 *)
      subst i2.
      rewrite Nat.min_id, Nat.max_id.
      rewrite (Nat.min_r j1 j2) by lia. rewrite (Nat.max_l j1 j2) by lia.
      rewrite (qsign_eq i1), (qsign_gt j1 j2 Hj).
      rewrite (Psym_lon i1 j1 i1 j2).
      rewrite reconstruction, (Qalt_diag_lat i1 j1 j2). ring.
    - (* i2 < i1, j1 < j2 *)
      rewrite (Nat.min_r i1 i2) by lia. rewrite (Nat.max_l i1 i2) by lia.
      rewrite (Nat.min_l j1 j2) by lia. rewrite (Nat.max_r j1 j2) by lia.
      rewrite (qsign_gt i1 i2 Hi), (qsign_lt j1 j2 Hj).
      rewrite (Psym_lat i1 j1 i2 j2), (Qalt_lat i1 j1 i2 j2).
      rewrite reconstruction. ring.
    - (* i2 < i1, j1 = j2 *)
      subst j2.
      rewrite (Nat.min_r i1 i2) by lia. rewrite (Nat.max_l i1 i2) by lia.
      rewrite Nat.min_id, Nat.max_id.
      rewrite (qsign_gt i1 i2 Hi), (qsign_eq j1).
      rewrite (Psym_lat i1 j1 i2 j1).
      rewrite reconstruction, (Qalt_diag_lon i1 i2 j1). ring.
    - (* i2 < i1, j2 < j1 *)
      rewrite (Nat.min_r i1 i2) by lia. rewrite (Nat.max_l i1 i2) by lia.
      rewrite (Nat.min_r j1 j2) by lia. rewrite (Nat.max_l j1 j2) by lia.
      rewrite (qsign_gt i1 i2 Hi), (qsign_gt j1 j2 Hj).
      rewrite (Psym_both i1 j1 i2 j2), (Qalt_both i1 j1 i2 j2).
      rewrite reconstruction. ring.
  Qed.
End CauchySplit.

(* ---- exact cell accounting ---- *)

(* cells(Psym product store) + cells(Qalt product store) = joint         *)
(* multiset count, division-free (all sides x4):                        *)
(*   [L(L+1)/2][M(M+1)/2] + [L(L-1)/2][M(M-1)/2] = LM(LM+1)/2.           *)
Theorem cauchy_cell_count : forall L M : nat,
  L * (L + 1) * (M * (M + 1)) + L * (L - 1) * (M * (M - 1))
  = 2 * (L * M * (L * M + 1)).
Proof.
  intros. destruct L as [|L']; destruct M as [|M'].
  - nia.
  - nia.
  - nia.
  - replace (S L' - 1) with L' by lia.
    replace (S M' - 1) with M' by lia.
    ring.
Qed.

(* The 45 = 36 + 9 instance, with all three counts COMPUTED from the     *)
(* actual enumerations: the symmetric enum for Psym's per-dim factors,   *)
(* the antisymmetric arrow for Qalt's, the joint enum on the right.      *)
Example cauchy_cells_3_3 :
  length (enum 2 0 3) * length (enum 2 0 3)
  + length (enumA nat (anti_heads 3) anti_step 2 0)
    * length (enumA nat (anti_heads 3) anti_step 2 0)
  = length (enum 2 0 9).
Proof. reflexivity. Qed.
