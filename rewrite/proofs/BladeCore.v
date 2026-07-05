(* ===================================================================== *)
(* BladeCore.v -- a machine-checked kernel of Blade's symmetry theorems  *)
(*                                                                       *)
(* Scope: the six results identified in the proof-architecture review,   *)
(* instantiated at the smallest rank/dimension where they have content   *)
(* (r = 2, d = 2), with generalization notes.  Checked with Coq 8.18.    *)
(*                                                                       *)
(* Sections:                                                             *)
(*   1. GroupLaw          -- diagonal S_r is the licensed symmetry       *)
(*   2. ProductCounterex  -- per-dim product symmetry FAILS (refutation  *)
(*                           of formalism section 10.9 step 3)           *)
(*   3. CountingLemma     -- product storage < joint distinct values:    *)
(*                           lossless product layout is impossible       *)
(*   4. Reynolds          -- per-dim Reynolds symmetrization genuinely   *)
(*                           has product symmetry; canonical access is   *)
(*                           lossless                                    *)
(*   5. LeftJustified     -- left-justified coords are a bijection to    *)
(*                           storage coords (r = 2 and r = 3, showing    *)
(*                           the cumulative-bound structure)             *)
(*   6. Canonicalization  -- fold+left-justify access is exact on        *)
(*                           symmetric tensors                           *)
(*   7. BladeCoreTypes    -- bounds safety by construction: dependent    *)
(*                           residual index types make out-of-bounds     *)
(*                           unrepresentable (soundness skeleton)        *)
(*   8. Versioning        -- specialization count explodes under         *)
(*                           composition (trichotomy support lemma)      *)
(* ===================================================================== *)

Require Import Arith Lia Psatz.

(* ===================================================================== *)
(* 1. GROUP LAW (soundness half).                                        *)
(* For one identity group -- method_for(A, A) with comm -- over a d-dim  *)
(* array, the DIAGONAL swap (exchanging whole argument slots, dragging   *)
(* all of each slot's indices together) is a symmetry of the output,     *)
(* for ANY commutative kernel.  This is Theorem 9.11 instantiated with   *)
(* composite indices; it licenses the joint simplex and an r! quotient.  *)
(* ===================================================================== *)

Section GroupLaw.
  Variable T U : Type.
  Variable f : T -> T -> U.
  Hypothesis f_comm : forall x y, f x y = f y x.
  Variable A : nat -> nat -> T.   (* one d=2 array; T abstracts the fiber *)

  Definition Out (i1 i2 j1 j2 : nat) : U := f (A i1 j1) (A i2 j2).

  Theorem diagonal_swap_is_symmetry :
    forall i1 i2 j1 j2, Out i2 i1 j2 j1 = Out i1 i2 j1 j2.
  Proof. intros; unfold Out; apply f_comm. Qed.
End GroupLaw.

(* ===================================================================== *)
(* 2. GROUP LAW (impossibility half), by counterexample.                 *)
(* The per-dimension swap (moving lat indices without lon indices) is    *)
(* NOT a symmetry, even for a commutative -- indeed additively           *)
(* separable -- kernel.  This is the prodsym-fiber kernel shape          *)
(* g(a) + g(b), and it refutes independent per-dim SymIdx lowering for   *)
(* a single identity group (formalism 10.9 step 3, and the compiler's    *)
(* rawAxisGroups clause (B) under the value-equivalence reading).        *)
(* ===================================================================== *)

Module ProductCounterexample.
  Definition A (i j : nat) : nat :=
    match i, j with
    | 0, 0 => 1
    | 0, _ => 10
    | _, 0 => 100
    | _, _ => 1000
    end.

  (* kernel: plus is commutative AND separable; Out = g(A i1 j1)+g(A i2 j2)
     with g = id.  The weakest possible adversary. *)
  Definition Out (i1 i2 j1 j2 : nat) : nat := A i1 j1 + A i2 j2.

  (* lat-only swap of logical index (0,1,0,1) is (1,0,0,1): values differ *)
  Theorem per_dim_swap_not_symmetry : Out 1 0 0 1 <> Out 0 1 0 1.
  Proof. compute. lia. Qed.   (* 110 <> 1001 *)

  (* Consequence: canonicalizing each axis group independently on read
     returns Out 0 1 0 1 where the true value is Out 1 0 0 1.  The
     product-simplex cell aliases two distinct comoments. *)
End ProductCounterexample.

(* ===================================================================== *)
(* 3. COUNTING LEMMA (information-theoretic impossibility).              *)
(* Product-simplex storage has strictly fewer cells than the number of   *)
(* distinct values under the TRUE (diagonal) symmetry, so no lossless    *)
(* product layout exists -- independent of any indexing scheme.          *)
(*                                                                       *)
(* r = 2:  cells(product) = T(n1) * T(n2)   where T(n) = n(n+1)/2        *)
(*         values(joint)  = T(n1 * n2)                                   *)
(* Stated division-free: both sides multiplied by 4.                     *)
(*   4 * T(n1) * T(n2)  =  n1(n1+1) * n2(n2+1)                           *)
(*   4 * T(n1*n2)       =  2 * (n1 n2)(n1 n2 + 1)                        *)
(* ===================================================================== *)

Theorem counting_lemma_r2 :
  forall n1 n2, 2 <= n1 -> 2 <= n2 ->
  (n1 * (n1 + 1)) * (n2 * (n2 + 1)) < 2 * ((n1 * n2) * (n1 * n2 + 1)).
Proof.
  intros n1 n2 H1 H2.
  (* factor out the common n1*n2 > 0; the residual inequality
     (n1+1)(n2+1) < 2(n1 n2 + 1) is equivalent to (n1-1)(n2-1) > 0 *)
  assert (Hkey : (n1 + 1) * (n2 + 1) < 2 * (n1 * n2 + 1)) by nia.
  replace ((n1 * (n1 + 1)) * (n2 * (n2 + 1)))
    with ((n1 * n2) * ((n1 + 1) * (n2 + 1))) by ring.
  replace (2 * ((n1 * n2) * (n1 * n2 + 1)))
    with ((n1 * n2) * (2 * (n1 * n2 + 1))) by ring.
  apply Nat.mul_lt_mono_pos_l; [nia | exact Hkey].
Qed.

(* Sanity instance matching the Python demo (L = M = 3): 36 < 45. *)
Example counting_3_3 : (3*(3+1)) * (3*(3+1)) < 2 * ((3*3) * (3*3+1)).
Proof. compute. lia. Qed.   (* 144 < 180, i.e. 4*36 < 4*45 *)

(* ===================================================================== *)
(* 4. REYNOLDS SOUNDNESS (r = 2, d = 2).                                 *)
(* The per-dim Reynolds symmetrization                                   *)
(*   R[i1,i2,j1,j2] = f(A i1 j1, A i2 j2) (+) f(A i1 j2, A i2 j1)        *)
(* (the sum over the lon-swap coset; with f commutative this is the      *)
(* full S2 x S2 orbit modulo the diagonal) genuinely has INDEPENDENT     *)
(* per-dimension symmetry.  Hence product-simplex storage is exactly     *)
(* right for the Reynolds object: canonical access is lossless.          *)
(* ===================================================================== *)

Section Reynolds.
  Variable T U : Type.
  Variable add : U -> U -> U.
  Hypothesis add_comm : forall x y, add x y = add y x.
  Variable f : T -> T -> U.
  Hypothesis f_comm : forall x y, f x y = f y x.
  Variable A : nat -> nat -> T.

  Definition R (i1 i2 j1 j2 : nat) : U :=
    add (f (A i1 j1) (A i2 j2)) (f (A i1 j2) (A i2 j1)).

  (* Generator 1: lat-only swap IS a symmetry of R. *)
  Theorem reynolds_lat_swap :
    forall i1 i2 j1 j2, R i2 i1 j1 j2 = R i1 i2 j1 j2.
  Proof.
    intros. unfold R.
    rewrite (f_comm (A i2 j1) (A i1 j2)).
    rewrite (f_comm (A i2 j2) (A i1 j1)).
    apply add_comm.
  Qed.

  (* Generator 2: lon-only swap IS a symmetry of R. *)
  Theorem reynolds_lon_swap :
    forall i1 i2 j1 j2, R i1 i2 j2 j1 = R i1 i2 j1 j2.
  Proof. intros. unfold R. apply add_comm. Qed.

  (* The generators give the whole of S2 x S2 (here: the remaining
     nontrivial element, the diagonal, by composition). *)
  Corollary reynolds_full_product_symmetry :
    forall i1 i2 j1 j2, R i2 i1 j2 j1 = R i1 i2 j1 j2.
  Proof. intros. rewrite reynolds_lat_swap, reynolds_lon_swap. reflexivity. Qed.

  (* Losslessness: the value at ANY logical index equals the value at the
     per-axis canonical (sorted) index, so the product-simplex cells
     determine the entire Reynolds tensor.  min/max via a decidable sort. *)
  Definition canon2 (a b : nat) : nat * nat :=
    if le_dec a b then (a, b) else (b, a).

  Theorem reynolds_canonical_access :
    forall i1 i2 j1 j2,
      let (ci1, ci2) := canon2 i1 i2 in
      let (cj1, cj2) := canon2 j1 j2 in
      R ci1 ci2 cj1 cj2 = R i1 i2 j1 j2.
  Proof.
    intros. unfold canon2.
    destruct (le_dec i1 i2); destruct (le_dec j1 j2);
      [ reflexivity
      | apply reynolds_lon_swap
      | apply reynolds_lat_swap
      | apply reynolds_full_product_symmetry ].
  Qed.
End Reynolds.

(* Accounting note (informal, arithmetic checked in section 3): the
   Reynolds object costs |coset| = r!^(d-1) kernel terms per cell over
   n^(rd)/r!^d cells = n^(rd)/r! kernel evaluations total.  FLOP factor
   r!, storage factor r!^d.  "(r!)^d is a storage theorem; r! is the
   FLOP theorem; Reynolds bridges them." *)

(* ===================================================================== *)
(* 5. LEFT-JUSTIFIED COORDINATES ARE A STORAGE BIJECTION.                *)
(* Formalism 14.2: iteration coordinates = storage coordinates.  We      *)
(* prove the transform (i, j) |-> (i, j - i) is a bijection between the  *)
(* canonical simplex { i <= j < n } and the left-justified storage       *)
(* domain { a + b < n } (the domain enumerated by the emitted loops      *)
(* for a in [0,n): for b in [0, n-a)), with explicit inverse.  The r = 3 *)
(* version exhibits the cumulative-bound structure of Theorem 2.5.       *)
(* ===================================================================== *)

Section LeftJustified.
  Variable n : nat.

  Definition lj2 (i j : nat) : nat * nat := (i, j - i).
  Definition lj2_inv (a b : nat) : nat * nat := (a, a + b).

  Theorem lj2_forward :
    forall i j, i <= j -> j < n ->
      let (a, b) := lj2 i j in a + b < n /\ lj2_inv a b = (i, j).
  Proof. intros. unfold lj2, lj2_inv. simpl. split; [lia | f_equal; lia]. Qed.

  Theorem lj2_backward :
    forall a b, a + b < n ->
      let (i, j) := lj2_inv a b in i <= j /\ j < n /\ lj2 i j = (a, b).
  Proof. intros. unfold lj2_inv, lj2. simpl. repeat split; [lia | lia | f_equal; lia]. Qed.

  (* r = 3: bounds b < n - a and c < n - a - b (cumulative subtraction). *)
  Definition lj3 (i j k : nat) : nat * nat * nat := (i, j - i, k - j).
  Definition lj3_inv (a b c : nat) : nat * nat * nat := (a, a + b, a + b + c).

  Theorem lj3_forward :
    forall i j k, i <= j -> j <= k -> k < n ->
      let '(a, b, c) := lj3 i j k in
      a + b + c < n /\ lj3_inv a b c = (i, j, k).
  Proof.
    intros. unfold lj3, lj3_inv. simpl.
    split; [lia | f_equal; [f_equal|]; lia].
  Qed.

  Theorem lj3_backward :
    forall a b c, a + b + c < n ->
      let '(i, j, k) := lj3_inv a b c in
      i <= j /\ j <= k /\ k < n /\ lj3 i j k = (a, b, c).
  Proof.
    intros. unfold lj3_inv, lj3. simpl.
    repeat split; try lia. f_equal; [f_equal|]; lia.
  Qed.
End LeftJustified.

(* ===================================================================== *)
(* 6. CANONICALIZATION CORRECTNESS (fold phase + access).                *)
(* For a genuinely symmetric tensor S, the two-phase access transform    *)
(* (sort within the symmetry group, then left-justify) reads back the    *)
(* exact value at EVERY logical index -- including non-canonical ones.   *)
(* This is the r = 2 instance of formalism 14.3, and it is the theorem   *)
(* that FAILS for the non-Reynolds product layout of section 2.          *)
(* ===================================================================== *)

Section Canonicalization.
  Variable T : Type.
  Variable S : nat -> nat -> T.
  Hypothesis S_sym : forall i j, S i j = S j i.

  (* storage cell (a, b) holds the canonical element S a (a + b) *)
  Definition store (a b : nat) : T := S a (a + b).

  Definition canon (i j : nat) : nat * nat :=
    if le_dec i j then (i, j) else (j, i).

  Theorem access_exact :
    forall i j,
      let (c1, c2) := canon i j in
      store c1 (c2 - c1) = S i j.
  Proof.
    intros. unfold canon, store.
    destruct (le_dec i j).
    - replace (i + (j - i)) with j by lia. reflexivity.
    - replace (j + (i - j)) with i by lia. apply S_sym.
  Qed.

  Theorem canon_identifies_orbit :
    forall i j, canon i j = canon j i.
  Proof.
    intros. unfold canon.
    destruct (le_dec i j); destruct (le_dec j i); try reflexivity; f_equal; lia.
  Qed.
End Canonicalization.

(* ===================================================================== *)
(* 7. BLADE-CORE TYPING SKELETON: bounds safety by construction.         *)
(* Dependent residual index types (BoundedIdx after currying a SymIdx)   *)
(* make out-of-bounds indexing UNREPRESENTABLE: an index value carries   *)
(* its bound proofs, arrays are total functions on such indices, and     *)
(* currying returns the residual-typed inner array.  Progress and        *)
(* preservation for the full surface calculus reduce to elaboration      *)
(* into these intrinsically-typed definitions; the payoff theorem        *)
(* (formalism 4.18.4, "out-of-bounds access is impossible") holds here   *)
(* definitionally, witnessed by totality.                                *)
(* ===================================================================== *)

Section BladeCoreTypes.
  Variable T : Type.

  Record BoundedIdx (l u : nat) : Type := mkBIdx {
    bval : nat;
    b_lo : l <= bval;
    b_hi : bval < u
  }.
  Arguments bval {l u}.

  (* Idx<n> is BoundedIdx 0 n (formalism 4.13). *)
  Definition Idx (n : nat) := BoundedIdx 0 n.

  (* A rank-1 array is a TOTAL function on its index type. *)
  Definition Arr (n : nat) := Idx n -> T.

  (* SymIdx<2, n> array in curried form: the inner index type DEPENDS on
     the outer index value -- the residual BoundedIdx i n of 4.14.1. *)
  Definition SymArr (n : nat) :=
    forall (i : Idx n), BoundedIdx (bval i) n -> T.

  (* Dimensional currying is the identity on this representation, and it
     is residual-typed: the result awaits exactly BoundedIdx i n. *)
  Definition curry_sym {n} (A : SymArr n) (i : Idx n)
    : BoundedIdx (bval i) n -> T := A i.

  (* Bounds safety: every well-typed access yields a value.  Vacuously
     total -- which is the point: the unsafe access is not writable. *)
  Theorem indexing_total :
    forall n (A : SymArr n) (i : Idx n) (j : BoundedIdx (bval i) n),
      exists v : T, A i j = v.
  Proof. intros. exists (A i j). reflexivity. Qed.

  (* The iterator emits only inhabitants: constructing the loop variable
     for the residual level requires exactly the proofs the loop bounds
     provide (b_lo from the group base, b_hi from the extent).  A full
     progress/preservation development would elaborate surface terms
     into this section's types; nothing at this layer can go wrong. *)
End BladeCoreTypes.

(* ===================================================================== *)
(* 8. VERSIONING EXPLOSION (support lemma for the 2.12-2.15 rewrite).    *)
(* Runtime multi-versioning escapes per-iteration branching, but under   *)
(* composition of k nodes with c >= 2 symcom configurations each, the    *)
(* specialization count is c^k >= 2^k: bounded code size forces          *)
(* compile-time symmetry-directed generation.  The arithmetic core:     *)
(* ===================================================================== *)

Theorem versioning_explosion :
  forall c k, 2 <= c -> 2 ^ k <= c ^ k.
Proof. intros. apply Nat.pow_le_mono_l. lia. Qed.

(* ===================================================================== *)
(* Generalization notes (not yet mechanized):                            *)
(*  - GroupLaw for general r, d: diagonal S_r acting on d-tuples; same   *)
(*    one-line proof over vectors of indices (Vector.t nat d).           *)
(*  - CountingLemma general form: prod_j C(n_j + r - 1, r) <             *)
(*    C(prod_j n_j + r - 1, r) for d >= 2, n_j >= 2, r >= 2.  Needs      *)
(*    binomial machinery (Coq.Arith.Binomial or mathcomp).               *)
(*  - Reynolds general form: orbit sum over coset representatives of     *)
(*    the diagonal in S_r^d; symmetry proof is reindexing of a finite    *)
(*    sum (mathcomp bigop is the right vehicle).                         *)
(*  - Left-justified UNIQUENESS (the stronger claim): among              *)
(*    enumeration orders realizable as per-level lower/upper bound       *)
(*    functions of prior indices, only the left-justified one makes the  *)
(*    identity a storage bijection.  Requires formalizing the space of   *)
(*    admissible enumerations first.                                     *)
(*  - Full Blade-core progress/preservation: surface syntax + typing +   *)
(*    elaboration into section 7's intrinsic types.                      *)
(* ===================================================================== *)
