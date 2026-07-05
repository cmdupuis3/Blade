(* ===================================================================== *)
(* BladeLowering.v -- Layer 3a: symmetry LOWERING and RAISING            *)
(* (formalism Theorems 9.9-9.22, corrected per the product-symmetry      *)
(* audit).                                                               *)
(*                                                                       *)
(* The organizing result is Theorem 9.11 in its sound general form:      *)
(* a permutation of argument positions is LICENSED (is a symmetry of     *)
(* the output) when it lies in H (the kernel's invariance group) AND     *)
(* stabilizes the array binding.  Both conditions are individually       *)
(* insufficient, and both insufficiencies are checked here:              *)
(*                                                                       *)
(*   output_symmetry_soundness   H + Stab => licensed (any r, any s)     *)
(*   licensed_* closure          licensed permutations form a monoid     *)
(*                               (for finite groups this generates the   *)
(*                               subgroup: s^k = id supplies inverses)   *)
(*   lower_2_1                   Theorem 9.9 derived AS AN INSTANCE of   *)
(*                               the framework (comm kernel + identical  *)
(*                               arrays => symmetric output)             *)
(*   shared_units_insufficient   Theorem 9.17: distinct arrays over the  *)
(*                               SAME index space, commutative kernel,   *)
(*                               asymmetric output.  H alone is not      *)
(*                               enough; identity generates symmetry.    *)
(*   input_symmetry_not_sufficient  Theorems 9.13/9.14: maximally        *)
(*                               symmetric input DATA, no H => no        *)
(*                               output symmetry.  Input symmetry is     *)
(*                               consumed, not propagated.               *)
(*   raise_1_2                   Theorems 9.16/9.18: a symmetric array's *)
(*                               accessor IS a commutative kernel        *)
(*                               (raising), closing the round trip with  *)
(*                               lower_2_1.                              *)
(*                                                                       *)
(* Self-contained (stdlib only); ordered after BladeCurryingGeneral in   *)
(* the tower.  Coq 8.18.                                                 *)
(* ===================================================================== *)

Require Import Arith Lia.

(* ===================================================================== *)
(* THE GENERAL FRAMEWORK.                                                *)
(* A call site is: a kernel f over position-indexed argument tuples, a   *)
(* binding B from positions to array identities, and data D from         *)
(* identities and indices to values.  Out ix applies the kernel to the   *)
(* values selected by the index assignment ix.  Identity-as-nat makes    *)
(* the stabilizer condition a decidable equation with no functional      *)
(* extensionality anywhere.                                              *)
(* ===================================================================== *)

Section OutputSymmetryFramework.
  Variable r : nat.
  Variables T U : Type.
  Variable f : (nat -> T) -> U.
  (* locality: the kernel reads only positions < r *)
  Hypothesis Hext : forall v v' : nat -> T,
    (forall p, p < r -> v p = v' p) -> f v = f v'.

  (* Index values are OPAQUE to the framework: Ix may be nat, a pair
     of nats (composite d-dim indices), or anything else.  The Group
     Law never inspects an index; it only moves indices between
     positions.  This is what makes the diagonal instance below a
     one-liner. *)
  Variable Ix : Type.
  Variable B : nat -> nat.          (* position -> array identity      *)
  Variable D : nat -> Ix -> T.      (* identity -> index -> value      *)

  Definition Out (ix : nat -> Ix) : U := f (fun p => D (B p) (ix p)).

  (* s is in H: the kernel cannot tell permuted tuples apart *)
  Definition invariant_under (s : nat -> nat) : Prop :=
    forall v : nat -> T, f (fun p => v (s p)) = f v.

  (* s stabilizes the binding: permuted positions hold the SAME array *)
  Definition stabilizes (s : nat -> nat) : Prop :=
    forall p, p < r -> B (s p) = B p.

  Definition into_range (s : nat -> nat) : Prop :=
    forall p, p < r -> s p < r.

  (* ------------------------------------------------------------------ *)
  (* Theorem 9.11, soundness half: H AND Stab license the permutation.  *)
  (* ------------------------------------------------------------------ *)
  Theorem output_symmetry_soundness :
    forall s, invariant_under s -> stabilizes s ->
    forall ix, Out (fun p => ix (s p)) = Out ix.
  Proof.
    intros s Hinv Hstab ix. unfold Out.
    transitivity (f (fun p => D (B (s p)) (ix (s p)))).
    - apply Hext. intros p Hp. rewrite (Hstab p Hp). reflexivity.
    - exact (Hinv (fun q => D (B q) (ix q))).
  Qed.

  (* ------------------------------------------------------------------ *)
  (* The licensed permutations form a monoid.  For a finite group of    *)
  (* permutations of [0, r), monoid closure suffices for group closure: *)
  (* every element has finite order, so s^(k-1) is s's inverse.         *)
  (* ------------------------------------------------------------------ *)
  Lemma invariant_id : invariant_under (fun p => p).
  Proof. intro v. reflexivity. Qed.

  Lemma stabilizes_id : stabilizes (fun p => p).
  Proof. intros p _. reflexivity. Qed.

  Lemma invariant_compose : forall s t,
    invariant_under s -> invariant_under t ->
    invariant_under (fun p => t (s p)).
  Proof.
    intros s t Hs Ht v.
    transitivity (f (fun q => v (t q))).
    - exact (Hs (fun q => v (t q))).
    - exact (Ht v).
  Qed.

  Lemma stabilizes_compose : forall s t,
    stabilizes s -> stabilizes t -> into_range s ->
    stabilizes (fun p => t (s p)).
  Proof.
    intros s t Hs Ht Hdom p Hp.
    rewrite (Ht (s p) (Hdom p Hp)). apply Hs. exact Hp.
  Qed.
  (* ------------------------------------------------------------------ *)
  (* Sign-tracked variant: if the kernel is ANTI-invariant under s      *)
  (* (permuting arguments negates the value -- antisymmetric kernels;   *)
  (* for the Hermitian case, neg := conjugation), the same two-step     *)
  (* proof yields output antisymmetry.                                  *)
  (* ------------------------------------------------------------------ *)
  Variable neg : U -> U.

  Definition antiinvariant_under (s : nat -> nat) : Prop :=
    forall v : nat -> T, f (fun p => v (s p)) = neg (f v).

  Theorem output_antisymmetry_soundness :
    forall s, antiinvariant_under s -> stabilizes s ->
    forall ix, Out (fun p => ix (s p)) = neg (Out ix).
  Proof.
    intros s Hinv Hstab ix. unfold Out.
    transitivity (f (fun p => D (B (s p)) (ix (s p)))).
    - apply Hext. intros p Hp. rewrite (Hstab p Hp). reflexivity.
    - exact (Hinv (fun q => D (B q) (ix q))).
  Qed.
End OutputSymmetryFramework.

(* ===================================================================== *)
(* THEOREM 9.9 (lower-2-1) AS A FRAMEWORK INSTANCE.                      *)
(* Commutative binary kernel + one array bound at both positions =>      *)
(* the swap is invariant (H) and stabilizing (identity), so the output   *)
(* is symmetric.  This re-derives BladeCore's diagonal_swap_is_symmetry  *)
(* from the tower rather than directly, demonstrating subsumption.      *)
(* ===================================================================== *)

Definition swap (p : nat) : nat :=
  match p with 0 => 1 | 1 => 0 | _ => p end.

Section LowerTwoOne.
  Variables T U : Type.
  Variable g : T -> T -> U.
  Hypothesis Hg : forall x y, g x y = g y x.
  Variable D : nat -> nat -> T.
  Variable a : nat.                       (* the ONE shared array identity *)

  Definition f2 (v : nat -> T) : U := g (v 0) (v 1).

  Lemma f2_ext : forall v v' : nat -> T,
    (forall p, p < 2 -> v p = v' p) -> f2 v = f2 v'.
  Proof. intros v v' H. unfold f2. f_equal; apply H; lia. Qed.

  Lemma f2_swap_invariant : invariant_under T U f2 swap.
  Proof. intro v. unfold f2. simpl. apply Hg. Qed.

  Lemma swap_stab_const : stabilizes 2 (fun _ => a) swap.
  Proof. intros p _. reflexivity. Qed.

  Definition Out2 (i j : nat) : U := g (D a i) (D a j).

  Theorem lower_2_1 : forall i j, Out2 j i = Out2 i j.
  Proof.
    intros i j.
    exact (output_symmetry_soundness 2 T U f2 f2_ext nat (fun _ => a) D swap
             f2_swap_invariant swap_stab_const
             (fun p => if p =? 0 then i else j)).
  Qed.
End LowerTwoOne.

(* ===================================================================== *)
(* THEOREM 9.17: "shared units alone are insufficient for symmetry."     *)
(* Two DISTINCT arrays (ids 0 and 1) over the same index space, a        *)
(* commutative kernel -- and the output is NOT symmetric.  H holds,      *)
(* Stab fails, license refused: identity generates symmetry; shared      *)
(* index spaces only make the swap well-typed.  (This is the checked     *)
(* form of the cross-array grant correction from the product-symmetry    *)
(* audit.)                                                               *)
(* ===================================================================== *)

Module SharedUnitsInsufficient.
  Definition g (x y : nat) : nat := x * y.

  Lemma g_comm : forall x y, g x y = g y x.
  Proof. intros. apply Nat.mul_comm. Qed.

  (* array 0: the identity series; array 1: the constant-1 series *)
  Definition D (aid i : nat) : nat := if aid =? 0 then i else 1.

  Definition Out2 (i j : nat) : nat := g (D 0 i) (D 1 j).

  Theorem shared_units_insufficient : Out2 1 0 <> Out2 0 1.
  Proof. compute. lia. Qed.       (* 1 <> 0 *)
End SharedUnitsInsufficient.

(* ===================================================================== *)
(* THEOREMS 9.13/9.14: input symmetry is CONSUMED, not propagated.       *)
(* The input data is maximally symmetric (E x y = E y x for every        *)
(* fiber), but the kernel carries no H -- and the output has no          *)
(* symmetry.  Output symmetry derives from H-and-Stab alone; symmetry    *)
(* of consumed dimensions contributes nothing.                           *)
(* ===================================================================== *)

Module InputSymmetryConsumed.
  Definition E (x y : nat) : nat := x + y.

  Lemma E_sym : forall x y, E x y = E y x.
  Proof. intros. apply Nat.add_comm. Qed.

  (* non-commutative kernel over two row-slices: reads only the FIRST   *)
  (* argument's fiber.  Out (i, j) = (row i of E) at 0 = i.             *)
  Definition Out2 (i j : nat) : nat := E i 0.

  Theorem input_symmetry_not_sufficient : Out2 1 0 <> Out2 0 1.
  Proof. compute. lia. Qed.       (* 1 <> 0 *)
End InputSymmetryConsumed.

(* ===================================================================== *)
(* THEOREMS 9.16/9.18 (raise-1-2): a symmetric array's accessor IS a     *)
(* commutative kernel.  Symmetric storage grants H to the indexing       *)
(* function -- the raising direction, closing the round trip with        *)
(* lower_2_1: lowering turns H+identity into storage symmetry; raising   *)
(* turns storage symmetry back into H.                                   *)
(* ===================================================================== *)

Section RaiseOneTwo.
  Variable T : Type.
  Variable Sy : nat -> nat -> T.
  Hypothesis HSy : forall i j, Sy i j = Sy j i.

  Definition acc (v : nat -> nat) : T := Sy (v 0) (v 1).

  Theorem raise_1_2 : invariant_under nat T acc swap.
  Proof. intro v. unfold acc. simpl. apply HSy. Qed.
End RaiseOneTwo.

(* ===================================================================== *)
(* THE DIAGONAL GROUP LAW AS A FRAMEWORK INSTANCE.                       *)
(* Composite (multi-dimensional) indices are just Ix := nat * nat: the   *)
(* joint swap over one identity group of rank-2 arrays is the swap       *)
(* instance with pair-valued indices.  This unifies BladeCore's          *)
(* diagonal_swap_is_symmetry into the tower and closes the deferred      *)
(* composite-index item.                                                 *)
(* ===================================================================== *)

Section DiagonalGroupLaw.
  Variables T U : Type.
  Variable g : T -> T -> U.
  Hypothesis Hg : forall x y, g x y = g y x.
  Variable D : nat -> (nat * nat) -> T.
  Variable a : nat.

  Definition OutD (i1 j1 i2 j2 : nat) : U := g (D a (i1, j1)) (D a (i2, j2)).

  Theorem diagonal_group_law :
    forall i1 j1 i2 j2, OutD i2 j2 i1 j1 = OutD i1 j1 i2 j2.
  Proof.
    intros.
    exact (output_symmetry_soundness 2 T U (f2 T U g)
             (f2_ext T U g) (nat * nat) (fun _ => a) D swap
             (f2_swap_invariant T U g Hg) (swap_stab_const a)
             (fun p => if p =? 0 then (i1, j1) else (i2, j2))).
  Qed.
End DiagonalGroupLaw.

(* ===================================================================== *)
(* SIGN-TRACKED LOWERING: antisymmetric kernels over one identity group  *)
(* (Hermitian is the same statement with neg := conjugation).  On the    *)
(* diagonal, OutA i i = neg (OutA i i): with neg := opp over a group,    *)
(* the diagonal vanishes -- stated here structurally, the vanishing      *)
(* itself needs U's group structure.                                     *)
(* ===================================================================== *)

Section AntisymmetricLowering.
  Variables T U : Type.
  Variable neg : U -> U.
  Variable g : T -> T -> U.
  Hypothesis Hg : forall x y, g x y = neg (g y x).
  Variable D : nat -> nat -> T.
  Variable a : nat.

  Lemma f2_swap_antiinvariant :
    antiinvariant_under T U (f2 T U g) neg swap.
  Proof. intro v. unfold f2. simpl. apply Hg. Qed.

  Definition OutA (i j : nat) : U := g (D a i) (D a j).

  Theorem antisym_lower_2_1 :
    forall i j, OutA j i = neg (OutA i j).
  Proof.
    intros i j.
    exact (output_antisymmetry_soundness 2 T U (f2 T U g)
             (f2_ext T U g) nat (fun _ => a) D neg swap
             f2_swap_antiinvariant (swap_stab_const a)
             (fun p => if p =? 0 then i else j)).
  Qed.
End AntisymmetricLowering.

(* ===================================================================== *)
(* Notes:                                                                *)
(*  - Completeness ("EXACTLY H-intersect-Stab"): for s violating Stab,   *)
(*    a distinguishing (f, D) always exists -- shared_units_insufficient *)
(*    is the r = 2 witness; the general construction (D a i := if        *)
(*    a =? B p then i else 1 at a violated position p) is mechanical     *)
(*    and deferred.                                                      *)
(*  - The DIAGONAL instance for multi-dim identity groups (the corrected *)
(*    Group Law) lives concretely in BladeCore.v; encoding composite     *)
(*    indices in this framework (ix : nat -> nat with a pairing scheme)  *)
(*    would unify them and is deferred to the general-arrow layer.       *)
(*  - Antisymmetric lowering (sign-tracked, over Z) and Hermitian        *)
(*    (conjugation) are the same framework with U a group action target; *)
(*    deferred.                                                          *)
(* ===================================================================== *)
