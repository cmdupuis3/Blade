(* ===================================================================== *)
(* BladeLayout.v -- STRIDING PARITY: the layout group B_d, and the        *)
(*                  4-element character ceiling the H/Stab framework      *)
(*                  imposes on it.                                        *)
(*                                                                       *)
(* Setting.  A d-axis array has a LAYOUT: an assignment of its d axes to   *)
(* memory levels (outermost..innermost) together with a per-axis           *)
(* iteration DIRECTION.  The set of layout choices is therefore the        *)
(* hyperoctahedral group B_d = Z_2^d semidirect S_d of signed             *)
(* permutations, order 2^d d!.  A STRIDING PARITY is an element of B_d.    *)
(*                                                                       *)
(* THE HEADLINE, stated honestly.  BladeLowering's H/Stab framework has    *)
(* exactly two grant forms: invariance (Out o g = Out) and                 *)
(* anti-invariance (Out o g = neg (Out)).  This file proves               *)
(*                                                                       *)
(*   (a) the two forms are closed under composition with the sign given    *)
(*       by XOR (graded_invariant_compose, licensed_compose) -- so the     *)
(*       grade map on the licensed set is a homomorphism to Z_2, i.e. a    *)
(*       ONE-DIMENSIONAL character, and the grade is unique whenever neg   *)
(*       is fixed-point-free anywhere on the kernel range                 *)
(*       (sign_determined);                                               *)
(*                                                                       *)
(*   (b) there are EXACTLY 4 such characters of B_d for d = 2 and d = 3,   *)
(*       computed and classified: trivial, permutation sign, flip parity,  *)
(*       and their xor (b2/b3_character_classified), while the number of   *)
(*       IRREDUCIBLE representations of B_d is the bipartition count       *)
(*       2, 5, 10, 20, 36, ... which grows without bound                   *)
(*       (bipartition_counts, irrep_count_exceeds_character_count).        *)
(*                                                                       *)
(* What is NOT claimed: nothing here says a richer licensing framework is  *)
(* impossible.  The collapse from an unmanageable set to a manageable one  *)
(* is a statement about the GRANT VOCABULARY of H/Stab -- its two          *)
(* constructors index a 4-element set of characters, not the growing set   *)
(* of irreps.  And nothing here is about cache HARDWARE: the file is       *)
(* about the group and the licensing structure only.                       *)
(*                                                                       *)
(* Sections:                                                             *)
(*   1. SignedPermutations -- B_d as data (permutation list paired with a  *)
(*      flip vector), composition, closure, inverses, orders |B_2| = 8,    *)
(*      |B_3| = 48, |B_4| = 384, all by vm_compute                         *)
(*   2a. GradedLicense     -- the Z_2-graded H/Stab license: XOR           *)
(*      composition, anti o anti = invariant, grade uniqueness, and the    *)
(*      two-grant-forms exhaustion                                        *)
(*   2b. Characters        -- the four characters exhibited and checked;   *)
(*      conjugacy forces equal values on adjacent transpositions and on    *)
(*      flips; generation by {tau, phi} (d = 2) and by the Coxeter set     *)
(*      (d = 3) closes completeness at exactly 4; bipartition contrast     *)
(*   3. ReversalLicense    -- direction parity is schedule-only iff the    *)
(*      reduce monoid is commutative AND associative; both hypotheses      *)
(*      refuted separately by concrete folds                              *)
(*   4. AxisExchange       -- transposing rank 2 preserves / negates /     *)
(*      breaks values according to declared symmetry; the CONTIGUITY       *)
(*      CONFLICT of C[i,j] = sum_k A[i,k] A[k,j] and its resolution by     *)
(*      declared symmetry, with decidable orbit computations at d = 2, 3   *)
(*   5. Propagation        -- characters compose by XOR through a          *)
(*      pipeline (character_composes), instantiated on the transpose       *)
(*      bridge (A B)^T = eps_A eps_B (B A) in all four sign cases, both    *)
(*      at general n and pinned at 3x3 over Z                             *)
(*                                                                       *)
(* Self-contained (stdlib only), like BladeCore, BladeLowering and         *)
(* BladeWreath; no Blade file is required, so build order is               *)
(* unconstrained.  The H/Stab vocabulary of section 2a is BladeLowering's  *)
(* OutputSymmetryFramework restated locally (Out, stabilizes, into_range,  *)
(* neg) rather than imported, exactly as BladeWreath restates permutes.    *)
(* Checked with Rocq 9.0.1 (the tower targets Coq 8.18; the only           *)
(* diagnostics are the deprecated-missing-stdlib warnings every file in    *)
(* the tower emits).                                                      *)
(* ===================================================================== *)

Require Import Arith Lia List Bool Permutation ZArith.
Import ListNotations.
Open Scope nat_scope.

(* ===================================================================== *)
(* 1. THE LAYOUT GROUP B_d, AS DATA.                                      *)
(*                                                                       *)
(* A signed permutation is a pair (s, e): s is a d-element list of axis    *)
(* indices with nth j s = sigma j (the axis placed at memory level j is    *)
(* determined by sigma), and e is a d-element list of booleans with        *)
(* nth j e = true meaning axis j is traversed in reverse.  The group law   *)
(* is the signed permutation matrix product: M(sigma,eps) has entry        *)
(* eps_j at (sigma j, j), and                                             *)
(*                                                                       *)
(*    (sigma, eps) . (tau, delta) = (sigma o tau, j |-> eps_(tau j) xor delta_j) *)
(*                                                                       *)
(* which is bcomp below.  The BladePointGroup discipline applies: the      *)
(* tables are DATA, their group properties are theorems.                   *)
(* ===================================================================== *)

Definition bsp : Type := (list nat * list bool)%type.

Definition bcomp (g h : bsp) : bsp :=
  (map (fun k => nth k (fst g) 0) (fst h),
   map (fun kf => xorb (nth (fst kf) (snd g) false) (snd kf))
       (combine (fst h) (snd h))).

Definition bid (d : nat) : bsp := (seq 0 d, repeat false d).

Definition bsp_dec (g h : bsp) : {g = h} + {g <> h}.
Proof.
  destruct g as [p e]; destruct h as [q f].
  destruct (list_eq_dec Nat.eq_dec p q) as [Ep | Np];
  destruct (list_eq_dec bool_dec e f) as [Ee | Ne].
  - left; subst; reflexivity.
  - right; intro H; inversion H; contradiction.
  - right; intro H; inversion H; contradiction.
  - right; intro H; inversion H; contradiction.
Defined.

Definition bsp_eqb (g h : bsp) : bool :=
  if bsp_dec g h then true else false.

Lemma bsp_eqb_eq : forall g h, bsp_eqb g h = true -> g = h.
Proof.
  intros g h H. unfold bsp_eqb in H.
  destruct (bsp_dec g h) as [E | N]; [exact E | discriminate].
Qed.

Lemma in_of_existsb : forall (g : bsp) (l : list bsp),
  existsb (bsp_eqb g) l = true -> In g l.
Proof.
  intros g l H. apply existsb_exists in H as (x & Hx & Hb).
  apply bsp_eqb_eq in Hb. rewrite Hb. exact Hx.
Qed.

(* --- the enumeration -------------------------------------------------- *)

Fixpoint interleave (x : nat) (l : list nat) : list (list nat) :=
  match l with
  | [] => [[x]]
  | y :: l' => (x :: y :: l') :: map (fun z => y :: z) (interleave x l')
  end.

Fixpoint permsOf (l : list nat) : list (list nat) :=
  match l with
  | [] => [[]]
  | x :: l' => flat_map (interleave x) (permsOf l')
  end.

Fixpoint bvecs (d : nat) : list (list bool) :=
  match d with
  | 0 => [[]]
  | S k => flat_map (fun v => [false :: v; true :: v]) (bvecs k)
  end.

Definition Bd (d : nat) : list bsp :=
  flat_map (fun s => map (fun v => (s, v)) (bvecs d)) (permsOf (seq 0 d)).

(* --- the orders -------------------------------------------------------- *)

Example bd_orders : map (fun d => length (Bd d)) [0; 1; 2; 3; 4]
                  = [1; 2; 8; 48; 384].
Proof. vm_compute. reflexivity. Qed.

Example bd_order_is_two_pow_times_factorial :
  map (fun d => length (Bd d)) [0; 1; 2; 3; 4]
  = map (fun d => 2 ^ d * fact d) [0; 1; 2; 3; 4].
Proof. vm_compute. reflexivity. Qed.

Theorem bd2_order : length (Bd 2) = 8.
Proof. vm_compute. reflexivity. Qed.

Theorem bd3_order : length (Bd 3) = 48.
Proof. vm_compute. reflexivity. Qed.

Theorem bd4_order : length (Bd 4) = 384.
Proof. vm_compute. reflexivity. Qed.

(* The enumerations are duplicate-free, so the lengths above are group     *)
(* ORDERS and not merely list lengths.                                    *)
Theorem bd2_nodup : length (nodup bsp_dec (Bd 2)) = 8.
Proof. vm_compute. reflexivity. Qed.

Theorem bd3_nodup : length (nodup bsp_dec (Bd 3)) = 48.
Proof. vm_compute. reflexivity. Qed.

Theorem bd4_nodup : length (nodup bsp_dec (Bd 4)) = 384.
Proof. vm_compute. reflexivity. Qed.

(* --- well-formedness: every listed element is a signed permutation ----- *)

Definition is_bsp (d : nat) (g : bsp) : bool :=
  Nat.eqb (length (fst g)) d && Nat.eqb (length (snd g)) d &&
  forallb (fun k => existsb (Nat.eqb k) (fst g)) (seq 0 d).

Theorem bd_elements_are_signed_permutations :
  forallb (is_bsp 2) (Bd 2) = true /\
  forallb (is_bsp 3) (Bd 3) = true /\
  forallb (is_bsp 4) (Bd 4) = true.
Proof. repeat split; vm_compute; reflexivity. Qed.

(* --- group laws, decidably --------------------------------------------- *)

Definition bd_closed (d : nat) : bool :=
  forallb (fun g => forallb (fun h => existsb (bsp_eqb (bcomp g h)) (Bd d))
                            (Bd d)) (Bd d).

Definition bd_has_inverses (d : nat) : bool :=
  forallb (fun g => existsb (fun h => bsp_eqb (bcomp g h) (bid d)) (Bd d))
          (Bd d).

Definition bd_identity (d : nat) : bool :=
  existsb (bsp_eqb (bid d)) (Bd d) &&
  forallb (fun g => bsp_eqb (bcomp (bid d) g) g &&
                    bsp_eqb (bcomp g (bid d)) g) (Bd d).

Theorem b2_is_a_group :
  bd_closed 2 = true /\ bd_has_inverses 2 = true /\ bd_identity 2 = true.
Proof. repeat split; vm_compute; reflexivity. Qed.

Theorem b3_is_a_group :
  bd_closed 3 = true /\ bd_has_inverses 3 = true /\ bd_identity 3 = true.
Proof. repeat split; vm_compute; reflexivity. Qed.

(* Composition is associative on the enumerated group (d <= 3): the        *)
(* semidirect product law is checked, not assumed.                         *)
Definition bd_assoc (d : nat) : bool :=
  forallb (fun g => forallb (fun h => forallb (fun k =>
    bsp_eqb (bcomp (bcomp g h) k) (bcomp g (bcomp h k))) (Bd d)) (Bd d))
    (Bd d).

Theorem b2_assoc : bd_assoc 2 = true.
Proof. vm_compute. reflexivity. Qed.

Theorem b3_assoc : bd_assoc 3 = true.
Proof. vm_compute. reflexivity. Qed.

(* At d = 4 the pairwise membership sweep is 384 x 384 searches through    *)
(* 384 elements; the cheaper equivalent kept here is that every product   *)
(* of two elements is again a signed permutation of 4 axes.                *)
Theorem b4_products_are_signed_permutations :
  forallb (fun g => forallb (fun h => is_bsp 4 (bcomp g h)) (Bd 4)) (Bd 4)
  = true.
Proof. vm_compute. reflexivity. Qed.

(* --- the Prop-level consequences used by section 2b -------------------- *)

Lemma closed_of_check : forall d, bd_closed d = true ->
  forall a b, In a (Bd d) -> In b (Bd d) -> In (bcomp a b) (Bd d).
Proof.
  intros d H a b Ha Hb. unfold bd_closed in H.
  rewrite forallb_forall in H. specialize (H a Ha).
  rewrite forallb_forall in H. specialize (H b Hb).
  apply in_of_existsb. exact H.
Qed.

Lemma bcomp_closed_2 : forall a b,
  In a (Bd 2) -> In b (Bd 2) -> In (bcomp a b) (Bd 2).
Proof. apply closed_of_check. vm_compute. reflexivity. Qed.

Lemma bcomp_closed_3 : forall a b,
  In a (Bd 3) -> In b (Bd 3) -> In (bcomp a b) (Bd 3).
Proof. apply closed_of_check. vm_compute. reflexivity. Qed.

Lemma bid_in_2 : In (bid 2) (Bd 2).
Proof. apply in_of_existsb. vm_compute. reflexivity. Qed.

Lemma bid_in_3 : In (bid 3) (Bd 3).
Proof. apply in_of_existsb. vm_compute. reflexivity. Qed.

Lemma bid_idem_2 : bcomp (bid 2) (bid 2) = bid 2.
Proof. vm_compute. reflexivity. Qed.

Lemma bid_idem_3 : bcomp (bid 3) (bid 3) = bid 3.
Proof. vm_compute. reflexivity. Qed.

(* --- the distinguished generators -------------------------------------- *)
(* adjt d k: the adjacent transposition of memory levels k and k+1 (a pure *)
(* axis exchange, no direction change).  flipgen d k: reversal of axis k    *)
(* alone (a pure direction change, no axis exchange).                      *)

Definition adjt (d k : nat) : bsp :=
  (map (fun i => if Nat.eqb i k then S k
                 else if Nat.eqb i (S k) then k else i) (seq 0 d),
   repeat false d).

Definition flipgen (d k : nat) : bsp :=
  (seq 0 d, map (fun i => Nat.eqb i k) (seq 0 d)).

Example generators_are_in_B3 :
  forallb (fun g => existsb (bsp_eqb g) (Bd 3))
    [adjt 3 0; adjt 3 1; flipgen 3 0; flipgen 3 1; flipgen 3 2] = true.
Proof. vm_compute. reflexivity. Qed.

Example generators_are_in_B2 :
  forallb (fun g => existsb (bsp_eqb g) (Bd 2))
    [adjt 2 0; flipgen 2 0; flipgen 2 1] = true.
Proof. vm_compute. reflexivity. Qed.

(* The two kinds of generator are genuinely different moves, and each has  *)
(* order 2 -- the Z_2 flavour of both factors of B_d.                       *)
Example generators_have_order_two :
  bsp_eqb (bcomp (adjt 3 0) (adjt 3 0)) (bid 3) = true /\
  bsp_eqb (bcomp (flipgen 3 0) (flipgen 3 0)) (bid 3) = true /\
  bsp_eqb (adjt 3 0) (flipgen 3 0) = false.
Proof. repeat split; vm_compute; reflexivity. Qed.

(* The flip subgroup is normal and the axis permutations act on it: this is *)
(* the semidirect structure, checked at d = 3 by conjugating one flip round *)
(* the whole group and finding only flips.                                  *)
Definition is_pure_flip (d : nat) (g : bsp) : bool :=
  if list_eq_dec Nat.eq_dec (fst g) (seq 0 d) then true else false.

Theorem flips_form_a_normal_subgroup_3 :
  forallb (fun g => forallb (fun gi =>
    if bsp_eqb (bcomp g gi) (bid 3)
    then is_pure_flip 3 (bcomp (bcomp g (flipgen 3 0)) gi)
    else true) (Bd 3)) (Bd 3) = true.
Proof. vm_compute. reflexivity. Qed.

(* ===================================================================== *)
(* 2a. THE Z_2-GRADED H/Stab LICENSE.                                     *)
(*                                                                       *)
(* BladeLowering's OutputSymmetryFramework restated, with its two grant    *)
(* forms fused into one bool-graded predicate.  The section signature is   *)
(* BladeLowering's verbatim (r, T, U, f, Hext, Ix, B, D, neg) plus the one *)
(* hypothesis the grading needs: neg is involutive.  That hypothesis is    *)
(* satisfied by every neg the tower uses -- negation on a ring, complex    *)
(* conjugation, the sign flip of BladeDeduce's table 2.                    *)
(*                                                                       *)
(* The point of the grading is that the framework's grant is a SIGN, and   *)
(* signs multiply.  graded_invariant_compose is the homomorphism law; the  *)
(* three corollaries are its three nontrivial instances, of which          *)
(* licensed_anti_anti is the load-bearing one: two anti-invariant layout   *)
(* moves compose to an INVARIANT one, so the licensed set can never be     *)
(* larger than a Z_2-graded extension of the invariant subgroup.  There is *)
(* no third grade to reach for (grant_forms_exhausted), and the grade is   *)
(* not free bookkeeping -- it is uniquely determined as soon as neg has a  *)
(* single non-fixed point on the kernel range (sign_determined).            *)
(* ===================================================================== *)

Section GradedLicense.
  Variable r : nat.
  Variables T U : Type.
  Variable f : (nat -> T) -> U.
  (* locality: the kernel reads only positions < r (BladeLowering's Hext) *)
  Hypothesis Hext : forall v v' : nat -> T,
    (forall p, p < r -> v p = v' p) -> f v = f v'.
  Variable Ix : Type.
  Variable B : nat -> nat.
  Variable D : nat -> Ix -> T.
  Variable neg : U -> U.
  Hypothesis neg_involutive : forall x : U, neg (neg x) = x.

  Definition Out (ix : nat -> Ix) : U := f (fun p => D (B p) (ix p)).

  Definition stabilizes (s : nat -> nat) : Prop :=
    forall p, p < r -> B (s p) = B p.

  Definition into_range (s : nat -> nat) : Prop :=
    forall p, p < r -> s p < r.

  (* The sign action of Z_2 (written additively as bool) on values. *)
  Definition sgn (b : bool) (x : U) : U := if b then neg x else x.

  Lemma sgn_xor : forall b1 b2 x, sgn b1 (sgn b2 x) = sgn (xorb b1 b2) x.
  Proof.
    intros b1 b2 x. destruct b1; destruct b2; simpl;
      try reflexivity. apply neg_involutive.
  Qed.

  (* THE GRADED GRANT.  b = false is BladeLowering's invariant_under,      *)
  (* b = true is its antiinvariant_under.                                  *)
  Definition graded_invariant (b : bool) (s : nat -> nat) : Prop :=
    forall v : nat -> T, f (fun p => v (s p)) = sgn b (f v).

  Definition licensed_with_sign (b : bool) (s : nat -> nat) : Prop :=
    graded_invariant b s /\ stabilizes s.

  (* --- the two grant forms, and the fact that there is no third --------- *)

  Definition invariant_under (s : nat -> nat) : Prop :=
    forall v : nat -> T, f (fun p => v (s p)) = f v.

  Definition antiinvariant_under (s : nat -> nat) : Prop :=
    forall v : nat -> T, f (fun p => v (s p)) = neg (f v).

  Theorem grant_forms_exhausted : forall s,
    (invariant_under s \/ antiinvariant_under s)
    <-> (exists b, graded_invariant b s).
  Proof.
    intro s. split.
    - intros [Hi | Ha].
      + exists false. intro v. exact (Hi v).
      + exists true. intro v. exact (Ha v).
    - intros ([|] & Hb).
      + right. intro v. exact (Hb v).
      + left. intro v. exact (Hb v).
  Qed.

  (* --- soundness, both grades in one statement -------------------------- *)
  (* BladeLowering's output_symmetry_soundness and                          *)
  (* output_antisymmetry_soundness are the b = false and b = true cases.    *)

  Theorem graded_output_soundness : forall b s,
    graded_invariant b s -> stabilizes s ->
    forall ix, Out (fun p => ix (s p)) = sgn b (Out ix).
  Proof.
    intros b s Hinv Hstab ix. unfold Out.
    transitivity (f (fun p => D (B (s p)) (ix (s p)))).
    - apply Hext. intros p Hp. rewrite (Hstab p Hp). reflexivity.
    - exact (Hinv (fun q => D (B q) (ix q))).
  Qed.

  Corollary graded_output_soundness_invariant : forall s,
    invariant_under s -> stabilizes s ->
    forall ix, Out (fun p => ix (s p)) = Out ix.
  Proof.
    intros s Hi Hs ix.
    exact (graded_output_soundness false s (fun v => Hi v) Hs ix).
  Qed.

  Corollary graded_output_soundness_anti : forall s,
    antiinvariant_under s -> stabilizes s ->
    forall ix, Out (fun p => ix (s p)) = neg (Out ix).
  Proof.
    intros s Ha Hs ix.
    exact (graded_output_soundness true s (fun v => Ha v) Hs ix).
  Qed.

  (* --- THE HOMOMORPHISM LAW: signs compose by XOR ---------------------- *)

  Theorem graded_invariant_compose : forall b1 b2 s t,
    graded_invariant b1 s -> graded_invariant b2 t ->
    graded_invariant (xorb b1 b2) (fun p => t (s p)).
  Proof.
    intros b1 b2 s t Hs Ht v.
    transitivity (sgn b1 (f (fun q => v (t q)))).
    - exact (Hs (fun q => v (t q))).
    - rewrite (Ht v). apply sgn_xor.
  Qed.

  Lemma stabilizes_compose : forall s t,
    stabilizes s -> stabilizes t -> into_range s ->
    stabilizes (fun p => t (s p)).
  Proof.
    intros s t Hs Ht Hdom p Hp.
    rewrite (Ht (s p) (Hdom p Hp)). apply Hs. exact Hp.
  Qed.

  Lemma graded_invariant_id : graded_invariant false (fun p => p).
  Proof. intro v. reflexivity. Qed.

  Lemma stabilizes_id : stabilizes (fun p => p).
  Proof. intros p _. reflexivity. Qed.

  Theorem licensed_id : licensed_with_sign false (fun p => p).
  Proof. split; [apply graded_invariant_id | apply stabilizes_id]. Qed.

  Theorem licensed_compose : forall b1 b2 s t,
    licensed_with_sign b1 s -> licensed_with_sign b2 t -> into_range s ->
    licensed_with_sign (xorb b1 b2) (fun p => t (s p)).
  Proof.
    intros b1 b2 s t (Hg1 & Hs1) (Hg2 & Hs2) Hdom. split.
    - apply graded_invariant_compose; assumption.
    - apply stabilizes_compose; assumption.
  Qed.

  (* The three nontrivial instances.  The first restates BladeLowering's    *)
  (* invariant_compose in the graded form; the second is the one that       *)
  (* caps the licensed set -- two sign flips CANCEL.                        *)

  Corollary licensed_inv_inv : forall s t,
    licensed_with_sign false s -> licensed_with_sign false t ->
    into_range s -> licensed_with_sign false (fun p => t (s p)).
  Proof. intros s t H1 H2 H3. exact (licensed_compose false false s t H1 H2 H3). Qed.

  Corollary licensed_anti_anti : forall s t,
    licensed_with_sign true s -> licensed_with_sign true t ->
    into_range s -> licensed_with_sign false (fun p => t (s p)).
  Proof. intros s t H1 H2 H3. exact (licensed_compose true true s t H1 H2 H3). Qed.

  Corollary licensed_anti_inv : forall s t,
    licensed_with_sign true s -> licensed_with_sign false t ->
    into_range s -> licensed_with_sign true (fun p => t (s p)).
  Proof. intros s t H1 H2 H3. exact (licensed_compose true false s t H1 H2 H3). Qed.

  Corollary licensed_inv_anti : forall s t,
    licensed_with_sign false s -> licensed_with_sign true t ->
    into_range s -> licensed_with_sign true (fun p => t (s p)).
  Proof. intros s t H1 H2 H3. exact (licensed_compose false true s t H1 H2 H3). Qed.

  (* --- the grade is not free bookkeeping ------------------------------- *)
  (* If neg fixes everything (e.g. characteristic 2, or neg = identity)     *)
  (* the grading is vacuous.  Off that degenerate locus -- one value with   *)
  (* f v <> neg (f v) is enough -- the grade of a licensed move is UNIQUE,  *)
  (* so the Z_2 grading is a genuine invariant of the move and the          *)
  (* homomorphism above is a genuine character.                             *)

  Theorem sign_determined : forall b1 b2 s,
    graded_invariant b1 s -> graded_invariant b2 s ->
    (exists v : nat -> T, f v <> neg (f v)) -> b1 = b2.
  Proof.
    intros b1 b2 s H1 H2 (v & Hv).
    specialize (H1 v). specialize (H2 v).
    destruct b1; destruct b2; simpl in H1, H2;
      try reflexivity; exfalso; apply Hv.
    - transitivity (f (fun p => v (s p))); [symmetry; exact H2 | exact H1].
    - transitivity (f (fun p => v (s p))); [symmetry; exact H1 | exact H2].
  Qed.

  (* The degenerate converse, for the record: if neg is the identity there  *)
  (* is only one grade, and anti-invariance says nothing beyond invariance. *)
  Theorem grading_vacuous_when_neg_trivial :
    (forall x : U, neg x = x) ->
    forall s, invariant_under s <-> antiinvariant_under s.
  Proof.
    intros Hn s. split; intros H v; rewrite (H v).
    - symmetry. apply Hn.
    - apply Hn.
  Qed.
End GradedLicense.

(* ===================================================================== *)
(* 2b. HOW MANY SUCH CHARACTERS ARE THERE?  EXACTLY FOUR.                 *)
(*                                                                       *)
(* Section 2a says the grade map of a licensed family is a homomorphism    *)
(* B_d -> Z_2.  This section counts those homomorphisms.  Enumerating all  *)
(* functions bsp -> bool is 2^|B_d| and infeasible, so the count is closed *)
(* the way it is closed in representation theory: by generators.           *)
(*                                                                       *)
(*   (i)   FOUR ARE EXHIBITED -- trivial, the sign of the permutation      *)
(*         part, the parity of the flip vector, and their xor -- each      *)
(*         checked to be a homomorphism over the whole multiplication      *)
(*         table at d = 2, 3, 4, and pairwise distinguished by their       *)
(*         values on one transposition and one flip.                       *)
(*                                                                       *)
(*   (ii)  B_d IS GENERATED by the Coxeter set {s_1..s_(d-1), phi_1}:      *)
(*         all d-1 adjacent transpositions plus ONE flip.  Computed as a   *)
(*         breadth-first closure that carries a WORD for every element,    *)
(*         then checked two ways -- every recorded word really evaluates   *)
(*         to its element (wtable_correct) and every group element is      *)
(*         reached (wtable_covers) -- at d = 2, 3, 4.                      *)
(*                                                                       *)
(*         RECORDED REFUTATION: one transposition plus one flip is NOT     *)
(*         enough for d >= 3.  {s_1, phi_1} closes at 8 elements out of 48 *)
(*         at d = 3 (tau_phi_does_not_generate_b3).  It happens to suffice *)
(*         at d = 2 only because there is just one adjacent transposition  *)
(*         there.                                                         *)
(*                                                                       *)
(*   (iii) CONJUGACY COLLAPSES THE GENERATOR VALUES.  A character is       *)
(*         constant on conjugacy classes (conj_same, from                  *)
(*         chi g = chi g^-1), all adjacent transpositions are conjugate,   *)
(*         and all flips are conjugate (checked at d = 3 and d = 4).  So   *)
(*         the d generator values collapse to TWO: one for transpositions, *)
(*         one for flips.  And they do not collapse further -- a           *)
(*         transposition is NOT conjugate to a flip                        *)
(*         (transposition_not_conjugate_to_flip_3), so both bits are live. *)
(*                                                                       *)
(*   (iv)  HENCE AT MOST FOUR, and four are exhibited, so EXACTLY FOUR     *)
(*         (b2/b3_character_classified: every character agrees with one of *)
(*         the four on the whole group).                                   *)
(*                                                                       *)
(* THE CONTRAST, which is the headline.  4 is constant in d.  The number   *)
(* of irreducible representations of B_d is the number of BIPARTITIONS of  *)
(* d -- 2, 5, 10, 20, 36, 65, 110, 185, 300, 481 for d = 1..10 -- and      *)
(* grows without bound (bipartition_counts).  The classification of        *)
(* B_d-irreps by bipartitions is classical and is CITED, not proved here;  *)
(* what is proved here is the bipartition ARITHMETIC and the fact that the *)
(* linear-character count stays at 4.  So the H/Stab grant vocabulary      *)
(* indexes a 4-element set, never the growing one.                         *)
(*                                                                       *)
(* d = 1 is the boundary case, and it is degenerate in the same way        *)
(* BladeWreath's r = 1 is: there is no transposition at all, chi_perm      *)
(* collapses onto chi_triv and chi_both onto chi_flip, and only 2          *)
(* characters survive -- which is exactly bipart 1 = 2 (d1_degeneracy).    *)
(* ===================================================================== *)

Definition biff (x y : bool) : bool := if x then y else negb y.

Lemma biff_true : forall x y, biff x y = true -> x = y.
Proof. intros [|] [|] H; simpl in H; congruence. Qed.

(* --- the four candidate characters ------------------------------------- *)

Definition upairs (m : nat) : list (nat * nat) :=
  flat_map (fun i => map (fun j => (i, j)) (seq (S i) (m - S i))) (seq 0 m).

(* Inversions of the permutation part: the classical parity that computes  *)
(* the sign of a permutation.                                             *)
Definition inv_count (s : list nat) : nat :=
  length (filter (fun ij => Nat.ltb (nth (snd ij) s 0) (nth (fst ij) s 0))
                 (upairs (length s))).

Definition chi_triv (g : bsp) : bool := false.
Definition chi_perm (g : bsp) : bool := Nat.odd (inv_count (fst g)).
Definition chi_flip (g : bsp) : bool := fold_right xorb false (snd g).
Definition chi_both (g : bsp) : bool := xorb (chi_perm g) (chi_flip g).

Definition four_chars : list (bsp -> bool) :=
  [chi_triv; chi_perm; chi_flip; chi_both].

Definition is_character (d : nat) (chi : bsp -> bool) : bool :=
  forallb (fun g => forallb (fun h =>
    biff (chi (bcomp g h)) (xorb (chi g) (chi h))) (Bd d)) (Bd d).

(* Sanity: the sign of the permutation part is the classical one -- the    *)
(* adjacent transposition is odd, the identity is even.                     *)
Example chi_perm_on_generators :
  chi_perm (adjt 3 0) = true /\ chi_perm (adjt 3 1) = true /\
  chi_perm (bid 3) = false /\ chi_perm (flipgen 3 0) = false.
Proof. repeat split; vm_compute; reflexivity. Qed.

Example chi_flip_on_generators :
  chi_flip (flipgen 3 0) = true /\ chi_flip (adjt 3 0) = false /\
  chi_flip (bid 3) = false.
Proof. repeat split; vm_compute; reflexivity. Qed.

(* --- (i) all four are genuine characters, at d = 2, 3, 4 --------------- *)

Theorem chi_triv_is_character_2 : is_character 2 chi_triv = true.
Proof. vm_compute. reflexivity. Qed.
Theorem chi_perm_is_character_2 : is_character 2 chi_perm = true.
Proof. vm_compute. reflexivity. Qed.
Theorem chi_flip_is_character_2 : is_character 2 chi_flip = true.
Proof. vm_compute. reflexivity. Qed.
Theorem chi_both_is_character_2 : is_character 2 chi_both = true.
Proof. vm_compute. reflexivity. Qed.

Theorem chi_triv_is_character_3 : is_character 3 chi_triv = true.
Proof. vm_compute. reflexivity. Qed.
Theorem chi_perm_is_character_3 : is_character 3 chi_perm = true.
Proof. vm_compute. reflexivity. Qed.
Theorem chi_flip_is_character_3 : is_character 3 chi_flip = true.
Proof. vm_compute. reflexivity. Qed.
Theorem chi_both_is_character_3 : is_character 3 chi_both = true.
Proof. vm_compute. reflexivity. Qed.

Theorem four_characters_are_characters_4 :
  forallb (is_character 4) four_chars = true.
Proof. vm_compute. reflexivity. Qed.

(* --- pairwise distinct, via their values on (one transposition, one flip) *)

Definition bb_dec (x y : bool * bool) : {x = y} + {x <> y}.
Proof.
  destruct x as [a b]; destruct y as [c e].
  destruct (bool_dec a c); destruct (bool_dec b e).
  - left; subst; reflexivity.
  - right; intro H; inversion H; contradiction.
  - right; intro H; inversion H; contradiction.
  - right; intro H; inversion H; contradiction.
Defined.

Definition char_signature (d : nat) (chi : bsp -> bool) : bool * bool :=
  (chi (adjt d 0), chi (flipgen d 0)).

Theorem four_characters_signatures_3 :
  map (char_signature 3) four_chars
  = [(false, false); (true, false); (false, true); (true, true)].
Proof. vm_compute. reflexivity. Qed.

Theorem four_characters_pairwise_distinct_3 :
  length (nodup bb_dec (map (char_signature 3) four_chars)) = 4.
Proof. vm_compute. reflexivity. Qed.

Theorem four_characters_pairwise_distinct_2 :
  length (nodup bb_dec (map (char_signature 2) four_chars)) = 4.
Proof. vm_compute. reflexivity. Qed.

Theorem four_characters_pairwise_distinct_4 :
  length (nodup bb_dec (map (char_signature 4) four_chars)) = 4.
Proof. vm_compute. reflexivity. Qed.

(* --- (ii) generation, with words carried along ------------------------- *)

Fixpoint wval (d : nat) (gens : list bsp) (w : list nat) : bsp :=
  match w with
  | [] => bid d
  | k :: w' => bcomp (nth k gens (bid d)) (wval d gens w')
  end.

Fixpoint absorb (acc cands : list (bsp * list nat)) : list (bsp * list nat) :=
  match cands with
  | [] => acc
  | c :: cs =>
      if existsb (fun p => bsp_eqb (fst p) (fst c)) acc
      then absorb acc cs
      else absorb (acc ++ [c]) cs
  end.

Definition grow (d : nat) (gens : list bsp) (acc : list (bsp * list nat))
  : list (bsp * list nat) :=
  absorb acc
    (flat_map (fun p =>
       map (fun k => (bcomp (nth k gens (bid d)) (fst p), k :: snd p))
           (seq 0 (length gens))) acc).

Fixpoint gclose (d : nat) (gens : list bsp) (n : nat)
  : list (bsp * list nat) :=
  match n with
  | 0 => [(bid d, [])]
  | S k => grow d gens (gclose d gens k)
  end.

Definition wtable_correct (d : nat) (gens : list bsp) (n : nat) : bool :=
  forallb (fun p => bsp_eqb (wval d gens (snd p)) (fst p)) (gclose d gens n).

Definition wtable_covers (d : nat) (gens : list bsp) (n : nat) : bool :=
  forallb (fun g => existsb (fun p => bsp_eqb (fst p) g) (gclose d gens n))
          (Bd d).

(* THE COXETER GENERATING SETS: all adjacent transpositions, plus ONE      *)
(* flip.  Written literally so the membership proofs below stay one-liners. *)
Definition gens2 : list bsp := [adjt 2 0; flipgen 2 0].
Definition gens3 : list bsp := [adjt 3 0; adjt 3 1; flipgen 3 0].

Theorem b2_generated_by_coxeter :
  wtable_correct 2 gens2 8 = true /\ wtable_covers 2 gens2 8 = true.
Proof. split; vm_compute; reflexivity. Qed.

Theorem b3_generated_by_coxeter :
  wtable_correct 3 gens3 12 = true /\ wtable_covers 3 gens3 12 = true.
Proof. split; vm_compute; reflexivity. Qed.

Example coxeter_closures_are_the_whole_group :
  length (gclose 2 gens2 8) = 8 /\ length (gclose 3 gens3 12) = 48.
Proof. split; vm_compute; reflexivity. Qed.

(* THE REFUTATION.  One transposition plus one flip is NOT a generating    *)
(* set past d = 2: at d = 3 it closes at 8 of 48 elements.  (At d = 2      *)
(* there is only one adjacent transposition, so the Coxeter set IS         *)
(* {tau, phi} and the coincidence is why d = 2 is misleading.)             *)
Theorem tau_phi_does_not_generate_b3 :
  wtable_covers 3 [adjt 3 0; flipgen 3 0] 12 = false.
Proof. vm_compute. reflexivity. Qed.

Example tau_phi_closure_is_eight_of_48 :
  length (gclose 3 [adjt 3 0; flipgen 3 0] 12) = 8 /\ length (Bd 3) = 48.
Proof. split; vm_compute; reflexivity. Qed.

(* --- (iii) conjugacy of the generator classes -------------------------- *)

Definition conj_check (d : nat) (a b : bsp) : bool :=
  existsb (fun g => existsb (fun gi =>
    bsp_eqb (bcomp g gi) (bid d) &&
    bsp_eqb (bcomp (bcomp g a) gi) b) (Bd d)) (Bd d).

Theorem adjacent_transpositions_conjugate_3 :
  conj_check 3 (adjt 3 0) (adjt 3 1) = true.
Proof. vm_compute. reflexivity. Qed.

Theorem flips_conjugate_3 :
  forallb (fun k => conj_check 3 (flipgen 3 0) (flipgen 3 k)) [0; 1; 2] = true.
Proof. vm_compute. reflexivity. Qed.

Theorem adjacent_transpositions_conjugate_4 :
  forallb (fun k => conj_check 4 (adjt 4 0) (adjt 4 k)) [0; 1; 2] = true.
Proof. vm_compute. reflexivity. Qed.

Theorem flips_conjugate_4 :
  forallb (fun k => conj_check 4 (flipgen 4 0) (flipgen 4 k)) [0; 1; 2; 3]
  = true.
Proof. vm_compute. reflexivity. Qed.

(* And the two classes do NOT merge.  If a transposition were conjugate to *)
(* a flip the two bits would collapse and there would be only 2 characters *)
(* at every d -- so this refutation is what keeps the count at 4.           *)
Theorem transposition_not_conjugate_to_flip_3 :
  conj_check 3 (adjt 3 0) (flipgen 3 0) = false.
Proof. vm_compute. reflexivity. Qed.

Theorem transposition_not_conjugate_to_flip_4 :
  conj_check 4 (adjt 4 0) (flipgen 4 0) = false.
Proof. vm_compute. reflexivity. Qed.

(* --- the Prop-level completeness argument ------------------------------ *)

Definition is_hom (d : nat) (chi : bsp -> bool) : Prop :=
  forall a b, In a (Bd d) -> In b (Bd d) ->
    chi (bcomp a b) = xorb (chi a) (chi b).

Lemma hom_of_check : forall d chi, is_character d chi = true -> is_hom d chi.
Proof.
  intros d chi H a b Ha Hb. unfold is_character in H.
  rewrite forallb_forall in H. specialize (H a Ha).
  rewrite forallb_forall in H. specialize (H b Hb).
  apply biff_true. exact H.
Qed.

Section CharacterCompleteness.
  Variable d : nat.
  Hypothesis Hcl : forall a b,
    In a (Bd d) -> In b (Bd d) -> In (bcomp a b) (Bd d).
  Hypothesis Hbid : In (bid d) (Bd d).
  Hypothesis Hidem : bcomp (bid d) (bid d) = bid d.

  Lemma char_bid : forall chi, is_hom d chi -> chi (bid d) = false.
  Proof.
    intros chi H. specialize (H _ _ Hbid Hbid). rewrite Hidem in H.
    destruct (chi (bid d)); simpl in H; congruence.
  Qed.

  (* A character is constant on conjugacy classes: chi g = chi g^-1        *)
  (* because chi g xor chi g^-1 = chi (identity) = false, so the two        *)
  (* conjugating factors cancel.                                           *)
  Lemma conj_same : forall chi g gi h,
    is_hom d chi -> In g (Bd d) -> In gi (Bd d) -> In h (Bd d) ->
    bcomp g gi = bid d ->
    chi (bcomp (bcomp g h) gi) = chi h.
  Proof.
    intros chi g gi h Hh Hg Hgi Hhh Hinv.
    assert (Hbc : In (bcomp g h) (Bd d)) by (apply Hcl; assumption).
    rewrite (Hh _ _ Hbc Hgi). rewrite (Hh _ _ Hg Hhh).
    assert (Hz : xorb (chi g) (chi gi) = false).
    { rewrite <- (Hh _ _ Hg Hgi). rewrite Hinv. apply char_bid. exact Hh. }
    destruct (chi g); destruct (chi gi); destruct (chi h);
      simpl in Hz |- *; congruence.
  Qed.

  Lemma conj_check_sound : forall chi a b,
    is_hom d chi -> In a (Bd d) -> conj_check d a b = true ->
    chi b = chi a.
  Proof.
    intros chi a b Hh Ha Hc. unfold conj_check in Hc.
    apply existsb_exists in Hc as (g & Hg & Hc).
    apply existsb_exists in Hc as (gi & Hgi & Hc).
    apply andb_true_iff in Hc as (H1 & H2).
    apply bsp_eqb_eq in H1. apply bsp_eqb_eq in H2.
    rewrite <- H2. exact (conj_same chi g gi a Hh Hg Hgi Ha H1).
  Qed.

  Lemma wval_in_Bd : forall gens w,
    (forall x, In x gens -> In x (Bd d)) -> In (wval d gens w) (Bd d).
  Proof.
    intros gens w Hg. induction w as [|k w IH]; simpl.
    - exact Hbid.
    - apply Hcl; [| exact IH].
      destruct (nth_in_or_default k gens (bid d)) as [Hin | He].
      + apply Hg. exact Hin.
      + rewrite He. exact Hbid.
  Qed.

  (* The word-induction step (BladeWordClosure's word_closure, specialized  *)
  (* to a Z_2-valued homomorphism): a character of a word is the xor of its  *)
  (* characters of the letters.                                             *)
  Lemma hom_word : forall gens chi,
    is_hom d chi -> (forall x, In x gens -> In x (Bd d)) ->
    forall w, chi (wval d gens w)
            = fold_right xorb false
                (map (fun k => chi (nth k gens (bid d))) w).
  Proof.
    intros gens chi Hh Hg w. induction w as [|k w IH]; simpl.
    - apply char_bid. exact Hh.
    - assert (Hk : In (nth k gens (bid d)) (Bd d)).
      { destruct (nth_in_or_default k gens (bid d)) as [Hin | He].
        - apply Hg. exact Hin.
        - rewrite He. exact Hbid. }
      rewrite (Hh _ _ Hk (wval_in_Bd gens w Hg)), IH. reflexivity.
  Qed.

  Lemma generated_by_gens : forall gens n,
    wtable_correct d gens n = true -> wtable_covers d gens n = true ->
    forall g, In g (Bd d) -> exists w, wval d gens w = g.
  Proof.
    intros gens n Hc Hcov g Hg.
    unfold wtable_covers in Hcov. rewrite forallb_forall in Hcov.
    specialize (Hcov g Hg). apply existsb_exists in Hcov as (p & Hp & Hb).
    apply bsp_eqb_eq in Hb.
    unfold wtable_correct in Hc. rewrite forallb_forall in Hc.
    specialize (Hc p Hp). apply bsp_eqb_eq in Hc.
    exists (snd p). rewrite Hc. exact Hb.
  Qed.

  (* THE COMPLETENESS ENGINE: two characters agreeing on a generating set   *)
  (* agree everywhere.                                                      *)
  Theorem char_determined : forall gens n chi1 chi2,
    is_hom d chi1 -> is_hom d chi2 ->
    (forall x, In x gens -> In x (Bd d)) ->
    (forall x, In x gens -> chi1 x = chi2 x) ->
    wtable_correct d gens n = true -> wtable_covers d gens n = true ->
    forall g, In g (Bd d) -> chi1 g = chi2 g.
  Proof.
    intros gens n chi1 chi2 Hh1 Hh2 Hgin Hagree Hc Hcov g Hg.
    destruct (generated_by_gens gens n Hc Hcov g Hg) as (w & Hw).
    rewrite <- Hw.
    rewrite (hom_word gens chi1 Hh1 Hgin w),
            (hom_word gens chi2 Hh2 Hgin w).
    f_equal. apply map_ext. intro k.
    destruct (nth_in_or_default k gens (bid d)) as [Hin | He].
    - apply Hagree. exact Hin.
    - rewrite He, (char_bid chi1 Hh1), (char_bid chi2 Hh2). reflexivity.
  Qed.
End CharacterCompleteness.

(* --- (iv) EXACTLY FOUR, at d = 2 and d = 3 ----------------------------- *)

Lemma gens2_in_Bd : forall x, In x gens2 -> In x (Bd 2).
Proof.
  intros x Hx. unfold gens2 in Hx. simpl in Hx.
  destruct Hx as [E | [E | []]]; rewrite <- E;
    apply in_of_existsb; vm_compute; reflexivity.
Qed.

Lemma gens3_in_Bd : forall x, In x gens3 -> In x (Bd 3).
Proof.
  intros x Hx. unfold gens3 in Hx. simpl in Hx.
  destruct Hx as [E | [E | [E | []]]]; rewrite <- E;
    apply in_of_existsb; vm_compute; reflexivity.
Qed.

Lemma adjt30_in_Bd : In (adjt 3 0) (Bd 3).
Proof. apply in_of_existsb. vm_compute. reflexivity. Qed.

(* The character determined by a pair of generator values: a is the value  *)
(* on transpositions, b the value on flips.  Every character of B_d is of   *)
(* this shape, which is exactly the at-most-four bound made explicit.      *)
Definition chi_of (a b : bool) (g : bsp) : bool :=
  xorb (andb a (chi_perm g)) (andb b (chi_flip g)).

Lemma chi_of_is_one_of_four : forall a b,
  exists c, In c four_chars /\ (forall g, chi_of a b g = c g).
Proof.
  intros [|] [|].
  - exists chi_both. split.
    + unfold four_chars; simpl; right; right; right; left; reflexivity.
    + intro g. unfold chi_of, chi_both. reflexivity.
  - exists chi_perm. split.
    + unfold four_chars; simpl; right; left; reflexivity.
    + intro g. unfold chi_of. simpl. destruct (chi_perm g); reflexivity.
  - exists chi_flip. split.
    + unfold four_chars; simpl; right; right; left; reflexivity.
    + intro g. unfold chi_of. simpl. destruct (chi_flip g); reflexivity.
  - exists chi_triv. split.
    + unfold four_chars; simpl; left; reflexivity.
    + intro g. unfold chi_of, chi_triv. reflexivity.
Qed.

Lemma chi_of_is_character_2 : forall a b, is_character 2 (chi_of a b) = true.
Proof. intros [|] [|]; vm_compute; reflexivity. Qed.

Lemma chi_of_is_character_3 : forall a b, is_character 3 (chi_of a b) = true.
Proof. intros [|] [|]; vm_compute; reflexivity. Qed.

Lemma chi_of_gen_values_2 : forall a b,
  chi_of a b (adjt 2 0) = a /\ chi_of a b (flipgen 2 0) = b.
Proof. intros [|] [|]; split; vm_compute; reflexivity. Qed.

Lemma chi_of_gen_values_3 : forall a b,
  chi_of a b (adjt 3 0) = a /\ chi_of a b (adjt 3 1) = a /\
  chi_of a b (flipgen 3 0) = b.
Proof. intros [|] [|]; repeat split; vm_compute; reflexivity. Qed.

(* At d = 2 the Coxeter set IS {tau, phi}, so no conjugacy step is needed. *)
Theorem b2_character_classified : forall chi,
  is_character 2 chi = true ->
  forall g, In g (Bd 2) ->
    chi g = chi_of (chi (adjt 2 0)) (chi (flipgen 2 0)) g.
Proof.
  intros chi Hc.
  assert (Hh : is_hom 2 chi) by (apply hom_of_check; exact Hc).
  destruct (chi_of_gen_values_2 (chi (adjt 2 0)) (chi (flipgen 2 0)))
    as (E1 & E2).
  apply (char_determined 2 bcomp_closed_2 bid_in_2 bid_idem_2 gens2 8
           chi (chi_of (chi (adjt 2 0)) (chi (flipgen 2 0))) Hh
           (hom_of_check 2 _ (chi_of_is_character_2 _ _)) gens2_in_Bd).
  - intros x Hx. unfold gens2 in Hx. simpl in Hx.
    destruct Hx as [E | [E | []]]; rewrite <- E.
    + rewrite E1. reflexivity.
    + rewrite E2. reflexivity.
  - exact (proj1 b2_generated_by_coxeter).
  - exact (proj2 b2_generated_by_coxeter).
Qed.

(* At d = 3 the second adjacent transposition is pinned by conjugacy: it    *)
(* must receive the same value as the first, which is why two bits and not  *)
(* three suffice.                                                          *)
Theorem b3_character_classified : forall chi,
  is_character 3 chi = true ->
  forall g, In g (Bd 3) ->
    chi g = chi_of (chi (adjt 3 0)) (chi (flipgen 3 0)) g.
Proof.
  intros chi Hc.
  assert (Hh : is_hom 3 chi) by (apply hom_of_check; exact Hc).
  assert (Hcj : chi (adjt 3 1) = chi (adjt 3 0)).
  { exact (conj_check_sound 3 bcomp_closed_3 bid_in_3 bid_idem_3 chi
             (adjt 3 0) (adjt 3 1) Hh adjt30_in_Bd
             adjacent_transpositions_conjugate_3). }
  destruct (chi_of_gen_values_3 (chi (adjt 3 0)) (chi (flipgen 3 0)))
    as (E1 & E2 & E3).
  apply (char_determined 3 bcomp_closed_3 bid_in_3 bid_idem_3 gens3 12
           chi (chi_of (chi (adjt 3 0)) (chi (flipgen 3 0))) Hh
           (hom_of_check 3 _ (chi_of_is_character_3 _ _)) gens3_in_Bd).
  - intros x Hx. unfold gens3 in Hx. simpl in Hx.
    destruct Hx as [E | [E | [E | []]]]; rewrite <- E.
    + rewrite E1. reflexivity.
    + rewrite E2. exact Hcj.
    + rewrite E3. reflexivity.
  - exact (proj1 b3_generated_by_coxeter).
  - exact (proj2 b3_generated_by_coxeter).
Qed.

(* EXACTLY FOUR: every character of B_2 / B_3 agrees on the whole group     *)
(* with one of the four exhibited, and the four are pairwise distinct       *)
(* (four_characters_pairwise_distinct_2/3 above).                          *)
Corollary b2_character_is_one_of_four : forall chi,
  is_character 2 chi = true ->
  exists c, In c four_chars /\ (forall g, In g (Bd 2) -> chi g = c g).
Proof.
  intros chi Hc.
  destruct (chi_of_is_one_of_four (chi (adjt 2 0)) (chi (flipgen 2 0)))
    as (c & Hin & He).
  exists c. split; [exact Hin |].
  intros g Hg. rewrite (b2_character_classified chi Hc g Hg). apply He.
Qed.

Corollary b3_character_is_one_of_four : forall chi,
  is_character 3 chi = true ->
  exists c, In c four_chars /\ (forall g, In g (Bd 3) -> chi g = c g).
Proof.
  intros chi Hc.
  destruct (chi_of_is_one_of_four (chi (adjt 3 0)) (chi (flipgen 3 0)))
    as (c & Hin & He).
  exists c. split; [exact Hin |].
  intros g Hg. rewrite (b3_character_classified chi Hc g Hg). apply He.
Qed.

(* ===================================================================== *)
(* 2c. THE FORWARD QUOTIENT: B_d / Z_2^d, AND WHICH CHARACTERS SURVIVE.    *)
(*                                                                       *)
(* Direction is FREE GAUGE.  Reversing the traversal of an axis is         *)
(* value-neutral whenever the reduce monoid is commutative and associative *)
(* (section 3), and it costs nothing in hardware.  So the pure-flip        *)
(* subgroup Z_2^d is ALWAYS licensed, with no declaration of any kind, and *)
(* canonical form fixes the direction of every axis to forward.            *)
(*                                                                       *)
(* This is a QUOTIENT, not a restriction, and that is what makes it a      *)
(* strengthening.  Z_2^d is normal in B_d (zflips_normal_2/3), the number  *)
(* of cosets is exactly d! (quotient_coset_count -- the cardinality        *)
(* statement of B_d / Z_2^d = S_d; the group isomorphism itself is         *)
(* classical and is cited, not proved here), and because the              *)
(* always-licensed Z_2^d sits INSIDE the licensed group, quotienting by it *)
(* makes the orbits BIGGER -- it rules out MORE iteration patterns, never  *)
(* fewer.                                                                 *)
(*                                                                       *)
(* THE RULE-OUT FACTOR IS MULTIPLICATIVE: 2^d from direction, which is     *)
(* free and available even with no declaration at all, times |G_S| from    *)
(* the declared symmetry.  Enumerated below                                *)
(* (ruleout_no_declaration, ruleout_full_symmetry):                        *)
(*                                                                       *)
(*     d      layouts   no declaration        fully symmetric              *)
(*     2         8      2 orbits  (4x)        1 orbit  (8x)                *)
(*     3        48      6 orbits  (8x)        1 orbit  (48x)               *)
(*     4       384     24 orbits (16x)        1 orbit  (384x)              *)
(*                                                                       *)
(* THE CHARACTER DICHOTOMY.  Of the four characters of B_d, exactly the    *)
(* two that are TRIVIAL ON Z_2^d descend to the quotient: the trivial      *)
(* character and the sign of the permutation part                          *)
(* (which_characters_descend, exactly_two_characters_descend).  Those two  *)
(* are precisely Blade's SymIdx (+1) and AntisymIdx (-1).  So the ENTIRE   *)
(* representation theory needed to select a forward-only layout is already *)
(* in the type system -- no new declaration form is required.               *)
(*                                                                       *)
(* The other two characters are not lost.  They are supported on the flip  *)
(* subgroup, so they are exactly the SIGN picked up when a backward-       *)
(* oriented layout is canonicalized into its forward representative.  A    *)
(* reflection-odd axis therefore never blocks canonicalization; it         *)
(* contributes a tracked sign, uniformly with the antisymmetric case.      *)
(*                                                                       *)
(* CONTRAST, the headline in its final form.  After the quotient the       *)
(* observable character group has exactly 2 elements at every d, while the *)
(* number of irreducible representations of S_d is the partition count     *)
(* 1, 2, 3, 5, 7, 11, 15, 22, 30, 42 for d = 1..10 and grows without       *)
(* bound (partition_counts).  Before the quotient the same contrast is 4   *)
(* against the bipartition count 2, 5, 10, 20, 36, ... (bipartition_counts).*)
(* Both irrep classifications are classical and are cited; what is proved  *)
(* here is the counting arithmetic and the constancy of the character      *)
(* count.                                                                 *)
(* ===================================================================== *)

Definition lic_group (d : nat) (GS : list (list nat)) : list bsp :=
  flat_map (fun s => map (fun v => (s, v)) (bvecs d)) GS.

(* The always-licensed direction subgroup: identity permutation, every     *)
(* flip vector.                                                            *)
Definition zflips (d : nat) : list bsp := lic_group d [seq 0 d].

Example zflips_orders : map (fun d => length (zflips d)) [2; 3; 4] = [4; 8; 16].
Proof. vm_compute. reflexivity. Qed.

Theorem zflips_is_a_subgroup_3 :
  forallb (fun z => existsb (bsp_eqb z) (Bd 3)) (zflips 3) = true /\
  forallb (fun z => forallb (fun w => existsb (bsp_eqb (bcomp z w)) (zflips 3))
                            (zflips 3)) (zflips 3) = true.
Proof. split; vm_compute; reflexivity. Qed.

(* Normality: conjugating any pure flip by any group element gives a pure   *)
(* flip.  This is the semidirect structure of B_d = Z_2^d : S_d.            *)
Theorem zflips_normal_2 :
  forallb (fun g => forallb (fun gi =>
    if bsp_eqb (bcomp g gi) (bid 2)
    then forallb (fun z => is_pure_flip 2 (bcomp (bcomp g z) gi)) (zflips 2)
    else true) (Bd 2)) (Bd 2) = true.
Proof. vm_compute. reflexivity. Qed.

Theorem zflips_normal_3 :
  forallb (fun g => forallb (fun gi =>
    if bsp_eqb (bcomp g gi) (bid 3)
    then forallb (fun z => is_pure_flip 3 (bcomp (bcomp g z) gi)) (zflips 3)
    else true) (Bd 3)) (Bd 3) = true.
Proof. vm_compute. reflexivity. Qed.

(* --- orbits of a licensed subgroup, by lexicographically least element -- *)
(* Orbits of the licensed subgroup H acting on layouts by left               *)
(* multiplication are named by their lex-least element.  Everything          *)
(* compared here is a list of digits below d, so no large numerals are ever  *)
(* built -- the arithmetic-coding version of this overflowed unary nat.      *)

Fixpoint lex_le_nat (a b : list nat) : bool :=
  match a, b with
  | [], _ => true
  | _, [] => false
  | x :: a', y :: b' =>
      if Nat.ltb x y then true
      else if Nat.eqb x y then lex_le_nat a' b' else false
  end.

Definition bkey (g : bsp) : list nat :=
  fst g ++ map (fun b : bool => if b then 1 else 0) (snd g).

Definition bmin (g h : bsp) : bsp :=
  if lex_le_nat (bkey g) (bkey h) then g else h.

Definition orbit_rep (H : list bsp) (g : bsp) : bsp :=
  fold_right bmin g (map (fun h => bcomp h g) H).

Definition num_orbits (d : nat) (H : list bsp) : nat :=
  length (nodup bsp_dec (map (orbit_rep H) (Bd d))).

(* The representative is CONSTANT ON ORBITS, so num_orbits really counts    *)
(* orbits and not merely distinct values of a function.                     *)
Definition orbit_rep_stable (d : nat) (H : list bsp) : bool :=
  forallb (fun g => forallb (fun h =>
    bsp_eqb (orbit_rep H (bcomp h g)) (orbit_rep H g)) H) (Bd d).

Theorem orbit_rep_is_stable :
  orbit_rep_stable 2 (zflips 2) = true /\
  orbit_rep_stable 3 (zflips 3) = true /\
  orbit_rep_stable 4 (zflips 4) = true /\
  orbit_rep_stable 2 (lic_group 2 (permsOf (seq 0 2))) = true /\
  orbit_rep_stable 3 (lic_group 3 (permsOf (seq 0 3))) = true /\
  orbit_rep_stable 3 (lic_group 3 [[0; 1; 2]; [1; 0; 2]]) = true.
Proof. repeat split; vm_compute; reflexivity. Qed.

(* B_d / Z_2^d has exactly d! cosets -- the cardinality half of the         *)
(* statement that the forward quotient is S_d.  (The group isomorphism      *)
(* itself is classical and is cited, not proved here.)                      *)
Theorem quotient_coset_count :
  num_orbits 2 (zflips 2) = 2 /\
  num_orbits 3 (zflips 3) = 6 /\
  num_orbits 4 (zflips 4) = 24.
Proof. repeat split; vm_compute; reflexivity. Qed.

(* The rule-out factor with NO declaration: direction alone already cuts   *)
(* the layout search space by 2^d.                                         *)
Theorem ruleout_no_declaration :
  (length (Bd 2) = 8  /\ num_orbits 2 (zflips 2) = 2) /\
  (length (Bd 3) = 48 /\ num_orbits 3 (zflips 3) = 6) /\
  (length (Bd 4) = 384 /\ num_orbits 4 (zflips 4) = 24).
Proof. repeat split; vm_compute; reflexivity. Qed.

(* With a full symmetry declaration the licensed group is all of B_d and    *)
(* the whole layout space collapses to ONE orbit: rule-out factor 2^d d!.   *)
(* (The same computation at d = 4 gives 1 as well; it is left out only      *)
(* because the stability sweep over 384 elements is expensive.)             *)
Theorem ruleout_full_symmetry :
  num_orbits 2 (lic_group 2 (permsOf (seq 0 2))) = 1 /\
  num_orbits 3 (lic_group 3 (permsOf (seq 0 3))) = 1.
Proof. split; vm_compute; reflexivity. Qed.

(* An intermediate declaration (symmetric in axes 0 and 1 only, at d = 3):  *)
(* rule-out factor 2^3 * 2 = 16, leaving 3 orbits.                          *)
Theorem ruleout_partial_symmetry_3 :
  num_orbits 3 (lic_group 3 [[0; 1; 2]; [1; 0; 2]]) = 3.
Proof. vm_compute. reflexivity. Qed.

(* --- which characters descend to the forward quotient ------------------ *)

Definition descends (d : nat) (chi : bsp -> bool) : bool :=
  forallb (fun z => negb (chi z)) (zflips d).

Theorem which_characters_descend :
  map (descends 2) four_chars = [true; true; false; false] /\
  map (descends 3) four_chars = [true; true; false; false].
Proof. split; vm_compute; reflexivity. Qed.

Theorem exactly_two_characters_descend :
  length (filter (descends 2) four_chars) = 2 /\
  length (filter (descends 3) four_chars) = 2.
Proof. split; vm_compute; reflexivity. Qed.

(* The two survivors are the trivial character and the permutation sign --  *)
(* SymIdx (+1) and AntisymIdx (-1).  They remain distinct after the         *)
(* quotient, so the surviving character group really has order 2.           *)
Theorem survivors_are_sym_and_antisym :
  descends 3 chi_triv = true /\ descends 3 chi_perm = true /\
  chi_triv (adjt 3 0) = false /\ chi_perm (adjt 3 0) = true.
Proof. repeat split; vm_compute; reflexivity. Qed.

(* The two non-survivors are supported on the flip subgroup: they are the   *)
(* sign a backward layout picks up on the way to its forward representative.*)
Theorem non_survivors_are_flip_signs :
  existsb chi_flip (zflips 3) = true /\ existsb chi_both (zflips 3) = true.
Proof. split; vm_compute; reflexivity. Qed.

(* --- the counting contrast -------------------------------------------- *)

Fixpoint partsF (fuel m k : nat) : list (list nat) :=
  match fuel with
  | 0 => []
  | S fu =>
      match m with
      | 0 => [[]]
      | _ => flat_map (fun j => if Nat.leb j (Nat.min m k)
                                then map (cons j) (partsF fu (m - j) j)
                                else []) (seq 1 k)
      end
  end.

Definition pcount (m : nat) : nat := length (partsF (S m) m m).

Definition bipart (d : nat) : nat :=
  fold_right Nat.add 0 (map (fun k => pcount k * pcount (d - k)) (seq 0 (S d))).

Example partition_numbers : map pcount [0; 1; 2; 3; 4; 5; 6] = [1; 1; 2; 3; 5; 7; 11].
Proof. vm_compute. reflexivity. Qed.

(* #irreps(S_d) = partitions(d): grows without bound, against a CONSTANT 2  *)
(* characters observable after the forward quotient.                        *)
Theorem partition_counts :
  map pcount [1; 2; 3; 4; 5; 6; 7; 8; 9; 10]
  = [1; 2; 3; 5; 7; 11; 15; 22; 30; 42].
Proof. vm_compute. reflexivity. Qed.

Theorem irrep_count_of_Sd_exceeds_character_count :
  forallb (fun d => Nat.ltb 2 (pcount d)) [3; 4; 5; 6; 7; 8; 9; 10] = true.
Proof. vm_compute. reflexivity. Qed.

(* #irreps(B_d) = bipartitions(d): the same contrast before the quotient,  *)
(* against a CONSTANT 4.                                                    *)
Theorem bipartition_counts :
  map bipart [1; 2; 3; 4; 5; 6; 7; 8; 9; 10]
  = [2; 5; 10; 20; 36; 65; 110; 185; 300; 481].
Proof. vm_compute. reflexivity. Qed.

Theorem irrep_count_of_Bd_exceeds_character_count :
  forallb (fun d => Nat.ltb 4 (bipart d)) [2; 3; 4; 5; 6; 7; 8; 9; 10] = true.
Proof. vm_compute. reflexivity. Qed.

(* --- the d = 1 boundary ------------------------------------------------ *)
(* Same flavour as BladeWreath's r = 1: there is no transposition at all,   *)
(* so chi_perm collapses onto chi_triv and chi_both onto chi_flip, and only *)
(* 2 of the 4 characters survive.  bipart 1 = 2 agrees.                     *)
Theorem d1_degeneracy :
  length (Bd 1) = 2 /\
  forallb (fun g => biff (chi_perm g) (chi_triv g)) (Bd 1) = true /\
  forallb (fun g => biff (chi_both g) (chi_flip g)) (Bd 1) = true /\
  bipart 1 = 2 /\ pcount 1 = 1.
Proof. repeat split; vm_compute; reflexivity. Qed.

(* ===================================================================== *)
(* 3. DIRECTION IS FREE GAUGE (and exactly when it is).                   *)
(*                                                                       *)
(* Short by design: this section exists to justify the quotient of        *)
(* section 2c, not to develop a theory of reduction order.  Reversing the *)
(* traversal of a reduced axis is value-neutral when the reduce monoid is *)
(* commutative AND associative (direction_is_free_gauge), and both        *)
(* hypotheses are separately necessary -- an associative non-commutative  *)
(* fold and a commutative non-associative fold each change value under    *)
(* reversal.  Where the license holds, canonical form fixes every axis to *)
(* FORWARD and the Z_2^d factor is quotiented out; where it fails (scans, *)
(* order-sensitive accumulation) the direction is part of the meaning and *)
(* the quotient is simply not taken.                                      *)
(*                                                                       *)
(* This is the repo's standing doctrine that a commutativity annotation   *)
(* is an ITERATION LICENSE rather than a value claim                      *)
(* (retired implicit-formers-and-deduction plan, on the Reynolds          *)
(* idiom).  Bitwise floating-point reproducibility is out of scope: the   *)
(* statement here is about the algebraic license only.                    *)
(* ===================================================================== *)

Section ReversalLicense.
  Variable A : Type.
  Variable op : A -> A -> A.
  Hypothesis op_comm : forall x y, op x y = op y x.
  Hypothesis op_assoc : forall x y z, op x (op y z) = op (op x y) z.

  Lemma op_left_swap : forall x y z, op y (op x z) = op x (op y z).
  Proof.
    intros x y z. rewrite op_assoc, (op_comm y x), <- op_assoc. reflexivity.
  Qed.

  Lemma fold_pull : forall x a m,
    fold_right op (op x a) m = op x (fold_right op a m).
  Proof.
    intros x a m. induction m as [|y m IH]; simpl; [reflexivity |].
    rewrite IH. apply op_left_swap.
  Qed.

  Theorem fold_right_rev : forall a l,
    fold_right op a (rev l) = fold_right op a l.
  Proof.
    intros a l. induction l as [|x l IH]; simpl; [reflexivity |].
    rewrite fold_right_app. simpl. rewrite fold_pull, IH. reflexivity.
  Qed.
End ReversalLicense.

Definition reduce_fwd (op : nat -> nat -> nat) (a n : nat) (g : nat -> nat)
  : nat := fold_right op a (map g (seq 0 n)).

Definition reduce_bwd (op : nat -> nat -> nat) (a n : nat) (g : nat -> nat)
  : nat := fold_right op a (map g (rev (seq 0 n))).

Theorem direction_is_free_gauge : forall op a n g,
  (forall x y, op x y = op y x) ->
  (forall x y z, op x (op y z) = op (op x y) z) ->
  reduce_bwd op a n g = reduce_fwd op a n g.
Proof.
  intros op a n g Hc Ha. unfold reduce_bwd, reduce_fwd.
  rewrite map_rev. apply (fold_right_rev nat op Hc Ha).
Qed.

(* Associative but NOT commutative: direction is part of the meaning. *)
Definition takefst (x y : nat) : nat := x.

Lemma takefst_assoc : forall x y z,
  takefst x (takefst y z) = takefst (takefst x y) z.
Proof. reflexivity. Qed.

Theorem assoc_alone_does_not_free_direction :
  reduce_bwd takefst 0 3 S <> reduce_fwd takefst 0 3 S.
Proof. vm_compute. lia. Qed.

(* Commutative but NOT associative: likewise. *)
Definition mulinc (x y : nat) : nat := x * y + 1.

Lemma mulinc_comm : forall x y, mulinc x y = mulinc y x.
Proof. intros x y. unfold mulinc. rewrite Nat.mul_comm. reflexivity. Qed.

Theorem comm_alone_does_not_free_direction :
  reduce_bwd mulinc 0 3 S <> reduce_fwd mulinc 0 3 S.
Proof. vm_compute. lia. Qed.

Example subtraction_pins_direction :
  fold_right Nat.sub 0 [1; 2; 3] <> fold_right Nat.sub 0 (rev [1; 2; 3]).
Proof. vm_compute. lia. Qed.

(* ===================================================================== *)
(* 4. AXIS EXCHANGE IS A SEMANTIC MOVE, GATED BY DECLARED SYMMETRY.       *)
(*                                                                       *)
(* Unlike direction, exchanging two memory axes of a rank-2 array changes *)
(* VALUES unless a symmetry is declared: it preserves them under          *)
(* symmetry (character +1), negates them under antisymmetry (character    *)
(* -1), and otherwise changes them into something that is neither         *)
(* (transpose_changes_value_without_declaration).  Those two characters   *)
(* are exactly the two that survive the forward quotient in section 2c,   *)
(* and they are exactly SymIdx and AntisymIdx.                            *)
(* ===================================================================== *)

Definition zsum (l : list Z) : Z := fold_right Z.add Z0 l.

Definition mmul (n : nat) (A B : nat -> nat -> Z) (i j : nat) : Z :=
  zsum (map (fun k => Z.mul (A i k) (B k j)) (seq 0 n)).

Definition mtr (A : nat -> nat -> Z) (i j : nat) : Z := A j i.

Definition zsgn (b : bool) (x : Z) : Z := if b then Z.opp x else x.

Lemma zsum_ext : forall (f g : nat -> Z) l,
  (forall k, f k = g k) -> zsum (map f l) = zsum (map g l).
Proof. intros f g l H. unfold zsum. f_equal. apply map_ext. exact H. Qed.

Lemma zsum_opp : forall (f : nat -> Z) l,
  zsum (map (fun k => Z.opp (f k)) l) = Z.opp (zsum (map f l)).
Proof.
  intros f l. induction l as [|x l IH]; [reflexivity |].
  cbn [map]. unfold zsum in IH |- *. cbn [fold_right]. rewrite IH.
  rewrite Z.opp_add_distr. reflexivity.
Qed.

Theorem transpose_preserves_when_symmetric : forall (A : nat -> nat -> Z),
  (forall i j, A j i = A i j) -> forall i j, mtr A i j = A i j.
Proof. intros A H i j. unfold mtr. apply H. Qed.

Theorem transpose_negates_when_antisymmetric : forall (A : nat -> nat -> Z),
  (forall i j, A j i = Z.opp (A i j)) ->
  forall i j, mtr A i j = Z.opp (A i j).
Proof. intros A H i j. unfold mtr. apply H. Qed.

Definition ztab (rows : list (list Z)) (i j : nat) : Z :=
  nth j (nth i rows nil) Z0.

Definition Agen : nat -> nat -> Z := ztab [[1; 2]; [3; 4]]%Z.

Theorem transpose_changes_value_without_declaration :
  mtr Agen 0 1 <> Agen 0 1 /\ mtr Agen 0 1 <> Z.opp (Agen 0 1).
Proof. split; vm_compute; lia. Qed.

(* --- the licensed index group acting on a reference's axis order ------- *)

Definition apply_perm (p c : list nat) : list nat := map (fun k => nth k c 0) p.

Definition lst_eqb (a b : list nat) : bool :=
  if list_eq_dec Nat.eq_dec a b then true else false.

Definition ref_orbit (G : list (list nat)) (r : list nat) : list (list nat) :=
  nodup (list_eq_dec Nat.eq_dec) (map (fun g => apply_perm g r) G).

Definition trivG (d : nat) : list (list nat) := [seq 0 d].
Definition symG2 : list (list nat) := [[0; 1]; [1; 0]].
Definition symG3_01 : list (list nat) := [[0; 1; 2]; [1; 0; 2]].

(* Orbits of the declared group on axis positions, at d = 2 and d = 3.     *)
Example orbit_computations :
  ref_orbit (trivG 2) [0; 1] = [[0; 1]] /\
  ref_orbit symG2 [0; 1] = [[0; 1]; [1; 0]] /\
  ref_orbit symG3_01 [0; 1; 2] = [[0; 1; 2]; [1; 0; 2]] /\
  ref_orbit (permsOf (seq 0 3)) [0; 1; 2] =
    [[0; 1; 2]; [1; 0; 2]; [1; 2; 0]; [0; 2; 1]; [2; 0; 1]; [2; 1; 0]].
Proof. repeat split; vm_compute; reflexivity. Qed.

(* ===================================================================== *)
(* 5. CANONICAL FORM: THE GUARANTEE.                                      *)
(*                                                                       *)
(* This is the layout-level analogue of the tower's index canonicalization *)
(* (BladeCore sections 5-6, formalism 12.2, BladeWreath's                  *)
(* block_canonical_access_general): a licensed group acts, one             *)
(* representative per orbit is distinguished, and access through the       *)
(* representative is lossless up to a tracked sign.                        *)
(*                                                                       *)
(* COST.  A reference is a list of loop-index names in MEMORY-AXIS order   *)
(* (outermost memory axis first, contiguous axis last).  A schedule is a   *)
(* loop order: the same names, outermost loop first.  The cost of a        *)
(* reference is the number of INVERSIONS (Kendall tau) between its memory  *)
(* order and the loop order.  Cost 0 is formalism section 9's              *)
(* outermost-slowest ideal for that reference; total cost is the sum over  *)
(* references.                                                            *)
(*                                                                       *)
(* ACTION AND SEARCH.  A licensed layout move permutes a reference's       *)
(* memory axes (apply_perm) and carries the character sign of section 2.   *)
(* The search space is every combination of licensed moves, one per        *)
(* reference occurrence, crossed with every loop order.  It is enumerated  *)
(* EXHAUSTIVELY (choices x permsOf), so the minimum is exact -- this is a  *)
(* guarantee, not a heuristic.  It rests on bounded arity and a bounded    *)
(* reference count: minimizing summed Kendall-tau over orderings is a      *)
(* Kemeny-style problem and is NP-hard for unbounded inputs, so the        *)
(* exhaustive argument is only available at the small ranks a kernel       *)
(* actually has.                                                          *)
(*                                                                       *)
(* CANONICAL FORM.  Minimum cost, with a lexicographic tie-break on the    *)
(* schedule encoding.  canonical_is_cost_minimal proves at ANY d, ANY      *)
(* licensed group and ANY reference set that the canonical schedule is a   *)
(* cost minimizer over the whole licensed space (canonical_is_in_space     *)
(* puts it in the space); uniqueness is then decided per instance          *)
(* (canonical_is_unique_tpair_sym and friends).                            *)
(*                                                                       *)
(* THE WORKED CASE where a declaration is genuinely load-bearing is        *)
(* C[i,j] = A[i,j] * A[j,i]: the two references demand opposite orders of  *)
(* the same two loop indices, so NO loop order reaches cost 0              *)
(* (tpair_no_loop_order_reaches_zero), and the symmetry rewrite does       *)
(* (tpair_symmetry_reaches_zero).  Matrix square C[i,j] = sum_k            *)
(* A[i,k] A[k,j] is deliberately included as the CONTRAST: there loop      *)
(* reordering alone already reaches cost 0                                 *)
(* (msq_loop_reordering_suffices), so it is not evidence for the           *)
(* declaration.                                                           *)
(* ===================================================================== *)

Fixpoint posn (x : nat) (l : list nat) : nat :=
  match l with
  | [] => 0
  | y :: l' => if Nat.eqb x y then 0 else S (posn x l')
  end.

Definition kendall (r lo : list nat) : nat :=
  length (filter (fun ij =>
            Nat.ltb (posn (nth (snd ij) r 0) lo) (posn (nth (fst ij) r 0) lo))
          (upairs (length r))).

Definition total_cost (refs : list (list nat)) (lo : list nat) : nat :=
  fold_right Nat.add 0 (map (fun r => kendall r lo) refs).

(* --- cost 0 is exactly the outermost-slowest ideal, generally ---------- *)

Lemma upairs_spec : forall m i j, In (i, j) (upairs m) -> i < j /\ j < m.
Proof.
  intros m i j H. unfold upairs in H.
  apply in_flat_map in H as (a & Ha & H).
  apply in_map_iff in H as (b & Hb & Hbin).
  inversion Hb; subst.
  apply in_seq in Ha. apply in_seq in Hbin. lia.
Qed.

Lemma filter_all_false : forall (X : Type) (f : X -> bool) (l : list X),
  (forall x, In x l -> f x = false) -> filter f l = [].
Proof.
  intros X f l. induction l as [|a l IH]; intro H; simpl; [reflexivity |].
  rewrite (H a (or_introl eq_refl)). apply IH.
  intros x Hx. apply H. right. exact Hx.
Qed.

Lemma posn_nth : forall l q, NoDup l -> q < length l -> posn (nth q l 0) l = q.
Proof.
  induction l as [|x l IH]; intros q Hnd Hq; simpl in Hq; [lia |].
  inversion Hnd as [| x' l' Hx Hnd' Heq]; subst.
  destruct q as [|q']; simpl.
  - rewrite Nat.eqb_refl. reflexivity.
  - destruct (Nat.eqb (nth q' l 0) x) eqn:E.
    + apply Nat.eqb_eq in E. exfalso. apply Hx. rewrite <- E.
      apply nth_In. lia.
    + f_equal. apply IH; [exact Hnd' | lia].
Qed.

(* A reference read in the loop order that IS its memory order costs        *)
(* nothing: the cost model does have cost 0 as its ideal, and it is         *)
(* attained.                                                              *)
Theorem kendall_self_is_zero : forall r, NoDup r -> kendall r r = 0.
Proof.
  intros r Hnd. unfold kendall.
  rewrite (filter_all_false _ _ (upairs (length r))); [reflexivity |].
  intros [i j] Hij. apply upairs_spec in Hij as (Hlt & Hj).
  simpl. rewrite (posn_nth r j Hnd Hj), (posn_nth r i Hnd (Nat.lt_trans _ _ _ Hlt Hj)).
  apply Nat.ltb_ge. lia.
Qed.

Corollary uniform_refs_reach_zero : forall r k,
  NoDup r -> total_cost (repeat r k) r = 0.
Proof.
  intros r k Hnd. induction k as [|k IH]; [reflexivity |].
  unfold total_cost in *. simpl.
  rewrite (kendall_self_is_zero r Hnd), IH. reflexivity.
Qed.

(* --- the exhaustive licensed search space ----------------------------- *)

Fixpoint choices (G : list (list nat)) (refs : list (list nat))
  : list (list (list nat)) :=
  match refs with
  | [] => [[]]
  | r :: rs => flat_map (fun g => map (fun c => apply_perm g r :: c)
                                      (choices G rs)) G
  end.

Definition sched : Type := (list (list nat) * list nat)%type.

Definition sched_cost (s : sched) : nat := total_cost (fst s) (snd s).
Definition sched_key (s : sched) : list nat := concat (fst s) ++ snd s.

Definition sched_space (d : nat) (G : list (list nat))
  (refs : list (list nat)) : list sched :=
  flat_map (fun rs => map (fun lo => (rs, lo)) (permsOf (seq 0 d)))
           (choices G refs).

Definition best_cost (d : nat) (G : list (list nat))
  (refs : list (list nat)) : nat :=
  fold_right Nat.min 99 (map sched_cost (sched_space d G refs)).

(* --- minimum with a lexicographic tie-break, in general ---------------- *)

Section MinPick.
  Variable S : Type.
  Variable cost : S -> nat.
  Variable key : S -> list nat.

  Definition better (s t : S) : bool :=
    orb (Nat.ltb (cost s) (cost t))
        (andb (Nat.eqb (cost s) (cost t)) (lex_le_nat (key s) (key t))).

  Definition pick (s t : S) : S := if better s t then s else t.

  Lemma pick_is_one_of : forall s t, pick s t = s \/ pick s t = t.
  Proof. intros s t. unfold pick. destruct (better s t); auto. Qed.

  Lemma pick_le_l : forall s t, cost (pick s t) <= cost s.
  Proof.
    intros s t. unfold pick, better.
    destruct (Nat.ltb (cost s) (cost t)) eqn:E1.
    - cbn. lia.
    - apply Nat.ltb_ge in E1.
      destruct (Nat.eqb (cost s) (cost t)) eqn:E2.
      + apply Nat.eqb_eq in E2.
        destruct (lex_le_nat (key s) (key t)); cbn; lia.
      + cbn. lia.
  Qed.

  Lemma pick_le_r : forall s t, cost (pick s t) <= cost t.
  Proof.
    intros s t. unfold pick, better.
    destruct (Nat.ltb (cost s) (cost t)) eqn:E1.
    - apply Nat.ltb_lt in E1. cbn. lia.
    - apply Nat.ltb_ge in E1.
      destruct (Nat.eqb (cost s) (cost t)) eqn:E2.
      + apply Nat.eqb_eq in E2.
        destruct (lex_le_nat (key s) (key t)); cbn; lia.
      + cbn. lia.
  Qed.

  Fixpoint foldpick (s : S) (l : list S) : S :=
    match l with
    | [] => s
    | t :: l' => pick t (foldpick s l')
    end.

  Lemma foldpick_in : forall l s, In (foldpick s l) (s :: l).
  Proof.
    induction l as [|t l IH]; intro s; simpl.
    - left. reflexivity.
    - destruct (pick_is_one_of t (foldpick s l)) as [E | E]; rewrite E.
      + right. left. reflexivity.
      + destruct (IH s) as [E2 | E2]; simpl in E2.
        * left. exact E2.
        * right. right. exact E2.
  Qed.

  Lemma foldpick_min : forall l s t,
    In t (s :: l) -> cost (foldpick s l) <= cost t.
  Proof.
    induction l as [|u l IH]; intros s t Ht; simpl in Ht |- *.
    - destruct Ht as [E | []]. subst t. lia.
    - destruct Ht as [E | [E | Ht]].
      + subst t. apply Nat.le_trans with (m := cost (foldpick s l));
          [apply pick_le_r | apply IH; simpl; left; reflexivity].
      + subst t. apply pick_le_l.
      + apply Nat.le_trans with (m := cost (foldpick s l));
          [apply pick_le_r | apply IH; simpl; right; exact Ht].
  Qed.
End MinPick.

Definition canon_sched (d : nat) (G : list (list nat))
  (refs : list (list nat)) : sched :=
  match sched_space d G refs with
  | [] => ([], [])
  | s :: rest => foldpick sched sched_cost sched_key s rest
  end.

(* THE GUARANTEE, at any d, any licensed group, any reference set:         *)
(* canonical form is in the licensed space and is a cost minimizer of it.  *)
Theorem canonical_is_in_space : forall d G refs,
  sched_space d G refs <> [] -> In (canon_sched d G refs) (sched_space d G refs).
Proof.
  intros d G refs Hne. unfold canon_sched.
  destruct (sched_space d G refs) as [|s0 rest] eqn:E.
  - exfalso. apply Hne. reflexivity.
  - exact (foldpick_in sched sched_cost sched_key rest s0).
Qed.

Theorem canonical_is_cost_minimal : forall d G refs s,
  In s (sched_space d G refs) ->
  sched_cost (canon_sched d G refs) <= sched_cost s.
Proof.
  intros d G refs s Hs. unfold canon_sched.
  destruct (sched_space d G refs) as [|s0 rest] eqn:E.
  - destruct Hs.
  - exact (foldpick_min sched sched_cost sched_key rest s0 s Hs).
Qed.

(* Deciding canonicality: is this schedule THE representative of its        *)
(* licensed orbit?  The layout analogue of asking whether an index tuple    *)
(* is sorted for symmetric storage.                                        *)
Definition sd_dec (s t : sched) : {s = t} + {s <> t}.
Proof.
  destruct s as [a b]; destruct t as [c e].
  destruct (list_eq_dec (list_eq_dec Nat.eq_dec) a c);
  destruct (list_eq_dec Nat.eq_dec b e).
  - left; subst; reflexivity.
  - right; intro H; inversion H; contradiction.
  - right; intro H; inversion H; contradiction.
  - right; intro H; inversion H; contradiction.
Defined.

Definition is_canonical (d : nat) (G : list (list nat))
  (refs : list (list nat)) (s : sched) : bool :=
  if sd_dec s (canon_sched d G refs) then true else false.

(* --- THE WORKED CASE: C[i,j] = A[i,j] * A[j,i] ------------------------- *)
(* Loop names i = 0, j = 1.  The two references to A carry the two axes in *)
(* OPPOSITE memory orders.                                                *)

Definition tp_refs : list (list nat) := [[0; 1]; [1; 0]].

(* No loop order reaches cost 0 without a declaration, and the best is 1.  *)
Theorem tpair_no_loop_order_reaches_zero :
  best_cost 2 (trivG 2) tp_refs = 1.
Proof. vm_compute. reflexivity. Qed.

Theorem tpair_zero_unreachable_without_declaration :
  existsb (fun s => Nat.eqb (sched_cost s) 0) (sched_space 2 (trivG 2) tp_refs)
  = false.
Proof. vm_compute. reflexivity. Qed.

(* A declared symmetry supplies the second axis order, and cost 0 is       *)
(* reached.                                                               *)
Theorem tpair_symmetry_reaches_zero : best_cost 2 symG2 tp_refs = 0.
Proof. vm_compute. reflexivity. Qed.

Theorem tpair_canonical_has_zero_cost :
  sched_cost (canon_sched 2 symG2 tp_refs) = 0.
Proof. vm_compute. reflexivity. Qed.

Theorem canonical_is_unique_tpair_sym :
  length (filter (is_canonical 2 symG2 tp_refs) (sched_space 2 symG2 tp_refs))
  = 1.
Proof. vm_compute. reflexivity. Qed.

Theorem canonical_is_unique_tpair_triv :
  length (filter (is_canonical 2 (trivG 2) tp_refs)
                 (sched_space 2 (trivG 2) tp_refs)) = 1.
Proof. vm_compute. reflexivity. Qed.

(* --- THE CONTRAST: matrix square needs no declaration ------------------ *)
(* C[i,j] = sum_k A[i,k] A[k,j], names i = 0, j = 1, k = 2.  The two       *)
(* references are A[i,k] = [0;2] and A[k,j] = [2;1]; the loop order        *)
(* (i, k, j) satisfies both, so cost 0 is reached with the TRIVIAL group.  *)
(* Recording this keeps the theory honest: matrix square is not evidence   *)
(* that a symmetry declaration buys locality.                              *)

Definition msq_refs : list (list nat) := [[0; 2]; [2; 1]].

Theorem msq_loop_reordering_suffices : best_cost 3 (trivG 2) msq_refs = 0.
Proof. vm_compute. reflexivity. Qed.

Example msq_zero_cost_order : total_cost msq_refs [0; 2; 1] = 0.
Proof. vm_compute. reflexivity. Qed.

Example msq_ijk_order_is_worse : total_cost msq_refs [0; 1; 2] = 1.
Proof. vm_compute. reflexivity. Qed.

Theorem canonical_is_unique_msq :
  length (filter (is_canonical 3 (trivG 2) msq_refs)
                 (sched_space 3 (trivG 2) msq_refs)) = 1.
Proof. vm_compute. reflexivity. Qed.

(* --- canonicalization is value-preserving UP TO THE CHARACTER ---------- *)
(* The rewrite that dissolves the conflict -- replacing A[j,i] by A[i,j]   *)
(* -- is exact when the declared character is +1 and sign-flipped when it  *)
(* is -1, in one statement over b : bool; and it is simply WRONG with no   *)
(* declaration.                                                           *)

Definition tpair (A : nat -> nat -> Z) (i j : nat) : Z :=
  Z.mul (A i j) (A j i).

Definition tpair_rw (A : nat -> nat -> Z) (i j : nat) : Z :=
  Z.mul (A i j) (A i j).

Theorem canonicalization_preserves_value_up_to_character :
  forall (A : nat -> nat -> Z) (b : bool),
    (forall i j, A j i = zsgn b (A i j)) ->
    forall i j, tpair A i j = zsgn b (tpair_rw A i j).
Proof.
  intros A b H i j. unfold tpair, tpair_rw.
  rewrite (H i j). destruct b; cbn [zsgn]; ring.
Qed.

Theorem canonicalization_invalid_without_declaration :
  tpair Agen 0 1 <> tpair_rw Agen 0 1 /\
  tpair Agen 0 1 <> Z.opp (tpair_rw Agen 0 1).
Proof. split; vm_compute; lia. Qed.

(* ===================================================================== *)
(* 6. PROPAGATION: CHARACTERS COMPOSE BY XOR THROUGH A PIPELINE.          *)
(*                                                                       *)
(* Section 2a's xor law is about composing two licensed moves on ONE       *)
(* kernel.  The same law holds along a PIPELINE: if stage one transforms   *)
(* with character chi1 and stage two with chi2, the composite transforms   *)
(* with chi1 chi2.  That is what makes striding parity a calculus rather   *)
(* than a per-kernel annotation.                                          *)
(*                                                                       *)
(* The concrete instance is the transpose bridge.  (A B)^T = B^T A^T       *)
(* always (transpose_of_product); for inputs of definite parity it becomes *)
(* (A B)^T = eps_A eps_B (B A), proved at general n in all four sign       *)
(* cases and pinned at 3x3 over Z.  This is the machine-checked form of    *)
(* the f(QR) = f(R^T Q^T) / f(QR) = -f(Q, R^T) reading.                    *)
(* ===================================================================== *)

Section CharacterPropagation.
  Variables V W X : Type.
  (* the group element acts on each carrier *)
  Variables (aV : V -> V) (aW : W -> W) (aX : X -> X).
  (* the sign acts on each carrier *)
  Variables (nW : W -> W) (nX : X -> X).
  Variables (F : V -> W) (G : W -> X).
  Variables b1 b2 : bool.

  Hypothesis HF : forall v,
    F (aV v) = (if b1 then nW (aW (F v)) else aW (F v)).
  Hypothesis HG : forall w,
    G (aW w) = (if b2 then nX (aX (G w)) else aX (G w)).
  (* G is sign-homogeneous: it carries a negation through *)
  Hypothesis HGn : forall w, G (nW w) = nX (G w).
  Hypothesis HnX : forall x, nX (nX x) = x.

  Theorem character_composes : forall v,
    G (F (aV v))
    = (if xorb b1 b2 then nX (aX (G (F v))) else aX (G (F v))).
  Proof.
    intro v. rewrite HF.
    destruct b1; destruct b2; cbn [xorb].
    - rewrite HGn, HG. cbn. apply HnX.
    - rewrite HGn, HG. cbn. reflexivity.
    - rewrite HG. cbn. reflexivity.
    - rewrite HG. cbn. reflexivity.
  Qed.
End CharacterPropagation.

(* --- the transpose bridge, at general n -------------------------------- *)

Theorem transpose_of_product : forall n A B i j,
  mtr (mmul n A B) i j = mmul n (mtr B) (mtr A) i j.
Proof.
  intros n A B i j. unfold mtr, mmul.
  apply zsum_ext. intro k. apply Z.mul_comm.
Qed.

Theorem transpose_product_sym_sym : forall n A B,
  (forall i j, A j i = A i j) -> (forall i j, B j i = B i j) ->
  forall i j, mtr (mmul n A B) i j = mmul n B A i j.
Proof.
  intros n A B HA HB i j. unfold mtr, mmul.
  apply zsum_ext. intro k. rewrite (HA k j), (HB i k). ring.
Qed.

Theorem transpose_product_sym_anti : forall n A B,
  (forall i j, A j i = A i j) -> (forall i j, B j i = Z.opp (B i j)) ->
  forall i j, mtr (mmul n A B) i j = Z.opp (mmul n B A i j).
Proof.
  intros n A B HA HB i j. unfold mtr, mmul.
  rewrite <- zsum_opp. apply zsum_ext. intro k.
  rewrite (HA k j), (HB i k). ring.
Qed.

Theorem transpose_product_anti_sym : forall n A B,
  (forall i j, A j i = Z.opp (A i j)) -> (forall i j, B j i = B i j) ->
  forall i j, mtr (mmul n A B) i j = Z.opp (mmul n B A i j).
Proof.
  intros n A B HA HB i j. unfold mtr, mmul.
  rewrite <- zsum_opp. apply zsum_ext. intro k.
  rewrite (HA k j), (HB i k). ring.
Qed.

Theorem transpose_product_anti_anti : forall n A B,
  (forall i j, A j i = Z.opp (A i j)) -> (forall i j, B j i = Z.opp (B i j)) ->
  forall i j, mtr (mmul n A B) i j = mmul n B A i j.
Proof.
  intros n A B HA HB i j. unfold mtr, mmul.
  apply zsum_ext. intro k. rewrite (HA k j), (HB i k). ring.
Qed.

(* All four cases in ONE statement, with the sign given by xor. *)
Corollary transpose_product_signed : forall n A B (ea eb : bool),
  (forall i j, A j i = zsgn ea (A i j)) ->
  (forall i j, B j i = zsgn eb (B i j)) ->
  forall i j, mtr (mmul n A B) i j = zsgn (xorb ea eb) (mmul n B A i j).
Proof.
  intros n A B ea eb HA HB i j.
  destruct ea; destruct eb; cbn [zsgn xorb] in HA, HB |- *.
  - now apply transpose_product_anti_anti.
  - now apply transpose_product_anti_sym.
  - now apply transpose_product_sym_anti.
  - now apply transpose_product_sym_sym.
Qed.

(* --- 3x3 pins over Z ---------------------------------------------------- *)
(* Symmetric- and antisymmetric-BY-CONSTRUCTION accessors (BladeWreath's    *)
(* symtab discipline), so the parity hypothesis holds at every pair of      *)
(* naturals with no per-witness case analysis.                             *)

Definition zsymtab (rows : list (list Z)) (i j : nat) : Z :=
  nth (Nat.max i j) (nth (Nat.min i j) rows nil) Z0.

Lemma zsymtab_sym : forall rows i j, zsymtab rows j i = zsymtab rows i j.
Proof.
  intros rows i j. unfold zsymtab.
  rewrite Nat.min_comm, Nat.max_comm. reflexivity.
Qed.

Definition zantitab (rows : list (list Z)) (i j : nat) : Z :=
  if Nat.ltb i j then nth j (nth i rows nil) Z0
  else if Nat.ltb j i then Z.opp (nth i (nth j rows nil) Z0)
  else Z0.

Lemma zantitab_anti : forall rows i j,
  zantitab rows j i = Z.opp (zantitab rows i j).
Proof.
  intros rows i j. unfold zantitab.
  destruct (Nat.ltb i j) eqn:E1; destruct (Nat.ltb j i) eqn:E2.
  - apply Nat.ltb_lt in E1; apply Nat.ltb_lt in E2; lia.
  - reflexivity.
  - symmetry. apply Z.opp_involutive.
  - reflexivity.
Qed.

Definition S1 : nat -> nat -> Z := zsymtab [[1; 2; 3]; [2; 4; 5]; [3; 5; 6]]%Z.
Definition S2 : nat -> nat -> Z := zsymtab [[7; 1; 2]; [1; 8; 3]; [2; 3; 9]]%Z.
Definition A1 : nat -> nat -> Z := zantitab [[0; 2; 3]; [0; 0; 5]; [0; 0; 0]]%Z.
Definition A2 : nat -> nat -> Z := zantitab [[0; 1; 4]; [0; 0; 6]; [0; 0; 0]]%Z.

Example sign_case_sym_sym : mtr (mmul 3 S1 S2) 0 1 = mmul 3 S2 S1 0 1.
Proof. vm_compute. reflexivity. Qed.

Example sign_case_sym_anti :
  mtr (mmul 3 S1 A2) 0 1 = Z.opp (mmul 3 A2 S1 0 1).
Proof. vm_compute. reflexivity. Qed.

Example sign_case_anti_sym :
  mtr (mmul 3 A1 S2) 0 1 = Z.opp (mmul 3 S2 A1 0 1).
Proof. vm_compute. reflexivity. Qed.

Example sign_case_anti_anti : mtr (mmul 3 A1 A2) 0 1 = mmul 3 A2 A1 0 1.
Proof. vm_compute. reflexivity. Qed.

(* Non-vacuity: the four pinned values are nonzero, so the +1 and -1 cases  *)
(* are genuinely distinguished rather than agreeing at 0.                   *)
Example sign_cases_nondegenerate :
  mmul 3 S2 S1 0 1 <> Z0 /\ mmul 3 A2 S1 0 1 <> Z0 /\
  mmul 3 S2 A1 0 1 <> Z0 /\ mmul 3 A2 A1 0 1 <> Z0.
Proof. repeat split; vm_compute; lia. Qed.

(* ===================================================================== *)
(* Generalization notes, and the scope limits (not mechanized here).       *)
(*                                                                       *)
(*  - INVARIANCE VS ORDERS.  Everything in section 2a -- the xor           *)
(*    composition law, the three sign corollaries, graded soundness, grade *)
(*    uniqueness -- is proved at ARBITRARY r, arbitrary kernel, arbitrary  *)
(*    index type, with no finiteness anywhere.  Everything about the SIZE  *)
(*    and EXACTNESS of the character group is computed at small d: group   *)
(*    orders at d <= 4, generation and completeness at d = 2 and d = 3.    *)
(*    A general-d completeness proof wants a Coxeter presentation of B_d   *)
(*    developed over the permutes predicate rather than an enumeration.    *)
(*                                                                       *)
(*  - THE IRREP COUNTS ARE CITED, NOT PROVED.  That the irreducibles of    *)
(*    S_d are indexed by partitions and those of B_d by bipartitions is    *)
(*    classical (Specht / Young, and the wreath-product construction for   *)
(*    B_d).  What this file proves is the counting arithmetic              *)
(*    (partition_counts, bipartition_counts) and the constancy of the      *)
(*    LINEAR character count against it.                                   *)
(*                                                                       *)
(*  - THE QUOTIENT IS A CARDINALITY STATEMENT.  quotient_coset_count       *)
(*    computes that B_d / Z_2^d has d! cosets at d = 2, 3, 4, and          *)
(*    zflips_normal_2/3 checks normality.  The group isomorphism           *)
(*    B_d / Z_2^d = S_d itself is classical and is cited.                  *)
(*                                                                       *)
(*  - COST 0 IS NECESSARY BUT NOT SUFFICIENT FOR FASTEST.  This is the     *)
(*    honest limit of the whole section 5 development.  The inversion      *)
(*    metric ranks schedules by stride coherence only.  Two schedules can  *)
(*    both have total cost 0 and still differ substantially in measured    *)
(*    time, because reuse distance, register blocking and vectorization    *)
(*    structure are not modeled -- a spread of roughly 2.1x was measured   *)
(*    between two cost-0 schedules of one kernel (a loop-reordered ikj     *)
(*    form against a symmetry-rewritten ijk form).  So the theory here     *)
(*    licenses an UPPER BOUND on attainable locality: it says which        *)
(*    iteration patterns are permissible and which are stride-coherent,    *)
(*    and it selects a unique representative among them.  It does NOT      *)
(*    decide absolute cache optimality, and no claim about hardware        *)
(*    behaviour is made anywhere in this file.                              *)
(*                                                                       *)
(*  - THE EXHAUSTIVE SEARCH IS BOUNDED-ARITY.  canonical_is_cost_minimal   *)
(*    is general, but its force comes from the search space being small    *)
(*    enough to enumerate.  Summed-Kendall-tau minimization over orderings *)
(*    is Kemeny-style and NP-hard for unbounded inputs; the guarantee is   *)
(*    available exactly because kernel rank and reference count are small. *)
(*                                                                       *)
(*  - MEASURED CONTEXT, cited as context and NOT as a Coq claim.  The      *)
(*    stride conflict of section 5's worked case is real: for              *)
(*    C[i,j] = A[i,j] A[j,i] at n = 4097 the symmetry rewrite ran about    *)
(*    3.90x faster than the best available loop order, with identical      *)
(*    checksums -- which is what makes the DECLARATION, not the schedule,  *)
(*    the load-bearing part there.  The matrix-square conflict is a        *)
(*    weaker case and is recorded as such (msq_loop_reordering_suffices).  *)
(*                                                                       *)
(*  - RANK 2 FOR THE VALUE-LEVEL THEOREMS.  Sections 4 and 6 are rank-2    *)
(*    (one transposition, one sign).  Higher rank wants the signed action  *)
(*    of S_d on d-tuples, whose sign bookkeeping is BladeLowering's        *)
(*    output_antisymmetry_soundness and BladeDeduce's table-2 composition; *)
(*    the graded framework of section 2a is already stated at arbitrary r  *)
(*    and would carry it.                                                  *)
(* ===================================================================== *)
