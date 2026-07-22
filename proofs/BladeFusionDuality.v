(* ===================================================================== *)
(* BladeFusionDuality.v -- the method/object duality is derived, not     *)
(* assumed.                                                              *)
(*                                                                       *)
(* The core primitive is loop-index fusion: a loop is welded to its      *)
(* indexing function, with the kernel and the arrays left abstract       *)
(* (fused_form names the equation; it is Out from BladeLowering).  The   *)
(* two abstract slots give r + 1 candidate partial applications; the     *)
(* detection lemmas (identity needs every array position, commutativity  *)
(* needs the kernel) prune them to exactly two -- bind all arrays        *)
(* awaiting a kernel (method_for), or bind the kernel awaiting arrays    *)
(* (object_for).  detections_jointly_license closes the loop: the two    *)
(* detected properties are exactly the premises that license symmetric   *)
(* iteration, and exactness (BladeCompleteness) is why nothing less      *)
(* suffices.  fusion_duality packages the result.                        *)
(*                                                                       *)
(* Imports BladeDMWF, BladeCurrying, BladeCurryingGeneral,               *)
(* BladeLowering.  Coq 8.18, stdlib only.                                *)
(* ===================================================================== *)

From Blade Require Import BladeDMWF BladeCurrying BladeCurryingGeneral BladeLowering.
Require Import List Arith Lia.
Import ListNotations.

(* The fusion equation, as an addressable fact: the fused primitive IS  *)
(* the loop-index weld with kernel and arrays abstract.                  *)
Lemma fused_form :
  forall (T U : Type) (f : (nat -> T) -> U) (Ix : Type)
         (B : nat -> nat) (D : nat -> Ix -> T) (ix : nat -> Ix),
    Out T U f Ix B D ix = f (fun p => D (B p) (ix p)).
Proof. reflexivity. Qed.

(* ---------------- the bridge lemmas ---------------- *)
(* These connect the currying layer's detected properties to the        *)
(* lowering layer's license premises -- the two layers meet here.       *)

Lemma swap_into_range : into_range 2 swap.
Proof. intros p Hp. destruct p as [|[|p]]; simpl; lia. Qed.

(* What method_for detects (full identity) IS the stabilizer premise:   *)
(* if every position holds the same array, every in-range permutation   *)
(* stabilizes the binding.                                              *)
Lemma all_same_stabilizes :
  forall r (B : nat -> nat) (s : nat -> nat),
    all_same r B -> into_range r s -> stabilizes r B s.
Proof.
  intros r B s HA Hin p Hp.
  apply HA; [apply Hin; exact Hp | exact Hp].
Qed.

(* ---------------- the closed loop ---------------- *)
(* The two detected properties JOINTLY instantiate the H-and-Stab        *)
(* license on the fused primitive: commutativity supplies H (the swap    *)
(* invariance of the lifted kernel), identity supplies Stab.  This is    *)
(* the reason the duality's two curryings are the meaningful ones: each  *)
(* binds exactly the information one license premise needs.              *)
Theorem detections_jointly_license :
  forall (T U : Type) (g : T -> T -> U),
    (forall x y, g x y = g y x) ->        (* object_for's detection: H  *)
  forall (B : nat -> nat),
    all_same 2 B ->                       (* method_for's detection: Stab *)
  forall (D : nat -> nat -> T) (ix : nat -> nat),
    Out T U (f2 T U g) nat B D (fun p => ix (swap p))
    = Out T U (f2 T U g) nat B D ix.
Proof.
  intros T U g Hg B HB D ix.
  exact (output_symmetry_soundness 2 T U (f2 T U g) (f2_ext T U g) nat B D
           swap (f2_swap_invariant T U g Hg)
           (all_same_stabilizes 2 B swap HB swap_into_range) ix).
Qed.

(* ---------------- the duality itself ---------------- *)
(* FUSION => DUALITY: over the fused primitive's non-index slots, the    *)
(* only proper, wasteless, detection-bearing curryings are               *)
(*   all positions, no kernel   -- method_for                            *)
(*   kernel only                -- object_for.                           *)
(* (Restates two_maximal_curryings at the identity/commutativity         *)
(* instance under its fusion-derived reading; the r + 1 - 2 = r - 1      *)
(* discarded lattice elements are pruned by 9.23/9.24 wastefulness.)     *)
Corollary fusion_duality :
  forall r, 2 <= r ->
  forall S : Spec,
    proper r S ->
    (detects r S (ArrQ (all_same r)) \/ detects r S (KerQ commutative)) ->
    gwasteless r (all_same r) commutative S ->
    (hasK S = true /\ (forall p, p < r -> hasPos S p = false))   (* object_for *)
    \/ (hasK S = false /\ allPos r S).                           (* method_for *)
Proof. exact two_maximal_identity_comm. Qed.

