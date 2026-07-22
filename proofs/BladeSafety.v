(* ===================================================================== *)
(* BladeSafety.v -- bounds safety with a failure model, verified offset  *)
(* arithmetic, and fusion correctness with a real intermediate buffer.   *)
(* Written in response to an external audit.                             *)
(*                                                                       *)
(* A bounds-safety claim has content only if the semantics contains a    *)
(* failure mode the typing discipline provably avoids (the old           *)
(* indexing_total in BladeCore proved only that a total function is      *)
(* total).  Here the store is the materialized buffer, lookups go        *)
(* through nth_error (which CAN return None), and the address is the     *)
(* compiler's triangular offset:                                         *)
(*   typed_access_safe   every in-bounds ordered pair's offset lookup    *)
(*                       returns exactly its cell -- never None          *)
(*   offset_in_range / offset_injective                                  *)
(*   roff_closed         the running block sum equals the closed-form    *)
(*                       polynomial the compiler emits                   *)
(*   bidx_access_safe    with sigma-typed indices the safety premises    *)
(*                       come from typability alone: no runtime check,   *)
(*                       and an out-of-bounds index cannot be built      *)
(* Scope: rank 2; the general-rank offset is a nested binomial sum       *)
(* (not done); surface-language progress/preservation is future work.    *)
(*                                                                       *)
(* fusion_eliminates_buffer states compose-apply (12.1) between          *)
(* genuinely different computations: materialize stage one into a        *)
(* buffer and index it through nth_error, versus fuse the kernels and    *)
(* never build the buffer.  Loop-fusion soundness with a real store,     *)
(* not a restatement.                                                    *)
(*                                                                       *)
(* Imports BladeDMWF, BladeCompute.  Coq 8.18, stdlib only.              *)
(* ===================================================================== *)

From Blade Require Import BladeDMWF BladeCompute.
Require Import List Arith Lia.
Import ListNotations.

(* ---------------- plumbing ---------------- *)

Lemma nth_error_seq' : forall len a k,
  k < len -> nth_error (seq a len) k = Some (a + k).
Proof.
  induction len as [|len IH]; intros a k Hk; [lia |].
  destruct k as [|k']; simpl.
  - f_equal. lia.
  - rewrite IH by lia. f_equal. lia.
Qed.

Lemma enum1_length : forall l u, length (enum 1 l u) = u - l.
Proof.
  intros. rewrite enum1_singletons, map_length, seq_length. reflexivity.
Qed.

(* ---------------- the triangular offset ---------------- *)

(* running block sum: roff u start d = sum over the d blocks starting    *)
(* at row start of their lengths (u - row)                               *)
Fixpoint roff (u start d : nat) : nat :=
  match d with
  | 0 => 0
  | S d' => (u - start) + roff u (S start) d'
  end.

(* the closed form the compiler emits, x2 to stay division-free *)
Lemma roff_closed : forall d s u,
  s + d <= u -> 2 * roff u s d = d * (2 * (u - s) - d + 1).
Proof.
  induction d as [|d' IH]; intros s u H; cbn [roff].
  - lia.
  - specialize (IH (S s) u ltac:(lia)).
    assert (E1 : u - S s = (u - s) - 1) by lia.
    rewrite E1 in IH.
    assert (E2 : 1 <= u - s) by lia.
    nia.
Qed.

(* ---------------- the safety theorem ---------------- *)

Lemma triangle_gen : forall d cnt start j u,
  d < cnt -> start + d <= j -> j < u ->
  nth_error (flat_map (fun k => map (cons k) (enum 1 k u)) (seq start cnt))
            (roff u start d + (j - (start + d)))
  = Some [start + d; j].
Proof.
  induction d as [|d' IH]; intros cnt start j u Hd Hle Hu.
  - destruct cnt as [|cnt']; [lia |].
    cbn [seq flat_map roff].
    replace (start + 0) with start by lia.
    replace (0 + (j - start)) with (j - start) by lia.
    rewrite nth_error_app1.
    2: { rewrite map_length, enum1_length. lia. }
    rewrite enum1_singletons, map_map.
    assert (Hs : nth_error (seq start (u - start)) (j - start) = Some j).
    { rewrite nth_error_seq' by lia. f_equal. lia. }
    erewrite map_nth_error; [reflexivity | exact Hs].
  - destruct cnt as [|cnt']; [lia |].
    cbn [seq flat_map roff].
    rewrite nth_error_app2.
    2: { rewrite map_length, enum1_length. lia. }
    rewrite map_length, enum1_length.
    replace (u - start + roff u (S start) d' + (j - (start + S d'))
             - (u - start))
      with (roff u (S start) d' + (j - (S start + d'))) by lia.
    replace (start + S d') with (S start + d') by lia.
    apply IH; lia.
Qed.

(* Totality AND correctness of the compiler's triangular addressing:    *)
(* for every canonical index pair, the offset lookup into the            *)
(* materialized buffer returns exactly the right cell -- never None.     *)
Theorem typed_access_safe : forall u i j,
  i <= j -> j < u ->
  nth_error (enum 2 0 u) (roff u 0 i + (j - i)) = Some [i; j].
Proof.
  intros u i j Hij Hu.
  assert (G := triangle_gen i u 0 j u ltac:(lia) ltac:(lia) ltac:(lia)).
  replace (0 + i) with i in G by lia.
  change (enum 2 0 u)
    with (flat_map (fun k => map (cons k) (enum 1 k u)) (seq 0 (u - 0))).
  rewrite Nat.sub_0_r.
  exact G.
Qed.

Corollary offset_in_range : forall u i j,
  i <= j -> j < u ->
  roff u 0 i + (j - i) < length (enum 2 0 u).
Proof.
  intros. apply nth_error_Some.
  rewrite typed_access_safe by assumption. discriminate.
Qed.

Corollary offset_injective : forall u i j i' j',
  i <= j -> j < u -> i' <= j' -> j' < u ->
  roff u 0 i + (j - i) = roff u 0 i' + (j' - i') ->
  i = i' /\ j = j'.
Proof.
  intros u i j i' j' H1 H2 H3 H4 E.
  assert (A := typed_access_safe u i j H1 H2).
  assert (B := typed_access_safe u i' j' H3 H4).
  rewrite E, B in A.
  injection A as E1 E2. subst. split; reflexivity.
Qed.

(* ---------------- the type-discipline packaging ---------------- *)

(* Idx<n> and the dependent residual as sigma types: the bound proofs    *)
(* live in the INDEX VALUES.                                             *)
Definition BIdx (b : nat) : Type := { v : nat | v < b }.
Definition RIdx (l u : nat) : Type := { v : nat | l <= v /\ v < u }.

(* The theorem indexing_total should have been: given ANY inhabitants    *)
(* of the typed indices, the buffer lookup succeeds and returns the      *)
(* right cell.  The safety premises are discharged by TYPABILITY alone   *)
(* (proj2_sig); no runtime check appears anywhere, and an out-of-bounds  *)
(* residual index cannot be constructed.                                 *)
Theorem bidx_access_safe :
  forall (u : nat) (i : BIdx u) (j : RIdx (proj1_sig i) u),
    nth_error (enum 2 0 u)
              (roff u 0 (proj1_sig i) + (proj1_sig j - proj1_sig i))
    = Some [proj1_sig i; proj1_sig j].
Proof.
  intros u [i Hi] [j [Hj1 Hj2]]. simpl.
  apply typed_access_safe; assumption.
Qed.

(* ---------------- buffer-elimination fusion correctness ------------- *)

(* Theorem 12.1 with a REAL store between the stages: the T/S route      *)
(* materializes stage 1 into a buffer and loops over positions reading   *)
(* through nth_error (a lookup that can fail); the S/T route fuses the   *)
(* kernels into one pass and never builds the buffer.  They agree.       *)
Theorem fusion_eliminates_buffer :
  forall (U W : Type) (dflt : U) (p : Arr U) (g : U -> W),
    map (fun k => match nth_error (veval p) k with
                  | Some v => g v
                  | None => g dflt
                  end)
        (seq 0 (length (veval p)))
    = veval (pipe p g).
Proof.
  intros. rewrite compose_apply_duality.
  set (V := veval p).
  transitivity (map (fun k => g (nth k V dflt)) (seq 0 (length V))).
  - apply map_ext_in. intros k Hk. apply in_seq in Hk.
    rewrite (nth_error_nth' V dflt) by lia. reflexivity.
  - transitivity (map g (map (fun k => nth k V dflt) (seq 0 (length V)))).
    + rewrite map_map. reflexivity.
    + rewrite nth_seq_id. reflexivity.
Qed.
